using GitHub.Copilot.SDK;
using TroubleScout.Tools;

namespace TroubleScout.Services;

internal sealed class ConversationHistoryTracker
{
    private readonly List<ReportPromptEntry> _reportPrompts = [];
    private readonly object _reportLock = new();
    private int _lastPromptIndex = -1;

    internal int RecordPrompt(string prompt)
    {
        lock (_reportLock)
        {
            _reportPrompts.Add(new ReportPromptEntry(DateTimeOffset.Now, prompt, [], string.Empty));
            _lastPromptIndex = _reportPrompts.Count - 1;
            return _lastPromptIndex;
        }
    }

    internal void SetPromptReply(int promptIndex, string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return;
        }

        lock (_reportLock)
        {
            if (promptIndex < 0 || promptIndex >= _reportPrompts.Count)
            {
                return;
            }

            var current = _reportPrompts[promptIndex];
            _reportPrompts[promptIndex] = current with { AgentReply = reply.Trim() };
        }
    }

    internal void RecordCommandAction(CommandActionLog actionLog)
    {
        var entry = new ReportActionEntry(
            actionLog.Timestamp,
            actionLog.Target,
            actionLog.Command,
            actionLog.Output,
            actionLog.ApprovalState.ToString(),
            "PowerShell");

        AppendActionToCurrentPrompt(entry);
    }

    internal void RecordMcpToolAction(ToolExecutionStartEvent toolStart)
    {
        var mcpServerName = ReadStringProperty(toolStart.Data, "McpServerName", "MCPServerName", "ServerName");
        if (string.IsNullOrWhiteSpace(mcpServerName))
        {
            return;
        }

        var toolName = toolStart.Data?.ToolName ?? "unknown-tool";
        var entry = new ReportActionEntry(
            DateTimeOffset.Now,
            mcpServerName,
            toolName,
            "N/A",
            "N/A",
            "MCP");

        AppendActionToCurrentPrompt(entry);
    }

    internal void ClearRecordedConversationHistory()
    {
        lock (_reportLock)
        {
            _reportPrompts.Clear();
            _lastPromptIndex = -1;
        }
    }

    internal int GetLatestPromptIndex()
    {
        lock (_reportLock)
        {
            return _lastPromptIndex;
        }
    }

    internal bool HasRecordedConversationHistory()
    {
        lock (_reportLock)
        {
            return _reportPrompts.Count > 0;
        }
    }

    internal List<ReportPromptEntry> GetRecordedPromptSnapshot()
    {
        lock (_reportLock)
        {
            return _reportPrompts
                .Select(prompt => new ReportPromptEntry(
                    prompt.Timestamp,
                    prompt.Prompt,
                    prompt.Actions.ToList(),
                    prompt.AgentReply))
                .ToList();
        }
    }

    private void AppendActionToCurrentPrompt(ReportActionEntry actionEntry)
    {
        lock (_reportLock)
        {
            if (_lastPromptIndex < 0 || _lastPromptIndex >= _reportPrompts.Count)
            {
                return;
            }

            _reportPrompts[_lastPromptIndex].Actions.Add(actionEntry);
        }
    }

    private static string? ReadStringProperty(object? instance, params string[] propertyNames)
    {
        if (instance == null)
        {
            return null;
        }

        var type = instance.GetType();
        foreach (var propertyName in propertyNames)
        {
            var property = type.GetProperty(propertyName);
            if (property?.CanRead != true)
            {
                continue;
            }

            var value = property.GetValue(instance) as string;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
