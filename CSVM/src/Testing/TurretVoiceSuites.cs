using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Airframe;
using CSVM.Flight.Audio;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.InstantAction;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>Whether a turret is heard firing, over C4's world, which carries all three mount
/// classes at once: the piratezep's gun rings, the two turret trucks, and the hull's twin cannon.
/// The voice's cue, its per-mount emitter and its playing state are read together, because each
/// alone passes a broken build.</summary>
internal static class TurretVoiceSuites
{
    private const string VoiceChapter = "C4";
    private const string TurretCue = "snd_chaingun";
    // The Firebrand: the airframe whose thirdp mount is the decode's own worked example, since its
    // 0.4-second FIRE_RATE is shorter than the lease and its gunner's cue is the turret one.
    private const string CarriedPlane = "player_fbrand";
    private const float StepDt = 1f / 60f;

    // How long the checks wait for one round, in frames. The piratezep belly ring redraws its
    // FIRE_RATE in [0.55, 0.7] s and alternates 6-10 s attacking with 4-5 s bored, so a whole bored
    // window plus a shot interval has to fit inside this.
    private const int ShotBudgetFrames = 1200;

    // The margin the sound manager leaves over a definition's audible distance before it silences
    // the voice (docs/formats/turrets.md). Held here as the decode's own figure rather than read
    // off GunVoice, so a build that culls at the RANGE pair itself fails this suite.
    private const float CullMargin = 1.1f;

    // Inside snd_chaingun's own 200 m audible distance and well past the shelf its band buys, so
    // the level read there is on the decoded law's ramp rather than on its flat head.
    private const float GainProbeMetres = 150f;

    [Suite("turret-gun-voices",
        "every turret the data gives a voice is heard firing from its own mount (BL-793): over C4, "
        + "each piratezep gun ring builds its OWN positional snd_chaingun emitter rather than one "
        + "shared per hull, levelled per frame by the decoded RANGE law over that definition's "
        + "authored audible distance and culled at 1.1 times it, the margin the sound manager "
        + "leaves over the pair, rather than at the engine-audio routine's 2000, carrying no Godot "
        + "attenuation model and no MaxDistance of its own, on the Effects bus at its source "
        + "asset's pitch with Doppler tracking off; at 150 m it stands at the level "
        + "SoundFalloff gives that pair rather than at Godot's inverse-distance one; the voice "
        + "sounds on the frame a round leaves the firepoint it "
        + "is placed at, is STILL sounding a quarter of a second later with no new round (the lease "
        + "the original renews per shot, not a clip restarted per projectile), goes quiet within "
        + "that lease once the gun stows, is STILL heard from just past the cue's own audible "
        + "distance, is silenced past the 1.1x cull and sounds again inside it; a carried gunner "
        + "builds the same voice under its host aircraft; and the two turret trucks and the "
        + "zeppelin's twin cannon stay silent, which is what their own entries author")]
    internal static void TurretGunVoices(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.SoundsPath, $"sound archive (soundsh)");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, VoiceChapter);
        ctx.RequireData(texturesPath, $"{VoiceChapter} textures");

        // Every distance below is the cue's own authored one. This suite reads at 1, not at the
        // reach the session ships.
        using var authored = TestContext.AtAuthoredSoundRadii();

        var turretDefs = TurretDefs.Load(ctx.ZrdrPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var soundDefs = SoundDefs.Load(ctx.ZrdrPath);
        soundDefs.TryGetValue(TurretCue, out var cue);
        ctx.Check(cue is { Is3D: true, Looped: true },
            $"{TurretCue} is authored 3D and LOOPED, so a turret's voice is a point in the world");
        if (cue == null)
        {
            return;
        }

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        using var archive = new SoundArchive(ctx.SoundsPath);
        var ears = new List<Vector3> { Vector3.Zero };
        ProjectilePool? pool = null;
        TurretEmplacementRuntime? emplacements = null;
        FlightController? bait = null;
        FlightController? carrier = null;
        Node3D? voiceHome = null;
        try
        {
            ctx.WithWorld(VoiceChapter, collision: false, world =>
            {
                var live = new ProjectilePool(textures, archive, soundDefs);
                pool = live;
                ctx.Host.AddChild(live);
                // The world's own sound node stands in as the emitter home, as GameSession hands
                // WorldRuntime.Sounds: a world gun's voice never goes inside the subtree it fires
                // from, since the animation runtime memoizes that subtree's node lookups.
                voiceHome = new Node3D { Name = "gun_voice_home" };
                ctx.Host.AddChild(voiceHome);
                var home = new GunVoiceHome(voiceHome, archive, soundDefs,
                    () => (IReadOnlyList<Vector3>)ears);

                var runtime = emplacements = new TurretEmplacementRuntime(turretDefs, weapons,
                    (pattern, scope) => world.Runtime.FindNodes(pattern, scope), live,
                    world.Runtime.WorldRoot, home);

                int voiced = runtime.Emplacements.Count(t => t.Voice != null);
                string silent = string.Join(", ", runtime.Emplacements.Where(t => t.Voice == null)
                    .GroupBy(t => t.Def.Title).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"));
                ctx.Note($"{VoiceChapter}: {voiced} of {runtime.Count} emplacements build a voice; silent by data: {silent}");

                // The .gw script leaves the piratezep switched off, and a ring under a switched-off
                // hull is dead before it is anything else.
                var hulls = world.Runtime.FindNodes("piratezep");
                ctx.Same(1, hulls.Count, $"{VoiceChapter}'s world carries the piratezep hull");
                if (hulls.Count != 1)
                {
                    return;
                }
                var zep = hulls[0];
                world.Runtime.SetTargetActive(zep, true);
                zep.Visible = true;

                // The rings OF THIS HULL. The MSG_TUR_BELLY title is authored on three entries and
                // the `ctur*` pattern matches a dozen zeppelin models, so a title match alone picks
                // up dormant rings on hulls this suite never switched on.
                var rings = runtime.Emplacements
                    .Where(t => t.Site is { } s && zep.IsAncestorOf(s))
                    .ToList();
                var belly = rings.Where(t => t.Def.Title == "MSG_TUR_BELLY").ToList();
                ctx.Check(belly.Count >= 2,
                    $"the piratezep carries its belly gun rings count={belly.Count} of {rings.Count} rings on the hull");
                if (belly.Count < 2)
                {
                    return;
                }
                ctx.Check(belly.All(t => t.Voice != null),
                    $"every belly ring built a voice ({belly.Count(t => t.Voice != null)} of {belly.Count})");

                // The ring question the entry asked: seventeen rings on one hull are seventeen
                // slots in the original, so two rings must not share one emitter.
                ctx.Check(!ReferenceEquals(belly[0].Voice, belly[1].Voice),
                    $"two rings on the same hull hold two voices, not one shared per hull");

                // The trucks and the twin cannon: the entry authors no SOUNDS on MSG_TUR_TRUCK, and
                // the twin cannon's wep_27 is not a CANNON, which is the fire path's second gate.
                var trucks = runtime.Emplacements
                    .Where(t => t.Label.StartsWith("MSG_TUR_TRUCK@", System.StringComparison.Ordinal))
                    .ToList();
                ctx.Same(2, trucks.Count, $"{VoiceChapter} places its two turret trucks");
                ctx.Check(trucks.Count > 0 && trucks.All(t => t.Voice == null),
                    $"a turret truck stays silent: its entry authors no SOUNDS block at all");
                var cannons = runtime.Emplacements
                    .Where(t => t.Label.StartsWith("MSG_TUR_ZEP_CANNON@", System.StringComparison.Ordinal))
                    .ToList();
                ctx.Check(cannons.Count > 0 && cannons.All(t => t.Voice == null),
                    $"the zeppelin's twin cannon stays silent too: its weapon carries no CANNON flag (count={cannons.Count})");

                var ring = belly[0];
                ctx.Check(ring.Alive && ring.Activated,
                    $"the ring under test is alive and awake alive={ring.Alive} awake={ring.Activated}");
                if (ring.Voice is not { } voice)
                {
                    return;
                }

                // The record ships the piratezep allied; forced hostile here so a plain player rig
                // is a target, the same stand-in the hull-blocking suites use.
                ring.SetTeam(InstantActionRuntime.EnemyTeam);
                ring.Def.InaccuracyDeg = 0f;

                // Parked down the centre of this ring's OWN arc rather than at a fixed offset: the
                // belly arc is a 140-degree wedge in the hull's frame, and where that points in the
                // world is a property of how the chapter placed the zeppelin.
                var at = ring.WorldPosition;
                var seat = at + (ring.BarrelWorldDir * 200f);
                ears[0] = at + new Vector3(30f, 0f, 0f);
                bait = BuildRig(ctx, planesGamez, textures, live, ctx.PlaneName, seat, at);

                void Step(int frames)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        runtime.SimStep(StepDt);
                        live.SimStep(StepDt);
                    }
                }

                ctx.Check(!voice.Sounding, $"before any round leaves, the ring is silent");
                bool engaged = StepToShot(ring, Step);
                ctx.Check(engaged, $"the ring engages the parked plane shots={ring.ShotsFired} gate={ring.Gate}");
                if (!engaged)
                {
                    return;
                }
                ctx.Check(voice.Sounding, $"…and its voice is sounding on the frame that round leaves");

                var emitter = voice.Emitter();
                float offMuzzle = ring.Firepoints.Min(fp => fp.GlobalPosition.DistanceTo(emitter.Position));
                ctx.Check(emitter.Name == TurretCue && offMuzzle < 0.01f,
                    $"…playing {emitter.Name} from the firepoint it fired out of, {offMuzzle:0.###} m off it");
                ctx.Check(emitter.Position.DistanceTo(ears[0]) > 20f
                    && emitter.Position.Length() > 1f,
                    $"…which is neither the listener nor the world origin ({emitter.Position.DistanceTo(ears[0]):0} m from the ear)");
                ctx.Check(Mathf.IsEqualApprox(emitter.RangeMax, cue.RangeMax)
                    && Mathf.IsEqualApprox(emitter.Cull, cue.RangeMax * CullMargin),
                    $"…attenuated over {TurretCue}'s own authored {emitter.RangeMax:0} m and culled at {emitter.Cull:0} m, the {CullMargin:0.0}x of it the sound manager allows, not EngineAudioCurves' 2000");
                CheckPlayers(ctx, voice);

                // The lease, which is the whole difference between a firing spell and a string of
                // isolated clips: the belly ring's own FIRE_RATE is 0.55-0.7 s, so a quarter second
                // after a round there is no new one and only the lease can be holding the loop.
                int held = 0;
                int atShot = ring.ShotsFired;
                for (int i = 0; i < 15; i++)
                {
                    Step(1);
                    if (voice.Sounding)
                    {
                        held++;
                    }
                }
                ctx.Check(held == 15,
                    $"the voice holds through {held} of 15 frames after the shot ({ring.ShotsFired - atShot} new round(s)), so it is leased rather than restarted");

                // The lease's far end, measured from a round rather than from here: stow the gun on
                // the very frame one leaves, so the clock below starts where the lease was renewed.
                if (!StepToShot(ring, Step))
                {
                    ctx.Check(false, $"a fresh round to time the lease from");
                    return;
                }
                ring.SetActivated(false);
                Step(24);
                bool soundingAt24 = voice.Sounding;
                Step(12);
                ctx.Check(soundingAt24 && !voice.Sounding,
                    $"a stowed gun runs its lease out and goes quiet: sounding at 0.40 s={soundingAt24}, at 0.60 s={voice.Sounding}");

                // The cull, driven from the listener rather than by moving the gun. Both placements
                // are past the distance the definition calls audible and only the far one is past
                // the margin over it, so a build culling at the RANGE pair itself fails the near.
                ring.SetActivated(true);
                if (!StepToShot(ring, Step))
                {
                    ctx.Check(false, $"a round to test the cull against");
                    return;
                }
                var abeam = ears[0];
                var muzzle = voice.Emitter().Position;
                var away = (abeam - muzzle).Normalized();
                ears[0] = muzzle + (away * (cue.RangeMax * 1.05f));
                Step(1);
                float justOut = voice.Emitter().Position.DistanceTo(ears[0]) / cue.RangeMax;
                ctx.Check(voice.Sounding && justOut > 1f && justOut < CullMargin,
                    $"a burst {justOut:0.00}x the {cue.RangeMax:0} m the definition calls audible is still heard, inside the margin the cull leaves over the RANGE pair");
                ears[0] = muzzle + (away * (cue.RangeMax * 1.15f));
                Step(1);
                float wellOut = voice.Emitter().Position.DistanceTo(ears[0]) / cue.RangeMax;
                ctx.Check(!voice.Sounding && wellOut > CullMargin,
                    $"…and {wellOut:0.00}x it, past the {CullMargin:0.0}x cull at {emitter.Cull:0} m, is silent");
                ears[0] = abeam;
                Step(1);
                ctx.Check(voice.Sounding,
                    $"…and sounds again from {ring.WorldPosition.DistanceTo(abeam):0} m, inside it");
                CheckGain(ctx, voice, cue, ears, GainProbeMetres, Step);

                // A carried gunner takes the same component, hung on its own host rather than on
                // the world's sound node: the original's turret slot is positional whoever owns it.
                carrier = BuildRig(ctx, planesGamez, textures, live, CarriedPlane,
                    at + new Vector3(600f, 0f, 0f), at);
                var carried = TurretController.BuildCarried(turretDefs,
                    PlaneStats.Load(ctx.ZrdrPath, CarriedPlane),
                    carrier.PlaneModel!, weapons, carrier, live,
                    new GunVoiceHome(carrier, archive, soundDefs, () => (IReadOnlyList<Vector3>)ears));
                ctx.Check(carried.Length > 0 && carried.All(t => t.Voice != null),
                    $"the {CarriedPlane}'s {carried.Length} carried gunner(s) build a voice of their own");
                if (carried.Length == 0 || carried[0].Voice is not { } carriedVoice)
                {
                    return;
                }
                ctx.Check(carriedVoice.GetParent() == carrier,
                    $"…hung on the host aircraft, not on the world's sound node (parent '{carriedVoice.GetParent()?.Name}')");
                ctx.Check(carriedVoice.Emitter().Name == TurretCue
                    && Mathf.IsEqualApprox(carriedVoice.Emitter().RangeMax, cue.RangeMax)
                    && Mathf.IsEqualApprox(carriedVoice.Emitter().Cull, cue.RangeMax * CullMargin),
                    $"…on the same cue, the same authored audible distance and the same {carriedVoice.Emitter().Cull:0} m cull as a world ring");
                CheckPlayers(ctx, carriedVoice);
                ctx.Note($"voices: {rings.Count} piratezep rings ({belly.Count} belly), {carried.Length} carried on {CarriedPlane}, {trucks.Count} truck(s) and {cannons.Count} twin cannon(s) silent; ring cue {TurretCue} audible {cue.RangeMax:0} m, cull {emitter.Cull:0} m, lease {TurretController.VoiceLeaseSeconds:0.0} s");
            });
        }
        finally
        {
            bait?.Free();
            carrier?.Free();
            emplacements?.Free();
            voiceHome?.Free();
            pool?.Free();
            textures.Dispose();
        }
    }

    /// <summary>One voice's live players. The pitch guard holds the decode in
    /// docs/formats/sounds.md: the original's world emitters carry no Doppler, and Godot's 3D
    /// player takes a shift from its own tracking mode without a line of ours asking for one. The
    /// attenuation guard holds the other half: the level is the decoded law's, so an engine model
    /// or a <c>MaxDistance</c> would multiply a second curve onto it. Internal so the hull's own
    /// voice suite reads the same properties rather than its own copy.</summary>
    internal static void CheckPlayers(TestContext ctx, GunVoice voice)
    {
        var players = voice.GetChildren().OfType<AudioStreamPlayer3D>().ToList();
        ctx.Same(1, players.Count, $"the voice holds exactly one positional player");
        foreach (var player in players)
        {
            ctx.Check(player.DopplerTracking == AudioStreamPlayer3D.DopplerTrackingEnum.Disabled
                && Mathf.IsEqualApprox(player.PitchScale, 1f)
                && player.Bus.ToString() == AudioBuses.Effects,
                $"…on the {AudioBuses.Effects} bus at pitch {player.PitchScale:0.000} with Doppler {player.DopplerTracking}");
            ctx.Check(player.AttenuationModel == AudioStreamPlayer3D.AttenuationModelEnum.Disabled
                && !(player.MaxDistance > 0f),
                $"…carrying no engine attenuation model ({player.AttenuationModel}) and no MaxDistance ({player.MaxDistance:0}), so the decoded law is the only curve on it");
        }
    }

    /// <summary>The level one gun voice stands at against <see cref="SoundFalloff"/>'s own answer
    /// for the same listener distance, definition and <c>VOLUME</c>. Internal so a hull's voice and
    /// a turret's are read the same way: a voice that sounds proves nothing about how loud it is,
    /// which is the whole of what the inverse-distance mapping got wrong.</summary>
    internal static void CheckGain(TestContext ctx, GunVoice voice, SoundDef cue,
        List<Vector3> ears, float metres, Action<int> step)
    {
        var from = voice.Emitter().Position;
        ears[0] = from + new Vector3(metres, 0f, 0f);
        step(1);
        float at = voice.Emitter().Position.DistanceTo(ears[0]);
        float want = SoundFalloff.GainDb(at, cue.RangeMin, cue.RangeMax, cue.Volume);
        ctx.Check(voice.Sounding && want > SoundFalloff.FloorDb
            && Mathf.Abs(voice.GainDb - want) < 0.05f,
            $"…and from {at:0} m it plays at {voice.GainDb:0.0} dB, the decoded law's own level for RANGE [{cue.RangeMin:0}, {cue.RangeMax:0}] ({want:0.0} dB), well above the {SoundFalloff.FloorDb:0} dB floor");
    }

    // Steps until one more round leaves this gun, and stops on that frame: every lease check below
    // is timed from the shot that renewed it, not from wherever the previous check happened to end.
    private static bool StepToShot(TurretController gun, Action<int> step)
    {
        int fired = gun.ShotsFired;
        for (int i = 0; i < ShotBudgetFrames && gun.ShotsFired == fired; i++)
        {
            step(1);
        }
        return gun.ShotsFired > fired;
    }

    // A parked aeroplane on the player's team: a target for a ring forced hostile, and a rig with
    // no phantom spawn velocity for the lead solve to chase.
    private static FlightController BuildRig(TestContext ctx, GameZ planesGamez,
        TextureArchive textures, ProjectilePool live, string plane, Vector3 pos, Vector3 look)
    {
        var stats = PlaneStats.Load(ctx.ZrdrPath, plane);
        var model = new PlaneBuilder(planesGamez, textures).Build(plane);
        var rig = new FlightController
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            Damage = new PlaneDamage(stats.DestroyableParts),
            PlayerIndex = 0,
            Projectiles = live,
            UseKeyboard = false,
            AllowPause = false,
        };
        rig.AddChild(model);
        rig.Setup(new FlightModel(stats), ctx.Camera, new CamParams(), pos, look);
        ctx.Host.AddChild(rig);
        rig.PlaceHeld(pos, look);
        return rig;
    }
}
