using System;
using System.Collections.Generic;

namespace CSVM.Sticks;

/// <summary>
/// The joystick library as <see cref="StickRoster"/> sees it: list, open, close, pump and raw
/// reads, addressed by SDL's instance id. <see cref="Sdl2Sticks"/> is the live one. A test hands
/// the roster a fake, so the gap-filler, hot-plug and the gate run without a DLL or a device.
/// Values are raw here (SDL's signed 16-bit axes, its hat bitmask); the roster normalises them.
/// </summary>
public interface IStickNative : IDisposable
{
    /// <summary>The loaded library's version, <c>major.minor.patch</c>.</summary>
    string Version { get; }

    /// <summary>The library's last error text, for a failed <see cref="Open"/>.</summary>
    string LastError { get; }

    /// <summary>Advances every open device's state one frame and drains the library's event
    /// queue; true when a device was added or removed since the last call.</summary>
    bool Pump();

    /// <summary>Every device the library currently lists, opened or not.</summary>
    IReadOnlyList<StickListing> List();

    /// <summary>Opens a listed device and reports its control counts; null when the library
    /// refuses it or it has gone since it was listed.</summary>
    Stick? Open(StickListing listing);

    /// <summary>Closes an opened device; a device never opened is ignored.</summary>
    void Close(int instance);

    /// <summary>An opened device's axis, -32768..32767; 0 for an unknown device or axis.</summary>
    short Axis(int instance, int axis);

    /// <summary>Whether an opened device's button is held; false for an unknown one.</summary>
    bool Button(int instance, int button);

    /// <summary>An opened device's hat as SDL's bitmask (up 1, right 2, down 4, left 8).</summary>
    byte Hat(int instance, int hat);
}
