using FluentAssertions;
using Moq;
using TroubleScout.Services;
using TroubleScout.Tools;
using TroubleScout.UI;
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
        toolNames.Should().Contain("authorize_delegated_powershell");
        toolNames.Should().Contain("stage_delegated_powershell_script");
        toolNames.Should().Contain("authorize_delegated_powershell_script");
        toolNames.Should().Contain("authorize_delegated_mcp");
        toolNames.Should().Contain("authorize_delegated_url");
        toolNames.Should().Contain("run_delegated_powershell");
        toolNames.Should().Contain("run_delegated_powershell_script");
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

    [Fact]
    public void GetTools_ReadOnlyHelpers_ShouldSkipPermissionPrompts()
    {
        var tools = _diagnosticTools.GetTools().ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var toolName in new[]
        {
            "get_system_info",
            "get_event_logs",
            "get_services",
            "get_processes",
            "get_disk_space",
            "get_network_info",
            "get_performance_counters"
        })
        {
            tools[toolName].AdditionalProperties.Should().ContainKey("skip_permission");
            tools[toolName].AdditionalProperties["skip_permission"].Should().Be(true);
        }
    }

    [Fact]
    public void GetTools_StateChangingHelpers_ShouldRequirePermissionFlow()
    {
        var tools = _diagnosticTools.GetTools().ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var toolName in new[]
        {
            "run_powershell",
            "authorize_delegated_powershell",
            "authorize_delegated_mcp",
            "authorize_delegated_url",
            "run_delegated_powershell",
            "connect_server",
            "connect_jea_server",
            "close_server_session"
        })
        {
            tools[toolName].AdditionalProperties.Should().NotContainKey("skip_permission");
        }
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

        _mockExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Returns(new CommandValidation(true, false));
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

    [Fact]
    public async Task DirectRead_WhenRepeatedInSameTurn_ShouldReturnAlreadyCollectedWithoutExecutingAgain()
    {
        _mockExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Returns(new CommandValidation(true, false));
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "System Info Output"));

        var getSystemInfoTool = _diagnosticTools.GetTools().First(t => t.Name == "get_system_info");

        var first = await getSystemInfoTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());
        var second = await getSystemInfoTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());

        first?.ToString().Should().Contain("System Info Output");
        second?.ToString().Should().Contain("ALREADY COLLECTED");
        second?.ToString().Should().Contain("answer the user now");
        _mockExecutor.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task DirectRead_AfterBeginDiagnosticTurn_ShouldAllowFreshExecution()
    {
        _mockExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Returns(new CommandValidation(true, false));
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "System Info Output"));

        var getSystemInfoTool = _diagnosticTools.GetTools().First(t => t.Name == "get_system_info");

        await getSystemInfoTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());
        _diagnosticTools.BeginDiagnosticTurn();
        await getSystemInfoTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());

        _mockExecutor.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DirectRead_DuringSynthesisOnlyRecovery_ShouldReturnAlreadyCollectedWithoutExecuting()
    {
        _mockExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Returns(new CommandValidation(true, false));

        using var recovery = _diagnosticTools.BeginSynthesisOnlyRecoveryTurn();
        var getSystemInfoTool = _diagnosticTools.GetTools().First(t => t.Name == "get_system_info");

        var result = await getSystemInfoTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());

        result!.ToString().Should().Contain("ALREADY COLLECTED");
        result.ToString().Should().Contain("answer from the diagnostics already collected");
        _mockExecutor.Verify(x => x.ValidateCommand(It.IsAny<string>()), Times.Never);
        _mockExecutor.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RunPowerShellCommand_DuringSynthesisOnlyRecovery_ShouldNotValidateOrExecute()
    {
        const string command = "Get-Service";
        _mockExecutor.Setup(x => x.ValidateCommand(command))
            .Returns(new CommandValidation(true, false));

        using var recovery = _diagnosticTools.BeginSynthesisOnlyRecoveryTurn();
        var runPowerShellTool = _diagnosticTools.GetTools().First(t => t.Name == "run_powershell");

        var result = await runPowerShellTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = command });

        result!.ToString().Should().Contain("ALREADY COLLECTED");
        result.ToString().Should().Contain("answer from the diagnostics already collected");
        _mockExecutor.Verify(x => x.ValidateCommand(It.IsAny<string>()), Times.Never);
        _mockExecutor.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task BeginDiagnosticTurn_DuringSynthesisOnlyRecovery_ShouldNotClearCompletedReads()
    {
        _mockExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Returns(new CommandValidation(true, false));
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "System Info Output"));

        var getSystemInfoTool = _diagnosticTools.GetTools().First(t => t.Name == "get_system_info");

        await getSystemInfoTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());
        using (var recovery = _diagnosticTools.BeginSynthesisOnlyRecoveryTurn())
        {
            _diagnosticTools.BeginDiagnosticTurn();
        }
        var afterRecovery = await getSystemInfoTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());

        afterRecovery!.ToString().Should().Contain("ALREADY COLLECTED");
        _mockExecutor.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
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

    [Fact]
    public async Task RunDelegatedPowerShell_ProtectedCommandWithoutGrant_ShouldNotQueueOrExecute()
    {
        var command = "Stop-Service -Name wuauserv";
        _mockExecutor.Setup(x => x.ValidateCommand(command))
            .Returns(new CommandValidation(true, true, "Requires approval"));

        var tool = _diagnosticTools.GetTools().Single(item => item.Name == "run_delegated_powershell");
        var result = await tool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = command });

        result!.ToString().Should().Contain("PREAUTHORIZATION REQUIRED");
        _diagnosticTools.PendingCommands.Should().BeEmpty();
        _mockExecutor.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RunDelegatedPowerShell_ProtectedCommandWithExactGrant_ShouldExecuteOnce()
    {
        var originalRedirected = ConsoleUI.IsInputRedirectedResolver;
        ConsoleUI.IsInputRedirectedResolver = static () => false;
        try
        {
        var command = "Stop-Service -Name wuauserv";
        _mockExecutor.Setup(x => x.ValidateCommand(command))
            .Returns(new CommandValidation(true, true, "Requires approval"));
        _mockExecutor.Setup(x => x.ActualComputerName).Returns(_targetServer);
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "Service stopped"));
        _mockApprovalCallback.Setup(callback => callback(command, It.IsAny<string>()))
            .ReturnsAsync(true);

        var tools = _diagnosticTools.GetTools().ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var authorization = await tools["authorize_delegated_powershell"].InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = command });
        var grantId = authorization!.ToString()!.Split("authorizationId=", StringSplitOptions.None)[1].Trim();

        var first = await tools["run_delegated_powershell"].InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = command, ["authorizationId"] = grantId });
        var second = await tools["run_delegated_powershell"].InvokeAsync(
            new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = command, ["authorizationId"] = grantId });

        first!.ToString().Should().Contain("Service stopped");
        second!.ToString().Should().Contain("PREAUTHORIZATION REQUIRED");
        _mockExecutor.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
        }
        finally
        {
            ConsoleUI.IsInputRedirectedResolver = originalRedirected;
        }
    }

    [Fact]
    public async Task AuthorizeDelegatedPowerShell_ProtectedHeadlessCommand_ShouldDenyWithoutPrompt()
    {
        var originalRedirected = ConsoleUI.IsInputRedirectedResolver;
        ConsoleUI.IsInputRedirectedResolver = static () => true;
        try
        {
            var command = "Stop-Service -Name wuauserv";
            _mockExecutor.Setup(x => x.ValidateCommand(command))
                .Returns(new CommandValidation(true, true, "Requires approval"));

            var tool = _diagnosticTools.GetTools().Single(item => item.Name == "authorize_delegated_powershell");
            var result = await tool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = command });

            result!.ToString().Should().Contain("[DENIED]");
            _mockApprovalCallback.Verify(callback => callback(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
        finally
        {
            ConsoleUI.IsInputRedirectedResolver = originalRedirected;
        }
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
        logs[0].ApprovalState.Should().Be(CommandApprovalState.StrictReadOnly);
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

        _mockExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Returns(new CommandValidation(true, false));
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "system output"));

        // Act
        var getSystemInfoTool = toolsWithLogger.GetTools().First(t => t.Name == "get_system_info");
        await getSystemInfoTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());

        // Assert
        logs.Should().ContainSingle();
        logs[0].Command.Should().Contain("Get-CimInstance Win32_OperatingSystem");
        logs[0].Output.Should().Be("system output");
        logs[0].ApprovalState.Should().Be(CommandApprovalState.StrictReadOnly);
    }

    [Fact]
    public async Task DelegatedPowerShellScript_ReadOnlyScript_ShouldStageRunCleanupAndLogScript()
    {
        var logs = new List<CommandActionLog>();
        using var tempRoot = new TestTempDirectory();
        var toolsWithLogger = new DiagnosticTools(
            _mockExecutor.Object,
            _mockApprovalCallback.Object,
            _targetServer,
            logs.Add,
            getExecutorCallback: name => name.Equals("srv1", StringComparison.OrdinalIgnoreCase) ? _mockExecutor.Object : null,
            scriptStore: new DelegatedPowerShellScriptStore(tempRoot.Path));
        const string script = "Get-Process | Select-Object -First 1 ProcessName";

        _mockExecutor.Setup(x => x.ValidateCommand(script))
            .Returns(new CommandValidation(true, false, Classification: CommandSafetyClassification.ReadOnly));
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);
        _mockExecutor.Setup(x => x.IsLocalExecution)
            .Returns(true);
        string? executedCommand = null;
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), false))
            .Callback<string, bool>((command, _) => executedCommand = command)
            .ReturnsAsync(new PowerShellResult(true, "process output"));

        var stage = toolsWithLogger.GetTools().First(t => t.Name == "stage_delegated_powershell_script");
        var staged = (await stage.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["script"] = script,
            ["description"] = "List one process"
        }))!.ToString();
        staged.Should().MatchRegex(@"scriptId=[0-9a-f]{32}");
        var scriptId = ExtractScriptId(staged!);

        var run = toolsWithLogger.GetTools().First(t => t.Name == "run_delegated_powershell_script");
        var result = (await run.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["scriptId"] = scriptId }))!.ToString();

        result.Should().Contain("process output");
        Directory.EnumerateFiles(tempRoot.Path, "*.ps1").Should().BeEmpty();
        Directory.EnumerateFiles(tempRoot.Path, "*.json").Should().BeEmpty();
        executedCommand.Should().Contain("-ExecutionPolicy Bypass");
        executedCommand.Should().Contain("-File");
        logs.Should().ContainSingle();
        logs[0].Command.Should().Be(script);
        logs[0].Source.Should().Be("Subagent PowerShell");
        logs[0].CodeKind.Should().Be("Script");
        logs[0].ScriptId.Should().Be(scriptId);
    }

    [Fact]
    public async Task DelegatedPowerShellScript_RunCanRecoverScriptFromTempFileById()
    {
        var logs = new List<CommandActionLog>();
        using var tempRoot = new TestTempDirectory();
        const string script = "Get-Service | Select-Object -First 1 Name";
        var staged = new DelegatedPowerShellScriptStore(tempRoot.Path).Stage(script, "List one service", "srv1");
        var toolsWithSeparateStore = new DiagnosticTools(
            _mockExecutor.Object,
            _mockApprovalCallback.Object,
            _targetServer,
            logs.Add,
            getExecutorCallback: name => name.Equals("srv1", StringComparison.OrdinalIgnoreCase) ? _mockExecutor.Object : null,
            scriptStore: new DelegatedPowerShellScriptStore(tempRoot.Path));

        _mockExecutor.Setup(x => x.ValidateCommand(script))
            .Returns(new CommandValidation(true, false, Classification: CommandSafetyClassification.ReadOnly));
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), false))
            .ReturnsAsync(new PowerShellResult(true, "service output"));

        var run = toolsWithSeparateStore.GetTools().First(t => t.Name == "run_delegated_powershell_script");
        var result = (await run.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["scriptId"] = staged.ScriptId,
            ["sessionName"] = "srv1"
        }))!.ToString();

        result.Should().Contain("service output");
        Directory.EnumerateFiles(tempRoot.Path, "*.ps1").Should().BeEmpty();
        Directory.EnumerateFiles(tempRoot.Path, "*.json").Should().BeEmpty();
        logs.Should().ContainSingle();
        logs[0].Description.Should().Be("List one service");
        logs[0].Target.Should().Be(_targetServer);
    }

    [Fact]
    public async Task DelegatedPowerShellScript_WithStagedSession_ShouldRunWithoutRepeatingSessionName()
    {
        using var tempRoot = new TestTempDirectory();
        const string script = "Get-Service | Select-Object -First 1 Name";
        var alternateExecutor = new Mock<PowerShellExecutor>("srv1");
        var toolsWithAlternateSession = new DiagnosticTools(
            _mockExecutor.Object,
            _mockApprovalCallback.Object,
            _targetServer,
            getExecutorCallback: name => name.Equals("srv1", StringComparison.OrdinalIgnoreCase) ? alternateExecutor.Object : null,
            scriptStore: new DelegatedPowerShellScriptStore(tempRoot.Path));

        alternateExecutor.Setup(x => x.ValidateCommand(script))
            .Returns(new CommandValidation(true, false, Classification: CommandSafetyClassification.ReadOnly));
        alternateExecutor.Setup(x => x.ActualComputerName)
            .Returns("SRV1");
        string? executedCommand = null;
        alternateExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), false))
            .Callback<string, bool>((command, _) => executedCommand = command)
            .ReturnsAsync(new PowerShellResult(true, "service output"));

        var stage = toolsWithAlternateSession.GetTools().First(t => t.Name == "stage_delegated_powershell_script");
        var staged = (await stage.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["script"] = script,
            ["description"] = "List one service",
            ["sessionName"] = "srv1"
        }))!.ToString();
        var scriptId = ExtractScriptId(staged!);

        var run = toolsWithAlternateSession.GetTools().First(t => t.Name == "run_delegated_powershell_script");
        var result = (await run.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["scriptId"] = scriptId }))!.ToString();

        result.Should().Contain("[srv1] service output");
        executedCommand.Should().Contain("-ExecutionPolicy Bypass");
        executedCommand.Should().Contain("-EncodedCommand");
        executedCommand.Should().NotContain(tempRoot.Path);
        Directory.EnumerateFiles(tempRoot.Path).Should().BeEmpty();
    }

    [Fact]
    public async Task DelegatedPowerShellScript_ProtectedScriptWithoutAuthorization_ShouldRequirePreauthorizationAndCleanup()
    {
        using var tempRoot = new TestTempDirectory();
        var toolsWithLogger = new DiagnosticTools(
            _mockExecutor.Object,
            _mockApprovalCallback.Object,
            _targetServer,
            scriptStore: new DelegatedPowerShellScriptStore(tempRoot.Path));
        const string script = "Stop-Service -Name spooler";

        _mockExecutor.Setup(x => x.ValidateCommand(script))
            .Returns(new CommandValidation(true, true, "Requires approval", CommandSafetyClassification.Mutating));

        var stage = toolsWithLogger.GetTools().First(t => t.Name == "stage_delegated_powershell_script");
        var staged = (await stage.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["script"] = script,
            ["description"] = "Stop spooler"
        }))!.ToString();
        staged.Should().MatchRegex(@"scriptId=[0-9a-f]{32}");
        var scriptId = ExtractScriptId(staged!);

        var run = toolsWithLogger.GetTools().First(t => t.Name == "run_delegated_powershell_script");
        var result = (await run.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["scriptId"] = scriptId }))!.ToString();

        result.Should().Contain("PREAUTHORIZATION REQUIRED");
        Directory.EnumerateFiles(tempRoot.Path, "*.ps1").Should().BeEmpty();
        Directory.EnumerateFiles(tempRoot.Path, "*.json").Should().BeEmpty();
    }

    [Fact]
    public async Task DelegatedPowerShellScript_ForJeaSession_ShouldRejectAndCleanup()
    {
        using var tempRoot = new TestTempDirectory();
        var jeaExecutor = new Mock<PowerShellExecutor>("testserver", "JEA-Role");
        var toolsWithLogger = new DiagnosticTools(
            jeaExecutor.Object,
            _mockApprovalCallback.Object,
            _targetServer,
            scriptStore: new DelegatedPowerShellScriptStore(tempRoot.Path));
        const string script = "Get-Service";

        jeaExecutor.Setup(x => x.ValidateCommand(script))
            .Returns(new CommandValidation(true, false, Classification: CommandSafetyClassification.ReadOnly));

        var stage = toolsWithLogger.GetTools().First(t => t.Name == "stage_delegated_powershell_script");
        var staged = (await stage.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["script"] = script,
            ["description"] = "List services"
        }))!.ToString();
        staged.Should().MatchRegex(@"scriptId=[0-9a-f]{32}");
        var scriptId = ExtractScriptId(staged!);

        var run = toolsWithLogger.GetTools().First(t => t.Name == "run_delegated_powershell_script");
        var result = (await run.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["scriptId"] = scriptId }))!.ToString();

        result.Should().Contain("JEA");
        result.Should().Contain("does not support delegated script execution");
        Directory.EnumerateFiles(tempRoot.Path, "*.ps1").Should().BeEmpty();
    }

    [Fact]
    public async Task ReadOnlyHelper_WhenCalledDuringSubagentRun_ShouldLogSubagentOrigin()
    {
        var logs = new List<CommandActionLog>();
        var toolsWithSubagentActive = new DiagnosticTools(
            _mockExecutor.Object,
            _mockApprovalCallback.Object,
            _targetServer,
            logs.Add,
            isSubagentRunActive: () => true);
        _mockExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Returns(new CommandValidation(true, false));
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "process output"));

        var getProcessesTool = toolsWithSubagentActive.GetTools().First(t => t.Name == "get_processes");
        var result = (await getProcessesTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments()))!.ToString();

        result.Should().Contain("process output");
        logs.Should().ContainSingle().Which.Source.Should().Be("Subagent PowerShell");
    }

    [Fact]
    public async Task ExecuteApprovedCommand_ShouldLogApprovedByUserState()
    {
        // Arrange
        var command = "Stop-Service -Name wuauserv";
        var logs = new List<CommandActionLog>();
        var toolsWithLogger = new DiagnosticTools(_mockExecutor.Object, _mockApprovalCallback.Object, _targetServer, logs.Add);

        _mockExecutor.Setup(x => x.ValidateCommand(command))
            .Returns(new CommandValidation(true, true, "Requires approval"));
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "done"));

        var runPowerShellTool = toolsWithLogger.GetTools().First(t => t.Name == "run_powershell");
        await runPowerShellTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["command"] = command });

        var pending = toolsWithLogger.PendingCommands.Should().ContainSingle().Subject;

        // Act
        await toolsWithLogger.ExecuteApprovedCommandAsync(pending);

        // Assert
        logs.Should().HaveCount(2);
        logs[0].ApprovalState.Should().Be(CommandApprovalState.ApprovalRequested);
        logs[1].ApprovalState.Should().Be(CommandApprovalState.ApprovedByUser);
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
        _mockExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Returns(new CommandValidation(true, false));
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

    [Fact]
    public async Task GetSystemInfo_WhenSafeCommandsRequireApproval_ShouldPromptBeforeExecuting()
    {
        // Arrange
        _mockExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Returns(new CommandValidation(true, true, "Requires approval"));
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);
        _mockApprovalCallback.Setup(x => x.Invoke(It.Is<string>(command => command.Contains("Get-CimInstance Win32_OperatingSystem", StringComparison.Ordinal)), "Requires approval"))
            .ReturnsAsync(true);
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "System Info Output"));

        // Act
        var tools = _diagnosticTools.GetTools().ToList();
        var getSystemInfoTool = tools.First(t => t.Name == "get_system_info");
        var result = await getSystemInfoTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());

        // Assert
        result?.ToString().Should().Contain("System Info Output");
        _mockApprovalCallback.Verify(x => x.Invoke(It.Is<string>(command => command.Contains("Get-CimInstance Win32_OperatingSystem", StringComparison.Ordinal)), "Requires approval"), Times.Once);
        _mockExecutor.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task GetSystemInfo_WhenApprovalDenied_ShouldNotExecute()
    {
        // Arrange
        _mockExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Returns(new CommandValidation(true, true, "Requires approval"));
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);
        _mockApprovalCallback.Setup(x => x.Invoke(It.Is<string>(command => command.Contains("Get-CimInstance Win32_OperatingSystem", StringComparison.Ordinal)), "Requires approval"))
            .ReturnsAsync(false);

        // Act
        var tools = _diagnosticTools.GetTools().ToList();
        var getSystemInfoTool = tools.First(t => t.Name == "get_system_info");
        var result = await getSystemInfoTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());

        // Assert
        result?.ToString().Should().Contain("DENIED");
        _mockApprovalCallback.Verify(x => x.Invoke(It.Is<string>(command => command.Contains("Get-CimInstance Win32_OperatingSystem", StringComparison.Ordinal)), "Requires approval"), Times.Once);
        _mockExecutor.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task GetPerformanceCounters_ShouldClassifyKnownReadOnlyCmdletWithoutPrompt()
    {
        // Arrange
        string? validatedCommand = null;

        _mockExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Callback<string>(command => validatedCommand = command)
            .Returns(new CommandValidation(true, false));
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "counter output"));

        // Act
        var tools = _diagnosticTools.GetTools().ToList();
        var getPerformanceCountersTool = tools.First(t => t.Name == "get_performance_counters");
        var result = await getPerformanceCountersTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());

        // Assert
        result?.ToString().Should().Contain("counter output");
        validatedCommand.Should().Be("Get-Counter");
        _mockApprovalCallback.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetPerformanceCounters_AutoUnknown_ShouldReviewActualGeneratedPowerShellCommand()
    {
        string? evaluatedCommand = null;
        var evaluator = new Mock<IAutoCommandApprovalEvaluator>();
        evaluator.Setup(x => x.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((command, _) => evaluatedCommand = command)
            .ReturnsAsync(new AutoCommandApprovalDecision(true, "gpt-5.4-mini", "Read-only performance query."));

        _mockExecutor.Object.ExecutionMode = ExecutionMode.Auto;
        _mockExecutor.Setup(x => x.ValidateCommand("Get-Counter"))
            .Returns(new CommandValidation(true, true, "Not configured as safe.", CommandSafetyClassification.Unknown));
        _mockExecutor.Setup(x => x.ActualComputerName)
            .Returns(_targetServer);
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "counter output"));

        var tools = new DiagnosticTools(
            _mockExecutor.Object,
            _mockApprovalCallback.Object,
            _targetServer,
            autoCommandApprovalEvaluator: evaluator.Object);
        var tool = tools.GetTools().First(t => t.Name == "get_performance_counters");

        var result = await tool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());

        result?.ToString().Should().Contain("counter output");
        evaluatedCommand.Should().Contain("Get-Counter -Counter");
        evaluatedCommand.Should().Contain("Select-Object -ExpandProperty CounterSamples");
        evaluatedCommand.Should().Contain("$actualComputer = $env:COMPUTERNAME");
        evaluatedCommand.Should().NotBe("Get-Counter (All)");
        _mockApprovalCallback.VerifyNoOtherCalls();
    }

    #endregion

    #region Multi-PSSession Tool Tests

    [Fact]
    public async Task ConnectServer_ShouldCallConnectCallback()
    {
        // Arrange
        var callbackCalled = false;
        string? callbackServer = null;
        Func<string, Task<(bool Success, string? Error)>> connectCallback = server =>
        {
            callbackCalled = true;
            callbackServer = server;
            return Task.FromResult<(bool, string?)>((true, null));
        };
        Func<string, PowerShellExecutor?> getExecutorCallback = _ => null;
        Func<string, Task<bool>> closeCallback = _ => Task.FromResult(true);

        var tools = new DiagnosticTools(
            _mockExecutor.Object, _mockApprovalCallback.Object, _targetServer, null,
            connectCallback, getExecutorCallback, closeCallback);

        var connectTool = tools.GetTools().First(t => t.Name == "connect_server");

        // Act
        await connectTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["serverName"] = "ServerB"
        });

        // Assert
        callbackCalled.Should().BeTrue();
        callbackServer.Should().Be("ServerB");
    }

    [Fact]
    public async Task ConnectServer_WhenCallbackFails_ShouldReturnError()
    {
        // Arrange
        Func<string, Task<(bool Success, string? Error)>> connectCallback = server =>
            Task.FromResult<(bool, string?)>((false, "Connection refused"));
        Func<string, PowerShellExecutor?> getExecutorCallback = _ => null;
        Func<string, Task<bool>> closeCallback = _ => Task.FromResult(true);

        var tools = new DiagnosticTools(
            _mockExecutor.Object, _mockApprovalCallback.Object, _targetServer, null,
            connectCallback, getExecutorCallback, closeCallback);

        var connectTool = tools.GetTools().First(t => t.Name == "connect_server");

        // Act
        var result = await connectTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["serverName"] = "ServerB"
        });

        // Assert
        var resultStr = result?.ToString();
        resultStr.Should().Contain("ERROR");
        resultStr.Should().Contain("Connection refused");
    }

    [Fact]
    public async Task CloseServerSession_ShouldCallCloseCallback()
    {
        // Arrange
        var closeCalled = false;
        string? closedServer = null;
        Func<string, Task<(bool Success, string? Error)>> connectCallback = _ =>
            Task.FromResult<(bool, string?)>((true, null));
        Func<string, PowerShellExecutor?> getExecutorCallback = _ => null;
        Func<string, Task<bool>> closeCallback = server =>
        {
            closeCalled = true;
            closedServer = server;
            return Task.FromResult(true);
        };

        var tools = new DiagnosticTools(
            _mockExecutor.Object, _mockApprovalCallback.Object, _targetServer, null,
            connectCallback, getExecutorCallback, closeCallback);

        var closeTool = tools.GetTools().First(t => t.Name == "close_server_session");

        // Act
        await closeTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["serverName"] = "ServerB"
        });

        // Assert
        closeCalled.Should().BeTrue();
        closedServer.Should().Be("ServerB");
    }

    [Fact]
    public async Task RunPowerShell_WithSessionName_ShouldUseAlternateExecutor()
    {
        // Arrange
        var altExecutor = new Mock<PowerShellExecutor>("ServerB");
        altExecutor.Setup(x => x.ActualComputerName).Returns("ServerB");
        altExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Returns(new CommandValidation(true, false));
        altExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "alt-output"));

        Func<string, Task<(bool Success, string? Error)>> connectCallback = _ =>
            Task.FromResult<(bool, string?)>((true, null));
        Func<string, PowerShellExecutor?> getExecutorCallback = name =>
            name.Equals("ServerB", StringComparison.OrdinalIgnoreCase) ? altExecutor.Object : null;
        Func<string, Task<bool>> closeCallback = _ => Task.FromResult(true);

        var tools = new DiagnosticTools(
            _mockExecutor.Object, _mockApprovalCallback.Object, _targetServer, null,
            connectCallback, getExecutorCallback, closeCallback);

        var runTool = tools.GetTools().First(t => t.Name == "run_powershell");

        // Act
        var result = await runTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["command"] = "Get-Service",
            ["sessionName"] = "ServerB"
        });

        // Assert
        var resultStr = result?.ToString();
        resultStr.Should().Contain("[ServerB]");
        resultStr.Should().Contain("alt-output");
        altExecutor.Object.Dispose();
    }

    [Fact]
    public async Task RunPowerShell_WithSessionName_WithoutMultiSessionSupport_ShouldReturnError()
    {
        // Arrange
        _mockExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Returns(new CommandValidation(true, false));
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "primary-output"));

        var runTool = _diagnosticTools.GetTools().First(t => t.Name == "run_powershell");

        // Act
        var result = await runTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["command"] = "Get-Service",
            ["sessionName"] = "ServerB"
        });

        // Assert
        var resultStr = result?.ToString();
        resultStr.Should().Contain("does not support multiple sessions");
        _mockExecutor.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RunPowerShell_WithNullSession_ShouldUsePrimaryExecutor()
    {
        // Arrange
        _mockExecutor.Setup(x => x.ActualComputerName).Returns(_targetServer);
        _mockExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Returns(new CommandValidation(true, false));
        _mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "primary-output"));

        Func<string, Task<(bool Success, string? Error)>> connectCallback = _ =>
            Task.FromResult<(bool, string?)>((true, null));
        Func<string, PowerShellExecutor?> getExecutorCallback = _ => null;
        Func<string, Task<bool>> closeCallback = _ => Task.FromResult(true);

        var tools = new DiagnosticTools(
            _mockExecutor.Object, _mockApprovalCallback.Object, _targetServer, null,
            connectCallback, getExecutorCallback, closeCallback);

        var runTool = tools.GetTools().First(t => t.Name == "run_powershell");

        // Act
        var result = await runTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["command"] = "Get-Service"
        });

        // Assert
        var resultStr = result?.ToString();
        resultStr.Should().NotContain("[ServerB]");
        resultStr.Should().Contain("primary-output");
    }

    #endregion

    #region PendingCommand Session Context Tests

    [Fact]
    public async Task RunPowerShell_WithSessionName_PendingCommand_ShouldUseCorrectExecutor()
    {
        // Arrange
        var altExecutor = new Mock<PowerShellExecutor>("ServerB");
        altExecutor.Setup(x => x.ActualComputerName).Returns("ServerB");
        altExecutor.Setup(x => x.ValidateCommand(It.IsAny<string>()))
            .Returns(new CommandValidation(true, true, "Requires approval"));
        altExecutor.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new PowerShellResult(true, "alt-approved-output"));

        _mockExecutor.Setup(x => x.ActualComputerName).Returns("PrimaryServer");

        Func<string, Task<(bool Success, string? Error)>> connectCallback = _ =>
            Task.FromResult<(bool, string?)>((true, null));
        Func<string, PowerShellExecutor?> getExecutorCallback = name =>
            name.Equals("ServerB", StringComparison.OrdinalIgnoreCase) ? altExecutor.Object : null;
        Func<string, Task<bool>> closeCallback = _ => Task.FromResult(true);

        var tools = new DiagnosticTools(
            _mockExecutor.Object, _mockApprovalCallback.Object, _targetServer, null,
            connectCallback, getExecutorCallback, closeCallback);

        var runTool = tools.GetTools().First(t => t.Name == "run_powershell");

        // Act - run a command that requires approval on an alternate server
        await runTool.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["command"] = "Stop-Service -Name wuauserv",
            ["sessionName"] = "ServerB"
        });

        // Assert - the pending command should carry the alternate executor and server name
        tools.PendingCommands.Should().HaveCount(1);
        var pending = tools.PendingCommands[0];
        pending.Executor.Should().BeSameAs(altExecutor.Object);
        pending.ServerName.Should().Be("ServerB");

        // Act - execute the approved command
        var result = await tools.ExecuteApprovedCommandAsync(pending);

        // Assert - should have used the alternate executor, not the primary
        result.Should().Contain("[ServerB]");
        result.Should().Contain("alt-approved-output");
        altExecutor.Verify(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
        altExecutor.Object.Dispose();
    }

    #endregion

    private static string ExtractScriptId(string text)
    {
        var start = text.IndexOf("scriptId=", StringComparison.Ordinal) + "scriptId=".Length;
        var end = text.IndexOf(' ', start);
        return text[start..end];
    }

    private sealed class TestTempDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "TroubleScout.Tests",
            Guid.NewGuid().ToString("N"));

        internal TestTempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
