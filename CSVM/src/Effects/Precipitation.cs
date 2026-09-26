using System;
using CSVM.Flight.Airframe;
using CSVM.Utils;
using Godot;

namespace CSVM.Effects;

/// <summary>Rain and snow, driven by the mission's <c>weather.json</c> precipitation block (see
/// <see cref="WeatherState.PrecipData"/>). Schema and the data→look TUNE mapping:
/// docs/formats/weather/atmosphere.md's Precipitation section; model and rendering:
/// docs/architecture.md's entry for this file.
/// ⚠ The sprites are procedural (<see cref="MakeFlakeTexture"/>/<see cref="MakeStreakTexture"/>):
/// the original drew untextured coloured primitives, which no texture archive carries.
/// ⚠ RAIN streaks along the data's GRAVITY+WIND fall direction, not the plane-relative velocity.
/// The plane-relative look is nicer but unauthored; it is a TUNE follow-up pending an A/B.
/// </summary>
public sealed partial class Precipitation : Node3D
{
    // --- TUNE: the feel, pending the user's A/B against the original ---
    private const float RainBoxHalf = 30f;   // camera-centred wrap-box half-extent (m), rain …
    private const float SnowBoxHalf = 30f;   // … and snow
    private const int PoolPerParticle = 40;  // MultiMesh instances per data PARTICLE (density)
    private const int DefaultParticles = 100; // SNOW carries no PARTICLES → use this (= rain's)
    private const int MinCount = 800, MaxCount = 5000;
    private const float FallScale = 5f;      // fall speed (m/s) = GRAVITY × this (rain 3→15, snow 1→5)
    private const float WindScale = 1f;      // horizontal drift (m/s) = WIND_VEL × this
    private const float FlakeSize = 0.32f;   // snow quad side (m; ×0.7–1.3 per-instance in-shader)
    private const float StreakWidth = 0.05f; // rain streak width (m)
    private const float StreakSeconds = 0.09f; // rain streak length = fall speed × this …
    private const float MinStreak = 0.8f;    // … but at least this (m)
    private const float OpacityTune = 1f;    // extra master opacity on top of ALPHA_GRADIENT.x
    private const float NearFade = 3f;       // fade particles within this many m of the camera …
    private const float FadeStart = 0.6f;    // … and out beyond this fraction of the box radius
    private const float SnowSwayAmp = 0.6f;  // snow horizontal flutter amplitude (m)
    private const float SnowSwayFreq = 1.3f; // … and frequency (rad/s)

    // Self-animating: fall/wind drift over csky_time, wrapped into a box centred on the camera.
    // POSITION is set directly, so instances stay at the node origin; only the AABB below keeps
    // them visible. INSTANCE_CUSTOM.xyz is the fixed base fraction, .w a flutter phase.
    // ⚠ Never use Godot's TIME here. This module has no per-frame C# hook, so csky_time is the
    // only handle on the animation, TIME kept the rain falling through a halted clock.
    private const string ShaderCode = """
        shader_type spatial;
        render_mode blend_mix, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

        #include "res://shaders/csky_time.gdshaderinc"

        uniform sampler2D sprite : filter_linear, repeat_disable;
        uniform vec3 tint = vec3(0.5);      // already sRGB→linear (set on the CPU, like the fog colour)
        uniform vec3 box_half = vec3(40.0);
        uniform vec3 fall_vel = vec3(0.0, -10.0, 0.0);  // world fall + wind (m/s)
        uniform float peak_alpha = 0.5;
        uniform float near_fade = 3.0;      // fade particles within this many m of the camera
        uniform float fade_start = 0.6;
        uniform vec2 cloud_band = vec2(1e8, 1e9); // [base, top] altitude: precip only BELOW the cloud cover
        uniform bool is_rain = false;
        uniform float flake_size = 0.4;
        uniform vec2 streak_size = vec2(0.05, 1.0);     // (width, length) m
        uniform float sway_amp = 0.6;
        uniform float sway_freq = 1.3;

        varying float v_alpha;

        void vertex() {
            vec3 seed = INSTANCE_CUSTOM.xyz;   // fixed per-instance, [0,1)³
            float phase = INSTANCE_CUSTOM.w;
            vec3 cell = box_half * 2.0;
            vec3 world = seed * cell + fall_vel * csky_time;
            vec3 rel = world - CAMERA_POSITION_WORLD;
            rel = mod(rel + box_half, cell) - box_half;   // wrap into [-box_half, box_half]
            if (!is_rain) {                                // snow flutter (break the lockstep fall)
                rel.x += sin(csky_time * sway_freq + phase) * sway_amp;
                rel.z += cos(csky_time * sway_freq * 0.9 + phase) * sway_amp;
            }
            vec3 center = CAMERA_POSITION_WORLD + rel;

            // Alpha: fade in over the first near_fade metres (so a particle sitting on the
            // camera doesn't blow up into a blob, this is a chase cam, not a cockpit), and
            // fade out past fade_start of the box radius so the box edge has no hard boundary.
            float md = length(rel);              // metres from the camera
            float dn = length(rel / box_half);   // normalized (1 ≈ box face)
            v_alpha = peak_alpha
                * smoothstep(0.0, near_fade, md)
                * (1.0 - smoothstep(fade_start, 1.0, dn))
                // Precip falls FROM the cloud base: full below it, gone above the overcast.
                * (1.0 - smoothstep(cloud_band.x, cloud_band.y, center.y));

            vec3 offset;
            if (is_rain) {
                // Axial billboard: long axis = fall direction, thin axis faces the camera.
                vec3 up = normalize(fall_vel);
                vec3 to_cam = normalize(CAMERA_POSITION_WORLD - center);
                vec3 right = cross(up, to_cam);
                float rl = length(right);
                right = rl > 1e-4 ? right / rl : INV_VIEW_MATRIX[0].xyz;
                offset = right * VERTEX.x * streak_size.x + up * VERTEX.y * streak_size.y;
            } else {
                // Camera billboard flake, per-instance size variety from the seed.
                float sz = flake_size * (0.7 + 0.6 * seed.z);
                offset = (INV_VIEW_MATRIX[0].xyz * VERTEX.x + INV_VIEW_MATRIX[1].xyz * VERTEX.y) * sz;
            }
            POSITION = PROJECTION_MATRIX * (VIEW_MATRIX * vec4(center + offset, 1.0));
        }

        void fragment() {
            ALBEDO = tint;
            ALPHA = texture(sprite, UV).a * v_alpha;
        }
        """;

    private MultiMesh _mm = null!;

    /// <summary>Builds the field for a mission's precipitation, or null if there is none.
    /// <paramref name="cloudBottom"/>/<paramref name="cloudTop"/> are the <c>CLOUD_COVER</c>
    /// band (metres): the precipitation only shows *below* it (it falls from the cloud base,
    /// none above the overcast). Pass an empty band (top ≤ bottom) to disable that gating.
    /// Self-animating once added to the tree, no per-frame driving needed.</summary>
    public static Precipitation? Create(WeatherState.PrecipData? precip, float cloudBottom, float cloudTop)
    {
        if (precip is not { } p)
            return null;
        var field = new Precipitation();
        field.Init(p, cloudBottom, cloudTop);
        return field;
    }

    // A soft round dot (white RGB, radial-falloff alpha): the snow flake.
    private static ImageTexture MakeFlakeTexture()
    {
        const int n = 16;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n - 0.5f, dy = (y + 0.5f) / n - 0.5f;
                float r = Mathf.Sqrt(dx * dx + dy * dy) * 2f; // 0 centre → 1 at the edge
                float a = 1f - Mathf.SmoothStep(0.15f, 0.95f, r);
                img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        return ImageTexture.CreateFromImage(img);
    }

    // A soft vertical streak (thin bright line, fading at both ends): the rain drop. Symmetric
    // top/bottom so the axial billboard's orientation doesn't matter.
    private static ImageTexture MakeStreakTexture()
    {
        const int w = 8, h = 32;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float dx = ((x + 0.5f) / w - 0.5f) / 0.22f;      // thin gaussian across the width
                float across = Mathf.Exp(-dx * dx);
                float along = Mathf.Sin(Mathf.Pi * (y + 0.5f) / h); // fade to 0 at both ends
                img.SetPixel(x, y, new Color(1f, 1f, 1f, across * along));
            }
        return ImageTexture.CreateFromImage(img);
    }

    private void Init(WeatherState.PrecipData p, float cloudBottom, float cloudTop)
    {
        Name = "precipitation";
        bool rain = p.Kind == WeatherState.PrecipKind.Rain;
        float boxHalf = rain ? RainBoxHalf : SnowBoxHalf;

        int particles = p.Particles > 0 ? p.Particles : DefaultParticles;
        int count = Mathf.Clamp(particles * PoolPerParticle, MinCount, MaxCount);

        float fallSpeed = p.Gravity * FallScale;
        float windRad = Mathf.DegToRad(p.WindDir);
        var fallVel = new Vector3(
            Mathf.Sin(windRad) * p.WindVel * WindScale,
            -fallSpeed,
            Mathf.Cos(windRad) * p.WindVel * WindScale);
        float streakLen = Mathf.Max(fallSpeed * StreakSeconds, MinStreak);
        // COLOR is a DX7 sRGB framebuffer value (like FOG_COLOR); convert to linear so the
        // unshaded ALBEDO renders back at the data's byte value in the sRGB framebuffer.
        var tint = p.Color.SrgbToLinear();
        float peakAlpha = p.AlphaGradient.X * OpacityTune;

        var mat = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        mat.SetShaderParameter("sprite", rain ? MakeStreakTexture() : MakeFlakeTexture());
        mat.SetShaderParameter("tint", new Vector3(tint.R, tint.G, tint.B));
        mat.SetShaderParameter("box_half", new Vector3(boxHalf, boxHalf, boxHalf));
        mat.SetShaderParameter("fall_vel", fallVel);
        mat.SetShaderParameter("peak_alpha", peakAlpha);
        mat.SetShaderParameter("near_fade", NearFade);
        mat.SetShaderParameter("fade_start", FadeStart);
        // Only rain/snow below the cloud cover; fade through the band (its interior is
        // whited-out anyway), gone above the overcast. No band → a huge sentinel = no gating.
        var cloudBand = cloudTop > cloudBottom ? new Vector2(cloudBottom, cloudTop) : new Vector2(1e8f, 1e9f);
        mat.SetShaderParameter("cloud_band", cloudBand);
        mat.SetShaderParameter("is_rain", rain);
        mat.SetShaderParameter("flake_size", FlakeSize);
        mat.SetShaderParameter("streak_size", new Vector2(StreakWidth, streakLen));
        mat.SetShaderParameter("sway_amp", SnowSwayAmp);
        mat.SetShaderParameter("sway_freq", SnowSwayFreq);

        _mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One },
            InstanceCount = count,
        };
        // The per-instance seeds baked into the MultiMesh custom data: the whole field's layout is
        // this one draw sequence, so pinning it is what makes a rain/snow shot reproducible.
        var rng = Rng.NewSystemRandom(Rng.Precip);
        for (int i = 0; i < count; i++)
        {
            _mm.SetInstanceTransform(i, Transform3D.Identity); // position comes from the shader
            _mm.SetInstanceCustomData(i, new Color(
                (float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(),
                (float)(rng.NextDouble() * Math.Tau)));
        }

        AddChild(new MultiMeshInstance3D
        {
            Multimesh = _mm,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // The shader repositions every instance around the (moving) camera, far from this
            // node's origin where the actual instance transforms sit, so give it a world-sized
            // custom AABB to never be frustum-culled. One draw call, always drawn: correct here.
            CustomAabb = new Aabb(new Vector3(-40000f, -40000f, -40000f), new Vector3(80000f, 80000f, 80000f)),
        });
        Log.Info("world", $"precipitation: {p.Kind.ToString().ToLowerInvariant()}, {count} particles, fall {fallSpeed:0.0} m/s, tint {p.Color.R * 255:0}/{p.Color.G * 255:0}/{p.Color.B * 255:0}, alpha {peakAlpha:0.00}");
    }
}
