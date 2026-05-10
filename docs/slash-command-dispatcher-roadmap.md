# Slash Command Dispatcher Roadmap

This checklist tracks the remaining complex slash-command extractions after the
registry-backed documentation and initial dispatcher work landed. Keep each item
as a separate PR so interactive behavior stays easy to review and test.

## Extraction Order

- [x] `/reasoning` - Move parsing, supported-effort validation, preference
  persistence, active-session recreation, failure rollback, and summary output
  into `SlashCommandDispatcher.DispatchAsync`.
- [x] `/report` - Move no-history handling, temp report generation, HTML write,
  browser-open attempt, and user-facing success/warning messages.
- [x] `/mcp-approvals` - Move `list`, `clear all`, and `clear <server>` while
  preserving per-session and persisted approval semantics.
- [ ] `/server` - Move direct multi-server connection flow while preserving
  Safe-mode approval outside spinners and session recreation after additional
  targets.
- [ ] `/jea` - Move guided prompts, example output, skip-approval connection,
  discovered-command display, status refresh, and session recreation.

## Deferred Commands

Defer `/model`, `/byok`, `/mcp-role`, `/settings`, `/login`, and `/clear` until
the ordered extraction list above is complete.

## Per-PR Validation

For each extraction PR:

- Add focused failing tests before production changes.
- Run `dotnet build`.
- Run `dotnet test`.
- Run `dotnet run -- --server localhost --prompt "how is this computer doing?"`.
- Bump `TroubleScout.csproj` version fields and add a matching `CHANGELOG.md`
  release section before opening the PR.
