using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Effects;
using CSVM.Flight.Camera;
using CSVM.Flight.Hud;
using CSVM.Mech3;
using CSVM.Mech3.Anim;
using CSVM.Utils;
using Godot;

using static CSVM.Testing.SuiteConstants;
namespace CSVM.Testing;

internal static class PufferSuites
{
    [Suite("emitter-lifetime",
        "a destructible's death starts a PUFFER_STATE emitter and BL-236's own retirement rule stops it")]
    internal static void EmitterLifetime(TestContext ctx)
    {
        const string chapter = "C1";   // the only chapter shipping refuel* (5 defs), matches bounce-launch
        const string pufferName = "fire_n_smoke";
        var fake = new CountingEmitterFactory();
        ctx.EmitterFactory = fake;
        try
        {
            // ⚠ Private, never the shared cache: this world is built with a fake emitter factory, and
            // a cached one would hand that fake to every later suite on this chapter.
            ctx.WithPrivateWorld(chapter, collision: false, world =>
            {
                var runtime = world.Runtime;
                DestructibleRegistry.Instance? tank = null;
                foreach (var inst in runtime.Destructibles.All)
                {
                    if (inst.Def.AnimName is { } name
                        && name.StartsWith("refuel", System.StringComparison.OrdinalIgnoreCase))
                    {
                        tank = inst;
                        break;
                    }
                }
                ctx.Check(tank != null, $"chapter ships a refuel tank chapter={chapter}");
                if (tank == null)
                {
                    return;
                }

                if (tank.Status == DestructibleRegistry.State.Destroyed)
                {
                    runtime.ResetDestructible(tank);
                }

                var before = new HashSet<string>(runtime.Emitters.Census.Select(r => r.Name));
                ctx.Check(!before.Contains(pufferName), $"{pufferName} is not yet known before the kill");

                runtime.DamageAt(tank.Anchor, tank.MaxHealth + 1f);
                ctx.Check(fake.Built.Count > 0, $"the death reached the fake factory count={fake.Built.Count}");

                bool sawEmitting = false;
                const float tick = 1f / 60f;
                for (int i = 0; i < 600; i++)   // 10 s, comfortably past the ~5 s the tank's own death instance takes to retire
                {
                    runtime.Advance(tick);
                    if (runtime.Emitters.Census.Any(r => r.Name == pufferName && r.Emitting))
                    {
                        sawEmitting = true;
                    }
                }
                ctx.Check(sawEmitting, $"{pufferName} started emitting after the kill");

                bool rowPresent = runtime.Emitters.Census.Any(r => r.Name == pufferName);
                ctx.Check(rowPresent, $"{pufferName}'s emitter row survives the kill, EndFor pauses, it does not forget");

                bool stillEmitting = rowPresent
                    && runtime.Emitters.Census.First(r => r.Name == pufferName).Emitting;
                ctx.Check(!stillEmitting,
                    $"{pufferName} stopped once the death instance retired (BL-236's own rule)");
            });
        }
        finally
        {
            ctx.EmitterFactory = null;
        }
    }

    // The wiring the world's emitters hang on, over a real built world: WHICH EffectAmbience
    // instance they read. An emitter left on the still-air null object reads zero wind and no
    // camera, and a camera-less emitter runs neither the distance fade nor either cull, so a
    // 300 m plume draws across the map with nothing failing anywhere. Identity is the only thing
    // that can see that, which is why the first check is a reference comparison.
    [Suite("world-emitter-ambience",
        "the world's own PUFFER_STATE emitters read the session's ambience, so a world plume takes "
        + "the mission wind and its authored FADE_RANGE culls it once a camera is published")]
    internal static void WorldEmitterAmbience(TestContext ctx)
    {
        // C5, whose bootstrap stands up the torch and train plumes, authored FADE_RANGE 300/500:
        // the tightest band a re-armed far cull has to act on, and the chapter the defect was
        // measured in.
        const string chapter = "C5";
        const float dt = 1f / 60f;
        var wind = new Vector3(0f, 0f, 12f);
        // One instance per session, exactly as GameSession holds one and WeatherRig.Tick writes it.
        var session = new EffectAmbience();
        session.SetWind(wind);
        ctx.Ambience = session;
        // ⚠ Detach the harness clock for the whole suite: it is a FixedStep clock nothing steps, so
        // FrameDt is 0, no particle would age and every count below would read the same.
        var clock = GameClock.Current;
        GameClock.Current = null;
        try
        {
            // ⚠ Private, never the shared cache: this world is built with an ambience of the
            // suite's own, and a cached one would hand it to every later suite on this chapter.
            ctx.WithPrivateWorld(chapter, collision: false, world =>
            {
                var emitters = new List<Puffer>();
                foreach (var child in world.Stage.GetChildren())
                {
                    if (child is Puffer puffer)
                        emitters.Add(puffer);
                }
                ctx.Check(emitters.Count > 0,
                    $"{chapter}'s bootstrap builds world emitters built={emitters.Count}");
                if (emitters.Count == 0)
                    return;

                int onSession = 0, onStill = 0;
                foreach (var puffer in emitters)
                {
                    if (ReferenceEquals(puffer.Ambience, session))
                        onSession++;
                    if (ReferenceEquals(puffer.Ambience, EffectAmbience.Still))
                        onStill++;
                }
                ctx.Same(emitters.Count, onSession,
                    $"world emitters reading the SESSION's ambience instance");
                ctx.Same(0, onStill, $"world emitters left on the still-air null object");
                var control = Puffer.CreateWith(
                    FadeTestState("no_ambience_control", 0f, 0f, 300f, 400f),
                    new RecordingEmitterRenderer());
                ctx.Check(ReferenceEquals(control.Ambience, EffectAmbience.Still),
                    $"ABLE-TO-FAIL CONTROL: an emitter built with no ambience reads Still, so the count above is a verdict");
                control.Free();

                // The wind half: the rig's published gust reaches those emitters, and a particle
                // over that same instance is carried by it (the integrator itself is puffer-wind's).
                ctx.Check(emitters[0].Ambience.Wind.IsEqualApprox(wind),
                    $"the wind published on the session reaches the world's emitters wind={emitters[0].Ambience.Wind}");
                var carried = RunWindParticle(ctx,
                    WindTestState("world_wind", Vector3.Zero, Vector3.Zero, 3f, 1f), session, 120, dt);
                ctx.Check(carried.Z > 5f,
                    $"a particle over that instance is carried downwind z={carried.Z:0.000}");
                var becalmed = RunWindParticle(ctx,
                    WindTestState("world_wind_control", Vector3.Zero, Vector3.Zero, 3f, 1f),
                    EffectAmbience.Still, 120, dt);
                ctx.Check(becalmed.Length() < 1e-5f,
                    $"ABLE-TO-FAIL CONTROL: the same particle on the still-air object never moves drift={becalmed.Length():0.000000}");

                // The fade half, on the built emitters themselves: one second of world time with no
                // camera published, where every live particle is written, then the same emitters
                // with a camera 60 km away, past every authored band in the install.
                for (int i = 0; i < 60; i++)
                {
                    world.Runtime.Advance(dt);
                    foreach (var puffer in emitters)
                        puffer._Process(dt);
                }
                int live = 0, drawn = 0;
                foreach (var puffer in emitters)
                {
                    live += puffer.LiveCount;
                    drawn += puffer.DrawnCount;
                }
                ctx.Check(live > 0, $"the bootstrap's emitters are actually emitting live={live}");
                ctx.Same(live, drawn,
                    $"with no camera published every live particle is written (the no-camera-no-fade rule)");

                session.SetCamera(new Vector3(0f, 60000f, 0f), Vector3.Down);
                foreach (var puffer in emitters)
                    puffer._Process(dt);
                int bandedLive = 0, bandedDrawn = 0, openLive = 0, openDrawn = 0;
                foreach (var puffer in emitters)
                {
                    bool banded = puffer.State.FarFadeEnd != float.MaxValue;
                    bandedLive += banded ? puffer.LiveCount : 0;
                    bandedDrawn += banded ? puffer.DrawnCount : 0;
                    openLive += banded ? 0 : puffer.LiveCount;
                    openDrawn += banded ? 0 : puffer.DrawnCount;
                }
                ctx.Check(bandedLive > 0,
                    $"{chapter}'s FADE_RANGE emitters hold particles to cull live={bandedLive}");
                ctx.Same(0, bandedDrawn,
                    $"60 km past its authored FADE_RANGE not one of their particles is written");
                ctx.Same(openLive, openDrawn,
                    $"and an emitter authoring no far band still draws, so the camera alone culls nothing");
                ctx.Note($"{chapter} world emitters: {emitters.Count} built, {live} live, {bandedLive} under an authored far band");
            });
        }
        finally
        {
            GameClock.Current = clock;
            ctx.Ambience = null;
        }
    }

    // ---- the emitter's own modes, with no GPU ---------------------------------------------------

    // Every Puffer emission path through the collapsed Emit/Stop pair, plus Burst, over a
    // RecordingEmitterRenderer. No atlas, TextureArchive or MultiMesh is built anywhere here, which
    // is its own tripwire: if it gets slow, something started building real emitters again. Both
    // states are read from the shipped readers, so every expected number derives from authored data.
    // ⚠ Keep GameClock.Current detached for the suite's duration. The harness clock is a FixedStep
    // one nothing steps, so FrameDt is 0 and every check would pass vacuously.
    [Suite("puffer-modes",
        "every continuous emitter path through Emit/Stop (plus Burst), driven through a fake renderer with no GPU")]
    internal static void PufferModes(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr effect readers");

        var burstState = PufferState.Load(ctx.ZrdrPath, "flame_ball.json", "fierypuffer");
        var trailState = PufferState.Load(ctx.ZrdrPath, "pufftrails.json", "smokepuffer");
        var speedCueState = PufferState.Load(
            SessionPaths.ChapterZrdr(ctx.DataRoot, ctx.Chapter), "speed_cue.json", "cuepuffer1");
        var speedCue2 = PufferState.Load(
            SessionPaths.ChapterZrdr(ctx.DataRoot, ctx.Chapter), "speed_cue.json", "cuepuffer2");
        var speedCue3 = PufferState.Load(
            SessionPaths.ChapterZrdr(ctx.DataRoot, ctx.Chapter), "speed_cue.json", "cuepuffer3");
        ctx.Check(burstState != null, $"flame_ball.json defines fierypuffer");
        ctx.Check(trailState != null, $"pufftrails.json defines smokepuffer");
        ctx.Check(speedCueState != null, $"{ctx.Chapter} speed_cue.json defines cuepuffer1");
        ctx.Check(speedCue2 != null, $"{ctx.Chapter} speed_cue.json defines cuepuffer2");
        ctx.Check(speedCue3 != null, $"{ctx.Chapter} speed_cue.json defines cuepuffer3");
        if (burstState == null || trailState == null || speedCueState == null
            || speedCue2 == null || speedCue3 == null)
            return;

        // The authored inputs every expected count below is derived from. Asserted rather than
        // assumed: a reader change must fail here, naming itself, rather than silently re-baselining
        // the mode assertions that read off it.
        ctx.Same(18, burstState.Number, $"fierypuffer NUMBER");
        ctx.Check(Mathf.IsEqualApprox(0.2f, burstState.TimeInterval), $"fierypuffer TIME_INTERVAL is 0.2 s");
        ctx.Check(Mathf.IsEqualApprox(1f, burstState.LifetimeMax), $"fierypuffer LIFETIME_RANGE max is 1 s");
        ctx.Same(6, burstState.TextureSequence.Count, $"fierypuffer flipbook frames");
        ctx.Same(0, burstState.Colors.Count, $"fierypuffer has no COLORS ramp");
        ctx.Check(Mathf.IsEqualApprox(2f, trailState.DistanceInterval), $"smokepuffer DISTANCE_INTERVAL is 2 m");
        ctx.Check(trailState.Colors.Count > 0, $"smokepuffer carries a COLORS ramp");
        ctx.Check(Mathf.IsEqualApprox(0.1f, trailState.TimeInterval),
            $"smokepuffer authors no TIME_INTERVAL, the still-host cadence is the synthetic 0.1 s");
        ctx.Same(1, trailState.Number, $"smokepuffer authors no NUMBER, a still-host batch is one puff");

        // Detached for the duration: the harness's own clock is a FixedStep one nothing steps, so
        // left installed every emitter tick would advance no sim and every check would pass
        // vacuously. The suites below drive their own dt directly instead.
        var clock = GameClock.Current;
        GameClock.Current = null;
        try
        {
            PufferBurstMode(ctx, burstState);
            PufferSustainMode(ctx, burstState);
            PufferSustainSubFrameEmission(ctx);
            PufferTrailMode(ctx, trailState);
            PufferTrailOffset(ctx, speedCueState);
            SpeedCueBands(ctx, speedCueState, speedCue2, speedCue3);
            SpeedCueChapterVariants(ctx);
            SpeedCueLateralSpread(ctx);
            PufferStillSputter(ctx, trailState);
            PufferUnauthoredCadence(ctx);
            PufferStaticBurn(ctx, trailState);
            PufferStopRevive(ctx, trailState);
            PufferTeleportGuard(ctx, trailState);
            PufferStartAgeBehavior(ctx);
        }
        finally
        {
            GameClock.Current = clock;
        }
    }

    // The particle integrator against the original's own arithmetic (docs/org/puffer.md), on a state
    // built here rather than loaded, because an authored puffer's random draws would obscure the
    // closed form. Three claims, each with the control that makes its "unchanged" readable: at zero
    // wind the coupling is algebraically absent, acceleration is applied after the position step and
    // before the damp, and WIND_FACTOR 0 is becalmed while 1 is fully carried behind the friction gate.
    [Suite("puffer-wind",
        "the traced integration order (position on last frame's velocity, then accel, then damp) "
        + "and friction damping toward the WIND rather than toward rest, WIND_FACTOR and all (B6)")]
    internal static void PufferWind(TestContext ctx)
    {
        var clock = GameClock.Current;
        GameClock.Current = null;
        try
        {
            PufferWindZeroIsNoOp(ctx);
            PufferAccelOrder(ctx);
            PufferWindFactorCoupling(ctx);
            PufferFrictionlessIgnoresWind(ctx);
            PufferWindGustModel(ctx);
        }
        finally
        {
            GameClock.Current = clock;
        }
    }

    // ---- the camera-distance fade ---------------------------------------------------------------

    // The distance alpha, driven entirely off the numbers two shipped readers author.
    // Every distance below is a VIEW-SPACE DEPTH: the camera sits at the origin looking down −Z
    // (Godot's forward), so a particle placed at `(0, 0, −d)` is at depth `d`, and one
    // pushed sideways is deliberately used to prove the measure is depth and not range.
    [Suite("puffer-distance-fade",
        "the NEAR_FADE/FAR_FADE camera-distance alpha and its two culls, against the shipped "
        + "bands of C3's spew_puffer and volcanosmoke, the cross-wire included (C7), and the "
        + "most-favourable-pane rule every viewer gets an answer from (B11)")]
    internal static void PufferDistanceFade(TestContext ctx)
    {
        var clock = GameClock.Current;
        GameClock.Current = null;
        try
        {
            PufferFadeBands(ctx);
            PufferFadeCrossWire(ctx);
            PufferFadeSwitchesOff(ctx);
            PufferFadeNoCameraNoFade(ctx);
            PufferFadeEveryPane(ctx);
        }
        finally
        {
            GameClock.Current = clock;
        }
    }

    // One particle that never moves and never dies, carrying a COLORS ramp so the drawn
    // alpha channel is the DISTANCE alpha alone: the ramp path writes `1 × distAlpha` and
    // leaves the life envelope out of it (the ramp's own alpha rides in the colour, exactly as
    // the shader's `v_alpha × v_color.a` expects).
    internal static PufferState FadeTestState(string name,
        float nearStart, float nearEnd, float farStart, float farEnd) => new()
        {
            Name = name,
            Number = 1,
            TimeInterval = 10f,
            SizeMin = 1f,
            SizeMax = 1f,
            LifetimeMin = 1000f,
            LifetimeMax = 1000f,
            NearFadeStart = nearStart,
            NearFadeEnd = nearEnd,
            FarFadeStart = farStart,
            FarFadeEnd = farEnd,
            Textures = new[] { "smoke101" },
            Colors = new[] { (0f, Colors.White), (1f, Colors.White) },
        };

    // Bursts one particle at `at` and returns the alpha it was drawn
    // with, or null when it was discarded. The burst is fired AT the point (burst mode stores
    // positions in the node's own frame, whose origin is the burst point), and a single
    // `_Process` at a dt small enough to leave the particle where it was born runs the draw.
    internal static float? FadeAlphaAt(TestContext ctx, PufferState state, Vector3 at,
        EffectAmbience ambience, PufferFadeSwitches? switches = null)
    {
        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu, activeDuration: 0.01f, ambience: ambience,
            fade: switches);
        ctx.Host.AddChild(puffer);
        try
        {
            puffer.Burst(at);
            puffer._Process(1f / 60f);
            ctx.Same(1, puffer.LiveCount,
                $"{state.Name}: the particle stays ALIVE whatever the fade decides, it is a draw rule, not a reaper");
            return gpu.Shown > 0 ? gpu.LastFrame[0].Alpha : null;
        }
        finally
        {
            puffer.Free();
        }
    }

    // C3's `spew_puffer` exactly as `waterfalls.zrd.json` authors it,
    // `FADE_RANGE [300, 400]`, `NEAR_FADE [40, 5]`, the only puffer in the install
    // carrying both a near fade and the tightest far band.
    internal static void PufferFadeBands(TestContext ctx)
    {
        var state = FadeTestState("spew_puffer", 40f, 5f, 300f, 400f);
        var amb = new EffectAmbience();
        amb.SetCamera(Vector3.Zero, Vector3.Forward);   // at the origin, looking down −Z

        ctx.Check(FadeAlphaAt(ctx, state, new Vector3(0f, 0f, -20f), amb) == null,
            $"inside NEAR_FADE[0] the particle is culled outright, not faded (20 m < 40 m)");
        ctx.Check(FadeAlphaAt(ctx, state, new Vector3(0f, 0f, -100f), amb) is { } mid
                  && Mathf.IsEqualApprox(mid, 1f),
            $"between the bands it draws at full alpha");
        ctx.Check(FadeAlphaAt(ctx, state, new Vector3(0f, 0f, -300f), amb) is { } edge
                  && Mathf.IsEqualApprox(edge, 1f),
            $"FADE_RANGE[0] is the LAST full-alpha distance, not the first faded one");

        float? half = FadeAlphaAt(ctx, state, new Vector3(0f, 0f, -350f), amb);
        ctx.Check(half is { } h && Mathf.IsEqualApprox(h, 0.5f, 1e-4f),
            $"half way across the far band the alpha is half alpha={half}");
        ctx.Check(FadeAlphaAt(ctx, state, new Vector3(0f, 0f, -450f), amb) == null,
            $"past FADE_RANGE[1] it is discarded (450 m > 400 m)");
        ctx.Check(FadeAlphaAt(ctx, state, new Vector3(0f, 0f, 100f), amb) == null,
            $"and a particle BEHIND the camera is culled by the unauthored-near rule at depth 0");

        // Depth, not range: 350 m ahead and 600 m sideways is 694 m away and still mid-band.
        float? sideways = FadeAlphaAt(ctx, state, new Vector3(600f, 0f, -350f), amb);
        ctx.Check(sideways is { } s && Mathf.IsEqualApprox(s, 0.5f, 1e-4f),
            $"the measure is VIEW-SPACE DEPTH: 600 m off-axis does not change the fade alpha={sideways}");
        float range = new Vector3(600f, 0f, -350f).Length();
        ctx.Check(range > 400f,
            $"ABLE-TO-FAIL CONTROL: that same point is {range:0} m away, a euclidean-range implementation would have discarded it");
    }

    // The cross-wire, reproduced rather than repaired: the near ramp's origin is FAR_FADE[0]. C3's
    // volcanosmoke is the only puffer in the install whose near pair ascends, so it is the only one
    // reaching the ramp branch, where the wrong origin drives alpha negative and the gate culls it.
    internal static void PufferFadeCrossWire(TestContext ctx)
    {
        var state = FadeTestState("volcanosmoke", 1f, 75f, 2000f, 3000f);
        var amb = new EffectAmbience();
        amb.SetCamera(Vector3.Zero, Vector3.Forward);

        ctx.Check(FadeAlphaAt(ctx, state, new Vector3(0f, 0f, -50f), amb) == null,
            $"at 50 m volcanosmoke is culled: the near ramp reads FAR_FADE[0] (2000), so its alpha is (50-2000)/74");
        ctx.Check(FadeAlphaAt(ctx, state, new Vector3(0f, 0f, -80f), amb) is { } beyond
                  && Mathf.IsEqualApprox(beyond, 1f),
            $"past NEAR_FADE[1] it pops in at full alpha, a hard edge at 75 m, not a 1-to-75 m fade-in");
        // The repair we deliberately did NOT make, stated as a number so it cannot creep back in.
        float repaired = (50f - 1f) / (75f - 1f);
        ctx.Check(repaired > 0.6f,
            $"ABLE-TO-FAIL CONTROL: with NEAR_FADE[0] as the ramp origin the 50 m sample would have drawn at alpha {repaired:0.000}");
    }

    // The author's three switches, each shown to switch. Config is file-backed with no
    // setter, so these come through `CreateWith`'s test-only override, see
    // PufferFadeSwitches.
    internal static void PufferFadeSwitchesOff(TestContext ctx)
    {
        var state = FadeTestState("spew_puffer", 40f, 5f, 300f, 400f);
        var amb = new EffectAmbience();
        amb.SetCamera(Vector3.Zero, Vector3.Forward);
        var near = new Vector3(0f, 0f, -20f);
        var far = new Vector3(0f, 0f, -450f);
        var band = new Vector3(0f, 0f, -350f);

        var noNear = new PufferFadeSwitches(DistanceFade: true, FarCull: true, NearCull: false);
        ctx.Check(FadeAlphaAt(ctx, state, near, amb, noNear) is { } n && Mathf.IsEqualApprox(n, 1f),
            $"puffer.nearCull false draws the particle the camera flew through");

        var noFade = new PufferFadeSwitches(DistanceFade: false, FarCull: true, NearCull: true);
        ctx.Check(FadeAlphaAt(ctx, state, band, amb, noFade) is { } b && Mathf.IsEqualApprox(b, 1f),
            $"puffer.distanceFade false keeps full alpha across the authored band");
        ctx.Check(FadeAlphaAt(ctx, state, far, amb, noFade) == null,
            $"and leaves the far CUTOFF alone, the ramp and the cull are two switches");

        // ⚠ farCull alone is a no-op, and that is a finding, not an oversight: the authored ramp
        // reaches zero at exactly the cutoff distance, so the alpha > 0 gate removes what the cull
        // would have. Asserted so the pairing stays documented in something that runs.
        var noFarCull = new PufferFadeSwitches(DistanceFade: true, FarCull: false, NearCull: true);
        ctx.Check(FadeAlphaAt(ctx, state, far, amb, noFarCull) == null,
            $"puffer.farCull false ALONE changes nothing, past the band the ramp's own alpha is already negative");
        var wideOpen = new PufferFadeSwitches(DistanceFade: false, FarCull: false, NearCull: true);
        ctx.Check(FadeAlphaAt(ctx, state, far, amb, wideOpen) is { } w && Mathf.IsEqualApprox(w, 1f),
            $"it takes distanceFade AND farCull together to keep a distant puffer drawn");

        // The original's own far-band multiplier: below 1 it pushes the fade outward, and it
        // touches the far band only.
        var pushedOut = new PufferFadeSwitches(true, true, true, GlobalFadeFactor: 0.5f);
        var wayOut = new Vector3(0f, 0f, -700f);
        ctx.Check(FadeAlphaAt(ctx, state, wayOut, amb) == null,
            $"ABLE-TO-FAIL CONTROL: at the default factor 700 m is well past the cutoff and culled");
        float? scaled = FadeAlphaAt(ctx, state, wayOut, amb, pushedOut);
        ctx.Check(scaled is { } sc && Mathf.IsEqualApprox(sc, 0.5f, 1e-4f),
            $"globalFadeFactor 0.5 measures that same 700 m as 350 m, half way across the band alpha={scaled}");
        ctx.Check(FadeAlphaAt(ctx, state, new Vector3(0f, 0f, -20f), amb, pushedOut) == null,
            $"and it does NOT reach the near band, which still culls at 20 m on the unscaled depth");
    }

    // No camera published ⇒ no distance fade at all, rather than one measured against
    // the world origin. This is what keeps the unit suites, the plane viewer and the damage lab
    // out of the unauthored near cull at depth 0, and it is the state every OTHER puffer suite
    // runs in, which is why none of them moved.
    internal static void PufferFadeNoCameraNoFade(TestContext ctx)
    {
        var state = FadeTestState("no_camera", 40f, 5f, 300f, 400f);
        var amb = new EffectAmbience();          // wind may be written; a camera never is
        ctx.Check(!amb.HasCamera, $"the ambience under test has no camera");
        ctx.Check(FadeAlphaAt(ctx, state, new Vector3(0f, 0f, -450f), amb) is { } far
                  && Mathf.IsEqualApprox(far, 1f),
            $"with no camera a particle past the far cutoff still draws at full alpha");
        ctx.Check(FadeAlphaAt(ctx, state, new Vector3(0f, 0f, -20f), amb) is { } near
                  && Mathf.IsEqualApprox(near, 1f),
            $"and one inside the near cull draws too");
    }

    // The splitscreen rule: the bands are evaluated against EVERY
    // pane's camera and the particle takes the most favourable answer, so a trail 20 m in front of
    // player 2 draws even while player 1's own camera near-culls it. Driven through the real
    // ViewerSet over real `Camera3D` nodes, which is the seam
    // `WeatherRig.Tick` publishes from.
    internal static void PufferFadeEveryPane(TestContext ctx)
    {
        var state = FadeTestState("spew_puffer", 40f, 5f, 300f, 400f);

        // Two panes looking the same way down −Z, player 2 astern of player 1 by 200 m, the
        // repro's geometry (P2 behind P1, shooting past him).
        var p1 = SuiteViewers.Camera(ctx, Vector3.Zero);
        var p2 = SuiteViewers.Camera(ctx, new Vector3(0f, 0f, 200f));
        try
        {
            var both = new ViewerSet();
            both.Bind(new[] { p1, p2 });
            var amb = new EffectAmbience();
            amb.SetViewers(both);

            var justAheadOfP1 = new Vector3(0f, 0f, -20f);
            ctx.Check(FadeAlphaAt(ctx, state, justAheadOfP1, amb) is { } near
                      && Mathf.IsEqualApprox(near, 1f),
                $"20 m ahead of P1 is inside P1's near cull but 220 m ahead of P2, so it DRAWS, the pane that can see it decides");

            var onlyP1 = new EffectAmbience();
            onlyP1.SetCamera(Vector3.Zero, Vector3.Forward);
            ctx.Check(FadeAlphaAt(ctx, state, justAheadOfP1, onlyP1) == null,
                $"ABLE-TO-FAIL CONTROL: that same particle against P1's camera alone is culled, which is the reported bug");

            ctx.Check(FadeAlphaAt(ctx, state, new Vector3(0f, 0f, 300f), amb) == null,
                $"behind BOTH panes it is still culled, 'any pane' is a union, not a disabled fade");

            // The most FAVOURABLE answer, not the first or the last: P1 reads 350 m (half way
            // across its far band), P2 sits 50 m short of it and reads full alpha.
            var p3 = SuiteViewers.Camera(ctx, new Vector3(0f, 0f, -300f));
            try
            {
                var nearer = new ViewerSet();
                nearer.Bind(new[] { p1, p3 });
                var ambNearer = new EffectAmbience();
                ambNearer.SetViewers(nearer);
                float? shared = FadeAlphaAt(ctx, state, new Vector3(0f, 0f, -350f), ambNearer);
                ctx.Check(shared is { } s && Mathf.IsEqualApprox(s, 1f),
                    $"a particle half-faded for P1 and full-alpha for the nearer pane draws at full alpha={shared}");
                ctx.Check(FadeAlphaAt(ctx, state, new Vector3(0f, 0f, -350f), onlyP1) is { } lone
                          && Mathf.IsEqualApprox(lone, 0.5f, 1e-4f),
                    $"ABLE-TO-FAIL CONTROL: P1 alone reads that same particle at half alpha");
            }
            finally
            {
                p3.Free();
            }

            // One pane is the old behaviour exactly, the single-viewer case must not move, and it
            // is what every capture, freecam shot and single-player session runs.
            var alone = new ViewerSet();
            alone.Bind(new[] { p1 });
            var ambAlone = new EffectAmbience();
            ambAlone.SetViewers(alone);
            float? single = FadeAlphaAt(ctx, state, new Vector3(0f, 0f, -350f), ambAlone);
            ctx.Check(single is { } one && Mathf.IsEqualApprox(one, 0.5f, 1e-4f),
                $"with ONE viewer the answer is that viewer's own, unchanged alpha={single}");
        }
        finally
        {
            p1.Free();
            p2.Free();
        }
    }

    // ---- PRIORITY inflates the sprite -------------------------------------------------------------

    // A PRIORITY 10 puffer spawns particles exactly 20% larger
    // than the same state at PRIORITY 0, `1 + 0.02·10 = 1.2`, the hardware-path `K`
    // (`PriorityScaleDefault`). Burst mode, NUMBER 1, a degenerate SIZE_RANGE so the drawn
    // size is deterministic and the only thing that can move it is PRIORITY.
    [Suite("puffer-priority-size",
        "PRIORITY inflates the drawn sprite by 1 + K·PRIORITY, folded into BaseSize at spawn (C8)")]
    internal static void PufferPrioritySize(TestContext ctx)
    {
        static PufferState State(float priority) => new()
        {
            Name = "test_priority",
            Number = 1,
            TimeInterval = 10f,
            SizeMin = 2f,
            SizeMax = 2f,
            LifetimeMin = 100f,
            LifetimeMax = 100f,
            Priority = priority,
            Textures = new[] { "smoke101" },
        };

        static float DrawnSize(TestContext ctx, PufferState state)
        {
            var gpu = new RecordingEmitterRenderer();
            var puffer = Puffer.CreateWith(state, gpu, activeDuration: 0.01f);
            ctx.Host.AddChild(puffer);
            try
            {
                puffer.Burst(Vector3.Zero);
                puffer._Process(1f / 60f);
                ctx.Same(1, gpu.Shown, $"{state.Name} priority={state.Priority}: exactly one particle is under test");
                return gpu.LastFrame[0].Size;
            }
            finally
            {
                puffer.Free();
            }
        }

        float baseline = DrawnSize(ctx, State(0f));
        float inflated = DrawnSize(ctx, State(10f));

        ctx.Check(Mathf.IsEqualApprox(baseline, 4f, 1e-4f),
            $"PRIORITY 0 draws at the plain size (radius 2 × diameter convention, A1) size={baseline:0.0000}");
        ctx.Check(Mathf.IsEqualApprox(inflated, baseline * 1.2f, 1e-4f),
            $"PRIORITY 10 draws 20% larger than PRIORITY 0, 1 + 0.02·10 baseline={baseline:0.0000} inflated={inflated:0.0000}");
    }

    // One particle, no randomness: NUMBER 1, a degenerate random-velocity range (min ==
    // max, so the draw is exact), no deviation, no growth, no ramps. Burst mode, so the single
    // batch lands at t = 0 at the burst point.
    internal static PufferState WindTestState(string name, Vector3 v0, Vector3 accel,
        float friction, float windFactor) => new()
        {
            Name = name,
            Number = 1,
            TimeInterval = 10f,          // one batch only, inside the 0.01 s active duration below
            SizeMin = 1f,
            SizeMax = 1f,
            LifetimeMin = 100f,
            LifetimeMax = 100f,
            MinRandomVelocity = v0,
            MaxRandomVelocity = v0,
            WorldAcceleration = accel,
            Friction = friction,
            WindFactor = windFactor,
            Textures = new[] { "smoke101" },
        };

    // Runs one WindTestState particle for `frames` steps of
    // `dt` and returns its displacement. The burst is fired at the world origin
    // and a burst puffer's particles are stored in its own (there, identity) frame, so the
    // written position IS the displacement.
    internal static Vector3 RunWindParticle(TestContext ctx, PufferState state, EffectAmbience ambience,
        int frames, float dt)
    {
        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu, activeDuration: 0.01f, ambience: ambience);
        ctx.Host.AddChild(puffer);
        try
        {
            puffer.Burst(Vector3.Zero);
            for (int i = 0; i < frames; i++)
                puffer._Process(dt);
            ctx.Same(1, gpu.Shown, $"{state.Name}: exactly one particle is under test");
            return gpu.LastFrame.Count > 0 ? gpu.LastFrame[0].Position : Vector3.Zero;
        }
        finally
        {
            puffer.Free();
        }
    }

    // Zero wind means the coupling term vanishes, so a friction particle decays to rest on the pure
    // damped curve: v_n = v0*damp^n and pos_n = v0*dt*(1 - damp^n)/(1 - damp), the geometric sum of
    // the position step, which the traced order takes before the damp.
    // ⚠ Keep the able-to-fail control: the identical emitter in a 10 m/s wind must land somewhere
    // else, or the match is a statement about a branch nothing exercised (docs/verification.md).
    internal static void PufferWindZeroIsNoOp(TestContext ctx)
    {
        const float Dt = 1f / 60f, Friction = 3f;
        const int Frames = 120;
        var v0 = new Vector3(20f, 0f, 0f);
        float damp = Mathf.Exp(-Friction * Dt);
        float expectedX = v0.X * Dt * (1f - Mathf.Pow(damp, Frames)) / (1f - damp);

        var still = new EffectAmbience();   // never written: Wind stays Vector3.Zero
        var calm = RunWindParticle(ctx, WindTestState("wind_zero", v0, Vector3.Zero, Friction, 1f),
            still, Frames, Dt);
        ctx.Check(Mathf.IsEqualApprox(calm.X, expectedX, 0.01f),
            $"at zero wind a friction particle rides the pure damped curve x={calm.X:0.0000} expected={expectedX:0.0000}");
        ctx.Check(Mathf.Abs(calm.Y) < 1e-4f && Mathf.Abs(calm.Z) < 1e-4f,
            $"and moves in no other axis y={calm.Y:0.000000} z={calm.Z:0.000000}");
        // 2 s at friction 3 leaves damp^120 = e^-6 = 0.0025 of the launch speed: at rest, and the
        // remaining travel per frame is below a millimetre.
        ctx.Check(Mathf.Pow(damp, Frames) < 0.01f,
            $"120 frames at friction 3 really is 'to rest' remaining={Mathf.Pow(damp, Frames):0.0000}");

        var blowing = new EffectAmbience();
        blowing.SetWind(new Vector3(0f, 0f, 10f));
        var carried = RunWindParticle(ctx, WindTestState("wind_control", v0, Vector3.Zero, Friction, 1f),
            blowing, Frames, Dt);
        ctx.Check(carried.Z > 5f,
            $"ABLE-TO-FAIL CONTROL: the same particle in a 10 m/s crosswind is carried instead z={carried.Z:0.000}");
        ctx.Check(Mathf.IsEqualApprox(carried.X, expectedX, 0.01f),
            $"and the crosswind leaves the unblown axis alone x={carried.X:0.0000}");
    }

    // The reorder, isolated: friction 0, no wind, a pure world acceleration. The traced
    // order steps position on LAST frame's velocity and only then adds `a·dt`, giving
    // `pos_n = a·dt²·n(n−1)/2`. Our old order (`v += a·dt` first, position after) gave
    // `a·dt²·n(n+1)/2`, larger by exactly `a·dt²·n`, i.e. one frame of the current
    // velocity, which is the whole of the difference and is asserted as such.
    internal static void PufferAccelOrder(TestContext ctx)
    {
        const float Dt = 1f / 60f;
        const int Frames = 60;
        var accel = new Vector3(0f, -9.8f, 0f);
        float traced = accel.Y * Dt * Dt * Frames * (Frames - 1) / 2f;
        float oldOrder = accel.Y * Dt * Dt * Frames * (Frames + 1) / 2f;

        var still = new EffectAmbience();
        var end = RunWindParticle(ctx, WindTestState("accel_order", Vector3.Zero, accel, 0f, 1f),
            still, Frames, Dt);

        ctx.Check(Mathf.IsEqualApprox(end.Y, traced, 0.0005f),
            $"accel lands after the position step y={end.Y:0.00000} traced={traced:0.00000}");
        ctx.Check(!Mathf.IsEqualApprox(end.Y, oldOrder, 0.0005f),
            $"and NOT where the old order put it old={oldOrder:0.00000}");
        ctx.Check(Mathf.IsEqualApprox(oldOrder - traced, accel.Y * Dt * Dt * Frames, 0.0005f),
            $"the gap is exactly one frame of the final velocity gap={oldOrder - traced:0.00000}");
    }

    // The per-puffer coupling. Both particles start at rest with no acceleration, so the
    // ONLY thing that can move them is the wind: `WIND_FACTOR` 0 must therefore not move at
    // all, and 1 must converge on the wind velocity. The carried one's closed form is exact,
    // `v_n = w(1 − damp^n)`, `pos_n = w·dt·(n − (1 − damp^n)/(1 − damp))`.
    internal static void PufferWindFactorCoupling(TestContext ctx)
    {
        const float Dt = 1f / 60f, Friction = 3f;
        const int Frames = 120;
        var wind = new Vector3(8f, 0f, 0f);
        float damp = Mathf.Exp(-Friction * Dt);
        float geometric = (1f - Mathf.Pow(damp, Frames)) / (1f - damp);
        float carriedX = wind.X * Dt * (Frames - geometric);

        var blowing = new EffectAmbience();
        blowing.SetWind(wind);

        var uncoupled = RunWindParticle(ctx,
            WindTestState("wf_zero", Vector3.Zero, Vector3.Zero, Friction, 0f), blowing, Frames, Dt);
        ctx.Check(uncoupled.Length() < 1e-5f,
            $"WIND_FACTOR 0 is genuinely becalmed in an 8 m/s wind drift={uncoupled.Length():0.000000}");

        var carried = RunWindParticle(ctx,
            WindTestState("wf_one", Vector3.Zero, Vector3.Zero, Friction, 1f), blowing, Frames, Dt);
        ctx.Check(Mathf.IsEqualApprox(carried.X, carriedX, 0.01f),
            $"WIND_FACTOR 1 is fully carried x={carried.X:0.0000} expected={carriedX:0.0000}");

        var half = RunWindParticle(ctx,
            WindTestState("wf_half", Vector3.Zero, Vector3.Zero, Friction, 0.3f), blowing, Frames, Dt);
        ctx.Check(Mathf.IsEqualApprox(half.X, carriedX * 0.3f, 0.01f),
            $"and the coupling is linear in WIND_FACTOR, 0.3 drifts 0.3× as far x={half.X:0.0000}");
    }

    // The engine's own gate: the whole damp-toward-wind block sits inside
    // `if (friction != 0)`, so a frictionless puffer is untouched by any wind at any factor.
    // It is load-bearing only because there is a wind term inside the block: with an unconditional
    // `Exp(0) == 1` damp and no wind the gate would be the identity.
    internal static void PufferFrictionlessIgnoresWind(TestContext ctx)
    {
        const float Dt = 1f / 60f;
        const int Frames = 120;
        var blowing = new EffectAmbience();
        blowing.SetWind(new Vector3(50f, 0f, 0f));

        var end = RunWindParticle(ctx,
            WindTestState("no_friction", Vector3.Zero, Vector3.Zero, 0f, 1f), blowing, Frames, Dt);
        ctx.Check(end.Length() < 1e-5f,
            $"a FRICTION 0 puffer feels no wind at all drift={end.Length():0.000000}");
    }

    // The gust itself (WorldWind), against the shipped authored values,
    // `STATIC_VELOCITY (0,2,0)`, `RANDOM_MAX_SPEED 10`, `RANDOM_ACCEL 5`,
    // `RANDOM_ANG_VEL 5`, which every one of the install's 53 weather readers carries.
    internal static void PufferWindGustModel(TestContext ctx)
    {
        var wind = new WorldWind(new Vector3(0f, 2f, 0f), 10f, 5f, 5f, new System.Random(7));
        ctx.Check(wind.Velocity == new Vector3(0f, 2f, 0f),
            $"frame 0 is the static vector alone (the engine's globals start at BSS zero)");

        float maxHorizontal = 0f;
        bool everGusted = false;
        for (int i = 0; i < 2000; i++)
        {
            wind.Step(1f / 60f);
            ctx.Check(wind.Magnitude >= 0f && wind.Magnitude <= 10f,
                $"gust magnitude stays in [0, RANDOM_MAX_SPEED] mag={wind.Magnitude:0.000}");
            if (!Mathf.IsEqualApprox(wind.Velocity.Y, 2f))
                ctx.Check(false, $"the gust is horizontal, Y must stay the static 2 m/s, saw {wind.Velocity.Y}");
            float h = new Vector2(wind.Velocity.X, wind.Velocity.Z).Length();
            maxHorizontal = Mathf.Max(maxHorizontal, h);
            if (h > 1f)
                everGusted = true;
        }
        ctx.Check(everGusted, $"the gust actually blows over 2000 frames");
        ctx.Check(maxHorizontal <= 10.001f, $"and never exceeds the ceiling max={maxHorizontal:0.000}");

        // The still-air wind a mission without a weather.json gets: no draws, no drift, ever.
        var still = WorldWind.Still();
        for (int i = 0; i < 100; i++)
            still.Step(1f / 60f);
        ctx.Check(still.Velocity == Vector3.Zero, $"WorldWind.Still() never blows v={still.Velocity}");
    }

    // A moving DISTANCE_INTERVAL host keeps AT_NODE's offset in the host frame for every
    // emitted puff, not only the still-host fallback. The retail speed cue is the canary: its
    // player,0,0,-60 attachment is what places the wisps 60 m ahead of the aircraft.
    internal static void PufferTrailOffset(TestContext ctx, PufferState state)
    {
        ctx.Check(state.AtNodeOffset.IsEqualApprox(new Vector3(0f, 0f, -60f)),
            $"cuepuffer1 carries its authored 60 m forward AT_NODE offset");
        ctx.Check(Mathf.IsEqualApprox(30f, state.DistanceInterval),
            $"cuepuffer1 emits every authored 30 m");
        state.DeviationDistance = 0f;
        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu);
        ctx.Host.AddChild(puffer);
        try
        {
            const float dt = 1f / 60f;
            puffer.Emit(Vector3.Zero, Basis.Identity, dt);
            puffer.Emit(new Vector3(60f, 0f, 0f), Basis.Identity, dt);
            puffer._Process(dt);
            ctx.Same(3, gpu.Shown, $"homing puff plus two distance puffs were emitted");
            ctx.Check(gpu.LastFrame.All(p => Mathf.Abs(p.Position.Z + 60f) < 0.1f),
                $"every moving-trail puff keeps the authored -60 m local-Z offset");
        }
        finally
        {
            puffer.Free();
        }
    }

    // The speed-cue adapter selects the retail altitude bands, preserves the selected
    // puffer above the final authored threshold, suppresses every cue near ground, and Reset
    // removes live particles so a respawn cannot bridge positions.
    internal static void SpeedCueBands(TestContext ctx, params PufferState[] states)
    {
        var gpu = new RecordingEmitterRenderer[3];
        var puffer = new Puffer[3];
        for (int i = 0; i < 3; i++)
        {
            states[i].DeviationDistance = 0f;
            gpu[i] = new RecordingEmitterRenderer();
            puffer[i] = Puffer.CreateWith(states[i], gpu[i]);
            ctx.Host.AddChild(puffer[i]);
        }
        try
        {
            var cue = SpeedCue.CreateWith(puffer[0], puffer[1], puffer[2]);
            const float dt = 1f / 60f;
            void Tick()
            {
                foreach (var p in puffer)
                    p._Process(dt);
            }

            cue.Update(dt, Vector3.Zero, Basis.Identity, 700f, 100f);
            Tick();
            cue.Update(dt, new Vector3(60f, 0f, 0f), Basis.Identity, 700f, 100f);
            Tick();
            ctx.Same(3, gpu[0].Shown, $"below 800 m cue1 emits at its 30 m interval");
            ctx.Same(-1, gpu[1].Shown, $"below 800 m cue2 has never started");
            ctx.Same(-1, gpu[2].Shown, $"below 800 m cue3 has never started");

            for (int i = 0; i < 6; i++)
            {
                cue.Update(dt, new Vector3(60f, 0f, 0f), Basis.Identity, 850f, 100f);
                Tick();
            }
            Tick();
            cue.Update(dt, new Vector3(90f, 0f, 0f), Basis.Identity, 850f, 100f);
            Tick();
            ctx.Same(3, gpu[1].Shown, $"800-900 m cue2 emits at its 15 m interval");

            for (int i = 0; i < 6; i++)
            {
                cue.Update(dt, new Vector3(90f, 0f, 0f), Basis.Identity, 1000f, 100f);
                Tick();
            }
            Tick();
            cue.Update(dt, new Vector3(106f, 0f, 0f), Basis.Identity, 1000f, 100f);
            Tick();
            ctx.Same(3, gpu[2].Shown, $"900-1200 m cue3 emits at its 8 m interval");

            cue.Reset();
            ctx.Same(0, gpu[0].Shown, $"speed-cue reset clears cue1 particles");
            ctx.Same(0, gpu[1].Shown, $"speed-cue reset clears cue2 particles");
            ctx.Same(0, gpu[2].Shown, $"speed-cue reset clears cue3 particles");

            cue.Update(dt, Vector3.Zero, Basis.Identity, 700f, 49f);
            Tick();
            ctx.Same(0, gpu[0].Shown, $"within 50 m of ground no speed cue starts");

            for (int i = 0; i < 6; i++)
                cue.Update(dt, Vector3.Zero, Basis.Identity, 850f, 100f);
            for (int i = 0; i < 6; i++)
                cue.Update(dt, Vector3.Zero, Basis.Identity, 1600f, 100f);
            cue.Update(dt, new Vector3(30f, 0f, 0f), Basis.Identity, 1600f, 100f);
            Tick();
            ctx.Same(4, gpu[1].Shown,
                $"above 1500 m preserves the previously selected puffer, matching the empty ELSE");
        }
        finally
        {
            foreach (var p in puffer)
                p.Free();
        }
    }

    // The one aspect-dependent term in the effect, over a freshly loaded cuepuffer1 rather than the
    // states the checks above blank. A 16:9 pane must spawn every wisp 4/3 as far off the flight
    // line as the same draw puts it at 4:3, and no further up, down, ahead or astern; an emitter no
    // SpeedCue drives must not move at all. Each population runs under a pinned RNG stream, so the
    // three are the SAME draws and every comparison below is per particle rather than statistical.
    internal static void SpeedCueLateralSpread(TestContext ctx)
    {
        const float Wide = 16f / 9f;
        var fresh = PufferState.Load(
            SessionPaths.ChapterZrdr(ctx.DataRoot, ctx.Chapter), "speed_cue.json", "cuepuffer1");
        ctx.Check(fresh != null, $"{ctx.Chapter} speed_cue.json still defines cuepuffer1");
        if (fresh == null)
            return;
        float half = 0.5f * fresh.DeviationDistance;
        ctx.Check(half > 0f, $"cuepuffer1 authors a deviation cube d={fresh.DeviationDistance:0.0} m");

        ctx.Check(Mathf.IsEqualApprox(1f, SpeedCue.LateralSpreadFor(SpeedCue.AuthoredAspect)),
            $"a 4:3 pane spreads nothing, the authored cube stands exactly where the data puts it");
        ctx.Check(Mathf.IsEqualApprox(4f / 3f, SpeedCue.LateralSpreadFor(Wide), 1e-5f),
            $"a 16:9 pane widens the lateral half-width by 1.3333, the aspect over 4:3");
        ctx.Check(Mathf.IsEqualApprox(1f, SpeedCue.LateralSpreadFor(1f)),
            $"and a pane NARROWER than 4:3 never spawns inside the authored cube");

        var authored = CueSpawnOffsets(ctx, fresh, Basis.Identity, SpeedCue.AuthoredAspect, true);
        var wide = CueSpawnOffsets(ctx, fresh, Basis.Identity, Wide, true);
        var generic = CueSpawnOffsets(ctx, fresh, Basis.Identity, Wide, false);
        int n = authored.Count;
        ctx.Check(n > 100 && wide.Count == n && generic.Count == n,
            $"the pinned stream gives three populations of one size authored={n} wide={wide.Count} generic={generic.Count}");
        if (n == 0 || wide.Count != n || generic.Count != n)
            return;

        float lateralGap = 0f, verticalGap = 0f, forwardGap = 0f, genericGap = 0f;
        for (int i = 0; i < n; i++)
        {
            lateralGap = Mathf.Max(lateralGap, Mathf.Abs(wide[i].X - (authored[i].X * 4f / 3f)));
            verticalGap = Mathf.Max(verticalGap, Mathf.Abs(wide[i].Y - authored[i].Y));
            forwardGap = Mathf.Max(forwardGap, Mathf.Abs(wide[i].Z - authored[i].Z));
            genericGap = Mathf.Max(genericGap, (generic[i] - authored[i]).Length());
        }
        ctx.Check(lateralGap < 1e-3f,
            $"at 16:9 every wisp spawns exactly 4/3 as far off the flight line worst={lateralGap:0.000000} m");
        ctx.Check(verticalGap < 1e-6f && forwardGap < 1e-6f,
            $"and at exactly the same height and the same distance ahead dy={verticalGap:0.000000} dz={forwardGap:0.000000}");
        ctx.Check(genericGap < 1e-6f,
            $"an emitter no SpeedCue drives takes no pane at all, its cube is byte-identical worst={genericGap:0.000000} m");

        var hAuthored = HalfWidths(authored);
        var hWide = HalfWidths(wide);
        ctx.Check(Mathf.IsEqualApprox(hAuthored.X, half, 0.2f) && Mathf.IsEqualApprox(hAuthored.Y, half, 0.2f)
                  && Mathf.IsEqualApprox(hAuthored.Z, half, 0.2f),
            $"the 4:3 population fills the authored cube half={half:0.00} m measured={hAuthored}");
        ctx.Check(Mathf.IsEqualApprox(hWide.X / hAuthored.X, 4f / 3f, 1e-3f),
            $"the 16:9 population's lateral half-width is 1.3333x the 4:3 one {hAuthored.X:0.000} m to {hWide.X:0.000} m");
        ctx.Check(hWide.X - hAuthored.X > 1f,
            $"ABLE-TO-FAIL CONTROL: that is a {hWide.X - hAuthored.X:0.00} m widening, far past any float noise");
        ctx.Note($"cue spread at d={fresh.DeviationDistance:0.0} m over {n} spawns: lateral {hAuthored.X:0.000} -> {hWide.X:0.000} m, vertical {hAuthored.Y:0.000} -> {hWide.Y:0.000} m, forward {hAuthored.Z:0.000} -> {hWide.Z:0.000} m");

        // Emitter frame, not world axes: a quarter turn puts the aircraft's right axis on world Z,
        // and the widening must follow it there and leave world X alone.
        var yawed = new Basis(Vector3.Up, Mathf.Pi / 2f);
        var turnedAuthored = CueSpawnOffsets(ctx, fresh, yawed, SpeedCue.AuthoredAspect, true);
        var turnedWide = CueSpawnOffsets(ctx, fresh, yawed, Wide, true);
        float turnedForward = 0f, turnedLateral = 0f;
        for (int i = 0; i < Mathf.Min(turnedAuthored.Count, turnedWide.Count); i++)
        {
            turnedForward = Mathf.Max(turnedForward,
                Mathf.Abs(turnedWide[i].Z - (turnedAuthored[i].Z * 4f / 3f)));
            turnedLateral = Mathf.Max(turnedLateral, Mathf.Abs(turnedWide[i].X - turnedAuthored[i].X));
        }
        ctx.Check(turnedForward < 1e-3f && turnedLateral < 1e-3f,
            $"a quarter turn carries the widening onto world Z and leaves world X alone dz={turnedForward:0.000000} dx={turnedLateral:0.000000}");
    }

    // The largest offset on each axis, which for a uniform cube over hundreds of spawns is its
    // half-width to well under a percent.
    internal static Vector3 HalfWidths(IReadOnlyList<Vector3> offsets)
    {
        var worst = Vector3.Zero;
        foreach (var o in offsets)
            worst = new Vector3(Mathf.Max(worst.X, Mathf.Abs(o.X)),
                Mathf.Max(worst.Y, Mathf.Abs(o.Y)), Mathf.Max(worst.Z, Mathf.Abs(o.Z)));
        return worst;
    }

    // One cue population's spawn offsets from its own emission point, under a pinned RNG stream so
    // two runs draw the same numbers. The aircraft is held still, which is what puts the emitter on
    // the synthetic still-host cadence at one fixed point, and `_Process(0)` writes the spawn
    // positions themselves rather than aged ones. `throughCue` drives the real `SpeedCue`, the only
    // thing that applies a pane's aspect; false drives the emitter directly, the generic path.
    internal static List<Vector3> CueSpawnOffsets(TestContext ctx, PufferState state, Basis basis,
        float aspect, bool throughCue)
    {
        const float Dt = 0.1f, Altitude = 700f, Agl = 100f;
        const int Steps = 400;
        var at = new Vector3(0f, Altitude, 0f);
        ulong master = Rng.Master;
        bool pinned = Rng.Pinned;
        Rng.Reset(20250101UL, true);
        var gpu = new RecordingEmitterRenderer();
        var puffers = new[]
        {
            Puffer.CreateWith(state, gpu),
            Puffer.CreateWith(state, new RecordingEmitterRenderer()),
            Puffer.CreateWith(state, new RecordingEmitterRenderer()),
        };
        foreach (var p in puffers)
            ctx.Host.AddChild(p);
        try
        {
            var cue = SpeedCue.CreateWith(puffers[0], puffers[1], puffers[2]);
            for (int i = 0; i < Steps; i++)
            {
                if (throughCue)
                    cue.Update(Dt, at, basis, Altitude, Agl, aspect);
                else
                    puffers[0].Emit(at, basis, Dt);
            }
            puffers[0]._Process(0f);
            var origin = at + (basis * state.AtNodeOffset);
            var offsets = new List<Vector3>();
            foreach (var p in gpu.LastFrame)
                offsets.Add(p.Position - origin);
            return offsets;
        }
        finally
        {
            foreach (var p in puffers)
                p.Free();
            Rng.Reset(master, pinned);
        }
    }

    // C1 and C4 share cue geometry and timing but retain their chapter-authored alpha
    // variants instead of collapsing onto one global tune.
    internal static void SpeedCueChapterVariants(TestContext ctx)
    {
        var expected = new[]
        {
            (Chapter: "C1", PeakAlpha: new[] { 0.4f, 0.5f, 0.5f }),
            (Chapter: "C4", PeakAlpha: new[] { 0.6f, 0.7f, 0.7f }),
        };
        foreach (var chapter in expected)
        {
            string path = SessionPaths.ChapterZrdr(ctx.DataRoot, chapter.Chapter);
            for (int i = 0; i < 3; i++)
            {
                var state = PufferState.Load(path, "speed_cue.json", $"cuepuffer{i + 1}");
                ctx.Check(state != null, $"{chapter.Chapter} defines cuepuffer{i + 1}");
                if (state == null)
                    continue;
                ctx.Check(state.Colors.Count == 3,
                    $"{chapter.Chapter} cuepuffer{i + 1} keeps its three-point colour ramp");
                if (state.Colors.Count == 3)
                    ctx.Check(Mathf.Abs(chapter.PeakAlpha[i] - state.Colors[1].Color.A) < 0.001f,
                        $"{chapter.Chapter} cuepuffer{i + 1} keeps its authored peak alpha");
            }
        }
    }

    // Burst: the pool is sized from the calling animation's stop time, the first batch is
    // spawned at t = 0 rather than one interval in, the flipbook walks its whole sequence, and the
    // emitter puts itself away once the last particle dies.
    internal static void PufferBurstMode(TestContext ctx, PufferState state)
    {
        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu, activeDuration: 0.3f);
        ctx.Host.AddChild(puffer);
        try
        {
            // 0.3 s of emission at one batch per 0.2 s = batches at t=0 and t=0.2, so 2 × NUMBER.
            ctx.Same(36, gpu.Capacity, $"burst pool = NUMBER × the batches 0.3 s of emission fits");
            ctx.Check(gpu.CullMargin >= 4f, $"burst emitter pads its cull margin margin={gpu.CullMargin}");

            puffer.Burst(new Vector3(0f, 500f, 0f));
            puffer._Process(1f / 60f);
            ctx.Same(18, gpu.Shown, $"burst spawns its first batch at t=0, not one interval in");
            ctx.Check(gpu.LastFrame.Count > 0 && gpu.LastFrame.All(p => p.Alpha < 1f),
                $"a ramp-less state fades in on the life envelope rather than drawing at full alpha");

            for (int i = 0; i < 6; i++)   // 0.3 s: comfortably past the second batch at 0.2 s
                puffer._Process(0.05f);
            ctx.Same(36, gpu.Shown, $"burst's second batch lands one TIME_INTERVAL in");

            for (int i = 0; i < 30; i++)  // 1.5 s: past the last batch's 1 s lifetime
                puffer._Process(0.05f);
            ctx.Same(0, gpu.Shown, $"burst ends when its last particle dies");
            ctx.Check(!puffer.Visible, $"a finished burst hides itself");
            ctx.Same(5, (long)gpu.MaxFrame, $"the flipbook reaches its last column (TEXTURE_SEQUENCE keys are life fractions)");
            ctx.Check(gpu.MaxIndex < gpu.Capacity, $"burst never writes past its pool max={gpu.MaxIndex} pool={gpu.Capacity}");
        }
        finally
        {
            puffer.Free();
        }
    }

    // A TIME_INTERVAL state through Emit: the authored state picks the sustain path, the pool is
    // sized to the steady-state population, emission starts on the first frame, a long frame's older
    // catch-up batches are born already dead while its youngest are born alive, the hitch drains its
    // own accumulator, and Stop ends emission without cutting the live particles short.
    // See docs/org/puffer.md.
    internal static void PufferSustainMode(TestContext ctx, PufferState state)
    {
        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu, sustained: true);
        ctx.Host.AddChild(puffer);
        try
        {
            // ceil(NUMBER × LIFETIME_max / TIME_INTERVAL) + NUMBER = ceil(18 × 1 / 0.2) + 18.
            ctx.Same(108, gpu.Capacity, $"sustain pool = the steady-state population, not a burst count");

            var origin = new Vector3(0f, 800f, 0f);
            puffer.Emit(origin, Basis.Identity, 1f / 60f);
            puffer._Process(1f / 60f);
            ctx.Same(18, gpu.Shown, $"a time state through Emit sustains, on its very first frame");
            ctx.Check(gpu.LastFrame.All(p => p.Position.DistanceTo(origin) < 3f),
                $"sustained particles spawn at the world point they are driven at, not at the node");

            // A 5 s hitch: the uncapped batch loop asks for all 25 batches at once, each carrying the
            // engine's sub-frame start-age offset, so the early ones are past LIFETIME_RANGE and the
            // born-dead skip drops them. Read before _Process, so this is the spawn path, not the reaper.
            puffer.Emit(origin, Basis.Identity, 5f);
            int afterHitch = puffer.LiveCount;
            ctx.Check(afterHitch <= gpu.Capacity,
                $"a 25-batch hitch cannot overrun the pool live={afterHitch} pool={gpu.Capacity}");
            ctx.Check(afterHitch > 18,
                $"its youngest batches ARE born alive, the hitch is not silently dropped whole live={afterHitch}");
            // ⚠ Do not assert how many of the spawns the born-dead skip discarded. The pool is sized to one
            // lifetime and the survivors are one lifetime's worth, so the pool clamp would answer the check
            // instead of the skip; the drain assertion below is where the skip's arithmetic is readable.
            puffer._Process(1f / 60f);

            // The hitch drained its own accumulator, so the next ordinary frame carries a sixth of an interval
            // and emits nothing. A per-frame batch cap would instead burst 8 more batches, which the engine
            // never produces. Able to fail: with a cap in place this reads a full pool instead of no change.
            int beforeNext = puffer.LiveCount;
            puffer.Emit(origin, Basis.Identity, 1f / 60f);
            ctx.Same(beforeNext, puffer.LiveCount,
                $"the hitch drained its accumulator; the next frame emits no catch-up burst live={puffer.LiveCount}");
            puffer._Process(1f / 60f);
            ctx.Check(gpu.MaxIndex < gpu.Capacity,
                $"the sustain path never writes past its pool max={gpu.MaxIndex} pool={gpu.Capacity}");

            puffer.Stop();
            puffer._Process(0.05f);
            ctx.Check(gpu.Shown > 0, $"Stop ends emission without clearing the live particles");
            for (int i = 0; i < 30; i++)  // 1.5 s, past LIFETIME_RANGE's 1 s
                puffer._Process(0.05f);
            ctx.Same(0, gpu.Shown, $"the live particles finish their own lifetimes and the emitter goes quiet");
        }
        finally
        {
            puffer.Free();
        }
    }

    // A fast-moving TIME_INTERVAL emitter spreads a frame's batches along its motion since the
    // previous call instead of stacking them on today's pose, and the first frame after a
    // Stop()/restart re-homes rather than interpolating from the stale pre-stop pose (the
    // rocket-explosion ghost-trail rule). See docs/org/puffer.md. A synthetic zero-velocity,
    // zero-deviation, zero-COLORS state removes every other source of scatter, and LIFETIME_RANGE is
    // pinned to 10 s so the batches clear the born-dead skip and stay in the fade envelope's ramp.
    internal static void PufferSustainSubFrameEmission(TestContext ctx)
    {
        var state = new PufferState
        {
            Name = "test_subframe_sustain",
            Number = 1,
            TimeInterval = 0.1f,
            SizeMin = 1f,
            SizeMax = 1f,
            LifetimeMin = 10f,
            LifetimeMax = 10f,
        };

        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu, sustained: true);
        ctx.Host.AddChild(puffer);
        try
        {
            var a = new Vector3(0f, 1200f, 0f);
            puffer.Emit(a, Basis.Identity, 0f); // homes at A, carry = TimeInterval exactly ⇒ 0 leftover
            puffer._Process(0f);
            ctx.Same(1, gpu.Shown, $"the homing frame sputters its own single batch at A");

            var b = a + new Vector3(100f, 0f, 0f); // 100 m in one (hitched) frame
            puffer.Emit(b, Basis.Identity, 0.8f);  // accumulator = 0 + 0.8 = 8 × interval exactly
            puffer._Process(0f);
            ctx.Same(9, gpu.Shown, $"8 more batches join the homing one shown={gpu.Shown}");

            var moved = gpu.LastFrame.Where(p => p.Position.X > 1f).OrderBy(p => p.Position.X).ToList();
            ctx.Same(8, moved.Count, $"the jump's own 8 batches, excluding the homing puff at A");

            bool evenlySpaced = true, endsAtB = false, noneAtOrigin = true;
            for (int k = 0; k < moved.Count; k++)
            {
                float expectedX = 12.5f * (k + 1);
                if (!Mathf.IsEqualApprox(moved[k].Position.X, expectedX, 0.05f))
                    evenlySpaced = false;
                // ageOffset_k = (1 - (k+1)/8) * dt with the raw dt = 0.8 s, i.e. 0.7, 0.6, … 0.0;
                // alpha = ageOffset / (10 * FadeIn=0.12).
                float expectedAlpha = (0.8f - 0.1f * (k + 1)) / 1.2f;
                if (!Mathf.IsEqualApprox(moved[k].Alpha, expectedAlpha, 0.001f))
                    ctx.Check(false,
                        $"batch {k} alpha reads its age offset directly: want {expectedAlpha:F4} got {moved[k].Alpha:F4}");
            }
            endsAtB = Mathf.IsEqualApprox(moved[^1].Position.X, 100f, 0.05f);
            noneAtOrigin = moved.All(p => p.Position.X > 5f);
            ctx.Check(evenlySpaced,
                $"eight spawn positions evenly spaced 12.5 m apart along the segment, not stacked at one point");
            ctx.Check(endsAtB, $"the last (frac=1) batch sits exactly at the current pose");
            ctx.Check(noneAtOrigin, $"none of the eight sit back at the segment's start");

            // Restart case: Stop() then Emit() at a far new site, whose first frame's puffs must appear only
            // there. ⚠ Give the restart frame a multi-batch accumulator; at exactly one batch frac is 1
            // regardless of prevOrigin and the check passes with the re-home missing.
            puffer.Stop();
            var c = b + new Vector3(1000f, 0f, 0f);
            puffer.Emit(c, Basis.Identity, 0.8f);
            puffer._Process(0f);
            int strays = gpu.LastFrame.Count(p => p.Position.X > b.X + 10f && p.Position.X < c.X - 10f);
            ctx.Same(0, strays,
                $"a restart re-homes at the new site, no puffs interpolated across the 1000 m jump");
            ctx.Check(gpu.LastFrame.Any(p => p.Position.DistanceTo(c) < 3f),
                $"the restart's first batch lands at the new site");
        }
        finally
        {
            puffer.Free();
        }
    }

    // A DISTANCE_INTERVAL state through `Emit`: the first call homes the trail and
    // time-sputters one batch (the still-host rule, on the homing frame no motion has elapsed
    // yet), a moving host emits one puff per interval of actual motion with the remainder carried
    // across frames instead of rounded away.
    internal static void PufferTrailMode(TestContext ctx, PufferState state)
    {
        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu);
        ctx.Host.AddChild(puffer);
        try
        {
            ctx.Same(640, gpu.Capacity, $"trail pool is the live-particle cap, not a burst count");

            puffer.Emit(new Vector3(0f, 600f, 0f), Basis.Identity, 1f / 60f);
            puffer._Process(1f / 60f);
            ctx.Same(1, gpu.Shown, $"the first call homes the trail and time-sputters one still-host batch");

            // 10 m at one puff per 2 m = 5 (+ the homing sputter), then 3 m more = 1 puff with
            // 1 m carried, not 2 rounded up.
            puffer.Emit(new Vector3(10f, 600f, 0f), Basis.Identity, 1f / 60f);
            puffer._Process(1f / 60f);
            ctx.Same(6, gpu.Shown, $"a moving host emits one puff per DISTANCE_INTERVAL of motion");
            ctx.Check(gpu.LastFrame.All(p => Mathf.IsEqualApprox(p.Alpha, 1f)),
                $"a COLORS ramp owns the fade, so the life envelope stays out of it");

            puffer.Emit(new Vector3(13f, 600f, 0f), Basis.Identity, 1f / 60f);
            puffer._Process(1f / 60f);
            ctx.Same(7, gpu.Shown, $"a partial interval carries into the next frame instead of rounding");
            ctx.Check(gpu.MaxIndex < gpu.Capacity, $"trail never writes past its pool max={gpu.MaxIndex} pool={gpu.Capacity}");
        }
        finally
        {
            puffer.Free();
        }
    }

    // A distance state whose host stands still keeps the time cadence (the damaged
    // building's sputter): the authored interval can never elapse, so `Emit` with no burn
    // rate falls back to one batch per synthetic 0.1 s TIME_INTERVAL, at the held point.
    internal static void PufferStillSputter(TestContext ctx, PufferState state)
    {
        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu);
        ctx.Host.AddChild(puffer);
        try
        {
            var p0 = new Vector3(0f, 900f, 0f);
            const float dt = 1f / 60f;
            for (int i = 0; i < 60; i++)   // 1 s held still, no particle dies (LIFETIME ≥ 1.5 s)
            {
                puffer.Emit(p0, Basis.Identity, dt);
                puffer._Process(dt);
            }
            ctx.Check(gpu.Shown >= 10 && gpu.Shown <= 12,
                $"a still host sputters on the 0.1 s time cadence, ~11 batches in 1 s shown={gpu.Shown}");
            ctx.Check(gpu.LastFrame.All(p => p.Position.DistanceTo(p0) < 3f),
                $"the sputter stays at the held point");
        }
        finally
        {
            puffer.Free();
        }
    }

    // A state that authors no interval at all, the compiled shape carries a zero the engine's own
    // setter refuses, emits at the constructor's 1 s. Reading that zero as a cadence instead puts
    // the emitter on its 1 ms emission floor, which fills the particle pool and holds it full; the
    // install's five such events are texture-less stubs that build nothing, so this suite is the
    // only place it is visible. Count over time, never one frame (verification.md SHOT-19).
    internal static void PufferUnauthoredCadence(TestContext ctx)
    {
        var state = PufferState.FromAnimEvent(new AnimData(new Dictionary<string, object?>
        {
            ["name"] = "truck1dust_puffer",
            ["lifetime_range"] = new Dictionary<string, object?> { ["min"] = 4f, ["max"] = 4f },
            ["interval_garbage"] = new Dictionary<string, object?>
            {
                ["interval_type"] = "Time",
                ["interval_value"] = 0f,
                ["has_interval_value"] = false,
                ["has_interval_type"] = false,
            },
        }));
        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu);
        ctx.Host.AddChild(puffer);
        try
        {
            var p0 = new Vector3(0f, 500f, 0f);
            const float dt = 1f / 60f;
            var series = new List<int>();
            for (int second = 0; second < 3; second++)
            {
                for (int i = 0; i < 60; i++)
                {
                    puffer.Emit(p0, Basis.Identity, dt);
                    puffer._Process(dt);
                }
                series.Add(gpu.Shown);
            }
            ctx.Note($"unauthored TIME_INTERVAL, live sprites after 1/2/3 s: {string.Join("/", series)}");
            ctx.Check(series[0] == 1 && series[1] == 2 && series[2] == 3,
                $"an unauthored state emits once a second, not on the emission floor, {string.Join("/", series)}");
        }
        finally
        {
            puffer.Free();
        }
    }

    // What it costs the frame to own an emitter that is doing nothing. Godot dispatches _Process to
    // every processing node, and a mission pre-warms thousands of emitters, so the claim is that a
    // dormant one does not ask for the callback at all, through each entry path and each end.
    [Suite("puffer-idle-process-gate",
        "a dormant emitter is off Godot's frame-callback list and each entry path puts it back, "
        + "which is what keeps a mission's pre-warmed field from costing the frame it is idle in")]
    internal static void PufferIdleProcessGate(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr effect readers");
        var burstState = PufferState.Load(ctx.ZrdrPath, "flame_ball.json", "fierypuffer");
        var trailState = PufferState.Load(ctx.ZrdrPath, "pufftrails.json", "smokepuffer");
        ctx.Check(burstState != null, $"flame_ball.json defines fierypuffer");
        ctx.Check(trailState != null, $"pufftrails.json defines smokepuffer");
        if (burstState == null || trailState == null)
        {
            return;
        }

        // Detached for the duration, for the same reason PufferModes does it: the harness clock is
        // a FixedStep one nothing steps, so an installed clock would freeze every emitter tick.
        var clock = GameClock.Current;
        GameClock.Current = null;
        try
        {
            PufferIdleFieldCost(ctx, burstState);
            PufferProcessGateBurst(ctx, burstState);
            PufferProcessGateTrail(ctx, trailState);
        }
        finally
        {
            GameClock.Current = clock;
        }
    }

    // The population claim, at a scale a mission reaches: a pre-warmed field asks Godot for nothing
    // until something starts one of them, and then for exactly that one.
    internal static void PufferIdleFieldCost(TestContext ctx, PufferState state)
    {
        const int population = 64;
        var field = new Puffer[population];
        for (int i = 0; i < population; i++)
        {
            field[i] = Puffer.CreateWith(state, new RecordingEmitterRenderer(), activeDuration: 0.3f);
            ctx.Host.AddChild(field[i]);
        }

        try
        {
            ctx.Check(field.All(p => p.IsInsideTree()),
                $"the built field is in the tree, so its processing flag is the one Godot dispatches on");
            ctx.Same(0, field.Count(p => p.IsProcessing()),
                $"no unstarted emitter of a {population}-strong field asks for a frame callback");
            field[0].Burst(new Vector3(0f, 500f, 0f));
            ctx.Same(1, field.Count(p => p.IsProcessing()),
                $"starting one of the field puts exactly that one on the frame path");
        }
        finally
        {
            foreach (var p in field)
            {
                p.Free();
            }
        }
    }

    // Burst: off when built, on for the run, off again when the last particle dies.
    internal static void PufferProcessGateBurst(TestContext ctx, PufferState state)
    {
        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu, activeDuration: 0.3f);
        ctx.Host.AddChild(puffer);
        try
        {
            ctx.Check(!puffer.IsProcessing(), $"a built, unstarted burst emitter is off the frame path");
            puffer.Burst(new Vector3(0f, 500f, 0f));
            ctx.Check(puffer.IsProcessing(), $"Burst puts the emitter back on the frame path");
            for (int i = 0; i < 40; i++)   // 2 s: past the last batch's 1 s lifetime
            {
                puffer._Process(0.05f);
            }

            ctx.Same(0, gpu.Shown, $"the burst has ended (its last particle died)");
            ctx.Check(!puffer.IsProcessing(), $"a finished burst takes itself off the frame path");
        }
        finally
        {
            puffer.Free();
        }
    }

    // The continuous paths: Emit turns a DISTANCE_INTERVAL emitter on, and Clear takes it off.
    internal static void PufferProcessGateTrail(TestContext ctx, PufferState state)
    {
        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu);
        ctx.Host.AddChild(puffer);
        try
        {
            ctx.Check(!puffer.IsProcessing(), $"a built, unstarted trail emitter is off the frame path");
            puffer.Emit(new Vector3(0f, 500f, 0f), Basis.Identity, 1f / 60f);
            ctx.Check(puffer.IsProcessing(), $"Emit puts the trail emitter on the frame path");
            puffer.Clear();
            ctx.Check(!puffer.IsProcessing(), $"Clear takes it off again");
        }
        finally
        {
            puffer.Free();
        }
    }

    // A host that CANNOT move (the damage lab's parked plane) declares a burn rate:
    // `Emit` with `staticBurnMps` spends virtual metres at the held point, the
    // authored per-metre density, not the time cadence, through the same carry as the moving
    // trail.
    internal static void PufferStaticBurn(TestContext ctx, PufferState state)
    {
        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu);
        ctx.Host.AddChild(puffer);
        try
        {
            var p0 = new Vector3(0f, 1000f, 0f);
            // 15 m/s × 0.2 s = 3 m/call over DISTANCE_INTERVAL 2: puff counts 1,2,1,2 as the
            // carry wraps, 6 total for 12 virtual metres. The time cadence over the same 0.8 s
            // would be 9 batches, so the count also proves which fallback ran.
            for (int i = 0; i < 4; i++)
            {
                puffer.Emit(p0, Basis.Identity, 0.2f, staticBurnMps: 15f);
                puffer._Process(0.2f);
            }
            ctx.Same(6, gpu.Shown, $"a declared burn rate spends virtual metres, not the time cadence");
            ctx.Check(gpu.LastFrame.All(p => p.Position.DistanceTo(p0) < 6f),
                $"the burn stays at the held point");
        }
        finally
        {
            puffer.Free();
        }
    }

    // The pause + far revive, through `Stop` itself: a pooled effect-template slot
    // is teleported to each new call site, so a distance-state emitter stopped at one blast and
    // revived at the next must re-home there, a kept trail origin draws a puff line across the
    // whole jump (the rocket-explosion ghost trails). `Stop` ends trail AND sustain
    // unconditionally; the revive's first call sputters fresh at the new site.
    internal static void PufferStopRevive(TestContext ctx, PufferState state)
    {
        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu, sustained: true);
        ctx.Host.AddChild(puffer);
        try
        {
            const float dt = 1f / 60f;
            var a = new Vector3(0f, 700f, 0f);
            puffer.Emit(a, Basis.Identity, dt);                              // homes at A
            puffer.Emit(a + new Vector3(10f, 0f, 0f), Basis.Identity, dt);   // trails 10 m
            puffer._Process(dt);
            ctx.Check(gpu.Shown > 0, $"the emitter trailed at the first site shown={gpu.Shown}");

            puffer.Stop();
            puffer.Stop();   // idempotent, a second stop is a no-op, not an error
            var b = a + new Vector3(1000f, 0f, 0f);
            puffer.Emit(b, Basis.Identity, dt);
            puffer._Process(dt);
            int strays = gpu.LastFrame.Count(p => p.Position.X > 20f && p.Position.X < 980f);
            ctx.Same(0, strays,
                $"a stopped trail revived at a far site re-homes there, no puff line across the jump");
            ctx.Check(gpu.LastFrame.Any(p => p.Position.DistanceTo(b) < 3f),
                $"the revive restarted emission fresh at the new site");
        }
        finally
        {
            puffer.Free();
        }
    }

    // The original's teleport guard on the DISTANCE path: the frame's motion length joins the emission
    // accumulator only if len < 200, so an emitter carried across the world lays no puff line along
    // the jump. PufferStopRevive covers the jump that goes through Stop and re-homes; this is the one
    // that does not. Four steps, each failing differently: a 199 m move pins the boundary from below,
    // the 500 m jump emits nothing, the guard counter reads 1, and a 20 m move afterwards proves the
    // carried remainder survived the guard rather than being reset with it.
    internal static void PufferTeleportGuard(TestContext ctx, PufferState state)
    {
        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu);
        ctx.Host.AddChild(puffer);
        try
        {
            const float dt = 1f / 60f;
            const float interval = 2f;   // smokepuffer's authored DISTANCE_INTERVAL, asserted above
            var a = new Vector3(0f, 1400f, 0f);
            puffer.Emit(a, Basis.Identity, dt);          // homes, + one still-host sputter batch
            puffer._Process(dt);
            ctx.Same(1, gpu.Shown, $"the homing frame sputters its own batch shown={gpu.Shown}");

            // 199 m: one metre under the guard, and the largest move the engine still accumulates.
            var b = a + new Vector3(199f, 0f, 0f);
            puffer.Emit(b, Basis.Identity, dt);
            puffer._Process(dt);
            ctx.Same(1 + (int)(199f / interval), gpu.Shown,
                $"a 199 m move is under the 200 m guard and emits its full 99 puffs shown={gpu.Shown}");
            ctx.Same(0, puffer.TeleportGuardCount, $"and does not trip the guard");

            // The teleport: 500 m in one frame, with no Stop in between, the case Stop's re-home
            // rule cannot reach.
            int before = gpu.Shown;
            var c = b + new Vector3(500f, 0f, 0f);
            puffer.Emit(c, Basis.Identity, dt);
            puffer._Process(dt);
            ctx.Same(before, gpu.Shown, $"a 500 m frame emits nothing at all shown={gpu.Shown}");
            ctx.Same(1, puffer.TeleportGuardCount,
                $"the guard tripped once, which is also the proof its log line fired");
            ctx.Same(0, gpu.LastFrame.Count(p => p.Position.X > b.X + 10f && p.Position.X < c.X - 10f),
                $"no puff was laid anywhere along the 500 m jump");

            // 20 m from the new pose: 1 m of carry survived the guard, so 21 m buys 10 puffs.
            puffer.Emit(c + new Vector3(20f, 0f, 0f), Basis.Identity, dt);
            puffer._Process(dt);
            ctx.Same(before + 10, gpu.Shown,
                $"the emitter resumes at the new site with its carry intact shown={gpu.Shown}");
        }
        finally
        {
            puffer.Free();
        }
    }

    // START_AGE_RANGE, on a synthetic state authoring a negative minimum like fire_at_zepskin3's
    // (-1.0, 0.1), the census's only negative case. Checks what the integrator settles: every particle
    // is drawn, a negative-age particle pins to stop 0 of the colour and growth ramps rather than
    // being skipped or extrapolated, and it outlives its authored LIFETIME_RANGE by up to StartAgeMin.
    internal static void PufferStartAgeBehavior(TestContext ctx)
    {
        var state = new PufferState
        {
            Name = "test_start_age",
            Number = 40,
            TimeInterval = 1000f, // never re-fires on its own, one Burst, one batch
            SizeMin = 2f,
            SizeMax = 2f,
            GrowthFactor = 5f, // an unclamped negative t would shrink (or negate) size below BaseSize
            LifetimeMin = 1f,
            LifetimeMax = 1f,
            StartAgeMin = -1f,
            StartAgeMax = 0.1f,
            Colors = new[] { (0f, Colors.Red), (0.5f, Colors.Green) },
        };
        ctx.Check(state.HasStartAgeRange, $"the synthetic state authors START_AGE_RANGE");

        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu, activeDuration: 0.01f);
        ctx.Host.AddChild(puffer);
        try
        {
            puffer.Burst(new Vector3(0f, 1100f, 0f));
            puffer._Process(1f / 60f);

            ctx.Same(40, gpu.Shown,
                $"every particle draws, this state's start age (max 0.1) never reaches its lifetime (1), so the born-dead skip stays silent shown={gpu.Shown}");

            int redCount = gpu.LastFrame.Count(p => p.Color == Colors.Red);
            ctx.Check(redCount > gpu.LastFrame.Count / 2,
                $"most particles (born with age <= 0) show the colour ramp's stop 0, not skipped or interpolated past it red={redCount}/{gpu.LastFrame.Count}");

            var bySize = gpu.LastFrame.GroupBy(p => p.Size).OrderByDescending(g => g.Count()).First();
            ctx.Check(bySize.Count() > gpu.LastFrame.Count / 2,
                $"most particles collapse onto one identical size, the growth ramp clamps age<=0 to stop 0 rather than each drawing its own extrapolated value count={bySize.Count()}/{gpu.LastFrame.Count}");
            float commonSize = bySize.Key;
            ctx.Check(gpu.LastFrame.All(p => p.Size >= commonSize - 1e-3f),
                $"no particle sits below the clamped stop-0 size, an unclamped negative age would shrink (or negate) it");

            // Past the unmodified 1 s LIFETIME_RANGE, particles born with a negative start age
            // must still be alive: they need up to Life - StartAgeMin = 2 s of Age to reap.
            for (int i = 0; i < 63; i++) // 1.05 s
                puffer._Process(1f / 60f);
            // ~85% of the authored range is expected to still be alive here (age0 in [-1,0.1)
            // uniformly, elapsed ~1.07 s ⇒ P(dead) ≈ 0.15); a generous >=20/40 margin below that.
            ctx.Check(gpu.Shown >= 20,
                $"negative-age particles outlive their authored LIFETIME_RANGE by |age0| instead of reaping on the old schedule shown={gpu.Shown}");

            for (int i = 0; i < 60; i++) // + 1 s more = past every particle's 2 s worst case
                puffer._Process(1f / 60f);
            ctx.Same(0, gpu.Shown, $"every particle eventually reaps once its real (shifted) age reaches its lifetime");
        }
        finally
        {
            puffer.Free();
        }
    }

    // The fire_n_smoke column, measured: the AUTHORED column with no engine-side rise or lifetime
    // multiplier on top, so a change that shortens the fire has to argue with a number. Its state
    // comes from the chapter's compiled program, which the reader would call a stub. It is run in
    // still air and in C1 IA1's authored upward wind, which friction damps toward. docs/org/puffer.md.
    // ⚠ Assert ratios and bands, never a pinned decimal; a height is one seed's extreme and moves
    // about 1.5 m with suite order, so the exact figures belong in ctx.Note.
    [Suite("puffer-fire-column",
        "the 30 s fire's authored column height, still air and in C1's own upward wind, the readout that retired the invented fire scales (D10)")]
    internal static void PufferFireColumn(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var defs = world.Session.Program.Subset(new[] { "large_30sec_fire" }).ByAnimName("large_30sec_fire");
            ctx.Check(defs.Count > 0, $"chapter program has large_30sec_fire defs={defs.Count}");
            if (defs.Count == 0)
                return;

            AnimData? payload = null;
            foreach (var seq in defs[0].Sequences)
            {
                foreach (var ev in seq.Events)
                {
                    if (payload == null && ev.Kind == "PufferState" && (ev.Data.Num("active_state") ?? 0f) > 0f)
                        payload = ev.Data;
                }
            }
            ctx.Check(payload != null, $"large_30sec_fire carries an ACTIVE_STATE 1 PufferState event");
            if (payload == null)
                return;

            var fire = PufferState.FromAnimEvent(payload);

            // The compiled numbers this measurement rests on, asserted rather than assumed: a data
            // change must fail here naming itself, not silently re-baseline the heights below.
            ctx.Check(fire.Name == "fire_n_smoke", $"the 30 s fire's emitter name={fire.Name}");
            ctx.Same(1, fire.Number, $"large_30sec_fire authors no NUMBER, one puff per interval (BL-218)");
            ctx.Check(Mathf.IsEqualApprox(0.1f, fire.TimeInterval), $"TIME_INTERVAL 0.1 s");
            ctx.Check(Mathf.IsEqualApprox(5f, fire.LocalVelocity.Y) && Mathf.IsEqualApprox(4f, fire.WorldVelocity.Y),
                $"the rise is LOCAL_VELOCITY 5 + WORLD_VELOCITY 4 m/s");
            ctx.Check(Mathf.IsEqualApprox(-1f, fire.WorldAcceleration.Y), $"WORLD_ACCELERATION −1 m/s²");
            ctx.Check(Mathf.IsEqualApprox(0.6f, fire.Friction), $"FRICTION 0.6");
            ctx.Check(Mathf.IsEqualApprox(1f, fire.SizeMin) && Mathf.IsEqualApprox(3.5f, fire.SizeMax),
                $"SIZE_RANGE 1–3.5 m (a RADIUS, A1)");
            ctx.Check(Mathf.IsEqualApprox(3.5f, fire.LifetimeMin) && Mathf.IsEqualApprox(5.5f, fire.LifetimeMax),
                $"LIFETIME_RANGE 3.5–5.5 s");
            ctx.Check(Mathf.IsEqualApprox(2.5f, fire.GrowthFactor), $"GROWTH_FACTOR 2.5");

            var clock = GameClock.Current;
            GameClock.Current = null;
            try
            {
                // C1 IA1's authored weather, static part held: extracted/C1/IA1/zrdr/weather.zrd.json
                // WIND STATIC_VELOCITY [0, 2, 0].
                var breeze = new EffectAmbience();
                breeze.SetWind(new Vector3(0f, 2f, 0f));

                var still = MeasureFireColumn(ctx, fire, EffectAmbience.Still);
                var wind = MeasureFireColumn(ctx, fire, breeze);

                ctx.Note($"large_30sec_fire column, 30 s at 1/60, still host, heights above the emitter:");
                ctx.Note($"  still air: centre apex {still.Centre:0.0} m, drawn top {still.Top:0.0} m (unscaled sprite: {still.PreA1Top:0.0} m), peak live {still.Live}, largest sprite {still.Sprite:0.0} m");
                ctx.Note($"  C1 IA1 wind (0,2,0): centre apex {wind.Centre:0.0} m, drawn top {wind.Top:0.0} m, peak live {wind.Live}");

                // What the authored column has to keep doing, as assertions rather than prose:
                // (1) the rise itself, LOCAL+WORLD velocity against FRICTION and the −1 accel;
                ctx.Check(still.Centre > 8f && still.Centre < 22f,
                    $"the authored rise integrates to a high-teens column centre={still.Centre:0.0} m");
                // (2) the sprite's own half-extent is a real share of the plume, the SIZE_RANGE
                //     radius showing up in the picture rather than only in a constant;
                ctx.Check(still.Top > still.Centre * 1.3f,
                    $"the drawn top stands well above the centre apex, A1's sprite is a real share of the plume top={still.Top:0.0} centre={still.Centre:0.0}");
                // (3) the wind coupling lifts this puffer. C1 IA1's authored wind blows straight
                //     UP, and with no engine-side fire tune this is the whole reason the authored
                //     column reaches: decouple the wind and the plume loses ~6 m of height.
                ctx.Check(wind.Centre > still.Centre + 3f,
                    $"C1's upward wind lifts the authored column wind={wind.Centre:0.0} still={still.Centre:0.0}");
                // (4) the height the controls verdict settled on: a plume ~2x this band was judged
                //     twice too tall, so re-introducing any rise/lifetime multiplier breaks it
                //     immediately.
                ctx.Check(wind.Top > 24f && wind.Top < 42f,
                    $"the wind-carried plume tops out in the judged band top={wind.Top:0.0} m");
            }
            finally
            {
                GameClock.Current = clock;
            }
        });
    }

    // Drives one sustained emitter for 30 s of held-still emission and reports the
    // column it built: the highest particle CENTRE, the highest drawn sprite TOP (centre plus the
    // quad's half-side, the renderer scales a 1×1 quad by `Size`), the peak live population
    // and the largest sprite ever drawn. Heights are relative to the emitter's own origin.
    internal static (float Centre, float Top, float PreA1Top, int Live, float Sprite) MeasureFireColumn(
        TestContext ctx, PufferState state, EffectAmbience ambience)
    {
        var gpu = new RecordingEmitterRenderer();
        // Every switch off: this puffer authors NEAR_FADE (70, 20) and the suite's camera sits at
        // the origin, so the authored near cull would discard the whole column before it is read.
        var puffer = Puffer.CreateWith(state, gpu, sustained: true, ambience: ambience,
            fade: new PufferFadeSwitches(DistanceFade: false, FarCull: false, NearCull: false));
        ctx.Host.AddChild(puffer);
        try
        {
            var origin = Vector3.Zero;
            const float dt = 1f / 60f;
            float centre = 0f, top = 0f, preA1 = 0f, sprite = 0f;
            int live = 0;
            for (int i = 0; i < 1800; i++)   // the authored 30 s of ACTIVE_STATE 1
            {
                puffer.Emit(origin, Basis.Identity, dt);
                puffer._Process(dt);
                foreach (var p in gpu.LastFrame)
                {
                    float y = p.Position.Y - origin.Y;
                    if (y > centre)
                        centre = y;
                    if (y + p.Size * 0.5f > top)
                        top = y + p.Size * 0.5f;
                    // The same run read at the half-size sprite convention: Size is exactly linear in
                    // SizeScaleDefault and the RNG stream does not depend on it, so quartering the side here is that
                    // convention's drawn top exactly rather than an estimate.
                    if (y + p.Size * 0.25f > preA1)
                        preA1 = y + p.Size * 0.25f;
                    if (p.Size > sprite)
                        sprite = p.Size;
                }
                if (puffer.LiveCount > live)
                    live = puffer.LiveCount;
            }
            ctx.Check(live > 0, $"{state.Name}: the emitter actually ran live={live}");
            return (centre, top, preA1, live, sprite);
        }
        finally
        {
            puffer.Free();
        }
    }

    // The blend rule, over the chapter's own archive and a real emitter: a sprite adds exactly when
    // bit 2 of its texture's render-flags word is set. The reader half (which enum spelling carries
    // the bit) is a unit; what needs an engine is that the shipped textures still carry the values
    // the census read, and that a flipbook crossing the flag routes its particles per frame.
    [Suite("puffer-blend-flag",
        "a puffer's blend is its texture's own additive bit, read per frame, over the chapter's "
        + "shipped archive, and each blend's fog comes from the sky rather than from the material")]
    internal static void PufferBlendFlag(TestContext ctx)
    {
        string texturePath = SessionPaths.ChapterTextures(ctx.DataRoot, ctx.Chapter);
        ctx.RequireData(texturePath, $"{ctx.Chapter} texture archive");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr effect readers");
        using var textures = new TextureArchive(texturePath);

        // The flagged set is exactly the emissive elements, and the value is a bitfield: fire101
        // ships 7 (additive plus both stretch bits) and splashbase ships 3 (both stretch bits and no
        // additive), so a reader that tested the word for zero would call splashbase additive.
        ctx.Same(7, textures.RenderFlags("fire101"), $"fire101's render-flags word");
        ctx.Same(3, textures.RenderFlags("splashbase"), $"splashbase's render-flags word");
        ctx.Check(textures.IsAdditive("fire101") && textures.IsAdditive("fire112"),
            $"the fire flipbook carries the additive bit");
        ctx.Check(!textures.IsAdditive("splashbase"), $"splashbase stretches but does not add");
        foreach (var name in new[]
                 {
                     "fire_f01", "fire_f06", "smoke101", "smoke103", "thickblksmoke01",
                     "exp_yel01", "poleflare", "magnesiumtip", "watersquirt",
                 })
        {
            ctx.Check(!textures.IsAdditive(name), $"{name} is alpha-mixed, no puffer sprite carries the bit");
        }
        ctx.Check(!textures.IsAdditive("no_such_texture"),
            $"a name the archive cannot resolve alpha-mixes, which is the engine's own fallback");

        // A rocket trail is where the two readings disagree: ramp-less and ending on a bright
        // sprite, so a sprite-darkness verdict would add it while the texture flag mixes it.
        var trail = PufferState.Load(ctx.ZrdrPath, "missile_puffers.json", "trailpuffer");
        ctx.Check(trail != null, $"missile_puffers.json defines trailpuffer");
        if (trail != null)
        {
            ctx.Same(0, trail.Colors.Count, $"trailpuffer authors no COLORS ramp");
            foreach (var (_, frame) in trail.TextureSequence)
                ctx.Check(!textures.IsAdditive(frame), $"trailpuffer frame {frame} alpha-mixes");
        }

        PufferBlendRouting(ctx, textures);
        PufferFogFollowsSky(ctx);
    }

    [Suite("puffer-draw-order",
        "the frame's particles reach the renderer farthest-first against pane 0, so a young bright "
        + "sprite paints over an old dark one at the same site")]
    internal static void PufferDrawOrder(TestContext ctx)
    {
        // ⚠ Detach the harness clock for the whole suite. It is a FixedStep clock nothing steps, so
        // FrameDt is 0, no particle would age and every flipbook column would read 0.
        var clock = GameClock.Current;
        GameClock.Current = null;
        try
        {
            PufferDrawOrderRow(ctx);
        }
        finally
        {
            GameClock.Current = clock;
        }
    }

    // A flipbook whose column rises with age, so the written column says how old each particle is:
    // column 0 is the young bright frame, the last column the dying dark one.
    private static void PufferDrawOrderRow(TestContext ctx)
    {
        var state = new PufferState
        {
            Name = "order_puffer",
            Number = 1,
            TimeInterval = 1f / 60f,
            SizeMin = 1f,
            SizeMax = 1f,
            LifetimeMin = 1f,
            LifetimeMax = 1f,
            TextureSequence = new[] { (0f, "fire_f01"), (0.05f, "fire_f06") },
        };
        var camera = new Vector3(0f, 0f, 0f);
        var forward = Vector3.Forward;   // down −Z
        var amb = new EffectAmbience();
        amb.SetCamera(camera, forward);

        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu, sustained: true, ambience: amb);
        ctx.Host.AddChild(puffer);
        try
        {
            // Emitted AWAY from the camera, one particle a step, so the spawn order is
            // near-to-far and the decode's order is its exact reverse. Sorting the wrong way, or
            // not at all, therefore fails rather than passing on a coincidence.
            const float step = 1f / 60f;
            for (int i = 1; i <= 6; i++)
            {
                puffer.Emit(new Vector3(0f, 0f, -50f * i), Basis.Identity, step);
                puffer._Process(step);
            }

            var written = gpu.LastFrame;
            ctx.Check(written.Count >= 5,
                $"the run laid a row of particles down the view axis count={written.Count}");
            if (written.Count < 5)
                return;

            var depths = new List<float>(written.Count);
            foreach (var p in written)
                depths.Add(forward.Dot(p.Position - camera));
            bool descending = true;
            for (int i = 1; i < depths.Count; i++)
                if (depths[i] > depths[i - 1] + 1e-3f)
                    descending = false;
            ctx.Check(descending,
                $"every particle is written no nearer than the one before it, over {depths.Count} written");
            ctx.Check(depths[0] > depths[depths.Count - 1],
                $"the first written is the farthest ({depths[0]:0} m) and the last the nearest ({depths[depths.Count - 1]:0} m)");

            // Here the oldest puff is the NEAREST, so its dark dying column paints last and that is
            // the rule rather than the defect: depth decides, never age or spawn order.
            ctx.Check(written[0].Frame < written[written.Count - 1].Frame,
                $"with the old puff nearest, the young column ({written[0].Frame}) is written first and the dark one ({written[written.Count - 1].Frame}) last");
        }
        finally
        {
            puffer.Free();
        }

        PufferDrawOrderDarkBehind(ctx, state, camera, forward);
        PufferDrawOrderWithoutCamera(ctx, state);
        PufferDrawOrderSorting(ctx);
    }

    // The filed symptom's own geometry: an old dark puff sitting BEHIND a young bright one. The
    // emitter writes the old one first anyway, so this direction is a control on the sort's sign
    // rather than on its existence, and an ascending or reversed key breaks it.
    private static void PufferDrawOrderDarkBehind(TestContext ctx, PufferState state,
        Vector3 camera, Vector3 forward)
    {
        var amb = new EffectAmbience();
        amb.SetCamera(camera, forward);
        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu, sustained: true, ambience: amb);
        ctx.Host.AddChild(puffer);
        try
        {
            const float step = 1f / 60f;
            for (int i = 6; i >= 1; i--)
            {
                puffer.Emit(new Vector3(0f, 0f, -50f * i), Basis.Identity, step);
                puffer._Process(step);
            }

            var written = gpu.LastFrame;
            ctx.Check(written.Count >= 5, $"the reversed run drew its row too count={written.Count}");
            if (written.Count < 5)
                return;
            float first = forward.Dot(written[0].Position - camera);
            float last = forward.Dot(written[written.Count - 1].Position - camera);
            ctx.Check(first > last,
                $"the old dark puff behind is written first ({first:0} m) and the young bright one in front last ({last:0} m)");
            ctx.Check(written[0].Frame > written[written.Count - 1].Frame,
                $"so the dark column ({written[0].Frame}) no longer paints over the bright one ({written[written.Count - 1].Frame})");
        }
        finally
        {
            puffer.Free();
        }
    }

    // No published camera means no depth to sort on, which is the session's first frame and every
    // unwired lab. The order is then the emitter's own, unchanged, rather than an invented one.
    private static void PufferDrawOrderWithoutCamera(TestContext ctx, PufferState state)
    {
        var gpu = new RecordingEmitterRenderer();
        var puffer = Puffer.CreateWith(state, gpu, sustained: true);
        ctx.Host.AddChild(puffer);
        try
        {
            const float step = 1f / 60f;
            for (int i = 1; i <= 4; i++)
            {
                puffer.Emit(new Vector3(0f, 0f, -50f * i), Basis.Identity, step);
                puffer._Process(step);
            }

            var written = gpu.LastFrame;
            ctx.Check(written.Count >= 3, $"the unwired emitter still draws count={written.Count}");
            if (written.Count < 3)
                return;
            bool spawnOrder = true;
            for (int i = 1; i < written.Count; i++)
                if (written[i].Position.Z > written[i - 1].Position.Z)
                    spawnOrder = false;
            ctx.Check(spawnOrder,
                $"with no camera published the write order is the emitter's own spawn order, near to far");
        }
        finally
        {
            puffer.Free();
        }
    }

    // The between-emitter half is Godot's own transparent-object depth sort, and it only tracks
    // the particles if the instance sorts on its AABB centre: a trail or sustain emitter pins its
    // node at the world origin. Set explicitly, so a Godot default change trips this.
    private static void PufferDrawOrderSorting(TestContext ctx)
    {
        var image = Image.CreateEmpty(2, 1, false, Image.Format.Rgba8);
        image.Fill(Colors.White);
        var renderer = new MultiMeshEmitterRenderer(ImageTexture.CreateFromImage(image), 1,
            new[] { false }, softParticles: false);
        var owner = new Node3D();
        ctx.Host.AddChild(owner);
        try
        {
            renderer.Attach(owner, 4, cullMargin: 1f);
            int lists = 0;
            foreach (var child in owner.GetChildren())
            {
                if (child is MultiMeshInstance3D mmi)
                {
                    ctx.Check(mmi.SortingUseAabbCenter,
                        $"the particle list depth-sorts on its cloud's AABB centre, not on its node's origin");
                    lists++;
                }
            }
            ctx.Same(1, lists, $"the uniform-blend column set builds exactly one draw list");
        }
        finally
        {
            owner.QueueFree();
        }
    }

    // Blend is per FRAME, so a flipbook whose columns disagree draws two lists and each particle
    // goes to the one its current column names. No shipped puffer mixes flagged and unflagged
    // frames, so the split is built here out of two real textures that do disagree.
    private static void PufferBlendRouting(TestContext ctx, TextureArchive textures)
    {
        var atlas = textures.Find("fire101");
        ctx.Check(atlas != null, $"the archive decodes fire101");
        if (atlas == null)
            return;
        var renderer = new MultiMeshEmitterRenderer(atlas, 2, new[] { true, false }, softParticles: false);
        ctx.Check(renderer.Split, $"a column set spanning both blends reports as split");
        var owner = new Node3D();
        ctx.Host.AddChild(owner);
        try
        {
            renderer.Attach(owner, 8, cullMargin: 1f);
            for (int i = 0; i < 3; i++)
                renderer.Write(i, Vector3.Zero, 1f, 0f, 1f, Colors.White);
            for (int i = 3; i < 5; i++)
                renderer.Write(i, Vector3.Zero, 1f, 1f, 1f, Colors.White);
            renderer.Show(5);
            ctx.Same(2, renderer.DrawnCounts.Mixed, $"the unflagged column's particles draw mixed");
            ctx.Same(3, renderer.DrawnCounts.Additive, $"the flagged column's particles draw additive");

            // The counts are per frame, not cumulative: a following frame that draws nothing must
            // publish zero rather than leave the last frame's instances visible.
            renderer.Show(0);
            ctx.Same(0, renderer.DrawnCounts.Mixed + renderer.DrawnCounts.Additive,
                $"an empty frame publishes nothing in either list");
        }
        finally
        {
            owner.QueueFree();
        }
    }

    // CSVM draws a puff far past the distance the original culled it at, so a far one has to sink
    // into the same fog the hill behind it takes. The values are the sky's own global uniforms and
    // the material declares none, which is what keeps one fog distance across the whole world.
    // That the globals exist is not asserted here: Godot refuses to compile a shader naming an
    // unregistered global, so a missing one reaches the harness as an engine error instead.
    private static void PufferFogFollowsSky(TestContext ctx)
    {
        var image = Image.CreateEmpty(2, 1, false, Image.Format.Rgba8);
        image.Fill(Colors.White);
        // A column set spanning both blends, so one Attach builds both lists and each blend's own
        // fog target is read off the shader it was compiled with.
        var renderer = new MultiMeshEmitterRenderer(ImageTexture.CreateFromImage(image), 2,
            new[] { true, false }, softParticles: false);
        var owner = new Node3D();
        ctx.Host.AddChild(owner);
        try
        {
            renderer.Attach(owner, 4, cullMargin: 1f);
            int lists = 0;
            foreach (var child in owner.GetChildren())
            {
                if (child is not MultiMeshInstance3D mmi
                    || mmi.MaterialOverride is not ShaderMaterial mat || mat.Shader == null)
                    continue;
                lists++;
                string code = mat.Shader.Code;
                bool additive = code.Contains("blend_add");
                string blend = additive ? "additive" : "mixed";
                ctx.Check(code.Contains("#include \"res://shaders/csky_atmosphere.gdshaderinc\""),
                    $"the {blend} list takes its fog colour and range from the sky's own globals");
                ctx.Check(code.Contains("csky_fog_amount(fog_world, CAMERA_POSITION_WORLD)"),
                    $"the {blend} list fogs by the sky's own amount at the fragment's world position");
                ctx.Check(
                    code.Contains(additive ? "mix(ALBEDO, vec3(0.0)" : "mix(ALBEDO, csky_fog_color"),
                    $"the {blend} list leaves the right value at full fog");
                foreach (var entry in mat.Shader.GetShaderUniformList())
                {
                    string name = entry.AsGodotDictionary()["name"].AsString();
                    ctx.Check(!name.Contains("fog"),
                        $"the {blend} list declares no fog parameter of its own, found {name}");
                }
            }
            ctx.Same(2, lists, $"a split column set builds one draw list per blend to check");
        }
        finally
        {
            owner.QueueFree();
        }
    }

    // Stays whole here rather than splitting an engine-free half into CSVM.Tests: Probes.Loadouts
    // calls StockLoadouts.Load and PlaneBuilder.Build to resolve Loadout.Bind's markers, both
    // native-backed and fatal off-engine, and binding needs a built plane either way.

}
