using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

public class StarterPromptTests
{
    [Fact]
    public void Starter_prompts_are_everyday_drafts_and_never_a_slash_command()
    {
        var prompts = StarterPrompts.All;
        Assert.True(prompts.Count >= 3);
        Assert.Equal(prompts.Count, prompts.Select(p => p.Label).Distinct(StringComparer.Ordinal).Count());
        Assert.All(prompts, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Label));
            Assert.False(string.IsNullOrWhiteSpace(p.Draft));
            Assert.False(p.Draft.TrimStart().StartsWith('/'));
            Assert.True(p.Draft.EndsWith(' ') || p.Draft.EndsWith('\n'));
        });
    }
}
