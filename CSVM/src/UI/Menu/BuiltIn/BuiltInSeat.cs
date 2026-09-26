using CSVM.UI.Screens;

namespace CSVM.UI.Menu.BuiltIn;

/// <summary>
/// Seat 0's input source for the Built-in presentation: the keyboard plus every unclaimed pad,
/// read through the launchscreen's own raw poller and translated into one frame of semantic
/// commands. The host owns the seat for the life of the process; <see cref="LaunchMenu"/> is
/// handed the same <see cref="MenuInput"/> so its pad bookkeeping (claiming, joining, hotplug)
/// keeps binding the devices behind the seat until the shared player setup owns them.
/// </summary>
public sealed class BuiltInSeat : IMenuInputSource
{
    /// <summary>A seat over <paramref name="input"/>, which stays the launchscreen's to bind.</summary>
    public BuiltInSeat(MenuInput input)
    {
        Input = input ?? throw new System.ArgumentNullException(nameof(input));
    }

    /// <summary>The raw poller behind the seat.</summary>
    public MenuInput Input { get; }

    public string DeviceLabel => Input.DeviceLabel;

    public bool CapturingText
    {
        get => Input.TextEntry;
        set => Input.TextEntry = value;
    }

    public MenuCommands Poll(float dt)
    {
        Input.Poll(dt);
        return new MenuCommands
        {
            MoveY = Input.Move,
            MoveX = Input.MoveX,
            Accept = Input.Accept,
            Back = Input.Back,
            Join = Input.Start,
            Loadout = Input.Loadout,
            Contents = Input.Presets,
            Typed = Input.Typed,
            Erase = Input.Erase,
        };
    }

    public void Prime() => Input.Prime();
}
