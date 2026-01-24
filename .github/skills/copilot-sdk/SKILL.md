---
name: copilot-sdk
description: Keep TroubleScout’s GitHub Copilot SDK integration current by verifying the installed Copilot CLI + GitHub.Copilot.SDK versions, updating when needed, and following the official usage guidance.
---

# Copilot SDK (TroubleScout)

Use this skill when you’re changing anything related to `GitHub.Copilot.SDK` or session/event streaming behavior.

## Always keep components current

This repo depends on two moving parts:

- **GitHub.Copilot.SDK (NuGet)**: the .NET client library used by TroubleScout.
- **GitHub Copilot CLI**: the local CLI the SDK talks to (server mode under the hood).

### Check SDK version (.NET)

- Check for outdated packages:
  - `dotnet list package --outdated`
- If `GitHub.Copilot.SDK` is behind:
  - Update to the latest version and confirm it still builds:
    - `dotnet add package GitHub.Copilot.SDK --version <latest>`
    - `dotnet build`

### Check Copilot CLI version

- Verify the CLI is installed and working:
  - `copilot --version`
- If it’s missing or outdated, follow the official installation/update guide (Windows supports WinGet):
  - https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli

Notes:
- Copilot CLI must be installed separately; the SDK communicates with it.
- If the CLI requires login, complete authentication before debugging SDK behavior.

## Official usage guidance (read these first)

- Copilot SDK repo: https://github.com/github/copilot-sdk
- Getting started guide: https://github.com/github/copilot-sdk/blob/main/docs/getting-started.md
- .NET reference/README: https://github.com/github/copilot-sdk/blob/main/dotnet/README.md
- .NET cookbook: https://github.com/github/copilot-sdk/tree/main/cookbook/dotnet

## Implementation expectations for TroubleScout

- Prefer the SDK’s **event subscription model** (subscribe via `session.On(...)` and treat `SessionIdle` as the completion signal).
- Avoid inventing APIs; validate patterns against the official .NET README.
- After any SDK-related change, run `dotnet build`.

## Troubleshooting checklist

- If sessions don’t start: verify `copilot --version` works and the CLI is authenticated.
- If events don’t stream: confirm session config enables streaming and handlers are subscribed before sending.
- If behavior differs after updates: re-check the official .NET README and the repo release notes, then make the smallest compatible change.
