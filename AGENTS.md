# AGENTS.md

Agent instructions for working in this repository.

## Scope

- Applies to the entire repository.
- Treat this as the primary instruction source for coding agents.

## Project Overview

TroubleScout is a .NET 10 CLI for AI-powered Windows Server troubleshooting using the GitHub Copilot SDK.

Architecture:

```text
Program.cs (CLI entry) -> TroubleshootingSession (Copilot integration)
                                   |
        +--------------------------+--------------------------+
        |                          |                          |
  UI/ConsoleUI.cs         Tools/DiagnosticTools.cs   Services/PowerShellExecutor.cs
```

## Core Dependencies

- GitHub.Copilot.SDK (`0.1.32`) via event-based streaming (`CopilotSession.On(...)`)
- Microsoft.PowerShell.SDK (`7.5.4`) for embedded PowerShell execution
- Spectre.Console (`0.54.0`) for terminal UI

## Coding Rules

- Keep changes focused and minimal.
- Follow existing patterns and naming.
- Do not introduce unrelated refactors.
- Escape user-controlled text in Spectre markup with `Markup.Escape(...)`.
- Prefer ASCII-safe output where possible.

## Copilot SDK Patterns (Required)

### Event handling and completion

- Use `TaskCompletionSource` and wait for `SessionIdleEvent` to determine turn completion.
- Handle streaming with `AssistantMessageDeltaEvent`; keep `AssistantMessageEvent` as fallback/final.
- Handle thinking tokens with `AssistantReasoningEvent` (`.Data.Content`); render via `ConsoleUI.WriteReasoningText()`.
- Dispose event subscriptions when done with each send cycle.

### Session configuration

- Keep `SystemMessageConfig.Mode` as append/default behavior unless there is a strong reason to replace.
- Keep `Streaming = true` for interactive UX.
- Build tool list with `AIFunctionFactory.Create(...)` in `Tools/DiagnosticTools.cs`.
- When configuring BYOK OpenAI-compatible providers, preserve richer `ModelInfo` metadata and use `ProviderConfig.WireApi = "responses"` for GPT-5-family models.

### Resource lifecycle

- Use `await using` or explicit async disposal for `CopilotClient`/`CopilotSession`.
- Preserve robust startup/shutdown behavior and actionable prerequisite errors.

## MCP + Skills Guidance

TroubleScout should support MCP servers and skills through Copilot SDK session config.

- MCP config source: `%USERPROFILE%\\.copilot\\mcp-config.json` by default.
- Use `SessionConfig.McpServers` (dictionary) for configured servers.
- Support local/stdio and remote (`http`/`sse`) MCP server definitions.
- Default skill directory should be `%USERPROFILE%\\.copilot\\skills` when present.
- Use `SessionConfig.SkillDirectories` and `SessionConfig.DisabledSkills`.
- Track and surface:
  - configured MCP servers and skills
  - runtime-used MCP servers and skills (from session events)
- Never print secret values from MCP headers/env vars.

## PowerShell Safety Model

`Services/PowerShellExecutor.cs` enforces command safety:

- Auto-execute: read-only `Get-*` style commands
- Require approval: mutating verbs (`Set-*`, `Start-*`, `Stop-*`, `Restart-*`, `Remove-*`, etc.)
- Blocked: sensitive commands like `Get-Credential`, `Get-Secret`

Preserve this model for all changes.

## Common Change Points

- Add diagnostic tools: `Tools/DiagnosticTools.cs`
- Change agent behavior/system message: `TroubleshootingSession.cs`
- Add CLI options: `Program.cs` (manual switch parsing)
- UI/status/help text: `UI/ConsoleUI.cs`

## Notable UX Behaviors

- **`/server` slash command** (not `/connect`) — accepts one or more server names separated by spaces or commas: `/server srv1 srv2` or `/server srv1,srv2`. Replaces the former `/connect` command.
- **Multi-server `--server` flag** — repeatable (`--server srv1 --server srv2`) or comma-separated (`--server srv1,srv2`); all servers connect at startup via `InitializeAsync`.
- **ESC cancellation** — a background poller in `RunInteractiveLoopAsync` cancels the active `CancellationTokenSource`; the token is passed to `_copilotSession.SendAsync` for true RPC-level cancellation. Poller skips `ReadKey` while `LiveThinkingIndicator.IsApprovalInProgress` is true.
- **Reasoning display** — `AssistantReasoningEvent` handled in `SendMessageAsync` event switch; routed to `ConsoleUI.WriteReasoningText()` (ANSI 256-colour dark grey 238, falls back to plain text when stdout is redirected).
- **Approval dialog safety** — `LiveThinkingIndicator.PauseForApproval()` / `ResumeAfterApproval()` must wrap any `AnsiConsole.Confirm` call made while the spinner is running, to prevent the spin loop from overwriting the prompt.

## Build, Test, Verify (Required)

After editing any `.cs` files:

1. `dotnet build`
2. `dotnet test`
3. End-to-end smoke test:
   - `dotnet run -- --server localhost --prompt "how is this computer doing?"`

Also ensure analyzer/compiler issues remain clean.

## Git Workflow Rules

- Never work directly on `main`.
- Branch naming:
  - `feature/<short-desc>` for features/changes
  - `fix/<short-desc>` for bug fixes
- Before opening a PR, always bump app version in `TroubleScout.csproj` (`Version`, `AssemblyVersion`, `FileVersion`) and update `CHANGELOG.md`.
- Commit subject must start with an emoji.
- Do not force-push.

## Release Packaging Rules

Release as a single zip containing:

- `TroubleScout.exe`
- `runtimes/` folder when runtime files are present in publish output

Do not split runtime artifacts into separate downloadable files.
