using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Net;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The seat record and the roster rules every seat-indexed system is built against. The ceiling and
/// the table width are two separate numbers. A seat's colour is the original's authored dword where
/// one exists. A roster is numbered from zero with no gap, because the numbering IS the index into
/// the spawn walk, the score rows and the markers.
/// </summary>
[Trait("Tier", "Quick")]
public sealed class NetSeatTests
{
    // The eight dwords at 00628eb4, the stored byte triplet read as red, green, blue. Restated
    // here rather than read off NetSeats, so a change to the table fails this and is judged.
    private static readonly uint[] Authored =
    {
        0x812D2D, 0x2D2D81, 0x2D812D, 0x81812D, 0x812D64, 0x66812D, 0x457C81, 0x662D81,
    };

    [Fact]
    public void TheCeilingStandsBehindAWiderTable()
    {
        Assert.Equal(16, NetSeats.MaxPlayers);
        Assert.Equal(16, NetSeats.SeatCapacity);
        Assert.True(NetSeats.SeatCapacity >= NetSeats.MaxPlayers,
            "every seat-indexed table has to hold the whole ceiling");
    }

    [Fact]
    public void TheFirstEightColoursAreTheOriginalsOwn()
    {
        Assert.Equal(NetSeats.SeatCapacity, NetSeats.SeatColors.Count);
        for (int seat = 0; seat < Authored.Length; seat++)
        {
            Assert.Equal(Authored[seat], NetSeats.SeatColor(seat));
        }
    }

    [Fact]
    public void EverySeatHasAColourAndNoTwoShareOne()
    {
        var seen = new HashSet<uint>();
        for (int seat = 0; seat < NetSeats.SeatCapacity; seat++)
        {
            uint colour = NetSeats.SeatColor(seat);
            Assert.True(colour <= 0xFFFFFFu, $"seat {seat}'s colour {colour:x} is not an 0xRRGGBB value");
            Assert.True(seen.Add(colour), $"seat {seat} repeats colour {colour:x6}");
        }
    }

    [Fact]
    public void TheDerivedColoursAreTheStatedComplement()
    {
        for (int seat = Authored.Length; seat < NetSeats.SeatCapacity; seat++)
        {
            Assert.Equal(~Authored[seat - Authored.Length] & 0xFFFFFFu, NetSeats.SeatColor(seat));
        }
    }

    [Fact]
    public void ASeatOutsideTheTableIsARefusalRatherThanAWrapAround()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NetSeats.SeatColor(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetSeats.SeatColor(NetSeats.SeatCapacity));
    }

    [Fact]
    public void ASeatCarriesItsOwnColour()
    {
        var seat = new NetSeat { SeatIndex = 3, Callsign = "Paladin", IsLocal = true };
        Assert.Equal(NetSeats.SeatColor(3), seat.Color);
    }

    [Fact]
    public void ASeatIsTheDecodedRecordsFields()
    {
        var seat = new NetSeat
        {
            PeerId = 42,
            SeatIndex = 1,
            TeamId = 2,
            IsLocal = false,
            Callsign = "Nathan Zachary",
            PlaneNode = "player_bhawk",
            Livery = "red",
            Score = -3,
        };

        Assert.Equal(42, seat.PeerId);
        Assert.Equal(2, seat.TeamId);
        Assert.False(seat.IsLocal);
        Assert.Equal("Nathan Zachary", seat.Callsign);
        Assert.Equal("player_bhawk", seat.PlaneNode);
        Assert.Equal("red", seat.Livery);
        Assert.Equal(-3, seat.Score);
        // Signed, because the original's own scoring takes a point off a death with no killer.
        Assert.True(seat.Score < 0);
    }

    [Fact]
    public void AWellFormedRosterPasses()
    {
        NetSeats.Validate(Roster(2, locals: 1));
        NetSeats.Validate(Roster(NetSeats.MaxPlayers, locals: 1));
        // A host flying splitscreen panes beside its guests: several seats are local.
        NetSeats.Validate(Roster(4, locals: 3));
    }

    [Fact]
    public void ARosterPastTheCeilingIsRefused()
    {
        Assert.Throws<ArgumentException>(() => NetSeats.Validate(Roster(NetSeats.MaxPlayers + 1, locals: 1)));
        Assert.Throws<ArgumentException>(() => NetSeats.Validate(Array.Empty<NetSeat>()));
    }

    [Fact]
    public void ARosterWithAGapOrARepeatIsRefused()
    {
        var gap = Roster(3, locals: 1).ToList();
        gap[2] = gap[2] with { SeatIndex = 7 };
        Assert.Throws<ArgumentException>(() => NetSeats.Validate(gap));

        var repeat = Roster(3, locals: 1).ToList();
        repeat[2] = repeat[2] with { SeatIndex = 1 };
        Assert.Throws<ArgumentException>(() => NetSeats.Validate(repeat));
    }

    [Fact]
    public void ARosterThisMachineFliesNoneOfIsRefused()
    {
        Assert.Throws<ArgumentException>(() => NetSeats.Validate(Roster(3, locals: 0)));
    }

    [Fact]
    public void AHostsRosterSeatsTheHostAtZero()
    {
        NetSeats.Validate(Roster(3, locals: 1), hostPeer: 1);

        var guestFirst = Roster(3, locals: 1).ToList();
        guestFirst[0] = guestFirst[0] with { PeerId = 2 };
        guestFirst[1] = guestFirst[1] with { PeerId = 1 };
        Assert.Throws<ArgumentException>(() => NetSeats.Validate(guestFirst, hostPeer: 1));

        // ABLE-TO-FAIL CONTROL: a guest's rebuilt roster names no host peer and is not held to it.
        NetSeats.Validate(guestFirst);

        Assert.Equal(1, NetSeats.CoopField(1, new[] { "player_bhawk" }, new[] { (4, "player_fury", "Lucy") })[0].PeerId);
        Assert.Equal(1, NetSeats.Field(1, new[] { "player_bhawk" }, new[] { 4 }, "player_bhawk")[0].PeerId);
    }

    [Fact]
    public void ACoopFieldSeatsEachGuestInThePlaneItPickedUnderItsName()
    {
        var field = NetSeats.CoopField(1, new[] { "player_bhawk" }, new[] { (4, "player_fury", "Lucy"), (9, "player_warhawk", " ") });

        Assert.Equal(new[] { 1, 4, 9 }, field.Select(s => s.PeerId));
        Assert.Equal(new[] { true, false, false }, field.Select(s => s.IsLocal));

        // A guest that sent a name is called by it. ABLE-TO-FAIL CONTROL: one that sent none falls
        // back to its player number.
        Assert.Equal(new[] { "P1", "Lucy", "P3" }, field.Select(s => s.Callsign));
        Assert.Equal(new[] { "player_bhawk", "player_fury", "player_warhawk" }, field.Select(s => s.PlaneNode));

        // ABLE-TO-FAIL CONTROL: the versus field flies every guest in one plane and names it by peer.
        var versus = NetSeats.Field(1, new[] { "player_bhawk" }, new[] { 4 }, "player_bhawk");
        Assert.Equal("guest 4", versus[1].Callsign);
        Assert.Equal("player_bhawk", versus[1].PlaneNode);
    }

    [Fact]
    public void ALocalOrdinalCountsOnlyTheSeatsFlownHere()
    {
        var guestSide = new[]
        {
            new NetSeat { SeatIndex = 0, PeerId = 1, Callsign = "P1" },
            new NetSeat { SeatIndex = 1, PeerId = 2, IsLocal = true, Callsign = "P2" },
            new NetSeat { SeatIndex = 2, PeerId = 3, Callsign = "P3" },
            new NetSeat { SeatIndex = 3, PeerId = 2, IsLocal = true, Callsign = "P4" },
        };

        Assert.Equal(-1, NetSeats.LocalOrdinal(guestSide, 0));
        Assert.Equal(0, NetSeats.LocalOrdinal(guestSide, 1));
        Assert.Equal(1, NetSeats.LocalOrdinal(guestSide, 3));
        Assert.Equal(-1, NetSeats.LocalOrdinal(guestSide, 4));

        // ABLE-TO-FAIL CONTROL: offline there is no roster and every seat is its own ordinal.
        Assert.Equal(3, NetSeats.LocalOrdinal(Array.Empty<NetSeat>(), 3));
    }

    private static IReadOnlyList<NetSeat> Roster(int count, int locals) =>
        Enumerable.Range(0, count)
            .Select(i => new NetSeat { PeerId = i + 1, SeatIndex = i, IsLocal = i < locals, Callsign = $"P{i + 1}" })
            .ToList();
}
