using System;
using System.Collections.Generic;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>
/// A closed convex hull over a point cloud: hull vertices, outward triangle faces and their
/// planes, plus the point and segment distance queries the fuse and blast passes ask. Engine-free
/// on purpose (plain structs only), so a suite can measure a hull's coverage against the mesh
/// without a physics server and a unit test can build one from a synthetic cloud. Thin clouds
/// (a wing slab, a fin) are padded to <c>minThickness</c> along each thin axis before hulling,
/// the same floor the box shapes applied per dimension, so a flat part still has a volume the
/// physics engine can collide with.
/// </summary>
public sealed class ConvexHull
{
    // Hull arithmetic runs on integer millimetres (the model unit is the metre): points closer
    // than this are one vertex, and every visibility test is an exact 64-bit volume sign.
    private const float Quantum = 1e-3f;

    private readonly Vector3[] _normals;
    private readonly float[] _offsets;

    private ConvexHull(Vector3[] points, int[] faces)
    {
        Points = points;
        Faces = faces;
        _normals = new Vector3[faces.Length / 3];
        _offsets = new float[faces.Length / 3];
        var inside = Vector3.Zero;
        foreach (var p in points)
            inside += p;
        inside /= points.Length;
        float volume = 0f;
        for (int f = 0; f < faces.Length; f += 3)
        {
            var a = points[faces[f]];
            var b = points[faces[f + 1]];
            var c = points[faces[f + 2]];
            var n = (b - a).Cross(c - a).Normalized();
            _normals[f / 3] = n;
            _offsets[f / 3] = n.Dot(a);
            volume += (a - inside).Dot((b - inside).Cross(c - inside)) / 6f;
        }
        Volume = volume;
        Bounds = new Aabb(points[0], Vector3.Zero);
        foreach (var p in points)
            Bounds = Bounds.Expand(p);
        Edges = UniqueEdges(faces);
    }

    /// <summary>The hull vertices, every one a corner of at least one face.</summary>
    public Vector3[] Points { get; }

    /// <summary>Outward-wound vertex index triples, three per face.</summary>
    public int[] Faces { get; }

    /// <summary>Each hull edge once, as an index pair (drawing).</summary>
    public (int A, int B)[] Edges { get; }

    /// <summary>The axis-aligned box around the hull (the box shape this hull replaces).</summary>
    public Aabb Bounds { get; }

    /// <summary>Enclosed volume in cubic metres.</summary>
    public float Volume { get; }

    /// <summary>Hulls <paramref name="cloud"/>, padding any axis thinner than
    /// <paramref name="minThickness"/> out to it first. A cloud too degenerate to hull even after
    /// padding (fewer than four non-coplanar points) falls back to its padded bounding box.</summary>
    public static ConvexHull Of(IReadOnlyList<Vector3> cloud, float minThickness)
    {
        if (cloud.Count == 0)
            throw new ArgumentException("a hull needs at least one point", nameof(cloud));
        var pts = Dedupe(cloud);
        var bounds = new Aabb(pts[0], Vector3.Zero);
        foreach (var p in pts)
            bounds = bounds.Expand(p);
        for (int axis = 0; axis < 3; axis++)
        {
            float grow = (minThickness - bounds.Size[axis]) * 0.5f;
            if (grow <= 0f)
                continue;
            var shift = Vector3.Zero;
            shift[axis] = grow;
            var padded = new List<Vector3>(pts.Count * 2);
            foreach (var p in pts)
            {
                padded.Add(p + shift);
                padded.Add(p - shift);
            }
            pts = Dedupe(padded);
            bounds.Position -= shift;
            bounds.Size += shift * 2f;
        }
        return Incremental(pts) ?? BoxHull(bounds);
    }

    /// <summary>Whether <paramref name="p"/> lies inside every face plane, within
    /// <paramref name="tolerance"/> metres.</summary>
    public bool Contains(Vector3 p, float tolerance) => SignedDistance(p) <= tolerance;

    /// <summary>Distance from <paramref name="p"/> to the hull surface (0 inside) and the nearest
    /// surface point (<paramref name="p"/> itself when inside).</summary>
    public float Distance(Vector3 p, out Vector3 nearest)
    {
        if (SignedDistance(p) <= 0f)
        {
            nearest = p;
            return 0f;
        }
        float best = float.PositiveInfinity;
        nearest = p;
        for (int f = 0; f < Faces.Length; f += 3)
        {
            var q = ClosestOnTriangle(p, Points[Faces[f]], Points[Faces[f + 1]], Points[Faces[f + 2]]);
            float d2 = q.DistanceSquaredTo(p);
            if (d2 < best)
            {
                best = d2;
                nearest = q;
            }
        }
        return Mathf.Sqrt(best);
    }

    private static List<Vector3> Dedupe(IReadOnlyList<Vector3> cloud)
    {
        var seen = new HashSet<(int, int, int)>();
        var result = new List<Vector3>(cloud.Count);
        foreach (var p in cloud)
        {
            var key = (Mathf.RoundToInt(p.X / Quantum), Mathf.RoundToInt(p.Y / Quantum), Mathf.RoundToInt(p.Z / Quantum));
            if (seen.Add(key))
                result.Add(p);
        }
        return result;
    }

    private static ConvexHull BoxHull(Aabb box)
    {
        var pts = new Vector3[8];
        for (int i = 0; i < 8; i++)
            pts[i] = box.Position + new Vector3((i & 1) * box.Size.X, ((i >> 1) & 1) * box.Size.Y, ((i >> 2) & 1) * box.Size.Z);
        var faces = new[]
        {
            0, 2, 1, 1, 2, 3, // -z
            4, 5, 6, 5, 7, 6, // +z
            0, 1, 4, 1, 5, 4, // -y
            2, 6, 3, 3, 6, 7, // +y
            0, 4, 2, 2, 4, 6, // -x
            1, 3, 5, 3, 7, 5, // +x
        };
        return new ConvexHull(pts, faces);
    }

    // Incremental hull on the quantised (millimetre integer) points with exact 64-bit volume
    // signs, so no epsilon can leave a fold: a seed tetrahedron, then every further point
    // strictly outside the current hull replaces the faces it can see with a fan over the
    // horizon. Null when the cloud has no non-degenerate tetrahedron.
    private static ConvexHull? Incremental(List<Vector3> pts)
    {
        var q = new (long X, long Y, long Z)[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            q[i] = ((long)Mathf.Round(pts[i].X / Quantum), (long)Mathf.Round(pts[i].Y / Quantum), (long)Mathf.Round(pts[i].Z / Quantum));
        if (!SeedTetrahedron(q, out int i0, out int i1, out int i2, out int i3))
            return null;
        var faces = new List<Face>
        {
            Face.Of(q, i0, i1, i2, i3), Face.Of(q, i0, i1, i3, i2),
            Face.Of(q, i0, i2, i3, i1), Face.Of(q, i1, i2, i3, i0),
        };
        // Far points first: the hull settles early and most later points fail every face test
        // in one pass, which is the whole cost of the interior of a mesh.
        var order = new int[q.Length];
        var far = new long[q.Length];
        long cx = 0, cy = 0, cz = 0;
        foreach (var v in q)
        {
            cx += v.X;
            cy += v.Y;
            cz += v.Z;
        }
        cx /= q.Length;
        cy /= q.Length;
        cz /= q.Length;
        for (int i = 0; i < q.Length; i++)
        {
            order[i] = i;
            far[i] = -((q[i].X - cx) * (q[i].X - cx) + (q[i].Y - cy) * (q[i].Y - cy) + (q[i].Z - cz) * (q[i].Z - cz));
        }
        Array.Sort(far, order);
        var visible = new List<int>();
        var edges = new HashSet<(int, int)>();
        foreach (int p in order)
        {
            if (p == i0 || p == i1 || p == i2 || p == i3)
                continue;
            visible.Clear();
            for (int f = 0; f < faces.Count; f++)
            {
                if (faces[f].Sees(q[p]))
                    visible.Add(f);
            }
            if (visible.Count == 0)
                continue;
            edges.Clear();
            foreach (int f in visible)
            {
                var face = faces[f];
                edges.Add((face.A, face.B));
                edges.Add((face.B, face.C));
                edges.Add((face.C, face.A));
            }
            var kept = new List<Face>(faces.Count + edges.Count);
            int next = 0;
            for (int f = 0; f < faces.Count; f++)
            {
                if (next < visible.Count && visible[next] == f)
                    next++;
                else
                    kept.Add(faces[f]);
            }
            foreach (var (a, b) in edges)
            {
                if (!edges.Contains((b, a)))
                    kept.Add(Face.Wound(q, a, b, p)); // the horizon edge keeps its winding
            }
            faces = kept;
        }
        var remap = new Dictionary<int, int>();
        var points = new List<Vector3>();
        var tris = new int[faces.Count * 3];
        for (int f = 0; f < faces.Count; f++)
        {
            tris[f * 3] = Remap(faces[f].A);
            tris[f * 3 + 1] = Remap(faces[f].B);
            tris[f * 3 + 2] = Remap(faces[f].C);
        }
        return new ConvexHull(points.ToArray(), tris);

        int Remap(int i)
        {
            if (!remap.TryGetValue(i, out int j))
            {
                j = points.Count;
                points.Add(new Vector3(q[i].X, q[i].Y, q[i].Z) * Quantum);
                remap[i] = j;
            }
            return j;
        }
    }

    // Six times the signed volume of the tetrahedron (a, b, c, p): positive when p is on the
    // side the face's winding points away from. Exact in 64 bits for millimetre coordinates
    // inside a few hundred metres.
    private static long Volume6((long X, long Y, long Z)[] q, int a, int b, int c, int p)
    {
        long abx = q[b].X - q[a].X, aby = q[b].Y - q[a].Y, abz = q[b].Z - q[a].Z;
        long acx = q[c].X - q[a].X, acy = q[c].Y - q[a].Y, acz = q[c].Z - q[a].Z;
        long apx = q[p].X - q[a].X, apy = q[p].Y - q[a].Y, apz = q[p].Z - q[a].Z;
        long nx = aby * acz - abz * acy;
        long ny = abz * acx - abx * acz;
        long nz = abx * acy - aby * acx;
        return nx * apx + ny * apy + nz * apz;
    }

    private static bool SeedTetrahedron((long X, long Y, long Z)[] q, out int i0, out int i1, out int i2, out int i3)
    {
        i0 = 0;
        i1 = i2 = i3 = -1;
        for (int i = 1; i < q.Length && i1 < 0; i++)
        {
            if (q[i] != q[i0])
                i1 = i;
        }
        if (i1 < 0)
            return false;
        for (int i = 1; i < q.Length && i2 < 0; i++)
        {
            long abx = q[i1].X - q[i0].X, aby = q[i1].Y - q[i0].Y, abz = q[i1].Z - q[i0].Z;
            long acx = q[i].X - q[i0].X, acy = q[i].Y - q[i0].Y, acz = q[i].Z - q[i0].Z;
            if (aby * acz - abz * acy != 0 || abz * acx - abx * acz != 0 || abx * acy - aby * acx != 0)
                i2 = i;
        }
        if (i2 < 0)
            return false;
        for (int i = 1; i < q.Length && i3 < 0; i++)
        {
            if (Volume6(q, i0, i1, i2, i) != 0)
                i3 = i;
        }
        return i3 >= 0;
    }

    private static (int A, int B)[] UniqueEdges(int[] faces)
    {
        var seen = new HashSet<(int, int)>();
        var edges = new List<(int, int)>();
        for (int f = 0; f < faces.Length; f += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = faces[f + k], b = faces[f + (k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                if (seen.Add(key))
                    edges.Add(key);
            }
        }
        return edges.ToArray();
    }

    // Ericson's closest-point-on-triangle, by Voronoi region of the query point.
    private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        float d1 = ab.Dot(ap), d2 = ac.Dot(ap);
        if (d1 <= 0f && d2 <= 0f)
            return a;
        var bp = p - b;
        float d3 = ab.Dot(bp), d4 = ac.Dot(bp);
        if (d3 >= 0f && d4 <= d3)
            return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            return a + ab * (d1 / (d1 - d3));
        var cp = p - c;
        float d5 = ab.Dot(cp), d6 = ac.Dot(cp);
        if (d6 >= 0f && d5 <= d6)
            return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    // The largest signed plane distance: at most 0 inside the hull.
    private float SignedDistance(Vector3 p)
    {
        float max = float.NegativeInfinity;
        for (int i = 0; i < _normals.Length; i++)
            max = Mathf.Max(max, _normals[i].Dot(p) - _offsets[i]);
        return max;
    }

    // A hull face with its integer plane cached, so a visibility test is one dot product.
    private readonly record struct Face(int A, int B, int C, long Nx, long Ny, long Nz, long D)
    {
        // Wound so the outward side faces away from the fourth seed point.
        public static Face Of((long X, long Y, long Z)[] q, int a, int b, int c, int inside) =>
            Volume6(q, a, b, c, inside) > 0 ? Wound(q, a, c, b) : Wound(q, a, b, c);

        public static Face Wound((long X, long Y, long Z)[] q, int a, int b, int c)
        {
            long abx = q[b].X - q[a].X, aby = q[b].Y - q[a].Y, abz = q[b].Z - q[a].Z;
            long acx = q[c].X - q[a].X, acy = q[c].Y - q[a].Y, acz = q[c].Z - q[a].Z;
            long nx = aby * acz - abz * acy;
            long ny = abz * acx - abx * acz;
            long nz = abx * acy - aby * acx;
            return new Face(a, b, c, nx, ny, nz, nx * q[a].X + ny * q[a].Y + nz * q[a].Z);
        }

        public bool Sees((long X, long Y, long Z) p) => Nx * p.X + Ny * p.Y + Nz * p.Z > D;
    }
}
