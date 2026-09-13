using Callsum.Core;

namespace Callsum.Core.Tests;

public class CallResultsTests : IDisposable
{
    // Тесты не трогают настоящие папки пользователя: всё живёт во временной.
    private readonly string _root = Directory.CreateTempSubdirectory("callsum-results").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(
        string name,
        bool transcript = true,
        bool summary = false,
        DateTime? when = null,
        string? source = "запись.mkv")
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        if (transcript)
        {
            var path = Path.Combine(folder, CallResults.TranscriptName);
            var header = source is null ? "" : $"\n- Файл: {source}\n- Длительность: 00:10:00\n";
            File.WriteAllText(path, $"# Расшифровка: {name}\n{header}\n---\n\nразговор");
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
    public void Результат_помнит_из_какой_записи_он_сделан()
    {
        // Чтобы обработать созвон заново, ядру нужен путь к записи. Имя папки
        // для этого не годится: она собирается по настраиваемому шаблону.
        Folder("вчерашний созвон", source: "2026-09-13 11-33-22.mkv");

        var result = Assert.Single(CallResults.Scan(_root));

        Assert.Equal("2026-09-13 11-33-22.mkv", result.SourceName);
    }

    [Fact]
    public void Расшифровка_без_имени_записи_не_ломает_список()
    {
        // Старые расшифровки заголовка могли не иметь — запись всё равно
        // показывается, просто обработать её заново не выйдет.
        Folder("без заголовка", source: null);

        Assert.Null(Assert.Single(CallResults.Scan(_root)).SourceName);
    }

    [Fact]
    public void Имя_записи_ищется_только_в_заголовке()
    {
        // Ниже разделителя начинается сам разговор: там «- Файл:» может
        // оказаться просто фразой собеседника, и верить ей нельзя.
        var folder = Path.Combine(_root, "разговор про файлы");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, CallResults.TranscriptName),
            "# Расшифровка\n\n---\n\n[00:01] Я: - Файл: договор.docx посмотри\n");

        Assert.Null(CallResults.Read(folder)?.SourceName);
    }

    [Fact]
    public void Запись_ищется_по_запомненному_пути()
    {
        // Точный путь пишется в машинный разбор при обработке — он и главный.
        var recording = Path.Combine(_root, "созвон.mkv");
        File.WriteAllText(recording, "");
        var folder = Folder("результат");
        File.WriteAllText(
            Path.Combine(folder, CallResults.DataName),
            $$"""{"source": {{System.Text.Json.JsonSerializer.Serialize(recording)}}, "segments": []}""");

        var result = Assert.IsType<CallResult>(CallResults.Read(folder));

        Assert.Equal(recording, result.SourcePath);
        Assert.Equal(recording, result.FindSource(recordingsFolder: null));
    }

    [Fact]
    public void Переехавшая_запись_ищется_по_имени_в_папке_записей()
    {
        // Путь из разбора может устареть: папку переименовали, диск сменился.
        var recordings = Directory.CreateDirectory(Path.Combine(_root, "записи")).FullName;
        File.WriteAllText(Path.Combine(recordings, "созвон.mkv"), "");
        var folder = Folder("результат", source: "созвон.mkv");
        File.WriteAllText(
            Path.Combine(folder, CallResults.DataName),
            """{"source": "Z:\\которого\\нет\\созвон.mkv", "segments": []}""");

        var result = Assert.IsType<CallResult>(CallResults.Read(folder));

        Assert.Equal(Path.Combine(recordings, "созвон.mkv"), result.FindSource(recordings));
    }

    [Fact]
    public void Пропавшая_запись_честно_не_находится()
    {
        // Окно должно сказать об этом, а не молча ничего не делать.
        var folder = Folder("результат", source: "потерянный созвон.mkv");

        Assert.Null(CallResults.Read(folder)?.FindSource(_root));
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
