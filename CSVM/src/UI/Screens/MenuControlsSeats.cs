using System;
using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.UI.Menu;

namespace CSVM.UI.Screens;

/// <summary>
/// The rebinding screen's seat bookkeeping for any presentation: which joined seats the shared
/// <see cref="ControlsFeature"/> offers a keymap for, which pad identity each context's rows sit
/// on, and the three maps a seat is staged from. A presentation hands it this frame's pollers and
/// keeps none of the detail itself, so both screens register seats the same way and one keymap
/// file belongs to one player number whichever presentation edited it.
/// </summary>
public sealed class MenuControlsSeats
{
    private readonly ControlsFeature _controls;
    private readonly List<MenuInput?> _seats = new();

    /// <summary>Over the shared feature every registration lands in.</summary>
    public MenuControlsSeats(ControlsFeature controls) =>
        _controls = controls ?? throw new ArgumentNullException(nameof(controls));

    /// <summary>Which pad identity a context's rows sit on, and therefore which one a captured
    /// control is stamped with and which one the seat's capture reader answers for. The menu
    /// poller's map is authored on its own seat placeholder and the other two on the portable one.
    /// ⚠ One function feeds all three uses. A capture on an identity the map does not use hides the
    /// conflict from the steal rule, and a reader on an identity the capture does not use reads
    /// every pad as false (<see cref="ICaptureDevices"/>).</summary>
    public static DeviceId PadOf(InputContext context) =>
        context == InputContext.Menu ? MenuInput.SeatPads : DefaultBindings.AnyPad;

    /// <summary>Puts the feature's player rows in step with <paramref name="pollers"/>, one entry
    /// per joined seat in player order and null where a seat has no poller. A registration is kept
    /// while the seat behind its number is the same poller, so a rebind already staged survives; a
    /// number that changed hands is registered again, since the number is what picks the keymap
    /// file. A seat with nothing to press gets no row at all.</summary>
    public void Sync(IReadOnlyList<MenuInput?> pollers)
    {
        ArgumentNullException.ThrowIfNull(pollers);
        while (_seats.Count < pollers.Count)
        {
            _seats.Add(null);
        }

        for (int i = 0; i < pollers.Count; i++)
        {
            // Nothing to press means no row: the chooser must never offer a player who cannot
            // capture. A null pad list is the in-session "every connected pad" reading, still a
            // device, so only a keyboard-less empty list is device-less.
            var input = pollers[i];
            var editable = input != null && (input.Keyboard || input.Pads is not { Length: 0 }) ? input : null;
            if (ReferenceEquals(_seats[i], editable))
            {
                continue;
            }

            _seats[i] = editable;
            if (editable == null)
            {
                _controls.RemoveSeat(i + 1);
            }
            else
            {
                _controls.AddSeat(i + 1, Profile(i + 1, editable), new SeatCaptureDevices(PadOf, () => editable.Pads), editable.Keyboard);
            }
        }

        for (int i = _seats.Count - 1; i >= pollers.Count; i--)
        {
            _controls.RemoveSeat(i + 1);
            _seats.RemoveAt(i);
        }
    }

    // One seat's three keymaps. Menu is the poller's own live map, so an accepted rebind there is
    // felt on the next frame. Flight and Camera come from the same saved file their polling sites
    // read at launch, on the portable pad placeholder: opening the screen on the shipped defaults
    // instead would show the player rows they never chose and Accept would write those back.
    private static BindingProfile Profile(int player, MenuInput input)
    {
        var saved = LaunchBindings.Profile(player, PadOf(InputContext.Flight), input.Keyboard);
        var maps = new Dictionary<InputContext, ActionMap>
        {
            [InputContext.Flight] = saved.Map(InputContext.Flight),
            [InputContext.Menu] = input.Map,
            [InputContext.Camera] = LaunchBindings.Map(player, InputContext.Camera, PadOf(InputContext.Camera), input.Keyboard),
        };
        // The flying scheme and its sensitivity ride with the flight rows they compete with, off the
        // same read.
        return new BindingProfile(maps, input.Keyboard)
        {
            MouseFlying = saved.MouseFlying,
            MouseSensitivity = saved.MouseSensitivity,
        };
    }
}
