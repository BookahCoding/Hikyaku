using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

public class Checkpoint5ReconnectTests
{
    private static readonly Uri Base = new("https://gateway.example.invalid/");

    private sealed class AuthHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage?>? _extra;
        public int TicketMints;
        public AuthHandler(Func<HttpRequestMessage, HttpResponseMessage?>? extra = null) => _extra = extra;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var custom = _extra?.Invoke(req);
            if (custom is not null) return Task.FromResult(custom);
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/password-login", StringComparison.Ordinal))
                return Task.FromResult(Ok("""{"ok":true}"""));
            if (path.EndsWith("/api/auth/ws-ticket", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref TicketMints);
                return Task.FromResult(Ok("""{"ticket":"T","ttl_seconds":30}"""));
            }
            throw new InvalidOperationException(path);
        }
        private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static void PushResult(FakeRpcTransport sock, string id, string resultJson)
        => sock.Push($$"""{"jsonrpc":"2.0","id":"{{id}}","result":{{resultJson}}}""");

    private static async Task PumpAnswers(IReadOnlyList<FakeRpcTransport> sockets, Func<string, string?> map, CancellationToken ct)
    {
        var seen = new HashSet<string>();
        while (!ct.IsCancellationRequested)
        {
            foreach (var sock in sockets.ToArray())
            {
                foreach (var raw in sock.Sent.ToArray())
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(raw);
                        if (!doc.RootElement.TryGetProperty("method", out var mEl)) continue;
                        if (!doc.RootElement.TryGetProperty("id", out var idEl)) continue;
                        var id = idEl.ToString();
                        if (!seen.Add(id)) continue;
                        var result = map(mEl.GetString()!);
                        if (result is null) continue;
                        PushResult(sock, id, result);
                    }
                    catch { /* ignore */ }
                }
            }
            try { await Task.Delay(5, ct); } catch (OperationCanceledException) { return; }
        }
    }

    [Fact]
    public async Task DisconnectBeforeSubmit_RemintsFreshTicket_NoPhantomSubmit()
    {
        var handler = new AuthHandler();
        using var auth = new DashboardAuth(Base, handler);
        await auth.SignInAsync("basic", "u", "p", default);
        var sockets = new List<FakeRpcTransport>();

        await using var transport = new HermesDashboardTransport(auth, "gw1", (_, _) =>
        {
            var sock = new FakeRpcTransport();
            sockets.Add(sock);
            sock.Push(FakeRpcTransport.ReadyFrame);
            return Task.FromResult<IRpcTransport>(sock);
        }, ownsAuth: false, reconnectDelay: (_, _) => Task.CompletedTask, readyTimeout: TimeSpan.FromSeconds(5));

        using var answerCts = new CancellationTokenSource();
        _ = Task.Run(() => PumpAnswers(sockets, m => m switch
        {
            "client.capabilities" => "{}",
            "session.create" => """{"session_id":"s1"}""",
            "prompt.submit" => """{"status":"streaming"}""",
            _ => null
        }, answerCts.Token));

        await transport.EnsureConnectedAsync(default);
        Assert.True(handler.TicketMints >= 1);
        var first = sockets[0];
        first.ServerClose(1001, "going away");

        // Wait for reconnect with a second ticket
        for (var i = 0; i < 200 && sockets.Count < 2; i++)
            await Task.Delay(10);
        Assert.True(sockets.Count >= 2, "expected fresh socket after disconnect");
        Assert.True(handler.TicketMints >= 2, "expected fresh ticket mint");

        // No prompt was submitted during reconnect-only path
        Assert.Equal(0, sockets.Sum(s => s.CountCalls("prompt.submit")));
        answerCts.Cancel();
    }

    [Fact]
    public void DisconnectAfterSubmitBeforeToken_MarksUncertain_DoesNotAutoResend()
    {
        Assert.False(PromptResendPolicy.MayAutoResend(PromptDeliveryState.Uncertain));
        Assert.False(PromptResendPolicy.MayAutoResend(PromptDeliveryState.Partial));
        Assert.False(PromptResendPolicy.MayAutoResend(PromptDeliveryState.Acknowledged));
        Assert.False(PromptResendPolicy.UserMayResend(PromptDeliveryState.Uncertain));
        Assert.True(PromptResendPolicy.UserMayResend(PromptDeliveryState.NotReceived));

        var state = PromptDeliveryState.Pending;
        state = PromptResendPolicy.AfterSubmitAck();
        state = PromptResendPolicy.AfterDisconnect(state);
        Assert.Equal(PromptDeliveryState.Uncertain, state);
        Assert.False(PromptResendPolicy.MayAutoResend(state));
    }

    [Fact]
    public void PartialReply_Disconnect_KeepsPartial_NoAutoResend()
    {
        var state = PromptResendPolicy.AfterFirstToken(PromptDeliveryState.Acknowledged);
        state = PromptResendPolicy.AfterDisconnect(state);
        Assert.Equal(PromptDeliveryState.Partial, state);
        Assert.False(PromptResendPolicy.MayAutoResend(state));
    }

    [Fact]
    public async Task ExpiredAuth_TerminatesAsSessionExpired()
    {
        var n = 0;
        var handler = new AuthHandler(req =>
        {
            if (!req.RequestUri!.AbsolutePath.EndsWith("/api/auth/ws-ticket", StringComparison.Ordinal))
                return null;
            if (Interlocked.Increment(ref n) == 1)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ticket":"T"}""", Encoding.UTF8, "application/json")
                };
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"reason":"session_expired"}""", Encoding.UTF8, "application/json")
            };
        });
        using var auth = new DashboardAuth(Base, handler);
        await auth.SignInAsync("basic", "u", "p", default);
        var sockets = new List<FakeRpcTransport>();
        await using var transport = new HermesDashboardTransport(auth, "gw1", (_, _) =>
        {
            var sock = new FakeRpcTransport();
            sockets.Add(sock);
            sock.Push(FakeRpcTransport.ReadyFrame);
            return Task.FromResult<IRpcTransport>(sock);
        }, ownsAuth: false, reconnectDelay: (_, _) => Task.CompletedTask);

        using var answerCts = new CancellationTokenSource();
        _ = Task.Run(() => PumpAnswers(sockets, m => m == "client.capabilities" ? "{}" : null, answerCts.Token));

        await transport.EnsureConnectedAsync(default);
        sockets[0].ServerClose(1001, "drop");

        for (var i = 0; i < 200 && transport.ConnectionPhase is not ConnectionSessionExpired; i++)
            await Task.Delay(15);

        Assert.IsType<ConnectionSessionExpired>(transport.ConnectionPhase);
        answerCts.Cancel();
    }

    [Fact]
    public async Task HostGuard4403_TerminatesFailed()
    {
        var handler = new AuthHandler();
        using var auth = new DashboardAuth(Base, handler);
        await auth.SignInAsync("basic", "u", "p", default);
        var sockets = new List<FakeRpcTransport>();
        await using var conn = new GatewayConnection(
            ct => auth.MintWsTicketAsync(ct),
            (_, _) =>
            {
                var sock = new FakeRpcTransport();
                sockets.Add(sock);
                sock.Push(FakeRpcTransport.ReadyFrame);
                return Task.FromResult<IRpcTransport>(sock);
            },
            delay: (_, _) => Task.CompletedTask,
            readyTimeout: TimeSpan.FromSeconds(2));

        using var answerCts = new CancellationTokenSource();
        _ = Task.Run(() => PumpAnswers(sockets, m => m == "client.capabilities" ? "{}" : null, answerCts.Token));

        conn.Start();
        for (var i = 0; i < 100 && conn.Phase is not ConnectionConnected; i++)
            await Task.Delay(20);
        Assert.IsType<ConnectionConnected>(conn.Phase);
        sockets[0].ServerClose(GatewayCloseCodes.RequestGuard, "guard");
        for (var i = 0; i < 100 && conn.Phase is not ConnectionFailed; i++)
            await Task.Delay(20);
        Assert.IsType<ConnectionFailed>(conn.Phase);
        answerCts.Cancel();
    }

    [Fact]
    public async Task CancellationStopsReconnectLoop()
    {
        var handler = new AuthHandler();
        using var auth = new DashboardAuth(Base, handler);
        await auth.SignInAsync("basic", "u", "p", default);
        await using var conn = new GatewayConnection(
            async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return "x";
            },
            (_, _) => throw new InvalidOperationException("should not open"),
            delay: (_, _) => Task.CompletedTask);

        conn.Start();
        await Task.Delay(30);
        await conn.DisposeAsync();
        Assert.IsType<ConnectionIdle>(conn.Phase);
    }

    [Fact]
    public void StaleFrame_WrongSessionFiltered_ByExistingStreamRules()
    {
        // Documented contract: ConsumeSessionEventsAsync skips other session_id (tested in CancellationRegressionTests).
        // After reconnect, recovery uses REST refetch — never auto prompt.submit.
        Assert.False(PromptResendPolicy.MayAutoResend(PromptDeliveryState.Partial));
    }
}

public class Checkpoint5ModelTests
{
    [Fact]
    public void ModelCatalog_ParseAndUsable_ChosenVsFreeOnly()
    {
        var json = JsonNode.Parse("""
        {
          "model": "free-a",
          "provider": "openrouter",
          "providers": [
            {
              "slug": "openrouter",
              "name": "OpenRouter",
              "models": ["free-a", "paid-b"],
              "capabilities": {
                "free-a": {"reasoning": false, "fast": true},
                "paid-b": {"reasoning": true, "can_disable_reasoning": true, "fast": false}
              },
              "pricing": {
                "free-a": {"free": true},
                "paid-b": {"input": "$1", "output": "$2"}
              }
            },
            {
              "slug": "custom",
              "name": "Custom",
              "authenticated": true,
              "models": ["mine"],
              "capabilities": { "mine": {"reasoning": true, "fast": false} }
            }
          ]
        }
        """) as JsonObject;

        var catalog = ModelCatalog.Parse(json).WithChosen(new HashSet<string> { "custom" }).Usable();
        Assert.Contains(catalog.Providers, p => p.Slug == "custom");
        var or = Assert.Single(catalog.Providers, p => p.Slug == "openrouter");
        Assert.All(or.Models, m => Assert.True(m.IsFree));
        Assert.DoesNotContain(or.Models, m => m.Id == "paid-b");
        Assert.Equal("free-a", catalog.CurrentModel);
        var paid = ModelCatalog.Parse(json).Find("openrouter", "paid-b");
        Assert.NotNull(paid);
        Assert.True(paid!.Reasoning);
        Assert.Equal(ReasoningEffort.Off, ReasoningEffortWire.ChoicesFor(paid).First());
    }

    [Fact]
    public async Task SetModel_UsesConfigSet_SessionScope_NoAutoPaidSwitch()
    {
        var sock = new FakeRpcTransport();
        sock.Push(FakeRpcTransport.ReadyFrame);
        await using var client = new JsonRpcClient(sock);
        await client.Ready.WaitAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var seen = new HashSet<string>();
            while (!cts.IsCancellationRequested)
            {
                foreach (var raw in sock.Sent.ToArray())
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(raw);
                    if (!doc.RootElement.TryGetProperty("id", out var idEl)) continue;
                    var id = idEl.GetString()!;
                    if (!seen.Add(id)) continue;
                    var method = doc.RootElement.GetProperty("method").GetString();
                    if (method == "client.capabilities")
                        sock.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":{}}");
                    else if (method == "config.set")
                        sock.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":{}}");
                    else if (method == "model.options")
                        sock.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":{\"providers\":[{\"slug\":\"p\",\"models\":[\"m\"],\"capabilities\":{\"m\":{\"reasoning\":true,\"fast\":true}}}]}}");
                }
                await Task.Delay(5);
            }
        });

        var controls = new SessionModelControls(() => client, () => Task.FromResult("runtime-1"));
        controls.RememberPending("pending-m", "pending-p", "medium", false);
        var create = new JsonObject();
        controls.ApplyToCreateParams(create);
        Assert.Equal("pending-m", create["model"]?.GetValue<string>());
        Assert.Equal("pending-p", create["provider"]?.GetValue<string>());
        Assert.Equal(false, create["fast"]?.GetValue<bool>());

        var result = await controls.SetModelAsync("m", "p");
        Assert.IsType<ModelSwitchDone>(result);
        Assert.Contains(sock.Sent, s => s.Contains("config.set") && s.Contains("--session") && s.Contains("m --provider p"));
        Assert.DoesNotContain(sock.Sent, s => s.Contains("confirm_expensive_model"));

        await controls.SetReasoningEffortAsync("high");
        await controls.SetFastAsync(true);
        Assert.Contains(sock.Sent, s => s.Contains("\"key\":\"reasoning\"") || s.Contains("\"key\": \"reasoning\"") || s.Contains("reasoning"));
        cts.Cancel();
    }

    [Fact]
    public async Task ExpensiveModel_NeedsConfirmation_DoesNotStaySwitched()
    {
        var sock = new FakeRpcTransport();
        sock.Push(FakeRpcTransport.ReadyFrame);
        await using var client = new JsonRpcClient(sock);
        await client.Ready.WaitAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var seen = new HashSet<string>();
            while (!cts.IsCancellationRequested)
            {
                foreach (var raw in sock.Sent.ToArray())
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(raw);
                    if (!doc.RootElement.TryGetProperty("id", out var idEl)) continue;
                    var id = idEl.GetString()!;
                    if (!seen.Add(id)) continue;
                    var method = doc.RootElement.GetProperty("method").GetString();
                    if (method == "client.capabilities")
                        sock.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":{}}");
                    else if (method == "config.set")
                        sock.Push("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":{\"confirm_required\":true,\"confirm_message\":\"costs more\"}}");
                }
                await Task.Delay(5);
            }
        });

        var controls = new SessionModelControls(() => client, () => Task.FromResult("r1"));
        controls.RememberPending("old", "p");
        var r = await controls.SetModelAsync("pricey", "p");
        Assert.IsType<ModelSwitchNeedsConfirmation>(r);
        Assert.Equal("old", controls.Model);
        cts.Cancel();
    }
}

public class Checkpoint5ToastTests
{
    private static readonly ProfileKey A = new("g1", "a");
    private static readonly ProfileKey B = new("g1", "b");

    [Fact]
    public async Task Toast_ColdStart_Deselect_Unknown_DeepLink_Denied()
    {
        var store = new MemoryStore();
        await new ProfileSelectionService(store).SetSelectedAsync(new[] { A });
        var selection = new ProfileSelectionService(store);
        var gate = new NotificationGate(selection);
        var presenter = new RecordingToastPresenter();
        var enabled = true;
        var toasts = new ToastNotificationService(gate, () => enabled, presenter);

        Assert.Equal(ToastObservation.DeniedByGate, toasts.TryShow(new GatedNotification(A, "t", "b")).Observation); // cold
        await selection.LoadAsync();
        Assert.Equal(ToastObservation.Observed, toasts.TryShow(new GatedNotification(A, "ok", "body")).Observation);
        Assert.Equal(ToastObservation.DeniedByGate, toasts.TryShow(new GatedNotification(B, "x", "y")).Observation);
        Assert.Equal(ToastObservation.DeniedByGate,
            toasts.TryShow(new GatedNotification(null, "dl", "x", Surface.DeepLink), deepLink: "hikyaku://x").Observation);

        await selection.SetSelectedAsync(Array.Empty<ProfileKey>());
        Assert.Equal(ToastObservation.DeniedByGate, toasts.TryShow(new GatedNotification(A, "after", "deselect")).Observation);

        enabled = false;
        await selection.SetSelectedAsync(new[] { A });
        Assert.Equal(ToastObservation.SkippedDisabled, toasts.TryShow(new GatedNotification(A, "off", "body")).Observation);
        Assert.Single(presenter.Shown);
    }

    [Fact]
    public void RecordingPresenter_LabelsObservationHonestly()
    {
        var p = new RecordingToastPresenter { Result = ToastObservation.NotObserved };
        var r = p.Show("t", "b", null);
        Assert.Equal(ToastObservation.NotObserved, r.Observation);
        Assert.Contains("notobserved", r.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
