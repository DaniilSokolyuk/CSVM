using System;
using System.Collections.Generic;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>
/// One aircraft's own shape, as the ground shadow needs it: the triangles the original rasterises
/// top down into the modulate texture every frame, the bounding box the footprint comes
/// from, and the texture that raster, the spread and the ramp write. The node both are taken from
/// is the original's own choice, the whole model root for an AI aircraft and the <c>geometry</c>
/// child for the player's own. A propeller or rotor blur disc is held apart as its own group and
/// re-posed each frame, so it turns in the shadow as it turns on the model.
/// <see cref="GroundShadowPass"/> is the only caller and <see cref="GroundShadowLaw"/> holds the
/// spread and the ramp. Decode: docs/org/shadows.md.
/// </summary>
public sealed class GroundShadowSilhouette
{
    /// <summary>The gamez node the player's own silhouette and footprint are taken from, its
    /// airframe without the cockpit interior the whole model root also carries. An aircraft
    /// without one falls back to the model root, as the original does.</summary>
    public const string PlayerNode = "geometry";

    // Where the footprint's own extremes land in the texture, in texels of the original's own 32
    // from either edge. It keeps the silhouette off the border band, which the spread writes into
    // but never scans, and it is scaled with the step so the margin is the same ground either way.
    private const float RasterInset = 0.6f;

    private readonly Node3D _model;
    private readonly Part[] _parts;
    private readonly bool[] _covered;
    private readonly int[] _ramp;
    private readonly byte[] _texels;
    private readonly Image _image;
    private readonly ImageTexture _texture;

    private GroundShadowSilhouette(Node3D model, string node, Part[] parts, Aabb box)
    {
        int size = GroundShadowLaw.TextureSize;
        Node = node;
        _model = model;
        _parts = parts;
        _covered = new bool[size * size];
        _ramp = new int[size * size];
        _texels = new byte[size * size];
        _image = Image.CreateFromData(size, size, false, Image.Format.L8, _texels);
        _texture = ImageTexture.CreateFromImage(_image);
        Box = box;
    }

    /// <summary>The rasterised node's bounding box, in the model root's own frame, which is what
    /// the eight projected corners of the footprint are read off.</summary>
    public Aabb Box { get; }

    /// <summary>Which node the raster and the box came from, the model root's own name unless
    /// this is the player's aircraft and it carries a <see cref="PlayerNode"/> child.</summary>
    public string Node { get; }

    /// <summary>The live modulate texture, white where nothing covers the ground.</summary>
    public Texture2D Texture => _texture;

    /// <summary>How many triangles the raster walks per frame, for the suites and a perf read.
    /// </summary>
    public int TriangleCount
    {
        get
        {
            int triangles = 0;
            foreach (var part in _parts)
                triangles += part.Indices.Length / 3;

            return triangles;
        }
    }

    /// <summary>How many vertices it projects per frame, which the shared index buffer keeps well
    /// below three per triangle.</summary>
    public int VertexCount
    {
        get
        {
            int vertices = 0;
            foreach (var part in _parts)
                vertices += part.Vertices.Length;

            return vertices;
        }
    }

    /// <summary>How many of this shape's groups are re-posed each frame rather than held at the
    /// pose they were read at: one per propeller or rotor blur disc that draws. For the
    /// suites.</summary>
    public int TurningCount
    {
        get
        {
            int turning = 0;
            foreach (var part in _parts)
            {
                if (part.Pivot != null)
                    turning++;
            }

            return turning;
        }
    }

    /// <summary>What fraction of the texture the last raster covered, before the spread widens it.
    /// An aircraft silhouette covers far less of its own footprint than any inscribed ellipse.
    /// </summary>
    public float CoveredFraction
    {
        get
        {
            int covered = 0;
            foreach (bool texel in _covered)
            {
                if (texel)
                    covered++;
            }

            return covered / (float)_covered.Length;
        }
    }

    /// <summary>This caster's shape, from the node the original would rasterise: the
    /// <c>geometry</c> child for the player's own aircraft and the whole model root for every
    /// other. Both the triangles and the box come out in the model root's frame, so the pose that
    /// root is drawn at carries them into the world.</summary>
    public static GroundShadowSilhouette For(Node3D model, bool isPlayer)
    {
        var node = (isPlayer ? FindByName(model, PlayerNode) : null) ?? model;
        var at = RelativeTo(node, model);
        var still = new Group(null);
        var groups = new List<Group> { still };
        Aabb box = default;
        bool any = false;
        Collect(node, at, at, true, still, groups, ref box, ref any);
        var parts = new List<Part>();
        foreach (var group in groups)
        {
            if (group.Pivot == null || group.Indices.Count > 0)
                parts.Add(new Part(group.Pivot, group.Vertices.ToArray(), group.Indices.ToArray()));
        }

        return new GroundShadowSilhouette(
            model, node.Name.ToString(), parts.ToArray(), box);
    }

    /// <summary>Is the texel at a normalized position inside the footprint covered? For the
    /// suites, which read the silhouette's own shape rather than the drawn pixels.</summary>
    public bool CoveredAt(float u, float v)
    {
        int size = GroundShadowLaw.TextureSize;
        int x = Math.Clamp(Mathf.FloorToInt(u * size), 0, size - 1);
        int y = Math.Clamp(Mathf.FloorToInt(v * size), 0, size - 1);
        return _covered[(y * size) + x];
    }

    /// <summary>Rebuild the texture for one frame: every triangle flattened onto the ground along
    /// the projection direction, filled into the coverage mask, then the original's fixed spread
    /// and its white-to-colour ramp. The footprint's scale about its origin cancels out of the
    /// texel mapping, so the unscaled footprint is what this takes.</summary>
    public void Raster(Transform3D pose, float groundY, Vector3 direction, Aabb footprint)
    {
        int size = GroundShadowLaw.TextureSize;
        Array.Clear(_covered, 0, _covered.Length);
        if (footprint.Size.X > 0f && footprint.Size.Z > 0f)
        {
            foreach (var part in _parts)
                RasterPart(part, pose * Posed(part), groundY, direction, footprint);
        }

        GroundShadowLaw.Spread(_covered, size, size, _ramp, GroundShadowLaw.TexelScale);
        for (int i = 0; i < _ramp.Length; i++)
            _texels[i] = (byte)Mathf.RoundToInt(GroundShadowLaw.Coverage(_ramp[i]) * 255f);
        _image.SetData(size, size, false, Image.Format.L8, _texels);
        _texture.Update(_image);
    }

    // The transform of a node relative to an ancestor, walked through the parents so it is
    // correct before the subtree joins the scene tree.
    private static Transform3D RelativeTo(Node3D node, Node3D root)
    {
        var at = Transform3D.Identity;
        for (Node3D? n = node; n != null && n != root; n = n.GetParent() as Node3D)
            at = n.Transform * at;
        return at;
    }

    // The first node under a root whose Godot name or gamez `cs_name` meta matches, the same two
    // names every other resolution against a built tree reads.
    private static Node3D? FindByName(Node root, string name)
    {
        if (root is Node3D found
            && (root.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)
                || (root.HasMeta(Mech3.AnimRuntime.NameMeta)
                    && root.GetMeta(Mech3.AnimRuntime.NameMeta).AsString()
                        .Equals(name, StringComparison.OrdinalIgnoreCase))))
        {
            return found;
        }

        for (int i = 0, count = root.GetChildCount(); i < count; i++)
        {
            if (FindByName(root.GetChild(i), name) is { } hit)
                return hit;
        }

        return null;
    }

    // The subtree's own bounding box, and the triangles of the part of it that draws, split into
    // the still airframe and one group per propeller or rotor blur disc.
    // ⚠ The box and the triangles differ, and deliberately: the box is the node's, which the
    // original reads off the model whatever is shown and whatever angle a disc is turned to, while
    // a hidden node rasterises nothing, so a torn damage panel widens no footprint and casts no
    // silhouette until it is shown.
    private static void Collect(Node node, Transform3D at, Transform3D within, bool drawn,
        Group group, List<Group> groups, ref Aabb box, ref bool any)
    {
        drawn = drawn && node is not Node3D { Visible: false };
        if (node is Node3D turning
            && Mech3.PropParts.IsDynamic(Mech3.PropParts.Classify(turning.Name.ToString())))
        {
            group = new Group(turning);
            groups.Add(group);
            within = Transform3D.Identity;
        }

        if (node is MeshInstance3D instance && instance.Mesh is { } mesh)
        {
            var local = instance.GetAabb();
            var here = new Aabb(at * local.GetEndpoint(0), Vector3.Zero);
            for (int i = 1; i < 8; i++)
                here = here.Expand(at * local.GetEndpoint(i));
            box = any ? box.Merge(here) : here;
            any = true;
            if (drawn)
                Surfaces(mesh, within, group);
        }

        for (int i = 0, count = node.GetChildCount(); i < count; i++)
        {
            var child = node.GetChild(i);
            var step = child is Node3D spatial ? spatial.Transform : Transform3D.Identity;
            Collect(child, at * step, within * step, drawn, group, groups, ref box, ref any);
        }
    }

    // One mesh's triangle surfaces, carried into the caster's frame and indexed. The built
    // surfaces are plain triangle lists, so a corner shared by six polygons would otherwise be
    // projected six times a frame. A surface of anything else (the light-point clouds a model can
    // carry) draws no polygon and casts no silhouette.
    private static void Surfaces(Mesh mesh, Transform3D at, Group group)
    {
        var shared = group.Shared;
        var vertices = group.Vertices;
        var indices = group.Indices;
        var built = mesh as ArrayMesh;
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            if (built != null
                && built.SurfaceGetPrimitiveType(surface) != Mesh.PrimitiveType.Triangles)
                continue;
            var arrays = mesh.SurfaceGetArrays(surface);
            if (arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array() is not { Length: > 0 } points)
                continue;
            var written = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            int count = written.Length > 0 ? written.Length : points.Length;
            for (int i = 0; i < count; i++)
            {
                var point = at * points[written.Length > 0 ? written[i] : i];
                if (!shared.TryGetValue(point, out int index))
                {
                    index = vertices.Count;
                    vertices.Add(point);
                    shared[point] = index;
                }

                indices.Add(index);
            }
        }
    }

    // Where one group sits in the model root's frame this frame. The still airframe was read in
    // that frame already; a blur disc is read in its own, so its live chain is what carries the
    // angle it is turned to now into the raster.
    private Transform3D Posed(Part part) =>
        part.Pivot is { } pivot && GodotObject.IsInstanceValid(pivot)
            ? RelativeTo(pivot, _model)
            : Transform3D.Identity;

    // One group's triangles into the coverage mask. Pose, flattening and footprint collapse into
    // one affine map per group per frame, so a vertex costs two dot products rather than a
    // transform, a projection and a division.
    private void RasterPart(Part part, Transform3D pose, float groundY, Vector3 direction,
        Aabb footprint)
    {
        int size = GroundShadowLaw.TextureSize;
        float inset = RasterInset * GroundShadowLaw.TexelScale;
        float perX = (size - (2f * inset)) / footprint.Size.X;
        float perZ = (size - (2f * inset)) / footprint.Size.Z;
        float alongX = direction.X / direction.Y;
        float alongZ = direction.Z / direction.Y;
        var basis = pose.Basis;
        var rowX = new Vector3(basis.X.X, basis.Y.X, basis.Z.X);
        var rowY = new Vector3(basis.X.Y, basis.Y.Y, basis.Z.Y);
        var rowZ = new Vector3(basis.X.Z, basis.Y.Z, basis.Z.Z);
        var perVertexX = (rowX - (rowY * alongX)) * perX;
        var perVertexZ = (rowZ - (rowY * alongZ)) * perZ;
        float atX = inset + (perX
            * (pose.Origin.X + ((groundY - pose.Origin.Y) * alongX) - footprint.Position.X));
        float atZ = inset + (perZ
            * (pose.Origin.Z + ((groundY - pose.Origin.Y) * alongZ) - footprint.Position.Z));
        var vertices = part.Vertices;
        var projected = part.Projected;
        for (int i = 0; i < vertices.Length; i++)
        {
            var vertex = vertices[i];
            projected[i] = new Vector2(
                (perVertexX.X * vertex.X) + (perVertexX.Y * vertex.Y) + (perVertexX.Z * vertex.Z) + atX,
                (perVertexZ.X * vertex.X) + (perVertexZ.Y * vertex.Y) + (perVertexZ.Z * vertex.Z) + atZ);
        }

        var indices = part.Indices;
        for (int i = 0; i + 2 < indices.Length; i += 3)
            Fill(projected[indices[i]], projected[indices[i + 1]], projected[indices[i + 2]]);
    }

    // One flattened triangle into the coverage mask: a texel is covered when its own centre lies
    // inside, so the union over a model's triangles is exactly its silhouette. ⚠ Both windings are
    // filled where the original culls one, a departure docs/org/shadows.md records.
    // ⚠ Keep the bounds and the edge walk written out. This runs per triangle per aircraft per
    // frame, and a debug build pays for every Mathf call: hoisting them out halved the pass.
    private void Fill(Vector2 a, Vector2 b, Vector2 c)
    {
        const int size = GroundShadowLaw.TextureSize;
        float area = ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));
        if (area == 0f)
            return;
        if (area < 0f)
            (b, c) = (c, b);
        float lowX = a.X;
        float highX = a.X;
        float lowY = a.Y;
        float highY = a.Y;
        if (b.X < lowX)
            lowX = b.X;
        if (b.X > highX)
            highX = b.X;
        if (c.X < lowX)
            lowX = c.X;
        if (c.X > highX)
            highX = c.X;
        if (b.Y < lowY)
            lowY = b.Y;
        if (b.Y > highY)
            highY = b.Y;
        if (c.Y < lowY)
            lowY = c.Y;
        if (c.Y > highY)
            highY = c.Y;
        if (lowX < 0f)
            lowX = 0f;
        if (lowY < 0f)
            lowY = 0f;
        if (highX > size)
            highX = size;
        if (highY > size)
            highY = size;
        int fromX = (int)(lowX + 0.5f);
        int toX = (int)(highX - 0.5f);
        int fromY = (int)(lowY + 0.5f);
        int toY = (int)(highY - 0.5f);
        if (fromX > toX || fromY > toY)
            return;

        // The three edge functions walked incrementally, one add per texel per edge, since each
        // is affine in the sample point.
        float alongX0 = a.Y - b.Y;
        float alongX1 = b.Y - c.Y;
        float alongX2 = c.Y - a.Y;
        float alongY0 = b.X - a.X;
        float alongY1 = c.X - b.X;
        float alongY2 = a.X - c.X;
        float at = fromX + 0.5f;
        float down = fromY + 0.5f;
        float edge0 = (alongY0 * (down - a.Y)) + (alongX0 * (at - a.X));
        float edge1 = (alongY1 * (down - b.Y)) + (alongX1 * (at - b.X));
        float edge2 = (alongY2 * (down - c.Y)) + (alongX2 * (at - c.X));
        for (int y = fromY; y <= toY; y++)
        {
            float row0 = edge0;
            float row1 = edge1;
            float row2 = edge2;
            int row = y * size;
            for (int x = fromX; x <= toX; x++)
            {
                if (row0 >= 0f && row1 >= 0f && row2 >= 0f)
                    _covered[row + x] = true;
                row0 += alongX0;
                row1 += alongX1;
                row2 += alongX2;
            }

            edge0 += alongY0;
            edge1 += alongY1;
            edge2 += alongY2;
        }
    }

    // One group of the shape as the raster walks it: the triangles in the frame of the node they
    // hang off, that node when it turns and null for the still airframe, and the projection
    // scratch the raster reuses rather than allocating one per frame.
    private sealed class Part
    {
        public Part(Node3D? pivot, Vector3[] vertices, int[] indices)
        {
            Pivot = pivot;
            Vertices = vertices;
            Indices = indices;
            Projected = new Vector2[vertices.Length];
        }

        public Node3D? Pivot { get; }

        public Vector3[] Vertices { get; }

        public int[] Indices { get; }

        public Vector2[] Projected { get; }
    }

    // One group under construction, with the corner dedup that keeps a vertex shared by six
    // polygons projected once. The dedup is per group, since two groups no longer share a frame.
    private sealed class Group
    {
        public Group(Node3D? pivot) => Pivot = pivot;

        public Node3D? Pivot { get; }

        public Dictionary<Vector3, int> Shared { get; } = new();

        public List<Vector3> Vertices { get; } = new();

        public List<int> Indices { get; } = new();
    }
}
