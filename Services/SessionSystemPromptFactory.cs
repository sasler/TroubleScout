using GitHub.Copilot.SDK;

namespace TroubleScout.Services;

internal sealed record SessionSystemPromptRequest(
    string TargetServer,
    string CurrentTargetServer,
    IReadOnlyCollection<string>? AdditionalServerNames,
    Func<(string ServerName, string ConfigurationName, PowerShellExecutor Executor)?> GetEffectivePrimaryJeaSession,
    IReadOnlyDictionary<string, PowerShellExecutor> Executors,
    IReadOnlyDictionary<string, string>? SystemPromptOverrides,
    string? SystemPromptAppend,
    string? MonitoringMcpServer,
    string? TicketingMcpServer,
    ExecutionMode ExecutionMode);

internal static class SessionSystemPromptFactory
{
    internal static SystemMessageConfig Create(SessionSystemPromptRequest request)
    {
        string? effectivePrimary = null;
        string? primaryJeaConfigName = null;
        PowerShellExecutor? primaryJeaExec = null;

        if (request.TargetServer.Equals(request.CurrentTargetServer, StringComparison.OrdinalIgnoreCase)
            && request.GetEffectivePrimaryJeaSession() is { } jeaSession)
        {
            effectivePrimary = jeaSession.ServerName;
            primaryJeaConfigName = jeaSession.ConfigurationName;
            primaryJeaExec = jeaSession.Executor;
        }

        var settings = new AppSettings
        {
            SystemPromptOverrides = request.SystemPromptOverrides?.ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.OrdinalIgnoreCase),
            SystemPromptAppend = request.SystemPromptAppend,
            MonitoringMcpServer = request.MonitoringMcpServer,
            TicketingMcpServer = request.TicketingMcpServer
        };

        return SystemPromptBuilder.CreateSystemMessage(
            request.TargetServer,
            request.AdditionalServerNames,
            effectivePrimary,
            primaryJeaConfigName,
            primaryJeaExec,
            request.Executors,
            settings,
            request.ExecutionMode);
    }
}
