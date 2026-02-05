# Release Process

This document explains how TroubleScout releases are created and published.

## Automated Release System

TroubleScout uses a simple automated release system powered by GitHub Actions. When you update the version in `TroubleScout.csproj` and push to main, a release tag is automatically created, which triggers the build and publish workflow.

### How It Works

The release process has two main stages:

1. **Version Update to Main**: When `TroubleScout.csproj` is updated on `main`, the `auto-release.yml` workflow triggers
2. **Automatic Tag Creation**: The workflow:
   - Reads the version from `TroubleScout.csproj`
   - Checks if a tag exists for that version
   - If no tag exists, creates and pushes the tag (e.g., `v1.2.0`)
   - Tag creation triggers the `release.yml` workflow
3. **Release Publishing**: When the tag is pushed:
   - The `release.yml` workflow builds the self-contained executable
   - A GitHub release is published with the packaged zip file

### Normal Release Flow (Simple!)

1. **Update version in TroubleScout.csproj**:
   ```xml
   <Version>1.3.0</Version>
   <AssemblyVersion>1.3.0.0</AssemblyVersion>
   <FileVersion>1.3.0.0</FileVersion>
   ```

2. **Commit and push to main**:
   ```bash
   git add TroubleScout.csproj
   git commit -m "🔖 Bump version to 1.3.0"
   git push origin main
   ```

3. **Automatic tag creation**:
   - Within 1-2 minutes, `auto-release.yml` workflow runs
   - Creates tag `v1.3.0` automatically
   - Tag is pushed to the repository

4. **Release publishes automatically**:
   - `release.yml` workflow runs
   - Builds and packages the release
   - GitHub release is published at `https://github.com/sasler/TroubleScout/releases`

### Manual Release Override

In rare cases where you need to create a release manually (e.g., automation failure), you can create the tag yourself:

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
git commit -m "🔖 Release v1.0.1"
git push origin main
```

#### 3. Create and Push Version Tag Manually (if automation fails)

```powershell
git tag -a v1.0.1 -m "Release v1.0.1"
git push origin v1.0.1
```

#### 4. Monitor Release Workflow

The GitHub Actions workflow will automatically:
- Build the self-contained executable
- Package TroubleScout.exe + runtimes/ folder into a zip
- Create a GitHub release with the packaged zip file

You can monitor the progress at: `https://github.com/sasler/TroubleScout/actions`

### Release Package Contents

Each release includes a single zip file named `TroubleScout-{version}-win-x64.zip` containing:

- `TroubleScout.exe` - Self-contained executable (~54 MB)
- `runtimes/` - PowerShell SDK native dependencies

**Important**: Both files must be extracted together. The executable requires the `runtimes/` folder to function.

### Version Numbering

TroubleScout follows semantic versioning (MAJOR.MINOR.PATCH):

- **MAJOR**: Breaking changes or significant rewrites
- **MINOR**: New features, backwards-compatible
- **PATCH**: Bug fixes, performance improvements, backwards-compatible

When updating the version in `TroubleScout.csproj`, choose the appropriate bump based on your changes.

### Troubleshooting

**No tag created after version update?**
- Verify the change to `TroubleScout.csproj` was pushed to main
- Check if `auto-release.yml` workflow ran: Check Actions tab
- Look for errors in the workflow logs
- Verify the tag doesn't already exist: `git tag -l`

**Tag already exists for version?**
- The workflow will skip tag creation if it already exists
- To create a new release, bump the version number in `TroubleScout.csproj`
- Commit and push the updated version

**Release workflow fails after tag creation?**
- Verify `release.yml` workflow ran: Check Actions tab
- Review build errors in workflow logs
- If build fails, fix the issue and manually re-run the workflow from the Actions tab

**Need to create a hotfix release?**
- Update version in `TroubleScout.csproj` (e.g., 1.2.0 → 1.2.1)
- Commit and push to main
- Tag and release will be created automatically
