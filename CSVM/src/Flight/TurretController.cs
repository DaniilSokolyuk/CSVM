using System;
using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight;

/// <summary>Where a gunner's tick stopped this frame, the fire gates of
/// <see cref="TurretController.SimStep"/> named, in the order they are taken. Read by the
/// targeting overlay (F15) so "it is not shooting" can be answered with WHICH gate rather than
/// by guesswork; <see cref="Firing"/> means every gate passed and a round left the muzzle.</summary>
public enum TurretGate
{
    Asleep,
    Dead,
    NoTarget,
    Bored,
    NoSolution,
    Reloading,
    NoAmmo,
    Blocked,
    Slewing,
    Firing,
}

/// <summary>One <c>ai.zrd</c> turret gunner (docs/formats/turrets.md): the same tracking loop for
/// a carried turret riding an aircraft (<see cref="BuildCarried"/>) and a world emplacement placed
/// at the entry's <c>NODES</c> patterns (<see cref="BuildEmplacements"/>). Per sim tick it acquires
/// the nearest hostile vehicle inside <c>DETECTION_RANGE</c>, solves a constant-velocity intercept
/// (<see cref="AimAssist.TryIntercept"/>), clamps the solution to the authored arcs, slews the
/// barrel, writes the pose onto the <c>PARTS</c> nodes, and fires through the shared
/// <see cref="ProjectilePool"/>, hit resolution is geometric, with <c>INACCURACY</c> as a scatter
/// cone, never a probability roll. A plain class, not a Node: a carried gunner is ticked by its
/// host's <c>SimStep</c>, an emplacement by <c>Session.TurretEmplacementRuntime</c>.</summary>
public sealed class TurretController
{
    /// <summary>The barrel's catch-up rate toward the clamped aim direction, per second, the
    /// binary's slew constant. At or past 1/rate seconds per frame the turn snaps whole.</summary>
    public const float SlewRate = 3.0f;

    /// <summary>The fire gate: the barrel must be within 15° of the solved aim direction
    /// (the binary compares against cos 15°). A turret still slewing does not fire.</summary>
    public const float FireGateCos = 0.965926f;

    /// <summary>The decoded moving-platform sanity cut: a differenced position implying a
    /// platform speed past this discards the estimate for the frame.</summary>
    public const float MaxPlatformSpeed = 447f;

    /// <summary>How long one round keeps this gunner's voice sounding, seconds: the turret fire
    /// path's own literal, renewed per round. A mount whose fire interval is shorter than this
    /// never falls silent between shots (docs/formats/turrets.md, "Projectile cadence and cannon
    /// audio have separate lifetimes"). ⚠ Not a shared constant: the general vehicle path renews
    /// per tick on a lease of zero instead (docs/org/weaponFire.md).</summary>
    public const float VoiceLeaseSeconds = 0.5f;

    /// <summary>How much of an emplacement's line-of-sight ray, off its own node, is not tested:
    /// the body a hull ring stands in, which the original never counts as its own cover. Past it
    /// the same hull DOES block. Clear of a ring's own bodies, which engulf it out to about 1 m,
    /// and under the 2 m at which a hull skin stands over one. ⚠ It does not clear a ground gun's
    /// rig, which answers out to 6 m; every emplacement excludes its own site subtree by RID as
    /// well (docs/formats/turrets.md "Acquiring").</summary>
    public const float MountSkirtM = 1.5f;

    private readonly FlightController? _host;
    private readonly IWorldQuery? _worldQuery; // the carried case's line-of-sight seam; null on an emplacement
    private readonly ProjectilePool _pool;
    private readonly RandomNumberGenerator _rng;
    private readonly AimCandidateSet _scan = new(); // reused per tick; the whole VehicleList
    private readonly Transform3D _yawRest;
    private readonly Transform3D _pitchRest;
    private readonly Node3D? _healthyNode; // emplacement kill switch; null on a carried turret
    private readonly Node3D? _site;        // emplacement placement node; null on a carried turret
    private readonly Node3D? _platform;    // the hull section the emplacement is mounted on

    private int _team;           // team id: the authored TEAM or the host's, until a record fans one
    private float _windowLeft;   // s left in the current attack/bored window
    private float _fireIn;       // s until the next shot is allowed
    private int _fpNext;         // round-robin firepoint cursor
    private double _losNext;     // game-time s the cached line-of-sight verdict expires
    private bool _losBlocked;
    private Vector3 _platformVel;
    private Vector3 _lastPos;
    private bool _hasLastPos;
    private bool _firstShotLogged;
    private FlightController? _acquisitionReported; // the human this gunner has already warned about
    private Godot.Collections.Array<Rid>? _platformColliders;
    private Godot.Collections.Array<Rid>? _siteColliders;

    private TurretController(TurretDef def, WeaponDef weapon, FlightController? host,
        IWorldQuery? worldQuery, ProjectilePool pool, Node3D? yawNode, Node3D pitchNode,
        Node3D[] firepoints, RandomNumberGenerator rng, int team, bool activated,
        Node3D? healthyNode, Node3D? site, Node3D? platform, string label,
        GunVoiceHome? voices = null)
    {
        Def = def;
        Weapon = weapon;
        _host = host;
        _worldQuery = worldQuery;
        _pool = pool;
        YawNode = yawNode;
        PitchNode = pitchNode;
        Firepoints = firepoints;
        _rng = rng;
        _team = team;
        Activated = activated;
        _healthyNode = healthyNode;
        _site = site;
        _platform = platform ?? site;
        Label = label;
        _yawRest = yawNode?.Transform ?? Transform3D.Identity;
        _pitchRest = pitchNode.Transform;
        // The fire routine's own gate: the entry's SOUNDS.CANNON, and only while the mount's weapon
        // carries the CANNON flag. Five shipped entries author no SOUNDS at all and three calibres
        // are not cannon, so a silent mount here is the data's answer (docs/formats/turrets.md).
        Voice = weapon.IsCannon
            ? GunVoice.Attach(voices, def.CannonSound, label, VoiceLeaseSeconds) : null;
        Ammo = def.Ammo;
        Attacking = true;
        _windowLeft = RandRange(def.AttackMin, def.AttackMax);
        // Load pose: the centre of each arc; a free axis stays unrotated. The original poses
        // dormant emplacements too, ACTIVATED gates the tick, not the load pose.
        BarrelLocal = LocalDir(def.RestYawDeg, def.RestPitchDeg);
        ApplyNodePose();
    }

    public TurretDef Def { get; }

    /// <summary>The gunner's own weapon, from its <c>ai.zrd</c> row (docs/formats/turrets.md).
    /// ⚠ Never take it from the stock loadout's turret slot. Those slots stay bound but inert
    /// (<see cref="GunGroup.IsTurret"/>), and reading one arms the gunner with the wrong
    /// calibre.</summary>
    public WeaponDef Weapon { get; }

    /// <summary>The traverse ring (<c>PARTS[0]</c> of the 3-element form), null on the
    /// 2-element form, where <see cref="PitchNode"/> takes the combined rotation.</summary>
    public Node3D? YawNode { get; }

    public Node3D PitchNode { get; }

    public Node3D[] Firepoints { get; }

    /// <summary>This gunner's own firing voice, or null when its entry authors no cue, its calibre
    /// is not a cannon, or the session built no sound archive. One per gunner rather than one per
    /// owner, which is what the original's per-turret sound slot is.</summary>
    public GunVoice? Voice { get; }

    /// <summary>Remaining rounds, real state (the original save/restores it), effectively
    /// unlimited at the shipped 9999/12000.</summary>
    public int Ammo { get; private set; }

    public int ShotsFired { get; private set; }

    /// <summary>True in a firing spell, false in the bored pause between spells (a duty cycle, not
    /// two moods: bored suppresses firing only, the aim solution keeps running). The windows are
    /// redrawn uniform(min,max) at every transition, so no two turrets stay in phase.</summary>
    public bool Attacking { get; private set; }

    /// <summary>The decoded awake gate: a dormant emplacement neither tracks nor fires until a
    /// script wakes it. Every carried turret is built awake (all 16 ship <c>ACTIVATED 1</c>).</summary>
    public bool Activated { get; private set; }

    /// <summary>Who this gunner is in a log line, the entry's TITLE plus, for an emplacement,
    /// the world node it stands on.</summary>
    public string Label { get; }

    /// <summary>The world node this emplacement was placed at (its <c>NODES</c> match); null on
    /// a carried turret. What a subtree-scoped activation walks against, the engine's own
    /// per-node lookup of the turret standing on a node
    /// (docs/formats/turrets.md "Waking a whole subtree").</summary>
    public Node3D? Site => _site;

    /// <summary>The team the acquisition gate and the aim-assist candidate list run on: the
    /// authored <see cref="TurretDef.TeamId"/> for an emplacement, the host's team for a
    /// carried turret, and a mission record's own value once something fans one on
    /// (<see cref="SetTeam"/>).</summary>
    public int Team => _team;

    /// <summary>The platform's velocity: the host's for a carried turret, the differenced own
    /// position (the decoded moving-host estimate, <see cref="MaxPlatformSpeed"/> cut) for an
    /// emplacement, zero while static, the ride while a zeppelin-slung mount moves.</summary>
    public Vector3 PlatformVelocity => _host?.WorldVelocity ?? _platformVel;

    /// <summary>The barrel direction in the turret's base frame (the yaw node's rest frame),
    /// what the pose writes and the fire gate read.</summary>
    public Vector3 BarrelLocal { get; private set; }

    /// <summary>Where this tick stopped, for the F15 targeting overlay. Plain observation: the
    /// tick writes it and nothing reads it back, so it cannot change what the gunner does.</summary>
    public TurretGate Gate { get; private set; }

    /// <summary>The position this gunner is tracking, valid while <see cref="Gate"/> is past
    /// <see cref="TurretGate.NoTarget"/>. The acquired aircraft's own position, not the lead
    /// solution, the overlay draws the line to the TARGET and the barrel shows the lead.</summary>
    public Vector3 TargetPosition { get; private set; }

    /// <summary>What <see cref="TargetPosition"/> belongs to, for the F15 overlay's roll-call, or
    /// null with no target. Observation only, like <see cref="Gate"/>: the acquisition already
    /// decided on geometry, and nothing reads this back into the gunner.</summary>
    public object? TargetSource { get; private set; }

    /// <summary>Carried: alive while the host is <see cref="FlightController.InPlay"/>. Emplacement:
    /// alive while its <c>HEALTHY_NODE</c> (default <c>healthy</c>) is visible IN THE TREE
    /// (docs/formats/turrets.md "Being alive, and being awake"): a mission's <c>.gw</c> switches a
    /// site off at its root, and the node's own flag would read that turret as alive.
    /// ⚠ Not <c>Crashed</c>: a gunner carried by an inert airframe must not fire, be fired at, or
    /// join the aim assist's turret candidate list.</summary>
    public bool Alive => _host != null
        ? _host.InPlay
        : _healthyNode == null || (GodotObject.IsInstanceValid(_healthyNode)
            && (_healthyNode.IsInsideTree() ? _healthyNode.IsVisibleInTree() : _healthyNode.Visible));

    /// <summary>Where the turret is, for the aim assist's candidate list and the detection gate.</summary>
    public Vector3 WorldPosition => (YawNode ?? PitchNode).GlobalPosition;

    /// <summary>The barrel direction in world space, <see cref="BarrelLocal"/> through the base
    /// frame. What a viewer (and the in-engine suite) sees the gun pointing along.</summary>
    public Vector3 BarrelWorldDir => BaseBasis() * BarrelLocal;

    /// <summary>Builds the host's carried turrets: every <c>thirdp</c> mount of its vehicle
    /// def's <c>turrets</c> block, resolved by TITLE against <c>ai.zrd</c> and by node name
    /// against the built plane model. <c>firstp</c> mounts are the cockpit-view rig, which CSVM
    /// does not render, skipped on purpose. A mount whose def, weapon or nodes do not resolve
    /// is skipped with a warning, never a throw: an unarmed turret ring is a degraded plane,
    /// not a broken session. <paramref name="voices"/> left null leaves every gunner silent.</summary>
    public static TurretController[] BuildCarried(TurretDefs defs, PlaneStats stats,
        Node3D planeModel, WeaponDefs weapons, FlightController host, ProjectilePool pool,
        GunVoiceHome? voices = null)
    {
        var built = new List<TurretController>();
        var nodesByName = CollectNamedNodes(planeModel);
        foreach (var mount in stats.TurretMounts)
        {
            if (mount.FirstPerson)
            {
                continue;
            }
            var def = defs.FindByTitle(mount.Title);
            if (def == null)
            {
                GD.PushWarning($"turret: no ai.zrd entry titled '{mount.Title}' ({stats.DefName})");
                continue;
            }
            var weapon = weapons.Get(def.WeaponName);
            if (weapon == null || def.Firepoints.Count == 0)
            {
                // "An entry with no resolvable WEAPON ticks no further", the engine's own rule.
                GD.PushWarning($"turret '{mount.Title}': weapon '{def.WeaponName}' unresolved or no firepoints");
                continue;
            }
            // PARTS names resolve inside the mount's own subtree (fire_turret1, …): the same
            // names repeat on the other viewpoint's rig and on every other plane of the type.
            if (!nodesByName.TryGetValue(mount.Node, out var mountRoot))
            {
                GD.PushWarning($"turret '{mount.Title}': mount node '{mount.Node}' not on {stats.NodeName}");
                continue;
            }
            var rig = CollectNamedNodes(mountRoot);
            Node3D? yaw = null;
            if (def.YawNode is { } yawName && !rig.TryGetValue(yawName, out yaw))
            {
                GD.PushWarning($"turret '{mount.Title}': yaw node '{yawName}' not under '{mount.Node}'");
                continue;
            }
            if (!rig.TryGetValue(def.PitchNode, out var pitch))
            {
                GD.PushWarning($"turret '{mount.Title}': pitch node '{def.PitchNode}' not under '{mount.Node}'");
                continue;
            }
            var fps = new List<Node3D>();
            foreach (var fpName in def.Firepoints)
            {
                if (rig.TryGetValue(fpName, out var fp))
                {
                    fps.Add(fp);
                }
                else
                {
                    GD.PushWarning($"turret '{mount.Title}': firepoint '{fpName}' not under '{mount.Node}'");
                }
            }
            if (fps.Count == 0)
            {
                continue;
            }
            var rng = new RandomNumberGenerator { Seed = (ulong)(uint)Utils.Rng.NewIntSeed(Utils.Rng.Weapons) };
            // The same seam the host itself reads physics through: a carried gunner's line of
            // sight goes through IWorldQuery, not through a cast back to the host's own type.
            built.Add(new TurretController(def, weapon, host, new GodotWorldQuery(host), pool, yaw,
                pitch, fps.ToArray(), rng, host.Team, activated: true, healthyNode: null, site: null,
                platform: null, label: mount.Title, voices));
        }
        return built.ToArray();
    }

    /// <summary>Builds the chapter's world emplacements: every standalone <c>ai.zrd</c> entry's
    /// <c>NODES</c> patterns resolved against the built world (docs/formats/turrets.md "Field table";
    /// <paramref name="findNodes"/> is <c>AnimRuntime.FindNodes</c>). One entry instantiates as many
    /// turrets as there are matching nodes. A matched node whose <c>PARTS</c> do not resolve is
    /// skipped with a warning. <paramref name="voices"/> left null leaves every gun silent.</summary>
    public static TurretController[] BuildEmplacements(TurretDefs defs, WeaponDefs weapons,
        Func<string, Node3D?, IReadOnlyList<Node3D>> findNodes, ProjectilePool pool,
        Node3D? worldRoot = null, GunVoiceHome? voices = null)
    {
        var built = new List<TurretController>();
        foreach (var def in defs.All)
        {
            if (def.Carried || def.NodePatterns.Count == 0)
            {
                continue;
            }
            var weapon = weapons.Get(def.WeaponName);
            if (weapon == null || def.Firepoints.Count == 0)
            {
                // "An entry with no resolvable WEAPON ticks no further", the engine's own rule.
                GD.PushWarning($"turret '{def.Title}': weapon '{def.WeaponName}' unresolved or no firepoints");
                continue;
            }
            foreach (var path in def.NodePatterns)
            {
                if (path.Count == 0)
                {
                    continue;
                }
                IReadOnlyList<Node3D> matches = findNodes(path[0], null);
                for (int seg = 1; seg < path.Count; seg++)
                {
                    var next = new List<Node3D>();
                    foreach (var scope in matches)
                    {
                        next.AddRange(findNodes(path[seg], scope));
                    }
                    matches = next;
                }
                foreach (var site in matches)
                {
                    var rig = CollectNamedNodes(site);
                    string label = $"{def.Title}@{site.Name}";
                    Node3D? yaw = null;
                    if (def.YawNode is { } yawName && !rig.TryGetValue(yawName, out yaw))
                    {
                        GD.PushWarning($"turret {label}: yaw node '{yawName}' not under the site");
                        continue;
                    }
                    if (!rig.TryGetValue(def.PitchNode, out var pitch))
                    {
                        GD.PushWarning($"turret {label}: pitch node '{def.PitchNode}' not under the site");
                        continue;
                    }
                    var fps = new List<Node3D>();
                    foreach (var fpName in def.Firepoints)
                    {
                        if (rig.TryGetValue(fpName, out var fp))
                        {
                            fps.Add(fp);
                        }
                    }
                    if (fps.Count == 0)
                    {
                        GD.PushWarning($"turret {label}: no firepoint under the site");
                        continue;
                    }
                    // The kill switch: HEALTHY_NODE (default "healthy") under the site, else the
                    // site itself, the decoded fallback chain.
                    rig.TryGetValue(def.HealthyNode ?? "healthy", out var healthy);
                    healthy ??= site;
                    var rng = new RandomNumberGenerator { Seed = (ulong)(uint)Utils.Rng.NewIntSeed(Utils.Rng.Weapons) };
                    // No host, so no IWorldQuery either: an emplacement's line of sight goes
                    // through WorldRayBlocked, which skips the gun's own mounting clutter.
                    built.Add(new TurretController(def, weapon, host: null, worldQuery: null, pool,
                        yaw, pitch, fps.ToArray(), rng, def.TeamId,
                        def.Activated, healthy, site, PlatformOf(site, worldRoot), label, voices));
                }
            }
        }
        return built.ToArray();
    }

    /// <summary>The structure an emplacement is mounted on: the node its site hangs off (a
    /// zeppelin ring's own gasbag group, a balloon's canopy), or the gun's own node when the site is
    /// already a top-level world child. What a round this gun fires owns, so its own flak neither
    /// strikes nor splashes its mount (docs/formats/turrets.md "Hit resolution is geometric").
    /// ⚠ Not the line-of-sight test's business. A mounting group holds the far side of the same
    /// hull, so excluding it there is how a ring shoots through its own zeppelin.</summary>
    public static Node3D? PlatformOf(Node3D? site, Node3D? worldRoot)
    {
        if (site == null || worldRoot == null || site.GetParent() is not Node3D parent
            || parent == worldRoot)
        {
            return site;   // a gun standing on the ground: its own body is the whole platform
        }
        return parent;
    }

    /// <summary>The pitch clamp, plain, and only when the axis is limited at all: the engine
    /// clamps only when both limits exist and differ, so an absent key (and min == max) is
    /// UNRESTRICTED, never locked.</summary>
    public static float ClampPitchDeg(float deg, TurretDef def) =>
        def.PitchRestricted ? Mathf.Clamp(deg, def.PitchMinDeg!.Value, def.PitchMaxDeg!.Value) : deg;

    /// <summary>The wrap-aware yaw clamp: the arc is a DIRECTED interval ([105,255] runs through
    /// 180; [-155,-5] is a different arc). The solved angle is tried as-is and at ±360; still
    /// outside, it snaps to whichever end stop is angularly NEARER, not the shortest-path one.
    /// ⚠ <c>[0,0]</c> (and an absent key) removes the limit entirely.</summary>
    public static float ClampYawDeg(float deg, TurretDef def)
    {
        if (!def.YawRestricted)
        {
            return deg;
        }
        float min = def.YawMinDeg!.Value;
        float max = def.YawMaxDeg!.Value;
        float a = Mathf.Wrap(deg, -180f, 180f);
        foreach (float candidate in stackalloc[] { a, a + 360f, a - 360f })
        {
            if (candidate >= min && candidate <= max)
            {
                return candidate;
            }
        }
        return AngularDistance(a, min) <= AngularDistance(a, max) ? min : max;
    }

    /// <summary>Barrel angles of a base-local direction: yaw about +Y from −Z, then pitch up.</summary>
    public static (float YawDeg, float PitchDeg) AnglesOfLocal(Vector3 dir)
    {
        var d = dir.Normalized();
        float pitch = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(d.Y, -1f, 1f)));
        float yaw = Mathf.Abs(d.X) < 1e-6f && Mathf.Abs(d.Z) < 1e-6f
            ? 0f
            : Mathf.RadToDeg(Mathf.Atan2(-d.X, -d.Z));
        return (yaw, pitch);
    }

    /// <summary>The inverse of <see cref="AnglesOfLocal"/>: RotY(yaw)·RotX(pitch) applied to −Z.</summary>
    public static Vector3 LocalDir(float yawDeg, float pitchDeg)
    {
        float y = Mathf.DegToRad(yawDeg);
        float p = Mathf.DegToRad(pitchDeg);
        return new Basis(Vector3.Up, y) * new Basis(Vector3.Right, p) * Vector3.Forward;
    }

    /// <summary>Mirrors <c>FlightController.WorldBlocksLine</c>'s exact call shape (world-layer
    /// mask, no exclusion) against whatever <see cref="IWorldQuery"/> a carried gunner was built
    /// with, so behaviour stays bit-identical to reading the host directly. Static, like the
    /// other decoded rules on this class, so <c>CSVM.Tests</c> can assert it against a synthetic
    /// <see cref="IWorldQuery"/> with no live node in the process.</summary>
    public static bool WorldBlocksLine(IWorldQuery world, Vector3 from, Vector3 to) =>
        world.Ray(from, to, CollisionLayers.World, null, out _);

    /// <summary>An emplacement's line-of-sight rule, as a query a suite can sweep bearing by
    /// bearing: the world-layer ray, blind to its first <see cref="MountSkirtM"/>, solid over
    /// everything past that except <paramref name="exclude"/>, which an emplacement fills with its
    /// own site subtree (docs/formats/turrets.md "Acquiring": the skirt clears a ring, not a gun).
    /// ⚠ Never pass the site's PARENT group. It holds a neighbouring gun on the same pad and a
    /// whole airbase around one, which are cover, and a hull ring's far side.</summary>
    public static bool WorldBlocksEmplacementLine(PhysicsDirectSpaceState3D space, Vector3 from,
        Vector3 to, Godot.Collections.Array<Rid>? exclude = null)
    {
        var span = to - from;
        float len = span.Length();
        if (len <= MountSkirtM)
        {
            return false;
        }
        // Both disposed on the way out: the query object and the hit dictionary are finalizable
        // wrappers otherwise, one pair per turret per tick (PERF-20).
        using var query = PhysicsRayQueryParameters3D.Create(from + span * (MountSkirtM / len), to, CollisionLayers.World);
        if (exclude is { Count: > 0 })
        {
            query.Exclude = exclude;
        }
        using var hit = space.IntersectRay(query);
        return hit.Count > 0;
    }

    /// <summary>Writes the team the acquisition gate runs on, for the fan a zeppelin record's team
    /// performs across its airship, guns included. Returns whether the value moved.
    /// ⚠ The original also clears the gunner's target POINTER here (<c>FUN_004acb70</c>, the write
    /// at <c>0x004acb90</c>), so a now-friendly lock is not kept. This gunner holds no pointer and
    /// re-acquires every tick; the cached sight line is what does carry over, so it goes.</summary>
    public bool SetTeam(int team)
    {
        if (_team == team)
        {
            return false;
        }
        _team = team;
        _losNext = 0.0;              // the cached verdict was taken against the old team's target
        TargetPosition = default;
        Gate = TurretGate.NoTarget;
        return true;
    }

    /// <summary>The activation stand-in's hook (and, later, the real <c>WAKEUP_TURRETS</c>'):
    /// wakes a dormant emplacement. Logged by the caller, never silent.</summary>
    public void Wake() => SetActivated(true);

    /// <summary>Writes the awake gate directly, both ways, which is what the engine's subtree
    /// walk does to every turret standing on a node it visits (<c>ACTIVATED</c> is a plain byte
    /// on the turret, set to the walk's flag, so the same call both wakes and stows).</summary>
    public void SetActivated(bool activated) => Activated = activated;

    /// <summary>One gunner tick: duty cycle, acquire, aim, slew, pose, fire. A dormant
    /// emplacement takes no tick at all, <c>ACTIVATED</c> gates tracking as well as fire.</summary>
    public void SimStep(float dt)
    {
        // Ahead of every gate: the voice must run down its lease on the ticks this gunner does not
        // fire, or a turret that stops shooting holds its loop for good.
        Voice?.Tick(dt);
        if (!Activated || !Alive)
        {
            Gate = Activated ? TurretGate.Dead : TurretGate.Asleep;
            return;
        }
        if (_host == null && dt > 0f)
        {
            // The decoded moving-platform estimate: difference own position across the frame,
            // discard implausible speeds (a teleporting anchor, the first frame in the tree).
            var here = WorldPosition;
            var vel = _hasLastPos ? (here - _lastPos) / dt : Vector3.Zero;
            _platformVel = vel.Length() <= MaxPlatformSpeed ? vel : _platformVel;
            _lastPos = here;
            _hasLastPos = true;
        }
        _windowLeft -= dt;
        if (_windowLeft <= 0f)
        {
            Attacking = !Attacking;
            _windowLeft = Attacking
                ? RandRange(Def.AttackMin, Def.AttackMax)
                : RandRange(Def.BoredMin, Def.BoredMax);
        }
        _fireIn -= dt;

        if (!AcquireTarget(out var targetPos, out var targetVel))
        {
            Gate = TurretGate.NoTarget;
            _acquisitionReported = null; // an empty field re-arms the warning, as a new target does
            return; // nothing in the detection field: hold the current pose
        }
        TargetPosition = targetPos;
        ReportAcquisition(targetPos);

        // The base frame the arcs are authored in: the yaw node's rest orientation on the host.
        var baseBasis = BaseBasis();
        var toBase = baseBasis.Transposed(); // inverse of an orthonormal basis
        var muzzlePos = Firepoints[_fpNext].GlobalPosition;

        // Lead: a true intercept from the muzzle, the round's speed, and the target's velocity
        // relative to the platform. No solution ⇒ the turret tracks the raw bearing and holds
        // fire, it never falls back to a straight shot.
        bool solution = AimAssist.TryIntercept(muzzlePos, Weapon.Velocity ?? ProjectilePool.DefaultVelocity,
            targetPos, targetVel - PlatformVelocity, out var aimWorld, out _);
        if (!solution)
        {
            var bearing = targetPos - muzzlePos;
            if (bearing.LengthSquared() < 1e-6f)
            {
                return;
            }
            aimWorld = bearing.Normalized();
        }

        var solvedLocal = (toBase * aimWorld).Normalized();
        var (yawDeg, pitchDeg) = AnglesOfLocal(solvedLocal);
        var desired = LocalDir(ClampYawDeg(yawDeg, Def), ClampPitchDeg(pitchDeg, Def));
        SlewToward(desired, dt);
        ApplyNodePose();

        // Fire gates, in the engine's order: awake spell, a real intercept, the shot clock,
        // ammo, the cached line of sight, and the 15° barrel-on-solution cone.
        if (!Attacking || !solution || _fireIn > 0f || Ammo <= 0)
        {
            Gate = !Attacking ? TurretGate.Bored
                : !solution ? TurretGate.NoSolution
                : _fireIn > 0f ? TurretGate.Reloading
                : TurretGate.NoAmmo;
            return;
        }
        if (LineOfSightBlocked(targetPos))
        {
            Gate = TurretGate.Blocked;
            return;
        }
        if (BarrelLocal.Dot(solvedLocal) < FireGateCos)
        {
            Gate = TurretGate.Slewing;
            return;
        }
        Gate = TurretGate.Firing;
        var fp = Firepoints[_fpNext];
        _fpNext = (_fpNext + 1) % Firepoints.Length; // round-robin, one muzzle per shot
        // INACCURACY perturbs the SHOT after the pose is written: the turret aims true and the
        // rounds spread. Same uniform-polar cone as the player assist's launch scatter.
        var dir = AimAssist.Scatter(aimWorld, Mathf.DegToRad(Def.InaccuracyDeg), _rng);
        // ⚠ Pass team explicitly. A world emplacement has no shooter id, so the shooter-id default
        // reads its rounds as neutral in the aim assist's ordnance candidate list. Its own mount
        // rides along as the round's owner: the flak must not strike or splash the gun firing it.
        _pool.Spawn(Weapon, fp.GlobalTransform, PlatformVelocity,
            _host?.PlayerIndex ?? ProjectilePool.NoShooter, fp, dir, team: _team,
            ownerBodies: _host == null ? PlatformColliderRids() : null);
        // A carried gunner's round belongs to the aeroplane carrying it, so it crosses the wire
        // with that aeroplane's own fire. A world emplacement has no host and reports nothing:
        // every machine runs the same emplacements.
        _host?.ReportWeaponFired(Weapon, fp.GlobalPosition, dir);
        if (_host == null && !_firstShotLogged)
        {
            _firstShotLogged = true; // verification breadcrumb: WHICH emplacements actually engage
            Log.Info("weapons", $"turret {Label}: engaging (first shot, team {_team})");
        }
        // ⚠ The voice is renewed, never restarted per round: restarting chaingun.wav at each
        // projectile keeps the ballistic rate but turns an audible firing spell into isolated shots
        // (docs/formats/turrets.md).
        Voice?.Renew(fp.GlobalPosition);
        Ammo--;
        ShotsFired++;
        _fireIn = RandRange(Def.FireRateMin, Def.FireRateMax);
    }

    /// <summary>The mounting section's own colliders, collected once: the RIDs a round this gunner
    /// fires owns. Internal so the suite can read the set rather than infer it
    /// from behaviour. RIDs are stable for the world's lifetime and the subtree gains no
    /// colliders after the build (a destroyed part hides, it is not re-parented). Empty for a
    /// section that resolved to nothing.</summary>
    internal Godot.Collections.Array<Rid> PlatformColliderRids() =>
        _platformColliders ??= ColliderRidsUnder(_platform);

    /// <summary>This gunner's own rig: the collider RIDs under its site, collected once. The
    /// line-of-sight exclusion, since a gun never counts the thing it is bolted to as its own
    /// cover. ⚠ Narrower than <see cref="PlatformColliderRids"/> on purpose: a site's parent
    /// group holds whatever else was modelled beside it, from a second gun on the same pad to an
    /// airbase, and that geometry blocks (docs/formats/turrets.md "Acquiring").</summary>
    internal Godot.Collections.Array<Rid> SiteColliderRids() =>
        _siteColliders ??= ColliderRidsUnder(_site);

    /// <summary>Whether this gunner's own sight-line rule reads <paramref name="target"/> as
    /// blocked right now, uncached. Internal for the suite that proves a ground gun sees past its
    /// own rig and not past a stranger's.</summary>
    internal bool OwnSightLineBlocked(Vector3 target) => WorldRayBlocked(WorldPosition, target + Vector3.Up * 0.2f);

    private static Godot.Collections.Array<Rid> ColliderRidsUnder(Node3D? root)
    {
        var rids = new Godot.Collections.Array<Rid>();
        if (root == null || !GodotObject.IsInstanceValid(root))
        {
            return rids;
        }
        void Walk(Node node)
        {
            if (node is CollisionObject3D body)
            {
                rids.Add(body.GetRid());
            }
            foreach (var child in node.GetChildren())
            {
                Walk(child);
            }
        }
        Walk(root);
        return rids;
    }

    private static float AngularDistance(float a, float b) =>
        Mathf.Abs(Mathf.Wrap(a - b, -180f, 180f));

    private static float Uniform(RandomNumberGenerator rng, float min, float max) =>
        min >= max ? min : min + rng.Randf() * (max - min);

    // cs_name → Node3D over a subtree, trimmed: the shipped models carry at least one turret
    // node with a trailing space ('brigturret2 '), which an exact match would silently miss.
    private static Dictionary<string, Node3D> CollectNamedNodes(Node3D root)
    {
        var map = new Dictionary<string, Node3D>(StringComparer.OrdinalIgnoreCase);
        void Walk(Node node)
        {
            if (node is Node3D n3d && n3d.HasMeta(AnimRuntime.NameMeta))
            {
                map.TryAdd(n3d.GetMeta(AnimRuntime.NameMeta).AsString().Trim(), n3d);
            }
            foreach (var child in node.GetChildren())
            {
                Walk(child);
            }
        }
        Walk(root);
        return map;
    }

    private float RandRange(float min, float max) => Uniform(_rng, min, max);

    // The base frame the authored angles live in: the pose anchor's parent pose composed with
    // its rest rotation. The rig's rest orientations are airframe-aligned in the shipped models,
    // so this is also the frame the gameplay maths (gates, intercept decomposition) runs in.
    private Basis BaseBasis()
    {
        var anchor = YawNode ?? PitchNode;
        var restBasis = YawNode != null ? _yawRest.Basis : _pitchRest.Basis;
        var parentBasis = (anchor.GetParent() as Node3D)?.GlobalBasis ?? Basis.Identity;
        return (parentBasis * restBasis).Orthonormalized();
    }

    // The nearest live hostile entry of the engine's VehicleList and mission-structure pools inside
    // DETECTION_RANGE, the shared target-picker's minimise-a-score structure with distance as the
    // score. The own plane (carried) and teammates are gated out by the aim assist's team rule.
    // ⚠ Not the aircraft half of the vehicle list: the decoded picker walks the whole pool, which
    // holds AI ground and sea vehicles too. A hull is a candidate as the vessel it is, and must
    // never reach one by being registered as aircraft (docs/org/targeting.md).
    private bool AcquireTarget(out Vector3 pos, out Vector3 vel)
    {
        pos = default;
        vel = default;
        TargetSource = null;
        _scan.Clear();
        _pool.CollectVehicleList(_scan);
        _pool.CollectMissionStructures(_scan);
        var here = WorldPosition;
        float best = float.MaxValue;
        bool aircraftInReach = false;
        foreach (var c in _scan.Vehicles)
        {
            if (!c.Live || (_host != null && ReferenceEquals(c.Source, _host)))
            {
                continue;
            }
            if (!AimAssist.Hostile(_team, c.Team))
            {
                continue;
            }
            float d = here.DistanceTo(c.Position);
            if (d > Def.DetectionRange)
            {
                continue;
            }
            aircraftInReach |= c.Source is FlightController;
            if (d >= best)
            {
                continue;
            }
            best = d;
            pos = c.Position;
            vel = c.Velocity;
            TargetSource = c.Source;
        }

        // The aircraft-first preference, the same departure the aeroplane picker takes, so a
        // zeppelin's guns and the wingmen beside them go after the same enemies.
        if (AiTargetRanking.AircraftFirst && aircraftInReach)
        {
            return best < float.MaxValue;
        }

        // Walked after the vehicles against the same running best, ties going to it, which is the
        // decoded pass order. A gasbag is the one class this pass drops. A structure is marked
        // where it stands and never leads, so its velocity is zero.
        foreach (var c in _scan.Structures)
        {
            if (!c.Live || !AimAssist.Hostile(_team, c.Team)
                || c.Source is Mech3.DestructibleRegistry.Instance { Gasbag: true })
            {
                continue;
            }
            float d = here.DistanceTo(c.Position);
            if (d > Def.DetectionRange || d > best)
            {
                continue;
            }
            best = d;
            pos = c.Position;
            vel = Vector3.Zero;
            TargetSource = c.Source;
        }

        return best < float.MaxValue;
    }

    // The WA-Turret dispatch site (docs/formats/combat-voice.md): one report per acquisition
    // episode, on the first tick this gunner both holds a human player and sees it. Losing that
    // target re-arms it, so a gunner tracking one player warns the flight once.
    // ⚠ Keep this sight line uncached. The fire path's cached verdict is stamped out of the RNG
    // stream that also draws the scatter. A refresh here would move every round this gun fires.
    private void ReportAcquisition(Vector3 targetPos)
    {
        if (TargetSource is not FlightController { IsHumanPiloted: true } player)
        {
            _acquisitionReported = null;
            return;
        }
        if (ReferenceEquals(player, _acquisitionReported) || SightLineBlocked(targetPos))
        {
            return;
        }
        _acquisitionReported = player;
        _pool.ReportTurretAcquisition(this, player);
    }

    // The original casts from the platform to 0.2 m above the target and caches the verdict for
    // a random 1–2 s before re-testing; a failed test blocks firing. World geometry only,
    // another aircraft in the way is not cover. C9a applies the cached test to whatever target
    // was acquired (the original scopes it to the player); emplacements keep that consistent.
    private bool LineOfSightBlocked(Vector3 targetPos)
    {
        double now = Utils.GameClock.Current?.Time ?? 0.0;
        if (now >= _losNext)
        {
            _losNext = now + RandRange(1f, 2f);
            _losBlocked = SightLineBlocked(targetPos);
        }
        return _losBlocked;
    }

    // The cast itself, either family's rule: a carried gunner reads the host's IWorldQuery seam,
    // an emplacement its own space with its rig excluded.
    private bool SightLineBlocked(Vector3 targetPos) => _worldQuery != null
        ? WorldBlocksLine(_worldQuery, WorldPosition, targetPos + Vector3.Up * 0.2f)
        : WorldRayBlocked(WorldPosition, targetPos + Vector3.Up * 0.2f);

    // FlightController.WorldBlocksLine's twin for a gunner with no host rig, against the space its
    // own node lives in. The exclusion is the site subtree, which is the gun's rig and nothing
    // else, whatever the site hangs off: a chapter can park a ground gun under a grouping node
    // rather than on the world root, and the hull a ring is bolted to is not in the site.
    private bool WorldRayBlocked(Vector3 from, Vector3 to) =>
        (YawNode ?? PitchNode).GetWorld3D()?.DirectSpaceState is { } space
        && WorldBlocksEmplacementLine(space, from, to, SiteColliderRids());

    // The bounded slew: the barrel chases the clamped aim direction at SlewRate per second,
    // renormalised each tick, snapping whole once one frame covers the turn. Same degenerate
    // guards as AimAssist.Tick, Slerp throws on (anti)parallel inputs.
    private void SlewToward(Vector3 desired, float dt)
    {
        float t = SlewRate * dt;
        if (t >= 1f)
        {
            BarrelLocal = desired;
            return;
        }
        float dot = BarrelLocal.Dot(desired);
        BarrelLocal = dot > 0.999f
            ? (BarrelLocal + (desired - BarrelLocal) * t).Normalized()
            : dot < -0.999f
                ? desired
                : BarrelLocal.Slerp(desired, t).Normalized();
    }

    // Writes the barrel pose onto the PARTS chain: yaw on the traverse ring, pitch on the gun,
    // or the combined rotation on the one node of the 2-element form.
    private void ApplyNodePose()
    {
        var (yawDeg, pitchDeg) = AnglesOfLocal(BarrelLocal);
        var yawB = new Basis(Vector3.Up, Mathf.DegToRad(yawDeg));
        var pitchB = new Basis(Vector3.Right, Mathf.DegToRad(pitchDeg));
        if (YawNode != null)
        {
            YawNode.Transform = new Transform3D(_yawRest.Basis * yawB, _yawRest.Origin);
            PitchNode.Transform = new Transform3D(_pitchRest.Basis * pitchB, _pitchRest.Origin);
        }
        else
        {
            PitchNode.Transform = new Transform3D(_pitchRest.Basis * yawB * pitchB, _pitchRest.Origin);
        }
    }
}
