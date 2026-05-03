using FluentAssertions;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests.Services;

public class SlashCommandDispatcherTests
{
    [Fact]
    public void SlashCommands_ShouldComeFromRegistrySuggestions()
    {
        SlashCommandDispatcher.SlashCommands.Should().Equal(SlashCommandRegistry.SlashCommands);
    }

    [Theory]
    [InlineData("/mode", "/mode", true)]
    [InlineData("/mode safe", "/mode", true)]
    [InlineData("/modeX", "/mode", false)]
    [InlineData("/server srv01", "/server", true)]
    [InlineData("/serverX", "/server", false)]
    public void IsInvocation_ShouldMatchOnlyExactCommandOrCommandWithArguments(string input, string command, bool expected)
    {
        SlashCommandDispatcher.IsInvocation(input, command).Should().Be(expected);
    }

    [Fact]
    public void Dispatch_WithUnknownSlashCommand_ShouldFallThrough()
    {
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers());

        var result = dispatcher.Dispatch("/does-not-exist");

        result.Handled.Should().BeFalse();
        result.ExitRequested.Should().BeFalse();
    }

    [Fact]
    public void Dispatch_WithHelp_ShouldHandleKnownCommand()
    {
        var calls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            ShowHelp = () => calls++
        });

        var result = dispatcher.Dispatch("/help");

        result.Handled.Should().BeTrue();
        result.ExitRequested.Should().BeFalse();
        calls.Should().Be(1);
    }

    [Theory]
    [InlineData("/exit")]
    [InlineData("/quit")]
    [InlineData("exit")]
    [InlineData("quit")]
    public void Dispatch_WithExitCommand_ShouldRequestExit(string input)
    {
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers());

        var result = dispatcher.Dispatch(input);

        result.Handled.Should().BeTrue();
        result.ExitRequested.Should().BeTrue();
    }

    [Fact]
    public void Dispatch_WithModeWithoutArgument_ShouldShowCurrentModeAndUsage()
    {
        var messages = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetExecutionMode = () => ExecutionMode.Safe,
            ShowInfo = messages.Add
        });

        var result = dispatcher.Dispatch("/mode");

        result.Handled.Should().BeTrue();
        messages.Should().Contain("Current mode: safe");
        messages.Should().Contain("Usage: /mode <safe|yolo>");
    }

    [Fact]
    public void Dispatch_WithInvalidMode_ShouldWarnAndNotSetMode()
    {
        var warnings = new List<string>();
        var setCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            ShowWarning = warnings.Add,
            SetExecutionMode = _ => setCalls++
        });

        var result = dispatcher.Dispatch("/mode maybe");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain("Invalid mode. Use: safe or yolo.");
        setCalls.Should().Be(0);
    }

    [Fact]
    public void Dispatch_WithModeArgument_ShouldSetModeAndShowStatus()
    {
        ExecutionMode? mode = null;
        var statusCalls = 0;
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            SetExecutionMode = value => mode = value,
            SetConsoleExecutionMode = value => mode = value,
            ShowStatus = _ => statusCalls++
        });

        var result = dispatcher.Dispatch("/mode yolo");

        result.Handled.Should().BeTrue();
        mode.Should().Be(ExecutionMode.Yolo);
        statusCalls.Should().Be(1);
    }

    [Fact]
    public void Dispatch_WithThemeArgument_ShouldNormalizePersistAndWarnForUnknownTheme()
    {
        string? appliedTheme = null;
        string? persistedTheme = null;
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            SetTheme = value => appliedTheme = value,
            PersistTheme = value => persistedTheme = value,
            ShowWarning = warnings.Add
        });

        var result = dispatcher.Dispatch("/theme neon");

        result.Handled.Should().BeTrue();
        appliedTheme.Should().Be("dark");
        persistedTheme.Should().Be("dark");
        warnings.Should().Contain("Unknown theme 'neon'. Falling back to 'dark'. Supported: dark, mono.");
    }

    [Fact]
    public void Dispatch_WithSaveAndNoMessage_ShouldShowNoMessageWarning()
    {
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetLastAssistantMessage = () => null,
            ShowWarning = warnings.Add
        });

        var result = dispatcher.Dispatch("/save out.md");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain(message => message.Contains("No assistant message captured yet", StringComparison.Ordinal));
    }

    [Fact]
    public void Dispatch_WithCopyAndNoMessage_ShouldShowNoMessageWarning()
    {
        var warnings = new List<string>();
        var dispatcher = new SlashCommandDispatcher(new SlashCommandHandlers
        {
            GetLastAssistantMessage = () => null,
            ShowWarning = warnings.Add
        });

        var result = dispatcher.Dispatch("/copy");

        result.Handled.Should().BeTrue();
        warnings.Should().Contain(message => message.Contains("No assistant message captured yet", StringComparison.Ordinal));
    }
}
