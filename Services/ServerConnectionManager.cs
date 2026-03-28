using TroubleScout.UI;

namespace TroubleScout.Services;

internal sealed class ServerConnectionManager
{
    private readonly Dictionary<string, PowerShellExecutor> _executors = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyDictionary<string, PowerShellExecutor> Executors => _executors;

    internal async Task<(bool Success, string? Error)> ConnectAdditionalServerAsync(
        string serverName,
        string targetServer,
        ExecutionMode executionMode,
        IReadOnlyList<string>? configuredSafeCommands,
        bool skipApproval = false)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return (false, "Server name cannot be empty.");
        }

        if (serverName.Equals(targetServer, StringComparison.OrdinalIgnoreCase))
        {
            return (true, null);
        }

        if (_executors.ContainsKey(serverName))
        {
            return (true, null);
        }

        if (!skipApproval && executionMode == ExecutionMode.Safe)
        {
            var approval = ConsoleUI.PromptCommandApproval(
                $"New-PSSession -ComputerName '{serverName}'",
                $"TroubleScout wants to establish a direct PowerShell session to {serverName}");
            if (approval != ApprovalResult.Approved)
            {
                return (false, $"Connection to {serverName} was denied by user.");
            }
        }

        var executor = new PowerShellExecutor(serverName)
        {
            ExecutionMode = executionMode
        };
        executor.SetCustomSafeCommands(configuredSafeCommands);

        var (success, error) = await executor.TestConnectionAsync();
        if (!success)
        {
            executor.Dispose();
            return (false, error ?? $"Failed to connect to {serverName}");
        }

        _executors[serverName] = executor;
        return (true, null);
    }

    internal async Task<(bool Success, string? Error)> ConnectJeaServerAsync(
        string serverName,
        string configurationName,
        string targetServer,
        ExecutionMode executionMode,
        IReadOnlyList<string>? configuredSafeCommands,
        bool skipApproval = false)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return (false, "Server name cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(configurationName))
        {
            return (false, "Configuration name cannot be empty.");
        }

        serverName = serverName.Trim();
        configurationName = configurationName.Trim();

        if (serverName.Equals(targetServer, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "The primary target server cannot be replaced with a JEA session. Use /server first if you want to change the primary target.");
        }

        if (PowerShellExecutor.IsLocalhostName(serverName))
        {
            return (false, "JEA connections require a remote server. Use a remote hostname, not localhost.");
        }

        if (_executors.TryGetValue(serverName, out var existingExecutor))
        {
            if (existingExecutor.IsJeaSession &&
                string.Equals(existingExecutor.ConfigurationName, configurationName, StringComparison.OrdinalIgnoreCase))
            {
                return (true, null);
            }

            return (false, $"A session named '{serverName}' is already connected. Close it before connecting a different JEA configuration.");
        }

        if (!skipApproval && executionMode == ExecutionMode.Safe)
        {
            var approval = ConsoleUI.PromptCommandApproval(
                $"New-PSSession -ComputerName '{serverName}' -ConfigurationName '{configurationName}'",
                $"TroubleScout wants to establish a constrained JEA session to {serverName} using configuration {configurationName}");
            if (approval != ApprovalResult.Approved)
            {
                return (false, $"JEA connection to {serverName} was denied by user.");
            }
        }

        var executor = new PowerShellExecutor(serverName, configurationName)
        {
            ExecutionMode = executionMode
        };
        executor.SetCustomSafeCommands(configuredSafeCommands);

        try
        {
            var (success, error) = await executor.TestConnectionAsync();
            if (!success)
            {
                executor.Dispose();
                return (false, error ?? $"Failed to connect to JEA endpoint '{configurationName}' on {serverName}");
            }

            await executor.DiscoverJeaCommandsAsync();
            _executors[serverName] = executor;
            return (true, null);
        }
        catch (Exception ex)
        {
            executor.Dispose();
            return (false, ex.Message);
        }
    }

    internal PowerShellExecutor? GetExecutorForServer(string serverName, string targetServer, PowerShellExecutor primaryExecutor)
    {
        if (string.IsNullOrWhiteSpace(serverName) ||
            serverName.Equals(targetServer, StringComparison.OrdinalIgnoreCase))
        {
            return primaryExecutor;
        }

        _executors.TryGetValue(serverName, out var executor);
        return executor;
    }

    internal Task<bool> CloseAdditionalServerSessionAsync(string serverName)
    {
        if (!_executors.TryGetValue(serverName, out var executor))
        {
            return Task.FromResult(false);
        }

        _executors.Remove(serverName);
        try
        {
            executor.Dispose();
        }
        catch
        {
            // Best-effort disposal.
        }

        return Task.FromResult(true);
    }

    internal void ApplySafeCommands(IReadOnlyList<string>? configuredSafeCommands)
    {
        foreach (var executor in _executors.Values)
        {
            executor.SetCustomSafeCommands(configuredSafeCommands);
        }
    }

    internal void SetExecutionMode(ExecutionMode executionMode)
    {
        foreach (var executor in _executors.Values)
        {
            executor.ExecutionMode = executionMode;
        }
    }

    internal void DisposeAllExecutors(Action<Exception>? onDisposeError = null)
    {
        foreach (var executor in _executors.Values)
        {
            try
            {
                executor.Dispose();
            }
            catch (Exception ex)
            {
                onDisposeError?.Invoke(ex);
            }
        }

        _executors.Clear();
    }
}
