---
name: microsoft-docs
description: Use official Microsoft documentation to answer questions for TroubleScout (.NET SDK/C#/.NET 10, PowerShell 7.x, Windows Server, WinRM, publishing and security guidance).
---

# Microsoft Docs (TroubleScout)

Use this skill when correctness depends on official Microsoft guidance (behavior, prerequisites, security constraints, configuration steps).

## Tools to use (Microsoft Docs MCP)

- Search: `mcp_microsoftdocs_microsoft_docs_search`
- Fetch full pages: `mcp_microsoftdocs_microsoft_docs_fetch`
- Code samples (when you need authoritative snippets): `mcp_microsoftdocs_microsoft_code_sample_search`

## When to use Microsoft docs

- .NET SDK/runtime behavior (especially publish/self-contained/single-file)
- PowerShell semantics and cmdlets
- WinRM / PowerShell remoting configuration
- Windows Server event logs, services, networking troubleshooting
- Security and least-privilege guidance for diagnostic tooling

## PowerShell documentation (important)

Use the PowerShell documentation hub for anything about cmdlets, language behavior, remoting, and troubleshooting:

- https://learn.microsoft.com/en-us/powershell/

Use the PowerShell hosting documentation when working on embedding/hosting PowerShell from C# (this repo uses the `Microsoft.PowerShell.SDK` NuGet package for hosting):

- https://learn.microsoft.com/en-us/powershell/scripting/developer/hosting/adding-and-invoking-commands

## Workflow

1. Frame the question with product + version + scenario.
   - Examples: 
     - ".NET 10 self-contained publish single file runtimes folder"
     - "PowerShell 7.5 runspace invoke pipeline error handling"
     - "WinRM enable remoting prerequisites Windows Server 2022"
2. Prefer Microsoft Learn / official docs.
3. If a page excerpt isn’t enough (prereqs/steps/caveats), fetch the full doc and follow its sequence.
4. Translate into concrete repo actions (code change, packaging step, CLI behavior), keeping safety constraints explicit.

## Output expectations

- Cite the authoritative source in your own words.
- Avoid guessing API names or flags; verify.
- If docs conflict with current repo behavior, call out the mismatch and propose the smallest safe change.
