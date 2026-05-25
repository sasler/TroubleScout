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
        bool showPostAnalysisActionPrompt,
        bool forcePostAnalysisActionPrompt,
        SessionMessageRequest request,
        Func<string, int?, CancellationToken, bool, bool, Task<bool>> sendMessage)
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
            var result = await runner.RunAsync(new CopilotTurnRequest
            {
                Session = new CopilotTurnSessionAdapter(request.CopilotSession),
                Prompt = SessionPromptFlow.BuildPromptForExecutionSafety(userMessage),
                CancellationToken = cancellationToken,
                ToolDescriptions = request.ToolDescriptions,
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
                    IncrementToolInvocation = request.IncrementToolInvocation
                }
            });

            if (result.HasStartedStreaming && !result.HasError && !result.WasCancelled)
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

            var approvalFollowUpHandled = false;
            if (!result.HasError)
            {
                approvalFollowUpHandled = await request.ProcessPendingApprovals(cancellationToken);
            }

            if (!approvalFollowUpHandled
                && !result.HasError
                && showPostAnalysisActionPrompt
                && SessionPromptFlow.ShouldOfferPostAnalysisActionPrompt(result.ResponseText, forcePostAnalysisActionPrompt))
            {
                turnOutcome = TurnOutcome.Success;
                return await SessionPromptFlow.HandlePostAnalysisActionAsync(
                    ConsoleUI.PromptPostAnalysisAction,
                    request.RecordPrompt,
                    (prompt, promptIndexForFollowUp, token, showPrompt, forcePrompt) => sendMessage(
                        prompt,
                        promptIndexForFollowUp,
                        token,
                        showPrompt,
                        forcePrompt),
                    ConsoleUI.ShowInfo,
                    cancellationToken);
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
