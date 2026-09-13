namespace Callsum.Core;

/// <summary>Чем и как запускать открытие файла.</summary>
/// <param name="Target">Программа, ссылка или сам файл.</param>
/// <param name="Arguments">Аргументы командной строки, уже с кавычками.</param>
/// <param name="UseShell">Открывать средствами Windows, а не запускать программу.</param>
public sealed record OpenCommand(string Target, string Arguments, bool UseShell);

/// <summary>
/// Настройка «чем открывать протоколы» из config.toml.
///
/// Разбор повторяет первую версию (`callsum/view.py`): у пользователей уже
/// записаны там пути и ссылки, и приложение обязано понимать их так же.
/// Понимает три записи:
/// пусто — то, что назначено в Windows для этого вида файлов;
/// ссылка вида obsidian://open?path={file};
/// программа с подстановкой («code -r {file}») или без неё («notepad++»).
/// </summary>
public static class DocumentOpener
{
    public const string Placeholder = "{file}";

    public static OpenCommand Build(string? setting, string path)
    {
        var app = (setting ?? "").Trim();
        if (app.Length == 0)
        {
            return new OpenCommand(path, "", UseShell: true);
        }

        if (app.Contains("://", StringComparison.Ordinal))
        {
            // Путь в ссылке обязан быть закодирован: пробелы и кириллица
            // в адресе иначе обрываются на первом же пробеле.
            var link = app.Contains(Placeholder, StringComparison.Ordinal)
                ? app
                : app.TrimEnd('/') + "/" + Placeholder;
            return new OpenCommand(
                link.Replace(Placeholder, Uri.EscapeDataString(path), StringComparison.Ordinal),
                "",
                UseShell: true);
        }

        var parts = Split(app);
        if (app.Contains(Placeholder, StringComparison.Ordinal))
        {
            var program = parts[0].Replace(Placeholder, path, StringComparison.Ordinal);
            var arguments = parts.Skip(1)
                .Select(part => Quote(part.Replace(Placeholder, path, StringComparison.Ordinal)));
            return new OpenCommand(program, string.Join(" ", arguments), UseShell: false);
        }

        return new OpenCommand(parts[0], string.Join(
            " ", parts.Skip(1).Select(Quote).Append(Quote(path))), UseShell: false);
    }

    /// <summary>
    /// Разобрать запись на программу и аргументы.
    ///
    /// Путь вида C:\Program Files\Typora\Typora.exe по пробелам не делится,
    /// даже записанный без кавычек: иначе запускать было бы нечего.
    /// </summary>
    private static List<string> Split(string command)
    {
        if (File.Exists(command))
        {
            return [command];
        }

        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var symbol in command)
        {
            if (symbol == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(symbol) && !quoted)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(symbol);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts.Count > 0 ? parts : [command];
    }

    /// <summary>Кавычки нужны там, где есть пробелы: ключи вроде -r без них.</summary>
    private static string Quote(string value) =>
        value.StartsWith('"') || !value.Any(char.IsWhiteSpace) ? value : $"\"{value}\"";
}
