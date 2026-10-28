using System.Text.Json.Nodes;

namespace Hikyaku.Core;

/// <summary>
/// A voice chat's tuning from the profile's <c>voice:</c> config (<c>silence_duration</c> in seconds, <c>barge_in</c>,
/// <c>barge_in_threshold_multiplier</c>, <c>barge_in_grace_seconds</c>, <c>stop_phrases</c>), the same keys Hermes Desktop and
/// the CLI honour. Anything missing or unreadable keeps the default; <see cref="StopPhrases"/> null means the default list.
/// </summary>
public sealed record VoiceChatSettings(VoiceActivity Activity, BargeInSettings BargeIn, IReadOnlyList<string>? StopPhrases)
{
    public static VoiceChatSettings Defaults { get; } = new(new VoiceActivity(), new BargeInSettings(), null);

    public static VoiceChatSettings From(ConfigValues? config)
    {
        if (config?.Root["voice"] is not JsonObject voice) return Defaults;
        double? Number(string key) =>
            voice[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
        bool? Flag(string key) => voice[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

        var activity = Defaults.Activity;
        if (Number("silence_duration") is { } seconds && seconds is >= 0.3 and <= 10)
            activity = activity with { SilenceMs = (long)Math.Round(seconds * 1000) };
        var bargeIn = Defaults.BargeIn;
        if (Flag("barge_in") is { } enabled) bargeIn = bargeIn with { Enabled = enabled };
        if (Number("barge_in_threshold_multiplier") is { } m && m > 0) bargeIn = bargeIn with { Multiplier = m };
        if (Number("barge_in_grace_seconds") is { } grace && grace is >= 0 and <= 5)
            bargeIn = bargeIn with { GraceMs = (long)Math.Round(grace * 1000) };
        IReadOnlyList<string>? phrases = voice["stop_phrases"] is JsonArray list
            ? list.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>()
                .Where(s => s.Trim().Length > 0).ToList()
            : null;
        return new VoiceChatSettings(activity, bargeIn, phrases);
    }
}
