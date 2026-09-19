using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CSVM.Net;

namespace CSVM.Testing;

/// <summary>The shipped ENet carrier, hosting and joining itself inside this one process over the
/// loopback address. It needs no extraction and no world. The subject is the socket, the roster
/// and the three delivery classes, driven as a session drives them.</summary>
internal static class EnetTransportSuites
{
    // Loopback only. A wildcard bind is what makes Windows ask the user about the firewall, and a
    // test run must never put a dialog on anybody's screen.
    private const string Loopback = "127.0.0.1";

    // Below the ephemeral range Windows allocates from. Walked rather than fixed, so a second run
    // does not fail on a port its sibling still holds.
    private const int FirstPort = 47100;
    private const int PortsToTry = 20;

    // How long a wait for the other end may take before the check that wanted it reports what it
    // saw instead. Loopback delivery is sub-millisecond; this is the give-up, not the budget.
    private const double WaitSeconds = 3.0;

    // How long "nothing should arrive" is watched for. Long enough that a payload the carrier did
    // send would have landed, short enough to cost the suite nothing.
    private const double QuietSeconds = 0.2;

    // What this run cannot show, said where the report carries it. A loopback socket delivers in
    // send order, so no sequenced payload here is stale on arrival.
    private const string StaleNote =
        "a stale sequenced payload cannot be provoked over a loopback socket, which never reorders; "
        + "the discard rule stays asserted on the loopback carrier, and this run asserts the ENet "
        + "delivery class the payload is sent under";

    [Suite("enet-transport",
        "the shipped ENet carrier hosts and joins itself over 127.0.0.1 inside one process: the "
        + "carrier selection hands this build that socket, each "
        + "end is told the other joined, nothing arrives until a step runs, a reliable payload "
        + "round trips on its own channel with its bytes and its sender intact, an "
        + "unreliable-sequenced burst is never delivered behind a newer payload, a plain "
        + "unreliable payload carries, and a hang-up empties both rosters and swallows the sends "
        + "that follow it")]
    internal static void HostAndJoinOverLoopback(TestContext ctx)
    {
        EnetTransport? host = null;
        EnetTransport? guest = null;
        try
        {
            // What the door and the command line open through. Without the Steam define it has to
            // be this carrier, or the rest of the run proves a socket nothing opens.
            ctx.Check(!NetCarrier.UsesSteam && NetCarrier.Name == "enet",
                $"the carrier selection hands this build the ENet socket (it hands it {NetCarrier.Name})");

            host = OpenHost(out int port, out string why);
            if (host == null)
            {
                ctx.Check(false, $"ENet cannot host on {Loopback} in this process: {why}");
                return;
            }

            guest = EnetTransport.Join(Loopback, port);
            var atHost = new Recorder();
            var atGuest = new Recorder();
            host.Bind(atHost);
            guest.Bind(atGuest);
            int guestPeer = guest.LocalPeer;
            ctx.Check(atHost.Connected.Count == 0 && atGuest.Connected.Count == 0,
                $"a join is not announced before a step ({atHost.Connected.Count} at the host, {atGuest.Connected.Count} at the guest)");

            double connectSeconds = Pump(host, guest,
                () => atHost.Connected.Count > 0 && atGuest.Connected.Count > 0);
            ctx.Check(host.LocalPeer == 1, $"the host is peer 1 (it is {host.LocalPeer})");
            ctx.Check(atHost.Connected.SequenceEqual(new[] { guestPeer }),
                $"the host is told the guest joined, by the id the guest calls itself ({Ids(atHost.Connected)} against {guestPeer})");
            ctx.Check(atGuest.Connected.SequenceEqual(new[] { 1 }),
                $"and the guest is told it reached the host ({Ids(atGuest.Connected)})");
            ctx.Check(host.Peers.SequenceEqual(new[] { guestPeer }) && guest.Peers.SequenceEqual(new[] { 1 }),
                $"both rosters hold the other end ({Ids(host.Peers)} and {Ids(guest.Peers)})");
            ctx.Check(host.LinkState == EnetLinkState.Up && guest.LinkState == EnetLinkState.Up,
                $"and both links read up ({host.LinkState} and {guest.LinkState})");

            ReliableRoundTrip(ctx, host, guest, atHost, atGuest, guestPeer);
            SequencedBurst(ctx, host, guest, atGuest, guestPeer);
            UnreliableCarries(ctx, host, guest, atGuest, guestPeer);
            HangUp(ctx, host, guest, atHost, atGuest, guestPeer);

            ctx.Note($"hosted on {Loopback}:{port}, connected in {connectSeconds:0.000}s, guest peer {guestPeer}");
            ctx.Note($"{StaleNote}");
        }
        finally
        {
            guest?.Dispose();
            host?.Dispose();
        }
    }

    private static string Ids(IEnumerable<int> peers) => string.Join(", ", peers);

    private static byte[] Payload(byte tag, int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(tag + i);
        }

        return bytes;
    }

    // The first port of the walk that binds, or null with the last error. A busy port is the
    // expected miss here, so it costs a try rather than the suite.
    private static EnetTransport? OpenHost(out int port, out string why)
    {
        why = "no port tried";
        for (int i = 0; i < PortsToTry; i++)
        {
            port = FirstPort + i;
            try
            {
                return EnetTransport.Host(port, maxPeers: 4, bindAddress: Loopback);
            }
            catch (InvalidOperationException e)
            {
                why = e.Message;
            }
        }

        port = 0;
        return null;
    }

    // Steps both ends until the condition holds or the give-up passes, and answers how long that
    // took. The sleep is what lets the loopback socket actually carry between two polls.
    private static double Pump(EnetTransport host, EnetTransport guest, Func<bool> until)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            host.Step(0.001);
            guest.Step(0.001);
            if (until() || watch.Elapsed.TotalSeconds >= WaitSeconds)
            {
                return watch.Elapsed.TotalSeconds;
            }

            Thread.Sleep(1);
        }
    }

    // Steps both ends for a fixed short while with nothing to wait for. That is how a check for
    // nothing arriving is given every chance to fail.
    private static void PumpQuiet(EnetTransport host, EnetTransport guest)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < QuietSeconds)
        {
            host.Step(0.001);
            guest.Step(0.001);
            Thread.Sleep(1);
        }
    }

    private static void ReliableRoundTrip(TestContext ctx, EnetTransport host, EnetTransport guest,
        Recorder atHost, Recorder atGuest, int guestPeer)
    {
        byte[] question = Payload(0x5A, 200);
        guest.Send(1, question, NetReliability.Reliable, channel: 3);
        ctx.Check(atHost.Payloads.Count == 0,
            $"a sent payload does not arrive before the receiving end steps ({atHost.Payloads.Count} landed)");

        Pump(host, guest, () => atHost.Payloads.Count > 0);
        var landed = atHost.Payloads.FirstOrDefault();
        bool whole = landed != null && landed.Peer == guestPeer && landed.Channel == 3
                     && landed.Bytes.SequenceEqual(question);
        ctx.Check(whole,
            $"a reliable payload reaches the host whole, from the guest, on the channel it was sent on (peer {landed?.Peer}, channel {landed?.Channel}, {landed?.Bytes.Length} of {question.Length} bytes)");

        byte[] answer = Payload(0xA5, 48);
        host.Send(guestPeer, answer, NetReliability.Reliable, channel: 3);
        Pump(host, guest, () => atGuest.Payloads.Count > 0);
        var back = atGuest.Payloads.FirstOrDefault();
        bool returned = back != null && back.Peer == 1 && back.Channel == 3 && back.Bytes.SequenceEqual(answer);
        ctx.Check(returned,
            $"and the answer reaches the guest the same way (peer {back?.Peer}, channel {back?.Channel}, {back?.Bytes.Length} of {answer.Length} bytes)");
    }

    private static void SequencedBurst(TestContext ctx, EnetTransport host, EnetTransport guest,
        Recorder atGuest, int guestPeer)
    {
        const int Burst = 24;
        atGuest.Payloads.Clear();
        for (int tag = 1; tag <= Burst; tag++)
        {
            host.Send(guestPeer, Payload((byte)tag, 16), NetReliability.UnreliableSequenced, channel: 1);
        }

        Pump(host, guest, () => atGuest.Payloads.Count >= Burst);
        var tags = atGuest.Payloads.Select(p => (int)p.Bytes[0]).ToList();
        bool ordered = tags.Count > 0 && tags.SequenceEqual(tags.OrderBy(t => t))
                       && tags.Distinct().Count() == tags.Count;
        ctx.Check(ordered,
            $"no sequenced payload is delivered behind a newer one ({tags.Count} of {Burst} arrived: {Ids(tags)})");
        ctx.Check(atGuest.Payloads.All(p => p.Channel == 1 && p.Peer == 1),
            $"every one of them on the channel and from the peer it was sent on");
        ctx.Check(EnetTransport.ModeFor(NetReliability.UnreliableSequenced).ToString() == "UnreliableOrdered",
            $"and the class rides ENet's own sequenced unreliable delivery, which is what discards a stale one");
    }

    private static void UnreliableCarries(TestContext ctx, EnetTransport host, EnetTransport guest,
        Recorder atGuest, int guestPeer)
    {
        atGuest.Payloads.Clear();
        byte[] loose = Payload(0x11, 8);
        host.Send(guestPeer, loose, NetReliability.Unreliable, channel: 0);
        Pump(host, guest, () => atGuest.Payloads.Count > 0);
        var landed = atGuest.Payloads.FirstOrDefault();
        bool carried = landed != null && landed.Channel == 0 && landed.Bytes.SequenceEqual(loose);
        ctx.Check(carried,
            $"an unreliable payload carries too, on the default channel (channel {landed?.Channel}, {landed?.Bytes.Length} of {loose.Length} bytes)");
    }

    private static void HangUp(TestContext ctx, EnetTransport host, EnetTransport guest,
        Recorder atHost, Recorder atGuest, int guestPeer)
    {
        atHost.Payloads.Clear();
        atGuest.Payloads.Clear();
        host.Disconnect(guestPeer);
        Pump(host, guest, () => atHost.Disconnected.Count > 0 && atGuest.Disconnected.Count > 0);

        ctx.Check(atHost.Disconnected.SequenceEqual(new[] { guestPeer }),
            $"the host is told the guest left ({Ids(atHost.Disconnected)})");
        ctx.Check(atGuest.Disconnected.SequenceEqual(new[] { 1 }),
            $"and the guest is told the host did ({Ids(atGuest.Disconnected)})");
        ctx.Check(host.Peers.Count == 0 && guest.Peers.Count == 0,
            $"both rosters are empty ({Ids(host.Peers)} and {Ids(guest.Peers)})");

        host.Send(guestPeer, Payload(0x77, 8), NetReliability.Reliable);
        guest.Send(1, Payload(0x88, 8), NetReliability.Reliable);
        PumpQuiet(host, guest);
        ctx.Check(atHost.Payloads.Count == 0 && atGuest.Payloads.Count == 0,
            $"and a send to a peer that has left is discarded rather than thrown or carried ({atHost.Payloads.Count} and {atGuest.Payloads.Count} landed)");
    }

    // One payload as the listener saw it. The bytes are copied out of the transport's own buffer,
    // which the seam lends only for the call.
    private sealed class Arrival
    {
        public required int Peer { get; init; }

        public required int Channel { get; init; }

        public required byte[] Bytes { get; init; }
    }

    private sealed class Recorder : INetTransportListener
    {
        public List<int> Connected { get; } = new();

        public List<int> Disconnected { get; } = new();

        public List<Arrival> Payloads { get; } = new();

        public void OnPeerConnected(int peer) => Connected.Add(peer);

        public void OnPeerDisconnected(int peer) => Disconnected.Add(peer);

        public void OnPayload(int peer, int channel, ReadOnlySpan<byte> payload) =>
            Payloads.Add(new Arrival { Peer = peer, Channel = channel, Bytes = payload.ToArray() });
    }
}
