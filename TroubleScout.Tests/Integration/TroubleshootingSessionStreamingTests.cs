using System.Reflection;
using FluentAssertions;
using Moq;
using Xunit;
using GitHub.Copilot.SDK;

namespace TroubleScout.Tests.Integration;

public class TroubleshootingSessionStreamingTests : IAsyncDisposable
{
    private readonly TroubleshootingSession _session;

    public TroubleshootingSessionStreamingTests()
    {
        _session = new TroubleshootingSession("localhost");
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SendMessageAsync_HandlesStreamingDeltasAndCompletion()
    {
        // Arrange
        var sessionMock = new Mock<CopilotSession>();

        // Capture the subscriber callback supplied to On(...)
        SessionEventHandler? capturedCallback = null;
        sessionMock.Setup(s => s.On(It.IsAny<SessionEventHandler>() ))
            .Returns<SessionEventHandler>(cb =>
            {
                capturedCallback = cb;
                return Mock.Of<IDisposable>();
            });

        // Setup SendAsync to invoke the captured callback with a delta and then SessionIdle
        sessionMock.Setup(s => s.SendAsync(It.IsAny<MessageOptions>()))
            .Returns(async () =>
            {
                // Give the test a chance to attach
                await Task.Delay(10);

                // Use reflection to construct event types so tests remain resilient across SDK versions
                var asm = typeof(CopilotClient).Assembly;
                var deltaType = asm.GetType("GitHub.Copilot.SDK.AssistantMessageDeltaEvent");
                var idleType = asm.GetType("GitHub.Copilot.SDK.SessionIdleEvent");

                if (deltaType == null || idleType == null)
                    throw new InvalidOperationException("Expected event types not found in SDK assembly");

                var delta = Activator.CreateInstance(deltaType)!;
                var deltaIdProp = deltaType.GetProperty("Id");
                deltaIdProp?.SetValue(delta, Guid.NewGuid());

                var dataProp = deltaType.GetProperty("Data");
                if (dataProp != null)
                {
                    var dataType = dataProp.PropertyType;
                    var data = Activator.CreateInstance(dataType)!;
                    var contentProp = dataType.GetProperty("DeltaContent");
                    contentProp?.SetValue(data, "hello");
                    dataProp.SetValue(delta, data);
                }

                var idleEvt = Activator.CreateInstance(idleType)!;

                // Invoke events
                capturedCallback?.Invoke(delta);
                capturedCallback?.Invoke(idleEvt);

                await Task.CompletedTask;
            });

        // Inject mock session into private field
        var sessionField = typeof(TroubleshootingSession).GetField("_copilotSession", BindingFlags.NonPublic | BindingFlags.Instance)!;
        sessionField.SetValue(_session, sessionMock.Object);

        // Act
        var result = await _session.SendMessageAsync("test message");

        // Assert
        result.Should().BeTrue();
    }
}
