using CSVM.Flight.Airframe;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Overlays;

/// <summary>
/// The debug kill key (F17): kills P1's currently selected <see cref="Flight.Weapons.TargetSelection"/> target
/// through its own death path, so kill-count and objective bookkeeping see it the same way a real
/// shot would, never by freeing the node. The playtester's escape hatch for a stray enemy blocking
/// an objective chain. P1-only, the precedent F19's <see cref="Labs.WorldDamageLab"/> and F51's weapon
/// lab already set for a single-pane debug tool. Inert with nothing selected.
/// ⚠ A world/carried turret carries no <c>HEALTH</c> key at all (<see cref="Flight.Weapons.TargetRef.Health"/>'s
/// own rule), so a turret selection is inert too rather than inventing a kill switch the decoded
/// data does not have.
/// </summary>
public sealed partial class DebugKillTarget : Node
{
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

    /// <summary>The routing itself, split out so a suite can drive it with a hand-built source and
    /// no live <see cref="Flight.Weapons.TargetSelection"/>/<see cref="Flight.Weapons.AimCandidateSet"/> scan behind it.</summary>
    internal void KillSource(object? source, string name, int killer)
    {
        switch (source)
        {
            case FlightController plane:
                // The crash path (AircraftLifecycle.Crash), the same one --crash and
                // --debug-scoreboard use: it reports Downed with a real killer, so kill-count and
                // GroupLiveCount both see this exactly as they see a real shot down.
                plane.DebugForceCrash(killer);
                Log.Info("weapons", $"debug kill (F17): {name} crashed");
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
}
