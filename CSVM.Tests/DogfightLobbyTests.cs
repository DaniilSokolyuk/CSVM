using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Net;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Dogfight lobby over a loopback mesh. The host owns the options and a greyed option cannot be
/// set. Launch waits for every pilot, and a pilot who leaves drops out of the count. A guest's pick
/// and chat reach the host.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class DogfightLobbyTests
{
    private static readonly LoopbackConditions Clean = new(0.0, 0.0, 0.0);

    // Each host lobby's sockets, the host's first, so a settle steps every end.
    private static readonly Dictionary<DogfightLobby, List<NetLobby>> Steps = new();

    [Fact]
    public void AGuestsOptionSetIsRefusedAndItReadsTheHostsOptions()
    {
        var (host, guests, _) = Lobbies(2);
        var guest = guests[0];

        Assert.False(guest.SetEnvironment(3));
        Assert.False(guest.SetTimeMinutes(5));
        Assert.False(guest.SetLimitedLives(true));

        // ABLE-TO-FAIL CONTROL: the same calls on the host take, and the guest reads them.
        Assert.True(host.SetEnvironment(3));
        Assert.True(host.SetTimeMinutes(5));
        Assert.True(host.SetLimitedLives(true));
        Settle(host, guests);
        Assert.True(guest.HasOptions);
        Assert.Equal(3, guest.Options.Environment);
        Assert.Equal(5, guest.Options.TimeMinutes);
        Assert.True(guest.Options.LimitedLives);
    }

    [Fact]
    public void AGreyedOptionCannotBeSet()
    {
        var (host, _, _) = Lobbies(1);

        Assert.False(host.SetMissionType(DogfightMissionType.CaptureTheFlag));
        Assert.False(host.SetMissionType(DogfightMissionType.ZeppelinVsZeppelin));
        Assert.False(host.SetAllowCustomPlanes(true));
        Assert.False(host.SetOutlawComponents(true));
        Assert.Equal((byte)DogfightMissionType.Deathmatch, host.Options.MissionType);

        // ABLE-TO-FAIL CONTROL: the live type is accepted.
        Assert.True(host.SetMissionType(DogfightMissionType.Deathmatch));
    }

    [Fact]
    public void LaunchIsRefusedUntilEveryPilotIsReadyAndAnOptionChangeClearsTheMarks()
    {
        var (host, guests, _) = Lobbies(3);
        Settle(host, guests);
        Assert.False(host.CanLaunch);

        host.SetReady(true);
        guests[0].SetReady(true);
        Settle(host, guests);
        Assert.False(host.CanLaunch);
        Assert.Equal(new[] { true, true, false }, host.Players.Select(p => p.Ready));

        // ABLE-TO-FAIL CONTROL: the last mark lets the host launch, and a guest never can.
        guests[1].SetReady(true);
        Settle(host, guests);
        Assert.True(host.CanLaunch);
        Assert.False(guests[0].CanLaunch);
        Assert.True(guests[0].AllReady);

        // A new round from the host clears every mark, its own included.
        Assert.True(host.SetScore(12));
        Settle(host, guests);
        Assert.False(host.Ready);
        Assert.False(guests[0].Ready);
        Assert.False(host.CanLaunch);
    }

    [Fact]
    public void ADisconnectDropsThePeerOutOfTheCount()
    {
        var (host, guests, mesh) = Lobbies(3);
        Settle(host, guests);
        host.SetReady(true);
        guests[0].SetReady(true);
        Settle(host, guests);
        Assert.Equal(3, host.Players.Count);
        Assert.False(host.CanLaunch);

        mesh[0].Disconnect(2);
        Settle(host, guests);

        // The unready pilot left, so the two still here are every pilot.
        Assert.Equal(2, host.Players.Count);
        Assert.Equal(2, guests[0].Players.Count);
        Assert.True(host.CanLaunch);
    }

    [Fact]
    public void AGuestsPickReachesTheHostsListUnderItsName()
    {
        var (host, guests, _) = Lobbies(2, "Lucy");
        var fit = CoopFit.Of(new[] { 2, 1 }, new[] { 0, 3 });
        Assert.True(guests[0].Pick(1, fit));
        Assert.False(guests[0].Pick(DogfightLobby.AirframeCount, default));
        Settle(host, guests);

        Assert.Equal("Lucy", host.Players[1].Name);
        Assert.Equal(1, host.Players[1].Airframe);
        Assert.Equal(1, guests[0].You);
        Assert.Equal("Lucy", guests[0].Players[guests[0].You].Name);
    }

    [Fact]
    public void AReadyGuestsChangedPickReachesTheHostAndClearsItsReady()
    {
        var (host, guests, _) = Lobbies(2);
        Settle(host, guests);
        guests[0].SetReady(true);
        Settle(host, guests);
        Assert.True(host.Players[1].Ready);

        // Control: the same pick again is no change, so the guest stays Ready on both ends.
        Assert.True(guests[0].Pick(guests[0].Airframe, guests[0].Fit));
        Settle(host, guests);
        Assert.True(guests[0].Ready);
        Assert.True(host.Players[1].Ready);

        int other = (guests[0].Airframe + 1) % DogfightLobby.AirframeCount;
        Assert.True(guests[0].Pick(other, default));
        Settle(host, guests);

        Assert.False(guests[0].Ready);
        Assert.False(host.Players[1].Ready);
        Assert.Equal(other, host.Players[1].Airframe);
    }

    [Fact]
    public void AGuestNoLobbyScreenShowsSendsNoPick()
    {
        var mesh = LoopbackTransport.Mesh(2, Clean, new Random(7));
        var hostWire = new NetLobby(mesh[0]);
        var guestWire = new NetLobby(mesh[1]);
        var host = new DogfightLobby(hostWire, () => "Host");
        var guest = new DogfightLobby(guestWire, () => "Lucy", 0);
        Steps[host] = new List<NetLobby> { hostWire, guestWire };
        var guests = new List<DogfightLobby> { guest };
        guest.Pick(3, default);
        Settle(host, guests);
        Assert.Empty(hostWire.Picks);

        // ABLE-TO-FAIL CONTROL: once a lobby screen stands on it, the same guest's pick arrives.
        guest.Show();
        Settle(host, guests);
        Assert.Equal(3, host.Players[1].Airframe);
        Assert.Equal("Lucy", host.Players[1].Name);
    }

    [Fact]
    public void ABuiltInLaunchWaitsForAPickedGuestsReadyAndWritesItsMapAndRules()
    {
        var (host, guests, _) = Lobbies(2);
        Settle(host, guests);
        var rules = new VersusRules(0, 7, 2, false);

        // ABLE-TO-FAIL CONTROL: with no picked guest an unlisted map still goes, as it always has.
        var (alone, _, _) = Lobbies(1);
        Assert.Null(alone.CheckBuiltInLaunch("C1C", rules));

        Assert.Equal(DogfightLobby.GuestsNotReady, host.CheckBuiltInLaunch("C5", rules));
        guests[0].SetReady(true);
        Settle(host, guests);
        Assert.Equal(DogfightLobby.MapUnlisted, host.CheckBuiltInLaunch("C1C", rules));
        Assert.Null(host.CheckBuiltInLaunch("C5", rules));
        Settle(host, guests);

        var heard = guests[0].Options;
        Assert.Equal(DogfightLobby.EnvironmentOf("C5"), heard.Environment);
        Assert.Equal(DogfightVictory.Time, heard.Victory);
        Assert.Equal(7, heard.TimeMinutes);
        Assert.True(heard.LimitedLives);
        Assert.Equal(2, heard.Lives);
        Assert.False(heard.AutoRespawn);
        Assert.True(guests[0].Ready);
    }

    [Fact]
    public void TheScoresNameEachSeatOffTheLaunchListBestFirst()
    {
        var match = new CSVM.Flight.VersusMatch(3, killTarget: 0, timeLimit: 60f);
        match.RegisterKill(2, 0);
        match.RegisterKill(2, 1);
        match.RegisterKill(1, 0);
        var names = new[] { "Zachary", "Nathan" };

        var scores = DogfightLobby.ScoresOf(match.Standings(), names);

        Assert.Equal(new[] { "P3", "Nathan", "Zachary" }, scores.Select(s => s.Name));
        Assert.Equal(new DogfightScore("P3", 2, 2, 0), scores[0]);
        Assert.Equal(2, scores[2].Deaths);
    }

    [Fact]
    public void LandingShowsTheScoresAndOpensANewRoundThatClearsEveryReady()
    {
        var (host, guests, _) = Lobbies(2);
        Settle(host, guests);
        host.SetReady(true);
        guests[0].SetReady(true);
        Settle(host, guests);
        Assert.True(host.CanLaunch);
        Assert.Empty(host.Scores);

        var scores = new[] { new DogfightScore("Host", 3, 3, 1) };
        host.Land(scores);
        guests[0].Land(scores);
        Settle(host, guests);

        Assert.Equal(scores, host.Scores);
        Assert.Equal(scores, guests[0].Scores);
        Assert.False(host.Ready);
        Assert.False(guests[0].Ready);
        Assert.Equal(new[] { false, false }, host.Players.Select(p => p.Ready));

        // ABLE-TO-FAIL CONTROL: both mark Ready under the new round and the host can launch again.
        host.SetReady(true);
        guests[0].SetReady(true);
        Settle(host, guests);
        Assert.True(host.CanLaunch);
    }

    [Fact]
    public void OneChatLineArrivesOnceOnEveryEnd()
    {
        var (host, guests, _) = Lobbies(3);
        Settle(host, guests);
        Assert.True(guests[0].Say("hello"));
        Settle(host, guests);
        Settle(host, guests);

        Assert.Single(host.Chat);
        Assert.Single(guests[0].Chat);
        Assert.Single(guests[1].Chat);
        Assert.Equal("hello", guests[1].Chat[0].Text);

        // ABLE-TO-FAIL CONTROL: an empty or overlong line is refused.
        Assert.False(host.Say("   "));
        Assert.False(host.Say(new string('x', LobbyChatMessage.MaxChars + 1)));
    }

    [Fact]
    public void TheRulesCarryOnlyTheChosenVictoryAndTheLivesWhenLimited()
    {
        var options = new DogfightOptionsMessage(1, 2, 1, DogfightVictory.Time, 5, 40, true, 2, false);
        var rules = DogfightLobby.RulesOf(options);
        Assert.Equal(0, rules.KillTarget);
        Assert.Equal(5, rules.TimeLimitMinutes);
        Assert.Equal(2, rules.Lives);
        Assert.False(rules.AutoRespawn);

        var scored = DogfightLobby.RulesOf(options with { Victory = DogfightVictory.Score, LimitedLives = false });
        Assert.Equal(40, scored.KillTarget);
        Assert.Equal(0, scored.TimeLimitMinutes);
        Assert.Equal(0, scored.Lives);
    }

    [Fact]
    public void TheLivesBoxIsGreyedUntilLimitedAndClampsToOneToNinetyNine()
    {
        var (host, _, _) = Lobbies(1);
        Assert.False(host.Options.LimitedLives);
        Assert.True(host.Options.AutoRespawn);
        Assert.False(host.SetLives(5));

        Assert.True(host.SetLimitedLives(true));
        Assert.Equal(DogfightLobby.DefaultLives, host.Options.Lives);
        Assert.True(host.SetLives(0));
        Assert.Equal(1, host.Options.Lives);
        Assert.True(host.SetLives(250));
        Assert.Equal(DogfightLobby.MaxLives, host.Options.Lives);
        Assert.Equal(1, DogfightLobby.RulesOf(host.Options with { Lives = 0 }).Lives);

        // ABLE-TO-FAIL CONTROL: a count inside the range is taken as typed.
        Assert.True(host.SetLives(7));
        Assert.Equal(7, host.Options.Lives);
    }

    private static (DogfightLobby Host, List<DogfightLobby> Guests, IReadOnlyList<LoopbackTransport> Mesh) Lobbies(
        int players, string guestName = "")
    {
        var mesh = LoopbackTransport.Mesh(players, Clean, new Random(players));
        var hostWire = new NetLobby(mesh[0]);
        var host = new DogfightLobby(hostWire, () => "Host");
        var guests = new List<DogfightLobby>();
        var wires = new List<NetLobby> { hostWire };
        for (int i = 1; i < players; i++)
        {
            var wire = new NetLobby(mesh[i]);
            wires.Add(wire);
            var guest = new DogfightLobby(wire, () => guestName, 0);
            guest.Show();
            guests.Add(guest);
        }

        Steps[host] = wires;
        return (host, guests, mesh);
    }

    // Every end steps its socket and then its lobby, four times over, which settles a relayed line.
    private static void Settle(DogfightLobby host, List<DogfightLobby> guests)
    {
        var wires = Steps[host];
        for (int round = 0; round < 4; round++)
        {
            wires[0].Step(0.016);
            host.Step();
            for (int i = 0; i < guests.Count; i++)
            {
                wires[i + 1].Step(0.016);
                guests[i].Step();
            }
        }
    }
}