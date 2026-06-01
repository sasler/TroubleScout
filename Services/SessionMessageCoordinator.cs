using GitHub.Copilot;
using TroubleScout.UI;

namespace TroubleScout.Services;

internal sealed class SessionMessageRequest
{
    internal required CopilotSession? CopilotSession { get; init; }
    internal ICopilotTurnSession? TurnSession { get; init; }
    internal required ConversationHistoryTracker HistoryTracker { get; init; }
    internal required SessionUsageTracker SessionUsageTracker { get; init; }
    internal required SessionEventTelemetry Telemetry { get; init; }
    internal required IReadOnlyDictionary<string, string> ToolDescriptions { get; init; }
    internal required Func<int> GetToolInvocationCount { get; init; }
    internal required Action IncrementToolInvocation { get; init; }
    internal required Func<StatusBarInfo> BuildStatusBarInfo { get; init; }
    internal required Func<string?> GetConfiguredSubagentModel { get; init; }
    internal required Func<CancellationToken, Task<bool>> ProcessPendingApprovals { get; init; }
    internal required Func<string, int> RecordPrompt { get; init; }
    internal required Action<int, string> SetPromptReply { get; init; }
    internal required Action<string> SetLastAssistantMessage { get; init; }
    internal required Action<ToolExecutionStartEvent> RecordMcpToolAction { get; init; }
    internal Func<CopilotTurnRequest, Task<CopilotTurnResult>>? RunTurn { get; init; }
    internal Func<IDisposable>? BeginSynthesisOnlyRecoveryTurn { get; init; }
    internal Action<string> ShowWarning { get; init; } = ConsoleUI.ShowWarning;
}

internal static class SessionMessageCoordinator
{
    internal static async Task<bool> SendMessageAsync(
        string userMessage,
        int? promptIndexOverride,
        CancellationToken cancellationToken,
        SessionMessageRequest request)
    {
        if (request.CopilotSession == null && request.TurnSession == null)
        {
            ConsoleUI.ShowError("Not Initialized", "Session not initialized. Call InitializeAsync first.");
            return false;
        }

        var turnStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var toolCountAtEntry = request.GetToolInvocationCount();
        var turnOutcome = TurnOutcome.Failed;
        var promptIndex = promptIndexOverride ?? request.RecordPrompt(userMessage);

        try
        {
            var result = await RunSingleTurnAsync(userMessage, promptIndex, cancellationToken, request);
            if (result.ShouldAttemptRecovery)
            {
                using var recovery = request.BeginSynthesisOnlyRecoveryTurn?.Invoke();
                result = await RunSingleTurnAsync(
                    BuildRecoveryPrompt(
                        userMessage,
                        result.GuardReason,
                        request.HistoryTracker.BuildRecoveryEvidence(promptIndex)),
                    promptIndex,
                    cancellationToken,
                    request);
                if (result.WasGuarded)
                {
                    request.ShowWarning("The main agent got stuck after diagnostics, and the recovery prompt did not complete. Returning control without rerunning diagnostics.");
                    turnOutcome = TurnOutcome.Failed;
                    return false;
                }
            }
            else if (result.WasGuarded)
            {
                request.ShowWarning(BuildGuardWarning(result.GuardReason));
                turnOutcome = TurnOutcome.Failed;
                return false;
            }

            if (result.WasCancelled)
            {
                turnOutcome = TurnOutcome.Cancelled;
                return false;
            }

            if (!result.HasError)
            {
                await request.ProcessPendingApprovals(cancellationToken);
            }

            turnOutcome = result.WasCancelled ? TurnOutcome.Cancelled
                : result.HasError ? TurnOutcome.Failed
                : TurnOutcome.Success;
            return result.Success;
        }
        catch (OperationCanceledException)
        {
            ConsoleUI.EndAIResponse();
            ConsoleUI.ShowCancelled();
            turnOutcome = TurnOutcome.Cancelled;
            return false;
        }
        catch (Exception ex)
        {
            ConsoleUI.EndAIResponse();
            ConsoleUI.ShowError("Error", ex.Message);
            turnOutcome = TurnOutcome.Failed;
            return false;
        }
        finally
        {
            turnStopwatch.Stop();
            var toolDelta = request.GetToolInvocationCount() - toolCountAtEntry;
            if (toolDelta < 0)
            {
                toolDelta = 0;
            }

            request.SessionUsageTracker.RecordCompletedTurn(turnStopwatch.Elapsed, toolDelta, turnOutcome);
        }
    }

    private static async Task<CopilotTurnResult> RunSingleTurnAsync(
        string message,
        int promptIndex,
        CancellationToken cancellationToken,
        SessionMessageRequest request)
    {
        var configuredSubagentModel = request.GetConfiguredSubagentModel();
        request.HistoryTracker.SubagentModelFallback = configuredSubagentModel;
        var turnSession = request.TurnSession ?? new CopilotTurnSessionAdapter(request.CopilotSession!);
        var runTurn = request.RunTurn ?? (turnRequest => new CopilotTurnRunner().RunAsync(turnRequest));
        var result = await runTurn(new CopilotTurnRequest
        {
            Session = turnSession,
            Prompt = SessionPromptFlow.BuildPromptForExecutionSafety(message),
            CancellationToken = cancellationToken,
            ToolDescriptions = request.ToolDescriptions,
            DefaultSubagentModel = configuredSubagentModel,
            CreateThinkingIndicator = () => new ConsoleTurnThinkingIndicator(ConsoleUI.CreateLiveThinkingIndicator()),
            Callbacks = new CopilotTurnCallbacks
            {
                StartReasoningBlock = ConsoleUI.StartReasoningBlock,
                WriteReasoningText = ConsoleUI.WriteReasoningText,
                RecordReasoningText = text => request.HistoryTracker.RecordReasoningText(promptIndex, text),
                EndReasoningBlock = ConsoleUI.EndReasoningBlock,
                StartAIResponse = ConsoleUI.StartAIResponse,
                WriteAIResponse = text => ConsoleUI.WriteAIResponse(text),
                EndAIResponse = ConsoleUI.EndAIResponse,
                ShowError = ConsoleUI.ShowError,
                ShowCancelled = ConsoleUI.ShowCancelled,
                ShowLiveStatusNotice = ConsoleUI.ShowLiveStatusNotice,
                RecordMcpToolAction = request.RecordMcpToolAction,
                RecordMcpToolComplete = request.HistoryTracker.RecordMcpToolComplete,
                RecordSubagentStarted = request.HistoryTracker.RecordSubagentStarted,
                RecordSubagentCompleted = request.HistoryTracker.RecordSubagentCompleted,
                RecordSubagentFailed = request.HistoryTracker.RecordSubagentFailed,
                RecordSubagentToolAction = request.HistoryTracker.RecordSubagentToolAction,
                RecordSubagentToolComplete = request.HistoryTracker.RecordSubagentToolComplete,
                RecordSubagentMessageDelta = request.HistoryTracker.RecordSubagentMessageDelta,
                RecordSubagentMessage = request.HistoryTracker.RecordSubagentMessage,
                ShowSubagentStarted = ConsoleUI.ShowSubagentStarted,
                ShowSubagentResult = ConsoleUI.ShowSubagentResult,
                IncrementToolInvocation = request.IncrementToolInvocation
            }
        });

        if (result.HasStartedStreaming && !result.HasError && !result.WasCancelled && !result.WasGuarded)
        {
            await request.Telemetry.RefreshSessionUsageMetricsAsync(request.CopilotSession, cancellationToken);
            var statusBarInfo = request.BuildStatusBarInfo();
            ConsoleUI.WriteStatusBar(statusBarInfo);
            request.HistoryTracker.SetPromptStatusBar(promptIndex, statusBarInfo);
        }

        if (!result.WasCancelled && !result.WasGuarded)
        {
            request.SetPromptReply(promptIndex, result.ResponseText);
            request.SetLastAssistantMessage(result.ResponseText);
        }

        return result;
    }

    private static string BuildRecoveryPrompt(string originalUserMessage, CopilotTurnGuardReason guardReason, string recoveryEvidence)
        => $"""
        [Internal TroubleScout recovery]
        The previous main-agent turn became stuck after diagnostics ({guardReason}). Use the already-collected evidence below to answer the user's original request:

        {originalUserMessage}

        Captured diagnostic evidence:
        {(string.IsNullOrWhiteSpace(recoveryEvidence) ? "(No app-captured diagnostic output was available.)" : recoveryEvidence)}

        Do not call tools, run PowerShell, use MCP or web research, delegate to a subagent, or ask for more data. Do not say diagnostic outputs are unavailable when the captured evidence above contains output. Provide the final concise health summary now. If the evidence is incomplete, say what was collected and what remains unknown, then stop.
        """;

    private static string BuildGuardWarning(CopilotTurnGuardReason guardReason)
        => guardReason == CopilotTurnGuardReason.SilentPreToolStall
            ? "The main agent stopped responding before diagnostics started. Returning control without recovery."
            : "The main agent got stuck after diagnostics. Returning control without rerunning diagnostics.";
}
