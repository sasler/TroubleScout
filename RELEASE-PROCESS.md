# Release Process

This document explains how TroubleScout releases are created, published, and submitted to WinGet.

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
   - A GitHub Release is created with packaged zip files for both architectures (e.g., `TroubleScout-v1.3.0-win-x64.zip` and `TroubleScout-v1.3.0-win-arm64.zip`).
   - If WinGet automation is configured, the published GitHub Release also triggers `.github/workflows/winget.yml`, which opens or updates the `winget-pkgs` PR for `sasler.TroubleScout`.

### Optional WinGet Automation

TroubleScout can automatically create a `microsoft/winget-pkgs` PR after each published GitHub Release.

The repository is configured to use [`vedantmgoyal9/winget-releaser`](https://github.com/vedantmgoyal9/winget-releaser) from a dedicated `.github/workflows/winget.yml` workflow triggered by `release: published`.

#### One-time setup

1. Fork `microsoft/winget-pkgs` under the same GitHub account or organization that owns this repository.
2. Create a **classic** GitHub Personal Access Token with `public_repo` scope.
3. Add the token to this repository as the `WINGET_TOKEN` secret.
4. Verify the existing WinGet package identifier remains `sasler.TroubleScout`.

#### Why this workflow is separate

- `winget-releaser` expects a **published** GitHub Release so the release assets are publicly available.
- Keeping WinGet submission separate from `release.yml` makes retries easier when `winget-pkgs` validation fails for reasons outside this repository.
- The workflow can also be run manually via **Actions -> Publish to WinGet** using a release tag like `v1.8.1`.

### Local WinGet Validation Helper

Before opening or retrying a WinGet PR, you can generate a fresh TroubleScout manifest and validate it locally:

```powershell
pwsh .\Tools\Validate-WinGetRelease.ps1 -Version 1.8.1
```

To also run the official `winget-pkgs` sandbox test after validation, clone `microsoft/winget-pkgs` locally and pass its path:

```powershell
pwsh .\Tools\Validate-WinGetRelease.ps1 -Version 1.8.1 -RunSandbox -WingetPkgsRoot C:\src\winget-pkgs
```

This helper:

- downloads the x64 and arm64 GitHub release zips for the specified version
- computes SHA256 hashes
- generates temporary WinGet manifest files
- runs `winget validate`
- optionally runs `Tools\SandboxTest.ps1` from a local `winget-pkgs` clone

### Manual Release Tips

- If you need to publish a hotfix, bump the version, update the changelog, merge the PR, and create a tag as above.
- If the release workflow fails after tagging, check the `release.yml` workflow run in the Actions tab and inspect build logs.
- If a tag already exists for a version, bump the version number for a new release.

### Troubleshooting

**No release after creating a tag?**

- Verify the tag was pushed: `git ls-remote --tags origin | grep v1.3.0`
- Confirm the `release.yml` workflow ran: Check the Actions tab and the run triggered by your tag push
- If build fails, review logs in the workflow and fix errors, then re-run the workflow if needed

**GitHub Release succeeded, but no WinGet PR was created?**

- Confirm `.github/workflows/winget.yml` exists on the default branch.
- Verify the `WINGET_TOKEN` repository secret is configured.
- Make sure the release is **published** and not a draft or prerelease.
- Verify a fork of `microsoft/winget-pkgs` exists under the repo owner account.
- Re-run the **Publish to WinGet** workflow manually with the release tag if needed.

**Need to create a hotfix release?**

- Update version in `TroubleScout.csproj` (e.g., 1.3.0 → 1.3.1)
- Update `CHANGELOG.md` for the release
- Merge to `main` and create an annotated tag to trigger the release

### Manual Release Recovery & Override

If you need to recover from a failed release or create a release manually, follow these recovery steps. Creating annotated tags is the normal way to trigger releases; the guidance below helps when something goes wrong (bad tag, failed workflow, missing assets).

#### 1. Create or recreate an annotated tag

If you need to publish a release immediately, create an annotated tag and push it:

```powershell
# Create and push an annotated tag
git tag -a v1.0.1 -m "Release v1.0.1"
git push origin v1.0.1
```

If the tag was pushed erroneously and you need to recreate it:

```powershell
# Delete local and remote tag, recreate and push
git tag -d v1.0.1
git push origin :refs/tags/v1.0.1
git tag -a v1.0.1 -m "Release v1.0.1"
git push origin v1.0.1
```

#### 2. Re-run the release workflow

If the release workflow failed after the tag was created, re-run the workflow from the **Actions** page:

- Find the run triggered by the tag push and choose **Re-run jobs** (or **Re-run failed jobs**) to retry the build and publish steps.
- If logs show a reproducible failure, fix the underlying issue and then recreate the tag (delete and re-create the tag) to trigger a fresh run.

#### 2a. Re-run the WinGet submission workflow

If the GitHub Release succeeded but the WinGet submission failed or did not start:

- Open the **Publish to WinGet** workflow in the Actions tab.
- Use **Run workflow** and provide the release tag (for example `v1.8.1`).
- If the workflow opens a `winget-pkgs` PR and the community validation later fails, treat that separately from TroubleScout's own release build.

#### 3. Re-publish release assets (if needed)

If the release completed but assets are missing or corrupted, you can recreate the release assets and upload them manually:

- Rebuild the package locally as described in the build steps and create both zips (e.g., `TroubleScout-v1.0.1-win-x64.zip`, `TroubleScout-v1.0.1-win-arm64.zip`).
- Upload the assets via the GitHub UI on the release page, or use the GitHub CLI:

```bash
# Create or update a GitHub release with assets
gh release create v1.0.1 "dist/TroubleScout-v1.0.1-win-x64.zip" --notes "Release v1.0.1"

# Add arm64 asset
gh release upload v1.0.1 "dist/TroubleScout-v1.0.1-win-arm64.zip"
```

#### 4. Notes & best practices

- Prefer recreating and re-pushing an annotated tag to trigger a clean release run instead of manually attempting to patch runs.
- Use the Actions logs to identify root causes before re-running.
- If you need a temporary manual trigger without creating a permanent tag, create a timestamped tag (e.g., `v1.0.1-rc1`) and delete it after verification.
- WinGet community validation failures are common enough that a failed `winget-pkgs` run does not automatically mean the TroubleScout release packaging is wrong. Re-run and inspect the external validation details before changing the app.
- Keep the current portable one-EXE release strategy unless local validation or a reproducible WinGet-specific failure proves that TroubleScout itself needs a behavioral change.

You can monitor workflow runs at: `https://github.com/sasler/TroubleScout/actions`

### Release Package Contents

Each release includes two zip files named `TroubleScout-v{version}-win-x64.zip` and `TroubleScout-v{version}-win-arm64.zip` containing:

- `TroubleScout.exe` - Self-contained executable (~54 MB)
- `runtimes/` (when present) - PowerShell SDK native dependencies

**Important**: Extract the full zip contents together. Some releases include `runtimes/` files and some do not, depending on publish output.

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

**WinGet validation fails after the PR is opened?**

- Check the `winget-pkgs` PR and its linked Azure validation run.
- Re-run the WinGet submission workflow if the first submission failed before opening the PR.
- If the PR exists but validation failed, prefer re-running the `winget-pkgs` validation (`@wingetbot run`) or updating the manifest PR rather than cutting a brand-new TroubleScout release unless the release assets themselves changed.

**Need to create a hotfix release?**

- Update version in `TroubleScout.csproj` (e.g., 1.2.0 → 1.2.1)
- Commit and push to main
- Create an annotated tag and push it to trigger the release (see steps above)
