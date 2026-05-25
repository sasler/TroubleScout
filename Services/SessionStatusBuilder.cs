using System.Globalization;
using TroubleScout.UI;

namespace TroubleScout.Services;

internal sealed record SessionStatusSnapshot(
    string? SelectedModel,
    string? ActiveProviderDisplayName,
    bool UseByokOpenAi,
    bool IsGitHubCopilotAuthenticated,
    string? ByokApiKey,
    string ByokBaseUrl,
    string? ReasoningDisplay,
    string SessionId,
    int ToolInvocationCount,
    SessionUsageSnapshot? LastUsage,
    SessionUsageTracker UsageTracker,
    double? SessionPremiumRequestCost,
    IReadOnlyList<string> ConfiguredMcpServers,
    IReadOnlyCollection<string> RuntimeMcpServers,
    string? ConfiguredMonitoringMcpServer,
    string? ConfiguredTicketingMcpServer,
    IReadOnlyList<string> ConfiguredSkills,
    IReadOnlyCollection<string> RuntimeSkills,
    IReadOnlyList<string> ConfigurationWarnings,
    IReadOnlyCollection<string> ApprovedMcpServers,
    IReadOnlyList<string> PersistedApprovedMcpServers,
    string ExecutionMode,
    string EffectiveTargetServer,
    string GitHubBillingDisplayMode,
    IReadOnlyDictionary<string, string> AgentModels);

internal static class SessionStatusBuilder
{
    internal static StatusBarInfo BuildStatusBarInfo(SessionStatusSnapshot snapshot)
    {
        var inputTokens = snapshot.LastUsage?.InputTokens ?? snapshot.LastUsage?.PromptTokens;
        var outputTokens = snapshot.LastUsage?.OutputTokens ?? snapshot.LastUsage?.CompletionTokens;
        var totalTokens = snapshot.LastUsage?.TotalTokens;

        return new StatusBarInfo(
            Model: snapshot.SelectedModel,
            Provider: snapshot.ActiveProviderDisplayName,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            TotalTokens: totalTokens,
            ToolInvocations: snapshot.ToolInvocationCount,
            SessionId: snapshot.SessionId)
        {
            ReasoningEffort = snapshot.ReasoningDisplay,
            SessionInputTokens = snapshot.UsageTracker.TotalInputTokens > 0 ? snapshot.UsageTracker.TotalInputTokens : null,
            SessionOutputTokens = snapshot.UsageTracker.TotalOutputTokens > 0 ? snapshot.UsageTracker.TotalOutputTokens : null,
            SessionCostEstimate = GetSessionCostEstimateDisplay(snapshot),
            SubagentCalls = snapshot.UsageTracker.SubagentCalls,
            SubagentTokens = snapshot.UsageTracker.SubagentTokens > 0 ? snapshot.UsageTracker.SubagentTokens : null
        };
    }

    internal static IReadOnlyList<(string Label, string Value)> GetStatusFields(
        SessionStatusSnapshot snapshot,
        bool includeMcpApprovals)
    {
        var fields = new List<(string Label, string Value)>();

        fields.Add((ConsoleUI.StatusSectionSeparator, "Provider"));
        fields.Add(("Provider", snapshot.ActiveProviderDisplayName ?? "default"));
        fields.Add(("Auth mode", snapshot.UseByokOpenAi ? "BYOK (OpenAI)" : "GitHub Copilot"));
        fields.Add(("Subagent model", snapshot.AgentModels.TryGetValue(AppSettingsStore.SubagentModelRole, out var subagentModel) ? subagentModel : "inherit"));
        fields.Add(("GitHub auth", snapshot.IsGitHubCopilotAuthenticated ? "Authenticated" : "Not authenticated"));
        fields.Add(("BYOK", !string.IsNullOrWhiteSpace(snapshot.ByokApiKey) && LooksLikeUrl(snapshot.ByokBaseUrl) ? "Configured" : "Not configured"));
        if (!string.IsNullOrWhiteSpace(snapshot.ReasoningDisplay))
        {
            fields.Add(("Reasoning", snapshot.ReasoningDisplay));
        }
        fields.Add(("Session ID", snapshot.SessionId));

        if (snapshot.ToolInvocationCount > 0
            || snapshot.UsageTracker.SubagentCalls > 0
            || !snapshot.UseByokOpenAi
            || (snapshot.LastUsage != null && snapshot.LastUsage.HasAny))
        {
            fields.Add((ConsoleUI.StatusSectionSeparator, "Usage"));
        }

        if (snapshot.ToolInvocationCount > 0)
        {
            fields.Add(("Tools used", snapshot.ToolInvocationCount.ToString(CultureInfo.InvariantCulture)));
        }

        if (snapshot.UsageTracker.SubagentCalls > 0)
        {
            fields.Add(("Subagents used", snapshot.UsageTracker.SubagentCalls.ToString(CultureInfo.InvariantCulture)));
            fields.Add(("Subagent tokens", snapshot.UsageTracker.SubagentTokens.ToString("N0", CultureInfo.InvariantCulture)));
        }

        if (!snapshot.UseByokOpenAi)
        {
            fields.Add(("Billing display", snapshot.GitHubBillingDisplayMode));
        }

        var costDisplay = GetSessionCostEstimateDisplay(snapshot);
        if (!string.IsNullOrWhiteSpace(costDisplay))
        {
            fields.Add(("Session cost", costDisplay));
        }

        if (snapshot.LastUsage != null && snapshot.LastUsage.HasAny)
        {
            AddUsageField(fields, "Prompt tokens", snapshot.LastUsage.PromptTokens);
            AddUsageField(fields, "Completion tokens", snapshot.LastUsage.CompletionTokens);
            AddUsageField(fields, "Total tokens", snapshot.LastUsage.TotalTokens);
            AddUsageField(fields, "Input tokens", snapshot.LastUsage.InputTokens);
            AddUsageField(fields, "Output tokens", snapshot.LastUsage.OutputTokens);
            AddContextUsageField(fields, snapshot.LastUsage.UsedContextTokens, snapshot.LastUsage.MaxContextTokens);
        }

        var hasMcpOrSkills =
            snapshot.ConfiguredMcpServers.Any(v => !string.IsNullOrWhiteSpace(v))
            || snapshot.RuntimeMcpServers.Count > 0
            || !string.IsNullOrWhiteSpace(snapshot.ConfiguredMonitoringMcpServer)
            || !string.IsNullOrWhiteSpace(snapshot.ConfiguredTicketingMcpServer)
            || snapshot.ConfiguredSkills.Any(v => !string.IsNullOrWhiteSpace(v))
            || snapshot.RuntimeSkills.Count > 0
            || snapshot.ConfigurationWarnings.Count > 0;

        if (hasMcpOrSkills)
        {
            fields.Add((ConsoleUI.StatusSectionSeparator, "Capabilities"));
        }

        AddCapabilityField(fields, "MCP configured", snapshot.ConfiguredMcpServers);
        AddCapabilityField(fields, "MCP used", snapshot.RuntimeMcpServers.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        AddCapabilityField(fields, "Monitoring MCP", snapshot.ConfiguredMonitoringMcpServer is null ? [] : [snapshot.ConfiguredMonitoringMcpServer]);
        AddCapabilityField(fields, "Ticketing MCP", snapshot.ConfiguredTicketingMcpServer is null ? [] : [snapshot.ConfiguredTicketingMcpServer]);
        if (includeMcpApprovals)
        {
            AddCapabilityField(fields, "MCP approved (session)", snapshot.ApprovedMcpServers.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            AddCapabilityField(fields, "MCP approved (persisted)", snapshot.PersistedApprovedMcpServers.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        }
        AddCapabilityField(fields, "Skills configured", snapshot.ConfiguredSkills);
        AddCapabilityField(fields, "Skills used", snapshot.RuntimeSkills.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        if (snapshot.ConfigurationWarnings.Count > 0)
        {
            fields.Add(("Capability warnings", string.Join(" | ", snapshot.ConfigurationWarnings)));
        }

        return fields;
    }

    internal static ReportSessionSummary BuildReportSessionSummary(SessionStatusSnapshot snapshot)
    {
        var modelDisplay = snapshot.SelectedModel ?? "Unknown";
        var modelsUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(modelDisplay))
        {
            modelsUsed.Add(modelDisplay);
        }

        return new ReportSessionSummary(
            CurrentModel: modelDisplay,
            CurrentProvider: snapshot.ActiveProviderDisplayName ?? "Unknown",
            ModelsUsed: modelsUsed.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList(),
            ConfiguredMcpServers: snapshot.ConfiguredMcpServers.ToList(),
            UsedMcpServers: snapshot.RuntimeMcpServers.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            MonitoringMcp: snapshot.ConfiguredMonitoringMcpServer,
            TicketingMcp: snapshot.ConfiguredTicketingMcpServer,
            ApprovedMcpServersForSession: snapshot.ApprovedMcpServers.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            PersistedApprovedMcpServers: snapshot.PersistedApprovedMcpServers.ToList(),
            ConfiguredSkills: snapshot.ConfiguredSkills.ToList(),
            UsedSkills: snapshot.RuntimeSkills.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            ExecutionMode: snapshot.ExecutionMode,
            TargetServer: snapshot.EffectiveTargetServer)
        {
            AgentModels = new Dictionary<string, string>(snapshot.AgentModels, StringComparer.OrdinalIgnoreCase),
            GitHubBillingDisplayMode = snapshot.UseByokOpenAi ? null : snapshot.GitHubBillingDisplayMode,
            SubagentCalls = snapshot.UsageTracker.SubagentCalls,
            SubagentTokens = snapshot.UsageTracker.SubagentTokens
        };
    }

    private static string? GetSessionCostEstimateDisplay(SessionStatusSnapshot snapshot)
    {
        if (snapshot.UseByokOpenAi)
        {
            return snapshot.UsageTracker.GetCostEstimateDisplay();
        }

        if (snapshot.GitHubBillingDisplayMode == AppSettingsStore.AiCreditsBillingMode)
        {
            return snapshot.UsageTracker.GetAiCreditsDisplay();
        }

        return snapshot.SessionPremiumRequestCost is > 0
            ? $"~{snapshot.SessionPremiumRequestCost.Value.ToString("0.#", CultureInfo.InvariantCulture)} premium reqs"
            : null;
    }

    private static void AddUsageField(List<(string Label, string Value)> fields, string label, int? value)
    {
        if (value.HasValue)
        {
            fields.Add((label, value.Value.ToString("N0", CultureInfo.InvariantCulture)));
        }
    }

    internal static void AddContextUsageField(List<(string Label, string Value)> fields, int? usedContext, int? maxContext)
    {
        if (!usedContext.HasValue || !maxContext.HasValue || maxContext.Value <= 0)
        {
            return;
        }

        var percentage = usedContext.Value * 100d / maxContext.Value;
        var value = $"{usedContext.Value.ToString("N0", CultureInfo.InvariantCulture)}/{maxContext.Value.ToString("N0", CultureInfo.InvariantCulture)} ({percentage.ToString("0.#", CultureInfo.InvariantCulture)}%)";
        fields.Add(("Context", value));
    }

    private static void AddCapabilityField(List<(string Label, string Value)> fields, string label, IEnumerable<string> values)
    {
        var distinct = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinct.Count == 0)
        {
            return;
        }

        const int maxItems = 2;
        var shown = distinct.Take(maxItems);
        var value = string.Join(", ", shown);
        if (distinct.Count > maxItems)
        {
            value += $" (+{distinct.Count - maxItems} more)";
        }

        fields.Add((label, value));
    }

    private static bool LooksLikeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                   || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }
}
