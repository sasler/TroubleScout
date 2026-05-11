using GitHub.Copilot.SDK;
using Spectre.Console;
using TroubleScout.Services;
using TroubleScout.Tools;
using TroubleScout.UI;

namespace TroubleScout;

public partial class TroubleshootingSession
{
    private DiagnosticTools CreateDiagnosticTools() =>
        new(_executor, PromptApprovalAsync, _targetServer, RecordCommandAction,
            s => ConnectAdditionalServerAsync(s), GetExecutorForServer, CloseAdditionalServerSessionAsync,
            (serverName, configurationName) => ConnectJeaServerAsync(serverName, configurationName));

    private SystemMessageConfig CreateSystemMessage(string targetServer, IReadOnlyCollection<string>? additionalServerNames = null)
        => SessionSystemPromptFactory.Create(new SessionSystemPromptRequest(
            targetServer,
            _targetServer,
            additionalServerNames,
            GetEffectivePrimaryJeaSession,
            _serverManager.Executors,
            _configuredSystemPromptOverrides,
            _configuredSystemPromptAppend,
            _configuredMonitoringMcpServer,
            _configuredTicketingMcpServer,
            _executionMode));

    private (string ServerName, string ConfigurationName, PowerShellExecutor Executor)? GetEffectivePrimaryJeaSession()
        => GetEffectivePrimaryJeaSessionInfo() is { } jeaSession
            ? (jeaSession.ServerName, jeaSession.ConfigurationName, jeaSession.Executor)
            : null;

    private SessionInitializationRequest CreateInitializationRequest()
        => new()
        {
            IsInitialized = () => _isInitialized,
            MarkInitialized = () => _isInitialized = true,
            TargetServer = _targetServer,
            Executor = _executor,
            AdditionalInitialServers = _additionalInitialServers,
            InitialJeaSession = _initialJeaSession,
            ServerManager = _serverManager,
            DebugMode = _debugMode,
            ByokExplicitlyRequested = _byokExplicitlyRequested,
            ModelExplicitlyRequested = _modelExplicitlyRequested,
            OpenAiApiKeyEnvironmentVariable = OpenAiApiKeyEnvironmentVariable,
            RequestedModel = _requestedModel,
            GetSelectedModel = () => _selectedModel,
            GetUseByokOpenAi = () => _useByokOpenAi,
            SetUseByokOpenAi = value => _useByokOpenAi = value,
            GetByokOpenAiApiKey = () => _byokOpenAiApiKey,
            SetCopilotClient = value => _copilotClient = value,
            SetGitHubAuthenticated = value => _isGitHubCopilotAuthenticated = value,
            SetSystemMessage = value => _systemMessageConfig = value,
            CreateSystemMessage = CreateSystemMessage,
            ConnectAdditionalServer = ConnectAdditionalServerAsync,
            ConnectJeaServer = ConnectJeaServerAsync,
            WarnIfPowerShellVersionIsOld = WarnIfPowerShellVersionIsOldAsync,
            IsGitHubAuthenticated = IsGitHubAuthenticatedAsync,
            CreateCopilotSession = CreateCopilotSessionAsync,
            GetMergedModelList = GetMergedModelListAsync,
            ResolveInitialSessionModel = ResolveInitialSessionModel,
            RefreshAvailableModels = RefreshAvailableModelsAsync,
            ModelDiscovery = _modelDiscovery,
            ConfigurationWarnings = _configurationWarnings
        };

    private SessionModelSwitchRequest CreateModelSwitchRequest()
        => new()
        {
            CopilotClient = _copilotClient,
            ModelDiscovery = _modelDiscovery,
            ResolveTargetSource = ResolveTargetSource,
            IsByokConfigured = IsByokConfigured,
            IsGitHubCopilotAuthenticated = () => _isGitHubCopilotAuthenticated,
            SetUseByokOpenAi = value => _useByokOpenAi = value,
            DisposeCurrentSession = async () =>
            {
                if (_copilotSession != null)
                {
                    await _copilotSession.DisposeAsync();
                    _copilotSession = null;
                }
            },
            CreateCopilotSession = CreateCopilotSessionAsync
        };

    private SessionMessageRequest CreateMessageRequest()
        => new()
        {
            CopilotSession = _copilotSession,
            HistoryTracker = _historyTracker,
            SessionUsageTracker = _sessionUsageTracker,
            Telemetry = _telemetry,
            ToolDescriptions = ToolDescriptions,
            GetToolInvocationCount = () => System.Threading.Volatile.Read(ref _toolInvocationCount),
            IncrementToolInvocation = () => _toolInvocationCount++,
            BuildStatusBarInfo = BuildStatusBarInfo,
            ProcessPendingApprovals = ProcessPendingApprovalsAsync,
            RecordPrompt = RecordPrompt,
            SetPromptReply = SetPromptReply,
            SetLastAssistantMessage = value => _lastAssistantMessage = value,
            RecordMcpToolAction = RecordMcpToolAction
        };

    private CopilotSessionLifecycleRequest CreateSessionLifecycleRequest()
        => new()
        {
            CopilotClient = _copilotClient,
            McpConfigPath = _mcpConfigPath,
            SkillDirectories = _skillDirectories,
            DisabledSkills = _disabledSkills,
            ConfiguredSkills = _configuredSkills,
            ConfiguredMcpServers = _configuredMcpServers,
            RuntimeMcpServers = _runtimeMcpServers,
            RuntimeSkills = _runtimeSkills,
            ConfigurationWarnings = _configurationWarnings,
            BuildSessionConfig = BuildSessionConfig,
            SetCopilotSession = value => _copilotSession = value,
            SetSelectedReasoningEffort = value => _selectedReasoningEffort = value,
            SetSelectedModel = value => _selectedModel = value,
            ResetStateForNewAiSession = ResetStateForNewAiSession,
            UseByokOpenAi = () => _useByokOpenAi
        };

    private async Task<bool> CreateCopilotSessionAsync(string? model, Action<string>? updateStatus)
        => await CopilotSessionLifecycle.CreateCopilotSessionAsync(model, updateStatus, CreateSessionLifecycleRequest());

    private void ResetStateForNewAiSession()
    {
        _sessionId = CreateSessionId();
        _toolInvocationCount = 0;
        _sessionUsageTracker.Reset();
        _telemetry.ResetForNewSession();
        _permissionHandler.ResetUrlApprovals();
    }

    internal SessionConfig BuildSessionConfig(string? model)
    {
        var mcpServers = McpConfigurationService.LoadServers(_mcpConfigPath).Servers;
        return BuildSessionConfig(model, mcpServers);
    }

    private SessionConfig BuildSessionConfig(string? model, IReadOnlyDictionary<string, McpServerConfig> availableMcpServers)
    {
        var provider = _useByokOpenAi
            ? new ProviderConfig
            {
                Type = "openai",
                BaseUrl = _byokOpenAiBaseUrl,
                ApiKey = _byokOpenAiApiKey,
                WireApi = GetByokWireApi(model)
            }
            : null;

        return CopilotSessionLifecycle.BuildSessionConfig(new SessionConfigBuildRequest(
            model,
            ResolveConfiguredReasoningEffort(model),
            _systemMessageConfig,
            _useByokOpenAi,
            _diagnosticTools.GetTools().ToList(),
            availableMcpServers,
            _configuredMonitoringMcpServer,
            _configuredTicketingMcpServer,
            _telemetry.HandleSessionLifecycleStateEvent,
            _permissionHandler.HandleAsync,
            _configurationWarnings,
            provider,
            _skillDirectories,
            _disabledSkills));
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

    private SessionTargetRequest CreateTargetRequest()
        => new()
        {
            GetTargetServer = () => _targetServer,
            SetTargetServer = value => _targetServer = value,
            GetExecutor = () => _executor,
            SetExecutor = value => _executor = value,
            ServerManager = _serverManager,
            GetStartupJeaFocusActive = () => _startupJeaFocusActive,
            SetStartupJeaFocusActive = value => _startupJeaFocusActive = value,
            GetEffectiveTargetServer = () => EffectiveTargetServer,
            RefreshSystemMessage = () => CreateSystemMessage(_targetServer, _serverManager.Executors.Keys.ToList()),
            SetSystemMessage = value => _systemMessageConfig = value,
            ExecutionMode = _executionMode,
            ConfiguredSafeCommands = _configuredSafeCommands,
            GetCopilotClient = () => _copilotClient,
            GetCopilotSession = () => _copilotSession,
            SetCopilotSession = value => _copilotSession = value,
            GetSelectedModel = () => _selectedModel,
            CreateCopilotSession = CreateCopilotSessionAsync,
            CreateDiagnosticTools = CreateDiagnosticTools,
            SetDiagnosticTools = value => _diagnosticTools = value
        };

    private void SetExecutionMode(ExecutionMode mode)
    {
        _executionMode = mode;
        _executor.ExecutionMode = mode;
        _serverManager.SetExecutionMode(mode);
    }

    internal StatusBarInfo BuildStatusBarInfo()
        => SessionStatusBuilder.BuildStatusBarInfo(CreateStatusSnapshot());

    public IReadOnlyList<(string Label, string Value)> GetStatusFields(bool includeMcpApprovals = false)
        => SessionStatusBuilder.GetStatusFields(CreateStatusSnapshot(), includeMcpApprovals);

    private SessionStatusSnapshot CreateStatusSnapshot()
        => new(
            SelectedModel,
            ActiveProviderDisplayName,
            _useByokOpenAi,
            _isGitHubCopilotAuthenticated,
            _byokOpenAiApiKey,
            _byokOpenAiBaseUrl,
            GetReasoningDisplay(GetSelectedModelInfo()),
            _sessionId,
            _toolInvocationCount,
            _telemetry.LastUsage,
            _sessionUsageTracker,
            _telemetry.SessionPremiumRequestCost,
            _configuredMcpServers,
            _runtimeMcpServers,
            _configuredMonitoringMcpServer,
            _configuredTicketingMcpServer,
            _configuredSkills,
            _runtimeSkills,
            _configurationWarnings,
            _permissionHandler.GetApprovedMcpServersSnapshot(),
            _permissionHandler.GetPersistedApprovedMcpServersSnapshot(),
            _executionMode.ToString(),
            EffectiveTargetServer);

    private IReadOnlyList<string>? GetAdditionalTargetsForDisplay()
        => SessionTargetDisplay.GetAdditionalTargetsForDisplay(EffectiveTargetServers);

    private SlashCommandDispatcher CreateSlashCommandDispatcher() =>
        new(new SlashCommandHandlers
        {
            ShowHelp = ConsoleUI.ShowHelp,
            ShowStatus = includeMcpApprovals => ConsoleUI.ShowStatusPanel(
                EffectiveTargetServer,
                EffectiveConnectionMode,
                _copilotSession != null,
                SelectedModel,
                _executionMode,
                GetStatusFields(includeMcpApprovals: includeMcpApprovals),
                GetAdditionalTargetsForDisplay(),
                DefaultSessionTarget),
            ShowStats = () => ConsoleUI.ShowStatsPanel(
                completedTurns: _sessionUsageTracker.CompletedTurns,
                failedTurns: _sessionUsageTracker.FailedTurns,
                cancelledTurns: _sessionUsageTracker.CancelledTurns,
                totalInputTokens: _sessionUsageTracker.TotalInputTokens,
                totalOutputTokens: _sessionUsageTracker.TotalOutputTokens,
                p50Latency: _sessionUsageTracker.GetTurnElapsedQuantile(0.5),
                p95Latency: _sessionUsageTracker.GetTurnElapsedQuantile(0.95),
                totalToolCalls: _sessionUsageTracker.TotalToolCalls,
                p50ToolsPerTurn: _sessionUsageTracker.GetTurnToolCountQuantile(0.5),
                p95ToolsPerTurn: _sessionUsageTracker.GetTurnToolCountQuantile(0.95),
                costEstimate: _sessionUsageTracker.GetCostEstimateDisplay()),
            ShowHistory = () => ConsoleUI.ShowCommandHistory(_executor.GetCommandHistory()),
            GetExecutionMode = () => _executionMode,
            SetExecutionMode = SetExecutionMode,
            SetConsoleExecutionMode = ConsoleUI.SetExecutionMode,
            GetTheme = () => ConsoleUI.CurrentTheme,
            SetTheme = theme => ConsoleUI.CurrentTheme = theme,
            PersistTheme = SettingsWorkflowService.PersistThemeSetting,
            GetSelectedModelInfo = GetSelectedModelInfo,
            GetSelectedModelName = () => SelectedModel,
            GetSelectedModelId = () => _selectedModel,
            HasCopilotClient = () => _copilotClient != null,
            IsDebugMode = () => _debugMode,
            RefreshAvailableModels = RefreshAvailableModelsAsync,
            GetAvailableModelCount = () => _modelDiscovery.AvailableModels.Count,
            GetModelSelectionEntries = GetModelSelectionEntries,
            PromptModelSelection = ConsoleUI.PromptModelSelection,
            IsCurrentModelAndSource = IsCurrentModelAndSource,
            PromptModelSwitchBehavior = ConsoleUI.PromptModelSwitchBehavior,
            ChangeModel = ChangeModelAsync,
            ClearRecordedHistory = ClearRecordedConversationHistory,
            RunSecondOpinion = async (previousModel, selectedModel, prompts) =>
                await InteractiveSessionLoop.RunCancelableAiOperationAsync(token =>
                    RequestSecondOpinionAsync(previousModel, selectedModel, prompts, token)),
            GetByokBaseUrl = () => _byokOpenAiBaseUrl,
            GetDefaultByokModel = () => _selectedModel ?? _requestedModel,
            GetOpenAiApiKeyEnvironmentVariable = () => OpenAiApiKeyEnvironmentVariable,
            GetEnvironmentVariable = Environment.GetEnvironmentVariable,
            SaveByokSettings = SettingsWorkflowService.SaveByokSettings,
            ClearByokRuntimeState = () =>
            {
                _useByokOpenAi = false;
                _byokOpenAiApiKey = null;
            },
            ConfigureByokOpenAi = ConfigureByokOpenAiAsync,
            GetConfiguredReasoningEffort = () => _configuredReasoningEffort,
            ApplyReasoningEffortSetting = ApplyReasoningEffortSetting,
            SaveReasoningEffortState = SettingsWorkflowService.SaveReasoningEffortState,
            GetReasoningDisplay = GetReasoningDisplay,
            PromptReasoningEffort = ConsoleUI.PromptReasoningEffort,
            HasActiveCopilotSession = () => _copilotSession != null,
            RunWithSpinnerAsync = ConsoleUI.RunWithSpinnerAsync,
            RecreateCopilotSession = async (targetModel, updateStatus) =>
            {
                if (_copilotSession != null)
                {
                    await _copilotSession.DisposeAsync();
                    _copilotSession = null;
                }

                return await CreateCopilotSessionAsync(targetModel, updateStatus);
            },
            ReconnectServer = ReconnectAsync,
            ConnectAdditionalServer = ConnectAdditionalServerAsync,
            ConnectJeaServer = ConnectJeaServerAsync,
            PromptCommandApproval = (command, reason) =>
                ConsoleUI.PromptCommandApproval(command, reason) == ApprovalResult.Approved,
            PromptText = () => ConsoleUI.GetUserInput(),
            ResetConversation = () => ResetConversationAsync(),
            ClearConsole = Console.Clear,
            ShowBanner = () => ConsoleUI.ShowBanner(),
            ShowWelcomeMessage = ConsoleUI.ShowWelcomeMessage,
            GetWelcomeHint = GetWelcomeHint,
            GetSessionId = () => _sessionId,
            EnsureSettingsFile = () => _ = AppSettingsStore.Load(),
            GetSettingsPath = () => AppSettingsStore.SettingsPath,
            OpenSettingsEditor = SettingsWorkflowService.TryOpenSettingsEditorAsync,
            ReloadSettings = ReloadSafeCommandsFromSettings,
            GetPersistedTheme = () => AppSettingsStore.Load().Theme,
            InvalidateModelCache = _modelDiscovery.InvalidateMergedModelListCache,
            LoginAndCreateGitHubSession = LoginAndCreateGitHubSessionAsync,
            GetConfiguredMonitoringMcpServer = () => _configuredMonitoringMcpServer,
            GetConfiguredTicketingMcpServer = () => _configuredTicketingMcpServer,
            GetAvailableMcpRoleServerNames = GetAvailableMcpRoleServerNames,
            PromptMcpRoleSelection = ConsoleUI.PromptMcpRoleSelection,
            SaveMcpRoleSettings = SettingsWorkflowService.SaveMcpRoleSettings,
            RefreshServerContext = () =>
                _systemMessageConfig = CreateSystemMessage(_targetServer, _serverManager.Executors.Keys.ToList()),
            RecreateCurrentCopilotSession = RecreateCurrentCopilotSessionAsync,
            ShowModelSelectionSummary = () => ConsoleUI.ShowModelSelectionSummary(SelectedModel, GetSelectedModelDetails()),
            GetJeaAllowedCommands = serverName =>
                _serverManager.Executors.TryGetValue(serverName, out var executor) && executor.JeaAllowedCommands is { Count: > 0 } commands
                    ? commands
                    : Array.Empty<string>(),
            ShowJeaDiscoveredCommands = (serverName, configurationName, commands) =>
            {
                AnsiConsole.MarkupLine($"[grey]Discovered commands for {Markup.Escape(serverName)} ({Markup.Escape(configurationName)}):[/]");
                foreach (var commandName in commands.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                {
                    AnsiConsole.MarkupLine($"  [grey]-[/] {Markup.Escape(commandName)}");
                }
            },
            GetLastAssistantMessage = () => _lastAssistantMessage,
            GetRecordedPrompts = GetRecordedPromptSnapshot,
            GetReportSessionSummary = () => SessionStatusBuilder.BuildReportSessionSummary(CreateStatusSnapshot()),
            ReplaceRecordedPrompts = ReplaceRecordedConversationHistory,
            HasRecordedHistory = HasRecordedConversationHistory,
            GetApprovedMcpServers = _permissionHandler.GetApprovedMcpServersSnapshot,
            GetPersistedApprovedMcpServers = _permissionHandler.GetPersistedApprovedMcpServersSnapshot,
            GetMcpServerRole = GetMcpServerRole,
            RemovePersistedMcpApproval = _permissionHandler.RemovePersistedMcpApproval,
            RemoveSessionMcpApproval = _permissionHandler.RemoveSessionMcpApproval,
            ClearPersistedMcpApprovals = _permissionHandler.ClearPersistedMcpApprovals,
            ClearSessionMcpApprovals = _permissionHandler.ClearSessionMcpApprovals,
            ConfirmOverwrite = targetPath => AnsiConsole.Confirm(SafeMarkup.Interpolate($"File '{targetPath}' already exists. Overwrite?"), defaultValue: false),
            ConfirmTranscriptLoadReplace = () => AnsiConsole.Confirm("Loading this transcript will replace the current recorded session history. Continue?", defaultValue: false),
            ShowInfo = ConsoleUI.ShowInfo,
            ShowWarning = ConsoleUI.ShowWarning,
            ShowSuccess = ConsoleUI.ShowSuccess,
            ShowError = ConsoleUI.ShowError
        });
}
