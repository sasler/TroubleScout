using System.Text;
using Spectre.Console;
using TroubleScout.Services;

namespace TroubleScout.UI;

public static partial class ConsoleUI
{
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
}
