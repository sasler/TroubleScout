using FluentAssertions;
using GitHub.Copilot;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests.Services;

public class CopilotSessionConfigBuilderTests
{
    [Fact]
    public void Build_ShouldIncludeCoreSessionSettings()
    {
        var config = CopilotSessionConfigBuilder.Build(CreateOptions(model: "gpt-4.1"));

        config.Model.Should().Be("gpt-4.1");
        config.ClientName.Should().Be("TroubleScout");
        config.Streaming.Should().BeTrue();
        config.IncludeSubAgentStreamingEvents.Should().BeTrue();
        config.OnEvent.Should().NotBeNull();
        config.OnPermissionRequest.Should().NotBeNull();
        config.DefaultAgent.Should().NotBeNull();
        config.DefaultAgent!.ExcludedTools.Should().Contain("web_search");
        config.DefaultAgent.ExcludedTools.Should().NotContain("run_powershell");
        config.DefaultAgent.ExcludedTools.Should().Contain("run_delegated_powershell");
        config.DefaultAgent.ExcludedTools.Should().Contain("run_delegated_powershell_script");
        config.DefaultAgent.ExcludedTools.Should().Contain(["shell", "shell.exec", "bash", "powershell"]);
        config.DefaultAgent.ExcludedTools.Should().NotContain([
            "get_system_info", "get_event_logs", "get_services", "get_processes",
            "get_disk_space", "get_network_info", "get_performance_counters"
        ]);
    }

    [Fact]
    public void Build_ForByok_ShouldDisableStreamingForOpenAiCompatibleGateways()
    {
        var config = CopilotSessionConfigBuilder.Build(CreateOptions(useByokOpenAi: true));

        config.Streaming.Should().BeFalse();
    }

    [Fact]
    public void Build_ShouldConfigureSingleDelegatedTroubleshootingSubagent()
    {
        var config = CopilotSessionConfigBuilder.Build(CreateOptions());

        config.CustomAgents.Should().NotBeNull();
        config.CustomAgents.Should().ContainSingle(agent => agent.Name == "troubleshooting-subagent"
            && agent.Infer == true
            && string.Equals(agent.DisplayName, "Troubleshooting Subagent", StringComparison.Ordinal));
        var subagent = config.CustomAgents!.Single();
        subagent.Tools.Should().Contain(["run_delegated_powershell", "run_delegated_powershell_script", "web_search"]);
        subagent.Tools.Should().NotContain(["get_system_info", "get_event_logs", "get_services", "get_processes"]);
        subagent.Tools.Should().NotContain("run_powershell");
    }

    [Fact]
    public void Build_WithSubagentModelOverride_ShouldApplyModelToDelegatedAgent()
    {
        var config = CopilotSessionConfigBuilder.Build(CreateOptions(
            agentModels: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["subagent"] = "gpt-5-mini"
            }));

        config.CustomAgents!.Should().ContainSingle()
            .Which.Model.Should().Be("gpt-5-mini");
    }

    [Fact]
    public void Build_WhenMonitoringAndTicketingRolesConfigured_ShouldGrantMappedServersToSingleSubagent()
    {
        var mcpServers = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["zabbix"] = new McpHttpServerConfig { Url = "https://monitoring.example/mcp" },
            ["redmine"] = new McpHttpServerConfig { Url = "https://ticketing.example/mcp" }
        };

        var config = CopilotSessionConfigBuilder.Build(CreateOptions(
            mcpServers: mcpServers,
            monitoringMcpServer: "zabbix",
            ticketingMcpServer: "redmine"));

        var subagent = config.CustomAgents!.Should().ContainSingle().Which;
        subagent.McpServers.Should().NotBeNull();
        subagent.McpServers!.Keys.Should().BeEquivalentTo("zabbix", "redmine");
    }

    [Fact]
    public void Build_WhenConfiguredRoleIsMissing_ShouldAppendCapabilityWarningAndSkipAgent()
    {
        var warnings = new List<string>();

        var config = CopilotSessionConfigBuilder.Build(CreateOptions(
            monitoringMcpServer: "missing-zabbix",
            configurationWarnings: warnings));

        warnings.Should().ContainSingle()
            .Which.Should().Be("Monitoring MCP 'missing-zabbix' is not available in the current MCP configuration.");
        config.CustomAgents.Should().ContainSingle(agent => agent.Name == "troubleshooting-subagent");
    }

    private static CopilotSessionConfigOptions CreateOptions(
        string? model = "gpt-4.1",
        bool useByokOpenAi = false,
        IReadOnlyDictionary<string, McpServerConfig>? mcpServers = null,
        string? monitoringMcpServer = null,
        string? ticketingMcpServer = null,
        ICollection<string>? configurationWarnings = null,
        IReadOnlyDictionary<string, string>? agentModels = null)
    {
        return new CopilotSessionConfigOptions(
            Model: model,
            ReasoningEffort: null,
            SystemMessage: new SystemMessageConfig { Mode = SystemMessageMode.Customize },
            UseByokOpenAi: useByokOpenAi,
            Tools: [],
            AvailableMcpServers: mcpServers ?? new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase),
            MonitoringMcpServer: monitoringMcpServer,
            TicketingMcpServer: ticketingMcpServer,
            OnEvent: _ => { },
            OnPermissionRequest: (_, _) => Task.FromResult(new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved }),
            ConfigurationWarnings: configurationWarnings,
            AgentModels: agentModels);
    }
}
