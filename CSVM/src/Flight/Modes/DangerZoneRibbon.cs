using System;
using System.Collections.Generic;
using Godot;

namespace CSVM.Flight.Modes;

/// <summary>One <c>dzpathN</c> route ribbon as the original flies it: the route polygon's vertices
/// joined by cubic segments parameterised in metres, plus the lane offsets the zone offers
/// (docs/org/aiPilot.md "The danger-zone run"). Pure geometry, shared by every pilot in the
/// mission; the per-pilot progress is a <see cref="DangerZoneRun"/>.</summary>
public sealed class DangerZoneRibbon
{
    /// <summary>How near the run's current point a pilot must come before the pose is written off
    /// the ribbon: 105 m, the original's 11025 m² test (<c>FUN_004216e0</c>).</summary>
    public const float LockRangeM = 105f;

    /// <summary>The reach of the proximity pick a hit pilot rolls for: an end inside 500 m
    /// (<c>FUN_004210e0</c>, 250000 m²). Not the node-tag entry, which has no range at all.</summary>
    public const float ProximityRangeM = 500f;

    private readonly int[] _laneCounts;

    private DangerZoneRibbon(string name, int index, IReadOnlyList<Segment> segments,
        IReadOnlyList<Vector3> lanes)
    {
        Name = name;
        Index = index;
        Segments = segments;
        Lanes = lanes;
        _laneCounts = new int[lanes.Count];
    }

    /// <summary>The gamez node name, <c>dzpathN</c>.</summary>
    public string Name { get; }

    /// <summary>The N of <c>dzpathN</c>, what a net node's danger-zone field names.</summary>
    public int Index { get; }

    /// <summary>Whether a pilot may take this zone. The mission's <c>dzones.zrd</c> <c>disable</c>
    /// list clears it, and the script's zone on/off op is the same byte (<c>+0x48</c>).</summary>
    public bool Active { get; set; } = true;

    /// <summary>The authored difficulty a pilot's <c>natural_touch</c> is compared against on the
    /// proximity pick, off the node's flag word (<c>+0x28 &gt;&gt; 23</c>). Zero on every shipped
    /// zone; the node-tag entry never tests it.</summary>
    public int Difficulty { get; init; }

    public IReadOnlyList<Segment> Segments { get; }

    /// <summary>The lateral offsets a run may fly at: the zero lane, then one per child node of
    /// the ribbon (its local translation). A pilot takes the least-occupied lane.</summary>
    public IReadOnlyList<Vector3> Lanes { get; }

    /// <summary>Whether some lane is unoccupied, the proximity pick's admission test.</summary>
    public bool HasFreeLane
    {
        get
        {
            foreach (int count in _laneCounts)
            {
                if (count == 0)
                    return true;
            }
            return false;
        }
    }

    /// <summary>Builds the ribbon from the route polygon's vertices in polygon order. Each pair of
    /// consecutive vertices becomes a cubic, the neighbouring chords averaged into its end
    /// tangents. The parameter is then rescaled to the chord length, the original's dzpath.cpp
    /// build (<c>FUN_00445ef0</c>). The difficulty is the node's own, see
    /// <see cref="Difficulty"/>.</summary>
    public static DangerZoneRibbon FromPolyline(string name, int index, IReadOnlyList<Vector3> vertices,
        IReadOnlyList<Vector3>? laneOffsets = null, int difficulty = 0)
    {
        if (vertices.Count < 2)
            throw new ArgumentException($"'{name}': a ribbon needs at least two vertices", nameof(vertices));
        var segments = new List<Segment>(vertices.Count - 1);
        for (int i = 0; i + 1 < vertices.Count; i++)
        {
            var p0 = vertices[i];
            var p1 = vertices[i + 1];
            var chord = p1 - p0;
            var m0 = i > 0 ? (chord + (p0 - vertices[i - 1])) * 0.5f : chord;
            var m1 = i + 2 < vertices.Count ? (chord + (vertices[i + 2] - p1)) * 0.5f : chord;
            float length = chord.Length();
            if (length < 1e-3f)
                continue; // a repeated vertex has no chord to rescale by
            // Hermite on t in [0,1], then rescaled so t runs in metres along the chord.
            var b = m0;
            var c = (chord * 3f) - (m0 * 2f) - m1;
            var d = (chord * -2f) + m0 + m1;
            segments.Add(new Segment(length, p0, b / length, c / (length * length),
                d / (length * length * length)));
        }
        if (segments.Count == 0)
            throw new ArgumentException($"'{name}': every vertex coincides", nameof(vertices));
        var lanes = new List<Vector3> { Vector3.Zero };
        if (laneOffsets != null)
            lanes.AddRange(laneOffsets);
        return new DangerZoneRibbon(name, index, segments, lanes) { Difficulty = difficulty };
    }

    /// <summary>The point <paramref name="t"/> metres into segment <paramref name="segment"/>,
    /// displaced by lane <paramref name="lane"/>.</summary>
    public Vector3 PointAt(int segment, float t, int lane = 0)
    {
        var s = Segments[segment];
        return s.A + (s.B * t) + (s.C * (t * t)) + (s.D * (t * t * t)) + Lanes[lane];
    }

    /// <summary>The first derivative at the parameter, metres per metre of parameter (about unit
    /// length on a straight chord).</summary>
    public Vector3 TangentAt(int segment, float t)
    {
        var s = Segments[segment];
        return s.B + (s.C * (2f * t)) + (s.D * (3f * t * t));
    }

    /// <summary>The second derivative at the parameter: the direction the ribbon bends toward,
    /// which the run banks its wings into.</summary>
    public Vector3 CurvatureAt(int segment, float t)
    {
        var s = Segments[segment];
        return (s.C * 2f) + (s.D * (6f * t));
    }

    /// <summary>The ribbon's first vertex, or its last when <paramref name="far"/>.</summary>
    public Vector3 End(bool far) =>
        far ? PointAt(Segments.Count - 1, Segments[Segments.Count - 1].Length) : PointAt(0, 0f);

    /// <summary>The tangent at an end pointing INTO the ribbon, the proximity pick's facing test.</summary>
    public Vector3 EndTangentInto(bool far) =>
        far ? -TangentAt(Segments.Count - 1, Segments[Segments.Count - 1].Length) : TangentAt(0, 0f);

    /// <summary>Takes the least-occupied lane for a run, counting it occupied until
    /// <see cref="ReleaseLane"/>.</summary>
    public int TakeLane()
    {
        int best = 0;
        for (int i = 1; i < _laneCounts.Length; i++)
        {
            if (_laneCounts[i] < _laneCounts[best])
                best = i;
        }
        _laneCounts[best]++;
        return best;
    }

    public void ReleaseLane(int lane)
    {
        if (lane >= 0 && lane < _laneCounts.Length && _laneCounts[lane] > 0)
            _laneCounts[lane]--;
    }

    /// <summary>One cubic piece, <c>A + Bt + Ct² + Dt³</c> for t from 0 to <see cref="Length"/>.</summary>
    public readonly record struct Segment(float Length, Vector3 A, Vector3 B, Vector3 C, Vector3 D);
}

/// <summary>One pilot's progress along a <see cref="DangerZoneRibbon"/>: which end it entered
/// from, its lane, and the cursor the rail run advances (the original's 0x18-byte run record at
/// vehicle <c>+0x9bc</c>).</summary>
public sealed class DangerZoneRun
{
    // The original advances the cursor in five sub-steps per frame, each a fifth of the frame's
    // distance divided by the local tangent length.
    private const int SubSteps = 5;

    public DangerZoneRun(DangerZoneRibbon ribbon, bool reversed)
    {
        Ribbon = ribbon;
        Reversed = reversed;
        Lane = ribbon.TakeLane();
        if (reversed)
        {
            Segment = ribbon.Segments.Count - 1;
            T = ribbon.Segments[Segment].Length;
        }
    }

    public DangerZoneRibbon Ribbon { get; }

    /// <summary>Entered from the far vertex, so the cursor walks the segments backwards.</summary>
    public bool Reversed { get; }

    public int Lane { get; }

    public int Segment { get; private set; }

    /// <summary>Metres into <see cref="Segment"/>.</summary>
    public float T { get; private set; }

    /// <summary>The cursor ran off the ribbon's far end (or its near end when reversed).</summary>
    public bool Done { get; private set; }

    /// <summary>Whether the cursor sits on the segment the run leaves the ribbon from, where the
    /// original halves its attitude blend rate.</summary>
    public bool OnLastSegment => Reversed ? Segment == 0 : Segment == Ribbon.Segments.Count - 1;

    public Vector3 Point => Ribbon.PointAt(Segment, T, Lane);

    /// <summary>The unit direction of travel at the cursor, the ribbon's tangent flipped for a
    /// reversed run.</summary>
    public Vector3 Direction
    {
        get
        {
            var tangent = Ribbon.TangentAt(Segment, T);
            if (Reversed)
                tangent = -tangent;
            return tangent.LengthSquared() > 1e-8f ? tangent.Normalized() : Vector3.Forward;
        }
    }

    /// <summary>The bend direction at the cursor (see <see cref="DangerZoneRibbon.CurvatureAt"/>).</summary>
    public Vector3 Curvature => Ribbon.CurvatureAt(Segment, T);

    /// <summary>Moves the cursor <paramref name="metres"/> along the ribbon, stepping across
    /// segment ends; running off the exit end sets <see cref="Done"/>.</summary>
    public void Advance(float metres)
    {
        if (Done)
            return;
        float per = metres / SubSteps;
        for (int i = 0; i < SubSteps && !Done; i++)
        {
            float tangentLength = Ribbon.TangentAt(Segment, T).Length();
            float step = tangentLength > 1e-6f ? per / tangentLength : per;
            if (!Reversed)
            {
                T += step;
                while (T >= Ribbon.Segments[Segment].Length)
                {
                    if (Segment >= Ribbon.Segments.Count - 1)
                    {
                        Done = true;
                        break;
                    }
                    T -= Ribbon.Segments[Segment].Length;
                    Segment++;
                }
            }
            else
            {
                T -= step;
                while (T <= 0f)
                {
                    if (Segment <= 0)
                    {
                        Done = true;
                        break;
                    }
                    Segment--;
                    T += Ribbon.Segments[Segment].Length;
                }
            }
        }
    }

    /// <summary>Hands the lane back; the run is finished or abandoned.</summary>
    public void Release() => Ribbon.ReleaseLane(Lane);
}

/// <summary>The on-rails integrator of the navigating mode: the pose comes off the ribbon, not
/// the flight model (<c>FUN_00490590</c>, run in place of the physics while the state is 5). The
/// aeroplane's residual offset from the rail closes over the first seconds, the wings bank into
/// the bend and the speed settles on a 155 mph cruise eased by the climb angle.</summary>
public sealed class DangerZoneRail
{
    /// <summary>The rail cruise, 69.2912 m/s (155 mph), before the climb-angle term.</summary>
    public const float CruiseSpeedMps = 69.2912f;

    /// <summary>The speed given up per unit of the direction's vertical component: 4.4704 m/s
    /// (10 mph), so a straight climb cruises at 145 mph and a dive at 165.</summary>
    public const float ClimbSpeedPenaltyMps = 4.4704f;

    /// <summary>How fast the speed walks toward the cruise, 22.352 m/s² (50 mph per second).</summary>
    public const float SpeedRateMps2 = 22.352f;

    /// <summary>The attitude blend rate toward the rail's frame, per second; the fraction of the
    /// remaining rotation taken each second, capped at the whole of it.</summary>
    public const float AttitudeRatePerS = 1.3f;

    /// <summary>The blend rate on the run's last segment, where the exit is eased.</summary>
    public const float ExitAttitudeRatePerS = 0.5f;

    /// <summary>The rail point's own smoothing, an exponential with this rate per second.</summary>
    public const float PointSmoothingPerS = 10f;

    /// <summary>The closing rate of the residual offset and its velocity once fully ramped,
    /// 111.76 m/s (250 mph, the AI speed ceiling).</summary>
    public const float OffsetClosingRateMps = 111.76f;

    /// <summary>The closing rate ramps up over the first 1/0.6 s after the lock.</summary>
    public const float OffsetRampPerS = 0.6f;

    private Vector3 _point;
    private Vector3 _offset;
    private Vector3 _offsetVelocity;
    private float _sinceLock;

    /// <summary>Locks the run at the aeroplane's present state: the offset from the rail point and
    /// the velocity the rail motion does not account for are what the run then closes out.</summary>
    public DangerZoneRail(DangerZoneRun run, Vector3 position, Basis attitude, Vector3 velocity, float speed)
    {
        Run = run;
        _point = run.Point;
        _offset = position - _point;
        _offsetVelocity = velocity - (run.Direction * speed);
        Attitude = attitude;
        Speed = speed;
        Position = position;
    }

    public DangerZoneRun Run { get; }

    public Vector3 Position { get; private set; }

    public Basis Attitude { get; private set; }

    public float Speed { get; private set; }

    /// <summary>The frame the rail asks for at the cursor: nose along the direction of travel and
    /// the wings banked so "up" points into the bend; a straight stretch keeps world up.</summary>
    public static Basis RailFrame(Vector3 direction, Vector3 curvature)
    {
        var lateral = curvature - (direction * curvature.Dot(direction));
        Vector3 up;
        if (lateral.LengthSquared() > 1e-6f)
        {
            up = lateral.Normalized();
        }
        else
        {
            up = Vector3.Up - (direction * Vector3.Up.Dot(direction));
            up = up.LengthSquared() > 1e-6f ? up.Normalized() : Vector3.Back;
        }
        var right = up.Cross(-direction).Normalized();
        return new Basis(right, up, -direction).Orthonormalized();
    }

    /// <summary>One sim step on the rail. Returns false once the cursor has left the ribbon.</summary>
    public bool Step(float dt)
    {
        if (Run.Done)
            return false;
        var direction = Run.Direction;
        var target = RailFrame(direction, Run.Curvature);
        float rate = Run.OnLastSegment ? ExitAttitudeRatePerS : AttitudeRatePerS;
        float blend = Mathf.Min(1f, rate * dt);
        Attitude = blend >= 1f
            ? target
            : new Basis(Attitude.GetRotationQuaternion().Slerp(target.GetRotationQuaternion(), blend));

        float cruise = CruiseSpeedMps - (ClimbSpeedPenaltyMps * direction.Y);
        Speed = Mathf.MoveToward(Speed, cruise, SpeedRateMps2 * dt);

        // The rail point trails the cursor through a short exponential, and the residual offset
        // coasts on its own velocity while both shrink at the ramped closing rate.
        float keep = Mathf.Exp(-PointSmoothingPerS * dt);
        _point = Run.Point + ((_point - Run.Point) * keep);
        _offset += _offsetVelocity * dt;
        float closing = Mathf.Min(1f, _sinceLock * OffsetRampPerS) * OffsetClosingRateMps * dt;
        _offsetVelocity = Shrink(_offsetVelocity, closing);
        _offset = Shrink(_offset, closing);
        Position = _point + _offset;
        _sinceLock += dt;

        Run.Advance(dt * Speed);
        return !Run.Done;
    }

    private static Vector3 Shrink(Vector3 v, float by)
    {
        float length = v.Length();
        return length - by <= 0f ? Vector3.Zero : v * ((length - by) / length);
    }
}
