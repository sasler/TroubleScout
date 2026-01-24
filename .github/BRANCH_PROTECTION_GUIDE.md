# Branch Protection Quick Reference

Quick guide for common branch protection scenarios and how to handle them.

## ✅ Making a Valid Pull Request

### 1. Create Your Branch

```bash
# From main branch
git checkout main
git pull origin main

# Create feature branch
git checkout -b feature/your-feature-name

# Or for bug fix
git checkout -b fix/bug-description
```

### 2. Make Your Changes

```bash
# Make changes to files
# ...

# Stage and commit with emoji prefix
git add .
git commit -m "✨ Add your feature"

# Or with conventional commit
git commit -m "feat: add your feature"
```

### 3. Push and Open PR

```bash
# Push your branch
git push -u origin feature/your-feature-name

# Open PR on GitHub targeting main branch
```

### 4. Pass All Checks

✅ **Required checks:**
- Build and Test (builds successfully)
- Tests on Windows and Ubuntu (all tests pass)
- Branch Protection validation (branch name, commit messages)
- Code owner approval

## 🔧 Common Scenarios

### Scenario 1: "Branch name doesn't follow convention"

**Error:** ❌ Branch name must follow pattern: {type}/{description}

**Solution:**
```bash
# Rename your branch locally
git branch -m old-branch-name feature/new-branch-name

# Delete old remote branch and push new one
git push origin --delete old-branch-name
git push -u origin feature/new-branch-name
```

**Valid branch names:**
- `feature/add-diagnostics`
- `fix/memory-leak`
- `docs/update-readme`
- `test/add-unit-tests`

### Scenario 2: "Commit message format is invalid"

**Error:** ❌ Commits do not follow required format

**Solution - Amend last commit:**
```bash
# Fix the last commit message
git commit --amend -m "✨ Add feature with proper emoji"
git push --force-with-lease
```

**Solution - Rewrite commit history (if multiple commits):**
```bash
# Interactive rebase from main
git rebase -i origin/main

# In the editor, mark commits as 'reword' and update messages
# Save and close the editor

# Force push (safe with --force-with-lease)
git push --force-with-lease
```

**Valid formats:**
- Emoji: `✨ Add new feature`
- Conventional: `feat: add new feature`

### Scenario 3: "PR requires approval"

**Error:** This pull request requires approval from a code owner

**Solution:**
1. Wait for a code owner to review your PR
2. Address any review comments
3. Once approved, the PR can be merged

**Code owners:** Check `.github/CODEOWNERS` file

### Scenario 4: "Required checks are failing"

**Error:** Required status checks are failing

**Solution:**
1. Click on the failing check to see details
2. Fix the issue in your code
3. Commit and push the fix
4. Checks will automatically re-run

**Common failures:**
- **Build failure:** Code doesn't compile - check error messages
- **Test failure:** Tests are failing - run tests locally
- **Branch naming:** See Scenario 1
- **Commit messages:** See Scenario 2

### Scenario 5: "Branch is out of date"

**Error:** This branch is out-of-date with the base branch

**Solution:**
```bash
# Update your branch with latest main
git checkout feature/your-branch
git fetch origin
git merge origin/main

# Or use rebase for cleaner history
git rebase origin/main

# Push the updates
git push
```

**Or use GitHub's "Update branch" button**

### Scenario 6: "Merge conflicts"

**Error:** This branch has conflicts that must be resolved

**Solution:**
```bash
# Update from main to see conflicts
git fetch origin
git merge origin/main

# Fix conflicts in your editor
# Look for <<<<<<, ======, >>>>>> markers

# After fixing, stage the resolved files
git add .
git commit -m "🔀 Merge main and resolve conflicts"
git push
```

### Scenario 7: "Need to make changes after approval"

**Situation:** PR is approved but you need to make changes

**Important:** New commits after approval may require re-approval (depending on settings)

**Solution:**
```bash
# Make your changes
git add .
git commit -m "✨ Address review feedback"
git push

# Checks will re-run
# May need new approval if settings require it
```

## 📊 Check Your PR Status

### View All Checks
```bash
# Using GitHub CLI
gh pr checks

# Or view on GitHub
# Go to your PR → "Checks" tab
```

### View Specific Workflow Logs
```bash
# List recent workflow runs
gh run list

# View specific run logs
gh run view <run-id> --log
```

## 🚀 Quick Commands

```bash
# Check current branch
git branch --show-current

# View recent commits
git log --oneline -5

# View changes
git status
git diff

# Test locally before pushing
dotnet build
dotnet test

# Push and track remote branch
git push -u origin feature/your-branch

# Force push safely (after rebase/amend)
git push --force-with-lease
```

## 🆘 Emergency: Need to Merge Urgently

**DO NOT bypass branch protection!**

If you have a critical hotfix:

1. **Fix the issues** that are blocking merge
2. **Fast-track review** by alerting code owners
3. **Only if absolutely critical:** Repository admin can temporarily disable protection
   - Must re-enable immediately after merge
   - Document why bypass was necessary

**Remember:** Branch protection exists to prevent bugs and security issues from reaching production.

## 📚 Learn More

- [CONTRIBUTING.md](../CONTRIBUTING.md) - Full contribution guidelines
- [BRANCH_PROTECTION.md](BRANCH_PROTECTION.md) - Detailed configuration guide
- [GitHub Branch Protection Docs](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches)

## 🤔 Still Stuck?

1. Check [GitHub Discussions](https://github.com/sasler/TroubleScout/discussions)
2. Review existing [Pull Requests](https://github.com/sasler/TroubleScout/pulls) for examples
3. Ask in PR comments - we're here to help!
