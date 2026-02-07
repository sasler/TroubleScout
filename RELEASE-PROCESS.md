# Release Process

This document explains how TroubleScout releases are created and published.

## Tag-based Release Process

TroubleScout uses a **tag-based** release process: the `release.yml` workflow is triggered when an annotated tag like `v1.3.0` is pushed to the repository. We intentionally avoid automatic tag creation so that releases are explicit and reviewers can verify changelogs and release notes before publishing.

### Normal Release Flow (Tag-based)

1. **Update version in `TroubleScout.csproj`**:

   ```xml
   <Version>1.3.0</Version>
   <AssemblyVersion>1.3.0.0</AssemblyVersion>
   <FileVersion>1.3.0.0</FileVersion>
   ```

2. **Update the CHANGELOG**:
   - Add a new section for `v1.3.0` (include date and a short summary).
   - Follow the repo formatting rules: ensure a blank line before and after section headers so release tooling matches expected formatting.

3. **Create a PR that contains the version bump and CHANGELOG updates**:
   - Use the PR template checklist to confirm the changelog was updated.
   - Have changes reviewed and merged to `main` following normal branch protection rules.

4. **Create an annotated tag and push it to trigger the release**:

   ```bash
   git tag -a v1.3.0 -m "Release v1.3.0"
   git push origin v1.3.0
   ```

5. **Release publishes automatically**:
   - Pushing the tag triggers `.github/workflows/release.yml` which builds and packages the release.
   - A GitHub Release is created with the packaged zip file (e.g., `TroubleScout-1.3.0-win-x64.zip`).

### Manual Release Tips

- If you need to publish a hotfix, bump the version, update the changelog, merge the PR, and create a tag as above.
- If the release workflow fails after tagging, check the `release.yml` workflow run in the Actions tab and inspect build logs.
- If a tag already exists for a version, bump the version number for a new release.

### Troubleshooting

**No release after creating a tag?**

- Verify the tag was pushed: `git ls-remote --tags origin | grep v1.3.0`
- Confirm the `release.yml` workflow ran: Check the Actions tab and the run triggered by your tag push
- If build fails, review logs in the workflow and fix errors, then re-run the workflow if needed

**Need to create a hotfix release?**

- Update version in `TroubleScout.csproj` (e.g., 1.3.0 → 1.3.1)
- Update `CHANGELOG.md` for the release
- Merge to `main` and create an annotated tag to trigger the release

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

### Post-Release Troubleshooting

**No tag created after version update?**

- Verify the change to `TroubleScout.csproj` was pushed to main
- Make sure a release tag was created and pushed (we no longer create tags automatically)
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
- Create an annotated tag and push it to trigger the release (see steps above)
