using System;
using System.Collections.Generic;
using CSVM.Flight.Camera;
using CSVM.Mech3;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.Campaign;

/// <summary>The rope-ladder switch flown against the built world: every frame outside a
/// cutscene it reads each flying human's attitude and position, tests them against the mission's
/// <c>pickups.zrd</c> sensors, and lets <see cref="LadderSwitch"/> start the ladder definitions
/// as mission triggers. One human holds the switch at a time (<see cref="Holder"/>). It also
/// answers the definitions' settle <c>CALLBACK</c> ahead of the cutscene host, which is where the
/// original registers the switch on each definition. Decode: docs/org/ladderSwitch.md.</summary>
public sealed partial class LadderSwitchRuntime : Node
{
    private readonly LadderSwitch _switch = new();
    private readonly List<PickupSpec> _pickups = new();
    private readonly Func<int, string?, string?, bool> _host;
    private Func<int, string?, string?, bool>? _inner;
    private AnimRuntime? _runtime;
    private CutsceneController? _cutscene;
    private Func<IReadOnlyList<PlayerRig>>? _humans;

    /// <summary>Constructs the switch. It ticks alongside the landings trigger, before the
    /// cutscene host, so a drop started this frame is hosted in the same frame.</summary>
    public LadderSwitchRuntime()
    {
        Name = "LadderSwitch";
        ProcessPriority = 999;
        _host = Host;
    }

    /// <summary>Raised with the new holder, or null, whenever the holder changes on a switch that
    /// decides its own. The host sends it on from here.</summary>
    public event Action<PlayerRig?>? HolderChanged;

    /// <summary>Where the ladder is, for a suite and the log.</summary>
    public LadderState State => _switch.State;

    /// <summary>The definition the switch last started, for a suite and the log.</summary>
    public string? LastStarted { get; private set; }

    /// <summary>The human holding the switch: the first to qualify, held until they stop
    /// qualifying. ⚠ A single owner rather than "any human qualifies" because the two ladder
    /// definitions each hold a transient state until their own <c>CALLBACK</c> settles it, and two
    /// humans drifting in and out of the same sensor would otherwise thrash
    /// <c>drop_ladder</c> against <c>retract_ladder</c>. Null while nobody qualifies.</summary>
    public PlayerRig? Holder { get; private set; }

    /// <summary>A guest's switch. It steps on the host's holder (<see cref="TakeHolder"/>), never
    /// on this machine's own read of the humans.</summary>
    public bool Replicated { get; private set; }

    /// <summary>Hands the holder to the host, for the reason
    /// <see cref="LandingApproachRuntime.Replicate"/> is one way.</summary>
    public void Replicate() => Replicated = true;

    /// <summary>The holder the host decided, which the next <see cref="Tick"/> steps on.</summary>
    public void TakeHolder(PlayerRig? holder) => Holder = holder;

    /// <summary>Binds the switch to the built world and takes the runtime's <c>CALLBACK</c> host
    /// slot, chaining to whatever held it. A re-bind after the roster graft keeps the chain intact
    /// and resets the ladder to retracted, the original's load-time state.</summary>
    public void Bind(
        AnimRuntime runtime,
        CutsceneController cutscene,
        Func<IReadOnlyList<PlayerRig>> humans,
        IReadOnlyList<PickupSpec>? pickups)
    {
        _runtime = runtime;
        _cutscene = cutscene;
        _humans = humans;
        Holder = null;
        _pickups.Clear();
        if (pickups != null)
        {
            _pickups.AddRange(pickups);
        }
        _switch.Reset();
        LastStarted = null;
        if (runtime.CallbackHost != _host)
        {
            _inner = runtime.CallbackHost;
            runtime.CallbackHost = _host;
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta) => Tick();

    /// <summary>One frame of the switch. Inert without a bound world or sensors to test.</summary>
    public void Tick()
    {
        if (_runtime == null || _pickups.Count == 0 || _cutscene is not { Playing: false })
        {
            return;
        }

        // ⚠ Stepped whether or not a holder was found: a holder that has stopped qualifying with
        // nobody to take over is what retracts the ladder, and the same pass finding a replacement
        // is what stops that retraction ever starting.
        if (!Replicated)
        {
            var was = Holder;
            Holder = LadderSwitch.Holder(Holder,
                _humans?.Invoke() ?? Array.Empty<PlayerRig>(), Qualifies);
            if (!ReferenceEquals(was, Holder))
            {
                HolderChanged?.Invoke(Holder);
            }
        }

        string? started = _switch.Step(Holder != null, Start);
        if (started != null)
        {
            LastStarted = started;
            Log.Info("world", $"ladder: started '{started}' ({_switch.State}){(Holder != null ? Log.Format($" for P{Holder.Index + 1}") : "")}");
        }
    }

    // Whether this human is asking for the ladder: flying, level within the decoded 45 degrees, and
    // inside an active sensor.
    private bool Qualifies(PlayerRig rig) =>
        rig.Controller is { Held: false, InPlay: true } plane
        && LadderSwitch.IsLevel(plane.Attitude)
        && InsideSensor(plane.WorldPosition);

    private bool InsideSensor(Vector3 playerPosition)
    {
        foreach (var pickup in _pickups)
        {
            var sensors = _runtime!.FindNodes(pickup.Node);
            if (sensors.Count > 0 && sensors[0].Visible
                && LadderSwitch.WithinSensor(playerPosition, sensors[0].GlobalPosition, pickup.Radius))
            {
                return true;
            }
        }
        return false;
    }

    // A mission trigger, so the drop's OBJECT_ADD_CHILD can materialize the library rope ladder.
    private bool Start(string animName) => _runtime!.PlayMissionTrigger(animName).Count > 0;

    private bool Host(int code, string? animName, string? rootName) =>
        _switch.Settle(code, animName) || (_inner?.Invoke(code, animName, rootName) ?? false);
}
