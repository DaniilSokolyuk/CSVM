namespace CSVM.Net;

/// <summary>
/// One pilot's place in a network match, shaped like the record the original allocates per player.
/// The peer it is addressed by, its team, whether this machine flies it, and its callsign. Then
/// the airframe and paint it chose, its seat index and its signed score. That index is the identity
/// every seat-indexed system already keys on. A remote pilot therefore indexes spawns, scores,
/// markers and colours exactly as a splitscreen pane does.
/// The record is what a session hands to and takes from the wire; the bytes are the vocabulary's.
/// </summary>
public sealed record NetSeat
{
    /// <summary>The transport peer this seat is reached through, the match key. A seat on this
    /// machine carries the local peer's own id.</summary>
    public int PeerId { get; init; }

    /// <summary>0-based seat number, below <see cref="NetSeats.SeatCapacity"/>. The original's own
    /// index is 1-based and its eighth pilot reads past the colour table; the remake's is not.
    /// </summary>
    public int SeatIndex { get; init; }

    /// <summary>The side this pilot fights on, 0 in a free-for-all. Teams are handed out from 1,
    /// which is what leaves block 0 of the spawn table to the un-teamed match.</summary>
    public int TeamId { get; init; }

    /// <summary>Whether this machine simulates the seat. A local seat gets a pane, a camera, a
    /// HUD, a listener and an input device. A remote one gets none of those, and everything
    /// else.</summary>
    public bool IsLocal { get; init; }

    /// <summary>What the scoreboard and the kill line call this pilot.</summary>
    public string Callsign { get; init; } = "";

    /// <summary>The airframe node this seat flies, the same name a <c>--plane=</c> entry carries.
    /// </summary>
    public string PlaneNode { get; init; } = "";

    /// <summary>The paint this seat chose, empty where it takes the session's own livery draw.
    /// </summary>
    public string Livery { get; init; } = "";

    /// <summary>This pilot's match score, signed: a suicide costs a point, so it goes negative.
    /// </summary>
    public int Score { get; init; }

    /// <summary>This seat's identity colour as 0xRRGGBB, the marker and scoreboard key.</summary>
    public uint Color => NetSeats.SeatColor(SeatIndex);
}
