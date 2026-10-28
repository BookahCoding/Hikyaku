using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 47 — the gateway's speak-stream socket (hermes-agent web_routers/audio.py; BookahHermes SpeechStream.kt @ 5f00986b):
/// the reply's text goes in as it grows, PCM comes back sentence by sentence, then "end"; "fallback" means voice the finished
/// reply the old way. Tested over a real WebSocket pair on loopback (the HTTP upgrade itself is the live part), plus the
/// warm-up leases, which must never fail a voice chat.
/// </summary>
public class Checkpoint47SpeechStreamTests
{
    [Fact]
    public void Text_frames_parse_and_junk_is_ignored()
    {
        Assert.Equal(new SpeechStart(22050, 1), SpeechStreamClient.ParseText("""{"type":"start","sample_rate":22050,"channels":1}"""));
        Assert.Equal(new SpeechStart(24000, 1), SpeechStreamClient.ParseText("""{"type":"start"}"""));
        Assert.IsType<SpeechEnd>(SpeechStreamClient.ParseText("""{"type":"end"}"""));
        Assert.IsType<SpeechFallback>(SpeechStreamClient.ParseText("""{"type":"fallback"}"""));
        Assert.Null(SpeechStreamClient.ParseText("""{"type":"other"}"""));
        Assert.Null(SpeechStreamClient.ParseText("not json"));
    }

    [Fact]
    public void The_socket_address_is_the_gateways_own_with_the_profile()
    {
        Assert.Equal("wss://hermes.example/hermes/api/audio/speak-stream?profile=my%20bot",
            SpeechStreamClient.SocketUri(new Uri("https://hermes.example/hermes/"), "my bot").AbsoluteUri);
        Assert.Equal("ws://gateway.example:9119/api/audio/speak-stream", SpeechStreamClient.SocketUri(new Uri("http://gateway.example:9119"), null).AbsoluteUri);
    }

    /// <summary>A connected client/server WebSocket pair over loopback TCP, without the HTTP upgrade.</summary>
    private static async Task<(WebSocket Client, WebSocket Server)> Pair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = await accept;
        listener.Stop();
        return (WebSocket.CreateFromStream(client.GetStream(), false, null, TimeSpan.FromSeconds(30)),
                WebSocket.CreateFromStream(server.GetStream(), true, null, TimeSpan.FromSeconds(30)));
    }

    private static async Task<string> ReceiveText(WebSocket ws)
    {
        var buffer = new byte[4096];
        var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, r.Count);
    }

    [Fact]
    public async Task Text_goes_in_and_audio_comes_out_until_the_end()
    {
        var (client, server) = await Pair();
        await using var stream = new SpeechStream(client);
        await stream.AppendAsync("Hello there. ");
        await stream.AppendAsync("");                                                   // nothing is sent for nothing
        await stream.FinishAsync();
        Assert.Equal("""{"text":"Hello there. "}""", await ReceiveText(server));
        Assert.Equal("""{"done":true}""", await ReceiveText(server));

        await server.SendAsync(Encoding.UTF8.GetBytes("""{"type":"start","sample_rate":24000,"channels":1}"""), WebSocketMessageType.Text, true, default);
        await server.SendAsync(new byte[] { 1, 0, 2, 0 }, WebSocketMessageType.Binary, true, default);
        await server.SendAsync(Encoding.UTF8.GetBytes("""{"type":"end"}"""), WebSocketMessageType.Text, true, default);
        await server.SendAsync(new byte[] { 9, 9 }, WebSocketMessageType.Binary, true, default);   // after the end: not read

        var events = new List<SpeechStreamEvent>();
        await foreach (var e in stream.Events(CancellationToken.None)) events.Add(e);
        Assert.Equal(3, events.Count);
        Assert.Equal(new SpeechStart(24000, 1), events[0]);
        Assert.Equal(new byte[] { 1, 0, 2, 0 }, Assert.IsType<SpeechAudio>(events[1]).Pcm);
        Assert.IsType<SpeechEnd>(events[2]);
    }

    [Fact]
    public async Task Stop_tells_the_gateway_and_a_dropped_socket_just_ends_the_speech()
    {
        var (client, server) = await Pair();
        await using var stream = new SpeechStream(client);
        await stream.StopAsync();
        Assert.Equal("""{"stop":true}""", await ReceiveText(server));

        var (client2, server2) = await Pair();
        await using var dropped = new SpeechStream(client2);
        server2.Abort();
        var events = new List<SpeechStreamEvent>();
        await foreach (var e in dropped.Events(CancellationToken.None)) events.Add(e);   // ends, does not throw
        Assert.Empty(events);
        await dropped.AppendAsync("too late");                                          // a send on a dead socket is quiet
    }

    private sealed class Recorder(HttpStatusCode status) : HttpMessageHandler
    {
        public readonly List<(string Path, string Body)> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.RequestUri!.PathAndQuery, await request.Content!.ReadAsStringAsync(ct)));
            if (status == HttpStatusCode.GatewayTimeout) throw new HttpRequestException("offline");
            return new HttpResponseMessage(status) { Content = new StringContent("{}") };
        }
    }

    [Fact]
    public async Task Warm_up_leases_are_sent_and_never_fail_the_chat()
    {
        var ok = new Recorder(HttpStatusCode.OK);
        using (var auth = new DashboardAuth(new Uri("https://hermes.example/"), ok))
        {
            var api = new AudioApi(auth);
            await api.SetTtsLeaseAsync("voice-1", true, "default", default);
            await api.SetSttLeaseAsync("voice-1", false, null, default);
        }
        Assert.Equal(("/api/audio/tts-lease?profile=default", """{"lease":"voice-1","active":true}"""), ok.Calls[0]);
        Assert.Equal(("/api/audio/stt-lease", """{"lease":"voice-1","active":false}"""), ok.Calls[1]);

        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.InternalServerError, HttpStatusCode.GatewayTimeout })
        {
            using var auth = new DashboardAuth(new Uri("https://hermes.example/"), new Recorder(status));
            await new AudioApi(auth).SetTtsLeaseAsync("voice-1", true, null, default);   // no exception
        }
    }
}
