namespace CSVM.Net;

/// <summary>
/// When a host repeats the match state, in simulation steps. Every change that matters is sent
/// where it happens, so this tick exists only for the clock. A guest that drifted needs the
/// remaining time refreshed, and the same tick is the one reading
/// <see cref="NetClockSlew"/> can take an offset from. Engine-free, one counter, and no opinion
/// about what the message carries.
/// </summary>
public sealed class MatchStateCadence
{
    /// <summary>Simulation steps between two clock ticks. Sixty at the fixed step is 1 Hz. That
    /// is the rate the versus HUD's whole-second readout shows a difference at. A faster tick
    /// would spend the wire on digits nobody sees. TUNE under BL-1025. The ending never
    /// waits for this, and a match with no time limit still ticks so the slew keeps reading.
    /// </summary>
    public const int TickStepInterval = 60;

    private int _steps;

    /// <summary>Simulation steps this cadence has been advanced through.</summary>
    public int Steps => _steps;

    /// <summary>Advances one simulation step and reports whether this is a ticking one. The first
    /// step ticks, so a session stepped once has already put the clock on the wire.</summary>
    public bool StepSends() => _steps++ % TickStepInterval == 0;
}
