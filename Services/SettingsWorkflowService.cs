using System.Diagnostics;
using TroubleScout.UI;

namespace TroubleScout.Services;

internal static class SettingsWorkflowService
{
    internal static void PersistThemeSetting(string theme)
    {
        var settings = AppSettingsStore.Load();
        settings.Theme = theme;
        AppSettingsStore.Save(settings);
    }

    internal static void SaveModelAndProviderState(string model, bool useByokOpenAi)
    {
        var settings = AppSettingsStore.Load();
        settings.LastModel = model;
        settings.UseByokOpenAi = useByokOpenAi;
        AppSettingsStore.Save(settings);
    }

    internal static void SaveReasoningEffortState(string? reasoningEffort)
    {
        var settings = AppSettingsStore.Load();
        settings.ReasoningEffort = ReasoningEffortHelper.Normalize(reasoningEffort);
        AppSettingsStore.Save(settings);
    }

    internal static void SaveByokSettings(bool enabled, string? baseUrl, string? apiKey)
    {
        var settings = AppSettingsStore.Load();
        settings.UseByokOpenAi = enabled;
        settings.ByokOpenAiBaseUrl = enabled ? baseUrl : null;
        settings.ByokOpenAiApiKey = enabled ? apiKey : null;
        AppSettingsStore.Save(settings);
    }

    internal static void SaveMcpRoleSettings(string? monitoringMcpServer, string? ticketingMcpServer)
    {
        var settings = AppSettingsStore.Load();

        settings.MonitoringMcpServer = string.IsNullOrWhiteSpace(monitoringMcpServer) ? null : monitoringMcpServer.Trim();
        settings.TicketingMcpServer = string.IsNullOrWhiteSpace(ticketingMcpServer) ? null : ticketingMcpServer.Trim();

        if (settings.PersistedApprovedMcpServers is { Count: > 0 } persisted)
        {
            var stillMapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(settings.MonitoringMcpServer))
            {
                stillMapped.Add(settings.MonitoringMcpServer);
            }

            if (!string.IsNullOrWhiteSpace(settings.TicketingMcpServer))
            {
                stillMapped.Add(settings.TicketingMcpServer);
            }

            var pruned = persisted
                .Where(p => !string.IsNullOrWhiteSpace(p) && stillMapped.Contains(p.Trim()))
                .ToList();
            if (pruned.Count != persisted.Count)
            {
                settings.PersistedApprovedMcpServers = pruned;
            }
        }

        AppSettingsStore.Save(settings);
    }

    internal static void SaveSubagentModelOverride(bool useByokOpenAi, string? model)
    {
        var settings = AppSettingsStore.Load();
        var provider = AppSettingsStore.GetProviderProfileKey(useByokOpenAi);
        settings.AgentModelProfiles ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!settings.AgentModelProfiles.TryGetValue(provider, out var models))
        {
            models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            settings.AgentModelProfiles[provider] = models;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            models.Remove(AppSettingsStore.SubagentModelRole);
            if (models.Count == 0)
            {
                settings.AgentModelProfiles.Remove(provider);
            }
        }
        else
        {
            models[AppSettingsStore.SubagentModelRole] = model.Trim();
        }

        AppSettingsStore.Save(settings);
    }

    internal static (IReadOnlyDictionary<string, string>? Overrides, string? Append) NormalizeSystemPromptSettings(
        IReadOnlyDictionary<string, string>? overrides,
        string? append)
    {
        IReadOnlyDictionary<string, string>? normalized = null;
        if (overrides != null)
        {
            var normalizedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in overrides)
            {
                var normalizedKey = entry.Key?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedKey) || string.IsNullOrWhiteSpace(entry.Value))
                {
                    continue;
                }

                if (AppSettingsStore.IsDefaultSystemPromptOverride(normalizedKey, entry.Value))
                {
                    continue;
                }

                normalizedOverrides[normalizedKey] = entry.Value;
            }

            normalized = normalizedOverrides.Count > 0 ? normalizedOverrides : null;
        }

        return (normalized, string.IsNullOrWhiteSpace(append) ? null : append);
    }

    internal static async Task<string?> TryOpenSettingsEditorAsync(string settingsPath)
    {
        var editorCommand = Environment.GetEnvironmentVariable("EDITOR");
        if (string.IsNullOrWhiteSpace(editorCommand))
        {
            editorCommand = Environment.GetEnvironmentVariable("VISUAL");
        }

        if (string.IsNullOrWhiteSpace(editorCommand) && OperatingSystem.IsWindows())
        {
            editorCommand = "notepad";
        }

        if (string.IsNullOrWhiteSpace(editorCommand))
        {
            return "No editor is configured. Set EDITOR or VISUAL to edit settings automatically.";
        }

        try
        {
            var (fileName, arguments) = ParseCommandWithArguments(editorCommand, settingsPath);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                CreateNoWindow = false
            });

            if (process == null)
            {
                return $"Could not start editor '{editorCommand}'.";
            }

            await process.WaitForExitAsync();
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not open settings editor: {TrimSingleLine(ex.Message)}";
        }
    }

    internal static (string FileName, string Arguments) ParseCommandWithArguments(string command, string settingsPath)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote > 0)
            {
                var fileName = trimmed[1..closingQuote];
                var existingArgs = trimmed[(closingQuote + 1)..].Trim();
                var args = string.IsNullOrWhiteSpace(existingArgs)
                    ? QuoteArgument(settingsPath)
                    : $"{existingArgs} {QuoteArgument(settingsPath)}";
                return (fileName, args);
            }
        }

        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace < 0)
        {
            return (trimmed, QuoteArgument(settingsPath));
        }

        var executable = trimmed[..firstSpace];
        var arguments = trimmed[(firstSpace + 1)..].Trim();
        return (executable, $"{arguments} {QuoteArgument(settingsPath)}");
    }

    private static string QuoteArgument(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string TrimSingleLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Unknown error";
        }

        var trimmed = text.Trim();
        var newlineIndex = trimmed.IndexOfAny(['\r', '\n']);
        return newlineIndex < 0 ? trimmed : trimmed[..newlineIndex].Trim();
    }
}
