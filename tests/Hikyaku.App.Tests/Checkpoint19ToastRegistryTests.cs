using Hikyaku.App;
using Hikyaku.Core;
using Microsoft.Win32;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 19a — the real per-user registry registration, exercised against a throwaway id under HKCU and removed afterwards, plus
/// the presenter's refusal when the app is not registered. No toast is shown.
/// </summary>
public sealed class Checkpoint19ToastRegistryTests : IDisposable
{
    private readonly string _id = "Hikyaku.Test." + Guid.NewGuid().ToString("N")[..12];
    private readonly HkcuToastRegistry _registry = new();

    public void Dispose() => _registry.Unregister(_id);

    private bool KeyExists() =>
        Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\" + _id) is { } k && DisposeAndTrue(k);

    private static bool DisposeAndTrue(IDisposable d) { d.Dispose(); return true; }

    [Fact]
    public void Register_writes_one_per_user_key_with_a_display_name_and_unregister_removes_it()
    {
        Assert.False(_registry.IsRegistered(_id));
        _registry.Register(_id, "Hikyaku test");
        Assert.True(_registry.IsRegistered(_id));
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\" + _id))
        {
            Assert.Equal("Hikyaku test", key!.GetValue("DisplayName"));
            Assert.Equal(new[] { "DisplayName" }, key.GetValueNames());          // nothing else is written
            Assert.Empty(key.GetSubKeyNames());
        }
        _registry.Unregister(_id);
        Assert.False(_registry.IsRegistered(_id));
        Assert.False(KeyExists());
    }

    [Fact]
    public void Registering_twice_and_unregistering_something_absent_are_harmless()
    {
        _registry.Register(_id, "One");
        _registry.Register(_id, "Two");
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\" + _id))
            Assert.Equal("Two", key!.GetValue("DisplayName"));
        _registry.Unregister(_id);
        _registry.Unregister(_id);                                                  // no throw
        Assert.False(_registry.IsRegistered(_id));
    }

    [Theory]
    [InlineData(@"..\Run")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("Software\\Microsoft")]
    public void Ids_that_could_address_another_key_are_refused_before_any_registry_access(string bad)
    {
        Assert.Throws<ArgumentException>(() => _registry.Register(bad, "x"));
        Assert.Throws<ArgumentException>(() => _registry.IsRegistered(bad));
        Assert.Throws<ArgumentException>(() => _registry.Unregister(bad));
    }

    [Fact]
    public void A_blank_or_hostile_display_name_is_cleaned_before_it_is_stored()
    {
        _registry.Register(_id, "  \u0007\n ");
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\" + _id);
        Assert.Equal("Hikyaku", key!.GetValue("DisplayName"));
    }

    [Fact]
    public void The_presenter_refuses_to_show_anything_when_the_app_is_not_registered()
    {
        var presenter = new WindowsToastPresenter(_id, _registry);                  // _id is not registered
        var result = presenter.Show("Title", "Body", null);
        Assert.Equal(ToastObservation.Unavailable, result.Observation);
        Assert.Contains("Settings", result.Detail);
        Assert.False(_registry.IsRegistered(_id));                                  // the presenter never registers by itself
    }
}
