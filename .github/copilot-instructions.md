# TroubleScout - Copilot Instructions

## Architecture Overview

TroubleScout is a .NET 10 CLI tool that uses the **GitHub Copilot SDK** to provide AI-powered Windows Server troubleshooting. The architecture:

```
Program.cs (CLI entry) → TroubleshootingSession (Copilot integration)
                                    ↓
    ┌───────────────────────────────┼───────────────────────────────┐
    ↓                               ↓                               ↓
ConsoleUI.cs              DiagnosticTools.cs              PowerShellExecutor.cs
(Spectre.Console TUI)     (AI tool functions)             (Local/WinRM execution)
```

## Key Dependencies

- **GitHub.Copilot.SDK** (0.1.15-preview): Uses event-based streaming via `CopilotSession.On()` - NOT async iterators
- **Microsoft.PowerShell.SDK** (7.5.4): Embedded PowerShell runspace for command execution
- **Spectre.Console** (0.54.0): Rich TUI components

## Critical Patterns

### Copilot SDK Usage (TroubleshootingSession.cs)

The SDK uses **event subscriptions** for streaming responses:

```csharp
// CORRECT: Subscribe to events
using var subscription = _copilotSession.On(evt =>
{
    switch (evt)
    {
        case AssistantMessageDeltaEvent delta:  // Streaming chunks
            ConsoleUI.WriteAIResponse(delta.Data?.DeltaContent ?? "");
            break;
        case SessionIdleEvent:  // Completion signal
            done.TrySetResult(true);
            break;
    }
});
await _copilotSession.SendAsync(new MessageOptions { Prompt = userMessage });
```

Model info comes from `SessionStartEvent` and `AssistantUsageEvent`, not from the session config.

### AI Tool Functions (DiagnosticTools.cs)

Tools are created via `AIFunctionFactory.Create()` from `Microsoft.Extensions.AI`:

```csharp
yield return AIFunctionFactory.Create(GetEventLogsAsync,
    "get_event_logs",
    "Description of what the tool does");
```

Parameters use `[Description("...")]` attributes for the AI to understand their purpose.

### Command Safety Model

PowerShellExecutor validates commands before execution:
- **Auto-execute**: `Get-*` commands (read-only)
- **Require approval**: `Set-*`, `Start-*`, `Stop-*`, `Restart-*`, `Remove-*`, etc.
- **Blocked**: `Get-Credential`, `Get-Secret`

Multi-line scripts are analyzed to check if ALL statements are safe.

### Console Output (ConsoleUI.cs)

- Use `AnsiConsole.Markup()` for styled output with Spectre markup syntax
- Escape user content with `Markup.Escape()` to prevent injection
- Avoid Unicode symbols that may not render (use ASCII alternatives)
- Use `RunWithSpinnerAsync()` for long-running operations with animated feedback

## Build & Run

```powershell
# Development
dotnet build
dotnet run -- --server localhost

# Self-contained executable (no .NET runtime needed on target)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# Output: bin\Release\net10.0\win-x64\publish\TroubleScout.exe + runtimes/
```

## File Conventions

| Directory | Purpose |
|-----------|---------|
| `Services/` | Infrastructure (PowerShell execution, WinRM) |
| `Tools/` | AI tool functions exposed to Copilot |
| `UI/` | Spectre.Console TUI components |

## Common Tasks

**Adding a new diagnostic tool**: Add method to `DiagnosticTools.cs` with `[Description]` attributes, yield it from `GetTools()`.

**Changing session behavior**: Modify `SystemMessageConfig` in `TroubleshootingSession.cs` - this is the AI's system prompt.

**Adding CLI options**: Update the manual argument parsing in `Program.cs` (uses simple switch/case, not a framework).
