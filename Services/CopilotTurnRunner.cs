using System.Reflection;
using System.Text;
using System.Text.Json;
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
    public string? DefaultSubagentModel { get; init; }
    public CopilotTurnGuardOptions? GuardOptions { get; init; }
    public CopilotTurnCallbacks Callbacks { get; init; } = new();
    public CancellationToken CancellationToken { get; init; }
}

internal sealed record CopilotTurnResult(
    bool Success,
    bool HasError,
    bool WasCancelled,
    bool HasStartedStreaming,
    string ResponseText,
    CopilotTurnGuardReason GuardReason = CopilotTurnGuardReason.None,
    bool HasDirectDiagnosticEvidence = false)
{
    public bool WasGuarded => GuardReason != CopilotTurnGuardReason.None;
    public bool ShouldAttemptRecovery => WasGuarded && HasDirectDiagnosticEvidence;
}

internal sealed class CopilotTurnRunner
{
    public async Task<CopilotTurnResult> RunAsync(CopilotTurnRequest request)
    {
        IDisposable? subscription = null;
        ITurnThinkingIndicator? thinkingIndicator = null;
        CancellationTokenSource? watchdogCts = null;
        Task? watchdogTask = null;
        CancellationTokenSource? guardCts = null;
        Task? guardTask = null;

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hasError = false;
        var wasCancelled = false;
        var hasStartedStreaming = false;
        var hasStartedReasoning = false;
        var pendingStreamLineBreak = false;
        var currentStreamMessageId = string.Empty;
        var processedDeltaIds = new HashSet<string>();
        var responseBuffer = new StringBuilder();
        var pendingRootToolEnvelope = new StringBuilder();
        var subagentOutput = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var subagentModels = new Dictionary<string, string>(StringComparer.Ordinal);
        var lastEventTimeTicks = DateTime.UtcNow.Ticks;
        Task? abortTask = null;
        var guard = new CopilotTurnGuard(request.GuardOptions ?? CopilotTurnGuardOptions.Default, DateTime.UtcNow);
        var guardReason = CopilotTurnGuardReason.None;
        var hasDirectDiagnosticEvidence = false;
        var guardTripped = 0;

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
            guardCts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
            guardTask = RunTurnGuardAsync(
                guard,
                TryTripGuard,
                request.GuardOptions ?? CopilotTurnGuardOptions.Default,
                guardCts.Token);

            subscription = request.Session.On(evt =>
            {
                var eventTimeUtc = DateTime.UtcNow;
                Interlocked.Exchange(ref lastEventTimeTicks, eventTimeUtc.Ticks);
                guard.RecordEvent(eventTimeUtc);

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
                        guard.RecordToolStart(toolStart);
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
                        TryTripGuard(guard.RecordToolComplete(toolComplete));
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
                        var startedModel = string.IsNullOrWhiteSpace(subagentStarted.Data?.Model)
                            ? request.DefaultSubagentModel
                            : subagentStarted.Data.Model;
                        thinkingIndicator.UpdateStatus($"Delegating to {startedName}");
                        request.Callbacks.ShowLiveStatusNotice(
                            string.IsNullOrWhiteSpace(startedModel)
                                ? $"Subagent started: {startedName}"
                                : $"Subagent started: {startedName} ({startedModel})");
                        if (!string.IsNullOrWhiteSpace(subagentStarted.Data?.ToolCallId))
                        {
                            subagentOutput[subagentStarted.Data.ToolCallId] = new StringBuilder();
                            if (!string.IsNullOrWhiteSpace(startedModel))
                            {
                                subagentModels[subagentStarted.Data.ToolCallId] = startedModel;
                            }
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
                        var completedModel = ResolveSubagentDisplayModel(
                            completedToolCallId,
                            subagentModels,
                            subagentCompleted.Data?.Model,
                            request.DefaultSubagentModel);
                        request.Callbacks.ShowSubagentResult(
                            completedName,
                            completedModel,
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
                        var failedModel = ResolveSubagentDisplayModel(
                            failedToolCallId,
                            subagentModels,
                            subagentFailed.Data?.Model,
                            request.DefaultSubagentModel);
                        request.Callbacks.ShowSubagentResult(
                            failedName,
                            failedModel,
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

                        if (TryHandleRootToolUseEnvelopeDelta(
                            delta.Data?.DeltaContent ?? string.Empty,
                            pendingRootToolEnvelope,
                            responseBuffer,
                            AppendRootAssistantText))
                        {
                            break;
                        }

                        AppendRootAssistantText(delta.Data?.DeltaContent ?? string.Empty);
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

                        if (IsRawToolUseEnvelope(msg.Data?.Content))
                        {
                            break;
                        }

                        if (!hasStartedStreaming && !string.IsNullOrEmpty(msg.Data?.Content))
                        {
                            AppendRootAssistantText(msg.Data.Content);
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

            var sendTask = request.Session.SendAsync(new MessageOptions { Prompt = request.Prompt }, request.CancellationToken);
            await AwaitSendAndTurnCompletionAsync(sendTask);
            FlushPendingRootToolEnvelopeIfNeeded();
            await AwaitAbortIfNeededAsync();
            await StopGuardAsync();
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

            var success = !hasError && !wasCancelled && guardReason == CopilotTurnGuardReason.None;
            return new CopilotTurnResult(
                success,
                hasError,
                wasCancelled,
                hasStartedStreaming,
                responseBuffer.ToString(),
                guardReason,
                hasDirectDiagnosticEvidence);
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
            await AwaitAbortIfNeededAsync();
            await StopGuardAsync();
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
            guardCts?.Cancel();
            guardCts?.Dispose();
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

        async Task StopGuardAsync()
        {
            guardCts?.Cancel();
            if (guardTask != null)
            {
                try
                {
                    await guardTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (OperationCanceledException)
                {
                    // Expected during turn completion and cancellation.
                }
                catch (TimeoutException)
                {
                    // Do not let guard shutdown delay returning control to the user.
                }
            }

            guardCts?.Dispose();
            guardCts = null;
            guardTask = null;
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

        async Task AwaitSendAndTurnCompletionAsync(Task<string> sendTask)
        {
            var completed = await Task.WhenAny(sendTask, done.Task);
            if (completed == sendTask)
            {
                try
                {
                    await sendTask;
                }
                catch (OperationCanceledException) when (wasCancelled || guardReason != CopilotTurnGuardReason.None)
                {
                    // Expected when cancellation or guard-triggered abort interrupts SendAsync.
                }
                catch (Exception) when (guardReason != CopilotTurnGuardReason.None)
                {
                    // The SDK may fault the in-flight send after a guard-triggered abort.
                }

                await done.Task;
                return;
            }

            await AwaitAbortIfNeededAsync();
            try
            {
                await sendTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) when (wasCancelled || guardReason != CopilotTurnGuardReason.None)
            {
                // Expected when cancellation or guard-triggered abort interrupts SendAsync.
            }
            catch (TimeoutException) when (wasCancelled || guardReason != CopilotTurnGuardReason.None)
            {
                _ = sendTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception) when (guardReason != CopilotTurnGuardReason.None)
            {
                // The SDK may fault the in-flight send after a guard-triggered abort.
            }
        }

        void AppendRootAssistantText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
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

            if (pendingStreamLineBreak && responseBuffer.Length > 0)
            {
                request.Callbacks.WriteAIResponse(Environment.NewLine);
                responseBuffer.AppendLine();
                pendingStreamLineBreak = false;
            }

            responseBuffer.Append(text);
            request.Callbacks.WriteAIResponse(text);
            TryTripGuard(guard.RecordRootAssistantText(text));
        }

        void FlushPendingRootToolEnvelopeIfNeeded()
        {
            if (pendingRootToolEnvelope.Length == 0)
            {
                return;
            }

            var buffered = pendingRootToolEnvelope.ToString();
            pendingRootToolEnvelope.Clear();
            if (!IsRawToolUseEnvelope(buffered))
            {
                AppendRootAssistantText(buffered);
            }
        }

        void TryTripGuard(CopilotTurnGuardReason reason)
        {
            if (reason == CopilotTurnGuardReason.None)
            {
                return;
            }

            if (Interlocked.Exchange(ref guardTripped, 1) != 0)
            {
                return;
            }

            guardReason = reason;
            hasDirectDiagnosticEvidence = guard.HasDirectDiagnosticEvidence;
            watchdogCts?.Cancel();
            guardCts?.Cancel();
            abortTask = AbortActiveTurnAsync();
            done.TrySetResult(false);
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

    internal static async Task RunTurnGuardAsync(
        CopilotTurnGuard guard,
        Action<CopilotTurnGuardReason> onGuardTripped,
        CopilotTurnGuardOptions options,
        CancellationToken cancellationToken)
    {
        var checkInterval = options.CheckInterval > TimeSpan.Zero
            ? options.CheckInterval
            : CopilotTurnGuardOptions.Default.CheckInterval;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(checkInterval, cancellationToken);
                var reason = guard.EvaluateTimeout(DateTime.UtcNow);
                if (reason == CopilotTurnGuardReason.None)
                {
                    continue;
                }

                if (reason == CopilotTurnGuardReason.SilentPreToolStall && guard.HasSeenEvent)
                {
                    continue;
                }

                onGuardTripped(reason);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during normal turn completion and cancellation.
        }
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

    private static string? ResolveSubagentDisplayModel(
        string? toolCallId,
        Dictionary<string, string> startedModels,
        string? eventModel,
        string? configuredFallback)
    {
        var normalizedEventModel = string.IsNullOrWhiteSpace(eventModel) ? null : eventModel.Trim();
        var normalizedFallback = string.IsNullOrWhiteSpace(configuredFallback) ? null : configuredFallback.Trim();
        if (!string.IsNullOrWhiteSpace(toolCallId)
            && startedModels.Remove(toolCallId, out var startedModel)
            && !string.IsNullOrWhiteSpace(startedModel))
        {
            if (!string.IsNullOrWhiteSpace(normalizedEventModel)
                && !startedModel.Equals(normalizedEventModel, StringComparison.OrdinalIgnoreCase))
            {
                return $"{startedModel} (completion reported {normalizedEventModel})";
            }

            return startedModel;
        }

        return normalizedEventModel ?? normalizedFallback;
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

    private static bool TryHandleRootToolUseEnvelopeDelta(
        string deltaText,
        StringBuilder pendingEnvelope,
        StringBuilder responseBuffer,
        Action<string> appendRootAssistantText)
    {
        if (pendingEnvelope.Length == 0
            && responseBuffer.Length > 0
            && !LooksLikeToolUseEnvelopeStart(deltaText))
        {
            return false;
        }

        if (pendingEnvelope.Length == 0 && !LooksLikeToolUseEnvelopeStart(deltaText))
        {
            return false;
        }

        pendingEnvelope.Append(deltaText);
        var candidate = pendingEnvelope.ToString();
        if (TrySuppressRawToolUseEnvelopePrefix(candidate, out var remainingText))
        {
            pendingEnvelope.Clear();
            appendRootAssistantText(remainingText);
            return true;
        }

        if (IsRawToolUseEnvelope(candidate))
        {
            pendingEnvelope.Clear();
            return true;
        }

        if (IsPotentialToolUseEnvelopePrefix(candidate))
        {
            return true;
        }

        pendingEnvelope.Clear();
        appendRootAssistantText(candidate);
        return true;
    }

    private static bool LooksLikeToolUseEnvelopeStart(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimStart();
        return trimmed.StartsWith("{", StringComparison.Ordinal)
            || trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPotentialToolUseEnvelopePrefix(string text)
    {
        var trimmed = TrimJsonFence(text).TrimStart();
        const string prefix = "{\"tool_uses\"";
        return trimmed.Length > 0
            && (prefix.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TrySuppressRawToolUseEnvelopePrefix(string text, out string remainingText)
    {
        remainingText = string.Empty;
        if (!TrySplitLeadingJsonObject(text, out var jsonObject, out remainingText))
        {
            return false;
        }

        if (!IsRawToolUseEnvelope(jsonObject))
        {
            remainingText = string.Empty;
            return false;
        }

        remainingText = remainingText.TrimStart();
        return true;
    }

    private static bool TrySplitLeadingJsonObject(string text, out string jsonObject, out string remainingText)
    {
        jsonObject = string.Empty;
        remainingText = string.Empty;

        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        if (start >= text.Length || text[start] != '{')
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                depth++;
                continue;
            }

            if (ch != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                jsonObject = text[start..(i + 1)];
                remainingText = text[(i + 1)..];
                return true;
            }
        }

        return false;
    }

    private static bool IsRawToolUseEnvelope(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = TrimJsonFence(text).Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal)
            || !trimmed.Contains("\"tool_uses\"", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tool_uses", out var toolUses)
                || toolUses.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return toolUses.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("recipient_name", out var recipient)
                && recipient.ValueKind == JsonValueKind.String
                && (recipient.GetString()?.StartsWith("functions.", StringComparison.OrdinalIgnoreCase) ?? false));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string TrimJsonFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstLineBreak = trimmed.IndexOf('\n');
        if (firstLineBreak < 0)
        {
            return text;
        }

        var withoutOpeningFence = trimmed[(firstLineBreak + 1)..].Trim();
        return withoutOpeningFence.EndsWith("```", StringComparison.Ordinal)
            ? withoutOpeningFence[..^3]
            : withoutOpeningFence;
    }
}
