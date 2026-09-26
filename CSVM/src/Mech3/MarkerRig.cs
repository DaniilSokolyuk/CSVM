using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;

namespace CSVM.Mech3;

/// <summary>
/// A player aircraft's weapon marker rig, read from planes.zbd GameZ: walks a
/// <c>player_*</c> root, accumulating locals down to each <c>firepoint*</c>/<c>pylon*</c>/
/// <c>target</c>, and reports plane-frame positions plus co-located groups. See
/// <c>docs/formats/markers.md</c> for the full decode.
/// This is the committed instrument <c>markers.md</c> regenerates from; <c>--dump-markers</c>
/// and <c>UI.Overlays.MarkerOverlay</c> share it so the gizmos and the dumped table agree.
/// </summary>
public sealed class MarkerRig
{
    /// <summary>Positions closer than this (metres) count as one physical mount. The data reuses
    /// the exact same coordinate for a shared mount, so any small positive tolerance separates
    /// "same point" from the next-nearest firepoint (the closest distinct pair here is ~0.5 m).</summary>
    public const float CoLocateTolerance = 1e-2f;

    /// <summary>The 11 player airframes, model-node name → display name (see markers.md's mapping
    /// table). The dump tool iterates this when no plane is named; a display name only makes the
    /// table readable, so an airframe missing from the data is skipped, not an error.</summary>
    public static readonly IReadOnlyList<(string Model, string Display)> PlayerAirframes = new[]
    {
        ("player_bhawk", "Bloodhawk"),
        ("player_pfighter", "Devastator"),
        ("player_fury", "Fury"),
        ("player_warhawk", "Warhawk"),
        ("player_autogyro", "Hoplite"),
        ("player_avenger", "Hellhound"),
        ("player_balmoral", "Balmoral"),
        ("player_brigand", "Brigand"),
        ("player_fbrand", "Firebrand"),
        ("player_kestrel", "Kestrel"),
        ("player_peacemaker", "Peacemaker"),
    };

    // Alternate-state subtrees that may carry their own same-named reference node distinct from
    // the plane's one authored marker in the top-level `markers` group, the cockpit interior
    // models and the wreck. Mirrors PlaneBuilder's own skip list for exactly this reason: without
    // it, a first-match walk over the whole subtree could resolve to a `cockpit1`/`cockpit2` copy
    // of `cockpit_camera` instead of the authored one (docs/formats/markers.md).
    private static readonly HashSet<string> AltStateSubtrees = new(StringComparer.OrdinalIgnoreCase)
    {
        "cockpit1", "cockpit2", "destroyed", "player_damage_off",
    };

    private MarkerRig(string planeRoot, List<Marker> markers, List<IReadOnlyList<int>> coLocated)
    {
        PlaneRoot = planeRoot;
        Markers = markers;
        CoLocated = coLocated;
    }

    public enum MarkerKind { Firepoint, Pylon, Target }

    public string PlaneRoot { get; }

    /// <summary>Every firepoint/pylon/target under the plane, sorted kind then ordinal.</summary>
    public IReadOnlyList<Marker> Markers { get; }

    /// <summary>Groups of ≥2 indices into <see cref="Markers"/> that share a position, two gun
    /// groups on one mount. Empty for the airframes whose every firepoint is distinct.</summary>
    public IReadOnlyList<IReadOnlyList<int>> CoLocated { get; }

    /// <summary>Classifies a node name as a weapon marker. Returns false for everything else in
    /// the markers group (cockpit_camera, exhaust*, ground_level, …) and for the bare, unnumbered
    /// <c>firepoint</c>/<c>pylon</c> the AI airframes carry, a numeric suffix is required, which
    /// every player firepoint/pylon has.</summary>
    public static bool Classify(string name, out MarkerKind kind, out int ordinal)
    {
        kind = MarkerKind.Target;
        ordinal = 0;
        if (name.Equals("target", StringComparison.OrdinalIgnoreCase))
        {
            kind = MarkerKind.Target;
            return true;
        }
        if (TrySuffix(name, "firepoint", out ordinal))
        {
            kind = MarkerKind.Firepoint;
            return true;
        }
        if (TrySuffix(name, "pylon", out ordinal))
        {
            kind = MarkerKind.Pylon;
            return true;
        }
        return false;
    }

    /// <summary>Reads a plane's marker rig from GameZ, or null when the root node is absent.
    /// Walks the subtree below the plane root, accumulating each node's local transform, so a
    /// marker's reported position is its origin in the airframe frame.</summary>
    public static MarkerRig? Extract(GameZ gamez, string planeRoot)
    {
        var root = gamez.FindByName(planeRoot);
        if (root == null)
        {
            return null;
        }
        var markers = new List<Marker>();
        void Walk(GameZNode node, Transform3D acc)
        {
            acc *= node.Local ?? Transform3D.Identity;
            if (Classify(node.Name, out var kind, out int ordinal))
            {
                markers.Add(new Marker(node.Name, kind, ordinal, acc.Origin));
            }
            foreach (int c in node.Children)
            {
                if (c >= 0 && c < gamez.Nodes.Count)
                {
                    Walk(gamez.Nodes[c], acc);
                }
            }
        }
        // Below the root only: the root's own local transform places the airframe in a scene,
        // which is not part of the plane frame the markers.md positions are quoted in.
        foreach (int c in root.Children)
        {
            if (c >= 0 && c < gamez.Nodes.Count)
            {
                Walk(gamez.Nodes[c], Transform3D.Identity);
            }
        }
        markers.Sort(static (a, b) => a.Kind != b.Kind
            ? a.Kind.CompareTo(b.Kind)
            : a.Ordinal.CompareTo(b.Ordinal));
        var coLocated = GroupCoLocated(markers.ConvertAll(m => m.Local), CoLocateTolerance);
        return new MarkerRig(planeRoot, markers, coLocated);
    }

    /// <summary>Reads one non-weapon marker node's plane-frame position by name, e.g.
    /// <c>cockpit_camera</c>, by the same accumulate-from-below-root walk <see cref="Extract"/>
    /// uses for weapon markers, skipping the alternate-state subtrees above. Returns
    /// <paramref name="fallback"/> (default the origin) when the plane root or the named node is
    /// absent, the original's own fallback for a plane with no such node.</summary>
    public static Vector3 FindNamedMarker(GameZ gamez, string planeRoot, string nodeName, Vector3 fallback = default)
    {
        var root = gamez.FindByName(planeRoot);
        if (root == null)
        {
            return fallback;
        }
        Vector3? found = null;
        void Walk(GameZNode node, Transform3D acc)
        {
            if (found != null || AltStateSubtrees.Contains(node.Name))
            {
                return;
            }
            acc *= node.Local ?? Transform3D.Identity;
            if (node.Name.Equals(nodeName, StringComparison.OrdinalIgnoreCase))
            {
                found = acc.Origin;
                return;
            }
            foreach (int c in node.Children)
            {
                if (found != null)
                {
                    return;
                }
                if (c >= 0 && c < gamez.Nodes.Count)
                {
                    Walk(gamez.Nodes[c], acc);
                }
            }
        }
        // Below the root only, same convention as Extract: the root's own transform places the
        // airframe in a scene, which is not part of the plane frame the offset is quoted in.
        foreach (int c in root.Children)
        {
            if (found != null)
            {
                break;
            }
            if (c >= 0 && c < gamez.Nodes.Count)
            {
                Walk(gamez.Nodes[c], Transform3D.Identity);
            }
        }
        return found ?? fallback;
    }

    /// <summary>Groups indices whose positions fall within <paramref name="tolerance"/> of each
    /// other, returning only the groups of two or more. Shared by the dump and the viewer overlay
    /// so both flag the same co-located mounts. O(n²), which is nothing for ~17 markers.</summary>
    public static List<IReadOnlyList<int>> GroupCoLocated(IReadOnlyList<Vector3> positions, float tolerance)
    {
        float tolSq = tolerance * tolerance;
        var groups = new List<IReadOnlyList<int>>();
        var claimed = new bool[positions.Count];
        for (int i = 0; i < positions.Count; i++)
        {
            if (claimed[i])
            {
                continue;
            }
            List<int>? group = null;
            for (int j = i + 1; j < positions.Count; j++)
            {
                if (!claimed[j] && positions[i].DistanceSquaredTo(positions[j]) <= tolSq)
                {
                    group ??= new List<int> { i };
                    group.Add(j);
                    claimed[j] = true;
                }
            }
            if (group != null)
            {
                claimed[i] = true;
                groups.Add(group);
            }
        }
        return groups;
    }

    /// <summary>One human-readable dump block for this plane: a header line, then every marker
    /// with its plane-frame position and, for firepoints, its mirror pair, and, for any
    /// co-located marker, the other names sharing that mount. This is the text the
    /// <c>--dump-markers</c> tool prints and the markers.md tables regenerate from.</summary>
    public string Format(string? display = null)
    {
        int firepoints = 0, pylons = 0;
        bool hasTarget = false;
        foreach (var m in Markers)
        {
            switch (m.Kind)
            {
                case MarkerKind.Firepoint: firepoints++; break;
                case MarkerKind.Pylon: pylons++; break;
                case MarkerKind.Target: hasTarget = true; break;
            }
        }
        // index → the group's other member names, for the ≡ annotation
        var shared = new Dictionary<int, string>();
        foreach (var group in CoLocated)
        {
            foreach (int idx in group)
            {
                var others = new List<string>();
                foreach (int other in group)
                {
                    if (other != idx)
                    {
                        others.Add(Markers[other].Name);
                    }
                }
                shared[idx] = string.Join(", ", others);
            }
        }
        var ordinals = new HashSet<int>();
        foreach (var m in Markers)
        {
            if (m.Kind == MarkerKind.Firepoint)
            {
                ordinals.Add(m.Ordinal);
            }
        }

        var sb = new StringBuilder();
        string title = display != null ? $"{display} ({PlaneRoot})" : PlaneRoot;
        sb.Append("=== ").Append(title).Append(": ")
            .Append(firepoints).Append(" firepoints, ")
            .Append(pylons).Append(" pylons")
            .Append(hasTarget ? ", target" : ", no target")
            .Append(CoLocated.Count > 0 ? $", {CoLocated.Count} co-located pair(s)" : "")
            .AppendLine(" ===");
        for (int i = 0; i < Markers.Count; i++)
        {
            var m = Markers[i];
            sb.Append("  ").Append(m.Name.PadRight(12)).Append(Vec(m.Local));
            if (m.Kind == MarkerKind.Firepoint)
            {
                int partner = MirrorPartner(m.Ordinal);
                sb.Append(ordinals.Contains(partner)
                    ? $"   pair(fp{Math.Min(m.Ordinal, partner)},fp{Math.Max(m.Ordinal, partner)})"
                    : "   (centreline)");
            }
            if (shared.TryGetValue(i, out var others))
            {
                sb.Append("   ≡ ").Append(others);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static bool TrySuffix(string name, string prefix, out int ordinal)
    {
        ordinal = 0;
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None,
            CultureInfo.InvariantCulture, out ordinal) && ordinal > 0;
    }

    // The mirror partner ordinal of a firepoint: gun pairs are consecutive
    // (fp1,fp2)(fp3,fp4)…, so odd n pairs with n+1 and even n with n−1. The Kestrel's fp7 is the
    // one centreline mount with no fp8, its partner simply isn't in the rig.
    private static int MirrorPartner(int ordinal) => (ordinal % 2 == 1) ? ordinal + 1 : ordinal - 1;

    private static string Vec(Vector3 v) => string.Format(CultureInfo.InvariantCulture,
        "( {0,7:+0.00;-0.00; 0.00}, {1,7:+0.00;-0.00; 0.00}, {2,7:+0.00;-0.00; 0.00} )", v.X, v.Y, v.Z);

    /// <param name="Ordinal">The trailing number (<c>firepoint7</c> → 7); 0 for <c>target</c>.</param>
    /// <param name="Local">Plane-frame position (metres), relative to the airframe origin.</param>
    public readonly record struct Marker(string Name, MarkerKind Kind, int Ordinal, Vector3 Local);
}
