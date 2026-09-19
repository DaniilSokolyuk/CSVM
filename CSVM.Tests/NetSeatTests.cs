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
        Assert.Equal(8, NetSeats.MaxPlayers);
        Assert.Equal(16, NetSeats.SeatCapacity);
        Assert.True(NetSeats.SeatCapacity > NetSeats.MaxPlayers,
            "the ceiling has to be raisable without resizing a table");
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

    private static IReadOnlyList<NetSeat> Roster(int count, int locals) =>
        Enumerable.Range(0, count)
            .Select(i => new NetSeat { PeerId = i + 1, SeatIndex = i, IsLocal = i < locals, Callsign = $"P{i + 1}" })
            .ToList();
}
