using System;
using CSVM.Net;

namespace CSVM.Session;

/// <summary>
/// The objectives graph over the wire. The host's graph runs its own rules, and every event it
/// raises goes out as a reliable <see cref="DirectorTransitionMessage"/>. A guest's graph is
/// replicated and changes only by replaying those events in the order they were raised. A guest's
/// cutscenes are not sent. They play from the definitions its own replayed <c>WAKE_ANIM</c> and
/// the shared start list start, so their codes are raised by its own animation runtime. The
/// mapping of what a guest replays and what it derives is
/// <c>docs/org/multiplayer-messages.md</c>'s.
/// </summary>
internal static class NetDirectorLink
{
    private const int NumberMask = 0xFFFF;
    private const int SourceShift = 16;
    private const int ObjectivesSoundBit = 0x100;

    /// <summary>Sends every event <paramref name="graph"/> raises to every peer, in the order it
    /// raises them. ⚠ Subscribed, never polled: a completion and its chain are raised inside one
    /// step. A guest replays them in that order or not at all.</summary>
    internal static void Publish(NetSession net, ObjectiveGraph graph)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(graph);
        graph.Transitioned += t => Send(net, Encode(t));
        graph.Completed += c => Send(net, new DirectorTransitionMessage((ushort)NetDirectorEvent.Settled, c.Number));
        graph.TimerExpired += () => Send(net, new DirectorTransitionMessage((ushort)NetDirectorEvent.TimerExpired, 0));
        graph.EndingDecided += e => Send(net, Encode(e));
        graph.MissionEnded += o => Send(net, new DirectorTransitionMessage((ushort)NetDirectorEvent.Ended, (int)o));
    }

    /// <summary>Hands <paramref name="graph"/> over to the host's and applies each event as it
    /// arrives, from inside the session's own net step.</summary>
    internal static void Follow(NetSession net, ObjectiveGraph graph)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(graph);
        graph.Replicate();
        net.On<DirectorTransitionMessage>((_, message) => Apply(graph, message));
    }

    /// <summary>One transition as the wire carries it.</summary>
    internal static DirectorTransitionMessage Encode(ObjectiveTransition t) =>
        new((ushort)EventOf(t.Kind), (t.Number & NumberMask) | ((t.Source & NumberMask) << SourceShift));

    /// <summary>One decided ending as the wire carries it.</summary>
    internal static DirectorTransitionMessage Encode(MissionEnding e) =>
        new((ushort)NetDirectorEvent.Ending, (int)e.Outcome | (e.ObjectivesSound ? ObjectivesSoundBit : 0));

    /// <summary>Replays one arrived event on a replicated graph. An unknown code is dropped, since
    /// a guest that guessed at one would be running a rule of its own.</summary>
    internal static void Apply(ObjectiveGraph graph, DirectorTransitionMessage message)
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
                    graph.ApplyTransition(kind, id & NumberMask, (id >> SourceShift) & NumberMask);
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
