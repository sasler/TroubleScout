# Slash Command Reference

This page lists every slash command available inside an interactive
TroubleScout session. The short table in the README under
[Interactive Commands](../README.md#interactive-commands) is the marketing
view; this is the full reference.

> **Note**: this file is maintained manually today. Once the slash-command
> registry refactor lands (see plan), it will be regenerated from the
> registry to remove the chance of drift.

## Conventions

- `<arg>` — required argument.
- `[arg]` — optional argument.
- `a|b` — choose one literal value.
- Arguments separated by whitespace unless noted otherwise.
- Commands start with `/`; the command token is case-insensitive (the
  dispatcher lower-cases the first token before matching).

## Session

### `/help`

Show the in-app command reference.

### `/status`

Show connection, target servers, model, provider, reasoning effort,
execution mode, configured/used MCP servers and skills, persisted MCP
approvals, and per-session usage.

### `/clear`

Start a new Copilot session. Conversation history, MCP per-session
approvals, and URL approvals are reset; persistent settings remain.

### `/history`

Show PowerShell command history captured during this session.

### `/report`

Generate a self-contained HTML report of the current session and open it
in the default browser. The report mirrors the terminal status bar per
prompt and includes a session-wide summary.

### `/exit`, `/quit`, `exit`, `quit`

Leave the interactive session.

## Diagnostics

### `/server <server1>[ <server2> ...]`

Connect to one or more additional servers. Names may be separated by
whitespace **or** commas.

Examples:

```text
/server srv1
/server srv1 srv2
/server srv1,srv2
```

### `/jea [server] [configurationName]`

Connect to a JEA-constrained PowerShell remoting endpoint. With no
arguments TroubleScout prompts for the server and configuration name in
turn. Because the user invokes `/jea` explicitly, no second Safe-mode
approval is requested before connecting.

JEA executions never use `AddScript(...)`; commands are built with the
PowerShell command API so no-language endpoints stay valid.

### `/mode <safe|yolo>`

Set the PowerShell execution mode for the current session.

- `safe` (default): only commands matching the safe list (`Get-*`,
  `Select-*`, `Sort-*`, etc.) auto-execute. Mutations require approval.
- `yolo`: remediation commands can execute without confirmation. Use with
  care.

## Configuration

### `/model`

Choose another AI model and session handoff mode interactively.

### `/reasoning [auto|<effort>]`

Set the reasoning effort for the current model when supported. With no
argument, prompts interactively.

### `/settings`

Open `settings.json` in the default editor, then reload prompt and safety
configuration after the editor exits.

### `/login`

Run GitHub Copilot login inside TroubleScout.

### `/byok env|<api-key> [base-url] [model]`

Enable OpenAI-compatible BYOK mode without GitHub authentication.

- `env` reads `OPENAI_API_KEY` from the environment.
- `<api-key>` passes the key directly. The key is encrypted at rest with
  DPAPI (`ByokOpenAiApiKeyEncrypted` in `settings.json`).
- `base-url` overrides the OpenAI-compatible endpoint URL.
- `model` selects an initial BYOK model.

### `/byok clear`

Clear saved BYOK settings for this profile.

## MCP

### `/capabilities`

Show configured and runtime-used MCP servers and skills. Also visible in
`/status`.

### `/mcp-role monitoring|ticketing <server|none> | clear <monitoring|ticketing|all>`

Configure monitoring and ticketing MCP role mappings. With no arguments,
opens an interactive prompt to assign or clear roles. Role mappings
persist to `settings.json` and are surfaced in startup, `/status`, and the
HTML report.

Examples:

```text
/mcp-role monitoring zabbix
/mcp-role ticketing redmine
/mcp-role monitoring none
/mcp-role clear all
```

### `/mcp-approvals [list|clear all|clear <server>]`

List or clear active and persisted MCP approvals. MCP approvals are
**per server**, not per tool — approving any tool from a server
auto-approves every other tool from that server for the rest of the
session. Persisting an approval across sessions is only offered for
servers mapped to a `monitoring` or `ticketing` role.

Examples:

```text
/mcp-approvals list
/mcp-approvals clear zabbix
/mcp-approvals clear all
```

## Approval prompt actions (not slash commands)

When the AI requests a mutating PowerShell command, an MCP tool call, or
an outbound URL fetch, TroubleScout prompts inline. These prompts are
**not** invoked with `/`, but they share the slash-command surface and
deserve mention here.

- **Command approval** — Yes / No / Explain (Explain shows a detail panel
  and re-prompts Yes / No).
- **MCP approval** — Approve once / Approve this server for the session /
  Approve and persist (monitoring/ticketing only) / Deny.
- **URL approval** — Allow this URL / Allow all URLs for this session /
  Deny.
- **Post-analysis action** — Continue investigating / Apply the fix /
  Stop for now.

## Cancellation

Press <kbd>Esc</kbd> during an AI turn to cancel at the RPC layer. The
key is ignored while an approval prompt is open, so accidental presses
do not abort an approval dialog.
