using System;
using System.Collections.Generic;
using CSVM.Flight;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Session;

/// <summary>
/// Builds and steps a mission's surface vehicles: a <c>mode ship</c> roster block or generator
/// launch becomes a copy of the chapter's library-root hull, placed on the water at its authored
/// spot and driven by <see cref="SurfaceVehicle"/>. The copy is indexed on the world runtime so
/// the chapter's own definitions anchor on it (the wake, the damage stages, the sinking), and its
/// destructible pool is the one a weapon hit or a ram reaches through <c>AnimRuntime.DamageAt</c>.
/// Stepped only by <see cref="SessionSimulation"/>, after the generators that may launch a hull.
/// </summary>
public sealed partial class SurfaceVehicleRuntime : Node
{
    // How far above and below the authored spot the water probe looks. A hull is authored at
    // y = 0 on every shipped block and the water sits within a metre of it.
    private const float WaterProbeReach = 500f;

    // How many surfaces the probe steps through looking for water, and how far below each it
    // restarts. A hull's own host is one deck and a hull's floor; nothing shipped stacks more.
    private const int WaterProbeHits = 4;
    private const float WaterProbeStep = 0.05f;

    private readonly GameZ _gamez;
    private readonly SceneBuilder _scene;
    private readonly AnimRuntime _runtime;
    private readonly VehicleDefs _defs;
    private readonly Node3D _worldRoot;
    private readonly List<SurfaceVehicle> _vessels = new();
    private readonly Dictionary<string, SurfaceVehicle> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="worldRoot">Where the hulls are parented and what the water probe casts in.</param>
    public SurfaceVehicleRuntime(GameZ gamez, SceneBuilder scene, AnimRuntime runtime,
        VehicleDefs defs, Node3D worldRoot)
    {
        Name = "surface_vehicles";
        _gamez = gamez;
        _scene = scene;
        _runtime = runtime;
        _defs = defs;
        _worldRoot = worldRoot;
    }

    /// <summary>Every hull built so far, roster and generator launches alike.</summary>
    public IReadOnlyList<SurfaceVehicle> Vessels => _vessels;

    /// <summary>The pool a hull's gun scans and fires through. Null in a build with no weapons
    /// (the suite labs, a bare stage), which leaves every hull unarmed rather than half-built.</summary>
    public ProjectilePool? Projectiles { get; set; }

    /// <summary>The weapon catalogue a hull's authored <c>weapons</c> id resolves against; null
    /// with the same effect as <see cref="Projectiles"/>.</summary>
    public WeaponDefs? Weapons { get; set; }

    /// <summary>Where a hull's firing voice hangs and what it resolves its cue through. Null leaves
    /// every hull's gun silent, which is what a muted or soundless session gets.</summary>
    public GunVoiceHome? Voices { get; set; }

    /// <summary>The string table a block's slot-20 title resolves through into
    /// <see cref="SurfaceVehicle.MarkerName"/>. Null leaves every hull unnamed, which is the
    /// shipped case for all but four blocks anyway.</summary>
    public Messages? Strings { get; set; }

    /// <summary>Whether every hull, built or yet to be built, follows another machine's samples.
    /// </summary>
    public bool Replicated { get; private set; }

    /// <summary>The hull a mission clause names, or null.</summary>
    public SurfaceVehicle? ByName(string name) =>
        _byName.TryGetValue(name, out var vessel) ? vessel : null;

    /// <summary>Hands every hull's patrol to another machine (<see cref="SurfaceVehicle.Replicate"/>),
    /// those a generator launches later included.</summary>
    public void Replicate()
    {
        Replicated = true;
        foreach (var vessel in _vessels)
        {
            vessel.Replicate();
        }
    }

    /// <summary>Builds one hull from <paramref name="plan"/> at <paramref name="position"/>,
    /// facing <paramref name="forward"/>, on the water. <paramref name="nodeName"/> is a
    /// generator's decoded launch name; null keeps the block's own. Null when the chapter carries
    /// no library-root model of the def, which is logged rather than invented.</summary>
    public SurfaceVehicle? Spawn(RosterSpawnPlan plan, Vector3 position, Vector3 forward,
        string? nodeName = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string name = nodeName ?? plan.Name;
        if (_gamez.FindByName(plan.Def) is not { } model || !_gamez.IsLibraryRoot(model))
        {
            Log.Info("world", $"surface: '{name}' not spawned: this chapter carries no library-root model '{plan.Def}'");
            return null;
        }
        if (_scene.BuildSubtree(model) is not { } body)
        {
            Log.Info("world", $"surface: '{name}' not spawned: the model '{plan.Def}' built to nothing");
            return null;
        }

        // The water is read before the hull is in the tree, so its own colliders cannot answer.
        float waterY = WaterHeightAt(position, out string waterNote);
        var flat = new Vector3(forward.X, 0f, forward.Z);
        float heading = flat.LengthSquared() > 1e-6f ? Mathf.Atan2(-flat.X, -flat.Z) : 0f;
        body.Name = name;
        body.Position = new Vector3(position.X, waterY, position.Z);
        body.Rotation = new Vector3(0f, heading, 0f);
        _worldRoot.AddChild(body);

        var pools = _runtime.IndexSpawnedCopy(body);
        DestructibleRegistry.Instance? pool = null;
        foreach (var candidate in pools)
        {
            // The hull's own pool is the one on the root; a compiled def outranks a reader one.
            if (candidate.Anchor == body && (pool == null || (candidate.Def.Archive != null && pool.Def.Archive == null)))
            {
                pool = candidate;
            }
        }
        var vessel = new SurfaceVehicle(name, plan, body, _runtime, pool,
            _defs.StartAnimsOf(plan.Def), _defs.InjureAnimsOf(plan.Def), waterY, heading)
        {
            MarkerName = MarkerNameOf(plan),
        };
        // The hull's own gun, from the def's own weapons block: the engine's weapon builder runs
        // for every vehicle it parses, so a boat is armed by the same path an aeroplane is
        // (docs/org/aiPilot.md "What a mode ship vehicle runs").
        float attackRadius = AttackRadiusOf(plan);
        vessel.Gunner = SurfaceGunner.Build(vessel, Projectiles, Weapons,
            _defs.WeaponsOf(plan.Def), attackRadius, Voices);
        if (Replicated)
        {
            vessel.Replicate();
        }
        _vessels.Add(vessel);
        _byName[name] = vessel;
        Log.Info("world", $"surface: '{name}' ({plan.Def}, {plan.Mode}) built at ({position.X:0},{waterY:0.##},{position.Z:0}){waterNote} team={plan.Team?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} group={plan.Group}{(vessel.MarkerName.Length > 0 ? $" marker '{vessel.MarkerName}'" : " unnamed")}{(pool != null ? Log.Format($" pool '{pool.Def.Name}' HP {pool.MaxHealth:0}") : " no destructible pool")}{(vessel.Gunner != null ? Log.Format($" armed {vessel.Gunner.Ammo} rounds, reach {attackRadius:0} m") : " unarmed")}{(plan.Inert ? " DEACTIVATED" : "")}");
        return vessel;
    }

    /// <summary>One session-simulation step of every hull.</summary>
    public void SimStep(float dt)
    {
        foreach (var vessel in _vessels)
        {
            if (GodotObject.IsInstanceValid(vessel.Body))
            {
                vessel.Step(dt);
            }
        }
    }

    /// <summary>Appends every built hull to the assist's candidate set, on <c>VehicleList</c> beside
    /// the aircraft roster (docs/org/aim-assist.md "The four lists": the decoded list holds
    /// "aircraft and AI ground/sea vehicles"), never the structure or turret list. An inert hull
    /// (not yet woken) is present but not live, the same shape a crashed pilot takes, so it neither
    /// brackets nor draws a gun lock before its wake. The velocity is the hull's own, since the
    /// engine's scorer leads a boat as it leads an aeroplane (the same docs page).</summary>
    public void CollectVehicles(AimCandidateSet into)
    {
        foreach (var vessel in _vessels)
        {
            if (!GodotObject.IsInstanceValid(vessel.Body))
            {
                continue;
            }
            into.AddVehicle(vessel.Position, vessel.Velocity, vessel.Team ?? AimAssist.NeutralTeam,
                !vessel.Inert && !vessel.IsDestroyed, vessel);
        }
    }

    // The radius the hull's own scorer admits a candidate inside, in the order the original writes
    // the attack triple at spawn: the def copy, then the net, then the block, each later writer
    // skipping a field the data leaves zero (docs/org/aiPilot.md "Where a hull's attack triple
    // comes from"). plan.Volumes is already the net overlaid by the block. A hull that resolves
    // to nothing keeps the decoded def record default: a 0 m reach would silence every boat and
    // truck in the game while still passing any check that only watches the scan.
    private float AttackRadiusOf(RosterSpawnPlan plan)
    {
        float radius = plan.Volumes.Attack.Radius;
        if (radius <= 0f)
        {
            radius = _defs.AttackOf(plan.Def) ?? VehicleDefs.DefaultAttackRadiusM;
        }
        return radius > 0f ? radius : VehicleDefs.DefaultAttackRadiusM;
    }

    // The hull's target-box name, resolved here because this is the seam where the string table
    // and the block meet, the same reason the aircraft spawner resolves its own there
    // (AiFlightAssembler.Assemble). An unresolved key is dropped rather than drawn: the raw
    // MSG_* spelling on the HUD is worse than the blank line most blocks ask for anyway. A miss
    // reads as the key echoed back, which is Messages.Get's own contract.
    private string MarkerNameOf(RosterSpawnPlan plan)
    {
        if (plan.Title is not { Length: > 0 } key || Strings is not { } strings)
        {
            return "";
        }
        string text = strings.Get(key);
        return text == key ? "" : text;
    }

    // The water surface under the authored spot: a downward probe on the world mask, carried on
    // past any other surface it meets first (a ship generator's launch point sits under the
    // host's own deck). No water hit, or no collision in this build, keeps the authored height.
    private float WaterHeightAt(Vector3 at, out string note)
    {
        var query = new GodotWorldQuery(_worldRoot);
        var from = at + Vector3.Up * WaterProbeReach;
        var to = at - Vector3.Up * WaterProbeReach;
        for (int i = 0; i < WaterProbeHits; i++)
        {
            if (!query.Ray(from, to, CollisionLayers.World, null, out var hit))
            {
                break;
            }
            if (ProjectilePool.SurfaceIsWater(hit.Collider))
            {
                note = Log.Format($" water={hit.Position.Y:0.##}");
                return hit.Position.Y;
            }
            from = hit.Position + Vector3.Down * WaterProbeStep;
        }
        note = " no water under it, authored height kept";
        return at.Y;
    }
}
