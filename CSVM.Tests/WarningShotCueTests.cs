using CSVM.Flight.Hud;
using Xunit;

namespace CSVM.Tests;

/// <summary>The incoming-fire shield's decoded accumulator, the half that runs without an engine.
/// The shipped values are player.json's: max 2.0 s, interval 1.0 s, and a dissipation of 2.0 that
/// the loader stores as its reciprocal, so 0.5 of charge goes per second of quiet.</summary>
public class WarningShotCueTests
{
    /// <summary>A fresh airframe starts armed: the vehicle constructor writes the flag set, so the
    /// very first round of a sortie is absorbed rather than felt.</summary>
    [Fact]
    public void AFreshAirframeAbsorbs()
    {
        var cue = Shipped();
        Assert.True(cue.Absorbs);
        Assert.Equal(0f, cue.Intensity, 4);
    }

    /// <summary>An interval charges by its own elapsed length, not by the number of rounds: thirty
    /// rounds in one second and one round in one second charge the same.</summary>
    [Fact]
    public void AnIntervalChargesByItsLengthNotByItsRounds()
    {
        var burst = Shipped();
        for (int i = 0; i < 30; i++)
            burst.RegisterHit();
        Assert.Equal(30, burst.Tick(1f));
        Assert.Equal(1f, burst.Intensity, 4);

        var single = Shipped();
        single.RegisterHit();
        Assert.Equal(1, single.Tick(1f));
        Assert.Equal(1f, single.Intensity, 4);
    }

    /// <summary>Two intervals of fire fill the shipped max and the shield drops, which is the whole
    /// rule: until then the rounds do nothing but sound.</summary>
    [Fact]
    public void SustainedFireSaturatesAndDisarms()
    {
        var cue = Shipped();
        cue.RegisterHit();
        cue.Tick(1f);
        Assert.True(cue.Absorbs);
        cue.RegisterHit();
        cue.Tick(1f);
        Assert.Equal(2f, cue.Intensity, 4);
        Assert.False(cue.Absorbs);
    }

    /// <summary>A quiet interval drains the accumulator and re-arms the shield the moment it sits
    /// below max, however much charge is left, so a gunner who lets up gives the whole thing
    /// back.</summary>
    [Fact]
    public void AQuietIntervalDrainsAndReArms()
    {
        var cue = Shipped();
        cue.RegisterHit();
        cue.Tick(1f);
        cue.RegisterHit();
        cue.Tick(1f);
        Assert.False(cue.Absorbs);
        cue.Tick(1f);
        Assert.Equal(1.5f, cue.Intensity, 4);
        Assert.True(cue.Absorbs);
    }

    /// <summary>The drain floors at zero rather than running negative, and four quiet intervals are
    /// what it takes to give a saturated accumulator all of its charge back.</summary>
    [Fact]
    public void TheDrainFloorsAtZero()
    {
        var cue = Shipped();
        cue.RegisterHit();
        cue.Tick(1f);
        cue.RegisterHit();
        cue.Tick(1f);
        for (int i = 0; i < 4; i++)
            cue.Tick(1f);
        Assert.Equal(0f, cue.Intensity, 4);
        cue.Tick(1f);
        Assert.Equal(0f, cue.Intensity, 4);
    }

    /// <summary>The interval has to close before anything happens: hits inside it are held, and the
    /// tick answers 0 so the canopy cadence does not run either.</summary>
    [Fact]
    public void NothingHappensBeforeTheIntervalCloses()
    {
        var cue = Shipped();
        cue.RegisterHit();
        Assert.Equal(0, cue.Tick(0.5f));
        Assert.Equal(0f, cue.Intensity, 4);
        Assert.Equal(1, cue.Tick(0.6f));
        Assert.Equal(1.1f, cue.Intensity, 4);
    }

    /// <summary>The hit count belongs to the interval that closed: the next one starts empty.
    /// </summary>
    [Fact]
    public void TheHitCountResetsWithTheInterval()
    {
        var cue = Shipped();
        cue.RegisterHit();
        cue.RegisterHit();
        Assert.Equal(2, cue.Tick(1f));
        Assert.Equal(0, cue.Tick(1f));
    }

    [Fact]
    public void ResetGivesBackAFreshAirframe()
    {
        var cue = Shipped();
        cue.RegisterHit();
        cue.Tick(1f);
        cue.RegisterHit();
        cue.Tick(1f);
        Assert.False(cue.Absorbs);
        cue.Reset();
        Assert.True(cue.Absorbs);
        Assert.Equal(0f, cue.Intensity, 4);
        Assert.Equal(0, cue.Tick(0.5f));
    }

    private static WarningShotCue Shipped() => new(2f, 0.5f, 1f);
}
