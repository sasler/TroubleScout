using FluentAssertions;
using System.Reflection;
using System.Globalization;
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
    public void GetRateLabel_ShouldReturnPremiumMultiplier_WhenBillingExists()
    {
        // Arrange
        var model = new ModelInfo
        {
            Id = "gpt-4.1",
            Name = "GPT 4.1",
            Billing = new ModelBilling { Multiplier = 0.25 }
        };

        // Act
        var actual = InvokeGetRateLabel(model);

        // Assert
        actual.Should().Be("0.25x premium");
    }

    [Fact]
    public void ShowModelSelectionSummary_ShouldRenderSelectedModelDetails()
    {
        // Arrange & Act
        AnsiConsole.Record();
        ConsoleUI.ShowModelSelectionSummary("gpt-4.1", [("Provider", "GitHub Copilot"), ("Context window", "128k")]);
        var output = AnsiConsole.ExportText();

        // Assert
        output.Should().Contain("Model selected");
        output.Should().Contain("gpt-4.1");
        output.Should().Contain("GitHub Copilot");
        output.Should().Contain("128k");
    }

    [Fact]
    public void PromptModelSwitchBehavior_WhenInputRedirected_ShouldDefaultToCleanSession()
    {
        var originalResolver = ConsoleUI.IsInputRedirectedResolver;
        var originalPromptOverride = ConsoleUI.ModelSwitchBehaviorPromptOverride;

        try
        {
            ConsoleUI.IsInputRedirectedResolver = static () => true;
            ConsoleUI.ModelSwitchBehaviorPromptOverride = null;

            var result = ConsoleUI.PromptModelSwitchBehavior("gpt-4.1", "claude-sonnet-4.6");

            result.Should().Be(TroubleshootingSession.ModelSwitchBehavior.CleanSession);
        }
        finally
        {
            ConsoleUI.IsInputRedirectedResolver = originalResolver;
            ConsoleUI.ModelSwitchBehaviorPromptOverride = originalPromptOverride;
        }
    }

    [Fact]
    public void PromptModelSwitchBehavior_WhenOverrideChoosesSecondOpinion_ShouldReturnSecondOpinion()
    {
        var originalResolver = ConsoleUI.IsInputRedirectedResolver;
        var originalPromptOverride = ConsoleUI.ModelSwitchBehaviorPromptOverride;

        try
        {
            ConsoleUI.IsInputRedirectedResolver = static () => false;
            ConsoleUI.ModelSwitchBehaviorPromptOverride = (_, choices) =>
                choices.Single(choice => choice.Contains("second opinion", StringComparison.OrdinalIgnoreCase));

            var result = ConsoleUI.PromptModelSwitchBehavior("gpt-4.1", "claude-sonnet-4.6");

            result.Should().Be(TroubleshootingSession.ModelSwitchBehavior.SecondOpinion);
        }
        finally
        {
            ConsoleUI.IsInputRedirectedResolver = originalResolver;
            ConsoleUI.ModelSwitchBehaviorPromptOverride = originalPromptOverride;
        }
    }

    [Fact]
    public void PromptModelSwitchBehavior_WhenOverrideChoosesCancel_ShouldReturnNull()
    {
        var originalResolver = ConsoleUI.IsInputRedirectedResolver;
        var originalPromptOverride = ConsoleUI.ModelSwitchBehaviorPromptOverride;

        try
        {
            ConsoleUI.IsInputRedirectedResolver = static () => false;
            ConsoleUI.ModelSwitchBehaviorPromptOverride = (_, choices) =>
                choices.Single(choice => choice.Contains("Cancel", StringComparison.OrdinalIgnoreCase));

            var result = ConsoleUI.PromptModelSwitchBehavior("gpt-4.1", "claude-sonnet-4.6");

            result.Should().BeNull();
        }
        finally
        {
            ConsoleUI.IsInputRedirectedResolver = originalResolver;
            ConsoleUI.ModelSwitchBehaviorPromptOverride = originalPromptOverride;
        }
    }

    [Fact]
    public void PromptPostAnalysisAction_WhenInputRedirected_ShouldReturnStop()
    {
        var originalResolver = ConsoleUI.IsInputRedirectedResolver;
        var originalPromptOverride = ConsoleUI.PostAnalysisActionPromptOverride;

        try
        {
            ConsoleUI.IsInputRedirectedResolver = static () => true;
            ConsoleUI.PostAnalysisActionPromptOverride = null;

            var result = ConsoleUI.PromptPostAnalysisAction();

            result.Should().Be(PostAnalysisAction.Stop);
        }
        finally
        {
            ConsoleUI.IsInputRedirectedResolver = originalResolver;
            ConsoleUI.PostAnalysisActionPromptOverride = originalPromptOverride;
        }
    }

    [Fact]
    public void PromptPostAnalysisAction_WhenOverrideChoosesApplyFix_ShouldReturnApplyFix()
    {
        var originalResolver = ConsoleUI.IsInputRedirectedResolver;
        var originalPromptOverride = ConsoleUI.PostAnalysisActionPromptOverride;

        try
        {
            ConsoleUI.IsInputRedirectedResolver = static () => false;
            ConsoleUI.PostAnalysisActionPromptOverride = static () => PostAnalysisAction.ApplyFix;

            var result = ConsoleUI.PromptPostAnalysisAction();

            result.Should().Be(PostAnalysisAction.ApplyFix);
        }
        finally
        {
            ConsoleUI.IsInputRedirectedResolver = originalResolver;
            ConsoleUI.PostAnalysisActionPromptOverride = originalPromptOverride;
        }
    }

    [Fact]
    public void BuildTerminalTitleSequence_ShouldEmitOscTitleSequence()
    {
        var sequence = ConsoleUI.BuildTerminalTitleSequence("TroubleScout");

        sequence.Should().Be("\u001b]0;TroubleScout\u0007");
    }

    [Fact]
    public void BuildWindowsTerminalProgressSequence_ShouldEmitIndeterminateOscSequence()
    {
        var sequence = ConsoleUI.BuildWindowsTerminalProgressSequence(TerminalProgressState.Indeterminate);

        sequence.Should().Be("\u001b]9;4;3;0\u0007");
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
        output.Should().Contain("--jea");
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
        output.Should().Contain("--jea");
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

    [Fact]
    public void MarkdownStreamRenderer_ParseMarkdownTable_ShouldMatchConsoleUiBehavior()
    {
        // Arrange
        var lines = new[]
        {
            "| Name | Status |",
            "| ---- | ------ |",
            "| SQL  | Running |",
            "| IIS  | Stopped |"
        };
        var renderer = new MarkdownStreamRenderer();

        // Act
        var table = renderer.ParseMarkdownTable(lines);

        // Assert
        table.Should().NotBeNull();
        table!.Columns.Should().HaveCount(2);
        table.Rows.Should().HaveCount(2);
    }

    private static string InvokeGetRateLabel(ModelInfo model)
        => ModelPickerUI.GetRateLabel(model);

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

        // Assert — new Table layout uses "Target Servers" as column value
        output.Should().Contain("Target Servers");
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

        // Assert — singular label in the Table column
        output.Should().Contain("Target");
        output.Should().Contain("PrimaryServer");
    }

    #endregion

    #region LiveThinkingIndicator Pause/Resume Tests

    [Fact]
    public void LiveThinkingIndicator_PauseForApproval_ShouldSuppressSpinnerWrites()
    {
        // Arrange – capture console output
        var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            using var indicator = new LiveThinkingIndicator();
            indicator.Start();

            // Let the spinner run briefly to confirm it writes
            Thread.Sleep(350);
            var beforePause = sw.ToString();
            beforePause.Should().NotBeEmpty("spinner should have written output before pause");

            // Act – pause
            LiveThinkingIndicator.PauseForApproval();
            sw.GetStringBuilder().Clear();

            // Wait and verify no new writes
            Thread.Sleep(350);
            var afterPause = sw.ToString();
            afterPause.Should().BeEmpty("spinner should not write while paused for approval");
        }
        finally
        {
            LiveThinkingIndicator.ResumeAfterApproval();
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void LiveThinkingIndicator_ResumeAfterApproval_ShouldReEnableSpinnerWrites()
    {
        // Arrange
        var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            using var indicator = new LiveThinkingIndicator();
            indicator.Start();
            Thread.Sleep(300);

            // Pause then resume
            LiveThinkingIndicator.PauseForApproval();
            Thread.Sleep(300);
            sw.GetStringBuilder().Clear();
            LiveThinkingIndicator.ResumeAfterApproval();

            // Act – wait for spinner to write again
            Thread.Sleep(350);
            var afterResume = sw.ToString();

            // Assert
            afterResume.Should().NotBeEmpty("spinner should resume writing after ResumeAfterApproval");
        }
        finally
        {
            LiveThinkingIndicator.ResumeAfterApproval();
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void PromptCommandApproval_ShouldPauseAndResumeIndicator()
    {
        // This test verifies the static _approvalInProgress flag is set/cleared
        // by checking the public Pause/Resume methods work correctly in isolation,
        // since PromptCommandApproval uses interactive console prompts.

        // Arrange – ensure clean state
        LiveThinkingIndicator.ResumeAfterApproval();

        // Act – simulate what PromptCommandApproval does internally
        LiveThinkingIndicator.PauseForApproval();
        var pausedState = LiveThinkingIndicator.IsApprovalInProgress;

        LiveThinkingIndicator.ResumeAfterApproval();
        var resumedState = LiveThinkingIndicator.IsApprovalInProgress;

        // Assert
        pausedState.Should().BeTrue("PauseForApproval should set the approval flag");
        resumedState.Should().BeFalse("ResumeAfterApproval should clear the approval flag");
    }

    #region ApprovalResult Enum Tests

    [Fact]
    public void ApprovalResult_ShouldHaveTwoValues()
    {
        var values = Enum.GetValues<ApprovalResult>();
        values.Should().HaveCount(2);
        values.Should().Contain(ApprovalResult.Approved);
        values.Should().Contain(ApprovalResult.Denied);
    }

    #endregion

    #region ShowCommandExplanation Tests

    [Fact]
    public void ShowCommandExplanation_ShouldRenderCommandAndReason()
    {
        var method = typeof(ConsoleUI).GetMethod("ShowCommandExplanation",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("ShowCommandExplanation should exist as a private static method");

        AnsiConsole.Record();
        method!.Invoke(null, new object?[] { "Get-Process", "Diagnostic reason", "I need process info to check CPU usage", null });
        var output = AnsiConsole.ExportText();

        output.Should().Contain("Get-Process");
        output.Should().Contain("Diagnostic reason");
        output.Should().Contain("I need process info to check CPU usage");
    }

    #endregion

    #region StatusBarInfo Tests

    [Fact]
    public void StatusBarInfo_Empty_ShouldHaveNullFields()
    {
        var empty = StatusBarInfo.Empty;
        empty.Model.Should().BeNull();
        empty.Provider.Should().BeNull();
        empty.ReasoningEffort.Should().BeNull();
        empty.InputTokens.Should().BeNull();
        empty.OutputTokens.Should().BeNull();
        empty.TotalTokens.Should().BeNull();
        empty.ToolInvocations.Should().Be(0);
    }

    [Fact]
    public void FormatCompactTokenCount_SmallNumber_ShouldReturnPlain()
    {
        ConsoleUI.FormatCompactTokenCount(500).Should().Be("500");
    }

    [Fact]
    public void FormatCompactTokenCount_Thousands_ShouldReturnKFormat()
    {
        ConsoleUI.FormatCompactTokenCount(1500).Should().Be("1.5k");
        ConsoleUI.FormatCompactTokenCount(25000).Should().Be("25k");
    }

    [Fact]
    public void FormatCompactTokenCount_Millions_ShouldReturnMFormat()
    {
        ConsoleUI.FormatCompactTokenCount(1_500_000).Should().Be("1.5M");
    }

    [Fact]
    public void FormatCompactTokenCount_ShouldUseInvariantCultureForCompactFormats()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

            ConsoleUI.FormatCompactTokenCount(1_500).Should().Be("1.5k");
            ConsoleUI.FormatCompactTokenCount(1_500_000).Should().Be("1.5M");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void WriteStatusBar_WithFullInfo_ShouldRenderModelAndTokens()
    {
        var info = new StatusBarInfo(
            Model: "gpt-4.1",
            Provider: "GitHub Copilot",
            InputTokens: 1234,
            OutputTokens: 567,
            TotalTokens: 1801,
            ToolInvocations: 3,
            SessionId: "TS-test")
        {
            ReasoningEffort = "high"
        };

        AnsiConsole.Record();
        ConsoleUI.WriteStatusBar(info);
        var output = AnsiConsole.ExportText();

        output.Should().Contain("gpt-4.1");
        output.Should().Contain("GitHub Copilot");
        output.Should().Contain("high");
        output.Should().Contain("1.2k");
        output.Should().Contain("567");
        output.Should().Contain("3");
    }

    [Fact]
    public void WriteStatusBar_WithNullUsage_ShouldStillRenderModel()
    {
        var info = new StatusBarInfo(
            Model: "claude-sonnet",
            Provider: "BYOK",
            InputTokens: null,
            OutputTokens: null,
            TotalTokens: null,
            ToolInvocations: 0,
            SessionId: null);

        AnsiConsole.Record();
        ConsoleUI.WriteStatusBar(info);
        var output = AnsiConsole.ExportText();

        output.Should().Contain("claude-sonnet");
        output.Should().Contain("BYOK");
    }

    [Fact]
    public void WriteStatusBar_WithOnlyTotalTokens_ShouldRenderTotalTokenCount()
    {
        var info = new StatusBarInfo(
            Model: "gpt-4.1",
            Provider: null,
            InputTokens: null,
            OutputTokens: null,
            TotalTokens: 5000,
            ToolInvocations: 0,
            SessionId: null);

        AnsiConsole.Record();
        ConsoleUI.WriteStatusBar(info);
        var output = AnsiConsole.ExportText();

        output.Should().Contain("gpt-4.1");
        output.Should().Contain("5k");
    }

    #endregion

    #endregion

    #region Cancellation UX Tests

    [Fact]
    public void ShowCancelled_ShouldRenderCancelledMessage()
    {
        // Arrange & Act
        AnsiConsole.Record();
        ConsoleUI.ShowCancelled();
        var output = AnsiConsole.ExportText();

        // Assert
        output.Should().Contain("Cancelled");
    }

    #region Retry Prompt Tests

    [Fact]
    public void ShowRetryPrompt_MethodShouldExist()
    {
        var method = typeof(ConsoleUI).GetMethod("ShowRetryPrompt",
            BindingFlags.Public | BindingFlags.Static);
        method.Should().NotBeNull("ShowRetryPrompt should exist as a public static method");
        method!.ReturnType.Should().Be(typeof(bool));
        method.GetParameters().Should().HaveCount(1);
        method.GetParameters()[0].ParameterType.Should().Be(typeof(string));
    }

    #endregion

    [Fact]
    public void LiveThinkingIndicator_SpinnerOutput_ShouldContainEscHint()
    {
        // Arrange – capture raw console output from the spinner
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);

        try
        {
            var indicator = ConsoleUI.CreateLiveThinkingIndicator();
            indicator.Start();

            // Allow a few spinner frames to render
            Thread.Sleep(500);

            indicator.Dispose();

            var output = sw.ToString();

            // Assert
            output.Should().Contain("ESC to cancel");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void LiveThinkingIndicator_DisposeImmediately_ShouldLeaveWindowsTerminalProgressCleared()
    {
        var originalOut = Console.Out;
        var originalOutputResolver = ConsoleUI.IsOutputRedirectedResolver;
        var originalResolver = ConsoleUI.IsWindowsTerminalSessionResolver;
        using var sw = new StringWriter();
        Console.SetOut(sw);

        try
        {
            ConsoleUI.IsOutputRedirectedResolver = static () => false;
            ConsoleUI.IsWindowsTerminalSessionResolver = static () => true;

            var indicator = ConsoleUI.CreateLiveThinkingIndicator();
            indicator.Start();
            indicator.Dispose();

            Thread.Sleep(300);

            var output = sw.ToString();
            var indeterminate = ConsoleUI.BuildWindowsTerminalProgressSequence(TerminalProgressState.Indeterminate);
            var hidden = ConsoleUI.BuildWindowsTerminalProgressSequence(TerminalProgressState.Hidden);

            output.Should().Contain(indeterminate);
            output.Should().Contain(hidden);
            output.LastIndexOf(hidden, StringComparison.Ordinal)
                .Should().BeGreaterThan(output.LastIndexOf(indeterminate, StringComparison.Ordinal));
        }
        finally
        {
            ConsoleUI.IsOutputRedirectedResolver = originalOutputResolver;
            ConsoleUI.IsWindowsTerminalSessionResolver = originalResolver;
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void LiveThinkingIndicator_FormatElapsed_ShouldFormatSeconds()
    {
        LiveThinkingIndicator.FormatElapsed(5).Should().Be("5s");
        LiveThinkingIndicator.FormatElapsed(45).Should().Be("45s");
    }

    [Fact]
    public void LiveThinkingIndicator_FormatElapsed_ShouldFormatMinutes()
    {
        LiveThinkingIndicator.FormatElapsed(60).Should().Be("1m");
        LiveThinkingIndicator.FormatElapsed(90).Should().Be("1m 30s");
        LiveThinkingIndicator.FormatElapsed(125).Should().Be("2m 5s");
    }

    [Fact]
    public void LiveThinkingIndicator_PhaseElapsed_ShouldResetOnUpdateStatus()
    {
        using var indicator = new LiveThinkingIndicator();
        indicator.Start();
        Thread.Sleep(100);

        indicator.UpdateStatus("New phase");
        var phaseAfterUpdate = indicator.PhaseElapsed;

        phaseAfterUpdate.TotalMilliseconds.Should().BeLessThan(200);
    }

    [Fact]
    public void LiveThinkingIndicator_Elapsed_ShouldTrackTotalTime()
    {
        using var indicator = new LiveThinkingIndicator();
        indicator.Start();
        Thread.Sleep(300);

        indicator.Elapsed.TotalMilliseconds.Should().BeGreaterThan(200);
    }

    #endregion

    #region Prompt History Tests

    /// <summary>
    /// Helper to access the private _promptHistory field via reflection.
    /// </summary>
    private static List<string> GetPromptHistoryField()
    {
        var field = typeof(ConsoleUI).GetField("_promptHistory", BindingFlags.Static | BindingFlags.NonPublic);
        field.Should().NotBeNull("ConsoleUI should have a _promptHistory field");
        return (List<string>)field!.GetValue(null)!;
    }

    /// <summary>
    /// Helper to clear prompt history between tests.
    /// </summary>
    private static void ClearPromptHistory()
    {
        GetPromptHistoryField().Clear();
    }

    [Fact]
    public void AddPromptHistory_ShouldAddEntry()
    {
        // Arrange
        ClearPromptHistory();

        // Act
        ConsoleUI.AddPromptHistory("test command");

        // Assert
        var history = GetPromptHistoryField();
        history.Should().ContainSingle().Which.Should().Be("test command");
    }

    [Fact]
    public void AddPromptHistory_ShouldNotAddDuplicate()
    {
        // Arrange
        ClearPromptHistory();

        // Act
        ConsoleUI.AddPromptHistory("duplicate");
        ConsoleUI.AddPromptHistory("duplicate");

        // Assert
        var history = GetPromptHistoryField();
        history.Should().ContainSingle();
    }

    [Fact]
    public void AddPromptHistory_ShouldNotAddWhitespace()
    {
        // Arrange
        ClearPromptHistory();

        // Act
        ConsoleUI.AddPromptHistory("");
        ConsoleUI.AddPromptHistory("   ");
        ConsoleUI.AddPromptHistory(null!);

        // Assert
        var history = GetPromptHistoryField();
        history.Should().BeEmpty();
    }

    [Fact]
    public void AddPromptHistory_ShouldCapAt100Entries()
    {
        // Arrange
        ClearPromptHistory();

        // Act
        for (int i = 0; i < 101; i++)
            ConsoleUI.AddPromptHistory($"entry-{i}");

        // Assert
        var history = GetPromptHistoryField();
        history.Should().HaveCount(100);
        history[0].Should().Be("entry-1", "oldest entry (entry-0) should have been removed");
        history[^1].Should().Be("entry-100");
    }

    #endregion

    #region Reasoning Text Tests

    [Fact]
    public void WriteReasoningText_ShouldOutputText()
    {
        // Arrange
        var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            // Act
            ConsoleUI.WriteReasoningText("thinking...");
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = sw.ToString();

        // Assert — text content always written; ANSI codes only on non-redirected terminals
        output.Should().Contain("thinking...");
    }

    [Fact]
    public void WriteReasoningText_ShouldNotOutput_WhenTextIsEmpty()
    {
        // Arrange
        var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            // Act
            ConsoleUI.WriteReasoningText("");
            ConsoleUI.WriteReasoningText(null!);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // Assert — nothing should be written
        sw.ToString().Should().BeEmpty();
    }

    [Fact]
    public void StartReasoningBlock_ShouldNotThrow()
    {
        // Act & Assert — should not throw regardless of terminal capability
        var act = () => ConsoleUI.StartReasoningBlock();
        act.Should().NotThrow();
    }

    [Fact]
    public void EndReasoningBlock_ShouldNotThrow()
    {
        // Act & Assert — should not throw
        var act = () => ConsoleUI.EndReasoningBlock();
        act.Should().NotThrow();
    }

    [Fact]
    public void EndReasoningBlock_ShouldAddSeparatorAfterReasoningText()
    {
        // Arrange
        var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);

        try
        {
            // Act
            ConsoleUI.WriteReasoningText("thinking...");
            ConsoleUI.EndReasoningBlock();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // Assert
        sw.ToString().Should().Contain("thinking...");
        sw.ToString().Should().EndWith(Environment.NewLine);
    }

    #endregion

    #region Context Value Markup Tests

    [Theory]
    [InlineData("25,000/100,000 (25%)", "green")]
    [InlineData("80,000/100,000 (80%)", "yellow")]
    [InlineData("95,000/100,000 (95%)", "red")]
    public void FormatContextValueMarkup_ShouldApplyCorrectColor(string value, string expectedColor)
    {
        var markup = ConsoleUI.FormatContextValueMarkup(value);

        markup.Should().Contain(expectedColor);
        markup.Should().Contain(Markup.Escape(value));
    }

    [Fact]
    public void FormatContextValueMarkup_WhenNoPercentage_ShouldFallBackToCyan()
    {
        var markup = ConsoleUI.FormatContextValueMarkup("unknown");

        markup.Should().Contain("cyan");
    }

    #endregion

    #region Status Panel Section Separator Tests

    [Fact]
    public void ShowStatusPanel_WithSectionSeparators_ShouldRenderFieldValues()
    {
        AnsiConsole.Record();
        ConsoleUI.ShowStatusPanel(
            "localhost", "Local", true, "gpt-4.1", ExecutionMode.Safe,
            usageFields: new (string, string)[]
            {
                (ConsoleUI.StatusSectionSeparator, "Provider"),
                ("Provider", "GitHub Copilot"),
                (ConsoleUI.StatusSectionSeparator, "Usage"),
                ("Prompt tokens", "1,234"),
                ("Context", "25,000/100,000 (25%)"),
            });
        var output = AnsiConsole.ExportText();

        output.Should().Contain("Provider");
        output.Should().Contain("GitHub Copilot");
        output.Should().Contain("25,000/100,000 (25%)");
    }

    [Fact]
    public void GetVisibleModelPickerRange_WhenSelectionNearEnd_ShouldScrollWindow()
    {
        var range = InvokeGetVisibleModelPickerRange(totalCount: 20, selectedIndex: 18, pageSize: 6);

        range.StartIndex.Should().Be(14);
        range.Count.Should().Be(6);
    }

    #endregion

    private static (int StartIndex, int Count) InvokeGetVisibleModelPickerRange(int totalCount, int selectedIndex, int pageSize)
    {
        return ModelPickerUI.GetVisibleModelPickerRange(totalCount, selectedIndex, pageSize);
    }

    #region ShowStatusPanel JEA Connection Mode Tests

    [Fact]
    public void ShowStatusPanel_WithJeaConnectionMode_ShouldRenderJeaLabelAndDefaultSession()
    {
        // Arrange & Act
        AnsiConsole.Record();
        ConsoleUI.ShowStatusPanel(
            "server1", "JEA (JEA-Admins)", true, "gpt-4.1", ExecutionMode.Safe, null,
            additionalTargets: null, defaultSessionTarget: "localhost");
        var output = AnsiConsole.ExportText();

        // Assert — JEA connection mode should appear
        output.Should().Contain("JEA (JEA-Admins)");
        output.Should().Contain("server1");
        output.Should().Contain("Default session");
        output.Should().Contain("localhost");
    }

    [Fact]
    public void ShowStatusPanel_CompactAiRow_ShouldCombineStatusAndModel()
    {
        // Arrange & Act
        AnsiConsole.Record();
        ConsoleUI.ShowStatusPanel(
            "localhost", "Local PowerShell", true, "gpt-4.1", ExecutionMode.Safe);
        var output = AnsiConsole.ExportText();

        // Assert — AI row should contain both status and model
        output.Should().Contain("Connected");
        output.Should().Contain("gpt-4.1");
        // Should NOT have separate "AI Model:" or "Copilot Status:" labels (old format)
        output.Should().NotContain("Copilot Status");
        output.Should().NotContain("AI Model");
    }

    #endregion

    #region ShowWelcomeMessage Compact Tests

    [Fact]
    public void ShowWelcomeMessage_ShouldBeCompactAndMentionHelp()
    {
        // Arrange & Act
        AnsiConsole.Record();
        ConsoleUI.ShowWelcomeMessage();
        var output = AnsiConsole.ExportText();

        // Assert — compact welcome should mention /help and /status but NOT show
        // the full command table (that now lives in ShowHelp)
        output.Should().Contain("/help");
        output.Should().Contain("/status");
        output.Should().Contain("/exit");
        // Should NOT contain the verbose command table entries from the old welcome
        output.Should().NotContain("/settings");
        output.Should().NotContain("/model");
        output.Should().NotContain("/server");
    }

    #endregion
}
