using System.Globalization;
using System.Net;
using System.Reflection;
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

        internal static ModelSelectionEntry FromService(Services.ModelSelectionEntry entry) =>
            new(entry.ModelId, entry.DisplayName, (ModelSource)(int)entry.Source)
            {
                ProviderLabel = entry.ProviderLabel,
                RateLabel = entry.RateLabel,
                DetailSummary = entry.DetailSummary,
                IsCurrent = entry.IsCurrent
            };
    }

    internal sealed record ByokPriceInfo(decimal? InputPricePerMillionTokens, decimal? OutputPricePerMillionTokens, string? DisplayText)
    {
        internal static ByokPriceInfo FromService(Services.ByokPriceInfo priceInfo) =>
            new(priceInfo.InputPricePerMillionTokens, priceInfo.OutputPricePerMillionTokens, priceInfo.DisplayText);
    }

    internal enum ModelSwitchBehavior
    {
        CleanSession,
        SecondOpinion
    }
    internal sealed record ShellPermissionAssessment(string Command, CommandValidation Validation, string PromptReason, string ImpactText);

    private const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1";
    private const string OpenAiApiKeyEnvironmentVariable = "OPENAI_API_KEY";
    private const int MaxSecondOpinionTurns = 8;
    private const int MaxSecondOpinionPromptChars = 24_000;
    private const int MaxSecondOpinionUserPromptChars = 2_000;
    private const int MaxSecondOpinionReplyChars = 3_000;
    private const int MaxSecondOpinionCommandChars = 800;
    private const int MaxSecondOpinionToolOutputChars = 3_000;

    private string _targetServer;
    private PowerShellExecutor _executor;
    private DiagnosticTools _diagnosticTools;
    private readonly ServerConnectionManager _serverManager = new();
    private CopilotClient? _copilotClient;
    private CopilotSession? _copilotSession;
    private bool _isInitialized;
    private string? _selectedModel;
    private string? _copilotVersion;
    private readonly ModelDiscoveryManager _modelDiscovery = new();
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
    private readonly ByokProviderManager _byokProviderManager = new();
    private bool _modelExplicitlyRequested;
    private readonly ConversationHistoryTracker _historyTracker = new();
    private string _sessionId = "n/a";
    private int _sessionCounter;
    private int _toolInvocationCount;
    private bool _isGitHubCopilotAuthenticated;
    private IReadOnlyList<string>? _configuredSafeCommands;
    private IReadOnlyDictionary<string, string>? _configuredSystemPromptOverrides;
    private string? _configuredSystemPromptAppend;
    private string? _configuredReasoningEffort;
    private string? _selectedReasoningEffort;
    private string? _configuredMonitoringMcpServer;
    private string? _configuredTicketingMcpServer;
    private double? _sessionPremiumRequestCost;
    private readonly HashSet<string> _approvedUrlsForSession = new(StringComparer.OrdinalIgnoreCase);
    private bool _allowAllUrlsForSession;
    private readonly HashSet<string> _approvedMcpServersForSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _persistedSeededApprovals = new(StringComparer.OrdinalIgnoreCase);
    private AppSettings? _appSettings;

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
        "/mcp-role",
        "/mcp-approvals",
        "/model",
        "/reasoning",
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
        string? effectivePrimary = null;
        string? primaryJeaConfigName = null;
        PowerShellExecutor? primaryJeaExec = null;

        if (targetServer.Equals(_targetServer, StringComparison.OrdinalIgnoreCase)
            && TryGetEffectivePrimaryJeaSession(out var primaryJeaServerName, out var configurationName, out var jeaExecCandidate))
        {
            effectivePrimary = primaryJeaServerName;
            primaryJeaConfigName = configurationName;
            primaryJeaExec = jeaExecCandidate;
        }

        var settings = new AppSettings
        {
            SystemPromptOverrides = _configuredSystemPromptOverrides?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase),
            SystemPromptAppend = _configuredSystemPromptAppend,
            MonitoringMcpServer = _configuredMonitoringMcpServer,
            TicketingMcpServer = _configuredTicketingMcpServer
        };

        return SystemPromptBuilder.CreateSystemMessage(
            targetServer,
            additionalServerNames,
            effectivePrimary,
            primaryJeaConfigName,
            primaryJeaExec,
            _serverManager.Executors,
            settings,
            _executionMode);
    }

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
        _appSettings = settings;
        ApplySystemPromptSettings(settings.SystemPromptOverrides, settings.SystemPromptAppend);
        ApplyReasoningEffortSetting(settings.ReasoningEffort);
        _configuredMonitoringMcpServer = settings.MonitoringMcpServer;
        _configuredTicketingMcpServer = settings.TicketingMcpServer;
        SeedPersistedMcpApprovals(settings.PersistedApprovedMcpServers);
        _systemMessageConfig = CreateSystemMessage(_targetServer);
        _executor = new PowerShellExecutor(_targetServer);
        _executor.ExecutionMode = _executionMode;
        ApplySafeCommandsToAllExecutors(settings.SafeCommands);
        _diagnosticTools = CreateDiagnosticTools();
    }

    private static Services.ModelSource ToServiceModelSource(ModelSource source)
        => (Services.ModelSource)(int)source;

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

    private DiagnosticTools CreateDiagnosticTools() =>
        new(_executor, PromptApprovalAsync, _targetServer, RecordCommandAction,
            s => ConnectAdditionalServerAsync(s), GetExecutorForServer, CloseAdditionalServerSessionAsync,
            (serverName, configurationName) => ConnectJeaServerAsync(serverName, configurationName));

    private readonly string? _requestedModel;
    private bool _useByokOpenAi
    {
        get => _byokProviderManager.UseByokOpenAi;
        set => _byokProviderManager.UseByokOpenAi = value;
    }

    private bool _byokExplicitlyRequested
    {
        get => _byokProviderManager.ExplicitlyRequested;
        set => _byokProviderManager.ExplicitlyRequested = value;
    }

    private string _byokOpenAiBaseUrl
    {
        get => _byokProviderManager.BaseUrl;
        set => _byokProviderManager.BaseUrl = value;
    }

    private string? _byokOpenAiApiKey
    {
        get => _byokProviderManager.ApiKey;
        set => _byokProviderManager.ApiKey = value;
    }

    private static readonly Regex MutatingIntentRegex = new(
        "\\b(empty|clear|delete|remove|restart|stop|start|set|enable|disable|kill|format|reset|recycle\\s+bin|trash)\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PostAnalysisHeadingRegex = new(
        "^\\s{0,3}#{1,6}\\s*(diagnosis|findings|recommendation|recommendations|next steps|root cause)\\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
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
            && _serverManager.Executors.TryGetValue(jea.ServerName, out var candidate)
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
                return [serverName, .._serverManager.Executors.Keys
                    .Where(k => !k.Equals(serverName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];
            }

            return [_targetServer, .._serverManager.Executors.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];
        }
    }

    public string? DefaultSessionTarget =>
        TryGetEffectivePrimaryJeaSession(out _, out _, out _) ? _targetServer : null;

    public bool IsAiSessionReady => _copilotSession != null;
    public string SelectedModel => GetModelDisplayName(_selectedModel) ?? "default";
    public string ActiveProviderDisplayName => _useByokOpenAi ? "BYOK (OpenAI)" : "GitHub Copilot";
    public string? SelectedReasoningEffort => GetReasoningDisplay(GetSelectedModelInfo());
    public string CopilotVersion => _copilotVersion ?? "unknown";
    public IReadOnlyList<string> ConfiguredMcpServers => _configuredMcpServers;
    public IReadOnlyList<string> RuntimeMcpServers => _runtimeMcpServers.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    public IReadOnlyList<string> ConfiguredSkills => _configuredSkills;
    public IReadOnlyList<string> RuntimeSkills => _runtimeSkills.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    public IReadOnlyList<string> ConfigurationWarnings => _configurationWarnings;
    public ExecutionMode CurrentExecutionMode => _executionMode;
    public IReadOnlyList<string> AllTargetServers =>
        [_targetServer, .._serverManager.Executors.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];

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
            if (_serverManager.Executors.Count > 0)
            {
                _systemMessageConfig = CreateSystemMessage(_targetServer, _serverManager.Executors.Keys.ToList());
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
            var cliPath = CopilotCliResolver.TryResolvePreferredCopilotCliPath();

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
                var report = await CopilotCliResolver.ValidateCopilotPrerequisitesAsync();
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
            _modelDiscovery.AvailableModels = await GetMergedModelListAsync(cliPath);

            if (_modelDiscovery.AvailableModels.Count == 0)
            {
                await ShowCopilotInitializationFailureAsync(
                    "No models were returned by Copilot CLI. Ensure you are authenticated and your subscription has model access.",
                    includeDiagnostics: true);
                return false;
            }

            var effectiveModel = ResolveInitialSessionModel(_modelDiscovery.AvailableModels);
            if (!string.IsNullOrWhiteSpace(_requestedModel)
                && !string.Equals(effectiveModel, _requestedModel, StringComparison.OrdinalIgnoreCase)
                && _modelDiscovery.AvailableModels.All(m => m.Id != _requestedModel))
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
                var report = await CopilotCliResolver.ValidateCopilotPrerequisitesAsync();
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
        => _modelDiscovery.ResolveInitialSessionModel(_requestedModel, availableModels);

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

        if (_modelDiscovery.AvailableModels.Count == 0 || _modelDiscovery.AvailableModels.All(m => !m.Id.Equals(newModel, StringComparison.OrdinalIgnoreCase)))
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

        if (_modelDiscovery.AvailableModels.Count == 0 || _modelDiscovery.AvailableModels.All(m => !m.Id.Equals(entry.ModelId, StringComparison.OrdinalIgnoreCase)))
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
        => (ModelSource)(int)_modelDiscovery.ResolveTargetSource(modelId, _useByokOpenAi, _isGitHubCopilotAuthenticated);

    private string? GetModelDisplayName(string? modelId)
        => _modelDiscovery.GetModelDisplayName(modelId);

    private async Task<List<ModelInfo>> GetMergedModelListAsync(string? cliPath)
        => await _modelDiscovery.GetMergedModelListAsync(_copilotClient, cliPath, TryGetCliModelIdsAsync);

    private async Task<List<ModelInfo>> TryGetGitHubProviderModelsAsync()
        => await _modelDiscovery.TryGetGitHubProviderModelsAsync(_copilotClient, _isGitHubCopilotAuthenticated, TryGetCliModelIdsAsync);

    private void UpdateAvailableModels(IReadOnlyList<ModelInfo> githubModels, IReadOnlyList<ModelInfo> byokModels)
        => _modelDiscovery.UpdateAvailableModels(githubModels, byokModels);

    private IReadOnlyList<ModelSelectionEntry> GetModelSelectionEntries()
        => _modelDiscovery
            .GetModelSelectionEntries(
                _isGitHubCopilotAuthenticated,
                IsByokConfigured(),
                (model, displayBase, source) => _modelDiscovery.BuildModelSelectionEntry(
                    model,
                    displayBase,
                    source,
                    _modelDiscovery.GetModelRateLabel,
                    (entryModel, entrySource) => BuildModelDetailSummary(entryModel, (ModelSource)(int)entrySource),
                    (modelId, entrySource) => IsCurrentModelAndSource(modelId, (ModelSource)(int)entrySource)))
            .Select(ModelSelectionEntry.FromService)
            .ToList();

    private ModelSelectionEntry BuildModelSelectionEntry(ModelInfo model, string displayBase, ModelSource source)
        => ModelSelectionEntry.FromService(_modelDiscovery.BuildModelSelectionEntry(
            model,
            displayBase,
            ToServiceModelSource(source),
            (entryModel, entrySource) => GetModelRateLabel(entryModel, (ModelSource)(int)entrySource),
            (entryModel, entrySource) => BuildModelDetailSummary(entryModel, (ModelSource)(int)entrySource),
            (modelId, entrySource) => IsCurrentModelAndSource(modelId, (ModelSource)(int)entrySource)));

    private async Task RefreshAvailableModelsAsync()
        => await _modelDiscovery.RefreshAvailableModelsAsync(_copilotClient, _isGitHubCopilotAuthenticated, TryGetCliModelIdsAsync, TryGetByokProviderModelsAsync);

    private static string ToModelDisplayName(string modelId)
        => ModelDiscoveryManager.ToModelDisplayName(modelId);

    private bool IsByokConfigured()
    {
        return !string.IsNullOrWhiteSpace(_byokOpenAiApiKey) && LooksLikeUrl(_byokOpenAiBaseUrl);
    }

    private string GetModelRateLabel(ModelInfo model, ModelSource source)
        => _modelDiscovery.GetModelRateLabel(model, ToServiceModelSource(source));

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
            return (value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        if (value >= 1_000)
        {
            return (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    private ModelInfo? GetSelectedModelInfo()
        => _modelDiscovery.GetSelectedModelInfo(_selectedModel);

    private ModelInfo? GetModelInfo(string? modelId)
        => _modelDiscovery.GetModelInfo(modelId);

    private static string? NormalizeReasoningEffort(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }

        var normalized = reasoningEffort.Trim().ToLowerInvariant();
        return normalized is "auto" or "default" ? null : normalized;
    }

    private void ApplyReasoningEffortSetting(string? reasoningEffort)
    {
        _configuredReasoningEffort = NormalizeReasoningEffort(reasoningEffort);
    }

    private static bool SupportsReasoningEffort(ModelInfo? model) =>
        model?.Capabilities?.Supports?.ReasoningEffort == true;

    private static IReadOnlyList<string> GetSupportedReasoningEfforts(ModelInfo? model)
    {
        if (model?.SupportedReasoningEfforts is not { Count: > 0 })
        {
            return [];
        }

        return model.SupportedReasoningEfforts
            .Select(NormalizeReasoningEffort)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? GetDefaultReasoningEffort(ModelInfo? model) =>
        NormalizeReasoningEffort(model?.DefaultReasoningEffort);

    private string? ResolveConfiguredReasoningEffort(string? modelId)
    {
        var configured = NormalizeReasoningEffort(_configuredReasoningEffort);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var model = GetModelInfo(modelId);
        if (!SupportsReasoningEffort(model))
        {
            return null;
        }

        var supported = GetSupportedReasoningEfforts(model);
        if (supported.Count == 0 || supported.Contains(configured, StringComparer.OrdinalIgnoreCase))
        {
            return configured;
        }

        return null;
    }

    private string? GetReasoningDisplay(ModelInfo? model)
    {
        if (!SupportsReasoningEffort(model))
        {
            return null;
        }

        var active = NormalizeReasoningEffort(_selectedReasoningEffort);
        if (!string.IsNullOrWhiteSpace(active))
        {
            return active;
        }

        var configured = ResolveConfiguredReasoningEffort(model?.Id);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var defaultReasoning = GetDefaultReasoningEffort(model);
        return string.IsNullOrWhiteSpace(defaultReasoning)
            ? "auto"
            : $"auto ({defaultReasoning})";
    }

    private IReadOnlyList<(string Label, string Value)> GetSelectedModelDetails()
        => _modelDiscovery.GetSelectedModelDetails(_selectedModel, _useByokOpenAi, FormatCompactTokenCount, GetReasoningDisplay);

    private async Task<IReadOnlyList<string>> TryGetCliModelIdsAsync(string cliPath)
    {
        try
        {
            var (command, args) = CopilotCliResolver.BuildCopilotCommand(cliPath, "--help");
            var helpResult = await CopilotCliResolver.ProcessRunnerResolver(command, args);
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
        var warning = await CopilotCliResolver.DetectPowerShellVersionWarningAsync();
        if (!string.IsNullOrWhiteSpace(warning))
        {
            ConsoleUI.ShowWarning(warning);
        }
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
        var cliPath = CopilotCliResolver.CopilotCliPathResolver();
        var nodeRuntimeRequired = await CopilotCliResolver.CliPathRequiresNodeRuntimeAsync(cliPath);

        var (copilotCommand, copilotArguments) = CopilotCliResolver.BuildCopilotCommand(cliPath, "--version");
        var copilotVersion = await CopilotCliResolver.ProcessRunnerResolver(copilotCommand, copilotArguments);
        diagnostics.Add(FormatDiagnosticLine($"{copilotCommand} {copilotArguments}", copilotVersion));

        var nodeVersion = await CopilotCliResolver.ProcessRunnerResolver("node", "--version");
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
               $"  1. Install Node.js {CopilotCliResolver.MinSupportedNodeMajorVersion}+ (LTS): winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements\n" +
               "  2. Restart your terminal\n" +
                             $"  3. Install or update Copilot CLI: {CopilotCliResolver.CopilotCliInstallUrl}\n" +
               "  4. copilot login\n\n" +
               "References:\n" +
               $"- {CopilotCliResolver.CopilotCliRepoUrl}\n" +
                             $"- {CopilotCliResolver.CopilotCliInstallUrl}";

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
                                      $"  - Install/update Copilot CLI: {CopilotCliResolver.CopilotCliInstallUrl}\n" +
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
                   $"  1. Install Node.js {CopilotCliResolver.MinSupportedNodeMajorVersion}+ (LTS): winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements\n" +
                   "  2. Restart your terminal\n" +
                   $"  3. Install/update Copilot CLI: {CopilotCliResolver.CopilotCliInstallUrl}\n" +
                   "  4. copilot login";
        }

        var result = "TroubleScout could not initialize the Copilot session.\n\n" +
                     "Try:\n" +
                     "  - copilot --version\n" +
                     "  - copilot login\n" +
                     $"  - winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements\n" +
                     $"  - Install/update Copilot CLI: {CopilotCliResolver.CopilotCliInstallUrl}";

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
        => await SendMessageAsync(
            userMessage,
            promptIndexOverride: null,
            cancellationToken,
            showPostAnalysisActionPrompt: false,
            forcePostAnalysisActionPrompt: false);

    private async Task<bool> SendMessageAsync(
        string userMessage,
        int? promptIndexOverride,
        CancellationToken cancellationToken = default,
        bool showPostAnalysisActionPrompt = false,
        bool forcePostAnalysisActionPrompt = false)
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
            var promptIndex = promptIndexOverride ?? _historyTracker.GetLatestPromptIndex();
            
            // Create a live thinking indicator (manually disposed before recursive calls)
            thinkingIndicator = ConsoleUI.CreateLiveThinkingIndicator();
            thinkingIndicator.Start();
            watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            watchdogTask = RunActivityWatchdogAsync(
                thinkingIndicator,
                () => new DateTime(Interlocked.Read(ref lastEventTimeTicks), DateTimeKind.Utc),
                () => hasStartedStreaming,
                watchdogCts.Token);

            // Subscribe to session events for streaming (manually disposed before recursive calls)
            subscription = _copilotSession.On(evt =>
            {
                Interlocked.Exchange(ref lastEventTimeTicks, DateTime.UtcNow.Ticks);

                switch (evt)
                {
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
                    
                    case ToolExecutionCompleteEvent toolComplete:
                        _historyTracker.RecordMcpToolComplete(toolComplete);
                        // Tool finished, back to thinking
                        if (hasStartedStreaming)
                        {
                            pendingStreamLineBreak = true;
                        }
                        thinkingIndicator.UpdateStatus("Processing results");
                        break;

                    case SubagentStartedEvent subagentStarted:
                        if (hasStartedStreaming)
                        {
                            pendingStreamLineBreak = true;
                        }
                        thinkingIndicator.UpdateStatus($"Delegating to {subagentStarted.Data?.AgentDisplayName ?? subagentStarted.Data?.AgentName ?? "sub-agent"}");
                        break;

                    case SubagentCompletedEvent:
                        if (hasStartedStreaming)
                        {
                            pendingStreamLineBreak = true;
                        }
                        thinkingIndicator.UpdateStatus("Processing delegated results");
                        break;

                    case SubagentFailedEvent:
                        if (hasStartedStreaming)
                        {
                            pendingStreamLineBreak = true;
                        }
                        thinkingIndicator.UpdateStatus("Delegated task failed");
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
                await RefreshSessionUsageMetricsAsync(cancellationToken);
                var statusBarInfo = BuildStatusBarInfo();
                ConsoleUI.WriteStatusBar(statusBarInfo);
                _historyTracker.SetPromptStatusBar(promptIndex, statusBarInfo);
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

            var approvalFollowUpHandled = false;

            // Handle any pending approval commands (may call SendMessageAsync recursively)
            if (!hasError && !wasCancelled)
            {
                approvalFollowUpHandled = await ProcessPendingApprovalsAsync(cancellationToken);
            }

            if (!approvalFollowUpHandled
                && !hasError
                && !wasCancelled
                && showPostAnalysisActionPrompt
                && ShouldOfferPostAnalysisActionPrompt(responseBuffer.ToString(), forcePostAnalysisActionPrompt))
            {
                return await HandlePostAnalysisActionAsync(cancellationToken);
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
        Func<bool> hasStartedStreaming,
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
                        if (hasStartedStreaming())
                        {
                            ConsoleUI.ShowLiveStatusNotice($"{nextWatchdogStatus} ({LiveThinkingIndicator.FormatElapsed((int)indicator.Elapsed.TotalSeconds)})");
                        }
                        else
                        {
                            indicator.UpdateStatus(nextWatchdogStatus);
                        }
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

    private static bool ShouldOfferPostAnalysisActionPrompt(string responseText, bool forcePrompt)
    {
        if (forcePrompt)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        if (responseText.Contains("Ready for next action", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return PostAnalysisHeadingRegex.IsMatch(responseText);
    }

    private static string BuildPostAnalysisFollowUpPrompt(PostAnalysisAction action)
    {
        return action switch
        {
            PostAnalysisAction.ContinueInvestigating => """
                Continue investigating from the current evidence.

                Requirements:
                - Do not repeat the full prior diagnosis unless something changed.
                - Gather only the next most useful diagnostics or validation steps.
                - If you reach an updated diagnosis or recommendation, stop after this response.
                - End with a short `## Ready for next action` section so TroubleScout can ask the user what to do next.
                """,
            PostAnalysisAction.ApplyFix => """
                Apply the most appropriate remediation based on your latest analysis.

                Requirements:
                - If you already recommended a fix in your most recent analysis, proceed with that path now instead of starting over.
                - If you have not proposed a fix yet, determine the next remediation step first.
                - Keep the explanation brief, request approval for any mutating commands, and do not repeat the full diagnosis unless it changed.
                - After reporting the outcome or next remediation step, end with a short `## Ready for next action` section so TroubleScout can ask the user what to do next.
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported post-analysis action.")
        };
    }

    private static string BuildApprovedCommandFollowUpPrompt(string executionSummary)
    {
        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("One or more approved command(s) have finished running.");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("Here are the approved command results:");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine(executionSummary.Trim());
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("Analyze what changed, summarize whether the action helped, and clearly state the next best option.");
        promptBuilder.AppendLine("Do not continue into more diagnostics or remediation on your own after this response.");
        promptBuilder.Append("End with a short `## Ready for next action` section so TroubleScout can ask the user what to do next.");
        return promptBuilder.ToString();
    }

    private async Task<bool> HandlePostAnalysisActionAsync(CancellationToken cancellationToken)
    {
        var action = ConsoleUI.PromptPostAnalysisAction();

        switch (action)
        {
            case PostAnalysisAction.ContinueInvestigating:
            {
                var promptIndex = RecordPrompt("TroubleScout action: Continue investigating.");
                return await SendMessageAsync(
                    BuildPostAnalysisFollowUpPrompt(PostAnalysisAction.ContinueInvestigating),
                    promptIndex,
                    cancellationToken,
                    showPostAnalysisActionPrompt: true,
                    forcePostAnalysisActionPrompt: false);
            }
            case PostAnalysisAction.ApplyFix:
            {
                var promptIndex = RecordPrompt("TroubleScout action: Apply the fix.");
                return await SendMessageAsync(
                    BuildPostAnalysisFollowUpPrompt(PostAnalysisAction.ApplyFix),
                    promptIndex,
                    cancellationToken,
                    showPostAnalysisActionPrompt: true,
                    forcePostAnalysisActionPrompt: false);
            }
            default:
                ConsoleUI.ShowInfo("Stopping here. TroubleScout is ready when you are.");
                return true;
        }
    }

    /// <summary>
    /// Process any commands that require user approval
    /// </summary>
    private async Task<bool> ProcessPendingApprovalsAsync(CancellationToken cancellationToken)
    {
        var pending = _diagnosticTools.PendingCommands;
        if (pending.Count == 0)
        {
            return false;
        }

        var commands = pending.Select(p => (p.Command, p.Reason)).ToList();
        var executedSummaries = new List<string>();
        
        if (commands.Count == 1)
        {
            var cmd = commands[0];
            var approval = ConsoleUI.PromptCommandApproval(cmd.Command, cmd.Reason, pending[0].Intent);
            if (approval == ApprovalResult.Approved)
            {
                ConsoleUI.ShowInfo($"Executing: {cmd.Command}");
                var result = await _diagnosticTools.ExecuteApprovedCommandAsync(pending[0]);
                ConsoleUI.ShowSuccess("Command executed");
                executedSummaries.Add($"Command: {cmd.Command}{Environment.NewLine}Result:{Environment.NewLine}{result}");
            }
            else
            {
                ConsoleUI.ShowWarning("Command skipped by user");
                _diagnosticTools.LogDeniedCommand(pending[0]);
                _diagnosticTools.ClearPendingCommands();
                return false;
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
                executedSummaries.Add($"Command: {cmd.Command}{Environment.NewLine}Result:{Environment.NewLine}{result}");
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
        }

        if (executedSummaries.Count == 0)
        {
            return false;
        }

        var promptIndex = RecordPrompt("TroubleScout action: Analyze approved command results.");
        var followUpPrompt = BuildApprovedCommandFollowUpPrompt(string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            executedSummaries));

        return await SendMessageAsync(
            followUpPrompt,
            promptIndex,
            cancellationToken,
            showPostAnalysisActionPrompt: true,
            forcePostAnalysisActionPrompt: true);
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

    private static void SaveReasoningEffortState(string? reasoningEffort)
    {
        var settings = AppSettingsStore.Load();
        settings.ReasoningEffort = NormalizeReasoningEffort(reasoningEffort);
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

    private static void SaveMcpRoleSettings(string? monitoringMcpServer, string? ticketingMcpServer)
    {
        var settings = AppSettingsStore.Load();

        settings.MonitoringMcpServer = string.IsNullOrWhiteSpace(monitoringMcpServer) ? null : monitoringMcpServer.Trim();
        settings.TicketingMcpServer = string.IsNullOrWhiteSpace(ticketingMcpServer) ? null : ticketingMcpServer.Trim();

        // Prune persisted MCP approvals that no longer correspond to a mapped role.
        // Persistence is only offered for monitoring/ticketing servers, so once a
        // server is unmapped its persisted trust must not silently survive.
        // (In-memory approvals are reconciled by SeedPersistedMcpApprovals on reload.)
        if (settings.PersistedApprovedMcpServers is { Count: > 0 } persisted)
        {
            var stillMapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(settings.MonitoringMcpServer)) stillMapped.Add(settings.MonitoringMcpServer);
            if (!string.IsNullOrWhiteSpace(settings.TicketingMcpServer)) stillMapped.Add(settings.TicketingMcpServer);

            var pruned = persisted.Where(p => !string.IsNullOrWhiteSpace(p) && stillMapped.Contains(p.Trim())).ToList();
            if (pruned.Count != persisted.Count)
            {
                settings.PersistedApprovedMcpServers = pruned;
            }
        }

        AppSettingsStore.Save(settings);
    }

    private void ApplySafeCommandsToAllExecutors(IReadOnlyList<string>? safeCommands)
    {
        _configuredSafeCommands = safeCommands?.Where(command => !string.IsNullOrWhiteSpace(command))
            .Select(command => command.Trim())
            .ToList();

        _executor.SetCustomSafeCommands(_configuredSafeCommands);
        _serverManager.ApplySafeCommands(_configuredSafeCommands);
    }

    private void ReloadSafeCommandsFromSettings()
    {
        var settings = AppSettingsStore.Load();
        _appSettings = settings;
        _configuredMonitoringMcpServer = settings.MonitoringMcpServer;
        _configuredTicketingMcpServer = settings.TicketingMcpServer;
        SeedPersistedMcpApprovals(settings.PersistedApprovedMcpServers);
        ApplySystemPromptSettings(settings.SystemPromptOverrides, settings.SystemPromptAppend);
        ApplyReasoningEffortSetting(settings.ReasoningEffort);
        ApplySafeCommandsToAllExecutors(settings.SafeCommands);
        _systemMessageConfig = CreateSystemMessage(_targetServer, _serverManager.Executors.Keys.ToList());
    }

    private void SeedPersistedMcpApprovals(IEnumerable<string>? persistedApprovals)
    {
        // Compute the set of approvals that *should* be seeded from persistence
        // right now, given the current monitoring/ticketing role mappings.
        // Persistence is only ever offered for those roles; once a role mapping
        // is cleared or changed, the prior trust must not silently apply.
        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(_configuredMonitoringMcpServer))
        {
            mapped.Add(_configuredMonitoringMcpServer);
        }
        if (!string.IsNullOrWhiteSpace(_configuredTicketingMcpServer))
        {
            mapped.Add(_configuredTicketingMcpServer);
        }

        var nextSeeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (persistedApprovals != null)
        {
            foreach (var name in persistedApprovals)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                var trimmed = name.Trim();
                if (mapped.Contains(trimmed))
                {
                    nextSeeded.Add(trimmed);
                }
            }
        }

        // Remove any previously-seeded persisted approvals that are no longer
        // valid (settings edited, role mapping cleared, etc.). Session-scoped
        // approvals (ApproveServerForSession) are intentionally left intact.
        foreach (var previous in _persistedSeededApprovals)
        {
            if (!nextSeeded.Contains(previous))
            {
                _approvedMcpServersForSession.Remove(previous);
            }
        }

        _persistedSeededApprovals.Clear();
        foreach (var current in nextSeeded)
        {
            _approvedMcpServersForSession.Add(current);
            _persistedSeededApprovals.Add(current);
        }
    }

    private async Task<(bool Success, string? Error)> RecreateCurrentCopilotSessionAsync()
    {
        if (_copilotClient == null || _copilotSession == null)
        {
            return (true, null);
        }

        await _copilotSession.DisposeAsync();
        _copilotSession = null;

        try
        {
            var success = await CreateCopilotSessionAsync(
                string.IsNullOrWhiteSpace(_selectedModel) ? null : _selectedModel,
                null);
            return (success, null);
        }
        catch (Exception ex)
        {
            return (false, TrimSingleLine(ex.Message));
        }
    }

    internal string? GetWelcomeHint()
    {
        if (!string.IsNullOrWhiteSpace(_configuredMonitoringMcpServer) || !string.IsNullOrWhiteSpace(_configuredTicketingMcpServer))
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_configuredMonitoringMcpServer))
            {
                parts.Add($"Monitoring MCP: {_configuredMonitoringMcpServer}");
            }

            if (!string.IsNullOrWhiteSpace(_configuredTicketingMcpServer))
            {
                parts.Add($"Ticketing MCP: {_configuredTicketingMcpServer}");
            }

            return $"{string.Join(" | ", parts)}. Use /mcp-role to change role mappings.";
        }

        return _configuredMcpServers.Count > 0
            ? "Use /mcp-role to map monitoring and ticketing MCP servers."
            : null;
    }

    private void HandleMcpApprovalsCommand(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // /mcp-approvals  -> list
        if (parts.Length <= 1 || string.Equals(parts[1], "list", StringComparison.OrdinalIgnoreCase))
        {
            var sessionApprovals = _approvedMcpServersForSession.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            var persisted = GetPersistedApprovedMcpServersSnapshot()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sessionApprovals.Count == 0 && persisted.Count == 0)
            {
                ConsoleUI.ShowInfo("No MCP approvals are active for this session.");
                ConsoleUI.ShowInfo("MCP servers you approve via the prompt appear here automatically.");
                return;
            }

            ConsoleUI.ShowInfo($"Active MCP approvals ({sessionApprovals.Count}):");
            foreach (var name in sessionApprovals)
            {
                var role = GetMcpServerRole(name);
                var persistedFlag = persisted.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase))
                    ? " [persisted]"
                    : string.Empty;
                var roleFlag = string.IsNullOrWhiteSpace(role) ? string.Empty : $" [{role}]";
                ConsoleUI.ShowInfo($"  {name}{roleFlag}{persistedFlag}");
            }

            if (persisted.Count > 0)
            {
                var orphaned = persisted
                    .Where(name => !_approvedMcpServersForSession.Contains(name))
                    .ToList();
                if (orphaned.Count > 0)
                {
                    ConsoleUI.ShowInfo("Persisted but not currently active:");
                    foreach (var name in orphaned)
                    {
                        ConsoleUI.ShowInfo($"  {name}");
                    }
                }
            }

            ConsoleUI.ShowInfo("Use /mcp-approvals clear all  or  /mcp-approvals clear <server> to remove persisted approvals.");
            return;
        }

        if (string.Equals(parts[1], "clear", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 3)
            {
                ConsoleUI.ShowWarning("Use /mcp-approvals clear all  or  /mcp-approvals clear <server>.");
                return;
            }

            if (string.Equals(parts[2], "all", StringComparison.OrdinalIgnoreCase))
            {
                var removed = ClearPersistedMcpApprovals();
                _approvedMcpServersForSession.Clear();
                ConsoleUI.ShowSuccess(removed > 0
                    ? $"Cleared {removed} persisted MCP approval{(removed == 1 ? string.Empty : "s")} and reset session approvals."
                    : "Cleared session MCP approvals (no persisted approvals were stored).");
                return;
            }

            var target = string.Join(' ', parts.Skip(2)).Trim();
            var persistedRemoved = RemovePersistedMcpApproval(target);
            var sessionRemoved = _approvedMcpServersForSession.Remove(target);

            if (persistedRemoved || sessionRemoved)
            {
                ConsoleUI.ShowSuccess($"Removed MCP approval for '{target}'.");
            }
            else
            {
                ConsoleUI.ShowWarning($"No active MCP approval found for '{target}'.");
            }
            return;
        }

        ConsoleUI.ShowWarning("Use /mcp-approvals [list|clear all|clear <server>].");
    }

    private async Task HandleMcpRoleCommandAsync(string input)
    {
        var availableServers = GetAvailableMcpRoleServerNames();
        var monitoring = _configuredMonitoringMcpServer;
        var ticketing = _configuredTicketingMcpServer;
        var parts = input.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 1)
        {
            if (availableServers.Count == 0 && string.IsNullOrWhiteSpace(monitoring) && string.IsNullOrWhiteSpace(ticketing))
            {
                ConsoleUI.ShowWarning("No MCP servers are configured. Add servers in your MCP config first, then use /mcp-role.");
                return;
            }

            (monitoring, ticketing) = ConsoleUI.PromptMcpRoleSelection(monitoring, ticketing, availableServers);
        }
        else
        {
            if (!TryApplyDirectMcpRoleCommand(parts, availableServers, ref monitoring, ref ticketing, out var usageError))
            {
                ConsoleUI.ShowWarning(usageError);
                ConsoleUI.ShowInfo("Usage:");
                ConsoleUI.ShowInfo("  /mcp-role");
                ConsoleUI.ShowInfo("  /mcp-role monitoring <server|none>");
                ConsoleUI.ShowInfo("  /mcp-role ticketing <server|none>");
                ConsoleUI.ShowInfo("  /mcp-role clear <monitoring|ticketing|all>");
                return;
            }
        }

        if (McpRoleValuesEqual(monitoring, _configuredMonitoringMcpServer)
            && McpRoleValuesEqual(ticketing, _configuredTicketingMcpServer))
        {
            ConsoleUI.ShowInfo($"MCP roles unchanged. Monitoring: {monitoring ?? "none"} | Ticketing: {ticketing ?? "none"}");
            ConsoleUI.ShowStatusPanel(EffectiveTargetServer, EffectiveConnectionMode, _copilotSession != null, SelectedModel, _executionMode, GetStatusFields(), GetAdditionalTargetsForDisplay(), DefaultSessionTarget);
            return;
        }

        SaveMcpRoleSettings(monitoring, ticketing);
        ReloadSafeCommandsFromSettings();
        var (sessionReloadSucceeded, sessionReloadError) = await RecreateCurrentCopilotSessionAsync();

        if (sessionReloadSucceeded)
        {
            ConsoleUI.ShowSuccess($"MCP roles saved. Monitoring: {monitoring ?? "none"} | Ticketing: {ticketing ?? "none"}");
            ConsoleUI.ShowStatusPanel(EffectiveTargetServer, EffectiveConnectionMode, _copilotSession != null, SelectedModel, _executionMode, GetStatusFields(), GetAdditionalTargetsForDisplay(), DefaultSessionTarget);
        }
        else
        {
            var message = "MCP roles were saved, but the AI session could not be recreated. Use /login or /model to reconnect.";
            if (!string.IsNullOrWhiteSpace(sessionReloadError))
            {
                message += $" {sessionReloadError}";
            }

            ConsoleUI.ShowWarning(message);
        }
    }

    private bool TryApplyDirectMcpRoleCommand(
        string[] parts,
        IReadOnlyList<string> availableServers,
        ref string? monitoring,
        ref string? ticketing,
        out string error)
    {
        error = "Invalid /mcp-role command.";
        if (parts.Length < 2)
        {
            return false;
        }

        var action = parts[1].Trim().ToLowerInvariant();
        if (action == "clear")
        {
            if (parts.Length < 3)
            {
                error = "Specify which role to clear: monitoring, ticketing, or all.";
                return false;
            }

            switch (parts[2].Trim().ToLowerInvariant())
            {
                case "monitoring":
                    monitoring = null;
                    return true;
                case "ticketing":
                    ticketing = null;
                    return true;
                case "all":
                    monitoring = null;
                    ticketing = null;
                    return true;
                default:
                    error = "Use /mcp-role clear <monitoring|ticketing|all>.";
                    return false;
            }
        }

        if (action is not "monitoring" and not "ticketing")
        {
            error = "Use /mcp-role monitoring <server|none> or /mcp-role ticketing <server|none>.";
            return false;
        }

        if (parts.Length < 3)
        {
            error = $"Specify the MCP server name to assign to the {action} role, or 'none' to clear it.";
            return false;
        }

        if (!TryResolveRequestedMcpRoleValue(parts[2], availableServers, out var resolvedValue, out error))
        {
            return false;
        }

        if (action == "monitoring")
        {
            monitoring = resolvedValue;
        }
        else
        {
            ticketing = resolvedValue;
        }

        return true;
    }

    private static bool McpRoleValuesEqual(string? left, string? right)
    {
        return string.Equals(
            left?.Trim(),
            right?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<string> GetAvailableMcpRoleServerNames()
    {
        var warnings = new List<string>();
        var servers = LoadMcpServersFromConfig(_mcpConfigPath, warnings);
        var choices = servers.Keys.ToList();

        if (!string.IsNullOrWhiteSpace(_configuredMonitoringMcpServer)
            && !choices.Contains(_configuredMonitoringMcpServer, StringComparer.OrdinalIgnoreCase))
        {
            choices.Add(_configuredMonitoringMcpServer);
        }

        if (!string.IsNullOrWhiteSpace(_configuredTicketingMcpServer)
            && !choices.Contains(_configuredTicketingMcpServer, StringComparer.OrdinalIgnoreCase))
        {
            choices.Add(_configuredTicketingMcpServer);
        }

        return choices
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryResolveRequestedMcpRoleValue(
        string requestedValue,
        IReadOnlyList<string> availableServers,
        out string? resolvedValue,
        out string error)
    {
        var trimmed = requestedValue.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            resolvedValue = null;
            error = string.Empty;
            return true;
        }

        var match = availableServers.FirstOrDefault(server => server.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            resolvedValue = match;
            error = string.Empty;
            return true;
        }

        resolvedValue = null;
        error = $"Unknown MCP server '{trimmed}'. Use /capabilities to see configured MCP servers.";
        return false;
    }

    private void ApplySystemPromptSettings(IReadOnlyDictionary<string, string>? overrides, string? append)
    {
        if (overrides == null)
        {
            _configuredSystemPromptOverrides = null;
        }
        else
        {
            var normalizedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in overrides)
            {
                var normalizedKey = entry.Key?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedKey) || string.IsNullOrWhiteSpace(entry.Value))
                {
                    continue;
                }

                if (AppSettingsStore.IsDefaultSystemPromptOverride(normalizedKey, entry.Value))
                {
                    continue;
                }

                normalizedOverrides[normalizedKey] = entry.Value;
            }

            _configuredSystemPromptOverrides = normalizedOverrides.Count > 0 ? normalizedOverrides : null;
        }

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
                    ConsoleUI.ShowWelcomeMessage(GetWelcomeHint());
                }
                else
                {
                    ConsoleUI.ShowWarning("Could not start a new session.");
                }

                continue;
            }

            if (firstToken == "/status")
            {
                ConsoleUI.ShowStatusPanel(EffectiveTargetServer, EffectiveConnectionMode, _copilotSession != null, SelectedModel, _executionMode, GetStatusFields(includeMcpApprovals: true), GetAdditionalTargetsForDisplay(), DefaultSessionTarget);
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
                var (sessionReloadSucceeded, sessionReloadError) = await RecreateCurrentCopilotSessionAsync();

                if (sessionReloadSucceeded)
                {
                    ConsoleUI.ShowSuccess("Settings reloaded. Safe command patterns and system prompt settings have been applied.");
                }
                else
                {
                    var message = "Settings were reloaded, but the AI session could not be recreated. Use /login or /model to reconnect.";
                    if (!string.IsNullOrWhiteSpace(sessionReloadError))
                    {
                        message += $" {sessionReloadError}";
                    }

                    ConsoleUI.ShowWarning(message);
                }

                continue;
            }

            if (IsSlashCommandInvocation(lowerInput, "/mcp-role"))
            {
                await HandleMcpRoleCommandAsync(input);
                continue;
            }

            if (IsSlashCommandInvocation(lowerInput, "/mcp-approvals"))
            {
                HandleMcpApprovalsCommand(input);
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

            if (IsSlashCommandInvocation(lowerInput, "/reasoning"))
            {
                var currentModel = GetSelectedModelInfo();
                if (currentModel == null)
                {
                    ConsoleUI.ShowWarning("No active model is selected yet. Use /model first.");
                    continue;
                }

                if (!SupportsReasoningEffort(currentModel))
                {
                    ConsoleUI.ShowInfo($"The current model '{SelectedModel}' does not expose reasoning-effort controls.");
                    continue;
                }

                var supportedEfforts = GetSupportedReasoningEfforts(currentModel);
                var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                string? requestedReasoningEffort;

                if (parts.Length < 2)
                {
                    requestedReasoningEffort = ConsoleUI.PromptReasoningEffort(
                        _configuredReasoningEffort,
                        supportedEfforts,
                        GetDefaultReasoningEffort(currentModel));
                }
                else
                {
                    requestedReasoningEffort = NormalizeReasoningEffort(parts[1]);
                    if (!string.IsNullOrWhiteSpace(requestedReasoningEffort)
                        && supportedEfforts.Count > 0
                        && !supportedEfforts.Contains(requestedReasoningEffort, StringComparer.OrdinalIgnoreCase))
                    {
                        ConsoleUI.ShowWarning($"Unsupported reasoning effort '{parts[1].Trim()}'. Supported values: {string.Join(", ", supportedEfforts)} or auto.");
                        continue;
                    }
                }

                var previousReasoningEffort = _configuredReasoningEffort;
                var normalizedReasoningEffort = NormalizeReasoningEffort(requestedReasoningEffort);
                if (string.Equals(previousReasoningEffort, normalizedReasoningEffort, StringComparison.OrdinalIgnoreCase))
                {
                    ConsoleUI.ShowInfo($"Reasoning remains: {GetReasoningDisplay(currentModel)}");
                    continue;
                }

                ApplyReasoningEffortSetting(normalizedReasoningEffort);
                SaveReasoningEffortState(normalizedReasoningEffort);

                if (_copilotSession != null)
                {
                    var targetModel = string.IsNullOrWhiteSpace(_selectedModel) ? currentModel.Id : _selectedModel;
                    var spinnerLabel = string.IsNullOrWhiteSpace(normalizedReasoningEffort)
                        ? "Restoring automatic reasoning..."
                        : $"Applying reasoning {normalizedReasoningEffort}...";

                    var success = await ConsoleUI.RunWithSpinnerAsync(spinnerLabel, async updateStatus =>
                    {
                        updateStatus("Restarting AI session...");
                        await _copilotSession.DisposeAsync();
                        _copilotSession = null;
                        return await CreateCopilotSessionAsync(targetModel, updateStatus);
                    });

                    if (success)
                    {
                        ConsoleUI.ShowModelSelectionSummary(SelectedModel, GetSelectedModelDetails());
                    }
                    else
                    {
                        ApplyReasoningEffortSetting(previousReasoningEffort);
                        SaveReasoningEffortState(previousReasoningEffort);
                    }
                }
                else
                {
                    ConsoleUI.ShowSuccess($"Reasoning preference saved: {GetReasoningDisplay(currentModel) ?? "auto"}");
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

                        if (_modelDiscovery.AvailableModels.Count == 0)
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

                if (_modelDiscovery.AvailableModels.Count == 0)
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

                var currentModel = SelectedModel;
                var selectedEntry = ConsoleUI.PromptModelSelection(currentModel, selectionEntries);
                if (selectedEntry == null)
                {
                    ConsoleUI.ShowInfo($"Keeping current model: {currentModel}");
                    continue;
                }

                if (!IsCurrentModelAndSource(selectedEntry))
                {
                    var switchBehavior = ModelSwitchBehavior.CleanSession;
                    var priorConversation = Array.Empty<ReportPromptEntry>();

                    if (HasRecordedConversationHistory())
                    {
                        var behaviorChoice = ConsoleUI.PromptModelSwitchBehavior(currentModel, selectedEntry.DisplayName);
                        if (!behaviorChoice.HasValue)
                        {
                            ConsoleUI.ShowInfo($"Keeping current model: {currentModel}");
                            continue;
                        }

                        switchBehavior = behaviorChoice.Value;
                        if (switchBehavior == ModelSwitchBehavior.SecondOpinion)
                        {
                            priorConversation = GetRecordedPromptSnapshot().ToArray();
                        }
                    }

                    var displayName = selectedEntry.DisplayName;
                    var success = await ConsoleUI.RunWithSpinnerAsync($"Switching to {displayName}...", async updateStatus =>
                    {
                        return await ChangeModelAsync(selectedEntry, updateStatus);
                    });
                    
                    if (success)
                    {
                        if (switchBehavior == ModelSwitchBehavior.CleanSession)
                        {
                            ClearRecordedConversationHistory();
                        }

                        ConsoleUI.ShowModelSelectionSummary(SelectedModel, GetSelectedModelDetails());

                        if (switchBehavior == ModelSwitchBehavior.SecondOpinion && priorConversation.Length > 0)
                        {
                            ConsoleUI.ShowInfo($"Asking {SelectedModel} for a second opinion using the current session context...");
                            await RunInteractiveCancelableAiOperationAsync(token =>
                                RequestSecondOpinionAsync(currentModel, SelectedModel, priorConversation, token));
                        }
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

                        _systemMessageConfig = CreateSystemMessage(_targetServer, _serverManager.Executors.Keys.ToList());

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
                    if (_serverManager.Executors.TryGetValue(serverName, out var executor) && executor.JeaAllowedCommands is { Count: > 0 })
                    {
                        ConsoleUI.ShowSuccess($"Connected to JEA endpoint '{configurationName}' on {serverName}");
                        AnsiConsole.MarkupLine($"[grey]Discovered commands for {Markup.Escape(serverName)} ({Markup.Escape(configurationName)}):[/]");
                        foreach (var commandName in executor.JeaAllowedCommands.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                        {
                            AnsiConsole.MarkupLine($"  [grey]-[/] {Markup.Escape(commandName)}");
                        }
                    }

                    _systemMessageConfig = CreateSystemMessage(_targetServer, _serverManager.Executors.Keys.ToList());

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
            var promptIndex = RecordPrompt(input);
            await RunInteractiveCancelableAiOperationAsync(token => SendMessageAsync(
                input,
                promptIndex,
                token,
                showPostAnalysisActionPrompt: true,
                forcePostAnalysisActionPrompt: false));
        }
    }

    private async Task<T> RunInteractiveCancelableAiOperationAsync<T>(Func<CancellationToken, Task<T>> operation)
    {
        using var escCts = new CancellationTokenSource();
        var escTask = Task.Run(async () =>
        {
            try
            {
                while (!escCts.Token.IsCancellationRequested)
                {
                    if (Console.KeyAvailable && !LiveThinkingIndicator.IsApprovalInProgress)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.Escape)
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
                // Expected when the AI finishes before ESC is pressed.
            }
        }, CancellationToken.None);

        try
        {
            return await operation(escCts.Token);
        }
        finally
        {
            escCts.Cancel();
            try { await escTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { /* ignore */ }
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

        var selectedByName = _modelDiscovery.AvailableModels.FirstOrDefault(model =>
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

        // Same model ΓÇö check if provider also matches
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

            ClearRecordedConversationHistory();

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

    private IReadOnlyList<string> BuildByokModelEndpointCandidates(string baseUrl)
        => _byokProviderManager.BuildByokModelEndpointCandidates(baseUrl);

    private async Task<List<ModelInfo>> TryGetByokProviderModelsAsync()
    {
        _modelDiscovery.ClearByokPricing();

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

                var discovery = _byokProviderManager.ParseByokModelsResponse(document.RootElement);
                if (discovery.Models.Count > 0)
                {
                    foreach (var entry in discovery.PricingByModelId)
                    {
                        _modelDiscovery.StoreByokPricing(entry.Key, entry.Value);
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

    private async Task<bool> ConfigureByokOpenAiAsync(string baseUrl, string apiKey, string? model, Action<string>? updateStatus)
        => await _byokProviderManager.ConfigureByokOpenAiAsync(
            baseUrl,
            apiKey,
            model,
            _copilotClient,
            async () =>
            {
                if (_copilotSession != null)
                {
                    await _copilotSession.DisposeAsync();
                    _copilotSession = null;
                }
            },
            TryGetByokProviderModelsAsync,
            CreateCopilotSessionAsync,
            SaveByokSettings,
            OpenAiApiKeyEnvironmentVariable,
            updateStatus);

    private async Task<bool> LoginAndCreateGitHubSessionAsync(Action<string>? updateStatus)
        => await _byokProviderManager.LoginAndCreateGitHubSessionAsync(
            _copilotClient,
            _selectedModel,
            async () =>
            {
                if (_copilotSession != null)
                {
                    await _copilotSession.DisposeAsync();
                    _copilotSession = null;
                }
            },
            GetMergedModelListAsync,
            CreateCopilotSessionAsync,
            RefreshAvailableModelsAsync,
            isAuthenticated => _isGitHubCopilotAuthenticated = isAuthenticated,
            updateStatus);

    private async Task<bool> IsGitHubAuthenticatedAsync()
        => await _byokProviderManager.IsGitHubAuthenticatedAsync(_copilotClient);

    private static bool IsBareExitCommand(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        return input.Equals("exit", StringComparison.Ordinal)
            || input.Equals("quit", StringComparison.Ordinal);
    }

    private int RecordPrompt(string prompt) => _historyTracker.RecordPrompt(prompt);

    private void SetPromptReply(int promptIndex, string reply) => _historyTracker.SetPromptReply(promptIndex, reply);

    private void RecordCommandAction(CommandActionLog actionLog) => _historyTracker.RecordCommandAction(actionLog);

    private void RecordMcpToolAction(ToolExecutionStartEvent toolStart) => _historyTracker.RecordMcpToolAction(toolStart);

    private void ClearRecordedConversationHistory() => _historyTracker.ClearRecordedConversationHistory();

    private bool HasRecordedConversationHistory() => _historyTracker.HasRecordedConversationHistory();

    private List<ReportPromptEntry> GetRecordedPromptSnapshot() => _historyTracker.GetRecordedPromptSnapshot();

    private async Task<bool> RequestSecondOpinionAsync(
        string previousModel,
        string newModel,
        IReadOnlyList<ReportPromptEntry> priorConversation,
        CancellationToken cancellationToken = default)
    {
        if (priorConversation.Count == 0)
        {
            return true;
        }

        var reportPrompt = $"Second opinion request after switching from {previousModel} to {newModel}.";
        var promptIndex = RecordPrompt(reportPrompt);
        var secondOpinionPrompt = SecondOpinionService.BuildSecondOpinionPrompt(
            previousModel,
            newModel,
            priorConversation,
            MaxSecondOpinionTurns,
            MaxSecondOpinionPromptChars,
            MaxSecondOpinionUserPromptChars,
            MaxSecondOpinionReplyChars,
            MaxSecondOpinionCommandChars,
            MaxSecondOpinionToolOutputChars);
        return await SendMessageAsync(
            secondOpinionPrompt,
            promptIndex,
            cancellationToken,
            showPostAnalysisActionPrompt: true,
            forcePostAnalysisActionPrompt: false);
    }

    private void GenerateAndOpenReport()
    {
        var prompts = GetRecordedPromptSnapshot();

        if (prompts.Count == 0)
        {
            ConsoleUI.ShowInfo("No prompts recorded yet. Ask a question first, then run /report.");
            return;
        }

        var reportsDir = Path.Combine(Path.GetTempPath(), "TroubleScout", "reports");
        Directory.CreateDirectory(reportsDir);

        var reportPath = Path.Combine(reportsDir, $"troublescout-report-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.html");
        var summary = BuildReportSessionSummary();
        var html = ReportHtmlBuilder.BuildReportHtml(prompts, summary);
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

    private ReportSessionSummary BuildReportSessionSummary()
    {
        var modelDisplay = SelectedModel ?? "Unknown";
        var providerDisplay = ActiveProviderDisplayName;
        var modelsUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(modelDisplay))
        {
            modelsUsed.Add(modelDisplay);
        }

        return new ReportSessionSummary(
            CurrentModel: modelDisplay,
            CurrentProvider: providerDisplay,
            ModelsUsed: modelsUsed.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList(),
            ConfiguredMcpServers: _configuredMcpServers.ToList(),
            UsedMcpServers: _runtimeMcpServers.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            MonitoringMcp: _configuredMonitoringMcpServer,
            TicketingMcp: _configuredTicketingMcpServer,
            ApprovedMcpServersForSession: _approvedMcpServersForSession.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            PersistedApprovedMcpServers: GetPersistedApprovedMcpServersSnapshot().ToList(),
            ConfiguredSkills: _configuredSkills.ToList(),
            UsedSkills: _runtimeSkills.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            ExecutionMode: _executionMode.ToString(),
            TargetServer: EffectiveTargetServer);
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
        _serverManager.DisposeAllExecutors(ex =>
        {
            if (_debugMode)
            {
                ConsoleUI.ShowWarning($"Additional session cleanup warning: {TrimSingleLine(ex.Message)}");
            }
        });
        
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

        ValidateConfiguredMcpRole("Monitoring", _configuredMonitoringMcpServer, mcpServers);
        ValidateConfiguredMcpRole("Ticketing", _configuredTicketingMcpServer, mcpServers);

        var config = BuildSessionConfig(model, mcpServers);

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
        ResetStateForNewAiSession();
        _selectedReasoningEffort = NormalizeReasoningEffort(config.ReasoningEffort);

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

    private void ResetStateForNewAiSession()
    {
        _sessionId = CreateSessionId();
        _lastUsage = null;
        _toolInvocationCount = 0;
        _sessionPremiumRequestCost = null;
        _sessionUsageTracker.Reset();
        _approvedUrlsForSession.Clear();
        _allowAllUrlsForSession = false;
    }

    private static string? GetByokWireApi(string? model)
    {
        return !string.IsNullOrWhiteSpace(model) && model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            ? "responses"
            : null;
    }

    private void HandleSessionLifecycleStateEvent(SessionEvent evt)
    {
        CaptureCapabilityUsage(evt);

        switch (evt)
        {
            case SessionStartEvent startEvt:
                if (!string.IsNullOrWhiteSpace(startEvt.Data?.SelectedModel))
                {
                    _selectedModel = startEvt.Data.SelectedModel;
                }

                _selectedReasoningEffort = NormalizeReasoningEffort(startEvt.Data?.ReasoningEffort);

                if (!string.IsNullOrWhiteSpace(startEvt.Data?.CopilotVersion))
                {
                    _copilotVersion = startEvt.Data.CopilotVersion;
                }

                break;

            case SessionModelChangeEvent modelChangeEvt:
                if (!string.IsNullOrWhiteSpace(modelChangeEvt.Data?.NewModel))
                {
                    _selectedModel = modelChangeEvt.Data.NewModel;
                }

                _selectedReasoningEffort = NormalizeReasoningEffort(modelChangeEvt.Data?.ReasoningEffort);

                break;

            case AssistantUsageEvent usageEvt:
                CaptureUsageMetrics(usageEvt);
                if (!string.IsNullOrEmpty(usageEvt.Data?.Model))
                {
                    _selectedModel = usageEvt.Data.Model;
                }

                break;
        }
    }

    internal SessionConfig BuildSessionConfig(string? model)
    {
        var warnings = new List<string>();
        var mcpServers = LoadMcpServersFromConfig(_mcpConfigPath, warnings);
        return BuildSessionConfig(model, mcpServers);
    }

    private SessionConfig BuildSessionConfig(string? model, IReadOnlyDictionary<string, McpServerConfig> availableMcpServers)
    {
        return new SessionConfig
        {
            Model = model,
            ReasoningEffort = ResolveConfiguredReasoningEffort(model),
            SystemMessage = _systemMessageConfig,
            Streaming = true,
            IncludeSubAgentStreamingEvents = false,
            Tools = _diagnosticTools.GetTools().ToList(),
            DefaultAgent = new DefaultAgentConfig
            {
                ExcludedTools = ["web_search"]
            },
            CustomAgents = BuildCustomAgentConfigs(availableMcpServers),
            ClientName = "TroubleScout",
            OnEvent = HandleSessionLifecycleStateEvent,
            OnPermissionRequest = (req, inv) =>
            {
                // Read-only operations and our own custom tools are always auto-approved
                var kind = NormalizePermissionKind(req.Kind);
                if (kind is "read" or "custom-tool")
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
                                Kind = PermissionRequestResultKind.Rejected
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
                                : PermissionRequestResultKind.Rejected
                        });
                    }
                }

                if (kind == "url")
                {
                    if (TryIsApprovedUrlRequest(req))
                    {
                        return Task.FromResult(new PermissionRequestResult
                        {
                            Kind = PermissionRequestResultKind.Approved
                        });
                    }

                    var url = GetUrlFromPermissionRequest(req);
                    var intention = GetUrlPermissionIntention(req);
                    var urlApproval = ConsoleUI.PromptUrlApproval(url ?? "URL fetch", intention);
                    return Task.FromResult(CreateUrlPermissionApprovalResult(url, urlApproval));
                }

                if (kind == "mcp")
                {
                    return Task.FromResult(HandleMcpPermissionRequest(req));
                }

                // In Safe mode: MCP, shell, file-write require user approval
                var description = DescribePermissionRequest(req);
                var approval = ConsoleUI.PromptCommandApproval(
                    description,
                    BuildPermissionPromptReason(kind));
                return Task.FromResult(CreatePermissionApprovalResult(req, kind, approval));
            }
        };
    }

    private IList<CustomAgentConfig> BuildCustomAgentConfigs(IReadOnlyDictionary<string, McpServerConfig> availableMcpServers)
    {
        var agents = new List<CustomAgentConfig>
        {
            new CustomAgentConfig
            {
                Name = "server-evidence-collector",
                DisplayName = "Server Evidence Collector",
                Description = "Collects targeted server and MCP evidence, then returns only the relevant findings.",
                Infer = true,
                Prompt = """
                    You are TroubleScout's focused evidence-collection sub-agent.
                    Gather only the evidence needed for the current troubleshooting step.
                    Prefer concise summaries over raw output dumps.
                    Always identify the source server or MCP system for each finding.
                    Do not recommend fixes unless the parent agent explicitly asked for remediation options.
                    Return only the findings that materially affect the diagnosis.
                    """
            },
            new CustomAgentConfig
            {
                Name = "issue-researcher",
                DisplayName = "Issue Researcher",
                Description = "Researches detected errors, symptoms, and event IDs on the web and returns concise findings.",
                Infer = true,
                Tools = ["web_search"],
                Prompt = """
                    You are TroubleScout's focused web research sub-agent.
                    Use web_search to validate suspected issues, error messages, event IDs, and likely root causes.
                    Prefer high-signal, directly relevant findings over generic troubleshooting advice.
                    Return a short summary of what is relevant, why it matters, and any high-confidence remediation guidance.
                    """
            }
        };

        var monitoringAgent = BuildRoleScopedAgent(
            "monitoring-investigator",
            "Monitoring Investigator",
            _configuredMonitoringMcpServer,
            availableMcpServers,
            """
            You are TroubleScout's monitoring-focused sub-agent.
            Use the mapped monitoring MCP server to gather only the alert, dashboard, trigger, incident, or telemetry data that is relevant to the current issue.
            Return concise findings with the monitoring source clearly identified.
            """
        );
        if (monitoringAgent != null)
        {
            agents.Add(monitoringAgent);
        }

        var ticketingAgent = BuildRoleScopedAgent(
            "ticket-investigator",
            "Ticket Investigator",
            _configuredTicketingMcpServer,
            availableMcpServers,
            """
            You are TroubleScout's ticket-focused sub-agent.
            Use the mapped ticketing MCP server to gather only the tickets, incidents, prior actions, or historical notes that materially affect the current diagnosis.
            Return concise findings with the ticketing source clearly identified.
            """
        );
        if (ticketingAgent != null)
        {
            agents.Add(ticketingAgent);
        }

        return agents;
    }

    private static CustomAgentConfig? BuildRoleScopedAgent(
        string name,
        string displayName,
        string? configuredServerName,
        IReadOnlyDictionary<string, McpServerConfig> availableMcpServers,
        string prompt)
    {
        if (string.IsNullOrWhiteSpace(configuredServerName)
            || !availableMcpServers.TryGetValue(configuredServerName, out var serverConfig))
        {
            return null;
        }

        return new CustomAgentConfig
        {
            Name = name,
            DisplayName = displayName,
            Description = $"Uses the mapped MCP role '{configuredServerName}' and returns concise findings.",
            Infer = true,
            McpServers = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase)
            {
                [configuredServerName] = serverConfig
            },
            Prompt = prompt
        };
    }

    private PermissionRequestResult CreateUrlPermissionApprovalResult(string? url, UrlApprovalResult approval)
    {
        switch (approval)
        {
            case UrlApprovalResult.ApproveAllUrls:
                _allowAllUrlsForSession = true;
                return new PermissionRequestResult
                {
                    Kind = PermissionRequestResultKind.Approved
                };

            case UrlApprovalResult.ApproveThisUrl:
            {
                var normalizedUrl = NormalizeUrlForApproval(url);
                if (!string.IsNullOrWhiteSpace(normalizedUrl))
                {
                    _approvedUrlsForSession.Add(normalizedUrl);
                }

                return new PermissionRequestResult
                {
                    Kind = PermissionRequestResultKind.Approved
                };
            }

            default:
                return new PermissionRequestResult
                {
                    Kind = PermissionRequestResultKind.Rejected
                };
        }
    }

    private bool TryIsApprovedUrlRequest(PermissionRequest request)
    {
        if (_allowAllUrlsForSession)
        {
            return true;
        }

        var normalizedUrl = NormalizeUrlForApproval(GetUrlFromPermissionRequest(request));
        return !string.IsNullOrWhiteSpace(normalizedUrl) && _approvedUrlsForSession.Contains(normalizedUrl);
    }

    private static string? GetUrlFromPermissionRequest(PermissionRequest request)
    {
        return request is PermissionRequestUrl urlRequest
            ? urlRequest.Url?.Trim()
            : ReadStringProperty(request, "Url", "Uri");
    }

    private static string? GetUrlPermissionIntention(PermissionRequest request)
    {
        return request is PermissionRequestUrl urlRequest
            ? urlRequest.Intention?.Trim()
            : ReadStringProperty(request, "Intention", "Reason", "Purpose");
    }

    private static string? NormalizeUrlForApproval(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            return parsed.AbsoluteUri;
        }

        return url.Trim();
    }

    private void ValidateConfiguredMcpRole(string roleName, string? configuredServerName, IReadOnlyDictionary<string, McpServerConfig> mcpServers)
    {
        if (string.IsNullOrWhiteSpace(configuredServerName))
        {
            return;
        }

        if (!mcpServers.ContainsKey(configuredServerName))
        {
            _configurationWarnings.Add($"{roleName} MCP '{configuredServerName}' is not available in the current MCP configuration.");
        }
    }

    private static PermissionRequestResult CreatePermissionApprovalResult(
        PermissionRequest request,
        string kind,
        ApprovalResult approval)
    {
        if (approval != ApprovalResult.Approved)
        {
            return new PermissionRequestResult
            {
                Kind = PermissionRequestResultKind.Rejected
            };
        }

        var result = new PermissionRequestResult
        {
            Kind = PermissionRequestResultKind.Approved
        };

        var sessionRule = TryCreateSessionScopedApprovalRule(request, kind);
        if (sessionRule != null)
        {
            result.Rules = [sessionRule];
        }

        return result;
    }

    private static object? TryCreateSessionScopedApprovalRule(PermissionRequest request, string kind)
    {
        // MCP approvals are now gated by TroubleshootingSession's own server-scoped HashSet,
        // so we no longer emit per-tool SDK approval rules for them. Other kinds may add their
        // own session-scoped rules in the future.
        _ = request;
        _ = kind;
        return null;
    }

    private PermissionRequestResult HandleMcpPermissionRequest(PermissionRequest request)
    {
        var serverName = request is PermissionRequestMcp typedMcp
            ? typedMcp.ServerName?.Trim()
            : ReadStringProperty(request, "McpServerName", "ServerName", "Server", "Name");
        var toolName = request is PermissionRequestMcp typedTool
            ? typedTool.ToolName?.Trim() ?? typedTool.ToolTitle?.Trim()
            : ReadStringProperty(request, "ToolName", "ToolTitle", "Tool", "Method");
        var argumentsPreview = ReadPermissionObjectString(request, "Args", "Arguments", "Params", "Input");

        // If we can't even identify the server, fall back to the generic approval path.
        if (string.IsNullOrWhiteSpace(serverName))
        {
            var description = DescribePermissionRequest(request);
            var fallback = ConsoleUI.PromptCommandApproval(
                description,
                BuildPermissionPromptReason("mcp"));
            return fallback == ApprovalResult.Approved
                ? new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved }
                : new PermissionRequestResult { Kind = PermissionRequestResultKind.Rejected };
        }

        // Already approved (this session or persisted on disk).
        if (_approvedMcpServersForSession.Contains(serverName))
        {
            return new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved };
        }

        // Auto-approve clearly read-only tools (get_*/list_*/search_*/...).
        if (McpReadOnlyHeuristic.IsReadOnlyToolName(toolName))
        {
            return new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved };
        }

        var role = GetMcpServerRole(serverName);
        var approval = ConsoleUI.PromptMcpApproval(
            serverName,
            toolName ?? "(unknown tool)",
            argumentsPreview,
            role);

        switch (approval)
        {
            case McpApprovalResult.ApproveOnce:
                return new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved };

            case McpApprovalResult.ApproveServerForSession:
                _approvedMcpServersForSession.Add(serverName);
                return new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved };

            case McpApprovalResult.ApproveServerPersist:
                _approvedMcpServersForSession.Add(serverName);
                if (_appSettings != null)
                {
                    try
                    {
                        AppSettingsStore.AddPersistedApprovedMcpServer(_appSettings, serverName);
                    }
                    catch
                    {
                        // settings persistence is best-effort; the in-memory approval still applies for this session.
                    }
                }
                return new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved };

            default:
                return new PermissionRequestResult { Kind = PermissionRequestResultKind.Rejected };
        }
    }

    private string? GetMcpServerRole(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return null;
        }

        if (string.Equals(serverName, _configuredMonitoringMcpServer, StringComparison.OrdinalIgnoreCase))
        {
            return "monitoring";
        }

        if (string.Equals(serverName, _configuredTicketingMcpServer, StringComparison.OrdinalIgnoreCase))
        {
            return "ticketing";
        }

        return null;
    }

    internal IReadOnlyCollection<string> GetApprovedMcpServersSnapshot()
        => _approvedMcpServersForSession.ToArray();

    internal IReadOnlyList<string> GetPersistedApprovedMcpServersSnapshot()
        => _appSettings?.PersistedApprovedMcpServers?.ToArray() ?? Array.Empty<string>();

    internal bool RemovePersistedMcpApproval(string serverName)
    {
        if (_appSettings == null || string.IsNullOrWhiteSpace(serverName))
        {
            return false;
        }

        var removed = AppSettingsStore.RemovePersistedApprovedMcpServer(_appSettings, serverName);
        if (removed)
        {
            _approvedMcpServersForSession.Remove(serverName.Trim());
        }
        return removed;
    }

    internal int ClearPersistedMcpApprovals()
    {
        if (_appSettings == null)
        {
            return 0;
        }

        var snapshot = _appSettings.PersistedApprovedMcpServers?.ToList() ?? new List<string>();
        var count = AppSettingsStore.ClearPersistedApprovedMcpServers(_appSettings);
        foreach (var name in snapshot)
        {
            _approvedMcpServersForSession.Remove(name);
        }
        return count;
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
                var serverName = request is PermissionRequestMcp mcpRequest
                    ? mcpRequest.ServerName?.Trim()
                    : ReadStringProperty(request, "McpServerName", "ServerName", "Server", "Name");
                var toolName = request is PermissionRequestMcp typedMcp
                    ? typedMcp.ToolName?.Trim() ?? typedMcp.ToolTitle?.Trim()
                    : ReadStringProperty(request, "ToolName", "ToolTitle", "Tool", "Method");
                var arguments = ReadPermissionObjectString(request, "Args", "Arguments", "Params", "Input");

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
                var path = request is PermissionRequestWrite writeRequest
                    ? writeRequest.FileName?.Trim()
                    : ReadStringProperty(request, "FileName", "Path", "FilePath", "Target", "Uri");
                return !string.IsNullOrWhiteSpace(path)
                    ? $"Write file: {path}"
                    : "File write";
            }
            case "read":
            {
                var path = request is PermissionRequestRead readRequest
                    ? readRequest.Path?.Trim()
                    : ReadStringProperty(request, "Path", "FilePath", "Target", "Uri");
                return !string.IsNullOrWhiteSpace(path)
                    ? $"Read file: {path}"
                    : "File read";
            }
            case "url":
            {
                var url = request is PermissionRequestUrl urlRequest
                    ? urlRequest.Url?.Trim()
                    : ReadStringProperty(request, "Url", "Uri");
                return !string.IsNullOrWhiteSpace(url)
                    ? $"Fetch URL: {url}"
                    : "URL fetch";
            }
            case "custom-tool":
            {
                var toolName = request is PermissionRequestCustomTool customToolRequest
                    ? customToolRequest.ToolName?.Trim()
                    : ReadStringProperty(request, "ToolName", "Tool", "Name");
                var arguments = ReadPermissionObjectString(request, "Args", "Arguments", "Params", "Input");

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
                var preview = ReadPermissionObjectString(request, "FullCommandText", "Command", "ToolName", "Path", "Url", "Uri")
                    ?? ReadPermissionObjectString(request, "Args", "Arguments", "Params", "Input");
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
        var command = request is PermissionRequestShell shellRequest
            ? shellRequest.FullCommandText?.Trim()
            : ReadStringProperty(request, "FullCommandText", "Command", "CommandLine")
              ?? ReadPermissionObjectRawString(request,
                  "FullCommandText",
                  "Command",
                  "CommandLine",
                  "CommandText",
                  "Cmd",
                  "ShellCommand",
                  "RawCommand",
                  "Text")
              ?? ReadNestedPermissionObjectRawString(request,
                  "Command",
                  "Payload",
                  "Input",
                  "Request",
                  "Details");

        return string.IsNullOrWhiteSpace(command)
            ? null
            : truncateForDisplay
                ? TrimPermissionPreview(command)
                : command.Trim();
    }

    private static string? ReadPermissionObjectString(object? instance, params string[] propertyNames)
    {
        if (instance == null)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            var text = ConvertPermissionExtensionValueToString(GetPropertyValue(instance, propertyName));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return TrimPermissionPreview(text);
            }
        }

        return null;
    }

    private static string? ReadPermissionObjectRawString(object? instance, params string[] propertyNames)
    {
        if (instance == null)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            var text = ConvertPermissionExtensionValueToRawString(GetPropertyValue(instance, propertyName));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? ReadNestedPermissionObjectRawString(object? instance, params string[] propertyNames)
    {
        if (instance == null)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            var value = GetPropertyValue(instance, propertyName);
            if (value == null)
            {
                continue;
            }

            var text = ExtractNestedRawCommandText(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
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

        var singleLine = Regex.Replace(text.Trim(), @"\s*[\r\n]+\s*", " ");
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
                _systemMessageConfig = CreateSystemMessage(_targetServer, _serverManager.Executors.Keys.ToList());

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
        _systemMessageConfig = CreateSystemMessage(_targetServer, _serverManager.Executors.Keys.ToList());
        _executor = new PowerShellExecutor(_targetServer);
        _executor.ExecutionMode = _executionMode;
        _executor.SetCustomSafeCommands(_configuredSafeCommands);
        _diagnosticTools = CreateDiagnosticTools();

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
        var result = await _serverManager.ConnectAdditionalServerAsync(
            serverName,
            _targetServer,
            _executionMode,
            _configuredSafeCommands,
            skipApproval);
        if (result.Success)
        {
            _systemMessageConfig = CreateSystemMessage(_targetServer, _serverManager.Executors.Keys.ToList());
        }

        return result;
    }

    private async Task<(bool Success, string? Error)> ConnectJeaServerAsync(
        string serverName,
        string configurationName,
        bool skipApproval = false)
    {
        var result = await _serverManager.ConnectJeaServerAsync(
            serverName,
            configurationName,
            _targetServer,
            _executionMode,
            _configuredSafeCommands,
            skipApproval);
        if (result.Success)
        {
            _systemMessageConfig = CreateSystemMessage(_targetServer, _serverManager.Executors.Keys.ToList());
        }

        return result;
    }

    private PowerShellExecutor? GetExecutorForServer(string serverName)
        => _serverManager.GetExecutorForServer(serverName, _targetServer, _executor);

    private async Task<bool> CloseAdditionalServerSessionAsync(string serverName)
    {
        var closed = await _serverManager.CloseAdditionalServerSessionAsync(serverName);
        if (closed)
        {
            _systemMessageConfig = CreateSystemMessage(_targetServer, _serverManager.Executors.Keys.ToList());
        }

        return closed;
    }

    private void SetExecutionMode(ExecutionMode mode)
    {
        _executionMode = mode;
        _executor.ExecutionMode = mode;
        _serverManager.SetExecutionMode(mode);
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
            ReasoningEffort = GetReasoningDisplay(GetSelectedModelInfo()),
            SessionInputTokens = _sessionUsageTracker.TotalInputTokens > 0 ? _sessionUsageTracker.TotalInputTokens : null,
            SessionOutputTokens = _sessionUsageTracker.TotalOutputTokens > 0 ? _sessionUsageTracker.TotalOutputTokens : null,
            SessionCostEstimate = GetSessionCostEstimateDisplay()
        };
    }

    private string? GetSessionCostEstimateDisplay()
    {
        if (_useByokOpenAi)
        {
            return _sessionUsageTracker.GetCostEstimateDisplay();
        }

        return _sessionPremiumRequestCost is > 0
            ? $"~{_sessionPremiumRequestCost.Value.ToString("0.#", CultureInfo.InvariantCulture)} premium reqs"
            : null;
    }

    public IReadOnlyList<(string Label, string Value)> GetStatusFields(bool includeMcpApprovals = false)
    {
        var fields = new List<(string Label, string Value)>();

        // -- Provider section --
        fields.Add((UI.ConsoleUI.StatusSectionSeparator, "Provider"));
        fields.Add(("Provider", ActiveProviderDisplayName));
        fields.Add(("Auth mode", _useByokOpenAi ? "BYOK (OpenAI)" : "GitHub Copilot"));
        fields.Add(("GitHub auth", _isGitHubCopilotAuthenticated ? "Authenticated" : "Not authenticated"));
        fields.Add(("BYOK", !string.IsNullOrWhiteSpace(_byokOpenAiApiKey) && LooksLikeUrl(_byokOpenAiBaseUrl) ? "Configured" : "Not configured"));
        var reasoningDisplay = GetReasoningDisplay(GetSelectedModelInfo());
        if (!string.IsNullOrWhiteSpace(reasoningDisplay))
        {
            fields.Add(("Reasoning", reasoningDisplay));
        }
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
            || !string.IsNullOrWhiteSpace(_configuredMonitoringMcpServer)
            || !string.IsNullOrWhiteSpace(_configuredTicketingMcpServer)
            || _configuredSkills.Any(v => !string.IsNullOrWhiteSpace(v))
            || _runtimeSkills.Count > 0
            || _configurationWarnings.Count > 0;

        if (hasMcpOrSkills)
        {
            fields.Add((UI.ConsoleUI.StatusSectionSeparator, "Capabilities"));
        }

        AddCapabilityField(fields, "MCP configured", _configuredMcpServers);
        AddCapabilityField(fields, "MCP used", _runtimeMcpServers.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        AddCapabilityField(fields, "Monitoring MCP", _configuredMonitoringMcpServer is null ? [] : [_configuredMonitoringMcpServer]);
        AddCapabilityField(fields, "Ticketing MCP", _configuredTicketingMcpServer is null ? [] : [_configuredTicketingMcpServer]);
        // Per-session and persisted MCP approvals are intentionally omitted from
        // the startup status panel and the post-action panel: at startup persisted
        // approvals are stale-looking (carried over from prior sessions) and the
        // session list is empty, while monitoring/ticketing servers are auto-
        // approved by role anyway. They remain available on demand via /status,
        // which passes includeMcpApprovals: true here, and continue to surface in
        // the HTML report under "MCP approved (session/persisted)".
        if (includeMcpApprovals)
        {
            AddCapabilityField(fields, "MCP approved (session)", _approvedMcpServersForSession.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            AddCapabilityField(fields, "MCP approved (persisted)", GetPersistedApprovedMcpServersSnapshot().OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        }
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
        var pricing = _modelDiscovery.GetActiveByokPricing(_selectedModel, _useByokOpenAi);
        return pricing == null ? null : ByokPriceInfo.FromService(pricing);
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

    private async Task RefreshSessionUsageMetricsAsync(CancellationToken cancellationToken)
    {
        if (_copilotSession == null || _useByokOpenAi)
        {
            return;
        }

        try
        {
            using var metricsTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            metricsTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            var metrics = await _copilotSession.Rpc.Usage.GetMetricsAsync(metricsTimeoutCts.Token);
            _sessionPremiumRequestCost = metrics.TotalPremiumRequestCost > 0
                ? metrics.TotalPremiumRequestCost
                : null;
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation/timeout so status-bar rendering cannot stall the session loop.
        }
        catch
        {
            // Ignore metrics fetch failures and leave the display empty rather than falling back to a guessed premium cost.
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

    internal static Dictionary<string, McpServerConfig> LoadMcpServersFromConfig(string? mcpConfigPath, List<string> warnings)
    {
        var result = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

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

    private static McpServerConfig? TryMapMcpServer(string serverName, JsonElement serverElement, out string? warning)
    {
        warning = null;

        if (serverElement.ValueKind != JsonValueKind.Object)
        {
            warning = $"Skipping MCP server '{serverName}': entry must be an object.";
            return null;
        }

        var type = GetOptionalString(serverElement, "type")?.Trim().ToLowerInvariant();
        if (type is "http" or "sse" or "remote")
        {
            var url = GetOptionalString(serverElement, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                warning = $"Skipping MCP server '{serverName}': remote server requires 'url'.";
                return null;
            }

            var remote = new McpHttpServerConfig
            {
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

        var local = new McpStdioServerConfig
        {
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
