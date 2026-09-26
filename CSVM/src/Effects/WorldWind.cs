using System;
using System.Collections.Generic;
using CSVM.Flight.Camera;
using Godot;

namespace CSVM.Effects;

/// <summary>The mission's global wind: a static base velocity plus a horizontal random-walk gust,
/// re-derived once per frame. Decoded verbatim from the original's one puffer tick
/// (<c>FUN_0054ee10</c>), authored per mission in <c>weather.zrd</c>'s <c>WIND</c> block. Keys,
/// setters and the engine's own naming: docs/formats/weather/atmosphere.md's Wind section and
/// docs/org/weather.md.
/// ⚠ Not <c>PARTICLES</c>' <c>WIND_DIR</c>/<c>WIND_VEL</c> in the same file. Those drive
/// precipitation drift (<see cref="Precipitation"/>) and are a different mechanism.
/// ⚠ The magnitude step carries no <c>dt</c>; only the heading step does. Reproduced as traced,
/// not smoothed, an invented <c>dt</c> would be a breeze the original never had.
/// </summary>
public sealed class WorldWind
{
    /// <summary><c>RANDOM_ANG_VEL</c>'s degrees→radians factor, as the weather reader spells
    /// it.</summary>
    public const float AngVelDegToRad = 0.017453292f;

    private const float Tau = 6.2831855f;

    private readonly Random _rng;
    private readonly Vector3 _static;
    private readonly float _maxSpeed;
    private readonly float _accelPerFrame;
    private readonly float _angVelRadPerSecond;

    private float _heading;
    private float _magnitude;

    /// <param name="staticVelocity">The <c>STATIC_VELOCITY</c> vector (m/s), world axes.</param>
    /// <param name="randomMaxSpeed"><c>RANDOM_MAX_SPEED</c>, the gust magnitude ceiling.</param>
    /// <param name="randomAccel"><c>RANDOM_ACCEL</c>, the per-frame magnitude step.</param>
    /// <param name="randomAngVelDegrees"><c>RANDOM_ANG_VEL</c> in degrees/s; converted here.</param>
    /// <param name="rng">The draw stream, one per session off <see cref="CSVM.Utils.Rng.Wind"/>,
    /// so <c>--det</c> reproduces the same gust.</param>
    public WorldWind(Vector3 staticVelocity, float randomMaxSpeed, float randomAccel,
        float randomAngVelDegrees, Random rng)
    {
        _static = staticVelocity;
        _maxSpeed = randomMaxSpeed;
        _accelPerFrame = randomAccel;
        _angVelRadPerSecond = randomAngVelDegrees * AngVelDegToRad;
        _rng = rng;
        // The globals live in BSS, so the original's first frame starts from heading 0 and
        // magnitude 0, i.e. from the static vector alone.
        Velocity = staticVelocity;
    }

    /// <summary>The wind this frame. Note the gust is purely HORIZONTAL: the vertical component is
    /// the static vector's, copied verbatim, while only x and z carry
    /// <c>magnitude·cos/sin(heading)</c>.</summary>
    public Vector3 Velocity { get; private set; }

    /// <summary>The gust heading (radians). Exposed for assertions, not for consumers.</summary>
    public float Heading => _heading;

    /// <summary>The gust magnitude (m/s), in <c>[0, RANDOM_MAX_SPEED]</c>. Exposed for
    /// assertions.</summary>
    public float Magnitude => _magnitude;

    /// <summary>A wind that never blows, a mission with no <c>weather.json</c>, and the
    /// still-air default every <see cref="EffectAmbience"/> starts on.</summary>
    public static WorldWind Still() =>
        new(Vector3.Zero, 0f, 0f, 0f, new Random(0));

    /// <summary>Advances the gust one frame, exactly in the engine's order: turn the
    /// heading, step the magnitude, reflect a negative magnitude through +π, clamp to the ceiling,
    /// then compose. The heading wrap happens BEFORE the reflection and is not re-applied after
    /// it, so the stored heading can sit above 2π for a frame, harmless (cos/sin do not care)
    /// and reproduced rather than tidied.</summary>
    public void Step(float dt)
    {
        _heading += Symmetric() * _angVelRadPerSecond * dt;
        if (_heading >= 0f)
        {
            if (_heading >= Tau)
            {
                _heading -= Tau;
            }
        }
        else
        {
            _heading += Tau;
        }

        // ⚠ No dt here, see the class remark. This is the traced arithmetic.
        _magnitude += Symmetric() * _accelPerFrame;
        if (_magnitude < 0f)
        {
            _heading += MathF.PI;
            _magnitude = -_magnitude;
        }
        if (_magnitude > _maxSpeed)
        {
            _magnitude = _maxSpeed;
        }

        Velocity = new Vector3(
            (_magnitude * MathF.Cos(_heading)) + _static.X,
            _static.Y,
            (_magnitude * MathF.Sin(_heading)) + _static.Z);
    }

    // The engine's own symmetric draw: `rand()·3.051851e-05 + rand()·3.051851e-05 −
    // 1.0` with one `rand()` result reused, i.e. `rand()/16384 − 1` over
    // `rand()`'s 0…32767, `[−1, +0.99994]`, not quite symmetric, and quantised to
    // 1/16384. Reproduced at that quantisation because it is free to do so. ⚠ This idiom is the
    // engine's ±1 draw and is deliberately NOT what the spawn deviation uses.
    private float Symmetric() => (_rng.Next(32768) / 16384f) - 1f;
}

/// <summary>The per-frame world state a <see cref="Puffer"/>'s simulation reads but does not own,
/// handed in at construction rather than reached for. One instance per session, written by
/// <c>WeatherRig.Tick</c> and read by every emitter it was passed to.
/// A small mutable holder rather than a value threaded through <c>_Process</c>: a
/// <see cref="Puffer"/> ticks itself off the scene tree, so nothing calls down into it per frame.
/// <see cref="Still"/> is the null object: the wind a puffer feels when nobody wired one in. It
/// refuses to be written, so a missing wire fails loudly at the writer rather than silently
/// blowing on every emitter in the process.</summary>
public sealed class EffectAmbience
{
    // Every pane's camera pose this frame, refilled in place from the session's ViewerSet. A list
    // rather than a single pose: the fade is evaluated per particle against all of them.
    private readonly List<ViewerSet.ViewerPose> _viewers = new();

    private readonly bool _frozen;

    public EffectAmbience()
    {
    }

    private EffectAmbience(bool frozen) => _frozen = frozen;

    /// <summary>Still air, the shared, unwritable default. See the class remark.</summary>
    public static EffectAmbience Still { get; } = new(frozen: true);

    /// <summary>The world's wind velocity this frame (m/s), <see cref="WorldWind.Velocity"/>.
    /// <see cref="Vector3.Zero"/> until someone steps a wind into it.</summary>
    public Vector3 Wind { get; private set; }

    /// <summary>Whether any camera pose has been published. False means "no camera known", and the
    /// camera-distance fade is skipped entirely rather than measured against the origin, the right
    /// answer for every caller that has no camera to give (the unit suites, the plane viewer, the
    /// damage lab), which would otherwise near-cull half their particles under the unauthored
    /// <c>NEAR_FADE</c> default's hard cull at depth 0.</summary>
    public bool HasCamera => _viewers.Count > 0;

    /// <summary>This frame's camera poses, one per rendered pane (one in single player, freecam and
    /// every scripted shot). Position and world-space forward (<c>-Z</c>) both, because the
    /// original's fade distance is the VIEW-SPACE DEPTH along that axis and not the euclidean
    /// range, see <see cref="Puffer.DistanceAlpha"/>, which evaluates the bands against each entry
    /// and keeps the most favourable answer.</summary>
    public IReadOnlyList<ViewerSet.ViewerPose> Viewers => _viewers;

    /// <summary>Publishes this frame's wind. Throws on <see cref="Still"/>, see the class
    /// remark.</summary>
    public void SetWind(Vector3 wind)
    {
        Writable();
        Wind = wind;
    }

    /// <summary>Publishes this frame's one camera pose, replacing whatever was published before.
    /// Throws on <see cref="Still"/>, for the same reason <see cref="SetWind"/> does. The
    /// single-viewer spelling, for a caller that has exactly one camera and no
    /// <see cref="ViewerSet"/> to hand over (the suites, the labs).</summary>
    public void SetCamera(Vector3 position, Vector3 forward)
    {
        Writable();
        _viewers.Clear();
        _viewers.Add(new ViewerSet.ViewerPose(position, forward));
    }

    /// <summary>Publishes this frame's pose for every pane, from the session's viewer set. Throws
    /// on <see cref="Still"/>, for the same reason <see cref="SetWind"/> does.
    /// Unlike the wind, the fade is a draw rule evaluated per pane: a particle draws if any pane
    /// would see it, at the most favourable of the panes' answers (see
    /// <see cref="Puffer.DistanceAlpha"/>), since one shared <c>MultiMesh</c> writes one alpha.
    /// An unbound set leaves <see cref="HasCamera"/> false, the no-camera-no-fade path.</summary>
    public void SetViewers(ViewerSet viewers)
    {
        Writable();
        viewers.Poses(_viewers);
    }

    private void Writable()
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                "EffectAmbience.Still is the still-air null object and cannot be written, "
                + "a session that has weather must construct its own EffectAmbience");
        }
    }
}
