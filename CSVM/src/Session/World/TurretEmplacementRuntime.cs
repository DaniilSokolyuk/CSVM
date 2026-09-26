using System;
using System.Collections.Generic;
using CSVM.Flight.Audio;
using CSVM.Flight.Weapons;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.World;

/// <summary>
/// The world's AA emplacements: the standalone <c>ai.zrd</c> entries resolved against the built
/// chapter world, each driven by the same <see cref="TurretController"/> loop as the carried
/// gunners. Registered with the shared pool so every player's aim assist sees them. Detail on
/// the wake mechanisms (<see cref="WakeAll"/>, <see cref="SetActivatedUnder"/>) and the tree
/// order this depends on: this module's docs/architecture.md entry.
/// SessionSimulation steps this after the zeppelin runtime, so a slung emplacement reads its
/// host's moved pose. It has no independent Godot tick.
/// ⚠ Shipped `ACTIVATED` is the default; <see cref="WakeAll"/> and <see cref="SetActivatedUnder"/>
/// are the only two wake paths, both logged. Never wake a turret silently.
/// </summary>
public sealed partial class TurretEmplacementRuntime : Node
{
    private readonly TurretController[] _turrets;

    /// <param name="worldRoot">The built world's root, so each emplacement knows which top-level
    /// world object it stands on. Omitted, an emplacement treats only its own site subtree as its
    /// platform, and a gun on a modelled hull is blocked by that hull.</param>
    /// <param name="voices">Where a gun's firing voice hangs; omitted, every gun is silent.</param>
    public TurretEmplacementRuntime(TurretDefs defs, WeaponDefs weapons,
        Func<string, Node3D?, IReadOnlyList<Node3D>> findNodes, ProjectilePool pool,
        Node3D? worldRoot = null, GunVoiceHome? voices = null)
    {
        _turrets = TurretController.BuildEmplacements(defs, weapons, findNodes, pool, worldRoot, voices);
        pool.RegisterWorldTurrets(_turrets);
        Name = "turret_emplacements";
    }

    public IReadOnlyList<TurretController> Emplacements => _turrets;

    public int Count => _turrets.Length;

    public int AwakeCount
    {
        get
        {
            int n = 0;
            foreach (var t in _turrets)
            {
                if (t.Activated)
                {
                    n++;
                }
            }
            return n;
        }
    }

    /// <summary>How many emplacements are alive right now. A snapshot, not a stored flag:
    /// <see cref="TurretController.Alive"/> reads the site's <c>HEALTHY_NODE</c> visibility in the
    /// tree, so this counts nothing meaningful before the subtree is added to it.</summary>
    public int AliveCount
    {
        get
        {
            int n = 0;
            foreach (var t in _turrets)
            {
                if (t.Alive)
                {
                    n++;
                }
            }
            return n;
        }
    }

    /// <summary>The <c>WAKEUP_TURRETS</c> stand-in: wakes every dormant emplacement, one
    /// breadcrumb per turret so a log shows exactly what the flag armed.</summary>
    public int WakeAll()
    {
        int woken = 0;
        foreach (var t in _turrets)
        {
            if (!t.Activated)
            {
                t.Wake();
                woken++;
                Log.Info("flight", $"turret {t.Label}: woken (--wake-turrets stand-in for WAKEUP_TURRETS)");
            }
        }
        return woken;
    }

    /// <summary>Writes <c>ACTIVATED</c> on every emplacement standing on
    /// <paramref name="root"/> or anywhere under it, and reports how many changed state. The
    /// engine's own primitive: a recursive walk of the node's children that looks each node up in
    /// the turret list and stores the flag, reached both by the Instant Action builder's zeppelin
    /// arm and by the objectives script's zeppelin-turret wake.</summary>
    public int SetActivatedUnder(Node3D root, bool activated)
    {
        int changed = 0;
        foreach (var t in _turrets)
        {
            if (t.Site is not { } site || !GodotObject.IsInstanceValid(site))
            {
                continue;
            }
            if (site != root && !root.IsAncestorOf(site))
            {
                continue;
            }
            if (t.Activated == activated)
            {
                continue;
            }
            t.SetActivated(activated);
            changed++;
        }
        return changed;
    }

    /// <summary>Writes <paramref name="team"/> on every emplacement standing on
    /// <paramref name="root"/> or anywhere under it, and reports how many changed. The subtree walk
    /// is <see cref="SetActivatedUnder"/>'s, because it is the same primitive: the original fans a
    /// zeppelin record's team across the whole airship, its guns included
    /// (<c>FUN_004bee80</c>, docs/org/targeting.md).</summary>
    public int SetTeamUnder(Node3D root, int team)
    {
        int changed = 0;
        foreach (var t in _turrets)
        {
            if (t.Site is not { } site || !GodotObject.IsInstanceValid(site))
            {
                continue;
            }
            if (site != root && !root.IsAncestorOf(site))
            {
                continue;
            }
            if (t.SetTeam(team))
            {
                changed++;
            }
        }
        return changed;
    }

    public void SimStep(float dt)
    {
        foreach (var t in _turrets)
        {
            t.SimStep(dt);
        }
    }
}
