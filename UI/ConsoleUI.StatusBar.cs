using System.Text;
using Spectre.Console;

namespace TroubleScout.UI;

public static partial class ConsoleUI
{
    /// <summary>
    /// Internal: structured status bar field used for width-aware rendering.
    /// Higher Priority means "drop later" (more important to keep).
    /// </summary>
    private sealed record StatusBarField(int Priority, string PlainText, string Markup);

    private const int StatusBarMinWidth = 40;
    private const int StatusBarTokensOnlyMaxWidth = 60;
    private const int StatusBarChromeOverhead = 8; // "─── " + " ───"
    private const string StatusBarSeparator = " | ";

    private static List<StatusBarField> BuildStatusBarFields(StatusBarInfo info)
    {
        var fields = new List<StatusBarField>();

        if (!string.IsNullOrWhiteSpace(info.Model))
        {
            fields.Add(new StatusBarField(
                Priority: 60,
                PlainText: $"Model: {info.Model}",
                Markup: $"[grey]Model:[/] [magenta]{Markup.Escape(info.Model!)}[/]"));
        }

        if (!string.IsNullOrWhiteSpace(info.Provider))
        {
            fields.Add(new StatusBarField(
                Priority: 30,
                PlainText: $"Provider: {info.Provider}",
                Markup: $"[grey]Provider:[/] [blue]{Markup.Escape(info.Provider!)}[/]"));
        }

        if (!string.IsNullOrWhiteSpace(info.ReasoningEffort))
        {
            fields.Add(new StatusBarField(
                Priority: 40,
                PlainText: $"Reasoning: {info.ReasoningEffort}",
                Markup: $"[grey]Reasoning:[/] [cyan]{Markup.Escape(info.ReasoningEffort!)}[/]"));
        }

        if (info.InputTokens.HasValue || info.OutputTokens.HasValue)
        {
            var inStr = info.InputTokens.HasValue ? FormatCompactTokenCount(info.InputTokens.Value) : "?";
            var outStr = info.OutputTokens.HasValue ? FormatCompactTokenCount(info.OutputTokens.Value) : "?";
            fields.Add(new StatusBarField(
                Priority: 100,
                PlainText: $"Tokens: {inStr} in / {outStr} out",
                Markup: $"[grey]Tokens:[/] [cyan]{inStr}[/][grey] in /[/] [cyan]{outStr}[/][grey] out[/]"));
        }
        else if (info.TotalTokens.HasValue)
        {
            var totalStr = FormatCompactTokenCount(info.TotalTokens.Value);
            fields.Add(new StatusBarField(
                Priority: 100,
                PlainText: $"Tokens: {totalStr}",
                Markup: $"[grey]Tokens:[/] [cyan]{totalStr}[/]"));
        }

        if (info.ToolInvocations > 0)
        {
            fields.Add(new StatusBarField(
                Priority: 50,
                PlainText: $"Tools: {info.ToolInvocations}",
                Markup: $"[grey]Tools:[/] [cyan]{info.ToolInvocations}[/]"));
        }

        if (info.SessionInputTokens.HasValue || info.SessionOutputTokens.HasValue)
        {
            var sessIn = info.SessionInputTokens.HasValue ? FormatCompactTokenCount((int)Math.Min(info.SessionInputTokens.Value, int.MaxValue)) : "?";
            var sessOut = info.SessionOutputTokens.HasValue ? FormatCompactTokenCount((int)Math.Min(info.SessionOutputTokens.Value, int.MaxValue)) : "?";
            fields.Add(new StatusBarField(
                Priority: 80,
                PlainText: $"Session: {sessIn} in / {sessOut} out",
                Markup: $"[grey]Session:[/] [cyan]{sessIn}[/][grey] in /[/] [cyan]{sessOut}[/][grey] out[/]"));
            if (!string.IsNullOrWhiteSpace(info.SessionCostEstimate))
            {
                fields.Add(new StatusBarField(
                    Priority: 70,
                    PlainText: info.SessionCostEstimate!,
                    Markup: Markup.Escape(info.SessionCostEstimate!)));
            }
        }
        else if (!string.IsNullOrWhiteSpace(info.SessionCostEstimate))
        {
            fields.Add(new StatusBarField(
                Priority: 80,
                PlainText: $"Session: {info.SessionCostEstimate}",
                Markup: $"[grey]Session:[/] {Markup.Escape(info.SessionCostEstimate!)}"));
        }

        return fields;
    }

    private static List<StatusBarField> SelectFieldsForWidth(IReadOnlyList<StatusBarField> fields, int width)
    {
        if (width < StatusBarMinWidth || fields.Count == 0)
        {
            return new List<StatusBarField>();
        }

        // In tokens-only mode keep only the highest-priority field (which we
        // intentionally weighted at 100 for the per-turn token field).
        if (width < StatusBarTokensOnlyMaxWidth)
        {
            var top = fields.OrderByDescending(f => f.Priority).FirstOrDefault();
            return top is null ? new List<StatusBarField>() : new List<StatusBarField> { top };
        }

        // Progressive inclusion: drop lowest priority field while we don't fit.
        var working = fields.ToList();
        while (working.Count > 0 && PlainLength(working) > width)
        {
            var victim = working.OrderBy(f => f.Priority).First();
            working.Remove(victim);
        }

        // Preserve original input order so tests have a deterministic shape.
        return fields.Where(f => working.Contains(f)).ToList();
    }

    private static int PlainLength(IReadOnlyList<StatusBarField> fields)
    {
        if (fields.Count == 0) return 0;
        var sum = StatusBarChromeOverhead;
        for (int i = 0; i < fields.Count; i++)
        {
            sum += fields[i].PlainText.Length;
            if (i < fields.Count - 1)
            {
                sum += StatusBarSeparator.Length;
            }
        }
        return sum;
    }

    /// <summary>
    /// Internal: build the final markup line for a given width. Empty when the
    /// status bar should be suppressed.
    /// </summary>
    internal static string BuildStatusBarLine(StatusBarInfo info, int width)
    {
        var selected = SelectFieldsForWidth(BuildStatusBarFields(info), width);
        if (selected.Count == 0) return string.Empty;
        var statusLine = string.Join("[grey] | [/]", selected.Select(f => f.Markup));
        var line = $"[dim]───[/] {statusLine} [dim]───[/]";
        return IsMonochromeTheme() ? StripSpectreColorTags(line) : line;
    }

    /// <summary>
    /// Internal: drop Spectre style tags from a markup string so monochrome
    /// theme renders as plain text. Closing tags ([/]) are also stripped.
    /// Markup.Escape output ("[[" / "]]") is preserved.
    /// </summary>
    internal static string StripSpectreColorTags(string markup)
    {
        if (string.IsNullOrEmpty(markup)) return markup;
        var sb = new StringBuilder(markup.Length);
        for (int i = 0; i < markup.Length; i++)
        {
            // Preserve escaped brackets
            if (i + 1 < markup.Length && markup[i] == '[' && markup[i + 1] == '[')
            {
                sb.Append("[[");
                i++;
                continue;
            }
            if (i + 1 < markup.Length && markup[i] == ']' && markup[i + 1] == ']')
            {
                sb.Append("]]");
                i++;
                continue;
            }
            if (markup[i] == '[')
            {
                var close = markup.IndexOf(']', i + 1);
                if (close > i)
                {
                    // Drop the entire tag (open or close).
                    i = close;
                    continue;
                }
            }
            sb.Append(markup[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Internal: build the plain-text representation used for width math. Empty
    /// when the bar is suppressed.
    /// </summary>
    internal static string BuildStatusBarPlain(StatusBarInfo info, int width)
    {
        var selected = SelectFieldsForWidth(BuildStatusBarFields(info), width);
        if (selected.Count == 0) return string.Empty;
        return "─── " + string.Join(StatusBarSeparator, selected.Select(f => f.PlainText)) + " ───";
    }

    /// <summary>
    /// Write a compact status bar showing model, provider, and usage info
    /// </summary>
    public static void WriteStatusBar(StatusBarInfo info)
    {
        var width = ConsoleWindowWidthResolver();
        var line = BuildStatusBarLine(info, width);
        if (string.IsNullOrEmpty(line)) return;
        AnsiConsole.MarkupLine(line);
        AnsiConsole.WriteLine();
    }
}
