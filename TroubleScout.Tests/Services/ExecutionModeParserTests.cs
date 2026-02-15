using FluentAssertions;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests.Services;

public class ExecutionModeParserTests
{
    [Theory]
    [InlineData("safe", ExecutionMode.Safe)]
    [InlineData("SAFE", ExecutionMode.Safe)]
    [InlineData("yolo", ExecutionMode.Yolo)]
    public void TryParse_WithValidValues_ShouldParse(string input, ExecutionMode expected)
    {
        var success = ExecutionModeParser.TryParse(input, out var mode);

        success.Should().BeTrue();
        mode.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("change")]
    [InlineData("unknown")]
    public void TryParse_WithInvalidValues_ShouldFail(string input)
    {
        var success = ExecutionModeParser.TryParse(input, out _);

        success.Should().BeFalse();
    }
}
