using System.Text.Json;
using TroubleScout.UI;

namespace TroubleScout.Services;

internal enum SessionTranscriptSaveResult
{
    Success,
    NoHistory,
    PathMissing,
    PathIsDirectory,
    ParentDirectoryMissing,
    FileAlreadyExists,
    WriteFailed
}

internal enum SessionTranscriptLoadResult
{
    Success,
    PathMissing,
    FileNotFound,
    PathIsDirectory,
    MalformedJson,
    UnsupportedSchemaVersion,
    EmptyTranscript,
    ReadFailed
}

internal sealed record SessionTranscriptDocument(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    ReportSessionSummary? Summary,
    List<ReportPromptEntry> Prompts);

internal static class SessionTranscriptService
{
    internal const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal static SessionTranscriptSaveResult Save(
        string? path,
        IReadOnlyList<ReportPromptEntry> prompts,
        ReportSessionSummary? summary,
        bool allowOverwrite,
        out string? detail)
    {
        detail = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return SessionTranscriptSaveResult.PathMissing;
        }

        if (prompts.Count == 0)
        {
            return SessionTranscriptSaveResult.NoHistory;
        }

        string fullPath;
        string? parent;
        try
        {
            fullPath = Path.GetFullPath(path);
            parent = Path.GetDirectoryName(fullPath);
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return SessionTranscriptSaveResult.WriteFailed;
        }

        if (Directory.Exists(fullPath))
        {
            return SessionTranscriptSaveResult.PathIsDirectory;
        }

        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            detail = parent;
            return SessionTranscriptSaveResult.ParentDirectoryMissing;
        }

        if (File.Exists(fullPath) && !allowOverwrite)
        {
            return SessionTranscriptSaveResult.FileAlreadyExists;
        }

        var document = new SessionTranscriptDocument(
            CurrentSchemaVersion,
            DateTimeOffset.Now,
            RedactSummary(summary),
            RedactPrompts(prompts));

        try
        {
            var json = JsonSerializer.Serialize(document, JsonOptions);
            File.WriteAllText(fullPath, json);
            return SessionTranscriptSaveResult.Success;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return SessionTranscriptSaveResult.WriteFailed;
        }
    }

    internal static SessionTranscriptLoadResult Load(
        string? path,
        out SessionTranscriptDocument? transcript,
        out string? detail)
    {
        transcript = null;
        detail = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return SessionTranscriptLoadResult.PathMissing;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return SessionTranscriptLoadResult.ReadFailed;
        }

        if (Directory.Exists(fullPath))
        {
            return SessionTranscriptLoadResult.PathIsDirectory;
        }

        if (!File.Exists(fullPath))
        {
            return SessionTranscriptLoadResult.FileNotFound;
        }

        SessionTranscriptDocument? document;
        try
        {
            var json = File.ReadAllText(fullPath);
            document = JsonSerializer.Deserialize<SessionTranscriptDocument>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            detail = ex.Message;
            return SessionTranscriptLoadResult.MalformedJson;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return SessionTranscriptLoadResult.ReadFailed;
        }

        if (document == null)
        {
            return SessionTranscriptLoadResult.MalformedJson;
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            detail = $"Unsupported transcript schema version {document.SchemaVersion}. Supported version: {CurrentSchemaVersion}.";
            return SessionTranscriptLoadResult.UnsupportedSchemaVersion;
        }

        if (document.Prompts is not { Count: > 0 })
        {
            return SessionTranscriptLoadResult.EmptyTranscript;
        }

        transcript = document with
        {
            Summary = RedactSummary(document.Summary),
            Prompts = RedactPrompts(document.Prompts)
        };
        return SessionTranscriptLoadResult.Success;
    }

    internal static List<ReportPromptEntry> RedactPrompts(IReadOnlyList<ReportPromptEntry> prompts)
    {
        return prompts
            .Select(prompt => new ReportPromptEntry(
                prompt.Timestamp,
                SecretRedactor.Redact(prompt.Prompt),
                (prompt.Actions ?? [])
                    .Select(RedactAction)
                    .ToList(),
                SecretRedactor.Redact(prompt.AgentReply))
            {
                StatusBar = RedactStatusBar(prompt.StatusBar)
            })
            .ToList();
    }

    private static ReportActionEntry RedactAction(ReportActionEntry action)
    {
        return new ReportActionEntry(
            action.Timestamp,
            SecretRedactor.Redact(action.Target),
            SecretRedactor.Redact(action.Command),
            SecretRedactor.Redact(action.Output),
            SecretRedactor.Redact(action.SafetyApproval),
            SecretRedactor.Redact(action.Source))
        {
            Arguments = SecretRedactor.Redact(action.Arguments),
            Success = action.Success,
            ToolCallId = SecretRedactor.Redact(action.ToolCallId)
        };
    }

    private static StatusBarInfo? RedactStatusBar(StatusBarInfo? statusBar)
    {
        if (statusBar == null)
        {
            return null;
        }

        return statusBar with
        {
            Model = SecretRedactor.Redact(statusBar.Model),
            Provider = SecretRedactor.Redact(statusBar.Provider),
            ReasoningEffort = SecretRedactor.Redact(statusBar.ReasoningEffort),
            SessionCostEstimate = SecretRedactor.Redact(statusBar.SessionCostEstimate)
        };
    }

    private static ReportSessionSummary? RedactSummary(ReportSessionSummary? summary)
    {
        if (summary == null)
        {
            return null;
        }

        return summary with
        {
            CurrentModel = SecretRedactor.Redact(summary.CurrentModel),
            CurrentProvider = SecretRedactor.Redact(summary.CurrentProvider),
            ModelsUsed = RedactList(summary.ModelsUsed),
            ConfiguredMcpServers = RedactList(summary.ConfiguredMcpServers),
            UsedMcpServers = RedactList(summary.UsedMcpServers),
            MonitoringMcp = SecretRedactor.Redact(summary.MonitoringMcp),
            TicketingMcp = SecretRedactor.Redact(summary.TicketingMcp),
            ApprovedMcpServersForSession = RedactList(summary.ApprovedMcpServersForSession),
            PersistedApprovedMcpServers = RedactList(summary.PersistedApprovedMcpServers),
            ConfiguredSkills = RedactList(summary.ConfiguredSkills),
            UsedSkills = RedactList(summary.UsedSkills),
            ExecutionMode = SecretRedactor.Redact(summary.ExecutionMode),
            TargetServer = SecretRedactor.Redact(summary.TargetServer)
        };
    }

    private static IReadOnlyList<string> RedactList(IReadOnlyList<string> values) =>
        values.Select(SecretRedactor.Redact).ToList();
}
