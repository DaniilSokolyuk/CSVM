using System;
using System.Collections.Generic;
using System.Reflection;
using CSVM.Flight.Modes;
using CSVM.Utils;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Splitscreen stunt-race bookkeeping (<see cref="StuntRace"/>), off-engine: who placed in what
/// order, a rematch's reset, and the standings sort. <see cref="StuntRace"/> itself routes
/// <c>GD.Print</c> through <see cref="Log"/>, safe under <c>TestHostLogSink</c>'s no-op sink.
/// ⚠ Never call <see cref="StuntMission"/>'s <c>Load</c>/<c>Complete</c> directly: both call
/// <c>GD.Print</c>/<c>GD.PushWarning</c>, which crashes the test host outside the engine
/// (verified). A fake mission comes from its private constructor via reflection, and a finish is
/// simulated by setting <c>Elapsed</c> and raising the private <c>RunCompleted</c> delegate.
/// </summary>
public class StuntRaceTests
{
    [Fact]
    public void FinishOrderAssignsPlacingsInFinishOrderNotEntryOrder()
    {
        var lines = new List<string>();
        using (Log.PushConsoleSink(lines.Add))
        {
            var race = new StuntRace();
            var a = race.Add(0, FakeMission(1), "Bloodhawk");
            var b = race.Add(1, FakeMission(1), "Kestrel");
            var c = race.Add(2, FakeMission(1), "Hoplite");

            int raceCompletedCount = 0;
            race.RaceCompleted += () => raceCompletedCount++;

            // Finished out of entry order: C first, then A, then B.
            FinishAt(c.Mission, 12.3f);
            FinishAt(a.Mission, 34.5f);
            FinishAt(b.Mission, 56.7f);

            Assert.Equal(1, c.Rank);
            Assert.Equal(2, a.Rank);
            Assert.Equal(3, b.Rank);
            Assert.Equal(12.3f, c.FinishTime);
            Assert.Equal(34.5f, a.FinishTime);
            Assert.Equal(56.7f, b.FinishTime);
            Assert.Equal(3, race.FinishedCount);
            Assert.True(race.AllFinished);
            Assert.Equal(1, raceCompletedCount); // fired exactly once, on the last finisher

            // The seam this item exists to prove: the finish line reached the installed sink,
            // once per racer, plus the one race-complete line, not the real GD.Print.
            Assert.Equal(4, lines.Count);
            Assert.Contains(lines, l => l.Contains(a.Tag) && l.Contains("finished"));
            Assert.Contains(lines, l => l.Contains(b.Tag) && l.Contains("finished"));
            Assert.Contains(lines, l => l.Contains(c.Tag) && l.Contains("finished"));
            Assert.Contains(lines, l => l.Contains("RACE COMPLETE"));
        }
    }

    [Fact]
    public void RestartClearsPlacingsClocksAndMissionProgressForARematch()
    {
        var race = new StuntRace();
        var a = race.Add(0, FakeMission(2), "Bloodhawk");
        var b = race.Add(1, FakeMission(2), "Kestrel");

        FinishAt(a.Mission, 10f);
        FinishAt(b.Mission, 20f);
        Assert.True(race.AllFinished);

        race.Restart();

        Assert.Equal(0, a.Rank);
        Assert.Equal(0, b.Rank);
        Assert.Equal(0f, a.FinishTime);
        Assert.Equal(0f, b.FinishTime);
        Assert.Equal(0, race.FinishedCount);
        Assert.False(race.AllFinished);
        // Restart() runs the real, public StuntMission.Reset(), asserting through it is the
        // point: a rematch must not leave the previous run's clock or completions behind.
        Assert.False(a.Mission.AllComplete);
        Assert.Equal(0f, a.Mission.Elapsed);
        Assert.Equal(0, a.Mission.CompletedCount);
    }

    [Fact]
    public void StandingsRanksAFinisherFirstThenStillFlyingByZonesThenByTheFasterClockOnATie()
    {
        var race = new StuntRace();
        var winner = race.Add(0, FakeMission(3), "Bloodhawk");
        var ahead = race.Add(1, FakeMission(3), "Kestrel");      // 3 zones cleared, still flying
        var tiedSlower = race.Add(2, FakeMission(3), "Hoplite"); // 2 zones, slower clock
        var tiedFaster = race.Add(3, FakeMission(3), "Autogyro"); // 2 zones, faster clock, the tie

        FinishAt(winner.Mission, 99f);
        SetProgress(ahead.Mission, completedCount: 3, elapsed: 40f);
        SetProgress(tiedSlower.Mission, completedCount: 2, elapsed: 30f);
        SetProgress(tiedFaster.Mission, completedCount: 2, elapsed: 20f);

        var order = new List<Racer>(race.Standings());

        Assert.Equal(new[] { winner, ahead, tiedFaster, tiedSlower }, order);
    }

    // ---- StuntMission off-engine test doubles (see the class doc-comment for why) ----

    private static StuntMission FakeMission(int zoneCount)
    {
        var zones = new List<StuntZone>();
        for (int i = 0; i < zoneCount; i++)
            zones.Add(new StuntZone { DzName = $"dz{i}" });
        var ctor = typeof(StuntMission).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(List<StuntZone>) }, null)!;
        return (StuntMission)ctor.Invoke(new object[] { zones });
    }

    private static void SetProgress(StuntMission mission, int completedCount, float elapsed)
    {
        typeof(StuntMission).GetProperty(nameof(StuntMission.CompletedCount))!
            .GetSetMethod(nonPublic: true)!.Invoke(mission, new object[] { completedCount });
        typeof(StuntMission).GetProperty(nameof(StuntMission.Elapsed))!
            .GetSetMethod(nonPublic: true)!.Invoke(mission, new object[] { elapsed });
    }

    private static void FinishAt(StuntMission mission, float elapsed)
    {
        SetProgress(mission, mission.TotalCount, elapsed);
        var field = typeof(StuntMission).GetField(nameof(StuntMission.RunCompleted),
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((Action?)field.GetValue(mission))?.Invoke();
    }
}
