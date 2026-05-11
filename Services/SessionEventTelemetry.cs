using System.Reflection;
using GitHub.Copilot.SDK;

namespace TroubleScout.Services;

internal sealed record SessionUsageSnapshot(
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    int? InputTokens,
    int? OutputTokens,
    int? MaxContextTokens,
    int? UsedContextTokens,
    int? FreeContextTokens)
{
    public bool HasAny => PromptTokens.HasValue || CompletionTokens.HasValue || TotalTokens.HasValue ||
                          InputTokens.HasValue || OutputTokens.HasValue || MaxContextTokens.HasValue ||
                          UsedContextTokens.HasValue || FreeContextTokens.HasValue;
}

internal sealed class SessionEventTelemetry
{
    private readonly SessionUsageTracker _sessionUsageTracker;
    private readonly ISet<string> _runtimeMcpServers;
    private readonly ISet<string> _runtimeSkills;
    private readonly Func<bool> _useByokOpenAi;
    private readonly Func<ModelInfo?> _getSelectedModelInfo;
    private readonly Func<ByokPriceInfo?> _getActiveByokPricing;
    private readonly Action<string> _setSelectedModel;
    private readonly Action<string?> _setSelectedReasoningEffort;
    private readonly Action<string> _setCopilotVersion;

    internal SessionEventTelemetry(
        SessionUsageTracker sessionUsageTracker,
        ISet<string> runtimeMcpServers,
        ISet<string> runtimeSkills,
        Func<bool> useByokOpenAi,
        Func<ModelInfo?> getSelectedModelInfo,
        Func<ByokPriceInfo?> getActiveByokPricing,
        Action<string> setSelectedModel,
        Action<string?> setSelectedReasoningEffort,
        Action<string> setCopilotVersion)
    {
        _sessionUsageTracker = sessionUsageTracker;
        _runtimeMcpServers = runtimeMcpServers;
        _runtimeSkills = runtimeSkills;
        _useByokOpenAi = useByokOpenAi;
        _getSelectedModelInfo = getSelectedModelInfo;
        _getActiveByokPricing = getActiveByokPricing;
        _setSelectedModel = setSelectedModel;
        _setSelectedReasoningEffort = setSelectedReasoningEffort;
        _setCopilotVersion = setCopilotVersion;
    }

    internal SessionUsageSnapshot? LastUsage { get; private set; }
    internal double? SessionPremiumRequestCost { get; private set; }

    internal void ResetForNewSession()
    {
        LastUsage = null;
        SessionPremiumRequestCost = null;
    }

    internal void HandleSessionLifecycleStateEvent(SessionEvent evt)
    {
        CaptureCapabilityUsage(evt);

        switch (evt)
        {
            case SessionStartEvent startEvt:
                if (!string.IsNullOrWhiteSpace(startEvt.Data?.SelectedModel))
                {
                    _setSelectedModel(startEvt.Data.SelectedModel);
                }

                _setSelectedReasoningEffort(ReasoningEffortHelper.Normalize(startEvt.Data?.ReasoningEffort));

                if (!string.IsNullOrWhiteSpace(startEvt.Data?.CopilotVersion))
                {
                    _setCopilotVersion(startEvt.Data.CopilotVersion);
                }

                break;

            case SessionModelChangeEvent modelChangeEvt:
                if (!string.IsNullOrWhiteSpace(modelChangeEvt.Data?.NewModel))
                {
                    _setSelectedModel(modelChangeEvt.Data.NewModel);
                }

                _setSelectedReasoningEffort(ReasoningEffortHelper.Normalize(modelChangeEvt.Data?.ReasoningEffort));

                break;

            case AssistantUsageEvent usageEvt:
                CaptureUsageMetrics(usageEvt);
                if (!string.IsNullOrEmpty(usageEvt.Data?.Model))
                {
                    _setSelectedModel(usageEvt.Data.Model);
                }

                break;
        }
    }

    internal async Task RefreshSessionUsageMetricsAsync(CopilotSession? copilotSession, CancellationToken cancellationToken)
    {
        if (copilotSession == null || _useByokOpenAi())
        {
            return;
        }

        try
        {
            using var metricsTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            metricsTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            var metrics = await copilotSession.Rpc.Usage.GetMetricsAsync(metricsTimeoutCts.Token);
            SessionPremiumRequestCost = metrics.TotalPremiumRequestCost > 0
                ? metrics.TotalPremiumRequestCost
                : null;
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation/timeout so status-bar rendering cannot stall the session loop.
        }
        catch
        {
            // Ignore metrics fetch failures and leave the display empty rather than falling back to a guessed premium cost.
        }
    }

    private void CaptureUsageMetrics(AssistantUsageEvent usageEvt)
    {
        var data = usageEvt.Data;
        if (data == null)
        {
            return;
        }

        var usageObj = GetPropertyValue(data, "Usage") ?? data;

        var promptTokens = ReadIntProperty(usageObj, "PromptTokens", "InputTokens", "RequestTokens");
        var completionTokens = ReadIntProperty(usageObj, "CompletionTokens", "OutputTokens", "ResponseTokens");
        var totalTokens = ReadIntProperty(usageObj, "TotalTokens", "Tokens");

        var inputTokens = ReadIntProperty(usageObj, "InputTokens", "PromptTokens", "RequestTokens");
        var outputTokens = ReadIntProperty(usageObj, "OutputTokens", "CompletionTokens", "ResponseTokens");

        var usedContext = ReadIntProperty(usageObj, "UsedTokens", "ContextTokensUsed", "ContextTokens", "UsedContextTokens");
        var maxContext = ReadIntProperty(usageObj, "MaxTokens", "MaxContextTokens", "ContextWindowTokens", "ContextTokensMax");
        var freeContext = ReadIntProperty(usageObj, "FreeTokens", "RemainingTokens", "ContextTokensRemaining");

        if (maxContext.HasValue && usedContext.HasValue && !freeContext.HasValue)
        {
            freeContext = Math.Max(0, maxContext.Value - usedContext.Value);
        }

        var snapshot = new SessionUsageSnapshot(
            promptTokens,
            completionTokens,
            totalTokens,
            inputTokens,
            outputTokens,
            maxContext,
            usedContext,
            freeContext);

        if (snapshot.HasAny)
        {
            LastUsage = snapshot;

            _sessionUsageTracker.RecordTurn(
                snapshot.InputTokens ?? snapshot.PromptTokens,
                snapshot.OutputTokens ?? snapshot.CompletionTokens,
                _getActiveByokPricing(),
                GetActivePremiumMultiplier());
        }
    }

    private double? GetActivePremiumMultiplier()
    {
        if (_useByokOpenAi())
        {
            return null;
        }

        return _getSelectedModelInfo()?.Billing?.Multiplier;
    }

    private void CaptureCapabilityUsage(SessionEvent evt)
    {
        if (evt is ToolExecutionStartEvent toolStart)
        {
            var mcpServerName = ReadStringProperty(toolStart.Data, "McpServerName", "MCPServerName", "ServerName");
            if (!string.IsNullOrWhiteSpace(mcpServerName))
            {
                _runtimeMcpServers.Add(mcpServerName);
            }
        }

        if (string.Equals(evt.Type, "skill.invoked", StringComparison.OrdinalIgnoreCase))
        {
            var eventData = GetPropertyValue(evt, "Data");
            var skillName = ReadStringProperty(eventData, "Name", "SkillName", "Id");
            if (!string.IsNullOrWhiteSpace(skillName))
            {
                _runtimeSkills.Add(skillName);
            }
        }
    }

    private static string? ReadStringProperty(object? instance, params string[] propertyNames)
    {
        if (instance == null)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            var prop = instance.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var value = prop?.GetValue(instance);
            if (value is string stringValue && !string.IsNullOrWhiteSpace(stringValue))
            {
                return stringValue.Trim();
            }
        }

        return null;
    }

    private static object? GetPropertyValue(object instance, string propertyName)
    {
        var prop = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return prop?.GetValue(instance);
    }

    private static int? ReadIntProperty(object? instance, params string[] propertyNames)
    {
        if (instance == null)
        {
            return null;
        }

        foreach (var name in propertyNames)
        {
            var prop = instance.GetType().GetProperty(
                name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null)
            {
                continue;
            }

            var value = prop.GetValue(instance);
            if (value == null)
            {
                continue;
            }

            if (value is int i)
            {
                return i;
            }

            if (value is long l)
            {
                return (int)l;
            }

            if (value is double d)
            {
                return (int)d;
            }

            if (value is float f)
            {
                return (int)f;
            }
        }

        return null;
    }
}
