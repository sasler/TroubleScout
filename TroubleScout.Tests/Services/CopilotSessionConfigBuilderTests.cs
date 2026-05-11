using FluentAssertions;
using GitHub.Copilot.SDK;
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
        config.IncludeSubAgentStreamingEvents.Should().BeFalse();
        config.OnEvent.Should().NotBeNull();
        config.OnPermissionRequest.Should().NotBeNull();
        config.DefaultAgent.Should().NotBeNull();
        config.DefaultAgent!.ExcludedTools.Should().Contain("web_search");
    }

    [Fact]
    public void Build_ForByok_ShouldDisableStreamingForOpenAiCompatibleGateways()
    {
        var config = CopilotSessionConfigBuilder.Build(CreateOptions(useByokOpenAi: true));

        config.Streaming.Should().BeFalse();
    }

    [Fact]
    public void Build_ShouldConfigureFoundationSubagents()
    {
        var config = CopilotSessionConfigBuilder.Build(CreateOptions());

        config.CustomAgents.Should().NotBeNull();
        config.CustomAgents.Should().Contain(agent => agent.Name == "server-evidence-collector"
            && agent.Infer == true
            && string.Equals(agent.DisplayName, "Server Evidence Collector", StringComparison.Ordinal));

        var issueResearcher = config.CustomAgents!.Single(agent => agent.Name == "issue-researcher");
        issueResearcher.Infer.Should().BeTrue();
        issueResearcher.Tools.Should().Equal("web_search");
    }

    [Fact]
    public void Build_WhenMonitoringAndTicketingRolesConfigured_ShouldAddRoleScopedAgents()
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

        var customAgents = config.CustomAgents!;
        var monitoringAgent = customAgents.Single(agent => agent.Name == "monitoring-investigator");
        monitoringAgent.McpServers.Should().NotBeNull();
        monitoringAgent.McpServers!.Keys.Should().Equal("zabbix");

        var ticketingAgent = customAgents.Single(agent => agent.Name == "ticket-investigator");
        ticketingAgent.McpServers.Should().NotBeNull();
        ticketingAgent.McpServers!.Keys.Should().Equal("redmine");
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
        config.CustomAgents.Should().NotContain(agent => agent.Name == "monitoring-investigator");
    }

    private static CopilotSessionConfigOptions CreateOptions(
        string? model = "gpt-4.1",
        bool useByokOpenAi = false,
        IReadOnlyDictionary<string, McpServerConfig>? mcpServers = null,
        string? monitoringMcpServer = null,
        string? ticketingMcpServer = null,
        ICollection<string>? configurationWarnings = null)
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
            ConfigurationWarnings: configurationWarnings);
    }
}
