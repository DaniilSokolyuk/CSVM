using System;
using System.Collections.Generic;
using CSVM.Net;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The lobby between a carrier and the session that later binds it. A host hands out its advert
/// on connect and on change, and a guest keeps the adverts. Every other payload is held until a
/// session binds, then replayed in order behind the roster.
/// </summary>
public class NetLobbyTests
{
    private static readonly LoopbackConditions Clean = new(0.0, 0.0, 0.0);

    [Fact]
    public void AnAdvertReachesEveryPeerAndIsResentOnlyWhenItChanges()
    {
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(1));
        var host = new NetLobby(mesh[0]);
        var guest = new NetLobby(mesh[1]);
        var advert = new SessionAdvertMessage(NetSessionKind.CampaignCoop, 4, 2, "Zachary");

        host.Advertise(advert);
        host.Advertise(advert);
        guest.Step(0.016);
        Assert.Equal(advert, guest.Advert);

        // The second call changed nothing, so nothing more crossed; the guest holds no payload.
        Assert.Equal(0, guest.Held);

        host.Advertise(advert with { Players = 3 });
        guest.Step(0.016);
        Assert.Equal(3, guest.Advert!.Value.Players);
    }

    [Fact]
    public void APeerConnectingMidFlightIsHandedTheAdvertButNeverReachesTheBoundSession()
    {
        var host = new RecordingTransport(localPeer: 1);
        var lobby = new NetLobby(host);
        lobby.Advertise(new SessionAdvertMessage(NetSessionKind.Dogfight, SessionAdvertMessage.NoMission, 1, ""));
        host.Connect(3);
        var session = new RecordingListener();
        lobby.Bind(session);

        // Each connect is answered with the build version, then the advert.
        host.Connect(7);
        Assert.Equal(4, host.Sent.Count);
        Assert.Equal(7, host.Sent[2].Peer);
        Assert.True(BuildVersionMessage.TryRead(host.Sent[2].Bytes, out _));
        Assert.Equal(7, host.Sent[3].Peer);
        Assert.True(SessionAdvertMessage.TryRead(host.Sent[3].Bytes, out _));

        // The field was fixed when the session bound: the newcomer waits in the lobby.
        Assert.Equal(new[] { 3 }, session.Connected);
        Assert.Equal(new[] { 3 }, lobby.Peers);
        Assert.Equal(new[] { 3, 7 }, lobby.AllPeers);
        host.Deliver(7, Handshake(5));
        Assert.Empty(session.Payloads);

        // ABLE-TO-FAIL CONTROL: the peer present at the bind is the session's, payloads included.
        host.Deliver(3, Handshake(6));
        Assert.Single(session.Payloads);
    }

    [Fact]
    public void APeerNamingAVersionThatDoesNotPlayLeavesEveryPeerListAndIsHeardOnlyToSayWhy()
    {
        var host = new RecordingTransport(localPeer: 1);
        var lobby = new NetLobby(host, new NetBuildVersion(0, 7));
        host.Connect(3);
        host.Connect(7);
        var session = new RecordingListener();
        lobby.Bind(session);

        // Peer 3 differs only in the patch, which the version does not carry: it stays.
        host.Deliver(3, Bytes(new BuildVersionMessage(new NetBuildVersion(0, 7))));
        host.Deliver(7, Handshake(4));
        host.Deliver(7, Bytes(new BuildVersionMessage(new NetBuildVersion(0, 6))));

        Assert.Equal(new[] { 7 }, lobby.Clashing);
        Assert.Equal(new[] { 3 }, lobby.Peers);
        Assert.Equal(new[] { 3 }, lobby.AllPeers);
        Assert.Equal(new[] { 7 }, session.Disconnected);
        Assert.True(lobby.TryVersionOf(7, out var theirs));
        Assert.Equal(new NetBuildVersion(0, 6), theirs);

        // What the clashing peer sends later is dropped, but not its close notice.
        int before = session.Payloads.Count;
        host.Deliver(7, Handshake(5));
        host.Deliver(7, Bytes(new SessionAdvertMessage(NetSessionKind.Dogfight, 0, 1, "Oskar")));
        Assert.Equal(before, session.Payloads.Count);
        Assert.Null(lobby.Advert);
        host.Deliver(7, Bytes(new SessionClosedMessage(NetCloseReason.VersionMismatch, new NetBuildVersion(0, 6), new NetBuildVersion(0, 7))));
        Assert.Equal(NetCloseReason.VersionMismatch, lobby.Closed!.Value.Reason);

        // The farewell names both ends' versions.
        lobby.Farewell(7, NetCloseReason.VersionMismatch);
        Assert.True(SessionClosedMessage.TryRead(host.Sent[^1].Bytes, out var farewell));
        Assert.Equal(new SessionClosedMessage(NetCloseReason.VersionMismatch, new NetBuildVersion(0, 7), new NetBuildVersion(0, 6)), farewell);

        // ABLE-TO-FAIL CONTROL: the peer whose version plays is still the session's.
        host.Deliver(3, Handshake(6));
        Assert.Equal(before + 1, session.Payloads.Count);
        host.Drop(7);
        Assert.Empty(lobby.Clashing);
    }

    [Fact]
    public void UnbindFreesTheCarrierForTheNextFlightAndDropsWhatWasHeld()
    {
        var host = new RecordingTransport(localPeer: 1);
        var lobby = new NetLobby(host);
        host.Connect(3);
        var first = new RecordingListener();
        lobby.Bind(first);

        // ABLE-TO-FAIL CONTROL: a second bind without an unbind is refused.
        Assert.Throws<InvalidOperationException>(() => lobby.Bind(new RecordingListener()));

        lobby.Unbind();
        Assert.False(lobby.Bound);
        host.Connect(7);
        host.Deliver(3, Handshake(1));
        Assert.Equal(1, lobby.Held);
        lobby.DropHeld();
        Assert.Equal(0, lobby.Held);
        host.Deliver(7, Handshake(2));
        lobby.Unbind();
        Assert.Equal(0, lobby.Held);

        var second = new RecordingListener();
        lobby.Bind(second);
        Assert.Equal(new[] { 3, 7 }, second.Connected);
        Assert.Empty(second.Payloads);
        Assert.Empty(first.Payloads);
    }

    [Fact]
    public void CoopFlowsAndPicksAreKeptByTheLobbyAndNeverPassedOn()
    {
        var host = new RecordingTransport(localPeer: 1);
        var lobby = new NetLobby(host);
        host.Connect(3);
        var session = new RecordingListener();
        lobby.Bind(session);

        var flow = new CoopFlowMessage(NetCoopScreen.FlightCheck, 4, 2, 1, 0b10, 2, 3, false, 0b100000, 0, 0);
        host.Deliver(3, Bytes(flow));
        host.Deliver(3, Bytes(flow));
        host.Deliver(3, Bytes(new CoopPickMessage(2, true, 7)));

        Assert.Equal(flow, lobby.Flow);
        Assert.Equal(2, lobby.Flows);
        Assert.Equal(new CoopPickMessage(2, true, 7), lobby.Picks[3]);

        // A seat fit is kept by seat, and a later launch's word for the seat replaces it.
        var fit = CoopFit.Of(new[] { 1 }, new[] { 4 });
        host.Deliver(3, Bytes(new CoopSeatFitMessage(2, default)));
        host.Deliver(3, Bytes(new CoopSeatFitMessage(2, fit)));
        Assert.Equal(fit, lobby.SeatFits[2]);
        Assert.Empty(session.Payloads);

        // A departing guest's pick goes with it, and its leaving reaches the session it flew in.
        host.Drop(3);
        Assert.False(lobby.Picks.ContainsKey(3));
        Assert.Equal(new[] { 3 }, session.Disconnected);

        // ABLE-TO-FAIL CONTROL: a peer that never belonged to the session leaves it untold.
        host.Connect(9);
        host.Drop(9);
        Assert.Equal(new[] { 3 }, session.Disconnected);
    }

    [Fact]
    public void EverythingButTheAdvertIsHeldUntilASessionBindsThenReplayedInOrder()
    {
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(3));
        var guest = new NetLobby(mesh[1]);
        Span<byte> buffer = stackalloc byte[HandshakeMessage.Size];
        Span<byte> advert = stackalloc byte[SessionAdvertMessage.Size];
        new HandshakeMessage(1, 0.0, 1).Write(buffer);
        mesh[0].Send(1, buffer, NetReliability.Reliable);
        new SessionAdvertMessage(NetSessionKind.CampaignCoop, 0, 2, "h").Write(advert);
        mesh[0].Send(1, advert, NetReliability.Reliable);
        new HandshakeMessage(2, 0.0, 1).Write(buffer);
        mesh[0].Send(1, buffer, NetReliability.Reliable);
        guest.Step(0.016);

        Assert.Equal(2, guest.Held);
        Assert.NotNull(guest.Advert);

        var session = new RecordingListener();
        guest.Bind(session);
        Assert.Equal(new[] { 0 }, session.Connected);
        Assert.Equal(2, session.Payloads.Count);
        Assert.True(HandshakeMessage.TryRead(session.Payloads[0], out var first));
        Assert.True(HandshakeMessage.TryRead(session.Payloads[1], out var second));
        Assert.Equal(1UL, first.Seed);
        Assert.Equal(2UL, second.Seed);
        Assert.Equal(0, guest.Held);
        Assert.Throws<InvalidOperationException>(() => guest.Bind(new RecordingListener()));
    }

    [Fact]
    public void TheHoldIsBoundedAndDropsTheOldest()
    {
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(5));
        var guest = new NetLobby(mesh[1]);
        Span<byte> buffer = stackalloc byte[HandshakeMessage.Size];
        for (int i = 0; i < NetLobby.HeldPayloads + 3; i++)
        {
            new HandshakeMessage((ulong)i, 0.0, 1).Write(buffer);
            mesh[0].Send(1, buffer, NetReliability.Reliable);
        }

        guest.Step(0.016);
        Assert.Equal(NetLobby.HeldPayloads, guest.Held);

        var session = new RecordingListener();
        guest.Bind(session);
        Assert.True(HandshakeMessage.TryRead(session.Payloads[0], out var oldest));
        Assert.Equal(3UL, oldest.Seed);
    }

    [Fact]
    public void AHostSessionOverTheLobbySeatsARemoteGuestTheGuestSessionHears()
    {
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(7));
        var hostLobby = new NetLobby(mesh[0]);
        var guestLobby = new NetLobby(mesh[1]);
        hostLobby.Advertise(new SessionAdvertMessage(NetSessionKind.CampaignCoop, 0, 2, "Zachary"));

        var roster = NetSeats.Field(hostLobby.LocalPeer, new[] { "player_bhawk" }, hostLobby.Peers, "player_bhawk");
        Assert.Equal(2, roster.Length);
        Assert.True(roster[0].IsLocal);
        Assert.False(roster[1].IsLocal);
        Assert.Equal(1, roster[1].PeerId);

        _ = NetSession.Host(hostLobby, roster, seed: 99);
        guestLobby.Step(0.016);
        var guest = NetSession.Guest(guestLobby);
        guestLobby.Step(0.016);

        Assert.True(guest.Joined);
        Assert.Equal(1, guest.LocalSeat);
        Assert.Equal(0, guest.DroppedUnknown);
    }

    private static byte[] Handshake(ulong seed)
    {
        var bytes = new byte[HandshakeMessage.Size];
        new HandshakeMessage(seed, 0.0, 1).Write(bytes);
        return bytes;
    }

    private static byte[] Bytes<T>(in T message)
        where T : struct, INetMessage<T>
    {
        var bytes = new byte[64];
        int length = message.Write(bytes);
        return bytes.AsSpan(0, length).ToArray();
    }

    // A carrier that records its sends and connects a peer on demand.
    private sealed class RecordingTransport : INetTransport
    {
        private readonly List<int> _peers = new();
        private INetTransportListener? _listener;

        public RecordingTransport(int localPeer) => LocalPeer = localPeer;

        public List<(int Peer, byte[] Bytes)> Sent { get; } = new();

        public int LocalPeer { get; }

        public IReadOnlyList<int> Peers => _peers;

        public void Bind(INetTransportListener listener) => _listener = listener;

        public void Connect(int peer)
        {
            _peers.Add(peer);
            _listener?.OnPeerConnected(peer);
        }

        public void Deliver(int peer, byte[] payload) => _listener?.OnPayload(peer, 0, payload);

        public void Drop(int peer)
        {
            _peers.Remove(peer);
            _listener?.OnPeerDisconnected(peer);
        }

        public void Send(int peer, ReadOnlySpan<byte> payload, NetReliability reliability, int channel = 0) =>
            Sent.Add((peer, payload.ToArray()));

        public void Disconnect(int peer) => _peers.Remove(peer);

        public void Step(double dt)
        {
        }
    }

    // A session stand-in that keeps what it was told.
    private sealed class RecordingListener : INetTransportListener
    {
        public List<int> Connected { get; } = new();

        public List<byte[]> Payloads { get; } = new();

        public List<int> Disconnected { get; } = new();

        public void OnPeerConnected(int peer) => Connected.Add(peer);

        public void OnPeerDisconnected(int peer) => Disconnected.Add(peer);

        public void OnPayload(int peer, int channel, ReadOnlySpan<byte> payload) => Payloads.Add(payload.ToArray());
    }
}
