using System;
using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Session.World;

namespace CSVM.Session.Roster;

/// <summary>The session's queue of crash rigs whose aeroplane is already flying. A mid-flight AI
/// introduction is the one build that happens on a frame the player is watching, and the crash rig
/// is the only part of it the aeroplane does not need in order to be in the world: everything the
/// rig owns is spent on damage and death, and both are forced through
/// <see cref="FlightController.EnsureCrashRig"/> before they can read it.
/// <see cref="Pump"/> advances the head build by one step a frame, so the rig's cost lands on the
/// frames after the launch instead of on the launch itself.
/// ⚠ Strictly one build at a time, head first. Rigs draw from the crash RNG stream at the request
/// rather than at the build (<c>WorldEffectsFactory.BeginFlightCrashRuntime</c>), so the order the
/// queue drains in cannot move a seed, but the emitter and node counts a run reports still depend
/// on it; interleaving two builds would make those counts depend on frame timing.</summary>
internal sealed class CrashRigQueue
{
    private readonly List<Entry> _pending = new();
    private bool _live;

    /// <summary>How many armed rigs are still unbuilt. Observation only, for the suites that pin
    /// the deferral and for a caller reporting its own backlog.</summary>
    public int PendingRigs => _pending.Count;

    /// <summary>Whether this queue is deferring at all. False until <see cref="GoLive"/>, so the
    /// aeroplanes a session builds BEFORE its first frame get their rigs in place.</summary>
    public bool Live => _live;

    /// <summary>Starts deferring. Called by the owner that pumps, once its build is behind it.
    /// ⚠ There is no frame to spare during a session build, and the aeroplanes placed there are on
    /// screen from the first drawn frame: deferring them would only move what that frame shows.</summary>
    public void GoLive() => _live = true;

    /// <summary>Arms one opened rig on its aeroplane and queues it behind whatever is already
    /// waiting, or builds it in place while this queue is not yet <see cref="Live"/>.
    /// <paramref name="onComplete"/> runs once the rig is bound and pre-warmed, whether it got there
    /// through <see cref="Pump"/>, through a forcing read, or right here.</summary>
    public void Defer(FlightController owner, WorldEffectsFactory.CrashRigBuild build,
        Action? onComplete = null)
    {
        if (!_live)
        {
            build.Finish();
            onComplete?.Invoke();
            return;
        }

        var entry = new Entry(owner, build, onComplete);
        _pending.Add(entry);
        owner.ArmPendingCrashRig(() => Complete(entry));
    }

    /// <summary>Advances the head rig by one step, completing it when that was its last. One step a
    /// call: the pump's own budget is a frame, and the steps are sized to fit one.</summary>
    public void Pump()
    {
        if (_pending.Count == 0)
        {
            return;
        }
        var head = _pending[0];
        if (head.Build.Step())
        {
            Complete(head);
        }
    }

    /// <summary>Builds every queued rig in place. The teardown-safe form for a caller that is about
    /// to stop pumping while its aeroplanes are still in play.</summary>
    public void FinishAll()
    {
        while (_pending.Count > 0)
        {
            Complete(_pending[0]);
        }
    }

    /// <summary>Drops one aeroplane's queued rig without building it. For a rollback: the
    /// controller is being removed from the world, so its rig has nothing left to build onto.</summary>
    public void Drop(FlightController owner)
    {
        owner.ArmPendingCrashRig(null);
        _pending.RemoveAll(e => ReferenceEquals(e.Owner, owner));
    }

    /// <summary>Drops every queued rig without building it, and disarms its aeroplane. For a
    /// membership clear, where the aircraft nodes are being freed and a forced build would stage a
    /// subtree under a controller on its way out.</summary>
    public void Discard()
    {
        foreach (var entry in _pending)
        {
            entry.Owner.ArmPendingCrashRig(null);
        }
        _pending.Clear();
    }

    // Disarmed before the build runs, so the completion hook inside it (the damage-stage wiring
    // reads the rig it is wiring) cannot re-enter this queue.
    private void Complete(Entry entry)
    {
        _pending.Remove(entry);
        entry.Owner.ArmPendingCrashRig(null);
        entry.Build.Finish();
        entry.OnComplete?.Invoke();
    }

    private sealed record Entry(FlightController Owner, WorldEffectsFactory.CrashRigBuild Build,
        Action? OnComplete);
}
