using System.Text.Json;

namespace Callsum.Core;

/// <summary>Стадии обработки, о которых ядро сообщает по ходу работы.</summary>
public static class EngineStage
{
    /// <summary>Первое распознавание на машине: программа доносит библиотеки CUDA.</summary>
    public const string Download = "download";

    /// <summary>Она же доносит веса модели — это гигабайты и минуты.</summary>
    public const string Model = "model";

    /// <summary>И FFmpeg, если в системе его нет: им извлекаются дорожки.</summary>
    public const string Ffmpeg = "ffmpeg";

    public const string Audio = "audio";
    public const string Transcribe = "transcribe";
    public const string Summary = "summary";
    public const string Done = "done";

    /// <summary>Подпись стадии для окна.</summary>
    public static string Describe(string stage) => stage switch
    {
        Download => "Скачиваю библиотеки для видеокарты",
        Model => "Скачиваю модель распознавания",
        Ffmpeg => "Скачиваю FFmpeg",
        Audio => "Готовлю дорожки…",
        Transcribe => "Распознаю речь",
        Summary => "Составляю протокол…",
        Done => "Готово",
        _ => stage,
    };
}

/// <summary>Событие от ядра. Тип разбирается заранее, остальное лежит в данных.</summary>
public abstract record EngineEvent(string? Id)
{
    /// <summary>Ядро запустилось и сообщило, на чём будет считать.</summary>
    public sealed record Ready(string Version, string Device, string ComputeType) : EngineEvent((string?)null);

    /// <summary>Ход обработки: доля от 0 до 1 либо null, когда она неизвестна.</summary>
    public sealed record Progress(string? Id, string Stage, double? Fraction, string? Detail)
        : EngineEvent(Id);

    public sealed record Log(string? Id, string Text) : EngineEvent(Id);

    public sealed record Done(string? Id, string OutDir, bool HasSummary) : EngineEvent(Id);

    public sealed record Failed(string? Id, string Message) : EngineEvent(Id);

    /// <summary>Ответ на проверку окружения — как есть, чтобы окно показало его само.</summary>
    public sealed record Doctor(string? Id, JsonElement Report) : EngineEvent(Id)
    {
        /// <summary>Папка результатов. Пути живут в настройках ядра, поэтому окно
        /// их спрашивает, а не выводит из своего расположения.</summary>
        public string? OutFolder => Text("out");

        /// <summary>Папка, куда OBS пишет записи.</summary>
        public string? RecordingsFolder => Text("recordings");

        /// <summary>Чем пользователь просил открывать протоколы ([view] markdown_app).</summary>
        public string? MarkdownApp => Text("markdown_app");

        /// <summary>Версия ядра: оно обновляется вместе с приложением, но живёт своей жизнью.</summary>
        public string? Version => Text("version");

        /// <summary>Папка со скачанными библиотеками CUDA.</summary>
        public string? CudaFolder => Text("cuda_dir");

        /// <summary>Папка с весами модели; пусто — общий кэш Hugging Face.</summary>
        public string? ModelFolder => Text("model_dir");

        /// <summary>На чём ядро будет считать: cuda или cpu.</summary>
        public string? Device => Text("device");

        public string? ComputeType => Text("compute_type");

        public bool Ffmpeg => Flag("ffmpeg");

        /// <summary>Ollama отвечает по адресу из настроек.</summary>
        public bool Ollama => Flag("ollama");

        /// <summary>Модель протокола уже загружена в Ollama.</summary>
        public bool SummaryModel => Flag("model");

        /// <summary>Какую модель ждёт протокол: окно скажет, что загружать.</summary>
        public string? SummaryModelName => Text("summary_model");

        /// <summary>Нужен ли протокол вообще — иначе про Ollama говорить незачем.</summary>
        public bool SummaryEnabled => Flag("summary_enabled");

        /// <summary>Почему не вышло завести папку записей.</summary>
        public string? RecordingsError => Text("recordings_error");

        public string? OutError => Text("out_error");

        /// <summary>Библиотеки CUDA на месте: иначе первое распознавание будет долгим.</summary>
        public bool CudaReady => Flag("cuda_ready");

        /// <summary>Почему не вышло — ядро объясняет само, окну остаётся показать.</summary>
        public string? FfmpegError => Text("ffmpeg_error");

        /// <summary>FFmpeg не найден, но программа скачает его сама перед обработкой.</summary>
        public bool FfmpegWillDownload => Flag("ffmpeg_will_download");

        /// <summary>Папка со своей копией FFmpeg; пусто — работаем системным.</summary>
        public string? FfmpegFolder => Text("ffmpeg_dir");

        public string? OllamaError => Text("ollama_error");

        private bool Flag(string name) =>
            Report.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        private string? Text(string name) =>
            Report.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>
    /// Настройки: значения как они записаны в config.toml и куда указывают пути.
    ///
    /// Владелец настроек — ядро: оно читает файл, знает умолчания и проверяет
    /// значения. Окно рисует по этим данным форму и возвращает изменения.
    /// </summary>
    public sealed record Settings(
        string? Id, string Path, JsonElement Values, JsonElement Resolved, JsonElement Defaults)
        : EngineEvent(Id)
    {
        /// <summary>Строковое значение раздела или пусто, если его там нет.</summary>
        public string Text(string section, string key) =>
            Field(section, key) is { ValueKind: JsonValueKind.String } value
                ? value.GetString() ?? ""
                : "";

        public bool Flag(string section, string key) =>
            Field(section, key) is { ValueKind: JsonValueKind.True };

        public double? Number(string section, string key) =>
            Field(section, key) is { ValueKind: JsonValueKind.Number } value
             && value.TryGetDouble(out var number)
                ? number
                : null;

        /// <summary>Значение-список, например расширения файлов записи.</summary>
        public IReadOnlyList<string> List(string section, string key) =>
            SettingValue.ToList(Field(section, key));

        /// <summary>Значение как оно записано в файле — чтобы сравнить с набранным в окне.</summary>
        public JsonElement? Value(string section, string key) => Field(section, key);

        /// <summary>Что стоит в этой настройке по умолчанию.</summary>
        public JsonElement? Default(string section, string key) => Field(Defaults, section, key);

        /// <summary>То же значение словами — окно показывает его подсказкой под полем.</summary>
        public string DefaultText(string section, string key) =>
            SettingValue.Describe(Default(section, key));

        /// <summary>Куда путь указывает на самом деле: в файле он может быть относительным.</summary>
        public string ResolvedPath(string key) => Folder(Resolved, key);

        /// <summary>Куда программа сложила бы всё сама, если не выбирать папку.</summary>
        public string DefaultPath(string key) => DefaultText("paths", key);

        private static string Folder(JsonElement block, string key) =>
            block.ValueKind == JsonValueKind.Object
             && block.TryGetProperty(key, out var value)
             && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        private JsonElement? Field(string section, string key) => Field(Values, section, key);

        private static JsonElement? Field(JsonElement block, string section, string key) =>
            block.ValueKind == JsonValueKind.Object
             && block.TryGetProperty(section, out var values)
             && values.ValueKind == JsonValueKind.Object
             && values.TryGetProperty(key, out var value)
                ? value
                : null;
    }

    /// <summary>Модели, установленные в Ollama: окно предлагает выбрать из них.</summary>
    public sealed record Models(string? Id, IReadOnlyList<string> Names) : EngineEvent(Id);

    /// <summary>Событие неизвестного вида: ядро новее приложения — не повод падать.</summary>
    public sealed record Unknown(string Type, JsonElement Data) : EngineEvent((string?)null);

    /// <summary>Разобрать строку протокола. Возвращает null, если это не JSON.</summary>
    public static EngineEvent? Parse(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("event", out var type))
            {
                return null;
            }

            var id = root.TryGetProperty("id", out var identifier) && identifier.ValueKind == JsonValueKind.String
                ? identifier.GetString()
                : null;

            return type.GetString() switch
            {
                "ready" => new Ready(
                    ReadText(root, "version"), ReadText(root, "device"), ReadText(root, "compute_type")),
                "progress" => new Progress(
                    id,
                    ReadText(root, "stage"),
                    ReadNumber(root, "fraction"),
                    ReadText(root, "detail") is { Length: > 0 } detail ? detail : null),
                "log" => new Log(id, ReadText(root, "text")),
                "done" => new Done(id, ReadText(root, "out_dir"), ReadFlag(root, "summary")),
                "error" => new Failed(id, ReadText(root, "message")),
                "doctor" => new Doctor(id, root.Clone()),
                "models" => new Models(id, SettingValue.ToList(Branch(root, "models"))),
                "settings" => new Settings(
                    id,
                    ReadText(root, "path"),
                    Branch(root, "values"),
                    Branch(root, "resolved"),
                    Branch(root, "defaults")),
                var other => new Unknown(other ?? "", root.Clone()),
            };
        }
    }

    /// <summary>Вложенный объект события — своей копией: исходный документ закроется.</summary>
    private static JsonElement Branch(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.Clone() : default;

    private static string ReadText(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool ReadFlag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static double? ReadNumber(JsonElement root, string name)
    {
        // Проверка вида обязательна: TryGetDouble на значении null не возвращает
        // «не число», а бросает исключение — и убивает чтение событий целиком.
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number))
        {
            return null;
        }

        // Ядро присылает -1, когда сказать о доле нечего: полоса должна бежать.
        return number < 0 ? null : number;
    }
}
