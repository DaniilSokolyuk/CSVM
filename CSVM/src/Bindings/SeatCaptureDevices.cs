using System;
using System.Collections.Generic;

namespace CSVM.Bindings;

/// <summary>One seat's capture readers: a <see cref="SeatDeviceState"/> per
/// <see cref="InputContext"/> over the same pad list, each built on the identity that context's
/// bindings are authored on.
/// ⚠ The three polling sites do not share one placeholder. Flight rows sit on
/// <see cref="DefaultBindings.AnyPad"/>, the free camera's on its own, a menu seat's on its own, so
/// a single reader answers for one context and reads false for the other two. The reader and the
/// identity are built from the one <c>padOf</c> passed here for that reason: they cannot disagree,
/// rather than agreeing because a call site paired them correctly.</summary>
public sealed class SeatCaptureDevices : ICaptureDevices
{
    private readonly Func<InputContext, DeviceId> _padOf;
    private readonly Dictionary<InputContext, SeatDeviceState> _states = new();

    /// <summary>Readers for a seat whose pad rows sit where <paramref name="padOf"/> says, over the
    /// pads <paramref name="pads"/> names. Both are the seat's own, since a splitscreen seat holds the
    /// pad its player joined on. Another seat's list would read a pad nobody at this row is holding.
    /// <paramref name="sticks"/> is the seat's stick reader, silent for every seat but seat 1.
    /// </summary>
    public SeatCaptureDevices(Func<InputContext, DeviceId> padOf, Func<int[]?> pads, IDeviceState? sticks = null)
    {
        ArgumentNullException.ThrowIfNull(padOf);
        ArgumentNullException.ThrowIfNull(pads);
        _padOf = padOf;
        foreach (var context in Enum.GetValues<InputContext>())
        {
            _states[context] = new SeatDeviceState(padOf(context), pads, sticks: sticks);
        }
    }

    /// <inheritdoc/>
    public DeviceId PadOf(InputContext context) => _padOf(context);

    /// <summary>That context's reader, with this frame's pad list taken first. The refresh happens
    /// here because a capture reads between the poller's own ticks: the screen swallows the frame
    /// it is capturing on, so nothing else is guaranteed to have refreshed the seat.</summary>
    public IDeviceState For(InputContext context)
    {
        var state = _states[context];
        state.Refresh();
        return state;
    }
}
