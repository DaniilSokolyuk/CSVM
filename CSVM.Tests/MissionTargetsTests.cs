using System.IO;
using CSVM.Flight.Weapons;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The mission <c>targets.json</c> reader (<c>docs/formats/missions.md</c>): a list of entries,
/// each a list of <c>[key, value]</c> pairs, not the flat alternating reader form.
/// Input is <c>fixtures/zrdr/targets.json</c>.
/// </summary>
public class MissionTargetsTests
{
    [Fact]
    public void OneEntryLabelsEveryNodeItLists()
    {
        var targets = Load();
        Assert.Equal(4, targets.Count); // two nodes from the first entry, one each from the second and fourth
        var zone = targets.For("probe_dz1");
        Assert.Equal("MSG_PROBE_ZONE", zone.Description);
        Assert.Equal("MSG_PROBE_DZ", zone.CategoryLabel);
        Assert.Equal("MSG_PROBE_FLYTHROUGH", zone.HelpLabel);
        Assert.Equal(zone, targets.For("probe_dz1_inner"));
    }

    [Fact]
    public void AnAbsentLabelStaysNullRatherThanBecomingEmptyText()
    {
        var point = Load().For("probe_point");
        Assert.Equal("MSG_PROBE_POINT", point.Description);
        Assert.Null(point.CategoryLabel);
    }

    [Fact]
    public void AnEntryWithNoNodesLabelsNothing()
    {
        // The fixture's third entry has a description but no "nodes" list, so it contributes
        // no mapping, 4 nodes from 4 entries, not 5.
        Assert.Equal(4, Load().Count);
    }

    [Fact]
    public void ANestedNodeListIsOnePathKeyedLikeTheScriptsTargets()
    {
        // [probe_tail, probe_hook] is the docking hook under the tail, the shape
        // ADD_OBJECTIVE_TARGET [[wv_tailhook, peoplehook]] names; the two tables meet on one key.
        var targets = Load();
        var hook = targets.For("probe_tail/probe_hook");
        Assert.Equal("MSG_PROBE_HOOK", hook.Description);
        Assert.Equal("MSG_PROBE_DOCK", hook.HelpLabel);
        Assert.Null(targets.For("probe_hook").Description);
        Assert.Null(targets.For("probe_tail").Description);
    }

    [Fact]
    public void AnUnlabelledNodeGivesAnAllNullTargetNotAThrow()
    {
        var missing = Load().For("probe_not_a_target");
        Assert.Null(missing.Description);
        Assert.Null(missing.CategoryLabel);
        Assert.Null(missing.HelpLabel);
    }

    [Fact]
    public void TheTwoValuelessFlagKeysAreReadIndependently()
    {
        // `objective` picks the Enemy cycle and `other_target` the Non-Aircraft one, so a reader
        // that folded them together would put every flagged structure on one cycle.
        var dir = TestData.TempDir();
        File.WriteAllText(Path.Combine(dir, "targets.json"),
            "[[[\"nodes\",[\"only_other\"]],[\"other_target\"]],"
            + "[[\"nodes\",[\"only_objective\"]],[\"objective\"]],"
            + "[[\"nodes\",[\"both\"]],[\"other_target\"],[\"objective\"]],"
            + "[[\"nodes\",[\"neither\"]],[\"description\",\"MSG_PLAIN\"]]]");
        var targets = MissionTargets.Load(dir);

        Assert.True(targets.For("only_other").OtherTarget);
        Assert.False(targets.For("only_other").Objective);
        Assert.True(targets.For("only_objective").Objective);
        Assert.False(targets.For("only_objective").OtherTarget);
        Assert.True(targets.For("both").OtherTarget && targets.For("both").Objective);
        Assert.False(targets.For("neither").OtherTarget || targets.For("neither").Objective);
    }

    [Fact]
    public void AFlaggedPathEntryKeepsTheFlagOnItsPathKey()
    {
        // C3/M01's own `other_target` entry: the curated list's whole answer for that mission is
        // one nested [piratezep, rock_zeppelin], and the bare child name must carry nothing.
        var dir = TestData.TempDir();
        File.WriteAllText(Path.Combine(dir, "targets.json"),
            "[[[\"nodes\",[[\"probe_hull\",\"probe_bag\"]]],[\"other_target\"],"
            + "[\"category_label\",\"MSG_PROBE_ZEP\"]]]");
        var targets = MissionTargets.Load(dir);

        Assert.True(targets.For("probe_hull/probe_bag").OtherTarget);
        Assert.Equal("MSG_PROBE_ZEP", targets.For("probe_hull/probe_bag").CategoryLabel);
        Assert.False(targets.For("probe_bag").OtherTarget);
        Assert.False(targets.For("probe_hull").OtherTarget);
    }

    [Fact]
    public void AMissionWithNoTargetsFileIsEmptyRatherThanAThrow()
    {
        Assert.Equal(0, MissionTargets.Load(TestData.TempDir()).Count);
    }

    [Fact]
    public void AMissionWithNoTargetsFileTakesTheChaptersWhole()
    {
        // The reader search path: the mission's zrdr, then the chapter's. C1C/M01 is the shipped
        // case, labelled entirely by C1C/zrdr/targets.zrd.
        var fromChapter = MissionTargets.Load(TestData.TempDir(), TestData.Fixture("zrdr"));
        Assert.Equal(4, fromChapter.Count);
        Assert.Equal("MSG_PROBE_POINT", fromChapter.For("probe_point").Description);
    }

    [Fact]
    public void AMissionsOwnTargetsFileWinsOverTheChaptersRatherThanMerging()
    {
        var missionZrdr = TestData.TempDir();
        File.WriteAllText(Path.Combine(missionZrdr, "targets.json"),
            "[[[\"description\", \"MSG_OWN\"], [\"nodes\", [\"own_node\"]]]]");
        var targets = MissionTargets.Load(missionZrdr, TestData.Fixture("zrdr"));
        Assert.Equal(1, targets.Count);
        Assert.Equal("MSG_OWN", targets.For("own_node").Description);
        Assert.Null(targets.For("probe_point").Description);
    }

    private static MissionTargets Load() => MissionTargets.Load(TestData.Fixture("zrdr"));
}
