# Changelog

All notable changes to TroubleScout will be documented in this file.

The format is loosely based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

<!--
Add new entries here as they land. When cutting a release, rename this section to
`## [vX.Y.Z] - YYYY-MM-DD`, bump `Version`/`AssemblyVersion`/`FileVersion` in
`TroubleScout.csproj`, and recreate an empty `## [Unreleased]` section above.
-->

### 🐛 Bug Fixes

- 🔒 **Patch GHSA-37gx-xxp4-5rgx / GHSA-w3x6-4m5h-cxqf** - pin `System.Security.Cryptography.Xml` to `9.0.15` so `dotnet run`/`dotnet build` no longer surface `NU1903` warnings about the vulnerable `9.0.10` version that `Microsoft.PowerShell.SDK 7.5.4` pulls in transitively.
- 🧹 **Trim startup status panel** - the "MCP approved (session)" and "MCP approved (persisted)" rows no longer appear in the boot capability table; they were always empty before any approval prompt and never gained signal afterward. Both tiers are still surfaced in the HTML report.

## [v1.13.0] - 2026-04-29

### ✨ New Features

- 🛡️ **Per-server MCP approval** - approving any tool from an MCP server now covers every tool from that server for the rest of the session, instead of re-prompting for each new tool name.
- 📌 **Persistent MCP approvals** - MCP servers mapped to a `monitoring` or `ticketing` role can now be marked as user-trusted; that approval is stored in `settings.json` and persists across sessions. Manage with the new `/mcp-approvals` slash command (`list`, `clear all`, `clear <name>`).
- 🚦 **Auto-approve clearly read-only MCP tools** - tool names starting with `get_`, `list_`, `search_`, `find_`, `describe_`, `read_`, `query_`, or `inspect_` no longer trigger approval prompts in Safe mode.
- 📝 **Markdown-rendered HTML report** - `/report` now renders assistant replies as proper Markdown (bundled `marked` + `DOMPurify`) and exposes a "Copy markdown" button next to a "Copy HTML" button. A new "Export full session as Markdown" button at the top downloads the entire transcript.
- 🧰 **Rich MCP entries in the report** - MCP actions now show the actual server, tool, full arguments, and tool output (no more `N/A`), with error styling when the call failed.
- 📊 **Status bar + session header in the report** - each prompt card now mirrors the terminal status bar (model, provider, reasoning, tokens, tools), and a session-wide summary header shows totals, models used, premium-request estimate, mapped MCP roles, and persisted approvals.
- 🎨 **Light/dark theme toggle** - the report defaults to dark and now offers a manual theme toggle that persists in `localStorage`.
- 🧭 **Floating table of contents** - the report now has a TOC sidebar on wide screens for quick navigation between prompts.

### 🐛 Bug Fixes

- 🔁 **Fix MCP approval re-prompt loop** - approving an MCP tool now correctly suppresses follow-up prompts for sibling tools in the same session.
- 🩹 **Stop misleading "modify system state" warning on MCP calls** - MCP approval prompts no longer reuse the shell-mutation copy and now describe the MCP invocation explicitly.

## [v1.12.0] - 2026-04-25

### ✨ New Features

- 🎛️ **Post-analysis action dialog** - after diagnosis/recommendations or after approved commands finish, TroubleScout now returns control to the user with a next-step dialog: continue investigating, apply the fix, or stop for now.
- 🎨 **Windows Terminal wait feedback** - TroubleScout now sets the running terminal title to `TroubleScout`, drives the Windows Terminal tab/taskbar progress ring with OSC `9;4`, and uses the ASCII-safe "Wheel of IT Blame" animation while the AI is busy.

### 🐛 Bug Fixes

- ♻️ **Stop endless post-approval recursion** - approved command follow-ups now ask the AI to summarize what changed and then yield control back to TroubleScout instead of recursively telling the model to "continue your analysis" forever.
- 💤 **Better stalled-stream visibility** - when a streamed response goes quiet mid-turn, TroubleScout now surfaces explicit wait notices while keeping the tab progress indicator active.

### 📝 Documentation & UX

- 📝 **Document the new checkpoint and waiting flow** - refreshed README and AGENTS guidance for the next-step action dialog, Windows Terminal title/progress behavior, and the new waiting animation.

## [v1.11.0] - 2026-04-25

### ✨ New Features

- 🧭 **In-app MCP role mapping** - added `/mcp-role` so monitoring and ticketing MCP roles can be assigned or cleared directly from TroubleScout without editing `settings.json` by hand.
- 🤝 **Role-aware MCP delegation** - TroubleScout now exposes focused monitoring and ticketing sub-agents that use only the mapped MCP server for that role, keeping delegated evidence collection targeted and concise.

### 🐛 Bug Fixes

- 🌐 **Three-way URL approval flow** - URL permission prompts now support allow-this-URL, allow-all-URLs, or deny, with per-session persistence for approved URLs.
- 🐛 **Prominent MCP role visibility** - startup hints, `/help`, and status output now make the configured monitoring/ticketing MCP roles easier to discover and confirm.

### 📝 Documentation & UX

- 📝 **Document MCP role workflows** - refreshed README and AGENTS guidance for `/mcp-role`, role-aware delegation, and session-scoped URL approval behavior.

## [v1.10.0] - 2026-04-25

### ✨ New Features

- 🚀 **Upgrade to GitHub.Copilot.SDK `0.3.0`** - TroubleScout now targets the latest Copilot SDK release, including the current MCP config types, session metrics RPC surface, and custom-agent session configuration.
- 🧭 **Focused sub-agent groundwork** - TroubleScout now provisions inferable sub-agents for server evidence collection and issue research, keeps `web_search` on the dedicated research path, and suppresses sub-agent delta streaming from the main TUI output so delegated work stays concise.
- 🏷️ **Optional monitoring/ticketing MCP roles** - `settings.json` now supports `MonitoringMcpServer` and `TicketingMcpServer` so existing MCP servers can be mapped to those org-specific roles and surfaced back to the agent and status views.

### 🐛 Bug Fixes

- 🐛 **Persist MCP approvals for the active session** - approving an MCP permission now returns a session-scoped SDK rule, so TroubleScout stops re-prompting for the same MCP access within the current session.
- 🐛 **Use SDK session metrics for GitHub premium request totals** - GitHub-mode session cost display now comes from `session.rpc.usage.getMetrics()` instead of TroubleScout's old local multiplier estimate.
- 🐛 **Update MCP config compatibility for SDK `0.3.0`** - MCP config parsing now maps SDK `stdio` / `http` server types while preserving compatibility with older `local` / `remote` config values.

### 📝 Documentation & UX

- 📝 **Refresh README and AGENTS guidance for SDK `0.3.0`** - updated version references, MCP-role settings, session-scoped approvals, and the new sub-agent/session-metrics behavior.

## [v1.9.2] - 2026-03-22

### 🏗️ Architecture

- **Decompose oversized classes** — extracted 14 focused service/UI classes from TroubleshootingSession.cs (6,627→4,244 lines, −36%), ConsoleUI.cs (2,300→1,319, −43%), and PowerShellExecutor.cs (990→574, −42%):
  - `Services/JsonParsingHelpers` — static JSON parsing utilities
  - `Services/PermissionEvaluator` — shell permission analysis and assessment
  - `Services/ReportHtmlBuilder` — HTML troubleshooting report generation
  - `Services/CommandValidator` — command safety validation and blocking
  - `Services/CopilotCliResolver` — CLI path resolution and prerequisite checks
  - `Services/SystemPromptBuilder` — system prompt construction
  - `Services/ModelDiscoveryManager` — model listing, pricing, and display
  - `Services/ConversationHistoryTracker` — thread-safe prompt/reply history
  - `Services/ServerConnectionManager` — multi-server session lifecycle
  - `Services/ByokProviderManager` — BYOK OpenAI configuration and auth
  - `Services/SecondOpinionService` — second-opinion prompt building
  - `UI/MarkdownStreamRenderer` — streaming markdown-to-ANSI conversion
  - `UI/ModelPickerUI` — interactive model selection UI
  - `UI/PowerShellSyntaxHighlighter` — PowerShell syntax highlighting

## [v1.9.1] - 2026-03-21

### ✨ New Features

- 🤝 **Second-opinion model switching** - after choosing a different model in `/model`, TroubleScout can now either start a new clean session or carry the recorded session transcript, assistant replies, and tool actions into the new model for a second opinion.

### 🐛 Bug Fixes

- 🐛 **Keep clean model switches truly clean** - choosing the clean-session path during `/model` now clears the recorded conversation history instead of only recreating the SDK session.
- 🐛 **Restore ESC cancellation for second opinions** - second-opinion handoff requests now use the same interactive ESC cancellation flow as normal prompts, so long-running follow-up analysis can still be cancelled cleanly.

### 📝 Documentation & UX

- 📝 **Update `/model` help and README** - documented the new session handoff choice in the interactive command reference and model-selection section.

## [v1.9.0] - 2026-03-21

### ✨ New Features

- 🚀 **Upgrade to GitHub.Copilot.SDK `0.2.0`** - TroubleScout now uses the latest Copilot SDK release, including earlier session lifecycle hooks and the newer system-prompt customization model.
- 🧠 **First-class reasoning control** - compatible models now support a persisted `/reasoning` command so users can choose an explicit reasoning effort (or return to automatic behavior) and see the active setting in model details, `/status`, and the post-response status bar.
- ✏️ **Pre-populated editable prompt defaults** - `settings.json` is now seeded with the built-in prompt sections (`investigation_approach`, `response_format`, `troubleshooting_approach`, `safety`) so users can edit the current prompt guidance directly instead of starting from empty keys.
- ⚡ **Faster read-only diagnostic flow** - the built-in `get_*` diagnostic helpers now opt into SDK `skip_permission`, so routine data collection no longer incurs extra Copilot permission prompts.

### 🐛 Bug Fixes

- 🐛 **Fix SDK `0.2.0` permission compatibility** - TroubleScout now handles the SDK's typed `PermissionRequest` hierarchy instead of relying on the removed extension-data payload.
- 🐛 **Fix multiline shell previews** - permission previews now collapse embedded newlines into a single readable command preview instead of truncating after the first line.
- 🐛 **Fix doubled usage totals** - session token and cost totals are no longer double-counted after the new early-event wiring.

### 📝 Documentation & UX

- 📝 **Refresh README for reasoning and prompt editing** - documented the new `/reasoning` control, clarified that `/settings` writes seeded prompt defaults, and refreshed the interactive command reference.
- 📝 **Update release-process examples** - versioned WinGet validation and release-tag examples now reference the `v1.9.0` release line.

## [v1.8.3] - 2026-03-18

### ✨ New Features

- 💰 **LiteLLM pricing fallback for BYOK models** — when the BYOK provider's `/models` endpoint doesn't include pricing metadata, TroubleScout now falls back to a curated LiteLLM-based pricing database covering ~80 popular chat models (GPT, Claude, Gemini, Llama, Mistral, etc.). Estimated prices are shown with a `~` prefix (e.g., `~$2.50/M in`) to clearly indicate they are estimates.
- 📊 **Session cost & token tracking** — the status bar after each AI response now shows cumulative session totals: total input/output tokens, estimated cost for BYOK models (`~$0.04 est.`), or estimated premium requests for GitHub models (`~2.5 premium reqs`).
- 🔍 **Autonomous investigation prompt** — system prompts now instruct the AI to exhaust all available diagnostic tools before asking the user, reducing unnecessary back-and-forth during troubleshooting.
- ✏️ **User-editable system prompts** — system prompt sections are now customizable via `settings.json`. Use `SystemPromptOverrides` (keyed by section name) to replace default sections, or `SystemPromptAppend` to add custom instructions.
- 🧹 **BYOK chat-only model filter** — the model picker now hides non-chat models (image generators, embedding models, audio models, etc.) when using BYOK providers, keeping the list focused on relevant models.
- 📐 **Fixed-width model selection table** — the `/model` picker table and panels now always expand to full terminal width, eliminating jarring resizes when model names or metadata vary in length.

### 🐛 Bug Fixes

- 🐛 **Auto-approve read-only PowerShell shell commands** — Safe mode now reuses TroubleScout's PowerShell safety validator for PowerShell-shaped `shell` permission requests, so read-only pipelines like `Get-ChildItem | Select-Object | Sort-Object` no longer trigger a misleading approval prompt.

### 📝 Documentation & UX

- 📝 **Clarify shell approval messaging** — when a PowerShell shell command still needs approval, the prompt now explains whether it is blocked, ambiguous, or simply not classified as read-only instead of always implying that every shell command is mutating.
- 📝 **AGENTS.md TDD workflow** — added a Development Workflow (TDD) section documenting the required test-driven development process for coding agents.

## [v1.8.2] - 2026-03-16

### ✨ Features

- 🔐 **Add startup `--jea` support** — TroubleScout can now preconnect a single JEA endpoint at launch with `--jea <server> <configurationName>`, making headless validation and smoke testing possible without going through the interactive TUI first.

### 🐛 Bug Fixes

- 🐛 **Fix JEA no-language runspaces** — JEA command execution now avoids the script-wrapper path that failed on no-language endpoints, so command discovery and constrained command execution can succeed.
- 🐛 **Remove redundant `/jea` approval prompt** — explicitly choosing `/jea` no longer triggers an extra Safe-mode confirmation before the session is established.
- 🐛 **Guide `/jea` input interactively** — entering `/jea` without parameters now prompts for the server name and configuration name instead of failing with usage-only output.

### 📝 Documentation & UX

- 📝 **Use generic JEA examples** — help text and docs now use placeholders like `server1` and `JEA-Admins` instead of real-looking infrastructure names.
- 📝 **Document JEA CLI parity** — README, release guidance, and AGENTS guidance now reflect the `--jea` startup flag and the updated `/jea` UX.

## [v1.8.1] - 2026-03-14

### ✨ Features

- 🚀 **Automated WinGet PR workflow** — published GitHub Releases can now trigger a dedicated `.github/workflows/winget.yml` job that uses `winget-releaser` to open or update the `microsoft/winget-pkgs` manifest PR for `sasler.TroubleScout`.

### 📝 Documentation & UX

- 📝 **Document WinGet automation setup** — release docs now cover the required `winget-pkgs` fork, `WINGET_TOKEN` secret, manual retry flow, and why WinGet submission runs separately from the main release workflow.
- 🧪 **Add local WinGet validation helper** — new `Tools/Validate-WinGetRelease.ps1` can download release zips, generate the TroubleScout manifest, run `winget validate`, and optionally invoke the official `winget-pkgs` Sandbox test before or after release publication.
- 📝 **Refresh versioned examples** — README, workflow examples, and release-process command samples now reference the `v1.8.1` release line.

## [v1.8.0] - 2026-03-13

### ✨ Features

- 🔐 **JEA (Just Enough Administration) support** — new `/jea <server> <configurationName>` slash command and `connect_jea_server` AI tool for constrained PowerShell endpoints. Automatically discovers available commands via `Get-Command` and strictly enforces the allowed command list — all other commands are blocked. System message is updated to inform the AI agent of available JEA commands.
- 🔧 **Configurable safe commands** — `SafeCommands` list in `settings.json` with wildcard support (e.g., `"Get-*"`). Pre-populated with defaults on first load. Dangerous verb wildcards (`Remove-*`, `Set-*`, etc.) are rejected as a safety guardrail. Changes are applied immediately when settings are reloaded.
- ⚙️ **`/settings` slash command** — opens `settings.json` in the configured editor (`EDITOR`/`VISUAL` env vars, fallback to `notepad`). Reloads and applies settings changes live after editor closes.
- ⚡ **Immediate startup feedback** — shows target server info before the initialization spinner for faster perceived startup.
- 🎨 **Redesigned HTML report** — modern dark-mode design with hero header, summary statistics cards, timeline-style prompt cards, color-coded approval states, copy-to-clipboard buttons, line-numbered code blocks, AI chat bubble for agent replies, print-friendly and responsive layout.

### 🐛 Bug Fixes

- 🐛 **Fix report opening as wrong user** — replaced `UseShellExecute` with `cmd.exe /c start` to respect the current process user context when running via RunAs.

### 🛡️ Security

- 🛡️ **JEA fail-closed validation** — JEA sessions block all commands until command discovery completes. Localhost JEA connections are rejected (requires remote target). Command-position-only extraction prevents false matches on hyphenated parameter values.
- 🛡️ **Safe command wildcard guardrails** — bare `"*"` and dangerous verb wildcards (e.g., `"Remove-*"`, `"Stop-*"`) are silently rejected to prevent accidental auto-approval of destructive commands.

## [v1.7.0] - 2026-03-12

### ✨ Features

- ✨ **Enhanced permission prompts** — approval dialogs now use a three-option `SelectionPrompt` (Yes / No / Explain). Choosing "Explain" shows a detailed command breakdown before re-prompting for approval.
- 📊 **Always-visible status bar** — a compact info line showing model, provider, token usage, and tool invocation count is displayed after every AI response.
- ⏱️ **Elapsed timer in thinking indicator** — the spinner now shows total elapsed time (e.g., `Thinking... (12s) — ESC to cancel`). Per-phase timers reset on each status change. Long-running phases trigger yellow warnings at 30s and 60s.
- 🛡️ **Activity watchdog** — a background watchdog during `SendMessageAsync` detects inactivity: 15s idle shows "Waiting for response", 30s shows "Connection seems slow" in the thinking indicator.
- 🔄 **Retry prompt** — new `ShowRetryPrompt` provides a Retry/Skip selection after errors or timeouts instead of silently failing.

### 🐛 Bug Fixes

- 🐛 **Cleaner ESC cancellation** — improved interaction between ESC polling and error states to reduce spurious "Communication error" messages.
- 🐛 **Thinking indicator clarity** — spinner now consistently shows "ESC to cancel" (was "ESC to stop") and includes elapsed time for better user orientation.

### 📝 Documentation & UX

- 📝 Update README with new approval flow, status bar, timer, and watchdog features.
- 📝 Update AGENTS.md Notable UX Behaviors with approval prompt, status bar, and watchdog details.

## [v1.6.0] - 2026-03-11

### ✨ Features

- ✨ **GitHub.Copilot.SDK upgraded to v0.1.32** — picks up the newer typed permission-result API and CLI compatibility improvements while keeping the existing event-streaming architecture.
- 🤖 **Richer model metadata and picker UX** — `/model` now shows provider-specific entries only for connected providers, restores GitHub premium multipliers, shows BYOK pricing when provider metadata includes it, supports ESC to keep the current model, and shows a clearer post-selection model summary.
- 📊 **More informative status view** — `/status` now groups provider, usage, and capability details more clearly and keeps the combined context-used/max view prominent.

### 🐛 Bug Fixes

- 🐛 **Fix Safe-mode approval dialog details** — permission prompts once again show the actual requested shell command or MCP tool details instead of a generic placeholder.
- 🐛 **Fix reasoning/output ordering** — reasoning is now kept strictly ahead of the assistant response, with a visible blank line separator and no late reasoning tokens after the response starts.
- 🐛 **Fix startup model fallback after SDK upgrade** — TroubleScout now resolves a verified available model at startup instead of depending on an invalid default model selection.
- 🐛 **Fix test settings leakage** — model-switch tests now use isolated settings storage so they cannot overwrite the real user profile state while validating provider switching.

### 📝 Documentation & UX

- 📝 Update README and agent guidance for SDK `0.1.32`, richer `/model` metadata, BYOK model metadata handling, and the refreshed status display.

## [v1.5.0] - 2026-03-03

### ✨ Features

- 🔌 **Multi-server `--server` flag** — Pass `--server` multiple times or use comma-separated values to connect to several servers at startup (e.g., `--server srv1 --server srv2` or `--server srv1,srv2`). CLI help updated to reflect multi-server syntax.
- 🖥️ **`/server` slash command** (replaces `/connect`) — Consistent with the CLI flag. Accepts multiple servers in a single call: `/server srv1 srv2` or `/server srv1,srv2`. Both space- and comma-separated syntax work.
- ⏹️ **ESC cancels the in-progress agent turn** — Press ESC while the AI is thinking to cancel the current turn at the SDK level. The spinner now shows `(ESC to stop)` at all times as a visible hint. On cancellation a clear `[Cancelled]` indicator is shown.
- ⌨️ **Prompt history** — Up/Down arrow keys recall previous inputs during the interactive prompt. ESC clears the current input buffer.
- 💭 **Reasoning display** — When a model emits reasoning/thinking tokens (`AssistantReasoningEvent`), they are streamed in dark grey with a 💭 prefix before the main response, giving visibility into the model's thought process.

### ⬆️ Dependencies

- ⬆️ **GitHub.Copilot.SDK upgraded to v0.1.29** — Removes the `--headless` flag that caused startup crashes with Copilot CLI v0.0.420. Adds defensive error handling around SDK startup to surface clean diagnostics on failure.

### 🐛 Bug Fixes

- 🐛 **Fix PSSession approval dialog** — The `LiveThinkingIndicator` background spinner was overwriting `AnsiConsole.Confirm` prompts for `connect_server` approval. The indicator now pauses during approval dialogs and resumes after.
- 🐛 **Fix `/byok clear` memory state** — `/byok clear` now resets in-memory BYOK state so a subsequent `/model` switch does not re-save `UseByokOpenAi=true` to disk.
- 🐛 **Fix multi-server agent awareness** — Agent system message now lists all active PSSessions so the AI knows which servers are connected without needing to ask the user.
- 🐛 **Fix reasoning display** — Reasoning tokens now stream incrementally via `AssistantReasoningDeltaEvent` instead of appearing all at once.

### ✨ Additions

- ✨ **`--no-byok` CLI flag** — Forces the GitHub Copilot provider at startup, ignoring any saved BYOK provider selection.

## [v1.4.0] - 2026-02-27

✨ **Features**

- 🖧 **Multi-server PSSession support** — Use `connect_server` and `close_server_session` tools to establish direct connections to multiple servers, avoiding PowerShell Remoting double-hop authentication issues. Run commands on any connected server via `run_powershell` with an optional `sessionName` parameter.
- 🔀 **Accurate provider/model switching** — Dual-source models (available via both GitHub Copilot and BYOK) now appear as separate entries in `/model`, making it explicit which provider will be used. Post-switch confirmation shows both model and provider.
- 🔧 **Richer tool/MCP usage display** — Tool invocations show human-readable descriptions (e.g., "Scanning Event Logs" instead of "get_event_logs"). MCP tool calls show the server name. Tool invocation count tracked in `/status`.

🛡️ **Reliability & Safety Improvements**

- ⬆️ **GitHub.Copilot.SDK updated to v0.1.28** — addresses breaking change requiring permission handler; read-only tool operations auto-approved in all modes; mutating MCP/shell operations prompt for approval in Safe mode.
- 🔒 **Execution mode changes apply live** — switching `/mode safe` or `/mode yolo` now immediately affects permission decisions, including for active multi-server sessions.
- 🛡️ **Multi-session command routing** — Approved commands for alternate server sessions now execute on the correct server, with proper target verification.
- 🔁 **Session executor robustness** — Additional PSSession executors are safely disposed even if one fails; execution mode propagates to all active sessions.

📝 **Documentation & UX**

- 💬 **Clearer AI guidance** — System message now explicitly encourages tool use, explains read-only tools auto-execute in all modes, and includes double-hop avoidance instructions.
- 📊 **Provider row in status** — `/status` and `/capabilities` now show the active provider (GitHub Copilot or BYOK) as a dedicated row alongside the AI model.

## [v1.3.4] - 2026-02-27

### 🐛 Bug Fixes

- 🐛 Fix `--help` / `-h` to display proper CLI usage (flags, options, examples) instead of the TUI slash-command reference
- 🐛 Fix `--mode` with missing value to emit a clear error and exit with code 1 instead of silently ignoring
- 🐛 Add missing-value error handling for all flags that require values (`--server`, `--prompt`, `--model`, `--mcp-config`, `--skills-dir`, `--disable-skill`, `--openai-base-url`, `--openai-api-key`); `--model` additionally hints to use `/model` interactively
- 🐛 Remove undocumented `-debug` alias; debug mode is now enabled only via `-d` or `--debug`

### 📝 Documentation & UX

- 📝 Add `ShowCliHelp()` method with full CLI flag reference and usage examples

### ✅ Testing

- ✅ Add `ShowCliHelp_ShouldRenderUsageAndOptions_WhenVersionIsProvided` and `ShowCliHelp_ShouldRenderUsageAndOptions_WhenVersionIsNull` tests that capture rendered output and assert on key sections/flags

## [v1.3.3] - 2026-02-20

### ✨ Features

- ✨ Add `/byok clear` command aliases (`/byok off`, `/byok disable`) to remove saved BYOK settings from profile storage

### 🛡️ Reliability Improvements

- 🛡️ Add non-interactive startup guard for no-argument launches so validator-style executable checks exit cleanly with status code 0

### 📝 Documentation & UX

- 📝 Update welcome and `/help` command references to include `/byok clear`

### ✅ Testing

- ✅ Re-validate with `dotnet build`, `dotnet test`, and smoke run (`dotnet run -- --server localhost --prompt "how is this computer doing?"`)

## [v1.3.2] - 2026-02-19

### ✨ Features

- ✨ Add OpenAI-compatible BYOK mode with `/byok`, base URL + API key configuration, and persisted session settings
- ✨ Add in-app `/login` command and allow dual-provider model usage (GitHub Copilot + BYOK)
- ✨ Merge `/model` catalog across providers and label model source (`GitHub`, `BYOK`, `GitHub+BYOK`)
- ✨ Add Windows ARM64 release artifacts alongside Windows x64 in release workflow and packaging

### 🛡️ Reliability Improvements

- 🛡️ Improve startup behavior for unauthenticated GitHub sessions by allowing interactive setup without immediate failure
- 🛡️ Fix status panel markup crash by escaping model text that contains source tags (e.g., `[GitHub]`)
- 🛡️ Keep GitHub auth status tracking accurate while BYOK is active
- 🛡️ Remove hardcoded model-rate and default-model assumptions from model selection paths

### 📝 Documentation & UX

- 📝 Add `LICENSE.md` for distribution and publishing readiness
- 📝 Refresh README/CONTRIBUTING/release docs for BYOK usage, bundled CLI behavior, and multi-architecture releases
- 📝 Expand quick-help and `/help` command references for `/login` and `/byok`

### ✅ Testing

- ✅ Update `ConsoleUITests` for dynamic model-rate behavior (no hardcoded model map)
- ✅ Update app settings persistence tests for BYOK fields
- ✅ Re-validate with `dotnet build` and targeted test runs for session, settings, and UI flows

## [v1.3.1] - 2026-02-19

### ✨ Features

- ✨ Render markdown pipe tables from streamed assistant responses as Spectre tables
- ✨ Add live slash-command suggestions while typing prompt input
- ✨ Make `/clear` start a new Copilot conversation session and surface a session ID

### 🛡️ Reliability Improvements

- 🛡️ Guard interactive prompt input against oversized pastes and reset input safely with explicit warning
- 🛡️ Fix multiline input redraw clearing to avoid row-overflow cursor issues

### 📝 Documentation & UX

- 📝 Split startup quick-help from full `/help` command reference
- 📝 Refresh help copy and reframe legacy "Diagnostic Categories" as "Troubleshooting Areas"
- 📝 Route `--help` output through Spectre-based UI help rendering

### ✅ Testing

- ✅ Add markdown table parsing tests in `TroubleScout.Tests/UI/ConsoleUITests.cs`
- ✅ Re-validate with `dotnet build`, `dotnet test`, and smoke run (`dotnet run -- --server localhost --prompt "how is this computer doing?"`)

## [v1.3.0] - 2026-02-18

### ✨ Features

- ✨ Upgrade `GitHub.Copilot.SDK` to `0.1.25`
- ✨ Expand `/model` catalog to include newly available CLI models (including `claude-sonnet-4.6` and `gpt-5.3-codex`)

### 🛡️ Reliability Improvements

- 🛡️ Enforce preinstalled Copilot CLI strategy with `CopilotSkipCliDownload=true`
- 🛡️ Improve Copilot CLI path resolution to avoid stale shell wrappers and use concrete installed targets
- 🛡️ Refresh model list when opening `/model` to surface newly available models without restarting
- 🛡️ Refine initialization failure messaging to clearly separate install, auth, and CLI startup issues

### 📝 Documentation & UX

- 📝 Update README prerequisites and model examples for current Copilot CLI usage
- 📝 Add inferred model multiplier labels when SDK billing metadata is absent in the model picker

### ✅ Testing

- ✅ Re-validate with `dotnet build`, `dotnet test`, and smoke run (`dotnet run -- --server localhost --prompt "how is this computer doing?"`)

## [v1.2.8] - 2026-02-17

### ✨ Features

- ✨ Improve session output reliability and model switching (`#34`)
- ✨ Add session report logging and UI enhancements (`#33`)

### 🛡️ Reliability Improvements

- 🛡️ Improve exit command parsing based on review feedback (`#34`)
- 🛡️ Refine report and approval logging behavior from PR review updates (`#33`)

### ✅ Testing

- ✅ Expand troubleshooting session and diagnostic tool test coverage for output/report flows (`#33`, `#34`)

## [v1.2.7] - 2026-02-15

### ✨ Features

- ✨ Add safe/YOLO execution modes with CLI flag and `/mode` switching
- ✨ Add session report logging and `/report` HTML export for prompts/actions

### 🛡️ Reliability Improvements

- 🛡️ Improve Copilot startup diagnostics with targeted CLI/Node checks and PowerShell version warnings
- 🛡️ Gate technical exception details behind `--debug` for clearer user-facing failures

### 📝 Documentation & UX

- 📝 Refresh Copilot CLI install guidance and release packaging notes
- 📝 Update status/prompt UI to show execution mode and new report command

### ✅ Testing

- ✅ Add coverage for prerequisite validation, execution mode parsing, and report logging

## [v1.2.6] - 2026-02-12

### ✨ Features

- ✨ Add Copilot MCP server support via `%USERPROFILE%\\.copilot\\mcp-config.json`
- ✨ Add Copilot skills support via `%USERPROFILE%\\.copilot\\skills` (with CLI overrides)
- ✨ Show configured and runtime-used MCP servers/skills in status output (`/status`, `/capabilities`)

### 🛡️ Reliability Improvements

- 🛡️ Improve Copilot startup prerequisite validation for CLI/Node.js/SDK compatibility
- 🛡️ Add fast Node.js major-version check (`>=24`) before Copilot session startup
- 🛡️ Improve initialization and protocol-mismatch errors with actionable remediation guidance

### 📝 Documentation & UX

- 📝 Add and document `--version` CLI support in help/banner workflows
- 📝 Update troubleshooting/prerequisite documentation for current Copilot auth/runtime setup

### ✅ Testing

- ✅ Add test coverage for unsupported Node.js version handling and prerequisite validation paths

### 🔧 Other Changes

- 🔧 Add root `AGENTS.md` for repository-wide coding-agent guidance
- 🔧 Remove legacy `.github/copilot-instructions.md` and align docs/help text

## [v1.2.5] - 2026-02-08

### ✨ Improvements

- ✨ Update GitHub.Copilot.SDK to v0.1.23
- ✨ Prefer native PowerShell cmdlets with resilient fallbacks for diagnostics
- ✨ Serialize runspace execution to avoid concurrent pipeline errors

## [v1.2.4] - 2026-02-06

### 🐛 Critical Bug Fixes

- 🐛 Fix release workflow failing to find `runtimes/` during packaging
  - Publish to an explicit output directory and package from there
  - Update GitHub Release action to v2
  - Always include a `runtimes/` directory in the zip (copied if present, otherwise created empty)

## [v1.2.3] - 2026-02-06

### 🐛 Critical Bug Fixes

- 🐛 Fix PowerShell SDK initialization in single-file published executables
  - Added `IncludeNativeLibrariesForSelfExtract` and `IncludeAllContentForSelfExtract` properties to enable proper resource extraction
  - Re-enabled `PublishSingleFile=true` for clean distribution (exe + runtimes folder only)
  - PowerShell SDK now extracts required configuration files to temp directory at runtime

### ✅ Testing

- ✅ Fixed test isolation issues in `AppSettingsStoreTests` with sequential execution
- ✅ All 74 tests passing with improved file handle cleanup
- ✅ Added GC collection to prevent file locking issues between tests

### 📝 Technical Details

- Root cause: PowerShell SDK requires physical configuration files, but single-file mode embeds them
- Solution: Use .NET's extraction properties to automatically extract embedded resources at runtime
- Package now distributes as clean single-file exe (125 MB) with runtimes folder, matching v1.0.x structure

## [v1.2.2] - 2026-02-06

### 🐛 Critical Bug Fixes

- 🐛 Fix PowerShell SDK initialization failure in published executables
  - Removed `PublishSingleFile=true` from build configuration
  - PowerShell SDK requires configuration files on disk that aren't compatible with single-file publishing
  - Application now ships as TroubleScout.exe with supporting DLLs in the same folder

### 📝 Technical Details

- Root cause: PowerShell SDK's `PSSnapInReader.ReadEnginePSSnapIns()` calls `Path.Combine` with null paths when configuration files are unavailable
- Single-file publishing embeds resources but PowerShell SDK needs physical files (PowerShell.Format.ps1xml, etc.)
- Solution: Distribute as standard published application with all required files

## [v1.2.1] - 2026-02-06

### 🐛 Bug Fixes

- 🐛 Fix null path exception in published executable when `ApplicationData` is unavailable
- 🐛 Add robust fallback chain for settings path: `ApplicationData` → `LocalApplicationData` → `CurrentDirectory`

### ✨ Improvements

- ✨ Use explicit `.Where()` filtering for cleaner, more readable code
- ✨ Make `GetCopilotCliPath` testable by changing visibility to `internal`

### ✅ Testing

- ✅ Add comprehensive test coverage for null ApplicationData scenarios
- ✅ Add 6 new tests validating fallback behavior and path resolution

## [v1.2.0] - 2026-02-05

### ✨ Features

- ✨ Add automated release PR workflow
- ✨ Improve error messages and consolidate documentation

### 🐛 Bug Fixes

- 🐛 Fix auto-release workflow non-fast-forward push errors
- 🐛 Fix TUI input redraw and line break issues

### 🔧 Other Changes

- 🔧 Update release workflow to follow best practices

- 📝 Add implementation summary
