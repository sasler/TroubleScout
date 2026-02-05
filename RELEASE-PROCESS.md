# Release Process

This document explains how TroubleScout releases are created and published.

## Automated Release System

TroubleScout uses a fully automated release system powered by GitHub Actions. When you merge a PR to main, the system analyzes commits to determine if a release is needed and automatically creates a release PR. When that PR is merged, the release is published automatically.

### How It Works

The release process has three main stages:

1. **PR Merge to Main**: When any PR is merged to `main`, the `auto-release.yml` workflow triggers
2. **Version Detection & Release PR**: The workflow:
   - Analyzes commits since the last release
   - Determines the version bump (major/minor/patch) using semantic versioning rules
   - Updates `TroubleScout.csproj` with new version numbers
   - Generates `CHANGELOG.md` entry from commits
   - Creates a "Release vX.Y.Z" PR automatically
3. **Release Publishing**: When the release PR is merged:
   - A version tag is created automatically
   - The `release.yml` workflow builds the self-contained executable
   - A GitHub release is published with the packaged zip file

### Semantic Versioning Rules

The automation uses commit message prefixes (emoji or conventional) to determine version bumps:

| Commit Type | Version Bump | Examples |
|-------------|--------------|----------|
| **Major** (x.0.0) | Breaking changes | `💥`, `BREAKING CHANGE:`, `breaking:` |
| **Minor** (0.x.0) | New features | `✨`, `feat:`, `feature:` |
| **Patch** (0.0.x) | Bug fixes, performance, refactoring | `🐛`, `fix:`, `⚡`, `perf:`, `♻️`, `refactor:` |
| **Skip Release** | Documentation, tests, CI, chores | `📝`, `docs:`, `🧪`, `test:`, `👷`, `ci:`, `chore:` |

### Normal Release Flow (Automated)

**You don't need to do anything!** Just merge PRs with proper commit messages:

1. **Create and merge your feature/fix PR** to `main` with appropriate commit message:
   ```bash
   git commit -m "✨ Add WinRM connection timeout configuration"
   # or
   git commit -m "🐛 Fix input wrap redraw issue"
   ```

2. **Wait for release PR**: Within 1-2 minutes, a release PR will be created automatically:
   - Title: `🔖 Release vX.Y.Z`
   - Contents: Updated `TroubleScout.csproj` and `CHANGELOG.md`
   - Labels: `release`, `automated`

3. **Review and merge release PR**:
   - Verify the version number is correct
   - Review the CHANGELOG.md entries
   - Ensure CI checks pass
   - Merge the PR (no special merge strategy required)

4. **Release publishes automatically**:
   - Tag is created: `vX.Y.Z`
   - Build workflow runs
   - GitHub release is published at `https://github.com/sasler/TroubleScout/releases`

### Manual Release Override

In rare cases where you need to create a release manually (e.g., automation failure, hotfix), you can bypass the automated system:

#### 1. Update Version Number

Edit `TroubleScout.csproj` and update the version numbers:

```xml
<Version>1.0.1</Version>
<AssemblyVersion>1.0.1.0</AssemblyVersion>
<FileVersion>1.0.1.0</FileVersion>
```

#### 2. Update CHANGELOG.md

Add a new section at the top of `CHANGELOG.md`:

```markdown
## [v1.0.1] - 2026-02-05

### 🐛 Bug Fixes
- Fixed critical security issue
```

#### 3. Commit and Push Changes

```powershell
git add TroubleScout.csproj CHANGELOG.md
git commit -m "🔖 Release v1.0.1"
git push origin main
```

#### 4. Create and Push Version Tag

Create an annotated tag with the CHANGELOG content:

```powershell
git tag -a v1.0.1 -m "Release v1.0.1

### 🐛 Bug Fixes
- Fixed critical security issue"

git push origin v1.0.1
```

#### 5. Monitor Release Workflow

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

### What Gets Released?

The automation system is smart about what triggers a release:

**Creates a release for:**
- New features (`✨ feat:`)
- Bug fixes (`🐛 fix:`)
- Performance improvements (`⚡ perf:`)
- Code refactoring (`♻️ refactor:`)
- Breaking changes (`💥 BREAKING CHANGE:`)

**Skips release for:**
- Documentation changes only (`📝 docs:`)
- Test changes only (`🧪 test:`)
- CI/CD changes only (`👷 ci:`)
- Chores and maintenance (`chore:`)

**Note**: If a PR contains both release-worthy commits (like features) AND non-release commits (like docs), a release will be created.

### Release PR Review Checklist

When a release PR is automatically created, review these items before merging:

- [ ] **Version number is correct**: Check if major/minor/patch bump is appropriate
- [ ] **CHANGELOG.md is accurate**: Ensure commit messages were categorized correctly
- [ ] **All CI checks pass**: Build and test workflows must succeed
- [ ] **No breaking changes missed**: If breaking changes exist, ensure major version bumped
- [ ] **Documentation is current**: README and docs reflect new version's features

### Troubleshooting

**No release PR created after merging?**
- Check if commits match non-release types (docs/test/ci/chore only)
- Verify `auto-release.yml` workflow ran: Check Actions tab
- Look for errors in the workflow logs

**Release PR has wrong version?**
- Check commit message prefixes match the semantic versioning rules
- Manually close the PR and update commit messages if needed
- The automation will create a new PR on the next push to main

**Multiple release PRs open?**
- This shouldn't happen - only one release PR per version
- Close older/stale release PRs manually
- The latest PR reflects the current state

**Release workflow fails after merging release PR?**
- Check if the tag was created: `git fetch --tags && git tag -l`
- Verify `release.yml` workflow ran: Check Actions tab
- Review build errors in workflow logs
- If build fails, fix the issue and re-run the workflow

**Need to update CHANGELOG after PR created?**
- Manually edit `CHANGELOG.md` in the release PR branch
- Commit and push changes to the release PR
- The changes will be included when the PR merges

**Want to customize release notes?**
- After the release is published, you can edit it on GitHub
- Go to Releases → Click the release → Edit button
- Add additional context, upgrade instructions, or warnings
