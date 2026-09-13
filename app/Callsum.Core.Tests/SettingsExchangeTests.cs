using System.Text.Json;
using Callsum.Core;

namespace Callsum.Core.Tests;

public class SettingsExchangeTests
{
    private const string Report = """
    {"event": "settings", "id": "1", "path": "C:/Users/User/AppData/Roaming/callsum/config.toml",
     "values": {"paths": {"out": "out", "folder_template": "{name}"},
                "summary": {"model": "qwen3:14b", "num_ctx": 8192, "enabled": true},
                "transcribe": {"vad": false}},
     "resolved": {"recordings": "D:/rec", "out": "D:/out"}}
    """;

    [Fact]
    public void Настройки_разбираются_по_разделам()
    {
        var message = Assert.IsType<EngineEvent.Settings>(EngineEvent.Parse(Report));

        Assert.EndsWith("config.toml", message.Path);
        Assert.Equal("qwen3:14b", message.Text("summary", "model"));
        Assert.Equal("{name}", message.Text("paths", "folder_template"));
        Assert.Equal(8192, message.Number("summary", "num_ctx"));
        Assert.True(message.Flag("summary", "enabled"));
        Assert.False(message.Flag("transcribe", "vad"));
    }

    [Fact]
    public void Путь_показывается_тот_куда_он_ведёт()
    {
        // В файле путь может быть относительным, а показать нужно настоящую папку.
        var message = Assert.IsType<EngineEvent.Settings>(EngineEvent.Parse(Report));

        Assert.Equal("out", message.Text("paths", "out"));
        Assert.Equal("D:/out", message.ResolvedPath("out"));
    }

    [Fact]
    public void Отсутствующее_значение_не_ломает_окно()
    {
        var message = Assert.IsType<EngineEvent.Settings>(EngineEvent.Parse(
            """{"event": "settings", "id": "1", "path": "config.toml"}"""));

        Assert.Equal("", message.Text("summary", "model"));
        Assert.Null(message.Number("summary", "num_ctx"));
        Assert.False(message.Flag("summary", "enabled"));
        Assert.Equal("", message.ResolvedPath("out"));
    }

    [Fact]
    public async Task Ответ_ждут_по_номеру_задания()
    {
        var transport = new FakeEngineTransport();
        await using var engine = new EngineClient(transport);
        await engine.StartAsync();

        var asking = engine.GetSettingsAsync();
        // Пока ядро занято, приходят чужие события — ответом они не являются.
        transport.Push("""{"event": "progress", "id": "1", "stage": "transcribe", "fraction": 0.5}""");
        transport.Push("""{"event": "done", "id": "42", "out_dir": "D:/out", "summary": true}""");
        Assert.False(asking.IsCompleted);

        transport.Push(Report.Replace("\"id\": \"1\"", "\"id\": \"1\""));

        var settings = await asking.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("qwen3:14b", settings.Text("summary", "model"));
    }

    [Fact]
    public async Task Изменения_уходят_разделами()
    {
        var transport = new FakeEngineTransport();
        await using var engine = new EngineClient(transport);
        await engine.StartAsync();

        var saving = engine.SaveSettingsAsync(new Dictionary<string, Dictionary<string, object?>>
        {
            ["paths"] = new() { ["out"] = @"D:\созвоны" },
            ["summary"] = new() { ["num_ctx"] = 12288, ["enabled"] = false },
        });
        transport.Push(Report);
        await saving.WaitAsync(TimeSpan.FromSeconds(5));

        var command = JsonDocument.Parse(transport.Commands[0]).RootElement;
        Assert.Equal("settings_set", command.GetProperty("cmd").GetString());
        var values = command.GetProperty("values");
        Assert.Equal(@"D:\созвоны", values.GetProperty("paths").GetProperty("out").GetString());
        Assert.Equal(12288, values.GetProperty("summary").GetProperty("num_ctx").GetInt32());
        Assert.False(values.GetProperty("summary").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Отказ_ядра_доходит_до_окна_ошибкой()
    {
        var transport = new FakeEngineTransport();
        await using var engine = new EngineClient(transport);
        await engine.StartAsync();

        var saving = engine.SaveSettingsAsync(new Dictionary<string, Dictionary<string, object?>>
        {
            ["погода"] = new() { ["дождь"] = true },
        });
        transport.Push("""{"event": "error", "id": "1", "message": "Неизвестные разделы настроек: погода"}""");

        var failure = await Assert.ThrowsAsync<EngineException>(
            async () => await saving.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("погода", failure.Message);
    }

    [Fact]
    public async Task Молчание_умершего_ядра_не_вешает_окно_навсегда()
    {
        // Иначе окно настроек осталось бы с надписью «читаю настройки…» насовсем.
        var transport = new FakeEngineTransport();
        await using var engine = new EngineClient(transport);
        await engine.StartAsync();

        var asking = engine.GetSettingsAsync();
        transport.Close();

        await Assert.ThrowsAsync<EngineException>(
            async () => await asking.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
