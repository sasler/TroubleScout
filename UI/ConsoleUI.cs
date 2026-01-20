using Spectre.Console;
using Spectre.Console.Rendering;

namespace TroubleScout.UI;

/// <summary>
/// Provides TUI components for the TroubleScout application
/// </summary>
public static class ConsoleUI
{
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
    public static void ShowStatusPanel(string targetServer, string connectionMode, bool copilotReady, string? model = null)
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
            new Markup("  [cyan]/connect[/] <server> - Connect to a different server")
        );

        var panel = new Panel(tips)
            .Header("[bold cyan] How to Use [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);
        
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Get user input with a styled prompt
    /// </summary>
    public static string GetUserInput()
    {
        AnsiConsole.Markup("[bold cyan]You[/] [grey]>[/] ");
        return Console.ReadLine() ?? string.Empty;
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
            // For streaming, write directly without markup processing
            Console.Write(text);
        }
        else
        {
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Start a new AI response block
    /// </summary>
    public static void StartAIResponse()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Markup("[bold green]TroubleScout[/] [grey]>[/] ");
    }

    /// <summary>
    /// End the AI response block
    /// </summary>
    public static void EndAIResponse()
    {
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
    public static string? PromptModelSelection(string currentModel)
    {
        AnsiConsole.MarkupLine($"[grey]Current Model:[/] [magenta]{Markup.Escape(currentModel)}[/]");
        AnsiConsole.WriteLine();
        
        var models = new[]
        {
            ("claude-sonnet-4.5", "Claude Sonnet 4.5", "1x rate"),
            ("claude-sonnet-4", "Claude Sonnet 4", "1x rate"),
            ("claude-haiku-4.5", "Claude Haiku 4.5", "0.33x rate"),
            ("gpt-5", "GPT-5", "1x rate")
        };
        
        var choices = models.Select(m => $"{m.Item2} ({m.Item1}) - {m.Item3}").ToList();
        choices.Add("Cancel");
        
        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select AI Model:[/]")
                .PageSize(6)
                .AddChoices(choices));
        
        if (selection == "Cancel")
            return null;
        
        // Extract model ID from selection
        var selectedIndex = choices.IndexOf(selection);
        return selectedIndex >= 0 && selectedIndex < models.Length ? models[selectedIndex].Item1 : null;
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
        var text = arguments != null 
            ? $"[grey]Executing:[/] [cyan]{Markup.Escape(toolName)}[/] [grey]{Markup.Escape(arguments)}[/]"
            : $"[grey]Executing:[/] [cyan]{Markup.Escape(toolName)}[/]";
        
        AnsiConsole.MarkupLine(text);
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
