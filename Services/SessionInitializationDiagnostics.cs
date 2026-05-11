using TroubleScout.UI;

namespace TroubleScout.Services;

internal static class SessionInitializationDiagnostics
{
    internal static string TrimSingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Unknown error";
        }

        var trimmed = text.Trim();
        var newlineIndex = trimmed.IndexOfAny(['\r', '\n']);
        return newlineIndex < 0 ? trimmed : trimmed[..newlineIndex].Trim();
    }

    internal static async Task ShowCopilotInitializationFailureAsync(
        string baseMessage,
        bool debugMode,
        Exception? exception = null,
        bool includeDiagnostics = false)
    {
        var message = baseMessage;

        if (includeDiagnostics)
        {
            var diagnostics = await RunSimpleStartupDiagnosticsAsync();
            message += "\n\nStartup diagnostics:\n" + string.Join("\n", diagnostics);
        }

        if (debugMode && exception != null)
        {
            message += "\n\nTechnical details:\n" + exception;
        }

        ConsoleUI.ShowError("Initialization Failed", message);
    }

    internal static string BuildProtocolMismatchMessage(CopilotPrerequisiteReport report, bool includeTechnicalDetails)
    {
        var message = "Copilot SDK protocol version mismatch detected.\n\n" +
               "Ensure Copilot CLI prerequisites are installed and compatible:\n" +
               $"  1. Install Node.js {CopilotCliResolver.MinSupportedNodeMajorVersion}+ (LTS): winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements\n" +
               "  2. Restart your terminal\n" +
               $"  3. Install or update Copilot CLI: {CopilotCliResolver.CopilotCliInstallUrl}\n" +
               "  4. copilot login\n\n" +
               "References:\n" +
               $"- {CopilotCliResolver.CopilotCliRepoUrl}\n" +
               $"- {CopilotCliResolver.CopilotCliInstallUrl}";

        if (!report.IsReady)
        {
            var diagnosticsText = includeTechnicalDetails
                ? report.ToDisplayText(includeWarnings: true)
                : string.Join(Environment.NewLine, report.Issues.Select(issue => $"- {issue.Title}"));
            message += "\n\nPrerequisite diagnostics:\n" + diagnosticsText;
        }

        return message;
    }

    internal static string BuildActionableInitializationMessage(Exception ex, CopilotPrerequisiteReport report, bool includeTechnicalDetails)
    {
        if (ex is InvalidOperationException invalidOp &&
            invalidOp.Message.Contains("protocol version mismatch", StringComparison.OrdinalIgnoreCase))
        {
            return BuildProtocolMismatchMessage(report, includeTechnicalDetails);
        }

        if (!report.IsReady)
        {
            var diagnosticsText = includeTechnicalDetails
                ? report.ToDisplayText(includeWarnings: true)
                : string.Join(Environment.NewLine, report.Issues.Select(issue => $"- {issue.Title}"));

            return "Copilot CLI prerequisites are not ready.\n\n" +
                   "Prerequisite diagnostics:\n" +
                   diagnosticsText;
        }

        var message = ex.Message;
        if (message.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            return "Copilot CLI is installed but not authenticated.\n\n" +
                   "To continue:\n" +
                   "  1. Run: copilot login\n" +
                   "  2. Re-run TroubleScout";
        }

        if (message.Contains("failed to start cli", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cli process exited", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("communication error with copilot cli", StringComparison.OrdinalIgnoreCase))
        {
            var startupFailureMessage = "The Copilot CLI failed during startup.\n\n" +
                                      "Try:\n" +
                                      "  - copilot --version\n" +
                                      "  - copilot login\n" +
                                      $"  - Install/update Copilot CLI: {CopilotCliResolver.CopilotCliInstallUrl}\n" +
                                      "  - Re-run TroubleScout";

            if (includeTechnicalDetails)
            {
                startupFailureMessage += $"\n\nTechnical details: {message}";
            }

            return startupFailureMessage;
        }

        if (message.Contains("node", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ENOENT", StringComparison.OrdinalIgnoreCase))
        {
            return "The Copilot CLI runtime is unavailable.\n\n" +
                   "Install/update prerequisites:\n" +
                   $"  1. Install Node.js {CopilotCliResolver.MinSupportedNodeMajorVersion}+ (LTS): winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements\n" +
                   "  2. Restart your terminal\n" +
                   $"  3. Install/update Copilot CLI: {CopilotCliResolver.CopilotCliInstallUrl}\n" +
                   "  4. copilot login";
        }

        var result = "TroubleScout could not initialize the Copilot session.\n\n" +
                     "Try:\n" +
                     "  - copilot --version\n" +
                     "  - copilot login\n" +
                     $"  - winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements\n" +
                     $"  - Install/update Copilot CLI: {CopilotCliResolver.CopilotCliInstallUrl}";

        if (includeTechnicalDetails)
        {
            result += $"\n\nTechnical details: {message}";
        }

        return result;
    }

    private static async Task<IReadOnlyList<string>> RunSimpleStartupDiagnosticsAsync()
    {
        var diagnostics = new List<string>();
        var cliPath = CopilotCliResolver.CopilotCliPathResolver();
        var nodeRuntimeRequired = await CopilotCliResolver.CliPathRequiresNodeRuntimeAsync(cliPath);

        var (copilotCommand, copilotArguments) = CopilotCliResolver.BuildCopilotCommand(cliPath, "--version");
        var copilotVersion = await CopilotCliResolver.ProcessRunnerResolver(copilotCommand, copilotArguments);
        diagnostics.Add(FormatDiagnosticLine($"{copilotCommand} {copilotArguments}", copilotVersion));

        var nodeVersion = await CopilotCliResolver.ProcessRunnerResolver("node", "--version");
        diagnostics.Add(FormatDiagnosticLine("node --version", nodeVersion));
        if (!nodeRuntimeRequired && nodeVersion.ExitCode != 0)
        {
            diagnostics.Add("- Note: Node.js is only required for some Copilot CLI installations.");
        }

        return diagnostics;
    }

    private static string FormatDiagnosticLine(string command, (int ExitCode, string StdOut, string StdErr) result)
    {
        var output = TrimSingleLine(string.IsNullOrWhiteSpace(result.StdOut) ? result.StdErr : result.StdOut);
        return result.ExitCode == 0
            ? $"- {command}: {output}"
            : $"- {command}: failed ({output})";
    }
}
