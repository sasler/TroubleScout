using System.Text;
using FluentAssertions;
using GitHub.Copilot;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests.Services;

#pragma warning disable CS0618 // Test fixtures exercise child event attribution exposed by the SDK.
public class CopilotTurnRunnerTests
{
    [Theory]
    [InlineData(14.9, null)]
    [InlineData(15.0, "Waiting for response")]
    [InlineData(29.9, "Waiting for response")]
    [InlineData(30.0, "Connection seems slow")]
    public void GetActivityWatchdogStatus_ShouldReturnExpectedThresholdStatus(
        double idleSeconds,
        string? expectedStatus)
    {
        CopilotTurnRunner.GetActivityWatchdogStatus(idleSeconds).Should().Be(expectedStatus);
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_ShouldReturnCancelled()
    {
        var session = new FakeTurnSession
        {
            SendAsyncHandler = async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return "message-id";
            }
        };
        using var cts = new CancellationTokenSource();
        var runTask = CreateRunner().RunAsync(CreateRequest(session, cancellationToken: cts.Token));

        await cts.CancelAsync();

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(3));
        result.WasCancelled.Should().BeTrue();
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_WhenCancelledAfterSend_ShouldAbortActiveTurn()
    {
        var session = new FakeTurnSession
        {
            SendAsyncHandler = (_, _) => Task.FromResult("message-id")
        };
        using var cts = new CancellationTokenSource();

        var runTask = CreateRunner().RunAsync(CreateRequest(session, cancellationToken: cts.Token));

        await cts.CancelAsync();

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(3));

        result.WasCancelled.Should().BeTrue();
        session.AbortCallCount.Should().Be(1);
    }


    [Fact]
    public async Task RunAsync_WhenSendThrowsCancellation_ShouldUnsubscribeBeforeDisposingIndicator()
    {
        var session = new FakeTurnSession
        {
            SendAsyncHandler = (_, token) => throw new OperationCanceledException(token)
        };
        var wasSubscribedWhenIndicatorDisposed = false;

        var result = await CreateRunner().RunAsync(CreateRequest(
            session,
            createThinkingIndicator: () => new FakeThinkingIndicator
            {
                OnDispose = () => wasSubscribedWhenIndicatorDisposed = session.HasActiveSubscription
            }));

        result.WasCancelled.Should().BeTrue();
        wasSubscribedWhenIndicatorDisposed.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_ShouldAppendAssistantDeltasOnce()
    {
        var output = new StringBuilder();
        var session = new FakeTurnSession();
        session.SendAsyncHandler = (_, _) =>
        {
            var delta = new AssistantMessageDeltaEvent
            {
                Data = new AssistantMessageDeltaData
                {
                    DeltaContent = "hello",
                    MessageId = "message-1"
                }
            };
            session.Emit(delta);
            session.Emit(delta);
            session.Emit(CreateIdle());
            return Task.FromResult("message-id");
        };

        var result = await CreateRunner().RunAsync(CreateRequest(session, writeAiResponse: text => output.Append(text)));

        result.Success.Should().BeTrue();
        result.ResponseText.Should().Be("hello");
        output.ToString().Should().Be("hello");
    }

    [Fact]
    public async Task RunAsync_WhenMessageIdChanges_ShouldInsertLineBreak()
    {
        var session = new FakeTurnSession();
        session.SendAsyncHandler = (_, _) =>
        {
            session.Emit(CreateDelta("first", "message-1"));
            session.Emit(CreateDelta("second", "message-2"));
            session.Emit(CreateIdle());
            return Task.FromResult("message-id");
        };

        var result = await CreateRunner().RunAsync(CreateRequest(session));

        result.Success.Should().BeTrue();
        result.ResponseText.Should().Be($"first{Environment.NewLine}second");
    }

    [Fact]
    public async Task RunAsync_ShouldIgnoreReasoningAfterStreamingStarts()
    {
        var reasoning = new StringBuilder();
        var session = new FakeTurnSession();
        session.SendAsyncHandler = (_, _) =>
        {
            session.Emit(CreateDelta("answer", "message-1"));
            session.Emit(new AssistantReasoningDeltaEvent
            {
                Data = new AssistantReasoningDeltaData
                {
                    DeltaContent = "late reasoning",
                    ReasoningId = "reasoning-1"
                }
            });
            session.Emit(CreateIdle());
            return Task.FromResult("message-id");
        };

        var result = await CreateRunner().RunAsync(CreateRequest(
            session,
            writeReasoningText: text => reasoning.Append(text)));

        result.Success.Should().BeTrue();
        result.ResponseText.Should().Be("answer");
        reasoning.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_WhenSessionError_ShouldFailWithoutSuccessfulResponse()
    {
        var errors = new List<(string Title, string Message)>();
        var session = new FakeTurnSession();
        session.SendAsyncHandler = (_, _) =>
        {
            session.Emit(new SessionErrorEvent
            {
                Data = new SessionErrorData
                {
                    ErrorType = "test",
                    Message = "boom"
                }
            });
            return Task.FromResult("message-id");
        };

        var result = await CreateRunner().RunAsync(CreateRequest(
            session,
            showError: (title, message) => errors.Add((title, message))));

        result.Success.Should().BeFalse();
        result.HasError.Should().BeTrue();
        result.ResponseText.Should().BeEmpty();
        errors.Should().ContainSingle().Which.Should().Be(("Session Error", "boom"));
    }

    [Fact]
    public async Task RunAsync_SubagentLifecycle_ShouldEmitDurableUsageNotices()
    {
        var notices = new List<string>();
        var session = new FakeTurnSession();
        session.SendAsyncHandler = (_, _) =>
        {
            session.Emit(new SubagentStartedEvent
            {
                Data = new SubagentStartedData
                {
                    AgentDisplayName = "Server Evidence Collector",
                    AgentName = "server-evidence-collector",
                    AgentDescription = "Collects evidence",
                    Model = "gpt-5-mini",
                    ToolCallId = "sub-1"
                }
            });
            session.Emit(new SubagentCompletedEvent
            {
                Data = new SubagentCompletedData
                {
                    AgentDisplayName = "Server Evidence Collector",
                    AgentName = "server-evidence-collector",
                    Model = "gpt-5-mini",
                    ToolCallId = "sub-1",
                    TotalTokens = 120,
                    TotalToolCalls = 2,
                    Duration = TimeSpan.FromSeconds(1.5)
                }
            });
            session.Emit(CreateIdle());
            return Task.FromResult("message-id");
        };

        await CreateRunner().RunAsync(CreateRequest(session, showLiveStatusNotice: notices.Add));

        notices.Should().Contain(message => message.Contains("Server Evidence Collector", StringComparison.Ordinal)
            && message.Contains("gpt-5-mini", StringComparison.Ordinal)
            && message.Contains("started", StringComparison.OrdinalIgnoreCase));
        notices.Should().Contain(message => message.Contains("120 tokens", StringComparison.Ordinal)
            && message.Contains("2 tools", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_SubagentFailure_ShouldEmitDurableUsageNotice()
    {
        var notices = new List<string>();
        var session = new FakeTurnSession();
        session.SendAsyncHandler = (_, _) =>
        {
            session.Emit(new SubagentFailedEvent
            {
                Data = new SubagentFailedData
                {
                    AgentDisplayName = "Approval Reviewer",
                    AgentName = "approval",
                    Model = "gpt-4.1",
                    ToolCallId = "sub-2",
                    TotalTokens = 45,
                    TotalToolCalls = 1,
                    Duration = TimeSpan.FromSeconds(2),
                    Error = "timeout"
                }
            });
            session.Emit(CreateIdle());
            return Task.FromResult("message-id");
        };

        await CreateRunner().RunAsync(CreateRequest(session, showLiveStatusNotice: notices.Add));

        notices.Should().Contain(message => message.Contains("Approval Reviewer", StringComparison.Ordinal)
            && message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            && message.Contains("45 tokens", StringComparison.Ordinal)
            && message.Contains("1 tools", StringComparison.Ordinal)
            && message.Contains("2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_SubagentMessage_ShouldAuditWithoutRenderingIntoRootResponse()
    {
        var output = new StringBuilder();
        var captured = new List<(string Parent, string Text)>();
        var session = new FakeTurnSession();
        session.SendAsyncHandler = (_, _) =>
        {
            session.Emit(new AssistantMessageDeltaEvent
            {
                Id = Guid.NewGuid(),
                Data = new AssistantMessageDeltaData
                {
                    MessageId = "child-message",
                    ParentToolCallId = "sub-1",
                    DeltaContent = "delegated finding"
                }
            });
            session.Emit(CreateDelta("root reply", "root-message"));
            session.Emit(CreateIdle());
            return Task.FromResult("message-id");
        };

        var result = await CreateRunner().RunAsync(CreateRequest(
            session,
            writeAiResponse: text => output.Append(text),
            recordSubagentMessageDelta: (parent, text) => captured.Add((parent, text))));

        result.ResponseText.Should().Be("root reply");
        output.ToString().Should().Be("root reply");
        captured.Should().ContainSingle().Which.Should().Be(("sub-1", "delegated finding"));
    }

    private static AssistantMessageDeltaEvent CreateDelta(string content, string messageId)
        => new()
        {
            Id = Guid.NewGuid(),
            Data = new AssistantMessageDeltaData
            {
                DeltaContent = content,
                MessageId = messageId
            }
        };

    private static SessionIdleEvent CreateIdle()
        => new()
        {
            Data = new SessionIdleData()
        };

    private static CopilotTurnRunner CreateRunner() => new();

    private static CopilotTurnRequest CreateRequest(
        FakeTurnSession session,
        CancellationToken cancellationToken = default,
        Action<string>? writeAiResponse = null,
        Action<string>? writeReasoningText = null,
        Action<string, string>? showError = null,
        Action<string>? showLiveStatusNotice = null,
        Action<string, string>? recordSubagentMessageDelta = null,
        Func<ITurnThinkingIndicator>? createThinkingIndicator = null)
        => new()
        {
            Session = session,
            Prompt = "prompt",
            CancellationToken = cancellationToken,
            ToolDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            CreateThinkingIndicator = createThinkingIndicator ?? (() => new FakeThinkingIndicator()),
            Callbacks = new CopilotTurnCallbacks
            {
                WriteAIResponse = writeAiResponse ?? (_ => { }),
                WriteReasoningText = writeReasoningText ?? (_ => { }),
                ShowError = showError ?? ((_, _) => { }),
                ShowLiveStatusNotice = showLiveStatusNotice ?? (_ => { }),
                RecordSubagentMessageDelta = recordSubagentMessageDelta ?? ((_, _) => { })
            }
        };

    private sealed class FakeTurnSession : ICopilotTurnSession
    {
        private Action<SessionEvent>? _handler;

        public bool HasActiveSubscription => _handler != null;

        public Func<MessageOptions, CancellationToken, Task<string>> SendAsyncHandler { get; set; }
            = (_, _) => Task.FromResult("message-id");

        public int AbortCallCount { get; private set; }

        public IDisposable On(Action<SessionEvent> handler)
        {
            _handler = handler;
            return new DelegateDisposable(() => _handler = null);
        }

        public Task<string> SendAsync(MessageOptions options, CancellationToken cancellationToken)
            => SendAsyncHandler(options, cancellationToken);

        public Task AbortAsync(CancellationToken cancellationToken)
        {
            AbortCallCount++;
            return Task.CompletedTask;
        }

        public void Emit(SessionEvent evt) => _handler?.Invoke(evt);
    }

    private sealed class FakeThinkingIndicator : ITurnThinkingIndicator
    {
        public Action? OnDispose { get; init; }
        public TimeSpan Elapsed { get; set; }
        public void Start() { }
        public void UpdateStatus(string status) { }
        public void ShowToolExecution(string toolName) { }
        public void StopForResponse() { }
        public void Dispose() => OnDispose?.Invoke();
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
#pragma warning restore CS0618
