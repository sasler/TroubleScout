# Security Policy

## Supported Versions

We release patches for security vulnerabilities. Currently supported versions:

| Version | Supported          |
| ------- | ------------------ |
| latest  | :white_check_mark: |
| < latest| :x:                |

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, please report them via:

1. **GitHub Security Advisories** (preferred):
   - Go to the [Security Advisories](https://github.com/sasler/TroubleScout/security/advisories) page
   - Click "Report a vulnerability"
   - Provide detailed information about the vulnerability

2. **Email** (alternative):
   - Contact the repository maintainer directly
   - Include "SECURITY" in the subject line
   - Provide detailed information about the vulnerability

### What to Include

Please include the following information in your report:

- Type of vulnerability (e.g., command injection, authentication bypass)
- Full paths of source file(s) related to the vulnerability
- Location of the affected source code (tag/branch/commit or direct URL)
- Step-by-step instructions to reproduce the vulnerability
- Proof-of-concept or exploit code (if possible)
- Impact of the vulnerability, including how an attacker might exploit it

### Response Timeline

- **Initial response**: Within 48 hours
- **Status update**: Within 7 days
- **Fix timeline**: Depends on severity and complexity
  - Critical: Within 7 days
  - High: Within 30 days
  - Medium: Within 90 days
  - Low: Next planned release

## Security Measures

### Code Review

- All changes require pull request review
- Code owners must approve changes
- Automated security scanning via CodeQL (when available)

### Branch Protection

The `main` branch is protected with:
- Required pull request reviews
- Required status checks (build, tests)
- No direct commits allowed
- No force pushes allowed

See [BRANCH_PROTECTION.md](.github/BRANCH_PROTECTION.md) for details.

### PowerShell Command Safety

TroubleScout implements a security model for PowerShell command execution:

**Auto-Execute (Safe):**
- All `Get-*` commands (read-only)
- Commands like `Format-*`, `Select-*`, `Where-*`, `Sort-*`

**Require Approval:**
- `Set-*`, `Start-*`, `Stop-*`, `Restart-*`
- `Remove-*`, `New-*`, `Add-*`, `Enable-*`, `Disable-*`
- Any command that modifies system state

**Blocked:**
- `Get-Credential` (sensitive credential handling)
- `Get-Secret` (secret management)

### Dependencies

- Regular dependency updates via Dependabot
- Automated security scanning for vulnerabilities
- Use of official, maintained packages only

### Best Practices

When contributing code:

1. **Never commit secrets** (API keys, passwords, tokens)
2. **Validate all user input** to prevent injection attacks
3. **Follow the principle of least privilege**
4. **Use parameterized commands** when executing PowerShell
5. **Sanitize output** before displaying to users
6. **Handle errors securely** (don't expose sensitive info in error messages)

## Known Security Considerations

### WinRM Security

When connecting to remote servers:
- Uses Windows Integrated Authentication (Kerberos/NTLM)
- Credentials are handled by the Windows security subsystem
- Never stores or logs credentials
- Always verify server identity before connecting

### AI/Copilot Interactions

- User prompts are sent to GitHub Copilot API
- Command execution requires explicit approval for non-read-only operations
- AI suggestions are validated before execution
- No sensitive data should be included in prompts

### Local Execution

- PowerShell commands run with current user's permissions
- Does not attempt privilege escalation
- Respects Windows access controls

## Security Updates

Security updates will be:
- Released as soon as possible after verification
- Documented in release notes
- Announced via GitHub Security Advisories
- Tagged with version bump reflecting severity

## Disclosure Policy

- We follow responsible disclosure practices
- Credit will be given to security researchers who report vulnerabilities
- We will coordinate disclosure timing with reporters
- Public disclosure only after fix is available

## Contact

For security concerns, please use:
- GitHub Security Advisories: https://github.com/sasler/TroubleScout/security/advisories
- Repository maintainer via GitHub

Thank you for helping keep TroubleScout and its users safe!
