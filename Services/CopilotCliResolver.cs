using System.Runtime.InteropServices;

namespace TroubleScout.Services;

public sealed record CopilotPrerequisiteIssue(string Title, string Details, bool IsBlocking);

public sealed record CopilotPrerequisiteReport(IReadOnlyList<CopilotPrerequisiteIssue> Issues)
{
    public bool IsReady => Issues.All(issue => !issue.IsBlocking);

    public string ToDisplayText(bool includeWarnings = true)
    {
        var lines = new List<string>();
        var relevantIssues = includeWarnings
            ? Issues
            : Issues.Where(issue => issue.IsBlocking).ToList();

        foreach (var issue in relevantIssues)
        {
            lines.Add($"- {issue.Title}");
            lines.Add($"  {issue.Details}");
            lines.Add(string.Empty);
        }

        return lines.Count == 0
            ? "All Copilot prerequisites look good."
            : string.Join(Environment.NewLine, lines).TrimEnd();
    }
}

public static class CopilotCliResolver
{
    internal const string CopilotCliRepoUrl = "https://github.com/github/copilot-cli";
    internal const string CopilotCliInstallUrl = "https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli";
    internal const int MinSupportedNodeMajorVersion = 24;

    internal static Func<string> CopilotCliPathResolver { get; set; } = GetCopilotCliPath;
    internal static Func<string, bool> FileExistsResolver { get; set; } = File.Exists;
    internal static Func<string, string, Task<(int ExitCode, string StdOut, string StdErr)>> ProcessRunnerResolver { get; set; } = RunProcessAsync;

    /// <summary>
    /// Get the path to the Copilot CLI.
    /// </summary>
    internal static string GetCopilotCliPath()
    {
        var preferredPath = TryResolvePreferredCopilotCliPath();
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            return preferredPath;
        }

        return "copilot";
    }

    internal static string? TryResolvePreferredCopilotCliPath()
    {
        var envPath = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        if (!string.IsNullOrEmpty(envPath) && FileExistsResolver(envPath))
        {
            return envPath;
        }

        var bundledPath = TryResolveBundledCopilotCliPath();
        if (!string.IsNullOrWhiteSpace(bundledPath))
        {
            return bundledPath;
        }

        var installedPath = TryResolveInstalledCopilotCliPath();
        if (!string.IsNullOrWhiteSpace(installedPath))
        {
            return installedPath;
        }

        return null;
    }

    private static string? TryResolveInstalledCopilotCliPath()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        var searchDirs = pathValue
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim().Trim('"'))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var directory in searchDirs)
        {
            var exePath = Path.Combine(directory, "copilot.exe");
            if (File.Exists(exePath))
            {
                return exePath;
            }
        }

        foreach (var directory in searchDirs)
        {
            var npmLoaderPath = Path.Combine(directory, "node_modules", "@github", "copilot", "npm-loader.js");
            if (File.Exists(npmLoaderPath))
            {
                return npmLoaderPath;
            }

            var indexPath = Path.Combine(directory, "node_modules", "@github", "copilot", "index.js");
            if (File.Exists(indexPath))
            {
                return indexPath;
            }
        }

        return null;
    }

    private static string? TryResolveBundledCopilotCliPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };
        var runtimeIdentifier = $"win-{architecture}";

        var candidates = new[]
        {
            Path.Combine(baseDirectory, "copilot.exe"),
            Path.Combine(baseDirectory, $"copilot-{runtimeIdentifier}.exe"),
            Path.Combine(baseDirectory, "vendor", "copilot.exe"),
            Path.Combine(baseDirectory, "vendor", $"copilot-{runtimeIdentifier}.exe"),
            Path.Combine(baseDirectory, "runtimes", runtimeIdentifier, "native", "copilot.exe"),
            Path.Combine(baseDirectory, "runtimes", runtimeIdentifier, "native", $"copilot-{runtimeIdentifier}.exe")
        };

        foreach (var candidate in candidates)
        {
            if (FileExistsResolver(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Check if Copilot SDK CLI is available
    /// </summary>
    public static async Task<bool> CheckCopilotAvailableAsync()
    {
        var report = await ValidateCopilotPrerequisitesAsync();
        return report.IsReady;
    }

    public static async Task<CopilotPrerequisiteReport> ValidateCopilotPrerequisitesAsync()
    {
        var issues = new List<CopilotPrerequisiteIssue>();

        try
        {
            var cliPath = CopilotCliPathResolver();

            if (Path.IsPathRooted(cliPath) && !FileExistsResolver(cliPath))
            {
                issues.Add(new CopilotPrerequisiteIssue(
                    "Copilot CLI binary was not found",
                    "TroubleScout could not locate the configured Copilot CLI path.\n" +
                    $"Configured path: {cliPath}\n\n" +
                    "If you are using a bundled deployment, ensure the CLI binary is included in the app folder.\n" +
                    "If you are using a system installation, set COPILOT_CLI_PATH or install Copilot CLI globally.",
                    true));

                return new CopilotPrerequisiteReport(issues);
            }

            if (cliPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && !FileExistsResolver(cliPath))
            {
                issues.Add(new CopilotPrerequisiteIssue(
                    "Copilot CLI is not installed",
                    "Install the prerequisites, then authenticate:\n" +
                    "  1. Install Node.js: https://nodejs.org/\n" +
                    $"  2. Install or update Copilot CLI: {CopilotCliInstallUrl}\n" +
                    "  3. Authenticate: copilot login\n" +
                    $"\nReferences:\n- {CopilotCliRepoUrl}\n- {CopilotCliInstallUrl}",
                    true));

                return new CopilotPrerequisiteReport(issues);
            }

            if (await CliPathRequiresNodeRuntimeAsync(cliPath))
            {
                var nodeVersion = await ProcessRunnerResolver("node", "--version");
                if (nodeVersion.ExitCode != 0)
                {
                    issues.Add(new CopilotPrerequisiteIssue(
                        "Node.js runtime is missing",
                        "Copilot CLI requires Node.js on this machine.\n" +
                        "Install Node.js from https://nodejs.org/ and restart your terminal.\n" +
                        $"Detection details: {TrimSingleLine(nodeVersion.StdErr)}",
                        true));

                    return new CopilotPrerequisiteReport(issues);
                }

                var detectedVersion = TrimSingleLine(nodeVersion.StdOut);
                var nodeMajorVersion = ParseNodeMajorVersion(detectedVersion);
                if (!nodeMajorVersion.HasValue || nodeMajorVersion.Value < MinSupportedNodeMajorVersion)
                {
                    issues.Add(new CopilotPrerequisiteIssue(
                        "Node.js version is unsupported",
                        $"Your Copilot CLI path appears to use the Node.js runtime and requires Node.js {MinSupportedNodeMajorVersion}+ (LTS recommended).\n" +
                        $"Detected: {detectedVersion}\n\n" +
                        "Fix on Windows:\n" +
                        "  1. winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements\n" +
                        "  2. Restart your terminal\n" +
                        $"  3. Install or update Copilot CLI: {CopilotCliInstallUrl}\n" +
                        "  4. Re-run TroubleScout\n\n" +
                        $"References:\n- {CopilotCliRepoUrl}\n- {CopilotCliInstallUrl}",
                        true));

                    return new CopilotPrerequisiteReport(issues);
                }
            }

            var (versionCommand, versionArguments) = BuildCopilotCommand(cliPath, "--version");
            var versionResult = await ProcessRunnerResolver(versionCommand, versionArguments);

            if (versionResult.ExitCode != 0)
            {
                issues.Add(new CopilotPrerequisiteIssue(
                    "Copilot CLI command failed",
                    "TroubleScout could not run the Copilot CLI version check.\n" +
                    "Try these commands:\n" +
                    $"  - Install/update Copilot CLI: {CopilotCliInstallUrl}\n" +
                    "  - copilot --version\n" +
                    "  - copilot login\n" +
                    $"\nCLI path used: {cliPath}\n" +
                    $"Error: {TrimSingleLine(string.IsNullOrWhiteSpace(versionResult.StdErr) ? versionResult.StdOut : versionResult.StdErr)}\n" +
                    $"\nReferences:\n- {CopilotCliRepoUrl}\n- {CopilotCliInstallUrl}",
                    true));

                return new CopilotPrerequisiteReport(issues);
            }

            var powerShellWarning = await DetectPowerShellVersionWarningAsync();
            if (!string.IsNullOrWhiteSpace(powerShellWarning))
            {
                issues.Add(new CopilotPrerequisiteIssue(
                    "PowerShell version is below recommended",
                    powerShellWarning,
                    false));
            }

            return new CopilotPrerequisiteReport(issues);
        }
        catch (Exception ex)
        {
            issues.Add(new CopilotPrerequisiteIssue(
                "Could not fully validate Copilot prerequisites",
                "TroubleScout could not complete the prerequisite check. Verify manually:\n" +
                "  - copilot --version\n" +
                $"  - Install/update Copilot CLI: {CopilotCliInstallUrl}\n" +
                $"Error: {TrimSingleLine(ex.Message)}",
                true));
            return new CopilotPrerequisiteReport(issues);
        }
    }

    internal static void ResetPrerequisiteValidationResolvers()
    {
        CopilotCliPathResolver = GetCopilotCliPath;
        FileExistsResolver = File.Exists;
        ProcessRunnerResolver = RunProcessAsync;
    }

    internal static async Task<bool> CliPathRequiresNodeRuntimeAsync(string cliPath)
    {
        if (CliPathRequiresNodeRuntime(cliPath))
        {
            return true;
        }

        if (!cliPath.Equals("copilot", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var whereResult = await ProcessRunnerResolver("cmd.exe", "/c where copilot");
        if (whereResult.ExitCode != 0)
        {
            return true;
        }

        var firstPath = whereResult.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim().Trim('"'))
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstPath))
        {
            return true;
        }

        var extension = Path.GetExtension(firstPath);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
               string.IsNullOrWhiteSpace(extension);
    }

    internal static (string Command, string Arguments) BuildCopilotCommand(string cliPath, string arguments)
    {
        if (cliPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            return ("node", $"\"{cliPath}\" {arguments}");
        }

        if (cliPath.Equals("copilot", StringComparison.OrdinalIgnoreCase))
        {
            return ("cmd.exe", $"/c copilot {arguments}");
        }

        return ("cmd.exe", $"/c \"\"{cliPath}\" {arguments}\"");
    }

    internal static async Task<string?> DetectPowerShellVersionWarningAsync()
    {
        var (shell, versionText) = await GetPowerShellVersionTextAsync();
        if (string.IsNullOrWhiteSpace(versionText))
            return null;

        var majorVersion = ParsePowerShellMajorVersion(versionText);
        if (!majorVersion.HasValue || majorVersion.Value >= 7)
            return null;

        return $"Detected {shell} {versionText}. Copilot CLI on Windows requires PowerShell 6+, and TroubleScout recommends PowerShell 7+.";
    }

    private static bool CliPathRequiresNodeRuntime(string cliPath)
    {
        if (string.IsNullOrWhiteSpace(cliPath))
            return true;

        if (cliPath.Equals("copilot", StringComparison.OrdinalIgnoreCase))
            return false;

        return cliPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
               cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
               cliPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(string fileName, string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                return (-1, string.Empty, "Failed to start process.");
            }

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                var timedOutStdOut = await stdOutTask;
                var timedOutStdErr = await stdErrTask;
                return (-1, timedOutStdOut,
                    string.IsNullOrWhiteSpace(timedOutStdErr)
                        ? "Process timed out after 10 seconds."
                        : TrimSingleLine(timedOutStdErr));
            }

            return (process.ExitCode, await stdOutTask, await stdErrTask);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    private static async Task<(string Shell, string? VersionText)> GetPowerShellVersionTextAsync()
    {
        var pwshVersion = await ProcessRunnerResolver("pwsh", "--version");
        if (pwshVersion.ExitCode == 0)
        {
            return ("pwsh", TrimSingleLine(pwshVersion.StdOut));
        }

        var windowsPowerShellVersion = await ProcessRunnerResolver(
            "powershell",
            "-NoLogo -NoProfile -Command \"$PSVersionTable.PSVersion.ToString()\"");

        if (windowsPowerShellVersion.ExitCode == 0)
        {
            return ("powershell", TrimSingleLine(windowsPowerShellVersion.StdOut));
        }

        return (string.Empty, null);
    }

    private static int? ParsePowerShellMajorVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
            return null;

        var trimmed = versionText.Trim();
        var dotIndex = trimmed.IndexOf('.');
        var majorPart = dotIndex >= 0 ? trimmed[..dotIndex] : trimmed;
        return int.TryParse(majorPart, out var major) ? major : null;
    }

    private static int? ParseNodeMajorVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
            return null;

        var trimmed = versionText.Trim();
        if (trimmed.StartsWith('v'))
        {
            trimmed = trimmed[1..];
        }

        var dotIndex = trimmed.IndexOf('.');
        var majorPart = dotIndex >= 0 ? trimmed[..dotIndex] : trimmed;

        return int.TryParse(majorPart, out var major) ? major : null;
    }

    private static string TrimSingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Unknown error";

        var trimmed = text.Trim();
        var newlineIndex = trimmed.IndexOfAny(['\r', '\n']);
        return newlineIndex < 0 ? trimmed : trimmed[..newlineIndex].Trim();
    }
}
