using System;
using CSVM.Flight.Airframe;
using Godot;

namespace CSVM.Flight.Camera;

/// <summary>The original's STATIC cameras, the crash cut, the death camera and the flyby, as one
/// law: a world point chosen once out of <see cref="CamParams"/>, lifted clear of whatever terrain
/// stands under it, then held while the view re-aims at the aircraft every frame. All three share
/// the lift, and the two random ones share the placement shape, so neither may grow an offset of
/// its own. The placement and the lift rule are static and engine-free; only the terrain probe
/// needs a live world. Constants and provenance: docs/formats/camparam.md.</summary>
public sealed class StaticCameras
{
    /// <summary>How far BELOW the candidate the clearance ray ends, metres. A literal in the
    /// original rather than a <c>camparam</c> field, and the only part of the probe the data does
    /// not author; <c>crash_chord_y</c> is how far above the candidate it starts.</summary>
    public const float ProbeDropMetres = 1000f;

    /// <summary>Half the arc the flyby's angle is kept out of, radians (15°). Two such arcs are
    /// excluded, one about the aircraft's local up and one about its local down, so a re-site
    /// always sits out on a flank rather than over or under the flight path.</summary>
    public const float FlybyWedgeRad = 0.2617994f;

    // What is left of a full turn once both wedges are removed, as a factor on the drawn angle.
    // The wedge is added back after the scale, which is what makes each excluded arc exactly
    // 2·FlybyWedgeRad wide however the draw lands.
    private const float FlybyWedgeScale = 5f / 6f;

    private readonly CamParams _cam;

    // One uniform draw in [0, 1). The original's `rand() * 3.051851e-05` is rand()/32767, and the
    // five draws a re-site makes come off this in the order the executable makes them.
    private readonly Func<float> _draw;

    private Vector3 _point;
    private bool _armed = true;
    private float _watchUntil;
    private float _switchDistSq;

    public StaticCameras(CamParams cam, Func<float> draw)
    {
        _cam = cam;
        _draw = draw;
    }

    /// <summary>The held world point: where the camera sits until something re-places it.</summary>
    public Vector3 Point => _point;

    /// <summary>Whether a placement is still owed. The original keeps one shared flag that the mode
    /// setter raises on entry and each static camera's own per-frame handler consumes.</summary>
    public bool Armed => _armed;

    /// <summary>Sim seconds the flyby must watch from its current spot before distance may ask for
    /// a re-site. An absolute deadline, not a countdown, exactly as the original stores it.</summary>
    public float WatchUntil => _watchUntil;

    /// <summary>The distance beyond which the flyby re-sites once the watch deadline has passed.
    /// Squared internally, because the test the original makes is against a squared distance.</summary>
    public float SwitchDistance => Mathf.Sqrt(_switchDistSq);

    /// <summary>How many times a point has been chosen. The flyby's re-site count, which is what a
    /// suite reads to prove the camera moved rather than merely held.</summary>
    public int Placements { get; private set; }

    /// <summary>The flyby's angle law: the drawn uniform turned into an angle about the flight axis
    /// with the two 15° wedges removed. Pure, so the exclusion unit-tests over the whole draw
    /// range.</summary>
    public static float FlybyAngleRad(float draw)
    {
        float a = (draw * Mathf.Tau) - Mathf.Pi;
        return (a * FlybyWedgeScale) + (a < 0f ? -FlybyWedgeRad : FlybyWedgeRad);
    }

    /// <summary>The death camera's plane-local offset: a point on a circle of radius
    /// <c>death_x</c> about the flight axis, carried <c>speed·death_interval + death_z</c> metres
    /// along the nose. Pure. ⚠ The third component is negative in the plane's own frame, which is
    /// AHEAD of the nose here because this codebase's plane basis has its nose at −Z.</summary>
    public static Vector3 DeathLocalOffset(CamParams cam, float speed, float angleDraw)
    {
        float theta = angleDraw * Mathf.Tau;
        return new Vector3(cam.DeathX * Mathf.Cos(theta), cam.DeathX * Mathf.Sin(theta),
            -((speed * cam.DeathInterval) + cam.DeathZ));
    }

    /// <summary>The flyby's plane-local offset: the same shape at a drawn radius and a drawn
    /// longitudinal interval. ⚠ Sine and cosine are swapped against
    /// <see cref="DeathLocalOffset"/> in the original, which is what puts the flyby's excluded
    /// wedges on the aircraft's local vertical instead of its lateral axis. Pure.</summary>
    public static Vector3 FlybyLocalOffset(CamParams cam, float speed,
        float angleDraw, float radiusDraw, float intervalDraw)
    {
        float theta = FlybyAngleRad(angleDraw);
        float radius = Lerp(cam.FlybyMinRadius, cam.FlybyMaxRadius, radiusDraw);
        float interval = Lerp(cam.FlybyMinInterval, cam.FlybyMaxInterval, intervalDraw);
        return new Vector3(radius * Mathf.Sin(theta), radius * Mathf.Cos(theta),
            -((speed * interval) + cam.FlybyZ));
    }

    /// <summary>The shared world clearance: a candidate never sits closer than <c>crash_elev</c>
    /// above the highest terrain under it, the probe running from <c>crash_chord_y</c> above the
    /// candidate down to <see cref="ProbeDropMetres"/> below it. Only ever raises. A downward ray's
    /// FIRST hit is the highest surface in that window, which is the maximum the original takes
    /// over its whole hit list. Pure but for the probe, so the rule tests on a stub world.</summary>
    public static Vector3 LiftClearOfWorld(Vector3 candidate, CamParams cam, IWorldQuery? world,
        Godot.Collections.Array<Rid>? exclude)
    {
        if (world == null)
        {
            return candidate;
        }
        var from = new Vector3(candidate.X, candidate.Y + cam.CrashChordY, candidate.Z);
        var to = new Vector3(candidate.X, candidate.Y - ProbeDropMetres, candidate.Z);
        // World layer only, and the aircraft excluded on top of it: the original hides the camera's
        // own aeroplane before the ray, so a plane is never its own obstacle.
        if (!world.Ray(from, to, CollisionLayers.World, exclude, out var report))
        {
            return candidate;
        }
        float floor = report.Position.Y + cam.CrashElev;
        return candidate.Y < floor ? new Vector3(candidate.X, floor, candidate.Z) : candidate;
    }

    /// <summary>Ask for a fresh point on the next step. The camera holds its current one until that
    /// step runs, so arming never moves the view by itself.</summary>
    public void Arm() => _armed = true;

    /// <summary>The death camera, one step: place once if a placement is owed, then report the held
    /// point. There is no timer and no re-frame, the original chooses one spot for the whole
    /// hold, so every later step re-aims at a moving wreck from a fixed vantage.</summary>
    public Vector3 StepDeath(Vector3 planePos, Basis attitude, float speed, IWorldQuery? world,
        Godot.Collections.Array<Rid>? exclude)
    {
        if (_armed)
        {
            _armed = false;
            _point = Place(DeathLocalOffset(_cam, speed, _draw()), planePos, attitude,
                _cam.DeathAlt, _cam.DeathMinAlt, world, exclude);
        }
        return _point;
    }

    /// <summary>The flyby, one step, on the sim clock: re-site when one is owed and the watch
    /// deadline has passed, then ask for the next one once the aircraft is past the drawn switch
    /// distance. ⚠ The re-site check runs BEFORE the distance check, as it does in the original, so
    /// a tripped threshold moves the camera on the FOLLOWING step and the pass is never cut at the
    /// frame it is measured on.</summary>
    public Vector3 StepFlyby(float now, Vector3 planePos, Basis attitude, float speed,
        IWorldQuery? world, Godot.Collections.Array<Rid>? exclude)
    {
        if (_armed && _watchUntil <= now)
        {
            _armed = false;
            _point = Place(FlybyLocalOffset(_cam, speed, _draw(), _draw(), _draw()), planePos,
                attitude, _cam.FlybyY, _cam.FlybyMinAlt, world, exclude);
            _watchUntil = now + Lerp(_cam.FlybyMinWatchTime, _cam.FlybyMaxWatchTime, _draw());
            float switchDist = Lerp(_cam.FlybyMinSwitchDist, _cam.FlybyMaxSwitchDist, _draw());
            _switchDistSq = switchDist * switchDist;
        }
        else if (_watchUntil <= now && planePos.DistanceSquaredTo(_point) > _switchDistSq)
        {
            _armed = true;
        }
        return _point;
    }

    /// <summary>Put the flyby back to its entry state: the next step places at once rather than
    /// waiting out a deadline left over from the last time the camera ran.</summary>
    public void ResetFlyby()
    {
        _armed = true;
        _watchUntil = 0f;
        _switchDistSq = 0f;
    }

    private static float Lerp(float min, float max, float draw) => min + ((max - min) * draw);

    // The shared tail of both placements: the local offset through the aircraft's basis, the
    // authored world-Y addition, the authored absolute floor, then the terrain lift. The order is
    // the original's and matters, the floor is applied before the probe, so a candidate under the
    // floor is lifted to it first and only then measured against the ground.
    private Vector3 Place(Vector3 local, Vector3 planePos, Basis attitude, float altAdd,
        float minAlt, IWorldQuery? world, Godot.Collections.Array<Rid>? exclude)
    {
        Placements++;
        var world3 = planePos + (attitude * local);
        world3.Y = Mathf.Max(world3.Y + altAdd, minAlt);
        return LiftClearOfWorld(world3, _cam, world, exclude);
    }
}
