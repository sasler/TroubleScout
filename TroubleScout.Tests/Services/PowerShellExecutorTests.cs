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

    [Fact]
    public void ValidateCommand_SelectObjectCommand_ShouldAutoApprove()
    {
        // Arrange
        var command = "Select-Object -InputObject @('a','b') -First 1";

        // Act
        var result = _executor.ValidateCommand(command);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeFalse();
    }

    [Fact]
    public void ValidateCommand_SimpleVariableExpression_ShouldAutoApprove()
    {
        // Arrange
        var command = "$env:COMPUTERNAME";

        // Act
        var result = _executor.ValidateCommand(command);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeFalse();
    }

    [Fact]
    public void ValidateCommand_DangerousVariableExpression_ShouldRequireApproval()
    {
        // Arrange
        var command = "$proc.Kill()";

        // Act
        var result = _executor.ValidateCommand(command);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeTrue();
    }

    [Fact]
    public void ValidateCommand_MutatingCommandInSafeMode_ShouldRequireApproval()
    {
        // Arrange
        _executor.ExecutionMode = ExecutionMode.Safe;

        // Act
        var result = _executor.ValidateCommand("Restart-Service -Name spooler");

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeTrue();
        result.Reason.Should().Contain("Safe mode");
    }

    [Fact]
    public void ValidateCommand_MutatingCommandInYoloMode_ShouldNotRequireApproval()
    {
        // Arrange
        _executor.ExecutionMode = ExecutionMode.Yolo;

        // Act
        var result = _executor.ValidateCommand("Restart-Service -Name spooler");

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeFalse();
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

    [Fact]
    public void SetCustomSafeCommands_WildcardMatch_ShouldAutoApprove()
    {
        // Arrange
        _executor.SetCustomSafeCommands(["Get-*"]);

        // Act
        var result = _executor.ValidateCommand("Get-Service");

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeFalse();
    }

    [Fact]
    public void SetCustomSafeCommands_ExactMatch_ShouldAutoApprove()
    {
        // Arrange
        _executor.SetCustomSafeCommands(["Out-String"]);

        // Act
        var result = _executor.ValidateCommand("Out-String");

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeFalse();
    }

    [Fact]
    public void SetCustomSafeCommands_NoMatch_ShouldRequireApproval()
    {
        // Arrange
        _executor.SetCustomSafeCommands(["Get-*"]);

        // Act
        var result = _executor.ValidateCommand("Out-String");

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeTrue();
    }

    [Fact]
    public void SetCustomSafeCommands_RemovingGetWildcard_ShouldRequireApprovalForGetCommands()
    {
        // Arrange
        _executor.SetCustomSafeCommands(["Out-String"]);

        // Act
        var result = _executor.ValidateCommand("Get-Service");

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeTrue();
    }

    [Fact]
    public void SetCustomSafeCommands_BlockedCommandStillBlocked()
    {
        // Arrange
        _executor.SetCustomSafeCommands(["Get-*"]);

        // Act
        var result = _executor.ValidateCommand("Get-Credential");

        // Assert
        result.IsAllowed.Should().BeFalse();
        result.RequiresApproval.Should().BeFalse();
    }

    [Theory]
    [InlineData("Get-Service", "Get-*", true)]
    [InlineData("get-service", "GET-*", true)]
    [InlineData("Out-String", "Out-String", true)]
    [InlineData("Out-String", "Out-*", true)]
    [InlineData("Out-String", "Get-*", false)]
    [InlineData("Get-Service", "Get-Service", true)]
    [InlineData("Get-Service", "Get-Serv*", true)]
    [InlineData("Get-Service", "Get-Service*", true)]
    [InlineData("Get-Service", "Get-Process", false)]
    [InlineData("Remove-Item", "*", false)]
    [InlineData("Set-Service", "Set-*", false)]
    [InlineData("Remove-Item", "Remove-*", false)]
    [InlineData("Stop-Process", "Stop-*", false)]
    [InlineData("Restart-Service", "Restart-*", false)]
    public void MatchesSafeCommandPattern_VariousCases(string cmdletName, string pattern, bool expected)
    {
        // Act
        var matches = PowerShellExecutor.MatchesSafeCommandPattern(cmdletName, pattern);

        // Assert
        matches.Should().Be(expected);
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

    [Fact]
    public void ValidateCommand_JeaSession_AllowedCommand_ShouldAutoApprove()
    {
        // Arrange
        using var executor = new PowerShellExecutor("server01", "JEA-InfraMgmt");
        executor.SetJeaAllowedCommandsForTesting(["Get-Service"]);

        // Act
        var result = executor.ValidateCommand("Get-Service");

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeFalse();
    }

    [Fact]
    public void ValidateCommand_JeaSession_BlockedCommand_ShouldBlock()
    {
        // Arrange
        using var executor = new PowerShellExecutor("server01", "JEA-InfraMgmt");
        executor.SetJeaAllowedCommandsForTesting(["Get-Service"]);

        // Act
        var result = executor.ValidateCommand("Get-Process");

        // Assert
        result.IsAllowed.Should().BeFalse();
        result.RequiresApproval.Should().BeFalse();
        result.Reason.Should().Contain("not available in JEA session");
    }

    [Fact]
    public void ValidateCommand_JeaSession_BlockedListStillApplies()
    {
        // Arrange
        using var executor = new PowerShellExecutor("server01", "JEA-InfraMgmt");
        executor.SetJeaAllowedCommandsForTesting(["Get-Credential"]);

        // Act
        var result = executor.ValidateCommand("Get-Credential");

        // Assert
        result.IsAllowed.Should().BeFalse();
        result.RequiresApproval.Should().BeFalse();
        result.Reason.Should().Contain("blocked");
    }

    [Fact]
    public void ValidateCommand_JeaSession_Pipeline_AllAllowed_ShouldAutoApprove()
    {
        // Arrange
        using var executor = new PowerShellExecutor("server01", "JEA-InfraMgmt");
        executor.SetJeaAllowedCommandsForTesting(["Get-Service", "Where-Object", "Select-Object"]);

        // Act
        var result = executor.ValidateCommand("Get-Service | Where-Object {$_.Status -eq 'Running'} | Select-Object Name");

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeFalse();
    }

    [Fact]
    public void ValidateCommand_JeaSession_Pipeline_SomeBlocked_ShouldBlock()
    {
        // Arrange
        using var executor = new PowerShellExecutor("server01", "JEA-InfraMgmt");
        executor.SetJeaAllowedCommandsForTesting(["Get-Service", "Where-Object"]);

        // Act
        var result = executor.ValidateCommand("Get-Service | Where-Object {$_.Status -eq 'Running'} | Select-Object Name");

        // Assert
        result.IsAllowed.Should().BeFalse();
        result.RequiresApproval.Should().BeFalse();
        result.Reason.Should().Contain("Select-Object");
    }

    [Fact]
    public void ValidateCommand_JeaSession_HyphenatedParameterValues_ShouldNotBlock()
    {
        // Arrange - hyphenated paths/server names in parameters should NOT be treated as cmdlets
        using var executor = new PowerShellExecutor("testserver", "TestConfig");
        executor.SetJeaAllowedCommandsForTesting(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Get-ChildItem", "Get-Content", "Get-Service"
        });

        // Act - command with hyphenated path that looks like a cmdlet
        var result = executor.ValidateCommand("Get-ChildItem -Path C:\\log-archive");

        // Assert - should pass because only Get-ChildItem is in command position
        result.IsAllowed.Should().BeTrue();
        result.RequiresApproval.Should().BeFalse();
    }

    [Fact]
    public void ValidateCommand_JeaSession_NullAllowedCommands_ShouldBlockAll()
    {
        // Arrange - JEA session where discovery hasn't completed yet
        using var executor = new PowerShellExecutor("testserver", "TestConfig");
        // Don't call SetJeaAllowedCommandsForTesting — _jeaAllowedCommands stays null

        // Act
        var result = executor.ValidateCommand("Get-Service");

        // Assert - fail-closed: block everything if discovery hasn't completed
        result.IsAllowed.Should().BeFalse();
        result.RequiresApproval.Should().BeFalse();
        result.Reason.Should().Contain("discovery has not completed");
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
    public async Task ExecuteAsync_CommandWithNoOutput_ShouldReturnEmptyOutput()
    {
        // Arrange
        await _executor.InitializeAsync();

        // Act
        var result = await _executor.ExecuteAsync("Start-Sleep -Milliseconds 10");

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().BeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSuppressPowerShellConfirmPreference()
    {
        // Arrange
        await _executor.InitializeAsync();

        // Act
        var result = await _executor.ExecuteAsync("$ConfirmPreference");

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Trim().Should().Be("None");
    }

    [Fact]
    public async Task ExecuteAsync_WhenCommandExceedsTimeout_ShouldReturnTimeoutError()
    {
        // Arrange
        await _executor.InitializeAsync();
        _executor.CommandTimeoutOverride = TimeSpan.FromMilliseconds(100);

        try
        {
            // Act
            var result = await _executor.ExecuteAsync("Start-Sleep -Seconds 5");

            // Assert
            result.Success.Should().BeFalse();
            result.Error.Should().Contain("timed out");
        }
        finally
        {
            _executor.CommandTimeoutOverride = null;
        }
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

    [Fact]
    public void AddHistoryEntry_ShouldRecordUserVisibleEntry()
    {
        // Arrange
        var entry = "[PENDING APPROVAL] Clear-RecycleBin -Force";

        // Act
        _executor.AddHistoryEntry(entry);
        var history = _executor.GetCommandHistory();

        // Assert
        history.Should().ContainSingle();
        history[0].Should().Be(entry);
    }

    [Fact]
    public async Task TestConnectionAsync_ShouldNotAddInternalHistoryEntries()
    {
        // Act
        var (success, _) = await _executor.TestConnectionAsync();
        var history = _executor.GetCommandHistory();

        // Assert
        success.Should().BeTrue();
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
