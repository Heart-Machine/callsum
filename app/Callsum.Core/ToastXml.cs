using System.Security;

namespace Callsum.Core;

/// <summary>Разметка всплывающего уведомления Windows.</summary>
public static class ToastXml
{
    /// <summary>
    /// Уведомление из заголовка и строки текста.
    ///
    /// Текст обязательно экранируется: в имени записи легко встречается
    /// амперсанд или угловая скобка, а от неверного XML уведомление не
    /// показывается вовсе — молча, без ошибки.
    /// </summary>
    public static string Build(string title, string message) =>
        "<toast><visual><binding template=\"ToastGeneric\">"
        + $"<text>{Escape(title)}</text><text>{Escape(message)}</text>"
        + "</binding></visual></toast>";

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";
}
