using FluentAssertions;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests;

public class SessionStatsTests
{
    private static SessionUsageTracker NewTracker() => (SessionUsageTracker)Activator.CreateInstance(
        typeof(SessionUsageTracker), nonPublic: true)!;

    [Fact]
    public void RecordCompletedTurn_IncrementsCounters()
    {
        var t = NewTracker();
        t.RecordCompletedTurn(TimeSpan.FromSeconds(1), toolDelta: 2, TurnOutcome.Success);
        t.RecordCompletedTurn(TimeSpan.FromSeconds(2), toolDelta: 0, TurnOutcome.Success);

        t.CompletedTurns.Should().Be(2);
        t.TotalToolCalls.Should().Be(2);
        t.FailedTurns.Should().Be(0);
        t.CancelledTurns.Should().Be(0);
    }

    [Fact]
    public void RecordCompletedTurn_TracksFailedAndCancelledOutcomes()
    {
        var t = NewTracker();
        t.RecordCompletedTurn(TimeSpan.FromSeconds(1), 0, TurnOutcome.Failed);
        t.RecordCompletedTurn(TimeSpan.FromSeconds(1), 0, TurnOutcome.Cancelled);
        t.RecordCompletedTurn(TimeSpan.FromSeconds(1), 0, TurnOutcome.Cancelled);

        t.FailedTurns.Should().Be(1);
        t.CancelledTurns.Should().Be(2);
        t.CompletedTurns.Should().Be(3);
    }

    [Fact]
    public void GetTurnElapsedQuantile_ReturnsNullWhenEmpty()
    {
        var t = NewTracker();
        t.GetTurnElapsedQuantile(0.5).Should().BeNull();
        t.GetTurnElapsedQuantile(0.95).Should().BeNull();
    }

    [Fact]
    public void GetTurnElapsedQuantile_SingleSample_ReturnsThatSample()
    {
        var t = NewTracker();
        t.RecordCompletedTurn(TimeSpan.FromSeconds(7), 0, TurnOutcome.Success);
        t.GetTurnElapsedQuantile(0.5).Should().Be(TimeSpan.FromSeconds(7));
        t.GetTurnElapsedQuantile(0.95).Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public void GetTurnElapsedQuantile_OnSequence1To100_ReturnsExpectedQuantiles()
    {
        var t = NewTracker();
        for (int i = 1; i <= 100; i++)
        {
            t.RecordCompletedTurn(TimeSpan.FromMilliseconds(i), 0, TurnOutcome.Success);
        }

        // Linear interpolation on 100 samples: p50 ≈ 50.5, p95 ≈ 95.05.
        var p50 = t.GetTurnElapsedQuantile(0.5)!.Value.TotalMilliseconds;
        var p95 = t.GetTurnElapsedQuantile(0.95)!.Value.TotalMilliseconds;
        p50.Should().BeApproximately(50.5, 0.5);
        p95.Should().BeApproximately(95.05, 0.5);
    }

    [Fact]
    public void GetTurnToolCountQuantile_TracksToolDeltas()
    {
        var t = NewTracker();
        t.RecordCompletedTurn(TimeSpan.FromSeconds(1), toolDelta: 0, TurnOutcome.Success);
        t.RecordCompletedTurn(TimeSpan.FromSeconds(1), toolDelta: 4, TurnOutcome.Success);
        t.RecordCompletedTurn(TimeSpan.FromSeconds(1), toolDelta: 2, TurnOutcome.Success);

        t.TotalToolCalls.Should().Be(6);
        t.GetTurnToolCountQuantile(0.5)!.Value.Should().BeApproximately(2.0, 0.001);
    }

    [Fact]
    public void Reset_ClearsCompletedTurnStats()
    {
        var t = NewTracker();
        t.RecordCompletedTurn(TimeSpan.FromSeconds(1), 3, TurnOutcome.Success);
        t.Reset();
        t.CompletedTurns.Should().Be(0);
        t.TotalToolCalls.Should().Be(0);
        t.GetTurnElapsedQuantile(0.5).Should().BeNull();
    }

    [Fact]
    public void GetTurnElapsedQuantile_NegativeOrAboveOne_Throws()
    {
        var t = NewTracker();
        t.RecordCompletedTurn(TimeSpan.FromSeconds(1), 0, TurnOutcome.Success);
        Assert.Throws<ArgumentOutOfRangeException>(() => t.GetTurnElapsedQuantile(-0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => t.GetTurnElapsedQuantile(1.1));
    }
}
