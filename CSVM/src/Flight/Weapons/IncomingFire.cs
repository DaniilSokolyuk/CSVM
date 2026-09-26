using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Weapons;

/// <summary>
/// <c>--incoming[=metres[,wep_id]]</c>, the incoming-fire test rig: a phantom shooter sitting on
/// each player's six, walking a burst up the tailpipe. Reaches both incoming-fire cues and the
/// shield behind them deterministically, without an AI gunner (which has to find its shot) or a
/// second pilot in splitscreen. Fires the target's own gun (or the named weapon) into the shared
/// pool under a shooter identity no player holds, so the rounds are real and land as any other
/// pilot's would, through <see cref="FlightController.TakeProjectileHit"/>. The muzzle sits
/// <see cref="Standoff"/> behind the aircraft, aimed along the target's own nose, so the round
/// overtakes on a parallel track. The metres argument offsets that track sideways, alternating
/// sides; at the default 0 the rounds land, and a wide offset is a rig that hits nothing, which is
/// what makes it the able-to-fail control.
/// </summary>
public sealed partial class IncomingFire : Node
{
    /// <summary>The shooter identity these rounds carry, outside every player index, so no
    /// aircraft ever excludes them as its own.</summary>
    public const int ShooterId = 10_000;

    /// <summary>Default lateral offset, metres: none, so a bare <c>--incoming</c> puts its rounds
    /// on the airframe, which is the only thing the original answers at all.</summary>
    public const float DefaultPass = 0f;

    // How far behind the target the phantom muzzle sits. A round leaves dead straight (A1,
    // CANNON_SPREAD is not a dispersion cone), so the offset achieved at the aircraft is the
    // requested one whatever this figure is. No data-driven reason remains for this exact number;
    // left at 120 m since nothing needs it changed.
    private const float Standoff = 120f;   // m
    // s between rounds, four a second: comfortably inside the shipped warning_shot_interval, so
    // every interval closes with a hit and the shield charges to the full transition, passes, then
    // ricochets and real damage. A slower rig leaves a quiet interval in every cycle, which re-arms
    // the shield forever and never shows the second half.
    private const float FireInterval = 0.25f;

    private readonly ProjectilePool _pool;
    private readonly WeaponDefs _weapons;
    private readonly List<FlightController> _targets = new();
    private readonly float _pass;
    private readonly string? _weaponId;

    private float _accum;
    private int _shot;

    public IncomingFire(ProjectilePool pool, WeaponDefs weapons, float pass, string? weaponId)
    {
        _pool = pool;
        _weapons = weapons;
        _pass = pass;
        _weaponId = weaponId;
        Name = "incoming_fire";
    }

    public void AddTarget(FlightController controller) => _targets.Add(controller);

    /// <summary>One session-simulation step of the burst clock: fires at each target in turn.</summary>
    public void SimStep(float dt)
    {
        if (_targets.Count == 0)
            return;
        _accum += dt;
        while (_accum >= FireInterval)
        {
            _accum -= FireInterval;
            FireAt(_targets[_shot % _targets.Count], _shot % 2 == 0 ? 1f : -1f);
            _shot++;
        }
    }

    private void FireAt(FlightController target, float side)
    {
        var weapon = ResolveWeapon(target);
        if (weapon == null)
            return;
        var xf = target.GlobalTransform;
        var forward = -xf.Basis.Z.Normalized();
        var right = xf.Basis.X.Normalized();
        var origin = xf.Origin - forward * Standoff + right * (_pass * side);
        var muzzle = new Transform3D(Basis.LookingAt(forward, Vector3.Up), origin);
        _pool.Spawn(weapon, muzzle, Vector3.Zero, ShooterId);
        Log.Info("weapons", $"incoming fire at P{target.PlayerIndex + 1}: {weapon.Id} {(_pass > 0f ? (side > 0f ? "right " : "left ") : string.Empty)}{_pass:0.0} m off, {Standoff:0} m astern");
    }

    private WeaponDef? ResolveWeapon(FlightController target)
    {
        if (_weaponId != null)
            return _weapons.Get(_weaponId);
        if (target.Loadout != null)
            foreach (var g in target.Loadout.FirableGuns)
                return g.Weapon;
        return null;
    }
}
