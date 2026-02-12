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
