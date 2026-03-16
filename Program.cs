using System.Reflection;
using TroubleScout;
using TroubleScout.Services;
using TroubleScout.UI;

// Parse command line arguments manually for simplicity
// Server list is pre-populated by ParseServers; the switch only handles the missing-value error case.
var servers = TroubleScout.Program.ParseServers(args);
(string ServerName, string ConfigurationName)? startupJea = null;
string? prompt = null;
string? model = null;
bool modelSpecifiedByCli = false;
string? mcpConfigPath = null;
var skillDirectories = new List<string>();
var disabledSkills = new List<string>();
var appVersion = GetAppVersion();
var debugMode = false;
var executionMode = ExecutionMode.Safe;
var useByokOpenAi = false;
string? byokOpenAiBaseUrl = null;
string? byokOpenAiApiKey = null;
var byokProviderSpecifiedByCli = false;
var byokBaseUrlSpecifiedByCli = false;
var byokApiKeySpecifiedByCli = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--server" or "-s" when i + 1 < args.Length:
            i++; // value already consumed by ParseServers; advance past it
            break;
        case "--server" or "-s":
            Console.WriteLine("--server (-s) requires a value: hostname or IP address. Repeat for multiple: -s srv1 -s srv2");
            return 1;
        case "--prompt" or "-p" when i + 1 < args.Length:
            prompt = args[++i];
            break;
        case "--prompt" or "-p":
            Console.WriteLine("--prompt (-p) requires a value: the text prompt to send.");
            return 1;
        case "--jea" when i + 2 < args.Length:
            if (startupJea.HasValue)
            {
                Console.WriteLine("--jea can only be specified once.");
                return 1;
            }

            startupJea = (args[++i], args[++i]);
            break;
        case "--jea":
            Console.WriteLine("--jea requires two values: <server> <configurationName>.");
            return 1;
        case "--model" or "-m" when i + 1 < args.Length:
            model = args[++i];
            modelSpecifiedByCli = true;
            break;
        case "--model" or "-m":
            Console.WriteLine("--model (-m) requires a model ID (e.g. gpt-4.1, gpt-5-mini).");
            Console.WriteLine("Available models can be viewed by running TroubleScout interactively and using /model.");
            return 1;
        case "--mcp-config" when i + 1 < args.Length:
            mcpConfigPath = args[++i];
            break;
        case "--mcp-config":
            Console.WriteLine("--mcp-config requires a value: path to the MCP server config JSON file.");
            return 1;
        case "--skills-dir" when i + 1 < args.Length:
            skillDirectories.Add(args[++i]);
            break;
        case "--skills-dir":
            Console.WriteLine("--skills-dir requires a value: path to a directory containing Copilot skill files.");
            return 1;
        case "--disable-skill" when i + 1 < args.Length:
            disabledSkills.Add(args[++i]);
            break;
        case "--disable-skill":
            Console.WriteLine("--disable-skill requires a value: the name of the skill to disable.");
            return 1;
        case "--help" or "-h":
            ConsoleUI.ShowCliHelp(appVersion);
            return 0;
        case "--version" or "-v":
            Console.WriteLine($"TroubleScout {appVersion}");
            return 0;
        case "--debug" or "-d":
            debugMode = true;
            break;
        case "--mode" when i + 1 < args.Length:
            if (!ExecutionModeParser.TryParse(args[++i], out executionMode))
            {
                Console.WriteLine("Invalid mode. Use: safe or yolo.");
                return 1;
            }
            break;
        case "--mode":
            Console.WriteLine("--mode requires a value: safe or yolo.");
            return 1;
        case "--byok-openai":
            useByokOpenAi = true;
            byokProviderSpecifiedByCli = true;
            break;
        case "--no-byok":
            useByokOpenAi = false;
            byokProviderSpecifiedByCli = true;
            break;
        case "--openai-base-url" when i + 1 < args.Length:
            byokOpenAiBaseUrl = args[++i];
            byokBaseUrlSpecifiedByCli = true;
            break;
        case "--openai-base-url":
            Console.WriteLine("--openai-base-url requires a value: the base URL of the OpenAI-compatible endpoint.");
            return 1;
        case "--openai-api-key" when i + 1 < args.Length:
            byokOpenAiApiKey = args[++i];
            byokApiKeySpecifiedByCli = true;
            break;
        case "--openai-api-key":
            Console.WriteLine("--openai-api-key requires a value: the API key for the OpenAI-compatible endpoint.");
            return 1;
    }
}

if (args.Length == 0 && IsNonInteractiveLaunch())
{
    Console.WriteLine($"TroubleScout {appVersion}");
    Console.WriteLine("Non-interactive launch detected. Use --help for usage.");
    return 0;
}

var settings = AppSettingsStore.Load();

if (!byokProviderSpecifiedByCli && settings.UseByokOpenAi)
{
    useByokOpenAi = true;
}

// Keep BYOK configuration available across restarts even when GitHub is the active provider.
if (!byokBaseUrlSpecifiedByCli && string.IsNullOrWhiteSpace(byokOpenAiBaseUrl))
{
    byokOpenAiBaseUrl = settings.ByokOpenAiBaseUrl;
}

if (!byokApiKeySpecifiedByCli && string.IsNullOrWhiteSpace(byokOpenAiApiKey))
{
    byokOpenAiApiKey = settings.ByokOpenAiApiKey;
}

if (string.IsNullOrWhiteSpace(model))
{
    if (!string.IsNullOrWhiteSpace(settings.LastModel))
    {
        model = settings.LastModel;
    }
}

if (useByokOpenAi)
{
    byokOpenAiApiKey = string.IsNullOrWhiteSpace(byokOpenAiApiKey)
        ? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
        : byokOpenAiApiKey;

    byokOpenAiBaseUrl = string.IsNullOrWhiteSpace(byokOpenAiBaseUrl)
        ? "https://api.openai.com/v1"
        : byokOpenAiBaseUrl;
}

if (string.IsNullOrWhiteSpace(mcpConfigPath))
{
    mcpConfigPath = ResolveDefaultMcpConfigPath();
}

if (skillDirectories.Count == 0)
{
    skillDirectories.AddRange(ResolveDefaultSkillDirectories());
}

skillDirectories = skillDirectories
    .Where(dir => !string.IsNullOrWhiteSpace(dir))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();

disabledSkills = disabledSkills
    .Where(skill => !string.IsNullOrWhiteSpace(skill))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();

if (!string.IsNullOrWhiteSpace(prompt))
{
    // Headless mode - single prompt execution
    await RunHeadlessModeAsync(servers, prompt, model, mcpConfigPath, skillDirectories, disabledSkills, debugMode, executionMode, useByokOpenAi, byokOpenAiBaseUrl, byokOpenAiApiKey, byokProviderSpecifiedByCli && useByokOpenAi, modelSpecifiedByCli, startupJea);
}
else
{
    // Interactive mode with full TUI
    await RunInteractiveModeAsync(servers, model, mcpConfigPath, skillDirectories, disabledSkills, appVersion, debugMode, executionMode, useByokOpenAi, byokOpenAiBaseUrl, byokOpenAiApiKey, byokProviderSpecifiedByCli && useByokOpenAi, modelSpecifiedByCli, startupJea);
}

return Environment.ExitCode;

static bool IsNonInteractiveLaunch()
{
    return !Environment.UserInteractive
        || Console.IsInputRedirected
        || Console.IsOutputRedirected
        || Console.IsErrorRedirected;
}

static string? ResolveDefaultMcpConfigPath()
{
    var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
    if (string.IsNullOrWhiteSpace(userProfile))
    {
        userProfile = Environment.GetEnvironmentVariable("HOME");
    }

    if (string.IsNullOrWhiteSpace(userProfile))
    {
        return null;
    }

    var path = Path.Combine(userProfile, ".copilot", "mcp-config.json");
    return File.Exists(path) ? path : null;
}

static List<string> ResolveDefaultSkillDirectories()
{
    var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
    if (string.IsNullOrWhiteSpace(userProfile))
    {
        userProfile = Environment.GetEnvironmentVariable("HOME");
    }

    if (string.IsNullOrWhiteSpace(userProfile))
    {
        return [];
    }

    var skillsPath = Path.Combine(userProfile, ".copilot", "skills");
    return Directory.Exists(skillsPath)
        ? [skillsPath]
        : [];
}

static string GetAppVersion()
{
    var assembly = Assembly.GetEntryAssembly() ?? typeof(TroubleshootingSession).Assembly;
    var informational = assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion;

    if (!string.IsNullOrWhiteSpace(informational))
    {
        return informational.Split('+')[0];
    }

    var fileVersion = assembly
        .GetCustomAttribute<AssemblyFileVersionAttribute>()
        ?.Version;

    return string.IsNullOrWhiteSpace(fileVersion)
        ? "unknown"
        : fileVersion;
}

/// <summary>
/// Run in interactive mode with full TUI
/// </summary>
static async Task RunInteractiveModeAsync(
    IReadOnlyList<string> servers,
    string? model,
    string? mcpConfigPath,
    IReadOnlyList<string> skillDirectories,
    IReadOnlyList<string> disabledSkills,
    string appVersion,
    bool debugMode,
    ExecutionMode executionMode,
    bool useByokOpenAi,
    string? byokOpenAiBaseUrl,
    string? byokOpenAiApiKey,
    bool byokExplicitlyRequested = false,
    bool modelExplicitlyRequested = false,
    (string ServerName, string ConfigurationName)? initialJeaSession = null)
{
    // Show the full TUI
    ConsoleUI.ShowBanner(appVersion);
    
    var primary = servers[0];
    var additional = servers.Count > 1 ? servers.Skip(1).ToList() : null;

    // Immediate startup feedback before the potentially slow initialization
    var serverDisplay = TroubleScout.Program.BuildStartupTargetDisplay(servers, initialJeaSession);
    ConsoleUI.ShowStartupProgress(serverDisplay);

    await using var session = new TroubleshootingSession(
        primary,
        model,
        mcpConfigPath,
        skillDirectories,
        disabledSkills,
        debugMode,
        executionMode,
        useByokOpenAi,
        byokOpenAiBaseUrl,
        byokOpenAiApiKey,
        byokExplicitlyRequested: byokExplicitlyRequested,
        modelExplicitlyRequested: modelExplicitlyRequested,
        additional,
        initialJeaSession);
    
    // Initialize with animated spinner
    var success = await ConsoleUI.RunWithSpinnerAsync("Initializing...", async updateStatus =>
    {
        return await session.InitializeAsync(updateStatus, allowInteractiveSetup: true);
    });
    
    if (!success)
    {
        ConsoleUI.ShowError("Startup Failed", "Could not initialize the troubleshooting session.");
        return;
    }

    // Show status panel once with full info
    var additionalTargets = session.EffectiveTargetServers.Count > 1
        ? session.EffectiveTargetServers.Skip(1).ToList()
        : null;
    ConsoleUI.ShowStatusPanel(session.EffectiveTargetServer, session.EffectiveConnectionMode, session.IsAiSessionReady, session.SelectedModel, session.CurrentExecutionMode, session.GetStatusFields(), additionalTargets, session.DefaultSessionTarget);
    
    // Show welcome and help hints
    ConsoleUI.ShowWelcomeMessage();
    ConsoleUI.ShowRule();
    Console.WriteLine();

    // Run the interactive loop
    await session.RunInteractiveLoopAsync();
}

/// <summary>
/// Run in headless mode for scripting/automation
/// </summary>
static async Task RunHeadlessModeAsync(
    IReadOnlyList<string> servers,
    string prompt,
    string? model,
    string? mcpConfigPath,
    IReadOnlyList<string> skillDirectories,
    IReadOnlyList<string> disabledSkills,
    bool debugMode,
    ExecutionMode executionMode,
    bool useByokOpenAi,
    string? byokOpenAiBaseUrl,
    string? byokOpenAiApiKey,
    bool byokExplicitlyRequested = false,
    bool modelExplicitlyRequested = false,
    (string ServerName, string ConfigurationName)? initialJeaSession = null)
{
    var primary = servers[0];
    var additional = servers.Count > 1 ? servers.Skip(1).ToList() : null;

    // Immediate startup feedback before the potentially slow initialization
    var serverDisplay = TroubleScout.Program.BuildStartupTargetDisplay(servers, initialJeaSession);
    ConsoleUI.ShowStartupProgress(serverDisplay);

    await using var session = new TroubleshootingSession(
        primary,
        model,
        mcpConfigPath,
        skillDirectories,
        disabledSkills,
        debugMode,
        executionMode,
        useByokOpenAi,
        byokOpenAiBaseUrl,
        byokOpenAiApiKey,
        byokExplicitlyRequested: byokExplicitlyRequested,
        modelExplicitlyRequested: modelExplicitlyRequested,
        additional,
        initialJeaSession);

    // Initialize with animated spinner
    var success = await ConsoleUI.RunWithSpinnerAsync("Initializing TroubleScout...", async updateStatus =>
    {
        return await session.InitializeAsync(updateStatus, allowInteractiveSetup: false);
    });

    if (!success)
    {
        ConsoleUI.ShowError("Initialization Failed", "Could not initialize session");
        if (debugMode)
        {
            ConsoleUI.ShowInfo("Debug mode enabled. See diagnostic details above.");
        }
        Environment.ExitCode = 1;
        return;
    }

    ConsoleUI.ShowInfo($"Target: {serverDisplay} | Model: {session.SelectedModel} | Mode: {session.CurrentExecutionMode.ToCliValue()}");
    ConsoleUI.ShowRule();
    ConsoleUI.ShowInfo($"Processing: {prompt}");
    Console.WriteLine();

    var result = await session.SendMessageAsync(prompt);
    
    Environment.ExitCode = result ? 0 : 1;
}

namespace TroubleScout
{
    /// <summary>
    /// Partial class to expose testable CLI parsing helpers.
    /// </summary>
    public partial class Program
    {
        /// <summary>
        /// Parse --server / -s flags from CLI args, supporting repeated flags and comma-separated values.
        /// </summary>
        public static List<string> ParseServers(string[] args)
        {
            var servers = new List<string> { "localhost" };
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--server" || args[i] == "-s") && i + 1 < args.Length)
                {
                    var serverArg = args[++i];
                    var parsed = serverArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (servers.Count == 1 && servers[0] == "localhost")
                        servers.Clear();
                    servers.AddRange(parsed);
                }
            }
            if (servers.Count == 0)
                servers = new List<string> { "localhost" };
            return servers;
        }

        public static (string ServerName, string ConfigurationName)? ParseStartupJea(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--jea" && i + 2 < args.Length)
                {
                    return (args[i + 1], args[i + 2]);
                }
            }

            return null;
        }

        public static string BuildStartupTargetDisplay(
            IReadOnlyList<string> servers,
            (string ServerName, string ConfigurationName)? initialJeaSession)
        {
            var primary = servers.Count > 0 ? servers[0] : "localhost";
            if (initialJeaSession.HasValue && primary.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return $"{initialJeaSession.Value.ServerName} (JEA: {initialJeaSession.Value.ConfigurationName}; default session: localhost)";
            }

            return servers.Count > 1 ? string.Join(", ", servers) : primary;
        }
    }
}
