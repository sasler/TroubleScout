using System.Text;

namespace TroubleScout.Services;

internal static class SecondOpinionService
{
    internal static string BuildSecondOpinionPrompt(
        string previousModel,
        string newModel,
        IReadOnlyList<ReportPromptEntry> prompts,
        int maxTurns,
        int maxPromptChars,
        int maxUserPromptChars,
        int maxReplyChars,
        int maxCommandChars,
        int maxToolOutputChars)
    {
        var visiblePrompts = prompts.Count > maxTurns
            ? prompts.Skip(prompts.Count - maxTurns).ToList()
            : prompts.ToList();
        var truncationNotes = new List<string>();
        var turnSections = new List<string>(visiblePrompts.Count);

        var sb = new StringBuilder();
        sb.AppendLine("You are providing a second opinion for an existing TroubleScout troubleshooting session.");
        sb.AppendLine($"Previous model: {previousModel}");
        sb.AppendLine($"New model: {newModel}");
        sb.AppendLine();
        sb.AppendLine("Review the full session context below, then continue helping from the same point.");
        sb.AppendLine("Call out where you agree or disagree with the prior analysis and suggest the best next troubleshooting steps.");

        if (visiblePrompts.Count != prompts.Count)
        {
            truncationNotes.Add($"Only the most recent {visiblePrompts.Count} turns are included.");
        }

        var firstVisibleTurnNumber = prompts.Count - visiblePrompts.Count + 1;
        for (var i = 0; i < visiblePrompts.Count; i++)
        {
            var prompt = visiblePrompts[i];
            var turnNumber = firstVisibleTurnNumber + i;
            turnSections.Add(BuildSecondOpinionTurnSection(
                prompt,
                turnNumber,
                maxUserPromptChars,
                maxReplyChars,
                maxCommandChars,
                maxToolOutputChars));
        }

        const string sizeLimitNote = "Older turns were omitted to fit prompt size limits.";
        var reservedNotesLength = sizeLimitNote.Length + 32;
        var remainingBudget = Math.Max(0, maxPromptChars - sb.Length - reservedNotesLength);
        var keptSections = new List<string>(turnSections.Count);
        var consumedLength = 0;

        for (var i = turnSections.Count - 1; i >= 0; i--)
        {
            var section = turnSections[i];
            if (consumedLength + section.Length > remainingBudget)
            {
                continue;
            }

            keptSections.Add(section);
            consumedLength += section.Length;
        }

        keptSections.Reverse();
        if (keptSections.Count != turnSections.Count)
        {
            truncationNotes.Add(sizeLimitNote);
        }

        if (truncationNotes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Context limits:");
            foreach (var note in truncationNotes)
            {
                sb.AppendLine($"- {note}");
            }
        }

        if (keptSections.Count == 0 && turnSections.Count > 0)
        {
            keptSections.Add(turnSections[^1]);
        }

        foreach (var section in keptSections)
        {
            sb.Append(section);
        }

        return EnforceSecondOpinionPromptLimit(sb.ToString().Trim(), maxPromptChars);
    }

    internal static string BuildSecondOpinionTurnSection(
        ReportPromptEntry prompt,
        int turnNumber,
        int maxUserPromptChars,
        int maxReplyChars,
        int maxCommandChars,
        int maxToolOutputChars)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"## Turn {turnNumber}");
        sb.AppendLine("### User");
        sb.AppendLine(TrimSecondOpinionText(prompt.Prompt, maxUserPromptChars));

        if (prompt.Actions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Tool actions");
            foreach (var action in prompt.Actions)
            {
                sb.AppendLine($"- Source: {action.Source}");
                sb.AppendLine($"  Target: {action.Target}");
                sb.AppendLine($"  Approval: {action.SafetyApproval}");
                sb.AppendLine($"  Command: {TrimSecondOpinionText(action.Command, maxCommandChars)}");

                if (!string.IsNullOrWhiteSpace(action.Output))
                {
                    sb.AppendLine("  Output:");
                    sb.AppendLine(IndentMultilineText(
                        TrimSecondOpinionText(action.Output, maxToolOutputChars),
                        "    "));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(prompt.AgentReply))
        {
            sb.AppendLine();
            sb.AppendLine("### Assistant");
            sb.AppendLine(TrimSecondOpinionText(prompt.AgentReply, maxReplyChars));
        }

        return sb.ToString();
    }

    internal static string IndentMultilineText(string text, string indent)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => indent + line));
    }

    internal static string TrimSecondOpinionText(string? text, int maxChars)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        return trimmed[..maxChars].TrimEnd() + "... [truncated]";
    }

    internal static string EnforceSecondOpinionPromptLimit(string prompt, int maxPromptChars)
    {
        const string truncationNotice = "\n\n[Context truncated to fit prompt size limits.]";
        if (prompt.Length <= maxPromptChars)
        {
            return prompt;
        }

        var maxContentLength = Math.Max(0, maxPromptChars - truncationNotice.Length);
        return prompt[..maxContentLength].TrimEnd() + truncationNotice;
    }
}
