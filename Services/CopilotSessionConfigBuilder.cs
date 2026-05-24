using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace TroubleScout.Services;

internal sealed record CopilotSessionConfigOptions(
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
    ICollection<string>? ConfigurationWarnings = null,
    ProviderConfig? Provider = null,
    IReadOnlyList<string>? SkillDirectories = null,
    IReadOnlyList<string>? DisabledSkills = null);

internal static class CopilotSessionConfigBuilder
{
    internal static SessionConfig Build(CopilotSessionConfigOptions options)
    {
        ValidateConfiguredMcpRole("Monitoring", options.MonitoringMcpServer, options.AvailableMcpServers, options.ConfigurationWarnings);
        ValidateConfiguredMcpRole("Ticketing", options.TicketingMcpServer, options.AvailableMcpServers, options.ConfigurationWarnings);

        var config = new SessionConfig
        {
            Model = options.Model,
            ReasoningEffort = options.ReasoningEffort,
            SystemMessage = options.SystemMessage,
            // Some OpenAI-compatible gateways reject streaming usage options emitted by the SDK.
            Streaming = !options.UseByokOpenAi,
            IncludeSubAgentStreamingEvents = false,
            Tools = options.Tools.Cast<AIFunctionDeclaration>().ToList(),
            DefaultAgent = new DefaultAgentConfig
            {
                ExcludedTools = ["web_search"]
            },
            CustomAgents = BuildCustomAgentConfigs(options),
            ClientName = "TroubleScout",
            OnEvent = options.OnEvent,
            OnPermissionRequest = options.OnPermissionRequest
        };

        if (options.Provider != null)
        {
            config.Provider = options.Provider;
        }

        if (options.AvailableMcpServers.Count > 0)
        {
            config.McpServers = options.AvailableMcpServers.ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.OrdinalIgnoreCase);
        }

        if (options.SkillDirectories is { Count: > 0 })
        {
            config.SkillDirectories = options.SkillDirectories.ToList();
        }

        if (options.DisabledSkills is { Count: > 0 })
        {
            config.DisabledSkills = options.DisabledSkills.ToList();
        }

        return config;
    }

    private static IList<CustomAgentConfig> BuildCustomAgentConfigs(CopilotSessionConfigOptions options)
    {
        var agents = new List<CustomAgentConfig>
        {
            new()
            {
                Name = "server-evidence-collector",
                DisplayName = "Server Evidence Collector",
                Description = "Collects targeted server and MCP evidence, then returns only the relevant findings.",
                Infer = true,
                Prompt = PromptTemplateLoader.Render(PromptTemplateIds.AgentServerEvidenceCollector)
            },
            new()
            {
                Name = "issue-researcher",
                DisplayName = "Issue Researcher",
                Description = "Researches detected errors, symptoms, and event IDs on the web and returns concise findings.",
                Infer = true,
                Tools = ["web_search"],
                Prompt = PromptTemplateLoader.Render(PromptTemplateIds.AgentIssueResearcher)
            }
        };

        var monitoringAgent = BuildRoleScopedAgent(
            "monitoring-investigator",
            "Monitoring Investigator",
            options.MonitoringMcpServer,
            options.AvailableMcpServers,
            PromptTemplateLoader.Render(PromptTemplateIds.AgentMonitoringInvestigator));
        if (monitoringAgent != null)
        {
            agents.Add(monitoringAgent);
        }

        var ticketingAgent = BuildRoleScopedAgent(
            "ticket-investigator",
            "Ticket Investigator",
            options.TicketingMcpServer,
            options.AvailableMcpServers,
            PromptTemplateLoader.Render(PromptTemplateIds.AgentTicketInvestigator));
        if (ticketingAgent != null)
        {
            agents.Add(ticketingAgent);
        }

        return agents;
    }

    private static CustomAgentConfig? BuildRoleScopedAgent(
        string name,
        string displayName,
        string? configuredServerName,
        IReadOnlyDictionary<string, McpServerConfig> availableMcpServers,
        string prompt)
    {
        if (string.IsNullOrWhiteSpace(configuredServerName)
            || !availableMcpServers.TryGetValue(configuredServerName, out var serverConfig))
        {
            return null;
        }

        return new CustomAgentConfig
        {
            Name = name,
            DisplayName = displayName,
            Description = $"Uses the mapped MCP role '{configuredServerName}' and returns concise findings.",
            Infer = true,
            McpServers = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase)
            {
                [configuredServerName] = serverConfig
            },
            Prompt = prompt
        };
    }

    private static void ValidateConfiguredMcpRole(
        string roleName,
        string? configuredServerName,
        IReadOnlyDictionary<string, McpServerConfig> mcpServers,
        ICollection<string>? configurationWarnings)
    {
        if (configurationWarnings == null || string.IsNullOrWhiteSpace(configuredServerName))
        {
            return;
        }

        if (!mcpServers.ContainsKey(configuredServerName))
        {
            configurationWarnings.Add($"{roleName} MCP '{configuredServerName}' is not available in the current MCP configuration.");
        }
    }
}
