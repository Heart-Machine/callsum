using System.Threading.Channels;
using Callsum.Core;

namespace Callsum.Core.Tests;

/// <summary>
/// Подставное ядро: принимает команды и отдаёт заготовленные события.
/// Позволяет проверить протокол, не запуская сборку на два гигабайта.
/// </summary>
public sealed class FakeEngineTransport : IEngineTransport
{
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>();

    public List<string> Commands { get; } = [];

    public bool Disposed { get; private set; }

    public event Action<string>? Diagnostics;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Push("""{"event": "ready", "version": "1.0.0", "device": "cuda", "compute_type": "float16"}""");
        return Task.CompletedTask;
    }

    public Task SendAsync(string line, CancellationToken cancellationToken)
    {
        Commands.Add(line);
        return Task.CompletedTask;
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _outgoing.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _outgoing.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>Прислать строку так, как это сделало бы ядро.</summary>
    public void Push(string line) => _outgoing.Writer.TryWrite(line);

    /// <summary>Написать в поток ошибок — туда ядро пишет свой журнал.</summary>
    public void PushDiagnostics(string line) => Diagnostics?.Invoke(line);

    /// <summary>Закрыть вывод: так выглядит завершившийся процесс ядра.</summary>
    public void Close() => _outgoing.Writer.TryComplete();
}
