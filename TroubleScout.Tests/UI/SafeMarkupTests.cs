using FluentAssertions;
using Spectre.Console;
using TroubleScout.UI;
using Xunit;

namespace TroubleScout.Tests.UI;

public class SafeMarkupTests
{
    [Fact]
    public void Escape_Null_ReturnsEmpty()
    {
        SafeMarkup.Escape(null).Should().Be(string.Empty);
    }

    [Fact]
    public void Escape_Empty_ReturnsEmpty()
    {
        SafeMarkup.Escape(string.Empty).Should().Be(string.Empty);
    }

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("[red]boom[/]", "[[red]]boom[[/]]")]
    [InlineData("contains [bracket]", "contains [[bracket]]")]
    public void Escape_DoublesSquareBrackets(string input, string expected)
    {
        SafeMarkup.Escape(input).Should().Be(expected);
    }

    [Fact]
    public void Interpolate_NoInterpolation_ReturnsLiteralFormat()
    {
        FormattableString template = $"plain string with no values";
        SafeMarkup.Interpolate(template).Should().Be("plain string with no values");
    }

    [Fact]
    public void Interpolate_PreservesMarkupTagsAndEscapesValue()
    {
        var hostile = "[admin]";
        FormattableString template = $"[yellow]Hello {hostile}[/]";

        var result = SafeMarkup.Interpolate(template);

        // Literal "[yellow]" and "[/]" tags survive; the interpolated value's
        // brackets get doubled by Markup.Escape so they render literally.
        result.Should().Be("[yellow]Hello [[admin]][/]");
    }

    [Fact]
    public void Interpolate_NullArgument_RendersAsEmpty()
    {
        string? value = null;
        FormattableString template = $"[red]{value}[/]";

        SafeMarkup.Interpolate(template).Should().Be("[red][/]");
    }

    [Fact]
    public void Interpolate_MultipleValues_EachEscapedIndependently()
    {
        var first = "user [1]";
        var second = "tool [foo/bar]";
        FormattableString template = $"[grey]{first}[/] used [cyan]{second}[/]";

        SafeMarkup.Interpolate(template).Should().Be(
            "[grey]user [[1]][/] used [cyan]tool [[foo/bar]][/]");
    }

    [Fact]
    public void Interpolate_NonStringArgument_UsesToString()
    {
        var count = 42;
        FormattableString template = $"[white]{count}[/]";

        SafeMarkup.Interpolate(template).Should().Be("[white]42[/]");
    }

    [Fact]
    public void Interpolate_PreservesFormatSpecifiersAndAlignment()
    {
        var count = 42;
        var value = 7;
        FormattableString template = $"[white]{count:D4}[/] [grey]{value,10}[/]";

        SafeMarkup.Interpolate(template).Should().Be("[white]0042[/] [grey]         7[/]");
    }

    [Fact]
    public void Interpolate_AppliesFormatThenEscapesResultingMarkupChars()
    {
        // A custom IFormattable returns markup-shaped text after applying its
        // format specifier. Interpolate must escape the post-format string,
        // not the raw object.
        var hostile = new FormattableHostile();
        FormattableString template = $"[bold]{hostile:wrap}[/]";

        SafeMarkup.Interpolate(template).Should().Be("[bold][[hostile]][/]");
    }

    private sealed class FormattableHostile : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider)
            => format == "wrap" ? "[hostile]" : "hostile";
    }

    [Fact]
    public void Interpolate_ResultParsesAsValidSpectreMarkup()
    {
        // Round-trip check: feed the SafeMarkup output back into the Spectre
        // parser and confirm it accepts it (i.e. we didn't produce a malformed
        // tag). The Markup constructor throws on parse errors.
        var hostile = "value with [unclosed bracket and ]closed bracket";
        FormattableString template = $"[bold]{hostile}[/]";

        var result = SafeMarkup.Interpolate(template);

        var act = () => _ = new Markup(result);

        act.Should().NotThrow("the escape should produce valid Spectre markup even " +
            "when the interpolated value contains unbalanced brackets.");
    }
}
