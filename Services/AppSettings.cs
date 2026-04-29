using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TroubleScout.Services;

public sealed class AppSettings
{
    public string? LastModel { get; set; }
    public string? ReasoningEffort { get; set; }
    public bool UseByokOpenAi { get; set; }
    public string? ByokOpenAiBaseUrl { get; set; }
    public string? ByokOpenAiApiKey { get; set; }
    public string? ByokOpenAiApiKeyEncrypted { get; set; }
    public List<string>? SafeCommands { get; set; }
    public Dictionary<string, string>? SystemPromptOverrides { get; set; }
    public string? SystemPromptAppend { get; set; }
    public string? MonitoringMcpServer { get; set; }
    public string? TicketingMcpServer { get; set; }
    public List<string>? PersistedApprovedMcpServers { get; set; }
}

public static class AppSettingsStore
{
    private static string? _settingsPath;
    internal static readonly string[] DefaultSafeCommands =
    [
        "Get-*",
        "Select-*",
        "Sort-*",
        "Group-*",
        "Where-*",
        "ForEach-*",
        "Measure-*",
        "Test-*",
        "ConvertTo-*",
        "ConvertFrom-*",
        "Compare-*",
        "Find-*",
        "Search-*",
        "Resolve-*",
        "Out-String",
        "Out-Null",
        "Format-Custom",
        "Format-Hex",
        "Format-List",
        "Format-Table",
        "Format-Wide"
    ];

    private const string DefaultInvestigationApproach = """
        ## Investigation Approach
        - When investigating an issue, exhaust ALL available diagnostic tools and data sources before asking the user for more information
        - Work proactively within a single investigation pass until you have a clear diagnosis, recommendation, or exhausted the relevant diagnostics
        - Only ask clarifying questions when the initial problem description is genuinely ambiguous or when you need credentials/access that you do not have
        - Present complete findings, analysis, and recommendations in one response, then hand control back to TroubleScout instead of continuing indefinitely on your own
        - When you are ready for the user to choose what happens next, end with a short `## Ready for next action` section
        - If one diagnostic approach yields no results, try alternative approaches before concluding
        - Gather data from ALL relevant sources (event logs, services, processes, performance counters, disk, network) in a single investigation pass
        """;

    private const string DefaultResponseFormat = """
        ## Response Format
        - ALWAYS start your response by confirming which server you're analyzing (e.g., "Analyzing <server>...")
        - Always format your response as Markdown
        - Use short Markdown sections and bullet lists to keep output readable
        - Separate distinct steps/findings with blank lines
        - For tabular data, use compact Markdown tables (pipe syntax) and avoid fixed-width ASCII-art table alignment
        - If a table would be too wide, reduce columns or use a concise bullet list instead of forcing alignment
        - Be concise but thorough
        - Use bullet points for lists
        - Highlight critical findings with **bold**
        - Use fenced code blocks for commands or command output when relevant
        - For remediation commands (non-Get commands), explain what they do and why they're needed
        - Always explain your reasoning
        - When presenting diagnostic data, include the source server name in your explanation
        """;

    private const string DefaultTroubleshootingApproach = """
        ## Troubleshooting Approach
        1. **Understand the Problem**: Ask clarifying questions if the issue description is vague
        2. **Gather Data**: Use the relevant diagnostic tools to collect information from the active target server or chosen JEA session
        3. **Verify Source**: Confirm the data comes from the intended server or session before drawing conclusions
        4. **Analyze**: Look for errors, warnings, resource exhaustion, or configuration issues
        5. **Diagnose**: Form hypotheses about the root cause based on evidence
        6. **Recommend**: Provide clear, actionable next steps
        """;

    private const string DefaultSafetyGuidance = """
        ## Safety
        - Only read-only Get-* commands execute automatically
        - Read-only diagnostic tools execute automatically in ALL modes (Safe and YOLO) — never wait for approval before using them
        - In Safe mode, only mutating PowerShell commands (run_powershell with Set-*, Stop-*, Start-*, Remove-*, Restart-* etc.) require user confirmation
        - In YOLO mode, remediation commands can execute without confirmation
        - For ANY mutating task, you MUST call the run_powershell tool with the exact command
        - For mutating PowerShell cmdlets that support confirmation prompts, include `-Confirm:$false` when appropriate after the user has approved the action
        - Never claim a command was executed unless run_powershell returned execution output
        - Never say you will keep monitoring, continue in the background, or confirm later after control returns to the user prompt. If a command is still running or needs follow-up, tell the user what happened and what they should run or ask next.
        - If no tool was executed, clearly state that no command has been run yet
        - Before claiming you do not have access to a tool, web capability, MCP server, or skill, first attempt to use the relevant available capability
        - If a capability is unavailable after an attempt, clearly state what you tried and what was unavailable
        - Never suggest commands that could cause data loss without clear warnings
        - Always consider the impact of recommended actions
        """;

    internal static IReadOnlyDictionary<string, string> DefaultSystemPromptOverrides { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["investigation_approach"] = DefaultInvestigationApproach,
            ["response_format"] = DefaultResponseFormat,
            ["troubleshooting_approach"] = DefaultTroubleshootingApproach,
            ["safety"] = DefaultSafetyGuidance
        };
    
    public static string SettingsPath
    {
        get
        {
            if (_settingsPath != null)
                return _settingsPath;

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
            {
                // Fallback to LocalApplicationData if ApplicationData is unavailable
                appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(appData))
                {
                    // Final fallback to current directory if no application data folders are available
                    appData = Environment.CurrentDirectory;
                }
            }

            _settingsPath = Path.Combine(appData, "TroubleScout", "settings.json");
            return _settingsPath;
        }
        internal set => _settingsPath = value;
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                var defaultSettings = CreateNormalizedSettings(new AppSettings(), out _);
                Save(defaultSettings);
                return defaultSettings;
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

            if (string.IsNullOrWhiteSpace(settings.ByokOpenAiApiKey)
                && !string.IsNullOrWhiteSpace(settings.ByokOpenAiApiKeyEncrypted))
            {
                settings.ByokOpenAiApiKey = TryDecrypt(settings.ByokOpenAiApiKeyEncrypted);
            }

            settings = CreateNormalizedSettings(settings, out var changed);
            if (changed)
            {
                Save(settings);
            }

            return settings;
        }
        catch
        {
            return CreateNormalizedSettings(new AppSettings(), out _);
        }
    }

    public static void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var persisted = new AppSettings
        {
            LastModel = settings.LastModel,
            ReasoningEffort = settings.ReasoningEffort,
            UseByokOpenAi = settings.UseByokOpenAi,
            ByokOpenAiBaseUrl = settings.ByokOpenAiBaseUrl,
            ByokOpenAiApiKey = null,
            ByokOpenAiApiKeyEncrypted = TryEncrypt(settings.ByokOpenAiApiKey),
            SafeCommands = settings.SafeCommands?.Where(command => !string.IsNullOrWhiteSpace(command)).Select(command => command.Trim()).ToList()
                ?? DefaultSafeCommands.ToList(),
            SystemPromptOverrides = settings.SystemPromptOverrides,
            SystemPromptAppend = settings.SystemPromptAppend,
            MonitoringMcpServer = NormalizeOptionalValue(settings.MonitoringMcpServer),
            TicketingMcpServer = NormalizeOptionalValue(settings.TicketingMcpServer),
            PersistedApprovedMcpServers = NormalizeMcpServerList(settings.PersistedApprovedMcpServers)
        };

        var json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(SettingsPath, json);
    }

    private static AppSettings CreateNormalizedSettings(AppSettings settings, out bool changed)
    {
        changed = false;

        var normalizedReasoningEffort = string.IsNullOrWhiteSpace(settings.ReasoningEffort)
            ? null
            : settings.ReasoningEffort.Trim().ToLowerInvariant();

        if (normalizedReasoningEffort is "auto" or "default")
        {
            normalizedReasoningEffort = null;
        }

        if (!string.Equals(settings.ReasoningEffort, normalizedReasoningEffort, StringComparison.Ordinal))
        {
            settings.ReasoningEffort = normalizedReasoningEffort;
            changed = true;
        }

        var normalizedMonitoringMcpServer = NormalizeOptionalValue(settings.MonitoringMcpServer);
        if (!string.Equals(settings.MonitoringMcpServer, normalizedMonitoringMcpServer, StringComparison.Ordinal))
        {
            settings.MonitoringMcpServer = normalizedMonitoringMcpServer;
            changed = true;
        }

        var normalizedTicketingMcpServer = NormalizeOptionalValue(settings.TicketingMcpServer);
        if (!string.Equals(settings.TicketingMcpServer, normalizedTicketingMcpServer, StringComparison.Ordinal))
        {
            settings.TicketingMcpServer = normalizedTicketingMcpServer;
            changed = true;
        }

        var normalizedPersistedApprovals = NormalizeMcpServerList(settings.PersistedApprovedMcpServers);
        if (!McpServerListEquals(settings.PersistedApprovedMcpServers, normalizedPersistedApprovals))
        {
            settings.PersistedApprovedMcpServers = normalizedPersistedApprovals;
            changed = true;
        }

        if (settings.SafeCommands == null)
        {
            settings.SafeCommands = DefaultSafeCommands.ToList();
            changed = true;
        }

        if (settings.SystemPromptOverrides == null)
        {
            settings.SystemPromptOverrides = DefaultSystemPromptOverrides.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            changed = true;
        }
        else
        {
            var normalizedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in settings.SystemPromptOverrides)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                {
                    continue;
                }

                normalizedOverrides[entry.Key.Trim()] = entry.Value;
            }

            foreach (var (key, value) in DefaultSystemPromptOverrides)
            {
                if (!normalizedOverrides.ContainsKey(key))
                {
                    normalizedOverrides[key] = value;
                    changed = true;
                }
            }

            if (normalizedOverrides.Count != settings.SystemPromptOverrides.Count)
            {
                changed = true;
            }

            settings.SystemPromptOverrides = normalizedOverrides;
        }

        return settings;
    }

    internal static bool IsDefaultSystemPromptOverride(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return DefaultSystemPromptOverrides.TryGetValue(key.Trim(), out var defaultValue)
            && string.Equals(defaultValue.Trim(), value.Trim(), StringComparison.Ordinal);
    }

    private static string? TryEncrypt(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            return plainText;
        }

        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return plainText;
        }
    }

    private static string? TryDecrypt(string? cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            return cipherText;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(cipherText);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return cipherText;
        }
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    internal static List<string>? NormalizeMcpServerList(IEnumerable<string>? values)
    {
        if (values == null)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var trimmed = raw.Trim();
            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static bool McpServerListEquals(List<string>? a, List<string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return a?.Count == 0 && b == null || b?.Count == 0 && a == null;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    public static bool AddPersistedApprovedMcpServer(AppSettings settings, string serverName)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(serverName))
        {
            return false;
        }

        var trimmed = serverName.Trim();
        settings.PersistedApprovedMcpServers ??= new List<string>();

        if (settings.PersistedApprovedMcpServers.Any(name => string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        settings.PersistedApprovedMcpServers.Add(trimmed);
        Save(settings);
        return true;
    }

    public static bool RemovePersistedApprovedMcpServer(AppSettings settings, string serverName)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(serverName) || settings.PersistedApprovedMcpServers == null)
        {
            return false;
        }

        var trimmed = serverName.Trim();
        var initial = settings.PersistedApprovedMcpServers.Count;
        settings.PersistedApprovedMcpServers.RemoveAll(name => string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase));

        if (settings.PersistedApprovedMcpServers.Count == 0)
        {
            settings.PersistedApprovedMcpServers = null;
        }

        if (settings.PersistedApprovedMcpServers?.Count != initial)
        {
            Save(settings);
            return true;
        }

        return false;
    }

    public static int ClearPersistedApprovedMcpServers(AppSettings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var count = settings.PersistedApprovedMcpServers?.Count ?? 0;
        if (count == 0)
        {
            return 0;
        }

        settings.PersistedApprovedMcpServers = null;
        Save(settings);
        return count;
    }
}
