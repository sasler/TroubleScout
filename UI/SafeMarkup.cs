using System.Globalization;
using Spectre.Console;

namespace TroubleScout.UI;

/// <summary>
/// Centralized helpers for emitting Spectre.Console markup that contains
/// user- or model-controlled text without breaking the markup parser or
/// allowing injection of unintended formatting.
///
/// Why this exists:
/// AGENTS.md requires every user-controlled value to be passed through
/// <c>Markup.Escape(...)</c> before it lands inside `[tag]...[/]` markup.
/// Doing that manually at every call site is easy to forget — and the
/// Spectre parser will throw if a stray `[` or `]` appears unescaped.
/// <see cref="SafeMarkup"/> exposes a single, intent-revealing entry point
/// so reviewers can grep for "SafeMarkup" and trust the call site.
///
/// Prefer <see cref="Interpolate(FormattableString)"/> for new code:
/// it accepts a C# interpolated string and auto-escapes every interpolated
/// value while leaving literal markup tags intact and preserving standard
/// composite-format semantics (format specifiers and alignment).
/// </summary>
internal static class SafeMarkup
{
    /// <summary>
    /// Returns the input safely escaped for use inside Spectre markup.
    /// Null and empty inputs return an empty string.
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return Markup.Escape(value);
    }

    /// <summary>
    /// Builds a Spectre markup string from a C# interpolated string,
    /// escaping every interpolated value while preserving literal markup
    /// tags (e.g. <c>[red]</c>, <c>[/]</c>) in the format itself, and
    /// honoring standard composite formatting semantics (format specifiers
    /// like <c>{count:D4}</c> and alignment like <c>{value,10}</c>).
    ///
    /// <example>
    /// <code>
    /// var userName = "[admin]";        // intentionally hostile
    /// var line = SafeMarkup.Interpolate($"[yellow]Hello {userName}[/]");
    /// // line == "[yellow]Hello [[admin]][/]"
    /// </code>
    /// </example>
    ///
    /// Null arguments render as the empty string, matching the behavior of
    /// <see cref="Markup.Escape(string)"/> for empty inputs.
    /// </summary>
    public static string Interpolate(FormattableString template)
    {
        if (template is null) return string.Empty;

        if (template.ArgumentCount == 0)
        {
            // Format string has no interpolated values; return as-is.
            return template.Format;
        }

        // Delegate to string.Format with a custom format provider that applies
        // the original format specifier first and then Markup.Escape. Alignment
        // (e.g. {value,10}) is handled by the composite formatter itself: it
        // pads the post-formatter return value, which is what we want — the
        // padding does not need escaping.
        return string.Format(EscapingFormatProvider.Instance, template.Format, template.GetArguments());
    }

    private sealed class EscapingFormatProvider : IFormatProvider, ICustomFormatter
    {
        public static readonly EscapingFormatProvider Instance = new();

        public object? GetFormat(Type? formatType)
            => formatType == typeof(ICustomFormatter) ? this : null;

        public string Format(string? format, object? arg, IFormatProvider? formatProvider)
        {
            string? rendered;
            if (arg is IFormattable formattable)
            {
                rendered = formattable.ToString(format, CultureInfo.CurrentCulture);
            }
            else
            {
                rendered = arg?.ToString();
            }

            return string.IsNullOrEmpty(rendered) ? string.Empty : Markup.Escape(rendered);
        }
    }
}
