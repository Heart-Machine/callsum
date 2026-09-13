using System.Text.Json;

namespace Callsum.Core;

/// <summary>Ошибка запуска ядра или работы с ним.</summary>
public sealed class EngineException : Exception
{
    public EngineException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Ядро обработки, запущенное отдельным процессом.
///
/// Команды уходят строками JSON в его стандартный ввод, события приходят
/// оттуда же построчно. Ядро живёт всё время работы приложения: модель
/// распознавания грузится в видеопамять секунды, и перезапускать процесс
/// на каждый созвон нельзя.
/// </summary>
public sealed class EngineClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IEngineTransport _transport;
    private readonly CancellationTokenSource _lifetime = new();

    private Task? _reader;
    private int _lastId;

    public EngineClient(IEngineTransport transport)
    {
        _transport = transport;
        _transport.Diagnostics += message => Diagnostics?.Invoke(message);
    }

    public EngineClient(string executable) : this(new ProcessTransport(executable)) { }

    /// <summary>Событие от ядра — уже разобранное.</summary>
    public event Action<EngineEvent>? EventReceived;

    /// <summary>Строки из потока ошибок ядра: его собственный журнал.</summary>
    public event Action<string>? Diagnostics;

    /// <summary>Ядро закрыло вывод — процесс завершился сам.</summary>
    public event Action? Stopped;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
        _reader = Task.Run(ReadLoopAsync, CancellationToken.None);
    }

    /// <summary>Поставить запись в обработку. Возвращает номер задания.</summary>
    public Task<string> ProcessAsync(string path, bool force = false, CancellationToken cancellationToken = default)
        => SendAsync(new { cmd = "process", path, force }, cancellationToken);

    public Task<string> SummarizeAsync(string transcript, CancellationToken cancellationToken = default)
        => SendAsync(new { cmd = "summarize", transcript }, cancellationToken);

    public Task<string> DoctorAsync(CancellationToken cancellationToken = default)
        => SendAsync(new { cmd = "doctor" }, cancellationToken);

    private async Task<string> SendAsync(object command, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _lastId).ToString();
        var payload = JsonSerializer.SerializeToNode(command, SerializerOptions)!.AsObject();
        payload["id"] = id;
        await _transport.SendAsync(payload.ToJsonString(SerializerOptions), cancellationToken)
            .ConfigureAwait(false);
        return id;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            await ReadEventsAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Молча умерший читатель — худший вид поломки: приложение думает,
            // что обработка идёт, а событий больше нет. Пусть будет видно.
            Diagnostics?.Invoke($"! Чтение событий ядра прервано: {exception}");
        }

        if (!_lifetime.IsCancellationRequested)
        {
            Stopped?.Invoke();
        }
    }

    private async Task ReadEventsAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _transport.ReadLineAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            if (line is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var message = EngineEvent.Parse(line);
            if (message is null)
            {
                // Не JSON — значит, в поток событий попало постороннее.
                // Показываем как диагностику, но работу не прерываем.
                Diagnostics?.Invoke(line);
                continue;
            }

            try
            {
                EventReceived?.Invoke(message);
            }
            catch
            {
                // Ошибка подписчика не должна останавливать чтение событий.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await _transport.SendAsync("{\"cmd\": \"shutdown\"}", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is EngineException or IOException or ObjectDisposedException)
            {
                // Ядро уже не слушает — значит, и прощаться не с кем.
            }
        }

        _lifetime.Cancel();
        await _transport.DisposeAsync().ConfigureAwait(false);

        if (_reader is not null)
        {
            await Task.WhenAny(_reader, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }
}
