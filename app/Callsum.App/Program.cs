using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Callsum.App;

/// <summary>
/// Точка входа. Своя, а не сгенерированная из XAML, ради одной строки:
/// Velopack должен получить управление первым.
///
/// При установке, обновлении и удалении приложение запускается со служебными
/// ключами и должно отработать их, не показывая окна и не подключаясь к OBS.
/// Если бы окно успевало появиться, установка заканчивалась бы мигающим окном
/// поверх мастера, а обновление — второй копией приложения.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(queue));
            _ = new App();
        });
    }
}
