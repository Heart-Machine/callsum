using System.Text.Json;

namespace Callsum.Core;

/// <summary>Выбор, показывать ли проверку окружения перед главным окном.</summary>
public sealed class WelcomePreferences(string path)
{
    private const string DontShowProperty = "dontShow";

    private readonly string _path = path;

    /// <summary>Путь в профиле пользователя, переживающий обновление приложения.</summary>
    public static WelcomePreferences ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "callsum-data", "welcome.json"));

    /// <summary>Не показываем экран только после явного выбора человека.</summary>
    public bool ShouldShow()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return true;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            return !document.RootElement.TryGetProperty(DontShowProperty, out var value)
                || value.ValueKind != JsonValueKind.True;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Сомнительная настройка не должна скрыть полезную проверку.
            return true;
        }
    }

    /// <summary>Запомнить решение только после нажатия «Продолжить».</summary>
    public void Save(bool dontShow)
    {
        try
        {
            if (!dontShow)
            {
                File.Delete(_path);
                return;
            }

            var folder = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            Directory.CreateDirectory(folder);
            File.WriteAllText(_path, "{\"dontShow\":true}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Экран всё равно должен пустить человека в приложение.
        }
    }
}
