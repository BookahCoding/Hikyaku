using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using System.Web;

namespace Hikyaku.Core;

/// <summary>Where the gateway sends the browser back to (RFC 8252 loopback redirect).</summary>
public interface ILoopbackReceiver
{
    Task<ILoopbackListener> StartAsync(CancellationToken ct);
}

public interface ILoopbackListener : IDisposable
{
    /// <summary><c>http://127.0.0.1:&lt;port&gt;/callback</c>.</summary>
    string RedirectUri { get; }

    /// <summary>The query of the next valid request to the callback path.</summary>
    Task<IReadOnlyDictionary<string, string>> AwaitCallbackAsync(CancellationToken ct);
}

/// <summary>
/// 127.0.0.1-only <see cref="HttpListener"/>. Answers every request; delivers only GET /callback with a loopback
/// Host header and a bounded query. The reply page is static and never reflects the query.
/// </summary>
public sealed class HttpListenerLoopbackReceiver : ILoopbackReceiver
{
    public const string CallbackPath = "/callback";
    public const int MaxQueryChars = 8192;
    public const int MaxParams = 16;
    public const int MaxValueChars = 4096;

    public Task<ILoopbackListener> StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Exception? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var http = new HttpListener();
            http.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                http.Start();
                return Task.FromResult<ILoopbackListener>(new Listener(http, port));
            }
            catch (HttpListenerException ex)
            {
                last = ex; // port taken between probe and bind: try another
                http.Close();
            }
        }
        throw new AuthFailedException("failed", "Could not open a local port for the browser to return to: " + last?.Message);
    }

    private sealed class Listener : ILoopbackListener
    {
        private const string Page =
            "<!doctype html><meta charset=utf-8><title>Hikyaku</title>" +
            "<body style=\"font-family:system-ui;margin:3em\"><h2>Signed in</h2><p>You can close this tab and return to Hikyaku.</p>";

        private readonly HttpListener _http;
        private readonly int _port;
        private readonly Channel<IReadOnlyDictionary<string, string>> _queue =
            Channel.CreateBounded<IReadOnlyDictionary<string, string>>(
                new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropWrite });
        private int _disposed;

        public Listener(HttpListener http, int port)
        {
            _http = http;
            _port = port;
            RedirectUri = $"http://127.0.0.1:{port}{CallbackPath}";
            _ = Task.Run(PumpAsync);
        }

        public string RedirectUri { get; }

        public async Task<IReadOnlyDictionary<string, string>> AwaitCallbackAsync(CancellationToken ct)
        {
            try { return await _queue.Reader.ReadAsync(ct).ConfigureAwait(false); }
            catch (ChannelClosedException) { throw new ObjectDisposedException(nameof(HttpListenerLoopbackReceiver)); }
        }

        private async Task PumpAsync()
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                HttpListenerContext ctx;
                try { ctx = await _http.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { return; } // listener closed
                try { Handle(ctx); }
                catch (Exception) { try { ctx.Response.Abort(); } catch { /* already gone */ } }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            if (!string.Equals(req.Url?.AbsolutePath, CallbackPath, StringComparison.Ordinal)) { Reply(ctx, 404, "Not found"); return; }
            if (req.HttpMethod != "GET") { Reply(ctx, 405, "Method not allowed"); return; }
            if (req.Headers["Host"] != $"127.0.0.1:{_port}") { Reply(ctx, 421, "Misdirected request"); return; }
            var raw = req.Url?.Query ?? "";
            if (raw.Length > MaxQueryChars) { Reply(ctx, 414, "URI too long"); return; }

            var parsed = HttpUtility.ParseQueryString(raw);
            if (parsed.Count > MaxParams) { Reply(ctx, 400, "Too many parameters"); return; }
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in parsed.AllKeys)
            {
                if (key is null) continue;
                var value = parsed[key] ?? "";
                if (value.Length > MaxValueChars) { Reply(ctx, 400, "Value too long"); return; }
                map[key] = value;
            }
            Reply(ctx, 200, Page, html: true);
            _queue.Writer.TryWrite(map);
        }

        private static void Reply(HttpListenerContext ctx, int status, string body, bool html = false)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var r = ctx.Response;
            r.StatusCode = status;
            r.ContentType = html ? "text/html; charset=utf-8" : "text/plain; charset=utf-8";
            r.Headers["Cache-Control"] = "no-store";
            r.Headers["Referrer-Policy"] = "no-referrer";
            r.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'";
            r.ContentLength64 = bytes.Length;
            r.OutputStream.Write(bytes, 0, bytes.Length);
            r.Close();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _queue.Writer.TryComplete();
            try { _http.Close(); } catch { /* already closed */ }
        }
    }
}

/// <summary>
/// Browser sign-in orchestration (BrowserSignIn.kt): open the gateway's authorize page, accept only the callback
/// that carries this attempt's <c>state</c>, redeem the code with the verifier that never left the app. One attempt
/// owns its verifier, state and listener; all are released on success, failure, timeout and cancellation.
/// </summary>
public sealed class NativeSignIn(
    DashboardAuth auth, ILoopbackReceiver loopback, TimeSpan? timeout = null)
{
    /// <summary>The gateway forgets an unfinished sign-in after 10 minutes.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    public async Task SignInAsync(string? provider, Action<Uri> openBrowser, CancellationToken ct)
    {
        using var listener = await loopback.StartAsync(ct).ConfigureAwait(false);
        var verifier = Pkce.NewVerifier();
        var state = Pkce.NewState();
        var url = auth.NativeAuthorizeUri(provider, Pkce.Challenge(verifier), listener.RedirectUri, state);
        if (!BrowserLaunchPolicy.IsAllowed(url, auth.BaseUri))
            throw new AuthFailedException("failed", "Refusing to open a sign-in page outside the configured gateway.");

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(timeout ?? DefaultTimeout);
        openBrowser(url);

        string code;
        try
        {
            while (true)
            {
                var q = await listener.AwaitCallbackAsync(wait.Token).ConfigureAwait(false);
                // Anything else reaching the port (old page reload, another app) lacks this attempt's state.
                if (!q.TryGetValue("state", out var got) || !FixedTimeEquals(got, state)) continue;
                if (q.TryGetValue("error", out var err) && !string.IsNullOrEmpty(err))
                    throw new AuthFailedException("denied", "The sign-in was not completed in the browser.");
                if (q.TryGetValue("code", out var c) && !string.IsNullOrEmpty(c)) { code = c; break; }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AuthFailedException("timeout", "The sign-in took too long. Try again.");
        }

        listener.Dispose();
        await auth.RedeemNativeCodeAsync(code, verifier, ct).ConfigureAwait(false);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
