using GitHub.Copilot.SDK;
using TroubleScout.UI;

namespace TroubleScout.Services;

internal sealed class SessionInitializationRequest
{
    internal required Func<bool> IsInitialized { get; init; }
    internal required Action MarkInitialized { get; init; }
    internal required string TargetServer { get; init; }
    internal required PowerShellExecutor Executor { get; init; }
    internal required IReadOnlyList<string> AdditionalInitialServers { get; init; }
    internal required (string ServerName, string ConfigurationName)? InitialJeaSession { get; init; }
    internal required ServerConnectionManager ServerManager { get; init; }
    internal required bool DebugMode { get; init; }
    internal required bool ByokExplicitlyRequested { get; init; }
    internal required bool ModelExplicitlyRequested { get; init; }
    internal required string OpenAiApiKeyEnvironmentVariable { get; init; }
    internal required string? RequestedModel { get; init; }
    internal required Func<string?> GetSelectedModel { get; init; }
    internal required Func<bool> GetUseByokOpenAi { get; init; }
    internal required Action<bool> SetUseByokOpenAi { get; init; }
    internal required Func<string?> GetByokOpenAiApiKey { get; init; }
    internal required Action<CopilotClient?> SetCopilotClient { get; init; }
    internal required Action<bool> SetGitHubAuthenticated { get; init; }
    internal required Action<SystemMessageConfig> SetSystemMessage { get; init; }
    internal required Func<string, IReadOnlyCollection<string>?, SystemMessageConfig> CreateSystemMessage { get; init; }
    internal required Func<string, bool, Task<(bool Success, string? Error)>> ConnectAdditionalServer { get; init; }
    internal required Func<string, string, bool, Task<(bool Success, string? Error)>> ConnectJeaServer { get; init; }
    internal required Func<Task> WarnIfPowerShellVersionIsOld { get; init; }
    internal required Func<Task<bool>> IsGitHubAuthenticated { get; init; }
    internal required Func<string?, Action<string>?, Task<bool>> CreateCopilotSession { get; init; }
    internal required Func<string?, Task<List<ModelInfo>>> GetMergedModelList { get; init; }
    internal required Func<IReadOnlyList<ModelInfo>, string?> ResolveInitialSessionModel { get; init; }
    internal required Func<Task> RefreshAvailableModels { get; init; }
    internal required ModelDiscoveryManager ModelDiscovery { get; init; }
    internal required List<string> ConfigurationWarnings { get; init; }
}

internal static class SessionInitializationCoordinator
{
    internal static async Task<bool> InitializeAsync(
        SessionInitializationRequest request,
        Action<string>? updateStatus,
        bool allowInteractiveSetup)
    {
        if (request.IsInitialized())
        {
            return true;
        }

        var copilotInitializationStarted = false;

        try
        {
            updateStatus?.Invoke($"Connecting to {request.TargetServer}...");

            var (connectionSuccess, connectionError) = await request.Executor.TestConnectionAsync();
            if (!connectionSuccess)
            {
                ConsoleUI.ShowError("Connection Failed", connectionError ?? $"Unable to connect to {request.TargetServer}");
                return false;
            }

            updateStatus?.Invoke($"Connected to {request.Executor.ActualComputerName}...");

            foreach (var additionalServer in request.AdditionalInitialServers)
            {
                if (additionalServer.Equals(request.TargetServer, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                updateStatus?.Invoke($"Connecting to {additionalServer}...");
                var (addSuccess, addError) = await request.ConnectAdditionalServer(additionalServer, true);
                if (!addSuccess)
                {
                    request.ConfigurationWarnings.Add($"Could not connect to additional server '{additionalServer}': {addError}");
                    if (request.DebugMode)
                    {
                        ConsoleUI.ShowWarning($"Additional server '{additionalServer}' failed: {addError}");
                    }
                }
            }

            if (request.ServerManager.Executors.Count > 0)
            {
                request.SetSystemMessage(request.CreateSystemMessage(
                    request.TargetServer,
                    request.ServerManager.Executors.Keys.ToList()));
            }

            if (request.InitialJeaSession is { } initialJeaSession)
            {
                updateStatus?.Invoke($"Connecting to JEA endpoint {initialJeaSession.ConfigurationName} on {initialJeaSession.ServerName}...");
                var (jeaSuccess, jeaError) = await request.ConnectJeaServer(
                    initialJeaSession.ServerName,
                    initialJeaSession.ConfigurationName,
                    true);
                if (!jeaSuccess)
                {
                    ConsoleUI.ShowError(
                        "JEA Connection Failed",
                        jeaError ?? $"Unable to connect to JEA endpoint '{initialJeaSession.ConfigurationName}' on {initialJeaSession.ServerName}");
                    return false;
                }
            }

            await request.WarnIfPowerShellVersionIsOld();

            updateStatus?.Invoke("Starting Copilot SDK...");
            copilotInitializationStarted = true;

            var cliPath = CopilotCliResolver.TryResolvePreferredCopilotCliPath();
            var clientOptions = new CopilotClientOptions { LogLevel = "info" };
            if (!string.IsNullOrWhiteSpace(cliPath))
            {
                clientOptions.CliPath = cliPath;
            }

            var copilotClient = new CopilotClient(clientOptions);
            request.SetCopilotClient(copilotClient);

            try
            {
                await copilotClient.StartAsync();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("protocol version mismatch", StringComparison.OrdinalIgnoreCase))
            {
                var report = await CopilotCliResolver.ValidateCopilotPrerequisitesAsync();
                await SessionInitializationDiagnostics.ShowCopilotInitializationFailureAsync(
                    SessionInitializationDiagnostics.BuildProtocolMismatchMessage(report, request.DebugMode),
                    request.DebugMode,
                    ex,
                    includeDiagnostics: true);
                request.SetCopilotClient(null);
                return false;
            }
            catch (Exception)
            {
                try { await copilotClient.DisposeAsync(); } catch { /* best-effort */ }
                request.SetCopilotClient(null);
                throw;
            }

            request.SetGitHubAuthenticated(await request.IsGitHubAuthenticated());

            if (request.GetUseByokOpenAi())
            {
                if (string.IsNullOrWhiteSpace(request.GetByokOpenAiApiKey()))
                {
                    if (request.ByokExplicitlyRequested)
                    {
                        await SessionInitializationDiagnostics.ShowCopilotInitializationFailureAsync(
                            $"BYOK mode requires an OpenAI API key.\n\nSet {request.OpenAiApiKeyEnvironmentVariable} or pass --openai-api-key.",
                            request.DebugMode);
                        return false;
                    }

                    ConsoleUI.ShowWarning(
                        "BYOK is enabled in saved settings but no API key is available. Falling back to GitHub Copilot.\n" +
                        "Use /byok to configure OpenAI-compatible mode, or /model to switch provider.");
                    request.SetUseByokOpenAi(false);
                }
                else
                {
                    var byokModel = request.ModelExplicitlyRequested
                        ? request.RequestedModel
                        : (!string.IsNullOrWhiteSpace(request.GetSelectedModel()) ? request.GetSelectedModel() : request.RequestedModel);

                    if (!await request.CreateCopilotSession(byokModel, updateStatus))
                    {
                        return false;
                    }

                    await request.RefreshAvailableModels();
                    request.MarkInitialized();
                    return true;
                }
            }

            // Re-read auth state through the caller because BYOK fallback may continue into GitHub mode.
            var isGitHubAuthenticated = await request.IsGitHubAuthenticated();
            request.SetGitHubAuthenticated(isGitHubAuthenticated);
            if (!isGitHubAuthenticated)
            {
                if (allowInteractiveSetup)
                {
                    ConsoleUI.ShowWarning(
                        "GitHub Copilot is not authenticated. Use /login to sign in, or /byok to configure OpenAI-compatible BYOK.");
                    request.MarkInitialized();
                    return true;
                }

                await SessionInitializationDiagnostics.ShowCopilotInitializationFailureAsync(
                    "Copilot CLI is installed but not authenticated.\n\nTo continue:\n  1. Run: copilot login\n  2. Re-run TroubleScout",
                    request.DebugMode,
                    includeDiagnostics: true);
                return false;
            }

            updateStatus?.Invoke("Fetching available models...");
            request.ModelDiscovery.AvailableModels = await request.GetMergedModelList(cliPath);

            if (request.ModelDiscovery.AvailableModels.Count == 0)
            {
                await SessionInitializationDiagnostics.ShowCopilotInitializationFailureAsync(
                    "No models were returned by Copilot CLI. Ensure you are authenticated and your subscription has model access.",
                    request.DebugMode,
                    includeDiagnostics: true);
                return false;
            }

            var effectiveModel = request.ResolveInitialSessionModel(request.ModelDiscovery.AvailableModels);
            if (!string.IsNullOrWhiteSpace(request.RequestedModel)
                && !string.Equals(effectiveModel, request.RequestedModel, StringComparison.OrdinalIgnoreCase)
                && request.ModelDiscovery.AvailableModels.All(m => m.Id != request.RequestedModel))
            {
                if (request.ModelExplicitlyRequested)
                {
                    ConsoleUI.ShowError("Invalid Model", $"The requested model '{request.RequestedModel}' is not available.");
                    return false;
                }

                ConsoleUI.ShowWarning($"Saved model '{request.RequestedModel}' is not available with the current provider. Using '{effectiveModel}'.\nUse /model to select a different one.");
            }

            if (!await request.CreateCopilotSession(effectiveModel, updateStatus))
            {
                if (allowInteractiveSetup)
                {
                    ConsoleUI.ShowWarning("AI session is not ready. Use /login or /byok to set up authentication, then continue.");
                    request.MarkInitialized();
                    return true;
                }

                return false;
            }

            await request.RefreshAvailableModels();
            request.MarkInitialized();
            return true;
        }
        catch (Exception ex)
        {
            if (copilotInitializationStarted)
            {
                var report = await CopilotCliResolver.ValidateCopilotPrerequisitesAsync();
                if (allowInteractiveSetup)
                {
                    ConsoleUI.ShowWarning(SessionInitializationDiagnostics.BuildActionableInitializationMessage(ex, report, request.DebugMode));
                    request.MarkInitialized();
                    return true;
                }

                await SessionInitializationDiagnostics.ShowCopilotInitializationFailureAsync(
                    SessionInitializationDiagnostics.BuildActionableInitializationMessage(ex, report, request.DebugMode),
                    request.DebugMode,
                    ex,
                    includeDiagnostics: true);
                return false;
            }

            ConsoleUI.ShowError("Initialization Failed", "TroubleScout could not complete startup.");
            if (request.DebugMode)
            {
                ConsoleUI.ShowWarning($"Technical details: {SessionInitializationDiagnostics.TrimSingleLine(ex.Message)}");
            }
            return false;
        }
    }
}
