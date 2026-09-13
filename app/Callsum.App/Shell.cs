using System.ComponentModel;
using System.Diagnostics;
using Callsum.Core;

namespace Callsum.App;

/// <summary>Открытие файлов и папок — тем, чем их открывает Windows или настройка.</summary>
public static class Shell
{
    /// <summary>Открыть документ. Возвращает текст ошибки или null, если всё вышло.</summary>
    public static string? OpenFile(string path, string? markdownApp = null)
    {
        if (!File.Exists(path))
        {
            return $"Файл не найден: {path}";
        }

        var command = DocumentOpener.Build(markdownApp, path);
        var info = new ProcessStartInfo(command.Target)
        {
            UseShellExecute = command.UseShell,
            Arguments = command.Arguments,
        };

        var hint = string.IsNullOrWhiteSpace(markdownApp)
            ? "Похоже, для .md не назначена программа."
            : "Проверьте параметр [view] markdown_app в config.toml.";
        return Run(info, path, hint);
    }

    /// <summary>Показать папку в проводнике.</summary>
    public static string? OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return $"Папка не найдена: {path}";
        }

        return Run(new ProcessStartInfo("explorer.exe", $"\"{path}\""), path, "");
    }

    private static string? Run(ProcessStartInfo info, string path, string hint)
    {
        try
        {
            Process.Start(info);
            return null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // Чаще всего это значит, что открывать нечем: программа не найдена
            // или для .md ничего не назначено. Сообщение должно говорить, что делать.
            return $"Не удалось открыть {path}: {exception.Message}. {hint}".TrimEnd();
        }
    }
}
