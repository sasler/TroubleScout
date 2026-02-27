using FluentAssertions;
using System.Reflection;
using GitHub.Copilot.SDK;
using Spectre.Console;
using TroubleScout.Services;
using TroubleScout.UI;
using Xunit;

namespace TroubleScout.Tests.UI;

// Spectre AnsiConsole recording uses shared static state; keep this collection sequential.
[CollectionDefinition("ConsoleUI", DisableParallelization = true)]
public class ConsoleUICollection { }

[Collection("ConsoleUI")]
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
    public void ShowCliHelp_ShouldRenderUsageAndOptions_WhenVersionIsProvided()
    {
        // Arrange & Act
        AnsiConsole.Record();
        ConsoleUI.ShowCliHelp("1.2.3");
        var output = AnsiConsole.ExportText();

        // Assert – key CLI help sections and flags must appear
        output.Should().Contain("USAGE");
        output.Should().Contain("OPTIONS");
        output.Should().Contain("--help");
        output.Should().Contain("--server");
        output.Should().Contain("--prompt");
        output.Should().Contain("--model");
        output.Should().Contain("1.2.3");
    }

    [Fact]
    public void ShowCliHelp_ShouldRenderUsageAndOptions_WhenVersionIsNull()
    {
        // Arrange & Act
        AnsiConsole.Record();
        ConsoleUI.ShowCliHelp(null);
        var output = AnsiConsole.ExportText();

        // Assert – key CLI help sections and flags must appear; no version line
        output.Should().Contain("USAGE");
        output.Should().Contain("OPTIONS");
        output.Should().Contain("--help");
        output.Should().NotContain("Version:");
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

    #region ShowStatusPanel Multi-Server Tests

    [Fact]
    public void ShowStatusPanel_WithAdditionalTargets_ShouldShowPluralLabel()
    {
        // Arrange & Act
        AnsiConsole.Record();
        ConsoleUI.ShowStatusPanel(
            "PrimaryServer", "WinRM", true, "gpt-4.1", ExecutionMode.Safe, null,
            additionalTargets: new[] { "ServerA", "ServerB" });
        var output = AnsiConsole.ExportText();

        // Assert
        output.Should().Contain("Target Servers:");
        output.Should().Contain("PrimaryServer");
        output.Should().Contain("ServerA");
        output.Should().Contain("ServerB");
    }

    [Fact]
    public void ShowStatusPanel_WithNoAdditional_ShouldShowSingularLabel()
    {
        // Arrange & Act
        AnsiConsole.Record();
        ConsoleUI.ShowStatusPanel(
            "PrimaryServer", "WinRM", true, "gpt-4.1", ExecutionMode.Safe, null,
            additionalTargets: null);
        var output = AnsiConsole.ExportText();

        // Assert – look for singular label, ensuring no plural variant on the same line
        output.Should().Contain("Target Server:");
        // The plural form "Target Servers:" should not appear without the "WithAdditional" test
        var lines = output.Split('\n');
        lines.Should().Contain(line => line.Contains("Target Server:") && !line.Contains("Target Servers:"));
    }

    #endregion
}
