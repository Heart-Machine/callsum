namespace Callsum.Obs;

/// <summary>Куда и с каким паролем подключаться к OBS.</summary>
public sealed class ObsSettings
{
    public const int DefaultPort = 4455;

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = DefaultPort;

    public string Password { get; init; } = "";

    /// <summary>
    /// Совместимость со стартовой проверкой до того, как она получит настройки
    /// ядра. Сервер проверяется подключением, а не чтением конфига OBS.
    /// </summary>
    public bool EnabledInObs { get; init; } = true;

    /// <summary>Подсказка, которую показываем, когда подключиться не вышло.</summary>
    public const string SetupHint =
        "В OBS: «Инструменты» → «Настройки WebSocket-сервера» → включить " +
        "«Включить WebSocket-сервер». Укажите пароль в настройках callsum.";

    /// <summary>
    /// Стандартное подключение для стартовой проверки. Файл настроек OBS не
    /// читается: пароль берётся только из Диспетчера учётных данных Windows.
    /// </summary>
    public static ObsSettings Load()
    {
        try
        {
            return new ObsSettings { Password = ObsCredentials.Read() };
        }
        catch (ObsCredentialsException)
        {
            // Сам стартовый экран не должен мешать открыть приложение: ошибка
            // проявится обычной проверкой подключения и подскажет, что делать.
            return new ObsSettings();
        }
    }
}
