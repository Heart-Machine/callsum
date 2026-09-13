using System.Runtime.InteropServices;
using Callsum.Core;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Callsum.App;

/// <summary>
/// Всплывающие уведомления Windows от имени callsum.
///
/// Имя в подписи уведомления Windows берёт из регистрации приложения по его
/// идентификатору (AppUserModelID), а не из процесса. Без регистрации там
/// оказывается имя исполняемого файла — в первой версии это было «Python»,
/// и понять, кто прислал уведомление, было невозможно.
/// </summary>
public sealed class Notifications
{
    /// <summary>Тот же идентификатор, что у первой версии: это одно приложение.</summary>
    public const string AppId = "Callsum.CallRecorder";

    public const string DisplayName = "callsum";

    private readonly Action<string> _log;
    private ToastNotifier? _notifier;

    public Notifications(Action<string> log) => _log = log;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>
    /// Назваться Windows. Делается один раз при запуске: до регистрации
    /// уведомления либо не показываются, либо подписаны чужим именем.
    /// </summary>
    public void Register()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppId);
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
            key?.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
            _notifier = ToastNotificationManager.CreateToastNotifier(AppId);
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException
                                              or InvalidOperationException or IOException)
        {
            // Без уведомлений приложение остаётся полностью работоспособным:
            // всё то же самое видно в окне, поэтому это замечание, а не ошибка.
            _log($"Уведомления Windows недоступны: {exception.Message}");
        }
    }

    /// <summary>Показать уведомление. Молча ничего не делает, если их нет.</summary>
    public void Show(string title, string message)
    {
        if (_notifier is null)
        {
            return;
        }

        try
        {
            var document = new XmlDocument();
            document.LoadXml(ToastXml.Build(title, message));
            _notifier.Show(new ToastNotification(document));
        }
        catch (Exception exception) when (exception is COMException or ArgumentException)
        {
            _log($"Не получилось показать уведомление: {exception.Message}");
        }
    }
}
