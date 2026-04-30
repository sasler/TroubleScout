using System.Text.RegularExpressions;

namespace TroubleScout.Services;

/// <summary>
/// Centralized policy + helper for redacting secret-shaped values out of
/// strings before they land in any user-visible artifact that gets persisted
/// (HTML report, opt-in transcript log, copy/paste of session output, …).
///
/// This is intentionally conservative: it errs on the side of redacting
/// things that *look* like secrets even when they are not. False positives
/// produce a redacted blob in the output; false negatives leak credentials.
///
/// The policy is keyword-anchored (key/secret/password/token + assignment)
/// plus a handful of well-known token shapes that don't need a keyword
/// (GitHub PATs, AWS access keys, JWTs, URLs with userinfo, etc.).
///
/// AGENTS.md tracks the redaction policy as a prerequisite for any expanded
/// session persistence (opt-in transcript, /replay).
/// </summary>
internal static class SecretRedactor
{
    public const string Mask = "***REDACTED***";

    // GitHub personal access tokens — fixed prefix + length.
    // Examples: ghp_xxxx (36 chars), gho_xxxx, ghu_xxxx, ghs_xxxx, ghr_xxxx, github_pat_xxxx.
    private static readonly Regex GitHubTokenPattern = new(
        @"\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}\b|\bgithub_pat_[A-Za-z0-9_]{20,}\b",
        RegexOptions.Compiled);

    // AWS access key IDs — fixed AKIA/ASIA prefix + 16 uppercase alphanumerics.
    private static readonly Regex AwsAccessKeyPattern = new(
        @"\b(AKIA|ASIA)[0-9A-Z]{16}\b",
        RegexOptions.Compiled);

    // JSON Web Tokens — three base64url segments separated by dots.
    private static readonly Regex JwtPattern = new(
        @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
        RegexOptions.Compiled);

    // Bearer tokens in HTTP-style headers. Token must be at least 8 chars to
    // avoid redacting the placeholder word "Bearer".
    private static readonly Regex BearerPattern = new(
        @"\bBearer\s+[A-Za-z0-9._\-+/=]{8,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Basic auth in URL: scheme://user:password@host
    private static readonly Regex UrlUserInfoPattern = new(
        @"(?<scheme>[a-zA-Z][a-zA-Z0-9+\-.]*://)(?<user>[^:/\s@]+):(?<pass>[^/\s@]+)@",
        RegexOptions.Compiled);

    // Connection-string style key=value pairs that name a sensitive field.
    // Captures things like Password=secret;, Pwd=secret;, AccountKey=...;.
    private static readonly Regex ConnectionStringSecretPattern = new(
        @"(?<key>(?:password|pwd|accountkey|sharedaccesskey|sharedsecret|secret))\s*=\s*(?<value>[^;""\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Generic key=value / key: value pairs whose KEY contains a secret-suggesting
    // substring. Anchored on word boundary so "background_color" won't match.
    // The keyword set deliberately mirrors the MCP read-only sensitive token list.
    private static readonly Regex KeyValueSecretPattern = new(
        @"\b(?<key>[A-Za-z0-9_\-]*?(?:api[_\-]?key|apikey|access[_\-]?key|access[_\-]?token|"
        + @"refresh[_\-]?token|private[_\-]?key|privatekey|ssh[_\-]?key|auth[_\-]?key|"
        + @"session[_\-]?key|secret[_\-]?key|client[_\-]?secret|secret|token|"
        + @"credential|passphrase)[A-Za-z0-9_\-]*?)\s*[:=]\s*(?<quote>[""']?)(?<value>[^""'\r\n,;]{4,})\k<quote>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns a copy of <paramref name="input"/> with secret-shaped substrings
    /// replaced by <see cref="Mask"/>. Returns the input unchanged when null,
    /// empty, or when no patterns match.
    /// </summary>
    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;

        var result = input;

        // Order matters: do the high-specificity patterns first so we don't
        // mask part of a longer match with a shorter one.
        result = GitHubTokenPattern.Replace(result, Mask);
        result = AwsAccessKeyPattern.Replace(result, Mask);
        result = JwtPattern.Replace(result, Mask);
        result = BearerPattern.Replace(result, "Bearer " + Mask);
        result = UrlUserInfoPattern.Replace(result, m =>
            $"{m.Groups["scheme"].Value}{m.Groups["user"].Value}:{Mask}@");
        result = ConnectionStringSecretPattern.Replace(result, m =>
            $"{m.Groups["key"].Value}={Mask}");
        result = KeyValueSecretPattern.Replace(result, m =>
        {
            var quote = m.Groups["quote"].Value;
            return $"{m.Groups["key"].Value}={quote}{Mask}{quote}";
        });

        return result;
    }

    /// <summary>
    /// Returns true when <see cref="Redact"/> would change the input — useful
    /// in tests and for skipping a logging/persistence step that wants to know
    /// it was about to write something sensitive.
    /// </summary>
    public static bool ContainsSecret(string? input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        return GitHubTokenPattern.IsMatch(input)
            || AwsAccessKeyPattern.IsMatch(input)
            || JwtPattern.IsMatch(input)
            || BearerPattern.IsMatch(input)
            || UrlUserInfoPattern.IsMatch(input)
            || ConnectionStringSecretPattern.IsMatch(input)
            || KeyValueSecretPattern.IsMatch(input);
    }
}
