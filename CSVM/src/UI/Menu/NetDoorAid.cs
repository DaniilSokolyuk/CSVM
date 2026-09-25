using System;
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

    /// <summary>A shut door whose host opens onto a loopback wire with <paramref name="guests"/>
    /// peers already on it, and whose router maps any port asked for.</summary>
    public static NetPlayFeature Host(int guests, out Func<int> unmapped)
    {
        var mesh = LoopbackTransport.Mesh(1 + Math.Max(0, guests), LoopbackConditions.Perfect, new Random(1));
        int given = 0;
        unmapped = () => given;
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

    /// <summary>A door joined over the loopback to a host advertising a campaign mission at
    /// <paramref name="missionSeq"/> with <paramref name="players"/> players in it. The advert
    /// has already landed when this returns.</summary>
    public static NetPlayFeature JoinedGuest(int missionSeq, int players)
    {
        var mesh = LoopbackTransport.Mesh(2, LoopbackConditions.Perfect, new Random(1));
        var host = new NetLobby(mesh[0]);
        host.Advertise(new SessionAdvertMessage(
            NetSessionKind.CampaignCoop, (byte)missionSeq, (byte)players, HostName));
        var door = new NetPlayFeature(
            (port, maxGuests, bind) => throw new InvalidOperationException("the aid's guest door hosts nothing"),
            (address, port) => mesh[1]);
        door.OpenJoin();
        door.Step(0.0);
        return door;
    }
}
