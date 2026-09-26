using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>BL-499: CM02's second Peacemaker squad, the one carrying the ace. The three blocks
/// ship <c>deactivated</c> and OBJECTIVE8 names them in <c>WAKEUP_ENEMIES</c>, so they must be out
/// of the world until the FIRST Peacemaker squad is wiped out (OBJECTIVE5's <c>DEDG [1, 0]</c>,
/// which naps OBJECTIVE8 awake 15 s later). Drives C3/M05's own roster through the session's
/// <see cref="FlightRoster"/> and its own objective graph, then ranks the woken squad's pick.
/// ⚠ <c>campaign-zeppelin-wakeup</c> covers the zeppelin arm of the same directive; an aircraft
/// roster block is a different consumer and neither suite stands in for the other.</summary>
internal static class CampaignSquadWakeSuites
{
    private const string Chapter = "C3";
    private const string Mission = "M05";

    // The gate and the woken objective, as the mission authors them.
    private const int GateObjective = 5;
    private const int WakeObjective = 8;
    private const float NapSeconds = 15f;

    // OBJECTIVE68, the SET_AI_NET the wake chain naps awake this many seconds after OBJECTIVE8.
    private const float NetSwapSeconds = 2f;
    private const string EscortNet = "M5Escort";

    private const float StepDt = 1f / 60f;

    // The roster group two of the ace's three blocks author (britpeace_8/9; britpeace_7 is group 4).
    private const int AceGroup = 2;
    private const int AceGroupSize = 2;

    // The targeting arm: the block whose list carries both authored terms, a nearer candidate on
    // the player's own side so distance argues against the human, and the mission's airship.
    // The player ring stands past the human's own pull under the decoded rank (the 0.7 base
    // weight and the player fighter's target_bias together are worth about 660 m), so the
    // withheld-list control still reads distance and not that pull.
    private const string BiasBlock = "britpeace_8";
    private const string NearBlock = "devastator_1";
    private const string PirateZep = "piratezep";
    private const float NearRingM = 400f;
    private const float PlayerRingM = 1500f;
    private const float ScanRangeM = 3000f;

    // BL-665: the block a mission script has moved before its wake. britpeace_7's own FindNodes
    // answer is overridden to a pose well clear of its authored spawn, so the same OBJECTIVE5/8
    // wake chain proves WAKEUP_ENEMIES re-places it at that pose rather than its authored one.
    private const string NodeOverrideBlock = "britpeace_7";

    private static readonly string[] Squad = { "britpeace_7", "britpeace_8", "britpeace_9" };
    private static readonly string[] FirstSquad = { "britpeace_1", "britpeace_2", "britpeace_3" };
    private static readonly Vector3 NodeOverrideOffset = new(5000f, 0f, 0f);

    // BL-499: the aircraft arm of the same directive, which the zeppelin arm above does not
    // stand in for. CM02's ace squad is the worked case.
    [Suite("campaign-squad-wakeup",
        "CM02's ace squad over C3/M05's own BUILT world: britpeace_7/8/9 ship deactivated and "
        + "OBJECTIVE8 names exactly them in WAKEUP_ENEMIES, the three are inert and out of play "
        + "at mission start while the first Peacemaker squad flies, wiping group 1 completes "
        + "OBJECTIVE5 and its authored nap puts them into the world 15 s later through the real "
        + "graph, each woken block walks a patrol net, and britpeace_8's authored always-target "
        + "on the player role moves its live pick off a nearer candidate onto the human")]
    internal static void CampaignSquadWakeup(TestContext ctx)
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
        var blocks = AiSkills.LoadRoster(missionZrdr);
        var report = new StringBuilder();
        CheckAuthored(ctx, script, blocks, Zeppelins.Load(missionZrdr), report);

        var skills = AiSkills.Load(ctx.ZrdrPath);
        var director = CampaignDirector.Create(script, mission,
            CampaignProfileDef.NewProfile("Zachary"), null);
        ctx.WithWorld(Chapter, collision: false, Mission, world =>
            Drive(ctx, world, director, blocks, skills, missionZrdr, chapterZrdr, texturesPath, report));

        ctx.WriteArtifact($"test-campaign-squad-wakeup-{Chapter}-{Mission}.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: the ace's squad stays out of the world until OBJECTIVE{GateObjective} wipes group 1");
    }

    [Suite("campaign-roster-wake-node",
        "BL-665 over CM02's own wake chain: britpeace_7's FindNodes answer is overridden to a "
        + "pose 5000 m from its authored spawn, the build itself already honours the override, "
        + "and once OBJECTIVE5's DEDG naps OBJECTIVE8 awake through the real graph the woken "
        + "rig lands on the overridden node rather than snapping back to its authored spawn")]
    internal static void CampaignRosterWakeNode(TestContext ctx)
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
        var blocks = AiSkills.LoadRoster(missionZrdr);
        var report = new StringBuilder();
        var director = CampaignDirector.Create(script, mission,
            CampaignProfileDef.NewProfile("Zachary"), null);
        ctx.WithWorld(Chapter, collision: false, Mission, world =>
            DriveNodeOverride(ctx, world, director, blocks, missionZrdr, chapterZrdr, texturesPath, report));

        ctx.WriteArtifact($"test-campaign-roster-wake-node-{Chapter}-{Mission}.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: '{NodeOverrideBlock}' wakes on the world node its name resolves to, not its authored spawn");
    }

    private static void DriveNodeOverride(TestContext ctx, TestWorld world, CampaignDirector director,
        IReadOnlyList<(string Name, List<object?> Fields)> blocks,
        string missionZrdr, string chapterZrdr, string texturesPath, StringBuilder report)
    {
        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        ProjectilePool? pool = null;
        FlightRoster? roster = null;
        FlightController? player = null;
        Node3D? overrideNode = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            roster = Spawner(ctx, planesGamez, textures, live);

            var pose = PlayerPose(blocks);
            player = HumanRig(ctx, planesGamez, textures, live, pose.Position, pose.Position + pose.Forward);
            var human = player;

            // The block's own authored spawn, read the same way the roster plan would with no
            // override, so the placed pose asserted below is provably somewhere else.
            if (Fields(blocks, NodeOverrideBlock) is not { } fields
                || AiSkills.RosterSpawnPose(fields) is not { } authoredPose)
            {
                throw new SuiteSkippedException($"{Chapter}/{Mission} does not author '{NodeOverrideBlock}'");
            }
            Vector3 authored = authoredPose.Position;
            Vector3 placed = authored + NodeOverrideOffset;

            overrideNode = new Node3D();
            ctx.Host.AddChild(overrideNode);
            overrideNode.GlobalPosition = placed;

            string what = director.BuildRoster(new CampaignDirector.RosterInputs
            {
                ChapterZrdrPath = chapterZrdr,
                MissionZrdrPath = missionZrdr,
                ZrdrPath = ctx.ZrdrPath,
                MinAiActiveDist = AiSkills.Load(ctx.ZrdrPath).MinAiActiveDist,
                Player = () => human,
                NetTrailers = new NetTrailerTargets(
                    () => human.WorldPosition,
                    name => world.Runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null),
                FindNodes = name => name.Equals(NodeOverrideBlock, StringComparison.OrdinalIgnoreCase)
                    ? new Node3D[] { overrideNode }
                    : world.Runtime.FindNodes(name),
                Spawn = (plan, pos, look, pilot) => roster!.SpawnAi(
                    CampaignRosterPlan.SpawnFor(plan, pos, look, pilot)),
                Rng = new Random(1),
            });
            report.AppendLine($"build summary suffix: '{what}'");

            var rigs = director.Roster;
            ctx.Check(rigs.TryGetValue(NodeOverrideBlock, out var rig) && rig.Inert,
                $"'{NodeOverrideBlock}' spawns inert, deactivated by CM02's own roster");
            if (rig == null)
            {
                return;
            }
            report.AppendLine($"spawn: '{NodeOverrideBlock}' authored={authored} placed={placed} actual={rig.WorldPosition}");
            ctx.Check(rig.WorldPosition.DistanceTo(placed) < 1f,
                $"the spawn itself already honours the overridden node: {rig.WorldPosition} vs {placed}");

            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Sounds = world.Runtime.Sounds,
                Projectiles = live,
                ListenerPosition = () => human.WorldPosition,
                PlayerAircraft = () => human,
                Rng = new Random(1),
            });

            var graph = director.Graph;
            ctx.Check(graph != null, $"the world phase armed the objective graph");
            if (graph == null)
            {
                return;
            }

            foreach (var name in FirstSquad)
            {
                if (rigs.TryGetValue(name, out var first))
                {
                    first.DebugForceCrash();
                }
            }

            float woke = -1f, elapsed = 0f;
            float limit = NapSeconds * 3f;
            while (elapsed < limit && woke < 0f)
            {
                director.Step(StepDt);
                elapsed += StepDt;
                if (rig.InPlay)
                {
                    woke = elapsed;
                }
            }
            report.AppendLine($"wake: '{NodeOverrideBlock}' woke {woke:0.00} s after group 1 went down, at {rig.WorldPosition}");
            ctx.Check(woke > 0f, $"'{NodeOverrideBlock}' is woken through the real graph, {woke:0.00} s in");
            ctx.Check(rig.WorldPosition.DistanceTo(placed) < 1f,
                $"the wake re-places '{NodeOverrideBlock}' on the overridden node, not its authored spawn: {rig.WorldPosition} vs {placed}");
            ctx.Check(rig.WorldPosition.DistanceTo(authored) > NodeOverrideOffset.Length() - 1f,
                $"…which is confirmed clear of the authored spawn itself: {rig.WorldPosition} vs authored {authored}");
        }
        finally
        {
            player?.Free();
            var members = new List<FlightController>(
                roster?.AiAircraft ?? Array.Empty<FlightController>());
            roster?.ClearMembership();
            foreach (var r in members)
            {
                r.Free();
            }
            overrideNode?.Free();
            pool?.Free();
            textures.Dispose();
        }
    }

    // The authored shape this suite consumes, read off the shipped files rather than restated: the
    // three blocks are deactivated, OBJECTIVE8 names exactly them, and the gate is group 1's DEDG.
    private static void CheckAuthored(TestContext ctx, ObjectiveScript script,
        IReadOnlyList<(string Name, List<object?> Fields)> blocks,
        IReadOnlyList<ZeppelinDef> zeppelins, StringBuilder report)
    {
        int deactivated = 0, live = 0;
        foreach (var name in Squad)
        {
            deactivated += Fields(blocks, name) is { } f && AiSkills.RosterDeactivated(f) ? 1 : 0;
        }
        foreach (var name in FirstSquad)
        {
            live += Fields(blocks, name) is { } f && !AiSkills.RosterDeactivated(f) ? 1 : 0;
        }
        ctx.Same(3, deactivated, $"the ace's squad ships deactivated on all three blocks");
        ctx.Same(3, live, $"…while the first Peacemaker squad ships live on all three");

        var wake = ObjectiveNumbered(script, WakeObjective);
        var gate = ObjectiveNumbered(script, GateObjective);
        ctx.Check(wake != null && wake.BeginDormant && SameSet(wake.WakeupEnemies, Squad),
            $"OBJECTIVE{WakeObjective} is dormant and its WAKEUP_ENEMIES is exactly the ace's squad");
        ctx.Check(gate?.Dedg is { Group: 1, Max: 0 },
            $"OBJECTIVE{GateObjective} completes on group 1 reaching zero, which is the first squad");
        ctx.Check(gate?.NapWhenComplete is { Target: WakeObjective } nap
                  && Mathf.IsEqualApprox(nap.Seconds, NapSeconds),
            $"…and it naps OBJECTIVE{WakeObjective} awake {NapSeconds:0} s later");

        foreach (var name in Squad)
        {
            IReadOnlyList<AiRatingBias> biases = Fields(blocks, name) is { } f
                ? AiSkills.RosterRatingBiases(f) : Array.Empty<AiRatingBias>();
            var parts = new List<string>();
            foreach (var b in biases)
            {
                parts.Add($"{b.Pattern}:{b.Bias:0.0#}");
            }
            report.AppendLine($"{name}: biases [{string.Join(", ", parts)}]");
        }

        // ⚠ The zeppelin the same list deprioritises authors no team, so its pools take the neutral
        // fall-through BL-407 owns and the gunner refuses them outright. The -0.8 arm is therefore
        // unreachable here for a reason that is not the bias.
        ZeppelinDef? pirate = null;
        foreach (var def in zeppelins)
        {
            if (def.Node.Equals(PirateZep, StringComparison.OrdinalIgnoreCase))
            {
                pirate = def;
            }
        }
        ctx.Check(pirate != null && ZeppelinRuntime.AuthoredTeam(pirate) == null,
            $"C3/M05's '{PirateZep}' record authors no team, so nothing in this session can target it");
        report.AppendLine($"{PirateZep}: team {pirate?.Team ?? "-"} (unauthored means neutral, and neutral is nobody's target)");
    }

    private static void Drive(TestContext ctx, TestWorld world, CampaignDirector director,
        IReadOnlyList<(string Name, List<object?> Fields)> blocks, AiSkills skills,
        string missionZrdr, string chapterZrdr, string texturesPath, StringBuilder report)
    {
        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        ProjectilePool? pool = null;
        FlightRoster? roster = null;
        FlightController? player = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            roster = Spawner(ctx, planesGamez, textures, live);

            var pose = PlayerPose(blocks);
            player = HumanRig(ctx, planesGamez, textures, live, pose.Position, pose.Position + pose.Forward);
            var human = player;

            string what = director.BuildRoster(new CampaignDirector.RosterInputs
            {
                ChapterZrdrPath = chapterZrdr,
                MissionZrdrPath = missionZrdr,
                ZrdrPath = ctx.ZrdrPath,
                MinAiActiveDist = skills.MinAiActiveDist,
                Player = () => human,
                NetTrailers = new NetTrailerTargets(
                    () => human.WorldPosition,
                    name => world.Runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null),
                FindNodes = name => world.Runtime.FindNodes(name),
                // The production spawn, and nothing else: no harness may set Inert itself, because
                // whether the flag survives the assembler is the whole question here.
                Spawn = (plan, pos, look, pilot) => roster!.SpawnAi(
                    CampaignRosterPlan.SpawnFor(plan, pos, look, pilot)),
                Rng = new Random(1),
            });
            report.AppendLine($"build summary suffix: '{what}'");

            var rigs = director.Roster;
            CheckAsleep(ctx, rigs, report);

            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Sounds = world.Runtime.Sounds,
                Projectiles = live,
                ListenerPosition = () => human.WorldPosition,
                PlayerAircraft = () => human,
                Rng = new Random(1),
            });
            WakeThroughTheGraph(ctx, director, rigs, report);
            CheckPick(ctx, rigs, human, live, report);
        }
        finally
        {
            player?.Free();
            var members = new List<FlightController>(
                roster?.AiAircraft ?? Array.Empty<FlightController>());
            roster?.ClearMembership();
            foreach (var rig in members)
            {
                rig.Free();
            }
            pool?.Free();
            textures.Dispose();
        }
    }

    // Mission start: the three are built and out of the world, and the first squad is flying.
    private static void CheckAsleep(TestContext ctx,
        IReadOnlyDictionary<string, FlightController> rigs, StringBuilder report)
    {
        int asleep = 0, flying = 0;
        foreach (var name in Squad)
        {
            bool out_ = rigs.TryGetValue(name, out var rig) && rig.Inert && !rig.InPlay;
            asleep += out_ ? 1 : 0;
            report.AppendLine($"start: {name} inert={rigs.TryGetValue(name, out var r) && r.Inert} "
                + $"inPlay={rigs.TryGetValue(name, out var r2) && r2.InPlay}");
        }
        foreach (var name in FirstSquad)
        {
            flying += rigs.TryGetValue(name, out var rig) && rig.InPlay ? 1 : 0;
        }
        ctx.Same(3, asleep, $"the ace's squad is out of the world at mission start");
        ctx.Same(3, flying, $"…while the first Peacemaker squad is in it");
    }

    // The gate, driven: group 1 is wiped out, OBJECTIVE5 completes, and the nap it authors puts
    // the squad in the world 15 s later and not before.
    private static void WakeThroughTheGraph(TestContext ctx, CampaignDirector director,
        IReadOnlyDictionary<string, FlightController> rigs, StringBuilder report)
    {
        var graph = director.Graph;
        ctx.Check(graph != null, $"the world phase armed the objective graph");
        if (graph == null)
        {
            return;
        }

        foreach (var name in FirstSquad)
        {
            if (rigs.TryGetValue(name, out var rig))
            {
                rig.DebugForceCrash();
            }
        }
        ctx.Check(!rigs[FirstSquad[0]].InPlay, $"group 1 is down: '{FirstSquad[0]}' is no longer in play");

        // A deactivated block is dead to DEDG (the original's deactivate primitive sets the
        // dead byte too), so the ace's two group-2 blocks count zero while parked and two once woken.
        int? parked = director.GroupLiveCount(AceGroup);
        report.AppendLine($"dedg: group {AceGroup} counts {parked?.ToString() ?? "-"} while the squad is parked");
        ctx.Same(0, parked ?? -1, $"DEDG counts no member of group {AceGroup} while its two blocks are deactivated");

        float woke = -1f, elapsed = 0f;
        float limit = NapSeconds * 3f;
        while (elapsed < limit && woke < 0f)
        {
            director.Step(StepDt);
            elapsed += StepDt;
            if (InPlayCount(rigs) == Squad.Length)
            {
                woke = elapsed;
            }
        }
        report.AppendLine($"wake: the squad is in the world {woke:0.00} s after group 1 went down");
        ctx.Check(woke > 0f, $"the squad is woken through the real graph, {woke:0.00} s in");
        ctx.Check(woke >= NapSeconds,
            $"…and not before the authored {NapSeconds:0} s nap: {woke:0.00} s");
        ctx.Check(woke < NapSeconds + 1f,
            $"…arriving on that nap rather than some later objective: {woke:0.00} s");
        int? awake = director.GroupLiveCount(AceGroup);
        report.AppendLine($"dedg: group {AceGroup} counts {awake?.ToString() ?? "-"} once the squad is woken");
        ctx.Same(AceGroupSize, awake ?? -1, $"…and counts both group-{AceGroup} blocks once WAKEUP_ENEMIES has put them in play");

        // Past OBJECTIVE68, the SET_AI_NET the wake chain naps awake two seconds later. The net the
        // squad is left flying is what decides where it goes, so it is measured rather than assumed.
        for (int i = 0; i < (int)(NetSwapSeconds * 2f / StepDt); i++)
        {
            director.Step(StepDt);
        }
        foreach (var name in Squad)
        {
            string net = rigs.TryGetValue(name, out var rig) && rig.Pilot?.Patrol is { } patrol
                ? $"{patrol.Net.Name}#{patrol.Net.Id}" : "-";
            report.AppendLine($"net: {name} flies '{net}' once OBJECTIVE68 has fired");
        }
        int netted = 0, onEscort = 0;
        foreach (var name in Squad)
        {
            var patrol = rigs.TryGetValue(name, out var rig) ? rig.Pilot?.Patrol : null;
            netted += patrol != null ? 1 : 0;
            onEscort += patrol?.Net.Name.Equals(EscortNet, StringComparison.OrdinalIgnoreCase) == true
                ? 1 : 0;
        }
        ctx.Same(3, netted, $"each of the woken three walks a patrol net after the wake");
        ctx.Same(3, onEscort, $"…and OBJECTIVE68's SET_AI_NET has moved all three onto '{EscortNet}'");
    }

    // The second half of the report: the woken squad's live pick. Its own always-target on the
    // 'player' role must beat a nearer candidate of the same side, or the wake puts three enemies
    // in the world that never come for the player at all.
    private static void CheckPick(TestContext ctx, IReadOnlyDictionary<string, FlightController> rigs,
        FlightController human, ProjectilePool live, StringBuilder report)
    {
        if (!rigs.TryGetValue(BiasBlock, out var shooter) || shooter.Pilot?.Gunner is not { } gunner
            || !rigs.TryGetValue(NearBlock, out var near))
        {
            return;
        }

        var origin = new Vector3(0f, 800f, 0f);
        var fwd = Vector3.Forward;
        human.Name = "player1";
        human.Held = true;
        shooter.Held = true;
        near.Held = true;
        void Park()
        {
            shooter.PlaceHeld(origin, origin + fwd);
            near.PlaceHeld(origin + (fwd * NearRingM), origin + (fwd * (NearRingM + 1f)));
            human.PlaceHeld(origin + (fwd * PlayerRingM), origin + (fwd * (PlayerRingM + 1f)));
        }

        if (shooter.Pilot.Machine is { } machine)
        {
            // The picker's reach is the ATTACK radius (docs/org/aiPilot.md); the activation one
            // goes with it so the pursue entry gate agrees with what the ranking admits.
            machine.AttackRange = ScanRangeM;
            machine.ActivationRange = ScanRangeM;
        }
        ctx.Check(near.Team == human.Team && shooter.Team != human.Team,
            $"the near candidate is on the player's side and the shooter is not: shooter={shooter.Team} near={near.Team} human={human.Team}");

        var authored = gunner.RatingBiases;
        FlightController? Acquire(IReadOnlyList<AiRatingBias>? biases)
        {
            Park();
            gunner.AutoTarget = true;
            gunner.RatingBiases = biases;
            gunner.Target = null;
            shooter.SimStep(StepDt);
            live.SimStep(StepDt);
            return gunner.Target as FlightController;
        }

        var control = Acquire(null);
        ctx.Check(ReferenceEquals(control, near),
            $"with the authored list withheld the nearer '{NearBlock}' at {NearRingM:0} m is the pick: {Label(control)}");
        var biased = Acquire(authored);
        report.AppendLine($"pick: control={Label(control)} biased={Label(biased)}");
        ctx.Check(ReferenceEquals(biased, human),
            $"'{BiasBlock}'s authored always-target on the player role moves the pick to the human at {PlayerRingM:0} m: {Label(biased)}");

        // The other arm of the same list, which cannot land in this mission for a reason that is
        // not the bias: the pattern reaches a zone the hull owns, but C3/M05's record authors no
        // team, so its pools fall through to neutral and are refused as candidates outright.
        float owned = AiTargetRanking.ObjectiveBiasFor("gasbag1", new[] { PirateZep }, authored);
        // Rank is minimised, so a positive term is the penalty and a hard exclusion is NotRanked.
        ctx.Check(owned > 0f && owned < AiTargetRanking.NotRanked,
            $"the '{PirateZep}' arm reaches a zone that hull owns as a penalty rather than an exclusion: {owned:0.##}");
        report.AppendLine($"bias reach: a '{PirateZep}' zone scores {owned:0.##}");
    }

    private static string Label(object? pick) =>
        pick is FlightController fc ? fc.Name.ToString() : pick?.ToString() ?? "none";

    private static int InPlayCount(IReadOnlyDictionary<string, FlightController> rigs)
    {
        int n = 0;
        foreach (var name in Squad)
        {
            n += rigs.TryGetValue(name, out var rig) && rig.InPlay ? 1 : 0;
        }
        return n;
    }

    private static ObjectiveDef? ObjectiveNumbered(ObjectiveScript script, int number) =>
        number >= 1 && number <= script.Objectives.Count ? script.Objectives[number - 1] : null;

    private static bool SameSet(IReadOnlyList<string> got, IReadOnlyList<string> wanted)
    {
        if (got.Count != wanted.Count)
        {
            return false;
        }
        foreach (var w in wanted)
        {
            bool hit = false;
            foreach (var g in got)
            {
                hit |= g.Equals(w, StringComparison.OrdinalIgnoreCase);
            }
            if (!hit)
            {
                return false;
            }
        }
        return true;
    }

    private static List<object?>? Fields(
        IReadOnlyList<(string Name, List<object?> Fields)> blocks, string name)
    {
        foreach (var (block, fields) in blocks)
        {
            if (block.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return fields;
            }
        }
        return null;
    }

    private static (Vector3 Position, Vector3 Forward) PlayerPose(
        IReadOnlyList<(string Name, List<object?> Fields)> blocks)
    {
        if (Fields(blocks, CampaignRosterPlan.PlayerBlock) is { } fields
            && AiSkills.RosterSpawnPose(fields) is { } pose)
        {
            return (pose.Position, new Basis(Vector3.Up, Mathf.DegToRad(pose.YawDeg)) * Vector3.Forward);
        }
        return (new Vector3(0f, 800f, 0f), Vector3.Forward);
    }

    // The human rig this suite flies nothing with: it exists so the leader pass has a player and
    // the ranking has a human to resolve the authored 'player' role against.
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
        rig.Setup(new FlightModel(stats), null, new CamParams(), pos, lookAt);
        rig.Name = "player1";
        ctx.Host.AddChild(rig);
        return rig;
    }

    // The session's own AI spawner with no world effects, the shape CampaignRosterSuites uses.
    private static FlightRoster Spawner(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        ProjectilePool live)
    {
        var spec = SessionSpec.Parse(Array.Empty<string>());
        var resources = new AircraftAssemblyResources
        {
            PlanesGamez = planesGamez,
            StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
            AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
            PaintRng = new RandomNumberGenerator(),
            ZrdrPath = ctx.ZrdrPath,
            StockLoadouts = StockLoadouts.Load(),
            WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
            WeaponMessages = Messages.Load(ctx.MessagesPath),
            Textures = textures,
            Shakes = ShakeDefs.Load(ctx.ZrdrPath),
        };
        return new FlightRoster(FlightRosterPolicy.From(spec),
            new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
            null!, ctx.Host, resources,
            new FlightWorldBindings { Projectiles = live, Gamez = planesGamez },
            new HumanRosterBindings());
    }
}
