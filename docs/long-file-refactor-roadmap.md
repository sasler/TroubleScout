# Long-File Refactor Roadmap

This checklist tracks the ordered long-file refactors after the slash-command
dispatcher roadmap completed. Use the step labels only as implementation order;
they are not GitHub pull request numbers.

## Roadmap Steps

- [x] Step A: Prompt Templates - Move long AI-facing prompts into embedded
  Markdown template files, keep short UI copy in code, preserve system prompt
  overrides, and add focused rendering/template tests.
- [x] Step B: Session Permission Handler - Extract URL approvals, MCP approvals,
  Safe/Yolo permission decisions, and `/mcp-approvals` state operations from
  `TroubleshootingSession`.
- [x] Step C: Remaining Low-Risk Slash Commands - Move `/clear`, `/settings`,
  `/login`, and `/mcp-role` out of `RunInteractiveLoopAsync`.
- [ ] Step D: Model And BYOK Slash Commands - Move `/model` and `/byok` command
  flows into dedicated command handling.
- [ ] Step E: MCP, Skills, And Session Config Services - Extract MCP config
  loading, skill discovery, and `SessionConfig` construction into services.
- [ ] Step F: Copilot Turn Runner - Extract `SendMessageAsync` streaming/event
  orchestration while keeping the public entry point stable.
- [ ] Step G: Console UI Split - Split terminal lifetime/progress, input
  editing/history, status bar rendering, approval prompts, and thinking
  indicator helpers behind the existing `ConsoleUI` facade.
- [ ] Step H: Secondary Large-File Cleanup - Split `ReportHtmlBuilder` and
  `DiagnosticTools` into focused helpers without broad rewrites.

## Per-Step Validation

For each implementation step:

- Add focused failing tests before production changes.
- Run `dotnet build`.
- Run `dotnet test`.
- Run `dotnet run -- --server localhost --prompt "how is this computer doing?"`.
- Bump `TroubleScout.csproj` version fields and add a matching `CHANGELOG.md`
  release section before opening the PR.
- Update this roadmap checklist before committing the completed step.
- Commit with an emoji-prefixed subject.
