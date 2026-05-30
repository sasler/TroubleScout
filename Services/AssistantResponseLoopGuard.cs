using System.Text;

namespace TroubleScout.Services;

internal sealed class AssistantResponseLoopGuard
{
    private const int MinimumRepeatedLineLength = 30;
    private const int RecentLineWindowSize = 5;
    private const int RepeatedLineThreshold = 3;

    private readonly Queue<string> _recentLines = new();
    private readonly StringBuilder _currentLine = new();

    public void Reset()
    {
        _recentLines.Clear();
        _currentLine.Clear();
    }

    public bool Observe(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var character in text)
        {
            if (character == '\r')
            {
                continue;
            }

            if (character == '\n')
            {
                var completedLine = _currentLine.ToString();
                _currentLine.Clear();
                if (ObserveCompletedLine(completedLine))
                {
                    return true;
                }

                continue;
            }

            _currentLine.Append(character);
        }

        return false;
    }

    private bool ObserveCompletedLine(string line)
    {
        var normalized = NormalizeLine(line);
        if (normalized.Length < MinimumRepeatedLineLength || IsMarkdownSeparatorLine(normalized))
        {
            return false;
        }

        _recentLines.Enqueue(normalized);
        while (_recentLines.Count > RecentLineWindowSize)
        {
            _recentLines.Dequeue();
        }

        var repeatCount = _recentLines.Count(recent => recent.Equals(normalized, StringComparison.Ordinal));
        return repeatCount >= RepeatedLineThreshold;
    }

    private static string NormalizeLine(string line)
    {
        var normalized = new StringBuilder(line.Length);
        var hasWhitespace = false;

        foreach (var character in line.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                hasWhitespace = true;
                continue;
            }

            if (hasWhitespace && normalized.Length > 0)
            {
                normalized.Append(' ');
                hasWhitespace = false;
            }

            normalized.Append(character);
        }

        return normalized.ToString();
    }

    private static bool IsMarkdownSeparatorLine(string line)
    {
        var hasSeparator = false;
        foreach (var character in line)
        {
            if (character is '-' or ':')
            {
                hasSeparator = true;
                continue;
            }

            if (character is '|' or ' ')
            {
                continue;
            }

            return false;
        }

        return hasSeparator;
    }
}
