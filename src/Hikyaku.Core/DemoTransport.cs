using System.Runtime.CompilerServices;

namespace Hikyaku.Core;

/// <summary>DEMO ONLY: scripted tokens for tests and the explicitly labelled demo mode. Not a backend.</summary>
public sealed class DemoTransport : IGatewayTransport
{
    public const string Label = "DEMO - scripted text, not a Hermes response";

    public Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProfileInfo>>(new[]
        {
            new ProfileInfo(new ProfileKey("demo-gateway", "profile-a"), "Demo profile A"),
            new ProfileInfo(new ProfileKey("demo-gateway", "profile-b"), "Demo profile B"),
        });

    public async IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var w in (Label + ".").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            await Task.Delay(30, ct).ConfigureAwait(false);
            yield return new TokenDelta(w + " ");
        }
        yield return new StreamCompleted();
    }
}
