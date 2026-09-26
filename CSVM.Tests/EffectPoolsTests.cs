using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Mech3;
using CSVM.Session.World;
using CSVM.Utils;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The effect-template pool sizes (<c>CSVM/data/effect_pools.json</c>),
/// committed engine config, so these run without an extraction. Parsing is tested through
/// <see cref="EffectPools.Parse"/> rather than <c>Load</c>: the sizing decision is pure, while
/// <c>Load</c> adds file IO and engine warnings that need a session.
/// </summary>
public class EffectPoolsTests
{
    private static string ConfigPath =>
        Path.Combine(TestData.RepoRoot, "CSVM", "data", "effect_pools.json");

    [Fact]
    public void TheShippedFileSizesTheGunFamilyAtOneAndTheDamageSputtersDeeperThanTheDefault()
    {
        var pools = Load();
        // The gun roots stay SHARED on purpose (the gun-effect throttle owns them), so a pool would be
        // copies nothing uses, this is the one deliberate 1.
        Assert.Equal(1, pools.SlotsFor("gunhit", players: 1));
        Assert.Equal(1, pools.SlotsFor("dum_gunhit", players: 1));
        Assert.Equal(1, pools.SlotsFor("mag_gunhit", players: 1));
        // Damage-stage smoke is the measured many-at-once case (five simultaneous kills wrapped a
        // 4-slot pool), so its root is sized above the default.
        Assert.True(pools.SlotsFor("partial_damage_obj", 1) > pools.SlotsFor("he_trails", 1));
    }

    [Fact]
    public void AnUnnamedRootTakesTheDefaultSize()
    {
        var pools = Load();
        // Only the deviations are authored; every other stage root reads the default entry.
        Assert.Equal(pools.Default.Base, pools.SlotsFor("he_ring", players: 1));
        Assert.Equal(pools.Default.Base, pools.SlotsFor("not_a_root_at_all", players: 1));
    }

    [Fact]
    public void ExtraPlayersDeepenThePoolButTheSharedRootsStayShared()
    {
        var pools = Load();
        int solo = pools.SlotsFor("he_trails", players: 1);
        // Splitscreen/multiplayer is the reason the per-player term exists: every extra aircraft is
        // another rocket landing somewhere else.
        Assert.True(pools.SlotsFor("he_trails", 4) > solo);
        Assert.True(pools.SlotsFor("he_trails", 16) > pools.SlotsFor("he_trails", 4));
        // ...but a root whose perExtraPlayer is 0 does not grow with the session.
        Assert.Equal(1, pools.SlotsFor("gunhit", 16));
    }

    [Fact]
    public void NoRootEverExceedsTheAuthoredCeiling()
    {
        var pools = Load();
        // The cap is the memory guard, each slot is one more copy of that root's subtree.
        Assert.Equal(pools.MaxSlots, pools.SlotsFor("partial_damage_obj", players: 64));
        Assert.Equal(pools.MaxSlots, pools.SlotsFor("he_trails", players: 64));
    }

    [Fact]
    public void APoolIsNeverSizedBelowOneCopy()
    {
        // A hand-edit to 0 (or a negative) would stage no template at all, which reads in-game as
        // "the effect is broken" rather than "the pool is off", clamp instead.
        var pools = EffectPools.Parse(Json("{\"default\":{\"base\":0},\"roots\":{\"he_ring\":{\"base\":-3}}}"));
        Assert.Equal(1, pools.SlotsFor("he_ring", 1));
        Assert.Equal(1, pools.SlotsFor("anything", 1));
    }

    [Fact]
    public void APartialFileOverridesOnlyWhatItNames()
    {
        // Overriding one root must not silently drop the built-in default for every other.
        var pools = EffectPools.Parse(Json("{\"roots\":{\"he_ring\":{\"base\":9}}}"));
        Assert.Equal(9, pools.SlotsFor("he_ring", 1));
        Assert.Equal(EffectPools.Fallback.Default.Base, pools.SlotsFor("he_trails", 1));
    }

    [Fact]
    public void ARootEntryWithoutABaseIsIgnoredRatherThanReadAsZero()
    {
        var pools = EffectPools.Parse(Json("{\"roots\":{\"he_ring\":{\"why\":\"prose only\"}}}"));
        Assert.Equal(pools.Default.Base, pools.SlotsFor("he_ring", 1));
    }

    [Fact]
    public void ARootNameThatIsNotAStageRootIsReported()
    {
        // A typo sizes nothing; the build says so rather than leaving it invisible.
        var pools = EffectPools.Parse(Json("{\"roots\":{\"he_ring\":{\"base\":2},\"he_rong\":{\"base\":2}}}"));
        Assert.Equal(new[] { "he_rong" }, pools.UnknownRoots(new[] { "he_ring", "he_trails" }));
    }

    [ExtractedDataFact]
    public void TheShippedFileNamesOnlyRealStageRoots()
    {
        // The committed file is checked against the roots a bound chapter really stages, so a
        // rename on either side fails here rather than in a silently unsized session.
        Assert.Empty(Load().UnknownRoots(StageRoots()));
    }

    [ExtractedDataFact]
    public void TheStageDepthIsTheDeepestRootNotTheDefault()
    {
        var pools = Load();
        var roots = StageRoots();
        int depth = pools.DepthFor(roots, players: 1);
        // The deepest root, whichever the file currently sizes deepest: naming one here pins the
        // assertion to today's tuning instead of to the rule it is about.
        int deepest = 0;
        foreach (string root in roots)
        {
            deepest = System.Math.Max(deepest, pools.SlotsFor(root, 1));
        }
        Assert.Equal(deepest, depth);
        Assert.True(depth > pools.Default.Base);
    }

    [Fact]
    public void TheCrashSectionSizesThePerPanelFamilyAndDefaultsEverythingElseToOne()
    {
        var pools = Load();
        // The damage-stage and destroy families carry one copy per authored CALL SITE, not per
        // anchor: pdpanel7 alone calls the flakes four times at pdp7. Counts: the file's why lines.
        Assert.Equal(12, pools.CrashSlotsFor("planeflakes"));
        Assert.True(pools.CrashSlotsFor("short_firetrail") > 1);
        Assert.True(pools.CrashSlotsFor("large_firetrail") > 1);
        // The Balmoral's crew: three chuteman calls from one node need three copies.
        Assert.Equal(3, pools.CrashSlotsFor("chuteman"));
        // A crash template with no entry stays single-copy, the pre-pool behaviour, right for
        // the once-per-crash choreography templates.
        Assert.Equal(1, pools.CrashSlotsFor("plane_sp_polys"));
        Assert.Equal(12, pools.CrashDepthFor(new[] { "planeflakes", "plane_sp_polys" }));
    }

    [Fact]
    public void TheCrashSectionIsItsOwnNamespaceNotTheWorldOne()
    {
        // A crash entry must not resize the world pool of the same name, and vice versa.
        var pools = EffectPools.Parse(Json("{\"crashRoots\":{\"he_ring\":{\"base\":5}}}"));
        Assert.Equal(5, pools.CrashSlotsFor("he_ring"));
        Assert.Equal(EffectPools.Fallback.Default.Base, pools.SlotsFor("he_ring", 1));
        Assert.Equal(new[] { "he_ring" }, pools.UnknownCrashRoots(new[] { "planeflakes" }));
    }

    private static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);

    private static EffectPools Load() => EffectPools.Parse(File.ReadAllBytes(ConfigPath));

    // The stage-root set the shipped config is sized against, derived from C1's bound
    // program and gamez by exactly the call the world-effects build makes: the answer is chapter
    // data, not a hand table, so these two checks need the player's extraction. No Godot node is built: the resolver is
    // asked with no scope, and the closure walk is engine-free.
    private static IReadOnlyList<string> StageRoots()
    {
        const string chapter = "C1";
        const string mission = "IA1";
        string dataRoot = TestData.DataRoot!;
        string shared = SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));
        var (chapterAnim, missionAnim) = AnimProgram.ArchivePaths(dataRoot, chapter, mission);
        var program = AnimProgram.Load(shared, SessionPaths.ChapterZrdr(dataRoot, chapter),
            SessionPaths.MissionZrdr(dataRoot, chapter, mission), chapterAnim, missionAnim);
        return WorldEffectsFactory.EffectStageRootNames(program,
            GameZ.Load(SessionPaths.ChapterGamez(dataRoot, chapter)));
    }
}
