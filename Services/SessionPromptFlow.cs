using System.Text;
using System.Text.RegularExpressions;
using TroubleScout.UI;

namespace TroubleScout.Services;

internal static class SessionPromptFlow
{
    private static readonly Regex MutatingIntentRegex = new(
        "\\b(empty|clear|delete|remove|restart|stop|start|set|enable|disable|kill|format|reset|recycle\\s+bin|trash)\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PostAnalysisHeadingRegex = new(
        "^\\s{0,3}#{1,6}\\s*(diagnosis|findings|recommendation|recommendations|next steps|root cause)\\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

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

    internal static bool ShouldOfferPostAnalysisActionPrompt(string responseText, bool forcePrompt)
    {
        if (forcePrompt)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        if (responseText.Contains("Ready for next action", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return PostAnalysisHeadingRegex.IsMatch(responseText);
    }

    internal static string BuildPostAnalysisFollowUpPrompt(PostAnalysisAction action)
    {
        return action switch
        {
            PostAnalysisAction.ContinueInvestigating => PromptTemplateLoader.Render(PromptTemplateIds.TurnPostAnalysisContinue),
            PostAnalysisAction.ApplyFix => PromptTemplateLoader.Render(PromptTemplateIds.TurnPostAnalysisApplyFix),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported post-analysis action.")
        };
    }

    internal static string BuildApprovedCommandFollowUpPrompt(string executionSummary)
        => PromptTemplateLoader.Render(
            PromptTemplateIds.TurnApprovedCommandFollowUp,
            new Dictionary<string, string?>
            {
                ["executionSummary"] = executionSummary.Trim()
            });

    internal static async Task<bool> HandlePostAnalysisActionAsync(
        Func<PostAnalysisAction> promptAction,
        Func<string, int> recordPrompt,
        Func<string, int, CancellationToken, bool, bool, Task<bool>> sendMessage,
        Action<string> showInfo,
        CancellationToken cancellationToken)
    {
        var action = promptAction();

        switch (action)
        {
            case PostAnalysisAction.ContinueInvestigating:
            {
                var promptIndex = recordPrompt("TroubleScout action: Continue investigating.");
                return await sendMessage(
                    BuildPostAnalysisFollowUpPrompt(PostAnalysisAction.ContinueInvestigating),
                    promptIndex,
                    cancellationToken,
                    true,
                    false);
            }
            case PostAnalysisAction.ApplyFix:
            {
                var promptIndex = recordPrompt("TroubleScout action: Apply the fix.");
                return await sendMessage(
                    BuildPostAnalysisFollowUpPrompt(PostAnalysisAction.ApplyFix),
                    promptIndex,
                    cancellationToken,
                    true,
                    false);
            }
            default:
                showInfo("Stopping here. TroubleScout is ready when you are.");
                return true;
        }
    }
}
