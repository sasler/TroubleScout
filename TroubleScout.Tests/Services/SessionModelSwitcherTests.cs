using FluentAssertions;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests.Services;

public class SessionModelSwitcherTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ChangeModelAsync_WithBlankModel_DoesNotResolveTargetSource(string? model)
    {
        var resolveCalled = false;
        var request = new SessionModelSwitchRequest
        {
            CopilotClient = null,
            ModelDiscovery = new ModelDiscoveryManager(),
            ResolveTargetSource = _ =>
            {
                resolveCalled = true;
                throw new InvalidOperationException("Blank models should be rejected before provider resolution.");
            },
            IsByokConfigured = () => false,
            IsGitHubCopilotAuthenticated = () => false,
            SetUseByokOpenAi = _ => { },
            DisposeCurrentSession = () => Task.CompletedTask,
            CreateCopilotSession = (_, _) => Task.FromResult(false)
        };

        var result = await SessionModelSwitcher.ChangeModelAsync(model!, request);

        result.Should().BeFalse();
        resolveCalled.Should().BeFalse();
    }
}
