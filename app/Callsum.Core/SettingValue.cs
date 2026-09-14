using System.Globalization;
using System.Text.Json;

namespace Callsum.Core;

/// <summary>
/// Значения настроек: перевод между тем, что набрано в окне, и тем, что лежит
/// в файле.
///
/// Настройки — это не только текст: числа, переключатели и списки. На стыке
/// окна и файла легко потерять смысл значения: «8192» из числового поля должно
/// уйти числом, иначе в config.toml появится num_ctx = "8192", и ядро потом
/// на нём споткнётся. Поэтому перевод собран здесь, рядом с проверками.
/// </summary>
public static class SettingValue
{
    /// <summary>Значение словами — для подсказки «По умолчанию …» под полем.</summary>
    public static string Describe(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString() ?? "",
        { ValueKind: JsonValueKind.True } => "да",
        { ValueKind: JsonValueKind.False } => "нет",
        { ValueKind: JsonValueKind.Number } number => Number(number),
        { ValueKind: JsonValueKind.Array } => FormatList(ToList(value)),
        _ => "",
    };

    /// <summary>Список строк из значения настройки; не список — значит пусто.</summary>
    public static IReadOnlyList<string> ToList(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Array } array
            ? [.. array.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String
                    ? item.GetString() ?? ""
                    : item.ToString())
                .Where(item => item.Length > 0)]
            : [];

    /// <summary>Список одной строкой, как его показывают и набирают в окне.</summary>
    public static string FormatList(IEnumerable<string> items) => string.Join(", ", items);

    /// <summary>Разобрать набранное списком: запятые, точки с запятой и пробелы.</summary>
    public static IReadOnlyList<string> ParseList(string written) =>
        [.. written.Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)];

    /// <summary>
    /// Расширения файлов записи — как их ждёт ядро: с точкой и строчными.
    ///
    /// Человек набирает «mp4» или «.MP4», а ядро сравнивает суффикс файла
    /// с этим списком буквально: без точки не совпало бы ничего.
    /// </summary>
    public static IReadOnlyList<string> ParseExtensions(string written) =>
        [.. ParseList(written)
            .Select(item => "." + item.TrimStart('.').ToLowerInvariant())
            .Where(item => item.Length > 1)
            .Distinct()];

    /// <summary>
    /// Что показать в списке, чтобы выбранное было видно.
    ///
    /// Видимую часть редактируемого списка рисует выбранный пункт, а не текст:
    /// значения, которого нет среди пунктов, человек не видит вовсе — поле
    /// выглядит пустым, хотя значение в нём есть. Поэтому своё значение —
    /// модель, которой нет в Ollama, язык, которого нет в нашем списке —
    /// встаёт в начало списка.
    /// </summary>
    public static IReadOnlyList<string> Options(IEnumerable<string> options, string? chosen)
    {
        var written = (chosen ?? "").Trim();
        var all = options.ToList();
        if (written.Length > 0 && !all.Contains(written))
        {
            all.Insert(0, written);
        }

        return all;
    }

    /// <summary>
    /// Осталось ли значение прежним.
    ///
    /// Сравнение идёт по смыслу, а не по тексту: 8192 из файла и 8192 из
    /// числового поля — одно и то же, и отправлять такое «изменение» значило бы
    /// переписывать файл на пустом месте.
    /// </summary>
    public static bool Same(JsonElement? stored, object? written) => (stored, written) switch
    {
        (null, null) => true,
        ({ ValueKind: JsonValueKind.String } text, string other) => text.GetString() == other,
        ({ ValueKind: JsonValueKind.True }, bool flag) => flag,
        ({ ValueKind: JsonValueKind.False }, bool flag) => !flag,
        ({ ValueKind: JsonValueKind.Number } number, int or long or double or float)
            // Дробные приходят из поля с точностью до знака, а в файле лежат как
            // записаны: сравнивать их на точное равенство нельзя.
            => Math.Abs(number.GetDouble() - Convert.ToDouble(written, CultureInfo.InvariantCulture))
               < 1e-9,
        ({ ValueKind: JsonValueKind.Array }, IEnumerable<string> items)
            => ToList(stored).SequenceEqual(items),
        _ => false,
    };

    /// <summary>Целое печатается без дробной части: «8192», а не «8192,0».</summary>
    private static string Number(JsonElement value) =>
        value.TryGetInt64(out var whole)
            ? whole.ToString(CultureInfo.CurrentCulture)
            : value.GetDouble().ToString(CultureInfo.CurrentCulture);
}
