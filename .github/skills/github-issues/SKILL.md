---
name: github-issues
description: Create and update TroubleScout GitHub issues with clear repro steps, environment details (Windows/PowerShell/.NET), and actionable acceptance criteria.
---

# GitHub Issues (TroubleScout)

Use this skill when you need to file a new issue, refine an existing issue, or keep issue content consistently actionable.

## Tools to use (GitHub MCP)

Prefer GitHub MCP tools for issue/PR operations instead of guessing state from local git:

- Issues: `mcp_github_github_search_issues`, `mcp_github_github_issue_read`, `mcp_github_github_issue_write`
- PRs (when relevant): `mcp_github_github_create_pull_request`, `mcp_github_github_update_pull_request`, `mcp_github_github_pull_request_read`

### PR review comments (line-specific)

When leaving review comments on a PR, use the pending-review workflow:

1. Create a pending review: `mcp_github_github_pull_request_review_write` with `method=create` (no `event`).
2. Add line comments: `mcp_github_github_add_comment_to_pending_review`.
3. Submit the review: `mcp_github_github_pull_request_review_write` with `method=submit_pending` and an `event`.

## Principles

- Prefer actionable detail over long narratives.
- Always include environment details for anything execution-related.
- Keep titles short, specific, and searchable.

## Before creating a new issue

- Search for duplicates (same error text, same feature/tool name, same symptom).
- If a template exists, follow it.

## Recommended title patterns

- Bug: `Bug: <symptom> when <condition>`
- Feature: `Feature: <capability> for <scenario>`
- Task: `Task: <deliverable>`

Examples:
- Bug: Crash when parsing empty event log output
- Feature: Add command approval prompt for Restart-* scripts
- Task: Document publish + zip release procedure

## Bug report checklist

Include:

- Observed behavior
- Expected behavior
- Steps to reproduce
- Error output / stack trace (redacted)
- Logs (redacted and trimmed)
- Environment:
  - Windows edition/version (e.g., Windows Server 2022)
  - PowerShell version (`$PSVersionTable`)
  - .NET runtime (`dotnet --info` if applicable)
  - TroubleScout version / commit
  - Local vs remote (WinRM) execution

## Feature request checklist

Include:

- Problem statement / why it matters
- Proposed CLI UX (flags, prompts, defaults)
- Safety/security considerations (especially for PowerShell execution)
- Acceptance criteria (clear “done” conditions)

## Updating existing issues

- Preserve intent: add info without rewriting history.
- Prefer adding missing reproduction steps and environment details.
- If closing, record the reason (fixed by PR, cannot reproduce, out of scope).
