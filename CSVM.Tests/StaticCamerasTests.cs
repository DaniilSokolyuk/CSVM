using System;
using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The death and flyby placement law and the world clearance all three static cameras share, as
/// pure arithmetic: the circle about the flight axis, the longitudinal
/// <c>speed · interval + z</c> carry, the flyby's two excluded wedges and the terrain lift.
/// Engine-free but for a stub world query, so none of it needs a live camera or a live space state.
/// Decode and constants: docs/formats/camparam.md.
/// </summary>
public class StaticCamerasTests
{
    private const float Tol = 1e-3f;

    // The shipped default block's own numbers, so a wrong field reads as a wrong number here.
    private const float DeathX = 80f, DeathInterval = 2f, DeathAlt = 5f, DeathMinAlt = 15.1f;
    private const float FlybyMinRadius = 5.5f, FlybyMaxRadius = 7f;
    private const float FlybyMinInterval = 1.9f, FlybyMaxInterval = 2.3f;
    private const float FlybyMinWatch = 3.8f, FlybyMaxWatch = 4.3f;
    private const float FlybyMinSwitch = 70f, FlybyMaxSwitch = 85f;
    private const float CrashChordY = 1000f, CrashElev = 40f;

    private const float Speed = 100f; // m/s, so speed·interval is a round few hundred metres

    /// <summary>The death spot sits exactly <c>death_x</c> off the flight axis whatever angle is
    /// drawn, which is the part of the law a hand-picked offset would get wrong.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.125f)]
    [InlineData(0.5f)]
    [InlineData(0.875f)]
    public void TheDeathSpotSitsTheAuthoredRadiusOffTheFlightAxis(float draw)
    {
        var local = StaticCameras.DeathLocalOffset(Params(), Speed, draw);
        Assert.Equal(DeathX, new Vector2(local.X, local.Y).Length(), Tol);
    }

    /// <summary>The longitudinal component is <c>speed · death_interval + death_z</c>, so it grows
    /// with airspeed. The field is seconds of velocity projection, never a re-frame timer, and the
    /// sign carries the camera along the aircraft's own nose axis.</summary>
    [Fact]
    public void TheDeathSpotCarriesSpeedTimesTheAuthoredInterval()
    {
        Assert.Equal(-(Speed * DeathInterval), StaticCameras.DeathLocalOffset(Params(), Speed, 0f).Z, Tol);
        Assert.Equal(-(50f * DeathInterval), StaticCameras.DeathLocalOffset(Params(), 50f, 0f).Z, Tol);
        Assert.Equal(0f, StaticCameras.DeathLocalOffset(Params(), 0f, 0f).Z, Tol);
    }

    /// <summary>The flyby's angle never lands inside either 15° wedge, one about the aircraft's
    /// local up and one about its local down, so a re-site is always out on a flank.</summary>
    [Fact]
    public void TheFlybyAngleStaysOutOfBothWedges()
    {
        for (int i = 0; i <= 400; i++)
        {
            float theta = StaticCameras.FlybyAngleRad(i / 400f);
            float fromUp = Math.Abs(theta);
            float fromDown = Mathf.Pi - fromUp;
            Assert.True(fromUp >= StaticCameras.FlybyWedgeRad - Tol,
                $"draw {i / 400f:0.000} landed {Mathf.RadToDeg(fromUp):0.0}° from local up");
            Assert.True(fromDown >= StaticCameras.FlybyWedgeRad - Tol,
                $"draw {i / 400f:0.000} landed {Mathf.RadToDeg(fromDown):0.0}° from local down");
        }
    }

    /// <summary>The wedges cost the draw nothing else: the two arcs it does reach run right up to
    /// their edges, so the whole flank is available rather than a narrowed band inside it.</summary>
    [Fact]
    public void TheFlybyAngleReachesBothWedgeEdges()
    {
        Assert.Equal(-StaticCameras.FlybyWedgeRad, StaticCameras.FlybyAngleRad(0.49999f), Tol);
        Assert.Equal(StaticCameras.FlybyWedgeRad, StaticCameras.FlybyAngleRad(0.5f), Tol);
        Assert.Equal(-(Mathf.Pi - StaticCameras.FlybyWedgeRad), StaticCameras.FlybyAngleRad(0f), Tol);
    }

    /// <summary>Sine and cosine are SWAPPED against the death camera, which is what puts the flyby's
    /// exclusion on the vertical rather than the lateral axis: at the angle that would park the
    /// death camera on the aircraft's own beam, the flyby is above it instead.</summary>
    [Fact]
    public void TheFlybyRotatesItsCircleAgainstTheDeathCameras()
    {
        var death = StaticCameras.DeathLocalOffset(Params(), Speed, 0f);
        Assert.Equal(DeathX, death.X, Tol);   // cosine on the lateral axis
        Assert.Equal(0f, death.Y, Tol);

        // FlybyAngleRad(0.5) is +15°, so a sine-first circle puts most of the radius on the
        // VERTICAL axis there; a cosine-first one would put it on the lateral axis.
        var flyby = StaticCameras.FlybyLocalOffset(Params(), Speed, 0.5f, 0f, 0f);
        Assert.True(Math.Abs(flyby.Y) > Math.Abs(flyby.X),
            $"flyby lateral {flyby.X:0.00} should be the smaller half at 15° off local up");
    }

    /// <summary>Both drawn ranges are read from the authored min/max pair and nothing else.</summary>
    [Fact]
    public void TheFlybyRadiusAndIntervalComeFromTheAuthoredRanges()
    {
        var near = StaticCameras.FlybyLocalOffset(Params(), Speed, 0.3f, 0f, 0f);
        var far = StaticCameras.FlybyLocalOffset(Params(), Speed, 0.3f, 1f, 1f);
        Assert.Equal(FlybyMinRadius, new Vector2(near.X, near.Y).Length(), Tol);
        Assert.Equal(FlybyMaxRadius, new Vector2(far.X, far.Y).Length(), Tol);
        Assert.Equal(-(Speed * FlybyMinInterval), near.Z, Tol);
        Assert.Equal(-(Speed * FlybyMaxInterval), far.Z, Tol);
    }

    /// <summary>The clearance only ever raises, and it raises to exactly <c>crash_elev</c> above
    /// the surface the probe found.</summary>
    [Fact]
    public void TheClearanceLiftsACandidateToTheAuthoredHeightAboveTerrain()
    {
        var world = new RayStub(hit: true, hitY: 200f);
        var lifted = StaticCameras.LiftClearOfWorld(new Vector3(10f, 205f, -3f), Params(), world, null);
        Assert.Equal(new Vector3(10f, 240f, -3f), lifted);
    }

    [Fact]
    public void TheClearanceLeavesACandidateAlreadyClearWhereItWas()
    {
        var world = new RayStub(hit: true, hitY: 200f);
        var candidate = new Vector3(10f, 400f, -3f);
        Assert.Equal(candidate, StaticCameras.LiftClearOfWorld(candidate, Params(), world, null));
    }

    [Fact]
    public void TheClearanceIsANoOpWhereTheProbeFindsNothing()
    {
        var candidate = new Vector3(10f, 5f, -3f);
        Assert.Equal(candidate,
            StaticCameras.LiftClearOfWorld(candidate, Params(), new RayStub(hit: false, hitY: 0f), null));
        Assert.Equal(candidate, StaticCameras.LiftClearOfWorld(candidate, Params(), null, null));
    }

    /// <summary>The probe's own window: <c>crash_chord_y</c> above the candidate down to the
    /// hard-coded drop below it, straight down and not along any view direction.</summary>
    [Fact]
    public void TheProbeRunsStraightDownTheAuthoredChordAboveTheCandidate()
    {
        var world = new RayStub(hit: false, hitY: 0f);
        StaticCameras.LiftClearOfWorld(new Vector3(12f, 60f, -7f), Params(), world, null);
        Assert.Equal(new Vector3(12f, 60f + CrashChordY, -7f), world.LastFrom);
        Assert.Equal(new Vector3(12f, 60f - StaticCameras.ProbeDropMetres, -7f), world.LastTo);
    }

    /// <summary>The death camera's absolute floor bites before the terrain probe does: a spot drawn
    /// under <c>death_min_alt</c> is lifted to it whatever the ground is doing.</summary>
    [Fact]
    public void TheDeathSpotNeverSitsBelowTheAuthoredFloor()
    {
        var rig = new StaticCameras(Params(), Fixed(0.25f));
        var point = rig.StepDeath(new Vector3(0f, -500f, 0f), Basis.Identity, 0f, null, null);
        Assert.Equal(DeathMinAlt, point.Y, Tol);
    }

    /// <summary>The death camera chooses ONE spot and holds it: later steps re-aim from the same
    /// place rather than re-framing, which is what separates it from the flyby.</summary>
    [Fact]
    public void TheDeathCameraPlacesOnceAndHolds()
    {
        var rig = new StaticCameras(Params(), Walk(0.1f));
        var first = rig.StepDeath(new Vector3(0f, 900f, 0f), Basis.Identity, Speed, null, null);
        for (int i = 0; i < 10; i++)
        {
            var later = rig.StepDeath(new Vector3(0f, 900f - (i * 40f), 0f), Basis.Identity, Speed, null, null);
            Assert.Equal(first, later);
        }
        Assert.Equal(1, rig.Placements);
    }

    /// <summary>The flyby's watch deadline is an absolute sim time out of the authored pair, and
    /// nothing re-sites before it, however far the aircraft has run.</summary>
    [Fact]
    public void TheFlybyHoldsItsSpotUntilTheDrawnWatchDeadline()
    {
        var rig = new StaticCameras(Params(), Fixed(0f));
        var spot = rig.StepFlyby(0f, Vector3.Zero, Basis.Identity, Speed, null, null);
        Assert.Equal(FlybyMinWatch, rig.WatchUntil, Tol);
        Assert.Equal(FlybyMinSwitch, rig.SwitchDistance, Tol);

        // Far past the switch distance, but inside the watch window: still the same spot.
        var same = rig.StepFlyby(FlybyMinWatch - 0.1f, new Vector3(0f, 0f, 5000f), Basis.Identity,
            Speed, null, null);
        Assert.Equal(spot, same);
        Assert.Equal(1, rig.Placements);
    }

    /// <summary>Past the deadline, exceeding the drawn switch distance asks for a re-site, and the
    /// move lands on the FOLLOWING step because the original tests the request first.</summary>
    [Fact]
    public void TheFlybyResitesOnTheStepAfterTheSwitchDistanceIsExceeded()
    {
        var rig = new StaticCameras(Params(), Fixed(0f));
        var spot = rig.StepFlyby(0f, Vector3.Zero, Basis.Identity, Speed, null, null);

        float past = FlybyMinWatch + 0.1f;
        var trip = rig.StepFlyby(past, new Vector3(0f, 0f, FlybyMinSwitch + 50f), Basis.Identity,
            Speed, null, null);
        Assert.Equal(spot, trip);            // the frame that trips it still holds the old spot
        Assert.True(rig.Armed);
        Assert.Equal(1, rig.Placements);

        rig.StepFlyby(past, new Vector3(0f, 0f, FlybyMinSwitch + 50f), Basis.Identity, Speed, null, null);
        Assert.Equal(2, rig.Placements);     // …and the next one moves
        Assert.False(rig.Armed);
    }

    /// <summary>Inside the switch distance the pass simply continues past its own deadline: the
    /// aircraft has to leave before the camera does.</summary>
    [Fact]
    public void TheFlybyStaysPutWhileTheAircraftIsStillInside()
    {
        // Stationary, so the spot lands beside the origin and the distance below is the one the
        // test means rather than the longitudinal carry a live airspeed would add to it.
        var rig = new StaticCameras(Params(), Fixed(0f));
        var spot = rig.StepFlyby(0f, Vector3.Zero, Basis.Identity, 0f, null, null);
        for (int i = 0; i < 20; i++)
        {
            var at = new Vector3(0f, 0f, FlybyMinSwitch - 10f);
            Assert.True(at.DistanceTo(spot) < FlybyMinSwitch);
            rig.StepFlyby(FlybyMinWatch + i, at, Basis.Identity, 0f, null, null);
        }
        Assert.Equal(1, rig.Placements);
    }

    /// <summary>Re-entering the camera starts a fresh pass rather than resuming the deadline the
    /// last one left behind, which would otherwise hold an entry frame on a stale spot.</summary>
    [Fact]
    public void ResettingTheFlybyPlacesAtOnce()
    {
        var rig = new StaticCameras(Params(), Fixed(1f));
        rig.StepFlyby(0f, Vector3.Zero, Basis.Identity, Speed, null, null);
        Assert.Equal(FlybyMaxWatch, rig.WatchUntil, Tol);
        Assert.Equal(FlybyMaxSwitch, rig.SwitchDistance, Tol);

        rig.ResetFlyby();
        rig.StepFlyby(0f, new Vector3(0f, 0f, 900f), Basis.Identity, Speed, null, null);
        Assert.Equal(2, rig.Placements);
    }

    /// <summary>The local offset goes through the aircraft's own basis, so a rolled aeroplane
    /// carries its death camera round with it instead of leaving it in world space.</summary>
    [Fact]
    public void ThePlacementRidesTheAircraftBasis()
    {
        var level = new StaticCameras(Params(), Fixed(0f));
        var rolled = new StaticCameras(Params(), Fixed(0f));
        var at = new Vector3(0f, 2000f, 0f);
        var a = level.StepDeath(at, Basis.Identity, 0f, null, null);
        var b = rolled.StepDeath(at, new Basis(Vector3.Forward, Mathf.Pi * 0.5f), 0f, null, null);
        Assert.Equal(DeathX, a.X - at.X, Tol);
        Assert.NotEqual(a, b);
    }

    private static CamParams Params() => new()
    {
        DeathX = DeathX,
        DeathInterval = DeathInterval,
        DeathZ = 0f,
        DeathAlt = DeathAlt,
        DeathMinAlt = DeathMinAlt,
        FlybyMinRadius = FlybyMinRadius,
        FlybyMaxRadius = FlybyMaxRadius,
        FlybyMinInterval = FlybyMinInterval,
        FlybyMaxInterval = FlybyMaxInterval,
        FlybyMinWatchTime = FlybyMinWatch,
        FlybyMaxWatchTime = FlybyMaxWatch,
        FlybyMinSwitchDist = FlybyMinSwitch,
        FlybyMaxSwitchDist = FlybyMaxSwitch,
        FlybyZ = 0f,
        FlybyY = 0f,
        FlybyMinAlt = 0.1f,
        CrashChordY = CrashChordY,
        CrashElev = CrashElev,
    };

    // Every draw the same value: the whole placement becomes a function of one number, which is
    // what lets a test name the exact metres it expects.
    private static Func<float> Fixed(float value) => () => value;

    // Successive draws, so a second placement cannot land on the first one by construction.
    private static Func<float> Walk(float step)
    {
        float at = 0f;
        return () =>
        {
            at = (at + step) % 1f;
            return at;
        };
    }

    // A one-surface world: every ray answers at the same height, and the endpoints are kept so the
    // probe's own window can be asserted.
    private sealed class RayStub : IWorldQuery
    {
        private readonly bool _hit;
        private readonly float _hitY;

        public RayStub(bool hit, float hitY)
        {
            _hit = hit;
            _hitY = hitY;
        }

        public Vector3 LastFrom { get; private set; }

        public Vector3 LastTo { get; private set; }

        public bool Sweep(IReadOnlyList<PlaneCollider.Part> parts, Transform3D baseTransform,
            Vector3 motion, uint mask, Godot.Collections.Array<Rid>? exclude, out SweepReport report)
        {
            report = default;
            return false;
        }

        public bool Ray(Vector3 from, Vector3 to, uint mask, Godot.Collections.Array<Rid>? exclude,
            out RayReport report)
        {
            LastFrom = from;
            LastTo = to;
            report = _hit ? new RayReport(new Vector3(from.X, _hitY, from.Z), Vector3.Up, null) : default;
            return _hit;
        }

        public bool Overlaps(IReadOnlyList<PlaneCollider.Part> parts, Transform3D pose, uint mask,
            Godot.Collections.Array<Rid>? exclude) => false;
    }
}
