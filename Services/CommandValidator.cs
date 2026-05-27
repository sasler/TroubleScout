using System.Management.Automation.Language;

namespace TroubleScout.Services;

internal class CommandValidator
{
    private static readonly string[] DefaultSafeCommands = AppSettingsStore.DefaultSafeCommands;

    private static readonly HashSet<string> BlockedCommands =
    [
        "Get-Credential",
        "Get-Secret"
    ];

    private static readonly string[] DangerousVerbPrefixes =
    [
        "Set-", "Start-", "Stop-", "Restart-", "Remove-", "New-", "Clear-",
        "Enable-", "Disable-", "Rename-", "Move-", "Add-", "Install-",
        "Uninstall-", "Invoke-", "Register-", "Unregister-", "Reset-",
        "Update-", "Grant-", "Revoke-", "Suspend-", "Resume-", "Push-",
        "Mount-", "Dismount-", "Repair-", "Format-"
    ];

    private static readonly HashSet<string> ReadOnlyFormatCommands =
    [
        "Format-Custom", "Format-Hex", "Format-List", "Format-Table", "Format-Wide"
    ];

    private static readonly HashSet<string> MutatingMembers =
    [
        "Kill", "Stop", "Dispose", "Delete", "Start",
        "WriteAllText", "WriteAllBytes", "WriteAllLines", "AppendAllText", "AppendAllLines",
        "Create", "CreateDirectory", "Move", "Copy", "Replace",
        "SetAttributes", "SetCreationTime", "SetLastAccessTime", "SetLastWriteTime"
    ];

    private readonly ExecutionMode _executionMode;
    private readonly IReadOnlyList<string>? _customSafeCommands;
    private readonly IReadOnlySet<string>? _jeaAllowedCommands;
    private readonly string? _configurationName;

    internal CommandValidator(
        ExecutionMode executionMode,
        IReadOnlyList<string>? customSafeCommands,
        IReadOnlySet<string>? jeaAllowedCommands,
        string? configurationName)
    {
        _executionMode = executionMode;
        _customSafeCommands = customSafeCommands;
        _jeaAllowedCommands = jeaAllowedCommands;
        _configurationName = configurationName;
    }

    public CommandValidation ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new CommandValidation(false, false, "Command cannot be empty", CommandSafetyClassification.Invalid);
        }

        if (_configurationName != null)
        {
            return _jeaAllowedCommands != null
                ? ValidateJeaCommand(command)
                : new CommandValidation(false, false, "JEA command discovery has not completed for this session.", CommandSafetyClassification.Blocked);
        }

        var classification = ClassifyCommand(command, out var detail);
        return classification switch
        {
            CommandSafetyClassification.ReadOnly => new CommandValidation(true, false, detail, classification),
            CommandSafetyClassification.Blocked => new CommandValidation(false, false, detail, classification),
            CommandSafetyClassification.Invalid => new CommandValidation(false, false, detail, classification),
            CommandSafetyClassification.Mutating => RequireApproval(detail ?? "Command can modify system state", classification),
            _ => RequireApproval(detail ?? "Command could not be proven read-only", classification)
        };
    }

    internal static CommandValidation ValidateStandaloneCommand(
        string command,
        ExecutionMode executionMode,
        IReadOnlyList<string>? safeCommands = null)
    {
        var validator = new CommandValidator(executionMode, safeCommands, null, null);
        return validator.ValidateCommand(command);
    }

    internal static bool MatchesSafeCommandPattern(string cmdletName, string pattern)
    {
        if (string.IsNullOrWhiteSpace(cmdletName) || string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var normalizedCmdletName = cmdletName.Trim();
        var normalizedPattern = pattern.Trim();

        if (normalizedPattern.EndsWith('*'))
        {
            var prefix = normalizedPattern[..^1];
            if (string.IsNullOrEmpty(prefix) || IsDangerousPrefix(prefix))
            {
                return false;
            }

            return normalizedCmdletName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        if (IsDangerousCommand(normalizedCmdletName) && !ReadOnlyFormatCommands.Contains(normalizedCmdletName))
        {
            return false;
        }

        return normalizedCmdletName.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase);
    }

    internal static string ExtractCmdletName(string command)
    {
        if (TryParse(command, out var ast, out _) && ast != null)
        {
            return ast.FindAll(node => node is CommandAst, searchNestedScriptBlocks: true)
                .OfType<CommandAst>()
                .Select(node => node.GetCommandName() ?? string.Empty)
                .FirstOrDefault() ?? string.Empty;
        }

        return string.Empty;
    }

    private CommandValidation ValidateJeaCommand(string command)
    {
        if (!TryParse(command, out var ast, out var error) || ast == null)
        {
            return new CommandValidation(false, false, error ?? "JEA command could not be parsed.", CommandSafetyClassification.Invalid);
        }

        var cmdlets = GetCommandNames(ast);
        if (cmdlets.Count == 0)
        {
            return new CommandValidation(false, false,
                $"Command does not contain a recognized JEA cmdlet for session '{_configurationName}'.",
                CommandSafetyClassification.Blocked);
        }

        foreach (var cmdlet in cmdlets)
        {
            if (BlockedCommands.Contains(cmdlet))
            {
                return new CommandValidation(false, false, $"Command '{cmdlet}' is blocked for security reasons", CommandSafetyClassification.Blocked);
            }

            if (!_jeaAllowedCommands!.Contains(cmdlet))
            {
                return new CommandValidation(false, false,
                    $"Command '{cmdlet}' is not available in JEA session '{_configurationName}'.",
                    CommandSafetyClassification.Blocked);
            }
        }

        return new CommandValidation(true, false, Classification: CommandSafetyClassification.ReadOnly);
    }

    private CommandSafetyClassification ClassifyCommand(string command, out string? detail)
    {
        detail = null;
        if (command.Contains('`'))
        {
            detail = "Command contains escape characters and cannot be proven read-only";
            return CommandSafetyClassification.Unknown;
        }

        if (!TryParse(command, out var ast, out var parseError) || ast == null)
        {
            detail = $"PowerShell command could not be parsed safely: {parseError}";
            return CommandSafetyClassification.Invalid;
        }

        if (ast.FindAll(node => node is FileRedirectionAst, true).Any())
        {
            detail = "Command includes file redirection that can modify system state";
            return CommandSafetyClassification.Mutating;
        }

        var invokedMembers = ast.FindAll(node => node is InvokeMemberExpressionAst, true)
            .OfType<InvokeMemberExpressionAst>()
            .ToList();
        var unsafeMember = invokedMembers
            .Select(node => node.Member.Extent.Text.Trim('\'', '"'))
            .FirstOrDefault(member => MutatingMembers.Contains(member));
        if (!string.IsNullOrWhiteSpace(unsafeMember))
        {
            detail = $"Command invokes mutating member '{unsafeMember}'";
            return CommandSafetyClassification.Mutating;
        }

        var unknownInvocation = invokedMembers.FirstOrDefault(node => !IsKnownReadOnlyMemberInvocation(node));
        if (unknownInvocation != null)
        {
            var unknownMember = unknownInvocation.Member.Extent.Text.Trim('\'', '"');
            detail = $"Command invokes object member '{unknownMember}' and cannot be proven read-only";
            return CommandSafetyClassification.Unknown;
        }

        var propertyAssignment = ast.FindAll(node => node is AssignmentStatementAst, true)
            .OfType<AssignmentStatementAst>()
            .FirstOrDefault(assignment => assignment.Left is not VariableExpressionAst);
        if (propertyAssignment != null)
        {
            detail = "Command assigns to an object property and can modify state";
            return CommandSafetyClassification.Mutating;
        }

        var commandAsts = ast.FindAll(node => node is CommandAst, searchNestedScriptBlocks: true)
            .OfType<CommandAst>()
            .ToList();
        if (commandAsts.Any(commandAst => string.IsNullOrWhiteSpace(commandAst.GetCommandName())))
        {
            detail = "Command contains a dynamic or unresolved invocation and cannot be proven read-only";
            return CommandSafetyClassification.Unknown;
        }

        var commandNames = commandAsts
            .Select(commandAst => commandAst.GetCommandName()!)
            .ToList();
        if (commandNames.Count == 0)
        {
            if (ast.FindAll(node => node is AssignmentStatementAst, true).Any())
            {
                detail = "Command contains only an assignment and cannot be proven read-only";
                return CommandSafetyClassification.Unknown;
            }

            return CommandSafetyClassification.ReadOnly;
        }

        foreach (var cmdlet in commandNames)
        {
            if (BlockedCommands.Contains(cmdlet))
            {
                detail = $"Command '{cmdlet}' is blocked for security reasons";
                return CommandSafetyClassification.Blocked;
            }

            if (IsDangerousCommand(cmdlet) && !ReadOnlyFormatCommands.Contains(cmdlet))
            {
                detail = $"Command '{cmdlet}' can modify system state";
                return CommandSafetyClassification.Mutating;
            }
        }

        var unknown = commandNames.FirstOrDefault(cmdlet => !IsReadOnlySingleCommand(cmdlet));
        if (!string.IsNullOrWhiteSpace(unknown))
        {
            detail = $"Command '{unknown}' is not recognized as read-only";
            return CommandSafetyClassification.Unknown;
        }

        return CommandSafetyClassification.ReadOnly;
    }

    private bool IsReadOnlySingleCommand(string cmdletName)
    {
        if (ReadOnlyFormatCommands.Contains(cmdletName))
        {
            return true;
        }

        return GetSafeCommandPatterns().Any(pattern => MatchesSafeCommandPattern(cmdletName, pattern));
    }

    private IReadOnlyList<string> GetSafeCommandPatterns() => _customSafeCommands ?? DefaultSafeCommands;

    private static bool IsKnownReadOnlyMemberInvocation(InvokeMemberExpressionAst invocation)
    {
        var member = invocation.Member.Extent.Text.Trim('\'', '"');
        var receiver = invocation.Expression.Extent.Text.Trim();

        if (member.Equals("Round", StringComparison.OrdinalIgnoreCase))
        {
            return receiver.Equals("[math]", StringComparison.OrdinalIgnoreCase);
        }

        return (member.Equals("AddDays", StringComparison.OrdinalIgnoreCase)
                || member.Equals("AddHours", StringComparison.OrdinalIgnoreCase)
                || member.Equals("AddMinutes", StringComparison.OrdinalIgnoreCase)
                || member.Equals("AddSeconds", StringComparison.OrdinalIgnoreCase))
            && receiver.Equals("(Get-Date)", StringComparison.OrdinalIgnoreCase);
    }

    private CommandValidation RequireApproval(string reason, CommandSafetyClassification classification)
    {
        var modeText = _executionMode == ExecutionMode.Auto ? "Auto" : "Strict";
        return new CommandValidation(true, true, $"{reason}. {modeText} mode requires explicit user approval.", classification);
    }

    private static bool TryParse(string command, out ScriptBlockAst? ast, out string? error)
    {
        ast = Parser.ParseInput(command, out _, out var parseErrors);
        if (parseErrors.Length == 0)
        {
            error = null;
            return true;
        }

        error = parseErrors[0].Message;
        return false;
    }

    private static List<string> GetCommandNames(ScriptBlockAst ast) =>
        ast.FindAll(node => node is CommandAst, searchNestedScriptBlocks: true)
            .OfType<CommandAst>()
            .Select(node => node.GetCommandName())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();

    private static bool IsDangerousCommand(string commandName) =>
        DangerousVerbPrefixes.Any(prefix => commandName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsDangerousPrefix(string prefix) =>
        DangerousVerbPrefixes.Any(dangerous => prefix.Equals(dangerous.TrimEnd('-'), StringComparison.OrdinalIgnoreCase)
            || prefix.Equals(dangerous, StringComparison.OrdinalIgnoreCase));
}
