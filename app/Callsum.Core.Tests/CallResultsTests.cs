using Callsum.Core;

namespace Callsum.Core.Tests;

public class CallResultsTests : IDisposable
{
    // Тесты не трогают настоящие папки пользователя: всё живёт во временной.
    private readonly string _root = Directory.CreateTempSubdirectory("callsum-results").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(string name, bool transcript = true, bool summary = false, DateTime? when = null)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        if (transcript)
        {
            var path = Path.Combine(folder, CallResults.TranscriptName);
            File.WriteAllText(path, "# Расшифровка");
            if (when is { } moment)
            {
                File.SetLastWriteTime(path, moment);
            }
        }

        if (summary)
        {
            File.WriteAllText(Path.Combine(folder, CallResults.SummaryName), "# Протокол");
        }

        return folder;
    }

    [Fact]
    public void Свежие_записи_идут_первыми()
    {
        Folder("позавчера", when: new DateTime(2026, 9, 11, 10, 0, 0));
        Folder("вчера", when: new DateTime(2026, 9, 12, 10, 0, 0));
        Folder("сегодня", when: new DateTime(2026, 9, 13, 10, 0, 0));

        var names = CallResults.Scan(_root).Select(result => result.Name);

        Assert.Equal(["сегодня", "вчера", "позавчера"], names);
    }

    [Fact]
    public void Папка_без_расшифровки_не_попадает_в_список()
    {
        // Так выглядит работа, оборванная на середине: открывать там нечего.
        Folder("готовая");
        Folder("брошенная", transcript: false);

        Assert.Equal("готовая", Assert.Single(CallResults.Scan(_root)).Name);
    }

    [Fact]
    public void Готовность_протокола_видна_в_списке()
    {
        Folder("с протоколом", summary: true);
        Folder("без протокола");

        var results = CallResults.Scan(_root).ToDictionary(result => result.Name);

        Assert.True(results["с протоколом"].HasSummary);
        Assert.EndsWith(CallResults.SummaryName, results["с протоколом"].MainDocument);
        Assert.False(results["без протокола"].HasSummary);
        Assert.Null(results["без протокола"].SummaryPath);
        // Без протокола открывать нужно расшифровку, а не несуществующий файл.
        Assert.EndsWith(CallResults.TranscriptName, results["без протокола"].MainDocument);
    }

    [Fact]
    public void Длинный_список_обрезается()
    {
        for (var index = 0; index < 5; index++)
        {
            Folder($"запись {index}", when: new DateTime(2026, 9, 13, 10, index, 0));
        }

        var results = CallResults.Scan(_root, limit: 2);

        Assert.Equal(["запись 4", "запись 3"], results.Select(result => result.Name));
    }

    [Fact]
    public void Несуществующая_папка_результатов_не_ошибка()
    {
        // Первый запуск у коллеги: обрабатывать ещё нечего, окно должно открыться.
        Assert.Empty(CallResults.Scan(Path.Combine(_root, "ещё-нет")));
        Assert.Empty(CallResults.Scan(""));
    }

    [Fact]
    public void Одну_папку_можно_прочитать_отдельно()
    {
        // По событию done окно узнаёт путь и добавляет запись, не перечитывая всё.
        var folder = Folder("свежая", summary: true);

        var result = Assert.IsType<CallResult>(CallResults.Read(folder));

        Assert.Equal("свежая", result.Name);
        Assert.True(result.HasSummary);
        Assert.Null(CallResults.Read(Path.Combine(_root, "нет такой")));
    }
}
