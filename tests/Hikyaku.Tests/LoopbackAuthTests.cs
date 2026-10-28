using System.Net;
using System.Text;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Real loopback HTTP (no mocks) for dashboard password-login + ws-ticket cookie round-trip.</summary>
public class LoopbackAuthTests
{
    [Fact]
    public async Task LoopbackPasswordLoginMintsTicketWithSessionCookie()
    {
        var listener = TestLoopback.Start(18900, 19000, out var port);

        using var run = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!run.IsCancellationRequested)
            {
                var ctx = await listener.GetContextAsync().WaitAsync(run.Token);
                var path = ctx.Request.Url!.AbsolutePath;
                if (path == "/auth/password-login")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    Assert.Contains("\"username\":\"loop-user\"", body);
                    Assert.DoesNotContain("loop-pass", ctx.Request.Url.ToString());
                    ctx.Response.Headers.Add("Set-Cookie", "hermes_session_at=LOOPAT; Path=/; HttpOnly");
                    ctx.Response.Headers.Add("Set-Cookie", "hermes_session_rt=LOOPRT; Path=/; HttpOnly");
                    var bytes = Encoding.UTF8.GetBytes("""{"ok":true}""");
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(bytes);
                }
                else if (path == "/api/auth/ws-ticket")
                {
                    var cookie = ctx.Request.Headers["Cookie"] ?? "";
                    Assert.Contains("hermes_session_at=LOOPAT", cookie);
                    var bytes = Encoding.UTF8.GetBytes("""{"ticket":"LOOP-TICKET","ttl_seconds":30}""");
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(bytes);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                }
                ctx.Response.Close();
            }
        }, run.Token);

        try
        {
            using var auth = new DashboardAuth(new Uri($"http://127.0.0.1:{port}/"));
            await auth.SignInAsync("basic", "loop-user", "loop-pass", default);
            Assert.True(auth.HasSession);
            var ticket = await auth.MintWsTicketAsync(default);
            Assert.Equal("LOOP-TICKET", ticket);
            Assert.DoesNotContain("LOOP-TICKET", DashboardAuth.WebSocketUri(auth.BaseUri).ToString());
        }
        finally
        {
            run.Cancel();
            listener.Stop();
            try { await server; } catch { /* cancelled */ }
            listener.Close();
        }
    }
}
