using Godot;

namespace CSVM.Flight.Weapons;

/// <summary>One live objective site as the targeting path sees it. It carries the world node the
/// mission flagged, the two label lines its marker prints, and where the site is this frame. There
/// is ONE instance per site for as long as the mission flags it. The selection is held by source
/// identity (<see cref="TargetRef.IsSameTarget"/>), so a fresh instance per rebuild would drop the
/// pilot's selection every frame.</summary>
public sealed class ObjectiveSite
{
    /// <summary>The flagged target's <see cref="ObjectiveTarget.Key"/>, this site's identity
    /// string. It is what <c>--target=</c> matches: a bare node name, or <c>parent/child</c> for a
    /// site the mission authored as a path.</summary>
    public string Node { get; init; } = "";

    /// <summary>The target itself: <see cref="ObjectiveTarget.Node"/> is the name of the world
    /// node the site stands on, the fallback a label is looked up by.</summary>
    public ObjectiveTarget Target { get; init; }

    /// <summary>The site's own resolved name, the marker's second line.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>The label half of the marker's line 1 (<c>category_label</c>), or null.</summary>
    public string? TypeLabel { get; set; }

    /// <summary>The category half of line 1 (the live <c>help_label</c>), or null.</summary>
    public string? Category { get; set; }

    /// <summary>Where the site is now, re-read from its source on every rebuild.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Which of the record's two flags this site stands on. True is <c>objective</c>
    /// (entity <c>+0x4d</c>, the Enemy cycle); false is <c>other_target</c> (<c>+0x4c</c>, the
    /// Non-Aircraft cycle). ⚠ A key carrying both reads as an objective, which is the order the
    /// class filter itself tests the two bytes in.</summary>
    public bool Objective { get; set; }
}
