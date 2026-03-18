using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GitHub.Copilot.SDK;
using Spectre.Console;
using TroubleScout.Services;
using TroubleScout.Tools;
using TroubleScout.UI;

namespace TroubleScout;

/// <summary>
/// Manages the Copilot-powered troubleshooting session
/// </summary>
public class TroubleshootingSession : IAsyncDisposable
{
    [Flags]
    internal enum ModelSource
    {
        None = 0,
        GitHub = 1,
        Byok = 2
    }

    internal record ModelSelectionEntry(string ModelId, string DisplayName, ModelSource Source)
    {
        public string ProviderLabel { get; init; } = string.Empty;
        public string RateLabel { get; init; } = "n/a";
        public string DetailSummary { get; init; } = string.Empty;
        public bool IsCurrent { get; init; }
    }
    internal sealed record ByokPriceInfo(decimal? InputPricePerMillionTokens, decimal? OutputPricePerMillionTokens, string? DisplayText);
    private sealed record ByokModelDiscoveryResult(List<ModelInfo> Models, Dictionary<string, ByokPriceInfo> PricingByModelId);
    internal sealed record ShellPermissionAssessment(string Command, CommandValidation Validation, string PromptReason, string ImpactText);

    private const string CopilotCliRepoUrl = "https://github.com/github/copilot-cli";
    private const string CopilotCliInstallUrl = "https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli";
    private const int MinSupportedNodeMajorVersion = 24;
    private const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1";
    private const string OpenAiApiKeyEnvironmentVariable = "OPENAI_API_KEY";

    internal static Func<string> CopilotCliPathResolver { get; set; } = GetCopilotCliPath;
    internal static Func<string, bool> FileExistsResolver { get; set; } = File.Exists;
    internal static Func<string, string, Task<(int ExitCode, string StdOut, string StdErr)>> ProcessRunnerResolver { get; set; } = RunProcessAsync;

    private string _targetServer;
    private PowerShellExecutor _executor;
    private DiagnosticTools _diagnosticTools;
    private readonly Dictionary<string, PowerShellExecutor> _additionalExecutors = new(StringComparer.OrdinalIgnoreCase);
    private CopilotClient? _copilotClient;
    private CopilotSession? _copilotSession;
    private bool _isInitialized;
    private string? _selectedModel;
    private string? _copilotVersion;
    private List<ModelInfo> _availableModels = new();
    private readonly Dictionary<string, ModelSource> _modelSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ByokPriceInfo> _byokPricing = new(StringComparer.OrdinalIgnoreCase);
    private readonly SessionUsageTracker _sessionUsageTracker = new();
    private readonly string? _mcpConfigPath;
    private readonly List<string> _skillDirectories;
    private readonly List<string> _disabledSkills;
    private readonly List<string> _configuredMcpServers = new();
    private readonly List<string> _configuredSkills = new();
    private readonly HashSet<string> _runtimeMcpServers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _runtimeSkills = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _configurationWarnings = new();
    private readonly IReadOnlyList<string> _additionalInitialServers;
    private readonly (string ServerName, string ConfigurationName)? _initialJeaSession;
    private bool _startupJeaFocusActive;
    private readonly bool _debugMode;
    private ExecutionMode _executionMode;
    private bool _useByokOpenAi;
    private bool _byokExplicitlyRequested;
    private bool _modelExplicitlyRequested;
    private string _byokOpenAiBaseUrl;
    private string? _byokOpenAiApiKey;
    private readonly List<ReportPromptEntry> _reportPrompts = [];
    private readonly object _reportLock = new();
    private int _lastPromptIndex = -1;
    private string _sessionId = "n/a";
    private int _sessionCounter;
    private int _toolInvocationCount;
    private bool _isGitHubCopilotAuthenticated;
    private IReadOnlyList<string>? _configuredSafeCommands;
    private IReadOnlyDictionary<string, string>? _configuredSystemPromptOverrides;
    private string? _configuredSystemPromptAppend;

    private CopilotUsageSnapshot? _lastUsage;

    private SystemMessageConfig _systemMessageConfig;

    private static readonly Dictionary<string, string> ToolDescriptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["run_powershell"]           = "Running PowerShell",
            ["get_system_info"]          = "Reading System Info",
            ["get_event_logs"]           = "Scanning Event Logs",
            ["get_services"]             = "Checking Services",
            ["get_processes"]            = "Listing Processes",
            ["get_disk_space"]           = "Checking Disk Space",
            ["get_network_info"]         = "Reading Network Config",
            ["get_performance_counters"] = "Reading Performance Counters",
            ["connect_server"]           = "Connecting to Server",
            ["connect_jea_server"]       = "Connecting to JEA Session",
            ["close_server_session"]     = "Closing Server Session",
        };

    private static readonly string[] SlashCommands =
    [
        "/help",
        "/status",
        "/clear",
        "/settings",
        "/model",
        "/mode",
        "/server",
        "/jea",
        "/capabilities",
        "/history",
        "/report",
        "/login",
        "/byok",
        "/exit",
        "/quit"
    ];

    private SystemMessageConfig CreateSystemMessage(string targetServer, IReadOnlyCollection<string>? additionalServerNames = null)
    {
        var effectivePrimary = targetServer;
        string? primaryJeaConfigName = null;
        PowerShellExecutor? primaryJeaExec = null;

        if (targetServer.Equals(_targetServer, StringComparison.OrdinalIgnoreCase)
            && TryGetEffectivePrimaryJeaSession(out var primaryJeaServerName, out var configurationName, out var jeaExecCandidate))
        {
            effectivePrimary = primaryJeaServerName;
            primaryJeaConfigName = configurationName;
            primaryJeaExec = jeaExecCandidate;
        }

        var targetInfo = effectivePrimary.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? "the local machine (localhost)"
            : $"the remote server: {effectivePrimary}";

        var connectedSessionsBlock = "";
        var jeaSessionsBlock = "";

        // Build the primary JEA context block when the effective primary is a JEA endpoint
        var primaryJeaBlock = "";
        if (primaryJeaConfigName != null)
        {
            var pjLines = new StringBuilder();
            pjLines.AppendLine();
            var safePrimary = SanitizeServerNameForPrompt(effectivePrimary);
            var safeConfig = SanitizeServerNameForPrompt(primaryJeaConfigName);
            pjLines.AppendLine($"## Primary JEA Endpoint: {safePrimary} (Configuration: {safeConfig})");
            pjLines.AppendLine("Your primary target is a constrained JEA endpoint. ONLY the following commands are available on this server:");

            if (primaryJeaExec?.JeaAllowedCommands is { Count: > 0 })
            {
                foreach (var cmd in primaryJeaExec.JeaAllowedCommands.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
                {
                    pjLines.AppendLine($"- {SanitizeServerNameForPrompt(cmd)}");
                }
            }
            else
            {
                pjLines.AppendLine("- Command discovery has not completed yet.");
            }

            pjLines.AppendLine("Do NOT attempt any other commands — they will be blocked by the JEA endpoint.");
            pjLines.AppendLine($"Use run_powershell with sessionName: \"{safePrimary}\" to target this JEA session.");
            pjLines.AppendLine("Do NOT use the built-in diagnostic helper tools for this endpoint; they rely on broader PowerShell language features than constrained JEA sessions allow.");
            pjLines.AppendLine();
            primaryJeaBlock = pjLines.ToString();
        }

        if (additionalServerNames is { Count: > 0 })
        {
            var sessionLines = new System.Text.StringBuilder();
            sessionLines.AppendLine();
            sessionLines.AppendLine("## Connected PSSessions");
            sessionLines.AppendLine("The following servers are ALREADY connected and available as named sessions. Use run_powershell with sessionName to target each:");
            if (primaryJeaConfigName != null)
            {
                sessionLines.AppendLine($"- Bootstrap/default session: {SanitizeServerNameForPrompt(targetServer)} — run_powershell without sessionName targets this session. Use it only when the user explicitly asks about {SanitizeServerNameForPrompt(targetServer)} or for local setup tasks.");
            }
            else
            {
                sessionLines.AppendLine($"- Primary (default): {SanitizeServerNameForPrompt(targetServer)} — use run_powershell without sessionName");
            }
            foreach (var server in additionalServerNames)
            {
                // Skip the JEA server that is already described in the primary JEA block
                if (primaryJeaConfigName != null && server.Equals(effectivePrimary, StringComparison.OrdinalIgnoreCase))
                    continue;

                var safe = SanitizeServerNameForPrompt(server);
                if (_additionalExecutors.TryGetValue(server, out var executor) && executor.IsJeaSession)
                {
                    var configName = SanitizeServerNameForPrompt(executor.ConfigurationName ?? "unknown");
                    sessionLines.AppendLine($"- {safe} (JEA: {configName}) — use run_powershell(command, sessionName: \"{safe}\")");
                }
                else
                {
                    sessionLines.AppendLine($"- {safe} — use run_powershell(command, sessionName: \"{safe}\")");
                }
            }
            sessionLines.AppendLine();
            sessionLines.AppendLine("When the user asks about multiple servers, gather data from ALL of them using run_powershell with the appropriate sessionName. Do NOT call connect_server for these — they are already connected.");
            connectedSessionsBlock = sessionLines.ToString();
        }

        // Build JEA blocks for additional (non-primary) JEA executors
        var jeaExecutors = _additionalExecutors
            .Where(entry => entry.Value.IsJeaSession)
            .Where(entry => primaryJeaConfigName == null || !entry.Key.Equals(effectivePrimary, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (jeaExecutors.Count > 0)
        {
            var jeaLines = new StringBuilder();
            jeaLines.AppendLine();
            foreach (var (serverName, executor) in jeaExecutors)
            {
                var safeServerName = SanitizeServerNameForPrompt(serverName);
                var safeConfigName = SanitizeServerNameForPrompt(executor.ConfigurationName ?? "unknown");
                jeaLines.AppendLine($"## JEA Session: {safeServerName} (Configuration: {safeConfigName})");
                jeaLines.AppendLine("This is a constrained JEA endpoint. ONLY the following commands are available:");

                if (executor.JeaAllowedCommands is { Count: > 0 })
                {
                    foreach (var commandName in executor.JeaAllowedCommands.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                    {
                        jeaLines.AppendLine($"- {SanitizeServerNameForPrompt(commandName)}");
                    }
                }
                else
                {
                    jeaLines.AppendLine("- Command discovery has not completed yet.");
                }

                jeaLines.AppendLine("Do NOT attempt any other commands — they will be blocked by the JEA endpoint.");
                jeaLines.AppendLine($"Use run_powershell with sessionName: \"{safeServerName}\" to target this session.");
                jeaLines.AppendLine();
            }

            jeaSessionsBlock = jeaLines.ToString();
        }

        var targetContextGuidance = primaryJeaConfigName == null
            ? $"""
            - You are currently connected to {targetInfo}
            - ALL commands and diagnostic operations will execute on this target server
            - When gathering data or making observations, you MUST always state which server the data comes from
            - Always verify that the data you receive is from the expected target server
            - If the user doesn't specify a server in their question, assume they mean the current target: {effectivePrimary}
            """
            : $"""
            - Your primary troubleshooting focus is {targetInfo}
            - If the user doesn't specify a server in their question, assume they mean the current JEA target: {effectivePrimary}
            - The default unnamed PowerShell session still targets {SanitizeServerNameForPrompt(targetServer)}. Do NOT use it for {SanitizeServerNameForPrompt(effectivePrimary)} unless the user explicitly asks about {SanitizeServerNameForPrompt(targetServer)}.
            - To work on {SanitizeServerNameForPrompt(effectivePrimary)}, use run_powershell with sessionName: "{SanitizeServerNameForPrompt(effectivePrimary)}"
            - Do NOT use the built-in diagnostic helper tools for {SanitizeServerNameForPrompt(effectivePrimary)}; they rely on broader PowerShell language features than constrained JEA endpoints allow
            - When gathering data or making observations, you MUST always state which server the data comes from
            - For the primary JEA endpoint, verify source using the targeted session/server name rather than `$env:COMPUTERNAME`, which may be unavailable in no-language mode
            """;

        var dataCollectionGuidance = primaryJeaConfigName == null
            ? "Use the diagnostic tools to collect relevant information FROM THE TARGET SERVER"
            : $"Use run_powershell with sessionName: \"{SanitizeServerNameForPrompt(effectivePrimary)}\" to collect data from the primary JEA endpoint. Only use the bootstrap session when the user explicitly asks about {SanitizeServerNameForPrompt(targetServer)}.";

        var sourceVerificationGuidance = primaryJeaConfigName == null
            ? $"Always confirm the data comes from {effectivePrimary} by checking $env:COMPUTERNAME"
            : $"For the primary JEA endpoint, confirm the source from the targeted session/server name ({SanitizeServerNameForPrompt(effectivePrimary)}) rather than using $env:COMPUTERNAME on the constrained endpoint";

        var investigationApproach = """
            ## Investigation Approach
            - When investigating an issue, exhaust ALL available diagnostic tools and data sources before asking the user for more information or permission to continue
            - Do NOT pause to ask the user if you should continue investigating — keep working until you have a clear diagnosis or have exhausted all available tools
            - Only ask clarifying questions when the initial problem description is genuinely ambiguous or when you need credentials/access that you do not have
            - Present your complete findings, analysis, and recommendations in one comprehensive response rather than stopping at each step to ask for permission
            - If one diagnostic approach yields no results, try alternative approaches before concluding
            - Gather data from ALL relevant sources (event logs, services, processes, performance counters, disk, network) in a single investigation pass
            """;

        var responseFormat = $"""
            ## Response Format
            - ALWAYS start your response by confirming which server you're analyzing (e.g., "Analyzing {effectivePrimary}...")
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

        var safetyGuidance = """
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

        if (_configuredSystemPromptOverrides != null)
        {
            if (_configuredSystemPromptOverrides.TryGetValue("investigation_approach", out var customInvestigation) && !string.IsNullOrWhiteSpace(customInvestigation))
                investigationApproach = customInvestigation;
            if (_configuredSystemPromptOverrides.TryGetValue("response_format", out var customFormat) && !string.IsNullOrWhiteSpace(customFormat))
                responseFormat = customFormat;
            if (_configuredSystemPromptOverrides.TryGetValue("safety", out var customSafety) && !string.IsNullOrWhiteSpace(customSafety))
                safetyGuidance = customSafety;
        }

        var appendSection = !string.IsNullOrWhiteSpace(_configuredSystemPromptAppend) ? $"\n\n{_configuredSystemPromptAppend}" : "";

        return new SystemMessageConfig
        {
            Content = $"""
            You are TroubleScout, an expert Windows Server troubleshooting assistant. 
            Your role is to diagnose issues on Windows servers by analyzing system data and providing actionable insights.

            ## Target Server Context
            {targetContextGuidance}
            {primaryJeaBlock}
            ## Your Capabilities
            - Execute read-only PowerShell commands (Get-*) to gather diagnostic information from the target server
            - Analyze Windows Event Logs, services, processes, performance counters, disk space, and network configuration
            - Use all available runtime capabilities when relevant, including built-in tools, configured MCP servers, and loaded skills
            - Always prefer using the available diagnostic tools to gather data rather than stating you cannot retrieve information
            - Attempt every relevant diagnostic tool before concluding data is unavailable
            - If a tool call returns an error or times out, retry it once with a slightly different approach before giving up
            - All read-only tools (get_system_info, get_event_logs, get_services, get_processes, get_disk_space, get_network_info, get_performance_counters) execute automatically without any confirmation required
            - Identify patterns, anomalies, and potential root causes
            - Provide clear, prioritized recommendations

            ## Troubleshooting Approach
            1. **Understand the Problem**: Ask clarifying questions if the issue description is vague
            2. **Gather Data**: {dataCollectionGuidance}
            3. **Verify Source**: {sourceVerificationGuidance}
            4. **Analyze**: Look for errors, warnings, resource exhaustion, or configuration issues
            5. **Diagnose**: Form hypotheses about the root cause based on evidence
            6. **Recommend**: Provide clear, actionable next steps
            
            {investigationApproach}
            
            {responseFormat}
            
            {safetyGuidance}

            ## Multi-Server Sessions & Double-Hop Avoidance
            - To avoid PowerShell double-hop authentication issues, NEVER run remote commands from one server to another.
            - If you need data from a different server, use connect_server(serverName) to establish a DIRECT session from this client.
            - If you need to use a constrained JEA endpoint, use connect_jea_server(serverName, configurationName) and then only run commands allowed by that endpoint.
            - Use run_powershell(command, sessionName: "serverName") to run commands on that specific server.
            - Use close_server_session(serverName) when done with a server to clean up resources.
            - Always indicate which server each piece of data comes from.
            {connectedSessionsBlock}
            {jeaSessionsBlock}
            Remember: Your goal is to help the user understand what's wrong with {effectivePrimary} and guide them to a solution, 
            not just dump raw data. Interpret the findings and provide expert analysis. Always maintain awareness of which 
            server you're working on.{appendSection}
            """
        };
    }

    /// <summary>
    /// Escapes a server name for safe embedding in the system prompt without changing its identity.
    /// Preserves the exact server identifier (including IPv6 colons, brackets, etc.) and only
    /// escapes characters that could break prompt syntax (backslashes and double-quotes).
    /// </summary>
    private static string SanitizeServerNameForPrompt(string serverName) =>
        serverName.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public TroubleshootingSession(
        string targetServer,
        string? model = null,
        string? mcpConfigPath = null,
        IReadOnlyList<string>? skillDirectories = null,
        IReadOnlyList<string>? disabledSkills = null,
        bool debugMode = false,
        ExecutionMode executionMode = ExecutionMode.Safe,
        bool useByokOpenAi = false,
        string? byokOpenAiBaseUrl = null,
        string? byokOpenAiApiKey = null,
        bool byokExplicitlyRequested = false,
        bool modelExplicitlyRequested = false,
        IReadOnlyList<string>? additionalInitialServers = null,
        (string ServerName, string ConfigurationName)? initialJeaSession = null)
    {
        _targetServer = string.IsNullOrWhiteSpace(targetServer) ? "localhost" : targetServer;
        _additionalInitialServers = (additionalInitialServers ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s) && !s.Equals(_targetServer, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _initialJeaSession = initialJeaSession is { } session
            ? (session.ServerName.Trim(), session.ConfigurationName.Trim())
            : null;
        _startupJeaFocusActive = _initialJeaSession.HasValue;
        _requestedModel = model;
        _mcpConfigPath = mcpConfigPath;
        _skillDirectories = skillDirectories?.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        _disabledSkills = disabledSkills?.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        _debugMode = debugMode;
        _executionMode = executionMode;
        _useByokOpenAi = useByokOpenAi;
        _byokExplicitlyRequested = byokExplicitlyRequested;
        _modelExplicitlyRequested = modelExplicitlyRequested;
        _byokOpenAiBaseUrl = string.IsNullOrWhiteSpace(byokOpenAiBaseUrl) ? DefaultOpenAiBaseUrl : byokOpenAiBaseUrl.Trim();
        _byokOpenAiApiKey = string.IsNullOrWhiteSpace(byokOpenAiApiKey)
            ? Environment.GetEnvironmentVariable(OpenAiApiKeyEnvironmentVariable)
            : byokOpenAiApiKey.Trim();
        _isGitHubCopilotAuthenticated = false;
        var settings = AppSettingsStore.Load();
        ApplySystemPromptSettings(settings.SystemPromptOverrides, settings.SystemPromptAppend);
        _systemMessageConfig = CreateSystemMessage(_targetServer);
        _executor = new PowerShellExecutor(_targetServer);
        _executor.ExecutionMode = _executionMode;
        ApplySafeCommandsToAllExecutors(settings.SafeCommands);
        _diagnosticTools = new DiagnosticTools(_executor, PromptApprovalAsync, _targetServer, RecordCommandAction,
            s => ConnectAdditionalServerAsync(s), GetExecutorForServer, CloseAdditionalServerSessionAsync,
            (serverName, configurationName) => ConnectJeaServerAsync(serverName, configurationName));
    }

    /// <summary>
    /// Convenience constructor: servers[0] is primary, the rest are additional initial servers.
    /// </summary>
    public TroubleshootingSession(
        IReadOnlyList<string> servers,
        string? model = null,
        string? mcpConfigPath = null,
        IReadOnlyList<string>? skillDirectories = null,
        IReadOnlyList<string>? disabledSkills = null,
        bool debugMode = false,
        ExecutionMode executionMode = ExecutionMode.Safe,
        bool useByokOpenAi = false,
        string? byokOpenAiBaseUrl = null,
        string? byokOpenAiApiKey = null,
        (string ServerName, string ConfigurationName)? initialJeaSession = null)
        : this(
            servers.Count > 0 ? servers[0] : "localhost",
            model,
            mcpConfigPath,
            skillDirectories,
            disabledSkills,
            debugMode,
            executionMode,
            useByokOpenAi,
            byokOpenAiBaseUrl,
            byokOpenAiApiKey,
            additionalInitialServers: servers.Count > 1 ? servers.Skip(1).ToList() : null,
            initialJeaSession: initialJeaSession)
    {
    }

    private readonly string? _requestedModel;
    private static readonly Regex MutatingIntentRegex = new(
        "\\b(empty|clear|delete|remove|restart|stop|start|set|enable|disable|kill|format|reset|recycle\\s+bin|trash)\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CliModelIdRegex = new(
        "\"((?:claude|gpt|gemini)-[a-z0-9][a-z0-9.-]*)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string TargetServer => _targetServer;
    public string ConnectionMode => _executor.GetConnectionMode();

    private bool TryGetEffectivePrimaryJeaSession(
        out string serverName,
        out string configurationName,
        out PowerShellExecutor executor)
    {
        if (_initialJeaSession is { } jea
            && _startupJeaFocusActive
            && _targetServer.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && _additionalExecutors.TryGetValue(jea.ServerName, out var candidate)
            && candidate.IsJeaSession)
        {
            serverName = jea.ServerName;
            configurationName = candidate.ConfigurationName ?? jea.ConfigurationName;
            executor = candidate;
            return true;
        }

        serverName = string.Empty;
        configurationName = string.Empty;
        executor = null!;
        return false;
    }

    /// <summary>
    /// The effective primary target for display purposes. When a startup JEA session is active
    /// and the base target is localhost, the JEA server is the effective primary target.
    /// </summary>
    public string EffectiveTargetServer
    {
        get
        {
            if (TryGetEffectivePrimaryJeaSession(out var serverName, out _, out _))
            {
                return serverName;
            }

            return _targetServer;
        }
    }

    /// <summary>
    /// Connection mode reflecting the effective primary context. Returns JEA mode
    /// when a startup JEA session is the effective primary target.
    /// </summary>
    public string EffectiveConnectionMode
    {
        get
        {
            if (TryGetEffectivePrimaryJeaSession(out _, out var configurationName, out _))
            {
                return $"JEA ({configurationName})";
            }

            return _executor.GetConnectionMode();
        }
    }

    /// <summary>
    /// All target servers, with the effective primary listed first.
    /// When a startup JEA session is the effective primary, localhost is excluded from the list.
    /// </summary>
    public IReadOnlyList<string> EffectiveTargetServers
    {
        get
        {
            if (TryGetEffectivePrimaryJeaSession(out var serverName, out _, out _))
            {
                // JEA server is primary; exclude localhost from the visible list
                return [serverName, .._additionalExecutors.Keys
                    .Where(k => !k.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];
            }

            return [_targetServer, .._additionalExecutors.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];
        }
    }

    public string? DefaultSessionTarget =>
        TryGetEffectivePrimaryJeaSession(out _, out _, out _) ? _targetServer : null;

    public bool IsAiSessionReady => _copilotSession != null;
    public string SelectedModel => GetModelDisplayName(_selectedModel) ?? "default";
    public string ActiveProviderDisplayName => _useByokOpenAi ? "BYOK (OpenAI)" : "GitHub Copilot";
    public string CopilotVersion => _copilotVersion ?? "unknown";
    public IReadOnlyList<string> ConfiguredMcpServers => _configuredMcpServers;
    public IReadOnlyList<string> RuntimeMcpServers => _runtimeMcpServers.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    public IReadOnlyList<string> ConfiguredSkills => _configuredSkills;
    public IReadOnlyList<string> RuntimeSkills => _runtimeSkills.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    public IReadOnlyList<string> ConfigurationWarnings => _configurationWarnings;
    public ExecutionMode CurrentExecutionMode => _executionMode;
    public IReadOnlyList<string> AllTargetServers =>
        [_targetServer, .._additionalExecutors.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];

    private sealed record CopilotUsageSnapshot(
        int? PromptTokens,
        int? CompletionTokens,
        int? TotalTokens,
        int? InputTokens,
        int? OutputTokens,
        int? MaxContextTokens,
        int? UsedContextTokens,
        int? FreeContextTokens)
    {
        public bool HasAny => PromptTokens.HasValue || CompletionTokens.HasValue || TotalTokens.HasValue ||
                              InputTokens.HasValue || OutputTokens.HasValue || MaxContextTokens.HasValue ||
                              UsedContextTokens.HasValue || FreeContextTokens.HasValue;
    }

    private sealed record ReportPromptEntry(DateTimeOffset Timestamp, string Prompt, List<ReportActionEntry> Actions, string AgentReply);

    private sealed record ReportActionEntry(
        DateTimeOffset Timestamp,
        string Target,
        string Command,
        string Output,
        string SafetyApproval,
        string Source);

    public sealed record CopilotPrerequisiteIssue(string Title, string Details, bool IsBlocking);

    public sealed record CopilotPrerequisiteReport(IReadOnlyList<CopilotPrerequisiteIssue> Issues)
    {
        public bool IsReady => Issues.All(issue => !issue.IsBlocking);

        public string ToDisplayText(bool includeWarnings = true)
        {
            var lines = new List<string>();
            var relevantIssues = includeWarnings
                ? Issues
                : Issues.Where(issue => issue.IsBlocking).ToList();

            foreach (var issue in relevantIssues)
            {
                lines.Add($"- {issue.Title}");
                lines.Add($"  {issue.Details}");
                lines.Add(string.Empty);
            }

            return lines.Count == 0
                ? "All Copilot prerequisites look good."
                : string.Join(Environment.NewLine, lines).TrimEnd();
        }
    }

    /// <summary>
    /// Initialize the session and establish connections
    /// </summary>
    public async Task<bool> InitializeAsync(Action<string>? updateStatus = null, bool allowInteractiveSetup = false)
    {
        if (_isInitialized)
            return true;

        var copilotInitializationStarted = false;

        try
        {
            // Test PowerShell connection and verify target
            updateStatus?.Invoke($"Connecting to {_targetServer}...");
            
            var (connectionSuccess, connectionError) = await _executor.TestConnectionAsync();
            if (!connectionSuccess)
            {
                ConsoleUI.ShowError("Connection Failed", connectionError ?? $"Unable to connect to {_targetServer}");
                return false;
            }

            // Show verified connection
            updateStatus?.Invoke($"Connected to {_executor.ActualComputerName}...");

            // Connect additional initial servers (non-fatal)
            foreach (var additionalServer in _additionalInitialServers)
            {
                if (additionalServer.Equals(_targetServer, StringComparison.OrdinalIgnoreCase))
                    continue;

                updateStatus?.Invoke($"Connecting to {additionalServer}...");
                var (addSuccess, addError) = await ConnectAdditionalServerAsync(additionalServer, skipApproval: true);
                if (!addSuccess)
                {
                    _configurationWarnings.Add($"Could not connect to additional server '{additionalServer}': {addError}");
                    if (_debugMode)
                    {
                        ConsoleUI.ShowWarning($"Additional server '{additionalServer}' failed: {addError}");
                    }
                }
            }

            // Regenerate system message to include successfully connected additional servers
            if (_additionalExecutors.Count > 0)
            {
                _systemMessageConfig = CreateSystemMessage(_targetServer, _additionalExecutors.Keys.ToList());
            }

            if (_initialJeaSession is { } initialJeaSession)
            {
                updateStatus?.Invoke($"Connecting to JEA endpoint {initialJeaSession.ConfigurationName} on {initialJeaSession.ServerName}...");
                var (jeaSuccess, jeaError) = await ConnectJeaServerAsync(
                    initialJeaSession.ServerName,
                    initialJeaSession.ConfigurationName,
                    skipApproval: true);
                if (!jeaSuccess)
                {
                    ConsoleUI.ShowError(
                        "JEA Connection Failed",
                        jeaError ?? $"Unable to connect to JEA endpoint '{initialJeaSession.ConfigurationName}' on {initialJeaSession.ServerName}");
                    return false;
                }
            }

            await WarnIfPowerShellVersionIsOldAsync();

            // Initialize Copilot client
            updateStatus?.Invoke("Starting Copilot SDK...");
            copilotInitializationStarted = true;
            
            // Resolve Copilot CLI path (env override, bundled app CLI, then installed fallback).
            // If nothing explicit is found, let the SDK use its default resolution path.
            var cliPath = TryResolvePreferredCopilotCliPath();

            var clientOptions = new CopilotClientOptions
            {
                LogLevel = "info"
            };

            if (!string.IsNullOrWhiteSpace(cliPath))
            {
                clientOptions.CliPath = cliPath;
            }

            _copilotClient = new CopilotClient(clientOptions);

            try
            {
                await _copilotClient.StartAsync();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("protocol version mismatch", StringComparison.OrdinalIgnoreCase))
            {
                var report = await ValidateCopilotPrerequisitesAsync();
                await ShowCopilotInitializationFailureAsync(
                    BuildProtocolMismatchMessage(report, _debugMode),
                    ex,
                    includeDiagnostics: true);
                _copilotClient = null;
                return false;
            }
            catch (Exception)
            {
                // CLI startup failed (e.g. unsupported --headless flag on older CLI versions).
                // Dispose and null out the client so guard-checks see it as unavailable,
                // then re-throw so the outer handler can show actionable diagnostics.
                try { await _copilotClient.DisposeAsync(); } catch { /* best-effort */ }
                _copilotClient = null;
                throw;
            }

            _isGitHubCopilotAuthenticated = await IsGitHubAuthenticatedAsync();

            if (_useByokOpenAi)
            {
                if (string.IsNullOrWhiteSpace(_byokOpenAiApiKey))
                {
                    if (_byokExplicitlyRequested)
                    {
                        // User explicitly passed --byok-openai: fail with a clear error.
                        await ShowCopilotInitializationFailureAsync(
                            $"BYOK mode requires an OpenAI API key.\n\nSet {OpenAiApiKeyEnvironmentVariable} or pass --openai-api-key.",
                            includeDiagnostics: false);
                        return false;
                    }

                    // BYOK was loaded from saved settings but no key is available.
                    // Fall back to GitHub Copilot so the app can still open.
                    ConsoleUI.ShowWarning(
                        "BYOK is enabled in saved settings but no API key is available. Falling back to GitHub Copilot.\n" +
                        "Use /byok to configure OpenAI-compatible mode, or /model to switch provider.");
                    _useByokOpenAi = false;
                    // fall through to GitHub Copilot path below
                }
                else
                {
                    // Use requested model only when it was explicitly specified via CLI.
                    // Otherwise pass null so the BYOK endpoint uses its default model,
                    // avoiding failures from a stale saved model that may not be supported.
                    var byokModel = _modelExplicitlyRequested
                        ? _requestedModel
                        : (!string.IsNullOrWhiteSpace(_selectedModel) ? _selectedModel : _requestedModel);

                    if (!await CreateCopilotSessionAsync(byokModel, updateStatus))
                    {
                        return false;
                    }

                    await RefreshAvailableModelsAsync();

                    _isInitialized = true;
                    return true;
                }
            }

            if (!_isGitHubCopilotAuthenticated)
            {
                if (allowInteractiveSetup)
                {
                    ConsoleUI.ShowWarning(
                        "GitHub Copilot is not authenticated. Use /login to sign in, or /byok to configure OpenAI-compatible BYOK.");
                    _isInitialized = true;
                    return true;
                }

                await ShowCopilotInitializationFailureAsync(
                    "Copilot CLI is installed but not authenticated.\n\nTo continue:\n  1. Run: copilot login\n  2. Re-run TroubleScout",
                    includeDiagnostics: true);
                return false;
            }

            updateStatus?.Invoke("Fetching available models...");
            _availableModels = await GetMergedModelListAsync(cliPath);

            if (_availableModels.Count == 0)
            {
                await ShowCopilotInitializationFailureAsync(
                    "No models were returned by Copilot CLI. Ensure you are authenticated and your subscription has model access.",
                    includeDiagnostics: true);
                return false;
            }

            var effectiveModel = ResolveInitialSessionModel(_availableModels);
            if (!string.IsNullOrWhiteSpace(_requestedModel)
                && !string.Equals(effectiveModel, _requestedModel, StringComparison.OrdinalIgnoreCase)
                && _availableModels.All(m => m.Id != _requestedModel))
            {
                if (_modelExplicitlyRequested)
                {
                    // User explicitly passed --model: fail with a clear error.
                    ConsoleUI.ShowError("Invalid Model", $"The requested model '{_requestedModel}' is not available.");
                    return false;
                }

                // Model from saved settings is not available (e.g. a BYOK-only model after switching to GitHub).
                // Warn and fall back to the first verified available model rather than trusting the SDK default.
                ConsoleUI.ShowWarning($"Saved model '{_requestedModel}' is not available with the current provider. Using '{effectiveModel}'.\nUse /model to select a different one.");
            }

            if (!await CreateCopilotSessionAsync(effectiveModel, updateStatus))
            {
                if (allowInteractiveSetup)
                {
                    ConsoleUI.ShowWarning("AI session is not ready. Use /login or /byok to set up authentication, then continue.");
                    _isInitialized = true;
                    return true;
                }

                return false;
            }

            await RefreshAvailableModelsAsync();
            
            _isInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            if (copilotInitializationStarted)
            {
                var report = await ValidateCopilotPrerequisitesAsync();
                if (allowInteractiveSetup)
                {
                    ConsoleUI.ShowWarning(BuildActionableInitializationMessage(ex, report, _debugMode));
                    _isInitialized = true;
                    return true;
                }

                await ShowCopilotInitializationFailureAsync(
                    BuildActionableInitializationMessage(ex, report, _debugMode),
                    ex,
                    includeDiagnostics: true);
                return false;
            }

            ConsoleUI.ShowError("Initialization Failed", "TroubleScout could not complete startup.");
            if (_debugMode)
            {
                ConsoleUI.ShowWarning($"Technical details: {TrimSingleLine(ex.Message)}");
            }
            return false;
        }
    }

    private string? ResolveInitialSessionModel(IReadOnlyList<ModelInfo> availableModels)
    {
        if (!string.IsNullOrWhiteSpace(_requestedModel)
            && availableModels.Any(model => model.Id.Equals(_requestedModel, StringComparison.OrdinalIgnoreCase)))
        {
            return _requestedModel;
        }

        return availableModels.FirstOrDefault()?.Id;
    }

    /// <summary>
    /// Change the AI model by creating a new session
    /// </summary>
    public async Task<bool> ChangeModelAsync(string newModel, Action<string>? updateStatus = null)
    {
        if (_copilotClient == null)
        {
            ConsoleUI.ShowError("Not Connected", "Copilot client not initialized");
            return false;
        }

        if (string.IsNullOrWhiteSpace(newModel))
        {
            ConsoleUI.ShowError("Invalid Model", "Model cannot be empty.");
            return false;
        }

        if (_availableModels.Count == 0 || _availableModels.All(m => !m.Id.Equals(newModel, StringComparison.OrdinalIgnoreCase)))
        {
            ConsoleUI.ShowError("Invalid Model", $"The selected model '{newModel}' is not available.");
            return false;
        }

        var targetSource = ResolveTargetSource(newModel);
        if (targetSource == ModelSource.None)
        {
            ConsoleUI.ShowError("Invalid Model", $"Could not determine provider for model '{newModel}'.");
            return false;
        }

        if (targetSource == ModelSource.Byok)
        {
            if (!IsByokConfigured())
            {
                ConsoleUI.ShowWarning("BYOK is not configured. Run /byok first to use BYOK models.");
                return false;
            }

            _useByokOpenAi = true;
        }
        else
        {
            if (!_isGitHubCopilotAuthenticated)
            {
                ConsoleUI.ShowWarning("GitHub Copilot is not authenticated. Run /login to use GitHub models.");
                return false;
            }

            _useByokOpenAi = false;
        }

        try
        {
            // Dispose existing session
            if (_copilotSession != null)
            {
                updateStatus?.Invoke("Closing current session...");
                await _copilotSession.DisposeAsync();
                _copilotSession = null;
            }

            if (!await CreateCopilotSessionAsync(newModel, updateStatus))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowError("Model Change Failed", ex.Message);
            return false;
        }
    }

    internal async Task<bool> ChangeModelAsync(ModelSelectionEntry entry, Action<string>? updateStatus = null)
    {
        if (_copilotClient == null)
        {
            ConsoleUI.ShowError("Not Connected", "Copilot client not initialized");
            return false;
        }

        if (string.IsNullOrWhiteSpace(entry.ModelId))
        {
            ConsoleUI.ShowError("Invalid Model", "Model cannot be empty.");
            return false;
        }

        if (_availableModels.Count == 0 || _availableModels.All(m => !m.Id.Equals(entry.ModelId, StringComparison.OrdinalIgnoreCase)))
        {
            ConsoleUI.ShowError("Invalid Model", $"The selected model '{entry.ModelId}' is not available.");
            return false;
        }

        if (entry.Source == ModelSource.Byok)
        {
            if (!IsByokConfigured())
            {
                ConsoleUI.ShowWarning("BYOK is not configured. Run /byok first to use BYOK models.");
                return false;
            }

            _useByokOpenAi = true;
        }
        else
        {
            if (!_isGitHubCopilotAuthenticated)
            {
                ConsoleUI.ShowWarning("GitHub Copilot is not authenticated. Run /login to use GitHub models.");
                return false;
            }

            _useByokOpenAi = false;
        }

        try
        {
            if (_copilotSession != null)
            {
                updateStatus?.Invoke("Closing current session...");
                await _copilotSession.DisposeAsync();
                _copilotSession = null;
            }

            if (!await CreateCopilotSessionAsync(entry.ModelId, updateStatus))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowError("Model Change Failed", ex.Message);
            return false;
        }
    }

    private ModelSource ResolveTargetSource(string modelId)
    {
        if (!_modelSources.TryGetValue(modelId, out var source))
        {
            return ModelSource.None;
        }

        if ((source & ModelSource.Byok) != 0 && (source & ModelSource.GitHub) != 0)
        {
            if (_useByokOpenAi)
            {
                return ModelSource.Byok;
            }

            return _isGitHubCopilotAuthenticated ? ModelSource.GitHub : ModelSource.Byok;
        }

        if ((source & ModelSource.Byok) != 0)
        {
            return ModelSource.Byok;
        }

        if ((source & ModelSource.GitHub) != 0)
        {
            return ModelSource.GitHub;
        }

        return ModelSource.None;
    }

    /// <summary>
    /// Get the path to the Copilot CLI.
    /// </summary>
    internal static string GetCopilotCliPath()
    {
        var preferredPath = TryResolvePreferredCopilotCliPath();
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            return preferredPath;
        }

        // Default to copilot in PATH
        return "copilot";
    }

    private static string? TryResolvePreferredCopilotCliPath()
    {
        // Check for COPILOT_CLI_PATH environment variable first
        var envPath = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        if (!string.IsNullOrEmpty(envPath) && FileExistsResolver(envPath))
        {
            return envPath;
        }

        var bundledPath = TryResolveBundledCopilotCliPath();
        if (!string.IsNullOrWhiteSpace(bundledPath))
        {
            return bundledPath;
        }

        var installedPath = TryResolveInstalledCopilotCliPath();
        if (!string.IsNullOrWhiteSpace(installedPath))
        {
            return installedPath;
        }

        return null;
    }

    private static string? TryResolveInstalledCopilotCliPath()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        var searchDirs = pathValue
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim().Trim('"'))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var directory in searchDirs)
        {
            var exePath = Path.Combine(directory, "copilot.exe");
            if (File.Exists(exePath))
            {
                return exePath;
            }
        }

        foreach (var directory in searchDirs)
        {
            var npmLoaderPath = Path.Combine(directory, "node_modules", "@github", "copilot", "npm-loader.js");
            if (File.Exists(npmLoaderPath))
            {
                return npmLoaderPath;
            }

            var indexPath = Path.Combine(directory, "node_modules", "@github", "copilot", "index.js");
            if (File.Exists(indexPath))
            {
                return indexPath;
            }
        }

        return null;
    }

    private static string? TryResolveBundledCopilotCliPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };
        var runtimeIdentifier = $"win-{architecture}";

        var candidates = new[]
        {
            Path.Combine(baseDirectory, "copilot.exe"),
            Path.Combine(baseDirectory, $"copilot-{runtimeIdentifier}.exe"),
            Path.Combine(baseDirectory, "vendor", "copilot.exe"),
            Path.Combine(baseDirectory, "vendor", $"copilot-{runtimeIdentifier}.exe"),
            Path.Combine(baseDirectory, "runtimes", runtimeIdentifier, "native", "copilot.exe"),
            Path.Combine(baseDirectory, "runtimes", runtimeIdentifier, "native", $"copilot-{runtimeIdentifier}.exe")
        };

        foreach (var candidate in candidates)
        {
            if (FileExistsResolver(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string? GetModelDisplayName(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var model = _availableModels.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        return model?.Name ?? modelId;
    }

    /// <summary>
    /// Check if Copilot SDK CLI is available
    /// </summary>
    public static async Task<bool> CheckCopilotAvailableAsync()
    {
        var report = await ValidateCopilotPrerequisitesAsync();
        return report.IsReady;
    }

    public static async Task<CopilotPrerequisiteReport> ValidateCopilotPrerequisitesAsync()
    {
        var issues = new List<CopilotPrerequisiteIssue>();

        try
        {
            var cliPath = CopilotCliPathResolver();

            if (Path.IsPathRooted(cliPath) && !FileExistsResolver(cliPath))
            {
                issues.Add(new CopilotPrerequisiteIssue(
                    "Copilot CLI binary was not found",
                    "TroubleScout could not locate the configured Copilot CLI path.\n" +
                    $"Configured path: {cliPath}\n\n" +
                    "If you are using a bundled deployment, ensure the CLI binary is included in the app folder.\n" +
                    "If you are using a system installation, set COPILOT_CLI_PATH or install Copilot CLI globally.",
                    true));

                return new CopilotPrerequisiteReport(issues);
            }

            if (cliPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && !FileExistsResolver(cliPath))
            {
                issues.Add(new CopilotPrerequisiteIssue(
                    "Copilot CLI is not installed",
                    "Install the prerequisites, then authenticate:\n" +
                    "  1. Install Node.js: https://nodejs.org/\n" +
                    $"  2. Install or update Copilot CLI: {CopilotCliInstallUrl}\n" +
                    "  3. Authenticate: copilot login\n" +
                    $"\nReferences:\n- {CopilotCliRepoUrl}\n- {CopilotCliInstallUrl}",
                    true));

                return new CopilotPrerequisiteReport(issues);
            }

            if (await CliPathRequiresNodeRuntimeAsync(cliPath))
            {
                var nodeVersion = await ProcessRunnerResolver("node", "--version");
                if (nodeVersion.ExitCode != 0)
                {
                    issues.Add(new CopilotPrerequisiteIssue(
                        "Node.js runtime is missing",
                        "Copilot CLI requires Node.js on this machine.\n" +
                        "Install Node.js from https://nodejs.org/ and restart your terminal.\n" +
                        $"Detection details: {TrimSingleLine(nodeVersion.StdErr)}",
                        true));

                    return new CopilotPrerequisiteReport(issues);
                }

                var detectedVersion = TrimSingleLine(nodeVersion.StdOut);
                var nodeMajorVersion = ParseNodeMajorVersion(detectedVersion);
                if (!nodeMajorVersion.HasValue || nodeMajorVersion.Value < MinSupportedNodeMajorVersion)
                {
                    issues.Add(new CopilotPrerequisiteIssue(
                        "Node.js version is unsupported",
                        $"Your Copilot CLI path appears to use the Node.js runtime and requires Node.js {MinSupportedNodeMajorVersion}+ (LTS recommended).\n" +
                        $"Detected: {detectedVersion}\n\n" +
                        "Fix on Windows:\n" +
                        "  1. winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements\n" +
                        "  2. Restart your terminal\n" +
                        $"  3. Install or update Copilot CLI: {CopilotCliInstallUrl}\n" +
                        "  4. Re-run TroubleScout\n\n" +
                        $"References:\n- {CopilotCliRepoUrl}\n- {CopilotCliInstallUrl}",
                        true));

                    return new CopilotPrerequisiteReport(issues);
                }
            }

            var (versionCommand, versionArguments) = BuildCopilotCommand(cliPath, "--version");
            var versionResult = await ProcessRunnerResolver(versionCommand, versionArguments);

            if (versionResult.ExitCode != 0)
            {
                issues.Add(new CopilotPrerequisiteIssue(
                    "Copilot CLI command failed",
                    "TroubleScout could not run the Copilot CLI version check.\n" +
                    "Try these commands:\n" +
                    $"  - Install/update Copilot CLI: {CopilotCliInstallUrl}\n" +
                    "  - copilot --version\n" +
                    "  - copilot login\n" +
                    $"\nCLI path used: {cliPath}\n" +
                    $"Error: {TrimSingleLine(string.IsNullOrWhiteSpace(versionResult.StdErr) ? versionResult.StdOut : versionResult.StdErr)}\n" +
                    $"\nReferences:\n- {CopilotCliRepoUrl}\n- {CopilotCliInstallUrl}",
                    true));

                return new CopilotPrerequisiteReport(issues);
            }

            var powerShellWarning = await DetectPowerShellVersionWarningAsync();
            if (!string.IsNullOrWhiteSpace(powerShellWarning))
            {
                issues.Add(new CopilotPrerequisiteIssue(
                    "PowerShell version is below recommended",
                    powerShellWarning,
                    false));
            }

            return new CopilotPrerequisiteReport(issues);
        }
        catch (Exception ex)
        {
            issues.Add(new CopilotPrerequisiteIssue(
                "Could not fully validate Copilot prerequisites",
                "TroubleScout could not complete the prerequisite check. Verify manually:\n" +
                "  - copilot --version\n" +
                $"  - Install/update Copilot CLI: {CopilotCliInstallUrl}\n" +
                $"Error: {TrimSingleLine(ex.Message)}",
                true));
            return new CopilotPrerequisiteReport(issues);
        }
    }

    private static (string Command, string Arguments) BuildCopilotCommand(string cliPath, string arguments)
    {
        if (cliPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            return ("node", $"\"{cliPath}\" {arguments}");
        }

        if (cliPath.Equals("copilot", StringComparison.OrdinalIgnoreCase))
        {
            return ("cmd.exe", $"/c copilot {arguments}");
        }

        return ("cmd.exe", $"/c \"\"{cliPath}\" {arguments}\"");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(string fileName, string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                return (-1, string.Empty, "Failed to start process.");
            }

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Ignore kill failures during timeout handling.
                }

                var timedOutStdOut = await stdOutTask;
                var timedOutStdErr = await stdErrTask;
                return (-1, timedOutStdOut,
                    string.IsNullOrWhiteSpace(timedOutStdErr)
                        ? "Process timed out after 10 seconds."
                        : TrimSingleLine(timedOutStdErr));
            }

            return (process.ExitCode, await stdOutTask, await stdErrTask);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    internal static void ResetPrerequisiteValidationResolvers()
    {
        CopilotCliPathResolver = GetCopilotCliPath;
        FileExistsResolver = File.Exists;
        ProcessRunnerResolver = RunProcessAsync;
    }

    private static bool CliPathRequiresNodeRuntime(string cliPath)
    {
        if (string.IsNullOrWhiteSpace(cliPath))
            return true;

        if (cliPath.Equals("copilot", StringComparison.OrdinalIgnoreCase))
            return false;

        return cliPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
               cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
               cliPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> CliPathRequiresNodeRuntimeAsync(string cliPath)
    {
        if (CliPathRequiresNodeRuntime(cliPath))
        {
            return true;
        }

        if (!cliPath.Equals("copilot", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var whereResult = await ProcessRunnerResolver("cmd.exe", "/c where copilot");
        if (whereResult.ExitCode != 0)
        {
            return true;
        }

        var firstPath = whereResult.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim().Trim('"'))
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstPath))
        {
            return true;
        }

        var extension = Path.GetExtension(firstPath);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(extension);
    }

    private async Task<List<ModelInfo>> GetMergedModelListAsync(string? cliPath)
    {
        if (_copilotClient == null)
        {
            return [];
        }

        var models = await _copilotClient.ListModelsAsync();

        var existingIds = new HashSet<string>(
            models.Where(model => !string.IsNullOrWhiteSpace(model.Id)).Select(model => model.Id),
            StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(cliPath))
        {
            var cliModelIds = await TryGetCliModelIdsAsync(cliPath);
            foreach (var cliModelId in cliModelIds)
            {
                if (existingIds.Contains(cliModelId))
                {
                    continue;
                }

                models.Add(new ModelInfo
                {
                    Id = cliModelId,
                    Name = ToModelDisplayName(cliModelId)
                });
                existingIds.Add(cliModelId);
            }
        }

        return models;
    }

    private async Task<List<ModelInfo>> TryGetGitHubProviderModelsAsync()
    {
        if (_copilotClient == null || !_isGitHubCopilotAuthenticated)
        {
            return [];
        }

        try
        {
            return await GetMergedModelListAsync(GetCopilotCliPath());
        }
        catch
        {
            return [];
        }
    }

    private void UpdateAvailableModels(IReadOnlyList<ModelInfo> githubModels, IReadOnlyList<ModelInfo> byokModels)
    {
        _modelSources.Clear();

        var byId = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in githubModels.Where(model => !string.IsNullOrWhiteSpace(model.Id)))
        {
            _modelSources[model.Id] = _modelSources.TryGetValue(model.Id, out var existing)
                ? existing | ModelSource.GitHub
                : ModelSource.GitHub;

            byId[model.Id] = new ModelInfo
            {
                Id = model.Id,
                Name = model.Name,
                Billing = model.Billing,
                Capabilities = model.Capabilities,
                Policy = model.Policy,
                SupportedReasoningEfforts = model.SupportedReasoningEfforts,
                DefaultReasoningEffort = model.DefaultReasoningEffort
            };
        }

        foreach (var model in byokModels.Where(model => !string.IsNullOrWhiteSpace(model.Id)))
        {
            _modelSources[model.Id] = _modelSources.TryGetValue(model.Id, out var existing)
                ? existing | ModelSource.Byok
                : ModelSource.Byok;

            if (!byId.TryGetValue(model.Id, out var existingModel))
            {
                var clonedModel = new ModelInfo
                {
                    Id = model.Id,
                    Name = model.Name,
                    Billing = model.Billing,
                    Policy = model.Policy,
                    DefaultReasoningEffort = model.DefaultReasoningEffort
                };

                if (model.Capabilities != null)
                {
                    clonedModel.Capabilities = model.Capabilities;
                }

                if (model.SupportedReasoningEfforts != null)
                {
                    clonedModel.SupportedReasoningEfforts = model.SupportedReasoningEfforts;
                }

                byId[model.Id] = clonedModel;
            }
            else
            {
                if (existingModel.Billing == null && model.Billing != null)
                {
                    existingModel.Billing = model.Billing;
                }

                if (string.IsNullOrWhiteSpace(existingModel.Name) && !string.IsNullOrWhiteSpace(model.Name))
                {
                    existingModel.Name = model.Name;
                }

                if (existingModel.Capabilities == null && model.Capabilities != null)
                {
                    existingModel.Capabilities = model.Capabilities;
                }

                if (existingModel.Policy == null && model.Policy != null)
                {
                    existingModel.Policy = model.Policy;
                }

                if ((existingModel.SupportedReasoningEfforts == null || existingModel.SupportedReasoningEfforts.Count == 0)
                    && model.SupportedReasoningEfforts is { Count: > 0 })
                {
                    existingModel.SupportedReasoningEfforts = model.SupportedReasoningEfforts;
                }

                if (string.IsNullOrWhiteSpace(existingModel.DefaultReasoningEffort)
                    && !string.IsNullOrWhiteSpace(model.DefaultReasoningEffort))
                {
                    existingModel.DefaultReasoningEffort = model.DefaultReasoningEffort;
                }
            }
        }

        _availableModels = byId.Values
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var model in _availableModels)
        {
            var sourceLabel = _modelSources.TryGetValue(model.Id, out var source)
                ? source switch
                {
                    ModelSource.GitHub => "GitHub",
                    ModelSource.Byok => "BYOK",
                    ModelSource.GitHub | ModelSource.Byok => "GitHub+BYOK",
                    _ => "Unknown"
                }
                : "Unknown";

            model.Name = $"{ToModelDisplayName(model.Id)} [{sourceLabel}]";
        }
    }

    private IReadOnlyList<ModelSelectionEntry> GetModelSelectionEntries()
    {
        var entries = new List<ModelSelectionEntry>();

        foreach (var model in _availableModels)
        {
            if (!_modelSources.TryGetValue(model.Id, out var source))
                continue;

            var displayBase = ToModelDisplayName(model.Id);

            if ((source & ModelSource.GitHub) != 0 && (source & ModelSource.Byok) != 0)
            {
                if (_isGitHubCopilotAuthenticated)
                {
                    entries.Add(BuildModelSelectionEntry(model, displayBase, ModelSource.GitHub));
                }

                if (IsByokConfigured())
                {
                    entries.Add(BuildModelSelectionEntry(model, displayBase, ModelSource.Byok));
                }
            }
            else if ((source & ModelSource.GitHub) != 0)
            {
                if (_isGitHubCopilotAuthenticated)
                {
                    entries.Add(BuildModelSelectionEntry(model, displayBase, ModelSource.GitHub));
                }
            }
            else if ((source & ModelSource.Byok) != 0)
            {
                if (IsByokConfigured())
                {
                    entries.Add(BuildModelSelectionEntry(model, displayBase, ModelSource.Byok));
                }
            }
        }

        return entries;
    }

    private ModelSelectionEntry BuildModelSelectionEntry(ModelInfo model, string displayBase, ModelSource source)
    {
        var providerLabel = source == ModelSource.Byok ? "BYOK / OpenAI" : "GitHub Copilot";
        return new ModelSelectionEntry(model.Id, $"{displayBase} ({providerLabel})", source)
        {
            ProviderLabel = providerLabel,
            RateLabel = GetModelRateLabel(model, source),
            DetailSummary = BuildModelDetailSummary(model, source),
            IsCurrent = IsCurrentModelAndSource(model.Id, source)
        };
    }

    private async Task RefreshAvailableModelsAsync()
    {
        var githubModels = await TryGetGitHubProviderModelsAsync();
        var byokModels = await TryGetByokProviderModelsAsync();
        UpdateAvailableModels(githubModels, byokModels);
    }

    private static string ToModelDisplayName(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return modelId;
        }

        var tokens = modelId.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return modelId;
        }

        var formattedTokens = tokens.Select(token => token.ToLowerInvariant() switch
        {
            "gpt" => "GPT",
            "claude" => "Claude",
            "gemini" => "Gemini",
            "codex" => "Codex",
            "mini" => "Mini",
            "max" => "Max",
            "pro" => "Pro",
            "preview" => "(Preview)",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(token)
        });

        return string.Join(' ', formattedTokens);
    }

    private bool IsByokConfigured()
    {
        return !string.IsNullOrWhiteSpace(_byokOpenAiApiKey) && LooksLikeUrl(_byokOpenAiBaseUrl);
    }

    private string GetModelRateLabel(ModelInfo model, ModelSource source)
    {
        if (source == ModelSource.Byok
            && _byokPricing.TryGetValue(model.Id, out var byokPrice)
            && !string.IsNullOrWhiteSpace(byokPrice.DisplayText))
        {
            return byokPrice.DisplayText!;
        }

        if (model.Billing != null)
        {
            return $"{model.Billing.Multiplier.ToString("0.##", CultureInfo.InvariantCulture)}x premium";
        }

        return "n/a";
    }

    private static string BuildModelDetailSummary(ModelInfo model, ModelSource source)
    {
        var details = new List<string>();

        var contextWindow = model.Capabilities?.Limits?.MaxContextWindowTokens;
        if (contextWindow is > 0)
        {
            details.Add($"context {FormatCompactTokenCount(contextWindow.Value)}");
        }

        var maxPrompt = model.Capabilities?.Limits?.MaxPromptTokens;
        if (maxPrompt is > 0)
        {
            details.Add($"prompt {FormatCompactTokenCount(maxPrompt.Value)}");
        }

        if (model.Capabilities?.Supports?.Vision == true)
        {
            details.Add("vision");
        }

        if (model.Capabilities?.Supports?.ReasoningEffort == true)
        {
            details.Add("reasoning");
        }

        if (!string.IsNullOrWhiteSpace(model.DefaultReasoningEffort))
        {
            details.Add($"default reasoning {model.DefaultReasoningEffort}");
        }

        if (source == ModelSource.GitHub && model.Billing?.Multiplier > 0)
        {
            details.Add($"multiplier {model.Billing.Multiplier:0.##}x");
        }

        return details.Count == 0 ? "No extra metadata available" : string.Join(" | ", details);
    }

    private static string FormatCompactTokenCount(int value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}M";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000d:0.#}k";
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private ModelInfo? GetSelectedModelInfo()
    {
        if (string.IsNullOrWhiteSpace(_selectedModel))
        {
            return null;
        }

        var selected = _availableModels.FirstOrDefault(model => model.Id.Equals(_selectedModel, StringComparison.OrdinalIgnoreCase));
        if (selected != null)
        {
            return selected;
        }

        return _availableModels.FirstOrDefault(model => model.Name.Equals(_selectedModel, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<(string Label, string Value)> GetSelectedModelDetails()
    {
        var model = GetSelectedModelInfo();
        if (model == null)
        {
            return [];
        }

        var source = _useByokOpenAi ? ModelSource.Byok : ModelSource.GitHub;
        var details = new List<(string Label, string Value)>
        {
            ("Provider", source == ModelSource.Byok ? "BYOK / OpenAI" : "GitHub Copilot")
        };

        var rateLabel = GetModelRateLabel(model, source);
        if (!rateLabel.Equals("n/a", StringComparison.OrdinalIgnoreCase))
        {
            details.Add((source == ModelSource.Byok ? "Pricing" : "Premium rate", rateLabel));
        }

        var contextWindow = model.Capabilities?.Limits?.MaxContextWindowTokens;
        if (contextWindow is > 0)
        {
            details.Add(("Context window", FormatCompactTokenCount(contextWindow.Value)));
        }

        var maxPrompt = model.Capabilities?.Limits?.MaxPromptTokens;
        if (maxPrompt is > 0)
        {
            details.Add(("Max prompt", FormatCompactTokenCount(maxPrompt.Value)));
        }

        var capabilities = new List<string>();
        if (model.Capabilities?.Supports?.Vision == true)
        {
            capabilities.Add("vision");
        }

        if (model.Capabilities?.Supports?.ReasoningEffort == true)
        {
            capabilities.Add("reasoning");
        }

        if (capabilities.Count > 0)
        {
            details.Add(("Capabilities", string.Join(", ", capabilities)));
        }

        if (model.SupportedReasoningEfforts is { Count: > 0 })
        {
            details.Add(("Reasoning efforts", string.Join(", ", model.SupportedReasoningEfforts)));
        }

        if (!string.IsNullOrWhiteSpace(model.DefaultReasoningEffort))
        {
            details.Add(("Default reasoning", model.DefaultReasoningEffort));
        }

        return details;
    }

    private async Task<IReadOnlyList<string>> TryGetCliModelIdsAsync(string cliPath)
    {
        try
        {
            var (command, args) = BuildCopilotCommand(cliPath, "--help");
            var helpResult = await ProcessRunnerResolver(command, args);
            if (helpResult.ExitCode != 0)
            {
                return [];
            }

            return ParseCliModelIds(helpResult.StdOut);
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ParseCliModelIds(string helpText)
    {
        if (string.IsNullOrWhiteSpace(helpText))
        {
            return [];
        }

        static void ExtractModelIds(string text, List<string> target)
        {
            foreach (Match match in CliModelIdRegex.Matches(text))
            {
                if (match.Groups.Count < 2)
                {
                    continue;
                }

                var value = match.Groups[1].Value;
                if (!target.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    target.Add(value);
                }
            }
        }

        var modelIds = new List<string>();

        var modelSectionStart = helpText.IndexOf("--model <model>", StringComparison.OrdinalIgnoreCase);
        if (modelSectionStart < 0)
        {
            ExtractModelIds(helpText, modelIds);
            return modelIds;
        }

        var modelSectionEnd = helpText.IndexOf("--no-alt-screen", modelSectionStart, StringComparison.OrdinalIgnoreCase);
        var modelSection = modelSectionEnd > modelSectionStart
            ? helpText[modelSectionStart..modelSectionEnd]
            : helpText[modelSectionStart..];

        ExtractModelIds(modelSection, modelIds);

        if (modelIds.Count == 0)
        {
            ExtractModelIds(helpText, modelIds);
        }

        return modelIds;
    }

    private async Task WarnIfPowerShellVersionIsOldAsync()
    {
        var warning = await DetectPowerShellVersionWarningAsync();
        if (!string.IsNullOrWhiteSpace(warning))
        {
            ConsoleUI.ShowWarning(warning);
        }
    }

    private static async Task<string?> DetectPowerShellVersionWarningAsync()
    {
        var (shell, versionText) = await GetPowerShellVersionTextAsync();
        if (string.IsNullOrWhiteSpace(versionText))
            return null;

        var majorVersion = ParsePowerShellMajorVersion(versionText);
        if (!majorVersion.HasValue || majorVersion.Value >= 7)
            return null;

        return $"Detected {shell} {versionText}. Copilot CLI on Windows requires PowerShell 6+, and TroubleScout recommends PowerShell 7+.";
    }

    private static async Task<(string Shell, string? VersionText)> GetPowerShellVersionTextAsync()
    {
        var pwshVersion = await ProcessRunnerResolver("pwsh", "--version");
        if (pwshVersion.ExitCode == 0)
        {
            return ("pwsh", TrimSingleLine(pwshVersion.StdOut));
        }

        var windowsPowerShellVersion = await ProcessRunnerResolver(
            "powershell",
            "-NoLogo -NoProfile -Command \"$PSVersionTable.PSVersion.ToString()\"");

        if (windowsPowerShellVersion.ExitCode == 0)
        {
            return ("powershell", TrimSingleLine(windowsPowerShellVersion.StdOut));
        }

        return (string.Empty, null);
    }

    private static int? ParsePowerShellMajorVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
            return null;

        var trimmed = versionText.Trim();
        var dotIndex = trimmed.IndexOf('.');
        var majorPart = dotIndex >= 0 ? trimmed[..dotIndex] : trimmed;
        return int.TryParse(majorPart, out var major) ? major : null;
    }

    private static int? ParseNodeMajorVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
            return null;

        var trimmed = versionText.Trim();
        if (trimmed.StartsWith('v'))
        {
            trimmed = trimmed[1..];
        }

        var dotIndex = trimmed.IndexOf('.');
        var majorPart = dotIndex >= 0 ? trimmed[..dotIndex] : trimmed;

        return int.TryParse(majorPart, out var major) ? major : null;
    }

    private static string TrimSingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Unknown error";

        var trimmed = text.Trim();
        var newlineIndex = trimmed.IndexOfAny(['\r', '\n']);
        return newlineIndex < 0 ? trimmed : trimmed[..newlineIndex].Trim();
    }

    private static async Task<IReadOnlyList<string>> RunSimpleStartupDiagnosticsAsync()
    {
        var diagnostics = new List<string>();
        var cliPath = CopilotCliPathResolver();
        var nodeRuntimeRequired = await CliPathRequiresNodeRuntimeAsync(cliPath);

        var (copilotCommand, copilotArguments) = BuildCopilotCommand(cliPath, "--version");
        var copilotVersion = await ProcessRunnerResolver(copilotCommand, copilotArguments);
        diagnostics.Add(FormatDiagnosticLine($"{copilotCommand} {copilotArguments}", copilotVersion));

        var nodeVersion = await ProcessRunnerResolver("node", "--version");
        diagnostics.Add(FormatDiagnosticLine("node --version", nodeVersion));
        if (!nodeRuntimeRequired && nodeVersion.ExitCode != 0)
        {
            diagnostics.Add("- Note: Node.js is only required for some Copilot CLI installations.");
        }

        return diagnostics;
    }

    private static string FormatDiagnosticLine(string command, (int ExitCode, string StdOut, string StdErr) result)
    {
        var output = TrimSingleLine(string.IsNullOrWhiteSpace(result.StdOut) ? result.StdErr : result.StdOut);
        return result.ExitCode == 0
            ? $"- {command}: {output}"
            : $"- {command}: failed ({output})";
    }

    private async Task ShowCopilotInitializationFailureAsync(
        string baseMessage,
        Exception? exception = null,
        bool includeDiagnostics = false)
    {
        var message = baseMessage;

        if (includeDiagnostics)
        {
            var diagnostics = await RunSimpleStartupDiagnosticsAsync();
            message += "\n\nStartup diagnostics:\n" + string.Join("\n", diagnostics);
        }

        if (_debugMode && exception != null)
        {
            message += "\n\nTechnical details:\n" + exception;
        }

        ConsoleUI.ShowError("Initialization Failed", message);
    }

    private static string BuildProtocolMismatchMessage(CopilotPrerequisiteReport report, bool includeTechnicalDetails)
    {
        var message = "Copilot SDK protocol version mismatch detected.\n\n" +
               "Ensure Copilot CLI prerequisites are installed and compatible:\n" +
               $"  1. Install Node.js {MinSupportedNodeMajorVersion}+ (LTS): winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements\n" +
               "  2. Restart your terminal\n" +
                             $"  3. Install or update Copilot CLI: {CopilotCliInstallUrl}\n" +
               "  4. copilot login\n\n" +
               "References:\n" +
               $"- {CopilotCliRepoUrl}\n" +
                             $"- {CopilotCliInstallUrl}";

        if (!report.IsReady)
        {
            var diagnosticsText = includeTechnicalDetails
                ? report.ToDisplayText(includeWarnings: true)
                : string.Join(Environment.NewLine, report.Issues.Select(issue => $"- {issue.Title}"));
            message += "\n\nPrerequisite diagnostics:\n" + diagnosticsText;
        }

        return message;
    }

    private static string BuildActionableInitializationMessage(Exception ex, CopilotPrerequisiteReport report, bool includeTechnicalDetails)
    {
        if (ex is InvalidOperationException invalidOp &&
            invalidOp.Message.Contains("protocol version mismatch", StringComparison.OrdinalIgnoreCase))
        {
            return BuildProtocolMismatchMessage(report, includeTechnicalDetails);
        }

        if (!report.IsReady)
        {
            var diagnosticsText = includeTechnicalDetails
                ? report.ToDisplayText(includeWarnings: true)
                : string.Join(Environment.NewLine, report.Issues.Select(issue => $"- {issue.Title}"));

            return "Copilot CLI prerequisites are not ready.\n\n" +
                   "Prerequisite diagnostics:\n" +
                   diagnosticsText;
        }

        var message = ex.Message;
        if (message.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            return "Copilot CLI is installed but not authenticated.\n\n" +
                   "To continue:\n" +
                   "  1. Run: copilot login\n" +
                   "  2. Re-run TroubleScout";
        }

        if (message.Contains("failed to start cli", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cli process exited", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("communication error with copilot cli", StringComparison.OrdinalIgnoreCase))
        {
            var startupFailureMessage = "The Copilot CLI failed during startup.\n\n" +
                                      "Try:\n" +
                                      "  - copilot --version\n" +
                                      "  - copilot login\n" +
                                      $"  - Install/update Copilot CLI: {CopilotCliInstallUrl}\n" +
                                      "  - Re-run TroubleScout";

            if (includeTechnicalDetails)
            {
                startupFailureMessage += $"\n\nTechnical details: {message}";
            }

            return startupFailureMessage;
        }

        if (message.Contains("node", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ENOENT", StringComparison.OrdinalIgnoreCase))
        {
            return "The Copilot CLI runtime is unavailable.\n\n" +
                   "Install/update prerequisites:\n" +
                   $"  1. Install Node.js {MinSupportedNodeMajorVersion}+ (LTS): winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements\n" +
                   "  2. Restart your terminal\n" +
                   $"  3. Install/update Copilot CLI: {CopilotCliInstallUrl}\n" +
                   "  4. copilot login";
        }

        var result = "TroubleScout could not initialize the Copilot session.\n\n" +
                     "Try:\n" +
                     "  - copilot --version\n" +
                     "  - copilot login\n" +
                     $"  - winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements\n" +
                     $"  - Install/update Copilot CLI: {CopilotCliInstallUrl}";

        if (includeTechnicalDetails)
        {
            result += $"\n\nTechnical details: {message}";
        }

        return result;
    }

    /// <summary>
    /// Send a message and process the response with streaming
    /// </summary>
    public async Task<bool> SendMessageAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        if (_copilotSession == null)
        {
            ConsoleUI.ShowError("Not Initialized", "Session not initialized. Call InitializeAsync first.");
            return false;
        }

        IDisposable? subscription = null;
        LiveThinkingIndicator? thinkingIndicator = null;
        CancellationTokenSource? watchdogCts = null;
        Task? watchdogTask = null;
        try
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hasError = false;
            var wasCancelled = false;
            var lastEventTimeTicks = DateTime.UtcNow.Ticks;

            // Register cancellation callback to unblock the done TCS
            using var cancelReg = cancellationToken.Register(() =>
            {
                wasCancelled = true;
                watchdogCts?.Cancel();
                done.TrySetResult(false);
            });
            var hasStartedStreaming = false;
            var hasStartedReasoning = false;
            var pendingStreamLineBreak = false;
            var currentStreamMessageId = string.Empty;
            var processedDeltaIds = new HashSet<string>();
            var responseBuffer = new StringBuilder();
            int promptIndex;
            lock (_reportLock)
            {
                promptIndex = _lastPromptIndex;
            }
            
            // Create a live thinking indicator (manually disposed before recursive calls)
            thinkingIndicator = ConsoleUI.CreateLiveThinkingIndicator();
            thinkingIndicator.Start();
            watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            watchdogTask = RunActivityWatchdogAsync(thinkingIndicator, () => new DateTime(Interlocked.Read(ref lastEventTimeTicks), DateTimeKind.Utc), watchdogCts.Token);

            // Subscribe to session events for streaming (manually disposed before recursive calls)
            subscription = _copilotSession.On(evt =>
            {
                Interlocked.Exchange(ref lastEventTimeTicks, DateTime.UtcNow.Ticks);
                CaptureCapabilityUsage(evt);

                switch (evt)
                {
                    case SessionStartEvent startEvt:
                        _selectedModel = startEvt.Data.SelectedModel;
                        _copilotVersion = startEvt.Data.CopilotVersion;
                        break;

                    case SessionModelChangeEvent modelChangeEvt:
                        _selectedModel = modelChangeEvt.Data.NewModel;
                        break;

                    case AssistantTurnStartEvent:
                        // AI has started processing
                        if (hasStartedStreaming)
                        {
                            pendingStreamLineBreak = true;
                        }
                        thinkingIndicator.UpdateStatus("Analyzing");
                        break;
                    
                    case AssistantReasoningDeltaEvent reasoningDelta:
                        if (hasStartedStreaming)
                            break;

                        var reasoningDeltaText = reasoningDelta.Data?.DeltaContent ?? "";
                        if (!string.IsNullOrEmpty(reasoningDeltaText))
                        {
                            if (!hasStartedReasoning)
                            {
                                hasStartedReasoning = true;
                                thinkingIndicator?.StopForResponse();
                                ConsoleUI.StartReasoningBlock();
                            }
                            ConsoleUI.WriteReasoningText(reasoningDeltaText);
                        }
                        break;

                    case AssistantReasoningEvent reasoning:
                        if (hasStartedStreaming)
                            break;

                        // Fallback for non-streaming reasoning (full content)
                        var reasoningText = reasoning.Data?.Content ?? "";
                        if (!string.IsNullOrEmpty(reasoningText))
                        {
                            if (!hasStartedReasoning)
                            {
                                hasStartedReasoning = true;
                                thinkingIndicator?.StopForResponse();
                                ConsoleUI.StartReasoningBlock();
                            }
                            ConsoleUI.WriteReasoningText(reasoningText);
                        }
                        break;
                    
                    case ToolExecutionStartEvent toolStart:
                        // Show which tool is being executed
                        if (hasStartedStreaming)
                        {
                            pendingStreamLineBreak = true;
                        }
                        var toolName = toolStart.Data?.ToolName ?? "tool";
                        var mcpServer = ReadStringProperty(toolStart.Data, "McpServerName", "MCPServerName", "ServerName");
                        string toolDisplay;
                        if (!string.IsNullOrWhiteSpace(mcpServer))
                        {
                            toolDisplay = $"MCP [{Markup.Escape(mcpServer)}]: {Markup.Escape(toolName)}";
                        }
                        else if (ToolDescriptions.TryGetValue(toolName, out var desc))
                        {
                            toolDisplay = desc;
                        }
                        else
                        {
                            toolDisplay = $"Using {toolName}";
                        }
                        thinkingIndicator.ShowToolExecution(toolDisplay);
                        RecordMcpToolAction(toolStart);
                        _toolInvocationCount++;
                        break;
                    
                    case ToolExecutionCompleteEvent:
                        // Tool finished, back to thinking
                        if (hasStartedStreaming)
                        {
                            pendingStreamLineBreak = true;
                        }
                        thinkingIndicator.UpdateStatus("Processing results");
                        break;
                    
                    case AssistantMessageDeltaEvent delta:
                        // Skip if we've already processed this event (deduplicate)
                        if (!processedDeltaIds.Add(delta.Id.ToString()))
                            break;

                        var deltaMessageId = ReadStringProperty(delta.Data, "MessageId", "Id");
                        if (!string.IsNullOrWhiteSpace(deltaMessageId))
                        {
                            if (!string.IsNullOrWhiteSpace(currentStreamMessageId)
                                && !currentStreamMessageId.Equals(deltaMessageId, StringComparison.Ordinal)
                                && responseBuffer.Length > 0)
                            {
                                pendingStreamLineBreak = true;
                            }

                            currentStreamMessageId = deltaMessageId;
                        }
                        
                        // First streaming chunk - stop the spinner and start response
                        if (!hasStartedStreaming)
                        {
                            hasStartedStreaming = true;
                            if (hasStartedReasoning)
                            {
                                ConsoleUI.EndReasoningBlock();
                            }
                            thinkingIndicator.StopForResponse();
                            ConsoleUI.StartAIResponse();
                        }
                        // Streaming message chunk - print incrementally
                        var deltaText = delta.Data?.DeltaContent ?? "";
                        if (pendingStreamLineBreak && responseBuffer.Length > 0)
                        {
                            ConsoleUI.WriteAIResponse(Environment.NewLine);
                            responseBuffer.AppendLine();
                            pendingStreamLineBreak = false;
                        }
                        responseBuffer.Append(deltaText);
                        ConsoleUI.WriteAIResponse(deltaText);
                        break;
                    
                    case AssistantMessageEvent msg:
                        // Final message received (non-streaming fallback)
                        if (!hasStartedStreaming && !string.IsNullOrEmpty(msg.Data?.Content))
                        {
                            if (hasStartedReasoning)
                            {
                                ConsoleUI.EndReasoningBlock();
                            }
                            thinkingIndicator.StopForResponse();
                            ConsoleUI.StartAIResponse();
                            ConsoleUI.WriteAIResponse(msg.Data.Content);
                            responseBuffer.Append(msg.Data.Content);
                            hasStartedStreaming = true;
                        }
                        break;
                    
                    case SessionErrorEvent errorEvent:
                        thinkingIndicator.StopForResponse();
                        ConsoleUI.EndAIResponse();
                        ConsoleUI.ShowError("Session Error", errorEvent.Data?.Message ?? "Unknown error");
                        hasError = true;
                        done.TrySetResult(false);
                        break;
                    
                    case SessionIdleEvent:
                        // Session finished processing
                        done.TrySetResult(true);
                        break;
                    case AssistantUsageEvent usageEvt:
                        CaptureUsageMetrics(usageEvt);
                        if (!string.IsNullOrEmpty(usageEvt.Data?.Model))
                        {
                            _selectedModel = usageEvt.Data.Model;
                        }
                        break;
                }
            });

            var prompt = BuildPromptForExecutionSafety(userMessage);

            // Send the message (pass cancellationToken so the SDK can cancel the in-flight RPC)
            await _copilotSession.SendAsync(new MessageOptions { Prompt = prompt }, cancellationToken);
            
            // Wait for completion
            await done.Task;
            watchdogCts.Cancel();
            try { await watchdogTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { /* ignore */ }
            watchdogCts.Dispose();
            watchdogCts = null;
            watchdogTask = null;
            
            // Explicitly dispose subscription BEFORE processing approvals
            // This prevents duplicate event handling when SendMessageAsync is called recursively
            subscription.Dispose();
            subscription = null;

            if (hasStartedStreaming)
            {
                ConsoleUI.EndAIResponse();
            }

            // Show compact status bar after response completes
            if (hasStartedStreaming && !hasError && !wasCancelled)
            {
                ConsoleUI.WriteStatusBar(BuildStatusBarInfo());
            }

            // Handle cancellation
            if (wasCancelled)
            {
                thinkingIndicator.Dispose();
                thinkingIndicator = null;
                ConsoleUI.ShowCancelled();
                return false;
            }

            SetPromptReply(promptIndex, responseBuffer.ToString());
            
            // Dispose thinking indicator before processing approvals
            thinkingIndicator.Dispose();
            thinkingIndicator = null;

            // Handle any pending approval commands (may call SendMessageAsync recursively)
            if (!hasError && !wasCancelled)
            {
                await ProcessPendingApprovalsAsync();
            }

            return !hasError && !wasCancelled;
        }
        catch (OperationCanceledException)
        {
            watchdogCts?.Cancel();
            ConsoleUI.EndAIResponse();
            ConsoleUI.ShowCancelled();
            return false;
        }
        catch (Exception ex)
        {
            watchdogCts?.Cancel();
            ConsoleUI.EndAIResponse();
            ConsoleUI.ShowError("Error", ex.Message);
            return false;
        }
        finally
        {
            watchdogCts?.Cancel();
            watchdogCts?.Dispose();
            subscription?.Dispose();
            thinkingIndicator?.Dispose();
        }
    }

    internal static async Task RunActivityWatchdogAsync(
        LiveThinkingIndicator indicator,
        Func<DateTime> getLastEventTime,
        CancellationToken cancellationToken)
    {
        const int checkIntervalMs = 2000;
        string? lastWatchdogStatus = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(checkIntervalMs, cancellationToken);

                var idleSeconds = (DateTime.UtcNow - getLastEventTime()).TotalSeconds;
                var nextWatchdogStatus = GetActivityWatchdogStatus(idleSeconds);

                if (!string.Equals(nextWatchdogStatus, lastWatchdogStatus, StringComparison.Ordinal))
                {
                    if (nextWatchdogStatus is not null)
                    {
                        indicator.UpdateStatus(nextWatchdogStatus);
                    }

                    lastWatchdogStatus = nextWatchdogStatus;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    internal static string? GetActivityWatchdogStatus(double idleSeconds)
    {
        const int slowWarningSeconds = 15;
        const int staleWarningSeconds = 30;

        if (idleSeconds >= staleWarningSeconds)
        {
            return "Connection seems slow";
        }

        if (idleSeconds >= slowWarningSeconds)
        {
            return "Waiting for response";
        }

        return null;
    }

    private static string BuildPromptForExecutionSafety(string userMessage)
    {
        var promptBuilder = new StringBuilder(userMessage);
        promptBuilder.Append("\n\nResponse formatting requirement: Always reply in Markdown with short sections, bullet points, and blank lines between sections. ");
        promptBuilder.Append("For tabular data, use compact Markdown tables (pipe syntax), avoid ASCII-art aligned tables, and if width is large use a concise bullet list instead.");

        if (MutatingIntentRegex.IsMatch(userMessage))
        {
            promptBuilder.Append("\n\nExecution safety requirement: If this request can modify system state, you must call run_powershell with the exact command. ");
            promptBuilder.Append("For PowerShell cmdlets that support confirmation prompts, include -Confirm:$false when appropriate. ");
            promptBuilder.Append("Do not claim any action was executed unless tool output confirms execution.");
        }

        return promptBuilder.ToString();
    }

    /// <summary>
    /// Process any commands that require user approval
    /// </summary>
    private async Task ProcessPendingApprovalsAsync()
    {
        var pending = _diagnosticTools.PendingCommands;
        if (pending.Count == 0) return;

        var commands = pending.Select(p => (p.Command, p.Reason)).ToList();
        
        if (commands.Count == 1)
        {
            var cmd = commands[0];
            var approval = ConsoleUI.PromptCommandApproval(cmd.Command, cmd.Reason, pending[0].Intent);
            if (approval == ApprovalResult.Approved)
            {
                ConsoleUI.ShowInfo($"Executing: {cmd.Command}");
                var result = await _diagnosticTools.ExecuteApprovedCommandAsync(pending[0]);
                ConsoleUI.ShowSuccess("Command executed");
                
                // Feed result back to the AI
                await SendMessageAsync($"The approved command '{cmd.Command}' has been executed. Result:\n{result}\n\nPlease continue your analysis with this information.");
            }
            else
            {
                ConsoleUI.ShowWarning("Command skipped by user");
                _diagnosticTools.LogDeniedCommand(pending[0]);
                _diagnosticTools.ClearPendingCommands();
            }
        }
        else
        {
            var approved = ConsoleUI.PromptBatchApproval(commands);

            var pendingSnapshot = pending.ToList();
            foreach (var index in approved)
            {
                var cmd = pendingSnapshot[index - 1];
                ConsoleUI.ShowInfo($"Executing: {cmd.Command}");
                var result = await _diagnosticTools.ExecuteApprovedCommandAsync(cmd);
                ConsoleUI.ShowSuccess("Command executed");
            }

            var approvedSet = new HashSet<int>(approved);
            for (var i = 0; i < pendingSnapshot.Count; i++)
            {
                if (!approvedSet.Contains(i + 1))
                {
                    _diagnosticTools.LogDeniedCommand(pendingSnapshot[i]);
                }
            }

            _diagnosticTools.ClearPendingCommands();

            if (approved.Count > 0)
            {
                await SendMessageAsync("The approved commands have been executed. Please continue your analysis.");
            }
        }
    }

    /// <summary>
    /// Callback for command approval prompts
    /// </summary>
    private Task<bool> PromptApprovalAsync(string command, string reason)
    {
        return Task.FromResult(ConsoleUI.PromptCommandApproval(command, reason) == ApprovalResult.Approved);
    }

    private static void SaveModelAndProviderState(string model, bool useByokOpenAi)
    {
        var settings = AppSettingsStore.Load();
        settings.LastModel = model;
        settings.UseByokOpenAi = useByokOpenAi;
        AppSettingsStore.Save(settings);
    }

    private static void SaveByokSettings(bool enabled, string? baseUrl, string? apiKey)
    {
        var settings = AppSettingsStore.Load();
        settings.UseByokOpenAi = enabled;
        settings.ByokOpenAiBaseUrl = enabled ? baseUrl : null;
        settings.ByokOpenAiApiKey = enabled ? apiKey : null;
        AppSettingsStore.Save(settings);
    }

    private void ApplySafeCommandsToAllExecutors(IReadOnlyList<string>? safeCommands)
    {
        _configuredSafeCommands = safeCommands?.Where(command => !string.IsNullOrWhiteSpace(command))
            .Select(command => command.Trim())
            .ToList();

        _executor.SetCustomSafeCommands(_configuredSafeCommands);

        foreach (var executor in _additionalExecutors.Values)
        {
            executor.SetCustomSafeCommands(_configuredSafeCommands);
        }
    }

    private void ReloadSafeCommandsFromSettings()
    {
        var settings = AppSettingsStore.Load();
        ApplySystemPromptSettings(settings.SystemPromptOverrides, settings.SystemPromptAppend);
        ApplySafeCommandsToAllExecutors(settings.SafeCommands);
        _systemMessageConfig = CreateSystemMessage(_targetServer, _additionalExecutors.Keys.ToList());
    }

    private void ApplySystemPromptSettings(IReadOnlyDictionary<string, string>? overrides, string? append)
    {
        _configuredSystemPromptOverrides = overrides?
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
            .ToDictionary(entry => entry.Key.Trim(), entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        _configuredSystemPromptAppend = string.IsNullOrWhiteSpace(append) ? null : append;
    }

    private static async Task<string?> TryOpenSettingsEditorAsync(string settingsPath)
    {
        var editorCommand = Environment.GetEnvironmentVariable("EDITOR");
        if (string.IsNullOrWhiteSpace(editorCommand))
        {
            editorCommand = Environment.GetEnvironmentVariable("VISUAL");
        }

        if (string.IsNullOrWhiteSpace(editorCommand) && OperatingSystem.IsWindows())
        {
            editorCommand = "notepad";
        }

        if (string.IsNullOrWhiteSpace(editorCommand))
        {
            return "No editor is configured. Set EDITOR or VISUAL to edit settings automatically.";
        }

        try
        {
            var (fileName, arguments) = ParseCommandWithArguments(editorCommand, settingsPath);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                CreateNoWindow = false
            });

            if (process == null)
            {
                return $"Could not start editor '{editorCommand}'.";
            }

            await process.WaitForExitAsync();
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not open settings editor: {TrimSingleLine(ex.Message)}";
        }
    }

    private static (string FileName, string Arguments) ParseCommandWithArguments(string command, string settingsPath)
    {
        var trimmedCommand = command.Trim();
        string fileName;
        string arguments;

        if (trimmedCommand.StartsWith('"'))
        {
            var closingQuote = trimmedCommand.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                fileName = trimmedCommand[1..closingQuote];
                arguments = trimmedCommand[(closingQuote + 1)..].Trim();
            }
            else
            {
                fileName = trimmedCommand.Trim('"');
                arguments = string.Empty;
            }
        }
        else
        {
            var firstSpace = trimmedCommand.IndexOf(' ');
            if (firstSpace >= 0)
            {
                fileName = trimmedCommand[..firstSpace];
                arguments = trimmedCommand[(firstSpace + 1)..].Trim();
            }
            else
            {
                fileName = trimmedCommand;
                arguments = string.Empty;
            }
        }

        var settingsArgument = $"\"{settingsPath}\"";
        return (fileName, string.IsNullOrWhiteSpace(arguments) ? settingsArgument : $"{arguments} {settingsArgument}");
    }

    /// <summary>
    /// Run the interactive session loop
    /// </summary>
    public async Task RunInteractiveLoopAsync()
    {
        ConsoleUI.SetExecutionMode(_executionMode);

        while (true)
        {
            var input = ConsoleUI.GetUserInput(SlashCommands).Trim();

            if (!string.IsNullOrEmpty(input))
                ConsoleUI.AddPromptHistory(input);

            if (string.IsNullOrWhiteSpace(input))
                continue;

            // Handle commands
            var lowerInput = input.ToLowerInvariant();
            var firstToken = GetFirstInputToken(lowerInput);
            
            if (firstToken is "/exit" or "/quit" || IsBareExitCommand(lowerInput))
            {
                ConsoleUI.ShowInfo("Ending session. Goodbye!");
                break;
            }

            if (firstToken == "/clear")
            {
                var resetSucceeded = await ResetConversationAsync();
                if (resetSucceeded)
                {
                    Console.Clear();
                    ConsoleUI.ShowBanner();
                    ConsoleUI.ShowStatusPanel(EffectiveTargetServer, EffectiveConnectionMode, _copilotSession != null, SelectedModel, _executionMode, GetStatusFields(), GetAdditionalTargetsForDisplay(), DefaultSessionTarget);
                    ConsoleUI.ShowSuccess($"Started new session: {_sessionId}");
                    ConsoleUI.ShowWelcomeMessage();
                }
                else
                {
                    ConsoleUI.ShowWarning("Could not start a new session.");
                }

                continue;
            }

            if (firstToken == "/status")
            {
                ConsoleUI.ShowStatusPanel(EffectiveTargetServer, EffectiveConnectionMode, _copilotSession != null, SelectedModel, _executionMode, GetStatusFields(), GetAdditionalTargetsForDisplay(), DefaultSessionTarget);
                continue;
            }

            if (firstToken == "/settings")
            {
                // Ensure the settings file exists before launching the editor on first use.
                _ = AppSettingsStore.Load();
                ConsoleUI.ShowInfo($"Settings file: {AppSettingsStore.SettingsPath}");
                var editorError = await TryOpenSettingsEditorAsync(AppSettingsStore.SettingsPath);
                if (!string.IsNullOrWhiteSpace(editorError))
                {
                    ConsoleUI.ShowWarning(editorError);
                }

                ReloadSafeCommandsFromSettings();
                if (_copilotClient != null && _copilotSession != null)
                {
                    await _copilotSession.DisposeAsync();
                    _copilotSession = null;
                    await CreateCopilotSessionAsync(
                        string.IsNullOrWhiteSpace(_selectedModel) ? null : _selectedModel,
                        null);
                }

                ConsoleUI.ShowSuccess("Settings reloaded. Safe command patterns and system prompt settings have been applied.");
                continue;
            }

            if (firstToken == "/login")
            {
                var loginSucceeded = await ConsoleUI.RunWithSpinnerAsync("Running Copilot login...", async updateStatus =>
                {
                    return await LoginAndCreateGitHubSessionAsync(updateStatus);
                });

                if (loginSucceeded)
                {
                    ConsoleUI.ShowSuccess("GitHub Copilot login completed and session is ready.");
                }

                continue;
            }

            if (IsSlashCommandInvocation(lowerInput, "/byok"))
            {
                var byokParts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (byokParts.Length > 1 &&
                    (byokParts[1].Equals("clear", StringComparison.OrdinalIgnoreCase)
                     || byokParts[1].Equals("off", StringComparison.OrdinalIgnoreCase)
                     || byokParts[1].Equals("disable", StringComparison.OrdinalIgnoreCase)))
                {
                    SaveByokSettings(false, null, null);
                    // Also update in-memory state so a subsequent /model switch doesn't re-save BYOK=true
                    _useByokOpenAi = false;
                    _byokOpenAiApiKey = null;
                    ConsoleUI.ShowSuccess("Saved BYOK settings cleared for this profile.");
                    ConsoleUI.ShowInfo("Current session provider remains unchanged until you switch model/provider or restart.");
                    ConsoleUI.ShowInfo($"The {OpenAiApiKeyEnvironmentVariable} environment variable (if set) is unchanged.");
                    continue;
                }

                string? apiKey = null;
                var byokBaseUrl = _byokOpenAiBaseUrl;
                var byokModel = _selectedModel ?? _requestedModel;

                if (byokParts.Length == 1)
                {
                    ConsoleUI.ShowInfo($"Enter OpenAI-compatible base URL (default: {_byokOpenAiBaseUrl})");
                    var baseUrlInput = ConsoleUI.GetUserInput().Trim();
                    if (!string.IsNullOrWhiteSpace(baseUrlInput))
                    {
                        byokBaseUrl = baseUrlInput;
                    }

                    ConsoleUI.ShowInfo($"Enter API key, or type 'env' to use {OpenAiApiKeyEnvironmentVariable}.");
                    var apiKeyInput = ConsoleUI.GetUserInput().Trim();
                    if (apiKeyInput.Equals("env", StringComparison.OrdinalIgnoreCase))
                    {
                        apiKey = Environment.GetEnvironmentVariable(OpenAiApiKeyEnvironmentVariable);
                    }
                    else
                    {
                        apiKey = apiKeyInput;
                    }
                }
                else
                {
                    var sourceArg = byokParts[1];

                    if (sourceArg.Equals("env", StringComparison.OrdinalIgnoreCase))
                    {
                        apiKey = Environment.GetEnvironmentVariable(OpenAiApiKeyEnvironmentVariable);
                        if (byokParts.Length > 2)
                        {
                            if (LooksLikeUrl(byokParts[2]))
                            {
                                byokBaseUrl = byokParts[2];
                                if (byokParts.Length > 3)
                                {
                                    byokModel = byokParts[3];
                                }
                            }
                            else
                            {
                                byokModel = byokParts[2];
                            }
                        }
                    }
                    else
                    {
                        if (byokParts.Length > 2 && LooksLikeUrl(byokParts[2]))
                        {
                            apiKey = sourceArg;
                            byokBaseUrl = byokParts[2];
                            if (byokParts.Length > 3)
                            {
                                byokModel = byokParts[3];
                            }
                        }
                        else if (LooksLikeUrl(sourceArg))
                        {
                            byokBaseUrl = sourceArg;
                            if (byokParts.Length > 2)
                            {
                                apiKey = byokParts[2];
                            }
                            if (byokParts.Length > 3)
                            {
                                byokModel = byokParts[3];
                            }
                        }
                        else
                        {
                            apiKey = sourceArg;
                            if (byokParts.Length > 2)
                            {
                                byokModel = byokParts[2];
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    ConsoleUI.ShowWarning($"No API key was provided. Set {OpenAiApiKeyEnvironmentVariable} or pass it as /byok <api-key> [base-url] [model].");
                    ConsoleUI.ShowInfo("Examples:");
                    ConsoleUI.ShowInfo("  /byok env https://api.openai.com/v1");
                    ConsoleUI.ShowInfo("  /byok sk-... https://aigw.example.org");
                    continue;
                }

                if (!LooksLikeUrl(byokBaseUrl))
                {
                    ConsoleUI.ShowWarning("Base URL is invalid. Example: https://api.openai.com/v1");
                    continue;
                }

                var byokReady = await ConfigureByokOpenAiAsync(byokBaseUrl, apiKey, byokModel, updateStatus: null);

                if (byokReady)
                {
                    ConsoleUI.ShowModelSelectionSummary(SelectedModel, GetSelectedModelDetails());
                }

                continue;
            }

            if (firstToken == "/help")
            {
                ConsoleUI.ShowHelp();
                continue;
            }

            if (firstToken == "/history")
            {
                ConsoleUI.ShowCommandHistory(_executor.GetCommandHistory());
                continue;
            }

            if (firstToken == "/report")
            {
                GenerateAndOpenReport();
                continue;
            }

            if (firstToken == "/capabilities")
            {
                ConsoleUI.ShowStatusPanel(EffectiveTargetServer, EffectiveConnectionMode, _copilotSession != null, SelectedModel, _executionMode, GetStatusFields(), GetAdditionalTargetsForDisplay(), DefaultSessionTarget);
                continue;
            }

            if (IsSlashCommandInvocation(lowerInput, "/mode"))
            {
                var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    ConsoleUI.ShowInfo($"Current mode: {_executionMode.ToCliValue()}");
                    ConsoleUI.ShowInfo("Usage: /mode <safe|yolo>");
                }
                else if (!ExecutionModeParser.TryParse(parts[1], out var requestedMode))
                {
                    ConsoleUI.ShowWarning("Invalid mode. Use: safe or yolo.");
                }
                else
                {
                    SetExecutionMode(requestedMode);
                    ConsoleUI.SetExecutionMode(_executionMode);
                    ConsoleUI.ShowSuccess($"Execution mode set to: {_executionMode.ToCliValue()}");
                    ConsoleUI.ShowStatusPanel(EffectiveTargetServer, EffectiveConnectionMode, _copilotSession != null, SelectedModel, _executionMode, GetStatusFields(), GetAdditionalTargetsForDisplay(), DefaultSessionTarget);
                }

                continue;
            }

            if (firstToken == "/model")
            {
                if (_copilotClient != null)
                {
                    try
                    {
                        await RefreshAvailableModelsAsync();

                        if (_availableModels.Count == 0)
                        {
                            ConsoleUI.ShowWarning("No models available. Authenticate with /login and/or configure BYOK with /byok.");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_debugMode)
                        {
                            ConsoleUI.ShowWarning($"Could not refresh model list: {TrimSingleLine(ex.Message)}");
                        }
                    }
                }

                if (_availableModels.Count == 0)
                {
                    ConsoleUI.ShowWarning("No models are available yet. Authenticate GitHub Copilot or configure BYOK, then try /model again.");
                    continue;
                }

                var selectionEntries = GetModelSelectionEntries();
                if (selectionEntries.Count == 0)
                {
                    ConsoleUI.ShowWarning("No connected provider models are available. Authenticate GitHub Copilot or configure BYOK first.");
                    continue;
                }

                var selectedEntry = ConsoleUI.PromptModelSelection(SelectedModel, selectionEntries);
                if (selectedEntry == null)
                {
                    ConsoleUI.ShowInfo($"Keeping current model: {SelectedModel}");
                    continue;
                }

                if (!IsCurrentModelAndSource(selectedEntry))
                {
                    var displayName = selectedEntry.DisplayName;
                    var success = await ConsoleUI.RunWithSpinnerAsync($"Switching to {displayName}...", async updateStatus =>
                    {
                        return await ChangeModelAsync(selectedEntry, updateStatus);
                    });
                    
                    if (success)
                    {
                        ConsoleUI.ShowModelSelectionSummary(SelectedModel, GetSelectedModelDetails());
                    }
                }
                continue;
            }

            if (IsSlashCommandInvocation(lowerInput, "/server"))
            {
                var parts = input.Split(new char[]{' ', ','}, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    ConsoleUI.ShowWarning("Usage: /server <server1>[,server2,...]");
                }
                else
                {
                    var primaryServer = parts[1];
                    var additionalServers = parts.Skip(2).ToList();

                    var success = await ConsoleUI.RunWithSpinnerAsync($"Connecting to {primaryServer}...", async updateStatus =>
                    {
                        return await ReconnectAsync(primaryServer, updateStatus);
                    });

                    if (success)
                    {
                        ConsoleUI.ShowSuccess($"Connected to {primaryServer}");

                        foreach (var srv in additionalServers)
                        {
                            // Approval must happen OUTSIDE the spinner (Spectre exclusivity constraint)
                            if (_executionMode == ExecutionMode.Safe)
                            {
                                var approval = ConsoleUI.PromptCommandApproval(
                                    $"New-PSSession -ComputerName '{srv}'",
                                    $"TroubleScout wants to establish a direct PowerShell session to {srv}");
                                if (approval != ApprovalResult.Approved)
                                {
                                    ConsoleUI.ShowWarning($"Connection to {srv} was denied.");
                                    continue;
                                }
                            }

                            var addSuccess = await ConsoleUI.RunWithSpinnerAsync($"Connecting to {srv}...", async _ =>
                            {
                                var (s, e) = await ConnectAdditionalServerAsync(srv, skipApproval: true);
                                if (!s) ConsoleUI.ShowWarning($"Could not connect to {srv}: {e}");
                                return s;
                            });
                        }

                        _systemMessageConfig = CreateSystemMessage(_targetServer, _additionalExecutors.Keys.ToList());

                        // Recreate the Copilot session so the updated system message (with connected sessions) takes effect
                        if (_copilotClient != null && _copilotSession != null && additionalServers.Count > 0)
                        {
                            await _copilotSession.DisposeAsync();
                            _copilotSession = null;
                            await CreateCopilotSessionAsync(
                                string.IsNullOrWhiteSpace(_selectedModel) ? null : _selectedModel,
                                null);
                        }

                        ConsoleUI.ShowStatusPanel(EffectiveTargetServer, EffectiveConnectionMode, _copilotSession != null, SelectedModel, _executionMode, GetStatusFields(), GetAdditionalTargetsForDisplay(), DefaultSessionTarget);
                    }
                }
                continue;
            }

            if (IsSlashCommandInvocation(lowerInput, "/jea"))
            {
                var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var serverName = parts.Length > 1 ? parts[1] : null;
                var configurationName = parts.Length > 2 ? string.Join(' ', parts.Skip(2)) : null;

                if (string.IsNullOrWhiteSpace(serverName))
                {
                    ConsoleUI.ShowInfo("Enter the server name for the JEA session:");
                    serverName = ConsoleUI.GetUserInput().Trim();
                    if (string.IsNullOrWhiteSpace(serverName))
                    {
                        ConsoleUI.ShowWarning("Server name cannot be empty.");
                        continue;
                    }
                }

                if (string.IsNullOrWhiteSpace(configurationName))
                {
                    ConsoleUI.ShowInfo("Enter the JEA configuration name:");
                    configurationName = ConsoleUI.GetUserInput().Trim();
                    if (string.IsNullOrWhiteSpace(configurationName))
                    {
                        ConsoleUI.ShowWarning("Configuration name cannot be empty.");
                        continue;
                    }
                }

                if (parts.Length < 3)
                {
                    ConsoleUI.ShowInfo("Example: /jea server1 JEA-Admins");
                }

                var success = await ConsoleUI.RunWithSpinnerAsync(
                    $"Connecting to JEA endpoint {configurationName} on {serverName}...",
                    async _ =>
                    {
                        var (connected, error) = await ConnectJeaServerAsync(serverName, configurationName, skipApproval: true);
                        if (!connected)
                        {
                            ConsoleUI.ShowWarning(error ?? $"Could not connect to JEA endpoint {configurationName} on {serverName}.");
                        }

                        return connected;
                    });

                if (success)
                {
                    if (_additionalExecutors.TryGetValue(serverName, out var executor) && executor.JeaAllowedCommands is { Count: > 0 })
                    {
                        ConsoleUI.ShowSuccess($"Connected to JEA endpoint '{configurationName}' on {serverName}");
                        AnsiConsole.MarkupLine($"[grey]Discovered commands for {Markup.Escape(serverName)} ({Markup.Escape(configurationName)}):[/]");
                        foreach (var commandName in executor.JeaAllowedCommands.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                        {
                            AnsiConsole.MarkupLine($"  [grey]-[/] {Markup.Escape(commandName)}");
                        }
                    }

                    _systemMessageConfig = CreateSystemMessage(_targetServer, _additionalExecutors.Keys.ToList());

                    if (_copilotClient != null && _copilotSession != null)
                    {
                        await _copilotSession.DisposeAsync();
                        _copilotSession = null;
                        await CreateCopilotSessionAsync(
                            string.IsNullOrWhiteSpace(_selectedModel) ? null : _selectedModel,
                            null);
                    }

                    ConsoleUI.ShowStatusPanel(EffectiveTargetServer, EffectiveConnectionMode, _copilotSession != null, SelectedModel, _executionMode, GetStatusFields(), GetAdditionalTargetsForDisplay(), DefaultSessionTarget);
                }

                continue;
            }

            // Send message to Copilot
            RecordPrompt(input);

            var escCts = new CancellationTokenSource();
            try
            {
                var escTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!escCts.Token.IsCancellationRequested)
                        {
                            if (Console.KeyAvailable && !LiveThinkingIndicator.IsApprovalInProgress)
                            {
                                var k = Console.ReadKey(intercept: true);
                                if (k.Key == ConsoleKey.Escape)
                                {
                                    escCts.Cancel();
                                    break;
                                }
                            }
                            await Task.Delay(50, escCts.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when AI finishes before ESC
                    }
                }, CancellationToken.None);

                await SendMessageAsync(input, escCts.Token);

                // Stop ESC polling if AI finished before ESC was pressed
                escCts.Cancel();
                // (no ResetConversationAsync needed - SDK cancellation is clean)
                try { await escTask.WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (TimeoutException) { /* ignore */ }
            }
            finally
            {
                escCts.Dispose();
            }
        }
    }

    private bool IsCurrentModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(_selectedModel))
        {
            return false;
        }

        if (_selectedModel.Equals(modelId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var selectedByName = _availableModels.FirstOrDefault(model =>
            model.Name.Equals(_selectedModel, StringComparison.OrdinalIgnoreCase));

        return selectedByName != null
            && selectedByName.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCurrentModelAndSource(ModelSelectionEntry entry)
        => IsCurrentModelAndSource(entry.ModelId, entry.Source);

    private bool IsCurrentModelAndSource(string modelId, ModelSource source)
    {
        if (!IsCurrentModel(modelId))
            return false;

        // Same model — check if provider also matches
        var currentSource = _useByokOpenAi ? ModelSource.Byok : ModelSource.GitHub;
        return currentSource == source;
    }

    private static string GetFirstInputToken(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var separatorIndex = input.IndexOf(' ');
        return separatorIndex >= 0 ? input[..separatorIndex] : input;
    }

    private static bool IsSlashCommandInvocation(string input, string command)
    {
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        return input.Equals(command, StringComparison.Ordinal)
            || input.StartsWith(command + " ", StringComparison.Ordinal);
    }

    private async Task<bool> ResetConversationAsync(Action<string>? updateStatus = null)
    {
        try
        {
            if (_copilotSession != null)
            {
                await _copilotSession.DisposeAsync();
                _copilotSession = null;
            }

            lock (_reportLock)
            {
                _reportPrompts.Clear();
                _lastPromptIndex = -1;
            }

            var modelToUse = string.IsNullOrWhiteSpace(_selectedModel)
                ? _requestedModel
                : _selectedModel;

            return await CreateCopilotSessionAsync(modelToUse, updateStatus);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                   || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> BuildByokModelEndpointCandidates(string baseUrl)
    {
        var normalizedBaseUrl = baseUrl.Trim().TrimEnd('/');
        var candidates = new List<string>
        {
            normalizedBaseUrl + "/models"
        };

        if (!normalizedBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(normalizedBaseUrl + "/v1/models");
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<ModelInfo>> TryGetByokProviderModelsAsync()
    {
        _byokPricing.Clear();

        if (string.IsNullOrWhiteSpace(_byokOpenAiApiKey) || !LooksLikeUrl(_byokOpenAiBaseUrl))
        {
            return [];
        }

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        var endpointCandidates = BuildByokModelEndpointCandidates(_byokOpenAiBaseUrl);
        foreach (var endpoint in endpointCandidates)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_byokOpenAiApiKey}");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var response = await httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var document = await JsonDocument.ParseAsync(stream);

                var discovery = ParseByokModelsResponse(document.RootElement);
                if (discovery.Models.Count > 0)
                {
                    foreach (var entry in discovery.PricingByModelId)
                    {
                        _byokPricing[entry.Key] = entry.Value;
                    }

                    return discovery.Models;
                }
            }
            catch
            {
                // Try next candidate endpoint.
            }
        }

        return [];
    }

    private static ByokModelDiscoveryResult ParseByokModelsResponse(JsonElement rootElement)
    {
        if (!TryGetJsonPropertyIgnoreCase(rootElement, "data", out var dataElement)
            || dataElement.ValueKind != JsonValueKind.Array)
        {
            return new ByokModelDiscoveryResult([], new(StringComparer.OrdinalIgnoreCase));
        }

        var discovered = new List<ModelInfo>();
        var pricing = new Dictionary<string, ByokPriceInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var modelElement in dataElement.EnumerateArray())
        {
            var modelId = ReadJsonStringProperty(modelElement, "id");
            if (string.IsNullOrWhiteSpace(modelId)
                || discovered.Any(existing => existing.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var modelName = ReadJsonStringProperty(modelElement, "name", "display_name", "displayName");

            if (ModelPricingDatabase.IsNonChatModel(modelId))
            {
                continue;
            }

            var apiMode = ReadJsonStringProperty(modelElement, "mode", "type", "object_type");
            if (!string.IsNullOrWhiteSpace(apiMode) && IsNonChatApiMode(apiMode))
            {
                continue;
            }

            var model = new ModelInfo
            {
                Id = modelId,
                Name = modelName ?? ToModelDisplayName(modelId)
            };

            var capabilities = BuildByokCapabilities(modelElement);
            if (capabilities != null)
            {
                model.Capabilities = capabilities;
            }

            var billingMultiplier = ReadJsonDoubleProperty(modelElement, "multiplier");
            if (!billingMultiplier.HasValue
                && TryGetJsonPropertyIgnoreCase(modelElement, "billing", out var billingElement)
                && billingElement.ValueKind == JsonValueKind.Object)
            {
                billingMultiplier = ReadJsonDoubleProperty(billingElement, "multiplier");
            }

            if (billingMultiplier.HasValue)
            {
                model.Billing = new ModelBilling
                {
                    Multiplier = billingMultiplier.Value
                };
            }

            var supportedReasoningEfforts = ReadJsonStringArrayProperty(modelElement, "supported_reasoning_efforts", "supportedReasoningEfforts");
            if (supportedReasoningEfforts.Count > 0)
            {
                model.SupportedReasoningEfforts = supportedReasoningEfforts;
            }

            var defaultReasoningEffort = ReadJsonStringProperty(modelElement, "default_reasoning_effort", "defaultReasoningEffort");
            if (!string.IsNullOrWhiteSpace(defaultReasoningEffort))
            {
                model.DefaultReasoningEffort = defaultReasoningEffort;
            }

            var priceInfo = ExtractByokPriceInfo(modelElement);
            if (priceInfo == null
                && !ModelPricingDatabase.IsNonChatModel(modelId)
                && ModelPricingDatabase.TryGetPrice(modelId, out var fallbackInput, out var fallbackOutput))
            {
                priceInfo = new ByokPriceInfo(fallbackInput, fallbackOutput, FormatByokPriceDisplayEstimate(fallbackInput, fallbackOutput));
            }

            if (priceInfo != null)
            {
                pricing[modelId] = priceInfo;
            }

            discovered.Add(model);
        }

        return new ByokModelDiscoveryResult(discovered, pricing);
    }

    private static bool IsNonChatApiMode(string mode)
    {
        return mode.Equals("image_generation", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("embedding", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("audio_transcription", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("audio_speech", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("completion", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("moderation", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("rerank", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("video_generation", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("realtime", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("responses", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("ocr", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> ConfigureByokOpenAiAsync(string baseUrl, string apiKey, string? model, Action<string>? updateStatus)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ConsoleUI.ShowWarning($"OpenAI API key is required. Set {OpenAiApiKeyEnvironmentVariable} or pass /byok <api-key> [base-url] [model].");
            return false;
        }

        if (!LooksLikeUrl(baseUrl))
        {
            ConsoleUI.ShowWarning("OpenAI-compatible base URL is required. Example: https://api.openai.com/v1");
            return false;
        }

        _useByokOpenAi = true;
        _byokOpenAiBaseUrl = baseUrl.Trim();
        _byokOpenAiApiKey = apiKey.Trim();

        if (_copilotClient == null)
        {
            ConsoleUI.ShowWarning("Copilot client is not ready. Restart TroubleScout and try /byok again.");
            return false;
        }

        if (_copilotSession != null)
        {
            await _copilotSession.DisposeAsync();
            _copilotSession = null;
        }

        updateStatus?.Invoke("Fetching models from OpenAI-compatible endpoint...");
        if (updateStatus == null)
        {
            ConsoleUI.ShowInfo("Fetching models from OpenAI-compatible endpoint...");
        }

        var discoveredModels = await TryGetByokProviderModelsAsync();
        if (discoveredModels.Count > 0)
        {
            var preferredModel = !string.IsNullOrWhiteSpace(model) && discoveredModels.Any(item => item.Id.Equals(model, StringComparison.OrdinalIgnoreCase))
                ? model
                : discoveredModels[0].Id;

            var selectedModel = ConsoleUI.PromptModelSelection(preferredModel, discoveredModels);
            if (string.IsNullOrWhiteSpace(selectedModel))
            {
                ConsoleUI.ShowWarning("BYOK model selection was canceled.");
                return false;
            }

            model = selectedModel;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                ConsoleUI.ShowInfo("Could not fetch models from the OpenAI-compatible endpoint.");
                ConsoleUI.ShowInfo("Enter model ID to continue with BYOK:");
                model = ConsoleUI.GetUserInput().Trim();
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                ConsoleUI.ShowWarning("A model ID is required when provider model discovery is unavailable.");
                return false;
            }
        }

        var created = await CreateCopilotSessionAsync(model, updateStatus);
        if (created)
        {
            SaveByokSettings(true, _byokOpenAiBaseUrl, _byokOpenAiApiKey);
        }

        return created;
    }

    private async Task<bool> LoginAndCreateGitHubSessionAsync(Action<string>? updateStatus)
    {
        if (_copilotClient == null)
        {
            ConsoleUI.ShowWarning("Copilot client is not ready. Restart TroubleScout and try again.");
            return false;
        }

        var cliPath = GetCopilotCliPath();
        var (command, args) = BuildCopilotCommand(cliPath, "login");

        updateStatus?.Invoke("Launching authentication flow...");

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                UseShellExecute = true,
                CreateNoWindow = false
            });

            if (process == null)
            {
                ConsoleUI.ShowWarning("Could not start copilot login process.");
                return false;
            }

            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowWarning($"Failed to run login flow: {TrimSingleLine(ex.Message)}");
            return false;
        }

        var authStatus = await _copilotClient.GetAuthStatusAsync();
        if (!authStatus.IsAuthenticated)
        {
            _isGitHubCopilotAuthenticated = false;
            ConsoleUI.ShowWarning("Login did not complete. Try /login again, then verify your browser/device flow finished.");
            return false;
        }

        _isGitHubCopilotAuthenticated = true;

        if (_copilotSession != null)
        {
            await _copilotSession.DisposeAsync();
            _copilotSession = null;
        }

        updateStatus?.Invoke("Creating authenticated AI session...");

        var githubModels = await GetMergedModelListAsync(GetCopilotCliPath());
        if (githubModels.Count == 0)
        {
            ConsoleUI.ShowWarning("No GitHub Copilot models are currently available after login.");
            return false;
        }

        _useByokOpenAi = false;
        var modelToUse = !string.IsNullOrWhiteSpace(_selectedModel)
            && githubModels.Any(model => model.Id.Equals(_selectedModel, StringComparison.OrdinalIgnoreCase))
                ? _selectedModel
                : githubModels[0].Id;

        var created = await CreateCopilotSessionAsync(modelToUse, updateStatus);
        if (created)
        {
            await RefreshAvailableModelsAsync();
        }

        return created;
    }

    private async Task<bool> IsGitHubAuthenticatedAsync()
    {
        if (_copilotClient == null)
        {
            return false;
        }

        try
        {
            var authStatus = await _copilotClient.GetAuthStatusAsync();
            return authStatus.IsAuthenticated;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsBareExitCommand(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        return input.Equals("exit", StringComparison.Ordinal)
            || input.Equals("quit", StringComparison.Ordinal);
    }

    private void RecordPrompt(string prompt)
    {
        lock (_reportLock)
        {
            _reportPrompts.Add(new ReportPromptEntry(DateTimeOffset.Now, prompt, [], string.Empty));
            _lastPromptIndex = _reportPrompts.Count - 1;
        }
    }

    private void SetPromptReply(int promptIndex, string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return;
        }

        lock (_reportLock)
        {
            if (promptIndex < 0 || promptIndex >= _reportPrompts.Count)
            {
                return;
            }

            var current = _reportPrompts[promptIndex];
            _reportPrompts[promptIndex] = current with { AgentReply = reply.Trim() };
        }
    }

    private void RecordCommandAction(CommandActionLog actionLog)
    {
        var entry = new ReportActionEntry(
            actionLog.Timestamp,
            actionLog.Target,
            actionLog.Command,
            actionLog.Output,
            actionLog.ApprovalState.ToString(),
            "PowerShell");

        AppendActionToCurrentPrompt(entry);
    }

    private void RecordMcpToolAction(ToolExecutionStartEvent toolStart)
    {
        var mcpServerName = ReadStringProperty(toolStart.Data, "McpServerName", "MCPServerName", "ServerName");
        if (string.IsNullOrWhiteSpace(mcpServerName))
        {
            return;
        }

        var toolName = toolStart.Data?.ToolName ?? "unknown-tool";

        var entry = new ReportActionEntry(
            DateTimeOffset.Now,
            mcpServerName,
            toolName,
            "N/A",
            "N/A",
            "MCP");

        AppendActionToCurrentPrompt(entry);
    }

    private void AppendActionToCurrentPrompt(ReportActionEntry actionEntry)
    {
        lock (_reportLock)
        {
            if (_lastPromptIndex < 0 || _lastPromptIndex >= _reportPrompts.Count)
            {
                return;
            }

            _reportPrompts[_lastPromptIndex].Actions.Add(actionEntry);
        }
    }

    private void GenerateAndOpenReport()
    {
        List<ReportPromptEntry> prompts;
        lock (_reportLock)
        {
            prompts = _reportPrompts
                .Select(prompt => new ReportPromptEntry(
                    prompt.Timestamp,
                    prompt.Prompt,
                    prompt.Actions.ToList(),
                    prompt.AgentReply))
                .ToList();
        }

        if (prompts.Count == 0)
        {
            ConsoleUI.ShowInfo("No prompts recorded yet. Ask a question first, then run /report.");
            return;
        }

        var reportsDir = Path.Combine(Path.GetTempPath(), "TroubleScout", "reports");
        Directory.CreateDirectory(reportsDir);

        var reportPath = Path.Combine(reportsDir, $"troublescout-report-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.html");
        var html = BuildReportHtml(prompts);
        File.WriteAllText(reportPath, html, Encoding.UTF8);

        try
        {
            // Use cmd.exe /c start instead of UseShellExecute to respect the current
            // user context when running as a different user (RunAs). UseShellExecute
            // opens the browser as the primary logged-in user, causing path mismatches.
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"\" \"{reportPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            System.Diagnostics.Process.Start(psi);
            ConsoleUI.ShowSuccess($"Report generated and opened: {reportPath}");
            ConsoleUI.ShowInfo($"Reports are stored in temp: {reportsDir}");
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowWarning($"Report generated at {reportPath}, but could not auto-open browser: {TrimSingleLine(ex.Message)}");
        }
    }

    private static string BuildReportHtml(IReadOnlyList<ReportPromptEntry> prompts)
    {
        var totalActions = prompts.Sum(prompt => prompt.Actions.Count);
        var generatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

        // Compute session duration from first to last prompt
        var sessionDuration = string.Empty;
        if (prompts.Count >= 2)
        {
            var span = prompts[^1].Timestamp - prompts[0].Timestamp;
            sessionDuration = span.TotalHours >= 1
                ? span.ToString(@"h\:mm\:ss")
                : span.ToString(@"m\:ss");
        }
        else
        {
            sessionDuration = "N/A";
        }

        // Compute approval breakdown
        var safeCount = 0;
        var approvedCount = 0;
        var blockedCount = 0;
        var deniedCount = 0;
        foreach (var p in prompts)
        {
            foreach (var a in p.Actions)
            {
                switch (a.SafetyApproval)
                {
                    case "SafeAuto":
                        safeCount++;
                        break;
                    case "AutoApprovedYolo":
                    case "ApprovedByUser":
                        approvedCount++;
                        break;
                    case "Blocked":
                        blockedCount++;
                        break;
                    case "ApprovalRequested":
                        // ApprovalRequested is an intermediate state that is later followed by ApprovedByUser or Denied.
                        break;
                    case "Denied":
                        deniedCount++;
                        break;
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\" />");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        sb.AppendLine("  <title>TroubleScout Session Report</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    *, *::before, *::after { box-sizing: border-box; }");
        sb.AppendLine("    :root { color-scheme: dark; }");
        sb.AppendLine("    body { font-family: 'Segoe UI', Inter, system-ui, -apple-system, sans-serif; margin: 0; background: #0b1121; color: #e2e8f0; line-height: 1.6; -webkit-font-smoothing: antialiased; }");
        sb.AppendLine("    .wrap { max-width: 1100px; margin: 0 auto; padding: 24px 20px 48px; }");

        // Hero header
        sb.AppendLine("    .hero { background: linear-gradient(135deg, #1e293b 0%, #0f172a 50%, #1a1c2e 100%); border: 1px solid #334155; border-radius: 16px; padding: 32px 36px 28px; margin-bottom: 24px; position: relative; overflow: hidden; }");
        sb.AppendLine("    .hero::before { content: ''; position: absolute; top: -50%; right: -20%; width: 400px; height: 400px; background: radial-gradient(circle, rgba(59,130,246,0.08) 0%, transparent 70%); pointer-events: none; }");
        sb.AppendLine("    .hero-top { display: flex; align-items: center; gap: 16px; margin-bottom: 20px; }");
        sb.AppendLine("    .hero-icon { flex-shrink: 0; }");
        sb.AppendLine("    .hero h1 { margin: 0; font-size: 2rem; font-weight: 700; letter-spacing: -0.02em; color: #f8fafc; }");
        sb.AppendLine("    .hero-subtitle { color: #94a3b8; font-size: 0.95rem; margin-top: 2px; }");
        sb.AppendLine("    .hero-stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 12px; margin-top: 20px; }");
        sb.AppendLine("    .hero-stat { background: rgba(255,255,255,0.04); border: 1px solid #334155; border-radius: 10px; padding: 12px 16px; text-align: center; }");
        sb.AppendLine("    .hero-stat-value { font-size: 1.5rem; font-weight: 700; color: #f8fafc; }");
        sb.AppendLine("    .hero-stat-label { font-size: 0.78rem; color: #94a3b8; text-transform: uppercase; letter-spacing: 0.05em; margin-top: 2px; }");

        // Summary cards
        sb.AppendLine("    .summary-row { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 12px; margin-bottom: 28px; }");
        sb.AppendLine("    .summary-card { border-radius: 12px; padding: 16px 18px; border: 1px solid; }");
        sb.AppendLine("    .summary-card .sc-val { font-size: 1.75rem; font-weight: 700; }");
        sb.AppendLine("    .summary-card .sc-lbl { font-size: 0.8rem; text-transform: uppercase; letter-spacing: 0.04em; opacity: 0.85; margin-top: 2px; }");
        sb.AppendLine("    .sc-green { background: rgba(34,197,94,0.08); border-color: rgba(34,197,94,0.25); color: #22c55e; }");
        sb.AppendLine("    .sc-blue { background: rgba(59,130,246,0.08); border-color: rgba(59,130,246,0.25); color: #3b82f6; }");
        sb.AppendLine("    .sc-red { background: rgba(239,68,68,0.08); border-color: rgba(239,68,68,0.25); color: #ef4444; }");
        sb.AppendLine("    .sc-amber { background: rgba(245,158,11,0.08); border-color: rgba(245,158,11,0.25); color: #f59e0b; }");

        // Timeline
        sb.AppendLine("    .timeline { position: relative; padding-left: 36px; }");
        sb.AppendLine("    .timeline::before { content: ''; position: absolute; left: 15px; top: 0; bottom: 0; width: 2px; background: linear-gradient(to bottom, #334155, #1e293b); }");
        sb.AppendLine("    .timeline-item { position: relative; margin-bottom: 20px; }");
        sb.AppendLine("    .timeline-dot { position: absolute; left: -29px; top: 18px; width: 12px; height: 12px; border-radius: 50%; background: #3b82f6; border: 2px solid #0b1121; z-index: 1; }");

        // Prompt card
        sb.AppendLine("    .prompt-card { background: #111827; border: 1px solid #1e293b; border-radius: 14px; overflow: hidden; transition: border-color 0.2s ease; }");
        sb.AppendLine("    .prompt-card:hover { border-color: #334155; }");
        sb.AppendLine("    .prompt-header { padding: 18px 20px; cursor: pointer; display: flex; align-items: flex-start; gap: 14px; }");
        sb.AppendLine("    .prompt-header:hover { background: rgba(255,255,255,0.02); }");
        sb.AppendLine("    .prompt-badge { flex-shrink: 0; width: 36px; height: 36px; border-radius: 10px; background: linear-gradient(135deg, #3b82f6, #6366f1); display: flex; align-items: center; justify-content: center; font-weight: 700; font-size: 0.9rem; color: #fff; }");
        sb.AppendLine("    .prompt-info { flex: 1; min-width: 0; }");
        sb.AppendLine("    .prompt-text { font-size: 1.05rem; font-weight: 600; color: #f1f5f9; word-break: break-word; }");
        sb.AppendLine("    .prompt-meta { display: flex; flex-wrap: wrap; gap: 12px; margin-top: 6px; font-size: 0.82rem; color: #94a3b8; }");
        sb.AppendLine("    .prompt-meta-item { display: flex; align-items: center; gap: 4px; }");
        sb.AppendLine("    .prompt-chevron { flex-shrink: 0; width: 20px; height: 20px; color: #64748b; transition: transform 0.25s ease; margin-top: 8px; }");
        sb.AppendLine("    details[open] > .prompt-header .prompt-chevron { transform: rotate(90deg); }");

        // Prompt card details/summary native reset
        sb.AppendLine("    .prompt-card > summary { list-style: none; }");
        sb.AppendLine("    .prompt-card > summary::-webkit-details-marker { display: none; }");
        sb.AppendLine("    .prompt-card > summary::marker { display: none; content: ''; }");

        // Prompt body with expand/collapse transition
        sb.AppendLine("    .prompt-body { padding: 0 20px 20px; border-top: 1px solid #1e293b; }");

        // Action card
        sb.AppendLine("    .action-card { background: #0d1525; border: 1px solid #1e293b; border-radius: 10px; padding: 16px; margin-top: 14px; }");
        sb.AppendLine("    .action-header { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; margin-bottom: 12px; }");
        sb.AppendLine("    .action-time { font-size: 0.82rem; color: #94a3b8; font-family: 'Cascadia Mono', Consolas, monospace; }");
        sb.AppendLine("    .action-target { font-size: 0.82rem; color: #cbd5e1; background: rgba(255,255,255,0.05); padding: 2px 10px; border-radius: 6px; }");

        // Approval chips
        sb.AppendLine("    .approval-chip { display: inline-flex; align-items: center; gap: 5px; padding: 3px 10px; border-radius: 999px; font-size: 0.78rem; font-weight: 600; }");
        sb.AppendLine("    .approval-SafeAuto { background: rgba(34,197,94,0.12); color: #22c55e; border: 1px solid rgba(34,197,94,0.3); }");
        sb.AppendLine("    .approval-AutoApprovedYolo { background: rgba(249,115,22,0.12); color: #f97316; border: 1px solid rgba(249,115,22,0.3); }");
        sb.AppendLine("    .approval-ApprovedByUser { background: rgba(59,130,246,0.12); color: #3b82f6; border: 1px solid rgba(59,130,246,0.3); }");
        sb.AppendLine("    .approval-ApprovalRequested { background: rgba(234,179,8,0.12); color: #eab308; border: 1px solid rgba(234,179,8,0.3); }");
        sb.AppendLine("    .approval-Denied { background: rgba(239,68,68,0.12); color: #ef4444; border: 1px solid rgba(239,68,68,0.3); }");
        sb.AppendLine("    .approval-Blocked { background: rgba(220,38,38,0.12); color: #dc2626; border: 1px solid rgba(220,38,38,0.3); }");
        sb.AppendLine("    .source-chip { display: inline-block; padding: 2px 9px; border-radius: 6px; font-size: 0.78rem; color: #94a3b8; background: rgba(255,255,255,0.05); border: 1px solid #334155; }");

        // Inner expandable sections
        sb.AppendLine("    .inner-section { margin-top: 10px; border: 1px solid #1e293b; border-radius: 8px; overflow: hidden; background: #0a1223; }");
        sb.AppendLine("    .inner-section > summary { list-style: none; padding: 10px 14px; font-size: 0.82rem; font-weight: 600; letter-spacing: 0.03em; color: #93c5fd; text-transform: uppercase; cursor: pointer; display: flex; align-items: center; gap: 8px; }");
        sb.AppendLine("    .inner-section > summary::-webkit-details-marker { display: none; }");
        sb.AppendLine("    .inner-section > summary::marker { display: none; content: ''; }");
        sb.AppendLine("    .inner-section > summary:hover { background: rgba(255,255,255,0.02); }");
        sb.AppendLine("    .inner-section > summary::before { content: '\\25B6'; font-size: 0.6rem; transition: transform 0.2s ease; display: inline-block; }");
        sb.AppendLine("    .inner-section[open] > summary::before { transform: rotate(90deg); }");
        sb.AppendLine("    .inner-content { padding: 12px 14px; }");

        // Code blocks with line numbers and copy button
        sb.AppendLine("    .code-wrap { position: relative; margin: 0; }");
        sb.AppendLine("    .copy-btn { position: absolute; top: 8px; right: 8px; background: rgba(255,255,255,0.08); border: 1px solid #334155; border-radius: 6px; color: #94a3b8; font-size: 0.75rem; padding: 4px 10px; cursor: pointer; transition: all 0.15s ease; z-index: 2; font-family: inherit; }");
        sb.AppendLine("    .copy-btn:hover { background: rgba(255,255,255,0.14); color: #e2e8f0; }");
        sb.AppendLine("    .copy-btn.copied { background: rgba(34,197,94,0.15); border-color: rgba(34,197,94,0.3); color: #22c55e; }");
        sb.AppendLine("    .code-block { margin: 0; white-space: pre-wrap; word-break: break-word; font-family: 'Cascadia Mono', Consolas, 'Courier New', monospace; font-size: 0.88rem; line-height: 1.6; border: 1px solid #1e293b; border-radius: 8px; padding: 12px 14px 12px 0; background: #080e1c; counter-reset: line; overflow-x: auto; }");
        sb.AppendLine("    .code-line { display: block; padding-left: 52px; position: relative; min-height: 1.6em; }");
        sb.AppendLine("    .code-line::before { counter-increment: line; content: counter(line); position: absolute; left: 0; width: 40px; text-align: right; color: #334155; font-size: 0.78rem; padding-right: 12px; user-select: none; -webkit-user-select: none; }");
        sb.AppendLine("    .output-block { margin: 0; white-space: pre-wrap; word-break: break-word; font-family: 'Cascadia Mono', Consolas, 'Courier New', monospace; font-size: 0.85rem; line-height: 1.5; border: 1px solid #1e293b; border-radius: 8px; padding: 12px 14px; background: #080e1c; max-height: 400px; overflow-y: auto; }");

        // Syntax highlighting tokens
        sb.AppendLine("    .tok-cmdlet { color: #67e8f9; font-weight: 600; }");
        sb.AppendLine("    .tok-param { color: #fde68a; }");
        sb.AppendLine("    .tok-string { color: #86efac; }");
        sb.AppendLine("    .tok-variable { color: #93c5fd; }");
        sb.AppendLine("    .tok-number { color: #c4b5fd; }");
        sb.AppendLine("    .tok-op { color: #f9a8d4; }");

        // Agent reply chat bubble
        sb.AppendLine("    .reply-bubble { margin-top: 16px; background: linear-gradient(135deg, #162032, #111827); border: 1px solid #1e293b; border-radius: 14px; padding: 18px 20px; position: relative; }");
        sb.AppendLine("    .reply-header { display: flex; align-items: center; gap: 10px; margin-bottom: 12px; }");
        sb.AppendLine("    .reply-avatar { width: 32px; height: 32px; border-radius: 8px; background: linear-gradient(135deg, #6366f1, #8b5cf6); display: flex; align-items: center; justify-content: center; flex-shrink: 0; }");
        sb.AppendLine("    .reply-label { font-size: 0.85rem; font-weight: 600; color: #a5b4fc; }");
        sb.AppendLine("    .reply-text { white-space: pre-wrap; word-break: break-word; font-size: 0.92rem; line-height: 1.65; color: #cbd5e1; }");

        // Muted text
        sb.AppendLine("    .muted { color: #64748b; font-size: 0.88rem; }");

        // No actions
        sb.AppendLine("    .no-actions { padding: 16px; text-align: center; color: #64748b; font-style: italic; }");

        // Footer
        sb.AppendLine("    .footer { margin-top: 40px; padding: 20px 0; border-top: 1px solid #1e293b; text-align: center; color: #475569; font-size: 0.82rem; }");
        sb.AppendLine("    .footer a { color: #64748b; text-decoration: none; }");

        // Print styles
        sb.AppendLine("    @media print {");
        sb.AppendLine("      body { background: #fff; color: #1e293b; -webkit-print-color-adjust: exact; print-color-adjust: exact; }");
        sb.AppendLine("      .hero { background: #f8fafc; border-color: #d1d5db; }");
        sb.AppendLine("      .hero h1, .hero-stat-value { color: #111827; }");
        sb.AppendLine("      .hero-subtitle, .hero-stat-label { color: #6b7280; }");
        sb.AppendLine("      .hero::before { display: none; }");
        sb.AppendLine("      .hero-stat { background: #f1f5f9; border-color: #d1d5db; }");
        sb.AppendLine("      .summary-card { border-width: 2px; }");
        sb.AppendLine("      .prompt-card { background: #fff; border-color: #d1d5db; break-inside: avoid; }");
        sb.AppendLine("      .prompt-card .prompt-body { display: block; }");
        sb.AppendLine("      .inner-section > .inner-content { display: block; }");
        sb.AppendLine("      .prompt-text { color: #111827; }");
        sb.AppendLine("      .prompt-meta { color: #6b7280; }");
        sb.AppendLine("      .prompt-chevron { display: none; }");
        sb.AppendLine("      .action-card { background: #f8fafc; border-color: #d1d5db; break-inside: avoid; }");
        sb.AppendLine("      .inner-section { background: #f8fafc; border-color: #d1d5db; }");
        sb.AppendLine("      .inner-section > summary { color: #3b82f6; }");
        sb.AppendLine("      .code-block, .output-block { background: #f1f5f9; border-color: #d1d5db; color: #1e293b; }");
        sb.AppendLine("      .code-line::before { color: #9ca3af; }");
        sb.AppendLine("      .tok-cmdlet { color: #0369a1; }");
        sb.AppendLine("      .tok-param { color: #92400e; }");
        sb.AppendLine("      .tok-string { color: #166534; }");
        sb.AppendLine("      .tok-variable { color: #1d4ed8; }");
        sb.AppendLine("      .tok-number { color: #7e22ce; }");
        sb.AppendLine("      .tok-op { color: #be185d; }");
        sb.AppendLine("      .reply-bubble { background: #f8fafc; border-color: #d1d5db; }");
        sb.AppendLine("      .reply-label { color: #4f46e5; }");
        sb.AppendLine("      .reply-text { color: #374151; }");
        sb.AppendLine("      .copy-btn { display: none; }");
        sb.AppendLine("      .timeline::before { background: #d1d5db; }");
        sb.AppendLine("      .timeline-dot { background: #3b82f6; border-color: #fff; }");
        sb.AppendLine("      .footer { color: #9ca3af; border-color: #d1d5db; }");
        sb.AppendLine("    }");

        // Responsive
        sb.AppendLine("    @media (max-width: 640px) {");
        sb.AppendLine("      .wrap { padding: 12px 10px 32px; }");
        sb.AppendLine("      .hero { padding: 20px 16px; border-radius: 12px; }");
        sb.AppendLine("      .hero h1 { font-size: 1.4rem; }");
        sb.AppendLine("      .hero-stats { grid-template-columns: repeat(2, 1fr); }");
        sb.AppendLine("      .summary-row { grid-template-columns: repeat(2, 1fr); }");
        sb.AppendLine("      .timeline { padding-left: 0; }");
        sb.AppendLine("      .timeline::before { display: none; }");
        sb.AppendLine("      .timeline-dot { display: none; }");
        sb.AppendLine("      .prompt-header { padding: 14px 14px; }");
        sb.AppendLine("      .prompt-badge { width: 30px; height: 30px; font-size: 0.8rem; }");
        sb.AppendLine("      .prompt-body { padding: 0 14px 14px; }");
        sb.AppendLine("      .action-card { padding: 12px; }");
        sb.AppendLine("    }");

        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class=\"wrap\">");

        // ── Hero header ──
        sb.AppendLine("    <div class=\"hero\">");
        sb.AppendLine("      <div class=\"hero-top\">");
        sb.AppendLine("        <div class=\"hero-icon\">");
        sb.AppendLine("          <svg width=\"44\" height=\"44\" viewBox=\"0 0 44 44\" fill=\"none\" xmlns=\"http://www.w3.org/2000/svg\">");
        sb.AppendLine("            <path d=\"M22 4L38 12V28C38 34.6 30.8 40 22 42C13.2 40 6 34.6 6 28V12L22 4Z\" fill=\"url(#shieldGrad)\" stroke=\"#3b82f6\" stroke-width=\"1.5\" />");
        sb.AppendLine("            <path d=\"M16 22L20 26L28 18\" stroke=\"#f8fafc\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\" />");
        sb.AppendLine("            <defs><linearGradient id=\"shieldGrad\" x1=\"6\" y1=\"4\" x2=\"38\" y2=\"42\" gradientUnits=\"userSpaceOnUse\"><stop stop-color=\"#3b82f6\" stop-opacity=\"0.3\" /><stop offset=\"1\" stop-color=\"#6366f1\" stop-opacity=\"0.15\" /></linearGradient></defs>");
        sb.AppendLine("          </svg>");
        sb.AppendLine("        </div>");
        sb.AppendLine("        <div>");
        sb.AppendLine("          <h1>TroubleScout</h1>");
        sb.AppendLine("          <div class=\"hero-subtitle\">Session Report</div>");
        sb.AppendLine("        </div>");
        sb.AppendLine("      </div>");
        sb.AppendLine("      <div class=\"hero-stats\">");
        sb.AppendLine($"        <div class=\"hero-stat\"><div class=\"hero-stat-value\">{prompts.Count}</div><div class=\"hero-stat-label\">Prompts</div></div>");
        sb.AppendLine($"        <div class=\"hero-stat\"><div class=\"hero-stat-value\">{totalActions}</div><div class=\"hero-stat-label\">Actions</div></div>");
        sb.AppendLine($"        <div class=\"hero-stat\"><div class=\"hero-stat-value\">{HtmlEncode(sessionDuration)}</div><div class=\"hero-stat-label\">Duration</div></div>");
        sb.AppendLine($"        <div class=\"hero-stat\"><div class=\"hero-stat-value\" style=\"font-size:0.95rem\">{HtmlEncode(generatedAt)}</div><div class=\"hero-stat-label\">Generated</div></div>");
        sb.AppendLine("      </div>");
        sb.AppendLine("    </div>");

        // ── Summary statistics cards ──
        sb.AppendLine("    <div class=\"summary-row\">");
        sb.AppendLine($"      <div class=\"summary-card sc-green\"><div class=\"sc-val\">{safeCount}</div><div class=\"sc-lbl\">Safe (Auto)</div></div>");
        sb.AppendLine($"      <div class=\"summary-card sc-blue\"><div class=\"sc-val\">{approvedCount}</div><div class=\"sc-lbl\">Approved</div></div>");
        sb.AppendLine($"      <div class=\"summary-card sc-red\"><div class=\"sc-val\">{blockedCount}</div><div class=\"sc-lbl\">Blocked</div></div>");
        sb.AppendLine($"      <div class=\"summary-card sc-amber\"><div class=\"sc-val\">{deniedCount}</div><div class=\"sc-lbl\">Denied</div></div>");
        sb.AppendLine("    </div>");

        // ── Timeline of prompt cards ──
        sb.AppendLine("    <div class=\"timeline\">");

        for (var i = 0; i < prompts.Count; i++)
        {
            var prompt = prompts[i];
            var promptTime = prompt.Timestamp.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

            sb.AppendLine("      <div class=\"timeline-item\">");
            sb.AppendLine("        <div class=\"timeline-dot\"></div>");
            sb.AppendLine("        <details class=\"prompt-card\">");
            sb.AppendLine("          <summary class=\"prompt-header\">");
            sb.AppendLine($"            <div class=\"prompt-badge\">{i + 1}</div>");
            sb.AppendLine("            <div class=\"prompt-info\">");
            sb.AppendLine($"              <div class=\"prompt-text\">{HtmlEncode(prompt.Prompt)}</div>");
            sb.AppendLine("              <div class=\"prompt-meta\">");
            sb.AppendLine($"                <span class=\"prompt-meta-item\">&#128337; {HtmlEncode(promptTime)}</span>");
            sb.AppendLine($"                <span class=\"prompt-meta-item\">&#9881; {prompt.Actions.Count} action{(prompt.Actions.Count == 1 ? "" : "s")}</span>");
            sb.AppendLine("              </div>");
            sb.AppendLine("            </div>");
            sb.AppendLine("            <svg class=\"prompt-chevron\" viewBox=\"0 0 20 20\" fill=\"currentColor\"><path fill-rule=\"evenodd\" d=\"M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z\" clip-rule=\"evenodd\" /></svg>");
            sb.AppendLine("          </summary>");
            sb.AppendLine("          <div class=\"prompt-body\">");

            if (prompt.Actions.Count == 0)
            {
                sb.AppendLine("            <div class=\"no-actions\">No actions captured for this prompt.</div>");
            }
            else
            {
                foreach (var action in prompt.Actions)
                {
                    var actionTime = action.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

                    sb.AppendLine("            <div class=\"action-card\">");
                    sb.AppendLine("              <div class=\"action-header\">");
                    sb.AppendLine($"                <span class=\"action-time\">{HtmlEncode(actionTime)}</span>");
                    sb.AppendLine($"                <span class=\"source-chip\">{HtmlEncode(action.Source)}</span>");
                    sb.AppendLine($"                <span class=\"approval-chip approval-{HtmlEncode(action.SafetyApproval)}\">{HtmlEncode(action.SafetyApproval)}</span>");
                    if (!string.IsNullOrWhiteSpace(action.Target))
                    {
                        sb.AppendLine($"                <span class=\"action-target\">{HtmlEncode(action.Target)}</span>");
                    }
                    sb.AppendLine("              </div>");

                    // Command section
                    sb.AppendLine("              <details class=\"inner-section\" open>");
                    sb.AppendLine("                <summary>Command</summary>");
                    sb.AppendLine("                <div class=\"inner-content\">");
                    sb.AppendLine($"                  <div class=\"code-wrap\"><button class=\"copy-btn\" onclick=\"copyCode(this)\">Copy</button><pre class=\"code-block\">{RenderCommandHtmlWithLineNumbers(action.Command)}</pre></div>");
                    sb.AppendLine("                </div>");
                    sb.AppendLine("              </details>");

                    // Output section
                    sb.AppendLine("              <details class=\"inner-section\">");
                    sb.AppendLine("                <summary>Output</summary>");
                    sb.AppendLine("                <div class=\"inner-content\">");
                    sb.AppendLine($"                  <div class=\"code-wrap\"><button class=\"copy-btn\" onclick=\"copyCode(this)\">Copy</button><pre class=\"output-block\">{HtmlEncode(action.Output)}</pre></div>");
                    sb.AppendLine("                </div>");
                    sb.AppendLine("              </details>");
                    sb.AppendLine("            </div>");
                }
            }

            // Agent reply chat bubble
            sb.AppendLine("            <div class=\"reply-bubble\">");
            sb.AppendLine("              <div class=\"reply-header\">");
            sb.AppendLine("                <div class=\"reply-avatar\">");
            sb.AppendLine("                  <svg width=\"18\" height=\"18\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"#fff\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"><path d=\"M12 2a4 4 0 0 1 4 4v2a4 4 0 0 1-8 0V6a4 4 0 0 1 4-4z\" /><path d=\"M20 21v-2a4 4 0 0 0-3-3.87\" /><path d=\"M4 21v-2a4 4 0 0 1 3-3.87\" /><circle cx=\"12\" cy=\"17\" r=\"4\" fill=\"rgba(255,255,255,0.15)\" stroke=\"#fff\" /></svg>");
            sb.AppendLine("                </div>");
            sb.AppendLine("                <span class=\"reply-label\">Agent Reply</span>");
            sb.AppendLine("              </div>");
            if (string.IsNullOrWhiteSpace(prompt.AgentReply))
            {
                sb.AppendLine("              <div class=\"muted\">No assistant reply captured for this prompt.</div>");
            }
            else
            {
                sb.AppendLine($"              <div class=\"reply-text\">{HtmlEncode(prompt.AgentReply)}</div>");
            }
            sb.AppendLine("            </div>");

            sb.AppendLine("          </div>");
            sb.AppendLine("        </details>");
            sb.AppendLine("      </div>");
        }

        sb.AppendLine("    </div>");

        // ── Footer ──
        sb.AppendLine($"    <div class=\"footer\">Generated by <strong>TroubleScout</strong> &middot; {HtmlEncode(generatedAt)}</div>");

        sb.AppendLine("  </div>");

        // ── JavaScript: copy-to-clipboard ──
        sb.AppendLine("  <script>");
        sb.AppendLine("    function copyCode(btn) {");
        sb.AppendLine("      var pre = btn.parentElement.querySelector('pre');");
        sb.AppendLine("      if (!pre) return;");
        sb.AppendLine("      var text = pre.textContent || pre.innerText;");
        sb.AppendLine("      if (navigator.clipboard && navigator.clipboard.writeText) {");
        sb.AppendLine("        navigator.clipboard.writeText(text).then(function() { showCopied(btn); }, function() {");
        sb.AppendLine("          fallbackCopy(text, btn);");
        sb.AppendLine("        });");
        sb.AppendLine("      } else {");
        sb.AppendLine("        fallbackCopy(text, btn);");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine("    function fallbackCopy(text, btn) {");
        sb.AppendLine("      var ta = document.createElement('textarea');");
        sb.AppendLine("      ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';");
        sb.AppendLine("      document.body.appendChild(ta); ta.select();");
        sb.AppendLine("      try { document.execCommand('copy'); showCopied(btn); } catch(e) {}");
        sb.AppendLine("      document.body.removeChild(ta);");
        sb.AppendLine("    }");
        sb.AppendLine("    function showCopied(btn) {");
        sb.AppendLine("      var orig = btn.textContent;");
        sb.AppendLine("      btn.textContent = '\\u2713 Copied'; btn.classList.add('copied');");
        sb.AppendLine("      setTimeout(function() { btn.textContent = orig; btn.classList.remove('copied'); }, 1500);");
        sb.AppendLine("    }");
        sb.AppendLine("  </script>");

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private static string HtmlEncode(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private static readonly Regex CommandTokenRegex = new(
        "(?<string>'[^'\\n\\r]*'|\"[^\"\\n\\r]*\")" +
        "|(?<variable>\\$[A-Za-z_][\\w:]*)" +
        "|(?<param>-[A-Za-z][\\w-]*)" +
        "|(?<cmdlet>\\b[A-Za-z]+-[A-Za-z][A-Za-z0-9]*\\b)" +
        "|(?<number>\\b\\d+(?:\\.\\d+)?\\b)" +
        "|(?<op>(?:-eq|-ne|-gt|-ge|-lt|-le|-and|-or|-not)\\b|[|;])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string RenderCommandHtml(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(command.Length + 32);
        var lastIndex = 0;

        foreach (Match match in CommandTokenRegex.Matches(command))
        {
            if (!match.Success)
            {
                continue;
            }

            if (match.Index > lastIndex)
            {
                builder.Append(HtmlEncode(command.Substring(lastIndex, match.Index - lastIndex)));
            }

            var cssClass = match.Groups["string"].Success ? "tok-string" :
                           match.Groups["variable"].Success ? "tok-variable" :
                           match.Groups["param"].Success ? "tok-param" :
                           match.Groups["cmdlet"].Success ? "tok-cmdlet" :
                           match.Groups["number"].Success ? "tok-number" :
                           match.Groups["op"].Success ? "tok-op" : string.Empty;

            var tokenText = HtmlEncode(match.Value);
            if (string.IsNullOrEmpty(cssClass))
            {
                builder.Append(tokenText);
            }
            else
            {
                builder.Append("<span class=\"").Append(cssClass).Append("\">").Append(tokenText).Append("</span>");
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < command.Length)
        {
            builder.Append(HtmlEncode(command.Substring(lastIndex)));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders a command with syntax highlighting, splitting multi-line commands into
    /// separate code-line spans so CSS counters produce per-line numbers.
    /// </summary>
    private static string RenderCommandHtmlWithLineNumbers(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return "<span class=\"code-line\"></span>";
        }

        var highlighted = RenderCommandHtml(command);
        // Split the already-highlighted HTML on literal newlines to produce one code-line per source line
        var lines = highlighted.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.Append("<span class=\"code-line\">").Append(line).AppendLine("</span>");
        }
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_copilotSession != null)
        {
            try
            {
                await _copilotSession.DisposeAsync();
            }
            catch (Exception ex)
            {
                ConsoleUI.ShowWarning($"Session cleanup warning: {TrimSingleLine(ex.Message)}");
            }
        }

        if (_copilotClient != null)
        {
            try
            {
                await _copilotClient.DisposeAsync();
            }
            catch (Exception ex)
            {
                ConsoleUI.ShowWarning($"Copilot cleanup warning: {TrimSingleLine(ex.Message)}");
            }
        }

        _executor.Dispose();

        foreach (var exec in _additionalExecutors.Values)
        {
            try { exec.Dispose(); }
            catch (Exception ex)
            {
                if (_debugMode)
                {
                    ConsoleUI.ShowWarning($"Additional session cleanup warning: {TrimSingleLine(ex.Message)}");
                }
            }
        }
        _additionalExecutors.Clear();
        
        GC.SuppressFinalize(this);
    }

    private async Task<bool> CreateCopilotSessionAsync(string? model, Action<string>? updateStatus)
    {
        if (_copilotClient == null)
        {
            ConsoleUI.ShowError("Not Connected", "Copilot client not initialized");
            return false;
        }

        updateStatus?.Invoke("Creating AI session...");

        ResetCapabilities();
        DiscoverConfiguredSkills();

        var mcpServers = LoadMcpServersFromConfig(_mcpConfigPath, _configurationWarnings);
        foreach (var serverName in mcpServers.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            _configuredMcpServers.Add(serverName);
        }

        var config = BuildSessionConfig(model);

        if (_useByokOpenAi)
        {
            config.Provider = new ProviderConfig
            {
                Type = "openai",
                BaseUrl = _byokOpenAiBaseUrl,
                ApiKey = _byokOpenAiApiKey,
                WireApi = GetByokWireApi(model)
            };
        }

        if (mcpServers.Count > 0)
        {
            config.McpServers = mcpServers;
        }

        if (_skillDirectories.Count > 0)
        {
            config.SkillDirectories = _skillDirectories.ToList();
        }

        if (_disabledSkills.Count > 0)
        {
            config.DisabledSkills = _disabledSkills.ToList();
        }

        _copilotSession = await _copilotClient.CreateSessionAsync(config);
        _sessionId = CreateSessionId();
        _lastUsage = null;
        _toolInvocationCount = 0;

        if (_configurationWarnings.Count > 0)
        {
            ConsoleUI.ShowWarning("Capabilities loaded with warnings. Use /status or /capabilities to review details.");
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            _selectedModel = model;
            // Persist model and active provider together to avoid stale provider mismatch after restart.
            SaveModelAndProviderState(model, _useByokOpenAi);
        }

        return true;
    }

    private static string? GetByokWireApi(string? model)
    {
        return !string.IsNullOrWhiteSpace(model) && model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            ? "responses"
            : null;
    }

    internal SessionConfig BuildSessionConfig(string? model)
    {
        return new SessionConfig
        {
            Model = model,
            SystemMessage = _systemMessageConfig,
            Streaming = true,
            Tools = _diagnosticTools.GetTools().ToList(),
            ClientName = "TroubleScout",
            OnPermissionRequest = (req, inv) =>
            {
                // Read-only operations and our own custom tools are always auto-approved
                var kind = NormalizePermissionKind(req.Kind);
                if (kind is "read" or "url" or "custom-tool")
                {
                    return Task.FromResult(new PermissionRequestResult
                    {
                        Kind = PermissionRequestResultKind.Approved
                    });
                }

                // In YOLO mode, approve everything (read live value so /mode changes take effect)
                if (_executionMode == ExecutionMode.Yolo)
                {
                    return Task.FromResult(new PermissionRequestResult
                    {
                        Kind = PermissionRequestResultKind.Approved
                    });
                }

                if (kind == "shell")
                {
                    var shellAssessment = EvaluateShellPermissionRequest(req);
                    if (shellAssessment != null)
                    {
                        if (shellAssessment.Validation.IsAllowed && !shellAssessment.Validation.RequiresApproval)
                        {
                            return Task.FromResult(new PermissionRequestResult
                            {
                                Kind = PermissionRequestResultKind.Approved
                            });
                        }

                        if (!shellAssessment.Validation.IsAllowed && !shellAssessment.Validation.RequiresApproval)
                        {
                            return Task.FromResult(new PermissionRequestResult
                            {
                                Kind = PermissionRequestResultKind.DeniedInteractivelyByUser
                            });
                        }

                        var shellApproval = ConsoleUI.PromptCommandApproval(
                            shellAssessment.Command,
                            shellAssessment.PromptReason,
                            impact: shellAssessment.ImpactText);
                        return Task.FromResult(new PermissionRequestResult
                        {
                            Kind = shellApproval == ApprovalResult.Approved
                                ? PermissionRequestResultKind.Approved
                                : PermissionRequestResultKind.DeniedInteractivelyByUser
                        });
                    }
                }

                // In Safe mode: MCP, shell, file-write require user approval
                var description = DescribePermissionRequest(req);
                var approval = ConsoleUI.PromptCommandApproval(
                    description,
                    BuildPermissionPromptReason(kind));
                return Task.FromResult(new PermissionRequestResult
                {
                    Kind = approval == ApprovalResult.Approved
                        ? PermissionRequestResultKind.Approved
                        : PermissionRequestResultKind.DeniedInteractivelyByUser
                });
            }
        };
    }

    internal ShellPermissionAssessment? EvaluateShellPermissionRequest(PermissionRequest request)
    {
        if (NormalizePermissionKind(request.Kind) != "shell")
        {
            return null;
        }

        var fullCommand = TryReadShellCommandText(request, truncateForDisplay: false);
        if (string.IsNullOrWhiteSpace(fullCommand) || !LooksLikePowerShellCommand(fullCommand))
        {
            return null;
        }

        var validation = PowerShellExecutor.ValidateStandaloneCommand(fullCommand, _executionMode, _configuredSafeCommands);
        return new ShellPermissionAssessment(
            TrimPermissionPreview(fullCommand),
            validation,
            validation.Reason ?? BuildPermissionPromptReason("shell"),
            BuildShellPermissionImpactText(validation));
    }

    private static string NormalizePermissionKind(string? kind)
    {
        var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "file-read" => "read",
            "file-write" => "write",
            "url-fetch" => "url",
            _ => normalized
        };
    }

    private static string DescribePermissionRequest(PermissionRequest request)
    {
        var kind = NormalizePermissionKind(request.Kind);
        var extensionData = request.ExtensionData;

        switch (kind)
        {
            case "shell":
            {
                var command = TryReadShellCommandText(request, truncateForDisplay: true);
                return !string.IsNullOrWhiteSpace(command)
                    ? command
                    : "Shell command";
            }
            case "mcp":
            {
                var serverName = ReadPermissionExtensionString(extensionData, "mcpServerName", "serverName", "server", "name");
                var toolName = ReadPermissionExtensionString(extensionData, "toolName", "tool", "method");
                var arguments = ReadPermissionExtensionString(extensionData, "arguments", "params", "input");

                var target = string.IsNullOrWhiteSpace(serverName)
                    ? toolName
                    : string.IsNullOrWhiteSpace(toolName)
                        ? serverName
                        : $"{serverName}/{toolName}";

                if (!string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(arguments))
                {
                    return TrimPermissionPreview($"{target} {arguments}");
                }

                return !string.IsNullOrWhiteSpace(target)
                    ? target
                    : "MCP tool invocation";
            }
            case "write":
            {
                var path = ReadPermissionExtensionString(extensionData, "path", "filePath", "target", "uri");
                return !string.IsNullOrWhiteSpace(path)
                    ? $"Write file: {path}"
                    : "File write";
            }
            case "read":
            {
                var path = ReadPermissionExtensionString(extensionData, "path", "filePath", "target", "uri");
                return !string.IsNullOrWhiteSpace(path)
                    ? $"Read file: {path}"
                    : "File read";
            }
            case "url":
            {
                var url = ReadPermissionExtensionString(extensionData, "url", "uri");
                return !string.IsNullOrWhiteSpace(url)
                    ? $"Fetch URL: {url}"
                    : "URL fetch";
            }
            case "custom-tool":
            {
                var toolName = ReadPermissionExtensionString(extensionData, "toolName", "tool", "name");
                var arguments = ReadPermissionExtensionString(extensionData, "arguments", "params", "input");

                if (!string.IsNullOrWhiteSpace(toolName) && !string.IsNullOrWhiteSpace(arguments))
                {
                    return TrimPermissionPreview($"{toolName} {arguments}");
                }

                return !string.IsNullOrWhiteSpace(toolName)
                    ? toolName
                    : "Custom tool invocation";
            }
            default:
            {
                var preview = ReadPermissionExtensionString(extensionData, "command", "toolName", "path", "url", "uri");
                return !string.IsNullOrWhiteSpace(preview)
                    ? TrimPermissionPreview(preview)
                    : $"Tool operation ({kind})";
            }
        }
    }

    private static string BuildPermissionPromptReason(string kind)
    {
        return kind switch
        {
            "mcp" => "Allow this MCP tool invocation in Safe mode?",
            "shell" => "Allow this shell command in Safe mode?",
            "write" => "Allow this file write in Safe mode?",
            "read" => "Allow this file read?",
            "url" => "Allow this URL fetch?",
            "custom-tool" => "Allow this custom tool invocation?",
            _ => $"Allow this tool operation in Safe mode? (kind: {Markup.Escape(kind)})"
        };
    }

    private static string BuildShellPermissionImpactText(CommandValidation validation)
    {
        if (!validation.IsAllowed && !validation.RequiresApproval)
        {
            return "This PowerShell command is blocked by TroubleScout safety rules.";
        }

        if (validation.RequiresApproval)
        {
            if (validation.Reason?.Contains("parse", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "This PowerShell command could not be confidently classified as read-only.";
            }

            return "This PowerShell command is not classified as read-only and may modify system state, services, or configuration.";
        }

        return "This PowerShell command was recognized as read-only.";
    }

    private static string? TryReadShellCommandText(PermissionRequest request, bool truncateForDisplay)
    {
        var command = ReadStringProperty(request, "FullCommandText", "Command", "CommandLine")
            ?? ReadRawPermissionExtensionString(request.ExtensionData,
                "fullCommandText",
                "command",
                "commandLine",
                "commandText",
                "cmd",
                "shellCommand",
                "rawCommand",
                "text")
            ?? ReadNestedRawPermissionExtensionString(request.ExtensionData,
                "command",
                "payload",
                "input",
                "request",
                "details");

        return string.IsNullOrWhiteSpace(command)
            ? null
            : truncateForDisplay
                ? TrimPermissionPreview(command)
                : command.Trim();
    }

    private static bool LooksLikePowerShellCommand(string command)
    {
        var trimmed = command.TrimStart();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed.StartsWith("$", StringComparison.Ordinal) ||
            trimmed.StartsWith("@(", StringComparison.Ordinal) ||
            trimmed.StartsWith("@{", StringComparison.Ordinal) ||
            trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return true;
        }

        var firstCommandSegment = trimmed
            .Split(['|', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .FirstOrDefault();

        var firstToken = firstCommandSegment?
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstToken) || firstToken.StartsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        return Regex.IsMatch(firstToken, "^[A-Za-z][A-Za-z0-9]*-[A-Za-z][A-Za-z0-9]*$");
    }

    private static string? ReadRawPermissionExtensionString(
        IReadOnlyDictionary<string, object>? extensionData,
        params string[] candidateKeys)
    {
        if (extensionData == null || extensionData.Count == 0)
            return null;

        foreach (var candidateKey in candidateKeys)
        {
            if (!TryGetExtensionValue(extensionData, candidateKey, out var value))
                continue;

            var text = ConvertPermissionExtensionValueToRawString(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? ReadNestedRawPermissionExtensionString(
        IReadOnlyDictionary<string, object>? extensionData,
        params string[] candidateKeys)
    {
        if (extensionData == null || extensionData.Count == 0)
            return null;

        foreach (var candidateKey in candidateKeys)
        {
            if (!TryGetExtensionValue(extensionData, candidateKey, out var value))
                continue;

            if (value == null)
                continue;

            var text = ExtractNestedRawCommandText(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? ReadPermissionExtensionString(
        IReadOnlyDictionary<string, object>? extensionData,
        params string[] candidateKeys)
    {
        if (extensionData == null || extensionData.Count == 0)
            return null;

        foreach (var candidateKey in candidateKeys)
        {
            if (!TryGetExtensionValue(extensionData, candidateKey, out var value))
                continue;

            var text = ConvertPermissionExtensionValueToString(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return TrimPermissionPreview(text);
            }
        }

        return null;
    }

    private static string? ReadNestedPermissionExtensionString(
        IReadOnlyDictionary<string, object>? extensionData,
        params string[] containerKeys)
    {
        if (extensionData == null || extensionData.Count == 0)
        {
            return null;
        }

        foreach (var containerKey in containerKeys)
        {
            if (!TryGetExtensionValue(extensionData, containerKey, out var value) || value == null)
            {
                continue;
            }

            var text = ExtractNestedCommandText(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return TrimPermissionPreview(text);
            }
        }

        return null;
    }

    private static string? ExtractNestedCommandText(object value)
    {
        if (value is JsonElement json)
        {
            return ExtractNestedCommandText(json);
        }

        if (value is IReadOnlyDictionary<string, object> readOnlyDictionary)
        {
            return ReadPermissionExtensionString(readOnlyDictionary,
                "fullCommandText",
                "command",
                "commandLine",
                "commandText",
                "cmd",
                "shellCommand",
                "rawCommand",
                "text");
        }

        if (value is IDictionary<string, object> dictionary)
        {
            return ReadPermissionExtensionString(
                dictionary.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase),
                "fullCommandText",
                "command",
                "commandLine",
                "commandText",
                "cmd",
                "shellCommand",
                "rawCommand",
                "text");
        }

        return null;
    }

    private static string? ExtractNestedRawCommandText(object value)
    {
        if (value is JsonElement json)
        {
            return ExtractNestedRawCommandText(json);
        }

        if (value is IReadOnlyDictionary<string, object> readOnlyDictionary)
        {
            return ReadRawPermissionExtensionString(readOnlyDictionary,
                "fullCommandText",
                "command",
                "commandLine",
                "commandText",
                "cmd",
                "shellCommand",
                "rawCommand",
                "text");
        }

        if (value is IDictionary<string, object> dictionary)
        {
            return ReadRawPermissionExtensionString(
                dictionary.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase),
                "fullCommandText",
                "command",
                "commandLine",
                "commandText",
                "cmd",
                "shellCommand",
                "rawCommand",
                "text");
        }

        return null;
    }

    private static string? ExtractNestedCommandText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return TrimSingleLine(element.GetString());
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in new[]
                 {
                     "fullCommandText", "command", "commandLine", "commandText", "cmd", "shellCommand", "rawCommand", "text"
                 })
        {
            if (TryGetJsonPropertyIgnoreCase(element, propertyName, out var value))
            {
                var text = ExtractNestedCommandText(value);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static string? ExtractNestedRawCommandText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in new[]
                 {
                     "fullCommandText", "command", "commandLine", "commandText", "cmd", "shellCommand", "rawCommand", "text"
                 })
        {
            if (TryGetJsonPropertyIgnoreCase(element, propertyName, out var value))
            {
                var text = ExtractNestedRawCommandText(value);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static bool TryGetExtensionValue(
        IReadOnlyDictionary<string, object> extensionData,
        string candidateKey,
        out object? value)
    {
        foreach (var entry in extensionData)
        {
            if (entry.Key.Equals(candidateKey, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string? ConvertPermissionExtensionValueToString(object? value)
    {
        string? rawText = value switch
        {
            null => null,
            string text => text,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            JsonElement json => json.GetRawText(),
            _ => value.ToString()
        };

        return string.IsNullOrWhiteSpace(rawText)
            ? null
            : TrimSingleLine(rawText);
    }

    private static string? ConvertPermissionExtensionValueToRawString(object? value)
    {
        string? rawText = value switch
        {
            null => null,
            string text => text,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            JsonElement json => ExtractNestedRawCommandText(json) ?? json.GetRawText(),
            _ => value.ToString()
        };

        return string.IsNullOrWhiteSpace(rawText)
            ? null
            : rawText.Trim();
    }

    private static string TrimPermissionPreview(string text)
    {
        const int maxLength = 180;

        var singleLine = TrimSingleLine(text);
        return singleLine.Length <= maxLength
            ? singleLine
            : singleLine[..maxLength].TrimEnd() + "...";
    }

    private async Task<bool> ReconnectAsync(string newServer, Action<string>? updateStatus = null)
    {
        if (string.IsNullOrWhiteSpace(newServer))
        {
            ConsoleUI.ShowWarning("Server name cannot be empty");
            return false;
        }

        newServer = newServer.Trim();

        if (newServer.Equals(_targetServer, StringComparison.OrdinalIgnoreCase))
        {
            if (_startupJeaFocusActive
                && _targetServer.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && !EffectiveTargetServer.Equals(_targetServer, StringComparison.OrdinalIgnoreCase))
            {
                _startupJeaFocusActive = false;
                _systemMessageConfig = CreateSystemMessage(_targetServer, _additionalExecutors.Keys.ToList());

                if (_copilotClient != null && _copilotSession != null)
                {
                    updateStatus?.Invoke("Refreshing AI session...");
                    await _copilotSession.DisposeAsync();
                    _copilotSession = null;

                    var modelToUse = string.IsNullOrWhiteSpace(_selectedModel) ? null : _selectedModel;
                    if (!await CreateCopilotSessionAsync(modelToUse, updateStatus))
                    {
                        return false;
                    }
                }

                ConsoleUI.ShowInfo("Primary focus reset to localhost.");
                return true;
            }

            ConsoleUI.ShowInfo($"Already connected to {newServer}");
            return true;
        }

        updateStatus?.Invoke("Closing current PowerShell session...");
        _executor.Dispose();

        _startupJeaFocusActive = false;
        _targetServer = newServer;
        _systemMessageConfig = CreateSystemMessage(_targetServer, _additionalExecutors.Keys.ToList());
        _executor = new PowerShellExecutor(_targetServer);
        _executor.ExecutionMode = _executionMode;
        _executor.SetCustomSafeCommands(_configuredSafeCommands);
        _diagnosticTools = new DiagnosticTools(_executor, PromptApprovalAsync, _targetServer, RecordCommandAction,
            s => ConnectAdditionalServerAsync(s), GetExecutorForServer, CloseAdditionalServerSessionAsync,
            (serverName, configurationName) => ConnectJeaServerAsync(serverName, configurationName));

        updateStatus?.Invoke($"Connecting to {_targetServer}...");
        var (connectionSuccess, connectionError) = await _executor.TestConnectionAsync();
        if (!connectionSuccess)
        {
            ConsoleUI.ShowError("Connection Failed", connectionError ?? $"Unable to connect to {_targetServer}");
            return false;
        }

        if (_copilotClient != null)
        {
            if (_copilotSession != null)
            {
                updateStatus?.Invoke("Closing AI session...");
                await _copilotSession.DisposeAsync();
                _copilotSession = null;
            }

            var modelToUse = string.IsNullOrWhiteSpace(_selectedModel) ? null : _selectedModel;

            if (!await CreateCopilotSessionAsync(modelToUse, updateStatus))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<(bool Success, string? Error)> ConnectAdditionalServerAsync(string serverName, bool skipApproval = false)
    {
        if (string.IsNullOrWhiteSpace(serverName))
            return (false, "Server name cannot be empty.");

        if (serverName.Equals(_targetServer, StringComparison.OrdinalIgnoreCase))
            return (true, null);

        if (_additionalExecutors.ContainsKey(serverName))
            return (true, null);

        if (!skipApproval && _executionMode == ExecutionMode.Safe)
        {
            var approval = ConsoleUI.PromptCommandApproval(
                $"New-PSSession -ComputerName '{serverName}'",
                $"TroubleScout wants to establish a direct PowerShell session to {serverName}");
            if (approval != ApprovalResult.Approved)
                return (false, $"Connection to {serverName} was denied by user.");
        }

        var executor = new PowerShellExecutor(serverName);
        executor.ExecutionMode = _executionMode;
        executor.SetCustomSafeCommands(_configuredSafeCommands);
        var (success, error) = await executor.TestConnectionAsync();
        if (!success)
        {
            executor.Dispose();
            return (false, error ?? $"Failed to connect to {serverName}");
        }

        _additionalExecutors[serverName] = executor;
        // Update system message configuration so future Copilot sessions include this additional server
        _systemMessageConfig = CreateSystemMessage(_targetServer, _additionalExecutors.Keys.ToList());
        return (true, null);
    }

    private async Task<(bool Success, string? Error)> ConnectJeaServerAsync(
        string serverName,
        string configurationName,
        bool skipApproval = false)
    {
        if (string.IsNullOrWhiteSpace(serverName))
            return (false, "Server name cannot be empty.");

        if (string.IsNullOrWhiteSpace(configurationName))
            return (false, "Configuration name cannot be empty.");

        serverName = serverName.Trim();
        configurationName = configurationName.Trim();

        if (serverName.Equals(_targetServer, StringComparison.OrdinalIgnoreCase))
            return (false, "The primary target server cannot be replaced with a JEA session. Use /server first if you want to change the primary target.");

        // JEA requires a remote connection — localhost creates an unconstrained local runspace
        if (PowerShellExecutor.IsLocalhostName(serverName))
            return (false, "JEA connections require a remote server. Use a remote hostname, not localhost.");

        if (_additionalExecutors.TryGetValue(serverName, out var existingExecutor))
        {
            if (existingExecutor.IsJeaSession &&
                string.Equals(existingExecutor.ConfigurationName, configurationName, StringComparison.OrdinalIgnoreCase))
            {
                return (true, null);
            }

            return (false, $"A session named '{serverName}' is already connected. Close it before connecting a different JEA configuration.");
        }

        if (!skipApproval && _executionMode == ExecutionMode.Safe)
        {
            var approval = ConsoleUI.PromptCommandApproval(
                $"New-PSSession -ComputerName '{serverName}' -ConfigurationName '{configurationName}'",
                $"TroubleScout wants to establish a constrained JEA session to {serverName} using configuration {configurationName}");
            if (approval != ApprovalResult.Approved)
                return (false, $"JEA connection to {serverName} was denied by user.");
        }

        var executor = new PowerShellExecutor(serverName, configurationName);
        executor.ExecutionMode = _executionMode;
        executor.SetCustomSafeCommands(_configuredSafeCommands);

        try
        {
            var (success, error) = await executor.TestConnectionAsync();
            if (!success)
            {
                executor.Dispose();
                return (false, error ?? $"Failed to connect to JEA endpoint '{configurationName}' on {serverName}");
            }

            await executor.DiscoverJeaCommandsAsync();
            _additionalExecutors[serverName] = executor;
            _systemMessageConfig = CreateSystemMessage(_targetServer, _additionalExecutors.Keys.ToList());
            return (true, null);
        }
        catch (Exception ex)
        {
            executor.Dispose();
            return (false, ex.Message);
        }
    }

    private PowerShellExecutor? GetExecutorForServer(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName) ||
            serverName.Equals(_targetServer, StringComparison.OrdinalIgnoreCase))
            return _executor;

        _additionalExecutors.TryGetValue(serverName, out var exec);
        return exec;
    }

    private Task<bool> CloseAdditionalServerSessionAsync(string serverName)
    {
        if (!_additionalExecutors.TryGetValue(serverName, out var executor))
            return Task.FromResult(false);

        _additionalExecutors.Remove(serverName); // remove first
        try { executor.Dispose(); }
        catch { /* swallow - best effort disposal */ }
        return Task.FromResult(true);
    }

    private void SetExecutionMode(ExecutionMode mode)
    {
        _executionMode = mode;
        _executor.ExecutionMode = mode;
        foreach (var exec in _additionalExecutors.Values)
            exec.ExecutionMode = mode;
    }

    internal StatusBarInfo BuildStatusBarInfo()
    {
        var inputTokens = _lastUsage?.InputTokens ?? _lastUsage?.PromptTokens;
        var outputTokens = _lastUsage?.OutputTokens ?? _lastUsage?.CompletionTokens;
        var totalTokens = _lastUsage?.TotalTokens;

        return new StatusBarInfo(
            Model: SelectedModel,
            Provider: ActiveProviderDisplayName,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            TotalTokens: totalTokens,
            ToolInvocations: _toolInvocationCount,
            SessionId: _sessionId)
        {
            SessionInputTokens = _sessionUsageTracker.TotalInputTokens > 0 ? _sessionUsageTracker.TotalInputTokens : null,
            SessionOutputTokens = _sessionUsageTracker.TotalOutputTokens > 0 ? _sessionUsageTracker.TotalOutputTokens : null,
            SessionCostEstimate = _sessionUsageTracker.GetCostEstimateDisplay()
        };
    }

    public IReadOnlyList<(string Label, string Value)> GetStatusFields()
    {
        var fields = new List<(string Label, string Value)>();

        // -- Provider section --
        fields.Add((UI.ConsoleUI.StatusSectionSeparator, "Provider"));
        fields.Add(("Provider", ActiveProviderDisplayName));
        fields.Add(("Auth mode", _useByokOpenAi ? "BYOK (OpenAI)" : "GitHub Copilot"));
        fields.Add(("GitHub auth", _isGitHubCopilotAuthenticated ? "Authenticated" : "Not authenticated"));
        fields.Add(("BYOK", !string.IsNullOrWhiteSpace(_byokOpenAiApiKey) && LooksLikeUrl(_byokOpenAiBaseUrl) ? "Configured" : "Not configured"));
        fields.Add(("Session ID", _sessionId));

        // -- Usage section --
        if (_toolInvocationCount > 0 || (_lastUsage != null && _lastUsage.HasAny))
        {
            fields.Add((UI.ConsoleUI.StatusSectionSeparator, "Usage"));
        }

        if (_toolInvocationCount > 0)
        {
            fields.Add(("Tools used", _toolInvocationCount.ToString()));
        }

        if (_lastUsage != null && _lastUsage.HasAny)
        {
            AddUsageField(fields, "Prompt tokens", _lastUsage.PromptTokens);
            AddUsageField(fields, "Completion tokens", _lastUsage.CompletionTokens);
            AddUsageField(fields, "Total tokens", _lastUsage.TotalTokens);
            AddUsageField(fields, "Input tokens", _lastUsage.InputTokens);
            AddUsageField(fields, "Output tokens", _lastUsage.OutputTokens);
            AddContextUsageField(fields, _lastUsage.UsedContextTokens, _lastUsage.MaxContextTokens);
        }

        // -- Capabilities section --
        var hasMcpOrSkills =
            _configuredMcpServers.Any(v => !string.IsNullOrWhiteSpace(v))
            || _runtimeMcpServers.Count > 0
            || _configuredSkills.Any(v => !string.IsNullOrWhiteSpace(v))
            || _runtimeSkills.Count > 0
            || _configurationWarnings.Count > 0;

        if (hasMcpOrSkills)
        {
            fields.Add((UI.ConsoleUI.StatusSectionSeparator, "Capabilities"));
        }

        AddCapabilityField(fields, "MCP configured", _configuredMcpServers);
        AddCapabilityField(fields, "MCP used", _runtimeMcpServers.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        AddCapabilityField(fields, "Skills configured", _configuredSkills);
        AddCapabilityField(fields, "Skills used", _runtimeSkills.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

        if (_configurationWarnings.Count > 0)
        {
            fields.Add(("Capability warnings", string.Join(" | ", _configurationWarnings)));
        }

        return fields;
    }

    /// <summary>
    /// Returns additional targets for the status panel display, excluding the effective primary.
    /// When a startup JEA session is the effective primary, localhost is excluded.
    /// </summary>
    private IReadOnlyList<string>? GetAdditionalTargetsForDisplay()
    {
        var all = EffectiveTargetServers;
        return all.Count > 1 ? all.Skip(1).ToList() : null;
    }

    private static void AddUsageField(List<(string Label, string Value)> fields, string label, int? value)
    {
        if (!value.HasValue)
            return;

        fields.Add((label, value.Value.ToString("N0", CultureInfo.InvariantCulture)));
    }

    private static void AddContextUsageField(List<(string Label, string Value)> fields, int? usedContext, int? maxContext)
    {
        if (!usedContext.HasValue || !maxContext.HasValue || maxContext.Value <= 0)
        {
            return;
        }

        var percentage = usedContext.Value * 100d / maxContext.Value;
        var value = $"{usedContext.Value.ToString("N0", CultureInfo.InvariantCulture)}/{maxContext.Value.ToString("N0", CultureInfo.InvariantCulture)} ({percentage.ToString("0.#", CultureInfo.InvariantCulture)}%)";
        fields.Add(("Context", value));
    }

    private static void AddCapabilityField(List<(string Label, string Value)> fields, string label, IEnumerable<string> values)
    {
        var distinct = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinct.Count == 0)
            return;

        const int maxItems = 2;
        var shown = distinct.Take(maxItems);
        var value = string.Join(", ", shown);
        if (distinct.Count > maxItems)
        {
            value += $" (+{distinct.Count - maxItems} more)";
        }

        fields.Add((label, value));
    }

    private ByokPriceInfo? GetActiveByokPricing()
    {
        if (!_useByokOpenAi || string.IsNullOrWhiteSpace(_selectedModel))
        {
            return null;
        }

        return _byokPricing.TryGetValue(_selectedModel, out var price) ? price : null;
    }

    private double? GetActivePremiumMultiplier()
    {
        if (_useByokOpenAi)
        {
            return null;
        }

        var model = GetSelectedModelInfo();
        return model?.Billing?.Multiplier;
    }

    private void CaptureUsageMetrics(AssistantUsageEvent usageEvt)
    {
        var data = usageEvt.Data;
        if (data == null)
            return;

        var usageObj = GetPropertyValue(data, "Usage") ?? data;

        var promptTokens = ReadIntProperty(usageObj, "PromptTokens", "InputTokens", "RequestTokens");
        var completionTokens = ReadIntProperty(usageObj, "CompletionTokens", "OutputTokens", "ResponseTokens");
        var totalTokens = ReadIntProperty(usageObj, "TotalTokens", "Tokens");

        var inputTokens = ReadIntProperty(usageObj, "InputTokens", "PromptTokens", "RequestTokens");
        var outputTokens = ReadIntProperty(usageObj, "OutputTokens", "CompletionTokens", "ResponseTokens");

        var usedContext = ReadIntProperty(usageObj, "UsedTokens", "ContextTokensUsed", "ContextTokens", "UsedContextTokens");
        var maxContext = ReadIntProperty(usageObj, "MaxTokens", "MaxContextTokens", "ContextWindowTokens", "ContextTokensMax");
        var freeContext = ReadIntProperty(usageObj, "FreeTokens", "RemainingTokens", "ContextTokensRemaining");

        if (maxContext.HasValue && usedContext.HasValue && !freeContext.HasValue)
        {
            freeContext = Math.Max(0, maxContext.Value - usedContext.Value);
        }

        var snapshot = new CopilotUsageSnapshot(
            promptTokens,
            completionTokens,
            totalTokens,
            inputTokens,
            outputTokens,
            maxContext,
            usedContext,
            freeContext);

        if (snapshot.HasAny)
        {
            _lastUsage = snapshot;

            var pricing = GetActiveByokPricing();
            var multiplier = GetActivePremiumMultiplier();
            _sessionUsageTracker.RecordTurn(
                snapshot.InputTokens ?? snapshot.PromptTokens,
                snapshot.OutputTokens ?? snapshot.CompletionTokens,
                pricing,
                multiplier);
        }
    }

    private void CaptureCapabilityUsage(SessionEvent evt)
    {
        if (evt is ToolExecutionStartEvent toolStart)
        {
            var mcpServerName = ReadStringProperty(toolStart.Data, "McpServerName", "MCPServerName", "ServerName");
            if (!string.IsNullOrWhiteSpace(mcpServerName))
            {
                _runtimeMcpServers.Add(mcpServerName);
            }
        }

        if (string.Equals(evt.Type, "skill.invoked", StringComparison.OrdinalIgnoreCase))
        {
            var eventData = GetPropertyValue(evt, "Data");
            var skillName = ReadStringProperty(eventData, "Name", "SkillName", "Id");
            if (!string.IsNullOrWhiteSpace(skillName))
            {
                _runtimeSkills.Add(skillName);
            }
        }
    }

    private static string? ReadStringProperty(object? instance, params string[] propertyNames)
    {
        if (instance == null)
            return null;

        foreach (var propertyName in propertyNames)
        {
            var prop = instance.GetType().GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var value = prop?.GetValue(instance);
            if (value is string stringValue && !string.IsNullOrWhiteSpace(stringValue))
            {
                return stringValue.Trim();
            }
        }

        return null;
    }

    private void ResetCapabilities()
    {
        _configuredMcpServers.Clear();
        _configuredSkills.Clear();
        _runtimeMcpServers.Clear();
        _runtimeSkills.Clear();
        _configurationWarnings.Clear();
    }

    private string CreateSessionId()
    {
        var sequence = Interlocked.Increment(ref _sessionCounter);
        return $"TS-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{sequence:D3}";
    }

    private static string ReadSkillNameFromManifest(string manifestPath)
    {
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                var name = nameElement.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
        }
        catch
        {
            // Ignore malformed manifest; caller falls back to folder name.
        }

        return string.Empty;
    }

    private void DiscoverConfiguredSkills()
    {
        var discoveredSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in _skillDirectories)
        {
            if (!Directory.Exists(directory))
            {
                _configurationWarnings.Add($"Skills directory not found: {directory}");
                continue;
            }

            foreach (var skillDir in Directory.GetDirectories(directory))
            {
                var skillMarkdown = Path.Combine(skillDir, "SKILL.md");
                var skillManifest = Path.Combine(skillDir, "skill.json");
                if (!File.Exists(skillMarkdown) && !File.Exists(skillManifest))
                {
                    continue;
                }

                var skillName = File.Exists(skillManifest)
                    ? ReadSkillNameFromManifest(skillManifest)
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(skillName))
                {
                    skillName = Path.GetFileName(skillDir);
                }

                if (!_disabledSkills.Contains(skillName, StringComparer.OrdinalIgnoreCase))
                {
                    discoveredSkills.Add(skillName);
                }
            }
        }

        _configuredSkills.Clear();
        _configuredSkills.AddRange(discoveredSkills);
        _configuredSkills.Sort(StringComparer.OrdinalIgnoreCase);
    }

    internal static Dictionary<string, object> LoadMcpServersFromConfig(string? mcpConfigPath, List<string> warnings)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(mcpConfigPath))
        {
            return result;
        }

        if (!File.Exists(mcpConfigPath))
        {
            warnings.Add($"MCP config file not found: {mcpConfigPath}");
            return result;
        }

        try
        {
            using var stream = File.OpenRead(mcpConfigPath);
            using var document = JsonDocument.Parse(stream);

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("MCP config root must be a JSON object.");
                return result;
            }

            JsonElement serversElement;
            if (!root.TryGetProperty("mcpServers", out serversElement) &&
                !root.TryGetProperty("servers", out serversElement))
            {
                warnings.Add("MCP config does not contain 'mcpServers' or 'servers'.");
                return result;
            }

            if (serversElement.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("MCP config 'mcpServers' or 'servers' must be a JSON object.");
                return result;
            }

            foreach (var property in serversElement.EnumerateObject())
            {
                var mapped = TryMapMcpServer(property.Name, property.Value, out var warning);
                if (mapped == null)
                {
                    if (!string.IsNullOrWhiteSpace(warning))
                    {
                        warnings.Add(warning);
                    }

                    continue;
                }

                result[property.Name] = mapped;
            }
        }
        catch (JsonException ex)
        {
            warnings.Add($"MCP config JSON parse error: {TrimSingleLine(ex.Message)}");
        }
        catch (Exception ex)
        {
            warnings.Add($"MCP config load failed: {TrimSingleLine(ex.Message)}");
        }

        return result;
    }

    private static object? TryMapMcpServer(string serverName, JsonElement serverElement, out string? warning)
    {
        warning = null;

        if (serverElement.ValueKind != JsonValueKind.Object)
        {
            warning = $"Skipping MCP server '{serverName}': entry must be an object.";
            return null;
        }

        var type = GetOptionalString(serverElement, "type")?.Trim().ToLowerInvariant();
        if (type is "http" or "sse")
        {
            var url = GetOptionalString(serverElement, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                warning = $"Skipping MCP server '{serverName}': remote server requires 'url'.";
                return null;
            }

            var remote = new McpRemoteServerConfig
            {
                Type = type!,
                Url = url!
            };

            var headers = GetStringDictionary(serverElement, "headers");
            if (headers != null)
            {
                remote.Headers = headers;
            }

            var remoteTools = GetStringList(serverElement, "tools");
            if (remoteTools != null)
            {
                remote.Tools = remoteTools;
            }

            var remoteTimeout = GetOptionalInt(serverElement, "timeout");
            if (remoteTimeout.HasValue)
            {
                remote.Timeout = remoteTimeout.Value;
            }

            return remote;
        }

        var command = GetOptionalString(serverElement, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            warning = $"Skipping MCP server '{serverName}': local/stdio server requires 'command'.";
            return null;
        }

        var local = new McpLocalServerConfig
        {
            Type = string.IsNullOrWhiteSpace(type) ? "local" : type!,
            Command = command!
        };

        var args = GetStringList(serverElement, "args");
        if (args != null)
        {
            local.Args = args;
        }

        var env = GetStringDictionary(serverElement, "env");
        if (env != null)
        {
            local.Env = env;
        }

        var cwd = GetOptionalString(serverElement, "cwd");
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            local.Cwd = cwd;
        }

        var localTools = GetStringList(serverElement, "tools");
        if (localTools != null)
        {
            local.Tools = localTools;
        }

        var localTimeout = GetOptionalInt(serverElement, "timeout");
        if (localTimeout.HasValue)
        {
            local.Timeout = localTimeout.Value;
        }

        return local;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static int? GetOptionalInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return property.TryGetInt32(out var value) ? value : null;
    }

    private static List<string>? GetStringList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                list.Add(value);
            }
        }

        return list.Count == 0 ? null : list;
    }

    private static Dictionary<string, string>? GetStringDictionary(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in property.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                dict[item.Name] = value!;
            }
        }

        return dict.Count == 0 ? null : dict;
    }

    private static object? GetPropertyValue(object instance, string propertyName)
    {
        var prop = instance.GetType().GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return prop?.GetValue(instance);
    }

    private static bool TryGetJsonPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName) || property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? ReadJsonStringProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryGetJsonPropertyIgnoreCase(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }
        }

        return null;
    }

    private static List<string> ReadJsonStringArrayProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetJsonPropertyIgnoreCase(element, propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var result = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;

                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(text.Trim());
                }
            }

            return result;
        }

        return [];
    }

    private static int? ReadJsonIntProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetJsonPropertyIgnoreCase(element, propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number)
            {
                if (value.TryGetInt32(out var intValue))
                    return intValue;
                if (value.TryGetInt64(out var longValue))
                    return (int)longValue;
                if (value.TryGetDouble(out var doubleValue))
                    return (int)doubleValue;
            }

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static double? ReadJsonDoubleProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetJsonPropertyIgnoreCase(element, propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var doubleValue))
            {
                return doubleValue;
            }

            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static decimal? ReadJsonDecimalProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetJsonPropertyIgnoreCase(element, propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var decimalValue))
            {
                return decimalValue;
            }

            if (value.ValueKind == JsonValueKind.String
                && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool? ReadJsonBoolProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetJsonPropertyIgnoreCase(element, propertyName, out var value))
                continue;

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            if (value.ValueKind == JsonValueKind.String
                && bool.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static ModelCapabilities? BuildByokCapabilities(JsonElement modelElement)
    {
        JsonElement supportsSource = modelElement;
        JsonElement limitsSource = modelElement;

        if (TryGetJsonPropertyIgnoreCase(modelElement, "capabilities", out var capabilitiesElement)
            && capabilitiesElement.ValueKind == JsonValueKind.Object)
        {
            if (TryGetJsonPropertyIgnoreCase(capabilitiesElement, "supports", out var supportsElement)
                && supportsElement.ValueKind == JsonValueKind.Object)
            {
                supportsSource = supportsElement;
            }

            if (TryGetJsonPropertyIgnoreCase(capabilitiesElement, "limits", out var limitsElement)
                && limitsElement.ValueKind == JsonValueKind.Object)
            {
                limitsSource = limitsElement;
            }
        }

        var supportsVision = ReadJsonBoolProperty(supportsSource, "vision", "supports_vision", "supportsVision");
        var supportsReasoningEffort = ReadJsonBoolProperty(supportsSource, "reasoningEffort", "reasoning_effort", "supports_reasoning_effort", "supportsReasoningEffort");
        var maxPromptTokens = ReadJsonIntProperty(limitsSource, "max_prompt_tokens", "maxPromptTokens");
        var maxContextWindowTokens = ReadJsonIntProperty(limitsSource, "max_context_window_tokens", "maxContextWindowTokens", "context_window", "contextWindow");

        if (!supportsVision.HasValue
            && !supportsReasoningEffort.HasValue
            && !maxPromptTokens.HasValue
            && !maxContextWindowTokens.HasValue)
        {
            return null;
        }

        var capabilities = new ModelCapabilities();

        if (supportsVision.HasValue || supportsReasoningEffort.HasValue)
        {
            capabilities.Supports = new ModelSupports
            {
                Vision = supportsVision ?? false,
                ReasoningEffort = supportsReasoningEffort ?? false
            };
        }

        if (maxPromptTokens.HasValue || maxContextWindowTokens.HasValue)
        {
            capabilities.Limits = new ModelLimits
            {
                MaxPromptTokens = maxPromptTokens,
                MaxContextWindowTokens = maxContextWindowTokens ?? 0
            };
        }

        return capabilities;
    }

    private static ByokPriceInfo? ExtractByokPriceInfo(JsonElement modelElement)
    {
        var display = ReadJsonStringProperty(modelElement, "price_display", "priceDisplay", "pricing_display", "pricingDisplay");
        var input = ReadJsonDecimalProperty(modelElement, "input_price", "inputPrice", "prompt_price", "promptPrice", "input_per_million", "inputPricePerMillionTokens");
        var output = ReadJsonDecimalProperty(modelElement, "output_price", "outputPrice", "completion_price", "completionPrice", "output_per_million", "outputPricePerMillionTokens");

        if (TryGetJsonPropertyIgnoreCase(modelElement, "pricing", out var pricingElement)
            && pricingElement.ValueKind == JsonValueKind.Object)
        {
            display ??= ReadJsonStringProperty(pricingElement, "display", "label", "summary");
            input ??= ReadJsonDecimalProperty(pricingElement, "input", "input_price", "inputPrice", "prompt", "prompt_price", "promptPrice", "input_per_million", "inputPricePerMillionTokens");
            output ??= ReadJsonDecimalProperty(pricingElement, "output", "output_price", "outputPrice", "completion", "completion_price", "completionPrice", "output_per_million", "outputPricePerMillionTokens");
        }

        if (!input.HasValue && !output.HasValue && string.IsNullOrWhiteSpace(display))
        {
            return null;
        }

        display ??= FormatByokPriceDisplay(input, output);
        return new ByokPriceInfo(input, output, display);
    }

    private static string? FormatByokPriceDisplay(decimal? inputPrice, decimal? outputPrice)
    {
        if (inputPrice.HasValue && outputPrice.HasValue)
        {
            return $"${inputPrice.Value.ToString("0.####", CultureInfo.InvariantCulture)}/M in, ${outputPrice.Value.ToString("0.####", CultureInfo.InvariantCulture)}/M out";
        }

        if (inputPrice.HasValue)
        {
            return $"${inputPrice.Value.ToString("0.####", CultureInfo.InvariantCulture)}/M";
        }

        if (outputPrice.HasValue)
        {
            return $"${outputPrice.Value.ToString("0.####", CultureInfo.InvariantCulture)}/M";
        }

        return null;
    }

    private static string FormatByokPriceDisplayEstimate(decimal inputPrice, decimal outputPrice)
    {
        return $"~${inputPrice.ToString("0.####", CultureInfo.InvariantCulture)}/M in, ~${outputPrice.ToString("0.####", CultureInfo.InvariantCulture)}/M out";
    }

    private static int? ReadIntProperty(object? instance, params string[] propertyNames)
    {
        if (instance == null)
            return null;

        foreach (var name in propertyNames)
        {
            var prop = instance.GetType().GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null)
                continue;

            var value = prop.GetValue(instance);
            if (value == null)
                continue;

            if (value is int i)
                return i;
            if (value is long l)
                return (int)l;
            if (value is double d)
                return (int)d;
            if (value is float f)
                return (int)f;
        }

        return null;
    }
}
