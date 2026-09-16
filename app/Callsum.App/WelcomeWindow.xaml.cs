using System.Collections.ObjectModel;
using Callsum.Core;
using Callsum.Obs;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Callsum.App;

/// <summary>Первый экран: показывает состояние окружения до начала записи.</summary>
public sealed partial class WelcomeWindow : Window
{
    internal static readonly SolidColorBrush Success = new(Colors.ForestGreen);
    internal static readonly SolidColorBrush Attention = new(Colors.DarkOrange);
    internal static readonly SolidColorBrush Failure = new(Colors.IndianRed);

    private readonly Action _continueToMain;
    private readonly bool _saveChoice;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _closed;
    private bool _checking;

    public ObservableCollection<WelcomeCheck> Checks { get; } = [];

    public WelcomeWindow(Action continueToMain, bool saveChoice)
    {
        _continueToMain = continueToMain;
        _saveChoice = saveChoice;
        InitializeComponent();
        Title = "callsum — Проверка готовности";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "callsum-icon.ico"));

        if (!saveChoice)
        {
            DontShowAgain.Visibility = Visibility.Collapsed;
            ContinueButton.Content = "Закрыть";
        }

        _ = CheckAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
        };
    }

    private async Task CheckAsync()
    {
        if (_closed || _checking)
        {
            return;
        }

        _checking = true;
        Checks.Clear();
        Checks.Add(WelcomeCheck.Checking());
        Checking.Visibility = Visibility.Visible;
        Checking.IsActive = true;
        Subtitle.Text = "Проверяем, всё ли готово к первому созвону";
        RefreshButton.IsEnabled = false;
        ContinueButton.IsEnabled = false;

        try
        {
            var engine = await CheckEngineAsync();
            if (engine?.Doctor is { } doctor)
            {
                AddDoctorChecks(doctor);
            }

            await CheckObsAsync(engine?.Settings);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Add(WelcomeKind.Problem, "Не удалось завершить проверку", exception.Message);
        }
        finally
        {
            if (!_closed)
            {
                _checking = false;
                Checking.IsActive = false;
                Checking.Visibility = Visibility.Collapsed;
                Subtitle.Text = "Проверка завершена";
                RefreshButton.IsEnabled = true;
                ContinueButton.IsEnabled = true;
            }
        }
    }

    private async Task<EngineCheck?> CheckEngineAsync()
    {
        var executable = EngineLocator.Find();
        if (executable is null)
        {
            Checks.Clear();
            Add(WelcomeKind.Problem, "Ядро обработки не найдено", EngineLocator.NotFoundMessage);
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var engine = new EngineClient(executable);
        try
        {
            await engine.StartAsync(timeout.Token);
            var doctor = await engine.GetDoctorAsync(timeout.Token);
            var settings = await engine.GetSettingsAsync(timeout.Token);
            Checks.Clear();
            Add(WelcomeKind.Ready, "Ядро обработки", $"Готово: {doctor.Version ?? "версия не указана"}.");
            return new EngineCheck(doctor, settings);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            Checks.Clear();
            Add(
                WelcomeKind.Problem,
                "Ядро обработки долго не отвечает",
                "Можно продолжить и проверить состояние в главном окне.");
            return null;
        }
        catch (Exception exception) when (exception is EngineException or IOException or TimeoutException)
        {
            Checks.Clear();
            Add(WelcomeKind.Problem, "Ядро обработки не запускается", exception.Message);
            return null;
        }
    }

    private void AddDoctorChecks(EngineEvent.Doctor doctor)
    {
        if (doctor.Ffmpeg)
        {
            Add(WelcomeKind.Ready, "FFmpeg", "Готов к обработке аудиодорожек.");
        }
        else if (doctor.FfmpegWillDownload)
        {
            Add(WelcomeKind.Note, "FFmpeg", "Скачается автоматически перед первой обработкой.");
        }
        else
        {
            Add(WelcomeKind.Problem, "FFmpeg", doctor.FfmpegError ?? "Без него записи не обработать.");
        }

        if (!doctor.CudaReady)
        {
            Add(WelcomeKind.Note, "Распознавание речи", "Библиотеки видеокарты и модель скачаются при первой обработке.");
        }
        else if (doctor.Device == "cpu")
        {
            Add(WelcomeKind.Note, "Распознавание речи", "Готово, но расчёт пойдёт на процессоре и займёт больше времени.");
        }
        else
        {
            Add(WelcomeKind.Ready, "Распознавание речи", "Готово использовать видеокарту.");
        }

        if (!doctor.SummaryEnabled)
        {
            Add(WelcomeKind.Ready, "Ollama и протокол", "Протоколы выключены в настройках; расшифровка будет работать.");
        }
        else if (!doctor.Ollama)
        {
            Add(WelcomeKind.Problem, "Ollama и протокол", doctor.OllamaError ?? "Ollama не отвечает; протокол не соберётся.");
        }
        else if (!doctor.SummaryModel)
        {
            var model = doctor.SummaryModelName ?? "выбранная модель";
            Add(WelcomeKind.Problem, "Ollama и протокол", $"В Ollama нет {model}. Расшифровка будет работать без протокола.");
        }
        else
        {
            Add(WelcomeKind.Ready, "Ollama и протокол", "Ollama отвечает, модель протокола загружена.");
        }

        var folders = new[] { doctor.RecordingsError, doctor.OutError }
            .FirstOrDefault(error => error is { Length: > 0 });
        if (folders is null)
        {
            Add(WelcomeKind.Ready, "Папки записей и результатов", "Доступны для записи.");
        }
        else
        {
            Add(WelcomeKind.Problem, "Папки записей и результатов", folders);
        }
    }

    private async Task CheckObsAsync(EngineEvent.Settings? appSettings)
    {
        if (appSettings is null)
        {
            Add(
                WelcomeKind.Problem,
                "OBS",
                "Настройки callsum не прочитаны, поэтому подключение к OBS не проверено.");
            return;
        }

        ObsSettings settings;
        try
        {
            var port = (int)(appSettings.Number("obs", "port") ?? ObsSettings.DefaultPort);
            settings = new ObsSettings
            {
                Host = appSettings.Text("obs", "host"),
                Port = port is 0 ? ObsSettings.DefaultPort : port,
                Password = ObsCredentials.Read(),
            };
        }
        catch (ObsCredentialsException exception)
        {
            Add(WelcomeKind.Problem, "OBS", exception.Message);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using var client = new ObsClient(settings);
        try
        {
            await client.ConnectAsync(timeout.Token);
            var setup = await new ObsService(client).GetSetupStateAsync(ObsSetup.DefaultProfile, timeout.Token);
            Add(
                setup.Ready ? WelcomeKind.Ready : WelcomeKind.Problem,
                "OBS",
                setup.Ready
                    ? $"Подключён на {settings.Host}:{settings.Port}, профиль и коллекция callsum готовы к записи."
                    : $"Подключён на {settings.Host}:{settings.Port}, но профиль или коллекция callsum отсутствуют. Их можно создать из главного окна.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ObsException or OperationCanceledException)
        {
            Add(
                WelcomeKind.Problem,
                "OBS",
                $"Не удалось подключиться к OBS на {settings.Host}:{settings.Port}. Запись из приложения пока недоступна.");
        }
    }

    private void Add(WelcomeKind kind, string title, string detail) =>
        Checks.Add(new WelcomeCheck(kind, title, detail));

    private void OnRefreshClick(object sender, RoutedEventArgs args) => _ = CheckAsync();

    private void OnContinueClick(object sender, RoutedEventArgs args)
    {
        if (_saveChoice)
        {
            WelcomePreferences.ForCurrentUser().Save(DontShowAgain.IsChecked == true);
        }

        _continueToMain();
        Close();
    }
}

/// <summary>Результат запуска ядра: его отчёт и настройки для проверки OBS.</summary>
internal sealed record EngineCheck(EngineEvent.Doctor Doctor, EngineEvent.Settings Settings);

public sealed class WelcomeCheck
{
    public WelcomeCheck(WelcomeKind kind, string title, string detail)
    {
        Kind = kind;
        Title = title;
        Detail = detail;
    }

    public WelcomeKind Kind { get; set; }

    public string Title { get; set; }

    public string Detail { get; set; }

    public Symbol Icon => Kind switch
    {
        WelcomeKind.Ready => Symbol.Accept,
        WelcomeKind.Note => Symbol.Help,
        WelcomeKind.Problem => Symbol.Cancel,
        _ => Symbol.Sync,
    };

    public Brush Tint => Kind switch
    {
        WelcomeKind.Ready => WelcomeWindow.Success,
        WelcomeKind.Note => WelcomeWindow.Attention,
        WelcomeKind.Problem => WelcomeWindow.Failure,
        _ => WelcomeWindow.Attention,
    };

    public static WelcomeCheck Checking() => new(WelcomeKind.Checking, "Проверяю окружение", "Это займёт несколько секунд.");
}

public enum WelcomeKind
{
    Checking,
    Ready,
    Note,
    Problem,
}
