# Summary: Automated Release Publishing

## Problem Statement
The repository had GitHub Actions for building and testing, but no workflow to automatically publish releases after PR merge or tag push.

## Solution Implemented

### 1. Created Release Workflow (`.github/workflows/release.yml`)

The workflow automatically publishes releases when version tags are pushed to GitHub.

**Trigger**: Push of version tags matching `v*.*.*` (e.g., `v1.0.0`, `v1.2.3`)

**Process**:
1. Triggers on version tag push
2. Builds self-contained Windows x64 executable with .NET 10
3. Packages `TroubleScout.exe` + `runtimes/` folder into a single zip file
4. Extracts release notes from `release-notes.md`
5. Creates GitHub release with the packaged zip and release notes

**Key Features**:
- Uses `windows-latest` runner for Windows-specific build
- Builds self-contained executable (no .NET runtime required on target)
- Automatically extracts version from tag name
- Packages both executable and required runtimes folder together
- Uses existing `release-notes.md` for release description

### 2. Updated Project Configuration

**TroubleScout.csproj**:
- Added `<Version>1.0.0</Version>` property
- Added `<AssemblyVersion>1.0.0.0</AssemblyVersion>` property
- Added `<FileVersion>1.0.0.0</FileVersion>` property

These properties enable version tracking in the compiled executable.

### 3. Created Documentation

**RELEASE-PROCESS.md**: Comprehensive guide for creating releases, including:
- How the automated release publishing works
- Step-by-step instructions for creating a new release
- Troubleshooting guide for common issues
- Package contents explanation

### 4. Updated Existing Documentation

**README.md**:
- Added release workflow badge
- Added "Release Process" section linking to RELEASE-PROCESS.md

**CONTRIBUTING.md**:
- Added "Release Process" section explaining automation
- Clarified that only maintainers can create releases

## How to Use

To create a new release, maintainers follow these steps:

1. **Update version** in `TroubleScout.csproj`:
   ```xml
   <Version>1.0.1</Version>
   ```

2. **Update release notes** in `release-notes.md`

3. **Commit and push changes**:
   ```bash
   git commit -m "🔖 Prepare release v1.0.1"
   git push origin main
   ```

4. **Create and push version tag**:
   ```bash
   git tag -a v1.0.1 -m "Release v1.0.1"
   git push origin v1.0.1
   ```

5. **GitHub Actions automatically**:
   - Builds the executable
   - Packages the release
   - Creates the GitHub release

## Benefits

✅ **Automated**: No manual build or upload steps required
✅ **Consistent**: Every release follows the same process
✅ **Version Controlled**: Tags in git history track releases
✅ **Manual Control**: Only triggers on explicit tag push (not on every main commit)
✅ **Complete Package**: Single zip contains both executable and runtimes
✅ **Self-Documented**: Release notes automatically included

## Testing

The workflow has been validated for:
- ✅ YAML syntax correctness
- ✅ Proper indentation and formatting
- ✅ Correct use of GitHub Actions APIs
- ✅ PowerShell script logic

The workflow will be fully tested when the first tag is pushed to the repository.

## Files Changed

1. `.github/workflows/release.yml` (new) - Release automation workflow
2. `TroubleScout.csproj` (modified) - Added version properties
3. `RELEASE-PROCESS.md` (new) - Release process documentation
4. `README.md` (modified) - Added release badge and section
5. `CONTRIBUTING.md` (modified) - Added release process section

## Next Steps

When ready to create the first release:
1. Ensure `release-notes.md` is finalized for v1.0.0
2. Push the tag `v1.0.0` to trigger the workflow
3. Monitor the workflow execution in GitHub Actions
4. Verify the release appears at: `https://github.com/sasler/TroubleScout/releases`
