using System;
using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The original's two-band atmosphere step function and the band its threshold selects.
/// The function compares the aircraft's altitude in feet against a single threshold and takes the
/// dense band at or below it, the thin band above. The threshold is not a shipped initialiser: it
/// lives in the uninitialised tail of <c>.data</c> and a reset routine stores 6561.68 ft into it,
/// so the whole flyable envelope below 2000 m runs on the dense band and the thin band is the
/// regime above that ceiling. Reproduced here in isolation so the band choice, its boundary and
/// the stall arithmetic that discriminates the two are asserted apart from the live plant.
/// Decode and live evidence: docs/org/flightModel.md's Atmosphere section.
/// </summary>
public class AtmosphereBandTests
{
    // The threshold the process holds in flight, in feet: exactly 2000 m converted at 3.28084.
    private const float ThresholdFt = 6561.6796875f;

    private const float SeaLevelDensity = 0.002377f;
    private const float SoundBase = 558f;
    private const float DenseDensityFactor = 0.9544815f;
    private const float DenseSoundFactor = 0.98842078f;
    private const float ThinDensityFactor = 0.057048105f;
    private const float ThinSoundFactor = 0.7348f;

    // The parser's fallback airframe, the case that separates the two bands: no stock aircraft is
    // authored far from this wing loading, so a band that cannot fly it cannot fly any of them.
    private const float FallbackWeightLb = 3500f;
    private const float FallbackRefAreaFt2 = 335f;
    private const float ClMaxStatic = 0.75f;
    private const float ClMaxMach = 0.15f;
    private const float FpsPerMph = 1.4666667f;

    // The thrust curve's own constants, restated rather than read off the plant for the same reason
    // the band factors above are: the assertion has to be able to disagree with the plant.
    private const double FeetPerMetre = 3.28084;
    private const double MetresPerFoot = 0.3048;
    private const double StandardG = 9.82;
    private const double MachFloor = 0.1;
    private const double PowScale = 1.33;
    private const double PowMach = 1.41;
    private const double VRefSlope = 0.84;
    private const double VRefMach = 0.112;
    private const double MachTrim = 1.0 / 60.0;
    private const double PolarScale = 0.73;
    private const double Parasite = 0.12;

    [Fact]
    public void TheThresholdIsTwoThousandMetresExpressedInFeet()
    {
        Assert.Equal(2000f * 3.28084f, ThresholdFt, 3);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1000.0)]
    [InlineData(2952.756)]
    [InlineData(6561.6796875)]
    public void EveryAltitudeAtOrBelowTheThresholdTakesTheDenseBand(double altFt)
    {
        var atm = Atmosphere(altFt, ThresholdFt);
        Assert.Equal(2.2688e-3, atm.Rho, 7);
        Assert.Equal(1109.5, atm.SoundFps, 1);
    }

    [Theory]
    [InlineData(6561.68)]
    [InlineData(10000.0)]
    public void EveryAltitudeAboveTheThresholdTakesTheThinBand(double altFt)
    {
        var atm = Atmosphere(altFt, ThresholdFt);
        Assert.Equal(1.3560e-4, atm.Rho, 8);
        Assert.Equal(968.0, atm.SoundFps, 1);
    }

    [Fact]
    public void TheDenseBandIsTheOneTheFlightModelCarries()
    {
        var atm = Atmosphere(0.0, ThresholdFt);
        Assert.Equal(2.2688e-3, atm.Rho, 7);
        Assert.Equal(1109.5, atm.SoundFps, 1);
    }

    [Fact]
    public void OnlyTheDenseBandFliesTheFallbackAirframe()
    {
        var dense = Atmosphere(0.0, ThresholdFt);
        var thin = Atmosphere(ThresholdFt + 1.0, ThresholdFt);
        double denseMph = StallFps(dense.Rho, dense.SoundFps) / FpsPerMph;
        double thinMph = StallFps(thin.Rho, thin.SoundFps) / FpsPerMph;

        Assert.InRange(denseMph, 72.0, 80.0);
        Assert.True(thinMph > 4.0 * denseMph, $"thin={thinMph:0.0} mph dense={denseMph:0.0} mph");
    }

    // A zero threshold is the reading a byte-level look at the uninitialised slot produces. It has
    // to change the answer at every altitude a stock aircraft reaches, or the live read of the
    // threshold would not be the thing that settles the band.
    [Fact]
    public void AZeroThresholdWouldPutEveryAirborneAircraftOnTheThinBand()
    {
        Assert.Equal(2.2688e-3, Atmosphere(0.0, 0.0).Rho, 7);
        Assert.Equal(1.3560e-4, Atmosphere(1.0, 0.0).Rho, 8);
        Assert.Equal(1.3560e-4, Atmosphere(3000.0, 0.0).Rho, 8);
    }

    /// <summary>The band's factor has a third consumer beside density and the speed of sound: it is
    /// the base of the thrust curve's Mach divisor, 1.33 times the same number. The live plant takes
    /// the band's own factor at every altitude, so a thin-band aircraft gets the base below 1 that
    /// makes the divisor shrink with Mach instead of the dense band's 1.31 that makes it grow.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(6000.0)]
    [InlineData(6500.0)]
    [InlineData(6600.0)]
    [InlineData(12000.0)]
    public void TheThrustDivisorTakesTheBandsOwnFactor(double altFt)
    {
        var stats = new PlaneStats { VehWeight = FallbackWeightLb, RefArea = FallbackRefAreaFt2, EnginePower = 1f };
        var m = new FlightModel(stats) { Position = new Vector3(0f, (float)(altFt / FeetPerMetre), 0f) };
        bool dense = altFt <= ThresholdFt;
        var atm = Atmosphere(altFt, ThresholdFt);
        double factor = dense ? DenseSoundFactor : ThinSoundFactor;

        foreach (double speedMs in new[] { 40.0, 90.0, 160.0 })
        {
            double want = ThrustAccel(speedMs, atm.Rho, atm.SoundFps, factor);
            double got = m.ThrustAccelAt((float)speedMs, 1f);
            Assert.True(Math.Abs(got - want) <= want * 1e-4,
                $"{altFt:0} ft at {speedMs:0} m/s: thrust accel {got:0.0000} against the "
                + $"{(dense ? "dense" : "thin")} band's own {want:0.0000} m/s²");

            // The other band's base is the mistake this pins: one factor for every altitude.
            double other = ThrustAccel(speedMs, atm.Rho, atm.SoundFps,
                dense ? ThinSoundFactor : DenseSoundFactor);
            Assert.True(Math.Abs(other - want) > want * 1e-3,
                $"{altFt:0} ft at {speedMs:0} m/s: the two bases give the same thrust, so this "
                + "asserts nothing");
        }
    }

    /// <summary>Thrust acceleration along the nose, m/s², restated from the decoded curve so the
    /// assertion above is an independent statement of the arithmetic.</summary>
    private static double ThrustAccel(double speedMs, double rho, double soundFps, double factor)
    {
        double mach = Math.Max(MachFloor, speedMs / (soundFps * MetresPerFoot));
        double vRefFps = ((VRefSlope * mach) + VRefMach) * soundFps;
        double qRef = 0.5 * rho * vRefFps * vRefFps;
        double cRef = PolarScale * (Parasite - (mach * MachTrim));
        double avail = qRef * cRef / (mach * Math.Pow(PowScale * factor, PowMach * mach));
        return avail * FallbackRefAreaFt2 * StandardG / FallbackWeightLb;
    }

    /// <summary>Density in slug/ft³ and speed of sound in ft/s at an altitude in feet.</summary>
    private static (double Rho, double SoundFps) Atmosphere(double altFt, double thresholdFt)
    {
        bool dense = altFt <= thresholdFt;
        double densityFactor = dense ? DenseDensityFactor : ThinDensityFactor;
        double soundFactor = dense ? DenseSoundFactor : ThinSoundFactor;
        return (densityFactor * SeaLevelDensity, (soundFactor + 1.0) * SoundBase);
    }

    /// <summary>The speed in ft/s at which the aerodynamic ceiling stops delivering 1 G.</summary>
    private static double StallFps(double rho, double soundFps)
    {
        double lo = 1.0;
        double hi = 4000.0;
        for (int i = 0; i < 200; i++)
        {
            double v = 0.5 * (lo + hi);
            double clMax = Math.Max(0.0, ClMaxStatic - (ClMaxMach * (v / soundFps)));
            double lift = clMax * 0.5 * rho * v * v * FallbackRefAreaFt2;
            if (lift < FallbackWeightLb)
            {
                lo = v;
            }
            else
            {
                hi = v;
            }
        }

        return 0.5 * (lo + hi);
    }
}
