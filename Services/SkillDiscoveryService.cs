using System.Text.Json;

namespace TroubleScout.Services;

internal sealed record SkillDiscoveryResult(
    IReadOnlyList<string> SkillNames,
    IReadOnlyList<string> Warnings);

internal static class SkillDiscoveryService
{
    internal static SkillDiscoveryResult DiscoverConfiguredSkills(
        IReadOnlyList<string> skillDirectories,
        IReadOnlyList<string> disabledSkills)
    {
        var warnings = new List<string>();
        var skills = DiscoverConfiguredSkills(skillDirectories, disabledSkills, warnings);
        return new SkillDiscoveryResult(skills, warnings);
    }

    internal static IReadOnlyList<string> DiscoverConfiguredSkills(
        IReadOnlyList<string> skillDirectories,
        IReadOnlyList<string> disabledSkills,
        ICollection<string> warnings)
    {
        var discoveredSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in skillDirectories)
        {
            if (!Directory.Exists(directory))
            {
                warnings.Add($"Skills directory not found: {directory}");
                continue;
            }

            foreach (var skillDir in Directory.GetDirectories(directory))
            {
                var skillMarkdown = Path.Combine(skillDir, "SKILL.md");
                var skillManifest = Path.Combine(skillDir, "skill.json");
                if (!File.Exists(skillMarkdown) && !File.Exists(skillManifest))
                {
                    continue;
                }

                var skillName = File.Exists(skillManifest)
                    ? ReadSkillNameFromManifest(skillManifest)
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(skillName))
                {
                    skillName = Path.GetFileName(skillDir);
                }

                if (!disabledSkills.Contains(skillName, StringComparer.OrdinalIgnoreCase))
                {
                    discoveredSkills.Add(skillName);
                }
            }
        }

        var sorted = discoveredSkills.ToList();
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        return sorted;
    }

    private static string ReadSkillNameFromManifest(string manifestPath)
    {
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                var name = nameElement.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
        }
        catch
        {
            // Ignore malformed manifest; caller falls back to folder name.
        }

        return string.Empty;
    }
}
