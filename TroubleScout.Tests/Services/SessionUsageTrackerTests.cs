using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests.Services;

public class SessionUsageTrackerTests
{
    [Fact]
    public void RecordTurn_AccumulatesTokens()
    {
        var tracker = new SessionUsageTracker();

        tracker.RecordTurn(100, 50, null, null);
        tracker.RecordTurn(200, 100, null, null);

        Assert.Equal(300, tracker.TotalInputTokens);
        Assert.Equal(150, tracker.TotalOutputTokens);
        Assert.Equal(2, tracker.TotalTurns);
    }

    [Fact]
    public void RecordTurn_ByokCost_CalculatesCorrectly()
    {
        var tracker = new SessionUsageTracker();
        var pricing = new TroubleshootingSession.ByokPriceInfo(2.50m, 10.00m, "$2.50/M in, $10.00/M out");

        tracker.RecordTurn(1_000_000, 500_000, pricing, null);

        Assert.Equal(7.50m, tracker.EstimatedCostUsd);
    }

    [Fact]
    public void RecordTurn_PremiumMultiplier_Accumulates()
    {
        var tracker = new SessionUsageTracker();

        tracker.RecordTurn(100, 50, null, 0.25);
        tracker.RecordTurn(100, 50, null, 0.25);

        Assert.Equal(0.5, tracker.EstimatedPremiumRequests);
    }

    [Fact]
    public void GetCostEstimateDisplay_ByokCost_ShowsDollarAmount()
    {
        var tracker = new SessionUsageTracker();
        var pricing = new TroubleshootingSession.ByokPriceInfo(2.50m, 10.00m, null);

        tracker.RecordTurn(100_000, 50_000, pricing, null);

        var display = tracker.GetCostEstimateDisplay();
        Assert.NotNull(display);
        Assert.StartsWith("~$", display);
    }

    [Fact]
    public void GetCostEstimateDisplay_PremiumRequests_ShowsMultiplier()
    {
        var tracker = new SessionUsageTracker();

        tracker.RecordTurn(100, 50, null, 1.0);

        var display = tracker.GetCostEstimateDisplay();
        Assert.NotNull(display);
        Assert.Contains("premium reqs", display);
    }

    [Fact]
    public void GetCostEstimateDisplay_NoUsage_ReturnsNull()
    {
        var tracker = new SessionUsageTracker();

        Assert.Null(tracker.GetCostEstimateDisplay());
    }

    [Fact]
    public void Reset_ClearsAll()
    {
        var tracker = new SessionUsageTracker();
        var pricing = new TroubleshootingSession.ByokPriceInfo(2.50m, 10.00m, null);

        tracker.RecordTurn(100, 50, pricing, 1.0);
        tracker.Reset();

        Assert.Equal(0, tracker.TotalInputTokens);
        Assert.Equal(0, tracker.TotalOutputTokens);
        Assert.Equal(0, tracker.TotalTurns);
        Assert.Equal(0m, tracker.EstimatedCostUsd);
        Assert.Equal(0.0, tracker.EstimatedPremiumRequests);
    }

    [Fact]
    public void RecordTurn_NullTokens_TreatedAsZero()
    {
        var tracker = new SessionUsageTracker();

        tracker.RecordTurn(null, null, null, null);

        Assert.Equal(0, tracker.TotalInputTokens);
        Assert.Equal(0, tracker.TotalOutputTokens);
        Assert.Equal(1, tracker.TotalTurns);
    }
}
