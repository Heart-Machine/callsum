using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Callsum.App;

/// <summary>
/// Показать окно, которое уже открыто.
///
/// Windows не даёт программе перехватывать фокус у той, с которой человек
/// работает прямо сейчас, — и правильно делает. Но здесь случай обратный:
/// фокус у второго экземпляра, потому что человек только что запустил его сам,
/// и окно он ждёт увидеть. Поэтому право выйти вперёд второй экземпляр отдаёт
/// явно, до того как попросит первый показаться.
/// </summary>
internal static class Foreground
{
    private const int Restore = 9;

    /// <summary>ASFW_ANY: вперёд может выйти любой процесс.</summary>
    private const uint AnyProcess = unchecked((uint)-1);

    /// <summary>
    /// Отдать право выводить окна на передний план.
    ///
    /// Зовётся до просьбы: после выхода отдавать будет уже некому, а без этого
    /// первый экземпляр смог бы только помигать кнопкой на панели задач.
    /// </summary>
    public static void Yield() => AllowSetForegroundWindow(AnyProcess);

    /// <summary>Развернуть окно и вынести его вперёд.</summary>
    public static void Raise(Window window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (IsIconic(handle))
        {
            // Activate свёрнутое окно не разворачивает — оно так и осталось бы
            // на панели задач, и человек решил бы, что запуск не сработал.
            ShowWindow(handle, Restore);
        }

        window.Activate();
        SetForegroundWindow(handle);
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
