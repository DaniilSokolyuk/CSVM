using System.IO;
using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The flight globals <c>PlaneStats</c> reads from player.json (lift/AoA/G limiters, turn/yaw fade
/// curves, the pitch fade, drag_fade_speed) come off this install's AUTHORED values, not silently
/// off the executable's compiled fallbacks baked into each field's default. A key that reads back
/// as its fallback is the failure mode this test exists to catch.
/// </summary>
public class PlaneStatsFlightGlobalsTests
{
    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    [ExtractedDataFact]
    public void TheAuthoredFlightGlobalsAreLoadedNotFallenBackTo()
    {
        var s = PlaneStats.Load(ZrdrPath, "player_bhawk");

        // lift_accel_rate: authored 0.75, fallback 1.2, NOT converted (a rate, 1/s).
        Assert.Equal(0.75f, s.LiftAccelRate, 3);

        // liftAOAs [5, 9] deg, cosined at load; fallback cosines are 0.98 / 0.96.
        Assert.Equal(Mathf.Cos(Mathf.DegToRad(5f)), s.LiftAoaCosLo, 4);
        Assert.Equal(Mathf.Cos(Mathf.DegToRad(9f)), s.LiftAoaCosHi, 4);
        Assert.NotEqual(0.98f, s.LiftAoaCosLo);
        Assert.NotEqual(0.96f, s.LiftAoaCosHi);

        // maxAOA 46 deg, cosined; fallback cosine is 0.85.
        Assert.Equal(Mathf.Cos(Mathf.DegToRad(46f)), s.MaxAoaCos, 4);
        Assert.NotEqual(0.85f, s.MaxAoaCos);

        // highGs [9, 15] / lowGs [-6, -9], plain G, NOT converted.
        Assert.Equal(9f, s.HighGStart, 3);
        Assert.Equal(15f, s.HighGMax, 3);
        Assert.Equal(-6f, s.LowGStart, 3);
        Assert.Equal(-9f, s.LowGMax, 3);

        // turn_fade_in 10 / turn_fade_out 50 mph, turn_fade_in matches its fallback value,
        // so it alone cannot prove the read; turn_fade_out (authored 50, fallback 40) can.
        Assert.Equal(10f * PhysicsConstants.MphToMs, s.TurnFadeIn, 3);
        Assert.Equal(50f * PhysicsConstants.MphToMs, s.TurnFadeOut, 3);

        // yaw_* set: authored 0.0625 / 0.17 / 10 / 50 / 400 mph against fallbacks
        // 0.05 / 0.1 / 10 / 22.5 / 45 mph, fade_in ties the fallback, the rest don't.
        Assert.Equal(0.0625f, s.YawLowSpeed, 4);
        Assert.Equal(0.17f, s.YawHighSpeed, 3);
        Assert.Equal(10f * PhysicsConstants.MphToMs, s.YawFadeIn, 3);
        Assert.Equal(50f * PhysicsConstants.MphToMs, s.YawMax, 3);
        Assert.Equal(400f * PhysicsConstants.MphToMs, s.YawFadeOut, 3);

        // high_speed_pitch_fade authored [1000, 1001] mph vs fallback [500, 600] mph.
        Assert.Equal(1000f * PhysicsConstants.MphToMs, s.HighSpeedPitchFadeLo, 2);
        Assert.Equal(1001f * PhysicsConstants.MphToMs, s.HighSpeedPitchFadeHi, 2);

        // groundblow_elev 400 / groundblow_mag 10 / ai_groundblow 0.5 against fallbacks
        // 100 / 1.5 / 0.9, all three raw scalars, and elev is METRES, so no conversion applies
        // to any of them. A 400 read back as 400 × MphToMs would be a 179 m ray.
        Assert.Equal(400f, s.GroundBlowElev, 3);
        Assert.Equal(10f, s.GroundBlowMag, 3);
        Assert.Equal(0.5f, s.AiGroundBlow, 3);

        // bounce_factor 0.6 against a fallback of 0.8, and it lives one level down, inside the
        // `crash` block, a reader that looked for it at the top level would read the fallback back.
        Assert.Equal(0.6f, s.BounceFactor, 3);
        Assert.NotEqual(0.8f, s.BounceFactor, 3);

        // The collision damage ranges, same `crash` block: authored [50, 300] against compiled
        // fallbacks [15, 200]. ⚠ Element 0 is the FLOOR and element 1 the SCALE, read in the
        // other order a 50 HP floor silently becomes a 300 HP one.
        Assert.Equal(50f, s.CollideArmorFloor, 3);
        Assert.Equal(300f, s.CollideArmorScale, 3);
        Assert.Equal(50f, s.CollideHealthFloor, 3);
        Assert.Equal(300f, s.CollideHealthScale, 3);

        // drag_fade_speed: authored 40 mph, happens to equal this field's
        // documented-as-unconfirmed fallback, so this only proves the read did not error,
        // not that the read (versus the fallback) took effect; see the field's comment.
        Assert.Equal(40f * PhysicsConstants.MphToMs, s.DragFadeSpeed, 3);
    }

    /// <summary>C22: autohead_turn_time/_max/_min_pitch reproduce the loader's own asymmetric
    /// arithmetic, turn_max's authored degrees are converted THEN DOUBLED, where the compiled
    /// default is already the doubled radian value and is not doubled again.</summary>
    [ExtractedDataFact]
    public void AutoheadReproducesTheLoadersAsymmetricArithmetic()
    {
        var s = PlaneStats.Load(ZrdrPath, "player_bhawk");

        // autohead_turn_time 0.75 s, no unit conversion either side, and happens to equal the
        // compiled default, so this alone only proves the read did not error.
        Assert.Equal(0.75f, s.AutoheadTurnTime, 3);

        // autohead_turn_max: authored 2.86°, converted ×π/180 THEN DOUBLED → 0.0998 rad. The
        // compiled fallback (0.1 rad) is NOT doubled, reading it back doubled would be the bug
        // this test exists to catch.
        Assert.Equal(Mathf.DegToRad(2.86f) * 2f, s.AutoheadTurnMax, 4);
        Assert.Equal(0.0998f, s.AutoheadTurnMax, 3);
        Assert.NotEqual(0.1f, s.AutoheadTurnMax, 4);

        // autohead_turn_min_pitch: authored −3.0°, converted once, no doubling, −0.0524 rad.
        // The compiled default happens to equal it, so this alone cannot prove the read took
        // effect over the fallback; see TheAuthoredFlightGlobalsAreLoadedNotFallenBackTo for that.
        Assert.Equal(Mathf.DegToRad(-3f), s.AutoheadTurnMinPitch, 4);
        Assert.Equal(-0.0524f, s.AutoheadTurnMinPitch, 3);
    }

    /// <summary>C22's compiled-default asymmetry, isolated from any file: a fresh
    /// <c>PlaneStats</c> (no <see cref="PlaneStats.Load"/> call, so no key is ever authored) carries
    /// turn_max already-doubled at 0.1 rad exactly, not degrees-converted-then-doubled.</summary>
    [Fact]
    public void AutoheadFallsBackToTheCompiledDefaultsWhenNoKeyIsPresent()
    {
        var s = new PlaneStats();
        Assert.Equal(0.75f, s.AutoheadTurnTime);
        Assert.Equal(0.1f, s.AutoheadTurnMax);
        Assert.Equal(-0.05235988f, s.AutoheadTurnMinPitch);
    }
}
