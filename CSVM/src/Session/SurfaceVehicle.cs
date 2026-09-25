using System;
using System.Collections.Generic;
using CSVM.Flight;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Session;

/// <summary>
/// One spawned surface vehicle: a chapter hull on the water, driven along its patrol net by the
/// scripted-path follower's law (<see cref="PathFollower"/>, docs/org/flightModel.md "The
/// scripted-path follower") with its height pinned to the water it was placed on. No pilot and
/// no flight model: the hull is a world subtree, its damage the destructible pool the chapter's
/// own <c>patrolboat</c> definitions register on it, and its wake and damage stages the
/// vehicle def's <c>start_anims</c> and <c>injure_anims</c> played on that subtree. Built and
/// stepped by <see cref="SurfaceVehicleRuntime"/>.
/// </summary>
public sealed class SurfaceVehicle
{
    private readonly AnimRuntime _runtime;
    private readonly DestructibleRegistry.Instance? _pool;
    private readonly IReadOnlyList<string> _startAnims;
    private readonly List<(float Fraction, string Anim)> _injureLadder;
    private readonly float _waterY;
    private PathFollower? _follower;
    private SurfaceRoute? _route;
    private AiNet? _net;
    private ZeppelinReplica? _replica;
    private bool _wakePlayed;
    private int? _team;

    internal SurfaceVehicle(string name, RosterSpawnPlan plan, Node3D body, AnimRuntime runtime,
        DestructibleRegistry.Instance? pool, IReadOnlyList<string> startAnims,
        IReadOnlyList<(float Fraction, string Anim)> injureAnims, float waterY, float heading)
    {
        Name = name;
        Plan = plan;
        Body = body;
        _runtime = runtime;
        _pool = pool;
        _startAnims = startAnims;
        _injureLadder = new List<(float, string)>(injureAnims);
        _waterY = waterY;
        Heading = heading;
        Inert = plan.Inert;
        Team = plan.Team;
        if (pool != null)
        {
            pool.Owner = name;
            pool.Team = plan.Team;
            pool.Dormant = Inert;
        }
        body.Visible = !Inert;
        if (!Inert)
        {
            PlayWake();
        }
    }

    /// <summary>Raised once, when the hull's pool reaches zero and its death sequence has run.</summary>
    public event Action<SurfaceVehicle>? Destroyed;

    /// <summary>The roster block's name, or the generator's decoded launch name.</summary>
    public string Name { get; }

    /// <summary>The name line the target box prints over this hull: its own roster block's slot-20
    /// title, resolved through the string table. EMPTY where the block authors none, which draws no
    /// name line at all and is what the original does (only C1B/M03's four boats author one of the
    /// install's 23 <c>mode ship</c> blocks). ⚠ Never the vehicle def's own <c>MSG_VEH_*</c> title:
    /// no author in the original reads it (docs/org/targeting.md). Set once by
    /// <see cref="SurfaceVehicleRuntime"/> at spawn, where the table and the block meet.</summary>
    public string MarkerName { get; internal set; } = "";

    public RosterSpawnPlan Plan { get; }

    /// <summary>The built hull, a copy of the chapter's library-root model.</summary>
    public Node3D Body { get; }

    public int Group => Plan.Group;

    /// <summary>The side, from the block's <c>team</c> slot; <c>SET_AI_TEAM</c> rewrites it and
    /// the pool follows, so the hull is that side's target.</summary>
    public int? Team
    {
        get => _pool?.Team ?? _team;
        set
        {
            _team = value;
            if (_pool != null)
            {
                _pool.Team = value;
            }
        }
    }

    /// <summary>Built <c>deactivated</c>: hidden, dormant to targeting and held, until
    /// <see cref="Wake"/>.</summary>
    public bool Inert { get; private set; }

    /// <summary>Whether the pool has reached zero. A dead hull stops where it died; the death
    /// sequence owns its parts from there.</summary>
    public bool IsDestroyed { get; private set; }

    /// <summary>The net being patrolled, or null while the hull sits with no route.</summary>
    public AiNet? Net => _net;

    /// <summary>Whether the hull follows another machine's samples instead of walking its net.
    /// </summary>
    public bool Replicated => _replica != null;

    /// <summary>The hull's live HP, or null when no definition registered a pool on it.</summary>
    public float? Health => _pool?.Health;

    public Vector3 Position => Body.GlobalPosition;

    /// <summary>Yaw in radians, Godot's convention (forward is -Z at 0).</summary>
    public float Heading { get; private set; }

    /// <summary>How fast the hull is really moving, m/s, and so the aim assist's lead term. Flat,
    /// because <see cref="WritePose"/> pins the hull to the water and drops the route's height:
    /// the follower's pitched vector is motion this hull does not have. A replicated hull reads the
    /// sent speed along its heading. A hull with no route, one still inert, or a destroyed one reads
    /// zero.</summary>
    public Vector3 Velocity =>
        Inert || IsDestroyed ? Vector3.Zero
        : _replica != null ? ZeppelinReplica.ForwardOf(_replica.YawRad, 0f) * _replica.Speed
        : _follower == null ? Vector3.Zero
        : new Vector3(_follower.Velocity.X, 0f, _follower.Velocity.Z);

    /// <summary>This hull's gun, or null when the build wired no weapon catalogue, the def arms
    /// nothing, or the model carries no mount (<see cref="SurfaceGunner.Build"/>). Set once by
    /// <see cref="SurfaceVehicleRuntime"/> at spawn.</summary>
    internal SurfaceGunner? Gunner { get; set; }

    /// <summary>The <c>WAKEUP_ENEMIES</c> half: the hull appears where it was placed, its pool
    /// becomes a target and its route, if it has one, starts. False when it was never inert.</summary>
    public bool Wake()
    {
        if (!Inert)
        {
            return false;
        }
        Inert = false;
        Body.Visible = true;
        if (_pool != null)
        {
            _pool.Dormant = false;
        }
        if (_follower != null)
        {
            _follower.Frozen = false;
        }
        PlayWake();
        return true;
    }

    /// <summary>Puts the hull on <paramref name="net"/> from where it is: the route starts at the
    /// nearest node and walks the net's edges from there, held while the hull is still inert.
    /// The <c>SET_AI_NET</c> arm and the roster spawn's own assignment.</summary>
    public void Patrol(AiNet net) => SetRoute(Array.Empty<Vector3>(), net);

    /// <summary>A generator launch: the hull runs its host's take-off path from its first point
    /// and joins <paramref name="net"/> where that path ends (the decoded surface launch, docs/
    /// formats/mission-entities/enemy-generators.md).</summary>
    public void Launch(IReadOnlyList<Vector3> run, AiNet net) => SetRoute(run, net);

    internal void Step(float dt)
    {
        if (IsDestroyed)
        {
            return;
        }
        if (_pool is { Status: DestructibleRegistry.State.Destroyed })
        {
            IsDestroyed = true;
            Gunner?.Silence();
            Destroyed?.Invoke(this);
            return;
        }
        StepInjureLadder();
        if (Inert)
        {
            return;
        }
        // The gun hangs off the AI update, not the net follower, so a hull parked with no route
        // still shoots. ⚠ It poses the turret and gun nodes, which is safe only past the death
        // check above: from there the sequence owns every child transform (see WritePose).
        Gunner?.Step(dt);
        if (_replica != null)
        {
            _replica.Step(dt);
            Heading = _replica.YawRad;
            WritePose(_replica.Position);
            return;
        }
        if (_follower == null)
        {
            return;
        }
        _follower.Step(dt);
        Heading = _follower.Heading;
        WritePose(_follower.Position);
    }

    /// <summary>Hands the hull's patrol to another machine. From here it chases that machine's
    /// samples (<see cref="ZeppelinReplica"/>'s law, no pitch) and walks no net. It holds where it
    /// stands until the first arrives. A later route assignment keeps only the net.</summary>
    internal void Replicate() => _replica ??= new ZeppelinReplica(Body.GlobalPosition, Heading, 0f);

    /// <summary>Takes one sample of the host's hull. False for a hull that is not replicated, or a
    /// sequence at or behind the newest taken.</summary>
    internal bool TakeSample(ushort sequence, Vector3 position, float speed, float yawRad) =>
        _replica?.Receive(sequence, position, speed, yawRad, 0f) == true;

    /// <summary>What the host sends of a hull on its patrol: where it is, its flat speed and its
    /// heading. False for a hull with nothing to send: inert, destroyed, routeless or replicated.
    /// </summary>
    internal bool TryReadPatrol(out Vector3 position, out float speed, out float yawRad)
    {
        position = Body.GlobalPosition;
        yawRad = Heading;
        speed = 0f;
        if (Inert || IsDestroyed || _replica != null || _follower == null)
        {
            return false;
        }
        speed = new Vector2(_follower.Velocity.X, _follower.Velocity.Z).Length();
        return true;
    }

    // A replicated hull draws no route: the draw would come off this machine's AI stream, and the
    // host's samples say where the hull goes.
    private void SetRoute(IReadOnlyList<Vector3> run, AiNet net)
    {
        _net = net;
        if (_replica != null)
        {
            return;
        }
        var start = Body.GlobalPosition;
        _route = new SurfaceRoute(start, run, net, Utils.Rng.NewSystemRandom(Utils.Rng.Ai));
        _follower = new PathFollower(_route, start, Heading)
        {
            Frozen = Inert,
            RideHeight = PathFollower.OtherClassRideHeight,
        };
    }

    // The follower steers the hull in the horizontal plane; its height is the water's, whatever
    // the net's nodes author, since a net is a route and not a waterline.
    // ⚠ The hull ROOT is the only node this class poses. The death sequence's ObjectMotions own
    // every child transform through MotionSet's channel rule (docs/org/objectMotion.md), so a
    // write here on a child would fight the sinking, the debris and the slick.
    private void WritePose(Vector3 position)
    {
        Body.GlobalPosition = new Vector3(position.X, _waterY, position.Z);
        Body.GlobalRotation = new Vector3(0f, Heading, 0f);
        RenderPoses.Record(Body);
    }

    private void PlayWake()
    {
        if (_wakePlayed)
        {
            return;
        }
        _wakePlayed = true;
        foreach (var anim in _startAnims)
        {
            int started = _runtime.PlayWithin(Body, anim).Count;
            if (started == 0)
            {
                Log.Info("world", $"surface: '{Name}' start anim '{anim}' resolves no definition on the hull");
            }
        }
    }

    // Each rung fires once as the pool falls through its fraction, the way the aircraft ladder
    // does; the fractions are of the pool the hull actually carries.
    private void StepInjureLadder()
    {
        if (_pool == null || _pool.MaxHealth <= 0f || _injureLadder.Count == 0)
        {
            return;
        }
        float fraction = _pool.Health / _pool.MaxHealth;
        for (int i = _injureLadder.Count - 1; i >= 0; i--)
        {
            if (fraction <= _injureLadder[i].Fraction)
            {
                _runtime.PlayWithin(Body, _injureLadder[i].Anim);
                _injureLadder.RemoveAt(i);
            }
        }
    }

    /// <summary>The route a hull follows as an unbounded waypoint list: the launch run first,
    /// then a walk of the net's edges from the node nearest the run's end, extended lazily as the
    /// follower advances. Unbounded so the follower never reaches its final leg, which is the
    /// aircraft climb-out and not a patrol.</summary>
    private sealed class SurfaceRoute : IReadOnlyList<Vector3>
    {
        private readonly List<Vector3> _points = new();
        private readonly int[][] _neighbours;
        private readonly Random _rng;
        private int _node;
        private int _previous = -1;

        public SurfaceRoute(Vector3 start, IReadOnlyList<Vector3> run, AiNet net, Random rng)
        {
            Net = net;
            _rng = rng;
            _points.Add(start);
            foreach (var p in run)
            {
                _points.Add(p);
            }
            var sets = new HashSet<int>[net.Nodes.Count];
            for (int i = 0; i < sets.Length; i++)
            {
                sets[i] = new HashSet<int>();
            }
            foreach (var (a, b) in net.Edges)
            {
                if (a != b && a >= 0 && b >= 0 && a < sets.Length && b < sets.Length)
                {
                    sets[a].Add(b);
                    sets[b].Add(a);
                }
            }
            _neighbours = new int[sets.Length][];
            for (int i = 0; i < sets.Length; i++)
            {
                _neighbours[i] = new int[sets[i].Count];
                sets[i].CopyTo(_neighbours[i]);
                Array.Sort(_neighbours[i]);
            }
            _node = Nearest(_points[^1]);
            _points.Add(net.Nodes[_node].Position);
        }

        public AiNet Net { get; }

        public int Count => int.MaxValue;

        public Vector3 this[int index]
        {
            get
            {
                while (_points.Count <= index)
                {
                    Extend();
                }
                return _points[index];
            }
        }

        public IEnumerator<Vector3> GetEnumerator() => _points.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private int Nearest(Vector3 from)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < Net.Nodes.Count; i++)
            {
                var p = Net.Nodes[i].Position;
                float d = new Vector2(p.X - from.X, p.Z - from.Z).LengthSquared();
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            return best;
        }

        // The next node: a random edge onward, not back the way it came unless that is the only
        // edge; a node with no edge at all holds the hull on the spot.
        private void Extend()
        {
            var options = _neighbours[_node];
            int next = _node;
            if (options.Length == 1)
            {
                next = options[0];
            }
            else if (options.Length > 1)
            {
                do
                {
                    next = options[_rng.Next(options.Length)];
                }
                while (next == _previous);
            }
            _previous = _node;
            _node = next;
            _points.Add(Net.Nodes[_node].Position);
        }
    }
}
