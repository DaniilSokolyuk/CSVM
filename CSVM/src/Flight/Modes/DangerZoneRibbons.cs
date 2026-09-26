using System;
using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Modes;

/// <summary>A mission's <see cref="DangerZoneRibbon"/> set, read straight off the chapter gamez
/// whether or not <c>--debug-dzpaths</c> built the geometry: every <c>dzpathN</c> node's route
/// polygon, the one polygon of its mesh whose material the two gate outlines do not share
/// (docs/formats/missions.md). Shared by every AI pilot of the session, since the lanes are
/// occupancy-counted across pilots. The original's <c>DZPathList</c> (<c>FUN_004459f0</c>).</summary>
public sealed class DangerZoneRibbons
{
    private readonly Dictionary<int, DangerZoneRibbon> _byIndex = new();

    private DangerZoneRibbons()
    {
    }

    public IReadOnlyCollection<DangerZoneRibbon> All => _byIndex.Values;

    /// <summary>Reads every ribbon of the world, each at the difficulty its own node authors.
    /// The disabled set is the mission's dzones.zrd disable list. It switches the named zones
    /// off for the AI as it does for the player. Null when the world carries no ribbon at
    /// all.</summary>
    public static DangerZoneRibbons? Load(GameZ gamez, IReadOnlySet<string>? disabled = null)
    {
        var set = new DangerZoneRibbons();
        var lanes = new List<string>();
        foreach (var node in gamez.Nodes)
        {
            if (!TryIndexOf(node.Name, out int index) || node.MeshIndex < 0 || node.MeshIndex >= gamez.Meshes.Count)
                continue;
            if (RoutePolygon(gamez.Meshes[node.MeshIndex]) is not { } route)
                continue;
            var xf = gamez.WorldTransformOf(node);
            var mesh = gamez.Meshes[node.MeshIndex];
            var vertices = new List<Vector3>(route.VertexIndices.Count);
            foreach (int vi in route.VertexIndices)
            {
                if (vi >= 0 && vi < mesh.Vertices.Count)
                    vertices.Add(xf * mesh.Vertices[vi]);
            }
            if (vertices.Count < 2)
                continue;
            var offsets = new List<Vector3>();
            foreach (int ci in node.Children)
            {
                if (ci >= 0 && ci < gamez.Nodes.Count && gamez.Nodes[ci].Local is { } local)
                    offsets.Add(local.Origin);
            }
            if (offsets.Count > 0)
                lanes.Add($"{node.Name} x{offsets.Count}");
            var ribbon = DangerZoneRibbon.FromPolyline(
                node.Name, index, vertices, offsets, node.DangerZoneDifficulty);
            if (disabled != null && disabled.Contains(node.Name))
                ribbon.Active = false;
            set._byIndex[index] = ribbon;
        }
        if (set._byIndex.Count == 0)
            return null;
        string laneNote = lanes.Count > 0 ? $", extra lanes on {string.Join(", ", lanes)}" : "";
        Log.Info("flight", $"danger-zone ribbons: {set._byIndex.Count} loaded{laneNote}");
        return set;
    }

    /// <summary>A set over ribbons built by hand, for a test or a probe with no gamez.</summary>
    public static DangerZoneRibbons Of(IEnumerable<DangerZoneRibbon> ribbons)
    {
        var set = new DangerZoneRibbons();
        foreach (var ribbon in ribbons)
            set._byIndex[ribbon.Index] = ribbon;
        return set;
    }

    /// <summary>The ribbon <c>dzpath&lt;index&gt;</c>, or null when the world has none by that number.</summary>
    public DangerZoneRibbon? ByIndex(int index) => _byIndex.GetValueOrDefault(index);

    /// <summary>The nearest end of any active ribbon to <paramref name="position"/>: the pick a
    /// tagged node with no path number makes (<c>FUN_004210e0</c>'s forced arm), with no range
    /// limit, no free-lane test and no difficulty test. Null when nothing is active.</summary>
    public (DangerZoneRibbon Ribbon, bool FarEnd)? NearestEnd(Vector3 position)
    {
        (DangerZoneRibbon, bool)? best = null;
        float bestDistance = float.MaxValue;
        foreach (var ribbon in _byIndex.Values)
        {
            if (!ribbon.Active)
                continue;
            for (int end = 0; end < 2; end++)
            {
                float distance = position.DistanceTo(ribbon.End(end == 1));
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = (ribbon, end == 1);
                }
            }
        }
        return best;
    }

    /// <summary>The pick a hit pilot's passed daredevil roll makes (<c>FUN_004210e0</c>'s
    /// unforced arm). Admitted: an active ribbon with a free lane whose difficulty
    /// <paramref name="naturalTouch"/> covers. Taken: the end inside
    /// <see cref="DangerZoneRibbon.ProximityRangeM"/> whose into-ribbon tangent best lines up
    /// with the line from <paramref name="position"/> to it, so the run continues the dive.
    /// Null when nothing qualifies, which arms the pilot's own retry hold.</summary>
    public (DangerZoneRibbon Ribbon, bool FarEnd)? ProximityPick(Vector3 position, int naturalTouch)
    {
        (DangerZoneRibbon, bool)? best = null;
        float bestDot = -1f;
        const float Reach = DangerZoneRibbon.ProximityRangeM * DangerZoneRibbon.ProximityRangeM;
        foreach (var ribbon in _byIndex.Values)
        {
            if (!ribbon.Active || ribbon.Difficulty > naturalTouch || !ribbon.HasFreeLane)
                continue;
            for (int end = 0; end < 2; end++)
            {
                bool far = end == 1;
                var toEnd = ribbon.End(far) - position;
                var tangent = ribbon.EndTangentInto(far);
                if (toEnd.LengthSquared() > Reach || toEnd.LengthSquared() < 1e-4f
                    || tangent.LengthSquared() < 1e-4f)
                {
                    continue;
                }
                float facing = toEnd.Normalized().Dot(tangent.Normalized());
                if (facing >= bestDot)
                {
                    bestDot = facing;
                    best = (ribbon, far);
                }
            }
        }
        return best;
    }

    private static bool TryIndexOf(string name, out int index)
    {
        index = -1;
        const string prefix = "dzpath";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Length == prefix.Length)
            return false;
        return int.TryParse(name.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out index);
    }

    // The route is the one polygon whose material no other polygon of the mesh shares; the gate
    // pair shares theirs. Polygon index is never the discriminator (docs/formats/missions.md).
    private static GameZPolygon? RoutePolygon(GameZMesh mesh)
    {
        var counts = new Dictionary<int, int>();
        foreach (var poly in mesh.Polygons)
            counts[poly.MaterialIndex] = counts.GetValueOrDefault(poly.MaterialIndex) + 1;
        GameZPolygon? route = null;
        foreach (var poly in mesh.Polygons)
        {
            if (counts[poly.MaterialIndex] == 1)
            {
                if (route != null)
                    return null; // two lone materials: not the route-plus-gate-pair shape
                route = poly;
            }
        }
        return route;
    }
}
