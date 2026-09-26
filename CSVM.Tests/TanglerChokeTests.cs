using System.IO;
using System.Linq;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Testing;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The choker's engine-dead duration (<see cref="TanglerChoke"/>) and the timer it feeds
/// (<see cref="FlightModel.ChokeEngine"/>), decoded from <c>FUN_004b9bc0</c>'s <c>TANGLER</c> branch
/// and <c>FUN_004b1690</c>. Every expectation is computed by hand from
/// <c>max × (1 − d²/RADIUS)</c> floored at <c>min</c>, never captured from the implementation.
///
/// <para>The unit mismatch is the point: the numerator is squared and the radius is raw, so the
/// full-strength zone of a 35 m weapon is about 4.6 m wide. A test that squared the radius would
/// pass against a "corrected" implementation and prove the wrong thing, which is why the boundary
/// is asserted in both places.</para>
/// </summary>
public class TanglerChokeTests
{
    private const float Dt = 1f / 60f;

    // wep_12's authored TANGLER block, restated so the pure tests below read as arithmetic. The
    // data half is asserted against the retail catalogue in TheChokersAuthoredPairDrivesTheCurve.
    private const float Wep12Radius = 35f;
    private const float Wep12Min = 5f;
    private const float Wep12Max = 13f;

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>A dead-centre hit gets the whole authored maximum, with nothing scaling it.</summary>
    [Fact]
    public void ADeadCentreHitKillsTheEngineForTheAuthoredMaximum()
    {
        Assert.Equal(13f, TanglerChoke.Duration(0f, Wep12Radius, Wep12Min, Wep12Max), 5);
    }

    /// <summary>The floor is reached at √(8/13 × 35) ≈ 4.64 m, because the squared distance is
    /// divided by the RAW radius. Either side of it is asserted so a squared radius, which would put
    /// this boundary at 21.5 m, fails here.</summary>
    [Fact]
    public void TheFloorIsReachedAtAboutFourPointSixMetresAndNotAtTwentyOne()
    {
        // 4.6 m: d² = 21.16, so (1 − 21.16/35) × 13 = 5.1406 s, still above the floor.
        Assert.Equal(5.1406f, TanglerChoke.Duration(4.6f * 4.6f, Wep12Radius, Wep12Min, Wep12Max), 3);

        // 4.7 m: d² = 22.09, so the term has fallen to 4.79 s and the floor takes over.
        Assert.Equal(Wep12Min, TanglerChoke.Duration(4.7f * 4.7f, Wep12Radius, Wep12Min, Wep12Max), 5);

        // The radius read as a square would leave 8.0 s here rather than the floor.
        Assert.Equal(Wep12Min, TanglerChoke.Duration(15f * 15f, Wep12Radius, Wep12Min, Wep12Max), 5);
    }

    /// <summary>Beyond the radius the term is deeply negative and the floor still holds: the formula
    /// alone never returns less than the minimum, at any distance. What decides whether a distant
    /// aircraft is choked at all is the round's fuse, not this curve.</summary>
    [Fact]
    public void BeyondTheRadiusTheFloorStillHolds()
    {
        Assert.Equal(Wep12Min, TanglerChoke.Duration(36f * 36f, Wep12Radius, Wep12Min, Wep12Max), 5);
        Assert.Equal(Wep12Min, TanglerChoke.Duration(500f * 500f, Wep12Radius, Wep12Min, Wep12Max), 5);
    }

    /// <summary>A zero radius takes the floor rather than dividing by zero. The original's default of
    /// 10.0 puts this out of reach of any authored weapon, so it is our guard, not its behaviour.</summary>
    [Fact]
    public void AZeroRadiusTakesTheFloor()
    {
        Assert.Equal(Wep12Min, TanglerChoke.Duration(0f, 0f, Wep12Min, Wep12Max), 5);
    }

    /// <summary>The bounds are globals set by the last-parsed <c>TANGLER</c>, so a catalogue with no
    /// choker in it keeps the static image's pair.</summary>
    [Fact]
    public void AnInstallWithNoChokerKeepsTheStaticImagesPair()
    {
        Assert.Equal(2f, TanglerChoke.ImageEngineDeadMin);
        Assert.Equal(10f, TanglerChoke.ImageEngineDeadMax);
        Assert.Equal((2f, 10f), TanglerChoke.EngineDeadBounds(new WeaponDefs()));
    }

    /// <summary>The authored numbers, read out of the retail catalogue rather than restated: exactly
    /// one entry carries a <c>TANGLER</c>, so the global pair is <c>wep_12</c>'s <c>[5, 13]</c>, and
    /// its <c>RADIUS</c> is the 35 the curve divides by. Its <c>DETONATION_DISTANCE</c> is also 35,
    /// and that is the one that decides whether a passing aircraft is caught.</summary>
    [ExtractedDataFact]
    public void TheChokersAuthoredPairDrivesTheCurve()
    {
        var defs = WeaponDefs.Load(ZrdrPath, null);
        var chokers = defs.All.Where(d => d.Tangler?.EngineDead != null).ToList();
        Assert.Single(chokers);

        var wep12 = chokers[0];
        Assert.Equal("wep_12", wep12.Id);
        Assert.Equal(Wep12Radius, wep12.Tangler!.Radius);
        Assert.Equal(35f, wep12.DetonationDistance);

        var (min, max) = TanglerChoke.EngineDeadBounds(defs);
        Assert.Equal(Wep12Min, min);
        Assert.Equal(Wep12Max, max);
        Assert.Equal(13f, TanglerChoke.Duration(0f, wep12.Tangler.Radius!.Value, min, max), 5);
        Assert.Equal(min, TanglerChoke.Duration(10f * 10f, wep12.Tangler.Radius.Value, min, max), 5);
    }

    /// <summary>The timer only ever extends: a shorter choke landing on a running one changes
    /// nothing, and a longer one takes over.</summary>
    [Fact]
    public void TheTimerOnlyEverExtends()
    {
        var m = new FlightModel(Bhawk());
        m.ChokeEngine(13f);
        Assert.Equal(13f, m.EngineDeadRemainingS, 5);

        m.ChokeEngine(5f);
        Assert.Equal(13f, m.EngineDeadRemainingS, 5);

        m.ChokeEngine(20f);
        Assert.Equal(20f, m.EngineDeadRemainingS, 5);

        m.ChokeEngine(-1f);
        Assert.Equal(20f, m.EngineDeadRemainingS, 5);
    }

    /// <summary>The timer runs down as the aircraft flies and the engine comes back at the throttle
    /// setting it died on, because nothing on this path touches the lever.</summary>
    [Fact]
    public void TheEngineComesBackWhenTheTimerRunsOut()
    {
        var m = new FlightModel(Bhawk());
        m.Reset(Vector3.Zero, Basis.Identity, 135f, 1f);
        m.ChokeEngine(0.5f);

        for (int i = 0; i < 15; i++)
            m.Step(new FlightInput { Throttle = 1f }, Dt);
        Assert.True(m.EngineDead);
        Assert.Equal(0.25f, m.EngineDeadRemainingS, 2);

        for (int i = 0; i < 30; i++)
            m.Step(new FlightInput { Throttle = 1f }, Dt);
        Assert.False(m.EngineDead);
        Assert.Equal(0f, m.EngineDeadRemainingS);
        Assert.Equal(1f, m.Throttle, 5);
    }

    /// <summary>A choked engine produces no thrust, and nothing else changes: the aircraft bleeds
    /// speed on drag alone rather than snapping to a stall figure, which is why the comparison is
    /// against a twin at the same state rather than against the stall speed.</summary>
    [Fact]
    public void AChokedEngineLosesItsThrustAndOnlyItsThrust()
    {
        var choked = new FlightModel(Bhawk());
        var running = new FlightModel(Bhawk());
        choked.Reset(Vector3.Zero, Basis.Identity, 135f, 1f);
        running.Reset(Vector3.Zero, Basis.Identity, 135f, 1f);
        choked.ChokeEngine(5f);

        for (int i = 0; i < 60; i++)
        {
            choked.Step(new FlightInput { Throttle = 1f }, Dt);
            running.Step(new FlightInput { Throttle = 1f }, Dt);
        }

        Assert.True(choked.Speed < running.Speed,
            $"a choked engine must make no thrust: {choked.Speed:0.00} vs {running.Speed:0.00} m/s");
        Assert.True(choked.Speed < 135f, $"drag alone must bleed the speed off: {choked.Speed:0.00} m/s");
        // Drag over one second, not a snap to the stall figure the recollection describes.
        Assert.True(choked.Speed > choked.StallSpeed,
            $"nothing clamps the airspeed: {choked.Speed:0.00} m/s against a stall of "
            + $"{choked.StallSpeed:0.00}");
    }

    /// <summary>A respawn restarts the engine, so a fresh airframe never flies choked.</summary>
    [Fact]
    public void ClearingTheChokeRestartsTheEngine()
    {
        var m = new FlightModel(Bhawk());
        m.ChokeEngine(13f);
        m.ClearChoke();
        Assert.False(m.EngineDead);
        Assert.Equal(0f, m.EngineDeadRemainingS);
    }

    // The Bloodhawk's real dynamics, as AttitudeThrustTests uses: the placeholder PlaneStats()
    // defaults are the executable's fallback aircraft and carry a different weight and area.
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
