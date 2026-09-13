namespace Callsum.Core;

/// <summary>Обработанная запись: папка с расшифровкой и, если он есть, протоколом.</summary>
public sealed record CallResult(string Name, string Folder, bool HasSummary, DateTimeOffset When)
{
    public string TranscriptPath => Path.Combine(Folder, CallResults.TranscriptName);

    public string? SummaryPath => HasSummary ? Path.Combine(Folder, CallResults.SummaryName) : null;

    /// <summary>Что открывать по нажатию: протокол, если он готов, иначе расшифровку.</summary>
    public string MainDocument => SummaryPath ?? TranscriptPath;
}

/// <summary>Чтение списка обработанных записей из папки результатов.</summary>
public static class CallResults
{
    public const string TranscriptName = "transcript.md";
    public const string SummaryName = "summary.md";

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
            When: transcript.LastWriteTime);
    }
}
