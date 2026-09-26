using System.Collections.Generic;
using CSVM.Session.Roster;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The decoded egen timing law (docs/formats/mission-entities/enemy-generators.md "The generator
/// cycle"), pure and engine-free: period composition, the hold-not-cancel altitude gate,
/// max_active blocking, the credit rule under which an uncredited cycle never launches, and the
/// door lead a released spawn waits out rather than flying through a hangar that is still shut.
/// </summary>
public class GeneratorCycleTests
{
    private const float Dt = 0.1f;
    private const float HighAltitude = 1000f;

    // Enough credit that the budget never enters a timing assertion.
    private const int Plenty = 99;

    [Fact]
    public void TheGapBetweenWavesIsIndPlusWaveNotWaveAlone()
    {
        // wave_size 1: every spawn completes a wave, so consecutive spawns sit a full
        // ind_period + wave_period apart (20 s here), never wave_period alone.
        var cycle = Credited(maxActive: 99, waveSize: 1, wavePeriod: 10f, indPeriod: 10f);
        var times = SpawnTimes(cycle, seconds: 65f);
        Assert.Equal(new[] { 20f, 40f, 60f }, times);
    }

    [Fact]
    public void InsideAWaveTheGapIsIndPeriodAlone()
    {
        // wave_size 3, ind 2, wave 10: a wave's individuals arrive 2 s apart, then the next
        // wave starts a composed 12 s after the wave's last spawn.
        var cycle = Credited(maxActive: 99, waveSize: 3, wavePeriod: 10f, indPeriod: 2f);
        var times = SpawnTimes(cycle, seconds: 30f);
        Assert.Equal(new[] { 12f, 14f, 16f, 28f, 30f }, times);
    }

    [Fact]
    public void TheAltitudeGateHoldsAndDoesNotCancel()
    {
        // Below the gate nothing spawns, but the timer keeps running (hold, not cancel): the
        // held spawn follows the FIRST step at altitude by the door lead, not a full period.
        var cycle = Credited(maxActive: 99, waveSize: 1, wavePeriod: 10f, indPeriod: 10f,
            minAltitude: 150f);
        for (float t = 0f; t < 60f; t += Dt)
        {
            Assert.False(cycle.Step(Dt, hostAltitude: 100f));
        }
        Assert.False(cycle.Step(Dt, hostAltitude: 200f));   // the hangar opens on this step
        Assert.True(cycle.DoorOpen);
        Assert.InRange(StepsUntilSpawn(cycle) * Dt, 3.9f, 4.15f);
    }

    [Fact]
    public void AHeldWaveResumesMidWaveNotFromTheTop()
    {
        // Block after the first individual of a 3-wave; on unblock the wave CONTINUES (the wave
        // counter was untouched), so the remaining gaps are ind_period, not the inter-wave gap.
        var cycle = Credited(maxActive: 99, waveSize: 3, wavePeriod: 10f, indPeriod: 2f,
            minAltitude: 150f);
        float t = 0f;
        var spawns = new List<float>();
        for (; t < 12.05f; t += Dt)
        {
            if (cycle.Step(Dt, HighAltitude))
            {
                spawns.Add(t + Dt);
            }
        }
        Assert.Single(spawns);   // wave started: one individual out at 12 s
        for (float held = 0f; held < 5f; held += Dt)
        {
            Assert.False(cycle.Step(Dt, hostAltitude: 100f));   // held below the gate
            t += Dt;
        }
        // The hangar shut while the wave was held, so the individual waits out the door lead.
        Assert.False(cycle.Step(Dt, HighAltitude));
        Assert.InRange(StepsUntilSpawn(cycle) * Dt, 3.9f, 4.15f);
        // The wave's third individual follows one ind_period later, not one inter-wave gap.
        int steps = 0;
        while (!cycle.Step(Dt, HighAltitude))
        {
            steps++;
        }
        Assert.InRange(steps * Dt, 1.8f, 2.05f);
    }

    [Fact]
    public void MaxActiveBlocksAndADeathFrees()
    {
        var cycle = Credited(maxActive: 1, waveSize: 1, wavePeriod: 1f, indPeriod: 1f);
        Assert.Single(SpawnTimes(cycle, seconds: 30f));
        Assert.Equal(1, cycle.Active);
        cycle.SpawnRemoved();
        // The timer accumulated the whole while (hold, not cancel), but the hangar shut under
        // the block, so the freed slot's spawn follows the reopening by the door lead.
        Assert.False(cycle.Step(Dt, HighAltitude));
        Assert.InRange(StepsUntilSpawn(cycle) * Dt, 3.9f, 4.15f);
    }

    [Fact]
    public void AnUncreditedCycleNeverLaunches()
    {
        // The decoded rule: the authored capacity is never read, every cycle starts at zero
        // remaining, and the block holds (timer running, door shut) until something credits it.
        var cycle = new GeneratorCycle(maxActive: 99, waveSize: 1, wavePeriod: 1f, indPeriod: 1f,
            minAltitude: null);
        Assert.Empty(SpawnTimes(cycle, seconds: 60f));
        Assert.False(cycle.DoorOpen);
        Assert.Equal(0, cycle.CapacityRemaining);
    }

    [Fact]
    public void ACreditLaunchesExactlyThatManyThenHolds()
    {
        var cycle = new GeneratorCycle(maxActive: 99, waveSize: 1, wavePeriod: 1f, indPeriod: 1f,
            minAltitude: null);
        cycle.GrantCapacity(2);
        Assert.Equal(2, SpawnTimes(cycle, seconds: 60f).Count);
        Assert.Equal(0, cycle.CapacityRemaining);
        // A later credit reopens the hangar on its first step and the held spawn follows the
        // door lead: the timer ran on, but the panels have not moved yet.
        cycle.GrantCapacity(1);
        Assert.False(cycle.Step(Dt, HighAltitude));
        Assert.True(cycle.DoorOpen);
        Assert.InRange(StepsUntilSpawn(cycle) * Dt, 3.9f, 4.15f);
    }

    [Fact]
    public void AHeldCreditWaitsOutTheDoorLeadAndThenRunsTheComposedPeriod()
    {
        // C4/M03's cargozep1: wave 1, ind 3 + wave 1. Credited 5 long after load, the crediting
        // step opens the hangar and the first Fury waits out the lead rather than flying through
        // panels that have not moved; the other four follow one every 4 s.
        var cycle = new GeneratorCycle(maxActive: 10, waveSize: 1, wavePeriod: 1f, indPeriod: 3f,
            minAltitude: null);
        Assert.Empty(SpawnTimes(cycle, seconds: 30f));
        cycle.GrantCapacity(5);
        var times = SpawnTimes(cycle, seconds: 30f);
        Assert.Equal(5, times.Count);
        Assert.InRange(times[0], GeneratorCycle.DoorLeadSeconds, GeneratorCycle.DoorLeadSeconds + 0.3f);
        for (int i = 1; i < times.Count; i++)
        {
            // One step of float accumulation slack on each 4 s gap.
            Assert.InRange(times[i] - times[i - 1], 3.95f, 4.25f);
        }
    }

    [Fact]
    public void AWaveNeedsCreditForItsWholeRemainder()
    {
        // wave_size 3 with credit 2: the block compares the wave's remainder against the
        // credit, so nothing launches until the third credit arrives.
        var cycle = new GeneratorCycle(maxActive: 99, waveSize: 3, wavePeriod: 1f, indPeriod: 1f,
            minAltitude: null);
        cycle.GrantCapacity(2);
        Assert.Empty(SpawnTimes(cycle, seconds: 30f));
        cycle.GrantCapacity(1);
        Assert.Equal(3, SpawnTimes(cycle, seconds: 30f).Count);
    }

    [Fact]
    public void TheDoorOpensFourSecondsBeforeADueSpawnAndSpawnsThroughIt()
    {
        // First spawn is due at ind + wave = 20 s, so the door opens at 16 s: closed through
        // 15.9 s, open at 16.0, spawn at 20.0 with the door still open.
        var cycle = Credited(maxActive: 99, waveSize: 1, wavePeriod: 10f, indPeriod: 10f);
        float openedAt = -1f, spawnedAt = -1f;
        for (float t = Dt; t <= 21f; t += Dt)
        {
            bool spawned = cycle.Step(Dt, HighAltitude);
            if (openedAt < 0f && cycle.DoorOpen)
            {
                openedAt = t;
            }
            if (spawned && spawnedAt < 0f)
            {
                spawnedAt = t;
                Assert.True(cycle.DoorOpen);   // the spawn drops through an open door
            }
        }
        Assert.InRange(openedAt, 15.95f, 16.1f);
        Assert.InRange(spawnedAt, 19.95f, 20.1f);
    }

    [Fact]
    public void NoSpawnSharesTheStepATravellingDoorOpensOn()
    {
        // The original's spawn branch tests the door for FULLY open, a state its open animation's
        // completion reaches, so an overdue cycle cannot satisfy both thresholds in one call. Four
        // shapes that leave the timer past its threshold with the hangar shut.
        var overdue = new List<GeneratorCycle>
        {
            LateCredit(), ReleasedGate(), FreedSlot(), LateCreditOnAShortPeriod(),
        };
        foreach (var cycle in overdue)
        {
            Assert.False(cycle.DoorOpen);
            Assert.False(cycle.Step(Dt, HighAltitude));
            Assert.True(cycle.DoorOpen);
            Assert.InRange(StepsUntilSpawn(cycle) * Dt, 3.9f, 4.15f);
        }
    }

    [Fact]
    public void TheDoorClosesEarlyOnlyWhenTheNextSpawnIsMoreThanEightSecondsAway()
    {
        // Gap 20 s: after a spawn the door holds its 4 s minimum, then closes early
        // (16 s still to go > 8) and reopens 4 s before the next spawn.
        var cycle = Credited(maxActive: 99, waveSize: 1, wavePeriod: 10f, indPeriod: 10f);
        RunUntilSpawn(cycle);
        float closedAt = -1f, reopenedAt = -1f;
        for (float t = Dt; t <= 20f; t += Dt)
        {
            bool spawned = cycle.Step(Dt, HighAltitude);
            if (closedAt < 0f && !cycle.DoorOpen)
            {
                closedAt = t;
            }
            if (closedAt > 0f && reopenedAt < 0f && cycle.DoorOpen)
            {
                reopenedAt = t;
            }
            if (spawned)
            {
                break;
            }
        }
        Assert.InRange(closedAt, 3.95f, 4.1f);      // the 4 s minimum open
        Assert.InRange(reopenedAt, 15.95f, 16.1f);  // 4 s before the 20 s spawn
    }

    [Fact]
    public void AFastCyclingGeneratorLeavesItsDoorOpen()
    {
        // Gap 7 s (C1/IA1's authored ind 5 + wave 2): at the 4 s minimum only 3 s remain to
        // the next spawn, not more than 8, so the door never closes between spawns.
        var cycle = Credited(maxActive: 99, waveSize: 1, wavePeriod: 2f, indPeriod: 5f);
        RunUntilSpawn(cycle);
        for (float t = Dt; t <= 15f; t += Dt)
        {
            cycle.Step(Dt, HighAltitude);
            Assert.True(cycle.DoorOpen);
        }
    }

    [Fact]
    public void ABlockedGeneratorClosesOnlyTheDoorAndReopensAheadOfTheReleasedSpawn()
    {
        // Block right after a spawn, with the door open and the since-spawn timer under the
        // 4 s minimum (the decoded rule measures the door's minimum on that same timer).
        var cycle = Credited(maxActive: 99, waveSize: 1, wavePeriod: 10f, indPeriod: 10f,
            minAltitude: 150f);
        RunUntilSpawn(cycle);
        Assert.True(cycle.DoorOpen);        // spawns drop through an open door
        for (float t = Dt; t <= 2f; t += Dt)
        {
            Assert.False(cycle.Step(Dt, hostAltitude: 100f));
        }
        Assert.True(cycle.DoorOpen);        // blocked 2 s in: still inside the 4 s minimum
        for (float t = Dt; t <= 3f; t += Dt)
        {
            Assert.False(cycle.Step(Dt, hostAltitude: 100f));
        }
        Assert.False(cycle.DoorOpen);       // past the minimum: ONLY the door closed
        // Stay blocked well past the 20 s due point: the timer ran on untouched (hold, not
        // cancel) but nothing opens or spawns while blocked.
        for (float t = Dt; t <= 16f; t += Dt)
        {
            Assert.False(cycle.Step(Dt, hostAltitude: 100f));
        }
        Assert.False(cycle.DoorOpen);
        // Release: the door reopens on this step and the held spawn waits out the lead behind it.
        Assert.False(cycle.Step(Dt, HighAltitude));
        Assert.True(cycle.DoorOpen);
        Assert.InRange(StepsUntilSpawn(cycle) * Dt, 3.9f, 4.15f);
    }

    [Fact]
    public void HostDeathDisablesPermanentlyAfterTheGrace()
    {
        // wave 1 every 1 s: a live bay would launch 30 times in 30 s. The kill leaves it
        // launching for the 3 s grace only, and the grace never restarts on a repeat report.
        var cycle = Credited(maxActive: 99, waveSize: 1, wavePeriod: 1f, indPeriod: 1f);
        cycle.HostDied();
        Assert.True(cycle.HostDead);
        Assert.False(cycle.Disabled);
        var spawns = SpawnTimes(cycle, seconds: 30f);
        Assert.All(spawns, t => Assert.True(t <= GeneratorCycle.HostDeathGraceSeconds));
        Assert.NotEmpty(spawns);
        Assert.True(cycle.Disabled);
        cycle.HostDied();
        Assert.Empty(SpawnTimes(cycle, seconds: 30f));
    }

    [Fact]
    public void FixedInstallationHostDeathUsesTheSameGraceAsAZeppelinKill()
    {
        // A fixed installation has no destroyed flag of its own; its death is its authored
        // `healthy` node going inactive, which reaches this same HostDied() call a zeppelin's
        // kill does. The state machine carries no notion of which named report triggered it.
        var cycle = Credited(maxActive: 99, waveSize: 1, wavePeriod: 2f, indPeriod: 2f);
        cycle.HostDied();
        var spawns = SpawnTimes(cycle, seconds: GeneratorCycle.HostDeathGraceSeconds + 10f);
        Assert.All(spawns, t => Assert.True(t <= GeneratorCycle.HostDeathGraceSeconds));
        Assert.True(cycle.Disabled);
    }

    [Fact]
    public void ACreditLandingInsideTheGraceStillLaunches()
    {
        // C5/M04's shape: the Dante's kill lands 0.3 s before OBJECTIVE11 credits Miles's
        // launch. The bay is past its first due point (timer 40 s against a 4 s threshold), so
        // the credit opens the door and the launch waits out the lead, outliving the grace.
        var cycle = new GeneratorCycle(maxActive: 1, waveSize: 1, wavePeriod: 2f, indPeriod: 2f,
            minAltitude: null);
        Assert.Empty(SpawnTimes(cycle, seconds: 40f));
        cycle.HostDied();
        for (float t = Dt; t <= 0.3f; t += Dt)
        {
            Assert.False(cycle.Step(Dt, HighAltitude));
        }
        cycle.GrantCapacity(1);
        Assert.False(cycle.Step(Dt, HighAltitude));
        Assert.InRange(StepsUntilSpawn(cycle) * Dt, 3.9f, 4.15f);
        Assert.Equal(1, cycle.Active);
        // The launch spent, the bay disables on its next step: the grace covered one hold.
        Assert.Empty(SpawnTimes(cycle, seconds: 10f));
        Assert.True(cycle.Disabled);
    }

    [Fact]
    public void ACreditLandingAfterTheGraceIsLost()
    {
        var cycle = new GeneratorCycle(maxActive: 1, waveSize: 1, wavePeriod: 2f, indPeriod: 2f,
            minAltitude: null);
        Assert.Empty(SpawnTimes(cycle, seconds: 40f));
        cycle.HostDied();
        Assert.Empty(SpawnTimes(cycle, seconds: GeneratorCycle.HostDeathGraceSeconds + 1f));
        cycle.GrantCapacity(1);
        Assert.Empty(SpawnTimes(cycle, seconds: 10f));
        Assert.True(cycle.Disabled);
    }

    private static GeneratorCycle Credited(int maxActive, int waveSize, float wavePeriod,
        float indPeriod, float? minAltitude = null)
    {
        var cycle = new GeneratorCycle(maxActive, waveSize, wavePeriod, indPeriod, minAltitude);
        cycle.GrantCapacity(Plenty);
        return cycle;
    }

    private static void RunUntilSpawn(GeneratorCycle cycle)
    {
        for (int i = 0; i < 10_000; i++)
        {
            if (cycle.Step(Dt, HighAltitude))
            {
                return;
            }
        }
        Assert.Fail("no spawn within the budget");
    }

    // C4/M03's cargozep1: nothing until the docking film's callback 800 credits it minutes in.
    private static GeneratorCycle LateCredit()
    {
        var cycle = new GeneratorCycle(maxActive: 10, waveSize: 1, wavePeriod: 1f, indPeriod: 3f,
            minAltitude: null);
        SpawnTimes(cycle, seconds: 30f);
        cycle.GrantCapacity(5);
        return cycle;
    }

    // The same shape on a cycle whose whole period is under the door lead.
    private static GeneratorCycle LateCreditOnAShortPeriod()
    {
        var cycle = new GeneratorCycle(maxActive: 10, waveSize: 1, wavePeriod: 0.5f,
            indPeriod: 0.5f, minAltitude: null);
        SpawnTimes(cycle, seconds: 30f);
        cycle.GrantCapacity(5);
        return cycle;
    }

    // A zeppelin that spent the whole cycle under its launch gate and has just climbed back.
    private static GeneratorCycle ReleasedGate()
    {
        var cycle = Credited(maxActive: 99, waveSize: 1, wavePeriod: 10f, indPeriod: 10f,
            minAltitude: 150f);
        for (float t = 0f; t < 60f; t += Dt)
        {
            cycle.Step(Dt, hostAltitude: 100f);
        }
        return cycle;
    }

    // A bay held at max_active whose one live spawn has just been shot down.
    private static GeneratorCycle FreedSlot()
    {
        var cycle = Credited(maxActive: 1, waveSize: 1, wavePeriod: 1f, indPeriod: 1f);
        SpawnTimes(cycle, seconds: 30f);
        cycle.SpawnRemoved();
        return cycle;
    }

    private static int StepsUntilSpawn(GeneratorCycle cycle)
    {
        for (int i = 1; i <= 10_000; i++)
        {
            if (cycle.Step(Dt, HighAltitude))
            {
                return i;
            }
        }
        Assert.Fail("no spawn within the budget");
        return 0;
    }

    private static List<float> SpawnTimes(GeneratorCycle cycle, float seconds)
    {
        var spawns = new List<float>();
        int steps = (int)(seconds / Dt);
        for (int i = 1; i <= steps; i++)
        {
            if (cycle.Step(Dt, HighAltitude))
            {
                // Report in whole tenths so float accumulation noise does not move an assert.
                spawns.Add((float)System.Math.Round(i * Dt, 1));
            }
        }
        return spawns;
    }
}
