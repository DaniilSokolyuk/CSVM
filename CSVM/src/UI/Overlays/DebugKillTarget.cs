using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Overlays;

/// <summary>
/// The debug kill key (F17): kills P1's selected <see cref="Flight.Weapons.TargetSelection"/> target
/// through its own death path, never by freeing the node. Kill counts and objectives then see it
/// as they see a real shot. The playtester's escape hatch for a stray enemy blocking an objective
/// chain, P1-only as <see cref="Labs.WorldDamageLab"/> is, and inert with nothing selected.
/// Over the wire, a plane or pool another machine owns is killed by that owner.
/// ⚠ Do not invent a kill for a turret: it carries no <c>HEALTH</c> key
/// (<see cref="Flight.Weapons.TargetRef.Health"/>'s rule), so a turret selection is inert.
/// </summary>
public sealed partial class DebugKillTarget : Node
{
    // The damage share a routed kill is claimed at. It is far past any airframe's armour and
    // health, so one hit drains both whole pools whatever the weapon spends per round.
    private const float LethalScale = 1e6f;

    private readonly System.Func<FlightController?> _pilot;
    private readonly System.Func<AnimRuntime?> _runtime;

    public DebugKillTarget(System.Func<FlightController?> pilot, System.Func<AnimRuntime?> runtime)
    {
        _pilot = pilot;
        _runtime = runtime;
        Name = "debug_kill_target";
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.F17 })
        {
            return;
        }
        Kill();
        GetViewport().SetInputAsHandled();
    }

    /// <summary>F17: kill P1's current selection. Public so a suite can drive it with no key
    /// event.</summary>
    public void Kill()
    {
        if (_pilot() is not { } pilot)
        {
            return;
        }
        if (pilot.Targeting?.Current is not { } target)
        {
            Log.Info("weapons", $"debug kill (F17): nothing selected");
            return;
        }
        KillSource(target.Candidate.Source, target.Name, pilot.PlayerIndex);
    }

    /// <summary>The weapon a routed kill is claimed with, the first on <paramref name="pilot"/>'s
    /// fit that spends both armour and health. Pylons come ahead of guns, since a shield absorbs a
    /// cannon round. Null when the fit carries none.</summary>
    internal static WeaponDef? LethalWeapon(FlightController? pilot)
    {
        if (pilot?.Loadout is not { } loadout)
        {
            return null;
        }

        foreach (var hardpoint in loadout.Hardpoints)
        {
            if (Spends(hardpoint.Weapon))
            {
                return hardpoint.Weapon;
            }
        }

        foreach (var gun in loadout.Guns)
        {
            if (Spends(gun.Weapon))
            {
                return gun.Weapon;
            }
        }

        return null;
    }

    /// <summary>The routing itself, split out so a suite can drive it with a hand-built source and
    /// no live <see cref="Flight.Weapons.TargetSelection"/>/<see cref="Flight.Weapons.AimCandidateSet"/> scan behind it.</summary>
    internal void KillSource(object? source, string name, int killer)
    {
        switch (source)
        {
            case FlightController plane when ClaimedElsewhere(plane, killer):
                Log.Info("weapons", $"debug kill (F17): {name} claimed on its owner as one lethal hit");
                break;
            case FlightController plane:
                // The crash path (AircraftLifecycle.Crash), the same one --crash and
                // --debug-scoreboard use: it reports Downed with a real killer, so kill-count and
                // GroupLiveCount both see this exactly as they see a real shot down.
                plane.DebugForceCrash(killer);
                Log.Info("weapons", $"debug kill (F17): {name} crashed");
                break;
            case DestructibleRegistry.Instance inst when _runtime() is { DamageReplicated: true, DamageClaim: { } claim }:
                // A replicated pool spends nothing here, so the kill goes to the host that owns it.
                claim(inst, inst.MaxHealth + 1f);
                Log.Info("weapons", $"debug kill (F17): {name} claimed on the host");
                break;
            case DestructibleRegistry.Instance inst:
                // AnimRuntime.DamageAt, the same call a rocket makes (WorldDamageLab's own Kill):
                // spends the whole pool at once and runs the def's own death sequence.
                _runtime()?.DamageAt(inst.Anchor, inst.MaxHealth + 1f);
                Log.Info("weapons", $"debug kill (F17): {name} destroyed");
                break;
            default:
                // A turret or a live round: no HEALTH key in the decoded data, so there is no
                // death path to route a kill through. Inert, not invented.
                Log.Info("weapons", $"debug kill (F17): {name} has no kill path");
                break;
        }
    }

    private static bool Spends(WeaponDef? weapon) =>
        weapon is { ArmorDamage: > 0f, HealthDamage: > 0f };

    // A networked plane's router decides who spends a hit on it. Offered as one lethal round, a
    // plane another machine owns goes to its owner as a real shot's claim would. A plane decided
    // here is declined, and the crash path below kills it as before.
    private bool ClaimedElsewhere(FlightController plane, int killer)
    {
        if (plane.HitRouter is not { } router || LethalWeapon(_pilot()) is not { } weapon)
        {
            return false;
        }

        return router(new AircraftHit(plane, weapon, plane.WorldPosition, -1, killer, LethalScale));
    }
}
