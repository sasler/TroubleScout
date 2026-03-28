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
        "Mount-", "Dismount-", "Repair-"
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
            return new CommandValidation(false, false, "Command cannot be empty");
        }

        if (_configurationName != null)
        {
            return _jeaAllowedCommands != null
                ? ValidateJeaCommand(command)
                : new CommandValidation(false, false, "JEA command discovery has not completed for this session.");
        }

        var isMultiStatement = command.Contains('\n') || command.Contains(';');

        if (isMultiStatement)
        {
            if (IsReadOnlyScript(command))
            {
                return new CommandValidation(true, false);
            }

            return GetMutatingCommandValidation("Script contains commands that can modify system state");
        }

        if (command.Contains('|'))
        {
            return IsReadOnlyScript(command)
                ? new CommandValidation(true, false)
                : GetMutatingCommandValidation("Pipeline contains commands that can modify system state");
        }

        var cmdletName = ExtractCmdletName(command);

        if (IsSimpleReadOnlyExpression(command))
        {
            return new CommandValidation(true, false);
        }

        if (string.IsNullOrEmpty(cmdletName))
        {
            return new CommandValidation(false, true, "Could not parse command - requires approval");
        }

        if (BlockedCommands.Contains(cmdletName, StringComparer.OrdinalIgnoreCase))
        {
            return new CommandValidation(false, false, $"Command '{cmdletName}' is blocked for security reasons");
        }

        if (IsReadOnlySingleCommand(cmdletName))
        {
            return new CommandValidation(true, false);
        }

        return GetMutatingCommandValidation($"Command '{cmdletName}' is not a read-only command");
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
            if (string.IsNullOrEmpty(prefix))
            {
                return false;
            }

            if (DangerousVerbPrefixes.Any(dv => prefix.Equals(dv.TrimEnd('-'), StringComparison.OrdinalIgnoreCase)
                                              || prefix.Equals(dv, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return normalizedCmdletName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return normalizedCmdletName.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase);
    }

    internal static string ExtractCmdletName(string command)
    {
        var trimmed = command.Trim();
        var firstPart = trimmed.Split('|')[0].Trim();
        var parts = firstPart.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        return parts.Length > 0 ? parts[0] : string.Empty;
    }

    private CommandValidation ValidateJeaCommand(string command)
    {
        var cmdlets = ExtractCommandPositionCmdlets(command);
        if (cmdlets.Count == 0)
        {
            return new CommandValidation(false, false,
                $"Command does not contain a recognized JEA cmdlet for session '{_configurationName}'.");
        }

        foreach (var cmdlet in cmdlets)
        {
            if (BlockedCommands.Contains(cmdlet, StringComparer.OrdinalIgnoreCase))
            {
                return new CommandValidation(false, false, $"Command '{cmdlet}' is blocked for security reasons");
            }

            if (!_jeaAllowedCommands!.Contains(cmdlet))
            {
                return new CommandValidation(false, false,
                    $"Command '{cmdlet}' is not available in JEA session '{_configurationName}'.");
            }
        }

        return new CommandValidation(true, false);
    }

    private static List<string> ExtractCommandPositionCmdlets(string command)
    {
        var cmdlets = new List<string>();
        var statements = command
            .Replace("\r\n", "\n")
            .Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s) && !s.StartsWith("#"));

        foreach (var statement in statements)
        {
            if (statement.StartsWith("$") || statement.StartsWith("[") || statement.StartsWith("@") ||
                statement.StartsWith("{") || statement.StartsWith("}") || statement.StartsWith("(") ||
                statement.StartsWith(")"))
            {
                continue;
            }

            var pipeParts = statement.Split('|').Select(p => p.Trim());
            foreach (var part in pipeParts)
            {
                if (string.IsNullOrEmpty(part) || part.StartsWith("{") || part.StartsWith("}"))
                {
                    continue;
                }

                var words = part.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0)
                {
                    continue;
                }

                var firstToken = words[0];
                if (firstToken.Contains('-') && !firstToken.StartsWith("$") && !firstToken.StartsWith("["))
                {
                    cmdlets.Add(firstToken);
                }
            }
        }

        return cmdlets;
    }

    private static bool IsSimpleReadOnlyExpression(string command)
    {
        var trimmed = command.Trim();
        if (!trimmed.StartsWith("$", StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed.Contains(';') || trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Contains('|'))
        {
            return false;
        }

        if (trimmed.Contains("=", StringComparison.Ordinal))
        {
            return false;
        }

        if (trimmed.Contains(".Kill(", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(".Stop(", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Set-", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Remove-", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Restart-", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Start-", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Stop-", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Clear-", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Format-Volume", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private bool IsReadOnlySingleCommand(string cmdletName)
    {
        return GetSafeCommandPatterns().Any(pattern => MatchesSafeCommandPattern(cmdletName, pattern));
    }

    private bool IsReadOnlyScript(string command)
    {
        var statements = command
            .Replace("\r\n", "\n")
            .Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s) && !s.StartsWith("#"));

        foreach (var statement in statements)
        {
            if (statement.StartsWith("$") || statement.StartsWith("[") || statement.StartsWith("@") ||
                statement.StartsWith("{") || statement.StartsWith("}") || statement.StartsWith("(") ||
                statement.StartsWith(")"))
            {
                continue;
            }

            if (statement.Contains(" = ") && !statement.Contains("-"))
            {
                continue;
            }

            var pipeParts = statement.Split('|').Select(p => p.Trim());
            foreach (var part in pipeParts)
            {
                if (string.IsNullOrEmpty(part))
                {
                    continue;
                }

                if (part.StartsWith("{") || part.StartsWith("}") || part == "{" || part == "}")
                {
                    continue;
                }

                if (part.Contains("{") && part.Contains("}"))
                {
                    var scriptBlockContent = part.Substring(part.IndexOf('{') + 1, part.LastIndexOf('}') - part.IndexOf('{') - 1);
                    if (scriptBlockContent.Contains(".Kill(", StringComparison.OrdinalIgnoreCase) ||
                        scriptBlockContent.Contains(".Stop(", StringComparison.OrdinalIgnoreCase) ||
                        scriptBlockContent.Contains("Stop-", StringComparison.OrdinalIgnoreCase) ||
                        scriptBlockContent.Contains("Restart-", StringComparison.OrdinalIgnoreCase) ||
                        scriptBlockContent.Contains("Remove-", StringComparison.OrdinalIgnoreCase) ||
                        scriptBlockContent.Contains("Set-", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                var words = part.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0)
                {
                    continue;
                }

                var cmdlet = words[0];
                if (!cmdlet.Contains('-') || cmdlet.StartsWith("$") || cmdlet.StartsWith("["))
                {
                    continue;
                }

                if (BlockedCommands.Contains(cmdlet, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!IsReadOnlySingleCommand(cmdlet))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private IReadOnlyList<string> GetSafeCommandPatterns()
    {
        return _customSafeCommands ?? DefaultSafeCommands;
    }

    private CommandValidation GetMutatingCommandValidation(string baseReason)
    {
        return _executionMode switch
        {
            ExecutionMode.Safe => new CommandValidation(true, true, $"{baseReason}. Safe mode requires explicit user approval."),
            ExecutionMode.Yolo => new CommandValidation(true, false, $"{baseReason}. YOLO mode allows execution without confirmation."),
            _ => new CommandValidation(true, true, $"{baseReason}. Requires user approval.")
        };
    }
}
