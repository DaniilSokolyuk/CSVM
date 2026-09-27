using System;
using CSVM.Net;

namespace CSVM.Session.Objectives;

/// <summary>
/// The objectives graph over the wire. The host's graph runs its own rules, and every event it
/// raises goes out as a reliable <see cref="DirectorTransitionMessage"/> stamped with the host's
/// clock. A guest's graph is replicated and changes only by replaying those events in the order
/// they were raised. A guest's cutscenes are not sent. They play from the definitions its own
/// replayed <c>WAKE_ANIM</c> and the shared start list start. The mapping of what a guest replays
/// and what it derives is <c>docs/org/multiplayer-messages.md</c>'s.
/// </summary>
internal static class NetDirectorLink
{
    private const int NumberMask = 0xFFFF;
    private const int SourceShift = 16;
    private const int ObjectivesSoundBit = 0x100;

    /// <summary>Sends every event <paramref name="graph"/> raises to every peer, in the order it
    /// raises them, stamped with <paramref name="hostClock"/>. ⚠ Subscribed, never polled: a
    /// completion and its chain are raised inside one step. A guest replays them in that order or
    /// not at all.</summary>
    internal static void Publish(NetSession net, ObjectiveGraph graph, Func<double>? hostClock = null)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(graph);
        void Stamped(DirectorTransitionMessage message) =>
            Send(net, message with { HostClock = (float)(hostClock?.Invoke() ?? 0.0) });

        graph.Transitioned += t => Stamped(Encode(t));
        graph.Completed += c => Stamped(new DirectorTransitionMessage((ushort)NetDirectorEvent.Settled, c.Number));
        graph.TimerExpired += () => Stamped(new DirectorTransitionMessage((ushort)NetDirectorEvent.TimerExpired, 0));
        graph.EndingDecided += e => Stamped(Encode(e));
        graph.MissionEnded += o => Stamped(new DirectorTransitionMessage((ushort)NetDirectorEvent.Ended, (int)o));
    }

    /// <summary>Hands <paramref name="graph"/> over to the host's and applies each event as it
    /// arrives, from inside the session's own net step. With <paramref name="catchUp"/>, what an
    /// event starts is advanced by how late it arrived.</summary>
    internal static void Follow(NetSession net, ObjectiveGraph graph, NetDirectorCatchUp? catchUp = null)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(graph);
        graph.Replicate();
        if (catchUp is null)
            net.On<DirectorTransitionMessage>((_, message) => Apply(graph, message));
        else
            net.On<DirectorTransitionMessage>((_, message) => catchUp.Apply(graph, message));
    }

    /// <summary>One transition as the wire carries it.</summary>
    internal static DirectorTransitionMessage Encode(ObjectiveTransition t) =>
        new((ushort)EventOf(t.Kind), (t.Number & NumberMask) | ((t.Source & NumberMask) << SourceShift));

    /// <summary>One decided ending as the wire carries it.</summary>
    internal static DirectorTransitionMessage Encode(MissionEnding e) =>
        new((ushort)NetDirectorEvent.Ending, (int)e.Outcome | (e.ObjectivesSound ? ObjectivesSoundBit : 0));

    /// <summary>Replays one arrived event on a replicated graph, <paramref name="late"/> seconds
    /// after the host raised it. An unknown code is dropped, since a guest that guessed at one
    /// would be running a rule of its own.</summary>
    internal static void Apply(ObjectiveGraph graph, DirectorTransitionMessage message, float late = 0f)
    {
        int id = message.Id;
        switch ((NetDirectorEvent)message.Code)
        {
            case NetDirectorEvent.Settled:
                graph.ApplySettled(id);
                break;
            case NetDirectorEvent.TimerExpired:
                graph.ApplyTimerExpired();
                break;
            case NetDirectorEvent.Ending:
                graph.ApplyEnding(new MissionEnding((MissionOutcome)(id & 0xFF), (id & ObjectivesSoundBit) != 0));
                break;
            case NetDirectorEvent.Ended:
                graph.ApplyEnded((MissionOutcome)id);
                break;
            default:
                if (KindOf((NetDirectorEvent)message.Code) is { } kind)
                {
                    graph.ApplyTransition(kind, id & NumberMask, (id >> SourceShift) & NumberMask, late);
                }

                break;
        }
    }

    private static void Send(NetSession net, in DirectorTransitionMessage message) =>
        net.Broadcast(message, NetChannels.Events);

    private static NetDirectorEvent EventOf(ObjectiveTransitionKind kind) => kind switch
    {
        ObjectiveTransitionKind.Woke => NetDirectorEvent.Woke,
        ObjectiveTransitionKind.Napped => NetDirectorEvent.Napped,
        ObjectiveTransitionKind.Completed => NetDirectorEvent.Completed,
        ObjectiveTransitionKind.Killed => NetDirectorEvent.Killed,
        ObjectiveTransitionKind.Slept => NetDirectorEvent.Slept,
        ObjectiveTransitionKind.Expired => NetDirectorEvent.Expired,
        ObjectiveTransitionKind.Hidden => NetDirectorEvent.Hidden,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "a transition kind with no wire code"),
    };

    private static ObjectiveTransitionKind? KindOf(NetDirectorEvent code) => code switch
    {
        NetDirectorEvent.Woke => ObjectiveTransitionKind.Woke,
        NetDirectorEvent.Napped => ObjectiveTransitionKind.Napped,
        NetDirectorEvent.Completed => ObjectiveTransitionKind.Completed,
        NetDirectorEvent.Killed => ObjectiveTransitionKind.Killed,
        NetDirectorEvent.Slept => ObjectiveTransitionKind.Slept,
        NetDirectorEvent.Expired => ObjectiveTransitionKind.Expired,
        NetDirectorEvent.Hidden => ObjectiveTransitionKind.Hidden,
        _ => null,
    };
}
