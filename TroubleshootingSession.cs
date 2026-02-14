using System.Globalization;
using System.Reflection;
using System.Text.Json;
using GitHub.Copilot.SDK;
using TroubleScout.Services;
using TroubleScout.Tools;
using TroubleScout.UI;

namespace TroubleScout;

/// <summary>
/// Manages the Copilot-powered troubleshooting session
/// </summary>
public class TroubleshootingSession : IAsyncDisposable
{
    private const string CopilotCliRepoUrl = "https://github.com/github/copilot-cli";
    private const string CopilotCliInstallUrl = "https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli";
    private const int MinSupportedNodeMajorVersion = 24;

    internal static Func<string> CopilotCliPathResolver { get; set; } = GetCopilotCliPath;
    internal static Func<string, bool> FileExistsResolver { get; set; } = File.Exists;
    internal static Func<string, string, Task<(int ExitCode, string StdOut, string StdErr)>> ProcessRunnerResolver { get; set; } = RunProcessAsync;

    private string _targetServer;
    private PowerShellExecutor _executor;
    private DiagnosticTools _diagnosticTools;
    private CopilotClient? _copilotClient;
    private CopilotSession? _copilotSession;
    private bool _isInitialized;
    private string? _selectedModel;
    private string? _copilotVersion;
    private List<ModelInfo> _availableModels = new();
    private readonly string? _mcpConfigPath;
    private readonly List<string> _skillDirectories;
    private readonly List<string> _disabledSkills;
    private readonly List<string> _configuredMcpServers = new();
    private readonly List<string> _configuredSkills = new();
    private readonly HashSet<string> _runtimeMcpServers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _runtimeSkills = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _configurationWarnings = new();
    private readonly bool _debugMode;

    private CopilotUsageSnapshot? _lastUsage;

    private SystemMessageConfig _systemMessageConfig;

    private static readonly string[] SlashCommands =
    [
        "/help",
        "/status",
        "/clear",
        "/model",
        "/connect",
        "/capabilities",
        "/history",
        "/exit",
        "/quit"
    ];

    private SystemMessageConfig CreateSystemMessage(string targetServer)
    {
        var targetInfo = targetServer.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? "the local machine (localhost)"
            : $"the remote server: {targetServer}";

        return new SystemMessageConfig
        {
            Content = $"""
            You are TroubleScout, an expert Windows Server troubleshooting assistant. 
            Your role is to diagnose issues on Windows servers by analyzing system data and providing actionable insights.

            ## Target Server Context
            - You are currently connected to {targetInfo}
            - ALL commands and diagnostic operations will execute on this target server
            - When gathering data or making observations, you MUST always state which server the data comes from
            - Always verify that the data you receive is from the expected target server
            - If the user doesn't specify a server in their question, assume they mean the current target: {targetServer}

            ## Your Capabilities
            - Execute read-only PowerShell commands (Get-*) to gather diagnostic information from the target server
            - Analyze Windows Event Logs, services, processes, performance counters, disk space, and network configuration
            - Identify patterns, anomalies, and potential root causes
            - Provide clear, prioritized recommendations

            ## Troubleshooting Approach
            1. **Understand the Problem**: Ask clarifying questions if the issue description is vague
            2. **Gather Data**: Use the diagnostic tools to collect relevant information FROM THE TARGET SERVER
            3. **Verify Source**: Always confirm the data comes from {targetServer} by checking $env:COMPUTERNAME
            4. **Analyze**: Look for errors, warnings, resource exhaustion, or configuration issues
            5. **Diagnose**: Form hypotheses about the root cause based on evidence
            6. **Recommend**: Provide clear, actionable next steps

            ## Response Format
            - ALWAYS start your response by confirming which server you're analyzing (e.g., "Analyzing {targetServer}...")
            - Be concise but thorough
            - Use bullet points for lists
            - Highlight critical findings with **bold**
            - For remediation commands (non-Get commands), explain what they do and why they're needed
            - Always explain your reasoning
            - When presenting diagnostic data, include the source server name in your explanation

            ## Safety
            - Only read-only Get-* commands execute automatically
            - Any remediation commands (Start-, Stop-, Set-, Restart-, etc.) require user approval
            - Never suggest commands that could cause data loss without clear warnings
            - Always consider the impact of recommended actions

            Remember: Your goal is to help the user understand what's wrong with {targetServer} and guide them to a solution, 
            not just dump raw data. Interpret the findings and provide expert analysis. Always maintain awareness of which 
            server you're working on.
            """
        };
    }

    public TroubleshootingSession(
        string targetServer,
        string? model = null,
        string? mcpConfigPath = null,
        IReadOnlyList<string>? skillDirectories = null,
        IReadOnlyList<string>? disabledSkills = null,
        bool debugMode = false)
    {
        _targetServer = string.IsNullOrWhiteSpace(targetServer) ? "localhost" : targetServer;
        _requestedModel = model;
        _mcpConfigPath = mcpConfigPath;
        _skillDirectories = skillDirectories?.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        _disabledSkills = disabledSkills?.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        _debugMode = debugMode;
        _systemMessageConfig = CreateSystemMessage(_targetServer);
        _executor = new PowerShellExecutor(_targetServer);
        _diagnosticTools = new DiagnosticTools(_executor, PromptApprovalAsync, _targetServer);
    }

    private readonly string? _requestedModel;

    public string TargetServer => _targetServer;
    public string ConnectionMode => _executor.GetConnectionMode();
    public string SelectedModel => GetModelDisplayName(_selectedModel) ?? "default";
    public string CopilotVersion => _copilotVersion ?? "unknown";
    public IReadOnlyList<string> ConfiguredMcpServers => _configuredMcpServers;
    public IReadOnlyList<string> RuntimeMcpServers => _runtimeMcpServers.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    public IReadOnlyList<string> ConfiguredSkills => _configuredSkills;
    public IReadOnlyList<string> RuntimeSkills => _runtimeSkills.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    public IReadOnlyList<string> ConfigurationWarnings => _configurationWarnings;

    private sealed record CopilotUsageSnapshot(
        int? PromptTokens,
        int? CompletionTokens,
        int? TotalTokens,
        int? InputTokens,
        int? OutputTokens,
        int? MaxContextTokens,
        int? UsedContextTokens,
        int? FreeContextTokens)
    {
        public bool HasAny => PromptTokens.HasValue || CompletionTokens.HasValue || TotalTokens.HasValue ||
                              InputTokens.HasValue || OutputTokens.HasValue || MaxContextTokens.HasValue ||
                              UsedContextTokens.HasValue || FreeContextTokens.HasValue;
    }

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

    /// <summary>
    /// Initialize the session and establish connections
    /// </summary>
    public async Task<bool> InitializeAsync(Action<string>? updateStatus = null)
    {
        if (_isInitialized)
            return true;

        var copilotInitializationStarted = false;

        try
        {
            // Test PowerShell connection and verify target
            updateStatus?.Invoke($"Connecting to {_targetServer}...");
            
            var (connectionSuccess, connectionError) = await _executor.TestConnectionAsync();
            if (!connectionSuccess)
            {
                ConsoleUI.ShowError("Connection Failed", connectionError ?? $"Unable to connect to {_targetServer}");
                return false;
            }

            // Show verified connection
            updateStatus?.Invoke($"Connected to {_executor.ActualComputerName}...");

            await WarnIfPowerShellVersionIsOldAsync();

            // Initialize Copilot client
            updateStatus?.Invoke("Starting Copilot SDK...");
            copilotInitializationStarted = true;
            
            // Resolve Copilot CLI path (env override, known locations, then PATH)
            var cliPath = GetCopilotCliPath();
            
            _copilotClient = new CopilotClient(new CopilotClientOptions
            {
                CliPath = cliPath,
                LogLevel = "info"
            });

            try
            {
                await _copilotClient.StartAsync();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("protocol version mismatch", StringComparison.OrdinalIgnoreCase))
            {
                var report = await ValidateCopilotPrerequisitesAsync();
                await ShowCopilotInitializationFailureAsync(
                    BuildProtocolMismatchMessage(report, _debugMode),
                    ex,
                    includeDiagnostics: true);
                _copilotClient = null;
                return false;
            }

            var authStatus = await _copilotClient.GetAuthStatusAsync();
            if (!authStatus.IsAuthenticated)
            {
                await ShowCopilotInitializationFailureAsync(
                    "Copilot CLI is not authenticated.\n\nRun: copilot login",
                    includeDiagnostics: true);
                return false;
            }

            updateStatus?.Invoke("Fetching available models...");
            _availableModels = await _copilotClient.ListModelsAsync();

            if (_availableModels.Count == 0)
            {
                await ShowCopilotInitializationFailureAsync(
                    "No models were returned by Copilot CLI. Ensure you are authenticated and your subscription has model access.",
                    includeDiagnostics: true);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_requestedModel) && _availableModels.All(m => m.Id != _requestedModel))
            {
                ConsoleUI.ShowError("Invalid Model", $"The requested model '{_requestedModel}' is not available for your account.");
                return false;
            }

            if (!await CreateCopilotSessionAsync(_requestedModel, updateStatus))
            {
                return false;
            }
            
            _isInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            if (copilotInitializationStarted)
            {
                var report = await ValidateCopilotPrerequisitesAsync();
                await ShowCopilotInitializationFailureAsync(
                    BuildActionableInitializationMessage(ex, report, _debugMode),
                    ex,
                    includeDiagnostics: true);
                return false;
            }

            ConsoleUI.ShowError("Initialization Failed", "TroubleScout could not complete startup.");
            if (_debugMode)
            {
                ConsoleUI.ShowWarning($"Technical details: {TrimSingleLine(ex.Message)}");
            }
            return false;
        }
    }

    /// <summary>
    /// Change the AI model by creating a new session
    /// </summary>
    public async Task<bool> ChangeModelAsync(string newModel, Action<string>? updateStatus = null)
    {
        if (_copilotClient == null)
        {
            ConsoleUI.ShowError("Not Connected", "Copilot client not initialized");
            return false;
        }

        if (_availableModels.Count == 0 || _availableModels.All(m => m.Id != newModel))
        {
            ConsoleUI.ShowError("Invalid Model", $"The selected model '{newModel}' is not available for your account.");
            return false;
        }

        try
        {
            // Dispose existing session
            if (_copilotSession != null)
            {
                updateStatus?.Invoke("Closing current session...");
                await _copilotSession.DisposeAsync();
                _copilotSession = null;
            }

            if (!await CreateCopilotSessionAsync(newModel, updateStatus))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            ConsoleUI.ShowError("Model Change Failed", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Get the path to the Copilot CLI from the SDK package
    /// </summary>
    internal static string GetCopilotCliPath()
    {
        // Check for COPILOT_CLI_PATH environment variable first
        var envPath = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        // Look for the CLI in common npm global locations
        var npmGlobalRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        
        // Only check npm paths if we have a valid ApplicationData folder
        if (!string.IsNullOrEmpty(npmGlobalRoot))
        {
            var possiblePaths = new[]
            {
                // npm global on Windows
                Path.Combine(npmGlobalRoot, "npm", "node_modules", "@github", "copilot-sdk", "node_modules", "@github", "copilot", "index.js"),
                Path.Combine(npmGlobalRoot, "npm", "node_modules", "@github", "copilot", "index.js")
            };

            // Use explicit filtering to find existing paths
            var existingPath = possiblePaths.Where(File.Exists).FirstOrDefault();
            if (existingPath != null)
            {
                return existingPath;
            }
        }

        // Default to copilot in PATH
        return "copilot";
    }

    private string? GetModelDisplayName(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var model = _availableModels.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        return model?.Name ?? modelId;
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

            if (CliPathRequiresNodeRuntime(cliPath))
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

    private static (string Command, string Arguments) BuildCopilotCommand(string cliPath, string arguments)
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
                    // Ignore kill failures during timeout handling.
                }

                return (-1, await stdOutTask, "Process timed out after 10 seconds.");
            }

            return (process.ExitCode, await stdOutTask, await stdErrTask);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    internal static void ResetPrerequisiteValidationResolvers()
    {
        CopilotCliPathResolver = GetCopilotCliPath;
        FileExistsResolver = File.Exists;
        ProcessRunnerResolver = RunProcessAsync;
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

    private async Task WarnIfPowerShellVersionIsOldAsync()
    {
        var warning = await DetectPowerShellVersionWarningAsync();
        if (!string.IsNullOrWhiteSpace(warning))
        {
            ConsoleUI.ShowWarning(warning);
        }
    }

    private static async Task<string?> DetectPowerShellVersionWarningAsync()
    {
        var (shell, versionText) = await GetPowerShellVersionTextAsync();
        if (string.IsNullOrWhiteSpace(versionText))
            return null;

        var majorVersion = ParsePowerShellMajorVersion(versionText);
        if (!majorVersion.HasValue || majorVersion.Value >= 7)
            return null;

        return $"Detected {shell} {versionText}. Copilot CLI on Windows recommends PowerShell 6+ (PowerShell 7+ preferred).";
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

    private static async Task<IReadOnlyList<string>> RunSimpleStartupDiagnosticsAsync()
    {
        var diagnostics = new List<string>();

        var copilotVersion = await ProcessRunnerResolver("cmd.exe", "/c copilot --version");
        diagnostics.Add(FormatDiagnosticLine("copilot --version", copilotVersion));

        var nodeVersion = await ProcessRunnerResolver("node", "--version");
        diagnostics.Add(FormatDiagnosticLine("node --version", nodeVersion));

        return diagnostics;
    }

    private static string FormatDiagnosticLine(string command, (int ExitCode, string StdOut, string StdErr) result)
    {
        var output = TrimSingleLine(string.IsNullOrWhiteSpace(result.StdOut) ? result.StdErr : result.StdOut);
        return result.ExitCode == 0
            ? $"- {command}: {output}"
            : $"- {command}: failed ({output})";
    }

    private async Task ShowCopilotInitializationFailureAsync(
        string baseMessage,
        Exception? exception = null,
        bool includeDiagnostics = false)
    {
        var message = baseMessage;

        if (includeDiagnostics)
        {
            var diagnostics = await RunSimpleStartupDiagnosticsAsync();
            message += "\n\nStartup diagnostics:\n" + string.Join("\n", diagnostics);
        }

        if (_debugMode && exception != null)
        {
            message += "\n\nTechnical details:\n" + exception;
        }

        ConsoleUI.ShowError("Initialization Failed", message);
    }

    private static string BuildProtocolMismatchMessage(CopilotPrerequisiteReport report, bool includeTechnicalDetails)
    {
        var message = "Copilot SDK protocol version mismatch detected.\n\n" +
               "Ensure Copilot CLI prerequisites are installed and compatible:\n" +
               $"  1. Install Node.js {MinSupportedNodeMajorVersion}+ (LTS): winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements\n" +
               "  2. Restart your terminal\n" +
                             $"  3. Install or update Copilot CLI: {CopilotCliInstallUrl}\n" +
               "  4. copilot login\n\n" +
               "References:\n" +
               $"- {CopilotCliRepoUrl}\n" +
                             $"- {CopilotCliInstallUrl}";

        if (!report.IsReady)
        {
            var diagnosticsText = includeTechnicalDetails
                ? report.ToDisplayText(includeWarnings: true)
                : string.Join(Environment.NewLine, report.Issues.Select(issue => $"- {issue.Title}"));
            message += "\n\nPrerequisite diagnostics:\n" + diagnosticsText;
        }

        return message;
    }

    private static string BuildActionableInitializationMessage(Exception ex, CopilotPrerequisiteReport report, bool includeTechnicalDetails)
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

            return "TroubleScout could not initialize the Copilot session.\n\n" +
                   "Prerequisite diagnostics:\n" +
                   diagnosticsText;
        }

        var message = ex.Message;
        if (message.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            return "Copilot CLI is not authenticated.\n\nRun: copilot login";
        }

        if (message.Contains("node", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ENOENT", StringComparison.OrdinalIgnoreCase))
        {
            return "The Copilot CLI runtime is unavailable.\n\n" +
                   "Install/update prerequisites:\n" +
                   $"  1. Install Node.js {MinSupportedNodeMajorVersion}+ (LTS): winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements\n" +
                   "  2. Restart your terminal\n" +
                   $"  3. Install/update Copilot CLI: {CopilotCliInstallUrl}\n" +
                   "  4. copilot login";
        }

        var result = "TroubleScout could not initialize the Copilot session.\n\n" +
                     "Try:\n" +
                     "  - copilot --version\n" +
                     "  - copilot login\n" +
                     $"  - winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements\n" +
                     $"  - Install/update Copilot CLI: {CopilotCliInstallUrl}";

        if (includeTechnicalDetails)
        {
            result += $"\n\nTechnical details: {message}";
        }

        return result;
    }

    /// <summary>
    /// Send a message and process the response with streaming
    /// </summary>
    public async Task<bool> SendMessageAsync(string userMessage)
    {
        if (_copilotSession == null)
        {
            ConsoleUI.ShowError("Not Initialized", "Session not initialized. Call InitializeAsync first.");
            return false;
        }

        try
        {
            var done = new TaskCompletionSource<bool>();
            var hasError = false;
            var hasStartedStreaming = false;
            var processedDeltaIds = new HashSet<string>();
            
            // Create a live thinking indicator (manually disposed before recursive calls)
            var thinkingIndicator = ConsoleUI.CreateLiveThinkingIndicator();
            thinkingIndicator.Start();

            // Subscribe to session events for streaming (manually disposed before recursive calls)
            var subscription = _copilotSession.On(evt =>
            {
                CaptureCapabilityUsage(evt);

                switch (evt)
                {
                    case AssistantTurnStartEvent:
                        // AI has started processing
                        thinkingIndicator.UpdateStatus("Analyzing");
                        break;
                    
                    case ToolExecutionStartEvent toolStart:
                        // Show which tool is being executed
                        thinkingIndicator.ShowToolExecution(toolStart.Data?.ToolName ?? "diagnostic");
                        break;
                    
                    case ToolExecutionCompleteEvent:
                        // Tool finished, back to thinking
                        thinkingIndicator.UpdateStatus("Processing results");
                        break;
                    
                    case AssistantMessageDeltaEvent delta:
                        // Skip if we've already processed this event (deduplicate)
                        if (!processedDeltaIds.Add(delta.Id.ToString()))
                            break;
                        
                        // First streaming chunk - stop the spinner and start response
                        if (!hasStartedStreaming)
                        {
                            hasStartedStreaming = true;
                            thinkingIndicator.StopForResponse();
                            ConsoleUI.StartAIResponse();
                        }
                        // Streaming message chunk - print incrementally
                        ConsoleUI.WriteAIResponse(delta.Data?.DeltaContent ?? "");
                        break;
                    
                    case AssistantMessageEvent msg:
                        // Final message received (non-streaming fallback)
                        if (!hasStartedStreaming && !string.IsNullOrEmpty(msg.Data?.Content))
                        {
                            thinkingIndicator.StopForResponse();
                            ConsoleUI.StartAIResponse();
                            ConsoleUI.WriteAIResponse(msg.Data.Content);
                            hasStartedStreaming = true;
                        }
                        break;
                    
                    case SessionErrorEvent errorEvent:
                        thinkingIndicator.StopForResponse();
                        ConsoleUI.EndAIResponse();
                        ConsoleUI.ShowError("Session Error", errorEvent.Data?.Message ?? "Unknown error");
                        hasError = true;
                        done.TrySetResult(false);
                        break;
                    
                    case SessionIdleEvent:
                        // Session finished processing
                        done.TrySetResult(true);
                        break;
                    case AssistantUsageEvent usageEvt:
                        CaptureUsageMetrics(usageEvt);
                        break;
                }
            });

            // Send the message
            await _copilotSession.SendAsync(new MessageOptions { Prompt = userMessage });
            
            // Wait for completion
            await done.Task;
            
            // Explicitly dispose subscription BEFORE processing approvals
            // This prevents duplicate event handling when SendMessageAsync is called recursively
            subscription.Dispose();

            if (hasStartedStreaming)
            {
                ConsoleUI.EndAIResponse();
            }
            
            // Dispose thinking indicator before processing approvals
            thinkingIndicator.Dispose();

            // Handle any pending approval commands (may call SendMessageAsync recursively)
            if (!hasError)
            {
                await ProcessPendingApprovalsAsync();
            }

            return !hasError;
        }
        catch (Exception ex)
        {
            ConsoleUI.EndAIResponse();
            ConsoleUI.ShowError("Error", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Process any commands that require user approval
    /// </summary>
    private async Task ProcessPendingApprovalsAsync()
    {
        var pending = _diagnosticTools.PendingCommands;
        if (pending.Count == 0) return;

        var commands = pending.Select(p => (p.Command, p.Reason)).ToList();
        
        if (commands.Count == 1)
        {
            var cmd = commands[0];
            if (ConsoleUI.PromptCommandApproval(cmd.Command, cmd.Reason))
            {
                ConsoleUI.ShowInfo($"Executing: {cmd.Command}");
                var result = await _diagnosticTools.ExecuteApprovedCommandAsync(pending[0]);
                ConsoleUI.ShowSuccess("Command executed");
                
                // Feed result back to the AI
                await SendMessageAsync($"The approved command '{cmd.Command}' has been executed. Result:\n{result}\n\nPlease continue your analysis with this information.");
            }
            else
            {
                ConsoleUI.ShowWarning("Command skipped by user");
                _diagnosticTools.ClearPendingCommands();
            }
        }
        else
        {
            var approved = ConsoleUI.PromptBatchApproval(commands);
            
            foreach (var index in approved)
            {
                var cmd = pending[index - 1];
                ConsoleUI.ShowInfo($"Executing: {cmd.Command}");
                var result = await _diagnosticTools.ExecuteApprovedCommandAsync(cmd);
                ConsoleUI.ShowSuccess("Command executed");
            }

            _diagnosticTools.ClearPendingCommands();

            if (approved.Count > 0)
            {
                await SendMessageAsync("The approved commands have been executed. Please continue your analysis.");
            }
        }
    }

    /// <summary>
    /// Callback for command approval prompts
    /// </summary>
    private Task<bool> PromptApprovalAsync(string command, string reason)
    {
        return Task.FromResult(ConsoleUI.PromptCommandApproval(command, reason));
    }

    private static void SaveLastModel(string model)
    {
        var settings = AppSettingsStore.Load();
        settings.LastModel = model;
        AppSettingsStore.Save(settings);
    }

    /// <summary>
    /// Run the interactive session loop
    /// </summary>
    public async Task RunInteractiveLoopAsync()
    {
        while (true)
        {
            var input = ConsoleUI.GetUserInput(SlashCommands).Trim();

            if (string.IsNullOrWhiteSpace(input))
                continue;

            // Handle commands
            var lowerInput = input.ToLowerInvariant();
            
            if (lowerInput is "/exit" or "/quit" or "exit" or "quit")
            {
                ConsoleUI.ShowInfo("Ending session. Goodbye!");
                break;
            }

            if (lowerInput == "/clear")
            {
                ConsoleUI.ShowBanner();
                ConsoleUI.ShowStatusPanel(_targetServer, ConnectionMode, true, SelectedModel, GetStatusFields());
                continue;
            }

            if (lowerInput == "/status")
            {
                ConsoleUI.ShowStatusPanel(_targetServer, ConnectionMode, _copilotSession != null, SelectedModel, GetStatusFields());
                continue;
            }

            if (lowerInput == "/help")
            {
                ConsoleUI.ShowHelp();
                continue;
            }

            if (lowerInput == "/history")
            {
                ConsoleUI.ShowCommandHistory(_executor.GetCommandHistory());
                continue;
            }

            if (lowerInput == "/capabilities")
            {
                ConsoleUI.ShowStatusPanel(_targetServer, ConnectionMode, _copilotSession != null, SelectedModel, GetStatusFields());
                continue;
            }

            if (lowerInput == "/model")
            {
                var newModel = ConsoleUI.PromptModelSelection(SelectedModel, _availableModels);
                if (newModel != null && newModel != _selectedModel)
                {
                    var success = await ConsoleUI.RunWithSpinnerAsync($"Switching to {newModel}...", async updateStatus =>
                    {
                        return await ChangeModelAsync(newModel, updateStatus);
                    });
                    
                    if (success)
                    {
                        ConsoleUI.ShowSuccess($"Now using model: {SelectedModel}");
                    }
                }
                continue;
            }

            if (lowerInput.StartsWith("/connect"))
            {
                var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    ConsoleUI.ShowWarning("Usage: /connect <server>");
                }
                else
                {
                    var newServer = parts[1];
                    var success = await ConsoleUI.RunWithSpinnerAsync($"Connecting to {newServer}...", async updateStatus =>
                    {
                        return await ReconnectAsync(newServer, updateStatus);
                    });

                    if (success)
                    {
                        ConsoleUI.ShowSuccess($"Connected to {newServer}");
                        ConsoleUI.ShowStatusPanel(_targetServer, ConnectionMode, _copilotSession != null, SelectedModel, GetStatusFields());
                    }
                }
                continue;
            }

            // Send message to Copilot
            await SendMessageAsync(input);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_copilotSession != null)
        {
            try
            {
                await _copilotSession.DisposeAsync();
            }
            catch (Exception ex)
            {
                if (_debugMode)
                {
                    ConsoleUI.ShowWarning($"Session cleanup warning: {TrimSingleLine(ex.Message)}");
                }
            }
        }

        if (_copilotClient != null)
        {
            try
            {
                await _copilotClient.DisposeAsync();
            }
            catch (Exception ex)
            {
                if (_debugMode)
                {
                    ConsoleUI.ShowWarning($"Copilot cleanup warning: {TrimSingleLine(ex.Message)}");
                }
            }
        }

        _executor.Dispose();
        
        GC.SuppressFinalize(this);
    }

    private async Task<bool> CreateCopilotSessionAsync(string? model, Action<string>? updateStatus)
    {
        if (_copilotClient == null)
        {
            ConsoleUI.ShowError("Not Connected", "Copilot client not initialized");
            return false;
        }

        updateStatus?.Invoke("Creating AI session...");

        ResetCapabilities();
        DiscoverConfiguredSkills();

        var mcpServers = LoadMcpServersFromConfig(_mcpConfigPath, _configurationWarnings);
        foreach (var serverName in mcpServers.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            _configuredMcpServers.Add(serverName);
        }

        var config = new SessionConfig
        {
            Model = model,
            SystemMessage = _systemMessageConfig,
            Streaming = true,
            Tools = _diagnosticTools.GetTools().ToList()
        };

        if (mcpServers.Count > 0)
        {
            config.McpServers = mcpServers;
        }

        if (_skillDirectories.Count > 0)
        {
            config.SkillDirectories = _skillDirectories.ToList();
        }

        if (_disabledSkills.Count > 0)
        {
            config.DisabledSkills = _disabledSkills.ToList();
        }

        _copilotSession = await _copilotClient.CreateSessionAsync(config);
        _lastUsage = null;

        if (_configurationWarnings.Count > 0)
        {
            ConsoleUI.ShowWarning("Capabilities loaded with warnings. Use /status or /capabilities to review details.");
        }

        // Capture model and usage info
        updateStatus?.Invoke("Verifying model...");

        var sessionIdle = new TaskCompletionSource<bool>();
        using var subscription = _copilotSession.On(evt =>
        {
            switch (evt)
            {
                case SessionStartEvent startEvt:
                    _selectedModel = startEvt.Data.SelectedModel;
                    _copilotVersion = startEvt.Data.CopilotVersion;
                    break;
                case SessionModelChangeEvent modelChange:
                    _selectedModel = modelChange.Data.NewModel;
                    break;
                case AssistantUsageEvent usageEvt:
                    CaptureUsageMetrics(usageEvt);
                    if (!string.IsNullOrEmpty(usageEvt.Data?.Model))
                    {
                        _selectedModel = usageEvt.Data.Model;
                    }
                    break;
                case SessionIdleEvent:
                    sessionIdle.TrySetResult(true);
                    break;
            }
        });

        await _copilotSession.SendAsync(new MessageOptions { Prompt = "Say 'ready' and nothing else." });
        await Task.WhenAny(sessionIdle.Task, Task.Delay(15000));

        if (!string.IsNullOrWhiteSpace(_selectedModel))
        {
            SaveLastModel(_selectedModel);
        }

        return true;
    }

    private async Task<bool> ReconnectAsync(string newServer, Action<string>? updateStatus = null)
    {
        if (string.IsNullOrWhiteSpace(newServer))
        {
            ConsoleUI.ShowWarning("Server name cannot be empty");
            return false;
        }

        newServer = newServer.Trim();

        if (newServer.Equals(_targetServer, StringComparison.OrdinalIgnoreCase))
        {
            ConsoleUI.ShowInfo($"Already connected to {newServer}");
            return true;
        }

        updateStatus?.Invoke("Closing current PowerShell session...");
        _executor.Dispose();

        _targetServer = newServer;
        _systemMessageConfig = CreateSystemMessage(_targetServer);
        _executor = new PowerShellExecutor(_targetServer);
        _diagnosticTools = new DiagnosticTools(_executor, PromptApprovalAsync, _targetServer);

        updateStatus?.Invoke($"Connecting to {_targetServer}...");
        var (connectionSuccess, connectionError) = await _executor.TestConnectionAsync();
        if (!connectionSuccess)
        {
            ConsoleUI.ShowError("Connection Failed", connectionError ?? $"Unable to connect to {_targetServer}");
            return false;
        }

        if (_copilotClient != null)
        {
            if (_copilotSession != null)
            {
                updateStatus?.Invoke("Closing AI session...");
                await _copilotSession.DisposeAsync();
                _copilotSession = null;
            }

            var modelToUse = !string.IsNullOrWhiteSpace(_selectedModel)
                ? _selectedModel
                : _requestedModel;

            if (!await CreateCopilotSessionAsync(modelToUse, updateStatus))
            {
                return false;
            }
        }

        return true;
    }

    public IReadOnlyList<(string Label, string Value)> GetStatusFields()
    {
        var fields = new List<(string Label, string Value)>();

        if (_lastUsage != null && _lastUsage.HasAny)
        {
            AddUsageField(fields, "Prompt tokens", _lastUsage.PromptTokens);
            AddUsageField(fields, "Completion tokens", _lastUsage.CompletionTokens);
            AddUsageField(fields, "Total tokens", _lastUsage.TotalTokens);
            AddUsageField(fields, "Input tokens", _lastUsage.InputTokens);
            AddUsageField(fields, "Output tokens", _lastUsage.OutputTokens);
            AddUsageField(fields, "Context max", _lastUsage.MaxContextTokens);
            AddUsageField(fields, "Context used", _lastUsage.UsedContextTokens);
            AddUsageField(fields, "Context free", _lastUsage.FreeContextTokens);
        }

        AddCapabilityField(fields, "MCP configured", _configuredMcpServers);
        AddCapabilityField(fields, "MCP used", _runtimeMcpServers.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        AddCapabilityField(fields, "Skills configured", _configuredSkills);
        AddCapabilityField(fields, "Skills used", _runtimeSkills.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

        if (_configurationWarnings.Count > 0)
        {
            fields.Add(("Capability warnings", string.Join(" | ", _configurationWarnings)));
        }

        return fields;
    }

    private static void AddUsageField(List<(string Label, string Value)> fields, string label, int? value)
    {
        if (!value.HasValue)
            return;

        fields.Add((label, value.Value.ToString("N0", CultureInfo.InvariantCulture)));
    }

    private static void AddCapabilityField(List<(string Label, string Value)> fields, string label, IEnumerable<string> values)
    {
        var distinct = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinct.Count == 0)
            return;

        fields.Add((label, string.Join(", ", distinct)));
    }

    private void CaptureUsageMetrics(AssistantUsageEvent usageEvt)
    {
        var data = usageEvt.Data;
        if (data == null)
            return;

        var usageObj = GetPropertyValue(data, "Usage") ?? data;

        var promptTokens = ReadIntProperty(usageObj, "PromptTokens", "InputTokens", "RequestTokens");
        var completionTokens = ReadIntProperty(usageObj, "CompletionTokens", "OutputTokens", "ResponseTokens");
        var totalTokens = ReadIntProperty(usageObj, "TotalTokens", "Tokens");

        var inputTokens = ReadIntProperty(usageObj, "InputTokens", "PromptTokens", "RequestTokens");
        var outputTokens = ReadIntProperty(usageObj, "OutputTokens", "CompletionTokens", "ResponseTokens");

        var usedContext = ReadIntProperty(usageObj, "UsedTokens", "ContextTokensUsed", "ContextTokens", "UsedContextTokens");
        var maxContext = ReadIntProperty(usageObj, "MaxTokens", "MaxContextTokens", "ContextWindowTokens", "ContextTokensMax");
        var freeContext = ReadIntProperty(usageObj, "FreeTokens", "RemainingTokens", "ContextTokensRemaining");

        if (maxContext.HasValue && usedContext.HasValue && !freeContext.HasValue)
        {
            freeContext = Math.Max(0, maxContext.Value - usedContext.Value);
        }

        var snapshot = new CopilotUsageSnapshot(
            promptTokens,
            completionTokens,
            totalTokens,
            inputTokens,
            outputTokens,
            maxContext,
            usedContext,
            freeContext);

        if (snapshot.HasAny)
        {
            _lastUsage = snapshot;
        }
    }

    private void CaptureCapabilityUsage(SessionEvent evt)
    {
        if (evt is ToolExecutionStartEvent toolStart)
        {
            var mcpServerName = ReadStringProperty(toolStart.Data, "McpServerName", "MCPServerName", "ServerName");
            if (!string.IsNullOrWhiteSpace(mcpServerName))
            {
                _runtimeMcpServers.Add(mcpServerName);
            }
        }

        if (string.Equals(evt.Type, "skill.invoked", StringComparison.OrdinalIgnoreCase))
        {
            var eventData = GetPropertyValue(evt, "Data");
            var skillName = ReadStringProperty(eventData, "Name", "SkillName", "Id");
            if (!string.IsNullOrWhiteSpace(skillName))
            {
                _runtimeSkills.Add(skillName);
            }
        }
    }

    private static string? ReadStringProperty(object? instance, params string[] propertyNames)
    {
        if (instance == null)
            return null;

        foreach (var propertyName in propertyNames)
        {
            var prop = instance.GetType().GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var value = prop?.GetValue(instance);
            if (value is string stringValue && !string.IsNullOrWhiteSpace(stringValue))
            {
                return stringValue.Trim();
            }
        }

        return null;
    }

    private void ResetCapabilities()
    {
        _configuredMcpServers.Clear();
        _configuredSkills.Clear();
        _runtimeMcpServers.Clear();
        _runtimeSkills.Clear();
        _configurationWarnings.Clear();
    }

    private static string ReadSkillNameFromManifest(string manifestPath)
    {
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                var name = nameElement.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
        }
        catch
        {
            // Ignore malformed manifest; caller falls back to folder name.
        }

        return string.Empty;
    }

    private void DiscoverConfiguredSkills()
    {
        var discoveredSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in _skillDirectories)
        {
            if (!Directory.Exists(directory))
            {
                _configurationWarnings.Add($"Skills directory not found: {directory}");
                continue;
            }

            foreach (var skillDir in Directory.GetDirectories(directory))
            {
                var skillMarkdown = Path.Combine(skillDir, "SKILL.md");
                var skillManifest = Path.Combine(skillDir, "skill.json");
                if (!File.Exists(skillMarkdown) && !File.Exists(skillManifest))
                {
                    continue;
                }

                var skillName = File.Exists(skillManifest)
                    ? ReadSkillNameFromManifest(skillManifest)
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(skillName))
                {
                    skillName = Path.GetFileName(skillDir);
                }

                if (!_disabledSkills.Contains(skillName, StringComparer.OrdinalIgnoreCase))
                {
                    discoveredSkills.Add(skillName);
                }
            }
        }

        _configuredSkills.Clear();
        _configuredSkills.AddRange(discoveredSkills);
        _configuredSkills.Sort(StringComparer.OrdinalIgnoreCase);
    }

    internal static Dictionary<string, object> LoadMcpServersFromConfig(string? mcpConfigPath, List<string> warnings)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(mcpConfigPath))
        {
            return result;
        }

        if (!File.Exists(mcpConfigPath))
        {
            warnings.Add($"MCP config file not found: {mcpConfigPath}");
            return result;
        }

        try
        {
            using var stream = File.OpenRead(mcpConfigPath);
            using var document = JsonDocument.Parse(stream);

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("MCP config root must be a JSON object.");
                return result;
            }

            JsonElement serversElement;
            if (!root.TryGetProperty("mcpServers", out serversElement) &&
                !root.TryGetProperty("servers", out serversElement))
            {
                warnings.Add("MCP config does not contain 'mcpServers' or 'servers'.");
                return result;
            }

            if (serversElement.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("MCP config 'mcpServers' or 'servers' must be a JSON object.");
                return result;
            }

            foreach (var property in serversElement.EnumerateObject())
            {
                var mapped = TryMapMcpServer(property.Name, property.Value, out var warning);
                if (mapped == null)
                {
                    if (!string.IsNullOrWhiteSpace(warning))
                    {
                        warnings.Add(warning);
                    }

                    continue;
                }

                result[property.Name] = mapped;
            }
        }
        catch (JsonException ex)
        {
            warnings.Add($"MCP config JSON parse error: {TrimSingleLine(ex.Message)}");
        }
        catch (Exception ex)
        {
            warnings.Add($"MCP config load failed: {TrimSingleLine(ex.Message)}");
        }

        return result;
    }

    private static object? TryMapMcpServer(string serverName, JsonElement serverElement, out string? warning)
    {
        warning = null;

        if (serverElement.ValueKind != JsonValueKind.Object)
        {
            warning = $"Skipping MCP server '{serverName}': entry must be an object.";
            return null;
        }

        var type = GetOptionalString(serverElement, "type")?.Trim().ToLowerInvariant();
        if (type is "http" or "sse")
        {
            var url = GetOptionalString(serverElement, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                warning = $"Skipping MCP server '{serverName}': remote server requires 'url'.";
                return null;
            }

            var remote = new McpRemoteServerConfig
            {
                Type = type!,
                Url = url!
            };

            var headers = GetStringDictionary(serverElement, "headers");
            if (headers != null)
            {
                remote.Headers = headers;
            }

            var remoteTools = GetStringList(serverElement, "tools");
            if (remoteTools != null)
            {
                remote.Tools = remoteTools;
            }

            var remoteTimeout = GetOptionalInt(serverElement, "timeout");
            if (remoteTimeout.HasValue)
            {
                remote.Timeout = remoteTimeout.Value;
            }

            return remote;
        }

        var command = GetOptionalString(serverElement, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            warning = $"Skipping MCP server '{serverName}': local/stdio server requires 'command'.";
            return null;
        }

        var local = new McpLocalServerConfig
        {
            Type = string.IsNullOrWhiteSpace(type) ? "local" : type!,
            Command = command!
        };

        var args = GetStringList(serverElement, "args");
        if (args != null)
        {
            local.Args = args;
        }

        var env = GetStringDictionary(serverElement, "env");
        if (env != null)
        {
            local.Env = env;
        }

        var cwd = GetOptionalString(serverElement, "cwd");
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            local.Cwd = cwd;
        }

        var localTools = GetStringList(serverElement, "tools");
        if (localTools != null)
        {
            local.Tools = localTools;
        }

        var localTimeout = GetOptionalInt(serverElement, "timeout");
        if (localTimeout.HasValue)
        {
            local.Timeout = localTimeout.Value;
        }

        return local;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static int? GetOptionalInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return property.TryGetInt32(out var value) ? value : null;
    }

    private static List<string>? GetStringList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                list.Add(value);
            }
        }

        return list.Count == 0 ? null : list;
    }

    private static Dictionary<string, string>? GetStringDictionary(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in property.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                dict[item.Name] = value!;
            }
        }

        return dict.Count == 0 ? null : dict;
    }

    private static object? GetPropertyValue(object instance, string propertyName)
    {
        var prop = instance.GetType().GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return prop?.GetValue(instance);
    }

    private static int? ReadIntProperty(object? instance, params string[] propertyNames)
    {
        if (instance == null)
            return null;

        foreach (var name in propertyNames)
        {
            var prop = instance.GetType().GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null)
                continue;

            var value = prop.GetValue(instance);
            if (value == null)
                continue;

            if (value is int i)
                return i;
            if (value is long l)
                return (int)l;
            if (value is double d)
                return (int)d;
            if (value is float f)
                return (int)f;
        }

        return null;
    }
}
