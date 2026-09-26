using System;
using System.Collections.Generic;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>Which broadside a cannon belongs to (the record's <c>left_cannons</c> /
/// <c>right_cannons</c> lists), or none, the "no side bears" answer of
/// <see cref="ZeppelinBroadside.TargetSide"/>.</summary>
public enum BroadsideSide
{
    None = -1,
    Left = 0,
    Right = 1,
}

/// <summary>The decoded per-cannon state machine: stowed → deploy → ready → fire (with the
/// re-fire timer holding the cannon in ready between volleys). Retracting is the return leg the
/// record's <c>retractAnim</c> plays; cannons mid-deploy or mid-retract are skipped entirely
/// (decoded).</summary>
public enum ZeppelinCannonState
{
    Stowed,
    Deploying,
    Ready,
    Retracting,
}

/// <summary>The zeppelin broadside law (M4 F19), pure and engine-free (docs/architecture.md): the
/// script-owned engage flag (<see cref="CannonsEngaged"/>, off until <c>COMPLETED_ZEPCANNONS</c>),
/// the decoded 90° firing arc, the per-cannon stowed → deploy → ready → fire machine with its own
/// re-fire timer, and the zeppelin-vs-zeppelin gasbag pick. Hit resolution is BALLISTIC: <see
/// cref="TryAim"/> is a lead/intercept solve, and a target with no solution is SKIPPED, never
/// rolled against. <see cref="StowAfterIdleSeconds"/> and the two fallback timings are invented,
/// named as such; no shipped record reaches them.</summary>
public sealed class ZeppelinBroadside
{
    /// <summary>The decoded arc gate: <c>dot(toTarget, sideNormal) &gt; 0.707</c>, a 45°
    /// half-angle on the firing side's perpendicular.</summary>
    public const float ArcCos = 0.707f;

    /// <summary>INVENTED: seconds a ready cannon waits with no target bearing on its side
    /// before retracting. The decode names deploy-instead-of-fire for a stowed cannon but no
    /// stow trigger.</summary>
    public const float StowAfterIdleSeconds = 10f;

    /// <summary>INVENTED fallback deploy/retract time for a cannon whose authored anim resolves
    /// to no definition; the value matches the shipped deploy defs' longest authored
    /// <c>run_time</c> (4 s). Unused when the anim resolves.</summary>
    public const float FallbackDeploySeconds = 4f;

    /// <summary>INVENTED fallback re-fire delay for a record authoring cannons but no
    /// <c>cannon_fire_delay</c>; no shipped record does (all 48 cannon-bearing records author
    /// 10/15/20 s).</summary>
    public const float FallbackFireDelaySeconds = 20f;

    /// <summary>The <c>targets</c> name standing for the human aircraft. The original resolves it
    /// through the same world-node table as any other name, a zeppelin's or a surface hull's, but
    /// no shipped script ever engages the cannons of a record naming it, so the arm is unreachable
    /// in the campaign (docs/formats/mission-entities.md "Broadside firing").</summary>
    public const string PlayerTarget = "player";

    public ZeppelinBroadside(ZeppelinDef def,
        Func<ZeppelinCannon, float>? deploySeconds = null,
        Func<ZeppelinCannon, float>? retractSeconds = null)
    {
        Def = def;
        FireDelaySeconds = def.CannonFireDelay ?? FallbackFireDelaySeconds;
        var cannons = new List<Cannon>(def.LeftCannons.Count + def.RightCannons.Count);
        foreach (var (list, side) in new[]
                 {
                     (def.LeftCannons, BroadsideSide.Left),
                     (def.RightCannons, BroadsideSide.Right),
                 })
        {
            foreach (var record in list)
            {
                cannons.Add(new Cannon(record, side,
                    deploySeconds?.Invoke(record) ?? FallbackDeploySeconds,
                    retractSeconds?.Invoke(record) ?? FallbackDeploySeconds));
            }
        }
        Cannons = cannons;
    }

    public ZeppelinDef Def { get; }

    /// <summary>The record's <c>cannon_fire_delay</c>, per CANNON, not per zeppelin
    /// (decoded: each cannon sets its own next-fire time to now + delay).</summary>
    public float FireDelaySeconds { get; }

    /// <summary>All cannons, left side first, in record order.</summary>
    public IReadOnlyList<Cannon> Cannons { get; }

    /// <summary>The decoded zeppelin byte <c>+0xc</c>: zero at construction, written only by the
    /// objective script's <c>COMPLETED_ZEPCANNONS</c>. While clear the per-frame pass never
    /// reaches the fire routine (so nothing deploys or fires) and a ready cannon is retracted
    /// outright; the record's <c>targets</c> list is inert until a script sets it.</summary>
    public bool CannonsEngaged { get; set; }

    /// <summary>The sign of the hull-local X axis that points out of the RIGHT broadside. +1
    /// by the mission convention (yaw 0 = −Z forward, +X starboard); the runtime re-derives it
    /// from the cannon nodes' built positions in case a model imports mirrored.</summary>
    public float RightSign { get; set; } = 1f;

    /// <summary>The firing side's perpendicular in world space: a ±1 unit vector along the
    /// hull's lateral axis by the cannon's side flag, rotated into world by the MOVING hull's
    /// yaw/pitch (zeppelins never bank), the decoded construction.</summary>
    public static Vector3 SideNormal(float yawRad, float pitchRad, BroadsideSide side,
        float rightSign = 1f)
    {
        float x = side == BroadsideSide.Right ? rightSign : -rightSign;
        return Basis.FromEuler(new Vector3(pitchRad, yawRad, 0f)) * new Vector3(x, 0f, 0f);
    }

    /// <summary>The decoded arc test: unit direction to the target against the side normal,
    /// <c>dot &gt; 0.707</c>. A zero separation is out of arc.</summary>
    public static bool InArc(Vector3 hullPos, Vector3 targetPos, Vector3 sideNormal)
    {
        var to = targetPos - hullPos;
        if (to.LengthSquared() < 1e-6f)
        {
            return false;
        }
        return to.Normalized().Dot(sideNormal) > ArcCos;
    }

    /// <summary>Which side's arc holds the target, or <see cref="BroadsideSide.None"/>. The two
    /// cones sit on opposite normals, so at most one side ever answers.</summary>
    public static BroadsideSide TargetSide(float yawRad, float pitchRad, float rightSign,
        Vector3 hullPos, Vector3 targetPos)
    {
        if (InArc(hullPos, targetPos, SideNormal(yawRad, pitchRad, BroadsideSide.Right, rightSign)))
        {
            return BroadsideSide.Right;
        }
        if (InArc(hullPos, targetPos, SideNormal(yawRad, pitchRad, BroadsideSide.Left, rightSign)))
        {
            return BroadsideSide.Left;
        }
        return BroadsideSide.None;
    }

    /// <summary>The decoded ballistic solve: a constant-velocity intercept
    /// (<see cref="AimAssist.TryIntercept"/>, consumed never re-derived) from the cannon's
    /// muzzle at the round's speed, against the target's velocity relative to the hull. False =
    /// no solution = the target is SKIPPED, the shipped engine never falls back to a straight
    /// shot and never rolls a hit chance.</summary>
    public static bool TryAim(Vector3 muzzlePos, float roundSpeed, Vector3 targetPos,
        Vector3 targetVel, Vector3 platformVel, out Vector3 aimDir) =>
        AimAssist.TryIntercept(muzzlePos, roundSpeed, targetPos, targetVel - platformVel,
            out aimDir, out _);

    public static bool IsPlayerTarget(string name) =>
        name.Equals(PlayerTarget, StringComparison.OrdinalIgnoreCase);

    /// <summary>The decoded candidate walk: the authored <c>targets</c> list in authored order,
    /// the first name whose <paramref name="resolve"/> yields a live target wins, a name that
    /// yields none is skipped. No team, side or hostility read exists in the original's chain;
    /// the gate that keeps the shipped campaign's broadsides off the player is
    /// <see cref="CannonsEngaged"/>, not a filter on this walk.</summary>
    public static T? FirstLiveTarget<T>(IReadOnlyList<string> targets, Func<string, T?> resolve)
        where T : struct
    {
        foreach (var name in targets)
        {
            if (resolve(name) is { } hit)
            {
                return hit;
            }
        }
        return null;
    }

    /// <summary>The zeppelin-vs-zeppelin pick: one index into the already-filtered list of the
    /// TARGET zeppelin's in-arc live gasbags, drawn from the seeded rng (the engine's
    /// <c>rand()</c>). −1 when none is in arc.</summary>
    public static int PickGasbag(int inArcCount, Random rng) =>
        inArcCount <= 0 ? -1 : rng.Next(inArcCount);

    /// <summary>One step of every cannon's machine. <paramref name="targetSide"/> is where the
    /// current target bears (ignored while <see cref="CannonsEngaged"/> is clear); <paramref
    /// name="cannonAlive"/> is F18's zone view, dropping a destroyed cannon from the volley. The
    /// caller plays the anims for <paramref name="deploying"/>/<paramref name="retracting"/> and
    /// fires each <paramref name="readyToFire"/> cannon, calling <see cref="Fired"/> on an
    /// actual launch.</summary>
    public void Step(float dt, BroadsideSide targetSide, Func<string, bool> cannonAlive,
        List<Cannon>? deploying = null, List<Cannon>? retracting = null,
        List<Cannon>? readyToFire = null)
    {
        if (!CannonsEngaged)
        {
            targetSide = BroadsideSide.None;
        }
        foreach (var cannon in Cannons)
        {
            cannon.RefireIn = Math.Max(0f, cannon.RefireIn - dt);
            if (!cannonAlive(cannon.Record.Node))
            {
                continue;   // destroyed (F18): out of the volley, and no anim ever plays again
            }
            if (!CannonsEngaged && cannon.State == ZeppelinCannonState.Ready)
            {
                // Disengaged, the original's pass retracts every ready cannon at once (decoded).
                cannon.State = ZeppelinCannonState.Retracting;
                cannon.StateLeft = cannon.RetractSeconds;
                retracting?.Add(cannon);
                continue;
            }
            switch (cannon.State)
            {
                case ZeppelinCannonState.Deploying:
                    cannon.StateLeft -= dt;
                    if (cannon.StateLeft <= 0f)
                    {
                        cannon.State = ZeppelinCannonState.Ready;
                        cannon.IdleFor = 0f;
                    }
                    break;   // mid-deploy: skipped entirely (decoded)
                case ZeppelinCannonState.Retracting:
                    cannon.StateLeft -= dt;
                    if (cannon.StateLeft <= 0f)
                    {
                        cannon.State = ZeppelinCannonState.Stowed;
                    }
                    break;   // mid-retract: skipped entirely (decoded)
                case ZeppelinCannonState.Stowed:
                    if (cannon.Side == targetSide)
                    {
                        // A stowed cannon triggers its deploy instead of firing (decoded).
                        cannon.State = ZeppelinCannonState.Deploying;
                        cannon.StateLeft = cannon.DeploySeconds;
                        deploying?.Add(cannon);
                    }
                    break;
                case ZeppelinCannonState.Ready:
                    if (cannon.Side == targetSide)
                    {
                        cannon.IdleFor = 0f;
                        if (cannon.RefireIn <= 0f)
                        {
                            readyToFire?.Add(cannon);
                        }
                    }
                    else
                    {
                        cannon.IdleFor += dt;
                        if (cannon.IdleFor >= StowAfterIdleSeconds)
                        {
                            cannon.State = ZeppelinCannonState.Retracting;
                            cannon.StateLeft = cannon.RetractSeconds;
                            retracting?.Add(cannon);
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>Arms the fired cannon's own re-fire timer: next fire at now +
    /// <c>cannon_fire_delay</c> (decoded, per cannon).</summary>
    public void Fired(Cannon cannon) => cannon.RefireIn = FireDelaySeconds;

    /// <summary>One broadside cannon's live state.</summary>
    public sealed class Cannon
    {
        public Cannon(ZeppelinCannon record, BroadsideSide side, float deploySeconds,
            float retractSeconds)
        {
            Record = record;
            Side = side;
            DeploySeconds = deploySeconds;
            RetractSeconds = retractSeconds;
        }

        public ZeppelinCannon Record { get; }

        public BroadsideSide Side { get; }

        /// <summary>How long the deploy anim runs, the authored def's own duration where it
        /// resolves, <see cref="FallbackDeploySeconds"/> where not.</summary>
        public float DeploySeconds { get; }

        public float RetractSeconds { get; }

        public ZeppelinCannonState State { get; internal set; } = ZeppelinCannonState.Stowed;

        /// <summary>Seconds left in the current deploy/retract leg.</summary>
        public float StateLeft { get; internal set; }

        /// <summary>Seconds until this cannon may fire again (the decoded per-cannon
        /// re-fire timer).</summary>
        public float RefireIn { get; internal set; }

        /// <summary>Seconds spent ready with no target bearing on this side, the invented
        /// stow countdown's input.</summary>
        public float IdleFor { get; internal set; }
    }
}
