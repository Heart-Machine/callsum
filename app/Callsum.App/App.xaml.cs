using Callsum.Core;
using Microsoft.UI.Xaml;

namespace Callsum.App;

public partial class App : Application
{
    private readonly SingleInstance? _instance;

    private Window? _window;
    private WelcomeWindow? _welcome;
    private bool _mainShown;

    /// <param name="instance">
    /// Занятое место одного приложения. Через него приходят просьбы показаться
    /// от тех запусков, которые не стали поднимать своё окно.
    /// </param>
    public App(SingleInstance? instance = null)
    {
        _instance = instance;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (WelcomePreferences.ForCurrentUser().ShouldShow())
        {
            ShowInitialWelcome();
            return;
        }

        ShowMainWindow();
    }

    private void ShowInitialWelcome()
    {
        var window = new WelcomeWindow(ShowMainWindow, saveChoice: true);
        _welcome = window;
        _window = window;
        window.Activate();

        // Закрытие первого окна без «Продолжить» — это выход, а не сворачивание
        // приложения в невидимое состояние с занятым единственным экземпляром.
        window.Closed += (_, _) =>
        {
            _welcome = null;
            if (!_mainShown)
            {
                _instance?.Dispose();
            }
        };
    }

    /// <summary>Открыть диагностику из главного окна, не меняя выбор первого запуска.</summary>
    public void ShowWelcome()
    {
        if (_welcome is not null)
        {
            _welcome.Activate();
            return;
        }

        var window = new WelcomeWindow(() => { }, saveChoice: false);
        _welcome = window;
        window.Closed += (_, _) => _welcome = null;
        window.Activate();
    }

    private void ShowMainWindow()
    {
        _mainShown = true;
        var window = new MainWindow();
        _window = window;
        window.Activate();

        // Просьба приходит из чужого потока, а окно живёт в своём — поэтому
        // работа перекладывается в очередь окна, а не делается на месте.
        var queue = window.DispatcherQueue;
        _instance?.Listen(() => queue.TryEnqueue(() => Foreground.Raise(window)));

        // Отпускаем место сами, а не оставляем это операционной системе:
        // брошенный мьютекс следующий запуск подберёт, но лишние полсекунды
        // на разбирательство потратит.
        window.Closed += (_, _) => _instance?.Dispose();
    }
}
