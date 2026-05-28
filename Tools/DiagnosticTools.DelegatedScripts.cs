using System.ComponentModel;
using System.Text;
using TroubleScout.Services;
using TroubleScout.UI;

namespace TroubleScout.Tools;

public partial class DiagnosticTools
{
    private static string BuildPowerShellChildProcessInvocation(string script, string? scriptPath)
    {
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var executableResolver = "$__tsPwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source; " +
            "if ([string]::IsNullOrWhiteSpace($__tsPwsh)) { $__tsPwsh = (Get-Command powershell.exe -ErrorAction Stop).Source }; ";
        if (!string.IsNullOrWhiteSpace(scriptPath))
        {
            return executableResolver + $"& $__tsPwsh -NoProfile -ExecutionPolicy Bypass -File '{EscapeSingleQuotes(scriptPath)}'";
        }

        return executableResolver + $"& $__tsPwsh -NoProfile -ExecutionPolicy Bypass -EncodedCommand '{encodedScript}'";
    }

    private Task<string> StageDelegatedPowerShellScriptAsync(
        [Description("The exact PowerShell script the subagent will run")] string script,
        [Description("Brief description shown in the terminal and report")] string description,
        [Description("Optional: the server session name the subagent will target")] string? sessionName = null)
    {
        try
        {
            var staged = _scriptStore.Stage(script.Trim(), description, sessionName);
            return Task.FromResult($"[OK] Staged delegated PowerShell scriptId={staged.ScriptId} path={staged.Path}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"[ERROR] Failed to stage delegated PowerShell script: {ex.Message}");
        }
    }

    private async Task<string> AuthorizeDelegatedPowerShellScriptAsync(
        [Description("The scriptId returned by stage_delegated_powershell_script")] string scriptId,
        [Description("Optional: the reason for delegated evidence collection")] string? intent = null)
    {
        if (!_scriptStore.TryGet(scriptId, out var staged))
        {
            return $"[ERROR] No staged delegated PowerShell script found for scriptId={scriptId}.";
        }

        var resolved = ResolveExecutor(staged.SessionName);
        if (resolved.Error != null)
        {
            LogCommandAction(
                staged.SessionName ?? _targetServer,
                staged.Script,
                resolved.Error,
                CommandApprovalState.Blocked,
                "Subagent PowerShell",
                staged.Description,
                "Script",
                staged.ScriptId);
            _scriptStore.Delete(scriptId);
            return resolved.Error;
        }

        var validation = resolved.Executor!.ValidateCommand(staged.Script);
        if (!validation.IsAllowed && !validation.RequiresApproval)
        {
            LogCommandAction(
                resolved.Target!,
                staged.Script,
                $"[BLOCKED] {validation.Reason}",
                CommandApprovalState.Blocked,
                "Subagent PowerShell",
                staged.Description,
                "Script",
                staged.ScriptId);
            _scriptStore.Delete(scriptId);
            return $"[BLOCKED] {validation.Reason}";
        }

        if (!validation.RequiresApproval)
        {
            return "[OK] This delegated script is read-only and does not require preauthorization.";
        }

        if (ConsoleUI.IsInputRedirectedResolver())
        {
            LogCommandAction(
                resolved.Target!,
                staged.Script,
                "[DENIED] Protected delegated script execution requires interactive approval, which is unavailable in headless mode.",
                CommandApprovalState.Denied,
                "Subagent PowerShell",
                staged.Description,
                "Script",
                staged.ScriptId);
            _scriptStore.Delete(scriptId);
            return "[DENIED] Protected delegated script execution requires interactive approval, which is unavailable in headless mode.";
        }

        var approved = await _approvalCallback(staged.Script, validation.Reason ?? intent ?? "Requires user approval");
        if (!approved)
        {
            LogCommandAction(
                resolved.Target!,
                staged.Script,
                "[DENIED] User denied authorization for delegated script execution.",
                CommandApprovalState.Denied,
                "Subagent PowerShell",
                staged.Description,
                "Script",
                staged.ScriptId);
            _scriptStore.Delete(scriptId);
            return "[DENIED] User denied authorization for delegated script execution.";
        }

        var authorizationId = Guid.NewGuid().ToString("N");
        lock (_delegatedGrantLock)
        {
            _delegatedScriptGrants[authorizationId] =
                new DelegatedPowerShellGrant(staged.ScriptId, NormalizeSessionName(staged.SessionName), CommandApprovalState.ApprovedByUser);
        }

        return $"[APPROVED] Delegate this exact script with authorizationId={authorizationId}";
    }

    private async Task<string> RunDelegatedPowerShellScriptAsync(
        [Description("The scriptId returned by stage_delegated_powershell_script")] string scriptId,
        [Description("One-use authorizationId for a protected script; omit for proven read-only scripts")] string? authorizationId = null,
        [Description("Optional: the preconnected server session name; must match the staged script target if one was set")] string? sessionName = null)
    {
        if (!_scriptStore.TryGet(scriptId, out var staged))
        {
            return $"[ERROR] No staged delegated PowerShell script found for scriptId={scriptId}.";
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(sessionName)
                && !string.IsNullOrWhiteSpace(staged.SessionName)
                && !string.Equals(staged.SessionName, NormalizeSessionName(sessionName), StringComparison.OrdinalIgnoreCase))
            {
                LogCommandAction(
                    staged.SessionName,
                    staged.Script,
                    $"[ERROR] Staged scriptId={scriptId} targets session '{staged.SessionName}'.",
                    CommandApprovalState.Blocked,
                    "Subagent PowerShell",
                    staged.Description,
                    "Script",
                    staged.ScriptId);
                return $"[ERROR] Staged scriptId={scriptId} targets session '{staged.SessionName}'.";
            }

            var resolved = ResolveExecutor(staged.SessionName ?? sessionName);
            if (resolved.Error != null)
            {
                LogCommandAction(
                    staged.SessionName ?? sessionName ?? _targetServer,
                    staged.Script,
                    resolved.Error,
                    CommandApprovalState.Blocked,
                    "Subagent PowerShell",
                    staged.Description,
                    "Script",
                    staged.ScriptId);
                return resolved.Error;
            }

            var executor = resolved.Executor!;
            if (executor.IsJeaSession)
            {
                LogCommandAction(
                    resolved.Target!,
                    staged.Script,
                    "[ERROR] JEA session does not support delegated script execution; delegate an exact single JEA command instead.",
                    CommandApprovalState.Blocked,
                    "Subagent PowerShell",
                    staged.Description,
                    "Script",
                    staged.ScriptId);
                return "[ERROR] JEA session does not support delegated script execution; delegate an exact single JEA command instead.";
            }

            var validation = executor.ValidateCommand(staged.Script);
            if (!validation.IsAllowed && !validation.RequiresApproval)
            {
                LogCommandAction(
                    resolved.Target!,
                    staged.Script,
                    $"[BLOCKED] {validation.Reason}",
                    CommandApprovalState.Blocked,
                    "Subagent PowerShell",
                    staged.Description,
                    "Script",
                    staged.ScriptId);
                return $"[BLOCKED] {validation.Reason}";
            }

            var approvalState = CommandApprovalState.StrictReadOnly;
            if (validation.RequiresApproval)
            {
                DelegatedPowerShellGrant? grant = null;
                lock (_delegatedGrantLock)
                {
                    if (!string.IsNullOrWhiteSpace(authorizationId)
                        && _delegatedScriptGrants.TryGetValue(authorizationId, out var candidate)
                        && candidate.Command.Equals(staged.ScriptId, StringComparison.Ordinal)
                        && string.Equals(candidate.SessionName, NormalizeSessionName(staged.SessionName ?? sessionName), StringComparison.OrdinalIgnoreCase))
                    {
                        grant = candidate;
                        _delegatedScriptGrants.Remove(authorizationId);
                    }
                }

                if (grant == null)
                {
                    var reason = string.IsNullOrWhiteSpace(validation.Reason)
                        ? string.Empty
                        : $" Reason: {validation.Reason}";
                    LogCommandAction(
                        resolved.Target!,
                        staged.Script,
                        $"[PREAUTHORIZATION REQUIRED] The primary agent must authorize this exact protected script before delegation.{reason}",
                        CommandApprovalState.ApprovalRequested,
                        "Subagent PowerShell",
                        staged.Description,
                        "Script",
                        staged.ScriptId);
                    return $"[PREAUTHORIZATION REQUIRED] The primary agent must authorize this exact protected script before delegation.{reason}";
                }

                approvalState = grant.ApprovalState;
            }

            executor.AddHistoryEntry($"[EXECUTED SCRIPT {staged.ScriptId}] {staged.Script}");
            ConsoleUI.ShowCommandExecution(
                staged.Script,
                resolved.IsAlternate ? (staged.SessionName ?? sessionName)! : _targetServer,
                CommandExecutionOrigin.SubagentPowerShell,
                staged.Description,
                staged.ScriptId,
                "Script");

            var invocation = BuildPowerShellChildProcessInvocation(
                staged.Script,
                executor.IsLocalExecution ? staged.Path : null);
            var wrappedCommand = WrapCommandWithTargetVerification(invocation, executor, resolved.IsAlternate ? staged.SessionName ?? sessionName : null);
            var result = await executor.ExecuteAsync(wrappedCommand, trackInHistory: false);
            var output = result.Success
                ? string.IsNullOrWhiteSpace(result.Output) ? "[OK] Script completed with no output." : result.Output
                : $"[ERROR] {result.Error ?? "Unknown error occurred"}";
            LogCommandAction(
                resolved.Target!,
                staged.Script,
                output,
                approvalState,
                "Subagent PowerShell",
                staged.Description,
                "Script",
                staged.ScriptId);
            return resolved.IsAlternate ? $"[{staged.SessionName ?? sessionName}] {output}" : output;
        }
        finally
        {
            _scriptStore.Delete(scriptId);
        }
    }
}
