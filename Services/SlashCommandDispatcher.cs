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
    internal Func<string, bool> ConfirmOverwrite { get; init; } = static _ => false;
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
