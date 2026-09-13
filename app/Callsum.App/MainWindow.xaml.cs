using Callsum.Obs;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Callsum.App;

/// <summary>
/// Главное окно: одна кнопка на запись, состояние связи с OBS и журнал.
///
/// Запись ведёт OBS, приложение им управляет. Все ответы OBS приходят из чужого
/// потока, поэтому интерфейс трогается только через очередь окна.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string TitleIdle = "callsum";
    private const string TitleRecording = "callsum — Идёт запись";

    private readonly DispatcherQueue _ui;
    private readonly ObsConnection _obs;
    private readonly DispatcherQueueTimer _clock;

    private DateTimeOffset? _recordingSince;
    private bool _pending;

    public MainWindow()
    {
        InitializeComponent();
        Title = TitleIdle;

        _ui = DispatcherQueue.GetForCurrentThread();
        _obs = new ObsConnection(ObsSettings.Load());
        _obs.StateChanged += OnConnectionChanged;
        _obs.RecordStateChanged += OnRecordStateChanged;
        _obs.Log += message => _ui.TryEnqueue(() => Append(message));

        _clock = _ui.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => UpdateTimer();
        _clock.Start();

        _obs.Start();
    }

    private void OnConnectionChanged(bool connected, string description) => _ui.TryEnqueue(() =>
    {
        ObsStatus.Text = connected ? $"OBS: подключён ({description})" : $"OBS: {description}";
        RecordButton.IsEnabled = connected && !_pending;
        if (!connected)
        {
            // Кто сейчас ведёт запись, приложение не знает — таймер врал бы.
            _recordingSince = null;
            ApplyRecordingState(false);
            Stage.Text = "Жду OBS";
        }
    });

    private void OnRecordStateChanged(bool active, string? path) => _ui.TryEnqueue(() =>
    {
        _pending = false;
        RecordButton.IsEnabled = _obs.Connected;
        _recordingSince = active ? DateTimeOffset.Now : null;
        ApplyRecordingState(active);

        if (active)
        {
            Append("Запись начата");
            return;
        }

        Append(path is null
            ? "! OBS не сообщил путь к файлу — обработайте запись вручную"
            : $"Запись остановлена: {path}");
    });

    private async void OnRecordClick(object sender, RoutedEventArgs args)
    {
        // Команда OBS выполняется не мгновенно: он переключает профиль и
        // закрывает файл. Пока это идёт, кнопка занята, иначе по ней успевают
        // нажать несколько раз.
        if (_pending)
        {
            return;
        }

        var starting = _recordingSince is null;
        _pending = true;
        RecordButton.IsEnabled = false;
        RecordButton.Content = starting ? "Запускаю…" : "Останавливаю…";
        Stage.Text = RecordButton.Content.ToString();

        var error = starting ? await _obs.StartRecordingAsync() : await _obs.StopRecordingAsync();
        if (error is not null)
        {
            _pending = false;
            RecordButton.IsEnabled = _obs.Connected;
            ApplyRecordingState(_recordingSince is not null);
            Append($"! {error}");
            return;
        }

        // Подтверждение придёт событием от OBS; если оно почему-то не придёт,
        // кнопку нужно вернуть в рабочее состояние, а не оставлять мёртвой.
        ReleaseButtonLater();
    }

    private void ReleaseButtonLater()
    {
        var timer = _ui.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(15);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (!_pending)
            {
                return;
            }

            _pending = false;
            RecordButton.IsEnabled = _obs.Connected;
            ApplyRecordingState(_recordingSince is not null);
        };
        timer.Start();
    }

    private void ApplyRecordingState(bool active)
    {
        RecordButton.Content = active ? "■ Остановить запись" : "● Начать запись";
        // Заголовок показывает состояние: видно и по подписи в панели задач.
        Title = active ? TitleRecording : TitleIdle;
        Stage.Text = active ? "Идёт запись" : "Готов к записи";
        if (!active)
        {
            Timer.Text = "00:00:00";
        }
    }

    private void UpdateTimer()
    {
        if (_recordingSince is { } since)
        {
            Timer.Text = (DateTimeOffset.Now - since).ToString(@"hh\:mm\:ss");
        }
    }

    private void Append(string message)
    {
        Log.Text = string.IsNullOrEmpty(Log.Text) ? message : $"{Log.Text}\n{message}";
    }
}
