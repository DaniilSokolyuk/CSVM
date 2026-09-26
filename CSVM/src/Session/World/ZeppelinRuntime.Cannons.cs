using System;
using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.World;

/// <summary>
/// The broadside half of <see cref="ZeppelinRuntime"/> (M4 F19): wires each cannon-bearing
/// record into a <see cref="ZeppelinBroadside"/> machine and, per sim step, resolves the
/// record's authored <c>targets</c>, gates on <c>cannon_fire_range</c> and the decoded arc,
/// plays the deploy/retract anims, and fires real <c>wep_28</c> rounds through the shared pool.
/// Detail: this module's docs/architecture.md entry.
/// </summary>
public sealed partial class ZeppelinRuntime
{
    /// <summary>The broadside ammunition id, hardcoded in the original's fire routine, looked
    /// up by name, never a record key (docs/formats/mission-entities.md "Broadside firing").</summary>
    public const string BroadsideWeaponId = "wep_28";

    private readonly AimCandidateSet _aircraftScan = new();
    private readonly HashSet<string> _warnedTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Node3D?> _targetNodes = new(StringComparer.OrdinalIgnoreCase);
    private ProjectilePool? _pool;
    private WeaponDef? _broadsideWeapon;
    private Random? _gasbagRng;
    private RandomNumberGenerator? _scatterRng;

    /// <summary>The live broadside machine, for the suite's state assertions; null for an
    /// unknown node or a record without cannons.</summary>
    public ZeppelinBroadside? BroadsideOf(string node) => Find(node)?.Broadside;

    /// <summary>The scattered world directions of the most recent volley (cleared when the
    /// next volley starts), how a suite observes the scatter without hooking the pool.</summary>
    public IReadOnlyList<Vector3> LastVolleyOf(string node) =>
        Find(node)?.LastVolleyDirs ?? (IReadOnlyList<Vector3>)Array.Empty<Vector3>();

    /// <summary>Total broadside rounds this zeppelin has fired.</summary>
    public int BroadsideShotsOf(string node) => Find(node)?.BroadsideShots ?? 0;

    /// <summary>What <c>COMPLETED_ZEPCANNONS</c> does: writes the engage flag of one zeppelin's
    /// broadside (<see cref="ZeppelinBroadside.CannonsEngaged"/>, the decoded byte <c>+0xc</c>).
    /// Every broadside starts disengaged, so a mission whose script never runs the directive
    /// never deploys a hatch. Returns false for an unknown node or one without cannons.</summary>
    public bool SetCannonsEngaged(string node, bool engaged)
    {
        if (Find(node) is not { Broadside: { } broadside } zep)
        {
            return false;
        }
        broadside.CannonsEngaged = engaged;
        Log.Info("weapons", $"zep: '{zep.Def.Node}' broadside {(engaged ? "engaged" : "disengaged")} by the mission script, targets [{string.Join(",", zep.Def.Targets)}]");
        return true;
    }

    /// <summary>Builds every cannon-bearing zeppelin's broadside machine. Idempotent. Resolves
    /// the hardcoded <c>wep_28</c> once; without it (degraded extraction) the broadside is
    /// disabled and says so. Call after <see cref="WireDamage"/> so F18's cannon pools exist,
    /// without a pool a cannon zone never dies and never thins the volley.</summary>
    public void WireCannons(ProjectilePool pool, WeaponDefs weapons)
    {
        if (_pool != null)
        {
            return;
        }
        _pool = pool;
        _broadsideWeapon = weapons.Get(BroadsideWeaponId);
        if (_broadsideWeapon == null)
        {
            Log.Info("weapons", $"zep: broadside disabled, '{BroadsideWeaponId}' not in weapons.zrd");
            return;
        }
        _gasbagRng = Rng.NewSystemRandom(Rng.Ai);
        _scatterRng = new RandomNumberGenerator { Seed = (ulong)(uint)Rng.NewIntSeed(Rng.Weapons) };
        foreach (var zep in _live)
        {
            WireBroadside(zep);
        }
    }

    // cs-name lookup under a subtree without the world index: matches the gamez name meta
    // (trimmed, turrets.md's trailing-space caveat) or the Godot node name.
    private static Node3D? FindUnder(Node3D root, string name)
    {
        Node3D? found = null;
        void Walk(Node node)
        {
            if (found != null)
            {
                return;
            }
            if (node is Node3D n3d)
            {
                string csName = n3d.HasMeta(AnimRuntime.NameMeta)
                    ? n3d.GetMeta(AnimRuntime.NameMeta).AsString().Trim()
                    : n3d.Name.ToString();
                if (csName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    found = n3d;
                    return;
                }
            }
            for (int i = 0, count = node.GetChildCount(); i < count; i++)
            {
                Walk(node.GetChild(i));
            }
        }
        Walk(root);
        return found;
    }

    private Node3D? CannonNode(LiveZeppelin zep, string name)
    {
        if (_runtime != null && ZoneNode(_runtime, zep.Host, name) is { } hit)
        {
            return hit;
        }
        return FindUnder(zep.Host, name);
    }

    // The authored anim's own duration: the max event end (start + run_time) across the def's
    // sequences. Null when no def resolves, the caller falls back to the invented constant.
    private float? DurationOf(string animName)
    {
        if (_runtime == null)
        {
            return null;
        }
        AnimDefinition? best = null;
        foreach (var candidate in _runtime.DefsFor(animName))
        {
            if (best == null || (best.Archive == null && candidate.Archive != null))
            {
                best = candidate;   // compiled def preferred, same rule as ZonePool
            }
        }
        if (best == null)
        {
            return null;
        }
        float end = 0f;
        foreach (var seq in best.Sequences)
        {
            foreach (var ev in seq.Events)
            {
                end = Mathf.Max(end, ev.StartTime + (ev.Data.Num("run_time") ?? 0f));
            }
        }
        return end > 0f ? end : null;
    }

    private void WireBroadside(LiveZeppelin zep)
    {
        var def = zep.Def;
        if (def.LeftCannons.Count + def.RightCannons.Count == 0)
        {
            return;
        }

        float Deploy(ZeppelinCannon c) =>
            DurationOf(c.DeployAnim) ?? ZeppelinBroadside.FallbackDeploySeconds;
        float Retract(ZeppelinCannon c) =>
            DurationOf(c.RetractAnim) ?? ZeppelinBroadside.FallbackDeploySeconds;
        var broadside = new ZeppelinBroadside(def, Deploy, Retract);

        // Resolve each cannon's world node (the muzzle) and its F18 zone pool (record-seeded
        // cannon_health pool or the compiled def's own, whichever the registry holds).
        var hullInverse = zep.Host.GlobalTransform.AffineInverse();
        float rightX = 0f;
        int rightSeen = 0;
        foreach (var cannon in broadside.Cannons)
        {
            var node = CannonNode(zep, cannon.Record.Node);
            if (node == null)
            {
                Log.Info("weapons", $"zep: '{def.Node}' cannon '{cannon.Record.Node}' has no world node, out of the broadside");
                continue;
            }
            zep.CannonNodes[cannon.Record.Node] = node;
            if (_runtime != null)
            {
                zep.CannonPools[cannon.Record.Node] = ExistingPool(_runtime, zep.Host, cannon.Record.Node);
            }
            if (cannon.Side == BroadsideSide.Right)
            {
                rightX += (hullInverse * node.GlobalPosition).X;
                rightSeen++;
            }
        }
        // The lateral sign, from the built model rather than convention, so a mirrored import
        // cannot fire the wrong side; the mission convention (+X starboard) is the fallback.
        if (rightSeen > 0 && rightX < 0f)
        {
            broadside.RightSign = -1f;
        }
        zep.Broadside = broadside;
        Log.Info("weapons", $"zep: '{def.Node}' broadside wired, {def.LeftCannons.Count}+{def.RightCannons.Count} cannons, {BroadsideWeaponId} {_broadsideWeapon!.Velocity ?? ProjectilePool.DefaultVelocity:0} m/s, delay {broadside.FireDelaySeconds:0.#} s, range {def.CannonFireRange ?? 0f:0} m, inaccuracy {def.CannonInaccuracyDeg ?? 0f:0.#}°, targets [{string.Join(",", def.Targets)}], disengaged until COMPLETED_ZEPCANNONS");
    }

    // One broadside step for a live, active zeppelin: resolve the authored target, gate on
    // range + arc, run the machine, play anims, fire/skip the ready cannons.
    private void StepBroadside(LiveZeppelin zep, float dt)
    {
        if (zep.Broadside is not { } broadside || _pool == null || _broadsideWeapon == null)
        {
            return;
        }
        var hullPos = zep.Host.GlobalPosition;
        // Disengaged (the shipped default), the original never reaches its fire routine, so
        // there is no target to resolve either.
        var target = broadside.CannonsEngaged ? ResolveTarget(zep) : null;
        var side = BroadsideSide.None;
        if (target is { } t
            && zep.Def.CannonFireRange is { } range
            && hullPos.DistanceTo(t.Pos) <= range)
        {
            side = ZeppelinBroadside.TargetSide(zep.Motion.YawRad, zep.Motion.PitchRad,
                broadside.RightSign, hullPos, t.Pos);
        }

        var deploying = new List<ZeppelinBroadside.Cannon>();
        var retracting = new List<ZeppelinBroadside.Cannon>();
        var ready = new List<ZeppelinBroadside.Cannon>();
        bool CannonAlive(string node) =>
            zep.CannonNodes.ContainsKey(node)
            && ZoneIsAlive(zep.CannonPools.TryGetValue(node, out var pool) ? pool : null);
        broadside.Step(dt, side, CannonAlive, deploying, retracting, ready);

        foreach (var cannon in deploying)
        {
            Log.Info("weapons", $"zep: '{zep.Def.Node}' cannon '{cannon.Record.Node}' deploys ({cannon.DeploySeconds:0.#} s)");
            _runtime?.PlayWithin(zep.Host, cannon.Record.DeployAnim, applyReset: false);
        }
        foreach (var cannon in retracting)
        {
            Log.Info("weapons", $"zep: '{zep.Def.Node}' cannon '{cannon.Record.Node}' retracts");
            _runtime?.PlayWithin(zep.Host, cannon.Record.RetractAnim, applyReset: false);
        }

        if (ready.Count == 0 || target is not { } tgt || side == BroadsideSide.None)
        {
            return;
        }
        var hullVel = zep.Motion.Forward * zep.Motion.Speed;
        float speed = _broadsideWeapon.Velocity ?? ProjectilePool.DefaultVelocity;
        var sideNormal = ZeppelinBroadside.SideNormal(zep.Motion.YawRad, zep.Motion.PitchRad,
            side, broadside.RightSign);
        zep.LastVolleyDirs.Clear();
        int fired = 0;
        int skipped = 0;
        foreach (var cannon in ready)
        {
            var muzzle = zep.CannonNodes[cannon.Record.Node];
            var aimPos = tgt.Pos;
            var aimVel = tgt.Vel;
            if (tgt.Zep is { } targetZep)
            {
                // The decoded zeppelin-vs-zeppelin arm: collect the TARGET's live gasbags
                // inside the firing arc and pick one with the seeded rng.
                var bags = InArcGasbags(zep, targetZep, hullPos, sideNormal);
                int pick = ZeppelinBroadside.PickGasbag(bags.Count, _gasbagRng!);
                if (pick < 0)
                {
                    skipped++;
                    continue;   // no in-arc gasbag left to aim at, nothing decoded says fall back
                }
                aimPos = bags[pick].GlobalPosition;
            }
            if (!ZeppelinBroadside.TryAim(muzzle.GlobalPosition, speed, aimPos, aimVel, hullVel,
                    out var aim))
            {
                skipped++;   // decoded: no intercept solution, the target is skipped
                continue;    // the re-fire timer is NOT armed; the cannon retries next step
            }
            var dir = AimAssist.Scatter(aim,
                Mathf.DegToRad(zep.Def.CannonInaccuracyDeg ?? 0f), _scatterRng!);
            _pool.Spawn(_broadsideWeapon, muzzle.GlobalTransform, hullVel,
                ProjectilePool.NoShooter, muzzle, dir);
            broadside.Fired(cannon);
            zep.LastVolleyDirs.Add(dir);
            zep.BroadsideShots++;
            fired++;
        }
        if (fired > 0)
        {
            Log.Info("weapons", $"zep: '{zep.Def.Node}' broadside {side.ToString().ToLowerInvariant()}: {fired} cannon(s) fire {BroadsideWeaponId} at '{tgt.Name}' (range {hullPos.DistanceTo(tgt.Pos):0} m)");
        }
        if (skipped > 0 && zep.SkipLogged < 8)
        {
            zep.SkipLogged++;
            Log.Info("weapons", $"zep: '{zep.Def.Node}' broadside: {skipped} cannon(s) skipped '{tgt.Name}', no solution");
        }
    }

    // The record's authored targets, first live one wins (authored order, only C5/M04's
    // dantezep authors two, and the decoded routine's ordering across several is not pinned).
    // 'player' resolves to the human aircraft as the original's node lookup would; what keeps
    // every shipped campaign broadside off the player is the engage flag, never a filter here.
    private (Vector3 Pos, Vector3 Vel, string Name, LiveZeppelin? Zep)? ResolveTarget(
        LiveZeppelin zep) =>
        ZeppelinBroadside.FirstLiveTarget(zep.Def.Targets, name => ResolveOne(zep, name));

    private (Vector3 Pos, Vector3 Vel, string Name, LiveZeppelin? Zep)? ResolveOne(
        LiveZeppelin zep, string name)
    {
        if (ZeppelinBroadside.IsPlayerTarget(name))
        {
            return NearestHumanAircraft(zep.Host.GlobalPosition) is { } plane
                ? (plane.Pos, plane.Vel, name, null)
                : null;
        }
        // ⚠ Never fall a roster name through to the world-node arm below. The original binds the
        // zeppelin half of the pair once at load, so that pair only ever takes the gasbag branch;
        // a dead hull is passed over rather than aimed at by its hull position.
        if (Find(name) is { } other)
        {
            if (other.Dead)
            {
                return null;
            }
            var vel = other.Dormant
                ? Vector3.Zero
                : other.Motion.Forward * other.Motion.Speed;
            return (other.Host.GlobalPosition, vel, name, other);
        }
        // The original resolves every authored name through the general node table and fires on a
        // pair with no zeppelin behind it by reading that node's world position, so a record
        // naming a surface hull is engaged. A world node carries no velocity into the solve.
        if (TargetNode(name) is { } world
            && GodotObject.IsInstanceValid(world) && world.IsInsideTree())
        {
            return (world.GlobalPosition, Vector3.Zero, name, null);
        }
        if (_warnedTargets.Add($"{zep.Def.Node}:{name}"))
        {
            Log.Info("weapons", $"zep: '{zep.Def.Node}' target '{name}' unresolved, skipped");
        }
        return null;
    }

    // A `targets` name through the world's general node table (`FUN_004d0280(7, name)`), resolved
    // once and kept: the original resolves the whole list at load and drops a name that names no
    // node there, which is what a cached null stands for here.
    private Node3D? TargetNode(string name)
    {
        if (_targetNodes.TryGetValue(name, out var cached))
        {
            return cached;
        }
        var node = _resolveNode(name);
        _targetNodes[name] = node;
        return node;
    }

    private (Vector3 Pos, Vector3 Vel)? NearestHumanAircraft(Vector3 from)
    {
        _aircraftScan.Clear();
        _pool!.CollectAircraft(_aircraftScan);
        (Vector3 Pos, Vector3 Vel)? best = null;
        float bestDist = float.MaxValue;
        foreach (var candidate in _aircraftScan.Vehicles)
        {
            if (!candidate.Live || candidate.Source is not FlightController { IsHumanPiloted: true })
            {
                continue;
            }
            float d = from.DistanceTo(candidate.Position);
            if (d < bestDist)
            {
                bestDist = d;
                best = (candidate.Position, candidate.Velocity);
            }
        }
        return best;
    }

    // The target zeppelin's live gasbag nodes inside the firing side's arc, in healthy-list
    // order (distinct nodes, C5/M01's duplicate entry is one node, one aim point).
    private List<Node3D> InArcGasbags(LiveZeppelin shooter, LiveZeppelin target, Vector3 hullPos,
        Vector3 sideNormal)
    {
        var bags = new List<Node3D>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in target.Def.Healthy)
        {
            if (!seen.Add(zone.Node) || !target.ZoneAlive(zone.Node))
            {
                continue;
            }
            if (!target.GasbagNodes.TryGetValue(zone.Node, out var node))
            {
                node = _runtime != null
                    ? ZoneNode(_runtime, target.Host, zone.Node) ?? FindUnder(target.Host, zone.Node)
                    : FindUnder(target.Host, zone.Node);
                target.GasbagNodes[zone.Node] = node;
            }
            if (node != null && ZeppelinBroadside.InArc(hullPos, node.GlobalPosition, sideNormal))
            {
                bags.Add(node);
            }
        }
        return bags;
    }
}
