using System;

namespace CSVM.Net;

/// <summary>
/// How a guest holds its own session clock against the host's: as one offset, moved gradually.
/// <c>HostTime(guestClock) = guestClock + Offset</c>, the conversion every replicated timestamp is
/// read through. Writing a fresh reading straight in would move every interpolated pose and every
/// timed event on one frame, which reads as a world-wide stutter. So a reading becomes a target
/// the offset walks to over <see cref="ConvergeSeconds"/>, at no more than
/// <see cref="MaxRateOffset"/> of real time. A one-way reading is read forward by half the newest
/// round trip (<see cref="ObserveRoundTrip"/>); without one, the offset lags by the latency.
/// Engine-free and clock-free, so a unit suite drives it whole.
/// </summary>
public sealed class NetClockSlew
{
    /// <summary>Seconds a fresh reading's whole error is walked off over. TUNE.</summary>
    public const double ConvergeSeconds = 2.0;

    /// <summary>The fastest the offset may move, as a fraction of real time. It is also how far
    /// off real speed host time runs while it converges. TUNE.</summary>
    public const double MaxRateOffset = 0.10;

    /// <summary>A reading further from the offset in use than this is not walked to at all. The
    /// link lost or gained too much to hide, so it is applied outright. TUNE.</summary>
    public const double SnapSeconds = 5.0;

    /// <summary>Below this the offset counts as arrived and is set exactly, so a settled guest
    /// stops nudging.</summary>
    public const double SettledSeconds = 0.001;

    /// <summary>Starts at the handshake's own offset: the guest's clock reads zero at its build,
    /// so the host's clock at send IS the opening offset. That first alignment is not a slew,
    /// because nothing has been drawn against the old one yet.</summary>
    public NetClockSlew(double openingOffset)
    {
        if (double.IsNaN(openingOffset) || double.IsInfinity(openingOffset))
        {
            throw new ArgumentOutOfRangeException(nameof(openingOffset), openingOffset, "an offset is finite seconds");
        }

        Offset = openingOffset;
        Target = openingOffset;
    }

    /// <summary>The offset in use now: add it to this guest's own session clock to get the host's.
    /// </summary>
    public double Offset { get; private set; }

    /// <summary>The offset the newest reading asked for, which <see cref="Offset"/> is walking to.
    /// </summary>
    public double Target { get; private set; }

    /// <summary>Signed offset-seconds per real second while converging, zero once settled.
    /// </summary>
    public double Rate { get; private set; }

    /// <summary>How many readings were too far out to walk to. A rising count says the window or
    /// the threshold is wrong, not that the link is.</summary>
    public int Snaps { get; private set; }

    /// <summary>Whether the offset has arrived at the newest reading.</summary>
    public bool Settled => Math.Abs(Target - Offset) <= SettledSeconds;

    /// <summary>The newest measured round trip to the host, in seconds, zero until one is
    /// measured. Half of it is how late every one-way reading is taken to be.</summary>
    public double RoundTrip { get; private set; }

    /// <summary>How many round trips have been measured.</summary>
    public int RoundTrips { get; private set; }

    /// <summary>Takes one one-way reading of the two clocks: the host's at send against this
    /// guest's at arrival, read forward by half the newest <see cref="RoundTrip"/>. It replaces
    /// the target outright, because a reading is the whole truth about the offset rather than a
    /// correction to it, and re-aims the walk. A reading past <see cref="SnapSeconds"/> from the
    /// offset in use is applied at once and counted.</summary>
    public void Observe(double hostClock, double guestClock) =>
        Aim(hostClock + (RoundTrip * 0.5) - guestClock, hostClock);

    /// <summary>Takes one round trip: asked at <paramref name="askedAt"/> on this guest's clock,
    /// answered with the host's <paramref name="hostClock"/>, back at <paramref name="guestClock"/>.
    /// The host's clock now is its answer plus half the round trip, true when both legs are equal.
    /// The first round trip ends the opening alignment, so it is applied at once. An event
    /// replayed while the latency was still being walked off would keep that error for good.
    /// </summary>
    public void ObserveRoundTrip(double askedAt, double hostClock, double guestClock)
    {
        double roundTrip = guestClock - askedAt;
        if (double.IsNaN(roundTrip) || double.IsInfinity(roundTrip) || roundTrip < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(askedAt), askedAt, "a round trip is finite seconds and ends after it starts");
        }

        RoundTrip = roundTrip;
        RoundTrips++;
        Observe(hostClock, guestClock);
        if (RoundTrips == 1)
        {
            Offset = Target;
            Rate = 0.0;
        }
    }

    /// <summary>Walks the offset for a frame of <paramref name="dt"/> seconds and returns how far
    /// it moved. It never overshoots: the step that would pass the target lands on it exactly.
    /// </summary>
    public double Advance(double dt)
    {
        if (dt <= 0.0 || Rate == 0.0)
        {
            return 0.0;
        }

        double error = Target - Offset;
        double step = Rate * dt;
        if (Math.Abs(step) >= Math.Abs(error))
        {
            step = error;
            Rate = 0.0;
        }

        Offset += step;
        return step;
    }

    /// <summary>The host's session clock as this guest reads it, the conversion every replicated
    /// timestamp goes through.</summary>
    public double HostTime(double guestClock) => guestClock + Offset;

    // One reading, as the offset it asks for.
    private void Aim(double target, double hostClock)
    {
        if (double.IsNaN(target) || double.IsInfinity(target))
        {
            throw new ArgumentOutOfRangeException(nameof(hostClock), hostClock, "a clock reading is finite seconds");
        }

        Target = target;
        double error = Target - Offset;
        if (Math.Abs(error) > SnapSeconds)
        {
            Offset = Target;
            Rate = 0.0;
            Snaps++;
            return;
        }

        Rate = Math.Abs(error) <= SettledSeconds
            ? 0.0
            : Math.Clamp(error / ConvergeSeconds, -MaxRateOffset, MaxRateOffset);
    }
}
