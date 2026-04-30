# Architecture

This document describes the runtime architecture of TroubleScout for users and
contributors who want to understand what happens between a typed prompt and a
diagnostic command running on a target server.

For agent-only build/test/style rules, see [AGENTS.md](../AGENTS.md). For the
contributor workflow, see [CONTRIBUTING.md](../CONTRIBUTING.md).

## High-level component map

```text
Program.cs (CLI entry, arg parsing)
        │
        ▼
TroubleshootingSession (per-process Copilot session orchestrator)
        │
        ├── UI/ConsoleUI.cs            ── Spectre.Console TUI: banner, prompts,
        │                                 status bar, approval dialogs, live
        │                                 thinking indicator, markdown stream.
        │
        ├── Tools/DiagnosticTools.cs   ── AI-callable tool functions registered
        │                                 with the Copilot SDK via
        │                                 AIFunctionFactory.Create(...).
        │
        └── Services/                  ── Infrastructure (no UI):
            ├── PowerShellExecutor      runspace lifecycle, JEA endpoints,
            │                            command execution.
            ├── CommandValidator        Safe-mode classification (Get-* auto,
            │                            mutations require approval, sensitive
            │                            commands blocked).
            ├── PermissionEvaluator     Composes validator + safe-list +
            │                            execution mode into an approval
            │                            decision.
            ├── ServerConnectionManager Tracks active targets and JEA sessions.
            ├── AppSettings(Store)      Per-profile settings.json with
            │                            DPAPI-encrypted secrets.
            ├── ByokProviderManager     OpenAI-compatible provider config.
            ├── ModelDiscoveryManager   Merged GitHub + BYOK model list.
            ├── ModelPricingDatabase    Token pricing for status bar / report.
            ├── SessionUsageTracker     Per-session token + premium usage.
            ├── ConversationHistoryTracker  In-memory transcript for /report.
            ├── ReportHtmlBuilder       Generates HTML session report.
            ├── McpReadOnlyHeuristic    Auto-approves clearly read-only MCP
            │                            tools (get_*, list_*, search_*, ...).
            └── SystemPromptBuilder     Composes the per-target system prompt.
```

## Request flow (user prompt → response)

1. `Program.cs` parses CLI options and constructs a `TroubleshootingSession`.
2. `TroubleshootingSession.InitializeAsync()`:
   - resolves the Copilot CLI binary,
   - opens a `CopilotClient` and a `CopilotSession`,
   - registers diagnostic tools via `AIFunctionFactory.Create(...)`,
   - wires MCP servers and skills from `~/.copilot/mcp-config.json` and
     `~/.copilot/skills` (defaults; override with `--mcp-config` /
     `--skills-dir`).
3. `RunInteractiveLoopAsync()` reads input via `ConsoleUI.GetUserInput`. Slash
   commands are dispatched in-process; everything else is sent to the Copilot
   session.
4. `SendMessageAsync()` subscribes to SDK events:
   - `AssistantMessageDeltaEvent`     → streamed markdown via
                                         `MarkdownStreamRenderer`,
   - `AssistantReasoningEvent`        → reasoning panel
                                         (`ConsoleUI.WriteReasoningText`),
   - tool / MCP / URL approval events → routed through the approval pipeline
                                         (see below),
   - `SessionIdleEvent`               → completion sentinel.
5. After each turn, `BuildStatusBarInfo()` renders a one-line status bar with
   model, provider, token counts, and tool invocations, and a post-analysis
   action prompt may offer continue / apply / stop.

A background watchdog (`RunActivityWatchdogAsync`) updates the live thinking
indicator if no events arrive for 15 / 30 seconds, so the user sees that the
app is waiting on the model rather than hung.

## Approval pipeline

Approvals are gated at three layers:

| Layer | What it gates | Implementation |
| --- | --- | --- |
| PowerShell command | `Set-*`, `Stop-*`, `Restart-*`, etc. | `CommandValidator` + `PermissionEvaluator`, prompted by `ConsoleUI.PromptCommandApproval` (Yes / No / Explain). |
| MCP tool | Per-server, not per-tool | `ConsoleUI.PromptMcpApproval` returning `McpApprovalResult` (`ApproveOnce`, `ApproveServerForSession`, `ApproveServerPersist`, `Deny`). Persistent only for monitoring/ticketing role-mapped servers. |
| Outbound URL | Web fetches inside the session | `ConsoleUI.PromptUrlApproval` (this URL / all URLs for session / deny). |

Read-only commands (`Get-*` and similar) auto-execute via
`AppSettingsStore.DefaultSafeCommands`. Read-only-shaped MCP tools
(`get_*`, `list_*`, `search_*`, `find_*`, `describe_*`, `read_*`, `query_*`,
`inspect_*`) auto-execute via `McpReadOnlyHeuristic`. Sensitive commands such
as `Get-Credential` and `Get-Secret` are blocked outright.

While the live thinking indicator is running, every prompt invocation must be
wrapped in `LiveThinkingIndicator.PauseForApproval()` /
`ResumeAfterApproval()` so the spinner does not overwrite the dialog.

## PowerShell execution

`Services/PowerShellExecutor.cs` owns one runspace per executor instance. Each
command runs in a fresh `PowerShell` pipeline bound to that runspace, which is
the recommended pattern for keeping execution state predictable while reusing
session config.

For JEA endpoints the executor never calls `AddScript(...)` — JEA sessions are
no-language by definition. Commands are built with the PowerShell command API
(`AddCommand` + `AddParameter`) so constrained endpoints stay valid and
unsupported language constructs fail clearly instead of surfacing the raw
runspace syntax error.

## Cancellation

ESC during a turn cancels at the RPC layer, not just the UI. A background
poller in `RunInteractiveLoopAsync` (using `Console.KeyAvailable` with a 50 ms
sleep, paused while approvals are open) cancels the active
`CancellationTokenSource`, and that token is passed straight to
`_copilotSession.SendAsync` so the SDK aborts the in-flight call.

## Settings and secrets

`AppSettings` is stored at `%APPDATA%\TroubleScout\settings.json` per profile.
The BYOK API key is encrypted at rest with DPAPI
(`ByokOpenAiApiKeyEncrypted`). MCP role mappings, persisted MCP approvals
(monitoring/ticketing only), system-prompt overrides, and the active safe
command list all live in the same file. `/settings` opens the file in the
default editor and reloads on save.

## Reports

`/report` calls `ReportHtmlBuilder` to render the `ConversationHistoryTracker`
contents into a self-contained HTML file in `%TEMP%`, with embedded `marked`
and `DOMPurify` for safe Markdown rendering and per-prompt status mirroring
the terminal status bar.
