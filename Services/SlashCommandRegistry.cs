namespace TroubleScout.Services;

internal sealed record SlashCommandDescriptor(
    string Usage,
    string Summary,
    IReadOnlyList<string> Suggestions);

internal static class SlashCommandRegistry
{
    internal static readonly SlashCommandDescriptor[] Commands =
    [
        new("/help", "Show this full command reference", ["/help"]),
        new("/status", "Show connection, model, mode, and session details", ["/status"]),
        new("/stats", "Show session statistics (turn count, tokens, latency p50/p95, tool counts)", ["/stats"]),
        new("/clear", "Start new session", ["/clear"]),
        new("/settings", "Open settings.json and reload settings after editing", ["/settings"]),
        new("/mcp-role [<monitoring|ticketing> <server|none> | clear <monitoring|ticketing|all>]", "Configure monitoring and ticketing MCP role mappings; no args opens interactive mode", ["/mcp-role"]),
        new("/mcp-approvals [list | clear all | clear <server>]", "List or clear active and persisted MCP approvals", ["/mcp-approvals"]),
        new("/model", "Choose another AI model and session handoff mode", ["/model"]),
        new("/reasoning [auto|<effort>]", "Set reasoning effort for the current model", ["/reasoning"]),
        new("/mode <safe|yolo>", "Set PowerShell execution mode", ["/mode"]),
        new("/server <server1>[,server2,...]", "Connect to one or more servers: /server srv1[,srv2,...]", ["/server"]),
        new("/jea [server] [configurationName]", "Connect to a JEA constrained endpoint", ["/jea"]),
        new("/login", "Run GitHub Copilot login inside TroubleScout", ["/login"]),
        new("/byok <env|api-key> [base-url] [model]", "Enable OpenAI-compatible BYOK without GitHub auth", ["/byok"]),
        new("/byok clear", "Clear saved BYOK settings for this profile", []),
        new("/capabilities", "Show configured and used MCP servers/skills", ["/capabilities"]),
        new("/history", "Show PowerShell command history for this session", ["/history"]),
        new("/report", "Generate and open HTML session report", ["/report"]),
        new("/theme <dark|mono>", "Set app chrome theme (panels, status bar). Does not affect Markdown responses.", ["/theme"]),
        new("/save <path>", "Save the last assistant response (Markdown) to a file", ["/save"]),
        new("/copy", "Copy the last assistant response to the clipboard", ["/copy"]),
        new("/exit, /quit, exit, quit", "Leave the interactive session", ["/exit", "/quit"])
    ];

    internal static readonly string[] SlashCommands = Commands
        .SelectMany(command => command.Suggestions)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
