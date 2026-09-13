using System.Text.Json;

namespace Callsum.Obs;

/// <summary>Состояние записи в OBS.</summary>
public sealed record RecordStatus(bool Active, TimeSpan Duration);

/// <summary>Источник звука в текущей коллекции сцен.</summary>
public sealed record ObsInput(string Name, string Kind);

/// <summary>Устройство записи звука: как его зовут в Windows и как его зовёт OBS.</summary>
public sealed record ObsDevice(string Name, string Value);

/// <summary>
/// Операции с OBS, нужные для записи созвонов. Поверх клиента протокола, чтобы
/// приложение работало с понятиями «начать запись», «переключить профиль»,
/// а не с именами запросов.
/// </summary>
public sealed class ObsService
{
    private readonly ObsClient _client;

    public ObsService(ObsClient client) => _client = client;

    public ObsClient Client => _client;

    // --- запись -------------------------------------------------------
    public Task StartRecordAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync("StartRecord", cancellationToken: cancellationToken);

    /// <summary>Остановить запись и вернуть путь к файлу — OBS отдаёт его в ответе.</summary>
    public async Task<string?> StopRecordAsync(CancellationToken cancellationToken = default)
    {
        var data = await _client.RequestAsync("StopRecord", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return data.TryGetProperty("outputPath", out var path) ? path.GetString() : null;
    }

    public async Task<RecordStatus> GetRecordStatusAsync(CancellationToken cancellationToken = default)
    {
        var data = await _client.RequestAsync("GetRecordStatus", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var active = data.GetProperty("outputActive").GetBoolean();
        var milliseconds = data.TryGetProperty("outputDuration", out var duration) ? duration.GetDouble() : 0;
        return new RecordStatus(active, TimeSpan.FromMilliseconds(milliseconds));
    }

    /// <summary>
    /// Подписаться на старт и стоп записи — в том числе начатые из самого OBS
    /// или по горячей клавише.
    /// </summary>
    public void OnRecordStateChanged(Action<bool, string?> callback)
    {
        _client.EventReceived += obsEvent =>
        {
            if (obsEvent.Type != "RecordStateChanged")
            {
                return;
            }

            var state = obsEvent.Data.TryGetProperty("outputState", out var value) ? value.GetString() ?? "" : "";
            if (state.EndsWith("_STARTED", StringComparison.Ordinal))
            {
                callback(true, null);
            }
            else if (state.EndsWith("_STOPPED", StringComparison.Ordinal))
            {
                var path = obsEvent.Data.TryGetProperty("outputPath", out var output) ? output.GetString() : null;
                callback(false, string.IsNullOrEmpty(path) ? null : path);
            }
        };
    }

    // --- профиль и коллекция сцен -------------------------------------
    public async Task<(string Current, IReadOnlyList<string> All)> GetProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        var data = await _client.RequestAsync("GetProfileList", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (data.GetProperty("currentProfileName").GetString() ?? "", ReadStrings(data, "profiles"));
    }

    public Task CreateProfileAsync(string name, CancellationToken cancellationToken = default) =>
        _client.RequestAsync("CreateProfile", new { profileName = name }, cancellationToken);

    public Task SetCurrentProfileAsync(string name, CancellationToken cancellationToken = default) =>
        _client.RequestAsync("SetCurrentProfile", new { profileName = name }, cancellationToken);

    public async Task<(string Current, IReadOnlyList<string> All)> GetSceneCollectionsAsync(
        CancellationToken cancellationToken = default)
    {
        var data = await _client.RequestAsync("GetSceneCollectionList", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (data.GetProperty("currentSceneCollectionName").GetString() ?? "",
                ReadStrings(data, "sceneCollections"));
    }

    public Task CreateSceneCollectionAsync(string name, CancellationToken cancellationToken = default) =>
        _client.RequestAsync("CreateSceneCollection", new { sceneCollectionName = name }, cancellationToken);

    public Task SetCurrentSceneCollectionAsync(string name, CancellationToken cancellationToken = default) =>
        _client.RequestAsync("SetCurrentSceneCollection", new { sceneCollectionName = name }, cancellationToken);

    /// <summary>
    /// Переключить профиль вместе с одноимённой коллекцией сцен: настройки записи
    /// живут в профиле, а раскладка звука по дорожкам — в коллекции.
    /// </summary>
    public async Task SwitchToProfileAsync(string name, CancellationToken cancellationToken = default)
    {
        var (currentProfile, _) = await GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        if (currentProfile != name)
        {
            await SetCurrentProfileAsync(name, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        var (currentCollection, collections) = await GetSceneCollectionsAsync(cancellationToken).ConfigureAwait(false);
        if (collections.Contains(name) && currentCollection != name)
        {
            await SetCurrentSceneCollectionAsync(name, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    public Task SetProfileParameterAsync(
        string category, string name, string value, CancellationToken cancellationToken = default) =>
        _client.RequestAsync(
            "SetProfileParameter",
            new { parameterCategory = category, parameterName = name, parameterValue = value },
            cancellationToken);

    public async Task<string?> GetRecordDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var data = await _client.RequestAsync("GetRecordDirectory", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return data.TryGetProperty("recordDirectory", out var value) ? value.GetString() : null;
    }

    public Task SetRecordDirectoryAsync(string folder, CancellationToken cancellationToken = default) =>
        _client.RequestAsync("SetRecordDirectory", new { recordDirectory = folder }, cancellationToken);

    public Task SetVideoSettingsAsync(int fps, int width, int height, CancellationToken cancellationToken = default) =>
        _client.RequestAsync(
            "SetVideoSettings",
            new
            {
                fpsNumerator = fps,
                fpsDenominator = 1,
                baseWidth = width,
                baseHeight = height,
                outputWidth = width,
                outputHeight = height,
            },
            cancellationToken);

    // --- источники звука ----------------------------------------------
    public async Task<string> GetCurrentSceneAsync(CancellationToken cancellationToken = default)
    {
        var data = await _client.RequestAsync("GetCurrentProgramScene", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (data.TryGetProperty("currentProgramSceneName", out var current))
        {
            return current.GetString() ?? "";
        }

        return data.TryGetProperty("sceneName", out var legacy) ? legacy.GetString() ?? "" : "";
    }

    public async Task<IReadOnlyList<ObsInput>> GetInputsAsync(CancellationToken cancellationToken = default)
    {
        var data = await _client.RequestAsync("GetInputList", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var inputs = new List<ObsInput>();
        foreach (var item in data.GetProperty("inputs").EnumerateArray())
        {
            inputs.Add(new ObsInput(
                item.GetProperty("inputName").GetString() ?? "",
                item.TryGetProperty("inputKind", out var kind) ? kind.GetString() ?? "" : ""));
        }

        return inputs;
    }

    public Task CreateInputAsync(
        string scene, string name, string kind, CancellationToken cancellationToken = default) =>
        _client.RequestAsync(
            "CreateInput",
            new
            {
                sceneName = scene,
                inputName = name,
                inputKind = kind,
                inputSettings = new { device_id = "default" },
                sceneItemEnabled = true,
            },
            cancellationToken);

    /// <summary>Настройка источника звука, в которой записано выбранное устройство.</summary>
    public const string DeviceProperty = "device_id";

    /// <summary>
    /// Устройства, между которыми можно выбирать для этого источника.
    ///
    /// Список даёт сам OBS: он перечисляет устройства Windows так же, как
    /// в окне свойств источника, вместе со значением «по умолчанию».
    /// </summary>
    public async Task<IReadOnlyList<ObsDevice>> GetInputDevicesAsync(
        string inputName, CancellationToken cancellationToken = default)
    {
        var data = await _client.RequestAsync(
            "GetInputPropertiesListPropertyItems",
            new { inputName, propertyName = DeviceProperty },
            cancellationToken).ConfigureAwait(false);

        var devices = new List<ObsDevice>();
        if (!data.TryGetProperty("propertyItems", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return devices;
        }

        foreach (var item in items.EnumerateArray())
        {
            // Недоступное устройство (отключённая гарнитура) OBS показывает,
            // но выбирать его нечего: запись вышла бы пустой.
            if (item.TryGetProperty("itemEnabled", out var enabled)
                && enabled.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            var value = item.TryGetProperty("itemValue", out var raw) && raw.ValueKind == JsonValueKind.String
                ? raw.GetString() ?? ""
                : "";
            var name = item.TryGetProperty("itemName", out var title) ? title.GetString() ?? "" : "";
            if (value.Length > 0)
            {
                devices.Add(new ObsDevice(name.Length > 0 ? name : value, value));
            }
        }

        return devices;
    }

    /// <summary>Какое устройство выбрано у источника сейчас.</summary>
    public async Task<string> GetInputDeviceAsync(
        string inputName, CancellationToken cancellationToken = default)
    {
        var data = await _client.RequestAsync(
            "GetInputSettings", new { inputName }, cancellationToken).ConfigureAwait(false);

        // Пустые настройки означают устройство по умолчанию: OBS не пишет
        // в файл то, что и так является значением по умолчанию.
        return data.TryGetProperty("inputSettings", out var settings)
            && settings.ValueKind == JsonValueKind.Object
            && settings.TryGetProperty(DeviceProperty, out var device)
            && device.ValueKind == JsonValueKind.String
                ? device.GetString() ?? "default"
                : "default";
    }

    /// <summary>Выбрать устройство источнику.</summary>
    public Task SetInputDeviceAsync(
        string inputName, string device, CancellationToken cancellationToken = default) =>
        _client.RequestAsync(
            "SetInputSettings",
            new
            {
                inputName,
                inputSettings = new Dictionary<string, object?> { [DeviceProperty] = device },
                // Остальные настройки источника остаются как были.
                overlay = true,
            },
            cancellationToken);

    /// <summary>Оставить источнику только одну дорожку — ту, на которую он должен писать.</summary>
    public Task SetInputTrackAsync(string name, int track, CancellationToken cancellationToken = default) =>
        _client.RequestAsync(
            "SetInputAudioTracks",
            new { inputName = name, inputAudioTracks = TrackMap(track) },
            cancellationToken);

    public static Dictionary<string, bool> TrackMap(int active) =>
        Enumerable.Range(1, 6).ToDictionary(index => index.ToString(), index => index == active);

    private static IReadOnlyList<string> ReadStrings(JsonElement data, string property)
    {
        var values = new List<string>();
        if (data.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var value = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.TryGetProperty("sceneCollectionName", out var name) ? name.GetString() : null;
                if (!string.IsNullOrEmpty(value))
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }
}
