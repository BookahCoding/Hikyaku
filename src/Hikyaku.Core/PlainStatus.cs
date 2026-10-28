namespace Hikyaku.Core;

/// <summary>Where the connection to Hermes stands, in the terms a person (not a developer) cares about.</summary>
public enum ConnectionKind { Demo, NotConfigured, NeedsSignIn, Starting, Connecting, Connected, Reconnecting, SessionExpired, Failed }

/// <summary>What to tell someone in Simple view, and whether to offer "Sign in again".</summary>
public sealed record PlainStatusLine(string Text, bool OfferSignIn);

/// <summary>
/// Simple view (someone with little computer experience) never sees protocol names, retry counters, addresses or file paths in the status line:
/// it says in a sentence what is wrong and what to do. Advanced view keeps the detailed line. Pure; no gateway.
/// </summary>
public static class PlainStatus
{
    public static PlainStatusLine For(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Demo => new("Demo mode: the replies are examples, not from Hermes.", false),
        ConnectionKind.NotConfigured => new("Hermes isn't set up on this computer yet. Ask the person who looks after it to finish the setup.", false),
        ConnectionKind.NeedsSignIn => new("You need to sign in before you can chat.", true),
        ConnectionKind.Starting or ConnectionKind.Connecting => new("Connecting to Hermes…", false),
        ConnectionKind.Connected => new("", false),
        ConnectionKind.Reconnecting => new("The connection dropped. Trying to reconnect…", false),
        ConnectionKind.SessionExpired => new("You've been signed out. Please sign in again to carry on.", true),
        ConnectionKind.Failed => new("Can't reach Hermes right now. Check your internet connection and try again in a moment.", false),
        _ => new("", false)
    };

    /// <summary>
    /// Advanced view: just as short (nothing at all when connected), written for whoever looks after the gateway, with the
    /// technical reason when the connection drops or fails. The old line ("[Connected] Dashboard credentials stored (DPAPI).
    /// Protocol: Hermes JSON-RPC …") was developer notes, not status.
    /// </summary>
    public static string ForAdvanced(ConnectionKind kind, string? reason = null)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? "" : " (" + reason.Trim() + ")";
        return kind switch
        {
            ConnectionKind.Demo => "Demo mode: scripted replies, not from Hermes.",
            ConnectionKind.NotConfigured => "Not set up: add the gateway address in Settings.",
            ConnectionKind.NeedsSignIn => "Not signed in: sign in from Settings.",
            ConnectionKind.Starting or ConnectionKind.Connecting => "Connecting…",
            ConnectionKind.Connected => "",
            ConnectionKind.Reconnecting => "Connection lost, reconnecting…" + why,
            ConnectionKind.SessionExpired => "Signed out by the gateway: sign in again from Settings.",
            ConnectionKind.Failed => "Can't connect" + why,
            _ => ""
        };
    }

    /// <summary>Said after the status when a saved copy of the chats is being shown instead of the live ones.</summary>
    public const string OfflineCopyNote = "Showing your saved chats until it reconnects.";
}

/// <summary>The text-size choices (a percentage the whole window is scaled by). Anything else becomes the nearest choice.</summary>
public static class TextScale
{
    public static readonly IReadOnlyList<int> Choices = [100, 115, 130];

    public static int Normalize(int percent) => Choices.OrderBy(c => Math.Abs(c - percent)).ThenBy(c => c).First();

    public static double Factor(int percent) => Normalize(percent) / 100.0;

    public static string Label(int percent) => Normalize(percent) switch { 115 => "Larger", 130 => "Largest", _ => "Normal" };
}
