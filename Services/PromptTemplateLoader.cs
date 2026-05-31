using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace TroubleScout.Services;

internal static class PromptTemplateIds
{
    internal const string SystemIdentity = "system.identity";
    internal const string SystemTargetContextDefault = "system.target-context.default";
    internal const string SystemTargetContextJea = "system.target-context.jea";
    internal const string SystemPowerShellDataCollectionFlow = "system.powershell-data-collection-flow";
    internal const string SystemInvestigationApproach = "system.investigation-approach";
    internal const string SystemResponseFormat = "system.response-format";
    internal const string SystemTroubleshootingApproach = "system.troubleshooting-approach";
    internal const string SystemSafety = "system.safety";
    internal const string SystemToolInstructions = "system.tool-instructions";
    internal const string SystemCustomInstructions = "system.custom-instructions";

    internal const string AgentServerEvidenceCollector = "agent.server-evidence-collector";
    internal const string AgentIssueResearcher = "agent.issue-researcher";
    internal const string AgentMonitoringInvestigator = "agent.monitoring-investigator";
    internal const string AgentTicketInvestigator = "agent.ticket-investigator";

    internal const string TurnResponseFormattingRequirement = "turn.response-formatting-requirement";
    internal const string TurnExecutionSafetyRequirement = "turn.execution-safety-requirement";
    internal const string TurnApprovedCommandFollowUp = "turn.approved-command-follow-up";

    internal static IReadOnlyCollection<string> All { get; } =
    [
        SystemIdentity,
        SystemTargetContextDefault,
        SystemTargetContextJea,
        SystemPowerShellDataCollectionFlow,
        SystemInvestigationApproach,
        SystemResponseFormat,
        SystemTroubleshootingApproach,
        SystemSafety,
        SystemToolInstructions,
        SystemCustomInstructions,
        AgentServerEvidenceCollector,
        AgentIssueResearcher,
        AgentMonitoringInvestigator,
        AgentTicketInvestigator,
        TurnResponseFormattingRequirement,
        TurnExecutionSafetyRequirement,
        TurnApprovedCommandFollowUp
    ];
}

internal static class PromptTemplateLoader
{
    private static readonly Regex PlaceholderRegex = new(@"\{\{(?<name>[A-Za-z0-9_.-]+)\}\}", RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, string> TemplateCache = new(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> ResourceNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [PromptTemplateIds.SystemIdentity] = "TroubleScout.Assets.prompts.system.identity.md",
        [PromptTemplateIds.SystemTargetContextDefault] = "TroubleScout.Assets.prompts.system.target-context-default.md",
        [PromptTemplateIds.SystemTargetContextJea] = "TroubleScout.Assets.prompts.system.target-context-jea.md",
        [PromptTemplateIds.SystemPowerShellDataCollectionFlow] = "TroubleScout.Assets.prompts.system.powershell-data-collection-flow.md",
        [PromptTemplateIds.SystemInvestigationApproach] = "TroubleScout.Assets.prompts.system.investigation-approach.md",
        [PromptTemplateIds.SystemResponseFormat] = "TroubleScout.Assets.prompts.system.response-format.md",
        [PromptTemplateIds.SystemTroubleshootingApproach] = "TroubleScout.Assets.prompts.system.troubleshooting-approach.md",
        [PromptTemplateIds.SystemSafety] = "TroubleScout.Assets.prompts.system.safety.md",
        [PromptTemplateIds.SystemToolInstructions] = "TroubleScout.Assets.prompts.system.tool-instructions.md",
        [PromptTemplateIds.SystemCustomInstructions] = "TroubleScout.Assets.prompts.system.custom-instructions.md",
        [PromptTemplateIds.AgentServerEvidenceCollector] = "TroubleScout.Assets.prompts.agents.server-evidence-collector.md",
        [PromptTemplateIds.AgentIssueResearcher] = "TroubleScout.Assets.prompts.agents.issue-researcher.md",
        [PromptTemplateIds.AgentMonitoringInvestigator] = "TroubleScout.Assets.prompts.agents.monitoring-investigator.md",
        [PromptTemplateIds.AgentTicketInvestigator] = "TroubleScout.Assets.prompts.agents.ticket-investigator.md",
        [PromptTemplateIds.TurnResponseFormattingRequirement] = "TroubleScout.Assets.prompts.turn.response-formatting-requirement.md",
        [PromptTemplateIds.TurnExecutionSafetyRequirement] = "TroubleScout.Assets.prompts.turn.execution-safety-requirement.md",
        [PromptTemplateIds.TurnApprovedCommandFollowUp] = "TroubleScout.Assets.prompts.turn.approved-command-follow-up.md",
    };

    internal static string Load(string templateId)
    {
        return TemplateCache.GetOrAdd(templateId, LoadUncached);
    }

    private static string LoadUncached(string templateId)
    {
        if (!ResourceNames.TryGetValue(templateId, out var resourceName))
        {
            throw new ArgumentException($"Unknown prompt template id: {templateId}", nameof(templateId));
        }

        var assembly = typeof(PromptTemplateLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Prompt template resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings();
    }

    internal static string Render(string templateId, IReadOnlyDictionary<string, string?>? values = null)
    {
        var template = Load(templateId);
        var result = PlaceholderRegex.Replace(template, match =>
        {
            var name = match.Groups["name"].Value;
            if (values == null || !values.TryGetValue(name, out var value))
            {
                throw new InvalidOperationException($"Prompt template '{templateId}' is missing value for placeholder '{name}'.");
            }

            return value ?? string.Empty;
        });

        return result.TrimEnd('\r', '\n');
    }
}
