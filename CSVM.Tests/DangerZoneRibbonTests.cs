using System;
using System.Collections.Generic;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Modes;
using CSVM.Mech3;
using Godot;
using Xunit;
using Xunit.Abstractions;

namespace CSVM.Tests;

/// <summary>
/// The decoded danger-zone run, engine-free. The ribbon's cubic pieces pass through the route
/// vertices with metres for a parameter. A run enters from the nearer end and walks the segments
/// the right way round. The rail closes the aeroplane's offset onto the ribbon and banks into the
/// bend. The pilot's node-tag entry drives the mode machine through approach, lock, run and the
/// return to patrol. The install sweep reads every shipped zone's authored difficulty.
/// </summary>
public class DangerZoneRibbonTests
{
    private const float Dt = 1f / 60f;

    private static readonly Vector3[] Bend =
    {
        new(0f, 400f, 0f), new(500f, 400f, 0f), new(1000f, 400f, -300f), new(1500f, 400f, -300f),
    };

    private readonly ITestOutputHelper _output;

    public DangerZoneRibbonTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Per chapter: the <c>dzpathN</c> nodes the gamez carries, the ribbons the loader
    /// builds from them, and the difficulty census as <c>value x count</c>. Surveyed off
    /// <c>extracted/*/gamez/nodes.json</c>; two chapters ship no danger zone at all.</summary>
    public static TheoryData<string, int, int, string> ChapterZoneDifficulties => new()
    {
        { "C1", 6, 6, "0 x6" },
        { "C1B", 7, 7, "0 x7" },
        { "C1C", 0, 0, "" },
        { "C2", 13, 13, "0 x13" },
        { "C2B", 0, 0, "" },
        { "C3", 5, 5, "0 x5" },
        { "C4", 15, 15, "0 x15" },
        { "C5", 34, 34, "0 x34" },
    };

    [Fact]
    public void SegmentsPassThroughTheVerticesAndRunInMetres()
    {
        var ribbon = DangerZoneRibbon.FromPolyline("dzpath1", 1, Bend);
        Assert.Equal(3, ribbon.Segments.Count);
        for (int i = 0; i < ribbon.Segments.Count; i++)
        {
            var s = ribbon.Segments[i];
            Assert.Equal(Bend[i].DistanceTo(Bend[i + 1]), s.Length, 2);
            Assert.True(ribbon.PointAt(i, 0f).DistanceTo(Bend[i]) < 1e-2f);
            Assert.True(ribbon.PointAt(i, s.Length).DistanceTo(Bend[i + 1]) < 1e-2f);
        }
        // A straight first chord has a tangent of about unit length in metres of parameter.
        Assert.InRange(ribbon.TangentAt(0, 0f).Length(), 0.9f, 1.1f);
        Assert.Equal(Bend[0], ribbon.End(false));
        Assert.True(ribbon.End(true).DistanceTo(Bend[3]) < 1e-2f);
        // The end tangents point INTO the ribbon from either end.
        Assert.True(ribbon.EndTangentInto(false).X > 0f);
        Assert.True(ribbon.EndTangentInto(true).X < 0f);
    }

    [Fact]
    public void ARunEntersFromTheChosenEndAndWalksTheSegmentsThatWay()
    {
        var ribbon = DangerZoneRibbon.FromPolyline("dzpath1", 1, Bend);
        var forward = new DangerZoneRun(ribbon, reversed: false);
        Assert.Equal(0, forward.Segment);
        Assert.Equal(Bend[0], forward.Point);
        Assert.True(forward.Direction.X > 0.99f);
        forward.Advance(600f);
        Assert.Equal(1, forward.Segment);
        Assert.False(forward.Done);
        forward.Advance(5000f);
        Assert.True(forward.Done);

        var back = new DangerZoneRun(ribbon, reversed: true);
        Assert.Equal(2, back.Segment);
        Assert.True(back.Point.DistanceTo(Bend[3]) < 1e-2f);
        Assert.True(back.Direction.X < -0.99f);
        back.Advance(600f);
        Assert.Equal(1, back.Segment);
        Assert.True(back.OnLastSegment == false);
        back.Advance(5000f);
        Assert.True(back.Done);
    }

    [Fact]
    public void LanesAreTakenLeastOccupiedFirstAndHandedBack()
    {
        var ribbon = DangerZoneRibbon.FromPolyline("dzpath2", 2, Bend,
            new[] { new Vector3(0f, 0f, 40f), new Vector3(0f, 0f, -40f) });
        Assert.Equal(3, ribbon.Lanes.Count);
        var a = new DangerZoneRun(ribbon, false);
        var b = new DangerZoneRun(ribbon, false);
        var c = new DangerZoneRun(ribbon, false);
        Assert.Equal(new[] { 0, 1, 2 }, new[] { a.Lane, b.Lane, c.Lane });
        Assert.False(ribbon.HasFreeLane);
        Assert.Equal(b.Point, a.Point + ribbon.Lanes[1]);
        b.Release();
        Assert.True(ribbon.HasFreeLane);
        Assert.Equal(1, new DangerZoneRun(ribbon, false).Lane);
    }

    [Fact]
    public void TheRailClosesTheOffsetOntoTheRibbonAndSettlesOnTheCruise()
    {
        var ribbon = DangerZoneRibbon.FromPolyline("dzpath3", 3, new[]
        {
            new Vector3(0f, 400f, 0f), new Vector3(3000f, 400f, 0f), new Vector3(6000f, 400f, 0f),
        });
        var run = new DangerZoneRun(ribbon, false);
        // Locked 80 m beside the entry, flying 90 m/s along it with a sideways drift.
        var rail = new DangerZoneRail(run, new Vector3(0f, 400f, 80f), Basis.Identity,
            new Vector3(90f, 0f, 10f), 90f);
        float offsetAt2S = 0f;
        for (int i = 0; i < 60 * 8 && rail.Step(Dt); i++)
        {
            if (i == 120)
                offsetAt2S = Mathf.Abs(rail.Position.Z);
        }
        Assert.False(run.Done);
        Assert.True(offsetAt2S < 80f, $"offset {offsetAt2S:0.0} m had not started closing by 2 s");
        Assert.True(Mathf.Abs(rail.Position.Z) < 1f, $"offset {rail.Position.Z:0.0} m never closed");
        Assert.InRange(rail.Speed, DangerZoneRail.CruiseSpeedMps - 0.5f, DangerZoneRail.CruiseSpeedMps + 0.5f);
        Assert.True((-rail.Attitude.Z).X > 0.999f, "the nose is not along the ribbon");
        Assert.True(rail.Position.X > 400f, "the cursor did not advance at the cruise");
    }

    [Fact]
    public void TheRailFrameBanksIntoTheBendAndStaysLevelOnAStraight()
    {
        var level = DangerZoneRail.RailFrame(Vector3.Right, Vector3.Zero);
        Assert.True(level.Y.Dot(Vector3.Up) > 0.999f);
        Assert.True((-level.Z).Dot(Vector3.Right) > 0.999f);

        // Bending toward -Z while flying +X: "up" is thrown into the turn, a knife-edge bank.
        var banked = DangerZoneRail.RailFrame(Vector3.Right, new Vector3(0f, 0f, -0.01f));
        Assert.True(banked.Y.Dot(Vector3.Forward) > 0.999f);
        Assert.True((-banked.Z).Dot(Vector3.Right) > 0.999f);
        Assert.True(Mathf.Abs(banked.Determinant() - 1f) < 1e-4f);
    }

    [Fact]
    public void AReachedTaggedNodeRunsTheMachineThroughApproachLockRunAndBack()
    {
        var ribbon = DangerZoneRibbon.FromPolyline("dzpath1", 1, new[]
        {
            new Vector3(2000f, 400f, 0f), new Vector3(2600f, 400f, 0f), new Vector3(3200f, 400f, 0f),
        });
        var zones = DangerZoneRibbonsFor(ribbon);
        var net = new CSVM.Mech3.AiNet
        {
            Id = 16,
            Name = "TestCourse",
            Nodes = new[]
            {
                new CSVM.Mech3.AiNetNode(new Vector3(0f, 400f, 0f), Array.Empty<float>()),
                new CSVM.Mech3.AiNetNode(new Vector3(1000f, 400f, 0f), new[] { 0f, 0f, 1f, 1f }),
                new CSVM.Mech3.AiNetNode(new Vector3(1000f, 400f, 2000f), Array.Empty<float>()),
            },
            Edges = new[] { (0, 1), (1, 2) },
        };
        var follower = new AiNetFollower(net, new Random(1));
        var machine = new AiModeMachine(new Random(1));
        var transitions = new List<AiMode>();
        machine.ModeChanged += (_, to, _) => transitions.Add(to);
        var pilot = new AiPilot { Patrol = follower, Machine = machine, DangerZones = zones };
        var model = new ScriptedModel(new Vector3(-100f, 400f, 0f));

        pilot.Next(model.Model, Dt); // seats on node 0, flies at node 1
        Assert.Equal(1, follower.CurrentIndex);
        model.Model.Position = new Vector3(1005f, 400f, 0f); // abeam node 1: arrival
        pilot.Next(model.Model, Dt);
        Assert.Equal(AiMode.ApproachingDangerZone, machine.Mode);
        Assert.NotNull(pilot.ZoneRun);
        Assert.False(pilot.ZoneRun!.Reversed);
        Assert.Null(pilot.RailPose);

        // Inside the lock range of the entry the next step hands the pose to the rail.
        model.Model.Position = new Vector3(1900f, 400f, 0f);
        pilot.Next(model.Model, Dt);
        Assert.Equal(AiMode.NavigatingDangerZone, machine.Mode);
        model.Model.Position = new Vector3(1900f, 400f, 0f);
        pilot.Next(model.Model, Dt);
        Assert.NotNull(pilot.RailPose);
        Assert.True(pilot.RailSpeed > 0f);

        // The run flies out the far end on its own clock and the walk re-seats on the net.
        int steps = 0;
        while (machine.Mode == AiMode.NavigatingDangerZone && steps++ < 60 * 60)
        {
            pilot.Next(model.Model, Dt);
            if (pilot.RailPose is { } pose)
                model.Model.Position = pose.Origin;
        }
        Assert.Equal(AiMode.Patrol, machine.Mode);
        Assert.Null(pilot.ZoneRun);
        Assert.NotNull(pilot.RailPose);          // the exit step still wrote the pose, as the original's does
        Assert.Equal(-1, follower.CurrentIndex);  // re-seated: the next patrol step seats afresh
        pilot.Next(model.Model, Dt);
        Assert.Null(pilot.RailPose);
        Assert.True(follower.CurrentIndex >= 0);
        Assert.Equal(new[]
        {
            AiMode.ApproachingDangerZone, AiMode.NavigatingDangerZone, AiMode.Patrol,
        }, transitions);
    }

    [Fact]
    public void AnUnnumberedTagTakesTheNearestEndAndAnInactiveRibbonIsRefused()
    {
        var near = DangerZoneRibbon.FromPolyline("dzpath1", 1, new[]
        {
            new Vector3(5000f, 400f, 0f), new Vector3(6000f, 400f, 0f),
        });
        var far = DangerZoneRibbon.FromPolyline("dzpath2", 2, new[]
        {
            new Vector3(-9000f, 400f, 0f), new Vector3(-800f, 400f, 0f),
        });
        var zones = DangerZoneRibbonsFor(near, far);
        var pick = zones.NearestEnd(new Vector3(0f, 400f, 0f));
        Assert.NotNull(pick);
        Assert.Same(far, pick!.Value.Ribbon);   // its far vertex at -800 is the nearest end of all
        Assert.True(pick.Value.FarEnd);

        far.Active = false;
        pick = zones.NearestEnd(new Vector3(0f, 400f, 0f));
        Assert.Same(near, pick!.Value.Ribbon);
        Assert.False(pick.Value.FarEnd);
    }

    /// <summary>The proximity pick a hit pilot's passed roll makes: the end inside 500 m whose
    /// tangent leads AWAY, not always the nearest one. Its admission terms are pinned here too:
    /// range, a free lane, the active byte.</summary>
    [Fact]
    public void TheProximityPickTakesTheEndThatLeadsAwayInsideFiveHundredMetres()
    {
        var from = new Vector3(0f, 400f, 0f);
        var ahead = DangerZoneRibbon.FromPolyline("dzpath1", 1, new[]
        {
            new Vector3(300f, 400f, 0f), new Vector3(800f, 400f, 0f),
        });
        // Nearer, but its ribbon runs back past the aeroplane: entering here would double back.
        var behind = DangerZoneRibbon.FromPolyline("dzpath2", 2, new[]
        {
            new Vector3(-200f, 400f, 0f), new Vector3(700f, 400f, 0f),
        });
        var zones = DangerZoneRibbonsFor(ahead, behind);

        var pick = zones.ProximityPick(from, naturalTouch: 9);
        Assert.NotNull(pick);
        Assert.Same(ahead, pick!.Value.Ribbon);
        Assert.False(pick.Value.FarEnd);

        // The only free lane taken by a run in progress drops the ribbon out of the pick.
        var run = new DangerZoneRun(ahead, reversed: false);
        Assert.False(ahead.HasFreeLane);
        Assert.Same(behind, zones.ProximityPick(from, 9)!.Value.Ribbon);
        run.Release();

        // Beyond 500 m nothing qualifies, however well the end lines up.
        Assert.Null(zones.ProximityPick(from + new Vector3(-1000f, 0f, 0f), 9));

        ahead.Active = false;
        behind.Active = false;
        Assert.Null(zones.ProximityPick(from, 9));
    }

    /// <summary>The pick's difficulty admission. The original refuses a zone when natural_touch
    /// is below it (<c>FUN_004210e0</c>). An exact match is taken; only a harder zone is passed
    /// over.</summary>
    [Fact]
    public void TheProximityPickRefusesAZoneAboveTheNaturalTouchAndTakesOneWithinIt()
    {
        var from = new Vector3(0f, 400f, 0f);
        var zone = DangerZoneRibbon.FromPolyline("dzpath1", 1, new[]
        {
            new Vector3(300f, 400f, 0f), new Vector3(800f, 400f, 0f),
        }, laneOffsets: null, difficulty: 4);
        var zones = DangerZoneRibbonsFor(zone);
        Assert.Equal(4, zone.Difficulty);

        Assert.Null(zones.ProximityPick(from, naturalTouch: 3));
        Assert.Same(zone, zones.ProximityPick(from, naturalTouch: 4)!.Value.Ribbon);
        Assert.Same(zone, zones.ProximityPick(from, naturalTouch: 9)!.Value.Ribbon);
    }

    /// <summary>Every shipped zone's difficulty, off the node's flag word. The census is pinned, so
    /// a reader or extraction change that moved those bits moves a test. Each ribbon is checked
    /// against its own node, so the loader cannot drop the term again.</summary>
    [ExtractedDataTheory]
    [MemberData(nameof(ChapterZoneDifficulties))]
    public void EveryShippedZoneTakesTheDifficultyItsNodeAuthors(
        string chapter, int expectedNodes, int expectedRibbons, string expectedCensus)
    {
        var gamez = GameZ.Load(SessionPaths.ChapterGamez(TestData.DataRoot!, chapter));
        var census = new SortedDictionary<int, int>();
        int nodes = 0;
        foreach (var node in gamez.Nodes)
        {
            if (!IsDangerZoneNode(node.Name))
                continue;
            nodes++;
            census[node.DangerZoneDifficulty] = census.GetValueOrDefault(node.DangerZoneDifficulty) + 1;
            _output.WriteLine($"{chapter} {node.Name} difficulty={node.DangerZoneDifficulty}");
        }
        Assert.Equal(expectedNodes, nodes);
        var parts = new List<string>();
        foreach (var pair in census)
            parts.Add($"{pair.Key} x{pair.Value}");
        Assert.Equal(expectedCensus, string.Join(" ", parts));

        var ribbons = DangerZoneRibbons.Load(gamez);
        Assert.Equal(expectedRibbons, ribbons?.All.Count ?? 0);
        foreach (var ribbon in ribbons?.All ?? (IReadOnlyCollection<DangerZoneRibbon>)Array.Empty<DangerZoneRibbon>())
            Assert.Equal(gamez.FindByName(ribbon.Name)!.DangerZoneDifficulty, ribbon.Difficulty);
    }

    [Fact]
    public void AnInterruptedRunResumesAsAnApproachAndTheRailIgnoresTheProbe()
    {
        var machine = new AiModeMachine(new Random(1)) { ProbeBlocked = (_, _) => "hill" };
        machine.Enter(AiMode.ApproachingDangerZone, "test");
        // The approach still runs the crash check, and the climb-out hands back to the approach.
        machine.Update(new Vector3(0f, 400f, 0f), new Vector3(0f, 0f, -100f), null, null, AiModeMachine.ProbeIntervalMaxS);
        Assert.Equal(AiMode.AvoidCrash, machine.Mode);
        machine.ProbeBlocked = (_, _) => null;
        machine.Update(new Vector3(0f, 400f, 0f), new Vector3(0f, 0f, -100f), null, null, AiModeMachine.ProbeIntervalMaxS);
        Assert.Equal(AiMode.ApproachingDangerZone, machine.Mode);

        // On rails nothing in the machine runs: no probe, no activation into pursue.
        machine.ProbeBlocked = (_, _) => "hill";
        machine.Enter(AiMode.NavigatingDangerZone, "test");
        machine.Update(new Vector3(0f, 10f, 0f), new Vector3(0f, 0f, -100f), new Vector3(0f, 10f, -500f), null, AiModeMachine.ProbeIntervalMaxS);
        Assert.Equal(AiMode.NavigatingDangerZone, machine.Mode);

        // A stun pre-empts the rail and hands back to the approach, the run record kept.
        machine.Stun(0.5f);
        Assert.Equal(AiMode.Stunned, machine.Mode);
        machine.Update(new Vector3(0f, 400f, 0f), Vector3.Zero, null, null, 1f);
        Assert.Equal(AiMode.ApproachingDangerZone, machine.Mode);
    }

    // A ribbon set built without a gamez, through the same private-constructor path Load uses.
    private static DangerZoneRibbons DangerZoneRibbonsFor(params DangerZoneRibbon[] ribbons) =>
        DangerZoneRibbons.Of(ribbons);

    // The numbered route nodes alone; the "dzpaths" parent they hang under is not a zone.
    private static bool IsDangerZoneNode(string name) =>
        name.StartsWith("dzpath", StringComparison.OrdinalIgnoreCase)
        && name.Length > 6
        && int.TryParse(name.AsSpan(6), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out _);

    // A bare FlightModel stand-in the tests move by hand: the pilot reads position, attitude,
    // velocity and speed, and the arrival test is on position alone.
    private sealed class ScriptedModel
    {
        public ScriptedModel(Vector3 position)
        {
            Model = new FlightModel(new PlaneStats());
            Model.Reset(position, Basis.Identity, 80f, 0.85f);
        }

        public FlightModel Model { get; }
    }
}
