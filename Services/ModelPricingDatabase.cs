namespace TroubleScout.Services;

internal sealed record ModelPricingEntry(decimal InputPricePerMillionTokens, decimal OutputPricePerMillionTokens, string Mode);

internal static class ModelPricingDatabase
{
    private static readonly HashSet<string> NonChatModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image_generation",
        "embedding",
        "audio_transcription",
        "audio_speech",
        "completion",
        "responses",
        "ocr",
        "rerank",
        "moderation",
        "video_generation",
        "realtime"
    };

    private static readonly Dictionary<string, ModelPricingEntry> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-3.5-turbo"] = new(0.50m, 1.50m, "chat"),
        ["gpt-4"] = new(30.00m, 60.00m, "chat"),
        ["gpt-4-turbo"] = new(10.00m, 30.00m, "chat"),
        ["gpt-4o"] = new(2.50m, 10.00m, "chat"),
        ["gpt-4o-mini"] = new(0.15m, 0.60m, "chat"),
        ["gpt-4.1"] = new(2.00m, 8.00m, "chat"),
        ["gpt-4.1-mini"] = new(0.40m, 1.60m, "chat"),
        ["gpt-4.1-nano"] = new(0.10m, 0.40m, "chat"),
        ["gpt-5"] = new(1.25m, 10.00m, "chat"),
        ["gpt-5-mini"] = new(0.25m, 2.00m, "chat"),
        ["gpt-5-nano"] = new(0.05m, 0.40m, "chat"),
        ["gpt-5.1"] = new(1.25m, 10.00m, "chat"),
        ["gpt-5.2"] = new(1.75m, 14.00m, "chat"),
        ["gpt-5.4"] = new(2.50m, 15.00m, "chat"),
        ["o1"] = new(15.00m, 60.00m, "chat"),
        ["o3"] = new(2.00m, 8.00m, "chat"),
        ["o3-mini"] = new(1.10m, 4.40m, "chat"),
        ["o4-mini"] = new(1.10m, 4.40m, "chat"),
        ["chatgpt-4o-latest"] = new(5.00m, 15.00m, "chat"),

        ["gpt-image-1"] = new(0m, 0m, "image_generation"),
        ["gpt-image-1-mini"] = new(0m, 0m, "image_generation"),
        ["gpt-image-1.5"] = new(0m, 0m, "image_generation"),
        ["dall-e-2"] = new(0m, 0m, "image_generation"),
        ["dall-e-3"] = new(0m, 0m, "image_generation"),
        ["text-embedding-3-large"] = new(0m, 0m, "embedding"),
        ["text-embedding-3-small"] = new(0m, 0m, "embedding"),
        ["text-embedding-ada-002"] = new(0m, 0m, "embedding"),
        ["whisper-1"] = new(0m, 0m, "audio_transcription"),
        ["gpt-4o-mini-tts"] = new(0m, 0m, "audio_speech"),
        ["gpt-4o-transcribe"] = new(0m, 0m, "audio_transcription"),
        ["gpt-4o-mini-transcribe"] = new(0m, 0m, "audio_transcription"),
        ["gpt-3.5-turbo-instruct"] = new(0m, 0m, "completion"),
        ["gpt-5-codex"] = new(0m, 0m, "responses"),
        ["gpt-5-pro"] = new(0m, 0m, "responses"),
        ["gpt-5.1-codex"] = new(0m, 0m, "responses"),
        ["gpt-5.1-codex-max"] = new(0m, 0m, "responses"),
        ["gpt-5.1-codex-mini"] = new(0m, 0m, "responses"),
        ["gpt-5.2-codex"] = new(0m, 0m, "responses"),
        ["gpt-5.2-pro"] = new(0m, 0m, "responses"),
        ["gpt-5.3-codex"] = new(0m, 0m, "responses"),
        ["gpt-5.4-pro"] = new(0m, 0m, "responses"),
        ["o1-pro"] = new(0m, 0m, "responses"),
        ["o3-pro"] = new(0m, 0m, "responses"),
        ["o3-deep-research"] = new(0m, 0m, "responses"),
        ["o4-mini-deep-research"] = new(0m, 0m, "responses"),
        ["chatgpt-image-latest"] = new(0m, 0m, "image_generation"),

        ["claude-3-haiku"] = new(0.25m, 1.25m, "chat"),
        ["claude-3-opus"] = new(15.00m, 75.00m, "chat"),
        ["claude-3-sonnet"] = new(3.00m, 15.00m, "chat"),
        ["claude-3.5-sonnet"] = new(3.00m, 15.00m, "chat"),
        ["claude-3.5-haiku"] = new(1.00m, 5.00m, "chat"),
        ["claude-3.7-sonnet"] = new(3.00m, 15.00m, "chat"),
        ["claude-4-sonnet"] = new(3.00m, 15.00m, "chat"),
        ["claude-4-opus"] = new(15.00m, 75.00m, "chat"),
        ["claude-haiku-4.5"] = new(1.00m, 5.00m, "chat"),
        ["claude-sonnet-4.5"] = new(3.00m, 15.00m, "chat"),
        ["claude-sonnet-4.6"] = new(3.00m, 15.00m, "chat"),
        ["claude-opus-4.5"] = new(5.00m, 25.00m, "chat"),
        ["claude-opus-4.6"] = new(5.00m, 25.00m, "chat"),
        ["claude-opus-4.1"] = new(15.00m, 75.00m, "chat"),

        ["gemini-2.0-flash"] = new(0.10m, 0.40m, "chat"),
        ["gemini-2.0-flash-lite"] = new(0.07m, 0.30m, "chat"),
        ["gemini-2.5-flash"] = new(0.30m, 2.50m, "chat"),
        ["gemini-2.5-flash-lite"] = new(0.10m, 0.40m, "chat"),
        ["gemini-2.5-pro"] = new(1.25m, 10.00m, "chat"),
        ["gemini-3-flash-preview"] = new(0.50m, 3.00m, "chat"),
        ["gemini-3-pro-preview"] = new(2.00m, 12.00m, "chat"),
        ["gemini-3.1-pro-preview"] = new(2.00m, 12.00m, "chat"),

        ["deepseek-chat"] = new(0.28m, 0.42m, "chat"),
        ["deepseek-reasoner"] = new(0.28m, 0.42m, "chat"),
        ["deepseek-r1"] = new(0.55m, 2.19m, "chat"),
        ["deepseek-v3"] = new(0.27m, 1.10m, "chat"),

        ["mistral-large-latest"] = new(0.50m, 1.50m, "chat"),
        ["mistral-small-latest"] = new(0.06m, 0.18m, "chat"),
        ["mistral-medium-latest"] = new(0.40m, 2.00m, "chat"),
        ["codestral-latest"] = new(1.00m, 3.00m, "chat"),

        ["llama-3.1-8b"] = new(0.10m, 0.10m, "chat"),
        ["llama-3.1-70b"] = new(0.88m, 0.88m, "chat"),
        ["llama-3.1-405b"] = new(5.33m, 16.00m, "chat"),
        ["llama-3.3-70b"] = new(0.88m, 0.88m, "chat"),
        ["llama-4-scout"] = new(0.17m, 0.65m, "chat"),
        ["llama-4-maverick"] = new(0.19m, 0.85m, "chat"),

        ["qwen-2.5-72b"] = new(0.90m, 0.90m, "chat")
    };

    private static readonly string[] EntryKeysByDescendingLength = Entries.Keys
        .OrderByDescending(static key => key.Length)
        .ToArray();

    public static bool TryGetPrice(string modelId, out decimal inputPerMillion, out decimal outputPerMillion)
    {
        inputPerMillion = 0m;
        outputPerMillion = 0m;

        if (!TryGetEntry(modelId, out var entry))
        {
            return false;
        }

        inputPerMillion = entry.InputPricePerMillionTokens;
        outputPerMillion = entry.OutputPricePerMillionTokens;
        return true;
    }

    public static bool TryGetMode(string modelId, out string mode)
    {
        mode = string.Empty;

        if (!TryGetEntry(modelId, out var entry))
        {
            return false;
        }

        mode = entry.Mode;
        return true;
    }

    public static bool IsNonChatModel(string modelId)
    {
        return TryGetMode(modelId, out var mode) && NonChatModes.Contains(mode);
    }

    private static bool TryGetEntry(string modelId, out ModelPricingEntry entry)
    {
        entry = default!;

        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        foreach (var candidate in GetLookupCandidates(modelId))
        {
            if (TryGetEntryForCandidate(candidate, out entry))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetLookupCandidates(string modelId)
    {
        var normalizedModelId = modelId.Trim();
        yield return normalizedModelId;

        var strippedModelId = StripProviderPrefix(normalizedModelId);
        if (!string.Equals(strippedModelId, normalizedModelId, StringComparison.Ordinal))
        {
            yield return strippedModelId;
        }
    }

    private static string StripProviderPrefix(string modelId)
    {
        var separatorIndex = modelId.LastIndexOf('/');
        if (separatorIndex < 0 || separatorIndex == modelId.Length - 1)
        {
            return modelId;
        }

        return modelId[(separatorIndex + 1)..];
    }

    private static bool TryGetEntryForCandidate(string modelId, out ModelPricingEntry entry)
    {
        if (Entries.TryGetValue(modelId, out entry!))
        {
            return true;
        }

        foreach (var key in EntryKeysByDescendingLength)
        {
            if (IsBaseModelMatch(modelId, key))
            {
                entry = Entries[key];
                return true;
            }
        }

        entry = default!;
        return false;
    }

    private static bool IsBaseModelMatch(string candidateModelId, string knownModelId)
    {
        if (!candidateModelId.StartsWith(knownModelId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (candidateModelId.Length == knownModelId.Length)
        {
            return true;
        }

        return candidateModelId[knownModelId.Length] switch
        {
            '-' or '.' or ':' or '@' => true,
            _ => false
        };
    }
}
