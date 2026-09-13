using Callsum.Core;
using Callsum.Obs;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Callsum.App;

/// <summary>
/// Главное окно: одна кнопка на запись, ход обработки и журнал.
///
/// Запись ведёт OBS, обработку — ядро в отдельном процессе. Ответы обоих
/// приходят из чужих потоков, поэтому интерфейс трогается только через
/// очередь окна.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string TitleIdle = "callsum";
    private const string TitleRecording = "callsum — Идёт запись";

    private readonly DispatcherQueue _ui;
    private readonly ObsConnection _obs;
    private readonly DispatcherQueueTimer _clock;

    private EngineClient? _engine;
    private DateTimeOffset? _recordingSince;
    private bool _pending;
    private int _queued;

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
        _ = StartEngineAsync();
    }

    // --- ядро обработки ------------------------------------------------
    private async Task StartEngineAsync()
    {
        var executable = EngineLocator.Find();
        if (executable is null)
        {
            _ui.TryEnqueue(() => Append($"! {EngineLocator.NotFoundMessage}"));
            return;
        }

        var engine = new EngineClient(executable);
        engine.EventReceived += message => _ui.TryEnqueue(() => HandleEngineEvent(message));
        engine.Diagnostics += line => _ui.TryEnqueue(() => Append(line));
        engine.Stopped += () => _ui.TryEnqueue(() => Append("! Ядро обработки завершилось"));

        try
        {
            await engine.StartAsync().ConfigureAwait(false);
            _engine = engine;
        }
        catch (EngineException exception)
        {
            _ui.TryEnqueue(() => Append($"! {exception.Message}"));
        }
    }

    private void HandleEngineEvent(EngineEvent message)
    {
        // Без этого ошибка в обработке события пропадала бесследно: окно
        // переставало показывать ход работы, а причина оставалась неизвестной.
        try
        {
            OnEngineEvent(message);
        }
        catch (Exception exception)
        {
            Append($"! Ошибка в окне при показе события {message.GetType().Name}: {exception.Message}");
        }
    }

    private void OnEngineEvent(EngineEvent message)
    {
        switch (message)
        {
            case EngineEvent.Ready ready:
                Append($"Ядро готово ({ready.Device}/{ready.ComputeType})");
                break;

            case EngineEvent.Progress progress:
                ShowProgress(progress);
                break;

            case EngineEvent.Log log:
                Append(log.Text);
                break;

            case EngineEvent.Done done:
                _queued = Math.Max(0, _queued - 1);
                HideProgress(_queued > 0 ? "Готово, обрабатываю следующий" : "Готово");
                Append(done.HasSummary
                    ? $"Протокол готов: {done.OutDir}"
                    : $"Расшифровка готова: {done.OutDir}");
                break;

            case EngineEvent.Failed failed:
                _queued = Math.Max(0, _queued - 1);
                HideProgress("Ошибка обработки");
                Append($"! {failed.Message}");
                break;
        }
    }

    private void ShowProgress(EngineEvent.Progress progress)
    {
        Stage.Text = progress.Detail is { Length: > 0 } detail && progress.Stage == EngineStage.Transcribe
            ? $"{EngineStage.Describe(progress.Stage)}: {detail}"
            : EngineStage.Describe(progress.Stage);

        Progress.Visibility = Visibility.Visible;
        if (progress.Fraction is { } fraction)
        {
            Progress.IsIndeterminate = false;
            Progress.Value = fraction;
        }
        else
        {
            // Сколько ждать — неизвестно: пусть полоса бежит, а не стоит на нуле.
            Progress.IsIndeterminate = true;
        }
    }

    private void HideProgress(string stage)
    {
        Progress.Visibility = Visibility.Collapsed;
        Progress.IsIndeterminate = false;
        Stage.Text = stage;
    }

    // --- связь с OBS ---------------------------------------------------
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

    private void OnRecordStateChanged(bool active, string? path) => _ui.TryEnqueue(async () =>
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

        if (path is null)
        {
            Append("! OBS не сообщил путь к файлу — обработайте запись вручную");
            return;
        }

        Append($"Запись остановлена: {path}");
        await ProcessAsync(path);
    });

    private async Task ProcessAsync(string path)
    {
        if (_engine is null)
        {
            Append($"! Обработка пропущена: {EngineLocator.NotFoundMessage}");
            return;
        }

        try
        {
            await _engine.ProcessAsync(path).ConfigureAwait(true);
            _queued++;
        }
        catch (EngineException exception)
        {
            Append($"! {exception.Message}");
        }
    }

    // --- кнопка --------------------------------------------------------
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
        if (active)
        {
            Stage.Text = "Идёт запись";
        }
        else
        {
            Timer.Text = "00:00:00";
            if (Progress.Visibility == Visibility.Collapsed)
            {
                Stage.Text = "Готов к записи";
            }
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
