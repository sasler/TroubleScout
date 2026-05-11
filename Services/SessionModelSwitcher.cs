using GitHub.Copilot.SDK;
using TroubleScout.UI;

namespace TroubleScout.Services;

internal sealed class SessionModelSwitchRequest
{
    internal required CopilotClient? CopilotClient { get; init; }
    internal required ModelDiscoveryManager ModelDiscovery { get; init; }
    internal required Func<string, ModelSource> ResolveTargetSource { get; init; }
    internal required Func<bool> IsByokConfigured { get; init; }
    internal required Func<bool> IsGitHubCopilotAuthenticated { get; init; }
    internal required Action<bool> SetUseByokOpenAi { get; init; }
    internal required Func<Task> DisposeCurrentSession { get; init; }
    internal required Func<string?, Action<string>?, Task<bool>> CreateCopilotSession { get; init; }
}

internal static class SessionModelSwitcher
{
    internal static async Task<bool> ChangeModelAsync(
        string newModel,
        SessionModelSwitchRequest request,
        Action<string>? updateStatus = null)
    {
        var targetSource = request.ResolveTargetSource(newModel);
        return await ChangeModelAsync(newModel, targetSource, request, updateStatus);
    }

    internal static async Task<bool> ChangeModelAsync(
        ModelSelectionEntry entry,
        SessionModelSwitchRequest request,
        Action<string>? updateStatus = null)
        => await ChangeModelAsync(entry.ModelId, entry.Source, request, updateStatus);

    private static async Task<bool> ChangeModelAsync(
        string modelId,
        ModelSource targetSource,
        SessionModelSwitchRequest request,
        Action<string>? updateStatus)
    {
        if (request.CopilotClient == null)
        {
            ConsoleUI.ShowError("Not Connected", "Copilot client not initialized");
            return false;
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            ConsoleUI.ShowError("Invalid Model", "Model cannot be empty.");
            return false;
        }

        if (request.ModelDiscovery.AvailableModels.Count == 0
            || request.ModelDiscovery.AvailableModels.All(m => !m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase)))
        {
            ConsoleUI.ShowError("Invalid Model", $"The selected model '{modelId}' is not available.");
            return false;
        }

        if (targetSource == ModelSource.None)
        {
            ConsoleUI.ShowError("Invalid Model", $"Could not determine provider for model '{modelId}'.");
            return false;
        }

        if (targetSource == ModelSource.Byok)
        {
            if (!request.IsByokConfigured())
            {
                ConsoleUI.ShowWarning("BYOK is not configured. Run /byok first to use BYOK models.");
                return false;
            }

            request.SetUseByokOpenAi(true);
        }
        else
        {
            if (!request.IsGitHubCopilotAuthenticated())
            {
                ConsoleUI.ShowWarning("GitHub Copilot is not authenticated. Run /login to use GitHub models.");
                return false;
            }

            request.SetUseByokOpenAi(false);
        }

        try
        {
            updateStatus?.Invoke("Closing current session...");
            await request.DisposeCurrentSession();
            return await request.CreateCopilotSession(modelId, updateStatus);
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowError("Model Change Failed", ex.Message);
            return false;
        }
    }
}
