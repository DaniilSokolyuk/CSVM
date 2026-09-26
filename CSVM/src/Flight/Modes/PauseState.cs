using System;
using CSVM.Flight.Airframe;

namespace CSVM.Flight.Modes;

/// <summary>
/// Who is holding the sim clock, and why: one instance shared by every human rig in the session
/// (assigned to <see cref="FlightController.PauseState"/> the same way <see cref="VersusMatch"/>
/// is), so any player's Start/P freezes the shared <c>GameClock</c> for everybody, but only the
/// player who paused may resume it. Owns none of the halt itself, <see cref="FlightController"/>
/// mirrors <see cref="Halted"/> into <c>GameClock.Halted</c> every frame; this class only decides
/// who is allowed to flip it, and keeps a results board's halt separate from a player's.
/// </summary>
public sealed class PauseState
{
    /// <summary>Fires on every accepted change, never on a rejected or redundant one.</summary>
    public event Action? Changed;

    /// <summary>The reasons currently holding the clock.</summary>
    public HaltReason Reasons { get; private set; }

    public bool Halted => Reasons != HaltReason.None;

    public bool Paused => (Reasons & HaltReason.Paused) != 0;

    /// <summary>Whether a results board is holding the clock.</summary>
    public bool Ended => (Reasons & HaltReason.Ended) != 0;

    /// <summary>The player who paused, or -1 when nobody has.</summary>
    public int OwnerPlayerIndex { get; private set; } = -1;

    /// <summary>Not paused: pauses and claims ownership for <paramref name="playerIndex"/>. Paused:
    /// resumes only if <paramref name="playerIndex"/> is the owner. Refused outright once a results
    /// board is up, whose own menu already offers the only two things left to do. Returns whether
    /// the state changed.</summary>
    public bool TryToggle(int playerIndex)
    {
        if (Ended)
            return false;
        if (!Paused)
        {
            Reasons |= HaltReason.Paused;
            OwnerPlayerIndex = playerIndex;
            Changed?.Invoke();
            return true;
        }
        if (playerIndex != OwnerPlayerIndex)
            return false;
        return ForceResume();
    }

    /// <summary>Drops the pause halt reason whoever owns it, for a rerun or an exit chosen from the
    /// pause menu. Returns whether the state changed.</summary>
    public bool ForceResume()
    {
        if (!Paused)
            return false;
        Reasons &= ~HaltReason.Paused;
        OwnerPlayerIndex = -1;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Sets a halt reason a results board owns. ⚠ Do not pass
    /// <see cref="HaltReason.Paused"/>; a pause carries ownership, so it goes through
    /// <see cref="TryToggle"/> alone.</summary>
    public bool Raise(HaltReason reason)
    {
        RejectPaused(reason);
        if ((Reasons & reason) == reason)
            return false;
        Reasons |= reason;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Drops a halt reason a results board owns. ⚠ Do not pass
    /// <see cref="HaltReason.Paused"/>; use <see cref="ForceResume"/>, which also clears
    /// ownership.</summary>
    public bool Clear(HaltReason reason)
    {
        RejectPaused(reason);
        if ((Reasons & reason) == 0)
            return false;
        Reasons &= ~reason;
        Changed?.Invoke();
        return true;
    }

    private static void RejectPaused(HaltReason reason)
    {
        if ((reason & HaltReason.Paused) != 0)
            throw new ArgumentException("a pause carries ownership; toggle it instead", nameof(reason));
    }
}
