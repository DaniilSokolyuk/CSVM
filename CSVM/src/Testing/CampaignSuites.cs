using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>Suites over the campaign runtime: what a mission's objectives graph does with the
/// shipped choreography, what a mission leaves destroyed, what survives the profile file, and what
/// a later mission of the same chapter starts with.</summary>
internal static class CampaignSuites
{
    // How many persisted objects the first mission destroys. Three is enough to prove the log
    // carries a set rather than one lucky node, and cheap enough to leave the world build dominant.
    private const int TargetCount = 3;

    // The decoded worked mission the headless graph suite drives (docs/formats/objectives.md's
    // vocabulary, walked in full for C2/M01; C1/M02 is the same vocabulary at a size that fits one
    // suite). Its chapter world is never built here: the graph runs against a scripted world.
    private const string GraphChapter = "C1";

    private const string GraphMission = "M02";

    // The one mission with a bespoke intro definition, and the worked case of the whole cutscene
    // decode (docs/formats/anim-definitions/cutscenes.md). The other twelve share `generic_intro`,
    // which authors the same eight codes in the same two places.
    private const string IntroChapter = "C1";

    private const string IntroMission = "M04";

    private const string IntroAnim = "mission_intro_animation";

    // The one story mission whose opening cutscene no start list names: the start anim calls it,
    // and the zeppelin's destruction it frames is that start anim's later call.
    private const string CalledChapter = "C3";

    private const string CalledMission = "M03";

    private const string CalledFrom = "calldestroy_the_cargozep";

    private const string CalledAnim = "cgzep_camera";

    private const string CalledDestruction = "destroy_the_cargozep";

    // BL-458: the mission whose SECONDARY (OBJECTIVE3, IDENTITY SECONDARY 11) and OBJECTIVE11
    // both gate on DANGER_ZONES_COMPLETED (dzpath1, dzpath4), the worked case that was
    // unreachable before a campaign session armed its own dzpathN gates.
    private const string DangerZoneChapter = "C3";

    private const string DangerZoneMission = "M01";

    // The reward-cash suite names its mission rather than following --chapter: C3/M01 is ordinal 1,
    // and it is the paying record whose objective bit a suite can reach without flying the mission.
    private const string CashChapter = "C3";

    private const string CashMission = "M01";

    // The chain-killed persisted object: C3's suspension bridge, dropped in the chapter's first
    // campaign mission by the fuel truck parked on it, and standing again in the chapter's second
    // (cm_sequence seq 0 and 1). The bridge is the install's one PERSIST_LOG def whose kill arrives
    // as a CALL_ANIMATION from another destructible's death.
    private const string BridgeChapter = "C3";

    private const string BridgeMission = "M01";

    private const string BridgeLaterMission = "M05";

    private const string BridgeDef = "susp_bridge";

    private const string BridgeTruckDef = "bridge_truck01";

    // The wildcard reader def the same chain reaction also kills, the control for a persisted kill
    // whose flag needs no hand-over.
    private const string ChainTruckDef = "t_truck**";

    // The code that takes the chrome off and the view off the aircraft, named here rather than read
    // off the host: what the presentation check asks of it is a property of the shipped data.
    private const int PresentationCode = 2;

    // The docking film's own completion code, the only ending twenty of the shipped missions have.
    private const int DockingCompleteCode = 13;

    // What the definition's `callback_sequence` authors, and what its RESET_STATE asserts as the
    // gameplay end state. Both lists are the shipped data, not this engine's choice.
    private static readonly int[] MovieCodes = { 20, 2, 11, 14, 913 };

    private static readonly int[] RestoreCodes = { 1, 10, 914, 667 };

    // The pane ratios the letterbox fit is swept over: 4:3, 16:10, the card's own 1.64211, 16:9
    // (the project's own default 1280x720), 2558x1408 (the window the reported bars leak was
    // captured in), 21:9, 3440x1440, and 3840x1080, the widest a borderless default reaches.
    private static readonly float[] LetterboxAspects =
        { 4f / 3f, 1.6f, 1.64211f, 16f / 9f, 2558f / 1408f, 21f / 9f, 3440f / 1440f, 32f / 9f };

    [Suite("campaign-persistence",
        "the cross-mission state log (B12, BL-243): three PERSIST_LOG objects destroyed in one "
        + "campaign mission are captured, survive the profile file and a second store instance, "
        + "and start the next mission of the SAME chapter destroyed, while a save-only "
        + "destructible killed alongside them never enters the log and starts that mission "
        + "intact; the later world is built from the bootstrap, so the log is the only thing "
        + "that could have wrecked them")]
    internal static void CampaignPersistence(TestContext ctx)
    {
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        var second = LastMissionOf(missions, ctx.Chapter);
        if (second is not { } later
            || CampaignSequence.PreviousInSameChapter(missions, later.Seq) is not { } earlier)
        {
            throw new SuiteSkippedException(
                $"chapter {ctx.Chapter} holds fewer than two campaign missions");
        }

        int chapter = later.Campaign;
        var profile = CampaignProfileDef.NewProfile("Zachary");
        string profileDir = Path.Combine(ctx.ScratchDir, "campaign-persistence");
        var store = new CampaignProfileStore(profileDir);
        var report = new StringBuilder();
        report.AppendLine($"chapter {ctx.Chapter} ({chapter}) mission {earlier.MissionFolder} -> {later.MissionFolder}");

        var carried = new List<int>();
        int saveOnly = -1;
        ctx.WithWorld(ctx.Chapter, collision: false, earlier.MissionFolder, world =>
        {
            var registry = world.Runtime.Destructibles;
            var persistedNodes = PersistedNodes(registry);
            foreach (int node in persistedNodes.Keys)
            {
                if (carried.Count >= TargetCount)
                {
                    break;
                }

                if (Live(registry, persistedNodes[node]) is { } live && live.MaxHealth > 0f)
                {
                    world.Runtime.DamageAt(persistedNodes[node], live.MaxHealth);
                    carried.Add(node);
                    report.AppendLine($"destroyed persisted node={node} name={persistedNodes[node].Name}");
                }
            }

            saveOnly = DestroySaveOnly(world.Runtime, persistedNodes, report);
            ctx.Check(carried.Count > 0, $"chapter {ctx.Chapter} has PERSIST_LOG destructibles to destroy");

            var captured = CampaignPersistLog.Capture(world.Runtime);
            var capturedNodes = new HashSet<int>();
            foreach (var state in captured)
            {
                capturedNodes.Add(state.Node);
                ctx.Check(state.Destroyed, $"captured state is the destroyed one node={state.Node}");
            }

            foreach (int node in carried)
            {
                ctx.Check(capturedNodes.Contains(node), $"the log carries the destroyed node node={node}");
            }

            ctx.Check(saveOnly < 0 || !capturedNodes.Contains(saveOnly),
                $"a destructible with no PERSIST_LOG def stays out of the log node={saveOnly}");
            // The director commits a capture only on a won mission, which is when the original
            // writes its world-state carrier; the merge below stands in for that commit.
            ctx.Check(CampaignPersistLog.CommitsOn(MissionOutcome.Won)
                && !CampaignPersistLog.CommitsOn(MissionOutcome.Lost),
                $"only a won mission commits its capture to the log");
            profile.PersistLog.Merge(chapter, earlier.Seq, captured);
            store.Save(profile);
        });

        // A second store instance over the same directory stands in for the process restart the
        // original's log survives; the world below is built from the bootstrap, as every CSVM
        // session is, so anything destroyed in it came from the log and from nothing else.
        var reloaded = new CampaignProfileStore(profileDir).Load("Zachary");
        ctx.Check(reloaded != null, $"the profile round-trips the log through its file");
        ctx.Same(profile.PersistLog.Count, reloaded?.PersistLog.Count ?? -1, $"log entries after the reload");

        ctx.WithWorld(ctx.Chapter, collision: false, later.MissionFolder, world =>
        {
            var registry = world.Runtime.Destructibles;
            var nodes = AllNodes(registry);
            int present = 0;
            foreach (int node in carried)
            {
                if (!nodes.TryGetValue(node, out var anchor) || Live(registry, anchor) is not { } live)
                {
                    continue;
                }

                present++;
                ctx.Check(live.Status == DestructibleRegistry.State.Healthy,
                    $"the bootstrap alone leaves it intact node={node}");
            }

            ctx.Check(present > 0, $"the later mission carries at least one of the destroyed objects");
            // The original opens on the destroyed pose, so the replay must start no instance: no
            // fireball, debris or sound, and a later hit must not replay the death either. A
            // death's first start is synchronous, so the watch brackets only the calls.
            var started = new List<string>();
            var before = world.Runtime.OnInstanceStarted;
            world.Runtime.OnInstanceStarted = (def, anchor) => started.Add($"{def.AnimName}@{anchor?.Name}");
            int applied;
            try
            {
                applied = reloaded!.PersistLog.ApplyTo(world.Runtime, chapter, earlier.Seq);
                foreach (int node in carried)
                {
                    if (nodes.TryGetValue(node, out var anchor) && Live(registry, anchor) is { } live)
                    {
                        world.Runtime.DamageAt(anchor, live.MaxHealth + 1f);
                    }
                }
            }
            finally
            {
                world.Runtime.OnInstanceStarted = before;
            }

            report.AppendLine($"applied {applied} of {reloaded!.PersistLog.For(chapter).Count} in {later.MissionFolder}, started=[{string.Join(", ", started)}]");
            ctx.Same(present, applied, $"every carried object present in the later mission is applied");
            ctx.Check(started.Count == 0,
                $"the replay and a later hit on each carried object start no instance (started=[{string.Join(", ", started)}])");

            foreach (int node in carried)
            {
                if (nodes.TryGetValue(node, out var anchor) && Live(registry, anchor) is { } live)
                {
                    ctx.Check(live.Status == DestructibleRegistry.State.Destroyed && live.Health <= 0f,
                        $"it starts the later mission destroyed node={node}");
                }
            }

            if (saveOnly >= 0 && nodes.TryGetValue(saveOnly, out var control)
                && Live(registry, control) is { } controlLive)
            {
                ctx.Check(controlLive.Status == DestructibleRegistry.State.Healthy,
                    $"the save-only destructible starts the later mission intact node={saveOnly}");
            }

            ctx.Same(0, reloaded.PersistLog.For(chapter == 1 ? 2 : 1).Count,
                $"nothing leaks into another chapter's log");
            // The backwards walk never reaches the capturing mission itself, so re-flying it opens
            // on the world its own previous sortie never touched.
            var ownReplay = CampaignSequence.PreviousInSameChapter(missions, earlier.Seq)?.Seq;
            ctx.Same(0, reloaded.PersistLog.Through(chapter, ownReplay).Count,
                $"re-flying {earlier.MissionFolder} carries none of its own wreckage (through seq {ownReplay?.ToString(CultureInfo.InvariantCulture) ?? "none"})");
        });

        ctx.WriteArtifact($"test-campaign-persistence-{ctx.Chapter}.txt", report.ToString());
        ctx.Note($"carried {carried.Count} persisted objects across {earlier.MissionFolder} -> {later.MissionFolder}");
    }

    /// <summary>The persist log over the chapter's chain-killed scenery: C3's suspension bridge
    /// dies from the fuel truck's <c>CALL_ANIMATION</c> rather than from a hit on itself, and the
    /// pool the world registers for it is the compiled twin, whose record carries no
    /// <c>PERSIST_LOG</c> of its own (docs/formats/anim-definitions.md).</summary>
    [Suite("persist-chain-kill",
        "C3/M01's fuel-truck chain reaction kills the suspension bridge, whose PERSIST_LOG is "
        + "authored only in the chapter reader def while the compiled twin is the pool the world "
        + "registers; the capture holds the dropped bridge alongside the chain's own trucks, and a "
        + "log carrying it opens C3/M05 with the deck down and dbase still standing, while the "
        + "same world built from the bootstrap alone has it whole")]
    internal static void PersistChainKill(TestContext ctx)
    {
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        if (MissionAt(missions, BridgeChapter, BridgeMission) is not { } dropped
            || MissionAt(missions, BridgeChapter, BridgeLaterMission) is not { } later)
        {
            throw new SuiteSkippedException(
                $"{BridgeChapter}/{BridgeMission} and /{BridgeLaterMission} are not both campaign missions");
        }

        int chapter = dropped.Campaign;
        var report = new StringBuilder();
        report.AppendLine($"{BridgeChapter} chapter {chapter}: {BridgeMission} seq {dropped.Seq} -> {BridgeLaterMission} seq {later.Seq}");
        ctx.Same(dropped.Seq, CampaignSequence.PreviousInSameChapter(missions, later.Seq)?.Seq ?? -1,
            $"{BridgeLaterMission} opens on what {BridgeMission} left");

        var log = new CampaignPersistLog();
        int bridgeNode = -1;
        ctx.WithWorld(BridgeChapter, collision: false, BridgeMission, world =>
        {
            var runtime = world.Runtime;
            var registry = runtime.Destructibles;
            var bridge = PoolNamed(registry, BridgeDef);
            var truck = PoolNamed(registry, BridgeTruckDef);
            ctx.Check(bridge != null && truck != null,
                $"{BridgeChapter}/{BridgeMission} ships both pools bridge={bridge?.Def.Name ?? "-"} truck={truck?.Def.Name ?? "-"}");
            if (bridge is not { } span || truck is not { } fuel)
            {
                return;
            }

            bridgeNode = NodeIndex(span.Anchor);
            report.AppendLine($"bridge def={span.Def.Name}/{span.Def.AnimName} archive={span.Def.Archive != null} persist={span.Def.PersistLog} node={bridgeNode} hp={span.MaxHealth:0.##}");
            ctx.Check(span.Def.Archive != null,
                $"the pool the world registers for the bridge is the compiled twin, the one a hit resolves to");
            ctx.Check(span.Def.PersistLog,
                $"…and it carries the PERSIST_LOG its chapter reader def authors");
            ctx.Check(bridgeNode >= 0, $"the bridge anchor carries a gamez node index node={bridgeNode}");
            ctx.Same(0, CampaignPersistLog.Capture(runtime).Count(s => s.Node == bridgeNode),
                $"the standing bridge is in no capture");

            // Two seconds: `chainreaction` staggers its calls on EVENT_OFFSET, and the bridge's
            // sits several steps down that list.
            runtime.DamageAt(fuel.Anchor, fuel.MaxHealth + 1f);
            for (int i = 0; i < 120; i++)
            {
                runtime.Advance(1f / 60f);
            }

            ctx.Check(span.Status == DestructibleRegistry.State.Destroyed && span.Health <= 0f,
                $"the chain drops the bridge with no hit on it status={span.Status} hp={span.Health:0.##}");
            var captured = CampaignPersistLog.Capture(runtime);
            foreach (var state in captured)
            {
                report.AppendLine($"captured node={state.Node} def={state.Def} name={state.NodeName} destroyed={state.Destroyed} hp={state.Health:0.##}");
            }

            ctx.Check(captured.Any(s => s.Node == bridgeNode && s.Destroyed),
                $"the capture holds the dropped bridge node={bridgeNode} ({captured.Count} state(s) captured)");
            // The control the same chain reaction kills: a wildcard reader def registers these,
            // so they were carried before the bridge was and separate the flag from the kill path.
            ctx.Check(captured.Count(s => s.Def.Equals(ChainTruckDef, System.StringComparison.OrdinalIgnoreCase)
                    && s.Destroyed) >= 2,
                $"…and the chain's own {ChainTruckDef} trucks, which a reader def registers");
            log.Merge(chapter, dropped.Seq, captured);
        });

        if (bridgeNode < 0)
        {
            return;
        }

        ctx.WithWorld(BridgeChapter, collision: false, BridgeLaterMission, world =>
        {
            var runtime = world.Runtime;
            var bridge = PoolNamed(runtime.Destructibles, BridgeDef);
            ctx.Check(bridge != null, $"{BridgeChapter}/{BridgeLaterMission} builds the bridge too");
            if (bridge is not { } span)
            {
                return;
            }

            ctx.Same(bridgeNode, NodeIndex(span.Anchor),
                $"the same gamez node index keys it in {BridgeLaterMission}");
            ctx.Check(span.Status == DestructibleRegistry.State.Healthy,
                $"the bootstrap alone opens {BridgeLaterMission} with the bridge whole status={span.Status}");

            int Down(string piece) => runtime.FindNodes(piece, span.Anchor).Count(n => !n.Visible);
            int applied = log.ApplyTo(runtime, chapter, dropped.Seq);
            report.AppendLine($"applied {applied} of {log.For(chapter).Count} in {BridgeLaterMission}, rope1 down={Down("rope1")} part5 down={Down("part5")}");
            ctx.Check(applied > 0, $"the carried log reaches {BridgeLaterMission} applied={applied}");
            ctx.Check(span.Status == DestructibleRegistry.State.Destroyed && span.Health <= 0f,
                $"…and opens it with the bridge down status={span.Status} hp={span.Health:0.##}");
            ctx.Check(Down("rope1") >= 1 && Down("part5") >= 1,
                $"the deck takes the dropped pose rope1 down={Down("rope1")} part5 down={Down("part5")}");
            ctx.Check(runtime.FindNodes("dbase", span.Anchor) is { Count: > 0 } plinth
                && plinth.All(n => n.Visible),
                $"…with dbase still standing under the wreck");
        });

        ctx.WriteArtifact("test-persist-chain-kill.txt", report.ToString());
        ctx.Note($"carried the chain-killed bridge node={bridgeNode} from {BridgeMission} into {BridgeLaterMission}");
    }

    /// <summary>Drives one shipped mission's objectives graph headless against a scripted world:
    /// the wake timings, the chains, the target-list edits, the display rows, and both endings the
    /// script authors. No world is built, so this suite costs nothing but the parse.</summary>
    [Suite("campaign-objectives",
        "the objectives runtime (D31) over a shipped mission's own choreography, headless: the "
        + "BEGIN_DORMANT wake timings and their sound/turret actions, the primary completing "
        + "off an INACTIVEn node, its KILL/WAKE/NAP chains and target-list edits, the display "
        + "rows and the mask bit their IDENTITY priority names (bit 0 staying clear, since it is "
        + "the win flag the director lays on), plus BOTH endings the script authors, the INSTANTWIN "
        + "path and the 300 s reminder fuse that naps the INSTANTLOSS objective")]
    internal static void CampaignObjectives(TestContext ctx)
    {
        string zrdr = SessionPaths.MissionZrdr(ctx.DataRoot, GraphChapter, GraphMission);
        ctx.RequireData(zrdr, $"{GraphChapter}/{GraphMission} zrdr");
        var script = ObjectiveScript.Load(zrdr);
        var report = new StringBuilder();
        report.AppendLine($"{GraphChapter}/{GraphMission}: {script.Objectives.Count} objectives");
        ctx.Same(50, script.Objectives.Count, $"the shipped {GraphChapter}/{GraphMission} objective count");

        var world = new ScriptedWorld();
        var graph = new ObjectiveGraph(script, world);
        var woke = new List<int>();
        var done = new List<int>();
        graph.Woke += e => woke.Add(e.Number);
        graph.Completed += e => done.Add(e.Number);

        ctx.Same(5, graph.Rows.Count, $"one display row per unique IDENTITY priority");
        ctx.Same(1, graph.Rows[0].Priority, $"the lowest priority sorts first, and is the primary");
        ctx.Same(11, graph.Rows[^1].Priority, $"the secondary's priority is the last row");
        ctx.Check(graph.StateOf(1) == ObjectiveState.Dormant, $"OBJECTIVE1 begins dormant");
        ctx.Check(graph.StateOf(2) == ObjectiveState.Awake, $"OBJECTIVE2's null body begins awake");

        Advance(graph, 3f);
        ctx.Check(done.Contains(2), $"the null block completes as a no-op on its first eligible tick");
        ctx.Check(woke.Contains(1), $"OBJECTIVE1 wakes at its BEGIN_DORMANT 2 s");
        ctx.Check(world.SoundGroups.Contains("snd_NW2Start"), $"its WAKEUP_SOUND_GROUP fired");
        ctx.Check(world.TurretPatterns.Contains("aagun**"), $"its WAKEUP_TURRETS pattern reached the world");
        Advance(graph, 3f);
        ctx.Check(woke.IndexOf(48) > woke.IndexOf(1), $"OBJECTIVE1's completion woke OBJECTIVE48 after it");
        ctx.Check(world.SoundGroups.Contains("music_prebattle_sg"),
            $"the woken cue's COMPLETED_SOUND_GROUP carries the music group D37 reads");

        // The primary: the pickup going inactive is what completes it, and its completion is where
        // this mission's chaining, target edits and net reassignments all land.
        world.Inactive.Add("pickup_objective");
        Advance(graph, 6f);
        ctx.Check(done.Contains(3), $"the primary completes when its INACTIVE node loses its active bit");
        ctx.Same(4, world.AiNets.Count, $"its four SET_AI_NET entries reached the world");
        ctx.Check(graph.IsOtherTarget("ftank01"), $"ADD_OTHER_TARGET added the fuel tank");
        ctx.Check(!graph.IsObjectiveTarget("caboose_polys"), $"REMOVE_OBJECTIVE_TARGET dropped the caboose");
        ctx.Check(graph.Rows[0].Completed, $"the primary's display row is marked");
        ctx.Same(1 << graph.Rows[0].Priority, graph.CompletedMask & (1 << graph.Rows[0].Priority),
            $"the recorded mask carries that primary at bit {graph.Rows[0].Priority}, its IDENTITY priority rather than its row position");
        ctx.Same(0, graph.CompletedMask & CampaignProgression.PrimaryObjectiveMask,
            $"and bit 0 stays clear in the graph's own mask: it is the win flag the director lays on");
        for (int n = 19; n <= 24; n++)
        {
            ctx.Check(!graph.AliveOf(n), $"the primary's KILL list retired the reminder loop OBJECTIVE{n}");
        }

        ctx.Check(done.Contains(4), $"its WAKE list ran OBJECTIVE4, which is conditionless");
        ctx.Check(graph.StateOf(6) != ObjectiveState.Dormant, $"its NAP list moved OBJECTIVE6 out of dormancy");
        Advance(graph, 8f);
        ctx.Check(done.Contains(6), $"the napped objective woke on its own timer and completed");
        report.AppendLine($"win-path run: {done.Count} completions, {woke.Count} wakes, mask 0x{graph.CompletedMask:x}");

        LossFuse(ctx, script, report);
        NapClearsCompletion(ctx);
        ctx.WriteArtifact("test-campaign-objectives.txt", report.ToString());
        ctx.Note($"drove {GraphChapter}/{GraphMission}'s {script.Objectives.Count}-objective graph to both endings");
    }

    /// <summary>The mission-end flow against a built world: a scripted kill drives an
    /// <c>INACTIVEn</c> condition off real node state, the graph's own end is what ends the
    /// mission, and the result reaches the profile through <see cref="CampaignProgression"/> with
    /// the destruction log captured and the return-to-cabin exit raised.</summary>
    [Suite("campaign-mission-end",
        "the campaign mission-end flow against a BUILT world (D31): a scripted kill drives an "
        + "INACTIVEn condition off real node state, the graph's own end ends the mission, and "
        + "the result reaches the profile through CampaignProgression with the destruction log "
        + "captured and the return-to-cabin exit raised; plus the three paths the mission-end fade "
        + "can be reached by, since it runs over whatever frame was last on screen: a win raised by "
        + "a docking film's own completion code and a loss landing under a film both keep the "
        + "film's shot for the whole leaving hold, while a win in free flight fades the pilot's own "
        + "view")]
    internal static void CampaignMissionEnd(TestContext ctx)
    {
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        if (FirstMissionOf(missions, ctx.Chapter) is not { } mission)
        {
            throw new SuiteSkippedException($"chapter {ctx.Chapter} holds no campaign mission");
        }

        var script = ObjectiveScript.Load(
            SessionPaths.MissionZrdr(ctx.DataRoot, mission.ChapterFolder, mission.MissionFolder));
        if (script.Objectives.Count == 0)
        {
            throw new SuiteSkippedException($"{mission.ChapterFolder}/{mission.MissionFolder} authors no objectives");
        }

        var profile = CampaignProfileDef.NewProfile("Zachary");
        var director = CampaignDirector.Create(script, mission, profile, null);
        var report = new StringBuilder();
        report.AppendLine($"{mission.ChapterFolder}/{mission.MissionFolder} seq={mission.Seq}: "
            + $"{script.Objectives.Count} objectives");
        ctx.WithWorld(ctx.Chapter, collision: false, mission.MissionFolder, world =>
        {
            director.Attach(new CampaignDirector.WorldInputs { Runtime = world.Runtime });
            var graph = director.Graph!;
            ctx.Check(graph.Count == script.Objectives.Count, $"every objective is armed");
            ctx.Check(graph.Rows.Count > 0, $"the mission has a player-visible objectives display");

            int completed = DriveOneInactive(ctx, world, script, graph, report);
            ctx.Check(completed > 0, $"a scripted kill drove an INACTIVEn condition off real node state");

            int winner = EndObjective(script);
            ctx.Check(winner > 0, $"the mission authors an INSTANTWIN objective");
            graph.Wake(winner);
            Advance(graph, 2f);
            ctx.Check(director.Result != null, $"the mission ended and banked its result");
            ctx.Check(!director.ReturnToCabin,
                $"but has not left the world yet: the leaving hold runs first");
            ctx.Check(Mathf.Abs(director.LeavingFade - 0f) < 0.01f,
                $"the mission-end fade starts at 0 the frame the ending lands, fade={director.LeavingFade:0.###}");
            Advance(director, 1f);
            ctx.Check(Mathf.Abs(director.LeavingFade - 0.5f) < 0.02f,
                $"a second into the two-second hold the fade sits halfway to black, fade={director.LeavingFade:0.###}");
            Advance(director, CampaignDirector.LeavingHoldS - 1f + 0.2f);
            ctx.Check(director.ReturnToCabin, $"the mission end raised the return-to-cabin exit");
            ctx.Check(Mathf.Abs(director.LeavingFade - 1f) < 0.01f,
                $"and the hold's own two seconds landed the fade fully black, so the scrapbook opens on it");
            var result = director.Result!.Value;
            ctx.Check(result.Outcome == MissionOutcome.Won, $"the outcome is the graph's own");
            ctx.Same(graph.CompletedMask | CampaignProgression.PrimaryObjectiveMask, result.Attempt.CompletedMask,
                $"the recorded mask is the graph's completed priorities under the win flag at bit 0");
            ctx.Check(CampaignProgression.ResultOf(profile, mission.Seq) != null,
                $"the attempt reached the profile's mission record");
            bool primary = (result.Attempt.CompletedMask & CampaignProgression.PrimaryObjectiveMask) != 0;
            ctx.Same(primary ? mission.Seq + 1 : 0, profile.MissionsCompleted,
                $"the position advances only on a completed primary (primary={primary})");
            report.AppendLine($"ended {result.Outcome}, mask 0x{result.Attempt.CompletedMask:x}, "
                + $"{result.Attempt.TimeMs} ms, persist log {profile.PersistLog.Count}");
        });

        ctx.WithWorld(ctx.Chapter, collision: false, mission.MissionFolder,
            world => CampaignMissionLossKeepsObjectiveBits(ctx, world, script, mission, report));

        EndingFadeRunsOverTheLastFrame(ctx, script, mission, report);
        ctx.WriteArtifact($"test-campaign-mission-end-{ctx.Chapter}.txt", report.ToString());
        ctx.Note($"flew {mission.ChapterFolder}/{mission.MissionFolder} to a graph-derived end");
    }

    /// <summary>A co-op sortie's <c>Shots</c>/<c>Hits</c> are the seated pilot's alone. Two
    /// human rigs each fire a real cannon round at the other's aircraft; only the seated pilot's
    /// (rig 0's) round reaches the recorded attempt, proving <c>WireScoredShooter</c> gates on the
    /// scripted player and never a guest. Money is the mission's, not the field's: the sortie banks
    /// what the reward table owes for the mission flown and the mask reached, once, however many
    /// humans were up.</summary>
    [Suite("campaign-coop-attempt",
        "what a co-op sortie writes to the seated profile: two human rigs each fire a real "
        + "cannon round at the other's aircraft, and only the seated pilot's reaches the recorded "
        + "attempt's Shots/Hits, WireScoredShooter gating on the scripted player and never a "
        + "guest; the money banked is the mission reward table's answer for this mission and mask, "
        + "taken once rather than once per human")]
    internal static void CampaignCoopAttempt(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        if (FirstMissionOf(missions, ctx.Chapter) is not { } mission)
        {
            throw new SuiteSkippedException($"chapter {ctx.Chapter} holds no campaign mission");
        }

        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, mission.ChapterFolder, mission.MissionFolder);
        ctx.RequireData(missionZrdr, $"{mission.ChapterFolder}/{mission.MissionFolder} zrdr");
        var script = ObjectiveScript.Load(missionZrdr);
        int winner = EndObjective(script);
        if (winner == 0)
        {
            throw new SuiteSkippedException(
                $"{mission.ChapterFolder}/{mission.MissionFolder} authors no INSTANTWIN objective");
        }

        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, mission.ChapterFolder);
        ctx.RequireData(texturesPath, $"{mission.ChapterFolder} textures");

        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weaponDefs = Flight.Weapons.WeaponDefs.Load(ctx.ZrdrPath, null);
        var cannon = weaponDefs.All.FirstOrDefault(w => w.IsCannon && w.ArmorDamage is > 0f);
        ctx.Check(cannon != null, $"a CANNON gun exists in the data");
        if (cannon == null)
        {
            textures.Dispose();
            return;
        }

        var seatedPos = new Vector3(0f, 800f, 0f);
        var guestPos = new Vector3(0f, 800f, -2000f);
        var targetPos = new Vector3(500f, 800f, 0f);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var director = CampaignDirector.Create(script, mission, profile, null);
        Flight.Weapons.ProjectilePool? pool = null;
        Flight.Airframe.FlightController? seated = null;
        Flight.Airframe.FlightController? guest = null;
        Flight.Airframe.FlightController? target = null;
        var report = new StringBuilder();
        try
        {
            var live = new Flight.Weapons.ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            seated = BuildAttemptRig(ctx, planesGamez, textures, live, 0, seatedPos);
            guest = BuildAttemptRig(ctx, planesGamez, textures, live, 1, guestPos);
            // Use an enemy target because friendly fire is disabled between
            // co-op humans, so a round aimed at the OTHER human would never register a hit.
            target = BuildAttemptRig(ctx, planesGamez, textures, live, 2, targetPos, Flight.Weapons.AimAssist.PlayerTeam + 1);
            var field = new List<Flight.Airframe.FlightController> { seated, guest };

            director.Attach(new CampaignDirector.WorldInputs
            {
                Projectiles = live,
                ListenerPosition = () => seatedPos,
                PlayerAircraft = () => seated,
                Humans = () => field,
            });
            director.Step(0.1f);
            ctx.Check(live.ScoredShooters.Contains(seated.PlayerIndex),
                $"the seated pilot's aircraft is wired into ScoredShooters on the first step");
            ctx.Check(!live.ScoredShooters.Contains(guest.PlayerIndex),
                $"...and the guest's never is");

            void FireAt(Flight.Airframe.FlightController shooter, Vector3 at)
            {
                var muzzle = new Transform3D(
                    Basis.LookingAt(Vector3.Back, Vector3.Up), at + new Vector3(0f, 0f, -20f));
                live.Spawn(cannon, muzzle, Vector3.Zero, shooterId: shooter.PlayerIndex);
                for (int i = 0; i < 20; i++)
                {
                    live.SimStep(1f / 60f);
                }
            }

            FireAt(guest, targetPos);
            ctx.Check(live.CannonRoundsFired == 0 && live.CannonHits == 0,
                $"a guest's cannon round moves neither counter: fired={live.CannonRoundsFired} hits={live.CannonHits}");

            FireAt(seated, targetPos);
            ctx.Check(live.CannonRoundsFired == 1 && live.CannonHits == 1,
                $"the seated pilot's own round counts as both fired and hit: fired={live.CannonRoundsFired} hits={live.CannonHits}");

            director.Graph!.Wake(winner);
            Advance(director, 2f);
            ctx.Check(director.Result != null, $"the mission ended and banked its result");
            var attempt = director.Result!.Value.Attempt;
            ctx.Same(1, attempt.Shots, $"the recorded attempt carries the seated pilot's shot count alone");
            ctx.Same(1, attempt.Hits, $"...and hit count alone, never the guest's");

            // Money is the mission's own, not the field's: a reward is per objective and banked
            // once whatever the headcount. Computed from the table rather than hard-coded, since
            // which mission this suite flies follows --chapter.
            int owed = ExpectedReward(mission.Seq, attempt.CompletedMask);
            int paid = director.Result!.Value.Recorded.MoneyPaid;
            ctx.Same(owed, paid,
                $"the sortie banks the mission's own reward, not one per human: expected ${owed} for mask 0x{attempt.CompletedMask:x}");

            report.AppendLine($"{mission.ChapterFolder}/{mission.MissionFolder}: seated shots="
                + $"{attempt.Shots} hits={attempt.Hits} money=${paid} banked once, guest's cannon round "
                + $"never reached ScoredShooters");
        }
        finally
        {
            pool?.Free();
            seated?.Free();
            guest?.Free();
            target?.Free();
            textures.Dispose();
        }

        ctx.WriteArtifact(
            $"test-campaign-coop-attempt-{mission.ChapterFolder}-{mission.MissionFolder}.txt", report.ToString());
        ctx.Note($"{mission.ChapterFolder}/{mission.MissionFolder}: a co-op sortie's Shots/Hits stay the seated pilot's alone");
    }

    /// <summary>BL-645: the mission reward table's cash half, end to end on the one mission whose
    /// reward the shipped data can reach without flying it. Two human rigs are up, and the payout
    /// is the mission's own, taken once: a squadron is paid what the objective pays, not that
    /// times the headcount. Flying the same mission again banks nothing, which is the gate on the
    /// mask as it stood before the run. The mission and its paying bit are named on the
    /// <c>[Suite]</c> description below.</summary>
    [Suite("campaign-mission-cash",
        "BL-645's mission reward cash on C3/M01 (ordinal 1, $900 on bit 12) with two humans up: "
        + "waking OBJECTIVE30 and its dependent SECONDARY 12 completes the paying objective for "
        + "real, the recorded mask carries bit 12 as a priority and not a row index, the sortie "
        + "banks $900 once rather than once per human, the wallet and the mission's Cash Earned "
        + "both read it, and a replay of the same objective banks nothing")]
    internal static void CampaignMissionCash(TestContext ctx)
    {
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        if (MissionAt(missions, CashChapter, CashMission) is not { } mission)
        {
            throw new SuiteSkippedException($"{CashChapter}/{CashMission} is not in cm_sequence");
        }

        var reward = FirstReward(mission.Seq);
        if (reward is not { Cash: > 0 })
        {
            throw new SuiteSkippedException($"{CashChapter}/{CashMission} carries no paying reward record");
        }

        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, mission.ChapterFolder, mission.MissionFolder);
        ctx.RequireData(missionZrdr, $"{CashChapter}/{CashMission} zrdr");
        var script = ObjectiveScript.Load(missionZrdr);

        // The paying objective is found by its own IDENTITY priority, which is what the reward
        // record names. Its gate objective comes off the script too, so neither is a magic number.
        var paying = script.Objectives.Find(d => d.Identity?.Priority == reward.Value.ObjectiveBit);
        ctx.Check(paying != null, $"the mission authors an IDENTITY at priority {reward.Value.ObjectiveBit}, the record's bit");
        int winner = EndObjective(script);
        ctx.Check(winner > 0, $"{CashChapter}/{CashMission} authors an INSTANTWIN objective");
        if (paying == null || winner == 0)
        {
            return;
        }

        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, mission.ChapterFolder);
        ctx.RequireData(texturesPath, $"{CashChapter} textures");
        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var seatedPos = new Vector3(0f, 800f, 0f);
        var guestPos = new Vector3(0f, 800f, -2000f);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var report = new StringBuilder();
        Flight.Weapons.ProjectilePool? pool = null;
        Flight.Airframe.FlightController? seated = null;
        Flight.Airframe.FlightController? guest = null;
        try
        {
            var live = new Flight.Weapons.ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            seated = BuildAttemptRig(ctx, planesGamez, textures, live, 0, seatedPos);
            guest = BuildAttemptRig(ctx, planesGamez, textures, live, 1, guestPos);
            var field = new List<Flight.Airframe.FlightController> { seated, guest };

            int paid = FlyForTheReward(ctx, script, mission, profile, live, seated, field, paying.Number, winner);
            ctx.Same(reward.Value.Cash, paid,
                $"two humans up and the mission banks its own ${reward.Value.Cash}, once, not once per human");
            ctx.Same(reward.Value.Cash, profile.Funds, $"which is what reached the wallet the hangar spends");
            var result = CampaignProgression.ResultOf(profile, mission.Seq);
            ctx.Same(reward.Value.Cash, result!.Best.Money, $"and the mission's own Cash Earned, which the scrapbook prints");

            // The same sortie again: the paying bit is in the profile's mask now, so the gate holds.
            int again = FlyForTheReward(ctx, script, mission, profile, live, seated, field, paying.Number, winner);
            ctx.Same(0, again, $"flying the same paying objective again banks nothing: each bonus is once per profile");
            ctx.Same(reward.Value.Cash, profile.Funds, $"so the wallet still holds one payout, not two");

            report.AppendLine($"{CashChapter}/{CashMission} ordinal {mission.Seq + 1}: OBJECTIVE{paying.Number} "
                + $"is IDENTITY priority {reward.Value.ObjectiveBit}, completing it with 2 humans up banked "
                + $"${paid} once; replay banked ${again}; funds ${profile.Funds}");
        }
        finally
        {
            pool?.Free();
            seated?.Free();
            guest?.Free();
            textures.Dispose();
        }

        ctx.WriteArtifact($"test-campaign-mission-cash-{CashChapter}-{CashMission}.txt", report.ToString());
        ctx.Note($"{CashChapter}/{CashMission}: a two-human sortie banks the mission's own reward, once");
    }

    /// <summary>BL-458: a campaign mission's own <c>dzpathN</c> gates, resolved against real
    /// world geometry, drive the exact objectives <c>objectives.zrd</c> gates on them. Proved
    /// over C3/M01's shipped SECONDARY (OBJECTIVE3, <c>dzpath1</c>) and OBJECTIVE11
    /// (<c>dzpath4</c>): both gate-crossing tests and completion through the director's real
    /// <c>NotifyDangerZoneCompleted</c> path.</summary>
    [Suite("campaign-danger-zones",
        "BL-458's campaign danger zones over C3/M01's own dzpathN gates, resolved against real "
        + "world geometry: the mission's DANGER_ZONES_COMPLETED names (dzpath1, dzpath4) are "
        + "armed and no others, a scripted crossing of both authored gates fires each zone's "
        + "completion, and the director's real NotifyDangerZoneCompleted path completes the "
        + "SECONDARY (OBJECTIVE3) and OBJECTIVE11 the way a flown mission would")]
    internal static void CampaignDangerZoneObjectives(TestContext ctx)
    {
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        if (MissionAt(missions, DangerZoneChapter, DangerZoneMission) is not { } mission)
        {
            throw new SuiteSkippedException($"{DangerZoneChapter}/{DangerZoneMission} is not in cm_sequence");
        }

        string missionZrdrPath = SessionPaths.MissionZrdr(ctx.DataRoot, mission.ChapterFolder, mission.MissionFolder);
        var script = ObjectiveScript.Load(missionZrdrPath);
        var secondary = script.Objectives.Find(d => d.DangerZones.Contains("dzpath1"));
        var worker = script.Objectives.Find(d => d.DangerZones.Contains("dzpath4"));
        ctx.Check(secondary?.Identity?.Class == ObjectiveClass.Secondary,
            $"OBJECTIVE{secondary?.Number} carries the mission's SECONDARY IDENTITY and gates on dzpath1");
        ctx.Check(worker != null, $"a second objective gates on dzpath4");
        if (secondary == null || worker == null)
        {
            return;
        }

        var report = new StringBuilder();
        report.AppendLine($"{DangerZoneChapter}/{DangerZoneMission}: SECONDARY OBJECTIVE{secondary.Number} "
            + $"on dzpath1, OBJECTIVE{worker.Number} on dzpath4");

        ctx.WithWorld(DangerZoneChapter, collision: false, DangerZoneMission, world =>
        {
            var zones = CampaignDangerZones.Load(script, world.Gamez, missionZrdrPath);
            ctx.Check(zones != null, $"the chapter world carries real dzpath1/dzpath4 gate geometry");
            if (zones == null)
            {
                return;
            }
            ctx.Same(2, zones.Count,
                $"exactly the two dzpathN names objectives.zrd references are armed");

            var completed = new List<string>();
            foreach (var name in new[] { "dzpath1", "dzpath4" })
            {
                // A fresh tracker per zone: Update tests every armed gate on every call, so one
                // shared instance's long "prime" jump between two zones' probe points can cross
                // the OTHER zone's gate plane too, a test-construction risk, not an engine one.
                var solo = CampaignDangerZones.Load(script, world.Gamez, missionZrdrPath)!;
                ctx.Check(solo.TryGateProbe(name, out var gc, out var gn, out var rc, out var rn),
                    $"'{name}' resolved a green/red gate pair");
                var half = new List<string>();
                solo.Update(gc - gn * 5f, half.Add);
                solo.Update(gc + gn * 5f, half.Add);
                // Some authored pairs sit only metres apart (a "thin slit" aperture), so crossing
                // green can carry this probe across red's plane too, completion, not a false one.
                solo.Update(rc - rn * 5f, half.Add);
                solo.Update(rc + rn * 5f, half.Add);
                ctx.Check(half.Contains(name), $"'{name}' completed once both authored gates were crossed");
                completed.AddRange(half);
            }
            report.AppendLine($"gate crossing completed: {string.Join(", ", completed)}");

            var profile = CampaignProfileDef.NewProfile("Zachary");
            var director = CampaignDirector.Create(script, mission, profile, null, missionZrdrPath);
            director.Attach(new CampaignDirector.WorldInputs { Runtime = world.Runtime, Gamez = world.Gamez });
            ctx.Same(zones.Count, director.ArmedDangerZones,
                $"Attach arms the same gate count off a real Gamez, the wiring BL-458 was missing");

            var graph = director.Graph!;
            graph.Wake(secondary.Number);
            graph.Wake(worker.Number);
            StepDirector(director, 0.1f);
            ctx.Check(!graph.CompletedOf(secondary.Number), $"the SECONDARY has not completed before any zone notify");

            // ScanForCompletion resolves one objective per tick, round robin (ObjectiveGraph.cs):
            // enough steps to cycle past every one of the mission's 39 armed objectives, not one.
            director.NotifyDangerZoneCompleted("dzpath1");
            StepDirector(director, 5f);
            ctx.Check(graph.CompletedOf(secondary.Number),
                $"OBJECTIVE{secondary.Number} (the SECONDARY) completes off the notify path BL-458 wired");

            director.NotifyDangerZoneCompleted("dzpath4");
            StepDirector(director, 5f);
            ctx.Check(graph.CompletedOf(worker.Number), $"OBJECTIVE{worker.Number} completes the same way");
            report.AppendLine($"OBJECTIVE{secondary.Number} and OBJECTIVE{worker.Number} completed via NotifyDangerZoneCompleted");
        });

        ctx.WriteArtifact("test-campaign-danger-zones.txt", report.ToString());
        ctx.Note($"{DangerZoneChapter}/{DangerZoneMission}'s SECONDARY completed through its own dzpathN gates");
    }

    /// <summary>The cutscene host over the shipped intro definition: the codes the definition
    /// raises reach it through the runtime's own dispatch, the world and the objectives update
    /// stop while it holds them, and the handoff puts every piece of session state back.</summary>
    [Suite("campaign-cutscene",
        "the cutscene host over C1/M04's shipped intro definition (D32): its authored callback "
        + "codes reach the host through the runtime's own dispatch, the world and the "
        + "objectives update stop while callback 20 holds them, the vehicle-death codes are "
        + "declined, and the definition's end hands off with every piece of cutscene state put "
        + "back; and C3/M03's opening scene, which no start list names, is reached through its "
        + "start anim's call: the world build stages its camera and player marker, the host "
        + "takes its codes, the destruction runs under it once, and the skip it arms ends it; "
        + "plus what an episode owes a seat that entered it from the cockpit view, at two seats "
        + "and over both exits: the airframe drawn and the interior pass off while the "
        + "presentation holds, both back on the hand-back, and neither seat's selected view "
        + "moved to get there (BL-625)")]
    internal static void CampaignCutscene(TestContext ctx)
    {
        var host = new CutsceneController();
        var held = new List<bool>();
        host.WorldHeld = h => held.Add(h);
        ctx.Host.AddChild(host);
        ctx.WithWorld(IntroChapter, collision: false, IntroMission, world =>
        {
            var stage = new Node3D { Name = "CutsceneStage" };
            var runtime = new AnimRuntime { AutoStart = false, ManualAdvance = true, SoundHandledElsewhere = true };
            runtime.CallbackHost = host.Host;
            ctx.Host.AddChild(stage);
            ctx.Host.AddChild(runtime);
            try
            {
                runtime.Bind(stage, world.Session.Program.Subset(IntroAnim));
                host.BindWorld(runtime);
                ctx.Check(!host.Playing, $"nothing owns the session until a definition raises a code");
                runtime.Play(IntroAnim);

                ctx.Check(host.Playing && host.Anim == IntroAnim,
                    $"'{IntroAnim}' took the session through the runtime's own CALLBACK dispatch");
                ctx.Same(20, host.Codes.Count > 0 ? host.Codes[0] : 0,
                    $"the movie state opens by holding the world");
                foreach (int code in MovieCodes)
                {
                    ctx.Check(host.Codes.Contains(code), $"the definition's authored callback {code} was hosted");
                }

                ctx.Check(host.HoldsWorld && host.Presenting && host.OutOfFlight && host.AiParked,
                    $"the world is held, the chrome is off, the player is out of flight and the AI is parked");
                ctx.Check(held.Count == 1 && held[0], $"the world hold reached the session once");
                ctx.Check(!host.Host(15, IntroAnim) && !host.Host(16, IntroAnim),
                    $"the vehicle-death codes are declined, so they keep the seams they had");

                // The definition still running is not a handoff; its end is.
                host.Tick();
                ctx.Check(host.Playing, $"a tick while the definition runs holds the cutscene");
                runtime.Stop(IntroAnim);
                host.Tick();
                ctx.Check(!host.Playing && !host.HoldsWorld && !host.Presenting && !host.OutOfFlight
                          && !host.AiParked && !host.CamParamsFree,
                    $"the definition ending hands off: every piece of cutscene state is back");
                ctx.Check(held.Count == 2 && !held[1], $"and the world hold was released");
                foreach (int code in RestoreCodes)
                {
                    ctx.Check(host.Codes.Contains(code),
                        $"the handoff raised the gameplay state the definition's RESET_STATE asserts ({code})");
                }
            }
            finally
            {
                runtime.Free();
                stage.Free();
            }
        });

        CutsceneHoldsObjectives(ctx);
        OpeningSceneCalledFromStartAnim(ctx);
        PresentingFramesTheAirframe(ctx);
        ctx.Note($"hosted {IntroChapter}/{IntroMission}'s '{IntroAnim}' from its first code to the handoff, and {CalledChapter}/{CalledMission}'s '{CalledAnim}' from its start anim to the skip");
    }

    /// <summary>The bars are data: the shared <c>letterbox</c> definition switches the node on and
    /// copies the cutscene camera's whole frame onto it every tick, so they hold their place in the
    /// frame through any camera path.</summary>
    [Suite("cutscene-letterbox",
        "the letterbox bars are data (D32): the chapter ships the node switched off as its "
        + "definition's base state, calling the definition switches it on outright with no "
        + "reveal, and the AT_NODE re-assert copies the cutscene camera's whole frame onto it "
        + "every tick; the card's authored height frames the film on every pane ratio, so the "
        + "film's rect is the same band of a 4:3 window and a 32:9 one, with the bars stretched "
        + "sideways to cover the width a wider pane adds")]
    internal static void CutsceneLetterbox(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var gamezBars = world.Gamez.FindByName(CutsceneController.BarsNode);
            ctx.Check(gamezBars != null && !gamezBars.Active,
                $"the chapter ships a {CutsceneController.BarsNode} node, switched off as its definition's base state");

            var stage = new Node3D { Name = "LetterboxStage" };
            var camera = new Node3D { Name = CutsceneController.CameraNode };
            var bars = new Node3D { Name = CutsceneController.BarsNode };
            stage.AddChild(camera);
            stage.AddChild(bars);
            var runtime = new AnimRuntime { AutoStart = false, ManualAdvance = true, SoundHandledElsewhere = true };
            ctx.Host.AddChild(stage);
            ctx.Host.AddChild(runtime);
            try
            {
                runtime.Bind(stage, world.Session.Program.Subset(CutsceneController.BarsNode));
                ctx.Check(!bars.Visible, $"the bind applies the definition's INACTIVE base state");

                var pose = new Transform3D(
                    Basis.FromEuler(new Vector3(0.21f, 0.73f, 0f)), new Vector3(120f, 240f, -360f));
                camera.GlobalTransform = pose;
                runtime.Play(CutsceneController.BarsNode);
                ctx.Check(bars.Visible, $"calling the definition switches the bars on outright, with no reveal");
                ctx.Check(bars.GlobalPosition.IsEqualApprox(pose.Origin),
                    $"and pins them to the cutscene camera's position");
                ctx.Check(bars.GlobalBasis.Z.IsEqualApprox(pose.Basis.Z),
                    $"including its facing, which is what holds the bars square to the frame");

                // The re-assert loop is what keeps them there while the camera flies its splines.
                camera.GlobalTransform = new Transform3D(pose.Basis, new Vector3(900f, 30f, 40f));
                runtime.Advance(1f / 60f);
                ctx.Check(bars.GlobalPosition.IsEqualApprox(camera.GlobalPosition),
                    $"the LOOP re-assert follows the camera on the next tick");
            }
            finally
            {
                runtime.Free();
                stage.Free();
            }

        });

        LetterboxBarsRideTheFrameTheyClad(ctx);
        LetterboxCoversThePane(ctx);
        LetterboxCardIsUnoccludable(ctx);
        PresentingLowersTheWorldOverlays(ctx);
        ctx.Note($"the {ctx.Chapter} letterbox card tracks the cutscene camera by transform copy");
    }

    // BL-452's placement half. The definition's LOOP pin is asserted somewhere inside the runtime's
    // instance walk, which runs newest-first, so a definition that poses camera1 by a sequence
    // event moves the eye AFTER the letterbox instance has already pinned the bars to where it was.
    // The rig cameras take camera1's final pose, so the bars end up cladding last frame's frame and
    // the world shows along the trailing edge. Driven the way the frame really runs: advance, then
    // move camera1, then tick the host.
    private static void LetterboxBarsRideTheFrameTheyClad(TestContext ctx)
    {
        var host = new CutsceneController();
        var stage = new Node3D { Name = "LetterboxOrderStage" };
        var camera = new Node3D { Name = CutsceneController.CameraNode };
        var bars = new Node3D { Name = CutsceneController.BarsNode };
        stage.AddChild(camera);
        stage.AddChild(bars);
        var runtime = new AnimRuntime { AutoStart = false, ManualAdvance = true, SoundHandledElsewhere = true };
        ctx.Host.AddChild(host);
        ctx.Host.AddChild(stage);
        ctx.Host.AddChild(runtime);
        try
        {
            ctx.WithWorld(ctx.Chapter, collision: false, world =>
            {
                runtime.Bind(stage, world.Session.Program.Subset(CutsceneController.BarsNode));
                host.BindWorld(runtime);
                host.HostDefinitions(new[] { CutsceneController.BarsNode });
                var start = new Transform3D(
                    Basis.FromEuler(new Vector3(0.05f, 0.4f, 0f)), new Vector3(60f, 300f, -120f));
                camera.GlobalTransform = start;
                runtime.Play(CutsceneController.BarsNode);
                host.Host(CutsceneController.CodeHoldsWorld, CutsceneController.BarsNode);

                // A frame's worth of a cutscene camera swinging with the aircraft it rides: the
                // pin has already run for this frame, and only now does the eye move.
                runtime.Advance(1f / 60f);
                var moved = new Transform3D(
                    Basis.FromEuler(new Vector3(0.05f, 0.4f + 0.03f, 0f)), new Vector3(60.4f, 300f, -120.2f));
                camera.GlobalTransform = moved;
                host.Tick();

                ctx.Check(bars.GlobalPosition.IsEqualApprox(moved.Origin),
                    $"the bars take the eye's position at the instant the frame is taken, off by {bars.GlobalPosition.DistanceTo(moved.Origin):0.####} m");
                ctx.Check(bars.GlobalBasis.Z.IsEqualApprox(moved.Basis.Z),
                    $"and its facing, which keeps the card square to the frame, off by {Mathf.RadToDeg(bars.GlobalBasis.Z.AngleTo(moved.Basis.Z)):0.###} deg");
            });
        }
        finally
        {
            runtime.Free();
            stage.Free();
            host.Free();
        }
    }

    // The card covers the pane on every ratio, over the chapter's OWN card rather than a
    // written-down extent: the height fit, then the sideways stretch, checked edge by edge.
    // ⚠ The cutscene roots are an option, so this needs a world built the way a story mission's is.
    private static void LetterboxCoversThePane(TestContext ctx)
    {
        var host = new CutsceneController();
        ctx.Host.AddChild(host);
        ctx.CutsceneRoots = true;
        try
        {
            Aabb card = default;
            Vector3[]? quads = null;
            ctx.WithWorld(IntroChapter, collision: false, IntroMission,
                world =>
                {
                    host.BindWorld(world.Runtime);
                    card = host.CardBox;
                    quads = host.CardMesh?.Mesh?.GetFaces();
                });
            ctx.Check(card.Size.X > 0f && card.Size.Y > 0f && card.GetCenter().Z < 0f,
                $"the built {CutsceneController.BarsNode} carries a measurable card ({card.Size})");
            if (CutsceneController.FramingFovDeg(card) is not { } fov)
            {
                ctx.Check(false, $"the built card carries no usable fit");
                return;
            }

            // One vertical extent for every pane: the fit takes no aspect, which is the whole of
            // the frame holding its shape on a window wider than the card.
            float seenHalfHeight = Mathf.Tan(Mathf.DegToRad(fov) * 0.5f) * Mathf.Abs(card.GetCenter().Z);
            foreach (float aspect in LetterboxAspects)
            {
                float stretch = CutsceneController.BarsWidthScale(card, aspect);
                // Each edge on its own account, in card units, so a card whose AABB centre is off
                // the node origin fails on the side it is short of instead of averaging out: the
                // reported leak was 17 px on one edge and none on the other.
                float seenHalfWidth = seenHalfHeight * aspect;
                // The pane is centred on the bars root's origin, which is where camera1 puts the
                // eye; the card is wherever its own extent says, which need not be centred there.
                float left = -seenHalfWidth - (card.Position.X * stretch);
                float right = (card.End.X * stretch) - seenHalfWidth;
                float below = -seenHalfHeight - card.Position.Y;
                float above = card.End.Y - seenHalfHeight;
                ctx.Check(left > 0f && right > 0f && below > 0f && above > 0f,
                    $"at {aspect:0.###} the card stretched {stretch:0.####} overhangs the pane on every edge (l {left:0.####}, r {right:0.####}, b {below:0.####}, a {above:0.####})");
            }

            LetterboxFilmRectByHeight(ctx, card, quads);
        }
        finally
        {
            ctx.CutsceneRoots = false;
            host.Free();
        }
    }

    // The film's rect, the gap the two bars leave, read off the card's own vertices and laid
    // against a narrow pane and a wide one: the band is the same fraction of both, a 4:3 pane is
    // framed by the unstretched card, and the wide pane's extra width is world kept rather than a
    // crop of the narrow picture. The vertices are the mesh's own and the fit's extents the bars
    // root's, so one factor carries between them: the pane's half-height is the card's own less
    // the overscan.
    private static void LetterboxFilmRectByHeight(TestContext ctx, Aabb card, Vector3[]? quads)
    {
        if (quads == null || quads.Length == 0 || CutsceneController.FramingFovDeg(card) is not { } fit)
        {
            ctx.Check(false, $"the card mesh carries vertices and a fit to read the bars' film rect off");
            return;
        }

        float top = float.MinValue;
        float bottom = float.MaxValue;
        float topInner = float.MaxValue;
        float bottomInner = float.MinValue;
        foreach (var vertex in quads)
        {
            top = Mathf.Max(top, vertex.Y);
            bottom = Mathf.Min(bottom, vertex.Y);
            if (vertex.Y > 0f)
            {
                topInner = Mathf.Min(topInner, vertex.Y);
            }
            else
            {
                bottomInner = Mathf.Max(bottomInner, vertex.Y);
            }
        }

        float half = (top - bottom) * 0.5f;
        ctx.Check(half > 0f && topInner < top && bottomInner > bottom,
            $"the card is a pair of bars with a gap ({bottom:0.###} to {bottomInner:0.###}, {topInner:0.###} to {top:0.###})");
        if (!(half > 0f))
        {
            return;
        }

        // The rect the film occupies in a viewport of this pixel size, in fractions of it: the
        // engine's own fit, the gap between the bars against the pane it framed, and the width the
        // stretched bars reach across.
        Rect2 Film(float width, float height)
        {
            float aspect = width / height;
            float paneHalf = Mathf.Tan(Mathf.DegToRad(fit) * 0.5f) * Mathf.Abs(card.GetCenter().Z);
            float toPane = paneHalf * (1f + CutsceneController.CardOverscan) / half;
            float reach = CutsceneController.BarsWidthScale(card, aspect) * card.Size.X * 0.5f
                / (paneHalf * aspect);
            return new Rect2(
                0f,
                (paneHalf - (topInner * toPane)) / (2f * paneHalf),
                Mathf.Min(1f, reach),
                (topInner - bottomInner) * toPane / (2f * paneHalf));
        }

        var narrow = Film(1024f, 768f);
        var wide = Film(3840f, 1080f);
        ctx.Check(Mathf.IsEqualApprox(narrow.Size.X, 1f) && narrow.Position.Y > 0f
            && narrow.Position.Y + narrow.Size.Y < 1f,
            $"on a 1024x768 pane the film is the pane's full width by {narrow.Size.Y:0.####} of its height, {narrow.Position.Y:0.####} down");
        ctx.Check(wide.IsEqualApprox(narrow),
            $"on a 3840x1080 pane it is the same rect (x {wide.Position.X:0.####}, y {wide.Position.Y:0.####}, w {wide.Size.X:0.####}, h {wide.Size.Y:0.####})");
        ctx.Check(Mathf.IsEqualApprox(CutsceneController.BarsWidthScale(card, 4f / 3f), 1f),
            $"a 4:3 pane is framed by the authored card, unstretched");
        ctx.Note($"the film's rect is the pane's full width by {narrow.Size.Y:0.###} of its height, {narrow.Position.Y:0.###} down from the top, on a 4:3 pane and a 32:9 one alike");
    }

    // The card is world geometry with no depth guarantee of its own, so anything the episode flies
    // between the camera and it (a walkway, an airframe) draws over the bars unless BindWorld's
    // override wins the pixel outright.
    private static void LetterboxCardIsUnoccludable(TestContext ctx)
    {
        var host = new CutsceneController();
        ctx.Host.AddChild(host);
        ctx.CutsceneRoots = true;
        try
        {
            // Read both the override's and the source shader's properties INSIDE the callback: the
            // materials are freed once WithWorld returns. The source read is the Mesh resource's
            // own surface material, untouched by MaterialOverride, so it is still the authored value.
            bool hasOverride = false, noDepthTest = false, transparent = false, unshaded = false, cullDisabled = false;
            int renderPriority = 0;
            var overrideAlbedo = Colors.White;
            Color? authoredAlbedo = null;
            ctx.WithWorld(IntroChapter, collision: false, IntroMission,
                world =>
                {
                    host.BindWorld(world.Runtime);
                    var mesh = host.CardMesh;
                    if (mesh?.Mesh?.SurfaceGetMaterial(0) is ShaderMaterial source)
                    {
                        var tint = source.GetShaderParameter("albedo_color");
                        if (tint.VariantType == Variant.Type.Color)
                        {
                            authoredAlbedo = tint.As<Color>();
                        }
                    }

                    if (mesh?.MaterialOverride is StandardMaterial3D material)
                    {
                        hasOverride = true;
                        noDepthTest = material.NoDepthTest;
                        transparent = material.Transparency == BaseMaterial3D.TransparencyEnum.Alpha;
                        renderPriority = material.RenderPriority;
                        unshaded = material.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded;
                        cullDisabled = material.CullMode == BaseMaterial3D.CullModeEnum.Disabled;
                        overrideAlbedo = material.AlbedoColor;
                    }
                });
            ctx.Check(hasOverride, $"BindWorld overrides the {CutsceneController.BarsNode} card's material");
            ctx.Check(noDepthTest,
                $"the override skips the depth test, so nothing between the camera and the card can win the pixel");
            ctx.Check(transparent,
                $"and moves the card into the sorted-transparent pass, where render priority is honoured");
            ctx.Check(renderPriority == 127,
                $"claiming the top render priority so it draws last among transparent geometry ({renderPriority})");
            ctx.Check(unshaded,
                $"the override is unshaded, so scene lighting cannot dim the bars off their authored colour");
            ctx.Check(cullDisabled,
                $"and culls neither face, so the card reads the same off either side of the quad");
            // The docs-established shipped colour is opaque black, so a source read that fails
            // (VariantType mismatch) still has a colour to check against.
            var expectedAlbedo = authoredAlbedo ?? Colors.Black;
            string authoredNote = authoredAlbedo is { } a ? a.ToString() : "unread, opaque black assumed";
            ctx.Check(
                overrideAlbedo.R == expectedAlbedo.R && overrideAlbedo.G == expectedAlbedo.G
                && overrideAlbedo.B == expectedAlbedo.B && overrideAlbedo.A == 1f,
                $"the override's colour ({overrideAlbedo}) matches the card's authored one ({authoredNote})");
        }
        finally
        {
            ctx.CutsceneRoots = false;
            host.Free();
        }
    }

    // BL-452's second cause: a CanvasLayer draws over all 3D content, so the rig's own world
    // overlays paint the sun and the cloud over the card unless the presentation code lowers them.
    private static void PresentingLowersTheWorldOverlays(TestContext ctx)
    {
        var host = new CutsceneController();
        ctx.Host.AddChild(host);
        var camera = new Camera3D();
        var overlay = new CanvasLayer();
        ctx.Host.AddChild(camera);
        ctx.Host.AddChild(overlay);
        try
        {
            var rig = new Flight.Camera.PlayerRig { Camera = camera, HudParent = ctx.Host };
            rig.WorldOverlays.Add(overlay);
            host.BindRigs(new[] { rig }, () => System.Array.Empty<Flight.Airframe.FlightController>());
            ctx.Check(overlay.Visible, $"a rig's world overlays are up while nothing presents");
            host.Host(2, CutsceneController.IntroAnims[1]);
            ctx.Check(host.Presenting && !overlay.Visible,
                $"the presentation code lowers them, so nothing draws over the bars");
            host.Host(1, CutsceneController.IntroAnims[1]);
            ctx.Check(!host.Presenting && overlay.Visible, $"and the handoff puts them back");
        }
        finally
        {
            overlay.Free();
            camera.Free();
            host.Free();
        }
    }

    // The aeroplane the episode's external camera frames, and the panel it must not frame. The
    // presentation code silences the per-frame camera arm, which is also the only thing that
    // re-asserts the first-person hiding, so both edges are this code's own to write: a pilot who
    // entered from the cockpit would otherwise hold that frame's state, body undrawn and interior
    // drawn, for the whole episode. Driven at two seats, cockpit and chase, over both exits.
    private static void PresentingFramesTheAirframe(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, IntroChapter);
        ctx.RequireData(texturesPath, $"{IntroChapter} textures");

        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var host = new CutsceneController();
        var world = new Node3D { Name = "PresentationWorld" };
        var runtime = new AnimRuntime { AutoStart = false, ManualAdvance = true, SoundHandledElsewhere = true };
        ctx.Host.AddChild(host);
        ctx.Host.AddChild(world);
        ctx.Host.AddChild(runtime);
        Seat? cockpit = null;
        Seat? chase = null;
        try
        {
            // An empty program, bound: the host asks the runtime whether its definition still runs,
            // and a program carrying none answers "ended", which is the ordinary exit.
            runtime.Bind(world, new AnimProgram());
            host.BindWorld(runtime);
            cockpit = Seat.Build(ctx, planesGamez, textures, Flight.Camera.PilotViewMode.Cockpit, 0);
            chase = Seat.Build(ctx, planesGamez, textures, Flight.Camera.PilotViewMode.Chase, 1);
            if (cockpit.Body == null || cockpit.Interior == null || cockpit.Pilot.CockpitPass == null)
            {
                ctx.Check(false, $"the seat build carries an airframe body, an interior and its pass");
                return;
            }

            host.BindRigs(new[] { cockpit.Rig, chase.Rig },
                () => System.Array.Empty<Flight.Airframe.FlightController>());
            DriveEpisode(ctx, host, cockpit, chase, byDefinitionEnd: true);
            DriveEpisode(ctx, host, cockpit, chase, byDefinitionEnd: false);
        }
        finally
        {
            chase?.Free();
            cockpit?.Free();
            runtime.Free();
            world.Free();
            host.Free();
            textures.Dispose();
        }
    }

    // One episode, taken to whichever exit `byDefinitionEnd` names: a tick that finds the
    // definition ended, or a player's skip. Both go through the controller's own restore, so both
    // owe the cockpit seat its view back.
    private static void DriveEpisode(TestContext ctx, CutsceneController host, Seat cockpit,
        Seat chase, bool byDefinitionEnd)
    {
        string exit = byDefinitionEnd ? "the definition ending" : "a player's skip";
        // The last flying frame, written by the arm the presentation is about to silence.
        cockpit.ApplyView();
        chase.ApplyView();
        ctx.Check(cockpit.Interior is { Visible: true } && cockpit.Body is { Visible: false },
            $"the cockpit seat enters the episode with the interior drawn and the airframe hidden, which is what that view leaves standing");
        ctx.Check(cockpit.Pilot.CockpitPass is { Visible: true },
            $"…and its interior pass drawing over the pane");

        host.Host(CutsceneController.CodeHoldsWorld, IntroAnim);
        host.Host(PresentationCode, IntroAnim);
        ctx.Check(host.Presenting && cockpit.Pilot.CameraOwned,
            $"the presentation code takes the view off the aircraft, which is what stops the arm re-asserting anything");
        ctx.Check(cockpit.Body is { Visible: true },
            $"so the code draws the airframe itself, and the episode's camera frames an aeroplane rather than nothing ({exit} leg)");
        ctx.Check(cockpit.Interior is { Visible: false } && cockpit.Pilot.CockpitPass is { Visible: false },
            $"…with the cockpit interior and its pass off the screen, so no panel hangs over the shot ({exit} leg)");
        ctx.Check(chase.Body is { Visible: true } && chase.Interior is { Visible: false },
            $"…and the chase seat, which was already drawing its airframe, is untouched");

        if (byDefinitionEnd)
        {
            host.Tick();
        }
        else
        {
            ctx.Check(host.Skip(), $"the skip is armed and the key press is taken");
        }

        ctx.Check(!host.Playing && !host.Presenting && !cockpit.Pilot.CameraOwned,
            $"{exit} hands the view back");
        ctx.Check(cockpit.Interior is { Visible: true } && cockpit.Body is { Visible: false },
            $"…and puts the cockpit seat back in the cockpit it chose, rather than leaving it outside its own aeroplane");
        ctx.Check(cockpit.Pilot.CockpitPass is { Visible: true },
            $"…with its interior pass drawing again");
        ctx.Check(cockpit.Pilot.ViewMode == Flight.Camera.PilotViewMode.Cockpit
                  && chase.Pilot.ViewMode == Flight.Camera.PilotViewMode.Chase,
            $"…and neither seat's SELECTED view was moved to get there");
        ctx.Check(chase.Body is { Visible: true } && chase.Interior is { Visible: false },
            $"…while the chase seat still draws its airframe and no interior");
    }

    // The objectives half of callback 20: a held director advances no dormancy timer, which is what
    // stops a mission's reminder fuses burning down behind the movie.
    private static void CutsceneHoldsObjectives(TestContext ctx)
    {
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        if (FirstMissionOf(missions, IntroChapter) is not { } mission)
        {
            return;
        }

        var script = ObjectiveScript.Load(
            SessionPaths.MissionZrdr(ctx.DataRoot, mission.ChapterFolder, mission.MissionFolder));
        var director = CampaignDirector.Create(script, mission, CampaignProfileDef.NewProfile("Zachary"), null);
        director.Attach(new CampaignDirector.WorldInputs());
        director.HoldForCutscene(true);
        for (int i = 0; i < 600; i++)
        {
            director.Step(1f);
        }

        ctx.Same(0, (long)director.Graph!.Elapsed, $"a held objectives graph does not advance under the movie");
        director.HoldForCutscene(false);
        director.Step(1f);
        ctx.Check(director.Graph!.Elapsed > 0f, $"and runs again once the cutscene hands off");
    }

    // C3/M03 opens on a cutscene no start list names: `calldestroy_the_cargozep` is the start anim,
    // and its first event calls `cgzep_camera`. Both gates have to read the start list's call
    // closure, the session's (the camera, the bars and the `player` marker stood up before the
    // bind) and the host's (the called definition's codes answered rather than counted).
    private static void OpeningSceneCalledFromStartAnim(TestContext ctx)
    {
        var host = new CutsceneController();
        ctx.Host.AddChild(host);
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(CalledChapter, collision: false, CalledMission, world =>
            {
                var startAnims = world.Session.Program.StartAnims;
                ctx.Check(!startAnims.Contains(CalledAnim) && startAnims.Contains(CalledFrom),
                    $"'{CalledAnim}' is in no start list; '{CalledFrom}' is, and calls it");
                ctx.Check(world.Session.Aircraft?.PlayerMarker != null,
                    $"the world build read the start list's call closure and staged the '{AircraftStage.PlayerNode}' marker the called definition is rooted on");
                ctx.Check(world.Runtime.FindNodes(CutsceneController.CameraNode).Count > 0,
                    $"and stood up '{CutsceneController.CameraNode}' for its camera path");

                var stage = new Node3D { Name = "CalledCutsceneStage" };
                var runtime = new AnimRuntime { AutoStart = false, ManualAdvance = true, SoundHandledElsewhere = true };
                runtime.CallbackHost = host.Host;
                ctx.Host.AddChild(stage);
                ctx.Host.AddChild(runtime);
                try
                {
                    runtime.Bind(stage, world.Session.Program.Subset(CalledFrom));
                    host.BindWorld(runtime);
                    runtime.Play(CalledFrom);
                    ctx.Check(host.Playing && host.Anim == CalledAnim,
                        $"playing the start anim hands the session to '{CalledAnim}' through the runtime's own dispatch");
                    ctx.Same(CutsceneController.CodeHoldsWorld, host.Codes.Count > 0 ? host.Codes[0] : 0,
                        $"whose first code holds the world");
                    ctx.Check(host.HoldsWorld && host.OutOfFlight && host.AiParked && host.Presenting && host.Skippable,
                        $"so the world is held, the player is out of flight, the AI is parked, the chrome is off and a skip is armed");
                    // The destruction reaches this runtime through the start anim's closure and
                    // nothing else, so the host has no call of its own to issue. A bare stage
                    // anchors it on nothing, so its running state is not read here.
                    ctx.Check(runtime.Handles(CalledDestruction),
                        $"'{CalledDestruction}' is in the same start anim's call closure, which is the one call it plays under the camera from");
                    ctx.Check(host.Skip() && !host.Playing && !host.HoldsWorld && !host.OutOfFlight,
                        $"and the skip the hold armed ends the scene and hands the session back");
                }
                finally
                {
                    runtime.Free();
                    stage.Free();
                }
            });
        }
        finally
        {
            ctx.CutsceneRoots = false;
            host.Free();
        }
    }

    // Runs the loss fuse the mission authors: nothing is satisfied, OBJECTIVE19 wakes at its 300 s
    // and naps the INSTANTLOSS objective 15 s later, which is what loses the mission.
    private static void LossFuse(TestContext ctx, ObjectiveScript script, StringBuilder report)
    {
        var graph = new ObjectiveGraph(script, new ScriptedWorld());
        Advance(graph, 305f);
        ctx.Check(graph.CompletedOf(19), $"the last reminder completes at its BEGIN_DORMANT 300 s");
        ctx.Check(graph.StateOf(24) == ObjectiveState.Napping, $"its NAP put the INSTANTLOSS objective on a timer");
        ctx.Check(!graph.Ended, $"the mission has not ended while that nap runs");
        Advance(graph, 20f);
        ctx.Check(graph.Outcome == MissionOutcome.Lost, $"the INSTANTLOSS objective ends the mission lost");
        report.AppendLine($"loss-fuse run: lost at {graph.Elapsed:0.0} s");
    }

    // NAP_OBJECTIVE_WHEN_I_COMPLETE clearing a completed flag is engine surface no shipped mission
    // cycles into, so it is driven from a script written in the same vocabulary.
    private static void NapClearsCompletion(TestContext ctx)
    {
        var script = ObjectiveScript.Parse(new List<object?>
        {
            new List<object?>
            {
                "OBJECTIVE1", new List<object?>
                {
                    "BEGIN_DORMANT", new List<object?> { 1.0f },
                    "NAP_OBJECTIVE_WHEN_I_COMPLETE", new List<object?> { 2.0f, 1.0f },
                },
                "OBJECTIVE2", null,
            },
        });
        var graph = new ObjectiveGraph(script, new ScriptedWorld());
        Advance(graph, 0.5f);
        ctx.Check(graph.CompletedOf(2), $"the conditionless objective completes first");
        Advance(graph, 0.7f);
        ctx.Check(!graph.CompletedOf(2), $"the nap cleared its completed flag");
        ctx.Check(graph.StateOf(2) == ObjectiveState.Napping, $"and left it napping");
        Advance(graph, 2f);
        ctx.Check(graph.CompletedOf(2), $"it ran again, which no plain wake could have done");
    }

    // The original runs both mask-building loops regardless of outcome, so a lost
    // attempt still records whichever non-primary objectives it completed, with only bit 0 (the
    // primary) forced clear (docs/org/debrief.md, "The completed-objective mask has two sources").
    // Driven independently of the win leg above, on its own fresh world, since a player death is
    // not a graph ending any shipped mission authors on its own.
    private static void CampaignMissionLossKeepsObjectiveBits(
        TestContext ctx, TestWorld world, ObjectiveScript script, CampaignMission mission, StringBuilder report)
    {
        var profile = CampaignProfileDef.NewProfile("Amelia");
        var director = CampaignDirector.Create(script, mission, profile, null);
        director.Attach(new CampaignDirector.WorldInputs { Runtime = world.Runtime });
        var graph = director.Graph!;

        int driven = DriveOneNonPrimaryInactive(ctx, world, script, graph, report);
        if (driven == 0)
        {
            throw new SuiteSkippedException(
                $"{mission.ChapterFolder}/{mission.MissionFolder} authors no non-primary INACTIVE "
                + "objective to drive ahead of the loss");
        }

        if ((graph.CompletedMask & CampaignProgression.PrimaryObjectiveMask) != 0)
        {
            throw new SuiteSkippedException(
                $"{mission.ChapterFolder}/{mission.MissionFolder}'s driven objective chained into "
                + "the primary, so this mission cannot show a non-zero mask with the primary clear");
        }

        ctx.Check(graph.NotifyPlayerLost(), $"the player's own death is reported once");
        graph.EndAfterPlayerLost();
        Advance(director, CampaignDirector.LeavingHoldS + 0.2f);
        ctx.Check(director.ReturnToCabin, $"a player-death loss still raised the return-to-cabin exit");
        ctx.Check(Mathf.Abs(director.LeavingFade - 1f) < 0.01f,
            $"the loss ending's own hold lands the fade fully black too, fade={director.LeavingFade:0.###}");
        var result = director.Result!.Value;
        ctx.Check(result.Outcome == MissionOutcome.Lost, $"the player's own death ends the mission lost");
        ctx.Check((result.Attempt.CompletedMask & CampaignProgression.PrimaryObjectiveMask) == 0,
            $"the primary bit is still forced clear on a loss, mask 0x{result.Attempt.CompletedMask:x}");
        ctx.Check(result.Attempt.CompletedMask != 0,
            $"…but the driven objective's own bit survives, mask 0x{result.Attempt.CompletedMask:x}");
        ctx.Check(!result.Recorded.PrimaryCompleted && !result.Recorded.Advanced,
            $"a non-zero mask on a loss still does not complete the primary or advance the campaign");
        // The loss is the mission's first, so it counts one against the skip offer and
        // the offer itself waits for the fourth.
        int attempts = CampaignProgression.ResultOf(profile, mission.Seq)?.Attempts ?? 0;
        ctx.Check(attempts == 1 && !result.Recorded.SkipOffered,
            $"the loss counted as attempt {attempts} and offered no skip on it");
        report.AppendLine($"loss leg: ended {result.Outcome}, mask 0x{result.Attempt.CompletedMask:x}, "
            + $"advanced={result.Recorded.Advanced}, attempt {attempts}");
    }

    // The mission-end fade runs over the frame that was last on screen, because the original fades
    // a COPY of it (docs/formats/objectives.md, "The mission-end path"). Three paths reach it and
    // they do not agree on what that frame is, so all three are driven: a win the docking film
    // raises itself, a loss landing while a film plays, and a win in free flight.
    private static void EndingFadeRunsOverTheLastFrame(
        TestContext ctx, ObjectiveScript script, CampaignMission mission, StringBuilder report)
    {
        EndingUnderAFilm(ctx, script, mission, report, byDocking: true);
        EndingUnderAFilm(ctx, script, mission, report, byDocking: false);
        EndingInFreeFlight(ctx, script, mission, report);
    }

    // An ending raised while a film is presenting: the docking's own completion code, or a death
    // reported from the anim side under any film. Both land at once, with no wrap-up the held world
    // would have to run down, and both leave the episode presenting for the whole hold.
    private static void EndingUnderAFilm(
        TestContext ctx, ObjectiveScript script, CampaignMission mission, StringBuilder report,
        bool byDocking)
    {
        string leg = byDocking ? "docking win" : "loss under a film";
        var director = CampaignDirector.Create(
            script, mission, CampaignProfileDef.NewProfile("Zachary"), null);
        director.Attach(new CampaignDirector.WorldInputs());
        var graph = director.Graph!;
        var host = new CutsceneController();
        var stage = new Node3D { Name = "EndingFilmStage" };
        var runtime = new AnimRuntime { AutoStart = false, ManualAdvance = true, SoundHandledElsewhere = true };
        ctx.Host.AddChild(host);
        ctx.Host.AddChild(stage);
        ctx.Host.AddChild(runtime);
        try
        {
            // An empty program, bound: the host asks the runtime whether its definition still runs,
            // and a program carrying none answers "ended", which is the exit under test here.
            runtime.Bind(stage, new AnimProgram());
            host.BindWorld(runtime);
            host.WorldHeld = director.HoldForCutscene;
            host.MissionComplete = () => graph.NotifyDockingComplete();
            host.EndingLanded = () => director.Result != null;
            host.Host(CutsceneController.CodeHoldsWorld, IntroAnim);
            host.Host(PresentationCode, IntroAnim);
            ctx.Check(host.Playing && host.Presenting,
                $"the film owns the session and the presentation before the ending ({leg})");

            if (byDocking)
            {
                host.Host(DockingCompleteCode, IntroAnim);
            }
            else
            {
                ctx.Check(graph.NotifyPlayerLost(), $"the player's own death is reported once ({leg})");
                graph.EndAfterPlayerLost();
            }

            ctx.Check(director.Result != null,
                $"the ending landed on the frame it was raised on, under the held world ({leg})");
            host.Tick();
            ctx.Check(host.Playing && host.Presenting && host.HeldForEnding,
                $"and the film keeps the shot at its own definition's end, so the fade has the film's frame to run over ({leg})");
            ctx.Check(!host.Skip(), $"…which no skip can pull out from under the fade ({leg})");

            bool heldThroughout = true;
            for (float t = 0f; t < CampaignDirector.LeavingHoldS + 0.2f; t += 0.1f)
            {
                director.Step(0.1f);
                host.Tick();
                heldThroughout &= host.Playing && host.Presenting;
            }

            ctx.Check(heldThroughout,
                $"the shot is the film's for every frame of the {CampaignDirector.LeavingHoldS:0.#}s hold ({leg})");
            ctx.Check(director.ReturnToCabin && Mathf.Abs(director.LeavingFade - 1f) < 0.01f,
                $"…and the hold ended fully black on it, fade={director.LeavingFade:0.###} ({leg})");
            report.AppendLine($"fade leg '{leg}': ended {director.Result!.Value.Outcome} under the "
                + $"film, presentation held={heldThroughout}");
        }
        finally
        {
            runtime.Free();
            stage.Free();
            host.Free();
        }
    }

    // The third path: the same ending with no film playing at all. There is nothing to keep, the
    // host stays down, and the frame the fade runs over is the pilot's own view.
    private static void EndingInFreeFlight(
        TestContext ctx, ObjectiveScript script, CampaignMission mission, StringBuilder report)
    {
        var director = CampaignDirector.Create(
            script, mission, CampaignProfileDef.NewProfile("Amelia"), null);
        director.Attach(new CampaignDirector.WorldInputs());
        var graph = director.Graph!;
        var host = new CutsceneController();
        var stage = new Node3D { Name = "EndingFreeFlightStage" };
        var runtime = new AnimRuntime { AutoStart = false, ManualAdvance = true, SoundHandledElsewhere = true };
        ctx.Host.AddChild(host);
        ctx.Host.AddChild(stage);
        ctx.Host.AddChild(runtime);
        try
        {
            runtime.Bind(stage, new AnimProgram());
            host.BindWorld(runtime);
            host.EndingLanded = () => director.Result != null;
            int winner = EndObjective(script);
            if (winner == 0)
            {
                // The suite above already reported the missing INSTANTWIN; nothing to drive here.
                return;
            }

            graph.Wake(winner);
            Advance(graph, 2f);
            ctx.Check(director.Result != null, $"the free-flight win ended the mission");

            bool downThroughout = true;
            for (float t = 0f; t < CampaignDirector.LeavingHoldS + 0.2f; t += 0.1f)
            {
                director.Step(0.1f);
                host.Tick();
                downThroughout &= !host.Playing && !host.Presenting && !host.HeldForEnding;
            }

            ctx.Check(downThroughout,
                $"no episode is kept over a win in free flight, so the fade runs over the pilot's own view");
            ctx.Check(director.ReturnToCabin && Mathf.Abs(director.LeavingFade - 1f) < 0.01f,
                $"…and that hold ends fully black too, fade={director.LeavingFade:0.###}");
            report.AppendLine($"fade leg 'free-flight win': ended {director.Result!.Value.Outcome} "
                + $"with no film, host down={downThroughout}");
        }
        finally
        {
            runtime.Free();
            stage.Free();
            host.Free();
        }
    }

    // Destroys (or deactivates) every node of one objective's INACTIVE paths, then ticks until it
    // completes. Two passes: an objective whose nodes are all real destructibles first, so the
    // condition is driven by a WEAPON kill through DamageAt rather than by a bare deactivation.
    private static int DriveOneInactive(
        TestContext ctx, TestWorld world, ObjectiveScript script, ObjectiveGraph graph, StringBuilder report)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            int found = DriveInactivePass(ctx, world, script, graph, report, pass == 0, def => true);
            if (found > 0)
            {
                return found;
            }
        }

        return 0;
    }

    // The same drive, restricted to an objective that carries a display row of its own that is not
    // the primary: what a lost attempt needs to prove it keeps a non-primary bit.
    private static int DriveOneNonPrimaryInactive(
        TestContext ctx, TestWorld world, ObjectiveScript script, ObjectiveGraph graph, StringBuilder report)
    {
        bool NonPrimary(ObjectiveDef def) => def.Identity is { Class: not ObjectiveClass.Primary };
        for (int pass = 0; pass < 2; pass++)
        {
            int found = DriveInactivePass(ctx, world, script, graph, report, pass == 0, NonPrimary);
            if (found > 0)
            {
                return found;
            }
        }

        return 0;
    }

    private static int DriveInactivePass(
        TestContext ctx, TestWorld world, ObjectiveScript script, ObjectiveGraph graph,
        StringBuilder report, bool destructiblesOnly, System.Func<ObjectiveDef, bool> extra)
    {
        foreach (var def in script.Objectives)
        {
            if (def.Inactive.Count == 0 || def.InstantLoss || !extra(def))
            {
                continue;
            }

            var nodes = new List<Node3D>();
            foreach (var path in def.Inactive)
            {
                if (Resolve(world, path) is { } node)
                {
                    nodes.Add(node);
                }
            }

            if (nodes.Count == 0 || nodes.Count < (def.InactiveCount ?? def.Inactive.Count)
                || (destructiblesOnly && !AllDestructible(world, nodes)))
            {
                continue;
            }

            int killed = 0;
            foreach (var node in nodes)
            {
                if (world.Runtime.Destructibles.Resolve(node) is { MaxHealth: > 0f } live)
                {
                    world.Runtime.DamageAt(node, live.MaxHealth);
                    killed++;
                }
                else
                {
                    AnimRuntime.SetSubtreeActive(node, false);
                }
            }

            graph.Wake(def.Number);
            Advance(graph, 10f);
            report.AppendLine($"OBJECTIVE{def.Number}: {nodes.Count} node(s), {killed} by weapon damage, "
                + $"completed={graph.CompletedOf(def.Number)}");
            ctx.Check(graph.CompletedOf(def.Number),
                $"OBJECTIVE{def.Number} completed once its nodes lost their active bit");
            return nodes.Count;
        }

        return 0;
    }

    private static bool AllDestructible(TestWorld world, List<Node3D> nodes)
    {
        foreach (var node in nodes)
        {
            if (world.Runtime.Destructibles.Resolve(node) is not { MaxHealth: > 0f })
            {
                return false;
            }
        }

        return true;
    }

    private static Node3D? Resolve(TestWorld world, IReadOnlyList<string> path)
    {
        Node3D? node = null;
        foreach (var name in path)
        {
            var found = world.Runtime.FindNodes(name, node);
            if (found.Count == 0)
            {
                return null;
            }

            node = found[0];
        }

        return node;
    }

    // What the reward table owes a fresh profile for one mission at one mask: every record on this
    // ordinal whose objective bit the run set. A fresh profile has banked nothing, so there is no
    // prior mask to gate against here.
    private static int ExpectedReward(int seq, int completedMask)
    {
        int owed = 0;
        foreach (var reward in CampaignProgression.MissionRewards)
        {
            if (reward.Ordinal == seq + 1 && (completedMask & (1 << reward.ObjectiveBit)) != 0)
            {
                owed += reward.Cash;
            }
        }

        return owed;
    }

    // One sortie on a fresh director: wake the paying objective's gate, wake the objective, then
    // end the mission on its own INSTANTWIN. Returns what the reward table paid for it.
    private static int FlyForTheReward(
        TestContext ctx, ObjectiveScript script, CampaignMission mission, CampaignProfileDef profile,
        Flight.Weapons.ProjectilePool live, Flight.Airframe.FlightController seated,
        List<Flight.Airframe.FlightController> field, int payingNumber, int winner)
    {
        var director = CampaignDirector.Create(script, mission, profile, null);
        var seatedPos = seated.WorldPosition;
        director.Attach(new CampaignDirector.WorldInputs
        {
            Projectiles = live,
            ListenerPosition = () => seatedPos,
            PlayerAircraft = () => seated,
            Humans = () => field,
        });
        director.Step(0.1f);

        // TICK_DEPENDS_ON_OBJ gates the whole objective on its dependency being AWAKE, so the gate
        // is woken first and left awake; the paying objective carries no conditions and completes
        // on its first eligible tick.
        var graph = director.Graph!;
        var paying = script.ByNumber(payingNumber)!;
        if (paying.TickDependsOn > 0)
        {
            graph.Wake(paying.TickDependsOn);
        }

        graph.Wake(payingNumber);
        Advance(director, 1f);
        ctx.Check((graph.CompletedMask & (1 << paying.Identity!.Value.Priority)) != 0,
            $"OBJECTIVE{payingNumber} completed and sits at mask bit {paying.Identity!.Value.Priority}");
        graph.Wake(winner);
        Advance(director, CampaignDirector.LeavingHoldS + 2.2f);
        ctx.Check(director.Result != null, $"the sortie ended and banked its result");
        return director.Result!.Value.Recorded.MoneyPaid;
    }

    // The reward record a mission ordinal carries, or null where the table names no reward for it.
    private static MissionReward? FirstReward(int seq)
    {
        foreach (var reward in CampaignProgression.MissionRewards)
        {
            if (reward.Ordinal == seq + 1)
            {
                return reward;
            }
        }

        return null;
    }

    private static int EndObjective(ObjectiveScript script)
    {
        foreach (var def in script.Objectives)
        {
            if (def.InstantWin)
            {
                return def.Number;
            }
        }

        return 0;
    }

    private static void Advance(ObjectiveGraph graph, float seconds)
    {
        for (float t = 0f; t < seconds; t += 0.1f)
        {
            graph.Step(0.1f);
        }
    }

    // The same walk through the director rather than the graph, which is what an ended mission's
    // leaving hold counts down on.
    private static void Advance(CampaignDirector director, float seconds)
    {
        for (float t = 0f; t < seconds; t += 0.1f)
        {
            director.Step(0.1f);
        }
    }

    // The director's own Step, not the graph's: it also runs the danger-zone tracker and the
    // scripted-path/music phases, the shape a real session drives.
    private static void StepDirector(CampaignDirector director, float seconds)
    {
        for (float t = 0f; t < seconds; t += 0.1f)
        {
            director.Step(0.1f);
        }
    }

    private static CampaignMission? FirstMissionOf(
        IReadOnlyList<CampaignMission> missions, string chapter)
    {
        CampaignMission? found = null;
        foreach (var m in missions)
        {
            if (m.ChapterFolder.Equals(chapter, System.StringComparison.OrdinalIgnoreCase)
                && (found == null || m.Seq < found.Value.Seq))
            {
                found = m;
            }
        }

        return found;
    }

    // The one mission at an exact (chapter, mission-folder) address, for a suite that names a
    // specific worked mission rather than "the chapter's first/last".
    private static CampaignMission? MissionAt(
        IReadOnlyList<CampaignMission> missions, string chapter, string missionFolder)
    {
        foreach (var m in missions)
        {
            if (m.ChapterFolder.Equals(chapter, System.StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(missionFolder, System.StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }

        return null;
    }

    // The last campaign mission stored in a chapter's world folder. The suite needs two missions of
    // one folder, which four of the eight folders have.
    private static CampaignMission? LastMissionOf(
        IReadOnlyList<CampaignMission> missions, string chapter)
    {
        CampaignMission? found = null;
        foreach (var m in missions)
        {
            if (m.ChapterFolder.Equals(chapter, System.StringComparison.OrdinalIgnoreCase)
                && (found == null || m.Seq > found.Value.Seq))
            {
                found = m;
            }
        }

        return found;
    }

    // A human rig for CampaignCoopAttempt, built and registered like CombatSuites' own manual rig:
    // Setup before the node joins the tree, then RegisterAircraft, so its body is a real hittable
    // target for another rig's cannon round.
    private static Flight.Airframe.FlightController BuildAttemptRig(
        TestContext ctx, GameZ planesGamez, TextureArchive textures, Flight.Weapons.ProjectilePool live,
        int index, Vector3 at, int? team = null)
    {
        var stats = Flight.Airframe.PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
        var pilot = new Flight.Airframe.FlightController
        {
            PlaneModel = model,
            Collider = Flight.Airframe.PlaneCollider.Build(model),
            Damage = stats.DestroyableParts.Count > 0 || stats.VehicleHealth is > 0f
                ? Flight.Airframe.PlaneDamage.For(stats) : null,
            PlayerIndex = FlightRoster.ShooterIdBase + index,
            IsHumanPiloted = true,
            Projectiles = live,
            UseKeyboard = false,
            PadDevices = System.Array.Empty<int>(),
            AllowPause = false,
            Team = team ?? Flight.Weapons.AimAssist.PlayerTeam,
            Name = $"AttemptRig{index}",
        };
        pilot.AddChild(model);
        pilot.Setup(new Flight.Airframe.FlightModel(stats), ctx.Camera, new Flight.Camera.CamParams(), at, at + Vector3.Forward);
        ctx.Host.AddChild(pilot);
        live.RegisterAircraft(pilot.Body!);
        return pilot;
    }

    // Node index -> anchor, for every node a PERSIST_LOG def binds.
    private static Dictionary<int, Node3D> PersistedNodes(DestructibleRegistry registry)
    {
        var nodes = new Dictionary<int, Node3D>();
        foreach (var inst in registry.All)
        {
            int index = NodeIndex(inst.Anchor);
            if (inst.Def.PersistLog && index >= 0)
            {
                nodes[index] = inst.Anchor;
            }
        }

        return nodes;
    }

    private static Dictionary<int, Node3D> AllNodes(DestructibleRegistry registry)
    {
        var nodes = new Dictionary<int, Node3D>();
        foreach (var inst in registry.All)
        {
            int index = NodeIndex(inst.Anchor);
            if (index >= 0)
            {
                nodes[index] = inst.Anchor;
            }
        }

        return nodes;
    }

    // The control: a destructible no PERSIST_LOG def binds, destroyed in the same mission. The
    // original's save-only defs are the transient layer, and must not cross a mission boundary.
    private static int DestroySaveOnly(
        AnimRuntime runtime, Dictionary<int, Node3D> persisted, StringBuilder report)
    {
        foreach (var inst in runtime.Destructibles.All)
        {
            int index = NodeIndex(inst.Anchor);
            if (index < 0 || persisted.ContainsKey(index)
                || Live(runtime.Destructibles, inst.Anchor) is not { } live
                || live.Status != DestructibleRegistry.State.Healthy || live.MaxHealth <= 0f)
            {
                continue;
            }

            runtime.DamageAt(inst.Anchor, live.MaxHealth);
            report.AppendLine($"destroyed save-only node={index} name={inst.Anchor.Name}");
            return index;
        }

        return -1;
    }

    private static DestructibleRegistry.Instance? Live(DestructibleRegistry registry, Node3D anchor) =>
        registry.Resolve(anchor);

    // The pool one named definition binds, for a suite that names a worked object rather than
    // taking whatever the chapter ships first.
    private static DestructibleRegistry.Instance? PoolNamed(DestructibleRegistry registry, string def) =>
        registry.All.FirstOrDefault(i =>
            i.Def.Name.Equals(def, System.StringComparison.OrdinalIgnoreCase));

    private static int NodeIndex(Node3D node) =>
        node.HasMeta(AnimRuntime.IndexMeta) ? (int)node.GetMeta(AnimRuntime.IndexMeta) : -1;

    // The graph's world seam with no world behind it: node activity and anim state answer what the
    // suite sets, and every world-touching action is recorded rather than performed. The two
    // families a session cannot answer at all report null here, exactly as the live adapter does.
    private sealed class ScriptedWorld : IObjectiveWorld
    {
        public HashSet<string> Inactive { get; } = new(System.StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> AnimStates { get; } = new(System.StringComparer.OrdinalIgnoreCase);

        public List<string> SoundGroups { get; } = new();

        public List<string> TurretPatterns { get; } = new();

        public List<string> AiNets { get; } = new();

        public List<string> Generators { get; } = new();

        public bool? NodeInactive(IReadOnlyList<string> path) => Inactive.Contains(path[^1]);

        public int AnimState(string anim) => AnimStates.TryGetValue(anim, out int state) ? state : 0;

        public int? GroupLiveCount(int group, string? generator) => null;

        public void WidenGroupEngagement(int group)
        {
        }

        public bool? TravelersMet(TravelersSpec spec) => null;

        public void WakeupEnemies(IReadOnlyList<string> names)
        {
        }

        public void WakeupTurrets(IReadOnlyList<string> patterns) => TurretPatterns.AddRange(patterns);

        public void WakeupZepTurrets(IReadOnlyList<string> nodes) => TurretPatterns.AddRange(nodes);

        public void WakeupGenerator(string name, int count) => Generators.Add(name);

        public void WakeAnim(string anim, string? node)
        {
        }

        public void PlaySoundGroup(string group) => SoundGroups.Add(group);

        public void StopQueuedSounds(IReadOnlyList<string> names)
        {
        }

        public void WarpVehicle(string vehicle, IReadOnlyList<WarpPoint> points)
        {
        }

        public void SetAiTeam(IReadOnlyList<(string Name, int Team)> entries)
        {
        }

        public void SetAiNet(IReadOnlyList<(string Name, string Net)> entries)
        {
            foreach (var entry in entries)
            {
                AiNets.Add(entry.Net);
            }
        }

        public void SetAiAttackRadius(IReadOnlyList<(string Name, float Radius)> entries)
        {
        }

        public void CompletedZepcannons(IReadOnlyList<(string Zeppelin, int Flag)> entries)
        {
        }

        public void CompletedStoppoint(IReadOnlyList<(string Net, int Stop, int Flag)> entries)
        {
        }

        public void StartTaxi(IReadOnlyList<string> names)
        {
        }
    }

    // One human seat for the presentation check: a real interior build, the per-mode visibility rig
    // over it and the interior's own render pass, wired the way the session's own human builder
    // wires them. Nothing here is shared with another suite, so it frees everything it made.
    private sealed class Seat
    {
        private Seat(Flight.Camera.PlayerRig rig, Flight.Airframe.FlightController pilot, Node3D? body,
            Node3D? interior, Flight.Camera.PilotViewMode view)
        {
            Rig = rig;
            Pilot = pilot;
            Body = body;
            Interior = interior;
            View = view;
        }

        internal Flight.Camera.PlayerRig Rig { get; }

        internal Flight.Airframe.FlightController Pilot { get; }

        // The airframe's own `healthy` group and its `cockpit1` subtree: the two nodes the per-mode
        // rule writes, read directly so the check reads the scene rather than the rule again.
        internal Node3D? Body { get; }

        internal Node3D? Interior { get; }

        private Flight.Camera.PilotViewMode View { get; }

        internal static Seat Build(TestContext ctx, GameZ planesGamez, TextureArchive textures,
            Flight.Camera.PilotViewMode view, int index)
        {
            var builder = new PlaneBuilder(planesGamez, textures, spinningProps: true,
                cockpitInterior: true);
            var model = builder.Build(ctx.PlaneName);
            var pilot = new Flight.Airframe.FlightController
            {
                PlaneModel = model,
                Collider = Flight.Airframe.PlaneCollider.Build(model),
                PlayerIndex = index,
                IsHumanPiloted = true,
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
                PinnedViewMode = view,
                Cockpit = Flight.Hud.CockpitVisibility.Bind(model, builder.CockpitInterior),
                CockpitInterior = builder.CockpitInterior,
                Name = $"CutsceneSeat{index}",
            };
            var body = WorldAndToolSuites.FindNamed(model, "healthy");
            pilot.AddChild(model);
            ctx.Host.AddChild(pilot);
            var camera = new Camera3D { Name = $"CutsceneSeatCamera{index}" };
            ctx.Host.AddChild(camera);
            // ⚠ After the visibility bind, and before Setup: the pass moves the interior out of the
            // plane model, which is why hiding the airframe cannot take the panel with it.
            if (builder.CockpitInterior is { } interior)
            {
                pilot.CockpitPass = Flight.Hud.CockpitOverlay.Build(ctx.Host, interior, null, null);
            }

            pilot.Setup(new Flight.Airframe.FlightModel(Flight.Airframe.PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName)),
                camera, new Flight.Camera.CamParams(), Vector3.Zero, Vector3.Forward);
            var rig = new Flight.Camera.PlayerRig
            {
                Index = index,
                Camera = camera,
                HudParent = ctx.Host,
                Controller = pilot,
            };
            return new Seat(rig, pilot, body, builder.CockpitInterior, view);
        }

        // The write the per-frame camera arm makes on an ordinary flying frame in this seat's view.
        internal void ApplyView() => Pilot.Cockpit?.Apply(View, Flight.Camera.PilotView.IsFirstPerson(View));

        internal void Free()
        {
            Pilot.CockpitPass?.Free();
            Pilot.Free();
            Rig.Camera.Free();
        }
    }
}
