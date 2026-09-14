using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, TaskCompletionSource<EngineEvent>> _waiting = new();

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

    /// <summary>Проверить окружение и дождаться отчёта.</summary>
    public Task<EngineEvent.Doctor> GetDoctorAsync(CancellationToken cancellationToken = default)
        => AskAsync<EngineEvent.Doctor>(new { cmd = "doctor" }, cancellationToken);

    /// <summary>
    /// Спросить, какие модели установлены в Ollama.
    ///
    /// Адрес передаётся свой, когда его только что поменяли в окне: список
    /// должен относиться к тому, что человек вписал, а не к сохранённому.
    /// </summary>
    public Task<EngineEvent.Models> GetOllamaModelsAsync(
        string? host = null, CancellationToken cancellationToken = default)
        => AskAsync<EngineEvent.Models>(new { cmd = "models", host }, cancellationToken);

    /// <summary>Спросить настройки и дождаться ответа.</summary>
    public Task<EngineEvent.Settings> GetSettingsAsync(CancellationToken cancellationToken = default)
        => AskAsync<EngineEvent.Settings>(new { cmd = "settings" }, cancellationToken);

    /// <summary>
    /// Сохранить изменённые значения и дождаться, пока ядро перечитает файл.
    ///
    /// Отправляются только изменения, по разделам: файл правится по одному
    /// значению, и присылать целиком всё, что окно когда-то прочитало, значило
    /// бы переписывать чужие правки, сделанные тем временем руками.
    /// </summary>
    public Task<EngineEvent.Settings> SaveSettingsAsync(
        IReadOnlyDictionary<string, Dictionary<string, object?>> values,
        CancellationToken cancellationToken = default)
        => AskAsync<EngineEvent.Settings>(new { cmd = "settings_set", values }, cancellationToken);

    /// <summary>
    /// Отправить команду и дождаться ответа именно на неё.
    ///
    /// Ответы сопоставляются по номеру задания: ядро выполняет команды по
    /// очереди, и пока идёт распознавание часового созвона, ответ на «покажи
    /// настройки» придёт много позже, чем его отправили.
    /// </summary>
    private async Task<T> AskAsync<T>(object command, CancellationToken cancellationToken)
        where T : EngineEvent
    {
        var waiter = new TaskCompletionSource<EngineEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var id = await SendAsync(command, cancellationToken, waiter).ConfigureAwait(false);

        try
        {
            using var registration = cancellationToken.Register(
                () => waiter.TrySetCanceled(cancellationToken));
            var answer = await waiter.Task.ConfigureAwait(false);
            return answer switch
            {
                T expected => expected,
                EngineEvent.Failed failed => throw new EngineException(failed.Message),
                _ => throw new EngineException($"Ядро ответило не тем, что ожидалось: {answer}"),
            };
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }

    private async Task<string> SendAsync(
        object command,
        CancellationToken cancellationToken,
        TaskCompletionSource<EngineEvent>? waiter = null)
    {
        var id = Interlocked.Increment(ref _lastId).ToString();
        if (waiter is not null)
        {
            // Ожидание заводится до отправки: ответ может прийти раньше, чем
            // вернётся управление из записи в трубу.
            _waiting[id] = waiter;
        }

        var payload = JsonSerializer.SerializeToNode(command, SerializerOptions)!.AsObject();
        payload["id"] = id;
        try
        {
            await _transport.SendAsync(payload.ToJsonString(SerializerOptions), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            _waiting.TryRemove(id, out _);
            throw;
        }

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

        // Ждущие ответа должны узнать, что его уже не будет: иначе окно
        // настроек останется с вечной надписью «читаю настройки…».
        FailWaiting(new EngineException("Ядро обработки завершилось, не ответив"));

        if (!_lifetime.IsCancellationRequested)
        {
            Stopped?.Invoke();
        }
    }

    private void FailWaiting(Exception reason)
    {
        foreach (var id in _waiting.Keys)
        {
            if (_waiting.TryRemove(id, out var waiter))
            {
                waiter.TrySetException(reason);
            }
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

            Answer(message);

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

    /// <summary>Отдать событие тому, кто ждёт ответа именно на эту команду.</summary>
    private void Answer(EngineEvent message)
    {
        // Ход работы и строки журнала тоже помечены номером задания, но ответом
        // на команду не являются: ждущий должен дождаться настоящего ответа.
        if (message is EngineEvent.Progress or EngineEvent.Log)
        {
            return;
        }

        if (message.Id is { Length: > 0 } id && _waiting.TryRemove(id, out var waiter))
        {
            waiter.TrySetResult(message);
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
