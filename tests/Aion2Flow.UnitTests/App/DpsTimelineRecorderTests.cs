using Cloris.Aion2Flow.ViewModels;

namespace Cloris.Aion2Flow.Tests.App;

public sealed class DpsTimelineRecorderTests
{
    [Fact]
    public void GetDpsSeries_ComputesRollingWindowFromCumulativeDamage()
    {
        var recorder = new DpsTimelineRecorder();
        var encounter = Guid.NewGuid();

        // 100 damage per second for 10 seconds.
        for (var second = 0; second < 10; second++)
            recorder.Record(encounter, 1, second * 1000L, (second + 1) * 100d);

        var series = recorder.GetDpsSeries(encounter, 1, windowSeconds: 5);

        Assert.Equal(10, series.Length);
        Assert.All(series, value => Assert.Equal(100d, value, precision: 6));
    }

    [Fact]
    public void Record_FillsGapsWithLastCumulativeValue()
    {
        var recorder = new DpsTimelineRecorder();
        var encounter = Guid.NewGuid();

        recorder.Record(encounter, 1, 0, 500);
        recorder.Record(encounter, 1, 6_000, 500);

        var series = recorder.GetDpsSeries(encounter, 1, windowSeconds: 5);

        Assert.Equal(7, series.Length);
        Assert.Equal(0d, series[6], precision: 6);
    }

    [Fact]
    public void Record_IgnoresDecreasingCumulativeSamples()
    {
        var recorder = new DpsTimelineRecorder();
        var encounter = Guid.NewGuid();

        recorder.Record(encounter, 1, 0, 300);
        recorder.Record(encounter, 1, 0, 100);

        var series = recorder.GetDpsSeries(encounter, 1);

        Assert.Single(series);
        Assert.Equal(300d, series[0], precision: 6);
    }

    [Fact]
    public void Recorder_EvictsOldestEncounterBeyondLimit()
    {
        var recorder = new DpsTimelineRecorder(maxEncounters: 2);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        recorder.Record(first, 1, 0, 1);
        recorder.Record(second, 1, 0, 1);
        recorder.Record(third, 1, 0, 1);

        Assert.False(recorder.HasEncounter(first));
        Assert.True(recorder.HasEncounter(second));
        Assert.True(recorder.HasEncounter(third));
    }

    [Fact]
    public void GetDpsSeries_UnknownCombatant_ReturnsEmpty()
    {
        var recorder = new DpsTimelineRecorder();
        Assert.Empty(recorder.GetDpsSeries(Guid.NewGuid(), 42));
    }
}
