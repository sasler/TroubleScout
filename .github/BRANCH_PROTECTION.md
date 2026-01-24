# Branch Protection Configuration Guide

This document provides step-by-step instructions for repository administrators to configure branch protection rules for the `main` branch.

## 📋 Overview

Branch protection rules ensure code quality by requiring reviews, status checks, and preventing direct commits to protected branches. This repository includes automated workflows to enforce protection policies, but GitHub repository settings must also be configured.

## 🔧 Configuring Branch Protection Rules

### Step 1: Access Branch Protection Settings

1. Navigate to your repository on GitHub
2. Click **Settings** (requires admin permissions)
3. Click **Branches** in the left sidebar
4. Click **Add branch protection rule** (or edit existing rule for `main`)

### Step 2: Configure Rule Pattern

- **Branch name pattern**: `main`

### Step 3: Enable Required Settings

#### ✅ Require Pull Request Reviews Before Merging

- [x] **Require a pull request before merging**
  - **Required number of approvals before merging**: 1
  - [x] **Dismiss stale pull request approvals when new commits are pushed**
  - [x] **Require review from Code Owners**
  - [ ] Require approval of the most recent reviewable push (optional)

#### ✅ Require Status Checks Before Merging

- [x] **Require status checks to pass before merging**
  - [x] **Require branches to be up to date before merging**
  
**Select these required status checks:**
- `build` (from Build and Test workflow)
- `test / windows-latest` (from Tests workflow)
- `test / ubuntu-latest` (from Tests workflow)
- `validate` (from Branch Protection workflow)
- `required-checks-status` (from Branch Protection workflow)

> **Note**: Status checks will only appear in the list after they have run at least once. Open a test PR to populate the list.

#### ✅ Require Conversation Resolution Before Merging

- [x] **Require conversation resolution before merging**

#### ✅ Require Signed Commits (Recommended)

- [x] **Require signed commits** (optional but recommended for security)

#### ✅ Require Linear History (Recommended)

- [x] **Require linear history** (enforces squash or rebase merges)

#### ✅ Include Administrators

- [x] **Do not allow bypassing the above settings**
  - This ensures even administrators follow the same rules

#### ✅ Restrict Push Access

- [ ] **Restrict who can push to matching branches** (optional)
  - Select specific users/teams if needed

#### ✅ Allow Force Pushes

- [ ] **Allow force pushes** (keep UNCHECKED)

#### ✅ Allow Deletions

- [ ] **Allow deletions** (keep UNCHECKED)

### Step 4: Save Changes

Click **Create** or **Save changes** to apply the branch protection rule.

## 🔍 Verification

After configuring, verify the rules are working:

1. **Test Direct Commit**: Try to push directly to `main` - should be rejected
   ```bash
   git checkout main
   git commit --allow-empty -m "test"
   git push origin main
   # Should fail with: "required status checks"
   ```

2. **Test PR Without Approval**: Open a PR and try to merge without approval - should be blocked

3. **Test PR Without Passing Checks**: Open a PR with failing tests - should be blocked

4. **Test Valid PR**: Open a compliant PR with:
   - Proper branch name (`feature/test-protection`)
   - Proper commit message (emoji or conventional commit)
   - Passing status checks
   - Code owner approval
   - Should successfully merge ✅

## 📊 Status Checks Reference

These workflows provide the required status checks:

| Workflow | Status Check Name | Purpose |
|----------|------------------|---------|
| `build.yml` | `build` | Ensures code builds and tests pass |
| `tests.yml` | `test / windows-latest` | Runs tests on Windows |
| `tests.yml` | `test / ubuntu-latest` | Runs tests on Ubuntu |
| `branch-protection.yml` | `validate` | Validates branch naming, commit messages |
| `branch-protection.yml` | `required-checks-status` | Summary check for all validations |

## 🚨 Troubleshooting

### Status Checks Not Appearing

**Problem**: Required status checks don't show up in the dropdown

**Solution**: 
1. Open a test PR targeting `main`
2. Wait for workflows to run
3. Go back to branch protection settings
4. Status checks should now appear in the list

### PR Can't Be Merged

**Problem**: PR shows "Merging is blocked"

**Possible Causes**:
1. **Status checks failing**: Check the "Checks" tab on the PR
2. **Missing approval**: Request review from a code owner
3. **Branch not up to date**: Click "Update branch" button
4. **Conversations not resolved**: Resolve all review comments
5. **Unsigned commits**: Sign commits with GPG/SSH key (if required)

### Urgent Hotfix Needed

**Problem**: Need to merge critical fix but checks are failing

**Solution**: 
1. **Do NOT bypass protection rules** - they exist for a reason
2. Fix the failing checks first
3. If truly urgent, a repository admin can temporarily disable the rule
4. **Always re-enable protection immediately after**

## 🔐 Additional Security Settings

### Enable Additional Repository Security Features

1. **Dependency Scanning**:
   - Go to **Settings** → **Security** → **Code security and analysis**
   - Enable **Dependabot alerts**
   - Enable **Dependabot security updates**

2. **Secret Scanning**:
   - Enable **Secret scanning**
   - Enable **Push protection** to block commits with secrets

3. **Code Scanning**:
   - Enable **CodeQL analysis** (if available)

### Require Two-Factor Authentication

1. **Organization Settings** → **Authentication security**
2. Enable **Require two-factor authentication**

## 📚 Additional Resources

- [GitHub Docs: Managing a branch protection rule](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/managing-a-branch-protection-rule)
- [GitHub Docs: About protected branches](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches)
- [GitHub Docs: About code owners](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-code-owners)

## 📝 Maintenance

Review branch protection settings:
- **Quarterly**: Ensure rules are still appropriate for team size and workflow
- **After team changes**: Update Code Owners when team members change
- **After workflow changes**: Update required status checks if workflows are renamed

---

Last updated: January 2026
