using System.IO;
using CSVM.Flight.Camera;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The typed <c>shakes.json</c> oscillator-source reader (<c>docs/formats/shakes.md</c>): scalar
/// typing, the per-source magnitude-term variants, and the unhandled-key tripwire. Input is
/// <c>fixtures/zrdr/shakes.json</c>; the golden asserts the six real sources on the install.
/// </summary>
public class ShakeDefsTests
{
    [Fact]
    public void EverySourceIsIndexedByIdAndKeptInFileOrder()
    {
        var defs = Load();
        Assert.Equal(4, defs.All.Count);
        Assert.Equal("fire_bullet", defs.All[0].Id);
        Assert.Equal("nitro", defs.All[3].Id);
        Assert.NotNull(defs.Get("FIRE_BULLET")); // id lookup is case-insensitive
        Assert.Null(defs.Get("probe_absent"));
    }

    [Fact]
    public void TheLawAndTheMagnitudeTermAreTyped()
    {
        var fire = Load().FireBullet!;
        Assert.Equal(15.0f, fire.Frequency);
        Assert.Equal(12.5f, fire.Damp);
        Assert.True(fire.Sawtooth);
        Assert.Equal(0.0002f, fire.MagnitudeFactor);
        Assert.Null(fire.HeFactor);
        Assert.Null(fire.Magnitude);
    }

    [Fact]
    public void EachMagnitudeVariantKeepsItsOwnFieldsAndTheOthersStayNull()
    {
        var defs = Load();
        var missile = defs.MissileImpact!;
        Assert.False(missile.Sawtooth);
        Assert.Equal(2.0f, missile.HeFactor);

        var speed = defs.HighSpeed!;
        Assert.Equal(1.0f, speed.MinSpeed);
        Assert.Equal(70.0f, speed.MagnitudeQuotient);
        Assert.Null(speed.MagnitudeFactor);

        var nitro = defs.Nitro!;
        Assert.Equal(0.05f, nitro.Magnitude);
        Assert.Null(nitro.MagnitudeFactor);

        // fixture carries no bullet_impact/explosion: absent stays null, not empty
        Assert.Null(defs.BulletImpact);
        Assert.Null(defs.Explosion);
    }

    [Fact]
    public void AnUnknownKeyIsReportedRatherThanSilentlyDropped()
    {
        var defs = Load();
        Assert.Equal(new[] { "PROBE_FUTURE_KEY" }, defs.MissileImpact!.UnhandledKeys);
        Assert.Empty(defs.FireBullet!.UnhandledKeys);
    }

    [ExtractedDataFact]
    public void TheInstallCarriesTheSixSourcesTheRuntimeWires()
    {
        var defs = ShakeDefs.Load(
            SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip")));
        Assert.Equal(6, defs.All.Count);
        Assert.NotNull(defs.FireBullet!.MagnitudeFactor);
        Assert.True(defs.FireBullet.Sawtooth);
        Assert.NotNull(defs.MissileImpact!.HeFactor);
        Assert.NotNull(defs.HighSpeed!.MagnitudeQuotient);
        Assert.NotNull(defs.HighSpeed.MinSpeed);
        Assert.NotNull(defs.Nitro!.Magnitude);
        foreach (var src in defs.All)
        {
            Assert.Empty(src.UnhandledKeys);
        }
    }

    private static ShakeDefs Load() => ShakeDefs.Load(TestData.Fixture("zrdr"));
}
