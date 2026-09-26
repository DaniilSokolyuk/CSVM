using CSVM.Bindings;
using CSVM.Sticks;

namespace CSVM.Flight.Airframe;

/// <summary>One side of a seat's non-keyboard hardware: its flight sticks alone, or everything
/// except them. A seat's reader answers pads and sticks together, and one keymap holds both. The
/// flight axes read the two apart, since a pad bends through <see cref="AnalogAxes.PadCurve"/> and a
/// stick flies linearly. A filter rather than a second source, so the seat's reader stays the only
/// thing that reads hardware.</summary>
public sealed class StickSplit : IDeviceState
{
    private readonly IDeviceState _source;
    private readonly bool _sticks;

    private StickSplit(IDeviceState source, bool sticks)
    {
        _source = source;
        _sticks = sticks;
    }

    /// <summary><paramref name="source"/> with every stick identity reading idle.</summary>
    public static StickSplit WithoutSticks(IDeviceState source) => new(source, false);

    /// <summary><paramref name="source"/> with every identity but a stick's reading idle.</summary>
    public static StickSplit SticksOnly(IDeviceState source) => new(source, true);

    public bool IsKeyDown(DeviceId device, int keyCode) =>
        Passes(device) && _source.IsKeyDown(device, keyCode);

    public bool IsButtonDown(DeviceId device, int button) =>
        Passes(device) && _source.IsButtonDown(device, button);

    public bool IsMouseButtonDown(DeviceId device, int button) =>
        Passes(device) && _source.IsMouseButtonDown(device, button);

    public float AxisValue(DeviceId device, int axis) =>
        Passes(device) ? _source.AxisValue(device, axis) : 0f;

    public HatDirection HatState(DeviceId device, int hat) =>
        Passes(device) ? _source.HatState(device, hat) : HatDirection.None;

    private bool Passes(DeviceId device) => StickModel.TryFromDevice(device, out _) == _sticks;
}
