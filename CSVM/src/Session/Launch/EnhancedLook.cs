using CSVM.Session.World;
using Godot;

namespace CSVM.Session.Launch;

/// <summary>
/// The enhanced graphics mode's settings on a sun and an Environment, in both directions: turned
/// on at launch, and on or off again when the mode switches live. Every magnitude here is TUNE,
/// since nothing in the original authors a shadow map or a screen-space pass. The off direction
/// puts back a fresh object's defaults, which is what the faithful path builds with.
/// </summary>
public static class EnhancedLook
{
    // TUNE, and the FALLBACK only: a flown mission overwrites this per zone from its own pushed-out
    // fog near (WeatherRig.ApplyEnhancedLighting), so shadows end where that zone's haze ramp
    // begins. This value is what a session with no weather.json gets, and it sits in the middle of
    // the pushed-near range the shipped zones resolve to (2000-4200 m).
    private const float ShadowMaxDistance = 3000f;

    // TUNE, paired with the distance above and judged the same way (WeatherRig.ApplyEnhancedLighting
    // sets the flown-mission copy). Fades the last cascade out before this fallback distance rather
    // than cutting at a hard edge.
    private const float ShadowFadeStart = 0.8f;

    // TUNE, judged at the controls, and the pair trades against each other: lower values put
    // dithered acne over every terrain triangle at C1's 25° sun, higher ones dissolve a hangar's
    // shadow along with it. These keep the building and aircraft silhouettes with no acne left.
    private const float ShadowBias = 0.05f;
    private const float ShadowNormalBias = 1.25f;

    // TUNE. Fractions of ShadowMaxDistance, tighter than Godot's 0.1/0.2/0.5 because the
    // shadows a player reads are the aircraft's own and the buildings it passes, all inside the
    // first few hundred metres; the outer cascades only have to carry a skyline into the haze.
    private const float ShadowSplit1 = 0.06f;
    private const float ShadowSplit2 = 0.17f;
    private const float ShadowSplit3 = 0.42f;

    // TUNE. Screen-space reflection on the glossy water arm: the step count buys reflection length
    // along the ray, the fades hide where a ray runs off the screen or past the depth buffer.
    // The fade-out exponent is the lever on the border and aircraft flicker, since it dims a ray
    // before it is lost. Depth tolerance measures inert here, from this value up to 8.0.
    private const int SsrMaxSteps = 64;
    private const float SsrFadeIn = 0.15f;
    private const float SsrFadeOut = 2.5f;
    private const float SsrDepthTolerance = 0.2f;

    // TUNE, judged at the controls. The sun's apparent size in degrees; the real sun is about
    // 0.5, softening a cast edge into a penumbra instead of a hard line. A 0.25/0.5/1.0/2.0
    // sweep at the C1 waterfall lake held the edge at 4-6 px through 1.0. Only 2.0 opened it
    // into a visibly soft ~18 px transition.
    private const float ShadowAngularDistance = 2.0f;

    // TUNE, judged at the controls: Godot's own default. Raising it alongside the angular
    // distance above widened the edge further, but it also dithered the lit water beside it.
    // Kept here rather than trading a hard line for banding.
    private const float ShadowBlur = 1.0f;

    // ⚠ Do not lower this while ShadowAngularDistance stays above the sun's real 0.5°.
    // Godot resolves a penumbra by sampling the shadow map through a disc rotated per screen
    // pixel. Too few samples for the disc's width leave that rotation as a woven pattern over
    // every lit surface. This width needs the top rung.
    private const RenderingServer.ShadowQuality ShadowFilterQuality = RenderingServer.ShadowQuality.SoftUltra;

    // Where the faithful path's filter quality comes from: project.godot, or Godot's own default.
    private const string ShadowFilterQualitySetting =
        "rendering/lights_and_shadows/directional_shadow/soft_shadow_filter_quality";

    // TUNE, judged at the controls on C2/C5. Godot's own default (1.0 m) reads a building's own
    // trim but misses the wider contact shading a street canyon wants at this world's scale
    // (buildings tens of metres tall, streets a similar width); this radius picks up a block's
    // base and a hangar's corner without darkening open tarmac.
    private const float SsaoRadius = 2.5f;

    // TUNE, judged at the controls: Godot's defaults (intensity 2.0, power 1.5) already read as
    // grounded contact shading rather than a grey wash at this radius, so both are kept.
    private const float SsaoIntensity = 2.0f;
    private const float SsaoPower = 1.5f;

    // TUNE, Godot defaults: detail keeps small-scale creases (window mullions, girders) from
    // being swallowed by the coarse term above; horizon and sharpness are the denoise pair that
    // keeps the depth-buffer edges from shimmering worse than the effect is worth.
    private const float SsaoDetail = 0.5f;
    private const float SsaoHorizon = 0.06f;
    private const float SsaoSharpness = 0.98f;

    // TUNE, judged at the controls against C21's contract (only the glow-arm sprites exceed 1.0
    // in the HDR buffer). A threshold of 1.0 blooms exactly them; bloom stays 0 so nothing below
    // threshold glows, and screen blend keeps a flare's halo additive without blowing its own
    // core out further.
    private const float GlowHdrThreshold = 1.0f;
    private const float GlowBloom = 0.0f;
    private const float GlowIntensity = 0.9f;
    private const float GlowStrength = 1.1f;
    private const Godot.Environment.GlowBlendModeEnum GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Screen;

    // TUNE. Scale and cap on the values the glow pass reads before it thresholds them; wide enough
    // that a saturated flare core (255 before the tonemap) still separates from its own falloff.
    private const float GlowHdrScale = 2.0f;
    private const float GlowHdrLuminanceCap = 8.0f;

    // TUNE, judged at the controls against a C4 horizon, a C1 horizon and C5 at night: AgX rolls
    // off the far-ridge washout the authored sun energy produces (Wave B) while keeping the night
    // city's contrast, where Filmic read flatter. Exposure stays neutral; the AgX-specific white
    // point is what recovers the horizon rather than the general TonemapWhite, which AgX ignores.
    private const Godot.Environment.ToneMapper TonemapMode = Godot.Environment.ToneMapper.Agx;
    private const float TonemapExposure = 1.0f;
    private const float TonemapAgxWhite = 6.0f;
    private const float TonemapAgxContrast = 1.0f;

    // The zone default FOG_COLOR (Flight/Airframe/Weather.cs's no-weather zone), which is the colour a
    // horizon dome fades into at eye level. Enhanced mode's sky until a flown zone writes its own
    // over it, so a world with no weather.json still reflects a plausible sky.
    private static readonly Color DefaultSkyColor = new(0.69f, 0.69f, 0.69f);

    /// <summary>The session sun's shadow maps, on or back to a fresh light's defaults.
    /// ⚠ Also sets the renderer-wide soft-shadow filter, which belongs to this light alone.</summary>
    public static void ApplySun(DirectionalLight3D sun, bool enhanced, EnhancedPasses skipped)
    {
        if (!enhanced)
        {
            // The colour too: the enhanced zone apply tints the sun, and the faithful one never
            // writes it back.
            var fresh = new DirectionalLight3D();
            sun.ShadowEnabled = false;
            sun.LightColor = fresh.LightColor;
            CopyShadow(fresh, sun, fresh.DirectionalShadowMaxDistance);
            fresh.Free();
            RenderingServer.DirectionalSoftShadowFilterSetQuality((RenderingServer.ShadowQuality)
                ProjectSettings.GetSetting(ShadowFilterQualitySetting, (int)RenderingServer.ShadowQuality.SoftLow).AsInt32());
            return;
        }
        // Four splits because the useful range spans an aircraft's own shadow a few metres below
        // it and a skyline several kilometres out.
        sun.ShadowEnabled = true;
        sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits;
        sun.DirectionalShadowMaxDistance = ShadowMaxDistance;
        sun.DirectionalShadowFadeStart = ShadowFadeStart;
        sun.DirectionalShadowSplit1 = ShadowSplit1;
        sun.DirectionalShadowSplit2 = ShadowSplit2;
        sun.DirectionalShadowSplit3 = ShadowSplit3;
        sun.DirectionalShadowBlendSplits = true;
        sun.ShadowBias = ShadowBias;
        sun.ShadowNormalBias = ShadowNormalBias;
        // Both zero leaves a hard shadow edge rather than no shadow. That isolates the penumbra
        // filter, which is the part resolving with a screen-space sample pattern.
        bool hard = (skipped & EnhancedPasses.SoftShadows) != 0;
        sun.LightAngularDistance = hard ? 0f : ShadowAngularDistance;
        sun.ShadowBlur = hard ? 0f : ShadowBlur;
        // A renderer-wide setting rather than a light property. It is set here beside the width it
        // carries, not in project.godot, where the faithful path would inherit it.
        RenderingServer.DirectionalSoftShadowFilterSetQuality(hard ? RenderingServer.ShadowQuality.Hard : ShadowFilterQuality);
    }

    /// <summary>A pass's own light takes the session sun's shadow settings, its distance clamped to
    /// <paramref name="maxDistance"/>, the far plane of the camera it serves.</summary>
    public static void CopyShadow(DirectionalLight3D from, DirectionalLight3D to, float maxDistance)
    {
        to.ShadowEnabled = from.ShadowEnabled;
        to.DirectionalShadowMode = from.DirectionalShadowMode;
        to.DirectionalShadowFadeStart = from.DirectionalShadowFadeStart;
        to.DirectionalShadowSplit1 = from.DirectionalShadowSplit1;
        to.DirectionalShadowSplit2 = from.DirectionalShadowSplit2;
        to.DirectionalShadowSplit3 = from.DirectionalShadowSplit3;
        to.DirectionalShadowBlendSplits = from.DirectionalShadowBlendSplits;
        to.ShadowBias = from.ShadowBias;
        to.ShadowNormalBias = from.ShadowNormalBias;
        to.LightAngularDistance = from.LightAngularDistance;
        to.ShadowBlur = from.ShadowBlur;
        to.DirectionalShadowMaxDistance = Mathf.Min(from.DirectionalShadowMaxDistance, maxDistance);
    }

    /// <summary>The screen-space passes, the tonemap and the mission sky on one Environment, or a
    /// fresh Environment's values for each. The faithful path's world is fullbright, so none of
    /// these passes has anything to work on there. A zone apply writes the ambient and the sky
    /// colour after this, in either mode.</summary>
    public static void ApplyEnvironment(Godot.Environment env, bool enhanced, EnhancedPasses skipped)
    {
        if (!enhanced)
        {
            var fresh = new Godot.Environment();
            env.Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() };
            env.ReflectedLightSource = fresh.ReflectedLightSource;
            env.SsaoEnabled = false;
            env.SsrEnabled = false;
            env.GlowEnabled = false;
            env.TonemapMode = fresh.TonemapMode;
            env.TonemapExposure = fresh.TonemapExposure;
            env.TonemapAgxWhite = fresh.TonemapAgxWhite;
            env.TonemapAgxContrast = fresh.TonemapAgxContrast;
            return;
        }
        // Reflections read the mission's own colour, not Godot's gradient. The gamez dome hides the
        // background, so this feeds only the water's specular. WeatherRig.WriteSkyColor writes the
        // flown zone's FOG_COLOR over the default on every zone apply.
        env.Sky = new Sky { SkyMaterial = new PanoramaSkyMaterial() };
        WeatherRig.WriteSkyColor(env, DefaultSkyColor);
        env.SsaoEnabled = (skipped & EnhancedPasses.Ssao) == 0;
        if (env.SsaoEnabled)
        {
            env.SsaoRadius = SsaoRadius;
            env.SsaoIntensity = SsaoIntensity;
            env.SsaoPower = SsaoPower;
            env.SsaoDetail = SsaoDetail;
            env.SsaoHorizon = SsaoHorizon;
            env.SsaoSharpness = SsaoSharpness;
        }
        // ⚠ SSR reflects only what the camera already draws; content off-screen or behind the
        // near plane has no reflection at all. Water is the one glossy population it serves.
        env.SsrEnabled = (skipped & EnhancedPasses.Ssr) == 0;
        if (env.SsrEnabled)
        {
            env.SsrMaxSteps = SsrMaxSteps;
            env.SsrFadeIn = SsrFadeIn;
            env.SsrFadeOut = SsrFadeOut;
            env.SsrDepthTolerance = SsrDepthTolerance;
        }
        // C21's glow-arm sprites are the only surfaces meant to bloom out of the HDR buffer.
        env.GlowEnabled = (skipped & EnhancedPasses.Glow) == 0;
        if (env.GlowEnabled)
        {
            env.GlowHdrThreshold = GlowHdrThreshold;
            env.GlowBloom = GlowBloom;
            env.GlowIntensity = GlowIntensity;
            env.GlowStrength = GlowStrength;
            env.GlowBlendMode = GlowBlendMode;
            env.GlowHdrScale = GlowHdrScale;
            env.GlowHdrLuminanceCap = GlowHdrLuminanceCap;
        }
        // Without the tonemap the HDR values a lit world produces clip instead of rolling off. No
        // bisect door closes it, since every enhanced frame's exposure depends on it.
        env.TonemapMode = TonemapMode;
        env.TonemapExposure = TonemapExposure;
        env.TonemapAgxWhite = TonemapAgxWhite;
        env.TonemapAgxContrast = TonemapAgxContrast;
    }
}
