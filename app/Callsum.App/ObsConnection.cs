using Callsum.Obs;

namespace Callsum.App;

/// <summary>
/// Связь с OBS для окна: подключается сама и восстанавливается после обрыва.
///
/// Подключение — сетевая операция на секунды, поэтому оно никогда не идёт
/// в потоке интерфейса: окно только получает готовые события.
/// </summary>
public sealed class ObsConnection : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _settingsLock = new();

    private ObsSettings _settings = new();
    private ObsClient? _client;
    private ObsService? _service;
    private bool _reportedOffline;
    private bool _started;
    private int _settingsVersion;

    /// <summary>Подключены ли сейчас: true, false и текст для показа пользователю.</summary>
    public event Action<bool, string>? StateChanged;

    /// <summary>Запись началась или закончилась — в том числе не из нашего окна.</summary>
    public event Action<bool, string?>? RecordStateChanged;

    public event Action<string>? Log;

    public bool Connected => _client?.Connected == true;

    /// <summary>
    /// Заменить адрес, порт и пароль. При изменении соединение поднимается заново:
    /// иначе новое значение осталось бы в форме до следующего запуска приложения.
    /// </summary>
    public async Task ConfigureAsync(ObsSettings settings)
    {
        ObsClient? former = null;
        var start = false;
        var changed = false;
        lock (_settingsLock)
        {
            changed = _settings.Host != settings.Host
                      || _settings.Port != settings.Port
                      || _settings.Password != settings.Password;
            if (changed)
            {
                _settings = settings;
                _settingsVersion++;
                former = _client;
                _client = null;
                _service = null;
                _reportedOffline = false;
            }

            if (!_started)
            {
                _started = true;
                start = true;
            }
        }

        if (former is not null)
        {
            await former.DisposeAsync().ConfigureAwait(false);
        }

        if (changed)
        {
            StateChanged?.Invoke(false, "применяю настройки подключения…");
        }

        if (start)
        {
            _ = Task.Run(() => KeepConnectedAsync(_lifetime.Token));
        }
    }

    private async Task KeepConnectedAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (Connected)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ObsException exception)
            {
                // Об отсутствии OBS пишем один раз, а не каждые три секунды.
                if (!_reportedOffline)
                {
                    _reportedOffline = true;
                    Log?.Invoke($"! {exception.Message}");
                }

                StateChanged?.Invoke(false, "нет подключения, пробую подключиться…");
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var (settings, version) = CurrentSettings();
        var client = new ObsClient(settings);
        client.Disconnected += () => OnDisconnected(client);

        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var service = new ObsService(client);
        service.OnRecordStateChanged((active, path) => RecordStateChanged?.Invoke(active, path));

        lock (_settingsLock)
        {
            // Пока шло подключение, пользователь мог уже задать другой адрес
            // или пароль. Старое соединение не должно вернуться поверх нового.
            if (version != _settingsVersion)
            {
                _ = client.DisposeAsync();
                return;
            }

            _client = client;
            _service = service;
            _reportedOffline = false;
        }
        StateChanged?.Invoke(true, $"{settings.Host}:{settings.Port}");

        // Запись могли начать до запуска приложения — покажем это сразу.
        var status = await service.GetRecordStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.Active)
        {
            RecordStateChanged?.Invoke(true, null);
        }
    }

    private (ObsSettings Settings, int Version) CurrentSettings()
    {
        lock (_settingsLock)
        {
            return (_settings, _settingsVersion);
        }
    }

    private void OnDisconnected(ObsClient client)
    {
        lock (_settingsLock)
        {
            if (!ReferenceEquals(_client, client))
            {
                return;
            }

            _client = null;
            _service = null;
        }

        StateChanged?.Invoke(false, "связь потеряна, восстанавливаю…");
        Log?.Invoke("! Связь с OBS потеряна, пробую подключиться заново");
    }

    /// <summary>Начать запись. Возвращает текст ошибки или null, если всё вышло.</summary>
    public async Task<string?> StartRecordingAsync()
    {
        var service = _service;
        if (service is null)
        {
            return "Нет подключения к OBS";
        }

        try
        {
            await service.StartRecordAsync().ConfigureAwait(false);
            return null;
        }
        catch (ObsException exception)
        {
            return exception.Message;
        }
    }

    public async Task<string?> StopRecordingAsync()
    {
        var service = _service;
        if (service is null)
        {
            return "Нет подключения к OBS";
        }

        try
        {
            await service.StopRecordAsync().ConfigureAwait(false);
            return null;
        }
        catch (ObsException exception)
        {
            return exception.Message;
        }
    }

    /// <summary>
    /// Настроен ли OBS под созвоны. null — спросить не у кого: нет подключения.
    /// </summary>
    public async Task<ObsService.SetupState?> GetSetupStateAsync()
    {
        var service = _service;
        if (service is null)
        {
            return null;
        }

        try
        {
            return await service.GetSetupStateAsync(ObsSetup.DefaultProfile).ConfigureAwait(false);
        }
        catch (ObsException)
        {
            // Связь оборвалась между проверками — окно просто не станет ничего
            // предлагать, а подключение восстановится само.
            return null;
        }
    }

    /// <summary>
    /// Завести в OBS профиль и коллекцию сцен callsum: дорожки, формат записи,
    /// папку. Возвращает текст ошибки или null, если всё вышло.
    ///
    /// Чужие настройки при этом не трогаются: всё, что мы меняем, живёт
    /// в своём профиле и своей коллекции, а прежние остаются на месте.
    /// Ход работы уходит в журнал окна — это десяток секунд, и человек должен
    /// видеть, что происходит.
    /// </summary>
    public async Task<string?> SetUpAsync(string recordingsFolder, string filenameFormat)
    {
        var service = _service;
        if (service is null)
        {
            return "Нет подключения к OBS";
        }

        try
        {
            var setup = new ObsSetup(service, message => Log?.Invoke(message));
            await setup.RunAsync(ObsSetup.DefaultProfile, recordingsFolder, filenameFormat)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
            when (exception is ObsException or IOException or UnauthorizedAccessException)
        {
            return exception.Message;
        }
    }

    /// <summary>Что можно выбрать для одного источника звука и что выбрано сейчас.</summary>
    public sealed record AudioChoice(
        string Input, IReadOnlyList<ObsDevice> Devices, string Current, string? Error);

    /// <summary>
    /// Устройства записи: список даёт сам OBS, как в окне свойств источника.
    ///
    /// Источники живут в коллекции сцен callsum. Если в OBS открыта другая
    /// коллекция, их там нет — об этом честно говорится, а не переключается
    /// втихую: переключение меняет то, что человек видит в OBS прямо сейчас.
    /// </summary>
    public async Task<AudioChoice> GetAudioChoiceAsync(string input)
    {
        var service = _service;
        if (service is null)
        {
            return new AudioChoice(input, [], "", "Нет подключения к OBS");
        }

        try
        {
            var devices = await service.GetInputDevicesAsync(input).ConfigureAwait(false);
            var current = await service.GetInputDeviceAsync(input).ConfigureAwait(false);
            return new AudioChoice(input, devices, current, null);
        }
        catch (ObsException exception)
        {
            var collection = exception.Message.Contains("не найден", StringComparison.OrdinalIgnoreCase)
                ? $"В OBS нет источника «{input}». Откройте коллекцию сцен callsum или создайте её заново."
                : exception.Message;
            return new AudioChoice(input, [], "", collection);
        }
    }

    /// <summary>Что вышло с папкой записи: поменяли, не понадобилось или не смогли.</summary>
    public sealed record RecordFolder(bool Changed, string? Note);

    /// <summary>
    /// Проследить, чтобы OBS писал записи туда, где их ищет программа.
    ///
    /// Путь хранится в профиле OBS, а не в настройках callsum: поменяв папку
    /// в окне, человек иначе получил бы записи в старом месте — ровно это
    /// и случилось при первой проверке настроек.
    /// </summary>
    public async Task<RecordFolder> EnsureRecordFolderAsync(string folder)
    {
        var service = _service;
        if (service is null)
        {
            return new RecordFolder(false, "OBS не подключён — папку записи он получит при следующем подключении");
        }

        try
        {
            var change = await service.EnsureRecordFolderAsync(ObsSetup.DefaultProfile, folder)
                .ConfigureAwait(false);
            if (change.ForeignProfile is { Length: > 0 } profile)
            {
                return new RecordFolder(
                    false,
                    $"в OBS открыт профиль «{profile}» — папку записи поменяем, когда он вернётся к callsum");
            }

            return new RecordFolder(change.Changed, null);
        }
        catch (ObsException exception)
        {
            return new RecordFolder(false, exception.Message);
        }
    }

    /// <summary>Выбрать устройство. Возвращает текст ошибки или null.</summary>
    public async Task<string?> SetAudioDeviceAsync(string input, string device)
    {
        var service = _service;
        if (service is null)
        {
            return "Нет подключения к OBS";
        }

        try
        {
            await service.SetInputDeviceAsync(input, device).ConfigureAwait(false);
            return null;
        }
        catch (ObsException exception)
        {
            return exception.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        ObsClient? client;
        lock (_settingsLock)
        {
            client = _client;
            _client = null;
            _service = null;
        }

        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }
}
