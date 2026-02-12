using System.Reflection;
using TroubleScout;
using TroubleScout.Services;
using TroubleScout.UI;

// Parse command line arguments manually for simplicity
var server = "localhost";
string? prompt = null;
string? model = null;
var appVersion = GetAppVersion();

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
        case "--help" or "-h":
            ShowHelp(appVersion);
            return 0;
        case "--version" or "-v":
            Console.WriteLine($"TroubleScout {appVersion}");
            return 0;
    }
}

if (string.IsNullOrWhiteSpace(model))
{
    var settings = AppSettingsStore.Load();
    if (!string.IsNullOrWhiteSpace(settings.LastModel))
    {
        model = settings.LastModel;
    }
}

if (!string.IsNullOrWhiteSpace(prompt))
{
    // Headless mode - single prompt execution
    await RunHeadlessModeAsync(server, prompt, model);
}
else
{
    // Interactive mode with full TUI
    await RunInteractiveModeAsync(server, model, appVersion);
}

return Environment.ExitCode;

static void ShowHelp(string version)
{
    Console.WriteLine($"TroubleScout {version} - AI-Powered Windows Server Troubleshooting Assistant");
    Console.WriteLine();
    Console.WriteLine("Usage: TroubleScout [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  -s, --server <name>   Target Windows Server (default: localhost)");
    Console.WriteLine("  -m, --model <name>    AI model to use (e.g., gpt-4o, claude-sonnet-4)");
    Console.WriteLine("  -p, --prompt <text>   Initial prompt for headless mode");
    Console.WriteLine("  -v, --version         Show app version and exit");
    Console.WriteLine("  -h, --help            Show help information");
    Console.WriteLine();
    Console.WriteLine("Available models depend on your GitHub Copilot subscription.");
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
static async Task RunInteractiveModeAsync(string server, string? model, string appVersion)
{
    // Show the full TUI
    ConsoleUI.ShowBanner(appVersion);
    
    await using var session = new TroubleshootingSession(server, model);
    
    // Initialize with animated spinner
    var success = await ConsoleUI.RunWithSpinnerAsync("Initializing...", async updateStatus =>
    {
        return await session.InitializeAsync(updateStatus);
    });
    
    if (!success)
    {
        ConsoleUI.ShowError("Startup Failed", "Could not initialize the troubleshooting session.");
        return;
    }

    // Show status panel once with full info
    ConsoleUI.ShowStatusPanel(server, session.ConnectionMode, true, session.SelectedModel);
    
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
static async Task RunHeadlessModeAsync(string server, string prompt, string? model)
{
    await using var session = new TroubleshootingSession(server, model);

    // Initialize with animated spinner
    var success = await ConsoleUI.RunWithSpinnerAsync("Initializing TroubleScout...", async updateStatus =>
    {
        return await session.InitializeAsync(updateStatus);
    });

    if (!success)
    {
        ConsoleUI.ShowError("Initialization Failed", "Could not initialize session");
        Environment.ExitCode = 1;
        return;
    }

    ConsoleUI.ShowInfo($"Target: {server} | Model: {session.SelectedModel}");
    ConsoleUI.ShowRule();
    ConsoleUI.ShowInfo($"Processing: {prompt}");
    Console.WriteLine();

    var result = await session.SendMessageAsync(prompt);
    
    Environment.ExitCode = result ? 0 : 1;
}
