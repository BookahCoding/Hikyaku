using System.Text.Json.Nodes;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

public class HeraldModelMergeTests
{
    [Fact]
    public void A_keyless_stub_does_not_hide_the_custom_catalog_that_follows_it()
    {
        var catalog = ModelCatalog.Parse(JsonNode.Parse("""
            {"providers":[
              {"slug":"custom","name":"Custom endpoint","authenticated":false,"models":["running"]},
              {"slug":"custom","name":"Custom","models":["local-a","running"],"featured_models":["local-a"],
               "pricing":{"running":{"free":true}}}
            ]}
            """)!.AsObject()).WithChosen(new HashSet<string>()).Usable();
        var provider = Assert.Single(catalog.Providers);
        Assert.Equal("Custom", provider.Name);
        Assert.True(provider.Keyless);
        Assert.True(provider.Chosen);
        Assert.Equal(new[] { "running", "local-a" }, provider.Models.Select(m => m.Id));
        Assert.Equal("Free", provider.Models[0].Price);
        Assert.Equal(new[] { "local-a" }, provider.Featured);
        Assert.All(provider.Models, model => Assert.Equal("custom", model.Provider));
    }

    [Fact]
    public void A_named_runtime_and_all_featured_models_survive_duplicate_rows()
    {
        var catalog = ModelCatalog.Parse(JsonNode.Parse("""
            {"providers":[
              {"slug":"custom:desk","name":"Local","auth_type":"local","models":["a"],"featured_models":["a"]},
              {"slug":"custom:desk","name":"Desk runtime","models":["b","a"],"featured_models":["b","missing"],"warning":"Check availability"},
              {"slug":"cloud","name":"Cloud","models":["c"]}
            ]}
            """)!.AsObject());
        Assert.Equal(new[] { "custom:desk", "cloud" }, catalog.Providers.Select(p => p.Slug));
        var provider = catalog.Providers[0];
        Assert.Equal("Desk runtime", provider.Name);
        Assert.Equal("local", provider.AuthType);
        Assert.Equal("Check availability", provider.Warning);
        Assert.Equal(new[] { "a", "b" }, provider.Featured);
        Assert.Equal(new[] { "a", "b" }, provider.Models.Select(m => m.Id));
    }

    [Fact]
    public void Merging_does_not_offer_cloud_rows_that_still_need_credentials()
    {
        var catalog = ModelCatalog.Parse(JsonNode.Parse("""
            {"providers":[
              {"slug":"cloud","name":"Cloud","auth_type":"api_key","key_env":"CLOUD_KEY","authenticated":false,"models":["locked"]},
              {"slug":"cloud","name":"Cloud","authenticated":true,"models":["available"]}
            ]}
            """)!.AsObject());
        Assert.Equal(new[] { "available" }, Assert.Single(catalog.Providers).Models.Select(m => m.Id));
    }
}
