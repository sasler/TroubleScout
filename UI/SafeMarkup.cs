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
/// value while leaving literal markup tags intact.
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
    /// tags (e.g. <c>[red]</c>, <c>[/]</c>) in the format itself.
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

        var args = template.GetArguments();
        if (args.Length == 0)
        {
            // Format string has no interpolated values; return as-is.
            return template.Format;
        }

        var escaped = new object?[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i]?.ToString();
            escaped[i] = string.IsNullOrEmpty(raw) ? string.Empty : Markup.Escape(raw);
        }

        return string.Format(template.Format, escaped);
    }
}
