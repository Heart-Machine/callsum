using Callsum.Core;

namespace Callsum.App;

/// <summary>Строка списка записей: те же данные, но подписанные для окна.</summary>
public sealed class ResultRow
{
    public ResultRow(CallResult result) => Result = result;

    public CallResult Result { get; }

    public string Name => Result.Name;

    public string Folder => Result.Folder;

    /// <summary>Что уже готово в папке — видно, не открывая её.</summary>
    public string Note => Result.HasSummary ? "Протокол и расшифровка" : "Только расшифровка";

    /// <summary>Как строку называет Windows: без этого в списке для экранного
    /// диктора и для средств проверки стояло имя класса.</summary>
    public override string ToString() => $"{Name} — {Note}";
}
