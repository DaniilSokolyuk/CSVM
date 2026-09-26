using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.InstantAction;
using CSVM.Session.Roster;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>CM14's intro (C2B/M04's <c>generic_intro</c>) authors no <c>CALLBACK</c> 913 of its
/// own, unlike the bespoke C1/M04 intro, yet the original parks every AI vehicle imperatively
/// before any mission's intro plays, independent of what that intro's own data authors
/// (docs/formats/anim-definitions/cutscenes.md). Proves the gap and its fix: an already-flying AI
/// aircraft is parked the instant code 20 opens the episode though 913 never appears in the
/// intro's own codes, the Gemini bay generator behind CM14's fighters (C2B/M04's
/// <c>geminizep</c>) launches nothing while the world is held, and both come back with the
/// handoff. The second suite takes the other half of that rule: the park belongs to every mission
/// start, so an Instant Action wave, which bootstraps no movie at all, is parked by the mission
/// start itself and lifted on its first step.</summary>
internal static class CutsceneAiParkSuites
{
    private const string Chapter = "C2B";
    private const string Mission = "M04";
    private const string IntroAnim = "generic_intro";
    private const string Generator = "geminizep";

    // The Instant Action half: a wave opens with no movie, so its bootstrap is the shared
    // no-movie definition and the start park has nothing to hold it.
    private const string IaChapter = "C1";
    private const string IaMission = "IA1";
    private const string Bootstrap = "player_setup";
    private const int WaveSize = 2;
    private const float StepDt = 1f / 60f;
    private const float HoldWindowS = 3f;

    // The bay's own wave_period + ind_period (2 s + 3 s, C2B/M04's egen.zrd), plus headroom for
    // the first cycle to come due after the hold lifts.
    private const float ResumeWaitS = 8f;

    // The mission's own credit (C2B/M04's objectives.zrd authors WAKEUP_GENERATOR geminizep 5).
    private const int WakeupCredit = 5;

    [Suite("cutscene-ai-park-intro",
        "CM14's intro (C2B/M04's generic_intro) authors no CALLBACK 913 of its own, yet an "
        + "already-flying AI aircraft is parked (Inert and Parked) the instant its code 20 opens "
        + "the episode; the Gemini bay generator behind CM14's fighters launches nothing for a "
        + "held window and resumes once the intro hands off, and the parked aircraft is revealed "
        + "with it")]
    internal static void CutsceneParksAiWithNoAuthored913(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(texturesPath, $"{Chapter} textures");

        EnemyGeneratorDef? gemini = null;
        foreach (var d in EnemyGenerators.Load(missionZrdr))
        {
            if (d.Node.Equals(Generator, StringComparison.OrdinalIgnoreCase))
            {
                gemini = d;
            }
        }

        ctx.Check(gemini != null, $"{Chapter}/{Mission} authors the generator '{Generator}'");
        if (gemini == null)
        {
            return;
        }

        var nets = AiNets.Load(chapterZrdr);
        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var report = new StringBuilder();
        try
        {
            ctx.WithWorld(Chapter, collision: false, Mission, world =>
                Drive(ctx, world, gemini, nets, planesGamez, textures, report));
        }
        finally
        {
            textures.Dispose();
        }

        ctx.WriteArtifact($"test-cutscene-ai-park-intro-{Chapter}-{Mission}.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: the intro parks the AI with no authored 913, and the Gemini bay stays silent through the hold");
    }

    [Suite("cutscene-ai-park-mission-start",
        "an Instant Action mission (C1/IA1) bootstraps no story intro, yet the original's mission "
        + "start parks every AI vehicle before any start list of any mission type runs: the wave's "
        + "aircraft are parked (Inert and Parked) by the session's mission-start park with nothing "
        + "playing and no callback code raised, and the first step lifts them, which is where the "
        + "bootstrap definition's own 914 lands")]
    internal static void MissionStartParksAiWithNoIntro(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, IaChapter);
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, IaChapter, IaMission),
            $"{IaChapter}/{IaMission} zrdr");
        ctx.RequireData(texturesPath, $"{IaChapter} textures");

        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var report = new StringBuilder();
        try
        {
            ctx.WithWorld(IaChapter, collision: false, IaMission, world =>
                DriveMissionStart(ctx, world, planesGamez, textures, report));
        }
        finally
        {
            textures.Dispose();
        }

        ctx.WriteArtifact($"test-cutscene-ai-park-start-{IaChapter}-{IaMission}.txt", report.ToString());
        ctx.Note($"{IaChapter}/{IaMission}: the mission start parks the wave with no intro to hold it, and the first step lifts it");
    }

    private static void Drive(TestContext ctx, TestWorld world, EnemyGeneratorDef gemini,
        IReadOnlyList<AiNet> nets, GameZ planesGamez, TextureArchive textures, StringBuilder report)
    {
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        var stage = new Node3D { Name = "CutsceneAiParkStage" };
        ctx.Host.AddChild(stage);
        var runtime = new AnimRuntime { AutoStart = false, ManualAdvance = true, SoundHandledElsewhere = true };
        ctx.Host.AddChild(runtime);
        FlightController? flyer = null;
        AiGeneratorRuntime? generators = null;
        try
        {
            flyer = BuildAiRig(ctx, planesGamez, textures, pool);
            var flying = new List<FlightController> { flyer };
            cutscene.BindWorld(runtime);
            cutscene.BindRigs(Array.Empty<PlayerRig>(), () => flying);

            runtime.Bind(stage, world.Session.Program.Subset(IntroAnim));
            runtime.CallbackHost = cutscene.Host;

            ctx.Check(!flyer.Inert && !cutscene.AiParked, $"the AI aircraft flies before the intro plays");
            runtime.Play(IntroAnim);

            ctx.Check(cutscene.Playing && cutscene.HoldsWorld,
                $"'{IntroAnim}' takes the session and holds the world");
            ctx.Check(!cutscene.Codes.Contains(913),
                $"'{IntroAnim}' authors no 913 of its own (codes: [{string.Join(",", cutscene.Codes)}])");
            ctx.Check(cutscene.AiParked && flyer.Inert && flyer.Parked,
                $"the AI is parked anyway, the imperative park the original runs before any intro");

            var host = world.Runtime.FindNodes(Generator) is { Count: > 0 } hits ? hits[0] : null;
            ctx.Check(host != null, $"'{Generator}' is a world node");
            if (host == null)
            {
                return;
            }

            // The gamez node's own pose sits under the generator's 300 m launch gate; the mission's
            // zeppelin record is where ZeppelinRuntime.Place puts it in a flown session.
            foreach (var z in Zeppelins.Load(SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission)))
            {
                if (z.Node.Equals(Generator, StringComparison.OrdinalIgnoreCase))
                {
                    host.GlobalPosition = z.Position;
                }
            }

            generators = new AiGeneratorRuntime(new[] { gemini },
                (name, scope) => world.Runtime.FindNodes(name, scope) is { Count: > 0 } n ? n[0] : null,
                nets, ctx.PlaneName, (_, _, _, _) => default);
            ctx.Same(1, generators.LiveCount, $"'{Generator}' is live over the built world");
            // The bay starts uncredited (capacity is dead data, docs/formats/mission-entities/
            // enemy-generators.md): the mission's own WAKEUP_GENERATOR geminizep 5 is what feeds it.
            ctx.Same(1, generators.GrantWaveCapacity(Generator, WakeupCredit),
                $"the mission's own WAKEUP_GENERATOR credit ({WakeupCredit}) reaches '{Generator}'");

            for (int i = 0; i < (int)(HoldWindowS / StepDt); i++)
            {
                if (!cutscene.HoldsWorld)
                {
                    generators.SimStep(StepDt);
                }
            }

            report.AppendLine($"held {HoldWindowS:0} s: generator launched {generators.LaunchOrdinal} while HoldsWorld={cutscene.HoldsWorld}");
            ctx.Same(0, generators.LaunchOrdinal,
                $"the Gemini bay launches nothing for {HoldWindowS:0} s of held world, the same gate SessionSimulation puts ahead of the generator phase");

            runtime.Stop(IntroAnim);
            cutscene.Tick();
            ctx.Check(!cutscene.Playing && !cutscene.HoldsWorld && !cutscene.AiParked,
                $"the intro's end hands off, releasing the hold and the park");
            ctx.Check(!flyer.Inert && !flyer.Parked, $"the parked aircraft comes back with the handoff");

            for (int i = 0; i < (int)(ResumeWaitS / StepDt); i++)
            {
                if (!cutscene.HoldsWorld)
                {
                    generators.SimStep(StepDt);
                }
            }

            ctx.Check(generators.LaunchOrdinal > 0,
                $"and the bay resumes launching once the hold lifts (ordinal {generators.LaunchOrdinal})");
        }
        finally
        {
            generators?.Free();
            flyer?.Free();
            runtime.Free();
            stage.Free();
            cutscene.Free();
            pool.Free();
        }
    }

    private static void DriveMissionStart(TestContext ctx, TestWorld world, GameZ planesGamez,
        TextureArchive textures, StringBuilder report)
    {
        var startDefs = world.Session.Program.Subset(world.Session.Program.StartAnims).Defs;
        var startNames = new List<string>();
        foreach (var def in startDefs)
        {
            if (def.AnimName is { } n && !startNames.Contains(n))
            {
                startNames.Add(n);
            }
        }

        report.AppendLine($"{IaChapter}/{IaMission} start list closure: {string.Join(", ", startNames)}");
        ctx.Check(startNames.Contains(Bootstrap),
            $"the wave bootstraps '{Bootstrap}', the definition of a mission opening without a movie");
        ctx.Check(!startNames.Any(n => CutsceneController.IsIntro(n)),
            $"and bootstraps no story intro, so nothing raises a hold code over it");

        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        var runtime = new AnimRuntime { AutoStart = false, ManualAdvance = true, SoundHandledElsewhere = true };
        ctx.Host.AddChild(runtime);
        var wave = new List<FlightController>();
        try
        {
            for (int i = 0; i < WaveSize; i++)
            {
                wave.Add(BuildAiRig(ctx, planesGamez, textures, pool, $"ia1_wave1_{i + 1}"));
            }

            cutscene.BindWorld(runtime);
            cutscene.BindRigs(Array.Empty<PlayerRig>(), () => wave);
            ctx.Check(wave.All(ai => !ai.Inert && !ai.Parked) && !cutscene.AiParked,
                $"the wave's {wave.Count} aircraft fly before the mission start parks them");

            cutscene.ParkAtMissionStart();
            report.AppendLine($"after the mission-start park: AiParked={cutscene.AiParked}, codes=[{string.Join(",", cutscene.Codes)}]");
            ctx.Check(cutscene.AiParked && wave.All(ai => ai.Inert && ai.Parked),
                $"the mission start parks the whole wave, with no intro and no episode to hold it");
            ctx.Check(!cutscene.Playing && !cutscene.HoldsWorld,
                $"and parks without opening an episode: the original's park is imperative, not a code");
            ctx.Same(0, cutscene.Codes.Count,
                $"no callback code was raised to park it (codes: [{string.Join(",", cutscene.Codes)}])");
            int present = InstantActionDirector.WaveMembersPresent(wave, zeppelinRun: false);
            ctx.Same(wave.Count, present,
                $"the wave-clear walk still counts every parked member, so the park is not a cleared wave");
            var sequencer = new InstantActionWaves(new[] { WaveSize, 0, 0, 0 });
            sequencer.Start();
            ctx.Check(sequencer.Step(present) == 0 && !sequencer.Finished,
                $"and the sequencer holds wave 1 through the park instead of finishing the mission");

            cutscene.Tick();
            report.AppendLine($"after the first step: AiParked={cutscene.AiParked}");
            ctx.Check(!cutscene.AiParked && wave.All(ai => !ai.Inert && !ai.Parked),
                $"the first step lifts the park, where the bootstrap definition's own reset raises 914");
            cutscene.Tick();
            ctx.Check(!cutscene.AiParked && wave.All(ai => !ai.Inert),
                $"and the lift is once: a later step neither re-parks the wave nor revives it again");
        }
        finally
        {
            foreach (var ai in wave)
            {
                ai.Free();
            }

            runtime.Free();
            cutscene.Free();
            pool.Free();
        }
    }

    // A minimal AI actor for the park/reveal check: no roster wiring, just an aircraft that flies.
    private static FlightController BuildAiRig(TestContext ctx, GameZ planesGamez,
        TextureArchive textures, ProjectilePool pool, string name = "cm14_gemini_fighter")
    {
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
        var rig = new FlightController
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            Damage = stats.DestroyableParts.Count > 0 || stats.VehicleHealth is > 0f
                ? PlaneDamage.For(stats) : null,
            Projectiles = pool,
            UseKeyboard = false,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            Team = AimAssist.PlayerTeam + 1,
        };
        rig.AddChild(model);
        rig.Setup(new FlightModel(stats), null, new CamParams(), new Vector3(0f, 800f, 0f), Vector3.Forward);
        rig.Name = name;
        ctx.Host.AddChild(rig);
        return rig;
    }
}
