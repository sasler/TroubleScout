using System.Text;
using GitHub.Copilot.SDK;

namespace TroubleScout.Services;

internal static class SystemPromptBuilder
{
    internal static SystemMessageConfig CreateSystemMessage(
        string targetServer,
        IReadOnlyCollection<string>? additionalServerNames,
        string? effectivePrimaryServer,
        string? primaryJeaConfigName,
        PowerShellExecutor? primaryJeaExecutor,
        IReadOnlyDictionary<string, PowerShellExecutor> additionalExecutors,
        AppSettings settings,
        ExecutionMode executionMode)
    {
        _ = executionMode;

        var effectivePrimary = effectivePrimaryServer ?? targetServer;
        var targetInfo = effectivePrimary.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? "the local machine (localhost)"
            : $"the remote server: {effectivePrimary}";

        var connectedSessionsBlock = string.Empty;
        var jeaSessionsBlock = string.Empty;

        var primaryJeaBlock = string.Empty;
        if (primaryJeaConfigName != null)
        {
            var primaryJeaLines = new StringBuilder();
            primaryJeaLines.AppendLine();
            var safePrimary = SanitizeServerNameForPrompt(effectivePrimary);
            var safeConfig = SanitizeServerNameForPrompt(primaryJeaConfigName);
            primaryJeaLines.AppendLine($"## Primary JEA Endpoint: {safePrimary} (Configuration: {safeConfig})");
            primaryJeaLines.AppendLine("Your primary target is a constrained JEA endpoint. ONLY the following commands are available on this server:");

            if (primaryJeaExecutor?.JeaAllowedCommands is { Count: > 0 })
            {
                foreach (var commandName in primaryJeaExecutor.JeaAllowedCommands.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                {
                    primaryJeaLines.AppendLine($"- {SanitizeServerNameForPrompt(commandName)}");
                }
            }
            else
            {
                primaryJeaLines.AppendLine("- Command discovery has not completed yet.");
            }

            primaryJeaLines.AppendLine("Do NOT attempt any other commands — they will be blocked by the JEA endpoint.");
            primaryJeaLines.AppendLine($"Use run_powershell with sessionName: \"{safePrimary}\" to target this JEA session.");
            primaryJeaLines.AppendLine("Do NOT use the built-in diagnostic helper tools for this endpoint; they rely on broader PowerShell language features than constrained JEA sessions allow.");
            primaryJeaLines.AppendLine();
            primaryJeaBlock = primaryJeaLines.ToString();
        }

        if (additionalServerNames is { Count: > 0 })
        {
            var sessionLines = new StringBuilder();
            sessionLines.AppendLine();
            sessionLines.AppendLine("## Connected PSSessions");
            sessionLines.AppendLine("The following servers are ALREADY connected and available as named sessions. Use run_powershell with sessionName to target each:");
            if (primaryJeaConfigName != null)
            {
                sessionLines.AppendLine($"- Bootstrap/default session: {SanitizeServerNameForPrompt(targetServer)} — run_powershell without sessionName targets this session. Use it only when the user explicitly asks about {SanitizeServerNameForPrompt(targetServer)} or for local setup tasks.");
            }
            else
            {
                sessionLines.AppendLine($"- Primary (default): {SanitizeServerNameForPrompt(targetServer)} — use run_powershell without sessionName");
            }

            foreach (var serverName in additionalServerNames)
            {
                if (primaryJeaConfigName != null && serverName.Equals(effectivePrimary, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var safeServerName = SanitizeServerNameForPrompt(serverName);
                if (additionalExecutors.TryGetValue(serverName, out var executor) && executor.IsJeaSession)
                {
                    var configName = SanitizeServerNameForPrompt(executor.ConfigurationName ?? "unknown");
                    sessionLines.AppendLine($"- {safeServerName} (JEA: {configName}) — use run_powershell(command, sessionName: \"{safeServerName}\")");
                }
                else
                {
                    sessionLines.AppendLine($"- {safeServerName} — use run_powershell(command, sessionName: \"{safeServerName}\")");
                }
            }

            sessionLines.AppendLine();
            sessionLines.AppendLine("When the user asks about multiple servers, gather data from ALL of them using run_powershell with the appropriate sessionName. Do NOT call connect_server for these — they are already connected.");
            connectedSessionsBlock = sessionLines.ToString();
        }

        var jeaExecutors = additionalExecutors
            .Where(entry => entry.Value.IsJeaSession)
            .Where(entry => primaryJeaConfigName == null || !entry.Key.Equals(effectivePrimary, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (jeaExecutors.Count > 0)
        {
            var jeaLines = new StringBuilder();
            jeaLines.AppendLine();
            foreach (var (serverName, executor) in jeaExecutors)
            {
                var safeServerName = SanitizeServerNameForPrompt(serverName);
                var safeConfigName = SanitizeServerNameForPrompt(executor.ConfigurationName ?? "unknown");
                jeaLines.AppendLine($"## JEA Session: {safeServerName} (Configuration: {safeConfigName})");
                jeaLines.AppendLine("This is a constrained JEA endpoint. ONLY the following commands are available:");

                if (executor.JeaAllowedCommands is { Count: > 0 })
                {
                    foreach (var commandName in executor.JeaAllowedCommands.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                    {
                        jeaLines.AppendLine($"- {SanitizeServerNameForPrompt(commandName)}");
                    }
                }
                else
                {
                    jeaLines.AppendLine("- Command discovery has not completed yet.");
                }

                jeaLines.AppendLine("Do NOT attempt any other commands — they will be blocked by the JEA endpoint.");
                jeaLines.AppendLine($"Use run_powershell with sessionName: \"{safeServerName}\" to target this session.");
                jeaLines.AppendLine();
            }

            jeaSessionsBlock = jeaLines.ToString();
        }

        var targetContextGuidance = primaryJeaConfigName == null
            ? $"""
            - You are currently connected to {targetInfo}
            - ALL commands and diagnostic operations will execute on this target server
            - When gathering data or making observations, you MUST always state which server the data comes from
            - Always verify that the data you receive is from the expected target server
            - If the user doesn't specify a server in their question, assume they mean the current target: {effectivePrimary}
            """
            : $"""
            - Your primary troubleshooting focus is {targetInfo}
            - If the user doesn't specify a server in their question, assume they mean the current JEA target: {effectivePrimary}
            - The default unnamed PowerShell session still targets {SanitizeServerNameForPrompt(targetServer)}. Do NOT use it for {SanitizeServerNameForPrompt(effectivePrimary)} unless the user explicitly asks about {SanitizeServerNameForPrompt(targetServer)}.
            - To work on {SanitizeServerNameForPrompt(effectivePrimary)}, use run_powershell with sessionName: "{SanitizeServerNameForPrompt(effectivePrimary)}"
            - Do NOT use the built-in diagnostic helper tools for {SanitizeServerNameForPrompt(effectivePrimary)}; they rely on broader PowerShell language features than constrained JEA endpoints allow
            - When gathering data or making observations, you MUST always state which server the data comes from
            - For the primary JEA endpoint, verify source using the targeted session/server name rather than `$env:COMPUTERNAME`, which may be unavailable in no-language mode
            """;

        var dataCollectionGuidance = primaryJeaConfigName == null
            ? "Use the diagnostic tools to collect relevant information FROM THE TARGET SERVER"
            : $"Use run_powershell with sessionName: \"{SanitizeServerNameForPrompt(effectivePrimary)}\" to collect data from the primary JEA endpoint. Only use the bootstrap session when the user explicitly asks about {SanitizeServerNameForPrompt(targetServer)}.";

        var sourceVerificationGuidance = primaryJeaConfigName == null
            ? $"Always confirm the data comes from {effectivePrimary} by checking $env:COMPUTERNAME"
            : $"For the primary JEA endpoint, confirm the source from the targeted session/server name ({SanitizeServerNameForPrompt(effectivePrimary)}) rather than using $env:COMPUTERNAME on the constrained endpoint";

        var investigationApproach = """
            ## Investigation Approach
            - When investigating an issue, exhaust ALL available diagnostic tools and data sources before asking the user for more information
            - Work proactively within a single investigation pass until you have a clear diagnosis, recommendation, or exhausted the relevant diagnostics
            - Only ask clarifying questions when the initial problem description is genuinely ambiguous or when you need credentials/access that you do not have
            - Present complete findings, analysis, and recommendations in one response, then hand control back to TroubleScout instead of continuing indefinitely on your own
            - When you are ready for the user to choose what happens next, end with a short `## Ready for next action` section
            - If one diagnostic approach yields no results, try alternative approaches before concluding
            - Gather data from ALL relevant sources (event logs, services, processes, performance counters, disk, network) in a single investigation pass
            """;

        var responseFormat = $"""
            ## Response Format
            - ALWAYS start your response by confirming which server you're analyzing (e.g., "Analyzing {effectivePrimary}...")
            - Always format your response as Markdown
            - Use short Markdown sections and bullet lists to keep output readable
            - Separate distinct steps/findings with blank lines
            - For tabular data, use compact Markdown tables (pipe syntax) and avoid fixed-width ASCII-art table alignment
            - If a table would be too wide, reduce columns or use a concise bullet list instead of forcing alignment
            - Be concise but thorough
            - Use bullet points for lists
            - Highlight critical findings with **bold**
            - Use fenced code blocks for commands or command output when relevant
            - For remediation commands (non-Get commands), explain what they do and why they're needed
            - Always explain your reasoning
            - When presenting diagnostic data, include the source server name in your explanation
            """;

        var troubleshootingApproach = $"""
            ## Troubleshooting Approach
            1. **Understand the Problem**: Ask clarifying questions if the issue description is vague
            2. **Gather Data**: {dataCollectionGuidance}
            3. **Verify Source**: {sourceVerificationGuidance}
            4. **Analyze**: Look for errors, warnings, resource exhaustion, or configuration issues
            5. **Diagnose**: Form hypotheses about the root cause based on evidence
            6. **Recommend**: Provide clear, actionable next steps
            """;

        var safetyGuidance = """
            ## Safety
            - Only read-only Get-* commands execute automatically
            - Read-only diagnostic tools execute automatically in ALL modes (Safe and YOLO) — never wait for approval before using them
            - In Safe mode, only mutating PowerShell commands (run_powershell with Set-*, Stop-*, Start-*, Remove-*, Restart-* etc.) require user confirmation
            - In YOLO mode, remediation commands can execute without confirmation
            - For ANY mutating task, you MUST call the run_powershell tool with the exact command
            - For mutating PowerShell cmdlets that support confirmation prompts, include `-Confirm:$false` when appropriate after the user has approved the action
            - Never claim a command was executed unless run_powershell returned execution output
            - Never say you will keep monitoring, continue in the background, or confirm later after control returns to the user prompt. If a command is still running or needs follow-up, tell the user what happened and what they should run or ask next.
            - If no tool was executed, clearly state that no command has been run yet
            - Before claiming you do not have access to a tool, web capability, MCP server, or skill, first attempt to use the relevant available capability
            - If a capability is unavailable after an attempt, clearly state what you tried and what was unavailable
            - Never suggest commands that could cause data loss without clear warnings
            - Always consider the impact of recommended actions
            """;

        var mcpRoleGuidance = string.Empty;
        if (!string.IsNullOrWhiteSpace(settings.MonitoringMcpServer) || !string.IsNullOrWhiteSpace(settings.TicketingMcpServer))
        {
            var roleLines = new StringBuilder();
            roleLines.AppendLine("## MCP Role Guidance");

            if (!string.IsNullOrWhiteSpace(settings.MonitoringMcpServer))
            {
                roleLines.AppendLine($"- Monitoring MCP server: {SanitizeServerNameForPrompt(settings.MonitoringMcpServer)}");
                roleLines.AppendLine("- Delegate monitoring lookups to the monitoring-focused sub-agent when the monitoring role is configured.");
            }

            if (!string.IsNullOrWhiteSpace(settings.TicketingMcpServer))
            {
                roleLines.AppendLine($"- Ticketing MCP server: {SanitizeServerNameForPrompt(settings.TicketingMcpServer)}");
                roleLines.AppendLine("- Delegate ticket history lookups to the ticket-focused sub-agent when the ticketing role is configured.");
            }

            roleLines.AppendLine("- When monitoring alerts, dashboards, incidents, or ticket history are relevant, consult the mapped MCP role early in the investigation.");
            roleLines.AppendLine("- Delegate external issue and remediation research to the issue-researcher sub-agent when web research could materially improve the diagnosis.");
            roleLines.AppendLine("- Keep delegated results concise and bring back only findings that materially affect the diagnosis.");
            roleLines.AppendLine();
            mcpRoleGuidance = roleLines.ToString();
        }

        if (settings.SystemPromptOverrides != null)
        {
            if (settings.SystemPromptOverrides.TryGetValue("investigation_approach", out var customInvestigation) && !string.IsNullOrWhiteSpace(customInvestigation))
            {
                investigationApproach = customInvestigation;
            }

            if (settings.SystemPromptOverrides.TryGetValue("response_format", out var customFormat) && !string.IsNullOrWhiteSpace(customFormat))
            {
                responseFormat = customFormat;
            }

            if (settings.SystemPromptOverrides.TryGetValue("troubleshooting_approach", out var customTroubleshootingApproach) && !string.IsNullOrWhiteSpace(customTroubleshootingApproach))
            {
                troubleshootingApproach = customTroubleshootingApproach;
            }

            if (settings.SystemPromptOverrides.TryGetValue("safety", out var customSafety) && !string.IsNullOrWhiteSpace(customSafety))
            {
                safetyGuidance = customSafety;
            }
        }

        var identity = """
            You are TroubleScout, an expert Windows Server troubleshooting assistant.
            Your role is to diagnose issues on Windows servers by analyzing system data and providing actionable insights.
            """;

        var environmentContext = $"""
            ## Target Server Context
            {targetContextGuidance}
            {primaryJeaBlock}
            """;

        var toolInstructions = $"""
            ## Your Capabilities
            - Execute read-only PowerShell commands (Get-*) to gather diagnostic information from the target server
            - Analyze Windows Event Logs, services, processes, performance counters, disk space, and network configuration
            - Use all available runtime capabilities when relevant, including built-in tools, configured MCP servers, and loaded skills
            - Always prefer using the available diagnostic tools to gather data rather than stating you cannot retrieve information
            - Attempt every relevant diagnostic tool before concluding data is unavailable
            - If a tool call returns an error or times out, retry it once with a slightly different approach before giving up
            - All read-only tools (get_system_info, get_event_logs, get_services, get_processes, get_disk_space, get_network_info, get_performance_counters) execute automatically without any confirmation required
            - Identify patterns, anomalies, and potential root causes
            - Provide clear, prioritized recommendations

            ## Multi-Server Sessions & Double-Hop Avoidance
            - To avoid PowerShell double-hop authentication issues, NEVER run remote commands from one server to another.
            - If you need data from a different server, use connect_server(serverName) to establish a DIRECT session from this client.
            - If you need to use a constrained JEA endpoint, use connect_jea_server(serverName, configurationName) and then only run commands allowed by that endpoint.
            - Use run_powershell(command, sessionName: "serverName") to run commands on that specific server.
            - Use close_server_session(serverName) when done with a server to clean up resources.
            - Always indicate which server each piece of data comes from.
            {connectedSessionsBlock}
            {jeaSessionsBlock}
            {mcpRoleGuidance}
            """;

        var guidelines = $"""
            {troubleshootingApproach}

            {investigationApproach}

            {responseFormat}
            """;

        var customInstructions = $"""
            Remember: Your goal is to help the user understand what's wrong with {effectivePrimary} and guide them to a solution,
            not just dump raw data. Interpret the findings and provide expert analysis. Always maintain awareness of which
            server you're working on.
            """;

        return new SystemMessageConfig
        {
            Mode = SystemMessageMode.Customize,
            Content = string.IsNullOrWhiteSpace(settings.SystemPromptAppend) ? null : settings.SystemPromptAppend,
            Sections = new Dictionary<string, SectionOverride>(StringComparer.OrdinalIgnoreCase)
            {
                [SystemPromptSections.Identity] = CreateSystemPromptSection(identity),
                [SystemPromptSections.EnvironmentContext] = CreateSystemPromptSection(environmentContext),
                [SystemPromptSections.ToolInstructions] = CreateSystemPromptSection(toolInstructions),
                [SystemPromptSections.Guidelines] = CreateSystemPromptSection(guidelines),
                [SystemPromptSections.Safety] = CreateSystemPromptSection(safetyGuidance),
                [SystemPromptSections.CustomInstructions] = CreateSystemPromptSection(customInstructions)
            }
        };
    }

    internal static SectionOverride CreateSystemPromptSection(string content)
    {
        return new SectionOverride
        {
            Action = SectionOverrideAction.Replace,
            Content = content
        };
    }

    internal static string SanitizeServerNameForPrompt(string serverName) =>
        serverName.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
