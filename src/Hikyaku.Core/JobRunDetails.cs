using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hikyaku.Core;

/// <summary>One run's result as the Inbox shows it, and whether it came whole from the hikyaku-cron plugin.</summary>
public sealed record RunDetail(RunText Result, bool FromPlugin);

/// <summary>
/// Fetches what a scheduled run found. An agent run is a stored chat: its result is the last answer the agent wrote. A
/// script-only run is an output document the sessions routes do not serve; the gateway's hikyaku-cron plugin
/// (hermes-plugin/hikyaku-cron) returns it whole through <c>command.dispatch</c>, as the herald-push Inbox once did. Without
/// the plugin (not a command, an unreadable answer, no answer in time) the gateway's short preview is used, and its absence
/// is remembered for this connection so it is not asked again. A dropped connection is not absence.
/// </summary>
public sealed class JobRunDetails(
    Func<string, string?, int, CancellationToken, Task<IReadOnlyList<SessionMessage>>> messages,
    Func<IJsonRpcPeer?> rpc)
{
    public const string PluginCommand = "hikyaku-cron";
    private const string ScriptPrefix = "cron_output:";
    private static readonly TimeSpan PluginTimeout = TimeSpan.FromSeconds(5);
    private int _plugin;                                   // 0 unknown, 1 present, -1 absent

    /// <summary>Whether the gateway answered as having the plugin; null until a script run was asked for.</summary>
    public bool? PluginPresent => _plugin switch { 1 => true, -1 => false, _ => null };

    public async Task<RunDetail?> LoadAsync(JobResult result, CancellationToken ct)
    {
        if (!result.IsScriptOutput)
        {
            var rows = await messages(result.Run.Id, result.Profile.Length > 0 ? result.Profile : null, 50, ct).ConfigureAwait(false);
            var answer = rows.LastOrDefault(m => m.Role == "assistant" && m.Content.Trim().Length > 0);
            if (answer is null) return null;
            var content = answer.Content.Trim();
            // Hermes does not deliver [SILENT] / NO_REPLY; the Inbox says "Nothing new" for the same answers.
            return CronRunText.IsCronSilence(content)
                ? new RunDetail(new RunText(RunStatus.Silent, "", null), false)
                : new RunDetail(new RunText(RunStatus.Ok, content, null), false);
        }
        if (_plugin >= 0 && await FromPluginAsync(result, ct).ConfigureAwait(false) is { } whole)
            return new RunDetail(CronRunText.Parse(whole), true);
        return new RunDetail(CronRunText.Parse(result.Preview), false);
    }

    private async Task<string?> FromPluginAsync(JobResult result, CancellationToken ct)
    {
        var id = result.Run.Id.StartsWith(ScriptPrefix, StringComparison.Ordinal) ? result.Run.Id[ScriptPrefix.Length..] : "";
        var colon = id.IndexOf(':');
        if (colon <= 0 || rpc() is not { } peer) return null;
        var (job, run) = (id[..colon], id[(colon + 1)..]);
        var profile = result.Profile.Length > 0 ? result.Profile : "default";
        try
        {
            var reply = await peer.RequestAsync("command.dispatch", new JsonObject
            {
                ["name"] = PluginCommand,
                ["arg"] = $"output {profile} {job} {run}"
            }, PluginTimeout, ct).ConfigureAwait(false);
            if (reply is not JsonObject { } obj || obj["type"]?.GetValue<string>() != "plugin"
                || obj["output"]?.GetValue<string>() is not { } output
                || JsonNode.Parse(output) is not JsonObject answer)
            {
                _plugin = -1;
                return null;
            }
            _plugin = 1;
            return answer["ok"]?.GetValue<bool>() == true ? answer["text"]?.GetValue<string>() : null;
        }
        catch (Exception ex) when (ex is RpcTimeoutException or TimeoutException or JsonException or FormatException
                                       || ex is RpcException rpc && rpc.Code == InboxApi.NotACommand
                                       || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Absent only when the plugin is not installed, its answer cannot be read, or it does not answer in time.
            // A dropped connection (ObjectDisposedException) or any other gateway error is not absence: the next try asks again.
            _plugin = -1;
            return null;
        }
    }
}
