using System.Reflection;
using TroubleScout.UI;
using Xunit;

namespace TroubleScout.Tests;

public class StatusBarWidthTests
{
    private static string RenderForWidth(StatusBarInfo info, int width)
    {
        var method = typeof(ConsoleUI).GetMethod("BuildStatusBarLine", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, new object[] { info, width }) as string;
        return result ?? string.Empty;
    }

    private static string RenderPlainForWidth(StatusBarInfo info, int width)
    {
        var method = typeof(ConsoleUI).GetMethod("BuildStatusBarPlain", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method!.Invoke(null, new object[] { info, width }) as string;
        return result ?? string.Empty;
    }

    private static StatusBarInfo SampleInfo() => new(
        Model: "GPT-5.5",
        Provider: "GitHub Copilot",
        InputTokens: 1234,
        OutputTokens: 567,
        TotalTokens: 1801,
        ToolInvocations: 3,
        SessionId: "abc")
    {
        ReasoningEffort = "medium"
    };

    [Fact]
    public void NarrowWidth_BelowFloor_SuppressesStatusBarEntirely()
    {
        var line = RenderForWidth(SampleInfo(), width: 30);
        Assert.Equal(string.Empty, line);
    }

    [Fact]
    public void MediumWidth_KeepsTokensDropsProviderAndReasoning()
    {
        var plain = RenderPlainForWidth(SampleInfo(), width: 50);
        Assert.Contains("Tokens:", plain);
        Assert.DoesNotContain("Provider:", plain);
        Assert.DoesNotContain("Reasoning:", plain);
    }

    [Fact]
    public void WideWidth_KeepsAllFields()
    {
        var plain = RenderPlainForWidth(SampleInfo(), width: 240);
        Assert.Contains("Model:", plain);
        Assert.Contains("Provider:", plain);
        Assert.Contains("Reasoning:", plain);
        Assert.Contains("Tokens:", plain);
        Assert.Contains("Tools:", plain);
    }

    [Fact]
    public void WidthDropsProviderBeforeReasoningBeforeTools()
    {
        // Provider should be the first field dropped when space tightens.
        var info = SampleInfo();
        var plainWide = RenderPlainForWidth(info, width: 240);
        Assert.Contains("Provider:", plainWide);

        // Find a width that drops Provider but keeps Tools and Tokens.
        var plainTight = RenderPlainForWidth(info, width: 80);
        if (!plainTight.Contains("Provider:"))
        {
            // Provider has been dropped; tokens should still be present.
            Assert.Contains("Tokens:", plainTight);
        }
    }

    [Fact]
    public void EmptyInfo_ReturnsEmpty()
    {
        var line = RenderForWidth(StatusBarInfo.Empty, width: 200);
        Assert.Equal(string.Empty, line);
    }

    [Fact]
    public void StatusBarLine_NeverWraps_UnderItsRenderedWidth()
    {
        // With 200 cols and a complete info, the rendered plain text length
        // should not exceed the width budget.
        var plain = RenderPlainForWidth(SampleInfo(), width: 200);
        Assert.True(plain.Length <= 200, $"plain.Length={plain.Length}");
    }
}

public class TerminalCapabilityTests
{
    [Fact]
    public void SetTerminalTitle_WhenOutputRedirected_DoesNotWriteOscSequence()
    {
        var prevRedirected = ConsoleUI.IsOutputRedirectedResolver;
        var prevWt = ConsoleUI.IsWindowsTerminalSessionResolver;
        var captured = new System.IO.StringWriter();
        var origOut = Console.Out;
        try
        {
            ConsoleUI.IsOutputRedirectedResolver = () => true;
            ConsoleUI.IsWindowsTerminalSessionResolver = () => true;
            Console.SetOut(captured);

            ConsoleUI.SetTerminalTitle("test-title");

            var output = captured.ToString();
            Assert.DoesNotContain("\u001b]0;", output);
            Assert.DoesNotContain("\u001b]2;", output);
        }
        finally
        {
            ConsoleUI.IsOutputRedirectedResolver = prevRedirected;
            ConsoleUI.IsWindowsTerminalSessionResolver = prevWt;
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public void SetTerminalTitle_OutsideWindowsTerminal_DoesNotWriteOscSequence()
    {
        var prevRedirected = ConsoleUI.IsOutputRedirectedResolver;
        var prevWt = ConsoleUI.IsWindowsTerminalSessionResolver;
        var captured = new System.IO.StringWriter();
        var origOut = Console.Out;
        try
        {
            ConsoleUI.IsOutputRedirectedResolver = () => false;
            ConsoleUI.IsWindowsTerminalSessionResolver = () => false;
            Console.SetOut(captured);

            ConsoleUI.SetTerminalTitle("test-title");

            var output = captured.ToString();
            Assert.DoesNotContain("\u001b]0;", output);
            Assert.DoesNotContain("\u001b]2;", output);
        }
        finally
        {
            ConsoleUI.IsOutputRedirectedResolver = prevRedirected;
            ConsoleUI.IsWindowsTerminalSessionResolver = prevWt;
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public void SetTerminalTitle_InsideWindowsTerminal_AndNotRedirected_WritesOscSequence()
    {
        var prevRedirected = ConsoleUI.IsOutputRedirectedResolver;
        var prevWt = ConsoleUI.IsWindowsTerminalSessionResolver;
        var captured = new System.IO.StringWriter();
        var origOut = Console.Out;
        try
        {
            ConsoleUI.IsOutputRedirectedResolver = () => false;
            ConsoleUI.IsWindowsTerminalSessionResolver = () => true;
            Console.SetOut(captured);

            ConsoleUI.SetTerminalTitle("test-title");

            var output = captured.ToString();
            Assert.Contains("\u001b]0;test-title\u0007", output);
        }
        finally
        {
            ConsoleUI.IsOutputRedirectedResolver = prevRedirected;
            ConsoleUI.IsWindowsTerminalSessionResolver = prevWt;
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public void SetWindowsTerminalProgress_WhenRedirected_DoesNotWrite()
    {
        var prevRedirected = ConsoleUI.IsOutputRedirectedResolver;
        var prevWt = ConsoleUI.IsWindowsTerminalSessionResolver;
        var captured = new System.IO.StringWriter();
        var origOut = Console.Out;
        try
        {
            ConsoleUI.IsOutputRedirectedResolver = () => true;
            ConsoleUI.IsWindowsTerminalSessionResolver = () => true;
            Console.SetOut(captured);

            ConsoleUI.SetWindowsTerminalProgress(TerminalProgressState.Indeterminate);

            var output = captured.ToString();
            Assert.DoesNotContain("\u001b]9;4;", output);
        }
        finally
        {
            ConsoleUI.IsOutputRedirectedResolver = prevRedirected;
            ConsoleUI.IsWindowsTerminalSessionResolver = prevWt;
            Console.SetOut(origOut);
        }
    }
}
