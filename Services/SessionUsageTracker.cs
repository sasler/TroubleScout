using System.Globalization;

namespace TroubleScout.Services;

public enum TurnOutcome
{
    Success,
    Failed,
    Cancelled
}

internal sealed class SessionUsageTracker
{
    private long _totalInputTokens;
    private long _totalOutputTokens;
    private int _totalTurns;
    private decimal _estimatedCostUsd;
    private double _estimatedPremiumRequests;

    private readonly List<double> _turnElapsedMillis = new();
    private readonly List<int> _turnToolCounts = new();
    private int _completedTurns;
    private int _failedTurns;
    private int _cancelledTurns;
    private int _totalToolCalls;
    private readonly object _completedTurnLock = new();

    public long TotalInputTokens => _totalInputTokens;
    public long TotalOutputTokens => _totalOutputTokens;
    public int TotalTurns => _totalTurns;
    public decimal EstimatedCostUsd => _estimatedCostUsd;
    public double EstimatedPremiumRequests => _estimatedPremiumRequests;

    public int CompletedTurns { get { lock (_completedTurnLock) return _completedTurns; } }
    public int FailedTurns { get { lock (_completedTurnLock) return _failedTurns; } }
    public int CancelledTurns { get { lock (_completedTurnLock) return _cancelledTurns; } }
    public int TotalToolCalls { get { lock (_completedTurnLock) return _totalToolCalls; } }

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

        lock (_completedTurnLock)
        {
            _turnElapsedMillis.Clear();
            _turnToolCounts.Clear();
            _completedTurns = 0;
            _failedTurns = 0;
            _cancelledTurns = 0;
            _totalToolCalls = 0;
        }
    }

    /// <summary>
    /// Record a completed turn's wall-clock elapsed time, the number of tool
    /// invocations that occurred during the turn, and the turn outcome. This
    /// is independent of <see cref="RecordTurn"/> (which records token usage),
    /// so it works for cancelled and failed turns where token usage may be
    /// missing.
    /// </summary>
    public void RecordCompletedTurn(TimeSpan elapsed, int toolDelta, TurnOutcome outcome)
    {
        lock (_completedTurnLock)
        {
            _completedTurns++;
            _turnElapsedMillis.Add(elapsed.TotalMilliseconds);
            _turnToolCounts.Add(toolDelta);
            _totalToolCalls += toolDelta;

            switch (outcome)
            {
                case TurnOutcome.Failed: _failedTurns++; break;
                case TurnOutcome.Cancelled: _cancelledTurns++; break;
            }
        }
    }

    /// <summary>
    /// Compute a quantile (0.5 = median, 0.95 = p95) over the recorded
    /// per-turn elapsed times. Returns null when no turns have been recorded.
    /// Linear-interpolation method on a sorted copy.
    /// </summary>
    public TimeSpan? GetTurnElapsedQuantile(double quantile)
    {
        lock (_completedTurnLock)
        {
            return ComputeQuantile(_turnElapsedMillis, quantile) is double ms ? TimeSpan.FromMilliseconds(ms) : null;
        }
    }

    public double? GetTurnToolCountQuantile(double quantile)
    {
        lock (_completedTurnLock)
        {
            return ComputeQuantile(_turnToolCounts.Select(i => (double)i).ToList(), quantile);
        }
    }

    private static double? ComputeQuantile(List<double> values, double quantile)
    {
        if (values.Count == 0) return null;
        if (quantile < 0 || quantile > 1) throw new ArgumentOutOfRangeException(nameof(quantile));

        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 1) return sorted[0];

        var pos = quantile * (sorted.Count - 1);
        var lower = (int)Math.Floor(pos);
        var upper = (int)Math.Ceiling(pos);
        if (lower == upper) return sorted[lower];

        var weight = pos - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    /// <summary>
    /// Format a summary string for display in the status bar.
    /// </summary>
    public string? GetCostEstimateDisplay()
    {
        if (_estimatedCostUsd > 0)
        {
            return $"~${_estimatedCostUsd.ToString("0.####", CultureInfo.InvariantCulture)} est.";
        }

        if (_estimatedPremiumRequests > 0)
        {
            return $"~{_estimatedPremiumRequests.ToString("0.#", CultureInfo.InvariantCulture)} premium reqs";
        }

        return null;
    }
}
