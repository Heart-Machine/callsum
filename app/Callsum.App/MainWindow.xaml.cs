using System.Collections.ObjectModel;
using Callsum.Core;
using Callsum.Obs;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Callsum.App;

/// <summary>
/// Главное окно: одна кнопка на запись, ход обработки, список готовых записей
/// и журнал.
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
    private readonly Notifications _notifications;
    private readonly ObservableCollection<ResultRow> _results = [];

    private EngineClient? _engine;
    private DateTimeOffset? _recordingSince;
    private string? _outFolder;
    private string? _markdownApp;
    private bool _pending;
    private int _queued;

    public MainWindow()
    {
        InitializeComponent();
        Title = TitleIdle;

        _ui = DispatcherQueue.GetForCurrentThread();
        Results.ItemsSource = _results;

        _notifications = new Notifications(Append);
        _notifications.Register();

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
            // Где лежат записи и результаты, знает ядро — пути записаны в его
            // настройках. Проверка окружения заодно приносит их, и окно узнаёт,
            // где искать готовые записи.
            await engine.DoctorAsync().ConfigureAwait(false);
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

            case EngineEvent.Doctor doctor:
                ApplyFolders(doctor);
                break;

            case EngineEvent.Done done:
                _queued = Math.Max(0, _queued - 1);
                HideProgress(_queued > 0 ? "Готово, обрабатываю следующий" : "Готово");
                Append(done.HasSummary
                    ? $"Протокол готов: {done.OutDir}"
                    : $"Расшифровка готова: {done.OutDir}");
                AddResult(done);
                break;

            case EngineEvent.Failed failed:
                _queued = Math.Max(0, _queued - 1);
                HideProgress("Ошибка обработки");
                Append($"! {failed.Message}");
                _notifications.Show("Запись не обработана", failed.Message);
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

    // --- список записей ------------------------------------------------
    private void ApplyFolders(EngineEvent.Doctor doctor)
    {
        _outFolder = doctor.OutFolder;
        _markdownApp = doctor.MarkdownApp;
        OpenOutFolder.IsEnabled = !string.IsNullOrWhiteSpace(_outFolder);
        _ = ReloadResultsAsync();
    }

    private async Task ReloadResultsAsync()
    {
        if (_outFolder is not { Length: > 0 } folder)
        {
            return;
        }

        // Перебор папок — работа с диском: на потоке окна он подмораживал бы
        // кнопку записи, а она должна нажиматься всегда.
        var found = await Task.Run(() => CallResults.Scan(folder));

        _results.Clear();
        foreach (var result in found)
        {
            _results.Add(new ResultRow(result));
        }

        UpdateResultsHint();
    }

    private void AddResult(EngineEvent.Done done)
    {
        var result = CallResults.Read(done.OutDir);
        if (result is null)
        {
            return;
        }

        // Повторная обработка той же записи не должна раздваивать строку:
        // прежняя убирается, новая встаёт сверху.
        for (var index = _results.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_results[index].Folder, result.Folder, StringComparison.OrdinalIgnoreCase))
            {
                _results.RemoveAt(index);
            }
        }

        _results.Insert(0, new ResultRow(result));
        UpdateResultsHint();

        _notifications.Show(result.HasSummary ? "Протокол готов" : "Расшифровка готова", result.Name);
    }

    private void UpdateResultsHint() =>
        ResultsHint.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnResultClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is ResultRow row)
        {
            // Открывается протокол, а если его нет — расшифровка: нажатие,
            // которое ничего не делает, выглядит поломкой.
            Report(Shell.OpenFile(row.Result.MainDocument, _markdownApp));
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: string folder })
        {
            Report(Shell.OpenFolder(folder));
        }
    }

    private void OnOpenOutFolderClick(object sender, RoutedEventArgs args)
    {
        if (_outFolder is { Length: > 0 } folder)
        {
            Report(Shell.OpenFolder(folder));
        }
    }

    private void Report(string? error)
    {
        if (error is not null)
        {
            Append($"! {error}");
        }
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
