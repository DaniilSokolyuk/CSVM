using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CSVM.Utils;
using Godot;

namespace CSVM.Mech3;

/// <summary>Which UV axes of a surface stay inside [0,1], see
/// <see cref="SceneBuilder.UvAxesWithinUnitSquare"/>. A fitting axis never exercises the
/// sampler wrap, so clamping it is free and removes the wrap's edge-bleed hairline.</summary>
[Flags]
public enum UvClampAxes
{
    None = 0,
    U = 1,
    V = 2,
    Both = U | V,
}

/// <summary>
/// Turns GameZ node subtrees into renderable Godot node trees: recursion over the
/// node hierarchy, n-gon triangulation, and a shared material cache. Callers
/// (PlaneBuilder, WorldBuilder) supply what to skip; the LOD rule (keep only the
/// nearest level of each LOD group) is common and lives here.
/// Polygons carry a draw priority (GameZPolygon.Priority) the original engine uses to
/// layer coplanar geometry (terrain-transition patches, road/shadow decals, plane
/// logos over the fuselage, skydome behind everything); non-zero priorities become a
/// per-surface depth bias, a view-space pull toward the eye, so those layers
/// resolve without z-fighting at any viewing distance.
/// </summary>
public sealed class SceneBuilder
{
    /// <summary>Instance shader parameter behind <see cref="TintLine"/>: rgb is the colour, alpha
    /// the strength. Alpha 0 (the default) is untinted.</summary>
    public const string TintParam = "csky_tint";

    // Equal-priority tie-break applied per-instance (nodes.json DFS order = draw order):
    // the airfield's apron detail over its base tile, road decals over rail decals. Spending
    // the whole flat index range on that spans 1.22-2.86 priority levels per chapter, which is
    // why a WORLD build ranks by the conflict graph instead (see ConflictRankBias); this is what
    // a build with no such graph, an aircraft, the --node= viewer, still uses.
    public const float NodeOrderBias = 5e-8f;

    // The world's cross-node tie-break: one slot per conflicting layer, not per node
    // (ConflictRank). ⚠ Do not change this step in isolation. It must exceed SurfaceRankCap x
    // SurfaceRankBias (1e-5), or within-mesh rank out-bids it, and ConflictRankCap x it + 1e-5
    // must also stay under NoClutterLayerBias. Measured in Godot: 5e-6 leaves a coplanar pair
    // swapping winner on 1,774 px under a 1 mm camera move, 1.2e-5 leaves 0
    // (analysis/bl-053-dense-rank).
    public const float ConflictRankBias = 1.2e-5f;
    // Measured worst across all eight chapters is 7. The cap keeps the whole tie-break
    // (7 x 1.2e-5 + 5 x SurfaceRankBias = 9.4e-5) below NoClutterLayerBias, so a no_clutter
    // overlay + tie-break stays inside one priority level; a chapter that ever needs more is
    // logged, not silently collapsed.
    public const int ConflictRankCap = 7;

    /// <summary>How near the drawing camera must stand to the eye <see cref="PhotoEyeParam"/>
    /// carries, in metres, to draw the photograph's fill. It separates the photograph's camera
    /// from every pane's, which never stands two and a half chase distances ahead of the nose.</summary>
    public const float PhotoEyeReach = 0.5f;

    /// <summary>Meta key a collider carries when its surface class is water or buildings.
    /// StringName, not <c>const string</c>, as every meta key here (see <c>AnimRuntime.NameMeta</c>).</summary>
    public static readonly StringName SurfaceMeta = "csky_surface";

    /// <summary>Meta key EVERY collider carries: an <c>int</c>, the original's numeric surface
    /// type id (<see cref="GameZMaterial.SoilId"/>). Every polygon in the body shares it, since
    /// bodies are split by soil as well as class (<see cref="CollidersForMesh"/>). A different
    /// name space from <see cref="SurfaceMeta"/>'s texture-derived class, so it is never spelled
    /// as that string.</summary>
    public static readonly StringName SurfaceIdMeta = "csky_surface_id";

    /// <summary>Meta key a collider carries when its node authors <see cref="GameZNode.CanModify"/>:
    /// a <c>bool</c>, always true where present. The crater carve is refused on any collider without
    /// it, and no shipped node sets it (docs/org/craters.md). Read by <see cref="CanModify"/>.</summary>
    public static readonly StringName CanModifyMeta = "csky_can_modify";

    /// <summary>Meta key a mission-structure node carries when the mission being built is one it
    /// authors an owner for. The value is an <c>int</c>, that owner's team
    /// (<see cref="GameZNode.MissionStructureTeam"/>). A flagged node authoring no owner for this
    /// mission carries none, since an unowned object is nobody's target either way. Read by
    /// <see cref="DestructibleRegistry.Register"/>.</summary>
    public static readonly StringName MissionStructureTeamMeta = "csky_mstruct_team";

    /// <summary>Meta key every flagged mission-structure node that is a gasbag carries: a
    /// <c>bool</c>, always true where present. A turret's candidate pass drops these; the player's
    /// own selection keeps them.</summary>
    public static readonly StringName MissionStructureGasbagMeta = "csky_mstruct_gasbag";

    public static readonly StringName OpacityParam = "csky_opacity";

    /// <summary>Instance shader parameter the Danger Zone photograph arms on its own pilot's
    /// aircraft for the one frame it draws. Its xyz is the photograph's eye, and w is 1 while
    /// armed. A faithful aircraft surface then takes <c>csky_sun_fill_rgb</c> as its ambient half,
    /// but only for a camera within <see cref="PhotoEyeReach"/> of that eye.</summary>
    public static readonly StringName PhotoEyeParam = "csky_photo_eye";

    /// <summary>The albedo sampler every generated shader here declares, as a ready
    /// <see cref="StringName"/>. ⚠ Use this, never the bare string, wherever the write is on a
    /// per-frame path: the conversion mints a finalizable wrapper per call, and that count is what
    /// sets the collection pause (docs/verification.md PERF-20).</summary>
    public static readonly StringName AlbedoTexParam = "albedo_tex";

    /// <summary>A polygon carrying the <c>no_clutter</c> flag (raw bit <c>0x800</c>) in the
    /// <see cref="DebugClutterFlag"/> view: nothing is scattered on this ground.</summary>
    public static readonly Color FlaggedColor = new(1f, 0f, 0f);

    /// <summary>A polygon without it: clutter-eligible ground.</summary>
    public static readonly Color ClearColor = new(0f, 1f, 0f);

    /// <summary>What the clutter itself is painted in that view, the decorations and sprite cards
    /// scattered ONTO the ground, stamped per instance as a full-strength
    /// <see cref="TintParam"/>. Their own polygons are unflagged, so without this a city block
    /// would read as clutter-eligible ground.</summary>
    public static readonly Color ClutterColor = new(0.1f, 0.35f, 1f, 1f);

    /// <summary>Surfaces given a CLAMPed sampler because their UVs never leave the unit square
    /// (the hairline-seam fix, see <see cref="UvsWithinUnitSquare"/>). Process-wide across every
    /// builder, purely for the load log; it is also what tells a capture which build made it.</summary>
    public static int ClampedSurfaceTotal;

    /// <summary>Surfaces given the shader-side single-axis edge clamp instead, one UV axis
    /// fits the unit square while the other tiles or scrolls (see
    /// <see cref="UvAxesWithinUnitSquare"/>). Same load-log role as
    /// <see cref="ClampedSurfaceTotal"/>.</summary>
    public static int EdgeClampedSurfaceTotal;

    /// <summary>The <c>--debug-clutterflag</c> view (<c>docs/cli.md</c>): every world polygon this
    /// builder emits is painted by its decoded <c>no_clutter</c> flag instead of the lit, fogged
    /// texture. Set by the caller before building (like <see cref="Cycler"/>); the recolour happens
    /// at build time, so it cannot be toggled at runtime. Off leaves every emitted vertex colour
    /// and every generated shader exactly as they are.</summary>
    public bool DebugClutterFlag;

    /// <summary>Which transparency classes this builder leaves unbuilt (<c>--hide-alpha</c>,
    /// <c>docs/cli.md</c>). Set by the caller before building, like <see cref="DebugClutterFlag"/>,
    /// because a hidden class is dropped at the surface rather than switched off at runtime. Empty
    /// in every ordinary run, so nothing but an isolation capture sees it.</summary>
    public TransparencyClass HiddenAlpha;

    /// <summary>Where a material's own texture flipbook (the gamez `cycle` block) is delivered.
    /// Set by the caller before building; null leaves cycling materials static.</summary>
    public TextureCycler? Cycler;

    /// <summary>Which mission of the chapter is being built, 1-based
    /// (<see cref="GameZ.MissionSlotOf"/>). A mission-structure node authors one owner per mission,
    /// so this decides which of its slots the build reads. Set by the caller before building; the
    /// default reads the first mission's slot, which every shipped structure authors the same as
    /// all its others.</summary>
    public int MissionSlot = 1;

    // Shared shader source lives in res://shaders/*.gdshaderinc, pulled in by Godot's shader
    // preprocessor, which resolves #include in a Shader whose Code is assigned at runtime from C#.
    // ⚠ Keep these blocks in files, not C# const strings. Four independently generated shaders
    // share them, and a const string still lets each shader choose whether and where to emit the
    // instance-uniform block, which is exactly what its ordering contract forbids (see
    // csky_instance_uniforms.gdshaderinc).
    internal const string InstanceUniformsInclude =
        "#include \"res://shaders/csky_instance_uniforms.gdshaderinc\"";
    internal const string SrgbInclude =
        "#include \"res://shaders/csky_srgb.gdshaderinc\"";
    internal const string AtmosphereInclude =
        "#include \"res://shaders/csky_atmosphere.gdshaderinc\"";
    internal const string ClutterFadeInclude =
        "#include \"res://shaders/csky_clutter_fade.gdshaderinc\"";
    internal const string LightsInclude =
        "#include \"res://shaders/csky_lights.gdshaderinc\"";
    internal const string TimeInclude =
        "#include \"res://shaders/csky_time.gdshaderinc\"";
    internal const string MipBiasInclude =
        "#include \"res://shaders/csky_mip_bias.gdshaderinc\"";
    internal const string FacadeInclude =
        "#include \"res://shaders/csky_facade.gdshaderinc\"";

    /// <summary>The animation runtime's <c>OBJECT_OPACITY_STATE</c> translucency, a per-instance
    /// multiplier on ALPHA, because materials are cached and this changes at runtime. Declared in
    /// the shared ordered preamble, but emitted ONLY into variants that already write ALPHA: an
    /// opaque variant has no alpha path, and adding one would move it into the transparent pass.
    /// ⚠ <see cref="AnimRuntime"/> must test for this term, not the uniform name; the declaration
    /// is everywhere, so only the term marks a shader that reads opacity.</summary>
    internal const string OpacityTerm = " * csky_opacity";

    /// <summary>The debug overlays' per-instance tint (<c>UI.Overlays.ClassOverlay</c>), the LAST
    /// write fragment() makes to ALBEDO, so a tinted object reads as its class colour at any
    /// distance. <c>mix(x, t, 0.0)</c> is exactly <c>x</c>, so the line changes no pixel while the
    /// overlay is off. ⚠ Do not move this into a <c>MaterialOverride</c>/<c>MaterialOverlay</c>.
    /// That is a different shader, carrying neither the depth bias nor the billboard spin and
    /// defaulting to <c>cull_back</c> where this world is <c>cull_front</c>.</summary>
    internal const string TintLine = "    ALBEDO = mix(ALBEDO, csky_tint.rgb, csky_tint.a);";

    /// <summary><see cref="TintLine"/>'s twin under <see cref="DebugClutterFlag"/>: the same last
    /// write to ALBEDO, from the raw vertex colour <see cref="EmitTriangle"/> painted the flag into.
    /// Emitted ONLY into the fullbright world shader of a builder that asked for the debug view, so
    /// the default shader text stays byte-for-byte what it was.
    /// ⚠ Keep the tint mix rather than assigning <c>COLOR.rgb</c> outright; it is what lets a
    /// clutter MultiMesh still be forced blue per instance.</summary>
    internal const string ClutterFlagTintLine = "    ALBEDO = mix(COLOR.rgb, csky_tint.rgb, csky_tint.a);";

    /// <summary>Multiplies every depth bias this builder emits. Each is a fraction of VIEW
    /// DISTANCE, so a subtree mounted at a scale other than 1 has all of them compressed by that
    /// factor while the renderer's depth noise floor stays put; a caller mounting one passes the
    /// inverse of its mount scale, and a priority level is worth the same DEPTH there as anywhere
    /// else (<see cref="PlaneBuilder.InteriorScale"/> is why this exists). ⚠ Baked into cached
    /// meshes and materials: set it before building, never between builds.</summary>
    internal float DepthBiasScale = 1f;

    /// <summary>Blend this builder's soft-alpha surfaces in GAMMA space, the way the original's
    /// framebuffer mixed sRGB values, instead of the linear space Godot composites in: the same
    /// correction <c>csky_srgb.gdshaderinc</c> makes for the DX7 vertex MODULATE, applied to the
    /// other gamma-space operation. Without it a decal's faint surround lands near 31 % opacity
    /// where the original put 8 %, which reads as a solid block (docs/formats/hud.md).
    /// ⚠ Set before building, like <see cref="DepthBiasScale"/>: it keys the shader.</summary>
    internal bool GammaBlendAlpha;

    /// <summary>The world's conflict ranks (<see cref="ConflictRank"/>), set by the caller before
    /// building. Null, every aircraft, every <c>--node=</c> subtree, any build with no conflict
    /// graph, leaves the cross-node tie-break on the flat node index, which is what those builds
    /// have always used and keeps them byte-identical.</summary>
    internal IReadOnlyDictionary<int, int>? ConflictRanks;

    private const string LightShaderCode = @"
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled;
uniform float range_far = 0.0;    // data unk52; 0 = the data authors no fade
uniform float fade_slope = 0.0;   // data unk56 in 0..1 units
uniform float blink_period = 0.0; // data unk08 under unk04; 0 = steady
#include ""res://shaders/csky_instance_uniforms.gdshaderinc""
// ^ reads csky_light_fade (0 = skydome stars, no range fade) and csky_opacity
//   (OBJECT_OPACITY_STATE); node_bias and csky_fog_on come along unused, which is the
//   price of one shared ordered block and costs nothing per instance.
#include ""res://shaders/csky_time.gdshaderinc""
void vertex() {
    float dist = max(length((MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz), 1.0);
    // A fixed world diameter projected to pixels, clamped: far lights stay visible
    // star-sized dots, near lights stop growing. TUNE: 6 m glow, 3..30 px.
    float px = 6.0 * PROJECTION_MATRIX[1][1] * VIEWPORT_SIZE.y / (2.0 * dist);
    POINT_SIZE = clamp(px, 3.0, 30.0);
    // The original's law where the data authors a fade, and a renderer-only horizon fade where
    // it does not: an unfaded light is one screen pixel there, which the fog and the 16-bit
    // frame buffer swallow, but is a soft sprite with a floor here and would litter the horizon.
    float fade = (range_far > 0.0)
        ? clamp((range_far - dist) * fade_slope, 0.0, 1.0)
        : 1.0 - smoothstep(2400.0, 4000.0, dist);
    // Lit for the first half of each 2x period, dark for the second, off one clock shared by
    // every light, which is what the original's per-light timers do, since they all start
    // together at load and none of them is ever reseeded.
    float lit = (blink_period > 0.0)
        ? 1.0 - step(0.5, fract(csky_time / (2.0 * blink_period)))
        : 1.0;
    COLOR.a = mix(1.0, fade, csky_light_fade) * lit;
}
void fragment() {
    float r = length(POINT_COORD - vec2(0.5)) * 2.0;
    // Soft star-like glow: hot gaussian core, no hard edge.
    float glow = exp(-r * r * 5.0) * smoothstep(1.0, 0.6, r);
    ALBEDO = COLOR.rgb * 1.6; // brightness gain, TUNE
    ALPHA = glow * COLOR.a * csky_opacity;
}";

    // The source UVs are exact 0.0/1.0 at the fold, so this only absorbs float noise.
    private const float UvEpsilon = 1e-6f;

    // Depth-bias fraction per priority level. Polygons are pulled toward the eye by
    // priority × this fraction of their view distance, same projected position, nearer
    // depth, replicating the original's coplanar-decal layering (terrain patches,
    // road/shadow decals, plane logos) without z-fighting at any range.
    // TUNE: big enough to beat coplanar interpolation noise at 10 km, small enough that
    // the ±49 extremes (cockpit gauges, skydome) stay well under 1% of view distance.
    private const float DepthBiasPerLevel = 2e-4f;
    // The within-mesh equal-priority tie-break, reproducing the original's draw order (drawn later
    // = on top): surface rank is the first-occurrence order of the (material, priority) group in
    // the polygon list, so a tile's roads and shoreline blends land over its base grass.
    // ⚠ Keep this and its cap strictly subordinate to one priority level and to the cross-node
    // step, or within-mesh order out-bids a real authored layer.
    private const float SurfaceRankBias = 2e-6f;
    private const int SurfaceRankCap = 5;
    // The no_clutter draw-order offset (GameZPolygon.NoClutter): where two coplanar layers are
    // painted over each other, the flagged one is measured to be the layer that draws on top. It is
    // a second layering axis, not a priority value, decode and evidence in docs/formats/gamez.md.
    // ⚠ Keep this at HALF a level, not the original's whole one. Priority 1 is a genuinely authored
    // value, so a full level would tie a flagged face with a real priority-1 overlay, and the two
    // were measured to resolve every C5 overlap identically.
    private const float NoClutterLayerBias = DepthBiasPerLevel * 0.5f;
    // Overlay passes (GameZPolygon.OverlayPasses) draw on the SAME triangles as their base pass
    // and must land in front of it. ⚠ Do not order them by surface rank instead: 97 of the 307
    // overlay-bearing models are already at SurfaceRankCap, where an appended overlay group shares
    // its base's capped rank and z-fights it. A tenth of a level out-ranks the whole rank budget
    // and still sits under NoClutterLayerBias, so a stacked overlay stays below a flagged layer.
    private const float OverlayPassBias = DepthBiasPerLevel * 0.1f;
    // The ceiling on the whole bias AFTER DepthBiasScale has multiplied it. ⚠ Clamp there and not
    // before the scale: 25x (the cockpit interior's) applied to the +-0.05 priority-level clamp
    // reaches 1.25, and a bias of 1 puts the surface exactly on the eye. A quarter of the view
    // distance leaves the authored +-49 range intact at that scale and cannot approach the near
    // plane. No unscaled surface comes near it (49 levels is 0.0098), so scale 1 is untouched.
    private const float MaxScaledBias = 0.25f;
    // Enhanced mode only: how far above 1.0 a surface the original draws as a light source (a glow
    // flare, a street lamp, a flame) writes its colour, so the glow pass has something to bloom.
    // TUNE, judged at the controls: above 1.0 is what triggers the bloom at all, and the value
    // decides how far it spreads. ⚠ Only the light-source arms take it; the general `lighting:
    // false` population is not emissive (docs/org/vertexLighting.md).
    private const float EmissiveScale = 1.5f;
    // Enhanced mode only: what a surface <see cref="ClassifySurface"/> calls water gets instead of
    // the matte world values, so screen-space reflection has a glossy surface to march against.
    // TUNE, judged at the controls: roughness sets how far a reflection smears, specular how much
    // of it survives at a glancing angle. Roughness is also what quiets a per-texel reflection
    // flicker, because it blurs a lost ray across its neighbours instead of dimming the water.
    private const float WaterRoughness = 0.25f;
    private const float WaterSpecular = 0.5f;
    // What an aircraft surface reflects under the scene lights, which only enhanced mode's aircraft
    // and a shaded builder without the per-vertex sun term draw under. The original has no
    // specular term at all (docs/org/vertexLighting.md), so any highlight here is ours. A TUNE
    // picked by eye against the original's screenshots, at the controls and not by a luminance
    // distance. Sunlight reads as a sheen, not as gloss.
    private const float AircraftSpecular = 0.25f;
    // The per-vertex sun term's colour triples (WeatherRig.SunVertexLight).
    // ⚠ Declare them here, never in csky_atmosphere.gdshaderinc, which every world shader
    // includes. Declared in that include, a headless probe never exits; the cause is not decoded
    // (docs/verification.md SHELL-21).
    private const string SunVertexLightDecl =
        "global uniform vec3 csky_sun_ambient_rgb;\nglobal uniform vec3 csky_sun_diffuse_rgb;\n"
        + "global uniform vec3 csky_sun_fill_rgb;";
    // ⚠ Format every scale invariantly; a comma decimal separator emits shader text that will not
    // compile. Godot discards EMISSION on an `unshaded` material and the glow pass reads the HDR
    // colour buffer, so these arms reach it by scaling the colour rather than by writing EMISSION.
    private static readonly string EmissiveLiteral =
        EmissiveScale.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly string WaterRoughnessLiteral =
        WaterRoughness.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly string WaterSpecularLiteral =
        WaterSpecular.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly string AircraftSpecularLiteral =
        AircraftSpecular.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);

    // ⚠ Process-wide, not per builder: Godot compiles a Shader the first time a material takes it,
    // and each generated text is a pure function of its key, so a per-builder memo made every later
    // builder recompile a shader byte-identical to one already live, on the frame path where an AI
    // spawn builds an aircraft (docs/verification.md PERF-22). Anything an instance field varies the
    // text by belongs in the key (`DebugClutterFlag`), or two builders that disagree about it share
    // one shader. Main thread only, like every other builder path.
    private static readonly Dictionary<int, Shader> BiasShaders = new(); // keyed by feature bits
    private static readonly Dictionary<int, Shader> BillboardShaders = new(); // cloud sprites, keyed by blend/scissor bits
    private static readonly Dictionary<int, Shader> CylindricalShaders = new(); // Y/X-axis facades, keyed by axis/blend/scissor/glow bits

    private readonly GameZ _gamez;
    private readonly TextureArchive _textures;
    private readonly bool _fullbright;
    // Original mode, shaded builders only: draw the original's per-vertex sun term instead of the
    // scene lights, the in-flight aircraft's light (docs/org/vertexLighting.md).
    private readonly bool _sunVertexLit;
    private readonly bool _generateCollision;
    private readonly bool _cullBackfaces;
    private readonly Func<string, bool>? _blendTexture;
    private readonly Func<string, bool>? _billboardTexture;
    private readonly Func<string, bool>? _glowTexture;
    private readonly Func<string, ImageTexture?, ImageTexture?>? _textureSubstitute;
    private readonly IReadOnlyDictionary<int, Vector2>? _scrollOverrides;
    // ⚠ Never drop a component of this key. Each one selects a different depth bias, sampler wrap
    // mode or shader variant, and one source material legitimately skins surfaces that disagree
    // about every one of them: scroll rate, ClampUv, NoClutter, the model's own Lit/Fogged render
    // flags, and Pass. Dropping one hands a cached material back at the wrong setting.
    // Non-scrolling surfaces all key on (0,0), so the common path's cache behaviour is unchanged.
    private readonly Dictionary<(int Material, int Priority, int Rank, bool NoClutter, bool DoubleSided, float ScrollU, float ScrollV, bool ClampUv, UvClampAxes EdgeClamp, bool Lit, bool Fogged, int Pass, bool ClutterFade), Material> _materialCache = new();
    // Keyed by (model index, force-double-sided, force-lit, clutter-fade): every override is baked
    // into the built surfaces (sidedness into the geometry groups, `lit` and the fade into which
    // material/shader a surface gets), so a forced build must not be handed back for the same
    // model referenced normally. No model in this install is referenced both ways (the deck's 144
    // tiles have exclusive model indices), so this is defence. `ForceLit` is the deck-underside
    // exception (`WorldBuilder.Add`); `ClutterFade` is ClutterBuilder's 3D-decoration build.
    private readonly Dictionary<(int Model, bool Force, bool ForceLit, bool ClutterFade), ArrayMesh?> _meshCache = new();
    private readonly Dictionary<int, Vector3> _meshPivotCache = new(); // billboard meshes only: local quad center
    private readonly Dictionary<int, ArrayMesh> _lightMeshCache = new();
    private readonly Dictionary<int, List<(string Name, string? Surface, int SurfaceId, List<ConcavePolygonShape3D> Shapes)>> _colliderCache = new();
    private readonly Dictionary<(float Far, float Slope, float Blink), ShaderMaterial> _lightMaterialCache = new();
    // Billboard glow material for a flare sprite quad: always alpha-blended (the soft ramp
    // must never scissor into a hard star cutout), no night dimming (it's a light source).
    private readonly Dictionary<(int Material, bool Fogged, bool ClampUv), Material> _glowMaterialCache = new();
    // Cylindrical (Y- or X-axis) billboard material. Unlike glow flares it respects the texture's
    // own alpha classification (trees and cables are hard cutouts, fire and flame are soft) and it
    // dims with the world SUNLIGHT unless the texture is itself a light source, the caller's
    // glowTexture predicate, the same delegate the spherical path uses, so one rule governs every
    // light-vs-scenery billboard in the renderer.
    private readonly Dictionary<(int Material, int Axis, bool Lit, bool Fogged, bool ClampUv), Material> _cylindricalMaterialCache = new();
    // Every textured material this builder made, paired with the texture name it resolved
    // from, the registry a live repaint needs (the viewer's livery lab re-runs the paint
    // and swaps each material's albedo in place, instead of rebuilding the whole aircraft
    // for every slider pixel). Only shader materials carrying an `albedo_tex` are listed.
    private readonly List<(ShaderMaterial Material, string TextureName)> _texturedMaterials = new();
    // A built world material against the verdict its shader was generated for. The verdict is a
    // shader variant rather than a parameter, so by the time a surface is committed it is reachable
    // nowhere else. The material cache also hands one object back for every surface sharing it.
    private readonly Dictionary<Material, TransparencyClass> _materialAlpha = new();
    private Shader? _lightShader;

    /// <param name="fullbright">Render unshaded, like the original's world pass: texture × baked
    /// vertex colour, ignoring scene lights.</param>
    /// <param name="blendTexture">True to alpha-BLEND a texture instead of the default 1-bit
    /// alpha-SCISSOR cutout. Only consulted for textures that carry alpha.</param>
    /// <param name="billboardTexture">True to render a texture's meshes as camera-facing sprites,
    /// recentred on their own quad centre. ⚠ Never the cloudlayer deck, which must stay flat.</param>
    public SceneBuilder(GameZ gamez, TextureArchive textures, bool fullbright = false,
        bool generateCollision = false, Func<string, bool>? blendTexture = null,
        Func<string, bool>? billboardTexture = null, bool cullBackfaces = false,
        Func<string, bool>? glowTexture = null,
        Func<string, ImageTexture?, ImageTexture?>? textureSubstitute = null,
        IReadOnlyDictionary<int, Vector2>? scrollOverrides = null, bool sunVertexLit = false)
    {
        _textureSubstitute = textureSubstitute;
        _scrollOverrides = scrollOverrides;
        _gamez = gamez;
        _textures = textures;
        _fullbright = fullbright;
        _sunVertexLit = sunVertexLit && !fullbright;
        _generateCollision = generateCollision;
        _blendTexture = blendTexture;
        _billboardTexture = billboardTexture;
        _cullBackfaces = cullBackfaces;
        _glowTexture = glowTexture;
    }

    /// <summary>How the original engine spins one model toward the camera: not at all, fully
    /// (a light-source glow), or about one fixed axis (an upright tree card, a flame on a
    /// horizontal pipe). This is the gamez model's OWN classification, not a guess of ours.</summary>
    public enum BillboardKind { None, Spherical, CylindricalY, CylindricalX }

    /// <summary>What a drawn thing does with its texture's alpha, split by what draws it. A world
    /// surface and a clutter card at the same verdict still take different shaders and different
    /// depth modes. An isolation capture therefore has to name the two separately.</summary>
    [Flags]
    public enum TransparencyClass
    {
        None = 0,
        BlendSurface = 1,
        ScissorSurface = 2,
        BlendCard = 4,
        ScissorCard = 8,
    }

    // The rendering-side spelling of ClassifyBillboard's two cylindrical cases: the mesh
    // spins about one fixed world axis to face the camera on the other two, so a tree card or
    // a flame stays upright instead of tipping over like a light glow. Kept as its own enum
    // because it also keys the shader/material caches below. None on a legacy extraction: there
    // is no per-axis fallback, so those meshes render static.
    private enum CylAxis { None, Y, X }

    public int MeshInstanceCount { get; private set; }
    public int ColliderCount { get; private set; }

    /// <summary>Mesh instances this builder moved onto a <see cref="ZoneGate"/> layer, indexed by
    /// gamez <c>zone_id</c> (slots 1…<see cref="ZoneGate.MaxZoneId"/>; slot 0 is always 0,
    /// <c>zone_id</c> 0 and −1 are ungated). Zero everywhere for a build that passed
    /// <c>zoneGate: false</c>, which is the aircraft, the deck and the dome.
    /// ⚠ Keep this census. A gate that silently stopped stamping and a chapter that authors no
    /// zoned content render identically, and only this count tells them apart.</summary>
    public int[] ZoneGatedMeshes { get; } = new int[ZoneGate.MaxZoneId + 1];

    /// <summary>Models built from an authored <c>lighting: false</c> / <c>fog: false</c> flag,
    /// the one-line evidence that a chapter's self-lit and unfogged geometry was actually read
    /// (a night chapter reporting zero means the flags are not reaching the materials).</summary>
    public int UnlitModelCount { get; private set; }
    public int UnfoggedModelCount { get; private set; }

    /// <summary>Models built with a non-zero UV scroll rate, from either source (the model's own
    /// <c>texture_scroll</c> or the boot script). Logged per world build: it is the one-line
    /// evidence that a chapter animates exactly the surfaces the data says and no others.</summary>
    public int ScrollingModelCount { get; private set; }

    /// <summary>Surfaces built from a polygon's overlay passes (<c>materials[1..]</c>), and the
    /// overlay polygons declined because their mesh renders as a camera-facing sprite or a
    /// cylindrical facade, whose materials carry no depth bias to order a pass with. Logged per
    /// world build: the one-line evidence that the second pass reached the mesh, and the tripwire
    /// if the declined count ever stops being zero.</summary>
    public int OverlayPassSurfaceCount { get; private set; }

    public int OverlayPassDeclinedCount { get; private set; }

    /// <summary>World surfaces committed per transparency class, the census an isolation capture
    /// (<see cref="HiddenAlpha"/>) is read against. Counts SURFACES, not materials: one cached
    /// material skins as many surfaces as the world has groups for it.</summary>
    public int BlendSurfaceCount { get; private set; }

    /// <inheritdoc cref="BlendSurfaceCount"/>
    public int ScissorSurfaceCount { get; private set; }

    /// <summary>Polygons dropped because their material names a texture the retail data lacks and
    /// the original draws nothing for (<see cref="TextureArchive.IsAbsentAndUndrawn"/>). Expected
    /// 2 for a C3 world build (the skydome's two cloud cards) and 0 everywhere else, which is what
    /// makes this the tripwire if the rule ever starts eating a chapter that ships the texture.</summary>
    public int UndrawnPolygonCount { get; private set; }

    /// <summary>Polygons this builder built that carry the <c>no_clutter</c> flag (raw bit
    /// <c>0x800</c>), and those that do not. Counted per BUILT MODEL, not per placement: a model
    /// instanced a hundred times is one mesh build and counts once, which is the granularity the
    /// flag itself has. Logged by <c>WorldSession</c> under <see cref="DebugClutterFlag"/>, so a
    /// screenshot of the view carries the census that explains it.</summary>
    public int FlaggedPolygonCount { get; private set; }

    public int ClearPolygonCount { get; private set; }

    /// <summary>Collision faces built one-sided against two-sided, per surface class, counted per
    /// BUILT MESH the way <see cref="FlaggedPolygonCount"/> is. One-sided is the polygon clearing
    /// <c>SHOW_BACKFACE</c>, which the original's ray test culls from behind.
    /// ⚠ Keep this census. A chapter reading zero one-sided faces is indistinguishable from the
    /// old blanket two-sided flag, and only this count tells them apart.</summary>
    public SortedDictionary<string, (int OneSided, int TwoSided)> CollisionSidedness { get; } = new();

    /// <summary>Collidable back-to-back pairs: one quad authored twice over the same vertices in
    /// opposite winding (docs/formats/gotchas.md). Both halves stay solid once sidedness is
    /// honoured, each from its own front, so a pair loses nothing.</summary>
    public int CollisionBackToBackPairs { get; private set; }

    /// <summary>The textured materials built here, each with its source texture name, so a
    /// caller can re-resolve and swap them without a rebuild. See <see cref="Repaint"/>.</summary>
    public IReadOnlyList<(ShaderMaterial Material, string TextureName)> TexturedMaterials => _texturedMaterials;

    /// <summary>Whether a struck collider's node accepts a crater, the original's
    /// <c>CAN_MODIFY</c> gate read off <see cref="CanModifyMeta"/>.</summary>
    public static bool CanModify(Node? collider) => collider != null && collider.HasMeta(CanModifyMeta);

    /// <summary>The one billboard rule, read straight from the gamez model: is this a flat card the
    /// engine turns toward the camera? Every caller goes through this. Null when the extraction
    /// carries no <c>ModelType</c> (a legacy tree) and means "apply your own fallback", which the
    /// callers do differently because they encode different intents.
    /// ⚠ Gate on <c>ModelType=="Facade"</c>, never on <c>FacadeMode</c> alone. A Default-typed
    /// model can carry a stale <c>FacadeMode</c>, and every 3D city block in C2/C5 does.</summary>
    public static BillboardKind? ClassifyBillboard(GameZMesh mesh)
    {
        if (mesh.ModelType == null)
            return null; // legacy extraction, caller falls back
        if (mesh.ModelType != "Facade")
            return BillboardKind.None;
        return mesh.FacadeMode switch
        {
            "SphericalY" => BillboardKind.Spherical,
            "CylindricalY" => BillboardKind.CylindricalY,
            "CylindricalX" => BillboardKind.CylindricalX,
            _ => BillboardKind.None,
        };
    }

    /// <summary>Which UV axes of this surface stay inside the unit square, i.e. map the texture once
    /// on that axis and never tile it, which is what makes a CLAMPed edge safe for it. The hairline
    /// seam this fixes and the mirrored UV triangle wave behind it: docs/formats/gotchas.md.
    /// ⚠ Never clamp blanketly; 54% of this install's surfaces genuinely tile.
    /// ⚠ Keep the test per axis. A surface can tile one axis and map the other exactly once, and a
    /// both-axes test leaves the fitting axis wrapping and bleeding its opposite edge in.</summary>
    public static UvClampAxes UvAxesWithinUnitSquare(List<GameZPolygon> polys, int pass)
    {
        bool any = false, uFits = true, vFits = true;
        foreach (var poly in polys)
        {
            if (PassUvs(poly, pass) is not { } uvs)
                continue;
            foreach (var uv in uvs)
            {
                any = true;
                if (uv.X < -UvEpsilon || uv.X > 1f + UvEpsilon)
                    uFits = false;
                if (uv.Y < -UvEpsilon || uv.Y > 1f + UvEpsilon)
                    vFits = false;
                if (!uFits && !vFits)
                    return UvClampAxes.None;
            }
        }
        // No UVs at all ⇒ nothing to clamp; keep the surface on the repeating path.
        return !any ? UvClampAxes.None
            : (uFits ? UvClampAxes.U : UvClampAxes.None) | (vFits ? UvClampAxes.V : UvClampAxes.None);
    }

    /// <summary>Builds the subtree rooted at <paramref name="node"/>; null if skipped entirely.
    /// ⚠ <paramref name="zoneGate"/> is per node: the data puts parents and children on different
    /// zones. The rest are inherited by every descendant: <paramref name="collisionSkip"/> renders a
    /// subtree but exempts it from collision, <paramref name="applyActive"/> starts each node at its
    /// own <c>flags.active</c>, and <paramref name="forceLit"/> applies <c>csky_world_light</c>
    /// whatever the authored <c>lighting</c> flag says, which is the deck's exception.</summary>
    public Node3D? BuildSubtree(GameZNode node, Predicate<GameZNode>? skip = null,
        Predicate<GameZNode>? collisionSkip = null, bool forceDoubleSided = false,
        bool forceLit = false, bool zoneGate = false, bool applyActive = false) =>
        BuildSubtree(node, skip, collisionSkip, _generateCollision, forceDoubleSided, forceLit,
            zoneGate, applyActive);

    /// <summary>Re-resolves every textured material through the current substitution hook and
    /// writes the result back into the live material. Used by the viewer's livery lab: the
    /// substitute closure reads a mutable painter, so changing the scheme and calling this
    /// repaints the built aircraft in place.</summary>
    public void Repaint()
    {
        foreach (var (mat, texName) in _texturedMaterials)
            if (Resolve(texName) is { } tex)
                mat.SetShaderParameter("albedo_tex", tex);
    }

    /// <summary>The transparency verdict a world material was generated for. The verdict is a
    /// shader variant rather than a parameter, so this registry is the only way back to it. A
    /// material this builder did not make reads as <see cref="TransparencyClass.None"/>.</summary>
    public TransparencyClass AlphaOf(Material? material) =>
        material != null && _materialAlpha.TryGetValue(material, out var known)
            ? known : TransparencyClass.None;

    /// <summary>The one biased albedo fetch every mip-mapped arm emits, defined in
    /// <c>csky_mip_bias.gdshaderinc</c> beside the global it reads.
    /// ⚠ Never emit a bare <c>texture(albedo_tex, ...)</c> anywhere. The original applies the
    /// chapter's bias as one device render state. An arm sampling unbiased draws that chapter at a
    /// mip level the original never chose (docs/org/textures.md).</summary>
    internal static string SampleAlbedo(string uv) => $"csky_sample_albedo(albedo_tex, {uv})";

    internal static bool UvsWithinUnitSquare(List<GameZPolygon> polys, int pass) =>
        UvAxesWithinUnitSquare(polys, pass) == UvClampAxes.Both;

    /// <summary>The translucent twin of a generated opaque shader: the same code with the blend
    /// variants' <c>ALPHA</c> line appended, so a runtime fade landing on an opaque-pass world piece
    /// has an alpha path to drive. Writing ALPHA moves the twin into the transparent pass, so
    /// ⚠ callers must install it per instance, never into the shared caches. Null when the code
    /// cannot take the line: no preamble means <c>csky_opacity</c> is undeclared, and no
    /// <c>col</c> local means there is no alpha to read.</summary>
    internal static Shader? FadeShaderFor(Shader source)
    {
        string code = source.Code;
        if (!code.Contains(InstanceUniformsInclude, StringComparison.Ordinal)
            || !code.Contains("vec4 col = ", StringComparison.Ordinal))
            return null;
        int close = code.LastIndexOf('}');
        if (close < 0)
            return null;
        return new Shader { Code = code[..close] + $"    ALPHA = col.a{OpacityTerm};\n" + code[close..] };
    }

    /// <summary>The surface class one texture name names, <c>"water"</c>, <c>"buildings"</c>, or
    /// null for the untagged default. The collision buckets are built from this
    /// (<see cref="CollidersForMesh"/>), and <c>MapEdgeExtender</c>'s <c>--dump-tilegrid</c> census
    /// reports it for every tile candidate it rejected: what the dropped geometry IS, not how big
    /// it was, is what says whether the continuation owed it a copy.</summary>
    internal static string? ClassifySurface(string? texture)
    {
        if (string.IsNullOrEmpty(texture))
            return null;
        var t = texture.ToLowerInvariant();
        // 'bldgshadow'/'bld_shadow' are baked ground shadow decals, not building geometry.
        if (t.Contains("shadow"))
            return t.StartsWith("water") || t.Contains("splash") ? "water" : null;
        if (t.StartsWith("water") || t.StartsWith("wtr") || t.StartsWith("srf")
            || t.Contains("wakefront") || t.Contains("watersquirt"))
            return "water";
        // 'empire'/'chrysler' are the C2/C5 film-set skyscraper walls (empire1, chrysler1/2,
        // the only 3 matching textures install-wide, measured before widening the classifier).
        if (t.Contains("build") || t.StartsWith("hangar") || t.StartsWith("bld")
            || t.Contains("cblock") || t.Contains("warehouse") || t.Contains("roof")
            || t.Contains("filmblock") || t.StartsWith("empire") || t.StartsWith("chrysler"))
            return "buildings";
        return null;
    }

    /// <summary>The built <see cref="ArrayMesh"/> for one gamez model index, from this builder's
    /// shared cache and carrying this builder's materials. Exists for
    /// <see cref="ClutterBuilder"/>'s 3D-decoration path, which draws one MultiMesh over this single
    /// mesh; the transform, the <c>node_bias</c> instance uniform and the collider are the caller's.
    /// The override flags share <see cref="BuildSubtree"/>'s cache; <paramref name="clutterFade"/>
    /// selects the bias shader's per-instance fade variant over the MultiMesh custom data.</summary>
    internal ArrayMesh? SharedMesh(int meshIndex, bool forceDoubleSided = false, bool forceLit = false,
        bool clutterFade = false) =>
        meshIndex >= 0 && meshIndex < _gamez.Meshes.Count
            ? GetMesh(meshIndex, forceDoubleSided, forceLit, clutterFade)
            : null;

    /// <summary>An untextured quad-frame mesh carrying the SAME fog-pipelined material a
    /// <c>Colored</c> polygon with no material entry gets, for <see cref="WorldBuilder"/>'s
    /// below-band-ceiling extension: that geometry is nowhere in the gamez mesh table, so it cannot
    /// go through <see cref="BuildSubtree"/> or <see cref="SharedMesh"/>. Always double-sided and
    /// built <c>lit: false</c>, because every quad sits past its chapter's authored fog far, where
    /// the mix result is <c>csky_fog_color</c> whatever <c>ALBEDO</c> is.</summary>
    internal ArrayMesh BuildFlatQuadMesh(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, Vector3 D)> quads)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        void Vert(Vector3 p)
        {
            st.SetNormal(Vector3.Up);
            st.SetColor(Colors.White);
            st.AddVertex(p);
        }
        foreach (var (a, b, c, d) in quads)
        {
            Vert(a); Vert(b); Vert(c);
            Vert(a); Vert(c); Vert(d);
        }
        st.SetMaterial(GetMaterial(-1, priority: 0, rank: 0, noClutter: false, doubleSided: true,
            lit: false, fogged: true));
        var mesh = new ArrayMesh();
        st.Commit(mesh);
        return mesh;
    }

    /// <summary>The cross-node draw-order tie-break for one gamez node. Every instance uniform
    /// named <c>node_bias</c>, placed world, clutter decorations, map-edge tiles, comes from
    /// here, so the three cannot drift apart.</summary>
    internal float NodeBiasOf(int nodeIndex)
    {
        if (ConflictRanks == null)
            return nodeIndex * NodeOrderBias * DepthBiasScale;
        // Absent = this node conflicts with nothing, which is rank 0 by construction.
        ConflictRanks.TryGetValue(nodeIndex, out int rank);
        return Math.Min(rank, ConflictRankCap) * ConflictRankBias * DepthBiasScale;
    }

    private static CylAxis GetCylindricalAxis(GameZMesh mesh) => ClassifyBillboard(mesh) switch
    {
        BillboardKind.CylindricalY => CylAxis.Y,
        BillboardKind.CylindricalX => CylAxis.X,
        _ => CylAxis.None,
    };

    // The UVs one pass of a polygon is skinned by: pass 0 is the base UvCoords, pass N the Nth
    // OverlayPasses entry's own. They are independent mappings of the same triangles.
    private static List<Vector2>? PassUvs(GameZPolygon poly, int pass) =>
        pass == 0 ? poly.UvCoords
            : poly.OverlayPasses != null && pass - 1 < poly.OverlayPasses.Count
                ? poly.OverlayPasses[pass - 1].UvCoords
                : null;

    // flatColorRestated: the polygon's vertex colours only restate its untextured material's own
    // colour (GameZ.VertexColorsRestateMaterialColor), so emit white and let the shader apply that
    // authored value once rather than squaring it.
    // ⚠ flagColors is per polygon, never an instance tint: the no_clutter flag varies WITHIN a mesh.
    private static void EmitPolygon(SurfaceTool st, GameZMesh mesh, GameZPolygon poly, Vector3 offset,
        List<Vector2>? uvs, bool flatColorRestated = false, bool flagColors = false)
    {
        int n = poly.VertexIndices.Count;
        if (n < 3)
            return;
        if (poly.TriangleStrip)
        {
            for (int i = 0; i + 2 < n; i++)
            {
                // alternate winding so all triangles of the strip face the same way
                if ((i & 1) == 0)
                    EmitTriangle(st, mesh, poly, i, i + 1, i + 2, offset, uvs, flatColorRestated, flagColors);
                else
                    EmitTriangle(st, mesh, poly, i, i + 2, i + 1, offset, uvs, flatColorRestated, flagColors);
            }
        }
        else
        {
            for (int i = 1; i + 1 < n; i++)
                EmitTriangle(st, mesh, poly, 0, i, i + 1, offset, uvs, flatColorRestated, flagColors);
        }
    }

    private static void EmitTriangle(SurfaceTool st, GameZMesh mesh, GameZPolygon poly, int a, int b, int c,
        Vector3 offset, List<Vector2>? uvs, bool flatColorRestated = false, bool flagColors = false)
    {
        Vector3 Pos(int corner) => mesh.Vertices[poly.VertexIndices[corner]] - offset;
        // flat normal fallback for polygons without normal data
        var flat = (Pos(b) - Pos(a)).Cross(Pos(c) - Pos(a));
        flat = flat.LengthSquared() > 1e-12f ? flat.Normalized() : Vector3.Up;

        foreach (var corner in stackalloc[] { a, b, c })
        {
            var normal = flat;
            if (poly.NormalIndices != null && corner < poly.NormalIndices.Count)
            {
                int ni = poly.NormalIndices[corner];
                if (ni >= 0 && ni < mesh.Normals.Count && mesh.Normals[ni].LengthSquared() > 1e-12f)
                    normal = mesh.Normals[ni].Normalized();
            }
            st.SetNormal(normal);
            st.SetColor(flagColors
                ? poly.NoClutter ? FlaggedColor : ClearColor
                : !flatColorRestated && poly.VertexColors != null && corner < poly.VertexColors.Count
                    ? poly.VertexColors[corner]
                    : Colors.White);
            if (uvs != null && corner < uvs.Count)
                st.SetUV(uvs[corner]);
            st.AddVertex(Pos(corner));
        }
    }

    // Total triangulated area of one polygon, triangulated exactly as EmitPolygon does
    // (strip order for tri_strips, a fan otherwise), a strip's raw index list is not an
    // outline, so fanning it would measure the wrong shape.
    private static float PolygonArea(GameZMesh mesh, GameZPolygon poly)
    {
        int n = poly.VertexIndices.Count;
        if (n < 3)
            return 0f;
        float Tri(int a, int b, int c)
        {
            var va = mesh.Vertices[poly.VertexIndices[a]];
            return 0.5f * (mesh.Vertices[poly.VertexIndices[b]] - va)
                .Cross(mesh.Vertices[poly.VertexIndices[c]] - va).Length();
        }
        float area = 0f;
        if (poly.TriangleStrip)
        {
            for (int i = 0; i + 2 < n; i++)
                area += Tri(i, i + 1, i + 2);
        }
        else
        {
            for (int i = 1; i + 1 < n; i++)
                area += Tri(0, i, i + 1);
        }
        return area;
    }

    // Same triangulation as EmitPolygon/PolygonArea (strip order for tri_strips, a fan
    // otherwise), but positions only, a collision shape carries no material/UV/normal data.
    // ⚠ Do not filter polygons here by texture alpha. The original's weapon ray reads no texture
    // at all (docs/org/weaponRay.md), so an alpha-cutout card is solid to it too.
    // `flip` reverses each triangle, which is what a one-sided shape needs (see CollidersForMesh).
    private static void EmitCollisionFaces(GameZMesh mesh, GameZPolygon poly, Vector3 offset,
        List<Vector3> faces, bool flip = false)
    {
        int n = poly.VertexIndices.Count;
        if (n < 3)
            return;
        Vector3 Pos(int i) => mesh.Vertices[poly.VertexIndices[i]] - offset;
        void Tri(int a, int b, int c)
        {
            faces.Add(Pos(a));
            faces.Add(Pos(flip ? c : b));
            faces.Add(Pos(flip ? b : c));
        }
        if (poly.TriangleStrip)
        {
            for (int i = 0; i + 2 < n; i++)
            {
                if ((i & 1) == 0)
                    Tri(i, i + 1, i + 2);
                else
                    Tri(i, i + 2, i + 1);
            }
        }
        else
        {
            for (int i = 1; i + 1 < n; i++)
                Tri(0, i, i + 1);
        }
    }

    // One trimesh of a surface class's faces, skipped when that half is empty so a class present in
    // one sidedness alone still costs one shape.
    private static void AddShape(List<ConcavePolygonShape3D> into, List<Vector3> faces, bool backface)
    {
        if (faces.Count == 0)
            return;
        var shape = new ConcavePolygonShape3D { BackfaceCollision = backface };
        shape.SetFaces(faces.ToArray());
        into.Add(shape);
    }

    // One quad authored twice over the same corners in opposite winding: how the data gives a
    // one-sided surface a front and a back. Matched on the corner SET rather than the loop, since
    // a strip and a fan over the same corners are the same face, and separated by Newell normal.
    private static int BackToBackPairs(GameZMesh mesh)
    {
        var byCorners = new Dictionary<string, List<Vector3>>();
        foreach (var poly in mesh.Polygons)
        {
            if (poly.VertexIndices.Count < 3)
                continue;
            int[] corners = poly.VertexIndices.ToArray();
            Array.Sort(corners);
            string key = string.Join(",", corners);
            if (!byCorners.TryGetValue(key, out var normals))
                byCorners[key] = normals = new List<Vector3>();
            normals.Add(NewellNormal(mesh, poly));
        }

        int pairs = 0;
        foreach (var normals in byCorners.Values)
        {
            for (int i = 0; i < normals.Count; i++)
            {
                for (int j = i + 1; j < normals.Count; j++)
                {
                    if (normals[i].Dot(normals[j]) < 0f)
                        pairs++;
                }
            }
        }
        return pairs;
    }

    // The polygon's own facing, summed around the whole loop rather than taken off one corner: a
    // source polygon can carry a degenerate corner that a single cross product zeroes.
    private static Vector3 NewellNormal(GameZMesh mesh, GameZPolygon poly)
    {
        var normal = Vector3.Zero;
        int count = poly.VertexIndices.Count;
        for (int i = 0; i < count; i++)
        {
            var a = mesh.Vertices[poly.VertexIndices[i]];
            var b = mesh.Vertices[poly.VertexIndices[(i + 1) % count]];
            normal += new Vector3(
                (a.Y - b.Y) * (a.Z + b.Z),
                (a.Z - b.Z) * (a.X + b.X),
                (a.X - b.X) * (a.Y + b.Y));
        }
        return normal;
    }

    private static string Sanitize(string name)
    {
        // Godot node names must not contain . : @ / " %
        Span<char> bad = stackalloc[] { '.', ':', '@', '/', '"', '%' };
        foreach (var ch in bad)
            name = name.Replace(ch, '_');
        return name.Length == 0 ? "node" : name;
    }

    private Node3D? BuildSubtree(GameZNode node, Predicate<GameZNode>? skip,
        Predicate<GameZNode>? collisionSkip, bool collidable, bool forceDoubleSided, bool forceLit,
        bool zoneGate, bool applyActive)
    {
        if (skip != null && skip(node))
            return null;
        // Of each LOD group, keep only the highest-detail level (range starts at 0).
        if (node.Kind == "Lod" && node.LodRangeMin != 0f)
            return null;
        if (collidable && collisionSkip != null && collisionSkip(node))
            collidable = false;

        var n3d = new Node3D { Name = Sanitize(node.Name) };
        // The ORIGINAL gamez name, for name-based resolution (AnimRuntime): Godot both
        // sanitizes ('.'→'_') and auto-renames duplicate siblings, so Name is unreliable.
        n3d.SetMeta(AnimRuntime.NameMeta, node.Name);
        // Compiled animation definitions reference their objects by exactly this flat index, which
        // binds them unambiguously where names do not: names are duplicated and carry a '.flt'
        // suffix inconsistently. See AnimRuntime.
        n3d.SetMeta(AnimRuntime.IndexMeta, node.Index);
        // The mission-structure flag and team travel with the built node because the destructible
        // registry meets a pool as a Node3D and has no gamez node to ask. Resolved here because
        // this is where the mission being built is known, and the ownership slot is per mission.
        if (node.IsMissionStructure)
        {
            if (_gamez.WorldObjectTeam(node, MissionSlot) is var team && team != 0)
            {
                n3d.SetMeta(MissionStructureTeamMeta, team);
            }

            if (node.IsGasbagStructure)
            {
                n3d.SetMeta(MissionStructureGasbagMeta, true);
            }
        }
        // Root transform is the node's OWN Local, not its world transform, a caller slicing a
        // nested node must overwrite it with GameZ.WorldTransformOf(node) or it lands at the
        // parent's origin.
        if (node.Local is { } local)
            n3d.Transform = local;

        // ACTIVE is live visibility, not existence: the record's bit is the node's starting state
        // and choreography moves it, so an inactive node is still built and indexed. Opt-in because
        // a library root a caller slices out (a plane, a prop) ships inactive and IS its subject.
        if (applyActive)
            n3d.Visible = node.Active;

        // The node's own zone_id layer, resolved once for both mesh instances below. 0 =
        // ungated (zone_id −1/0, or the gate switched off for this build), leave the instance on
        // the default layer, which every cull mask always keeps.
        uint zoneLayer = zoneGate ? ZoneGate.LayerFor(node.ZoneId) : 0u;
        if (node.MeshIndex >= 0 && node.MeshIndex < _gamez.Meshes.Count
            && !_gamez.IsMarkerGizmo(node.MeshIndex))
        {
            var mesh = GetMesh(node.MeshIndex, forceDoubleSided, forceLit);
            if (mesh != null)
            {
                var mi = new MeshInstance3D { Mesh = mesh, Name = "mesh" };
                if (zoneLayer != 0)
                {
                    // MOVED onto the zone layer, never added to it: a cull mask ORs its bits, so
                    // an instance left on the default layer 1 as well would keep drawing in a
                    // camera that dropped the zone bit.
                    mi.Layers = zoneLayer;
                    ZoneGatedMeshes[node.ZoneId]++;
                }
                mi.SetInstanceShaderParameter("node_bias", NodeBiasOf(node.Index));
                // Billboard meshes were recentered on their quad center; put the instance
                // there so the material's billboard pivots at the center, not the node origin.
                if (_meshPivotCache.TryGetValue(node.MeshIndex, out var pivot))
                    mi.Position = pivot;
                n3d.AddChild(mi);
                MeshInstanceCount++;
                if (collidable)
                    AttachCollision(n3d, node.MeshIndex, node.CanModify);
            }
            // Point-sprite lights (night-sky stars, nav/tower beacons): rendered by the
            // original engine as small glowing dots. Never collidable, never shadowed.
            if (_gamez.Meshes[node.MeshIndex].Lights.Count > 0)
            {
                var lights = new MeshInstance3D
                {
                    Mesh = GetLightPoints(node.MeshIndex),
                    Name = "lights",
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                if (zoneLayer != 0)
                {
                    lights.Layers = zoneLayer;
                    ZoneGatedMeshes[node.ZoneId]++;
                }
                n3d.AddChild(lights);
                MeshInstanceCount++;
            }
        }

        foreach (var childIndex in node.Children)
        {
            if (childIndex < 0 || childIndex >= _gamez.Nodes.Count)
                continue;
            var child = BuildSubtree(_gamez.Nodes[childIndex], skip, collisionSkip, collidable,
                forceDoubleSided, forceLit, zoneGate, applyActive);
            if (child != null)
                n3d.AddChild(child);
        }
        return n3d;
    }

    // One static trimesh body PER (SURFACE CLASS, SOIL) pair actually present in the mesh, not one
    // body for the whole mesh, see CollidersForMesh. The parent node carries the world transform,
    // so each collider lines up with the rendered surface it was carved from. Shapes are cached
    // per mesh index and shared across instances (shapes are resources).
    private void AttachCollision(Node3D parent, int meshIndex, bool canModify)
    {
        bool tracked = false;
        foreach (var (name, surface, surfaceId, shapes) in CollidersForMesh(meshIndex))
        {
            // ⚠ Keep the distinct per-bucket names rather than "col" for all of them. Sibling
            // bodies sharing a requested name are renamed to an opaque "@StaticBody3D@N",
            // breaking ColliderOverlay's "col" check once a mesh splits in two.
            var body = new StaticBody3D { Name = name };
            // ⚠ A bucket's one-sided and two-sided halves share ONE body. Two bodies would collide
            // with the naming rule above, and every meta, count and OwnerOf answer below is per
            // bucket, not per shape.
            foreach (var shape in shapes)
                body.AddChild(new CollisionShape3D { Shape = shape });
            // Stamp the struck-surface class (water / buildings), so a weapon impact can pick the
            // right IMPACT variant. 'default' (terrain / anything unclassified) is the
            // common case and stamps nothing.
            if (surface != null)
                body.SetMeta(SurfaceMeta, surface);
            // Every body carries the original's numeric surface id too, a different name space
            // from the class string above (see SurfaceIdMeta).
            body.SetMeta(SurfaceIdMeta, surfaceId);
            // The node's own crater gate, stamped per body since a weapon reads the body it struck.
            if (canModify)
                body.SetMeta(CanModifyMeta, true);
            parent.AddChild(body);
            ColliderCount++;
            tracked = true;
        }
        // Collision follows the node's tree visibility from here on; nothing else writes
        // Disabled (WorldCollision).
        if (tracked)
        {
            WorldCollision.Track(parent);
        }
    }

    // This mesh's colliding geometry split into one trimesh per (surface class, soil) pair actually
    // present, each polygon's own material deciding which shape it joins. The soil split is what
    // lets a body's SurfaceIdMeta be every struck polygon's own soil, as the original reads it per
    // polygon (docs/org/weaponImpact.md). Within a class the soil with the most polygons keeps the
    // class's plain name and comes first; any other soil is suffixed. Cached per mesh.
    // ⚠ Do not merge buckets by area or count; a coastal tile's water or a shed's default wall then reads wrong.
    private List<(string Name, string? Surface, int SurfaceId, List<ConcavePolygonShape3D> Shapes)> CollidersForMesh(int meshIndex)
    {
        if (_colliderCache.TryGetValue(meshIndex, out var cached))
            return cached;
        var mesh = _gamez.Meshes[meshIndex];
        _meshPivotCache.TryGetValue(meshIndex, out var offset); // Vector3.Zero when this mesh has none
        // "" stands in for the untagged/default bucket: Dictionary<TKey> needs a non-null key.
        const string defaultTag = "";
        var buckets = new Dictionary<(string Tag, int Soil), (List<Vector3> OneSided, List<Vector3> TwoSided, int Polys)>();
        foreach (var poly in mesh.Polygons)
        {
            bool hasMaterial = poly.MaterialIndex >= 0 && poly.MaterialIndex < _gamez.Materials.Count;
            var material = hasMaterial ? _gamez.Materials[poly.MaterialIndex] : null;
            string tag = ClassifySurface(material?.TextureName) ?? defaultTag;
            var key = (tag, material?.SoilId ?? 0);
            if (!buckets.TryGetValue(key, out var faces))
                faces = (new List<Vector3>(), new List<Vector3>(), 0);
            buckets[key] = faces with { Polys = faces.Polys + 1 };
            // ⚠ The one-sided half is emitted REVERSED: the source's visible side is Godot's BACK
            // face, while a shape without BackfaceCollision is solid along Godot's own face normals
            // (docs/formats/gotchas.md). Unreversed it would be solid from behind alone.
            EmitCollisionFaces(mesh, poly, offset,
                poly.ShowBackface ? faces.TwoSided : faces.OneSided, flip: !poly.ShowBackface);
            CountSidedness(tag, poly.ShowBackface);
        }
        CollisionBackToBackPairs += BackToBackPairs(mesh);
        var result = new List<(string, string?, int, List<ConcavePolygonShape3D>)>();
        // Classes in first-seen order, as the bodies were ordered before the soil split.
        foreach (var tag in buckets.Keys.Select(k => k.Tag).Distinct().ToList())
        {
            var soils = buckets.Where(kv => kv.Key.Tag == tag)
                .OrderByDescending(kv => kv.Value.Polys)
                .ThenBy(kv => kv.Key.Soil)
                .ToList();
            string className = tag == defaultTag ? "col" : $"col_{tag}";
            bool first = true;
            foreach (var (key, faces) in soils)
            {
                // A polygon is solid from the side it is seen from and no other, the test the
                // original runs (docs/org/weaponRay.md). The winding is per polygon, and
                // `chapter-census` is the tripwire for a down-wound tile a plane falls through.
                var shapes = new List<ConcavePolygonShape3D>();
                AddShape(shapes, faces.OneSided, backface: false);
                AddShape(shapes, faces.TwoSided, backface: true);
                if (shapes.Count == 0)
                    continue;
                string name = first ? className : $"{className}_soil{key.Soil.ToString(CultureInfo.InvariantCulture)}";
                first = false;
                result.Add((name, tag == defaultTag ? null : tag, key.Soil, shapes));
            }
        }
        _colliderCache[meshIndex] = result;
        return result;
    }

    // The sidedness census, keyed by the same surface class the bodies are named for; "" is
    // CollidersForMesh's untagged bucket, so the printed line and the body names agree.
    private void CountSidedness(string tag, bool showBackface)
    {
        CollisionSidedness.TryGetValue(tag, out var seen);
        CollisionSidedness[tag] = showBackface
            ? (seen.OneSided, seen.TwoSided + 1)
            : (seen.OneSided + 1, seen.TwoSided);
    }

    // Point-sprite lights: camera-facing soft radial glows, additive so they shine over whatever is
    // behind them. The data drives reach and blink, FadeFar/FadeSlope are the original's own
    // distance fade, BlinkPeriod its beacon flash; the sprite's size is this renderer's, since the
    // original draws one screen pixel and has no size to copy. One POINTS surface per
    // (fade, blink) group per mesh. The camera-anchored skydome's stars sit past any data range,
    // so BuildHorizon exempts them through the csky_light_fade instance uniform.
    private ArrayMesh GetLightPoints(int meshIndex)
    {
        if (_lightMeshCache.TryGetValue(meshIndex, out var cached))
            return cached;

        // Group the mesh's lights by their fade/blink params → one POINTS surface +
        // shared material per group (a mesh's lights are uniform in practice).
        var groups = new Dictionary<(float Far, float Slope, float Blink), (List<Vector3> P, List<Color> C)>();
        foreach (var l in _gamez.Meshes[meshIndex].Lights)
        {
            var key = (l.FadeFar, l.FadeSlope, l.BlinkPeriod);
            if (!groups.TryGetValue(key, out var g))
                groups[key] = g = (new List<Vector3>(), new List<Color>());
            g.P.Add(l.Position);
            g.C.Add(l.Color);
        }

        var mesh = new ArrayMesh();
        foreach (var (key, g) in groups)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = g.P.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = g.C.ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays);

            if (!_lightMaterialCache.TryGetValue(key, out var mat))
            {
                _lightShader ??= new Shader { Code = LightShaderCode };
                mat = new ShaderMaterial { Shader = _lightShader };
                mat.SetShaderParameter("range_far", key.Far);
                mat.SetShaderParameter("fade_slope", key.Slope);
                mat.SetShaderParameter("blink_period", key.Blink);
                _lightMaterialCache[key] = mat;
            }
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
        }
        _lightMeshCache[meshIndex] = mesh;
        return mesh;
    }

    private ArrayMesh? GetMesh(int meshIndex, bool forceDoubleSided = false, bool forceLit = false,
        bool clutterFade = false)
    {
        if (_meshCache.TryGetValue((meshIndex, forceDoubleSided, forceLit, clutterFade), out var cached))
            return cached;
        var mesh = BuildMesh(_gamez.Meshes[meshIndex], meshIndex, forceDoubleSided, forceLit, clutterFade);
        _meshCache[(meshIndex, forceDoubleSided, forceLit, clutterFade)] = mesh;
        return mesh;
    }

    private ArrayMesh? BuildMesh(GameZMesh mesh, int meshIndex, bool forceDoubleSided, bool forceLit,
        bool clutterFade = false)
    {
        if (mesh.Polygons.Count == 0)
            return null;

        // Camera-FACING sprites are recentered on their quad centre: the material billboards around
        // the mesh origin, but the source quads sit offset from it by up to ~650 m, so without this
        // they would swing around the node as the camera turns.
        bool glowSprite = IsGlowSpriteMesh(mesh);
        // ⚠ Never recentre a single-axis facade. Its shader spins the quad about the model origin,
        // so an offset quad ORBITS it, which is how the lighthouse beam sweeps, the street lamps
        // hang off their poles, and a muzzle flash sits at the barrel tip, not the gun's pivot.
        var cylAxis = GetCylindricalAxis(mesh);
        var offset = Vector3.Zero;
        if ((UsesBillboardTexture(mesh) || glowSprite) && mesh.Vertices.Count > 0)
        {
            foreach (var v in mesh.Vertices)
                offset += v;
            offset /= mesh.Vertices.Count;
            _meshPivotCache[meshIndex] = offset;
        }

        // One Godot surface per (material, priority, no_clutter, sidedness, pass): each selects a
        // different material, depth bias or cull mode. ⚠ Keep the groups in first-occurrence order,
        // which is the original's within-mesh draw order and what the group's rank tie-breaks on.
        var groups = new List<(int Material, int Priority, bool NoClutter, bool DoubleSided, int Pass, List<GameZPolygon> Polys)>();
        var groupIndex = new Dictionary<(int, int, bool, bool, int), int>();
        int overlayLevels = 0;
        foreach (var poly in mesh.Polygons)
        {
            if (DrawsNothing(poly.MaterialIndex))
            {
                UndrawnPolygonCount++;
                continue;
            }
            bool doubleSided = forceDoubleSided || !_cullBackfaces || poly.ShowBackface;
            var key = (poly.MaterialIndex, poly.Priority, poly.NoClutter, doubleSided, 0);
            if (!groupIndex.TryGetValue(key, out int gi))
            {
                groupIndex[key] = gi = groups.Count;
                groups.Add((poly.MaterialIndex, poly.Priority, poly.NoClutter, doubleSided, 0, new List<GameZPolygon>()));
            }
            groups[gi].Polys.Add(poly);
            if (poly.NoClutter)
                FlaggedPolygonCount++;
            else
                ClearPolygonCount++;
            if (poly.OverlayPasses != null)
                overlayLevels = Math.Max(overlayLevels, poly.OverlayPasses.Count);
        }

        // A sprite or facade mesh takes a billboard material, which has no depth-bias parameter to
        // order a pass with, so declining leaves it exactly as it builds without one. Measured zero
        // across all 8 chapters and planes.zbd; counted so it stays that way.
        if (overlayLevels > 0 && (glowSprite || cylAxis != CylAxis.None))
        {
            foreach (var poly in mesh.Polygons)
                OverlayPassDeclinedCount += poly.OverlayPasses?.Count ?? 0;
            overlayLevels = 0;
        }

        // ⚠ Overlay passes must become groups AFTER every base group, so a mesh's whole base skin is
        // committed before anything drawn on top of it. Their ordering over their own base is
        // OverlayPassBias, never rank, see the constant.
        for (int pass = 1; pass <= overlayLevels; pass++)
        {
            foreach (var poly in mesh.Polygons)
            {
                if (poly.OverlayPasses == null || poly.OverlayPasses.Count < pass)
                    continue;
                // The base polygon is gone, so its overlays have nothing to sit on; an overlay
                // naming an undrawn texture goes the same way.
                if (DrawsNothing(poly.MaterialIndex)
                    || DrawsNothing(poly.OverlayPasses[pass - 1].MaterialIndex))
                {
                    UndrawnPolygonCount++;
                    continue;
                }
                bool doubleSided = forceDoubleSided || !_cullBackfaces || poly.ShowBackface;
                var key = (poly.OverlayPasses[pass - 1].MaterialIndex, poly.Priority, poly.NoClutter, doubleSided, pass);
                if (!groupIndex.TryGetValue(key, out int gi))
                {
                    groupIndex[key] = gi = groups.Count;
                    groups.Add((key.Item1, poly.Priority, poly.NoClutter, doubleSided, pass, new List<GameZPolygon>()));
                    OverlayPassSurfaceCount++;
                }
                groups[gi].Polys.Add(poly);
            }
        }

        // The model's UV animation: the boot script's rate where a mission set one, else the model's
        // own field. Only the bias path carries it, because every scrolling model in this install is
        // ModelType "Default", so no glow flare or cylindrical facade needs it.
        var scroll = EffectiveScroll(mesh, meshIndex);
        if (scroll != Vector2.Zero)
            ScrollingModelCount++;

        // The model's own authored render flags (docs/formats/gotchas.md). `forceLit` is deck-local
        // (`WorldBuilder.Add`): the deck is the ONE surface the original dims by the mission
        // SUNLIGHT despite authoring `lighting: false`, and `SunIncidence` is calibrated on it.
        bool lit = mesh.Lighting || forceLit;
        bool fogged = mesh.Fog;
        if (!lit)
            UnlitModelCount++;
        if (!fogged)
            UnfoggedModelCount++;

        // Every polygon was undrawn: no surface to commit, and a surface-less ArrayMesh would still
        // cost an instance. Same answer as an empty mesh.
        if (groups.Count == 0)
            return null;

        var arrayMesh = new ArrayMesh();
        for (int rank = 0; rank < groups.Count; rank++)
        {
            var (materialIndex, priority, noClutter, doubleSided, pass, polys) = groups[rank];
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            foreach (var poly in polys)
                EmitPolygon(st, mesh, poly, offset, PassUvs(poly, pass),
                    _gamez.VertexColorsRestateMaterialColor(poly, materialIndex), DebugClutterFlag);
            // A surface whose UVs never leave the unit square never needs the sampler to wrap, and
            // wrapping is what produces the hairline seams (see UvsWithinUnitSquare). A scrolling
            // surface is excluded: its UVs run past 1 and rely on repeat to come back round.
            var unitAxes = UvAxesWithinUnitSquare(polys, pass);
            bool clampUv = scroll == Vector2.Zero && unitAxes == UvClampAxes.Both;
            // The partial case: one axis fits the unit square while the other tiles or scrolls. The
            // sampler must keep repeating for that other axis, so the fitting, non-scrolling axis is
            // clamped in the shader instead (see UvAxesWithinUnitSquare).
            var edgeClamp = UvClampAxes.None;
            if (!clampUv)
            {
                if (unitAxes.HasFlag(UvClampAxes.U) && scroll.X == 0)
                    edgeClamp |= UvClampAxes.U;
                if (unitAxes.HasFlag(UvClampAxes.V) && scroll.Y == 0)
                    edgeClamp |= UvClampAxes.V;
            }

            if (clampUv)
                ClampedSurfaceTotal++;
            else if (edgeClamp != UvClampAxes.None)
                EdgeClampedSurfaceTotal++;
            // The glow/cylindrical paths take only the full clamp: their quads' UVs are
            // authored inside the unit square, so the partial case cannot arise there. They also
            // carry no clutter fade: no 3D decoration in the install is a glow or a facade.
            var surfaceMaterial = glowSprite ? GetGlowMaterial(materialIndex, fogged, clampUv)
                : cylAxis != CylAxis.None ? GetCylindricalMaterial(materialIndex, cylAxis, lit, fogged, clampUv)
                : GetMaterial(materialIndex, priority, rank, noClutter, doubleSided, scroll, clampUv, lit, fogged, pass, edgeClamp, clutterFade);
            var alpha = _materialAlpha.TryGetValue(surfaceMaterial, out var known) ? known : TransparencyClass.None;
            if (alpha == TransparencyClass.BlendSurface)
                BlendSurfaceCount++;
            else if (alpha == TransparencyClass.ScissorSurface)
                ScissorSurfaceCount++;
            // ⚠ Drop the surface, never hide the instance. The group is one of several on a shared
            // mesh, and the classes are isolated from each other, not from the world.
            if (alpha != TransparencyClass.None && HiddenAlpha.HasFlag(alpha))
                continue;
            st.SetMaterial(surfaceMaterial);
            st.Commit(arrayMesh);
        }
        return arrayMesh;
    }

    // The UV scroll rate for one model: the caller's per-model override if it has one, else the
    // model's own texture_scroll field. The two are the same setting read at two different times,
    // the engine's Object3DSetScroll writes this field, so a chapter's tex_fx.gw rates are already
    // baked into the shipped gamez while the per-mission ones can only be applied at load.
    private Vector2 EffectiveScroll(GameZMesh mesh, int meshIndex) =>
        _scrollOverrides != null && _scrollOverrides.TryGetValue(meshIndex, out var rate)
            ? rate
            : mesh.TextureScroll;

    // A polygon the original draws nothing at all for: its material names a texture the retail data
    // lacks AND this chapter's archive cannot resolve it. Dropped before grouping, so no surface,
    // no material and no fallback card is built for it. ⚠ The resolve half lives in
    // TextureArchive.IsAbsentAndUndrawn and is what keeps the seven chapters that DO ship cloud1/
    // cloud2 drawing them; a name-only test here would strip their skydomes too.
    private bool DrawsNothing(int materialIndex) =>
        materialIndex >= 0 && materialIndex < _gamez.Materials.Count
        && _gamez.Materials[materialIndex].TextureName is { } texName
        && _textures.IsAbsentAndUndrawn(texName);

    // True if any of the mesh's polygons is skinned with a billboard (cloud-sprite) texture.
    private bool UsesBillboardTexture(GameZMesh mesh)
    {
        if (_billboardTexture == null)
            return false;
        foreach (var poly in mesh.Polygons)
        {
            if (poly.MaterialIndex < 0 || poly.MaterialIndex >= _gamez.Materials.Count)
                continue;
            var tex = _gamez.Materials[poly.MaterialIndex].TextureName;
            if (tex != null && _billboardTexture(tex))
                return true;
        }
        return false;
    }

    // A glow flare SPRITE, the lamp and beacon light quads: billboards fully toward the camera,
    // never night-dimmed, since a light source is not lit scenery. That is ClassifyBillboard's
    // Spherical case minus the cloud sprites, which are routed through _billboardTexture instead.
    // ⚠ The legacy branch (one polygon plus a "flare"-ish texture name) is not the real rule. It
    // under-matches, and is kept only so the documented v0.6.1 rollback still works.
    private bool IsGlowSpriteMesh(GameZMesh mesh)
    {
        if (ClassifyBillboard(mesh) is { } kind)
            return kind == BillboardKind.Spherical && !UsesBillboardTexture(mesh);

        // Legacy fallback (no ModelType data in this extraction).
        if (_glowTexture == null || mesh.Polygons.Count != 1)
            return false;
        var poly = mesh.Polygons[0];
        if (poly.MaterialIndex < 0 || poly.MaterialIndex >= _gamez.Materials.Count)
            return false;
        var tex = _gamez.Materials[poly.MaterialIndex].TextureName;
        return tex != null && _glowTexture(tex);
    }

    // A glow flare is a light source: its shader never applied csky_world_light in the first
    // place, so the model's `lighting` flag has no term to gate here and is not part of the key.
    private Material GetGlowMaterial(int materialIndex, bool fogged, bool clampUv)
    {
        var key = (materialIndex, fogged, clampUv);
        if (_glowMaterialCache.TryGetValue(key, out var cached))
            return cached;
        var texName = _gamez.Materials[materialIndex].TextureName;
        var tex = texName != null ? Resolve(texName) : null;
        Material mat;
        if (tex != null)
        {
            var billboard = BillboardMaterial(tex, blend: true, scissor: false, glow: true, lit: true, fogged: fogged, clampUv: clampUv);
            RegisterCycle(_gamez.Materials[materialIndex], billboard); // same albedo_tex, see GetCylindricalMaterial
            mat = billboard;
        }
        else
        {
            mat = GetMaterial(materialIndex, 0, 0, noClutter: false, doubleSided: true, lit: true, fogged: fogged);
        }
        _glowMaterialCache[key] = mat;
        return mat;
    }

    private Material GetCylindricalMaterial(int materialIndex, CylAxis axis, bool lit, bool fogged, bool clampUv)
    {
        var key = (materialIndex, (int)axis, lit, fogged, clampUv);
        if (_cylindricalMaterialCache.TryGetValue(key, out var cached))
            return cached;
        var texName = _gamez.Materials[materialIndex].TextureName;
        var tex = texName != null ? Resolve(texName) : null;
        Material mat;
        if (tex != null)
        {
            bool blend = _textures.LastHadAlpha && _textures.LastAlphaIsSoft;
            bool scissor = _textures.LastHadAlpha && !blend;
            bool glow = texName != null && _glowTexture != null && _glowTexture(texName);
            var billboard = CylindricalBillboardMaterial(tex, axis, blend, scissor, glow, lit, fogged, clampUv);
            // A billboard shader samples the same albedo_tex, so a flipbook drives it identically,
            // and the fire cycles land HERE rather than on the bias path: fire1/fire2/flame01 are
            // all Facade/CylindricalY meshes (EffectCycles).
            RegisterCycle(_gamez.Materials[materialIndex], billboard);
            mat = billboard;
        }
        else
        {
            mat = GetMaterial(materialIndex, 0, 0, noClutter: false, doubleSided: true, lit: lit, fogged: fogged);
        }
        _cylindricalMaterialCache[key] = mat;
        return mat;
    }

    private Material GetMaterial(int materialIndex, int priority, int rank, bool noClutter, bool doubleSided,
        Vector2 scroll = default, bool clampUv = false, bool lit = true, bool fogged = true, int pass = 0,
        UvClampAxes edgeClamp = UvClampAxes.None, bool clutterFade = false)
    {
        rank = Math.Min(rank, SurfaceRankCap);
        var key = (materialIndex, priority, rank, noClutter, doubleSided, scroll.X, scroll.Y, clampUv, edgeClamp, lit, fogged, pass, clutterFade);
        if (_materialCache.TryGetValue(key, out var cached))
            return cached;
        var mat = BuildMaterial(materialIndex, priority, rank, noClutter, doubleSided, scroll, clampUv, lit, fogged, pass, edgeClamp, clutterFade);
        _materialCache[key] = mat;
        return mat;
    }

    // The archive lookup every material goes through, plus the caller's optional substitution
    // (aircraft paint). ⚠ Find() must still run even when a substitute exists: it is what sets
    // LastHadAlpha/LastAlphaIsSoft, which the blend/scissor choice reads.
    // ⚠ A substitute must keep the original's alpha class (opaque, cutout or soft); the
    // blend/scissor decision is already made from the archive's classification.
    private ImageTexture? Resolve(string texName)
    {
        var tex = _textures.Find(texName);
        // A drop-in colour is the answer the run was launched to get; a composited paint scheme
        // would paint straight over it and the aircraft would be the one thing the census misses.
        if (_textureSubstitute != null && !TextureDropIn.Covers(texName))
        {
            return _textureSubstitute(texName, tex);
        }
        return tex;
    }

    // Registers a built material against its source's flipbook. ⚠ Call this per BUILT material, not
    // per source material: the cache key lets one cycling source produce several ShaderMaterials,
    // and each needs its own frame swaps. Frames resolve now, while the TextureArchive is open.
    private void RegisterCycle(GameZMaterial src, ShaderMaterial mat)
    {
        if (Cycler == null || src.CycleTextures.Count < 2)
            return;
        var frames = new List<ImageTexture>(src.CycleTextures.Count);
        foreach (var name in src.CycleTextures)
        {
            if (Resolve(name) is not { } frame)
                return; // an incomplete flipbook would strobe a hole; leave it static instead
            frames.Add(frame);
        }
        Cycler.Add(mat, frames, src.CycleSpeed, src.CycleLooping, src.TextureName ?? "?");
    }

    private Material BuildMaterial(int materialIndex, int priority, int rank, bool noClutter, bool doubleSided,
        Vector2 scroll, bool clampUv, bool lit, bool fogged, int pass, UvClampAxes edgeClamp = UvClampAxes.None,
        bool clutterFade = false)
    {
        var src = materialIndex >= 0 && materialIndex < _gamez.Materials.Count
            ? _gamez.Materials[materialIndex]
            : null;

        if (src?.TextureName is { } texName)
        {
            var tex = Resolve(texName);
            if (tex == null)
                // Genuine game-data gaps (pir_spinner, barngrill) get a neutral gray, like
                // the original engine; anything else is likely our lookup failing and stays
                // debug-magenta so it's obvious.
                return NewStandard(albedoColor: TextureArchive.IsKnownAbsent(texName)
                    ? new Color(0.5f, 0.5f, 0.5f)
                    : Colors.Magenta);

            // Soft-alpha textures (shadow decals, clouds, prop blur, waterfalls, smoke, detected
            // from the pixels) and the caller's explicit blend list alpha-blend; every other alpha
            // texture scissors to a hard cutout, which is right for fences, trees and railings.
            bool blend = _textures.LastHadAlpha
                && (_textures.LastAlphaIsSoft || (_blendTexture != null && _blendTexture(texName)));
            bool scissor = _textures.LastHadAlpha && !blend;
            // Cloud sprites face the camera and take a billboard material: no depth bias, since a
            // free-floating sprite has nothing coplanar to fight, but the SAME cylindrical fog, so
            // they fade into the fog wall instead of punching through it as crisp white.
            if (_billboardTexture != null && _billboardTexture(texName))
            {
                var billboard = BillboardMaterial(tex, blend, scissor, glow: false, lit: lit, fogged: fogged, clampUv: clampUv);
                RegisterCycle(src, billboard); // same albedo_tex, see GetCylindricalMaterial
                return billboard;
            }
            var textured = BiasMaterial(priority, rank, noClutter, doubleSided, tex, null, blend, scissor, scroll, clampUv, lit, fogged, pass, edgeClamp, clutterFade, ClassifySurface(texName) == "water");
            _texturedMaterials.Add((textured, texName)); // for a live repaint, see Repaint()
            RegisterCycle(src, textured);
            return textured;
        }

        var color = src?.Color ?? Colors.White;
        return BiasMaterial(priority, rank, noClutter, doubleSided, null, color, blend: color.A < 1f, scissor: false,
            scroll: Vector2.Zero, clampUv: false, lit: lit, fogged: fogged, pass: pass, clutterFade: clutterFade);
    }

    private StandardMaterial3D NewStandard(Color? albedoColor = null)
    {
        var mat = new StandardMaterial3D
        {
            // Only used for billboards (cloud sprites) and the missing-texture fallback,
            // which should render from both sides regardless of source sidedness.
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Roughness = 0.85f,
            Metallic = 0.0f,
            VertexColorUseAsAlbedo = true, // baked lighting from the source data
        };
        if (_fullbright)
            mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        if (albedoColor is { } c)
            mat.AlbedoColor = c;
        return mat;
    }

    // Records the verdict a world material's shader was generated for. Every constructor that can
    // make a blended or scissored surface calls it. The depth_draw_never variants live on the
    // billboard shaders, so a census reaching only the bias path would call them opaque.
    private void NoteAlpha(Material mat, bool blend, bool scissor) =>
        _materialAlpha[mat] = blend ? TransparencyClass.BlendSurface
            : scissor ? TransparencyClass.ScissorSurface : TransparencyClass.None;

    // A ShaderMaterial that mirrors NewStandard's look but pulls the geometry toward the
    // eye (or pushes it away, negative priority) by a fraction of its view distance.
    // The per-node draw-order term is added at instance level (see BuildSubtree).
    private ShaderMaterial BiasMaterial(int priority, int rank, bool noClutter, bool doubleSided, ImageTexture? tex,
        Color? color, bool blend, bool scissor, Vector2 scroll, bool clampUv, bool lit, bool fogged, int pass = 0,
        UvClampAxes edgeClamp = UvClampAxes.None, bool clutterFade = false, bool water = false)
    {
        // Only a textured surface can scroll its UVs (a Colored material has no sampler).
        bool scrolls = tex != null && scroll != Vector2.Zero;
        if (tex == null)
            edgeClamp = UvClampAxes.None;
        var mat = new ShaderMaterial
        {
            Shader = GetBiasShader(shaded: !_fullbright, textured: tex != null, blend, scissor, doubleSided,
                scrolls, clampUv && tex != null, lit, fogged, edgeClamp, clutterFade, water),
        };
        NoteAlpha(mat, blend, scissor);
        float bias = Mathf.Clamp(priority * DepthBiasPerLevel, -0.05f, 0.05f) + rank * SurfaceRankBias;
        if (noClutter)
            bias += NoClutterLayerBias;
        // An overlay pass shares its base's priority and no_clutter flag by construction, it is
        // the same polygon, so this term is the whole of what puts it in front (OverlayPassBias).
        bias += pass * OverlayPassBias;
        bias = Mathf.Clamp(bias * DepthBiasScale, -MaxScaledBias, MaxScaledBias);
        mat.SetShaderParameter("depth_bias", bias);
        if (tex != null)
            mat.SetShaderParameter("albedo_tex", tex);
        if (color is { } c)
            mat.SetShaderParameter("albedo_color", c);
        if (scrolls)
            mat.SetShaderParameter("scroll_rate", scroll);
        // Half a texel keeps every bilinear tap inside the texture without cropping any
        // visible content: sampling at exactly the inset returns the pure edge texel.
        if (tex != null && edgeClamp != UvClampAxes.None)
            mat.SetShaderParameter("uv_edge_inset", new Vector2(
                edgeClamp.HasFlag(UvClampAxes.U) ? 0.5f / tex.GetWidth() : 0f,
                edgeClamp.HasFlag(UvClampAxes.V) ? 0.5f / tex.GetHeight() : 0f));
        return mat;
    }

    // `lit` / `fogged` are the model's own authored render flags. They select shader VARIANTS
    // rather than driving a uniform on purpose: a lit, fogged surface then emits byte-for-byte
    // the shader text it always did, so honouring the flags cannot perturb the overwhelming
    // majority of the world through float rounding in a mix().
    // Key bits: 1-64 the flags above, 128 !lit, 256 !fogged, 512/1024 edgeClamp, 2048 clutterFade,
    // 4096 DebugClutterFlag, 8192 enhanced, 16384 water, 32768 sun, 65536 gamma blend; free 131072.
    private Shader GetBiasShader(bool shaded, bool textured, bool blend, bool scissor, bool doubleSided,
        bool scroll, bool clampUv, bool lit, bool fogged, UvClampAxes edgeClamp = UvClampAxes.None,
        bool clutterFade = false, bool water = false)
    {
        // Enhanced mode only: a world surface authored `lighting: true` shades under the real scene
        // lights off its decoded normals. `lighting: false` is self-lit by intent and keeps the
        // fullbright arm, so `fullbright` rather than `!shaded` selects the terms that arm owns.
        bool worldLit = GraphicsMode.Enhanced && !shaded && lit;
        bool fullbright = !shaded && !worldLit;
        // ⚠ The water arm exists only inside the lit world arm, so original mode never sets its key
        // bit and its shader text cannot move.
        bool waterLit = worldLit && water;
        // The original's own aircraft light: unshaded, the per-vertex sun term times the authored
        // colour, clamped, then the texel (docs/org/vertexLighting.md). No Godot light reaches it.
        bool sunLit = shaded && _sunVertexLit && !GraphicsMode.Enhanced;
        // Only a blending surface has an alpha to correct; a scissor compares against a fixed 0.5
        // and moving its alpha would move the cutout silhouette instead of the composite.
        bool gammaBlend = blend && GammaBlendAlpha;
        int key = (shaded ? 1 : 0) | (textured ? 2 : 0) | (blend ? 4 : 0) | (scissor ? 8 : 0) | (doubleSided ? 16 : 0)
            | (scroll ? 32 : 0) | (clampUv ? 64 : 0) | (lit ? 0 : 128) | (fogged ? 0 : 256)
            | ((int)edgeClamp << 9) | (clutterFade ? 2048 : 0) | (DebugClutterFlag ? 4096 : 0)
            | (GraphicsMode.Enhanced ? 8192 : 0) | (waterLit ? 16384 : 0) | (sunLit ? 32768 : 0)
            | (gammaBlend ? 65536 : 0);
        if (BiasShaders.TryGetValue(key, out var cached))
            return cached;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("shader_type spatial;");
        // Sidedness: the source's visible side is the CCW loop, which is Godot's BACK face, so
        // single-sided source polygons take cull_front (docs/formats/gotchas.md).
        sb.Append(doubleSided
            ? "render_mode skip_vertex_transform, cull_disabled"
            : "render_mode skip_vertex_transform, cull_front");
        if (fullbright || sunLit)
            sb.Append(", unshaded");
        sb.AppendLine(";");
        sb.AppendLine("uniform float depth_bias = 0.0;");
        // The shared ordered instance-uniform block; this shader always carries instance uniforms,
        // so it always takes the full preamble, see the contract in the .gdshaderinc. `csky_fog_on`
        // is a per-instance runtime fog opt-out that nothing sets to 0 today.
        sb.AppendLine(InstanceUniformsInclude);
        // Distance fog + the per-mission SUNLIGHT dimming.
        sb.AppendLine(AtmosphereInclude);
        // The clutter variant only: the authored far fade off the MultiMesh custom data. A discard
        // in the fragment stage is what keeps this out of every world shader.
        if (clutterFade)
        {
            sb.AppendLine(ClutterFadeInclude);
            sb.AppendLine("varying flat float v_clutter_alpha;");
        }
        // The point-light term reaches only the two original-mode arms that evaluate the original's
        // vertex light, and only on a model authored `lighting: true`.
        bool pointLit = lit && (fullbright || sunLit);
        if (pointLit)
            sb.AppendLine(LightsInclude);
        if (textured)
        {
            // Anisotropic mipmap filtering: the world is viewed at grazing angles from the air,
            // where isotropic selection blurs the ground to mush, and no higher-resolution archive
            // exists to fix it with (docs/formats/README.md on the rtexture tiers).
            sb.AppendLine("uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, "
                + (clampUv ? "repeat_disable;" : "repeat_enable;"));
            // The chapter's own mip LOD bias, one global because the original's is one device
            // render state a chapter sets once (docs/org/textures.md).
            sb.AppendLine(MipBiasInclude);
        }
        // UV animation (the model's texture_scroll / the boot script's Object3DSetScroll). Emitted
        // only for surfaces that actually scroll, so every other shader's text is unchanged.
        // ⚠ The phase must come from csky_time, not Godot's TIME, or the scroll ignores the clock.
        if (scroll)
        {
            sb.AppendLine(TimeInclude);
            sb.AppendLine("uniform vec2 scroll_rate = vec2(0.0);");
        }
        // Single-axis edge clamp (see UvAxesWithinUnitSquare): the sampler stays repeat_enable
        // for the tiling axis, so the fitting axis is clamped on the coordinate instead,
        // inset by half a texel so no bilinear tap can cross the edge and wrap.
        if (textured && edgeClamp != UvClampAxes.None)
            sb.AppendLine("uniform vec2 uv_edge_inset = vec2(0.0);");
        if (!textured)
            sb.AppendLine("uniform vec4 albedo_color : source_color = vec4(1.0);");
        // Both world arms: the original's DX7 pipeline multiplied texture × baked vertex colour in
        // GAMMA space, so linearising the vertex colour first reproduces that product
        // (docs/formats/gotchas.md). A linear multiply washes out every baked-dark corner.
        if (!shaded || sunLit || gammaBlend)
            sb.AppendLine(SrgbInclude);
        if (sunLit)
        {
            sb.AppendLine(SunVertexLightDecl);
            sb.AppendLine("varying vec3 v_sun_lit;");
            // Where this frame's origin sits in the world, for a model drawn in a frame of its own
            // (the cockpit pass, CockpitOverlay). Zero for anything drawn in the world itself.
            if (lit)
                sb.AppendLine("uniform vec3 light_origin = vec3(0.0);");
        }
        if (fullbright && lit)
            sb.AppendLine("varying vec3 v_point_gain;");
        // ⚠ Keep NORMAL pre-negated on every lit path. Every visible fragment is back-facing under
        // cull_front and Godot negates NORMAL there, so without this every upward-facing surface
        // shades as though lit from underneath (docs/formats/gotchas.md).
        string normalSign = shaded || worldLit ? "-" : "";
        // Past its far fade an instance collapses to its origin and costs no fragments; inside the
        // ramp the fragment stage dithers it out.
        string clutterVertex = clutterFade
            ? "    v_clutter_alpha = csky_clutter_fade_alpha(MODEL_MATRIX[3].xyz, CAMERA_POSITION_WORLD, INSTANCE_CUSTOM);\n"
              + "    VERTEX *= step(0.004, v_clutter_alpha);\n"
            : "";
        // Per vertex in world space: the sun and the point lights, summed into one factor on the
        // authored colour and clamped at white. The sun's ambient half is the photograph's fill at
        // an armed eye (PhotoEyeParam); `lighting: false` takes the authored colour unchanged.
        string sunVertex = !sunLit ? ""
            : lit ? "    vec3 sun_ambient = csky_photo_eye.w > 0.5 && distance(CAMERA_POSITION_WORLD, csky_photo_eye.xyz) < "
                    + PhotoEyeReach.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + "\n"
                    + "        ? csky_sun_fill_rgb : csky_sun_ambient_rgb;\n"
                    + "    v_sun_lit = clamp(COLOR.rgb * (sun_ambient + csky_sun_diffuse_rgb\n"
                    + "        * max(dot(normalize(MODEL_NORMAL_MATRIX * NORMAL), csky_sun_dir), 0.0)\n"
                    + "        + csky_point_light((MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz + light_origin)), 0.0, 1.0);\n"
            : "    v_sun_lit = COLOR.rgb;\n";
        // The fullbright world keeps its collapsed sun, csky_world_light. It takes the point lights
        // as the linear gain they make on the clamped product, per vertex as the original evaluates
        // them. Zero where no light reaches, so an unlit frame is untouched.
        string pointVertex = !(fullbright && lit) ? ""
            : "    v_point_gain = vec3(0.0);\n"
              + "    if (csky_light_count > 0) {\n"
              + "        vec3 point = csky_point_light((MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz);\n"
              + "        v_point_gain = csky_srgb_to_linear(clamp(COLOR.rgb * (csky_world_light + point), 0.0, 1.0))\n"
              + "            - csky_srgb_to_linear(COLOR.rgb * csky_world_light);\n"
              + "    }\n";
        sb.AppendLine($@"
void vertex() {{
{clutterVertex}{sunVertex}{pointVertex}    VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
    NORMAL = {normalSign}normalize(MODELVIEW_NORMAL_MATRIX * NORMAL);
    // Scale toward the eye (the view-space origin): identical projected position,
    // depth nudged nearer by bias × distance, a scale-invariant polygon offset.
    VERTEX *= 1.0 - (depth_bias + node_bias);
}}

void fragment() {{");
        if (clutterFade)
            sb.AppendLine("    if (!csky_clutter_dither_keep(FRAGCOORD.xy, v_clutter_alpha)) { discard; }");
        // Shaded (planes) keeps raw COLOR for the real-lighting path; fullbright (world) applies
        // the gamma-space vertex modulate (see SrgbToLinearFn above).
        string vcol = sunLit ? "vec4(csky_srgb_to_linear(v_sun_lit), COLOR.a)"
            : shaded ? "COLOR" : "vec4(csky_srgb_to_linear(COLOR.rgb), COLOR.a)";
        // The surface's own albedo is kept separately from the modulated result: a point light's
        // term is light falling ON the surface, so it must be modulated by the same albedo
        // rather than added to the final colour (an unlit black texture stays black under a lamp).
        if (textured && edgeClamp != UvClampAxes.None)
        {
            sb.AppendLine(scroll ? "    vec2 suv = UV + scroll_rate * csky_time;"
                : "    vec2 suv = UV;");
            if (edgeClamp.HasFlag(UvClampAxes.U))
                sb.AppendLine("    suv.x = clamp(suv.x, uv_edge_inset.x, 1.0 - uv_edge_inset.x);");
            if (edgeClamp.HasFlag(UvClampAxes.V))
                sb.AppendLine("    suv.y = clamp(suv.y, uv_edge_inset.y, 1.0 - uv_edge_inset.y);");
            sb.AppendLine($"    vec4 base_col = {SampleAlbedo("suv")};");
        }
        else
        {
            sb.AppendLine(!textured ? "    vec4 base_col = albedo_color;"
                : scroll ? $"    vec4 base_col = {SampleAlbedo("UV + scroll_rate * csky_time")};"
                : $"    vec4 base_col = {SampleAlbedo("UV")};");
        }
        sb.AppendLine($"    vec4 col = {vcol} * base_col;");
        sb.AppendLine("    ALBEDO = col.rgb;");
        // Per-mission SUNLIGHT dimming (world/deck/clutter), skipped for a model authored
        // `lighting: false`. Neither lit arm applies it: a real sun carries that energy there, and
        // a scalar on ALBEDO would dim the surface a second time.
        if (fullbright && lit)
            sb.AppendLine("    ALBEDO *= csky_world_light;");
        if (shaded && !sunLit)
        {
            sb.AppendLine("    ROUGHNESS = 0.85;");
            sb.AppendLine("    METALLIC = 0.0;");
            sb.AppendLine($"    SPECULAR = {AircraftSpecularLiteral};");
        }
        else if (waterLit)
        {
            // Glossy, so screen-space reflection has a surface to march against (WaterRoughness).
            sb.AppendLine($"    ROUGHNESS = {WaterRoughnessLiteral};");
            sb.AppendLine("    METALLIC = 0.0;");
            sb.AppendLine($"    SPECULAR = {WaterSpecularLiteral};");
        }
        else if (worldLit)
        {
            // Matte: the source authors no gloss for terrain or building walls, so any specular
            // sheen here is invented, and it reads as wet plastic as the sun swings past.
            sb.AppendLine("    ROUGHNESS = 1.0;");
            sb.AppendLine("    METALLIC = 0.0;");
            sb.AppendLine("    SPECULAR = 0.0;");
        }
        // Distance fog, cylindrical: VERTEX is the view-space position here under
        // skip_vertex_transform, and INV_VIEW_MATRIX lifts it back to world.
        if (fogged)
            sb.AppendLine("    vec3 fog_world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;");
        // Point lights go in before the fog mix, on the same texel as the vertex colour they scale.
        if (fullbright && lit)
            sb.AppendLine("    ALBEDO += base_col.rgb * v_point_gain;");
        // A model authored `fog: false` is exempt from distance fog entirely; the instance-level
        // csky_fog_on stays the per-instance opt-out beside it (see the uniform block).
        if (fogged)
        {
            sb.AppendLine("    float fog_amt = csky_fog_amount(fog_world, CAMERA_POSITION_WORLD);");
            // ⚠ The lit arms fog through FOG, never ALBEDO: Godot lights whatever ALBEDO holds, so
            // a fogged albedo still varies with its normal at full fog. csky_fog_color is linear,
            // the space FOG.rgb resolves in and the one the fullbright arm's mix lands in.
            sb.AppendLine(worldLit
                ? "    FOG = vec4(csky_fog_color, csky_fog_on * fog_amt);"
                : "    ALBEDO = mix(ALBEDO, csky_fog_color, csky_fog_on * fog_amt);");
        }
        // The debug overlays' per-instance tint, a no-op at alpha 0. Under --debug-clutterflag the
        // fullbright world shows the flag colour EmitTriangle wrote into COLOR instead of the lit,
        // fogged texture, which C5's night art would otherwise swallow whole.
        sb.AppendLine(DebugClutterFlag && !shaded ? ClutterFlagTintLine : TintLine);
        if (blend || scissor)
            sb.AppendLine(gammaBlend
                ? $"    ALPHA = csky_srgb_to_linear(vec3(col.a)).r{OpacityTerm};"
                : $"    ALPHA = col.a{OpacityTerm};");
        if (scissor)
            sb.AppendLine("    ALPHA_SCISSOR_THRESHOLD = 0.5;");
        sb.AppendLine("}");

        var shader = new Shader { Code = sb.ToString() };
        BiasShaders[key] = shader;
        return shader;
    }

    // A camera-facing billboard material for the cloud sprites (cloud1/cloud2, flat 2D cards
    // in the source). Unlike the bias shader it does no depth bias (free-floating sprites have
    // nothing coplanar to fight) but carries the identical cylindrical distance-fog term, so
    // distant sprites fade into the fog wall in step with the terrain they float over. blend /
    // scissor follow the alpha classification (cloud1/cloud2 are soft-alpha ⇒ blend).
    private ShaderMaterial BillboardMaterial(ImageTexture tex, bool blend, bool scissor, bool glow, bool lit, bool fogged,
        bool clampUv)
    {
        var mat = new ShaderMaterial { Shader = GetBillboardShader(blend, scissor, glow, lit, fogged, clampUv) };
        mat.SetShaderParameter("albedo_tex", tex);
        NoteAlpha(mat, blend, scissor);
        return mat;
    }

    // Key bits taken: 1 blend, 2 scissor, 4 glow, 8 !lit, 16 !fogged, 32 clampUv, 64 enhanced
    // mode. Next free bit is 128.
    private Shader GetBillboardShader(bool blend, bool scissor, bool glow, bool lit, bool fogged, bool clampUv)
    {
        // A glow variant already ignores csky_world_light, so `lit` cannot split its key.
        lit |= glow;
        int key = (blend ? 1 : 0) | (scissor ? 2 : 0) | (glow ? 4 : 0) | (lit ? 0 : 8) | (fogged ? 0 : 16)
            | (clampUv ? 32 : 0) | (GraphicsMode.Enhanced ? 64 : 0);
        if (BillboardShaders.TryGetValue(key, out var cached))
            return cached;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("shader_type spatial;");
        // Fullbright like the rest of the world, double-sided, no shadows. fog_disabled turns
        // OFF Godot's built-in fog (we roll our own csky_fog_* below); blend clouds render in
        // the transparent pass writing no depth, scissor/opaque render normally.
        sb.Append("render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled");
        if (blend)
            sb.Append(", blend_mix, depth_draw_never");
        sb.AppendLine(";");
        // A sprite whose UVs never leave the unit square never needs the sampler to wrap, and
        // wrapping it bleeds the texture's opposite edge in at the UV border, the same
        // hairline artifact UvsWithinUnitSquare exists for.
        sb.AppendLine("uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, "
            + (clampUv ? "repeat_disable;" : "repeat_enable;"));
        // The same chapter mip bias the world mesh takes. The original's is one device render
        // state, so a billboard samples at the level the terrain under it does.
        sb.AppendLine(MipBiasInclude);
        // Same global distance-fog params as the world shader. Clouds always fog, so this shader
        // never reads csky_fog_on, and an OPAQUE cloud sprite therefore declares no instance
        // uniform at all, deliberately keeping it off the instance-uniform buffer.
        sb.AppendLine(AtmosphereInclude);
        // The alpha-writing variants already carry csky_opacity, i.e. they are on that buffer
        // regardless, so they take the shared ordered preamble and agree on indices with every
        // other shader, at no additional cost.
        if (blend || scissor)
            sb.AppendLine(InstanceUniformsInclude);
        sb.AppendLine(SrgbInclude); // DX7 gamma-space vertex modulate (world/cloud pass)
        sb.AppendLine(FacadeInclude);
        sb.AppendLine($@"
void vertex() {{
    // The SphericalY facade pose, keeping the instance scale (Godot's billboard_keep_scale, by
    // hand, the bias shader can't billboard, like FogVolumeClutter). The mesh was recentered on its
    // quad centre and the instance placed there, so the quad pivots at its centre.
    mat3 face = csky_facade_spherical(MODEL_MATRIX[3].xyz, CAMERA_POSITION_WORLD);
    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
        vec4(face[0], 0.0), vec4(face[1], 0.0), vec4(face[2], 0.0), MODEL_MATRIX[3]);
    MODELVIEW_MATRIX[0] *= length(MODEL_MATRIX[0].xyz);
    MODELVIEW_MATRIX[1] *= length(MODEL_MATRIX[1].xyz);
    MODELVIEW_MATRIX[2] *= length(MODEL_MATRIX[2].xyz);
}}

void fragment() {{
    vec4 col = vec4(csky_srgb_to_linear(COLOR.rgb), COLOR.a) * {SampleAlbedo("UV")};");
        // Cylindrical distance fog, identical to the world shader: VERTEX is the view-space
        // position in fragment; INV_VIEW_MATRIX lifts it back to world for the horizontal camera
        // distance + the fragment-altitude fade. A model authored `fog: false` skips it.
        if (fogged)
            sb.AppendLine(@"    vec3 fog_world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
    float fog_amt = csky_fog_amount(fog_world, CAMERA_POSITION_WORLD);");
        // Glow flares are light sources: no SUNLIGHT night dimming (a lamp doesn't get
        // darker at night, it's what lights the scene), and neither is a model the artists
        // authored `lighting: false`. Clouds ride the world brightness.
        string lightTerm = glow || !lit ? "col.rgb" : "col.rgb * csky_world_light";
        if (glow && GraphicsMode.Enhanced)
            lightTerm = $"col.rgb * {EmissiveLiteral}";
        sb.AppendLine(fogged
            ? $"    ALBEDO = mix({lightTerm}, csky_fog_color, fog_amt);"
            : $"    ALBEDO = {lightTerm};");
        // Only the variants that took the preamble above have csky_tint declared at all.
        if (blend || scissor)
        {
            sb.AppendLine(TintLine);
            sb.AppendLine($"    ALPHA = col.a{OpacityTerm};");
        }
        if (scissor)
            sb.AppendLine("    ALPHA_SCISSOR_THRESHOLD = 0.5;");
        sb.AppendLine("}");

        var shader = new Shader { Code = sb.ToString() };
        BillboardShaders[key] = shader;
        return shader;
    }

    // A single-axis (Y or X) cylindrical billboard: the mesh spins about that one fixed world axis
    // to face the camera on the other two, so trees, lampposts, flames and cables stay upright
    // instead of tipping toward it like a light-source glow. Same technique as Clutter's tree
    // billboard, kept as its own generator because the two already differ in fog, dimming and
    // instancing (Clutter is a MultiMesh with no per-mesh pivot cache).
    private ShaderMaterial CylindricalBillboardMaterial(ImageTexture tex, CylAxis axis, bool blend, bool scissor,
        bool glow, bool lit, bool fogged, bool clampUv)
    {
        var mat = new ShaderMaterial { Shader = GetCylindricalShader(axis, blend, scissor, glow, lit, fogged, clampUv) };
        mat.SetShaderParameter("albedo_tex", tex);
        NoteAlpha(mat, blend, scissor);
        return mat;
    }

    // Key bits taken: 1 axis, 2 blend, 4 scissor, 8 glow, 16 !lit, 32 !fogged, 64 clampUv,
    // 128 enhanced mode. Next free bit is 256.
    private Shader GetCylindricalShader(CylAxis axis, bool blend, bool scissor, bool glow, bool lit, bool fogged,
        bool clampUv)
    {
        lit |= glow; // a glow variant already ignores csky_world_light, same key
        int key = (axis == CylAxis.X ? 1 : 0) | (blend ? 2 : 0) | (scissor ? 4 : 0) | (glow ? 8 : 0)
            | (lit ? 0 : 16) | (fogged ? 0 : 32) | (clampUv ? 64 : 0) | (GraphicsMode.Enhanced ? 128 : 0);
        if (CylindricalShaders.TryGetValue(key, out var cached))
            return cached;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("shader_type spatial;");
        sb.Append("render_mode skip_vertex_transform, unshaded, cull_disabled, shadows_disabled");
        if (blend)
            sb.Append(", blend_mix, depth_draw_never");
        sb.AppendLine(";");
        // Clamp when the facade's UVs never leave the unit square, see GetBillboardShader.
        sb.AppendLine("uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, "
            + (clampUv ? "repeat_disable;" : "repeat_enable;"));
        // The chapter mip bias, as in GetBillboardShader. A facade stands in the same street as
        // the walls beside it, and must choose its level the same way.
        sb.AppendLine(MipBiasInclude);
        // csky_world_light arrives with the atmosphere include and is READ only when !glow
        // (a light source does not dim with the mission's SUNLIGHT); declaring it either way
        // costs nothing, since a global uniform is project-wide rather than per-instance.
        sb.AppendLine(AtmosphereInclude);
        // Only the alpha-writing variants carry an instance uniform at all, so only they take
        // the preamble, an opaque facade stays off the instance-uniform buffer entirely.
        if (blend || scissor)
            sb.AppendLine(InstanceUniformsInclude);
        sb.AppendLine(SrgbInclude);
        // Fixed axis + the plane the camera direction is measured in: Y keeps world-up fixed and
        // spins in XZ (Clutter's tree technique); X keeps local-right fixed and spins in YZ, for a
        // horizontal pipe's flame or a muzzle flash facing the camera around its barrel axis.
        string toCam = axis == CylAxis.X
            ? "CAMERA_POSITION_WORLD.yz - origin.yz" : "CAMERA_POSITION_WORLD.xz - origin.xz";
        string spinRows = axis == CylAxis.X
            ? "vec3(1.0, 0.0, 0.0), vec3(0.0, dir.y, -dir.x), vec3(0.0, dir.x, dir.y)"
            : "vec3(dir.y, 0.0, -dir.x), vec3(0.0, 1.0, 0.0), vec3(dir.x, 0.0, dir.y)";
        sb.AppendLine($@"
void vertex() {{
    vec3 origin = MODEL_MATRIX[3].xyz;
    vec2 to_cam = {toCam};
    float len = length(to_cam);
    vec2 dir = len > 1e-4 ? to_cam / len : vec2(0.0, 1.0);
    mat3 spin = mat3({spinRows});
    VERTEX = (VIEW_MATRIX * vec4(origin + spin * VERTEX, 1.0)).xyz;
}}

void fragment() {{
    vec4 col = vec4(csky_srgb_to_linear(COLOR.rgb), COLOR.a) * {SampleAlbedo("UV")};");
        if (fogged)
            sb.AppendLine(@"    vec3 fog_world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
    float fog_amt = csky_fog_amount(fog_world, CAMERA_POSITION_WORLD);");
        string cylLight = glow || !lit ? "col.rgb" : "col.rgb * csky_world_light";
        if (glow && GraphicsMode.Enhanced)
            cylLight = $"col.rgb * {EmissiveLiteral}";
        sb.AppendLine(fogged
            ? $"    ALBEDO = mix({cylLight}, csky_fog_color, fog_amt);"
            : $"    ALBEDO = {cylLight};");
        // As in GetBillboardShader: csky_tint exists only where the preamble was taken.
        if (blend || scissor)
        {
            sb.AppendLine(TintLine);
            sb.AppendLine($"    ALPHA = col.a{OpacityTerm};");
        }
        if (scissor)
            sb.AppendLine("    ALPHA_SCISSOR_THRESHOLD = 0.5;");
        sb.AppendLine("}");

        var shader = new Shader { Code = sb.ToString() };
        CylindricalShaders[key] = shader;
        return shader;
    }
}
