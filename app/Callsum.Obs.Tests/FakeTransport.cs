using System.Text.Json;
using System.Threading.Channels;
using Callsum.Obs;

namespace Callsum.Obs.Tests;

/// <summary>
/// Мини-OBS: отвечает на рукопожатие и на запросы по их requestId.
/// Позволяет проверить разбор протокола без запущенного OBS.
/// </summary>
public sealed class FakeTransport : IObsTransport
{
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();

    public FakeTransport(bool authRequired = true, string? failRequest = null)
    {
        AuthRequired = authRequired;
        FailRequest = failRequest;
    }

    public bool AuthRequired { get; }

    public string? FailRequest { get; }

    public List<JsonDocument> Sent { get; } = [];

    public bool Closed { get; private set; }

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        var authentication = AuthRequired
            ? ", \"authentication\": {\"challenge\": \"вызов\", \"salt\": \"соль\"}"
            : "";
        Push($"{{\"op\": 0, \"d\": {{\"rpcVersion\": 1{authentication}}}}}");
        return Task.CompletedTask;
    }

    public Task SendAsync(string message, CancellationToken cancellationToken)
    {
        var document = JsonDocument.Parse(message);
        Sent.Add(document);
        var operation = document.RootElement.GetProperty("op").GetInt32();

        if (operation == 1)
        {
            Push("{\"op\": 2, \"d\": {\"negotiatedRpcVersion\": 1}}");
        }
        else if (operation == 6)
        {
            var data = document.RootElement.GetProperty("d");
            var requestType = data.GetProperty("requestType").GetString();
            var requestId = data.GetProperty("requestId").GetString();
            var ok = requestType != FailRequest;
            var status = ok
                ? "{\"result\": true, \"code\": 100}"
                : "{\"result\": false, \"code\": 501, \"comment\": \"запись не идёт\"}";
            var response = ok ? ", \"responseData\": {\"outputPath\": \"D:/созвон.mkv\"}" : "";
            Push($"{{\"op\": 7, \"d\": {{\"requestType\": \"{requestType}\", " +
                 $"\"requestId\": \"{requestId}\", \"requestStatus\": {status}{response}}}}}");
        }

        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ChannelClosedException)
        {
            return null;
        }
    }

    public Task CloseAsync()
    {
        Closed = true;
        _incoming.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        CloseAsync();
        return ValueTask.CompletedTask;
    }

    /// <summary>Прислать событие, как это сделал бы OBS.</summary>
    public void PushEvent(string eventType, string eventData) =>
        Push($"{{\"op\": 5, \"d\": {{\"eventType\": \"{eventType}\", \"eventData\": {eventData}}}}}");

    public void Push(string message) => _incoming.Writer.TryWrite(message);
}
