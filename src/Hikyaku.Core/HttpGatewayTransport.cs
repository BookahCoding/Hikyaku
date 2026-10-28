using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Hikyaku.Core;

/// <summary>
/// LEGACY / unverified OpenAI-compat SSE transport kept for existing unit tests only.
/// Production UI uses <see cref="HermesDashboardTransport"/> (dashboard JSON-RPC over WebSocket).
/// Do not treat this as Hermes dashboard protocol.
/// </summary>
public sealed class HttpGatewayTransport(HttpClient http, Uri baseUri, Func<string?> tokenProvider,
    string chatPath = "/v1/chat/completions") : IGatewayTransport
{
    public Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct)
        => throw new NotSupportedException("Profile discovery endpoint is not documented/pinned yet.");

    public async IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = request.Profile.ProfileId,
            stream = true,
            messages = request.Messages.Select(m => new { role = m.Role, content = m.Content })
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, chatPath))
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (tokenProvider() is { Length: > 0 } t) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await foreach (var data in SseParser.ReadDataAsync(reader, ct).ConfigureAwait(false))
        {
            if (data == "[DONE]") break;
            var text = TryExtractDelta(data);
            if (text is { Length: > 0 }) yield return new TokenDelta(text);
        }
        yield return new StreamCompleted();
    }

    public static string? TryExtractDelta(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var c) && c.ValueKind == JsonValueKind.Array &&
                c.GetArrayLength() > 0 &&
                c[0].TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
                return content.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}
