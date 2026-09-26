using System;
using System.Collections.Generic;

namespace CSVM.Net;

/// <summary>
/// The seat roster's own rules. How many pilots a match admits, how wide every seat-indexed table
/// is, each seat's identity colour, and what makes a roster well formed. The ceiling and the table
/// width are two numbers on purpose. Lowering the ceiling is then this one constant, and raising
/// the width is the data's block size, rather than a sweep of every array.
/// </summary>
public static class NetSeats
{
    /// <summary>Pilots one match admits. The original codes no cap at all; its lobby shows
    /// "Players (1 of 16)" and its data holds 16. Its authored colour table and respawn fan serve
    /// eight, so seats past that take derived colours and a wrapped fan
    /// (<c>docs/org/multiplayer-spawn.md</c>).</summary>
    public const int MaxPlayers = 16;

    /// <summary>How wide every seat-indexed table is built, never below <see cref="MaxPlayers"/>.
    /// Sixteen is what the original's own data holds: 16-entry spawn blocks, a 16-entry lobby
    /// player array and a 16-entry team array.</summary>
    public const int SeatCapacity = 16;

    // The original's per-pilot colour table at 00628eb4, eight dwords, zeros from 00628ed4. Each
    // entry's three stored bytes are read here as red, green, blue. That is the reading under which
    // the set comes out red, blue, green, yellow, magenta, lime, teal and violet. Nothing decoded
    // shows which channel its consumer takes first, so the reading is a TUNE.
    private static readonly uint[] Authored =
    {
        0x812D2D, 0x2D2D81, 0x2D812D, 0x81812D, 0x812D64, 0x66812D, 0x457C81, 0x662D81,
    };

    private static readonly uint[] Table = BuildTable();

    /// <summary>Every seat's colour as 0xRRGGBB, <see cref="SeatCapacity"/> long. The first eight
    /// are the authored table; the rest are derived (see <see cref="SeatColor"/>).</summary>
    public static IReadOnlyList<uint> SeatColors => Table;

    /// <summary>Seat <paramref name="seat"/>'s identity colour as 0xRRGGBB. Seats 0 to 7 take the
    /// original's authored dwords in order. The remake's index is 0-based where the original's was
    /// 1-based, so its eighth pilot read past the table and ours does not. Seats 8 to 15 take the
    /// channel-wise complement of seat minus 8, a light twin of a dark authored colour. It collides
    /// with none of them. TUNE.</summary>
    public static uint SeatColor(int seat)
    {
        if (seat < 0 || seat >= SeatCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(seat), seat, $"a seat index is 0 to {SeatCapacity - 1}");
        }

        return Table[seat];
    }

    /// <summary>Throws unless <paramref name="roster"/> is a match a session can be built from. One
    /// to <see cref="MaxPlayers"/> seats, numbered 0 upward with no gap, and at least one of them
    /// flown on this machine. The numbering is required because every seat-indexed table is
    /// addressed by it directly. A leaver's seat therefore stays in the roster until the match
    /// ends, rather than being closed up.</summary>
    public static void Validate(IReadOnlyList<NetSeat> roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        if (roster.Count == 0 || roster.Count > MaxPlayers)
        {
            throw new ArgumentException($"a match holds 1 to {MaxPlayers} seats, not {roster.Count}", nameof(roster));
        }

        var seen = new HashSet<int>();
        int locals = 0;
        foreach (var seat in roster)
        {
            if (seat.SeatIndex < 0 || seat.SeatIndex >= roster.Count)
            {
                throw new ArgumentException($"seat index {seat.SeatIndex} is outside 0 to {roster.Count - 1}", nameof(roster));
            }

            if (!seen.Add(seat.SeatIndex))
            {
                throw new ArgumentException($"two seats claim index {seat.SeatIndex}", nameof(roster));
            }

            if (seat.IsLocal)
            {
                locals++;
            }
        }

        if (locals == 0)
        {
            throw new ArgumentException("no seat in the roster is flown on this machine", nameof(roster));
        }
    }

    /// <summary>A host's field: one seat per plane in <paramref name="localPlanes"/> flown here,
    /// then one per peer in <paramref name="peers"/> flying <paramref name="remotePlane"/>, cut at
    /// <see cref="MaxPlayers"/>. Local seats are called P1 upward and a remote one after its peer.
    /// A remote guest holds a seat and nothing else here: no pane, no pad, no input.</summary>
    public static NetSeat[] Field(
        int localPeer, IReadOnlyList<string> localPlanes, IReadOnlyList<int> peers, string remotePlane)
    {
        ArgumentNullException.ThrowIfNull(localPlanes);
        ArgumentNullException.ThrowIfNull(peers);
        var seats = new List<NetSeat>(localPlanes.Count + peers.Count);
        for (int i = 0; i < localPlanes.Count && seats.Count < MaxPlayers; i++)
        {
            seats.Add(new NetSeat
            {
                PeerId = localPeer,
                SeatIndex = seats.Count,
                IsLocal = true,
                Callsign = $"P{(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                PlaneNode = localPlanes[i],
            });
        }

        foreach (int peer in peers)
        {
            if (seats.Count >= MaxPlayers)
            {
                break;
            }

            seats.Add(new NetSeat
            {
                PeerId = peer,
                SeatIndex = seats.Count,
                Callsign = $"guest {peer.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                PlaneNode = remotePlane,
            });
        }

        Validate(seats);
        return seats.ToArray();
    }

    /// <summary>A co-op host's field: one seat per plane in <paramref name="localPlanes"/> flown
    /// here, then one per guest flying the plane it picked, cut at <see cref="MaxPlayers"/>. A
    /// guest is called by the player name its pick carried. A seat with no name is called by its
    /// player number.</summary>
    public static NetSeat[] CoopField(
        int localPeer, IReadOnlyList<string> localPlanes, IReadOnlyList<(int Peer, string Plane, string Name)> guests)
    {
        ArgumentNullException.ThrowIfNull(localPlanes);
        ArgumentNullException.ThrowIfNull(guests);
        var seats = new List<NetSeat>(localPlanes.Count + guests.Count);
        for (int i = 0; i < localPlanes.Count + guests.Count && seats.Count < MaxPlayers; i++)
        {
            bool local = i < localPlanes.Count;
            string name = local ? "" : (guests[i - localPlanes.Count].Name ?? "").Trim();
            seats.Add(new NetSeat
            {
                PeerId = local ? localPeer : guests[i - localPlanes.Count].Peer,
                SeatIndex = seats.Count,
                IsLocal = local,
                Callsign = name.Length > 0
                    ? name
                    : $"P{(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                PlaneNode = local ? localPlanes[i] : guests[i - localPlanes.Count].Plane,
            });
        }

        Validate(seats);
        return seats.ToArray();
    }

    /// <summary>Which of this machine's own seats <paramref name="seat"/> is, counting from 0 in
    /// seat order. That is its index into a list of local seats only, as a launch's menu picks are.
    /// -1 for a seat flown elsewhere. With no roster every seat is local, so it is its own.
    /// </summary>
    public static int LocalOrdinal(IReadOnlyList<NetSeat> roster, int seat)
    {
        ArgumentNullException.ThrowIfNull(roster);
        if (seat >= roster.Count)
        {
            return roster.Count == 0 ? seat : -1;
        }

        if (seat < 0 || !roster[seat].IsLocal)
        {
            return -1;
        }

        int ordinal = 0;
        for (int i = 0; i < seat; i++)
        {
            ordinal += roster[i].IsLocal ? 1 : 0;
        }

        return ordinal;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[SeatCapacity];
        for (int i = 0; i < SeatCapacity; i++)
        {
            table[i] = i < Authored.Length ? Authored[i] : ~Authored[i - Authored.Length] & 0xFFFFFFu;
        }

        return table;
    }
}
