using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GitHub.Copilot.SDK;
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
    public static StatusBarInfo Empty => new(null, null, null, null, null, 0, null);
}

/// <summary>
/// Provides TUI components for the TroubleScout application
/// </summary>
public static class ConsoleUI
{
    private static ExecutionMode _currentExecutionMode = ExecutionMode.Safe;
    private static int _lastInputRowCount = 1;
    private static int _lastSuggestionRowCount;
    private static int _lastSuggestionRowOffset = 1;
    private const int MaxPromptInputLength = 4000;
    private static readonly List<string> _promptHistory = new();
    private static readonly MarkdownStreamRenderer _markdownRenderer = new();
    private const int MaxPromptHistorySize = 100;
    internal static Func<bool> IsInputRedirectedResolver { get; set; } = static () => Console.IsInputRedirected;
    internal static Func<string, IReadOnlyList<string>, string>? ModelSwitchBehaviorPromptOverride { get; set; }

    public static void SetExecutionMode(ExecutionMode mode)
    {
        _currentExecutionMode = mode;
    }

    private static string GetPromptMarkup()
    {
        return _currentExecutionMode == ExecutionMode.Yolo
            ? "[bold cyan]You[/] [bold OrangeRed1](YOLO)[/][grey] >[/] "
            : "[bold cyan]You[/] [grey]>[/] ";
    }

    private static string GetPromptText()
    {
        return _currentExecutionMode == ExecutionMode.Yolo
            ? "You (YOLO) > "
            : "You > ";
    }

    /// <summary>
    /// Display the application banner
    /// </summary>
    public static void ShowBanner(string? version = null)
    {
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
        ExecutionMode executionMode = ExecutionMode.Safe,
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
    public static void ShowWelcomeMessage()
    {
        AnsiConsole.MarkupLine("[grey]Describe your issue and TroubleScout will investigate.[/]");
        AnsiConsole.MarkupLine("[grey]Type[/] [cyan]/help[/] [grey]for commands,[/] [cyan]/status[/] [grey]for session info,[/] [cyan]/exit[/] [grey]to quit.[/]");
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
        optionsTable.AddRow("[cyan]--jea[/] [grey]<server> <configurationName>[/]", "Preconnect a single startup JEA endpoint session");
        optionsTable.AddRow("[cyan]--mode[/] [grey]<safe|yolo>[/]", "PowerShell execution mode (default: safe)");
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
        AnsiConsole.MarkupLine("  [grey]# Use a specific AI model with YOLO execution mode[/]");
        AnsiConsole.MarkupLine("  [cyan]troublescout[/] --model gpt-4.1 --mode yolo");
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

        commandTable.AddRow("[cyan]/help[/]", "Show this full command reference");
        commandTable.AddRow("[cyan]/status[/]", "Show connection, model, mode, and session details");
        commandTable.AddRow("[cyan]/clear[/]", "Start new session");
        commandTable.AddRow("[cyan]/settings[/]", "Open settings.json and reload settings after editing");
        commandTable.AddRow("[cyan]/model[/]", "Choose another AI model and session handoff mode");
        commandTable.AddRow("[cyan]/reasoning[/] [grey][[auto|<effort>]][/]", "Set reasoning effort for the current model");
        commandTable.AddRow("[cyan]/mode[/] [grey]<safe|yolo>[/]", "Set PowerShell execution mode");
        commandTable.AddRow("[cyan]/server[/] [grey]<server1>[[,server2,...]][/]", "Connect to one or more servers: /server srv1[[,srv2,...]]");
        commandTable.AddRow("[cyan]/jea[/] [grey][[server]] [[configurationName]][/]", "Connect to a JEA constrained endpoint");
        commandTable.AddRow("[cyan]/login[/]", "Run GitHub Copilot login inside TroubleScout");
        commandTable.AddRow("[cyan]/byok[/] [grey]<env|api-key> [[base-url]] [[model]][/]", "Enable OpenAI-compatible BYOK without GitHub auth");
        commandTable.AddRow("[cyan]/byok clear[/]", "Clear saved BYOK settings for this profile");
        commandTable.AddRow("[cyan]/capabilities[/]", "Show configured and used MCP servers/skills");
        commandTable.AddRow("[cyan]/history[/]", "Show PowerShell command history for this session");
        commandTable.AddRow("[cyan]/report[/]", "Generate and open HTML session report");
        commandTable.AddRow("[cyan]/exit[/], [cyan]/quit[/], [cyan]exit[/], [cyan]quit[/]", "Leave the interactive session");

        var helpPanel = new Panel(new Rows(
            new Markup("[grey]Interactive command reference[/]"),
            new Markup(""),
            commandTable,
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
    /// Get user input with a styled prompt
    /// </summary>
    public static string GetUserInput(IReadOnlyList<string>? slashCommands = null)
    {
        AnsiConsole.Markup(GetPromptMarkup());
        if (slashCommands == null || slashCommands.Count == 0)
        {
            var value = Console.ReadLine() ?? string.Empty;
            if (value.Length > MaxPromptInputLength)
            {
                Console.WriteLine();
                Console.WriteLine();
                ShowProminentWarning("Input Too Large", $"Prompt exceeds {MaxPromptInputLength:N0} characters and was discarded.");
                AnsiConsole.Markup(GetPromptMarkup());
                return string.Empty;
            }

            return value;
        }

        var buffer = new StringBuilder();
        var completionIndex = -1;
        List<string>? matches = null;
        var historyIndex = -1;
        _lastInputRowCount = 1;
        _lastSuggestionRowCount = 0;

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
                    ClearSuggestions();
                    continue;
                }

                ClearSuggestions();
                Console.WriteLine();
                _lastInputRowCount = 1;
                historyIndex = -1;
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Escape)
            {
                buffer.Clear();
                completionIndex = -1;
                matches = null;
                historyIndex = -1;
                RedrawInputLine(string.Empty);
                ClearSuggestions();
                continue;
            }

            if (key.Key == ConsoleKey.UpArrow)
            {
                if (_promptHistory.Count == 0) continue;
                if (historyIndex == -1) historyIndex = _promptHistory.Count;
                historyIndex = Math.Max(0, historyIndex - 1);
                buffer.Clear();
                buffer.Append(_promptHistory[historyIndex]);
                completionIndex = -1;
                matches = null;
                RedrawInputLine(buffer.ToString());
                ClearSuggestions();
                continue;
            }

            if (key.Key == ConsoleKey.DownArrow)
            {
                if (historyIndex == -1) continue;
                historyIndex++;
                if (historyIndex >= _promptHistory.Count)
                {
                    historyIndex = -1;
                    buffer.Clear();
                }
                else
                {
                    buffer.Clear();
                    buffer.Append(_promptHistory[historyIndex]);
                }
                completionIndex = -1;
                matches = null;
                RedrawInputLine(buffer.ToString());
                ClearSuggestions();
                continue;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                    completionIndex = -1;
                    matches = null;
                    RedrawInputLine(buffer.ToString());
                    UpdateSuggestions(buffer.ToString(), slashCommands);
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
                UpdateSuggestions(buffer.ToString(), slashCommands);
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                if (buffer.Length + 1 > MaxPromptInputLength)
                {
                    HandleOversizedInput(buffer);
                    completionIndex = -1;
                    matches = null;
                    continue;
                }

                buffer.Append(key.KeyChar);
                completionIndex = -1;
                matches = null;
                Console.Write(key.KeyChar);
                UpdateInputRowCount(buffer.Length);
                UpdateSuggestions(buffer.ToString(), slashCommands);
            }
        }
    }

    public static void AddPromptHistory(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;
        if (_promptHistory.Count > 0 && _promptHistory[^1] == input) return;
        _promptHistory.Add(input);
        if (_promptHistory.Count > MaxPromptHistorySize)
            _promptHistory.RemoveAt(0);
    }

    private static void RedrawInputLine(string text)
    {
        var width = Math.Max(1, Console.BufferWidth);
        var currentRows = GetInputRowCount(width, text.Length);
        var rowsToClear = Math.Max(_lastInputRowCount, currentRows);

        ClearSuggestions();
        ClearInputRows(rowsToClear, width);
        AnsiConsole.Markup(GetPromptMarkup());
        Console.Write(text);
        _lastInputRowCount = currentRows;
    }

    private static void UpdateSuggestions(string text, IReadOnlyList<string> slashCommands)
    {
        if (!text.StartsWith("/", StringComparison.OrdinalIgnoreCase) || text.Contains(' '))
        {
            ClearSuggestions();
            return;
        }

        var matches = slashCommands
            .Where(cmd => cmd.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .Take(6)
            .ToList();

        if (matches.Count == 0)
        {
            ClearSuggestions();
            return;
        }

        DrawSuggestions(matches);
    }

    private static void DrawSuggestions(IReadOnlyList<string> matches)
    {
        if (matches.Count == 0)
        {
            ClearSuggestions();
            return;
        }

        var width = Math.Max(1, Console.BufferWidth);
        var originalLeft = Console.CursorLeft;
        var originalTop = Console.CursorTop;
        var suggestionRow = originalTop + 1;
        var offset = 1;

        if (suggestionRow >= Console.BufferHeight)
        {
            if (originalTop == 0)
            {
                ClearSuggestions();
                _lastSuggestionRowCount = 0;
                _lastSuggestionRowOffset = 0;
                return;
            }

            suggestionRow = originalTop - 1;
            offset = -1;
        }

        var rendered = "Suggestions: " + string.Join("  ", matches);

        if (rendered.Length > width)
        {
            rendered = rendered[..Math.Max(0, width - 1)];
        }

        Console.SetCursorPosition(0, suggestionRow);
        Console.Write(new string(' ', width));
        Console.SetCursorPosition(0, suggestionRow);
        Console.Write(rendered);

        Console.SetCursorPosition(Math.Min(originalLeft, width - 1), originalTop);
        _lastSuggestionRowCount = 1;
        _lastSuggestionRowOffset = offset;
    }

    private static void ClearSuggestions()
    {
        if (_lastSuggestionRowCount <= 0)
        {
            return;
        }

        var width = Math.Max(1, Console.BufferWidth);
        var originalLeft = Console.CursorLeft;
        var originalTop = Console.CursorTop;
        var suggestionRow = originalTop + _lastSuggestionRowOffset;

        if (suggestionRow >= 0 && suggestionRow < Console.BufferHeight)
        {
            Console.SetCursorPosition(0, suggestionRow);
            Console.Write(new string(' ', width));
        }

        Console.SetCursorPosition(Math.Min(originalLeft, width - 1), originalTop);
        _lastSuggestionRowCount = 0;
        _lastSuggestionRowOffset = 1;
    }

    private static void HandleOversizedInput(StringBuilder buffer)
    {
        ClearSuggestions();
        Console.WriteLine();
        Console.WriteLine();
        ShowProminentWarning("Input Too Large", $"Prompt exceeds {MaxPromptInputLength:N0} characters and was cleared.");
        buffer.Clear();
        DrainPendingInput();
        _lastInputRowCount = 1;
        AnsiConsole.Markup(GetPromptMarkup());
    }

    private static void DrainPendingInput()
    {
        while (Console.KeyAvailable)
        {
            _ = Console.ReadKey(intercept: true);
        }
    }

    private static void UpdateInputRowCount(int textLength)
    {
        var width = Math.Max(1, Console.BufferWidth);
        _lastInputRowCount = GetInputRowCount(width, textLength);
    }

    private static int GetInputRowCount(int width, int textLength)
    {
        var totalLength = GetPromptText().Length + textLength;
        if (totalLength <= 0)
        {
            return 1;
        }

        var rows = (totalLength + width - 1) / width;

        // When totalLength is an exact multiple of the console width, the console
        // automatically wraps to the beginning of the next line. This means the text
        // occupies "rows" lines, but the cursor is on the following line. To ensure
        // ClearInputRows clears all visually affected rows, we treat this as one extra row.
        if (totalLength % width == 0)
        {
            rows++;
        }

        return rows;
    }

    private static void ClearInputRows(int rows, int width)
    {
        if (rows <= 0 || width <= 0)
        {
            return;
        }

        var cursorTop = Console.CursorTop;
        var maxRowsAvailable = cursorTop + 1;
        var rowsToClear = Math.Min(rows, maxRowsAvailable);
        var startRow = cursorTop - (rowsToClear - 1);


        for (var i = 0; i < rowsToClear; i++)
        {
            Console.SetCursorPosition(0, startRow + i);
            Console.Write(new string(' ', width));
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
        _markdownRenderer.WriteAIResponse(text, isComplete);
    }

    /// <summary>
    /// Reset the stream buffer state (call at start of new response)
    /// </summary>
    public static void ResetStreamBuffer()
    {
        _markdownRenderer.ResetStreamBuffer();
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
        _markdownRenderer.StartAIResponse();
    }

    /// <summary>
    /// End the AI response block
    /// </summary>
    public static void EndAIResponse()
    {
        _markdownRenderer.EndAIResponse();
    }

    /// <summary>
    /// Write a compact status bar showing model, provider, and usage info
    /// </summary>
    public static void WriteStatusBar(StatusBarInfo info)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(info.Model))
        {
            parts.Add($"[grey]Model:[/] [magenta]{Markup.Escape(info.Model)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(info.Provider))
        {
            parts.Add($"[grey]Provider:[/] [blue]{Markup.Escape(info.Provider)}[/]");
        }

        if (!string.IsNullOrWhiteSpace(info.ReasoningEffort))
        {
            parts.Add($"[grey]Reasoning:[/] [cyan]{Markup.Escape(info.ReasoningEffort)}[/]");
        }

        if (info.InputTokens.HasValue || info.OutputTokens.HasValue)
        {
            var inStr = info.InputTokens.HasValue ? FormatCompactTokenCount(info.InputTokens.Value) : "?";
            var outStr = info.OutputTokens.HasValue ? FormatCompactTokenCount(info.OutputTokens.Value) : "?";
            parts.Add($"[grey]Tokens:[/] [cyan]{inStr}[/][grey] in /[/] [cyan]{outStr}[/][grey] out[/]");
        }
        else if (info.TotalTokens.HasValue)
        {
            parts.Add($"[grey]Tokens:[/] [cyan]{FormatCompactTokenCount(info.TotalTokens.Value)}[/]");
        }

        if (info.ToolInvocations > 0)
        {
            parts.Add($"[grey]Tools:[/] [cyan]{info.ToolInvocations}[/]");
        }

        if (info.SessionInputTokens.HasValue || info.SessionOutputTokens.HasValue)
        {
            var sessIn = info.SessionInputTokens.HasValue ? FormatCompactTokenCount((int)Math.Min(info.SessionInputTokens.Value, int.MaxValue)) : "?";
            var sessOut = info.SessionOutputTokens.HasValue ? FormatCompactTokenCount((int)Math.Min(info.SessionOutputTokens.Value, int.MaxValue)) : "?";
            parts.Add($"[grey]Session:[/] [cyan]{sessIn}[/][grey] in /[/] [cyan]{sessOut}[/][grey] out[/]");
            if (!string.IsNullOrWhiteSpace(info.SessionCostEstimate))
            {
                parts.Add(Markup.Escape(info.SessionCostEstimate));
            }
        }
        else if (!string.IsNullOrWhiteSpace(info.SessionCostEstimate))
        {
            parts.Add($"[grey]Session:[/] {Markup.Escape(info.SessionCostEstimate)}");
        }

        if (parts.Count == 0)
            return;

        var statusLine = string.Join("[grey] | [/]", parts);
        AnsiConsole.MarkupLine($"[dim]───[/] {statusLine} [dim]───[/]");
        AnsiConsole.WriteLine();
    }

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

    /// <summary>
    /// Display AI model selection prompt and return the selected model
    /// </summary>
    public static string? PromptModelSelection(string currentModel, IReadOnlyList<ModelInfo> models)
        => ModelPickerUI.PromptModelSelection(currentModel, models);

    internal static TroubleshootingSession.ModelSelectionEntry? PromptModelSelection(
        string currentModel,
        IReadOnlyList<TroubleshootingSession.ModelSelectionEntry> entries)
        => ModelPickerUI.PromptModelSelection(currentModel, entries);

    internal static TroubleshootingSession.ModelSwitchBehavior? PromptModelSwitchBehavior(
        string currentModel,
        string selectedModel)
        => ModelPickerUI.PromptModelSwitchBehavior(currentModel, selectedModel);

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
    public static void ShowCommandExecution(string command, string targetServer)
    {
        EnsureLineBreak();
        var serverDisplay = targetServer.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? "[green]localhost[/]"
            : $"[yellow]{Markup.Escape(targetServer)}[/]";
        
        AnsiConsole.MarkupLine($"[grey]>[/] [dim]Running on {serverDisplay}:[/] [cyan]{Markup.Escape(command)}[/]");
    }

    private static string GetExecutionModeMarkup(ExecutionMode mode)
    {
        return mode switch
        {
            ExecutionMode.Safe => "[green]SAFE[/]",
            ExecutionMode.Yolo => "[red]YOLO[/]",
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

    /// <summary>
    /// Display reasoning/thinking text in a visually muted dark color
    /// </summary>
    public static void WriteReasoningText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Console.IsOutputRedirected)
        {
            Console.Write(text);
        }
        else
        {
            // ANSI dark grey (color 238 on 256-color palette) — clearly dimmer than normal output
            Console.Write($"\x1b[38;5;238m{text}\x1b[0m");
        }
    }

    /// <summary>
    /// Start a reasoning/thinking block with a muted prefix label
    /// </summary>
    public static void StartReasoningBlock()
    {
        EnsureLineBreak();
        if (!Console.IsOutputRedirected)
            Console.Write("\x1b[38;5;238m\U0001f4ad \x1b[0m");  // dark grey thinking emoji prefix
    }

    /// <summary>
    /// End a reasoning/thinking block
    /// </summary>
    public static void EndReasoningBlock()
    {
        EnsureLineBreak();
        Console.WriteLine();
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
    private string _currentStatus = "Thinking";
    private bool _isRunning;
    private bool _hasStartedResponse;
    private readonly object _lock = new();
    private Task? _spinnerTask;
    private static readonly string[] SpinnerFrames = [".", "..", "...", "....", "...."];
    private int _spinnerIndex;
    private readonly System.Diagnostics.Stopwatch _totalElapsed = new();
    private readonly System.Diagnostics.Stopwatch _phaseElapsed = new();

    private static volatile bool _approvalInProgress;

    /// <summary>Whether an approval dialog is currently suppressing spinner output.</summary>
    public static bool IsApprovalInProgress => _approvalInProgress;

    /// <summary>Pause spinner output while an approval dialog is visible.</summary>
    public static void PauseForApproval() => _approvalInProgress = true;

    /// <summary>Resume spinner output after an approval dialog completes.</summary>
    public static void ResumeAfterApproval() => _approvalInProgress = false;

    /// <summary>Total elapsed time since the indicator started.</summary>
    public TimeSpan Elapsed => _totalElapsed.Elapsed;

    /// <summary>Elapsed time since the last phase change.</summary>
    public TimeSpan PhaseElapsed => _phaseElapsed.Elapsed;

    public void Start()
    {
        lock (_lock)
        {
            if (_isRunning) return;
            _isRunning = true;
            _hasStartedResponse = false;
            _totalElapsed.Restart();
            _phaseElapsed.Restart();
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

                        if (!_approvalInProgress)
                        {
                            WriteSpinnerFrame();
                        }
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

    private void WriteSpinnerFrame()
    {
        var phaseSec = (int)_phaseElapsed.Elapsed.TotalSeconds;
        var totalSec = (int)_totalElapsed.Elapsed.TotalSeconds;
        var elapsed = FormatElapsed(totalSec);

        string statusColor;
        string status;
        string hint;

        if (phaseSec >= 60)
        {
            statusColor = "\u001b[33m";
            status = $"\u26a0 Still waiting ({elapsed})";
            hint = "operation may be stalled \u2014 ESC to cancel";
        }
        else if (phaseSec >= 30)
        {
            statusColor = "\u001b[33m";
            status = $"\u26a0 Still working ({elapsed})";
            hint = "this is taking longer than usual \u2014 ESC to cancel";
        }
        else
        {
            statusColor = "\u001b[36m";
            status = $"{_currentStatus}{SpinnerFrames[_spinnerIndex]}";
            if (totalSec >= 3)
            {
                status += $" ({elapsed})";
            }

            hint = "ESC to cancel";
        }

        Console.Write($"\r\x1b[K{statusColor}{status}\u001b[0m  \u001b[90m{hint}\u001b[0m");
        _spinnerIndex = (_spinnerIndex + 1) % SpinnerFrames.Length;
    }

    internal static string FormatElapsed(int totalSeconds)
    {
        if (totalSeconds < 60)
        {
            return $"{totalSeconds}s";
        }

        var min = totalSeconds / 60;
        var sec = totalSeconds % 60;
        return sec > 0 ? $"{min}m {sec}s" : $"{min}m";
    }

    public void UpdateStatus(string status)
    {
        lock (_lock)
        {
            _currentStatus = status;
            _phaseElapsed.Restart();
        }
    }

    public void ShowToolExecution(string toolName)
    {
        lock (_lock)
        {
            _currentStatus = $"Running {toolName}";
            _phaseElapsed.Restart();
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
            _totalElapsed.Stop();
            _phaseElapsed.Stop();
            
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
