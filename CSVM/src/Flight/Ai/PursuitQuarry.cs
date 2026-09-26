using CSVM.Flight.Airframe;
using Godot;

namespace CSVM.Flight.Ai;

/// <summary>The AI flight law's view of its standing target for one sim step, whatever the
/// target's class: an aircraft, a turret or a world structure such as a zeppelin part. The
/// original's pursue reads its victim through the <c>Target</c> vtable (position, velocity) and
/// casts to <c>TargetVehicle</c> only for the arms that need an aeroplane, so a netted pilot
/// whose sweep picked a gasbag engine flies at it exactly as it would at a fighter
/// (<c>FUN_0041d9f0</c>, docs/org/aiPilot.md "Target acquisition"). <see cref="Of"/> is the one
/// place a <see cref="AiGunner.Target"/> becomes this shape.</summary>
public readonly struct PursuitQuarry
{
    /// <summary>World position.</summary>
    public Vector3 Position { get; init; }

    /// <summary>World velocity, m/s: the hull's for a zeppelin part, zero for scenery.</summary>
    public Vector3 Velocity { get; init; }

    /// <summary>The nose axis of an aircraft quarry, or zero for anything else, which is what
    /// makes the aspect test and the lead offset collapse to "aim at the position".</summary>
    public Vector3 Nose { get; init; }

    /// <summary>Whether the quarry is an aeroplane: the only class the merge rule and the
    /// sixth-sense trigger read (the original's <c>TargetVehicle</c> cast).</summary>
    public bool IsAircraft { get; init; }

    /// <summary>Whether the quarry is on the original's vehicle list, an aeroplane or a hull: the
    /// <c>TargetVehicle</c> cast the pursuit dwell's 20 s and 15 s holds test, which a turret or a
    /// structure fails (docs/org/aiPilot.md).</summary>
    public bool IsVehicle { get; init; }

    /// <summary>Whether the quarry is the pursuer's assigned <c>primary_target</c>, which lifts the
    /// dwell refusal and skips the whole pursuit revert (<see cref="AiGunner.IsPrimaryTarget"/>).</summary>
    public bool IsPrimaryTarget { get; init; }

    /// <summary>A human at the quarry's controls, for the lay-off assist.</summary>
    public bool IsHumanPiloted { get; init; }

    /// <summary>An AI aircraft quarry's own mode, for the sixth-sense trigger; null otherwise.</summary>
    public AiMode? Mode { get; init; }

    /// <summary>Whether the quarry is a zeppelin gasbag, which decides the ordnance class the
    /// rocketeer may launch at it.</summary>
    public bool IsGasbag { get; init; }

    /// <summary>The live target this snapshot was taken from, or null for a target that is not
    /// in play this step (dead, out of the tree, dormant), in which case the pilot has no quarry.
    /// <paramref name="gunner"/> is the pursuer's own, read for its assigned target alone.</summary>
    public static PursuitQuarry? Of(object? target, AiGunner? gunner = null)
    {
        if (!FlightController.TryTargetGeometry(target, out var position, out var velocity,
                out var forward, out bool live) || !live)
        {
            return null;
        }
        var aircraft = target as FlightController;
        return new PursuitQuarry
        {
            Position = position,
            Velocity = velocity,
            Nose = aircraft != null ? forward : Vector3.Zero,
            IsAircraft = aircraft != null,
            IsVehicle = aircraft != null || target is SurfaceVehicle,
            IsPrimaryTarget = gunner?.IsPrimaryTarget(target) ?? false,
            IsHumanPiloted = aircraft?.IsHumanPiloted ?? false,
            Mode = aircraft?.Pilot?.Machine?.Mode,
            IsGasbag = target is Mech3.DestructibleRegistry.Instance { Gasbag: true },
        };
    }
}
