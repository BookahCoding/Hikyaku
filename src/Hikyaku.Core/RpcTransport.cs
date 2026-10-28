using System.Threading.Channels;

namespace Hikyaku.Core;

/// <summary>WS close codes from hermes_cli/web_routers/chat_ws.py (BookahHermes RpcTransport.kt).</summary>
public static class GatewayCloseCodes
{
    public const int TicketRejected = 4401;
    public const int RequestGuard = 4403;
}

public sealed class TransportClosedException(int? code, string? reason)
    : Exception($"WebSocket closed (code={code}{(reason is null ? "" : $", {reason}")})")
{
    public int? Code { get; } = code;
    public string? Reason { get; } = reason;
}

/// <summary>Text-frame pipe abstracted so JSON-RPC is testable without a network.</summary>
public interface IRpcTransport
{
    IAsyncEnumerable<string> ReadFramesAsync(CancellationToken ct);
    Task SendAsync(string text, CancellationToken ct);
    Task CloseAsync(int code = 1000, string reason = "", CancellationToken ct = default);
}

/// <summary>In-memory RPC transport for wire-fixture tests (mirrors BookahHermes FakeTransport).</summary>
public sealed class FakeRpcTransport : IRpcTransport
{
    private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
    private TransportClosedException? _close;
    private readonly List<string> _sent = new();
    private readonly object _gate = new();

    public const string ReadyFrame =
        """{"jsonrpc":"2.0","method":"event","params":{"type":"gateway.ready","payload":{"heartbeat":false,"replay_epoch":"e1"}}}""";

    public IReadOnlyList<string> Sent
    {
        get { lock (_gate) return _sent.ToArray(); }
    }

    public void Push(string json) => _inbound.Writer.TryWrite(json);

    public void ServerClose(int? code, string? reason = null)
    {
        _close = new TransportClosedException(code, reason);
        _inbound.Writer.TryComplete();
    }

    public async IAsyncEnumerable<string> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (await _inbound.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_inbound.Reader.TryRead(out var frame))
                yield return frame;
        }
        throw _close ?? new TransportClosedException(null, "closed");
    }

    public Task SendAsync(string text, CancellationToken ct)
    {
        lock (_gate) _sent.Add(text);
        return Task.CompletedTask;
    }

    public Task CloseAsync(int code = 1000, string reason = "", CancellationToken ct = default)
    {
        ServerClose(code, reason);
        return Task.CompletedTask;
    }

    public string? FirstCall(string method) =>
        Sent.Select(ParseMethod).FirstOrDefault(m => m.Method == method) is { } hit ? hit.Raw : null;

    public int CountCalls(string method) => Sent.Select(ParseMethod).Count(m => m.Method == method);

    public static (string? Method, string Raw) ParseMethod(string raw)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("method", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String)
                return (m.GetString(), raw);
        }
        catch (System.Text.Json.JsonException) { }
        return (null, raw);
    }
}
