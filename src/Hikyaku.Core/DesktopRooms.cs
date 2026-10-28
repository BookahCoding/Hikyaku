using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>Read-only Desktop Bot Mode group rooms — BookahHermes DesktopRooms.kt.</summary>
public sealed record DesktopLine(
    bool FromUser,
    string Speaker,
    string Text,
    string? Id = null,
    string? Source = null,
    double? At = null,
    bool Truncated = false);

public sealed record DesktopMember(string Name, string? Handle = null);

public sealed record DesktopRoom(
    string Key,
    string Name,
    string? RoomId = null,
    IReadOnlyList<DesktopMember>? Members = null,
    IReadOnlyList<DesktopLine>? Lines = null,
    int Omitted = 0,
    int Revision = 0,
    double? UpdatedAt = null)
{
    public IReadOnlyList<DesktopMember> MembersList => Members ?? Array.Empty<DesktopMember>();
    public IReadOnlyList<DesktopLine> LinesList => Lines ?? Array.Empty<DesktopLine>();

    /// <summary>Desktop needs-you: newest member line mentions @user.</summary>
    public bool NeedsYou
    {
        get
        {
            for (var i = LinesList.Count - 1; i >= 0; i--)
            {
                var line = LinesList[i];
                if (line.FromUser) continue;
                return NeedsYouMention.IsMatch(line.Text);
            }
            return false;
        }
    }

    private static readonly Regex NeedsYouMention = new(@"@user\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

public sealed record RestrictedRoomView(string Key, string Name, string Explanation);

public sealed record FilteredRooms(
    IReadOnlyList<DesktopRoom> Visible,
    IReadOnlyList<RestrictedRoomView> Restricted);

public static class DesktopRooms
{
    public const string MetaKey = "hermes-bots-groups";

    public static IReadOnlyList<DesktopRoom> Parse(JsonObject? uiMeta)
    {
        var snapshot = uiMeta?[MetaKey] as JsonObject;
        if (snapshot is null) return Array.Empty<DesktopRoom>();
        var rooms = snapshot["rooms"] as JsonObject;
        if (rooms is null) return Array.Empty<DesktopRoom>();
        var deleted = snapshot["deleted"] as JsonObject ?? new JsonObject();
        var parsed = new List<DesktopRoom>();
        foreach (var (key, body) in rooms)
        {
            if (body is not JsonObject obj) continue;
            var room = ParseRoom(key, obj, deleted);
            if (room is not null) parsed.Add(room);
        }
        return parsed
            .OrderByDescending(r => r.UpdatedAt ?? 0)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DesktopRoom? ParseRoom(string key, JsonObject body, JsonObject deleted)
    {
        var revision = Int(body, "revision") ?? 0;
        var tombstoneAt = Double(deleted, key);
        if (tombstoneAt is not null && (key.StartsWith("id:", StringComparison.Ordinal) || revision <= tombstoneAt))
            return null;

        var lines = new List<DesktopLine>();
        if (body["log"] is JsonArray log)
        {
            foreach (var el in log)
                if (el is JsonObject lo && ParseLine(lo) is { } line)
                    lines.Add(line);
        }

        var members = new List<DesktopMember>();
        if (body["members"] is JsonArray mem)
        {
            foreach (var el in mem)
            {
                if (el is not JsonObject m) continue;
                var name = Text(m, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                members.Add(new DesktopMember(name, Text(m, "handle")));
            }
        }

        var nameRoom = Text(body, "name");
        if (string.IsNullOrWhiteSpace(nameRoom))
            nameRoom = key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key;

        double? updatedAt = null;
        foreach (var line in lines)
            if (line.At is { } at && (updatedAt is null || at > updatedAt))
                updatedAt = at;

        return new DesktopRoom(
            Key: key,
            Name: nameRoom!,
            RoomId: Text(body, "roomId"),
            Members: members,
            Lines: lines,
            Omitted: Int(body, "omitted") ?? 0,
            Revision: revision,
            UpdatedAt: updatedAt);
    }

    private static DesktopLine? ParseLine(JsonObject line)
    {
        var text = Text(line, "text");
        if (text is null) return null;
        if (line["from"] is not JsonObject from) return null;
        var fromUser = Text(from, "kind") == "user";
        var speaker = Text(from, "name");
        if (string.IsNullOrWhiteSpace(speaker))
            speaker = fromUser ? "You" : "A member";
        return new DesktopLine(
            FromUser: fromUser,
            Speaker: speaker!,
            Text: text,
            Id: Text(line, "id"),
            Source: Text(from, "source"),
            At: Double(line, "at"),
            Truncated: line["truncated"] is JsonValue tv && tv.TryGetValue(out bool t) && t);
    }

    private static string? Text(JsonObject o, string k) =>
        o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonObject o, string k) =>
        o[k] is JsonValue v && v.TryGetValue(out int i) ? i : null;

    private static double? Double(JsonObject o, string k) =>
        o[k] is JsonValue v && v.TryGetValue(out double d) ? d : null;
}
