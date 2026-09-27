using System.Collections.Generic;
using CSVM.Utils;
using Godot;

namespace CSVM.Effects;

/// <summary>The GPU half of a <see cref="Puffer"/>: live particles in, draw calls out. The seam
/// exists so the emitter's three modes are reachable by a test, which a real <see cref="MultiMesh"/>
/// is not. The atlas is built above this seam, in <c>Puffer.Create</c>, and passed to the renderer's
/// constructor, so a <c>Puffer</c> needs no <c>TextureArchive</c> below it.
/// Per frame: <see cref="Write"/> for indices <c>0 … liveCount-1</c>, packed and ascending, then one
/// <see cref="Show"/>. Slots past <c>liveCount</c> keep their last value and simply aren't
/// drawn.</summary>
public interface IEmitterRenderer
{
    /// <summary>Sizes the draw pool at <paramref name="capacity"/> particles and attaches whatever
    /// draws under <paramref name="owner"/>. Called once, from the emitter's own init.
    /// <paramref name="cullMargin"/> pads the frustum test for billboarding, which moves vertices
    /// off the pool's computed AABB.</summary>
    void Attach(Node3D owner, int capacity, float cullMargin);

    /// <summary>Re-sizes the draw pool to <paramref name="capacity"/> slots, keeping nothing: the
    /// emitter rewrites every live slot before the next <see cref="Show"/>. A continuous emitter
    /// calls this when its authored emission outgrows the pool it was sized with.</summary>
    void Grow(int capacity);

    /// <summary>One live particle's state this frame: world <paramref name="position"/>, uniform
    /// <paramref name="size"/>, atlas column <paramref name="frame"/>, <paramref name="alpha"/>
    /// envelope and ramp <paramref name="color"/>.</summary>
    void Write(int index, Vector3 position, float size, float frame, float alpha, Color color);

    /// <summary>Publishes the frame: the first <paramref name="liveCount"/> written slots draw.</summary>
    void Show(int liveCount);
}

/// <summary>
/// The real renderer: billboarded quads in a <see cref="MultiMesh"/> (one draw call), whose shared
/// shader billboards each instance toward the camera, picks its flipbook column from per-instance
/// custom data, and composites additively or mixed.
/// ⚠ Blend belongs to the TEXTURE, not to the emitter, so this type takes one verdict per atlas
/// column rather than one per emitter. A column set that disagrees draws as two MultiMeshes, one
/// per blend, and each particle goes to the one its current column names. The verdict itself is
/// read off the texture header in <c>Puffer.Create</c>, which is what keeps this seam free of
/// <c>TextureArchive</c>.
/// </summary>
public sealed class MultiMeshEmitterRenderer : IEmitterRenderer
{
    private const string ShaderCode = """
        shader_type spatial;
        render_mode BLEND_MODE, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
        #include "res://shaders/csky_srgb.gdshaderinc"
        #include "res://shaders/csky_atmosphere.gdshaderinc"

        uniform sampler2D atlas : source_color, filter_linear, repeat_disable;
        uniform float frame_count = 1.0;
        uniform sampler2D depth_texture : hint_depth_texture, filter_nearest;
        SCREEN_UNIFORM

        varying flat float v_frame;
        varying flat float v_alpha;
        varying flat vec4 v_color;

        void vertex() {
            // Billboard the instance quad toward the camera, keeping its per-instance scale
            // (Godot's billboard_keep_scale, done by hand because this is a MultiMesh).
            MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
                INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
            MODELVIEW_MATRIX[0] *= length(MODEL_MATRIX[0].xyz);
            MODELVIEW_MATRIX[1] *= length(MODEL_MATRIX[1].xyz);
            MODELVIEW_MATRIX[2] *= length(MODEL_MATRIX[2].xyz);
            v_frame = INSTANCE_CUSTOM.x;
            v_alpha = INSTANCE_CUSTOM.y;
            v_color = COLOR;
        }

        void fragment() {
            float col = floor(v_frame + 0.5);
            vec2 uv = vec2((UV.x + col) / frame_count, UV.y);
            vec4 t = texture(atlas, uv);
            // fade to nothing at the quad rim: some source frames leak bright pixels
            // to their border (fire_f01 edge maxes at 247), which otherwise paints
            // faint additive rectangles over dark ground on large grown quads
            vec2 rim = smoothstep(vec2(0.0), vec2(0.12), UV)
                     * smoothstep(vec2(0.0), vec2(0.12), vec2(1.0) - UV);
            // Soft particles: fade alpha over the last ~1.5 m before the scene depth, so a tilted
            // billboard doesn't hard-cut into the terrain. Disabled for the smokeball, which sits on
            // the ground and would otherwise fade every fresh puff to invisible.
            float scene_raw = texture(depth_texture, SCREEN_UV).r;
            vec4 unproj = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, scene_raw, 1.0);
            float scene_z = unproj.z / unproj.w;
            float soft = SOFT_EXPR;
            // The COLORS ramp is authored in DX7 framebuffer bytes, the same gamma-space
            // modulate every fullbright pass linearises (csky_srgb.gdshaderinc); multiplied in
            // raw it draws two shades too pale. White, the ramp-less case, is a fixed point.
            ALBEDO = t.rgb * csky_srgb_to_linear(v_color.rgb);
            // The mission's own distance fog, the cylinder every world surface takes. FOG_TARGET is
            // what this blend leaves at full fog: the sky's colour where the quad replaces the
            // background, nothing where it only adds to one already fogged (org/puffer.md).
            vec3 fog_world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
            ALBEDO = mix(ALBEDO, FOG_TARGET, csky_fog_amount(fog_world, CAMERA_POSITION_WORLD));
            ALPHA = t.a * v_alpha * v_color.a * rim.x * rim.y * soft;
            COMPOSITE
        }
        """;

    // The original's DX7 device mixes framebuffer bytes. This alpha makes Godot's linear mix land
    // on that byte over the opaque background. ⚠ Do not drop it for the raw alpha: a linear-space
    // mix draws a near-black plume at about half the original's darkening. Keep it a convex mix:
    // an additive form blows a stack of light sprites out to white. See docs/org/puffer.md.
    private const string GammaMixComposite = """
            vec3 src_g = csky_linear_to_srgb(clamp(ALBEDO, 0.0, 1.0));
            vec3 dst_l = clamp(texture(screen_texture, SCREEN_UV).rgb, 0.0, 1.0);
            vec3 dst_g = csky_linear_to_srgb(dst_l);
            vec3 out_g = src_g * ALPHA + dst_g * (1.0 - ALPHA);
            vec3 src_l = csky_srgb_to_linear(src_g);
            vec3 luma = vec3(0.2126, 0.7152, 0.0722);
            float span = dot(src_l - dst_l, luma);
            float moved = dot(csky_srgb_to_linear(out_g) - dst_l, luma);
            ALBEDO = src_l;
            ALPHA = abs(span) > 1e-4 ? clamp(moved / span, 0.0, 1.0) : ALPHA;
""";

    // One compiled Shader per code variant (blend × soft), shared by every renderer. A Shader per
    // emitter cost about 6 ms to compile, paid by every live miss and by each of the couple of
    // hundred emitters a crash rig builds ahead.
    private static readonly Dictionary<(bool Mix, bool Soft), Shader> ShaderVariants = new();

    // One quad for every layer in the process: the mesh is a unit billboard and the per-instance
    // scale lives in the MultiMesh transforms, so nothing about it is per emitter. The material
    // stays per emitter, since it carries that emitter's atlas and would hold it alive past the
    // texture archive it was baked from.
    private static readonly QuadMesh UnitQuad = new() { Size = Vector2.One };

    private readonly ImageTexture _atlas;
    private readonly int _frameCount;
    // One verdict per atlas column, so a flipbook that crosses from a flagged frame to an
    // unflagged one changes blend mid-life exactly as the original does.
    private readonly bool[] _additiveFrames;
    private readonly bool _softParticles;

    // The mixed list and the additive one. Only the blends the column set actually uses are built,
    // so the common uniform case stays at one MultiMesh and one draw call.
    private Layer? _mix;
    private Layer? _add;

    /// <summary>Builds the renderer for an already-packed <paramref name="atlas"/> of
    /// <paramref name="frameCount"/> side-by-side frames. <paramref name="additiveFrames"/> is one
    /// flag per atlas column, true where the texture's render-flags word carries the additive bit
    /// (<c>docs/org/textures.md</c>); <paramref name="softParticles"/> enables the depth fade.</summary>
    public MultiMeshEmitterRenderer(ImageTexture atlas, int frameCount,
        IReadOnlyList<bool> additiveFrames, bool softParticles)
    {
        _atlas = atlas;
        _frameCount = frameCount;
        _additiveFrames = new bool[Mathf.Max(1, frameCount)];
        for (int i = 0; i < _additiveFrames.Length && i < additiveFrames.Count; i++)
            _additiveFrames[i] = additiveFrames[i];
        _softParticles = softParticles;
    }

    /// <summary>True when the column set spans both blends, so this emitter draws two MultiMeshes
    /// instead of one. Readable so a suite can assert the split without reading pixels.</summary>
    public bool Split => AnyAdditive && !AllAdditive;

    /// <summary>Diagnostics: how many particles the last published frame put in each blend's draw
    /// list, which is how a suite asserts the per-frame routing without reading pixels.</summary>
    public (int Mixed, int Additive) DrawnCounts => (_mix?.Drawn ?? 0, _add?.Drawn ?? 0);

    private bool AnyAdditive
    {
        get
        {
            foreach (bool a in _additiveFrames)
                if (a)
                    return true;
            return false;
        }
    }

    private bool AllAdditive
    {
        get
        {
            foreach (bool a in _additiveFrames)
                if (!a)
                    return false;
            return true;
        }
    }

    public void Attach(Node3D owner, int capacity, float cullMargin)
    {
        // Runtime shader/material build, not load-time, a new Puffer's first draw. Usually
        // absorbed into EmitterDirector's EffectPoolMiss scope; fires alone only when nothing else
        // is already open.
        using var _ = PerfSample.Scope(PerfSite.MaterialCreate);
        if (!AllAdditive)
            _mix = Layer.Build(this, owner, capacity, cullMargin, additive: false);
        if (AnyAdditive)
            _add = Layer.Build(this, owner, capacity, cullMargin, additive: true);
    }

    public void Grow(int capacity)
    {
        _mix?.Grow(capacity);
        _add?.Grow(capacity);
    }

    public void Write(int index, Vector3 position, float size, float frame, float alpha, Color color)
    {
        // The shader picks its column with the same rounding, so a particle lands in the list that
        // draws the frame it is actually showing.
        int col = Mathf.Clamp(Mathf.FloorToInt(frame + 0.5f), 0, _additiveFrames.Length - 1);
        var layer = _additiveFrames[col] ? _add : _mix;
        layer?.Write(position, size, frame, alpha, color);
    }

    public void Show(int liveCount)
    {
        _mix?.Publish();
        _add?.Publish();
    }

    private static Shader ShaderFor(bool mix, bool soft)
    {
        if (!ShaderVariants.TryGetValue((mix, soft), out var shader))
        {
            // A mixed quad stands in for the background, so full fog leaves the sky's fog colour,
            // the world's own answer. An additive one only brightens a background that already
            // carries that colour, so full fog leaves nothing to add.
            var code = ShaderCode
                .Replace("BLEND_MODE", mix ? "blend_mix" : "blend_add")
                .Replace("SCREEN_UNIFORM", mix
                    ? "uniform sampler2D screen_texture : hint_screen_texture, filter_nearest;" : "")
                .Replace("COMPOSITE", mix ? GammaMixComposite : "")
                .Replace("FOG_TARGET", mix ? "csky_fog_color" : "vec3(0.0)")
                .Replace("SOFT_EXPR", soft ? "clamp((VERTEX.z - scene_z) / 1.5, 0.0, 1.0)" : "1.0");
            ShaderVariants[(mix, soft)] = shader = new Shader { Code = code };
        }
        return shader;
    }

    // One blend's draw list. The write cursor counts up across a frame's writes and is published
    // and reset by Show, so neither half has to know the emitter's own packed indices.
    private sealed class Layer
    {
        private MultiMeshInstance3D _mmi = null!;
        private MultiMesh _mm = null!;
        private int _written;

        public int Drawn { get; private set; }

        public static Layer Build(MultiMeshEmitterRenderer owner, Node3D parent, int capacity,
            float cullMargin, bool additive)
        {
            var mat = new ShaderMaterial { Shader = ShaderFor(!additive, owner._softParticles) };
            mat.SetShaderParameter("atlas", owner._atlas);
            mat.SetShaderParameter("frame_count", (float)owner._frameCount);
            var layer = new Layer();
            layer._mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseCustomData = true,
                UseColors = true,
                Mesh = UnitQuad,
                InstanceCount = capacity,
                VisibleInstanceCount = 0,
            };
            layer._mmi = new MultiMeshInstance3D
            {
                Multimesh = layer._mm,
                MaterialOverride = mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                // billboarding moves verts off the MultiMesh's computed AABB, pad culling; the
                // margin covers the largest tuned size any spawn path could produce
                ExtraCullMargin = cullMargin,
                // ⚠ Do not switch this to node-origin sorting; a trail emitter's node sits at the
                // world origin. Godot's default, pinned because the between-emitter half of the
                // original's depth sort rides on it (docs/org/textures.md)
                SortingUseAabbCenter = true,
            };
            parent.AddChild(layer._mmi);
            return layer;
        }

        public void Grow(int capacity)
        {
            // Setting InstanceCount reallocates the buffer and clears every slot; the emitter's next
            // frame writes all its live ones back before Show, so nothing drawn is lost for longer.
            if (_mm != null && capacity > _mm.InstanceCount)
            {
                _mm.VisibleInstanceCount = 0;
                _mm.InstanceCount = capacity;
            }
        }

        public void Write(Vector3 position, float size, float frame, float alpha, Color color)
        {
            if (_mm == null || _written >= _mm.InstanceCount)
                return;
            int index = _written++;
            _mm.SetInstanceTransform(index,
                new Transform3D(Basis.Identity.Scaled(new Vector3(size, size, size)), position));
            _mm.SetInstanceCustomData(index, new Color(frame, alpha, 0f, 0f));
            _mm.SetInstanceColor(index, color);
        }

        public void Publish()
        {
            if (_mm != null)
                _mm.VisibleInstanceCount = _written;
            Drawn = _written;
            _written = 0;
        }
    }
}
