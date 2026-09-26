using CSVM.UI.Screens;

namespace CSVM.UI.Boards;

/// <summary>
/// A board menu, its rows and the reader that drives them, kept together so a board wires a menu
/// in two lines rather than restating the poll-handle-repaint order five times. The board owns one
/// of these while its menu is up, adds <see cref="View"/> to its body, and calls
/// <see cref="Poll"/> each frame.
/// </summary>
public sealed class BoardMenuHost
{
    private readonly MenuInput _input;

    private BoardMenuHost(BoardMenu menu, BoardMenuView view, MenuInput input)
    {
        Menu = menu;
        View = view;
        _input = input;
    }

    public BoardMenu Menu { get; }

    public BoardMenuView View { get; }

    /// <summary>Builds the rows at the board's own scale and primes the reader, so a button still
    /// held from whatever raised the board is not read as a fresh press on the next frame.
    /// <paramref name="legend"/> is whether the rows carry the seat's control hints under them.</summary>
    public static BoardMenuHost Build(BoardMenu menu, MenuInput input, float s, bool legend)
    {
        input.Prime();
        return new BoardMenuHost(menu, BoardMenuView.Build(menu, s, input, legend), input);
    }

    /// <summary>One frame of the owner's menu input. ⚠ Pass wall time, not sim time: the clock this
    /// menu is holding does not advance, so the cursor's auto-repeat would never fire on sim dt.
    /// Escape and Start reach the pause toggle through FlightController, so only the pad's B is
    /// read as back here; reading the combined back would act twice on one press.</summary>
    public void Poll(float dt) => Poll(dt, null);

    /// <summary>One frame, offered first to <paramref name="first"/>, a second cursor region on the
    /// board (the photographs), which answers whether it took the frame; the rows read it only when
    /// it did not.</summary>
    public void Poll(float dt, System.Func<MenuInput, bool>? first)
    {
        _input.Poll(dt);
        bool taken = first?.Invoke(_input) == true;
        if (!taken && Menu.Handle(_input.Move, _input.Accept, _input.PadBack))
            View.Refresh();

        // A player who reaches for the other device mid-board is shown that device's controls, the
        // same handover the flight prompts follow.
        if (_input.DeviceMoved)
            View.Relegend(_input);
    }
}
