namespace Hikyaku.Core;

/// <summary>A suggestion on an empty chat. Choosing it fills the message box; it does not send.</summary>
public sealed record StarterPrompt(string Label, string Draft);

/// <summary>Short, everyday ways to start a chat. Shown above the box until the first message, in both views.</summary>
public static class StarterPrompts
{
    public static IReadOnlyList<StarterPrompt> All { get; } =
    [
        new("Help me write", "Help me write "),
        new("Explain something", "Explain this in plain words:\n"),
        new("Make a plan", "Help me plan "),
        new("Check my writing", "Read this and tell me what to change:\n"),
    ];
}
