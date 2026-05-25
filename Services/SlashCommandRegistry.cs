namespace TroubleScout.Services;

internal sealed record SlashCommandDescriptor(
    string Category,
    string Usage,
    string Summary,
    IReadOnlyList<string> Suggestions,
    IReadOnlyList<string>? Details = null,
    IReadOnlyList<string>? Examples = null);

internal static class SlashCommandRegistry
{
    private const string Session = "Session";
    private const string Diagnostics = "Diagnostics";
    private const string Configuration = "Configuration";
    private const string Mcp = "MCP";

    internal static readonly SlashCommandDescriptor[] Commands =
    [
        new(Session, "/help", "Show the in-app command reference.", ["/help"]),
        new(Session, "/status", "Show connection, target servers, model, provider, reasoning effort, execution mode, capability details, and per-session usage.", ["/status"]),
        new(Session, "/stats", "Show session statistics, including turn count, failed/cancelled turns, token usage, latency percentiles, and tool-call counts.", ["/stats"]),
        new(Session, "/clear", "Start a new Copilot session.", ["/clear"],
            ["Conversation history, MCP per-session approvals, and URL approvals are reset; persistent settings remain."]),
        new(Session, "/history", "Show PowerShell command history captured during this session.", ["/history"]),
        new(Session, "/report", "Generate a self-contained HTML report of the current session and open it in the default browser.", ["/report"],
            ["The report mirrors the terminal status bar per prompt and includes a session-wide summary."]),
        new(Session, "/transcript save|load <path>", "Save or load a redacted session transcript JSON file.", ["/transcript"],
            [
                "- `save` writes the current recorded prompts, assistant replies, tool actions, status bars, and session summary metadata to a versioned JSON file.",
                "- `load` validates a transcript file and replaces the current recorded history after confirmation when history already exists.",
                "Loaded transcripts are immediately available to `/report` and `/model` second-opinion context.",
                "Transcript persistence is always explicit; TroubleScout does not automatically write transcript files."
            ],
            ["/transcript save C:\\Temp\\troublescout-session.json", "/transcript load C:\\Temp\\troublescout-session.json"]),
        new(Session, "/exit, /quit, exit, quit", "Leave the interactive session.", ["/exit", "/quit"]),

        new(Diagnostics, "/server <server1>[ <server2> ...]", "Connect to one or more additional servers.", ["/server"],
            ["Names may be separated by whitespace or commas."],
            ["/server srv1", "/server srv1 srv2", "/server srv1,srv2"]),
        new(Diagnostics, "/jea [server] [configurationName]", "Connect to a JEA-constrained PowerShell remoting endpoint.", ["/jea"],
            [
                "With no arguments, TroubleScout prompts for the server and configuration name in turn. Because the user invokes `/jea` explicitly, no second Safe-mode approval is requested before connecting.",
                "JEA executions never use `AddScript(...)`; commands are built with the PowerShell command API so no-language endpoints stay valid."
            ],
            ["/jea server1 JEA-Admins"]),
        new(Diagnostics, "/mode <strict|auto>", "Set the PowerShell execution mode for the current session.", ["/mode"],
            [
                "- `strict` (default): proven read-only commands auto-execute; mutations and unknown commands require approval.",
                "- `auto`: unknown command candidates can be evaluated by the configured approval subagent; known mutations still require approval."
            ]),

        new(Configuration, "/model", "Choose another AI model and session handoff mode interactively.", ["/model"]),
        new(Configuration, "/agent-model [role] [model|inherit]", "Configure per-provider models for evidence, research, monitoring, ticketing, and approval subagents.", ["/agent-model"],
            ["The `approval` role must have an explicit model before `/mode auto` can be enabled."]),
        new(Configuration, "/reasoning [auto|<effort>]", "Set the reasoning effort for the current model when supported.", ["/reasoning"],
            ["With no argument, prompts interactively."]),
        new(Configuration, "/settings", "Open `settings.json` in the default editor, then reload prompt and safety configuration after the editor exits.", ["/settings"]),
        new(Configuration, "/login", "Run GitHub Copilot login inside TroubleScout.", ["/login"]),
        new(Configuration, "/byok [env|<api-key>|<base-url> [api-key]] [base-url] [model]", "Enable OpenAI-compatible BYOK mode without GitHub authentication.", ["/byok"],
            [
                "- With no arguments, prompts interactively for the base URL and API key.",
                "- `env` reads `OPENAI_API_KEY` from the environment.",
                "- `<api-key>` passes the key directly. The key is encrypted at rest with DPAPI (`ByokOpenAiApiKeyEncrypted` in `settings.json`).",
                "- `base-url` overrides the OpenAI-compatible endpoint URL. It can appear after `env`/`<api-key>` or as the first argument before the API key.",
                "- `model` selects an initial BYOK model."
            ],
            ["/byok", "/byok env https://api.openai.com/v1", "/byok sk-... https://aigw.example.org", "/byok https://aigw.example.org sk-... gpt-5"]),
        new(Configuration, "/byok clear|off|disable", "Clear saved BYOK settings for this profile.", [],
            ["`clear`, `off`, and `disable` are accepted aliases."]),
        new(Configuration, "/theme <dark|mono>", "Set the app chrome theme.", ["/theme"],
            ["Theme applies to panels and the status bar only; it does not retint Markdown responses, reasoning output, or the spinner."]),
        new(Configuration, "/save <path>", "Save the last assistant response as Markdown to a file.", ["/save"],
            ["The command refuses directory targets, refuses to create parent directories, and prompts before overwriting an existing file."]),
        new(Configuration, "/copy", "Copy the last assistant response Markdown to the local clipboard.", ["/copy"],
            ["This uses a short-lived local PowerShell pipeline instead of the active target-server or JEA executor."]),

        new(Mcp, "/capabilities", "Show configured and runtime-used MCP servers and skills.", ["/capabilities"],
            ["The same capability information is also visible in `/status`."]),
        new(Mcp, "/mcp-role monitoring|ticketing <server|none> | clear <monitoring|ticketing|all>", "Configure monitoring and ticketing MCP role mappings.", ["/mcp-role"],
            ["With no arguments, opens an interactive prompt to assign or clear roles. Role mappings persist to `settings.json` and are surfaced in startup, `/status`, and the HTML report."],
            ["/mcp-role monitoring zabbix", "/mcp-role ticketing redmine", "/mcp-role monitoring none", "/mcp-role clear all"]),
        new(Mcp, "/mcp-approvals [list|clear all|clear <server>]", "List or clear active and persisted MCP approvals.", ["/mcp-approvals"],
            ["MCP approvals are per server, not per tool. Approving any tool from a server auto-approves every other tool from that server for the rest of the session. Persisting an approval across sessions is only offered for servers mapped to a monitoring or ticketing role."],
            ["/mcp-approvals list", "/mcp-approvals clear zabbix", "/mcp-approvals clear all"])
    ];

    internal static readonly string[] SlashCommands = Commands
        .SelectMany(command => command.Suggestions)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
