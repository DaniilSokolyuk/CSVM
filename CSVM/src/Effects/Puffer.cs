using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CSVM.Flight.Camera;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Effects;

/// <summary>The three camera-distance switches plus the original's own far-band multiplier, forced
/// instead of read from <c>config.json</c>. Only <see cref="Puffer.CreateWith"/>, the test entry
/// point, accepts one; the real path always reads Config, whose keys default to exactly the
/// original's behaviour (<c>true</c>, <c>true</c>, <c>true</c>,
/// <see cref="Puffer.GlobalFadeFactorDefault"/>).</summary>
public readonly record struct PufferFadeSwitches(
    bool DistanceFade, bool FarCull, bool NearCull, float GlobalFadeFactor = 1f);

/// <summary>A running instance of a <see cref="PufferState"/>: a CPU-simulated burst of billboard
/// sprites, handed one frame at a time to an <see cref="IEmitterRenderer"/>. The CPU integration
/// honours the reader parameters directly, random velocity, friction, size growth, and the
/// non-uniform flipbook timing, which map awkwardly onto Godot's built-in particle material.
/// Everything past that integration is behind the renderer seam, so <see cref="CreateWith"/> can
/// reach all three emission modes with no GPU and no <c>TextureArchive</c>. See
/// <c>docs/architecture.md</c> for plumbing and <c>docs/org/puffer.md</c> for the decode.
/// </summary>
public sealed partial class Puffer : Node3D
{
    /// <summary>Default for config.json <c>puffer.*SizeScale</c> (per-spawn-path size multipliers).
    /// Not a tune: <c>SIZE_RANGE</c> is a screen-space half-extent, so the sprite spans
    /// <c>2 × SIZE_RANGE</c>, this is the decoded radius→diameter conversion. See
    /// <c>docs/org/puffer.md</c>.</summary>
    public const float SizeScaleDefault = 2f;

    // ⚠ Do not add a rise/lifetime multiplier for the fire family (`puffer.fireRiseScale`,
    // `puffer.fireLifetimeScale`). A prior tune of this kind measured 2.11x too tall against
    // footage. The authored numbers reach on their own, see docs/org/puffer.md, "The fire pair".

    /// <summary>Default for config.json <c>puffer.globalFadeFactor</c> (<c>PufferSetGlobalFadeFactor</c>
    /// in the original). Multiplies only the FAR band's measured distance; near comparisons stay
    /// unscaled. No authored writer in this install. See <c>docs/org/puffer.md</c>.</summary>
    public const float GlobalFadeFactorDefault = 1f;

    /// <summary>The <c>K</c> in the original's <c>1 + K·PRIORITY</c> screen-radius scale. The
    /// engine constant is <c>0.01</c> on the software path and <c>0.02</c> on the hardware one,
    /// this project has no software path (see <see cref="DistanceAlpha"/>'s remark on the same
    /// split), so this is the hardware value. Default <c>PRIORITY</c> is 0, so the factor is 1
    /// unless authored.</summary>
    public const float PriorityScaleDefault = 0.02f;

    // Alpha-weighted mean luminance (0–1) below which the sprite a particle dies on counts as
    // smoke. ⚠ This decides only the soft-particle depth fade, never the blend: blend is the
    // texture's own additive bit (docs/org/textures.md). Dark sprites sit at ground-level sites
    // where the fade would zero every fresh puff against the terrain behind it, and the measured
    // population separates cleanly here, fire_f06 0.018 and thickblksmoke 0.004 below, nothing
    // above it under 0.12. See docs/org/puffer.md.
    private const float SmokeLuminance = 16f / 255f;

    // Life-fade envelope (a render nicety, not in the reader): ease the additive glow in
    // and out so particles don't pop at spawn/death. The flipbook itself already dims.
    private const float FadeIn = 0.12f, FadeOutStart = 0.6f;

    private const int TrailPool = 640; // starting pool for trail emitters, grown on demand
    // A sustained emitter never stops, so its pool is sized to the steady-state population
    // (Number per interval, each living up to LifetimeMax) rather than to a burst duration.
    private const int SustainPoolMin = 16, SustainPoolMax = 2048;
    // Where a continuous emitter's pool stops doubling (GrowPool). INVENTED, like every pool size
    // here: the engine has no cap. Sized above the smoke screen's steady state with headroom.
    private const int ContinuousPoolMax = 8192;
    // The original's teleport guard on the DISTANCE path: it
    // accumulates the frame's motion length into the emitter's interval counter only
    // `if (len &lt; 200.0)`, a respawned or pooled emitter that jumps across the world
    // lays no line of puffs along the jump. The time path has no equivalent test: there the
    // engine accumulates `dt` unconditionally.
    private const float TeleportGuardMeters = 200f;

    // The view depth a particle at or behind the eye plane sorts at, so it draws first. The
    // original's key is `ftol(0.01 / minRHW)`, integer hundreds of metres, and such a quad takes
    // the sentinel 999 instead; 99,900 m is that sentinel in depth. Unreachable while the near
    // cull is on, which drops those particles before the sort sees them. See docs/org/textures.md.
    private const float BehindEyeSortDepth = 99_900f;

    // Baked atlases per archive, keyed by the frame list: a state many emitters share (a crash
    // rig builds 28 lgpuffers ahead) bakes and measures its frames once. Keyed by the archive so
    // a chapter change can never serve another chapter's frames.
    private static readonly ConditionalWeakTable<TextureArchive, Dictionary<string, (ImageTexture Atlas, bool DiesDark)>> AtlasCache = new();

    // Particle spread/size/life/frame jitter. One stream per emitter, drawn off the master seed's
    // puffer stream, so a run repeats and two emitters still scatter independently.
    private readonly System.Random _rng = Rng.NewSystemRandom(Rng.Puffer);

    // Dev-tunable BaseSize multipliers, one per spawn path (burst = SpawnBatch, trail and sustain
    // = SpawnSustained by way of TrailAdvance / SustainAt). Read once per emitter at Init; --det drops
    // config overrides, so scripted captures stay a function of the committed tree.
    private float _burstSizeScale = SizeScaleDefault;
    private float _trailSizeScale = SizeScaleDefault;
    private float _sustainSizeScale = SizeScaleDefault;

    // 1 + K·PRIORITY, read once at Init off the authored state, not a config knob, since it
    // is a decoded engine constant rather than a tuning surface.
    private float _priorityFactor = 1f;

    // The three distance switches (config.json puffer.distanceFade / farCull / nearCull), read
    // once at Init. All default TRUE, every one of them reproduces the original, and a flag that
    // shipped off would be a silent divergence wearing a config key. See DistanceAlpha.
    private bool _distanceFade = true;
    private bool _farCull = true;
    private bool _nearCull = true;
    private float _globalFadeFactor = GlobalFadeFactorDefault;

    // 1/(end - start) per band, precomputed at Init exactly as the original's setters do at
    // set time, and, as they do, left as the raw difference (0) when the two ends are equal,
    // which is the unauthored far band's FLT_MAX/FLT_MAX case.
    private float _nearRecip;
    private float _farRecip;

    private PufferState _state = null!;
    private IEmitterRenderer _renderer = null!;
    // The world state this emitter reads but does not own (the wind and the camera position).
    // Still air unless a caller wired the session's own, see EffectAmbience.
    private EffectAmbience _ambience = EffectAmbience.Still;
    private Particle[] _particles = Array.Empty<Particle>();
    private int _liveCount;
    private int _drawnCount;

    // The frame's draw payloads and their sort keys, pooled alongside _particles so the
    // back-to-front reorder allocates nothing on the frame path. Sized on first use and regrown
    // with the particle pool, never shrunk.
    private float[] _drawKeys = Array.Empty<float>();
    private DrawItem[] _drawItems = Array.Empty<DrawItem>();

    private bool _emitting;
    private float _sinceStart;
    private int _burstsSpawned, _burstsTotal;
    // ⚠ Write this through SetActive, never directly: the flag also gates the node's own
    // _Process, and an idle emitter left processing costs a frame callback for nothing.
    private bool _active;

    private bool _trailing;        // distance-interval trail mode (DISTANCE_INTERVAL states)
    private Vector3 _trailPrev;    // last emit-line end, world space
    private float _trailCarry;     // meters of motion carried into the next interval

    private bool _sustaining;      // continuous TIME_INTERVAL mode (AnimRuntime's PUFFER_STATE)
    private float _sustainCarry;   // seconds carried into the next emission interval
    private Vector3 _sustainPrevOrigin; // last frame's emitter origin, for sub-frame interpolation

    // A teleport is supposed to be rare, so the first occurrence per emitter
    // logs with its numbers and the rest are counted silently. The suppression is announced in
    // the line itself, so a reader never mistakes one line for one event.
    private int _teleportGuardCount;

    /// <summary>Diagnostics: how many frames this emitter's motion tripped the original's
    /// <see cref="TeleportGuardMeters"/> distance guard. Readable so a suite can assert the guard
    /// fired without parsing a log.</summary>
    public int TeleportGuardCount => _teleportGuardCount;

    /// <summary>Live particle count, diagnostics only (the <c>--debug-anim</c> puffer census, which
    /// is how a headless run confirms a crash's emitters are actually spawning).</summary>
    public int LiveCount => _liveCount;

    /// <summary>How many of the live particles the last <c>_Process</c> actually WROTE, which is
    /// what the distance fade decides and <see cref="LiveCount"/> deliberately does not: the fade is
    /// a draw rule, so a culled particle keeps living. Diagnostics, for a suite reading a real
    /// emitter over a real world rather than a fake renderer.</summary>
    internal int DrawnCount => _drawnCount;

    /// <summary>The authored state this emitter was built from, so a suite can say which rule
    /// applies to which emitter (an unauthored <c>FADE_RANGE</c> never culls, whatever the camera
    /// says). Read-only in intent; nothing may write through it.</summary>
    internal PufferState State => _state;

    /// <summary>Widens the emitter-frame LATERAL half-width of a sustained spawn's
    /// <c>DEVIATION_DISTANCE</c> offset; the vertical and forward halves are untouched. 1 spawns
    /// exactly where the authored cube puts it, and every emitter but the speed cue stays there.
    /// ⚠ Any other value re-scatters this emitter, so write it only from
    /// <see cref="Flight.Hud.SpeedCue.LateralSpreadFor"/>, whose remark carries the rule.</summary>
    internal float LateralSpreadScale { get; set; } = 1f;

    /// <summary>An opacity each particle takes at birth and keeps for its life, multiplying its
    /// ramp. Godot's particle holds its own reference to the ramp current when it spawned, so
    /// a host that rewrites the ramp per frame changes only the particles born after the change.
    /// 1 for every authored emitter; <see cref="Flight.Airframe.ExhaustSmoke"/> alone writes it.</summary>
    internal float BirthAlpha { get; set; } = 1f;

    /// <summary>The ambience handed in at construction, so a suite can ask WHICH instance an
    /// emitter reads rather than only what that instance says this frame. The wiring is what breaks
    /// silently: an emitter left on <see cref="EffectAmbience.Still"/> reads zero wind and no
    /// camera, and a camera-less emitter runs neither the distance fade nor either cull, which is
    /// indistinguishable from a becalmed mission until something measures the identity.</summary>
    internal EffectAmbience Ambience => _ambience;

    /// <summary>Builds an emitter for <paramref name="state"/>, loading its texture frames into an
    /// atlas; null if a frame is missing. <paramref name="activeDuration"/> is the burst duration
    /// (ignored by DISTANCE_INTERVAL states). <paramref name="sustained"/> selects continuous
    /// emission over a burst. <paramref name="softParticles"/> null takes the measured default (see
    /// <see cref="SmokeLuminance"/>). <paramref name="ambience"/> null is still air.</summary>
    public static Puffer? Create(PufferState state, TextureArchive textures, float activeDuration = 0.3f,
        bool sustained = false, bool? softParticles = null, EffectAmbience? ambience = null)
    {
        bool sequenced = state.TextureSequence.Count > 0;
        var frameNames = sequenced
            ? state.TextureSequence.Select(f => f.Texture).ToList()
            : state.Textures.ToList();
        var (atlas, diesDark) = BuildAtlas(frameNames, textures, sequenced);
        if (atlas == null)
            return null;
        // The whole blend verdict: the texture's own additive bit, read per frame because the
        // sprite changes under the particle. Neither the COLORS ramp nor the sprite's darkness
        // enters into it, see docs/org/textures.md.
        var additive = new bool[frameNames.Count];
        for (int i = 0; i < frameNames.Count; i++)
            additive[i] = textures.IsAdditive(frameNames[i]);
        var puffer = new Puffer();
        puffer.Init(state, new MultiMeshEmitterRenderer(atlas, frameNames.Count, additive,
            softParticles ?? !(state.Colors.Count > 0 || diesDark)),
            activeDuration, sustained, ambience);
        return puffer;
    }

    /// <summary>Builds an emitter over a supplied <paramref name="renderer"/>, the same modes and
    /// pool sizing as <see cref="Create"/>, with no atlas and no <c>TextureArchive</c>, so the
    /// three modes are assertable by a test. ⚠ Constructing a <c>Puffer</c> draws one RNG seed off
    /// <see cref="Rng.Puffer"/>; calling this on a capture path moves every puffer-bearing golden.
    /// <paramref name="fade"/> forces the three distance switches for a test; null (the real path)
    /// reads them from Config.</summary>
    public static Puffer CreateWith(PufferState state, IEmitterRenderer renderer,
        float activeDuration = 0.3f, bool sustained = false, EffectAmbience? ambience = null,
        PufferFadeSwitches? fade = null)
    {
        var puffer = new Puffer();
        puffer.Init(state, renderer, activeDuration, sustained, ambience, fade);
        return puffer;
    }

    /// <summary>Loads a named PUFFER_STATE from a zrdr effects reader and builds its
    /// emitter under <paramref name="parent"/>; null (logged by <see cref="PufferState.Load"/>) when
    /// the reader or its textures are missing. Shared by the flight assembly and the
    /// static damage lab.</summary>
    public static Puffer? MakePuffer(string zrdrPath, TextureArchive textures, Node parent,
        string file, string name, float duration = 0.3f, EffectAmbience? ambience = null)
    {
        var state = PufferState.Load(zrdrPath, file, name);
        var puffer = state != null ? Create(state, textures, duration, ambience: ambience) : null;
        if (puffer != null)
            parent.AddChild(puffer);
        return puffer;
    }

    /// <summary>Fire one burst at a fixed world position (decoupled from any moving parent).</summary>
    public void Burst(Vector3 worldPosition)
    {
        TopLevel = true; // ignore parent transform: the fireball stays put in world space
        // Toggling TopLevel PRESERVES the node's global transform (Godot 4), so the parent's
        // rotation at this moment would silently stick as this node's basis and skew every
        // local-space particle, set the whole transform, never just the position.
        GlobalTransform = new Transform3D(Basis.Identity, worldPosition);
        _liveCount = 0;
        _sinceStart = 0f;
        _burstsSpawned = 0;
        _emitting = true;
        SetActive(true);
        Visible = true;
        SpawnBatch(); // t = 0 immediately, so a screenshot on the impact frame already shows fire
    }

    /// <summary>Kills the effect and clears every live particle (called at/*before* respawn).</summary>
    public void Clear()
    {
        _emitting = false;
        _trailing = false;
        _sustaining = false;
        SetActive(false);
        _liveCount = 0;
        _drawnCount = 0;
        _renderer?.Show(0);
        Visible = false;
    }

    /// <summary>The one continuous drive: feed the host's world pose + dt every frame and the
    /// authored state picks the mode (see <c>docs/architecture.md</c>). A still host keeps the
    /// synthetic time cadence; a host that cannot move at all passes
    /// <paramref name="staticBurnMps"/>, spending virtual metres per second at the held pose.
    /// <see cref="Stop"/> ends the run; the next call re-homes rather than trailing from the old
    /// site.</summary>
    public void Emit(Vector3 worldPos, Basis worldBasis, float dt, float staticBurnMps = 0f)
    {
        if (_state.DistanceInterval <= 0f)
        {
            SustainAt(worldPos, worldBasis, dt);
            return;
        }
        // DISTANCE_INTERVAL follows the authored AT_NODE point, not the host origin. The still-host
        // fallback already applies this in SustainAt; applying it here keeps moving emissions at
        // that same point (speed_cue is player + local (0,0,-60), 60 m ahead of the aircraft).
        var trailPos = worldPos + worldBasis * _state.AtNodeOffset;
        bool moved = _trailing && (trailPos - _trailPrev).LengthSquared() > 1e-8f;
        if (!moved && staticBurnMps > 0f)
        {
            TrailBurnAt(trailPos, worldBasis, dt, staticBurnMps);
            return;
        }
        TrailAdvance(trailPos, worldBasis, dt);
        // An unmoved DISTANCE_INTERVAL state still runs one SustainAt batch: the synthetic
        // TIME_INTERVAL cadence sputters a single homing puff at the muzzle/exhaust.
        if (!moved)
            SustainAt(worldPos, worldBasis, dt);
    }

    /// <summary>Stops a continuous run: trail AND sustain together, unconditionally, the
    /// ghost-trail rule: a pooled slot is teleported
    /// between call sites, so a trail origin kept across the pause would draw a puff line from
    /// the previous site on revival. Idempotent; live particles finish their own lifetimes
    /// (<see cref="Clear"/> is the hard kill).</summary>
    public void Stop()
    {
        SustainEnd();
        TrailEnd();
    }

    /// <summary>⚠ Do not drop this. Godot turns processing ON at ready for every script that
    /// defines <c>_Process</c>, so an emitter built dormant would start asking for a frame callback
    /// it returns straight out of; this puts the node back where <see cref="SetActive"/> left
    /// it.</summary>
    public override void _Ready() => SetProcess(_active);

    public override void _Process(double delta)
    {
        using var _ = ProcessSiteCost.Enter(ProcessSite.Puffers);
        if (!_active)
            return;
        // Particles are sim state: they freeze with a halted clock and scale with a scaled one.
        float dt = GameClock.Current?.FrameDt ?? (float)delta;
        _sinceStart += dt;

        if (_emitting)
        {
            while (_burstsSpawned < _burstsTotal && _sinceStart >= _burstsSpawned * _state.TimeInterval)
                SpawnBatch();
            if (_burstsSpawned >= _burstsTotal)
                _emitting = false;
        }

        // integrate live particles, swap-removing the dead so survivors stay packed at the front
        bool flipbook = _state.TextureSequence.Count > 0;
        bool hasRamp = _state.Colors.Count > 0;
        float damp = Mathf.Exp(-_state.Friction * dt);
        // Friction damps toward the WIND, not toward rest, read once per frame like the original.
        // Zero wind is an algebraic no-op, which is what keeps a windless golden unmoved.
        var windTarget = _ambience.Wind * _state.WindFactor;
        // A DRAW rule, not a sim rule: a discarded particle keeps living and ageing, just unwritten
        // this frame. Empty until WeatherRig.Tick publishes the pane cameras (EffectAmbience.Viewers).
        var viewers = _ambience.Viewers;
        bool fading = viewers.Count > 0;
        // Burst mode keeps particle positions in the node's own frame (the burst point is the
        // node's translation); trail and sustain force the transform to identity and store world
        // positions. Every mode leaves the basis at identity, so one addition covers all three.
        var nodeOrigin = GlobalPosition;
        // The original depth-sorts the frame's whole transparent list, so the draw order belongs
        // to the camera and not to the order particles sit in. Pane 0 supplies the one order every
        // pane draws, a deliberate seam; see docs/org/textures.md and docs/org/puffer.md.
        bool sorting = viewers.Count > 0;
        var sortView = sorting ? viewers[0] : default;
        if (_drawItems.Length < _particles.Length)
        {
            _drawKeys = new float[_particles.Length];
            _drawItems = new DrawItem[_particles.Length];
        }
        int drawn = 0;
        for (int i = 0; i < _liveCount; i++)
        {
            ref var p = ref _particles[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                _particles[i] = _particles[--_liveCount];
                i--;
                continue;
            }
            // ⚠ Do not reorder: pos advances on last frame's velocity, THEN acceleration, THEN
            // friction. Swapping lands a different position on frame one for any accelerated puffer.
            p.Pos += p.Vel * dt;
            p.Vel += _state.WorldAcceleration * dt;
            // ⚠ Do not drop this gate. Damping unconditionally is identity only while the target is
            // rest; with wind as the target a frictionless puffer would wrongly feel it.
            if (_state.Friction != 0f)
            {
                p.Vel = ((p.Vel - windTarget) * damp) + windTarget;
            }

            // A negative-age particle is still drawn on the frame it's born, pinned to ramp stop 0
            //, see docs/org/puffer.md. Distance gate before any draw work: discarded means unwritten.
            var world = nodeOrigin + p.Pos;
            float distAlpha = 1f;
            if (fading && !NearestViewerAlpha(world, viewers, out distAlpha))
                continue;

            float lifeFrac = p.Age > 0f ? p.Age / p.Life : 0f;
            float size = p.BaseSize * Mathf.Lerp(1f, _state.GrowthFactor, lifeFrac);
            // The key is negated view depth, so an ascending sort is the decode's farthest-first.
            float depth = sorting ? sortView.Forward.Dot(world - sortView.Position) : 0f;
            _drawKeys[drawn] = depth > 0f ? -depth : -BehindEyeSortDepth;
            // ⚠ The distance alpha MULTIPLIES the COLORS ramp's alpha or the fade envelope's; it
            // replaces neither. Getting that precedence wrong makes every ramped puffer invisible.
            _drawItems[drawn++] = new DrawItem
            {
                Pos = p.Pos,
                Size = size,
                Frame = flipbook ? FrameFor(lifeFrac) : p.Frame,
                Alpha = (hasRamp ? 1f : FadeFor(lifeFrac)) * distAlpha * p.BirthAlpha,
                Color = hasRamp ? RampColor(lifeFrac) : Colors.White,
            };
        }

        // Array.Sort is unstable, which is the original's own exposure on two equal depths: its
        // final pass runs an unstable sort over each equal-key, equal-texture run.
        if (sorting && drawn > 1)
            Array.Sort(_drawKeys, _drawItems, 0, drawn);
        for (int i = 0; i < drawn; i++)
        {
            ref var item = ref _drawItems[i];
            _renderer.Write(i, item.Pos, item.Size, item.Frame, item.Alpha, item.Color);
        }

        _renderer.Show(drawn);
        _drawnCount = drawn;

        if (_liveCount == 0 && !_emitting && !_trailing && !_sustaining)
        {
            SetActive(false);
            Visible = false;
        }
    }

    // A fade band's `1/width`, with the engine's own degenerate case: both of its
    // setters store the raw difference first and only invert it
    // when it is non-zero, so an equal-ended band keeps 0 rather than an infinity. The unauthored
    // far band (`FLT_MAX`, `FLT_MAX`) is exactly that case, and nothing ever reaches its
    // ramp anyway.
    private static float Reciprocal(float width) => width != 0f ? 1f / width : 0f;

    private static float FadeFor(float lifeFrac) =>
        lifeFrac < FadeIn ? lifeFrac / FadeIn
        : lifeFrac > FadeOutStart ? Mathf.Max(0f, 1f - (lifeFrac - FadeOutStart) / (1f - FadeOutStart))
        : 1f;

    // The cached form of BakeAtlas: one bake per (archive, frame list, sequenced).
    private static (ImageTexture? Atlas, bool DiesDark) BuildAtlas(IReadOnlyList<string> names,
        TextureArchive textures, bool sequenced)
    {
        var cache = AtlasCache.GetOrCreateValue(textures);
        string key = (sequenced ? "seq:" : "pool:") + string.Join("\n", names);
        if (cache.TryGetValue(key, out var hit))
            return hit;
        var baked = BakeAtlas(names, textures, sequenced);
        if (baked.Atlas is { } atlas)
            cache[key] = (atlas, baked.DiesDark);
        return baked;
    }

    // Packs the frames side by side into one atlas, and measures whether a particle DIES
    // on a dark sprite, the flipbook's last frame, or a static pool's mean luminance. That
    // measurement drives the soft-particle default alone (see SmokeLuminance), and distinguishes
    // "fire_n_smoke", whose flipbook ends near-black despite starting bright. Null atlas when a
    // frame is missing.
    private static (ImageTexture? Atlas, bool DiesDark) BakeAtlas(IReadOnlyList<string> names,
        TextureArchive textures, bool sequenced)
    {
        if (names.Count == 0)
            return (null, false);
        var frames = new Image[names.Count];
        int fw = 0, fh = 0;
        for (int i = 0; i < names.Count; i++)
        {
            var tex = textures.Find(names[i]);
            if (tex == null)
                return (null, false);
            var img = tex.GetImage();
            img.Convert(Image.Format.Rgba8);
            frames[i] = img;
            fw = Mathf.Max(fw, img.GetWidth());
            fh = Mathf.Max(fh, img.GetHeight());
        }
        var atlas = Image.CreateEmpty(fw * names.Count, fh, false, Image.Format.Rgba8);
        float lastLum = 0f, meanLum = 0f;
        for (int i = 0; i < frames.Length; i++)
        {
            var f = frames[i];
            if (f.GetWidth() != fw || f.GetHeight() != fh)
                f.Resize(fw, fh);
            atlas.BlitRect(f, new Rect2I(0, 0, fw, fh), new Vector2I(i * fw, 0));
            lastLum = MeanLuminance(f);
            meanLum += lastLum / frames.Length;
        }
        return (ImageTexture.CreateFromImage(atlas),
            (sequenced ? lastLum : meanLum) < SmokeLuminance);
    }

    // A sprite's mean luminance weighted by its own alpha, what it actually
    // contributes when composited, rather than what its unmasked pixels contain.
    private static float MeanLuminance(Image img)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        if (w == 0 || h == 0)
            return 0f;
        float sum = 0f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = img.GetPixel(x, y);
                sum += (0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B) * c.A;
            }
        return sum / (w * h);
    }

    // Godot dispatches _Process to every processing node, and each C# callback crosses the managed
    // boundary whether or not its body does anything. A mission pre-warms thousands of emitters, so
    // the frame cost is set by how many ask to be called, not by how many run.
    // ⚠ A node joining the process group mid-pass is not visited until the next frame, so a start
    // costs one frame before the emitter's first integrate-and-draw. That is a particle shot's
    // whole difference across this gate; see docs/verification.md PERF-24.
    private void SetActive(bool active)
    {
        _active = active;
        SetProcess(active);
    }

    // The distance alpha across every pane: keeps the most favourable
    // answer of DistanceAlpha evaluated per viewer, drawing if any pane would draw it.
    // ⚠ Must be per-viewer, not nearest-by-range: the fade is view-space depth along each camera's
    // own forward axis, and a pane facing away can still be the range-nearest one. See
    // `docs/architecture.md`.
    private bool NearestViewerAlpha(Vector3 worldPos,
        IReadOnlyList<ViewerSet.ViewerPose> viewers, out float alpha)
    {
        alpha = 0f;
        bool drawn = false;
        for (int v = 0; v < viewers.Count; v++)
        {
            var pose = viewers[v];
            if (!DistanceAlpha(worldPos, pose.Position, pose.Forward, out float paneAlpha))
                continue;
            drawn = true;
            if (paneAlpha > alpha)
                alpha = paneAlpha;
            if (alpha >= 1f)
                break;                      // nothing a further pane says can beat full alpha
        }
        return drawn;
    }

    // The camera-distance alpha for ONE viewer, decoded verbatim from the original's
    // per-particle draw; `false` means the particle is discarded this frame. The distance is
    // view-space DEPTH, not euclidean range. ⚠ The near ramp reads the FAR band's origin, not a
    // typo, verified in raw assembly, and must not be "repaired". `_distanceFade`/`_farCull`/
    // `_nearCull` are three separate mechanisms, not one switch. See
    // `docs/formats/effects.md` and `docs/org/puffer.md`.
    private bool DistanceAlpha(Vector3 worldPos, in Vector3 camPos, in Vector3 camFwd,
        out float alpha)
    {
        alpha = 1f;
        float d = camFwd.Dot(worldPos - camPos);
        float scaled = d * _globalFadeFactor;   // the far band only, see GlobalFadeFactorDefault
        if (_farCull && _state.FarFadeEnd <= scaled)
            return false;
        if (scaled <= _state.FarFadeStart)
        {
            // Inside the far band's full-alpha region, so the NEAR band decides. Unauthored, that
            // is (0, 0): nothing is nearer than depth 0 except what is behind the camera, which
            // the engine culls here and so do we.
            if (_nearCull && d <= _state.NearFadeStart)
                return false;
            if (_state.NearFadeEnd <= d || !_distanceFade)
                return true;                    // the engine's `goto`: alpha 1, past the >0 gate
            alpha = (d - _state.FarFadeStart) * _nearRecip;   // ⚠ FarFadeStart, the cross-wire
        }
        else
        {
            if (!_distanceFade)
                return true;
            alpha = (_state.FarFadeEnd - scaled) * _farRecip;
        }
        return alpha > 0f;
    }

    // Advances a DISTANCE_INTERVAL trail emitter to the followed node's new world position: the
    // engine's one emission accumulator fed with metres of motion instead of seconds, then the
    // same batch loop as SustainAt (NUMBER particles per batch, spread along this frame's motion,
    // born the sub-frame age, with LOCAL_VELOCITY in the host's frame). The first call starts the
    // trail; call every frame while the effect is on. See docs/org/puffer.md, "The emission
    // accumulator".
    private void TrailAdvance(Vector3 worldPos, Basis worldBasis, float dt)
    {
        if (_state.DistanceInterval <= 0f)
            return;
        if (!_trailing)
        {
            TopLevel = true;             // particles live in world space, left behind the plane
            // ⚠ Set the whole transform, not just position: TopLevel preserves the global transform
            // (Godot 4), so a flying parent's attitude would otherwise yaw world-space puffs off-origin.
            GlobalTransform = Transform3D.Identity;
            _trailing = true;
            _trailPrev = worldPos;
            _trailCarry = 0f;
            SetActive(true);
            Visible = true;
            return;
        }
        var prev = _trailPrev;
        float dist = (worldPos - prev).Length();
        _trailPrev = worldPos;
        if (dist < 1e-5f)
            return;
        // The original's teleport guard, verbatim: skip the whole emit past this distance so a
        // jump lays no puff line along itself. See docs/org/puffer.md.
        if (dist >= TeleportGuardMeters)
        {
            if (_teleportGuardCount++ == 0)
                Log.Info("world", $"puffer teleport guard tripped name={_state.Name} dist={dist:0.0}m guard={TeleportGuardMeters:0}m (first occurrence only; further ones counted silently)");
            return;
        }
        _trailCarry += dist;
        EmitBatches(prev, worldPos, worldBasis, dt, ref _trailCarry, _state.DistanceInterval, _trailSizeScale);
    }

    // Stops trail emission; live smoke decays naturally.
    private void TrailEnd() => _trailing = false;

    // Static-viewer variant of TrailAdvance: emits the trail's
    // per-meter puffs AT a fixed world point, spending `speedMps`
    // meters of virtual motion per second, the damage lab's parked plane, whose
    // panels burn in place (the puffs' own random velocity and growth make the
    // stacked emissions read as a flickering fire). Same carry, pool and spawn
    // path as the moving trail, with no motion to spread along and no sub-frame age.
    private void TrailBurnAt(Vector3 worldPos, Basis worldBasis, float dt, float speedMps)
    {
        if (_state.DistanceInterval <= 0f)
            return;
        if (!_trailing)
        {
            TopLevel = true;
            GlobalTransform = Transform3D.Identity; // see TrailAdvance: TopLevel keeps the global basis
            _trailing = true;
            _trailPrev = worldPos;
            _trailCarry = 0f;
            SetActive(true);
            Visible = true;
        }
        _trailCarry += speedMps * dt;
        EmitBatches(worldPos, worldPos, worldBasis, 0f, ref _trailCarry, _state.DistanceInterval, _trailSizeScale);
    }

    // The engine's batch loop, shared by the distance and time modes: the FULL
    // floor(accumulator / interval), uncapped, batch b at `frac = (b+1)·interval/accumulator`
    // along `prev → origin` and born `(1 − frac)·dt` old. ⚠ Do not cap the count and do not
    // clamp the age; both are invented divergences, see docs/org/puffer.md.
    private void EmitBatches(Vector3 prev, Vector3 origin, Basis worldBasis, float dt,
        ref float accumulator, float interval, float sizeScale)
    {
        interval = Mathf.Max(interval, 1e-3f);
        float total = accumulator;
        int batches = (int)(total / interval);
        for (int b = 0; b < batches; b++)
        {
            float frac = (b + 1) * interval / total;
            SpawnSustained(prev.Lerp(origin, frac), worldBasis, (1f - frac) * dt, sizeScale);
        }
        accumulator -= batches * interval;
    }

    // Continuous emission at a moving world point, spread along the emitter's own motion
    // since the previous call rather than stacked on today's pose (batch `k` at
    // `frac = (k+1)·interval/accumulator` along `prevOrigin → origin`). Call every frame
    // while the puffer is on; SustainEnd stops emission and lets particles decay.
    // ⚠ No per-frame batch cap, see `docs/org/puffer.md` for why a cap is actively wrong.
    private void SustainAt(Vector3 worldPos, Basis worldBasis, float dt)
    {
        var origin = worldPos + worldBasis * _state.AtNodeOffset;
        if (!_sustaining)
        {
            TopLevel = true;                 // world-space particles, like the trail mode
            GlobalTransform = Transform3D.Identity; // see TrailAdvance: TopLevel keeps the global basis
            _sustaining = true;
            SetActive(true);
            Visible = true;
            _sustainCarry = _state.TimeInterval; // emit on the very first frame
            // No prior pose to interpolate from, re-home here rather than trailing from a
            // stale point, exactly as TrailAdvance's own homing rule: without this a revive
            // after Stop() would draw a line of puffs from wherever the emitter last was.
            _sustainPrevOrigin = origin;
        }
        _sustainCarry += dt;
        // ⚠ The age offset rides the RAW dt, unclamped, exactly as the engine does. Clamping it to
        // keep long-hitch batches alive is an invented divergence, see docs/org/puffer.md.
        EmitBatches(_sustainPrevOrigin, origin, worldBasis, dt, ref _sustainCarry, _state.TimeInterval, _sustainSizeScale);
        _sustainPrevOrigin = origin;
    }

    // Stops sustained emission; live particles finish their lifetimes.
    private void SustainEnd() => _sustaining = false;

    private void Init(PufferState state, IEmitterRenderer renderer, float activeDuration,
        bool sustained, EffectAmbience? ambience = null, PufferFadeSwitches? fade = null)
    {
        _state = state;
        _renderer = renderer;
        _ambience = ambience ?? EffectAmbience.Still;
        Name = "puffer_" + state.Name;
        _burstSizeScale = Config.GetFloat("puffer.burstSizeScale", SizeScaleDefault);
        _trailSizeScale = Config.GetFloat("puffer.trailSizeScale", SizeScaleDefault);
        _sustainSizeScale = Config.GetFloat("puffer.sustainSizeScale", SizeScaleDefault);
        // Every distance-switch default reproduces the original; see the fields' remark and
        // DistanceAlpha.
        _distanceFade = fade?.DistanceFade ?? Config.GetBool("puffer.distanceFade", true);
        _farCull = fade?.FarCull ?? Config.GetBool("puffer.farCull", true);
        _nearCull = fade?.NearCull ?? Config.GetBool("puffer.nearCull", true);
        _globalFadeFactor = fade?.GlobalFadeFactor
            ?? Config.GetFloat("puffer.globalFadeFactor", GlobalFadeFactorDefault);
        _nearRecip = Reciprocal(state.NearFadeEnd - state.NearFadeStart);
        _farRecip = Reciprocal(state.FarFadeEnd - state.FarFadeStart);
        _priorityFactor = 1f + PriorityScaleDefault * state.Priority;
        if (state.DistanceInterval > 0f)
        {
            // Distance states use the trail pool even on the sustained path: the steady-state formula
            // below would floor them at 16, dropping ~85% of a flight-speed trail's authored emission.
            _particles = new Particle[TrailPool];
        }
        else if (sustained)
        {
            // Number per interval, each living up to LIFETIME_RANGE's max, plus one interval's
            // worth of headroom, the authored lifetime is the whole story.
            int steady = Mathf.CeilToInt(state.Number * state.LifetimeMax
                                         / Mathf.Max(state.TimeInterval, 1e-3f)) + state.Number;
            _particles = new Particle[Mathf.Clamp(steady, SustainPoolMin, SustainPoolMax)];
        }
        else
        {
            // Emit at t = 0, TimeInterval, 2·TimeInterval, … while active.
            _burstsTotal = Mathf.Max(1, Mathf.FloorToInt(activeDuration / Mathf.Max(state.TimeInterval, 1e-3f) + 1e-3f) + 1);
            _particles = new Particle[state.Number * _burstsTotal];
        }

        // The cull margin is a fact about the particles, not about the draw: it covers the largest
        // tuned size any spawn path here could produce, and the renderer only applies it.
        renderer.Attach(this, _particles.Length,
            Mathf.Max(4f, state.SizeMax * state.GrowthFactor * _priorityFactor
                * Mathf.Max(_burstSizeScale, Mathf.Max(_trailSizeScale, _sustainSizeScale))));
        Visible = false;
    }

    // One batch of a continuous emitter, trail or sustain. `origin` is the emitter's world point
    // for this batch, already AT_NODE-offset and motion-interpolated (EmitBatches owns both).
    // `ageOffset` is the sub-frame `(1 - frac)·dt` correction, added to the start age even for a
    // puffer with no `START_AGE_RANGE` authored.
    private void SpawnSustained(Vector3 origin, Basis worldBasis, float ageOffset, float sizeScale)
    {
        var min = _state.MinRandomVelocity;
        var max = _state.MaxRandomVelocity;
        // LOCAL_VELOCITY is in the emitter node's frame (the smokestack's "up", the smoke
        // screen's astern); WORLD_VELOCITY is not. Rotating the local part is what keeps a
        // banking/turning emitter correct.
        var baseVel = worldBasis * _state.LocalVelocity + _state.WorldVelocity;
        float d = _state.DeviationDistance;
        // The emitter's own right axis, so the widening below follows the host through a bank
        // instead of a world axis. Skipped entirely at the default scale, where it is unused.
        float widen = LateralSpreadScale - 1f;
        var right = widen != 0f ? worldBasis.X.Normalized() : Vector3.Zero;
        for (int k = 0; k < _state.Number; k++)
        {
            if (_liveCount >= _particles.Length && !GrowPool())
                break;
            // ⚠ The draw order (pos → vel → size → life) and the ±0.5·d half-width offset are both
            // load-bearing: changing either re-scatters every sustained emitter (c1-waterfall golden).
            var offset = new Vector3(Rand(-0.5f * d, 0.5f * d), Rand(-0.5f * d, 0.5f * d), Rand(-0.5f * d, 0.5f * d));
            // Scales the lateral component alone, after the three draws, so the draw order and the
            // default scale's spawn point both stay exactly what they were.
            if (widen != 0f)
                offset += right * (right.Dot(offset) * widen);
            var pos = origin + offset;
            var vel = baseVel + new Vector3(Rand(min.X, max.X), Rand(min.Y, max.Y), Rand(min.Z, max.Z));
            float size = Rand(_state.SizeMin, _state.SizeMax) * sizeScale * _priorityFactor;
            float life = Rand(_state.LifetimeMin, _state.LifetimeMax);
            // Gated on HasStartAgeRange so the ~2,900 puffers without the key draw nothing extra and
            // stay bit-identical. ageOffset is computed, not drawn, so it costs no extra _rng call.
            float age = (_state.HasStartAgeRange ? Rand(_state.StartAgeMin, _state.StartAgeMax) : 0f) + ageOffset;
            float frame = _state.TextureSequence.Count > 0 ? 0f
                : Mathf.Min(_state.Textures.Count - 1,
                    Mathf.FloorToInt((float)_rng.NextDouble() * _state.Textures.Count));
            // The engine's born-dead skip: a particle born past its own lifetime is never created.
            // Every draw above still runs, so a skip changes what's stored, never the neighbours' scatter.
            if (age >= life)
                continue;
            _particles[_liveCount++] = new Particle
            {
                Pos = pos,
                Vel = vel,
                BaseSize = size,
                Life = life,
                Age = age,
                Frame = frame,
                BirthAlpha = BirthAlpha,
            };
        }
    }

    // A continuous emitter's pool grows to what its authored emission needs, up to a hard ceiling,
    // because the engine bounds its particles only by what can be born alive (docs/org/puffer.md):
    // the smoke screen's NUMBER 4 per 0.65 m at flight speed keeps ~2,000 puffs alive, three times
    // the trail pool it starts on. False once the ceiling is reached; the batch then drops the rest.
    private bool GrowPool()
    {
        if (_particles.Length >= ContinuousPoolMax)
            return false;
        int capacity = Mathf.Min(_particles.Length * 2, ContinuousPoolMax);
        Array.Resize(ref _particles, capacity);
        _renderer.Grow(capacity);
        return true;
    }

    // Interpolates the COLORS (lifeFrac, color) ramp.
    private Color RampColor(float lifeFrac)
    {
        var ramp = _state.Colors;
        if (lifeFrac <= ramp[0].Frac)
            return ramp[0].Color;
        for (int i = 1; i < ramp.Count; i++)
            if (lifeFrac <= ramp[i].Frac)
            {
                float span = ramp[i].Frac - ramp[i - 1].Frac;
                float t = span > 1e-6f ? (lifeFrac - ramp[i - 1].Frac) / span : 1f;
                return ramp[i - 1].Color.Lerp(ramp[i].Color, t);
            }
        return ramp[^1].Color;
    }

    private void SpawnBatch()
    {
        var min = _state.MinRandomVelocity;
        var max = _state.MaxRandomVelocity;
        var baseVel = _state.LocalVelocity + _state.WorldVelocity;
        float d = _state.DeviationDistance;
        for (int k = 0; k < _state.Number && _liveCount < _particles.Length; k++)
        {
            // ±0.5·d, not ±d, see SpawnSustained's comment.
            var pos = new Vector3(Rand(-0.5f * d, 0.5f * d), Rand(-0.5f * d, 0.5f * d), Rand(-0.5f * d, 0.5f * d));
            var vel = baseVel + new Vector3(Rand(min.X, max.X), Rand(min.Y, max.Y), Rand(min.Z, max.Z));
            float size = Rand(_state.SizeMin, _state.SizeMax) * _burstSizeScale * _priorityFactor;
            float life = Rand(_state.LifetimeMin, _state.LifetimeMax);
            // START_AGE_RANGE: see SpawnSustained's comment, gated draw, right after Life.
            float age = _state.HasStartAgeRange ? Rand(_state.StartAgeMin, _state.StartAgeMax) : 0f;
            // The engine's born-dead skip, see SpawnSustained. The burst path has no sub-frame
            // term either (its batches are keyed off _sinceStart, not a carried accumulator), so
            // as on the trail path only an authored START_AGE_RANGE can fire it.
            if (age >= life)
                continue;
            _particles[_liveCount++] = new Particle
            {
                Pos = pos,
                Vel = vel,
                BaseSize = size,
                Life = life,
                Age = age,
                BirthAlpha = BirthAlpha,
            };
        }
        _burstsSpawned++;
    }

    // Latest flipbook frame whose keyed time has been reached (times ascending). ⚠ The
    // key is a FRACTION of the particle's own lifetime, not seconds, reading it as seconds
    // collapsed `large_30sec_fire` into a static ball. See `docs/org/puffer.md`.
    private float FrameFor(float lifeFrac)
    {
        var seq = _state.TextureSequence;
        int frame = 0;
        for (int i = 0; i < seq.Count && seq[i].Time <= lifeFrac; i++)
            frame = i;
        return frame;
    }

    private float Rand(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    private struct Particle
    {
        public Vector3 Pos, Vel;
        public float BaseSize, Age, Life;
        public float Frame; // static-TEXTURES pool: the randomly picked atlas column
        public float BirthAlpha; // Puffer.BirthAlpha as it stood at spawn
    }

    // One particle's finished draw payload, buffered so the frame can be reordered before it
    // reaches the renderer. Position stays in the node's frame, the same value Write took before.
    private struct DrawItem
    {
        public Vector3 Pos;
        public float Size, Frame, Alpha;
        public Color Color;
    }
}
