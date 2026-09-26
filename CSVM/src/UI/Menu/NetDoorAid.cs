using System;
using System.Collections.Generic;
using System.Threading;
using CSVM.Net;

namespace CSVM.UI.Menu;

/// <summary>
/// The multiplayer doors a screenshot aid stands on in place of the launcher's own. Both run over
/// the in-process loopback, so an aid opens no socket, raises no firewall dialog and asks no
/// router for a port. The host door's router answer is a fixed mapping at a documentation
/// address. The guest door is already linked to a loopback host holding a campaign mission open.
/// Nothing here is reachable outside the <c>--menu=</c> aids and the suites.
/// </summary>
public static class NetDoorAid
{
    /// <summary>The address the aid's router reports, from the range set aside for examples.
    /// </summary>
    public const string ExternalAddress = "203.0.113.24";

    /// <summary>The name the aid's campaign host advertises under.</summary>
    public const string HostName = "Zachary";

    // The mapping lands on a worker thread, so the aid waits a bounded while for it. A shot of
    // a band still asking the router would show a state no player sees for long.
    private const int MappingWaitMs = 2000;

    /// <summary>The games the aid's LAN answers with, each at its own documentation address. They
    /// are a campaign waiting with room, one full, one in the air, and a Dogfight.</summary>
    public static IReadOnlyList<LanGame> SampleGames { get; } = new[]
    {
        new LanGame("192.0.2.10", NetPlayFeature.DefaultPort, new SessionAdvertMessage(
            NetSessionKind.CampaignCoop, 2, 2, HostName, NetSessionStatus.Waiting, NetPlayFeature.CoopHumans)),
        new LanGame("192.0.2.11", NetPlayFeature.DefaultPort, new SessionAdvertMessage(
            NetSessionKind.CampaignCoop, 14, 4, "Nathan", NetSessionStatus.Full, NetPlayFeature.CoopHumans)),
        new LanGame("192.0.2.12", NetPlayFeature.DefaultPort, new SessionAdvertMessage(
            NetSessionKind.CampaignCoop, 30, 3, "Sheila", NetSessionStatus.InMission, NetPlayFeature.CoopHumans)),
        new LanGame("192.0.2.13", NetPlayFeature.DefaultPort, new SessionAdvertMessage(
            NetSessionKind.Dogfight, SessionAdvertMessage.NoMission, 5, "Lucy", NetSessionStatus.Waiting, NetSeats.MaxPlayers)),
    };

    /// <summary>A shut door whose host opens onto a loopback wire with <paramref name="guests"/>
    /// peers already on it, and whose router maps any port asked for.</summary>
    public static NetPlayFeature Host(int guests, out Func<int> unmapped) => Host(guests, out unmapped, out _);

    /// <summary>A door as <see cref="Host(int, out Func{int})"/>, with <paramref name="guestEnds"/>
    /// the guests' own ends of its wire so an aid can answer for them.</summary>
    public static NetPlayFeature Host(int guests, out Func<int> unmapped, out IReadOnlyList<INetTransport> guestEnds)
    {
        var mesh = LoopbackTransport.Mesh(1 + Math.Max(0, guests), LoopbackConditions.Perfect, new Random(1));
        int given = 0;
        unmapped = () => given;
        var ends = new List<INetTransport>(mesh);
        ends.RemoveAt(0);
        guestEnds = ends;
        return new NetPlayFeature(
            (port, maxGuests, bind) => mesh[0],
            (address, port) => throw new InvalidOperationException("the aid's host door joins nothing"),
            port => new UpnpPortMapResult(UpnpPortMapOutcome.Mapped, port, ExternalAddress, "aid"),
            port => given = port);
    }

    /// <summary>Opens <paramref name="door"/> as a campaign host and waits for its mapping, so the
    /// band reads as it settles.</summary>
    public static void OpenCoopHost(NetPlayFeature door, int missionSeq, int localPlayers)
    {
        ArgumentNullException.ThrowIfNull(door);
        door.OpenCoopHost(NetSeats.MaxPlayers - localPlayers);
        door.Offer(missionSeq, HostName, localPlayers);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (door.PortMap == null && waited.ElapsedMilliseconds < MappingWaitMs)
        {
            door.Step(0.0);
            Thread.Sleep(1);
        }
    }

    /// <summary>Answers Ready on <paramref name="airframe"/> for the guest at <paramref name="guest"/>,
    /// under the round <paramref name="host"/> has under way, and lets the host hear it.</summary>
    public static void AnswerReady(NetPlayFeature host, INetTransport guest, int airframe)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(guest);
        Span<byte> bytes = stackalloc byte[CoopPickMessage.Size];
        new CoopPickMessage(host.CoopEpoch, true, (byte)airframe).Write(bytes);
        guest.Send(guest.Peers[0], bytes, NetReliability.Reliable);
        host.Step(0.0);
    }

    /// <summary>A shut door whose LAN search hears <see cref="SampleGames"/>, answered at once
    /// from documentation addresses. With <paramref name="silent"/> nothing answers, so the
    /// games list stands on its Searching box.</summary>
    public static NetPlayFeature Searching(bool silent = false) => new(
        (port, maxGuests, bind) => throw new InvalidOperationException("the aid's search door hosts nothing"),
        (address, port) => throw new InvalidOperationException("the aid's search door joins nothing"),
        lan: (bind, port) => new SampleLan(silent));

    /// <summary>A door joined over the loopback to a host advertising a campaign mission at
    /// <paramref name="missionSeq"/> with <paramref name="players"/> players in it. The advert
    /// has already landed when this returns.</summary>
    public static NetPlayFeature JoinedGuest(int missionSeq, int players) => Joined(missionSeq, players, out _);

    /// <summary>A door joined as <see cref="JoinedGuest"/> that has also heard its host name its
    /// boards as <paramref name="flow"/>, so a guest's campaign follows them. With
    /// <paramref name="ready"/> the guest has answered Ready under that round.</summary>
    public static NetPlayFeature CoopGuest(CoopFlowMessage flow, bool ready)
    {
        var door = Joined(flow.MissionSeq, flow.Humans, out var host);
        Span<byte> bytes = stackalloc byte[CoopFlowMessage.Size];
        flow.Write(bytes);
        host.Send(host.Peers[0], bytes, NetReliability.Reliable);
        door.Step(0.0);
        if (ready)
        {
            door.PickCoop(door.CoopPickAirframe, true);
            door.Step(0.0);
        }

        return door;
    }

    private static NetPlayFeature Joined(int missionSeq, int players, out INetTransport host)
    {
        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(1));
        var lobby = new NetLobby(mesh[0]);
        lobby.Advertise(new SessionAdvertMessage(
            NetSessionKind.CampaignCoop, (byte)missionSeq, (byte)players, HostName));
        var door = new NetPlayFeature(
            (port, maxGuests, bind) => throw new InvalidOperationException("the aid's guest door hosts nothing"),
            (address, port) => mesh[1]);
        door.OpenJoin();
        door.Step(0.0);
        host = mesh[0];
        return door;
    }

    // The aid's LAN: every query is answered by each sample game at once, from its own address.
    private sealed class SampleLan : ILanSocket
    {
        private readonly bool _silent;
        private readonly Queue<(byte[] Bytes, string Address)> _inbox = new();

        public SampleLan(bool silent) => _silent = silent;

        public void Send(string address, int port, ReadOnlySpan<byte> datagram)
        {
            if (_silent || !LanDiscovery.TryReadQuery(datagram, out uint token))
            {
                return;
            }

            foreach (var game in SampleGames)
            {
                byte[] reply = new byte[LanDiscovery.Size];
                LanDiscovery.WriteReply(reply, token, game.Port, game.Advert);
                _inbox.Enqueue((reply, game.Address));
            }
        }

        public byte[]? Receive(out string address, out int port)
        {
            port = LanDiscovery.Port;
            if (_inbox.Count == 0)
            {
                address = "";
                return null;
            }

            var (bytes, from) = _inbox.Dequeue();
            address = from;
            return bytes;
        }

        public void Dispose() => _inbox.Clear();
    }
}
