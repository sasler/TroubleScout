# TroubleScout

[![Build and Test](https://github.com/sasler/TroubleScout/actions/workflows/build.yml/badge.svg)](https://github.com/sasler/TroubleScout/actions/workflows/build.yml)
[![Tests](https://github.com/sasler/TroubleScout/actions/workflows/tests.yml/badge.svg)](https://github.com/sasler/TroubleScout/actions/workflows/tests.yml)
[![Branch Protection](https://github.com/sasler/TroubleScout/actions/workflows/branch-protection.yml/badge.svg)](https://github.com/sasler/TroubleScout/actions/workflows/branch-protection.yml)
[![Release](https://github.com/sasler/TroubleScout/actions/workflows/release.yml/badge.svg)](https://github.com/sasler/TroubleScout/actions/workflows/release.yml)

## AI-Powered Windows Server Troubleshooting Assistant

TroubleScout is a .NET CLI tool that uses the GitHub Copilot SDK to provide an AI-powered Windows Server troubleshooting assistant. Describe your issue in natural language, and TroubleScout will investigate using safe, read-only PowerShell commands.

## Features

- **Natural Language Troubleshooting**: Describe your issue, and the AI analyzes and diagnoses problems
- **Safe by Default**: Only `Get-*` commands execute automatically; remediation commands require explicit approval
- **Interactive TUI**: Rich terminal UI with streaming responses using Spectre.Console
- **Local or Remote**: Works with localhost or remote servers via WinRM
- **Comprehensive Diagnostics**: Analyzes event logs, services, processes, disk space, network, and performance counters
- **Session Persistence**: Maintains conversation context for follow-up questions

## Prerequisites

**For pre-built release:**

1. **Windows x64** operating system
2. **Node.js 24+ (LTS recommended)** - Required for the Copilot CLI runtime - [Download](https://nodejs.org/)
3. **GitHub Copilot SDK CLI** - Install via npm:

   ```bash
   npm install -g @github/copilot-sdk
   ```

4. **GitHub Copilot Access** - Active GitHub Copilot subscription

**For building from source:**

1. **.NET 10.0 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/10.0)
2. All prerequisites listed above

> **Note**: The pre-built release includes a self-contained .NET runtime, so you don't need to install .NET SDK unless you're building from source.

## Installation

### Option 1: Download Pre-built Release (Recommended)

1. **Download the latest release** from [Releases](https://github.com/sasler/TroubleScout/releases)
2. **Extract** `TroubleScout.exe` and the `runtimes/` folder to a directory
3. **Install prerequisites**:
   - [Node.js](https://nodejs.org/) (for Copilot CLI runtime)
   - Install GitHub Copilot CLI: `npm install -g @github/copilot-sdk`
4. **Run** `TroubleScout.exe` from the command line

> **Note**: The release includes a self-contained .NET runtime - no .NET SDK installation required!

### Option 2: Build from Source

```bash
# Clone the repository
git clone https://github.com/sasler/TroubleScout.git
cd TroubleScout

# Build the project
dotnet build

# Run the application
dotnet run
```

**Build a self-contained executable:**

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# Output: bin\Release\net10.0\win-x64\publish\TroubleScout.exe
```

## Usage

### Interactive Mode (Default)

```bash
# Using pre-built release
TroubleScout.exe

# Using source build
dotnet run

# Troubleshoot a remote server
TroubleScout.exe --server myserver.domain.com
# or
dotnet run -- --server myserver.domain.com
```

### Headless Mode

```bash
# Single prompt execution for scripting (pre-built release)
TroubleScout.exe --server localhost --prompt "Check why the SQL Server service is stopped"

# Using source build
dotnet run -- --server localhost --prompt "Check why the SQL Server service is stopped"
```

### Command Line Options

| Option     | Short | Description                                      |
| ---------- | ----- | ------------------------------------------------ |
| `--server` | `-s`  | Target server name or IP (default: localhost)    |
| `--model`  | `-m`  | AI model to use (e.g., gpt-4o, claude-sonnet-4)  |
| `--prompt` | `-p`  | Initial prompt for headless mode                 |
| `--version`| `-v`  | Show app version and exit                        |
| `--help`   | `-h`  | Show help information                            |

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

| Category    | Description                          | Example Commands                          |
| ----------- | ------------------------------------ | ----------------------------------------- |
| System      | OS info, uptime, hardware specs      | `Get-ComputerInfo`, `Get-CimInstance`     |
| Events      | Windows Event Log analysis           | `Get-EventLog`, `Get-WinEvent`            |
| Services    | Windows service status               | `Get-Service`                             |
| Processes   | Running processes and resource usage | `Get-Process`                             |
| Performance | CPU, memory, disk metrics            | `Get-Counter`                             |
| Network     | Network adapters and configuration   | `Get-NetAdapter`, `Get-NetIPAddress`      |
| Storage     | Disk space and volume health         | `Get-Volume`, `Get-Disk`                  |

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

| Command            | Description            |
| ------------------ | ---------------------- |
| `/exit` or `/quit` | End the session        |
| `/clear`           | Clear the screen       |
| `/status`          | Show connection status |

## Architecture

```text
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

| Variable           | Description                               |
| ------------------ | ----------------------------------------- |
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

If you still see startup failures, install/update both CLI packages and re-authenticate:

```bash
npm install -g @github/copilot@latest @github/copilot-sdk@latest
copilot login
```

References:

- [Copilot CLI](https://github.com/github/copilot-cli)
- [Copilot SDK](https://github.com/github/copilot-sdk)

### "Protocol version mismatch" or SDK/CLI compatibility errors

TroubleScout requires Node.js 24+ for current Copilot SDK/CLI builds.

- On Windows, install/update Node LTS:

   ```powershell
   winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements
   ```

- Restart the terminal, then update Copilot SDK package:

   ```bash
   npm install -g @github/copilot-sdk@0.1.23
   ```

- Update Copilot CLI packages:

   ```bash
   npm install -g @github/copilot@latest @github/copilot-sdk@latest
   ```

- Re-authenticate:

   ```bash
   copilot login
   ```

- If you run TroubleScout from source, update the SDK package:

   ```bash
   dotnet add package GitHub.Copilot.SDK --version <latest>
   dotnet build
   ```

### "JSON-RPC connection lost"

- Ensure Node.js 24+ is installed and in PATH
- Check that you have an active GitHub Copilot subscription
- Verify authentication with `copilot` interactive mode

### "Connection Failed" (Remote Server)

- Verify WinRM is enabled on the target server
- Check firewall allows port 5985/5986
- Ensure your account has admin privileges on the target

## License

MIT

## Contributing

Contributions are welcome! This repository has branch protection enabled on the `main` branch to maintain code quality.

**Before contributing:**

- Read [CONTRIBUTING.md](CONTRIBUTING.md) for detailed guidelines
- Follow branch naming conventions: `feature/`, `fix/`, `docs/`, etc.
- Use emoji-prefixed or conventional commit messages
- Ensure all tests pass locally before opening a PR

**Quick Start:**

1. Fork the repository
2. Create a branch: `git checkout -b feature/your-feature`
3. Make changes and commit: `git commit -m "✨ Add your feature"`
4. Push and open a pull request

All pull requests require:

- ✅ Passing CI/CD checks (build and tests)
- ✅ Code owner review and approval
- ✅ Branch up-to-date with main

## Release Process

Releases are automatically published via GitHub Actions when version tags are pushed. See [RELEASE-PROCESS.md](RELEASE-PROCESS.md) for detailed instructions on creating new releases.

See [CONTRIBUTING.md](CONTRIBUTING.md) for complete guidelines.
