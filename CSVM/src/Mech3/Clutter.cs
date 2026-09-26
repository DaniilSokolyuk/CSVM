using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CSVM.Utils;
using Godot;

namespace CSVM.Mech3;

/// <summary>
/// Stamps the boot script's clutter templates onto every placed polygon painted with a template's
/// ground texture, once per integer repeat of that texture across each triangle. Placement is
/// computed in texture space, so clutter rotates, mirrors and stretches with the painted ground.
/// Decode: docs/org/clutter.md. Authored side: docs/formats/clutter.md and templates.md.
/// Plumbing: this module's entry in docs/architecture.md. The remake-only rules (no world grid,
/// the fixed placement seed, the seen dedup, shared collision shapes) are on their own members.
/// ⚠ Do not add a world-space grid or a global clutter origin. The original has neither, and a
/// fixed X/Z step costs C1 about four times its trees.
/// ⚠ no_clutter does not mean bare ground. Where two coplanar layers are painted over each other
/// it selects which one decorates, and flagged means the layer beneath stamps instead.
/// </summary>
public sealed class ClutterBuilder
{
    /// <summary>Node metadata key under which a clutter root (or a map-edge extension cell)
    /// holds the shared <see cref="ConcavePolygonShape3D"/>s its bodies reference by RID.
    /// The physics server holds RIDs, not Refs, without this anchor the shapes would be
    /// collected while bodies still point at them.</summary>
    public static readonly StringName SharedShapeMeta = "csvm_clutter_shapes";

    /// <summary>Meta on each collision region body: the <see cref="Rect2"/> of world x/z its
    /// placements' origins lie in, so a crater cull skips the regions it cannot reach without
    /// reading a single shape.</summary>
    public static readonly StringName CollisionCellMeta = "csvm_clutter_cell";

    // The sprite shader's distance-fog block, emitted only into the fogged variant.
    private const string FogLines =
        "    vec3 fog_world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;\n"
        + "    float fog_amt = csky_fog_amount(fog_world, CAMERA_POSITION_WORLD);\n"
        + "    ALBEDO = mix(ALBEDO, csky_fog_color, csky_fog_on * fog_amt);\n";

    // The SphericalY pose, single-sourced with every other facade population so the law cannot
    // drift. Emitted into the spherical variant alone; a CylindricalY card poses off its own
    // vertical and calls nothing from it.
    private const string FacadeInclude = "#include \"res://shaders/csky_facade.gdshaderinc\"";

    // The cutout the scissor variant applies, at Godot's own default threshold. ⚠ Emit it only when
    // the archive calls the card's alpha hard: a cut through soft ink both erases what sits under
    // the threshold and solidifies what sits above it, and the original cuts nothing at all.
    private const string ScissorLine = "    ALPHA_SCISSOR_THRESHOLD = 0.5;\n";

    // The seed of the substitute/scale stream: the constant the original seeds its own world
    // build with, borrowed as a label rather than as a claim, our PRNG, traversal and draw count
    // all differ, so the sequences cannot and do not agree. What IS reproduced is the property
    // that matters, the placement is a function of the data alone, identical on every launch,
    // pinned session or not. See the note at the draw site.
    private const int PlacementSeed = unchecked((int)0x8EA91836);

    // The largest integer UV lattice one triangle may span before it is refused and counted.
    // 64 × 64 repeats of the ground texture across a single triangle; the widest measured span in
    // the shipped data is a handful, so this is a tripwire for a corrupt UV array, not a knob.
    private const long MaxLatticeCells = 4096;

    // One shared shape per decoration mesh, attached to the region body by RID with a per-placement
    // transform. Expanding per placement built C5 a 2.5M-triangle trimesh whose BVH alone cost 3.4 s.
    // Only 3D decorations are collidable; sprite cards deliberately get no collider.
    // ⚠ Never call a ShapeOwner* method on these bodies. The node does not know about RID-attached
    // shapes, so CollisionObject3D._update_shapes() would clear the body and re-add nothing.
    private const float CollisionRegion = 1024f;

    private readonly GameZ _gamez;
    private readonly TextureArchive _textures;
    private readonly SceneBuilder? _scene;

    // The chapter's templates.zrd, or null when it has none (C1C/C2B ship an empty one, which is
    // a spec with no blocks, a different thing, and the reader keeps them apart). A decoration
    // with no block here is retail-data-normal and means every default: no substitution, scale 1.
    private readonly ClutterTemplateSpec? _props;

    // Every kind this Build will export, INCLUDING the substitution-only kinds minted in
    // ResolveProperties, models no template scatters directly, which exist solely as the target
    // of somebody else's roll (43 of C5's 78 blocks). They carry no CellPlacements, so they are
    // deliberately absent from Template.Kinds: the lattice walk must never treat one as a source.
    private readonly List<Kind> _allKinds = new();

    // Model name → the one kind a substitution to that name resolves to. The original resolves a
    // target through the engine's global model table, i.e. to ONE model however many templates
    // mention it; first-seen wins here, which is the same statement over a deterministic walk.
    private readonly Dictionary<string, Kind> _kindsByModel = new(StringComparer.OrdinalIgnoreCase);

    // One sprite shader per (lit, fogged) pair the decoration models actually ask for.
    private readonly Dictionary<int, Shader> _shaders = new();

    // Shared collision shapes of the last collidable Build, keyed by decoration MeshIndex.
    private Dictionary<int, ConcavePolygonShape3D>? _solidShapes;

    // What the UV-lattice walk skipped and what it asserted, for the one summary line Build
    // logs. Replaced per Build, never accumulated across two.
    private LatticeStats _stats = new();

    /// <param name="scene">The world's SceneBuilder, for the 3D-decoration path. Null leaves only
    /// the sprite path.</param>
    /// <param name="props">The chapter's <c>templates.zrd</c> (docs/formats/templates.md), driving
    /// <c>substitute</c>, <c>scale_range</c> and <c>far_fade_range</c>. Null builds every decoration
    /// at its authored model and size, never fading, so a caller with no reader still gets clutter
    /// rather than an exception.</param>
    public ClutterBuilder(GameZ gamez, TextureArchive textures, SceneBuilder? scene = null,
        ClutterTemplateSpec? props = null)
    {
        _gamez = gamez;
        _textures = textures;
        _scene = scene;
        _props = props;
    }

    /// <summary>Why <see cref="UvTriangle.Build"/> refused a triangle. The two faults are
    /// independent: a fan or strip artifact of an n-gon with repeated or collinear corners has
    /// zero WORLD area and a perfectly ordinary UV area, so a UV-area guard alone lets it through
    /// and its meaningless affine map, both axes collapsed onto a line, plants a row of trees
    /// along that line. There are 1,655 of them on C1's <c>terpat02</c> alone.</summary>
    public enum UvTriangleFault
    {
        /// <summary>The triangle has world-space area but no invertible UV map.</summary>
        ZeroUvArea,

        /// <summary>The triangle's three world vertices are collinear or coincident.</summary>
        ZeroWorldArea,
    }

    /// <summary>Total decoration sprites (billboard cards) placed by the last Build.</summary>
    public int InstanceCount { get; private set; }

    /// <summary>Card kinds the last Build drew blended, and card kinds it cut to a hard silhouette,
    /// the census an isolation capture is read against. Counts KINDS, since a kind is one MultiMesh
    /// and one draw, which is the unit a draw-order question is asked in.</summary>
    public int BlendCardKinds { get; private set; }

    /// <inheritdoc cref="BlendCardKinds"/>
    public int ScissorCardKinds { get; private set; }

    /// <summary>Total 3D decorations (city-block buildings, parked cars) placed by the last
    /// Build. Zero on every chapter whose templates carry only sprites.</summary>
    public int SolidCount { get; private set; }

    /// <summary>Collision triangles built for the 3D decorations by the last Build (0 when the
    /// build was not collidable). Since the shapes are shared this counts the DISTINCT
    /// triangles, ~2.3k in C5, not the 2.55M a per-placement expansion would produce.</summary>
    public int SolidCollisionTriangles { get; private set; }

    /// <summary>Per-kind counts of the last Build, e.g. "firtree1.tif ×4980".</summary>
    public string Summary { get; private set; } = "";

    /// <summary>The decoration kinds of the last Build (null until Build placed something).</summary>
    public IReadOnlyList<KindExport>? ExportedKinds { get; private set; }

    /// <summary>Of <see cref="SolidCollisionTriangles"/>, those from a polygon clearing
    /// <c>SHOW_BACKFACE</c>: the size of the divergence left by these shapes staying two-sided while
    /// the world's honour the flag. The reason they do is on <c>BuildSolidCollision</c>.</summary>
    public int SolidCollisionOneSidedTriangles { get; private set; }

    /// <summary>Distinct collision shapes built by the last Build (one per decoration mesh).</summary>
    public int SolidCollisionShapes { get; private set; }

    /// <summary>Shape attachments made by the last Build, one per collidable 3D decoration.</summary>
    public int SolidCollisionInstances { get; private set; }


    /// <summary>The chapter's registered clutter template names, read from the interp extraction's
    /// <c>AddClutterTemplates</c> lines. Empty when the file or script is missing.
    /// ⚠ Return every registered name, unfiltered. Which district dresses a given patch is the
    /// per-polygon <c>no_clutter</c> gate's decision, never a curated exclusion list here.</summary>
    public static List<string> TemplateNames(string interpPath, string chapter)
    {
        var names = new List<string>();
        if (!File.Exists(interpPath))
            return names;
        var wanted = $"support\\{chapter.ToLowerInvariant()}\\adjust.gw";
        using var doc = JsonDocument.Parse(File.ReadAllBytes(interpPath));
        foreach (var script in doc.RootElement.EnumerateArray())
        {
            if (!script.TryGetProperty("name", out var n)
                || !string.Equals(n.GetString(), wanted, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var line in script.GetProperty("lines").EnumerateArray())
            {
                var parts = (line.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] == "AddClutterTemplates")
                    for (int i = 1; i < parts.Length; i++)
                        names.Add(parts[i]);
            }
        }
        return names;
    }

    /// <summary><c>--clutter-templates=</c>'s replacement for <see cref="TemplateNames"/>: the
    /// caller's names, filtered to those this gamez carries a template root for. Building one
    /// district alone is what makes it an A/B instrument. It logs what resolved and what did not,
    /// because a name no chapter carries is normal and would otherwise read as an empty district.
    /// ⚠ It does not bypass the <c>no_clutter</c> gate, which is per polygon, not per
    /// template.</summary>
    public static List<string> OverrideTemplateNames(GameZ gamez, IReadOnlyList<string> requested)
    {
        var resolved = new List<string>();
        var absent = new List<string>();
        foreach (var name in requested)
            (FindTemplateRoot(gamez, name) != null ? resolved : absent).Add(name);
        Log.Info("world", $"clutter: --clutter-templates={string.Join(",", requested)} replaces the chapter's registered set (the per-polygon no_clutter gate still applies), in gamez: {(resolved.Count > 0 ? string.Join(",", resolved) : "(none)")}; not carried by this chapter: {(absent.Count > 0 ? string.Join(",", absent) : "(none)")} (normal in the original's data, not an error)");
        return resolved;
    }

    /// <summary>Finds a template root by name. Null when the gamez ships none, which is normal:
    /// C2B registers three templates it does not carry. Static and shared with
    /// <c>CSVM.Effects.FogVolumeClutter</c>, so the two lookups cannot diverge.
    /// ⚠ Match only parentless nodes. The world carries unrelated leaf nodes under the same
    /// names, and a root hangs off nothing because the boot script loads it by name.</summary>
    public static GameZNode? FindTemplateRoot(GameZ gamez, string name)
    {
        var isChild = new bool[gamez.Nodes.Count];
        foreach (var n in gamez.Nodes)
            foreach (var c in n.Children)
                if (c >= 0 && c < isChild.Length)
                    isChild[c] = true;
        foreach (var n in gamez.Nodes)
            if (!isChild[n.Index] && n.Kind == "Object3d"
                && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase))
                return n;
        return null;
    }

    /// <summary>The first node at or under <paramref name="node"/> carrying a non-empty mesh,
    /// a template root's ground quad, or a decoration node's card/building. Public and static
    /// because it is the other half of resolving a template (with
    /// <see cref="FindTemplateRoot(GameZ, string)"/>), and the UV a decoration is stored at is
    /// only checkable against a worked example if both halves can be reached.</summary>
    public static GameZNode? FirstWithMesh(GameZ gamez, GameZNode node, bool includeSelf = true) =>
        FirstWithMesh(gamez, node, includeSelf, out _);

    /// <summary>The same search, also handing back the transform from <paramref name="node"/>'s
    /// own frame down to the node that carries the mesh, identity when that is the node itself.
    /// ⚠ A decoration model is a node chain, so take its position from here rather than from the
    /// chain's top node alone. C5's <c>w_lightglow</c> mesh sits 4.75 m up its own
    /// <c>w_lightglow.flt</c>, which is the height of the lamp head it belongs on.</summary>
    public static GameZNode? FirstWithMesh(GameZ gamez, GameZNode node, bool includeSelf,
        out Transform3D toMesh)
    {
        toMesh = Transform3D.Identity;
        if (includeSelf && node.MeshIndex >= 0 && node.MeshIndex < gamez.Meshes.Count
            && gamez.Meshes[node.MeshIndex].Polygons.Count > 0)
            return node;
        foreach (var c in node.Children)
        {
            if (c < 0 || c >= gamez.Nodes.Count)
                continue;
            var child = gamez.Nodes[c];
            if (FirstWithMesh(gamez, child, true, out var below) is { } found)
            {
                toMesh = (child.Local ?? Transform3D.Identity) * below;
                return found;
            }
        }
        return null;
    }

    /// <summary>Every mesh-bearing node under <paramref name="deco"/> other than
    /// <paramref name="drawn"/>, each with its transform into <paramref name="drawn"/>'s own frame.
    /// A decoration model is a node chain whose meshes sit on several nodes. The original draws
    /// the whole chain, so these are the rest of the building.
    /// ⚠ LOD alternates are excluded here exactly as <see cref="SceneBuilder"/> excludes them
    /// (nearest level only), or a block would draw its far silhouette inside itself.</summary>
    public static List<(int MeshIndex, Transform3D Local)> ExtraMeshes(
        GameZ gamez, GameZNode deco, GameZNode drawn)
    {
        var found = new List<(GameZNode Node, Transform3D Xf)>();
        // The decoration node's own local placed the stamp already, so the frame here is the one
        // below it. That is the frame the drawn mesh's own MeshLift was measured in.
        if (drawn.Index == deco.Index)
            Walk(deco, Transform3D.Identity);
        else
            foreach (var c in deco.Children)
                if (c >= 0 && c < gamez.Nodes.Count)
                    Walk(gamez.Nodes[c], Transform3D.Identity);
        var parts = new List<(int, Transform3D)>();
        int at = found.FindIndex(f => f.Node.Index == drawn.Index);
        if (at < 0)
            return parts;
        var toLocal = found[at].Xf.AffineInverse();
        foreach (var (node, xf) in found)
            if (node.Index != drawn.Index)
                parts.Add((node.MeshIndex, toLocal * xf));
        return parts;

        void Walk(GameZNode node, Transform3D xf)
        {
            if (WorldBuilder.SkipWorldNode(node) || (node.Kind == "Lod" && node.LodRangeMin != 0f))
                return;
            var here = node.Local is { } local ? xf * local : xf;
            if (node.MeshIndex >= 0 && node.MeshIndex < gamez.Meshes.Count
                && gamez.Meshes[node.MeshIndex].Polygons.Count > 0)
                found.Add((node, here));
            foreach (var c in node.Children)
                if (c >= 0 && c < gamez.Nodes.Count)
                    Walk(gamez.Nodes[c], here);
        }
    }

    /// <summary>The template's ground quad as the original's decoration projection uses it: the
    /// polygon's plane, and the affine map from a local position on it to the polygon's own
    /// interpolated texture UV.
    /// ⚠ Do not reduce this to a scalar tiling period, and do not key anything off corner order.
    /// Two signed axes are the least that describes the four non-square and UV-mirrored templates,
    /// and the winding differs between templates while the parameterisation does not.</summary>
    public static GroundQuad? GroundInfo(GameZ gamez, GameZNode ground)
    {
        var mesh = gamez.Meshes[ground.MeshIndex];
        var tex = FirstTexture(gamez, mesh);
        if (tex == null || mesh.Vertices.Count == 0)
            return null;
        Vector3 min = mesh.Vertices[0], max = mesh.Vertices[0];
        foreach (var v in mesh.Vertices)
        {
            min = min.Min(v);
            max = max.Max(v);
        }
        float extX = max.X - min.X, extZ = max.Z - min.Z;
        if (Mathf.Max(extX, extZ) < 1f)
            return null;

        foreach (var poly in mesh.Polygons)
        {
            if (poly.UvCoords == null || poly.UvCoords.Count < 3 || poly.VertexIndices.Count < 3)
                continue;
            Vector3 a = mesh.Vertices[poly.VertexIndices[0]],
                    b = mesh.Vertices[poly.VertexIndices[1]],
                    c = mesh.Vertices[poly.VertexIndices[2]];

            // Double throughout, rounded once in TryUv. Rounding an intermediate separately moves a
            // tree by an ulp: invisible in an instance count, enough to move the c1-flight golden.
            double[] e1 = { b.X - (double)a.X, b.Y - (double)a.Y, b.Z - (double)a.Z };
            double[] e2 = { c.X - (double)a.X, c.Y - (double)a.Y, c.Z - (double)a.Z };
            double[] n = Cross(e1, e2);
            double d = (n[0] * n[0]) + (n[1] * n[1]) + (n[2] * n[2]);
            if (d < 1e-9)
                continue;   // a degenerate first triangle carries no plane and no UV map
            // The reciprocal basis of {e1, e2, n}. A gradient built from it is perpendicular to n,
            // which is the projection along the quad normal, and it carries the mirroring sign.
            double[] f1 = Cross(e2, n), f2 = Cross(n, e1);
            for (int k = 0; k < 3; k++)
            {
                f1[k] /= d;
                f2[k] /= d;
            }
            double len = Math.Sqrt(d);
            Vector2 uvA = poly.UvCoords[0], uvB = poly.UvCoords[1], uvC = poly.UvCoords[2];
            double du1 = uvB.X - (double)uvA.X, du2 = uvC.X - (double)uvA.X;
            double dv1 = uvB.Y - (double)uvA.Y, dv2 = uvC.Y - (double)uvA.Y;
            var quad = new GroundQuad
            {
                Texture = tex,
                ExtentX = extX,
                ExtentZ = extZ,
                NormalX = n[0] / len,
                NormalY = n[1] / len,
                NormalZ = n[2] / len,
                AnchorX = a.X,
                AnchorY = a.Y,
                AnchorZ = a.Z,
                AnchorU = uvA.X,
                AnchorV = uvA.Y,
                GradUX = (f1[0] * du1) + (f2[0] * du2),
                GradUY = (f1[1] * du1) + (f2[1] * du2),
                GradUZ = (f1[2] * du1) + (f2[2] * du2),
                GradVX = (f1[0] * dv1) + (f2[0] * dv2),
                GradVY = (f1[1] * dv1) + (f2[1] * dv2),
                GradVZ = (f1[2] * dv1) + (f2[2] * dv2),
            };
            // The polygon's own fan, for the containment half of the projection test.
            for (int i = 1; i + 1 < poly.VertexIndices.Count; i++)
            {
                quad.Faces.Add(mesh.Vertices[poly.VertexIndices[0]]);
                quad.Faces.Add(mesh.Vertices[poly.VertexIndices[i]]);
                quad.Faces.Add(mesh.Vertices[poly.VertexIndices[i + 1]]);
            }
            return quad;
        }
        return null;
    }

    /// <summary>Builds the clutter for the given template names; null when nothing was
    /// placed (no templates, or none of their ground textures appear in the world).</summary>
    /// <param name="collision">Attach static colliders to the 3D decorations. Sprites are never
    /// collidable whatever this says. Off for static viewing, on in flight.</param>
    public Node3D? Build(IReadOnlyList<string> templateNames, bool collision = false,
        string worldName = "world1")
    {
        var templates = new Dictionary<string, Template>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in templateNames)
            if (ParseTemplate(name) is { } t)
                templates[t.GroundTexture] = t;
        if (templates.Count == 0)
            return null;

        ResolveProperties(templates);
        _stats = new LatticeStats();
        PlaceOnWorld(templates, worldName);
        _stats.Report();

        var root = new Node3D { Name = "clutter" };
        var parts = new List<string>();
        var exported = new List<KindExport>();
        var exportedMesh = new List<int>();   // parallel: each export's decoration MeshIndex
        var solidKinds = new List<Kind>();
        InstanceCount = SolidCount = SolidCollisionTriangles = 0;
        BlendCardKinds = ScissorCardKinds = 0;
        SolidCollisionShapes = SolidCollisionInstances = SolidCollisionOneSidedTriangles = 0;
        _solidShapes = null;
        // _allKinds, not templates.Values, a substitution-only kind has instances to export and
        // no template to be found under.
        foreach (var kind in _allKinds)
        {
            if (kind.Instances.Count == 0)
                continue;
            var mmi = kind.Solid ? BuildSolidInstance(kind) : BuildKindInstance(kind);
            if (mmi == null)
                continue;
            root.AddChild(mmi);
            exported.Add(new KindExport
            {
                Texture = kind.Label,
                Mesh = (ArrayMesh)mmi.Multimesh!.Mesh,
                Material = mmi.MaterialOverride,
                Solid = kind.Solid,
                NodeBias = NodeBiasOf(kind),
                Width = kind.Width,
                Height = kind.Height,
                CullMargin = CullMarginOf(kind),
                Placements = kind.Instances,
                Fades = kind.Fades,
            });
            exportedMesh.Add(kind.MeshIndex);
            if (kind.Solid)
            {
                SolidCount += kind.Instances.Count;
                solidKinds.Add(kind);
                // The rest of the decoration's own chain, one MultiMesh each over the same stamps.
                // Exported too, so MapEdgeExtender's copies past the map edge are whole buildings.
                foreach (var (meshIndex, local) in kind.ExtraParts)
                {
                    var placements = Composed(kind.Instances, local);
                    if (BuildSolidPart(kind, meshIndex, placements) is not { } partMmi)
                        continue;
                    root.AddChild(partMmi);
                    exported.Add(new KindExport
                    {
                        Texture = kind.Label,
                        Mesh = (ArrayMesh)partMmi.Multimesh!.Mesh,
                        Material = partMmi.MaterialOverride,
                        Solid = true,
                        NodeBias = NodeBiasOf(kind),
                        Width = kind.Width,
                        Height = kind.Height,
                        CullMargin = CullMarginOf(kind),
                        Placements = placements,
                        Fades = kind.Fades,
                    });
                    exportedMesh.Add(meshIndex);
                }
            }
            else
            {
                InstanceCount += kind.Instances.Count;
            }
            parts.Add($"{kind.Label} ×{kind.Instances.Count}");
        }
        if (collision && solidKinds.Count > 0)
        {
            BuildSolidCollision(root, solidKinds);
            // Hand each solid export its shared shape, so MapEdgeExtender can attach the same
            // one to its mirrored placements past the map edge.
            if (_solidShapes != null)
                for (int i = 0; i < exported.Count; i++)
                    if (exported[i].Solid && _solidShapes.TryGetValue(exportedMesh[i], out var s))
                        exported[i].CollisionShape = s;
        }
        Summary = string.Join(", ", parts);
        ExportedKinds = exported.Count > 0 ? exported : null;
        return InstanceCount == 0 && SolidCount == 0 ? null : root;
    }

    // Any billboard kind is a placeable card: C1's CylindricalY trees and bushes, and C5's
    // SphericalY poleflare glows beside their CylindricalY lightpole posts.
    private static bool IsSpriteCard(GameZMesh mesh)
    {
        if (SceneBuilder.ClassifyBillboard(mesh) is { } kind)
            return kind != SceneBuilder.BillboardKind.None;

        // Legacy v0.6.1 extraction (no ModelType): keep the original shape heuristic, or a
        // rollback tree would place no clutter at all.
        if (mesh.Polygons.Count != 1 || mesh.Vertices.Count != 4)
            return false;
        Vector3 min = mesh.Vertices[0], max = mesh.Vertices[0];
        foreach (var v in mesh.Vertices)
        {
            min = min.Min(v);
            max = max.Max(v);
        }
        return max.Z - min.Z <= 0.1f * Mathf.Max(max.X - min.X, max.Y - min.Y);
    }

    // The original stamper's steps 4, 6 and 7 (docs/org/clutter.md): the UV bounding box floored to
    // an integer lattice, containment tested in UV space, the world position recovered through the
    // triangle's own affine map. Steps 9, 10 and 11 follow below; step 5 is not applied.
    // ⚠ Never introduce a world grid here. Stamping per integer texture repeat is what makes the
    // clutter rotate and stretch with the painted ground, and a fixed grid loses C1 most of its trees.
    private static void PlaceOnTriangle(Template template, Vector3 a, Vector3 b, Vector3 c,
        Vector2 uva, Vector2 uvb, Vector2 uvc, HashSet<(int, int, int)> seen, LatticeStats stats,
        Random rng, Random fadeRng)
    {
        // ⚠ Guard world area and UV area independently. Neither implies the other, and a strip
        // artifact with healthy UVs and no world area plants a row of trees along a line.
        var tri = UvTriangle.Build(a, b, c, uva, uvb, uvc, out var fault);
        if (tri == null)
        {
            if (fault == UvTriangleFault.ZeroWorldArea)
                stats.ZeroWorldArea++;
            else
                stats.ZeroUvArea++;
            return;
        }

        // A remake-only footprint floor; it fires on 14 triangles in C5 and nowhere else.
        // ⚠ Do not add a slope cull beside it. The original's is authored per kind and defaults to
        // no cull, and no chapter authors it; a hard-coded one measures as inert install-wide.
        float area2 = (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
        float xzArea = 0.5f * Mathf.Abs(area2);
        if (xzArea < 0.5f)
            return;

        // A tripwire for a corrupt UV array, not a knob: every shipped span is modest, so this
        // should never fire, and the count in the summary line is a finding if it does.
        if (tri.CellCount > MaxLatticeCells)
        {
            stats.OverLargeLattice++;
            if (tri.CellCount > stats.WorstLatticeCells)
                stats.WorstLatticeCells = tri.CellCount;
            return;
        }

        for (int uInt = tri.MinU; uInt <= tri.MaxU; uInt++)
            for (int vInt = tri.MinV; vInt <= tri.MaxV; vInt++)
                for (int k = 0; k < template.Kinds.Count; k++)
                {
                    var kind = template.Kinds[k];
                    foreach (var cell in kind.CellPlacements)
                    {
                        // The decoration's quad UV in [0, 1), shifted to this repeat of the texture.
                        // Double for the same reason the quad map is: one extra rounding moves a golden.
                        double cu = uInt + (double)cell.OnQuad.Origin.X;
                        double cv = vInt + (double)cell.OnQuad.Origin.Z;
                        if (!tri.Contains(cu, cv))
                            continue;
                        var p = tri.World(cu, cv);
                        // ⚠ Do not remove this quarter-metre dedup. It is remake-only and the strict
                        // edge test already takes C1B/C2/C3/C5 to zero rejections, but C1 still reads
                        // 36 and C4 139 from a source nobody has diagnosed.
                        var key = (kind.MeshIndex, Mathf.RoundToInt(p.X * 4f), Mathf.RoundToInt(p.Z * 4f));
                        if (!seen.Add(key))
                        {
                            stats.DedupRejected++;
                            continue;
                        }
                        // The mechanical check for trees in the sea: a wrong UV containment test
                        // still yields a finite point, and only the source triangle can refuse it.
                        stats.Placed++;
                        if (!InSourceTriangle(a, b, c, p))
                            stats.OutsideSource++;

                        // ⚠ Roll here, after the point is known to be kept, and in the engine's
                        // order of substitute then scale. That is what keeps the draw sequence a
                        // function of the seed and the authored data alone.
                        var target = Roll(kind, rng, stats);
                        if (target == null)
                            continue;   // an unresolvable target: it keeps its share, places nothing

                        // ⚠ Draw the scale from the SOURCE kind, never the substituted target. The
                        // stamper holds the decoration's own kind block and the roll rewrites only
                        // the model pointer.
                        float scale = kind.ScaleRange.X
                            + ((kind.ScaleRange.Y - kind.ScaleRange.X) * (float)rng.NextDouble());
                        if (scale > target.MaxScale)
                            target.MaxScale = scale;

                        // Step 11: one draw for both distances, from the SOURCE kind's block.
                        // ⚠ Off its own stream, not `rng`: ours is not the original's anyway,
                        // and leaving the substitute/scale draws alone keeps a fade A/B fade-only.
                        var fade = ClutterKindProps.FadeThresholds(
                            kind.FarFadeMin, kind.FarFadeMax, (float)fadeRng.NextDouble());
                        target.Fades.Add(new Color(fade.X, fade.Y, fade.Z, 0f));

                        // A sprite drops its basis and authored Y, since the shader re-faces it and
                        // its mesh carries the card's extent; a 3D decoration keeps both.
                        // ⚠ The scale compounds onto that basis and never replaces it.
                        var basis = (target.Solid ? cell.OnQuad.Basis : Basis.Identity)
                            .Scaled(new Vector3(scale, scale, scale));
                        // ⚠ Both branches carry the mesh lift. It is where the decoration's own
                        // chain puts the drawn mesh, so dropping it buries C5's lamp glow in the road.
                        var lift = basis * cell.MeshLift;
                        target.Instances.Add(target.Solid
                            ? new Transform3D(basis, new Vector3(p.X, p.Y + cell.OnQuad.Origin.Y, p.Z) + lift)
                            : new Transform3D(basis, p + lift));
                    }
                }
    }

    // Step 9: one uniform draw walked against the kind's cumulative substitution
    // table. The engine subtracts each normalised share from the draw and takes the first entry
    // that sends it negative, which is the same selection as this comparison against the running
    // sum. A kind with no table always stamps itself, and a draw that falls past the last entry
    // (float error only, since the shares sum to 1) does too, the engine's own fallback, which
    // leaves the model pointer at the entry's own node.
    private static Kind? Roll(Kind kind, Random rng, LatticeStats stats)
    {
        if (kind.Substitutes.Count == 0)
            return kind;
        float r = (float)rng.NextDouble();
        foreach (var (cumulative, target) in kind.Substitutes)
        {
            if (r >= cumulative)
                continue;
            if (target == null)
                stats.SubstituteNothing++;
            else if (!ReferenceEquals(target, kind))
                stats.Substituted++;
            return target;
        }
        return kind;
    }

    // Barycentric containment in the triangle's OWN plane (not an XZ projection, a hillside
    // triangle's XZ shadow is a different shape). The slack absorbs the single rounding the
    // affine map ends on; a genuinely misplaced instance misses by metres, not by 1e-3.
    private static bool InSourceTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 p)
    {
        const float slack = 1e-3f;
        var n = (b - a).Cross(c - a);
        float d = n.LengthSquared();
        if (d < 1e-9f)
            return false;
        float w0 = (b - a).Cross(p - a).Dot(n) / d;
        float w1 = (c - b).Cross(p - b).Dot(n) / d;
        float w2 = (a - c).Cross(p - c).Dot(n) / d;
        return w0 >= -slack && w1 >= -slack && w2 >= -slack;
    }

    // The mesh's triangles in its own local space, fan or strip as the polygon says, and how many of
    // them came from a polygon that clears SHOW_BACKFACE. ⚠ Unlike SceneBuilder.EmitCollisionFaces
    // this does not alternate a strip's winding, so the triangles it returns are not consistently
    // wound and cannot be given a sidedness (see BuildSolidCollision).
    private static int AppendTriangles(GameZMesh mesh, List<Vector3> into)
    {
        int oneSided = 0;
        foreach (var poly in mesh.Polygons)
        {
            int n = poly.VertexIndices.Count;
            if (!poly.ShowBackface && n >= 3)
                oneSided += n - 2;
            if (poly.TriangleStrip)
            {
                for (int i = 0; i + 2 < n; i++)
                {
                    into.Add(mesh.Vertices[poly.VertexIndices[i]]);
                    into.Add(mesh.Vertices[poly.VertexIndices[i + 1]]);
                    into.Add(mesh.Vertices[poly.VertexIndices[i + 2]]);
                }
            }
            else
            {
                for (int i = 1; i + 1 < n; i++)
                {
                    into.Add(mesh.Vertices[poly.VertexIndices[0]]);
                    into.Add(mesh.Vertices[poly.VertexIndices[i]]);
                    into.Add(mesh.Vertices[poly.VertexIndices[i + 1]]);
                }
            }
        }

        return oneSided;
    }

    private static double[] Cross(double[] p, double[] q) => new[]
    {
        (p[1] * q[2]) - (p[2] * q[1]),
        (p[2] * q[0]) - (p[0] * q[2]),
        (p[0] * q[1]) - (p[1] * q[0]),
    };

    private static string? FirstTexture(GameZ gamez, GameZMesh mesh)
    {
        foreach (var poly in mesh.Polygons)
            if (poly.MaterialIndex >= 0 && poly.MaterialIndex < gamez.Materials.Count
                && gamez.Materials[poly.MaterialIndex].TextureName is { } tex)
                return tex;
        return null;
    }

    // Distinct example names for a one-line skip summary (many decorations share a name,
    // repeats would read like a bug); the caller keeps the true count beside it.
    private static string SkipExamples(List<string> names)
    {
        var distinct = new List<string>();
        foreach (var n in names)
            if (!distinct.Contains(n))
                distinct.Add(n);
        return distinct.Count > 5
            ? string.Join(", ", distinct.GetRange(0, 5)) + ", …"
            : string.Join(", ", distinct);
    }

    private static string Sanitize(string name)
    {
        Span<char> bad = stackalloc[] { '.', ':', '@', '/', '"', '%' };
        foreach (var ch in bad)
            name = name.Replace(ch, '_');
        return name.Length == 0 ? "clutter_kind" : name;
    }

    // The per-kind properties a stamp draws from: the two the placement applies (scale, fade).
    private static void AdoptBlock(Kind kind, ClutterKindProps block)
    {
        kind.ScaleRange = block.ScaleRange;
        kind.FarFadeMin = block.FarFadeMin;
        kind.FarFadeMax = block.FarFadeMax;
    }

    // Every placement of one kind as one MultiMesh, each instance carrying its fade thresholds as
    // custom data. ⚠ UseCustomData must be set before InstanceCount, or the buffer has no slot.
    private static MultiMesh InstancedMesh(Kind kind, Mesh mesh)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = mesh,
            InstanceCount = kind.Instances.Count,
        };
        for (int i = 0; i < kind.Instances.Count; i++)
        {
            mm.SetInstanceTransform(i, kind.Instances[i]);
            mm.SetInstanceCustomData(i, kind.Fades[i]);
        }
        ClutterCull.Index(mm, kind.Instances);
        return mm;
    }

    // A kind's stamps in one of its further meshes' frames. That frame is the drawn mesh's own
    // for every decoration this install ships. The composition is here so a data change cannot
    // silently drop a part's offset.
    private static List<Transform3D> Composed(List<Transform3D> instances, Transform3D local)
    {
        var composed = new List<Transform3D>(instances.Count);
        foreach (var xf in instances)
            composed.Add(xf * local);
        return composed;
    }

    // A billboard card, turned toward the camera by FaceBasisLines because the source decorations
    // are one-sided quads that would vanish edge-on. Fullbright, SceneBuilder's cylindrical fog, and
    // the archive's own blend-or-scissor verdict. `lit` and `fogged` are the decoration model's own
    // authored flags, emitted as variants so a lit, fogged kind gets the base form.
    // ⚠ A blended card takes depth_prepass_alpha, never depth_draw_never: a kind is one MultiMesh,
    // one draw in buffer order, so a card writing no depth is painted over by every later card.
    private static string ShaderCode(bool lit, bool fogged, bool clampUv, bool spherical, bool blend) => $$"""
        shader_type spatial;
        render_mode skip_vertex_transform, unshaded, cull_disabled, shadows_disabled{{(blend ? ", blend_mix, depth_prepass_alpha" : "")}};

        uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, {{(clampUv ? "repeat_disable" : "repeat_enable")}};

        // Single-sourced with SceneBuilder so the fog, the gamma-space modulate and the chapter's
        // mip LOD bias cannot drift. The original's bias is one device render state, so a stamped
        // card picks its level exactly as the ground it is planted on does.
        #include "res://shaders/csky_atmosphere.gdshaderinc"
        #include "res://shaders/csky_srgb.gdshaderinc"
        #include "res://shaders/csky_clutter_fade.gdshaderinc"
        #include "res://shaders/csky_mip_bias.gdshaderinc"
        {{(spherical ? FacadeInclude : "")}}

        // ⚠ Never declare an instance uniform below this include. Godot indexes them by declaration
        // order and merges the mapping across one GeometryInstance3D's materials, so two shaders
        // that disagree read each other's slots.
        #include "res://shaders/csky_instance_uniforms.gdshaderinc"

        varying flat float v_alpha;

        void vertex() {
            vec3 origin = MODEL_MATRIX[3].xyz;
            // The authored far fade: past it the card collapses to its planted point and costs no
            // fragments; inside the ramp the fragment stage dithers it out.
            v_alpha = csky_clutter_fade_alpha(origin, CAMERA_POSITION_WORLD, INSTANCE_CUSTOM);
            float keep = step(0.004, v_alpha);
        {{FaceBasisLines(spherical)}}
            VERTEX = (VIEW_MATRIX * vec4(origin + face * VERTEX * keep, 1.0)).xyz;
        }

        void fragment() {
            if (!csky_clutter_dither_keep(FRAGCOORD.xy, v_alpha)) {
                discard;
            }
            vec4 col = vec4(csky_srgb_to_linear(COLOR.rgb), COLOR.a) * {{SceneBuilder.SampleAlbedo("UV")}};
            ALBEDO = col.rgb{{(lit ? " * csky_world_light" : "")}};
        {{(fogged ? FogLines : "")}}{{SceneBuilder.TintLine}}
            ALPHA = col.a;
        {{(blend ? "" : ScissorLine)}}}
        """;

    // The basis a card's vertices are turned by, the rendering half of its decoration model's own
    // FacadeMode (docs/org/vertexLighting.md, "Facades: the same gate, a different N").
    // A SphericalY glow takes the world-up facade look-at, which reads the eye's position and
    // never its basis, so a roll leaves it alone. A CylindricalY card spins about its planted
    // point's vertical alone, so a tree or a lamp post stays upright.
    private static string FaceBasisLines(bool spherical) => spherical
        ? "    mat3 face = csky_facade_spherical(origin, CAMERA_POSITION_WORLD);"
        : "    vec2 to_cam = CAMERA_POSITION_WORLD.xz - origin.xz;\n"
            + "    float len = length(to_cam);\n"
            + "    vec2 dir = len > 1e-4 ? to_cam / len : vec2(0.0, 1.0);\n"
            + "    mat3 face = mat3(\n"
            + "        vec3(dir.y, 0.0, -dir.x),\n"
            + "        vec3(0.0, 1.0, 0.0),\n"
            + "        vec3(dir.x, 0.0, dir.y));";

    // A template subtree: root → ground node (first descendant with a mesh; its texture
    // + quad size define what gets decorated and the tiling period) → decoration nodes
    // (a local translation each, sprite mesh on the child below).
    private Template? ParseTemplate(string name)
    {
        var root = FindTemplateRoot(name);
        if (root == null)
        {
            Log.Info("world", $"clutter template not in gamez template={name}");
            return null;
        }
        var ground = FirstWithMesh(_gamez, root);
        if (ground == null || GroundInfo(_gamez, ground) is not { } quad)
        {
            Log.Info("world", $"clutter template has no textured ground quad template={name}");
            return null;
        }
        var template = new Template { GroundTexture = quad.Texture };

        var kinds = new Dictionary<int, Kind>();
        // Genuinely unusable decorations only: no mesh below the node, or no resolvable texture.
        // Collected and logged as one summary line.
        List<string>? skipped = null;
        // The engine's "does not project to polygon" path. No retail decoration takes it.
        // ⚠ A miss must skip and say so, never quietly land at UV (0,0).
        List<string>? offQuad = null;
        foreach (var childIndex in ground.Children)
        {
            var deco = _gamez.Nodes[childIndex];
            var decoMesh = FirstWithMesh(_gamez, deco, includeSelf: false, out var toMesh);
            if (decoMesh == null)
            {
                (skipped ??= new List<string>()).Add(deco.Name);
                continue;
            }
            // ⚠ The stored XZ is the ground quad's interpolated texture UV wrapped into [0, 1), not
            // metres. Basis and Y stay as authored; the solid path needs both.
            var local = deco.Local ?? Transform3D.Identity;
            if (!quad.TryUv(local.Origin, out var uv))
            {
                (offQuad ??= new List<string>()).Add(deco.Name);
                continue;
            }
            // ⚠ The UV is projected from the chain's TOP node, which is where the original casts
            // its ray; the lift below moves the drawn mesh, never the point it is stamped at.
            var cell = (
                OnQuad: new Transform3D(local.Basis, new Vector3(
                    GroundQuad.Wrap(uv.X), local.Origin.Y, GroundQuad.Wrap(uv.Y))),
                MeshLift: toMesh.Origin);

            if (!kinds.TryGetValue(decoMesh.MeshIndex, out var kind))
            {
                if (MakeKind(decoMesh, deco.Name, deco) is not { } made)
                {
                    (skipped ??= new List<string>()).Add(deco.Name);
                    continue;
                }
                kind = made;
                kinds[decoMesh.MeshIndex] = kind;
                template.Kinds.Add(kind);
            }
            kind.CellPlacements.Add(cell);
        }
        if (skipped != null)
            Log.Info("world", $"clutter template skipped decorations template={name} skipped={skipped.Count} examples='{SkipExamples(skipped)}'");
        if (offQuad != null)
            Log.Info("world", $"clutter decorations do not project onto the ground quad template={name} skipped={offQuad.Count} examples='{SkipExamples(offQuad)}'");
        return template.Kinds.Count > 0 ? template : null;
    }

    /// <inheritdoc cref="FindTemplateRoot(GameZ, string)"/>
    private GameZNode? FindTemplateRoot(string name) => FindTemplateRoot(_gamez, name);

    // One decoration kind from the node carrying its mesh. Shared by the template walk and by the
    // substitution-target minting below, so a model reached either way is classified, labelled and
    // rendered identically, a `firtree2` stamped because `firtree1` rolled it must be the same
    // kind of thing as a `firtree2` the template placed itself. Null when the mesh is neither a
    // sprite card nor a solid decoration (no texture, or no SceneBuilder for the solid path).
    private Kind? MakeKind(GameZNode meshNode, string model, GameZNode deco)
    {
        if (SpriteInfo(meshNode.MeshIndex) is { } s)
        {
            return new Kind
            {
                MeshIndex = meshNode.MeshIndex,
                NodeIndex = meshNode.Index,
                Model = model,
                Label = s.Texture,
                Width = s.Width,
                Height = s.Height,
                Billboard = SceneBuilder.ClassifyBillboard(_gamez.Meshes[meshNode.MeshIndex])
                    ?? SceneBuilder.BillboardKind.CylindricalY,
                Lit = _gamez.Meshes[meshNode.MeshIndex].Lighting,
                Fogged = _gamez.Meshes[meshNode.MeshIndex].Fog,
            };
        }
        if (IsSolidDecoration(meshNode.MeshIndex))
        {
            var solid = new Kind
            {
                MeshIndex = meshNode.MeshIndex,
                NodeIndex = meshNode.Index,
                Model = model,
                Label = model,
                Solid = true,
            };
            solid.ExtraParts.AddRange(ExtraMeshes(_gamez, deco, meshNode));
            return solid;
        }
        return null;
    }

    // The templates.zrd per-kind properties attached to the parsed kinds, and every `substitute`
    // target resolved to the kind its stamps will land in. Runs ONCE, after every template is
    // parsed and before a single lattice cell is walked, for two reasons: a target may be another
    // template's decoration (so all templates must exist first), and a target may be no template's
    // decoration at all (so a kind has to be minted, which cannot happen mid-walk, where
    // PlaceOnTriangle is iterating Template.Kinds by index).
    private void ResolveProperties(Dictionary<string, Template> templates)
    {
        _allKinds.Clear();
        _kindsByModel.Clear();
        foreach (var template in templates.Values)
            foreach (var kind in template.Kinds)
            {
                _allKinds.Add(kind);
                // ⚠ First-seen wins, matching the engine's one model per name. C5 carries the same
                // building as four meshes, so otherwise the target depends on who rolled it.
                if (kind.Model.Length > 0)
                    _kindsByModel.TryAdd(kind.Model, kind);
            }
        if (_props == null)
            return;

        // Two passes: every kind gets its own block's scale first, because minting a target below
        // appends to _allKinds and a target minted for one source may be another source's target.
        foreach (var kind in _allKinds)
            if (_props.Find(kind.Model) is { } block)
                AdoptBlock(kind, block);

        int minted = 0, missing = 0;
        // ⚠ Indexed, and bounded to the sources present now. Minting appends, and a minted kind
        // takes its block's scale but never its own substitutes: a stamp is rolled once.
        for (int i = 0, sources = _allKinds.Count; i < sources; i++)
        {
            var kind = _allKinds[i];
            if (_props.Find(kind.Model) is not { Substitutes.Count: > 0 } block)
                continue;
            var table = new List<(float Cumulative, Kind? Target)>(block.Substitutes.Count);
            float acc = 0f;
            foreach (var sub in block.Substitutes)
            {
                acc += sub.Fraction;
                // ⚠ An entry naming the kind's own model stays in this kind. Routing it through
                // first-seen instead would shuffle instances between C5's mesh duplicates and move
                // goldens for a distinction the original cannot express.
                var target = string.Equals(sub.Model, kind.Model, StringComparison.OrdinalIgnoreCase)
                    ? kind
                    : ResolveModel(sub.Model, ref minted);
                if (target == null)
                    missing++;
                table.Add((acc, target));
            }
            kind.Substitutes = table;
        }

        if (minted > 0 || missing > 0)
            Log.Info("world", $"clutter substitute targets: minted={minted} unresolvable={missing}");
    }

    // A substitution target as a kind: an existing one, or minted from the gamez node of that name.
    // Null is the engine's own unresolvable-target path, which keeps its share and places nothing.
    // No placed retail decoration reaches it, so the branch is fidelity rather than a live case.
    private Kind? ResolveModel(string model, ref int minted)
    {
        if (_kindsByModel.TryGetValue(model, out var known))
            return known;

        // The engine's lookup reaches any model in the gamez, not only those under a template.
        // C5's cb00b and C3's palmtree2/3 are authored as substitution targets only.
        foreach (var node in _gamez.Nodes)
        {
            if (!string.Equals(node.Name, model, StringComparison.OrdinalIgnoreCase))
                continue;
            if (FirstWithMesh(_gamez, node) is not { } meshNode)
                continue;
            if (MakeKind(meshNode, model, node) is not { } kind)
                continue;
            if (_props?.Find(model) is { } block)
                AdoptBlock(kind, block);
            _kindsByModel[model] = kind;
            _allKinds.Add(kind);
            minted++;
            return kind;
        }
        return null;
    }

    // A 3D decoration: anything with real geometry that is NOT a billboard card. C2's
    // filmblock/resblock buildings and parklot Studebakers, C5's cblock city blocks (2-27
    // polygons, up to 108 m tall). Requires a SceneBuilder to render through, without one
    // these fall back to the skip list.
    private bool IsSolidDecoration(int meshIndex)
    {
        if (_scene == null || meshIndex < 0 || meshIndex >= _gamez.Meshes.Count)
            return false;
        var mesh = _gamez.Meshes[meshIndex];
        if (mesh.Polygons.Count == 0 || mesh.Vertices.Count == 0)
            return false;
        // Not a card by the shared rule (nulls, a legacy extraction, fall through to the
        // shape heuristic in IsSpriteCard, which SpriteInfo already applied and rejected).
        return !IsSpriteCard(mesh);
    }

    // Only genuine sprite cards billboard, decided by the gamez model through the shared
    // SceneBuilder.ClassifyBillboard rather than a local shape guess. A Default model is not
    // skipped here; it takes the solid path in IsSolidDecoration.
    private (string Texture, float Width, float Height)? SpriteInfo(int meshIndex)
    {
        var mesh = _gamez.Meshes[meshIndex];
        var tex = FirstTexture(_gamez, mesh);
        if (tex == null || mesh.Vertices.Count == 0 || !IsSpriteCard(mesh))
            return null;
        Vector3 min = mesh.Vertices[0], max = mesh.Vertices[0];
        foreach (var v in mesh.Vertices)
        {
            min = min.Min(v);
            max = max.Max(v);
        }
        return (tex, max.X - min.X, max.Y - min.Y);
    }

    // Walk the placed world (the same set WorldBuilder renders: world children +
    // partition-referenced subtrees, nearest LOD only) and stamp each template onto
    // every polygon textured with its ground texture.
    private void PlaceOnWorld(Dictionary<string, Template> templates, string worldName)
    {
        GameZNode? world = null;
        foreach (var n in _gamez.Nodes)
            if (n.Kind == "World" && string.Equals(n.Name, worldName, StringComparison.OrdinalIgnoreCase))
            {
                world = n;
                break;
            }
        if (world == null)
            return;

        var seen = new HashSet<(int Kind, int X, int Z)>(); // dedup across decal-layered coplanar polys

        // ⚠ Seed this stream fixed, never from Rng.Master. A chapter's forest is the same forest on
        // every launch in the original, and the master rerolls the species mix on an unpinned run.
        // This is not the original's stream and cannot be, so do not try to match it.
        var rng = new Random(PlacementSeed);
        var fadeRng = new Random(PlacementSeed);   // the fade's own stream, see PlaceOnTriangle

        void Walk(int nodeIndex, Transform3D xf)
        {
            if (nodeIndex < 0 || nodeIndex >= _gamez.Nodes.Count)
                return;
            var node = _gamez.Nodes[nodeIndex];
            if (WorldBuilder.SkipWorldNode(node) || (node.Kind == "Lod" && node.LodRangeMin != 0f))
                return;
            if (node.Local is { } local)
                xf *= local;
            if (node.MeshIndex >= 0 && node.MeshIndex < _gamez.Meshes.Count)
                PlaceOnMesh(_gamez.Meshes[node.MeshIndex], xf, templates, seen, rng, fadeRng);
            foreach (var c in node.Children)
                Walk(c, xf);
        }
        foreach (var c in world.Children)
            Walk(c, Transform3D.Identity);
        if (world.PartitionNodes != null)
            foreach (var idx in world.PartitionNodes)
                Walk(idx, Transform3D.Identity);
    }

    private void PlaceOnMesh(GameZMesh mesh, Transform3D xf,
        Dictionary<string, Template> templates, HashSet<(int, int, int)> seen, Random rng,
        Random fadeRng)
    {
        foreach (var poly in mesh.Polygons)
        {
            if (poly.MaterialIndex < 0 || poly.MaterialIndex >= _gamez.Materials.Count)
                continue;
            var tex = _gamez.Materials[poly.MaterialIndex].TextureName;
            if (tex == null || !templates.TryGetValue(tex, out var template))
                continue;
            // The engine's gate: it picks which coplanar layer decorates, not whether ground is bare.
            // ⚠ Never pair it with a map-wide cblock4/5/6 suppression. The gate alone empties C5's
            // downtown, the suppression alone doubles the city; they are one coupled change.
            if (poly.NoClutter)
            {
                _stats.NoClutterFlagged++;
                continue;
            }
            // Always layer 0: no polygon in the install names a registered template above it.
            // A null UV array has no lattice, which the engine skips and so does this.
            int n = poly.VertexIndices.Count;
            var uvs = poly.UvCoords;
            if (uvs == null || uvs.Count < n)
            {
                _stats.NoUvArray++;
                continue;
            }
            // Same enumeration as SceneBuilder.EmitPolygon, so surface heights match what renders.
            // ⚠ Index UVs by corner position, never by vertex id. C1 model 953 lists one vertex at
            // two corners with two UVs, and the id route stamps it in the wrong texture frame.
            if (poly.TriangleStrip)
            {
                for (int i = 0; i + 2 < n; i++)
                    PlaceOnTriangle(template,
                        xf * mesh.Vertices[poly.VertexIndices[i]],
                        xf * mesh.Vertices[poly.VertexIndices[i + 1]],
                        xf * mesh.Vertices[poly.VertexIndices[i + 2]],
                        uvs[i], uvs[i + 1], uvs[i + 2], seen, _stats, rng, fadeRng);
            }
            else
            {
                for (int i = 1; i + 1 < n; i++)
                    PlaceOnTriangle(template,
                        xf * mesh.Vertices[poly.VertexIndices[0]],
                        xf * mesh.Vertices[poly.VertexIndices[i]],
                        xf * mesh.Vertices[poly.VertexIndices[i + 1]],
                        uvs[0], uvs[i], uvs[i + 1], seen, _stats, rng, fadeRng);
            }
        }
    }

    // ---------------------------------------------------------------- rendering

    private Shader SpriteShader(bool lit, bool fogged, bool clampUv, bool spherical, bool blend)
    {
        int key = (lit ? 1 : 0) | (fogged ? 2 : 0) | (clampUv ? 4 : 0) | (spherical ? 8 : 0) | (blend ? 16 : 0);
        if (!_shaders.TryGetValue(key, out var shader))
            _shaders[key] = shader = new Shader { Code = ShaderCode(lit, fogged, clampUv, spherical, blend) };
        return shader;
    }

    // All instances of one kind as a single MultiMesh draw call. Null when an isolation run
    // (--hide-alpha) asked for this kind's transparency class to be left out.
    private MultiMeshInstance3D? BuildKindInstance(Kind kind)
    {
        var tex = _textures.Find(kind.Label);
        // ⚠ Read the verdict straight off the Find above and nowhere else; it is what sets the
        // archive's Last* fields. A card whose alpha the archive calls soft blends, exactly as the
        // same texture does on a world surface, so foliage and glow are not cut here alone.
        bool blend = tex != null && _textures.LastHadAlpha && _textures.LastAlphaIsSoft;
        var alpha = blend ? SceneBuilder.TransparencyClass.BlendCard : SceneBuilder.TransparencyClass.ScissorCard;
        if (blend)
            BlendCardKinds++;
        else
            ScissorCardKinds++;
        if (_scene != null && _scene.HiddenAlpha.HasFlag(alpha))
            return null;
        // Clamp when the card's UVs never leave the unit square, the same data-driven rule as
        // SceneBuilder's world surfaces; wrapping bleeds the texture's opposite edge in at the
        // UV border (the hairline-seam / tracer-tail artifact).
        bool clampUv = SceneBuilder.UvsWithinUnitSquare(_gamez.Meshes[kind.MeshIndex].Polygons, pass: 0);
        var mat = new ShaderMaterial
        {
            Shader = SpriteShader(kind.Lit, kind.Fogged, clampUv,
                kind.Billboard == SceneBuilder.BillboardKind.Spherical, blend),
        };
        if (tex != null)
            mat.SetShaderParameter("albedo_tex", tex);

        var mm = InstancedMesh(kind, BuildSpriteMesh(kind.MeshIndex));

        return new MultiMeshInstance3D
        {
            Multimesh = mm,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = CullMarginOf(kind),
            Name = Sanitize(kind.Label),
        };
    }

    // The billboard shader swings verts outside the MultiMesh's static AABB, so the card's own
    // width is the margin, ⚠ GROWN BY THE LARGEST SCALE any of its instances actually got.
    // C2's spruce reaches 3.0×, and a margin left at 1× would pop a third of
    // that card off the screen edge. Measured from the placements rather than from the kind's own
    // scale_range, because a kind reached by substitution is scaled by its sources' ranges.
    private float CullMarginOf(Kind kind) => kind.Width * kind.MaxScale;

    // The world's cross-node draw-order tie-break, taken through SceneBuilder so a decoration and
    // the world node it was stamped from land on the same rank. Every instance of a kind shares one
    // value, because a MultiMesh has a single instance-uniform set.
    private float NodeBiasOf(Kind kind)
        => _scene?.NodeBiasOf(kind.NodeIndex) ?? (kind.NodeIndex * SceneBuilder.NodeOrderBias);

    private MultiMeshInstance3D? BuildSolidInstance(Kind kind)
    {
        // The clutter-fade variant of the world materials: the same surfaces, plus the per-instance
        // distance fade the sprite shader carries, so a city block fades like the card beside it.
        var mesh = _scene?.SharedMesh(kind.MeshIndex, clutterFade: true);
        if (mesh == null)
            return null;
        var mm = InstancedMesh(kind, mesh);

        var mmi = new MultiMeshInstance3D
        {
            Multimesh = mm,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Name = Sanitize(kind.Label),
        };
        mmi.SetInstanceShaderParameter("node_bias", NodeBiasOf(kind));
        return mmi;
    }

    // One further mesh of a decoration's chain, drawn over the kind's own stamps already moved
    // into that mesh's frame. Same materials, same fades and the same draw-order bias as the
    // mesh it belongs with, because it is one building, not a second decoration.
    private MultiMeshInstance3D? BuildSolidPart(Kind kind, int meshIndex, List<Transform3D> placements)
    {
        var mesh = _scene?.SharedMesh(meshIndex, clutterFade: true);
        if (mesh == null)
            return null;
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = mesh,
            InstanceCount = placements.Count,
        };
        for (int i = 0; i < placements.Count; i++)
        {
            mm.SetInstanceTransform(i, placements[i]);
            mm.SetInstanceCustomData(i, kind.Fades[i]);
        }
        ClutterCull.Index(mm, placements);
        var mmi = new MultiMeshInstance3D
        {
            Multimesh = mm,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Name = Sanitize(kind.Label + "_" + meshIndex),
        };
        mmi.SetInstanceShaderParameter("node_bias", NodeBiasOf(kind));
        return mmi;
    }

    private void BuildSolidCollision(Node3D root, List<Kind> kinds)
    {
        // One shape per distinct decoration mesh, keyed by MeshIndex, a decoration's further
        // meshes included. That over-counts C5's mesh duplicates, but at about 40 triangles a
        // shape it is not worth a geometry hash.
        var shapes = _solidShapes = new Dictionary<int, ConcavePolygonShape3D>();
        var tris = new List<Vector3>();
        foreach (var kind in kinds)
        {
            Shape(kind.MeshIndex);
            foreach (var (meshIndex, _) in kind.ExtraParts)
                Shape(meshIndex);
        }

        SolidCollisionShapes = shapes.Count;
        if (shapes.Count == 0)
            return;

        // The shapes are referenced by the physics server through their RIDs only, which does
        // not keep the Godot Ref alive. Anchor them on the clutter root so their lifetime is
        // the scene tree's, without this the GC can free a shape out from under live bodies.
        var anchor = new Godot.Collections.Array();
        foreach (var s in shapes.Values)
            anchor.Add(s);
        root.SetMeta(SharedShapeMeta, anchor);

        var regions = new Dictionary<(int, int), StaticBody3D>();
        foreach (var kind in kinds)
        {
            Attach(kind.MeshIndex, kind.Instances);
            foreach (var (meshIndex, local) in kind.ExtraParts)
                Attach(meshIndex, Composed(kind.Instances, local));
        }

        void Shape(int meshIndex)
        {
            if (shapes.ContainsKey(meshIndex))
                return;
            tris.Clear();
            int oneSided = AppendTriangles(_gamez.Meshes[meshIndex], tris);
            if (tris.Count == 0)
                return;
            // ⚠ Two-sided where SceneBuilder's world colliders are one-sided, and not for want of
            // the flag. This triangulation does not alternate a strip's winding, so the triangles
            // have no agreed front. Whether the original holds these at all is undecoded.
            var shape = new ConcavePolygonShape3D { Data = tris.ToArray(), BackfaceCollision = true };
            shapes[meshIndex] = shape;
            SolidCollisionTriangles += tris.Count / 3;
            SolidCollisionOneSidedTriangles += oneSided;
        }

        void Attach(int meshIndex, List<Transform3D> placements)
        {
            if (!shapes.TryGetValue(meshIndex, out var shape))
                return;
            var shapeRid = shape.GetRid();
            foreach (var xf in placements)
            {
                var key = (Mathf.FloorToInt(xf.Origin.X / CollisionRegion),
                           Mathf.FloorToInt(xf.Origin.Z / CollisionRegion));
                if (!regions.TryGetValue(key, out var body))
                {
                    // Named to locate the cell in a crash log.
                    regions[key] = body = new StaticBody3D { Name = $"clutter_bld_{key.Item1}_{key.Item2}" };
                    body.SetMeta(CollisionCellMeta, new Rect2(
                        key.Item1 * CollisionRegion, key.Item2 * CollisionRegion, CollisionRegion, CollisionRegion));
                    root.AddChild(body);
                }
                PhysicsServer3D.BodyAddShape(body.GetRid(), shapeRid, xf);
                SolidCollisionInstances++;
            }
        }
    }

    // The sprite's own source geometry (verts + UVs, fan-triangulated) in local space:
    // x spans ± half the width around the planted point, y up from 0, the billboard
    // shader spins it about that local origin.
    private ArrayMesh BuildSpriteMesh(int meshIndex)
    {
        var mesh = _gamez.Meshes[meshIndex];
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var poly in mesh.Polygons)
        {
            int n = poly.VertexIndices.Count;
            for (int i = 1; i + 1 < n; i++)
                foreach (var corner in stackalloc[] { 0, i, i + 1 })
                {
                    st.SetNormal(Vector3.Back);
                    st.SetColor(poly.VertexColors != null && corner < poly.VertexColors.Count
                        ? poly.VertexColors[corner]
                        : Colors.White);
                    if (poly.UvCoords != null && corner < poly.UvCoords.Count)
                        st.SetUV(poly.UvCoords[corner]);
                    st.AddVertex(mesh.Vertices[poly.VertexIndices[corner]]);
                }
        }
        var arrayMesh = new ArrayMesh();
        st.Commit(arrayMesh);
        return arrayMesh;
    }

    /// <summary>One decoration kind of the last Build, exported so <see cref="MapEdgeExtender"/>
    /// can mirror it past the map edge and continue the forest, or in C5 the city.
    /// <c>Placements</c> carries full transforms because a building's authored basis is part of
    /// its placement. <c>Material</c> is null for a solid kind, whose mesh already carries
    /// SceneBuilder's per-surface materials. <c>CollisionShape</c> is the kind's shared shape in
    /// its own local space, which is what lets the extender attach it for one BodyAddShape
    /// call.</summary>
    public sealed class KindExport
    {
        public string Texture = "";
        public ArrayMesh Mesh = null!;
        public Material? Material;
        public bool Solid;
        public float NodeBias;      // solid kinds: the `node_bias` instance uniform value
        public float Width, Height;
        public float CullMargin;    // Width grown by the largest scale_range draw these got
        public Shape3D? CollisionShape;
        public IReadOnlyList<Transform3D> Placements = null!;
        public IReadOnlyList<Color> Fades = null!;   // parallel: each placement's fade custom data
    }

    /// <summary>One world triangle as the original's stamper sees it: the integer UV lattice its
    /// texture coordinates span, a containment test in UV space, and the affine map back to a
    /// world position.
    /// ⚠ Keep everything double and round exactly once, in <see cref="World"/>. A float
    /// intermediate moves a tree by an ulp, which is enough to move a forest golden.
    /// ⚠ Never reuse one triangle's map for the next. Neighbouring triangles of one polygon carry
    /// different UV frames, so that is a wrong answer rather than an optimisation.</summary>
    public sealed class UvTriangle
    {
        // Below this the world triangle is a sliver with no usable plane. 1e-4 m² is a square
        // 1 cm on a side; the artifacts this catches are exactly zero-area, not merely small.
        private const double MinWorldArea2 = 1e-8;

        // Likewise in UV space, where the quantity is the map's determinant.
        private const double MinUvArea2 = 1e-12;

        private readonly double _u0, _v0, _u1, _v1, _u2, _v2;
        private readonly double _p0X, _p0Y, _p0Z;
        private readonly double _aX, _aY, _aZ;   // world displacement per +1 U
        private readonly double _bX, _bY, _bZ;   // world displacement per +1 V
        private readonly double _sign;           // the UV winding, for the edge test

        private UvTriangle(Vector3 a, Vector2 uva, Vector2 uvb, Vector2 uvc,
            double[] axisU, double[] axisV, double det)
        {
            _u0 = uva.X;
            _v0 = uva.Y;
            _u1 = uvb.X;
            _v1 = uvb.Y;
            _u2 = uvc.X;
            _v2 = uvc.Y;
            _p0X = a.X;
            _p0Y = a.Y;
            _p0Z = a.Z;
            _aX = axisU[0];
            _aY = axisU[1];
            _aZ = axisU[2];
            _bX = axisV[0];
            _bY = axisV[1];
            _bZ = axisV[2];
            _sign = det > 0 ? 1.0 : -1.0;
            MinU = (int)Math.Floor(Math.Min(_u0, Math.Min(_u1, _u2)));
            MaxU = (int)Math.Floor(Math.Max(_u0, Math.Max(_u1, _u2)));
            MinV = (int)Math.Floor(Math.Min(_v0, Math.Min(_v1, _v2)));
            MaxV = (int)Math.Floor(Math.Max(_v0, Math.Max(_v1, _v2)));
        }

        /// <summary>The UV bounding box floored to integers (step 4), inclusive on both ends.</summary>
        public int MinU { get; }

        /// <inheritdoc cref="MinU"/>
        public int MaxU { get; }

        /// <inheritdoc cref="MinU"/>
        public int MinV { get; }

        /// <inheritdoc cref="MinU"/>
        public int MaxV { get; }

        /// <summary>Integer lattice cells the walk would visit. <c>long</c> because a corrupt UV
        /// array could overflow an <c>int</c> before the caller's bound ever saw it.</summary>
        public long CellCount => ((long)MaxU - MinU + 1) * ((long)MaxV - MinV + 1);

        /// <summary>World displacement per +1 of U. Public for the test that pins
        /// the worked example; the walk uses <see cref="World"/>.</summary>
        public Vector3 AxisU => new((float)_aX, (float)_aY, (float)_aZ);

        /// <summary>World displacement per +1 of V.</summary>
        public Vector3 AxisV => new((float)_bX, (float)_bY, (float)_bZ);

        /// <summary>Null when the triangle is degenerate in either space, with
        /// <paramref name="fault"/> saying which, the caller counts both, because they are
        /// different defects in the source mesh and netting them hides one.</summary>
        public static UvTriangle? Build(Vector3 a, Vector3 b, Vector3 c,
            Vector2 uva, Vector2 uvb, Vector2 uvc, out UvTriangleFault fault)
        {
            double[] e1 = { b.X - (double)a.X, b.Y - (double)a.Y, b.Z - (double)a.Z };
            double[] e2 = { c.X - (double)a.X, c.Y - (double)a.Y, c.Z - (double)a.Z };
            double[] n = Cross(e1, e2);
            if ((n[0] * n[0]) + (n[1] * n[1]) + (n[2] * n[2]) < MinWorldArea2)
            {
                fault = UvTriangleFault.ZeroWorldArea;
                return null;
            }

            double du1 = uvb.X - (double)uva.X, dv1 = uvb.Y - (double)uva.Y;
            double du2 = uvc.X - (double)uva.X, dv2 = uvc.Y - (double)uva.Y;
            double det = (du1 * dv2) - (du2 * dv1);
            if (Math.Abs(det) < MinUvArea2)
            {
                fault = UvTriangleFault.ZeroUvArea;
                return null;
            }

            // Solve A·du1 + B·dv1 = e1 and A·du2 + B·dv2 = e2 for the two world axes. This is
            // the same map step 7 builds, written as a 2×2 inverse rather than a ray-cast.
            var axisU = new double[3];
            var axisV = new double[3];
            for (int k = 0; k < 3; k++)
            {
                axisU[k] = ((e1[k] * dv2) - (e2[k] * dv1)) / det;
                axisV[k] = ((e2[k] * du1) - (e1[k] * du2)) / det;
            }

            fault = UvTriangleFault.ZeroUvArea;   // unused on the success path
            return new UvTriangle(a, uva, uvb, uvc, axisU, axisV, det);
        }

        /// <summary>Step 6: the three edge cross-products in UV space, on the side the triangle's
        /// own winding calls inside.
        /// ⚠ Keep this strict on the edge, matching the original. A candidate on the diagonal two
        /// triangles share must be claimed by neither; an inclusive test double-stamps it.
        /// ⚠ Keep it in double. A float32 boundary evaluation produces off-by-one instances in
        /// C5.</summary>
        public bool Contains(double u, double v)
        {
            double w0 = (((_u1 - _u0) * (v - _v0)) - ((_v1 - _v0) * (u - _u0))) * _sign;
            double w1 = (((_u2 - _u1) * (v - _v1)) - ((_v2 - _v1) * (u - _u1))) * _sign;
            double w2 = (((_u0 - _u2) * (v - _v2)) - ((_v0 - _v2) * (u - _u2))) * _sign;
            return w0 > 0 && w1 > 0 && w2 > 0;
        }

        /// <summary>Step 7: the world position at a texture coordinate. The single rounding of
        /// the whole walk happens here.</summary>
        public Vector3 World(double u, double v)
        {
            double du = u - _u0, dv = v - _v0;
            return new Vector3(
                (float)(_p0X + (_aX * du) + (_bX * dv)),
                (float)(_p0Y + (_aY * du) + (_bY * dv)),
                (float)(_p0Z + (_aZ * du) + (_bZ * dv)));
        }
    }

    /// <summary>A template's ground quad, reduced to what placement needs: which terrain
    /// texture it decorates, its per-axis local extent, and the affine map from a local
    /// position on its plane to the polygon's own interpolated texture UV, which is the
    /// coordinate every decoration is stored in.</summary>
    public sealed class GroundQuad
    {
        // The quad polygon's triangles, in the template's local space (containment only).
        public readonly List<Vector3> Faces = new();

        public string Texture = "";
        // The quad's own local X/Z extents. ⚠ Descriptive only: NOTHING in placement reads them,
        // and nothing should, the lattice spacing comes from the WORLD polygon's UVs, not from
        // the template's size.
        public float ExtentX, ExtentZ;

        // The plane and the UV map, in DOUBLE, see the note in GroundInfo for why the single
        // rounding at the end of TryUv is load-bearing rather than pedantry.
        public double NormalX, NormalY, NormalZ;         // unit plane normal
        public double AnchorX, AnchorY, AnchorZ;         // a point on the plane…
        public double AnchorU, AnchorV;                  // …and the UV there
        public double GradUX, GradUY, GradUZ;            // d(u) per metre of local displacement
        public double GradVX, GradVY, GradVZ;            // d(v) likewise

        // The original's fmod wrap, both branches. A negative coordinate maps to 1 − frac, and
        // an exact 1.0 (a frac that rounded away) collapses back to 0. No retail decoration
        // takes the negative branch, all 32 resolving quads span exactly 0..1, so the wrap
        // folds nothing, but the original takes it, so this does.
        public static float Wrap(float value)
        {
            float f = value % 1f;
            if (f < 0f)
            {
                f += 1f;
                if (f >= 1f)
                    f = 0f;
            }
            return f;
        }

        /// <summary>The quad's interpolated texture UV under <paramref name="local"/>, projected
        /// along the quad normal. False when the decoration does not project onto the polygon,
        /// the original logs and skips that case, and so does the caller.</summary>
        public bool TryUv(Vector3 local, out Vector2 uv)
        {
            // The original ray-casts the decoration ±5 along the quad normal, so a decoration
            // further off the plane than that reaches nothing to project onto.
            const double rayReach = 5.0;

            double ox = local.X - AnchorX, oy = local.Y - AnchorY, oz = local.Z - AnchorZ;
            double dist = (NormalX * ox) + (NormalY * oy) + (NormalZ * oz);
            double fx = ox - (NormalX * dist), fy = oy - (NormalY * dist), fz = oz - (NormalZ * dist);
            uv = new Vector2(
                (float)(AnchorU + (GradUX * fx) + (GradUY * fy) + (GradUZ * fz)),
                (float)(AnchorV + (GradVX * fx) + (GradVY * fy) + (GradVZ * fz)));
            var foot = new Vector3(
                (float)(AnchorX + fx), (float)(AnchorY + fy), (float)(AnchorZ + fz));
            return Math.Abs(dist) <= rayReach && Contains(foot);
        }

        private bool Contains(Vector3 p)
        {
            // Barycentric slack: an authored decoration sitting exactly on the quad's edge
            // belongs to the quad.
            const float edgeSlack = 1e-4f;

            for (int i = 0; i + 2 < Faces.Count; i += 3)
            {
                Vector3 a = Faces[i], b = Faces[i + 1], c = Faces[i + 2];
                var n = (b - a).Cross(c - a);
                float d = n.LengthSquared();
                if (d < 1e-9f)
                    continue;
                // Barycentric weights via the triangle's own normal, so the test works on a
                // tilted quad as well as a flat one.
                float w0 = (b - a).Cross(p - a).Dot(n) / d;
                float w1 = (c - b).Cross(p - b).Dot(n) / d;
                float w2 = (a - c).Cross(p - c).Dot(n) / d;
                if (w0 >= -edgeSlack && w1 >= -edgeSlack && w2 >= -edgeSlack)
                    return true;
            }
            return false;
        }
    }

    // One decoration kind: every template instance of the same decoration mesh (all 13
    // firtree1 placements share mesh + texture), plus where it sits in each grid cell.
    private sealed class Kind
    {
        // Where each decoration of this kind sits on the template's ground quad, as the quad's
        // own TEXTURE UV: OnQuad.Origin.X is u and Origin.Z is v, both in [0, 1), not metres, and
        // not relative to a corner (the winding differs between templates). Origin Y and the basis
        // are the decoration node's own, used by the solid path only. MeshLift is the offset from
        // that node down to the mesh it draws, and every stamp carries it, sprite or solid.
        public readonly List<(Transform3D OnQuad, Vector3 MeshLift)> CellPlacements = new();
        public readonly List<Transform3D> Instances = new(); // world placements

        // Parallel to Instances: each stamp's (near², far², 1/(far² − near²), 0) as the MultiMesh
        // custom data the fade shader reads; zero for a stamp that never fades.
        public readonly List<Color> Fades = new();

        // The decoration's FURTHER mesh-bearing nodes besides MeshIndex, each with its transform
        // into MeshIndex's own frame. A 3D decoration is a node chain and the original draws all
        // of it. C5's cb15a hangs two street walls and a roof cap off `o2` and `g8`, and loses
        // them when only the `Default` mesh is drawn. Empty for a card and for a single-mesh
        // decoration (docs/formats/clutter.md).
        public readonly List<(int MeshIndex, Transform3D Local)> ExtraParts = new();

        public int MeshIndex;
        public int NodeIndex;                    // a representative decoration node (draw order)
        public string Model = "";                // the decoration node's own name, e.g. firtree1.flt
        public string Label = "";                // texture (sprites) or node name (solids)
        public bool Solid;                       // a 3D decoration, not a billboard card
        public float Width, Height;              // sprite quad extents (sprites only)

        // The decoration model's own FacadeMode, which picks the sprite shader's face basis.
        // ⚠ Never guess it from the card's shape. A legacy extraction carries no ModelType, and
        // CylindricalY is what the whole install's clutter is apart from C5's lamp glows.
        public SceneBuilder.BillboardKind Billboard = SceneBuilder.BillboardKind.CylindricalY;

        // templates.zrd's `scale_range` for THIS kind's model, (1,1) when it authors none or no
        // spec was supplied. ⚠ It is the SOURCE kind's range that scales a substituted stamp,
        // see the roll in PlaceOnTriangle.
        public Vector2 ScaleRange = Vector2.One;

        // templates.zrd's `far_fade_range` bounds for THIS kind's model, (0,0) when it authors none.
        // ⚠ The SOURCE kind's, like the scale: a substituted stamp fades by the block it was
        // authored under, not the one it became.
        public Vector2 FarFadeMin, FarFadeMax;

        // `substitute` as a CUMULATIVE table: the running sum of the engine's own normalised
        // shares, paired with the kind each share lands in. A null target is a model the gamez
        // does not carry, it keeps its share and places nothing, as the original does. Empty
        // when the kind authors no substitution, which is every stamp landing in its own kind.
        public IReadOnlyList<(float Cumulative, Kind? Target)> Substitutes =
            Array.Empty<(float, Kind?)>();

        // The largest scale any instance of this kind actually received, so the billboard's cull
        // margin can grow with it. ⚠ Measured rather than derived from ScaleRange: a kind reached
        // by substitution is scaled by its SOURCES' ranges, not by its own.
        public float MaxScale = 1f;
        // The decoration model's own render flags (sprites only, a solid decoration draws
        // through SceneBuilder's materials, which read them themselves).
        public bool Lit = true;
        public bool Fogged = true;
    }

    // A template carries no metric size at all: the lattice spacing comes from the WORLD
    // POLYGON's UVs, so the only thing a template needs to know about its quad is which texture
    // it decorates.
    private sealed class Template
    {
        public readonly List<Kind> Kinds = new();

        public string GroundTexture = "";
    }

    // What one Build's lattice walk refused, and what it asserted about what it kept. Reported
    // as one line rather than per triangle: the counts are in the thousands, and an absent
    // category must be distinguishable from a truncated one.
    private sealed class LatticeStats
    {
        public int ZeroWorldArea;      // fan/strip artifacts of n-gons with repeated corners
        public int ZeroUvArea;         // no invertible affine map to recover a position through
        public int NoUvArray;          // the engine's null-UV gate
        public int NoClutterFlagged;   // the engine's 0x800 gate, the OTHER layer decorates here
        public int OverLargeLattice;   // refused by MaxLatticeCells
        public long WorstLatticeCells; // the largest lattice seen among those refused
        public int Placed;             // instances the lattice produced (after the seen dedup)
        public int OutsideSource;      // …of which any that missed their own source triangle
        public int Substituted;        // …of which any whose model the substitute roll changed
        public int SubstituteNothing;  // rolls that landed on a model the gamez does not carry
        public int DedupRejected;      // the quarter-metre `seen` set actually catching something.
                                       // C1B/C2/C3/C5 read 0, C1 reads 36 and C4 reads 139 from
                                       // an undiagnosed source, so a nonzero count in those two
                                       // is expected, not a regression signal.

        public void Report()
        {
            string worst = OverLargeLattice > 0 ? $" worst_lattice_cells={WorstLatticeCells}" : "";
            Log.Info("world", $"clutter uv lattice: placed={Placed} substituted={Substituted} substitute_nothing={SubstituteNothing} outside_source={OutsideSource} dedup_rejected={DedupRejected} skipped_no_clutter_flag={NoClutterFlagged} skipped_zero_world_area={ZeroWorldArea} skipped_zero_uv_area={ZeroUvArea} skipped_no_uv_array={NoUvArray} skipped_over_large_lattice={OverLargeLattice}{worst}");
            if (OutsideSource > 0)
                Log.Warn("world", $"clutter instances landed OUTSIDE their source triangle count={OutsideSource} of {Placed}, the UV containment test disagrees with the affine map");
        }
    }
}
