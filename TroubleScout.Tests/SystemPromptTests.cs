using System.Reflection;
using FluentAssertions;
using GitHub.Copilot.SDK;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests;

[Collection("AppSettings")]
public class SystemPromptTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string? _originalSettingsPath;

    public SystemPromptTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"troublescout-systemprompt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _originalSettingsPath = AppSettingsStore.SettingsPath;
        AppSettingsStore.SettingsPath = Path.Combine(_testDirectory, "settings.json");
    }

    public void Dispose()
    {
        if (_originalSettingsPath != null)
        {
            AppSettingsStore.SettingsPath = _originalSettingsPath;
        }

        try
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DefaultSystemPrompt_ContainsInvestigationApproach()
    {
        await using var session = new TroubleshootingSession("localhost");

        var config = InvokeCreateSystemMessage(session, "localhost");
        var content = GetCombinedPromptContent(config);

        config.Mode.Should().Be(SystemMessageMode.Customize);
        content.Should().Contain("exhaust ALL available diagnostic tools");
    }

    [Fact]
    public async Task DefaultSystemPrompt_ContainsAutonomousGuidance()
    {
        await using var session = new TroubleshootingSession("localhost");

        var content = GetCombinedPromptContent(InvokeCreateSystemMessage(session, "localhost"));

        content.Should().Contain("Do NOT pause to ask the user");
    }

    [Fact]
    public async Task SystemPromptOverride_ReplacesSection()
    {
        AppSettingsStore.Save(new AppSettings
        {
            SystemPromptOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["investigation_approach"] = """
                ## Investigation Approach
                - Use the custom investigation workflow.
                """
            }
        });

        await using var session = new TroubleshootingSession("localhost");

        var content = GetCombinedPromptContent(InvokeCreateSystemMessage(session, "localhost"));

        content.Should().Contain("Use the custom investigation workflow.");
        content.Should().NotContain("exhaust ALL available diagnostic tools");
    }

    [Fact]
    public async Task SystemPromptAppend_AddsToEnd()
    {
        const string appendText = """
            ## Custom Prompt Notes
            - Add this after the built-in guidance.
            """;

        AppSettingsStore.Save(new AppSettings
        {
            SystemPromptAppend = appendText
        });

        await using var session = new TroubleshootingSession("localhost");

        var content = GetCombinedPromptContent(InvokeCreateSystemMessage(session, "localhost"));

        content.TrimEnd().Should().EndWith(appendText);
    }

    [Fact]
    public async Task SystemPrompt_WhenMonitoringAndTicketingMcpConfigured_ShouldDescribeRolesAndSubagentUsage()
    {
        AppSettingsStore.Save(new AppSettings
        {
            MonitoringMcpServer = "zabbix",
            TicketingMcpServer = "redmine"
        });

        await using var session = new TroubleshootingSession("localhost");

        var content = GetCombinedPromptContent(InvokeCreateSystemMessage(session, "localhost"));

        content.Should().Contain("Monitoring MCP server: zabbix");
        content.Should().Contain("Ticketing MCP server: redmine");
        content.Should().Contain("Delegate monitoring lookups to the monitoring-focused sub-agent");
        content.Should().Contain("Delegate ticket history lookups to the ticket-focused sub-agent");
        content.Should().Contain("Delegate external issue and remediation research to the issue-researcher sub-agent");
    }

    [Fact]
    public async Task SystemPrompt_WhenOnlyTicketingMcpConfigured_ShouldNotMentionMonitoringSubagent()
    {
        AppSettingsStore.Save(new AppSettings
        {
            TicketingMcpServer = "redmine"
        });

        await using var session = new TroubleshootingSession("localhost");

        var content = GetCombinedPromptContent(InvokeCreateSystemMessage(session, "localhost"));

        content.Should().Contain("Ticketing MCP server: redmine");
        content.Should().Contain("Delegate ticket history lookups to the ticket-focused sub-agent");
        content.Should().NotContain("Monitoring MCP server:");
        content.Should().NotContain("Delegate monitoring lookups to the monitoring-focused sub-agent");
    }

    [Fact]
    public async Task SystemPromptOverride_ReplacesTroubleshootingApproachSection()
    {
        AppSettingsStore.Save(new AppSettings
        {
            SystemPromptOverrides = new Dictionary<string, string>
            {
                ["troubleshooting_approach"] = """
                ## Troubleshooting Approach
                1. Use the custom troubleshooting flow.
                """
            }
        });

        await using var session = new TroubleshootingSession("localhost");

        var content = GetCombinedPromptContent(InvokeCreateSystemMessage(session, "localhost"));

        content.Should().Contain("Use the custom troubleshooting flow.");
        content.Should().NotContain("1. **Understand the Problem**");
    }

    [Fact]
    public async Task SystemPromptOverride_NormalizesDuplicateKeys_LastValueWins()
    {
        AppSettingsStore.Save(new AppSettings
        {
            SystemPromptOverrides = new Dictionary<string, string>
            {
                [" safety "] = """
                ## Safety
                - Old safety text.
                """,
                ["Safety"] = """
                ## Safety
                - New safety text.
                """
            }
        });

        await using var session = new TroubleshootingSession("localhost");

        var content = GetCombinedPromptContent(InvokeCreateSystemMessage(session, "localhost"));

        content.Should().Contain("New safety text.");
        content.Should().NotContain("Old safety text.");
    }

    private static SystemMessageConfig InvokeCreateSystemMessage(TroubleshootingSession session, string targetServer)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("CreateSystemMessage", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull("CreateSystemMessage should exist on TroubleshootingSession");

        return (SystemMessageConfig)method!.Invoke(session, [targetServer, null])!;
    }

    private static string GetCombinedPromptContent(SystemMessageConfig config)
    {
        var sections = config.Sections?
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Value.Content)
            .Where(content => !string.IsNullOrWhiteSpace(content))
            .ToList()
            ?? [];

        if (!string.IsNullOrWhiteSpace(config.Content))
        {
            sections.Add(config.Content);
        }

        sections.Should().NotBeEmpty();
        return string.Join("\n\n", sections);
    }
}
