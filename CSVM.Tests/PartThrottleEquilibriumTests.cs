using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Flight.Airframe;
using CSVM.Testing;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The level-flight equilibrium at every lever position, on all eleven stock airframes. The lever
/// enters the original's force path once, as a plain multiply on available thrust, so setting
/// thrust equal to drag gives a closed form in Mach whose only airframe inputs are engine power
/// and drag_factor. This suite solves that form here, independently of the plant, and flies the
/// plant to its own equilibrium against it.
/// Decode, the derivation and the eleven-airframe table: docs/org/flightModel.md, "Part-throttle
/// equilibrium".
/// ⚠ The targets are decoded, not measured. Do not replace one with a speed read off footage, and
/// do not widen the tolerance to admit one.
/// </summary>
public class PartThrottleEquilibriumTests
{
    private const float Dt = 1f / 60f;
    private const float Mph = 0.44704f;

    // The closed form's atmosphere: the dense band, which covers the whole flyable envelope.
    private const double SoundFps = 1109.5;
    private const double MetresPerFoot = 0.3048;
    private const double AirDensity = 2.2688e-3;

    // The thrust curve's own constants, kept here rather than read off FlightModel so that the
    // solve is an independent statement of the decode and not a restatement of the plant.
    private const double MachFloor = 0.1;
    private const double PowBase = 1.33 * 0.98842078;
    private const double PowMach = 1.41;
    private const double VRefSlope = 0.84;
    private const double VRefMach = 0.112;
    private const double MachTrim = 1.0 / 60.0;
    private const double Parasite = 0.12;
    private const double DragLinear = 0.8;
    private const double DragQuad = 0.5;

    // How close the flown equilibrium has to sit to the solved one. The fixed point of the
    // integrator is the fixed point of the force sum, so the step size does not enter; the margin
    // covers float arithmetic and the settling window only.
    private const double Tolerance = 0.005;

    private static readonly string[] Planes =
    {
        "player_bhawk", "player_pfighter", "player_fury", "player_warhawk", "player_autogyro",
        "player_avenger", "player_balmoral", "player_brigand", "player_fbrand", "player_kestrel",
        "player_peacemaker",
    };

    private static readonly float[] Levers =
    {
        0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f, 0.875f, 1f,
    };

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>Every airframe, every lever: the plant flies to the solved equilibrium from both
    /// sides. Approaching from above and below matters because the solve only says where thrust
    /// equals drag, and a stable equilibrium has to attract from either direction.</summary>
    [ExtractedDataFact]
    public void EveryAirframeFliesToTheDecodedEquilibriumAtEveryLever()
    {
        int checkedPoints = 0;
        foreach (string plane in Planes)
        {
            var stats = PlaneStats.Load(ZrdrPath, plane);
            double floor = LevelFlightFloorMps(stats);
            foreach (float lever in Levers)
            {
                double target = EquilibriumMps(lever, stats);
                if (target < floor)
                {
                    continue;
                }

                foreach (float from in new[] { 0.75f, 1.25f })
                {
                    float entry = Math.Max((float)(floor * 1.02), (float)target * from);
                    double flown = FlyToEquilibrium(stats, lever, entry);
                    Assert.True(Math.Abs(flown - target) <= Tolerance * target,
                        $"{plane} at lever {lever:0.000} entered {entry / Mph:0.0} mph: flew to "
                        + $"{flown / Mph:0.00} mph against the decoded {target / Mph:0.00} mph");
                    checkedPoints++;
                }
            }
        }

        // Only the Balmoral's 1/8 lever solves below its own level-flight floor, so every other
        // airframe contributes all eight levers from both sides.
        Assert.Equal(((Planes.Length * Levers.Length) - 1) * 2, checkedPoints);
    }

    /// <summary>The curve rises with the lever on every airframe, in the solve and in the plant
    /// alike. Monotonicity is what makes the lever usable as a speed control, and it is not free:
    /// the thrust curve itself rises with speed, so a steeper one would fold the curve over.</summary>
    [ExtractedDataFact]
    public void TheEquilibriumRisesWithEveryLeverStep()
    {
        foreach (string plane in Planes)
        {
            var stats = PlaneStats.Load(ZrdrPath, plane);
            double floor = LevelFlightFloorMps(stats);
            double previousSolved = 0.0;
            double previousFlown = 0.0;
            foreach (float lever in Levers)
            {
                double solved = EquilibriumMps(lever, stats);
                Assert.True(solved > previousSolved,
                    $"{plane}: the solved equilibrium fell at lever {lever:0.000}");
                previousSolved = solved;
                if (solved < floor)
                {
                    continue;
                }

                double flown = FlyToEquilibrium(stats, lever, (float)solved * 0.75f);
                Assert.True(flown > previousFlown,
                    $"{plane}: the flown equilibrium fell at lever {lever:0.000}");
                previousFlown = flown;
            }
        }
    }

    /// <summary>The Bloodhawk's decoded points, written out. These are the numbers the eighth-throttle
    /// probe row reports, and pinning them here is what stops the row's shape drifting unnoticed
    /// while every ratio test still passes.</summary>
    [ExtractedDataTheory]
    [InlineData(0.125f, 134.5)]
    [InlineData(0.25f, 176.1)]
    [InlineData(0.5f, 230.4)]
    [InlineData(0.75f, 269.3)]
    [InlineData(1f, 300.5)]
    public void TheBloodhawksCurvePassesThroughItsDecodedPoints(float lever, double mph)
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        Assert.Equal(mph, EquilibriumMps(lever, stats) / Mph, 1);
        Assert.Equal(mph, FlyToEquilibrium(stats, lever, (float)(mph * Mph * 0.8)) / Mph, 1);
    }

    /// <summary>The able-to-fail control: the same assertion under a lever off by 5 % has to miss.
    /// Without it the tolerance could be loose enough to pass any curve of roughly the right
    /// shape.</summary>
    [ExtractedDataFact]
    public void AFivePercentLeverErrorMissesTheDecodedEquilibrium()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        foreach (float lever in Levers)
        {
            double target = EquilibriumMps(lever, stats);
            double flown = FlyToEquilibrium(stats, lever * 0.95f, (float)target);
            Assert.True(Math.Abs(flown - target) > Tolerance * target,
                $"a 5 % lever error still landed inside the tolerance at lever {lever:0.000}: "
                + $"{flown / Mph:0.00} vs {target / Mph:0.00} mph");
        }
    }

    /// <summary>The Mach floor binds below 75.6 mph, where the thrust curve stops falling with
    /// speed and goes flat. The autogyro is the airframe light enough to hold level flight there,
    /// and the floored and unfloored curves separate by far more than the tolerance.</summary>
    [ExtractedDataFact]
    public void TheMachFloorSetsTheShapeOfTheLowestLevers()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_autogyro");
        double floored = EquilibriumMps(0.02f, stats);
        double unfloored = SolveLever(0.02f, stats, floorMach: false);

        Assert.True(floored > LevelFlightFloorMps(stats),
            "the autogyro must be able to hold level flight below the Mach floor for this case");
        Assert.Equal(floored / Mph, FlyToEquilibrium(stats, 0.02f, (float)floored * 1.3f) / Mph, 1);
        Assert.True(Math.Abs(unfloored - floored) > 10.0 * Tolerance * floored,
            $"the floor has to move this point: floored {floored / Mph:0.0} mph, "
            + $"unfloored {unfloored / Mph:0.0} mph");
    }

    /// <summary>The one stock lever with no level solution: the Balmoral at 1/8 solves below the
    /// speed at which its wings can still carry nom_gravity, so the plant trades height for the
    /// difference and settles faster than the solve, descending.</summary>
    [ExtractedDataFact]
    public void TheBalmoralsEighthLeverSolvesBelowItsOwnLevelFlightFloor()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_balmoral");
        double target = EquilibriumMps(0.125f, stats);
        double floor = LevelFlightFloorMps(stats);
        Assert.True(target < floor,
            $"the Balmoral's 1/8 solve {target / Mph:0.0} mph must sit below its floor "
            + $"{floor / Mph:0.0} mph, or this case is not the one being described");

        var m = new FlightModel(stats);
        m.Reset(Vector3.Zero, Basis.Identity, (float)target, 0.125f);
        for (float t = 0f; t < 300f; t += Dt)
        {
            m.Step(new FlightInput { Throttle = 0.125f }, Dt);
        }

        Assert.True(m.Speed > target,
            $"settled at {m.Speed / Mph:0.0} mph, not above the level solve {target / Mph:0.0}");
        Assert.True(m.VelocityDir.Y < -0.05f,
            $"the settled path is {Mathf.RadToDeg(Mathf.Asin(m.VelocityDir.Y)):0.0}°, not a descent");
    }

    /// <summary>Every airframe's curve is one curve in engine power over drag_factor: two airframes
    /// with the same ratio hold the same Mach at the same lever whatever their weight or wing area,
    /// because reference area and air density cancel out of the balance.</summary>
    [ExtractedDataFact]
    public void TheCurveDependsOnNothingButThePowerToDragRatio()
    {
        var kestrel = PlaneStats.Load(ZrdrPath, "player_kestrel");
        var scaled = PlaneStats.Load(ZrdrPath, "player_kestrel");
        scaled.EnginePower *= 2f;
        scaled.DragFactor *= 2f;
        scaled.RefArea *= 3f;

        foreach (float lever in Levers)
        {
            Assert.Equal(EquilibriumMps(lever, kestrel), EquilibriumMps(lever, scaled), 3);
        }
    }

    // Flies level from `entry` until the speed stops moving, and returns the speed it holds.
    private static double FlyToEquilibrium(PlaneStats stats, float lever, float entry)
    {
        var m = new FlightModel(stats);
        m.Reset(Vector3.Zero, Basis.Identity, entry, lever);
        for (float t = 0f; t < 300f; t += Dt)
        {
            m.Step(new FlightInput { Throttle = lever }, Dt);
        }

        return m.Speed;
    }

    // The lever that holds a given Mach in level flight: thrust available times the lever against
    // the Mach polar, with reference area, air density and the shared 0.73 cancelled out.
    private static double LeverAt(double mach, PlaneStats stats)
    {
        double floored = Math.Max(MachFloor, mach);
        double thrust = stats.EnginePower
                        * Sq((VRefSlope * floored) + VRefMach)
                        * (Parasite - (floored * MachTrim))
                        / (floored * Math.Pow(PowBase, PowMach * floored));
        double drag = stats.DragFactor * mach * mach
                      * (Parasite + (DragLinear * mach) + (DragQuad * mach * mach));
        return drag / thrust;
    }

    private static double EquilibriumMps(double lever, PlaneStats stats) =>
        SolveLever(lever, stats, floorMach: true);

    // Inverts the lever curve by bisection. It is strictly increasing in Mach, which is what
    // TheEquilibriumRisesWithEveryLeverStep asserts, so the bracket cannot hold two roots.
    private static double SolveLever(double lever, PlaneStats stats, bool floorMach)
    {
        double lo = 1e-6;
        double hi = 1.5;
        for (int i = 0; i < 200; i++)
        {
            double mid = 0.5 * (lo + hi);
            double at = floorMach ? LeverAt(mid, stats) : UnflooredLeverAt(mid, stats);
            if (at < lever)
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

    // The same balance with the thrust curve's Mach floor removed, for the control that shows the
    // floor is what shapes the lowest levers.
    private static double UnflooredLeverAt(double mach, PlaneStats stats)
    {
        double thrust = stats.EnginePower
                        * Sq((VRefSlope * mach) + VRefMach)
                        * (Parasite - (mach * MachTrim))
                        / (mach * Math.Pow(PowBase, PowMach * mach));
        double drag = stats.DragFactor * mach * mach
                      * (Parasite + (DragLinear * mach) + (DragQuad * mach * mach));
        return drag / thrust;
    }

    // The slowest level flight an airframe has: the speed at which the aerodynamic ceiling still
    // delivers nom_gravity. Below it the wings cannot hold the aircraft up at any lever, so the
    // level balance has no solution there however the thrust and drag terms sit.
    private static double LevelFlightFloorMps(PlaneStats stats)
    {
        double load = stats.Gravity / 9.82;
        double fps = Math.Sqrt(2.0 * stats.VehWeight * load / (0.75 * AirDensity * stats.RefArea));
        for (int i = 0; i < 8; i++)
        {
            double clMax = 0.75 - (0.15 * (fps / SoundFps));
            fps = Math.Sqrt(2.0 * stats.VehWeight * load / (clMax * AirDensity * stats.RefArea));
        }

        return fps * MetresPerFoot;
    }

    private static double Sq(double x) => x * x;
}
