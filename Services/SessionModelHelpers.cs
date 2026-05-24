using System.Globalization;
using System.Text.RegularExpressions;
using GitHub.Copilot;

namespace TroubleScout.Services;

internal static class SessionModelHelpers
{
    private static readonly Regex CliModelIdRegex = new(
        "\"((?:claude|gpt|gemini)-[a-z0-9][a-z0-9.-]*)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Gpt5FamilyModelRegex = new(
        "(^|[^a-z0-9])gpt[\\s._-]*5($|[^a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string BuildModelDetailSummary(ModelInfo model, ModelSource source)
    {
        var details = new List<string>();

        var contextWindow = model.Capabilities?.Limits?.MaxContextWindowTokens;
        if (contextWindow is > 0)
        {
            details.Add($"context {FormatCompactTokenCount(contextWindow.Value)}");
        }

        var maxPrompt = model.Capabilities?.Limits?.MaxPromptTokens;
        if (maxPrompt is > 0)
        {
            details.Add($"prompt {FormatCompactTokenCount(maxPrompt.Value)}");
        }

        if (model.Capabilities?.Supports?.Vision == true)
        {
            details.Add("vision");
        }

        if (model.Capabilities?.Supports?.ReasoningEffort == true)
        {
            details.Add("reasoning");
        }

        if (!string.IsNullOrWhiteSpace(model.DefaultReasoningEffort))
        {
            details.Add($"default reasoning {model.DefaultReasoningEffort}");
        }

        if (source == ModelSource.GitHub && model.Billing?.Multiplier > 0)
        {
            details.Add($"multiplier {model.Billing.Multiplier:0.##}x");
        }

        return details.Count == 0 ? "No extra metadata available" : string.Join(" | ", details);
    }

    internal static string FormatCompactTokenCount(int value)
    {
        if (value >= 1_000_000)
        {
            return (value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        if (value >= 1_000)
        {
            return (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    internal static IReadOnlyList<string> ParseCliModelIds(string helpText)
    {
        if (string.IsNullOrWhiteSpace(helpText))
        {
            return [];
        }

        var modelIds = new List<string>();
        var modelSectionStart = helpText.IndexOf("--model <model>", StringComparison.OrdinalIgnoreCase);
        if (modelSectionStart < 0)
        {
            ExtractModelIds(helpText, modelIds);
            return modelIds;
        }

        var modelSectionEnd = helpText.IndexOf("--no-alt-screen", modelSectionStart, StringComparison.OrdinalIgnoreCase);
        var modelSection = modelSectionEnd > modelSectionStart
            ? helpText[modelSectionStart..modelSectionEnd]
            : helpText[modelSectionStart..];

        ExtractModelIds(modelSection, modelIds);

        if (modelIds.Count == 0)
        {
            ExtractModelIds(helpText, modelIds);
        }

        return modelIds;
    }

    internal static string? GetByokWireApi(string? model)
        => IsGpt5FamilyModel(model) ? "responses" : null;

    internal static bool IsGpt5FamilyModel(string? model)
        => !string.IsNullOrWhiteSpace(model) && Gpt5FamilyModelRegex.IsMatch(model);

    private static void ExtractModelIds(string text, List<string> target)
    {
        foreach (Match match in CliModelIdRegex.Matches(text))
        {
            if (match.Groups.Count < 2)
            {
                continue;
            }

            var value = match.Groups[1].Value;
            if (!target.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(value);
            }
        }
    }
}
