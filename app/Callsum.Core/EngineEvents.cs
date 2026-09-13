using System.Text.Json;

namespace Callsum.Core;

/// <summary>Стадии обработки, о которых ядро сообщает по ходу работы.</summary>
public static class EngineStage
{
    public const string Audio = "audio";
    public const string Transcribe = "transcribe";
    public const string Summary = "summary";
    public const string Done = "done";

    /// <summary>Подпись стадии для окна.</summary>
    public static string Describe(string stage) => stage switch
    {
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
    public sealed record Settings(string? Id, string Path, JsonElement Values, JsonElement Resolved)
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

        /// <summary>Куда путь указывает на самом деле: в файле он может быть относительным.</summary>
        public string ResolvedPath(string key) =>
            Resolved.ValueKind == JsonValueKind.Object
             && Resolved.TryGetProperty(key, out var value)
             && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        private JsonElement? Field(string section, string key) =>
            Values.ValueKind == JsonValueKind.Object
             && Values.TryGetProperty(section, out var block)
             && block.ValueKind == JsonValueKind.Object
             && block.TryGetProperty(key, out var value)
                ? value
                : null;
    }

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
                "settings" => new Settings(
                    id,
                    ReadText(root, "path"),
                    Branch(root, "values"),
                    Branch(root, "resolved")),
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
