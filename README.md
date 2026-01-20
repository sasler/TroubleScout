# TroubleScout

**AI-Powered Windows Server Troubleshooting Assistant**

TroubleScout is a .NET CLI tool that uses the GitHub Copilot SDK to provide an AI-powered Windows Server troubleshooting assistant. Describe your issue in natural language, and TroubleScout will investigate using safe, read-only PowerShell commands.

## Features

- **Natural Language Troubleshooting**: Describe your issue, and the AI analyzes and diagnoses problems
- **Safe by Default**: Only `Get-*` commands execute automatically; remediation commands require explicit approval
- **Interactive TUI**: Rich terminal UI with streaming responses using Spectre.Console
- **Local or Remote**: Works with localhost or remote servers via WinRM
- **Comprehensive Diagnostics**: Analyzes event logs, services, processes, disk space, network, and performance counters
- **Session Persistence**: Maintains conversation context for follow-up questions

## Prerequisites

1. **.NET 10.0 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/10.0)
2. **Node.js** - Required for the Copilot CLI runtime
3. **GitHub Copilot SDK CLI** - The .NET SDK communicates with Copilot via a CLI subprocess:
   ```bash
   npm install -g @github/copilot-sdk
   ```
4. **GitHub Copilot Access** - Active GitHub Copilot subscription

> **Note**: The GitHub.Copilot.SDK NuGet package (already included in the project) provides the .NET API. The npm package provides the CLI runtime that the SDK spawns internally for JSON-RPC communication with the Copilot API.

## Installation

```bash
# Clone or download the project
cd TroubleScout

# Build the project
dotnet build

# Run the application
dotnet run
```

## Usage

### Interactive Mode (Default)

```bash
# Troubleshoot localhost
dotnet run

# Troubleshoot a remote server
dotnet run -- --server myserver.domain.com
```

### Headless Mode

```bash
# Single prompt execution for scripting
dotnet run -- --server localhost --prompt "Check why the SQL Server service is stopped"
```

### Command Line Options

| Option | Short | Description |
|--------|-------|-------------|
| `--server` | `-s` | Target server name or IP (default: localhost) |
| `--model` | `-m` | AI model to use (e.g., gpt-4o, claude-sonnet-4) |
| `--prompt` | `-p` | Initial prompt for headless mode |
| `--help` | `-h` | Show help information |

### Model Selection

You can specify which AI model to use with the `--model` option:

```bash
# Use a specific model
dotnet run -- --model gpt-4o

# Use Claude
dotnet run -- --model claude-sonnet-4
```

Available models depend on your GitHub Copilot subscription. If not specified, the default model for your account is used.

## Example Prompts

- "The server is running slow and users are complaining about login times"
- "Check why the SQL Server service keeps stopping"
- "Analyze disk space and find what's using the most storage"
- "Look for errors in the System event log from the past hour"
- "Why is CPU usage so high?"

## Diagnostic Categories

| Category | Description | Example Commands |
|----------|-------------|------------------|
| System | OS info, uptime, hardware specs | `Get-ComputerInfo`, `Get-CimInstance` |
| Events | Windows Event Log analysis | `Get-EventLog`, `Get-WinEvent` |
| Services | Windows service status | `Get-Service` |
| Processes | Running processes and resource usage | `Get-Process` |
| Performance | CPU, memory, disk metrics | `Get-Counter` |
| Network | Network adapters and configuration | `Get-NetAdapter`, `Get-NetIPAddress` |
| Storage | Disk space and volume health | `Get-Volume`, `Get-Disk` |

## Security Model

TroubleScout uses a permission-based security model:

### Automatic Execution (No Approval Required)
- All `Get-*` commands (read-only)
- Multi-line scripts containing only safe read operations
- Commands like `Format-*`, `Select-*`, `Where-*`, `Sort-*`

### Requires User Approval
- `Set-*`, `Start-*`, `Stop-*`, `Restart-*`
- `Remove-*`, `New-*`, `Add-*`, `Enable-*`, `Disable-*`
- Any command that can modify system state

### Blocked Commands
- `Get-Credential` (sensitive credential handling)
- `Get-Secret` (secret management)

## Interactive Commands

| Command | Description |
|---------|-------------|
| `/exit` or `/quit` | End the session |
| `/clear` | Clear the screen |
| `/status` | Show connection status |

## Architecture

```
┌─────────────────────────────────────────────────┐
│                 TroubleScout CLI                │
├─────────────────────────────────────────────────┤
│  ┌───────────────┐  ┌─────────────────────────┐ │
│  │  Spectre.     │  │    GitHub Copilot SDK   │ │
│  │  Console TUI  │  │    (JSON-RPC Client)    │ │
│  └───────────────┘  └─────────────────────────┘ │
│           │                     │               │
│           ▼                     ▼               │
│  ┌───────────────────────────────────────────┐  │
│  │         TroubleshootingSession            │  │
│  │  - Session management                     │  │
│  │  - Event handling (streaming)             │  │
│  │  - Tool execution                         │  │
│  └───────────────────────────────────────────┘  │
│           │                     │               │
│           ▼                     ▼               │
│  ┌───────────────┐  ┌─────────────────────────┐ │
│  │  PowerShell   │  │    DiagnosticTools      │ │
│  │  Executor     │  │    (AI Functions)       │ │
│  │  (Local/WinRM)│  │                         │ │
│  └───────────────┘  └─────────────────────────┘ │
└─────────────────────────────────────────────────┘
            │
            ▼
┌─────────────────────────────────────────────────┐
│           Copilot CLI (SDK Server)              │
│           copilot --server --stdio              │
└─────────────────────────────────────────────────┘
            │
            ▼
┌─────────────────────────────────────────────────┐
│           GitHub Copilot API                    │
│           (AI Language Model)                   │
└─────────────────────────────────────────────────┘
```

## Environment Variables

| Variable | Description |
|----------|-------------|
| `COPILOT_CLI_PATH` | Custom path to the Copilot CLI executable |

## Remote Server Requirements (WinRM)

When troubleshooting remote servers:

1. **WinRM must be enabled** on the target server:
   ```powershell
   Enable-PSRemoting -Force
   ```

2. **Windows Integrated Authentication** is used (Kerberos/NTLM)

3. **Network connectivity** on port 5985 (HTTP) or 5986 (HTTPS)

## Troubleshooting

### "Copilot CLI Not Found"
Ensure the `@github/copilot-sdk` npm package is installed globally:
```bash
npm install -g @github/copilot-sdk
```

### "JSON-RPC connection lost"
- Ensure Node.js is installed and in PATH
- Check that you have an active GitHub Copilot subscription
- Verify authentication with `copilot` interactive mode

### "Connection Failed" (Remote Server)
- Verify WinRM is enabled on the target server
- Check firewall allows port 5985/5986
- Ensure your account has admin privileges on the target

## License

MIT

## Contributing

Contributions are welcome! Please submit pull requests or open issues for bugs and feature requests.
