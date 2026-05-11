namespace TroubleScout.Services;

internal sealed record EffectivePrimaryJeaSession(string ServerName, string ConfigurationName, PowerShellExecutor Executor);

internal static class SessionTargetDisplay
{
    internal static EffectivePrimaryJeaSession? GetEffectivePrimaryJeaSession(
        (string ServerName, string ConfigurationName)? initialJeaSession,
        bool startupJeaFocusActive,
        string targetServer,
        IReadOnlyDictionary<string, PowerShellExecutor> executors)
    {
        if (initialJeaSession is { } jea
            && startupJeaFocusActive
            && targetServer.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && executors.TryGetValue(jea.ServerName, out var candidate)
            && candidate.IsJeaSession)
        {
            return new EffectivePrimaryJeaSession(
                jea.ServerName,
                candidate.ConfigurationName ?? jea.ConfigurationName,
                candidate);
        }

        return null;
    }

    internal static string GetEffectiveTargetServer(string targetServer, EffectivePrimaryJeaSession? jeaSession)
        => jeaSession?.ServerName ?? targetServer;

    internal static string GetEffectiveConnectionMode(PowerShellExecutor executor, EffectivePrimaryJeaSession? jeaSession)
        => jeaSession is null ? executor.GetConnectionMode() : $"JEA ({jeaSession.ConfigurationName})";

    internal static IReadOnlyList<string> GetEffectiveTargetServers(
        string targetServer,
        IReadOnlyDictionary<string, PowerShellExecutor> executors,
        EffectivePrimaryJeaSession? jeaSession)
    {
        if (jeaSession is not null)
        {
            return [jeaSession.ServerName, ..executors.Keys
                .Where(k => !k.Equals(jeaSession.ServerName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];
        }

        return [targetServer, ..executors.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];
    }

    internal static IReadOnlyList<string>? GetAdditionalTargetsForDisplay(IReadOnlyList<string> effectiveTargetServers)
        => effectiveTargetServers.Count > 1 ? effectiveTargetServers.Skip(1).ToList() : null;
}
