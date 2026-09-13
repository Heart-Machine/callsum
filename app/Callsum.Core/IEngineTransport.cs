using System.Diagnostics;
using System.Text;

namespace Callsum.Core;

/// <summary>
/// Канал связи с ядром. Вынесен в интерфейс, чтобы разбор протокола можно было
/// проверить тестами, не запуская настоящее ядро на два гигабайта.
/// </summary>
public interface IEngineTransport : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);

    Task SendAsync(string line, CancellationToken cancellationToken);

    /// <summary>Следующая строка событий или null, когда ядро закрыло вывод.</summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>Строка из потока ошибок ядра — туда идёт его журнал.</summary>
    event Action<string>? Diagnostics;
}

/// <summary>Настоящее ядро: отдельный процесс, команды и события через его потоки.</summary>
public sealed class ProcessTransport : IEngineTransport
{
    private readonly string _executable;
    private Process? _process;

    public ProcessTransport(string executable) => _executable = executable;

    public event Action<string>? Diagnostics;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(_executable)
        {
            // Ядро работает мотором: команды в стандартный ввод, события в вывод.
            Arguments = "serve",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            WorkingDirectory = Path.GetDirectoryName(_executable) ?? Environment.CurrentDirectory,
        };

        var process = Process.Start(info)
            ?? throw new EngineException($"Не удалось запустить ядро: {_executable}");

        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                Diagnostics?.Invoke(args.Data);
            }
        };
        process.BeginErrorReadLine();

        _process = process;
        return Task.CompletedTask;
    }

    public async Task SendAsync(string line, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new EngineException("Ядро не запущено");
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw new EngineException("Ядро не запущено");
        return await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                // Ядро доводит начатое распознавание до конца, поэтому даём время,
                // но не ждём вечно: приложение не должно зависать при выходе.
                process.StandardInput.Close();
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Процесс уже завершился сам.
            }
        }
        finally
        {
            process.Dispose();
        }
    }
}
