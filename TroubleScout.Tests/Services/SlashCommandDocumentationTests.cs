using FluentAssertions;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests.Services;

public class SlashCommandDocumentationTests
{
    [Fact]
    public void SlashCommandReference_ShouldMatchRegistryGeneratedMarkdown()
    {
        var docsPath = Path.Combine(FindRepoRoot(), "docs", "slash-commands.md");

        var actual = File.ReadAllText(docsPath).ReplaceLineEndings("\n");
        var expected = SlashCommandDocumentation.GenerateMarkdown().ReplaceLineEndings("\n");

        actual.Should().Be(expected);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TroubleScout.csproj")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate TroubleScout repo root.");
    }
}
