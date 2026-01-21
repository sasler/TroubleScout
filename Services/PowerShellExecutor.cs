using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;

namespace TroubleScout.Services;

/// <summary>
/// Result of a PowerShell command execution
/// </summary>
public record PowerShellResult(
    bool Success,
    string Output,
    string? Error = null
);

/// <summary>
/// Result of command validation
/// </summary>
public record CommandValidation(
    bool IsAllowed,
    bool RequiresApproval,
    string? Reason = null
);

/// <summary>
/// Executes PowerShell commands locally or remotely via WinRM
/// </summary>
public class PowerShellExecutor : IDisposable
{
    private readonly string _targetServer;
    private readonly bool _useLocalExecution;
    private Runspace? _runspace;
    private bool _disposed;
    private string? _actualComputerName;

    /// <summary>
    /// The target server name that was requested
    /// </summary>
    public string TargetServer => _targetServer;

    /// <summary>
    /// The actual computer name where commands are executing (verified during connection)
    /// </summary>
    public string? ActualComputerName => _actualComputerName;

    /// <summary>
    /// Commands that are allowed to run automatically (read-only Get-* commands)
    /// </summary>
    private static readonly HashSet<string> SafeCommandPrefixes =
    [
        "Get-"
    ];

    /// <summary>
    /// Explicitly blocked commands even if they start with Get-
    /// </summary>
    private static readonly HashSet<string> BlockedCommands =
    [
        "Get-Credential",
        "Get-Secret"
    ];

    public PowerShellExecutor(string targetServer)
    {
        _targetServer = targetServer;
        _useLocalExecution = IsLocalhost(targetServer);
    }

    private static bool IsLocalhost(string server)
    {
        return string.IsNullOrEmpty(server) ||
               server.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               server.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               server.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
               server.Equals(".", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates a command and determines if it can be auto-executed or requires approval
    /// </summary>
    public CommandValidation ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new CommandValidation(false, false, "Command cannot be empty");
        }

        // Check if it's a multi-line script or simple command
        var isMultiStatement = command.Contains('\n') || command.Contains(';');
        
        if (isMultiStatement)
        {
            // For multi-statement scripts, check if ALL statements are read-only
            if (IsReadOnlyScript(command))
            {
                return new CommandValidation(true, false);
            }
            else
            {
                return new CommandValidation(true, true, "Script contains commands that can modify system state and requires approval");
            }
        }

        // Parse the command to get the cmdlet name
        var cmdletName = ExtractCmdletName(command);

        if (string.IsNullOrEmpty(cmdletName))
        {
            return new CommandValidation(false, true, "Could not parse command - requires approval");
        }

        // Check if explicitly blocked
        if (BlockedCommands.Contains(cmdletName, StringComparer.OrdinalIgnoreCase))
        {
            return new CommandValidation(false, false, $"Command '{cmdletName}' is blocked for security reasons");
        }

        // Check if it's a safe Get-* command
        if (SafeCommandPrefixes.Any(prefix => cmdletName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return new CommandValidation(true, false);
        }

        // All other commands require user approval
        return new CommandValidation(true, true, $"Command '{cmdletName}' is not a read-only command and requires user approval");
    }

    /// <summary>
    /// Extracts the cmdlet name from a command string
    /// </summary>
    private static string ExtractCmdletName(string command)
    {
        // Simple extraction - get the first word/cmdlet
        var trimmed = command.Trim();
        
        // Handle piped commands - get the first cmdlet
        var firstPart = trimmed.Split('|')[0].Trim();
        
        // Get the cmdlet name (first word)
        var parts = firstPart.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        
        return parts.Length > 0 ? parts[0] : string.Empty;
    }

    /// <summary>
    /// Check if a multi-statement script contains only safe read-only operations
    /// </summary>
    private bool IsReadOnlyScript(string command)
    {
        // Safe cmdlet prefixes (read-only operations)
        var safePrefixes = new[]
        {
            "Get-", "Select-", "Where-", "Sort-", "Group-", 
            "Measure-", "Test-", "ConvertTo-", "ConvertFrom-", "Compare-",
            "Find-", "Search-", "Resolve-", "Out-String", "Out-Null"
        };
        
        // Specific safe Format-* cmdlets (Format-Volume is NOT safe)
        var safeFormatCmdlets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Format-Custom", "Format-Hex", "Format-List", "Format-Table", "Format-Wide"
        };

        // Split by common statement separators
        var statements = command
            .Replace("\r\n", "\n")
            .Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s) && !s.StartsWith("#"));

        foreach (var statement in statements)
        {
            // Skip variable assignments, object declarations, and block delimiters
            if (statement.StartsWith("$") || statement.StartsWith("[") || statement.StartsWith("@") ||
                statement.StartsWith("{") || statement.StartsWith("}") || statement.StartsWith("(") ||
                statement.StartsWith(")"))
                continue;
            
            // Skip property assignments inside objects (PropertyName = Value)
            if (statement.Contains(" = ") && !statement.Contains("-"))
                continue;

            // Check each part of piped commands
            var pipeParts = statement.Split('|').Select(p => p.Trim());
            foreach (var part in pipeParts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                
                // Skip parts that are just block delimiters
                if (part.StartsWith("{") || part.StartsWith("}") || part == "{" || part == "}")
                    continue;
                
                var words = part.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;
                var cmdlet = words[0];
                
                // Skip if it's not a cmdlet (no dash, or starts with special chars)
                if (!cmdlet.Contains('-') || cmdlet.StartsWith("$") || cmdlet.StartsWith("["))
                    continue;

                // Check if it's a safe cmdlet prefix
                var isSafe = safePrefixes.Any(prefix => cmdlet.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                
                // Special handling for Format-* cmdlets
                if (!isSafe && cmdlet.StartsWith("Format-", StringComparison.OrdinalIgnoreCase))
                {
                    isSafe = safeFormatCmdlets.Contains(cmdlet);
                }
                
                // If not explicitly safe, the script requires approval
                if (!isSafe)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Initializes the PowerShell runspace
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_runspace != null)
            return;

        await Task.Run(() =>
        {
            if (_useLocalExecution)
            {
                // Local execution - create a local runspace
                _runspace = RunspaceFactory.CreateRunspace();
                _runspace.Open();
            }
            else
            {
                // Remote execution via WinRM with Windows integrated auth
                var uri = new Uri($"http://{_targetServer}:5985/WSMAN");
                var connectionInfo = new WSManConnectionInfo(uri)
                {
                    AuthenticationMechanism = AuthenticationMechanism.Default,
                    OperationTimeout = 4 * 60 * 1000, // 4 minutes
                    OpenTimeout = 60 * 1000 // 1 minute
                };

                _runspace = RunspaceFactory.CreateRunspace(connectionInfo);
                _runspace.Open();
            }
        });
    }

    /// <summary>
    /// Executes a PowerShell command and returns the result
    /// </summary>
    public async Task<PowerShellResult> ExecuteAsync(string command)
    {
        if (_runspace == null)
        {
            await InitializeAsync();
        }

        // Check if the runspace is still open
        if (_runspace!.RunspaceStateInfo.State != RunspaceState.Opened)
        {
            return new PowerShellResult(false, string.Empty, 
                $"Remote session to {_targetServer} has been disconnected. Please restart TroubleScout.");
        }

        return await Task.Run(() =>
        {
            try
            {
                using var ps = PowerShell.Create();
                ps.Runspace = _runspace;
                
                // Wrap command with Out-String to ensure text output, and verify target
                var wrappedCommand = $@"
                    $ErrorActionPreference = 'Continue'
                    $currentComputer = $env:COMPUTERNAME
                    {command} | Out-String -Width 200
                ";
                
                ps.AddScript(wrappedCommand);

                var results = ps.Invoke();
                var output = new StringBuilder();

                foreach (var result in results)
                {
                    if (result != null)
                    {
                        output.Append(result.ToString());
                    }
                }

                if (ps.HadErrors)
                {
                    var errors = new StringBuilder();
                    foreach (var error in ps.Streams.Error)
                    {
                        errors.AppendLine(error.ToString());
                    }
                    return new PowerShellResult(false, output.ToString(), errors.ToString());
                }

                return new PowerShellResult(true, output.ToString());
            }
            catch (Exception ex)
            {
                return new PowerShellResult(false, string.Empty, ex.Message);
            }
        });
    }

    /// <summary>
    /// Tests the connection to the target server and verifies we're connected to the right machine
    /// </summary>
    public async Task<(bool Success, string? Error)> TestConnectionAsync()
    {
        try
        {
            await InitializeAsync();
            var result = await ExecuteAsync("$env:COMPUTERNAME");
            
            if (!result.Success)
            {
                return (false, result.Error ?? "Failed to execute test command");
            }

            _actualComputerName = result.Output.Trim();

            // For remote connections, verify we're actually connected to the right server
            if (!_useLocalExecution)
            {
                // Extract the hostname from the target (remove domain if present)
                var expectedHost = _targetServer.Split('.')[0];
                
                if (!_actualComputerName.Equals(expectedHost, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, $"Target mismatch! Expected to connect to '{expectedHost}' but connected to '{_actualComputerName}'. " +
                                   "This may indicate a WinRM configuration issue or the remote session is not being established correctly.");
                }
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Gets the connection mode description
    /// </summary>
    public string GetConnectionMode()
    {
        if (_useLocalExecution)
        {
            return $"Local PowerShell ({_actualComputerName ?? Environment.MachineName})";
        }
        
        return _actualComputerName != null 
            ? $"WinRM to {_actualComputerName}" 
            : $"WinRM to {_targetServer} (not verified)";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _runspace?.Close();
        _runspace?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
