using System.IO;
using System.Linq;
using CSVM;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The <c>ai.zrd</c> turret reader (docs/formats/turrets.md) on the hand-authored
/// <c>fixtures/zrdr/ai.json</c>: the two structural families, the <c>PARTS</c> chain forms, the
/// duty-cycle windows, the unauthored-but-accepted keys, and the by-title lookup's titleless
/// short-circuit, plus the arc semantics that are the item's most likely visible failure:
/// <c>YAW [0,0]</c> means unrestricted, and the yaw arc is a directed interval whose out-of-arc
/// answer is the angularly nearer end stop.
/// </summary>
public class TurretDefsTests
{
    [Fact]
    public void ParsesBothFamilies()
    {
        var defs = Load();
        Assert.Equal(4, defs.All.Count);
        Assert.Equal(2, defs.All.Count(d => d.Carried));
        Assert.Equal(2, defs.All.Count(d => !d.Carried));
        Assert.All(defs.All.Where(d => d.Carried), d => Assert.Empty(d.NodePatterns));
        Assert.All(defs.All.Where(d => !d.Carried), d => Assert.NotEmpty(d.NodePatterns));
    }

    [Fact]
    public void ParsesTheThreePartChainWithAFirepointList()
    {
        var d = Load().FindByTitle("MSG_TUR_TEST_REAR")!;
        Assert.Equal("tturret", d.YawNode);
        Assert.Equal("tgun", d.PitchNode);
        Assert.Equal(new[] { "tfire1", "tfire2" }, d.Firepoints);
        Assert.Equal("tturret", d.HealthyNode);
        Assert.True(d.Activated);
        Assert.Equal(1, d.Team);
        Assert.Equal("snd_chaingun", d.CannonSound);
    }

    [Fact]
    public void ParsesTheTwoPartChainAsPitchPlusFirepoint()
    {
        var d = Load().FindByTitle("MSG_TUR_TEST_FREE")!;
        Assert.Null(d.YawNode);
        Assert.Equal("fgun", d.PitchNode);
        Assert.Equal(new[] { "ffire" }, d.Firepoints);
    }

    [Fact]
    public void ParsesTheWeaponBlock()
    {
        var rear = Load().FindByTitle("MSG_TUR_TEST_REAR")!;
        Assert.Equal("wep_29", rear.WeaponName);
        Assert.Equal(500, rear.Ammo);
        Assert.Equal(600f, rear.DetectionRange);
        Assert.Equal(0.5f, rear.FireRateMin);
        Assert.Equal(0.5f, rear.FireRateMax); // a scalar authors min == max

        var free = Load().FindByTitle("MSG_TUR_TEST_FREE")!;
        Assert.Equal(1.0f, free.FireRateMin);
        Assert.Equal(1.8f, free.FireRateMax);
    }

    [Fact]
    public void ParsesDutyCycleWindows()
    {
        var d = Load().FindByTitle("MSG_TUR_TEST_REAR")!;
        Assert.Equal(8f, d.AttackMin);
        Assert.Equal(8f, d.AttackMax);
        Assert.Equal(2f, d.BoredMin);
        Assert.Equal(4f, d.BoredMax);
    }

    [Fact]
    public void ToleratesTheUnauthoredKeys()
    {
        // DEACTIVATE / STICKINESS / SHOOT_UP_ONLY / FIRE_LIMITS parse without breaking the rest.
        var d = Load().FindByTitle("MSG_TUR_TEST_FREE")!;
        Assert.Equal(15f, d.InaccuracyDeg);
        Assert.Equal(450f, d.DetectionRange);
    }

    [Fact]
    public void StandaloneNodesArePathPatterns()
    {
        var d = Load().FindByTitle("MSG_TUR_TEST_AAA")!;
        Assert.False(d.Activated);
        Assert.Equal(8f, d.Health);
        Assert.Equal(2, d.NodePatterns.Count);
        Assert.Equal(new[] { "aatest**" }, d.NodePatterns[0]);
        Assert.Equal(new[] { "testzep", "ttur*" }, d.NodePatterns[1]);
    }

    [Fact]
    public void AnAbsentTeamDefaultsToTheFirstEnemyTeam()
    {
        // The decoded loader default: no TEAM key = enemy team id 2 (the original's team space
        // is 0 neutral / 1 ally / 2+ enemy). Authored TEAM 1 stays the ally id.
        var defs = Load();
        Assert.Equal(TurretDef.DefaultTeamId, defs.FindByTitle("MSG_TUR_TEST_AAA")!.TeamId);
        Assert.Equal(1, defs.FindByTitle("MSG_TUR_TEST_REAR")!.TeamId);
    }

    [Fact]
    public void ATitlelessEntryMatchesAnyLookup()
    {
        var defs = Load();
        // A present title matches its own entry first; an unknown title falls through to the
        // titleless entry, which the engine's lookup accepts unconditionally.
        Assert.Equal("MSG_TUR_TEST_REAR", defs.FindByTitle("MSG_TUR_TEST_REAR")!.Title);
        var wildcard = defs.FindByTitle("MSG_TUR_NOT_AUTHORED");
        Assert.NotNull(wildcard);
        Assert.Null(wildcard!.Title);
    }

    [Fact]
    public void YawZeroZeroIsUnrestrictedNotLocked()
    {
        var d = Load().FindByTitle("MSG_TUR_TEST_FREE")!;
        Assert.Equal(0f, d.YawMinDeg);
        Assert.Equal(0f, d.YawMaxDeg);
        Assert.False(d.YawRestricted);
        Assert.Equal(123f, TurretController.ClampYawDeg(123f, d));
        // An absent PITCH is the same answer.
        Assert.Null(d.PitchMinDeg);
        Assert.False(d.PitchRestricted);
        Assert.Equal(-80f, TurretController.ClampPitchDeg(-80f, d));
    }

    [Fact]
    public void RestPoseIsTheArcCentreAndZeroOnAFreeAxis()
    {
        var rear = Load().FindByTitle("MSG_TUR_TEST_REAR")!;
        Assert.Equal(180f, rear.RestYawDeg);
        Assert.Equal(-17.5f, rear.RestPitchDeg);
        var free = Load().FindByTitle("MSG_TUR_TEST_FREE")!;
        Assert.Equal(0f, free.RestYawDeg);
        Assert.Equal(0f, free.RestPitchDeg);
    }

    [Fact]
    public void YawArcIsDirectedAndSnapsToTheNearerEndStop()
    {
        var rear = Load().FindByTitle("MSG_TUR_TEST_REAR")!; // YAW [105, 255], through 180
        Assert.Equal(180f, TurretController.ClampYawDeg(180f, rear));
        Assert.Equal(-150f + 360f, TurretController.ClampYawDeg(-150f, rear)); // the +360 alias lands inside
        Assert.Equal(105f, TurretController.ClampYawDeg(40f, rear));   // nearer the low stop
        Assert.Equal(255f, TurretController.ClampYawDeg(-40f, rear));  // nearer the high stop, across the wrap
    }

    [Fact]
    public void TheSameSpanOnTheOtherSideIsADifferentArc()
    {
        var aaa = Load().FindByTitle("MSG_TUR_TEST_AAA")!; // YAW [-155, -5]
        Assert.Equal(-80f, TurretController.ClampYawDeg(-80f, aaa));
        // 180° sits outside; the nearer end stop is -155 (25° away against 175°).
        Assert.Equal(-155f, TurretController.ClampYawDeg(180f, aaa));
        // The mirrored arc would have accepted it, same span, different arc.
        var rear = Load().FindByTitle("MSG_TUR_TEST_REAR")!;
        Assert.Equal(180f, TurretController.ClampYawDeg(180f, rear));
    }

    [Fact]
    public void BarrelAnglesRoundTrip()
    {
        foreach (var (yaw, pitch) in new[] { (0f, 0f), (30f, -10f), (-120f, 45f), (175f, 5f) })
        {
            var dir = TurretController.LocalDir(yaw, pitch);
            var (y, p) = TurretController.AnglesOfLocal(dir);
            Assert.Equal(yaw, y, 2);
            Assert.Equal(pitch, p, 2);
        }
        // Yaw 0 pitch 0 is local forward (-Z), and yaw 180 faces +Z.
        Assert.True(TurretController.LocalDir(0f, 0f).IsEqualApprox(Vector3.Forward));
        Assert.True(TurretController.LocalDir(180f, 0f).IsEqualApprox(Vector3.Back));
    }

    private static TurretDefs Load() => TurretDefs.Load(TestData.Fixture("zrdr"));
}

/// <summary>Golden counts against the retail extraction, skipped without extracted/ data.
/// These pin the shipped censuses docs/formats/turrets.md records.</summary>
public class TurretDefsGoldenTests
{
    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    [ExtractedDataFact]
    public void TheFortyTwoEntriesSplitSixteenCarried()
    {
        var defs = TurretDefs.Load(ZrdrPath);
        Assert.Equal(42, defs.All.Count);
        Assert.Equal(16, defs.All.Count(d => d.Carried));
        Assert.Equal(26, defs.All.Count(d => !d.Carried));
        // The carried/standalone split is exact: carried = HEALTHY_NODE, standalone = NODES.
        Assert.All(defs.All, d => Assert.Equal(d.Carried,
            !string.IsNullOrEmpty(d.HealthyNode) && d.NodePatterns.Count == 0));
    }

    [ExtractedDataFact]
    public void EveryCarriedEntryShipsAwake()
    {
        var defs = TurretDefs.Load(ZrdrPath);
        Assert.All(defs.All.Where(d => d.Carried), d => Assert.True(d.Activated));
        Assert.Equal(22, defs.All.Count(d => !d.Activated)); // all standalone, C9b's dormants
    }

    [ExtractedDataFact]
    public void KeyCoverageMatchesTheDocumentedCensus()
    {
        var defs = TurretDefs.Load(ZrdrPath);
        // 37, not the documented raw count of 38: MSG_TUR_TRAIN nests its one PITCH inside the
        // WEAPON block, where the turret parser does not read it, that turret has no pitch arc.
        Assert.Equal(37, defs.All.Count(d => d.PitchMinDeg != null));
        Assert.Equal(36, defs.All.Count(d => d.YawMinDeg != null));
        Assert.Equal(20, defs.All.Count(d => d.Team != null));
        Assert.Equal(17, defs.All.Count(d => d.Health != null));
        Assert.Equal(37, defs.All.Count(d => d.CannonSound != null));
        // Exactly one shipped entry authors YAW [0,0], the unrestricted spelling.
        Assert.Equal(1, defs.All.Count(d => d.YawMinDeg == 0f && d.YawMaxDeg == 0f));
        Assert.All(defs.All.Where(d => d.YawMinDeg == 0f && d.YawMaxDeg == 0f),
            d => Assert.False(d.YawRestricted));
    }

    [ExtractedDataFact]
    public void TheStandaloneFamilyShipsFourAwakeAndFourAllied()
    {
        // The four TEAM 1 entries are the piratezep set; see docs/formats/turrets.md "Teams".
        var defs = TurretDefs.Load(ZrdrPath);
        var standalone = defs.All.Where(d => !d.Carried).ToList();
        Assert.Equal(4, standalone.Count(d => d.Activated));
        Assert.Equal(4, standalone.Count(d => d.Team != null));
        Assert.All(standalone.Where(d => d.Team != null), d => Assert.Equal(1, d.TeamId));
        Assert.All(standalone.Where(d => d.Team == null),
            d => Assert.Equal(TurretDef.DefaultTeamId, d.TeamId));
        // The awake set: MSG_TUR_THUG (no TEAM, hostile) + the three allied piratezep rings.
        Assert.Equal(1, standalone.Count(d => d.Activated && d.Team == null));
        Assert.Equal("MSG_TUR_THUG", standalone.Single(d => d.Activated && d.Team == null).Title);
        Assert.Equal(3, standalone.Count(d => d.Activated && d.TeamId == 1));
    }

    [ExtractedDataFact]
    public void EveryWeaponIdResolvesInTheBallisticsCatalogue()
    {
        var defs = TurretDefs.Load(ZrdrPath);
        var weapons = WeaponDefs.Load(ZrdrPath, null);
        Assert.All(defs.All, d => Assert.NotNull(weapons.Get(d.WeaponName)));
        Assert.Equal(8, defs.All.Select(d => d.WeaponName).Distinct().Count());
    }

    [ExtractedDataFact]
    public void GunGroupTwinsDifferOnlyInHealthyNodeAndParts()
    {
        // _G1/_G3 are the firstp/thirdp rig selectors, not difficulty grades: across all eight
        // pairs every behavioural field is identical and only the driven node set changes.
        var defs = TurretDefs.Load(ZrdrPath);
        var pairs = defs.All.Where(d => d.Title?.EndsWith("_G1") == true).ToList();
        Assert.Equal(8, pairs.Count);
        foreach (var g1 in pairs)
        {
            var g3 = defs.FindByTitle(g1.Title![..^3] + "_G3");
            Assert.NotNull(g3);
            Assert.Equal(g1.WeaponName, g3!.WeaponName);
            Assert.Equal(g1.FireRateMin, g3.FireRateMin);
            Assert.Equal(g1.DetectionRange, g3.DetectionRange);
            Assert.Equal(g1.InaccuracyDeg, g3.InaccuracyDeg);
            Assert.Equal(g1.PitchMinDeg, g3.PitchMinDeg);
            Assert.Equal(g1.PitchMaxDeg, g3.PitchMaxDeg);
            Assert.Equal(g1.YawMinDeg, g3.YawMinDeg);
            Assert.Equal(g1.YawMaxDeg, g3.YawMaxDeg);
            Assert.Equal(g1.AttackMin, g3.AttackMin);
            Assert.Equal(g1.BoredMin, g3.BoredMin);
            Assert.NotEqual(g1.HealthyNode, g3.HealthyNode);
        }
    }

    [ExtractedDataFact]
    public void TurretAirframesCarryViewpointKeyedMounts()
    {
        // The host→gunner link: vehicle.zrd's turrets block, keyed firstp/thirdp, the _G1/_G3
        // suffixes select the view rig. The Balmoral is the only two-turret airframe.
        var kestrel = PlaneStats.Load(ZrdrPath, "player_kestrel");
        var third = kestrel.TurretMounts.Where(m => !m.FirstPerson).ToList();
        var first = kestrel.TurretMounts.Where(m => m.FirstPerson).ToList();
        Assert.Single(third);
        Assert.Equal("MSG_TUR_PAC_G3", third[0].Title);
        Assert.Equal("kestrel_turret1", third[0].Node);
        Assert.Single(first);
        Assert.Equal("MSG_TUR_PAC_G1", first[0].Title);

        var balmoral = PlaneStats.Load(ZrdrPath, "player_balmoral");
        Assert.Equal(2, balmoral.TurretMounts.Count(m => !m.FirstPerson));
        Assert.Equal(2, balmoral.TurretMounts.Count(m => m.FirstPerson));

        var bhawk = PlaneStats.Load(ZrdrPath, "player_bhawk");
        Assert.Empty(bhawk.TurretMounts);
    }
}
