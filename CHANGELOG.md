# Changelog

All notable changes to TroubleScout will be documented in this file.

## [v1.2.2] - 2026-02-06

### 🐛 Critical Bug Fixes
- 🐛 Fix PowerShell SDK initialization failure in published executables
  - Removed `PublishSingleFile=true` from build configuration
  - PowerShell SDK requires configuration files on disk that aren't compatible with single-file publishing
  - Application now ships as TroubleScout.exe with supporting DLLs in the same folder

### 📝 Technical Details
- Root cause: PowerShell SDK's `PSSnapInReader.ReadEnginePSSnapIns()` calls `Path.Combine` with null paths when configuration files are unavailable
- Single-file publishing embeds resources but PowerShell SDK needs physical files (PowerShell.Format.ps1xml, etc.)
- Solution: Distribute as standard published application with all required files

## [v1.2.1] - 2026-02-06

### 🐛 Bug Fixes
- 🐛 Fix null path exception in published executable when `ApplicationData` is unavailable
- 🐛 Add robust fallback chain for settings path: `ApplicationData` → `LocalApplicationData` → `CurrentDirectory`

### ✨ Improvements
- ✨ Use explicit `.Where()` filtering for cleaner, more readable code
- ✨ Make `GetCopilotCliPath` testable by changing visibility to `internal`

### ✅ Testing
- ✅ Add comprehensive test coverage for null ApplicationData scenarios
- ✅ Add 6 new tests validating fallback behavior and path resolution

## [v1.2.0] - 2026-02-05

### ✨ Features
- ✨ Add automated release PR workflow
- ✨ Improve error messages and consolidate documentation

### 🐛 Bug Fixes
- 🐛 Fix auto-release workflow non-fast-forward push errors
- 🐛 Fix TUI input redraw and line break issues

### 🔧 Other Changes
- 🔧 Update release workflow to follow best practices
- 📝 Add implementation summary
