# Target Server Awareness Changes

## Summary
TroubleScout now always operates on the correct target server (local or remote) and displays clear indicators about where commands are executing.

## Changes Made

### 1. System Message Update ([TroubleshootingSession.cs](TroubleshootingSession.cs))
- Changed from static to instance-based system message that includes target server context
- AI is now explicitly told which server it's connected to
- Instructions added to always verify `$env:COMPUTERNAME` matches the target
- AI must state which server data comes from in its responses

### 2. Target Server Context ([DiagnosticTools.cs](Tools/DiagnosticTools.cs))
- Constructor now accepts `targetServer` parameter
- All PowerShell commands are wrapped with target verification
- Each command prepends `Write-Host "[Target: $env:COMPUTERNAME]"` to output
- This ensures every response shows which computer the data came from

### 3. Command Execution Display ([ConsoleUI.cs](UI/ConsoleUI.cs))
- New `ShowCommandExecution()` method displays commands before execution
- Shows the command being run and the target server
- Uses color coding: green for localhost, yellow for remote servers
- Format: `> Running on [SERVER]: [COMMAND]`

### 4. Consistent Behavior
- All diagnostic tool methods now:
  - Wrap commands with target verification
  - Display what command is executing
  - Show which server the command runs on
- Works for both predefined tools (Get-SystemInfo, Get-EventLogs, etc.) and custom PowerShell commands

## Example Output

When running commands, users will now see:

```
> Running on REMOTE-SERVER: Get-SystemInfo
[Target: REMOTE-SERVER]
ComputerName    : REMOTE-SERVER
OSName          : Microsoft Windows Server 2022
...
```

## Benefits

1. **No Ambiguity**: Always clear which server is being diagnosed
2. **Verification**: `$env:COMPUTERNAME` check ensures data comes from the intended target
3. **Transparency**: Users see exactly what commands are executing
4. **AI Context**: The AI model is explicitly aware of the target and includes it in analysis
5. **Remote-First**: When connected to a remote server, all operations default to that server
