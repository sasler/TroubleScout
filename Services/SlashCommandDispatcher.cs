namespace TroubleScout.Services;

internal sealed record SlashCommandResult(bool Handled, bool ExitRequested)
{
    internal static SlashCommandResult NotHandled { get; } = new(false, false);
    internal static SlashCommandResult HandledCommand { get; } = new(true, false);
    internal static SlashCommandResult Exit { get; } = new(true, true);
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
    internal Func<string?> GetLastAssistantMessage { get; init; } = static () => null;
    internal Func<List<ReportPromptEntry>> GetRecordedPrompts { get; init; } = static () => [];
    internal Func<ReportSessionSummary?> GetReportSessionSummary { get; init; } = static () => null;
    internal Action<IReadOnlyList<ReportPromptEntry>> ReplaceRecordedPrompts { get; init; } = static _ => { };
    internal Func<bool> HasRecordedHistory { get; init; } = static () => false;
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
        if (string.IsNullOrWhiteSpace(input))
        {
            return SlashCommandResult.NotHandled;
        }

        var trimmedInput = input.Trim();
        var lowerInput = trimmedInput.ToLowerInvariant();
        var firstToken = GetFirstToken(lowerInput);

        if (firstToken is "/exit" or "/quit" || IsBareExitCommand(lowerInput))
        {
            _handlers.ShowInfo("Ending session. Goodbye!");
            return SlashCommandResult.Exit;
        }

        if (firstToken == "/status")
        {
            _handlers.ShowStatus(true);
            return SlashCommandResult.HandledCommand;
        }

        if (firstToken == "/stats")
        {
            _handlers.ShowStats();
            return SlashCommandResult.HandledCommand;
        }

        if (firstToken == "/help")
        {
            _handlers.ShowHelp();
            return SlashCommandResult.HandledCommand;
        }

        if (firstToken == "/history")
        {
            _handlers.ShowHistory();
            return SlashCommandResult.HandledCommand;
        }

        if (firstToken == "/capabilities")
        {
            _handlers.ShowStatus(false);
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(lowerInput, "/mode"))
        {
            HandleModeCommand(trimmedInput);
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(lowerInput, "/theme"))
        {
            HandleThemeCommand(trimmedInput);
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(lowerInput, "/save"))
        {
            HandleSaveCommand(trimmedInput);
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(lowerInput, "/transcript"))
        {
            HandleTranscriptCommand(trimmedInput);
            return SlashCommandResult.HandledCommand;
        }

        if (IsInvocation(lowerInput, "/copy"))
        {
            HandleCopyCommand();
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
