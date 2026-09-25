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
    public void APeerConnectingLaterIsHandedTheCurrentAdvertBeforeAnythingElse()
    {
        var host = new RecordingTransport(localPeer: 1);
        var lobby = new NetLobby(host);
        lobby.Advertise(new SessionAdvertMessage(NetSessionKind.Dogfight, SessionAdvertMessage.NoMission, 1, ""));
        var session = new RecordingListener();
        lobby.Bind(session);

        host.Connect(7);
        Assert.Single(host.Sent);
        Assert.Equal(7, host.Sent[0].Peer);
        Assert.True(SessionAdvertMessage.TryRead(host.Sent[0].Bytes, out _));
        Assert.Equal(new[] { 7 }, session.Connected);
    }

    [Fact]
    public void EverythingButTheAdvertIsHeldUntilASessionBindsThenReplayedInOrder()
    {
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(3));
        var guest = new NetLobby(mesh[1]);
        Span<byte> buffer = stackalloc byte[HandshakeMessage.Size];
        new HandshakeMessage(1, 0.0, 1).Write(buffer);
        mesh[0].Send(1, buffer, NetReliability.Reliable);
        new SessionAdvertMessage(NetSessionKind.CampaignCoop, 0, 2, "h").Write(buffer);
        mesh[0].Send(1, buffer, NetReliability.Reliable);
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

        public void OnPeerConnected(int peer) => Connected.Add(peer);

        public void OnPeerDisconnected(int peer)
        {
        }

        public void OnPayload(int peer, int channel, ReadOnlySpan<byte> payload) => Payloads.Add(payload.ToArray());
    }
}
