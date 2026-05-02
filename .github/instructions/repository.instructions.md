---
applyTo: "**"
---

# TroubleScout Repository Instructions

These instructions are shared guidance for GitHub Copilot and other GitHub-aware
agents. Codex-specific agents should read `AGENTS.md` first, then use this file
as supporting context.

## Pull Requests

- Pull request titles must start with a gitmoji-style emoji, for example
  `🔐 Redact persisted session data`.
- Follow `.github/pull_request_template.md` when opening or updating PRs.
- When implementing reviewer feedback, follow
  `.github/prompts/ImplementReviewersSuggestions.prompt.md`.

## Validation

- After editing `.cs` files, run `dotnet build`, `dotnet test`, and the smoke
  test from `AGENTS.md`.
- Keep compiler and analyzer output warning-clean.

## Workflow Compatibility

- Preserve existing GitHub Copilot prompts, skills, and workflows under
  `.github/`; they remain part of the repository workflow.
- Keep Codex-facing guidance in `AGENTS.md`, and add links there when new
  `.github` workflows should be followed by Codex too.
