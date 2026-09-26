using System;
using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.Mech3.Anim;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The off-engine charter for <c>src/Mech3/Anim/NameResolver.cs</c>: the wildcard matcher, the
/// memoized <c>FindAll</c>, the three-tier scope chain, the symbol-table authority and the
/// anchoring rules, asserted against a plain token node type, no Godot, no chapter world.
/// ⚠ <see cref="TestNode"/> deliberately has no overridden <c>Equals</c>, so a generic resolver
/// cannot be shown to inherit a node type's own equality. Fixture shapes mirror the cases named
/// in the resolver's own doc comments; the values are invented.
/// </summary>
public class NameResolverTests
{
    // ---- '#' matches a run of digits, including zero ----

    [Fact]
    public void HashMatchesDigitRunIncludingZero()
    {
        var bare = Node("air_gen");
        var numbered = Node("air_gen12");
        var lettered = Node("air_genx");
        var resolver = Build(
            (bare, "air_gen", null),
            (numbered, "air_gen12", null),
            (lettered, "air_genx", null));

        var found = resolver.FindAll("air_gen#", null);

        Assert.Contains(bare, found);
        Assert.Contains(numbered, found);
        Assert.DoesNotContain(lettered, found);
    }

    // ---- '*' matches at most one digit: the original's odometer stamps one digit per star, and
    // its compiler instanced `lkshadow*` as plain `lkshadow` ----

    [Fact]
    public void StarMatchesAtMostOneDigit()
    {
        var bare = Node("fly_trail");
        var t1 = Node("fly_trail1");
        var t10 = Node("fly_trail10"); // two digits need two stars
        var lettered = Node("fly_trailx");
        var unrelated = Node("healthy");
        var resolver = Build(
            (bare, "fly_trail", null), (t1, "fly_trail1", null), (t10, "fly_trail10", null),
            (lettered, "fly_trailx", null), (unrelated, "healthy", null));

        var found = resolver.FindAll("fly_trail*", null);

        Assert.Equal(new[] { bare, t1 }, found);
    }

    [Fact]
    public void DoubleStarTemplateFormMatchesTwoDigitsNotALetterRun()
    {
        // C3's shape: the shared `crate**` destructible must instance on the numbered crates and
        // never on `craterlake`, whose death would switch every world `healthy` node off.
        var crate01 = Node("crate01");
        var lake = Node("craterlake");
        var resolver = Build((crate01, "crate01", null), (lake, "craterlake", null));

        Assert.Equal(new[] { crate01 }, resolver.FindAll("crate**", null));
    }

    // ---- plain names compare case-insensitively ----

    [Fact]
    public void MatchIsCaseInsensitive()
    {
        var node = Node("Air_Gen");
        var resolver = Build((node, "Air_Gen", null));

        Assert.Contains(node, resolver.FindAll("air_gen", null));
        Assert.Contains(node, resolver.FindAll("AIR_GEN", null));
    }

    // ---- a '.flt' model-file suffix matches the pattern with or without it ----

    [Fact]
    public void FltSuffixMatchesPatternWithOrWithoutSuffix()
    {
        var node = Node("ap_radiotwr.flt");
        var resolver = Build((node, "ap_radiotwr.flt", null));

        Assert.Contains(node, resolver.FindAll("ap_radiotwr", null));
        Assert.Contains(node, resolver.FindAll("ap_radiotwr.flt", null));
    }

    // ---- scope restriction reads the Add-time parent snapshot, not a live tree ----

    [Fact]
    public void ScopeRestrictsToItsOwnSubtreeWhenTwoTemplatesShareAChildName()
    {
        // he_trails/ap_trails: two staged effect templates whose fly_trail1 children share a name
        // (docs/architecture.md's AnimRuntime entry), scoping must tell the copies apart.
        var heTrails = Node("he_trails");
        var heTrail1 = Node("fly_trail1");
        var apTrails = Node("ap_trails");
        var apTrail1 = Node("fly_trail1");
        var resolver = Build(
            (heTrails, "he_trails", null),
            (heTrail1, "fly_trail1", heTrails),
            (apTrails, "ap_trails", null),
            (apTrail1, "fly_trail1", apTrails));

        Assert.Equal(new[] { heTrail1 }, resolver.FindAll("fly_trail1", heTrails));
        Assert.Equal(new[] { apTrail1 }, resolver.FindAll("fly_trail1", apTrails));

        var global = resolver.FindAll("fly_trail1", null);
        Assert.Equal(2, global.Count);
        Assert.Contains(heTrail1, global);
        Assert.Contains(apTrail1, global);
    }

    [Fact]
    public void ScopeIncludesTheScopeNodeItself()
    {
        var root = Node("m_build01");
        var resolver = Build((root, "m_build01", null));

        Assert.Equal(new[] { root }, resolver.FindAll("m_build**", root));
    }

    // ---- FindAll's memoized result is the same list instance on repeat, read-only-list semantics ----

    [Fact]
    public void FindAllMemoizesTheSameListInstanceForARepeatQuery()
    {
        var node = Node("healthy");
        var resolver = Build((node, "healthy", null));

        var first = resolver.FindAll("healthy", null);
        var second = resolver.FindAll("healthy", null);

        Assert.Same(first, second);
    }

    [Fact]
    public void ClearFindCacheRevisitsAPatternAddedAfterTheFirstQuery()
    {
        var resolver = new NameResolver<TestNode>();

        Assert.Empty(resolver.FindAll("late_arrival", null));

        resolver.Add(Node("late_arrival"), "late_arrival", null);
        Assert.Empty(resolver.FindAll("late_arrival", null)); // still the stale memoized miss

        resolver.ClearFindCache();
        Assert.Single(resolver.FindAll("late_arrival", null));
    }

    // ---- a NAME path walks one scoped segment at a time (through the public tier surface:
    // ResolvePath itself is private, which is what makes the tier order structural) ----

    [Fact]
    public void PathResolutionWalksEachSegmentInsideThePreviousMatches()
    {
        var root = Node("m_build01");
        var door = Node("door1");
        var hinge = Node("hinge1");
        var resolver = Build(
            (root, "m_build01", null),
            (door, "door1", root),
            (hinge, "hinge1", door));

        var found = resolver.ResolveScoped(
            new List<string> { "m_build01", "door1", "hinge1" }, Def("bld"), null);

        Assert.Equal(new[] { hinge }, found);
    }

    // ---- the three-tier scope order: anchor subtree, own template roots, global ----

    [Fact]
    public void AnchorSubtreeWinsOverOwnRootAndGlobal()
    {
        // fly_trail1 exists under the anchor, under the def's own staged root AND as a stray
        // global copy: the anchor's copy wins outright, and the own-root hook is never consulted.
        var anchor = Node("he_ring");
        var mine = Node("fly_trail1");
        var ownRoot = Node("he_trails");
        var ownTrail = Node("fly_trail1");
        var stray = Node("fly_trail1");
        int hookCalls = 0;
        var resolver = new NameResolver<TestNode>(
            ownRootsOf: (_, _) =>
            {
                hookCalls++;
                return new[] { ownRoot };
            });
        resolver.Add(anchor, "he_ring", null);
        resolver.Add(mine, "fly_trail1", anchor);
        resolver.Add(ownRoot, "he_trails", null);
        resolver.Add(ownTrail, "fly_trail1", ownRoot);
        resolver.Add(stray, "fly_trail1", null);

        var found = resolver.ResolveScoped(new List<string> { "fly_trail1" }, Def("he_trails"), anchor);

        Assert.Equal(new[] { mine }, found);
        Assert.Equal(0, hookCalls); // the own-root tier is consulted only on an anchor miss
    }

    [Fact]
    public void OwnRootTierIsConsultedOnAnchorMissBeforeGlobal()
    {
        // The anchor subtree misses; the def's own placed copy must win over the stray global
        // match (the shape real chapter data produces: a copy parked at the stage origin).
        var anchor = Node("he_ring");
        var ownRoot = Node("he_trails");
        var ownTrail = Node("fly_trail1");
        var stray = Node("fly_trail1");
        var resolver = new NameResolver<TestNode>(ownRootsOf: (_, _) => new[] { ownRoot });
        resolver.Add(anchor, "he_ring", null);
        resolver.Add(ownRoot, "he_trails", null);
        resolver.Add(ownTrail, "fly_trail1", ownRoot);
        resolver.Add(stray, "fly_trail1", null);

        var found = resolver.ResolveScoped(new List<string> { "fly_trail1" }, Def("he_trails"), anchor);

        Assert.Equal(new[] { ownTrail }, found);
    }

    [Fact]
    public void GlobalTierIsSkippedUnderLocalNodesOnly()
    {
        // The name resolves only globally: a LOCAL_NODES_ONLY def gets nothing, the same def
        // without the flag falls through to the whole index.
        var anchor = Node("he_ring");
        var stray = Node("fly_trail1");
        var resolver = new NameResolver<TestNode>();
        resolver.Add(anchor, "he_ring", null);
        resolver.Add(stray, "fly_trail1", null);
        var path = new List<string> { "fly_trail1" };

        Assert.Empty(resolver.ResolveScoped(path, Def("he_trails", localNodesOnly: true), anchor));
        Assert.Equal(new[] { stray }, resolver.ResolveScoped(path, Def("he_trails"), anchor));
    }

    [Fact]
    public void DeadAnchorFallsToTheLocalOnlyGlobalResolve()
    {
        // A dead anchor (liveness predicate false) cannot scope anything: the resolve drops to
        // the plain whole-index walk, which also ignores LOCAL_NODES_ONLY, exactly like the
        // null-anchor path it shares.
        var anchor = Node("he_ring");
        var mine = Node("fly_trail1");
        var stray = Node("fly_trail1");
        var resolver = new NameResolver<TestNode>(isLive: n => !ReferenceEquals(n, anchor));
        resolver.Add(anchor, "he_ring", null);
        resolver.Add(mine, "fly_trail1", anchor);
        resolver.Add(stray, "fly_trail1", null);

        var found = resolver.ResolveScoped(
            new List<string> { "fly_trail1" }, Def("he_trails", localNodesOnly: true), anchor);

        Assert.Equal(2, found.Count); // the anchor no longer narrows, both copies, index order
        Assert.Contains(mine, found);
        Assert.Contains(stray, found);
    }

    // ---- a definition the world holds several instances of writes inside its own instance only ----

    [Fact]
    public void AWildcardInstancedDefsMissedNameReachesNoOtherNode()
    {
        // C3's shape: the shared `crate**` destructible, whose death writes `healthy INACTIVE`.
        // An instance carrying no healthy model must switch nothing off anywhere else.
        var crate01 = Node("crate01");
        var crateHealthy = Node("healthy");
        var crate02 = Node("crate02"); // this copy carries no healthy model of its own
        var hull = Node("piratezep");
        var hullHealthy = Node("healthy");
        var resolver = Build(
            (crate01, "crate01", null),
            (crateHealthy, "healthy", crate01),
            (crate02, "crate02", null),
            (hull, "piratezep", null),
            (hullHealthy, "healthy", hull));
        var def = Def("crate**", rootName: "healthy");
        var path = new List<string> { "healthy" };

        Assert.Equal(new[] { crateHealthy }, resolver.ResolveScoped(path, def, crate01));
        Assert.Contains(hullHealthy, resolver.FindAll("healthy", null)); // the world node is there
        Assert.Empty(resolver.ResolveScoped(path, def, crate02));        // and stays unreached
        Assert.Equal(1, resolver.GlobalTierRefused);
    }

    [Fact]
    public void AnExactlyNamedDefStillFallsThroughToTheGlobalTier()
    {
        // The contrast: one world object, one instance. A name it does not carry is still the
        // world's to answer, exactly as the original's localOnly-clear chain answers it.
        var anchor = Node("air_gen");
        var stray = Node("door1");
        var resolver = Build((anchor, "air_gen", null), (stray, "door1", null));

        var found = resolver.ResolveScoped(new List<string> { "door1" }, Def("air_gen"), anchor);

        Assert.Equal(new[] { stray }, found);
        Assert.Equal(0, resolver.GlobalTierRefused);
    }

    [Fact]
    public void AWildcardCarryingNameKeepsTheGlobalTier()
    {
        // The boundary: `lkshw*` writing `lkshb*` (the install's only such write). A wildcard in
        // the name stands in for the digit the odometer stamps into it. It still names the
        // definition's own family, so the whole index answers, as it does for the matcher today.
        var shadow = Node("lkshw1");
        var burnt = Node("lkshb1");
        var plain = Node("lkshb");
        var resolver = Build((shadow, "lkshw1", null), (burnt, "lkshb1", null), (plain, "lkshb", null));
        var def = Def("lkshw*");

        Assert.Equal(new[] { burnt, plain }, resolver.ResolveScoped(new List<string> { "lkshb*" }, def, shadow));
        Assert.Empty(resolver.ResolveScoped(new List<string> { "lkshb" }, def, shadow));
        Assert.Equal(1, resolver.GlobalTierRefused); // the plain name, and only it
    }

    [Fact]
    public void ARootLiftedDefIsRefusedTheGlobalTierToo()
    {
        // The other multi-instance anchoring: an exact NAME that matches nothing, lifted onto every
        // parent of its ANIMATION_ROOT_NAME. Two instances, so a generic name is theirs, not the
        // world's; the refusal has to read the lift, which the definition alone does not record.
        var b1 = Node("apbuild01.flt");
        var b2 = Node("apbuild02.flt");
        var stray = Node("door1");
        var resolver = Build(
            (b1, "apbuild01.flt", null), (Node("healthy"), "healthy", b1),
            (b2, "apbuild02.flt", null), (Node("healthy"), "healthy", b2),
            (stray, "door1", null));
        var def = Def("m_build01", rootName: "healthy"); // no node carries the NAME

        Assert.Equal(new[] { b1, b2 }, resolver.Anchors(def));
        Assert.Empty(resolver.ResolveScoped(new List<string> { "door1" }, def, b1));
        Assert.Equal(1, resolver.GlobalTierRefused);
    }

    // ---- symbol authority: an exact gamez-index binding beats ambiguous name matching ----

    [Fact]
    public void SymbolLookupBeatsAmbiguousNameMatch()
    {
        // C1's `caboose` shape: name matching resolves the real consist AND an unrelated
        // `caboose.flt` (the .flt double-match); the compiled symbol table names exactly one.
        var consist = Node("caboose");
        var decoy = Node("caboose.flt");
        var resolver = new NameResolver<TestNode>();
        resolver.Add(consist, "caboose", null, gamezIndex: 7);
        resolver.Add(decoy, "caboose.flt", null, gamezIndex: 8);
        var def = Def("train");
        def.NodeRefs["caboose"] = 7;

        Assert.Equal(2, resolver.FindAll("caboose", null).Count); // the name ambiguity is real
        Assert.True(resolver.SymbolClaims(def, "caboose", out var node));
        Assert.Same(consist, node);
    }

    // ---- a mech3ax dedup suffix (~n) is resolved by the compiled index, never by name ----

    [Fact]
    public void SymbolLookupResolvesADedupSuffixToItsOwnSibling()
    {
        // pzhomebase's shape: two same-named "land_on" siblings under different parents. mech3ax
        // dedups the second one to "land_on~2" in the def's own node table, but both keep the
        // bare world name "land_on", so only the compiled index can tell them apart.
        var manualLand = Node("pz_manual_land");
        var landOn1 = Node("land_on");
        var autoLand = Node("pz_auto_land");
        var landOn2 = Node("land_on");
        var resolver = new NameResolver<TestNode>();
        resolver.Add(manualLand, "pz_manual_land", null);
        resolver.Add(landOn1, "land_on", manualLand, gamezIndex: 3618);
        resolver.Add(autoLand, "pz_auto_land", null);
        resolver.Add(landOn2, "land_on", autoLand, gamezIndex: 3615);
        var def = Def("piratezep");
        def.NodeRefs["land_on"] = 3618;
        def.NodeRefs["land_on~2"] = 3615;

        Assert.Equal(2, resolver.FindAll("land_on", null).Count); // the bare name is ambiguous
        Assert.True(resolver.SymbolClaims(def, "land_on", out var bound1));
        Assert.Same(landOn1, bound1);
        Assert.True(resolver.SymbolClaims(def, "land_on~2", out var bound2));
        Assert.Same(landOn2, bound2);
    }

    // ---- twin narrowing: a shared NAME resolves to the instance holding the def's symbol root ----

    [Fact]
    public void TwinNarrowingKeepsTheDefsOwnInstance()
    {
        // C1's two hangars are both `air_gen`; the def whose symbol table roots under eairg31
        // (`air_gen#1`) must anchor on that instance alone, or the pair cross-binds.
        var (resolver, a1, _) = TwinHangars(a1Index: 31, a2Index: 42);
        var def = Def("air_gen", rootName: "air_gen");
        def.NodeRefs["air_gen"] = 31;

        Assert.Equal(new[] { a1 }, resolver.Anchors(def));
    }

    // ---- narrowing is tri-state: undecidable (null) leaves the name match standing ----

    [Fact]
    public void NarrowingLeavesBothTwinsForAReaderDef()
    {
        var (resolver, a1, a2) = TwinHangars(a1Index: 31, a2Index: 42);
        var def = Def("air_gen", rootName: "air_gen"); // reader def: no symbol table

        Assert.Equal(new[] { a1, a2 }, resolver.Anchors(def));
    }

    [Fact]
    public void NarrowingLeavesBothTwinsWhenTheIndexWasNeverBuilt()
    {
        var (resolver, a1, a2) = TwinHangars(a1Index: 31, a2Index: 42);
        var def = Def("air_gen", rootName: "air_gen");
        def.NodeRefs["air_gen"] = 99; // no built node carries this index

        Assert.Equal(new[] { a1, a2 }, resolver.Anchors(def));
    }

    [Fact]
    public void NarrowingLeavesBothTwinsWhenTheRootIsOutsideEveryCandidate()
    {
        var (resolver, a1, a2) = TwinHangars(a1Index: 31, a2Index: 42);
        var foreign = Node("elsewhere");
        resolver.Add(foreign, "elsewhere", null, gamezIndex: 77);
        var def = Def("air_gen", rootName: "elsewhere");
        def.NodeRefs["elsewhere"] = 77;

        Assert.Equal(new[] { a1, a2 }, resolver.Anchors(def));
    }

    // ---- the ANIMATION_ROOT_NAME lift and its policy inputs ----

    [Fact]
    public void RootLiftAnchorsOnTheParentsOfRootMatches()
    {
        var b1 = Node("apbuild01.flt");
        var b2 = Node("apbuild02.flt");
        var resolver = Build(
            (b1, "apbuild01.flt", null), (Node("healthy"), "healthy", b1),
            (b2, "apbuild02.flt", null), (Node("healthy"), "healthy", b2));
        var def = Def("m_build**", rootName: "healthy"); // NAME matches nothing here

        Assert.Equal(new[] { b1, b2 }, resolver.Anchors(def));
    }

    [Fact]
    public void RootLiftRefusesAboveMaxRootLift()
    {
        // Above the cap the root is a generic per-object name ('healthy'): the def belongs to
        // game objects, not world nodes, and must not anchor at all.
        var resolver = new NameResolver<TestNode> { MaxRootLift = 2 };
        for (int i = 0; i < 3; i++)
        {
            var parent = Node($"b{i}");
            resolver.Add(parent, $"b{i}", null);
            resolver.Add(Node("healthy"), "healthy", parent);
        }
        var def = Def("m_build**", rootName: "healthy");

        Assert.Empty(resolver.Anchors(def));
    }

    [Fact]
    public void SuppressRootLiftRefusesEvenUnderTheCap()
    {
        var parent = Node("b0");
        var resolver = new NameResolver<TestNode> { SuppressRootLift = true };
        resolver.Add(parent, "b0", null);
        resolver.Add(Node("healthy"), "healthy", parent);
        var def = Def("m_build**", rootName: "healthy");

        Assert.Empty(resolver.Anchors(def));
    }

    // ---- NameResolveFallback: the by-index map stays empty, name resolution stands alone ----

    [Fact]
    public void NameResolveFallbackLeavesSymbolLookupsEmpty()
    {
        var resolver = new NameResolver<TestNode> { NameResolveFallback = true };
        var trail = Node("fly_trail1");
        resolver.Add(trail, "fly_trail1", null, gamezIndex: 400); // colliding index spaces: never mapped
        var def = Def("he_trails");
        def.NodeRefs["fly_trail1"] = 400;

        Assert.True(resolver.SymbolClaims(def, "fly_trail1", out var bound)); // the symbol still claims it
        Assert.Null(bound);                                                   // but binds to nothing
        Assert.Contains(trail, resolver.FindAll("fly_trail1", null));         // names still resolve
    }

    [Fact]
    public void AClaimedUnbuiltIndexResolvesToTheOnePooledCopyCarryingIt()
    {
        var resolver = new NameResolver<TestNode>();
        var pool = Node("pool");
        var copy = Node("copilot_pickup_switch");
        resolver.Add(pool, "pool", null);
        resolver.Add(copy, "copilot_pickup_switch", pool, gamezIndex: 2703, indexByPointer: false);
        var def = Def("pickup_timing");
        def.NodeRefs["copilot_pickup_switch"] = 2703;

        Assert.True(resolver.SymbolClaims(def, "copilot_pickup_switch", out var bound));
        Assert.Same(copy, bound); // the staged actor answers for the index the build never created

        // A second copy on the same index is an effect pool, which stays anchor-scoped.
        var second = Node("copilot_pickup_switch");
        resolver.Add(second, "copilot_pickup_switch", pool, gamezIndex: 2703, indexByPointer: false);
        Assert.True(resolver.SymbolClaims(def, "copilot_pickup_switch", out var ambiguous));
        Assert.Null(ambiguous);
    }

    [Fact]
    public void AClaimInsideAStagedCopyNarrowsToThatCopysOwnNode()
    {
        // CM07's shape: the parked aircraft-archive figure binds the flare def's cp_lh by index,
        // while the def is dispatched onto the chapter's staged passenger copy carrying the same
        // limb under a different index. The consist's caboose, which the copy lacks, keeps binding.
        var figure = Node("pickup_cpilot");
        var figureHand = Node("cp_lh");
        var passenger = Node("pickup_agent");
        var passengerHand = Node("cp_lh");
        var caboose = Node("caboose");
        var resolver = new NameResolver<TestNode>(privateCopyOf: n => n == passenger || n == passengerHand ? passenger : null);
        resolver.Add(figure, "pickup_cpilot", null, gamezIndex: 9769);
        resolver.Add(figureHand, "cp_lh", figure, gamezIndex: 9784);
        resolver.Add(caboose, "caboose", null, gamezIndex: 1200);
        resolver.Add(passenger, "pickup_agent", null, gamezIndex: 2680, indexByPointer: false);
        resolver.Add(passengerHand, "cp_lh", passenger, gamezIndex: 2686, indexByPointer: false);
        var def = Def("pickup_flare");
        def.NodeRefs["cp_lh"] = 9784;
        def.NodeRefs["caboose"] = 1200;

        Assert.Same(figureHand, resolver.Resolve("cp_lh", def, null));         // off the pool: the index binds
        Assert.Same(passengerHand, resolver.Resolve("cp_lh", def, passenger)); // on the copy: its own hand
        Assert.Same(passengerHand, resolver.Resolve("cp_lh", def, passengerHand));
        Assert.Same(caboose, resolver.Resolve("caboose", def, passenger));     // a name the copy lacks stands
    }

    // ---- multi-target NAME1 defs anchor through their authored paths (M4 F18) ----

    [Fact]
    public void MultiTargetDefAnchorsThroughItsOwnPathsOnly()
    {
        // The zeppelin sub-part shape: piratezep's rtur*/ltur* turrets. The path scoping is
        // what keeps the def off ANOTHER zeppelin's same-named turrets, a bare "ltur*" name
        // match would grab mp1zep's too.
        var piratezep = Node("piratezep");
        var rtur1 = Node("rtur1");
        var ltur1 = Node("ltur1");
        var mp1zep = Node("mp1zep");
        var decoy = Node("ltur1");
        var resolver = Build(
            (piratezep, "piratezep", null),
            (rtur1, "rtur1", piratezep),
            (ltur1, "ltur1", piratezep),
            (mp1zep, "mp1zep", null),
            (decoy, "ltur1", mp1zep));
        var def = Def(""); // NAME1 defs parse with an empty NAME
        def.MultiTargets.Add(("pzrtur*", new[] { "piratezep", "rtur*" }));
        def.MultiTargets.Add(("pzltur*", new[] { "piratezep", "ltur*" }));

        var anchors = resolver.Anchors(def);

        Assert.Equal(2, anchors.Count);
        Assert.Contains(rtur1, anchors);
        Assert.Contains(ltur1, anchors);
        Assert.DoesNotContain(decoy, anchors);
    }

    [Fact]
    public void EmptyNameWithoutTargetsStaysRefusedEvenWithAGenericRoot()
    {
        // The refusal survives for the defs it was written for: an empty-NAME def
        // carrying only a generic ANIMATION_ROOT_NAME must not root-lift onto every instance.
        var parent = Node("b0");
        var resolver = new NameResolver<TestNode>();
        resolver.Add(parent, "b0", null);
        resolver.Add(Node("healthy"), "healthy", parent);

        Assert.Empty(resolver.Anchors(Def("", rootName: "healthy")));
    }

    // ---- a freed node has to LEAVE the index, not merely be skipped by it ----

    [Fact]
    public void AFreedRowThrowsForAScopedFindAllOverLiveNodes()
    {
        // The site the engine throws at: the scope filter walks the recorded parent chain through
        // a map keyed by node identity, so a live row's own lookup meets the dead key.
        var world = Node("world");
        var wing = Node("hook");
        var resolver = WithFreedAirframe(world, wing);

        Assert.Throws<ObjectDisposedException>(() => resolver.FindAll("hook", world));
        Assert.Equal(2, resolver.DropFreed());
        Assert.Equal(new[] { wing }, resolver.FindAll("hook", world));
    }

    [Fact]
    public void AFreedAirframeThrowsWhenTheNextOneIsStagedOverIt()
    {
        // Nothing tells the index a node has gone, so the next stage's own rows are what meet the
        // dead ones: an airframe swap indexes a replacement over the aircraft it freed.
        var resolver = WithFreedAirframe();

        Assert.Equal(2, resolver.FreedRows());
        Assert.Throws<ObjectDisposedException>(() => StageWarhawk(resolver));
    }

    [Fact]
    public void DropFreedLetsTheNextAirframeStageAndResolveItsOwnNames()
    {
        var resolver = WithFreedAirframe();

        Assert.Equal(2, resolver.DropFreed());
        Assert.Equal(0, resolver.FreedRows());
        var (staged, hook) = StageWarhawk(resolver);

        Assert.Equal(new[] { staged }, resolver.FindAll("player_warhawk", null));
        Assert.Equal(new[] { hook }, resolver.FindAll("hook", staged));
        Assert.Equal(new[] { hook }, resolver.FindAll("hook", null));
    }

    private static TestNode Node(string label) => new() { Label = label };

    // One drive's airframe freed the way a rig is when its drive ends, over any live rows the
    // caller wants beside it: the state landings-hookup-airframe leaves the node table in.
    private static NameResolver<TestNode> WithFreedAirframe(TestNode? liveRoot = null, TestNode? liveChild = null)
    {
        var gone = Node("player_balmoral");
        var goneHook = Node("hook");
        var resolver = new NameResolver<TestNode>(FreedThrowsIdentity.Instance, isLive: n => !n.Freed);
        if (liveRoot != null)
        {
            resolver.Add(liveRoot, "world", null);
        }

        if (liveChild != null)
        {
            resolver.Add(liveChild, "hook", liveRoot);
        }

        resolver.Add(gone, "player_balmoral", null);
        resolver.Add(goneHook, "hook", gone);
        gone.Freed = true;
        goneHook.Freed = true;
        return resolver;
    }

    private static (TestNode Staged, TestNode Hook) StageWarhawk(NameResolver<TestNode> resolver)
    {
        var staged = Node("player_warhawk");
        var hook = Node("hook");
        resolver.Add(staged, "player_warhawk", null);
        resolver.Add(hook, "hook", staged);
        return (staged, hook);
    }

    private static AnimDefinition Def(string name, string? rootName = null, bool localNodesOnly = false) =>
        new() { Name = name, RootName = rootName, LocalNodesOnly = localNodesOnly };

    // C1's twin-hangar shape: two instance roots, each owning a child that shares the NAME
    // `air_gen`, distinguishable only by gamez index.
    private static (NameResolver<TestNode> Resolver, TestNode A1, TestNode A2) TwinHangars(
        int a1Index, int a2Index)
    {
        var eairg31 = Node("eairg31");
        var eairg32 = Node("eairg32");
        var a1 = Node("air_gen");
        var a2 = Node("air_gen");
        var resolver = new NameResolver<TestNode>();
        resolver.Add(eairg31, "eairg31", null);
        resolver.Add(a1, "air_gen", eairg31, a1Index);
        resolver.Add(eairg32, "eairg32", null);
        resolver.Add(a2, "air_gen", eairg32, a2Index);
        return (resolver, a1, a2);
    }

    private static NameResolver<TestNode> Build(params (TestNode Node, string Name, TestNode? Parent)[] rows)
    {
        var resolver = new NameResolver<TestNode>();
        foreach (var (node, name, parent) in rows)
        {
            resolver.Add(node, name, parent);
        }
        return resolver;
    }

    private sealed class TestNode
    {
        public string Label = "";

        public bool Freed;
    }

    // The engine's node identity as a freed node meets it: Godot reads an instance id that is
    // gone. Every key hashes alike so one bucket holds them all, which makes the collision the
    // engine only meets on some runs certain here.
    private sealed class FreedThrowsIdentity : IEqualityComparer<TestNode>
    {
        public static readonly FreedThrowsIdentity Instance = new();

        public bool Equals(TestNode? x, TestNode? y) => ReferenceEquals(Live(x), Live(y));

        public int GetHashCode(TestNode obj)
        {
            Live(obj);
            return 0;
        }

        private static TestNode? Live(TestNode? node) =>
            node is { Freed: true } ? throw new ObjectDisposedException(nameof(TestNode)) : node;
    }
}
