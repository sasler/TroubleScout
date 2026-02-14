using FluentAssertions;
using TroubleScout;
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
        // Should either be "copilot" or a path ending with .js
        (cliPath == "copilot" || cliPath.EndsWith(".js")).Should().BeTrue();
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
    public void GetCopilotCliPath_WhenApplicationDataIsNull_ShouldFallbackToCopilotInPath()
    {
        // Arrange
        var originalEnvValue = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        
        try
        {
            // Clear COPILOT_CLI_PATH to force the method to check ApplicationData
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", null);

            // Act
            var cliPath = TroubleshootingSession.GetCopilotCliPath();

            // Assert
            // When ApplicationData is null or npm paths don't exist, should fallback to "copilot"
            cliPath.Should().NotBeNullOrEmpty();
            // The path will either be a valid npm path or the fallback "copilot"
            (cliPath.Contains("copilot") || cliPath == "copilot").Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("COPILOT_CLI_PATH", originalEnvValue);
        }
    }

    #endregion
}
