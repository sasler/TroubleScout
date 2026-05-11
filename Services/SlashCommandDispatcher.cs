using GitHub.Copilot.SDK;
using System.Text;

namespace TroubleScout.Services;

internal sealed record SlashCommandResult(bool Handled, bool ExitRequested)
{
    internal static SlashCommandResult NotHandled { get; } = new(false, false);
    internal static SlashCommandResult HandledCommand { get; } = new(true, false);
    internal static SlashCommandResult Exit { get; } = new(true, true);
}

internal sealed record SlashCommandInput(string Original, string Trimmed, string Lower, string FirstToken)
{
    internal static SlashCommandInput? Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim();
        var lower = trimmed.ToLowerInvariant();
        return new SlashCommandInput(input, trimmed, lower, SlashCommandDispatcher.GetFirstToken(lower));
    }
}

internal sealed class SlashCommandHandlers
{
    internal Action ShowHelp { get; init; } = static () => { };
    internal Action<bool> ShowStatus { get; init; } = static _ => { };
    internal Action ShowStats { get; init; } = static () => { };
    internal Action ShowHistory { get; init; } = static () => { };
    internal Func<ExecutionMode> GetExecutionMode { get; init; } = static () => ExecutionMode.Safe;
    internal Action<ExecutionMode> SetExecutionMode { get; init; } = static _ => { };
    internal Action<ExecutionMode> SetConsoleExecutionMode { get; init; } = static _ => { };
    internal Func<string> GetTheme { get; init; } = static () => "dark";
    internal Action<string> SetTheme { get; init; } = static _ => { };
    internal Action<string> PersistTheme { get; init; } = static _ => { };
    internal Func<ModelInfo?> GetSelectedModelInfo { get; init; } = static () => null;
    internal Func<string?> GetSelectedModelName { get; init; } = static () => null;
    internal Func<string?> GetSelectedModelId { get; init; } = static () => null;
    internal Func<bool> HasCopilotClient { get; init; } = static () => false;
    internal Func<bool> IsDebugMode { get; init; } = static () => false;
    internal Func<Task> RefreshAvailableModels { get; init; } = static () => Task.CompletedTask;
    internal Func<int> GetAvailableModelCount { get; init; } = static () => 0;
    internal Func<IReadOnlyList<ModelSelectionEntry>> GetModelSelectionEntries { get; init; } = static () => Array.Empty<ModelSelectionEntry>();
    internal Func<string, IReadOnlyList<ModelSelectionEntry>, ModelSelectionEntry?> PromptModelSelection { get; init; } = static (_, _) => null;
    internal Func<ModelSelectionEntry, bool> IsCurrentModelAndSource { get; init; } = static _ => false;
    internal Func<string, string, ModelSwitchBehavior?> PromptModelSwitchBehavior { get; init; } = static (_, _) => ModelSwitchBehavior.CleanSession;
    internal Func<ModelSelectionEntry, Action<string>?, Task<bool>> ChangeModel { get; init; } = static (_, _) => Task.FromResult(false);
    internal Action ClearRecordedHistory { get; init; } = static () => { };
    internal Func<string, string, IReadOnlyList<ReportPromptEntry>, Task> RunSecondOpinion { get; init; } = static (_, _, _) => Task.CompletedTask;
    internal Func<string?> GetByokBaseUrl { get; init; } = static () => null;
    internal Func<string?> GetDefaultByokModel { get; init; } = static () => null;
    internal Func<string> GetOpenAiApiKeyEnvironmentVariable { get; init; } = static () => "OPENAI_API_KEY";
    internal Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;
    internal Action<bool, string?, string?> SaveByokSettings { get; init; } = static (_, _, _) => { };
    internal Action ClearByokRuntimeState { get; init; } = static () => { };
    internal Func<string, string, string?, Action<string>?, Task<bool>> ConfigureByokOpenAi { get; init; } = static (_, _, _, _) => Task.FromResult(false);
    internal Func<string?> GetConfiguredReasoningEffort { get; init; } = static () => null;
    internal Action<string?> ApplyReasoningEffortSetting { get; init; } = static _ => { };
    internal Action<string?> SaveReasoningEffortState { get; init; } = static _ => { };
    internal Func<ModelInfo, string?> GetReasoningDisplay { get; init; } = static _ => null;
    internal Func<string?, IReadOnlyList<string>, string?, string?> PromptReasoningEffort { get; init; } = static (current, _, _) => current;
    internal Func<bool> HasActiveCopilotSession { get; init; } = static () => false;
    internal Func<string, Func<Action<string>, Task<bool>>, Task<bool>> RunWithSpinnerAsync { get; init; } = static async (_, action) => await action(static _ => { });
    internal Func<string, Action<string>?, Task<bool>> RecreateCopilotSession { get; init; } = static (_, _) => Task.FromResult(false);
    internal Func<string, Action<string>?, Task<bool>> ReconnectServer { get; init; } = static (_, _) => Task.FromResult(false);
    internal Func<string, bool, Task<(bool Success, string? Error)>> ConnectAdditionalServer { get; init; } = static (_, _) => Task.FromResult((false, (string?)null));
    internal Func<string, string, bool, Task<(bool Success, string? Error)>> ConnectJeaServer { get; init; } = static (_, _, _) => Task.FromResult((false, (string?)null));
    internal Func<string, string, bool> PromptCommandApproval { get; init; } = static (_, _) => false;
    internal Func<string> PromptText { get; init; } = static () => string.Empty;
    internal Func<Task<bool>> ResetConversation { get; init; } = static () => Task.FromResult(false);
    internal Action ClearConsole { get; init; } = static () => { };
    internal Action ShowBanner { get; init; } = static () => { };
    internal Action<string?> ShowWelcomeMessage { get; init; } = static _ => { };
    internal Func<string?> GetWelcomeHint { get; init; } = static () => null;
    internal Func<string> GetSessionId { get; init; } = static () => "n/a";
    internal Action EnsureSettingsFile { get; init; } = static () => { };
    internal Func<string> GetSettingsPath { get; init; } = static () => AppSettingsStore.SettingsPath;
    internal Func<string, Task<string?>> OpenSettingsEditor { get; init; } = static _ => Task.FromResult((string?)null);
    internal Action ReloadSettings { get; init; } = static () => { };
    internal Func<string?> GetPersistedTheme { get; init; } = static () => null;
    internal Action InvalidateModelCache { get; init; } = static () => { };
    internal Func<Action<string>, Task<bool>> LoginAndCreateGitHubSession { get; init; } = static _ => Task.FromResult(false);
    internal Func<string?> GetConfiguredMonitoringMcpServer { get; init; } = static () => null;
    internal Func<string?> GetConfiguredTicketingMcpServer { get; init; } = static () => null;
    internal Func<IReadOnlyList<string>> GetAvailableMcpRoleServerNames { get; init; } = static () => Array.Empty<string>();
    internal Func<string?, string?, IReadOnlyList<string>, (string? Monitoring, string? Ticketing)> PromptMcpRoleSelection { get; init; } = static (monitoring, ticketing, _) => (monitoring, ticketing);
    internal Action<string?, string?> SaveMcpRoleSettings { get; init; } = static (_, _) => { };
    internal Action RefreshServerContext { get; init; } = static () => { };
    internal Func<Task<(bool Success, string? Error)>> RecreateCurrentCopilotSession { get; init; } = static () => Task.FromResult((true, (string?)null));
    internal Action ShowModelSelectionSummary { get; init; } = static () => { };
    internal Func<string, IReadOnlyCollection<string>> GetJeaAllowedCommands { get; init; } = static _ => Array.Empty<string>();
    internal Action<string, string, IReadOnlyCollection<string>> ShowJeaDiscoveredCommands { get; init; } = static (_, _, _) => { };
    internal Func<string?> GetLastAssistantMessage { get; init; } = static () => null;
    internal Func<List<ReportPromptEntry>> GetRecordedPrompts { get; init; } = static () => [];
    internal Func<ReportSessionSummary?> GetReportSessionSummary { get; init; } = static () => null;
    internal Action<IReadOnlyList<ReportPromptEntry>> ReplaceRecordedPrompts { get; init; } = static _ => { };
    internal Func<bool> HasRecordedHistory { get; init; } = static () => false;
    internal Func<IReadOnlyCollection<string>> GetApprovedMcpServers { get; init; } = static () => Array.Empty<string>();
    internal Func<IReadOnlyList<string>> GetPersistedApprovedMcpServers { get; init; } = static () => Array.Empty<string>();
    internal Func<string, string?> GetMcpServerRole { get; init; } = static _ => null;
    internal Func<string, bool> RemovePersistedMcpApproval { get; init; } = static _ => false;
    internal Func<string, bool> RemoveSessionMcpApproval { get; init; } = static _ => false;
    internal Func<int> ClearPersistedMcpApprovals { get; init; } = static () => 0;
    internal Action ClearSessionMcpApprovals { get; init; } = static () => { };
    internal Func<string> CreateReportPath { get; init; } = SlashCommandDispatcher.CreateDefaultReportPath;
    internal Action<string, string> WriteReportHtml { get; init; } = SlashCommandDispatcher.WriteReportHtmlFile;
    internal Action<string> OpenReport { get; init; } = SlashCommandDispatcher.OpenReportInDefaultBrowser;
    internal Func<string, bool> ConfirmOverwrite { get; init; } = static _ => false;
    internal Func<bool> ConfirmTranscriptLoadReplace { get; init; } = static () => false;
    internal Action<string> ShowInfo { get; init; } = static _ => { };
    internal Action<string> ShowWarning { get; init; } = static _ => { };
    internal Action<string> ShowSuccess { get; init; } = static _ => { };
    internal Action<string, string> ShowError { get; init; } = static (_, _) => { };
}

internal sealed class SlashCommandDispatcher
{
    private readonly SlashCommandHandlers _handlers;

    internal SlashCommandDispatcher(SlashCommandHandlers handlers)
    {
        _handlers = handlers;
    }

    internal static readonly string[] SlashCommands = SlashCommandRegistry.SlashCommands;

    internal SlashCommandResult Dispatch(string input)
    {
        var parsed = SlashCommandInput.Parse(input);
        return parsed is null ? SlashCommandResult.NotHandled : Dispatch(parsed);
    }

    private SlashCommandResult Dispatch(SlashCommandInput input)
    {
        if (input.FirstToken is "/exit" or "/quit" || IsBareExitCommand(input.Lower))
        {
            _handlers.ShowInfo("Ending session. Goodbye!");
            return SlashCommandResult.Exit;
        }

        if (input.FirstToken == "/status")
        {
            _handlers.ShowStatus(true);
            return SlashCommandResult.HandledCommand;
        }

        if (input.FirstToken == "/stats")
        {
            _handlers.ShowStats();
            return SlashCommandResult.HandledCommand;
        }

        if (input.FirstToken == "/help")
        {
            _handlers.ShowHelp();
            return SlashCommandResult.HandledCommand;
        }

        if (input.FirstToken == "/history")
        {
            _handlers.ShowHistory();
            return SlashCommandResult.HandledCommand;
        }

        if (input.FirstToken == "/capabilities")
        {
            _handlers.ShowStatus(false);
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(input.Lower, "/mode"))
        {
            HandleModeCommand(input.Trimmed);
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(input.Lower, "/theme"))
        {
            HandleThemeCommand(input.Trimmed);
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(input.Lower, "/save"))
        {
            HandleSaveCommand(input.Trimmed);
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(input.Lower, "/transcript"))
        {
            HandleTranscriptCommand(input.Trimmed);
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(input.Lower, "/copy"))
        {
            HandleCopyCommand();
            return SlashCommandResult.HandledCommand;
        }

        return SlashCommandResult.NotHandled;
    }

    internal async Task<SlashCommandResult> DispatchAsync(string input)
    {
        var parsed = SlashCommandInput.Parse(input);
        if (parsed is null)
        {
            return SlashCommandResult.NotHandled;
        }

        var result = Dispatch(parsed);
        if (result.Handled)
        {
            return result;
        }

        if (IsInvocation(parsed.Lower, "/byok"))
        {
            await HandleByokCommandAsync(parsed.Trimmed);
            return SlashCommandResult.HandledCommand;
        }

        if (parsed.FirstToken == "/model")
        {
            await HandleModelCommandAsync();
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(parsed.Lower, "/reasoning"))
        {
            await HandleReasoningCommandAsync(parsed.Trimmed);
            return SlashCommandResult.HandledCommand;
        }

        if (parsed.FirstToken == "/clear")
        {
            await HandleClearCommandAsync();
            return SlashCommandResult.HandledCommand;
        }

        if (parsed.FirstToken == "/settings")
        {
            await HandleSettingsCommandAsync();
            return SlashCommandResult.HandledCommand;
        }

        if (parsed.FirstToken == "/login")
        {
            await HandleLoginCommandAsync();
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(parsed.Lower, "/mcp-role"))
        {
            await HandleMcpRoleCommandAsync(parsed.Trimmed);
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(parsed.Lower, "/report"))
        {
            HandleReportCommand();
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(parsed.Lower, "/mcp-approvals"))
        {
            HandleMcpApprovalsCommand(parsed.Trimmed);
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(parsed.Lower, "/jea"))
        {
            await HandleJeaCommandAsync(parsed.Trimmed);
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(parsed.Lower, "/server"))
        {
            await HandleServerCommandAsync(parsed.Trimmed);
            return SlashCommandResult.HandledCommand;
        }

        return SlashCommandResult.NotHandled;
    }

    private async Task HandleClearCommandAsync()
    {
        var resetSucceeded = await _handlers.ResetConversation();
        if (resetSucceeded)
        {
            _handlers.ClearConsole();
            _handlers.ShowBanner();
            _handlers.ShowStatus(false);
            _handlers.ShowSuccess($"Started new session: {_handlers.GetSessionId()}");
            _handlers.ShowWelcomeMessage(_handlers.GetWelcomeHint());
        }
        else
        {
            _handlers.ShowWarning("Could not start a new session.");
        }
    }

    private async Task HandleSettingsCommandAsync()
    {
        _handlers.EnsureSettingsFile();

        var settingsPath = _handlers.GetSettingsPath();
        _handlers.ShowInfo($"Settings file: {settingsPath}");

        var editorError = await _handlers.OpenSettingsEditor(settingsPath);
        if (!string.IsNullOrWhiteSpace(editorError))
        {
            _handlers.ShowWarning(editorError);
        }

        _handlers.ReloadSettings();
        _handlers.SetTheme(AppSettingsStore.NormalizeTheme(_handlers.GetPersistedTheme()));
        _handlers.InvalidateModelCache();

        var (sessionReloadSucceeded, sessionReloadError) = await _handlers.RecreateCurrentCopilotSession();
        if (sessionReloadSucceeded)
        {
            _handlers.ShowSuccess("Settings reloaded. Safe command patterns and system prompt settings have been applied.");
        }
        else
        {
            var message = "Settings were reloaded, but the AI session could not be recreated. Use /login or /model to reconnect.";
            if (!string.IsNullOrWhiteSpace(sessionReloadError))
            {
                message += $" {sessionReloadError}";
            }

            _handlers.ShowWarning(message);
        }
    }

    private async Task HandleLoginCommandAsync()
    {
        _handlers.InvalidateModelCache();
        var loginSucceeded = await _handlers.RunWithSpinnerAsync("Running Copilot login...", async updateStatus =>
        {
            return await _handlers.LoginAndCreateGitHubSession(updateStatus);
        });

        if (loginSucceeded)
        {
            _handlers.ShowSuccess("GitHub Copilot login completed and session is ready.");
        }
    }

    private async Task HandleMcpRoleCommandAsync(string input)
    {
        var availableServers = _handlers.GetAvailableMcpRoleServerNames();
        var monitoring = _handlers.GetConfiguredMonitoringMcpServer();
        var ticketing = _handlers.GetConfiguredTicketingMcpServer();
        var parts = input.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 1)
        {
            if (availableServers.Count == 0 && string.IsNullOrWhiteSpace(monitoring) && string.IsNullOrWhiteSpace(ticketing))
            {
                _handlers.ShowWarning("No MCP servers are configured. Add servers in your MCP config first, then use /mcp-role.");
                return;
            }

            (monitoring, ticketing) = _handlers.PromptMcpRoleSelection(monitoring, ticketing, availableServers);
        }
        else
        {
            if (!TryApplyDirectMcpRoleCommand(parts, availableServers, ref monitoring, ref ticketing, out var usageError))
            {
                _handlers.ShowWarning(usageError);
                _handlers.ShowInfo("Usage:");
                _handlers.ShowInfo("  /mcp-role");
                _handlers.ShowInfo("  /mcp-role monitoring <server|none>");
                _handlers.ShowInfo("  /mcp-role ticketing <server|none>");
                _handlers.ShowInfo("  /mcp-role clear <monitoring|ticketing|all>");
                return;
            }
        }

        if (McpRoleValuesEqual(monitoring, _handlers.GetConfiguredMonitoringMcpServer())
            && McpRoleValuesEqual(ticketing, _handlers.GetConfiguredTicketingMcpServer()))
        {
            _handlers.ShowInfo($"MCP roles unchanged. Monitoring: {monitoring ?? "none"} | Ticketing: {ticketing ?? "none"}");
            _handlers.ShowStatus(false);
            return;
        }

        _handlers.SaveMcpRoleSettings(monitoring, ticketing);
        _handlers.ReloadSettings();
        var (sessionReloadSucceeded, sessionReloadError) = await _handlers.RecreateCurrentCopilotSession();

        if (sessionReloadSucceeded)
        {
            _handlers.ShowSuccess($"MCP roles saved. Monitoring: {monitoring ?? "none"} | Ticketing: {ticketing ?? "none"}");
            _handlers.ShowStatus(false);
        }
        else
        {
            var message = "MCP roles were saved, but the AI session could not be recreated. Use /login or /model to reconnect.";
            if (!string.IsNullOrWhiteSpace(sessionReloadError))
            {
                message += $" {sessionReloadError}";
            }

            _handlers.ShowWarning(message);
        }
    }

    private static bool TryApplyDirectMcpRoleCommand(
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

    internal static string GetFirstToken(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var separatorIndex = input.IndexOf(' ');
        return separatorIndex >= 0 ? input[..separatorIndex] : input;
    }

    internal static bool IsInvocation(string input, string command)
    {
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        return input.Equals(command, StringComparison.Ordinal)
            || input.StartsWith(command + " ", StringComparison.Ordinal);
    }

    internal static bool IsBareExitCommand(string input)
        => input.Equals("exit", StringComparison.Ordinal)
           || input.Equals("quit", StringComparison.Ordinal);

    private async Task HandleByokCommandAsync(string input)
    {
        var byokParts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var openAiApiKeyEnvironmentVariable = _handlers.GetOpenAiApiKeyEnvironmentVariable();

        if (byokParts.Length > 1 &&
            (byokParts[1].Equals("clear", StringComparison.OrdinalIgnoreCase)
             || byokParts[1].Equals("off", StringComparison.OrdinalIgnoreCase)
             || byokParts[1].Equals("disable", StringComparison.OrdinalIgnoreCase)))
        {
            _handlers.SaveByokSettings(false, null, null);
            _handlers.ClearByokRuntimeState();
            _handlers.InvalidateModelCache();
            _handlers.ShowSuccess("Saved BYOK settings cleared for this profile.");
            _handlers.ShowInfo("Current session provider remains unchanged until you switch model/provider or restart.");
            _handlers.ShowInfo($"The {openAiApiKeyEnvironmentVariable} environment variable (if set) is unchanged.");
            return;
        }

        string? apiKey = null;
        var byokBaseUrl = _handlers.GetByokBaseUrl();
        var byokModel = _handlers.GetDefaultByokModel();

        if (byokParts.Length == 1)
        {
            _handlers.ShowInfo($"Enter OpenAI-compatible base URL (default: {byokBaseUrl})");
            var baseUrlInput = _handlers.PromptText().Trim();
            if (!string.IsNullOrWhiteSpace(baseUrlInput))
            {
                byokBaseUrl = baseUrlInput;
            }

            _handlers.ShowInfo($"Enter API key, or type 'env' to use {openAiApiKeyEnvironmentVariable}.");
            var apiKeyInput = _handlers.PromptText().Trim();
            apiKey = apiKeyInput.Equals("env", StringComparison.OrdinalIgnoreCase)
                ? _handlers.GetEnvironmentVariable(openAiApiKeyEnvironmentVariable)
                : apiKeyInput;
        }
        else
        {
            var sourceArg = byokParts[1];

            if (sourceArg.Equals("env", StringComparison.OrdinalIgnoreCase))
            {
                apiKey = _handlers.GetEnvironmentVariable(openAiApiKeyEnvironmentVariable);
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
            _handlers.ShowWarning($"No API key was provided. Set {openAiApiKeyEnvironmentVariable} or pass it as /byok <api-key> [base-url] [model].");
            _handlers.ShowInfo("Examples:");
            _handlers.ShowInfo("  /byok env https://api.openai.com/v1");
            _handlers.ShowInfo("  /byok sk-... https://aigw.example.org");
            return;
        }

        if (!LooksLikeUrl(byokBaseUrl))
        {
            _handlers.ShowWarning("Base URL is invalid. Example: https://api.openai.com/v1");
            return;
        }

        var byokReady = await _handlers.ConfigureByokOpenAi(byokBaseUrl!, apiKey, byokModel, null);

        if (byokReady)
        {
            _handlers.InvalidateModelCache();
            _handlers.ShowModelSelectionSummary();
        }
    }

    private async Task HandleModelCommandAsync()
    {
        if (_handlers.HasCopilotClient())
        {
            try
            {
                await _handlers.RefreshAvailableModels();

                if (_handlers.GetAvailableModelCount() == 0)
                {
                    _handlers.ShowWarning("No models available. Authenticate with /login and/or configure BYOK with /byok.");
                }
            }
            catch (Exception ex)
            {
                if (_handlers.IsDebugMode())
                {
                    _handlers.ShowWarning($"Could not refresh model list: {TrimSingleLine(ex.Message)}");
                }
            }
        }

        if (_handlers.GetAvailableModelCount() == 0)
        {
            _handlers.ShowWarning("No models are available yet. Authenticate GitHub Copilot or configure BYOK, then try /model again.");
            return;
        }

        var selectionEntries = _handlers.GetModelSelectionEntries();
        if (selectionEntries.Count == 0)
        {
            _handlers.ShowWarning("No connected provider models are available. Authenticate GitHub Copilot or configure BYOK first.");
            return;
        }

        var currentModel = _handlers.GetSelectedModelName() ?? string.Empty;
        var selectedEntry = _handlers.PromptModelSelection(currentModel, selectionEntries);
        if (selectedEntry == null)
        {
            _handlers.ShowInfo($"Keeping current model: {currentModel}");
            return;
        }

        if (_handlers.IsCurrentModelAndSource(selectedEntry))
        {
            return;
        }

        var switchBehavior = ModelSwitchBehavior.CleanSession;
        var priorConversation = Array.Empty<ReportPromptEntry>();

        if (_handlers.HasRecordedHistory())
        {
            var behaviorChoice = _handlers.PromptModelSwitchBehavior(currentModel, selectedEntry.DisplayName);
            if (!behaviorChoice.HasValue)
            {
                _handlers.ShowInfo($"Keeping current model: {currentModel}");
                return;
            }

            switchBehavior = behaviorChoice.Value;
            if (switchBehavior == ModelSwitchBehavior.SecondOpinion)
            {
                priorConversation = _handlers.GetRecordedPrompts().ToArray();
            }
        }

        var displayName = selectedEntry.DisplayName;
        var success = await _handlers.RunWithSpinnerAsync($"Switching to {displayName}...", async updateStatus =>
        {
            return await _handlers.ChangeModel(selectedEntry, updateStatus);
        });

        if (!success)
        {
            return;
        }

        if (switchBehavior == ModelSwitchBehavior.CleanSession)
        {
            _handlers.ClearRecordedHistory();
        }

        _handlers.ShowModelSelectionSummary();

        if (switchBehavior == ModelSwitchBehavior.SecondOpinion && priorConversation.Length > 0)
        {
            var selectedModel = _handlers.GetSelectedModelName() ?? string.Empty;
            _handlers.ShowInfo($"Asking {selectedModel} for a second opinion using the current session context...");
            await _handlers.RunSecondOpinion(currentModel, selectedModel, priorConversation);
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

    private async Task HandleReasoningCommandAsync(string input)
    {
        var currentModel = _handlers.GetSelectedModelInfo();
        if (currentModel == null)
        {
            _handlers.ShowWarning("No active model is selected yet. Use /model first.");
            return;
        }

        if (!ReasoningEffortHelper.SupportsReasoningEffort(currentModel))
        {
            var modelName = _handlers.GetSelectedModelName();
            if (string.IsNullOrWhiteSpace(modelName))
            {
                modelName = string.IsNullOrWhiteSpace(currentModel.Id) ? currentModel.Name : currentModel.Id;
            }

            _handlers.ShowInfo($"The current model '{modelName}' does not expose reasoning-effort controls.");
            return;
        }

        var supportedEfforts = ReasoningEffortHelper.GetSupportedReasoningEfforts(currentModel);
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string? requestedReasoningEffort;

        if (parts.Length < 2)
        {
            requestedReasoningEffort = _handlers.PromptReasoningEffort(
                _handlers.GetConfiguredReasoningEffort(),
                supportedEfforts,
                ReasoningEffortHelper.GetDefaultReasoningEffort(currentModel));
        }
        else
        {
            requestedReasoningEffort = ReasoningEffortHelper.Normalize(parts[1]);
            if (!string.IsNullOrWhiteSpace(requestedReasoningEffort)
                && supportedEfforts.Count > 0
                && !supportedEfforts.Contains(requestedReasoningEffort, StringComparer.OrdinalIgnoreCase))
            {
                _handlers.ShowWarning($"Unsupported reasoning effort '{parts[1].Trim()}'. Supported values: {string.Join(", ", supportedEfforts)} or auto.");
                return;
            }
        }

        var previousReasoningEffort = _handlers.GetConfiguredReasoningEffort();
        var normalizedReasoningEffort = ReasoningEffortHelper.Normalize(requestedReasoningEffort);
        if (string.Equals(previousReasoningEffort, normalizedReasoningEffort, StringComparison.OrdinalIgnoreCase))
        {
            _handlers.ShowInfo($"Reasoning remains: {_handlers.GetReasoningDisplay(currentModel)}");
            return;
        }

        _handlers.ApplyReasoningEffortSetting(normalizedReasoningEffort);
        _handlers.SaveReasoningEffortState(normalizedReasoningEffort);

        if (_handlers.HasActiveCopilotSession())
        {
            var selectedModelId = _handlers.GetSelectedModelId();
            var targetModel = string.IsNullOrWhiteSpace(selectedModelId) ? currentModel.Id : selectedModelId;
            var spinnerLabel = string.IsNullOrWhiteSpace(normalizedReasoningEffort)
                ? "Restoring automatic reasoning..."
                : $"Applying reasoning {normalizedReasoningEffort}...";

            var success = await _handlers.RunWithSpinnerAsync(spinnerLabel, async updateStatus =>
            {
                updateStatus("Restarting AI session...");
                return await _handlers.RecreateCopilotSession(targetModel, updateStatus);
            });

            if (success)
            {
                _handlers.ShowModelSelectionSummary();
            }
            else
            {
                _handlers.ApplyReasoningEffortSetting(previousReasoningEffort);
                _handlers.SaveReasoningEffortState(previousReasoningEffort);
            }
        }
        else
        {
            _handlers.ShowSuccess($"Reasoning preference saved: {_handlers.GetReasoningDisplay(currentModel) ?? "auto"}");
        }
    }

    private void HandleReportCommand()
    {
        var prompts = _handlers.GetRecordedPrompts();

        if (prompts.Count == 0)
        {
            _handlers.ShowInfo("No prompts recorded yet. Ask a question first, then run /report.");
            return;
        }

        var reportPath = _handlers.CreateReportPath();
        var summary = _handlers.GetReportSessionSummary();
        var html = ReportHtmlBuilder.BuildReportHtml(prompts, summary, contentAlreadyRedacted: true);
        _handlers.WriteReportHtml(reportPath, html);

        try
        {
            _handlers.OpenReport(reportPath);
            _handlers.ShowSuccess($"Report generated and opened: {reportPath}");
            _handlers.ShowInfo($"Reports are stored in temp: {Path.GetDirectoryName(reportPath) ?? Path.GetTempPath()}");
        }
        catch (Exception ex)
        {
            _handlers.ShowWarning($"Report generated at {reportPath}, but could not auto-open browser: {TrimSingleLine(ex.Message)}");
        }
    }

    private void HandleMcpApprovalsCommand(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // /mcp-approvals  -> list
        if (parts.Length <= 1 || string.Equals(parts[1], "list", StringComparison.OrdinalIgnoreCase))
        {
            var sessionApprovals = _handlers.GetApprovedMcpServers()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var persisted = _handlers.GetPersistedApprovedMcpServers()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var persistedSet = new HashSet<string>(persisted, StringComparer.OrdinalIgnoreCase);

            if (sessionApprovals.Count == 0 && persisted.Count == 0)
            {
                _handlers.ShowInfo("No MCP approvals are active for this session.");
                _handlers.ShowInfo("MCP servers you approve via the prompt appear here automatically.");
                return;
            }

            _handlers.ShowInfo($"Active MCP approvals ({sessionApprovals.Count}):");
            foreach (var name in sessionApprovals)
            {
                var role = _handlers.GetMcpServerRole(name);
                var persistedFlag = persistedSet.Contains(name)
                    ? " [persisted]"
                    : string.Empty;
                var roleFlag = string.IsNullOrWhiteSpace(role) ? string.Empty : $" [{role}]";
                _handlers.ShowInfo($"  {name}{roleFlag}{persistedFlag}");
            }

            if (persisted.Count > 0)
            {
                var sessionSet = new HashSet<string>(sessionApprovals, StringComparer.OrdinalIgnoreCase);
                var orphaned = persisted
                    .Where(name => !sessionSet.Contains(name))
                    .ToList();
                if (orphaned.Count > 0)
                {
                    _handlers.ShowInfo("Persisted but not currently active:");
                    foreach (var name in orphaned)
                    {
                        _handlers.ShowInfo($"  {name}");
                    }
                }
            }

            _handlers.ShowInfo("Use /mcp-approvals clear all  or  /mcp-approvals clear <server> to remove persisted approvals.");
            return;
        }

        if (string.Equals(parts[1], "clear", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 3)
            {
                _handlers.ShowWarning("Use /mcp-approvals clear all  or  /mcp-approvals clear <server>.");
                return;
            }

            if (string.Equals(parts[2], "all", StringComparison.OrdinalIgnoreCase))
            {
                var removed = _handlers.ClearPersistedMcpApprovals();
                _handlers.ClearSessionMcpApprovals();
                _handlers.ShowSuccess(removed > 0
                    ? $"Cleared {removed} persisted MCP approval{(removed == 1 ? string.Empty : "s")} and reset session approvals."
                    : "Cleared session MCP approvals (no persisted approvals were stored).");
                return;
            }

            var target = string.Join(' ', parts.Skip(2)).Trim();
            var persistedRemoved = _handlers.RemovePersistedMcpApproval(target);
            var sessionRemoved = _handlers.RemoveSessionMcpApproval(target);

            if (persistedRemoved || sessionRemoved)
            {
                _handlers.ShowSuccess($"Removed MCP approval for '{target}'.");
            }
            else
            {
                _handlers.ShowWarning($"No active MCP approval found for '{target}'.");
            }
            return;
        }

        _handlers.ShowWarning("Use /mcp-approvals [list|clear all|clear <server>].");
    }

    private async Task HandleServerCommandAsync(string input)
    {
        var parts = input.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            _handlers.ShowWarning("Usage: /server <server1>[,server2,...]");
            return;
        }

        var primaryServer = parts[1];
        var additionalServers = parts.Skip(2).ToList();

        var success = await _handlers.RunWithSpinnerAsync($"Connecting to {primaryServer}...", async updateStatus =>
        {
            return await _handlers.ReconnectServer(primaryServer, updateStatus);
        });

        if (!success)
        {
            return;
        }

        _handlers.ShowSuccess($"Connected to {primaryServer}");

        foreach (var server in additionalServers)
        {
            if (_handlers.GetExecutionMode() == ExecutionMode.Safe)
            {
                var approved = _handlers.PromptCommandApproval(
                    $"New-PSSession -ComputerName '{server}'",
                    $"TroubleScout wants to establish a direct PowerShell session to {server}");
                if (!approved)
                {
                    _handlers.ShowWarning($"Connection to {server} was denied.");
                    continue;
                }
            }

            await _handlers.RunWithSpinnerAsync($"Connecting to {server}...", async _ =>
            {
                var (connected, error) = await _handlers.ConnectAdditionalServer(server, true);
                if (!connected)
                {
                    _handlers.ShowWarning($"Could not connect to {server}: {error}");
                }

                return connected;
            });
        }

        _handlers.RefreshServerContext();

        if (additionalServers.Count > 0)
        {
            var (recreated, recreateError) = await _handlers.RecreateCurrentCopilotSession();
            if (!recreated)
            {
                var message = "Connected servers, but the AI session could not be recreated. Use /login or /model to reconnect.";
                if (!string.IsNullOrWhiteSpace(recreateError))
                {
                    message += $" {recreateError}";
                }

                _handlers.ShowWarning(message);
            }
        }

        _handlers.ShowStatus(false);
    }

    private async Task HandleJeaCommandAsync(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var serverName = parts.Length > 1 ? parts[1] : null;
        var configurationName = parts.Length > 2 ? string.Join(' ', parts.Skip(2)) : null;

        if (string.IsNullOrWhiteSpace(serverName))
        {
            _handlers.ShowInfo("Enter the server name for the JEA session:");
            serverName = _handlers.PromptText().Trim();
            if (string.IsNullOrWhiteSpace(serverName))
            {
                _handlers.ShowWarning("Server name cannot be empty.");
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(configurationName))
        {
            _handlers.ShowInfo("Enter the JEA configuration name:");
            configurationName = _handlers.PromptText().Trim();
            if (string.IsNullOrWhiteSpace(configurationName))
            {
                _handlers.ShowWarning("Configuration name cannot be empty.");
                return;
            }
        }

        if (parts.Length < 3)
        {
            _handlers.ShowInfo("Example: /jea server1 JEA-Admins");
        }

        var connected = await _handlers.RunWithSpinnerAsync(
            $"Connecting to JEA endpoint {configurationName} on {serverName}...",
            async _ =>
            {
                var (success, error) = await _handlers.ConnectJeaServer(serverName, configurationName, true);
                if (!success)
                {
                    _handlers.ShowWarning(error ?? $"Could not connect to JEA endpoint {configurationName} on {serverName}.");
                }

                return success;
            });

        if (!connected)
        {
            return;
        }

        var allowedCommands = _handlers.GetJeaAllowedCommands(serverName);
        if (allowedCommands.Count > 0)
        {
            _handlers.ShowSuccess($"Connected to JEA endpoint '{configurationName}' on {serverName}");
            _handlers.ShowJeaDiscoveredCommands(serverName, configurationName, allowedCommands);
        }

        _handlers.RefreshServerContext();

        var (recreated, recreateError) = await _handlers.RecreateCurrentCopilotSession();
        if (!recreated)
        {
            var message = "Connected JEA endpoint, but the AI session could not be recreated. Use /login or /model to reconnect.";
            if (!string.IsNullOrWhiteSpace(recreateError))
            {
                message += $" {recreateError}";
            }

            _handlers.ShowWarning(message);
        }

        _handlers.ShowStatus(false);
    }

    internal static string CreateDefaultReportPath()
    {
        var reportsDir = Path.Combine(Path.GetTempPath(), "TroubleScout", "reports");
        Directory.CreateDirectory(reportsDir);

        return Path.Combine(reportsDir, $"troublescout-report-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.html");
    }

    internal static void OpenReportInDefaultBrowser(string reportPath)
    {
        System.Diagnostics.Process.Start(CreateReportOpenStartInfo(reportPath));
    }

    internal static System.Diagnostics.ProcessStartInfo CreateReportOpenStartInfo(string reportPath)
    {
        // Use cmd.exe /c start instead of UseShellExecute to respect the current
        // user context when running as a different user (RunAs). UseShellExecute
        // opens the browser as the primary logged-in user, causing path mismatches.
        return new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c start \"\" \"{reportPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    internal static void WriteReportHtmlFile(string reportPath, string html)
    {
        File.WriteAllText(reportPath, html, Encoding.UTF8);
    }

    private static string TrimSingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Unknown error";
        }

        var trimmed = text.Trim();
        var newlineIndex = trimmed.IndexOfAny(['\r', '\n']);
        return newlineIndex < 0 ? trimmed : trimmed[..newlineIndex].Trim();
    }

    private void HandleModeCommand(string input)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            _handlers.ShowInfo($"Current mode: {_handlers.GetExecutionMode().ToCliValue()}");
            _handlers.ShowInfo("Usage: /mode <safe|yolo>");
            return;
        }

        if (!ExecutionModeParser.TryParse(parts[1], out var requestedMode))
        {
            _handlers.ShowWarning("Invalid mode. Use: safe or yolo.");
            return;
        }

        _handlers.SetExecutionMode(requestedMode);
        _handlers.SetConsoleExecutionMode(requestedMode);
        _handlers.ShowSuccess($"Execution mode set to: {requestedMode.ToCliValue()}");
        _handlers.ShowStatus(false);
    }

    private void HandleThemeCommand(string input)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            _handlers.ShowInfo($"Current theme: {_handlers.GetTheme()}");
            _handlers.ShowInfo("Usage: /theme <dark|mono>. Theme applies to app chrome (panels, status bar) only; it does not retint Markdown responses, reasoning, or the spinner.");
            return;
        }

        var requested = parts[1].Trim().ToLowerInvariant();
        var normalized = AppSettingsStore.NormalizeTheme(requested);
        if (!string.Equals(requested, normalized, StringComparison.Ordinal))
        {
            _handlers.ShowWarning($"Unknown theme '{parts[1]}'. Falling back to '{normalized}'. Supported: dark, mono.");
        }

        _handlers.SetTheme(normalized);
        _handlers.PersistTheme(normalized);
        _handlers.ShowSuccess($"Theme set to '{normalized}'.");
    }

    private void HandleSaveCommand(string input)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            _handlers.ShowInfo("Usage: /save <path>  — writes the last assistant message (Markdown) to disk.");
            return;
        }

        var targetPath = parts[1].Trim().Trim('"');
        var content = _handlers.GetLastAssistantMessage();
        var result = MessagePersistence.Save(targetPath, content, allowOverwrite: false, out var detail);

        if (result == SaveMessageResult.FileAlreadyExists)
        {
            if (!_handlers.ConfirmOverwrite(targetPath))
            {
                _handlers.ShowInfo("Save cancelled.");
                return;
            }

            result = MessagePersistence.Save(targetPath, content, allowOverwrite: true, out detail);
        }

        ShowSaveResult(result, targetPath, detail);
    }

    private void HandleTranscriptCommand(string input)
    {
        var parts = input.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            ShowTranscriptUsage();
            return;
        }

        var verb = parts[1].Trim().ToLowerInvariant();
        if (verb is not ("save" or "load"))
        {
            _handlers.ShowWarning("Invalid transcript action. Use: save or load.");
            ShowTranscriptUsage();
            return;
        }

        if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[2]))
        {
            ShowTranscriptUsage();
            return;
        }

        var targetPath = parts[2].Trim().Trim('"');
        if (verb == "save")
        {
            HandleTranscriptSave(targetPath);
        }
        else
        {
            HandleTranscriptLoad(targetPath);
        }
    }

    private void ShowTranscriptUsage()
    {
        _handlers.ShowInfo("Usage: /transcript save <path>  — writes the current redacted session transcript to disk.");
        _handlers.ShowInfo("Usage: /transcript load <path>  — loads a redacted transcript into the current session history.");
    }

    private void HandleTranscriptSave(string targetPath)
    {
        var result = SessionTranscriptService.Save(
            targetPath,
            _handlers.GetRecordedPrompts(),
            _handlers.GetReportSessionSummary(),
            allowOverwrite: false,
            out var detail);

        if (result == SessionTranscriptSaveResult.FileAlreadyExists)
        {
            if (!_handlers.ConfirmOverwrite(targetPath))
            {
                _handlers.ShowInfo("Transcript save cancelled.");
                return;
            }

            result = SessionTranscriptService.Save(
                targetPath,
                _handlers.GetRecordedPrompts(),
                _handlers.GetReportSessionSummary(),
                allowOverwrite: true,
                out detail);
        }

        ShowTranscriptSaveResult(result, targetPath, detail);
    }

    private void HandleTranscriptLoad(string targetPath)
    {
        var result = SessionTranscriptService.Load(targetPath, out var transcript, out var detail);
        if (result != SessionTranscriptLoadResult.Success)
        {
            ShowTranscriptLoadResult(result, targetPath, detail, promptCount: 0);
            return;
        }

        if (_handlers.HasRecordedHistory() && !_handlers.ConfirmTranscriptLoadReplace())
        {
            _handlers.ShowInfo("Transcript load cancelled.");
            return;
        }

        _handlers.ReplaceRecordedPrompts(transcript!.Prompts);
        ShowTranscriptLoadResult(result, targetPath, detail, transcript.Prompts.Count);
    }

    private void ShowTranscriptSaveResult(SessionTranscriptSaveResult result, string targetPath, string? detail)
    {
        switch (result)
        {
            case SessionTranscriptSaveResult.Success:
                _handlers.ShowSuccess($"Saved redacted transcript to '{targetPath}'.");
                break;
            case SessionTranscriptSaveResult.NoHistory:
                _handlers.ShowWarning("No session history captured yet — ask something first.");
                break;
            case SessionTranscriptSaveResult.PathMissing:
                ShowTranscriptUsage();
                break;
            case SessionTranscriptSaveResult.PathIsDirectory:
                _handlers.ShowWarning($"'{targetPath}' is a directory. Provide a file path.");
                break;
            case SessionTranscriptSaveResult.ParentDirectoryMissing:
                _handlers.ShowWarning($"Parent directory does not exist: {detail}. Create it first; /transcript will not.");
                break;
            case SessionTranscriptSaveResult.FileAlreadyExists:
                _handlers.ShowWarning($"'{targetPath}' already exists. Transcript was not overwritten.");
                break;
            case SessionTranscriptSaveResult.WriteFailed:
                _handlers.ShowError("Transcript save failed", detail ?? "unknown error");
                break;
        }
    }

    private void ShowTranscriptLoadResult(SessionTranscriptLoadResult result, string targetPath, string? detail, int promptCount)
    {
        switch (result)
        {
            case SessionTranscriptLoadResult.Success:
                _handlers.ShowSuccess($"Loaded {promptCount} transcript prompt(s) from '{targetPath}'.");
                _handlers.ShowInfo("Loaded history is now available to /report and /model second-opinion context.");
                break;
            case SessionTranscriptLoadResult.PathMissing:
                ShowTranscriptUsage();
                break;
            case SessionTranscriptLoadResult.FileNotFound:
                _handlers.ShowWarning($"Transcript file not found: {targetPath}");
                break;
            case SessionTranscriptLoadResult.PathIsDirectory:
                _handlers.ShowWarning($"'{targetPath}' is a directory. Provide a transcript file path.");
                break;
            case SessionTranscriptLoadResult.MalformedJson:
                _handlers.ShowError("Transcript load failed", detail ?? "The file is not valid transcript JSON.");
                break;
            case SessionTranscriptLoadResult.UnsupportedSchemaVersion:
                _handlers.ShowError("Transcript load failed", detail ?? "Unsupported transcript schema version.");
                break;
            case SessionTranscriptLoadResult.EmptyTranscript:
                _handlers.ShowWarning("Transcript does not contain any prompt entries.");
                break;
            case SessionTranscriptLoadResult.ReadFailed:
                _handlers.ShowError("Transcript load failed", detail ?? "unknown error");
                break;
        }
    }

    private void ShowSaveResult(SaveMessageResult result, string targetPath, string? detail)
    {
        switch (result)
        {
            case SaveMessageResult.Success:
                _handlers.ShowSuccess($"Saved last response to '{targetPath}'.");
                break;
            case SaveMessageResult.NoMessageAvailable:
                _handlers.ShowWarning("No assistant message captured yet — ask something first.");
                break;
            case SaveMessageResult.PathMissing:
                _handlers.ShowWarning("Usage: /save <path>");
                break;
            case SaveMessageResult.PathIsDirectory:
                _handlers.ShowWarning($"'{targetPath}' is a directory. Provide a file path.");
                break;
            case SaveMessageResult.ParentDirectoryMissing:
                _handlers.ShowWarning($"Parent directory does not exist: {detail}. Create it first; /save will not.");
                break;
            case SaveMessageResult.WriteFailed:
                _handlers.ShowError("Save failed", detail ?? "unknown error");
                break;
        }
    }

    private void HandleCopyCommand()
    {
        var content = _handlers.GetLastAssistantMessage();
        if (string.IsNullOrEmpty(content))
        {
            _handlers.ShowWarning("No assistant message captured yet — ask something first.");
            return;
        }

        var copied = MessagePersistence.Copy(content, out var detail);
        if (copied)
        {
            _handlers.ShowSuccess("Last response copied to clipboard.");
        }
        else
        {
            _handlers.ShowError("Copy failed", string.IsNullOrEmpty(detail) ? "Clipboard not available." : detail);
        }
    }
}
