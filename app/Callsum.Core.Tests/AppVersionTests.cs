using Callsum.Core;

namespace Callsum.Core.Tests;

public class AppVersionTests
{
    [Fact]
    public void Предрелиз_виден_в_окне()
    {
        // Числовая версия суффикс не хранит: собранная из 1.1.0-rc.1 сборка
        // называлась бы «1.1.0» — тем же именем, что и то, что ушло коллегам.
        Assert.Equal("1.1.0-rc.1", AppVersion.Readable("1.1.0-rc.1", new Version(1, 1, 0, 0)));
    }

    [Fact]
    public void Хеш_коммита_человеку_не_показывается()
    {
        // Сборка дописывает его через «+», когда знает, из какого коммита собрана.
        Assert.Equal("1.1.0", AppVersion.Readable("1.1.0+9a3f21c", new Version(1, 1, 0, 0)));
        Assert.Equal(
            "1.1.0-rc.1", AppVersion.Readable("1.1.0-rc.1+9a3f21c", new Version(1, 1, 0, 0)));
    }

    [Fact]
    public void Без_информационной_версии_остаётся_числовая()
    {
        Assert.Equal("1.1.0", AppVersion.Readable(null, new Version(1, 1, 0, 0)));
        Assert.Equal("1.1.0", AppVersion.Readable("   ", new Version(1, 1, 0, 0)));
        Assert.Equal("", AppVersion.Readable(null, null));
    }

    [Fact]
    public void Версия_своей_сборки_читается()
    {
        var version = AppVersion.Current(typeof(AppVersion).Assembly);

        Assert.Matches(@"^\d+\.\d+\.\d+", version);
        Assert.DoesNotContain("+", version);
    }
}
