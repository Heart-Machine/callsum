namespace Callsum.Core;

/// <summary>Насколько это мешает: «так не заработает» или «просто знайте».</summary>
public enum WarningLevel
{
    /// <summary>Работать будет, но не так, как человек ожидает.</summary>
    Note,

    /// <summary>Что-то из нужного не получится.</summary>
    Problem,
}

/// <summary>Замечание об окружении: что не так и что с этим делать.</summary>
public sealed record EnvironmentWarning(WarningLevel Level, string Title, string What);

/// <summary>
/// Что в окружении помешает работе.
///
/// Ядро умеет проверять окружение, но раньше его ответ читала только вкладка
/// «О программе»: про молчащую Ollama человек узнавал после часа распознавания,
/// когда протокол не собирался. Здесь тот же ответ превращается в замечания
/// для главного окна — с тем, что делать, а не только с тем, что случилось.
/// </summary>
public static class EnvironmentCheck
{
    public static IReadOnlyList<EnvironmentWarning> Read(EngineEvent.Doctor doctor)
    {
        var found = new List<EnvironmentWarning>();

        if (!doctor.Ffmpeg && doctor.FfmpegWillDownload)
        {
            // Это не поломка: программа донесёт его сама перед первой
            // обработкой. Сказать стоит, чтобы сотня мегабайт не стала
            // неожиданностью в тот момент, когда человек ждёт расшифровку.
            found.Add(new EnvironmentWarning(
                WarningLevel.Note,
                "FFmpeg приедет при первой обработке",
                "Им извлекаются дорожки из записи. Это около 110 МБ, один раз на машину — "
                + "ставить его руками не нужно."));
        }
        else if (!doctor.Ffmpeg)
        {
            found.Add(new EnvironmentWarning(
                WarningLevel.Problem,
                "Нет FFmpeg — записи не обработать",
                Explain(
                    doctor.FfmpegError,
                    "Установите его: winget install Gyan.FFmpeg — и перезапустите callsum.")));
        }

        // Про Ollama говорим, только когда протокол нужен: с выключенным
        // протоколом это была бы жалоба на то, чего не просили.
        if (doctor.SummaryEnabled && !doctor.Ollama)
        {
            found.Add(new EnvironmentWarning(
                WarningLevel.Problem,
                "Ollama не отвечает — протокол не соберётся",
                Explain(doctor.OllamaError, "Запустите Ollama.")
                + " Расшифровка получится и без неё: протокол можно собрать потом, кнопкой «Заново»."));
        }
        else if (doctor.SummaryEnabled && !doctor.SummaryModel)
        {
            var model = doctor.SummaryModelName is { Length: > 0 } name ? name : "модель протокола";
            found.Add(new EnvironmentWarning(
                WarningLevel.Problem,
                $"В Ollama нет модели {model}",
                $"Загрузите её один раз: ollama pull {model}. "
                + "Либо выберите другую в настройках, на вкладке «Протокол»."));
        }

        if (!doctor.CudaReady)
        {
            found.Add(new EnvironmentWarning(
                WarningLevel.Note,
                "Первое распознавание будет долгим",
                "Библиотеки видеокарты и модель распознавания программа скачает один раз — "
                + "это несколько гигабайт. Ход скачивания будет виден здесь же."));
        }
        else if (doctor.Device == "cpu")
        {
            // Видеокарты может не быть, а может быть выбран процессор руками —
            // поэтому говорим о последствии, а не о причине.
            found.Add(new EnvironmentWarning(
                WarningLevel.Note,
                "Распознавание пойдёт на процессоре",
                "Это в десятки раз дольше, чем на видеокарте: часовой созвон будет считаться "
                + "часами. Если видеокарта NVIDIA есть, проверьте «На чём считать» в настройках."));
        }

        foreach (var (folder, name) in new[]
                 {
                     (doctor.RecordingsError, "записей"),
                     (doctor.OutError, "результатов"),
                 })
        {
            if (folder is { Length: > 0 } trouble)
            {
                found.Add(new EnvironmentWarning(
                    WarningLevel.Problem,
                    $"Не удалось создать папку {name}",
                    $"{trouble} Выберите другую папку в настройках, на вкладке «Папки»."));
            }
        }

        return found;
    }

    /// <summary>Объяснение от ядра, а если его нет — своё.</summary>
    private static string Explain(string? reported, string fallback) =>
        reported is { Length: > 0 } text ? text : fallback;
}
