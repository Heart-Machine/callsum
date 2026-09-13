using System.Text.Json;

namespace Callsum.Obs;

/// <summary>Куда и с каким паролем подключаться к OBS.</summary>
public sealed class ObsSettings
{
    public const int DefaultPort = 4455;

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = DefaultPort;

    public string Password { get; init; } = "";

    /// <summary>Включён ли websocket-сервер в самом OBS.</summary>
    public bool EnabledInObs { get; init; } = true;

    /// <summary>Подсказка, которую показываем, когда подключиться не вышло.</summary>
    public const string SetupHint =
        "В OBS: «Инструменты» → «Настройки WebSocket-сервера» → включить " +
        "«Включить WebSocket-сервер». Пароль подхватится сам.";

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "obs-studio", "plugin_config", "obs-websocket", "config.json");

    /// <summary>
    /// Прочитать настройки из конфига самого OBS.
    /// Пароль хранится там же, поэтому спрашивать его у пользователя не нужно —
    /// и хранить у себя тоже.
    /// </summary>
    public static ObsSettings Load(string? configPath = null)
    {
        var path = configPath ?? ConfigPath;
        if (!File.Exists(path))
        {
            return new ObsSettings();
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var authRequired = GetBool(root, "auth_required", true);
            return new ObsSettings
            {
                Port = GetInt(root, "server_port", DefaultPort),
                Password = authRequired ? GetString(root, "server_password") : "",
                EnabledInObs = GetBool(root, "server_enabled", false),
            };
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // Испорченный или недоступный конфиг — не повод падать: подключение
            // всё равно будет попробовано со значениями по умолчанию.
            return new ObsSettings();
        }
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int GetInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static bool GetBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}
