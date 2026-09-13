using System.Text.Json;
using Callsum.Obs;

namespace Callsum.Obs.Tests;

/// <summary>Выбор устройств записи: список даёт OBS, выбор уходит обратно ему.</summary>
public class AudioDeviceTests
{
    /// <summary>Так выглядит настоящий идентификатор устройства WASAPI.</summary>
    private const string Headset = @"{0.0.1.00000000}.{f0b1c2d3}";

    private static ObsSettings Settings() =>
        new() { Host = "127.0.0.1", Port = 4455, Password = "пароль" };

    private static async Task<(FakeTransport Transport, ObsService Service, ObsClient Client)> ConnectAsync(
        params (string Request, string Response)[] answers)
    {
        var transport = new FakeTransport();
        foreach (var (request, response) in answers)
        {
            transport.Responses[request] = response;
        }

        var client = new ObsClient(Settings(), () => transport);
        await client.ConnectAsync();
        return (transport, new ObsService(client), client);
    }

    private static JsonElement RequestData(FakeTransport transport, string requestType) =>
        transport.Sent
            .Select(document => document.RootElement.GetProperty("d"))
            .First(data => data.TryGetProperty("requestType", out var type)
                        && type.GetString() == requestType)
            .GetProperty("requestData");

    [Fact]
    public async Task Список_устройств_спрашивается_у_OBS()
    {
        var (transport, service, client) = await ConnectAsync((
            "GetInputPropertiesListPropertyItems",
            $$"""
            {"propertyItems": [
              {"itemName": "По умолчанию", "itemValue": "default", "itemEnabled": true},
              {"itemName": "Микрофон гарнитуры", "itemValue": "{{Headset}}", "itemEnabled": true}]}
            """));
        await using var _ = client;

        var devices = await service.GetInputDevicesAsync("Микрофон");

        var request = RequestData(transport, "GetInputPropertiesListPropertyItems");
        Assert.Equal("Микрофон", request.GetProperty("inputName").GetString());
        Assert.Equal(ObsService.DeviceProperty, request.GetProperty("propertyName").GetString());
        Assert.Equal(["По умолчанию", "Микрофон гарнитуры"], devices.Select(device => device.Name));
        Assert.Equal(Headset, devices[1].Value);
    }

    [Fact]
    public async Task Недоступное_устройство_в_список_не_попадает()
    {
        // Отключённую гарнитуру OBS показывает, но записать с неё нечего.
        var (_, service, client) = await ConnectAsync((
            "GetInputPropertiesListPropertyItems",
            """
            {"propertyItems": [
              {"itemName": "По умолчанию", "itemValue": "default", "itemEnabled": true},
              {"itemName": "Отключённая гарнитура", "itemValue": "off", "itemEnabled": false}]}
            """));
        await using var _ = client;

        var devices = await service.GetInputDevicesAsync("Микрофон");

        Assert.Equal("По умолчанию", Assert.Single(devices).Name);
    }

    [Fact]
    public async Task Выбранное_устройство_читается_из_настроек_источника()
    {
        var (_, service, client) = await ConnectAsync((
            "GetInputSettings", $$$"""{"inputSettings": {"device_id": "{{{Headset}}}"}}"""));
        await using var _ = client;

        Assert.Equal(Headset, await service.GetInputDeviceAsync("Микрофон"));
    }

    [Fact]
    public async Task Пустые_настройки_означают_устройство_по_умолчанию()
    {
        // OBS не хранит в файле то, что и так является значением по умолчанию.
        var (_, service, client) = await ConnectAsync(("GetInputSettings", """{"inputSettings": {}}"""));
        await using var _ = client;

        Assert.Equal("default", await service.GetInputDeviceAsync("Звук системы"));
    }

    [Fact]
    public async Task Выбор_устройства_не_трогает_остальные_настройки_источника()
    {
        var (transport, service, client) = await ConnectAsync();
        await using var _ = client;

        await service.SetInputDeviceAsync("Микрофон", Headset);

        var request = RequestData(transport, "SetInputSettings");
        Assert.Equal("Микрофон", request.GetProperty("inputName").GetString());
        Assert.Equal(
            Headset,
            request.GetProperty("inputSettings").GetProperty(ObsService.DeviceProperty).GetString());
        // overlay: true — остальные настройки источника остаются как были.
        Assert.True(request.GetProperty("overlay").GetBoolean());
    }
}
