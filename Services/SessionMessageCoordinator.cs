using GitHub.Copilot;
using TroubleScout.UI;

namespace TroubleScout.Services;

internal sealed class SessionMessageRequest
{
    internal required CopilotSession? CopilotSession { get; init; }
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
}

internal static class SessionMessageCoordinator
{
    internal static async Task<bool> SendMessageAsync(
        string userMessage,
        int? promptIndexOverride,
        CancellationToken cancellationToken,
        SessionMessageRequest request)
    {
        if (request.CopilotSession == null)
        {
            ConsoleUI.ShowError("Not Initialized", "Session not initialized. Call InitializeAsync first.");
            return false;
        }

        var turnStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var toolCountAtEntry = request.GetToolInvocationCount();
        var turnOutcome = TurnOutcome.Failed;
        var promptIndex = promptIndexOverride ?? request.HistoryTracker.GetLatestPromptIndex();

        try
        {
            var runner = new CopilotTurnRunner();
            var configuredSubagentModel = request.GetConfiguredSubagentModel();
            request.HistoryTracker.SubagentModelFallback = configuredSubagentModel;
            var result = await runner.RunAsync(new CopilotTurnRequest
            {
                Session = new CopilotTurnSessionAdapter(request.CopilotSession),
                Prompt = SessionPromptFlow.BuildPromptForExecutionSafety(userMessage),
                CancellationToken = cancellationToken,
                ToolDescriptions = request.ToolDescriptions,
                DefaultSubagentModel = configuredSubagentModel,
                CreateThinkingIndicator = () => new ConsoleTurnThinkingIndicator(ConsoleUI.CreateLiveThinkingIndicator()),
                Callbacks = new CopilotTurnCallbacks
                {
                    StartReasoningBlock = ConsoleUI.StartReasoningBlock,
                    WriteReasoningText = ConsoleUI.WriteReasoningText,
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

            if (result.HasStartedStreaming && !result.HasError && !result.WasCancelled && !result.WasLoopGuardAborted)
            {
                await request.Telemetry.RefreshSessionUsageMetricsAsync(request.CopilotSession, cancellationToken);
                var statusBarInfo = request.BuildStatusBarInfo();
                ConsoleUI.WriteStatusBar(statusBarInfo);
                request.HistoryTracker.SetPromptStatusBar(promptIndex, statusBarInfo);
            }

            if (result.WasCancelled)
            {
                turnOutcome = TurnOutcome.Cancelled;
                return false;
            }

            request.SetPromptReply(promptIndex, result.ResponseText);
            request.SetLastAssistantMessage(result.ResponseText);

            if (!result.HasError)
            {
                await request.ProcessPendingApprovals(cancellationToken);
            }

            if (result.WasLoopGuardAborted)
            {
                var completedToolDelta = request.GetToolInvocationCount() - toolCountAtEntry;
                if (completedToolDelta > 0)
                {
                    ConsoleUI.ShowWarning("Response stopped because the assistant got stuck after diagnostics. TroubleScout returned control; review /report for the partial response and tool results.");
                }
                else if (string.IsNullOrWhiteSpace(result.ResponseText))
                {
                    ConsoleUI.ShowWarning("Response stopped because the assistant did not produce any events before the timeout. TroubleScout returned control; try again or switch models with /model.");
                }
                else
                {
                    ConsoleUI.ShowWarning("Response stopped because the assistant got stuck while responding. TroubleScout returned control; try again or switch models with /model.");
                }
                turnOutcome = TurnOutcome.Success;
                return true;
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
}
