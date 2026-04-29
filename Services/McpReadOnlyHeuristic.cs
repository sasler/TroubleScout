namespace TroubleScout.Services;

/// <summary>
/// Heuristic that classifies MCP tool names as obviously read-only.
/// Used to short-circuit the approval prompt for clearly safe lookups
/// like list_*, get_*, search_*, etc.
/// </summary>
internal static class McpReadOnlyHeuristic
{
    private static readonly string[] ReadOnlyPrefixes =
    [
        "get_",
        "list_",
        "search_",
        "find_",
        "describe_",
        "read_",
        "query_",
        "inspect_",
        "show_",
        "fetch_",
        "lookup_"
    ];

    private static readonly string[] ReadOnlyExactNames =
    [
        "get",
        "list",
        "search",
        "find",
        "describe",
        "read",
        "query",
        "inspect",
        "show",
        "fetch",
        "lookup",
        "ping",
        "status",
        "version",
        "capabilities"
    ];

    /// <summary>
    /// Returns true when the supplied tool name looks like a clearly read-only
    /// operation (e.g. get_*, list_*, search_*).
    /// Tool names are normalized by lower-casing and stripping any
    /// "<server>/" or "<server>-" prefix so that "Redmine/Redmine-list_issues"
    /// and "list_issues" are both recognized.
    /// </summary>
    // Substrings that mark a tool name as sensitive even when the verb looks read-only.
    // e.g. "get_credential", "read_secret", "fetch_token" must still go through approval.
    private static readonly string[] SensitiveTokens =
    [
        "credential",
        "secret",
        "password",
        "passwd",
        "token",
        "api_key",
        "apikey",
        "access_key",
        "access_token",
        "refresh_token",
        "private_key",
        "privatekey",
        "ssh_key",
        "auth_key",
        "session_key",
        "bearer",
        "cookie",
        "certificate",
        "vault",
        "keystore"
    ];

    public static bool IsReadOnlyToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        var normalized = NormalizeToolName(toolName);

        foreach (var sensitive in SensitiveTokens)
        {
            if (normalized.Contains(sensitive, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        foreach (var name in ReadOnlyExactNames)
        {
            if (string.Equals(normalized, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var prefix in ReadOnlyPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeToolName(string toolName)
    {
        var trimmed = toolName.Trim();
        var slashIdx = trimmed.LastIndexOf('/');
        if (slashIdx >= 0 && slashIdx < trimmed.Length - 1)
        {
            trimmed = trimmed[(slashIdx + 1)..];
        }

        var dashIdx = trimmed.IndexOf('-');
        if (dashIdx > 0 && dashIdx < trimmed.Length - 1)
        {
            trimmed = trimmed[(dashIdx + 1)..];
        }

        return trimmed.ToLowerInvariant();
    }
}
