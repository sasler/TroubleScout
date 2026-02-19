using FluentAssertions;
using System.Reflection;
using Spectre.Console;
using TroubleScout.UI;
using Xunit;

namespace TroubleScout.Tests.UI;

public class ConsoleUITests
{
    [Theory]
    [InlineData("claude-haiku-4.5", "0.33x")]
    [InlineData("claude-opus-4.6", "3x")]
    [InlineData("claude-sonnet-4.6", "1x")]
    [InlineData("gemini-3-pro-preview", "1x")]
    [InlineData("gpt-5.3-codex", "1x")]
    [InlineData("gpt-5.1-codex-mini", "0.33x")]
    [InlineData("gpt-5-mini", "0x")]
    [InlineData("gpt-4.1", "0x")]
    public void GetInferredRateLabel_ShouldReturnExpectedRates(string modelId, string expected)
    {
        // Act
        var actual = InvokeGetInferredRateLabel(modelId);

        // Assert
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown-model")]
    public void GetInferredRateLabel_ShouldReturnNullForUnknownOrEmpty(string? modelId)
    {
        // Act
        var actual = InvokeGetInferredRateLabel(modelId);

        // Assert
        actual.Should().BeNull();
    }

    [Fact]
    public void GetInferredRateLabel_ShouldBeCaseInsensitiveAndTrimmed()
    {
        // Act
        var actual = InvokeGetInferredRateLabel("  GPT-5.3-CODEX  ");

        // Assert
        actual.Should().Be("1x");
    }

    [Fact]
    public void ParseMarkdownTable_ShouldCreateTableWithColumnsAndRows()
    {
        // Arrange
        var lines = new[]
        {
            "| Name | Status |",
            "| ---- | ------ |",
            "| SQL  | Running |",
            "| IIS  | Stopped |"
        };

        // Act
        var table = ConsoleUI.ParseMarkdownTable(lines);

        // Assert
        table.Should().NotBeNull();
        table!.Columns.Should().HaveCount(2);
        table.Rows.Should().HaveCount(2);
    }

    [Fact]
    public void ParseMarkdownTable_ShouldIgnoreSeparatorOnlyRows()
    {
        // Arrange
        var lines = new[]
        {
            "| --- | --- |",
            "| :--- | ---: |"
        };

        // Act
        var table = ConsoleUI.ParseMarkdownTable(lines);

        // Assert
        table.Should().BeNull();
    }

    [Fact]
    public void ParseMarkdownTable_ShouldHandleHeaderWithoutSeparator()
    {
        // Arrange
        var lines = new[]
        {
            "| Service | State |",
            "| DNS | Running |"
        };

        // Act
        var table = ConsoleUI.ParseMarkdownTable(lines);

        // Assert
        table.Should().NotBeNull();
        table!.Columns.Should().HaveCount(2);
        table.Rows.Should().HaveCount(1);
    }

    private static string? InvokeGetInferredRateLabel(string? modelId)
    {
        var method = typeof(ConsoleUI).GetMethod("GetInferredRateLabel", BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        return method!.Invoke(null, [modelId]) as string;
    }
}
