using System.Reflection;
using System.Text;
using GitHub.Copilot;

namespace TroubleScout.Services;

internal enum CopilotTurnGuardReason
{
    None,
    RepeatedStatusAfterDiagnostics,
    RepeatedToolAfterDiagnostics,
    PostDiagnosticStall,
    SilentPreToolStall
}

internal sealed record CopilotTurnGuardOptions
{
    internal static CopilotTurnGuardOptions Default { get; } = new();

    public TimeSpan PostDiagnosticStallTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan SilentPreToolStallTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(2);
    public int RepeatedStatusThreshold { get; init; } = 3;
    public int MinimumRepeatedStatusLength { get; init; } = 24;
}

internal sealed class CopilotTurnGuard
{
    private static readonly HashSet<string> RootDiagnosticTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "run_powershell",
        "get_system_info",
        "get_event_logs",
        "get_services",
        "get_processes",
        "get_disk_space",
        "get_network_info",
        "get_performance_counters"
    };

    private static readonly string[] StatusCues =
    [
        "checking",
        "gathering",
        "collecting",
        "reading",
        "querying",
        "running",
        "inspecting",
        "analyzing",
        "retrieving",
        "looking",
        "examining",
        "waiting",
        "processing",
        "getting"
    ];

    private readonly object _gate = new();
    private readonly CopilotTurnGuardOptions _options;
    private readonly Dictionary<string, bool> _rootToolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rootDiagnosticToolNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _rootDiagnosticToolCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeRootToolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _statusLineCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _lineBuffer = new();
    private readonly DateTime _startedAtUtc;
    private DateTime _lastEventAtUtc;
    private bool _hasSeenEvent;
    private bool _hasRootDiagnosticCompleted;

    internal CopilotTurnGuard(CopilotTurnGuardOptions options, DateTime startedAtUtc)
    {
        _options = options;
        _startedAtUtc = startedAtUtc;
        _lastEventAtUtc = startedAtUtc;
    }

    internal bool HasDirectDiagnosticEvidence
    {
        get
        {
            lock (_gate)
            {
                return _hasRootDiagnosticCompleted;
            }
        }
    }

    internal bool HasSeenEvent
    {
        get
        {
            lock (_gate)
            {
                return _hasSeenEvent;
            }
        }
    }

    internal void RecordEvent(DateTime eventTimeUtc)
    {
        lock (_gate)
        {
            _hasSeenEvent = true;
            _lastEventAtUtc = eventTimeUtc;
        }
    }

    internal void RecordToolStart(ToolExecutionStartEvent toolStart)
    {
        var data = toolStart.Data;
        var toolCallId = ReadStringProperty(data, "ToolCallId");
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return;
        }

        var parentToolCallId = ReadStringProperty(data, "ParentToolCallId");
        if (!string.IsNullOrWhiteSpace(parentToolCallId))
        {
            return;
        }

        var toolName = NormalizeToolName(data?.ToolName ?? data?.McpToolName);
        lock (_gate)
        {
            _activeRootToolCalls.Add(toolCallId);
            var isDiagnostic = IsRootDiagnosticToolName(toolName);
            _rootToolCalls[toolCallId] = isDiagnostic;
            if (isDiagnostic && !toolName.Equals("command", StringComparison.OrdinalIgnoreCase))
            {
                _rootDiagnosticToolNames[toolCallId] = toolName;
            }
        }
    }

    internal CopilotTurnGuardReason RecordToolComplete(ToolExecutionCompleteEvent toolComplete)
    {
        var data = toolComplete.Data;
        var toolCallId = ReadStringProperty(data, "ToolCallId");
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return CopilotTurnGuardReason.None;
        }

        var parentToolCallId = ReadStringProperty(data, "ParentToolCallId");
        if (!string.IsNullOrWhiteSpace(parentToolCallId))
        {
            return CopilotTurnGuardReason.None;
        }

        lock (_gate)
        {
            _activeRootToolCalls.Remove(toolCallId);
            if (_rootToolCalls.Remove(toolCallId, out var isDiagnostic) && isDiagnostic)
            {
                _hasRootDiagnosticCompleted = true;
                if (_rootDiagnosticToolNames.Remove(toolCallId, out var toolName))
                {
                    _rootDiagnosticToolCounts.TryGetValue(toolName, out var count);
                    count++;
                    _rootDiagnosticToolCounts[toolName] = count;
                    if (count >= 2)
                    {
                        return CopilotTurnGuardReason.RepeatedToolAfterDiagnostics;
                    }
                }
            }
        }

        return CopilotTurnGuardReason.None;
    }

    internal CopilotTurnGuardReason RecordRootAssistantText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return CopilotTurnGuardReason.None;
        }

        lock (_gate)
        {
            if (!_hasRootDiagnosticCompleted)
            {
                return CopilotTurnGuardReason.None;
            }

            if (!text.Contains('\n', StringComparison.Ordinal)
                && ShouldTrackStatusLine(NormalizeStatusLine(text)))
            {
                return RecordStatusLine(NormalizeStatusLine(text));
            }

            _lineBuffer.Append(text);
            while (TryTakeLine(out var line))
            {
                var reason = RecordStatusLine(NormalizeStatusLine(line));
                if (reason != CopilotTurnGuardReason.None)
                {
                    return reason;
                }
            }
        }

        return CopilotTurnGuardReason.None;
    }

    internal CopilotTurnGuardReason EvaluateTimeout(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_hasSeenEvent
                && nowUtc - _startedAtUtc >= _options.SilentPreToolStallTimeout)
            {
                return CopilotTurnGuardReason.SilentPreToolStall;
            }

            if (_hasRootDiagnosticCompleted
                && _activeRootToolCalls.Count == 0
                && nowUtc - _lastEventAtUtc >= _options.PostDiagnosticStallTimeout)
            {
                return CopilotTurnGuardReason.PostDiagnosticStall;
            }
        }

        return CopilotTurnGuardReason.None;
    }

    private bool TryTakeLine(out string line)
    {
        for (var i = 0; i < _lineBuffer.Length; i++)
        {
            if (_lineBuffer[i] != '\n')
            {
                continue;
            }

            line = _lineBuffer.ToString(0, i).TrimEnd('\r');
            _lineBuffer.Remove(0, i + 1);
            return true;
        }

        line = string.Empty;
        return false;
    }

    private bool ShouldTrackStatusLine(string normalized)
    {
        if (normalized.Length < _options.MinimumRepeatedStatusLength)
        {
            return false;
        }

        if (IsMarkdownTableLine(normalized) || IsMarkdownSeparator(normalized))
        {
            return false;
        }

        return StatusCues.Any(cue => normalized.Contains(cue, StringComparison.OrdinalIgnoreCase));
    }

    private CopilotTurnGuardReason RecordStatusLine(string normalized)
    {
        if (!ShouldTrackStatusLine(normalized))
        {
            return CopilotTurnGuardReason.None;
        }

        _statusLineCounts.TryGetValue(normalized, out var count);
        count++;
        _statusLineCounts[normalized] = count;
        return count >= _options.RepeatedStatusThreshold
            ? CopilotTurnGuardReason.RepeatedStatusAfterDiagnostics
            : CopilotTurnGuardReason.None;
    }

    private static bool IsMarkdownTableLine(string line)
        => line.StartsWith('|') && line.EndsWith('|');

    private static bool IsMarkdownSeparator(string line)
        => line.All(ch => ch is '|' or '-' or ':' or ' ');

    private static string NormalizeStatusLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal)
            || trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..].TrimStart();
        }

        return string.Join(' ', trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return string.Empty;
        }

        var normalized = toolName.Trim();
        var lastDot = normalized.LastIndexOf('.');
        return lastDot >= 0 && lastDot < normalized.Length - 1
            ? normalized[(lastDot + 1)..]
            : normalized;
    }

    private static bool IsRootDiagnosticToolName(string toolName)
    {
        return RootDiagnosticTools.Contains(toolName)
            || toolName.Equals("command", StringComparison.OrdinalIgnoreCase)
            || toolName.StartsWith("get ", StringComparison.OrdinalIgnoreCase)
            || toolName.StartsWith("read ", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadStringProperty(object? instance, params string[] propertyNames)
    {
        if (instance == null)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            var prop = instance.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var value = prop?.GetValue(instance);
            if (value is string stringValue && !string.IsNullOrWhiteSpace(stringValue))
            {
                return stringValue.Trim();
            }
        }

        return null;
    }
}
