using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hikyaku.Core;

/// <summary>
/// Hosted rooms: group chats the gateway itself runs (<c>groups.*</c> over the dashboard socket), so a room keeps
/// working with no client attached. Shapes follow BookahHermes <c>rooms/Rooms.kt</c> @ 5f00986b (the gateway's
/// <c>groups_bot_relay</c> contract); unknown fields are ignored and odd ones read as missing, never fatal.
/// This is separate from <see cref="DesktopRoom"/>, the read-only mirror of the rooms Desktop runs.
/// </summary>
public sealed record HostedRoomMember(string? MemberId, string? Profile, string? Handle, string? DisplayName)
{
    /// <summary>What to call them in the transcript.</summary>
    public string Label => FirstNonBlank(DisplayName, Profile, Handle, MemberId) ?? "someone";

    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

public sealed record HostedRoom(
    string RoomId, string Name, IReadOnlyList<HostedRoomMember> Members,
    int? LatestSeq, double? UpdatedAt, double? DisbandedAt, int Revision)
{
    public HostedRoomMember? Member(string? memberId) =>
        memberId is null ? null : Members.FirstOrDefault(m => m.MemberId == memberId);
}

public sealed record RoomEvent(
    string RoomId, int Seq, string EventId, string Kind, string ActorKind, string ActorId,
    JsonObject Payload, double CreatedAt)
{
    public string? MessageText => RoomJson.Str(Payload, "text");
}

public sealed record RoomLogPage(IReadOnlyList<RoomEvent> Events, int Cursor, int LatestSeq, bool HasMore)
{
    public static readonly RoomLogPage Empty = new([], 0, 0, false);
}

/// <summary>One thing the room waits on the user for: an approval or a retry, with the driver's own coordinates.</summary>
public sealed record RoomPendingAction(
    string Kind, string? MemberId, string? TaskId, int? ExecutionGeneration, string? RequestId, string? ApprovalSummary);

public sealed record RoomDriverStatus(bool Running, bool Working, bool Blocked, IReadOnlyList<RoomPendingAction> PendingActions);

public sealed record RoomState(HostedRoom Room, RoomDriverStatus? Driver);

public sealed record RoomCapabilities(int ProtocolVersion, bool Driver, IReadOnlyList<string> Methods, int MaxLogLimit);

/// <summary>One thing the transcript shows; everything else in the log is machinery the user shouldn't read.</summary>
public abstract record RoomLine(int Seq)
{
    public sealed record Message(int Seq, bool FromUser, string? Speaker, string Text, string EventId, string? Profile, double? CreatedAt) : RoomLine(Seq);
    public sealed record System(int Seq, string Text) : RoomLine(Seq);
}

/// <summary>Tolerant JSON readers shared by the room parsers.</summary>
internal static class RoomJson
{
    public static string? Str(JsonNode? node, string key)
    {
        if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is not JsonValue jv) return null;
        return jv.TryGetValue<string>(out var s) ? s : null;
    }

    public static int? Int(JsonNode? node, string key)
    {
        if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is not JsonValue jv) return null;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        if (jv.TryGetValue<double>(out var d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
        return null;
    }

    public static double? Dbl(JsonNode? node, string key)
    {
        if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is not JsonValue jv) return null;
        if (jv.TryGetValue<double>(out var d)) return d;
        if (jv.TryGetValue<long>(out var l)) return l;
        return null;
    }

    public static bool Bool(JsonNode? node, string key)
    {
        if (node is not JsonObject o || !o.TryGetPropertyValue(key, out var v) || v is not JsonValue jv) return false;
        return jv.TryGetValue<bool>(out var b) && b;
    }
}

public static class HostedRoomParser
{
    public static HostedRoomMember? Member(JsonNode? n) => n is JsonObject
        ? new HostedRoomMember(RoomJson.Str(n, "member_id"), RoomJson.Str(n, "profile"), RoomJson.Str(n, "handle"), RoomJson.Str(n, "display_name"))
        : null;

    public static HostedRoom? Room(JsonNode? n)
    {
        var id = RoomJson.Str(n, "room_id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var members = new List<HostedRoomMember>();
        if (n is JsonObject o && o["members"] is JsonArray arr)
            foreach (var m in arr) if (Member(m) is { } hm) members.Add(hm);
        return new HostedRoom(id, RoomJson.Str(n, "name") ?? id, members,
            RoomJson.Int(n, "latest_seq"), RoomJson.Dbl(n, "updated_at"), RoomJson.Dbl(n, "disbanded_at"), RoomJson.Int(n, "revision") ?? 0);
    }

    public static RoomEvent? Event(JsonNode? n)
    {
        var seq = RoomJson.Int(n, "seq");
        var kind = RoomJson.Str(n, "kind");
        var eventId = RoomJson.Str(n, "event_id");
        if (seq is null || kind is null || eventId is null) return null;
        var actor = (n as JsonObject)?["actor"];
        return new RoomEvent(RoomJson.Str(n, "room_id") ?? "", seq.Value, eventId, kind,
            RoomJson.Str(actor, "kind") ?? "", RoomJson.Str(actor, "id") ?? "",
            (n as JsonObject)?["payload"] as JsonObject ?? new JsonObject(), RoomJson.Dbl(n, "created_at") ?? 0);
    }

    public static RoomLogPage Page(JsonNode? n)
    {
        if (n is not JsonObject o) return RoomLogPage.Empty;
        var events = new List<RoomEvent>();
        if (o["events"] is JsonArray arr)
            foreach (var e in arr) if (Event(e) is { } ev) events.Add(ev);       // one odd entry loses only itself
        return new RoomLogPage(events, RoomJson.Int(o, "cursor") ?? 0, RoomJson.Int(o, "latest_seq") ?? 0, RoomJson.Bool(o, "has_more"));
    }

    public static RoomState? State(JsonNode? n)
    {
        if (n is not JsonObject o || Room(o["room"]) is not { } room) return null;
        RoomDriverStatus? driver = null;
        if (o["driver_status"] is JsonObject d)
        {
            var actions = new List<RoomPendingAction>();
            if (d["pending_actions"] is JsonArray arr)
                foreach (var a in arr)
                {
                    var kind = RoomJson.Str(a, "kind");
                    if (kind is null) continue;
                    var approval = (a as JsonObject)?["approval"];
                    actions.Add(new RoomPendingAction(kind, RoomJson.Str(a, "member_id"), RoomJson.Str(a, "task_id"),
                        RoomJson.Int(a, "execution_generation"), RoomJson.Str(a, "request_id"),
                        RoomJson.Str(approval, "command") ?? RoomJson.Str(approval, "description")));
                }
            driver = new RoomDriverStatus(RoomJson.Bool(d, "running"), RoomJson.Bool(d, "working"), RoomJson.Bool(d, "blocked"), actions);
        }
        return new RoomState(room, driver);
    }

    public static RoomCapabilities Capabilities(JsonNode? n)
    {
        var methods = new List<string>();
        if (n is JsonObject o && o["methods"] is JsonArray arr)
            foreach (var m in arr) if (m is JsonValue v && v.TryGetValue<string>(out var s)) methods.Add(s);
        return new RoomCapabilities(RoomJson.Int(n, "protocol_version") ?? 0, RoomJson.Bool(n, "driver"), methods, RoomJson.Int(n, "max_log_limit") ?? 50);
    }
}

/// <summary>Log events → transcript lines, and merging log pages without drawing a line twice.</summary>
public static class RoomTranscript
{
    public static RoomLine? Line(RoomEvent e, IReadOnlyList<HostedRoomMember> members)
    {
        string Member(string? id) => members.FirstOrDefault(m => m.MemberId == id)?.Label ?? "A member";
        double? at = e.CreatedAt > 0 ? e.CreatedAt : null;
        switch (e.Kind)
        {
            case "message.user":
                return string.IsNullOrWhiteSpace(e.MessageText) ? null : new RoomLine.Message(e.Seq, true, null, e.MessageText, e.EventId, null, at);
            case "message.member":
            {
                if (string.IsNullOrWhiteSpace(e.MessageText)) return null;
                var id = RoomJson.Str(e.Payload, "member_id");
                var row = members.FirstOrDefault(m => m.MemberId == id);
                return new RoomLine.Message(e.Seq, false, Member(id), e.MessageText, e.EventId, row?.Profile ?? id, at);
            }
            case "turn.failed":
                return new RoomLine.System(e.Seq, Member(RoomJson.Str(e.Payload, "member_id")) + " couldn't take their turn: "
                    + (RoomJson.Str(e.Payload, "error") ?? "something went wrong"));
            case "turn.cancelled":
            {
                var reason = RoomJson.Str(e.Payload, "reason");
                return new RoomLine.System(e.Seq, "Turn stopped" + (string.IsNullOrWhiteSpace(reason) ? "." : ": " + reason));
            }
            case "turn.settled":
                // A member that chose to say nothing: quiet, but not silent.
                return RoomJson.Bool(e.Payload, "passed")
                    ? new RoomLine.System(e.Seq, Member(RoomJson.Str(e.Payload, "member_id")) + " had nothing to add.")
                    : null;
            case "room.stop_requested":
                return new RoomLine.System(e.Seq, "Stopping the room was asked for.");
            case "room.renamed":
                return RoomJson.Str(e.Payload, "name") is { } name ? new RoomLine.System(e.Seq, "Renamed to “" + name + "”.") : null;
            default:
                return null;     // turn.started / room.activity / authority events are bookkeeping
        }
    }

    public static IReadOnlyList<RoomLine> Lines(IEnumerable<RoomEvent> events, IReadOnlyList<HostedRoomMember> members) =>
        events.Select(e => Line(e, members)).OfType<RoomLine>().ToList();

    /// <summary>Existing plus incoming, by seq: an overlapping catch-up or a duplicate read never draws a line twice.</summary>
    public static IReadOnlyList<RoomEvent> Merge(IReadOnlyList<RoomEvent> existing, IReadOnlyList<RoomEvent> incoming)
    {
        if (incoming.Count == 0) return existing;
        var seen = existing.Select(e => e.Seq).ToHashSet();
        var fresh = incoming.Where(e => seen.Add(e.Seq)).ToList();
        if (fresh.Count == 0) return existing;
        return existing.Concat(fresh).OrderBy(e => e.Seq).ToList();
    }

    /// <summary>
    /// The members still to answer the user's newest message, in the order they take turns, read off the log (the gateway
    /// gives no "who is working now"). First round only; empty once the discussion has settled.
    /// </summary>
    public static IReadOnlyList<HostedRoomMember> WaitingFor(IReadOnlyList<RoomEvent> events, IReadOnlyList<HostedRoomMember> members)
    {
        var asked = events.LastOrDefault(e => e.Kind == "message.user");
        if (asked is null) return [];
        var about = events.Where(e => RoomJson.Str(e.Payload, "discussion_event_id") == asked.EventId).ToList();
        if (about.Any(e => e.Kind == "room.activity" && RoomJson.Str(e.Payload, "status") is "settled" or "bounded")) return [];
        var firstRound = about.Where(e => RoomJson.Int(e.Payload, "round_index") == 0).ToList();
        var answered = firstRound.Where(e => e.Kind is "message.member" or "turn.settled" or "turn.failed" or "turn.cancelled")
            .Select(e => RoomJson.Str(e.Payload, "member_id")).Where(s => s is not null).ToHashSet();
        var putOff = firstRound.GroupBy(e => RoomJson.Str(e.Payload, "member_id"))
            .Where(g => g.Last().Kind == "turn.deferred").Select(g => g.Key).ToHashSet();
        return RoomMentions.Recipients(asked.MessageText ?? "", members)
            .Where(m => m.MemberId is not null && !answered.Contains(m.MemberId))     // a legacy row with no id can't be told apart
            .OrderBy(m => putOff.Contains(m.MemberId))
            .ToList();
    }

    public static string? WaitingLine(IReadOnlyList<HostedRoomMember> waiting)
    {
        if (waiting.Count == 0) return null;
        var now = waiting[0].Label;
        return waiting.Count == 1 ? now + " is replying…" : now + " is replying · then " + string.Join(", ", waiting.Skip(1).Select(m => m.Label));
    }
}

/// <summary>An <c>@word</c> being typed: where it starts and ends (the whole word, also past the cursor) and what follows the <c>@</c> up to the cursor.</summary>
public sealed record MentionQuery(int Start, int End, string Query);

/// <summary>Who a message to a room asks first, by the gateway's own rule, and the composer's @ picker.</summary>
public static class RoomMentions
{
    private static readonly Regex Mention = new("@([A-Za-z0-9][A-Za-z0-9._:-]*)", RegexOptions.Compiled);
    public const int MaxText = 100_000;

    /// <summary>The members whose handles the text names with @, every member for @all / @everyone, and every member when it names no one the room knows.</summary>
    public static IReadOnlyList<HostedRoomMember> Recipients(string text, IReadOnlyList<HostedRoomMember> members)
    {
        var handles = members.Select(m => m.Handle?.ToLowerInvariant()).Where(h => h is not null).ToHashSet();
        var named = new HashSet<string>();
        var everyone = false;
        foreach (Match m in Mention.Matches(text))
        {
            var h = m.Groups[1].Value.ToLowerInvariant();
            if (h is "all" or "everyone") everyone = true;
            else if (handles.Contains(h)) named.Add(h);
        }
        if (everyone || named.Count == 0) return members;
        return members.Where(m => m.Handle is not null && named.Contains(m.Handle.ToLowerInvariant())).ToList();
    }

    /// <summary>The members <paramref name="query"/> (typed after @) can mean, by handle or any word of their name, best first.</summary>
    public static IReadOnlyList<HostedRoomMember> Mentionable(IReadOnlyList<HostedRoomMember> members, string query)
    {
        var q = query.ToLowerInvariant();
        return members.Where(m => m.Handle is not null)
            .Select(m => (Member: m, Handle: m.Handle!.ToLowerInvariant()))
            .Where(x => q.Length == 0 || x.Handle.Contains(q, StringComparison.Ordinal)
                        || x.Member.Label.ToLowerInvariant().Split(' ').Any(w => w.StartsWith(q, StringComparison.Ordinal)))
            .OrderByDescending(x => x.Handle.StartsWith(q, StringComparison.Ordinal))
            .ThenBy(x => x.Member.Label.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(x => x.Member).ToList();
    }

    /// <summary>Whether <paramref name="query"/> can still become @all, the room's word for every member.</summary>
    public static bool MentionsEveryone(string query) =>
        "all".StartsWith(query, StringComparison.OrdinalIgnoreCase) || "everyone".StartsWith(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>The @word the cursor is in, when it is at the start of the text or after whitespace (an e-mail address does not count), else null.</summary>
    public static MentionQuery? QueryAt(string text, int cursor)
    {
        if (cursor < 0 || cursor > text.Length) return null;
        var start = cursor;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] != '@') start--;
        if (start == 0 || text[start - 1] != '@') return null;
        var at = start - 1;
        if (at > 0 && !char.IsWhiteSpace(text[at - 1])) return null;
        var query = text[start..cursor];
        if (query.Any(c => !IsHandleChar(c))) return null;
        var end = cursor;
        while (end < text.Length && IsHandleChar(text[end])) end++;
        return new MentionQuery(at, end, query);
    }

    private static bool IsHandleChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';

    /// <summary>Replaces the @word under the pick with <c>@handle </c>; returns the new text and cursor.</summary>
    public static (string Text, int Cursor) Apply(string text, MentionQuery q, string handle)
    {
        var insert = "@" + handle + " ";
        var tail = q.End < text.Length && text[q.End] == ' ' ? text[(q.End + 1)..] : text[q.End..];
        var result = text[..q.Start] + insert + tail;
        return (result, q.Start + insert.Length);
    }
}

public enum RoomFailureKind { NotConnected, Unsupported, Refused, Failed }

/// <summary>A room call that did not succeed, with words the user can read.</summary>
public sealed class RoomException(RoomFailureKind kind, string message) : Exception(message)
{
    public RoomFailureKind Kind { get; } = kind;
}

/// <summary>
/// The gateway calls behind hosted rooms (BookahHermes <c>RoomsApi.kt</c> @ 5f00986b). Every write is idempotent by an id the
/// caller supplies, so a retry after a lost answer is the same ask. Creating rooms and uploading files are not here yet.
/// </summary>
public sealed class HostedRoomsApi(Func<IJsonRpcPeer?> peer)
{
    public const string MainThread = "main";
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(60);

    public async Task<RoomCapabilities> CapabilitiesAsync(CancellationToken ct = default) =>
        HostedRoomParser.Capabilities(await CallAsync("groups.capabilities", new JsonObject(), null, ct).ConfigureAwait(false));

    /// <summary>Rooms this gateway hosts, most recently changed first; a deleted room that lingers as a tombstone is gone.</summary>
    public async Task<IReadOnlyList<HostedRoom>> ListAsync(CancellationToken ct = default)
    {
        var reply = await CallAsync("groups.list", new JsonObject(), null, ct).ConfigureAwait(false);
        var rooms = new List<HostedRoom>();
        if (reply is JsonObject o && o["rooms"] is JsonArray arr)
            foreach (var r in arr)
                if (HostedRoomParser.Room(r) is { DisbandedAt: null } room) rooms.Add(room);
        return rooms.OrderByDescending(r => r.UpdatedAt ?? 0).ToList();
    }

    public async Task<RoomState> StateAsync(string roomId, CancellationToken ct = default)
    {
        var reply = await CallAsync("groups.state", new JsonObject { ["room_id"] = RequireId(roomId) }, null, ct).ConfigureAwait(false);
        return HostedRoomParser.State(reply) ?? throw new RoomException(RoomFailureKind.Failed, "The gateway didn't answer for the room.");
    }

    /// <summary>Everything after <paramref name="sinceSeq"/>, up to <paramref name="limit"/> events.</summary>
    public async Task<RoomLogPage> LogAsync(string roomId, int sinceSeq, int limit, CancellationToken ct = default) =>
        HostedRoomParser.Page(await CallAsync("groups.log", new JsonObject
        {
            ["room_id"] = RequireId(roomId), ["since_seq"] = Math.Max(0, sinceSeq), ["limit"] = Math.Clamp(limit, 1, 500)
        }, null, ct).ConfigureAwait(false));

    /// <summary>One message.user, idempotent by <paramref name="eventId"/>.</summary>
    public async Task<RoomEvent> SendAsync(string roomId, string text, string eventId, string threadId = MainThread, CancellationToken ct = default)
    {
        var body = text?.Trim() ?? "";
        if (body.Length == 0) throw new RoomException(RoomFailureKind.Refused, "There is nothing to send.");
        if (body.Length > RoomMentions.MaxText) throw new RoomException(RoomFailureKind.Refused, "That message is too long to send to a room.");
        var reply = await CallAsync("groups.send", new JsonObject
        {
            ["room_id"] = RequireId(roomId),
            ["event_id"] = RequireId(eventId),
            ["payload"] = new JsonObject { ["text"] = body, ["thread_id"] = threadId }
        }, SendTimeout, ct).ConfigureAwait(false);
        return HostedRoomParser.Event((reply as JsonObject)?["event"]) ?? throw new RoomException(RoomFailureKind.Failed, "The gateway didn't take the message.");
    }

    /// <summary>Asks the whole room to stop; <paramref name="cancelId"/> makes a retry the same ask.</summary>
    public Task StopAsync(string roomId, string cancelId, CancellationToken ct = default) =>
        CallAsync("groups.stop", new JsonObject { ["room_id"] = RequireId(roomId), ["cancel_id"] = RequireId(cancelId) }, null, ct);

    /// <summary>Deletes the room for good, for every client; its work is stopped first.</summary>
    public Task DisbandAsync(string roomId, string cancelId, CancellationToken ct = default) =>
        CallAsync("groups.disband", new JsonObject { ["room_id"] = RequireId(roomId), ["cancel_id"] = RequireId(cancelId) }, SendTimeout, ct);

    public async Task<HostedRoom> RenameAsync(string roomId, string name, string eventId, CancellationToken ct = default)
    {
        var clean = name?.Trim() ?? "";
        if (clean.Length == 0) throw new RoomException(RoomFailureKind.Refused, "A room needs a name.");
        var reply = await CallAsync("groups.rename", new JsonObject
        {
            ["room_id"] = RequireId(roomId), ["event_id"] = RequireId(eventId), ["name"] = clean
        }, null, ct).ConfigureAwait(false);
        return HostedRoomParser.Room((reply as JsonObject)?["room"]) ?? throw new RoomException(RoomFailureKind.Failed, "The gateway didn't say what it renamed.");
    }

    /// <summary>Gives the failed turn <paramref name="taskId"/> another go.</summary>
    public Task RetryAsync(string roomId, string taskId, CancellationToken ct = default) =>
        CallAsync("groups.retry", new JsonObject { ["room_id"] = RequireId(roomId), ["task_id"] = RequireId(taskId) }, null, ct);

    /// <summary>
    /// Answers one pending approval with the coordinates the room's state carried; only <c>once</c> or <c>deny</c> are accepted
    /// (never an "always" grant from here). The whole tuple must match what is pending, so a stale answer is refused.
    /// </summary>
    public Task ApproveAsync(string roomId, RoomPendingAction action, string choice, string requestId, CancellationToken ct = default)
    {
        if (choice is not ("once" or "deny")) throw new RoomException(RoomFailureKind.Refused, "Only “allow once” or “deny” can be answered from here.");
        if (action.Kind != "approval" || action.MemberId is null || action.TaskId is null || action.ExecutionGeneration is null)
            throw new RoomException(RoomFailureKind.Refused, "That is not an approval this app can answer.");
        return CallAsync("groups.approve", new JsonObject
        {
            ["room_id"] = RequireId(roomId), ["member_id"] = action.MemberId, ["task_id"] = action.TaskId,
            ["execution_generation"] = action.ExecutionGeneration, ["choice"] = choice, ["request_id"] = RequireId(requestId)
        }, SendTimeout, ct);
    }

    private static string RequireId(string id) =>
        string.IsNullOrWhiteSpace(id) ? throw new RoomException(RoomFailureKind.Refused, "A room id is missing.") : id;

    private async Task<JsonNode?> CallAsync(string method, JsonObject args, TimeSpan? timeout, CancellationToken ct)
    {
        var p = peer() ?? throw new RoomException(RoomFailureKind.NotConnected, "Not connected to the gateway.");
        try
        {
            return await p.RequestAsync(method, args, timeout, ct).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.Code == -32601 || ex.Code == 4010 || ex.Message.Contains("method not found", StringComparison.OrdinalIgnoreCase))
        {
            throw new RoomException(RoomFailureKind.Unsupported, "This gateway does not host rooms.");
        }
        catch (RpcException ex)
        {
            throw new RoomException(RoomFailureKind.Failed, string.IsNullOrWhiteSpace(ex.Message) ? "The gateway refused the request." : ex.Message);
        }
        catch (RpcTimeoutException)
        {
            throw new RoomException(RoomFailureKind.Failed, "The gateway didn't answer in time. Check the room before sending again.");
        }
        catch (Exception ex) when (ex is TransportClosedException or System.Net.WebSockets.WebSocketException)
        {
            throw new RoomException(RoomFailureKind.NotConnected, "The connection to the gateway dropped. Check the room before sending again.");
        }
    }
}
