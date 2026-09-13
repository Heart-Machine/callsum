using Callsum.Core;

namespace Callsum.Core.Tests;

public class EngineLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "callsum-core-" + Guid.NewGuid().ToString("N"));

    private string Create(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Ядро_рядом_с_приложением_находится_первым()
    {
        // Так оно и лежит после установки у коллеги.
        var expected = Create("app", EngineLocator.ExecutableName);
        Create("dist", "callsum-core", EngineLocator.ExecutableName);

        Assert.Equal(expected, EngineLocator.Find(Path.Combine(_root, "app")));
    }

    [Fact]
    public void Ядро_из_папки_сборки_находится_при_разработке()
    {
        // При разработке приложение запускается из bin, а ядро собрано в dist
        // в корне проекта — поиск поднимается по папкам вверх.
        var expected = Create("dist", "callsum-core", EngineLocator.ExecutableName);
        var runningFrom = Path.Combine(_root, "app", "bin", "Debug", "net9.0");
        Directory.CreateDirectory(runningFrom);

        Assert.Equal(expected, EngineLocator.Find(runningFrom));
    }

    [Fact]
    public void Когда_ядра_нет_возвращается_пусто_и_есть_что_сказать()
    {
        Directory.CreateDirectory(_root);

        Assert.Null(EngineLocator.Find(_root));
        Assert.Contains("build-core", EngineLocator.NotFoundMessage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
