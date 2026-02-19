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

1. **Windows x64 or Windows ARM64** operating system
2. **Authentication mode**:
   - **GitHub mode (default):** Active GitHub Copilot subscription and authenticated Copilot CLI
   - **BYOK mode:** OpenAI API key (`OPENAI_API_KEY`) with `--byok-openai`
3. **GitHub Copilot CLI** - Bundled with TroubleScout releases; install separately only if you run from source without bundled assets:

   [Install Copilot CLI](https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli)

4. **On Windows:** PowerShell 6+ is required by Copilot CLI docs (PowerShell 7+ recommended)

5. **Node.js 24+ (LTS recommended)** is only needed when using npm-based Copilot CLI installs - [Download](https://nodejs.org/)

> **Important**: TroubleScout release packages include architecture-specific Copilot CLI assets for bundled use.

**For building from source:**

1. **.NET 10.0 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/10.0)
2. All prerequisites listed above

> **Note**: The pre-built release includes a self-contained .NET runtime, so you don't need to install .NET SDK unless you're building from source.

## Installation

### Option 1: Download Pre-built Release (Recommended)

1. **Download the latest release** from [Releases](https://github.com/sasler/TroubleScout/releases)
2. **Extract** `TroubleScout.exe` (and `runtimes/` if present) to a directory
3. **Choose auth mode**:
   - GitHub mode: run `copilot login` once
   - BYOK mode: set `OPENAI_API_KEY` and pass `--byok-openai`
4. **Install prerequisites**:
   - Ensure PowerShell 6+ (PowerShell 7+ recommended)
   - Install/update GitHub Copilot CLI only if you are not using bundled release assets: [Install Copilot CLI](https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli)
   - Install [Node.js](https://nodejs.org/) only if you choose npm-based Copilot CLI install
5. **Run** `TroubleScout.exe` from the command line

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

dotnet publish -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true
# Output: bin\Release\net10.0\win-arm64\publish\TroubleScout.exe
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

- `--server` (`-s`): Target server name or IP (default: localhost)
- `--model` (`-m`): AI model to use (e.g., gpt-4o, claude-sonnet-4)
- `--prompt` (`-p`): Initial prompt for headless mode
- `--mcp-config`: MCP config JSON path (default: `%USERPROFILE%\\.copilot\\mcp-config.json`)
- `--skills-dir`: Skills root directory (repeatable, default: `%USERPROFILE%\\.copilot\\skills` when present)
- `--disable-skill`: Disable a loaded skill by name (repeatable)
- `--debug` (`-d` or `-debug`): Show technical diagnostics and exception details
- `--byok-openai`: Use BYOK mode with OpenAI provider instead of GitHub auth
- `--openai-base-url`: Override OpenAI base URL (default: `https://api.openai.com/v1`)
- `--openai-api-key`: Provide OpenAI API key directly (or use `OPENAI_API_KEY`)
- `--version` (`-v`): Show app version and exit
- `--help` (`-h`): Show help information

### Model Selection

You can specify which AI model to use with the `--model` option:

```bash
# Use a specific model
dotnet run -- --model gpt-5.3-codex

# Use Claude
dotnet run -- --model claude-sonnet-4.6
```

Available models depend on auth mode:

- GitHub mode: models available to your Copilot account/subscription
- BYOK mode: models available from your configured OpenAI provider

### MCP Servers and Skills

TroubleScout can load MCP servers and skills through Copilot SDK session configuration.

- By default, MCP server config is read from `%USERPROFILE%\\.copilot\\mcp-config.json`
- By default, skills are loaded from `%USERPROFILE%\\.copilot\\skills` if that directory exists
- Use `/status` or `/capabilities` to see configured MCP servers/skills and runtime-used MCP servers/skills.

Examples:

```bash
# Use default MCP config (%USERPROFILE%\\.copilot\\mcp-config.json) and skills (%USERPROFILE%\\.copilot\\skills, if present)
dotnet run -- --server localhost

# Use a custom MCP config path
dotnet run -- --mcp-config C:\\path\\to\\mcp-config.json

# Add additional skill directory and disable a skill
dotnet run -- --skills-dir C:\\skills --disable-skill experimental-feature
```

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

| Command              | Description                                 |
| -------------------- | ------------------------------------------- |
| `/exit` or `/quit`   | End the session                             |
| `/clear`             | Clear the screen                            |
| `/status`            | Show connection status                      |
| `/login`             | Run Copilot login from the app              |

Use `/byok env <base-url> [model]` (or `/byok <api-key> <base-url> [model]`) to enable OpenAI-compatible BYOK. TroubleScout fetches available models from that endpoint and uses the same model picker as `/model`.

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

Install or update GitHub Copilot CLI using the official setup guide:

[Install Copilot CLI](https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli)

If you still see startup failures, verify the CLI and re-authenticate:

```bash
copilot --version
copilot login
```

References:

- [Copilot CLI](https://github.com/github/copilot-cli)
- [Copilot CLI install guide](https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli)

### "Protocol version mismatch" or SDK/CLI compatibility errors

TroubleScout requires Node.js 24+ for current Copilot SDK/CLI builds.

- On Windows, install/update Node LTS:

   ```powershell
   winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements
   ```

- Restart the terminal.

- Install or update Copilot CLI:

   [Install Copilot CLI](https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli)

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
