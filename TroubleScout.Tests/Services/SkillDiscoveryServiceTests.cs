using FluentAssertions;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests.Services;

public class SkillDiscoveryServiceTests
{
    [Fact]
    public void DiscoverConfiguredSkills_ShouldUseManifestNameWhenPresent()
    {
        using var temp = TemporaryDirectory.Create();
        var skillDir = Path.Combine(temp.Path, "folder-name");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Skill");
        File.WriteAllText(Path.Combine(skillDir, "skill.json"), """
        {
          "name": "manifest-skill"
        }
        """);

        var result = SkillDiscoveryService.DiscoverConfiguredSkills([temp.Path], []);

        result.SkillNames.Should().Equal("manifest-skill");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverConfiguredSkills_ShouldFallbackToFolderName()
    {
        using var temp = TemporaryDirectory.Create();
        var skillDir = Path.Combine(temp.Path, "folder-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Skill");

        var result = SkillDiscoveryService.DiscoverConfiguredSkills([temp.Path], []);

        result.SkillNames.Should().Equal("folder-skill");
    }

    [Fact]
    public void DiscoverConfiguredSkills_ShouldApplyDisabledSkillsAndCaseInsensitiveDedupeAndSorting()
    {
        using var first = TemporaryDirectory.Create();
        using var second = TemporaryDirectory.Create();

        CreateSkill(first.Path, "zeta", "Zeta");
        CreateSkill(first.Path, "alpha", "Alpha");
        CreateSkill(second.Path, "alpha-copy", "alpha");
        CreateSkill(second.Path, "disabled", "Disabled");

        var result = SkillDiscoveryService.DiscoverConfiguredSkills(
            [first.Path, second.Path],
            ["disabled"]);

        result.SkillNames.Should().Equal("Alpha", "Zeta");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverConfiguredSkills_WhenDirectoryMissing_ShouldWarnAndContinue()
    {
        using var temp = TemporaryDirectory.Create();
        CreateSkill(temp.Path, "present", null);
        var missing = Path.Combine(Path.GetTempPath(), $"missing-skills-{Guid.NewGuid():N}");

        var result = SkillDiscoveryService.DiscoverConfiguredSkills([missing, temp.Path], []);

        result.SkillNames.Should().Equal("present");
        result.Warnings.Should().ContainSingle().Which.Should().Contain("Skills directory not found");
    }

    private static void CreateSkill(string root, string folderName, string? manifestName)
    {
        var skillDir = Path.Combine(root, folderName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Skill");

        if (!string.IsNullOrWhiteSpace(manifestName))
        {
            File.WriteAllText(Path.Combine(skillDir, "skill.json"), $$"""
            {
              "name": "{{manifestName}}"
            }
            """);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"troublescout-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
