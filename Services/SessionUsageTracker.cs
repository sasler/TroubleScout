namespace TroubleScout.Services;

internal sealed class SessionUsageTracker
{
    private long _totalInputTokens;
    private long _totalOutputTokens;
    private int _totalTurns;
    private decimal _estimatedCostUsd;
    private double _estimatedPremiumRequests;

    public long TotalInputTokens => _totalInputTokens;
    public long TotalOutputTokens => _totalOutputTokens;
    public int TotalTurns => _totalTurns;
    public decimal EstimatedCostUsd => _estimatedCostUsd;
    public double EstimatedPremiumRequests => _estimatedPremiumRequests;

    /// <summary>
    /// Record a turn's token usage and calculate costs.
    /// For BYOK: uses per-million-token pricing.
    /// For GitHub: accumulates premium request multiplier.
    /// </summary>
    public void RecordTurn(int? inputTokens, int? outputTokens, TroubleshootingSession.ByokPriceInfo? pricing, double? premiumMultiplier)
    {
        var inTokens = inputTokens ?? 0;
        var outTokens = outputTokens ?? 0;

        _totalInputTokens += inTokens;
        _totalOutputTokens += outTokens;
        _totalTurns++;

        if (pricing != null)
        {
            if (pricing.InputPricePerMillionTokens.HasValue)
            {
                _estimatedCostUsd += (decimal)inTokens / 1_000_000m * pricing.InputPricePerMillionTokens.Value;
            }

            if (pricing.OutputPricePerMillionTokens.HasValue)
            {
                _estimatedCostUsd += (decimal)outTokens / 1_000_000m * pricing.OutputPricePerMillionTokens.Value;
            }
        }

        if (premiumMultiplier.HasValue && premiumMultiplier.Value > 0)
        {
            _estimatedPremiumRequests += premiumMultiplier.Value;
        }
    }

    public void Reset()
    {
        _totalInputTokens = 0;
        _totalOutputTokens = 0;
        _totalTurns = 0;
        _estimatedCostUsd = 0;
        _estimatedPremiumRequests = 0;
    }

    /// <summary>
    /// Format a summary string for display in the status bar.
    /// </summary>
    public string? GetCostEstimateDisplay()
    {
        if (_estimatedCostUsd > 0)
        {
            return $"~${_estimatedCostUsd:0.####}";
        }

        if (_estimatedPremiumRequests > 0)
        {
            return $"~{_estimatedPremiumRequests:0.#} premium reqs";
        }

        return null;
    }
}
