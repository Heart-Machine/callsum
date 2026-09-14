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
    private readonly Updates _updates;
    private readonly ObservableCollection<ResultRow> _results = [];

    /// <summary>Задания, которые запускало это окно: ответы на них — его дело.</summary>
    private readonly HashSet<string> _mine = [];

    private EngineClient? _engine;
    private SettingsWindow? _settings;
    private DateTimeOffset? _recordingSince;
    private string? _outFolder;
    private string? _markdownApp;
    private string? _recordingsFolder;
    private string? _recordFolderNote;
    private bool _pending;
    private bool _obsSetupOffered;
    private bool _warned;
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

        _updates = new Updates(message => _ui.TryEnqueue(() => Append(message)));

        _obs.Start();
        _ = StartEngineAsync();
        _ = CheckUpdatesAsync();
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
        // Ядро отвечает и на вопросы других окон: окно настроек спрашивает
        // у него список моделей Ollama, вкладка «О программе» — окружение.
        // Их отказ — не «ошибка обработки»: подпись стадии относится к записи,
        // а показать причину должен тот, кто спрашивал.
        if (message is EngineEvent.Done or EngineEvent.Failed
            && message.Id is { Length: > 0 } id && !_mine.Remove(id))
        {
            if (message is EngineEvent.Failed other)
            {
                Append($"! {other.Message}");
            }

            return;
        }

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
                // Проверка окружения — первое, что окно спрашивает у ядра:
                // здесь и выясняется, что OBS пишет не туда, где ищет программа.
                ApplyFolders(doctor.OutFolder, doctor.RecordingsFolder, doctor.MarkdownApp);
                ShowWarnings(doctor);
                _ = SyncRecordFolderAsync();
                break;

            // Настройки могли поменять папки прямо сейчас — в окне настроек.
            // Папку записи в OBS задаёт оно же и само о ней отчитывается.
            case EngineEvent.Settings settings:
                ApplyFolders(
                    settings.ResolvedPath("out"),
                    settings.ResolvedPath("recordings"),
                    settings.Text("view", "markdown_app"));
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
        // Подробность уточняет стадию там, где она о чём-то говорит: кого
        // распознаём сейчас и сколько мегабайт уже скачано.
        var detailed = progress.Stage is EngineStage.Transcribe or EngineStage.Download or EngineStage.Model;
        Stage.Text = detailed && progress.Detail is { Length: > 0 } detail
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
    private void ApplyFolders(string? outFolder, string? recordings, string? markdownApp)
    {
        var moved = !string.Equals(_outFolder, outFolder, StringComparison.OrdinalIgnoreCase);
        _outFolder = string.IsNullOrWhiteSpace(outFolder) ? _outFolder : outFolder;
        _recordingsFolder = string.IsNullOrWhiteSpace(recordings) ? _recordingsFolder : recordings;
        _markdownApp = markdownApp;
        OpenOutFolder.IsEnabled = !string.IsNullOrWhiteSpace(_outFolder);

        if (moved)
        {
            _ = ReloadResultsAsync();
        }
    }

    /// <summary>
    /// Проследить, чтобы OBS писал записи туда, где их ищет ядро.
    ///
    /// Папку записи хранит профиль OBS, а папку для поиска — настройки callsum.
    /// Разъехавшись, они дают самое неприятное: в настройках одно, на диске
    /// другое, и человек ищет запись там, где её нет.
    /// </summary>
    private async Task SyncRecordFolderAsync()
    {
        if (_recordingsFolder is not { Length: > 0 } folder || !_obs.Connected)
        {
            return;
        }

        var result = await _obs.EnsureRecordFolderAsync(folder);
        if (result.Changed)
        {
            Append($"OBS теперь пишет записи в {folder}");
        }
        else if (result.Note is { Length: > 0 } note && note != _recordFolderNote)
        {
            // Одно и то же замечание на каждое переподключение — шум в журнале.
            _recordFolderNote = note;
            Append($"! Папка записи: {note}");
        }
    }

    // --- окружение -------------------------------------------------------
    /// <summary>
    /// Что помешает работе — плашками над кнопкой записи.
    ///
    /// Ядро проверяет окружение при запуске, но раньше его ответ читала только
    /// вкладка «О программе»: про молчащую Ollama человек узнавал через час,
    /// когда протокол не собрался, а про отсутствующий FFmpeg — когда пропала
    /// первая запись.
    /// </summary>
    private void ShowWarnings(EngineEvent.Doctor doctor)
    {
        Warnings.Children.Clear();
        var found = EnvironmentCheck.Read(doctor);
        foreach (var warning in found)
        {
            Warnings.Children.Add(new InfoBar
            {
                IsOpen = true,
                // Закрыть плашку можно было бы не читая, а причина осталась бы.
                IsClosable = false,
                Severity = warning.Level == WarningLevel.Problem
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Informational,
                Title = warning.Title,
                Message = warning.What,
            });
        }

        if (found.Count == 0)
        {
            // Отвечаем только тому, кто спрашивал: при запуске тишина и так
            // означает, что всё в порядке.
            if (_warned)
            {
                Append("Окружение в порядке");
            }

            _warned = false;
            return;
        }

        _warned = true;

        // Ollama запускают, модель докачивают — и хочется убедиться, что
        // помогло, не перезапуская программу.
        var again = new HyperlinkButton { Content = "Проверить ещё раз", Padding = new Thickness(4, 0, 4, 0) };
        again.Click += OnRecheckClick;
        Warnings.Children.Add(again);
    }

    private async void OnRecheckClick(object sender, RoutedEventArgs args)
    {
        if (_engine is null)
        {
            Append($"! {EngineLocator.NotFoundMessage}");
            return;
        }

        Append("Проверяю окружение…");
        try
        {
            await _engine.DoctorAsync();
        }
        catch (EngineException exception)
        {
            Append($"! {exception.Message}");
        }
    }

    // --- настройка OBS --------------------------------------------------
    /// <summary>
    /// Готов ли OBS писать созвоны.
    ///
    /// Без профиля и коллекции сцен callsum запись идёт с чужими настройками:
    /// одной дорожкой, в другом формате и не в ту папку. Раньше это выяснялось
    /// после созвона, когда записывать заново уже нечего, — поэтому окно
    /// спрашивает сразу, как только OBS подключился.
    /// </summary>
    private async Task CheckObsSetupAsync()
    {
        var state = await _obs.GetSetupStateAsync();
        _ui.TryEnqueue(() => ShowObsSetup(state));
    }

    private void ShowObsSetup(ObsService.SetupState? state)
    {
        if (state is null)
        {
            return;
        }

        SetUpObs.Visibility = state.Ready ? Visibility.Collapsed : Visibility.Visible;
        if (state.Ready)
        {
            _obsSetupOffered = false;
            return;
        }

        // Одно и то же предложение на каждое переподключение — шум в журнале.
        if (_obsSetupOffered)
        {
            return;
        }

        _obsSetupOffered = true;
        var missing = (state.Profile, state.Collection) switch
        {
            (false, false) => "профиля и коллекции сцен",
            (true, false) => "коллекции сцен",
            _ => "профиля",
        };
        Append($"В OBS нет {missing} callsum — запись пойдёт с чужими настройками. "
               + "Нажмите «Настроить OBS»: программа заведёт их сама, ваши настройки останутся на месте.");
    }

    private async void OnSetUpObsClick(object sender, RoutedEventArgs args)
    {
        if (_recordingSince is not null)
        {
            // Настройка перечитывает профиль OBS — посреди записи это её оборвёт.
            Append("! Сейчас идёт запись — настрою OBS, когда она закончится");
            return;
        }

        if (_engine is null)
        {
            Append($"! Куда писать записи, знают настройки, а их читает ядро: {EngineLocator.NotFoundMessage}");
            return;
        }

        SetUpObs.IsEnabled = false;
        Append("Настраиваю OBS — это занимает около полминуты…");
        try
        {
            // Папку и имя файла берём из настроек, а не из своих представлений:
            // иначе OBS начал бы писать не туда, где программа ищет записи.
            var settings = await _engine.GetSettingsAsync();
            var folder = settings.ResolvedPath("recordings");
            var format = settings.Text("obs", "filename_format");

            if (await _obs.SetUpAsync(folder, format) is { Length: > 0 } error)
            {
                Append($"! Не вышло настроить OBS: {error}");
                return;
            }

            Append($"OBS настроен: записи пойдут в {folder}");
            _notifications.Show("OBS настроен", "Можно записывать созвон");
        }
        catch (Exception exception) when (exception is EngineException or TimeoutException)
        {
            Append($"! {exception.Message}");
        }
        finally
        {
            SetUpObs.IsEnabled = true;
            _ = CheckObsSetupAsync();
        }
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

    // --- обновления ----------------------------------------------------
    private async Task CheckUpdatesAsync()
    {
        var version = await _updates.FetchAsync();
        if (version is null)
        {
            return;
        }

        _ui.TryEnqueue(() =>
        {
            UpdateReady.Content = $"Обновить до {version}";
            UpdateReady.Visibility = Visibility.Visible;
            Append($"Скачано обновление {version}. Нажмите «Обновить» — приложение перезапустится.");
        });
    }

    private void OnUpdateClick(object sender, RoutedEventArgs args)
    {
        if (_recordingSince is not null)
        {
            // Перезапуск посреди записи оборвал бы созвон.
            Append("! Сейчас идёт запись — обновлю, когда она закончится");
            return;
        }

        Append("Обновляюсь и перезапускаюсь…");
        _updates.Apply();
    }

    // --- настройки -----------------------------------------------------
    private void OnSettingsClick(object sender, RoutedEventArgs args)
    {
        if (_engine is null)
        {
            Append($"! Настройки читает ядро обработки: {EngineLocator.NotFoundMessage}");
            return;
        }

        // Второе окно настроек показывало бы те же значения дважды, и сохранение
        // из одного затирало бы то, что набрано в другом.
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        // О сохранении в журнал пишет само ядро — второй раз повторять незачем,
        // а папки окно обновит по событию с настройками.
        var window = new SettingsWindow(_engine, _obs, _updates, () => _recordingSince is not null);
        window.Closed += (_, _) => _settings = null;
        _settings = window;
        window.Activate();
    }

    private async void OnReprocessClick(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: ResultRow row })
        {
            return;
        }

        // Ядру нужен путь к самой записи: результат оно собирает заново с нуля.
        var path = row.Result.FindSource(_recordingsFolder);
        if (path is null)
        {
            Append($"! Не нашёл исходную запись для «{row.Name}»"
                   + $" — её имя {row.Result.SourceName ?? "неизвестно"},"
                   + $" искал в {_recordingsFolder ?? "папке записей"}");
            return;
        }

        Append($"Обрабатываю заново: {Path.GetFileName(path)}");
        await ProcessAsync(path, force: true);
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
        if (connected)
        {
            // OBS мог запуститься позже приложения — папку записи он должен
            // получить и в этом случае, а не только при чтении настроек.
            _ = SyncRecordFolderAsync();
            _ = CheckObsSetupAsync();
        }

        if (!connected)
        {
            // Кто сейчас ведёт запись, приложение не знает — таймер врал бы.
            _recordingSince = null;
            ApplyRecordingState(false);

            // Пока идёт обработка, подпись занята делом: при закрытом OBS
            // попытки подключиться повторяются каждые три секунды, и ход
            // работы то и дело сменялся на «Жду OBS».
            if (_queued == 0)
            {
                Stage.Text = "Жду OBS";
            }
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

    private async Task ProcessAsync(string path, bool force = false)
    {
        if (_engine is null)
        {
            Append($"! Обработка пропущена: {EngineLocator.NotFoundMessage}");
            return;
        }

        try
        {
            // Номер задания запоминается: по нему потом видно, что отказ ядра
            // относится к этой записи, а не к чужому вопросу.
            _mine.Add(await _engine.ProcessAsync(path, force).ConfigureAwait(true));
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
