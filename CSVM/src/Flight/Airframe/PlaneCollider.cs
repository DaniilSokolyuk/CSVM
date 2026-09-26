using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>
/// The flying aircraft's collision silhouette: plane-frame convex hulls FlightController sweeps
/// each physics frame (a lone center-line ray would let everything but the nose pass through
/// obstacles). The same hulls mount as <see cref="AircraftBody"/>'s hit shapes, single-sourced,
/// never a second derivation. Built once from the model's mesh triangles (PropAnimator-style
/// tree walk), transformed into FlightController's frame; hidden subtrees and the nose prop's
/// blur discs are excluded, but the autogyro's overhead rotor discs stay in, that plane's wing.
/// Each hull is the convex hull of one clipped region's triangles, so it hugs the silhouette
/// where a box bridged air. The four regions partition the airframe on x and z. ⚠ Hulls still
/// overlap where regions share clipped triangles; the earliest in <see cref="Parts"/> order is
/// what a caller reports. All threshold consts below are TUNE, see <c>CONTEXT.md</c>.
/// </summary>
public sealed class PlaneCollider
{
    // ⚠ Do not lower WingBandFrac to close a canard gap: it pulls the inboard wing-root chord
    // into the wing region. The band between the fuselage and the wing is covered by the
    // fuselage region instead (see Layout).
    private const float WingBandFrac = 0.35f;        // |x| beyond this × half-span = wing verts
    private const float TailStartFrac = 0.7f;        // z beyond this × length (nose −Z → tail +Z) = tail verts
    private const float WingSplitGap = 2f;           // m of empty chord between wing clusters ⇒ split (canards)
    private const float MinThickness = 0.3f;         // m, floor per hull dimension (thin fins/slabs)
    private const float VolumeSplitFrac = 0.3f;      // a cut must remove ≥ this share of a region's box volume
    // Total hull budget per plane. Every hull is a shape the terrain sweep casts each physics
    // frame, so the budget buys silhouette fidelity at a per-frame cost; the gain is large up to
    // here and small above it, and the measured ratios are in docs/org/weaponRay.md.
    private const int MaxParts = 16;

    private const float MinCutWidth = 0.35f;         // m, cut planes keep this far from the cluster rim
    private const int Bins = 64;                     // cut-plane candidates per axis (bin edges)

    private PlaneCollider(List<Part> parts) => Parts = parts;

    /// <summary>The airframe hulls; Local places each hull's centre in the
    /// FlightController's frame (the plane model's parent).</summary>
    public IReadOnlyList<Part> Parts { get; }

    /// <summary>One-line description of the hulls for the load log.</summary>
    public string Summary => string.Join(", ", Parts.Select(p => PartLine(p.Name, p.Hull)));

    /// <summary>One hull's census text. Invariant, because the string is built here and the log
    /// call only interpolates it: a comma-decimal machine would otherwise log 3,8×3,0×5,6 m.
    /// Engine-free, so a test pins the text without a physics shape.</summary>
    public static string PartLine(string name, ConvexHull hull) =>
        Log.Format($"{name} {hull.Bounds.Size.X:0.0}×{hull.Bounds.Size.Y:0.0}×{hull.Bounds.Size.Z:0.0} m/{hull.Points.Length}v");

    /// <summary>Derives the collision hulls from the built plane model; null if it has no usable
    /// geometry. Classification is geometric (extents only, no per-plane data): wing/tail/fuselage
    /// come from thresholds against the model's own half-span and length.</summary>
    public static PlaneCollider? Build(Node3D planeRoot)
    {
        var tris = new List<Triangle>();
        Collect(planeRoot, planeRoot.Transform, tris);
        var parts = new List<Part>();
        foreach (var region in Layout(tris))
        {
            parts.Add(new Part(region.Name, region.Hull,
                new Transform3D(Basis.Identity, region.Centre),
                new ConvexPolygonShape3D { Points = region.Hull.Points }));
        }
        return parts.Count > 0 ? new PlaneCollider(parts) : null;
    }

    /// <summary>The engine-free half of <see cref="Build"/>: the named regions and their hulls,
    /// each hull centred on its own bounds so <c>Centre</c> places it. Empty for unusable
    /// geometry.</summary>
    public static List<Region> Layout(List<Triangle> tris)
    {
        var regions = new List<Region>();
        if (tris.Count == 0)
            return regions;

        var all = Enclose(tris);
        float halfSpan = Mathf.Max(Mathf.Abs(all.Position.X), Mathf.Abs(all.End.X));
        float length = all.Size.Z;
        if (halfSpan < 0.1f || length < 0.1f)
            return regions;

        float tailStartZ = all.Position.Z + TailStartFrac * length;
        float wingBand = WingBandFrac * halfSpan;

        // Clipped geometry, not vertex picks: a triangle is cut at each boundary so
        // a giant wing-root triangle can't drag the wing region past the split.
        var wing = ClipAxis(tris, 0, wingBand, keepGreater: true);
        wing.AddRange(ClipAxis(tris, 0, -wingBand, keepGreater: false));
        // ⚠ Keep the tail's two x clips. Bounded on z alone it reaches tip to tip and one hull
        // over it bridges the whole wing gap (docs/org/weaponRay.md); the outboard aft geometry
        // it gives up is wing region at every z, so a wingtip strike is wing and never tail.
        var tail = ClipAxis(
            ClipAxis(ClipAxis(tris, 2, tailStartZ, keepGreater: true),
                0, wingBand, keepGreater: false),
            0, -wingBand, keepGreater: true);
        // The fuselage region runs out to the wing band, so the inboard chord, the canard roots
        // and the gear are inside some hull; refinement cuts the bridged air back out.
        var fuselage = ClipAxis(
            ClipAxis(ClipAxis(tris, 2, tailStartZ, keepGreater: false),
                0, wingBand, keepGreater: false),
            0, -wingBand, keepGreater: true);

        var clusters = new List<(string Name, List<Triangle> Tris)>();
        if (fuselage.Count > 0)
            clusters.Add(("fuselage", fuselage));
        if (tail.Count > 0)
            clusters.Add(("tail", tail));
        clusters.AddRange(WingClusters(wing));
        foreach (var (name, cluster) in Refine(clusters))
            AddHull(regions, name, cluster);
        return regions;
    }

    /// <summary>Gathers every visible mesh triangle under <paramref name="node"/>, transformed by
    /// the accumulated node transforms (<paramref name="xf"/> already includes the node's own).
    /// Public so a suite can measure hull coverage against the same triangles.</summary>
    public static void Collect(Node node, Transform3D xf, List<Triangle> tris)
    {
        if (node is Node3D n3d)
        {
            if (!n3d.Visible)
                return; // hidden subtrees (wing flares) are not part of the airframe
            var kind = PropParts.Classify(n3d.Name);
            if (kind is PropParts.Kind.PropMain or PropParts.Kind.PropGhost)
                return; // translucent discs would sink the fuselage hull's belly line
            if (n3d is MeshInstance3D mi)
            {
                // GetFaces yields triangle surfaces only (3 verts per face), so the
                // point-sprite "lights" instances contribute nothing anyway.
                if (mi.Mesh is { } mesh)
                {
                    var faces = mesh.GetFaces();
                    for (int i = 0; i + 2 < faces.Length; i += 3)
                        tris.Add(new Triangle(xf * faces[i], xf * faces[i + 1], xf * faces[i + 2]));
                }
                return;
            }
        }
        foreach (var child in node.GetChildren())
        {
            if (child is Node3D c)
                Collect(c, xf * c.Transform, tris);
            else
                Collect(child, xf, tris);
        }
    }

    // Splits the wing triangles at the widest chord (z) gap: a canard
    // plane's outboard geometry forms two clusters (nose canards, aft main wing)
    // that would otherwise merge into one nose-to-tail slab. The forward cluster is
    // the canard.
    private static IEnumerable<(string Name, List<Triangle> Tris)> WingClusters(List<Triangle> wing)
    {
        if (wing.Count == 0)
            yield break;
        wing.Sort((a, b) => a.Centroid.Z.CompareTo(b.Centroid.Z));
        float bestGap = 0f;
        int splitAt = -1;
        float coveredZ = Mathf.Max(wing[0].A.Z, Mathf.Max(wing[0].B.Z, wing[0].C.Z));
        for (int i = 1; i < wing.Count; i++)
        {
            float minZ = Mathf.Min(wing[i].A.Z, Mathf.Min(wing[i].B.Z, wing[i].C.Z));
            float gap = minZ - coveredZ;
            if (gap > bestGap)
            {
                bestGap = gap;
                splitAt = i;
            }
            coveredZ = Mathf.Max(coveredZ, Mathf.Max(wing[i].A.Z, Mathf.Max(wing[i].B.Z, wing[i].C.Z)));
        }
        if (bestGap < WingSplitGap)
        {
            yield return ("wing", wing);
            yield break;
        }
        yield return ("canard", wing.GetRange(0, splitAt));
        yield return ("wing", wing.GetRange(splitAt, wing.Count - splitAt));
    }

    // Greedy volume-guided refinement: repeatedly cut the cluster whose
    // best cut removes the most enclosed box volume (bridged air), until no cut removes
    // at least VolumeSplitFrac of its box or the MaxParts budget is reached.
    // Cutting the biggest offender first spends the budget where the misfit is
    // worst. Judged on boxes, not hulls, so the pieces (and the part order) do not
    // depend on the shape emitted afterwards.
    private static List<(string Name, List<Triangle> Tris)> Refine(
        List<(string Name, List<Triangle> Tris)> clusters)
    {
        var result = new List<(string Name, List<Triangle> Tris)>(clusters);
        while (result.Count < MaxParts)
        {
            int bestIdx = -1;
            float bestGain = 0f;
            List<List<Triangle>>? bestPieces = null;
            for (int i = 0; i < result.Count; i++)
            {
                if (!BestCut(result[i].Tris, out var pieces, out float gain) || gain <= bestGain)
                    continue;
                if (result.Count + pieces.Count - 1 > MaxParts)
                    continue; // a triple would bust the budget; a pair may still fit
                bestIdx = i;
                bestGain = gain;
                bestPieces = pieces;
            }
            if (bestIdx < 0)
                break;
            var name = result[bestIdx].Name;
            result.RemoveAt(bestIdx);
            for (int p = 0; p < bestPieces!.Count; p++)
                result.Insert(bestIdx + p, (name, bestPieces[p]));
        }
        return result;
    }

    // Best single or double axis cut by volume removed (dims clamped to MinThickness
    // so flat slabs still count); a double cut exists to separate bilateral pairs like twin fins
    // in one pass. Bins enclose full triangle corners so a surface crossing a cut
    // stays covered on both sides (overlap, not a leak), do not bin by centroid alone.
    // Returns false below VolumeSplitFrac gain or inside MinCutWidth of the rim.
    private static bool BestCut(List<Triangle> tris, out List<List<Triangle>> pieces, out float gain)
    {
        pieces = null!;
        gain = 0f;
        if (tris.Count < 8)
            return false; // below ~8 triangles a cut just chases mesh noise
        var whole = Enclose(tris);
        float wholeVol = ClampedVolume(whole);
        bool found = false;
        int bestAxis = 0, bestLo = 0, bestHi = 0; // bin-edge cut planes (hi 0 = single cut)
        for (int axis = 0; axis < 3; axis++)
        {
            float min = whole.Position[axis], size = whole.Size[axis];
            if (size < 2f * MinCutWidth)
                continue;
            // bin the triangles by centroid; each bin's AABB encloses full corners
            var binBox = new Aabb[Bins];
            var occupied = new bool[Bins];
            foreach (var t in tris)
            {
                int b = Mathf.Clamp((int)((t.Centroid[axis] - min) / size * Bins), 0, Bins - 1);
                var box = EncloseOne(t);
                binBox[b] = occupied[b] ? binBox[b].Merge(box) : box;
                occupied[b] = true;
            }
            // prefix[k] = box of bins [0, k), suffix[k] = box of bins [k, Bins)
            var prefix = new Aabb[Bins + 1];
            var prefixAny = new bool[Bins + 1];
            var suffix = new Aabb[Bins + 1];
            var suffixAny = new bool[Bins + 1];
            for (int k = 0; k < Bins; k++)
            {
                prefixAny[k + 1] = prefixAny[k] || occupied[k];
                prefix[k + 1] = !occupied[k] ? prefix[k]
                    : prefixAny[k] ? prefix[k].Merge(binBox[k]) : binBox[k];
            }
            for (int k = Bins - 1; k >= 0; k--)
            {
                suffixAny[k] = suffixAny[k + 1] || occupied[k];
                suffix[k] = !occupied[k] ? suffix[k + 1]
                    : suffixAny[k + 1] ? suffix[k + 1].Merge(binBox[k]) : binBox[k];
            }
            bool CutOk(int k) // cut plane at bin edge k, MinCutWidth off the rim
            {
                float at = min + size * k / Bins;
                return at - min >= MinCutWidth && min + size - at >= MinCutWidth;
            }
            // single cut at bin edge k; then for each k a sweep of second cuts j > k
            // whose middle range [k, j) accumulates incrementally
            for (int k = 1; k < Bins; k++)
            {
                if (!CutOk(k))
                    continue;
                if (prefixAny[k] && suffixAny[k])
                {
                    float g = wholeVol - (ClampedVolume(prefix[k]) + ClampedVolume(suffix[k]));
                    if (g > gain)
                    {
                        gain = g;
                        found = true;
                        (bestAxis, bestLo, bestHi) = (axis, k, 0);
                    }
                }
                if (!prefixAny[k])
                    continue; // an empty head piece reduces to a single cut at j
                var mid = default(Aabb);
                bool midAny = false;
                float headVol = ClampedVolume(prefix[k]);
                for (int j = k + 1; j < Bins; j++)
                {
                    if (occupied[j - 1])
                    {
                        mid = midAny ? mid.Merge(binBox[j - 1]) : binBox[j - 1];
                        midAny = true;
                    }
                    if (!CutOk(j) || !suffixAny[j])
                        continue;
                    float g = wholeVol - (headVol + (midAny ? ClampedVolume(mid) : 0f)
                                          + ClampedVolume(suffix[j]));
                    if (g > gain)
                    {
                        gain = g;
                        found = true;
                        (bestAxis, bestLo, bestHi) = (axis, k, j);
                    }
                }
            }
        }
        if (!found || gain < VolumeSplitFrac * wholeVol)
            return false;
        float bMin = whole.Position[bestAxis], bSize = whole.Size[bestAxis];
        var parts = new List<Triangle>[3] { new(), new(), new() };
        foreach (var t in tris)
        {
            int b = Mathf.Clamp((int)((t.Centroid[bestAxis] - bMin) / bSize * Bins), 0, Bins - 1);
            parts[b < bestLo ? 0 : bestHi == 0 || b < bestHi ? 1 : 2].Add(t);
        }
        pieces = parts.Where(p => p.Count > 0).ToList();
        return pieces.Count >= 2;
    }

    private static float ClampedVolume(Aabb box) =>
        Mathf.Max(box.Size.X, MinThickness) *
        Mathf.Max(box.Size.Y, MinThickness) *
        Mathf.Max(box.Size.Z, MinThickness);

    // The hull is built about the region's box centre so its bounds are the box it replaces.
    private static void AddHull(List<Region> regions, string name, List<Triangle> tris)
    {
        if (tris.Count == 0)
            return; // e.g. the autogyro may lack a matching region, tolerate absences
        var centre = Enclose(tris).GetCenter();
        var cloud = new List<Vector3>(tris.Count * 3);
        foreach (var t in tris)
        {
            cloud.Add(t.A - centre);
            cloud.Add(t.B - centre);
            cloud.Add(t.C - centre);
        }
        regions.Add(new Region(name, ConvexHull.Of(cloud, MinThickness), centre));
    }

    // Clips every triangle against an axis-aligned plane (axis 0=x, 2=z),
    // keeping the pieces on the requested side, Sutherland–Hodgman against one
    // plane, re-fanned into triangles. Degenerate slivers are dropped.
    private static List<Triangle> ClipAxis(List<Triangle> tris, int axis, float plane, bool keepGreater)
    {
        var result = new List<Triangle>();
        Span<Vector3> poly = stackalloc Vector3[4];
        foreach (var t in tris)
        {
            int n = 0;
            Span<Vector3> corners = stackalloc[] { t.A, t.B, t.C };
            for (int i = 0; i < 3; i++)
            {
                var cur = corners[i];
                var next = corners[(i + 1) % 3];
                float dCur = keepGreater ? cur[axis] - plane : plane - cur[axis];
                float dNext = keepGreater ? next[axis] - plane : plane - next[axis];
                if (dCur >= 0f)
                    poly[n++] = cur;
                if (dCur > 0f != dNext > 0f && Mathf.Abs(dCur - dNext) > 1e-6f)
                    poly[n++] = cur.Lerp(next, dCur / (dCur - dNext));
            }
            for (int i = 2; i < n; i++)
            {
                var tri = new Triangle(poly[0], poly[i - 1], poly[i]);
                if ((tri.B - tri.A).Cross(tri.C - tri.A).LengthSquared() > 1e-8f)
                    result.Add(tri);
            }
        }
        return result;
    }

    private static Aabb EncloseOne(Triangle t) =>
        new Aabb(t.A, Vector3.Zero).Expand(t.B).Expand(t.C);

    private static Aabb Enclose(List<Triangle> tris)
    {
        var box = EncloseOne(tris[0]);
        for (int i = 1; i < tris.Count; i++)
            box = box.Merge(EncloseOne(tris[i]));
        return box;
    }

    /// <summary>One airframe hull: its part name, the hull centred on <c>Local.Origin</c>, and the
    /// physics shape over the same points.</summary>
    public readonly record struct Part(string Name, ConvexHull Hull, Transform3D Local, ConvexPolygonShape3D Shape);

    /// <summary>A named region before the physics shape exists (engine-free).</summary>
    public readonly record struct Region(string Name, ConvexHull Hull, Vector3 Centre);

    public readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C)
    {
        public Vector3 Centroid => (A + B + C) / 3f;
    }
}
