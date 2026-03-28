using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace TroubleScout.UI;

internal class MarkdownStreamRenderer
{
    private readonly StringBuilder _streamBuffer = new();
    private bool _inBold;
    private bool _inCode;
    private bool _inCodeBlock;
    private bool _atLineStart = true;
    private bool _inMarkdownTable;
    private readonly StringBuilder _tableLineAccumulator = new();
    private readonly List<string> _markdownTableLines = [];

    public void WriteAIResponse(string text, bool isComplete = false)
    {
        if (!isComplete)
        {
            _streamBuffer.Append(text);
            FlushStreamBuffer(forceFlush: false);
        }
        else
        {
            Console.WriteLine();
        }
    }

    public void ResetStreamBuffer()
    {
        _streamBuffer.Clear();
        _inBold = false;
        _inCode = false;
        _inCodeBlock = false;
        _atLineStart = true;
        _inMarkdownTable = false;
        _tableLineAccumulator.Clear();
        _markdownTableLines.Clear();
    }

    public void StartAIResponse()
    {
        ResetStreamBuffer();
        EnsureLineBreak();
        AnsiConsole.Markup("[bold green]TroubleScout[/] [grey]>[/] ");
    }

    public void EndAIResponse()
    {
        FlushStreamBuffer(forceFlush: true);
        Console.Write("\x1b[0m");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine();
    }

    internal Table? ParseMarkdownTable(IReadOnlyList<string> lines)
    {
        var rows = lines
            .Select(ParseMarkdownTableRow)
            .Where(cells => cells.Count > 0)
            .Where(cells => !IsSeparatorRow(cells))
            .ToList();

        if (rows.Count == 0)
        {
            return null;
        }

        var header = rows[0];
        if (header.Count == 0)
        {
            return null;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey);

        foreach (var headerCell in header)
        {
            table.AddColumn(new TableColumn($"[bold]{Markup.Escape(headerCell)}[/]"));
        }

        for (var rowIndex = 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var normalizedCells = new List<IRenderable>(header.Count);
            for (var columnIndex = 0; columnIndex < header.Count; columnIndex++)
            {
                var cell = columnIndex < row.Count ? row[columnIndex] : string.Empty;
                normalizedCells.Add(new Markup(Markup.Escape(cell)));
            }

            table.AddRow(normalizedCells);
        }

        return table;
    }

    private void FlushStreamBuffer(bool forceFlush)
    {
        var content = _streamBuffer.ToString();
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        var output = new StringBuilder();
        var i = 0;

        while (i < content.Length)
        {
            if (_inMarkdownTable)
            {
                if (_atLineStart && content[i] != '|')
                {
                    FlushMarkdownTable();
                    _inMarkdownTable = false;
                    continue;
                }

                var tableChar = content[i];
                _tableLineAccumulator.Append(tableChar);

                if (tableChar == '\n')
                {
                    _markdownTableLines.Add(_tableLineAccumulator.ToString().TrimEnd('\r', '\n'));
                    _tableLineAccumulator.Clear();
                    _atLineStart = true;
                }
                else if (tableChar != '\r')
                {
                    _atLineStart = false;
                }

                i++;
                continue;
            }

            if (!_inCodeBlock && _atLineStart && content[i] == '|')
            {
                if (output.Length > 0)
                {
                    Console.Write(output.ToString());
                    output.Clear();
                }

                _inMarkdownTable = true;
                continue;
            }

            if (i + 2 < content.Length && content[i] == '`' && content[i + 1] == '`' && content[i + 2] == '`')
            {
                if (_inCodeBlock)
                {
                    output.Append("\x1b[0m");
                    _inCodeBlock = false;
                    i += 3;
                    while (i < content.Length && content[i] != '\n' && content[i] != '\r')
                    {
                        i++;
                    }

                    continue;
                }

                output.Append("\x1b[90m");
                _inCodeBlock = true;
                i += 3;
                while (i < content.Length && content[i] != '\n' && content[i] != '\r')
                {
                    i++;
                }

                continue;
            }

            if (_inCodeBlock)
            {
                output.Append(content[i]);
                if (content[i] == '\n')
                {
                    _atLineStart = true;
                }
                else if (content[i] != '\r')
                {
                    _atLineStart = false;
                }

                i++;
                continue;
            }

            if (i + 1 < content.Length && content[i] == '*' && content[i + 1] == '*')
            {
                if (_inBold)
                {
                    output.Append("\x1b[0m");
                    _inBold = false;
                }
                else
                {
                    output.Append("\x1b[1;33m");
                    _inBold = true;
                }

                i += 2;
                continue;
            }

            if (content[i] == '`')
            {
                if (_inCode)
                {
                    output.Append("\x1b[0m");
                    _inCode = false;
                }
                else
                {
                    output.Append("\x1b[36m");
                    _inCode = true;
                }

                i++;
                continue;
            }

            if (content[i] == '#' && (i == 0 || content[i - 1] == '\n'))
            {
                while (i < content.Length && content[i] == '#')
                {
                    i++;
                }

                if (i < content.Length && content[i] == ' ')
                {
                    i++;
                }

                output.Append("\x1b[1;36m");

                while (i < content.Length && content[i] != '\n')
                {
                    output.Append(content[i]);
                    i++;
                }

                output.Append("\x1b[0m");
                _atLineStart = false;
                continue;
            }

            if (content[i] == '-' && i + 1 < content.Length && content[i + 1] == ' ' &&
                (i == 0 || content[i - 1] == '\n'))
            {
                output.Append("\x1b[32m-\x1b[0m");
                i++;
                continue;
            }

            if (char.IsDigit(content[i]) && i + 1 < content.Length && content[i + 1] == '.' &&
                (i == 0 || content[i - 1] == '\n'))
            {
                output.Append($"\x1b[32m{content[i]}.\x1b[0m");
                i += 2;
                continue;
            }

            output.Append(content[i]);
            if (content[i] == '\n')
            {
                _atLineStart = true;
            }
            else if (content[i] != '\r')
            {
                _atLineStart = false;
            }

            i++;
        }

        if (forceFlush && _inMarkdownTable)
        {
            if (_tableLineAccumulator.Length > 0)
            {
                _markdownTableLines.Add(_tableLineAccumulator.ToString().TrimEnd('\r', '\n'));
                _tableLineAccumulator.Clear();
            }

            FlushMarkdownTable();
            _inMarkdownTable = false;
        }

        if (output.Length > 0)
        {
            Console.Write(output.ToString());
        }

        _streamBuffer.Clear();
    }

    private void FlushMarkdownTable()
    {
        if (_tableLineAccumulator.Length > 0)
        {
            _markdownTableLines.Add(_tableLineAccumulator.ToString().TrimEnd('\r', '\n'));
            _tableLineAccumulator.Clear();
        }

        if (_markdownTableLines.Count == 0)
        {
            return;
        }

        var table = ParseMarkdownTable(_markdownTableLines);
        if (table != null)
        {
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }
        else
        {
            foreach (var line in _markdownTableLines)
            {
                Console.WriteLine(line);
            }
        }

        _markdownTableLines.Clear();
    }

    private static List<string> ParseMarkdownTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        if (trimmed.StartsWith("|", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith("|", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed
            .Split('|')
            .Select(cell => cell.Trim())
            .ToList();
    }

    private static bool IsSeparatorRow(IReadOnlyList<string> cells)
    {
        if (cells.Count == 0)
        {
            return false;
        }

        foreach (var cell in cells)
        {
            var normalized = cell.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (normalized.Length == 0)
            {
                return false;
            }

            foreach (var ch in normalized)
            {
                if (ch != '-' && ch != ':')
                {
                    return false;
                }
            }
        }

        return true;
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
}
