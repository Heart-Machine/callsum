using Callsum.Obs;

namespace Callsum.Obs.Tests;

public class ObsSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "callsum-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Настройки_подключения_имеют_значения_по_умолчанию()
    {
        var settings = new ObsSettings();

        Assert.Equal("127.0.0.1", settings.Host);
        Assert.Equal(ObsSettings.DefaultPort, settings.Port);
        Assert.Equal("", settings.Password);
        Assert.True(settings.EnabledInObs);
    }

    [Fact]
    public void Настройки_подключения_принимают_явные_значения()
    {
        var settings = new ObsSettings { Host = "localhost", Port = 4466, Password = "тайна" };

        Assert.Equal("localhost", settings.Host);
        Assert.Equal(4466, settings.Port);
        Assert.Equal("тайна", settings.Password);
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
