using System.ComponentModel;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using TroubleScout.Services;
using TroubleScout.UI;

namespace TroubleScout.Tools;

/// <summary>
/// Provides diagnostic tools for the Copilot AI agent to gather Windows Server information
/// </summary>
public class DiagnosticTools
{
    private readonly PowerShellExecutor _executor;
    private readonly Func<string, string, Task<bool>> _approvalCallback;
    private readonly List<PendingCommand> _pendingCommands = [];
    private readonly string _targetServer;
    private readonly Action<CommandActionLog>? _actionLogger;
    private readonly Func<string, Task<(bool Success, string? Error)>>? _connectServerCallback;
    private readonly Func<string, PowerShellExecutor?>? _getExecutorCallback;
    private readonly Func<string, Task<bool>>? _closeSessionCallback;
    private readonly Func<string, string, Task<(bool Success, string? Error)>>? _connectJeaServerCallback;

    public IReadOnlyList<PendingCommand> PendingCommands => _pendingCommands.AsReadOnly();

    public DiagnosticTools(
        PowerShellExecutor executor,
        Func<string, string, Task<bool>> approvalCallback,
        string targetServer,
        Action<CommandActionLog>? actionLogger = null,
        Func<string, Task<(bool Success, string? Error)>>? connectServerCallback = null,
        Func<string, PowerShellExecutor?>? getExecutorCallback = null,
        Func<string, Task<bool>>? closeSessionCallback = null,
        Func<string, string, Task<(bool Success, string? Error)>>? connectJeaServerCallback = null)
    {
        _executor = executor;
        _approvalCallback = approvalCallback;
        _targetServer = targetServer;
        _actionLogger = actionLogger;
        _connectServerCallback = connectServerCallback;
        _getExecutorCallback = getExecutorCallback;
        _closeSessionCallback = closeSessionCallback;
        _connectJeaServerCallback = connectJeaServerCallback;
    }

    private static string EscapeSingleQuotes(string value)
    {
        return value.Replace("'", "''");
    }

    private static bool IsWarnOutput(string? output)
    {
        return !string.IsNullOrWhiteSpace(output) && output.TrimStart().StartsWith("[WARN]", StringComparison.OrdinalIgnoreCase);
    }

    private void LogReadOnlyAction(string command, string output)
    {
        _actionLogger?.Invoke(new CommandActionLog(
            DateTimeOffset.Now,
            _executor.ActualComputerName ?? _targetServer,
            command,
            output,
            CommandApprovalState.SafeAuto));
    }

    private void LogCommandAction(string target, string command, string output, CommandApprovalState approvalState)
    {
        _actionLogger?.Invoke(new CommandActionLog(
            DateTimeOffset.Now,
            target,
            command,
            output,
            approvalState));
    }

    private async Task<(bool ShouldExecute, string? TerminalOutput, CommandApprovalState ApprovalState)> EnsureCommandApprovedAsync(
        PowerShellExecutor executor,
        string target,
        string displayCommand,
        string validationCommand)
    {
        var validation = executor.ValidateCommand(validationCommand);
        if (!validation.IsAllowed && !validation.RequiresApproval)
        {
            executor.AddHistoryEntry($"[BLOCKED] {displayCommand}");
            var blockedOutput = $"[BLOCKED] {validation.Reason}";
            LogCommandAction(target, displayCommand, blockedOutput, CommandApprovalState.Blocked);
            return (false, blockedOutput, CommandApprovalState.Blocked);
        }

        if (!validation.RequiresApproval)
        {
            return (true, null, CommandApprovalState.SafeAuto);
        }

        executor.AddHistoryEntry($"[PENDING APPROVAL] {displayCommand}");
        var approved = await _approvalCallback(displayCommand, validation.Reason ?? "Requires user approval");
        if (!approved)
        {
            const string deniedOutput = "[DENIED] User denied approval; command was not executed.";
            LogCommandAction(target, displayCommand, deniedOutput, CommandApprovalState.Denied);
            return (false, deniedOutput, CommandApprovalState.Denied);
        }

        return (true, null, CommandApprovalState.ApprovedByUser);
    }

    /// <summary>
    /// Creates AI tools for the Copilot session
    /// </summary>
    public IEnumerable<AIFunction> GetTools()
    {
        yield return AIFunctionFactory.Create(RunPowerShellCommandAsync,
            "run_powershell",
            "Execute a PowerShell command on the target Windows server. Read-only commands run automatically. Mutating commands require approval in Safe mode or run automatically in YOLO mode.");

        yield return AIFunctionFactory.Create(GetSystemInfoAsync,
            "get_system_info",
            "Get basic system information including OS version, hostname, uptime, and hardware specs.");

        yield return AIFunctionFactory.Create(GetEventLogsAsync,
            "get_event_logs",
            "Get recent Windows Event Log entries. Supports System, Application, and Security logs.");

        yield return AIFunctionFactory.Create(GetServicesAsync,
            "get_services",
            "Get Windows services status. Can filter by status (Running, Stopped) or search by name.");

        yield return AIFunctionFactory.Create(GetProcessesAsync,
            "get_processes",
            "Get running processes with CPU and memory usage. Can filter by name or sort by resource usage.");

        yield return AIFunctionFactory.Create(GetDiskSpaceAsync,
            "get_disk_space",
            "Get disk space information for all volumes including free space and health status.");

        yield return AIFunctionFactory.Create(GetNetworkInfoAsync,
            "get_network_info",
            "Get network adapter information including IP addresses, status, and configuration.");

        yield return AIFunctionFactory.Create(GetPerformanceCountersAsync,
            "get_performance_counters",
            "Get performance counter values for CPU, memory, disk, and network metrics.");

        yield return AIFunctionFactory.Create(ConnectServerAsync, "connect_server",
            "Establish a direct PowerShell remoting session to a target server. " +
            "Use this to avoid double-hop authentication issues when you need to run commands on " +
            "a server that is different from the current primary target. Each session runs commands " +
            "directly on that server without going through an intermediate hop.");

        yield return AIFunctionFactory.Create(ConnectJeaServerAsync, "connect_jea_server",
            "Connect to a JEA (Just Enough Administration) constrained PowerShell endpoint on a remote server. " +
            "Only the commands allowed by the JEA configuration will be available.");

        yield return AIFunctionFactory.Create(CloseServerSessionAsync, "close_server_session",
            "Close and dispose a named PowerShell session previously created with connect_server. " +
            "Call this when you no longer need to run commands on that server.");
    }

    /// <summary>
    /// Run an arbitrary PowerShell command with validation
    /// </summary>
    private async Task<string> RunPowerShellCommandAsync(
        [Description("The PowerShell command to execute")] string command,
        [Description("Optional: briefly explain why you need to run this command (shown to user during approval)")] string? intent = null,
        [Description("Optional: the server name to run the command on. If omitted, runs on the primary target server. Must match a server name established with connect_server.")] string? sessionName = null)
    {
        // Resolve the executor for the given session name
        var executor = _executor;
        var target = _executor.ActualComputerName ?? _targetServer;
        var isAlternate = false;

        if (!string.IsNullOrWhiteSpace(sessionName) && _getExecutorCallback == null)
        {
            return $"[ERROR] This tool instance does not support multiple sessions. Cannot target server '{sessionName}'.";
        }

        if (!string.IsNullOrWhiteSpace(sessionName) && _getExecutorCallback != null)
        {
            var altExecutor = _getExecutorCallback(sessionName);
            if (altExecutor == null)
                return $"[ERROR] No session found for server '{sessionName}'. Use connect_server first.";
            executor = altExecutor;
            target = executor.ActualComputerName ?? sessionName;
            isAlternate = true;
        }

        var validation = executor.ValidateCommand(command);

        if (!validation.IsAllowed && !validation.RequiresApproval)
        {
            executor.AddHistoryEntry($"[BLOCKED] {command}");
            _actionLogger?.Invoke(new CommandActionLog(
                DateTimeOffset.Now,
                target,
                command,
                $"[BLOCKED] {validation.Reason}",
                CommandApprovalState.Blocked));
            return $"[BLOCKED] {validation.Reason}";
        }

        if (validation.RequiresApproval)
        {
            executor.AddHistoryEntry($"[PENDING APPROVAL] {command}");
            // Add to pending commands for user approval
            var pending = new PendingCommand(command, validation.Reason ?? "Requires user approval",
                isAlternate ? executor : null, isAlternate ? sessionName : null, intent);
            _pendingCommands.Add(pending);
            _actionLogger?.Invoke(new CommandActionLog(
                DateTimeOffset.Now,
                target,
                command,
                "Command queued for user approval.",
                CommandApprovalState.ApprovalRequested));

            return $"[PENDING APPROVAL] Command '{command}' requires user approval before execution. " +
                   $"Reason: {validation.Reason}. " +
                   "The command has been queued and will be shown to the user for approval.";
        }

        // Safe command - execute directly with target verification
        var wrappedCommand = executor.IsJeaSession
            ? command
            : WrapCommandWithTargetVerification(command, executor, isAlternate ? sessionName : null);
        executor.AddHistoryEntry($"[EXECUTED] {command}");
        ConsoleUI.ShowCommandExecution(command, isAlternate ? sessionName! : _targetServer);
        var result = await executor.ExecuteAsync(wrappedCommand, trackInHistory: false);

        if (!result.Success)
        {
            _actionLogger?.Invoke(new CommandActionLog(
                DateTimeOffset.Now,
                target,
                command,
                $"[ERROR] {result.Error ?? "Unknown error occurred"}",
                executor.ExecutionMode == ExecutionMode.Yolo ? CommandApprovalState.AutoApprovedYolo : CommandApprovalState.SafeAuto));
            var errorOutput = $"[ERROR] {result.Error ?? "Unknown error occurred"}";
            return isAlternate ? $"[{sessionName}] {errorOutput}" : errorOutput;
        }

        var output = string.IsNullOrWhiteSpace(result.Output)
            ? "[OK] Command completed with no output."
            : result.Output;

        _actionLogger?.Invoke(new CommandActionLog(
            DateTimeOffset.Now,
            target,
            command,
            output,
            executor.ExecutionMode == ExecutionMode.Yolo ? CommandApprovalState.AutoApprovedYolo : CommandApprovalState.SafeAuto));

        return isAlternate ? $"[{sessionName}] {output}" : output;
    }

    /// <summary>
    /// Establish a direct PowerShell session to a target server
    /// </summary>
    private async Task<string> ConnectServerAsync(
        [Description("The server name to connect to")] string serverName)
    {
        if (_connectServerCallback == null)
            return "[ERROR] Multi-server sessions are not supported in this configuration.";

        var (success, error) = await _connectServerCallback(serverName);
        if (!success)
            return $"[ERROR] {error ?? $"Failed to connect to {serverName}"}";

        return $"[OK] Connected to {serverName}. Use run_powershell with sessionName: \"{serverName}\" to execute commands.";
    }

    /// <summary>
    /// Establish a JEA-constrained PowerShell session to a target server
    /// </summary>
    private async Task<string> ConnectJeaServerAsync(
        [Description("The server name to connect to")] string serverName,
        [Description("The JEA configuration name to use")] string configurationName)
    {
        if (_connectJeaServerCallback == null)
            return "[ERROR] JEA sessions are not supported in this configuration.";

        var (success, error) = await _connectJeaServerCallback(serverName, configurationName);
        if (!success)
            return $"[ERROR] {error ?? $"Failed to connect to JEA endpoint '{configurationName}' on {serverName}"}";

        return $"[OK] Connected to JEA endpoint '{configurationName}' on {serverName}. Use run_powershell with sessionName: \"{serverName}\" to execute allowed commands only.";
    }

    /// <summary>
    /// Close a named PowerShell session
    /// </summary>
    private async Task<string> CloseServerSessionAsync(
        [Description("The server name of the session to close")] string serverName)
    {
        if (_closeSessionCallback == null)
            return "[ERROR] Multi-server sessions are not supported in this configuration.";

        var closed = await _closeSessionCallback(serverName);
        if (!closed)
            return $"[ERROR] No active session found for server '{serverName}'.";

        return $"[OK] Session to {serverName} closed.";
    }

    /// <summary>
    /// Wrap a command to include target server verification
    /// </summary>
    private string WrapCommandWithTargetVerification(string command, PowerShellExecutor? executor = null, string? serverLabel = null)
    {
        var exec = executor ?? _executor;
        var label = serverLabel ?? _targetServer;
        // Get the verified computer name from the executor
        var expectedComputer = exec.ActualComputerName ?? label.Split('.')[0];
        
        // Prepend a verification that checks computer name and FAILS if wrong target
        // This prevents silently running commands on the wrong server
        return $@"
            $actualComputer = $env:COMPUTERNAME
            $expectedComputer = '{expectedComputer}'
            if ($expectedComputer -ne 'localhost' -and $actualComputer -notlike ""$expectedComputer*"") {{
                throw ""CRITICAL TARGET MISMATCH: Expected to run on '$expectedComputer' but executing on '$actualComputer'. The remote session may have been lost. Please restart TroubleScout.""
            }}
            Write-Output ""[Executing on: $actualComputer]""
            {command}
        ".Trim();
    }

    /// <summary>
    /// Get basic system information
    /// </summary>
    private async Task<string> GetSystemInfoAsync()
    {
        var command = @"
            $os = Get-CimInstance Win32_OperatingSystem
            $cs = Get-CimInstance Win32_ComputerSystem
            $uptime = (Get-Date) - $os.LastBootUpTime
            
            [PSCustomObject]@{
                ComputerName = $env:COMPUTERNAME
                OSName = $os.Caption
                OSVersion = $os.Version
                OSBuild = $os.BuildNumber
                Manufacturer = $cs.Manufacturer
                Model = $cs.Model
                TotalMemoryGB = [math]::Round($cs.TotalPhysicalMemory / 1GB, 2)
                FreeMemoryGB = [math]::Round($os.FreePhysicalMemory / 1MB, 2)
                UptimeDays = [math]::Round($uptime.TotalDays, 2)
                LastBoot = $os.LastBootUpTime
            } | Format-List
        ";
        const string displayCommand = "Get-SystemInfo";
        var target = _executor.ActualComputerName ?? _targetServer;
        var approval = await EnsureCommandApprovedAsync(_executor, target, displayCommand, displayCommand);
        if (!approval.ShouldExecute)
        {
            return approval.TerminalOutput!;
        }

        var wrappedCommand = WrapCommandWithTargetVerification(command);
        ConsoleUI.ShowCommandExecution(displayCommand, _targetServer);
        var result = await _executor.ExecuteAsync(wrappedCommand);
        var output = result.Success ? result.Output : $"[ERROR] {result.Error}";
        LogCommandAction(target, displayCommand, output, approval.ApprovalState);
        return output;
    }

    /// <summary>
    /// Get Windows Event Log entries
    /// </summary>
    private async Task<string> GetEventLogsAsync(
        [Description("Log name: System, Application, or Security")] string logName = "System",
        [Description("Number of recent entries to retrieve (max 50)")] int count = 20,
        [Description("Filter by entry type: Error, Warning, Information, or All")] string entryType = "All")
    {
        count = Math.Min(Math.Max(count, 1), 50);

        var normalizedLogName = logName.Trim();
        var allowedLogs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System",
            "Application",
            "Security"
        };
        if (!allowedLogs.Contains(normalizedLogName))
        {
            normalizedLogName = "System";
        }

        var safeLogName = EscapeSingleQuotes(normalizedLogName);
        
        var level = entryType.ToLowerInvariant() switch
        {
            "error" => "2",
            "warning" => "3",
            "information" => "4",
            _ => ""
        };

        var entryFilter = entryType.ToLowerInvariant() switch
        {
            "error" => "Error",
            "warning" => "Warning",
            "information" => "Information",
            _ => ""
        };

        var primaryCommand = string.IsNullOrEmpty(level)
            ? $@"
                Get-WinEvent -LogName '{safeLogName}' -MaxEvents {count} -ErrorAction Stop 2>$null |
                    Select-Object TimeCreated, LevelDisplayName, ProviderName, Id, Message |
                    Format-Table -AutoSize -Wrap
            "
            : $@"
                Get-WinEvent -FilterHashtable @{{ LogName = '{safeLogName}'; Level = {level} }} -MaxEvents {count} -ErrorAction Stop 2>$null |
                    Select-Object TimeCreated, LevelDisplayName, ProviderName, Id, Message |
                    Format-Table -AutoSize -Wrap
            ";

        var fallbackCommand = string.IsNullOrEmpty(entryFilter)
            ? $@"
                Get-EventLog -LogName '{safeLogName}' -Newest {count} -ErrorAction Stop 2>$null |
                    Select-Object TimeGenerated, EntryType, Source, EventID, Message |
                    Format-Table -AutoSize -Wrap
            "
            : $@"
                Get-EventLog -LogName '{safeLogName}' -EntryType {entryFilter} -Newest {count} -ErrorAction Stop 2>$null |
                    Select-Object TimeGenerated, EntryType, Source, EventID, Message |
                    Format-Table -AutoSize -Wrap
            ";
        
        var wrappedCommand = WrapCommandWithTargetVerification(primaryCommand);
        var displayCommand = string.IsNullOrEmpty(entryFilter)
            ? $"Get-WinEvent -LogName '{normalizedLogName}' -MaxEvents {count}"
            : $"Get-WinEvent -LogName '{normalizedLogName}' -Level {entryType} -MaxEvents {count}";
        var target = _executor.ActualComputerName ?? _targetServer;
        var primaryApproval = await EnsureCommandApprovedAsync(_executor, target, displayCommand, displayCommand);
        if (!primaryApproval.ShouldExecute)
        {
            return primaryApproval.TerminalOutput!;
        }

        ConsoleUI.ShowCommandExecution(displayCommand, _targetServer);
        var result = await _executor.ExecuteAsync(wrappedCommand);
        if (result.Success && string.IsNullOrWhiteSpace(result.Error) && !string.IsNullOrWhiteSpace(result.Output) && !IsWarnOutput(result.Output))
        {
            LogCommandAction(target, displayCommand, result.Output, primaryApproval.ApprovalState);
            return result.Output;
        }

        var fallbackDisplayCommand = string.IsNullOrEmpty(entryFilter)
            ? $"Get-EventLog -LogName '{normalizedLogName}' -Newest {count}"
            : $"Get-EventLog -LogName '{normalizedLogName}' -EntryType {entryType} -Newest {count}";
        var fallbackApproval = await EnsureCommandApprovedAsync(_executor, target, fallbackDisplayCommand, fallbackDisplayCommand);
        if (!fallbackApproval.ShouldExecute)
        {
            return fallbackApproval.TerminalOutput!;
        }

        var wrappedFallback = WrapCommandWithTargetVerification(fallbackCommand);
        var fallbackResult = await _executor.ExecuteAsync(wrappedFallback);
        var fallbackOutput = fallbackResult.Success && string.IsNullOrWhiteSpace(fallbackResult.Error) && !string.IsNullOrWhiteSpace(fallbackResult.Output)
            ? fallbackResult.Output
            : "[WARN] Event log data unavailable.";
        LogCommandAction(target, fallbackDisplayCommand, fallbackOutput, fallbackApproval.ApprovalState);
        return fallbackOutput;
    }

    /// <summary>
    /// Get Windows services status
    /// </summary>
    private async Task<string> GetServicesAsync(
        [Description("Filter by service status: Running, Stopped, or All")] string status = "All",
        [Description("Search filter for service name (supports wildcards)")] string? nameFilter = null)
    {
        var stateFilter = status.ToLowerInvariant() switch
        {
            "running" => "Running",
            "stopped" => "Stopped",
            _ => ""
        };

        var nameFilterValue = !string.IsNullOrWhiteSpace(nameFilter)
            ? nameFilter.Trim()
            : string.Empty;
        var safeNameFilterValue = EscapeSingleQuotes(nameFilterValue);

        var command = $@"
            try {{
                if (-not (Get-Command Get-Service -ErrorAction SilentlyContinue)) {{
                    throw 'Get-Service not available'
                }}
                $ErrorActionPreference = 'Stop'
                $services = Get-Service -ErrorAction Stop 2>$null
                if ('{stateFilter}' -ne '') {{
                    $services = $services | Where-Object {{ $_.Status -eq '{stateFilter}' }}
                }}
                if ('{safeNameFilterValue}' -ne '') {{
                    $services = $services | Where-Object {{ $_.Name -like '*{safeNameFilterValue}*' -or $_.DisplayName -like '*{safeNameFilterValue}*' }}
                }}
                $services |
                    Select-Object Status, Name, DisplayName, StartType |
                    Sort-Object Status, Name |
                    Format-Table -AutoSize
            }} catch {{
                $services = Get-CimInstance -ClassName Win32_Service -ErrorAction SilentlyContinue
                if ('{stateFilter}' -ne '') {{
                    $services = $services | Where-Object {{ $_.State -eq '{stateFilter}' }}
                }}
                if ('{safeNameFilterValue}' -ne '') {{
                    $services = $services | Where-Object {{ $_.Name -like '*{safeNameFilterValue}*' -or $_.DisplayName -like '*{safeNameFilterValue}*' }}
                }}
                $services |
                    Select-Object State, Name, DisplayName, StartMode |
                    Sort-Object State, Name |
                    Format-Table -AutoSize
            }}
        ";
        
        var wrappedCommand = WrapCommandWithTargetVerification(command);
        var displayCommand = string.IsNullOrWhiteSpace(nameFilterValue)
            ? (string.IsNullOrWhiteSpace(stateFilter)
                ? "Get-Service"
                : $"Get-Service | Where-Object Status -eq '{stateFilter}'")
            : $"Get-Service -Name '*{nameFilterValue}*'";
        var target = _executor.ActualComputerName ?? _targetServer;
        var approval = await EnsureCommandApprovedAsync(_executor, target, displayCommand, displayCommand);
        if (!approval.ShouldExecute)
        {
            return approval.TerminalOutput!;
        }

        ConsoleUI.ShowCommandExecution(displayCommand, _targetServer);
        var result = await _executor.ExecuteAsync(wrappedCommand);
        var output = result.Success ? result.Output : $"[ERROR] {result.Error}";
        LogCommandAction(target, displayCommand, output, approval.ApprovalState);
        return output;
    }

    /// <summary>
    /// Get running processes
    /// </summary>
    private async Task<string> GetProcessesAsync(
        [Description("Filter by process name (supports wildcards)")] string? nameFilter = null,
        [Description("Sort by: CPU, Memory, or Name")] string sortBy = "Memory",
        [Description("Number of top processes to show")] int top = 20)
    {
        top = Math.Min(Math.Max(top, 1), 100);
        
        var nameClause = !string.IsNullOrEmpty(nameFilter) 
            ? $" -Name '*{nameFilter}*'" 
            : "";

        var sortProperty = sortBy.ToLowerInvariant() switch
        {
            "cpu" => "CPU",
            "memory" => "WorkingSet64",
            _ => "ProcessName"
        };

        var command = $@"
            Get-Process{nameClause} | 
            Sort-Object {sortProperty} -Descending |
            Select-Object -First {top} ProcessName, Id, 
                @{{N='CPU(s)';E={{[math]::Round($_.CPU, 2)}}}},
                @{{N='Memory(MB)';E={{[math]::Round($_.WorkingSet64 / 1MB, 2)}}}},
                @{{N='Handles';E={{$_.HandleCount}}}},
                @{{N='Threads';E={{$_.Threads.Count}}}} |
            Format-Table -AutoSize
        ";
        
        var wrappedCommand = WrapCommandWithTargetVerification(command);
        var displayCommand = $"Get-Process -Top {top} -SortBy {sortProperty}";
        var target = _executor.ActualComputerName ?? _targetServer;
        var approval = await EnsureCommandApprovedAsync(_executor, target, displayCommand, displayCommand);
        if (!approval.ShouldExecute)
        {
            return approval.TerminalOutput!;
        }

        ConsoleUI.ShowCommandExecution(displayCommand, _targetServer);
        var result = await _executor.ExecuteAsync(wrappedCommand);
        var output = result.Success ? result.Output : $"[ERROR] {result.Error}";
        LogCommandAction(target, displayCommand, output, approval.ApprovalState);
        return output;
    }

    /// <summary>
    /// Get disk space information
    /// </summary>
    private async Task<string> GetDiskSpaceAsync()
    {
        var primaryCommand = @"
            Get-Volume -ErrorAction Stop 2>$null | Where-Object { $_.DriveLetter } |
                Select-Object DriveLetter, FileSystemLabel, FileSystem,
                    @{N='SizeGB';E={[math]::Round($_.Size / 1GB, 2)}},
                    @{N='FreeGB';E={[math]::Round($_.SizeRemaining / 1GB, 2)}},
                    @{N='UsedGB';E={[math]::Round(($_.Size - $_.SizeRemaining) / 1GB, 2)}},
                    @{N='PercentFree';E={[math]::Round(($_.SizeRemaining / $_.Size) * 100, 1)}},
                    HealthStatus, OperationalStatus |
                Format-Table -AutoSize
        ";

        var cmdletFallback = @"
            Get-PSDrive -PSProvider FileSystem |
                Select-Object Name,
                    @{N='UsedGB';E={[math]::Round($_.Used / 1GB, 2)}},
                    @{N='FreeGB';E={[math]::Round($_.Free / 1GB, 2)}} |
                Format-Table -AutoSize
        ";

        var cimFallback = @"
            Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType=3' |
                Select-Object DeviceID,
                    @{N='SizeGB';E={[math]::Round($_.Size / 1GB, 2)}},
                    @{N='FreeGB';E={[math]::Round($_.FreeSpace / 1GB, 2)}},
                    @{N='UsedGB';E={[math]::Round(($_.Size - $_.FreeSpace) / 1GB, 2)}},
                    @{N='PercentFree';E={[math]::Round(($_.FreeSpace / $_.Size) * 100, 1)}} |
                Format-Table -AutoSize
        ";
        
        var wrappedCommand = WrapCommandWithTargetVerification(primaryCommand);
        const string displayCommand = "Get-Volume";
        var target = _executor.ActualComputerName ?? _targetServer;
        var primaryApproval = await EnsureCommandApprovedAsync(_executor, target, displayCommand, displayCommand);
        if (!primaryApproval.ShouldExecute)
        {
            return primaryApproval.TerminalOutput!;
        }

        ConsoleUI.ShowCommandExecution(displayCommand, _targetServer);
        var result = await _executor.ExecuteAsync(wrappedCommand);
        if (result.Success && string.IsNullOrWhiteSpace(result.Error) && !string.IsNullOrWhiteSpace(result.Output) && !IsWarnOutput(result.Output))
        {
            LogCommandAction(target, displayCommand, result.Output, primaryApproval.ApprovalState);
            return result.Output;
        }

        const string cmdletFallbackDisplayCommand = "Get-PSDrive -PSProvider FileSystem";
        var cmdletFallbackApproval = await EnsureCommandApprovedAsync(_executor, target, cmdletFallbackDisplayCommand, cmdletFallbackDisplayCommand);
        if (!cmdletFallbackApproval.ShouldExecute)
        {
            return cmdletFallbackApproval.TerminalOutput!;
        }

        var wrappedCmdletFallback = WrapCommandWithTargetVerification(cmdletFallback);
        var cmdletResult = await _executor.ExecuteAsync(wrappedCmdletFallback);
        if (cmdletResult.Success && string.IsNullOrWhiteSpace(cmdletResult.Error) && !string.IsNullOrWhiteSpace(cmdletResult.Output))
        {
            LogCommandAction(target, cmdletFallbackDisplayCommand, cmdletResult.Output, cmdletFallbackApproval.ApprovalState);
            return cmdletResult.Output;
        }

        const string cimFallbackDisplayCommand = "Get-CimInstance Win32_LogicalDisk";
        var cimFallbackApproval = await EnsureCommandApprovedAsync(_executor, target, cimFallbackDisplayCommand, cimFallbackDisplayCommand);
        if (!cimFallbackApproval.ShouldExecute)
        {
            return cimFallbackApproval.TerminalOutput!;
        }

        var wrappedCimFallback = WrapCommandWithTargetVerification(cimFallback);
        var cimResult = await _executor.ExecuteAsync(wrappedCimFallback);
        var cimOutput = cimResult.Success && string.IsNullOrWhiteSpace(cimResult.Error)
            ? cimResult.Output
            : $"[ERROR] {cimResult.Error}";
        LogCommandAction(target, cimFallbackDisplayCommand, cimOutput, cimFallbackApproval.ApprovalState);
        return cimOutput;
    }

    /// <summary>
    /// Get network adapter information
    /// </summary>
    private async Task<string> GetNetworkInfoAsync()
    {
        var command = @"
            try {
                if (-not (Get-Command Get-NetAdapter -ErrorAction SilentlyContinue)) {
                    throw 'Get-NetAdapter not available'
                }
                $ErrorActionPreference = 'Stop'
                Get-NetAdapter -ErrorAction Stop 2>$null | Where-Object { $_.Status -eq 'Up' } |
                    ForEach-Object {
                        $adapter = $_
                        $ipConfig = Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
                        $dns = Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
                        
                        [PSCustomObject]@{
                            Name = $adapter.Name
                            Status = $adapter.Status
                            LinkSpeed = $adapter.LinkSpeed
                            MacAddress = $adapter.MacAddress
                            IPAddress = ($ipConfig.IPAddress -join ', ')
                            SubnetPrefix = ($ipConfig.PrefixLength -join ', ')
                            DNSServers = ($dns.ServerAddresses -join ', ')
                        }
                    } | Format-List
            } catch {
                Get-CimInstance -ClassName Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=TRUE' |
                    Select-Object Description, MACAddress,
                        @{N='IPAddress';E={($_.IPAddress -join ', ')}},
                        @{N='Subnet';E={($_.IPSubnet -join ', ')}},
                        @{N='DefaultGateway';E={($_.DefaultIPGateway -join ', ')}},
                        @{N='DNSServers';E={($_.DNSServerSearchOrder -join ', ')}} |
                    Format-List
            }
        ";
        
        var wrappedCommand = WrapCommandWithTargetVerification(command);
        const string displayCommand = "Get-NetAdapter";
        var target = _executor.ActualComputerName ?? _targetServer;
        var approval = await EnsureCommandApprovedAsync(_executor, target, displayCommand, displayCommand);
        if (!approval.ShouldExecute)
        {
            return approval.TerminalOutput!;
        }

        ConsoleUI.ShowCommandExecution(displayCommand, _targetServer);
        var result = await _executor.ExecuteAsync(wrappedCommand);
        var output = result.Success ? result.Output : $"[ERROR] {result.Error}";
        LogCommandAction(target, displayCommand, output, approval.ApprovalState);
        return output;
    }

    /// <summary>
    /// Get performance counter values
    /// </summary>
    private async Task<string> GetPerformanceCountersAsync(
        [Description("Category: CPU, Memory, Disk, Network, or All")] string category = "All")
    {
        var counters = category.ToLowerInvariant() switch
        {
            "cpu" => @"'\Processor(_Total)\% Processor Time'",
            "memory" => @"'\Memory\Available MBytes', '\Memory\% Committed Bytes In Use'",
            "disk" => @"'\PhysicalDisk(_Total)\% Disk Time', '\PhysicalDisk(_Total)\Avg. Disk Queue Length'",
            "network" => @"'\Network Interface(*)\Bytes Total/sec'",
            _ => @"'\Processor(_Total)\% Processor Time', '\Memory\Available MBytes', '\Memory\% Committed Bytes In Use', '\PhysicalDisk(_Total)\% Disk Time'"
        };

        var primaryCommand = $@"
            $samples = Get-Counter -Counter {counters} -ErrorAction Stop 2>$null |
                Select-Object -ExpandProperty CounterSamples |
                Select-Object Path, @{{N='Value';E={{[math]::Round($_.CookedValue, 2)}}}}
            if (-not $samples) {{
                Write-Output '[WARN] Performance counter data unavailable.'
            }} else {{
                $samples | Format-Table -AutoSize
            }}
        ";
        
        var wrappedCommand = WrapCommandWithTargetVerification(primaryCommand);
        var displayCommand = $"Get-Counter ({category})";
        var target = _executor.ActualComputerName ?? _targetServer;
        var primaryApproval = await EnsureCommandApprovedAsync(_executor, target, displayCommand, displayCommand);
        if (!primaryApproval.ShouldExecute)
        {
            return primaryApproval.TerminalOutput!;
        }

        ConsoleUI.ShowCommandExecution(displayCommand, _targetServer);
        var result = await _executor.ExecuteAsync(wrappedCommand);
        if (result.Success && string.IsNullOrWhiteSpace(result.Error) && !string.IsNullOrWhiteSpace(result.Output) && !IsWarnOutput(result.Output))
        {
            LogCommandAction(target, displayCommand, result.Output, primaryApproval.ApprovalState);
            return result.Output;
        }

        var fallbackCommand = category.ToLowerInvariant() switch
        {
            "cpu" => @"
                Get-CimInstance -ClassName Win32_PerfFormattedData_PerfOS_Processor |
                    Where-Object { $_.Name -eq '_Total' } |
                    Select-Object Name, @{N='CPUPercent';E={$_.PercentProcessorTime}} |
                    Format-Table -AutoSize
            ",
            "memory" => @"
                $os = Get-CimInstance -ClassName Win32_OperatingSystem
                [PSCustomObject]@{
                    FreeMemoryMB = [math]::Round($os.FreePhysicalMemory / 1024, 2)
                    TotalMemoryMB = [math]::Round($os.TotalVisibleMemorySize / 1024, 2)
                    CommittedPercent = [math]::Round((($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / $os.TotalVisibleMemorySize) * 100, 1)
                } | Format-List
            ",
            "disk" => @"
                Get-CimInstance -ClassName Win32_PerfFormattedData_PerfDisk_PhysicalDisk |
                    Where-Object { $_.Name -eq '_Total' } |
                    Select-Object Name, PercentDiskTime, AvgDiskQueueLength |
                    Format-Table -AutoSize
            ",
            "network" => @"
                Get-CimInstance -ClassName Win32_PerfFormattedData_Tcpip_NetworkInterface |
                    Select-Object Name, BytesTotalPersec |
                    Sort-Object BytesTotalPersec -Descending |
                    Select-Object -First 5 |
                    Format-Table -AutoSize
            ",
            _ => @"
                $cpu = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfOS_Processor | Where-Object { $_.Name -eq '_Total' }
                $disk = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfDisk_PhysicalDisk | Where-Object { $_.Name -eq '_Total' }
                $os = Get-CimInstance -ClassName Win32_OperatingSystem
                [PSCustomObject]@{
                    CPUPercent = $cpu.PercentProcessorTime
                    DiskPercentTime = $disk.PercentDiskTime
                    DiskQueueLength = $disk.AvgDiskQueueLength
                    FreeMemoryMB = [math]::Round($os.FreePhysicalMemory / 1024, 2)
                    TotalMemoryMB = [math]::Round($os.TotalVisibleMemorySize / 1024, 2)
                } | Format-List
            "
        };

        var fallbackDisplayCommand = $"Performance fallback ({category})";
        var fallbackValidationCommand = "Get-CimInstance";
        var fallbackApproval = await EnsureCommandApprovedAsync(_executor, target, fallbackDisplayCommand, fallbackValidationCommand);
        if (!fallbackApproval.ShouldExecute)
        {
            return fallbackApproval.TerminalOutput!;
        }

        var wrappedFallback = WrapCommandWithTargetVerification(fallbackCommand);
        var fallbackResult = await _executor.ExecuteAsync(wrappedFallback);
        var fallbackOutput = fallbackResult.Success && string.IsNullOrWhiteSpace(fallbackResult.Error) && !string.IsNullOrWhiteSpace(fallbackResult.Output)
            ? fallbackResult.Output
            : "[WARN] Performance counter data unavailable.";
        LogCommandAction(target, fallbackDisplayCommand, fallbackOutput, fallbackApproval.ApprovalState);
        return fallbackOutput;
    }

    /// <summary>
    /// Clear pending commands after they've been processed
    /// </summary>
    public void ClearPendingCommands()
    {
        _pendingCommands.Clear();
    }

    /// <summary>
    /// Execute a pending command after user approval
    /// </summary>
    public async Task<string> ExecuteApprovedCommandAsync(PendingCommand command)
    {
        var executor = command.Executor ?? _executor;
        var serverName = command.ServerName;
        var wrappedCommand = WrapCommandWithTargetVerification(command.Command, executor, serverName);
        executor.AddHistoryEntry($"[EXECUTED AFTER APPROVAL] {command.Command}");
        var displayTarget = serverName ?? _targetServer;
        ConsoleUI.ShowCommandExecution(command.Command, displayTarget);
        var result = await executor.ExecuteAsync(wrappedCommand, trackInHistory: false);
        _pendingCommands.Remove(command);
        var target = executor.ActualComputerName ?? displayTarget;
        var prefix = serverName != null ? $"[{serverName}] " : string.Empty;

        if (!result.Success)
        {
            _actionLogger?.Invoke(new CommandActionLog(
                DateTimeOffset.Now,
                target,
                command.Command,
                $"[ERROR] {result.Error ?? "Unknown error occurred"}",
                CommandApprovalState.ApprovedByUser));
            return $"{prefix}[ERROR] {result.Error ?? "Unknown error occurred"}";
        }

        var output = string.IsNullOrWhiteSpace(result.Output)
            ? "[OK] Command completed with no output."
            : result.Output;

        _actionLogger?.Invoke(new CommandActionLog(
            DateTimeOffset.Now,
            target,
            command.Command,
            output,
            CommandApprovalState.ApprovedByUser));

        return $"{prefix}{output}";
    }

    public void LogDeniedCommand(PendingCommand command)
    {
        _actionLogger?.Invoke(new CommandActionLog(
            DateTimeOffset.Now,
            _executor.ActualComputerName ?? _targetServer,
            command.Command,
            "User denied approval; command was not executed.",
            CommandApprovalState.Denied));
    }
}

/// <summary>
/// Represents a command pending user approval
/// </summary>
public sealed record PendingCommand(string Command, string Reason, PowerShellExecutor? Executor = null, string? ServerName = null, string? Intent = null);

public enum CommandApprovalState
{
    SafeAuto,
    ApprovalRequested,
    ApprovedByUser,
    AutoApprovedYolo,
    Denied,
    Blocked
}

public record CommandActionLog(
    DateTimeOffset Timestamp,
    string Target,
    string Command,
    string Output,
    CommandApprovalState ApprovalState);
