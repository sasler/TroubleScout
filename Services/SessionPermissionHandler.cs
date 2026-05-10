using GitHub.Copilot.SDK;
using TroubleScout.UI;

namespace TroubleScout.Services;

internal sealed class SessionPermissionHandler
{
    internal delegate ApprovalResult CommandApprovalPrompt(string command, string reason, string? impact);
    internal delegate UrlApprovalResult UrlApprovalPrompt(string url, string? intention);
    internal delegate McpApprovalResult McpApprovalPrompt(string serverName, string toolName, string? argumentsPreview, string? role);

    private readonly Func<ExecutionMode> _getExecutionMode;
    private readonly Func<IReadOnlyList<string>?> _getConfiguredSafeCommands;
    private readonly Func<string, string?> _getMcpServerRole;
    private readonly CommandApprovalPrompt _promptCommandApproval;
    private readonly UrlApprovalPrompt _promptUrlApproval;
    private readonly McpApprovalPrompt _promptMcpApproval;
    private readonly HashSet<string> _approvedUrlsForSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _approvedMcpServersForSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _persistedSeededApprovals = new(StringComparer.OrdinalIgnoreCase);
    private bool _allowAllUrlsForSession;
    private AppSettings? _appSettings;

    internal SessionPermissionHandler(
        Func<ExecutionMode> getExecutionMode,
        Func<IReadOnlyList<string>?> getConfiguredSafeCommands,
        Func<string, string?> getMcpServerRole,
        CommandApprovalPrompt promptCommandApproval,
        UrlApprovalPrompt promptUrlApproval,
        McpApprovalPrompt promptMcpApproval,
        AppSettings? appSettings = null)
    {
        _getExecutionMode = getExecutionMode;
        _getConfiguredSafeCommands = getConfiguredSafeCommands;
        _getMcpServerRole = getMcpServerRole;
        _promptCommandApproval = promptCommandApproval;
        _promptUrlApproval = promptUrlApproval;
        _promptMcpApproval = promptMcpApproval;
        _appSettings = appSettings;
    }

    internal Task<PermissionRequestResult> HandleAsync(PermissionRequest request, PermissionInvocation invocation)
    {
        _ = invocation;

        var kind = PermissionEvaluator.NormalizePermissionKind(request.Kind);
        if (kind is "read" or "custom-tool")
        {
            return Task.FromResult(Approved());
        }

        if (_getExecutionMode() == ExecutionMode.Yolo)
        {
            return Task.FromResult(Approved());
        }

        if (kind == "shell")
        {
            var shellAssessment = PermissionEvaluator.EvaluateShellPermissionRequest(
                request,
                _getExecutionMode(),
                _getConfiguredSafeCommands());
            if (shellAssessment != null)
            {
                if (shellAssessment.Validation.IsAllowed && !shellAssessment.Validation.RequiresApproval)
                {
                    return Task.FromResult(Approved());
                }

                if (!shellAssessment.Validation.IsAllowed && !shellAssessment.Validation.RequiresApproval)
                {
                    return Task.FromResult(Rejected());
                }

                var shellApproval = _promptCommandApproval(
                    shellAssessment.Command,
                    shellAssessment.PromptReason,
                    shellAssessment.ImpactText);
                return Task.FromResult(shellApproval == ApprovalResult.Approved ? Approved() : Rejected());
            }
        }

        if (kind == "url")
        {
            if (TryIsApprovedUrlRequest(request))
            {
                return Task.FromResult(Approved());
            }

            var url = GetUrlFromPermissionRequest(request);
            var intention = GetUrlPermissionIntention(request);
            var urlApproval = _promptUrlApproval(url ?? "URL fetch", intention);
            return Task.FromResult(CreateUrlPermissionApprovalResult(url, urlApproval));
        }

        if (kind == "mcp")
        {
            return Task.FromResult(HandleMcpPermissionRequest(request));
        }

        var description = PermissionEvaluator.DescribePermissionRequest(request);
        var approval = _promptCommandApproval(
            description,
            PermissionEvaluator.BuildPermissionPromptReason(kind),
            null);
        return Task.FromResult(approval == ApprovalResult.Approved ? Approved() : Rejected());
    }

    internal void ResetUrlApprovals()
    {
        _approvedUrlsForSession.Clear();
        _allowAllUrlsForSession = false;
    }

    internal void UpdateAppSettings(AppSettings? settings)
    {
        _appSettings = settings;
    }

    internal void SeedPersistedMcpApprovals(IEnumerable<string>? persistedApprovals)
    {
        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in persistedApprovals ?? [])
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var trimmed = name.Trim();
            if (!string.IsNullOrWhiteSpace(_getMcpServerRole(trimmed)))
            {
                mapped.Add(trimmed);
            }
        }

        foreach (var previous in _persistedSeededApprovals)
        {
            if (!mapped.Contains(previous))
            {
                _approvedMcpServersForSession.Remove(previous);
            }
        }

        _persistedSeededApprovals.Clear();
        foreach (var current in mapped)
        {
            _approvedMcpServersForSession.Add(current);
            _persistedSeededApprovals.Add(current);
        }
    }

    internal IReadOnlyCollection<string> GetApprovedMcpServersSnapshot()
        => _approvedMcpServersForSession.ToArray();

    internal IReadOnlyList<string> GetPersistedApprovedMcpServersSnapshot()
        => _appSettings?.PersistedApprovedMcpServers?.ToArray() ?? Array.Empty<string>();

    internal bool RemovePersistedMcpApproval(string serverName)
    {
        if (_appSettings == null || string.IsNullOrWhiteSpace(serverName))
        {
            return false;
        }

        var removed = AppSettingsStore.RemovePersistedApprovedMcpServer(_appSettings, serverName);
        if (removed)
        {
            _approvedMcpServersForSession.Remove(serverName.Trim());
            _persistedSeededApprovals.Remove(serverName.Trim());
        }

        return removed;
    }

    internal bool RemoveSessionMcpApproval(string serverName)
        => !string.IsNullOrWhiteSpace(serverName) && _approvedMcpServersForSession.Remove(serverName.Trim());

    internal int ClearPersistedMcpApprovals()
    {
        if (_appSettings == null)
        {
            return 0;
        }

        var snapshot = _appSettings.PersistedApprovedMcpServers?.ToList() ?? [];
        var count = AppSettingsStore.ClearPersistedApprovedMcpServers(_appSettings);
        foreach (var name in snapshot)
        {
            _approvedMcpServersForSession.Remove(name);
            _persistedSeededApprovals.Remove(name);
        }

        return count;
    }

    internal void ClearSessionMcpApprovals()
    {
        _approvedMcpServersForSession.Clear();
        _persistedSeededApprovals.Clear();
    }

    private PermissionRequestResult CreateUrlPermissionApprovalResult(string? url, UrlApprovalResult approval)
    {
        switch (approval)
        {
            case UrlApprovalResult.ApproveAllUrls:
                _allowAllUrlsForSession = true;
                return Approved();

            case UrlApprovalResult.ApproveThisUrl:
                var normalizedUrl = NormalizeUrlForApproval(url);
                if (!string.IsNullOrWhiteSpace(normalizedUrl))
                {
                    _approvedUrlsForSession.Add(normalizedUrl);
                }

                return Approved();

            default:
                return Rejected();
        }
    }

    private bool TryIsApprovedUrlRequest(PermissionRequest request)
    {
        if (_allowAllUrlsForSession)
        {
            return true;
        }

        var normalizedUrl = NormalizeUrlForApproval(GetUrlFromPermissionRequest(request));
        return !string.IsNullOrWhiteSpace(normalizedUrl) && _approvedUrlsForSession.Contains(normalizedUrl);
    }

    private PermissionRequestResult HandleMcpPermissionRequest(PermissionRequest request)
    {
        var serverName = request is PermissionRequestMcp typedMcp
            ? typedMcp.ServerName?.Trim()
            : PermissionEvaluator.ReadStringProperty(request, "McpServerName", "ServerName", "Server", "Name");
        var toolName = request is PermissionRequestMcp typedTool
            ? typedTool.ToolName?.Trim() ?? typedTool.ToolTitle?.Trim()
            : PermissionEvaluator.ReadStringProperty(request, "ToolName", "ToolTitle", "Tool", "Method");
        var argumentsPreview = PermissionEvaluator.ReadPermissionObjectString(request, "Args", "Arguments", "Params", "Input");

        if (string.IsNullOrWhiteSpace(serverName))
        {
            var description = PermissionEvaluator.DescribePermissionRequest(request);
            var fallback = _promptCommandApproval(
                description,
                PermissionEvaluator.BuildPermissionPromptReason("mcp"),
                null);
            return fallback == ApprovalResult.Approved ? Approved() : Rejected();
        }

        if (_approvedMcpServersForSession.Contains(serverName))
        {
            return Approved();
        }

        if (McpReadOnlyHeuristic.IsReadOnlyToolName(toolName))
        {
            return Approved();
        }

        var role = _getMcpServerRole(serverName);
        var approval = _promptMcpApproval(
            serverName,
            toolName ?? "(unknown tool)",
            argumentsPreview,
            role);

        switch (approval)
        {
            case McpApprovalResult.ApproveOnce:
                return Approved();

            case McpApprovalResult.ApproveServerForSession:
                _approvedMcpServersForSession.Add(serverName);
                return Approved();

            case McpApprovalResult.ApproveServerPersist:
                _approvedMcpServersForSession.Add(serverName);
                if (_appSettings != null)
                {
                    try
                    {
                        AppSettingsStore.AddPersistedApprovedMcpServer(_appSettings, serverName);
                    }
                    catch
                    {
                        // Persistence is best-effort; the session approval still applies.
                    }
                }
                return Approved();

            default:
                return Rejected();
        }
    }

    private static PermissionRequestResult Approved()
        => new() { Kind = PermissionRequestResultKind.Approved };

    private static PermissionRequestResult Rejected()
        => new() { Kind = PermissionRequestResultKind.Rejected };

    private static string? GetUrlFromPermissionRequest(PermissionRequest request)
    {
        return request is PermissionRequestUrl urlRequest
            ? urlRequest.Url?.Trim()
            : PermissionEvaluator.ReadStringProperty(request, "Url", "Uri");
    }

    private static string? GetUrlPermissionIntention(PermissionRequest request)
    {
        return request is PermissionRequestUrl urlRequest
            ? urlRequest.Intention?.Trim()
            : PermissionEvaluator.ReadStringProperty(request, "Intention", "Reason", "Purpose");
    }

    private static string? NormalizeUrlForApproval(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)
            ? parsed.AbsoluteUri
            : url.Trim();
    }

}
