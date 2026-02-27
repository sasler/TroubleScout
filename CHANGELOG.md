# Changelog

All notable changes to TroubleScout will be documented in this file.

## [v1.4.0] - 2026-02-27

### ✨ Features

- 🖧 Add multi-PSSession support for double-hop avoidance: `connect_server`, `close_server_session` tools and `run_powershell` `sessionName` parameter
- 🖧 AI agent system message now includes multi-server session guidance to prevent double-hop authentication failures
- 🖧 Status panel shows all connected target servers when additional sessions are active

### ✅ Testing

- ✅ Add `AllTargetServers_WithNoAdditional_ShouldReturnPrimaryOnly`, `AllTargetServers_WithAdditional_ShouldIncludeAll`, `ConnectAdditionalServer_SameAsPrimary_ShouldSucceedWithoutNewExecutor` session tests
- ✅ Add `ConnectServer_ShouldCallConnectCallback`, `ConnectServer_WhenCallbackFails_ShouldReturnError`, `CloseServerSession_ShouldCallCloseCallback`, `RunPowerShell_WithSessionName_ShouldUseAlternateExecutor`, `RunPowerShell_WithNullSession_ShouldUsePrimaryExecutor` tool tests
- ✅ Add `ShowStatusPanel_WithAdditionalTargets_ShouldShowPluralLabel`, `ShowStatusPanel_WithNoAdditional_ShouldShowSingularLabel` UI tests

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
