using System;
using System.Collections.Generic;
using CSVM.Flight;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Session;

/// <summary>What one generator launch built: an aircraft, a surface vehicle, or nothing (the
/// decoded empty launch). A bare <see cref="FlightController"/> converts, so an aircraft-only
/// spawner keeps its shape.</summary>
public readonly record struct LaunchedVehicle(FlightController? Aircraft, SurfaceVehicle? Vessel)
{
    /// <summary>The spawner declined to build: this end replicates its generator aircraft from
    /// the host, so the cycle's slot is handed back and nothing is counted.</summary>
    public static LaunchedVehicle Refusal => new(null, null) { Refused = true };

    public bool IsEmpty => Aircraft == null && Vessel == null;

    /// <summary>Whether this is <see cref="Refusal"/>.</summary>
    public bool Refused { get; init; }

    public static implicit operator LaunchedVehicle(FlightController? aircraft) => new(aircraft, null);
}

/// <summary>One generator aircraft launch as the host made it. It holds the live generator's
/// index, the launch counter the name was formed from and the patrol net's index in that
/// generator's list. It also holds the pose, velocity and lever the aircraft left on. A replicating end rebuilds the launch from
/// this alone, through <see cref="AiGeneratorRuntime.LaunchReplicated"/>.</summary>
public readonly record struct GeneratorAircraftLaunch(int Generator, int LaunchOrdinal, int Net,
    Vector3 Position, Vector3 Drop, Vector3? Velocity, bool CarrierDrop, float? Throttle);

/// <summary>Runs a mission's enemy generators (M4 B6 + F20): each loaded
/// <see cref="EnemyGeneratorDef"/> gets a <see cref="GeneratorCycle"/> and spawns AI aircraft
/// through the handed roster callback as its waves come due, dropping at the origin node's live
/// position in the authored drop attitude and patrolling the cyclic net pick through
/// <see cref="AiNetFollower"/>; a surface host's launch first flies its take-off path under
/// <see cref="PathFollower"/>, and a hull launch runs that path and joins its net. A generator
/// whose host or whole nets list fails to resolve is dropped at load, never loaded inert. Every
/// load drop, door transition and spawn prints an <c>egen:</c> line. Every cycle starts
/// uncredited and launches only what <see cref="GrantWaveCapacity"/> feeds it (a script's
/// <c>WAKEUP_GENERATOR</c>, an Instant Action wave, or cutscene callback 800 through
/// <see cref="BindCallbackHost"/>). ⚠ <see cref="UseInstantActionLaunches"/> chooses the launch
/// source, see this module's entry in docs/architecture.md before touching it.</summary>
public sealed partial class AiGeneratorRuntime : Node
{
    /// <summary>The generator cutscene callback 800 credits, by host node. The original looks
    /// the name up literally in its mission-script host (<c>FUN_0047e080</c>), so this is a
    /// constant and not data.</summary>
    public const string CallbackCreditedHost = "cargozep1";

    /// <summary>The launches callback 800 adds, the original's literal.</summary>
    public const int CallbackCredit = 5;

    /// <summary>The most recent spawn's net pick, per generator node.</summary>
    public readonly Dictionary<string, string> SpawnedNet = new(StringComparer.OrdinalIgnoreCase);

    // The mission-script host's one credit callback. 801 to 803 live in the same host in the
    // original and reactivate the first still-deactivated bhat aircraft, which CampaignDirector
    // answers from its own link in this chain.
    private const int CreditCallbackCode = 800;

    // The spawn basis needs a horizontal component (Basis.LookingAt with world up),
    // so the authored −90° drop pitch is clamped this far shy of vertical. Invented margin;
    // the −90° drop attitude itself is the authored `rotation`.
    private const float MaxDropPitchDeg = 80f;

    // FUN_00452450 preserves the carrier's world velocity, then adds this vertical component.
    private const float CarrierLaunchDownwardSpeed = 22.352f;

    // A surface launch is placed this far above its first take-off path point, and opens the
    // throttle where a carrier drop closes it (FUN_00451bf0's multi-point branch).
    private const float SurfaceLaunchLift = 0.2f;
    private const float SurfaceLaunchThrottle = 1f;

    // FUN_0048a110 adds this to the velocity's Y on reaching the last waypoint of an aircraft-class
    // run, the one kick the handoff gives the flight model.
    private const float HandoffUpwardSpeed = 1f;

    // How far along a take-off path the loader will look. The shipped paths run 4 to 6 points;
    // only the first two place a launch, and the rest are the run the aircraft has yet to fly.
    private const int MaxLaunchPathPoints = 16;

    private readonly List<LiveGenerator> _live = new();
    private readonly List<TakeOffRun> _runs = new();
    private readonly string _planeName;
    private readonly Func<EnemyGeneratorDef, Vector3, Vector3, AiPilot, LaunchedVehicle> _spawn;
    private readonly Func<string, Node3D, int>? _playAnim;
    private readonly Action<string, Node3D>? _stopAnim;
    private readonly Func<AiNet, Func<Vector3?>?>? _trailerTarget;
    private readonly Func<int, string?, string?, bool> _host;
    private Func<int, string?, string?, bool>? _inner;
    private bool _replaying;

    /// <param name="trailerTarget">Where an anchored net's trailer target is, per net;
    /// null leaves every generated patroller on its net's authored coordinates. One generator's
    /// nets are player-anchored in the shipped data (C5's <c>M4Miles</c>), so this is not
    /// hypothetical.</param>
    public AiGeneratorRuntime(IReadOnlyList<EnemyGeneratorDef> defs,
        Func<string, Node3D?, Node3D?>? resolveNode, IReadOnlyList<AiNet> chapterNets,
        string planeName, Func<EnemyGeneratorDef, Vector3, Vector3, AiPilot, LaunchedVehicle> spawn,
        Func<string, Node3D, int>? playAnim = null, Action<string, Node3D>? stopAnim = null,
        Func<AiNet, Func<Vector3?>?>? trailerTarget = null)
    {
        Name = "ai_generators";
        _planeName = planeName;
        _spawn = spawn;
        _playAnim = playAnim;
        _stopAnim = stopAnim;
        _trailerTarget = trailerTarget;
        _host = Host;
        foreach (var def in defs)
        {
            // Load-drop 1: unresolved host node (decoded rule: dropped, not loaded inert).
            Node3D? host = resolveNode?.Invoke(def.Node, null);
            if (host == null)
            {
                Log.Info("flight", $"egen: generator '{def.Node}' dropped: host node unresolved{(resolveNode == null ? " (no world runtime on this stage)" : "")}");
                continue;
            }
            // Load-drop 2: none of the nets resolve against the chapter's neindex names.
            var nets = new List<AiNet>();
            foreach (var net in def.Nets)
            {
                if (AiNets.ByName(chapterNets, net) is { } resolved)
                {
                    nets.Add(resolved);
                }
            }
            if (nets.Count == 0)
            {
                Log.Info("flight", $"egen: generator '{def.Node}' dropped: no net of [{string.Join(", ", def.Nets)}] resolves");
                continue;
            }
            // The zeppelin drop point; a miss falls back to the host node (not a drop condition).
            Node3D? origin = def.Origin != null ? resolveNode!(def.Origin, host) : null;
            // Load-drop 3: a surface host launches along a take-off path in its own subtree, and
            // one shorter than two points rejects the generator (decoded rule).
            var path = def.IsZeppelin ? null : ResolveLaunchPath(def.Node, host, resolveNode);
            if (!def.IsZeppelin && path == null)
            {
                Log.Info("flight", $"egen: generator '{def.Node}' dropped: no take-off path '{EnemyGenerators.LaunchPathNode(def.Node, 0)}'/'{EnemyGenerators.LaunchPathNode(def.Node, 1)}' under the host");
                continue;
            }
            _live.Add(new LiveGenerator(def, new GeneratorCycle(def), host, origin, nets) { LaunchPath = path });
            string kind = def.IsZeppelin ? "zeppelin launch" : def.MovingPath ? "moving spawner" : "spawner";
            var netNames = new List<string>(nets.Count);
            foreach (var n in nets)
            {
                netNames.Add(n.Name);
            }
            Log.Info("flight", $"egen: generator '{def.Node}' live ({kind}), wave {def.WaveSize} every {def.IndPeriod + def.WavePeriod:0.#} s (ind {def.IndPeriod:0.#} + wave {def.WavePeriod:0.#}), max_active {def.MaxActive}, nets [{string.Join(", ", netNames)}]{(def.MinAltitude is { } gate ? Log.Format($", launch gate {gate:0} m") : "")}{(path != null ? $", take-off path {path.Count} point(s)" : "")}{(def.OpenAnim != null ? $", doors '{def.OpenAnim}'/'{def.CloseAnim}'" : ", doors unnamed (timing runs log-only)")}");
        }
    }

    /// <summary>Raised after every aircraft a generator launched on its own cycle, with the
    /// aircraft. An Instant Action release is not a launch here, since it builds nothing.</summary>
    public event Action<GeneratorAircraftLaunch, FlightController>? AircraftLaunched;

    /// <summary>The launch counter the instance names of this mission's launches run on: it
    /// starts at 0 with the generators and advances once per successful launch across all of
    /// them, which is what makes a name unique (docs/formats/mission-entities/enemy-generators.md).
    /// Read it BEFORE the spawn whose name it forms.</summary>
    public int LaunchOrdinal { get; private set; }

    /// <summary>Whether this end's generator aircraft come from the host rather than its own
    /// cycles (<see cref="Replicate"/>).</summary>
    public bool Replicated { get; private set; }

    /// <summary>Whether the spawner should refuse this launch: this end replicates, and the call
    /// is not the replay of a host launch.</summary>
    public bool RefusesOwnAircraft => Replicated && !_replaying;

    /// <summary>Launches this end's own cycles came due for and the spawner refused.</summary>
    public int RefusedLaunches { get; private set; }

    /// <summary>Generators that survived the load drops.</summary>
    public int LiveCount => _live.Count;

    /// <summary>Launches still flying their host's take-off run, not yet handed to their net.</summary>
    public int RunningCount => _runs.Count;

    /// <summary>Takes the runtime's <c>CALLBACK</c> host slot for the mission-script host's credit
    /// code, chaining to whatever held it. ⚠ Bind once, after every other chained host has bound:
    /// a host that re-binds after this one would chain to this host while this host still chains
    /// to its old delegate, and an unanswered code would then loop between the two.</summary>
    public void BindCallbackHost(AnimRuntime runtime)
    {
        if (runtime.CallbackHost != _host)
        {
            _inner = runtime.CallbackHost;
            runtime.CallbackHost = _host;
        }
    }

    /// <summary>Instant Action's zeppelin arm: every generator hosted on
    /// <paramref name="hostNode"/> releases an already-built wave member through
    /// <paramref name="release"/> instead of spawning a fresh aircraft, the decoded shape (this
    /// module's entry in docs/architecture.md). A null release is accounted like a failed spawn.
    /// Returns how many generators this claimed; 0 means the selected zeppelin carries no
    /// generator, so nothing on that mode will ever launch.</summary>
    public int UseInstantActionLaunches(string hostNode,
        Func<Vector3, Vector3, Vector3, FlightController?> release)
    {
        int claimed = 0;
        foreach (var gen in _live)
        {
            if (!gen.Def.Node.Equals(hostNode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            gen.Release = release;
            claimed++;
        }
        return claimed;
    }

    /// <summary>The credit every launch comes from: adds <paramref name="count"/> to the
    /// remaining capacity of every generator hosted on <paramref name="hostNode"/>, the shape of
    /// the objective apply (<c>FUN_00469af0</c>), the wave director's top-up (<c>FUN_0045b9d0</c>)
    /// and callback 800 alike. Returns how many generators were credited. ⚠ The original's wave
    /// counter advances whether or not the top-up lands, so a wave whose zeppelin has no generator
    /// is simply lost, the caller reports it rather than compensating.</summary>
    public int GrantWaveCapacity(string hostNode, int count)
    {
        int fed = 0;
        foreach (var gen in _live)
        {
            if (!gen.Def.Node.Equals(hostNode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            gen.Cycle.GrantCapacity(count);
            fed++;
        }
        return fed;
    }

    /// <summary>The named host died: every generator whose host node (or authored
    /// <c>healthy</c> node, the submarine's) carries this name launches for
    /// <see cref="GeneratorCycle.HostDeathGraceSeconds"/> more, then disables permanently. Fed by
    /// <c>ZeppelinRuntime.ZeppelinKilled</c> and, for a fixed installation, by
    /// <c>AnimRuntime.DestructibleKilled</c>. A disabled generator's door keeps its last state (the
    /// decoded loop early-outs before any door rule). Returns how many went on the grace.</summary>
    public int NotifyHostDied(string nodeName)
    {
        int dying = 0;
        foreach (var gen in _live)
        {
            if (gen.Cycle.Disabled || gen.Cycle.HostDead
                || (!gen.Def.Node.Equals(nodeName, StringComparison.OrdinalIgnoreCase)
                    && !(gen.Def.HealthyNode?.Equals(nodeName, StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                continue;
            }
            gen.Cycle.HostDied();
            dying++;
            Log.Info("flight", $"egen: generator '{gen.Def.Node}' host died: launches for {GeneratorCycle.HostDeathGraceSeconds:0} s more, then disabled permanently");
        }
        return dying;
    }

    /// <summary>Hands this end's generator aircraft to the host. The cycles keep their own timers
    /// and doors, but the spawner refuses every aircraft launch they come due for. The host's
    /// launches arrive through <see cref="LaunchReplicated"/>. Surface hulls are not affected.</summary>
    public void Replicate() => Replicated = true;

    /// <summary>Builds the host's launch on this end with the host's net pick, pose and launch
    /// counter. Returns the aircraft, or null when the generator, the net or the build is missing.
    /// No take-off run is started, since the host's samples fly the copy.</summary>
    public FlightController? LaunchReplicated(in GeneratorAircraftLaunch launch)
    {
        if (launch.Generator < 0 || launch.Generator >= _live.Count)
        {
            Log.Info("flight", $"egen: replicated launch names generator {launch.Generator}, this end has {_live.Count}");
            return null;
        }
        var gen = _live[launch.Generator];
        if (launch.Net < 0 || launch.Net >= gen.Nets.Count)
        {
            Log.Info("flight", $"egen: replicated launch from '{gen.Def.Node}' names net {launch.Net}, the generator has {gen.Nets.Count}");
            return null;
        }
        var net = gen.Nets[launch.Net];
        SpawnedNet[gen.Def.Node] = net.Name;
        var pos = launch.Position;
        var pilot = AiPilot.HoldingCourse(pos, pos + launch.Drop);
        pilot.Patrol = new AiNetFollower(net, Rng.NewSystemRandom(Rng.Ai),
            trailerTarget: _trailerTarget?.Invoke(net));
        // The spawner forms the name from the counter, so it reads the host's value.
        LaunchOrdinal = launch.LaunchOrdinal;
        LaunchedVehicle launched;
        _replaying = true;
        try
        {
            launched = _spawn(gen.Def, pos, pos + launch.Drop, pilot);
        }
        finally
        {
            _replaying = false;
        }
        LaunchOrdinal = launch.LaunchOrdinal + 1;
        if (launched.Aircraft is not { } controller)
        {
            Log.Info("flight", $"egen: replicated launch from '{gen.Def.Node}' built no aircraft");
            return null;
        }
        if (launch.Velocity is { } velocity)
            controller.Activate(pos, pos + launch.Drop, velocity,
                carrierDrop: launch.CarrierDrop, launchThrottle: launch.Throttle);
        gen.SpawnCount++;
        Log.Info("flight", $"egen: '{gen.Def.Node}' replicated launch #{launch.LaunchOrdinal}: '{controller.Name}' at ({pos.X:0},{pos.Y:0},{pos.Z:0}) patrolling net '{net.Name}'");
        return controller;
    }

    /// <summary>One session-simulation step of every live cycle, door transitions included.</summary>
    public void SimStep(float dt)
    {
        // Runs first, so a launch this step first moves on the next, as the original's does.
        StepRuns(dt);
        foreach (var gen in _live)
        {
            var hostPosition = gen.Host.GlobalPosition;
            gen.HostVelocity = dt > 0f ? (hostPosition - gen.LastHostPosition) / dt : Vector3.Zero;
            gen.LastHostPosition = hostPosition;
            // The altitude read is live off the host node, so the min_altitude gate holds (not
            // cancels) whenever F17's flown zeppelin sits below it.
            bool wasDisabled = gen.Cycle.Disabled;
            bool spawned = gen.Cycle.Step(dt, hostPosition.Y);
            if (gen.Cycle.Disabled && !wasDisabled)
            {
                Log.Info("flight", $"egen: generator '{gen.Def.Node}' disabled permanently: host died");
            }
            if (gen.Cycle.DoorOpen != gen.DoorOpen)
            {
                gen.DoorOpen = gen.Cycle.DoorOpen;
                int started = PlayDoor(gen, opening: gen.DoorOpen);
                // A door with nothing to run is fully open the moment it is asked, so its overdue
                // launch is not held: the barracuda's defaulted name resolves to no definition.
                if (gen.DoorOpen && started == 0)
                {
                    spawned |= gen.Cycle.ReleaseDoorHold();
                }
            }
            if (spawned)
            {
                Spawn(gen);
            }
        }
    }

    // The path nodes sit in the host's own subtree, so their live global positions already carry
    // the moving_path transform the original applies by hand against the host's live matrix.
    private static List<Node3D>? ResolveLaunchPath(string node, Node3D host,
        Func<string, Node3D?, Node3D?>? resolveNode)
    {
        if (resolveNode == null)
        {
            return null;
        }
        var points = new List<Node3D>();
        for (int i = 0; i < MaxLaunchPathPoints; i++)
        {
            if (resolveNode(EnemyGenerators.LaunchPathNode(node, i), host) is not { } point)
            {
                break;
            }
            points.Add(point);
        }
        return points.Count > 1 ? points : null;
    }

    // Answered whatever definition raised it: the original's mission-script host reads the code
    // alone. True even when no generator carries the name, as there.
    private bool Host(int code, string? animName, string? rootName)
    {
        if (code != CreditCallbackCode)
        {
            return _inner?.Invoke(code, animName, rootName) ?? false;
        }
        int fed = GrantWaveCapacity(CallbackCreditedHost, CallbackCredit);
        Log.Info("flight", $"egen: callback {code} from '{animName ?? "?"}': '{CallbackCreditedHost}' +{CallbackCredit} credit (granted {fed})");
        return true;
    }

    // The take-off run is the second movement law from its generator entry: the launched aircraft
    // is a puppet of the path follower (held, no flight integration, no collision) until the last
    // waypoint, where it drops into the flight model at the speed the run left it, lever open,
    // and its patrol net reseats where it arrived. A launch killed on the run leaves it here.
    private void StepRuns(float dt)
    {
        for (int i = _runs.Count - 1; i >= 0; i--)
        {
            var run = _runs[i];
            if (run.Ended || !GodotObject.IsInstanceValid(run.Rig) || !run.Rig.InPlay)
            {
                _runs.RemoveAt(i);
                continue;
            }
            run.Follower.Step(dt);
            var nose = run.Nose;
            if (run.Follower.Following)
            {
                run.Rig.PlaceHeld(run.Follower.Position, run.Follower.Position + nose);
                continue;
            }
            // The decoded handoff: the run's velocity plus one metre per second upward, the
            // lever left open, and no re-activation (so no spawn grace to fly blind through).
            run.Rig.ReleaseHeld(nose * run.Follower.Speed + Vector3.Up * HandoffUpwardSpeed,
                SurfaceLaunchThrottle);
            var at = run.Follower.Position;
            Log.Info("flight", $"egen: '{run.Generator}' launch '{run.Rig.Name}' completes its take-off run at ({at.X:0},{at.Y:0},{at.Z:0}), {run.Follower.Speed:0.#} m/s, released to net '{run.Net}'");
            _runs.RemoveAt(i);
        }
    }

    // How many instances the transition started, which is 0 for a door with no definition to run.
    private int PlayDoor(LiveGenerator gen, bool opening)
    {
        string? anim = opening ? gen.Def.OpenAnim : gen.Def.CloseAnim;
        string what = opening ? "open" : "close";
        if (anim == null)
        {
            Log.Info("flight", $"egen: '{gen.Def.Node}' door {what} (no authored anim)");
            return 0;
        }
        // Stop the opposite motion first: a fast cycle can otherwise leave both from-to motions
        // writing. Hooks are scoped to the host's subtree, C1 has three 'hangerdoors' namesakes.
        string? other = opening ? gen.Def.CloseAnim : gen.Def.OpenAnim;
        if (other != null && other != anim)
        {
            _stopAnim?.Invoke(other, gen.Host);
        }
        int started = _playAnim?.Invoke(anim, gen.Host) ?? 0;
        Log.Info("flight", $"egen: '{gen.Def.Node}' door {what} (anim '{anim}'{(started > 0 ? $", {started} instance(s))" : ", not in this program)")}");
        return started;
    }

    // pos/drop below are computed once and shared by the release and net-pick branches; the
    // F12 zeppelin arm must not re-derive its own drop point.
    private void Spawn(LiveGenerator gen)
    {
        var anchor = gen.Origin ?? gen.Host;
        var pos = anchor.GlobalPosition;
        Vector3? launchVelocity = gen.Def.IsZeppelin
            ? gen.HostVelocity + Vector3.Down * CarrierLaunchDownwardSpeed
            : null;
        var forward = -gen.Host.GlobalTransform.Basis.Z;
        forward.Y = 0f;
        forward = forward.LengthSquared() > 1e-6f ? forward.Normalized() : Vector3.Forward;

        // The authored drop attitude: rotation's pitch about the host's right axis, clamped shy
        // of vertical so Basis.LookingAt keeps a horizontal component.
        var drop = forward;
        if (gen.Def.RotationDeg is { } rot && Mathf.Abs(rot.X) > 0.01f)
        {
            float pitch = Mathf.DegToRad(Mathf.Clamp(rot.X, -MaxDropPitchDeg, MaxDropPitchDeg));
            // Rotating about the host's RIGHT axis (forward × up) by a negative angle pitches
            // the vector down, matching the authored sign (−90 = drop).
            drop = forward.Rotated(forward.Cross(Vector3.Up).Normalized(), pitch).Normalized();
        }

        // A surface host launches off its own take-off path rather than out of the host node: on
        // the first point, nose on the second, at rest. The path's own climb supplies the pitch,
        // so no spawn-height offset and no altitude gate belong here.
        float? launchThrottle = null;
        if (gen.LaunchPath is { Count: > 1 } path)
        {
            pos = path[0].GlobalPosition + Vector3.Up * SurfaceLaunchLift;
            var run = path[1].GlobalPosition - path[0].GlobalPosition;
            if (run.LengthSquared() > 1e-6f)
            {
                forward = drop = run.Normalized();
            }
            launchVelocity = Vector3.Zero;
            launchThrottle = SurfaceLaunchThrottle;
        }

        // Instant Action's zeppelin arm (F12): release a parked wave member instead of building a
        // new aircraft. No net pick, a released member is a wave enemy carrying its own
        // primary_target, not a generator-authored patroller.
        if (gen.Release is { } release)
        {
            var released = release(pos, pos + drop, launchVelocity ?? Vector3.Zero);
            if (released == null)
            {
                gen.Cycle.SpawnRemoved();   // the slot was counted before the release could fail
                Log.Info("flight", $"egen: '{gen.Def.Node}' launch skipped: no parked wave member left");
                return;
            }
            gen.SpawnCount++;
            LaunchOrdinal++;
            released.Downed += (_, _) => gen.Cycle.SpawnRemoved();
            Log.Info("flight", $"egen: '{gen.Def.Node}' launch #{gen.SpawnCount}: IA wave member '{released.Name}' released at ({pos.X:0},{pos.Y:0},{pos.Z:0}), active {gen.Cycle.Active}/{gen.Def.MaxActive}, {gen.Cycle.CapacityRemaining} of this wave's credit left");
            return;
        }

        // The cyclic net pick (choose_nets is cyclic on every authored file; 'random' falls back
        // to cyclic here until something authors it). The spawned pilot patrols it.
        int netIndex = gen.NetCursor % gen.Nets.Count;
        var net = gen.Nets[netIndex];
        gen.NetCursor++;
        SpawnedNet[gen.Def.Node] = net.Name;

        var pilot = AiPilot.HoldingCourse(pos, pos + forward);
        pilot.Patrol = new AiNetFollower(net, Rng.NewSystemRandom(Rng.Ai),
            trailerTarget: _trailerTarget?.Invoke(net));
        var launched = _spawn(gen.Def, pos, pos + drop, pilot);
        if (launched.Refused)
        {
            gen.Cycle.SpawnRemoved();
            RefusedLaunches++;
            Log.Info("flight", $"egen: '{gen.Def.Node}' launch refused: this end's generator aircraft come from the host");
            return;
        }
        if (launched.Vessel is { } vessel)
        {
            // A hull runs the host's take-off path and joins its net where the path ends; it is
            // never held under the aircraft run below, which ends in a climb-out.
            gen.SpawnCount++;
            LaunchOrdinal++;
            vessel.Destroyed += _ => gen.Cycle.SpawnRemoved();
            var run = new List<Vector3>();
            if (gen.LaunchPath is { } points)
            {
                for (int i = 1; i < points.Count; i++)
                {
                    run.Add(points[i].GlobalPosition);
                }
            }
            vessel.Launch(run, net);
            Log.Info("flight", $"egen: '{gen.Def.Node}' spawn #{gen.SpawnCount}: surface vehicle '{vessel.Name}' launched at ({pos.X:0},{pos.Y:0},{pos.Z:0}) down {run.Count} path point(s) onto net '{net.Name}', active {gen.Cycle.Active}/{gen.Def.MaxActive}, params '{gen.Def.VehicleParams}'");
            return;
        }
        var controller = launched.Aircraft;
        if (controller == null)
        {
            if (gen.Def.VehicleParams != null)
            {
                // The decoded empty launch (a params label naming no block): the original
                // reports it as a launch, so the ordinal advances and the max_active slot
                // stays taken by nothing until the generator blocks itself.
                LaunchOrdinal++;
                Log.Info("flight", $"egen: '{gen.Def.Node}' launch counted, nothing built, active {gen.Cycle.Active}/{gen.Def.MaxActive}");
                return;
            }
            gen.Cycle.SpawnRemoved();   // the slot was counted before the spawn could fail
            return;
        }
        if (launchVelocity is { } velocity)
            controller.Activate(pos, pos + drop, velocity,
                carrierDrop: launchThrottle == null, launchThrottle: launchThrottle);
        gen.SpawnCount++;
        int ordinal = LaunchOrdinal++;
        controller.Downed += (_, _) => gen.Cycle.SpawnRemoved();
        if (gen.LaunchPath is { } runPoints)
        {
            StartTakeOffRun(gen, controller, runPoints, pos, forward, net.Name);
        }
        AircraftLaunched?.Invoke(
            new GeneratorAircraftLaunch(_live.IndexOf(gen), ordinal, netIndex, pos, drop, launchVelocity,
                launchThrottle == null, launchThrottle),
            controller);
        Log.Info("flight", $"egen: '{gen.Def.Node}' spawn #{gen.SpawnCount}: '{controller.Name}' dropped at ({pos.X:0},{pos.Y:0},{pos.Z:0}) patrolling net '{net.Name}', active {gen.Cycle.Active}/{gen.Def.MaxActive}{(gen.Def.VehicleParams != null ? $", params '{gen.Def.VehicleParams}'" : "")}");
    }

    // The launch pose is the run's first pose: waypoint 0 lifted the decoded 0.2 m, nose down the
    // first leg. The path is read live so a run off a still-driving hull stays on its deck.
    private void StartTakeOffRun(LiveGenerator gen, FlightController rig, List<Node3D> points,
        Vector3 pos, Vector3 forward, string net)
    {
        var waypoints = new LiveWaypoints(points);
        float heading = Mathf.Atan2(-forward.X, -forward.Z);
        var follower = new PathFollower(waypoints, pos, heading) { Frozen = false };
        var run = new TakeOffRun(gen.Def.Node, rig, follower, waypoints, net);
        rig.Downed += (_, _) => run.Ended = true;
        rig.Held = true;
        rig.PlaceHeld(pos, pos + run.Nose);
        _runs.Add(run);
    }

    // The path nodes' live positions, so a follower over them rides the host's live matrix the
    // way the original re-transforms a moving_path record at every read.
    private sealed class LiveWaypoints : IReadOnlyList<Vector3>
    {
        private readonly List<Node3D> _points;

        public LiveWaypoints(List<Node3D> points)
        {
            _points = points;
        }

        public int Count => _points.Count;

        public Vector3 this[int index] => _points[index].GlobalPosition;

        public IEnumerator<Vector3> GetEnumerator()
        {
            foreach (var point in _points)
            {
                yield return point.GlobalPosition;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TakeOffRun
    {
        private readonly LiveWaypoints _waypoints;

        public TakeOffRun(string generator, FlightController rig, PathFollower follower,
            LiveWaypoints waypoints, string net)
        {
            Generator = generator;
            Rig = rig;
            Follower = follower;
            Net = net;
            _waypoints = waypoints;
        }

        public string Generator { get; }

        public FlightController Rig { get; }

        public PathFollower Follower { get; }

        public string Net { get; }

        public bool Ended { get; set; }

        /// <summary>The follower's own motion direction, yaw and pitch: a deck run that climbs
        /// authors its pitch in the points, and the final leg's climb-out raises it further.</summary>
        public Vector3 Nose
        {
            get
            {
                float pitch = Follower.Pitch;
                float heading = Follower.Heading;
                return new Vector3(-Mathf.Sin(heading) * Mathf.Cos(pitch), Mathf.Sin(pitch),
                    -Mathf.Cos(heading) * Mathf.Cos(pitch));
            }
        }
    }

    private sealed class LiveGenerator
    {
        public LiveGenerator(EnemyGeneratorDef def, GeneratorCycle cycle, Node3D host,
            Node3D? origin, List<AiNet> nets)
        {
            Def = def;
            Cycle = cycle;
            Host = host;
            Origin = origin;
            Nets = nets;
            LastHostPosition = host.GlobalPosition;
        }

        public EnemyGeneratorDef Def { get; }

        public GeneratorCycle Cycle { get; }

        public Node3D Host { get; }

        public Node3D? Origin { get; }

        public Vector3 LastHostPosition { get; set; }

        public Vector3 HostVelocity { get; set; }

        public List<AiNet> Nets { get; }

        /// <summary>A surface host's take-off path, in host subtree order; null on a zeppelin.
        /// Never shorter than two points, which is a load-drop condition.</summary>
        public List<Node3D>? LaunchPath { get; init; }

        /// <summary>F12's Instant Action launch hook: non-null once
        /// <see cref="AiGeneratorRuntime.UseInstantActionLaunches"/> has claimed this generator,
        /// and then it replaces the plane spawn entirely.</summary>
        public Func<Vector3, Vector3, Vector3, FlightController?>? Release { get; set; }

        public int NetCursor { get; set; }

        public int SpawnCount { get; set; }

        public bool DoorOpen { get; set; }
    }
}
