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
    IFolderBridgePrompts prompts)
{
    private const string Command = "hikyaku-files";

    public bool PluginMissing { get; private set; }

    public async Task TickAsync(CancellationToken ct)
    {
        if (rpc() is not { } peer) return;
        if (profile() is not { } key) return;

        var pending = await CommandAsync(peer, "pending", ct).ConfigureAwait(false);
        if (PluginMissing || pending is null) return;
        if (pending["ok"]?.GetValue<bool>() != true || pending["pending"]?.GetValue<bool>() != true) return;

        var id = pending["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id)) return;
        var op = pending["op"]?.GetValue<string>() ?? "";
        var path = pending["path"]?.GetValue<string>() ?? "";
        var askedProfile = pending["profile"]?.GetValue<string>() ?? "";
        if (!string.Equals(askedProfile, key.ProfileId, StringComparison.Ordinal))
        {
            await AnswerAsync(peer, id, Fail("invalid"), ct).ConfigureAwait(false);
            return;
        }

        if (!TryWriteBytes(pending, op, out var write, out var writeError))
        {
            await AnswerAsync(peer, id, Fail(writeError), ct).ConfigureAwait(false);
            return;
        }

        var access = await store.GetAsync(key, ct).ConfigureAwait(false);
        var ready = await ResolveAsync(peer, id, key, access, op, path, ct).ConfigureAwait(false);
        if (ready is null) return;

        var hold = await CommandAsync(peer, "hold " + id, ct).ConfigureAwait(false);
        if (PluginMissing || hold is null || hold["ok"]?.GetValue<bool>() != true) return;

        var result = FolderBridgeGate.Apply(ready.Value.Access, op, path, write, ready.Value.DeleteYes);
        await AnswerAsync(peer, id, ResultJson(result), ct).ConfigureAwait(false);
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
                    var allow = await prompts.AllowNetworkAsync(path, ct).ConfigureAwait(false);
                    if (!allow)
                    {
                        await AnswerAsync(peer, id, Fail("cancelled"), ct).ConfigureAwait(false);
                        return null;
                    }
                    await store.AddFolderAsync(key, ShareRoot(path), true, ct).ConfigureAwait(false);
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

    /// <summary>The share root is the server and the share, which is what Allow remembers.</summary>
    private static string ShareRoot(string path)
    {
        var normalized = path.Replace('/', '\\');
        var parts = normalized.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal) && parts.Length >= 2)
            return @"\\" + parts[0] + @"\" + parts[1];
        return normalized.TrimEnd('\\');
    }
}
