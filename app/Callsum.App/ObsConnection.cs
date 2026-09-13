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

    private readonly ObsSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();

    private ObsClient? _client;
    private ObsService? _service;
    private bool _reportedOffline;

    public ObsConnection(ObsSettings settings) => _settings = settings;

    /// <summary>Подключены ли сейчас: true, false и текст для показа пользователю.</summary>
    public event Action<bool, string>? StateChanged;

    /// <summary>Запись началась или закончилась — в том числе не из нашего окна.</summary>
    public event Action<bool, string?>? RecordStateChanged;

    public event Action<string>? Log;

    public bool Connected => _client?.Connected == true;

    public void Start() => _ = Task.Run(() => KeepConnectedAsync(_lifetime.Token));

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
        var client = new ObsClient(_settings);
        client.Disconnected += () =>
        {
            _client = null;
            _service = null;
            StateChanged?.Invoke(false, "связь потеряна, восстанавливаю…");
            Log?.Invoke("! Связь с OBS потеряна, пробую подключиться заново");
        };

        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var service = new ObsService(client);
        service.OnRecordStateChanged((active, path) => RecordStateChanged?.Invoke(active, path));

        _client = client;
        _service = service;
        _reportedOffline = false;
        StateChanged?.Invoke(true, $"{_settings.Host}:{_settings.Port}");

        // Запись могли начать до запуска приложения — покажем это сразу.
        var status = await service.GetRecordStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.Active)
        {
            RecordStateChanged?.Invoke(true, null);
        }
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

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }
}
