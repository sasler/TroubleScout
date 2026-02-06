# Changelog

All notable changes to TroubleScout will be documented in this file.

## [v1.2.3] - 2026-02-06

### 🐛 Critical Bug Fixes
- 🐛 Fix PowerShell SDK initialization in single-file published executables
  - Added `IncludeNativeLibrariesForSelfExtract` and `IncludeAllContentForSelfExtract` properties to enable proper resource extraction
  - Re-enabled `PublishSingleFile=true` for clean distribution (exe + runtimes folder only)
  - PowerShell SDK now extracts required configuration files to temp directory at runtime

### ✅ Testing
- ✅ Fixed test isolation issues in `AppSettingsStoreTests` with sequential execution
- ✅ All 74 tests passing with improved file handle cleanup
- ✅ Added GC collection to prevent file locking issues between tests

### 📝 Technical Details
- Root cause: PowerShell SDK requires physical configuration files, but single-file mode embeds them
- Solution: Use .NET's extraction properties to automatically extract embedded resources at runtime
- Package now distributes as clean single-file exe (125 MB) with runtimes folder, matching v1.0.x structure

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
