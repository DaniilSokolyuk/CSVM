using System;
using System.Collections.Generic;
using CSVM.Net;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The multiplayer door off-engine: the two fields a board edits, the four operations, the
/// readouts a board draws, and what a launch takes off it. The carrier is a loopback mesh and the
/// router is a stub, which is the whole point of the feature taking both as delegates.
/// </summary>
public class NetPlayFeatureTests
{
    private static readonly LoopbackConditions Clean = new(0.0, 0.0, 0.0);

    [Fact]
    public void ADoorStartsShutOnItsOwnDefaults()
    {
        var door = Door();

        Assert.Equal(NetDoorStage.Shut, door.Stage);
        Assert.Equal(NetPlayFeature.DefaultPort, door.Port);
        Assert.Equal(NetPlayFeature.DefaultAddress, door.Address);
        Assert.Equal("", door.Fault);
        Assert.Null(door.PortMap);
        Assert.Null(door.Link);
        Assert.False(door.CanLaunch);
        Assert.False(door.IsHost);
        Assert.Equal(0, door.Peers);
    }

    [Fact]
    public void ThePortStepsInsideTheUnprivilegedRangeAndWrapsAtBothEnds()
    {
        var door = Door();

        door.StepPort(1);
        Assert.Equal(NetPlayFeature.DefaultPort + 1, door.Port);

        door.StepPort(-1);
        door.StepPort(-(NetPlayFeature.DefaultPort - 1024));
        Assert.Equal(1024, door.Port);

        door.StepPort(-1);
        Assert.Equal(65535, door.Port);

        door.StepPort(1);
        Assert.Equal(1024, door.Port);
    }

    [Fact]
    public void TheAddressTakesOnlyWhatAnAddressIsWrittenWith()
    {
        var door = Door();
        while (door.Address.Length > 0)
        {
            door.EraseAddress();
        }

        door.TypeAddress("10.0.0.7");
        door.TypeAddress("/");
        door.TypeAddress(" ");
        Assert.Equal("10.0.0.7", door.Address);

        door.EraseAddress();
        Assert.Equal("10.0.0.", door.Address);

        door.TypeAddress(new string('9', NetPlayFeature.AddressLimit));
        Assert.Equal(NetPlayFeature.AddressLimit, door.Address.Length);
    }

    /// <summary>A full IPv6 address, bracketed with a port, is typed character by character and
    /// kept whole. A 48-character cap or a refused bracket would cut it short.</summary>
    [Theory]
    [InlineData("2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90")]
    [InlineData("[2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90]:47500")]
    [InlineData("[ffff:ffff:ffff:ffff:ffff:ffff:255.255.255.255]:65535")]
    public void AFullIpv6AddressIsTypedWhole(string address)
    {
        var door = Door();
        while (door.Address.Length > 0)
        {
            door.EraseAddress();
        }

        int taken = 0;
        foreach (char c in address)
        {
            taken += door.TypeAddress(c.ToString());
        }

        Assert.Equal(address, door.Address);
        Assert.Equal(address.Length, taken);
        Assert.Equal(0, door.TypeAddress("/"));
    }

    /// <summary>A pasted address lands trimmed and whole. A refused character or one past the cap
    /// is left out and reported, for the box's reject cue.</summary>
    [Fact]
    public void APastedAddressIsTrimmedFilteredAndCapped()
    {
        var door = Door();
        while (door.Address.Length > 0)
        {
            door.EraseAddress();
        }

        Assert.Equal((46, false), door.PasteAddress(" \t[2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90]:47500\r\n"));
        Assert.Equal("[2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90]:47500", door.Address);

        while (door.Address.Length > 0)
        {
            door.EraseAddress();
        }

        Assert.Equal((6, true), door.PasteAddress("::1/128"));
        Assert.Equal("::1128", door.Address);

        Assert.Equal((NetPlayFeature.AddressLimit - 6, true), door.PasteAddress(new string('a', 80)));
        Assert.Equal(NetPlayFeature.AddressLimit, door.Address.Length);
        Assert.Equal((0, false), door.PasteAddress(null));
    }

    /// <summary>What a typed address splits into. A bare IPv6 address is all host, so its last
    /// group is never read as a port. A bracketed one names its port after the bracket, and one
    /// colon splits a host from its port. The board's port stands where the address names none.
    /// </summary>
    [Theory]
    [InlineData("2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90", "2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90", 47600)]
    [InlineData("[2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90]:47500", "2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90", 47500)]
    [InlineData("[2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90]", "2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90", 47600)]
    [InlineData("::1", "::1", 47600)]
    [InlineData("[::1]:5000", "::1", 5000)]
    [InlineData("fe80::1%12", "fe80::1%12", 47600)]
    [InlineData("10.0.0.7", "10.0.0.7", 47600)]
    [InlineData("10.0.0.7:5000", "10.0.0.7", 5000)]
    [InlineData("[::1]:99999", "::1", 47600)]
    public void ATypedAddressSplitsIntoTheHostAndPortTheJoinOpensOn(string typed, string host, int port)
    {
        Assert.Equal((host, port), NetPlayFeature.SplitAddress(typed, 47600));
    }

    /// <summary>The join reaches the carrier as the split host and port, and the board's words
    /// write an IPv6 host back bracketed.</summary>
    [Theory]
    [InlineData("2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90", "2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90", 47500, "[2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90]:47500")]
    [InlineData("[2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90]:47600", "2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90", 47600, "[2a04:6ec0:232:6640:feb1:ff80:9ed7:dd90]:47600")]
    [InlineData("10.0.0.7:5000", "10.0.0.7", 5000, "10.0.0.7:5000")]
    public void AJoinOpensOnTheSplitHostAndPort(string typed, string host, int port, string shown)
    {
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(29));
        (string Host, int Port)? asked = null;
        var door = new NetPlayFeature(
            (_, _, _) => mesh[0],
            (address, at) =>
            {
                asked = (address, at);
                return mesh[1];
            });
        while (door.Address.Length > 0)
        {
            door.EraseAddress();
        }

        door.TypeAddress(typed);
        door.OpenJoin();

        Assert.Equal((host, port), asked);
        Assert.Equal(shown, door.JoinTargetText);
    }

    [Fact]
    public void HostingOpensTheSocketAndAsksTheRouterForThePortOnce()
    {
        int asked = 0;
        var mapped = new UpnpPortMapResult(UpnpPortMapOutcome.Mapped, 47500, "203.0.113.9", "mapped");
        var door = new NetPlayFeature(
            (_, _, _) => LoopbackTransport.Mesh(1, Clean, new Random(1))[0],
            (_, _) => throw new InvalidOperationException("a host does not join"),
            port =>
            {
                asked++;
                return mapped with { Port = port };
            },
            _ => { });

        door.OpenHost(7);
        Assert.Equal(NetDoorStage.Hosting, door.Stage);
        Assert.True(door.IsHost);
        Assert.True(door.CanLaunch);

        // The mapping is asked for away from the frame, so it lands on a step rather than inside
        // the open. The wait is a wall-clock deadline. The pool thread running the mapping
        // starves under the parallel unit run, where a fixed step count failed.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (door.PortMap == null && DateTime.UtcNow < deadline)
        {
            door.Step(0.016);
            System.Threading.Thread.Sleep(1);
        }

        Assert.Equal(1, asked);
        Assert.True(door.PortMap!.Value.IsMapped);
        Assert.Equal(NetPlayFeature.DefaultPort, door.PortMap!.Value.Port);
        Assert.Equal("203.0.113.9", door.PortMap!.Value.ExternalAddress);
    }

    [Fact]
    public void ASocketThatWillNotOpenLeavesTheDoorShutWithTheReasonOnIt()
    {
        var door = new NetPlayFeature(
            (port, _, _) => throw new InvalidOperationException($"cannot host on {port}"),
            (address, _) => throw new InvalidOperationException($"cannot join {address}"));

        door.OpenHost(7);
        Assert.Equal(NetDoorStage.Failed, door.Stage);
        Assert.Contains("cannot host", door.Fault, StringComparison.Ordinal);
        Assert.False(door.CanLaunch);
        Assert.Null(door.BuildLaunch());

        door.OpenJoin();
        Assert.Equal(NetDoorStage.Failed, door.Stage);
        Assert.Contains("cannot join", door.Fault, StringComparison.Ordinal);
    }

    [Fact]
    public void AJoinLandsOnAStepAndTheLaunchTakesTheWire()
    {
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(11));
        var door = new NetPlayFeature(
            (_, _, _) => mesh[0],
            (_, _) => mesh[1]);

        door.OpenJoin();
        Assert.Equal(NetDoorStage.Joining, door.Stage);
        Assert.False(door.CanLaunch);

        door.Step(0.016);
        Assert.Equal(NetDoorStage.Joined, door.Stage);
        Assert.Equal(1, door.Peers);
        Assert.True(door.CanLaunch);

        var launch = door.BuildLaunch();
        Assert.NotNull(launch);
        var lobby = Assert.IsType<NetLobby>(launch!.Transport);
        Assert.Same(mesh[1], lobby.Inner);
        Assert.False(lobby.Bound);
        Assert.False(launch.IsHost);
    }

    [Fact]
    public void ACoopHostAdvertisesItsMissionAndCountsItsGuestsOnTheWire()
    {
        var mesh = LoopbackTransport.Mesh(3, Clean, new Random(23));
        var host = new NetPlayFeature((_, _, _) => mesh[0], (_, _) => mesh[0]);
        var guest = new NetPlayFeature((_, _, _) => mesh[1], (_, _) => mesh[1]);

        host.OpenCoopHost(NetSeats.MaxPlayers - 2);
        host.Offer(7, "Zachary", 2);
        host.Step(0.016);
        Assert.True(host.IsCoopHost);
        Assert.Equal(NetSessionKind.CampaignCoop, host.HostKind);

        guest.OpenJoin();
        guest.Step(0.016);
        Assert.True(guest.IsCoopGuest);
        var advert = guest.Advert!.Value;
        Assert.Equal(NetSessionKind.CampaignCoop, advert.Kind);
        Assert.Equal(7, advert.MissionSeq);
        Assert.Equal(2, advert.Chapter);
        Assert.Equal(3, advert.MissionInChapter);
        Assert.Equal("Zachary", advert.Host);

        // Two local seats and the mesh's two other peers.
        Assert.Equal(4, advert.Players);

        // A new mission reaches the guest on the host's next step, and nothing else is resent.
        host.Offer(8, "Zachary", 2);
        host.Step(0.016);
        guest.Step(0.016);
        Assert.Equal(8, guest.Advert!.Value.MissionSeq);
    }

    [Fact]
    public void ADogfightHostAdvertisesADogfightAndClosingForgetsTheCoopKind()
    {
        // A loopback end binds once, so every open takes a fresh one.
        var host = new NetPlayFeature(
            (_, _, _) => LoopbackTransport.Mesh(2, Clean, new Random(29))[0],
            (_, _) => throw new InvalidOperationException("a host does not join"));
        host.OpenCoopHost(7);
        Assert.True(host.IsCoopHost);
        host.Close();

        host.OpenHost(7);
        Assert.False(host.IsCoopHost);
        Assert.Equal(NetSessionKind.Dogfight, host.Advertising!.Value.Kind);
        Assert.False(host.Advertising!.Value.HasMission);
    }

    [Fact]
    public void AGuestLearnsTheHostStartedFromTheAnswerTheLobbyHolds()
    {
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(31));
        var guest = new NetPlayFeature((_, _, _) => mesh[1], (_, _) => mesh[1]);
        guest.OpenJoin();
        guest.Step(0.016);
        Assert.False(guest.HostStarted);

        // The host's session answers every peer on the wire the moment it is built.
        var roster = NetSeats.Field(mesh[0].LocalPeer, new[] { "plane" }, mesh[0].Peers, "plane");
        _ = NetSession.Host(mesh[0], roster, seed: 5);
        guest.Step(0.016);
        Assert.True(guest.HostStarted);
    }

    [Fact]
    public void ABuiltInHostRunsAnUnshownLobbyThatAnOriginalGuestPicksAgainst()
    {
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(37));
        var host = new NetPlayFeature((_, _, _) => mesh[0], (_, _) => mesh[0]);
        var guest = new NetPlayFeature((_, _, _) => mesh[1], (_, _) => mesh[1]);
        host.OpenHost(NetSeats.MaxPlayers - 1);
        guest.OpenJoin();
        Pump(host, guest);

        Assert.True(host.Dogfight is { IsHost: true, Shown: false });
        Assert.False(host.Advertising!.Value.HasMission);
        Assert.True(guest.Dogfight is { HasOptions: true });

        guest.Dogfight!.Show();
        guest.Dogfight.Pick(2, default);
        Pump(host, guest);
        Assert.Equal(2, host.Dogfight!.Players[1].Airframe);
    }

    [Fact]
    public void AGuestBackFromAMatchTakesNoTailForALaunchUntilTheHostNamesANewRound()
    {
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(41));
        var host = new NetPlayFeature((_, _, _) => mesh[0], (_, _) => mesh[0]);
        var guest = new NetPlayFeature((_, _, _) => mesh[1], (_, _) => mesh[1]);
        host.OpenDogfightHost(NetSeats.MaxPlayers - 1);
        guest.OpenJoin();
        Pump(host, guest);
        guest.Dogfight!.Show();
        guest.Dogfight.SetReady(true);
        host.Dogfight!.SetReady(true);
        Pump(host, guest);
        Assert.True(host.Dogfight.CanLaunch);

        // One session payload stands in for the host's opener, and then for its match's tail.
        Assert.NotNull(host.BuildLaunch());
        SessionPayload(mesh);
        guest.Step(0.016);
        Assert.True(guest.DogfightLaunchDue);
        Assert.NotNull(guest.BuildLaunch());
        Assert.True(guest.Reclaim());
        SessionPayload(mesh);
        guest.Step(0.016);
        Assert.False(guest.DogfightLaunchDue);

        // ABLE-TO-FAIL CONTROL: once the host lands and names a new round, a payload is a launch.
        Assert.True(host.Reclaim());
        host.Dogfight.Land(Array.Empty<DogfightScore>());
        Pump(host, guest);
        guest.Dogfight.SetReady(true);
        SessionPayload(mesh);
        guest.Step(0.016);
        Assert.True(guest.DogfightLaunchDue);
    }

    [Fact]
    public void AJoinNobodyAnswersGivesUpAndSaysSo()
    {
        var mesh = LoopbackTransport.Mesh(1, Clean, new Random(3));
        var door = new NetPlayFeature((_, _, _) => mesh[0], (_, _) => mesh[0]);

        door.OpenJoin();
        for (int i = 0; i < 10; i++)
        {
            door.Step(NetPlayFeature.JoinTimeoutSeconds / 8.0);
        }

        Assert.Equal(NetDoorStage.Failed, door.Stage);
        Assert.Contains("did not answer", door.Fault, StringComparison.Ordinal);
    }

    [Fact]
    public void ClosingGivesTheRoutersPortBackWhetherOrNotALaunchTookTheWire()
    {
        var given = new List<int>();
        var mesh = LoopbackTransport.Mesh(1, Clean, new Random(5));
        var door = new NetPlayFeature(
            (_, _, _) => mesh[0],
            (_, _) => mesh[0],
            port => new UpnpPortMapResult(UpnpPortMapOutcome.Mapped, port, "198.51.100.4", "mapped"),
            given.Add);

        door.StepPort(3);
        door.OpenHost(7);
        Assert.NotNull(door.BuildLaunch());

        door.Close();
        Assert.Equal(NetDoorStage.Shut, door.Stage);
        Assert.Equal(new[] { NetPlayFeature.DefaultPort + 3 }, given);

        // The port and the address are the player's, not the socket's, so they survive the close.
        Assert.Equal(NetPlayFeature.DefaultPort + 3, door.Port);
    }

    [Fact]
    public void AnOpenDoorRefusesToEditTheFieldsUnderIt()
    {
        var mesh = LoopbackTransport.Mesh(1, Clean, new Random(7));
        var door = new NetPlayFeature((_, _, _) => mesh[0], (_, _) => mesh[0]);
        door.OpenHost(7);

        door.StepPort(5);
        door.TypeAddress("9");
        door.EraseAddress();

        Assert.Equal(NetPlayFeature.DefaultPort, door.Port);
        Assert.Equal(NetPlayFeature.DefaultAddress, door.Address);
    }

    [Fact]
    public void DiscardingTakesTheDoorBackToShutWithNoFaultLeftOnIt()
    {
        var door = new NetPlayFeature(
            (_, _, _) => throw new InvalidOperationException("no"),
            (_, _) => throw new InvalidOperationException("no"));
        door.OpenHost(7);
        Assert.Equal(NetDoorStage.Failed, door.Stage);

        door.Discard();
        Assert.Equal(NetDoorStage.Shut, door.Stage);
        Assert.Equal("", door.Fault);
    }

    [Fact]
    public void ACoopHostSeatsFourHumansAndTellsTheFifthTheGameIsFull()
    {
        var mesh = LoopbackTransport.Mesh(5, Clean, new Random(41));
        var host = new NetPlayFeature((_, _, _) => mesh[0], (_, _) => mesh[0]);
        host.OpenCoopHost(NetSeats.MaxPlayers - 1);
        host.Offer(3, "Zachary", 1);
        var guests = new List<NetPlayFeature>();
        for (int i = 1; i < mesh.Count; i++)
        {
            int end = i;
            var guest = new NetPlayFeature((_, _, _) => mesh[end], (_, _) => mesh[end]);
            guest.OpenJoin();
            guests.Add(guest);
        }

        for (int frame = 0; frame < 4; frame++)
        {
            host.Step(0.016);
            guests.ForEach(guest => guest.Step(0.016));
        }

        Assert.Equal(3, host.Peers);
        Assert.Equal(NetSessionStatus.Full, host.Advertising!.Value.Status);
        Assert.Equal(4, host.Advertising!.Value.Players);
        var refused = guests.FindAll(guest => guest.Stage == NetDoorStage.Failed);
        Assert.Single(refused);
        Assert.Equal(CoopDoorText.GameFull, refused[0].Fault);
        Assert.Equal(3, guests.FindAll(guest => guest.Stage == NetDoorStage.Joined).Count);
    }

    [Fact]
    public void AHostRefusesAGuestOfAnotherMinorWithBothVersionsAndSeatsOneAPatchApart()
    {
        // The loopback links every end to every other, and a real guest links only to its host.
        var mesh = LoopbackTransport.Mesh(3, Clean, new Random(53));
        mesh[1].Disconnect(mesh[2].LocalPeer);
        var host = new NetPlayFeature((_, _, _) => mesh[0], (_, _) => mesh[0]) { Version = NetBuildVersion.Parse("0.7.0") };
        var patched = new NetPlayFeature((_, _, _) => mesh[1], (_, _) => mesh[1]) { Version = NetBuildVersion.Parse("0.7.4") };
        var older = new NetPlayFeature((_, _, _) => mesh[2], (_, _) => mesh[2]) { Version = NetBuildVersion.Parse("0.6.9") };
        host.OpenCoopHost(NetSeats.MaxPlayers - 1);
        host.Offer(3, "Zachary", 1);
        patched.OpenJoin();
        older.OpenJoin();
        for (int frame = 0; frame < 4; frame++)
        {
            host.Step(0.016);
            patched.Step(0.016);
            older.Step(0.016);
        }

        Assert.Equal(NetDoorStage.Failed, older.Stage);
        Assert.Equal("Host runs 0.7, you run 0.6", older.Fault);
        Assert.Equal(1, host.Peers);
        Assert.Equal(2, host.Advertising!.Value.Players);

        // ABLE-TO-FAIL CONTROL: a guest a patch apart joins the same host.
        Assert.Equal(NetDoorStage.Joined, patched.Stage);
        Assert.Equal("", patched.Fault);

        // The refused guest is off the host's carrier once the grace has passed.
        host.Step(NetPlayFeature.RefuseGraceSeconds + 0.1);
        host.Step(0.016);
        Assert.Equal(new[] { mesh[1].LocalPeer }, mesh[0].Peers);
    }

    [Fact]
    public void AHostsRefusalNamesBothVersionsAndAGuestRefusesASilentHostOfAnotherVersionItself()
    {
        // The host's close notice is what the guest reads when it arrives first.
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(59));
        var host = new NetPlayFeature((_, _, _) => mesh[0], (_, _) => mesh[0]) { Version = new NetBuildVersion(0, 6) };
        var guest = new NetPlayFeature((_, _, _) => mesh[1], (_, _) => mesh[1]) { Version = new NetBuildVersion(0, 7) };
        host.OpenDogfightHost(NetSeats.MaxPlayers - 1);
        guest.OpenJoin();
        for (int frame = 0; frame < 3; frame++)
        {
            host.Step(0.016);
            guest.Step(0.016);
        }

        Assert.Equal("Host runs 0.6, you run 0.7", guest.Fault);
        Assert.Equal(0, host.Peers);

        // A lobby that names its version and never steps a door refuses nobody, so the guest's
        // own check is the one that fires.
        var silent = LoopbackTransport.Mesh(2, Clean, new Random(61));
        using var lobby = new NetLobby(silent[0], new NetBuildVersion(0, 8));
        var alone = new NetPlayFeature((_, _, _) => silent[1], (_, _) => silent[1]) { Version = new NetBuildVersion(0, 7) };
        alone.OpenJoin();
        for (int frame = 0; frame < 3; frame++)
        {
            lobby.Step(0.016);
            alone.Step(0.016);
        }

        Assert.Null(lobby.Closed);
        Assert.Equal(NetDoorStage.Failed, alone.Stage);
        Assert.Equal("Host runs 0.8, you run 0.7", alone.Fault);

        // ABLE-TO-FAIL CONTROL: two doors that never set a version are both unknown and play.
        var plain = LoopbackTransport.Mesh(2, Clean, new Random(67));
        var plainHost = new NetPlayFeature((_, _, _) => plain[0], (_, _) => plain[0]);
        var plainGuest = new NetPlayFeature((_, _, _) => plain[1], (_, _) => plain[1]);
        plainHost.OpenDogfightHost(NetSeats.MaxPlayers - 1);
        plainGuest.OpenJoin();
        for (int frame = 0; frame < 3; frame++)
        {
            plainHost.Step(0.016);
            plainGuest.Step(0.016);
        }

        Assert.Equal(NetDoorStage.Joined, plainGuest.Stage);
        Assert.Equal(1, plainHost.Peers);
    }

    [Fact]
    public void JoiningAListedGameOfAnotherVersionIsRefusedBeforeAnySocketOpens()
    {
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(71));
        int opened = 0;
        var door = new NetPlayFeature(
            (_, _, _) => mesh[0],
            (_, _) =>
            {
                opened++;
                return mesh[1];
            })
        { Version = new NetBuildVersion(0, 6) };
        var advert = new SessionAdvertMessage(NetSessionKind.Dogfight, 0, 1, "Oskar");

        door.JoinGame(new LanGame("127.0.0.1", 47500, advert, new NetBuildVersion(0, 7)));
        Assert.Equal(0, opened);
        Assert.Equal(NetDoorStage.Failed, door.Stage);
        Assert.Equal("Host runs 0.7, you run 0.6", door.Fault);
        Assert.False(door.PlaysWith(new LanGame("127.0.0.1", 47500, advert, new NetBuildVersion(0, 7))));

        // ABLE-TO-FAIL CONTROL: the same game at this build's version opens the join.
        door.Discard();
        door.JoinGame(new LanGame("127.0.0.1", 47500, advert, new NetBuildVersion(0, 6)));
        Assert.Equal(1, opened);
        Assert.Equal(NetDoorStage.Joining, door.Stage);
    }

    [Fact]
    public void AGuestTellsAHostThatClosedFromALinkThatDropped()
    {
        var mesh = LoopbackTransport.Mesh(3, Clean, new Random(43));
        var host = new NetPlayFeature((_, _, _) => mesh[0], (_, _) => mesh[0]);
        var told = new NetPlayFeature((_, _, _) => mesh[1], (_, _) => mesh[1]);
        var dropped = new NetPlayFeature((_, _, _) => mesh[2], (_, _) => mesh[2]);
        host.OpenCoopHost(NetSeats.MaxPlayers - 1);
        told.OpenJoin();
        dropped.OpenJoin();
        host.Step(0.016);
        told.Step(0.016);
        dropped.Step(0.016);
        Assert.Equal(NetDoorStage.Joined, told.Stage);
        Assert.Equal(NetDoorStage.Joined, dropped.Stage);

        // ABLE-TO-FAIL CONTROL: a link cut with no notice on it reads as the host leaving.
        mesh[0].Disconnect(mesh[2].LocalPeer);
        dropped.Step(0.016);
        Assert.Equal(CoopDoorText.HostLeft, dropped.Fault);

        host.Close();
        host.Step(0.016);
        told.Step(0.016);
        Assert.Equal(NetDoorStage.Failed, told.Stage);
        Assert.Equal(CoopDoorText.HostClosed, told.Fault);
    }

    [Fact]
    public void ASearchHearsAnOpenDoorOnTheLanAndJoinsItWhereItAnswered()
    {
        var lan = new LoopbackLan();
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(47));
        var host = new NetPlayFeature((_, _, _) => mesh[0], (_, _) => mesh[0], lan: lan.Bind);
        var guest = new NetPlayFeature((_, _, _) => mesh[1], (_, _) => mesh[1], lan: lan.Bind)
        {
            BindAddress = "127.0.0.2",
        };
        Assert.True(guest.CanSearch);

        // A shut door answers nobody.
        guest.Search();
        host.Step(0.016);
        guest.Step(0.016);
        Assert.Empty(guest.Games);

        host.OpenCoopHost(NetSeats.MaxPlayers - 1);
        host.Offer(7, "Zachary", 1);
        Assert.True(host.Answering);
        guest.Search();
        host.Step(0.016);
        guest.Step(0.016);
        var game = Assert.Single(guest.Games);
        Assert.Equal("127.0.0.1", game.Address);
        Assert.Equal(NetPlayFeature.DefaultPort, game.Port);
        Assert.Equal("Zachary", game.Advert.Host);
        Assert.Equal(NetSessionKind.CampaignCoop, game.Advert.Kind);

        guest.JoinGame(game);
        host.Step(0.016);
        guest.Step(0.016);
        Assert.Equal(NetDoorStage.Joined, guest.Stage);

        host.Close();
        Assert.False(host.Answering);
        guest.StopSearch();
        Assert.False(guest.Searching);
        Assert.Empty(guest.Games);
    }

    [Fact]
    public void ADoorWithNoLanNeitherSearchesNorAnswers()
    {
        var door = Door();
        Assert.False(door.CanSearch);
        door.Search();
        Assert.False(door.Searching);
        door.OpenCoopHost(3);
        Assert.False(door.Answering);
    }

    [Fact]
    public void AGuestsReadyCountsOnlyUnderTheHostsCurrentRoundOfPicks()
    {
        var (host, guest) = CoopPair(53);
        host.ShowCoop(NetCoopScreen.FlightCheck, 3, 2, 0b10_0000);
        Pump(host, guest);

        // ABLE-TO-FAIL CONTROL: a seated guest that has not said Ready holds the launch.
        Assert.False(host.CoopAllReady);
        Assert.False(Assert.Single(host.CoopGuests).Ready);

        guest.PickCoop(5, true);
        Pump(host, guest);
        var seated = Assert.Single(host.CoopGuests);
        Assert.True(host.CoopAllReady);
        Assert.Equal(1, seated.Slot);
        Assert.Equal(5, seated.Airframe);
        Assert.True(guest.CoopReady);
        Assert.True(guest.CoopFlow!.Value.IsReady(1));

        // Between the briefing and the flight check the round stands, and so does the Ready.
        byte round = host.CoopEpoch;
        host.ShowCoop(NetCoopScreen.Briefing, 3, 2, 0b10_0000);
        Assert.Equal(round, host.CoopEpoch);
        Assert.True(host.CoopAllReady);

        // Backing out to the cabin starts a new round: the pick already there is stale at once.
        host.ShowCoop(NetCoopScreen.Cabin, 3, 2, 0b10_0000);
        Assert.NotEqual(round, host.CoopEpoch);
        Assert.False(host.CoopAllReady);
        Pump(host, guest);
        Assert.False(guest.CoopPickReady);
        Assert.False(guest.CoopReady);

        // A new mission clears it the same way.
        host.ShowCoop(NetCoopScreen.Briefing, 3, 2, 0b10_0000);
        Pump(host, guest);
        guest.PickCoop(5, true);
        Pump(host, guest);
        Assert.True(host.CoopAllReady);
        host.ShowCoop(NetCoopScreen.Briefing, 4, 2, 0b10_0000);
        Assert.False(host.CoopAllReady);
    }

    [Fact]
    public void APickTheHostsHangarDoesNotHoldFliesTheStarter()
    {
        var (host, guest) = CoopPair(59);
        host.ShowCoop(NetCoopScreen.FlightCheck, 3, 2, 0b1010_0000);
        guest.PickCoop(9, true);
        Pump(host, guest);
        Assert.Equal(NetPlayFeature.StarterAirframe, Assert.Single(host.CoopGuests).Airframe);

        // ABLE-TO-FAIL CONTROL: an airframe the hangar holds is flown as picked.
        guest.PickCoop(7, true);
        Pump(host, guest);
        Assert.Equal(7, Assert.Single(host.CoopGuests).Airframe);
    }

    [Fact]
    public void ACoopFlightIsAdvertisedInMissionAndAGuestLaunchesOnlyIntoANewOne()
    {
        var (host, guest) = CoopPair(61);
        host.ShowCoop(NetCoopScreen.FlightCheck, 3, 2, 0b10_0000);
        guest.PickCoop(5, true);
        Pump(host, guest);

        // ABLE-TO-FAIL CONTROL: a door on its boards is not in a mission.
        Assert.NotEqual(NetSessionStatus.InMission, host.Advertising!.Value.Status);
        Assert.False(guest.CoopLaunchDue);

        var launch = host.BuildLaunch()!;
        var roster = NetSeats.Field(launch.Transport.LocalPeer, new[] { "plane" }, launch.Transport.Peers, "plane");
        var session = NetSession.Host(launch.Transport, roster, seed: 5);
        Assert.Equal(NetCoopScreen.InMission, host.CoopScreen);
        host.Step(0.016);
        Assert.Equal(NetSessionStatus.InMission, host.Advertising!.Value.Status);

        guest.Step(0.016);
        Assert.True(guest.CoopLaunchDue);
        Assert.NotNull(guest.BuildLaunch());
        Assert.False(guest.CoopLaunchDue);

        // Back from the flight while the host still flies it: what trails in opens nothing.
        Assert.True(guest.Reclaim());
        session.Broadcast(new ClockPingMessage(1f, 2f), NetChannels.Events);
        guest.Step(0.016);
        Assert.False(guest.CoopLaunchDue);

        // The host's next flight comes after a debrief, and that one seats the guest again.
        Assert.True(host.Reclaim());
        host.ShowCoop(NetCoopScreen.Debrief, 3, 3, 0b10_0000);
        Pump(host, guest);
        Assert.NotEqual(NetSessionStatus.InMission, host.Advertising!.Value.Status);
        host.ShowCoop(NetCoopScreen.InMission, 3, 3, 0b10_0000);
        Pump(host, guest);
        _ = NetSession.Host(launch.Transport, roster, seed: 6);
        guest.Step(0.016);
        Assert.True(guest.CoopLaunchDue);
    }

    [Fact]
    public void AGuestsPickCarriesItsFitAndNameAndTheHostsLaunchTellsItEverySeatsFit()
    {
        var (host, guest) = CoopPair(67);
        host.ShowCoop(NetCoopScreen.FlightCheck, 3, 2, 0b10_0000);
        var fit = CoopFit.Of(new[] { 3, 1 }, new[] { 0, 7 });
        guest.PickCoop(5, true, fit);
        Pump(host, guest);

        // ABLE-TO-FAIL CONTROL: a guest with no player name is seated with none.
        Assert.Equal(string.Empty, Assert.Single(host.CoopGuests).Name);
        guest.PlayerName = "Lucy";
        Pump(host, guest);
        var seated = Assert.Single(host.CoopGuests);
        Assert.Equal(fit, seated.Fit);
        Assert.Equal("Lucy", seated.Name);

        // ABLE-TO-FAIL CONTROL: nothing names a seat's fit to the guest before the launch.
        Assert.Empty(guest.CoopSeatFits);
        var launch = host.BuildLaunch()!;
        var hostFit = CoopFit.Of(new[] { 2 }, null);
        host.TellSeatFits(new[] { hostFit, seated.Fit });
        launch.Transport.Step(0.016);
        guest.Step(0.016);
        Assert.Equal(hostFit, guest.CoopSeatFits[0]);
        Assert.Equal(fit, guest.CoopSeatFits[1]);
    }

    [Fact]
    public void AGuestWalkingOutOfItsFlightIsHeardAtOnceAndOnlyForThatFlight()
    {
        var (host, guest) = CoopPair(71);
        host.ShowCoop(NetCoopScreen.FlightCheck, 3, 2, 0b10_0000);
        guest.PickCoop(5, true);
        Pump(host, guest);

        // ABLE-TO-FAIL CONTROL: a guest on its boards has no flight to leave.
        guest.LeaveCoopMission();
        Pump(host, guest);
        Assert.False(Assert.Single(host.CoopGuests).Left);

        var launch = host.BuildLaunch()!;
        for (int frame = 0; frame < 3; frame++)
        {
            launch.Transport.Step(0.016);
            host.Step(0.016);
            guest.Step(0.016);
        }

        Assert.False(Assert.Single(host.CoopGuests).Left);
        guest.LeaveCoopMission();
        launch.Transport.Step(0.016);
        host.Step(0.016);
        Assert.True(Assert.Single(host.CoopGuests).Left);

        // The mark belongs to the flight: the host's debrief is a new round and the guest is back.
        Assert.True(host.Reclaim());
        host.ShowCoop(NetCoopScreen.Debrief, 3, 3, 0b10_0000);
        Assert.False(Assert.Single(host.CoopGuests).Left);
        Pump(host, guest);
        host.ShowCoop(NetCoopScreen.InMission, 3, 3, 0b10_0000);
        Pump(host, guest);
        Assert.False(Assert.Single(host.CoopGuests).Left);
    }

    // A co-op host with one local seat and one guest seated behind it, both on their boards.
    private static (NetPlayFeature Host, NetPlayFeature Guest) CoopPair(int seed)
    {
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(seed));
        var host = new NetPlayFeature((_, _, _) => mesh[0], (_, _) => mesh[0]);
        var guest = new NetPlayFeature((_, _, _) => mesh[1], (_, _) => mesh[1]);
        host.OpenCoopHost(NetSeats.MaxPlayers - 1);
        host.Offer(3, "Zachary", 1);
        guest.OpenJoin();
        Pump(host, guest);
        Assert.True(guest.IsCoopGuest);
        return (host, guest);
    }

    // A payload no lobby reads, sent host to guest, which the guest's lobby holds for a session.
    private static void SessionPayload(IReadOnlyList<LoopbackTransport> mesh) =>
        mesh[0].Send(mesh[1].LocalPeer, new byte[] { 0xEE, 1, 2, 3 }, NetReliability.Reliable);

    private static void Pump(NetPlayFeature host, NetPlayFeature guest)
    {
        for (int frame = 0; frame < 3; frame++)
        {
            host.Step(0.016);
            guest.Step(0.016);
        }
    }

    // A door over a one-peer mesh with no router behind it, which is every case that does not
    // care what the carrier does.
    private static NetPlayFeature Door()
    {
        var mesh = LoopbackTransport.Mesh(1, Clean, new Random(17));
        return new NetPlayFeature((_, _, _) => mesh[0], (_, _) => mesh[0]);
    }
}
