using System.Collections.Generic;
using CSVM.Sticks;

namespace CSVM.Tests;

/// <summary>
/// An in-memory stick library for <see cref="StickRoster"/> tests: devices are plugged and
/// unplugged by hand, and each control holds whatever value the test sets.
/// </summary>
internal sealed class FakeStickNative : IStickNative
{
    private readonly List<Stick> _devices = new();
    private readonly Dictionary<(int, int), short> _axes = new();
    private readonly HashSet<(int, int)> _buttons = new();
    private readonly Dictionary<(int, int), byte> _hats = new();
    private bool _plugged;

    public HashSet<int> Opened { get; } = new();

    public HashSet<int> Refuse { get; } = new();

    public int ListCalls { get; private set; }

    public bool Disposed { get; private set; }

    public string Version => "2.32.10";

    public string LastError => "refused";

    public void Plug(int instance, string name, StickModel model, int axes = 8, int buttons = 128, int hats = 1)
    {
        _devices.Add(new Stick(instance, name, model, "guid" + instance, axes, buttons, hats));
        _plugged = true;
    }

    public void Unplug(int instance)
    {
        _devices.RemoveAll(d => d.Instance == instance);
        _plugged = true;
    }

    public void Set(int instance, int axis, short raw, int button, byte hat)
    {
        _axes[(instance, axis)] = raw;
        _buttons.Add((instance, button));
        _hats[(instance, 0)] = hat;
    }

    public void SetAxis(int instance, int axis, short raw) => _axes[(instance, axis)] = raw;

    public void Press(int instance, int button) => _buttons.Add((instance, button));

    public void Release(int instance, int button) => _buttons.Remove((instance, button));

    public void SetHat(int instance, int hat, byte bits) => _hats[(instance, hat)] = bits;

    public bool Pump()
    {
        bool plugged = _plugged;
        _plugged = false;
        return plugged;
    }

    public IReadOnlyList<StickListing> List()
    {
        ListCalls++;
        return _devices.ConvertAll(d => new StickListing(d.Instance, d.Name, d.Model, d.Guid));
    }

    public Stick? Open(StickListing listing)
    {
        var device = _devices.Find(d => d.Instance == listing.Instance);
        if (device is null || Refuse.Contains(listing.Instance))
        {
            return null;
        }

        Opened.Add(listing.Instance);
        return device;
    }

    public void Close(int instance) => Opened.Remove(instance);

    public short Axis(int instance, int axis) => _axes.GetValueOrDefault((instance, axis));

    public bool Button(int instance, int button) => _buttons.Contains((instance, button));

    public byte Hat(int instance, int hat) => _hats.GetValueOrDefault((instance, hat));

    public void Dispose() => Disposed = true;
}
