namespace Callsum.Core;

/// <summary>Где приложение ищет ядро обработки.</summary>
public static class EngineLocator
{
    public const string ExecutableName = "callsum-core.exe";

    /// <summary>
    /// Порядок поиска: рядом с приложением (так оно устанавливается у коллеги),
    /// затем в папке сборки ядра внутри проекта — это удобно при разработке,
    /// когда приложение запускается из bin, а ядро собирается в dist.
    /// </summary>
    public static IEnumerable<string> Candidates(string? baseDirectory = null)
    {
        var start = baseDirectory ?? AppContext.BaseDirectory;
        var development = IsBuildOutput(start);
        if (development)
        {
            foreach (var candidate in ProjectCores(start))
            {
                yield return candidate;
            }
        }

        yield return Path.Combine(start, ExecutableName);
        yield return Path.Combine(start, "core", ExecutableName);

        if (!development)
        {
            foreach (var candidate in ProjectCores(start))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> ProjectCores(string start)
    {
        var folder = new DirectoryInfo(start);
        while (folder is not null)
        {
            yield return Path.Combine(folder.FullName, "dist", "callsum-core", ExecutableName);
            folder = folder.Parent;
        }
    }

    /// <summary>
    /// У `dotnet run` рядом с dll могла остаться старая копия core из прежней
    /// публикации. В разработке свежая сборка живёт в dist и должна быть первой.
    /// </summary>
    private static bool IsBuildOutput(string start)
    {
        for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent)
        {
            if (string.Equals(folder.Name, "bin", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Путь к ядру или null, если его нигде нет.</summary>
    public static string? Find(string? baseDirectory = null) =>
        Candidates(baseDirectory).FirstOrDefault(File.Exists);

    /// <summary>Что сказать пользователю, когда ядро не найдено.</summary>
    public static string NotFoundMessage =>
        $"Не найдено ядро обработки ({ExecutableName}). Соберите его командой " +
        "packaging\\build-core.cmd или положите рядом с приложением.";
}
