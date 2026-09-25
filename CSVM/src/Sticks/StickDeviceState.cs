using System;
using System.Collections.Generic;
using CSVM.Bindings;

namespace CSVM.Sticks;

/// <summary>
/// The sticks as the binding model reads them: an <see cref="IDeviceState"/> answering for stick
/// identities (<see cref="StickModel.Device"/>) through the roster's per-model reads. L and R
/// therefore stay two devices, while identical units of one model merge. Every stick belongs to
/// seat 1, so any other seat reads nothing. The seat is re-asked on each read, because a flight
/// sets its seat index after building its readers.
/// ⚠ Do not add a gate here; the roster already reads neutral under <see cref="Pads.InputBlocked"/>,
/// the pads' own gate.
/// </summary>
public sealed class StickDeviceState : IDeviceState
{
    /// <summary>The player index that owns every stick: seat 1.</summary>
    public const int OwningSeat = 0;

    private readonly Func<int> _playerIndex;
    private readonly Func<StickRoster?> _roster;

    /// <param name="playerIndex">The reading seat's zero-based player index, re-read per call.</param>
    /// <param name="roster">The roster to read, null when sticks are off.</param>
    public StickDeviceState(Func<int> playerIndex, Func<StickRoster?> roster)
    {
        _playerIndex = playerIndex ?? throw new ArgumentNullException(nameof(playerIndex));
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
    }

    /// <summary>The game's reader for a seat, over the one live roster (<see cref="StickPump.Roster"/>).
    /// </summary>
    public static StickDeviceState Live(Func<int> playerIndex) => new(playerIndex, () => StickPump.Roster);

    public bool IsKeyDown(DeviceId device, int keyCode) => false;

    public bool IsMouseButtonDown(DeviceId device, int button) => false;

    public bool IsButtonDown(DeviceId device, int button) =>
        Reads(device, out var roster, out var model) && roster.ModelButton(model, button);

    public float AxisValue(DeviceId device, int axis) =>
        Reads(device, out var roster, out var model) ? roster.ModelAxis(model, axis) : 0f;

    public HatDirection HatState(DeviceId device, int hat) =>
        Reads(device, out var roster, out var model) ? roster.ModelHat(model, hat) : HatDirection.None;

    /// <summary>The stick identities this seat reads right now, one per connected model, in roster
    /// order. Empty for any seat but seat 1 or with sticks off. Listed while reads are blocked,
    /// since what is connected is a hardware fact.</summary>
    public IReadOnlyList<DeviceId> Devices()
    {
        if (Roster() is not { } roster)
        {
            return Array.Empty<DeviceId>();
        }

        var devices = new List<DeviceId>(roster.Sticks.Count);
        foreach (var stick in roster.Sticks)
        {
            var device = stick.Model.Device;
            if (!devices.Contains(device))
            {
                devices.Add(device);
            }
        }

        return devices;
    }

    private StickRoster? Roster() => _playerIndex() == OwningSeat ? _roster() : null;

    private bool Reads(DeviceId device, out StickRoster roster, out StickModel model)
    {
        roster = null!;
        if (!StickModel.TryFromDevice(device, out model) || Roster() is not { } live)
        {
            return false;
        }

        roster = live;
        return true;
    }
}
