using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using Godot;

namespace CSVM.Testing;

/// <summary>The suite over the Hollywood race (C2/M03): its first site is a hangar the chapter
/// builds as a group node at the world origin, and its race chain is one DANGER_ZONES_COMPLETED
/// objective per zone, each waking the next, with a DEDG per racer that kills the whole chain the
/// moment that racer dies. Both are pinned against the mission's own shipped files.</summary>
internal static class CampaignRaceSuites
{
    private const string Chapter = "C2";

    private const string Mission = "M03";

    // The first site: a geometry node the first zone runs through, and the point marker of that
    // zone, which the anchor has to land beside.
    private const string HangarNode = "sghangar";

    private const string FirstZonePoint = "dz1";

    // How far the hangar's marker may stand from the zone's own point marker. The hangar body is
    // 260 m long, so the site's anchor and the zone point agree only if the anchor is read off the
    // hangar's parts rather than its node.
    private const float AnchorTolerance = 150f;

    // The racer groups the mission's DEDG objectives watch.
    private const int FirstRacerGroup = 3;

    private const int LastRacerGroup = 8;

    // ScanForCompletion resolves one objective per tick, round robin over the mission's 57
    // blocks, so a zone's completion needs more ticks than one round to be certain to land.
    private const float SettleSeconds = 8f;

    // The three zones flown in order.
    private static readonly string[] Zones = { "dzpath1", "dzpath2", "dzpath3" };

    /// <summary>C2/M03's race, twice over: against the BUILT chapter world the first zone's site
    /// stands on the hangar the zone runs through and not at the world origin its group node
    /// occupies; and headless over the real objective graph, a player flying zones 1, 2 and 3 in
    /// order with every racer alive completes the zone objectives one after another and no
    /// racer-death objective fires, while a racer dying afterwards fires its DEDG objective and
    /// kills the rest of the chain, which is what a rammed racer does to the race.</summary>
    [Suite("campaign-race-chain",
        "the Hollywood race (C2/M03) over its own files: against the BUILT chapter world the "
        + "first site's marker stands on the seaplane hangar the zone runs through (a group "
        + "node at the world origin, its parts carrying the coordinates) beside dz1, and "
        + "headless over the real graph a player flying dzpath1, 2 and 3 in order with every "
        + "racer alive completes OBJECTIVE17, 18 and 19 with no racer-death DEDG firing, while "
        + "a racer dying afterwards fires its DEDG and kills the rest of the chain")]
    internal static void CampaignRaceChain(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var mission = MissionAt(CampaignSequence.Load(ctx.ZrdrPath))
            ?? throw new SuiteSkippedException($"{Chapter}/{Mission} is not in cm_sequence");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, Chapter), $"{Chapter} textures");

        var script = ObjectiveScript.Load(missionZrdr);
        var targets = MissionTargets.Load(missionZrdr, SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter));
        var messages = Messages.Load(ctx.MessagesPath);
        var report = new StringBuilder();
        report.AppendLine($"{Chapter}/{Mission}: {targets.Count} target entries, {script.Objectives.Count} objectives");

        ctx.Check(targets.For(HangarNode).Objective,
            $"targets.zrd flags '{HangarNode}' as the site the mission starts on");
        var chain = ZoneObjectives(script);
        ctx.Same(Zones.Length, chain.Count,
            $"one DANGER_ZONES_COMPLETED objective gates on each of {string.Join(", ", Zones)}");
        var racerDeaths = RacerDeathObjectives(script);
        ctx.Same(LastRacerGroup - FirstRacerGroup + 1, racerDeaths.Count,
            $"a DEDG [group, 0] objective watches each racer group {FirstRacerGroup}..{LastRacerGroup}");
        if (chain.Count != Zones.Length || racerDeaths.Count == 0)
        {
            return;
        }

        var profile = CampaignProfileDef.NewProfile("Zachary");
        var director = CampaignDirector.Create(script, mission, profile, null, missionZrdr);
        ctx.WithWorld(Chapter, collision: false, Mission, world =>
            CheckHangarSite(ctx, world, director, targets, messages, report));

        CheckRaceChain(ctx, script, chain, racerDeaths, report);

        ctx.WriteArtifact($"test-campaign-race-chain-{Chapter}.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: the hangar site stands on the hangar, and three zones flown complete OBJECTIVE{chain[0].Number}, {chain[1].Number} and {chain[2].Number} with no racer-death objective firing");
    }

    // The marker half, over the built world: the site the mission starts with resolves to the
    // hangar's node, and its anchor stands beside the first zone's own point marker.
    private static void CheckHangarSite(TestContext ctx, TestWorld world, CampaignDirector director,
        MissionTargets targets, Messages messages, StringBuilder report)
    {
        var listener = ctx.Camera.GlobalPosition;
        director.Attach(new CampaignDirector.WorldInputs
        {
            Runtime = world.Runtime,
            Gamez = world.Gamez,
            Sounds = world.Runtime.Sounds,
            ListenerPosition = () => listener,
            Rng = new Random(1),
        });
        director.Graph!.Step(0.1f);

        var hangar = ObjectiveSites.ResolveTarget(world.Runtime, ObjectiveTarget.Parse(HangarNode));
        var point = ObjectiveSites.ResolveTarget(world.Runtime, ObjectiveTarget.Parse(FirstZonePoint));
        ctx.Check(hangar != null, $"'{HangarNode}' resolves to a world node");
        ctx.Check(point != null, $"'{FirstZonePoint}' resolves to a world node");
        if (hangar == null || point == null)
        {
            return;
        }

        var anchor = ObjectiveSites.SiteAnchor(hangar);
        report.AppendLine($"'{HangarNode}' node at {hangar.GlobalPosition}, anchor {anchor}, '{FirstZonePoint}' at {point.GlobalPosition}");
        ctx.Check(hangar.GlobalPosition.IsZeroApprox(),
            $"the chapter builds '{HangarNode}' as a group node at the world origin, so its own position is not the site");
        ctx.Check(anchor.DistanceTo(point.GlobalPosition) < AnchorTolerance,
            $"the site anchor stands within {AnchorTolerance:0} m of '{FirstZonePoint}' ({anchor.DistanceTo(point.GlobalPosition):0} m)");

        var sites = new ObjectiveSites(director, messages, targets, world.Runtime);
        var offered = new List<AimCandidate>();
        sites.Collect(offered);
        ObjectiveSite? site = null;
        foreach (var candidate in offered)
        {
            if (candidate.Source is ObjectiveSite s && s.Target.Is(HangarNode))
            {
                site = s;
                report.AppendLine($"offered '{s.Node}' \"{s.DisplayName}\" at {s.Position}");
            }
        }

        ctx.Check(site != null, $"'{HangarNode}' is offered as an objective site before any zone is flown");
        ctx.Check(site != null && site.Position.IsEqualApprox(anchor),
            $"and the site is marked at the hangar's anchor, not at the node's origin");
    }

    // The graph half, headless: every racer alive, the three zones flown in order.
    private static void CheckRaceChain(TestContext ctx, ObjectiveScript script,
        List<ObjectiveDef> chain, List<ObjectiveDef> racerDeaths, StringBuilder report)
    {
        var world = new RacerWorld();
        var graph = new ObjectiveGraph(script, world);
        graph.Step(0.1f);
        ctx.Check(!graph.CompletedOf(chain[0].Number) && graph.StateOf(chain[0].Number) == ObjectiveState.Awake,
            $"OBJECTIVE{chain[0].Number} (zone 1) starts awake and incomplete");

        for (int i = 0; i < Zones.Length; i++)
        {
            graph.NotifyDangerZoneCompleted(Zones[i]);
            Settle(graph);
            ctx.Check(graph.CompletedOf(chain[i].Number),
                $"flying '{Zones[i]}' completes OBJECTIVE{chain[i].Number}");
            if (i + 1 < chain.Count)
            {
                ctx.Check(graph.StateOf(chain[i + 1].Number) == ObjectiveState.Awake,
                    $"and wakes OBJECTIVE{chain[i + 1].Number} for the next zone");
            }

            foreach (var death in racerDeaths)
            {
                ctx.Check(!graph.CompletedOf(death.Number),
                    $"OBJECTIVE{death.Number} (racer group {death.Dedg!.Value.Group} dead) has not fired after '{Zones[i]}'");
            }
        }

        report.AppendLine($"zones 1..3 flown with every racer alive: OBJECTIVE{chain[0].Number}, {chain[1].Number}, {chain[2].Number} completed, no racer-death objective fired");
        ctx.Check(graph.Outcome == MissionOutcome.None, $"and the mission is still running");

        // The race the sortie log showed: one racer dies and its DEDG objective ends the race.
        var first = racerDeaths[0];
        world.Dead.Add(first.Dedg!.Value.Group);
        Settle(graph);
        var next = chain[^1].WakeWhenComplete.Count > 0 ? chain[^1].WakeWhenComplete[0] : 0;
        report.AppendLine($"racer group {first.Dedg.Value.Group} dead: OBJECTIVE{first.Number} completed={graph.CompletedOf(first.Number)}, next zone objective {next} alive={graph.AliveOf(next)}");
        ctx.Check(graph.CompletedOf(first.Number),
            $"a racer of group {first.Dedg.Value.Group} dying fires OBJECTIVE{first.Number}");
        ctx.Check(next > 0 && !graph.AliveOf(next),
            $"which kills the rest of the chain (OBJECTIVE{next}), so no later zone can count");
    }

    private static void Settle(ObjectiveGraph graph)
    {
        for (float t = 0f; t < SettleSeconds; t += 0.1f)
        {
            graph.Step(0.1f);
        }
    }

    private static List<ObjectiveDef> ZoneObjectives(ObjectiveScript script)
    {
        var chain = new List<ObjectiveDef>();
        foreach (var zone in Zones)
        {
            foreach (var def in script.Objectives)
            {
                if (def.DangerZones.Count == 1
                    && string.Equals(def.DangerZones[0], zone, StringComparison.OrdinalIgnoreCase))
                {
                    chain.Add(def);
                    break;
                }
            }
        }

        return chain;
    }

    // The awake-from-start DEDG [group, 0] objectives over the racer groups, in group order.
    private static List<ObjectiveDef> RacerDeathObjectives(ObjectiveScript script)
    {
        var found = new List<ObjectiveDef>();
        for (int group = FirstRacerGroup; group <= LastRacerGroup; group++)
        {
            foreach (var def in script.Objectives)
            {
                if (!def.BeginDormant && def.Dedg is { Max: 0 } dedg && dedg.Group == group)
                {
                    found.Add(def);
                    break;
                }
            }
        }

        return found;
    }

    private static CampaignMission? MissionAt(IReadOnlyList<CampaignMission> missions)
    {
        foreach (var m in missions)
        {
            if (m.ChapterFolder.Equals(Chapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(Mission, StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }

        return null;
    }

    // The graph's world seam with a roster of one live member per group: DEDG reads a group as
    // wiped out only once the suite puts it in Dead. Everything else answers as an engine with
    // no world does, and every action is a no-op.
    private sealed class RacerWorld : IObjectiveWorld
    {
        public HashSet<int> Dead { get; } = new();

        public bool? NodeInactive(IReadOnlyList<string> path) => null;

        public int AnimState(string anim) => 0;

        public int? GroupLiveCount(int group, string? generator) => Dead.Contains(group) ? 0 : 1;

        public void WidenGroupEngagement(int group)
        {
        }

        public bool? TravelersMet(TravelersSpec spec) => null;

        public void WakeupEnemies(IReadOnlyList<string> names)
        {
        }

        public void WakeupTurrets(IReadOnlyList<string> patterns)
        {
        }

        public void WakeupZepTurrets(IReadOnlyList<string> nodes)
        {
        }

        public void WakeupGenerator(string name, int count)
        {
        }

        public void WakeAnim(string anim, string? node)
        {
        }

        public void PlaySoundGroup(string group)
        {
        }

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
}
