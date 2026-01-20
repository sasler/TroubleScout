using System.ComponentModel;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using TroubleScout.Services;

namespace TroubleScout.Tools;

/// <summary>
/// Provides diagnostic tools for the Copilot AI agent to gather Windows Server information
/// </summary>
public class DiagnosticTools
{
    private readonly PowerShellExecutor _executor;
    private readonly Func<string, string, Task<bool>> _approvalCallback;
    private readonly List<PendingCommand> _pendingCommands = [];

    public IReadOnlyList<PendingCommand> PendingCommands => _pendingCommands.AsReadOnly();

    public DiagnosticTools(PowerShellExecutor executor, Func<string, string, Task<bool>> approvalCallback)
    {
        _executor = executor;
        _approvalCallback = approvalCallback;
    }

    /// <summary>
    /// Creates AI tools for the Copilot session
    /// </summary>
    public IEnumerable<AIFunction> GetTools()
    {
        yield return AIFunctionFactory.Create(RunPowerShellCommandAsync,
            "run_powershell",
            "Execute a PowerShell command on the target Windows server. Only Get-* commands run automatically; other commands require user approval.");

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
    }

    /// <summary>
    /// Run an arbitrary PowerShell command with validation
    /// </summary>
    private async Task<string> RunPowerShellCommandAsync(
        [Description("The PowerShell command to execute")] string command)
    {
        var validation = _executor.ValidateCommand(command);

        if (!validation.IsAllowed && !validation.RequiresApproval)
        {
            return $"[BLOCKED] {validation.Reason}";
        }

        if (validation.RequiresApproval)
        {
            // Add to pending commands for user approval
            var pending = new PendingCommand(command, validation.Reason ?? "Requires user approval");
            _pendingCommands.Add(pending);

            return $"[PENDING APPROVAL] Command '{command}' requires user approval before execution. " +
                   $"Reason: {validation.Reason}. " +
                   "The command has been queued and will be shown to the user for approval.";
        }

        // Safe command - execute directly
        var result = await _executor.ExecuteAsync(command);

        if (!result.Success)
        {
            return $"[ERROR] {result.Error ?? "Unknown error occurred"}";
        }

        return string.IsNullOrWhiteSpace(result.Output) 
            ? "[OK] Command completed with no output." 
            : result.Output;
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
        return await RunPowerShellCommandAsync(command);
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
        
        var typeFilter = entryType.ToLowerInvariant() switch
        {
            "error" => " -EntryType Error",
            "warning" => " -EntryType Warning",
            "information" => " -EntryType Information",
            _ => ""
        };

        var command = $@"
            Get-EventLog -LogName {logName}{typeFilter} -Newest {count} | 
            Select-Object TimeGenerated, EntryType, Source, EventID, Message |
            Format-Table -AutoSize -Wrap
        ";
        
        return await RunPowerShellCommandAsync(command);
    }

    /// <summary>
    /// Get Windows services status
    /// </summary>
    private async Task<string> GetServicesAsync(
        [Description("Filter by service status: Running, Stopped, or All")] string status = "All",
        [Description("Search filter for service name (supports wildcards)")] string? nameFilter = null)
    {
        var whereClause = status.ToLowerInvariant() switch
        {
            "running" => " | Where-Object { $_.Status -eq 'Running' }",
            "stopped" => " | Where-Object { $_.Status -eq 'Stopped' }",
            _ => ""
        };

        var nameClause = !string.IsNullOrEmpty(nameFilter) 
            ? $" -Name '*{nameFilter}*'" 
            : "";

        var command = $@"
            Get-Service{nameClause}{whereClause} | 
            Select-Object Status, Name, DisplayName, StartType |
            Sort-Object Status, Name |
            Format-Table -AutoSize
        ";
        
        return await RunPowerShellCommandAsync(command);
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
        
        return await RunPowerShellCommandAsync(command);
    }

    /// <summary>
    /// Get disk space information
    /// </summary>
    private async Task<string> GetDiskSpaceAsync()
    {
        var command = @"
            Get-Volume | Where-Object { $_.DriveLetter } |
            Select-Object DriveLetter, FileSystemLabel, FileSystem,
                @{N='SizeGB';E={[math]::Round($_.Size / 1GB, 2)}},
                @{N='FreeGB';E={[math]::Round($_.SizeRemaining / 1GB, 2)}},
                @{N='UsedGB';E={[math]::Round(($_.Size - $_.SizeRemaining) / 1GB, 2)}},
                @{N='PercentFree';E={[math]::Round(($_.SizeRemaining / $_.Size) * 100, 1)}},
                HealthStatus, OperationalStatus |
            Format-Table -AutoSize
        ";
        
        return await RunPowerShellCommandAsync(command);
    }

    /// <summary>
    /// Get network adapter information
    /// </summary>
    private async Task<string> GetNetworkInfoAsync()
    {
        var command = @"
            Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } |
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
        ";
        
        return await RunPowerShellCommandAsync(command);
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

        var command = $@"
            Get-Counter -Counter {counters} -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty CounterSamples |
            Select-Object Path, @{{N='Value';E={{[math]::Round($_.CookedValue, 2)}}}} |
            Format-Table -AutoSize
        ";
        
        return await RunPowerShellCommandAsync(command);
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
        var result = await _executor.ExecuteAsync(command.Command);
        _pendingCommands.Remove(command);

        if (!result.Success)
        {
            return $"[ERROR] {result.Error ?? "Unknown error occurred"}";
        }

        return string.IsNullOrWhiteSpace(result.Output) 
            ? "[OK] Command completed with no output." 
            : result.Output;
    }
}

/// <summary>
/// Represents a command pending user approval
/// </summary>
public record PendingCommand(string Command, string Reason);
