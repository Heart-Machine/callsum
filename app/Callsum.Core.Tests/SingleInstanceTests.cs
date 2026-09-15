using Callsum.Core;

namespace Callsum.Core.Tests;

public class SingleInstanceTests
{
    /// <summary>Своё имя на каждый прогон: тесты не должны мешать друг другу.</summary>
    private static string FreshKey() => "callsum-test-" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public void Разные_папки_программы_дают_разные_имена()
    {
        // Установленная копия и собранная из исходников — разные приложения:
        // иначе, чтобы проверить новую сборку, пришлось бы закрывать рабочую.
        var installed = SingleInstance.KeyFor(@"C:\Users\Кто-то\AppData\Local\callsum\current");
        var built = SingleInstance.KeyFor(@"D:\projects\callsum\app\bin\Debug");

        Assert.NotEqual(installed, built);
    }

    [Fact]
    public void Одна_папка_даёт_одно_имя_как_её_ни_запиши()
    {
        // Два ярлыка на одну установку — это и есть тот случай, ради которого
        // всё затевалось: путь у них один, пусть и записанный по-разному.
        var plain = SingleInstance.KeyFor(@"C:\callsum\current");

        Assert.Equal(plain, SingleInstance.KeyFor(@"C:\CALLSUM\Current\"));
        Assert.Equal(plain, SingleInstance.KeyFor(@"C:\callsum\other\..\current"));
    }

    [Fact]
    public void Имя_годится_для_мьютекса_и_канала()
    {
        // В именах мьютекса и канала нет места разделителям пути.
        var key = SingleInstance.KeyFor(@"C:\Program Files\callsum (x86)\current");

        Assert.DoesNotContain('\\', key);
        Assert.DoesNotContain('/', key);
        Assert.All(key, symbol => Assert.True(char.IsAsciiLetterOrDigit(symbol) || symbol == '-'));
    }

    [Fact]
    public void Место_занимает_только_первый()
    {
        var key = FreshKey();
        using var first = SingleInstance.Claim(key);

        Assert.NotNull(first);
        // Со стороны, а не отсюда же: мьютекс Windows пускает своего владельца
        // повторно, и проверка из занявшего потока ничего бы не проверила.
        Assert.Null(FromAnotherThread(() => SingleInstance.Claim(key)));
    }

    /// <summary>Сделать то же самое чужими руками — как это делает второй запуск.</summary>
    private static SingleInstance? FromAnotherThread(Func<SingleInstance?> attempt)
    {
        SingleInstance? taken = null;
        var thread = new Thread(() => taken = attempt());
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "попытка занять место зависла");
        return taken;
    }

    [Fact]
    public void Освобождённое_место_занимает_следующий()
    {
        // Приложение закрыли — второй запуск должен работать как обычный первый.
        var key = FreshKey();
        SingleInstance.Claim(key)!.Dispose();

        using var next = SingleInstance.Claim(key);

        Assert.NotNull(next);
    }

    [Fact]
    public void Просьба_показаться_доходит_до_первого()
    {
        var key = FreshKey();
        using var first = SingleInstance.Claim(key)!;
        var asked = new ManualResetEventSlim();
        first.Listen(asked.Set);

        var answered = SingleInstance.Signal(key, TimeSpan.FromSeconds(10));

        Assert.True(answered, "первый обязан ответить, что показался");
        Assert.True(asked.Wait(TimeSpan.FromSeconds(5)), "окно должны были попросить показаться");
    }

    [Fact]
    public void Второй_раз_просьба_проходит_так_же()
    {
        // Канал переоткрывается на каждую просьбу: запускать ярлык дважды —
        // обычное дело, и второй раз не должен упираться в тишину.
        var key = FreshKey();
        using var first = SingleInstance.Claim(key)!;
        var times = 0;
        first.Listen(() => Interlocked.Increment(ref times));

        Assert.True(SingleInstance.Signal(key, TimeSpan.FromSeconds(10)));
        Assert.True(SingleInstance.Signal(key, TimeSpan.FromSeconds(10)));
        Assert.Equal(2, times);
    }

    [Fact]
    public void Молчание_в_ответ_не_выдаётся_за_согласие()
    {
        // Место занято, а канала нет: так выглядит уходящий после обновления
        // процесс. Второй экземпляр должен понять это и занять место сам,
        // иначе обновление не поднимало бы приложение обратно.
        var key = FreshKey();
        using var silent = SingleInstance.Claim(key);

        Assert.False(SingleInstance.Signal(key, TimeSpan.FromMilliseconds(700)));
    }
}
