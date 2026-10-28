using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>
/// This PC's id towards the herald-push plugin (the Inbox's <c>device_id</c>). The plugin only accepts hex digits and dashes,
/// 8 to 64 of them (hermes-herald-push <c>store.py</c> <c>DEVICE_ID_RE</c>); Android uses a UUID. The first Windows format,
/// "desktop-" plus hex, failed that check, so an id in that format is replaced by a fresh one.
/// </summary>
public static partial class InboxDevice
{
    [GeneratedRegex("^[0-9a-fA-F-]{8,64}$")]
    private static partial Regex Accepted();

    public static string NewId() => Guid.NewGuid().ToString("D");

    public static bool IsValid(string? id) => !string.IsNullOrEmpty(id) && Accepted().IsMatch(id);

    /// <summary>The id to keep using: this one if the plugin accepts it, otherwise a new one.</summary>
    public static string Ensure(string? id) => IsValid(id) ? id! : NewId();
}
