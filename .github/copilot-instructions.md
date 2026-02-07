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

- **GitHub.Copilot.SDK** (0.1.23): Uses event-based streaming via `CopilotSession.On()` - NOT async iterators
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
# NOTE: The runtimes/ folder contains native dependencies (PowerShell SDK) that cannot be embedded.
```

## Creating a Release

**IMPORTANT**: Always create a **single zip file** containing both TroubleScout.exe and the runtimes folder.

```powershell
# 1. Build the release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# 2. Create release package (single zip file)
$version = "v1.0.x"  # Update version number
$publishPath = "bin\Release\net10.0\win-x64\publish"
$tempDir = "TroubleScout-$version"

New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
Copy-Item "$publishPath\TroubleScout.exe" -Destination $tempDir
Copy-Item "$publishPath\runtimes" -Destination $tempDir -Recurse
Compress-Archive -Path $tempDir -DestinationPath "TroubleScout-$version-win-x64.zip" -Force
Remove-Item $tempDir -Recurse -Force

# 3. Commit and tag
git add -A
git commit -m "🔖 Release $version"
git push
git tag -a $version -m "Release $version"
git push origin $version

# 4. Create GitHub release with the single zip file
gh release create $version `
  "TroubleScout-$version-win-x64.zip" `
  --title "$version - <Title>" `
  --notes "<Release notes here>"
```

**DO NOT**:
- Create separate files for TroubleScout.exe and runtimes.zip
- Create version-specific release notes files (e.g., release-v1.0.3.md)
- Use the existing release-notes.md file for release descriptions

**DO**:
- Always package TroubleScout.exe and runtimes/ folder together in a single zip
- Include release notes directly in the `gh release create` command
- Use descriptive filenames like `TroubleScout-v1.0.3-win-x64.zip`
```

## Development Workflow

**ALWAYS build after making code changes**: After editing any `.cs` files, run `dotnet build` to check for compilation errors. Use the `get_errors` tool to verify no issues remain. Fix any build errors before proceeding with additional changes or considering the task complete.

**Git/PR workflow (required)**:
- **ALWAYS check what git branch you are on before starting work** (use `git branch --show-current` or `git status -sb`).
- **Never make changes directly on `main`**. If the current branch is `main`, immediately create and switch to a new branch:
    - Use `feature/<short-desc>` when adding or changing things.
    - Use `fix/<short-desc>` when fixing things.
    - Keep `<short-desc>` lowercase and hyphenated.
- **All git commit subject lines must begin with an emoji**, followed by a space, then the summary (example: `✨ Add WinRM connectivity check`).
- **All pull request titles must begin with an emoji**, followed by a space.
- **All issue titles must begin with an emoji**, followed by a space.
- **After creating commits on a feature/fix branch, push to `origin` without asking if not already pushed/upstreamed**.
    - Never force-push.
    - If push is rejected (permissions, auth, protected branches, missing `origin`, network failure), stop and report the exact error and safe next options.

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
