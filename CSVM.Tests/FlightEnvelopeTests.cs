using System;
using System.IO;
using System.Linq;
using CSVM.Flight.Airframe;
using CSVM.Tooling;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Bloodhawk's flown envelope against its DECODED targets (engine-free:
/// <c>Probes.FlightEnvelope</c> touches no live Node). Every asserted row is judged against the
/// executable's own arithmetic or against a named product exception; a disagreeing footage figure
/// is carried in the row's text as discarded and gates nothing, which is the parity ledger's rule
/// (docs/org/flightModel.md, "Parity ledger"). The probe's informational rows report the decoded
/// plant's number with nothing to compare it to.
/// ⚠ A demotion to informational is never the quiet way to make a run green.
/// </summary>
[Trait("Tier", "Quick")]
public class FlightEnvelopeTests
{
    // How many flight scenarios carry a decoded target to assert. Pinned so that silently demoting
    // one to informational cannot read as a green run.
    private const int FlightScenarios = 4;
    private const double Mph = 0.44704;

    // The dive the terminal-dive row is flown at, and the along-path share of gravity there.
    private const double DivePathDeg = 70.7;

    // The decoded thrust and drag constants, restated here rather than read off FlightModel so the
    // solves below are an independent statement of the decode. Same set (and same reason) as
    // PartThrottleEquilibriumTests, which owns the level curve's derivation.
    private const double SoundFps = 1109.5;
    private const double MetresPerFoot = 0.3048;
    private const double AirDensity = 2.2688e-3;
    private const double StandardG = 9.82;
    private const double MachFloor = 0.1;
    private const double PowBase = 1.33 * 0.98842078;
    private const double PowMach = 1.41;
    private const double VRefSlope = 0.84;
    private const double VRefMach = 0.112;
    private const double MachTrim = 1.0 / 60.0;
    private const double PolarScale = 0.73;
    private const double Parasite = 0.12;
    private const double DragLinear = 0.8;
    private const double DragQuad = 0.5;
    private const double AttitudeThrustBoth = 0.24;

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    [ExtractedDataFact]
    public void TheFlownEnvelopeStillMatchesItsDecodedTargets()
    {
        var r = Probes.FlightEnvelope(ZrdrPath, "player_bhawk");
        Assert.True(r.Error == null, $"plane stats load error={r.Error ?? "-"}");
        Assert.Equal(FlightScenarios, r.Asserted);
        foreach (var row in r.Rows)
        {
            if (row.Asserted)
            {
                string target = $"target={row.Target:0.00}{row.Unit} "
                                + $"tol=±{row.Tolerance:0.00} err={row.ErrorPct:+0.0;-0.0}%";
                Assert.True(row.Ok, $"{row.Name} model={row.Model:0.00}{row.Unit} {target}");
            }
        }
    }

    /// <summary>The two speed targets are the decode's own, solved here from the thrust curve, the
    /// Mach polar and gravity without touching the plant. A target that drifted toward the plant it
    /// judges, or back toward the footage figures it replaced, fails here rather than passing above
    /// as a row that agrees with itself.</summary>
    [ExtractedDataFact]
    public void TheAssertedSpeedTargetsAreSolvedFromTheDecodeNotFromThePlant()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        var rows = Probes.FlightEnvelope(ZrdrPath, "player_bhawk").Rows;

        double level = SolveMps(stats, alongPathG: 0.0, thrustScale: 1.0) / Mph;
        double dive = SolveMps(stats, alongPathG: stats.Gravity * Math.Sin(Rad(DivePathDeg)),
                               thrustScale: 1.0 + (AttitudeThrustBoth * Math.Sin(Rad(DivePathDeg))))
                      / Mph;

        foreach (string name in new[] { "level-top-speed", "level-speed-near-cap" })
        {
            double pinned = rows.Single(x => x.Name == name).Target!.Value;
            Assert.True(Math.Abs(pinned - level) <= 0.5,
                $"{name} is pinned at {pinned:0.00} mph against the decoded level solve "
                + $"{level:0.00} mph (the filmed 300.40 is discarded, docs/org/flightModel.md)");
        }

        double pinnedDive = rows.Single(x => x.Name == "terminal-dive").Target!.Value;
        Assert.True(Math.Abs(pinnedDive - dive) <= 1.0,
            $"terminal-dive is pinned at {pinnedDive:0.00} mph against the decoded balance "
            + $"{dive:0.00} mph (the filmed 355.20 is discarded, docs/org/flightModel.md)");
    }

    /// <summary>The able-to-fail control for the solves above: the same balance with the attitude
    /// thrust term dropped has to miss the pinned dive target. Without it the tolerance could admit
    /// any arithmetic of roughly the right size (<c>METHOD-9</c>).</summary>
    [ExtractedDataFact]
    public void DroppingTheAttitudeThrustTermMissesTheDiveTarget()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        double flat = SolveMps(stats, stats.Gravity * Math.Sin(Rad(DivePathDeg)), 1.0) / Mph;
        double pinned = Probes.FlightEnvelope(ZrdrPath, "player_bhawk")
            .Rows.Single(x => x.Name == "terminal-dive").Target!.Value;
        Assert.True(Math.Abs(pinned - flat) > 1.0,
            $"the dive solves to {flat:0.00} mph with no attitude thrust, inside the tolerance "
            + $"around the pinned {pinned:0.00} mph, the control measures nothing");
    }

    /// <summary>The ceiling row asserts no target, because the height an aircraft reaches above the
    /// band edge is the coast its climb rate buys and the plant's climb rate is a recorded residual.
    /// What it must do is cross the edge at all, which is what makes it a ceiling measurement.</summary>
    [ExtractedDataFact]
    public void TheAltitudeCeilingRowAssertsNoTarget()
    {
        var row = Probes.FlightEnvelope(ZrdrPath, "player_bhawk")
            .Rows.Single(x => x.Name == "altitude-ceiling");
        Assert.Null(row.Target);
        Assert.True(row.Model > 2000.0 / MetresPerFoot,
            $"the ceiling row apexed at {row.Model:0} ft, below the 2000 m band edge it must cross");
    }

    private static double Rad(double deg) => deg * Math.PI / 180.0;

    // The steady speed at which thrust plus the along-path share of gravity equals drag. Bisected
    // on Mach because both curves are monotone in it over the flyable band, so the bracket holds
    // one root; the airframe enters through engine power, drag_factor, reference area and weight.
    private static double SolveMps(PlaneStats stats, double alongPathG, double thrustScale)
    {
        double lo = 1e-4;
        double hi = 1.5;
        for (int i = 0; i < 200; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (Excess(stats, mid, alongPathG, thrustScale) > 0)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return 0.5 * (lo + hi) * SoundFps * MetresPerFoot;
    }

    // Along-path acceleration at a Mach: thrust available times the attitude scale, plus gravity's
    // share, minus the Mach polar. Every term is the executable's, in its own imperial units.
    private static double Excess(PlaneStats stats, double mach, double alongPathG, double thrustScale)
    {
        double floored = Math.Max(MachFloor, mach);
        double vRefFps = ((VRefSlope * floored) + VRefMach) * SoundFps;
        double qRef = 0.5 * AirDensity * vRefFps * vRefFps;
        double cRef = PolarScale * (Parasite - (floored * MachTrim));
        double avail = qRef * cRef / (floored * Math.Pow(PowBase, PowMach * floored));
        double thrust = stats.EnginePower * stats.RefArea * avail * StandardG / stats.VehWeight;

        double speedFps = mach * SoundFps;
        double q = 0.5 * AirDensity * speedFps * speedFps;
        double cd = PolarScale * (Parasite + (DragLinear * mach) + (DragQuad * mach * mach));
        double drag = q * stats.RefArea * stats.DragFactor * cd * StandardG / stats.VehWeight;

        return (thrust * thrustScale) + alongPathG - drag;
    }
}
