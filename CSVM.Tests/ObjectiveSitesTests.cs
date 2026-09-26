using System.Collections.Generic;
using System.IO;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Objectives;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// <see cref="ObjectiveSites.CollectFlagged"/>'s two site sources: <c>targets.zrd</c>'s own
/// entries carrying the pass's flag and the graph's matching <c>ADD_</c> edits, each dropped once
/// a completed objective's <c>REMOVE_</c> names its key; the <see cref="TargetFlag.OtherTarget"/>
/// pass's curated list, which puts a mission's chosen structures on the Non-Aircraft cycle while
/// leaving every unflagged entry off every cycle; the director-free split both passes make over
/// the shipped Instant Action and multiplayer tables, where nothing edits either half; the
/// <c>TRAVELERS</c> point <c>PointFor</c> reads for a key on either flag's list; and the
/// resolved-node destroyed gate <see cref="ObjectiveSites.LiveDespiteState"/>. A site is offered
/// from mission start with the graph's store still empty; a roster block's own flag is NOT
/// collected here, riding its aircraft.
/// </summary>
public class ObjectiveSitesTests
{
    [Fact]
    public void ATargetsZrdFlaggedEntryIsOfferedFromMissionStart()
    {
        var script = Script("\"OBJECTIVE1\",[\"BEGIN_DORMANT\",[-1.0],\"INACTIVE1\",[\"never\"]]");
        var graph = new ObjectiveGraph(script, new FakeWorld());
        var into = new List<string>();

        Assert.Empty(graph.ObjectiveTargets);
        ObjectiveSites.CollectFlagged(TargetFlag.Objective, script, graph,
            TargetsWithObjectiveFlag("rfspt1"), into);

        Assert.Contains("rfspt1", into);
    }

    [Fact]
    public void AFlaggedSiteIsDroppedOnceItsOwnKeyIsRemovedByACompletedObjective()
    {
        var script = Script("\"OBJECTIVE1\",[\"REMOVE_OBJECTIVE_TARGET\",[\"rfspt1\"]]");
        var graph = new ObjectiveGraph(script, new FakeWorld());
        var targets = TargetsWithObjectiveFlag("rfspt1");

        var before = new List<string>();
        ObjectiveSites.CollectFlagged(TargetFlag.Objective, script, graph, targets, before);
        Assert.Contains("rfspt1", before);

        // OBJECTIVE1 is conditionless, so it completes on the first tick, the way a shipped
        // primary does once its gate reads true.
        graph.Step(0.1f);
        Assert.True(graph.CompletedOf(1));

        var after = new List<string>();
        ObjectiveSites.CollectFlagged(TargetFlag.Objective, script, graph, targets, after);
        Assert.DoesNotContain("rfspt1", after);
    }

    [Fact]
    public void ASiteTheGraphAddsIsOfferedAndStillRemovable()
    {
        var script = Script(
            "\"OBJECTIVE1\",[\"ADD_OBJECTIVE_TARGET\",[\"caboose_polys\"]],"
            + "\"OBJECTIVE2\",[\"BEGIN_DORMANT\",[-1.0],\"INACTIVE1\",[\"never\"]]");
        var graph = new ObjectiveGraph(script, new FakeWorld());

        graph.Step(0.1f);
        var into = new List<string>();
        ObjectiveSites.CollectFlagged(TargetFlag.Objective, script, graph, new MissionTargets(), into);
        Assert.Contains("caboose_polys", into);
    }

    [Fact]
    public void AGraphAddDoesNotDuplicateATargetsZrdEntryOfTheSameKey()
    {
        var script = Script("\"OBJECTIVE1\",[\"ADD_OBJECTIVE_TARGET\",[\"rfspt1\"]]");
        var graph = new ObjectiveGraph(script, new FakeWorld());
        graph.Step(0.1f);

        var into = new List<string>();
        ObjectiveSites.CollectFlagged(TargetFlag.Objective, script, graph,
            TargetsWithObjectiveFlag("rfspt1"), into);

        Assert.Single(into, key => key == "rfspt1");
    }

    [Fact]
    public void ASiteWhoseResolvedNodeReadsDestroyedIsNoLongerLive()
    {
        // CM21's six rfspt*/lfspt* Destroy Support Beam sites: their own REMOVE_OBJECTIVE_TARGET
        // fires per-beam on that beam's own objective, not on a kill, so a beam shot down before
        // its objective completes must leave the cycle on the destroyed read alone.
        Assert.False(ObjectiveSites.LiveDespiteState(DestructibleRegistry.State.Destroyed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(DestructibleRegistry.State.Healthy)]
    [InlineData(DestructibleRegistry.State.Damaged)]
    public void ASiteNotReadDestroyedStaysLive(DestructibleRegistry.State? state)
    {
        Assert.True(ObjectiveSites.LiveDespiteState(state));
    }

    [Fact]
    public void AnOtherTargetEntryIsCollectedForTheNonAircraftCycle()
    {
        var script = Script("\"OBJECTIVE1\",[\"BEGIN_DORMANT\",[-1.0],\"INACTIVE1\",[\"never\"]]");
        var graph = new ObjectiveGraph(script, new FakeWorld());
        var into = new List<string>();

        ObjectiveSites.CollectFlagged(TargetFlag.Objective, script, graph,
            TargetsWithFlag("g_tower1", "other_target"), into);
        Assert.Empty(into);
        ObjectiveSites.CollectFlagged(TargetFlag.OtherTarget, script, graph,
            TargetsWithFlag("g_tower1", "other_target"), into);

        Assert.Contains("g_tower1", into);
    }

    [Fact]
    public void AnUnflaggedTableEntryIsCollectedByNeitherPass()
    {
        // The whole point of the curated list: a mission names plenty of nodes it does not flag,
        // and those reach no cycle. Reading the table's keys alone would put them all on one.
        var script = Script("\"OBJECTIVE1\",[\"BEGIN_DORMANT\",[-1.0],\"INACTIVE1\",[\"never\"]]");
        var graph = new ObjectiveGraph(script, new FakeWorld());
        var targets = Labelled("hydrogentank1");
        var into = new List<string>();

        ObjectiveSites.CollectFlagged(TargetFlag.Objective, script, graph, targets, into);
        ObjectiveSites.CollectFlagged(TargetFlag.OtherTarget, script, graph, targets, into);

        Assert.Empty(into);
    }

    [Fact]
    public void AnOtherTargetSiteIsDroppedOnceACompletedObjectiveRemovesIt()
    {
        var script = Script("\"OBJECTIVE1\",[\"REMOVE_OTHER_TARGET\",[\"trcargo01\"]]");
        var graph = new ObjectiveGraph(script, new FakeWorld());
        var targets = TargetsWithFlag("trcargo01", "other_target");

        var before = new List<string>();
        ObjectiveSites.CollectFlagged(TargetFlag.OtherTarget, script, graph, targets, before);
        Assert.Contains("trcargo01", before);

        graph.Step(0.1f);
        Assert.True(graph.CompletedOf(1));

        var after = new List<string>();
        ObjectiveSites.CollectFlagged(TargetFlag.OtherTarget, script, graph, targets, after);
        Assert.DoesNotContain("trcargo01", after);
    }

    [Fact]
    public void AKeyOnBothFlagsIsOfferedOnceAsAnObjective()
    {
        // The class filter tests the objective byte first, so a key carrying both is an objective
        // and must not be offered a second time on the Non-Aircraft cycle.
        var script = Script("\"OBJECTIVE1\",[\"ADD_OTHER_TARGET\",[\"rfspt1\"]]");
        var graph = new ObjectiveGraph(script, new FakeWorld());
        graph.Step(0.1f);
        var targets = TargetsWithFlag("rfspt1", "objective");

        var into = new List<string>();
        ObjectiveSites.CollectFlagged(TargetFlag.Objective, script, graph, targets, into);
        int objectives = into.Count;
        ObjectiveSites.CollectFlagged(TargetFlag.OtherTarget, script, graph, targets, into);

        Assert.Equal(1, objectives);
        Assert.Single(into, key => key == "rfspt1");
    }

    [Fact]
    public void AnOtherTargetKeyReadsItsObjectivesTravelersPoint()
    {
        // The flag picks the cycle, not the place, so a site an ADD_OTHER_TARGET names has to reach
        // the same point an ADD_OBJECTIVE_TARGET one does. Matching only the objective keys drops
        // such a site onto its node bounds, which C3/M01's village shows is kilometres away.
        var script = Script(
            "\"OBJECTIVE1\",[\"TRAVELERS\",[\"player\",\"APPROACHING\",[-5458.0,120.0,-5390.0],1100.0],"
            + "\"ADD_OTHER_TARGET\",[\"t_truck01\"]]");

        Assert.Equal(new Vector3(-5458f, 120f, -5390f), ObjectiveSites.PointFor(script, "t_truck01"));
    }

    [Fact]
    public void TheDirectorFreeFeedSplitsTheTableOnItsOwnFlagsAlone()
    {
        // Instant Action and the multiplayer modes run no director, so both passes take a null
        // script and graph and the table's own keys are the whole answer.
        var targets = Flagged(("zep", "objective"), ("rearm", "other_target"), ("crate", null));

        var into = new List<string>();
        ObjectiveSites.CollectFlagged(TargetFlag.Objective, null, null, targets, into);
        int objectives = into.Count;
        ObjectiveSites.CollectFlagged(TargetFlag.OtherTarget, null, null, targets, into);

        Assert.Equal(1, objectives);
        Assert.Equal(new[] { "zep", "rearm" }, into);
    }

    [Fact]
    public void TheDirectorFreeFeedStillOffersAKeyOnBothFlagsOnce()
    {
        var dir = TestData.TempDir();
        File.WriteAllText(Path.Combine(dir, "targets.json"),
            "[[[\"nodes\",[\"both\"]],[\"objective\"],[\"other_target\"]]]");
        var targets = MissionTargets.Load(dir);

        var into = new List<string>();
        ObjectiveSites.CollectFlagged(TargetFlag.Objective, null, null, targets, into);
        int objectives = into.Count;
        ObjectiveSites.CollectFlagged(TargetFlag.OtherTarget, null, null, targets, into);

        Assert.Equal(1, objectives);
        Assert.Single(into);
    }

    [ExtractedDataTheory]
    [InlineData("C1")]
    [InlineData("C1C")]
    [InlineData("C2")]
    [InlineData("C2B")]
    [InlineData("C3")]
    [InlineData("C4")]
    public void AnInstantActionTableOffersTheRadioTowerAndNothingElse(string chapter)
    {
        // Every IA1 table names the chapter's zeppelin and its danger zones without a flag, and
        // flags the radio tower `other_target` alone, so the Non-Aircraft cycle gets one entry.
        var (objectives, others) = Split(chapter, "IA1");

        Assert.Empty(objectives);
        Assert.Equal(new[] { "ap_transmitter" }, others);
    }

    [ExtractedDataTheory]
    [InlineData("C1")]
    [InlineData("C1B")]
    [InlineData("C1C")]
    [InlineData("C2")]
    [InlineData("C2B")]
    [InlineData("C3")]
    [InlineData("C4")]
    [InlineData("C5")]
    public void AZeppelinMatchTableOffersTheTwoRearmBasesBesideTheTwoAirships(string chapter)
    {
        // MP3's four flagged entries: the two airships as objectives on the Enemy cycle and the
        // two rearm bases as other-targets on the Non-Aircraft one.
        var (objectives, others) = Split(chapter, "MP3");

        Assert.Equal(new[] { "multiplayer1zep", "multiplayer2zep" }, objectives);
        Assert.Equal(new[] { "zep_rearm_node_1", "zep_rearm_node_2" }, others);
    }

    // Both passes over a shipped mode table, with no director editing either half.
    private static (List<string> Objectives, List<string> Others) Split(string chapter, string mission)
    {
        var targets = MissionTargets.Load(
            SessionPaths.MissionZrdr(TestData.DataRoot!, chapter, mission),
            SessionPaths.ChapterZrdr(TestData.DataRoot!, chapter));
        var into = new List<string>();
        ObjectiveSites.CollectFlagged(TargetFlag.Objective, null, null, targets, into);
        int objectives = into.Count;
        ObjectiveSites.CollectFlagged(TargetFlag.OtherTarget, null, null, targets, into);
        // Sorted: each half is a set of keys, and the table's own record order is not a claim
        // this test is making.
        var first = into.GetRange(0, objectives);
        var second = into.GetRange(objectives, into.Count - objectives);
        first.Sort(System.StringComparer.OrdinalIgnoreCase);
        second.Sort(System.StringComparer.OrdinalIgnoreCase);
        return (first, second);
    }

    private static MissionTargets Flagged(params (string Node, string? Flag)[] entries)
    {
        var dir = TestData.TempDir();
        var text = new List<string>();
        foreach (var (node, flag) in entries)
        {
            text.Add("[[\"nodes\",[\"" + node + "\"]]"
                + (flag == null ? "" : ",[\"" + flag + "\"]") + "]");
        }

        File.WriteAllText(Path.Combine(dir, "targets.json"), "[" + string.Join(",", text) + "]");
        return MissionTargets.Load(dir);
    }

    private static MissionTargets TargetsWithFlag(string node, string flag)
    {
        var dir = TestData.TempDir();
        File.WriteAllText(Path.Combine(dir, "targets.json"),
            "[[[\"nodes\",[\"" + node + "\"]],[\"" + flag + "\",true]]]");
        return MissionTargets.Load(dir);
    }

    private static MissionTargets Labelled(string node)
    {
        var dir = TestData.TempDir();
        File.WriteAllText(Path.Combine(dir, "targets.json"),
            "[[[\"nodes\",[\"" + node + "\"]],[\"help_label\",\"MSG_OBJ_DESTROY\"]]]");
        return MissionTargets.Load(dir);
    }

    private static MissionTargets TargetsWithObjectiveFlag(string node)
    {
        var dir = TestData.TempDir();
        File.WriteAllText(Path.Combine(dir, "targets.json"),
            "[[[\"nodes\",[\"" + node + "\"]],[\"objective\",true]]]");
        return MissionTargets.Load(dir);
    }

    private static ObjectiveScript Script(string body)
    {
        var dir = TestData.TempDir();
        File.WriteAllText(Path.Combine(dir, "objectives.json"), "[[" + body + "]]");
        return ObjectiveScript.Load(dir);
    }

    // Answers only what a test sets; no condition ever reads true unless the test drives it.
    private sealed class FakeWorld : IObjectiveWorld
    {
        public bool? NodeInactive(IReadOnlyList<string> path) => false;

        public int AnimState(string anim) => 0;

        public int? GroupLiveCount(int group, string? generator) => 0;

        public void WidenGroupEngagement(int group)
        {
        }

        public bool? TravelersMet(TravelersSpec spec) => false;

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
