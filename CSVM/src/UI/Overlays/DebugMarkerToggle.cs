using System.Collections.Generic;
using CSVM.Flight.Hud;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Overlays;

/// <summary>
/// The all-aircraft markers key (F16): switches <see cref="TargetHud.MarkAll"/> on every human
/// pane, the overlay <c>--debug-markers</c> switches on at launch, so a flight test can call up
/// every live aircraft's identity, slant range, health, armour and AI mode without relaunching.
/// ⚠ Session-level, never one handler per pane. A splitscreen pane's HUD lives inside a
/// <c>SubViewport</c> built with <c>HandleInputLocally</c> off, which routes unhandled input to the
/// window instead of to that pane's own nodes, so a per-pane key would answer in single player
/// alone.
/// </summary>
public sealed partial class DebugMarkerToggle : Node
{
    private readonly System.Func<IReadOnlyList<TargetHud>> _huds;

    public DebugMarkerToggle(System.Func<IReadOnlyList<TargetHud>> huds)
    {
        _huds = huds;
        Name = "debug_marker_toggle";
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.F16 })
        {
            return;
        }
        Toggle();
        GetViewport().SetInputAsHandled();
    }

    /// <summary>F16: show or hide the markers on every pane at once. Public so a suite can drive it
    /// with no key event.</summary>
    public void Toggle()
    {
        var huds = _huds();
        if (huds.Count == 0)
        {
            Log.Info("flight", $"all-aircraft markers (F16): no flight pane to mark on");
            return;
        }

        // One answer written to every pane rather than a flip each, so two panes cannot drift into
        // opposite states however they were built.
        bool on = !huds[0].MarkAll;
        foreach (var hud in huds)
        {
            hud.MarkAll = on;
        }

        Log.Info("flight",
            $"all-aircraft markers (F16): {(on ? "on" : "off")} on {huds.Count} pane(s)");
    }
}
