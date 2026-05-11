using Spectre.Console;
using Spectre.Console.Rendering;

namespace TroubleScout.UI;

public static partial class ConsoleUI
{
    /// <summary>
    /// Display a command that requires approval
    /// </summary>
    public static ApprovalResult PromptCommandApproval(
        string command,
        string reason,
        string? agentIntent = null,
        string? impact = null)
    {
        LiveThinkingIndicator.PauseForApproval();
        try
        {
            if (CommandApprovalPromptOverride != null)
            {
                return CommandApprovalPromptOverride(command, reason, agentIntent, impact);
            }

            AnsiConsole.WriteLine();
            
            var rows = new List<IRenderable>
            {
                new Markup($"[yellow]Command:[/] [white]{Markup.Escape(command)}[/]")
            };

            if (!string.IsNullOrWhiteSpace(agentIntent))
            {
                rows.Add(new Markup(""));
                rows.Add(new Markup($"[cyan]Why:[/] [white]{Markup.Escape(agentIntent)}[/]"));
            }

            rows.Add(new Markup(""));
            rows.Add(new Markup($"[red]{Markup.Escape(impact ?? "This command can modify system state.")}[/]"));

            var panel = new Panel(new Rows(rows))
            .Header("[bold yellow] Approval Required [/]")
            .Border(BoxBorder.Heavy)
            .BorderColor(Color.Yellow);
            
            AnsiConsole.Write(panel);

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]What would you like to do?[/]")
                    .HighlightStyle(new Style(Color.Cyan1))
                    .AddChoices(new[]
                    {
                        "❌ No, skip",
                        "✅ Yes, execute",
                        "❓ Explain what this does"
                    }));

            if (choice.Contains("Explain", StringComparison.OrdinalIgnoreCase))
            {
                ShowCommandExplanation(command, reason, agentIntent, impact);

                return AnsiConsole.Confirm("[yellow]Do you want to execute this command?[/]", false)
                    ? ApprovalResult.Approved
                    : ApprovalResult.Denied;
            }

            return choice.Contains("Yes", StringComparison.OrdinalIgnoreCase)
                ? ApprovalResult.Approved
                : ApprovalResult.Denied;
        }
        finally
        {
            LiveThinkingIndicator.ResumeAfterApproval();
        }
    }

    public static UrlApprovalResult PromptUrlApproval(string url, string? intention = null)
    {
        LiveThinkingIndicator.PauseForApproval();
        try
        {
            if (UrlApprovalPromptOverride != null)
            {
                return UrlApprovalPromptOverride(url, intention);
            }

            AnsiConsole.WriteLine();

            var rows = new List<IRenderable>
            {
                new Markup($"[yellow]URL:[/] [white]{Markup.Escape(url)}[/]")
            };

            if (!string.IsNullOrWhiteSpace(intention))
            {
                rows.Add(new Markup(""));
                rows.Add(new Markup($"[cyan]Why:[/] [white]{Markup.Escape(intention)}[/]"));
            }

            rows.Add(new Markup(""));
            rows.Add(new Markup("[red]External URL access can bring non-local data into the session.[/]"));

            var panel = new Panel(new Rows(rows))
                .Header("[bold yellow] URL Approval Required [/]")
                .Border(BoxBorder.Heavy)
                .BorderColor(Color.Yellow);

            AnsiConsole.Write(panel);

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]What would you like to do?[/]")
                    .HighlightStyle(new Style(Color.Cyan1))
                    .AddChoices(new[]
                    {
                        "✅ Allow this URL",
                        "🌐 Allow all URLs for this session",
                        "❌ Deny"
                    }));

            if (choice.Contains("all URLs", StringComparison.OrdinalIgnoreCase))
            {
                return UrlApprovalResult.ApproveAllUrls;
            }

            if (choice.Contains("Allow this URL", StringComparison.OrdinalIgnoreCase))
            {
                return UrlApprovalResult.ApproveThisUrl;
            }

            return UrlApprovalResult.Deny;
        }
        finally
        {
            LiveThinkingIndicator.ResumeAfterApproval();
        }
    }

    /// <summary>
    /// Display an MCP tool approval prompt with server-scoped and (optionally) persistent options.
    /// </summary>
    /// <param name="serverName">Name of the MCP server.</param>
    /// <param name="toolName">Name of the MCP tool being invoked.</param>
    /// <param name="argumentsPreview">Pretty-printed arguments (JSON or key=value form). Optional.</param>
    /// <param name="role">When set (e.g. "monitoring" or "ticketing"), the persistent option is offered.</param>
    public static McpApprovalResult PromptMcpApproval(
        string serverName,
        string toolName,
        string? argumentsPreview,
        string? role)
    {
        LiveThinkingIndicator.PauseForApproval();
        try
        {
            if (McpApprovalPromptOverride != null)
            {
                return McpApprovalPromptOverride(serverName, toolName, argumentsPreview, role);
            }

            AnsiConsole.WriteLine();

            var rows = new List<IRenderable>
            {
                new Markup($"[yellow]MCP server:[/] [white]{Markup.Escape(serverName)}[/]"),
                new Markup($"[yellow]Tool:[/] [white]{Markup.Escape(toolName)}[/]")
            };

            if (!string.IsNullOrWhiteSpace(role))
            {
                rows.Add(new Markup($"[yellow]Role:[/] [white]{Markup.Escape(role)}[/]"));
            }

            if (!string.IsNullOrWhiteSpace(argumentsPreview))
            {
                rows.Add(new Markup(""));
                rows.Add(new Markup("[grey]Arguments:[/]"));
                rows.Add(new Markup($"[white]{Markup.Escape(argumentsPreview)}[/]"));
            }

            rows.Add(new Markup(""));
            rows.Add(new Markup("[grey]MCP tools run in the MCP server process; approving lets the AI invoke them on your behalf.[/]"));

            var panel = new Panel(new Rows(rows))
                .Header("[bold yellow] MCP Approval Required [/]")
                .Border(BoxBorder.Heavy)
                .BorderColor(Color.Yellow);

            AnsiConsole.Write(panel);

            var hasRole = !string.IsNullOrWhiteSpace(role);
            const string KeyOnce = "once";
            const string KeySession = "session";
            const string KeyPersist = "persist";
            const string KeyDeny = "deny";

            var labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KeyOnce] = "✅ Yes, run this once",
                [KeySession] = $"🛡️ Yes, allow all tools from '{serverName}' for this session"
            };
            if (hasRole)
            {
                labels[KeyPersist] = $"📌 Yes, always allow '{serverName}' (saved to settings)";
            }
            labels[KeyDeny] = "❌ No, skip";

            var labelToKey = labels.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]What would you like to do?[/]")
                    .HighlightStyle(new Style(Color.Cyan1))
                    .AddChoices(labels.Values));

            return labelToKey.TryGetValue(choice, out var key) ? key switch
            {
                KeyOnce => McpApprovalResult.ApproveOnce,
                KeySession => McpApprovalResult.ApproveServerForSession,
                KeyPersist => McpApprovalResult.ApproveServerPersist,
                _ => McpApprovalResult.Deny
            } : McpApprovalResult.Deny;
        }
        finally
        {
            LiveThinkingIndicator.ResumeAfterApproval();
        }
    }

    public static PostAnalysisAction PromptPostAnalysisAction()
    {
        LiveThinkingIndicator.PauseForApproval();
        try
        {
            if (PostAnalysisActionPromptOverride != null)
            {
                return PostAnalysisActionPromptOverride();
            }

            if (IsInputRedirectedResolver())
            {
                return PostAnalysisAction.Stop;
            }

            AnsiConsole.WriteLine();
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]What should TroubleScout do next?[/]")
                    .HighlightStyle(new Style(Color.Cyan1))
                    .AddChoices([
                        "Continue investigating",
                        "Apply the fix",
                        "Stop for now"
                    ]));

            return choice switch
            {
                "Continue investigating" => PostAnalysisAction.ContinueInvestigating,
                "Apply the fix" => PostAnalysisAction.ApplyFix,
                _ => PostAnalysisAction.Stop
            };
        }
        finally
        {
            LiveThinkingIndicator.ResumeAfterApproval();
        }
    }

    public static (string? Monitoring, string? Ticketing) PromptMcpRoleSelection(
        string? currentMonitoring,
        string? currentTicketing,
        IReadOnlyList<string> availableServers)
    {
        LiveThinkingIndicator.PauseForApproval();
        try
        {
            if (McpRolePromptOverride != null)
            {
                return McpRolePromptOverride(currentMonitoring, currentTicketing, availableServers);
            }

            var choices = new List<string> { "<None>" };
            choices.AddRange(availableServers
                .Where(server => !string.IsNullOrWhiteSpace(server))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(server => server, StringComparer.OrdinalIgnoreCase));

            var monitoring = PromptMcpRoleValue("monitoring", currentMonitoring, choices);
            var ticketing = PromptMcpRoleValue("ticketing", currentTicketing, choices);
            return (monitoring, ticketing);
        }
        finally
        {
            LiveThinkingIndicator.ResumeAfterApproval();
        }
    }

    private static string? PromptMcpRoleValue(string roleName, string? currentValue, IReadOnlyList<string> choices)
    {
        var title = string.IsNullOrWhiteSpace(currentValue)
            ? $"[yellow]Select the {roleName} MCP server[/]"
            : $"[yellow]Select the {roleName} MCP server (current: {Markup.Escape(currentValue)})[/]";

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(title)
                .HighlightStyle(new Style(Color.Cyan1))
                .AddChoices(choices));

        return selection.Equals("<None>", StringComparison.OrdinalIgnoreCase)
            ? null
            : selection;
    }

    private static void ShowCommandExplanation(
        string command,
        string reason,
        string? agentIntent = null,
        string? impact = null)
    {
        AnsiConsole.WriteLine();

        var grid = new Grid();
        grid.AddColumn(new GridColumn().PadRight(2));
        grid.AddColumn(new GridColumn());

        if (!string.IsNullOrWhiteSpace(agentIntent))
        {
            grid.AddRow("[cyan]Why:[/]", $"[white]{Markup.Escape(agentIntent)}[/]");
        }

        grid.AddRow("[grey]Command:[/]", $"[white]{Markup.Escape(command)}[/]");
        grid.AddRow("[grey]Safety rule:[/]", $"[white]{Markup.Escape(reason)}[/]");
        grid.AddRow("[grey]Impact:[/]", $"[yellow]{Markup.Escape(impact ?? "This command may modify system state, services, or configuration.")}[/]");

        var explanationPanel = new Panel(grid)
            .Header("[bold cyan] Command Explanation [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Cyan1);

        AnsiConsole.Write(explanationPanel);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Display multiple pending commands for batch approval
    /// </summary>
    public static List<int> PromptBatchApproval(IReadOnlyList<(string Command, string Reason)> commands)
    {
        LiveThinkingIndicator.PauseForApproval();
        try
        {
            AnsiConsole.WriteLine();
            
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Yellow)
                .AddColumn(new TableColumn("[bold]#[/]").Centered())
                .AddColumn(new TableColumn("[bold]Command[/]"))
                .AddColumn(new TableColumn("[bold]Reason[/]"));

            for (int i = 0; i < commands.Count; i++)
            {
                table.AddRow(
                    $"[cyan]{i + 1}[/]",
                    Markup.Escape(commands[i].Command),
                    Markup.Escape(commands[i].Reason));
            }

            var panel = new Panel(table)
                .Header("[bold yellow] Commands Requiring Approval [/]")
                .Border(BoxBorder.Heavy)
                .BorderColor(Color.Yellow);
            
            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();

            var approved = AnsiConsole.Prompt(
                new MultiSelectionPrompt<int>()
                    .Title("[yellow]Select commands to approve:[/]")
                    .NotRequired()
                    .PageSize(10)
                    .MoreChoicesText("[grey](Move up and down to reveal more commands)[/]")
                    .InstructionsText("[grey](Press [cyan]<space>[/] to toggle, [green]<enter>[/] to accept)[/]")
                    .AddChoices(Enumerable.Range(1, commands.Count)));

            return approved;
        }
        finally
        {
            LiveThinkingIndicator.ResumeAfterApproval();
        }
    }
}
