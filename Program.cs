using System.Reflection;
using TroubleScout;
using TroubleScout.Services;
using TroubleScout.UI;

// Parse command line arguments manually for simplicity
var server = "localhost";
string? prompt = null;
string? model = null;
string? mcpConfigPath = null;
var skillDirectories = new List<string>();
var disabledSkills = new List<string>();
var appVersion = GetAppVersion();
var debugMode = false;
var executionMode = ExecutionMode.Safe;
var useByokOpenAi = false;
string? byokOpenAiBaseUrl = null;
string? byokOpenAiApiKey = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--server" or "-s" when i + 1 < args.Length:
            server = args[++i];
            break;
        case "--prompt" or "-p" when i + 1 < args.Length:
            prompt = args[++i];
            break;
        case "--model" or "-m" when i + 1 < args.Length:
            model = args[++i];
            break;
        case "--mcp-config" when i + 1 < args.Length:
            mcpConfigPath = args[++i];
            break;
        case "--skills-dir" when i + 1 < args.Length:
            skillDirectories.Add(args[++i]);
            break;
        case "--disable-skill" when i + 1 < args.Length:
            disabledSkills.Add(args[++i]);
            break;
        case "--help" or "-h":
            ConsoleUI.ShowBanner(appVersion);
            ConsoleUI.ShowHelp();
            return 0;
        case "--version" or "-v":
            Console.WriteLine($"TroubleScout {appVersion}");
            return 0;
        case "--debug" or "-debug" or "-d":
            debugMode = true;
            break;
        case "--mode" when i + 1 < args.Length:
            if (!ExecutionModeParser.TryParse(args[++i], out executionMode))
            {
                Console.WriteLine("Invalid mode. Use: safe or yolo.");
                return 1;
            }
            break;
        case "--byok-openai":
            useByokOpenAi = true;
            break;
        case "--openai-base-url" when i + 1 < args.Length:
            byokOpenAiBaseUrl = args[++i];
            break;
        case "--openai-api-key" when i + 1 < args.Length:
            byokOpenAiApiKey = args[++i];
            break;
    }
}

var settings = AppSettingsStore.Load();

if (!useByokOpenAi && settings.UseByokOpenAi)
{
    useByokOpenAi = true;
    byokOpenAiBaseUrl = settings.ByokOpenAiBaseUrl;
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
    await RunHeadlessModeAsync(server, prompt, model, mcpConfigPath, skillDirectories, disabledSkills, debugMode, executionMode, useByokOpenAi, byokOpenAiBaseUrl, byokOpenAiApiKey);
}
else
{
    // Interactive mode with full TUI
    await RunInteractiveModeAsync(server, model, mcpConfigPath, skillDirectories, disabledSkills, appVersion, debugMode, executionMode, useByokOpenAi, byokOpenAiBaseUrl, byokOpenAiApiKey);
}

return Environment.ExitCode;

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
    string server,
    string? model,
    string? mcpConfigPath,
    IReadOnlyList<string> skillDirectories,
    IReadOnlyList<string> disabledSkills,
    string appVersion,
    bool debugMode,
    ExecutionMode executionMode,
    bool useByokOpenAi,
    string? byokOpenAiBaseUrl,
    string? byokOpenAiApiKey)
{
    // Show the full TUI
    ConsoleUI.ShowBanner(appVersion);
    
    await using var session = new TroubleshootingSession(
        server,
        model,
        mcpConfigPath,
        skillDirectories,
        disabledSkills,
        debugMode,
        executionMode,
        useByokOpenAi,
        byokOpenAiBaseUrl,
        byokOpenAiApiKey);
    
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
    ConsoleUI.ShowStatusPanel(server, session.ConnectionMode, session.IsAiSessionReady, session.SelectedModel, session.CurrentExecutionMode, session.GetStatusFields());
    
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
    string server,
    string prompt,
    string? model,
    string? mcpConfigPath,
    IReadOnlyList<string> skillDirectories,
    IReadOnlyList<string> disabledSkills,
    bool debugMode,
    ExecutionMode executionMode,
    bool useByokOpenAi,
    string? byokOpenAiBaseUrl,
    string? byokOpenAiApiKey)
{
    await using var session = new TroubleshootingSession(
        server,
        model,
        mcpConfigPath,
        skillDirectories,
        disabledSkills,
        debugMode,
        executionMode,
        useByokOpenAi,
        byokOpenAiBaseUrl,
        byokOpenAiApiKey);

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

    ConsoleUI.ShowInfo($"Target: {server} | Model: {session.SelectedModel} | Mode: {session.CurrentExecutionMode.ToCliValue()}");
    ConsoleUI.ShowRule();
    ConsoleUI.ShowInfo($"Processing: {prompt}");
    Console.WriteLine();

    var result = await session.SendMessageAsync(prompt);
    
    Environment.ExitCode = result ? 0 : 1;
}
