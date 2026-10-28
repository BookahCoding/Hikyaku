using System.IO;
using System.Text.Json;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>Non-secret connection settings (no tokens). Profile selection lives in ProfileSelectionService.</summary>
public sealed class AppSettings
{
    public string GatewayId { get; set; } = "";
    public string GatewayUrl { get; set; } = "";
    /// <summary>Dashboard username (non-secret). Password is DPAPI-only.</summary>
    public string Username { get; set; } = "";
    public List<string> KnownProfileIds { get; set; } = new();
    /// <summary>The profile chosen in the sidebar's profile switcher (unset: the gateway's "default" profile).</summary>
    public string? ActiveProfileId { get; set; }
    /// <summary>Profile id → the gateway's display name, remembered from the last profile list (shown in the switcher).</summary>
    public Dictionary<string, string> ProfileNames { get; set; } = new();
    /// <summary>Legacy: Ukiyo-e light when true. Prefer <see cref="Appearance"/>.</summary>
    public bool LightTheme { get; set; }
    /// <summary>Hokusai theme family id (washi-ink, great-wave, red-fuji, pine-mist).</summary>
    public string ThemeId { get; set; } = "washi-ink";
    /// <summary>light | dark | system</summary>
    public string Appearance { get; set; } = "system";
    /// <summary>Per-device sidebar collapsed preference.</summary>
    public bool SidebarCollapsed { get; set; }
    /// <summary>Stable desktop device id for herald-push cron Inbox (not a phone push registration).</summary>
    public string DesktopDeviceId { get; set; } = "";
    /// <summary>
    /// simple | advanced. Visibility only. Unset in an existing settings file means the install predates the choice and keeps the
    /// full chrome (advanced); a brand-new install starts simple.
    /// </summary>
    public string? Experience { get; set; }

    public bool IsAdvanced => !string.Equals(Experience?.Trim(), "simple", StringComparison.OrdinalIgnoreCase);

    public void SetAdvanced(bool advanced) => Experience = advanced ? "advanced" : "simple";

    /// <summary>How much the whole window is scaled (100, 115 or 130 percent: Normal / Larger / Largest). Anything else becomes the nearest choice.</summary>
    public int TextScalePercent { get; set; } = 100;

    /// <summary>When false, ToastNotificationService skips OS delivery (gate still applies).</summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>One quiet, anonymous check for a newer version when the app starts (nothing is downloaded until asked).</summary>
    public bool CheckUpdatesOnStart { get; set; } = true;

    public AppearanceMode AppearanceMode => Appearance?.Trim().ToLowerInvariant() switch
    {
        "light" => AppearanceMode.Light,
        "dark" => AppearanceMode.Dark,
        _ => AppearanceMode.System
    };

    public void SetAppearanceMode(AppearanceMode mode)
    {
        Appearance = mode switch
        {
            AppearanceMode.Light => "light",
            AppearanceMode.Dark => "dark",
            _ => "system"
        };
        LightTheme = AppearanceResolver.Resolve(mode, ThemeService.OsPrefersDark());
    }

    public static AppSettings Load(string path)
    {
        try
        {
            var existed = File.Exists(path);
            var s = existed ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new();
            if (!existed) s.Experience = "simple";      // a new install starts simple; an older file without the key stays advanced
            // Migrate pre-CP7 installs that only had LightTheme.
            if (string.IsNullOrWhiteSpace(s.ThemeId)) s.ThemeId = "washi-ink";
            if (string.IsNullOrWhiteSpace(s.Appearance))
                s.Appearance = s.LightTheme ? "light" : "dark";
            return s;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return new(); }   // unreadable file: Experience unset = advanced
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public Uri? ParsedUrl => Uri.TryCreate(GatewayUrl, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http") ? u : null;
}
