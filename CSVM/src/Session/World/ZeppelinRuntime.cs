using System;
using System.Collections.Generic;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.World;

/// <summary>Runs a mission's zeppelins (M4 F17 motion + F18 damage, behind <c>--zeppelins</c>):
/// each <see cref="ZeppelinDef"/> whose world node and net resolve gets a
/// <see cref="ZeppelinMotion"/> on B5's <see cref="AiNetFollower"/>, placed at its authored pose,
/// and the node is flown kinematically, no <c>FlightController</c>. A hull an animation motion
/// owns (a start anim's SI script) is neither placed nor flown until that motion ends; the
/// follower then resumes from the pose the script left. Every place/skip/hold/park and node
/// capture prints a <c>zep:</c> line. <see cref="WireDamage"/> builds F18's per-part scalar pools
/// from the record where authored, else the compiled def; a zone with neither is not damageable.
/// <see cref="ZeppelinDamage"/> owns the kill and drives <see cref="ZeppelinEnginesDisabled"/>,
/// Instant Action's own <c>zeppelin_run</c> win; <see cref="GateWeaponDamage"/> gates
/// <c>DAMAGES_ZEPPELIN</c> on gasbag zones only. ⚠ Deliberately unwired knowledge is named at
/// its own site (this module's docs/architecture.md entry).</summary>
public sealed partial class ZeppelinRuntime : Node
{
    /// <summary>The zeppelin's own floor on <see cref="AiNetFollower"/>'s decoded arrival
    /// radius. Set below the shortest shipped leg, 143.9 m (C4/M04's M4Piratezep), so a short
    /// leg is flown rather than skipped at once. The census's sharpest turn, 67.7° on a 205 m
    /// leg against a 343.8 m turn circle, still breaks out rather than orbiting
    /// (<c>ZeppelinsTests.TheWorstShippedTurnBreaksOutRatherThanOrbits</c>).</summary>
    public const float ArrivalFloorM = 50f;

    private readonly List<LiveZeppelin> _live = new();

    // The world's own name-to-node lookup, the general table the original resolves a record's
    // node and every `targets` name through. Kept so the broadside can aim at a name that is no
    // zeppelin, and so nothing here builds a second index of its own.
    private readonly Func<string, Node3D?> _resolveNode;

    // What a mission-script net reassignment needs long after the constructor's inputs have gone
    // out of scope: the chapter's nets by name, and the trailer resolver a re-seated follower needs
    // for an anchored net to keep riding its target.
    private readonly IReadOnlyList<AiNet> _chapterNets;
    private readonly Func<AiNet, Func<Vector3?>?>? _trailerTarget;

    // The emplacements a team write fans onto; null in a build with no turret runtime, where a
    // hull simply carries no guns.
    private TurretEmplacementRuntime? _turrets;

    private AnimRuntime? _runtime;
    private Func<Node3D, bool>? _transformDriven;
    private float _sinceLog;
    private int _gateLogged;

    /// <param name="trailerTarget">An anchored net's trailer target, per net; null flies every
    /// route at its authored coordinates.</param>
    /// <param name="transformDriven">Whether an animation motion owns a node's transform channel
    /// (<c>MotionSet.DrivesTransform</c>); null until <see cref="WireDamage"/> adopts it.</param>
    /// <param name="teamOverride">--zep='s side. ⚠ Never stamp it on the part pools instead.</param>
    /// <param name="seatOverride">--zep='s seat. ⚠ Never place the node instead.</param>
    public ZeppelinRuntime(IReadOnlyList<ZeppelinDef> defs, Func<string, Node3D?> resolveNode,
        IReadOnlyList<AiNet> chapterNets, Func<AiNet, Func<Vector3?>?>? trailerTarget = null,
        Func<Node3D, bool>? transformDriven = null, int? teamOverride = null,
        Vector3? seatOverride = null)
    {
        Name = "zeppelins";
        _resolveNode = resolveNode;
        _chapterNets = chapterNets;
        _trailerTarget = trailerTarget;
        _transformDriven = transformDriven;
        foreach (var def in defs)
        {
            var host = resolveNode(def.Node);
            if (host == null)
            {
                Log.Info("flight", $"zep: '{def.Node}' skipped: world node unresolved");
                continue;
            }
            // ⚠ Do not drop this: a record is itself the hull's activation, so without it C2's
            // Pandora is invisible under its own live docking hook (docs/formats/gamez.md).
            // Dormancy is opacity, so a `deactivated` record may be activated here too.
            if (!host.Visible)
            {
                Log.Info("flight", $"zep: '{def.Node}' hull switched on, its node was built inactive");
            }

            AnimRuntime.SetSubtreeActive(host, true);
            var net = AiNets.ByName(chapterNets, def.Net);
            if (net == null)
            {
                // Still placed: the authored pose is real even without a route (and B6's
                // generator altitude gate reads the node's live Y).
                if (!Driven(host))
                    Place(host, def.Position, Mathf.DegToRad(def.YawDeg), Mathf.DegToRad(def.PitchDeg));
                Log.Info("flight", $"zep: '{def.Node}' placed but held: net '{def.Net}' not in neindex");
                continue;
            }
            // A zeppelin flies its net through ZeppelinMotion, not the aeroplane executor whose
            // along-leg test is decoded, so it keeps its own floor on the radius (ArrivalFloorM).
            var follower = new AiNetFollower(net, Rng.NewSystemRandom(Rng.Ai), ArrivalFloorM,
                trailerTarget?.Invoke(net), observesStopPoints: true);
            var motion = new ZeppelinMotion(def, follower);
            if (seatOverride is { } seat)
            {
                motion.ResumeAt(seat, Mathf.DegToRad(def.YawDeg), Mathf.DegToRad(def.PitchDeg));
            }
            var zep = new LiveZeppelin(def, motion, host, teamOverride);
            // A start anim's SI script already owns the hull's pose at bootstrap: the follower
            // parks and the record seat is where the script ends, not where the hull starts.
            if (Driven(host))
                Park(zep);
            else
                Place(host, motion.Position, motion.YawRad, motion.PitchRad);
            _live.Add(zep);
            // ⚠ The motion's position, not the record's: --zep= re-seats the law, so the authored
            // seat and where the hull actually stands are different points on that path.
            Log.Info("flight", $"zep: '{def.Node}' placed at ({motion.Position.X:0},{motion.Position.Y:0},{motion.Position.Z:0}) on net '{net.Name}' ({net.Nodes.Count} nodes), max_speed {def.MaxSpeed:0.#} m/s, engines {motion.TotalEngines}, team {AuthoredTeam(def)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unauthored"}{(def.Deactivated ? ", deactivated, out of the world until woken" : "")}");
        }
    }

    /// <summary>Raised once when a zeppelin's survivor count crosses the threshold, the
    /// generator runtime disables the dead host's generator off this (decoded rule).</summary>
    public event Action<string>? ZeppelinKilled;

    /// <summary>Raised once when a zeppelin's last live engine dies, Instant Action's own win
    /// signal on <c>zeppelin_run</c>, matching the original's empty-engine-vector test
    /// (<c>FUN_0045b9d0</c>; addresses in docs/formats/instant-action.md's "two winning paths").
    /// ⚠ A record authoring no engines fires this on the first step, matching the original;
    /// unobservable in this install (all 58 records author 12 or 14).</summary>
    public event Action<string>? ZeppelinEnginesDisabled;

    /// <summary>Zeppelins placed on a resolved net (a dormant <c>deactivated</c> one counts, it
    /// is placed and flies once a script layer wakes it).</summary>
    public int LiveCount => _live.Count;

    /// <summary>The record's authored team as an engine team id, or null on the 42 of 58 records
    /// authoring none. The parser's three names mint ids in the one shared team space,
    /// <c>ally</c> 1, <c>neutral</c> 0, <c>enemy</c> the first enemy index, 2, and a bare integer
    /// is the runtime id verbatim (docs/org/targeting.md "Zeppelins carry a record override").
    /// ⚠ Never substitute a value for an unauthored record: its parts fall through to
    /// <see cref="AimAssist.NeutralTeam"/>, the original's own rule.</summary>
    public static int? AuthoredTeam(ZeppelinDef def) =>
        def.TeamId ?? (def.Team?.ToLowerInvariant() switch
        {
            "enemy" => TurretDef.DefaultTeamId,
            "ally" => AimAssist.PlayerTeam,
            "neutral" => AimAssist.NeutralTeam,
            _ => (int?)null,
        });

    /// <summary>Appends every placed zeppelin's host node, the drawn hull the cloud band's
    /// per-object zone gate (<see cref="Objectives.ObjectZoneGate"/>) reads an altitude span off.</summary>
    public void CollectHosts(List<Node3D> into)
    {
        foreach (var live in _live)
            into.Add(live.Host);
    }

    /// <summary>Completes the record-team fan onto the guns standing on each airship, which is the
    /// rest of what <c>FUN_004bee80</c> writes. Call once the emplacements exist; a record authoring
    /// no team leaves its guns on their own <c>TURRET</c> default, since substituting one there is
    /// the same invention <see cref="AuthoredTeam"/> refuses. Returns how many gunners moved.</summary>
    public int FanTeamsOntoTurrets(TurretEmplacementRuntime turrets)
    {
        // Adopted here because the caller holds them, not so a later team write can work: that one
        // finds them for itself, and neither fan depends on the other having run.
        _turrets = turrets;
        int moved = 0;
        foreach (var zep in _live)
        {
            if (zep.Team is not { } team)
            {
                continue;
            }
            int changed = turrets.SetTeamUnder(zep.Host, team);
            moved += changed;
            Log.Info("flight", $"zep: '{zep.Def.Node}' team {team} onto {changed} turret(s)");
        }
        return moved;
    }

    /// <summary>The live motions by node name; damage writes
    /// <see cref="ZeppelinMotion.AliveEngines"/>).</summary>
    public ZeppelinMotion? MotionFor(string node) => Find(node)?.Motion;

    /// <summary>Whether this zeppelin's kill has fired (false for an unknown node).</summary>
    public bool IsDead(string node) => Find(node)?.Dead ?? false;

    /// <summary>Instant Action's builder holds a zeppelin it has switched off (F12): the record
    /// stays placed, but motion/broadside/damage poll stop here. CSVM's stand-in for the
    /// original's object-graph teardown, which has no equivalent here, see this module's entry
    /// in docs/architecture.md. Switching the node off is the caller's own act.
    /// Returns whether the name is a zeppelin of this mission; one-way, like the original.</summary>
    public bool Hold(string node)
    {
        if (Find(node) is not { } zep)
        {
            return false;
        }
        zep.Held = true;
        return true;
    }

    /// <summary>Whether this zeppelin is still out of the world on its record's own
    /// <c>deactivated</c> (false for an unknown node).</summary>
    public bool IsDormant(string node) => Find(node)?.Dormant ?? false;

    /// <summary>`WAKEUP_ENEMIES` on a zeppelin: the mission script puts a <c>deactivated</c> record
    /// into play, its motion runs, its parts become targets and damageable, and the hull comes
    /// back to full opacity, which is where a mission's own reveal animation then fades it in from.
    /// Returns whether the name is a dormant zeppelin of this mission; already-woken and unknown
    /// names both answer false, so the caller can report an unconsumed directive.</summary>
    public bool Wake(string node)
    {
        if (Find(node) is not { Dormant: true } zep)
        {
            return false;
        }
        zep.Dormant = false;
        SetDormancy(zep, false);
        Log.Info("flight", $"zep: '{zep.Def.Node}' woken by the mission script, motion, damage and targeting live");
        return true;
    }

    /// <summary>What <c>COMPLETED_STOPPOINT</c> does: arms or disarms one stop point of the net
    /// <paramref name="netName"/> names, for every zeppelin flying it. Returns how many followers
    /// carried the id. The original writes the flag on the shared net record rather than per
    /// vehicle, but no shipped mission puts two zeppelins on one net, so the two readings are
    /// indistinguishable in this install.</summary>
    public int SetStopPoint(string netName, int stopPointId, bool halts)
    {
        int hit = 0;
        foreach (var zep in _live)
        {
            var follower = zep.Motion.Follower;
            if (!follower.Net.Name.Equals(netName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            int node = follower.SetStopPoint(stopPointId, halts);
            if (node < 0)
            {
                Log.Info("flight", $"zep: stop point {stopPointId} of '{netName}' addresses no node, '{zep.Def.Node}' unchanged");
                continue;
            }
            hit++;
            Log.Info("flight", $"zep: '{zep.Def.Node}' stop point {stopPointId} of '{netName}' {(halts ? "armed" : "released")} at node {node}");
        }
        return hit;
    }

    /// <summary>The zeppelin arm of <c>SET_AI_NET</c> (<c>FUN_004bd7a0</c>): a fresh follower on the
    /// named net, seated from where the hull stands, so a reassignment captures the new route
    /// instead of restarting it at node 0. ⚠ Offer the seat no heading: the nose-aligned edge pick
    /// is the aeroplane AI's, and a zeppelin is not a vehicle in the original. Returns whether the
    /// airship moved: a net this chapter does not carry leaves it on its route and reads as no
    /// move, as does a name that is no zeppelin of this mission.</summary>
    public bool SetNet(string node, string netName)
    {
        if (Find(node) is not { } zep)
        {
            return false;
        }

        if (AiNets.ByName(_chapterNets, netName) is not { } net)
        {
            Log.Warn("flight", $"zep: '{zep.Def.Node}' SET_AI_NET '{netName}' is not a net this chapter carries, so it keeps '{zep.Motion.Follower.Net.Name}'");
            return false;
        }

        // The hull's live pose, not the record's seat and not the motion's own field: while a
        // scripted motion owns the transform the hull is wherever that script has put it.
        var from = zep.Host.GlobalPosition;
        var follower = new AiNetFollower(net, Rng.NewSystemRandom(Rng.Ai), ArrivalFloorM,
            _trailerTarget?.Invoke(net), observesStopPoints: true);
        follower.Update(from);
        zep.Motion.Follower = follower;
        Log.Info("flight", $"zep: '{zep.Def.Node}' onto net '{net.Name}#{net.Id}' by the mission script, seated at node {follower.CurrentIndex} of {net.Nodes.Count} from ({from.X:0},{from.Y:0},{from.Z:0})");
        return true;
    }

    /// <summary>The zeppelin arm of <c>SET_AI_TEAM</c> (<c>FUN_00469e20</c> into the record fan
    /// <c>FUN_004bee80</c>): the script's raw integer replaces the record's own team and reaches
    /// every part the record fan reaches, the gasbag, engine and cannon pools and every emplacement
    /// standing on the hull. ⚠ Fan all of it or none: a write that stopped at the pools would leave
    /// the guns shooting for the side the airship has just left. Returns whether the name is a
    /// zeppelin of this mission.</summary>
    public bool SetTeam(string node, int team)
    {
        if (Find(node) is not { } zep)
        {
            return false;
        }

        zep.Team = team;
        int pools = 0;
        foreach (var inst in ZonePools(zep))
        {
            inst.Team = team;
            pools++;
        }

        int guns = Turrets()?.SetTeamUnder(zep.Host, team) ?? 0;
        Log.Info("flight", $"zep: '{zep.Def.Node}' team {team} by the mission script, onto {pools} damage pool(s) and {guns} gun(s)");
        return true;
    }

    /// <summary>Hands every hull's path to the host: from here each is a <see cref="ZeppelinReplica"/>
    /// started where it stands, and its follower and steer law no longer run. A zeppelin is named
    /// by its index in placement order, which both ends build from the same records. Idempotent.
    /// </summary>
    public void Replicate()
    {
        foreach (var zep in _live)
        {
            zep.Replica ??= new ZeppelinReplica(zep.Motion.Position, zep.Motion.YawRad, zep.Motion.PitchRad);
        }
    }

    /// <summary>The replica of the zeppelin at <paramref name="index"/>, or null on an end that
    /// flies its own paths.</summary>
    public ZeppelinReplica? ReplicaAt(int index) =>
        index >= 0 && index < _live.Count ? _live[index].Replica : null;

    /// <summary>A host sample for the zeppelin at <paramref name="index"/>. False for an index this
    /// end did not place, an end that is not replicating, or a stale sample.</summary>
    public bool TakePath(int index, ushort sequence, Vector3 position, float speed, float yawRad, float pitchRad) =>
        ReplicaAt(index) is { } replica && replica.Receive(sequence, position, speed, yawRad, pitchRad);

    /// <summary>What the host sends for the zeppelin at <paramref name="index"/>: where its hull
    /// stands, its heading and pitch, and its speed. False for a hull out of the world, held or
    /// dead, which does not move. A scripted hull reports its script's pose at speed zero.</summary>
    public bool TryReadPath(int index, out Vector3 position, out float speed, out float yawRad, out float pitchRad)
    {
        position = default;
        speed = yawRad = pitchRad = 0f;
        if (index < 0 || index >= _live.Count || _live[index] is { Dormant: true } or { Held: true } or { Dead: true }
            || !GodotObject.IsInstanceValid(_live[index].Host))
        {
            return false;
        }

        var zep = _live[index];
        if (zep.Scripted)
        {
            var euler = zep.Host.GlobalTransform.Basis.Orthonormalized().GetEuler();
            position = zep.Host.GlobalPosition;
            (yawRad, pitchRad) = (euler.Y, euler.X);
            return true;
        }

        position = zep.Motion.Position;
        speed = zep.Motion.Speed;
        (yawRad, pitchRad) = (zep.Motion.YawRad, zep.Motion.PitchRad);
        return true;
    }

    /// <summary>Current surviving healthy-entry count, or -1 for an unknown/unwired node.</summary>
    public int SurvivorsOf(string node) =>
        Find(node) is { Damage: { } damage } zep ? damage.Survivors(zep.ZoneAlive) : -1;

    /// <summary>Appends every live zeppelin's damage zones (gasbags, engines, cannons) to
    /// <paramref name="into"/>, one candidate per part, each riding its hull so it carries the
    /// airship's own velocity rather than zero. A plain list and NOT
    /// <see cref="AimCandidateSet"/>'s <c>Structures</c>: the only channel by which a structure
    /// becomes selectable, and the pool takes it only while the pilot's selected ordnance carries
    /// <c>LOCK_ON</c>, which is what that divergence buys (<c>Flight.Weapons.TargetPool.Rebuild</c>).</summary>
    public void CollectTargetParts(List<AimCandidate> into, int team = AimAssist.WorldTeam)
    {
        foreach (var zep in _live)
        {
            if (zep.Dormant)
            {
                continue;   // not in the world yet; its script wake-up is what puts it there
            }
            var vel = zep.Motion.Forward * zep.Motion.Speed;
            int zepTeam = zep.Team ?? team;
            foreach (var inst in zep.GasbagZones.Values)
            {
                AddPart(into, inst, zepTeam, vel);
            }

            foreach (var inst in zep.EngineZones.Values)
            {
                AddPart(into, inst, zepTeam, vel);
            }

            foreach (var cannon in zep.CannonZones)
            {
                AddPart(into, cannon.Instance, zepTeam, vel);
            }
        }
    }

    /// <summary>Builds every zeppelin's damage zones over the world's destructible registry
    /// (the class summary's seeding rules) and starts the per-step damage poll. Idempotent.</summary>
    public void WireDamage(AnimRuntime runtime)
    {
        if (_runtime != null)
        {
            return;
        }
        _runtime = runtime;
        _transformDriven ??= runtime.Motions.DrivesTransform;
        // The gasbag exemption on the collision path, on the shared sink so every rig inherits it.
        runtime.CollideDamageGate = GateCollisionDamage;
        foreach (var zep in _live)
        {
            if (!zep.Scripted && Driven(zep.Host))
                Park(zep);
            WireZones(zep, runtime);
            if (zep.Dormant)
            {
                SetDormancy(zep, true);
            }
        }
    }

    /// <summary>The DAMAGES_ZEPPELIN routing gate (<c>ProjectilePool.WorldDamageGate</c>): a
    /// weapon without the flag cannot damage a GASBAG zone; every other target passes. The
    /// impact effect/sound still play, only the damage is refused.</summary>
    public bool GateWeaponDamage(Node? struck, WeaponDef weapon)
    {
        if (_runtime == null || ZeppelinDamage.MayDamageGasbag(weapon))
        {
            return true;
        }
        if (_runtime.Destructibles.Resolve(struck) is not { } inst)
        {
            return true;
        }
        foreach (var zep in _live)
        {
            if (zep.GasbagInstances.Contains(inst))
            {
                if (_gateLogged < 8)
                {
                    _gateLogged++;
                    Log.Info("flight", $"zep: gasbag hit by {weapon.Id} ({weapon.Name}) blocked, no DAMAGES_ZEPPELIN");
                }
                return false;
            }
        }
        return true;
    }

    /// <summary>The same gate for a COLLISION (<c>AnimRuntime.CollideDamageGate</c>): a ram cannot
    /// damage a gasbag, and every other zeppelin part passes.
    /// ⚠ A ram carries no <see cref="WeaponDef"/>, so it cannot reuse
    /// <see cref="GateWeaponDamage"/>. The original needs no second entry point: it delivers a ram
    /// as a <c>wep_24</c> weapon hit, which lacks <c>DAMAGES_ZEPPELIN</c>.</summary>
    public bool GateCollisionDamage(DestructibleRegistry.Instance inst)
    {
        foreach (var zep in _live)
        {
            if (zep.GasbagInstances.Contains(inst))
            {
                if (_gateLogged < 8)
                {
                    _gateLogged++;
                    Log.Info("flight", $"zep: gasbag RAM blocked, a collision carries no DAMAGES_ZEPPELIN");
                }
                return false;
            }
        }
        return true;
    }

    /// <summary>One step of every active motion, written onto the world nodes, then the damage
    /// poll.</summary>
    public void SimStep(float dt)
    {
        _sinceLog += dt;
        bool log = _sinceLog >= 10f;
        if (log)
        {
            _sinceLog = 0f;
        }
        foreach (var zep in _live)
        {
            if (zep.Dormant || zep.Held)
            {
                // Placed, not stepped: either waiting on the mission script's WAKEUP_ENEMIES (the
                // record's own `deactivated`, see Wake) or switched off by Instant Action's own
                // builder (F12, see Hold).
                continue;
            }
            if (!zep.Dead && Driven(zep.Host))
            {
                // One writer per transform channel: while an ObjectMotion (an SI script above
                // all) drives the hull, the follower neither steps nor writes.
                if (!zep.Scripted)
                    Park(zep);
                if (log)
                {
                    var p = zep.Host.GlobalPosition;
                    Log.Info("flight", $"zep: '{zep.Def.Node}' at ({p.X:0},{p.Y:0},{p.Z:0}) under a scripted motion, follower parked");
                }
            }
            else if (!zep.Dead)
            {
                if (zep.Scripted)
                    Resume(zep);
                int before = zep.Motion.Follower.CurrentIndex;
                if (zep.Replica is { } replica)
                {
                    // A guest's hull goes where the host's samples take it, never where its own
                    // follower would. The two ends cannot then part on a branch pick.
                    replica.Step(dt);
                    zep.Motion.Follow(replica.Position, replica.YawRad, replica.PitchRad, replica.Speed);
                }
                else
                {
                    zep.Motion.Step(dt);
                }

                Place(zep.Host, zep.Motion.Position, zep.Motion.YawRad, zep.Motion.PitchRad);
                if (zep.Motion.Follower.CurrentIndex != before && before >= 0)
                {
                    Log.Info("flight", $"zep: '{zep.Def.Node}' captured node {before}, next {zep.Motion.Follower.CurrentIndex} of '{zep.Motion.Follower.Net.Name}'");
                }
                if (log)
                {
                    var p = zep.Motion.Position;
                    Log.Info("flight", $"zep: '{zep.Def.Node}' at ({p.X:0},{p.Y:0},{p.Z:0}) speed {zep.Motion.Speed:0.#}/{zep.Motion.EffectiveMaxSpeed:0.#} m/s pitch {Mathf.RadToDeg(zep.Motion.PitchRad):0.#}° toward node {zep.Motion.Follower.CurrentIndex}{(zep.Motion.Follower.Holding ? ", holding on its stop point" : "")}");
                }
            }
            PollDamage(zep);
            if (!zep.Dead)
            {
                StepBroadside(zep, dt);   // F19: a dead zeppelin's cannons go quiet
            }
        }
    }

    // ---- the F18 damage half (static helpers first, per ordering rules) ----

    // Yaw about Y (mission convention, 0 = −Z) then pitch about X; zeppelins never bank.
    private static void Place(Node3D host, Vector3 position, float yawRad, float pitchRad)
    {
        host.GlobalTransform = new Transform3D(
            Basis.FromEuler(new Vector3(pitchRad, yawRad, 0f)), position);
        RenderPoses.Record(host);
    }

    // The follower's park: from here until the motion ends, the hull's pose is the script's.
    private static void Park(LiveZeppelin zep)
    {
        zep.Scripted = true;
        var p = zep.Host.GlobalPosition;
        Log.Info("flight", $"zep: '{zep.Def.Node}' pose owned by a scripted motion from ({p.X:0},{p.Y:0},{p.Z:0}), net follower parked");
    }

    // The follower's first write after the script: the motion restarts from the hull's live pose
    // (the script's last frame) and re-seats on the nearest net node from there, engines as they
    // stand. ⚠ Never from the record's seat: the hull is wherever the script left it.
    private static void Resume(LiveZeppelin zep)
    {
        zep.Scripted = false;
        var xform = zep.Host.GlobalTransform;
        var euler = xform.Basis.Orthonormalized().GetEuler();
        var follower = zep.Motion.Follower;
        follower.Reseat();
        zep.Motion.ResumeAt(xform.Origin, euler.Y, euler.X);
        zep.Replica?.Reseat(xform.Origin, euler.Y, euler.X);
        Log.Info("flight", $"zep: '{zep.Def.Node}' scripted motion ended, follower resumes from ({xform.Origin.X:0},{xform.Origin.Y:0},{xform.Origin.Z:0}) yaw {Mathf.RadToDeg(euler.Y):0.#}° pitch {Mathf.RadToDeg(euler.X):0.#}°, re-seating on '{follower.Net.Name}'");
    }

    // The zone-pool seeding rule: an authored record hp re-seeds an existing def pool or
    // registers a fresh one on the record's destroy-anim def; no record hp falls back to the
    // def pool alone; neither yields null (the caller logs it).
    private static DestructibleRegistry.Instance? ZonePool(AnimRuntime runtime, Node3D host,
        string nodeName, float? recordHp, string? destroyAnim)
    {
        var existing = ExistingPool(runtime, host, nodeName);
        if (recordHp is not { } hp)
        {
            return existing;
        }
        if (existing != null)
        {
            existing.Reseed(hp);
            return existing;
        }
        var node = ZoneNode(runtime, host, nodeName);
        if (node == null)
        {
            return null;
        }
        // The record's destroy anim def doubles as the pool's def, so DamageAt's zero-HP death
        // plays exactly the authored destruction (gasbag1's pzep_gasbagtorpedo1). Prefer the
        // compiled form, the same preference the registry's Resolve applies.
        AnimDefinition? poolDef = null;
        if (destroyAnim != null)
        {
            foreach (var candidate in runtime.DefsFor(destroyAnim))
            {
                if (poolDef == null || (poolDef.Archive == null && candidate.Archive != null))
                {
                    poolDef = candidate;
                }
            }
        }
        if (poolDef == null)
        {
            // The record authored hp but its anim resolves to no def (degraded extraction):
            // the pool still exists so the zone can die; the death plays nothing.
            Log.Info("flight", $"zep: zone '{nodeName}' destroy anim '{destroyAnim ?? "-"}' resolves to no def, pool registered without choreography");
            poolDef = new AnimDefinition { Name = nodeName, AnimName = $"zep_zone_{nodeName}" };
        }
        return runtime.Destructibles.Register(poolDef, node, hp);
    }

    private static DestructibleRegistry.Instance? ExistingPool(AnimRuntime runtime, Node3D host,
        string nodeName)
    {
        var node = ZoneNode(runtime, host, nodeName);
        if (node == null)
        {
            return null;
        }
        DestructibleRegistry.Instance? best = null;
        foreach (var pool in runtime.Destructibles.PoolsOn(node))
        {
            if (best == null || (best.Def.Archive == null && pool.Def.Archive != null))
            {
                best = pool;   // compiled def preferred, same rule as Resolve
            }
        }
        return best;
    }

    private static Node3D? ZoneNode(AnimRuntime runtime, Node3D host, string nodeName) =>
        runtime.FindNodes(nodeName, host) is { Count: > 0 } hits ? hits[0] : null;

    // The team the healthy entry's flagged state child carries, or null where the zone group
    // is missing, has no such child, or the child authors no owner for this mission.
    private static int? StateChildTeam(AnimRuntime runtime, Node3D host, ZeppelinHealthyZone zone) =>
        StateChild(runtime, host, zone) is { } child ? DestructibleRegistry.MissionStructureTeamOf(child) : null;

    // The node a healthy entry names, walked from the hull the way the record loader walks it
    // (gasbag1, then panels inside it); null where either step does not resolve.
    private static Node3D? StateChild(AnimRuntime runtime, Node3D host, ZeppelinHealthyZone zone) =>
        ZoneNode(runtime, host, zone.Node) is { } group ? ZoneNode(runtime, group, zone.Kind) : null;

    private static void AddPart(List<AimCandidate> into, DestructibleRegistry.Instance? inst,
        int team, Vector3 velocity)
    {
        if (inst == null || !GodotObject.IsInstanceValid(inst.Anchor) || !inst.Anchor.IsInsideTree())
        {
            return;
        }

        into.Add(new AimCandidate
        {
            Position = inst.Anchor.GlobalPosition,
            Velocity = velocity,
            Team = team,
            Live = ZoneIsAlive(inst),
            ConeOverride = AimAssist.NoConeOverride,
            Source = inst,
        });
    }

    private static bool ZoneIsAlive(DestructibleRegistry.Instance? inst) =>
        inst == null || inst.Status != DestructibleRegistry.State.Destroyed;

    // Every wired zone pool of one zeppelin, in no particular order.
    private static IEnumerable<DestructibleRegistry.Instance> ZonePools(LiveZeppelin zep)
    {
        foreach (var inst in zep.GasbagZones.Values)
        {
            if (inst != null)
            {
                yield return inst;
            }
        }

        foreach (var inst in zep.EngineZones.Values)
        {
            if (inst != null)
            {
                yield return inst;
            }
        }

        foreach (var cannon in zep.CannonZones)
        {
            yield return cannon.Instance;
        }
    }

    private bool Driven(Node3D host) => _transformDriven?.Invoke(host) ?? false;

    // The guns a team write reaches: the emplacement runtime handed over by the record fan, else
    // the one standing beside this runtime, which is where the session hangs the two. Resolved on
    // the read so a script write that arrives before the record fan still reaches the guns.
    private TurretEmplacementRuntime? Turrets()
    {
        if (_turrets == null && GetParent() is { } parent)
        {
            foreach (var sibling in parent.GetChildren())
            {
                if (sibling is TurretEmplacementRuntime found)
                {
                    _turrets = found;
                    break;
                }
            }
        }
        return _turrets;
    }

    private LiveZeppelin? Find(string node)
    {
        foreach (var zep in _live)
        {
            if (zep.Def.Node.Equals(node, StringComparison.OrdinalIgnoreCase))
            {
                return zep;
            }
        }
        return null;
    }

    // Builds one zeppelin's zone pools: gasbags (record hp where authored), engines (def
    // pools), cannon_health cannons (record hp 200 over/instead of the def pool).
    private void WireZones(LiveZeppelin zep, AnimRuntime runtime)
    {
        var def = zep.Def;
        zep.Damage = new ZeppelinDamage(def);

        // Record hp per gasbag node, where authored (57 of 58 records).
        var recordHp = new Dictionary<string, ZeppelinGasbag>(StringComparer.OrdinalIgnoreCase);
        foreach (var bag in def.Gasbags)
        {
            recordHp[bag.Node] = bag;
        }

        // The critical zones: one pool per DISTINCT healthy node (duplicate healthy entries
        // share the node's one pool and are counted per entry by the aggregator).
        foreach (var zone in def.Healthy)
        {
            if (zep.GasbagZones.ContainsKey(zone.Node))
            {
                continue;
            }
            zep.StateNodes[zone.Node] = StateChild(runtime, zep.Host, zone);
            ZeppelinGasbag? bag = recordHp.TryGetValue(zone.Node, out var b) ? b : null;
            var inst = ZonePool(runtime, zep.Host, zone.Node, bag?.Hp, bag?.DestroyAnim);
            zep.GasbagZones[zone.Node] = inst;
            if (inst != null)
            {
                // The identity the acquisition's ordnance gate, the ranking's -0.5 and the
                // rocketeer's torpedo match all read; a zone node carries no mission-structure
                // meta, so the registry cannot stamp it and the record's healthy list is the source.
                inst.Gasbag = true;
                zep.GasbagInstances.Add(inst);
                // The pool stands on the zone group, but the flagged mission structure is the
                // state child the entry names (gasbag1/panels), whose own slot is the team the
                // original builds it with (targeting.md "What a mission structure's team is").
                inst.Team ??= StateChildTeam(runtime, zep.Host, zone);
            }
            else
            {
                Log.Info("flight", $"zep: '{def.Node}' zone '{zone.Node}' has no authored hp and no destructible def, not damageable");
            }
        }

        // Engines: def-pooled destructibles only (no record key authors engine hp); an engine
        // without a pool never dies and stays in the alive count.
        int pooled = 0;
        foreach (var engine in def.Engines)
        {
            var inst = ExistingPool(runtime, zep.Host, engine);
            zep.EngineZones[engine] = inst;
            if (inst != null)
            {
                pooled++;
            }
        }
        if (pooled < def.Engines.Count)
        {
            // Instant Action's zeppelin_run is won by emptying this list, so an engine that can
            // never die makes that mode unwinnable on its own objective. Say it outright rather
            // than leaving it to be read out of the census line below.
            GD.PushWarning($"zep: '{def.Node}' has {def.Engines.Count - pooled} engine(s) with no " +
                           $"destructible pool, they can never die, so an Instant Action " +
                           $"zeppelin run on this hull cannot be won on engines");
        }

        // cannon_health cannons: record hp beats the def pool (Reseed); the record's stages
        // (0.6/0.3) play through the aggregator, mirroring the def-pooled DAMAGE_SEQUENCE path.
        foreach (var cannon in def.CannonHealth)
        {
            var inst = ZonePool(runtime, zep.Host, cannon.Cannon, cannon.Hp, cannon.DestroyAnim);
            if (inst != null)
            {
                zep.CannonZones.Add(new CannonZone(cannon, inst));
            }
        }

        // The record's team, fanned onto every part the way the original fans one value across the
        // whole airship; a record authoring none leaves the pools' own null in place.
        int fanned = 0;
        foreach (var inst in ZonePools(zep))
        {
            // Unconditional: a zone is named for itself, so the airship's own name is all a
            // rating_biases pattern naming the airship can match.
            inst.Owner = def.Node;
            if (zep.Team is { } authored)
            {
                inst.Team = authored;
                fanned++;
            }
        }

        Log.Info("flight", $"zep: '{def.Node}' damage wired, {zep.GasbagInstances.Count}/{zep.GasbagZones.Count} gasbag zones pooled, {pooled}/{def.Engines.Count} engines, {zep.CannonZones.Count}/{def.CannonHealth.Count} cannons, kill at survivors < {def.NumHealthyRequired} of {def.Healthy.Count}{(zep.Team is { } team ? Log.Format($", team {team} on {fanned} pool(s)") : ", no authored team")}");
    }

    // Out of the world, or back in it. The hull is posed at the fade alpha an
    // OBJECT_OPACITY_FROM_TO reveal starts from rather than switched off, because the same rule
    // that drops a faded subtree's colliders then applies, and because a mission's own reveal
    // animates that very parameter, writing Visible instead would leave the fade running on a
    // node the unplaced-entity poll can switch back on underneath it.
    private void SetDormancy(LiveZeppelin zep, bool dormant)
    {
        foreach (var inst in ZonePools(zep))
        {
            inst.Dormant = dormant;
        }
        _runtime?.SetSubtreeOpacity(zep.Host, dormant ? 0f : 1f);
    }

    private void PollDamage(LiveZeppelin zep)
    {
        if (zep.Damage is not { } damage)
        {
            return;
        }

        // Engine deaths update the count that drives the decoded square-root curve.
        int engines = damage.AliveEngines(
            name => zep.EngineZones.TryGetValue(name, out var e) ? ZoneIsAlive(e) : true);
        if (engines != zep.Motion.AliveEngines)
        {
            zep.Motion.AliveEngines = engines;
            Log.Info("flight", $"zep: '{zep.Def.Node}' engines {engines}/{zep.Motion.TotalEngines}, max speed now {zep.Motion.EffectiveMaxSpeed:0.#} m/s");
        }

        // ⚠ Gated on the hull, and on the record's own engine count, not
        // ZeppelinMotion.TotalEngines (floors at 1). A death sequence that empties the nacelles
        // must not raise this after the fact, see docs/architecture.md.
        if (!zep.Dead && !zep.EnginesDisabled && engines == 0)
        {
            zep.EnginesDisabled = true;
            Log.Info("flight", $"zep: '{zep.Def.Node}' ENGINES DISABLED, 0 of {zep.Def.Engines.Count} engine(s) live");
            ZeppelinEnginesDisabled?.Invoke(zep.Def.Node);
        }

        // Per-zone kill lines, once each.
        foreach (var (node, inst) in zep.GasbagZones)
        {
            if (!zep.ZoneAlive(node) && zep.DeadZones.Add(node))
            {
                string how = inst is { Status: DestructibleRegistry.State.Destroyed }
                    ? "destroyed" : "switched off by a script";
                Log.Info("flight", $"zep: '{zep.Def.Node}' gasbag '{node}' {how}, survivors {damage.Survivors(zep.ZoneAlive)}/{damage.Required} required");
            }
        }

        // Record-authored cannon stages and kill lines (def-pooled cannons stage through
        // their own DAMAGE_SEQUENCE instead).
        foreach (var cannon in zep.CannonZones)
        {
            float fraction = cannon.Instance.MaxHealth > 0f
                ? cannon.Instance.Health / cannon.Instance.MaxHealth : 0f;
            int fired = cannon.StagesFired;
            foreach (var anim in ZeppelinDamage.CrossedStages(cannon.Record.Stages, fraction, ref fired))
            {
                _runtime?.Play(anim);
            }
            cannon.StagesFired = fired;
            if (cannon.Instance.Status == DestructibleRegistry.State.Destroyed
                && zep.DeadZones.Add(cannon.Record.Cannon))
            {
                Log.Info("flight", $"zep: '{zep.Def.Node}' cannon '{cannon.Record.Cannon}' destroyed (bound gasbag '{cannon.Record.Gasbag}', binding recorded, no decoded damage transfer)");
            }
        }

        // The kill, once: survivors < num_healthy_required (the decoded polarity).
        if (!zep.Dead && damage.IsDead(zep.ZoneAlive))
        {
            zep.Dead = true;
            int survivors = damage.Survivors(zep.ZoneAlive);
            Log.Info("flight", $"zep: '{zep.Def.Node}' DESTROYED, survivors {survivors} < required {damage.Required}");
            PlayHullDeath(zep);
            ZeppelinKilled?.Invoke(zep.Def.Node);
        }
    }

    // The authored hull death: the def anchored on the zeppelin node whose activation
    // prerequisite counts anims (all_pzep_gasbags, pops the remaining bags and calls
    // killpzep). Data-selected, never a hardcoded name; a zeppelin shipping none logs so.
    // ⚠ Effect templates snap to absolute world points and never track a moving host, this
    // plays where the hull died at that instant, not where it drifts to afterward.
    private void PlayHullDeath(LiveZeppelin zep)
    {
        if (_runtime == null)
        {
            return;
        }
        foreach (var def in _runtime.ProgramDefs)
        {
            if (def.PrereqAnims.Count > 0 && def.AnimName != null
                && def.Name.Equals(zep.Def.Node, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("flight", $"zep: '{zep.Def.Node}' death plays '{def.AnimName}'");
                _runtime.Play(def.AnimName);
                return;
            }
        }
        Log.Info("flight", $"zep: '{zep.Def.Node}' ships no prerequisite-gated hull death def, kill recorded without choreography");
    }

    private sealed class LiveZeppelin
    {
        public LiveZeppelin(ZeppelinDef def, ZeppelinMotion motion, Node3D host,
            int? teamOverride = null)
        {
            Def = def;
            Motion = motion;
            Host = host;
            ZoneAlive = node => ZoneIsAlive(
                    GasbagZones.TryGetValue(node, out var inst) ? inst : null)
                && StateActive(StateNodes.TryGetValue(node, out var state) ? state : null);
            Dormant = def.Deactivated;
            Team = teamOverride ?? AuthoredTeam(def);
        }

        public ZeppelinDef Def { get; }

        /// <summary>The record's own <c>deactivated</c>, until a script wakes it: out of the world
        /// altogether rather than merely stopped, which is what <see cref="Held"/> is.</summary>
        public bool Dormant { get; set; }

        /// <summary>The record's authored team, or null on a record authoring none. Writable
        /// because <c>SET_AI_TEAM</c> replaces it mid-mission, and every read below is live.</summary>
        public int? Team { get; set; }

        /// <summary>One motion for the zeppelin's life; a scripted hand-back re-seats it in
        /// place (<see cref="ZeppelinMotion.ResumeAt"/>), engines and limits as they stand.</summary>
        public ZeppelinMotion Motion { get; }

        public Node3D Host { get; }

        /// <summary>An animation motion owns the hull's transform channel: the follower is
        /// parked and writes nothing until the motion ends (see <c>Resume</c>).</summary>
        public bool Scripted { get; set; }

        public ZeppelinDamage? Damage { get; set; }

        /// <summary>The host's path for this hull, on a guest; null where this end flies it.</summary>
        public ZeppelinReplica? Replica { get; set; }

        public bool Dead { get; set; }

        /// <summary>Whether the engines-gone signal has fired (one-way, like <see cref="Dead"/>).
        /// </summary>
        public bool EnginesDisabled { get; set; }

        /// <summary>Switched off by Instant Action's builder (F12, <see cref="Hold"/>), placed
        /// but stepped no further, the runtime counterpart of the record's own
        /// <c>deactivated</c>.</summary>
        public bool Held { get; set; }

        /// <summary>Pool per distinct healthy node; null = zone not damageable (logged).</summary>
        public Dictionary<string, DestructibleRegistry.Instance?> GasbagZones { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The gasbag pools as a set, what the DAMAGES_ZEPPELIN gate protects.</summary>
        public HashSet<DestructibleRegistry.Instance> GasbagInstances { get; } = new();

        public Dictionary<string, DestructibleRegistry.Instance?> EngineZones { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<CannonZone> CannonZones { get; } = new();

        /// <summary>Zones whose kill line has printed (one line per zone).</summary>
        public HashSet<string> DeadZones { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The F19 broadside machine; null until <c>WireCannons</c>, or for a record
        /// without cannons.</summary>
        public ZeppelinBroadside? Broadside { get; set; }

        /// <summary>Cannon node name → its world node (the muzzle). Unresolved cannons are
        /// absent and out of the broadside.</summary>
        public Dictionary<string, Node3D> CannonNodes { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Cannon node name → its F18 zone pool; a null/absent pool never dies.</summary>
        public Dictionary<string, DestructibleRegistry.Instance?> CannonPools { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gasbag aim nodes, resolved lazily when another zeppelin shoots at THIS one.</summary>
        public Dictionary<string, Node3D?> GasbagNodes { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The most recent volley's scattered fire directions (suite observability).</summary>
        public List<Vector3> LastVolleyDirs { get; } = new();

        public int BroadsideShots { get; set; }

        /// <summary>No-solution skip lines printed (rate-limited like the gate log).</summary>
        public int SkipLogged { get; set; }

        /// <summary>The node each distinct healthy entry names (gasbag1/panels), null where it
        /// does not resolve.</summary>
        public Dictionary<string, Node3D?> StateNodes { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The aggregator's zone-aliveness view: a destroyed pool, or the entry's own node
        /// switched off, which is the one flag the original's survivor walk reads.
        /// ⚠ Never the pool alone: a script that burns a gasbag switches the node off with no
        /// damage at all, and the hull then flies its net through its own crash.</summary>
        public Func<string, bool> ZoneAlive { get; }

        private static bool StateActive(Node3D? node) =>
            node == null || !GodotObject.IsInstanceValid(node) || node.Visible;
    }

    private sealed class CannonZone
    {
        public CannonZone(ZeppelinCannonHealth record, DestructibleRegistry.Instance instance)
        {
            Record = record;
            Instance = instance;
        }

        public ZeppelinCannonHealth Record { get; }

        public DestructibleRegistry.Instance Instance { get; }

        public int StagesFired { get; set; }
    }
}
