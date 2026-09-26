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

    // The stall suite's own walk. Engine suites run in parallel shards, and a busy port costs an
    // engine error line that the battery counts against the shard.
    private const int StallFirstPort = 47130;

    // How long a wait for the other end may take before the check that wanted it reports what it
    // saw instead. Loopback delivery is sub-millisecond; this is the give-up, not the budget.
    private const double WaitSeconds = 3.0;

    // How long "nothing should arrive" is watched for. Long enough that a payload the carrier did
    // send would have landed, short enough to cost the suite nothing.
    private const double QuietSeconds = 0.2;

    // The stall suite's ENet timeout. Short, so a stall four times as long costs two seconds
    // rather than the forty a shipped timeout would need.
    private const int ShortTimeoutMs = 500;

    // How long one end goes unstepped, four timeouts' worth.
    private const double StallSeconds = 2.0;

    // How often the stepped end sends into a stall, so ENet always has a reliable send awaiting
    // an acknowledgement.
    private const double SendEverySeconds = 0.05;

    // The give-up for a drop that must happen, three timeouts' worth.
    private const double DropWithinSeconds = 1.5;

    // The service thread's ceiling in the hung-game check, well inside its stall.
    private const double HungCeilingSeconds = 1.0;

    // What this run cannot show, said where the report carries it. A loopback socket delivers in
    // send order, so no sequenced payload here is stale on arrival.
    private const string StaleNote =
        "a stale sequenced payload cannot be provoked over a loopback socket, which never reorders; "
        + "the discard rule stays asserted on the loopback carrier, and this run asserts the ENet "
        + "delivery class the payload is sent under";

    private static readonly EnetTransport.Keepalive Short = new(32, ShortTimeoutMs, ShortTimeoutMs, 300.0);

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

    [Suite("enet-load-stall",
        "an ENet end whose main thread stops stepping, as a blocking mission load does, keeps its "
        + "link: a stall four times the timeout on either end drops nothing, and every reliable "
        + "payload sent into it lands in order afterwards, and the unreliable state channel carries at "
        + "once. Without the service thread the same stall "
        + "drops the link, a frozen end is still dropped within the timeout, and so is one stalled "
        + "past the ceiling")]
    internal static void LoadStallKeepsTheLink(TestContext ctx)
    {
        ctx.Note($"the stall is driven by not stepping one end for {StallSeconds:0.0}s of wall time against a {ShortTimeoutMs} ms ENet timeout, over 127.0.0.1, whose round trip is near zero; the shipped keepalive keeps Godot's 5 to 30 s timeouts");
        using (var pair = Pair.Open(ctx, Short))
        {
            if (pair == null)
            {
                return;
            }

            StallSurvives(ctx, pair, stallHost: false);
            StallSurvives(ctx, pair, stallHost: true);

            // A process that dies takes its service thread with it, and ENet's own timeout is the
            // only thing left to notice.
            pair.Guest.Freeze();
            double dropped = Stall(pair.Host, pair.Guest, pair.AtHost, pair.GuestPeer, StallSeconds, out _);
            ctx.Check(!pair.Host.Peers.Contains(pair.GuestPeer) && dropped <= DropWithinSeconds,
                $"a frozen guest, a crashed one, is still dropped by the host within the timeout (after {dropped:0.00}s, roster {Ids(pair.Host.Peers)})");
            ctx.Note($"frozen guest dropped after {dropped:0.00}s");
        }

        using (var control = Pair.Open(ctx, Short with { Service = false }))
        {
            if (control == null)
            {
                return;
            }

            double dropped = Stall(control.Host, control.Guest, control.AtHost, control.GuestPeer, StallSeconds, out _);
            ctx.Check(!control.Host.Peers.Contains(control.GuestPeer) && dropped < StallSeconds,
                $"control: without the service thread the same stall drops the guest (after {dropped:0.00}s of {StallSeconds:0.0}s, roster {Ids(control.Host.Peers)})");
            ctx.Note($"control: the unserviced stall dropped the guest after {dropped:0.00}s");
        }

        using (var hung = Pair.Open(ctx, Short with { CeilingSeconds = HungCeilingSeconds }))
        {
            if (hung == null)
            {
                return;
            }

            double dropped = Stall(hung.Host, hung.Guest, hung.AtHost, hung.GuestPeer, StallSeconds * 2.0, out _);
            ctx.Check(!hung.Host.Peers.Contains(hung.GuestPeer) && dropped >= HungCeilingSeconds
                      && dropped <= HungCeilingSeconds + DropWithinSeconds,
                $"a guest stalled past the {HungCeilingSeconds:0.0}s ceiling, a hung game, is dropped after the ceiling and the timeout (after {dropped:0.00}s)");
            ctx.Note($"hung guest dropped after {dropped:0.00}s");
        }
    }

    private static string Ids(IEnumerable<int> peers) => string.Join(", ", peers);

    // One end stalls while the other steps and sends reliably into the stall. That is a host that
    // finished its load talking to a guest still in its own.
    private static void StallSurvives(TestContext ctx, Pair pair, bool stallHost)
    {
        var (stepped, stalled, atStepped, atStalled) = stallHost
            ? (pair.Guest, pair.Host, pair.AtGuest, pair.AtHost)
            : (pair.Host, pair.Guest, pair.AtHost, pair.AtGuest);
        int toStalled = stallHost ? 1 : pair.GuestPeer;
        int toStepped = stallHost ? pair.GuestPeer : 1;
        string who = stallHost ? "host" : "guest";
        atStalled.Payloads.Clear();
        Stall(stepped, stalled, atStepped, toStalled, StallSeconds, out int sent);
        Pump(pair.Host, pair.Guest, () => atStalled.Payloads.Count >= sent);

        ctx.Check(stepped.Peers.Contains(toStalled) && stalled.Peers.Contains(toStepped),
            $"a {who} that stopped stepping for {StallSeconds:0.0}s is still on both rosters ({Ids(pair.Host.Peers)} and {Ids(pair.Guest.Peers)})");
        ctx.Check(stepped.LinkState == EnetLinkState.Up && stalled.LinkState == EnetLinkState.Up,
            $"and both links still read up ({stepped.LinkState} and {stalled.LinkState})");
        var tags = atStalled.Payloads.Select(p => BitConverter.ToInt32(p.Bytes, 0)).ToList();
        ctx.Check(sent > 0 && tags.SequenceEqual(Enumerable.Range(0, sent)),
            $"and the {sent} reliable payload(s) sent into the {who}'s stall all land afterwards, in order ({tags.Count} landed)");

        // The state channel right after the stall. ENet thins unreliable sends when round trips
        // jump, and a stall is one long round trip.
        const int Burst = 20;
        atStalled.Payloads.Clear();
        for (int i = 0; i < Burst; i++)
        {
            stepped.Send(toStalled, BitConverter.GetBytes(i), NetReliability.UnreliableSequenced, channel: 1);
            Pump(pair.Host, pair.Guest, () => atStalled.Payloads.Count > i);
            if (atStalled.Payloads.Count <= i)
            {
                break;
            }
        }

        ctx.Check(atStalled.Payloads.Count == Burst,
            $"and the state channel carries at once after the {who}'s stall ({atStalled.Payloads.Count} of {Burst} unreliable payloads landed before the first loss)");
    }

    // Steps one end alone for the span, sending the other a reliable payload every tick. Answers
    // how long the stepped end kept the stalled one on its roster.
    private static double Stall(EnetTransport stepped, EnetTransport stalled, Recorder atStepped, int toStalled,
        double seconds, out int sent)
    {
        sent = 0;
        int before = atStepped.Disconnected.Count;
        var watch = Stopwatch.StartNew();
        double nextSend = 0.0;
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            if (watch.Elapsed.TotalSeconds >= nextSend)
            {
                stepped.Send(toStalled, BitConverter.GetBytes(sent), NetReliability.Reliable);
                sent++;
                nextSend += SendEverySeconds;
            }

            stepped.Step(0.001);
            if (atStepped.Disconnected.Count > before)
            {
                return watch.Elapsed.TotalSeconds;
            }

            Thread.Sleep(2);
        }

        return watch.Elapsed.TotalSeconds;
    }

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
    private static EnetTransport? OpenHost(out int port, out string why, EnetTransport.Keepalive? keepalive = null,
        int firstPort = FirstPort)
    {
        why = "no port tried";
        for (int i = 0; i < PortsToTry; i++)
        {
            port = firstPort + i;
            try
            {
                return EnetTransport.Host(port, maxPeers: 4, bindAddress: Loopback, keepalive);
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

        // The last seat's fire channel is the highest one negotiated. A count one short would
        // refuse it silently at the socket, which no loopback suite can see.
        atGuest.Payloads.Clear();
        int top = NetChannels.ForFire(NetSeats.MaxPlayers - 1);
        byte[] far = Payload(0x22, 8);
        host.Send(guestPeer, far, NetReliability.Unreliable, channel: top);
        Pump(host, guest, () => atGuest.Payloads.Count > 0);
        var topLanded = atGuest.Payloads.FirstOrDefault();
        bool topCarried = topLanded != null && topLanded.Channel == top && topLanded.Bytes.SequenceEqual(far);
        ctx.Check(topCarried && top == EnetTransport.ChannelCount - 1,
            $"and the last seat's fire channel, the highest negotiated, carries over the socket (channel {top} of {EnetTransport.ChannelCount}, landed on {topLanded?.Channel})");

        // The last seat's state channel sits below the fire range and carries too.
        atGuest.Payloads.Clear();
        int state = NetChannels.ForSeat(NetSeats.MaxPlayers - 1);
        host.Send(guestPeer, far, NetReliability.UnreliableSequenced, channel: state);
        Pump(host, guest, () => atGuest.Payloads.Count > 0);
        var stateLanded = atGuest.Payloads.FirstOrDefault();
        ctx.Check(stateLanded != null && stateLanded.Channel == state && state < NetChannels.FirstFire,
            $"and so does the last seat's state channel, below the fire range (channel {state}, landed on {stateLanded?.Channel})");
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

    // A host and a guest joined over the loopback address, on one keepalive, closed together.
    private sealed class Pair : IDisposable
    {
        private Pair(EnetTransport host, EnetTransport guest)
        {
            Host = host;
            Guest = guest;
            host.Bind(AtHost);
            guest.Bind(AtGuest);
        }

        public EnetTransport Host { get; }

        public EnetTransport Guest { get; }

        public Recorder AtHost { get; } = new();

        public Recorder AtGuest { get; } = new();

        public int GuestPeer => Guest.LocalPeer;

        // Null, with the reason checked as a failure, when the socket will not open or join.
        public static Pair? Open(TestContext ctx, EnetTransport.Keepalive keepalive)
        {
            var host = OpenHost(out int port, out string why, keepalive, StallFirstPort);
            if (host == null)
            {
                ctx.Check(false, $"ENet cannot host on {Loopback} in this process: {why}");
                return null;
            }

            var pair = new Pair(host, EnetTransport.Join(Loopback, port, keepalive));
            Pump(host, pair.Guest, () => pair.AtHost.Connected.Count > 0 && pair.AtGuest.Connected.Count > 0);
            if (pair.Host.Peers.Count == 0 || pair.Guest.Peers.Count == 0)
            {
                ctx.Check(false, $"the guest never joined the host on {Loopback}:{port}");
                pair.Dispose();
                return null;
            }

            return pair;
        }

        public void Dispose()
        {
            Guest.Dispose();
            Host.Dispose();
        }
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
