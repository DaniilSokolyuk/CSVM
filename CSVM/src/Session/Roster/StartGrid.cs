using System;
using System.Collections.Generic;
using CSVM.Flight.Modes;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.Roster;

/// <summary>The abreast starting grid: one anchor spawn, every pilot fanned symmetrically about it
/// on that anchor's heading, and the whole field raised as one to clear the ground. Detail on the
/// anchor delegation and the injected terrain sampler: this module's docs/architecture.md entry.
/// ⚠ Not a splitscreen spawner. Dogfight deliberately does not use this, since four dogfighters
/// abreast on one heading is an instant head-on merge; a stunt race and a co-op campaign mission
/// are its only callers.
/// ⚠ Scripted paths (solo flight, --vs, zone-less chapters, and a --det race) bypass this by never
/// constructing it, the caller picks the implementation once. Never add a bypass branch inside
/// this class; the race path's --det spawn guarantee is structural, not a runtime check.</summary>
public sealed class StartGrid : IFlightStarts
{
    /// <summary>TUNE: default metres between neighbouring grid slots, measured across the line;
    /// overridable as <c>startGrid.slotSpacing</c>. Not yet judged at the playtest
    /// (this module's docs/architecture.md entry).</summary>
    public const float SlotSpacingDefault = 60f;

    /// <summary>TUNE: default metres of air the lowest slot must have under it before the field
    /// is left alone; overridable as <c>startGrid.groundClearance</c>. Deliberately loose against
    /// the known airframe-collision overhang; dial it at the playtest.</summary>
    public const float GroundClearanceDefault = 100f;

    private readonly SpawnPicker _picker;
    private readonly Func<Vector3, float?> _groundAt;

    /// <param name="picker">Resolves the anchor. The grid delegates to it rather than reimplementing
    /// any part of spawn resolution.</param>
    /// <param name="groundAt">World height of the ground under a point, or null when nothing was
    /// found there.</param>
    public StartGrid(SpawnPicker picker, Func<Vector3, float?> groundAt)
    {
        _picker = picker;
        _groundAt = groundAt;
    }

    /// <summary>The whole field as one starting line: slot <c>i</c> of <c>n</c> sits
    /// <c>(i - (n-1)/2) * SlotSpacing</c> metres along the line perpendicular to the anchor
    /// heading, centred on the anchor whatever the count. Every pilot faces the anchor's heading.
    /// ⚠ The ground is probed under every slot and the entire field raised by the worst slot's
    /// need, never each plane by its own ground (this module's docs/architecture.md entry).</summary>
    public IReadOnlyList<FlightStart> ChooseStarts(IReadOnlyList<SpawnPoint>? spawns,
        string missionZrdrPath, int spawnBase, int playerCount)
    {
        // Read-through at the point of use, once per session: the grid is constructed at session
        // build and asked exactly once, so this is also the moment the values in force are logged.
        float spacing = Config.GetFloat("startGrid.slotSpacing", SlotSpacingDefault);
        float clearance = Config.GetFloat("startGrid.groundClearance", GroundClearanceDefault);

        var (anchorPos, anchorLookAt) = _picker.ChooseSpawn(spawns, missionZrdrPath, spawnBase, 0, "grid anchor ");
        // One start state for the whole grid, from the picker, so every slot begins on the same
        // throttle and speed. Deriving it per slot is the mixed-grid unfairness the grid exists
        // to remove, in the one coordinate the slot geometry does not control.
        var (throttle, speed) = _picker.StartState(spawns, missionZrdrPath);

        // The heading comes from the anchor's own pos→look-at pair, never from the spawn's
        // HeadingDeg: the --pos override has no heading field, and re-reading the list entry would
        // silently ignore --direction.
        var dir = anchorLookAt - anchorPos;
        dir = dir.LengthSquared() < 1e-6f ? Vector3.Forward : dir.Normalized();
        var across = dir.Cross(Vector3.Up);
        // Straight up or down has no perpendicular in the horizontal plane. Nothing in the spawn
        // data can produce it (every emitted spawn is level), but --direction=0,1,0 can.
        across = across.LengthSquared() < 1e-6f ? Vector3.Right : across.Normalized();

        var slots = new Vector3[playerCount];
        for (int i = 0; i < playerCount; i++)
        {
            slots[i] = anchorPos + across * ((i - (playerCount - 1) / 2f) * spacing);
        }

        float lift = 0f;
        int unprobed = 0;
        foreach (var slot in slots)
        {
            if (_groundAt(slot) is not { } ground)
            {
                unprobed++;
                continue;
            }
            lift = Mathf.Max(lift, ground + clearance - slot.Y);
        }
        // A slot with no ground under it asks for no lift, but says so: silence would read the
        // same as flat ground at sea level, and a whole-field miss means a broken sampler.
        if (unprobed > 0)
        {
            Log.Warn("flight", $"grid found no ground under {unprobed}/{playerCount} slots, field not raised on their account");
        }
        lift = Mathf.Max(lift, 0f);

        // The heading every slot shares, back in the units the spawn data states it in, so the
        // grid's lines read against the anchor's own line above them. The +360 before the wrap is
        // what keeps a due-north field from printing IEEE negative zero as "heading=-0°".
        float headingDeg = Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(-dir.X, -dir.Z)) + 360f, 360f);

        // ⚠ This per-slot line is the primary field instrument, not a convenience: the grid is
        // not photographable (chase-cam panes put a neighbour outside the frustum).
        var starts = new FlightStart[playerCount];
        for (int i = 0; i < playerCount; i++)
        {
            var pos = slots[i] + Vector3.Up * lift;
            starts[i] = new FlightStart(pos, pos + dir, throttle, speed);
            Log.Info("flight", $"spawn [P{i + 1} grid slot {i + 1} of {playerCount}] pos=({pos.X:0},{pos.Y:0},{pos.Z:0}) heading={headingDeg:0}° spacing={spacing:0.#}m lift={lift:0.#}m");
        }
        return starts;
    }
}
