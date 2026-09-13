using Callsum.Core;

namespace Callsum.Core.Tests;

public class DoctorEventTests
{
    [Fact]
    public void Проверка_окружения_приносит_папки()
    {
        var line = """
        {"event": "doctor", "id": "1", "ffmpeg": true, "device": "cuda",
         "recordings": "D:/rec", "out": "D:/out"}
        """;

        var message = Assert.IsType<EngineEvent.Doctor>(EngineEvent.Parse(line));

        Assert.Equal("D:/out", message.OutFolder);
        Assert.Equal("D:/rec", message.RecordingsFolder);
    }

    [Fact]
    public void Отсутствие_папок_в_отчёте_не_ломает_разбор()
    {
        // Старое ядро могло не прислать пути — окно просто не покажет список.
        var message = Assert.IsType<EngineEvent.Doctor>(
            EngineEvent.Parse("""{"event": "doctor", "id": "1", "out": null}"""));

        Assert.Null(message.OutFolder);
        Assert.Null(message.RecordingsFolder);
        Assert.Null(message.MarkdownApp);
    }

    [Fact]
    public void Настройка_чем_открывать_протоколы_доходит_до_окна()
    {
        var message = Assert.IsType<EngineEvent.Doctor>(EngineEvent.Parse(
            """{"event": "doctor", "id": "1", "markdown_app": "code -r {file}"}"""));

        Assert.Equal("code -r {file}", message.MarkdownApp);
    }
}
