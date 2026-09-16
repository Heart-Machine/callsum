namespace Callsum.Obs;

/// <summary>Куда и с каким паролем подключаться к OBS.</summary>
public sealed class ObsSettings
{
    public const int DefaultPort = 4455;

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = DefaultPort;

    public string Password { get; init; } = "";

    /// <summary>Подсказка, которую показываем, когда подключиться не вышло.</summary>
    public const string SetupHint =
        "В OBS: «Инструменты» → «Настройки WebSocket-сервера» → включить " +
        "«Включить WebSocket-сервер». Укажите пароль в настройках callsum.";
}
