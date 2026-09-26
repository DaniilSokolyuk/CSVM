using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Ai;
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

/// <summary>How loud a firing gun is at the reach the build SHIPS. Two mounts a pilot meets in the
/// air: a world turret's own voice and an AI aeroplane's caliber loop. Its sibling
/// <see cref="TurretVoiceSuites"/> reads the same law at the authored radii, and asks whether a
/// voice exists at all. This one asks what a listener 200 to 1000 m off gets, which is what a
/// complaint about not hearing a shooting enemy is about.</summary>
internal static class GunVoiceReachSuites
{
    private const string ReachChapter = "C4";

    // The cue every shipped turret entry authors under SOUNDS.CANNON, and the one the belly rings
    // under test carry. No entry in ai.zrd names any other, so this is the turret gun sound.
    private const string TurretCue = "snd_chaingun";

    // The caliber loop an enemy fighter's forward guns carry; the smallest of the family, and so
    // the shortest-reaching of them.
    private const string MountCue = "snd_30cal";

    private const string MountPlane = "player_fbrand";
    private const float StepDt = 1f / 60f;
    private const int ShotBudgetFrames = 1200;

    // Where both halves assert. Inside every gun cue's scaled audible radius, and past the shelf
    // its band buys. The level read there is on the law's ramp, not on its flat head.
    private const float EngagementMetres = 300f;

    [Suite("gun-voice-reach",
        "what a listener 200 to 1000 m from a firing gun actually gets at the reach the build ships "
        + "(BL-933): a C4 belly ring firing on a parked plane holds a voice that is granted, "
        + "unculled and at the decoded law's own level for snd_chaingun's authored RANGE at 300 m, "
        + "and an AI aeroplane's snd_30cal loop the same at 300 m, both carrying no engine "
        + "attenuation model and no MaxDistance so that law is the only curve on them; the cull of "
        + "each is its authored audible distance times the shipped range factor times the decoded "
        + "1.1 margin, and the level at 200, 300, 500, 800 and 1000 m is recorded as a note, since "
        + "a turret cue reaching 500 m and a 30-cal reaching 375 m is what the data buys and no "
        + "amount of level work moves it")]
    internal static void GunVoiceReach(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.SoundsPath, $"sound archive (soundsh)");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, ReachChapter);
        ctx.RequireData(texturesPath, $"{ReachChapter} textures");

        // The whole point of this suite: the reach a player flies with, not the decoded radii.
        using var shipped = TestContext.AtShippedSoundRadii();

        var turretDefs = TurretDefs.Load(ctx.ZrdrPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var soundDefs = SoundDefs.Load(ctx.ZrdrPath);
        if (!soundDefs.TryGetValue(TurretCue, out var turretDef)
            || !soundDefs.TryGetValue(MountCue, out var mountDef))
        {
            ctx.Check(false, $"{TurretCue} and {MountCue} are both defined in sounds.json");
            return;
        }

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        using var archive = new SoundArchive(ctx.SoundsPath);
        var ears = new List<Vector3> { Vector3.Zero };
        ProjectilePool? pool = null;
        TurretEmplacementRuntime? emplacements = null;
        FlightController? bait = null;
        FlightController? mountRig = null;
        Node3D? voiceHome = null;
        try
        {
            ctx.WithWorld(ReachChapter, collision: false, world =>
            {
                var live = new ProjectilePool(textures, archive, soundDefs);
                pool = live;
                ctx.Host.AddChild(live);
                voiceHome = new Node3D { Name = "gun_reach_voice_home" };
                ctx.Host.AddChild(voiceHome);
                var home = new GunVoiceHome(voiceHome, archive, soundDefs,
                    () => (IReadOnlyList<Vector3>)ears);
                var runtime = emplacements = new TurretEmplacementRuntime(turretDefs, weapons,
                    (pattern, scope) => world.Runtime.FindNodes(pattern, scope), live,
                    world.Runtime.WorldRoot, home);

                var ring = AwakenBellyRing(ctx, world, runtime);
                if (ring?.Voice is not { } voice)
                {
                    return;
                }

                var at = ring.WorldPosition;
                ears[0] = at + new Vector3(30f, 0f, 0f);
                bait = BuildRig(ctx, planesGamez, textures, live, ctx.PlaneName,
                    at + (ring.BarrelWorldDir * 200f), at);

                void Step(int frames)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        runtime.SimStep(StepDt);
                        live.SimStep(StepDt);
                    }
                }

                if (!StepToShot(ring, Step))
                {
                    ctx.Check(false, $"the ring engages the parked plane shots={ring.ShotsFired} gate={ring.Gate}");
                    return;
                }
                var muzzle = voice.Emitter().Position;
                var away = (ears[0] - muzzle).Normalized();
                ctx.Check(Mathf.IsEqualApprox(voice.Emitter().Cull,
                        turretDef.RangeMax * SoundFalloff.RangeScale * WeaponSoundCue.CullMargin),
                    $"the ring's {TurretCue} is culled at {voice.Emitter().Cull:0} m, its authored {turretDef.RangeMax:0} m grown by the shipped x{SoundFalloff.RangeScale:0.###} and the decoded {WeaponSoundCue.CullMargin:0.0} margin");
                TurretVoiceSuites.CheckPlayers(ctx, voice);

                var rows = new List<string>();
                foreach (float metres in ProbeDistances())
                {
                    if (!StepToShot(ring, Step))
                    {
                        ctx.Check(false, $"a fresh round to read the level at {metres:0} m from");
                        return;
                    }
                    ears[0] = muzzle + (away * metres);
                    Step(1);
                    float dist = voice.Emitter().Position.DistanceTo(ears[0]);
                    float law = SoundFalloff.SessionGainDb(dist, turretDef.RangeMin, turretDef.RangeMax,
                        turretDef.Volume);
                    rows.Add(Row(metres, voice.Sounding, voice.GainDb, law));
                    CheckLevel(ctx, $"the ring's {TurretCue}", metres, voice.Sounding, voice.GainDb, law);
                }
                ctx.Note($"turret {TurretCue} RANGE [{turretDef.RangeMin:0}, {turretDef.RangeMax:0}] at x{SoundFalloff.RangeScale:0.###}, cull {voice.Emitter().Cull:0} m: {string.Join(", ", rows)}");

                // The AI aeroplane's own mount, which needs no world: its loop hangs on the
                // aircraft and reads the same nearest-listener seam the ring above does.
                mountRig = BuildRig(ctx, planesGamez, textures, live, MountPlane,
                    at + (away * 2000f), at);
                if (AiWeaponAudio.Attach(mountRig, archive, soundDefs, weapons,
                    () => (IReadOnlyList<Vector3>)ears) is not { } mount)
                {
                    ctx.Check(false, $"the AI aircraft builds its weapon audio");
                    return;
                }
                var from = mountRig.GlobalPosition;
                var mountRows = new List<string>();
                foreach (float metres in ProbeDistances())
                {
                    ears[0] = from + (away * metres);
                    mount.StartGunLoop(MountCue);
                    float dist = mount.GlobalPosition.DistanceTo(ears[0]);
                    float law = SoundFalloff.SessionGainDb(dist, mountDef.RangeMin, mountDef.RangeMax,
                        mountDef.Volume);
                    mountRows.Add(Row(metres, mount.LoopSounding, mount.LoopGainDb, law));
                    CheckLevel(ctx, $"an AI mount's {MountCue}", metres, mount.LoopSounding,
                        mount.LoopGainDb, law);
                }
                ctx.Check(Mathf.IsEqualApprox(mount.LoopCull,
                        mountDef.RangeMax * SoundFalloff.RangeScale * WeaponSoundCue.CullMargin),
                    $"…culled at {mount.LoopCull:0} m, its authored {mountDef.RangeMax:0} m grown by the same two factors");
                ctx.Note($"mount {MountCue} RANGE [{mountDef.RangeMin:0}, {mountDef.RangeMax:0}] at x{SoundFalloff.RangeScale:0.###}, cull {mount.LoopCull:0} m: {string.Join(", ", mountRows)}");
            });
        }
        finally
        {
            bait?.Free();
            mountRig?.Free();
            emplacements?.Free();
            voiceHome?.Free();
            pool?.Free();
            textures.Dispose();
        }
    }

    // The distances a shooting enemy is met at. An engagement opens around 800 m and closes inside
    // 300 m, and the complaint covers that whole span.
    private static float[] ProbeDistances() => new[] { 200f, EngagementMetres, 500f, 800f, 1000f };

    private static string Row(float metres, bool sounding, float gainDb, float lawDb) =>
        sounding
            ? Log.Format($"{metres:0} m {gainDb:0.0} dB")
            : Log.Format($"{metres:0} m culled (law {lawDb:0.0} dB)");

    // Two verdicts in one. A sounding voice stands at the law's own level for its distance, and a
    // voice is granted at all at the engagement distance. Past the cull only the first applies,
    // which is the honest half of the reading.
    private static void CheckLevel(TestContext ctx, string what, float metres, bool sounding,
        float gainDb, float lawDb)
    {
        if (Mathf.IsEqualApprox(metres, EngagementMetres))
        {
            ctx.Check(sounding && lawDb > SoundFalloff.FloorDb,
                $"{what} is granted a voice and unculled at {metres:0} m (law {lawDb:0.0} dB)");
        }
        if (!sounding)
        {
            return;
        }
        ctx.Check(Mathf.Abs(gainDb - lawDb) < 0.05f,
            $"{what} plays at {gainDb:0.0} dB from {metres:0} m, the decoded law's own level there ({lawDb:0.0} dB)");
    }

    // The piratezep is left switched off by the .gw script, and a ring under a switched-off hull is
    // dead before it is anything else. Returns the belly ring under test, or null.
    private static TurretController? AwakenBellyRing(TestContext ctx, TestWorld world,
        TurretEmplacementRuntime runtime)
    {
        var hulls = world.Runtime.FindNodes("piratezep");
        ctx.Same(1, hulls.Count, $"{ReachChapter}'s world carries the piratezep hull");
        if (hulls.Count != 1)
        {
            return null;
        }
        var zep = hulls[0];
        world.Runtime.SetTargetActive(zep, true);
        zep.Visible = true;
        var belly = runtime.Emplacements
            .Where(t => t.Site is { } s && zep.IsAncestorOf(s) && t.Def.Title == "MSG_TUR_BELLY")
            .ToList();
        ctx.Check(belly.Count >= 1 && belly[0].Voice != null,
            $"the piratezep's belly gun rings build a voice ({belly.Count(t => t.Voice != null)} of {belly.Count})");
        if (belly.Count == 0 || belly[0].Voice == null)
        {
            return null;
        }
        var ring = belly[0];
        // The record ships the piratezep allied; forced hostile so a plain player rig is a target.
        ring.SetTeam(InstantActionRuntime.EnemyTeam);
        ring.Def.InaccuracyDeg = 0f;
        return ring;
    }

    // Steps until one more round leaves this gun, and stops on that frame. Every level below is
    // then read off a freshly renewed lease, not one part-way through running out.
    private static bool StepToShot(TurretController gun, Action<int> step)
    {
        int fired = gun.ShotsFired;
        for (int i = 0; i < ShotBudgetFrames && gun.ShotsFired == fired; i++)
        {
            step(1);
        }
        return gun.ShotsFired > fired;
    }

    // A parked aeroplane, a target for a ring forced hostile and a carrier for the mount half.
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
