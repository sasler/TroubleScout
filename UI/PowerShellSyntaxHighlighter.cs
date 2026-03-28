using System.Text.RegularExpressions;

namespace TroubleScout.UI;

internal static class PowerShellSyntaxHighlighter
{
    private static readonly Regex PowerShellTokenRegex = new(
        "(?<string>'([^'\\\\]|\\\\.)*'|\"([^\"\\\\]|\\\\.)*\")" +
        "|(?<variable>\\$\\{[^}]+\\}|\\$[A-Za-z_][\\w:]*|\\$\\([^)]+\\))" +
        "|(?<keyword>\\b(?:if|else|elseif|foreach|for|while|do|switch|try|catch|finally|throw|return|function|param|begin|process|end|break|continue|class|enum|using|in|default)\\b)" +
        "|(?<op>(?:-eq|-ne|-gt|-ge|-lt|-le|-and|-or|-not)\\b|\\|\\||&&|[|;])" +
        "|(?<cmdlet>\\b[A-Za-z]+-[A-Za-z][A-Za-z0-9]*\\b)" +
        "|(?<param>-[A-Za-z][\\w-]*)" +
        "|(?<number>\\b\\d+(?:\\.\\d+)?\\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string HighlightPowerShellMarkup(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var builder = new System.Text.StringBuilder(input.Length + 16);
        int lastIndex = 0;

        foreach (Match match in PowerShellTokenRegex.Matches(input))
        {
            if (!match.Success)
                continue;

            if (match.Index > lastIndex)
            {
                builder.Append(Spectre.Console.Markup.Escape(input.Substring(lastIndex, match.Index - lastIndex)));
            }

            var tokenText = Spectre.Console.Markup.Escape(match.Value);
            var style = GetPowerShellTokenStyle(match);

            if (string.IsNullOrEmpty(style))
            {
                builder.Append(tokenText);
            }
            else
            {
                builder.Append('[').Append(style).Append(']').Append(tokenText).Append("[/]");
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < input.Length)
        {
            builder.Append(Spectre.Console.Markup.Escape(input.Substring(lastIndex)));
        }

        return builder.ToString();
    }

    private static string? GetPowerShellTokenStyle(Match match)
    {
        if (match.Groups["string"].Success)
            return "green";
        if (match.Groups["variable"].Success)
            return "deepskyblue1";
        if (match.Groups["keyword"].Success)
            return "violet";
        if (match.Groups["cmdlet"].Success)
            return "cyan";
        if (match.Groups["param"].Success)
            return "yellow";
        if (match.Groups["number"].Success)
            return "blue";
        if (match.Groups["op"].Success)
            return "magenta";

        return null;
    }
}
