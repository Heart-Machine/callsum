using System.Reflection;
using Callsum.Core;
using Callsum.Obs;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace Callsum.App;

/// <summary>
/// Окно настроек: форма поверх того же config.toml, который правят руками.
///
/// Значения приходят от ядра и уходят обратно к нему: файл читает и пишет оно,
/// а окно только показывает. Отправляются лишь изменённые поля — иначе
/// сохранение затирало бы правки, сделанные тем временем в файле.
///
/// Настроек три десятка, поэтому они разложены по вкладкам, а редкие спрятаны
/// под «Дополнительно»: рядом с «Моделью» должно стоять то, что меняют, а не
/// то, что трогают раз в жизни.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>Модели распознавания, между которыми обычно выбирают.</summary>
    private static readonly string[] WhisperModels =
        ["large-v3", "large-v3-turbo", "medium", "small", "base", "tiny"];

    private static readonly string[] Languages = ["ru", "en", "de", "fr", "es"];

    /// <summary>Сколько Ollama держит модель в видеопамяти после ответа.</summary>
    private static readonly string[] KeepAliveChoices = ["0s", "5m", "30m", "-1"];

    private readonly EngineClient _engine;
    private readonly ObsConnection _obs;
    private readonly Updates _updates;
    private readonly Func<bool> _recording;
    private readonly DispatcherQueue _ui;
    private readonly List<Setting> _fields = [];
    private readonly Dictionary<string, FrameworkElement> _pages = [];

    private EngineEvent.Settings? _loaded;
    private string _microphone = "";
    private string _systemAudio = "";
    private string _tab = "";
    private bool _modelsAsked;
    private bool _aboutAsked;

    public SettingsWindow(EngineClient engine, ObsConnection obs, Updates updates, Func<bool> recording)
    {
        InitializeComponent();
        Title = "callsum — Настройки";
        _engine = engine;
        _obs = obs;
        _updates = updates;
        _recording = recording;
        _ui = DispatcherQueue.GetForCurrentThread();

        AppWindow.Resize(new SizeInt32(960, 840));

        _pages["folders"] = PageFolders;
        _pages["record"] = PageRecord;
        _pages["transcribe"] = PageTranscribe;
        _pages["speakers"] = PageSpeakers;
        _pages["summary"] = PageSummary;
        _pages["about"] = PageAbout;

        WhisperModel.ItemsSource = WhisperModels;
        Language.ItemsSource = Languages;
        KeepAlive.ItemsSource = KeepAliveChoices;
        AboutVersion.Text = $"callsum {Version}";

        Describe();

        _ = LoadAsync();
        _ = LoadDevicesAsync();
    }

    /// <summary>Версия приложения — её же несёт установщик.</summary>
    private static string Version => AppVersion.Current(Assembly.GetExecutingAssembly());

    /// <summary>
    /// Какие поля есть на форме и где их значения живут в файле.
    ///
    /// Порядок совпадает с порядком на вкладках: так проще проверить, что ни
    /// одна настройка из config.toml не осталась без своего поля.
    /// </summary>
    private void Describe()
    {
        On("folders");
        PathField(Recordings, "paths", "recordings", "Записи", RecordingsHint, resolve: "recordings");
        PathField(Out, "paths", "out", "Расшифровки и протоколы", OutHint, resolve: "out");
        Line(FolderTemplate, "paths", "folder_template", "Папка одного созвона", FolderTemplateHint);
        // Пустая программа для markdown — это «чем открывает Windows».
        Line(MarkdownApp, "view", "markdown_app", "Чем открывать", MarkdownAppHint, required: false);

        On("record");
        Line(FilenameFormat, "obs", "filename_format", "Имя файла записи", FilenameFormatHint);
        Flag(AutoSwitch, "obs", "auto_switch");
        Flag(RestoreAfter, "obs", "restore_after");
        Line(ObsProfile, "obs", "profile", "Профиль OBS", ObsProfileHint);
        Line(ObsHost, "obs", "host", "Адрес OBS", ObsHostHint);
        Number(ObsPort, "obs", "port", "Порт OBS", ObsPortHint);
        Words(Extensions, "audio", "extensions", "Какие файлы считать записями", ExtensionsHint);
        Number(StableSeconds, "audio", "stable_seconds", "Файл дописан, если не менялся", StableSecondsHint);

        On("transcribe");
        Choice(WhisperModel, "transcribe", "model", "Модель", WhisperModelHint);
        // Пустой язык — это «определять самому», осмысленное значение.
        Choice(Language, "transcribe", "language", "Язык", LanguageHint, required: false);
        PathField(ModelDir, "transcribe", "model_dir", "Куда скачивать модель", ModelDirHint, required: false);
        Pick(Device, "transcribe", "device", "На чём считать", DeviceHint);
        Pick(ComputeType, "transcribe", "compute_type", "Точность вычислений", ComputeTypeHint);
        Flag(Vad, "transcribe", "vad");
        Number(BeamSize, "transcribe", "beam_size", "Ширина поиска", BeamSizeHint);

        On("speakers");
        Line(SpeakerOne, "speakers", "1", "Дорожка 1", SpeakerOneHint);
        Line(SpeakerTwo, "speakers", "2", "Дорожка 2", SpeakerTwoHint);
        Line(SpeakerDefault, "speakers", "default", "Если дорожка одна", SpeakerDefaultHint);
        Number(MergeGap, "speakers", "merge_gap", "Слить соседние реплики", MergeGapHint, whole: false);
        // Пауза, по которой режут реплику, лежит в другом разделе файла, но
        // человеку она нужна рядом с той, по которой реплики склеивают.
        Number(SplitGap, "transcribe", "split_gap", "Разбить реплику", SplitGapHint, whole: false);

        On("summary");
        Flag(SummaryEnabled, "summary", "enabled");
        Line(SummaryHost, "summary", "host", "Адрес Ollama", SummaryHostHint);
        // Подсказка под моделью занята списком из Ollama, поэтому её нет здесь.
        Choice(SummaryModel, "summary", "model", "Модель протокола", hint: null);
        Number(NumCtx, "summary", "num_ctx", "Окно контекста", NumCtxHint);
        Number(Temperature, "summary", "temperature", "Температура", TemperatureHint, whole: false);
        Flag(Think, "summary", "think");
        Choice(KeepAlive, "summary", "keep_alive", "Держать модель в видеопамяти", KeepAliveHint);
        Number(ChunkChars, "summary", "chunk_chars", "Размер куска", ChunkCharsHint);
        Number(ChunkOverlap, "summary", "chunk_overlap_chars", "Нахлёст кусков", ChunkOverlapHint);
        Number(TimeoutSeconds, "summary", "timeout_seconds", "Сколько ждать ответа", TimeoutSecondsHint);
    }

    // --- перечисление полей --------------------------------------------
    /// <summary>Дальше идут поля этой вкладки: на ней же покажется их ошибка.</summary>
    private void On(string tab) => _tab = tab;

    private void Line(TextBox box, string section, string key, string label, TextBlock? hint,
                      bool required = true) =>
        _fields.Add(new TextSetting(box, section, key, label, _tab, hint, required));

    private void PathField(TextBox box, string section, string key, string label, TextBlock? hint,
                           string? resolve = null, bool required = true) =>
        _fields.Add(new PathSetting(box, section, key, label, _tab, hint, resolve, required));

    private void Words(TextBox box, string section, string key, string label, TextBlock? hint) =>
        _fields.Add(new ListSetting(box, section, key, label, _tab, hint));

    private void Flag(ToggleSwitch toggle, string section, string key) =>
        _fields.Add(new FlagSetting(toggle, section, key, toggle.Header as string ?? key, _tab));

    private void Number(NumberBox box, string section, string key, string label, TextBlock? hint,
                        bool whole = true) =>
        _fields.Add(new NumberSetting(box, section, key, label, _tab, hint, whole));

    private void Choice(ComboBox box, string section, string key, string label, TextBlock? hint,
                        bool required = true) =>
        _fields.Add(new ChoiceSetting(box, section, key, label, _tab, hint, required));

    private void Pick(ComboBox box, string section, string key, string label, TextBlock? hint) =>
        _fields.Add(new PickSetting(box, section, key, label, _tab, hint));

    // --- вкладки --------------------------------------------------------
    private void OnTabChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Первая вкладка выбрана прямо в разметке, и событие приходит ещё до
        // того, как окно успело собрать свои страницы.
        if (_pages.Count == 0 || args.SelectedItem is not NavigationViewItem { Tag: string tab })
        {
            return;
        }

        foreach (var (name, page) in _pages)
        {
            page.Visibility = name == tab ? Visibility.Visible : Visibility.Collapsed;
        }

        // Иначе длинная вкладка открывается с той же высоты, где закончилась
        // прошлая, — и человек видит середину формы.
        Pages.ChangeView(null, 0, null, true);

        if (tab == "summary" && !_modelsAsked)
        {
            _ = LoadModelsAsync();
        }

        if (tab == "about" && !_aboutAsked)
        {
            _ = LoadAboutAsync();
        }
    }

    private void Select(string tab)
    {
        foreach (var item in Tabs.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == tab)
            {
                Tabs.SelectedItem = item;
                return;
            }
        }
    }

    // --- настройки ------------------------------------------------------
    private async Task LoadAsync()
    {
        try
        {
            var settings = await _engine.GetSettingsAsync();
            _ui.TryEnqueue(() => Show(settings));
        }
        catch (Exception exception) when (exception is EngineException or TimeoutException)
        {
            _ui.TryEnqueue(() => Status.Text = $"Не удалось прочитать настройки: {exception.Message}");
        }
    }

    private void Show(EngineEvent.Settings settings)
    {
        _loaded = settings;
        AboutConfig.Text = $"Файл настроек: {settings.Path}";
        ShowFolderButton(OpenConfig, Path.GetDirectoryName(settings.Path));

        foreach (var field in _fields)
        {
            field.Show(settings);
            field.ShowHint(settings);
        }

        Form.IsEnabled = true;
        Save.IsEnabled = true;
    }

    private async void OnSaveClick(object sender, RoutedEventArgs args)
    {
        if (_loaded is not { } settings)
        {
            return;
        }

        foreach (var field in _fields)
        {
            if (field.Problem(settings) is not { } trouble)
            {
                continue;
            }

            // Ошибку показываем там, где её можно исправить: поле может стоять
            // на другой вкладке и вовсе не быть на виду.
            Select(field.Tab);
            field.Focus();
            Status.Text = trouble;
            return;
        }

        var changes = Collect(settings);
        var devices = CollectDevices();
        if (changes.Count == 0 && devices.Count == 0)
        {
            Status.Text = "Менять нечего";
            return;
        }

        Save.IsEnabled = false;
        Status.Text = "Сохраняю…";
        try
        {
            var done = new List<string>();
            var notes = new List<string>();
            if (changes.Count > 0)
            {
                Show(await _engine.SaveSettingsAsync(changes));
                done.Add(Describe(changes));

                // Куда писать запись, решает профиль OBS, а не наши настройки.
                // Без этого человек поменял бы папку в окне, а записи продолжали
                // бы уходить в старую — и он искал бы их там, где их нет.
                if (NewRecordingsFolder(changes) is { Length: > 0 } folder)
                {
                    var result = await _obs.EnsureRecordFolderAsync(folder);
                    if (result.Changed)
                    {
                        done.Add("папка записи в OBS");
                    }
                    else if (result.Note is { Length: > 0 } note)
                    {
                        notes.Add(note);
                    }
                }
            }

            // Устройства уходят в OBS, а не в config.toml: они живут в его
            // коллекции сцен вместе с источниками.
            foreach (var (input, device) in devices)
            {
                var error = await _obs.SetAudioDeviceAsync(input, device.Value);
                if (error is not null)
                {
                    Status.Text = error;
                    return;
                }

                Remember(input, device.Value);
                done.Add($"устройство «{input}»");
            }

            Status.Text = $"Сохранено: {string.Join(", ", done)}"
                          + (notes.Count > 0 ? $". Но {string.Join("; ", notes)}" : "");
        }
        catch (Exception exception) when (exception is EngineException or TimeoutException)
        {
            Status.Text = exception.Message;
        }
        finally
        {
            Save.IsEnabled = true;
        }
    }

    /// <summary>Только изменённые значения, разложенные по разделам файла.</summary>
    private Dictionary<string, Dictionary<string, object?>> Collect(EngineEvent.Settings settings)
    {
        var changes = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var field in _fields.Where(item => item.Changed(settings)))
        {
            if (!changes.TryGetValue(field.Section, out var block))
            {
                changes[field.Section] = block = [];
            }

            block[field.Key] = field.Written;
        }

        return changes;
    }

    private static string Describe(Dictionary<string, Dictionary<string, object?>> changes) =>
        string.Join(", ", changes.SelectMany(
            section => section.Value.Keys.Select(key => $"[{section.Key}] {key}")));

    /// <summary>Новая папка записей, если её меняли в этот раз.</summary>
    private static string? NewRecordingsFolder(Dictionary<string, Dictionary<string, object?>> changes) =>
        changes.TryGetValue("paths", out var paths) && paths.TryGetValue("recordings", out var value)
            ? value as string
            : null;

    private async void OnBrowseClick(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: TextBox target })
        {
            return;
        }

        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        // Неупакованному приложению выбор папки нужно привязать к окну — иначе
        // диалог не знает, кому принадлежит, и не открывается вовсе.
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            target.Text = folder.Path;
            Status.Text = "";
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs args) => Close();

    // --- устройства записи ----------------------------------------------
    /// <summary>
    /// Спросить у OBS, между какими устройствами можно выбирать.
    ///
    /// Это отдельная от настроек ядра история: устройства записи живут не
    /// в config.toml, а в коллекции сцен OBS — там же, где сами источники.
    /// </summary>
    private async Task LoadDevicesAsync()
    {
        await ShowDevicesAsync(ObsSetup.MicrophoneInput, MicrophoneDevice, MicrophoneHint);
        await ShowDevicesAsync(ObsSetup.SystemAudioInput, SystemDevice, SystemHint);
    }

    private async Task ShowDevicesAsync(string input, ComboBox box, TextBlock hint)
    {
        var choice = await _obs.GetAudioChoiceAsync(input);
        _ui.TryEnqueue(() =>
        {
            if (choice.Error is { Length: > 0 } error)
            {
                hint.Text = error;
                return;
            }

            box.ItemsSource = choice.Devices;
            box.SelectedItem = choice.Devices.FirstOrDefault(
                device => device.Value == choice.Current);
            box.IsEnabled = true;
            hint.Text = box.SelectedItem is null && choice.Current.Length > 0
                // Выбранного устройства нет в списке — обычно это отключённая
                // гарнитура: запись с неё будет пустой, и лучше сказать сразу.
                ? "Выбранное устройство сейчас недоступно — похоже, оно отключено"
                : "";

            if (input == ObsSetup.MicrophoneInput)
            {
                _microphone = choice.Current;
            }
            else
            {
                _systemAudio = choice.Current;
            }
        });
    }

    /// <summary>Выбранные устройства, если их поменяли.</summary>
    private List<(string Input, ObsDevice Device)> CollectDevices()
    {
        var changed = new List<(string, ObsDevice)>();
        if (MicrophoneDevice.SelectedItem is ObsDevice microphone && microphone.Value != _microphone)
        {
            changed.Add((ObsSetup.MicrophoneInput, microphone));
        }

        if (SystemDevice.SelectedItem is ObsDevice system && system.Value != _systemAudio)
        {
            changed.Add((ObsSetup.SystemAudioInput, system));
        }

        return changed;
    }

    private void Remember(string input, string device)
    {
        if (input == ObsSetup.MicrophoneInput)
        {
            _microphone = device;
        }
        else
        {
            _systemAudio = device;
        }
    }

    // --- модели протокола ------------------------------------------------
    /// <summary>
    /// Список моделей спрашивается у Ollama через ядро — и только когда вкладка
    /// открыта: за сетью на каждое открытие настроек ходить незачем.
    /// </summary>
    private async Task LoadModelsAsync()
    {
        _modelsAsked = true;
        RefreshModels.IsEnabled = false;
        SummaryModelHint.Text = "Спрашиваю у Ollama…";
        var host = SummaryHost.Text.Trim();
        try
        {
            var answer = await _engine.GetOllamaModelsAsync(host.Length > 0 ? host : null);
            _ui.TryEnqueue(() => ShowModels(answer.Names));
        }
        catch (Exception exception) when (exception is EngineException or TimeoutException)
        {
            _ui.TryEnqueue(() => SummaryModelHint.Text =
                $"{exception.Message} Имя модели можно вписать руками.");
        }
        finally
        {
            _ui.TryEnqueue(() => RefreshModels.IsEnabled = true);
        }
    }

    private void ShowModels(IReadOnlyList<string> names)
    {
        // Набранное не должно пропасть от того, что приехал список: модель
        // могли вписать руками, пока он ехал.
        var chosen = SummaryModel.Text;
        SummaryModel.ItemsSource = names;
        SummaryModel.Text = chosen;
        SummaryModelHint.Text = names.Count > 0
            ? "Модели, загруженные в Ollama."
            : "В Ollama нет ни одной модели. Загрузить: ollama pull qwen3:14b";
    }

    private void OnRefreshModelsClick(object sender, RoutedEventArgs args) => _ = LoadModelsAsync();

    // --- о программе -----------------------------------------------------
    private async Task LoadAboutAsync()
    {
        _aboutAsked = true;
        try
        {
            var doctor = await _engine.GetDoctorAsync();
            _ui.TryEnqueue(() => ShowAbout(doctor));
        }
        catch (Exception exception) when (exception is EngineException or TimeoutException)
        {
            // Спросить можно будет ещё раз: вкладка не запомнит неудачу.
            _aboutAsked = false;
            _ui.TryEnqueue(() => AboutDevice.Text = exception.Message);
        }
    }

    private void ShowAbout(EngineEvent.Doctor doctor)
    {
        AboutEngine.Text = $"Ядро обработки {doctor.Version}";
        AboutDevice.Text = doctor.Device == "cuda"
            ? $"Распознавание считает видеокарта ({doctor.ComputeType})"
            : $"Распознавание считает процессор ({doctor.ComputeType}) — это в десятки раз дольше";
        AboutFfmpeg.Text = doctor.Ffmpeg
            ? "ffmpeg на месте"
            : $"ffmpeg не найден: {doctor.FfmpegError}";
        AboutOllama.Text = doctor.Ollama
            ? doctor.SummaryModel
                ? "Ollama отвечает, выбранная модель в ней есть"
                : "Ollama отвечает, но выбранной модели в ней нет"
            : $"Ollama не отвечает: {doctor.OllamaError}";
        AboutCuda.Text = doctor.CudaReady
            ? "Библиотеки видеокарты на месте"
            : "Библиотеки видеокарты ещё не скачаны — это случится при первом распознавании";

        AboutModels.Text = doctor.ModelFolder is { Length: > 0 } models
            ? $"Модели распознавания: {models}"
            : "Модели распознавания: общий кэш Hugging Face";
        ShowFolderButton(OpenModels, doctor.ModelFolder);

        AboutCudaDir.Text = $"Библиотеки видеокарты: {doctor.CudaFolder}";
        ShowFolderButton(OpenCudaDir, doctor.CudaFolder);
    }

    /// <summary>Кнопка «Открыть папку» работает, только когда папка уже есть.</summary>
    private static void ShowFolderButton(Button button, string? folder)
    {
        button.Tag = folder ?? "";
        button.IsEnabled = folder is { Length: > 0 } && Directory.Exists(folder);
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: string folder } && folder.Length > 0)
        {
            Status.Text = Shell.OpenFolder(folder) ?? "";
        }
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs args)
    {
        CheckUpdate.IsEnabled = false;
        UpdateStatus.Text = "Проверяю…";
        var check = await _updates.CheckAsync();
        CheckUpdate.IsEnabled = true;

        if (check.Error is { Length: > 0 } error)
        {
            UpdateStatus.Text = error;
            return;
        }

        if (check.Version is not { Length: > 0 } version)
        {
            UpdateStatus.Text = $"Установлена последняя версия, {Version}";
            return;
        }

        UpdateStatus.Text = $"Скачано обновление {version}. Приложение перезапустится.";
        ApplyUpdate.Visibility = Visibility.Visible;
    }

    private void OnApplyUpdateClick(object sender, RoutedEventArgs args)
    {
        if (_recording())
        {
            // Перезапуск посреди записи оборвал бы созвон.
            UpdateStatus.Text = "Сейчас идёт запись — обновлю, когда она закончится";
            return;
        }

        _updates.Apply();
    }

    // --- поля формы -------------------------------------------------------
    /// <summary>
    /// Одна настройка на форме: знает, где её значение живёт в файле, и умеет
    /// перекладывать его в свой элемент управления и обратно.
    /// </summary>
    private abstract class Setting
    {
        protected Setting(string section, string key, string label, string tab, TextBlock? hint)
        {
            Section = section;
            Key = key;
            Label = label;
            Tab = tab;
            Hint = hint;
            // Пояснение написано в разметке рядом с полем: запоминаем его,
            // чтобы дописывать к нему значение по умолчанию, а не затирать.
            Explain = hint?.Text ?? "";
        }

        public string Section { get; }

        public string Key { get; }

        public string Label { get; }

        /// <summary>Вкладка, на которой стоит поле: на ней показывается и ошибка.</summary>
        public string Tab { get; }

        private TextBlock? Hint { get; }

        private string Explain { get; }

        /// <summary>Значение, набранное в окне, — в том виде, в каком его ждёт файл.</summary>
        public abstract object? Written { get; }

        public abstract void Show(EngineEvent.Settings settings);

        public abstract void Focus();

        /// <summary>Что не так с набранным, или null, если всё в порядке.</summary>
        public virtual string? Problem(EngineEvent.Settings settings) => null;

        public virtual bool Changed(EngineEvent.Settings settings) =>
            !SettingValue.Same(settings.Value(Section, Key), Written);

        /// <summary>Подсказка под полем: пояснение из разметки и значение по умолчанию.</summary>
        public void ShowHint(EngineEvent.Settings settings)
        {
            if (Hint is null)
            {
                return;
            }

            var byDefault = ByDefault(settings);
            Hint.Text = (Explain.Length, byDefault.Length) switch
            {
                (_, 0) => Explain,
                (0, _) => $"По умолчанию {byDefault}",
                _ => $"{Explain} По умолчанию {byDefault}.",
            };
        }

        /// <summary>Значение по умолчанию словами — для подсказки под полем.</summary>
        protected virtual string ByDefault(EngineEvent.Settings settings) =>
            settings.DefaultText(Section, Key);

        protected static string? Empty(string written, bool required, string label) =>
            required && written.Length == 0
                // Пустая папка или модель — это не «по умолчанию», а неработающая
                // программа: лучше сказать сразу, чем чинить config.toml потом.
                ? $"Поле «{label}» нельзя оставить пустым"
                : null;
    }

    private sealed class TextSetting(
        TextBox box, string section, string key, string label, string tab, TextBlock? hint, bool required)
        : Setting(section, key, label, tab, hint)
    {
        public override object? Written => box.Text.Trim();

        public override void Show(EngineEvent.Settings settings) =>
            box.Text = settings.Text(Section, Key);

        public override string? Problem(EngineEvent.Settings settings) =>
            Empty(box.Text.Trim(), required, Label);

        public override void Focus() => box.Focus(FocusState.Programmatic);
    }

    /// <summary>Папка: показывается полным путём и полным же записывается.</summary>
    private sealed class PathSetting(
        TextBox box, string section, string key, string label, string tab, TextBlock? hint,
        string? resolve, bool required)
        : Setting(section, key, label, tab, hint)
    {
        public override object? Written => box.Text.Trim();

        public override void Show(EngineEvent.Settings settings) =>
            // Папки показываются полным путём, даже если в файле они записаны
            // относительно: «out» не говорит человеку, где искать протоколы.
            box.Text = resolve is { } folder && settings.ResolvedPath(folder) is { Length: > 0 } full
                ? full
                : settings.Text(Section, Key);

        public override bool Changed(EngineEvent.Settings settings)
        {
            var written = box.Text.Trim();
            if (resolve is not { } folder)
            {
                return !SettingValue.Same(settings.Value(Section, Key), written);
            }

            // Папка сравнивается с полным путём — его и показывали. Заодно
            // относительный путь из старого файла переписывается на полный.
            var stored = settings.Text(Section, Key);
            return written != settings.ResolvedPath(folder) || !Path.IsPathFullyQualified(stored);
        }

        public override string? Problem(EngineEvent.Settings settings)
        {
            var written = box.Text.Trim();
            if (written.Length == 0)
            {
                return Empty(written, required, Label);
            }

            // Относительный путь считается от того места, откуда запущено ядро,
            // и означает разные папки в разных руках. Программа хранит полные.
            return Path.IsPathFullyQualified(written)
                ? null
                : $"Путь в поле «{Label}» должен быть полным — "
                  + $"например, {settings.DefaultPath("out")}";
        }

        public override void Focus() => box.Focus(FocusState.Programmatic);
    }

    /// <summary>Список значений, набранный через запятую.</summary>
    private sealed class ListSetting(
        TextBox box, string section, string key, string label, string tab, TextBlock? hint)
        : Setting(section, key, label, tab, hint)
    {
        public override object? Written => SettingValue.ParseExtensions(box.Text);

        public override void Show(EngineEvent.Settings settings) =>
            box.Text = SettingValue.FormatList(settings.List(Section, Key));

        public override string? Problem(EngineEvent.Settings settings) =>
            Empty(box.Text.Trim(), required: true, Label);

        public override void Focus() => box.Focus(FocusState.Programmatic);
    }

    private sealed class FlagSetting(
        ToggleSwitch toggle, string section, string key, string label, string tab)
        : Setting(section, key, label, tab, hint: null)
    {
        public override object? Written => toggle.IsOn;

        public override void Show(EngineEvent.Settings settings) =>
            toggle.IsOn = settings.Flag(Section, Key);

        public override void Focus() => toggle.Focus(FocusState.Programmatic);
    }

    private sealed class NumberSetting(
        NumberBox box, string section, string key, string label, string tab, TextBlock? hint, bool whole)
        : Setting(section, key, label, tab, hint)
    {
        // Целое уходит целым: 8192.0 в файле выглядело бы странно, а некоторые
        // из этих значений ядро передаёт дальше как есть.
        public override object? Written => double.IsNaN(box.Value)
            ? null
            : whole ? (long)Math.Round(box.Value) : box.Value;

        public override void Show(EngineEvent.Settings settings) =>
            box.Value = settings.Number(Section, Key) ?? double.NaN;

        public override string? Problem(EngineEvent.Settings settings) =>
            double.IsNaN(box.Value) ? $"Поле «{Label}» нельзя оставить пустым" : null;

        public override void Focus() => box.Focus(FocusState.Programmatic);
    }

    /// <summary>Список, из которого можно выбрать, но можно и вписать своё.</summary>
    private sealed class ChoiceSetting(
        ComboBox box, string section, string key, string label, string tab, TextBlock? hint, bool required)
        : Setting(section, key, label, tab, hint)
    {
        public override object? Written => box.Text.Trim();

        public override void Show(EngineEvent.Settings settings) =>
            box.Text = settings.Text(Section, Key);

        public override string? Problem(EngineEvent.Settings settings) =>
            Empty(box.Text.Trim(), required, Label);

        public override void Focus() => box.Focus(FocusState.Programmatic);
    }

    /// <summary>Выбор из готовых значений: в файл уходит то, что в Tag.</summary>
    private sealed class PickSetting(
        ComboBox box, string section, string key, string label, string tab, TextBlock? hint)
        : Setting(section, key, label, tab, hint)
    {
        public override object? Written => Chosen()?.Tag as string ?? "";

        public override void Show(EngineEvent.Settings settings)
        {
            var stored = settings.Text(Section, Key);
            var known = box.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag as string == stored);
            if (known is null)
            {
                // В файле может стоять значение, которого мы не предлагаем, —
                // терять его из-за этого нельзя.
                known = new ComboBoxItem { Content = stored, Tag = stored };
                box.Items.Add(known);
            }

            box.SelectedItem = known;
        }

        public override void Focus() => box.Focus(FocusState.Programmatic);

        protected override string ByDefault(EngineEvent.Settings settings)
        {
            // В списке стоят понятные названия, и подсказка должна называть то
            // же самое: «По умолчанию auto» под полем «Выбрать самому» только
            // сбивает с толку.
            var value = base.ByDefault(settings);
            var item = box.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(known => known.Tag as string == value);
            return item?.Content as string ?? value;
        }

        private ComboBoxItem? Chosen() => box.SelectedItem as ComboBoxItem;
    }
}
