using FluentAssertions;
using Moq;
using TroubleScout.Services;
using TroubleScout.Tools;
using TroubleScout.UI;
using Xunit;

namespace TroubleScout.Tests.Services;

public class PendingCommandApprovalProcessorTests : IDisposable
{
    private readonly Mock<PowerShellExecutor> _executor = new("localhost");

    public void Dispose()
    {
        _executor.Object.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ProcessAsync_WhenHeadless_ShouldDenyPendingCommandsWithoutPrompting()
    {
        var originalRedirected = ConsoleUI.IsInputRedirectedResolver;
        try
        {
            ConsoleUI.IsInputRedirectedResolver = static () => true;
            _executor.Setup(value => value.ValidateCommand("Restart-Service Spooler"))
                .Returns(new CommandValidation(true, true, "Requires approval"));
            var tools = new DiagnosticTools(_executor.Object, (_, _) => Task.FromResult(true), "localhost");
            var function = tools.GetTools().Single(value => value.Name == "run_powershell");
            await function.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
            {
                ["command"] = "Restart-Service Spooler"
            });
            var sendCount = 0;
            var processor = new PendingCommandApprovalProcessor(
                tools,
                _ => 0,
                (_, _, _) =>
                {
                    sendCount++;
                    return Task.FromResult(true);
                });

            var handled = await processor.ProcessAsync(CancellationToken.None);

            handled.Should().BeFalse();
            tools.PendingCommands.Should().BeEmpty();
            sendCount.Should().Be(0);
            _executor.Verify(value => value.ExecuteAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        }
        finally
        {
            ConsoleUI.IsInputRedirectedResolver = originalRedirected;
        }
    }
}
