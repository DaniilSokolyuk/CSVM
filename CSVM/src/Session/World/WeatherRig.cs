using System;
using System.Collections.Generic;
using CSVM.Effects;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Mech3;
using CSVM.Session.Objectives;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.World;

/// <summary>Loads and applies the flown mission's weather (<see cref="Build"/>) and drives the
/// per-rig skydome/whiteout/deck/zone-gate update every frame (<see cref="Tick"/>). Constructed
/// once per session, same lifetime as <see cref="Roster.LiveryResolver"/>/<see cref="Roster.SpawnPicker"/>/
/// <see cref="WorldEffectsFactory"/>. The horizon build loop stays on <c>GameSession</c>, a
/// <c>SceneBuilder</c> concern, not weather state, and <see cref="Build"/> only invokes it as a
/// callback between resolving the zone and applying fog/whiteout/precip.
/// This class is the sole owner of render visibility: <see cref="Tick"/> narrows each camera's
/// cull mask to its own <see cref="CSVM.Mech3.ZoneGate"/> state and switches this rig's own deck
/// and dome copies with <c>Node3D.Visible</c> under the same rule. Full decode:
/// docs/org/weather.md.</summary>
public sealed class WeatherRig
{
    // ⚠ TUNE, judged at the controls: the authored SUNLIGHT numbers are the original's own
    // lighting units, not Godot energies. Both factors are anchored on the install's modal day
    // zone (diffuse 1.5, ambient 0.5) landing on the 1.6 and 0.9 the faithful path hardcodes, so
    // a day mission keeps today's level while the data alone carries C1B's night zone
    // (0.6 / 0.15) down to 0.64 and 0.27.
    private const float SunEnergyPerDiffuse = 1.07f;
    private const float AmbientEnergyPerAuthored = 1.8f;

    // ⚠ TUNE, judged at the controls, and NOT enhanced mode's pair. There a real sun lights the
    // whole world through a tonemap; here it lights only the Godot-shaded surfaces of a faithful
    // flight over a fullbright world. The in-flight aircraft is not one of them: it draws the
    // decoded per-vertex term (SunVertexLight). This is the level a zone authoring the install's
    // modal day SUNLIGHT keeps, and FaithfulEnergies caps there. No night cap either: the
    // faithful world light ignores FOG_COLOR.
    private const float FaithfulSunEnergy = 1.6f;
    private const float FaithfulAmbientEnergy = 0.9f;

    // ⚠ TUNE, enhanced mode only, and a PROXY the original never uses: it lights from SUNLIGHT and
    // darkens from FOG_COLOR independently, so nothing in the game reads one off the other. What
    // licenses it is that the two populations do not overlap: every zone under a night sky authors
    // a fog luminance of 0.00 to 0.10 and every day zone 0.69 to 0.85, with nothing between across
    // all 212 blocks. See docs/org/weather.md for the census.
    private const float NightFogLuminance = 0.25f;

    // ⚠ TUNE, and the install's OWN night pair (C1B's night zones, SUNLIGHT 0.6 / 0.15), used as a
    // ceiling rather than a replacement: a night zone gets at most the light a night zone authors,
    // so C5's day-level 1.5 / 0.5 under a black sky stops lighting the city like noon while a zone
    // already authored dimmer keeps its own values.
    private const float NightDiffuseCap = 0.6f;
    private const float NightAmbientCap = 0.15f;

    // The flat sky panorama's size, enhanced mode only. One texel would do for a colour that is
    // uniform in every direction; a few keeps the equirectangular sampler and its mip chain off
    // the degenerate case for nothing.
    private const int SkyPanoramaWidth = 8;
    private const int SkyPanoramaHeight = 4;

    // ⚠ TUNE, judged at the controls, and enhanced mode ONLY. Shadowed ground reads badly through
    // the authored haze: a cast shadow needs contrast to be seen at all, and the zones put full fog
    // close enough that half of every shadow is already grey. Pushing near and far out together
    // keeps the ramp's shape (and so the horizon's look) while giving the shadowed range clear air
    // to live in. The faithful path keeps the authored ranges exactly, per the prohibition in
    // ApplyZone.
    private const float EnhancedFogRangeScale = 2f;

    // TUNE, judged at the controls. The last shadow cascade fades out over this fraction of the
    // sun's DirectionalShadowMaxDistance rather than cutting at a hard edge, so a shadow reads as
    // dissolving into the coming haze instead of vanishing the frame before it starts.
    private const float EnhancedShadowFadeStart = 0.8f;

    // ⚠ TUNE, and the FALLBACK only, a mission that authors CLOUD_COVER colours overrides it
    // (WeatherState.WhiteoutColor). It holds because the three reachable-band chapters that author
    // nothing measure right at this value: the original's C1 in-cloud interior reads 248 against
    // our 243, and C1C and C2B share C1's colourless CLOUD_COVER block. Do not "unify" it with the
    // zone FOG_COLOR: C1's fog is 0.69 = 176, which would darken a passing A/B by 70 units.
    private static readonly Color WhiteoutFallbackColor = new(0.95f, 0.95f, 0.96f);

    // One flicker per rig, keyed like `_lastLoggedVolumeWhiteout`, the original keeps its drift
    // state in a single global, but that assumes one camera, and splitscreen panes on opposite
    // sides of the band must not share a drift phase.
    private readonly Dictionary<int, BandFlicker> _bandFlicker = new();

    private readonly SessionSpec _spec;
    private readonly Node3D _worldRoot;
    // The seam every puffer in the session reads its wind through. Owned by GameSession (which
    // has to hand it to the emitter factories long before this rig exists), written here because
    // this is the class that holds the mission's weather and already ticks once per frame.
    private readonly EffectAmbience _ambience;
    // The session's viewer set, whose poses Tick publishes onto the ambience each frame for
    // the puffer distance fade. Constructor-injected beside _ambience because it is the same
    // shape of thing: a session-owned seam this class only WRITES THROUGH. An unbound set (no
    // GameSession, i.e. the suites' own rigs) publishes no camera, which is the no-fade path.
    private readonly ViewerSet _viewers;
    // The world's one DirectionalLight3D, whose bearing is the zone's authored
    // SUNLIGHT_ORIENTATION. A constructor argument rather than a SetX() like SetDeckZoneId /
    // SetFogVolumes, because ApplyZone cannot do its job without it, the others are optional
    // refinements to a rig that already works, this is not.
    private readonly DirectionalLight3D _sun;
    // The session's one Environment, written by both lighting arms: the ambient source, colour
    // and energy in enhanced mode, the ambient energy alone in the faithful path. Optional because
    // the suites build rigs without one, and a null simply leaves the ambient unwritten.
    private readonly Godot.Environment? _env;
    // One rig's deck tiles and which variant they currently carry, keyed by the deck node's
    // instance id, per rig, because each rig flies its own copy of the deck (AssignCloudDecks)
    // through its own altitude regime.
    private readonly Dictionary<ulong, DeckLighting> _deckLighting = new();
    // The deck Y last applied to each rig's OWN deck copy (keyed by instance id, same key
    // SetDeckDimmed uses), so Tick can log a change rather than every frame, the probe evidence
    // that the deck sits at its authored altitude rather than the band centre.
    private readonly Dictionary<ulong, float> _lastLoggedDeckY = new();
    // Per rig, the last in-volume whiteout density logged, so a probe's log says the curtain
    // engaged and how hard, without a line every frame. Only the crossings of the 0/non-0 boundary
    // and moves of 0.05 or more are said out loud.
    private readonly Dictionary<int, float> _lastLoggedVolumeWhiteout = new();
    // Extra (sun, env) pairs that mirror the session sun and Environment in enhanced mode, the
    // cockpit overlay's own clones, registered once each is built, so a zone crossing mid-flight
    // reaches the interior pass too rather than leaving it lit for the mission's first zone.
    private readonly List<(DirectionalLight3D Sun, Godot.Environment? Env)> _extraLighting = new();
    // The gate's other half: the authored world wears its zone_id from the build, a flown object
    // earns one per frame from its own altitude. Owned here because this class already holds the
    // mission's band and is the session's one owner of render visibility.
    private readonly ObjectZoneGate _objectGate = new();
    private readonly List<Node3D> _noObjects = new();

    private WeatherState? _weather;
    private string _activeZone;
    private Precipitation? _precip;
    private Vector3 _deckCenter;
    // The deck tiles' own authored altitude (WorldBuilder.CloudDeckAltitude), where the deck floor
    // sits, world-fixed, in both regimes.
    private float _deckAltitude;
    // The deck tiles' undimmed meshes by dimmed-mesh RID (WorldBuilder.CloudDeckUndimmedMeshes).
    private IReadOnlyDictionary<Rid, ArrayMesh> _deckUndimmedMeshes = new Dictionary<Rid, ArrayMesh>();
    private bool _loggedDeckLighting;
    // The chapter's fog-volume census + whether its fogvol.zrd arms fog_zone, the two inputs
    // CameraWeatherState's state-3 test needs. Set separately from Build for the same reason as
    // _deckCenter: this is chapter/world data (GameSession's fogVolumes census), not mission
    // weather, built once beside the world rather than per rig. Empty/false by default, which
    // simply never resolves to state 3 (most chapters).
    private IReadOnlyList<FogVolumeBox> _fogVolumes = Array.Empty<FogVolumeBox>();
    // The same chapter data read as the in-volume WHITEOUT, the two decompiled ramps, the union
    // and the authored fog_color (FogVolumeWhiteout). Disarmed everywhere but C5, where it costs
    // nothing: Density short-circuits on the flag before touching a volume.
    private FogVolumeWhiteout _fogWhiteout = FogVolumeWhiteout.Disarmed;
    // The fog edge trigger: which zone's fog globals are live, and the state they were applied for.
    // Rebuilt by LoadWeather (a new mission is a new zone table); disarmed outright by an explicit
    // --sky-zone.
    private FogStateTrigger _fogState = new(stateDriven: false, buildZone: string.Empty);
    // The zone gate for the one subtree SceneBuilder does NOT stamp with a zone layer because it is
    // a per-rig camera-anchored copy: the deck tiles' own gamez zone_id
    // (WorldBuilder.CloudDeckZoneId). -1 = ungated, which is what a chapter with no deck and a
    // legacy extraction both give, and it simply keeps the node visible at every state. The domes
    // are the other such subtree and carry their zone ids per dome, on the rig
    // (PlayerRig.HorizonDomes), there is one per built horizon zone, not one per rig.
    private int _deckZoneId = -1;

    // The mission's global wind, the WIND block's static vector plus its random-walk gust
    // (Effects.WorldWind). Still air until LoadWeather reads a weather.json, and still air for a
    // mission that has none.
    private WorldWind _wind = WorldWind.Still();

    // Whether Build has written its zone yet, and the FOG_STATE received before it did. The
    // world's animations start inside the world build, ahead of this rig.
    private bool _zoneWritten;
    private AnimRuntime.FogStateChange? _pendingFogState;

    // The session's drawn aircraft and zeppelins, read fresh each frame. A wave spawns and an
    // airframe is shot down long after this rig is built, so a held list goes stale.
    private Func<IReadOnlyList<Node3D>>? _gatedObjects;

    public WeatherRig(SessionSpec spec, Node3D worldRoot, DirectionalLight3D sun,
        EffectAmbience? ambience = null, ViewerSet? viewers = null, Godot.Environment? env = null)
    {
        _spec = spec;
        _worldRoot = worldRoot;
        _sun = sun;
        _env = env;
        _activeZone = spec.SkyZone;
        _ambience = ambience ?? new EffectAmbience();
        _viewers = viewers ?? new ViewerSet();
    }

    /// <summary>The faithful path's scene light with no mission weather to read: the sun's
    /// <c>LightEnergy</c> and the Environment's <c>AmbientLightEnergy</c> the launcher builds
    /// with. It is what <see cref="FaithfulEnergies"/> resolves for a zone authoring the install's
    /// modal day SUNLIGHT, so a mission carrying no weather.json lights like a day one.</summary>
    public static (float Sun, float Ambient) DefaultEnergies => (FaithfulSunEnergy, FaithfulAmbientEnergy);

    /// <summary>The install's modal day <c>SUNLIGHT</c> pair scaled by white, the shape
    /// <see cref="SunlightRgb"/> carries, for a session with no mission weather to read.</summary>
    public static (Vector3 Diffuse, Vector3 Ambient) DefaultSunlightRgb =>
        (Vector3.One * WeatherState.DefaultDiffuse, Vector3.One * WeatherState.DefaultAmbient);

    /// <summary>The fog this rig last wrote into the three <c>csky_fog_*</c> globals (colour in
    /// linear, range and altitude in metres). A mirror, because the renderer refuses to read a
    /// global back outside the editor; it is what a suite asserts a fog change by.</summary>
    public FogWritten FogGlobals { get; private set; }

    /// <summary>The per-object half of the zone gate, for the suite that asserts an object above
    /// the band stops drawing for a camera below it.</summary>
    public ObjectZoneGate ObjectGate => _objectGate;

    /// <summary>The applied zone's <c>SUNLIGHT_DIFFUSE</c>/<c>SUNLIGHT_AMBIENT</c>, each scaled
    /// by its own authored colour. The authored pair rather than the energies derived from it,
    /// for the reader that needs the light itself: the ground shadow's darkness is the light the
    /// aircraft blocks (<c>Flight/Airframe/GroundShadowLaw</c>).</summary>
    public (Vector3 Diffuse, Vector3 Ambient) SunlightRgb { get; private set; } = DefaultSunlightRgb;

    /// <summary>The deck's regime for one camera: the tiles' own world-fixed altitude in every
    /// regime, wearing the dimmed underside below <paramref name="bandCentre"/> and the undimmed
    /// top at or above it. Pure, so two cameras either side get independently correct answers.
    /// ⚠ Do not carry the deck above the camera as a ceiling, and do not gate the ambient cloud
    /// populations on altitude here. Both were tried and superseded (docs/org/weather.md).</summary>
    public static (float DeckY, bool DeckDimmed) DeckRegime(float cameraY, float bandCentre, float authoredY)
        => (authoredY, cameraY < bandCentre);

    /// <summary>The trailing zone number of a <c>zone1</c>/<c>zone2</c>/<c>zone3</c> name, or null
    /// for anything else, the <c>--sky-zone</c> STATE OVERRIDE. An explicit
    /// <c>--sky-zone=zoneN</c> forces the <see cref="CSVM.Mech3.ZoneGate"/> to state N as well as
    /// pinning the fog: the flag exists so an inspection pose reproduces, and a pose whose fog
    /// said <c>zone2</c> while its CONTENT was culled for state 1 would not be that. Public and
    /// static because it is the rule, not a helper.</summary>
    public static int? ZoneNumberOf(string zoneName)
    {
        if (zoneName.Length == 0)
            return null;
        int n = zoneName[^1] - '0';
        return n >= 1 && n <= ZoneGate.MaxZoneId ? n : null;
    }

    /// <summary>The enhanced-mode Godot energies for one zone's authored SUNLIGHT: the sun's
    /// <c>LightEnergy</c> and the Environment's <c>AmbientLightEnergy</c>. Pure, so the mapping is
    /// pinnable without a live scene, and so a viewport holding its own copy of the sun can
    /// resolve the same numbers.</summary>
    public static (float Sun, float Ambient) EnhancedEnergies(WeatherState.ZoneWeather fog)
    {
        bool night = IsNightZone(fog);
        float diffuse = night ? MathF.Min(fog.SunDiffuse, NightDiffuseCap) : fog.SunDiffuse;
        float ambient = night ? MathF.Min(fog.SunAmbient, NightAmbientCap) : fog.SunAmbient;
        return (diffuse * SunEnergyPerDiffuse, ambient * AmbientEnergyPerAuthored);
    }

    /// <summary>The faithful path's Godot energies for one zone's authored SUNLIGHT. Each scalar
    /// scales its own day-level energy and is capped there, so a dim zone darkens what the scene
    /// lights shade while a bright one keeps the day level. The in-flight aircraft does not read
    /// them; it draws <see cref="SunVertexLight"/>. Pure, so the mapping is pinnable without a
    /// live scene.</summary>
    public static (float Sun, float Ambient) FaithfulEnergies(WeatherState.ZoneWeather fog)
        => (FaithfulSunEnergy * MathF.Min(fog.SunDiffuse / WeatherState.DefaultDiffuse, 1f),
            FaithfulAmbientEnergy * MathF.Min(fog.SunAmbient / WeatherState.DefaultAmbient, 1f));

    /// <summary>The original's per-vertex sun term for one zone as its two colour triples, the
    /// ambient half and the half that <c>max(N·L, 0)</c> scales: the drawn diffuse is the authored
    /// vertex colour times <c>Ambient + Diffuse × max(N·L, 0)</c>, clamped at white. With
    /// <c>SUNLIGHT_BICOLORED</c> clear both halves carry the diffuse colour and the authored
    /// ambient colour is never read; set, each half carries its own (docs/org/vertexLighting.md).
    /// Pure, so the rule is pinnable without a live scene.</summary>
    public static (Vector3 Ambient, Vector3 Diffuse) SunVertexLight(WeatherState.ZoneWeather fog)
        => (Scaled(fog.SunAmbient, fog.SunBicolored ? fog.SunColorAmbient : fog.SunColorDiffuse),
            Scaled(fog.SunDiffuse, fog.SunColorDiffuse));

    /// <summary>The ambient scalar the Danger Zone photograph lights its pilot's aircraft with, off
    /// the zone's <c>SUNLIGHT_AMBIENT</c>: <c>ambient × 1.5 + 0.1</c> rounded to a byte, clamped to
    /// 0..255 and read back as a 1/255 fraction, which caps it at 1 (docs/formats/campaign-screens.md,
    /// "The fill light"). Double arithmetic, as the original's 53-bit x87 evaluates it. Pure.</summary>
    public static float PhotographFillAmbient(float ambient)
    {
        double level = ((((double)ambient * 1.5) + 0.1f) * 255.0) + 0.5;
        int stored = level >= 255.0 ? 255 : level <= 0.0 ? 0 : (int)level;
        return stored * 0.003921569f;
    }

    /// <summary>The photograph's ambient half as a colour triple: <see cref="PhotographFillAmbient"/>
    /// in place of the zone's ambient scalar, on the colour <see cref="SunVertexLight"/> gives that
    /// half. The diffuse half is the zone's own. Pure.</summary>
    public static Vector3 PhotographFill(WeatherState.ZoneWeather fog)
        => Scaled(PhotographFillAmbient(fog.SunAmbient), fog.SunBicolored ? fog.SunColorAmbient : fog.SunColorDiffuse);

    /// <summary>Whether a zone's authored <c>FOG_COLOR</c> puts it under a night sky, which is
    /// what caps its enhanced energies. Pure and public so the rule can be pinned and so a log
    /// line can say which side of it a zone fell on.</summary>
    public static bool IsNightZone(WeatherState.ZoneWeather fog)
        => (0.2126f * fog.FogColor.R) + (0.7152f * fog.FogColor.G) + (0.0722f * fog.FogColor.B)
           < NightFogLuminance;

    /// <summary>The fog range as written: the authored pair in the faithful path, pushed out by
    /// <c>EnhancedFogRangeScale</c> in enhanced mode so shadowed ground is not already grey. Pure
    /// and identity in original mode, which is what keeps that path byte-for-byte the authored
    /// one.</summary>
    public static Vector2 FogRangeFor(Vector2 authored)
        => GraphicsMode.Enhanced ? authored * EnhancedFogRangeScale : authored;

    /// <summary>The push factor <see cref="FogRangeFor"/> applies as a plain scalar (identity, 1,
    /// in original mode), so another distance-gated population can follow the same pushed fog
    /// without this class exposing <c>EnhancedFogRangeScale</c> itself.</summary>
    public static float EnhancedFogScale() => GraphicsMode.Enhanced ? EnhancedFogRangeScale : 1f;

    /// <summary>Paints one Environment's sky a single flat colour, enhanced mode's stand-in for
    /// the mission's horizon dome. Godot draws a reflection off a <c>Sky</c> resource only, so the
    /// colour goes in as a panorama; a background colour alone leaves the specular with no radiance
    /// at all. Public because <c>Launcher</c> builds the Environment.
    /// ⚠ Float format and an explicit linear value: an 8-bit texture's sRGB decode is the
    /// renderer's choice, and a wrong one shifts every reflection.</summary>
    public static void WriteSkyColor(Godot.Environment env, Color skyColor)
    {
        if (env.Sky?.SkyMaterial is not PanoramaSkyMaterial panorama)
            return;
        var image = Image.CreateEmpty(SkyPanoramaWidth, SkyPanoramaHeight, false, Image.Format.Rgbaf);
        image.Fill(skyColor.SrgbToLinear());
        panorama.Panorama = ImageTexture.CreateFromImage(image);
    }

    /// <summary>Registers a second (sun, env) pair, a cockpit overlay's cloned copies, so every
    /// future zone change reaches it too, not only the zone live when it was built. Both lighting
    /// arms mirror the LEVELS and COLOURS they resolve. <paramref name="env"/> may be null (a suite
    /// rig with no Environment). Excluded: the shadow max distance, a clone's camera having its own
    /// far plane, and the bearing, which <c>CockpitOverlay.Sync</c> takes off the world sun every
    /// frame in the pass's own basis.</summary>
    public void RegisterExtraLighting(DirectionalLight3D sun, Godot.Environment? env)
        => _extraLighting.Add((sun, env));

    /// <summary>Loads the mission's weather.json and resolves the rendered zone, builds the
    /// per-rig domes via <paramref name="buildDomes"/> (needs the resolved zone), then applies
    /// fog + whiteout + precipitation, in that order. ⚠ Do not reorder: each step depends on the
    /// zone the previous one resolved.</summary>
    public void Build(string missionZrdrPath, IReadOnlyList<PlayerRig> rigs,
        IReadOnlyList<HorizonZone> horizonZones, Action<string> buildDomes)
    {
        LoadWeather(missionZrdrPath, horizonZones);
        buildDomes(_activeZone);
        SetupWeather(rigs);
        _zoneWritten = true;
        if (_pendingFogState is { } pending)
        {
            _pendingFogState = null;
            ApplyFogState(pending);
        }
    }

    /// <summary>An animation's <c>FOG_STATE</c>: writes only the fields the event carries onto the
    /// fog globals <c>ApplyZone</c> owns, and the next camera-state edge writes the zone back over
    /// them, the original's own last-writer order (both go through one fog record, decoded in
    /// docs/org/weather.md). Before <see cref="Build"/> the event is held and applied after the
    /// zone. <c>--no-fog</c> keeps the range out of reach, as it does for the zone.</summary>
    public void ApplyFogState(AnimRuntime.FogStateChange fog)
    {
        if (!_zoneWritten)
        {
            _pendingFogState = fog;
            return;
        }
        if (fog.Color is { } color)
        {
            var linear = color.SrgbToLinear();
            WriteFogColor(new Vector3(linear.R, linear.G, linear.B));
        }
        if (fog.Range is { } range && !_spec.NoFog)
            // Through the same push the zone's range takes, or crossing a FOG_STATE edge in
            // enhanced mode would snap the haze back to the authored distance mid-flight.
            WriteFogRange(FogRangeFor(range));
        if (fog.Altitude is { } altitude)
            WriteFogAltitude(altitude);
        Log.Info("world", $"weather: FOG_STATE '{fog.Name}' over zone '{_activeZone}': {(fog.Color is { } c ? Log.Format($"fog {c.R:0.00} gray, ") : "")}{(fog.Range is { } r ? Log.Format($"range {r.X:0}–{r.Y:0} m, ") : "")}{(fog.Altitude is { } a ? Log.Format($"altitude {a.X:0}–{a.Y:0} m") : "")}{(_spec.NoFog ? " (--no-fog: range untouched)" : "")}");
    }

    /// <summary>The deck geometry's original AABB centre, so <see cref="Tick"/> can re-anchor it
    /// under each player every frame. Set separately from <see cref="Build"/> because the cloud
    /// deck is world geometry (<c>GameSession</c>'s <c>cloudDeck</c>), not weather state, and is
    /// built whenever a chapter world loads, not only when this rig itself gets built.</summary>
    public void SetDeckCenter(Vector3 center) => _deckCenter = center;

    /// <summary>The deck tiles' undimmed twin meshes
    /// (<c>WorldBuilder.CloudDeckUndimmedMeshes</c>), which is what lets <see cref="Tick"/> take
    /// the deck's SUNLIGHT dimming off above the cloud band (<c>DeckDimmed</c>). Set from
    /// the same place as <see cref="SetDeckCenter"/>, for the same reason, the deck is world
    /// geometry built with the chapter, not weather state. An empty map (a chapter with no deck,
    /// or a world built before this existed) simply leaves the deck as built.</summary>
    public void SetDeckUndimmedMeshes(IReadOnlyDictionary<Rid, ArrayMesh> undimmed)
    {
        _deckUndimmedMeshes = undimmed;
        _deckLighting.Clear();
        _loggedDeckLighting = false;
    }

    /// <summary>The chapter's fog-volume census and parsed <c>fogvol.zrd</c>. Set separately from
    /// <see cref="Build"/> for the same reason as <see cref="SetDeckCenter"/>: this is
    /// chapter/world data, not mission weather. Feeds <see cref="Tick"/>'s per-camera
    /// <see cref="WeatherState.CameraWeatherState"/> call and the in-volume
    /// <see cref="FogVolumeWhiteout"/>. Never called keeps every camera at state 1/2 and every
    /// frame's volume whiteout at 0.</summary>
    public void SetFogVolumes(IReadOnlyList<FogVolumeBox> volumes, FogVolumeSpec? spec)
    {
        _fogVolumes = volumes;
        _fogWhiteout = FogVolumeWhiteout.From(spec, volumes);
    }

    /// <summary>The cloud deck's own gamez <c>zone_id</c> (<c>WorldBuilder.CloudDeckZoneId</c>),
    /// the one piece of the zone gate that cannot ride a visual layer, because the deck is a
    /// per-rig camera-anchored copy. Set from the same place as <see cref="SetDeckCenter"/>, for
    /// the same reason: the deck is world geometry built with the chapter, not mission weather.
    /// Never called leaves the deck at −1, i.e. drawn at every state.</summary>
    public void SetDeckZoneId(int zoneId) => _deckZoneId = zoneId;

    /// <summary>The drawn objects the per-object zone gate moves between layers each frame: the
    /// session's aircraft and zeppelins, with whatever effect meshes hang under them. A delegate
    /// rather than a list because the roster changes all flight long. Never called leaves the gate
    /// idle, which is what a suite building a bare rig gets.</summary>
    public void SetGatedObjects(Func<IReadOnlyList<Node3D>> objects) => _gatedObjects = objects;

    /// <summary>The deck tiles' own authored altitude (<c>WorldBuilder.CloudDeckAltitude</c>),
    /// read off the built data. Set from the same place as <see cref="SetDeckCenter"/>, for the
    /// same reason: the deck is world geometry built with the chapter, not mission weather.
    /// <see cref="Tick"/> places the deck HERE rather than at
    /// <see cref="WeatherState.CloudBandCentre"/>, the two differ by 87 m in C1. Never called
    /// leaves the deck at 0, unread where <c>rig.Deck</c> is null.</summary>
    public void SetDeckAltitude(float altitude) => _deckAltitude = altitude;

    /// <summary>Everything decided per camera, once per rig: re-centers the skydome, fades the
    /// cloud-band whiteout, places the cloud deck in its regime, and applies the
    /// <see cref="CSVM.Mech3.ZoneGate"/> cull mask for that camera's weather state, plus
    /// <c>Node3D.Visible</c> on this rig's own deck and dome copies. Every camera in the session
    /// comes through here, including the freecam/probe camera, so a scripted shot obeys the same
    /// rules the player does.</summary>
    public void Tick(IReadOnlyList<PlayerRig> rigs)
    {
        // The flicker's rate input: null only outside a running session (GameClock.Current unset),
        // which freezes the flicker rather than pacing it off a wall clock, nothing calls Tick
        // there anyway.
        float frameDt = GameClock.Current?.FrameDt ?? 0f;

        // Stepped once per frame, ahead of the rig loop and everything that reads it, the
        // original has one wind for the world, and stepping it per rig would double a
        // splitscreen session's gust rate.
        _wind.Step(frameDt);
        _ambience.SetWind(_wind.Velocity);

        // Every pane's camera pose, from the session's viewer set rather than rigs[0]: the
        // puffer distance fade is a per-pane draw rule, so a trail near player 2 must draw in
        // player 2's pane regardless of what player 1 points at.
        _ambience.SetViewers(_viewers);

        // Each drawn object's own zone, resolved once for the whole session rather than per rig.
        // The verdict is the object's altitude against the band, which no camera takes part in.
        // Opened by --no-fog, since a band that hides nothing must not keep hiding aeroplanes.
        _objectGate.Tick(_gatedObjects?.Invoke() ?? _noObjects, _weather, _fogWhiteout.Armed,
            _fogVolumes, _spec.NoZoneCull || _spec.NoFog);

        foreach (var rig in rigs)
        {
            var camPos = rig.Camera.Position;

            // The binary's per-frame camera weather state (1/2/3), published on the rig; the fog
            // edge trigger below the loop consumes it. Logged only on a change, at debug
            // verbosity, since a flight spends whole minutes in one state.
            if (_weather != null)
            {
                int state = _weather.CameraWeatherState(camPos, _fogWhiteout.Armed, _fogVolumes);
                if (state != rig.CameraWeatherState)
                {
                    Log.Debug("world",
                        $"camera weather state: player {rig.Index} {rig.CameraWeatherState} -> {state}");
                    rig.CameraWeatherState = state;
                }
            }

            // Keeps the skydome a zero-parallax backdrop, like the original, so the moon stays
            // at its authored elevation instead of sliding into the horizon band as the plane
            // climbs. One-frame lag against the flight camera is invisible at range.
            if (rig.Horizon != null)
                rig.Horizon.Position = camPos;

            // One overlay, two sources: the CLOUD_COVER band's altitude whiteout, and, where
            // the chapter arms fog_zone, the fvol volumes' own curtain. See docs/org/weather.md
            // "Precedence: 3 beats 2".
            if (rig.Whiteout != null && _weather != null)
            {
                // The colour is re-read per frame, not set once at build: where the authored pair
                // differs it lerps across the band with the camera (CLOUD_COVER, weather.md).
                var c = _weather.WhiteoutColor(camPos.Y) ?? WhiteoutFallbackColor;
                // --no-fog covers the whiteout too: flying into the cloud band would otherwise
                // still white the pane out, which reads as "fog is not actually off". It covers
                // the volume curtain for the same reason.
                float band = _spec.NoFog ? 0f : _weather.WhiteoutAmount(camPos.Y);
                // The decompiled in-cloud flicker, applied only while the band opacity sits
                // strictly inside (0,1), the binary's own guard. See docs/org/weather.md.
                if (band > 0f && band < 1f)
                    band = ApplyBandFlicker(rig.Index, band, frameDt);
                float volume = _spec.NoFog ? 0f : _fogWhiteout.Density(camPos);
                if (volume > 0f)
                {
                    // Union of band and volume (a + b - a·b, the binary's own combiner), the
                    // curtain composited over the band as the nearer air. The two never coexist
                    // in shipped data (docs/org/weather.md), so this union is unmeasured today.
                    var volumeColor = _fogWhiteout.Color ?? _weather.CloudTopColor ?? WhiteoutFallbackColor;
                    float bandShare = band * (1f - volume);
                    float union = volume + bandShare;
                    c = volumeColor.Lerp(c, bandShare / union);
                    c.A = union;
                }
                else
                {
                    // The band alone, by construction, which is what keeps the seven disarmed
                    // chapters (and C5 away from its strips) on the plain band path.
                    c.A = band;
                }
                LogVolumeWhiteout(rig.Index, volume);
                rig.Whiteout.Color = c;
            }

            // The deck sits at its own authored altitude, X/Z-following the camera only, never
            // pinned to the band centre (docs/org/weather.md "The band-centre deck pin,
            // SUPERSEDED"), and docs/formats/fogvol.md has the fvol slab clearance.
            if (rig.Deck != null && _weather is { HasCloudBand: true } weather)
            {
                (float deckY, bool deckDimmed) = DeckRegime(camPos.Y, weather.CloudBandCentre, _deckAltitude);
                rig.Deck.Position = new Vector3(
                    camPos.X - _deckCenter.X,
                    deckY - _deckCenter.Y,
                    camPos.Z - _deckCenter.Z);
                // Logged only on a change, so the probe's log names the exact Y this rig's deck
                // copy renders at, the authored 960/1050, at every altitude. A SECOND line here
                // means the deck moved, and something is wrong.
                ulong deckId = rig.Deck.GetInstanceId();
                if (!_lastLoggedDeckY.TryGetValue(deckId, out float lastY) || !Mathf.IsEqualApprox(lastY, deckY))
                {
                    _lastLoggedDeckY[deckId] = deckY;
                    string regime = deckDimmed ? "below band, dimmed underside" : "at/above band, undimmed top";
                    Log.Debug("world",
                        $"deck: player {rig.Index} camera y={camPos.Y:0.0} -> deck y={deckY:0.0} ({regime})");
                }
                // Below the band centre the camera sees the overcast's dimmed underside, above it
                // the undimmed top, masked by the same opaque whiteout core the crossing sits in
                // (docs/org/weather.md "The cloud deck's two regimes").
                SetDeckDimmed(rig.Deck, deckDimmed);
            }

            // The original's zone_id visibility gate, applied per camera because splitscreen
            // panes can sit in different states at once. Cull mask for shared world content,
            // Visible for the deck/dome's own per-rig copies (docs/org/weather.md).
            int gate = _spec.SkyZoneExplicit
                ? ZoneNumberOf(_spec.SkyZone) ?? rig.CameraWeatherState
                : rig.CameraWeatherState;
            rig.Camera.CullMask = _spec.NoZoneCull
                ? ZoneGate.OpenCullMask(rig.Camera.CullMask)
                : ZoneGate.CullMask(rig.Camera.CullMask, gate);
            if (rig.Deck != null)
                rig.Deck.Visible = _spec.NoZoneCull || ZoneGate.Draws(_deckZoneId, gate);
            // One dome per horizon zone, showing only at its own state; a chapter with a single
            // built dome keeps it at every state rather than render no sky at all
            // (docs/org/weather.md).
            bool gateDomes = !_spec.NoZoneCull && rig.HorizonDomes.Count > 1;
            foreach (var dome in rig.HorizonDomes)
                dome.Node.Visible = !gateDomes || ZoneGate.Draws(dome.ZoneId, gate);
        }

        // The camera's zone, re-applied only at the state edge, never per frame, since the deck
        // rim annulus reads these same fog globals. Driven by rig 0 because these are global
        // shader uniforms, so splitscreen wears rig 0's zone (docs/org/weather.md).
        if (_weather != null && rigs.Count > 0
            && _fogState.Next(rigs[0].CameraWeatherState, _weather) is { } change)
        {
            if (change.FellBack)
                // Once per state, not per crossing: a mission with no authored ZONE<n> keeps the
                // file's first zone rather than rendering fullbright/no-fog. The one line
                // explaining a state change that moves nothing on screen.
                Log.Info("world", $"weather: {_spec.Chapter}/{_spec.Mission} authors no 'zone{change.State}' (zones: {string.Join("/", _weather.ZoneNames)}), camera state {change.State} keeps fog zone '{change.Zone}'");
            if (change.Applied)
            {
                var fog = _weather.Zone(change.Zone);
                ApplyZone(fog);
                Log.Info("world", $"weather: camera state {change.State} -> fog zone '{change.Zone}', fog {fog.FogNear:0}-{fog.FogFar:0} m, altitude {fog.FogLow:0}-{fog.FogHigh:0} m, world light {fog.WorldLight:0.00}, sun {Mathf.RadToDeg(fog.SunOrientation.X):0.#}°/{Mathf.RadToDeg(fog.SunOrientation.Y):0.#}° (dome built for '{_activeZone}'){LightSuffix(fog)}");
            }
        }
    }

    // The resolved energies said out loud beside the world light, so a zone log says which
    // lighting the flight got and which authored pair produced it. Both modes print, since the
    // faithful arm moves per zone too.
    private static string LightSuffix(WeatherState.ZoneWeather fog)
    {
        if (!GraphicsMode.Enhanced)
        {
            (float sun, float ambient) = FaithfulEnergies(fog);
            return Log.Format($"; scene sun energy {sun:0.00} (diffuse {fog.SunDiffuse:0.##}), ")
                   + Log.Format($"ambient energy {ambient:0.00} (ambient {fog.SunAmbient:0.##})");
        }

        (float sunEnergy, float ambientEnergy) = EnhancedEnergies(fog);
        return Log.Format($"; enhanced sun energy {sunEnergy:0.00} (diffuse {fog.SunDiffuse:0.##}), ")
               + Log.Format($"ambient energy {ambientEnergy:0.00} (ambient {fog.SunAmbient:0.##}), ")
               + Log.Format($"shadows to {FogRangeFor(new Vector2(fog.FogNear, fog.FogFar)).X:0} m")
               + (IsNightZone(fog) ? "; night zone, energies capped" : string.Empty);
    }

    // "zone2 0 meshes, zone1 3 meshes", the evidence the pick was made on, not just its result.
    private static List<string> HorizonZoneCounts(IReadOnlyList<HorizonZone> horizonZones)
    {
        var counts = new List<string>();
        foreach (var z in horizonZones)
            counts.Add($"{z.Name} {z.MeshedNodes} meshes");
        return counts;
    }

    // The energy/colour half of ApplyEnhancedLighting, shared by the session sun/env and every
    // registered clone, so the two can never drift onto different formulas.
    private static void ApplyEnhancedSunAndEnv(DirectionalLight3D sun, Godot.Environment? env,
        float sunEnergy, Color sunColor, float ambientEnergy, Color ambientColor, Color skyColor)
    {
        sun.LightEnergy = sunEnergy;
        sun.LightColor = sunColor;
        if (env == null)
            return;
        // The zone's own FOG_COLOR, which is the colour its horizon dome fades into and measures
        // within a few units of that dome as drawn (docs/org/weather.md). Glossy water reflects
        // this rather than a placeholder gradient, so a night zone mirrors its own sky.
        WriteSkyColor(env, skyColor);
        env.ReflectedLightSource = Godot.Environment.ReflectionSource.Bg;
        WriteColorAmbient(env, ambientColor, ambientEnergy);
    }

    // One Environment's ambient as a colour and an energy, the write both lighting arms share.
    // ⚠ The source is written on every apply and never left on AmbientSource.Sky: Godot then takes
    // the fill off the sky cubemap scaled by the background energy, which is a daylight procedural
    // gradient at night as well as by day, and ignores both values passed here.
    private static void WriteColorAmbient(Godot.Environment? env, Color ambientColor, float ambientEnergy)
    {
        if (env == null)
            return;
        env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        env.AmbientLightColor = ambientColor;
        env.AmbientLightEnergy = ambientEnergy;
    }

    // One authored SUNLIGHT scalar against its own colour, the triple the original's light class
    // holds and the ground shadow's colour reads.
    private static Vector3 Scaled(float intensity, Color colour) =>
        new(colour.R * intensity, colour.G * intensity, colour.B * intensity);

    // Says out loud that one rig's in-volume whiteout engaged, and how hard, the
    // evidence a probe reads, since a curtain that never fires and one that fires at 0.02 look
    // the same in a night frame. Only the 0 ↔ non-0 crossings and moves of 0.05 or
    // more are logged, so a pass through a street strip costs a handful of lines rather than one
    // per frame. Silent for every disarmed chapter, which never reaches a non-zero density.
    private void LogVolumeWhiteout(int rigIndex, float density)
    {
        if (!_fogWhiteout.Armed)
        {
            return;
        }
        bool known = _lastLoggedVolumeWhiteout.TryGetValue(rigIndex, out float last);
        if (known && (density > 0f) == (last > 0f) && Mathf.Abs(density - last) < 0.05f)
        {
            return;
        }
        if (!known && density <= 0f)
        {
            return;   // the resting state; the census line at build already says the rig is armed
        }
        _lastLoggedVolumeWhiteout[rigIndex] = density;
        Log.Debug("world", $"fvol whiteout: player {rigIndex} density {density:0.000}");
    }

    // This rig's flicker off the band's raw `WhiteoutAmount`, seeded from the
    // same Rng.Clouds stream every `--det` run re-derives identically,
    // created lazily so the first call for a rig is that rig's own frame 0 (the identity
    // guarantee lives in BandFlicker.Apply, not here).
    private float ApplyBandFlicker(int rigIndex, float op, float frameDt)
    {
        if (!_bandFlicker.TryGetValue(rigIndex, out var flicker))
        {
            // A per-instance System.Random off Rng.Clouds, not the shared Godot RNG stream: the
            // drift re-randomizes repeatedly over the instance's lifetime, and keeping it off a
            // native Godot object is what lets BandFlickerTests exercise it outside the engine.
            _bandFlicker[rigIndex] = flicker = new BandFlicker(Rng.NewSystemRandom(Rng.Clouds));
        }
        return flicker.Apply(op, frameDt);
    }

    // Puts one rig's deck copy into its regime's lit variant: the dimmed mesh each tile was
    // built with, or its undimmed twin. A per-instance mesh assignment, never a shared-material
    // change, so two splitscreen panes on opposite sides of the band can hold different variants
    // at once. The two meshes differ only in which shader the surface picked
    // (`SceneBuilder.BuildMesh`'s `lit`), so the swap moves no pixel but the brightness it exists
    // to change. Written only on a change.
    private void SetDeckDimmed(Node3D deck, bool dimmed)
    {
        ulong id = deck.GetInstanceId();
        if (!_deckLighting.TryGetValue(id, out var lighting))
        {
            _deckLighting[id] = lighting = CollectDeckTiles(deck);
        }
        if (lighting.Dimmed == dimmed)
        {
            return;
        }
        lighting.Dimmed = dimmed;
        foreach (var tile in lighting.Tiles)
        {
            tile.Instance.Mesh = dimmed ? tile.Dimmed : tile.Undimmed;
        }
    }

    // Resolves one deck copy's tiles to swap, once: every MeshInstance3D
    // under it whose mesh has a recorded undimmed twin. Deferred to the first
    // Tick rather than done at build because the splitscreen copies are made
    // after the world is built, and a copy's instances are its own nodes (they share the
    // resources, which is exactly what makes the RID lookup find them).
    private DeckLighting CollectDeckTiles(Node3D deck)
    {
        // As built: WorldBuilder gives every deck tile `forceLit: true`, the below-band variant, so
        // a deck that never ticks keeps the dimmed underside.
        var lighting = new DeckLighting { Dimmed = true };
        int instances = 0;
        Collect(deck);
        if (!_loggedDeckLighting)
        {
            _loggedDeckLighting = true;
            // Said out loud once per session: "0 of 144" is what a broken lookup looks like, and
            // it would otherwise be indistinguishable from a deck that is simply never above the
            // band.
            Log.Info("world", $"deck lighting: {lighting.Tiles.Count} of {instances} deck tile(s) carry an undimmed twin, the sheet keeps SUNLIGHT below the cloud band, drops it above");
        }
        if (lighting.Tiles.Count != instances)
        {
            GD.PushWarning($"deck lighting: {instances - lighting.Tiles.Count} deck tile(s) have no "
                           + "undimmed twin and will stay dimmed above the cloud band");
        }
        return lighting;

        void Collect(Node node)
        {
            // The rim extension under this node is not a deck tile and carries no undimmed twin;
            // skip it by its meta tag so the "N of M" census below counts tiles only.
            if (node is MeshInstance3D mi && !mi.HasMeta(WorldBuilder.DeckExtensionMeta))
            {
                instances++;
                if (mi.Mesh is { } mesh && _deckUndimmedMeshes.TryGetValue(mesh.GetRid(), out var undimmed))
                {
                    lighting.Tiles.Add((mi, mesh, undimmed));
                }
            }
            foreach (var child in node.GetChildren())
            {
                Collect(child);
            }
        }
    }

    // Resolves _activeZone, the zone the fog and skydome both build from, the mission's own
    // zone names first, then, for the default request only, the chapter's horizon geometry
    // (a bare-marker dome yields to a zone that has one). An explicit --sky-zone= is honoured
    // literally, empty dome and all, see docs/cli.md. Called before the domes because C5's
    // zone1+zone3 chapter needs the fallback before the zone2 default renders neither.
    private void LoadWeather(string missionZrdrPath, IReadOnlyList<HorizonZone> horizonZones)
    {
        _weather = WeatherState.Load(missionZrdrPath);
        // The mission's WIND block, read per mission rather than baked, see docs/org/weather.md
        // for the authored values every weather.zrd shares. A mission with no weather.json gets
        // still air, not someone else's breeze.
        _wind = _weather == null
            ? WorldWind.Still()
            : new WorldWind(_weather.WindStatic, _weather.WindRandomMaxSpeed,
                _weather.WindRandomAccel, _weather.WindRandomAngVel,
                Rng.NewSystemRandom(Rng.Wind));
        if (_weather != null)
            // Said out loud once per session: a puffer that drifts sideways for no visible reason
            // is otherwise indistinguishable from a broken spawn, and this is the one line that
            // names the force doing it.
            Log.Info("world", $"wind: static ({_weather.WindStatic.X:0.##}, {_weather.WindStatic.Y:0.##}, {_weather.WindStatic.Z:0.##}) m/s, gust <= {_weather.WindRandomMaxSpeed:0.##} m/s (step {_weather.WindRandomAccel:0.##} m/s per frame, turning {_weather.WindRandomAngVel:0.##} deg/s), carries every puffer with FRICTION by its WIND_FACTOR");
        string byFile = _weather?.ResolveZone(_spec.SkyZone) ?? _spec.SkyZone;
        _activeZone = _spec.SkyZoneExplicit
            ? byFile
            : _weather?.ResolveZone(_spec.SkyZone, horizonZones)
              ?? WeatherState.PreferPopulatedHorizonZone(byFile, horizonZones);
        if (!_activeZone.Equals(byFile, StringComparison.OrdinalIgnoreCase))
            // The horizon correction. Printed with the counts it was decided on, because this is
            // the one line that says which sky and which fog the flight actually got.
            Log.Info("world", $"weather: {_spec.Chapter} builds no horizon geometry under '{byFile}' ({string.Join(", ", HorizonZoneCounts(horizonZones))}), rendering '{_activeZone}' sky and fog");
        // The fog zone follows the camera's weather state from here, starting at the zone Build
        // just resolved. An explicit --sky-zone disarms the machine entirely, so an inspection
        // pose renders one named zone reproducibly.
        _fogState = new FogStateTrigger(stateDriven: !_spec.SkyZoneExplicit, buildZone: _activeZone);
        if (_weather == null)
        {
            GD.PushWarning($"no weather.json for {_spec.Chapter}/{_spec.Mission}, flying without fog / whiteout");
            return;
        }
        if (!byFile.Equals(_spec.SkyZone, StringComparison.OrdinalIgnoreCase))
            // Not a fault: a chapter that numbers its zones differently resolves here every
            // flight. C5 (zone1/zone3) does so on all 8 missions, and zone1 is the confirmed
            // correct choice there, so this must not read as a missing-data warning.
            Log.Info("world", $"weather: {_spec.Chapter}/{_spec.Mission} has no '{_spec.SkyZone}' (zones: {string.Join("/", _weather.ZoneNames)}), rendering '{_activeZone}'");
    }

    // Applies the loaded weather: sets the distance-fog global shader parameters for
    // the rendered sky zone (all world + aircraft surfaces pick them up), and builds the
    // full-screen cloud-band whiteout overlay (its opacity is driven each frame from the camera
    // altitude in Tick). No-op if the mission has no weather.json, the fog
    // globals keep their registered no-op range. Call LoadWeather first.
    private void SetupWeather(IReadOnlyList<PlayerRig> rigs)
    {
        if (_weather == null)
            return;
        var fog = _weather.Zone(_activeZone);
        var fogRange = ApplyZone(fog);
        Log.Info("world", $"weather [{_activeZone}]{(_spec.NoFog ? " --no-fog: fog + whiteout OFF, world light unchanged;" : ":")} fog {fog.FogColor.R:0.00} gray {fogRange.X:0}–{fogRange.Y:0} m (authored {fog.FogNear:0}–{fog.FogFar:0}), altitude {fog.FogLow:0}–{fog.FogHigh:0} m; world light {fog.WorldLight:0.00}; sun {Mathf.RadToDeg(fog.SunOrientation.X):0.#}° pitch / {Mathf.RadToDeg(fog.SunOrientation.Y):0.#}° yaw; cloud band {_weather.CloudBottom:0}–{_weather.CloudTop:0} m (±{_weather.CloudThickness:0}){LightSuffix(fog)}");
        if (_fogWhiteout.Armed)
        {
            // Said out loud once per session, because "the curtain never fired" and "the chapter
            // never armed it" are the same picture otherwise. Only C5 prints it.
            var wc = _fogWhiteout.Color ?? _weather.CloudTopColor ?? WhiteoutFallbackColor;
            Log.Info("world", $"fvol whiteout: armed, {_fogVolumes.Count} volume(s), approach {_fogWhiteout.FadeDist:0.#} m, interior decay {_fogWhiteout.InteriorFadeDist:0.#} m, colour {wc.ToHtml(false)}{(_fogWhiteout.Color == null ? " (CLOUD_COVER TOP_COLOR default)" : " (authored)")}");
        }
        SetupWhiteoutAndPrecip(rigs);
    }

    // Applies one zone: the fog globals, the SUNLIGHT-derived world light, and the sun's
    // bearing, returning the fog range written. Called by SetupWeather at build and by Tick on
    // every zone change; every write below is idempotent, letting the second caller be an edge
    // trigger. ⚠ Keep fog and light in one method, splitting would let one drift from the other.
    // ⚠ Every write below is GlobalShaderParameterSet, never Add: Add runs once per process in
    // Launcher._Ready, so a second Add on an in-process relaunch of a foggy mission crashes.
    private Vector2 ApplyZone(WeatherState.ZoneWeather fog)
    {
        // FOG_COLOR is a DX7-era sRGB framebuffer value; the shader mixes ALBEDO in linear
        // space, so convert here. See docs/org/weather.md for the 176-gray measurement.
        var fogLinear = fog.FogColor.SrgbToLinear();
        WriteFogColor(new Vector3(fogLinear.R, fogLinear.G, fogLinear.B));
        // ⚠ Do not scale the authored fog ranges in the FAITHFUL path: VIEWING_RANGE ships
        // FOG_SCALE 1.0 at HIGH in every chapter and no screenshot residual licenses re-opening it
        // (docs/org/weather.md). FogRangeFor is identity there; only enhanced mode pushes them out.
        var fogRange = _spec.NoFog
            ? new Vector2(1e8f, 1e9f)   // out of reach; --no-fog writes the range, not the unused csky_fog_on toggle
            : FogRangeFor(new Vector2(fog.FogNear, fog.FogFar));
        WriteFogRange(fogRange);
        // FOG_ALTITUDE: the fog cylinder's vertical extent, full fog below FogLow, fading to
        // none at FogHigh (FRAGMENT altitude, settled in C2 at the controls of the original,
        // see csky_atmosphere.gdshaderinc and docs/org/weather.md).
        WriteFogAltitude(new Vector2(fog.FogLow, fog.FogHigh));
        // World brightness from the zone's SUNLIGHT, applied as a scalar on the fullbright
        // world/deck/dome. Applied in gamma space, matching the DX7 baked-lighting chain, see
        // docs/org/weather.md for the measured gamma-vs-linear difference.
        float worldLightLinear = new Color(fog.WorldLight, fog.WorldLight, fog.WorldLight).SrgbToLinear().R;
        RenderingServer.GlobalShaderParameterSet("csky_world_light", worldLightLinear);
        // The zone's authored SUNLIGHT_ORIENTATION, adopted unconditionally, no tune, no clamp.
        // It shades aircraft only; the world is fullbright and casts no shadow from it.
        // ⚠ One light for the whole session: in splitscreen both panes wear rig 0's zone.
        _sun.Rotation = fog.SunOrientation;
        // The same bearing and the uncollapsed pair, for the lit cloud cards, which shade per
        // vertex off normals that turn with the camera (docs/org/vertexLighting.md). Both modes:
        // a card is unshaded. csky_sun_dir points toward the light, as max(N.L, 0) wants.
        var toSun = (_sun.IsInsideTree() ? _sun.GlobalBasis : _sun.Basis).Z.Normalized();
        RenderingServer.GlobalShaderParameterSet("csky_sun_dir", toSun);
        RenderingServer.GlobalShaderParameterSet("csky_sun_light",
            new Vector2(fog.SunAmbient, fog.SunDiffuse));
        // The same term with its colours, for the faithful aircraft, which carries no collapse.
        (Vector3 ambientRgb, Vector3 diffuseRgb) = SunVertexLight(fog);
        RenderingServer.GlobalShaderParameterSet("csky_sun_ambient_rgb", ambientRgb);
        RenderingServer.GlobalShaderParameterSet("csky_sun_diffuse_rgb", diffuseRgb);
        RenderingServer.GlobalShaderParameterSet("csky_sun_fill_rgb", PhotographFill(fog));
        // The authored pair itself, published for the ground shadow, which derives its darkness
        // from the light rather than from either mode's energies.
        SunlightRgb = (Scaled(fog.SunDiffuse, fog.SunColorDiffuse),
            Scaled(fog.SunAmbient, fog.SunColorAmbient));
        if (GraphicsMode.Enhanced)
            ApplyEnhancedLighting(fog);
        else
            ApplyFaithfulLighting(fog);
        return fogRange;
    }

    // Enhanced mode only: the authored SUNLIGHT drives a real sun and the Environment ambient
    // rather than the fullbright dimming scalar, which is written back to 1.0 so the billboards
    // and clutter that still read the global are not dimmed a second time.
    private void ApplyEnhancedLighting(WeatherState.ZoneWeather fog)
    {
        RenderingServer.GlobalShaderParameterSet("csky_world_light", 1f);
        // Shadows end where this zone's haze BEGINS, off the AUTHORED near, so a shadow fades out
        // before the ramp rather than mixing with it (docs/architecture.md). The session sun only:
        // a registered clone owns its own camera-relative distance (RegisterExtraLighting).
        if (fog.FogFar > 0f)
        {
            _sun.DirectionalShadowMaxDistance = FogRangeFor(new Vector2(fog.FogNear, fog.FogFar)).X;
            _sun.DirectionalShadowFadeStart = EnhancedShadowFadeStart;
        }
        (float sunEnergy, float ambientEnergy) = EnhancedEnergies(fog);
        ApplyEnhancedSunAndEnv(_sun, _env, sunEnergy, fog.SunColorDiffuse, ambientEnergy,
            fog.SunColorAmbient, fog.FogColor);
        foreach (var (sun, env) in _extraLighting)
            ApplyEnhancedSunAndEnv(sun, env, sunEnergy, fog.SunColorDiffuse, ambientEnergy,
                fog.SunColorAmbient, fog.FogColor);
    }

    // The faithful path's half of the zone apply: the authored SUNLIGHT pair drives the scene sun
    // and ambient, which reach only the Godot-shaded surfaces. The in-flight aircraft takes the
    // csky_sun_* globals instead, and the fullbright world takes csky_world_light.
    private void ApplyFaithfulLighting(WeatherState.ZoneWeather fog)
    {
        (float sunEnergy, float ambientEnergy) = FaithfulEnergies(fog);
        _sun.LightEnergy = sunEnergy;
        WriteColorAmbient(_env, fog.SunColorAmbient, ambientEnergy);
        foreach (var (sun, env) in _extraLighting)
        {
            sun.LightEnergy = sunEnergy;
            WriteColorAmbient(env, fog.SunColorAmbient, ambientEnergy);
        }
    }

    // The per-rig whiteout overlays and the mission's precipitation field, the half of
    // SetupWeather that builds NODES rather than writing global shader parameters,
    // split out so the zone-apply half (ApplyZone) can be re-run on a camera-state
    // change without rebuilding either.
    private void SetupWhiteoutAndPrecip(IReadOnlyList<PlayerRig> rigs)
    {
        if (_weather == null)
            return;
        // ⚠ Built for a reachable CLOUD_COVER band OR an armed fog_zone, so the volume curtain
        // always has a surface to paint on. No shipped chapter needs both; the OR exists so they
        // cannot drift apart.
        if (_weather.HasCloudBand || _fogWhiteout.Armed)
        {
            // One overlay per rig: in splitscreen it must dim only the pane whose player is
            // inside the cloud. The ambient cloud field is not here, see Effects/FogVolumeClutter.
            foreach (var rig in rigs)
            {
                // A pane-filling overlay so the whiteout swallows everything outside (terrain,
                // plane, clouds) uniformly, like the original. Its layer keeps it behind the HUD
                // and behind the cockpit pass, which is what leaves the interior clear.
                var canvas = new CanvasLayer { Layer = UI.Boards.HudLayers.Whiteout, Name = "whiteout" };
                rig.Whiteout = new ColorRect
                {
                    Color = new Color(
                        _weather.WhiteoutColor(_weather.CloudBottom) ?? WhiteoutFallbackColor, 0f),
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                rig.Whiteout.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                canvas.AddChild(rig.Whiteout);
                rig.HudParent.AddChild(canvas);
                rig.WorldOverlays.Add(canvas);
            }
        }

        // Rain/snow: only missions whose weather.json carries a TYPE block get a field, and it
        // shows below the CLOUD_COVER band only. Self-animating off csky_time, see this
        // module's own docs/architecture.md entry.
        _precip = Precipitation.Create(_weather.Precip, _weather.CloudBottom, _weather.CloudTop);
        if (_precip != null)
            _worldRoot.AddChild(_precip);
    }

    private void WriteFogColor(Vector3 linear)
    {
        RenderingServer.GlobalShaderParameterSet("csky_fog_color", linear);
        FogGlobals = FogGlobals with { ColorLinear = linear };
    }

    private void WriteFogRange(Vector2 range)
    {
        RenderingServer.GlobalShaderParameterSet("csky_fog_range", range);
        FogGlobals = FogGlobals with { Range = range };
    }

    private void WriteFogAltitude(Vector2 altitude)
    {
        RenderingServer.GlobalShaderParameterSet("csky_fog_alt", altitude);
        FogGlobals = FogGlobals with { Altitude = altitude };
    }

    /// <summary>The three fog globals as last written (<see cref="FogGlobals"/>).</summary>
    public readonly record struct FogWritten(Vector3 ColorLinear, Vector2 Range, Vector2 Altitude);

    /// <summary>What one camera-state change asks the fog chain to do: which state it is, the zone
    /// that state resolved to, whether the globals need re-writing at all
    /// (<see cref="Applied"/>, false when two states share a zone through the file fallback), and
    /// whether that resolution WAS a fallback, reported once per state
    /// (<see cref="FellBack"/>).</summary>
    public readonly record struct FogZoneChange(int State, string Zone, bool Applied, bool FellBack);

    /// <summary>The edge trigger between <c>PlayerRig.CameraWeatherState</c> and the fog globals:
    /// answers which zone's fog is now live, and <c>null</c> every other frame.
    /// ⚠ Edge-triggered, not per-frame-recomputed, the deck rim annulus reads these same
    /// globals, and a per-frame rewrite would shimmer at the boundary. <see cref="Applications"/>
    /// lets a test assert the count, since a repeated write is invisible otherwise.
    /// Stays silent when <paramref name="stateDriven"/> is false (an explicit <c>--sky-zone</c>)
    /// or when the resolved zone is already live (a mission with no <c>ZONE2</c> falls back).</summary>
    public sealed class FogStateTrigger
    {
        private readonly bool _stateDriven;
        private readonly HashSet<int> _fallbacksReported = new();
        private string _zone;
        private int _state;

        /// <param name="stateDriven">False pins the fog to <paramref name="buildZone"/> for the
        /// whole flight (an explicit <c>--sky-zone</c>).</param>
        /// <param name="buildZone">The zone <c>WeatherRig.Build</c> already applied, the trigger
        /// starts live on it, so a chapter whose state-1 zone is that same zone never writes a
        /// global.</param>
        public FogStateTrigger(bool stateDriven, string buildZone)
        {
            _stateDriven = stateDriven;
            _zone = buildZone;
        }

        /// <summary>How many times this trigger has asked for the globals to be re-written.</summary>
        public int Applications { get; private set; }

        /// <summary>The zone whose fog is live.</summary>
        public string Zone => _zone;

        /// <summary>The camera's state this frame; null unless it CHANGED since the last call
        /// (and always null when the trigger is not state-driven).</summary>
        public FogZoneChange? Next(int state, WeatherState weather)
        {
            if (!_stateDriven || state == _state)
                return null;
            _state = state;
            var zone = weather.ZoneForState(state);
            bool fellBack = !zone.Equals(
                "zone" + state.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase);
            bool applied = !zone.Equals(_zone, StringComparison.OrdinalIgnoreCase);
            if (applied)
            {
                _zone = zone;
                Applications++;
            }
            return new FogZoneChange(state, zone, applied, fellBack && _fallbacksReported.Add(state));
        }
    }

    /// <summary>The decompiled in-cloud flicker: remaps the band's raw opacity through
    /// <see cref="LogCurve"/>/<see cref="AtanCurve"/> blended by a parameter that drifts in
    /// [0,1], re-randomizing its speed and sign-flipping at each bound.
    /// ⚠ Frame 0 is not <c>t = 0</c>. Neither curve is the identity at an interior opacity, so a
    /// fresh instance instead ramps the blended remap's amplitude in from 0 over
    /// <see cref="RampFrames"/> calls, keeping <see cref="Apply"/>'s first call bit-for-bit
    /// identity, what the golden shots and <c>FlatColorTests</c>/<c>DeckRegimeTests</c> pins
    /// were measured against.</summary>
    public sealed class BandFlicker
    {
        // ⚠ TUNE, the decompile's rate multiplier is read from a per-mission weather-struct field
        // (≈ +0x934) that no reader decodes and no capture pins a value for. Picked so the MIDPOINT
        // of the re-randomized drift speed (0.2..1.0, mean 0.6) traverses the full [0,1] range in a
        // few seconds: 5.5 * 0.6 * 0.1 = 0.33/s -> ~3 s at the mean, 1.8-9.2 s across the
        // randomized range.
        public const float DefaultRate = 5.5f;

        // ⚠ TUNE, how many `Apply` calls (sim frames) the amplitude ramps in over: 30 = 0.5 s
        // at the fixed 60 Hz `--det` step. Long enough that the ramp itself is not the shimmer;
        // short enough that a flight spends effectively none of its time in it.
        public const int RampFrames = 30;

        private readonly Random _rng;
        private float _t;
        private float _driftSpeed;
        private long _framesSeen;

        public BandFlicker(Random rng)
        {
            _rng = rng;
            _driftSpeed = RandomDriftSpeed(rng);
        }

        /// <summary>The blend parameter's current value, exposed for the determinism test, not
        /// consumed by <see cref="WeatherRig"/>.</summary>
        public float T => _t;

        /// <summary>The log-shaped curve: <c>ln(op*5+1)/ln(6)</c>, the binary computes both terms
        /// as <c>log2</c>, but the base cancels in the ratio, so natural log reproduces it
        /// exactly.</summary>
        public static float LogCurve(float op) => MathF.Log((op * 5f) + 1f) / MathF.Log(6f);

        /// <summary>The atan-shaped curve: <c>(atan((op-0.5)*10)+0.5)/(atan(5)+0.5)</c>, the
        /// binary's remap of the raw opacity parameter.</summary>
        public static float AtanCurve(float op) =>
            (MathF.Atan((op - 0.5f) * 10f) + 0.5f) / (MathF.Atan(5f) + 0.5f);

        /// <summary>The two curves blended by <paramref name="t"/> and clamped to [0,1], the
        /// binary's <c>fVar3 = (logCurve - atanCurve) * t + atanCurve</c> followed by its own
        /// clamp. Both curves agree at <c>op=1</c> (both equal 1) and the clamp forces <c>op=0</c>
        /// to 0 regardless of <paramref name="t"/> (<c>AtanCurve(0)</c> is negative), which is why
        /// the remap never moves the band's hard edges, only its interior.</summary>
        public static float Remap(float op, float t) =>
            Mathf.Clamp(AtanCurve(op) + (t * (LogCurve(op) - AtanCurve(op))), 0f, 1f);

        /// <summary>Advances the drift by one frame and returns the flickered opacity, identity on
        /// the very first call (see the class summary), and identity again whenever
        /// <paramref name="op"/> sits exactly at 0 or 1 (the binary's guard: the whole remap block,
        /// including the drift update, is skipped there, so a band pinned at a hard edge never
        /// drifts).</summary>
        public float Apply(float op, float frameDt, float rate = DefaultRate)
        {
            if (op <= 0f || op >= 1f)
                return op;

            float ramp = Mathf.Clamp(_framesSeen / (float)RampFrames, 0f, 1f);
            _framesSeen++;

            _t += rate * frameDt * _driftSpeed * 0.1f;
            if (_t > 1f || _t < 0f)
            {
                float mag = RandomDriftSpeed(_rng);
                if (_t <= 1f)
                {
                    _t = 0f;
                    _driftSpeed = mag;
                }
                else
                {
                    _t = 1f;
                    _driftSpeed = -mag;
                }
            }

            float remapped = Remap(op, _t);
            return op + (ramp * (remapped - op));
        }

        private static float RandomDriftSpeed(Random rng) => 0.2f + ((float)rng.NextDouble() * 0.8f);
    }

    // ONE rig's deck copy, resolved for the lit-variant swap: its tiles with both
    // meshes each, and which variant they are carrying now (null until the first
    // Tick decides). Per deck copy, not per session, see
    // SetDeckDimmed.
    private sealed class DeckLighting
    {
        public List<(MeshInstance3D Instance, Mesh Dimmed, Mesh Undimmed)> Tiles { get; } = new();

        public bool? Dimmed { get; set; }
    }
}
