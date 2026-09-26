using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using CSVM.Session.World;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The zeppelins reader (docs/formats/mission-entities.md): fixture units for the full and the
/// minimal record shapes, the decoded num_healthy_required default+clamp, and the [null] file,
/// plus golden counts over the install so a reader or extraction change moves a test instead of
/// silently drifting.
/// </summary>
public class ZeppelinsTests
{
    private static readonly string[] Chapters = { "C1", "C1B", "C1C", "C2", "C2B", "C3", "C4", "C5" };

    // Chapter-scope dirs that are not mission dirs (the same discovery rule Probes.Ai uses).
    private static readonly HashSet<string> ChapterScopeDirs =
        new(StringComparer.OrdinalIgnoreCase) { "gamez", "texture", "cam_anim", "zrdr" };

    [Fact]
    public void TheFullRecordParsesFromTheFixture()
    {
        var defs = Zeppelins.Load(TestData.Fixture("zeppelins"));
        Assert.Equal(2, defs.Count);

        var zep = defs[0];
        Assert.Equal("testzep", zep.Node);
        Assert.Equal(new Godot.Vector3(-100f, 450f, 200f), zep.Position);
        Assert.Equal(-90f, zep.YawDeg);
        Assert.Equal(0f, zep.PitchDeg);
        Assert.Equal(15f, zep.MaxSpeed);
        Assert.Equal(4.47f, zep.MaxAccel, 2);
        Assert.Equal(0.5f, zep.AccelPitchDeg);
        Assert.Equal(1.5f, zep.AccelYawDeg);
        Assert.Equal(15f, zep.MaxRateYawDeg);
        Assert.Equal(5f, zep.MaxRatePitchDeg);
        Assert.Equal(-30f, zep.MinPitchDeg);   // stays in authored degrees (the unit-bug rule)
        Assert.Equal(30f, zep.MaxPitchDeg);
        Assert.Equal("TestNet", zep.Net);
        Assert.Equal(new[] { "player" }, zep.Targets);
        Assert.Equal(3, zep.Healthy.Count);
        Assert.Equal(new ZeppelinHealthyZone("gasbag1", "panels"), zep.Healthy[0]);
        Assert.Equal(4, zep.Engines.Count);
        Assert.Equal(3, zep.Gasbags.Count);
        Assert.Equal(new ZeppelinGasbag("gasbag3", 400f, "test_gasbagtorpedo3"), zep.Gasbags[2]);
        Assert.Equal(20f, zep.CannonFireDelay);
        Assert.Equal(500f, zep.CannonFireRange);
        Assert.Single(zep.LeftCannons);
        Assert.Equal(new ZeppelinCannon("lbroad1", "deploy_test_lbroad1", "retract_test_lbroad1"),
            zep.LeftCannons[0]);
        Assert.Single(zep.RightCannons);
        Assert.Equal(10f, zep.CannonInaccuracyDeg);
        Assert.Equal("Enemy", zep.Team);
        Assert.Null(zep.TeamId);
        Assert.True(zep.Deactivated);

        var ch = Assert.Single(zep.CannonHealth);
        Assert.Equal("lbroad1", ch.Cannon);
        Assert.Equal("gunback", ch.GunBackNode);
        Assert.Equal("frame", ch.FrameNode);
        Assert.Equal("gasbag2", ch.Gasbag);
        Assert.Equal(200f, ch.Hp);
        Assert.Equal("test_lbroad1_destroy", ch.DestroyAnim);
        Assert.Equal(new[] { (0.6f, "60_test_lbroad1"), (0.3f, "30_test_lbroad1") }, ch.Stages);
    }

    [Fact]
    public void TheDecodedThresholdRulesApply()
    {
        var defs = Zeppelins.Load(TestData.Fixture("zeppelins"));

        // Clamped at load to the healthy count: authored 4 over 3 zones reads 3.
        Assert.Equal(3, defs[0].NumHealthyRequired);

        // Defaulted to 1 when unauthored beside a healthy list.
        Assert.Equal(1, defs[1].NumHealthyRequired);
    }

    [Fact]
    public void TheMinimalRecordParsesWithEveryOptionalKeyAbsent()
    {
        var defs = Zeppelins.Load(TestData.Fixture("zeppelins"));
        var bare = defs[1];
        Assert.Equal("barezep", bare.Node);
        Assert.Empty(bare.Gasbags);           // the C5/M01 piratezep shape
        Assert.Empty(bare.LeftCannons);
        Assert.Empty(bare.RightCannons);
        Assert.Empty(bare.CannonHealth);
        Assert.Empty(bare.Targets);
        Assert.Null(bare.CannonFireDelay);
        Assert.Null(bare.CannonFireRange);
        Assert.Null(bare.CannonInaccuracyDeg);
        Assert.False(bare.Deactivated);
        Assert.Equal(2, bare.Engines.Count);

        // The parser's other accepted team spelling: a bare integer id.
        Assert.Null(bare.Team);
        Assert.Equal(2, bare.TeamId);
    }

    [Fact]
    public void ANullFileIsNoZeppelinsNotAnError()
    {
        Assert.Empty(Zeppelins.Load(TestData.Fixture("zeppelins-null")));
    }

    // ---- Goldens over the install ---------------------------------------------------------------

    [ExtractedDataFact]
    public void TheInstallWideCensusMatchesTheScopingCounts()
    {
        int files = 0, nullFiles = 0, records = 0;
        int withGasbags = 0, withCannons = 0, withTargets = 0, withCannonHealth = 0;
        int withTeam = 0, deactivated = 0, withInaccuracy = 0;
        int healthyEntries = 0;
        var gasbagless = new List<string>();
        foreach (var (chapter, mission) in Missions())
        {
            var missionZrdr = SessionPaths.MissionZrdr(TestData.DataRoot!, chapter, mission);
            List<ZeppelinDef> defs;
            try
            {
                defs = Zeppelins.Load(missionZrdr);
            }
            catch (FileNotFoundException)
            {
                continue;   // 3 MP dirs ship no zeppelins file at all
            }
            files++;
            if (defs.Count == 0)
            {
                nullFiles++;
                continue;
            }
            var netNames = new HashSet<string>(
                AiNets.LoadIndex(SessionPaths.ChapterZrdr(TestData.DataRoot!, chapter)).Values,
                StringComparer.OrdinalIgnoreCase);
            foreach (var def in defs)
            {
                records++;
                if (def.Gasbags.Count > 0)
                {
                    withGasbags++;
                }
                else
                {
                    gasbagless.Add($"{chapter}/{mission}: {def.Node}");
                }
                if (def.LeftCannons.Count > 0)
                {
                    withCannons++;
                    Assert.Equal(def.LeftCannons.Count, def.RightCannons.Count); // always symmetric
                }
                if (def.Targets.Count > 0)
                {
                    withTargets++;
                }
                if (def.CannonHealth.Count > 0)
                {
                    withCannonHealth++;
                }
                if (def.Team != null || def.TeamId != null)
                {
                    withTeam++;
                }
                if (def.Deactivated)
                {
                    deactivated++;
                }
                if (def.CannonInaccuracyDeg != null)
                {
                    withInaccuracy++;
                }
                healthyEntries += def.Healthy.Count;

                // Universal keys and their measured ranges.
                Assert.InRange(def.MaxSpeed, 5f, 30f);
                Assert.Equal(4.47f, def.MaxAccel, 2);
                Assert.Equal(-30f, def.MinPitchDeg);
                Assert.Equal(30f, def.MaxPitchDeg);
                Assert.InRange(def.NumHealthyRequired, 1, def.Healthy.Count);
                Assert.NotEmpty(def.Engines);
                Assert.Contains(def.Net, netNames); // every authored net resolves by name
            }
        }
        Assert.Equal(50, files);
        Assert.Equal(12, nullFiles);
        Assert.Equal(58, records);
        Assert.Equal(57, withGasbags);
        Assert.Equal(new[] { "C5/M01: piratezep" }, gasbagless);
        Assert.Equal(48, withCannons);
        // The scoping census's "targets 47" counts KEY presence: 39 records author a name
        // list; the 8 IA1 records author "targets", null, which reads as present-but-empty.
        Assert.Equal(39, withTargets);
        Assert.Equal(24, withCannonHealth);
        Assert.Equal(16, withTeam);
        Assert.Equal(316, healthyEntries);
        // The deactivated KEY is on 9 records, but two (C1/M04, C2/M03) author 0: the value
        // decides, so 7 start switched off.
        Assert.Equal(7, deactivated);
        Assert.Equal(3, withInaccuracy);
    }

    [ExtractedDataFact]
    public void TheWorkedExampleZeppelinReadsExactlyAsTheScopingDocMeasuredIt()
    {
        // C1/M04 piratezep: the plan's worked example.
        var defs = Zeppelins.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C1", "M04"));
        var zep = Assert.Single(defs);
        Assert.Equal("piratezep", zep.Node);
        Assert.Equal("PirateZep1", zep.Net);
        Assert.Equal(6, zep.Gasbags.Count);
        foreach (var bag in zep.Gasbags)
        {
            Assert.Equal(120f, bag.Hp);
        }
        Assert.Equal(4, zep.NumHealthyRequired);
        Assert.Equal(12, zep.Engines.Count);
        Assert.Equal(6, zep.LeftCannons.Count);
        Assert.Equal(6, zep.RightCannons.Count);
        Assert.Equal(20f, zep.CannonFireDelay);
        Assert.Equal(500f, zep.CannonFireRange);
        Assert.Equal(new[] { "player" }, zep.Targets);
        Assert.Empty(zep.CannonHealth);
        Assert.False(zep.Deactivated);   // the key is authored 0 here, the value decides
    }

    // BL-670: the arrival floor has to clear every shipped zeppelin leg, campaign and
    // Instant Action alike. Otherwise a short leg advances the walk before the hull is
    // underway (docs/formats/mission-entities.md, "Steering").
    [ExtractedDataFact]
    public void TheArrivalFloorClearsEveryShippedZeppelinLeg()
    {
        float shortest = float.MaxValue;
        foreach (var (chapter, mission) in Missions())
        {
            var missionZrdr = SessionPaths.MissionZrdr(TestData.DataRoot!, chapter, mission);
            List<ZeppelinDef> defs;
            try
            {
                defs = Zeppelins.Load(missionZrdr);
            }
            catch (FileNotFoundException)
            {
                continue;   // 3 MP dirs ship no zeppelins file at all
            }
            if (defs.Count == 0)
            {
                continue;
            }
            var nets = AiNets.Load(SessionPaths.ChapterZrdr(TestData.DataRoot!, chapter));
            foreach (var def in defs)
            {
                var net = AiNets.ByName(nets, def.Net);
                Assert.NotNull(net);
                foreach (var (a, b) in net!.Edges)
                {
                    var leg = net.Nodes[b].Position - net.Nodes[a].Position;
                    float horiz = new Vector2(leg.X, leg.Z).Length();
                    Assert.True(ZeppelinRuntime.ArrivalFloorM < horiz,
                        $"{chapter}/{mission} net={def.Net} edge {a}-{b} is {horiz:0.#} m, " +
                        $"no wider than the {ZeppelinRuntime.ArrivalFloorM} m arrival floor");
                    shortest = Mathf.Min(shortest, horiz);
                }
            }
        }

        // The campaign's own shortest leg, C4/M04's M4Piratezep: re-measure it here rather
        // than trust the number staying true as extraction or mission data drifts.
        Assert.Equal(143.9f, shortest, 1);
    }

    // C1B/MP3's ZVZ1a turns 67.7 degrees at node 6 on a 205 m leg, against a 343.8 m turn
    // circle: BL-670's own worst-case shortfall. Flying it real confirms the hull turns
    // through and carries on, never orbiting the node.
    [ExtractedDataFact]
    public void TheWorstShippedTurnBreaksOutRatherThanOrbits()
    {
        var net = AiNets.ByName(
            AiNets.Load(SessionPaths.ChapterZrdr(TestData.DataRoot!, "C1B")), "ZVZ1a")!;
        var def = Zeppelins.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C1B", "MP3"))
            .First(d => d.Node.Equals("multiplayer1zep", StringComparison.OrdinalIgnoreCase));

        var follower = new AiNetFollower(net, new Random(1), ZeppelinRuntime.ArrivalFloorM,
            observesStopPoints: true);
        var motion = new ZeppelinMotion(def, follower);
        int steps = 0;
        const int budget = 60 * 90; // 90 sim-seconds, ~3x the measured transit through node 6
        bool reachedNode6 = false;
        bool passedNode6 = false;
        while (!passedNode6 && steps < budget)
        {
            motion.Step(1f / 60f);
            steps++;
            if (follower.ArrivedNode is not { } arrived)
            {
                continue;
            }
            if (arrived.Position == net.Nodes[6].Position)
            {
                reachedNode6 = true;
            }
            else if (reachedNode6)
            {
                passedNode6 = true;
            }
        }

        Assert.True(reachedNode6, $"never reached node 6 in {steps / 60f:0.#}s");
        // Advancing past node 6, not just reaching it, rules out a hull circling forever.
        Assert.True(passedNode6,
            $"reached node 6 but never advanced past it in {steps / 60f:0.#}s: orbiting");
    }

    private static IEnumerable<(string Chapter, string Mission)> Missions()
    {
        foreach (var chapter in Chapters)
        {
            var dir = Path.Combine(TestData.DataRoot!, "extracted", chapter);
            if (!Directory.Exists(dir))
            {
                continue;
            }
            var missions = new List<string>();
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(d);
                if (!ChapterScopeDirs.Contains(name)
                    && !name.StartsWith("rtexture", StringComparison.OrdinalIgnoreCase))
                {
                    missions.Add(name);
                }
            }
            missions.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var m in missions)
            {
                yield return (chapter, m);
            }
        }
    }
}
