using System;
using System.Collections.Generic;
using System.Linq;

namespace CSVM.Net;

/// <summary>
/// Two or more transports wired to each other in one process through delivery queues, under an
/// injected <see cref="LoopbackConditions"/> model per direction. Nothing arrives until
/// <see cref="Step"/> runs, so a suite owns delivery time. It can hold a payload back, overtake
/// it with a newer one, or lose it on demand. The reliability contract is enforced rather than
/// imitated. Loss touches only the unreliable classes, a reliable stream keeps its send order
/// whatever the jitter, and a stale sequenced payload is discarded.
/// </summary>
public sealed class LoopbackTransport : INetTransport
{
    private readonly int _local;
    private readonly Random _rng;
    private readonly List<int> _peers = new();
    private readonly Dictionary<int, LoopbackTransport> _links = new();
    private readonly Dictionary<int, LoopbackConditions> _outbound = new();
    private readonly Dictionary<long, int> _sent = new();
    private readonly Dictionary<long, int> _newest = new();
    private readonly Dictionary<int, Queue<Pending>> _reliable = new();
    private readonly Dictionary<int, double> _reliableTail = new();
    private readonly List<Pending> _unreliable = new();
    private INetTransportListener? _listener;
    private double _now;

    private LoopbackTransport(int local, Random rng)
    {
        _local = local;
        _rng = rng;
    }

    /// <inheritdoc/>
    public int LocalPeer => _local;

    /// <inheritdoc/>
    public IReadOnlyList<int> Peers => _peers;

    /// <summary>Seconds this end has been stepped, the clock every deadline is measured on.</summary>
    public double Now => _now;

    /// <summary>Builds <paramref name="peerCount"/> transports with ids 0 upwards, every one linked
    /// to every other under <paramref name="conditions"/>. All of them draw from the one
    /// <paramref name="rng"/>, so a seeded generator replays the whole mesh.</summary>
    public static IReadOnlyList<LoopbackTransport> Mesh(int peerCount, LoopbackConditions conditions, Random rng)
    {
        if (peerCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(peerCount), peerCount, "a mesh holds at least one transport");
        }

        var mesh = new LoopbackTransport[peerCount];
        for (int i = 0; i < peerCount; i++)
        {
            mesh[i] = new LoopbackTransport(i, rng);
        }

        for (int i = 0; i < peerCount; i++)
        {
            for (int j = 0; j < peerCount; j++)
            {
                if (i != j)
                {
                    mesh[i].Attach(mesh[j], conditions);
                }
            }
        }

        return mesh;
    }

    /// <inheritdoc/>
    public void Bind(INetTransportListener listener)
    {
        if (_listener != null)
        {
            throw new InvalidOperationException($"peer {_local} already has a listener bound");
        }

        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
        foreach (int peer in _peers.ToArray())
        {
            listener.OnPeerConnected(peer);
        }
    }

    /// <summary>Replaces the conditions on the direction from here to <paramref name="peer"/>. A
    /// suite makes a reorder with this rather than waiting for the jitter to draw one.</summary>
    public void SetConditions(int peer, LoopbackConditions conditions)
    {
        if (!_links.ContainsKey(peer))
        {
            throw new ArgumentOutOfRangeException(nameof(peer), peer, "not a connected peer");
        }

        _outbound[peer] = conditions;
    }

    /// <inheritdoc/>
    public void Send(int peer, ReadOnlySpan<byte> payload, NetReliability reliability, int channel = 0)
    {
        if (!_links.TryGetValue(peer, out var link))
        {
            return;
        }

        var conditions = _outbound[peer];
        int sequence = 0;
        if (reliability == NetReliability.UnreliableSequenced)
        {
            long key = Key(peer, channel);
            _sent.TryGetValue(key, out sequence);
            sequence++;
            _sent[key] = sequence;
        }

        // The sequence number is spent even when the payload is lost. A loss then leaves a gap
        // rather than a run the receiver would accept out of order.
        if (reliability != NetReliability.Reliable && conditions.Drops(_rng))
        {
            return;
        }

        link.Accept(_local, channel, reliability, sequence, payload.ToArray(), conditions.Delay(_rng));
    }

    /// <inheritdoc/>
    public void Disconnect(int peer)
    {
        if (!_links.TryGetValue(peer, out var link))
        {
            return;
        }

        Detach(peer);
        link.Detach(_local);
    }

    /// <inheritdoc/>
    public void Step(double dt)
    {
        if (dt < 0.0 || double.IsNaN(dt))
        {
            throw new ArgumentOutOfRangeException(nameof(dt), dt, "a transport does not step backwards");
        }

        _now += dt;
        Deliver();
    }

    private static long Key(int peer, int channel) => ((long)peer << 32) | (uint)channel;

    private static void Forget(Dictionary<long, int> counters, int peer)
    {
        foreach (long key in counters.Keys.Where(k => (int)(k >> 32) == peer).ToList())
        {
            counters.Remove(key);
        }
    }

    private void Attach(LoopbackTransport peer, LoopbackConditions conditions)
    {
        _links[peer._local] = peer;
        _outbound[peer._local] = conditions;
        _peers.Add(peer._local);
        _listener?.OnPeerConnected(peer._local);
    }

    // The receiving end of a send. The deadline is on this end's own clock. A delay is however
    // long this transport is stepped after the send, whatever the sender's clock reads.
    private void Accept(int from, int channel, NetReliability reliability, int sequence, byte[] payload, double delay)
    {
        double deadline = _now + delay;
        if (reliability != NetReliability.Reliable)
        {
            _unreliable.Add(new Pending(from, channel, reliability, sequence, payload, deadline));
            return;
        }

        // Jitter must not reorder a reliable stream, so a payload is never due before the one in
        // front of it. The queue is then drained from the head.
        if (_reliableTail.TryGetValue(from, out double tail))
        {
            deadline = Math.Max(deadline, tail);
        }

        _reliableTail[from] = deadline;
        if (!_reliable.TryGetValue(from, out var queue))
        {
            queue = new Queue<Pending>();
            _reliable[from] = queue;
        }

        queue.Enqueue(new Pending(from, channel, reliability, sequence, payload, deadline));
    }

    private void Detach(int peer)
    {
        _links.Remove(peer);
        _outbound.Remove(peer);
        _peers.Remove(peer);
        _reliable.Remove(peer);
        _reliableTail.Remove(peer);
        _unreliable.RemoveAll(pending => pending.From == peer);
        Forget(_sent, peer);
        Forget(_newest, peer);
        _listener?.OnPeerDisconnected(peer);
    }

    // Everything due, in deadline order, with the sequencing rule applied as each one is handed
    // over. The whole set leaves the queues before any of it is reported. A listener that sends
    // or hangs up inside a callback cannot disturb the walk.
    private void Deliver()
    {
        var due = new List<Pending>();
        foreach (var queue in _reliable.Values)
        {
            while (queue.Count > 0 && queue.Peek().Deadline <= _now)
            {
                due.Add(queue.Dequeue());
            }
        }

        var held = new List<Pending>(_unreliable.Count);
        foreach (var pending in _unreliable)
        {
            if (pending.Deadline <= _now)
            {
                due.Add(pending);
            }
            else
            {
                held.Add(pending);
            }
        }

        _unreliable.Clear();
        _unreliable.AddRange(held);

        // OrderBy is stable, and the reliable deadlines are monotonic per sender, so sorting the
        // pooled set by deadline keeps both send orders intact.
        foreach (var pending in due.OrderBy(pending => pending.Deadline))
        {
            if (pending.Reliability == NetReliability.UnreliableSequenced)
            {
                long key = Key(pending.From, pending.Channel);
                _newest.TryGetValue(key, out int newest);
                if (pending.Sequence <= newest)
                {
                    continue;
                }

                _newest[key] = pending.Sequence;
            }

            _listener?.OnPayload(pending.From, pending.Channel, pending.Payload);
        }
    }

    // One payload waiting on the receiving end's clock, with the moment it becomes due.
    private readonly record struct Pending(
        int From, int Channel, NetReliability Reliability, int Sequence, byte[] Payload, double Deadline);
}
