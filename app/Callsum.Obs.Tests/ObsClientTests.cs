using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Callsum.Obs;

namespace Callsum.Obs.Tests;

public class ObsClientTests
{
    private static ObsSettings Settings(string password = "пароль") =>
        new() { Host = "127.0.0.1", Port = 4455, Password = password };

    [Fact]
    public void Ответ_на_вызов_считается_по_формуле_протокола()
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("пароль" + "соль")));
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + "вызов")));

        Assert.Equal(expected, ObsClient.BuildAuthentication("пароль", "соль", "вызов"));
    }

    [Fact]
    public async Task Рукопожатие_отправляет_ответ_на_вызов_и_подписку_на_события()
    {
        var transport = new FakeTransport();
        await using var client = new ObsClient(Settings(), () => transport);

        await client.ConnectAsync();

        var identify = transport.Sent[0].RootElement.GetProperty("d");
        Assert.Equal(1, transport.Sent[0].RootElement.GetProperty("op").GetInt32());
        Assert.Equal(
            ObsClient.BuildAuthentication("пароль", "соль", "вызов"),
            identify.GetProperty("authentication").GetString());
        Assert.Equal(ObsClient.SubscriptionOutputs, identify.GetProperty("eventSubscriptions").GetInt32());
    }

    [Fact]
    public async Task Без_пароля_ошибка_объясняет_причину()
    {
        var transport = new FakeTransport();
        await using var client = new ObsClient(Settings(password: ""), () => transport);

        var error = await Assert.ThrowsAsync<ObsException>(() => client.ConnectAsync());

        Assert.Contains("пароль", error.Message);
    }

    [Fact]
    public async Task Когда_пароль_не_нужен_подключение_проходит_без_него()
    {
        var transport = new FakeTransport(authRequired: false);
        await using var client = new ObsClient(Settings(password: ""), () => transport);

        await client.ConnectAsync();

        Assert.True(client.Connected);
        Assert.False(transport.Sent[0].RootElement.GetProperty("d").TryGetProperty("authentication", out _));
    }

    [Fact]
    public async Task Запрос_возвращает_данные_ответа()
    {
        var transport = new FakeTransport();
        await using var client = new ObsClient(Settings(), () => transport);
        await client.ConnectAsync();

        var data = await client.RequestAsync("StopRecord");

        Assert.Equal("D:/созвон.mkv", data.GetProperty("outputPath").GetString());
    }

    [Fact]
    public async Task Отказ_OBS_приходит_с_кодом_и_пояснением()
    {
        var transport = new FakeTransport(failRequest: "StopRecord");
        await using var client = new ObsClient(Settings(), () => transport);
        await client.ConnectAsync();

        var error = await Assert.ThrowsAsync<ObsException>(() => client.RequestAsync("StopRecord"));

        Assert.Contains("501", error.Message);
        Assert.Contains("запись не идёт", error.Message);
    }

    [Fact]
    public async Task События_доходят_до_подписчика()
    {
        var transport = new FakeTransport();
        await using var client = new ObsClient(Settings(), () => transport);
        var received = new TaskCompletionSource<ObsEvent>();
        client.EventReceived += obsEvent => received.TrySetResult(obsEvent);
        await client.ConnectAsync();

        transport.PushEvent(
            "RecordStateChanged",
            "{\"outputState\": \"OBS_WEBSOCKET_OUTPUT_STOPPED\", \"outputPath\": \"D:/созвон.mkv\"}");

        var obsEvent = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("RecordStateChanged", obsEvent.Type);
        Assert.Equal("D:/созвон.mkv", obsEvent.Data.GetProperty("outputPath").GetString());
    }

    [Fact]
    public async Task Запрос_без_подключения_отклоняется_сразу()
    {
        await using var client = new ObsClient(Settings(), () => new FakeTransport());

        var error = await Assert.ThrowsAsync<ObsException>(() => client.RequestAsync("GetRecordStatus"));

        Assert.Contains("Нет подключения", error.Message);
    }

    [Fact]
    public async Task Обрыв_связи_будит_ожидающих_и_сообщает_подписчику()
    {
        var transport = new FakeTransport(failRequest: "__ничего__");
        await using var client = new ObsClient(Settings(), () => transport);
        var disconnected = new TaskCompletionSource();
        client.Disconnected += () => disconnected.TrySetResult();
        await client.ConnectAsync();

        await transport.CloseAsync();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(client.Connected);
    }
}
