using System.Net.WebSockets;
using System.Text;

namespace Callsum.Obs;

/// <summary>
/// Канал связи с OBS. Вынесен в интерфейс, чтобы разбор протокола можно было
/// проверить тестами без запущенного OBS.
/// </summary>
public interface IObsTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    Task SendAsync(string message, CancellationToken cancellationToken);

    /// <summary>Следующее сообщение или null, если связь закрыта.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);

    Task CloseAsync();
}

/// <summary>Настоящий канал: websocket поверх ClientWebSocket.</summary>
public sealed class WebSocketTransport : IObsTransport
{
    private readonly ClientWebSocket _socket = new();

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        _socket.ConnectAsync(uri, cancellationToken);

    public Task SendAsync(string message, CancellationToken cancellationToken) =>
        _socket.SendAsync(
            Encoding.UTF8.GetBytes(message),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        // Сообщение может прийти несколькими кадрами, поэтому собираем целиком.
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    public async Task CloseAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // Связь уже оборвалась — для закрытия это не ошибка.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _socket.Dispose();
    }
}
