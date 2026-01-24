---
name: git-commit
description: Create clean, reviewable commits for TroubleScout by staging logically, writing emoji-prefixed messages, and following repo safety rules (no secrets, no force-push, no bypassing hooks).
---

# Git Commit (TroubleScout)

Use this skill when you need to turn working changes into one or more high-signal commits.

## Inputs to gather

- What was the intent of the change? (bug fix vs behavior change vs docs vs build/packaging)
- Which areas are touched? (CLI entry, Copilot session behavior, PowerShell execution/safety, UI output, tools)
- Is anything sensitive present in diffs? (tokens, hostnames, IPs, event log contents, stack traces with secrets)

## Workflow

1. Confirm you are not on `main`.
2. Review the working tree (`git status -sb`) and inspect the diff (`git diff`).
3. Split unrelated changes into separate commits.
4. Stage intentionally:
   - Prefer `git add -p` for mixed files.
   - Avoid staging generated build output (`bin/`, `obj/`) unless explicitly requested.
5. If `.cs` files changed, run `dotnet build` before committing.
6. Write an emoji-prefixed commit subject line:
   - Format: `<emoji> <summary>`
   - Keep it short and specific; use imperative mood.
7. Commit without bypassing hooks (do not use `--no-verify`).

## Emoji prefixes

Commit subjects must start with an emoji. Use any emoji; these are common conventions:

- ✨ New behavior / feature
- 🐛 Bug fix
- ♻️ Refactor
- 📝 Docs
- 🔧 Build/config
- ✅ Tests
- 🔖 Release/tagging

## Message examples

- ✨ Add WinRM connectivity check
- 🐛 Fix null handling in Copilot delta events
- 🔧 Update publish defaults for win-x64
- 📝 Clarify release packaging instructions

## Safety checks

- Do not commit secrets (tokens, credentials), personal data, or sensitive server data.
- If logs are required, redact/trim to the minimal necessary context.
- Never force-push. Never rewrite published history.
