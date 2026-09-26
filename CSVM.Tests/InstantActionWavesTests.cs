using System.Collections.Generic;
using CSVM.Flight.Modes;
using CSVM.Session.InstantAction;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The E11 wave sequencer (docs/formats/instant-action.md "The wave sequencer and the mission
/// end"): <see cref="InstantActionWaves"/>'s own trigger (advance on
/// last kill, a 0-enemy wave falling through, no advance past wave 4) and its two static geometry
/// helpers (the 500 m-from-nearest-human spawn draw with its literal-index-0 fallback, and the
/// 100 m / 45° fan), all pure over their inputs, so these fixtures need no engine.
/// </summary>
public class InstantActionWavesTests
{
    [Fact]
    public void StartActivatesWaveOneWhenItHasEnemies()
    {
        var waves = new InstantActionWaves(new[] { 6, 5, 4, 3 });
        int first = waves.Start();
        Assert.Equal(1, first);
        Assert.Equal(1, waves.CurrentWave);
        Assert.Equal(6, waves.CurrentWaveSize);
        Assert.False(waves.Finished);
    }

    [Fact]
    public void StartSkipsLeadingZeroEnemyWaves()
    {
        var waves = new InstantActionWaves(new[] { 0, 0, 4, 3 });
        int first = waves.Start();
        Assert.Equal(3, first);
        Assert.Equal(3, waves.CurrentWave);
        Assert.Equal(4, waves.CurrentWaveSize);
    }

    [Fact]
    public void EveryWaveEmptyFinishesImmediately()
    {
        var waves = new InstantActionWaves(new[] { 0, 0, 0, 0 });
        int first = waves.Start();
        Assert.Equal(0, first);
        Assert.True(waves.Finished);
        Assert.Equal(0, waves.CurrentWaveSize);
    }

    [Fact]
    public void StepDoesNothingWhileTheCurrentWaveStillHasMembersAlive()
    {
        var waves = new InstantActionWaves(new[] { 6, 5, 4, 3 });
        waves.Start();
        int next = waves.Step(aliveInCurrentWave: 1);
        Assert.Equal(0, next);
        Assert.Equal(1, waves.CurrentWave);
    }

    [Fact]
    public void StepAdvancesOnTheLastKillOfTheCurrentWave()
    {
        var waves = new InstantActionWaves(new[] { 6, 5, 4, 3 });
        waves.Start();
        int next = waves.Step(aliveInCurrentWave: 0);
        Assert.Equal(2, next);
        Assert.Equal(2, waves.CurrentWave);
        Assert.Equal(5, waves.CurrentWaveSize);
    }

    [Fact]
    public void StepCascadesPastAZeroEnemyWaveMidSequence()
    {
        var waves = new InstantActionWaves(new[] { 6, 0, 4, 3 });
        waves.Start(); // wave 1
        int next = waves.Step(aliveInCurrentWave: 0); // wave 2 is empty, falls through to 3
        Assert.Equal(3, next);
        Assert.Equal(3, waves.CurrentWave);
    }

    [Fact]
    public void SequencerNeverAdvancesPastWaveFour()
    {
        var waves = new InstantActionWaves(new[] { 1, 1, 1, 1 });
        waves.Start();
        Assert.Equal(2, waves.Step(0));
        Assert.Equal(3, waves.Step(0));
        Assert.Equal(4, waves.Step(0));
        int next = waves.Step(0); // wave 4's last kill, nothing left to activate
        Assert.Equal(0, next);
        Assert.True(waves.Finished);
        // A further tick, even with a bogus "still alive" reading, changes nothing.
        Assert.Equal(0, waves.Step(0));
        Assert.Equal(0, waves.Step(5));
    }

    [Fact]
    public void ChooseWaveSpawnPicksAmongPointsFarFromEveryHuman()
    {
        var spawns = new List<SpawnPoint>
        {
            new(new Vector3(0f, 0f, 0f), 0f),      // near P1 below, excluded
            new(new Vector3(1000f, 0f, 0f), 0f),   // far from both humans, eligible
            new(new Vector3(0f, 0f, 1000f), 90f),  // far from both humans, eligible
        };
        var humans = new List<Vector3> { new(0f, 0f, 0f), new(50f, 0f, 0f) };

        var (idx0, _) = InstantActionWaves.ChooseWaveSpawn(spawns, humans, draw: 0);
        Assert.Equal(1, idx0); // draw 0 % 2 eligible -> the first eligible index (1)
        var (idx1, _) = InstantActionWaves.ChooseWaveSpawn(spawns, humans, draw: 1);
        Assert.Equal(2, idx1); // draw 1 % 2 eligible -> the second eligible index (2)
    }

    [Fact]
    public void ChooseWaveSpawnFallsBackToTheLiteralFirstEntryWhenNoneIsFarEnough()
    {
        var spawns = new List<SpawnPoint>
        {
            new(new Vector3(10f, 0f, 0f), 0f),
            new(new Vector3(20f, 0f, 0f), 0f),
        };
        var humans = new List<Vector3> { new(0f, 0f, 0f) }; // both points well under 500 m

        var (idx, point) = InstantActionWaves.ChooseWaveSpawn(spawns, humans, draw: 12345);
        Assert.Equal(0, idx); // the literal first entry, never a random one
        Assert.Equal(spawns[0].Position, point.Position);
    }

    [Fact]
    public void FanOffsetPutsTheLeaderExactlyOnThePointAndFansTheRest()
    {
        // Six aircraft: 0, 100, 100, 200, 200, 300 m on sides (none), -, +, +, -, -.
        Assert.Equal((0f, 0f), InstantActionWaves.FanOffset(0));
        Assert.Equal((100f, -45f), InstantActionWaves.FanOffset(1));
        Assert.Equal((100f, 45f), InstantActionWaves.FanOffset(2));
        Assert.Equal((200f, 45f), InstantActionWaves.FanOffset(3));
        Assert.Equal((200f, -45f), InstantActionWaves.FanOffset(4));
        Assert.Equal((300f, -45f), InstantActionWaves.FanOffset(5));
    }
}
