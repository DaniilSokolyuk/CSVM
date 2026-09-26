using System.IO;
using CSVM.Flight.Airframe;
using CSVM.Tooling;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The attitude-dependent thrust scale, and the sustained climb that settled it. Decode, the
/// four-arrangement climb comparison and why there is no climb-gravity constant:
/// docs/org/flightModel.md, "The sustained climb".
/// ⚠ The sign is the whole risk: the quantity is negative in a climb, and a flipped sign still
/// flies, merely swapping climb for dive. <see cref="TheScaleIsReadOffTheNoseAndNotItsMirror"/> is
/// the able-to-fail form.
/// </summary>
public class AttitudeThrustTests
{
    private const float Dt = 1f / 60f;

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>The decoded numbers themselves: two coefficients, the second one-sided, so a
    /// vertical climb keeps (1 − 0.24)(1 − 0.13) = 0.6612 of available thrust and a vertical dive
    /// gets 1.24, not 1.24 and 1/1.24, and not symmetric.</summary>
    [Fact]
    public void TheScaleIsTwoCoefficientsAndOnlyOneOfThemIsTwoSided()
    {
        Assert.Equal(1.0f, FlightModel.AttitudeThrustScale(0f), 5);
        Assert.Equal(0.6612f, FlightModel.AttitudeThrustScale(-1f), 4);
        Assert.Equal(1.24f, FlightModel.AttitudeThrustScale(1f), 4);

        // One-sided, so the dive side is the bare linear term while the climb side carries both.
        // A symmetric reading would put the dive at 1/0.6612 = 1.51.
        Assert.True(FlightModel.AttitudeThrustScale(0.5f) > 1f);
        Assert.True(FlightModel.AttitudeThrustScale(-0.5f) < 1f);
        Assert.Equal(1.12f, FlightModel.AttitudeThrustScale(0.5f), 4);
    }

    /// <summary>The sign, read out of the integrator rather than off the formula. Thrust is the
    /// only force term the throttle touches, so differencing full throttle against zero at an
    /// otherwise identical state isolates it exactly.
    /// ⚠ Fails under a dropped sign, where the flown behaviour would still look plausible.</summary>
    [Fact]
    public void TheScaleIsReadOffTheNoseAndNotItsMirror()
    {
        float level = ThrustTermAt(0f);
        float climb = ThrustTermAt(90f);
        float dive = ThrustTermAt(-90f);

        Assert.True(climb < level,
            $"a vertical climb must LOSE thrust: {climb:0.000} vs level {level:0.000} m/s², "
            + "the argument's sign is inverted, and the flown result would still look plausible");
        Assert.True(dive > level,
            $"a vertical dive must GAIN thrust: {dive:0.000} vs level {level:0.000} m/s²");
        Assert.Equal(0.6612f, climb / level, 3);
        Assert.Equal(1.24f, dive / level, 3);
    }

    /// <summary>Level flight is untouched, the scale is exactly 1 with the nose on the horizon, so
    /// every wings-level measurement in the pinned envelope is inert by construction, and
    /// this change cannot have bought its climb behaviour by moving the top speed.</summary>
    [Fact]
    public void LevelFlightIsUntouched()
    {
        var m = new FlightModel(Bhawk());
        m.Reset(Vector3.Zero, Basis.Identity, 135f, 1f);
        Assert.Equal(1f, FlightModel.AttitudeThrustScale(m.Attitude.Z.Y), 6);
    }

    /// <summary>The sustained full-throttle climb the original was filmed holding, against the
    /// model. The bound separates the four candidate arrangements (docs/org/flightModel.md, "The
    /// sustained climb"); the residual against the measured 163.05 mph is real and recorded, not
    /// tuned away. The band-edge check matters: a run that crosses 2000 m reports a coast in thin
    /// air, not the climb.</summary>
    [ExtractedDataFact]
    public void TheSustainedClimbSettlesFarBelowEitherFittedArrangement()
    {
        var r = Probes.SustainedClimb(ZrdrPath, "player_bhawk");
        Assert.True(r.Error == null, $"{r.Error ?? "-"}");
        Assert.True(r.BandEdgeAt < 0,
            $"the climb crossed the 2000 m band edge at +{r.BandEdgeAt:0.0} s, this run measures "
            + "the coast above it, not the climb");
        Assert.True(r.PlateauMph < 220.0,
            $"sustained climb settles at {r.PlateauMph:0.00} mph against a measured 163.05, every "
            + "arrangement that keeps the climb-gravity constant or drops the attitude terms sits "
            + "above 230 here");
        Assert.True(r.PlateauMph > 163.05,
            $"sustained climb settles at {r.PlateauMph:0.00} mph, BELOW the original's 163.05, the "
            + "residual has always been on the fast side, so this is a different defect");
    }

    /// <summary>The executable's own sustained climb, taken through the rig the shipped Dynamics
    /// tuner uses. A climb settles slower the steeper it is, so the vertical figure is the FLOOR of
    /// every climb angle: if it is above the filmed 163.05 mph, no sustained climb this plant can
    /// fly reaches that speed, and the footage residual is the footage's
    /// (docs/org/flightModel.md, "The sustained climb").</summary>
    [ExtractedDataFact]
    public void TheTunersOwnClimbFloorIsAboveTheFilmedPlateau()
    {
        var r = Probes.TunerEnvelope(ZrdrPath, "player_bhawk");
        Assert.True(r.Error == null, $"{r.Error ?? "-"}");
        Assert.All(r.Rows, x => Assert.True(x.Converged,
            $"'{x.Name}' never stopped gaining speed in {Probes.TunerEnvelopeSteps} steps, so "
            + $"{x.SpeedMph:0.0} mph is a lower bound, not the equilibrium the tuner reports"));

        double climb90 = r.At(90);
        double climb45 = r.At(45);
        Assert.True(climb90 < climb45,
            $"a vertical climb must settle SLOWER than a 45° one ({climb90:0.0} vs {climb45:0.0} "
            + "mph), so the attitude-thrust sign is inverted");
        Assert.True(climb90 > 163.05,
            $"the tuner's vertical climb settles at {climb90:0.0} mph, at or below the filmed "
            + "163.05, so the plant CAN fly the footage's plateau and the residual has a home in "
            + "the force sum after all");

        // The able-to-fail half: two rigs that share only the force sum. One pins the attitude and
        // integrates from rest at 100 Hz, the other flies free from a 300 mph entry at 60 Hz.
        double free = Probes.SustainedClimb(ZrdrPath, "player_bhawk").PlateauMph;
        Assert.True(System.Math.Abs(r.At(56.3) - free) < 2.0,
            $"the pinned rig settles at {r.At(56.3):0.00} mph on the filmed path and the free climb "
            + $"plateaus at {free:0.00}; they read the same equilibrium, so a gap means one of "
            + "them has stopped measuring it");
    }

    // One step's thrust contribution along the nose, m/s², at a nose-up angle: the
    // difference between a full-throttle step and a zero-throttle step from the same state.
    private static float ThrustTermAt(float noseDeg)
    {
        var attitude = Basis.Identity.Rotated(Vector3.Right, Mathf.DegToRad(noseDeg));
        var full = new FlightModel(Bhawk());
        var idle = new FlightModel(Bhawk());
        full.Reset(Vector3.Zero, attitude, 135f, 1f);
        idle.Reset(Vector3.Zero, attitude, 135f, 0f);
        var before = full.VelocityDir * full.Speed;
        full.Step(new FlightInput { Throttle = 1f }, Dt);
        idle.Step(new FlightInput { Throttle = 0f }, Dt);
        var delta = (full.VelocityDir * full.Speed) - (idle.VelocityDir * idle.Speed);
        _ = before;
        return delta.Length() / Dt;
    }

    // The Bloodhawk's real dynamics, the placeholder `PlaneStats()` defaults are the
    // executable's fallback aircraft and carry a different weight and reference area.
    private static PlaneStats Bhawk() => new()
    {
        PitchTorque = 3.3f,
        RollTorque = 7.5f,
        RudderTorque = 2f,
        ReturnRate = 3f,
        AngMomentumDamp = 5f,
        RecInertia = new Vector3(1.18f, 1f, 1.1f),
        FdSpeed = 135f,
        VehWeight = 1900f,
        RefArea = 330f,
        DragFactor = 0.37f,
        EnginePower = 0.62f,
    };
}
