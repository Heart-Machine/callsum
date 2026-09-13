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
/// </summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>Поле формы: где значение живёт в файле и как называется для человека.</summary>
    /// <param name="PathKey">Ключ папки в ответе ядра, если поле — это путь.</param>
    private sealed record Field(
        string Section, string Key, string Label, bool Required = true, string? PathKey = null);

    private readonly EngineClient _engine;
    private readonly ObsConnection _obs;
    private readonly DispatcherQueue _ui;
    private readonly Dictionary<TextBox, Field> _fields = [];

    private EngineEvent.Settings? _loaded;
    private string _microphone = "";
    private string _systemAudio = "";

    public SettingsWindow(EngineClient engine, ObsConnection obs)
    {
        InitializeComponent();
        Title = "callsum — Настройки";
        _engine = engine;
        _obs = obs;
        _ui = DispatcherQueue.GetForCurrentThread();

        AppWindow.Resize(new SizeInt32(620, 900));

        _fields[Recordings] = new("paths", "recordings", "Записи", PathKey: "recordings");
        _fields[Out] = new("paths", "out", "Расшифровки и протоколы", PathKey: "out");
        _fields[FolderTemplate] = new("paths", "folder_template", "Папка одного созвона");
        _fields[FilenameFormat] = new("obs", "filename_format", "Файл записи в OBS");
        _fields[WhisperModel] = new("transcribe", "model", "Модель распознавания");
        // Пустой язык — это «определять самому», а пустая программа для markdown —
        // «чем открывает Windows». Оба значения осмысленны.
        _fields[Language] = new("transcribe", "language", "Язык", Required: false);
        _fields[SummaryHost] = new("summary", "host", "Адрес Ollama");
        _fields[SummaryModel] = new("summary", "model", "Модель протокола");
        _fields[MarkdownApp] = new("view", "markdown_app", "Чем открывать", Required: false);

        _ = LoadAsync();
        _ = LoadDevicesAsync();
    }

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

    private async Task LoadAsync()
    {
        try
        {
            var settings = await _engine.GetSettingsAsync();
            _ui.TryEnqueue(() => Show(settings));
        }
        catch (Exception exception) when (exception is EngineException or TimeoutException)
        {
            _ui.TryEnqueue(() =>
            {
                Source.Text = "Не удалось прочитать настройки";
                Status.Text = exception.Message;
            });
        }
    }

    private void Show(EngineEvent.Settings settings)
    {
        _loaded = settings;
        Source.Text = $"Файл настроек: {settings.Path}";

        foreach (var (box, field) in _fields)
        {
            // Папки показываются полным путём, даже если в файле они записаны
            // относительно: «out» не говорит человеку, где искать протоколы.
            box.Text = field.PathKey is { } key && settings.ResolvedPath(key) is { Length: > 0 } folder
                ? folder
                : settings.Text(field.Section, field.Key);
        }

        SummaryEnabled.IsOn = settings.Flag("summary", "enabled");
        ShowDefaults();

        Form.IsEnabled = true;
        Save.IsEnabled = true;
    }

    /// <summary>Подсказать, куда программа сложила бы всё сама.</summary>
    private void ShowDefaults()
    {
        if (_loaded is not { } settings)
        {
            return;
        }

        RecordingsHint.Text = Hint(settings.DefaultPath("recordings"));
        OutHint.Text = Hint(settings.DefaultPath("out"));
    }

    private static string Hint(string byDefault) =>
        byDefault.Length > 0 ? $"По умолчанию {byDefault}" : "";

    private async void OnBrowseClick(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string name })
        {
            return;
        }

        var target = name == "Recordings" ? Recordings : Out;
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

    private async void OnSaveClick(object sender, RoutedEventArgs args)
    {
        if (_loaded is not { } settings)
        {
            return;
        }

        if (Empty() is { } missing)
        {
            // Пустая папка или модель — это не «по умолчанию», а неработающая
            // программа: лучше сказать сразу, чем чинить config.toml потом.
            Status.Text = $"Поле «{missing}» нельзя оставить пустым";
            return;
        }

        if (Relative() is { } incomplete)
        {
            // Относительный путь считается от того места, откуда запущено ядро,
            // и означает разные папки в разных руках. Программа хранит полные.
            Status.Text = $"Путь в поле «{incomplete}» должен быть полным — "
                          + $"например, {settings.DefaultPath("out")}";
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
            if (changes.Count > 0)
            {
                var saved = await _engine.SaveSettingsAsync(changes);
                _loaded = saved;
                ShowDefaults();
                done.Add(Describe(changes));
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

            Status.Text = $"Сохранено: {string.Join(", ", done)}";
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

    /// <summary>Название первого обязательного поля, которое оставили пустым.</summary>
    private string? Empty() => _fields
        .Where(pair => pair.Value.Required && pair.Key.Text.Trim().Length == 0)
        .Select(pair => pair.Value.Label)
        .FirstOrDefault();

    /// <summary>Название первой папки, путь к которой указан не полностью.</summary>
    private string? Relative() => _fields
        .Where(pair => pair.Value.PathKey is not null && !Path.IsPathFullyQualified(pair.Key.Text.Trim()))
        .Select(pair => pair.Value.Label)
        .FirstOrDefault();

    /// <summary>Только изменённые значения, разложенные по разделам файла.</summary>
    private Dictionary<string, Dictionary<string, object?>> Collect(EngineEvent.Settings settings)
    {
        var changes = new Dictionary<string, Dictionary<string, object?>>();

        void Change(string section, string key, object? value)
        {
            if (!changes.TryGetValue(section, out var block))
            {
                changes[section] = block = [];
            }

            block[key] = value;
        }

        foreach (var (box, field) in _fields)
        {
            var written = box.Text.Trim();
            if (field.PathKey is { } key)
            {
                // Папка сравнивается с полным путём — её и показывали. Заодно
                // относительный путь из старого файла переписывается на полный.
                var stored = settings.Text(field.Section, field.Key);
                if (written != settings.ResolvedPath(key) || !Path.IsPathFullyQualified(stored))
                {
                    Change(field.Section, field.Key, written);
                }

                continue;
            }

            if (written != settings.Text(field.Section, field.Key))
            {
                Change(field.Section, field.Key, written);
            }
        }

        if (SummaryEnabled.IsOn != settings.Flag("summary", "enabled"))
        {
            Change("summary", "enabled", SummaryEnabled.IsOn);
        }

        return changes;
    }

    private static string Describe(Dictionary<string, Dictionary<string, object?>> changes) =>
        string.Join(", ", changes.SelectMany(
            section => section.Value.Keys.Select(key => $"[{section.Key}] {key}")));

    private void OnCloseClick(object sender, RoutedEventArgs args) => Close();
}
