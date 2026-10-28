using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Hikyaku.Core;

public sealed record TerminalShareDestination(ProfileKey Profile, string? SessionId)
{
    public string Label =>
        string.IsNullOrWhiteSpace(SessionId)
            ? $"{Profile.GatewayId}/{Profile.ProfileId}"
            : $"{Profile.GatewayId}/{Profile.ProfileId} · {SessionId}";
}

public sealed record TerminalSharePreviewResult(
    bool Ok,
    string? Error = null,
    string? PreviewToken = null,
    string? RedactedPreview = null,
    string DestinationLabel = "",
    bool HardBlocked = false);

public sealed record TerminalShareConfirmResult(
    bool Ok,
    string? Error = null,
    string? Payload = null,
    bool HardBlocked = false);

/// <summary>
/// Explicit selection → redacted preview → confirm with destination revalidation.
/// Suspected keys stay hard-blocked; acknowledgement never shares.
/// Transmit only the approved displayed (redacted) payload — never text hidden by redaction.
/// Heuristic redaction is not a guarantee. No ordinary cache of raw terminal output.
/// </summary>
public static class TerminalShareGate
{
    public static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(5);
    public const int MaxPending = 8;
    public const int MaxRawChars = 64 * 1024;

    /// <summary>Test seam; null uses DateTimeOffset.UtcNow.</summary>
    public static Func<DateTimeOffset>? UtcNow { get; set; }

    private static readonly ConcurrentDictionary<string, PendingPreview> Pending = new(StringComparer.Ordinal);

    private sealed record PendingPreview(
        string? RawForReinspect,
        string ApprovedDisplayed,
        TerminalShareDestination Destination,
        DateTimeOffset CreatedUtc,
        bool HardBlocked,
        string? BlockMessage);

    public static int PendingCount
    {
        get
        {
            SweepExpired();
            return Pending.Count;
        }
    }

    public static TerminalSharePreviewResult Preview(string selectedText, TerminalShareDestination destination)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
            return new TerminalSharePreviewResult(false, "Select terminal text before adding to chat.");

        if (selectedText.Length > MaxRawChars)
            return new TerminalSharePreviewResult(false, "Selection too large to stage.");

        SweepExpired();
        EnforceCapacity();

        var decision = ProjectShareGate.Decide(selectedText, "terminal-selection.txt", acknowledgeRisk: false);
        var redacted = RedactForPreview(selectedText);

        if (decision.HardBlocked || !decision.Allowed)
        {
            // Do not retain raw credentials — only redacted display for the blocked dialog token.
            var token = MintToken();
            Pending[token] = new PendingPreview(
                RawForReinspect: null,
                ApprovedDisplayed: redacted,
                Destination: destination,
                CreatedUtc: Now(),
                HardBlocked: true,
                BlockMessage: decision.Message);
            return new TerminalSharePreviewResult(
                false,
                decision.Message,
                PreviewToken: token,
                RedactedPreview: redacted,
                DestinationLabel: destination.Label,
                HardBlocked: true);
        }

        var okToken = MintToken();
        Pending[okToken] = new PendingPreview(
            RawForReinspect: selectedText,
            ApprovedDisplayed: redacted,
            Destination: destination,
            CreatedUtc: Now(),
            HardBlocked: false,
            BlockMessage: null);
        return new TerminalSharePreviewResult(
            true,
            PreviewToken: okToken,
            RedactedPreview: redacted,
            DestinationLabel: destination.Label);
    }

    public static TerminalShareConfirmResult Confirm(
        string previewToken,
        TerminalShareDestination destination,
        bool acknowledgeRisk)
    {
        SweepExpired();
        if (!Pending.TryGetValue(previewToken, out var pending))
            return new TerminalShareConfirmResult(false, "Preview expired — select again and confirm.");

        if (IsExpired(pending))
        {
            Pending.TryRemove(previewToken, out _);
            return new TerminalShareConfirmResult(false, "Preview expired — select again and confirm.");
        }

        if (!SameDestination(pending.Destination, destination))
            return new TerminalShareConfirmResult(false, "Destination changed — re-preview for the current profile/session.");

        // Consume only after destination revalidation succeeds.
        if (!Pending.TryRemove(previewToken, out pending))
            return new TerminalShareConfirmResult(false, "Preview expired — select again and confirm.");

        if (pending.HardBlocked)
        {
            _ = acknowledgeRisk; // understanding only
            return new TerminalShareConfirmResult(
                false,
                pending.BlockMessage ?? "Sharing remains blocked.",
                HardBlocked: true);
        }

        // Re-inspect the original only when retained; transmit displayed/approved payload.
        if (pending.RawForReinspect is { } raw)
        {
            var again = ProjectShareGate.Decide(raw, "terminal-selection.txt", acknowledgeRisk: false);
            if (!again.Allowed || again.HardBlocked)
                return new TerminalShareConfirmResult(false, again.Message, HardBlocked: true);
        }

        return new TerminalShareConfirmResult(true, Payload: pending.ApprovedDisplayed);
    }

    public static bool Discard(string previewToken) =>
        !string.IsNullOrEmpty(previewToken) && Pending.TryRemove(previewToken, out _);

    public static void DiscardAll() => Pending.Clear();

    public static void DiscardForDestination(TerminalShareDestination destination)
    {
        foreach (var kv in Pending)
        {
            if (SameDestination(kv.Value.Destination, destination))
                Pending.TryRemove(kv.Key, out _);
        }
    }

    public static void DiscardForProfile(ProfileKey profile)
    {
        foreach (var kv in Pending)
        {
            if (kv.Value.Destination.Profile.GatewayId == profile.GatewayId
                && kv.Value.Destination.Profile.ProfileId == profile.ProfileId)
                Pending.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>Test aid: true if any pending entry still holds this exact raw string.</summary>
    public static bool DebugContainsRaw(string raw)
    {
        foreach (var kv in Pending)
        {
            if (string.Equals(kv.Value.RawForReinspect, raw, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static string RedactForPreview(string text)
    {
        var s = text;
        s = System.Text.RegularExpressions.Regex.Replace(
            s, @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----",
            "[redacted: private key block]");
        s = System.Text.RegularExpressions.Regex.Replace(
            s, @"(?i)(sk-[A-Za-z0-9]{20,}|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16})",
            "[redacted: token]");
        s = System.Text.RegularExpressions.Regex.Replace(
            s, @"(?i)(password|passwd|secret|api[_-]?key|token)\s*[:=]\s*[""'][^""']{8,}[""']",
            "$1=[redacted]");
        return s;
    }

    private static void SweepExpired()
    {
        var now = Now();
        foreach (var kv in Pending)
        {
            if (now - kv.Value.CreatedUtc > PendingTtl)
                Pending.TryRemove(kv.Key, out _);
        }
    }

    private static void EnforceCapacity()
    {
        while (Pending.Count >= MaxPending)
        {
            var oldest = Pending.OrderBy(kv => kv.Value.CreatedUtc).FirstOrDefault();
            if (oldest.Key is null) break;
            Pending.TryRemove(oldest.Key, out _);
        }
    }

    private static bool IsExpired(PendingPreview pending) =>
        Now() - pending.CreatedUtc > PendingTtl;

    private static DateTimeOffset Now() => UtcNow?.Invoke() ?? DateTimeOffset.UtcNow;

    private static bool SameDestination(TerminalShareDestination a, TerminalShareDestination b) =>
        a.Profile.GatewayId == b.Profile.GatewayId
        && a.Profile.ProfileId == b.Profile.ProfileId
        && string.Equals(a.SessionId ?? "", b.SessionId ?? "", StringComparison.Ordinal);

    private static string MintToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }
}
