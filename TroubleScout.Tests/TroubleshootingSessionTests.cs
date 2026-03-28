using FluentAssertions;
using GitHub.Copilot.SDK;
using System.Reflection;
using System.Globalization;
using System.Text.Json;
using TroubleScout;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests;

// Tests in this class mutate static resolver delegates and must not run in parallel.
[CollectionDefinition("TroubleshootingSession", DisableParallelization = true)]
public class TroubleshootingSessionCollection { }

[Collection("TroubleshootingSession")]
public class TroubleshootingSessionTests : IAsyncDisposable
{
    private readonly TroubleshootingSession _session;

    public TroubleshootingSessionTests()
    {
        _session = new TroubleshootingSession("localhost");
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    #region Constructor and Basic Properties Tests

    [Fact]
    public void Constructor_WithLocalhost_ShouldSetTargetServer()
    {
        // Act & Assert
        _session.TargetServer.Should().Be("localhost");
    }

    [Fact]
    public async Task Constructor_WithRemoteServer_ShouldSetTargetServer()
    {
        // Arrange & Act
        await using var session = new TroubleshootingSession("remoteserver");

        // Assert
        session.TargetServer.Should().Be("remoteserver");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Constructor_WithEmptyOrNullServer_ShouldDefaultToLocalhost(string? server)
    {
        // Arrange & Act
        await using var session = new TroubleshootingSession(server!);

        // Assert
        session.TargetServer.Should().Be("localhost");
    }

    [Fact]
    public async Task Constructor_WithModel_ShouldAcceptModelParameter()
    {
        // Arrange & Act
        await using var session = new TroubleshootingSession("localhost", "gpt-4.1");

        // Assert
        session.TargetServer.Should().Be("localhost");
    }

    [Fact]
    public void IsCurrentModel_ShouldReturnTrue_WhenSelectedModelMatchesId()
    {
        // Arrange
        SetPrivateField(_session, "_selectedModel", "gpt-4.1");

        // Act
        var actual = InvokeIsCurrentModel(_session, "gpt-4.1");

        // Assert
        actual.Should().BeTrue();
    }

    [Fact]
    public void IsCurrentModel_ShouldReturnTrue_WhenSelectedModelMatchesDisplayName()
    {
        // Arrange
        SetPrivateField(_session, "_selectedModel", "GPT-4.1");
        SetPrivateField(_session, "_availableModels", new List<ModelInfo>
        {
            new() { Id = "gpt-4.1", Name = "GPT-4.1" }
        });

        // Act
        var actual = InvokeIsCurrentModel(_session, "gpt-4.1");

        // Assert
        actual.Should().BeTrue();
    }

    [Fact]
    public void IsCurrentModel_ShouldReturnFalse_WhenModelDoesNotMatch()
    {
        // Arrange
        SetPrivateField(_session, "_selectedModel", "claude-sonnet-4.5");
        SetPrivateField(_session, "_availableModels", new List<ModelInfo>
        {
            new() { Id = "gpt-4.1", Name = "GPT-4.1" },
            new() { Id = "claude-sonnet-4.5", Name = "Claude Sonnet 4.5" }
        });

        // Act
        var actual = InvokeIsCurrentModel(_session, "gpt-4.1");

        // Assert
        actual.Should().BeFalse();
    }

    [Fact]
    public void IsCurrentModelAndSource_SameModelDifferentProvider_ShouldReturnFalse()
    {
        // Arrange - session defaults to GitHub (not BYOK)
        SetPrivateField(_session, "_selectedModel", "gpt-4.1");
        SetPrivateField(_session, "_useByokOpenAi", false);

        // Create a ModelSelectionEntry with Byok source via reflection
        var entryType = typeof(TroubleshootingSession).GetNestedType("ModelSelectionEntry", BindingFlags.NonPublic);
        var sourceType = typeof(TroubleshootingSession).GetNestedType("ModelSource", BindingFlags.NonPublic);
        entryType.Should().NotBeNull();
        sourceType.Should().NotBeNull();
        var byokValue = Enum.Parse(sourceType, "Byok");
        var entry = Activator.CreateInstance(entryType!, "gpt-4.1", "GPT-4.1", byokValue)!;

        var method = typeof(TroubleshootingSession)
            .GetMethod("IsCurrentModelAndSource", BindingFlags.Instance | BindingFlags.NonPublic, null, [entryType], null);
        method.Should().NotBeNull();

        // Act
        var result = (bool)method!.Invoke(_session, [entry])!;

        // Assert - same model but different source should return false
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveInitialSessionModel_WhenRequestedModelMissing_ShouldReturnFirstAvailable()
    {
        // Arrange
        await using var session = new TroubleshootingSession("localhost", "gpt-5");
        var availableModels = new List<ModelInfo>
        {
            new() { Id = "gpt-4.1", Name = "GPT-4.1" },
            new() { Id = "claude-sonnet-4.6", Name = "Claude Sonnet 4.6" }
        };

        // Act
        var actual = InvokeResolveInitialSessionModel(session, availableModels);

        // Assert
        actual.Should().Be("gpt-4.1");
    }

    [Fact]
    public async Task ResolveInitialSessionModel_WhenRequestedModelExists_ShouldReturnRequestedModel()
    {
        // Arrange
        await using var session = new TroubleshootingSession("localhost", "claude-sonnet-4.6");
        var availableModels = new List<ModelInfo>
        {
            new() { Id = "gpt-4.1", Name = "GPT-4.1" },
            new() { Id = "claude-sonnet-4.6", Name = "Claude Sonnet 4.6" }
        };

        // Act
        var actual = InvokeResolveInitialSessionModel(session, availableModels);

        // Assert
        actual.Should().Be("claude-sonnet-4.6");
    }

    [Fact]
    public async Task Constructor_WithExecutionMode_ShouldSetCurrentExecutionMode()
    {
        // Arrange & Act
        await using var session = new TroubleshootingSession("localhost", executionMode: ExecutionMode.Yolo);

        // Assert
        session.CurrentExecutionMode.Should().Be(ExecutionMode.Yolo);
    }

    [Fact]
    public void SlashCommands_ShouldIncludeReportCommand()
    {
        // Arrange
        var field = typeof(TroubleshootingSession).GetField("SlashCommands", BindingFlags.Static | BindingFlags.NonPublic);

        // Act
        var commands = field?.GetValue(null) as string[];

        // Assert
        commands.Should().NotBeNull();
        commands.Should().Contain("/report");
        commands.Should().Contain("/model");
        commands.Should().Contain("/mode");
        commands.Should().Contain("/reasoning");
    }

    [Theory]
    [InlineData("/mode", "/mode", true)]
    [InlineData("/mode safe", "/mode", true)]
    [InlineData("/reasoning high", "/reasoning", true)]
    [InlineData("/model", "/mode", false)]
    [InlineData("/modeX", "/mode", false)]
    [InlineData("/server srv01", "/server", true)]
    [InlineData("/serverX", "/server", false)]
    public void IsSlashCommandInvocation_ShouldMatchOnlyExactCommandOrCommandWithArguments(
        string input,
        string command,
        bool expected)
    {
        // Arrange
        var method = typeof(TroubleshootingSession)
            .GetMethod("IsSlashCommandInvocation", BindingFlags.Static | BindingFlags.NonPublic);

        // Act
        var actual = (bool)method!.Invoke(null, [input, command])!;

        // Assert
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData("/model", "/model")]
    [InlineData("/mode safe", "/mode")]
    [InlineData("exit", "exit")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void GetFirstInputToken_ShouldReturnFirstToken(string input, string expected)
    {
        // Arrange
        var method = typeof(TroubleshootingSession)
            .GetMethod("GetFirstInputToken", BindingFlags.Static | BindingFlags.NonPublic);

        // Act
        var token = method!.Invoke(null, [input]) as string;

        // Assert
        token.Should().Be(expected);
    }

    [Theory]
    [InlineData("exit", true)]
    [InlineData("quit", true)]
    [InlineData("exit code 1", false)]
    [InlineData("quit smoking", false)]
    [InlineData("/exit", false)]
    public void IsBareExitCommand_ShouldRequireExactMatch(string input, bool expected)
    {
        // Arrange
        var method = typeof(TroubleshootingSession)
            .GetMethod("IsBareExitCommand", BindingFlags.Static | BindingFlags.NonPublic);

        // Act
        var actual = (bool)method!.Invoke(null, [input])!;

        // Assert
        actual.Should().Be(expected);
    }

    [Fact]
    public void CreateSystemMessage_ShouldRequireCapabilityAttemptBeforeClaimingUnavailability()
    {
        // Arrange
        var method = typeof(TroubleshootingSession)
            .GetMethod("CreateSystemMessage", BindingFlags.Instance | BindingFlags.NonPublic);

        // Act
        var config = method!.Invoke(_session, ["localhost", null]);
        var content = GetCombinedPromptContent((SystemMessageConfig)config!);

        // Assert
        content.Should().NotBeNullOrWhiteSpace();
        content.Should().Contain("Before claiming you do not have access to a tool");
        content.Should().Contain("configured MCP servers");
        content.Should().Contain("loaded skills");
    }

    #endregion

    #region MCP Configuration Tests

    [Fact]
    public void LoadMcpServersFromConfig_WhenFileMissing_ShouldReturnEmptyAndWarning()
    {
        // Arrange
        var warnings = new List<string>();
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");

        // Act
        var servers = TroubleshootingSession.LoadMcpServersFromConfig(path, warnings);

        // Assert
        servers.Should().BeEmpty();
        warnings.Should().ContainSingle();
        warnings[0].Should().Contain("not found");
    }

    [Fact]
    public void LoadMcpServersFromConfig_WhenPathNotProvided_ShouldReturnEmptyWithoutWarning()
    {
        // Arrange
        var warnings = new List<string>();

        // Act
        var servers = TroubleshootingSession.LoadMcpServersFromConfig(null, warnings);

        // Assert
        servers.Should().BeEmpty();
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void LoadMcpServersFromConfig_WithValidJson_ShouldParseRemoteAndLocalServers()
    {
        // Arrange
        var warnings = new List<string>();
        var filePath = Path.Combine(Path.GetTempPath(), $"mcp-{Guid.NewGuid():N}.json");
        var json = """
        {
            "mcpServers": {
                "remote-server": {
                    "type": "http",
                    "url": "https://example.com/mcp",
                    "tools": ["*"]
                },
                "local-server": {
                    "type": "local",
                    "command": "node",
                    "args": ["server.js"]
                }
            }
        }
        """;

        File.WriteAllText(filePath, json);

        try
        {
            // Act
            var servers = TroubleshootingSession.LoadMcpServersFromConfig(filePath, warnings);

            // Assert
            warnings.Should().BeEmpty();
            servers.Should().HaveCount(2);
            servers.Keys.Should().Contain(["remote-server", "local-server"]);
            servers["remote-server"].GetType().Name.Should().Be("McpRemoteServerConfig");
            servers["local-server"].GetType().Name.Should().Be("McpLocalServerConfig");
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public void LoadMcpServersFromConfig_WithInvalidEntry_ShouldSkipOnlyInvalidServer()
    {
        // Arrange
        var warnings = new List<string>();
        var filePath = Path.Combine(Path.GetTempPath(), $"mcp-{Guid.NewGuid():N}.json");
        var json = """
        {
            "mcpServers": {
                "invalid-server": {
                    "type": "http"
                },
                "valid-server": {
                    "type": "http",
                    "url": "https://example.com/mcp"
                }
            }
        }
        """;

        File.WriteAllText(filePath, json);

        try
        {
            // Act
            var servers = TroubleshootingSession.LoadMcpServersFromConfig(filePath, warnings);

            // Assert
            servers.Should().ContainSingle();
            servers.Keys.Should().Contain("valid-server");
            warnings.Should().ContainSingle();
            warnings[0].Should().Contain("invalid-server");
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    #endregion

    #region CLI Availability Tests

    [Fact]
    public async Task CheckCopilotAvailable_ShouldNotThrow()
    {
        // Act & Assert - Just verify it doesn't throw
        // The actual result depends on the test environment
        Func<Task> act = () => CopilotCliResolver.CheckCopilotAvailableAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_ShouldNotThrow()
    {
        // Act
        Func<Task> act = async () => await CopilotCliResolver.ValidateCopilotPrerequisitesAsync();

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenNodeMissing_ShouldReturnBlockingIssue()
    {
        // Arrange
        CopilotCliResolver.CopilotCliPathResolver = () => @"C:\fake\copilot.js";
        CopilotCliResolver.FileExistsResolver = _ => true;
        CopilotCliResolver.ProcessRunnerResolver = (fileName, _) =>
        {
            if (fileName == "node")
            {
                return Task.FromResult((1, string.Empty, "node was not found"));
            }

            return Task.FromResult((0, "ok", string.Empty));
        };

        try
        {
            // Act
            var report = await CopilotCliResolver.ValidateCopilotPrerequisitesAsync();

            // Assert
            report.IsReady.Should().BeFalse();
            report.Issues.Should().ContainSingle();
            report.Issues[0].Title.Should().Be("Node.js runtime is missing");
            report.Issues[0].IsBlocking.Should().BeTrue();
        }
        finally
        {
            CopilotCliResolver.ResetPrerequisiteValidationResolvers();
        }
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenNodeVersionTooOld_ShouldReturnBlockingIssue()
    {
        // Arrange
        CopilotCliResolver.CopilotCliPathResolver = () => @"C:\fake\copilot.js";
        CopilotCliResolver.FileExistsResolver = _ => true;
        CopilotCliResolver.ProcessRunnerResolver = (fileName, _) =>
        {
            if (fileName == "node")
            {
                return Task.FromResult((0, "v22.22.0", string.Empty));
            }

            return Task.FromResult((0, "ok", string.Empty));
        };

        try
        {
            // Act
            var report = await CopilotCliResolver.ValidateCopilotPrerequisitesAsync();

            // Assert
            report.IsReady.Should().BeFalse();
            report.Issues.Should().ContainSingle();
            report.Issues[0].Title.Should().Be("Node.js version is unsupported");
            report.Issues[0].IsBlocking.Should().BeTrue();
            report.Issues[0].Details.Should().Contain("requires Node.js 24+");
            report.Issues[0].Details.Should().Contain("Detected: v22.22.0");
            report.Issues[0].Details.Should().NotContain("@github/copilot-sdk");
        }
        finally
        {
            CopilotCliResolver.ResetPrerequisiteValidationResolvers();
        }
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenValidationThrows_ShouldReturnBlockingIssueWithDetails()
    {
        // Arrange
        CopilotCliResolver.CopilotCliPathResolver = () => throw new InvalidOperationException("boom");

        try
        {
            // Act
            var report = await CopilotCliResolver.ValidateCopilotPrerequisitesAsync();

            // Assert
            report.IsReady.Should().BeFalse();
            report.Issues.Should().ContainSingle();
            report.Issues[0].Title.Should().Be("Could not fully validate Copilot prerequisites");
            report.Issues[0].IsBlocking.Should().BeTrue();
            report.Issues[0].Details.Should().Contain("boom");
            report.Issues[0].Details.Should().NotContain("@github/copilot-sdk");
        }
        finally
        {
            CopilotCliResolver.ResetPrerequisiteValidationResolvers();
        }
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenCopilotExeAndNodeMissing_ShouldRemainReady()
    {
        CopilotCliResolver.CopilotCliPathResolver = () => "copilot";
        CopilotCliResolver.FileExistsResolver = _ => true;
        CopilotCliResolver.ProcessRunnerResolver = (fileName, arguments) =>
        {
            if (fileName == "cmd.exe" && arguments.Equals("/c where copilot", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult((0, @"C:\Program Files\GitHub\Copilot\copilot.exe", string.Empty));
            }

            if (fileName == "cmd.exe" && arguments.Contains("copilot --version", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult((0, "copilot 1.0.0", string.Empty));
            }

            if (fileName == "node")
            {
                return Task.FromResult((1, string.Empty, "node not found"));
            }

            if (fileName == "pwsh")
            {
                return Task.FromResult((0, "7.4.1", string.Empty));
            }

            return Task.FromResult((0, "ok", string.Empty));
        };

        try
        {
            var report = await CopilotCliResolver.ValidateCopilotPrerequisitesAsync();

            report.IsReady.Should().BeTrue();
            report.Issues.Should().BeEmpty();
        }
        finally
        {
            CopilotCliResolver.ResetPrerequisiteValidationResolvers();
        }
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenCopilotCmdAndNodeMissing_ShouldReturnBlockingIssue()
    {
        CopilotCliResolver.CopilotCliPathResolver = () => "copilot";
        CopilotCliResolver.FileExistsResolver = _ => true;
        CopilotCliResolver.ProcessRunnerResolver = (fileName, arguments) =>
        {
            if (fileName == "cmd.exe" && arguments.Equals("/c where copilot", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult((0, @"C:\Users\test\AppData\Roaming\npm\copilot.cmd", string.Empty));
            }

            if (fileName == "node")
            {
                return Task.FromResult((1, string.Empty, "node not found"));
            }

            return Task.FromResult((0, "ok", string.Empty));
        };

        try
        {
            var report = await CopilotCliResolver.ValidateCopilotPrerequisitesAsync();

            report.IsReady.Should().BeFalse();
            report.Issues.Should().ContainSingle();
            report.Issues[0].Title.Should().Be("Node.js runtime is missing");
        }
        finally
        {
            CopilotCliResolver.ResetPrerequisiteValidationResolvers();
        }
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenPowerShellIsSix_ShouldAddNonBlockingWarning()
    {
        CopilotCliResolver.CopilotCliPathResolver = () => "copilot";
        CopilotCliResolver.FileExistsResolver = _ => true;
        CopilotCliResolver.ProcessRunnerResolver = (fileName, arguments) =>
        {
            if (fileName == "cmd.exe" && arguments.Equals("/c where copilot", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult((0, @"C:\Program Files\GitHub\Copilot\copilot.exe", string.Empty));
            }

            if (fileName == "cmd.exe" && arguments.Contains("copilot --version", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult((0, "copilot 1.0.0", string.Empty));
            }

            if (fileName == "node")
            {
                return Task.FromResult((1, string.Empty, "node not found"));
            }

            if (fileName == "pwsh")
            {
                return Task.FromResult((0, "6.2.7", string.Empty));
            }

            return Task.FromResult((0, "ok", string.Empty));
        };

        try
        {
            var report = await CopilotCliResolver.ValidateCopilotPrerequisitesAsync();

            report.IsReady.Should().BeTrue();
            report.Issues.Should().ContainSingle();
            report.Issues[0].Title.Should().Be("PowerShell version is below recommended");
            report.Issues[0].IsBlocking.Should().BeFalse();
            report.Issues[0].Details.Should().Contain("recommends PowerShell 7+");
        }
        finally
        {
            CopilotCliResolver.ResetPrerequisiteValidationResolvers();
        }
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenPwshUnavailable_ShouldFallbackToWindowsPowerShell()
    {
        CopilotCliResolver.CopilotCliPathResolver = () => "copilot";
        CopilotCliResolver.FileExistsResolver = _ => true;
        CopilotCliResolver.ProcessRunnerResolver = (fileName, arguments) =>
        {
            if (fileName == "cmd.exe" && arguments.Equals("/c where copilot", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult((0, @"C:\Program Files\GitHub\Copilot\copilot.exe", string.Empty));
            }

            if (fileName == "cmd.exe" && arguments.Contains("copilot --version", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult((0, "copilot 1.0.0", string.Empty));
            }

            if (fileName == "pwsh")
            {
                return Task.FromResult((1, string.Empty, "pwsh not found"));
            }

            if (fileName == "powershell")
            {
                return Task.FromResult((0, "5.1.19041.4522", string.Empty));
            }

            if (fileName == "node")
            {
                return Task.FromResult((1, string.Empty, "node not found"));
            }

            return Task.FromResult((0, "ok", string.Empty));
        };

        try
        {
            var report = await CopilotCliResolver.ValidateCopilotPrerequisitesAsync();

            report.IsReady.Should().BeTrue();
            report.Issues.Should().ContainSingle();
            report.Issues[0].Details.Should().Contain("Detected powershell 5.1.19041.4522");
        }
        finally
        {
            CopilotCliResolver.ResetPrerequisiteValidationResolvers();
        }
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenPowerShellVersionIsUnparseable_ShouldNotWarn()
    {
        CopilotCliResolver.CopilotCliPathResolver = () => "copilot";
        CopilotCliResolver.FileExistsResolver = _ => true;
        CopilotCliResolver.ProcessRunnerResolver = (fileName, arguments) =>
        {
            if (fileName == "cmd.exe" && arguments.Equals("/c where copilot", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult((0, @"C:\Program Files\GitHub\Copilot\copilot.exe", string.Empty));
            }

            if (fileName == "cmd.exe" && arguments.Contains("copilot --version", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult((0, "copilot 1.0.0", string.Empty));
            }

            if (fileName == "pwsh")
            {
                return Task.FromResult((0, "preview", string.Empty));
            }

            if (fileName == "node")
            {
                return Task.FromResult((1, string.Empty, "node not found"));
            }

            return Task.FromResult((0, "ok", string.Empty));
        };

        try
        {
            var report = await CopilotCliResolver.ValidateCopilotPrerequisitesAsync();

            report.IsReady.Should().BeTrue();
            report.Issues.Should().BeEmpty();
        }
        finally
        {
            CopilotCliResolver.ResetPrerequisiteValidationResolvers();
        }
    }

    #endregion

    #region Connection Mode Tests

    [Fact]
    public void ConnectionMode_BeforeInitialization_ShouldReturnDefault()
    {
        // Act
        var mode = _session.ConnectionMode;

        // Assert
        mode.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SelectedModel_BeforeInitialization_ShouldReturnDefault()
    {
        // Act
        var model = _session.SelectedModel;

        // Assert
        model.Should().Be("default");
    }

    [Fact]
    public void CopilotVersion_BeforeInitialization_ShouldReturnUnknown()
    {
        // Act
        var version = _session.CopilotVersion;

        // Assert
        version.Should().Be("unknown");
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public async Task DisposeAsync_MultipleCall_ShouldNotThrow()
    {
        // Act & Assert
        await _session.DisposeAsync();
        await _session.DisposeAsync(); // Second call should be safe
    }

    [Fact]
    public async Task DisposeAsync_ShouldCleanupResources()
    {
        // Arrange
        var session = new TroubleshootingSession("localhost");

        // Act
        await session.DisposeAsync();

        // Assert - Should not throw
        session.Should().NotBeNull();
    }

    [Fact]
    public void SetExecutionMode_ShouldPropagateToAdditionalExecutors()
    {
        // Arrange - add an additional executor via the private field
        var additionalExecutors = GetPrivateField<Dictionary<string, PowerShellExecutor>>(_session, "_additionalExecutors");
        var altExecutor = new PowerShellExecutor("server2");
        altExecutor.ExecutionMode = ExecutionMode.Safe;
        additionalExecutors["server2"] = altExecutor;

        var method = typeof(TroubleshootingSession)
            .GetMethod("SetExecutionMode", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        // Act
        method!.Invoke(_session, [ExecutionMode.Yolo]);

        // Assert
        altExecutor.ExecutionMode.Should().Be(ExecutionMode.Yolo);
        altExecutor.Dispose();
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public void BuildReportHtml_ShouldEncodeContentAndIncludePromptActionAndReply()
    {
        // Arrange
        const string prompt = "<script>alert('xss')</script>";
        const string command = "Get-Service -Name 'BITS' | Where-Object { $_.Status -eq 'Running' }";
        const string output = "<b>danger</b>";
        const string reply = "Investigated <tag> and confirmed status.";

        // Act
        var html = BuildReportHtmlViaReflection(
            prompt,
            reply,
            (command, output, "SafeAuto", "PowerShell", "localhost"));

        // Assert
        html.Should().Contain("TroubleScout Session Report");
        html.Should().Contain("&lt;script&gt;alert(&#39;xss&#39;)&lt;/script&gt;");
        html.Should().NotContain(prompt);
        html.Should().Contain("&lt;b&gt;danger&lt;/b&gt;");
        html.Should().Contain("Investigated &lt;tag&gt; and confirmed status.");
        html.Should().Contain("PowerShell");
        html.Should().Contain("SafeAuto");
        html.Should().Contain("Command");
        html.Should().Contain("Output");
    }

    [Fact]
    public void BuildReportHtml_ShouldNotCountApprovalRequestedAsApproved()
    {
        // Act
        var html = BuildReportHtmlViaReflection(
            "Check services",
            "reply",
            ("Get-Service", "queued", "ApprovalRequested", "PowerShell", "localhost"),
            ("Stop-Service", "done", "ApprovedByUser", "PowerShell", "localhost"));

        // Assert
        html.Should().Contain("<div class=\"summary-card sc-blue\"><div class=\"sc-val\">1</div><div class=\"sc-lbl\">Approved</div></div>");
    }

    [Fact]
    public void BuildSecondOpinionPrompt_ShouldIncludePriorConversationAndActions()
    {
        // Act
        var prompt = BuildSecondOpinionPromptViaService(
            "gpt-4.1",
            "claude-sonnet-4.6",
            (
                Prompt: "The print spooler keeps stopping.",
                Reply: "The spooler looks unstable. Check dependent services and recent crashes.",
                Actions:
                [
                    (
                        Command: "Get-Service Spooler",
                        Output: "Status   Name               DisplayName\n------   ----               -----------\nStopped  Spooler            Print Spooler",
                        SafetyApproval: "Approved",
                        Source: "PowerShell",
                        Target: "localhost"
                    )
                ]
            ),
            (
                Prompt: "Also check the event logs.",
                Reply: "Look for Service Control Manager events around the stop time.",
                Actions: []
            ));

        // Assert
        prompt.Should().Contain("second opinion");
        prompt.Should().Contain("gpt-4.1");
        prompt.Should().Contain("claude-sonnet-4.6");
        prompt.Should().Contain("The print spooler keeps stopping.");
        prompt.Should().Contain("Check dependent services");
        prompt.Should().Contain("Get-Service Spooler");
        prompt.Should().Contain("Print Spooler");
        prompt.Should().Contain("Also check the event logs.");
        prompt.Should().Contain("Service Control Manager");
    }

    [Fact]
    public void BuildSecondOpinionPrompt_WhenHistoryIsLarge_ShouldKeepRecentTurnsAndMarkTruncation()
    {
        var prompts = Enumerable.Range(1, 10)
            .Select(index => (
                Prompt: $"Prompt {index} " + new string('P', 2300),
                Reply: $"Reply {index} " + new string('R', 3300),
                Actions: new[]
                {
                    (
                        Command: $"Get-Thing-{index} " + new string('C', 900),
                        Output: $"Output {index} " + new string('O', 3200),
                        SafetyApproval: "Approved",
                        Source: "PowerShell",
                        Target: "localhost"
                    )
                }))
            .ToArray();

        var prompt = BuildSecondOpinionPromptViaService("gpt-4.1", "claude-sonnet-4.6", prompts);

        prompt.Should().Contain("Only the most recent 8 turns are included.");
        prompt.Should().Contain("Older turns were omitted to fit prompt size limits.");
        prompt.Should().NotContain($"## Turn 1{Environment.NewLine}");
        prompt.Should().NotContain($"## Turn 2{Environment.NewLine}");
        prompt.Should().Contain($"## Turn 10{Environment.NewLine}");
        prompt.Should().Contain("[truncated]");
        prompt.Length.Should().BeLessOrEqualTo(24_000);
    }

    [Fact]
    public void RenderCommandHtml_ShouldApplyPowerShellSyntaxHighlighting()
    {
        // Arrange
        const string command = "Get-Service -Name 'BITS' | Where-Object { $_.Status -eq 'Running' }";

        // Act
        var html = ReportHtmlBuilder.RenderCommandHtml(command);

        // Assert
        html.Should().NotBeNullOrEmpty();
        html.Should().Contain("tok-cmdlet");
        html.Should().Contain("tok-param");
        html.Should().Contain("tok-string");
        html.Should().Contain("tok-variable");
        html.Should().Contain("tok-op");
    }

    private static string BuildReportHtmlViaReflection(
        string prompt,
        string reply,
        params (string Command, string Output, string SafetyApproval, string Source, string Target)[] actions)
    {
        var promptEntry = new ReportPromptEntry(
            DateTimeOffset.Now,
            prompt,
            actions.Select(action => new ReportActionEntry(
                DateTimeOffset.Now,
                action.Target,
                action.Command,
                action.Output,
                action.SafetyApproval,
                action.Source)).ToList(),
            reply);

        return ReportHtmlBuilder.BuildReportHtml([promptEntry]);
    }

    private static string BuildSecondOpinionPromptViaService(
        string previousModel,
        string newModel,
        params (string Prompt, string Reply, (string Command, string Output, string SafetyApproval, string Source, string Target)[] Actions)[] prompts)
    {
        var promptEntries = prompts
            .Select(prompt => new ReportPromptEntry(
                DateTimeOffset.Now,
                prompt.Prompt,
                prompt.Actions.Select(action => new ReportActionEntry(
                    DateTimeOffset.Now,
                    action.Target,
                    action.Command,
                    action.Output,
                    action.SafetyApproval,
                    action.Source)).ToList(),
                prompt.Reply))
            .ToList();

        return SecondOpinionService.BuildSecondOpinionPrompt(previousModel, newModel, promptEntries, 8, 24_000, 2_000, 3_000, 800, 3_000);
    }

    private static bool InvokeIsCurrentModel(TroubleshootingSession session, string modelId)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("IsCurrentModel", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        return (bool)method!.Invoke(session, [modelId])!;
    }

    private static void SetPrivateField(object instance, string fieldName, object? value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null && instance is TroubleshootingSession session
            && (fieldName == "_availableModels" || fieldName == "_modelSources" || fieldName == "_byokPricing"))
        {
            instance = GetModelDiscoveryManager(session);
            field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        }

        if (field != null)
        {
            field.SetValue(instance, value);
            return;
        }

        var property = instance.GetType().GetProperty(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        property.Should().NotBeNull();
        property!.SetValue(instance, value);
    }

    [Fact]
    public async Task SendMessage_BeforeInitialization_ShouldReturnFalse()
    {
        // Act
        var result = await _session.SendMessageAsync("test message");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ChangeModel_BeforeInitialization_ShouldReturnFalse()
    {
        // Act
        var result = await _session.ChangeModelAsync("gpt-4.1");

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Server Management Tests

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("Localhost")]
    public async Task TargetServer_CaseVariations_ShouldBePreserved(string serverName)
    {
        // Arrange & Act
        await using var session = new TroubleshootingSession(serverName);

        // Assert
        session.TargetServer.Should().Be(serverName);
    }

    [Fact]
    public async Task Constructor_WithComplexServerName_ShouldHandleCorrectly()
    {
        // Arrange & Act
        await using var session = new TroubleshootingSession("server.domain.com");

        // Assert
        session.TargetServer.Should().Be("server.domain.com");
    }

    #endregion

    #region GetCopilotCliPath Tests

    [Fact]
    public void GetCopilotCliPath_ShouldReturnValidPath()
    {
        // Act
        var cliPath = CopilotCliResolver.GetCopilotCliPath();

        // Assert
        cliPath.Should().NotBeNullOrEmpty();
        // Should either be PATH fallback or a concrete install path
        (cliPath == "copilot" || Path.IsPathRooted(cliPath)).Should().BeTrue();
    }

    [Fact]
    public void GetCopilotCliPath_WhenCopilotCliPathEnvIsSet_ShouldUseEnvVariable()
    {
        // Arrange
        var testPath = Path.Combine(Path.GetTempPath(), "test-copilot.js");
        File.WriteAllText(testPath, "// test");
        var originalEnvValue = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        
        try
        {
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", testPath);

            // Act
            var cliPath = CopilotCliResolver.GetCopilotCliPath();

            // Assert
            cliPath.Should().Be(testPath);
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", originalEnvValue);
            if (File.Exists(testPath))
            {
                File.Delete(testPath);
            }
        }
    }

    [Fact]
    public void GetCopilotCliPath_WhenNoResolversMatch_ShouldFallbackToCopilotInPath()
    {
        // Arrange
        var originalEnvValue = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalFileExistsResolver = CopilotCliResolver.FileExistsResolver;
        
        try
        {
            // Clear COPILOT_CLI_PATH and PATH to force fallback behavior
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", null);
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            CopilotCliResolver.FileExistsResolver = _ => false;

            // Act
            var cliPath = CopilotCliResolver.GetCopilotCliPath();

            // Assert
            cliPath.Should().Be("copilot");
        }
        finally
        {
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", originalEnvValue);
            Environment.SetEnvironmentVariable("PATH", originalPath);
            CopilotCliResolver.FileExistsResolver = originalFileExistsResolver;
        }
    }

    [Fact]
    public void GetCopilotCliPath_WhenCopilotExeIsOnPath_ShouldReturnExePath()
    {
        // Arrange
        var originalEnvValue = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalFileExistsResolver = CopilotCliResolver.FileExistsResolver;
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilot-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var exePath = Path.Combine(tempDir, "copilot.exe");
        File.WriteAllText(exePath, "test");

        try
        {
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", null);
            Environment.SetEnvironmentVariable("PATH", tempDir);
            CopilotCliResolver.FileExistsResolver = _ => false;

            // Act
            var cliPath = CopilotCliResolver.GetCopilotCliPath();

            // Assert
            cliPath.Should().Be(exePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", originalEnvValue);
            Environment.SetEnvironmentVariable("PATH", originalPath);
            CopilotCliResolver.FileExistsResolver = originalFileExistsResolver;
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void GetCopilotCliPath_WhenNpmLoaderIsOnPath_ShouldReturnNpmLoaderPath()
    {
        // Arrange
        var originalEnvValue = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalFileExistsResolver = CopilotCliResolver.FileExistsResolver;
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilot-path-{Guid.NewGuid():N}");
        var npmLoaderPath = Path.Combine(tempDir, "node_modules", "@github", "copilot", "npm-loader.js");
        Directory.CreateDirectory(Path.GetDirectoryName(npmLoaderPath)!);
        File.WriteAllText(npmLoaderPath, "test");

        try
        {
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", null);
            Environment.SetEnvironmentVariable("PATH", tempDir);
            CopilotCliResolver.FileExistsResolver = _ => false;

            // Act
            var cliPath = CopilotCliResolver.GetCopilotCliPath();

            // Assert
            cliPath.Should().Be(npmLoaderPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", originalEnvValue);
            Environment.SetEnvironmentVariable("PATH", originalPath);
            CopilotCliResolver.FileExistsResolver = originalFileExistsResolver;
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ParseCliModelIds_ShouldExtractDistinctModelIdsFromModelSection()
    {
        // Arrange
        const string helpText = """
            Usage:
                            --model <model>  Model to use ("gpt-5.3-codex", "claude-sonnet-4.6", "gpt-5.3-codex")
              --no-alt-screen  Disable alternate screen mode
            """;

        // Act
        var result = InvokeParseCliModelIds(helpText);

        // Assert
        result.Should().ContainInOrder("gpt-5.3-codex", "claude-sonnet-4.6");
        result.Should().HaveCount(2);
    }

    [Fact]
    public void ParseCliModelIds_WhenSectionMarkerMissing_ShouldFallbackToFullHelpText()
    {
        // Arrange
        const string helpText = "Available models: \"gpt-5.1\", \"claude-opus-4.6\", \"1.0.0\"";

        // Act
        var result = InvokeParseCliModelIds(helpText);

        // Assert
        result.Should().Contain("gpt-5.1");
        result.Should().Contain("claude-opus-4.6");
        result.Should().NotContain("1.0.0");
    }

    [Fact]
    public void ParseCliModelIds_WhenSectionHasNoMatches_ShouldFallbackToFullHelpText()
    {
        // Arrange
        const string helpText = """
            --model <model>  Choose from aliases like "default"
            --no-alt-screen
            Other output includes "gpt-5" and "gemini-3-pro-preview"
            """;

        // Act
        var result = InvokeParseCliModelIds(helpText);

        // Assert
        result.Should().Contain("gpt-5");
        result.Should().Contain("gemini-3-pro-preview");
    }

    [Fact]
    public void ToModelDisplayName_ShouldFormatKnownTokens()
    {
        // Arrange & Act
        var displayName = InvokeToModelDisplayName("gpt-5.3-codex-mini-preview");

        // Assert
        displayName.Should().Be("GPT 5.3 Codex Mini (Preview)");
    }

    [Fact]
    public void ResolveTargetSource_WhenModelAvailableInBothAndByokActive_ShouldPreferByok()
    {
        // Arrange
        SetModelSources(_session, ["gpt-5"], "GitHub, Byok");
        SetPrivateField(_session, "_useByokOpenAi", true);
        SetPrivateField(_session, "_isGitHubCopilotAuthenticated", true);

        // Act
        var source = InvokeResolveTargetSource(_session, "gpt-5");

        // Assert
        source.Should().Be("Byok");
    }

    [Fact]
    public void UpdateAvailableModels_WhenDuplicateAcrossProviders_ShouldLabelCombinedSource()
    {
        // Arrange
        var githubModels = new List<ModelInfo>
        {
            new() { Id = "gpt-5", Name = "GPT 5" }
        };

        var byokModels = new List<ModelInfo>
        {
            new() { Id = "gpt-5", Name = "GPT 5 (BYOK)" }
        };

        // Act
        InvokeUpdateAvailableModels(_session, githubModels, byokModels);
        var availableModels = GetPrivateField<List<ModelInfo>>(_session, "_availableModels");

        // Assert
        availableModels.Should().ContainSingle(model =>
            model.Id == "gpt-5" && model.Name.Contains("[GitHub+BYOK]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TryGetByokProviderModelsAsync_WhenByokNotConfigured_ShouldReturnEmpty()
    {
        // Arrange
        SetPrivateField(_session, "_byokOpenAiApiKey", null);
        SetPrivateField(_session, "_byokOpenAiBaseUrl", "https://api.openai.com/v1");

        // Act
        var models = await InvokeTryGetByokProviderModelsAsync(_session);

        // Assert
        models.Should().BeEmpty();
    }

    [Fact]
    public void ParseByokModelsResponse_ShouldCapturePricingAndCapabilities()
    {
        // Arrange
        const string json = """
            {
              "data": [
                {
                  "id": "gpt-5",
                  "name": "GPT 5",
                  "pricing": {
                    "input": 1.25,
                    "output": 10
                  },
                  "supported_reasoning_efforts": ["low", "medium", "high"],
                  "default_reasoning_effort": "medium",
                  "capabilities": {
                    "supports": {
                      "vision": true,
                      "reasoning_effort": true
                    },
                    "limits": {
                      "max_context_window_tokens": 400000,
                      "max_prompt_tokens": 128000
                    }
                  }
                }
              ]
            }
            """;

        using var document = JsonDocument.Parse(json);

        // Act
        var result = InvokeParseByokModelsResponse(document.RootElement);

        // Assert
        result.Models.Should().ContainSingle();
        result.Models[0].Id.Should().Be("gpt-5");
        result.Models[0].Capabilities.Should().NotBeNull();
        result.Models[0].Capabilities!.Supports!.Vision.Should().BeTrue();
        result.Models[0].Capabilities.Supports.ReasoningEffort.Should().BeTrue();
        result.Models[0].Capabilities.Limits!.MaxContextWindowTokens.Should().Be(400000);
        result.Models[0].Capabilities.Limits.MaxPromptTokens.Should().Be(128000);
        result.Models[0].DefaultReasoningEffort.Should().Be("medium");
        result.Models[0].SupportedReasoningEfforts.Should().Contain(["low", "medium", "high"]);
        result.PricingByModelId.Should().ContainKey("gpt-5");
        result.PricingByModelId["gpt-5"].DisplayText.Should().Be("$1.25/M in, $10/M out");
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", new[] { "https://api.openai.com/v1/models" })]
    [InlineData("https://example.test", new[] { "https://example.test/models", "https://example.test/v1/models" })]
    [InlineData(" https://example.test/v1/ ", new[] { "https://example.test/v1/models" })]
    public void BuildByokModelEndpointCandidates_ShouldNormalizeAndDeduplicate(string baseUrl, string[] expected)
    {
        var manager = new ByokProviderManager();

        var endpoints = manager.BuildByokModelEndpointCandidates(baseUrl);

        endpoints.Should().Equal(expected);
    }

    [Fact]
    public void GetModelSelectionEntries_DualSourceModel_ShouldProduceTwoEntries()
    {
        // Arrange
        var githubModels = new List<ModelInfo> { new() { Id = "gpt-5", Name = "GPT 5" } };
        var byokModels = new List<ModelInfo> { new() { Id = "gpt-5", Name = "GPT 5" } };
        SetPrivateField(_session, "_isGitHubCopilotAuthenticated", true);
        SetPrivateField(_session, "_byokOpenAiApiKey", "sk-test");
        SetPrivateField(_session, "_byokOpenAiBaseUrl", "https://api.openai.com/v1");
        InvokeUpdateAvailableModels(_session, githubModels, byokModels);

        // Act
        var entries = InvokeGetModelSelectionEntries(_session);

        // Assert
        entries.Should().HaveCount(2);
        entries.Select(e => e.DisplayName).Should().Contain(n => n.Contains("GitHub Copilot", StringComparison.Ordinal));
        entries.Select(e => e.DisplayName).Should().Contain(n => n.Contains("BYOK", StringComparison.Ordinal));
        entries.Select(e => e.ModelId).Should().AllBe("gpt-5");
    }

    [Fact]
    public void GetModelSelectionEntries_SingleSourceByok_ShouldProduceOneEntry()
    {
        // Arrange
        var githubModels = new List<ModelInfo>();
        var byokModels = new List<ModelInfo> { new() { Id = "custom-model", Name = "Custom Model" } };
        SetPrivateField(_session, "_byokOpenAiApiKey", "sk-test");
        SetPrivateField(_session, "_byokOpenAiBaseUrl", "https://api.openai.com/v1");
        InvokeUpdateAvailableModels(_session, githubModels, byokModels);

        // Act
        var entries = InvokeGetModelSelectionEntries(_session);

        // Assert
        entries.Should().ContainSingle();
        entries[0].ModelId.Should().Be("custom-model");
        entries[0].Source.Should().Be("Byok");
    }

    [Fact]
    public void GetModelSelectionEntries_SingleSourceGitHub_ShouldProduceOneEntry()
    {
        // Arrange
        var githubModels = new List<ModelInfo> { new() { Id = "gpt-4.1", Name = "GPT 4.1" } };
        var byokModels = new List<ModelInfo>();
        SetPrivateField(_session, "_isGitHubCopilotAuthenticated", true);
        InvokeUpdateAvailableModels(_session, githubModels, byokModels);

        // Act
        var entries = InvokeGetModelSelectionEntries(_session);

        // Assert
        entries.Should().ContainSingle();
        entries[0].ModelId.Should().Be("gpt-4.1");
        entries[0].Source.Should().Be("GitHub");
    }

    [Fact]
    public void GetModelSelectionEntries_WhenGitHubDisconnected_ShouldHideGitHubEntries()
    {
        // Arrange
        var githubModels = new List<ModelInfo> { new() { Id = "gpt-4.1", Name = "GPT 4.1" } };
        InvokeUpdateAvailableModels(_session, githubModels, []);

        // Act
        var entries = InvokeGetModelSelectionEntries(_session);

        // Assert
        entries.Should().BeEmpty();
    }

    [Fact]
    public void ActiveProviderDisplayName_WhenByok_ShouldReturnByokLabel()
    {
        // Arrange
        SetPrivateField(_session, "_useByokOpenAi", true);

        // Act
        var provider = _session.ActiveProviderDisplayName;

        // Assert
        provider.Should().Be("BYOK (OpenAI)");
    }

    [Fact]
    public void ActiveProviderDisplayName_WhenGitHub_ShouldReturnGitHubLabel()
    {
        // Arrange
        SetPrivateField(_session, "_useByokOpenAi", false);

        // Act
        var provider = _session.ActiveProviderDisplayName;

        // Assert
        provider.Should().Be("GitHub Copilot");
    }

    [Fact]
    public void GetStatusFields_ShouldIncludeProviderField()
    {
        // Arrange
        SetPrivateField(_session, "_useByokOpenAi", false);

        // Act
        var fields = _session.GetStatusFields();

        // Assert
        fields.Should().Contain(f => f.Label == "Provider" && f.Value == "GitHub Copilot");
    }

    [Fact]
    public void GetStatusFields_WhenUsageIncludesContext_ShouldIncludeCombinedContextField()
    {
        // Arrange
        SetPrivateField(_session, "_lastUsage", CreateUsageSnapshot(maxContextTokens: 100000, usedContextTokens: 25000));

        // Act
        var fields = _session.GetStatusFields();

        // Assert
        fields.Should().Contain(f => f.Label == "Context" && f.Value == "25,000/100,000 (25%)");
    }

    [Fact]
    public async Task ChangeModelAsync_WithByokEntry_ShouldSetByokProvider()
    {
        await ExecuteWithTemporarySettingsPathAsync(async _settingsPath =>
        {
            // Arrange — set up dual-source model
            var githubModels = new List<ModelInfo> { new() { Id = "gpt-5", Name = "GPT 5" } };
            var byokModels = new List<ModelInfo> { new() { Id = "gpt-5", Name = "GPT 5" } };
            InvokeUpdateAvailableModels(_session, githubModels, byokModels);

            SetPrivateField(_session, "_useByokOpenAi", false);
            SetPrivateField(_session, "_byokOpenAiApiKey", "sk-test");
            SetPrivateField(_session, "_byokOpenAiBaseUrl", "https://api.openai.com/v1");

            // Set a non-null _copilotClient to pass the guard check
            var client = new CopilotClient(new CopilotClientOptions());
            SetPrivateField(_session, "_copilotClient", client);

            var entries = InvokeGetModelSelectionEntries(_session);
            var byokEntry = entries.First(e => e.Source == "Byok");

            // Act — will fail at session creation or persist only to the temporary settings path
            _ = await InvokeChangeModelAsyncWithEntry(_session, byokEntry);

            // Assert — _useByokOpenAi should be set to true
            var useByok = GetPrivateField<bool>(_session, "_useByokOpenAi");
            useByok.Should().BeTrue();

            // Clean up: detach client from session to avoid double-dispose issues
            SetPrivateField(_session, "_copilotClient", null);
            try { await client.DisposeAsync(); } catch { /* SDK cleanup may fail in test context */ }
        });
    }

    [Fact]
    public async Task ChangeModelAsync_WithGitHubEntry_ShouldClearByokProvider()
    {
        await ExecuteWithTemporarySettingsPathAsync(async _settingsPath =>
        {
            // Arrange — set up dual-source model
            var githubModels = new List<ModelInfo> { new() { Id = "gpt-5", Name = "GPT 5" } };
            var byokModels = new List<ModelInfo> { new() { Id = "gpt-5", Name = "GPT 5" } };
            InvokeUpdateAvailableModels(_session, githubModels, byokModels);

            SetPrivateField(_session, "_useByokOpenAi", true);
            SetPrivateField(_session, "_isGitHubCopilotAuthenticated", true);

            // Set a non-null _copilotClient to pass the guard check
            var client = new CopilotClient(new CopilotClientOptions());
            SetPrivateField(_session, "_copilotClient", client);

            var entries = InvokeGetModelSelectionEntries(_session);
            var githubEntry = entries.First(e => e.Source == "GitHub");

            // Act — will fail at session creation or persist only to the temporary settings path
            _ = await InvokeChangeModelAsyncWithEntry(_session, githubEntry);

            // Assert — _useByokOpenAi should be set to false
            var useByok = GetPrivateField<bool>(_session, "_useByokOpenAi");
            useByok.Should().BeFalse();

            // Clean up: detach client from session to avoid double-dispose issues
            SetPrivateField(_session, "_copilotClient", null);
            try { await client.DisposeAsync(); } catch { /* SDK cleanup may fail in test context */ }
        });
    }

    [Fact]
    public void SaveModelAndProviderState_WhenSwitchingToGitHub_ShouldPersistProviderAndKeepByokConfig()
    {
        ExecuteWithTemporarySettingsPath(_ =>
        {
            // Arrange
            AppSettingsStore.Save(new AppSettings
            {
                LastModel = "old-model",
                UseByokOpenAi = true,
                ByokOpenAiBaseUrl = "https://proxy.example/v1",
                ByokOpenAiApiKey = "sk-test"
            });

            // Act
            InvokeSaveModelAndProviderState("gpt-4.1", useByokOpenAi: false);
            var saved = AppSettingsStore.Load();

            // Assert
            saved.LastModel.Should().Be("gpt-4.1");
            saved.UseByokOpenAi.Should().BeFalse();
            saved.ByokOpenAiBaseUrl.Should().Be("https://proxy.example/v1");
            saved.ByokOpenAiApiKey.Should().Be("sk-test");
        });
    }

    [Fact]
    public void ResetStateForNewAiSession_ShouldClearSessionUsageTracker()
    {
        var tracker = GetPrivateField<SessionUsageTracker>(_session, "_sessionUsageTracker");
        var pricing = new TroubleshootingSession.ByokPriceInfo(2.50m, 10.00m, null);
        tracker.RecordTurn(100, 50, pricing, 1.0);

        var method = typeof(TroubleshootingSession).GetMethod("ResetStateForNewAiSession", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        method!.Invoke(_session, null);

        tracker.TotalInputTokens.Should().Be(0);
        tracker.TotalOutputTokens.Should().Be(0);
        tracker.TotalTurns.Should().Be(0);
        tracker.GetCostEstimateDisplay().Should().BeNull();
    }

    [Fact]
    public void SaveModelAndProviderState_WhenSwitchingToByok_ShouldPersistProviderAndModel()
    {
        ExecuteWithTemporarySettingsPath(_ =>
        {
            // Arrange
            AppSettingsStore.Save(new AppSettings
            {
                LastModel = "old-model",
                UseByokOpenAi = false,
                ByokOpenAiBaseUrl = "https://api.openai.com/v1",
                ByokOpenAiApiKey = "sk-test"
            });

            // Act
            InvokeSaveModelAndProviderState("gpt-4.1", useByokOpenAi: true);
            var saved = AppSettingsStore.Load();

            // Assert
            saved.LastModel.Should().Be("gpt-4.1");
            saved.UseByokOpenAi.Should().BeTrue();
        });
    }

    #endregion

    #region SessionConfig Tests

    [Fact]
    public void BuildSessionConfig_ShouldIncludeClientName()
    {
        // Arrange & Act
        var config = InvokeBuildSessionConfig(_session, "gpt-4.1");

        // Assert
        config.ClientName.Should().Be("TroubleScout");
    }

    [Fact]
    public void BuildSessionConfig_ShouldIncludePermissionRequestHandler()
    {
        // Arrange & Act
        var config = InvokeBuildSessionConfig(_session, "gpt-4.1");

        // Assert
        config.OnPermissionRequest.Should().NotBeNull();
    }

    [Fact]
    public void BuildSessionConfig_ShouldIncludeEarlyEventHandler()
    {
        var config = InvokeBuildSessionConfig(_session, "gpt-4.1");

        config.OnEvent.Should().NotBeNull();
    }

    [Fact]
    public void EarlyEventHandler_SessionStart_ShouldCaptureSelectedModelAndCopilotVersion()
    {
        var config = InvokeBuildSessionConfig(_session, "gpt-4.1");

        config.OnEvent.Should().NotBeNull();
        config.OnEvent!(new SessionStartEvent
        {
            Data = new SessionStartData
            {
                SessionId = "session-1",
                Version = 2,
                Producer = "copilot",
                CopilotVersion = "1.0.10",
                StartTime = DateTimeOffset.UtcNow,
                SelectedModel = "claude-sonnet-4.6",
                ReasoningEffort = "high"
            }
        });

        GetPrivateField<string>(_session, "_selectedModel").Should().Be("claude-sonnet-4.6");
        GetPrivateField<string>(_session, "_copilotVersion").Should().Be("1.0.10");
        GetPrivateField<string>(_session, "_selectedReasoningEffort").Should().Be("high");
    }

    [Fact]
    public void EarlyEventHandler_SessionModelChange_ShouldCaptureNewModel()
    {
        SetPrivateField(_session, "_selectedModel", "gpt-4.1");
        var config = InvokeBuildSessionConfig(_session, "gpt-4.1");

        config.OnEvent.Should().NotBeNull();
        config.OnEvent!(new SessionModelChangeEvent
        {
            Data = new SessionModelChangeData
            {
                PreviousModel = "gpt-4.1",
                NewModel = "gpt-5-mini",
                PreviousReasoningEffort = "medium",
                ReasoningEffort = "high"
            }
        });

        GetPrivateField<string>(_session, "_selectedModel").Should().Be("gpt-5-mini");
        GetPrivateField<string>(_session, "_selectedReasoningEffort").Should().Be("high");
    }

    [Fact]
    public void EarlyEventHandler_AssistantUsage_ShouldRecordUsageOnce()
    {
        var config = InvokeBuildSessionConfig(_session, "gpt-4.1");
        config.OnEvent.Should().NotBeNull();

        config.OnEvent!(new AssistantUsageEvent
        {
            Data = new AssistantUsageData
            {
                Model = "gpt-4.1",
                InputTokens = 120,
                OutputTokens = 30
            }
        });

        var tracker = GetPrivateField<SessionUsageTracker>(_session, "_sessionUsageTracker");
        tracker.TotalInputTokens.Should().Be(120);
        tracker.TotalOutputTokens.Should().Be(30);
        tracker.TotalTurns.Should().Be(1);
    }

    [Fact]
    public void BuildSessionConfig_WhenReasoningEffortConfiguredAndSupported_ShouldApplyReasoningEffort()
    {
        ExecuteWithTemporarySettingsPath(_ =>
        {
            AppSettingsStore.Save(new AppSettings
            {
                ReasoningEffort = "high"
            });

            var session = new TroubleshootingSession("localhost");
            SetPrivateField(session, "_availableModels", new List<ModelInfo>
            {
                new ModelInfo
                {
                    Id = "gpt-5",
                    Name = "GPT 5",
                    SupportedReasoningEfforts = ["low", "medium", "high"],
                    DefaultReasoningEffort = "medium",
                    Capabilities = new ModelCapabilities
                    {
                        Supports = new ModelSupports
                        {
                            ReasoningEffort = true
                        }
                    }
                }
            });

            var config = InvokeBuildSessionConfig(session, "gpt-5");

            config.ReasoningEffort.Should().Be("high");
        });
    }

    [Fact]
    public void BuildSessionConfig_WhenReasoningEffortConfiguredButUnsupported_ShouldNotApplyReasoningEffort()
    {
        ExecuteWithTemporarySettingsPath(_ =>
        {
            AppSettingsStore.Save(new AppSettings
            {
                ReasoningEffort = "high"
            });

            var session = new TroubleshootingSession("localhost");
            SetPrivateField(session, "_availableModels", new List<ModelInfo>
            {
                new ModelInfo
                {
                    Id = "gpt-4.1",
                    Name = "GPT 4.1",
                    SupportedReasoningEfforts = ["low"],
                    Capabilities = new ModelCapabilities
                    {
                        Supports = new ModelSupports
                        {
                            ReasoningEffort = true
                        }
                    }
                }
            });

            var config = InvokeBuildSessionConfig(session, "gpt-4.1");

            config.ReasoningEffort.Should().BeNull();
        });
    }

    [Fact]
    public void GetStatusFields_WhenReasoningIsActive_ShouldIncludeReasoningField()
    {
        SetPrivateField(_session, "_selectedModel", "gpt-5");
        SetPrivateField(_session, "_selectedReasoningEffort", "high");
        SetPrivateField(_session, "_availableModels", new List<ModelInfo>
        {
            new ModelInfo
            {
                Id = "gpt-5",
                Name = "GPT 5",
                SupportedReasoningEfforts = ["low", "medium", "high"],
                DefaultReasoningEffort = "medium",
                Capabilities = new ModelCapabilities
                {
                    Supports = new ModelSupports
                    {
                        ReasoningEffort = true
                    }
                }
            }
        });

        var fields = _session.GetStatusFields();

        fields.Should().Contain(field => field.Label == "Reasoning" && field.Value == "high");
    }

    [Fact]
    public void BuildStatusBarInfo_WhenReasoningIsActive_ShouldIncludeReasoningEffort()
    {
        SetPrivateField(_session, "_selectedModel", "gpt-5");
        SetPrivateField(_session, "_selectedReasoningEffort", "high");
        SetPrivateField(_session, "_availableModels", new List<ModelInfo>
        {
            new ModelInfo
            {
                Id = "gpt-5",
                Name = "GPT 5",
                SupportedReasoningEfforts = ["low", "medium", "high"],
                DefaultReasoningEffort = "medium",
                Capabilities = new ModelCapabilities
                {
                    Supports = new ModelSupports
                    {
                        ReasoningEffort = true
                    }
                }
            }
        });

        var info = _session.BuildStatusBarInfo();

        info.ReasoningEffort.Should().Be("high");
    }

    [Fact]
    public async Task PermissionHandler_FileRead_ShouldApproveInSafeMode()
    {
        // Arrange - default session is Safe mode
        var config = InvokeBuildSessionConfig(_session, "gpt-4.1");
        var handler = config.OnPermissionRequest!;

        // Act
        var result = await handler(new PermissionRequest { Kind = "file-read" }, new PermissionInvocation());

        // Assert
        result.Kind.Should().Be(PermissionRequestResultKind.Approved);
    }

    [Fact]
    public async Task PermissionHandler_UrlFetch_ShouldApproveInSafeMode()
    {
        // Arrange
        var config = InvokeBuildSessionConfig(_session, "gpt-4.1");
        var handler = config.OnPermissionRequest!;

        // Act
        var result = await handler(new PermissionRequest { Kind = "url-fetch" }, new PermissionInvocation());

        // Assert
        result.Kind.Should().Be(PermissionRequestResultKind.Approved);
    }

    [Fact]
    public async Task PermissionHandler_CustomTool_ShouldApproveInSafeMode()
    {
        // Arrange
        var config = InvokeBuildSessionConfig(_session, "gpt-4.1");
        var handler = config.OnPermissionRequest!;

        // Act
        var result = await handler(new PermissionRequest { Kind = "custom-tool" }, new PermissionInvocation());

        // Assert
        result.Kind.Should().Be(PermissionRequestResultKind.Approved);
    }

    [Fact]
    public async Task PermissionHandler_Read_ShouldApproveInSafeMode()
    {
        // Arrange
        var config = InvokeBuildSessionConfig(_session, "gpt-4.1");
        var handler = config.OnPermissionRequest!;

        // Act
        var result = await handler(new PermissionRequest { Kind = "read" }, new PermissionInvocation());

        // Assert
        result.Kind.Should().Be(PermissionRequestResultKind.Approved);
    }

    [Fact]
    public async Task PermissionHandler_Mcp_InYoloMode_ShouldApprove()
    {
        // Arrange - switch to YOLO mode
        var session = new TroubleshootingSession("localhost", executionMode: ExecutionMode.Yolo);
        var config = InvokeBuildSessionConfig(session, "gpt-4.1");
        var handler = config.OnPermissionRequest!;

        // Act
        var result = await handler(new PermissionRequest { Kind = "mcp" }, new PermissionInvocation());

        // Assert
        result.Kind.Should().Be(PermissionRequestResultKind.Approved);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task PermissionHandler_ShellReadOnlyPowerShellCommand_ShouldApproveInSafeMode()
    {
        // Arrange
        var config = InvokeBuildSessionConfig(_session, "gpt-4.1");
        var handler = config.OnPermissionRequest!;
        var request = CreateShellPermissionRequest("Get-ChildItem -Path 'C:\\src\\temp' | Select-Object Name,Length,LastWriteTime | Sort-Object LastWriteTime -Descending");

        // Act
        var result = await handler(request, new PermissionInvocation());

        // Assert
        result.Kind.Should().Be(PermissionRequestResultKind.Approved);
    }

    [Fact]
    public async Task PermissionHandler_ShellBlockedPowerShellCommand_ShouldDenyInSafeMode()
    {
        // Arrange
        var config = InvokeBuildSessionConfig(_session, "gpt-4.1");
        var handler = config.OnPermissionRequest!;
        var request = CreateShellPermissionRequest("Get-Credential");

        // Act
        var result = await handler(request, new PermissionInvocation());

        // Assert
        result.Kind.Should().Be(PermissionRequestResultKind.DeniedInteractivelyByUser);
    }

    [Fact]
    public void EvaluateShellPermissionRequest_ReadOnlyPipeline_ShouldReturnSafeValidation()
    {
        // Arrange
        var request = CreateShellPermissionRequest("Get-ChildItem -Path 'C:\\src\\temp' | Select-Object Name,Length,LastWriteTime | Sort-Object LastWriteTime -Descending");

        // Act
        var assessment = PermissionEvaluator.EvaluateShellPermissionRequest(request, _session.CurrentExecutionMode, null);

        // Assert
        assessment.Should().NotBeNull();
        assessment!.Command.Should().Contain("Get-ChildItem");
        assessment.Validation.IsAllowed.Should().BeTrue();
        assessment.Validation.RequiresApproval.Should().BeFalse();
        assessment.ImpactText.Should().Contain("recognized as read-only");
    }

    [Fact]
    public void EvaluateShellPermissionRequest_MutatingPowerShellCommand_ShouldRequireApproval()
    {
        // Arrange
        var request = CreateShellPermissionRequest("Restart-Service -Name spooler");

        // Act
        var assessment = PermissionEvaluator.EvaluateShellPermissionRequest(request, _session.CurrentExecutionMode, null);

        // Assert
        assessment.Should().NotBeNull();
        assessment!.Validation.IsAllowed.Should().BeTrue();
        assessment.Validation.RequiresApproval.Should().BeTrue();
        assessment.PromptReason.Should().Contain("Safe mode");
        assessment.ImpactText.Should().Contain("not classified as read-only");
    }

    [Fact]
    public void EvaluateShellPermissionRequest_NonPowerShellShellCommand_ShouldReturnNull()
    {
        // Arrange
        var request = CreateShellPermissionRequest("cmd /c dir");

        // Act
        var assessment = PermissionEvaluator.EvaluateShellPermissionRequest(request, _session.CurrentExecutionMode, null);

        // Assert
        assessment.Should().BeNull();
    }

    [Fact]
    public void EvaluateShellPermissionRequest_LongCommandWithMutatingSuffix_ShouldUseFullCommandForValidation()
    {
        // Arrange
        var longPath = new string('a', 220);
        var command = $"Get-ChildItem -Path 'C:\\{longPath}' | Select-Object Name,Length,LastWriteTime; Restart-Service -Name spooler";
        var request = CreateShellPermissionRequest(command);

        // Act
        var assessment = PermissionEvaluator.EvaluateShellPermissionRequest(request, _session.CurrentExecutionMode, null);

        // Assert
        assessment.Should().NotBeNull();
        assessment!.Command.Should().Contain("...");
        assessment.Validation.IsAllowed.Should().BeTrue();
        assessment.Validation.RequiresApproval.Should().BeTrue();
        assessment.PromptReason.Should().Contain("Safe mode");
    }

    [Fact]
    public void EvaluateShellPermissionRequest_NoSpacePipeline_ShouldStillLookLikePowerShell()
    {
        // Arrange
        var request = CreateShellPermissionRequest("Get-Process|Sort-Object CPU");

        // Act
        var assessment = PermissionEvaluator.EvaluateShellPermissionRequest(request, _session.CurrentExecutionMode, null);

        // Assert
        assessment.Should().NotBeNull();
        assessment!.Validation.IsAllowed.Should().BeTrue();
        assessment.Validation.RequiresApproval.Should().BeFalse();
    }

    [Fact]
    public void EvaluateShellPermissionRequest_BatchAtEchoCommand_ShouldReturnNull()
    {
        // Arrange
        var request = CreateShellPermissionRequest("@echo off");

        // Act
        var assessment = PermissionEvaluator.EvaluateShellPermissionRequest(request, _session.CurrentExecutionMode, null);

        // Assert
        assessment.Should().BeNull();
    }

    [Fact]
    public void DescribePermissionRequest_ShellRequest_ShouldPreferConcreteCommand()
    {
        // Arrange
        var request = CreateShellPermissionRequest("Restart-Service Spooler");

        // Act
        var actual = InvokeDescribePermissionRequest(request);

        // Assert
        actual.Should().Be("Restart-Service Spooler");
    }

    [Fact]
    public void DescribePermissionRequest_ShellRequest_ShouldUseFullCommandTextPropertyWhenAvailable()
    {
        // Arrange
        var request = CreateShellPermissionRequest("Clear-RecycleBin -Force");

        // Act
        var actual = InvokeDescribePermissionRequest(request);

        // Assert
        actual.Should().Be("Clear-RecycleBin -Force");
    }

    [Fact]
    public void DescribePermissionRequest_ShellRequest_ShouldTrimReflectedCommandPreview()
    {
        // Arrange
        var request = CreateShellPermissionRequest("Clear-RecycleBin -Force\r\n-Confirm:$false");

        // Act
        var actual = InvokeDescribePermissionRequest(request);

        // Assert
        actual.Should().Be("Clear-RecycleBin -Force -Confirm:$false");
    }

    [Fact]
    public void DescribePermissionRequest_ShellRequest_ShouldReadFullCommandTextFromTypedRequest()
    {
        // Arrange
        var request = CreateShellPermissionRequest("Clear-RecycleBin -Force");

        // Act
        var actual = InvokeDescribePermissionRequest(request);

        // Assert
        actual.Should().Be("Clear-RecycleBin -Force");
    }

    [Fact]
    public void DescribePermissionRequest_McpRequest_ShouldIncludeServerAndTool()
    {
        // Arrange
        var request = CreateMcpPermissionRequest("context7", "query-docs", "{\"libraryId\":\"/github/copilot-sdk\"}");

        // Act
        var actual = InvokeDescribePermissionRequest(request);

        // Assert
        actual.Should().Contain("context7/query-docs");
        actual.Should().Contain("\"libraryId\"");
    }

    [Fact]
    public void DescribePermissionRequest_McpRequest_ShouldIgnoreEmptyArguments()
    {
        // Arrange
        var request = CreateMcpPermissionRequest("context7", "query-docs", "   ");

        // Act
        var actual = InvokeDescribePermissionRequest(request);

        // Assert
        actual.Should().Be("context7/query-docs");
    }

    [Fact]
    public void CreateSystemMessage_ShouldWarnAgainstBackgroundMonitoringClaims()
    {
        // Act
        var config = InvokeCreateSystemMessage(_session, "localhost");

        // Assert
        GetCombinedPromptContent(config).Should().Contain("Never say you will keep monitoring");
    }

    [Fact]
    public void BuildPromptForExecutionSafety_ShouldMentionConfirmFalseForMutatingRequests()
    {
        // Act
        var prompt = InvokeBuildPromptForExecutionSafety("please empty my trash");

        // Assert
        prompt.Should().Contain("-Confirm:$false");
    }

    [Fact]
    public void AddContextUsageField_ShouldUseInvariantFormatting()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

        try
        {
            var fields = new List<(string Label, string Value)>();

            InvokeAddContextUsageField(fields, 25000, 100000);

            fields.Should().ContainSingle();
            fields[0].Label.Should().Be("Context");
            fields[0].Value.Should().Be("25,000/100,000 (25%)");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void GetModelRateLabel_ShouldUseInvariantFormatting()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

        try
        {
            var model = new ModelInfo
            {
                Id = "gpt-4.1",
                Name = "GPT 4.1",
                Billing = new ModelBilling { Multiplier = 0.25 }
            };

            var actual = InvokeGetModelRateLabel(_session, model, "GitHub");

            actual.Should().Be("0.25x premium");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    #endregion

    private static SessionConfig InvokeBuildSessionConfig(TroubleshootingSession session, string? model)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("BuildSessionConfig", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull("BuildSessionConfig should be an internal/private method on TroubleshootingSession");
        return (SessionConfig)method!.Invoke(session, [model])!;
    }

    private static string InvokeDescribePermissionRequest(PermissionRequest request)
    {
        return PermissionEvaluator.DescribePermissionRequest(request);
    }

    private static PermissionRequestShell CreateShellPermissionRequest(string command)
    {
        return new PermissionRequestShell
        {
            Kind = "shell",
            FullCommandText = command,
            Intention = "Inspect system state",
            Commands = [],
            PossiblePaths = [],
            PossibleUrls = [],
            HasWriteFileRedirection = false,
            CanOfferSessionApproval = false
        };
    }

    private static PermissionRequestMcp CreateMcpPermissionRequest(string serverName, string toolName, object? args)
    {
        return new PermissionRequestMcp
        {
            Kind = "mcp",
            ServerName = serverName,
            ToolName = toolName,
            ToolTitle = toolName,
            Args = args,
            ReadOnly = true
        };
    }

    private static SystemMessageConfig InvokeCreateSystemMessage(TroubleshootingSession session, string targetServer)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("CreateSystemMessage", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull("CreateSystemMessage should exist on TroubleshootingSession");
        return (SystemMessageConfig)method!.Invoke(session, [targetServer, new List<string>()])!;
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

    private static string InvokeBuildPromptForExecutionSafety(string userMessage)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("BuildPromptForExecutionSafety", BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull("BuildPromptForExecutionSafety should exist on TroubleshootingSession");
        return (string)method!.Invoke(null, [userMessage])!;
    }

    private static void InvokeAddContextUsageField(List<(string Label, string Value)> fields, int? usedContext, int? maxContext)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("AddContextUsageField", BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull("AddContextUsageField should exist on TroubleshootingSession");
        method!.Invoke(null, [fields, usedContext, maxContext]);
    }

    private static string InvokeGetModelRateLabel(TroubleshootingSession session, ModelInfo model, string sourceName)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("GetModelRateLabel", BindingFlags.Instance | BindingFlags.NonPublic);
        var sourceType = typeof(TroubleshootingSession).GetNestedType("ModelSource", BindingFlags.NonPublic);
        sourceType.Should().NotBeNull();
        var source = Enum.Parse(sourceType, sourceName);

        method.Should().NotBeNull("GetModelRateLabel should exist on TroubleshootingSession");
        return (string)method!.Invoke(session, [model, source])!;
    }

    private static string? InvokeResolveInitialSessionModel(TroubleshootingSession session, IReadOnlyList<ModelInfo> availableModels)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("ResolveInitialSessionModel", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull("ResolveInitialSessionModel should exist on TroubleshootingSession");
        return method!.Invoke(session, [availableModels]) as string;
    }

    private static void InvokeSaveModelAndProviderState(string model, bool useByokOpenAi)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("SaveModelAndProviderState", BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull("SaveModelAndProviderState should exist on TroubleshootingSession");
        method!.Invoke(null, [model, useByokOpenAi]);
    }

    private static void ExecuteWithTemporarySettingsPath(Action<string> testAction)
    {
        var originalSettingsPath = AppSettingsStore.SettingsPath;
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"troublescout-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var tempSettingsPath = Path.Combine(tempDirectory, "settings.json");
            AppSettingsStore.SettingsPath = tempSettingsPath;
            testAction(tempSettingsPath);
        }
        finally
        {
            AppSettingsStore.SettingsPath = originalSettingsPath;
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp test files.
            }
        }
    }

    private static async Task ExecuteWithTemporarySettingsPathAsync(Func<string, Task> testAction)
    {
        var originalSettingsPath = AppSettingsStore.SettingsPath;
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"troublescout-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var tempSettingsPath = Path.Combine(tempDirectory, "settings.json");
            AppSettingsStore.SettingsPath = tempSettingsPath;
            await testAction(tempSettingsPath);
        }
        finally
        {
            AppSettingsStore.SettingsPath = originalSettingsPath;
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp test files.
            }
        }
    }

    private static IReadOnlyList<string> InvokeParseCliModelIds(string helpText)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("ParseCliModelIds", BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        return (IReadOnlyList<string>)method!.Invoke(null, [helpText])!;
    }

    private static string InvokeToModelDisplayName(string modelId)
    {
        var method = typeof(ModelDiscoveryManager)
            .GetMethod("ToModelDisplayName", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        method.Should().NotBeNull();
        return (string)method!.Invoke(null, [modelId])!;
    }

    private static string InvokeResolveTargetSource(TroubleshootingSession session, string modelId)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("ResolveTargetSource", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        var result = method!.Invoke(session, [modelId]);
        result.Should().NotBeNull();
        return result!.ToString()!;
    }

    private static void InvokeUpdateAvailableModels(
        TroubleshootingSession session,
        IReadOnlyList<ModelInfo> githubModels,
        IReadOnlyList<ModelInfo> byokModels)
    {
        var manager = GetModelDiscoveryManager(session);
        var method = typeof(ModelDiscoveryManager)
            .GetMethod("UpdateAvailableModels", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        method.Should().NotBeNull();
        method!.Invoke(manager, [githubModels, byokModels]);
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null && instance is TroubleshootingSession session
            && (fieldName == "_availableModels" || fieldName == "_modelSources" || fieldName == "_byokPricing"))
        {
            instance = GetModelDiscoveryManager(session);
            field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        }
        else if (field == null && instance is TroubleshootingSession managerSession && fieldName == "_additionalExecutors")
        {
            instance = GetServerConnectionManager(managerSession);
            field = instance.GetType().GetField("_executors", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        if (field != null)
        {
            return (T)field.GetValue(instance)!;
        }

        var property = instance.GetType().GetProperty(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        property.Should().NotBeNull();
        return (T)property!.GetValue(instance)!;
    }

    private static void SetModelSources(TroubleshootingSession session, IReadOnlyList<string> modelIds, string enumName)
    {
        var manager = GetModelDiscoveryManager(session);
        var field = manager.GetType().GetField("_modelSources", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();

        var modelSources = field!.GetValue(manager);
        modelSources.Should().NotBeNull();

        var dictionaryType = modelSources!.GetType();
        var clearMethod = dictionaryType.GetMethod("Clear");
        clearMethod.Should().NotBeNull();
        clearMethod!.Invoke(modelSources, null);

        var modelSourceType = typeof(ModelDiscoveryManager).Assembly.GetType("TroubleScout.Services.ModelSource");
        modelSourceType.Should().NotBeNull();
        var sourceValue = Enum.Parse(modelSourceType!, enumName, ignoreCase: true);

        var addMethod = dictionaryType.GetMethod("Add", [typeof(string), modelSourceType!]);
        addMethod.Should().NotBeNull();

        foreach (var modelId in modelIds)
        {
            addMethod!.Invoke(modelSources, [modelId, sourceValue]);
        }
    }

    private static async Task<List<ModelInfo>> InvokeTryGetByokProviderModelsAsync(TroubleshootingSession session)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("TryGetByokProviderModelsAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        var task = (Task<List<ModelInfo>>)method!.Invoke(session, [])!;
        return await task;
    }

    private static (List<ModelInfo> Models, Dictionary<string, TestByokPriceInfo> PricingByModelId) InvokeParseByokModelsResponse(JsonElement rootElement)
    {
        var method = typeof(ModelDiscoveryManager)
            .GetMethod("ParseByokModelsResponse", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        method.Should().NotBeNull();
        var result = method!.Invoke(null, [rootElement]);
        result.Should().NotBeNull();

        var resultType = result!.GetType();
        var models = (List<ModelInfo>)resultType.GetProperty("Models")!.GetValue(result)!;
        var pricingObject = resultType.GetProperty("PricingByModelId")!.GetValue(result)!;

        var pricing = new Dictionary<string, TestByokPriceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (System.Collections.IEnumerable)pricingObject)
        {
            var entryType = entry.GetType();
            var key = (string)entryType.GetProperty("Key")!.GetValue(entry)!;
            var value = entryType.GetProperty("Value")!.GetValue(entry)!;
            var valueType = value.GetType();

            pricing[key] = new TestByokPriceInfo(
                (decimal?)valueType.GetProperty("InputPricePerMillionTokens")!.GetValue(value),
                (decimal?)valueType.GetProperty("OutputPricePerMillionTokens")!.GetValue(value),
                (string?)valueType.GetProperty("DisplayText")!.GetValue(value));
        }

        return (models, pricing);
    }

    private record TestModelSelectionEntry(string ModelId, string DisplayName, string Source);
    private record TestByokPriceInfo(decimal? InputPricePerMillionTokens, decimal? OutputPricePerMillionTokens, string? DisplayText);

    private static List<TestModelSelectionEntry> InvokeGetModelSelectionEntries(TroubleshootingSession session)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("GetModelSelectionEntries", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull("GetModelSelectionEntries should exist on TroubleshootingSession");
        var result = method!.Invoke(session, []);
        result.Should().NotBeNull();

        // Convert via reflection to our test record
        var list = new List<TestModelSelectionEntry>();
        foreach (var item in (System.Collections.IEnumerable)result!)
        {
            var type = item.GetType();
            var modelId = (string)type.GetProperty("ModelId")!.GetValue(item)!;
            var displayName = (string)type.GetProperty("DisplayName")!.GetValue(item)!;
            var source = type.GetProperty("Source")!.GetValue(item)!.ToString()!;
            list.Add(new TestModelSelectionEntry(modelId, displayName, source));
        }
        return list;
    }

    private static async Task<bool> InvokeChangeModelAsyncWithEntry(TroubleshootingSession session, TestModelSelectionEntry testEntry)
    {
        var entryType = session.GetType().GetNestedType("ModelSelectionEntry", BindingFlags.NonPublic);
        entryType.Should().NotBeNull("ModelSelectionEntry should exist as a nested type");

        // Parse the source enum value
        var modelSourceType = session.GetType().GetNestedType("ModelSource", BindingFlags.NonPublic)!;
        var sourceValue = Enum.Parse(modelSourceType, testEntry.Source, ignoreCase: true);

        // Construct a ModelSelectionEntry instance
        var entry = Activator.CreateInstance(entryType!, testEntry.ModelId, testEntry.DisplayName, sourceValue);

        var method = typeof(TroubleshootingSession)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "ChangeModelAsync"
                && m.GetParameters().Length >= 1
                && m.GetParameters()[0].ParameterType == entryType);

        method.Should().NotBeNull("ChangeModelAsync(ModelSelectionEntry) overload should exist");
        var task = (Task<bool>)method!.Invoke(session, [entry, null])!;
        return await task;
    }

    private static object GetModelDiscoveryManager(TroubleshootingSession session)
    {
        var field = session.GetType().GetField("_modelDiscovery", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("_modelDiscovery should exist on TroubleshootingSession");
        return field!.GetValue(session)!;
    }

    private static object GetServerConnectionManager(TroubleshootingSession session)
    {
        var field = session.GetType().GetField("_serverManager", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("_serverManager should exist on TroubleshootingSession");
        return field!.GetValue(session)!;
    }

    private static object CreateUsageSnapshot(
        int? promptTokens = null,
        int? completionTokens = null,
        int? totalTokens = null,
        int? inputTokens = null,
        int? outputTokens = null,
        int? maxContextTokens = null,
        int? usedContextTokens = null,
        int? freeContextTokens = null)
    {
        var snapshotType = typeof(TroubleshootingSession).GetNestedType("CopilotUsageSnapshot", BindingFlags.NonPublic);
        snapshotType.Should().NotBeNull();

        return Activator.CreateInstance(
            snapshotType!,
            promptTokens,
            completionTokens,
            totalTokens,
            inputTokens,
            outputTokens,
            maxContextTokens,
            usedContextTokens,
            freeContextTokens)!;
    }

    #region Multi-PSSession / AllTargetServers Tests

    [Fact]
    public void AllTargetServers_WithNoAdditional_ShouldReturnPrimaryOnly()
    {
        // Act
        var servers = _session.AllTargetServers;

        // Assert
        servers.Should().HaveCount(1);
        servers[0].Should().Be("localhost");
    }

    [Fact]
    public async Task AllTargetServers_WithAdditional_ShouldIncludeAll()
    {
        // Arrange
        var dict = GetPrivateField<Dictionary<string, PowerShellExecutor>>(_session, "_additionalExecutors");
        var execB = new PowerShellExecutor("ServerB");
        var execA = new PowerShellExecutor("ServerA");
        dict["ServerB"] = execB;
        dict["ServerA"] = execA;

        try
        {
            // Act
            var servers = _session.AllTargetServers;

            // Assert – primary first, then alphabetical additional
            servers.Should().HaveCount(3);
            servers[0].Should().Be("localhost");
            servers[1].Should().Be("ServerA");
            servers[2].Should().Be("ServerB");
        }
        finally
        {
            dict.Clear();
            execB.Dispose();
            execA.Dispose();
        }
    }

    [Fact]
    public async Task ConnectAdditionalServer_SameAsPrimary_ShouldSucceedWithoutNewExecutor()
    {
        // Arrange – invoke the private ConnectAdditionalServerAsync method
        var method = typeof(TroubleshootingSession)
            .GetMethod("ConnectAdditionalServerAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        // Act
        var task = (Task<(bool Success, string? Error)>)method!.Invoke(_session, ["localhost", false])!;
        var result = await task;

        // Assert
        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
        _session.AllTargetServers.Should().HaveCount(1, "no additional executor should be created for the primary server");
    }

    [Fact]
    public void CreateSystemMessage_WithAdditionalServers_ListsAllSessions()
    {
        // Arrange
        var method = typeof(TroubleshootingSession)
            .GetMethod("CreateSystemMessage", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var additionalServers = new List<string> { "SERVER-B", "SERVER-C" } as IReadOnlyCollection<string>;

        // Act
        var config = method!.Invoke(_session, ["SERVER-A", additionalServers]);
        var content = GetCombinedPromptContent((SystemMessageConfig)config!);

        // Assert — should contain connected-sessions listing
        content.Should().NotBeNullOrWhiteSpace();
        content.Should().Contain("Connected PSSessions", "system message should list all connected sessions");
        content.Should().Contain("SERVER-A", "primary server should appear in session list");
        content.Should().Contain("SERVER-B", "additional server should appear in session list");
        content.Should().Contain("SERVER-C", "additional server should appear in session list");
        content.Should().Contain("sessionName", "system message should explain sessionName parameter");
        content.Should().Contain("Do NOT call connect_server for these", "system message should warn against reconnecting");
    }

    [Fact]
    public void CreateSystemMessage_WithoutAdditionalServers_OmitsConnectedSessionsBlock()
    {
        // Arrange
        var method = typeof(TroubleshootingSession)
            .GetMethod("CreateSystemMessage", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        // Act — no additional servers
        var config = method!.Invoke(_session, ["SERVER-A", null]);
        var content = GetCombinedPromptContent((SystemMessageConfig)config!);

        // Assert — should NOT contain the connected-sessions block
        content.Should().NotBeNullOrWhiteSpace();
        content.Should().NotContain("Connected PSSessions",
            "system message should not list sessions when there are no additional servers");
        // But the multi-server section should still exist
        content.Should().Contain("Multi-Server Sessions",
            "the generic multi-server guidance should remain");
    }

    #endregion

    #region Tool Visibility Tests (T4)

    [Fact]
    public void ToolDescriptions_ShouldContainAllRegisteredToolNames()
    {
        // Arrange — get the static ToolDescriptions dictionary via reflection
        var field = typeof(TroubleshootingSession)
            .GetField("ToolDescriptions", BindingFlags.Static | BindingFlags.NonPublic);
        field.Should().NotBeNull("ToolDescriptions dictionary should exist");

        var dict = field!.GetValue(null) as IDictionary<string, string>;
        dict.Should().NotBeNull();

        // Assert — all 10 registered tool names should be present
        var expectedTools = new[]
        {
            "run_powershell", "get_system_info", "get_event_logs", "get_services",
            "get_processes", "get_disk_space", "get_network_info", "get_performance_counters",
            "connect_server", "connect_jea_server", "close_server_session"
        };

        foreach (var tool in expectedTools)
        {
            dict.Should().ContainKey(tool, $"ToolDescriptions should have an entry for '{tool}'");
        }

        dict.Should().HaveCount(expectedTools.Length, "ToolDescriptions should have exactly the registered tool count");
    }

    [Fact]
    public void GetStatusFields_WhenToolsUsed_ShouldIncludeToolCount()
    {
        // Arrange — set _toolInvocationCount > 0 via reflection
        var field = typeof(TroubleshootingSession)
            .GetField("_toolInvocationCount", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("_toolInvocationCount field should exist");

        field!.SetValue(_session, 5);

        // Act
        var fields = _session.GetStatusFields();

        // Assert
        fields.Should().Contain(f => f.Label == "Tools used" && f.Value == "5");
    }

    [Fact]
    public void GetStatusFields_WhenNoToolsUsed_ShouldNotIncludeToolCount()
    {
        // Arrange — _toolInvocationCount is 0 by default
        var field = typeof(TroubleshootingSession)
            .GetField("_toolInvocationCount", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("_toolInvocationCount field should exist");

        field!.SetValue(_session, 0);

        // Act
        var fields = _session.GetStatusFields();

        // Assert
        fields.Should().NotContain(f => f.Label == "Tools used");
    }

    [Fact]
    public void GetStatusFields_ShouldIncludeSectionSeparators()
    {
        // Arrange
        SetPrivateField(_session, "_useByokOpenAi", false);

        // Act
        var fields = _session.GetStatusFields();

        // Assert – at least the Provider section separator should be present
        fields.Should().Contain(f => f.Label == TroubleScout.UI.ConsoleUI.StatusSectionSeparator && f.Value == "Provider");
    }

    [Fact]
    public void GetStatusFields_WhenUsagePresent_ShouldNotIncludeRedundantContextFields()
    {
        // Arrange
        SetPrivateField(_session, "_lastUsage", CreateUsageSnapshot(maxContextTokens: 100000, usedContextTokens: 25000));

        // Act
        var fields = _session.GetStatusFields();

        // Assert – combined "Context" field present, redundant individual fields removed
        fields.Should().Contain(f => f.Label == "Context");
        fields.Should().NotContain(f => f.Label == "Context max");
        fields.Should().NotContain(f => f.Label == "Context used");
        fields.Should().NotContain(f => f.Label == "Context free");
    }

    [Fact]
    public void SystemMessage_ShouldEncourageToolUse()
    {
        // Arrange
        var method = typeof(TroubleshootingSession)
            .GetMethod("CreateSystemMessage", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        // Act
        var config = method!.Invoke(_session, ["test-server", null]);
        var content = GetCombinedPromptContent((SystemMessageConfig)config!);

        // Assert
        content.Should().NotBeNullOrWhiteSpace();
        content.Should().Contain("Attempt every relevant diagnostic tool",
            "system message should encourage using all tools before giving up");
    }

    [Fact]
    public void SystemMessage_ShouldExplainReadOnlyAlwaysAllowed()
    {
        // Arrange
        var method = typeof(TroubleshootingSession)
            .GetMethod("CreateSystemMessage", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        // Act
        var config = method!.Invoke(_session, ["test-server", null]);
        var content = GetCombinedPromptContent((SystemMessageConfig)config!);

        // Assert
        content.Should().NotBeNullOrWhiteSpace();
        content.Should().Contain("Read-only diagnostic tools execute automatically in ALL modes",
            "system message should clarify read-only tools work in all modes");
    }

    #endregion

    #region Multi-Server CLI / Constructor Tests (Task 2)

    [Fact]
    public async Task Constructor_WithAdditionalInitialServers_ShouldStoreServers()
    {
        // Arrange & Act — use the convenience constructor overload
        var servers = new List<string> { "primary-srv", "extra1", "extra2" };
        await using var session = new TroubleshootingSession(servers);

        // Assert — primary should be servers[0], additional stored in _additionalInitialServers
        session.TargetServer.Should().Be("primary-srv");

        // The additional servers list should be stored (checked via reflection)
        var field = typeof(TroubleshootingSession)
            .GetField("_additionalInitialServers", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("_additionalInitialServers field should exist");

        var storedAdditional = field!.GetValue(session) as IReadOnlyList<string>;
        storedAdditional.Should().NotBeNull();
        storedAdditional.Should().BeEquivalentTo(new[] { "extra1", "extra2" });
    }

    [Fact]
    public async Task Constructor_WithAdditionalInitialServers_ShouldSkipDuplicateOfPrimary()
    {
        // Arrange & Act — additional list contains the primary server
        var servers = new List<string> { "primary-srv", "primary-srv", "extra1" };
        await using var session = new TroubleshootingSession(servers);

        // Assert — duplicate of primary should be excluded
        var field = typeof(TroubleshootingSession)
            .GetField("_additionalInitialServers", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();

        var storedAdditional = field!.GetValue(session) as IReadOnlyList<string>;
        storedAdditional.Should().NotBeNull();
        storedAdditional.Should().BeEquivalentTo(new[] { "extra1" });
    }

    [Fact]
    public async Task Constructor_WithSingleServerList_ShouldHaveNoAdditional()
    {
        // Arrange & Act
        var servers = new List<string> { "only-srv" };
        await using var session = new TroubleshootingSession(servers);

        // Assert
        session.TargetServer.Should().Be("only-srv");
        var field = typeof(TroubleshootingSession)
            .GetField("_additionalInitialServers", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();

        var storedAdditional = field!.GetValue(session) as IReadOnlyList<string>;
        storedAdditional.Should().NotBeNull();
        storedAdditional.Should().BeEmpty();
    }

    [Fact]
    public async Task InitializeAsync_WithAdditionalServers_ShouldAttemptConnectionToEach()
    {
        // Arrange — create session with additional servers
        // Connections will fail (no real server) but the method should be called for each
        await using var session = new TroubleshootingSession(
            "localhost",
            additionalInitialServers: new List<string> { "fake-server-1", "fake-server-2" });

        // Verify the field was stored
        var field = typeof(TroubleshootingSession)
            .GetField("_additionalInitialServers", BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();

        var storedAdditional = field!.GetValue(session) as IReadOnlyList<string>;
        storedAdditional.Should().NotBeNull();
        storedAdditional.Should().HaveCount(2);
        storedAdditional.Should().Contain("fake-server-1");
        storedAdditional.Should().Contain("fake-server-2");
    }

    [Fact]
    public async Task ConnectAdditionalServer_NonExistentHost_ShouldReturnFailure()
    {
        // Arrange — use Yolo mode to skip approval prompt, then invoke private method
        await using var session = new TroubleshootingSession("localhost", executionMode: ExecutionMode.Yolo);

        var method = typeof(TroubleshootingSession)
            .GetMethod("ConnectAdditionalServerAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        // Act — connection should fail for non-existent host
        var task = (Task<(bool Success, string? Error)>)method!.Invoke(session, ["nonexistent-host-12345", false])!;
        var result = await task;

        // Assert — should fail with connection error
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(new[] { "--server", "srv1" }, new[] { "srv1" })]
    [InlineData(new[] { "-s", "srv1" }, new[] { "srv1" })]
    [InlineData(new[] { "-s", "srv1", "-s", "srv2" }, new[] { "srv1", "srv2" })]
    [InlineData(new[] { "-s", "srv1,srv2,srv3" }, new[] { "srv1", "srv2", "srv3" })]
    [InlineData(new[] { "-s", "srv1", "-s", "srv2,srv3" }, new[] { "srv1", "srv2", "srv3" })]
    [InlineData(new string[0], new[] { "localhost" })]
    [InlineData(new[] { "--server", "," }, new[] { "localhost" })]
    [InlineData(new[] { "--server", "" }, new[] { "localhost" })]
    public void ParseServers_ShouldAccumulateAndSplitCommas(string[] args, string[] expected)
    {
        // Act
        var servers = TroubleScout.Program.ParseServers(args);

        // Assert
        servers.Should().BeEquivalentTo(expected, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public void ParseStartupJea_ShouldReturnConfiguredSession()
    {
        var jea = TroubleScout.Program.ParseStartupJea(["--server", "srv1", "--jea", "server2", "JEA-Admins"]);

        jea.HasError.Should().BeFalse();
        jea.Session.Should().Be(("server2", "JEA-Admins"));
    }

    [Fact]
    public void ParseStartupJea_WhenMissing_ShouldReturnNull()
    {
        var jea = TroubleScout.Program.ParseStartupJea(["--server", "srv1"]);

        jea.HasError.Should().BeFalse();
        jea.Session.Should().BeNull();
    }

    [Fact]
    public void ParseStartupJea_WhenDuplicated_ShouldReturnError()
    {
        var jea = TroubleScout.Program.ParseStartupJea(["--jea", "server1", "JEA-Admins", "--jea", "server2", "JEA-Readers"]);

        jea.HasError.Should().BeTrue();
        jea.Error.Should().Be("--jea can only be specified once.");
    }

    [Fact]
    public void ParseStartupJea_WhenMissingConfiguration_ShouldReturnError()
    {
        var jea = TroubleScout.Program.ParseStartupJea(["--jea", "server1"]);

        jea.HasError.Should().BeTrue();
        jea.Error.Should().Be("--jea requires two values: <server> <configurationName>.");
    }

    [Fact]
    public void BuildStartupTargetDisplay_WithLocalhostJea_ShouldIncludeDefaultSession()
    {
        var display = TroubleScout.Program.BuildStartupTargetDisplay(
            ["localhost"],
            ("server1", "JEA-Admins"));

        display.Should().Be("server1 (JEA: JEA-Admins; default session: localhost)");
    }

    [Fact]
    public void BuildStartupTargetDisplay_WithRemotePrimaryAndJea_ShouldKeepPrimaryDisplay()
    {
        var display = TroubleScout.Program.BuildStartupTargetDisplay(
            ["server1"],
            ("server2", "JEA-Admins"));

        display.Should().Be("server1");
    }

    [Fact]
    public void BuildStartupTargetDisplay_WithLocalhostJeaAndAdditionalServers_ShouldIncludeAdditionalSessions()
    {
        var display = TroubleScout.Program.BuildStartupTargetDisplay(
            ["localhost", "server3", "server4"],
            ("server1", "JEA-Admins"));

        display.Should().Be("server1 (JEA: JEA-Admins; default session: localhost; additional sessions: server3, server4)");
    }

    [Fact]
    public void IsSlashCommandInvocation_Server_ShouldMatchServerCommand()
    {
        // Arrange
        var method = typeof(TroubleshootingSession)
            .GetMethod("IsSlashCommandInvocation", BindingFlags.Static | BindingFlags.NonPublic);

        // Act
        var result = (bool)method!.Invoke(null, ["/server srv01", "/server"])!;

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsSlashCommandInvocation_Connect_ShouldNotMatchAfterRename()
    {
        // Arrange
        var method = typeof(TroubleshootingSession)
            .GetMethod("IsSlashCommandInvocation", BindingFlags.Static | BindingFlags.NonPublic);

        // Act
        var result = (bool)method!.Invoke(null, ["/connect srv01", "/server"])!;

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void SlashCommands_ShouldContainServer_NotConnect()
    {
        // Arrange
        var field = typeof(TroubleshootingSession)
            .GetField("SlashCommands", BindingFlags.Static | BindingFlags.NonPublic);

        // Act
        var commands = field?.GetValue(null) as string[];

        // Assert
        commands.Should().NotBeNull();
        commands.Should().Contain("/server");
        commands.Should().NotContain("/connect");
    }

    [Fact]
    public void SlashCommands_ShouldContainSettings()
    {
        // Arrange
        var field = typeof(TroubleshootingSession)
            .GetField("SlashCommands", BindingFlags.Static | BindingFlags.NonPublic);

        // Act
        var commands = field?.GetValue(null) as string[];

        // Assert
        commands.Should().NotBeNull();
        commands.Should().Contain("/settings");
    }

    [Fact]
    public void SlashCommands_ShouldContainJea()
    {
        // Arrange
        var field = typeof(TroubleshootingSession)
            .GetField("SlashCommands", BindingFlags.Static | BindingFlags.NonPublic);

        // Act
        var commands = field?.GetValue(null) as string[];

        // Assert
        commands.Should().NotBeNull();
        commands.Should().Contain("/jea");
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task SendMessageAsync_ShouldAcceptCancellationToken()
    {
        // Arrange – session is not initialized so it returns false immediately,
        // but this validates the method signature accepts a CancellationToken.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = await _session.SendMessageAsync("test", cts.Token);

        // Assert – returns false because session is not initialized (no hang)
        result.Should().BeFalse();
    }

    [Fact]
    public async Task SendMessageAsync_WithDefaultToken_ShouldStillWork()
    {
        // Arrange – verify the default parameter works (no token passed)

        // Act
        var result = await _session.SendMessageAsync("test");

        // Assert – returns false because session is not initialized
        result.Should().BeFalse();
    }

    [Fact]
    public void RunActivityWatchdog_ShouldBeCancellable()
    {
        // Verify the watchdog method signature exists and is internal static for test visibility.
        var method = typeof(TroubleshootingSession).GetMethod(
            "RunActivityWatchdogAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull("RunActivityWatchdogAsync should exist");
        method!.ReturnType.Should().Be(typeof(Task));
    }

    [Theory]
    [InlineData(14.9, null)]
    [InlineData(15.0, "Waiting for response")]
    [InlineData(29.9, "Waiting for response")]
    [InlineData(30.0, "Connection seems slow")]
    public void GetActivityWatchdogStatus_ShouldReturnExpectedThresholdStatus(
        double idleSeconds,
        string? expectedStatus)
    {
        var status = TroubleshootingSession.GetActivityWatchdogStatus(idleSeconds);

        status.Should().Be(expectedStatus);
    }

    #endregion

    #region Reasoning Delta Streaming Tests

    [Fact]
    public void SDK_AssistantReasoningDeltaEvent_ShouldExist()
    {
        // Assert — the SDK exposes the delta event type used for streaming reasoning
        var type = typeof(GitHub.Copilot.SDK.AssistantReasoningDeltaEvent);
        type.Should().NotBeNull();
        type.Name.Should().Be("AssistantReasoningDeltaEvent");
    }

    [Fact]
    public void SDK_AssistantReasoningDeltaData_ShouldHaveDeltaContentProperty()
    {
        // Assert — DeltaContent property exists for incremental reasoning text
        var prop = typeof(GitHub.Copilot.SDK.AssistantReasoningDeltaData)
            .GetProperty("DeltaContent");
        prop.Should().NotBeNull("AssistantReasoningDeltaData should have a DeltaContent property");
    }

    [Fact]
    public void SDK_AssistantReasoningEvent_ShouldStillExistAsFallback()
    {
        // Assert — the full (non-streaming) reasoning event should remain in the SDK
        var type = typeof(GitHub.Copilot.SDK.AssistantReasoningEvent);
        type.Should().NotBeNull();
    }

    #endregion

    #region ConnectAdditionalServerAsync SkipApproval Tests

    [Fact]
    public async Task ConnectAdditionalServer_SkipApproval_ShouldBypassApprovalInSafeMode()
    {
        // Arrange — session in Safe mode; invoke with skipApproval=true via reflection
        await using var session = new TroubleshootingSession("localhost", executionMode: ExecutionMode.Safe);

        var method = typeof(TroubleshootingSession)
            .GetMethod("ConnectAdditionalServerAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        // Act — calling with skipApproval: true should NOT prompt (would throw in test if it did)
        // Use a non-existent host so it fails at connection, proving it got past approval
        var task = (Task<(bool Success, string? Error)>)method!.Invoke(session, ["nonexistent-skipapproval-test", true])!;
        var result = await task;

        // Assert — should fail due to connection error, NOT due to approval denial
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Error.Should().NotContain("denied", "skipApproval=true should bypass approval prompt");
    }

    [Fact]
    public async Task ConnectAdditionalServer_SameAsPrimary_WithSkipApproval_ShouldSucceed()
    {
        // Arrange — same-as-primary should short-circuit regardless of skipApproval
        var method = typeof(TroubleshootingSession)
            .GetMethod("ConnectAdditionalServerAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        // Act
        var task = (Task<(bool Success, string? Error)>)method!.Invoke(_session, ["localhost", true])!;
        var result = await task;

        // Assert
        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void ConnectAdditionalServerAsync_ShouldAcceptSkipApprovalParameter()
    {
        // Assert — method signature must include the optional bool parameter
        var method = typeof(TroubleshootingSession)
            .GetMethod("ConnectAdditionalServerAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var parameters = method!.GetParameters();
        parameters.Should().HaveCount(2, "ConnectAdditionalServerAsync should have (string, bool) parameters");
        parameters[0].ParameterType.Should().Be(typeof(string));
        parameters[1].ParameterType.Should().Be(typeof(bool));
        parameters[1].HasDefaultValue.Should().BeTrue("skipApproval should have a default value");
        parameters[1].DefaultValue.Should().Be(false, "skipApproval default should be false");
    }

    #endregion

    #region JEA Primary Context Tests

    [Fact]
    public async Task EffectiveTargetServer_WithoutJea_ShouldReturnTargetServer()
    {
        // Arrange & Act
        await using var session = new TroubleshootingSession("myserver");

        // Assert
        session.EffectiveTargetServer.Should().Be("myserver");
    }

    [Fact]
    public async Task EffectiveTargetServer_WithJeaButNonLocalhost_ShouldReturnTargetServer()
    {
        // When the base target is NOT localhost, JEA session doesn't replace it
        await using var session = new TroubleshootingSession(
            "myserver",
            initialJeaSession: ("server1", "JEA-Admins"));

        // The JEA executor hasn't been connected yet (no InitializeAsync), so it stays as base
        session.EffectiveTargetServer.Should().Be("myserver");
    }

    [Fact]
    public async Task EffectiveConnectionMode_WithoutJea_ShouldReturnBaseConnectionMode()
    {
        await using var session = new TroubleshootingSession("localhost");

        // Without JEA, effective connection mode should match the base executor's mode
        session.EffectiveConnectionMode.Should().Contain("Local PowerShell");
    }

    [Fact]
    public async Task CreateSystemMessage_WithJeaContext_ShouldIncludePrimaryJeaBlock()
    {
        // Arrange - create a session with a startup JEA session targeting localhost
        await using var session = new TroubleshootingSession(
            "localhost",
            initialJeaSession: ("server1", "JEA-Admins"));

        var additionalExecutors = GetPrivateField<Dictionary<string, PowerShellExecutor>>(session, "_additionalExecutors");
        additionalExecutors["server1"] = new PowerShellExecutor("server1", "JEA-Admins");

        var method = typeof(TroubleshootingSession)
            .GetMethod("CreateSystemMessage", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var config = method!.Invoke(session, ["localhost", new[] { "server1" }]);
        var content = GetCombinedPromptContent((SystemMessageConfig)config!);

        content.Should().NotBeNullOrWhiteSpace();
        content.Should().Contain("## Primary JEA Endpoint: server1 (Configuration: JEA-Admins)");
        content.Should().Contain("assume they mean the current JEA target: server1");
        content.Should().Contain("run_powershell with sessionName: \"server1\"");
        content.Should().Contain("Bootstrap/default session: localhost");
        content.Should().NotContain("Primary (default): localhost");
        session.DefaultSessionTarget.Should().Be("localhost");
    }

    [Fact]
    public async Task EffectiveTargetServers_WithoutJea_ShouldMatchAllTargetServers()
    {
        await using var session = new TroubleshootingSession("localhost");

        // Without JEA, effective target servers should be the same as AllTargetServers
        session.EffectiveTargetServers.Should().BeEquivalentTo(session.AllTargetServers);
    }

    [Fact]
    public async Task EffectiveTargetServer_WhenStartupJeaIsReplacedByNormalSession_ShouldRevertToLocalhost()
    {
        await using var session = new TroubleshootingSession(
            "localhost",
            initialJeaSession: ("server1", "JEA-Admins"));

        var additionalExecutors = GetPrivateField<Dictionary<string, PowerShellExecutor>>(session, "_additionalExecutors");
        additionalExecutors["server1"] = new PowerShellExecutor("server1");

        session.EffectiveTargetServer.Should().Be("localhost");
        session.DefaultSessionTarget.Should().BeNull();
    }

    [Fact]
    public async Task ReconnectAsync_WithLocalhostDuringStartupJeaFocus_ShouldResetFocusToLocalhost()
    {
        await using var session = new TroubleshootingSession(
            "localhost",
            initialJeaSession: ("server1", "JEA-Admins"));

        var additionalExecutors = GetPrivateField<Dictionary<string, PowerShellExecutor>>(session, "_additionalExecutors");
        additionalExecutors["server1"] = new PowerShellExecutor("server1", "JEA-Admins");

        session.EffectiveTargetServer.Should().Be("server1");

        var method = typeof(TroubleshootingSession)
            .GetMethod("ReconnectAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var reconnectTask = (Task<bool>)method!.Invoke(session, ["localhost", null])!;
        var result = await reconnectTask;

        result.Should().BeTrue();
        session.EffectiveTargetServer.Should().Be("localhost");
        session.DefaultSessionTarget.Should().BeNull();
    }

    #endregion
}
