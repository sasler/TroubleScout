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
    IReadOnlyList<string>? DisabledSkills = null,
    IReadOnlyDictionary<string, string>? AgentModels = null);

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
            // Child events are retained for report/audit attribution and are not rendered in the root response stream.
            IncludeSubAgentStreamingEvents = true,
            Tools = options.Tools.Cast<AIFunctionDeclaration>().ToList(),
            DefaultAgent = new DefaultAgentConfig
            {
                ExcludedTools =
                [
                    "web_search",
                    "shell",
                    "shell.exec",
                    "bash",
                    "powershell",
                    "run_powershell",
                    "run_delegated_powershell",
                    "get_system_info",
                    "get_event_logs",
                    "get_services",
                    "get_processes",
                    "get_disk_space",
                    "get_network_info",
                    "get_performance_counters"
                ]
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
        var delegatedMcpServers = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
        AddConfiguredRoleServer(options.MonitoringMcpServer, options.AvailableMcpServers, delegatedMcpServers);
        AddConfiguredRoleServer(options.TicketingMcpServer, options.AvailableMcpServers, delegatedMcpServers);

        return
        [
            new()
            {
                Name = "troubleshooting-subagent",
                DisplayName = "Troubleshooting Subagent",
                Description = "Collects targeted server, MCP, and research evidence, then returns only relevant findings.",
                Infer = true,
                Model = GetSubagentModel(options),
                Tools =
                [
                    "get_system_info",
                    "get_event_logs",
                    "get_services",
                    "get_processes",
                    "get_disk_space",
                    "get_network_info",
                    "get_performance_counters",
                    "run_delegated_powershell",
                    "web_search"
                ],
                McpServers = delegatedMcpServers.Count == 0 ? null : delegatedMcpServers,
                Prompt = PromptTemplateLoader.Render(PromptTemplateIds.AgentServerEvidenceCollector)
            }
        ];
    }

    private static void AddConfiguredRoleServer(
        string? configuredServerName,
        IReadOnlyDictionary<string, McpServerConfig> availableMcpServers,
        IDictionary<string, McpServerConfig> delegatedMcpServers)
    {
        if (!string.IsNullOrWhiteSpace(configuredServerName)
            && availableMcpServers.TryGetValue(configuredServerName, out var serverConfig))
        {
            delegatedMcpServers[configuredServerName] = serverConfig;
        }
    }

    private static string? GetSubagentModel(CopilotSessionConfigOptions options) =>
        options.AgentModels != null && options.AgentModels.TryGetValue(AppSettingsStore.SubagentModelRole, out var model) && !string.IsNullOrWhiteSpace(model)
            ? model.Trim()
            : null;

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
