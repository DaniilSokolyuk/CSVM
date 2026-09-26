using System;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using Godot;

namespace CSVM.Testing;

/// <summary>Losing the aircraft loses the campaign mission: the player's own death routed into
/// the objective graph's lost ending, over the first story mission's BUILT world and its own
/// shipped script. Three legs, so the rule is measured against both of its alternatives: the
/// crash ends the mission by default, <c>--no-crash-loss</c> leaves it running, and a mission
/// nobody crashes in runs on. ⚠ The crash state read here is the aircraft's own
/// <c>Downed</c> report, never an altitude: the under-map backstop teleports without one
/// (<c>docs/verification.md</c> INSTR-22), and the fourth leg pins that.</summary>
internal static class CampaignPlayerDeathSuites
{
    private const string Chapter = "C3";
    private const string Mission = "M01";

    private const float StepDt = 1f / 60f;

    // The shortest wrap-up either of the graph's own authored endings runs, and the window every
    // leg is measured over. A player death takes neither, so a mission that ends on one of them
    // instead is separable from a mission that ends on this one.
    private const float WrapUpDelay = 3f;
    private const float WrapUpWindow = 6f;

    // Well below FlightController's under-map backstop, and below any C3 terrain.
    private const float UnderMapAltitude = -400f;

    // BL-491: a campaign mission had three endings and none of them was the player dying, so
    // the aircraft could be lost and the mission flew on.
    [Suite("campaign-player-death",
        "losing the player's aircraft over C3/M01's own BUILT world and shipped script, the "
        + "mission being one of the four that author no loss at all, so a Lost outcome there "
        + "can only be the death: a crash driven through the production death path ends the "
        + "mission lost where the wreck lands rather than on either wrap-up delay, hands the "
        + "player back to the cabin, and commits nothing to the persist log; --no-crash-loss "
        + "leaves the same crash flying, a mission nobody crashes in runs on, and an aircraft "
        + "the under-map backstop teleported ends nothing at all")]
    internal static void CampaignPlayerDeath(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");
        ctx.RequireData(texturesPath, $"{Chapter} textures");

        CampaignMission? found = null;
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(Chapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(Mission, StringComparison.OrdinalIgnoreCase))
            {
                found = m;
            }
        }
        if (found is not { } mission)
        {
            throw new SuiteSkippedException($"{Chapter}/{Mission} is not in cm_sequence");
        }

        var script = ObjectiveScript.Load(missionZrdr);
        var report = new StringBuilder();
        CheckAuthored(ctx, script, report);

        ctx.WithWorld(Chapter, collision: false, Mission,
            world => Drive(ctx, world, script, mission, texturesPath, report));

        ctx.WriteArtifact($"test-campaign-player-death-{Chapter}-{Mission}.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: the player's death ends the mission lost, and --no-crash-loss keeps it flying");
    }

    // Why this mission is the case that distinguishes the rule: C3/M01 authors NO loss at all, so
    // a Lost outcome here can only be the player's own death. Four of the 21 shipped campaign
    // missions are like it (docs/formats/objectives.md, "Win and loss").
    private static void CheckAuthored(TestContext ctx, ObjectiveScript script, StringBuilder report)
    {
        int instantLoss = 0, flaggedLost = 0;
        foreach (var def in script.Objectives)
        {
            instantLoss += def.InstantLoss ? 1 : 0;
            flaggedLost += def.Lost ? 1 : 0;
        }
        report.AppendLine($"authored: {script.Objectives.Count} objective(s), {instantLoss} INSTANTLOSS, {flaggedLost} LOST");
        ctx.Same(0, instantLoss,
            $"{Chapter}/{Mission} authors no INSTANTLOSS, so its script cannot lose the mission");
        ctx.Same(0, flaggedLost,
            $"…and no LOST-flagged objective either, so a Lost outcome here is the player's own death");
    }

    private static void Drive(TestContext ctx, TestWorld world, ObjectiveScript script,
        CampaignMission mission, string texturesPath, StringBuilder report)
    {
        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        ProjectilePool? pool = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var control = Fly(ctx, world, script, mission, planesGamez, textures, live,
                endsOnPlayerDeath: true, crash: false, underMap: false, report, "control");
            ctx.Check(!control.Ended,
                $"a mission nobody crashes in is still running after {WrapUpWindow:0} s: {control.Line}");

            var died = Fly(ctx, world, script, mission, planesGamez, textures, live,
                endsOnPlayerDeath: true, crash: true, underMap: false, report, "crashed");
            ctx.Check(died.Ended && died.Outcome == MissionOutcome.Lost,
                $"losing the aircraft ends the mission lost: {died.Line}");
            ctx.Check(died.ReturnToCabin,
                $"…and hands the player back to the cabin, the way the graph's own endings do: {died.Line}");
            // A crash flown into the ground is already down, so the debrief is due at once: the
            // original's delay on this ending is the wreck's fall, and this one had none.
            ctx.Check(died.EndedAt > 0f && died.EndedAt < WrapUpDelay,
                $"…where the wreck lands rather than on one of the graph's own wrap-up delays: {died.EndedAt:0.000} s");
            ctx.Check(died.PersistCount == 0,
                $"…committing nothing to the persist log, so the retry starts from the chapter state the profile already held: {died.PersistCount} object(s)");

            var flewOn = Fly(ctx, world, script, mission, planesGamez, textures, live,
                endsOnPlayerDeath: false, crash: true, underMap: false, report, "no-crash-loss");
            ctx.Check(!flewOn.Ended,
                $"--no-crash-loss leaves the same crash flying: {flewOn.Line}");

            // ⚠ The able-to-fail control for what "crashed" means: the under-map backstop respawns
            // without a crash flag and without a Downed report, so a rule read off altitude would
            // end the mission here and this leg would go red.
            var underMap = Fly(ctx, world, script, mission, planesGamez, textures, live,
                endsOnPlayerDeath: true, crash: false, underMap: true, report, "under-map");
            ctx.Check(!underMap.Ended && !underMap.Crashed,
                $"an aircraft the under-map backstop teleported never crashed and ends nothing: {underMap.Line}");
        }
        finally
        {
            pool?.Free();
            textures.Dispose();
        }
    }

    // One flown leg: a fresh director and graph over the same world, the player's death (or its
    // absence) driven through the production path, then stepped past the wrap-up.
    private static Leg Fly(TestContext ctx, TestWorld world, ObjectiveScript script,
        CampaignMission mission, GameZ planesGamez, TextureArchive textures, ProjectilePool live,
        bool endsOnPlayerDeath, bool crash, bool underMap, StringBuilder report, string label)
    {
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var director = CampaignDirector.Create(script, mission, profile, null);
        director.EndsOnPlayerDeath = endsOnPlayerDeath;
        var human = HumanRig(ctx, planesGamez, textures, live,
            new Vector3(0f, underMap ? UnderMapAltitude : 800f, 0f), Vector3.Forward);
        try
        {
            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Sounds = world.Runtime.Sounds,
                Projectiles = live,
                ListenerPosition = () => human.WorldPosition,
                PlayerAircraft = () => human,
                Rng = new Random(1),
            });

            // One step first: the director subscribes to the player's death on the first step that
            // finds an aircraft, because the rig is built after Attach has run.
            director.Step(StepDt);
            if (crash)
            {
                human.DebugForceCrash();
            }

            float elapsed = 0f, endedAt = -1f;
            while (elapsed < WrapUpWindow)
            {
                if (underMap)
                {
                    human.SimStep(StepDt);
                }
                director.Step(StepDt);
                elapsed += StepDt;
                if (endedAt < 0f && director.Result != null)
                {
                    endedAt = elapsed;
                }
            }

            var result = director.Result;
            var leg = new Leg
            {
                Ended = result != null,
                Outcome = result?.Outcome ?? MissionOutcome.None,
                ReturnToCabin = director.ReturnToCabin,
                EndedAt = endedAt,
                Crashed = human.Crashed,
                PersistCount = profile.PersistLog.Count,
                Line = $"{label}: ended={result != null} outcome={result?.Outcome.ToString() ?? "-"} "
                    + $"at={endedAt:0.00}s crashed={human.Crashed} cabin={director.ReturnToCabin} "
                    + $"persist={profile.PersistLog.Count}",
            };
            report.AppendLine(leg.Line);
            return leg;
        }
        finally
        {
            human.Free();
        }
    }

    // The human rig, built the way the sibling campaign suites build theirs: a real aircraft node
    // with real damage state, so DebugForceCrash goes through the production death path.
    private static FlightController HumanRig(TestContext ctx, GameZ planesGamez,
        TextureArchive textures, ProjectilePool live, Vector3 pos, Vector3 lookAt)
    {
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
        var rig = new FlightController
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            Damage = stats.DestroyableParts.Count > 0 || stats.VehicleHealth is > 0f
                ? PlaneDamage.For(stats) : null,
            PlayerIndex = FlightRoster.ShooterIdBase - 1,
            IsHumanPiloted = true,
            Projectiles = live,
            UseKeyboard = false,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            Team = AimAssist.PlayerTeam,
        };
        rig.AddChild(model);
        rig.Setup(new FlightModel(stats), null, new CamParams(), pos, pos + lookAt);
        rig.Name = "player1";
        ctx.Host.AddChild(rig);
        return rig;
    }

    private readonly record struct Leg
    {
        public bool Ended { get; init; }

        public MissionOutcome Outcome { get; init; }

        public bool ReturnToCabin { get; init; }

        public float EndedAt { get; init; }

        public bool Crashed { get; init; }

        public int PersistCount { get; init; }

        public string Line { get; init; }
    }
}
