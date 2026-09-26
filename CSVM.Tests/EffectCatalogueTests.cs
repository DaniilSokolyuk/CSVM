using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Producer-range tripwires for <see cref="EffectCatalogue"/>: every producer of an effect name is
/// checked here to still emit only names the catalogue knows about, engine-free.
/// <see cref="ImpactOutcomeTests"/> carries the third (the gunhit formula, folded into its
/// weapon-by-surface battery).
/// </summary>
public class EffectCatalogueTests
{
    // Every player airframe's node name (the same 11 ExtractedGoldenTests
    // .ShippedChaseDistances lists), so PlaneStats.Load can walk every plane's
    // data rather than just the one (the Devastator) that happens to carry a 0.99 entry today,
    // a future plane picking one up must be caught here too.
    private static readonly string[] AllPlaneNodeNames =
    {
        "player_bhawk", "player_fury", "player_peacemaker", "player_kestrel", "player_fbrand",
        "player_warhawk", "player_balmoral", "player_pfighter", "player_autogyro",
        "player_avenger", "player_brigand",
    };

    private static string SharedZrdr =>
        SessionPaths.PreferUnzipped(System.IO.Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>The graze family's whole producible range, over every surface id the cascade can be
    /// handed: every registry slot, plus the out-of-range and null-material arms. Against a program
    /// shipping exactly the three touchdown defs this install ships, every one of those resolves a
    /// name the world-effects bind would have bound, because
    /// <see cref="EffectCatalogue.WorldEffectAnimNames"/> appends the same vector. So a graze can
    /// never select a def nothing staged.</summary>
    [Fact]
    public void EveryTouchdownSelectionIsANameTheWorldBindBinds()
    {
        var shipped = new HashSet<string>(
            new[] { "touchdown_default", "touchdown_dirt", "touchdown_water" }, StringComparer.Ordinal);
        var table = new SurfaceDefTable(
            EffectCatalogue.TouchdownDefPrefix, lastResort: null, shipped.Contains);

        var produced = new HashSet<string>(StringComparer.Ordinal);
        // -1 and 14 are the cascade's two out-of-range arms; null is "no struck material".
        foreach (int? id in Enumerable.Range(-1, SurfaceRegistry.Names.Count + 2).Cast<int?>().Append(null))
        {
            string? def = table.DefForSurfaceId(id);
            Assert.NotNull(def); // slot 0 ships, so the play-nothing arm is unreachable here
            produced.Add(def!);
        }

        Assert.Equal(shipped, produced);
    }

    /// <summary>The correction B12 exists to make, stated as a test rather than left to a comment.
    /// Only <c>dirt</c>(13) raises dust. Everything else sparks off <c>touchdown_default</c>:
    /// ordinary terrain (id 0), the ids with no def of their own
    /// (<c>fire</c>/<c>airstrip</c>/<c>buildings</c>/<c>dzone</c>), an out-of-range id and a null
    /// material. Our build had this backwards, scraping dust off everything that was not a building
    /// or water.</summary>
    [Fact]
    public void OnlyDirtScrapesDustAndOnlyWaterSplashes()
    {
        var shipped = new HashSet<string>(
            new[] { "touchdown_default", "touchdown_dirt", "touchdown_water" }, StringComparer.Ordinal);
        var table = new SurfaceDefTable(
            EffectCatalogue.TouchdownDefPrefix, lastResort: null, shipped.Contains);

        Assert.Equal("touchdown_dirt", table.DefForSurfaceId(13));
        Assert.Equal("touchdown_water", table.DefForSurfaceId(1));
        foreach (int? id in new int?[] { 0, 5, 8, 11, 12, -1, 14, null })
            Assert.Equal("touchdown_default", table.DefForSurfaceId(id));
    }

    /// <summary>The one arm the crash family does not share: with no slot 0 to fall back on, the
    /// touchdown cascade plays <b>nothing</b> (<c>FUN_0048d2c0</c> skips its play call) where the
    /// crash cascade resolves its bare anim name. It is unreachable in this install, since all
    /// eight chapters ship <c>touchdown_default</c>, but it is the difference between the two
    /// families, so it is pinned rather than assumed away.</summary>
    [Fact]
    public void AnEmptyTouchdownVectorPlaysNothing()
    {
        var table = new SurfaceDefTable(
            EffectCatalogue.TouchdownDefPrefix, lastResort: null, _ => false);

        Assert.Null(table.DefForSurfaceId(1));
        Assert.Null(table.DefForSurfaceId(null));
        Assert.Empty(table.PlayableDefs);
    }

    /// <summary>The AI aircraft family rides the same cascade as the player's: against the
    /// trio this install ships, <c>dirt</c>(13) and <c>water</c>(1) resolve their own def and
    /// every other arm, ordinary terrain, the def-less ids, out-of-range, null material, falls
    /// to slot 0, <c>ai_crash_default</c>.</summary>
    [Fact]
    public void TheAiCrashCascadeMirrorsThePlayerFamilys()
    {
        var shipped = new HashSet<string>(
            new[] { "ai_crash_default", "ai_crash_dirt", "ai_crash_water" }, StringComparer.Ordinal);
        var table = new SurfaceDefTable(
            EffectCatalogue.AiCrashDefPrefix, lastResort: "bloodhawk", shipped.Contains);

        Assert.Equal("ai_crash_dirt", table.DefForSurfaceId(13));
        Assert.Equal("ai_crash_water", table.DefForSurfaceId(1));
        foreach (int? id in new int?[] { 0, 5, 8, 11, 12, -1, 14, null })
            Assert.Equal("ai_crash_default", table.DefForSurfaceId(id));
        Assert.Equal(3, table.PlayableDefs.Count);
    }

    /// <summary>The AI family's last-resort arm is the crash family's, not touchdown's: a vector
    /// that cannot answer resolves the bare vehicle name (<c>FUN_00479240</c> interns the params
    /// NAME into the slot <c>FUN_0048b920</c> falls to), never "play nothing". Unreachable in this
    /// install, all eight chapters ship <c>ai_crash_default</c>, but it is where the AI family
    /// and the graze family part, so it is pinned rather than assumed.</summary>
    [Fact]
    public void AnEmptyAiCrashVectorFallsToTheBarePlaneName()
    {
        var table = new SurfaceDefTable(
            EffectCatalogue.AiCrashDefPrefix, lastResort: "bloodhawk", _ => false);

        Assert.Equal("bloodhawk", table.DefForSurfaceId(13));
        Assert.Equal("bloodhawk", table.DefForSurfaceId(null));
        Assert.Empty(table.PlayableDefs);
    }

    /// <summary>The per-chapter census, as a golden: every chapter's compiled anim program defines
    /// exactly the three <c>ai_crash_*</c> defs (<c>default</c>/<c>dirt</c>/<c>water</c>), the
    /// same trio as the player and touchdown families, carrying the shared <c>kestrel</c>
    /// anim-root NAME, and the family pick follows the pilot: a human rig binds
    /// <c>player_crash_*</c>, an AI plane <c>ai_crash_*</c> off the same program.
    /// The eight loads run concurrently, not serially; assertions stay serial.</summary>
    [ExtractedDataFact]
    public void EveryChapterShipsExactlyTheThreeAiCrashDefs()
    {
        string dataRoot = TestData.DataRoot!;
        string shared = SharedZrdr;
        var chapters = new[] { "C1", "C1B", "C1C", "C2", "C2B", "C3", "C4", "C5" };
        var programs = chapters
            .Select(chapter => Task.Run(() =>
            {
                var (chapterAnim, missionAnim) = AnimProgram.ArchivePaths(dataRoot, chapter, "IA1");
                return AnimProgram.Load(shared, SessionPaths.ChapterZrdr(dataRoot, chapter),
                    SessionPaths.MissionZrdr(dataRoot, chapter, "IA1"), chapterAnim, missionAnim);
            }))
            .ToArray();
        Task.WaitAll(programs);

        for (int i = 0; i < chapters.Length; i++)
        {
            var chapter = chapters[i];
            var program = programs[i].Result;
            try
            {
                var ai = EffectCatalogue.CrashDefTableFor(program, humanPiloted: false, "bloodhawk");
                Assert.Equal(
                    new[] { "ai_crash_default", "ai_crash_water", "ai_crash_dirt" },
                    ai.PlayableDefs);
                Assert.Equal("ai_crash_dirt", ai.DefForSurfaceId(13));
                Assert.Equal("ai_crash_water", ai.DefForSurfaceId(1));
                Assert.Equal("ai_crash_default", ai.DefForSurfaceId(0));
                Assert.Equal("ai_crash_default", ai.DefForSurfaceId(null));
                foreach (var def in ai.PlayableDefs)
                {
                    Assert.All(program.ByAnimName(def),
                        d => Assert.Equal(EffectCatalogue.AiCrashAnimRoot, d.Name));
                }

                var human = EffectCatalogue.CrashDefTableFor(program, humanPiloted: true, "bloodhawk");
                Assert.All(human.PlayableDefs,
                    def => Assert.StartsWith(EffectCatalogue.CrashDefPrefix, def, StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                // Naming the chapter here is the only reason for this catch: xUnit's own
                // assertion messages carry the mismatch, not which of the eight loop bodies hit it.
                throw new Xunit.Sdk.XunitException($"chapter {chapter}: {ex.Message}", ex);
            }
        }
    }

    /// <summary>What the collider overlay colours by: against the three defs per
    /// family this install ships, only <c>default</c>(0), <c>water</c>(1) and <c>dirt</c>(13)
    /// resolve to themselves, every other id resolves slot 0, because that is the def a touch
    /// there plays. Pinned as a list rather than as "the ids we ship", since which ids resolve is
    /// whatever the bound program defines.</summary>
    [Fact]
    public void OnlyTheIdsWithADefOfTheirOwnResolveToThemselves()
    {
        var shipped = new HashSet<string>(
            new[]
            {
                "player_crash_default", "player_crash_dirt", "player_crash_water",
                "touchdown_default", "touchdown_dirt", "touchdown_water",
            }, StringComparer.Ordinal);

        var resolved = EffectCatalogue.ResolvedSurfaceIds(shipped.Contains);

        Assert.Equal(SurfaceRegistry.Names.Count, resolved.Count);
        foreach (int id in Enumerable.Range(0, resolved.Count))
            Assert.Equal(id is 0 or 1 or 13 ? id : SurfaceRegistry.Default, resolved[id]);
    }

    /// <summary>Either family is enough: a program shipping a crash def for an id but no graze def
    /// still makes that id behave as itself on contact, so the overlay must draw it as itself. The
    /// union is why both vectors are asked rather than one standing in for both.</summary>
    [Fact]
    public void OneFamilysDefIsEnoughToResolveAnIdToItself()
    {
        var crashOnly = EffectCatalogue.ResolvedSurfaceIds(
            name => name is "player_crash_default" or "player_crash_dirt");
        var grazeOnly = EffectCatalogue.ResolvedSurfaceIds(
            name => name is "touchdown_default" or "touchdown_dirt");

        Assert.Equal(13, crashOnly[13]);
        Assert.Equal(13, grazeOnly[13]);
        Assert.Equal(SurfaceRegistry.Default, crashOnly[1]);
        Assert.Equal(SurfaceRegistry.Default, grazeOnly[1]);
    }

    /// <summary>A program defining nothing resolves every id to slot 0, including slot 0 itself,
    /// the overlay then draws one colour, which is honest: nothing distinguishes those surfaces on
    /// contact. The able-to-fail control for the two cases above.</summary>
    [Fact]
    public void AProgramWithNoDefsResolvesEveryIdToSlotZero()
    {
        var resolved = EffectCatalogue.ResolvedSurfaceIds(_ => false);

        Assert.All(resolved, id => Assert.Equal(SurfaceRegistry.Default, id));
    }

    /// <summary>The per-part damage-effect shims (`DamageVisuals.OnPartDamage`'s own filter,
    /// <c>anim.EndsWith("_damage_effects")</c>): across every player airframe's
    /// <c>destroyable_parts</c>, every <c>injure_anims</c> entry that filter would fire on must be
    /// one of <see cref="EffectCatalogue.PlaneDamageEffectAnims"/>, today only the Devastator
    /// carries any (its 0.99 entries), the other ten carry none, but a future plane picking one up
    /// with a name the catalogue does not know would otherwise wire nothing, silently.</summary>
    [ExtractedDataFact]
    public void EveryPlanesDamageEffectShimIsInTheCatalogue()
    {
        var zrdr = SharedZrdr;
        var violations = new List<string>();
        int found = 0;

        foreach (var nodeName in AllPlaneNodeNames)
        {
            var stats = PlaneStats.Load(zrdr, nodeName);
            foreach (var part in stats.DestroyableParts)
            {
                foreach (var (_, anim) in part.InjureAnims)
                {
                    if (!anim.EndsWith("_damage_effects", StringComparison.OrdinalIgnoreCase))
                        continue;
                    found++;
                    if (!EffectCatalogue.PlaneDamageEffectAnims.Contains(anim))
                        violations.Add($"{nodeName}/{part.Name}: '{anim}' is not in PlaneDamageEffectAnims");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join("\n", violations));
        // The Devastator's four parts are the shipped case (DamageVisuals' own doc: "the 10
        // aircraft whose data carries no 0.99 entry at all"), a check that found none would be
        // passing on no evidence, exactly the trap ExtractedDataFact exists to avoid.
        Assert.True(found > 0, "no plane's data carried a *_damage_effects entry, the check ran on nothing");
    }

    /// <summary>Every airframe's injure_anims entry that <see cref="DamageVisuals.RigAnimFor"/>
    /// maps to a rig anim must be a name the crash rig binds
    /// (<see cref="EffectCatalogue.DamageStageAnims"/> or
    /// <see cref="EffectCatalogue.PlaneDamageEffectAnims"/>), else the crossing plays nothing.
    /// Also pins the 0.10 player_smoketrail -> player_damage_trail mapping.</summary>
    [ExtractedDataFact]
    public void EveryInjureStageAnimIsBoundOnTheRig()
    {
        var zrdr = SharedZrdr;
        var bound = new HashSet<string>(
            EffectCatalogue.DamageStageAnims.Concat(EffectCatalogue.PlaneDamageEffectAnims),
            StringComparer.OrdinalIgnoreCase);
        var violations = new List<string>();
        int found = 0;

        foreach (var nodeName in AllPlaneNodeNames)
        {
            var stats = PlaneStats.Load(zrdr, nodeName);
            var entries = stats.DestroyableParts
                .SelectMany(p => p.InjureAnims.Select(e => (Where: p.Name, e.Anim)))
                .Concat(stats.VehicleInjureAnims.Select(e => (Where: "(vehicle)", e.Anim)));
            foreach (var (where, anim) in entries)
            {
                if (DamageVisuals.RigAnimFor(anim) is not { } stage)
                    continue;
                found++;
                if (!bound.Contains(stage))
                    violations.Add($"{nodeName}/{where}: '{anim}' plays '{stage}', which the rig does not bind");
            }
        }

        Assert.True(violations.Count == 0, string.Join("\n", violations));
        Assert.True(found > 0, "no plane's data carried a stage entry, the check ran on nothing");
        Assert.Equal("player_damage_trail", DamageVisuals.RigAnimFor("player_smoketrail"));
    }

    /// <summary>The AI half: every airframe's AI-side injure_anims entry that
    /// <see cref="DamageVisuals.RigAnimFor"/> maps must be one of
    /// <see cref="EffectCatalogue.AiDamageStageAnims"/>. An AI ladder names anims the player menu
    /// never does, so a flat two-name list going stale would leave a plane staging nothing.</summary>
    [ExtractedDataFact]
    public void EveryAiInjureStageAnimIsOneOfTheAiMenu()
    {
        var zrdr = SharedZrdr;
        var ai = new HashSet<string>(EffectCatalogue.AiDamageStageAnims, StringComparer.OrdinalIgnoreCase);
        var violations = new List<string>();
        int found = 0;

        foreach (var nodeName in AllPlaneNodeNames)
        {
            var stats = PlaneStats.LoadForAi(zrdr, nodeName);
            foreach (var (_, anim) in stats.VehicleInjureAnims)
            {
                if (DamageVisuals.RigAnimFor(anim) is not { } stage)
                    continue;
                found++;
                if (!ai.Contains(stage))
                    violations.Add($"{nodeName}: AI entry '{anim}' plays '{stage}', not an AI menu name");
            }
        }

        Assert.True(violations.Count == 0, string.Join("\n", violations));
        Assert.True(found > 0, "no AI airframe's data carried a stage entry, the check ran on nothing");
    }

    /// <summary>The cockpit gauge families must stay unplayable on an airframe: C1 ships
    /// <c>nose_damage_green</c> and three sets like it, plus four <c>*_got_hit</c> defs, so a
    /// program-existence rule in place of the curated menus would light indicator geometry on the
    /// aircraft. Also pins that a name in neither menu maps to nothing at all.</summary>
    [Fact]
    public void NoCockpitGaugeDefEverPlaysOnTheAirframe()
    {
        foreach (var part in new[] { "nose", "tail", "leftwing", "rightwing" })
        {
            foreach (var tier in new[] { "green", "yellow", "red" })
                Assert.Null(DamageVisuals.RigAnimFor($"{part}_damage_{tier}"));
            Assert.Null(DamageVisuals.RigAnimFor($"{part}_got_hit"));
        }

        Assert.Null(DamageVisuals.RigAnimFor("balmoral_healthtest"));
        Assert.Equal("pfsmoketrail", DamageVisuals.RigAnimFor("PFSmokeTrail"));
        Assert.Equal("random_remote_damage", DamageVisuals.RigAnimFor("random_remote_damage"));
    }

    /// <summary>The healthy↔torn candidate sets derive from the authored defs, the
    /// hideable skins are exactly the two `*_h` nodes `plane_reset` re-ACTIVEs (pdp2_h/pdp3_h,
    /// one shared def OPERAND_NODE-retargeted at every airframe) and the torn set is exactly the
    /// eight `pdpN` targets of the `pdpanelN` defs. A parse change that drops either set silently
    /// re-engages DamageVisuals' geometric fallback; this pins the derivation to the data.</summary>
    [ExtractedDataFact]
    public void PanelPairingSetsDeriveFromTheAuthoredDefs()
    {
        var zrdr = SharedZrdr;
        var defs = CSVM.Mech3.AnimDefs.LoadFileDefs(zrdr, "player_destruct_reset.json")
            .Concat(CSVM.Mech3.AnimDefs.LoadFileDefs(zrdr, "player-1.json"))
            .ToList();
        Assert.True(defs.Count > 0, "the two reader files loaded no defs, the check ran on nothing");

        var pairing = DamageVisuals.PanelPairingSets(defs);

        Assert.NotNull(pairing);
        Assert.Equal(new[] { "pdp2_h", "pdp3_h" },
            pairing!.HideableHealthy.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(new[] { "pdp1", "pdp2", "pdp3", "pdp4", "pdp5", "pdp6", "pdp7", "pdp8" },
            pairing.TornTargets.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
    }
}
