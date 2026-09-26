using System;
using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Mech3;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.Campaign;

/// <summary>The mid-mission cutscene trigger: every frame, with nothing already playing, each armed
/// <c>landings.zrd</c> row tests every flying human against its approach node's condition volume,
/// attitude cone and speed band, and the first row any of them passes starts its animation with
/// <see cref="CutsceneController"/> hosting it, once per entry into that volume and owned by the
/// human who flew it. An <c>auto</c> row offers the auto-land to each passing human instead of
/// starting anything. Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
public sealed partial class LandingApproachRuntime : Node
{
    private readonly List<Bound> _bound = new();
    private readonly List<LandingApproach> _approaches = new();
    private readonly HashSet<int> _boundIds = new();
    // Rows that have fired and whose condition has not stopped passing since. A cutscene ends with
    // the aircraft parked where the definition left it, which is still inside the volume that
    // started it, so without this the row re-fires on the frame after the handoff. ⚠ Per ROW, not
    // per row and human: a row fires once per entry into its volume however many humans are in it,
    // and a second human arriving is not a second entry.
    private readonly HashSet<int> _latched = new();
    // Which humans an `auto` row is offering the prompt to right now, by player index: the prompt is
    // per pane, so only the human inside the sphere sees it and only they can press it.
    private readonly HashSet<int> _offered = new();
    // A replicated trigger's own humans who pressed while offered, by player index. Held from the
    // press until the prompt goes away. The host's copy reaches the sphere a buffer delay after this
    // aeroplane did, and a one-frame press would be over by then.
    private readonly HashSet<int> _pressing = new();
    // The humans passing the row under test, in player order. A field so the per-row scan allocates
    // nothing on a tick that fires nothing, which is nearly all of them.
    private readonly List<PlayerRig> _passing = new();
    private AnimRuntime? _runtime;
    private CutsceneController? _cutscene;
    private Func<IReadOnlyList<PlayerRig>>? _humans;
    // The human the row being started belongs to, read by the trigger-owner seam during the
    // PlayMissionTrigger call and null outside one. A field rather than an argument because the
    // slot is written inside the runtime's own start, which carries a definition name alone; every
    // other path through that seam means the scripted player, which is what null says.
    private PlayerRig? _startingFor;

    /// <summary>Constructs the trigger. SessionSimulation ticks it before mission objectives;
    /// CutsceneController then mirrors a newly started row later in the same frame.</summary>
    public LandingApproachRuntime()
    {
        Name = "LandingApproaches";
    }

    /// <summary>Raised with the row's index in the bound table and the human whose flying started
    /// it, after the row's definition has started. The host sends it on from here.</summary>
    public event Action<int, PlayerRig>? Started;

    /// <summary>How many rows bound to a node this world built. Zero when the mission carries none
    /// of the chapter's approach animations, which is the original's own load-time rejection.
    /// </summary>
    public int Armed => _bound.Count;

    /// <summary>Whether an <c>auto</c> row passes for any human right now: the original lights its
    /// auto-land prompt off exactly this, and never starts the animation on that row by itself.
    /// <see cref="OffersAutoLandTo"/> is the per-pane answer the HUD is actually drawn from.</summary>
    public bool AutoLandOffered => _offered.Count > 0;

    /// <summary>The animation the last row to pass started, for a suite and the log.</summary>
    public string? LastStarted { get; private set; }

    /// <summary>The player index of the human whose flying started <see cref="LastStarted"/>, for a
    /// suite and the log; null before anything has started.</summary>
    public int? LastStartedBy { get; private set; }

    /// <summary>A guest's trigger: it offers the prompt to its own humans and records their
    /// button, and starts a row only when <see cref="StartRow"/> says the host did.</summary>
    public bool Replicated { get; private set; }

    /// <summary>Hands the decision to the host. ⚠ One way: a trigger that both decided and
    /// replayed would start a row twice, once from each machine's own copy of the flyer.</summary>
    public void Replicate() => Replicated = true;

    /// <summary>Starts row <paramref name="id"/> for <paramref name="by"/> because the host did.
    /// False when this world never bound that row.</summary>
    public bool StartRow(int id, PlayerRig by)
    {
        ArgumentNullException.ThrowIfNull(by);
        if (_runtime == null)
        {
            return false;
        }

        RefreshBindings();
        foreach (var bound in _bound)
        {
            if (bound.Id == id)
            {
                _latched.Add(id);
                Start(bound, by);
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether this replicated trigger's human <paramref name="playerIndex"/> has pressed
    /// the auto-land while offered it, and is still offered it.</summary>
    public bool Pressing(int playerIndex) => _pressing.Contains(playerIndex);

    /// <summary>Binds the resolved rows to the built world. Rows whose approach node the build
    /// never created are dropped, so <see cref="Armed"/> counts what can actually fire.
    /// <paramref name="humans"/> is the human field, read fresh each tick because an airframe swap
    /// rebuilds a rig's controller.</summary>
    public void Bind(
        AnimRuntime runtime,
        IReadOnlyList<LandingApproach> approaches,
        CutsceneController cutscene,
        Func<IReadOnlyList<PlayerRig>> humans)
    {
        _runtime = runtime;
        _cutscene = cutscene;
        // The trigger slot, on the runtime rather than on this class: every path that starts a
        // definition writes it, and a suite binding a trigger onto a bare world runtime gets the
        // wiring a session's world build gives it. See `_startingFor` for the human half.
        runtime.MissionTriggerOwner = anim => cutscene.Own(anim, _startingFor);
        _humans = humans;
        _bound.Clear();
        _approaches.Clear();
        _approaches.AddRange(approaches);
        _boundIds.Clear();
        _latched.Clear();
        _offered.Clear();
        _pressing.Clear();
        RefreshBindings();

        if (_bound.Count > 0)
        {
            Log.Info("world", $"landings: {_bound.Count} approach trigger(s) armed");
        }
    }

    /// <summary>Is this human being offered the auto-land right now? The prompt is per pane, so a
    /// human outside the sphere neither sees it nor can press it.</summary>
    public bool OffersAutoLandTo(int playerIndex) => _offered.Contains(playerIndex);

    /// <summary>One session-simulation step. Cheap and inert when the world armed no rows.</summary>
    public void Tick()
    {
        _offered.Clear();
        if (_runtime == null || _cutscene is not { Playing: false })
        {
            _pressing.Clear();
            return;
        }
        if (RefreshBindings() > 0)
        {
            Log.Info("world", $"landings: {_bound.Count} approach trigger(s) armed after world staging");
        }
        if (_bound.Count == 0 || _humans?.Invoke() is not { Count: > 0 } humans)
        {
            _pressing.Clear();
            return;
        }

        if (Replicated)
        {
            OfferOnly(humans);
            return;
        }

        for (int i = 0; i < _bound.Count; i++)
        {
            var bound = _bound[i];
            CollectPassing(bound, humans);
            if (_passing.Count == 0)
            {
                _latched.Remove(bound.Id);
                continue;
            }

            if (bound.Approach.Auto)
            {
                if (OfferAutoLand(bound) is { } presser)
                {
                    Start(bound, presser);
                    return;
                }
                continue;
            }

            if (!_latched.Add(bound.Id))
            {
                continue;
            }

            // First one wins, in player order: two humans entering the volume on the same tick
            // start the row once, and it belongs to the lower-numbered of them.
            Start(bound, _passing[0]);
            return;
        }
    }

    // A replicated trigger's tick: the prompt for this machine's own humans and the button they
    // pressed under it, and no start. A seat flown elsewhere is skipped, since its own machine
    // offers it the prompt.
    private void OfferOnly(IReadOnlyList<PlayerRig> humans)
    {
        foreach (var bound in _bound)
        {
            if (!bound.Approach.Auto)
            {
                continue;
            }

            CollectPassing(bound, humans);
            foreach (var rig in _passing)
            {
                if (rig.Controller is { RemoteOwned: false } plane && _offered.Add(rig.Index)
                    && plane.AutoLandPressed())
                {
                    _pressing.Add(rig.Index);
                }
            }
        }

        _pressing.IntersectWith(_offered);
    }

    // The humans this row passes for, in player order. The original's own two guards are here, in
    // its order: no cutscene running (the caller's), and a pilot still flying rather than pinned
    // inside one.
    private void CollectPassing(Bound bound, IReadOnlyList<PlayerRig> humans)
    {
        _passing.Clear();
        foreach (var rig in humans)
        {
            if (rig.Controller is { Held: false, Inert: false } plane
                && Passes(bound, plane, plane.WorldVelocity.Length()))
            {
                _passing.Add(rig);
            }
        }
    }

    // Lights the prompt in every passing human's pane and answers the first of them holding the
    // button down, or null. The latch is the manual row's: a press starts the row once per entry
    // into the sphere, however many humans are standing in it.
    private PlayerRig? OfferAutoLand(Bound bound)
    {
        PlayerRig? presser = null;
        foreach (var rig in _passing)
        {
            _offered.Add(rig.Index);
            if (presser == null && rig.Controller is { } plane && plane.AutoLandPressed())
            {
                presser = rig;
            }
        }

        return presser != null && _latched.Add(bound.Id) ? presser : null;
    }

    private int RefreshBindings()
    {
        int added = 0;
        for (int i = 0; i < _approaches.Count; i++)
        {
            if (_boundIds.Contains(i))
            {
                continue;
            }

            var approach = _approaches[i];
            var found = _runtime!.FindNodes(approach.Node);
            if (found.Count == 0)
            {
                continue;
            }

            var arm = _runtime.FindNodes(LandingApproaches.ArmNode, found[0]);
            _bound.Add(new Bound(i, approach, found[0], arm.Count > 0 ? arm[0] : null));
            _boundIds.Add(i);
            added++;
        }
        return added;
    }

    private bool Passes(Bound bound, FlightController plane, float speed)
    {
        var approach = bound.Approach;
        if (bound.Arm is { Visible: false } || !approach.SpeedInBand(speed))
        {
            return false;
        }

        var frame = bound.Node.GlobalTransform;
        return LandingApproaches.AngleBetween(frame.Basis, plane.Attitude) <= approach.AngleRad
            && approach.Contains(frame.AffineInverse() * plane.WorldPosition);
    }

    // Starts the row's definition for the human who flew it. The cutscene host is already registered
    // for every definition this chapter's table can reach, which is where the original's
    // per-instance registration lands (CutsceneController.HostDefinitions), and the trigger call
    // writes the slot itself, reading `_startingFor` for the human as it goes.
    private void Start(Bound bound, PlayerRig by)
    {
        var approach = bound.Approach;
        _startingFor = by;
        int started;
        try
        {
            started = _runtime!.PlayMissionTrigger(approach.Anim).Count;
        }
        finally
        {
            _startingFor = null;
        }

        LastStarted = approach.Anim;
        LastStartedBy = by.Index;
        Log.Info("world", $"landings: '{approach.Node}' flown by P{by.Index + 1}, started '{approach.Anim}' ({started} definition(s))");
        Started?.Invoke(bound.Id, by);
    }

    private readonly record struct Bound(int Id, LandingApproach Approach, Node3D Node, Node3D? Arm);
}
