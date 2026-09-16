using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Callsum.Obs;

/// <summary>Ошибка связи с OBS или отказ на запрос.</summary>
public sealed class ObsException : Exception
{
    public ObsException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Событие от OBS: тип и его данные.</summary>
public sealed record ObsEvent(string Type, JsonElement Data);

/// <summary>
/// Клиент протокола obs-websocket 5: JSON поверх websocket.
///
/// Сообщения помечены полем op: 0 — приветствие сервера, 1 — наш ответ на него,
/// 2 — подтверждение, 5 — событие, 6 — запрос, 7 — ответ на запрос. Запросы и
/// события идут по одному соединению: фоновая задача читает сообщения, ответы
/// раздаёт ожидающим по requestId, события — подписчикам.
/// </summary>
public sealed class ObsClient : IAsyncDisposable
{
    /// <summary>Битовая маска подписок: события вывода, в том числе записи.</summary>
    public const int SubscriptionOutputs = 1 << 6;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Русские символы в путях не должны превращаться в \uXXXX: так сообщения
        // читаемы в журнале, а OBS одинаково принимает оба варианта.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ObsSettings _settings;
    private readonly Func<IObsTransport> _transportFactory;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();

    private IObsTransport? _transport;
    private Task? _reader;

    public ObsClient(ObsSettings settings, Func<IObsTransport>? transportFactory = null)
    {
        _settings = settings;
        _transportFactory = transportFactory ?? (() => new WebSocketTransport());
    }

    /// <summary>Сколько ждём ответ на запрос.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public bool Connected => _transport is not null;

    public event Action<ObsEvent>? EventReceived;

    /// <summary>Соединение оборвалось не по нашей воле.</summary>
    public event Action? Disconnected;

    /// <summary>Ответ на вызов сервера: base64(sha256(base64(sha256(пароль+соль)) + вызов)).</summary>
    public static string BuildAuthentication(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var transport = _transportFactory();
        try
        {
            await transport.ConnectAsync(
                new Uri($"ws://{_settings.Host}:{_settings.Port}"), cancellationToken).ConfigureAwait(false);
            await IdentifyAsync(transport, cancellationToken).ConfigureAwait(false);
        }
        catch (ObsException)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            var reason = $"Не удалось подключиться к OBS на {_settings.Host}:{_settings.Port}: {exception.Message}. " +
                         $"Проверьте, что OBS запущен. {ObsSettings.SetupHint}";
            throw new ObsException(reason, exception);
        }

        _transport = transport;
        _reader = Task.Run(() => ReadLoopAsync(transport), CancellationToken.None);
    }

    private async Task IdentifyAsync(IObsTransport transport, CancellationToken cancellationToken)
    {
        var hello = await ReceiveMessageAsync(transport, cancellationToken).ConfigureAwait(false)
            ?? throw new ObsException("OBS закрыл соединение до приветствия");
        if (GetOperation(hello) != 0)
        {
            throw new ObsException("Ожидалось приветствие от OBS, пришло другое сообщение");
        }

        var payload = new Dictionary<string, object>
        {
            ["rpcVersion"] = 1,
            ["eventSubscriptions"] = SubscriptionOutputs,
        };

        var data = hello.RootElement.GetProperty("d");
        if (data.TryGetProperty("authentication", out var authentication))
        {
            if (string.IsNullOrEmpty(_settings.Password))
            {
                throw new ObsException("OBS требует пароль websocket, а он не найден");
            }

            payload["authentication"] = BuildAuthentication(
                _settings.Password,
                authentication.GetProperty("salt").GetString() ?? "",
                authentication.GetProperty("challenge").GetString() ?? "");
        }

        await transport.SendAsync(
            JsonSerializer.Serialize(new { op = 1, d = payload }, SerializerOptions),
            cancellationToken).ConfigureAwait(false);

        var answer = await ReceiveMessageAsync(transport, cancellationToken).ConfigureAwait(false)
            ?? throw new ObsException("OBS закрыл соединение, не подтвердив подключение");
        if (GetOperation(answer) != 2)
        {
            throw new ObsException("OBS не принял подключение");
        }
    }

    /// <summary>Отправить запрос и дождаться ответа. Возвращает responseData.</summary>
    public async Task<JsonElement> RequestAsync(
        string requestType,
        object? requestData = null,
        CancellationToken cancellationToken = default)
    {
        var transport = _transport ?? throw new ObsException("Нет подключения к OBS");
        var requestId = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = waiter;

        try
        {
            var message = JsonSerializer.Serialize(new
            {
                op = 6,
                d = new
                {
                    requestType,
                    requestId,
                    requestData = requestData ?? new { },
                },
            }, SerializerOptions);

            await transport.SendAsync(message, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(RequestTimeout);
            await using var registration = timeout.Token.Register(() => waiter.TrySetCanceled()).ConfigureAwait(false);
            return await waiter.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ObsException($"OBS не ответил на {requestType} за {RequestTimeout.TotalSeconds:0} с");
        }
        catch (ObsException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ObsException($"Не удалось отправить {requestType}: {exception.Message}", exception);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task ReadLoopAsync(IObsTransport transport)
    {
        while (!_lifetime.IsCancellationRequested)
        {
            var raw = await transport.ReceiveAsync(_lifetime.Token).ConfigureAwait(false);
            if (raw is null)
            {
                break;
            }

            JsonDocument message;
            try
            {
                message = JsonDocument.Parse(raw);
            }
            catch (JsonException)
            {
                continue;
            }

            using (message)
            {
                switch (GetOperation(message))
                {
                    case 7:
                        CompleteRequest(message.RootElement.GetProperty("d"));
                        break;
                    case 5:
                        DispatchEvent(message.RootElement.GetProperty("d"));
                        break;
                }
            }
        }

        // Разбудить всех, кто ждёт ответа: иначе они провисят до таймаута.
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new ObsException("Связь с OBS потеряна"));
        }

        if (!_lifetime.IsCancellationRequested)
        {
            _transport = null;
            Disconnected?.Invoke();
        }
    }

    private void CompleteRequest(JsonElement payload)
    {
        var requestId = payload.GetProperty("requestId").GetString() ?? "";
        if (!_pending.TryRemove(requestId, out var waiter))
        {
            return;
        }

        var status = payload.GetProperty("requestStatus");
        if (status.GetProperty("result").GetBoolean())
        {
            // Данные переживают Dispose документа только как копия.
            var data = payload.TryGetProperty("responseData", out var response)
                ? response.Clone()
                : default;
            waiter.TrySetResult(data);
            return;
        }

        var comment = status.TryGetProperty("comment", out var text) ? text.GetString() : null;
        var requestType = payload.GetProperty("requestType").GetString();
        var code = status.GetProperty("code").GetInt32();
        waiter.TrySetException(new ObsException(
            $"{requestType}: код {code}" + (string.IsNullOrEmpty(comment) ? "" : $", {comment}")));
    }

    private void DispatchEvent(JsonElement payload)
    {
        var type = payload.GetProperty("eventType").GetString() ?? "";
        var data = payload.TryGetProperty("eventData", out var value) ? value.Clone() : default;
        try
        {
            EventReceived?.Invoke(new ObsEvent(type, data));
        }
        catch
        {
            // Ошибка подписчика не должна рвать соединение.
        }
    }

    private static async Task<JsonDocument?> ReceiveMessageAsync(
        IObsTransport transport, CancellationToken cancellationToken)
    {
        var raw = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        return raw is null ? null : JsonDocument.Parse(raw);
    }

    private static int GetOperation(JsonDocument message) =>
        message.RootElement.TryGetProperty("op", out var op) && op.TryGetInt32(out var value) ? value : -1;

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        var transport = _transport;
        _transport = null;
        if (transport is not null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        if (_reader is not null)
        {
            await Task.WhenAny(_reader, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }
}
