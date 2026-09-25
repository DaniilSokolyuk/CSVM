using System;
using CSVM.Net;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The campaign network door's words: what an advert names, the host's band, and the two guest
/// readouts. They are built off loopback doors, so they are the words a board draws.
/// </summary>
public class CoopDoorTextTests
{
    private static readonly LoopbackConditions Clean = new(0.0, 0.0, 0.0);

    [Fact]
    public void AnAdvertNamesTheKindAndACampaignItsChapterMissionAndName()
    {
        var coop = new SessionAdvertMessage(NetSessionKind.CampaignCoop, 7, 3, "Zachary");
        Assert.Equal("Campaign co-op, chapter 2, mission 3: M8",
            CoopDoorText.SessionName(coop, seq => $"M{seq + 1}"));
        Assert.Equal("Dogfight", CoopDoorText.SessionName(
            new SessionAdvertMessage(NetSessionKind.Dogfight, SessionAdvertMessage.NoMission, 2, ""), _ => "x"));
        Assert.Equal("1 player", CoopDoorText.Players(1));
        Assert.Equal("16 players", CoopDoorText.Players(16));
    }

    [Fact]
    public void TheHostBandNamesTheMappedAddressAndTheGuestsOnlyWhileHostingACampaign()
    {
        var mesh = LoopbackTransport.Mesh(3, Clean, new Random(2));
        var door = new NetPlayFeature(
            (_, _, _) => mesh[0],
            (_, _) => mesh[0],
            port => new UpnpPortMapResult(UpnpPortMapOutcome.Mapped, port, "203.0.113.24", "mapped"),
            _ => { });
        Assert.Equal("", CoopDoorText.HostBand(door));

        door.OpenCoopHost(14);
        Assert.Equal($"NETWORK OPEN  port {NetPlayFeature.DefaultPort}  2 guests", CoopDoorText.HostBand(door));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (door.PortMap == null && DateTime.UtcNow < deadline)
        {
            door.Step(0.016);
            System.Threading.Thread.Sleep(1);
        }

        Assert.Equal($"NETWORK OPEN  203.0.113.24:{NetPlayFeature.DefaultPort}  2 guests", CoopDoorText.HostBand(door));

        // ABLE-TO-FAIL CONTROL: a Dogfight host opened from the Multiplayer board draws no band.
        door.Close();
        door.OpenHost(14);
        Assert.Equal("", CoopDoorText.HostBand(door));
    }

    [Fact]
    public void AJoinedGuestNamesTheCampaignSessionAndItsWaitingBoardSaysWhatItWaitsFor()
    {
        var door = NetDoorAid.JoinedGuest(missionSeq: 7, players: 3);
        Assert.True(door.IsCoopGuest);

        string joined = CoopDoorText.JoinedStatus(door, "", seq => $"M{seq + 1}");
        Assert.Contains("Campaign co-op, chapter 2, mission 3: M8", joined, StringComparison.Ordinal);
        Assert.Contains("hosted by Zachary", joined, StringComparison.Ordinal);
        Assert.Contains("3 players", joined, StringComparison.Ordinal);

        string waiting = CoopDoorText.WaitingStatus(door, seq => $"M{seq + 1}");
        Assert.Contains("Hosted by Zachary", waiting, StringComparison.Ordinal);
        Assert.EndsWith("Waiting for the host to launch the mission.", waiting, StringComparison.Ordinal);
    }
}
