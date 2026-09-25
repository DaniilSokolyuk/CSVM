using System;
using CSVM.Mech3;
using CSVM.Net;

namespace CSVM.Session;

/// <summary>
/// A guest's answer to a director event that arrived late. How late is the guest's shared clock
/// minus the event's host stamp. It applies the event and advances what the event started by
/// that much: cutscenes, their motions, one-shots, radio calls and the graph's timers.
/// Presentation with no position of its own is not advanced. The decode is
/// <c>docs/org/multiplayer-messages.md</c>'s.
/// </summary>
internal sealed class NetDirectorCatchUp
{
    private readonly Func<double> _sharedNow;
    private readonly AnimRuntime? _runtime;
    private readonly WorldSounds? _sounds;

    /// <summary>A catch-up reading <paramref name="sharedNow"/>, the guest's estimate of the
    /// host's clock, that advances what starts on <paramref name="runtime"/> and
    /// <paramref name="sounds"/>.</summary>
    public NetDirectorCatchUp(Func<double> sharedNow, AnimRuntime? runtime, WorldSounds? sounds)
    {
        ArgumentNullException.ThrowIfNull(sharedNow);
        _sharedNow = sharedNow;
        _runtime = runtime;
        _sounds = sounds;
    }

    /// <summary>False applies every event on arrival with no catch-up, the control a suite runs
    /// beside the real guest.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many events this guest has applied.</summary>
    public int Arrivals { get; private set; }

    /// <summary>How late the most recent event arrived, in seconds.</summary>
    public float LastLateness { get; private set; }

    /// <summary>How long before <paramref name="sharedNow"/> the host raised an event stamped
    /// <paramref name="stamp"/>. Never negative: an event read as early is applied now. A
    /// non-finite reading is taken as on time.</summary>
    public static float Lateness(double sharedNow, float stamp)
    {
        double late = sharedNow - stamp;
        return double.IsFinite(late) && late > 0.0 ? (float)late : 0f;
    }

    /// <summary>Applies one arrived event to <paramref name="graph"/> and catches up on it.</summary>
    public void Apply(ObjectiveGraph graph, DirectorTransitionMessage message)
    {
        float measured = Lateness(_sharedNow(), message.HostClock);
        float late = Enabled ? measured : 0f;
        Arrivals++;
        LastLateness = measured;
        if (late <= 0f)
        {
            NetDirectorLink.Apply(graph, message);
            return;
        }

        var radio = _sounds?.Radio;
        float wasSounds = _sounds?.LateBy ?? 0f;
        float wasRadio = radio?.LateBy ?? 0f;
        try
        {
            if (_sounds != null)
                _sounds.LateBy = late;
            if (radio != null)
                radio.LateBy = late;
            _runtime?.CollectLateStarts();
            NetDirectorLink.Apply(graph, message, late);
            _runtime?.CatchUp(late);
        }
        finally
        {
            if (_sounds != null)
                _sounds.LateBy = wasSounds;
            if (radio != null)
                radio.LateBy = wasRadio;
        }
    }
}
