using FluentAssertions;
using GitHub.Copilot.SDK;
using System.Reflection;
using TroubleScout;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests;

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
        await using var session = new TroubleshootingSession("localhost", "gpt-4o");

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
    }

    [Theory]
    [InlineData("/mode", "/mode", true)]
    [InlineData("/mode safe", "/mode", true)]
    [InlineData("/model", "/mode", false)]
    [InlineData("/modeX", "/mode", false)]
    [InlineData("/connect srv01", "/connect", true)]
    [InlineData("/connectX", "/connect", false)]
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
        var config = method!.Invoke(_session, ["localhost"]);
        var content = config?.GetType().GetProperty("Content")?.GetValue(config) as string;

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
        Func<Task> act = () => TroubleshootingSession.CheckCopilotAvailableAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_ShouldNotThrow()
    {
        // Act
        Func<Task> act = async () => await TroubleshootingSession.ValidateCopilotPrerequisitesAsync();

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenNodeMissing_ShouldReturnBlockingIssue()
    {
        // Arrange
        TroubleshootingSession.CopilotCliPathResolver = () => @"C:\fake\copilot.js";
        TroubleshootingSession.FileExistsResolver = _ => true;
        TroubleshootingSession.ProcessRunnerResolver = (fileName, _) =>
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
            var report = await TroubleshootingSession.ValidateCopilotPrerequisitesAsync();

            // Assert
            report.IsReady.Should().BeFalse();
            report.Issues.Should().ContainSingle();
            report.Issues[0].Title.Should().Be("Node.js runtime is missing");
            report.Issues[0].IsBlocking.Should().BeTrue();
        }
        finally
        {
            TroubleshootingSession.ResetPrerequisiteValidationResolvers();
        }
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenNodeVersionTooOld_ShouldReturnBlockingIssue()
    {
        // Arrange
        TroubleshootingSession.CopilotCliPathResolver = () => @"C:\fake\copilot.js";
        TroubleshootingSession.FileExistsResolver = _ => true;
        TroubleshootingSession.ProcessRunnerResolver = (fileName, _) =>
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
            var report = await TroubleshootingSession.ValidateCopilotPrerequisitesAsync();

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
            TroubleshootingSession.ResetPrerequisiteValidationResolvers();
        }
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenValidationThrows_ShouldReturnBlockingIssueWithDetails()
    {
        // Arrange
        TroubleshootingSession.CopilotCliPathResolver = () => throw new InvalidOperationException("boom");

        try
        {
            // Act
            var report = await TroubleshootingSession.ValidateCopilotPrerequisitesAsync();

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
            TroubleshootingSession.ResetPrerequisiteValidationResolvers();
        }
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenCopilotExeAndNodeMissing_ShouldRemainReady()
    {
        TroubleshootingSession.CopilotCliPathResolver = () => "copilot";
        TroubleshootingSession.FileExistsResolver = _ => true;
        TroubleshootingSession.ProcessRunnerResolver = (fileName, arguments) =>
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
            var report = await TroubleshootingSession.ValidateCopilotPrerequisitesAsync();

            report.IsReady.Should().BeTrue();
            report.Issues.Should().BeEmpty();
        }
        finally
        {
            TroubleshootingSession.ResetPrerequisiteValidationResolvers();
        }
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenCopilotCmdAndNodeMissing_ShouldReturnBlockingIssue()
    {
        TroubleshootingSession.CopilotCliPathResolver = () => "copilot";
        TroubleshootingSession.FileExistsResolver = _ => true;
        TroubleshootingSession.ProcessRunnerResolver = (fileName, arguments) =>
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
            var report = await TroubleshootingSession.ValidateCopilotPrerequisitesAsync();

            report.IsReady.Should().BeFalse();
            report.Issues.Should().ContainSingle();
            report.Issues[0].Title.Should().Be("Node.js runtime is missing");
        }
        finally
        {
            TroubleshootingSession.ResetPrerequisiteValidationResolvers();
        }
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenPowerShellIsSix_ShouldAddNonBlockingWarning()
    {
        TroubleshootingSession.CopilotCliPathResolver = () => "copilot";
        TroubleshootingSession.FileExistsResolver = _ => true;
        TroubleshootingSession.ProcessRunnerResolver = (fileName, arguments) =>
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
            var report = await TroubleshootingSession.ValidateCopilotPrerequisitesAsync();

            report.IsReady.Should().BeTrue();
            report.Issues.Should().ContainSingle();
            report.Issues[0].Title.Should().Be("PowerShell version is below recommended");
            report.Issues[0].IsBlocking.Should().BeFalse();
            report.Issues[0].Details.Should().Contain("recommends PowerShell 7+");
        }
        finally
        {
            TroubleshootingSession.ResetPrerequisiteValidationResolvers();
        }
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenPwshUnavailable_ShouldFallbackToWindowsPowerShell()
    {
        TroubleshootingSession.CopilotCliPathResolver = () => "copilot";
        TroubleshootingSession.FileExistsResolver = _ => true;
        TroubleshootingSession.ProcessRunnerResolver = (fileName, arguments) =>
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
            var report = await TroubleshootingSession.ValidateCopilotPrerequisitesAsync();

            report.IsReady.Should().BeTrue();
            report.Issues.Should().ContainSingle();
            report.Issues[0].Details.Should().Contain("Detected powershell 5.1.19041.4522");
        }
        finally
        {
            TroubleshootingSession.ResetPrerequisiteValidationResolvers();
        }
    }

    [Fact]
    public async Task ValidateCopilotPrerequisites_WhenPowerShellVersionIsUnparseable_ShouldNotWarn()
    {
        TroubleshootingSession.CopilotCliPathResolver = () => "copilot";
        TroubleshootingSession.FileExistsResolver = _ => true;
        TroubleshootingSession.ProcessRunnerResolver = (fileName, arguments) =>
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
            var report = await TroubleshootingSession.ValidateCopilotPrerequisitesAsync();

            report.IsReady.Should().BeTrue();
            report.Issues.Should().BeEmpty();
        }
        finally
        {
            TroubleshootingSession.ResetPrerequisiteValidationResolvers();
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
            command,
            output,
            reply,
            "SafeAuto",
            "PowerShell",
            "localhost");

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
    public void RenderCommandHtml_ShouldApplyPowerShellSyntaxHighlighting()
    {
        // Arrange
        const string command = "Get-Service -Name 'BITS' | Where-Object { $_.Status -eq 'Running' }";
        var method = typeof(TroubleshootingSession)
            .GetMethod("RenderCommandHtml", BindingFlags.Static | BindingFlags.NonPublic);

        // Act
        var html = method?.Invoke(null, [command]) as string;

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
        string command,
        string output,
        string reply,
        string safetyApproval,
        string source,
        string target)
    {
        var sessionType = typeof(TroubleshootingSession);
        var actionType = sessionType.GetNestedType("ReportActionEntry", BindingFlags.NonPublic);
        var promptType = sessionType.GetNestedType("ReportPromptEntry", BindingFlags.NonPublic);
        var buildMethod = sessionType.GetMethod("BuildReportHtml", BindingFlags.Static | BindingFlags.NonPublic);

        actionType.Should().NotBeNull();
        promptType.Should().NotBeNull();
        buildMethod.Should().NotBeNull();

        var actionCtor = actionType!
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(ctor => ctor.GetParameters().Length == 6);
        var action = actionCtor.Invoke([
            DateTimeOffset.Now,
            target,
            command,
            output,
            safetyApproval,
            source
        ]);

        var actionListType = typeof(List<>).MakeGenericType(actionType);
        var actionList = Activator.CreateInstance(actionListType)!;
        actionListType.GetMethod("Add")!.Invoke(actionList, [action]);

        var promptCtor = promptType!
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(ctor => ctor.GetParameters().Length == 4);
        var promptEntry = promptCtor.Invoke([
            DateTimeOffset.Now,
            prompt,
            actionList,
            reply
        ]);

        var promptArray = Array.CreateInstance(promptType, 1);
        promptArray.SetValue(promptEntry, 0);

        return (string)buildMethod!.Invoke(null, [promptArray])!;
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
        field.Should().NotBeNull();
        field!.SetValue(instance, value);
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
        var result = await _session.ChangeModelAsync("gpt-4o");

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
        var cliPath = TroubleshootingSession.GetCopilotCliPath();

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
            var cliPath = TroubleshootingSession.GetCopilotCliPath();

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
        
        try
        {
            // Clear COPILOT_CLI_PATH and PATH to force fallback behavior
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", null);
            Environment.SetEnvironmentVariable("PATH", string.Empty);

            // Act
            var cliPath = TroubleshootingSession.GetCopilotCliPath();

            // Assert
            cliPath.Should().Be("copilot");
        }
        finally
        {
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", originalEnvValue);
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void GetCopilotCliPath_WhenCopilotExeIsOnPath_ShouldReturnExePath()
    {
        // Arrange
        var originalEnvValue = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilot-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var exePath = Path.Combine(tempDir, "copilot.exe");
        File.WriteAllText(exePath, "test");

        try
        {
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", null);
            Environment.SetEnvironmentVariable("PATH", tempDir);

            // Act
            var cliPath = TroubleshootingSession.GetCopilotCliPath();

            // Assert
            cliPath.Should().Be(exePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", originalEnvValue);
            Environment.SetEnvironmentVariable("PATH", originalPath);
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilot-path-{Guid.NewGuid():N}");
        var npmLoaderPath = Path.Combine(tempDir, "node_modules", "@github", "copilot", "npm-loader.js");
        Directory.CreateDirectory(Path.GetDirectoryName(npmLoaderPath)!);
        File.WriteAllText(npmLoaderPath, "test");

        try
        {
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", null);
            Environment.SetEnvironmentVariable("PATH", tempDir);

            // Act
            var cliPath = TroubleshootingSession.GetCopilotCliPath();

            // Assert
            cliPath.Should().Be(npmLoaderPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", originalEnvValue);
            Environment.SetEnvironmentVariable("PATH", originalPath);
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

    #endregion

    private static IReadOnlyList<string> InvokeParseCliModelIds(string helpText)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("ParseCliModelIds", BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        return (IReadOnlyList<string>)method!.Invoke(null, [helpText])!;
    }

    private static string InvokeToModelDisplayName(string modelId)
    {
        var method = typeof(TroubleshootingSession)
            .GetMethod("ToModelDisplayName", BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        return (string)method!.Invoke(null, [modelId])!;
    }
}
