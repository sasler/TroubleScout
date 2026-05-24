using System.Globalization;
using GitHub.Copilot;
using Spectre.Console;
using TroubleScout.Services;
using TroubleScout.Tools;
using TroubleScout.UI;

namespace TroubleScout;

/// <summary>
/// Manages the Copilot-powered troubleshooting session
/// </summary>
public partial class TroubleshootingSession : IAsyncDisposable
{
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
    private string? _lastAssistantMessage;
    private bool _isGitHubCopilotAuthenticated;
    private IReadOnlyList<string>? _configuredSafeCommands;
    private IReadOnlyDictionary<string, string>? _configuredSystemPromptOverrides;
    private string? _configuredSystemPromptAppend;
    private string? _configuredReasoningEffort;
    private string? _selectedReasoningEffort;
    private string? _configuredMonitoringMcpServer;
    private string? _configuredTicketingMcpServer;
    private readonly SessionPermissionHandler _permissionHandler;
    private readonly SessionEventTelemetry _telemetry;

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

    private static readonly string[] SlashCommands = SlashCommandDispatcher.SlashCommands;

    private EffectivePrimaryJeaSession? GetEffectivePrimaryJeaSessionInfo()
        => SessionTargetDisplay.GetEffectivePrimaryJeaSession(
            _initialJeaSession,
            _startupJeaFocusActive,
            _targetServer,
            _serverManager.Executors);

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
        ApplyReasoningEffortSetting(settings.ReasoningEffort);
        ConsoleUI.CurrentTheme = AppSettingsStore.NormalizeTheme(settings.Theme);
        _configuredMonitoringMcpServer = settings.MonitoringMcpServer;
        _configuredTicketingMcpServer = settings.TicketingMcpServer;
        _permissionHandler = new SessionPermissionHandler(
            () => _executionMode,
            () => _configuredSafeCommands,
            GetMcpServerRole,
            (command, reason, impact) => ConsoleUI.PromptCommandApproval(command, reason, impact: impact),
            ConsoleUI.PromptUrlApproval,
            ConsoleUI.PromptMcpApproval,
            settings);
        _permissionHandler.SeedPersistedMcpApprovals(settings.PersistedApprovedMcpServers);
        _telemetry = new SessionEventTelemetry(
            _sessionUsageTracker,
            _runtimeMcpServers,
            _runtimeSkills,
            () => _useByokOpenAi,
            GetSelectedModelInfo,
            GetActiveByokPricing,
            modelValue => _selectedModel = modelValue,
            reasoningEffort => _selectedReasoningEffort = reasoningEffort,
            version => _copilotVersion = version);
        _systemMessageConfig = CreateSystemMessage(_targetServer);
        _executor = new PowerShellExecutor(_targetServer);
        _executor.ExecutionMode = _executionMode;
        ApplySafeCommandsToAllExecutors(settings.SafeCommands);
        _diagnosticTools = CreateDiagnosticTools();
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

    public string TargetServer => _targetServer;
    public string ConnectionMode => _executor.GetConnectionMode();

    /// <summary>
    /// The effective primary target for display purposes. When a startup JEA session is active
    /// and the base target is localhost, the JEA server is the effective primary target.
    /// </summary>
    public string EffectiveTargetServer
        => SessionTargetDisplay.GetEffectiveTargetServer(_targetServer, GetEffectivePrimaryJeaSessionInfo());

    /// <summary>
    /// Connection mode reflecting the effective primary context. Returns JEA mode
    /// when a startup JEA session is the effective primary target.
    /// </summary>
    public string EffectiveConnectionMode
        => SessionTargetDisplay.GetEffectiveConnectionMode(_executor, GetEffectivePrimaryJeaSessionInfo());

    /// <summary>
    /// All target servers, with the effective primary listed first.
    /// When a startup JEA session is the effective primary, localhost is excluded from the list.
    /// </summary>
    public IReadOnlyList<string> EffectiveTargetServers
        => SessionTargetDisplay.GetEffectiveTargetServers(
            _targetServer,
            _serverManager.Executors,
            GetEffectivePrimaryJeaSessionInfo());

    public string? DefaultSessionTarget =>
        GetEffectivePrimaryJeaSessionInfo() is null ? null : _targetServer;

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

    /// <summary>
    /// Initialize the session and establish connections
    /// </summary>
    public async Task<bool> InitializeAsync(Action<string>? updateStatus = null, bool allowInteractiveSetup = false)
        => await SessionInitializationCoordinator.InitializeAsync(
            CreateInitializationRequest(),
            updateStatus,
            allowInteractiveSetup);

    private string? ResolveInitialSessionModel(IReadOnlyList<ModelInfo> availableModels)
        => _modelDiscovery.ResolveInitialSessionModel(_requestedModel, availableModels);

    /// <summary>
    /// Change the AI model by creating a new session
    /// </summary>
    public async Task<bool> ChangeModelAsync(string newModel, Action<string>? updateStatus = null)
        => await SessionModelSwitcher.ChangeModelAsync(newModel, CreateModelSwitchRequest(), updateStatus);

    internal async Task<bool> ChangeModelAsync(ModelSelectionEntry entry, Action<string>? updateStatus = null)
        => await SessionModelSwitcher.ChangeModelAsync(entry, CreateModelSwitchRequest(), updateStatus);

    private ModelSource ResolveTargetSource(string modelId)
        => _modelDiscovery.ResolveTargetSource(modelId, _useByokOpenAi, _isGitHubCopilotAuthenticated);

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
                    BuildModelDetailSummary,
                    IsCurrentModelAndSource))
            .ToList();

    private ModelSelectionEntry BuildModelSelectionEntry(ModelInfo model, string displayBase, ModelSource source)
        => _modelDiscovery.BuildModelSelectionEntry(
            model,
            displayBase,
            source,
            GetModelRateLabel,
            BuildModelDetailSummary,
            IsCurrentModelAndSource);

    private async Task RefreshAvailableModelsAsync()
        => await _modelDiscovery.RefreshAvailableModelsAsync(_copilotClient, _isGitHubCopilotAuthenticated, TryGetCliModelIdsAsync, TryGetByokProviderModelsAsync);

    private static string ToModelDisplayName(string modelId)
        => ModelDiscoveryManager.ToModelDisplayName(modelId);

    private bool IsByokConfigured()
    {
        return !string.IsNullOrWhiteSpace(_byokOpenAiApiKey) && LooksLikeUrl(_byokOpenAiBaseUrl);
    }

    private string GetModelRateLabel(ModelInfo model, ModelSource source)
        => _modelDiscovery.GetModelRateLabel(model, source);

    private static string BuildModelDetailSummary(ModelInfo model, ModelSource source)
        => SessionModelHelpers.BuildModelDetailSummary(model, source);

    private static string FormatCompactTokenCount(int value)
        => SessionModelHelpers.FormatCompactTokenCount(value);

    private ModelInfo? GetSelectedModelInfo()
        => _modelDiscovery.GetSelectedModelInfo(_selectedModel);

    private ModelInfo? GetModelInfo(string? modelId)
        => _modelDiscovery.GetModelInfo(modelId);

    private void ApplyReasoningEffortSetting(string? reasoningEffort)
    {
        _configuredReasoningEffort = ReasoningEffortHelper.Normalize(reasoningEffort);
    }

    private string? ResolveConfiguredReasoningEffort(string? modelId)
    {
        var configured = ReasoningEffortHelper.Normalize(_configuredReasoningEffort);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var model = GetModelInfo(modelId);
        if (!ReasoningEffortHelper.SupportsReasoningEffort(model))
        {
            return null;
        }

        var supported = ReasoningEffortHelper.GetSupportedReasoningEfforts(model);
        if (supported.Count == 0 || supported.Contains(configured, StringComparer.OrdinalIgnoreCase))
        {
            return configured;
        }

        return null;
    }

    private string? GetReasoningDisplay(ModelInfo? model)
    {
        if (!ReasoningEffortHelper.SupportsReasoningEffort(model))
        {
            return null;
        }

        var active = ReasoningEffortHelper.Normalize(_selectedReasoningEffort);
        if (!string.IsNullOrWhiteSpace(active))
        {
            return active;
        }

        var configured = ResolveConfiguredReasoningEffort(model?.Id);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var defaultReasoning = ReasoningEffortHelper.GetDefaultReasoningEffort(model);
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
        => SessionModelHelpers.ParseCliModelIds(helpText);

    private async Task WarnIfPowerShellVersionIsOldAsync()
    {
        var warning = await CopilotCliResolver.DetectPowerShellVersionWarningAsync();
        if (!string.IsNullOrWhiteSpace(warning))
        {
            ConsoleUI.ShowWarning(warning);
        }
    }

    private static string TrimSingleLine(string? text)
        => SessionInitializationDiagnostics.TrimSingleLine(text);

    private async Task ShowCopilotInitializationFailureAsync(
        string baseMessage,
        Exception? exception = null,
        bool includeDiagnostics = false)
        => await SessionInitializationDiagnostics.ShowCopilotInitializationFailureAsync(
            baseMessage,
            _debugMode,
            exception,
            includeDiagnostics);

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
        => await SessionMessageCoordinator.SendMessageAsync(
            userMessage,
            promptIndexOverride,
            cancellationToken,
            showPostAnalysisActionPrompt,
            forcePostAnalysisActionPrompt,
            CreateMessageRequest(),
            (prompt, promptIndex, token, showPrompt, forcePrompt) => SendMessageAsync(
                prompt,
                promptIndex,
                token,
                showPostAnalysisActionPrompt: showPrompt,
                forcePostAnalysisActionPrompt: forcePrompt));

    internal static async Task RunActivityWatchdogAsync(
        LiveThinkingIndicator indicator,
        Func<DateTime> getLastEventTime,
        Func<bool> hasStartedStreaming,
        CancellationToken cancellationToken)
        => await CopilotTurnRunner.RunActivityWatchdogAsync(
            new ConsoleTurnThinkingIndicator(indicator),
            getLastEventTime,
            hasStartedStreaming,
            ConsoleUI.ShowLiveStatusNotice,
            cancellationToken);

    internal static string? GetActivityWatchdogStatus(double idleSeconds)
        => CopilotTurnRunner.GetActivityWatchdogStatus(idleSeconds);

    private async Task<bool> ProcessPendingApprovalsAsync(CancellationToken cancellationToken)
        => await new PendingCommandApprovalProcessor(
                _diagnosticTools,
                RecordPrompt,
                (prompt, promptIndex, token, showPrompt, forcePrompt) => SendMessageAsync(
                    prompt,
                    promptIndex,
                    token,
                    showPostAnalysisActionPrompt: showPrompt,
                    forcePostAnalysisActionPrompt: forcePrompt))
            .ProcessAsync(cancellationToken);

    /// <summary>
    /// Callback for command approval prompts
    /// </summary>
    private Task<bool> PromptApprovalAsync(string command, string reason)
    {
        return Task.FromResult(ConsoleUI.PromptCommandApproval(command, reason) == ApprovalResult.Approved);
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
        _configuredMonitoringMcpServer = settings.MonitoringMcpServer;
        _configuredTicketingMcpServer = settings.TicketingMcpServer;
        _permissionHandler.UpdateAppSettings(settings);
        _permissionHandler.SeedPersistedMcpApprovals(settings.PersistedApprovedMcpServers);
        ApplySystemPromptSettings(settings.SystemPromptOverrides, settings.SystemPromptAppend);
        ApplyReasoningEffortSetting(settings.ReasoningEffort);
        ApplySafeCommandsToAllExecutors(settings.SafeCommands);
        _systemMessageConfig = CreateSystemMessage(_targetServer, _serverManager.Executors.Keys.ToList());
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

    private IReadOnlyList<string> GetAvailableMcpRoleServerNames()
    {
        var servers = McpConfigurationService.LoadServers(_mcpConfigPath).Servers;
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

    private void ApplySystemPromptSettings(IReadOnlyDictionary<string, string>? overrides, string? append)
    {
        var normalized = SettingsWorkflowService.NormalizeSystemPromptSettings(overrides, append);
        _configuredSystemPromptOverrides = normalized.Overrides;
        _configuredSystemPromptAppend = normalized.Append;
    }

    /// <summary>
    /// Run the interactive session loop
    /// </summary>
    public async Task RunInteractiveLoopAsync()
        => await InteractiveSessionLoop.RunAsync(
            _executionMode,
            SlashCommands,
            CreateSlashCommandDispatcher,
            RecordPrompt,
            (input, promptIndex, token) => SendMessageAsync(
                input,
                promptIndex,
                token,
                showPostAnalysisActionPrompt: true,
                forcePostAnalysisActionPrompt: false));

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
        => SlashCommandDispatcher.GetFirstToken(input);

    private static bool IsSlashCommandInvocation(string input, string command)
        => SlashCommandDispatcher.IsInvocation(input, command);

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
        => ByokModelDiscoveryService.LooksLikeUrl(value);

    private IReadOnlyList<string> BuildByokModelEndpointCandidates(string baseUrl)
        => _byokProviderManager.BuildByokModelEndpointCandidates(baseUrl);

    private async Task<List<ModelInfo>> TryGetByokProviderModelsAsync()
        => await ByokModelDiscoveryService.TryGetByokProviderModelsAsync(
            _byokOpenAiApiKey,
            _byokOpenAiBaseUrl,
            _byokProviderManager,
            _modelDiscovery);

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
            PromptByokModelSelection,
            CreateCopilotSessionAsync,
            SettingsWorkflowService.SaveByokSettings,
            OpenAiApiKeyEnvironmentVariable,
            updateStatus);

    private string? PromptByokModelSelection(string currentModel, IReadOnlyList<ModelInfo> models)
    {
        var entries = models
            .Select(model => BuildModelSelectionEntry(model, ToModelDisplayName(model.Id), ModelSource.Byok))
            .ToList();

        return ConsoleUI.PromptModelSelection(currentModel, entries)?.ModelId;
    }

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
        => SlashCommandDispatcher.IsBareExitCommand(input);

    private int RecordPrompt(string prompt) => _historyTracker.RecordPrompt(prompt);

    private void SetPromptReply(int promptIndex, string reply) => _historyTracker.SetPromptReply(promptIndex, reply);

    private void RecordCommandAction(CommandActionLog actionLog) => _historyTracker.RecordCommandAction(actionLog);

    private void RecordMcpToolAction(ToolExecutionStartEvent toolStart) => _historyTracker.RecordMcpToolAction(toolStart);

    private void ClearRecordedConversationHistory() => _historyTracker.ClearRecordedConversationHistory();

    private void ReplaceRecordedConversationHistory(IReadOnlyList<ReportPromptEntry> prompts) => _historyTracker.ReplaceRecordedConversationHistory(prompts);

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

    private static string? GetByokWireApi(string? model)
        => SessionModelHelpers.GetByokWireApi(model);

    private static bool IsGpt5FamilyModel(string? model)
        => SessionModelHelpers.IsGpt5FamilyModel(model);

    private async Task<bool> ReconnectAsync(string newServer, Action<string>? updateStatus = null)
        => await SessionTargetCoordinator.ReconnectAsync(newServer, CreateTargetRequest(), updateStatus);

    private async Task<(bool Success, string? Error)> ConnectAdditionalServerAsync(string serverName, bool skipApproval = false)
        => await SessionTargetCoordinator.ConnectAdditionalServerAsync(serverName, CreateTargetRequest(), skipApproval);

    private async Task<(bool Success, string? Error)> ConnectJeaServerAsync(
        string serverName,
        string configurationName,
        bool skipApproval = false)
        => await SessionTargetCoordinator.ConnectJeaServerAsync(serverName, configurationName, CreateTargetRequest(), skipApproval);

    private PowerShellExecutor? GetExecutorForServer(string serverName)
        => _serverManager.GetExecutorForServer(serverName, _targetServer, _executor);

    private async Task<bool> CloseAdditionalServerSessionAsync(string serverName)
        => await SessionTargetCoordinator.CloseAdditionalServerSessionAsync(serverName, CreateTargetRequest());

    private static void AddContextUsageField(List<(string Label, string Value)> fields, int? usedContext, int? maxContext)
        => SessionStatusBuilder.AddContextUsageField(fields, usedContext, maxContext);

    private ByokPriceInfo? GetActiveByokPricing()
    {
        var pricing = _modelDiscovery.GetActiveByokPricing(_selectedModel, _useByokOpenAi);
        return pricing;
    }

    private string CreateSessionId()
    {
        var sequence = Interlocked.Increment(ref _sessionCounter);
        return $"TS-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{sequence:D3}";
    }

}
