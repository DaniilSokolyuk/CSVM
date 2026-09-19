namespace CSVM.Net;

/// <summary>
/// When an owner puts its own aeroplane on the wire, and what sequence each sample carries. One
/// per session, advanced once per simulation step, so the rate is in sim steps and not in frames
/// or wall seconds. The sequence is per seat, because two seats sharing one ladder would each
/// drop the other's samples as stale. Engine-free and state-free beyond those two counters: a
/// sample's contents are the session's to fill, and its fate on the far side is
/// <see cref="RemotePoseBuffer"/>'s.
/// </summary>
public sealed class AircraftStateCadence
{
    /// <summary>Simulation steps between two samples of one aircraft. Three at the fixed step is
    /// 20 Hz, which puts two send intervals inside
    /// <see cref="RemotePoseBuffer.BufferDelaySeconds"/>. One lost sample then still leaves the
    /// buffer a pair to read between. Accepted on the two-session harness beside the buffer's own
    /// two constants, where jitter and not the rate bounds the residual.</summary>
    public const int SendStepInterval = 3;

    private readonly ushort[] _sequence = new ushort[NetSeats.SeatCapacity];
    private int _steps;

    /// <summary>Simulation steps this cadence has been advanced through.</summary>
    public int Steps => _steps;

    /// <summary>Advances one simulation step and reports whether this is a sending one. The first
    /// step sends, so a session stepped once has already put its opening pose on the wire.
    /// </summary>
    public bool StepSends() => _steps++ % SendStepInterval == 0;

    /// <summary>The sequence the next sample of <paramref name="seat"/> carries, counted from
    /// zero. It wraps at 16 bits, which the receiving buffer's comparison is written for.
    /// </summary>
    /// <param name="seat">The seat index the sample describes.</param>
    public ushort Next(int seat) => _sequence[seat]++;
}
