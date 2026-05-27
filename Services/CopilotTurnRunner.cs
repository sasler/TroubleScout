using System.Reflection;
using System.Text;
using GitHub.Copilot;
using Spectre.Console;
using TroubleScout.UI;

namespace TroubleScout.Services;

internal interface ICopilotTurnSession
{
    IDisposable On(Action<SessionEvent> handler);
    Task<string> SendAsync(MessageOptions options, CancellationToken cancellationToken);
    Task AbortAsync(CancellationToken cancellationToken);
}

internal sealed class CopilotTurnSessionAdapter(CopilotSession session) : ICopilotTurnSession
{
    public IDisposable On(Action<SessionEvent> handler) => session.On<SessionEvent>(handler);

    public Task<string> SendAsync(MessageOptions options, CancellationToken cancellationToken)
        => session.SendAsync(options, cancellationToken);

    public Task AbortAsync(CancellationToken cancellationToken)
        => session.AbortAsync(cancellationToken);
}

internal interface ITurnThinkingIndicator : IDisposable
{
    TimeSpan Elapsed { get; }
    void Start();
    void UpdateStatus(string status);
    void ShowToolExecution(string toolName);
    void StopForResponse();
}

internal sealed class ConsoleTurnThinkingIndicator(LiveThinkingIndicator indicator) : ITurnThinkingIndicator
{
    public TimeSpan Elapsed => indicator.Elapsed;
    public void Start() => indicator.Start();
    public void UpdateStatus(string status) => indicator.UpdateStatus(status);
    public void ShowToolExecution(string toolName) => indicator.ShowToolExecution(toolName);
    public void StopForResponse() => indicator.StopForResponse();
    public void Dispose() => indicator.Dispose();
}

internal sealed class CopilotTurnCallbacks
{
    public Action StartReasoningBlock { get; init; } = static () => { };
    public Action<string> WriteReasoningText { get; init; } = static _ => { };
    public Action EndReasoningBlock { get; init; } = static () => { };
    public Action StartAIResponse { get; init; } = static () => { };
    public Action<string> WriteAIResponse { get; init; } = static _ => { };
    public Action EndAIResponse { get; init; } = static () => { };
    public Action<string, string> ShowError { get; init; } = static (_, _) => { };
    public Action ShowCancelled { get; init; } = static () => { };
    public Action<string> ShowLiveStatusNotice { get; init; } = static _ => { };
    public Action<ToolExecutionStartEvent> RecordMcpToolAction { get; init; } = static _ => { };
    public Action<ToolExecutionCompleteEvent> RecordMcpToolComplete { get; init; } = static _ => { };
    public Action<SubagentStartedEvent> RecordSubagentStarted { get; init; } = static _ => { };
    public Action<SubagentCompletedEvent> RecordSubagentCompleted { get; init; } = static _ => { };
    public Action<SubagentFailedEvent> RecordSubagentFailed { get; init; } = static _ => { };
    public Action<ToolExecutionStartEvent> RecordSubagentToolAction { get; init; } = static _ => { };
    public Action<ToolExecutionCompleteEvent> RecordSubagentToolComplete { get; init; } = static _ => { };
    public Action<string, string> RecordSubagentMessageDelta { get; init; } = static (_, _) => { };
    public Action<string, string> RecordSubagentMessage { get; init; } = static (_, _) => { };
    public Action<string, string?> ShowSubagentStarted { get; init; } = static (_, _) => { };
    public Action<string, string?, string, TimeSpan?, long?, long?, bool, string?> ShowSubagentResult { get; init; } = static (_, _, _, _, _, _, _, _) => { };
    public Action IncrementToolInvocation { get; init; } = static () => { };
}

internal sealed class CopilotTurnRequest
{
    public required ICopilotTurnSession Session { get; init; }
    public required string Prompt { get; init; }
    public required Func<ITurnThinkingIndicator> CreateThinkingIndicator { get; init; }
    public required IReadOnlyDictionary<string, string> ToolDescriptions { get; init; }
    public CopilotTurnCallbacks Callbacks { get; init; } = new();
    public CancellationToken CancellationToken { get; init; }
}

internal sealed record CopilotTurnResult(
    bool Success,
    bool HasError,
    bool WasCancelled,
    bool HasStartedStreaming,
    string ResponseText);

internal sealed class CopilotTurnRunner
{
    public async Task<CopilotTurnResult> RunAsync(CopilotTurnRequest request)
    {
        IDisposable? subscription = null;
        ITurnThinkingIndicator? thinkingIndicator = null;
        CancellationTokenSource? watchdogCts = null;
        Task? watchdogTask = null;

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hasError = false;
        var wasCancelled = false;
        var hasStartedStreaming = false;
        var hasStartedReasoning = false;
        var pendingStreamLineBreak = false;
        var currentStreamMessageId = string.Empty;
        var processedDeltaIds = new HashSet<string>();
        var responseBuffer = new StringBuilder();
        var subagentOutput = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var lastEventTimeTicks = DateTime.UtcNow.Ticks;
        Task? abortTask = null;

        try
        {
            using var cancelReg = request.CancellationToken.Register(() =>
            {
                wasCancelled = true;
                watchdogCts?.Cancel();
                abortTask = AbortActiveTurnAsync();
                done.TrySetResult(false);
            });

            thinkingIndicator = request.CreateThinkingIndicator();
            thinkingIndicator.Start();
            watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
            watchdogTask = RunActivityWatchdogAsync(
                thinkingIndicator,
                () => new DateTime(Interlocked.Read(ref lastEventTimeTicks), DateTimeKind.Utc),
                () => hasStartedStreaming,
                request.Callbacks.ShowLiveStatusNotice,
                watchdogCts.Token);

            subscription = request.Session.On(evt =>
            {
                Interlocked.Exchange(ref lastEventTimeTicks, DateTime.UtcNow.Ticks);

                switch (evt)
                {
                    case AssistantTurnStartEvent:
                        if (hasStartedStreaming)
                        {
                            pendingStreamLineBreak = true;
                        }
                        thinkingIndicator.UpdateStatus("Analyzing");
                        break;

                    case AssistantReasoningDeltaEvent reasoningDelta:
                        if (hasStartedStreaming)
                        {
                            break;
                        }

                        var reasoningDeltaText = reasoningDelta.Data?.DeltaContent ?? string.Empty;
                        if (!string.IsNullOrEmpty(reasoningDeltaText))
                        {
                            if (!hasStartedReasoning)
                            {
                                hasStartedReasoning = true;
                                thinkingIndicator.StopForResponse();
                                request.Callbacks.StartReasoningBlock();
                            }

                            request.Callbacks.WriteReasoningText(reasoningDeltaText);
                        }
                        break;

                    case AssistantReasoningEvent reasoning:
                        if (hasStartedStreaming)
                        {
                            break;
                        }

                        var reasoningText = reasoning.Data?.Content ?? string.Empty;
                        if (!string.IsNullOrEmpty(reasoningText))
                        {
                            if (!hasStartedReasoning)
                            {
                                hasStartedReasoning = true;
                                thinkingIndicator.StopForResponse();
                                request.Callbacks.StartReasoningBlock();
                            }

                            request.Callbacks.WriteReasoningText(reasoningText);
                        }
                        break;

                    case ToolExecutionStartEvent toolStart:
                        if (hasStartedStreaming)
                        {
                            pendingStreamLineBreak = true;
                        }

                        thinkingIndicator.ShowToolExecution(BuildToolDisplay(toolStart, request.ToolDescriptions));
                        var startParentToolCallId = ReadStringProperty(toolStart.Data, "ParentToolCallId");
                        if (!string.IsNullOrWhiteSpace(startParentToolCallId))
                        {
                            request.Callbacks.RecordSubagentToolAction(toolStart);
                        }
                        else
                        {
                            request.Callbacks.RecordMcpToolAction(toolStart);
                        }
                        request.Callbacks.IncrementToolInvocation();
                        break;

                    case ToolExecutionCompleteEvent toolComplete:
                        var completeParentToolCallId = ReadStringProperty(toolComplete.Data, "ParentToolCallId");
                        if (!string.IsNullOrWhiteSpace(completeParentToolCallId))
                        {
                            request.Callbacks.RecordSubagentToolComplete(toolComplete);
                        }
                        else
                        {
                            request.Callbacks.RecordMcpToolComplete(toolComplete);
                        }
                        if (hasStartedStreaming)
                        {
                            pendingStreamLineBreak = true;
                        }
                        thinkingIndicator.UpdateStatus("Processing results");
                        break;

                    case SubagentStartedEvent subagentStarted:
                        if (hasStartedStreaming)
                        {
                            pendingStreamLineBreak = true;
                        }
                        var startedName = subagentStarted.Data?.AgentDisplayName ?? subagentStarted.Data?.AgentName ?? "subagent";
                        var startedModel = subagentStarted.Data?.Model;
                        thinkingIndicator.UpdateStatus($"Delegating to {startedName}");
                        request.Callbacks.ShowLiveStatusNotice(
                            string.IsNullOrWhiteSpace(startedModel)
                                ? $"Subagent started: {startedName}"
                                : $"Subagent started: {startedName} ({startedModel})");
                        if (!string.IsNullOrWhiteSpace(subagentStarted.Data?.ToolCallId))
                        {
                            subagentOutput[subagentStarted.Data.ToolCallId] = new StringBuilder();
                        }
                        request.Callbacks.ShowSubagentStarted(startedName, startedModel);
                        request.Callbacks.RecordSubagentStarted(subagentStarted);
                        break;

                    case SubagentCompletedEvent subagentCompleted:
                        if (hasStartedStreaming)
                        {
                            pendingStreamLineBreak = true;
                        }
                        thinkingIndicator.UpdateStatus("Processing delegated results");
                        var completedName = subagentCompleted.Data?.AgentDisplayName ?? subagentCompleted.Data?.AgentName ?? "subagent";
                        var completedSuffix = new List<string>();
                        if (subagentCompleted.Data?.TotalTokens is long tokens && tokens > 0)
                        {
                            completedSuffix.Add($"{tokens:N0} tokens");
                        }
                        if (subagentCompleted.Data?.TotalToolCalls is long tools && tools > 0)
                        {
                            completedSuffix.Add(FormatToolCount(tools));
                        }
                        request.Callbacks.ShowLiveStatusNotice(
                            $"Subagent completed: {completedName}" +
                            (completedSuffix.Count == 0 ? string.Empty : $" ({string.Join(", ", completedSuffix)})"));
                        var completedToolCallId = subagentCompleted.Data?.ToolCallId;
                        var completedResult = !string.IsNullOrWhiteSpace(completedToolCallId)
                            && subagentOutput.Remove(completedToolCallId, out var completedBuffer)
                                ? completedBuffer.ToString()
                                : string.Empty;
                        request.Callbacks.ShowSubagentResult(
                            completedName,
                            subagentCompleted.Data?.Model,
                            completedResult,
                            subagentCompleted.Data?.Duration,
                            subagentCompleted.Data?.TotalTokens,
                            subagentCompleted.Data?.TotalToolCalls,
                            true,
                            null);
                        request.Callbacks.RecordSubagentCompleted(subagentCompleted);
                        break;

                    case SubagentFailedEvent subagentFailed:
                        if (hasStartedStreaming)
                        {
                            pendingStreamLineBreak = true;
                        }
                        thinkingIndicator.UpdateStatus("Delegated task failed");
                        var failedName = subagentFailed.Data?.AgentDisplayName ?? subagentFailed.Data?.AgentName ?? "subagent";
                        var failedSuffix = new List<string>();
                        if (subagentFailed.Data?.Duration is TimeSpan failedDuration)
                        {
                            failedSuffix.Add($"{failedDuration.TotalSeconds:0.#}s");
                        }
                        if (subagentFailed.Data?.TotalTokens is long failedTokens && failedTokens > 0)
                        {
                            failedSuffix.Add($"{failedTokens:N0} tokens");
                        }
                        if (subagentFailed.Data?.TotalToolCalls is long failedTools && failedTools > 0)
                        {
                            failedSuffix.Add(FormatToolCount(failedTools));
                        }
                        request.Callbacks.ShowLiveStatusNotice(
                            $"Subagent failed: {failedName}" +
                            (failedSuffix.Count == 0 ? string.Empty : $" ({string.Join(", ", failedSuffix)})"));
                        var failedToolCallId = subagentFailed.Data?.ToolCallId;
                        var failedResult = !string.IsNullOrWhiteSpace(failedToolCallId)
                            && subagentOutput.Remove(failedToolCallId, out var failedBuffer)
                                ? failedBuffer.ToString()
                                : string.Empty;
                        request.Callbacks.ShowSubagentResult(
                            failedName,
                            subagentFailed.Data?.Model,
                            failedResult,
                            subagentFailed.Data?.Duration,
                            subagentFailed.Data?.TotalTokens,
                            subagentFailed.Data?.TotalToolCalls,
                            false,
                            subagentFailed.Data?.Error);
                        request.Callbacks.RecordSubagentFailed(subagentFailed);
                        break;

                    case AssistantMessageDeltaEvent delta:
                        if (!processedDeltaIds.Add(delta.Id.ToString()))
                        {
                            break;
                        }

                        var deltaParentToolCallId = ReadStringProperty(delta.Data, "ParentToolCallId");
                        if (!string.IsNullOrWhiteSpace(deltaParentToolCallId))
                        {
                            var delegatedDelta = delta.Data?.DeltaContent ?? string.Empty;
                            if (!subagentOutput.TryGetValue(deltaParentToolCallId, out var delegatedBuffer))
                            {
                                delegatedBuffer = new StringBuilder();
                                subagentOutput[deltaParentToolCallId] = delegatedBuffer;
                            }
                            delegatedBuffer.Append(delegatedDelta);
                            request.Callbacks.RecordSubagentMessageDelta(deltaParentToolCallId, delegatedDelta);
                            break;
                        }

                        var deltaMessageId = ReadStringProperty(delta.Data, "MessageId", "Id");
                        if (!string.IsNullOrWhiteSpace(deltaMessageId))
                        {
                            if (!string.IsNullOrWhiteSpace(currentStreamMessageId)
                                && !currentStreamMessageId.Equals(deltaMessageId, StringComparison.Ordinal)
                                && responseBuffer.Length > 0)
                            {
                                pendingStreamLineBreak = true;
                            }

                            currentStreamMessageId = deltaMessageId;
                        }

                        if (!hasStartedStreaming)
                        {
                            hasStartedStreaming = true;
                            if (hasStartedReasoning)
                            {
                                request.Callbacks.EndReasoningBlock();
                            }

                            thinkingIndicator.StopForResponse();
                            request.Callbacks.StartAIResponse();
                        }

                        var deltaText = delta.Data?.DeltaContent ?? string.Empty;
                        if (pendingStreamLineBreak && responseBuffer.Length > 0)
                        {
                            request.Callbacks.WriteAIResponse(Environment.NewLine);
                            responseBuffer.AppendLine();
                            pendingStreamLineBreak = false;
                        }

                        responseBuffer.Append(deltaText);
                        request.Callbacks.WriteAIResponse(deltaText);
                        break;

                    case AssistantMessageEvent msg:
                        var messageParentToolCallId = ReadStringProperty(msg.Data, "ParentToolCallId");
                        if (!string.IsNullOrWhiteSpace(messageParentToolCallId))
                        {
                            var delegatedMessage = msg.Data?.Content ?? string.Empty;
                            if (!subagentOutput.TryGetValue(messageParentToolCallId, out var finalDelegatedBuffer))
                            {
                                subagentOutput[messageParentToolCallId] = new StringBuilder(delegatedMessage);
                            }
                            else if (finalDelegatedBuffer.Length == 0)
                            {
                                finalDelegatedBuffer.Append(delegatedMessage);
                            }
                            request.Callbacks.RecordSubagentMessage(messageParentToolCallId, delegatedMessage);
                            break;
                        }

                        if (!hasStartedStreaming && !string.IsNullOrEmpty(msg.Data?.Content))
                        {
                            if (hasStartedReasoning)
                            {
                                request.Callbacks.EndReasoningBlock();
                            }

                            thinkingIndicator.StopForResponse();
                            request.Callbacks.StartAIResponse();
                            request.Callbacks.WriteAIResponse(msg.Data.Content);
                            responseBuffer.Append(msg.Data.Content);
                            hasStartedStreaming = true;
                        }
                        break;

                    case SessionErrorEvent errorEvent:
                        thinkingIndicator.StopForResponse();
                        request.Callbacks.EndAIResponse();
                        request.Callbacks.ShowError("Session Error", errorEvent.Data?.Message ?? "Unknown error");
                        hasError = true;
                        done.TrySetResult(false);
                        break;

                    case SessionIdleEvent:
                        done.TrySetResult(true);
                        break;
                }
            });

            await request.Session.SendAsync(new MessageOptions { Prompt = request.Prompt }, request.CancellationToken);
            await done.Task;
            await AwaitAbortIfNeededAsync();
            await StopWatchdogAsync();

            subscription.Dispose();
            subscription = null;

            if (hasStartedStreaming)
            {
                request.Callbacks.EndAIResponse();
            }

            if (wasCancelled)
            {
                thinkingIndicator.Dispose();
                thinkingIndicator = null;
                request.Callbacks.ShowCancelled();
            }

            var success = !hasError && !wasCancelled;
            return new CopilotTurnResult(
                success,
                hasError,
                wasCancelled,
                hasStartedStreaming,
                responseBuffer.ToString());
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
            await AwaitAbortIfNeededAsync();
            await StopWatchdogAsync();
            subscription?.Dispose();
            subscription = null;
            if (hasStartedStreaming)
            {
                request.Callbacks.EndAIResponse();
            }

            thinkingIndicator?.Dispose();
            thinkingIndicator = null;
            request.Callbacks.ShowCancelled();
            return new CopilotTurnResult(false, hasError, true, hasStartedStreaming, responseBuffer.ToString());
        }
        finally
        {
            watchdogCts?.Cancel();
            watchdogCts?.Dispose();
            subscription?.Dispose();
            thinkingIndicator?.Dispose();
        }

        async Task StopWatchdogAsync()
        {
            watchdogCts?.Cancel();
            if (watchdogTask != null)
            {
                try { await watchdogTask.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { /* ignore */ }
            }

            watchdogCts?.Dispose();
            watchdogCts = null;
            watchdogTask = null;
        }

        async Task AbortActiveTurnAsync()
        {
            try
            {
                await request.Session.AbortAsync(CancellationToken.None);
            }
            catch
            {
                // Cancellation should not surface a secondary abort failure to the user.
            }
        }

        async Task AwaitAbortIfNeededAsync()
        {
            var pendingAbort = abortTask;
            if (pendingAbort != null)
            {
                await pendingAbort;
            }
        }
    }

    private static string FormatToolCount(long count)
        => $"{count:N0} {(count == 1 ? "tool" : "tools")}";

    internal static async Task RunActivityWatchdogAsync(
        ITurnThinkingIndicator indicator,
        Func<DateTime> getLastEventTime,
        Func<bool> hasStartedStreaming,
        Action<string> showLiveStatusNotice,
        CancellationToken cancellationToken)
    {
        const int checkIntervalMs = 2000;
        string? lastWatchdogStatus = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(checkIntervalMs, cancellationToken);

                var idleSeconds = (DateTime.UtcNow - getLastEventTime()).TotalSeconds;
                var nextWatchdogStatus = GetActivityWatchdogStatus(idleSeconds);

                if (!string.Equals(nextWatchdogStatus, lastWatchdogStatus, StringComparison.Ordinal))
                {
                    if (nextWatchdogStatus is not null)
                    {
                        if (hasStartedStreaming())
                        {
                            showLiveStatusNotice($"{nextWatchdogStatus} ({LiveThinkingIndicator.FormatElapsed((int)indicator.Elapsed.TotalSeconds)})");
                        }
                        else
                        {
                            indicator.UpdateStatus(nextWatchdogStatus);
                        }
                    }

                    lastWatchdogStatus = nextWatchdogStatus;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during turn completion and cancellation.
        }
    }

    internal static string? GetActivityWatchdogStatus(double idleSeconds)
    {
        const int slowWarningSeconds = 15;
        const int staleWarningSeconds = 30;

        if (idleSeconds >= staleWarningSeconds)
        {
            return "Connection seems slow";
        }

        if (idleSeconds >= slowWarningSeconds)
        {
            return "Waiting for response";
        }

        return null;
    }

    private static string BuildToolDisplay(
        ToolExecutionStartEvent toolStart,
        IReadOnlyDictionary<string, string> toolDescriptions)
    {
        var toolName = toolStart.Data?.ToolName ?? "tool";
        var mcpServer = ReadStringProperty(toolStart.Data, "McpServerName", "MCPServerName", "ServerName");
        if (!string.IsNullOrWhiteSpace(mcpServer))
        {
            return $"MCP [{Markup.Escape(mcpServer)}]: {Markup.Escape(toolName)}";
        }

        return toolDescriptions.TryGetValue(toolName, out var desc)
            ? desc
            : $"Using {toolName}";
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
