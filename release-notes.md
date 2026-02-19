# 🎉 TroubleScout v1.0.0 - Initial Release

AI-Powered Windows Server Troubleshooting Assistant using GitHub Copilot SDK

## ✨ Features

- Natural language troubleshooting with streaming AI responses
- Safe PowerShell command execution with approval system
- Interactive TUI with Spectre.Console
- Support for local and remote servers (WinRM)
- Comprehensive diagnostic tools (system, events, services, processes, network, storage)
- Model selection and switching (Claude, GPT)

## 📦 Installation

1. Download the architecture package you need (`TroubleScout-v1.0.0-win-x64.zip` or `TroubleScout-v1.0.0-win-arm64.zip`) below
2. Extract to a folder
3. Choose auth mode:
   - GitHub mode: run `copilot login`
   - BYOK mode: set `OPENAI_API_KEY` and run with `--byok-openai`
4. Run `TroubleScout.exe`

**No .NET SDK required** - self-contained runtime included!

## 📋 Requirements

- Windows x64 or Windows ARM64
- Node.js ([Download](https://nodejs.org/)) only for npm-based CLI installs
- GitHub Copilot subscription (GitHub mode) or OpenAI API key (BYOK mode)

## 📝 What's Inside

- `TroubleScout.exe` (54 MB) - Self-contained executable
- `runtimes/` folder (when present) - PowerShell runtime dependencies
