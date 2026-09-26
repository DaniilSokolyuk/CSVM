using System;
using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Effects;

/// <summary>
/// Enhanced mode's cloud field, a documented divergence (docs/architecture/Root.md): static volumetric
/// impostors. At session start one pass raymarches eight procedural cumulus volumes into an atlas
/// (<see cref="BakeShader"/>), then a single MultiMesh of camera-facing cards places them across the
/// chapter at its authored cloud height, overcast where the chapter has a deck, scattered where not.
/// The raymarch runs once, so the field costs one sorted billboard draw and carries no per-frame
/// noise. It stands in for the flat deck (<c>WeatherRig.DeckReplaced</c>), the fvol sprite field, the
/// cloudparent clusters and the dome's painted cards, which <c>GameSession</c> hides.
/// ⚠ Keep the cards sorted back to front: they blend without writing depth.
/// </summary>
public sealed partial class VolumetricClouds : Node3D
{
    // TUNE: the layer floor for a chapter that places no cloud geometry at all (C2, C3, C5).
    private const float DefaultFloor = 1500f;

    // The atlas: AtlasCols x AtlasRows variants, TilePixels square each. Rows 0 and 1 are cumulus
    // with a flat base, row 2 a tower's crown (no base, to stack over a cumulus), row 3 a flat
    // stratocumulus sheet; the generator picks a row by the part it is building.
    private const int AtlasCols = 4;
    private const int AtlasRows = 4;
    private const int TilePixels = 512;

    // TUNE, judged at the controls: how many cards, how big and how tall the layer is. Overcast
    // closes the sky the way the flat deck did; scattered leaves several clouds over a chapter.
    private const int OvercastCount = 70;
    private const float OvercastMinWidth = 2800f, OvercastMaxWidth = 5500f;
    private const float ScatteredMinWidth = 2200f, ScatteredMaxWidth = 4800f;
    private const float LayerSpread = 700f;

    // The overcast field overhangs the map by this fraction per side, so the horizon is not an empty
    // rim; a scattered one keeps to the map, where it would otherwise thin out around the player.
    private const float OvercastOverhang = 0.35f;
    private const float ScatteredOverhang = 0f;

    // TUNE: how much of a region the original fills with cloud one cloud stands for, the sparse
    // scatter over a chapter that authors none, and how deep into a tall region clouds may climb.
    private const float AuthoredAreaPerCloud = 8_000_000f;
    private const int SparseCount = 14;
    private const float MergeCell = 4096f;
    private const float AuthoredMaxDepth = 1500f;

    // How many frames one atlas tile may take before the field gives up on it.
    private const int BakeRetryFrames = 30;

    // Only re-sort once the camera has moved this far: the order barely changes between frames.
    private const float ResortDistance = 25f;

    // The one-time bake: each tile raymarches a cumulus built from a few seeded blobs eroded by fbm
    // noise, flat-bottomed, lit from above. R carries the sunlit term, G the ambient term, A the
    // opacity, so the cards can relight the cloud with each zone's own SUNLIGHT.
    private const string BakeShader = """
        shader_type canvas_item;

        const int COLS = 4;
        // The tile this pass renders; the viewport is one tile.
        uniform int tile = 0;
        const int STEPS = 112;
        const int LIGHT_STEPS = 10;

        vec3 hash33(vec3 p) {
            p = fract(p * vec3(0.1031, 0.1030, 0.0973));
            p += dot(p, p.yxz + 33.33);
            return fract((p.xxy + p.yxx) * p.zyx);
        }

        float hash13(vec3 p) {
            p = fract(p * 0.1031);
            p += dot(p, p.zyx + 31.32);
            return fract((p.x + p.y) * p.z);
        }

        // Gradient (Perlin) noise in [0, 1].
        float perlin(vec3 p) {
            vec3 i = floor(p);
            vec3 f = fract(p);
            vec3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
            float n = 0.0;
            float r[8];
            for (int k = 0; k < 8; k++) {
                vec3 o = vec3(float(k & 1), float((k >> 1) & 1), float((k >> 2) & 1));
                vec3 g = hash33(i + o) * 2.0 - 1.0;
                r[k] = dot(g, f - o);
            }
            n = mix(mix(mix(r[0], r[1], u.x), mix(r[2], r[3], u.x), u.y),
                    mix(mix(r[4], r[5], u.x), mix(r[6], r[7], u.x), u.y), u.z);
            return n * 0.5 + 0.5;
        }

        // Inverted cellular (Worley) noise: 1 at feature points, the billowy lobes of cumulus.
        float worley(vec3 p) {
            vec3 i = floor(p);
            vec3 f = fract(p);
            float d = 1.0;
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++) {
                vec3 o = vec3(float(x), float(y), float(z));
                vec3 pt = o + hash33(i + o);
                d = min(d, dot(pt - f, pt - f));
            }
            return 1.0 - sqrt(d);
        }

        float worley_fbm(vec3 p) {
            return worley(p) * 0.625 + worley(p * 2.0) * 0.25 + worley(p * 4.0) * 0.125;
        }

        float remap(float v, float lo, float hi, float nlo, float nhi) {
            return nlo + (v - lo) * (nhi - nlo) / max(hi - lo, 0.0001);
        }

        // One cloud part in [-1, 1]^3 (Nubis): a mass of spheres shaped by kind (0 a cumulus with a
        // flat base under a rounded top, 2 a tower's crown that fades out downwards, 3 a flat sheet),
        // its surface carved into cauliflower billows by inverted Worley at two scales, then its rim
        // frayed by a finer Perlin-Worley so the edge thins into wisps instead of a smooth outline.
        float density(vec3 p, float seed, int kind) {
            vec3 q = p + seed * 11.7;
            float shape = -1.0;
            for (int k = 0; k < 7; k++) {
                float fk = float(k) + seed * 7.0;
                float h = hash13(vec3(fk, 2.7, seed));
                float cx = hash13(vec3(fk, 1.3, seed)) - 0.5;
                float cz = hash13(vec3(fk, 4.1, seed)) * 0.5 - 0.25;
                float rr = hash13(vec3(fk, 5.9, seed)) * 0.1;
                vec3 c;
                float r;
                vec3 squash;
                if (kind == 2) {
                    c = vec3(cx * 0.8, -0.1 + h * 0.25, cz);
                    r = mix(0.4, 0.26, h) + rr;
                    squash = vec3(1.0, 1.0, 1.0);
                } else if (kind == 3) {
                    c = vec3(cx * 1.4, -0.3 + h * 0.1, cz * 1.4);
                    r = 0.3 + rr * 2.0;
                    squash = vec3(1.0, 2.4, 1.0);
                } else {
                    c = vec3(cx, -0.25 + h * 0.35, cz);
                    // Higher spheres are smaller: a broad base under a narrower crown.
                    r = mix(0.42, 0.24, h) + rr;
                    squash = vec3(1.0, 1.15, 1.0);
                }
                shape = max(shape, 1.0 - length((p - c) * squash) / r);
            }
            if (kind == 2) {
                // A crown sits over its tower's lower card: no base of its own, it thins out downwards.
                shape -= (1.0 - smoothstep(-0.5, -0.05, p.y)) * 0.9;
            } else {
                // The base is flat, as a condensation level is, but ragged: a ruler-straight underside
                // reads as a line across the clouds behind it.
                // It thins over a band rather than ending on an edge, so from below it dissolves.
                float base_y = -0.46 + (perlin(q * vec3(3.0, 0.0, 3.0) + 5.0) - 0.5) * 0.12;
                shape -= (1.0 - smoothstep(base_y, base_y + 0.26, p.y)) * 1.1;
            }
            // ⚠ Fade before the tile's edges (p.y spans [-0.55, 0.45], p.x [-1, 1]): a crown that
            // reaches them is cut flat, a straight line across the sky.
            shape -= smoothstep(0.22, 0.43, p.y) + smoothstep(0.75, 0.97, abs(p.x));
            float fq = kind == 3 ? 5.0 : 3.2;
            float billow = worley_fbm(q * fq) * 0.6 + worley_fbm(q * fq * 2.5) * 0.4;
            float carved = shape - (1.0 - billow) * 0.75;
            float fray = remap(perlin(q * 14.0), worley(q * 14.0) - 1.0, 1.0, 0.0, 1.0);
            // Erode hardest where the cloud is thinnest (Nubis): the rim goes to wisps, the core holds.
            carved -= (1.0 - fray) * mix(0.3, 0.08, clamp(carved * 3.0, 0.0, 1.0));
            return clamp(carved / 0.45, 0.0, 1.0);
        }

        void fragment() {
            vec2 cell = vec2(float(tile % COLS), float(tile / COLS));
            vec2 local = UV * 2.0 - 1.0;
            float seed = cell.x + cell.y * float(COLS) + 1.0;
            int kind = cell.y < 2.0 ? 0 : int(cell.y);
            // Lit from above and a little towards the viewer (who looks along +z from z = -1), the
            // side a flyer sees most.
            vec3 light_dir = normalize(vec3(0.6, 0.75, -0.35));
            float transmit = 1.0;
            float sunlit = 0.0;
            float ambient = 0.0;
            float dz = 2.0 / float(STEPS);
            float sigma = 14.0;
            for (int i = 0; i < STEPS; i++) {
                vec3 p = vec3(local.x, -local.y * 0.5 - 0.05, -1.0 + (float(i) + 0.5) * dz);
                float d = density(p, seed, kind);
                if (d <= 0.002) {
                    continue;
                }
                float od = 0.0;
                for (int j = 1; j <= LIGHT_STEPS; j++) {
                    od += density(p + light_dir * (float(j) * 0.05), seed, kind) * 0.05;
                }
                // Multiple scattering as three octaves (Wrenninge): each less absorbed and dimmer, so
                // the shadowed side stays luminous grey instead of turning black. The powder term
                // darkens the creases between billows, where light has scattered in from few sides.
                float scatter = exp(-od * sigma) + 0.25 * exp(-od * sigma * 0.3) + 0.08 * exp(-od * sigma * 0.08);
                float powder = 1.0 - exp(-d * 6.0);
                // The view ray sees a thinner medium than the light ray, so rims stay translucent
                // while the core still shadows itself.
                float absorb = d * dz * sigma * 0.45;
                // Energy-conserving step (Hillaire): the light a step scatters is what it removes
                // from the ray, so thick samples cannot overshoot and flatten to white.
                float taken = 1.0 - exp(-absorb);
                sunlit += transmit * taken * scatter * mix(0.3, 1.0, powder) * 0.7;
                ambient += transmit * taken * (0.08 + 0.92 * clamp(p.y * 1.5 + 0.6, 0.0, 1.0));
                transmit *= exp(-absorb);
                if (transmit < 0.01) {
                    break;
                }
            }
            float alpha = 1.0 - transmit;
            float norm = max(alpha, 0.0001);
            COLOR = vec4(clamp(sunlit / norm, 0.0, 1.0), clamp(ambient / norm, 0.0, 1.0), 0.0, alpha);
        }
        """;

    // The cards: spherical facades like the fvol sprites, fogged with the world's own fog, relit per
    // zone off the SUNLIGHT pair the zone apply publishes, faded out as the camera flies into one.
    private const string CardShader = """
        shader_type spatial;
        render_mode blend_mix, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

        uniform sampler2D atlas : filter_linear_mipmap, repeat_disable;

        #include "res://shaders/csky_atmosphere.gdshaderinc"
        #include "res://shaders/csky_facade.gdshaderinc"

        const float VARIANTS_X = 4.0;
        const float VARIANTS_Y = 4.0;

        varying flat float v_variant;
        varying float v_near;

        global uniform float csky_night = 0.0;

        float hg(float cos_t, float g) {
            float g2 = g * g;
            return (1.0 - g2) / (12.566 * pow(1.0 + g2 - 2.0 * g * cos_t, 1.5));
        }

        void vertex() {
            vec3 origin = MODEL_MATRIX[3].xyz;
            float width = length(MODEL_MATRIX[0].xyz);
            mat3 face = csky_facade_spherical(origin, CAMERA_POSITION_WORLD);
            MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
                vec4(face[0], 0.0), vec4(face[1], 0.0), vec4(face[2], 0.0), MODEL_MATRIX[3]);
            MODELVIEW_MATRIX[0] *= width;
            MODELVIEW_MATRIX[1] *= length(MODEL_MATRIX[1].xyz);
            v_variant = floor(INSTANCE_CUSTOM.r * VARIANTS_X * VARIANTS_Y);
            // Dissolve as the eye nears the card, so flying into a cloud never clips its quad.
            float dist = distance(CAMERA_POSITION_WORLD, origin);
            v_near = smoothstep(width * 0.25, width * 0.8, dist);
        }

        void fragment() {
            vec2 cell = vec2(mod(v_variant, VARIANTS_X), floor(v_variant / VARIANTS_X));
            vec2 uv = (cell + UV) / vec2(VARIANTS_X, VARIANTS_Y);
            vec4 c = texture(atlas, uv);
            vec3 fog_world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
            // The zone's SUNLIGHT: a cool sky ambient that darkens towards the base, and a warm direct
            // term on the side the baked light reached, weakened as the sun sinks. The direct term
            // dominates, so a cloud reads as a lit volume rather than a grey cutout.
            float sun_up = clamp(csky_sun_dir.y * 0.5 + 0.5, 0.2, 1.0);
            // A night zone (WeatherRig.IsNightZone) keeps its SUNLIGHT energies for the ground, but
            // its clouds are moonlit: dim, cool, little direct light.
            float day = 1.0 - csky_night;
            vec3 sky = mix(vec3(0.3, 0.34, 0.45), vec3(0.62, 0.68, 0.8), day) * csky_sun_light.x * (0.2 + 0.8 * c.g);
            vec3 sun = mix(vec3(0.5, 0.55, 0.65) * 0.3, vec3(1.0, 0.96, 0.88), day)
                * csky_sun_light.y * pow(c.r, 1.3) * sun_up * 1.5;
            // Dual-lobe Henyey-Greenstein (Hillaire's cloud phase) at runtime, since the atlas was lit
            // from one fixed direction: towards the sun the thin rims glow (the silver lining) and the
            // cloud brightens overall, away from it the back lobe keeps it from going dull.
            vec3 view = normalize(fog_world - CAMERA_POSITION_WORLD);
            float cos_t = dot(view, normalize(csky_sun_dir));
            float phase = mix(hg(cos_t, 0.6), hg(cos_t, -0.25), 0.4) * 12.566;
            float rim = c.a * (1.0 - c.a) * 4.0;
            sun *= mix(0.8, phase, 0.35);
            sun += vec3(1.0, 0.95, 0.85) * csky_sun_light.y * sun_up * day * rim * max(phase - 1.0, 0.0) * 0.35;
            vec3 col = clamp(sky + sun, 0.0, 1.8);
            // TUNE: the zone fog is sized for the ground; at full strength it erases every cloud past
            // its far edge, so a cloud takes part of it and stays a hazy shape on the horizon.
            float fog_amt = csky_fog_amount(fog_world, CAMERA_POSITION_WORLD) * 0.8;
            ALBEDO = mix(col, csky_fog_color, fog_amt);
            ALPHA = c.a * v_near;
        }
        """;

    private readonly List<Card> _cards;
    private readonly MultiMesh _multiMesh;
    private readonly ShaderMaterial _material;
    private SubViewport? _bake;
    private ShaderMaterial? _bakeMaterial;
    private Image? _atlas;
    private int _bakeFrames;
    private int _bakeTile;
    private Vector3 _sortedFrom = new(float.MaxValue, 0f, 0f);

    private VolumetricClouds(List<Card> cards, bool overcast)
    {
        Name = "volumetric_clouds";
        _cards = cards;
        _material = new ShaderMaterial { Shader = new Shader { Code = CardShader } };
        _multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One, Material = _material },
            InstanceCount = cards.Count,
        };
        Log.Info("world", $"volumetric clouds: {cards.Count} baked cards, {(overcast ? "overcast" : "scattered")}");
    }

    /// <summary>Where the field sits and how closed it is, off what the chapter authors: a chapter
    /// with a deck puts it at the <c>CLOUD_COVER</c> floor, overcast; one without puts it at its lowest
    /// placed <c>cloudparent</c> cluster, or <see cref="DefaultFloor"/>, scattered.
    /// ⚠ Never take the band floor for a chapter with no deck: C1B, C2, C3 and C5 author it at 10 to
    /// 20 km, where the original draws nothing.</summary>
    public static (float Floor, bool Overcast) LayerFor(bool hasDeck, (float Bottom, float Top)? band,
        IEnumerable<Node3D> cloudClusters, GameZ gamez)
    {
        if (hasDeck && band is { } b)
        {
            return (b.Bottom, true);
        }

        float? lowest = null;
        foreach (var cluster in cloudClusters)
        {
            if (ClusterBox(cluster, gamez) is { } box)
            {
                lowest = lowest is { } l ? Mathf.Min(l, box.Position.Y) : box.Position.Y;
            }
        }

        return (lowest ?? DefaultFloor, false);
    }

    /// <summary>The world-space bounds of one placed <c>cloudparent</c> cluster, off the gamez
    /// vertices through each node's whole transform chain, never the built nodes' GlobalTransform: the
    /// world is not in the scene tree yet when the session builds this. Null for a cluster with no
    /// mesh under it.</summary>
    public static Aabb? ClusterBox(Node3D cluster, GameZ gamez)
    {
        if (!cluster.HasMeta(AnimRuntime.IndexMeta))
        {
            return null;
        }

        Aabb? box = null;
        var stack = new Stack<int>();
        stack.Push((int)cluster.GetMeta(AnimRuntime.IndexMeta));
        while (stack.Count > 0)
        {
            var node = gamez.Nodes[stack.Pop()];
            if (node.MeshIndex >= 0 && node.MeshIndex < gamez.Meshes.Count)
            {
                var xf = gamez.WorldTransformOf(node);
                foreach (var v in gamez.Meshes[node.MeshIndex].Vertices)
                {
                    var w = xf * v;
                    box = box is { } b ? b.Expand(w) : new Aabb(w, Vector3.Zero);
                }
            }

            foreach (int child in node.Children)
            {
                if (child >= 0 && child < gamez.Nodes.Count)
                {
                    stack.Push(child);
                }
            }
        }

        return box;
    }

    /// <summary>The field over the chapter's <c>World</c> area at <paramref name="floor"/>, seeded off
    /// <paramref name="chapter"/> so a chapter's clouds stand in the same places every flight. Null when
    /// the gamez carries no area to lay it over. <paramref name="authored"/> are the regions the
    /// original fills with cloud (its fvol boxes and cloudparent clusters): most clouds go there, at
    /// those regions' own heights and in proportion to their size, and only a sparse scatter covers
    /// the rest of the map.</summary>
    public static VolumetricClouds? Create(GameZ gamez, float floor, bool overcast, string chapter,
        IReadOnlyList<Aabb> authored)
    {
        GameZNode? world = null;
        foreach (var n in gamez.Nodes)
        {
            if (n.Kind == "World" && n.HasArea)
            {
                world = n;
                break;
            }
        }

        if (world == null)
        {
            return null;
        }

        float x0 = Mathf.Min(world.AreaLeft, world.AreaRight), x1 = Mathf.Max(world.AreaLeft, world.AreaRight);
        float z0 = Mathf.Min(world.AreaTop, world.AreaBottom), z1 = Mathf.Max(world.AreaTop, world.AreaBottom);
        float overhang = overcast ? OvercastOverhang : ScatteredOverhang;
        float padX = (x1 - x0) * overhang, padZ = (z1 - z0) * overhang;
        var rng = new Random(StableSeed(chapter));
        float minW = overcast ? OvercastMinWidth : ScatteredMinWidth;
        float maxW = overcast ? OvercastMaxWidth : ScatteredMaxWidth;
        var cards = new List<Card>(OvercastCount * 6);
        float Rand() => (float)rng.NextDouble();
        // An overcast deck closes the whole sky, so it keeps its full field and the authored clouds
        // come on top.
        // A scattered sky follows the original's own cloud count: a chapter that authors none keeps a
        // sparse handful, one that places clusters gets a few clouds at each and little elsewhere.
        int backgroundCount = overcast ? OvercastCount : authored.Count == 0 ? SparseCount : SparseCount / 2;
        for (int i = 0; i < backgroundCount; i++)
        {
            float width = Mathf.Lerp(minW, maxW, Rand());
            var at = new Vector3(
                Mathf.Lerp(x0 - padX, x1 + padX, Rand()),
                floor + (Rand() * LayerSpread),
                Mathf.Lerp(z0 - padZ, z1 + padZ, Rand()));
            AddCloud(cards, rng, at, width, overcast, Rand());
        }

        Log.Info("world", $"volumetric clouds: {authored.Count} authored cloud region(s)");
        foreach (var box in MergeNearby(authored))
        {
            // A cluster of sprites is one or two clouds; a wide fvol box one per AuthoredAreaPerCloud.
            int n = Math.Max(1 + rng.Next(2), (int)(AuthoredArea(box) / AuthoredAreaPerCloud));
            float top = Mathf.Min(box.End.Y, box.Position.Y + AuthoredMaxDepth);
            for (int k = 0; k < n; k++)
            {
                // A merged group of clusters sizes its cloud off its own extent, a wide box off the
                // field's range.
                float extent = Mathf.Max(box.Size.X, box.Size.Z);
                float width = extent < MergeCell * 2f
                    ? Mathf.Clamp(extent * Mathf.Lerp(0.9f, 1.4f, Rand()), minW * 0.6f, maxW)
                    : Mathf.Lerp(minW, maxW, Rand());
                // Spread a little past the region's own edge, so a small cluster reads as a group of
                // clouds rather than one pinned to a point.
                float grow = Mathf.Max(width * 0.6f, 0f);
                var at = new Vector3(
                    Mathf.Lerp(box.Position.X - grow, box.End.X + grow, Rand()),
                    Mathf.Lerp(box.Position.Y, Mathf.Max(top, box.Position.Y), Rand() * 0.6f),
                    Mathf.Lerp(box.Position.Z - grow, box.End.Z + grow, Rand()));
                AddCloud(cards, rng, at, width, overcast, Rand());
            }
        }

        return new VolumetricClouds(cards, overcast);
    }

    public override void _Ready()
    {
        AddChild(new MultiMeshInstance3D
        {
            Multimesh = _multiMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // The field spans the whole chapter; its own AABB would cull it from inside.
            ExtraCullMargin = 16384f,
        });
        StartBake();
    }

    public override void _Process(double delta)
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null)
        {
            return;
        }

        var eye = camera.GlobalPosition;
        if (eye.DistanceSquaredTo(_sortedFrom) < ResortDistance * ResortDistance)
        {
            return;
        }

        _sortedFrom = eye;
        _cards.Sort((a, b) => b.Position.DistanceSquaredTo(eye).CompareTo(a.Position.DistanceSquaredTo(eye)));
        for (int i = 0; i < _cards.Count; i++)
        {
            var c = _cards[i];
            var basis = Basis.FromScale(new Vector3(c.Width, c.Height, 1f));
            _multiMesh.SetInstanceTransform(i, new Transform3D(basis, c.Position));
            _multiMesh.SetInstanceCustomData(i, new Color((c.Variant + 0.5f) / (AtlasCols * AtlasRows), 0f, 0f, 0f));
        }
    }

    public override void _ExitTree()
    {
        if (_bake != null)
        {
            RenderingServer.FramePostDraw -= FinishBake;
            _bake = null;
        }
    }

    // One cloud of a kind picked by weight: overcast leans to sheets and ridges, fair weather to single
    // cumulus and towers.
    private static void AddCloud(List<Card> cards, Random rng, Vector3 at, float width, bool overcast, float pick)
    {
        if (overcast ? pick < 0.5f : pick < 0.15f)
        {
            AddSheet(cards, rng, at, width * 1.4f);
        }
        else if (pick < (overcast ? 0.7f : 0.4f))
        {
            AddRidge(cards, rng, at, width);
        }
        else if (!overcast && pick < 0.65f)
        {
            AddTower(cards, rng, at, width);
        }
        else
        {
            AddCumulus(cards, rng, at, width);
        }
    }

    // The original places its sprite clusters close together, often dozens a chapter; one cloud per
    // cluster turns a sky of a few clouds into a solid mass. Regions whose centres share a
    // MergeCell square become one region, so a group of clusters stands for one or two clouds.
    private static List<Aabb> MergeNearby(IReadOnlyList<Aabb> regions)
    {
        var cells = new Dictionary<(int, int), Aabb>();
        var merged = new List<Aabb>();
        foreach (var box in regions)
        {
            if (AuthoredArea(box) >= AuthoredAreaPerCloud)
            {
                merged.Add(box);
                continue;
            }

            var c = box.GetCenter();
            var key = ((int)Mathf.Floor(c.X / MergeCell), (int)Mathf.Floor(c.Z / MergeCell));
            cells[key] = cells.TryGetValue(key, out var held) ? held.Merge(box) : box;
        }

        merged.AddRange(cells.Values);
        return merged;
    }

    // A region's horizontal area, what its cloud count scales with.
    private static float AuthoredArea(Aabb box) => box.Size.X * box.Size.Z;

    // The cloud generator: each kind of cloud is a small arrangement of atlas cards, the way a flight
    // simulator's sprite clouds are built, so the sky mixes single cumulus, towers, ridges and sheets
    // instead of repeating one shape. A card's position is its centre; a cumulus tile's base sits
    // 0.37 of the card's height below it.
    private static Card BaseCard(Random rng, Vector3 baseAt, float width) =>
        new(baseAt + new Vector3(0f, width * 0.5f * 0.37f, 0f), width, width * 0.5f, rng.Next(2 * AtlasCols));

    private static void AddCumulus(List<Card> cards, Random rng, Vector3 at, float width)
    {
        cards.Add(BaseCard(rng, at, width));
        if (rng.NextDouble() < 0.5)
        {
            float side = (float)((rng.NextDouble() * 2) - 1) * width * 0.4f;
            cards.Add(BaseCard(rng, at + new Vector3(side, 0f, width * 0.2f), width * 0.7f));
        }
    }

    // Cumulus congestus: a wide base with crowns stacked on it, each narrower and nudged sideways, so
    // it climbs two or three times as high as it is wide at the base.
    private static void AddTower(List<Card> cards, Random rng, Vector3 at, float width)
    {
        width *= 1.2f;
        cards.Add(BaseCard(rng, at, width));
        int crowns = 2 + rng.Next(3);
        float w = width * 0.85f;
        var c = at + new Vector3(0f, width * 0.32f, 0f);
        for (int k = 0; k < crowns; k++)
        {
            c += new Vector3((float)((rng.NextDouble() * 2) - 1) * w * 0.12f, w * 0.26f, (float)((rng.NextDouble() * 2) - 1) * w * 0.08f);
            cards.Add(new Card(c, w, w * 0.5f, (2 * AtlasCols) + rng.Next(AtlasCols)));
            w *= 0.8f;
        }
    }

    // A cloud street: cumulus in a loose line along one heading, as wind lines them up.
    private static void AddRidge(List<Card> cards, Random rng, Vector3 at, float width)
    {
        float heading = (float)(rng.NextDouble() * Mathf.Tau);
        var step = new Vector3(Mathf.Cos(heading), 0f, Mathf.Sin(heading));
        int n = 3 + rng.Next(4);
        for (int k = 0; k < n; k++)
        {
            float w = width * Mathf.Lerp(0.55f, 0.95f, (float)rng.NextDouble());
            var off = (step * ((k - (n * 0.5f)) * width * 0.55f)) + new Vector3(0f, (float)rng.NextDouble() * width * 0.05f, (float)((rng.NextDouble() * 2) - 1) * width * 0.15f);
            cards.Add(BaseCard(rng, at + off, w));
        }
    }

    // A stratocumulus patch: flat sheet cards spread over an area, low and close together.
    private static void AddSheet(List<Card> cards, Random rng, Vector3 at, float width)
    {
        int n = 4 + rng.Next(5);
        for (int k = 0; k < n; k++)
        {
            float w = width * Mathf.Lerp(0.6f, 1f, (float)rng.NextDouble());
            var off = new Vector3((float)((rng.NextDouble() * 2) - 1) * width, (float)rng.NextDouble() * width * 0.04f, (float)((rng.NextDouble() * 2) - 1) * width);
            cards.Add(new Card(at + off + new Vector3(0f, w * 0.5f * 0.37f, 0f), w, w * 0.5f, (3 * AtlasCols) + rng.Next(AtlasCols)));
        }
    }

    // The same chapter always seeds the same field: string.GetHashCode is randomized per process.
    private static int StableSeed(string chapter)
    {
        int h = 17;
        foreach (char ch in chapter)
        {
            h = unchecked((h * 31) + ch);
        }

        return h;
    }

    // Whether any texel of the atlas is opaque enough to be a cloud: a coarse grid is plenty, every
    // tile holds a cloud across its middle.
    private static bool HasInk(Image image)
    {
        int w = image.GetWidth(), h = image.GetHeight();
        for (int y = h / 8; y < h; y += h / 8)
        {
            for (int x = w / 16; x < w; x += w / 16)
            {
                if (image.GetPixel(x, y).A > 0.05f)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Renders the atlas in an offscreen viewport one tile per frame, since the whole atlas in one draw
    // outruns Metal's GPU timeout ("timeout waiting for fence"); FinishBake copies each tile into the
    // atlas image, then uploads it as a mipmapped texture the cards sample and frees the viewport.
    private void StartBake()
    {
        _atlas = Image.CreateEmpty(AtlasCols * TilePixels, AtlasRows * TilePixels, false, Image.Format.Rgba8);
        _bakeMaterial = new ShaderMaterial { Shader = new Shader { Code = BakeShader } };
        _bakeMaterial.SetShaderParameter("tile", 0);
        _bake = new SubViewport
        {
            Size = new Vector2I(TilePixels, TilePixels),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
            Disable3D = true,
        };
        _bake.AddChild(new ColorRect
        {
            Size = new Vector2(TilePixels, TilePixels),
            Material = _bakeMaterial,
        });
        AddChild(_bake);
        RenderingServer.FramePostDraw += FinishBake;
    }

    // ⚠ Never take a tile's first post-draw: a session builds inside a long frame, and the first
    // callback can land before the viewport has drawn, leaving a transparent atlas and no clouds at
    // all. A tile is taken only once it holds opaque texels, retried for a few frames.
    private void FinishBake()
    {
        if (_bake == null || !IsInstanceValid(_bake) || _atlas == null || _bakeMaterial == null)
        {
            RenderingServer.FramePostDraw -= FinishBake;
            _bake = null;
            return;
        }

        if (++_bakeFrames < 2)
        {
            return;
        }

        var image = _bake.GetTexture().GetImage();
        if (image == null || image.IsEmpty() || !HasInk(image))
        {
            if (_bakeFrames < BakeRetryFrames)
            {
                _bake.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
                return;
            }

            Log.Warn("world", $"volumetric clouds: atlas tile {_bakeTile} still empty after {_bakeFrames} frames, flying without clouds");
            RenderingServer.FramePostDraw -= FinishBake;
            _bake.QueueFree();
            _bake = null;
            return;
        }

        image.Convert(Image.Format.Rgba8);
        _atlas.BlitRect(image, new Rect2I(0, 0, image.GetWidth(), image.GetHeight()), new Vector2I(_bakeTile % AtlasCols * TilePixels, _bakeTile / AtlasCols * TilePixels));
        if (++_bakeTile < AtlasCols * AtlasRows)
        {
            _bakeFrames = 0;
            _bakeMaterial.SetShaderParameter("tile", _bakeTile);
            _bake.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            return;
        }

        RenderingServer.FramePostDraw -= FinishBake;
        _atlas.GenerateMipmaps();
        Log.Info("world", $"volumetric clouds: atlas baked in {AtlasCols * AtlasRows} tiles");
        _material.SetShaderParameter("atlas", ImageTexture.CreateFromImage(_atlas));
        _atlas = null;
        _bake.QueueFree();
        _bake = null;
    }

    // One card: where it stands, its size and which atlas variant it draws.
    private readonly record struct Card(Vector3 Position, float Width, float Height, int Variant);
}
