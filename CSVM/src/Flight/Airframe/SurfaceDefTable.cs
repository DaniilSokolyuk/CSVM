using System;
using System.Collections.Generic;
using CSVM.Mech3;

namespace CSVM.Flight.Airframe;

/// <summary>
/// One of the original's per-surface anim-def vectors, <c>"player_crash_" + name</c> or
/// <c>"touchdown_" + name</c> over every <see cref="SurfaceRegistry.Names"/> slot, plus the
/// cascade that indexes it with a struck material's numeric surface id
/// (<see cref="SceneBuilder.SurfaceIdMeta"/>, ultimately <see cref="GameZMaterial.SoilId"/>).
/// Faithful to <c>FUN_0048b920</c>; the touchdown family's one difference (its empty last-resort
/// arm plays nothing, `FUN_0048d2c0`) is decoded in
/// analysis/surface-classification/FINDINGS.md's 2026-08-12 section.
/// ⚠ The empty-slot arm is the mechanism, not a special case. Never hardcode which ids fall
/// back to slot 0; ask the bound program.
/// Engine-free and pure, so the cascade is testable without a scene.
/// </summary>
public sealed class SurfaceDefTable
{
    private readonly string?[] _slots;
    private readonly string? _lastResort;

    /// <summary>Builds the vector for <paramref name="prefix"/> against the program
    /// <paramref name="defExists"/> answers for. <paramref name="lastResort"/> is the bare anim
    /// name the original falls to when the vector cannot answer at all. It is <b>null</b> for a
    /// family whose handler plays nothing in that case, which is touchdown (see the remarks).</summary>
    public SurfaceDefTable(string prefix, string? lastResort, Func<string, bool> defExists)
    {
        _lastResort = lastResort;
        _slots = new string?[SurfaceRegistry.Names.Count];
        var playable = new List<string>();
        for (int id = 0; id < _slots.Length; id++)
        {
            string def = prefix + SurfaceRegistry.Names[id];
            if (!defExists(def))
                continue; // an empty slot: this id falls back to slot 0
            _slots[id] = def;
            playable.Add(def);
        }
        PlayableDefs = playable;
    }

    /// <summary>Every def this vector can reach, in slot order, what a runtime binding this
    /// family has to bind, since the struck surface is only known at the moment of impact.
    /// The registry names are pairwise distinct, so no def appears twice.</summary>
    public IReadOnlyList<string> PlayableDefs { get; }

    /// <summary>The def for a struck surface id, or the bare last-resort anim name. Null when this
    /// family has no last resort and the original plays nothing.
    /// <paramref name="surfaceId"/> is null for "no struck material", the original's null-material
    /// arm, which the headless <c>--crash</c> force takes.</summary>
    public string? DefForSurfaceId(int? surfaceId)
    {
        // vector[id], when the id is in range (signed lower bound, unsigned upper) and its slot
        // names a def that exists.
        if (surfaceId is { } id && id >= 0 && (uint)id < (uint)_slots.Length && _slots[id] is { } named)
            return named;
        // Otherwise slot 0, unless the vector itself cannot answer, which is the bare anim name.
        return _slots.Length > 0 && _slots[0] is { } fallback ? fallback : _lastResort;
    }
}
