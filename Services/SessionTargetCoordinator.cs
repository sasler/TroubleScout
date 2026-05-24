using GitHub.Copilot;
using TroubleScout.Tools;
using TroubleScout.UI;

namespace TroubleScout.Services;

internal sealed class SessionTargetRequest
{
    internal required Func<string> GetTargetServer { get; init; }
    internal required Action<string> SetTargetServer { get; init; }
    internal required Func<PowerShellExecutor> GetExecutor { get; init; }
    internal required Action<PowerShellExecutor> SetExecutor { get; init; }
    internal required ServerConnectionManager ServerManager { get; init; }
    internal required Func<bool> GetStartupJeaFocusActive { get; init; }
    internal required Action<bool> SetStartupJeaFocusActive { get; init; }
    internal required Func<string> GetEffectiveTargetServer { get; init; }
    internal required Func<SystemMessageConfig> RefreshSystemMessage { get; init; }
    internal required Action<SystemMessageConfig> SetSystemMessage { get; init; }
    internal required ExecutionMode ExecutionMode { get; init; }
    internal required IReadOnlyList<string>? ConfiguredSafeCommands { get; init; }
    internal required Func<CopilotClient?> GetCopilotClient { get; init; }
    internal required Func<CopilotSession?> GetCopilotSession { get; init; }
    internal required Action<CopilotSession?> SetCopilotSession { get; init; }
    internal required Func<string?> GetSelectedModel { get; init; }
    internal required Func<string?, Action<string>?, Task<bool>> CreateCopilotSession { get; init; }
    internal required Func<DiagnosticTools> CreateDiagnosticTools { get; init; }
    internal required Action<DiagnosticTools> SetDiagnosticTools { get; init; }
}

internal static class SessionTargetCoordinator
{
    internal static async Task<bool> ReconnectAsync(
        string newServer,
        SessionTargetRequest request,
        Action<string>? updateStatus = null)
    {
        if (string.IsNullOrWhiteSpace(newServer))
        {
            ConsoleUI.ShowWarning("Server name cannot be empty");
            return false;
        }

        newServer = newServer.Trim();
        var currentTarget = request.GetTargetServer();

        if (newServer.Equals(currentTarget, StringComparison.OrdinalIgnoreCase))
        {
            if (request.GetStartupJeaFocusActive()
                && currentTarget.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && !request.GetEffectiveTargetServer().Equals(currentTarget, StringComparison.OrdinalIgnoreCase))
            {
                request.SetStartupJeaFocusActive(false);
                request.SetSystemMessage(request.RefreshSystemMessage());

                if (request.GetCopilotClient() != null && request.GetCopilotSession() != null)
                {
                    updateStatus?.Invoke("Refreshing AI session...");
                    await request.GetCopilotSession()!.DisposeAsync();
                    request.SetCopilotSession(null);

                    var modelToUse = string.IsNullOrWhiteSpace(request.GetSelectedModel()) ? null : request.GetSelectedModel();
                    if (!await request.CreateCopilotSession(modelToUse, updateStatus))
                    {
                        return false;
                    }
                }

                ConsoleUI.ShowInfo("Primary focus reset to localhost.");
                return true;
            }

            ConsoleUI.ShowInfo($"Already connected to {newServer}");
            return true;
        }

        updateStatus?.Invoke("Closing current PowerShell session...");
        request.GetExecutor().Dispose();

        request.SetStartupJeaFocusActive(false);
        request.SetTargetServer(newServer);
        request.SetSystemMessage(request.RefreshSystemMessage());
        var executor = new PowerShellExecutor(newServer)
        {
            ExecutionMode = request.ExecutionMode
        };
        executor.SetCustomSafeCommands(request.ConfiguredSafeCommands);
        request.SetExecutor(executor);
        request.SetDiagnosticTools(request.CreateDiagnosticTools());

        updateStatus?.Invoke($"Connecting to {newServer}...");
        var (connectionSuccess, connectionError) = await executor.TestConnectionAsync();
        if (!connectionSuccess)
        {
            ConsoleUI.ShowError("Connection Failed", connectionError ?? $"Unable to connect to {newServer}");
            return false;
        }

        if (request.GetCopilotClient() != null)
        {
            if (request.GetCopilotSession() != null)
            {
                updateStatus?.Invoke("Closing AI session...");
                await request.GetCopilotSession()!.DisposeAsync();
                request.SetCopilotSession(null);
            }

            var modelToUse = string.IsNullOrWhiteSpace(request.GetSelectedModel()) ? null : request.GetSelectedModel();
            if (!await request.CreateCopilotSession(modelToUse, updateStatus))
            {
                return false;
            }
        }

        return true;
    }

    internal static async Task<(bool Success, string? Error)> ConnectAdditionalServerAsync(
        string serverName,
        SessionTargetRequest request,
        bool skipApproval = false)
    {
        var result = await request.ServerManager.ConnectAdditionalServerAsync(
            serverName,
            request.GetTargetServer(),
            request.ExecutionMode,
            request.ConfiguredSafeCommands,
            skipApproval);
        if (result.Success)
        {
            request.SetSystemMessage(request.RefreshSystemMessage());
        }

        return result;
    }

    internal static async Task<(bool Success, string? Error)> ConnectJeaServerAsync(
        string serverName,
        string configurationName,
        SessionTargetRequest request,
        bool skipApproval = false)
    {
        var result = await request.ServerManager.ConnectJeaServerAsync(
            serverName,
            configurationName,
            request.GetTargetServer(),
            request.ExecutionMode,
            request.ConfiguredSafeCommands,
            skipApproval);
        if (result.Success)
        {
            request.SetSystemMessage(request.RefreshSystemMessage());
        }

        return result;
    }

    internal static async Task<bool> CloseAdditionalServerSessionAsync(
        string serverName,
        SessionTargetRequest request)
    {
        var closed = await request.ServerManager.CloseAdditionalServerSessionAsync(serverName);
        if (closed)
        {
            request.SetSystemMessage(request.RefreshSystemMessage());
        }

        return closed;
    }
}
