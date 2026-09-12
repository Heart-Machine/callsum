using Callsum.Obs;

namespace Callsum.Obs.Tests;

public class ObsSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "callsum-tests-" + Guid.NewGuid().ToString("N"));

    private string WriteConfig(string json)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Пароль_и_порт_берутся_из_настроек_OBS()
    {
        // Пароль лежит в конфиге самого OBS — спрашивать его у пользователя не нужно.
        var path = WriteConfig(
            """{"server_enabled": true, "server_port": 4466, "auth_required": true, "server_password": "тайна"}""");

        var settings = ObsSettings.Load(path);

        Assert.Equal(4466, settings.Port);
        Assert.Equal("тайна", settings.Password);
        Assert.True(settings.EnabledInObs);
    }

    [Fact]
    public void Выключенный_сервер_виден_в_настройках()
    {
        var path = WriteConfig("""{"server_enabled": false, "server_port": 4455, "auth_required": false}""");

        Assert.False(ObsSettings.Load(path).EnabledInObs);
    }

    [Fact]
    public void Без_авторизации_пароль_не_подставляется()
    {
        var path = WriteConfig(
            """{"server_enabled": true, "server_port": 4455, "auth_required": false, "server_password": "лишний"}""");

        Assert.Equal("", ObsSettings.Load(path).Password);
    }

    [Fact]
    public void Отсутствующий_конфиг_даёт_значения_по_умолчанию()
    {
        var settings = ObsSettings.Load(Path.Combine(_folder, "нет-такого.json"));

        Assert.Equal("127.0.0.1", settings.Host);
        Assert.Equal(ObsSettings.DefaultPort, settings.Port);
        Assert.Equal("", settings.Password);
    }

    [Fact]
    public void Испорченный_конфиг_не_роняет_программу()
    {
        var path = WriteConfig("{это не json");

        var settings = ObsSettings.Load(path);

        Assert.Equal(ObsSettings.DefaultPort, settings.Port);
    }

    [Fact]
    public void Дорожка_остаётся_ровно_одна()
    {
        var tracks = ObsService.TrackMap(2);

        Assert.True(tracks["2"]);
        Assert.Equal(1, tracks.Count(pair => pair.Value));
        Assert.Equal(6, tracks.Count);
    }

    [Fact]
    public void Папка_профиля_ищется_по_имени_внутри_basic_ini()
    {
        // Имя папки OBS чистит от спецсимволов, поэтому по имени профиля её не найти.
        var profiles = Path.Combine(_folder, "profiles");
        var folder = Path.Combine(profiles, "callsum_2");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "basic.ini"), "[General]\nName=callsum\n");

        Assert.Equal(folder, ObsSetup.FindProfileDirectory("callsum", profiles));
        Assert.Null(ObsSetup.FindProfileDirectory("другой", profiles));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
