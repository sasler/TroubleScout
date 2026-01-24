# Release Process

This document explains how TroubleScout releases are created and published.

## Automated Release Publishing

TroubleScout uses GitHub Actions to automatically build and publish releases when version tags are pushed to the repository.

### How It Works

1. **Tag Creation**: When a version tag (e.g., `v1.0.0`, `v1.2.3`) is pushed to GitHub, the `release.yml` workflow is triggered
2. **Build**: The workflow builds a self-contained Windows x64 executable with all dependencies
3. **Package**: Creates a single zip file containing `TroubleScout.exe` and the `runtimes/` folder
4. **Release**: Automatically creates a GitHub release with the packaged zip file and release notes

### Creating a New Release

To create a new release, follow these steps:

#### 1. Update Version Number

Edit `TroubleScout.csproj` and update the version numbers:

```xml
<Version>1.0.1</Version>
<AssemblyVersion>1.0.1.0</AssemblyVersion>
<FileVersion>1.0.1.0</FileVersion>
```

#### 2. Commit and Push Changes

```powershell
git add TroubleScout.csproj
git commit -m "🔖 Prepare release v1.0.1"
git push origin main
```

#### 3. Create and Push Version Tag with Release Notes

Create an annotated tag with detailed release notes in the tag message:

```powershell
git tag -a v1.0.1 -m "Release v1.0.1

### 🐛 Bug Fixes
- Fixed connection timeout issue
- Improved error handling

### ✨ Enhancements
- Added better logging

### 📋 Requirements
- Windows x64
- Node.js
- Active GitHub Copilot subscription"

git push origin v1.0.1
```

#### 4. Monitor Release Workflow

The GitHub Actions workflow will automatically:
- Build the self-contained executable
- Package TroubleScout.exe + runtimes/ folder into a zip
- Create a GitHub release with auto-generated release notes from commits
- Create a GitHub release at: `https://github.com/sasler/TroubleScout/releases`

You can monitor the progress at: `https://github.com/sasler/TroubleScout/actions`

### Release Package Contents

Each release includes a single zip file named `TroubleScout-{version}-win-x64.zip` containing:

- `TroubleScout.exe` - Self-contained executable (~54 MB)
- `runtimes/` - PowerShell SDK native dependencies

**Important**: Both files must be extracted together. The executable requires the `runtimes/` folder to function.

### Triggering Releases

Releases are **only** triggered by version tags matching the pattern `v*.*.*`. This ensures:

- Manual control over when releases are created
- Clear version tracking in git history
- Prevents accidental releases from every main branch commit

### Troubleshooting

**Workflow not triggering?**
- Verify the tag format matches `v*.*.*` (e.g., `v1.0.0`, not `1.0.0` or `version-1.0.0`)
- Check that you pushed the tag: `git push origin v1.0.0`

**Build fails?**
- Verify the project builds locally: `dotnet build -c Release`
- Check the workflow logs in GitHub Actions

**Release not appearing?**
- The workflow requires `contents: write` permission (already configured)
- Check GitHub Actions logs for errors

**Want to customize release notes?**
- After the workflow creates the release, you can edit it on GitHub to add custom notes
- Alternatively, include detailed information in your tag annotation message
