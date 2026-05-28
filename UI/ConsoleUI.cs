using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GitHub.Copilot;
using Spectre.Console;
using Spectre.Console.Rendering;
using TroubleScout.Services;

namespace TroubleScout.UI;

/// <summary>
/// Result of a user approval prompt
/// </summary>
public enum ApprovalResult
{
    Approved,
    Denied
}

public enum UrlApprovalResult
{
    ApproveThisUrl,
    ApproveAllUrls,
    Deny
}

public enum McpApprovalResult
{
    ApproveOnce,
    ApproveServerForSession,
    ApproveServerPersist,
    Deny
}

public enum TerminalProgressState
{
    Hidden = 0,
    Normal = 1,
    Error = 2,
    Indeterminate = 3,
    Warning = 4
}

/// <summary>
/// Data for the compact status bar shown after each AI response
/// </summary>
public sealed record StatusBarInfo(
    string? Model,
    string? Provider,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    int ToolInvocations,
    string? SessionId)
{
    public string? ReasoningEffort { get; init; }
    public long? SessionInputTokens { get; init; }
    public long? SessionOutputTokens { get; init; }
    public string? SessionCostEstimate { get; init; }
    public int SubagentCalls { get; init; }
    public long? SubagentTokens { get; init; }
    public static StatusBarInfo Empty => new(null, null, null, null, null, 0, null);
}

/// <summary>
/// Provides TUI components for the TroubleScout application
/// </summary>
public static partial class ConsoleUI
{
    private static ExecutionMode _currentExecutionMode = ExecutionMode.Strict;
    private static int _lastInputRowCount = 1;
    private static int _lastSuggestionRowCount;
    private static int _lastSuggestionRowOffset = 1;
    private const int MaxPromptInputLength = 4000;
    private static readonly List<string> _promptHistory = new();
    private static readonly MarkdownStreamRenderer _markdownRenderer = new();
    private const int MaxPromptHistorySize = 100;
    internal static Func<bool> IsInputRedirectedResolver { get; set; } = static () => Console.IsInputRedirected;
    internal static Func<bool> IsOutputRedirectedResolver { get; set; } = static () => Console.IsOutputRedirected;
    internal static Func<bool> IsWindowsTerminalSessionResolver { get; set; } =
        static () => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WT_SESSION"));
    internal static Func<int> ConsoleWindowWidthResolver { get; set; } = static () =>
    {
        try
        {
            var width = Console.WindowWidth;
            // Non-interactive / redirected output (CI, piped) frequently reports
            // 0 or a negative value. Treat those as "unknown" and fall back to a
            // generous default so the full status bar still renders. Width-aware
            // elision only kicks in when we have a real positive measurement.
            return width > 0 ? width : 120;
        }
        catch { return 120; }
    };

    /// <summary>
    /// The active theme for app chrome (banner, panels, status bar). One of
    /// "dark" and "mono". Default is "dark". Mono strips Spectre color
    /// tags from chrome surfaces; it does NOT retint Markdown response
    /// rendering, reasoning ANSI, or the live spinner.
    /// </summary>
    public static string CurrentTheme { get; set; } = "dark";

    internal static bool IsMonochromeTheme()
        => string.Equals(CurrentTheme, "mono", StringComparison.OrdinalIgnoreCase);
    internal static Func<string, string, string?, string?, ApprovalResult>? CommandApprovalPromptOverride { get; set; }
    internal static Func<string, string?, UrlApprovalResult>? UrlApprovalPromptOverride { get; set; }
    internal static Func<string, string, string?, string?, McpApprovalResult>? McpApprovalPromptOverride { get; set; }
    internal static Func<string?, string?, IReadOnlyList<string>, (string? Monitoring, string? Ticketing)>? McpRolePromptOverride { get; set; }
    private static readonly object _terminalStateLock = new();
    private static readonly object _liveOutputLock = new();
    private static string? _originalConsoleTitle;
    private static bool _capturedOriginalConsoleTitle;

    public static void SetExecutionMode(ExecutionMode mode)
    {
        _currentExecutionMode = mode;
    }

    private static void WithLiveOutputLock(Action action)
    {
        lock (_liveOutputLock)
        {
            action();
        }
    }


    /// <summary>
    /// Display the application banner
    /// </summary>
    public static void ShowBanner(string? version = null)
    {
        SetTerminalTitle("TroubleScout");
        AnsiConsole.Clear();
        
        var banner = new FigletText("TroubleScout")
            .Color(Color.Cyan1)
            .Centered();
        
        AnsiConsole.Write(banner);
        
        AnsiConsole.Write(new Rule("[grey]AI-Powered Windows Server Troubleshooting Assistant[/]")
            .RuleStyle("cyan")
            .Centered());

        if (!string.IsNullOrWhiteSpace(version) && !version.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[grey]Version:[/] [cyan]{Markup.Escape(version)}[/]");
        }
        
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Show immediate startup progress before the initialization spinner
    /// </summary>
    public static void ShowStartupProgress(string targetServer)
    {
        AnsiConsole.MarkupLine($"[grey]Target:[/] [white]{Markup.Escape(targetServer)}[/]  [grey]— preparing session...[/]");
    }

    /// <summary>
    /// Display the status panel with connection and auth info.
    /// Uses a compact Table layout for a cleaner appearance.
    /// </summary>
    public static void ShowStatusPanel(
        string targetServer,
        string connectionMode,
        bool copilotReady,
        string? model = null,
        ExecutionMode executionMode = ExecutionMode.Strict,
        IReadOnlyList<(string Label, string Value)>? usageFields = null,
        IReadOnlyList<string>? additionalTargets = null,
        string? defaultSessionTarget = null)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[grey]Property[/]").NoWrap())
            .AddColumn(new TableColumn("[grey]Value[/]"));

        // --- Target server(s) ---
        if (additionalTargets?.Count > 0)
        {
            var allServers = new List<string> { targetServer };
            allServers.AddRange(additionalTargets);
            var serverMarkups = allServers.Select(s =>
                s.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    ? "[green]localhost[/]"
                    : $"[yellow]{Markup.Escape(s)}[/]");
            table.AddRow("[bold]Target Servers[/]", string.Join(", ", serverMarkups));
        }
        else
        {
            var serverColor = targetServer.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? "[green]localhost[/]"
                : $"[yellow]{Markup.Escape(targetServer)}[/]";
            table.AddRow("[bold]Target[/]", serverColor);
        }

        // --- Connection + Mode on same conceptual level ---
        table.AddRow("[bold]Connection[/]", $"[blue]{Markup.Escape(connectionMode)}[/]");
        if (!string.IsNullOrWhiteSpace(defaultSessionTarget))
        {
            var defaultSessionDisplay = defaultSessionTarget.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? "[green]localhost[/]"
                : $"[yellow]{Markup.Escape(defaultSessionTarget)}[/]";
            table.AddRow("[bold]Default session[/]", defaultSessionDisplay);
        }

        table.AddRow("[bold]Mode[/]", GetExecutionModeMarkup(executionMode));

        // --- AI status ---
        var copilotStatus = copilotReady
            ? "[green]Connected[/]"
            : "[yellow]Not ready[/]";
        var modelDisplay = copilotReady && !string.IsNullOrEmpty(model) && model != "default"
            ? $"[magenta]{Markup.Escape(model)}[/]"
            : copilotReady ? "[grey]default[/]" : null;
        var aiValue = modelDisplay != null ? $"{copilotStatus} — {modelDisplay}" : copilotStatus;
        table.AddRow("[bold]AI[/]", aiValue);

        // --- Usage / capability fields ---
        if (usageFields != null)
        {
            foreach (var (label, value) in usageFields)
            {
                if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
                    continue;

                if (label == StatusSectionSeparator)
                {
                    table.AddEmptyRow();
                    continue;
                }

                var markup = IsContextField(label)
                    ? FormatContextValueMarkup(value)
                    : $"[cyan]{Markup.Escape(value)}[/]";

                table.AddRow(
                    $"[grey]{Markup.Escape(label)}[/]",
                    markup);
            }
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    internal const string StatusSectionSeparator = "\x1F";

    private static bool IsContextField(string label)
    {
        return label.Equals("Context", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Colorize the combined context usage value based on fill percentage.
    /// Input format: "25,000/100,000 (25%)"
    /// </summary>
    internal static string FormatContextValueMarkup(string value)
    {
        var match = Regex.Match(value, @"\((\d+(?:\.\d+)?)%\)");
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            var color = pct switch
            {
                >= 90 => "red",
                >= 70 => "yellow",
                _ => "green"
            };
            return $"[bold {color}]{Markup.Escape(value)}[/]";
        }

        return $"[cyan]{Markup.Escape(value)}[/]";
    }

    /// <summary>
    /// Display available diagnostic categories
    /// </summary>
    public static void ShowDiagnosticCategories()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold cyan]Area[/]").Centered())
            .AddColumn(new TableColumn("[bold cyan]Description[/]"))
            .AddColumn(new TableColumn("[bold cyan]Example Commands[/]"));

        table.AddRow(
            "System",
            "OS info, uptime, hardware specs",
            "Get-ComputerInfo, Get-CimInstance");
        
        table.AddRow(
            "Events",
            "Windows Event Log analysis",
            "Get-EventLog, Get-WinEvent");
        
        table.AddRow(
            "Services",
            "Windows service status",
            "Get-Service");
        
        table.AddRow(
            "Processes",
            "Running processes and resource usage",
            "Get-Process");
        
        table.AddRow(
            "Performance",
            "CPU, memory, disk metrics",
            "Get-Counter");
        
        table.AddRow(
            "Network",
            "Network adapters and configuration",
            "Get-NetAdapter, Get-NetIPAddress");
        
        table.AddRow(
            "Storage",
            "Disk space and volume health",
            "Get-Volume, Get-Disk");

        var content = new Rows(
            new Markup("[grey]These are troubleshooting areas the assistant uses as guidance. They are not strict command groups.[/]"),
            new Markup(""),
            table
        );

        var panel = new Panel(content)
            .Header("[bold cyan] Troubleshooting Areas [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);
        
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Display a compact welcome hint after the status table.
    /// The full command reference lives in /help.
    /// </summary>
    public static void ShowWelcomeMessage(string? mcpRoleHint = null)
    {
        AnsiConsole.MarkupLine("[grey]Describe your issue and TroubleScout will investigate.[/]");
        AnsiConsole.MarkupLine("[grey]Type[/] [cyan]/help[/] [grey]for commands,[/] [cyan]/status[/] [grey]for session info,[/] [cyan]/exit[/] [grey]to quit.[/]");
        AnsiConsole.MarkupLine("[grey]After findings, TroubleScout will ask whether to continue investigating, apply the fix, or stop.[/]");
        if (!string.IsNullOrWhiteSpace(mcpRoleHint))
        {
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(mcpRoleHint)}[/]");
        }
    }

    /// <summary>
    /// Display CLI usage help for command-line invocation (--help / -h)
    /// </summary>
    public static void ShowCliHelp(string? version = null)
    {
        AnsiConsole.MarkupLine("[bold cyan]TroubleScout[/] – AI-Powered Windows Server Troubleshooting Assistant");
        if (!string.IsNullOrWhiteSpace(version) && !version.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[grey]Version:[/] [cyan]{Markup.Escape(version)}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]USAGE[/]");
        AnsiConsole.MarkupLine("  [cyan]troublescout[/] [grey][[options]][/]");
        AnsiConsole.WriteLine();

        var optionsTable = new Table()
            .Border(TableBorder.None)
            .AddColumn(new TableColumn("[bold]Option[/]").NoWrap().Width(38))
            .AddColumn(new TableColumn("[bold]Description[/]"));

        optionsTable.AddRow("[cyan]-s[/], [cyan]--server[/] [grey]<hostname>[/]", "Target server(s) hostname or IP. Repeat for multiple: -s srv1 -s srv2 (default: localhost)");
        optionsTable.AddRow("[cyan]-p[/], [cyan]--prompt[/] [grey]<text>[/]", "Run a single prompt in headless mode and exit");
        optionsTable.AddRow("[cyan]-m[/], [cyan]--model[/] [grey]<model-id>[/]", "AI model to use (e.g. gpt-4.1, gpt-5-mini)");
        optionsTable.AddRow("[cyan]--subagent-model[/] [grey]<model-id>[/]", "Model used for delegated evidence collection");
        optionsTable.AddRow("[cyan]--jea[/] [grey]<server> <configurationName>[/]", "Preconnect a single startup JEA endpoint session");
        optionsTable.AddRow("[cyan]--mode[/] [grey]<strict|auto>[/]", "PowerShell execution mode (default: strict)");
        optionsTable.AddRow("[cyan]--mcp-config[/] [grey]<path>[/]", "Path to MCP server config JSON file");
        optionsTable.AddRow("[cyan]--skills-dir[/] [grey]<path>[/]", "Directory containing Copilot skill files (repeatable)");
        optionsTable.AddRow("[cyan]--disable-skill[/] [grey]<name>[/]", "Disable a specific skill by name (repeatable)");
        optionsTable.AddRow("[cyan]--byok-openai[/]", "Enable Bring-Your-Own-Key OpenAI-compatible mode");
        optionsTable.AddRow("[cyan]--no-byok[/]", "Force GitHub Copilot provider (ignores saved BYOK provider selection)");
        optionsTable.AddRow("[cyan]--openai-base-url[/] [grey]<url>[/]", "Base URL for BYOK OpenAI-compatible endpoint");
        optionsTable.AddRow("[cyan]--openai-api-key[/] [grey]<key>[/]", "API key for BYOK OpenAI-compatible endpoint");
        optionsTable.AddRow("[cyan]-d[/], [cyan]--debug[/]", "Enable debug/diagnostic output");
        optionsTable.AddRow("[cyan]-v[/], [cyan]--version[/]", "Print version and exit");
        optionsTable.AddRow("[cyan]-h[/], [cyan]--help[/]", "Show this help and exit");

        AnsiConsole.MarkupLine("[bold]OPTIONS[/]");
        AnsiConsole.Write(optionsTable);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold]EXAMPLES[/]");
        AnsiConsole.MarkupLine("  [grey]# Launch the interactive TUI against a remote server[/]");
        AnsiConsole.MarkupLine("  [cyan]troublescout[/] --server web01");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [grey]# Connect to multiple servers at startup[/]");
        AnsiConsole.MarkupLine("  [cyan]troublescout[/] -s web01 -s web02 -s db01");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [grey]# Run a single headless prompt and exit[/]");
        AnsiConsole.MarkupLine("  [cyan]troublescout[/] --server web01 --prompt [grey]\"Check disk space\"[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [grey]# Use a specific AI model with automatic review for unknown read-only commands[/]");
        AnsiConsole.MarkupLine("  [cyan]troublescout[/] --model gpt-4.1 --mode auto");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [grey]# Preconnect a JEA endpoint before starting[/]");
        AnsiConsole.MarkupLine("  [cyan]troublescout[/] --server server1 --jea server2 JEA-Admins");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [grey]# Use BYOK OpenAI-compatible endpoint[/]");
        AnsiConsole.MarkupLine("  [cyan]troublescout[/] --byok-openai --openai-api-key $env:MY_KEY");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[grey]Use [cyan]/help[/] inside the interactive session for TUI command reference.[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Display full help including categories
    /// </summary>
    public static void ShowHelp()
    {
        var commandTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold cyan]Command[/]").NoWrap())
            .AddColumn(new TableColumn("[bold cyan]Description[/]"));

        foreach (var command in SlashCommandRegistry.Commands)
        {
            commandTable.AddRow(
                $"[cyan]{Markup.Escape(command.Usage)}[/]",
                Markup.Escape(command.Summary));
        }

        var helpPanel = new Panel(new Rows(
            new Markup("[grey]Interactive command reference[/]"),
            new Markup(""),
            commandTable,
            new Markup(""),
            new Markup("[grey]After diagnosis or approved changes, TroubleScout asks whether to continue investigating, apply the fix, or stop.[/]"),
            new Markup(""),
            new Markup("[grey]Tip:[/] Type [cyan]/[/] and keep typing to filter commands live. Press [cyan]Tab[/] to complete.")
        ))
        .Header("[bold cyan] Help [/]")
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Grey);

        AnsiConsole.Write(helpPanel);
        AnsiConsole.WriteLine();

        ShowDiagnosticCategories();
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
                new Markup(PowerShellSyntaxHighlighter.HighlightPowerShellMarkup(commands[i])));
        }

        var panel = new Panel(table)
            .Header("[bold cyan] PowerShell History [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);

        AnsiConsole.Write(panel);
        AnsiConsole.MarkupLine("[grey]Tip:[/] Run [cyan]/report[/] for full per-prompt details including outputs.");
        AnsiConsole.WriteLine();
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
        SetWindowsTerminalProgress(TerminalProgressState.Indeterminate);
        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync(message, async ctx =>
                {
                    result = await action(status => ctx.Status(status));
                });
        }
        finally
        {
            ClearWindowsTerminalProgress();
        }
        return result;
    }

    /// <summary>
    /// Display an AI response with streaming support
    /// </summary>
    public static void WriteAIResponse(string text, bool isComplete = false)
    {
        WithLiveOutputLock(() => _markdownRenderer.WriteAIResponse(text, isComplete));
    }

    /// <summary>
    /// Reset the stream buffer state (call at start of new response)
    /// </summary>
    public static void ResetStreamBuffer()
    {
        WithLiveOutputLock(_markdownRenderer.ResetStreamBuffer);
    }

    internal static Table? ParseMarkdownTable(IReadOnlyList<string> lines)
    {
        return _markdownRenderer.ParseMarkdownTable(lines);
    }

    /// <summary>
    /// Start a new AI response block
    /// </summary>
    public static void StartAIResponse()
    {
        WithLiveOutputLock(_markdownRenderer.StartAIResponse);
    }

    /// <summary>
    /// End the AI response block
    /// </summary>
    public static void EndAIResponse()
    {
        WithLiveOutputLock(_markdownRenderer.EndAIResponse);
    }

    /// <summary>
    /// Display AI model selection prompt and return the selected model
    /// </summary>
    public static string? PromptModelSelection(string currentModel, IReadOnlyList<ModelInfo> models)
        => ModelPickerUI.PromptModelSelection(currentModel, models);

    internal static ModelSelectionEntry? PromptModelSelection(
        string currentModel,
        IReadOnlyList<ModelSelectionEntry> entries)
        => ModelPickerUI.PromptModelSelection(currentModel, entries);

    public static string? PromptReasoningEffort(
        string? currentReasoningEffort,
        IReadOnlyList<string> supportedEfforts,
        string? defaultReasoningEffort)
        => ModelPickerUI.PromptReasoningEffort(currentReasoningEffort, supportedEfforts, defaultReasoningEffort);

    internal static string FormatCompactTokenCount(int value)
        => ModelPickerUI.FormatCompactTokenCount(value);

    public static void ShowModelSelectionSummary(string selectedModel, IReadOnlyList<(string Label, string Value)> details)
        => ModelPickerUI.ShowModelSelectionSummary(selectedModel, details);

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
    /// Render a session statistics table covering completed turn count,
    /// outcome breakdown, token totals, p50/p95 latency, and tool-call stats.
    /// </summary>
    public static void ShowStatsPanel(
        int completedTurns,
        int failedTurns,
        int cancelledTurns,
        long totalInputTokens,
        long totalOutputTokens,
        TimeSpan? p50Latency,
        TimeSpan? p95Latency,
        int totalToolCalls,
        double? p50ToolsPerTurn,
        double? p95ToolsPerTurn,
        string? costEstimate)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Session Statistics[/]");
        table.AddColumn("Metric");
        table.AddColumn("Value");

        table.AddRow("Completed turns", completedTurns.ToString());
        if (failedTurns > 0) table.AddRow("Failed turns", failedTurns.ToString());
        if (cancelledTurns > 0) table.AddRow("Cancelled turns", cancelledTurns.ToString());

        var totalTokens = totalInputTokens + totalOutputTokens;
        table.AddRow("Tokens (in / out / total)", $"{totalInputTokens:N0} / {totalOutputTokens:N0} / {totalTokens:N0}");

        if (p50Latency.HasValue) table.AddRow("Latency p50", FormatDuration(p50Latency.Value));
        if (p95Latency.HasValue) table.AddRow("Latency p95", FormatDuration(p95Latency.Value));

        table.AddRow("Total tool calls", totalToolCalls.ToString());
        if (p50ToolsPerTurn.HasValue) table.AddRow("Tools/turn p50", p50ToolsPerTurn.Value.ToString("0.##"));
        if (p95ToolsPerTurn.HasValue) table.AddRow("Tools/turn p95", p95ToolsPerTurn.Value.ToString("0.##"));

        if (!string.IsNullOrEmpty(costEstimate)) table.AddRow("Cost estimate", costEstimate);

        AnsiConsole.Write(table);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1) return $"{duration.TotalMilliseconds:0} ms";
        if (duration.TotalSeconds < 60) return $"{duration.TotalSeconds:0.#} s";
        return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
    }

    /// <summary>
    /// Display an info message
    /// </summary>
    public static void ShowInfo(string message)
    {
        EnsureLineBreak();
        AnsiConsole.MarkupLine($"[blue]ℹ[/] {Markup.Escape(message)}");
    }

    public static void ShowLiveStatusNotice(string message)
    {
        WithLiveOutputLock(() =>
        {
            EnsureLineBreak();
            AnsiConsole.MarkupLine($"[blue]ℹ[/] {Markup.Escape(message)}");
        });
    }

    internal static void ShowSubagentStarted(string name, string? model)
    {
        WithLiveOutputLock(() =>
        {
            EnsureLineBreak();
            var modelText = string.IsNullOrWhiteSpace(model) ? "model unavailable" : model;
            var panel = new Panel(new Markup($"[grey]Model:[/] [cyan]{Markup.Escape(modelText)}[/]"))
                .Header($"[bold blue] Sub-agent: {Markup.Escape(name)} [/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Blue);
            AnsiConsole.Write(panel);
        });
    }

    internal static void ShowSubagentResult(
        string name,
        string? model,
        string returnedContent,
        TimeSpan? duration,
        long? tokens,
        long? toolCalls,
        bool success,
        string? error)
    {
        WithLiveOutputLock(() =>
        {
            EnsureLineBreak();
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(returnedContent))
            {
                lines.Add($"[grey]Returned findings:[/]{Environment.NewLine}{Markup.Escape(returnedContent.Trim())}");
            }
            if (!string.IsNullOrWhiteSpace(error))
            {
                lines.Add($"[red]Error:[/] {Markup.Escape(error)}");
            }

            var metrics = new List<string>();
            if (!string.IsNullOrWhiteSpace(model)) metrics.Add($"model {model}");
            if (duration.HasValue) metrics.Add($"{duration.Value.TotalSeconds:0.#}s");
            if (tokens.HasValue) metrics.Add($"{tokens.Value:N0} tokens");
            if (toolCalls.HasValue) metrics.Add($"{toolCalls.Value:N0} tools");
            lines.Add($"[grey]{Markup.Escape(string.Join(" | ", metrics))}[/]");
            var color = success ? Color.Blue : Color.Red;
            var markupColor = success ? "blue" : "red";
            var state = success ? "completed" : "failed";
            var panel = new Panel(new Markup(string.Join(Environment.NewLine + Environment.NewLine, lines)))
                .Header($"[bold {markupColor}] Sub-agent {Markup.Escape(state)}: {Markup.Escape(name)} [/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(color);
            AnsiConsole.Write(panel);
        });
    }

    /// <summary>
    /// Display a warning message
    /// </summary>
    public static void ShowWarning(string message)
    {
        AnsiConsole.MarkupLine($"[yellow]⚠[/] {Markup.Escape(message)}");
    }

    public static void ShowProminentWarning(string title, string message)
    {
        var panel = new Panel(new Markup($"[bold yellow]{Markup.Escape(message)}[/]"))
            .Header($"[bold yellow] {Markup.Escape(title)} [/]")
            .Border(BoxBorder.Heavy)
            .BorderColor(Color.Yellow);

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
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
    public static void ShowCommandExecution(
        string command,
        string targetServer,
        CommandExecutionOrigin origin = CommandExecutionOrigin.MainAgentPowerShell,
        string? description = null,
        string? scriptId = null,
        string? codeKind = null)
    {
        EnsureLineBreak();
        var serverDisplay = targetServer.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? "[green]localhost[/]"
            : $"[yellow]{Markup.Escape(targetServer)}[/]";

        var (label, color, icon) = origin switch
        {
            CommandExecutionOrigin.SubagentPowerShell => ("Subagent", "deepskyblue1", ">>"),
            CommandExecutionOrigin.ApprovalSubagent => ("Approval subagent", "yellow", "??"),
            CommandExecutionOrigin.Mcp => ("MCP", "mediumpurple1", "::"),
            CommandExecutionOrigin.Tool => ("Tool", "cyan", "**"),
            _ => ("Main agent", "green", ">")
        };

        var lineCount = CountLines(command);
        var isLong = lineCount > 1 || command.Length > 160;
        if (isLong)
        {
            var kind = string.IsNullOrWhiteSpace(codeKind) ? "command" : codeKind!.ToLowerInvariant();
            var idText = string.IsNullOrWhiteSpace(scriptId) ? string.Empty : $" [grey]({Markup.Escape(scriptId!)})[/]";
            var descriptionText = string.IsNullOrWhiteSpace(description) ? kind : Markup.Escape(description!);
            AnsiConsole.MarkupLine($"[{color}]{icon}[/] [dim]{label} running on {serverDisplay}:[/] [cyan]{descriptionText}[/]{idText} [grey]- {lineCount} lines; full {kind} is captured in /report[/]");
            return;
        }

        var prefix = string.IsNullOrWhiteSpace(description)
            ? string.Empty
            : $"[grey]{Markup.Escape(description!)}:[/] ";
        AnsiConsole.MarkupLine($"[{color}]{icon}[/] [dim]{label} running on {serverDisplay}:[/] {prefix}[cyan]{Markup.Escape(command)}[/]");
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return text.Replace("\r\n", "\n").Count(ch => ch == '\n') + 1;
    }

    private static string GetExecutionModeMarkup(ExecutionMode mode)
    {
        return mode switch
        {
            ExecutionMode.Strict => "[green]STRICT[/]",
            ExecutionMode.Auto => "[yellow]AUTO[/]",
            _ => "[grey]UNKNOWN[/]"
        };
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
    /// Display a cancellation message when the user presses ESC during an agent turn.
    /// </summary>
    public static void ShowCancelled()
    {
        EnsureLineBreak();
        AnsiConsole.MarkupLine("[grey]⊘ Cancelled[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Prompt the user to retry after an error or timeout
    /// </summary>
    public static bool ShowRetryPrompt(string message)
    {
        LiveThinkingIndicator.PauseForApproval();
        try
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(message)}[/]");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]What would you like to do?[/]")
                    .HighlightStyle(new Style(Color.Cyan1))
                    .AddChoices(["Retry", "Skip"]));

            return choice.StartsWith("Retry", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            LiveThinkingIndicator.ResumeAfterApproval();
        }
    }

}
