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
    internal Func<string?> GetConfiguredReasoningEffort { get; init; } = static () => null;
    internal Action<string?> ApplyReasoningEffortSetting { get; init; } = static _ => { };
    internal Action<string?> SaveReasoningEffortState { get; init; } = static _ => { };
    internal Func<ModelInfo, string?> GetReasoningDisplay { get; init; } = static _ => null;
    internal Func<string?, IReadOnlyList<string>, string?, string?> PromptReasoningEffort { get; init; } = static (current, _, _) => current;
    internal Func<bool> HasActiveCopilotSession { get; init; } = static () => false;
    internal Func<string, Func<Action<string>, Task<bool>>, Task<bool>> RunWithSpinnerAsync { get; init; } = static async (_, action) => await action(static _ => { });
    internal Func<string, Action<string>?, Task<bool>> RecreateCopilotSession { get; init; } = static (_, _) => Task.FromResult(false);
    internal Action ShowModelSelectionSummary { get; init; } = static () => { };
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

        if (IsInvocation(parsed.Lower, "/reasoning"))
        {
            await HandleReasoningCommandAsync(parsed.Trimmed);
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

        return SlashCommandResult.NotHandled;
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
