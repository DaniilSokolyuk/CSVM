using System;
using System.Collections.Generic;
using Godot;

namespace CSVM.Bindings;

/// <summary>This tick's hardware for one seat: the keyboard, the mouse, and the pads the seat holds
/// behind one placeholder identity. A seat reads a SET of pads while a binding names one device.
/// Pad defaults therefore sit on a placeholder (<see cref="DefaultBindings.AnyPad"/> and its
/// per-site twins); this ORs buttons and takes the largest-magnitude axis across the set.
/// ⚠ Pad reads go through <see cref="Pads.For"/>, never a connection index from the device registry.
/// It carries the <c>--no-pads</c> and focus gate and the phantom-device policy (span the set, never
/// <c>pads[0]</c>). Every other joypad identity goes to the seat's stick reader, if it has one.
/// </summary>
public sealed class SeatDeviceState : IDeviceState, IStickDevices
{
    private readonly DeviceId _seatPad;
    private readonly Func<int[]?> _seatDevices;
    private readonly bool _readsPads;
    private readonly IDeviceState? _sticks;
    private readonly List<int> _pads = new();

    /// <summary>A seat reading <paramref name="seatPad"/>'s bindings off the pads
    /// <paramref name="seatDevices"/> names, re-asked each <see cref="Refresh"/> because a seat's
    /// device list changes when a splitscreen player joins. Pass <paramref name="readsPads"/> false
    /// for the keyboard-half reader of a site that resolves both halves separately; it mutes
    /// <paramref name="sticks"/> too. <paramref name="sticks"/> answers for every other identity.</summary>
    public SeatDeviceState(DeviceId seatPad, Func<int[]?> seatDevices, bool readsPads = true, IDeviceState? sticks = null)
    {
        _seatPad = seatPad;
        _seatDevices = seatDevices ?? throw new ArgumentNullException(nameof(seatDevices));
        _readsPads = readsPads;
        _sticks = readsPads ? sticks : null;
    }

    /// <summary>Whether the seat's stick reader reads neutral right now; false without one.</summary>
    public bool ReadsBlocked => _sticks is IStickDevices { ReadsBlocked: true };

    /// <summary>Takes this tick's pad list once, before anything resolves. <see cref="Pads.For"/>
    /// re-reads the connected roster on every call, and a tick would otherwise ask it once per pad
    /// binding rather than once.</summary>
    public void Refresh()
    {
        _pads.Clear();
        if (!_readsPads)
            return;
        foreach (int pad in Pads.For(_seatDevices()))
        {
            _pads.Add(pad);
        }
    }

    // The keyboard and the mouse are read ungated here, unlike the pads. Godot polls joypads
    // regardless of window focus while key state is focus-scoped, which is why --no-pads exists with
    // no keyboard counterpart; a seat that must not read them mutes them on PlayerActions instead.
    public bool IsKeyDown(DeviceId device, int keyCode) =>
        device.Kind == DeviceKind.Keyboard && Input.IsKeyPressed((Key)keyCode);

    public bool IsMouseButtonDown(DeviceId device, int button) =>
        device.Kind == DeviceKind.Mouse && Input.IsMouseButtonPressed((MouseButton)button);

    public bool IsButtonDown(DeviceId device, int button)
    {
        if (device != _seatPad)
            return _sticks is not null && _sticks.IsButtonDown(device, button);
        foreach (int pad in _pads)
        {
            if (Input.IsJoyButtonPressed(pad, (JoyButton)button))
                return true;
        }

        return false;
    }

    /// <summary>The largest-magnitude reading across this seat's pads, so an idle phantom device
    /// reads about zero and never masks the real stick.</summary>
    public float AxisValue(DeviceId device, int axis)
    {
        if (device != _seatPad)
            return _sticks?.AxisValue(device, axis) ?? 0f;
        float best = 0f;
        foreach (int pad in _pads)
        {
            float value = Input.GetJoyAxis(pad, (JoyAxis)axis);
            if (Mathf.Abs(value) > Mathf.Abs(best))
                best = value;
        }

        return best;
    }

    /// <summary>A stick's hat, and nothing on the pad placeholder: Godot reports a d-pad as four
    /// buttons (<see cref="DefaultBindings"/>), so <see cref="BindingStore"/> rejects a hat there.
    /// </summary>
    public HatDirection HatState(DeviceId device, int hat) =>
        device != _seatPad && _sticks is not null ? _sticks.HatState(device, hat) : HatDirection.None;

    /// <summary>The stick identities the seat's stick reader answers for, empty without one or on a
    /// keyboard-half reader.</summary>
    public IReadOnlyList<DeviceId> Devices() =>
        _sticks is IStickDevices sticks ? sticks.Devices() : Array.Empty<DeviceId>();
}
