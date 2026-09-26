using System.Collections.Generic;

namespace CSVM.Bindings;

/// <summary>A device reader that also lists the stick identities it answers for right now. A
/// capture scans those past the pad placeholder. The interface keeps this
/// namespace free of the stick library, as <see cref="IStickRows"/> does: the stick side implements
/// it, and <see cref="SeatDeviceState"/> passes its stick reader's list through.</summary>
public interface IStickDevices
{
    /// <summary>Whether stick reads are neutral right now (unfocused window, <c>--no-pads</c>), so a
    /// capture must not take them as where the sticks rest.</summary>
    bool ReadsBlocked { get; }

    /// <summary>One identity per connected stick model this reader answers for, empty for a seat
    /// that reads no sticks.</summary>
    IReadOnlyList<DeviceId> Devices();
}
