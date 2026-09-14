using Callsum.Obs;

namespace Callsum.Obs.Tests;

/// <summary>
/// Приложение спрашивает у OBS, настроен ли он под созвоны: без профиля
/// и коллекции сцен запись пойдёт с чужими настройками.
/// </summary>
public class SetupStateTests
{
    private static async Task<(ObsService Service, ObsClient Client)> ConnectAsync(
        string profiles, string collections)
    {
        var transport = new FakeTransport();
        transport.Responses["GetProfileList"] =
            $$"""{"currentProfileName": "Стримы", "profiles": {{profiles}}}""";
        transport.Responses["GetSceneCollectionList"] =
            $$"""{"currentSceneCollectionName": "Стримы", "sceneCollections": {{collections}}}""";

        var client = new ObsClient(
            new ObsSettings { Host = "127.0.0.1", Port = 4455, Password = "пароль" },
            () => transport);
        await client.ConnectAsync();
        return (new ObsService(client), client);
    }

    [Fact]
    public async Task Настроенный_OBS_виден_настроенным()
    {
        var (service, client) = await ConnectAsync(
            """["Стримы", "callsum"]""", """["Стримы", "callsum"]""");
        await using var _ = client;

        var state = await service.GetSetupStateAsync("callsum");

        Assert.True(state.Profile);
        Assert.True(state.Collection);
        Assert.True(state.Ready);
    }

    [Fact]
    public async Task Чистый_OBS_настроенным_не_считается()
    {
        // Так выглядит OBS у коллеги сразу после установки.
        var (service, client) = await ConnectAsync("""["Untitled"]""", """["Untitled"]""");
        await using var _ = client;

        var state = await service.GetSetupStateAsync("callsum");

        Assert.False(state.Profile);
        Assert.False(state.Collection);
        Assert.False(state.Ready);
    }

    [Fact]
    public async Task Профиля_без_коллекции_сцен_недостаточно()
    {
        // Настройки записи есть, а источников звука нет: писать будет нечего.
        var (service, client) = await ConnectAsync(
            """["Стримы", "callsum"]""", """["Стримы"]""");
        await using var _ = client;

        var state = await service.GetSetupStateAsync("callsum");

        Assert.True(state.Profile);
        Assert.False(state.Collection);
        Assert.False(state.Ready);
    }

    [Fact]
    public async Task Чужой_профиль_не_считается_нашим()
    {
        // Имя сравнивается целиком: «callsum-2» — не наш профиль.
        var (service, client) = await ConnectAsync(
            """["callsum-2"]""", """["callsum-2"]""");
        await using var _ = client;

        Assert.False((await service.GetSetupStateAsync("callsum")).Ready);
    }
}
