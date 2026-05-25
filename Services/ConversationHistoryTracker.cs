using System.Text.Json;
using GitHub.Copilot;
using TroubleScout.Tools;
using TroubleScout.UI;

namespace TroubleScout.Services;

internal sealed class ConversationHistoryTracker
{
    private readonly List<ReportPromptEntry> _reportPrompts = [];
    private readonly object _reportLock = new();
    private int _lastPromptIndex = -1;
    private readonly Dictionary<string, McpActionLocation> _pendingMcpActions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpActionLocation> _pendingSubagentActions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpActionLocation> _pendingSubagentToolActions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _subagentReturnedFindings = new(StringComparer.Ordinal);

    private readonly record struct McpActionLocation(int PromptIndex, int ActionIndex);

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

    internal void SetPromptStatusBar(int promptIndex, StatusBarInfo? statusBar)
    {
        if (statusBar == null)
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
            _reportPrompts[promptIndex] = current with { StatusBar = statusBar };
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
            actionLog.Source ?? "PowerShell");

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
        var argumentsPreview = FormatArgumentsForReport(toolStart.Data?.Arguments);

        var toolCallId = ReadStringProperty(toolStart.Data, "ToolCallId");

        var entry = new ReportActionEntry(
            DateTimeOffset.Now,
            mcpServerName,
            toolName,
            string.Empty,
            "MCP",
            "MCP")
        {
            Arguments = argumentsPreview,
            ToolCallId = toolCallId
        };

        AppendActionToCurrentPrompt(entry);

        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            lock (_reportLock)
            {
                if (_lastPromptIndex >= 0 && _lastPromptIndex < _reportPrompts.Count)
                {
                    var actions = _reportPrompts[_lastPromptIndex].Actions;
                    _pendingMcpActions[toolCallId!] = new McpActionLocation(_lastPromptIndex, actions.Count - 1);
                }
            }
        }
    }

    internal void RecordMcpToolComplete(ToolExecutionCompleteEvent toolComplete)
    {
        var data = toolComplete.Data;
        if (data == null)
        {
            return;
        }

        var toolCallId = ReadStringProperty(data, "ToolCallId");
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return;
        }

        lock (_reportLock)
        {
            if (!_pendingMcpActions.TryGetValue(toolCallId!, out var location))
            {
                return;
            }

            _pendingMcpActions.Remove(toolCallId!);

            if (location.PromptIndex < 0 || location.PromptIndex >= _reportPrompts.Count)
            {
                return;
            }

            var actions = _reportPrompts[location.PromptIndex].Actions;
            if (location.ActionIndex < 0 || location.ActionIndex >= actions.Count)
            {
                return;
            }

            var existing = actions[location.ActionIndex];
            var output = ExtractOutput(data) ?? string.Empty;
            var success = ReadBooleanProperty(data, "Success");

            actions[location.ActionIndex] = existing with
            {
                Output = output,
                Success = success,
                SafetyApproval = success == false ? "Failed" : (string.IsNullOrEmpty(existing.SafetyApproval) ? "MCP" : existing.SafetyApproval)
            };
        }
    }

    internal void RecordSubagentStarted(SubagentStartedEvent started)
    {
        var data = started.Data;
        if (data == null)
        {
            return;
        }

        var name = data.AgentDisplayName ?? data.AgentName ?? "subagent";
        var toolCallId = data.ToolCallId;
        var model = string.IsNullOrWhiteSpace(data.Model) ? string.Empty : $"Model: {data.Model}";
        AppendActionToCurrentPrompt(new ReportActionEntry(
            DateTimeOffset.Now,
            name,
            "Delegated investigation",
            model,
            "Delegated",
            "Subagent")
        {
            ToolCallId = toolCallId
        });

        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            lock (_reportLock)
            {
                _pendingSubagentActions[toolCallId] = new McpActionLocation(
                    _lastPromptIndex,
                    _reportPrompts[_lastPromptIndex].Actions.Count - 1);
            }
        }
    }

    internal void RecordSubagentCompleted(SubagentCompletedEvent completed) =>
        CompleteSubagentAction(
            completed.Data?.ToolCallId,
            completed.Data?.TotalTokens,
            completed.Data?.TotalToolCalls,
            success: true,
            error: null);

    internal void RecordSubagentFailed(SubagentFailedEvent failed) =>
        CompleteSubagentAction(
            failed.Data?.ToolCallId,
            failed.Data?.TotalTokens,
            failed.Data?.TotalToolCalls,
            success: false,
            error: failed.Data?.Error);

    internal void RecordSubagentToolAction(ToolExecutionStartEvent toolStart)
    {
        var data = toolStart.Data;
        var parentToolCallId = ReadStringProperty(data, "ParentToolCallId");
        if (data == null || string.IsNullOrWhiteSpace(parentToolCallId))
        {
            return;
        }

        lock (_reportLock)
        {
            if (!_pendingSubagentActions.TryGetValue(parentToolCallId, out var parentLocation)
                || parentLocation.PromptIndex < 0
                || parentLocation.PromptIndex >= _reportPrompts.Count)
            {
                return;
            }

            var parentActions = _reportPrompts[parentLocation.PromptIndex].Actions;
            if (parentLocation.ActionIndex < 0 || parentLocation.ActionIndex >= parentActions.Count)
            {
                return;
            }

            var parent = parentActions[parentLocation.ActionIndex];
            var toolCallId = data.ToolCallId;
            var mcpServerName = ReadStringProperty(data, "McpServerName", "MCPServerName", "ServerName");
            parentActions.Add(new ReportActionEntry(
                DateTimeOffset.Now,
                string.IsNullOrWhiteSpace(mcpServerName) ? parent.Target : mcpServerName,
                data.ToolName ?? data.McpToolName ?? "unknown-tool",
                string.Empty,
                "Delegated",
                "Subagent Tool")
            {
                Arguments = FormatArgumentsForReport(data.Arguments),
                ToolCallId = toolCallId
            });

            if (!string.IsNullOrWhiteSpace(toolCallId))
            {
                _pendingSubagentToolActions[toolCallId] = new McpActionLocation(parentLocation.PromptIndex, parentActions.Count - 1);
            }
        }
    }

    internal void RecordSubagentToolComplete(ToolExecutionCompleteEvent toolComplete)
    {
        var data = toolComplete.Data;
        if (data == null || string.IsNullOrWhiteSpace(data.ToolCallId))
        {
            return;
        }

        lock (_reportLock)
        {
            if (!_pendingSubagentToolActions.Remove(data.ToolCallId, out var location)
                || location.PromptIndex < 0
                || location.PromptIndex >= _reportPrompts.Count)
            {
                return;
            }

            var actions = _reportPrompts[location.PromptIndex].Actions;
            if (location.ActionIndex < 0 || location.ActionIndex >= actions.Count)
            {
                return;
            }

            var existing = actions[location.ActionIndex];
            actions[location.ActionIndex] = existing with
            {
                Output = ExtractOutput(data) ?? string.Empty,
                Success = data.Success,
                SafetyApproval = data.Success ? "Delegated" : "Failed"
            };
        }
    }

    internal void RecordSubagentMessageDelta(string parentToolCallId, string content)
    {
        if (string.IsNullOrWhiteSpace(parentToolCallId) || string.IsNullOrEmpty(content))
        {
            return;
        }

        lock (_reportLock)
        {
            _subagentReturnedFindings[parentToolCallId] =
                _subagentReturnedFindings.TryGetValue(parentToolCallId, out var current)
                    ? current + content
                    : content;
        }
    }

    internal void RecordSubagentMessage(string parentToolCallId, string content)
    {
        if (string.IsNullOrWhiteSpace(parentToolCallId) || string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        lock (_reportLock)
        {
            if (!_subagentReturnedFindings.ContainsKey(parentToolCallId))
            {
                _subagentReturnedFindings[parentToolCallId] = content;
            }
        }
    }

    internal void ClearRecordedConversationHistory()
    {
        lock (_reportLock)
        {
            _reportPrompts.Clear();
            _lastPromptIndex = -1;
            _pendingMcpActions.Clear();
            _pendingSubagentActions.Clear();
            _pendingSubagentToolActions.Clear();
            _subagentReturnedFindings.Clear();
        }
    }

    internal void ReplaceRecordedConversationHistory(IReadOnlyList<ReportPromptEntry> prompts)
    {
        lock (_reportLock)
        {
            _reportPrompts.Clear();
            _reportPrompts.AddRange(SessionTranscriptService.RedactPrompts(prompts));
            _lastPromptIndex = _reportPrompts.Count - 1;
            _pendingMcpActions.Clear();
            _pendingSubagentActions.Clear();
            _pendingSubagentToolActions.Clear();
            _subagentReturnedFindings.Clear();
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
            return SessionTranscriptService.RedactPrompts(_reportPrompts);
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

    private void CompleteSubagentAction(string? toolCallId, long? tokens, long? tools, bool success, string? error)
    {
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return;
        }

        lock (_reportLock)
        {
            if (!_pendingSubagentActions.TryGetValue(toolCallId, out var location)
                || location.PromptIndex < 0
                || location.PromptIndex >= _reportPrompts.Count)
            {
                return;
            }

            var actions = _reportPrompts[location.PromptIndex].Actions;
            if (location.ActionIndex < 0 || location.ActionIndex >= actions.Count)
            {
                return;
            }

            var parts = new List<string>();
            if (tokens is > 0)
            {
                parts.Add($"{tokens:N0} tokens");
            }
            if (tools is > 0)
            {
                parts.Add($"{tools:N0} tools");
            }
            if (!string.IsNullOrWhiteSpace(error))
            {
                parts.Add(error);
            }

            var current = actions[location.ActionIndex];
            if (!string.IsNullOrWhiteSpace(current.Output))
            {
                parts.Insert(0, current.Output);
            }

            actions[location.ActionIndex] = current with
            {
                Output = string.Join(", ", parts),
                SafetyApproval = success ? "Delegated" : "Failed",
                Success = success
            };

            if (_subagentReturnedFindings.Remove(toolCallId, out var returnedFindings)
                && !string.IsNullOrWhiteSpace(returnedFindings))
            {
                actions.Add(new ReportActionEntry(
                    DateTimeOffset.Now,
                    current.Target,
                    "Returned findings",
                    returnedFindings.Trim(),
                    success ? "Delegated" : "Failed",
                    "Subagent Result")
                {
                    Success = success,
                    ToolCallId = toolCallId
                });
            }

            _pendingSubagentActions.Remove(toolCallId);
        }
    }

    private static string? FormatArgumentsForReport(object? arguments)
    {
        if (arguments == null)
        {
            return null;
        }

        try
        {
            // If it's already a string, try to pretty-print as JSON; otherwise return as-is.
            if (arguments is string s)
            {
                if (string.IsNullOrWhiteSpace(s))
                {
                    return null;
                }

                try
                {
                    using var doc = JsonDocument.Parse(s);
                    return JsonSerializer.Serialize(doc.RootElement, JsonOptions);
                }
                catch
                {
                    return s.Trim();
                }
            }

            return JsonSerializer.Serialize(arguments, JsonOptions);
        }
        catch
        {
            return arguments.ToString();
        }
    }

    private static string? ExtractOutput(object? data)
    {
        if (data == null)
        {
            return null;
        }

        // First try the simple Result.Content shape used by the SDK.
        var resultProperty = data.GetType().GetProperty("Result");
        var result = resultProperty?.GetValue(data);
        if (result != null)
        {
            var content = ReadStringProperty(result, "Content", "DetailedContent");
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            // Concatenate any text contents in the Contents array.
            var contentsProperty = result.GetType().GetProperty("Contents");
            if (contentsProperty?.GetValue(result) is System.Collections.IEnumerable contents)
            {
                var collected = new System.Text.StringBuilder();
                foreach (var item in contents)
                {
                    var text = ReadStringProperty(item, "Text", "Content");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (collected.Length > 0)
                        {
                            collected.AppendLine();
                        }
                        collected.Append(text);
                    }
                }

                if (collected.Length > 0)
                {
                    return collected.ToString();
                }
            }
        }

        // If the call failed, surface the error message instead.
        var errorProperty = data.GetType().GetProperty("Error");
        var error = errorProperty?.GetValue(data);
        if (error != null)
        {
            var message = ReadStringProperty(error, "Message", "Description");
            if (!string.IsNullOrWhiteSpace(message))
            {
                return $"[error] {message}";
            }
        }

        return null;
    }

    private static bool? ReadBooleanProperty(object? instance, string propertyName)
    {
        if (instance == null)
        {
            return null;
        }

        var property = instance.GetType().GetProperty(propertyName);
        if (property?.CanRead != true)
        {
            return null;
        }

        var value = property.GetValue(instance);
        return value switch
        {
            bool b => b,
            null => null,
            _ => null
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

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
