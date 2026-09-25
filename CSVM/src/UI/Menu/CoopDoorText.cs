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
        string where = net.PortMap is { } map
            ? map.IsMapped
                ? $"{map.ExternalAddress}:{map.Port.ToString(CultureInfo.InvariantCulture)}"
                : $"port {port}, this network only"
            : $"port {port}";
        int guests = net.Peers;
        string joined = guests == 1 ? "1 guest" : $"{guests.ToString(CultureInfo.InvariantCulture)} guests";
        return $"NETWORK OPEN  {where}  {joined}";
    }

    private static string HostedBy(SessionAdvertMessage advert) =>
        advert.Host.Length > 0 ? $"hosted by {advert.Host}, " : "";

    private static string Capital(string text) =>
        text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;
}
