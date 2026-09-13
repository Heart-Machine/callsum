using System.Text.Json;

namespace Callsum.Core;

/// <summary>Обработанная запись: папка с расшифровкой и, если он есть, протоколом.</summary>
/// <param name="SourceName">Имя исходной записи из заголовка расшифровки.</param>
/// <param name="SourcePath">Полный путь к записи, который запомнил разбор.</param>
public sealed record CallResult(
    string Name,
    string Folder,
    bool HasSummary,
    DateTimeOffset When,
    string? SourceName = null,
    string? SourcePath = null)
{
    public string TranscriptPath => Path.Combine(Folder, CallResults.TranscriptName);

    public string? SummaryPath => HasSummary ? Path.Combine(Folder, CallResults.SummaryName) : null;

    /// <summary>Что открывать по нажатию: протокол, если он готов, иначе расшифровку.</summary>
    public string MainDocument => SummaryPath ?? TranscriptPath;

    /// <summary>
    /// Где лежит исходная запись — она нужна, чтобы обработать созвон заново.
    ///
    /// Сначала спрашивается путь, запомненный при разборе: он точный. Если
    /// записи там уже нет (папку переименовали, файл перенесли), она ищется по
    /// имени в папке записей. Ничего не нашлось — null, и окно об этом скажет.
    /// </summary>
    public string? FindSource(string? recordingsFolder)
    {
        if (SourcePath is { Length: > 0 } known && File.Exists(known))
        {
            return known;
        }

        if (SourceName is { Length: > 0 } name && recordingsFolder is { Length: > 0 } folder)
        {
            var guess = Path.Combine(folder, name);
            if (File.Exists(guess))
            {
                return guess;
            }
        }

        return null;
    }
}

/// <summary>Чтение списка обработанных записей из папки результатов.</summary>
public static class CallResults
{
    public const string TranscriptName = "transcript.md";
    public const string SummaryName = "summary.md";

    /// <summary>Разбор в машинном виде: там записан полный путь к записи.</summary>
    public const string DataName = "transcript.json";

    /// <summary>
    /// Обработанные записи, свежие сверху.
    ///
    /// Признак обработанной записи — расшифровка: именно её наличие ядро считает
    /// готовностью и по ней же пропускает повторную обработку. Папки без неё
    /// в списке не показываются: там либо чужие файлы, либо работа, оборванная
    /// на середине, и открывать в них нечего.
    /// </summary>
    public static IReadOnlyList<CallResult> Scan(string outFolder, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(outFolder) || !Directory.Exists(outFolder))
        {
            return [];
        }

        var found = new List<CallResult>();
        foreach (var folder in Directory.EnumerateDirectories(outFolder))
        {
            if (Read(folder) is { } result)
            {
                found.Add(result);
            }
        }

        return found
            .OrderByDescending(result => result.When)
            .ThenByDescending(result => result.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>Одна папка результата или null, если расшифровки в ней нет.</summary>
    public static CallResult? Read(string folder)
    {
        var transcript = new FileInfo(Path.Combine(folder, TranscriptName));
        if (!transcript.Exists)
        {
            return null;
        }

        // Время берём по расшифровке, а не по папке: папка меняется и от
        // временных файлов, а расшифровка пишется один раз, когда работа готова.
        return new CallResult(
            Name: new DirectoryInfo(folder).Name,
            Folder: transcript.DirectoryName ?? folder,
            HasSummary: File.Exists(Path.Combine(folder, SummaryName)),
            When: transcript.LastWriteTime,
            SourceName: ReadSourceName(transcript.FullName),
            SourcePath: ReadSourcePath(Path.Combine(folder, DataName)));
    }

    /// <summary>
    /// Полный путь к записи из машинного разбора.
    ///
    /// Читается только начало файла: `source` стоит первым ключом, а сам разбор
    /// весит десятки килобайт на каждый созвон — перебирая сотню папок, их
    /// незачем разбирать целиком.
    /// </summary>
    private static string? ReadSourcePath(string data)
    {
        try
        {
            using var stream = File.OpenRead(data);
            var head = new byte[4096];
            var read = stream.Read(head, 0, head.Length);
            var reader = new Utf8JsonReader(head.AsSpan(0, read), isFinalBlock: false, state: default);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName
                    || reader.CurrentDepth != 1
                    || !reader.ValueTextEquals("source"u8))
                {
                    continue;
                }

                return reader.Read() && reader.TokenType == JsonTokenType.String
                    ? reader.GetString()
                    : null;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException
                                              or UnauthorizedAccessException)
        {
            // Нет разбора или он повреждён — останется имя из расшифровки.
        }

        return null;
    }

    /// <summary>
    /// Имя исходной записи из заголовка расшифровки (строка «- Файл: …»).
    ///
    /// Нужно, чтобы обработать созвон заново: ядру нужен путь к записи, а не
    /// к результату. Имя папки для этого не годится — оно собирается по
    /// настраиваемому шаблону и может не совпадать с именем файла.
    /// </summary>
    private static string? ReadSourceName(string transcript)
    {
        const string marker = "- Файл:";
        try
        {
            using var reader = new StreamReader(transcript);
            // Заголовок идёт первым; читать всю расшифровку ради одной строки незачем.
            for (var line = reader.ReadLine(); line is not null; line = reader.ReadLine())
            {
                if (line.StartsWith(marker, StringComparison.Ordinal))
                {
                    var name = line[marker.Length..].Trim();
                    return name.Length > 0 ? name : null;
                }

                if (line.StartsWith("---", StringComparison.Ordinal))
                {
                    return null;
                }
            }
        }
        catch (IOException)
        {
            // Расшифровку читает кто-то ещё — не повод прятать запись из списка.
        }

        return null;
    }
}
