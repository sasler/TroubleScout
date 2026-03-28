using System.Management.Automation;
using System.Management.Automation.Language;
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
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan NoOutputCommandTimeout = TimeSpan.FromSeconds(30);
    private readonly string _targetServer;
    private readonly bool _useLocalExecution;
    private readonly string? _configurationName;
    private Runspace? _runspace;
    private bool _disposed;
    private string? _actualComputerName;
    private readonly List<string> _commandHistory = new();
    private readonly object _historyLock = new();
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private ExecutionMode _executionMode = ExecutionMode.Safe;
    private IReadOnlyList<string>? _customSafeCommands;
    private HashSet<string>? _jeaAllowedCommands;

    /// <summary>
    /// The target server name that was requested
    /// </summary>
    public string TargetServer => _targetServer;

    /// <summary>
    /// The actual computer name where commands are executing (verified during connection)
    /// </summary>
    public virtual string? ActualComputerName => _actualComputerName;
    public bool IsJeaSession => _configurationName != null;
    public string? ConfigurationName => _configurationName;
    public IReadOnlySet<string>? JeaAllowedCommands => _jeaAllowedCommands;

    public ExecutionMode ExecutionMode
    {
        get => _executionMode;
        set => _executionMode = value;
    }

    internal TimeSpan? CommandTimeoutOverride { get; set; }

    /// <summary>
    /// Gets a snapshot of PowerShell commands executed in this session
    /// </summary>
    public IReadOnlyList<string> GetCommandHistory()
    {
        lock (_historyLock)
        {
            return _commandHistory.ToList();
        }
    }

    public void AddHistoryEntry(string entry)
    {
        TrackCommand(entry);
    }

    public PowerShellExecutor(string targetServer)
    {
        _targetServer = targetServer;
        _useLocalExecution = IsLocalhostName(targetServer);
    }

    public PowerShellExecutor(string targetServer, string configurationName) : this(targetServer)
    {
        _configurationName = configurationName;
    }

    public void SetCustomSafeCommands(IReadOnlyList<string>? commands)
    {
        _customSafeCommands = commands?.Where(command => !string.IsNullOrWhiteSpace(command))
            .Select(command => command.Trim())
            .ToList();
    }

    internal static CommandValidation ValidateStandaloneCommand(
        string command,
        ExecutionMode executionMode,
        IReadOnlyList<string>? safeCommands = null)
    {
        return CommandValidator.ValidateStandaloneCommand(command, executionMode, safeCommands);
    }

    internal static bool IsLocalhostName(string server)
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
    public virtual CommandValidation ValidateCommand(string command)
    {
        return CreateCommandValidator().ValidateCommand(command);
    }

    internal static bool MatchesSafeCommandPattern(string cmdletName, string pattern)
    {
        return CommandValidator.MatchesSafeCommandPattern(cmdletName, pattern);
    }

    private CommandValidator CreateCommandValidator() =>
        new(_executionMode, _customSafeCommands, _jeaAllowedCommands, _configurationName);

    /// <summary>
    /// Initializes the PowerShell runspace
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_runspace != null)
            return;

        await _initLock.WaitAsync();
        try
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

                    if (!string.IsNullOrEmpty(_configurationName))
                    {
                        connectionInfo.ShellUri = $"http://schemas.microsoft.com/powershell/{_configurationName}";
                    }

                    _runspace = RunspaceFactory.CreateRunspace(connectionInfo);
                    _runspace.Open();
                }
            });
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Executes a PowerShell command and returns the result
    /// </summary>
    public virtual async Task<PowerShellResult> ExecuteAsync(string command, bool trackInHistory = true)
    {
        if (_disposed)
        {
            return new PowerShellResult(false, string.Empty, "PowerShell executor has been disposed.");
        }

        if (_runspace == null)
        {
            await InitializeAsync();
        }

        if (trackInHistory)
        {
            TrackCommand(command);
        }

        await _executionLock.WaitAsync();
        try
        {
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
                    var timeout = CommandTimeoutOverride ?? GetCommandTimeout(command);

                    // JEA endpoints often use NoLanguage mode, so avoid AddScript/script blocks there.
                    if (IsJeaSession)
                    {
                        ConfigureJeaPipeline(ps, command);
                    }
                    else
                    {
                        // Serialize runspace usage to avoid concurrent pipeline execution.
                        var wrappedCommand = $@"
                        $ErrorActionPreference = 'Continue'
                        $ConfirmPreference = 'None'
                        $PSDefaultParameterValues['*:Confirm'] = $false
                        $currentComputer = $env:COMPUTERNAME
                        $__tsOutput = (& {{
{command}
                        }} | Out-String -Width 200)
                        if (-not [string]::IsNullOrWhiteSpace($__tsOutput)) {{
                            $__tsOutput.TrimEnd()
                        }}
                    ";

                        ps.AddScript(wrappedCommand);
                    }

                    var asyncResult = ps.BeginInvoke();
                    if (!asyncResult.AsyncWaitHandle.WaitOne(timeout))
                    {
                        try
                        {
                            ps.Stop();
                        }
                        catch
                        {
                            // Ignore stop failures during timeout handling.
                        }

                        return new PowerShellResult(
                            false,
                            string.Empty,
                            $"Command timed out after {timeout.TotalSeconds:0} seconds.");
                    }

                    var results = ps.EndInvoke(asyncResult);
                    var output = new StringBuilder();

                    foreach (var result in results.Where(result => result != null))
                    {
                        output.Append(result);
                    }

                    if (ps.HadErrors)
                    {
                        var errors = new StringBuilder();
                        foreach (var error in ps.Streams.Error)
                        {
                            errors.AppendLine(error.ToString());
                        }

                        var outputText = IsJeaSession ? output.ToString().TrimEnd() : output.ToString();
                        return new PowerShellResult(false, outputText, errors.ToString());
                    }

                    var finalOutput = IsJeaSession ? output.ToString().TrimEnd() : output.ToString();
                    return new PowerShellResult(true, finalOutput);
                }
                catch (Exception ex)
                {
                    return new PowerShellResult(false, string.Empty, ex.Message);
                }
            });
        }
        finally
        {
            _executionLock.Release();
        }
    }

    private void TrackCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        lock (_historyLock)
        {
            _commandHistory.Add(command.Trim());
        }
    }

    private TimeSpan GetCommandTimeout(string command)
    {
        return ExpectsNoOutput(command) ? NoOutputCommandTimeout : DefaultCommandTimeout;
    }

    private static bool ExpectsNoOutput(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var trimmed = command.Trim();
        if (trimmed.Contains("Out-Null", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("> $null", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("[void]", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var cmdletName = CommandValidator.ExtractCmdletName(trimmed);
        if (string.IsNullOrWhiteSpace(cmdletName))
        {
            return false;
        }

        return cmdletName.StartsWith("Set-", StringComparison.OrdinalIgnoreCase)
            || cmdletName.StartsWith("Start-", StringComparison.OrdinalIgnoreCase)
            || cmdletName.StartsWith("Stop-", StringComparison.OrdinalIgnoreCase)
            || cmdletName.StartsWith("Restart-", StringComparison.OrdinalIgnoreCase)
            || cmdletName.StartsWith("Remove-", StringComparison.OrdinalIgnoreCase)
            || cmdletName.StartsWith("New-", StringComparison.OrdinalIgnoreCase)
            || cmdletName.StartsWith("Clear-", StringComparison.OrdinalIgnoreCase)
            || cmdletName.StartsWith("Enable-", StringComparison.OrdinalIgnoreCase)
            || cmdletName.StartsWith("Disable-", StringComparison.OrdinalIgnoreCase)
            || cmdletName.StartsWith("Rename-", StringComparison.OrdinalIgnoreCase)
            || cmdletName.StartsWith("Move-", StringComparison.OrdinalIgnoreCase)
            || cmdletName.StartsWith("Add-", StringComparison.OrdinalIgnoreCase)
            || cmdletName.StartsWith("Install-", StringComparison.OrdinalIgnoreCase)
            || cmdletName.StartsWith("Uninstall-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tests the connection to the target server and verifies we're connected to the right machine
    /// </summary>
    public async Task<(bool Success, string? Error)> TestConnectionAsync()
    {
        try
        {
            await InitializeAsync();

            if (IsJeaSession)
            {
                _actualComputerName = _targetServer.Split('.')[0];
                return (true, null);
            }

            var result = await ExecuteAsync("$env:COMPUTERNAME", trackInHistory: false);
            
            if (!result.Success)
            {
                return (false, result.Error ?? "Failed to execute test command");
            }

            _actualComputerName = result.Output.Trim();
            
            // Fallback to Environment.MachineName if PowerShell didn't return a value
            if (string.IsNullOrWhiteSpace(_actualComputerName))
            {
                _actualComputerName = Environment.MachineName;
            }

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

    public async Task<IReadOnlySet<string>> DiscoverJeaCommandsAsync()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PowerShellExecutor));
        }

        if (_runspace == null)
        {
            await InitializeAsync();
        }

        await _executionLock.WaitAsync();
        try
        {
            if (_runspace!.RunspaceStateInfo.State != RunspaceState.Opened)
            {
                throw new InvalidOperationException($"Remote session to {_targetServer} has been disconnected. Please restart TroubleScout.");
            }

            var discoveredCommands = await Task.Run(() =>
            {
                using var ps = PowerShell.Create();
                ps.Runspace = _runspace;

                if (!TryBuildJeaPipeline(ps, "Get-Command", out var parseError))
                {
                    throw new InvalidOperationException(parseError);
                }

                var asyncResult = ps.BeginInvoke();
                var timeout = CommandTimeoutOverride ?? DefaultCommandTimeout;
                if (!asyncResult.AsyncWaitHandle.WaitOne(timeout))
                {
                    try
                    {
                        ps.Stop();
                    }
                    catch
                    {
                        // Ignore stop failures during timeout handling.
                    }

                    throw new InvalidOperationException($"JEA command discovery timed out after {timeout.TotalSeconds:0} seconds.");
                }

                var results = ps.EndInvoke(asyncResult);
                if (ps.HadErrors)
                {
                    var errors = new StringBuilder();
                    foreach (var error in ps.Streams.Error)
                    {
                        errors.AppendLine(error.ToString());
                    }

                    throw new InvalidOperationException(errors.ToString().Trim());
                }

                return results
                    .Select(result => result?.BaseObject switch
                    {
                        CommandInfo commandInfo => commandInfo.Name,
                        _ => result?.Properties["Name"]?.Value?.ToString()
                    })
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            });

            _jeaAllowedCommands = discoveredCommands;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to discover JEA commands: {ex.Message}", ex);
        }
        finally
        {
            _executionLock.Release();
        }

        return _jeaAllowedCommands;
    }

    internal void SetJeaAllowedCommandsForTesting(IEnumerable<string> commands)
    {
        _jeaAllowedCommands = commands
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .Select(command => command.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void ConfigureJeaPipeline(PowerShell ps, string command)
    {
        if (!TryBuildJeaPipeline(ps, command, out var error))
        {
            throw new InvalidOperationException(error);
        }

        if (_jeaAllowedCommands?.Contains("Out-String") == true)
        {
            ps.AddCommand("Out-String").AddParameter("Width", 200);
        }
    }

    internal static bool TryBuildJeaPipeline(PowerShell ps, string command, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(command))
        {
            error = "JEA command cannot be empty.";
            return false;
        }

        Token[] tokens;
        ParseError[] parseErrors;
        var ast = Parser.ParseInput(command, out tokens, out parseErrors);
        if (parseErrors.Length > 0)
        {
            error = parseErrors[0].Message;
            return false;
        }

        if (ast.EndBlock is null)
        {
            error = "JEA command could not be parsed.";
            return false;
        }

        var statements = ast.EndBlock.Statements;
        if (statements.Count != 1 || statements[0] is not PipelineAst pipelineAst)
        {
            error = "JEA commands must be a single pipeline or command.";
            return false;
        }

        if (pipelineAst.PipelineElements.Count == 0)
        {
            error = "JEA command did not contain any pipeline elements.";
            return false;
        }

        if (pipelineAst.Background)
        {
            error = "JEA commands do not support background execution.";
            return false;
        }

        for (var commandIndex = 0; commandIndex < pipelineAst.PipelineElements.Count; commandIndex++)
        {
            if (pipelineAst.PipelineElements[commandIndex] is not CommandAst commandAst)
            {
                error = "JEA commands only support command pipelines.";
                return false;
            }

            var commandName = commandAst.GetCommandName();
            if (string.IsNullOrWhiteSpace(commandName))
            {
                error = "JEA commands must start each pipeline segment with a cmdlet name.";
                return false;
            }

            if (commandAst.Redirections.Count > 0)
            {
                error = "JEA commands do not support redirection.";
                return false;
            }

            ps.AddCommand(commandName);

            var elements = commandAst.CommandElements;
            for (var elementIndex = 1; elementIndex < elements.Count; elementIndex++)
            {
                var element = elements[elementIndex];

                if (element is CommandParameterAst parameterAst)
                {
                    Ast? parameterArgument = parameterAst.Argument;
                    if (parameterArgument is null &&
                        elementIndex + 1 < elements.Count &&
                        elements[elementIndex + 1] is not CommandParameterAst)
                    {
                        parameterArgument = elements[++elementIndex];
                    }

                    if (parameterArgument is null)
                    {
                        ps.AddParameter(parameterAst.ParameterName);
                        continue;
                    }

                    if (!TryGetJeaArgumentValue(parameterArgument, out var parameterValue, out error))
                    {
                        error = $"Unsupported JEA parameter value for -{parameterAst.ParameterName}: {error}";
                        return false;
                    }

                    ps.AddParameter(parameterAst.ParameterName, parameterValue);
                    continue;
                }

                if (!TryGetJeaArgumentValue(element, out var argumentValue, out error))
                {
                    error = $"Unsupported JEA argument '{element.Extent.Text}': {error}";
                    return false;
                }

                ps.AddArgument(argumentValue);
            }
        }

        return true;
    }

    private static bool TryGetJeaArgumentValue(Ast ast, out object value, out string error)
    {
        error = string.Empty;
        value = string.Empty;

        switch (ast)
        {
            case StringConstantExpressionAst stringAst:
                value = stringAst.Value;
                return true;

            case ConstantExpressionAst constantAst:
                value = constantAst.Value?.ToString() ?? string.Empty;
                return true;
        }

        error = "language-mode expressions are not supported in JEA commands.";
        return false;
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
        _executionLock.Dispose();
        _initLock.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
