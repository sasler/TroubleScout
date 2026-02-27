using FluentAssertions;
using System.Reflection;
using GitHub.Copilot.SDK;
using Spectre.Console;
using TroubleScout.UI;
using Xunit;

namespace TroubleScout.Tests.UI;

public class ConsoleUITests
{
    [Fact]
    public void GetRateLabel_ShouldReturnNa_WhenBillingIsMissing()
    {
        // Arrange
        var model = new ModelInfo { Id = "model-a", Name = "Model A" };

        // Act
        var actual = InvokeGetRateLabel(model);

        // Assert
        actual.Should().Be("n/a");
    }

    [Fact]
    public void ShowCliHelp_ShouldNotThrow_WhenVersionIsProvided()
    {
        // Act & Assert – should not throw when a version string is given
        var act = () => ConsoleUI.ShowCliHelp("1.2.3");
        act.Should().NotThrow();
    }

    [Fact]
    public void ShowCliHelp_ShouldNotThrow_WhenVersionIsNull()
    {
        // Act & Assert – should not throw when version is omitted
        var act = () => ConsoleUI.ShowCliHelp(null);
        act.Should().NotThrow();
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

    private static string InvokeGetRateLabel(ModelInfo model)
    {
        var method = typeof(ConsoleUI).GetMethod("GetRateLabel", BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        return method!.Invoke(null, [model]) as string ?? string.Empty;
    }
}
