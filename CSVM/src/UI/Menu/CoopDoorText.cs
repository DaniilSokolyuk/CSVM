using System;
using System.Globalization;
using CSVM.Net;

namespace CSVM.UI.Menu;

/// <summary>
/// The words the campaign's network door is shown in, on both of its ends. A host's campaign
/// boards carry a one-line band naming the open port and the address guests reach it at. A
/// guest's join board names the session a host advertised, and its waiting board says what it is
/// waiting for. Engine-free and built off the door alone, so a presentation draws the same words
/// a unit test reads. The mission's long name is the caller's, since only it holds the langui
/// table.
/// </summary>
public static class CoopDoorText
{
    /// <summary>The waiting board's heading.</summary>
    public const string WaitingHeading = "CAMPAIGN CO-OP";

    /// <summary>The waiting board's one row, which closes the link.</summary>
    public const string LeaveRow = "Leave the session";

    /// <summary>The Network board's last row once a campaign host has answered, in place of the
    /// Dogfight's way on to the map.</summary>
    public const string WaitRow = "Continue → Wait for the host";

    /// <summary>The press that opens and closes the door on a campaign board, as its footers name
    /// it.</summary>
    public const string TogglePress = "L / Y  Network";

    /// <summary>A guest's word when its host closed the session and said so.</summary>
    public const string HostClosed = "Host closed the game";

    /// <summary>A guest's word when its link to the host dropped without a close notice.</summary>
    public const string HostLeft = "Host left the game";

    /// <summary>A guest's word when the host had no seat left for it.</summary>
    public const string GameFull = "The game is full";

    /// <summary>The question a co-op guest's Back asks on the host's boards.</summary>
    public const string LeaveQuestion = "Leave the co-op session?";

    /// <summary>The cabin's door while it is shut.</summary>
    public const string HostCoopButton = "HOST CO-OP";

    /// <summary>The cabin's door while it is open.</summary>
    public const string CloseNetworkButton = "CLOSE NETWORK";

    /// <summary>The games list's Game Name: the host's name and what it holds open.</summary>
    public static string GameName(SessionAdvertMessage advert)
    {
        string what = advert.Kind switch
        {
            NetSessionKind.CampaignCoop => "campaign",
            NetSessionKind.Dogfight => "dogfight",
            _ => "game",
        };
        return advert.Host.Length > 0 ? $"{advert.Host}'s {what}" : Capital(what);
    }

    /// <summary>The games list's # of Players, as "2/4". An advert naming no cap takes its kind's.
    /// </summary>
    public static string PlayerCount(SessionAdvertMessage advert)
    {
        int cap = advert.Cap > 0
            ? advert.Cap
            : advert.Kind == NetSessionKind.CampaignCoop ? NetPlayFeature.CoopHumans : NetSeats.MaxPlayers;
        return $"{advert.Players.ToString(CultureInfo.InvariantCulture)}/{cap.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>The games list's Mission Type.</summary>
    public static string MissionType(SessionAdvertMessage advert) => advert.Kind switch
    {
        NetSessionKind.CampaignCoop => "Campaign co-op",
        NetSessionKind.Dogfight => "Dogfight",
        _ => "Unknown",
    };

    /// <summary>The games list's Mission Environment: a campaign mission's long name through
    /// <paramref name="missionName"/>, or its shortcode, such as "C2/M03", when
    /// <paramref name="fits"/> says the name overflows the column. A Dogfight names its lobby's
    /// environment, and one from a host with no lobby names none.
    /// </summary>
    public static string Environment(SessionAdvertMessage advert, Func<int, string> missionName, Func<string, bool> fits)
    {
        ArgumentNullException.ThrowIfNull(missionName);
        ArgumentNullException.ThrowIfNull(fits);
        if (advert.Kind == NetSessionKind.Dogfight)
        {
            return DogfightLobby.EnvironmentName(advert.MissionSeq);
        }

        if (advert.Kind != NetSessionKind.CampaignCoop || !advert.HasMission)
        {
            return "";
        }

        string name = missionName(advert.MissionSeq);
        return fits(name) ? name : Shortcode(advert);
    }

    /// <summary>A campaign mission's shortcode, chapter and mission, as "C2/M03".</summary>
    public static string Shortcode(SessionAdvertMessage advert) =>
        $"C{advert.Chapter.ToString(CultureInfo.InvariantCulture)}/M{advert.MissionInChapter.ToString("00", CultureInfo.InvariantCulture)}";

    /// <summary>The games list's Status.</summary>
    public static string Status(SessionAdvertMessage advert) => advert.Status switch
    {
        NetSessionStatus.Waiting => "Waiting",
        NetSessionStatus.InMission => "In mission",
        NetSessionStatus.Full => "Full",
        _ => "",
    };

    /// <summary>The games list's Status for a game heard on the LAN. A game of a version this build
    /// does not play with reads as that version, such as "Version 0.7", in place of its status.
    /// </summary>
    public static string Status(LanGame game, NetBuildVersion own) =>
        own.PlaysWith(game.Version) ? Status(game.Advert) : $"Version {game.Version}";

    /// <summary>Why a guest and a host of versions that do not play together were kept apart,
    /// naming both, as "Host runs 0.7, you run 0.6".</summary>
    public static string VersionMismatch(NetBuildVersion host, NetBuildVersion own) =>
        $"Host runs {host}, you run {own}";

    /// <summary>Whether a games list row may be picked: a session this build knows, with a seat.
    /// </summary>
    public static bool Joinable(SessionAdvertMessage advert) =>
        advert.Kind != NetSessionKind.Unknown && advert.Status is NetSessionStatus.Waiting or NetSessionStatus.InMission;

    /// <summary>What an advert names: the kind of session and, for a campaign, the chapter, the
    /// mission within it and the mission's long name through <paramref name="missionName"/>.
    /// </summary>
    public static string SessionName(SessionAdvertMessage advert, Func<int, string> missionName)
    {
        ArgumentNullException.ThrowIfNull(missionName);
        return advert.Kind switch
        {
            NetSessionKind.CampaignCoop when advert.HasMission =>
                $"Campaign co-op, chapter {advert.Chapter.ToString(CultureInfo.InvariantCulture)}, "
                + $"mission {advert.MissionInChapter.ToString(CultureInfo.InvariantCulture)}: {missionName(advert.MissionSeq)}",
            NetSessionKind.CampaignCoop => "Campaign co-op",
            NetSessionKind.Dogfight => "Dogfight",
            _ => "A session this build does not know",
        };
    }

    /// <summary>A player count as a phrase, "1 player" or "3 players".</summary>
    public static string Players(int count) =>
        count == 1 ? "1 player" : $"{count.ToString(CultureInfo.InvariantCulture)} players";

    /// <summary>The Network board's status once a join has landed. It names the session once the
    /// advert arrives.
    /// <paramref name="link"/> is the board's own link readout, appended after the address.
    /// </summary>
    public static string JoinedStatus(NetPlayFeature net, string link, Func<int, string> missionName)
    {
        ArgumentNullException.ThrowIfNull(net);
        string linked = $"Linked to {net.Address}{link}";
        if (net.Advert is not { } advert)
        {
            return net.HostStarted
                ? $"{linked}. The host has started: pick the host's map and fly."
                : $"{linked}. Waiting for the host to start.";
        }

        string session = $"{SessionName(advert, missionName)}, {HostedBy(advert)}{Players(advert.Players)}";
        return advert.Kind == NetSessionKind.CampaignCoop
            ? $"{linked}. {session}. Continue, and wait there for the host's launch."
            : net.HostStarted
                ? $"{linked}. {session}. The host has started: pick the host's map and fly."
                : $"{linked}. {session}. Waiting for the host to start.";
    }

    /// <summary>The waiting board's status line: the mission, the host, the field, and whether
    /// the host has launched yet.</summary>
    public static string WaitingStatus(NetPlayFeature net, Func<int, string> missionName)
    {
        ArgumentNullException.ThrowIfNull(net);
        if (net.Advert is not { } advert)
        {
            return $"Linked to {net.Address}. Waiting for the host to name its session.";
        }

        string state = net.HostStarted
            ? "The host has launched the mission."
            : "Waiting for the host to launch the mission.";
        return $"{SessionName(advert, missionName)}. {Capital(HostedBy(advert))}{Players(advert.Players)} at {net.Address}. {state}";
    }

    /// <summary>A campaign host's band: the port, the address the router reports, and how many
    /// guests are on the wire. Empty while the door is not a campaign host, so a board with the
    /// door shut draws nothing extra.</summary>
    public static string HostBand(NetPlayFeature net)
    {
        ArgumentNullException.ThrowIfNull(net);
        if (!net.IsCoopHost)
        {
            return "";
        }

        string port = net.Port.ToString(CultureInfo.InvariantCulture);
        string where = net.PortMap switch
        {
            { IsMapped: true } map => $"{map.ExternalAddress}:{map.Port.ToString(CultureInfo.InvariantCulture)}",
            { Outcome: UpnpPortMapOutcome.NoPublicAddress } => $"port {port}, LAN only: no public IPv4",
            not null => $"port {port}, this network only",
            null => $"port {port}",
        };
        int guests = net.Peers;
        string joined = guests == 1 ? "1 guest" : $"{guests.ToString(CultureInfo.InvariantCulture)} guests";
        return $"NETWORK OPEN  {where}  {joined}";
    }

    /// <summary>What the router said about a host's port, as a sentence for the door's status
    /// line. Every outcome but a mapping says guests on this network still join.</summary>
    public static string RouterStatus(UpnpPortMapResult map)
    {
        string port = map.Port.ToString(CultureInfo.InvariantCulture);
        string local = "guests on this network still join";
        string kind = IgdAddress.Word(IgdAddress.Kind(map.ExternalAddress));
        return map.Outcome switch
        {
            UpnpPortMapOutcome.Mapped => $"Router mapped port {port}, reachable at {map.ExternalAddress}.",
            UpnpPortMapOutcome.NoPublicAddress =>
                $"The router answered, but this internet line has no public IPv4 address (the router's own, "
                + $"{map.ExternalAddress}, is {kind}). {Capital(local)}; guests outside need IPv6 with port {port} "
                + "opened on the router, or another player hosts.",
            UpnpPortMapOutcome.NoGateway => $"No UPnP router answered, so port {port} is not mapped; {local}.",
            UpnpPortMapOutcome.TimedOut => $"The router did not answer in time, so port {port} is not mapped; {local}.",
            _ => $"The router declined to map port {port}; {local}.",
        };
    }

    /// <summary>A co-op guest's band over the host's boards. It says whose campaign it follows and
    /// what the host is doing, or on the flight check what the guest still owes. Empty while the
    /// door is not a co-op guest's or the host has named no board yet.</summary>
    public static string GuestBand(NetPlayFeature net)
    {
        ArgumentNullException.ThrowIfNull(net);
        if (!net.IsCoopGuest || net.CoopFlow is not { } flow)
        {
            return "";
        }

        string host = net.Advert is { Host.Length: > 0 } advert ? advert.Host : "The host";
        string doing = flow.Screen switch
        {
            NetCoopScreen.Briefing => $"{host} is on the briefing",
            NetCoopScreen.FlightCheck => net.CoopReady
                ? $"Ready, waiting for {host} to launch"
                : "Pick your plane and ammo, then press Ready",
            NetCoopScreen.InMission => $"{host} is in a mission; you fly from the next briefing",
            NetCoopScreen.Debrief => flow.Won ? "Mission won" : "Mission failed",
            _ => $"{host} is in the cabin",
        };
        return $"CO-OP  {doing}";
    }

    /// <summary>What every player is told when a guest's link drops mid-mission.</summary>
    public static string Left(string name) => $"{(name.Length > 0 ? name : "A guest")} left";

    private static string HostedBy(SessionAdvertMessage advert) =>
        advert.Host.Length > 0 ? $"hosted by {advert.Host}, " : "";

    private static string Capital(string text) =>
        text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;
}
