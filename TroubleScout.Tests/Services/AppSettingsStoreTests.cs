using FluentAssertions;
using TroubleScout.Services;
using TroubleScout.Tests.Fixtures;
using Xunit;

namespace TroubleScout.Tests.Services;

// Define collection to force sequential execution
[CollectionDefinition("AppSettings", DisableParallelization = true)]
public class AppSettingsCollection { }

[Collection("AppSettings")]
public class AppSettingsStoreTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string? _originalSettingsPath;

    public AppSettingsStoreTests()
    {
        _testDirectory = TestHelpers.CreateTempDirectory();
        
        // Save original settings path and set test settings path
        _originalSettingsPath = AppSettingsStore.SettingsPath;
        var testSettingsPath = Path.Combine(_testDirectory, "settings.json");
        AppSettingsStore.SettingsPath = testSettingsPath;
    }

    public void Dispose()
    {
        // Restore original settings path
        if (_originalSettingsPath != null)
        {
            AppSettingsStore.SettingsPath = _originalSettingsPath;
        }
        
        // Clean up test directory
        try
        {
            TestHelpers.CleanupTempDirectory(_testDirectory);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[TestCleanup] Failed to clean up temp directory '{_testDirectory}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"[TestCleanup] Access denied while cleaning up temp directory '{_testDirectory}': {ex.Message}");
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ShouldReturnDefault()
    {
        // Act
        var settings = AppSettingsStore.Load();

        // Assert
        settings.Should().NotBeNull();
        settings.LastModel.Should().BeNull();
        settings.SystemPromptOverrides.Should().NotBeNull();
        settings.SystemPromptOverrides.Should().ContainKeys("investigation_approach", "response_format", "troubleshooting_approach", "safety");
    }

    [Fact]
    public void Load_WhenSystemPromptOverridesMissing_ShouldPersistEditableDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppSettingsStore.SettingsPath)!);
        File.WriteAllText(AppSettingsStore.SettingsPath, """
        {
          "LastModel": "gpt-4.1"
        }
        """);

        var settings = AppSettingsStore.Load();
        var json = File.ReadAllText(AppSettingsStore.SettingsPath);

        settings.SystemPromptOverrides.Should().NotBeNull();
        settings.SystemPromptOverrides.Should().ContainKeys("investigation_approach", "response_format", "troubleshooting_approach", "safety");
        json.Should().Contain("\"SystemPromptOverrides\"");
        json.Should().Contain("\"investigation_approach\"");
    }

    [Fact]
    public void Load_WhenPreviousInvestigationDefaultStored_ShouldRefreshDelegationGuidance()
    {
        const string previousInvestigationDefault = """
            ## Investigation Approach
            - Begin with the most relevant diagnostic evidence for the reported symptom and expand only when findings justify it
            - Before each command or script, estimate whether the result is likely to exceed about 50 lines or otherwise produce broad raw data
            - Use direct diagnostic tools or `run_powershell` yourself for small, bounded reads; delegate high-volume evidence and supporting research to the troubleshooting subagent, using exact parent-authored PowerShell commands or staged script IDs, and consume its concise findings
            - Before delegating high-volume evidence, tell the user: "Handing this to the subagent to summarize the data."
            - Do not repeat the same successful direct read in a turn. Once the tool returns data, interpret it and move toward the answer.
            - Work proactively within a single investigation pass until you have a clear diagnosis, recommendation, or exhausted relevant diagnostics
            - Only ask clarifying questions when the initial problem description is genuinely ambiguous or when you need credentials/access that you do not have
            - Present complete findings, analysis, and recommendations in one response, then hand control back to TroubleScout instead of continuing indefinitely on your own
            - Return a complete answer for the current request and then stop; the user can send another query for additional work
            - If one relevant diagnostic approach yields no results, try the next most likely source before concluding
            - Constrain log/event time ranges and result counts; do not collect broad raw dumps by default
            """;

        Directory.CreateDirectory(Path.GetDirectoryName(AppSettingsStore.SettingsPath)!);
        File.WriteAllText(AppSettingsStore.SettingsPath, $$"""
        {
          "SystemPromptOverrides": {
            "investigation_approach": {{System.Text.Json.JsonSerializer.Serialize(previousInvestigationDefault)}}
          }
        }
        """);

        var settings = AppSettingsStore.Load();
        var json = File.ReadAllText(AppSettingsStore.SettingsPath);

        settings.SystemPromptOverrides.Should().NotBeNull();
        settings.SystemPromptOverrides!["investigation_approach"].Should().Contain("Follow the PowerShell Data Collection Flow");
        settings.SystemPromptOverrides["investigation_approach"].Should().Contain("Use MCP servers, skills, and web research when relevant");
        settings.SystemPromptOverrides["investigation_approach"].Should().Contain("After a successful small diagnostic pass");
        json.Should().Contain("Follow the PowerShell Data Collection Flow");
    }

    [Fact]
    public void Load_WhenImmediatePreviousInvestigationDefaultStored_ShouldRefreshDelegationGuidance()
    {
        const string previousInvestigationDefault = """
            ## Investigation Approach
            - Begin with the most relevant diagnostic evidence for the reported symptom and expand only when findings justify it
            - Before each command or script, estimate whether the result is likely to exceed about 50 lines or otherwise produce broad raw data
            - Use direct diagnostic tools or `run_powershell` yourself for small, bounded reads; delegate high-volume evidence and supporting research to the troubleshooting subagent, using exact parent-authored PowerShell commands or staged script IDs, and consume its concise findings
            - Never delegate just to summarize a routine server/PC health check, choose small diagnostics, or avoid writing the final answer yourself
            - After a successful small diagnostic pass, answer from the collected data instead of calling the troubleshooting subagent for summarization
            - Before delegating high-volume evidence, tell the user: "Handing this to the subagent to summarize the data."
            - Do not repeat the same successful direct read in a turn. Once the tool returns data, interpret it and move toward the answer.
            - Work proactively within a single investigation pass until you have a clear diagnosis, recommendation, or exhausted relevant diagnostics
            - Only ask clarifying questions when the initial problem description is genuinely ambiguous or when you need credentials/access that you do not have
            - Present complete findings, analysis, and recommendations in one response, then hand control back to TroubleScout instead of continuing indefinitely on your own
            - Return a complete answer for the current request and then stop; the user can send another query for additional work
            - If one relevant diagnostic approach yields no results, try the next most likely source before concluding
            - Constrain log/event time ranges and result counts; do not collect broad raw dumps by default
            """;

        Directory.CreateDirectory(Path.GetDirectoryName(AppSettingsStore.SettingsPath)!);
        File.WriteAllText(AppSettingsStore.SettingsPath, $$"""
        {
          "SystemPromptOverrides": {
            "investigation_approach": {{System.Text.Json.JsonSerializer.Serialize(previousInvestigationDefault)}}
          }
        }
        """);

        var settings = AppSettingsStore.Load();
        var json = File.ReadAllText(AppSettingsStore.SettingsPath);

        settings.SystemPromptOverrides.Should().NotBeNull();
        settings.SystemPromptOverrides!["investigation_approach"].Should().Contain("Follow the PowerShell Data Collection Flow");
        settings.SystemPromptOverrides["investigation_approach"].Should().NotContain("Before each command or script");
        settings.SystemPromptOverrides["investigation_approach"].Should().NotContain("Never delegate just to summarize");
        json.Should().Contain("Follow the PowerShell Data Collection Flow");
    }

    [Fact]
    public void Load_WhenSafeCommandsMissing_ShouldReturnDefaults()
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(AppSettingsStore.SettingsPath)!);
        File.WriteAllText(AppSettingsStore.SettingsPath, """
        {
          "LastModel": "gpt-4.1"
        }
        """);

        // Act
        var settings = AppSettingsStore.Load();
        var json = File.ReadAllText(AppSettingsStore.SettingsPath);

        // Assert
        settings.SafeCommands.Should().NotBeNull();
        settings.SafeCommands.Should().Contain([
            "Get-*", "Select-*", "Sort-*", "Group-*", "Where-*", "ForEach-*", "Measure-*", "Test-*",
            "ConvertTo-*", "ConvertFrom-*", "Compare-*", "Find-*", "Search-*", "Resolve-*",
            "Out-String", "Out-Null", "Format-Custom", "Format-Hex", "Format-List", "Format-Table", "Format-Wide"
        ]);
        json.Should().Contain("\"SafeCommands\"");
        json.Should().Contain("\"Get-*\"");
    }

    [Fact]
    public void Save_ThenLoad_ShouldPersistSettings()
    {
        // Arrange
        var settings = new AppSettings
        {
            LastModel = "gpt-4.1",
            ReasoningEffort = "high",
            UseByokOpenAi = true,
            ByokOpenAiBaseUrl = "https://proxy.example/v1",
            ByokOpenAiApiKey = "sk-test",
            MonitoringMcpServer = "zabbix",
            TicketingMcpServer = "redmine"
        };

        // Act
        AppSettingsStore.Save(settings);
        var loaded = AppSettingsStore.Load();

        // Assert
        loaded.Should().NotBeNull();
        loaded.LastModel.Should().Be("gpt-4.1");
        loaded.ReasoningEffort.Should().Be("high");
        loaded.UseByokOpenAi.Should().BeTrue();
        loaded.ByokOpenAiBaseUrl.Should().Be("https://proxy.example/v1");
        loaded.ByokOpenAiApiKey.Should().Be("sk-test");
        loaded.MonitoringMcpServer.Should().Be("zabbix");
        loaded.TicketingMcpServer.Should().Be("redmine");
    }

    [Fact]
    public void Save_ThenLoad_ShouldPersistPerProviderSubagentModelAndBillingMode()
    {
        var settings = new AppSettings
        {
            AgentModelProfiles = new Dictionary<string, Dictionary<string, string>>
            {
                ["github"] = new() { ["subagent"] = "gpt-5-mini" },
                ["byok"] = new() { ["subagent"] = "cheap-byok-model" }
            },
            GitHubBillingDisplayMode = "ai-credits"
        };

        AppSettingsStore.Save(settings);
        var loaded = AppSettingsStore.Load();

        AppSettingsStore.GetAgentModelsForProvider(loaded, useByokOpenAi: false)["subagent"].Should().Be("gpt-5-mini");
        AppSettingsStore.GetAgentModelsForProvider(loaded, useByokOpenAi: true)["subagent"].Should().Be("cheap-byok-model");
        loaded.GitHubBillingDisplayMode.Should().Be("ai-credits");
    }

    [Fact]
    public void SaveSubagentModelOverride_ShouldUpdateAndRemoveConfiguredSubagentModel()
    {
        AppSettingsStore.Save(new AppSettings
        {
            AgentModelProfiles = new Dictionary<string, Dictionary<string, string>>
            {
                ["github"] = new()
                {
                    ["subagent"] = "gpt-4.1"
                }
            }
        });

        SettingsWorkflowService.SaveSubagentModelOverride(false, "gpt-5-mini");
        var updated = AppSettingsStore.GetAgentModelsForProvider(AppSettingsStore.Load(), useByokOpenAi: false);

        updated["subagent"].Should().Be("gpt-5-mini");

        SettingsWorkflowService.SaveSubagentModelOverride(false, null);
        var removed = AppSettingsStore.GetAgentModelsForProvider(AppSettingsStore.Load(), useByokOpenAi: false);

        removed.Should().NotContainKey("subagent");
    }

    [Fact]
    public void ResolveGitHubBillingDisplayMode_ShouldDefaultToCreditsAfterCutover()
    {
        AppSettingsStore.ResolveGitHubBillingDisplayMode(null, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero))
            .Should().Be("ai-credits");
        AppSettingsStore.ResolveGitHubBillingDisplayMode(null, new DateTimeOffset(2026, 5, 31, 23, 59, 59, TimeSpan.Zero))
            .Should().Be("premium-requests-legacy");
    }

    [Fact]
    public void Load_ShouldNormalizeMonitoringAndTicketingMcpServerNames()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppSettingsStore.SettingsPath)!);
        File.WriteAllText(AppSettingsStore.SettingsPath, """
        {
          "MonitoringMcpServer": "  zabbix  ",
          "TicketingMcpServer": "   "
        }
        """);

        var settings = AppSettingsStore.Load();
        var json = File.ReadAllText(AppSettingsStore.SettingsPath);

        settings.MonitoringMcpServer.Should().Be("zabbix");
        settings.TicketingMcpServer.Should().BeNull();
        json.Should().Contain("\"MonitoringMcpServer\": \"zabbix\"");
        json.Should().Contain("\"TicketingMcpServer\": null");
    }

    [Fact]
    public void Save_ByokApiKey_ShouldPersistEncryptedFieldAndKeepCompatibility()
    {
        // Arrange
        var settings = new AppSettings
        {
            UseByokOpenAi = true,
            ByokOpenAiBaseUrl = "https://proxy.example/v1",
            ByokOpenAiApiKey = "sk-secret"
        };

        // Act
        AppSettingsStore.Save(settings);
        var json = File.ReadAllText(AppSettingsStore.SettingsPath);
        var loaded = AppSettingsStore.Load();

        // Assert
        json.Should().Contain("\"ByokOpenAiApiKeyEncrypted\"");
        loaded.ByokOpenAiApiKey.Should().Be("sk-secret");

        if (OperatingSystem.IsWindows())
        {
            json.Should().NotContain("\"ByokOpenAiApiKey\": \"sk-secret\"");
        }
    }

    [Fact]
    public void Save_WithSafeCommands_ShouldPersist()
    {
        // Arrange
        var settings = new AppSettings
        {
            SafeCommands = ["Get-*", "Out-String", "Format-Table"]
        };

        // Act
        AppSettingsStore.Save(settings);
        var loaded = AppSettingsStore.Load();

        // Assert
        loaded.SafeCommands.Should().Equal("Get-*", "Out-String", "Format-Table");
    }

    [Fact]
    public void Save_ShouldCreateDirectoryAndPersist()
    {
        // Arrange
        var settings = new AppSettings { LastModel = "claude-sonnet-4.5" };

        // Act
        AppSettingsStore.Save(settings);

        // Assert
        Directory.Exists(_testDirectory).Should().BeTrue();
        var loaded = AppSettingsStore.Load();
        loaded.LastModel.Should().Be("claude-sonnet-4.5");
    }

    [Fact]
    public void Load_WhenFileIsCorrupted_ShouldReturnDefault()
    {
        // Arrange
        var settingsPath = AppSettingsStore.SettingsPath;
        
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "{ invalid json }");

        // Act
        var settings = AppSettingsStore.Load();

        // Assert
        settings.Should().NotBeNull();
        settings.LastModel.Should().BeNull();
    }

    [Fact]
    public void Load_WithExistingSafeCommands_ShouldPreserveUserConfig()
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(AppSettingsStore.SettingsPath)!);
        File.WriteAllText(AppSettingsStore.SettingsPath, """
        {
          "SafeCommands": [
            "Get-*",
            "Out-String",
            "MyCustom-Command"
          ]
        }
        """);

        // Act
        var settings = AppSettingsStore.Load();

        // Assert
        settings.SafeCommands.Should().Equal("Get-*", "Out-String", "MyCustom-Command");
    }

    [Fact]
    public void Save_MultipleModels_ShouldOverwritePrevious()
    {
        // Arrange
        var settings1 = new AppSettings { LastModel = "gpt-4.1" };
        var settings2 = new AppSettings { LastModel = "claude-sonnet-4.5" };

        // Act
        AppSettingsStore.Save(settings1);
        AppSettingsStore.Save(settings2);
        var loaded = AppSettingsStore.Load();

        // Assert
        loaded.LastModel.Should().Be("claude-sonnet-4.5");
    }

    [Fact]
    public void Load_WhenFileIsEmpty_ShouldReturnDefault()
    {
        // Arrange
        var settingsPath = AppSettingsStore.SettingsPath;
        
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, string.Empty);

        // Act
        var settings = AppSettingsStore.Load();

        // Assert
        settings.Should().NotBeNull();
        settings.LastModel.Should().BeNull();
    }

    [Fact]
    public void Save_ShouldCreateIndentedJson()
    {
        // Arrange
        var settings = new AppSettings { LastModel = "gpt-4.1" };
        var settingsPath = AppSettingsStore.SettingsPath;

        // Act
        AppSettingsStore.Save(settings);
        var jsonContent = File.ReadAllText(settingsPath);

        // Assert
        jsonContent.Should().Contain("  "); // Should have indentation
        jsonContent.Should().Contain("\"LastModel\"");
    }

    [Fact]
    public void SettingsPath_WhenApplicationDataIsNull_ShouldUseFallback()
    {
        // Arrange - Save current path and reset to force recalculation
        var currentPath = AppSettingsStore.SettingsPath;
        AppSettingsStore.SettingsPath = null!;

        // Act
        var settingsPath = AppSettingsStore.SettingsPath;

        // Assert
        // The path should be valid - either from ApplicationData, LocalApplicationData, or CurrentDirectory
        settingsPath.Should().NotBeNullOrEmpty();
        settingsPath.Should().Contain("TroubleScout");
        settingsPath.Should().EndWith("settings.json");

        // Verify it's a valid path that can be used
        var directory = Path.GetDirectoryName(settingsPath);
        directory.Should().NotBeNullOrEmpty();
        
        // Cleanup - restore the test path
        AppSettingsStore.SettingsPath = currentPath;
    }

    [Fact]
    public void SettingsPath_AfterManualSet_ShouldReturnSetValue()
    {
        // Arrange
        var customPath = Path.Combine(_testDirectory, "custom", "settings.json");

        // Act
        AppSettingsStore.SettingsPath = customPath;
        var retrievedPath = AppSettingsStore.SettingsPath;

        // Assert
        retrievedPath.Should().Be(customPath);
        
        // Cleanup - restore original path to avoid test interference
        AppSettingsStore.SettingsPath = Path.Combine(_testDirectory, "settings.json");
    }
}
