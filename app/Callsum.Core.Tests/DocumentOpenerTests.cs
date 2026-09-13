using Callsum.Core;

namespace Callsum.Core.Tests;

public class DocumentOpenerTests
{
    private const string Path = @"D:\out\созвон 12\summary.md";

    [Fact]
    public void Пустая_настройка_отдаёт_файл_Windows()
    {
        var command = DocumentOpener.Build("", Path);

        Assert.True(command.UseShell);
        Assert.Equal(Path, command.Target);
    }

    [Fact]
    public void Имя_программы_получает_файл_в_кавычках()
    {
        // Без кавычек путь с пробелом дошёл бы до программы обрезанным.
        var command = DocumentOpener.Build("notepad++", Path);

        Assert.False(command.UseShell);
        Assert.Equal("notepad++", command.Target);
        Assert.Equal($"\"{Path}\"", command.Arguments);
    }

    [Fact]
    public void Путь_с_пробелами_не_делится_на_части()
    {
        // Настоящий путь к программе проверяется по диску: делить его по
        // пробелам нельзя, а отличить от команды иначе не получится.
        var program = System.IO.Path.Combine(
            Directory.CreateTempSubdirectory("callsum-opener").FullName, "Открыть файл.exe");
        File.WriteAllText(program, "");

        var command = DocumentOpener.Build(program, Path);

        Assert.Equal(program, command.Target);
        Assert.Equal($"\"{Path}\"", command.Arguments);
    }

    [Fact]
    public void Подстановка_ставит_файл_туда_куда_просили()
    {
        var command = DocumentOpener.Build("code -r {file}", Path);

        Assert.Equal("code", command.Target);
        Assert.Equal($"-r \"{Path}\"", command.Arguments);
    }

    [Fact]
    public void Ссылка_открывается_средствами_Windows_с_кодированным_путём()
    {
        var command = DocumentOpener.Build("obsidian://open?path={file}", Path);

        Assert.True(command.UseShell);
        Assert.StartsWith("obsidian://open?path=", command.Target);
        Assert.DoesNotContain(" ", command.Target);
        Assert.Contains(Uri.EscapeDataString(Path), command.Target);
    }

    [Fact]
    public void Ссылка_без_подстановки_получает_путь_в_конец()
    {
        var command = DocumentOpener.Build("myapp://open/", Path);

        Assert.Equal($"myapp://open/{Uri.EscapeDataString(Path)}", command.Target);
    }
}
