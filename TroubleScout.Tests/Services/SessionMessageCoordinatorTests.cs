using FluentAssertions;
using GitHub.Copilot;
using TroubleScout.Services;
using TroubleScout.Tools;
using TroubleScout.UI;
using Xunit;

namespace TroubleScout.Tests.Services;

public class SessionMessageCoordinatorTests
{
    [Fact]
    public async Task SendMessageAsync_GuardedDirectDiagnosticTurn_ShouldAttemptOneRecoveryPrompt()
    {
        var history = new ConversationHistoryTracker();
        var promptIndex = history.RecordPrompt("how is this computer doing?");
        history.RecordCommandAction(new CommandActionLog(
            DateTimeOffset.UtcNow,
            "localhost",
            "Get-Counter",
            "CPUPercent : 12\nFreeMemoryGB : 10.5\nDiskFreeGB : 221",
            CommandApprovalState.StrictReadOnly,
            "Main Agent PowerShell",
            Description: "Read performance counters"));
        var prompts = new List<string>();
        var recoveryBeginCount = 0;
        var recoveryDisposeCount = 0;
        var turnResults = new Queue<CopilotTurnResult>([
            new(
                Success: false,
                HasError: false,
                WasCancelled: false,
                HasStartedStreaming: false,
                ResponseText: string.Empty,
                GuardReason: CopilotTurnGuardReason.RepeatedStatusAfterDiagnostics,
                HasDirectDiagnosticEvidence: true),
            new(
                Success: true,
                HasError: false,
                WasCancelled: false,
                HasStartedStreaming: false,
                ResponseText: "Final health summary")
        ]);
        var request = CreateRequest(
            history,
            turn =>
            {
                prompts.Add(turn.Prompt);
                return Task.FromResult(turnResults.Dequeue());
            },
            beginRecovery: () =>
            {
                recoveryBeginCount++;
                return new DelegateDisposable(() => recoveryDisposeCount++);
            });

        var result = await SessionMessageCoordinator.SendMessageAsync(
            "how is this computer doing?",
            promptIndex,
            CancellationToken.None,
            request);

        result.Should().BeTrue();
        prompts.Should().HaveCount(2);
        prompts[1].Should().Contain("already-collected evidence");
        prompts[1].Should().Contain("CPUPercent : 12");
        prompts[1].Should().Contain("Do not say diagnostic outputs are unavailable");
        prompts[1].Should().Contain("Do not call tools");
        recoveryBeginCount.Should().Be(1);
        recoveryDisposeCount.Should().Be(1);
        var snapshot = history.GetRecordedPromptSnapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].Prompt.Should().Be("how is this computer doing?");
        snapshot[0].AgentReply.Should().Be("Final health summary");
    }

    [Fact]
    public async Task SendMessageAsync_WithoutPromptIndex_ShouldRecordPromptForRecoveryEvidence()
    {
        var history = new ConversationHistoryTracker();
        var prompts = new List<string>();
        var callCount = 0;
        var request = CreateRequest(
            history,
            turn =>
            {
                prompts.Add(turn.Prompt);
                callCount++;
                if (callCount == 1)
                {
                    history.RecordCommandAction(new CommandActionLog(
                        DateTimeOffset.UtcNow,
                        "localhost",
                        "Get-Volume",
                        "DriveLetter : C\nSizeRemaining : 221 GB",
                        CommandApprovalState.StrictReadOnly,
                        "Main Agent PowerShell",
                        Description: "Read disk volume information"));

                    return Task.FromResult(new CopilotTurnResult(
                        Success: false,
                        HasError: false,
                        WasCancelled: false,
                        HasStartedStreaming: false,
                        ResponseText: string.Empty,
                        GuardReason: CopilotTurnGuardReason.PostDiagnosticStall,
                        HasDirectDiagnosticEvidence: true));
                }

                return Task.FromResult(new CopilotTurnResult(
                    Success: true,
                    HasError: false,
                    WasCancelled: false,
                    HasStartedStreaming: false,
                    ResponseText: "Final health summary"));
            });

        var result = await SessionMessageCoordinator.SendMessageAsync(
            "how is this computer doing?",
            promptIndexOverride: null,
            CancellationToken.None,
            request);

        result.Should().BeTrue();
        prompts.Should().HaveCount(2);
        prompts[1].Should().Contain("SizeRemaining : 221 GB");
        var snapshot = history.GetRecordedPromptSnapshot();
        snapshot.Should().ContainSingle();
        snapshot[0].Prompt.Should().Be("how is this computer doing?");
        snapshot[0].Actions.Should().ContainSingle(action => action.Description == "Read disk volume information");
        snapshot[0].AgentReply.Should().Be("Final health summary");
    }

    [Fact]
    public async Task SendMessageAsync_WhenRecoveryAlsoGuards_ShouldNotRetryAgain()
    {
        var history = new ConversationHistoryTracker();
        var promptIndex = history.RecordPrompt("how is this computer doing?");
        var warnings = new List<string>();
        var callCount = 0;
        var request = CreateRequest(
            history,
            _ =>
            {
                callCount++;
                return Task.FromResult(new CopilotTurnResult(
                    Success: false,
                    HasError: false,
                    WasCancelled: false,
                    HasStartedStreaming: false,
                    ResponseText: string.Empty,
                    GuardReason: CopilotTurnGuardReason.PostDiagnosticStall,
                    HasDirectDiagnosticEvidence: true));
            },
            beginRecovery: () => new DelegateDisposable(() => { }),
            showWarning: warnings.Add);

        var result = await SessionMessageCoordinator.SendMessageAsync(
            "how is this computer doing?",
            promptIndex,
            CancellationToken.None,
            request);

        result.Should().BeFalse();
        callCount.Should().Be(2);
        warnings.Should().ContainSingle(message => message.Contains("stuck after diagnostics", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SendMessageAsync_SilentPreToolGuard_ShouldReturnControlWithoutRecovery()
    {
        var history = new ConversationHistoryTracker();
        var promptIndex = history.RecordPrompt("how is this computer doing?");
        var warnings = new List<string>();
        var callCount = 0;
        var request = CreateRequest(
            history,
            _ =>
            {
                callCount++;
                return Task.FromResult(new CopilotTurnResult(
                    Success: false,
                    HasError: false,
                    WasCancelled: false,
                    HasStartedStreaming: false,
                    ResponseText: string.Empty,
                    GuardReason: CopilotTurnGuardReason.SilentPreToolStall,
                    HasDirectDiagnosticEvidence: false));
            },
            beginRecovery: () => throw new InvalidOperationException("Recovery should not start without evidence."),
            showWarning: warnings.Add);

        var result = await SessionMessageCoordinator.SendMessageAsync(
            "how is this computer doing?",
            promptIndex,
            CancellationToken.None,
            request);

        result.Should().BeFalse();
        callCount.Should().Be(1);
        warnings.Should().ContainSingle(message => message.Contains("before diagnostics started", StringComparison.OrdinalIgnoreCase));
    }

    private static SessionMessageRequest CreateRequest(
        ConversationHistoryTracker history,
        Func<CopilotTurnRequest, Task<CopilotTurnResult>> runTurn,
        Func<IDisposable>? beginRecovery = null,
        Action<string>? showWarning = null)
    {
        var usage = new SessionUsageTracker();
        return new SessionMessageRequest
        {
            CopilotSession = null,
            TurnSession = new FakeTurnSession(),
            HistoryTracker = history,
            SessionUsageTracker = usage,
            Telemetry = CreateTelemetry(usage),
            ToolDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            GetToolInvocationCount = () => 0,
            IncrementToolInvocation = () => { },
            BuildStatusBarInfo = () => new StatusBarInfo("model", "provider", null, null, null, 0, null),
            GetConfiguredSubagentModel = () => null,
            ProcessPendingApprovals = _ => Task.FromResult(true),
            RecordPrompt = history.RecordPrompt,
            SetPromptReply = history.SetPromptReply,
            SetLastAssistantMessage = _ => { },
            RecordMcpToolAction = _ => { },
            RunTurn = runTurn,
            BeginSynthesisOnlyRecoveryTurn = beginRecovery ?? (() => new DelegateDisposable(() => { })),
            ShowWarning = showWarning ?? (_ => { })
        };
    }

    private static SessionEventTelemetry CreateTelemetry(SessionUsageTracker usage)
        => new(
            usage,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            useByokOpenAi: () => false,
            getSelectedModelInfo: () => null,
            getActiveByokPricing: () => null,
            setSelectedModel: _ => { },
            setSelectedReasoningEffort: _ => { },
            setCopilotVersion: _ => { });

    private sealed class FakeTurnSession : ICopilotTurnSession
    {
        public IDisposable On(Action<SessionEvent> handler) => new DelegateDisposable(() => { });

        public Task<string> SendAsync(MessageOptions options, CancellationToken cancellationToken)
            => Task.FromResult("message-id");

        public Task AbortAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
