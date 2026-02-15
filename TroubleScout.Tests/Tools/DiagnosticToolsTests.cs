using FluentAssertions;
using Moq;
using TroubleScout.Services;
using TroubleScout.Tools;
using Xunit;

namespace TroubleScout.Tests.Tools;

public class DiagnosticToolsTests : IDisposable
{
    private readonly Mock<PowerShellExecutor> _mockExecutor;
    private readonly Mock<Func<string, string, Task<bool>>> _mockApprovalCallback;
    private readonly DiagnosticTools _diagnosticTools;
    private readonly string _targetServer = "testserver";

    public DiagnosticToolsTests()
    {
        _mockExecutor = new Mock<PowerShellExecutor>("testserver");
        _mockApprovalCallback = new Mock<Func<string, string, Task<bool>>>();
        _diagnosticTools = new DiagnosticTools(_mockExecutor.Object, _mockApprovalCallback.Object, _targetServer);
    }

    public void Dispose()
    {
        _mockExecutor.Object.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Tool Registration Tests

    [Fact]
    public void GetTools_ShouldReturnAllDiagnosticTools()
    {
        // Act
        var tools = _diagnosticTools.GetTools().ToList();

        // Assert
        tools.Should().NotBeEmpty();
        tools.Should().HaveCountGreaterOrEqualTo(8);
        
        var toolNames = tools.Select(t => t.Name).ToArray();
        toolNames.Should().Contain("run_powershell");
        toolNames.Should().Contain("get_system_info");
        toolNames.Should().Contain("get_event_logs");
        toolNames.Should().Contain("get_services");
        toolNames.Should().Contain("get_processes");
        toolNames.Should().Contain("get_disk_space");
        toolNames.Should().Contain("get_network_info");
        toolNames.Should().Contain("get_performance_counters");
    }

    [Fact]
    public void GetTools_AllTools_ShouldHaveDescriptions()
    {
        // Act
        var tools = _diagnosticTools.GetTools().ToList();

        // Assert
        tools.Should().AllSatisfy(tool =>
        {
            tool.Description.Should().NotBeNullOrEmpty();
        });
    }

    #endregion

    #region Target Verification Tests

    [Fact]
    public async Task RunPowerShellCommand_WhenExecuted_ShouldIncludeTargetVerification()
    {
        // Arrange
        var command = "Get-Service";
        string? capturedCommand = null;

        _mockExecutor.Setup(x => x.ValidateCommand(command))
            .Returns(new CommandValidation(true, false));
        
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);

        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .Callback<string, bool>((cmd, _) => capturedCommand = cmd)
            .ReturnsAsync(new PowerShellResult(true, "Output"));

        // Act
        var tools = _diagnosticTools.GetTools().ToList();
        var runPowerShellTool = tools.First(t => t.Name == "run_powershell");
        await runPowerShellTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = command });

        // Assert
        capturedCommand.Should().NotBeNull();
        capturedCommand.Should().Contain("$actualComputer = $env:COMPUTERNAME");
        capturedCommand.Should().Contain(_targetServer);
        capturedCommand.Should().Contain(command);
    }

    [Fact]
    public async Task GetSystemInfo_ShouldIncludeTargetVerification()
    {
        // Arrange
        string? capturedCommand = null;

        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);

        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .Callback<string, bool>((cmd, _) => capturedCommand = cmd)
            .ReturnsAsync(new PowerShellResult(true, "System Info Output"));

        // Act
        var tools = _diagnosticTools.GetTools().ToList();
        var getSystemInfoTool = tools.First(t => t.Name == "get_system_info");
        await getSystemInfoTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());

        // Assert
        capturedCommand.Should().NotBeNull();
        capturedCommand.Should().Contain("$actualComputer = $env:COMPUTERNAME");
        capturedCommand.Should().Contain(_targetServer);
    }

    #endregion

    #region Command Approval Tests

    [Fact]
    public async Task RunPowerShellCommand_SafeCommand_ShouldExecuteDirectly()
    {
        // Arrange
        var command = "Get-Service";
        
        _mockExecutor.Setup(x => x.ValidateCommand(command))
            .Returns(new CommandValidation(true, false));
        
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);

        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "Service output"));

        // Act
        var tools = _diagnosticTools.GetTools().ToList();
        var runPowerShellTool = tools.First(t => t.Name == "run_powershell");
        var result = await runPowerShellTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = command });

        // Assert
        result.Should().NotBeNull();
        _diagnosticTools.PendingCommands.Should().BeEmpty();
        _mockExecutor.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task RunPowerShellCommand_UnsafeCommand_ShouldQueueForApproval()
    {
        // Arrange
        var command = "Stop-Service -Name wuauserv";
        
        _mockExecutor.Setup(x => x.ValidateCommand(command))
            .Returns(new CommandValidation(true, true, "Requires approval"));

        // Act
        var tools = _diagnosticTools.GetTools().ToList();
        var runPowerShellTool = tools.First(t => t.Name == "run_powershell");
        var result = await runPowerShellTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = command });

        // Assert
        var resultString = result?.ToString();
        resultString.Should().Contain("PENDING APPROVAL");
        _diagnosticTools.PendingCommands.Should().HaveCount(1);
        _diagnosticTools.PendingCommands[0].Command.Should().Be(command);
        _mockExecutor.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RunPowerShellCommand_BlockedCommand_ShouldReturnBlocked()
    {
        // Arrange
        var command = "Get-Credential";
        
        _mockExecutor.Setup(x => x.ValidateCommand(command))
            .Returns(new CommandValidation(false, false, "Blocked for security"));

        // Act
        var tools = _diagnosticTools.GetTools().ToList();
        var runPowerShellTool = tools.First(t => t.Name == "run_powershell");
        var result = await runPowerShellTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = command });

        // Assert
        var resultString = result?.ToString();
        resultString.Should().Contain("BLOCKED");
        _diagnosticTools.PendingCommands.Should().BeEmpty();
        _mockExecutor.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    #endregion

    #region Pending Command Management Tests

    [Fact]
    public async Task ClearPendingCommands_ShouldRemoveAllPending()
    {
        // Arrange
        _mockExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Returns(new CommandValidation(true, true, "Requires approval"));

        var tools = _diagnosticTools.GetTools().ToList();
        var runPowerShellTool = tools.First(t => t.Name == "run_powershell");

        // Add multiple pending commands
        await runPowerShellTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = "Stop-Service wuauserv" });
        await runPowerShellTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = "Restart-Computer" });

        // Act
        _diagnosticTools.ClearPendingCommands();

        // Assert
        _diagnosticTools.PendingCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteApprovedCommand_ShouldExecuteAndRemoveFromPending()
    {
        // Arrange
        var command = "Stop-Service -Name wuauserv";
        
        _mockExecutor.Setup(x => x.ValidateCommand(command))
            .Returns(new CommandValidation(true, true, "Requires approval"));
        
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);

        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "Service stopped"));

        // Add pending command first
        var tools = _diagnosticTools.GetTools().ToList();
        var runPowerShellTool = tools.First(t => t.Name == "run_powershell");
        await runPowerShellTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = command });

        var actualPendingCommand = _diagnosticTools.PendingCommands[0];

        // Act
        var result = await _diagnosticTools.ExecuteApprovedCommandAsync(actualPendingCommand);

        // Assert
        result.Should().Contain("Service stopped");
        _diagnosticTools.PendingCommands.Should().BeEmpty();
        _mockExecutor.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
    }

    #endregion

    #region Report Logging Tests

    [Fact]
    public async Task RunPowerShellCommand_SafeCommand_ShouldLogSafeAutoState()
    {
        // Arrange
        var command = "Get-Service";
        var logs = new List<CommandActionLog>();
        var toolsWithLogger = new DiagnosticTools(_mockExecutor.Object, _mockApprovalCallback.Object, _targetServer, logs.Add);

        _mockExecutor.Setup(x => x.ValidateCommand(command))
            .Returns(new CommandValidation(true, false));
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "ok"));

        // Act
        var runPowerShellTool = toolsWithLogger.GetTools().First(t => t.Name == "run_powershell");
        await runPowerShellTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = command });

        // Assert
        logs.Should().ContainSingle();
        logs[0].ApprovalState.Should().Be(CommandApprovalState.SafeAuto);
    }

    [Fact]
    public async Task RunPowerShellCommand_UnsafeCommand_ShouldLogApprovalRequestedState()
    {
        // Arrange
        var command = "Stop-Service -Name wuauserv";
        var logs = new List<CommandActionLog>();
        var toolsWithLogger = new DiagnosticTools(_mockExecutor.Object, _mockApprovalCallback.Object, _targetServer, logs.Add);

        _mockExecutor.Setup(x => x.ValidateCommand(command))
            .Returns(new CommandValidation(true, true, "Requires approval"));

        // Act
        var runPowerShellTool = toolsWithLogger.GetTools().First(t => t.Name == "run_powershell");
        await runPowerShellTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = command });

        // Assert
        logs.Should().ContainSingle();
        logs[0].ApprovalState.Should().Be(CommandApprovalState.ApprovalRequested);
    }

    [Fact]
    public void LogDeniedCommand_ShouldLogDeniedStateWithExpectedOutput()
    {
        // Arrange
        var logs = new List<CommandActionLog>();
        var toolsWithLogger = new DiagnosticTools(_mockExecutor.Object, _mockApprovalCallback.Object, _targetServer, logs.Add);

        // Act
        toolsWithLogger.LogDeniedCommand(new PendingCommand("Stop-Service -Name wuauserv", "Requires approval"));

        // Assert
        logs.Should().ContainSingle();
        logs[0].ApprovalState.Should().Be(CommandApprovalState.Denied);
        logs[0].Output.Should().Be("User denied approval; command was not executed.");
    }

    [Fact]
    public async Task GetSystemInfo_ShouldLogSafeAutoStateWithOutput()
    {
        // Arrange
        var logs = new List<CommandActionLog>();
        var toolsWithLogger = new DiagnosticTools(_mockExecutor.Object, _mockApprovalCallback.Object, _targetServer, logs.Add);

        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "system output"));

        // Act
        var getSystemInfoTool = toolsWithLogger.GetTools().First(t => t.Name == "get_system_info");
        await getSystemInfoTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());

        // Assert
        logs.Should().ContainSingle();
        logs[0].Command.Should().Be("Get-SystemInfo");
        logs[0].Output.Should().Be("system output");
        logs[0].ApprovalState.Should().Be(CommandApprovalState.SafeAuto);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task RunPowerShellCommand_WhenExecutionFails_ShouldReturnError()
    {
        // Arrange
        var command = "Get-Service";
        
        _mockExecutor.Setup(x => x.ValidateCommand(command))
            .Returns(new CommandValidation(true, false));
        
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);

        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(false, "", "Command failed"));

        // Act
        var tools = _diagnosticTools.GetTools().ToList();
        var runPowerShellTool = tools.First(t => t.Name == "run_powershell");
        var result = await runPowerShellTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = command });

        // Assert
        var resultString = result?.ToString();
        resultString.Should().Contain("ERROR");
        resultString.Should().Contain("Command failed");
    }

    [Fact]
    public async Task GetSystemInfo_WhenExecutionFails_ShouldReturnError()
    {
        // Arrange
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);

        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(false, "", "Access denied"));

        // Act
        var tools = _diagnosticTools.GetTools().ToList();
        var getSystemInfoTool = tools.First(t => t.Name == "get_system_info");
        var result = await getSystemInfoTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());

        // Assert
        var resultString = result?.ToString();
        resultString.Should().Contain("ERROR");
        resultString.Should().Contain("Access denied");
    }

    #endregion
}
