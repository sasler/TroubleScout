using System.Text;
using GitHub.Copilot;

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
            ? PromptTemplateLoader.Render(
                PromptTemplateIds.SystemTargetContextDefault,
                new Dictionary<string, string?>
                {
                    ["targetInfo"] = targetInfo,
                    ["effectivePrimary"] = effectivePrimary
                })
            : PromptTemplateLoader.Render(
                PromptTemplateIds.SystemTargetContextJea,
                new Dictionary<string, string?>
                {
                    ["targetInfo"] = targetInfo,
                    ["targetServer"] = SanitizeServerNameForPrompt(targetServer),
                    ["effectivePrimary"] = SanitizeServerNameForPrompt(effectivePrimary)
                });

        var dataCollectionGuidance = primaryJeaConfigName == null
            ? "Use the diagnostic tools to collect relevant information FROM THE TARGET SERVER"
            : $"Use run_powershell with sessionName: \"{SanitizeServerNameForPrompt(effectivePrimary)}\" to collect data from the primary JEA endpoint. Only use the bootstrap session when the user explicitly asks about {SanitizeServerNameForPrompt(targetServer)}.";

        var sourceVerificationGuidance = primaryJeaConfigName == null
            ? $"Always confirm the data comes from {effectivePrimary} by checking $env:COMPUTERNAME"
            : $"For the primary JEA endpoint, confirm the source from the targeted session/server name ({SanitizeServerNameForPrompt(effectivePrimary)}) rather than using $env:COMPUTERNAME on the constrained endpoint";

        var investigationApproach = PromptTemplateLoader.Render(PromptTemplateIds.SystemInvestigationApproach);

        var responseFormat = PromptTemplateLoader.Render(
            PromptTemplateIds.SystemResponseFormat,
            new Dictionary<string, string?>
            {
                ["effectivePrimary"] = effectivePrimary
            });

        var troubleshootingApproach = PromptTemplateLoader.Render(
            PromptTemplateIds.SystemTroubleshootingApproach,
            new Dictionary<string, string?>
            {
                ["dataCollectionGuidance"] = dataCollectionGuidance,
                ["sourceVerificationGuidance"] = sourceVerificationGuidance
            });

        var safetyGuidance = PromptTemplateLoader.Render(PromptTemplateIds.SystemSafety);

        var mcpRoleGuidance = string.Empty;
        if (!string.IsNullOrWhiteSpace(settings.MonitoringMcpServer) || !string.IsNullOrWhiteSpace(settings.TicketingMcpServer))
        {
            var roleLines = new StringBuilder();
            roleLines.AppendLine("## MCP Role Guidance");
            roleLines.AppendLine("- Delegate targeted diagnostic, monitoring, ticketing, and research lookups to the troubleshooting subagent.");

            if (!string.IsNullOrWhiteSpace(settings.MonitoringMcpServer))
            {
                roleLines.AppendLine($"- Monitoring MCP server: {SanitizeServerNameForPrompt(settings.MonitoringMcpServer)}");
            }

            if (!string.IsNullOrWhiteSpace(settings.TicketingMcpServer))
            {
                roleLines.AppendLine($"- Ticketing MCP server: {SanitizeServerNameForPrompt(settings.TicketingMcpServer)}");
            }

            roleLines.AppendLine("- When monitoring alerts, dashboards, incidents, or ticket history are relevant, consult the mapped MCP role early in the investigation.");
            roleLines.AppendLine("- Keep delegated results concise and bring back only findings that materially affect the diagnosis.");
            roleLines.AppendLine();
            mcpRoleGuidance = roleLines.ToString();
        }
        else
        {
            mcpRoleGuidance = """
                ## Delegation Guidance
                - Delegate targeted diagnostic and research lookups to the troubleshooting subagent.
                - Keep delegated results concise and return only evidence that affects the diagnosis.

                """;
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

        var identity = PromptTemplateLoader.Render(PromptTemplateIds.SystemIdentity);

        var environmentContext = $"""
            ## Target Server Context
            {targetContextGuidance}
            {primaryJeaBlock}
            """;

        var toolInstructions = PromptTemplateLoader.Render(
            PromptTemplateIds.SystemToolInstructions,
            new Dictionary<string, string?>
            {
                ["connectedSessionsBlock"] = connectedSessionsBlock,
                ["jeaSessionsBlock"] = jeaSessionsBlock,
                ["mcpRoleGuidance"] = mcpRoleGuidance
            });

        var guidelines = $"""
            {troubleshootingApproach}

            {investigationApproach}

            {responseFormat}
            """;

        var customInstructions = PromptTemplateLoader.Render(
            PromptTemplateIds.SystemCustomInstructions,
            new Dictionary<string, string?>
            {
                ["effectivePrimary"] = effectivePrimary
            });

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
