using FluentAssertions;
using TroubleScout.Services;
using TroubleScout.Tests.Fixtures;
using Xunit;

namespace TroubleScout.Tests.Services;

public class PowerShellExecutorTests : IDisposable
{
    private readonly PowerShellExecutor _executor;

    public PowerShellExecutorTests()
    {
        _executor = new PowerShellExecutor("localhost");
    }

    public void Dispose()
    {
        _executor.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Command Validation Tests

    [Theory]
    [InlineData("Get-Service")]
    [InlineData("Get-Process")]
    [InlineData("Get-EventLog -LogName System")]
    [InlineData("Get-Disk | Format-List")]
    [InlineData("Get-Volume | Select-Object DriveLetter")]
    public void ValidateCommand_SafeGetCommands_ShouldAutoApprove(string command)
    {
        // Act
        var result = _executor.ValidateCommand(command);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeFalse();
    }

    [Theory]
    [InlineData("Set-Service -Name wuauserv -StartupType Manual")]
    [InlineData("Restart-Service -Name wuauserv")]
    [InlineData("Stop-Process -Name notepad")]
    [InlineData("Remove-Item -Path C:\\temp\\test.txt")]
    [InlineData("Start-Service -Name spooler")]
    public void ValidateCommand_ModificationCommands_ShouldRequireApproval(string command)
    {
        // Act
        var result = _executor.ValidateCommand(command);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeTrue();
        result.Reason.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("Get-Credential")]
    [InlineData("Get-Secret -Name MySecret")]
    [InlineData("Get-Credential -UserName admin")]
    public void ValidateCommand_BlockedCommands_ShouldBeRejected(string command)
    {
        // Act
        var result = _executor.ValidateCommand(command);

        // Assert
        result.IsAllowed.Should().BeFalse();
        result.RequiresApproval.Should().BeFalse();
        result.Reason.Should().Contain("blocked");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ValidateCommand_EmptyOrWhitespace_ShouldBeRejected(string? command)
    {
        // Act
        var result = _executor.ValidateCommand(command!);

        // Assert
        result.IsAllowed.Should().BeFalse();
        result.RequiresApproval.Should().BeFalse();
        result.Reason.Should().Contain("empty");
    }

    #endregion

    #region Multi-line Script Safety Tests

    [Fact]
    public void ValidateCommand_ReadOnlyMultiLineScript_ShouldAutoApprove()
    {
        // Arrange
        var script = @"
$services = Get-Service
$processes = Get-Process
$services | Format-List
$processes | Select-Object Name,Id
";

        // Act
        var result = _executor.ValidateCommand(script);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeFalse();
    }

    [Fact]
    public void ValidateCommand_MixedSafeAndUnsafeScript_ShouldRequireApproval()
    {
        // Arrange
        var script = @"
$services = Get-Service
Stop-Service -Name wuauserv
Write-Output ""Service stopped""
";

        // Act
        var result = _executor.ValidateCommand(script);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeTrue();
        result.Reason.Should().Contain("modify");
    }

    [Fact]
    public void ValidateCommand_FormatListCommand_ShouldAutoApprove()
    {
        // Arrange
        var command = "Get-Disk | Format-List";

        // Act
        var result = _executor.ValidateCommand(command);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeFalse();
    }

    [Fact]
    public void ValidateCommand_FormatVolumeCommand_ShouldRequireApproval()
    {
        // Arrange - Format-Volume FORMATS DISKS, not output!
        var command = "Get-Disk | Format-Volume";

        // Act
        var result = _executor.ValidateCommand(command);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeTrue("Format-Volume modifies disks, not output formatting");
    }

    [Fact]
    public void ValidateCommand_ComplexReadOnlyPipeline_ShouldAutoApprove()
    {
        // Arrange
        var command = "Get-Service | Where-Object {$_.Status -eq 'Running'} | Select-Object Name,DisplayName | Sort-Object Name";

        // Act
        var result = _executor.ValidateCommand(command);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeFalse();
    }

    [Fact]
    public void ValidateCommand_ScriptWithComments_ShouldIgnoreComments()
    {
        // Arrange
        var script = @"
# This is a comment
Get-Service
# Another comment
Get-Process
";

        // Act
        var result = _executor.ValidateCommand(script);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeFalse();
    }

    #endregion

    #region Edge Cases and Security Tests

    [Fact]
    public void ValidateCommand_InjectionAttemptWithSemicolon_ShouldRequireApproval()
    {
        // Arrange
        var command = "Get-Process; Remove-Item C:\\test.txt";

        // Act
        var result = _executor.ValidateCommand(command);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeTrue();
    }

    [Fact]
    public void ValidateCommand_InjectionAttemptWithBacktick_ShouldRequireApproval()
    {
        // Arrange
        var command = "Get-Service `; Remove-Item C:\\test.txt";

        // Act
        var result = _executor.ValidateCommand(command);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeTrue();
    }

    [Fact]
    public void ValidateCommand_PipelineWithDangerousForEach_ShouldRequireApproval()
    {
        // Arrange
        var command = "Get-Process | ForEach-Object { $_.Kill() }";

        // Act
        var result = _executor.ValidateCommand(command);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeTrue();
    }

    #endregion

    #region Local Execution Tests

    [Fact]
    public async Task ExecuteAsync_SimpleGetCommand_ShouldReturnSuccess()
    {
        // Arrange
        await _executor.InitializeAsync();

        // Act
        var result = await _executor.ExecuteAsync("Get-Date | Select-Object -ExpandProperty Year");

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().NotBeNullOrEmpty();
        result.Error.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_InvalidCommand_ShouldReturnError()
    {
        // Arrange
        await _executor.InitializeAsync();

        // Act
        var result = await _executor.ExecuteAsync("Get-NonExistentCmdlet");

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TestConnectionAsync_LocalExecution_ShouldSucceed()
    {
        // Act
        var (success, error) = await _executor.TestConnectionAsync();

        // Assert
        success.Should().BeTrue();
        error.Should().BeNullOrEmpty();
        _executor.ActualComputerName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetConnectionMode_LocalExecution_ShouldIndicateLocal()
    {
        // Act
        var mode = _executor.GetConnectionMode();

        // Assert
        mode.Should().Contain("Local PowerShell");
    }

    #endregion

    #region Command History Tests

    [Fact]
    public async Task GetCommandHistory_AfterExecutingCommands_ShouldTrackAll()
    {
        // Arrange
        await _executor.InitializeAsync();
        var commands = new[] { "Get-Date", "Get-Process | Select-Object -First 1" };

        // Act
        foreach (var cmd in commands)
        {
            await _executor.ExecuteAsync(cmd);
        }
        var history = _executor.GetCommandHistory();

        // Assert
        history.Should().HaveCount(commands.Length);
        history.Should().Contain(commands);
    }

    [Fact]
    public void GetCommandHistory_BeforeAnyExecution_ShouldBeEmpty()
    {
        // Act
        var history = _executor.GetCommandHistory();

        // Assert
        history.Should().BeEmpty();
    }

    #endregion

    #region Target Server Tests

    [Fact]
    public void Constructor_LocalhostVariants_ShouldRecognizeAsLocal()
    {
        // Arrange & Act
        var executors = new[]
        {
            new PowerShellExecutor("localhost"),
            new PowerShellExecutor("127.0.0.1"),
            new PowerShellExecutor("."),
            new PowerShellExecutor(Environment.MachineName),
            new PowerShellExecutor("")
        };

        // Assert
        foreach (var executor in executors)
        {
            executor.GetConnectionMode().Should().Contain("Local");
            executor.Dispose();
        }
    }

    [Fact]
    public void TargetServer_ShouldReturnRequestedServer()
    {
        // Arrange
        var serverName = "testserver";
        using var executor = new PowerShellExecutor(serverName);

        // Act & Assert
        executor.TargetServer.Should().Be(serverName);
    }

    #endregion
}
