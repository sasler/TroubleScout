using System.Diagnostics;
using System.Text.Json;
using GitHub.Copilot.SDK;
using TroubleScout.UI;

namespace TroubleScout.Services;

internal sealed class ByokProviderManager
{
    internal bool UseByokOpenAi { get; set; }
    internal bool ExplicitlyRequested { get; set; }
    internal string BaseUrl { get; set; } = string.Empty;
    internal string? ApiKey { get; set; }

    internal async Task<bool> ConfigureByokOpenAiAsync(
        string baseUrl,
        string apiKey,
        string? model,
        CopilotClient? copilotClient,
        Func<Task> disposeCurrentSessionAsync,
        Func<Task<List<ModelInfo>>> tryGetByokProviderModelsAsync,
        Func<string?, Action<string>?, Task<bool>> createCopilotSessionAsync,
        Action<bool, string?, string?> saveByokSettings,
        string openAiApiKeyEnvironmentVariable,
        Action<string>? updateStatus)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ConsoleUI.ShowWarning($"OpenAI API key is required. Set {openAiApiKeyEnvironmentVariable} or pass /byok <api-key> [base-url] [model].");
            return false;
        }

        if (!LooksLikeUrl(baseUrl))
        {
            ConsoleUI.ShowWarning("OpenAI-compatible base URL is required. Example: https://api.openai.com/v1");
            return false;
        }

        UseByokOpenAi = true;
        BaseUrl = baseUrl.Trim();
        ApiKey = apiKey.Trim();

        if (copilotClient == null)
        {
            ConsoleUI.ShowWarning("Copilot client is not ready. Restart TroubleScout and try /byok again.");
            return false;
        }

        await disposeCurrentSessionAsync();

        updateStatus?.Invoke("Fetching models from OpenAI-compatible endpoint...");
        if (updateStatus == null)
        {
            ConsoleUI.ShowInfo("Fetching models from OpenAI-compatible endpoint...");
        }

        var discoveredModels = await tryGetByokProviderModelsAsync();
        if (discoveredModels.Count > 0)
        {
            var preferredModel = !string.IsNullOrWhiteSpace(model) && discoveredModels.Any(item => item.Id.Equals(model, StringComparison.OrdinalIgnoreCase))
                ? model
                : discoveredModels[0].Id;

            var selectedModel = ConsoleUI.PromptModelSelection(preferredModel, discoveredModels);
            if (string.IsNullOrWhiteSpace(selectedModel))
            {
                ConsoleUI.ShowWarning("BYOK model selection was canceled.");
                return false;
            }

            model = selectedModel;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                ConsoleUI.ShowInfo("Could not fetch models from the OpenAI-compatible endpoint.");
                ConsoleUI.ShowInfo("Enter model ID to continue with BYOK:");
                model = ConsoleUI.GetUserInput().Trim();
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                ConsoleUI.ShowWarning("A model ID is required when provider model discovery is unavailable.");
                return false;
            }
        }

        var created = await createCopilotSessionAsync(model, updateStatus);
        if (created)
        {
            saveByokSettings(true, BaseUrl, ApiKey);
        }

        return created;
    }

    internal async Task<bool> LoginAndCreateGitHubSessionAsync(
        CopilotClient? copilotClient,
        string? selectedModel,
        Func<Task> disposeCurrentSessionAsync,
        Func<string?, Task<List<ModelInfo>>> getMergedModelListAsync,
        Func<string?, Action<string>?, Task<bool>> createCopilotSessionAsync,
        Func<Task> refreshAvailableModelsAsync,
        Action<bool> setGitHubCopilotAuthenticated,
        Action<string>? updateStatus)
    {
        if (copilotClient == null)
        {
            ConsoleUI.ShowWarning("Copilot client is not ready. Restart TroubleScout and try again.");
            return false;
        }

        var cliPath = CopilotCliResolver.GetCopilotCliPath();
        var (command, args) = CopilotCliResolver.BuildCopilotCommand(cliPath, "login");

        updateStatus?.Invoke("Launching authentication flow...");

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                UseShellExecute = true,
                CreateNoWindow = false
            });

            if (process == null)
            {
                ConsoleUI.ShowWarning("Could not start copilot login process.");
                return false;
            }

            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowWarning($"Failed to run login flow: {TrimSingleLine(ex.Message)}");
            return false;
        }

        var authStatus = await copilotClient.GetAuthStatusAsync();
        if (!authStatus.IsAuthenticated)
        {
            setGitHubCopilotAuthenticated(false);
            ConsoleUI.ShowWarning("Login did not complete. Try /login again, then verify your browser/device flow finished.");
            return false;
        }

        setGitHubCopilotAuthenticated(true);
        await disposeCurrentSessionAsync();

        updateStatus?.Invoke("Creating authenticated AI session...");

        var githubModels = await getMergedModelListAsync(CopilotCliResolver.GetCopilotCliPath());
        if (githubModels.Count == 0)
        {
            ConsoleUI.ShowWarning("No GitHub Copilot models are currently available after login.");
            return false;
        }

        UseByokOpenAi = false;
        var modelToUse = !string.IsNullOrWhiteSpace(selectedModel)
            && githubModels.Any(model => model.Id.Equals(selectedModel, StringComparison.OrdinalIgnoreCase))
                ? selectedModel
                : githubModels[0].Id;

        var created = await createCopilotSessionAsync(modelToUse, updateStatus);
        if (created)
        {
            await refreshAvailableModelsAsync();
        }

        return created;
    }

    internal async Task<bool> IsGitHubAuthenticatedAsync(CopilotClient? copilotClient)
    {
        if (copilotClient == null)
        {
            return false;
        }

        try
        {
            var authStatus = await copilotClient.GetAuthStatusAsync();
            return authStatus.IsAuthenticated;
        }
        catch
        {
            return false;
        }
    }

    internal IReadOnlyList<string> BuildByokModelEndpointCandidates(string baseUrl)
    {
        var normalizedBaseUrl = baseUrl.Trim().TrimEnd('/');
        var candidates = new List<string>
        {
            normalizedBaseUrl + "/models"
        };

        if (!normalizedBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(normalizedBaseUrl + "/v1/models");
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal (List<ModelInfo> Models, Dictionary<string, ByokPriceInfo> PricingByModelId) ParseByokModelsResponse(JsonElement rootElement)
    {
        var parsed = ModelDiscoveryManager.ParseByokModelsResponse(rootElement);
        return (parsed.Models, parsed.PricingByModelId);
    }

    internal bool IsNonChatApiMode(string mode)
    {
        return mode.Equals("image_generation", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("embedding", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("audio_transcription", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("audio_speech", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("completion", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("moderation", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("rerank", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("video_generation", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("realtime", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("ocr", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                   || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private static string TrimSingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }
}
