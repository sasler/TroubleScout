using GitHub.Copilot.SDK;

namespace TroubleScout.Services;

internal static class ReasoningEffortHelper
{
    internal static string? Normalize(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }

        var normalized = reasoningEffort.Trim().ToLowerInvariant();
        return normalized is "auto" or "default" ? null : normalized;
    }

    internal static bool SupportsReasoningEffort(ModelInfo? model) =>
        model?.Capabilities?.Supports?.ReasoningEffort == true;

    internal static IReadOnlyList<string> GetSupportedReasoningEfforts(ModelInfo? model)
    {
        if (model?.SupportedReasoningEfforts is not { Count: > 0 })
        {
            return [];
        }

        return model.SupportedReasoningEfforts
            .Select(Normalize)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string? GetDefaultReasoningEffort(ModelInfo? model) =>
        Normalize(model?.DefaultReasoningEffort);
}
