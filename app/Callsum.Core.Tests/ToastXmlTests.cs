using Callsum.Core;

namespace Callsum.Core.Tests;

public class ToastXmlTests
{
    [Fact]
    public void Уведомление_содержит_заголовок_и_текст()
    {
        var xml = ToastXml.Build("Протокол готов", "2026-09-13 09-49-28");

        Assert.Contains("<text>Протокол готов</text>", xml);
        Assert.Contains("<text>2026-09-13 09-49-28</text>", xml);
    }

    [Fact]
    public void Опасные_знаки_в_имени_записи_экранируются()
    {
        // Неверный XML не показывается вовсе — молча, без всякой ошибки.
        var xml = ToastXml.Build("Готово", "R&D <итоги>");

        Assert.Contains("R&amp;D &lt;итоги&gt;", xml);
        Assert.DoesNotContain("<итоги>", xml);
    }
}
