using System.Text.Json;

namespace Callsum.Obs;

/// <summary>
/// Создание отдельного профиля и коллекции сцен под запись созвонов.
///
/// Настройки OBS живут в двух переключаемых контейнерах: профиль хранит вывод
/// (формат, дорожки, папка, видео), коллекция сцен — источники и раскладку звука
/// по дорожкам. Поэтому callsum заводит свои и не трогает те, которыми
/// пользователь пользуется для всего остального.
/// </summary>
public sealed class ObsSetup
{
    public const string DefaultProfile = "callsum";

    /// <summary>Имя источника, который пишет микрофон — первую дорожку.</summary>
    public const string MicrophoneInput = "Микрофон";

    /// <summary>Имя источника, который пишет всё, что играет в колонках, — вторую дорожку.</summary>
    public const string SystemAudioInput = "Звук системы";

    /// <summary>Микрофон — дорожка 1, всё, что играет в колонках, — дорожка 2.</summary>
    private static readonly (string Kind, string DefaultName, int Track)[] AudioSources =
    [
        ("wasapi_input_capture", MicrophoneInput, 1),
        ("wasapi_output_capture", SystemAudioInput, 2),
    ];

    /// <summary>
    /// Пустую чёрную картинку незачем писать с дефолтным битрейтом OBS: на CRF 32
    /// час записи занимает единицы мегабайт вместо гигабайта.
    /// </summary>
    private static readonly object RecordEncoder = new
    {
        bitrate = 500,
        crf = 32,
        keyint_sec = 4,
        preset = "veryfast",
        profile = "high",
        rate_control = "CRF",
        tune = "",
        x264opts = "",
    };

    private readonly ObsService _obs;
    private readonly Action<string> _log;

    public ObsSetup(ObsService obs, Action<string>? log = null)
    {
        _obs = obs;
        _log = log ?? Console.WriteLine;
    }

    public static string ProfilesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "obs-studio", "basic", "profiles");

    public async Task RunAsync(
        string name,
        string recordingsFolder,
        string filenameFormat = "%CCYY-%MM-%DD %hh-%mm-%ss",
        CancellationToken cancellationToken = default)
    {
        var (previous, profiles) = await _obs.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        _log($"Текущий профиль OBS: «{previous}» — он останется нетронутым");

        if (!profiles.Contains(name))
        {
            await _obs.CreateProfileAsync(name, cancellationToken).ConfigureAwait(false);
            _log($"Создан профиль «{name}»");
        }
        else
        {
            await _obs.SwitchToProfileAsync(name, cancellationToken).ConfigureAwait(false);
            _log($"Профиль «{name}» уже был, обновляю настройки");
        }

        Directory.CreateDirectory(recordingsFolder);
        var parameters = new (string Section, string Key, string Value)[]
        {
            ("Output", "Mode", "Advanced"),
            ("Output", "FilenameFormatting", filenameFormat),
            ("AdvOut", "RecType", "Standard"),
            ("AdvOut", "RecFormat2", "mkv"),
            // Битовая маска дорожек: 1 (микрофон) + 2 (звук системы) = 3.
            ("AdvOut", "RecTracks", "3"),
            ("AdvOut", "RecFilePath", recordingsFolder),
            ("AdvOut", "RecEncoder", "obs_x264"),
            // Звук: 96 кбит/с на дорожку — для речи с запасом, а файл втрое легче.
            ("AdvOut", "Track1Bitrate", "96"),
            ("AdvOut", "Track2Bitrate", "96"),
        };

        foreach (var (section, key, value) in parameters)
        {
            await _obs.SetProfileParameterAsync(section, key, value, cancellationToken).ConfigureAwait(false);
        }

        await _obs.SetRecordDirectoryAsync(recordingsFolder, cancellationToken).ConfigureAwait(false);
        _log($"Запись: mkv, дорожки 1+2, папка {recordingsFolder}");
        _log($"Имя файла записи: {filenameFormat}");

        WriteEncoderSettings(name);

        // Картинка для протокола не нужна, поэтому холст маленький и 10 кадров/с:
        // файл занимает копейки, а видеокодек почти не ест процессор.
        await _obs.SetVideoSettingsAsync(10, 640, 360, cancellationToken).ConfigureAwait(false);
        _log("Видео: 640x360, 10 кадров/с (пустая картинка, нужен только звук)");

        var (_, collections) = await _obs.GetSceneCollectionsAsync(cancellationToken).ConfigureAwait(false);
        if (!collections.Contains(name))
        {
            await _obs.CreateSceneCollectionAsync(name, cancellationToken).ConfigureAwait(false);
            // OBS перестраивает микшер в потоке интерфейса; если сразу лезть
            // к источникам, он успевает зависнуть — даём ему договорить.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            _log($"Создана коллекция сцен «{name}»");
        }
        else
        {
            await _obs.SwitchToProfileAsync(name, cancellationToken).ConfigureAwait(false);
            _log($"Коллекция сцен «{name}» уже была");
        }

        await RouteAudioAsync(cancellationToken).ConfigureAwait(false);
        await ApplyProfileAsync(name, cancellationToken).ConfigureAwait(false);
        _log("Готово. Вернуть свои настройки: меню «Профиль» и «Коллекция сцен» в OBS.");
    }

    /// <summary>Развести микрофон и звук системы по разным дорожкам.</summary>
    private async Task RouteAudioAsync(CancellationToken cancellationToken)
    {
        var scene = await _obs.GetCurrentSceneAsync(cancellationToken).ConfigureAwait(false);
        var inputs = await _obs.GetInputsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (kind, defaultName, track) in AudioSources)
        {
            var name = inputs.FirstOrDefault(input => input.Kind == kind)?.Name;
            if (name is null)
            {
                name = defaultName;
                await _obs.CreateInputAsync(scene, name, kind, cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                _log($"Добавлен источник «{name}»");
            }

            await _obs.SetInputTrackAsync(name, track, cancellationToken).ConfigureAwait(false);
            _log($"«{name}» -> дорожка {track}");
        }
    }

    /// <summary>
    /// Перечитать профиль: переключаем его туда-обратно.
    ///
    /// Изменение параметров правит конфиг, но активный вывод OBS продолжает
    /// работать на старых значениях до перезагрузки профиля — без этого шага
    /// запись всё ещё шла бы в mp4 с одной дорожкой. Заодно OBS сохраняет
    /// конфиг на диск, и настройки переживают аварийное завершение.
    /// </summary>
    private async Task ApplyProfileAsync(string name, CancellationToken cancellationToken)
    {
        var (_, profiles) = await _obs.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        var other = profiles.FirstOrDefault(profile => profile != name);
        if (other is null)
        {
            _log("! Перезапустите OBS, чтобы настройки профиля вступили в силу");
            return;
        }

        await _obs.SetCurrentProfileAsync(other, cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
        await _obs.SetCurrentProfileAsync(name, cancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
        _log("Профиль перечитан, настройки записи активны");
    }

    /// <summary>Настройки видеокодека OBS хранит файлом рядом с профилем, не в конфиге.</summary>
    private void WriteEncoderSettings(string profile)
    {
        var folder = FindProfileDirectory(profile);
        if (folder is null)
        {
            _log("! Папка профиля не найдена — битрейт видео остался по умолчанию");
            return;
        }

        File.WriteAllText(
            Path.Combine(folder, "recordEncoder.json"),
            JsonSerializer.Serialize(RecordEncoder, new JsonSerializerOptions { WriteIndented = true }));
        _log("Видеокодек: x264 CRF 32 (чёрная картинка весит копейки)");
    }

    /// <summary>Папка профиля: её имя OBS чистит от спецсимволов, поэтому ищем по basic.ini.</summary>
    public static string? FindProfileDirectory(string profile, string? profilesDirectory = null)
    {
        var root = profilesDirectory ?? ProfilesDirectory;
        if (!Directory.Exists(root))
        {
            return null;
        }

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var ini = Path.Combine(folder, "basic.ini");
            if (File.Exists(ini) && File.ReadLines(ini).Any(line => line.Trim() == $"Name={profile}"))
            {
                return folder;
            }
        }

        return null;
    }
}
