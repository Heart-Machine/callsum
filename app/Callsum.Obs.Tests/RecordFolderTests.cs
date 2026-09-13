using System.Text.Json;
using Callsum.Obs;

namespace Callsum.Obs.Tests;

/// <summary>Папка записи в OBS должна совпадать с той, где программа ищет записи.</summary>
public class RecordFolderTests
{
    private const string Folder = @"D:\созвоны\записи";

    private static ObsSettings Settings() =>
        new() { Host = "127.0.0.1", Port = 4455, Password = "пароль" };

    private static async Task<(FakeTransport Transport, ObsService Service, ObsClient Client)> ConnectAsync(
        string profile, string? recordFolder)
    {
        var transport = new FakeTransport();
        transport.Responses["GetProfileList"] =
            $$"""{"currentProfileName": "{{profile}}", "profiles": ["{{profile}}"]}""";
        if (recordFolder is not null)
        {
            transport.Responses["GetRecordDirectory"] =
                $$"""{"recordDirectory": {{JsonSerializer.Serialize(recordFolder)}}}""";
        }

        var client = new ObsClient(Settings(), () => transport);
        await client.ConnectAsync();
        return (transport, new ObsService(client), client);
    }

    private static bool WasSent(FakeTransport transport, string requestType) =>
        transport.Sent.Any(document =>
            document.RootElement.GetProperty("d").TryGetProperty("requestType", out var type)
            && type.GetString() == requestType);

    [Fact]
    public async Task Папка_записи_подтягивается_к_настройкам()
    {
        var (transport, service, client) = await ConnectAsync("callsum", @"C:\старое\место");
        await using var _ = client;

        var change = await service.EnsureRecordFolderAsync("callsum", Folder);

        Assert.True(change.Changed);
        Assert.Null(change.ForeignProfile);
        // Рукопожатие тоже лежит в отправленном, и поля requestType у него нет.
        var request = transport.Sent
            .Select(document => document.RootElement.GetProperty("d"))
            .First(data => data.TryGetProperty("requestType", out var type)
                        && type.GetString() == "SetRecordDirectory")
            .GetProperty("requestData");
        Assert.Equal(Folder, request.GetProperty("recordDirectory").GetString());
    }

    [Fact]
    public async Task Совпадающая_папка_не_трогается()
    {
        // Лишний запрос заставил бы OBS перезаписывать профиль на каждом запуске.
        var (transport, service, client) = await ConnectAsync("callsum", Folder);
        await using var _ = client;

        var change = await service.EnsureRecordFolderAsync("callsum", Folder);

        Assert.False(change.Changed);
        Assert.False(WasSent(transport, "SetRecordDirectory"));
    }

    [Fact]
    public async Task Завершающий_слеш_не_считается_разницей()
    {
        var (transport, service, client) = await ConnectAsync("callsum", Folder + @"\");
        await using var _ = client;

        Assert.False((await service.EnsureRecordFolderAsync("callsum", Folder)).Changed);
        Assert.False(WasSent(transport, "SetRecordDirectory"));
    }

    [Fact]
    public async Task Чужой_профиль_не_трогается()
    {
        // Профиль, которым человек пользуется для всего остального, — не наше дело.
        var (transport, service, client) = await ConnectAsync("Стримы", @"C:\стримы");
        await using var _ = client;

        var change = await service.EnsureRecordFolderAsync("callsum", Folder);

        Assert.False(change.Changed);
        Assert.Equal("Стримы", change.ForeignProfile);
        Assert.False(WasSent(transport, "SetRecordDirectory"));
        Assert.False(WasSent(transport, "GetRecordDirectory"));
    }
}
