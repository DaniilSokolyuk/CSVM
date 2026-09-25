namespace CSVM.Net;

/// <summary>
/// Which transport channel a message rides. A sequenced payload is discarded per (sender,
/// channel), and the host relays every guest's stream under its own peer id. Two guests sharing
/// one channel would discard each other by sequence number. A seat's own channel keeps its stream
/// ordered against itself alone. A seat's fire has a channel apart from its state, so a burst is
/// never judged against the pose samples sent around it. Everything reliable stays on
/// <see cref="Events"/>, where nothing is discarded and the order across seats does not matter.
/// </summary>
public static class NetChannels
{
    /// <summary>The channel the join and every reliable event ride.</summary>
    public const int Events = 0;

    /// <summary>The channel seat 0's unreliable stream rides; the rest follow in seat order. One
    /// above <see cref="Events"/> so a sequence number never meets the join.</summary>
    public const int FirstSeat = 1;

    /// <summary>The channel seat 0's fire rides; the rest follow in seat order. The range sits
    /// above the whole state range, so no seat's fire shares a channel with anybody's state.
    /// </summary>
    public const int FirstFire = FirstSeat + NetSeats.SeatCapacity;

    /// <summary>How many channels the layout uses, 0 to one below this: the events channel, one
    /// state channel and one fire channel per seat.</summary>
    public const int Count = FirstFire + NetSeats.SeatCapacity;

    /// <summary>The channel seat <paramref name="seat"/>'s unreliable stream rides. A seat outside
    /// the roster's ceiling falls back to <see cref="Events"/>, which costs ordering rather than
    /// delivery.</summary>
    public static int ForSeat(int seat) =>
        seat >= 0 && seat < NetSeats.MaxPlayers ? FirstSeat + seat : Events;

    /// <summary>The channel seat <paramref name="seat"/>'s fire rides, with the same fallback to
    /// <see cref="Events"/> as <see cref="ForSeat"/>.</summary>
    public static int ForFire(int seat) =>
        seat >= 0 && seat < NetSeats.MaxPlayers ? FirstFire + seat : Events;
}
