using GitHub.Copilot;
using Microsoft.Extensions.AI;
using TroubleScout.Tools;
using TroubleScout.UI;

namespace TroubleScout.Services;

internal sealed class CopilotSessionLifecycleRequest
{
    internal required CopilotClient? CopilotClient { get; init; }
    internal required string? McpConfigPath { get; init; }
    internal required IReadOnlyList<string> SkillDirectories { get; init; }
    internal required IReadOnlyList<string> DisabledSkills { get; init; }
    internal required List<string> ConfiguredSkills { get; init; }
    internal required List<string> ConfiguredMcpServers { get; init; }
    internal required ISet<string> RuntimeMcpServers { get; init; }
    internal required ISet<string> RuntimeSkills { get; init; }
    internal required List<string> ConfigurationWarnings { get; init; }
    internal required Func<string?, SessionConfig> BuildSessionConfig { get; init; }
    internal required Action<CopilotSession> SetCopilotSession { get; init; }
    internal required Action<string?> SetSelectedReasoningEffort { get; init; }
    internal required Action<string> SetSelectedModel { get; init; }
    internal required Action ResetStateForNewAiSession { get; init; }
    internal required Func<bool> UseByokOpenAi { get; init; }
}

internal sealed record SessionConfigBuildRequest(
    string? Model,
    string? ReasoningEffort,
    SystemMessageConfig SystemMessage,
    bool UseByokOpenAi,
    IReadOnlyList<AIFunction> Tools,
    IReadOnlyDictionary<string, McpServerConfig> AvailableMcpServers,
    string? MonitoringMcpServer,
    string? TicketingMcpServer,
    Action<SessionEvent> OnEvent,
    Func<PermissionRequest, PermissionInvocation, Task<PermissionRequestResult>> OnPermissionRequest,
    ICollection<string> ConfigurationWarnings,
    ProviderConfig? Provider,
    IReadOnlyList<string> SkillDirectories,
    IReadOnlyList<string> DisabledSkills,
    IReadOnlyDictionary<string, string>? AgentModels = null);

internal static class CopilotSessionLifecycle
{
    internal static async Task<bool> CreateCopilotSessionAsync(
        string? model,
        Action<string>? updateStatus,
        CopilotSessionLifecycleRequest request)
    {
        if (request.CopilotClient == null)
        {
            ConsoleUI.ShowError("Not Connected", "Copilot client not initialized");
            return false;
        }

        updateStatus?.Invoke("Creating AI session...");

        ResetCapabilities(request);
        var skillDiscovery = SkillDiscoveryService.DiscoverConfiguredSkills(request.SkillDirectories, request.DisabledSkills);
        request.ConfiguredSkills.AddRange(skillDiscovery.SkillNames);
        request.ConfigurationWarnings.AddRange(skillDiscovery.Warnings);

        var mcpConfiguration = McpConfigurationService.LoadServers(request.McpConfigPath);
        var mcpServers = mcpConfiguration.Servers;
        request.ConfigurationWarnings.AddRange(mcpConfiguration.Warnings);
        foreach (var serverName in mcpServers.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            request.ConfiguredMcpServers.Add(serverName);
        }

        var config = request.BuildSessionConfig(model);
        request.SetCopilotSession(await request.CopilotClient.CreateSessionAsync(config));
        request.ResetStateForNewAiSession();
        request.SetSelectedReasoningEffort(ReasoningEffortHelper.Normalize(config.ReasoningEffort));

        if (request.ConfigurationWarnings.Count > 0)
        {
            ConsoleUI.ShowWarning("Capabilities loaded with warnings. Use /status or /capabilities to review details.");
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            request.SetSelectedModel(model);
            SettingsWorkflowService.SaveModelAndProviderState(model, request.UseByokOpenAi());
        }

        return true;
    }

    internal static SessionConfig BuildSessionConfig(SessionConfigBuildRequest request)
        => CopilotSessionConfigBuilder.Build(new CopilotSessionConfigOptions(
            Model: request.Model,
            ReasoningEffort: request.ReasoningEffort,
            SystemMessage: request.SystemMessage,
            UseByokOpenAi: request.UseByokOpenAi,
            Tools: request.Tools,
            AvailableMcpServers: request.AvailableMcpServers,
            MonitoringMcpServer: request.MonitoringMcpServer,
            TicketingMcpServer: request.TicketingMcpServer,
            OnEvent: request.OnEvent,
            OnPermissionRequest: request.OnPermissionRequest,
            ConfigurationWarnings: request.ConfigurationWarnings,
            Provider: request.Provider,
            SkillDirectories: request.SkillDirectories,
            DisabledSkills: request.DisabledSkills,
            AgentModels: request.AgentModels));

    private static void ResetCapabilities(CopilotSessionLifecycleRequest request)
    {
        request.ConfiguredMcpServers.Clear();
        request.ConfiguredSkills.Clear();
        request.RuntimeMcpServers.Clear();
        request.RuntimeSkills.Clear();
        request.ConfigurationWarnings.Clear();
    }
}
