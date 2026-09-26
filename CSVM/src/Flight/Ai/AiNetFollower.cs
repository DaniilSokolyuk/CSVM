using System;
using System.Collections.Generic;
using CSVM.Mech3;
using Godot;

namespace CSVM.Flight.Ai;

/// <summary>Walks an <see cref="AiNet"/> patrol graph as a stream of waypoints: positions in, the
/// current target node out. Aircraft-agnostic on purpose, so <c>ZeppelinMotion</c> (F17) reuses it;
/// <see cref="AiPilot.Patrol"/> is the aircraft consumer, and turns back and re-flies a dead end
/// (<see cref="PickOnward"/>) as its own decoded rule. Traversal is decoded in this module's
/// docs/architecture.md entry; the anchored-trailer ride is docs/formats/ai-nets.md.
/// Arrival is the decoded along-leg test (<see cref="ArrivalRadius"/>). A node's stop point
/// (<see cref="AiNetNode.StopPointId"/>) is live state here, armed and disarmed by
/// <see cref="SetStopPoint"/>; a reached node's <see cref="AiNetNode.EntersDangerZone"/> is
/// reported through <see cref="ArrivedNode"/> for the aircraft pilot to act on.
/// A caller observing stop points (<see cref="ObservesStopPoints"/>) also holds unconditionally at
/// a structural dead end instead of turning back (<see cref="Update"/>, `FUN_004bf9d0`).</summary>
public sealed class AiNetFollower
{
    /// <summary>The floor on a leg's arrival radius, metres: <c>CCENet+0x28</c>, whose constructor
    /// (<c>FUN_004303d0</c>) seats 10 and which every shipped net leaves at that default.</summary>
    public const float MinArrivalRadiusM = 10f;

    /// <summary>How near an armed stop point counts as parked on it, metres: the zeppelin
    /// follower's own hold distance (<c>FUN_004bf360</c>), which is NOT the leg's arrival radius.
    /// A stop point is a place to sit, so the original drops the abeam capture and holds the
    /// vehicle on the node itself.</summary>
    public const float StopPointHoldM = 30f;

    // The radius is a tenth of the leg's HORIZONTAL length, floored (FUN_00431a90, which squares
    // it into edge+0x1c at net load). Altitude change along a leg does not widen the capture.
    private const float ArrivalRadiusPerLeg = 0.1f;

    // Below this a heading is no facing at all, and the seeded draw stands in for it.
    private const float HeadingEpsilon = 1e-6f;

    private readonly Random _rng;
    private readonly bool[] _stops;
    private readonly float _minArrivalRadius;
    private readonly int[][] _neighbors;
    private readonly Func<Vector3?>? _trailerTarget;
    private readonly int _anchorIndex = -1;
    private int _previousIndex = -1;
    private int _avoidFrom = -1;
    private int _avoidTo = -1;
    private Vector3? _legOrigin;

    /// <param name="trailerTarget">Where the net's trailer target is right now, null when it cannot
    /// be located; only a caller that WANTS the net to ride passes one. Called per node read.</param>
    /// <param name="minArrivalRadius">The net's own floor on the per-leg radius. Raised only by a
    /// caller whose vehicle cannot turn inside the decoded one; never lowers it.</param>
    /// <param name="observesStopPoints">⚠ Whether an armed stop point halts this walk. Off unless
    /// the caller is the ZEPPELIN follower, the only one that reads the flag (architecture.md).</param>
    public AiNetFollower(AiNet net, Random rng, float minArrivalRadius = MinArrivalRadiusM,
        Func<Vector3?>? trailerTarget = null, bool observesStopPoints = false)
    {
        if (net.Nodes.Count == 0)
            throw new ArgumentException($"net '{net.Name}#{net.Id}' has no nodes", nameof(net));
        Net = net;
        _rng = rng;
        _minArrivalRadius = Mathf.Max(minArrivalRadius, MinArrivalRadiusM);
        if (trailerTarget != null && net.Trailer is { NodeIndex: >= 0 } trailer
            && trailer.NodeIndex < net.Nodes.Count)
        {
            _anchorIndex = trailer.NodeIndex;
            _trailerTarget = trailerTarget;
        }

        ObservesStopPoints = observesStopPoints;

        // The file's halt flags are the net's starting state, then the script owns them.
        _stops = new bool[net.Nodes.Count];
        for (int i = 0; i < _stops.Length; i++)
            _stops[i] = net.Nodes[i].StopsHere;

        // Undirected adjacency off the explicit edge list, never node order (the graph
        // branches; a list-order walk is the documented wrong reading).
        var sets = new HashSet<int>[net.Nodes.Count];
        for (int i = 0; i < sets.Length; i++)
            sets[i] = new HashSet<int>();
        foreach (var (a, b) in net.Edges)
        {
            if (a == b)
                continue;
            sets[a].Add(b);
            sets[b].Add(a);
        }
        _neighbors = new int[sets.Length][];
        for (int i = 0; i < sets.Length; i++)
        {
            _neighbors[i] = new int[sets[i].Count];
            sets[i].CopyTo(_neighbors[i]);
            Array.Sort(_neighbors[i]); // stable candidate order, so the rng draw is reproducible
        }
    }

    /// <summary>The net being flown: nodes with their authored fields, edges, and the trailer.
    /// Node halt flags here are the STARTING state; the live ones are this follower's.</summary>
    public AiNet Net { get; }

    /// <summary>The node index currently flown toward; −1 before the first
    /// <see cref="Update"/> picks the nearest node.</summary>
    public int CurrentIndex { get; private set; } = -1;

    /// <summary>The node the current leg starts at, −1 while the walk is flying at its own seat
    /// node. This is the engine's <c>obj+0x2e8</c>, the node a vehicle is flying FROM.</summary>
    public int LegStartIndex => _previousIndex;

    /// <summary>How many node captures have advanced the target so far.</summary>
    public int Advances { get; private set; }

    /// <summary>The node the last <see cref="Update"/> arrived at and stepped past, or null when
    /// that call advanced nothing. The aircraft follower's danger-zone report: the original reads
    /// the reached node's <c>+0x11</c>/<c>+0x14</c> right after the step (<c>FUN_0041d1f0</c>),
    /// so <see cref="AiPilot"/> starts a run off this and never off the node it flies toward.</summary>
    public AiNetNode? ArrivedNode { get; private set; }

    /// <summary>Whether an armed stop point halts this walk at all (the constructor's
    /// <c>observesStopPoints</c>). The flags are still readable and writable when it is false.
    /// </summary>
    public bool ObservesStopPoints { get; }

    /// <summary>Parked on an armed stop point, OR on a structural dead end (see
    /// <see cref="ObservesStopPoints"/>): <see cref="Update"/> has brought the follower within
    /// <see cref="StopPointHoldM"/> of <see cref="CurrentIndex"/> and either that node's halt
    /// flag is set or it has no further edge to fly, so the walk goes no further. The zeppelin
    /// law reads this to hold station.</summary>
    public bool Holding { get; private set; }

    /// <summary>True when this follower rides its net's trailer target: the net has an
    /// anchor node AND the caller supplied a target. False is the fixed-route case, which is every
    /// unanchored net and every caller that did not opt in.</summary>
    public bool Anchored => _anchorIndex >= 0;

    /// <summary>The position of the node currently flown toward, trailer offset included. Only
    /// valid once <see cref="Update"/> has run.</summary>
    public Vector3 CurrentTarget => NodePosition(CurrentIndex);

    /// <summary>Where the leg being flown STARTS, trailer offset included: the node just left, or
    /// the seat node when <see cref="Update"/> was given a heading. A caller with no heading is
    /// seated on the nearest node itself, and its first leg starts where the vehicle was.
    /// Null only before the first <see cref="Update"/>. <see cref="AiPilot.PatrolAim"/> and the
    /// arrival test are the consumers.</summary>
    public Vector3? LegStart =>
        _previousIndex >= 0 ? NodePosition(_previousIndex)
        : _legOrigin is { } o ? o + LiveOffset()
        : null;

    /// <summary>The radius, metres, at which a leg from <paramref name="legStart"/> to
    /// <paramref name="node"/> counts as reached, a tenth of its horizontal length, floored at
    /// <see cref="MinArrivalRadiusM"/>. The engine keeps the square in <c>edge+0x1c</c>.</summary>
    public static float ArrivalRadius(Vector3 legStart, Vector3 node, float floorM = MinArrivalRadiusM)
    {
        var leg = node - legStart;
        return Mathf.Max(ArrivalRadiusPerLeg * Mathf.Sqrt((leg.X * leg.X) + (leg.Z * leg.Z)), floorM);
    }

    /// <summary>How far an anchored net's nodes are carried from their authored coordinates right
    /// now: <c>target − anchorNode</c> in X and Z, <b>zero in Y</b>, because the pattern keeps its
    /// authored altitude, so a ring authored at 400 m stays at 400 m over a zeppelin at 200 m.
    /// <see cref="Vector3.Zero"/> when the net is unanchored or the target cannot be located,
    /// which is the engine's own no-trailer branch (`FUN_00432010`). Static because the overlay
    /// needs the same offset for a net nobody is flying.</summary>
    public static Vector3 TrailerOffset(AiNet net, Vector3? target)
    {
        if (target is not { } t || net.Trailer is not { NodeIndex: >= 0 } trailer
            || trailer.NodeIndex >= net.Nodes.Count)
            return Vector3.Zero;
        var anchor = net.Nodes[trailer.NodeIndex].Position;
        return new Vector3(t.X - anchor.X, 0f, t.Z - anchor.Z);
    }

    /// <summary>Where node <paramref name="index"/> is right now: its authored position plus the
    /// live trailer offset. EVERY node read goes through here, because the original's does: a
    /// follower that offset only its current target would seat itself on the wrong node.</summary>
    public Vector3 NodePosition(int index) => Net.Nodes[index].Position + LiveOffset();

    /// <summary>Drops the walk back to "nearest node next", so the next <see cref="Update"/>
    /// re-seats from wherever the follower now is: the original's activation snap
    /// (<c>FUN_004b0f40</c> into <c>FUN_00432010</c>) and its danger-zone exit
    /// (<c>FUN_00490590</c>) alike, both decoded in this module's architecture.md entry.</summary>
    /// <param name="avoidFrom">One end of an edge the next seat pick must refuse, −1 for none.</param>
    /// <param name="avoidTo">The other end of that edge.</param>
    public void Reseat(int avoidFrom = -1, int avoidTo = -1)
    {
        CurrentIndex = -1;
        _previousIndex = -1;
        _legOrigin = null;
        Holding = false;
        ArrivedNode = null;
        _avoidFrom = avoidFrom;
        _avoidTo = avoidTo;
    }

    /// <summary>Whether node <paramref name="index"/> currently halts whoever reaches it: an
    /// armed stop point, or (for a caller observing stop points) the structural dead end this
    /// walk is currently sitting on. <see cref="Airframe.ZeppelinMotion.TargetSpeed"/> reads this to ramp
    /// the throttle down on approach, the same as an armed stop, so the dead-end hold below
    /// (<see cref="Update"/>) is never reached still at cruise speed.</summary>
    public bool StopsAt(int index) => index >= 0 && index < _stops.Length
        && (_stops[index] || IsStructuralDeadEnd(index));

    /// <summary>The node an id addresses: the FIRST node carrying it, the engine's own scan
    /// (<c>FUN_004319a0</c>). −1 when no node does, and for id 0, which the script side rejects
    /// before it ever gets here (<c>FUN_0046a0d0</c> tests <c>id &gt; 0</c>) and which 27 shipped
    /// nodes carry as their "no stop point" value.</summary>
    public int StopPointNode(int stopPointId)
    {
        if (stopPointId <= 0)
        {
            return -1;
        }
        for (int i = 0; i < Net.Nodes.Count; i++)
        {
            if (Net.Nodes[i].StopPointId == stopPointId)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Arms or disarms the stop point <paramref name="stopPointId"/> names, the whole of
    /// what <c>COMPLETED_STOPPOINT</c> does. Returns the node it wrote, or −1 when the net carries
    /// no such id. Disarming the node being held releases the walk on the next
    /// <see cref="Update"/>.</summary>
    public int SetStopPoint(int stopPointId, bool halts)
    {
        int node = StopPointNode(stopPointId);
        if (node < 0)
        {
            return -1;
        }
        _stops[node] = halts;
        if (!halts && node == CurrentIndex)
        {
            Holding = false;
        }
        return node;
    }

    /// <summary>Advances the walk from <paramref name="position"/>: the first call seats on the
    /// nearest node and targets the far end of an edge, and drawing abeam that node steps on.
    /// Returns true when the target changed.</summary>
    /// <param name="heading">The vehicle's nose, which picks the edge. <see cref="Vector3.Zero"/>
    /// for a caller with none: it seats on the nearest node as a target and draws instead.</param>
    // ⚠ The test is ALONG the leg, not a distance to the node (FUN_0041d1f0, after the law call):
    // it fires however far off to the side the vehicle is, so one that cannot turn tightly enough
    // flows past its node instead of orbiting it forever.
    public bool Update(Vector3 position, Vector3 heading = default)
    {
        ArrivedNode = null;
        if (CurrentIndex < 0)
        {
            int seat = NearestNode(position);
            int avoid = AvoidedNeighborOf(seat);
            _avoidFrom = -1;
            _avoidTo = -1;
            if (heading.LengthSquared() > HeadingEpsilon && _neighbors[seat].Length > 0)
            {
                _previousIndex = seat;
                _legOrigin = null;
                CurrentIndex = PickOnward(seat, avoid, heading);
            }
            else
            {
                CurrentIndex = seat;
                _legOrigin = position - LiveOffset();
            }
            return true;
        }
        var node = CurrentTarget;
        // ⚠ A structural dead end holds unconditionally, not via the aircraft follower's own
        // turn-back (PickOnward's degree-1 short-circuit): decoded in
        // docs/formats/mission-entities.md "Route ends and stop points" (FUN_004bf9d0).
        if (ObservesStopPoints && StopsAt(CurrentIndex))
        {
            // An armed stop point (or a structural dead end) is never advanced past, however far
            // past it the vehicle drifts.
            Holding = position.DistanceSquaredTo(node) <= StopPointHoldM * StopPointHoldM;
            return false;
        }
        var start = LegStart ?? position;
        var leg = node - start;
        if (leg.LengthSquared() > 1e-6f
            && (position - node).Dot(leg.Normalized()) <= -ArrivalRadius(start, node, _minArrivalRadius))
            return false;
        if (_neighbors[CurrentIndex].Length == 0)
            return false; // an isolated node is held, not escaped by inventing an edge
        int next = PickOnward(CurrentIndex, _previousIndex, heading);
        ArrivedNode = Net.Nodes[CurrentIndex];
        _previousIndex = CurrentIndex;
        _legOrigin = null;
        CurrentIndex = next;
        Advances++;
        return true;
    }

    // The onward neighbour of `from`, excluding `exclude` (the node just left, −1 at the seat)
    // when anything else is available; a path net still turns back at its dead ends.
    // ⚠ With a heading this is the engine's own rule and must stay deterministic: the edge whose
    // leg direction best lines up with the vehicle's nose (FUN_00431e40, called from the net
    // assignment and from the walk step). Vehicles seated on one node with one heading must all
    // leave it the same way, which is what keeps a group flying one net in formation.
    private int PickOnward(int from, int exclude, Vector3 heading)
    {
        var candidates = _neighbors[from];
        if (candidates.Length == 1)
            return candidates[0];
        int onward = 0;
        foreach (int c in candidates)
        {
            if (c != exclude)
                onward++;
        }
        if (onward == 0)
        {
            exclude = -1;
            onward = candidates.Length;
        }
        if (heading.LengthSquared() > HeadingEpsilon)
        {
            var nose = heading.Normalized();
            var origin = Net.Nodes[from].Position;
            int best = -1;
            float bestDot = float.NegativeInfinity;
            foreach (int c in candidates)
            {
                if (c == exclude)
                    continue;
                var leg = Net.Nodes[c].Position - origin;
                if (leg.LengthSquared() < 1e-6f)
                    continue;
                float aligned = leg.Normalized().Dot(nose);
                if (aligned > bestDot)
                {
                    bestDot = aligned;
                    best = c;
                }
            }
            if (best >= 0)
                return best;
        }
        int pick = _rng.Next(onward);
        foreach (int c in candidates)
        {
            if (c == exclude)
                continue;
            if (pick-- == 0)
                return c;
        }
        return candidates[0]; // unreachable; keeps the compiler satisfied
    }

    // The neighbour of `seat` across the edge Reseat was told to avoid, or −1 when that edge is
    // not incident to the seat. The engine excludes an EDGE ID from the seat pick (FUN_00490590's
    // exit passes the leg the walk was on, FUN_00431e40 skips the candidate carrying it); an
    // undirected neighbour list says the same thing by naming the far end.
    private int AvoidedNeighborOf(int seat) =>
        seat == _avoidFrom ? _avoidTo
        : seat == _avoidTo ? _avoidFrom
        : -1;

    // The current node's only edge is the one just flown: nowhere further to go. Scoped to
    // CurrentIndex (never a lookahead) because _previousIndex describes THIS leg only.
    private bool IsStructuralDeadEnd(int index) => ObservesStopPoints && index == CurrentIndex
        && index >= 0 && _neighbors[index].Length == 1 && _neighbors[index][0] == _previousIndex;

    private Vector3 LiveOffset() =>
        _anchorIndex < 0 ? Vector3.Zero : TrailerOffset(Net, _trailerTarget!());

    // The seat scan runs in authored space: a uniform offset moves every node equally.
    // ⚠ Edgeless nodes are skipped, the engine's own rule (docs/org/aiPilot.md); a net whose nodes
    // are all edgeless still gets a seat rather than nothing.
    private int NearestNode(Vector3 position)
    {
        var local = position - LiveOffset();
        int best = -1;
        float bestSq = float.MaxValue;
        for (int i = 0; i < Net.Nodes.Count; i++)
        {
            if (_neighbors[i].Length == 0)
                continue;
            float dSq = Net.Nodes[i].Position.DistanceSquaredTo(local);
            if (dSq < bestSq)
            {
                bestSq = dSq;
                best = i;
            }
        }
        if (best >= 0)
            return best;
        for (int i = 0; i < Net.Nodes.Count; i++)
        {
            float dSq = Net.Nodes[i].Position.DistanceSquaredTo(local);
            if (dSq < bestSq)
            {
                bestSq = dSq;
                best = i;
            }
        }
        return Math.Max(best, 0);
    }
}
