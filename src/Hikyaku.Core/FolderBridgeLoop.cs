using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hikyaku.Core;

public interface IFolderBridgePrompts
{
    Task<string?> ChooseFolderAsync(string askedPath, CancellationToken ct);
    Task<bool> AllowNetworkAsync(string path, CancellationToken ct);
    Task<bool> AllowKeyFileAsync(string path, CancellationToken ct);
    Task<bool> ConfirmDeleteAsync(string path, bool folder, CancellationToken ct);
}

/// <summary>
/// One poll of the hikyaku-files queue. Asks on this PC, and touches a file only while the lease is held.
/// </summary>
public sealed class FolderBridgeLoop(
    Func<IJsonRpcPeer?> rpc,
    FolderBridgeStore store,
    Func<ProfileKey?> profile,
    IFolderBridgePrompts prompts,
    Func<string, bool>? profileOnGateway = null)
{
    private const string Command = "hikyaku-files";

    public bool PluginMissing { get; private set; }

    private bool _storedThisTick;

    /// <summary>One poll. Returns true when this tick remembered a folder or a key file.</summary>
    public async Task<bool> TickAsync(CancellationToken ct)
    {
        _storedThisTick = false;
        if (rpc() is not { } peer) return false;
        if (profile() is not { } key) return false;

        var pending = await CommandAsync(peer, "pending", ct).ConfigureAwait(false);
        if (PluginMissing || pending is null) return false;
        if (pending["ok"]?.GetValue<bool>() != true || pending["pending"]?.GetValue<bool>() != true) return false;

        var id = pending["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id)) return false;
        var op = pending["op"]?.GetValue<string>() ?? "";
        var path = pending["path"]?.GetValue<string>() ?? "";
        var askedProfile = pending["profile"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(askedProfile) || !OnThisGateway(key, askedProfile))
        {
            await AnswerAsync(peer, id, Fail("invalid"), ct).ConfigureAwait(false);
            return false;
        }

        if (!TryWriteBytes(pending, op, out var write, out var writeError))
        {
            await AnswerAsync(peer, id, Fail(writeError), ct).ConfigureAwait(false);
            return false;
        }

        var asked = new ProfileKey(key.GatewayId, askedProfile);
        var access = await store.GetAsync(asked, ct).ConfigureAwait(false);
        var ready = await ResolveAsync(peer, id, asked, access, op, path, ct).ConfigureAwait(false);
        if (ready is null) return _storedThisTick;

        var hold = await CommandAsync(peer, "hold " + id, ct).ConfigureAwait(false);
        if (PluginMissing || hold is null || hold["ok"]?.GetValue<bool>() != true) return _storedThisTick;

        var result = FolderBridgeGate.Apply(ready.Value.Access, op, path, write, ready.Value.DeleteYes);
        await AnswerAsync(peer, id, ResultJson(result), ct).ConfigureAwait(false);
        return _storedThisTick;
    }

    private async Task<Ready?> ResolveAsync(
        IJsonRpcPeer peer, string id, ProfileKey key, BridgeProfile access, string op, string path, CancellationToken ct)
    {
        var deleteYes = false;
        var decision = FolderBridgeGate.Decide(access, op, path);
        for (var hop = 0; hop < 4; hop++)
        {
            switch (decision.Ask)
            {
                case BridgeAsk.None:
                    return new Ready(access, deleteYes);
                case BridgeAsk.ChooseFolder:
                {
                    var chosen = await prompts.ChooseFolderAsync(decision.Path, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(chosen))
                    {
                        await AnswerAsync(peer, id, Fail("cancelled"), ct).ConfigureAwait(false);
                        return null;
                    }
                    await store.AddFolderAsync(key, chosen, false, ct).ConfigureAwait(false);
                    _storedThisTick = true;
                    access = await store.GetAsync(key, ct).ConfigureAwait(false);
                    decision = FolderBridgeGate.Decide(access, op, path);
                    if (decision.Ask == BridgeAsk.ChooseFolder)
                    {
                        await AnswerAsync(peer, id, Fail("outside"), ct).ConfigureAwait(false);
                        return null;
                    }
                    break;
                }
                case BridgeAsk.AllowNetwork:
                {
                    var allow = await prompts.AllowNetworkAsync(decision.Path, ct).ConfigureAwait(false);
                    if (!allow)
                    {
                        await AnswerAsync(peer, id, Fail("cancelled"), ct).ConfigureAwait(false);
                        return null;
                    }
                    await store.AddFolderAsync(key, RememberedDirectory(decision.Path, op), true, ct).ConfigureAwait(false);
                    _storedThisTick = true;
                    access = await store.GetAsync(key, ct).ConfigureAwait(false);
                    decision = FolderBridgeGate.Decide(access, op, path);
                    if (decision.Ask == BridgeAsk.AllowNetwork)
                    {
                        await AnswerAsync(peer, id, Fail("outside"), ct).ConfigureAwait(false);
                        return null;
                    }
                    break;
                }
                case BridgeAsk.AllowKeyFile:
                {
                    var allow = await prompts.AllowKeyFileAsync(decision.Path, ct).ConfigureAwait(false);
                    if (!allow)
                    {
                        await AnswerAsync(peer, id, Fail("cancelled"), ct).ConfigureAwait(false);
                        return null;
                    }
                    await store.AllowKeyFileAsync(key, decision.Path, ct).ConfigureAwait(false);
                    _storedThisTick = true;
                    access = await store.GetAsync(key, ct).ConfigureAwait(false);
                    decision = FolderBridgeGate.Decide(access, op, path);
                    break;
                }
                case BridgeAsk.ConfirmDelete:
                {
                    deleteYes = await prompts.ConfirmDeleteAsync(decision.Path, op == "delete_folder", ct).ConfigureAwait(false);
                    if (!deleteYes)
                    {
                        await AnswerAsync(peer, id, Fail("cancelled"), ct).ConfigureAwait(false);
                        return null;
                    }
                    return new Ready(access, true);
                }
            }
        }

        await AnswerAsync(peer, id, Fail("invalid"), ct).ConfigureAwait(false);
        return null;
    }

    private bool OnThisGateway(ProfileKey sidebar, string askedProfile) =>
        profileOnGateway is { } known
            ? known(askedProfile)
            : string.Equals(askedProfile, sidebar.ProfileId, StringComparison.Ordinal);

    private readonly record struct Ready(BridgeProfile Access, bool DeleteYes);

    private async Task<JsonObject?> CommandAsync(IJsonRpcPeer peer, string arg, CancellationToken ct)
    {
        JsonNode? reply;
        try
        {
            reply = await peer.RequestAsync("command.dispatch", new JsonObject
            {
                ["name"] = Command,
                ["arg"] = arg
            }, ct: ct).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.Code == InboxApi.NotACommand)
        {
            PluginMissing = true;
            return null;
        }

        if (reply is not JsonObject obj
            || obj["type"]?.GetValue<string>() != "plugin"
            || obj["output"]?.GetValue<string>() is not { Length: > 0 } output)
            return null;

        try
        {
            var parsed = JsonNode.Parse(output) as JsonObject;
            if (parsed is not null) PluginMissing = false;
            return parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task AnswerAsync(IJsonRpcPeer peer, string id, string json, CancellationToken ct) =>
        CommandAsync(peer, "answer " + id + " " + json, ct);

    private static bool TryWriteBytes(JsonObject pending, string op, out byte[]? write, out string error)
    {
        write = null;
        error = "";
        if (op != "write") return true;
        var hasText = pending["text"] is not null;
        var hasData = pending["data"] is not null;
        if (hasText == hasData)
        {
            error = "invalid";
            return false;
        }
        if (hasText)
        {
            write = Encoding.UTF8.GetBytes(pending["text"]!.GetValue<string>());
            return true;
        }
        try
        {
            write = Convert.FromBase64String(pending["data"]!.GetValue<string>());
            return true;
        }
        catch (FormatException)
        {
            error = "invalid";
            return false;
        }
    }

    private static string Fail(string error) =>
        new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString();

    private static string ResultJson(BridgeResult result)
    {
        var body = new JsonObject { ["ok"] = result.Ok };
        if (!result.Ok)
        {
            body["error"] = result.Error;
            return body.ToJsonString();
        }
        if (result.Encoding.Length > 0) body["encoding"] = result.Encoding;
        if (result.Encoding == "utf-8") body["text"] = result.Text;
        else if (result.Encoding == "base64" && result.Bytes is { } bytes)
            body["data"] = Convert.ToBase64String(bytes);
        if (result.Entries is { } entries)
        {
            var list = new JsonArray();
            foreach (var entry in entries)
                list.Add(new JsonObject { ["name"] = entry.Name, ["dir"] = entry.Dir });
            body["entries"] = list;
        }
        return body.ToJsonString();
    }

    /// <summary>The folder Allow remembers: the directory of the path that was shown, not the whole share.</summary>
    private static string RememberedDirectory(string shown, string op)
    {
        var full = shown;
        try { full = Path.GetFullPath(shown); } catch { /* the shown path is already the one to keep */ }
        string dir;
        if (op is "list" or "delete_folder" && !File.Exists(full))
            dir = full;
        else
        {
            var parent = Path.GetDirectoryName(full);
            dir = string.IsNullOrEmpty(parent) ? full : parent;
        }
        if (dir.Length == 3 && dir[1] == ':' && dir[2] == '\\') return dir;
        return dir.TrimEnd('\\');
    }
}
