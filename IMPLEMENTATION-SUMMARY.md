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
4. Creates GitHub release with auto-generated release notes from commits
5. Uploads the packaged zip as a release asset

**Key Features**:
- Uses `windows-latest` runner for Windows-specific build
- Builds self-contained executable (no .NET runtime required on target)
- Automatically extracts version from tag name
- Packages both executable and required runtimes folder together
- Generates release notes automatically from commit history
- Validates that runtimes folder exists before packaging

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

2. **Commit and push changes**:
   ```bash
   git commit -m "🔖 Prepare release v1.0.1"
   git push origin main
   ```

3. **Create and push version tag with release notes**:
   ```bash
   git tag -a v1.0.1 -m "Release v1.0.1

   ### Bug Fixes
   - Fixed connection timeout
   - Improved error handling"
   
   git push origin v1.0.1
   ```

4. **GitHub Actions automatically**:
   - Builds the executable
   - Packages the release
   - Creates the GitHub release with auto-generated notes

## Benefits

✅ **Automated**: No manual build or upload steps required
✅ **Consistent**: Every release follows the same process
✅ **Version Controlled**: Tags in git history track releases
✅ **Manual Control**: Only triggers on explicit tag push (not on every main commit)
✅ **Complete Package**: Single zip contains both executable and runtimes
✅ **Auto-Generated Notes**: Release notes generated from commit history
✅ **Error Handling**: Validates runtimes folder exists before packaging

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
1. Update version in `TroubleScout.csproj` to the desired version
2. Commit and push the version update
3. Create an annotated tag with release notes: `git tag -a v1.0.0 -m "Release notes here"`
4. Push the tag `v1.0.0` to trigger the workflow
5. Monitor the workflow execution in GitHub Actions
6. Verify the release appears at: `https://github.com/sasler/TroubleScout/releases`
7. (Optional) Edit the release on GitHub to customize the description
