using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Flight.Audio;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Ai;

/// <summary>
/// The gun a <c>mode ship</c> hull carries: the acquisition, the mount and the fire decision a
/// patrol boat and a turret truck run, decoded in docs/org/aiPilot.md ("What a <c>mode ship</c>
/// vehicle runs") and docs/org/aiPilot/aiWeapons.md. One per armed
/// <see cref="SurfaceVehicle"/>, stepped by it.
/// ⚠ A hull NEVER pursues and NEVER applies the quick draw, so this carries neither
/// <see cref="FlightController"/>'s Pursue-only gate nor a call to <see cref="AiGunner.Solve"/>,
/// whose first act is that gate. It shoots while flying its net. What it shares with the aircraft
/// path is the lead solver and the aim-quality threshold, not the gate order.
/// ⚠ The def's <c>attack_dwell</c> and <c>not_pursuit_dwell</c> are deliberately unread: both feed
/// the no-pursuit-before timestamp, which a class that never pursues never reaches.
/// </summary>
internal sealed class SurfaceGunner
{
    /// <summary>How long a picked target is held before the sweep may replace it, seconds, the
    /// hardcoded value at <c>FUN_004b0f20</c>. ⚠ This, not the def's dwell fields, is what paces a
    /// hull's target churn.</summary>
    public const float TargetHoldSeconds = 20f;

    /// <summary>The aim-error cone half-angle, degrees. A surface block authors <c>-1</c> in all
    /// nine skill slots (<c>dead_eye</c> included), so this is CSVM's standing unauthored-rating
    /// convention, the worst rating, the same default <see cref="AiGunner.DeadEyeAngleDeg"/>
    /// carries, and NOT a decoded boat-specific value. What the engine does with a <c>-1</c>
    /// rating is unread.</summary>
    public const float DeadEyeAngleDeg = 4f;

    /// <summary>How long one renewal keeps the hull's firing voice sounding, seconds: the general
    /// vehicle fire path's own literal, which is ZERO, renewed on every tick that selects the gun
    /// (docs/org/weaponFire.md, "The vehicle gun's voice is renewed per tick"). ⚠ Not the turret
    /// path's <see cref="TurretController.VoiceLeaseSeconds"/>: that one is renewed per round and
    /// has to bridge a refire interval, this one never does.</summary>
    public const float VoiceLeaseSeconds = 0f;

    private readonly SurfaceVehicle _vessel;
    private readonly ProjectilePool _pool;
    private readonly WeaponDef _weapon;
    private readonly AiWeaponSlot _slot;

    // The ATTACK cylinder's radius, never the activation one: the reach the scorer admits a
    // candidate inside. 400 m on both shipped hull defs against a 2500 m activation, and tighter
    // than the def's own 500 m weapon window, so that window's far end never binds on a shipped
    // hull (docs/org/aiPilot.md "Where a hull's attack triple comes from").
    private readonly float _attackRadius;

    private readonly Node3D _turretNode;
    private readonly Node3D _gunNode;
    private readonly Node3D _firepoint;
    private readonly Transform3D _turretRest;
    private readonly Transform3D _gunRest;
    private readonly GunVoice? _voice;
    private readonly RandomNumberGenerator _rng;
    private readonly AimCandidateSet _scan = new();
    private readonly List<RankedTargetCandidate> _ranked = new();
    private readonly List<AimCandidate> _sources = new();

    // The mount's aim in the HULL frame, which is the frame the engine slews and guards in; it
    // starts down the hull's nose, the mount's own authored rest direction (0, 0, -1).
    private Vector3 _aimLocal = Vector3.Forward;
    private object? _target;
    private float _holdLeft;
    private float _fireIn;
    private bool _firstShotLogged;

    private SurfaceGunner(SurfaceVehicle vessel, ProjectilePool pool, WeaponDef weapon,
        AiWeaponSlot slot, float attackRadius, Node3D turretNode, Node3D gunNode,
        Node3D firepoint, GunVoiceHome? voices)
    {
        _vessel = vessel;
        _pool = pool;
        _weapon = weapon;
        _slot = slot;
        _attackRadius = attackRadius;
        _turretNode = turretNode;
        _gunNode = gunNode;
        _firepoint = firepoint;
        _turretRest = turretNode.Transform;
        _gunRest = gunNode.Transform;
        // The armed weapon record's own looped cue, which on both shipped surface defs is wep_29's
        // snd_turretgun. ⚠ Never the vehicle def's cannon_sound: no shipped def authors one, so
        // that slot is null install-wide (docs/org/weaponFire.md).
        _voice = GunVoice.Attach(voices, weapon.LoopedSoundName, vessel.Name, VoiceLeaseSeconds);
        _rng = Rng.Stream(Rng.Weapons);
        Ammo = slot.Rounds;
    }

    /// <summary>Rounds left in the def's authored magazine (9000 on both surface defs, so
    /// effectively unlimited); a spent gun holds fire rather than reloading.</summary>
    public int Ammo { get; private set; }

    public int ShotsFired { get; private set; }

    /// <summary>What the acquisition last picked, or null while nothing ranks, the identity the
    /// suite reads rather than inferring the pick from where the barrel points.</summary>
    public object? Target => _target;

    /// <summary>The radius the ranking admits a candidate inside, the hull's attack volume as
    /// <c>SurfaceVehicleRuntime</c> resolved it at spawn. Exposed so the suite reads the
    /// reach rather than inferring it from which shots landed.</summary>
    public float AttackRadius => _attackRadius;

    /// <summary>The mount's aim in the hull's own frame, the value the guards and the slew act on
    /// and the aim-quality gate is measured from.</summary>
    public Vector3 AimLocal => _aimLocal;

    /// <summary>Where a round actually leaves: the model's own <c>firepoint</c> marker. Exposed so
    /// the suite can read the muzzle rather than infer it from where rounds appear, the same
    /// reason <see cref="TurretController.PlatformColliderRids"/> is reachable.</summary>
    public Vector3 MuzzlePosition => _firepoint.GlobalPosition;

    /// <summary>The two nodes the mount poses, for the suite's rotated-from-rest reading.</summary>
    public (Node3D Turret, Node3D Gun) MountNodes => (_turretNode, _gunNode);

    /// <summary>This hull's firing voice, or null when the session built no sound archive or the
    /// weapon authors no looped cue. Exposed for the same reason
    /// <see cref="TurretController.Voice"/> is: audio cannot be screenshot-verified.</summary>
    internal GunVoice? Voice => _voice;

    /// <summary>Builds the gunner for <paramref name="vessel"/>, or null when this hull is not an
    /// armed one. Every reason to decline is a missing input, never a policy: no pool or catalogue
    /// wired, no authored <c>weapons</c> block, an unknown weapon id, or no mount chain in the
    /// model. ⚠ The node lookup is recursive by name (<c>FUN_004761c0</c>): on both shipped defs
    /// the chain sits under <c>healthy</c>, so a direct-child lookup silences every boat.</summary>
    public static SurfaceGunner? Build(SurfaceVehicle vessel, ProjectilePool? pool,
        WeaponDefs? weapons, IReadOnlyList<AiWeaponSlot> fit, float attackRadius,
        GunVoiceHome? voices = null)
    {
        if (pool == null || weapons == null || fit.Count == 0)
        {
            return null;
        }
        // The first slot: a hull's fit is one gun, and the engine's own fire decision considers
        // only the first cannon in the list in any case (aiWeapons.md, "The trigger routine").
        var slot = fit[0];
        if (weapons.Get(slot.WeaponId) is not { } weapon)
        {
            Log.Info("weapons", $"surface: '{vessel.Name}' unarmed: no weapon def '{slot.WeaponId}'");
            return null;
        }
        var turret = FindDescendant(vessel.Body, "turret");
        var gun = FindDescendant(vessel.Body, "gun");
        var firepoint = gun != null ? FindDescendant(gun, "firepoint") : null;
        if (turret == null || gun == null || firepoint == null)
        {
            Log.Info("weapons", $"surface: '{vessel.Name}' unarmed: model carries no turret/gun/firepoint chain");
            return null;
        }
        return new SurfaceGunner(vessel, pool, weapon, slot, attackRadius, turret, gun,
            firepoint, voices);
    }

    /// <summary>One sim step: age the clocks, hold or re-acquire the target, aim the mount, and
    /// fire when every gate is open. Called only for a woken, undestroyed hull.</summary>
    public void Step(float dt)
    {
        _fireIn -= dt;
        _holdLeft -= dt;
        // Ahead of every gate, the way a turret gunner's is: the voice must run its lease down on
        // the ticks this gun does not renew it, or a hull that stops shooting holds the loop.
        _voice?.Tick(dt);
        if (!Acquire(out var targetPos, out var targetVel))
        {
            return; // nothing ranks: the mount holds where it is, the way an idle turret does
        }

        var basis = _vessel.Body.GlobalTransform.Basis.Orthonormalized();
        var muzzle = _firepoint.GlobalPosition;
        float roundSpeed = _weapon.Velocity ?? ProjectilePool.DefaultVelocity;
        // The lead is solved on the target's velocity RELATIVE to the hull, the CANNON row of the
        // decoded solver table (aiWeapons.md). No solution means the round cannot catch it, and
        // the engine points the mount back down its own axis rather than at a straight bearing.
        if (!AimAssist.TryIntercept(muzzle, roundSpeed, targetPos, targetVel - _vessel.Velocity,
                out var aimWorld, out _))
        {
            AimAt(basis, Vector3.Forward, dt);
            return;
        }

        var rawLocal = (basis.Transposed() * aimWorld).Normalized();
        AimAt(basis, rawLocal, dt);
        // The engine gates on the separation itself against the slot's authored window, both ends
        // squared at parse time.
        float sep2 = muzzle.DistanceSquaredTo(targetPos);
        if (sep2 < _slot.MinRangeM * _slot.MinRangeM || sep2 > _slot.MaxRangeM * _slot.MaxRangeM
            || Ammo <= 0)
        {
            return;
        }
        // ⚠ Renew here, never beside the shot, and from the hull origin, never the firepoint: the
        // fire decision renews the loop ahead of both the refire timer and the aim gate below
        // (docs/org/weaponFire.md).
        _voice?.Renew(_vessel.Position);
        // The residual is measured against the RAW lead, so the angle the elevation guards gave
        // away is charged to the shot exactly as an aeroplane's traverse clamp is.
        if (SurfaceGunMount.AimQuality(_aimLocal, rawLocal) < AiGunner.AimQualityCos)
        {
            return;
        }
        if (_fireIn > 0f)
        {
            return;
        }

        Fire(basis);
    }

    /// <summary>Silences the gun at once, for a hull whose death sequence owns everything audible
    /// from here on. Needed because a destroyed hull is no longer stepped at all, so the lease has
    /// nothing left to run it down.</summary>
    public void Silence() => _voice?.Stop();

    // A recursive find-by-name under a subtree, the shape FUN_004761c0 takes: the whole subtree,
    // not the immediate children.
    private static Node3D? FindDescendant(Node3D root, string name)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is not Node3D node)
            {
                continue;
            }
            if (string.Equals(node.Name, name, System.StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
            if (FindDescendant(node, name) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static void CollectRids(Node node, Godot.Collections.Array<Rid> into)
    {
        if (node is CollisionObject3D body)
        {
            into.Add(body.GetRid());
        }
        // By index: a hull walk per shot through GetChildren() is an engine array per node (PERF-20).
        for (int i = 0, count = node.GetChildCount(); i < count; i++)
        {
            CollectRids(node.GetChild(i), into);
        }
    }

    // Guards the desired direction into the mount's elevation band, slews the mount toward it and
    // writes the pose onto the two nodes, so the barrel a player sees is the barrel that shoots.
    private void AimAt(Basis basis, Vector3 desiredLocal, float dt)
    {
        var guarded = SurfaceGunMount.Guard(desiredLocal);
        _aimLocal = SurfaceGunMount.Slew(_aimLocal, guarded, dt);
        var (yawDeg, pitchDeg) = TurretController.AnglesOfLocal(_aimLocal);
        _turretNode.Transform = new Transform3D(
            _turretRest.Basis * new Basis(Vector3.Up, Mathf.DegToRad(yawDeg)), _turretRest.Origin);
        _gunNode.Transform = new Transform3D(
            _gunRest.Basis * new Basis(Vector3.Right, Mathf.DegToRad(pitchDeg)), _gunRest.Origin);
    }

    // Keeps the standing target while the 20 s hold runs and it is still live, otherwise sweeps
    // the three candidate pools and ranks them with the non-jet scorer. The geometry comes back
    // out of the fresh scan rather than off the held object, so one candidate shape serves every
    // target class and nothing has to type-switch on what a hull is shooting at.
    private bool Acquire(out Vector3 targetPos, out Vector3 targetVel)
    {
        targetPos = Vector3.Zero;
        targetVel = Vector3.Zero;
        int ownTeam = _vessel.Team ?? AimAssist.NeutralTeam;
        if (ownTeam == AimAssist.NeutralTeam)
        {
            _target = null;
            return false; // an unowned hull is nobody's enemy, on either side of the gate
        }

        _scan.Clear();
        _pool.CollectVehicleList(_scan);
        _pool.CollectTurrets(_scan);
        _pool.CollectMissionStructures(_scan);
        _ranked.Clear();
        _sources.Clear();
        Collect(_scan.Vehicles, ownTeam, isTurret: false, aircraftOnly: true);
        Collect(_scan.Turrets, ownTeam, isTurret: true, aircraftOnly: false);
        Collect(_scan.Structures, ownTeam, isTurret: false, aircraftOnly: false);

        if (_target != null && _holdLeft > 0f)
        {
            for (int i = 0; i < _sources.Count; i++)
            {
                if (ReferenceEquals(_sources[i].Source, _target))
                {
                    targetPos = _sources[i].Position;
                    targetVel = _sources[i].Velocity;
                    return true;
                }
            }
        }

        // aircraftFirst: false, because the aircraft-first departure is scoped to the two pickers
        // an ally flies behind, the aeroplane's and the turret's. A hull keeps the decoded order,
        // which is also what leaves one picker in the build running the original's own priority.
        int best = AiTargetRanking.SelectBest(_vessel.Position, -_vessel.Body.GlobalTransform.Basis.Z,
            _attackRadius, AiScorer.Other, aircraftFirst: false, _ranked, out _);
        if (best < 0)
        {
            _target = null;
            return false;
        }

        _target = _sources[best].Source;
        _holdLeft = TargetHoldSeconds;
        targetPos = _sources[best].Position;
        targetVel = _sources[best].Velocity;
        return true;
    }

    // One pool's hostile, live members as rank candidates. The vehicle pool carries the session's
    // hulls beside its aircraft; aircraftOnly drops the hulls, matching the limit the aircraft
    // path already has, so a boat does not shoot another boat.
    private void Collect(List<AimCandidate> pool, int ownTeam, bool isTurret, bool aircraftOnly)
    {
        foreach (var c in pool)
        {
            if (!c.Live || ReferenceEquals(c.Source, _vessel))
            {
                continue;
            }
            if (c.Team == AimAssist.NeutralTeam || c.Team == ownTeam)
            {
                continue;
            }
            if (aircraftOnly && c.Source is not FlightController)
            {
                continue;
            }
            string name = c.Source switch
            {
                FlightController fc => fc.IsHumanPiloted ? AiTargetRanking.PlayerRole : fc.Name,
                DestructibleRegistry.Instance pool2 => pool2.Def.Name,
                _ => string.Empty,
            };
            _ranked.Add(new RankedTargetCandidate
            {
                Position = c.Position,
                Velocity = c.Velocity,
                IsPlayer = c.Source is FlightController { IsHumanPiloted: true },
                IsAircraft = c.Source is FlightController,
                IsStructureClass = isTurret || c.Source is DestructibleRegistry.Instance,
                IsWingman = c.Source is FlightController { Pilot.Escort: not null },
                IsGasbag = c.Source is DestructibleRegistry.Instance { Gasbag: true },
                // The candidate's own target_bias; the scorer's struct_bias is a hull's own field
                // and the shipped hull defs author none, so a turret or structure candidate here
                // spends nothing.
                ClassBias = (c.Source as FlightController)?.Stats?.AiTargetBias ?? 0f,
                ObjectiveBias = AiTargetRanking.ObjectiveBiasFor(name, null, isTurret),
                AlliedAttackers = 0,
            });
            _sources.Add(c);
        }
    }

    private void Fire(Basis basis)
    {
        var fp = _firepoint;
        // The round leaves along the MOUNT's aim, not the raw lead: the barrel is a real node here
        // and a shot down a different line than the one drawn would read as a miss aimed at
        // nothing. The dead-eye cone is the shot's own scatter, drawn once per round.
        var aimWorld = (basis * _aimLocal).Normalized();
        var dir = AimAssist.Scatter(aimWorld, Mathf.DegToRad(DeadEyeAngleDeg), _rng);
        // ⚠ Team explicitly and the hull's own colliders as the round's owner, the world
        // emplacement's case: a hull has no shooter id, so the default would read its rounds as
        // neutral, and its muzzle sits ON the thing it would otherwise strike.
        _pool.Spawn(_weapon, fp.GlobalTransform, _vessel.Velocity, ProjectilePool.NoShooter,
            fp, dir, team: _vessel.Team, ownerBodies: HullColliderRids());
        if (!_firstShotLogged)
        {
            _firstShotLogged = true; // verification breadcrumb: WHICH hulls actually engage
            Log.Info("weapons", $"surface: '{_vessel.Name}' engaging (first shot, team {_vessel.Team?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}, {_slot.WeaponId})");
        }
        Ammo--;
        ShotsFired++;
        _fireIn = _slot.RefireSeconds;
    }

    // The hull's own colliders, walked fresh rather than cached: a hull's subtree changes as the
    // injure ladder and the death sequence swap parts, where an emplacement's mounting section
    // does not.
    private Godot.Collections.Array<Rid> HullColliderRids()
    {
        var rids = new Godot.Collections.Array<Rid>();
        CollectRids(_vessel.Body, rids);
        return rids;
    }
}
