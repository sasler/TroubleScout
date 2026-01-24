---
name: microsoft-code-reference
description: Validate APIs and patterns for TroubleScout by checking official Microsoft references and code samples (.NET SDK/C#/.NET 10, and PowerShell automation via the Microsoft.PowerShell.SDK .NET package) before implementing.
---

# Microsoft Code Reference (TroubleScout)

Use this skill when you’re about to write or change code that depends on a specific API surface, and you want to avoid incorrect signatures, outdated patterns, or version mismatches.

## Target areas in this repo

- .NET SDK / .NET 10 language/runtime behavior and publish options
- PowerShell automation patterns via the `Microsoft.PowerShell.SDK` .NET package (runspaces, pipelines, streams)
- Windows/PowerShell remoting and configuration nuances

## PowerShell references (important)

- PowerShell documentation hub (cmdlets, language behavior, remoting): https://learn.microsoft.com/en-us/powershell/
- Hosting PowerShell from C# (adding/invoking commands): https://learn.microsoft.com/en-us/powershell/scripting/developer/hosting/adding-and-invoking-commands

## Tools to use (Microsoft Docs MCP)

- API/concept search: `mcp_microsoftdocs_microsoft_docs_search`
- Full reference pages: `mcp_microsoftdocs_microsoft_docs_fetch`
- Official code snippets: `mcp_microsoftdocs_microsoft_code_sample_search`

## Workflow

1. Identify what must be correct:
   - Type name, method name, overload, parameter names, expected behavior.
2. Verify against official references and/or official code samples.
3. Prefer samples that match the language/runtime used here.
4. Implement the smallest correct change.
5. Validate by building (`dotnet build`).

## Guardrails

- Do not invent APIs.
- If references and samples disagree, treat it as a version mismatch and explicitly resolve which version applies.
- When there’s ambiguity, bias toward compilation-verified patterns over assumptions.

## Useful query prompts

- ".NET 10 publish self-contained single file runtimes folder"
- "Microsoft.PowerShell.SDK runspace pipeline invoke async"
- "PowerShell error stream handling in C#"
- "PowerShell hosting adding and invoking commands"
