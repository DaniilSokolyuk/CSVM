using System;
using CSVM.Flight.Hud;
using Xunit;

namespace CSVM.Tests;

/// <summary>The canopy-glass cue's decoded cadence, the half that runs without an engine. The
/// interval that gets it here belongs to <see cref="WarningShotCue"/>, so every call below stands
/// for one interval that closed with at least one gun hit.</summary>
public class CanopyHoleCueTests
{
    /// <summary>The decoded gate: the health fraction must be BELOW the closed share. A pristine
    /// airframe is at 1.0 against a closed share of 1.0, so it never opens a hole.</summary>
    [Fact]
    public void APristineAirframeKeepsItsGlass()
    {
        var cue = new CanopyHoleCue();
        Assert.Null(cue.TryOpenHole(1f, new ScriptedRandom(0.99, 0.0)));
        Assert.Equal(CanopyHoleCue.HoleCount, cue.ClosedCount);
    }

    /// <summary>Each opened hole raises the bar: with four closed of five the gate is 0.8, so an
    /// airframe sitting at 0.9 health opens the first hole and then stops.</summary>
    [Fact]
    public void EachHoleNeedsMoreDamageThanTheLast()
    {
        var cue = new CanopyHoleCue();
        Assert.Equal(1, cue.TryOpenHole(0.9f, new ScriptedRandom(0.99, 0.0)));
        Assert.Null(cue.TryOpenHole(0.9f, new ScriptedRandom(0.99, 0.0)));
        Assert.NotNull(cue.TryOpenHole(0.7f, new ScriptedRandom(0.99, 0.0)));
        Assert.Equal(3, cue.ClosedCount);
    }

    [Fact]
    public void TheDrawBelowTheShippedChanceOpensNothing()
    {
        var cue = new CanopyHoleCue();
        // 0.7 is the threshold itself, and the original goes on only ABOVE it.
        Assert.Null(cue.TryOpenHole(0.1f, new ScriptedRandom(1.0 - CanopyHoleCue.HoleChance, 0.0)));
        Assert.NotNull(cue.TryOpenHole(0.1f, new ScriptedRandom(0.71, 0.0)));
    }

    /// <summary>A hole is opened once. Five eligible intervals with the airframe on its last legs
    /// open all five and the sixth finds no glass left.</summary>
    [Fact]
    public void EveryHoleOpensAtMostOnce()
    {
        var cue = new CanopyHoleCue();
        var opened = new bool[CanopyHoleCue.HoleCount];
        for (int i = 0; i < CanopyHoleCue.HoleCount; i++)
        {
            int? hole = cue.TryOpenHole(0.01f, new ScriptedRandom(0.99, 0.999));
            Assert.NotNull(hole);
            Assert.InRange(hole!.Value, 1, CanopyHoleCue.HoleCount);
            Assert.False(opened[hole.Value - 1]);
            opened[hole.Value - 1] = true;
        }

        Assert.Equal(0, cue.ClosedCount);
        Assert.Null(cue.TryOpenHole(0.01f, new ScriptedRandom(0.99, 0.999)));
    }

    [Fact]
    public void ResetGivesBackAPristineCanopy()
    {
        var cue = new CanopyHoleCue();
        Assert.NotNull(cue.TryOpenHole(0.1f, new ScriptedRandom(0.99, 0.0)));
        cue.Reset();
        Assert.Equal(CanopyHoleCue.HoleCount, cue.ClosedCount);
    }

    // A draw stream that hands out the values a test asks for, so the 0.3 chance and the index pick
    // are exercised as decisions rather than as luck.
    private sealed class ScriptedRandom : Random
    {
        private readonly double[] _values;
        private int _next;

        internal ScriptedRandom(params double[] values) => _values = values;

        public override double NextDouble() => _values[_next++ % _values.Length];
    }
}
