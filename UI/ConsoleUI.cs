using System.Text;
using System.Text.RegularExpressions;
using GitHub.Copilot.SDK;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace TroubleScout.UI;

/// <summary>
/// Provides TUI components for the TroubleScout application
/// </summary>
public static class ConsoleUI
{
    private const string PromptMarkup = "[bold cyan]You[/] [grey]>[/] ";
    private const string PromptText = "You > ";
    private static int _lastInputRowCount = 1;

    /// <summary>
    /// Display the application banner
    /// </summary>
    public static void ShowBanner()
    {
        AnsiConsole.Clear();
        
        var banner = new FigletText("TroubleScout")
            .Color(Color.Cyan1)
            .Centered();
        
        AnsiConsole.Write(banner);
        
        AnsiConsole.Write(new Rule("[grey]AI-Powered Windows Server Troubleshooting Assistant[/]")
            .RuleStyle("cyan")
            .Centered());
        
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Display the status panel with connection and auth info
    /// </summary>
    public static void ShowStatusPanel(
        string targetServer,
        string connectionMode,
        bool copilotReady,
        string? model = null,
        IReadOnlyList<(string Label, string Value)>? usageFields = null)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().PadRight(2));
        grid.AddColumn(new GridColumn());
        
        var serverStatus = targetServer.Equals("localhost", StringComparison.OrdinalIgnoreCase) 
            ? "[green]localhost[/]" 
            : $"[yellow]{targetServer}[/]";
        
        var copilotStatus = copilotReady 
            ? "[green]Connected[/]" 
            : "[yellow]Connecting...[/]";
        
        grid.AddRow("[grey]Target Server:[/]", serverStatus);
        grid.AddRow("[grey]Connection Mode:[/]", $"[blue]{connectionMode}[/]");
        grid.AddRow("[grey]Copilot Status:[/]", copilotStatus);
        
        if (copilotReady)
        {
            var modelDisplay = !string.IsNullOrEmpty(model) && model != "default" 
                ? $"[magenta]{model}[/]" 
                : "[grey]default[/]";
            grid.AddRow("[grey]AI Model:[/]", modelDisplay);
        }

        if (usageFields != null)
        {
            foreach (var (label, value) in usageFields)
            {
                if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
                    continue;

                grid.AddRow(
                    $"[grey]{Markup.Escape(label)}:[/]",
                    $"[cyan]{Markup.Escape(value)}[/]");
            }
        }
        
        var panel = new Panel(grid)
            .Header("[bold cyan] Status [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);
        
        AnsiConsole.Write(panel);
        
        if (copilotReady)
        {
            AnsiConsole.MarkupLine("[grey]  Tip: Use --model <name> to select model, or /model for more info[/]");
        }
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Display available diagnostic categories
    /// </summary>
    public static void ShowDiagnosticCategories()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold cyan]Category[/]").Centered())
            .AddColumn(new TableColumn("[bold cyan]Description[/]"))
            .AddColumn(new TableColumn("[bold cyan]Example Commands[/]"));

        table.AddRow(
            "[green]System[/]",
            "OS info, uptime, hardware specs",
            "Get-ComputerInfo, Get-CimInstance");
        
        table.AddRow(
            "[yellow]Events[/]",
            "Windows Event Log analysis",
            "Get-EventLog, Get-WinEvent");
        
        table.AddRow(
            "[blue]Services[/]",
            "Windows service status",
            "Get-Service");
        
        table.AddRow(
            "[magenta]Processes[/]",
            "Running processes and resource usage",
            "Get-Process");
        
        table.AddRow(
            "[red]Performance[/]",
            "CPU, memory, disk metrics",
            "Get-Counter");
        
        table.AddRow(
            "[cyan]Network[/]",
            "Network adapters and configuration",
            "Get-NetAdapter, Get-NetIPAddress");
        
        table.AddRow(
            "[white]Storage[/]",
            "Disk space and volume health",
            "Get-Volume, Get-Disk");

        var panel = new Panel(table)
            .Header("[bold cyan] Diagnostic Categories [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);
        
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Display the welcome message and usage hints
    /// </summary>
    public static void ShowWelcomeMessage()
    {
        var tips = new Rows(
            new Markup("[grey]Describe your issue in natural language, and TroubleScout will investigate.[/]"),
            new Markup(""),
            new Markup("[grey]Examples:[/]"),
            new Markup("  [italic]\"The server is running slow and users are complaining about login times\"[/]"),
            new Markup("  [italic]\"Check why the SQL Server service keeps stopping\"[/]"),
            new Markup("  [italic]\"Analyze disk space and find what's using the most storage\"[/]"),
            new Markup(""),
            new Markup("[grey]Commands:[/]"),
            new Markup("  [cyan]/exit[/] or [cyan]/quit[/]  - End the session"),
            new Markup("  [cyan]/clear[/]          - Clear the screen"),
            new Markup("  [cyan]/status[/]         - Show connection status"),
            new Markup("  [cyan]/model[/]          - Change AI model"),
            new Markup("  [cyan]/connect[/] <server> - Connect to a different server"),
            new Markup("  [cyan]/history[/]        - Show PowerShell commands run this session"),
            new Markup("  [cyan]/help[/]           - Show help, examples, and categories"),
            new Markup(""),
            new Markup("[grey]Tip:[/] Press [cyan]Tab[/] to complete /commands")
        );

        var panel = new Panel(tips)
            .Header("[bold cyan] How to Use [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);
        
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Display full help including categories
    /// </summary>
    public static void ShowHelp()
    {
        ShowWelcomeMessage();
        ShowDiagnosticCategories();
    }

    /// <summary>
    /// Get user input with a styled prompt
    /// </summary>
    public static string GetUserInput(IReadOnlyList<string>? slashCommands = null)
    {
        AnsiConsole.Markup(PromptMarkup);
        if (slashCommands == null || slashCommands.Count == 0)
        {
            return Console.ReadLine() ?? string.Empty;
        }

        var buffer = new StringBuilder();
        var completionIndex = -1;
        List<string>? matches = null;
        _lastInputRowCount = 1;

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                if (Console.KeyAvailable)
                {
                    buffer.Append(' ');
                    completionIndex = -1;
                    matches = null;
                    RedrawInputLine(buffer.ToString());
                    continue;
                }

                Console.WriteLine();
                _lastInputRowCount = 1;
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                    completionIndex = -1;
                    matches = null;
                    RedrawInputLine(buffer.ToString());
                }
                continue;
            }

            if (key.Key == ConsoleKey.Tab)
            {
                var current = buffer.ToString();
                if (!current.StartsWith("/", StringComparison.OrdinalIgnoreCase) || current.Contains(' '))
                {
                    continue;
                }

                matches ??= slashCommands
                    .Where(cmd => cmd.StartsWith(current, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                {
                    Console.Beep();
                    matches = null;
                    continue;
                }

                completionIndex = (completionIndex + 1) % matches.Count;
                buffer.Clear();
                buffer.Append(matches[completionIndex]);
                RedrawInputLine(buffer.ToString());
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
                completionIndex = -1;
                matches = null;
                Console.Write(key.KeyChar);
                UpdateInputRowCount(buffer.Length);
            }
        }
    }

    private static void RedrawInputLine(string text)
    {
        var width = Math.Max(1, Console.BufferWidth);
        var currentRows = GetInputRowCount(width, text.Length);
        var rowsToClear = Math.Max(_lastInputRowCount, currentRows);

        ClearInputRows(rowsToClear, width);
        AnsiConsole.Markup(PromptMarkup);
        Console.Write(text);
        _lastInputRowCount = currentRows;
    }

    private static void UpdateInputRowCount(int textLength)
    {
        var width = Math.Max(1, Console.BufferWidth);
        _lastInputRowCount = GetInputRowCount(width, textLength);
    }

    private static int GetInputRowCount(int width, int textLength)
    {
        var totalLength = PromptText.Length + textLength;
        if (totalLength <= 0)
        {
            return 1;
        }

        return (totalLength + width - 1) / width;
    }

    private static void ClearInputRows(int rows, int width)
    {
        var startRow = Math.Max(0, Console.CursorTop - Math.Max(0, rows - 1));
        Console.SetCursorPosition(0, startRow);

        for (var i = 0; i < rows; i++)
        {
            Console.Write(new string(' ', width));
            if (i < rows - 1)
            {
                Console.WriteLine();
            }
        }

        Console.SetCursorPosition(0, startRow);
    }

    /// <summary>
    /// Display PowerShell command history for the session
    /// </summary>
    public static void ShowCommandHistory(IReadOnlyList<string> commands)
    {
        if (commands.Count == 0)
        {
            ShowInfo("No PowerShell commands have been executed in this session.");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]#[/]").Centered())
            .AddColumn(new TableColumn("[bold]Command[/]"));

        for (int i = 0; i < commands.Count; i++)
        {
            table.AddRow(
                new Markup($"[cyan]{i + 1}[/]"),
                new Markup(HighlightPowerShellMarkup(commands[i])));
        }

        var panel = new Panel(table)
            .Header("[bold cyan] PowerShell History [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private static readonly Regex PowerShellTokenRegex = new(
        "(?<string>'([^'\\\\]|\\\\.)*'|\"([^\"\\\\]|\\\\.)*\")" +
        "|(?<variable>\\$\\{[^}]+\\}|\\$[A-Za-z_][\\w:]*|\\$\\([^)]+\\))" +
        "|(?<keyword>\\b(?:if|else|elseif|foreach|for|while|do|switch|try|catch|finally|throw|return|function|param|begin|process|end|break|continue|class|enum|using|in|default)\\b)" +
        "|(?<op>(?:-eq|-ne|-gt|-ge|-lt|-le|-and|-or|-not)\\b|\\|\\||&&|[|;])" +
        "|(?<cmdlet>\\b[A-Za-z]+-[A-Za-z][A-Za-z0-9]*\\b)" +
        "|(?<param>-[A-Za-z][\\w-]*)" +
        "|(?<number>\\b\\d+(?:\\.\\d+)?\\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string HighlightPowerShellMarkup(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var builder = new StringBuilder(input.Length + 16);
        int lastIndex = 0;

        foreach (Match match in PowerShellTokenRegex.Matches(input))
        {
            if (!match.Success)
                continue;

            if (match.Index > lastIndex)
            {
                builder.Append(Markup.Escape(input.Substring(lastIndex, match.Index - lastIndex)));
            }

            var tokenText = Markup.Escape(match.Value);
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
            builder.Append(Markup.Escape(input.Substring(lastIndex)));
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

    /// <summary>
    /// Display a thinking/processing indicator
    /// </summary>
    public static IDisposable ShowThinking(string message = "Analyzing...")
    {
        var status = AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"));
        
        return new ThinkingContext(status, message);
    }

    /// <summary>
    /// Creates a live thinking indicator that can be updated and stopped
    /// </summary>
    public static LiveThinkingIndicator CreateLiveThinkingIndicator()
    {
        return new LiveThinkingIndicator();
    }

    /// <summary>
    /// Run an async task with an animated spinner
    /// </summary>
    public static async Task<T> RunWithSpinnerAsync<T>(string message, Func<Action<string>, Task<T>> action)
    {
        T result = default!;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync(message, async ctx =>
            {
                result = await action(status => ctx.Status(status));
            });
        return result;
    }

    /// <summary>
    /// Display an AI response with streaming support
    /// </summary>
    public static void WriteAIResponse(string text, bool isComplete = false)
    {
        if (!isComplete)
        {
            // Buffer the text for markdown processing
            _streamBuffer.Append(text);
            
            // Process and output any complete segments
            FlushStreamBuffer(forceFlush: false);
        }
        else
        {
            Console.WriteLine();
        }
    }

    private static readonly StringBuilder _streamBuffer = new();
    private static bool _inBold;
    private static bool _inCode;
    private static bool _inCodeBlock;

    /// <summary>
    /// Reset the stream buffer state (call at start of new response)
    /// </summary>
    public static void ResetStreamBuffer()
    {
        _streamBuffer.Clear();
        _inBold = false;
        _inCode = false;
        _inCodeBlock = false;
    }

    /// <summary>
    /// Flush the stream buffer, converting markdown to ANSI
    /// </summary>
    private static void FlushStreamBuffer(bool forceFlush)
    {
        var content = _streamBuffer.ToString();
        if (string.IsNullOrEmpty(content)) return;

        var output = new StringBuilder();
        int i = 0;

        while (i < content.Length)
        {
            // Check for code block (```)
            if (i + 2 < content.Length && content[i] == '`' && content[i + 1] == '`' && content[i + 2] == '`')
            {
                // Only process if we can see the end or are forcing
                if (_inCodeBlock)
                {
                    output.Append("\x1b[0m"); // Reset
                    _inCodeBlock = false;
                    i += 3;
                    // Skip optional language identifier on opening
                    while (i < content.Length && content[i] != '\n' && content[i] != '\r')
                        i++;
                    continue;
                }
                else
                {
                    output.Append("\x1b[90m"); // Gray for code block
                    _inCodeBlock = true;
                    i += 3;
                    // Skip optional language identifier
                    while (i < content.Length && content[i] != '\n' && content[i] != '\r')
                        i++;
                    continue;
                }
            }

            // Inside code block, just output as-is
            if (_inCodeBlock)
            {
                output.Append(content[i]);
                i++;
                continue;
            }

            // Check for bold (**text**)
            if (i + 1 < content.Length && content[i] == '*' && content[i + 1] == '*')
            {
                if (_inBold)
                {
                    output.Append("\x1b[0m"); // Reset
                    _inBold = false;
                }
                else
                {
                    output.Append("\x1b[1;33m"); // Bold yellow
                    _inBold = true;
                }
                i += 2;
                continue;
            }

            // Check for inline code (`text`)
            if (content[i] == '`')
            {
                if (_inCode)
                {
                    output.Append("\x1b[0m"); // Reset
                    _inCode = false;
                }
                else
                {
                    output.Append("\x1b[36m"); // Cyan for inline code
                    _inCode = true;
                }
                i++;
                continue;
            }

            // Check for headers (## at start of line)
            if (content[i] == '#' && (i == 0 || content[i - 1] == '\n'))
            {
                int headerLevel = 0;
                while (i < content.Length && content[i] == '#')
                {
                    headerLevel++;
                    i++;
                }
                // Skip space after #
                if (i < content.Length && content[i] == ' ')
                    i++;
                
                // Output header formatting
                output.Append("\x1b[1;36m"); // Bold cyan for headers
                
                // Find end of line and output
                while (i < content.Length && content[i] != '\n')
                {
                    output.Append(content[i]);
                    i++;
                }
                output.Append("\x1b[0m"); // Reset after header
                continue;
            }

            // Check for bullet points
            if (content[i] == '-' && i + 1 < content.Length && content[i + 1] == ' ' && 
                (i == 0 || content[i - 1] == '\n'))
            {
                output.Append("\x1b[32m-\x1b[0m"); // Green bullet
                i++;
                continue;
            }

            // Check for numbered lists
            if (char.IsDigit(content[i]) && i + 1 < content.Length && content[i + 1] == '.' &&
                (i == 0 || content[i - 1] == '\n'))
            {
                output.Append($"\x1b[32m{content[i]}.\x1b[0m"); // Green number
                i += 2;
                continue;
            }

            // Regular character
            output.Append(content[i]);
            i++;
        }

        // Write the processed output
        Console.Write(output.ToString());
        _streamBuffer.Clear();
    }

    /// <summary>
    /// Start a new AI response block
    /// </summary>
    public static void StartAIResponse()
    {
        ResetStreamBuffer(); // Reset markdown parsing state
        EnsureLineBreak();
        AnsiConsole.Markup("[bold green]TroubleScout[/] [grey]>[/] ");
    }

    /// <summary>
    /// End the AI response block
    /// </summary>
    public static void EndAIResponse()
    {
        // Ensure any remaining formatting is reset
        Console.Write("\x1b[0m");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Display a command that requires approval
    /// </summary>
    public static bool PromptCommandApproval(string command, string reason)
    {
        AnsiConsole.WriteLine();
        
        var panel = new Panel(new Rows(
            new Markup($"[yellow]Command:[/] [white]{Markup.Escape(command)}[/]"),
            new Markup(""),
            new Markup($"[grey]Reason: {Markup.Escape(reason)}[/]"),
            new Markup(""),
            new Markup("[red]This command can modify system state.[/]")
        ))
        .Header("[bold yellow] Approval Required [/]")
        .Border(BoxBorder.Heavy)
        .BorderColor(Color.Yellow);
        
        AnsiConsole.Write(panel);
        
        return AnsiConsole.Confirm("[yellow]Do you want to execute this command?[/]", false);
    }

    /// <summary>
    /// Display multiple pending commands for batch approval
    /// </summary>
    public static List<int> PromptBatchApproval(IReadOnlyList<(string Command, string Reason)> commands)
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

    /// <summary>
    /// Display AI model selection prompt and return the selected model
    /// </summary>
    public static string? PromptModelSelection(string currentModel, IReadOnlyList<ModelInfo> models)
    {
        AnsiConsole.MarkupLine($"[grey]Current Model:[/] [magenta]{Markup.Escape(currentModel)}[/]");
        AnsiConsole.WriteLine();

        var choices = models
            .Select(m => new ModelChoice(m, GetDisplayName(m, currentModel), GetRateLabel(m)))
            .ToList();

        var maxNameLength = choices.Count == 0 ? 0 : choices.Max(c => c.DisplayName.Length);
        choices.Add(ModelChoice.Cancel);

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<ModelChoice>()
                .Title("[cyan]Select AI Model:[/]")
                .PageSize(12)
                .UseConverter(choice =>
                {
                    if (choice.IsCancel)
                        return "Cancel";

                    var name = choice.DisplayName.PadRight(maxNameLength);
                    var rate = Markup.Escape(choice.RateLabel);
                    return $"{Markup.Escape(name)}  [grey]{rate}[/]";
                })
                .AddChoices(choices));

        return selection.IsCancel ? null : selection.Model.Id;
    }

    private static string GetDisplayName(ModelInfo model, string currentModel)
    {
        var isDefault = model.Id.Equals(currentModel, StringComparison.OrdinalIgnoreCase);
        return isDefault ? $"{model.Name} (default)" : model.Name;
    }

    private static string GetRateLabel(ModelInfo model)
    {
        return model.Billing != null ? $"{model.Billing.Multiplier:0.##}x" : "n/a";
    }

    private sealed record ModelChoice(ModelInfo Model, string DisplayName, string RateLabel)
    {
        public static ModelChoice Cancel { get; } = new(new ModelInfo(), "Cancel", "");
        public bool IsCancel => ReferenceEquals(this, Cancel);
    }

    /// <summary>
    /// Display an error message
    /// </summary>
    public static void ShowError(string title, string message)
    {
        var panel = new Panel(new Markup($"[red]{Markup.Escape(message)}[/]"))
            .Header($"[bold red] {title} [/]")
            .Border(BoxBorder.Heavy)
            .BorderColor(Color.Red);
        
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Display a success message
    /// </summary>
    public static void ShowSuccess(string message)
    {
        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");
    }

    /// <summary>
    /// Display an info message
    /// </summary>
    public static void ShowInfo(string message)
    {
        AnsiConsole.MarkupLine($"[blue]ℹ[/] {Markup.Escape(message)}");
    }

    /// <summary>
    /// Display a warning message
    /// </summary>
    public static void ShowWarning(string message)
    {
        AnsiConsole.MarkupLine($"[yellow]⚠[/] {Markup.Escape(message)}");
    }

    /// <summary>
    /// Display tool execution info
    /// </summary>
    public static void ShowToolExecution(string toolName, string? arguments = null)
    {
        EnsureLineBreak();
        var text = arguments != null 
            ? $"[grey]Executing:[/] [cyan]{Markup.Escape(toolName)}[/] [grey]{Markup.Escape(arguments)}[/]"
            : $"[grey]Executing:[/] [cyan]{Markup.Escape(toolName)}[/]";
        
        AnsiConsole.MarkupLine(text);
    }

    /// <summary>
    /// Display PowerShell command execution info
    /// </summary>
    public static void ShowCommandExecution(string command, string targetServer)
    {
        EnsureLineBreak();
        var serverDisplay = targetServer.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? "[green]localhost[/]"
            : $"[yellow]{Markup.Escape(targetServer)}[/]";
        
        AnsiConsole.MarkupLine($"[grey]>[/] [dim]Running on {serverDisplay}:[/] [cyan]{Markup.Escape(command)}[/]");
    }

    /// <summary>
    /// Display a horizontal rule
    /// </summary>
    public static void ShowRule(string? title = null)
    {
        if (string.IsNullOrEmpty(title))
        {
            AnsiConsole.Write(new Rule().RuleStyle("grey"));
        }
        else
        {
            AnsiConsole.Write(new Rule($"[grey]{title}[/]").RuleStyle("grey"));
        }
    }

    private static void EnsureLineBreak()
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        try
        {
            if (Console.CursorLeft != 0)
            {
                Console.WriteLine();
            }
        }
        catch (IOException)
        {
            // Ignore cursor checks when no console is attached (e.g., test runs).
        }
    }

    /// <summary>
    /// Context manager for the thinking indicator
    /// </summary>
    private class ThinkingContext : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _spinnerTask;

        public ThinkingContext(Status status, string message)
        {
            _spinnerTask = Task.Run(async () =>
            {
                try
                {
                    await status.StartAsync(message, async ctx =>
                    {
                        while (!_cts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(100, _cts.Token);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    // Expected when disposed
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _spinnerTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Ignore timeout
            }
            _cts.Dispose();
        }
    }
}

/// <summary>
/// A live thinking indicator that shows animated status and can be updated or stopped
/// </summary>
public class LiveThinkingIndicator : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private string _currentStatus = "Thinking...";
    private bool _isRunning;
    private bool _hasStartedResponse;
    private readonly object _lock = new();
    private Task? _spinnerTask;
    private static readonly string[] SpinnerFrames = [".", "..", "...", "....", "...."];
    private int _spinnerIndex;

    public void Start()
    {
        lock (_lock)
        {
            if (_isRunning) return;
            _isRunning = true;
            _hasStartedResponse = false;
        }

        _spinnerTask = Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    lock (_lock)
                    {
                        if (_hasStartedResponse) break;
                        
                        // Clear the current line and write status
                        Console.Write($"\r\x1b[K[cyan]{_currentStatus}{SpinnerFrames[_spinnerIndex]}[/]".Replace("[cyan]", "\u001b[36m").Replace("[/]", "\u001b[0m"));
                        _spinnerIndex = (_spinnerIndex + 1) % SpinnerFrames.Length;
                    }
                    await Task.Delay(200, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        });
    }

    public void UpdateStatus(string status)
    {
        lock (_lock)
        {
            _currentStatus = status;
        }
    }

    public void ShowToolExecution(string toolName)
    {
        lock (_lock)
        {
            _currentStatus = $"Running {toolName}";
        }
    }

    public void StopForResponse()
    {
        lock (_lock)
        {
            if (_hasStartedResponse) return;
            _hasStartedResponse = true;
            
            // Clear the status line
            Console.Write("\r\x1b[K");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        
        lock (_lock)
        {
            if (!_hasStartedResponse)
            {
                // Clear the status line if we haven't started a response
                Console.Write("\r\x1b[K");
            }
        }
        
        try
        {
            _spinnerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore
        }
        _cts.Dispose();
    }
}
