using System.Text;
using System.Text.RegularExpressions;

namespace TroubleScout.Services;

internal static class SessionPromptFlow
{
    private static readonly Regex MutatingIntentRegex = new(
        "\\b(empty|clear|delete|remove|restart|stop|start|set|enable|disable|kill|format|reset|recycle\\s+bin|trash)\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string BuildPromptForExecutionSafety(string userMessage)
    {
        var promptBuilder = new StringBuilder(userMessage);
        promptBuilder.Append("\n\n");
        promptBuilder.Append(PromptTemplateLoader.Render(PromptTemplateIds.TurnResponseFormattingRequirement));

        if (MutatingIntentRegex.IsMatch(userMessage))
        {
            promptBuilder.Append("\n\n");
            promptBuilder.Append(PromptTemplateLoader.Render(PromptTemplateIds.TurnExecutionSafetyRequirement));
        }

        return promptBuilder.ToString();
    }

    internal static string BuildApprovedCommandFollowUpPrompt(string executionSummary)
        => PromptTemplateLoader.Render(
            PromptTemplateIds.TurnApprovedCommandFollowUp,
            new Dictionary<string, string?>
            {
                ["executionSummary"] = executionSummary.Trim()
            });

}
