namespace CSVM.Net;

/// <summary>
/// Which transport channel a message rides. A sequenced payload is discarded per (sender,
/// channel), and the host relays every guest's stream under its own peer id. Two guests sharing
/// one channel would discard each other by sequence number. A seat's own channel keeps its stream
/// ordered against itself alone. Everything reliable stays on <see cref="Events"/>, where nothing
/// is discarded and the order across seats does not matter.
/// </summary>
public static class NetChannels
{
    /// <summary>The channel the join and every reliable event ride.</summary>
    public const int Events = 0;

    /// <summary>The channel seat 0's unreliable stream rides; the rest follow in seat order. One
    /// above <see cref="Events"/> so a sequence number never meets the join.</summary>
    public const int FirstSeat = 1;

    /// <summary>The channel seat <paramref name="seat"/>'s unreliable stream rides. A seat outside
    /// the roster's ceiling falls back to <see cref="Events"/>, which costs ordering rather than
    /// delivery.</summary>
    public static int ForSeat(int seat) =>
        seat >= 0 && seat < NetSeats.MaxPlayers ? FirstSeat + seat : Events;
}
