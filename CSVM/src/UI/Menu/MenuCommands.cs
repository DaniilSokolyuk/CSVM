namespace CSVM.UI.Menu;

/// <summary>
/// One seat's menu input source: whatever devices feed that seat, polled once per frame into
/// semantic commands. Not synonymous with a pad. A source may read the keyboard plus every
/// unclaimed pad, one claimed pad, a mouse, or later a HOTAS/HOSAS binding, and a presentation
/// cannot tell which; device knowledge stays on this side of the seam.
/// </summary>
public interface IMenuInputSource
{
    /// <summary>What drives this seat, for join strips and device labels.</summary>
    string DeviceLabel { get; }

    /// <summary>Whether the seat's typed characters currently feed a text field. While true the
    /// source itself silences its letter aliases, so typing a name cannot walk the cursor.</summary>
    bool CapturingText { get; set; }

    /// <summary>Reads the seat's devices and returns this frame's commands.</summary>
    MenuCommands Poll(float dt);

    /// <summary>Seeds edge detection from the current device state, so a button still held from
    /// whatever showed the menu is not read as a fresh press on the next frame.</summary>
    void Prime();
}

/// <summary>The pointer half of one seat's commands, in window pixels; the active presentation
/// maps it into its own canvas (the Original presentation through <see cref="Boards.BoardFit"/>). A
/// source with no pointer reports null, not a zeroed position. <paramref name="Wheel"/> is the
/// wheel's steps this frame, positive toward a list's foot; a presentation turns it into list
/// steps over whatever list the pointer stands on. <paramref name="RightPressed"/> is the
/// secondary button as a held state and not an edge, which is all the one screen that reads it
/// needs; it drives no command of its own.</summary>
public readonly record struct MenuPointer(
    float X, float Y, bool Pressed, bool Clicked, int Wheel = 0, bool RightPressed = false);

/// <summary>
/// One frame of one seat's semantic menu commands, already device-neutral: cursor steps arrive
/// with auto-repeat applied, presses are edges, text arrives as typed characters. A presentation
/// reads meaning (accept, back, join) and never a key, button or axis, which is what lets
/// keyboard, mouse, pad and a future flight-control setup share one contract.
/// </summary>
public sealed record MenuCommands
{
    /// <summary>The idle frame: no movement, no presses, nothing typed, no pointer.</summary>
    public static readonly MenuCommands None = new();

    /// <summary>Vertical cursor step: −1 up, +1 down, 0 none.</summary>
    public int MoveY { get; init; }

    /// <summary>Horizontal cursor or stepper step: −1 left, +1 right, 0 none.</summary>
    public int MoveX { get; init; }

    /// <summary>Confirm the focused thing (edge).</summary>
    public bool Accept { get; init; }

    /// <summary>Leave the current screen or modal (edge).</summary>
    public bool Back { get; init; }

    /// <summary>The join gesture from a device this seat could claim (edge).</summary>
    public bool Join { get; init; }

    /// <summary>Open the loadout for whatever the screen is about (edge).</summary>
    public bool Loadout { get; init; }

    /// <summary>Open the screen's contents list, the Instant Action presets today (edge).</summary>
    public bool Contents { get; init; }

    /// <summary>Characters typed this frame while the seat is capturing text, else "".</summary>
    public string Typed { get; init; } = string.Empty;

    /// <summary>Delete one character of captured text (edge).</summary>
    public bool Erase { get; init; }

    /// <summary>This seat's pointer, or null when its devices have none.</summary>
    public MenuPointer? Pointer { get; init; }
}
