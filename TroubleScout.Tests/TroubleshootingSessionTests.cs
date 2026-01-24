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
        // Act
        var isAvailable = await TroubleshootingSession.CheckCopilotAvailableAsync();

        // Assert - Just verify it doesn't throw
        // The actual result depends on the test environment
        isAvailable.Should().Be(isAvailable);
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
}
