using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>Stage 38: the schema-driven settings editor's pure rules (Advanced only). No gateway.</summary>
public class Checkpoint38TypedSettingsTests
{
    private static SettingEntry E(string path, string type, JsonNode? value = null, params string[] options) =>
        new(path, type, "desc " + path, path.Contains('.') ? path.Split('.')[0] : "general", options, value);

    private static ConfigSchemaInfo Schema() => new(new Dictionary<string, ConfigFieldInfo>
    {
        ["model"] = new("string", "The model", "general", []),
        ["approvals.mode"] = new("select", "How dangerous commands are approved", "approvals", ["manual", "smart", "off"]),
        ["voice.enabled"] = new("boolean", "Speak replies", "voice", []),
        ["agent.max_turns"] = new("number", "Turn limit", "agent", []),
        ["tools.disabled"] = new("list", "Disabled tools", "tools", []),
        ["api_key"] = new("string", "Provider key", "general", []),
    });

    [Fact]
    public void The_schema_and_the_values_flatten_into_entries_grouped_by_category_general_first()
    {
        var values = JsonNode.Parse("""{"model":"big","approvals":{"mode":"manual"},"voice":{"enabled":true},"agent":{"max_turns":30},"api_key":"sk-abc"}""")!.AsObject();
        var all = TypedSettings.Flatten(Schema(), values);
        Assert.Equal(["api_key", "model", "agent.max_turns", "approvals.mode", "tools.disabled", "voice.enabled"], all.Select(e => e.Path));
        Assert.Equal("manual", TypedSettings.Display(all.Single(e => e.Path == "approvals.mode").Value));
        Assert.Equal("true", all.Single(e => e.Path == "voice.enabled").DisplayValue);
        Assert.Null(all.Single(e => e.Path == "tools.disabled").Value);                  // a value the file does not have is simply missing
    }

    [Fact]
    public void An_unquoted_yaml_off_still_counts_as_approvals_off_and_odd_values_are_unknown_not_errors()
    {
        Assert.Equal(ApprovalMode.Off, new ConfigValues(JsonNode.Parse("""{"approvals":{"mode":false}}""")!.AsObject()).ApprovalsMode);
        Assert.Equal(ApprovalMode.Manual, new ConfigValues(JsonNode.Parse("""{"approvals":{"mode":true}}""")!.AsObject()).ApprovalsMode);
        Assert.Equal(ApprovalMode.Manual, new ConfigValues(JsonNode.Parse("""{"approvals":{"mode":7}}""")!.AsObject()).ApprovalsMode);
        Assert.Equal(ApprovalMode.Manual, new ConfigValues(JsonNode.Parse("""{"approvals":{"mode":{"x":1}}}""")!.AsObject()).ApprovalsMode);
    }

    [Fact]
    public void Secrets_are_hidden_and_never_editable_and_only_the_four_simple_types_are_editable()
    {
        var all = TypedSettings.Flatten(Schema(), JsonNode.Parse("""{"api_key":"sk-abc"}""")!.AsObject());
        var key = all.Single(e => e.Path == "api_key");
        Assert.True(key.IsSecret);
        Assert.Equal("(hidden)", key.DisplayValue);                                       // the value never reaches the screen
        Assert.False(key.IsEditable);
        Assert.False(TypedSettings.ParseEdit(key, "sk-new").Ok);
        Assert.False(all.Single(e => e.Path == "tools.disabled").IsEditable);             // a list is edited in config.yaml
        Assert.All(new[] { "model", "approvals.mode", "voice.enabled", "agent.max_turns" }, p => Assert.True(all.Single(e => e.Path == p).IsEditable));
        Assert.True(TypedSettings.IsSecretPath("mcp.github.TOKEN"));
        Assert.True(TypedSettings.IsSecretPath("openai.api-key"));
        Assert.True(TypedSettings.IsSecretPath("foo.auth_header"));
        Assert.True(TypedSettings.IsSecretPath("foo.bearer"));
        Assert.False(TypedSettings.IsSecretPath("agent.author"));
        Assert.Null(key.Value);                                                           // the secret's value is never even held
        Assert.False(TypedSettings.IsSecretPath("agent.max_turns"));
    }

    [Fact]
    public void An_edit_is_checked_against_the_fields_type()
    {
        Assert.True((bool)TypedSettings.ParseEdit(E("voice.enabled", "boolean"), "true").Value!);
        Assert.False(TypedSettings.ParseEdit(E("voice.enabled", "boolean"), "maybe").Ok);
        Assert.Equal(40, (long)TypedSettings.ParseEdit(E("agent.max_turns", "number"), " 40 ").Value!);
        Assert.Equal(0.5, (double)TypedSettings.ParseEdit(E("agent.temp", "number"), "0.5").Value!);
        Assert.False(TypedSettings.ParseEdit(E("agent.max_turns", "number"), "lots").Ok);
        Assert.False(TypedSettings.ParseEdit(E("agent.max_turns", "number"), "NaN").Ok);
        Assert.False(TypedSettings.ParseEdit(E("agent.max_turns", "number"), "").Ok);
        var mode = E("approvals.mode", "select", null, "manual", "smart", "off");
        Assert.Equal("smart", (string)TypedSettings.ParseEdit(mode, "smart").Value!);
        Assert.False(TypedSettings.ParseEdit(mode, "anything").Ok);                       // only the gateway's own options
        Assert.False(TypedSettings.ParseEdit(mode, "SMART").Ok);
        Assert.Equal("", (string)TypedSettings.ParseEdit(E("model", "string"), "").Value!);
        Assert.False(TypedSettings.ParseEdit(E("model", "string"), new string('a', TypedSettings.MaxTextChars + 1)).Ok);
        Assert.False(TypedSettings.ParseEdit(E("model", "string"), "a\0b").Ok);
        Assert.False(TypedSettings.ParseEdit(E("tools.disabled", "list"), "x").Ok);
    }

    [Fact]
    public void A_select_that_allows_not_set_accepts_the_empty_choice()
    {
        var tz = E("timezone", "select", null, "", "Europe/Athens");
        Assert.Equal("", (string)TypedSettings.ParseEdit(tz, "").Value!);
    }

    [Fact]
    public void The_patch_holds_only_the_one_nested_key()
    {
        var patch = TypedSettings.PatchFor("approvals.mode", JsonValue.Create("off")!);
        Assert.Equal("off", (string)patch["approvals"]!["mode"]!);
        Assert.Single(patch);
        Assert.Single(patch["approvals"]!.AsObject());
        Assert.Equal(7, (int)TypedSettings.PatchFor("top", JsonValue.Create(7)!)["top"]!);
    }

    [Fact]
    public void A_value_equal_to_the_current_one_is_not_a_change()
    {
        Assert.True(TypedSettings.Unchanged(E("a", "number", JsonValue.Create(30)), JsonValue.Create(30L)));
        Assert.True(TypedSettings.Unchanged(E("a", "number", JsonValue.Create(30)), JsonValue.Create(30.0)));
        Assert.True(TypedSettings.Unchanged(E("a", "boolean", JsonValue.Create(true)), JsonValue.Create(true)));
        Assert.True(TypedSettings.Unchanged(E("a", "string", JsonValue.Create("x")), JsonValue.Create("x")));
        Assert.False(TypedSettings.Unchanged(E("a", "string", JsonValue.Create("x")), JsonValue.Create("X")));
        Assert.False(TypedSettings.Unchanged(E("a", "string", null), JsonValue.Create("")));        // missing is not the same as empty
        Assert.True(TypedSettings.Unchanged(E("a", "string", null), null));                          // missing and still unset: nothing to send
        Assert.False(TypedSettings.Unchanged(E("a", "boolean", JsonValue.Create(true)), JsonValue.Create(false)));
    }

    [Fact]
    public void Settings_that_govern_what_the_agent_may_do_carry_an_extra_warning()
    {
        Assert.NotNull(TypedSettings.RiskWarning("approvals.mode"));
        Assert.NotNull(TypedSettings.RiskWarning("terminal.allow_commands"));
        Assert.NotNull(TypedSettings.RiskWarning("security.trust_level"));
        Assert.Null(TypedSettings.RiskWarning("voice.enabled"));
        Assert.Null(TypedSettings.RiskWarning("agent.max_turns"));
    }

    [Fact]
    public void Search_matches_every_word_in_the_path_description_or_category()
    {
        var all = TypedSettings.Flatten(Schema(), new JsonObject());
        Assert.Equal(["voice.enabled"], TypedSettings.Filter(all, "speak").Select(e => e.Path));
        Assert.Equal(["approvals.mode"], TypedSettings.Filter(all, "APPROVALS dangerous").Select(e => e.Path));
        Assert.Empty(TypedSettings.Filter(all, "speak dangerous"));
        Assert.Equal(all.Count, TypedSettings.Filter(all, "  ").Count());
    }
}
