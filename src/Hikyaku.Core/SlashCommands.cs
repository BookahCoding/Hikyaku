namespace Hikyaku.Core;

/// <summary>A command typed in the chat box (<c>/name argument</c>) that this app runs itself.</summary>
public sealed record SlashCommand(string Name, string Usage, string Description, bool AdvancedOnly);

/// <summary>
/// The client-side half of the slash commands (names and aliases follow BookahHermes <c>Slash.kt</c> @ 5f00986b, which follows
/// Hermes Desktop's command table). They replace the Model / Reasoning / Fast / YOLO chips that crowded the chat box.
/// Anything else that starts with "/" is sent as an ordinary message, as before.
/// </summary>
public static class SlashCommands
{
    public static readonly IReadOnlyList<SlashCommand> All =
    [
        new("new", "", "Start a new chat", false),
        new("stop", "", "Stop the reply that is running", false),
        new("btw", "<question>", "Ask a side question without interrupting the reply", false),
        new("bg", "<task>", "Run a task in the background beside this chat", false),
        new("model", "[name]", "Choose the model for this chat", true),
        new("reasoning", "none | low | medium | high", "How much the model thinks before answering", true),
        new("fast", "on | off", "Faster replies where the model offers it", true),
        new("yolo", "on | off", "Run tools in this chat without asking first (asks you to confirm)", true),
        new("branch", "", "Copy this chat into a new chat", true),
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["reset"] = "new",
        ["fork"] = "branch",
        ["background"] = "bg",
        ["effort"] = "reasoning",
    };

    public static readonly IReadOnlyList<string> ReasoningLevels = ["none", "low", "medium", "high"];

    /// <summary>"/name rest" for a known command (alias resolved, name lower case). False for plain text and unknown commands.</summary>
    public static bool TryParse(string? input, out string name, out string argument)
    {
        name = argument = "";
        var text = (input ?? "").Trim();
        if (text.Length < 2 || text[0] != '/') return false;
        var space = text.IndexOfAny([' ', '\t', '\n', '\r']);
        var raw = (space < 0 ? text[1..] : text[1..space]).ToLowerInvariant();
        if (Aliases.TryGetValue(raw, out var canonical)) raw = canonical;
        if (All.All(c => c.Name != raw)) return false;
        name = raw;
        argument = space < 0 ? "" : text[(space + 1)..].Trim();
        return true;
    }

    /// <summary>Suggestions while the person is still typing the command name ("/" or "/mo"), never once an argument starts.</summary>
    public static IReadOnlyList<SlashCommand> Suggest(string? input, bool advanced)
    {
        var text = input ?? "";
        if (text.Length == 0 || text[0] != '/' || text.IndexOfAny([' ', '\t', '\n', '\r']) >= 0) return [];
        var prefix = text[1..];
        return All.Where(c => (advanced || !c.AdvancedOnly) && c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>"on" / "off" (also yes / no, true / false, 1 / 0); null for anything else, including nothing.</summary>
    public static bool? OnOff(string argument) => argument.Trim().ToLowerInvariant() switch
    {
        "on" or "yes" or "true" or "1" => true,
        "off" or "no" or "false" or "0" => false,
        _ => null
    };
}
