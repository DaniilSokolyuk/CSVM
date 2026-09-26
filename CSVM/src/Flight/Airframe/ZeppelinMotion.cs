using System;
using CSVM.Flight.Ai;
using CSVM.Mech3;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>The kinematic zeppelin motion law (M4 F17): flies a <see cref="ZeppelinDef"/> along
/// its net through the shared <see cref="AiNetFollower"/>, forward-only along the facing, under
/// the record's own yaw/pitch/speed limits (docs/architecture.md). Pure state, no Node, no
/// flight model; the consumer writes <see cref="Position"/>/<see cref="YawRad"/>/
/// <see cref="PitchRad"/> onto the world node. <see cref="AliveEngines"/> is the seam F18's damage
/// aggregator drives, re-scaling the live limits through <see cref="EngineFactor"/>.</summary>
public sealed class ZeppelinMotion
{
    /// <summary>The steer law's ease band (FUN_004bf530/FUN_004bf620): inside 25° of error the
    /// commanded rate is <c>max_rate · (error / 25°)²</c>, so a turn eases out toward its
    /// target instead of arriving at full rate and overshooting.</summary>
    public const float EaseRad = 0.43633232f;

    // The stop-point approach ramp (FUN_004bf360): full speed until the along-forward range to an
    // armed stop point falls under 250 m, then linearly down to zero, and a hold inside the
    // follower's 30 m. Only the zeppelin follower reads a node's halt flag at all.
    private const float StopApproachM = 250f;

    // The dock glide's decay rate (FUN_004bf360 inside 30 m: pose ← node + (pose − node)·e^(−0.2·dt),
    // and the pitch decayed toward level at the same rate).
    private const float DockDecayPerS = 0.2f;

    private readonly float _maxRateYaw;      // rad/s
    private readonly float _maxRatePitch;    // rad/s
    private readonly float _accelYaw;        // rad/s²
    private readonly float _accelPitch;      // rad/s²
    private float _yawRate;
    private float _pitchRate;

    public ZeppelinMotion(ZeppelinDef def, AiNetFollower follower)
    {
        Def = def;
        Follower = follower;
        Position = def.Position;
        YawRad = Mathf.DegToRad(def.YawDeg);
        // Verbatim: the original's own initial-pitch clamp never fires (unit bug, it compares
        // the already-radian pitch against still-degree bounds), and that same degree-valued band
        // bounds the drawn pose only, so no step below clamps the pitch (mission-entities.md).
        PitchRad = Mathf.DegToRad(def.PitchDeg);
        _maxRateYaw = Mathf.DegToRad(def.MaxRateYawDeg);
        _maxRatePitch = Mathf.DegToRad(def.MaxRatePitchDeg);
        _accelYaw = Mathf.DegToRad(def.AccelYawDeg);
        _accelPitch = Mathf.DegToRad(def.AccelPitchDeg);
        TotalEngines = Math.Max(1, def.Engines.Count);
        AliveEngines = TotalEngines;
    }

    public ZeppelinDef Def { get; }

    /// <summary>B5's graph walk, shared with the aircraft consumer. Mutable on purpose:
    /// <c>SET_AI_NET</c> retargets a zeppelin at runtime, so a later mission-script layer swaps
    /// the follower rather than rebuilding the motion.</summary>
    public AiNetFollower Follower { get; set; }

    public Vector3 Position { get; private set; }

    /// <summary>Heading, radians, mission-data convention (0 = −Z; the node's Y rotation).</summary>
    public float YawRad { get; private set; }

    public float PitchRad { get; private set; }

    /// <summary>Current forward speed, m/s.</summary>
    public float Speed { get; private set; }

    /// <summary>The engine-loss denominator: the record's engine count at load (never below
    /// 1, so a record with no engines list still moves).</summary>
    public int TotalEngines { get; }

    /// <summary>The damage aggregator writes the surviving engine count here as
    /// nacelles die; every read below re-derives the scaled limits, so a mid-flight loss slows
    /// the zeppelin on its next step. Clamped to [0, <see cref="TotalEngines"/>] on use.</summary>
    public int AliveEngines { get; set; }

    /// <summary>Speed under the current engine loss: <c>sqrt(alive/total) · max_speed</c>.</summary>
    public float EffectiveMaxSpeed => EngineFactor(AliveEngines, TotalEngines) * Def.MaxSpeed;

    /// <summary>Acceleration under the current engine loss:
    /// <c>(0.8·sqrt(alive/total) + 0.2) · max_accel</c>, a 20 % floor while speed goes to
    /// zero at total loss.</summary>
    public float EffectiveMaxAccel =>
        ((0.8f * EngineFactor(AliveEngines, TotalEngines)) + 0.2f) * Def.MaxAccel;

    /// <summary>The forward unit vector of the current facing.</summary>
    public Vector3 Forward => new(
        -Mathf.Sin(YawRad) * Mathf.Cos(PitchRad),
        Mathf.Sin(PitchRad),
        -Mathf.Cos(YawRad) * Mathf.Cos(PitchRad));

    /// <summary>The decoded engine-loss curve: <c>f = sqrt(alive/total)</c>. NOT the design
    /// document's three 10/40/50 bands, those are design-era and refuted
    /// (docs/formats/mission-entities.md "Engine loss").</summary>
    public static float EngineFactor(int alive, int total)
    {
        if (total <= 0)
        {
            return 1f;
        }
        return Mathf.Sqrt(Math.Clamp(alive, 0, total) / (float)total);
    }

    /// <summary>The decoded steer law, one axis (FUN_004bf530 for pitch, FUN_004bf620 for yaw,
    /// the same code): the rate the error asks for is the full rate limit, eased quadratically
    /// inside <see cref="EaseRad"/> of the target, and the live rate moves toward it through the
    /// rate-acceleration limit. Ramps in, and settles without the overshoot a bang-bang rate
    /// under a small <c>accel_*</c> turns into a standing ±30° oscillation.</summary>
    public static float Steer(float rate, float error, float dt, float maxRate, float accel)
    {
        float desired = error < 0f ? -maxRate : maxRate;
        if (Mathf.Abs(error) < EaseRad)
        {
            float ease = error / EaseRad;
            desired *= ease * ease;
        }
        return Mathf.MoveToward(rate, desired, accel * dt);
    }

    /// <summary>Re-seats the law where a scripted motion left the hull: pose replaced, speed and
    /// both turn rates zeroed, limits and engines as they stand. The follower's own re-seat is
    /// the caller's.</summary>
    public void ResumeAt(Vector3 position, float yawRad, float pitchRad)
    {
        Position = position;
        YawRad = Mathf.Wrap(yawRad, -Mathf.Pi, Mathf.Pi);
        PitchRad = pitchRad;
        Speed = 0f;
        _yawRate = 0f;
        _pitchRate = 0f;
    }

    /// <summary>Takes a pose and speed decided elsewhere, a <see cref="ZeppelinReplica"/>'s, so every
    /// reader of this motion sees where the hull is drawn. The law does not run; turn rates are
    /// zeroed so a later <see cref="Step"/> starts from rest.</summary>
    public void Follow(Vector3 position, float yawRad, float pitchRad, float speed)
    {
        Position = position;
        YawRad = yawRad;
        PitchRad = pitchRad;
        Speed = speed;
        _yawRate = 0f;
        _pitchRate = 0f;
    }

    /// <summary>One sim step: walk the net, steer yaw and pitch at the current node through the
    /// decoded steer law (<see cref="Steer"/>), accelerate toward the (engine-scaled) max speed,
    /// and move forward along the facing.</summary>
    public void Step(float dt)
    {
        if (dt <= 0f)
        {
            return;
        }
        // No heading: the nose-aligned edge pick is the aeroplane AI's rule (FUN_00431e40), and a
        // zeppelin is not a vehicle in the original at all. It keeps the nearest-node seat.
        Follower.Update(Position);
        var to = Follower.CurrentTarget - Position;
        var flat = new Vector2(to.X, to.Z);
        // ⚠ The glide keeps running once the follower reports the hold: dropping it there froze
        // the hull on the 30 m sphere instead of settling it on the node. A follower still on its
        // SEAT is the own-node law instead (FUN_004bf500), which keeps the hull where it stands.
        if (Follower.StopsAt(Follower.CurrentIndex) && Follower.LegStartIndex >= 0
            && (Follower.Holding || to.Dot(Forward) < AiNetFollower.StopPointHoldM))
        {
            Dock(to, dt);
            return;
        }

        // Turning needs way on: the original scales both rates by speed over the AUTHORED
        // max_speed (not the engine-scaled one), so a docked or engine-dead hull holds its pose.
        float way = Def.MaxSpeed > 0f ? Speed / Def.MaxSpeed : 0f;

        // Yaw at the node. Inside a metre the bearing is noise, so the heading holds there.
        if (flat.LengthSquared() > 1f)
        {
            float desiredYaw = Mathf.Atan2(-to.X, -to.Z);
            _yawRate = Steer(_yawRate, Mathf.AngleDifference(YawRad, desiredYaw), dt,
                _maxRateYaw, _accelYaw);
            YawRad = Mathf.Wrap(YawRad + (way * _yawRate * dt), -Mathf.Pi, Mathf.Pi);
        }

        // Pitch at the node's altitude, the raw slope from here to it: the record's ±30 band
        // never bounds it (constructor). A station-keeping hull asks for level instead
        // (FUN_004bf500), through this same speed factor, so at a stop the ask does nothing.
        float desiredPitch = Follower.Holding ? 0f : Mathf.Atan2(to.Y, flat.Length());
        _pitchRate = Steer(_pitchRate, Mathf.AngleDifference(PitchRad, desiredPitch), dt,
            _maxRatePitch, _accelPitch);
        PitchRad = Mathf.Wrap(PitchRad + (way * _pitchRate * dt), -Mathf.Pi, Mathf.Pi);

        // Speed toward the engine-scaled maximum, at the engine-scaled acceleration.
        Speed = Mathf.MoveToward(Speed, TargetSpeed(to), EffectiveMaxAccel * dt);
        Position += Forward * (Speed * dt);
    }

    // FUN_004bf360's inside-30 m branch: within the hold distance of a halting node ahead the
    // throttle is cut and the hull, its heading and its pitch decay onto the node, the leg's own
    // bearing and level at DockDecayPerS. This is what carries the hull the rest of the way onto
    // the node, in altitude as well as plan, rather than leaving it stopped a hold distance short.
    private void Dock(Vector3 toNode, float dt)
    {
        float keep = Mathf.Exp(-DockDecayPerS * dt);
        var leg = Follower.CurrentTarget - (Follower.LegStart ?? Position);
        if (new Vector2(leg.X, leg.Z).LengthSquared() > 1f)
        {
            float bearing = Mathf.Atan2(-leg.X, -leg.Z);
            YawRad = Mathf.Wrap(bearing + (Mathf.AngleDifference(bearing, YawRad) * keep),
                -Mathf.Pi, Mathf.Pi);
        }

        // ⚠ Do not move this decay onto the speed-scaled steer law; the original levels a docking
        // hull outside the speed factor, which is the only reason a parked airship reads level at
        // all. The station-keep law's own ask for level is inert once the throttle is cut.
        PitchRad = Mathf.Wrap(PitchRad, -Mathf.Pi, Mathf.Pi) * keep;
        Position = Follower.CurrentTarget - (toNode * keep);
        _yawRate = 0f;
        _pitchRate = 0f;
        Speed = Mathf.MoveToward(Speed, 0f, EffectiveMaxAccel * dt);
        Position += Forward * (Speed * dt);
    }

    // What the throttle asks for this step: the engine-scaled maximum, or the stop-point ramp
    // when the node ahead halts. The range measured is ALONG the facing, not the straight-line
    // distance, so a zeppelin that has overshot its stop point is already at zero.
    private float TargetSpeed(Vector3 toNode)
    {
        if (Follower.Holding)
        {
            return 0f;
        }
        if (!Follower.StopsAt(Follower.CurrentIndex))
        {
            return EffectiveMaxSpeed;
        }
        float along = toNode.Dot(Forward);
        if (along >= StopApproachM)
        {
            return EffectiveMaxSpeed;
        }
        return along <= AiNetFollower.StopPointHoldM
            ? 0f : EffectiveMaxSpeed * (along / StopApproachM);
    }
}
