using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Hikyaku.Core;

/// <summary>What the gateway sends back while it voices a reply.</summary>
public abstract record SpeechStreamEvent;
/// <summary>Audio follows: 16-bit little-endian PCM at <see cref="SampleRate"/>.</summary>
public sealed record SpeechStart(int SampleRate, int Channels) : SpeechStreamEvent;
public sealed record SpeechAudio(byte[] Pcm) : SpeechStreamEvent;
/// <summary>Everything said has been sent.</summary>
public sealed record SpeechEnd : SpeechStreamEvent;
/// <summary>The gateway made no audio at all; voice the finished reply the old way (<c>/api/audio/speak</c>).</summary>
public sealed record SpeechFallback : SpeechStreamEvent;

/// <summary>One reply's speech, voiced while the reply is still being written (<c>/api/audio/speak-stream</c>).</summary>
public interface ISpeechStream : IAsyncDisposable
{
    /// <summary>The gateway's answers; ends after <see cref="SpeechEnd"/> or <see cref="SpeechFallback"/>, or when the socket drops.</summary>
    IAsyncEnumerable<SpeechStreamEvent> Events(CancellationToken ct);
    /// <summary>More of the reply, as written; the gateway cuts it into sentences.</summary>
    Task AppendAsync(string text);
    /// <summary>The reply is complete: what is left is voiced, then <see cref="SpeechEnd"/>.</summary>
    Task FinishAsync();
    /// <summary>Stops at once: nothing more is voiced.</summary>
    Task StopAsync();
}

/// <summary>
/// The speak-stream socket (hermes-agent <c>web_routers/audio.py</c> <c>speak_stream_ws</c>; BookahHermes SpeechStream.kt @ 5f00986b).
/// It opens with a fresh single-use ticket in the subprotocol list, as <c>/api/ws</c> does (an unnamed ticket opens any socket
/// except the host terminal), so the ticket never lands in a URL, and with <c>?profile=</c> so the profile's own voice speaks.
/// </summary>
public static class SpeechStreamClient
{
    /// <summary>Opens the socket; null when this gateway cannot (an older one, a refused upgrade, no network).</summary>
    public static async Task<ISpeechStream?> OpenAsync(DashboardAuth auth, string? profile, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        try
        {
            var ticket = await auth.MintWsTicketAsync(ct).ConfigureAwait(false);
            ws.Options.AddSubProtocol("hermes-gateway-v1");
            ws.Options.AddSubProtocol("hermes-gateway-ticket." + ticket);
            var uri = SocketUri(auth.BaseUri, profile);
            foreach (var h in auth.AccessHeadersFor(uri)) ws.Options.SetRequestHeader(h.Key, h.Value);   // Cloudflare Access
            await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            return new SpeechStream(ws);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or AuthFailedException or IOException
                                       || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            ws.Dispose();
            return null;
        }
    }

    public static Uri SocketUri(Uri baseUri, string? profile)
    {
        var b = new UriBuilder(new Uri(DashboardAuth.NormalizeBase(baseUri), "api/audio/speak-stream"))
        {
            Scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Query = string.IsNullOrWhiteSpace(profile) ? "" : "profile=" + Uri.EscapeDataString(profile)
        };
        if (b.Uri.IsDefaultPort) b.Port = -1;
        return b.Uri;
    }

    /// <summary>The gateway's text frames: <c>start</c> with the audio's rate, <c>end</c>, <c>fallback</c>; anything else is ignored.</summary>
    public static SpeechStreamEvent? ParseText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
                return null;
            int Positive(string name, int fallback) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0 ? n : fallback;
            return t.GetString() switch
            {
                "start" => new SpeechStart(Positive("sample_rate", 24_000), Positive("channels", 1)),
                "end" => new SpeechEnd(),
                "fallback" => new SpeechFallback(),
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }
}

/// <summary><see cref="ISpeechStream"/> over a socket. A dropped socket ends the speech with what was said; sends on it are quiet.</summary>
public sealed class SpeechStream(WebSocket ws) : ISpeechStream
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _send = new(1, 1);

    public async IAsyncEnumerable<SpeechStreamEvent> Events([EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) yield break;
                    if (message.Length + result.Count > MaxMessageBytes) yield break;
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException
                                           || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                yield break;                                   // a dropped socket ends the speech
            }
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                yield return new SpeechAudio(message.ToArray());
                continue;
            }
            var e = SpeechStreamClient.ParseText(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            if (e is null) continue;
            yield return e;
            if (e is SpeechEnd or SpeechFallback) yield break;
        }
    }

    public Task AppendAsync(string text) => text.Length == 0 ? Task.CompletedTask : SendAsync(JsonSerializer.Serialize(new { text }));

    public Task FinishAsync() => SendAsync("""{"done":true}""");

    public Task StopAsync() => SendAsync("""{"stop":true}""");

    private async Task SendAsync(string json)
    {
        await _send.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { /* gone: nothing to say back */ }
        finally { _send.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        try { ws.Abort(); } catch (ObjectDisposedException) { }
        ws.Dispose();
        _send.Dispose();
        return ValueTask.CompletedTask;
    }
}
