using System.Text;

namespace TroubleScout.Services;

internal static class SlashCommandDocumentation
{
    internal static string GenerateMarkdown()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Slash Command Reference");
        sb.AppendLine();
        sb.AppendLine("This page lists every slash command available inside an interactive");
        sb.AppendLine("TroubleScout session. The short table in the README under");
        sb.AppendLine("[Interactive Commands](../README.md#interactive-commands) is the marketing");
        sb.AppendLine("view; this is the full reference.");
        sb.AppendLine();
        sb.AppendLine("> This file is generated from `Services/SlashCommandRegistry.cs`.");
        sb.AppendLine("> Update the registry metadata when command documentation changes.");
        sb.AppendLine();
        sb.AppendLine("## Conventions");
        sb.AppendLine();
        sb.AppendLine("- `<arg>` - required argument.");
        sb.AppendLine("- `[arg]` - optional argument.");
        sb.AppendLine("- `a|b` - choose one literal value.");
        sb.AppendLine("- Arguments separated by whitespace unless noted otherwise.");
        sb.AppendLine("- Commands start with `/`; the command token is case-insensitive.");
        sb.AppendLine();

        foreach (var category in GetCategories())
        {
            sb.AppendLine($"## {category}");
            sb.AppendLine();

            foreach (var command in SlashCommandRegistry.Commands.Where(command => command.Category == category))
            {
                AppendCommand(sb, command);
            }
        }

        AppendPromptActions(sb);
        AppendCancellation(sb);

        return sb.ToString();
    }

    private static IEnumerable<string> GetCategories()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in SlashCommandRegistry.Commands)
        {
            if (seen.Add(command.Category))
            {
                yield return command.Category;
            }
        }
    }

    private static void AppendCommand(StringBuilder sb, SlashCommandDescriptor command)
    {
        sb.AppendLine($"### `{command.Usage}`");
        sb.AppendLine();
        sb.AppendLine(command.Summary);
        sb.AppendLine();

        foreach (var detail in command.Details ?? Array.Empty<string>())
        {
            sb.AppendLine(detail);
            sb.AppendLine();
        }

        if (command.Examples is { Count: > 0 })
        {
            sb.AppendLine("Examples:");
            sb.AppendLine();
            sb.AppendLine("```text");
            foreach (var example in command.Examples)
            {
                sb.AppendLine(example);
            }
            sb.AppendLine("```");
            sb.AppendLine();
        }
    }

    private static void AppendPromptActions(StringBuilder sb)
    {
        sb.AppendLine("## Approval prompt actions (not slash commands)");
        sb.AppendLine();
        sb.AppendLine("When the AI requests a mutating PowerShell command, an MCP tool call, or");
        sb.AppendLine("an outbound URL fetch, TroubleScout prompts inline. These prompts are");
        sb.AppendLine("not invoked with `/`, but they share the slash-command surface and");
        sb.AppendLine("deserve mention here.");
        sb.AppendLine();
        sb.AppendLine("- **Command approval** - Yes / No / Explain. Explain shows a detail panel and re-prompts Yes / No.");
        sb.AppendLine("- **MCP approval** - Approve once / Approve this server for the session / Approve and persist (monitoring/ticketing only) / Deny.");
        sb.AppendLine("- **URL approval** - Allow this URL / Allow all URLs for this session / Deny.");
        sb.AppendLine("- **Post-analysis action** - Continue investigating / Apply the fix / Stop for now.");
        sb.AppendLine();
    }

    private static void AppendCancellation(StringBuilder sb)
    {
        sb.AppendLine("## Cancellation");
        sb.AppendLine();
        sb.AppendLine("Press <kbd>Esc</kbd> during an AI turn to cancel at the RPC layer. The");
        sb.AppendLine("key is ignored while an approval prompt is open, so accidental presses");
        sb.AppendLine("do not abort an approval dialog.");
        sb.AppendLine();
    }
}
