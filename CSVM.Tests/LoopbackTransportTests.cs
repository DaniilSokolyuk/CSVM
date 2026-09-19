using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Net;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The loopback carrier's contract, driven as two sessions will drive it. In-order, reordered,
/// delayed and dropped deliveries between two transports, with latency, jitter and loss from a
/// seeded generator. Every reliability guarantee the seam promises is asserted here, because this
/// is the carrier every replication suite above it runs on.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class LoopbackTransportTests
{
    [Fact]
    public void A_bound_listener_is_told_about_the_peers_the_mesh_already_gave_it()
    {
        var mesh = LoopbackTransport.Mesh(3, LoopbackConditions.Perfect, new Random(1));
        var host = new Recorder();

        mesh[0].Bind(host);

        Assert.Equal(0, mesh[0].LocalPeer);
        Assert.Equal(new[] { 1, 2 }, mesh[0].Peers);
        Assert.Equal(new[] { 1, 2 }, host.Connected);
        Assert.Throws<InvalidOperationException>(() => mesh[0].Bind(new Recorder()));
    }

    [Fact]
    public void Nothing_is_delivered_before_a_step_and_nothing_before_its_latency()
    {
        var mesh = LoopbackTransport.Mesh(2, new LoopbackConditions(0.1, 0.0, 0.0), new Random(2));
        var guest = new Recorder();
        mesh[1].Bind(guest);

        mesh[0].Send(1, Tag(7), NetReliability.Reliable);
        Assert.Empty(guest.Tags);

        mesh[1].Step(0.05);
        Assert.Empty(guest.Tags);

        mesh[1].Step(0.06);
        Assert.Equal(new[] { 7 }, guest.Tags);
        Assert.Equal(0.11, mesh[1].Now, 6);
    }

    [Fact]
    public void Loss_takes_both_unreliable_classes_and_never_a_reliable_payload()
    {
        var mesh = LoopbackTransport.Mesh(2, new LoopbackConditions(0.0, 0.0, 1.0), new Random(3));
        var guest = new Recorder();
        mesh[1].Bind(guest);

        mesh[0].Send(1, Tag(1), NetReliability.Unreliable);
        mesh[0].Send(1, Tag(2), NetReliability.UnreliableSequenced);
        mesh[0].Send(1, Tag(3), NetReliability.Reliable);
        mesh[1].Step(1.0);

        Assert.Equal(new[] { 3 }, guest.Tags);
    }

    [Fact]
    public void A_reliable_stream_keeps_its_send_order_under_jitter_and_total_loss()
    {
        // Jitter wider than half the latency, and loss at certainty. Either would reorder or thin
        // this stream if the reliable class were carried like the other two.
        var mesh = LoopbackTransport.Mesh(2, new LoopbackConditions(0.05, 0.04, 1.0), new Random(4));
        var guest = new Recorder();
        mesh[1].Bind(guest);

        for (int tag = 1; tag <= 20; tag++)
        {
            mesh[0].Send(1, Tag(tag), NetReliability.Reliable);
        }

        mesh[1].Step(1.0);

        Assert.Equal(Enumerable.Range(1, 20).ToArray(), guest.Tags);
    }

    [Theory]
    [InlineData(NetReliability.UnreliableSequenced, new[] { 2 })]
    [InlineData(NetReliability.Unreliable, new[] { 2, 1 })]
    public void An_overtaken_payload_is_dropped_only_when_it_is_sequenced(
        NetReliability reliability, int[] expected)
    {
        // The reorder is made rather than drawn. The first payload goes over a slow link and the
        // second over an instant one, so the newer arrives a step ahead.
        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(5));
        var guest = new Recorder();
        mesh[1].Bind(guest);

        mesh[0].SetConditions(1, new LoopbackConditions(0.2, 0.0, 0.0));
        mesh[0].Send(1, Tag(1), reliability);
        mesh[0].SetConditions(1, LoopbackConditions.Perfect);
        mesh[0].Send(1, Tag(2), reliability);

        mesh[1].Step(0.05);
        mesh[1].Step(0.5);

        Assert.Equal(expected, guest.Tags);
    }

    [Fact]
    public void Sequencing_is_per_channel_so_a_buried_payload_buries_nothing_beside_it()
    {
        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(6));
        var guest = new Recorder();
        mesh[1].Bind(guest);

        mesh[0].SetConditions(1, new LoopbackConditions(0.2, 0.0, 0.0));
        mesh[0].Send(1, Tag(1), NetReliability.UnreliableSequenced, 0);
        mesh[0].Send(1, Tag(3), NetReliability.UnreliableSequenced, 1);
        mesh[0].SetConditions(1, LoopbackConditions.Perfect);
        mesh[0].Send(1, Tag(2), NetReliability.UnreliableSequenced, 0);

        mesh[1].Step(0.05);
        mesh[1].Step(0.5);

        // Tag 2 overtook tag 1 and buried it. Tag 3 is the first payload on channel 1, so landing
        // late costs it nothing.
        Assert.Equal(new[] { 2, 3 }, guest.Tags);
        Assert.Equal(new[] { 0, 1 }, guest.Payloads.Select(payload => payload.Channel).ToArray());
    }

    [Fact]
    public void Both_directions_carry_and_a_hang_up_empties_both_rosters()
    {
        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(7));
        var host = new Recorder();
        var guest = new Recorder();
        mesh[0].Bind(host);
        mesh[1].Bind(guest);

        mesh[0].Send(1, Tag(1), NetReliability.Reliable);
        mesh[1].Send(0, Tag(2), NetReliability.Reliable);
        mesh[0].Step(0.0);
        mesh[1].Step(0.0);

        Assert.Equal(new[] { 2 }, host.Tags);
        Assert.Equal(new[] { 1 }, guest.Tags);

        mesh[0].Disconnect(1);
        mesh[0].Send(1, Tag(9), NetReliability.Reliable);
        mesh[1].Step(1.0);

        Assert.Empty(mesh[0].Peers);
        Assert.Empty(mesh[1].Peers);
        Assert.Equal(new[] { 1 }, host.Disconnected);
        Assert.Equal(new[] { 0 }, guest.Disconnected);
        Assert.Equal(new[] { 1 }, guest.Tags);
    }

    [Fact]
    public void One_seed_replays_a_lossy_jittered_run_exactly()
    {
        int[] first = Run(new Random(11));
        int[] again = Run(new Random(11));

        Assert.Equal(first, again);

        // The model really acts in both directions it claims to. Payloads went missing, and the
        // survivors did not arrive in the order they were sent.
        Assert.InRange(first.Length, 1, 39);
        Assert.NotEqual(first.OrderBy(tag => tag).ToArray(), first);

        static int[] Run(Random rng)
        {
            var mesh = LoopbackTransport.Mesh(2, new LoopbackConditions(0.05, 0.03, 0.35), rng);
            var guest = new Recorder();
            mesh[1].Bind(guest);
            for (int tag = 1; tag <= 40; tag++)
            {
                mesh[0].Send(1, Tag(tag), NetReliability.Unreliable);
                mesh[1].Step(0.01);
            }

            mesh[1].Step(1.0);
            return guest.Tags;
        }
    }

    private static byte[] Tag(int tag) => new[] { (byte)tag };

    private sealed class Recorder : INetTransportListener
    {
        public List<int> Connected { get; } = new();

        public List<int> Disconnected { get; } = new();

        public List<(int Peer, int Channel, int Tag)> Payloads { get; } = new();

        public int[] Tags => Payloads.Select(payload => payload.Tag).ToArray();

        public void OnPeerConnected(int peer) => Connected.Add(peer);

        public void OnPeerDisconnected(int peer) => Disconnected.Add(peer);

        public void OnPayload(int peer, int channel, ReadOnlySpan<byte> payload) =>
            Payloads.Add((peer, channel, payload[0]));
    }
}
