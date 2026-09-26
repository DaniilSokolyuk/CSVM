using System;
using System.Collections.Generic;
using CSVM.Bindings;

namespace CSVM.Flight.Airframe;

/// <summary>Flight's consumed-input latch, one per aircraft. A cutscene skip or a pause-sheet
/// dismiss hands input back on the frame the control that confirmed it is still physically down.
/// One control serves both sides: gamepad B is <c>MenuBack</c> and <c>FireGuns</c>, gamepad A is
/// <c>MenuAccept</c> and <c>FireRockets</c>, and a cutscene takes any key at all.
/// <see cref="FlightController"/> reads its buttons as edge-free levels, so that still-down press
/// would read as a fresh command. <see cref="Arm"/> runs where input comes back, and
/// <see cref="Read"/> is each latched action's own read every frame after. Pure state, driven by
/// its own unit tests and, through the re-entry points, by the flight input handoff suite. Public
/// so those tests can construct and drive it; this project carries no InternalsVisibleTo to
/// <c>CSVM.Tests</c>.</summary>
public sealed class FlightReentryLatch
{
    /// <summary>The actions whose <see cref="FlightController"/> read goes through this latch: the
    /// discrete flight commands that act on the frame they read true. The attitude and look axes
    /// are absent, a stick deflection being a posture rather than a press, and so is
    /// <c>Pause</c>, whose own edge detector already spans the handover.</summary>
    public static readonly IReadOnlyList<InputAction> Latched = new[]
    {
        InputAction.FireGuns,
        InputAction.FireRockets,
        InputAction.Nitro,
        InputAction.Respawn,
        InputAction.AutoLand,
        InputAction.SelectGunGroup,
        InputAction.SelectGunGroupPrev,
        InputAction.SelectOrdnance,
        InputAction.SelectOrdnancePrev,
    };

    private static readonly int ActionCount = Enum.GetValues(typeof(InputAction)).Length;

    private readonly bool[] _armed = new bool[ActionCount];

    /// <summary>Arms every latched action, whatever the controls read as input comes back.
    /// ⚠ Do not gate this on a button reading taken here. A re-entry point runs inside an input
    /// handler, whose cached action snapshot was resolved before the press that reached it. Such a
    /// reading answers "up" for the very press this latch exists to swallow. The first
    /// <see cref="Read"/> after the arm follows a fresh poll and disarms on its own when nothing
    /// was down.</summary>
    public void Arm()
    {
        foreach (var action in Latched)
        {
            _armed[(int)action] = true;
        }
    }

    /// <summary>This frame's reading for one action: released while that action is armed and
    /// <paramref name="held"/> stays true (the consumed press has not let go yet), which also
    /// disarms it the moment <paramref name="held"/> goes false, so the next real press reads
    /// through normally. An action outside <see cref="Latched"/> is never armed and so always
    /// reads through.</summary>
    public bool Read(InputAction action, bool held)
    {
        if (!held)
        {
            _armed[(int)action] = false;
            return false;
        }

        return !_armed[(int)action];
    }
}
