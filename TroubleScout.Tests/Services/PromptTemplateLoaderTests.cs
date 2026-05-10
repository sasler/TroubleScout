using FluentAssertions;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests.Services;

public class PromptTemplateLoaderTests
{
    [Fact]
    public void Load_WithKnownTemplateId_ShouldReturnEmbeddedMarkdown()
    {
        var template = PromptTemplateLoader.Load(PromptTemplateIds.SystemIdentity);

        template.Should().Contain("You are TroubleScout");
        template.Should().Contain("Windows Server troubleshooting assistant");
    }

    [Fact]
    public void Render_WithPlaceholders_ShouldSubstituteValues()
    {
        var rendered = PromptTemplateLoader.Render(
            PromptTemplateIds.SystemCustomInstructions,
            new Dictionary<string, string?>
            {
                ["effectivePrimary"] = "srv01"
            });

        rendered.Should().Contain("what's wrong with srv01");
        rendered.Should().NotContain("{{effectivePrimary}}");
    }

    [Fact]
    public void Render_WithMissingPlaceholder_ShouldFailClearly()
    {
        var act = () => PromptTemplateLoader.Render(PromptTemplateIds.SystemCustomInstructions);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*effectivePrimary*");
    }
}
