using System;
using Godot;

namespace CSVM.Flight;

/// <summary>A zeppelin flown elsewhere: the original's guest law for a hull whose path the host
/// sends (<c>FUN_00470550</c>). The last sample is a target, dead-reckoned forward at the sent speed.
/// The hull's position and facing each decay onto it at <see cref="ChaseRatePerS"/>. No net is walked and no steer law runs. Before any sample the target
/// is the pose the hull was placed at, so it holds there. Pure state, no <c>Node</c>; the layout
/// and addresses are <c>docs/org/multiplayer-messages.md</c>'s "The zeppelin state packet".</summary>
public sealed class ZeppelinReplica
{
    /// <summary>The rate both chases decay at. Each step the hull keeps <c>e^(-2 dt)</c> of its
    /// position and facing, the rest from the target. Decoded as <c>FUN_0053e2e0(2 dt)</c> at
    /// <c>0x00470574</c>.</summary>
    public const float ChaseRatePerS = 2f;

    /// <summary>The host's send period, seconds (<c>FUN_0049adf0</c> re-arms its timer to now plus
    /// the float at <c>0x006032e0</c>).</summary>
    public const float SendSeconds = 0.5f;

    private Vector3 _target;
    private Vector3 _targetForward;
    private Vector3 _forward;
    private bool _anySample;
    private ushort _newest;

    /// <summary>Starts a hull at the pose it was placed at, holding there until a sample arrives.
    /// </summary>
    public ZeppelinReplica(Vector3 position, float yawRad, float pitchRad)
    {
        Reseat(position, yawRad, pitchRad);
        _target = position;
        _targetForward = _forward;
    }

    /// <summary>Where the hull is drawn this step.</summary>
    public Vector3 Position { get; private set; }

    /// <summary>Heading, radians, in <see cref="ZeppelinMotion"/>'s convention (0 = -Z).</summary>
    public float YawRad { get; private set; }

    /// <summary>Pitch, radians, read off the chased facing.</summary>
    public float PitchRad { get; private set; }

    /// <summary>The last sample's speed, which dead-reckons the target, m/s.</summary>
    public float Speed { get; private set; }

    /// <summary>Samples taken.</summary>
    public int Accepted { get; private set; }

    /// <summary>Samples dropped behind a newer one.</summary>
    public int Stale { get; private set; }

    /// <summary>The unit forward of a heading and pitch, the one <see cref="ZeppelinMotion"/> flies.
    /// </summary>
    public static Vector3 ForwardOf(float yawRad, float pitchRad) => new(
        -Mathf.Sin(yawRad) * Mathf.Cos(pitchRad),
        Mathf.Sin(pitchRad),
        -Mathf.Cos(yawRad) * Mathf.Cos(pitchRad));

    /// <summary>Takes one host sample. False, and nothing changes, for a sequence at or behind the
    /// newest taken, compared with 16-bit wrap.</summary>
    public bool Receive(ushort sequence, Vector3 position, float speed, float yawRad, float pitchRad)
    {
        if (_anySample && (short)(sequence - _newest) <= 0)
        {
            Stale++;
            return false;
        }

        _anySample = true;
        _newest = sequence;
        _target = position;
        _targetForward = ForwardOf(yawRad, pitchRad);
        Speed = speed;
        Accepted++;
        return true;
    }

    /// <summary>Moves the drawn pose to where something else left the hull, a scripted motion's
    /// last frame, so the chase resumes from there rather than jumping. The target is kept.</summary>
    public void Reseat(Vector3 position, float yawRad, float pitchRad)
    {
        Position = position;
        YawRad = Mathf.Wrap(yawRad, -Mathf.Pi, Mathf.Pi);
        PitchRad = pitchRad;
        _forward = ForwardOf(YawRad, PitchRad);
    }

    /// <summary>One step of the chase in the original's order. Position closes on the target and
    /// the facing on the sent facing. The angles are read off the facing, then the target is
    /// carried forward along it.</summary>
    public void Step(float dt)
    {
        if (dt <= 0f)
        {
            return;
        }

        float keep = Mathf.Exp(-ChaseRatePerS * dt);
        Position = (Position * keep) + (_target * (1f - keep));
        var blended = (_forward * keep) + (_targetForward * (1f - keep));
        if (blended.LengthSquared() > 1e-12f)
        {
            _forward = blended.Normalized();
        }

        PitchRad = Mathf.Asin(Math.Clamp(_forward.Y, -1f, 1f));
        if (new Vector2(_forward.X, _forward.Z).LengthSquared() > 1e-12f)
        {
            YawRad = Mathf.Atan2(-_forward.X, -_forward.Z);
        }

        _target += _forward * (Speed * dt);
    }
}
