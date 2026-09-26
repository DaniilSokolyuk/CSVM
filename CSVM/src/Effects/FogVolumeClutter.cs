using System;
using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Effects;

/// <summary>
/// The original's ambient cloud field: the chapter's own <c>fogvol.zrd</c> clutter table scattered
/// through its gamez <c>fvol*</c> volumes. Schema, per-chapter numbers and the decoded/undecoded
/// split: docs/formats/fogvol.md.
/// Built once, world-anchored, zero per-frame cost, one <see cref="MultiMeshInstance3D"/> per
/// sprite kind, shared by every splitscreen pane; the shader billboards, fades and culls per view.
/// ⚠ Do not re-introduce a hand-tuned cloud field. Every count, radius, size, opacity and band
/// margin is authored data read here; the two that are not are marked at their own fields.
/// ⚠ C1B, C2 and C3 render nothing here on purpose (see fogvol.md); their ambient sky is the
/// world's own placed <c>cloudparent</c> sprites, built elsewhere.
/// </summary>
public sealed partial class FogVolumeClutter : Node3D
{
    /// <summary>The X/Z offset every lattice card ships displaced by, in metres, unless
    /// <c>--cloud-jitter=</c> says otherwise. A remake-only departure with nothing decoded behind
    /// it: the original's own perturbation leaves its lattice standing. Flown along the deck, the
    /// undisplaced field reads as rows. ⚠ Do not widen the decoded perturbation to chase it; the
    /// reading is in docs/org/cloudCards.md.</summary>
    public const float ShippedJitter = 30f;

    // A runaway guard, not a tuning knob: the shipped chapters place up to ~25k sprites (base
    // field plus the map-edge continuation), so this is several times the largest real field. It
    // can only bind if a future extraction reports a `distance` near zero or a volume far larger
    // than a map, both of which should be seen rather than swallowed.
    private const int MaxPlacements = 80_000;

    // Shape-classification threshold (docs/formats/fogvol.md): a volume no more than this many
    // card-heights thick reads as a sheet, which is what the map-edge continuation continues. A
    // judgement call from a clean gap in the volumes' own measured thickness, not authored data,
    // see the docs page for the measurements.
    private const float TopAnchorHeightFactor = 1.5f;

    // sqrt(3)/2, the row step of the scatter's staggered lattice as a multiple of the authored
    // `distance` (the column step). Decoded, not chosen: it is the spacing that makes a row
    // offset by half a column step equidistant from its neighbours (docs/org/cloudCards.md).
    private const float RowStepFactor = 0.8660254f;

    /// <summary>Sprites placed, summed over every kind, the authored volumes' own placements
    /// plus the map-edge continuation (<see cref="ExtensionCount"/>). Zero means nothing was
    /// built and <see cref="Create"/> returned null.</summary>
    public int InstanceCount { get; private set; }

    /// <summary>Sprites placed by the map-edge continuation alone, zero in every chapter whose
    /// <c>fvol*</c> volumes carry no map-spanning slab (C1B,
    /// C2, C3 render nothing at all; C5's strips and C1C's build-ups are local geometry and are
    /// never extended). <see cref="InstanceCount"/> minus this is the count the authored volumes
    /// alone would have produced.</summary>
    public int ExtensionCount { get; private set; }

    /// <summary>Sprites placed inside the authored volumes alone, <see cref="InstanceCount"/>
    /// minus <see cref="ExtensionCount"/>, i.e. what the in-volume scatter produces before the
    /// continuation runs. Unaffected by the extension, which runs after the base draw and never
    /// reads the shared RNG stream the base draw uses.</summary>
    public int BaseCount => InstanceCount - ExtensionCount;

    /// <summary>Per-kind counts of the build, e.g. "cloudsprite1 x4471 (cloud1.tif, fade
    /// 2000-3000..3100-3500 m)", the evidence that the authored weights and both endpoints of the
    /// per-sprite band reached the field.</summary>
    public string Summary { get; private set; } = "";

    /// <summary>Builds the chapter's ambient cloud field, or null when the data asks for none:
    /// no <c>fogvol.zrd</c>, no <c>fvol*</c> volume, no resolvable template, or a
    /// <c>distance</c> that is not a usable mean spacing. Add the result to the world root at
    /// identity, its instance transforms are absolute world coordinates.</summary>
    /// <param name="jitter"><c>--cloud-jitter=</c>, metres: a remake-only uniform X/Z offset per
    /// lattice card, applied after every decoded draw. 0 leaves the decoded field untouched.</param>
    public static FogVolumeClutter? Create(GameZ gamez, TextureArchive textures,
        FogVolumeSpec? spec, IReadOnlyList<FogVolumeBox> volumes, float jitter = 0f)
    {
        if (spec == null)
        {
            return null;
        }
        if (volumes.Count == 0 || spec.Clutter.Count == 0)
        {
            // Said out loud: "this chapter has no ambient clouds" is a conclusion drawn from
            // separate absences, and a silent one reads exactly like a reader that broke.
            Log.Info("world", $"fogvol: no ambient cloud field volumes={volumes.Count} clutter={spec.Clutter.Count} clutter_key={spec.HasClutterKey}");
            return null;
        }
        if (spec.Distance < 1f)
        {
            Log.Info("world", $"fogvol: unusable scatter period distance={spec.Distance}");
            return null;
        }

        var kinds = ResolveKinds(gamez, textures, spec);
        if (kinds.Count == 0)
        {
            return null;
        }

        var field = new FogVolumeClutter();
        field.Scatter(spec, volumes, kinds, jitter);
        if (field.InstanceCount == 0)
        {
            field.QueueFree();
            return null;
        }
        field.Build(gamez, kinds);
        return field;
    }

    // The gamez side of one clutter alternative: the sprite card its template root carries.
    // Resolved by ClutterBuilder's own template rule, a parentless Object3d of that name whose
    // first meshed descendant is the card (docs/formats/clutter.md).
    private static List<Kind> ResolveKinds(GameZ gamez, TextureArchive textures, FogVolumeSpec spec)
    {
        var kinds = new List<Kind>();
        foreach (var block in spec.Clutter)
        {
            foreach (var reference in block.Nodes)
            {
                var root = ClutterBuilder.FindTemplateRoot(gamez, reference.Node);
                var card = root == null ? null : FirstWithMesh(gamez, root);
                if (card == null)
                {
                    // Retail-data-normal for C1B/C2/C3, see the class remarks.
                    Log.Info("world", $"fogvol clutter template not in gamez template={reference.Node}");
                    continue;
                }
                var mesh = gamez.Meshes[card.MeshIndex];
                var texture = FirstTexture(gamez, mesh);
                kinds.Add(new Kind
                {
                    Name = reference.Node,
                    Block = block,
                    Weight = block.Weight * reference.Weight,
                    MeshIndex = card.MeshIndex,
                    Texture = texture == null ? null : textures.Find(texture),
                    TextureName = texture ?? "?",
                    Lit = mesh.Lighting,
                    Fogged = mesh.Fog,
                    Radius = CardRadius(mesh),
                });
            }
        }
        return kinds;
    }

    private static GameZNode? FirstWithMesh(GameZ gamez, GameZNode node)
    {
        if (node.MeshIndex >= 0 && node.MeshIndex < gamez.Meshes.Count
            && gamez.Meshes[node.MeshIndex].Polygons.Count > 0)
        {
            return node;
        }
        foreach (var childIndex in node.Children)
        {
            if (childIndex >= 0 && childIndex < gamez.Nodes.Count
                && FirstWithMesh(gamez, gamez.Nodes[childIndex]) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static string? FirstTexture(GameZ gamez, GameZMesh mesh)
    {
        foreach (var poly in mesh.Polygons)
        {
            if (poly.MaterialIndex >= 0 && poly.MaterialIndex < gamez.Materials.Count
                && gamez.Materials[poly.MaterialIndex].TextureName is { } tex)
            {
                return tex;
            }
        }
        return null;
    }

    // Half the card's largest local extent, what the billboard can swing outside the MultiMesh's
    // static AABB, so it sizes ExtraCullMargin.
    private static float CardRadius(GameZMesh mesh)
    {
        float radius = 0f;
        foreach (var v in mesh.Vertices)
        {
            radius = Mathf.Max(radius, Mathf.Max(Mathf.Abs(v.X), Mathf.Max(Mathf.Abs(v.Y), Mathf.Abs(v.Z))));
        }
        return radius;
    }

    private static float Lerp(Vector2 range, float t) => range.X + ((range.Y - range.X) * t);

    // One sprite's shader custom data: the polygon normal its draw distance is scaled against, and
    // the single draw `t` its fade band is interpolated with. The normal is remapped onto [0, 1]
    // because a MultiMesh custom-data slot is a Color, whose range a future Godot may narrow.
    private static Color BandData(Vector3 normal, float t) => new(
        (normal.X * 0.5f) + 0.5f, (normal.Y * 0.5f) + 0.5f, (normal.Z * 0.5f) + 0.5f, t);

    // The card's own source geometry (verts, UVs and the authored vertex colours), triangulated by
    // the same fan/strip rule as SceneBuilder.EmitPolygon, the cloud cards are tri-strips.
    // Recentred on the quad's centroid so the billboard pivots at its middle, like the placed cloud
    // sprites (SceneBuilder recenters those the same way); the shipped cards are already centred to
    // within 3 mm, so this moves nothing in this install and keeps a future one honest.
    private static ArrayMesh BuildCardMesh(GameZ gamez, int meshIndex)
    {
        var mesh = gamez.Meshes[meshIndex];
        var centre = Vector3.Zero;
        foreach (var v in mesh.Vertices)
        {
            centre += v;
        }
        if (mesh.Vertices.Count > 0)
        {
            centre /= mesh.Vertices.Count;
        }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var poly in mesh.Polygons)
        {
            int n = poly.VertexIndices.Count;
            int first = poly.TriangleStrip ? 0 : 1;
            int last = poly.TriangleStrip ? n - 3 : n - 2;
            for (int i = first; i <= last; i++)
            {
                Span<int> corners = poly.TriangleStrip
                    ? stackalloc int[] { i, i + 1, i + 2 }
                    : stackalloc int[] { 0, i, i + 1 };
                foreach (int corner in corners)
                {
                    st.SetNormal(CardNormal(mesh, poly, corner));
                    // The authored colour, unscaled. ⚠ Never scale it here: no per-card colour term
                    // exists and a brightness gap on this population is coverage
                    // (docs/org/cloudCards.md). The one term the original applies is per vertex.
                    st.SetColor(poly.VertexColors != null && corner < poly.VertexColors.Count
                        ? poly.VertexColors[corner]
                        : Colors.White);
                    if (poly.UvCoords != null && corner < poly.UvCoords.Count)
                    {
                        st.SetUV(poly.UvCoords[corner]);
                    }
                    st.AddVertex(mesh.Vertices[poly.VertexIndices[corner]] - centre);
                }
            }
        }
        var arrayMesh = new ArrayMesh();
        st.Commit(arrayMesh);
        return arrayMesh;
    }

    // One corner's authored normal, the geometry the original's per-vertex facade light runs on
    // (docs/org/vertexLighting.md). Every shipped card carries three under `normal_indices
    // [1, 1, 0, 2]`. The top corners share one along the card's own +Y; the bottom two carry the
    // pair pointing out of it along +Z. A card without them falls back to the quad's own facing,
    // which the unlit arm never reads.
    private static Vector3 CardNormal(GameZMesh mesh, GameZPolygon poly, int corner)
    {
        if (poly.NormalIndices != null && corner < poly.NormalIndices.Count)
        {
            int index = poly.NormalIndices[corner];
            if (index >= 0 && index < mesh.Normals.Count && mesh.Normals[index].LengthSquared() > 1e-12f)
            {
                return mesh.Normals[index].Normalized();
            }
        }
        return Vector3.Back;
    }

    // The card's SphericalY facade pose (hand-rolled: a MultiMesh cannot use Godot's billboard
    // flag), plus the clutter fade, whose draw distance is scaled by the viewing angle against
    // each sprite's own polygon normal. `cull` collapses the quad to a point once the fade has
    // dropped it, so a sprite outside its draw distance costs no fragments, what lets the whole
    // field be one static MultiMesh with no streaming. ⚠ The fade distance is the true 3D one, not
    // the fog's horizontal cylinder: a cloud overhead is as far away as one on the horizon.
    private static string ShaderCode(bool lit, bool fogged)
    {
        // ⚠ A `lighting: true` card takes the original's PER-VERTEX term on normals turned by the
        // same `face` the quad takes, never the collapsed csky_world_light and never another basis
        // (docs/org/vertexLighting.md). C1 and C4 author false and take nothing.
        string varying = lit ? "\nvarying float v_light;" : string.Empty;
        string vertexLight = lit
            ? "\n    v_light = csky_sun_vertex_light(face * NORMAL);"
            : string.Empty;
        // The product is clamped, not the factor. The original clamps after multiplying the
        // authored colour, and it clamps in the framebuffer's own gamma space. COLOR is still in
        // that space here.
        string vcol = lit ? "clamp(COLOR.rgb * v_light, 0.0, 1.0)" : "COLOR.rgb";
        string albedo = fogged
            ? "    vec3 fog_world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;\n"
              + "    float fog_amt = csky_fog_amount(fog_world, CAMERA_POSITION_WORLD);\n"
              + "    ALBEDO = mix(col.rgb, csky_fog_color, fog_amt);"
            : "    ALBEDO = col.rgb;";
        return $$"""
            shader_type spatial;
            render_mode blend_mix, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

            uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_disable;
            uniform vec2 far_fade_0 = vec2(1e8, 1e9);  // far_fade_range[0], metres: band start, gone
            uniform vec2 far_fade_1 = vec2(1e8, 1e9);  // far_fade_range[1], the band's other end

            #include "res://shaders/csky_atmosphere.gdshaderinc"
            #include "res://shaders/csky_srgb.gdshaderinc"
            #include "res://shaders/csky_clutter_fade.gdshaderinc"
            #include "res://shaders/csky_facade.gdshaderinc"
            // The chapter's mip LOD bias is one device render state in the original. A card picks
            // its level as every other mip-mapped arm does.
            #include "res://shaders/csky_mip_bias.gdshaderinc"

            varying flat float v_alpha;{{varying}}

            void vertex() {
                vec3 origin = MODEL_MATRIX[3].xyz;
                // INSTANCE_CUSTOM: xyz the sprite's own polygon normal off the [0,1] encoding,
                // w the one draw its whole fade band is interpolated with.
                vec2 band = mix(far_fade_0, far_fade_1, INSTANCE_CUSTOM.w);
                vec2 band_sq = band * band;
                vec4 fade = vec4(band_sq, 1.0 / max(band_sq.y - band_sq.x, 1.0), 0.0);
                v_alpha = csky_clutter_fade_alpha_angled(
                    origin, CAMERA_POSITION_WORLD, fade, INSTANCE_CUSTOM.xyz * 2.0 - 1.0);
                float cull = step(0.004, v_alpha);
                mat3 face = csky_facade_spherical(origin, CAMERA_POSITION_WORLD);
                MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
                    vec4(face[0], 0.0), vec4(face[1], 0.0), vec4(face[2], 0.0), MODEL_MATRIX[3]);
                MODELVIEW_MATRIX[0] *= length(MODEL_MATRIX[0].xyz) * cull;
                MODELVIEW_MATRIX[1] *= length(MODEL_MATRIX[1].xyz) * cull;
                MODELVIEW_MATRIX[2] *= length(MODEL_MATRIX[2].xyz);{{vertexLight}}
            }

            void fragment() {
                vec4 col = vec4(csky_srgb_to_linear({{vcol}}), COLOR.a) * csky_sample_albedo(albedo_tex, UV);
            {{albedo}}
                ALPHA = col.a * v_alpha;
            }
            """;
    }

    // The scatter: every authored face of every volume carries a staggered lattice in its own
    // plane, `distance * sqrt(3)/2` by `distance`. Each point that falls inside that face's
    // outline places one sprite, weighted over the resolved clutter table. `distance` is still the
    // field's areal DENSITY, an authored mean spacing, not a lattice phase.
    // See docs/org/cloudCards.md for the decoded walk and docs/formats/fogvol.md for the density.
    private void Scatter(FogVolumeSpec spec, IReadOnlyList<FogVolumeBox> volumes, List<Kind> kinds,
        float jitter)
    {
        Name = "fog_volume_clutter";
        float period = spec.Distance;
        float totalWeight = 0f;
        foreach (var kind in kinds)
        {
            totalWeight += Mathf.Max(kind.Weight, 0f);
        }
        if (totalWeight <= 0f)
        {
            return;
        }

        // The authored card's own extent (docs/formats/fogvol.md's "card size", e.g. 132.3 m for
        // C1/C1C/C2B/C4, 70 m for C5), every kind in a chapter shares one, so the largest among
        // them is the card height the map-spanning-slab test below measures a volume against.
        float cardHeight = 0f;
        foreach (var kind in kinds)
        {
            cardHeight = Mathf.Max(cardHeight, kind.Radius * 2f);
        }

        // One draw sequence off the master seed's cloud stream, fixed volume/face/lattice order, so
        // the whole field is a function of the seed. No camera, pane or frame is read
        // here, so world-anchoring and determinism are the same property.
        var rng = Rng.NewSystemRandom(Rng.Clouds);

        // ⚠ Keep the band draws off Rng.Clouds entirely. One there, even taken before the loop,
        // reseeds the placements of any later field built in the same process. A suite that builds
        // two chapters then sees the second one's counts move.
        var bandRng = Rng.NewSystemRandom(Rng.CloudBands);

        // The remake-only X/Z jitter draws off its own stream, and only when asked for. Every
        // value of the knob lays the same decoded field, and moves only where each card sits.
        var jitterRng = jitter > 0f ? Rng.NewSystemRandom(Rng.CloudJitter) : null;

        // One pass per volume's own faces, not per clutter block, weights are already flattened
        // into Kind.Weight. Overlapping volumes (C1C's build-ups over its own slab) each scatter
        // their own faces; every lattice is anchored on its face, not on the world origin.
        foreach (var volume in volumes)
        {
            // The per-volume reference point the perpendicular offset runs away from. The decode
            // reads it off the volume record and no further. On a slab's top face the direction it
            // gives is the centre's: up near the middle, tilting outward at the rim.
            var reference = volume.Box.GetCenter();
            foreach (var face in volume.Polygons ?? Array.Empty<FogVolumeFace>())
            {
                ScatterFace(face, reference, period, kinds, totalWeight, rng, bandRng, jitter, jitterRng);
            }
        }

        // Continue the map-spanning slab's own cell field past the base map. Runs after every
        // authored volume above has drawn, and touches no state that loop reads. It never calls
        // `rng` at all, so it cannot realign the interior placements.
        ExtendPastMapEdge(volumes, kinds, period, cardHeight, totalWeight);
    }

    // One authored face's own staggered lattice, laid in the face's plane on the basis
    // U = unit(v1 - v0), V = cross(U, n). It steps `distance * sqrt(3)/2` along U and `distance`
    // along V, each extent cut into a whole number of steps. Every other row is offset by half a
    // step, and every point is tested against the face's own outline. The draws per accepted point
    // are the decoded order: kind, band, perpendicular offset, perturbation, scale
    // (docs/org/cloudCards.md).
    private void ScatterFace(in FogVolumeFace face, Vector3 reference, float period,
        List<Kind> kinds, float totalWeight, Random rng, Random bandRng, float jitter, Random? jitterRng)
    {
        var origin = face.Vertices[0];
        var along = face.Vertices[1] - origin;
        if (along.LengthSquared() <= 0f)
        {
            return;
        }
        var u = along.Normalized();
        var v = u.Cross(face.Normal);

        float uMin = 0f, uMax = 0f, vMin = 0f, vMax = 0f;
        foreach (var vertex in face.Vertices)
        {
            var offset = vertex - origin;
            float du = offset.Dot(u), dv = offset.Dot(v);
            uMin = Mathf.Min(uMin, du);
            uMax = Mathf.Max(uMax, du);
            vMin = Mathf.Min(vMin, dv);
            vMax = Mathf.Max(vMax, dv);
        }

        float stepU = period * RowStepFactor;
        int rows = Mathf.FloorToInt((uMax - uMin) / stepU);
        int columns = Mathf.FloorToInt((vMax - vMin) / period);
        float Rand(float a, float b) => a + ((float)rng.NextDouble() * (b - a));

        for (int row = 0; row <= rows && InstanceCount < MaxPlacements; row++)
        {
            float stagger = (row & 1) == 0 ? 0f : period * 0.5f;
            for (int column = 0; column <= columns && InstanceCount < MaxPlacements; column++)
            {
                var point = origin + (u * (uMin + (row * stepU)))
                    + (v * (vMin + (column * period) + stagger));
                if (!face.Contains(point))
                {
                    continue;
                }

                // Weighted draw over the whole table (block weight x node weight).
                float pick = Rand(0f, totalWeight);
                var kind = kinds[kinds.Count - 1];
                foreach (var candidate in kinds)
                {
                    pick -= Mathf.Max(candidate.Weight, 0f);
                    if (pick <= 0f)
                    {
                        kind = candidate;
                        break;
                    }
                }

                var block = kind.Block;
                float band = (float)bandRng.NextDouble();
                // The offset runs along the direction from the volume's reference point, not the
                // face normal and not +Y. On a slab's top face that is up in the middle, tilting
                // outward at the rim, which is the shape the decode describes.
                var away = point - reference;
                away = away.LengthSquared() > 0f ? away.Normalized() : face.Normal;
                float perp = Lerp(block.PerpDistRange, (float)rng.NextDouble());
                // One magnitude, then an independent draw on each axis, so the perturbation is a
                // displacement in space rather than a ring around the lattice point.
                float perturb = Lerp(block.PerturbDistRange, (float)rng.NextDouble());
                var displace = new Vector3(Rand(-0.5f, 0.5f), Rand(-0.5f, 0.5f), Rand(-0.5f, 0.5f))
                    * perturb;
                float scale = Lerp(block.ScaleRange, (float)rng.NextDouble());
                // Not the original's: the decoded ±10 m on a 130 m lattice leaves its rows
                // standing. This widens only the horizontal spread, after every decoded draw.
                if (jitterRng != null)
                {
                    displace += new Vector3(
                        ((float)jitterRng.NextDouble() * 2f - 1f) * jitter, 0f,
                        ((float)jitterRng.NextDouble() * 2f - 1f) * jitter);
                }

                kind.Placements.Add(new Transform3D(
                    Basis.Identity.Scaled(new Vector3(scale, scale, scale)),
                    point + (away * perp) + displace));
                kind.Bands.Add(BandData(face.Normal, band));
                InstanceCount++;
            }
        }
    }

    // Identifies the chapter's map-spanning slab from data (FogVolumeSpec.FindMapSpanningSlab,
    // pure geometry, never a chapter name or a hardcoded fvol1..9 range). If one exists, it tiles
    // that slab's own `distance`-cell field outward past the map rim. C1C's build-ups and C5's
    // strips are too tall to read as a sheet and are never extended. C1B/C2/C3 ship no fvol* at
    // all (docs/formats/fogvol.md's Map-edge continuation section).
    // ⚠ The ring continues the slab's TOP face alone, one authored face, not the volume.
    private void ExtendPastMapEdge(IReadOnlyList<FogVolumeBox> volumes, List<Kind> kinds,
        float period, float cardHeight, float totalWeight)
    {
        var (found, skipReason) = FogVolumeSpec.FindMapSpanningSlab(volumes, cardHeight, TopAnchorHeightFactor);
        if (skipReason != null)
        {
            string msg = $"fogvol: map-edge continuation skipped, {skipReason}";
            Log.Info("world", $"{msg}");
        }
        if (found is not { } slab)
        {
            return;
        }

        // Bounded at the largest authored far_fade, not at MapEdgeExtender's own reach. The shader
        // collapses a sprite once its band has dropped it, and the view-angle law reaches at most
        // half that band horizontally. A wider ring buys nothing (docs/formats/fogvol.md).
        float radius = 0f;
        foreach (var kind in kinds)
        {
            radius = Mathf.Max(radius, kind.Block.FarFade.Y);
        }
        if (radius <= 0f)
        {
            return;
        }

        float bx0 = slab.MinX, bx1 = slab.MaxX, bz0 = slab.MinZ, bz1 = slab.MaxZ, topY = slab.TopY;
        // Eight regions tile the radius-margin ring with no gap and no overlap: four edge strips,
        // four corner squares. Every inner edge is exactly bx0/bx1/bz0/bz1, the same coordinate
        // the interior loop clips its outermost cell to, so the join is exact by construction.
        EmitExtensionRegion(bx0 - radius, bx0, bz0, bz1, period, topY, kinds, totalWeight); // west
        EmitExtensionRegion(bx1, bx1 + radius, bz0, bz1, period, topY, kinds, totalWeight); // east
        EmitExtensionRegion(bx0, bx1, bz0 - radius, bz0, period, topY, kinds, totalWeight); // south
        EmitExtensionRegion(bx0, bx1, bz1, bz1 + radius, period, topY, kinds, totalWeight); // north
        EmitExtensionRegion(bx0 - radius, bx0, bz0 - radius, bz0, period, topY, kinds, totalWeight); // sw
        EmitExtensionRegion(bx1, bx1 + radius, bz0 - radius, bz0, period, topY, kinds, totalWeight); // se
        EmitExtensionRegion(bx0 - radius, bx0, bz1, bz1 + radius, period, topY, kinds, totalWeight); // nw
        EmitExtensionRegion(bx1, bx1 + radius, bz1, bz1 + radius, period, topY, kinds, totalWeight); // ne
    }

    // One rectangular slice of the extension ring, tiled with the same distance x distance cells
    // as the interior loop. Two deliberate differences: every cell is accepted unconditionally,
    // there is no authored shape out here to test against, and each cell draws off its own
    // hashed generator (`Rng.NewSystemRandom(Rng.Clouds, gx, gz)`), not the interior's shared
    // stream, so `--det` stays stable regardless of how many cells the enumeration bounds admit.
    // Y is the slab's own constant top plus `perp_dist_range`, inheriting the interior's rule.
    private void EmitExtensionRegion(float x0, float x1, float z0, float z1, float period,
        float topY, List<Kind> kinds, float totalWeight)
    {
        if (x1 <= x0 || z1 <= z0)
        {
            return;
        }
        int gx0 = Mathf.CeilToInt(x0 / period), gx1 = Mathf.FloorToInt(x1 / period);
        int gz0 = Mathf.CeilToInt(z0 / period), gz1 = Mathf.FloorToInt(z1 / period);
        // A region thinner than one period (never true of any shipped chapter's far_fade against
        // its 80-130 m `distance`) still gets exactly one remainder cell rather than none.
        if (gx1 < gx0)
        {
            gx1 = gx0 = Mathf.RoundToInt((x0 + x1) * 0.5f / period);
        }
        if (gz1 < gz0)
        {
            gz1 = gz0 = Mathf.RoundToInt((z0 + z1) * 0.5f / period);
        }

        for (int gx = gx0; gx <= gx1 && InstanceCount < MaxPlacements; gx++)
        {
            float cx0 = gx == gx0 ? x0 : (gx * period) - (period * 0.5f);
            float cx1 = gx == gx1 ? x1 : (gx * period) + (period * 0.5f);
            for (int gz = gz0; gz <= gz1 && InstanceCount < MaxPlacements; gz++)
            {
                float cz0 = gz == gz0 ? z0 : (gz * period) - (period * 0.5f);
                float cz1 = gz == gz1 ? z1 : (gz * period) + (period * 0.5f);

                var rng = Rng.NewSystemRandom(Rng.Clouds, gx, gz);
                float Rand(float a, float b) => a + ((float)rng.NextDouble() * (b - a));

                float x = Rand(cx0, cx1);
                float z = Rand(cz0, cz1);

                float pick = Rand(0f, totalWeight);
                var kind = kinds[kinds.Count - 1];
                foreach (var candidate in kinds)
                {
                    pick -= Mathf.Max(candidate.Weight, 0f);
                    if (pick <= 0f)
                    {
                        kind = candidate;
                        break;
                    }
                }

                var block = kind.Block;
                float bearing = Rand(0f, Mathf.Tau);
                float perturb = Lerp(block.PerturbDistRange, (float)rng.NextDouble());
                float scale = Lerp(block.ScaleRange, (float)rng.NextDouble());
                kind.Placements.Add(new Transform3D(
                    Basis.Identity.Scaled(new Vector3(scale, scale, scale)),
                    new Vector3(
                        x + (Mathf.Sin(bearing) * perturb),
                        topY + Lerp(block.PerpDistRange, (float)rng.NextDouble()),
                        z + (Mathf.Cos(bearing) * perturb))));
                // ⚠ The band draw comes last, after every draw a placement reads, so the ring's
                // positions do not move. The slab's own top face continues out here: +Y, like the
                // face the interior scatters over.
                kind.Bands.Add(BandData(Vector3.Up, (float)rng.NextDouble()));
                InstanceCount++;
                ExtensionCount++;
            }
        }
    }

    private void Build(GameZ gamez, List<Kind> kinds)
    {
        var parts = new List<string>();
        var meshCache = new Dictionary<int, ArrayMesh>();
        foreach (var kind in kinds)
        {
            if (kind.Placements.Count == 0)
            {
                continue;
            }
            if (!meshCache.TryGetValue(kind.MeshIndex, out var mesh))
            {
                meshCache[kind.MeshIndex] = mesh = BuildCardMesh(gamez, kind.MeshIndex);
            }
            var mat = new ShaderMaterial { Shader = new Shader { Code = ShaderCode(kind.Lit, kind.Fogged) } };
            if (kind.Texture != null)
            {
                mat.SetShaderParameter("albedo_tex", kind.Texture);
            }
            mat.SetShaderParameter("far_fade_0", kind.Block.FarFadeNear);
            mat.SetShaderParameter("far_fade_1", kind.Block.FarFade);

            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                // ⚠ Before InstanceCount, or the band has no slot in the buffer.
                UseCustomData = true,
                Mesh = mesh,
                InstanceCount = kind.Placements.Count,
            };
            for (int i = 0; i < kind.Placements.Count; i++)
            {
                mm.SetInstanceTransform(i, kind.Placements[i]);
                mm.SetInstanceCustomData(i, kind.Bands[i]);
            }
            AddChild(new MultiMeshInstance3D
            {
                Multimesh = mm,
                MaterialOverride = mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                // The billboard swings vertices outside the instances' static AABB.
                ExtraCullMargin = kind.Radius * Mathf.Max(kind.Block.ScaleRange.Y, 1f),
                Name = kind.Name,
            });
            parts.Add($"{kind.Name} x{kind.Placements.Count} ({kind.TextureName}, "
                      + $"fade {kind.Block.FarFadeNear.X:0}-{kind.Block.FarFadeNear.Y:0}"
                      + $"..{kind.Block.FarFade.X:0}-{kind.Block.FarFade.Y:0} m)");
        }
        Summary = string.Join(", ", parts);
    }

    // One alternative of the resolved scatter table: a clutter block paired with one of the
    // template nodes it names, and everything the gamez says about that node's sprite card.
    private sealed class Kind
    {
        public readonly List<Transform3D> Placements = new();

        // Parallel to Placements, one entry per instance, see BandData.
        public readonly List<Color> Bands = new();

        public string Name = "";
        public FogClutter Block = null!;
        public float Weight;
        public int MeshIndex;
        public ImageTexture? Texture;
        public string TextureName = "";
        public bool Lit = true;
        public bool Fogged = true;
        public float Radius;
    }
}
