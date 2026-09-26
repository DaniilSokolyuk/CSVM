using System;
using System.Collections.Generic;
using CSVM.Utils;

namespace CSVM.Flight.Camera;

/// <summary>The view a pilot has SELECTED, as opposed to the look-behind held for as long as a
/// key is down. The original's selector accepts exactly these three and nothing else
/// (<c>FUN_0042c210</c> stores the accepted value, <c>FUN_004414a0</c> falls back to Cockpit for
/// anything outside the set), docs/org/cameraViews.md, "Only three views are player-selectable".
/// The numbers ARE the engine's camera modes (<c>camera + 0x14c</c>), kept so a log line or a
/// future decode reads against the same values the binary uses.</summary>
public enum PilotViewMode
{
    /// <summary>Mode 0: the following 3rd-person camera, this project's only view until A1.</summary>
    Chase = 0,

    /// <summary>Mode 6: first person with the <c>cockpit1</c> interior drawn (80° H in the
    /// original). Placement is A2's, FOV A3's, interior B11's, A1 lands the mode alone.</summary>
    Cockpit = 6,

    /// <summary>Mode 7: first person from the same <c>cockpit_camera</c> point with the interior
    /// and the <c>markers</c>/<c>dontmove</c> nodes hidden (60° H in the original).</summary>
    Nose = 7,
}

/// <summary>The rules a selected view obeys, as pure functions: which mode a cycle key press
/// lands on, whether a mode is one of the two first-person views, and what a held numpad key does
/// to the selection. Separate from <see cref="CameraController"/> (which owns a
/// <c>Camera3D</c> and cannot be built without an engine) so the decisions are testable headlessly
///, <c>PilotViewTests</c> is that test.</summary>
public static class PilotView
{
    /// <summary>The three views in the order the original's Default View dropdown offers them,
    /// which is neither the enum's order nor the cycle key's. The list is uiData 2127's, read off
    /// the item branch at <c>0x0040d124</c> in crimson.exe: index 0 is langui 112, index 1 is
    /// langui 136, index 2 is langui 113 (docs/org/menu-inventory.md).</summary>
    public static readonly IReadOnlyList<PilotViewMode> Selectable =
        new[] { PilotViewMode.Cockpit, PilotViewMode.Nose, PilotViewMode.Chase };

    /// <summary>Whether this mode is one of the original's two first-person views. The answer the
    /// anim data's <c>PLAYER_1ST_PERSON</c> condition (id 120) is given, and the gate every later
    /// first-person behaviour hangs off.</summary>
    public static bool IsFirstPerson(PilotViewMode mode) =>
        mode is PilotViewMode.Cockpit or PilotViewMode.Nose;

    /// <summary>One press of the cycle key ("Cycle Cockpit Views", <c>MSG_LOOK_FORWARD</c>): a
    /// three-stop cycle, Cockpit → Nose → Chase → Cockpit. The original's own key walks all three
    /// selectable views, confirmed at the controls of the original; an earlier reading that it
    /// swaps only the first-person pair came from the binding's name and is retired.</summary>
    public static PilotViewMode Cycle(PilotViewMode mode) => mode switch
    {
        PilotViewMode.Cockpit => PilotViewMode.Nose,
        PilotViewMode.Nose => PilotViewMode.Chase,
        _ => PilotViewMode.Cockpit,
    };

    /// <summary>One step of a Default View row, <paramref name="dir"/> places along
    /// <see cref="Selectable"/> with wrap. Separate from <see cref="Cycle"/> because that is the
    /// flight key's own order and this is the dropdown's; a row that stepped the key's order would
    /// walk the original's three items out of the order the original lists them in.</summary>
    public static PilotViewMode Step(PilotViewMode mode, int dir)
    {
        int at = 0;
        for (int i = 0; i < Selectable.Count; i++)
        {
            if (Selectable[i] == mode)
            {
                at = i;
            }
        }

        return Selectable[(((at + dir) % Selectable.Count) + Selectable.Count) % Selectable.Count];
    }

    /// <summary>Which view is actually in force this frame. The look-behind is a camera override
    /// outside first person only: in first person it is a head look-back that never leaves the
    /// cockpit (confirmed at the controls of the original). The numpad never appears here, in any
    /// mode it is the head-look snap cluster the original binds it to
    /// (<c>OriginalScreenshots/Keybinds Views 2.png</c>), and a snap swings the view it is in
    /// rather than replacing it. Either way the selection is untouched.</summary>
    public static PilotViewMode Effective(PilotViewMode selected, bool backActive = false) =>
        backActive && !IsFirstPerson(selected) ? PilotViewMode.Chase : selected;

    /// <summary>The <c>--view=</c> spelling of a selected mode, or null when the argument names
    /// something else (a numpad digit, <c>back</c>, a typo) and the caller should go on to parse
    /// it as one of those.</summary>
    public static PilotViewMode? Parse(string s) =>
        Named(s, PilotViewMode.Chase) ?? Named(s, PilotViewMode.Cockpit) ?? Named(s, PilotViewMode.Nose);

    /// <summary>The original's own label for a view, the langui string its Default View dropdown
    /// draws: 112 <c>IDS_VIEWCOCKPIT</c>, 136 <c>IDS_VIEWNOCOCKPIT</c>, 113 <c>IDS_VIEWCHASE</c>.
    /// Not <see cref="Name"/>: these are the words on screen, that one is the argument spelling.
    /// ⚠ "First Person" is the original's name for the nose view, which reads as the pair's name
    /// rather than one of them; it is kept because it is the word the original's dropdown draws.
    /// </summary>
    public static string Label(PilotViewMode mode) => mode switch
    {
        PilotViewMode.Cockpit => "Cockpit",
        PilotViewMode.Nose => "First Person",
        _ => "Exterior",
    };

    /// <summary>The mode's name for a log line and for <c>--view=</c>, lower case, so the
    /// breadcrumb a scripted capture reads back matches the argument that asked for it.</summary>
    public static string Name(PilotViewMode mode) => mode switch
    {
        PilotViewMode.Cockpit => ViewWords.Cockpit,
        PilotViewMode.Nose => ViewWords.Nose,
        _ => ViewWords.Chase,
    };

    // One name matched case-insensitively, so --view=Cockpit reads like --view=BACK already does.
    private static PilotViewMode? Named(string s, PilotViewMode mode) =>
        string.Equals(s, Name(mode), StringComparison.OrdinalIgnoreCase) ? mode : null;
}
