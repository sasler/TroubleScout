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

- GitHub.Copilot.SDK (`0.3.0`) via event-based streaming (`CopilotSession.On(...)`)
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
- Keep `IncludeSubAgentStreamingEvents = false` unless the TUI is explicitly updated to render sub-agent deltas separately from the main assistant stream.
- Use `DefaultAgent.ExcludedTools` plus inferable `CustomAgents` to keep the root agent focused; the current foundation routes `web_search` through a dedicated research sub-agent.

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
- Support optional `MonitoringMcpServer` / `TicketingMcpServer` settings that map existing configured MCP servers to those roles in the system prompt and status output.
- Preserve the in-app `/mcp-role` workflow so those role mappings can be assigned or cleared without editing JSON manually.
- Track and surface:
  - configured MCP servers and skills
  - runtime-used MCP servers and skills (from session events)
- Return session-scoped MCP approval rules after the user approves an MCP permission so repeated prompts stop for the rest of the active TroubleScout session.
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
- **`/jea` guided flow** — `/jea` can be entered with or without parameters. When arguments are omitted, `RunInteractiveLoopAsync` prompts first for the server name and then for the configuration name. Because the user explicitly invoked `/jea`, the TUI does not ask for a second Safe-mode approval prompt before connecting.
- **Single-session `--jea` flag** — `--jea <server> <configurationName>` preconnects one startup JEA session during `InitializeAsync`, which is useful for headless runs and JEA smoke tests.
- **No-language JEA execution** — `Services/PowerShellExecutor.cs` must avoid `AddScript(...)` for JEA sessions. Build JEA commands with the PowerShell command API so no-language endpoints can connect and run allowed cmdlets; unsupported language constructs should fail clearly instead of surfacing the raw runspace syntax error.
- **ESC cancellation** — a background poller in `RunInteractiveLoopAsync` cancels the active `CancellationTokenSource`; the token is passed to `_copilotSession.SendAsync` for true RPC-level cancellation. Poller skips `ReadKey` while `LiveThinkingIndicator.IsApprovalInProgress` is true.
- **Reasoning display** — `AssistantReasoningEvent` handled in `SendMessageAsync` event switch; routed to `ConsoleUI.WriteReasoningText()` (ANSI 256-colour dark grey 238, falls back to plain text when stdout is redirected).
- **Approval dialog safety** — `LiveThinkingIndicator.PauseForApproval()` / `ResumeAfterApproval()` must wrap any `AnsiConsole.Prompt` or `SelectionPrompt` call made while the spinner is running, to prevent the spin loop from overwriting the prompt.
- **Three-option approval prompts** — `ConsoleUI.PromptCommandApproval` returns `ApprovalResult` (Approved/Denied). The prompt offers Yes, No, or Explain via `SelectionPrompt<string>`. Explain shows a detail panel then re-prompts with a binary Yes/No.
- **Post-analysis action dialog** — after a turn reaches diagnosis/recommendations or after approved commands finish, `ConsoleUI.PromptPostAnalysisAction()` asks whether to continue investigating, apply the fix, or stop for now. Prompt guidance should have the agent end with `## Ready for next action` so TroubleScout can reclaim control cleanly.
- **URL approval prompts** — `ConsoleUI.PromptUrlApproval()` provides a three-way choice: allow this URL, allow all URLs for the active session, or deny. Exact-URL approvals and allow-all state are stored only for the current TroubleScout session.
- **`/mcp-role` slash command** — supports interactive mapping plus direct assignment (`/mcp-role monitoring zabbix`, `/mcp-role ticketing redmine`, `/mcp-role clear all`). Changes persist to `settings.json`, reload session settings immediately, and should remain visible in startup/status surfaces.
- **Post-response status bar** — `ConsoleUI.WriteStatusBar(StatusBarInfo)` renders a compact one-line bar after each AI response with model name, provider, token counts, and tool invocations. Data comes from `BuildStatusBarInfo()`.
- **Terminal title + progress ring** — `ConsoleUI.BeginAppLifetime()` / `EndAppLifetime()` set the running title to `TroubleScout`, restore the prior title on exit when possible, and use `SetWindowsTerminalProgress()` to drive Windows Terminal's OSC `9;4` tab/taskbar progress indicator while the app is busy.
- **Waiting animation** — `LiveThinkingIndicator` uses the ASCII-safe "Wheel of IT Blame" frames as a prefix animation, keeps the Windows Terminal progress ring active for the full AI turn, and clears it on dispose.
- **Elapsed timer** — `LiveThinkingIndicator` tracks total and per-phase elapsed time. Shows `(Xs)` after 3s of runtime. Per-phase timer resets on `UpdateStatus`/`ShowToolExecution`. Warnings appear at 30s ("Still working...") and 60s ("Still waiting...") when a phase is stuck.
- **Activity watchdog** — `RunActivityWatchdogAsync` runs alongside `SendMessageAsync` and monitors `lastEventTime`. If no events arrive for 15s, updates indicator to "Waiting for response"; at 30s, "Connection seems slow". Once streaming has started, those thresholds emit visible wait notices instead of silently going idle. Watchdog is cancelled before the thinking indicator is disposed.

## Build, Test, Verify (Required)

After editing any `.cs` files:

1. `dotnet build`
2. `dotnet test`
3. End-to-end smoke test:
   - `dotnet run -- --server localhost --prompt "how is this computer doing?"`
   - For JEA-related changes, also validate the startup CLI path with `--jea <server> <configurationName>` using environment-appropriate test values.

Also ensure analyzer/compiler issues remain clean.

## Development Workflow (TDD)

All coding tasks must follow this workflow for each logical unit of work:

1. **Write failing tests** — only tests that actually matter. Less is better. Focus on behavior, not implementation details.
2. **Implement** — write the minimum code to make the tests pass.
3. **Run tests, fix, repeat** — `dotnet build && dotnet test` until all tests pass.
4. **Run smoke tests** — `dotnet run -- --server localhost --prompt "how is this computer doing?"`. If anything fails, go back to step 2. The app was fully functional before your changes — any errors are regressions you introduced.
5. **Code review** — send to a subagent with a different model for review. If implementation used GPT, use the latest Sonnet model for review; if it used Claude, use the latest GPT model.
6. **Apply valid suggestions** — implement the review feedback, then repeat from step 3.
7. **Commit and move to next task**.

### Model Selection for Agents

| Task                    | Model        |
|-------------------------|--------------|
| Coding / implementation | GPT 5.4      |
| Code review             | Sonnet 4.6   |
| UI / design tasks       | Opus 4.6     |
| App smoke tests         | GPT 4.1 (GitHub) — free tier |

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
