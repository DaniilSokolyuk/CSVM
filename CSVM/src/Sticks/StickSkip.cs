using System;
using CSVM.Bindings;

namespace CSVM.Sticks;

/// <summary>
/// Seat 1's stick half of the cutscene skip: <see cref="InputAction.SkipCutscene"/> read off the
/// active stick profiles' menu rows, with a press edge and the held state. Keys and pads skip
/// through input events. SDL2 sticks raise none, so a cutscene or cinema polls this engine-free
/// reader every frame (<see cref="Live"/>).
/// ⚠ Read the stick rows alone. Every key and pad button already skips through its event. A keymap
/// row read here would be a second path for that press, outside the <c>--no-pads</c> gate.
/// </summary>
public sealed class StickSkip
{
    private readonly Func<StickProfileSet?> _profiles;
    private readonly StickDeviceState _state;
    private readonly ActionMap _rows = new();
    private readonly PlayerActions _actions;
    private int _revision = -1;

    /// <param name="playerIndex">The reading seat's zero-based player index; only seat 1 reads.</param>
    /// <param name="roster">The roster to read, null while sticks are off.</param>
    /// <param name="profiles">The profile set whose menu rows bind the skip, null while sticks are off.</param>
    public StickSkip(Func<int> playerIndex, Func<StickRoster?> roster, Func<StickProfileSet?> profiles)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _state = new StickDeviceState(playerIndex, roster);
        _actions = new PlayerActions(_rows, readsKeyboard: false);
    }

    /// <summary>Whether the skip went down on the last <see cref="Poll"/>, having been up before it.
    /// </summary>
    public bool Pressed { get; private set; }

    /// <summary>Whether the skip was down on the last <see cref="Poll"/>.</summary>
    public bool Held { get; private set; }

    /// <summary>Seat 1's reader over the live roster and profiles, which stay null under
    /// <c>--no-pads</c>, so a scripted run reads no stick.</summary>
    public static StickSkip Live() =>
        new(() => StickDeviceState.OwningSeat, () => StickPump.Roster, () => StickProfiles.Live);

    /// <summary>Reads the sticks and updates <see cref="Pressed"/> and <see cref="Held"/>.</summary>
    public void Poll()
    {
        if (_profiles() is { } set)
        {
            set.MergeIfChanged(_rows, InputContext.Menu, ref _revision);
        }

        _actions.Poll(_state);
        bool held = _actions.Held(InputAction.SkipCutscene);
        Pressed = held && !Held;
        Held = held;
    }

    /// <summary>Takes the current state as the starting point, so a button still held from whatever
    /// opened the screen is not read as a press.</summary>
    public void Prime()
    {
        Poll();
        Pressed = false;
    }
}
