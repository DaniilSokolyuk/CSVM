using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Mech3;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The patrol-net walk, engine-free on hand-authored nets: traversal follows the EDGE
/// list (never node order), starts at the nearest node, refuses to backtrack while an onward
/// edge exists, turns back at a dead end, resolves branches reproducibly from its seed, and the
/// id/name lookups cover both ways the data references a net.
/// </summary>
public class AiNetFollowerTests
{
    [Fact]
    public void StartsAtTheNearestNode()
    {
        var f = new AiNetFollower(Loop(), new Random(1));
        Assert.Equal(-1, f.CurrentIndex);
        Assert.True(f.Update(new Vector3(900f, 400f, 1100f))); // nearest is node 2 (1000,1000)
        Assert.Equal(2, f.CurrentIndex);
        Assert.Equal(new Vector3(1000f, 400f, 1000f), f.CurrentTarget);
    }

    [Fact]
    public void ReseatPicksTheNearestNodeAgainFromWhereverItNowIs()
    {
        // The activation snap (BL-364): a wave member seats itself while parked, is teleported,
        // and must patrol from its arrival rather than fly back to the parking pose's node.
        var f = new AiNetFollower(Loop(), new Random(1));
        f.Update(Vector3.Zero);
        Assert.Equal(0, f.CurrentIndex);
        f.Reseat();
        Assert.Equal(-1, f.CurrentIndex);
        Assert.True(f.Update(new Vector3(900f, 400f, 1100f)));
        Assert.Equal(2, f.CurrentIndex);
    }

    [Fact]
    public void ReseatRefusesTheEdgeItWasToldToAvoid()
    {
        // The danger-zone exit (FUN_00490590): the seat pick excludes the leg the walk was on when
        // the run began, so a racer set down beside its own entry node carries on round the course
        // instead of flying that leg again and re-locking the zone it just flew.
        var f = new AiNetFollower(Loop(), new Random(1));
        var atZero = new Vector3(50f, 0f, 50f);
        var towardOne = new Vector3(1f, 0f, 0f);
        f.Reseat();
        f.Update(atZero, towardOne);
        Assert.Equal(1, f.CurrentIndex); // the nose alone picks the 0-1 leg

        f.Reseat(0, 1);
        f.Update(atZero, towardOne);
        Assert.Equal(0, f.LegStartIndex);
        Assert.Equal(3, f.CurrentIndex);

        // The exclusion is spent on the seat it applied to; the next re-seat is unconstrained.
        f.Reseat();
        f.Update(atZero, towardOne);
        Assert.Equal(1, f.CurrentIndex);
    }

    [Fact]
    public void LapsALoopAlongItsEdgesWithoutBacktracking()
    {
        // From 0 the seed picks 1 or 3; every later step has exactly one onward neighbour, so
        // the lap direction is fixed after the first hop and the walk must keep circling.
        var visited = Walk(new AiNetFollower(Loop(), new Random(1)), 8);
        Assert.Equal(0, visited[0]);
        for (int i = 1; i < visited.Count; i++)
        {
            Assert.NotEqual(visited[i], visited[i - 1]);
            if (i >= 2)
                Assert.NotEqual(visited[i], visited[i - 2]); // never the node just left
        }
        // Two full laps: position 5 revisits position 1's node, and so on around.
        for (int i = 5; i < visited.Count; i++)
            Assert.Equal(visited[i - 4], visited[i]);
    }

    [Fact]
    public void TurnsBackAtADeadEnd()
    {
        var visited = Walk(new AiNetFollower(Path(), new Random(1)), 6);
        // 0 → 1 → 2 (dead end) → 1 → 0 (dead end) → 1 → 2: the ping-pong patrol of an open path.
        Assert.Equal(new List<int> { 0, 1, 2, 1, 0, 1, 2 }, visited);
    }

    [Fact]
    public void BranchChoiceIsSeedDeterministicAndAlwaysAnEdge()
    {
        var net = Star();
        var a = Walk(new AiNetFollower(net, new Random(42)), 20);
        var b = Walk(new AiNetFollower(net, new Random(42)), 20);
        Assert.Equal(a, b); // the same seed flies the same route
        for (int i = 1; i < a.Count; i++)
        {
            bool isEdge = net.Edges.Contains((a[i - 1], a[i])) || net.Edges.Contains((a[i], a[i - 1]));
            Assert.True(isEdge, $"hop {a[i - 1]}→{a[i]} is not an edge of the net");
        }
    }

    [Fact]
    public void ArrivalIsMeasuredAlongTheLegAtATenthOfItsHorizontalLength()
    {
        var f = new AiNetFollower(Path(), new Random(1));
        f.Update(new Vector3(-500f, 400f, 0f)); // seats on node 0, so the first leg is 500 m long
        Assert.Equal(0, f.CurrentIndex);
        Assert.Equal(50f, AiNetFollower.ArrivalRadius(new Vector3(-500f, 400f, 0f), f.CurrentTarget));
        Assert.False(f.Update(new Vector3(-100f, 400f, 0f)));
        Assert.Equal(0, f.CurrentIndex);
        Assert.True(f.Update(new Vector3(-40f, 400f, 0f)));
        Assert.Equal(1, f.CurrentIndex);
        Assert.Equal(1, f.Advances);
    }

    [Fact]
    public void DrawingAbeamTheNodeArrivesHoweverFarOffToTheSideItIs()
    {
        // The whole point of the along-leg test: an aeroplane that cannot turn tightly enough
        // flows PAST its node rather than orbiting a horizontal capture circle it never enters.
        var f = new AiNetFollower(Path(), new Random(1));
        f.Update(new Vector3(-500f, 400f, 0f));
        f.Update(new Vector3(-40f, 400f, 0f));
        Assert.Equal(1, f.CurrentIndex); // node 1, on the 1000 m leg 0→1, radius 100 m
        Assert.False(f.Update(new Vector3(880f, 400f, 3000f)));
        Assert.True(f.Update(new Vector3(910f, 400f, 3000f)));
        Assert.Equal(2, f.Advances);
    }

    [Fact]
    public void ANearlyVerticalLegFallsBackOnTheNetsOwnFloor()
    {
        // A tenth of a horizontal length of zero is zero, so without CCENet+0x28's 10 m the
        // capture would be a point and a climb leg would never end.
        var straightUp = new Vector3(0f, 400f, 0f);
        Assert.Equal(10f, AiNetFollower.ArrivalRadius(Vector3.Zero, straightUp));
    }

    /// <summary>The leg the aeroplane is on, which is what <c>AiPilot.PatrolAim</c> measures its
    /// cross-track error against, and what the arrival test projects onto. Null only before the
    /// walk is seated: our first target is the nearest node rather than the far end of its edge,
    /// so the first leg starts where the vehicle was standing.</summary>
    [Fact]
    public void TheLegStartsAtTheNodeJustLeftAndBeforeThatWhereTheWalkWasSeated()
    {
        var f = new AiNetFollower(Path(), new Random(1));
        Assert.Null(f.LegStart);
        var seat = new Vector3(-500f, 400f, 0f);
        f.Update(seat);
        Assert.Equal(seat, f.LegStart);
        Assert.True(f.Update(f.CurrentTarget));
        Assert.Equal(f.NodePosition(0), f.LegStart);
        Assert.NotEqual(f.LegStart, f.CurrentTarget);
    }

    [Fact]
    public void AnIsolatedNodeIsHeldNotEscaped()
    {
        var net = new AiNet
        {
            Id = 10,
            Name = "TestIsolated",
            Nodes = new[] { Node(0f, 0f) },
            Edges = Array.Empty<(int, int)>(),
        };
        var f = new AiNetFollower(net, new Random(1));
        f.Update(Vector3.Zero);
        Assert.False(f.Update(f.CurrentTarget));
        Assert.Equal(0, f.CurrentIndex);
        Assert.Equal(0, f.Advances);
    }

    [Fact]
    public void TagsRideAlongUntouchedAndAnAnchoredNetIsFixedWithoutASupplier()
    {
        var net = new AiNet
        {
            Id = 11,
            Name = "TestTagged",
            Nodes = new[]
            {
                new AiNetNode(new Vector3(0f, 400f, 0f), new[] { 3f, 1f }),
                Node(1000f, 0f),
            },
            Edges = new[] { (0, 1) },
            Trailer = new AiNetTrailer(1, "piratezep"),
        };
        var f = new AiNetFollower(net, new Random(1));
        // Tags are exposed raw and acted on by nothing (stop-point vs segment id is still open,
        // F17 owns resolving it). The trailer IS decoded, but riding it is the caller's
        // opt-in: no supplier, no offset.
        Assert.Equal(new[] { 3f, 1f }, f.Net.Nodes[0].Tags);
        Assert.Equal(new AiNetTrailer(1, "piratezep"), f.Net.Trailer);
        Assert.False(f.Anchored);
        f.Update(Vector3.Zero);
        Assert.Equal(new Vector3(0f, 400f, 0f), f.CurrentTarget);
    }

    [Fact]
    public void AnAnchoredNetRidesItsTargetAndKeepsItsAuthoredAltitude()
    {
        // out = (node − anchor) + target in X/Z, node.y untouched. The target sits at
        // 50 m; the ring stays at its authored 400 m.
        var target = new Vector3(5000f, 50f, -2000f);
        var f = new AiNetFollower(Anchored(), new Random(1), trailerTarget: () => target);
        Assert.True(f.Anchored);
        // Anchor node 4 is at (500,500), so the offset is (4500, 0, −2500).
        Assert.Equal(new Vector3(4500f, 400f, -2500f), f.NodePosition(0));
        Assert.Equal(new Vector3(5500f, 400f, -2500f), f.NodePosition(1));
        // Moving the target moves the whole pattern, live.
        target = new Vector3(0f, 900f, 1000f);
        Assert.Equal(new Vector3(-500f, 400f, 500f), f.NodePosition(0));
    }

    [Fact]
    public void TheOffsetMovesTheSeatScanToo()
    {
        // Trap (b) on the entry: a follower that offset only its current target would seat on the
        // node nearest in AUTHORED space. Standing beside the ridden node 2 must seat on node 2.
        var target = new Vector3(10000f, 0f, 10000f);   // offset (9500, 0, 9500)
        var f = new AiNetFollower(Anchored(), new Random(1), trailerTarget: () => target);
        Assert.True(f.Update(new Vector3(10400f, 400f, 10600f)));
        Assert.Equal(2, f.CurrentIndex);
        Assert.Equal(new Vector3(10500f, 400f, 10500f), f.CurrentTarget);
    }

    [Fact]
    public void TheEdgelessAnchorNodeIsNeverSeatedOn()
    {
        // FUN_00431900 skips nodes of degree 0, and the anchor is parked off the ring in every
        // shipped case. Seated there the walk would have no neighbour and the plane would hold
        // that node forever, which is the failure this skip exists to prevent.
        var f = new AiNetFollower(Anchored(), new Random(1));
        f.Update(new Vector3(500f, 400f, 500f)); // standing ON node 4, the nearest node outright
        Assert.NotEqual(4, f.CurrentIndex);
        Assert.True(f.Update(f.CurrentTarget));  // and it advances, rather than holding
    }

    [Fact]
    public void AnUnlocatableTargetFallsBackToTheAuthoredCoordinates()
    {
        // The engine's own unresolved-target branch, and what a --fly session with no such world
        // node must do rather than collapsing the net onto the origin.
        Vector3? target = null;
        var f = new AiNetFollower(Anchored(), new Random(1), trailerTarget: () => target);
        Assert.Equal(new Vector3(1000f, 400f, 0f), f.NodePosition(1));
        target = new Vector3(200f, 0f, 300f);   // offset (−300, 0, −200)
        Assert.Equal(new Vector3(700f, 400f, -200f), f.NodePosition(1));
    }

    [Fact]
    public void TrailerOffsetIsZeroForEveryShapeThatCarriesNoAnchor()
    {
        var ring = Loop();   // no trailer at all
        Assert.Equal(Vector3.Zero, AiNetFollower.TrailerOffset(ring, new Vector3(5000f, 0f, 5000f)));
        // [-1, "name"]: a named target with no attach node (4 shipped nets).
        var unattached = new AiNet
        {
            Id = 12,
            Name = "TestUnattached",
            Nodes = ring.Nodes,
            Edges = ring.Edges,
            Trailer = new AiNetTrailer(-1, "piratezep"),
        };
        Assert.Equal(Vector3.Zero, AiNetFollower.TrailerOffset(unattached, new Vector3(5000f, 0f, 5000f)));
        // An out-of-range anchor index is unseen in this install and must not throw either.
        var broken = new AiNet
        {
            Id = 13,
            Name = "TestBrokenAnchor",
            Nodes = ring.Nodes,
            Edges = ring.Edges,
            Trailer = new AiNetTrailer(99, "player"),
        };
        Assert.Equal(Vector3.Zero, AiNetFollower.TrailerOffset(broken, Vector3.Zero));
        Assert.False(new AiNetFollower(broken, new Random(1), trailerTarget: () => Vector3.Zero).Anchored);
    }

    [Fact]
    public void LookupWorksBothWaysTheDataReferencesNets()
    {
        var nets = new List<AiNet> { Loop(), Path(), Star() };
        Assert.Same(nets[1], AiNets.ById(nets, 8));
        Assert.Null(AiNets.ById(nets, 99));
        Assert.Same(nets[2], AiNets.ByName(nets, "TestStar"));
        Assert.Same(nets[2], AiNets.ByName(nets, "teststar")); // the join key, case-insensitive
        Assert.Null(AiNets.ByName(nets, "NoSuchNet"));
        Assert.Same(nets[0], AiNets.Resolve(nets, "7"));       // all digits reads as an id
        Assert.Same(nets[0], AiNets.Resolve(nets, "TestLoop"));
        Assert.Null(AiNets.Resolve(nets, "12"));
    }

    [Fact]
    public void AnArmedStopPointHoldsTheWalkUntilTheScriptReleasesIt()
    {
        // C3/M01's shape: an open path whose middle node is stop point 1, armed in the file, and
        // whose far end is armed under id 0 so nothing can ever release it.
        var f = new AiNetFollower(StopPath(), new Random(1), observesStopPoints: true);
        f.Update(Vector3.Zero);
        Assert.Equal(0, f.CurrentIndex);
        Assert.True(f.Update(f.CurrentTarget));
        Assert.Equal(1, f.CurrentIndex);            // the armed stop point

        Assert.False(f.Update(f.CurrentTarget));    // standing on it does not advance
        Assert.True(f.Holding);
        Assert.False(f.Update(f.CurrentTarget));
        Assert.Equal(1, f.CurrentIndex);

        Assert.Equal(1, f.SetStopPoint(1, false));  // COMPLETED_STOPPOINT [net, 1, 0]
        Assert.False(f.Holding);
        Assert.True(f.Update(f.CurrentTarget));
        Assert.Equal(2, f.CurrentIndex);

        // The far end is a terminal dock: armed, and its id 0 is the one the script side rejects.
        Assert.False(f.Update(f.CurrentTarget));
        Assert.True(f.Holding);
        Assert.Equal(-1, f.SetStopPoint(0, false));
        Assert.True(f.Update(f.CurrentTarget) == false && f.CurrentIndex == 2);
    }

    [Fact]
    public void AZeppelinFollowerHoldsAtAnUnarmedDeadEndInsteadOfShuttlingBack()
    {
        // Klondike1 (piratezep's mission net): an open path whose far end authors no stop point at all.
        // The aircraft follower's own rule turns back and re-flies it (next fact); the zeppelin
        // follower holds there instead (FUN_004bf9d0's own-node no-further-edge gate).
        var f = new AiNetFollower(Path(), new Random(1), observesStopPoints: true);
        Walk(f, 2); // 0 -> 1 -> 2, the open end
        Assert.Equal(2, f.CurrentIndex);
        Assert.False(f.Update(f.CurrentTarget)); // held, not re-picking node 1
        Assert.True(f.Holding);
        Assert.Equal(2, f.CurrentIndex);
        Assert.False(f.Update(f.CurrentTarget));
        Assert.Equal(2, f.CurrentIndex); // still, no shuttle back toward node 0
    }

    [Fact]
    public void APlacedFollowerLeavesItsOwnDeadEndSeatNodeRatherThanHoldingThere()
    {
        // A zeppelin's own spawn can itself be a degree-1 node. At placement the follower has
        // flown no leg yet, so its seat node's only neighbour must not read as "just flown".
        var f = new AiNetFollower(Path(), new Random(1), observesStopPoints: true);
        Assert.True(f.Update(new Vector3(0f, 0f, 0f))); // seats exactly on node 0, a dead end
        Assert.Equal(0, f.CurrentIndex);
        Assert.False(f.Holding);
        Assert.False(f.StopsAt(f.CurrentIndex));
        Assert.True(f.Update(f.CurrentTarget), "held at its own seat instead of leaving");
        Assert.Equal(1, f.CurrentIndex);
    }

    [Fact]
    public void AnAircraftFollowerStillTurnsBackAtTheSameDeadEnd()
    {
        // The unconditional hold above is opt-in (ObservesStopPoints); an aircraft follower on
        // the identical open path keeps its own decoded rule, turning back and re-flying it.
        var f = new AiNetFollower(Path(), new Random(1));
        var visited = Walk(f, 3); // 0 -> 1 -> 2 -> back to 1
        Assert.Equal(new[] { 0, 1, 2, 1 }, visited);
        Assert.False(f.Holding);
    }

    [Fact]
    public void StopPointsAreReadableButInertOnAFollowerThatDoesNotObserveThem()
    {
        // Only the zeppelin follower reads the halt flag (FUN_004bf9d0); the aircraft one does not.
        var f = new AiNetFollower(StopPath(), new Random(1));
        Assert.False(f.ObservesStopPoints);
        Assert.True(f.StopsAt(1));
        Assert.Equal(1, f.StopPointNode(1));
        var visited = Walk(f, 2);
        Assert.Equal(new[] { 0, 1, 2 }, visited);
        Assert.False(f.Holding);
    }

    [Fact]
    public void AStopPointIdResolvesToTheFirstNodeCarryingIt()
    {
        // C2B's PirateZep1 puts one id on a run of eight nodes; the engine's scan takes its head.
        var f = new AiNetFollower(RunPath(), new Random(1), observesStopPoints: true);
        Assert.Equal(1, f.StopPointNode(5));
        Assert.Equal(-1, f.StopPointNode(4));
        Assert.Equal(1, f.SetStopPoint(5, true));
        Assert.True(f.StopsAt(1));
        Assert.False(f.StopsAt(2));
    }

    [Fact]
    public void ANodeDecodesItsFourOptionalFieldsPositionally()
    {
        Assert.Equal(0, Node(0f, 0f).StopPointId);
        Assert.False(Node(0f, 0f).StopsHere);
        Assert.Equal(-1, Node(0f, 0f).DangerZonePath);

        var stop = Tagged(3f, 1f);
        Assert.Equal(3, stop.StopPointId);
        Assert.True(stop.StopsHere);
        Assert.False(stop.EntersDangerZone);
        Assert.Equal(-1, stop.DangerZonePath);

        var dz = Tagged(0f, 0f, 1f, 34f);   // the shipped shape on M4MilesRun
        Assert.Equal(0, dz.StopPointId);
        Assert.False(dz.StopsHere);
        Assert.True(dz.EntersDangerZone);
        Assert.Equal(34, dz.DangerZonePath);
    }

    /// <summary>The seat with a heading (<c>FUN_00475fc0</c>): the nearest node becomes the node
    /// flown FROM and the target is the far end of the edge best lined up with the nose, so the
    /// first leg is an authored one rather than a run at the node.</summary>
    [Fact]
    public void AHeadingSeatsOnTheNearestNodeAndFliesTheBestAlignedEdge()
    {
        // Nearest to (100, 100) is node 0; from there the +X nose picks edge 0→1 over 0→3.
        var f = new AiNetFollower(Loop(), new Random(1));
        Assert.True(f.Update(new Vector3(100f, 400f, 100f), Vector3.Right));
        Assert.Equal(0, f.LegStartIndex);
        Assert.Equal(1, f.CurrentIndex);

        // The same seat with the nose turned the other way takes the other edge.
        var g = new AiNetFollower(Loop(), new Random(1));
        Assert.True(g.Update(new Vector3(100f, 400f, 100f), Vector3.Back));
        Assert.Equal(0, g.LegStartIndex);
        Assert.Equal(3, g.CurrentIndex);
    }

    /// <summary>Why <c>BL-498</c> broke CM02's bomber formation: aircraft seated on one node with
    /// one heading must all leave it the same way. The seeded draw split them at the first node,
    /// with nobody attacking, and the decoded pick is deterministic.</summary>
    [Fact]
    public void VehiclesSeatedOnOneNodeWithOneHeadingAllLeaveItTheSameWay()
    {
        var starts = new[]
        {
            new Vector3(60f, 400f, 40f), new Vector3(10f, 400f, -60f), new Vector3(-40f, 400f, -10f),
        };
        var picked = new List<int>();
        for (int i = 0; i < starts.Length; i++)
        {
            // A different seed each, which is what the roster hands each spawned aircraft.
            var f = new AiNetFollower(Star(), new Random(i + 1));
            f.Update(starts[i], Vector3.Right);
            picked.Add(f.CurrentIndex);
        }
        Assert.Equal(new[] { 1, 1, 1 }, picked);
    }

    /// <summary>The walk step (<c>FUN_0041d8f0</c>) runs the same pick with the edge just flown
    /// excluded, so a branch is resolved by the nose and never by the seed.</summary>
    [Fact]
    public void TheOnwardStepPicksByTheNoseAndSkipsTheEdgeJustFlown()
    {
        // Seated at node 1 (the +X end of one spoke), nose back toward the hub.
        var f = new AiNetFollower(Star(), new Random(1));
        f.Update(new Vector3(900f, 400f, 0f), Vector3.Left);
        Assert.Equal(1, f.LegStartIndex);
        Assert.Equal(0, f.CurrentIndex);

        // At the hub, still heading −X: node 3 is straight ahead, node 1 is the edge just flown.
        Assert.True(f.Update(f.CurrentTarget, Vector3.Left));
        Assert.Equal(3, f.CurrentIndex);
    }

    private static AiNetNode Node(float x, float z) => new(new Vector3(x, 400f, z), Array.Empty<float>());

    private static AiNetNode Tagged(params float[] tags) => new(new Vector3(0f, 400f, 0f), tags);

    // A 4-node square loop, 0-1-2-3-0. Edge (0,3) closes it, so a directed reading
    // would dead-end; the undirected walk is what carries the lap.
    private static AiNet Loop() => new()
    {
        Id = 7,
        Name = "TestLoop",
        Nodes = new[] { Node(0f, 0f), Node(1000f, 0f), Node(1000f, 1000f), Node(0f, 1000f) },
        Edges = new[] { (0, 1), (1, 2), (2, 3), (0, 3) },
    };

    // An open 3-node path 0-1-2, with no loop to hide a backtracking bug in.
    private static AiNet Path() => new()
    {
        Id = 8,
        Name = "TestPath",
        Nodes = new[] { Node(0f, 0f), Node(1000f, 0f), Node(2000f, 0f) },
        Edges = new[] { (0, 1), (1, 2) },
    };

    // C1's `M4ReinfAce` in miniature: a square ring 0-1-2-3-0 plus an EDGELESS
    // anchor node parked off it, and a `[4, "player"]` trailer. That is the shipped shape of
    // all 76 anchored nets: the anchor is the last node and carries no edge.
    private static AiNet Anchored() => new()
    {
        Id = 10,
        Name = "TestAnchored",
        Nodes = new[]
        {
            Node(0f, 0f), Node(1000f, 0f), Node(1000f, 1000f), Node(0f, 1000f), Node(500f, 500f),
        },
        Edges = new[] { (0, 1), (1, 2), (2, 3), (0, 3) },
        Trailer = new AiNetTrailer(4, "player"),
    };

    // An open 3-node path whose middle node is stop point 1 (armed) and whose far end is armed
    // under the unaddressable id 0, C3/M01's M1PirateZep in miniature.
    private static AiNet StopPath() => new()
    {
        Id = 11,
        Name = "TestStops",
        Nodes = new[]
        {
            Node(0f, 0f),
            new AiNetNode(new Vector3(1000f, 400f, 0f), new[] { 1f, 1f }),
            new AiNetNode(new Vector3(2000f, 400f, 0f), new[] { 0f, 1f }),
        },
        Edges = new[] { (0, 1), (1, 2) },
    };

    // One stop-point id spread over a RUN of nodes, the C2B PirateZep1 shape.
    private static AiNet RunPath() => new()
    {
        Id = 12,
        Name = "TestRun",
        Nodes = new[]
        {
            Node(0f, 0f),
            new AiNetNode(new Vector3(1000f, 400f, 0f), new[] { 5f, 0f }),
            new AiNetNode(new Vector3(2000f, 400f, 0f), new[] { 5f, 0f }),
        },
        Edges = new[] { (0, 1), (1, 2) },
    };

    // A hub with three spokes: node 0 connects to 1, 2 and 3 (the branch case).
    private static AiNet Star() => new()
    {
        Id = 9,
        Name = "TestStar",
        Nodes = new[] { Node(0f, 0f), Node(1000f, 0f), Node(0f, 1000f), Node(-1000f, 0f) },
        Edges = new[] { (0, 1), (0, 2), (0, 3) },
    };

    // Walks by teleporting to each target in turn. Arrival is by construction, so what is
    // under test is purely which node the follower picks next.
    private static List<int> Walk(AiNetFollower f, int hops)
    {
        var visited = new List<int>();
        f.Update(Vector3.Zero); // first call: nearest node
        visited.Add(f.CurrentIndex);
        for (int i = 0; i < hops; i++)
        {
            Assert.True(f.Update(f.CurrentTarget), $"hop {i}: standing at the target did not advance");
            visited.Add(f.CurrentIndex);
        }
        return visited;
    }
}
