using System.Text.Json;
using Callsum.Core;

namespace Callsum.Core.Tests;

public class EngineClientTests
{
    private static async Task<T> WaitFor<T>(TaskCompletionSource<T> source) =>
        await source.Task.WaitAsync(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Приветствие_ядра_говорит_на_чём_оно_считает()
    {
        var transport = new FakeEngineTransport();
        await using var engine = new EngineClient(transport);
        var ready = new TaskCompletionSource<EngineEvent.Ready>();
        engine.EventReceived += message =>
        {
            if (message is EngineEvent.Ready value)
            {
                ready.TrySetResult(value);
            }
        };

        await engine.StartAsync();

        var value = await WaitFor(ready);
        Assert.Equal("cuda", value.Device);
        Assert.Equal("float16", value.ComputeType);
        Assert.Equal("1.0.0", value.Version);
    }

    [Fact]
    public async Task Команда_обработки_уходит_с_путём_и_номером()
    {
        var transport = new FakeEngineTransport();
        await using var engine = new EngineClient(transport);
        await engine.StartAsync();

        var id = await engine.ProcessAsync(@"D:\записи\созвон.mkv", force: true);

        var command = JsonDocument.Parse(transport.Commands[0]).RootElement;
        Assert.Equal("process", command.GetProperty("cmd").GetString());
        Assert.Equal(@"D:\записи\созвон.mkv", command.GetProperty("path").GetString());
        Assert.True(command.GetProperty("force").GetBoolean());
        Assert.Equal(id, command.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Номер_занимается_до_того_как_ядро_успеет_ответить()
    {
        var transport = new FakeEngineTransport();
        await using var engine = new EngineClient(transport);
        string? assigned = null;
        var done = new TaskCompletionSource<EngineEvent.Done>();
        engine.EventReceived += message =>
        {
            if (message is EngineEvent.Done value)
            {
                done.TrySetResult(value);
            }
        };
        transport.Sent = command =>
        {
            var payload = JsonDocument.Parse(command).RootElement;
            if (payload.GetProperty("cmd").GetString() != "process")
            {
                return;
            }

            var id = payload.GetProperty("id").GetString();
            Assert.Equal(id, assigned);
            transport.Push($"{{\"event\":\"done\",\"id\":\"{id}\",\"out_dir\":\"D:/out\",\"summary\":false}}");
        };
        await engine.StartAsync();

        var id = await engine.ProcessAsync("созвон.mkv", onAssigned: value => assigned = value);

        Assert.Equal(id, assigned);
        Assert.Equal(id, (await WaitFor(done)).Id);
    }

    [Fact]
    public async Task Номера_заданий_не_повторяются()
    {
        var transport = new FakeEngineTransport();
        await using var engine = new EngineClient(transport);
        await engine.StartAsync();

        var first = await engine.ProcessAsync("первая.mkv");
        var second = await engine.ProcessAsync("вторая.mkv");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Ход_обработки_разбирается_со_стадией_и_долей()
    {
        var transport = new FakeEngineTransport();
        await using var engine = new EngineClient(transport);
        var progress = new TaskCompletionSource<EngineEvent.Progress>();
        engine.EventReceived += message =>
        {
            if (message is EngineEvent.Progress value)
            {
                progress.TrySetResult(value);
            }
        };
        await engine.StartAsync();

        transport.Push(
            """{"event": "progress", "id": "1", "stage": "transcribe", "fraction": 0.42, "detail": "Собеседник"}""");

        var value = await WaitFor(progress);
        Assert.Equal("transcribe", value.Stage);
        Assert.Equal(0.42, value.Fraction);
        Assert.Equal("Собеседник", value.Detail);
    }

    [Fact]
    public async Task Неизвестная_доля_означает_бегущую_полосу()
    {
        // Ядро присылает -1, когда сказать о доле нечего: загрузка модели,
        // составление протокола. Полоса в окне должна бежать, а не стоять на нуле.
        var transport = new FakeEngineTransport();
        await using var engine = new EngineClient(transport);
        var progress = new TaskCompletionSource<EngineEvent.Progress>();
        engine.EventReceived += message =>
        {
            if (message is EngineEvent.Progress value)
            {
                progress.TrySetResult(value);
            }
        };
        await engine.StartAsync();

        transport.Push("""{"event": "progress", "id": "1", "stage": "summary", "fraction": -1.0}""");

        Assert.Null((await WaitFor(progress)).Fraction);
    }

    [Theory]
    [InlineData("""{"event": "progress", "id": "1", "stage": "audio", "fraction": null}""")]
    [InlineData("""{"event": "progress", "id": "1", "stage": "audio"}""")]
    [InlineData("""{"event": "progress", "id": "1", "stage": "audio", "fraction": "неизвестно"}""")]
    public void Неизвестная_доля_в_любом_виде_не_ломает_разбор(string line)
    {
        // Ядро присылает null, когда сказать о доле нечего. Проверять вид
        // значения обязательно: TryGetDouble на null бросает исключение —
        // однажды это убило чтение событий целиком, и окно замолкало.
        var message = Assert.IsType<EngineEvent.Progress>(EngineEvent.Parse(line));

        Assert.Null(message.Fraction);
        Assert.Equal("audio", message.Stage);
    }

    [Fact]
    public async Task Готовый_созвон_приходит_с_папкой_и_признаком_протокола()
    {
        var transport = new FakeEngineTransport();
        await using var engine = new EngineClient(transport);
        var done = new TaskCompletionSource<EngineEvent.Done>();
        engine.EventReceived += message =>
        {
            if (message is EngineEvent.Done value)
            {
                done.TrySetResult(value);
            }
        };
        await engine.StartAsync();

        transport.Push(
            """{"event": "done", "id": "1", "out_dir": "D:/out/созвон", "summary": true}""");

        var value = await WaitFor(done);
        Assert.Equal("D:/out/созвон", value.OutDir);
        Assert.True(value.HasSummary);
    }

    [Fact]
    public async Task Ошибка_обработки_доходит_текстом()
    {
        var transport = new FakeEngineTransport();
        await using var engine = new EngineClient(transport);
        var failed = new TaskCompletionSource<EngineEvent.Failed>();
        engine.EventReceived += message =>
        {
            if (message is EngineEvent.Failed value)
            {
                failed.TrySetResult(value);
            }
        };
        await engine.StartAsync();

        transport.Push("""{"event": "error", "id": "1", "message": "Все дорожки пустые"}""");

        Assert.Equal("Все дорожки пустые", (await WaitFor(failed)).Message);
    }

    [Fact]
    public async Task Посторонняя_строка_не_ломает_чтение_событий()
    {
        // В поток событий может попасть чужой вывод — работу это прерывать не должно.
        var transport = new FakeEngineTransport();
        await using var engine = new EngineClient(transport);
        var diagnostics = new TaskCompletionSource<string>();
        var done = new TaskCompletionSource<EngineEvent.Done>();
        engine.Diagnostics += line => diagnostics.TrySetResult(line);
        engine.EventReceived += message =>
        {
            if (message is EngineEvent.Done value)
            {
                done.TrySetResult(value);
            }
        };
        await engine.StartAsync();

        transport.Push("это не JSON");
        transport.Push("""{"event": "done", "id": "1", "out_dir": "D:/out/созвон", "summary": false}""");

        Assert.Equal("это не JSON", await WaitFor(diagnostics));
        Assert.False((await WaitFor(done)).HasSummary);
    }

    [Fact]
    public async Task Событие_нового_вида_не_роняет_приложение()
    {
        // Ядро может оказаться новее приложения — тогда придут незнакомые события.
        var transport = new FakeEngineTransport();
        await using var engine = new EngineClient(transport);
        var unknown = new TaskCompletionSource<EngineEvent.Unknown>();
        engine.EventReceived += message =>
        {
            if (message is EngineEvent.Unknown value)
            {
                unknown.TrySetResult(value);
            }
        };
        await engine.StartAsync();

        transport.Push("""{"event": "погода", "за_окном": "дождь"}""");

        Assert.Equal("погода", (await WaitFor(unknown)).Type);
    }

    [Fact]
    public void Шаблоны_из_ядра_разбираются_с_источником()
    {
        var message = Assert.IsType<EngineEvent.Prompts>(EngineEvent.Parse(
            """{"event":"prompts","id":"1","folder":"D:/prompts","items":[{"name":"summary_ru.md","content":"{transcript}","custom":true,"error":"нет {meta}"}]}"""));

        Assert.Equal("D:/prompts", message.Folder);
        Assert.Single(message.Items);
        Assert.Equal("Обычный созвон", message.Items[0].Title);
        Assert.True(message.Items[0].IsCustom);
        Assert.Equal("нет {meta}", message.Items[0].Error);
    }

    [Fact]
    public async Task Завершение_ядра_замечается()
    {
        var transport = new FakeEngineTransport();
        await using var engine = new EngineClient(transport);
        var stopped = new TaskCompletionSource();
        engine.Stopped += () => stopped.TrySetResult();
        await engine.StartAsync();

        transport.Close();

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task При_выходе_ядру_отправляется_прощание()
    {
        var transport = new FakeEngineTransport();
        var engine = new EngineClient(transport);
        await engine.StartAsync();

        await engine.DisposeAsync();

        Assert.Contains(transport.Commands, command => command.Contains("shutdown"));
        Assert.True(transport.Disposed);
    }
}
