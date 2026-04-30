using System.Globalization;
using System.Text.Json;
using GitHub.Copilot.SDK;

namespace TroubleScout.Services;

internal enum ModelSource
{
    None = 0,
    GitHub = 1,
    Byok = 2
}

internal record ModelSelectionEntry(string ModelId, string DisplayName, ModelSource Source)
{
    public string ProviderLabel { get; init; } = string.Empty;
    public string RateLabel { get; init; } = "n/a";
    public string DetailSummary { get; init; } = string.Empty;
    public bool IsCurrent { get; init; }
}

internal sealed record ByokPriceInfo(decimal? InputPricePerMillionTokens, decimal? OutputPricePerMillionTokens, string? DisplayText);

internal sealed class ModelDiscoveryManager
{
    internal sealed record ByokModelDiscoveryResult(List<ModelInfo> Models, Dictionary<string, ByokPriceInfo> PricingByModelId);

    private List<ModelInfo> _availableModels = new();
    private readonly Dictionary<string, ModelSource> _modelSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ByokPriceInfo> _byokPricing = new(StringComparer.OrdinalIgnoreCase);

    private List<ModelInfo>? _cachedMergedModelList;
    private string? _cachedMergedModelListKey;
    private readonly object _mergedModelListCacheLock = new();

    internal List<ModelInfo> AvailableModels
    {
        get => _availableModels;
        set => _availableModels = value ?? [];
    }
    internal IReadOnlyDictionary<string, ModelSource> ModelSources => _modelSources;
    internal IReadOnlyDictionary<string, ByokPriceInfo> ByokPricing => _byokPricing;

    internal string? ResolveInitialSessionModel(string? requestedModel, IReadOnlyList<ModelInfo> availableModels)
    {
        if (!string.IsNullOrWhiteSpace(requestedModel)
            && availableModels.Any(model => model.Id.Equals(requestedModel, StringComparison.OrdinalIgnoreCase)))
        {
            return requestedModel;
        }

        return availableModels.FirstOrDefault()?.Id;
    }

    internal ModelSource ResolveTargetSource(string modelId, bool useByokOpenAi, bool isGitHubCopilotAuthenticated)
    {
        if (!_modelSources.TryGetValue(modelId, out var source))
        {
            return ModelSource.None;
        }

        if ((source & ModelSource.Byok) != 0 && (source & ModelSource.GitHub) != 0)
        {
            if (useByokOpenAi)
            {
                return ModelSource.Byok;
            }

            return isGitHubCopilotAuthenticated ? ModelSource.GitHub : ModelSource.Byok;
        }

        if ((source & ModelSource.Byok) != 0)
        {
            return ModelSource.Byok;
        }

        if ((source & ModelSource.GitHub) != 0)
        {
            return ModelSource.GitHub;
        }

        return ModelSource.None;
    }

    internal string? GetModelDisplayName(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var model = _availableModels.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        return model?.Name ?? modelId;
    }

    internal async Task<List<ModelInfo>> GetMergedModelListAsync(
        CopilotClient? copilotClient,
        string? cliPath,
        Func<string, Task<IReadOnlyList<string>>> tryGetCliModelIdsAsync)
    {
        if (copilotClient == null)
        {
            return [];
        }

        var cacheKey = cliPath ?? string.Empty;
        return await GetMergedModelListAsync(cacheKey, async () =>
        {
            var models = await copilotClient.ListModelsAsync();

            var existingIds = new HashSet<string>(
                models.Where(model => !string.IsNullOrWhiteSpace(model.Id)).Select(model => model.Id),
                StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(cliPath))
            {
                var cliModelIds = await tryGetCliModelIdsAsync(cliPath);
                foreach (var cliModelId in cliModelIds)
                {
                    if (existingIds.Contains(cliModelId))
                    {
                        continue;
                    }

                    models.Add(new ModelInfo
                    {
                        Id = cliModelId,
                        Name = ToModelDisplayName(cliModelId)
                    });
                    existingIds.Add(cliModelId);
                }
            }

            return models.ToList();
        });
    }

    internal async Task<List<ModelInfo>> GetMergedModelListAsync(
        string cacheKey,
        Func<Task<List<ModelInfo>>> fetchMergedListAsync)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        ArgumentNullException.ThrowIfNull(fetchMergedListAsync);

        lock (_mergedModelListCacheLock)
        {
            if (_cachedMergedModelList != null
                && string.Equals(_cachedMergedModelListKey, cacheKey, StringComparison.Ordinal))
            {
                return [.. _cachedMergedModelList];
            }
        }

        var fetched = await fetchMergedListAsync();
        var snapshot = fetched is null ? new List<ModelInfo>() : [.. fetched];

        lock (_mergedModelListCacheLock)
        {
            _cachedMergedModelList = [.. snapshot];
            _cachedMergedModelListKey = cacheKey;
        }

        return snapshot;
    }

    internal void InvalidateMergedModelListCache()
    {
        lock (_mergedModelListCacheLock)
        {
            _cachedMergedModelList = null;
            _cachedMergedModelListKey = null;
        }
    }

    internal async Task<List<ModelInfo>> TryGetGitHubProviderModelsAsync(
        CopilotClient? copilotClient,
        bool isGitHubCopilotAuthenticated,
        Func<string, Task<IReadOnlyList<string>>> tryGetCliModelIdsAsync)
    {
        if (copilotClient == null || !isGitHubCopilotAuthenticated)
        {
            return [];
        }

        try
        {
            return await GetMergedModelListAsync(copilotClient, CopilotCliResolver.GetCopilotCliPath(), tryGetCliModelIdsAsync);
        }
        catch
        {
            return [];
        }
    }

    internal void UpdateAvailableModels(IReadOnlyList<ModelInfo> githubModels, IReadOnlyList<ModelInfo> byokModels)
    {
        _modelSources.Clear();

        var byId = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in githubModels.Where(model => !string.IsNullOrWhiteSpace(model.Id)))
        {
            _modelSources[model.Id] = _modelSources.TryGetValue(model.Id, out var existing)
                ? existing | ModelSource.GitHub
                : ModelSource.GitHub;

            byId[model.Id] = new ModelInfo
            {
                Id = model.Id,
                Name = model.Name,
                Billing = model.Billing,
                Capabilities = model.Capabilities,
                Policy = model.Policy,
                SupportedReasoningEfforts = model.SupportedReasoningEfforts,
                DefaultReasoningEffort = model.DefaultReasoningEffort
            };
        }

        foreach (var model in byokModels.Where(model => !string.IsNullOrWhiteSpace(model.Id)))
        {
            _modelSources[model.Id] = _modelSources.TryGetValue(model.Id, out var existing)
                ? existing | ModelSource.Byok
                : ModelSource.Byok;

            if (!byId.TryGetValue(model.Id, out var existingModel))
            {
                var clonedModel = new ModelInfo
                {
                    Id = model.Id,
                    Name = model.Name,
                    Billing = model.Billing,
                    Policy = model.Policy,
                    DefaultReasoningEffort = model.DefaultReasoningEffort
                };

                if (model.Capabilities != null)
                {
                    clonedModel.Capabilities = model.Capabilities;
                }

                if (model.SupportedReasoningEfforts != null)
                {
                    clonedModel.SupportedReasoningEfforts = model.SupportedReasoningEfforts;
                }

                byId[model.Id] = clonedModel;
            }
            else
            {
                if (existingModel.Billing == null && model.Billing != null)
                {
                    existingModel.Billing = model.Billing;
                }

                if (string.IsNullOrWhiteSpace(existingModel.Name) && !string.IsNullOrWhiteSpace(model.Name))
                {
                    existingModel.Name = model.Name;
                }

                if (existingModel.Capabilities == null && model.Capabilities != null)
                {
                    existingModel.Capabilities = model.Capabilities;
                }

                if (existingModel.Policy == null && model.Policy != null)
                {
                    existingModel.Policy = model.Policy;
                }

                if ((existingModel.SupportedReasoningEfforts == null || existingModel.SupportedReasoningEfforts.Count == 0)
                    && model.SupportedReasoningEfforts is { Count: > 0 })
                {
                    existingModel.SupportedReasoningEfforts = model.SupportedReasoningEfforts;
                }

                if (string.IsNullOrWhiteSpace(existingModel.DefaultReasoningEffort)
                    && !string.IsNullOrWhiteSpace(model.DefaultReasoningEffort))
                {
                    existingModel.DefaultReasoningEffort = model.DefaultReasoningEffort;
                }
            }
        }

        _availableModels = byId.Values
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var model in _availableModels)
        {
            var sourceLabel = _modelSources.TryGetValue(model.Id, out var source)
                ? source switch
                {
                    ModelSource.GitHub => "GitHub",
                    ModelSource.Byok => "BYOK",
                    ModelSource.GitHub | ModelSource.Byok => "GitHub+BYOK",
                    _ => "Unknown"
                }
                : "Unknown";

            model.Name = $"{ToModelDisplayName(model.Id)} [{sourceLabel}]";
        }
    }

    internal IReadOnlyList<ModelSelectionEntry> GetModelSelectionEntries(
        bool isGitHubCopilotAuthenticated,
        bool isByokConfigured,
        Func<ModelInfo, string, ModelSource, ModelSelectionEntry> buildModelSelectionEntry)
    {
        var entries = new List<ModelSelectionEntry>();

        foreach (var model in _availableModels)
        {
            if (!_modelSources.TryGetValue(model.Id, out var source))
            {
                continue;
            }

            var displayBase = ToModelDisplayName(model.Id);

            if ((source & ModelSource.GitHub) != 0 && (source & ModelSource.Byok) != 0)
            {
                if (isGitHubCopilotAuthenticated)
                {
                    entries.Add(buildModelSelectionEntry(model, displayBase, ModelSource.GitHub));
                }

                if (isByokConfigured)
                {
                    entries.Add(buildModelSelectionEntry(model, displayBase, ModelSource.Byok));
                }
            }
            else if ((source & ModelSource.GitHub) != 0)
            {
                if (isGitHubCopilotAuthenticated)
                {
                    entries.Add(buildModelSelectionEntry(model, displayBase, ModelSource.GitHub));
                }
            }
            else if ((source & ModelSource.Byok) != 0)
            {
                if (isByokConfigured)
                {
                    entries.Add(buildModelSelectionEntry(model, displayBase, ModelSource.Byok));
                }
            }
        }

        return entries;
    }

    internal ModelSelectionEntry BuildModelSelectionEntry(
        ModelInfo model,
        string displayBase,
        ModelSource source,
        Func<ModelInfo, ModelSource, string> getModelRateLabel,
        Func<ModelInfo, ModelSource, string> buildModelDetailSummary,
        Func<string, ModelSource, bool> isCurrentModelAndSource)
    {
        var providerLabel = source == ModelSource.Byok ? "BYOK / OpenAI" : "GitHub Copilot";
        return new ModelSelectionEntry(model.Id, $"{displayBase} ({providerLabel})", source)
        {
            ProviderLabel = providerLabel,
            RateLabel = getModelRateLabel(model, source),
            DetailSummary = buildModelDetailSummary(model, source),
            IsCurrent = isCurrentModelAndSource(model.Id, source)
        };
    }

    internal async Task RefreshAvailableModelsAsync(
        CopilotClient? copilotClient,
        bool isGitHubCopilotAuthenticated,
        Func<string, Task<IReadOnlyList<string>>> tryGetCliModelIdsAsync,
        Func<Task<List<ModelInfo>>> tryGetByokProviderModelsAsync)
    {
        var githubModels = await TryGetGitHubProviderModelsAsync(copilotClient, isGitHubCopilotAuthenticated, tryGetCliModelIdsAsync);
        var byokModels = await tryGetByokProviderModelsAsync();
        UpdateAvailableModels(githubModels, byokModels);
    }

    internal static string ToModelDisplayName(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return modelId;
        }

        var tokens = modelId.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return modelId;
        }

        var formattedTokens = tokens.Select(token => token.ToLowerInvariant() switch
        {
            "gpt" => "GPT",
            "claude" => "Claude",
            "gemini" => "Gemini",
            "codex" => "Codex",
            "mini" => "Mini",
            "max" => "Max",
            "pro" => "Pro",
            "preview" => "(Preview)",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(token)
        });

        return string.Join(' ', formattedTokens);
    }

    internal string GetModelRateLabel(ModelInfo model, ModelSource source)
    {
        if (source == ModelSource.Byok
            && _byokPricing.TryGetValue(model.Id, out var byokPrice)
            && !string.IsNullOrWhiteSpace(byokPrice.DisplayText))
        {
            return byokPrice.DisplayText!;
        }

        if (model.Billing != null)
        {
            return $"{model.Billing.Multiplier.ToString("0.##", CultureInfo.InvariantCulture)}x premium";
        }

        return "n/a";
    }

    internal ModelInfo? GetSelectedModelInfo(string? selectedModel)
    {
        return GetModelInfo(selectedModel);
    }

    internal ModelInfo? GetModelInfo(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var selected = _availableModels.FirstOrDefault(model => model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        if (selected != null)
        {
            return selected;
        }

        return _availableModels.FirstOrDefault(model => model.Name.Equals(modelId, StringComparison.OrdinalIgnoreCase));
    }

    internal IReadOnlyList<(string Label, string Value)> GetSelectedModelDetails(
        string? selectedModel,
        bool useByokOpenAi,
        Func<int, string> formatCompactTokenCount,
        Func<ModelInfo?, string?> getReasoningDisplay)
    {
        var model = GetSelectedModelInfo(selectedModel);
        if (model == null)
        {
            return [];
        }

        var source = useByokOpenAi ? ModelSource.Byok : ModelSource.GitHub;
        var details = new List<(string Label, string Value)>
        {
            ("Provider", source == ModelSource.Byok ? "BYOK / OpenAI" : "GitHub Copilot")
        };

        var rateLabel = GetModelRateLabel(model, source);
        if (!rateLabel.Equals("n/a", StringComparison.OrdinalIgnoreCase))
        {
            details.Add((source == ModelSource.Byok ? "Pricing" : "Premium rate", rateLabel));
        }

        var contextWindow = model.Capabilities?.Limits?.MaxContextWindowTokens;
        if (contextWindow is > 0)
        {
            details.Add(("Context window", formatCompactTokenCount(contextWindow.Value)));
        }

        var maxPrompt = model.Capabilities?.Limits?.MaxPromptTokens;
        if (maxPrompt is > 0)
        {
            details.Add(("Max prompt", formatCompactTokenCount(maxPrompt.Value)));
        }

        var capabilities = new List<string>();
        if (model.Capabilities?.Supports?.Vision == true)
        {
            capabilities.Add("vision");
        }

        if (model.Capabilities?.Supports?.ReasoningEffort == true)
        {
            capabilities.Add("reasoning");
        }

        if (capabilities.Count > 0)
        {
            details.Add(("Capabilities", string.Join(", ", capabilities)));
        }

        var reasoningDisplay = getReasoningDisplay(model);
        if (!string.IsNullOrWhiteSpace(reasoningDisplay))
        {
            details.Add(("Reasoning", reasoningDisplay));
        }

        if (model.SupportedReasoningEfforts is { Count: > 0 })
        {
            details.Add(("Reasoning efforts", string.Join(", ", model.SupportedReasoningEfforts)));
        }

        if (!string.IsNullOrWhiteSpace(model.DefaultReasoningEffort))
        {
            details.Add(("Default reasoning", model.DefaultReasoningEffort));
        }

        return details;
    }

    internal void ClearByokPricing()
    {
        _byokPricing.Clear();
    }

    internal void StoreByokPricing(string modelId, ByokPriceInfo priceInfo)
    {
        _byokPricing[modelId] = priceInfo;
    }

    internal ByokPriceInfo? GetActiveByokPricing(string? selectedModel, bool useByokOpenAi)
    {
        if (!useByokOpenAi || string.IsNullOrWhiteSpace(selectedModel))
        {
            return null;
        }

        return _byokPricing.TryGetValue(selectedModel, out var price) ? price : null;
    }

    internal static ByokModelDiscoveryResult ParseByokModelsResponse(JsonElement rootElement)
    {
        if (!JsonParsingHelpers.TryGetJsonPropertyIgnoreCase(rootElement, "data", out var dataElement)
            || dataElement.ValueKind != JsonValueKind.Array)
        {
            return new ByokModelDiscoveryResult([], new(StringComparer.OrdinalIgnoreCase));
        }

        var discovered = new List<ModelInfo>();
        var pricing = new Dictionary<string, ByokPriceInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var modelElement in dataElement.EnumerateArray())
        {
            var modelId = JsonParsingHelpers.ReadJsonStringProperty(modelElement, "id");
            if (string.IsNullOrWhiteSpace(modelId)
                || discovered.Any(existing => existing.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var modelName = JsonParsingHelpers.ReadJsonStringProperty(modelElement, "name", "display_name", "displayName");

            if (ModelPricingDatabase.IsNonChatModel(modelId))
            {
                continue;
            }

            var apiMode = JsonParsingHelpers.ReadJsonStringProperty(modelElement, "mode", "type", "object_type");
            if (!string.IsNullOrWhiteSpace(apiMode) && IsNonChatApiMode(apiMode))
            {
                continue;
            }

            var model = new ModelInfo
            {
                Id = modelId,
                Name = modelName ?? ToModelDisplayName(modelId)
            };

            var capabilities = BuildByokCapabilities(modelElement);
            if (capabilities != null)
            {
                model.Capabilities = capabilities;
            }

            var billingMultiplier = JsonParsingHelpers.ReadJsonDoubleProperty(modelElement, "multiplier");
            if (!billingMultiplier.HasValue
                && JsonParsingHelpers.TryGetJsonPropertyIgnoreCase(modelElement, "billing", out var billingElement)
                && billingElement.ValueKind == JsonValueKind.Object)
            {
                billingMultiplier = JsonParsingHelpers.ReadJsonDoubleProperty(billingElement, "multiplier");
            }

            if (billingMultiplier.HasValue)
            {
                model.Billing = new ModelBilling
                {
                    Multiplier = billingMultiplier.Value
                };
            }

            var supportedReasoningEfforts = JsonParsingHelpers.ReadJsonStringArrayProperty(modelElement, "supported_reasoning_efforts", "supportedReasoningEfforts");
            if (supportedReasoningEfforts.Count > 0)
            {
                model.SupportedReasoningEfforts = supportedReasoningEfforts;
            }

            var defaultReasoningEffort = JsonParsingHelpers.ReadJsonStringProperty(modelElement, "default_reasoning_effort", "defaultReasoningEffort");
            if (!string.IsNullOrWhiteSpace(defaultReasoningEffort))
            {
                model.DefaultReasoningEffort = defaultReasoningEffort;
            }

            var priceInfo = ExtractByokPriceInfo(modelElement);
            if (priceInfo == null
                && !ModelPricingDatabase.IsNonChatModel(modelId)
                && ModelPricingDatabase.TryGetPrice(modelId, out var fallbackInput, out var fallbackOutput))
            {
                priceInfo = new ByokPriceInfo(fallbackInput, fallbackOutput, FormatByokPriceDisplayEstimate(fallbackInput, fallbackOutput));
            }

            if (priceInfo != null)
            {
                pricing[modelId] = priceInfo;
            }

            discovered.Add(model);
        }

        return new ByokModelDiscoveryResult(discovered, pricing);
    }

    internal static ModelCapabilities? BuildByokCapabilities(JsonElement modelElement)
    {
        JsonElement supportsSource = modelElement;
        JsonElement limitsSource = modelElement;

        if (JsonParsingHelpers.TryGetJsonPropertyIgnoreCase(modelElement, "capabilities", out var capabilitiesElement)
            && capabilitiesElement.ValueKind == JsonValueKind.Object)
        {
            if (JsonParsingHelpers.TryGetJsonPropertyIgnoreCase(capabilitiesElement, "supports", out var supportsElement)
                && supportsElement.ValueKind == JsonValueKind.Object)
            {
                supportsSource = supportsElement;
            }

            if (JsonParsingHelpers.TryGetJsonPropertyIgnoreCase(capabilitiesElement, "limits", out var limitsElement)
                && limitsElement.ValueKind == JsonValueKind.Object)
            {
                limitsSource = limitsElement;
            }
        }

        var supportsVision = JsonParsingHelpers.ReadJsonBoolProperty(supportsSource, "vision", "supports_vision", "supportsVision");
        var supportsReasoningEffort = JsonParsingHelpers.ReadJsonBoolProperty(supportsSource, "reasoningEffort", "reasoning_effort", "supports_reasoning_effort", "supportsReasoningEffort");
        var maxPromptTokens = JsonParsingHelpers.ReadJsonIntProperty(limitsSource, "max_prompt_tokens", "maxPromptTokens");
        var maxContextWindowTokens = JsonParsingHelpers.ReadJsonIntProperty(limitsSource, "max_context_window_tokens", "maxContextWindowTokens", "context_window", "contextWindow");

        if (!supportsVision.HasValue
            && !supportsReasoningEffort.HasValue
            && !maxPromptTokens.HasValue
            && !maxContextWindowTokens.HasValue)
        {
            return null;
        }

        var capabilities = new ModelCapabilities();

        if (supportsVision.HasValue || supportsReasoningEffort.HasValue)
        {
            capabilities.Supports = new ModelSupports
            {
                Vision = supportsVision ?? false,
                ReasoningEffort = supportsReasoningEffort ?? false
            };
        }

        if (maxPromptTokens.HasValue || maxContextWindowTokens.HasValue)
        {
            capabilities.Limits = new ModelLimits
            {
                MaxPromptTokens = maxPromptTokens,
                MaxContextWindowTokens = maxContextWindowTokens ?? 0
            };
        }

        return capabilities;
    }

    internal static ByokPriceInfo? ExtractByokPriceInfo(JsonElement modelElement)
    {
        var display = JsonParsingHelpers.ReadJsonStringProperty(modelElement, "price_display", "priceDisplay", "pricing_display", "pricingDisplay");
        var input = JsonParsingHelpers.ReadJsonDecimalProperty(modelElement, "input_price", "inputPrice", "prompt_price", "promptPrice", "input_per_million", "inputPricePerMillionTokens");
        var output = JsonParsingHelpers.ReadJsonDecimalProperty(modelElement, "output_price", "outputPrice", "completion_price", "completionPrice", "output_per_million", "outputPricePerMillionTokens");

        if (JsonParsingHelpers.TryGetJsonPropertyIgnoreCase(modelElement, "pricing", out var pricingElement)
            && pricingElement.ValueKind == JsonValueKind.Object)
        {
            display ??= JsonParsingHelpers.ReadJsonStringProperty(pricingElement, "display", "label", "summary");
            input ??= JsonParsingHelpers.ReadJsonDecimalProperty(pricingElement, "input", "input_price", "inputPrice", "prompt", "prompt_price", "promptPrice", "input_per_million", "inputPricePerMillionTokens");
            output ??= JsonParsingHelpers.ReadJsonDecimalProperty(pricingElement, "output", "output_price", "outputPrice", "completion", "completion_price", "completionPrice", "output_per_million", "outputPricePerMillionTokens");
        }

        if (!input.HasValue && !output.HasValue && string.IsNullOrWhiteSpace(display))
        {
            return null;
        }

        display ??= FormatByokPriceDisplay(input, output);
        return new ByokPriceInfo(input, output, display);
    }

    internal static string? FormatByokPriceDisplay(decimal? inputPrice, decimal? outputPrice)
    {
        if (inputPrice.HasValue && outputPrice.HasValue)
        {
            return $"${inputPrice.Value.ToString("0.####", CultureInfo.InvariantCulture)}/M in, ${outputPrice.Value.ToString("0.####", CultureInfo.InvariantCulture)}/M out";
        }

        if (inputPrice.HasValue)
        {
            return $"${inputPrice.Value.ToString("0.####", CultureInfo.InvariantCulture)}/M";
        }

        if (outputPrice.HasValue)
        {
            return $"${outputPrice.Value.ToString("0.####", CultureInfo.InvariantCulture)}/M";
        }

        return null;
    }

    internal static string FormatByokPriceDisplayEstimate(decimal inputPrice, decimal outputPrice)
    {
        return $"~${inputPrice.ToString("0.####", CultureInfo.InvariantCulture)}/M in, ~${outputPrice.ToString("0.####", CultureInfo.InvariantCulture)}/M out";
    }

    private static bool IsNonChatApiMode(string mode)
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
}
