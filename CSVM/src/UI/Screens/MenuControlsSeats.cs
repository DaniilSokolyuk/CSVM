using System;
using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.Sticks;
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
    private readonly Func<int, bool, BindingProfile> _saved;
    private readonly List<MenuInput?> _seats = new();
    private readonly Func<StickProfileSet?> _sticks;
    private int _stickRevision = -1;

    /// <summary>Over the shared feature every registration lands in. The saved keymap on the
    /// portable pad placeholder comes from <paramref name="saved"/>, by default
    /// <see cref="LaunchBindings.Profile"/>. Seat 1's stick rows follow <paramref name="sticks"/>,
    /// by default <see cref="StickProfiles.Live"/>.</summary>
    public MenuControlsSeats(
        ControlsFeature controls, Func<int, bool, BindingProfile>? saved = null, Func<StickProfileSet?>? sticks = null)
    {
        _controls = controls ?? throw new ArgumentNullException(nameof(controls));
        _saved = saved ?? ((player, keyboard) => LaunchBindings.Profile(player, PadOf(InputContext.Flight), keyboard));
        _sticks = sticks ?? (() => StickProfiles.Live);
    }

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
                // The stick reader answers only for seat index 0, so every other seat scans no stick.
                int seat = i;
                var devices = new SeatCaptureDevices(PadOf, () => editable.Pads, StickDeviceState.Live(() => seat));
                _controls.AddSeat(i + 1, Profile(i + 1, editable), devices, editable.Keyboard);
            }
        }

        for (int i = _seats.Count - 1; i >= pollers.Count; i--)
        {
            _controls.RemoveSeat(i + 1);
            _seats.RemoveAt(i);
        }

        FollowStickRows();
    }

    /// <summary>Takes every player row off the feature, so the next <see cref="Sync"/> registers
    /// each seat again. A presentation calls it on each activation.
    /// ⚠ The pause leaf registers the same player numbers over the flight's readers. A menu that kept
    /// its entries past a flight would leave the feature on a dead reader's menu map. An Accept there
    /// would save that reader's rows over the stick profile's.</summary>
    public void Forget()
    {
        foreach (int player in new List<int>(_controls.Players))
        {
            _controls.RemoveSeat(player);
        }

        _seats.Clear();
    }

    // Seat 1's stick rows once the profile set moves under a registration, which a save does when it
    // hands the generic default to another stick. A registration keeps its maps across screens, and
    // without this an Accept would write the rows staged before the move into every profile.
    private void FollowStickRows()
    {
        if (_sticks() is not { } set || set.Revision == _stickRevision)
        {
            return;
        }

        _stickRevision = set.Revision;
        if (_seats.Count > 0 && _seats[0] != null)
        {
            _controls.Follow(1, set.MergeInto);
        }
    }

    // One seat's three keymaps. Menu is the poller's own live map, so an accepted rebind there is
    // felt on the next frame. Flight and Camera come from the same saved file their polling sites
    // read at launch, on the portable pad placeholder: opening the screen on the shipped defaults
    // instead would show the player rows they never chose and Accept would write those back.
    private BindingProfile Profile(int player, MenuInput input)
    {
        var saved = _saved(player, input.Keyboard);
        var maps = new Dictionary<InputContext, ActionMap>
        {
            [InputContext.Flight] = saved.Map(InputContext.Flight),
            [InputContext.Menu] = input.Map,
            [InputContext.Camera] = saved.Map(InputContext.Camera),
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
