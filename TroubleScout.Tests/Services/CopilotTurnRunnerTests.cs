using System.Text;
using FluentAssertions;
using GitHub.Copilot.SDK;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests.Services;

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
            SendAsyncHandler = (_, token) => Task.Delay(TimeSpan.FromSeconds(30), token)
        };
        using var cts = new CancellationTokenSource();
        var runTask = CreateRunner().RunAsync(CreateRequest(session, cancellationToken: cts.Token));

        await cts.CancelAsync();

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(3));
        result.WasCancelled.Should().BeTrue();
        result.Success.Should().BeFalse();
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
            return Task.CompletedTask;
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
            return Task.CompletedTask;
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
            return Task.CompletedTask;
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
            return Task.CompletedTask;
        };

        var result = await CreateRunner().RunAsync(CreateRequest(
            session,
            showError: (title, message) => errors.Add((title, message))));

        result.Success.Should().BeFalse();
        result.HasError.Should().BeTrue();
        result.ResponseText.Should().BeEmpty();
        errors.Should().ContainSingle().Which.Should().Be(("Session Error", "boom"));
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
                ShowError = showError ?? ((_, _) => { })
            }
        };

    private sealed class FakeTurnSession : ICopilotTurnSession
    {
        private SessionEventHandler? _handler;

        public bool HasActiveSubscription => _handler != null;

        public Func<MessageOptions, CancellationToken, Task> SendAsyncHandler { get; set; }
            = (_, _) => Task.CompletedTask;

        public IDisposable On(SessionEventHandler handler)
        {
            _handler = handler;
            return new DelegateDisposable(() => _handler = null);
        }

        public Task SendAsync(MessageOptions options, CancellationToken cancellationToken)
            => SendAsyncHandler(options, cancellationToken);

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
