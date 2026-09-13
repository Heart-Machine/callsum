using Callsum.Core;
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
    private sealed record Field(string Section, string Key, string Label, bool Required = true);

    private readonly EngineClient _engine;
    private readonly DispatcherQueue _ui;
    private readonly Dictionary<TextBox, Field> _fields = [];

    private EngineEvent.Settings? _loaded;

    public SettingsWindow(EngineClient engine)
    {
        InitializeComponent();
        Title = "callsum — Настройки";
        _engine = engine;
        _ui = DispatcherQueue.GetForCurrentThread();

        AppWindow.Resize(new SizeInt32(620, 900));

        _fields[Recordings] = new("paths", "recordings", "Записи созвонов");
        _fields[Out] = new("paths", "out", "Расшифровки и протоколы");
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
            box.Text = settings.Text(field.Section, field.Key);
        }

        SummaryEnabled.IsOn = settings.Flag("summary", "enabled");
        ShowResolved();

        Form.IsEnabled = true;
        Save.IsEnabled = true;
    }

    /// <summary>
    /// Показать, куда путь ведёт на самом деле.
    ///
    /// В файле он может быть относительным — и человек, увидев «out», вправе
    /// не догадаться, где искать готовые протоколы.
    /// </summary>
    private void ShowResolved()
    {
        if (_loaded is not { } settings)
        {
            return;
        }

        RecordingsHint.Text = Hint(Recordings.Text, settings.ResolvedPath("recordings"));
        OutHint.Text = Hint(Out.Text, settings.ResolvedPath("out"));
    }

    private static string Hint(string written, string resolved) =>
        resolved.Length > 0 && !string.Equals(written.Trim(), resolved, StringComparison.OrdinalIgnoreCase)
            ? $"Сейчас это {resolved}"
            : "";

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

        var changes = Collect(settings);
        if (changes.Count == 0)
        {
            Status.Text = "Менять нечего";
            return;
        }

        Save.IsEnabled = false;
        Status.Text = "Сохраняю…";
        try
        {
            var saved = await _engine.SaveSettingsAsync(changes);
            _loaded = saved;
            ShowResolved();
            Status.Text = $"Сохранено: {Describe(changes)}";
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

    /// <summary>Название первого обязательного поля, которое оставили пустым.</summary>
    private string? Empty() => _fields
        .Where(pair => pair.Value.Required && pair.Key.Text.Trim().Length == 0)
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
