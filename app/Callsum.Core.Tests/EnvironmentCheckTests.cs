using Callsum.Core;

namespace Callsum.Core.Tests;

/// <summary>
/// Главное окно предупреждает о том, что помешает работе. Раньше про молчащую
/// Ollama человек узнавал после часа распознавания.
/// </summary>
public class EnvironmentCheckTests
{
    private static EngineEvent.Doctor Report(string fields)
    {
        var line = $$"""{"event": "doctor", "id": "1", {{fields}}}""";
        return Assert.IsType<EngineEvent.Doctor>(EngineEvent.Parse(line));
    }

    /// <summary>Машина, на которой всё в порядке.</summary>
    private const string Healthy =
        """
        "ffmpeg": true, "ollama": true, "model": true, "summary_enabled": true,
        "summary_model": "qwen3:14b", "cuda_ready": true, "device": "cuda",
        "compute_type": "float16"
        """;

    [Fact]
    public void На_здоровой_машине_предупреждать_не_о_чем()
    {
        Assert.Empty(EnvironmentCheck.Read(Report(Healthy)));
    }

    [Fact]
    public void Без_ffmpeg_сказано_что_ставить()
    {
        var found = EnvironmentCheck.Read(Report(
            Healthy.Replace("\"ffmpeg\": true", "\"ffmpeg\": false")
            + """, "ffmpeg_error": "Не найден ffmpeg. Установите FFmpeg и добавьте его в PATH: winget install Gyan.FFmpeg" """));

        var warning = Assert.Single(found);
        Assert.Equal(WarningLevel.Problem, warning.Level);
        Assert.Contains("FFmpeg", warning.Title);
        // Объяснение берётся от ядра: оно знает, чего именно не нашло.
        Assert.Contains("winget install Gyan.FFmpeg", warning.What);
    }

    [Fact]
    public void Отсутствующий_FFmpeg_который_приедет_сам_не_пугает()
    {
        // Программа донесёт его перед первой обработкой: это не поломка,
        // а предупреждение о сотне мегабайт.
        var found = EnvironmentCheck.Read(Report(
            Healthy.Replace("\"ffmpeg\": true", "\"ffmpeg\": false")
            + """, "ffmpeg_will_download": true """));

        var warning = Assert.Single(found);
        Assert.Equal(WarningLevel.Note, warning.Level);
        Assert.Contains("приедет", warning.Title);
        Assert.DoesNotContain("winget", warning.What);
    }

    [Fact]
    public void Молчащая_Ollama_не_скрывает_что_расшифровка_получится()
    {
        var found = EnvironmentCheck.Read(Report(
            Healthy.Replace("\"ollama\": true", "\"ollama\": false")));

        var warning = Assert.Single(found);
        Assert.Contains("Ollama", warning.Title);
        Assert.Contains("Расшифровка получится", warning.What);
    }

    [Fact]
    public void С_выключенным_протоколом_про_Ollama_молчим()
    {
        // Жалоба на то, чего не просили, приучает не читать предупреждения.
        var found = EnvironmentCheck.Read(Report(
            Healthy
                .Replace("\"ollama\": true", "\"ollama\": false")
                .Replace("\"summary_enabled\": true", "\"summary_enabled\": false")));

        Assert.Empty(found);
    }

    [Fact]
    public void Отсутствующая_модель_названа_по_имени()
    {
        var found = EnvironmentCheck.Read(Report(
            Healthy.Replace("\"model\": true", "\"model\": false")));

        var warning = Assert.Single(found);
        Assert.Contains("qwen3:14b", warning.Title);
        Assert.Contains("ollama pull qwen3:14b", warning.What);
    }

    [Fact]
    public void Нескачанные_библиотеки_объясняют_долгий_первый_запуск()
    {
        var found = EnvironmentCheck.Read(Report(
            Healthy.Replace("\"cuda_ready\": true", "\"cuda_ready\": false")));

        var warning = Assert.Single(found);
        Assert.Equal(WarningLevel.Note, warning.Level);
        Assert.Contains("гигабайт", warning.What);
    }

    [Fact]
    public void Про_процессор_говорим_когда_библиотеки_уже_на_месте()
    {
        // Пока их нет, устройство ещё не выбрано по-настоящему: два замечания
        // об одном и том же только путали бы.
        var onCpu = Healthy.Replace("\"device\": \"cuda\"", "\"device\": \"cpu\"");

        var ready = Assert.Single(EnvironmentCheck.Read(Report(onCpu)));
        Assert.Equal(WarningLevel.Note, ready.Level);
        Assert.Contains("процессоре", ready.Title);

        var downloading = Assert.Single(EnvironmentCheck.Read(Report(
            onCpu.Replace("\"cuda_ready\": true", "\"cuda_ready\": false"))));
        Assert.Contains("Первое распознавание", downloading.Title);
    }

    [Fact]
    public void Недоступная_папка_видна_сразу()
    {
        var found = EnvironmentCheck.Read(Report(
            Healthy + """, "recordings_error": "[WinError 5] Отказано в доступе: 'R:\\\\записи'" """));

        var warning = Assert.Single(found);
        Assert.Equal(WarningLevel.Problem, warning.Level);
        Assert.Contains("папку записей", warning.Title);
        Assert.Contains("настройках", warning.What);
    }

    [Fact]
    public void Старое_ядро_не_вызывает_ложных_тревог()
    {
        // Ответ без новых полей: молчать лучше, чем пугать выдуманным.
        var found = EnvironmentCheck.Read(Report(
            """ "ffmpeg": true, "ollama": true, "model": true, "cuda_ready": true, "device": "cuda" """));

        Assert.Empty(found);
    }
}
