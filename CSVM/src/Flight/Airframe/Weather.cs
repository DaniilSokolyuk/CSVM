using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>
/// Per-mission atmosphere from the mission's own <c>weather.json</c>, a zrdr reader. Feeds the
/// remake's distance fog, the cloud-band whiteout, and the precipitation field. Decode and reader
/// layout: docs/formats/weather.md and docs/formats/weather/atmosphere.md.
/// The zone names are per chapter, not a fixed pair, see <see cref="ResolveZone(string)"/> for
/// the fallback this requires. <see cref="CameraWeatherState"/> is the binary's per-frame camera
/// zone 1/2/3, published each frame by <c>WeatherRig.Tick</c> but consumed by nothing yet.
/// </summary>
public sealed class WeatherState
{
    /// <summary>The install's modal day <c>SUNLIGHT_DIFFUSE</c>, the fallback for a zone that
    /// authors none and the day anchor both lighting mappings scale a zone against
    /// (<c>WeatherRig.EnhancedEnergies</c>, <c>WeatherRig.FaithfulEnergies</c>). ⚠ Do not unify it
    /// with <c>WorldLightFactor</c>'s own 1/0 defaults, which are the faithful collapse's and must
    /// not move. All 53 shipped weather.json author both keys, so the fallback never fires.</summary>
    public const float DefaultDiffuse = 1.5f;

    /// <summary>The ambient half of <see cref="DefaultDiffuse"/>'s pair.</summary>
    public const float DefaultAmbient = 0.5f;

    // A no-op fog (nothing fades) for missions/zones without a FOG_RANGES: near/far so far out
    // that smoothstep is 0 across the whole world. WorldLight 1 = fullbright (no darkening).
    private const float SunIncidence = 0.46f;
    private const float MinWorldLight = 0.15f;

    // SunIncidence/MinWorldLight are a TUNE for the world-brightness scalar the original derives
    // from its per-zone SUNLIGHT_AMBIENT/DIFFUSE; see docs/org/weather.md for the calibration.
    // The sun bearing here is the LAUNCHER's own hand-picked value, not the binary's straight-down
    // default: it only has to make the plane model read in a mission with no weather.json. A
    // mission that has one never reaches this; its zone's authored bearing wins.
    private static readonly ZoneWeather NoFog = new(new Color(0.69f, 0.69f, 0.69f), 1e8f, 1e9f, 1e8f, 1e9f, 1e9f, 1f,
        new Vector3(Mathf.DegToRad(-45f), Mathf.DegToRad(150f), 0f),
        DefaultDiffuse, DefaultAmbient, Colors.White, Colors.White);

    private readonly Dictionary<string, ZoneWeather> _zones = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _zoneNames = new(); // file order, ResolveZone's fallback order

    public enum PrecipKind { Rain, Snow }

    /// <summary>The whiteout cloud band, metres of altitude. <see cref="CloudBottom"/>/<see
    /// cref="CloudTop"/> are where sight is clear; the fully-opaque core is <see
    /// cref="CloudThickness"/> deep and centred on the band's midpoint (see
    /// <see cref="WhiteoutAmount"/>).</summary>
    public float CloudTop { get; private set; }

    public float CloudBottom { get; private set; }

    public float CloudThickness { get; private set; }

    public bool HasCloudBand => CloudTop > CloudBottom;

    /// <summary>The cloud band's midpoint: the altitude the fully-opaque core is centred on
    /// (<see cref="WhiteoutAmount"/>) and the altitude the deck's ceiling/floor regime flips at
    /// (<c>WeatherRig.Tick</c>). One spelling for both, because the flip is unobservable
    /// only for as long as it stays inside that core. Meaningless without
    /// <see cref="HasCloudBand"/>.</summary>
    public float CloudBandCentre => (CloudTop + CloudBottom) * 0.5f;

    /// <summary>The whiteout core's BOTTOM edge: camera altitude at or above this is camera state
    /// 2 (see <see cref="CameraWeatherState"/>, which guards it). Deliberately a third spelling,
    /// distinct from <see cref="CloudBandCentre"/> and <see cref="CloudBottom"/>, the original
    /// computes state-2 and the deck-regime flip as two separate thresholds.</summary>
    public float CloudCoreBottom => CloudBandCentre - (CloudThickness * 0.5f);

    /// <summary>The cloud band's own colours from CLOUD_COVER's <c>TOP_COLOR</c>/<c>BOTTOM_COLOR</c>,
    /// when the mission carries them (integer-RGB in the data, normalized by
    /// <see cref="ParseColor"/>). Null when absent (C1/IA1 has neither). Consumed by
    /// <see cref="WhiteoutColor"/>, which documents what they are and are not.</summary>
    public Color? CloudTopColor { get; private set; }

    public Color? CloudBottomColor { get; private set; }

    /// <summary>The <c>WIND</c> block: a steady base velocity (m/s) plus the three parameters of
    /// the horizontal random-walk gust laid over it (docs/formats/weather/atmosphere.md "Wind").
    /// Consumed by <c>WeatherRig</c>, which builds <see cref="CSVM.Effects.WorldWind"/> and
    /// publishes it to every puffer through <c>EffectAmbience</c>. ⚠ Wind's one decoded consumer
    /// is the puffer particle system; it does not move the cloud clutter.</summary>
    public Vector3 WindStatic { get; private set; }

    public float WindRandomMaxSpeed { get; private set; }

    public float WindRandomAccel { get; private set; }

    /// <summary>The gust's turn rate in DEGREES per second, exactly as the key spells it, the
    /// binary converts on the way into its global (multiplying by 0.017453292), and so does
    /// <see cref="CSVM.Effects.WorldWind"/>, which is where the conversion belongs.</summary>
    public float WindRandomAngVel { get; private set; }

    /// <summary>The mission's precipitation, or null when the weather.json has no TYPE block.</summary>
    public PrecipData? Precip { get; private set; }

    /// <summary>The zones this mission's weather.json actually defines, lowercased and in file
    /// order ("zone1", "zone2", or "zone1", "zone3" in C5). The <c>SW_*</c> software-renderer
    /// twins are excluded. Empty only if the file carries no <c>ZONE*</c> block at all.</summary>
    public IReadOnlyList<string> ZoneNames => _zoneNames;

    /// <summary>Loads the flown mission's weather.json (a zrdr zip or unpacked dir). Null if
    /// the file is absent (some folders are multiplayer-only, etc.).</summary>
    public static WeatherState? Load(string missionZrdrPath)
    {
        List<object?> root;
        try
        {
            root = Zrdr.LoadFile(missionZrdrPath, "weather.json");
        }
        catch (IOException)
        {
            return null;
        }
        if (root.Count == 0 || root[0] is not List<object?> inner)
            return null;
        var dict = ZrdrDict.FromAlternating(inner);

        var w = new WeatherState();
        if (dict.List("CLOUD_COVER") is { } cc)
        {
            w.CloudTop = ScalarAfter(cc, "TOP");
            w.CloudBottom = ScalarAfter(cc, "BOTTOM");
            w.CloudThickness = ScalarAfter(cc, "THICKNESS");
            w.CloudTopColor = ParseColor(ListAfter(cc, "TOP_COLOR"));
            w.CloudBottomColor = ParseColor(ListAfter(cc, "BOTTOM_COLOR"));
        }
        if (dict.List("WIND") is { } wind)
        {
            w.WindStatic = Vec3After(wind, "STATIC_VELOCITY");
            w.WindRandomMaxSpeed = ScalarAfter(wind, "RANDOM_MAX_SPEED");
            w.WindRandomAccel = ScalarAfter(wind, "RANDOM_ACCEL");
            w.WindRandomAngVel = ScalarAfter(wind, "RANDOM_ANG_VEL");
        }
        foreach (var zone in ZoneKeys(inner))
            if (dict.Dict(zone) is { } z)
            {
                w._zoneNames.Add(zone.ToLowerInvariant());
                var color = ParseColor(z.List("FOG_COLOR")) ?? NoFog.FogColor;
                (float near, float far) = z.List("FOG_RANGES") is { Count: >= 2 } fr
                    && fr[0] is float n && fr[1] is float f
                        ? (n, f)
                        : (NoFog.FogNear, NoFog.FogFar);
                (float low, float high) = z.List("FOG_ALTITUDE") is { Count: >= 2 } fa
                    && fa[0] is float l && fa[1] is float h
                        ? (l, h)
                        : (NoFog.FogNear, NoFog.FogFar);
                float clip = z.List("CLIP_RANGES") is { Count: >= 2 } cr && cr[1] is float c ? c : NoFog.ClipFar;
                w._zones[zone] = new ZoneWeather(color, near, far, low, high, clip,
                    WorldLightFactor(z), SunOrientationOf(z),
                    SunlightScalar(z, "SUNLIGHT_DIFFUSE", DefaultDiffuse),
                    SunlightScalar(z, "SUNLIGHT_AMBIENT", DefaultAmbient),
                    ParseColor(z.List("SUNLIGHT_COLOR_DIFFUSE")) ?? Colors.White,
                    ParseColor(z.List("SUNLIGHT_COLOR_AMBIENT")) ?? Colors.White,
                    SunlightScalar(z, "SUNLIGHT_BICOLORED", 0f) != 0f);
                var zf = w._zones[zone];
                // The fields, not the record: a composite ToString() would escape Log's invariant
                // formatting. Sun bearing logged in DEGREES, as the file authors it.
                Log.Info("world", $"weather zone zone={zone} fog_color={zf.FogColor.ToHtml(false)} fog_near={zf.FogNear:0.###} fog_far={zf.FogFar:0.###} fog_low={zf.FogLow:0.###} fog_high={zf.FogHigh:0.###} clip_far={zf.ClipFar:0.###} world_light={zf.WorldLight:0.###} sun_pitch={Mathf.RadToDeg(zf.SunOrientation.X):0.###} sun_yaw={Mathf.RadToDeg(zf.SunOrientation.Y):0.###} sun_roll={Mathf.RadToDeg(zf.SunOrientation.Z):0.###}");
            }
        w.ParsePrecip(inner);
        return w;
    }

    /// <summary>Swaps a zone whose horizon subtree is empty for the one zone that has geometry,
    /// the geometry half of <see cref="ResolveZone(string, IReadOnlyList{HorizonZone})"/>, public
    /// on its own so a mission with no weather.json still gets the correction. Fires only when the
    /// request is one of the horizon's zones, builds nothing, and exactly one sibling builds
    /// something; every other shape keeps the request. ⚠ Never take the horizon's first zone as a
    /// C5 fallback instead, see docs/formats/weather.md on the two orders disagreeing.</summary>
    public static string PreferPopulatedHorizonZone(string zone, IReadOnlyList<HorizonZone> horizonZones)
    {
        bool requestedIsAZone = false;
        foreach (var z in horizonZones)
            if (z.Name.Equals(zone, StringComparison.OrdinalIgnoreCase))
            {
                if (z.BuildsGeometry)
                    return zone;
                requestedIsAZone = true;
            }
        if (!requestedIsAZone)
            return zone;
        string? only = null;
        foreach (var z in horizonZones)
        {
            if (!z.BuildsGeometry)
                continue;
            if (only != null)
                return zone;   // more than one candidate, not decidable from the geometry
            only = z.Name;
        }
        return only ?? zone;
    }

    /// <summary>The zone to actually render: the request itself when this mission defines it,
    /// otherwise the file's first zone, keeping the request unchanged only when the file defines
    /// none (<see cref="NoFog"/>). This is what makes the <c>zone2</c> default safe on C5, which
    /// ships zone1+zone3 and lands on <c>zone1</c>, confirmed by playtest.
    /// ⚠ Do not "fix" the fallback into picking <c>zone3</c> for C5. See docs/formats/weather.md
    /// for the per-chapter A/B this default is settled by.</summary>
    public string ResolveZone(string requested) =>
        _zones.ContainsKey(requested) || _zoneNames.Count == 0 ? requested : _zoneNames[0];

    /// <summary>The zone a given camera weather state wears: state N asks for <c>zoneN</c>
    /// through <see cref="ResolveZone(string)"/>'s file fallback, keeping this safe on data that
    /// does not author the requested zone. Deliberately NOT routed through
    /// <see cref="ResolveZone(string, IReadOnlyList{HorizonZone})"/>: the horizon correction owns
    /// the dome's zone once per flight, while the fog zone changes every camera crossing of the
    /// cloud core. Reconciling the two is open.</summary>
    public string ZoneForState(int cameraState) =>
        ResolveZone("zone" + cameraState.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The zone to render when the chapter's own <c>horizon</c> subtree gets a say:
    /// <see cref="ResolveZone(string)"/> first, then <see cref="PreferPopulatedHorizonZone"/>,
    /// with the correction taken only if this mission also defines fog for it, so the sky and the
    /// fog stay the same zone (docs/formats/weather.md). This is what fixes C1B, C2 and C3, whose
    /// <c>horizon/zone2</c> is a bare marker even though the mission does define <c>ZONE2</c>
    /// fog.</summary>
    public string ResolveZone(string requested, IReadOnlyList<HorizonZone> horizonZones)
    {
        var zone = ResolveZone(requested);
        var preferred = PreferPopulatedHorizonZone(zone, horizonZones);
        return preferred.Equals(zone, StringComparison.OrdinalIgnoreCase) || _zones.ContainsKey(preferred)
            ? preferred
            : zone;
    }


    /// <summary>One zone's weather ("zone1"/"zone2"/"zone3"), fog, world light and sun bearing;
    /// <see cref="NoFog"/> if the zone is absent. Callers should pass a <see cref="ResolveZone"/>
    /// result rather than a raw request.</summary>
    public ZoneWeather Zone(string zone) => _zones.TryGetValue(zone, out var z) ? z : NoFog;

    /// <summary>Whiteout opacity 0..1 at a given altitude: a symmetric trapezoid across the
    /// cloud band (user-observed in-game). Clear sight (0) at BOTTOM and TOP, ramping
    /// linearly to a fully-opaque core (1, the plane is no longer visible) that is THICKNESS
    /// deep and centred on the band's midpoint. THICKNESS is the depth of that opaque core, not
    /// an edge transition, so C1/IA1 (970–1124, ±30) is clear at 970/1124 and total in
    /// 1032–1062, with linear ramps between.</summary>
    public float WhiteoutAmount(float altitude)
    {
        if (!HasCloudBand || altitude <= CloudBottom || altitude >= CloudTop)
            return 0f;
        float mid = CloudBandCentre;
        float coreHalf = CloudThickness * 0.5f;       // half-depth of the opaque core, centred on mid
        float dist = MathF.Abs(altitude - mid);
        if (dist <= coreHalf)
            return 1f;
        float ramp = (CloudTop - CloudBottom) * 0.5f - coreHalf; // core edge → band edge
        return ramp > 1e-3f ? Mathf.Clamp(1f - (dist - coreHalf) / ramp, 0f, 1f) : 1f;
    }

    /// <summary>The colour the whiteout paints at a given altitude, or null when the mission
    /// authors no <c>CLOUD_COVER</c> colour: <see cref="CloudBottomColor"/> lerping to
    /// <see cref="CloudTopColor"/> by altitude across the band, decoded from <c>FUN_0042ee40</c>
    /// (docs/formats/weather/atmosphere.md). Not a deck-mesh face tint or a skydome grade; see
    /// that page for why.</summary>
    public Color? WhiteoutColor(float altitude)
    {
        if (CloudTopColor is not { } top)
            return CloudBottomColor;
        if (CloudBottomColor is not { } bottom)
            return top;
        if (!HasCloudBand)
            return top;
        return bottom.Lerp(top, Mathf.Clamp((altitude - CloudBottom) / (CloudTop - CloudBottom), 0f, 1f));
    }

    /// <summary>The camera's per-frame weather state (the binary's camera state 1/2/3): 1 by
    /// default; 2 at or above <see cref="CloudCoreBottom"/>; 3 when <paramref name="fogZoneArmed"/>
    /// and the camera sits inside any of <paramref name="volumes"/>'s authored shape. State 3
    /// takes precedence, matching the binary's assignment order, though this never arbitrates in
    /// shipped data. Exposed but consumed by nothing yet; <c>WeatherRig.Tick</c> publishes it per
    /// camera each frame.</summary>
    public int CameraWeatherState(
        Vector3 cameraPosition, bool fogZoneArmed, IReadOnlyList<FogVolumeBox> volumes)
    {
        int state = HasCloudBand && cameraPosition.Y >= CloudCoreBottom ? 2 : 1;
        if (fogZoneArmed)
            foreach (var volume in volumes)
                if (volume.Contains(cameraPosition))
                    return 3;
        return state;
    }

    /// <summary>The zone a drawn object earns from its own world-space vertical span, the original's
    /// per-object half of the zone gate (<c>FUN_00489f60</c>). It is 1 wholly below the band's
    /// midpoint, 2 wholly above it, -1 when it straddles the midpoint or the mission authors no
    /// band. A -1 object is ungated, drawn at every camera state. The threshold is the MIDPOINT,
    /// not the opaque core's edge the camera's own state flips at.
    /// Decode: docs/formats/weather/atmosphere.md.</summary>
    public int ObjectZone(float minY, float maxY)
    {
        if (!HasCloudBand)
            return -1;
        float centre = CloudBandCentre;
        if (maxY < centre)
            return 1;
        return centre < minY ? 2 : -1;
    }

    /// <summary>The same verdict for an object whose world extent is <paramref name="span"/>, with
    /// the original's own first arm ahead of the band. An object standing in an armed <c>fvol</c>
    /// volume is zone 3 whatever its altitude, which keeps it drawn for a camera in that volume.
    /// Precedence and inputs match <see cref="CameraWeatherState"/>.</summary>
    public int ObjectZone(Aabb span, bool fogZoneArmed, IReadOnlyList<FogVolumeBox> volumes)
    {
        if (fogZoneArmed)
        {
            var centre = span.GetCenter();
            foreach (var volume in volumes)
                if (volume.Contains(centre))
                    return 3;
        }

        return ObjectZone(span.Position.Y, span.Position.Y + span.Size.Y);
    }

    private static float WorldLightFactor(ZrdrDict zone)
    {
        float diffuse = zone.List("SUNLIGHT_DIFFUSE") is { Count: >= 1 } sd && sd[0] is float dv ? dv : 1f;
        float ambient = zone.List("SUNLIGHT_AMBIENT") is { Count: >= 1 } sa && sa[0] is float av ? av : 0f;
        return Mathf.Clamp(ambient + diffuse * SunIncidence, MinWorldLight, 1f);
    }

    // One SUNLIGHT_* scalar carried through uncollapsed, for the lighting that drives a real sun
    // from it rather than the fullbright brightness scalar WorldLightFactor folds it into.
    private static float SunlightScalar(ZrdrDict zone, string key, float fallback)
        => zone.List(key) is { Count: >= 1 } list && list[0] is float v ? v : fallback;

    // The zone's `SUNLIGHT_ORIENTATION` as Godot euler RADIANS, ready to assign
    // straight to a DirectionalLight3D's `Rotation`, see
    // ZoneWeather.SunOrientation for why no axis conversion is needed. The data is
    // degrees; the binary multiplies by the same 0.017453292. Absent (or short) → the binary's
    // own default, pitch −π/2: straight down.
    private static Vector3 SunOrientationOf(ZrdrDict zone)
    {
        if (zone.List("SUNLIGHT_ORIENTATION") is not { Count: >= 2 } so
            || so[0] is not float pitch || so[1] is not float yaw)
            return new Vector3(-Mathf.Pi * 0.5f, 0f, 0f);
        // ROLL is optional in shape but present install-wide (always 0). A directional light is
        // rotationally symmetric about its own beam, so roll cannot change the shading, it is
        // carried anyway rather than dropped, so the record holds what the file holds.
        float roll = so.Count >= 3 && so[2] is float r ? r : 0f;
        return new Vector3(Mathf.DegToRad(pitch), Mathf.DegToRad(yaw), Mathf.DegToRad(roll));
    }

    // The file's own per-zone block names, in FILE ORDER (docs/formats/weather.md). ResolveZone
    // falls back along this order, so it must come from this raw walk, not ZrdrDict, which is a
    // Dictionary and does not preserve it. "ZONE" + digits only: the SW_ZONE* software-renderer
    // twins must never enter the selectable set.
    private static List<string> ZoneKeys(List<object?> inner)
    {
        var keys = new List<string>();
        for (int i = 0; i + 1 < inner.Count; i++)
            if (inner[i] is string s && inner[i + 1] is List<object?>
                && s.StartsWith("ZONE", StringComparison.OrdinalIgnoreCase)
                && s.Length > 4 && char.IsDigit(s[4])
                && !keys.Exists(k => k.Equals(s, StringComparison.OrdinalIgnoreCase)))
                keys.Add(s);
        return keys;
    }

    // key → the immediately following scalar in a flat alternating list
    // (CLOUD_COVER: "TOP", 1124, "BOTTOM", 970, …).
    private static float ScalarAfter(List<object?> list, string key)
    {
        for (int i = 0; i + 1 < list.Count; i++)
            if (list[i] is string s && s.Equals(key, StringComparison.OrdinalIgnoreCase)
                && list[i + 1] is float f)
                return f;
        return 0f;
    }

    // key → the immediately following string (precipitation: "TYPE", "SNOW", …). Null if absent.
    private static string? StringAfter(List<object?> list, string key)
    {
        for (int i = 0; i + 1 < list.Count; i++)
            if (list[i] is string s && s.Equals(key, StringComparison.OrdinalIgnoreCase)
                && list[i + 1] is string v)
                return v;
        return null;
    }

    // key → the immediately following 2-vector (precipitation: "ALPHA_GRADIENT", [0.5, 0.0]).
    private static Vector2 Vec2After(List<object?> list, string key)
    {
        for (int i = 0; i + 1 < list.Count; i++)
            if (list[i] is string s && s.Equals(key, StringComparison.OrdinalIgnoreCase)
                && list[i + 1] is List<object?> v && v.Count >= 2
                && v[0] is float x && v[1] is float y)
                return new Vector2(x, y);
        return Vector2.Zero;
    }

    // key → the immediately following 3-vector (WIND: "STATIC_VELOCITY", [0, 2, 0], …).
    private static Vector3 Vec3After(List<object?> list, string key)
    {
        for (int i = 0; i + 1 < list.Count; i++)
            if (list[i] is string s && s.Equals(key, StringComparison.OrdinalIgnoreCase)
                && list[i + 1] is List<object?> v && v.Count >= 3
                && v[0] is float x && v[1] is float y && v[2] is float z)
                return new Vector3(x, y, z);
        return Vector3.Zero;
    }

    // key → the immediately following list value in a flat alternating block
    // (CLOUD_COVER: "TOP_COLOR", [192, 192, 192], …). Null if the key is absent.
    private static List<object?>? ListAfter(List<object?> list, string key)
    {
        for (int i = 0; i + 1 < list.Count; i++)
            if (list[i] is string s && s.Equals(key, StringComparison.OrdinalIgnoreCase)
                && list[i + 1] is List<object?> v)
                return v;
        return null;
    }

    // A weather.json colour triple, normalized to 0..1. Two encodings coexist: normalized
    // floats (C1 FOG_COLOR 0.69) and integer RGB (C4 FOG_COLOR [192,192,192]; every
    // TOP_COLOR/BOTTOM_COLOR). Any component strictly > 1 means the whole triple is 0–255 and
    // is divided by 255, verified unambiguous across all weather.json (the only 1.0-bearing
    // colour is a float sky-fog [0.80,0.84,1.0], whose max is exactly 1, so it stays a float).
    // These are DX7 sRGB framebuffer values; GameSession converts them to linear for the shader.
    private static Color? ParseColor(List<object?>? list)
    {
        if (list is not { Count: >= 3 } || list[0] is not float r || list[1] is not float g || list[2] is not float b)
            return null;
        float scale = r > 1f || g > 1f || b > 1f ? 1f / 255f : 1f;
        return new Color(r * scale, g * scale, b * scale);
    }

    // The precipitation block (TYPE SNOW|RAIN, COLOR, WIND_DIR, WIND_VEL, GRAVITY, [PARTICLES],
    // ALPHA_GRADIENT) sits at the END of the root dict, after SHADOW_ANGLES, as bare-scalar
    // top-level siblings, so it's walked raw from `inner` (like CLOUD_COVER/WIND), not via the
    // dict (which drops a key's value when it's a bare scalar rather than a list). The keys are
    // unique at inner's top level (FOG_COLOR/SUNLIGHT_* live inside the nested zone sub-lists,
    // which the flat walkers never descend into), so each first-match is the right one.
    private void ParsePrecip(List<object?> inner)
    {
        string? type = StringAfter(inner, "TYPE");
        PrecipKind kind;
        if (string.Equals(type, "RAIN", StringComparison.OrdinalIgnoreCase)) kind = PrecipKind.Rain;
        else if (string.Equals(type, "SNOW", StringComparison.OrdinalIgnoreCase)) kind = PrecipKind.Snow;
        else return; // no TYPE (most missions) or an unknown type → no precipitation
        Precip = new PrecipData(
            kind,
            ParseColor(ListAfter(inner, "COLOR")) ?? new Color(0.5f, 0.5f, 0.5f),
            WindDir: ScalarAfter(inner, "WIND_DIR"),
            WindVel: ScalarAfter(inner, "WIND_VEL"),
            Gravity: ScalarAfter(inner, "GRAVITY"),
            Particles: (int)ScalarAfter(inner, "PARTICLES"), // absent (SNOW) → 0
            AlphaGradient: Vec2After(inner, "ALPHA_GRADIENT"));
    }

    /// <summary>One day/night zone's weather: distance fog, the world-brightness scalar with the
    /// uncollapsed SUNLIGHT pair and colours beside it, and the sun bearing, written together in
    /// one call by the original's zone-apply (docs/formats/weather.md). <see cref="SunOrientation"/>
    /// is Godot euler RADIANS, a <see cref="DirectionalLight3D"/> <c>Rotation</c>. ⚠ It is the
    /// shading direction, not the <c>sun</c> billboard's position (90 degrees apart in C3).
    /// <see cref="SunBicolored"/> is <c>SUNLIGHT_BICOLORED</c> (docs/org/vertexLighting.md).</summary>
    public readonly record struct ZoneWeather(Color FogColor, float FogNear, float FogFar, float FogLow, float FogHigh, float ClipFar, float WorldLight, Vector3 SunOrientation, float SunDiffuse, float SunAmbient, Color SunColorDiffuse, Color SunColorAmbient, bool SunBicolored = false);

    /// <summary>The mission's precipitation, from the bare-scalar block at the end of
    /// weather.json. Only some missions carry one; C1/C5 IA1 have none. Consumed by
    /// <see cref="Effects.Precipitation"/>. <see cref="WindDir"/>/<see cref="WindVel"/> are the
    /// precipitation's own drift, separate from the cloud <c>WIND</c> block above;
    /// <see cref="Gravity"/> is a fall-rate multiplier in the data's own units. Field-by-field
    /// detail: docs/formats/weather/atmosphere.md.</summary>
    public readonly record struct PrecipData(
        PrecipKind Kind, Color Color, float WindDir, float WindVel,
        float Gravity, int Particles, Vector2 AlphaGradient);
}
