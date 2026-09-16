using Callsum.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Callsum.App.Tests")]

namespace Callsum.App;

/// <summary>
/// Точка входа. Своя, а не сгенерированная из XAML, ради двух вещей, которые
/// должны случиться до окна.
///
/// Первая — Velopack. При установке, обновлении и удалении приложение
/// запускается со служебными ключами и должно отработать их, не показывая окна
/// и не подключаясь к OBS. Если бы окно успевало появиться, установка
/// заканчивалась бы мигающим окном поверх мастера.
///
/// Вторая — проверка, не открыто ли приложение уже. Ярлык у коллеги не один:
/// в меню «Пуск», на рабочем столе, да и сам исполняемый файл под рукой.
/// Запущенное дважды, оно поднимало два ядра, держало в видеопамяти две копии
/// модели и после остановки записи бралось обрабатывать один файл вдвоём.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        // Имя — по папке программы: установленная копия и собранная из
        // исходников остаются разными приложениями, а все ярлыки на одну
        // установку ведут в одну папку и потому считаются одним.
        var key = SingleInstance.KeyFor(AppContext.BaseDirectory);
        var instance = SingleInstance.Claim(key) ?? Elsewhere(key);
        if (instance is null)
        {
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(queue));
            _ = new App(instance);
        });
    }

    /// <summary>
    /// Место занято. Показать то приложение, которое уже открыто, и уйти —
    /// либо всё-таки занять место, если занявший его не отвечает.
    ///
    /// Молчание в ответ означает, что держатель уходит или завис. Так бывает
    /// после обновления: Velopack запускает новую версию, дождавшись ухода
    /// прежней, но если та задержится, новая молча вышла бы — и обновление
    /// не поднимало бы приложение обратно.
    /// </summary>
    private static SingleInstance? Elsewhere(string key)
    {
        Foreground.Yield();
        return SingleInstance.Signal(key)
            ? null
            : SingleInstance.Claim(key, TimeSpan.FromSeconds(10));
    }
}
