using CSVM.Flight.Modes;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// Instant Action's wrap-up board, <see cref="ResultsBoard"/>'s shell, the whole window on its
/// own <c>CanvasLayer</c>, since the mission ends for every human at once. Shows the shipped
/// screen's four rows, Time to Complete Mission, Enemies Shot Down, Danger Zones Completed,
/// Shot %, the ones <c>IA_WRAPUP.SCRIPT</c> and <c>LAYOUT.CSV</c> actually wire. On a
/// <c>stunt_flying</c> mission it also carries the run's <see cref="StuntSplits"/> section and
/// player 1's <see cref="StuntShotStrip"/>, so one
/// board covers the mission rather than stacking with <see cref="StuntScoreboard"/>. Decode:
/// docs/formats/instant-action/wrap-up.md. Every value is handed in by the caller at
/// <see cref="Present"/> time rather than read live off any source, the way
/// <see cref="VersusBoard"/> draws off a <see cref="VersusMatch.Standings"/> snapshot.
/// </summary>
public sealed partial class IaWrapupBoard : ResultsBoard
{
    // Base metrics at 720p (scaled by window height). All TUNE, mirrors VersusBoard/StuntRaceBoard
    // so every shared results board reads as the same screen.
    private const int TitleFont = 30;
    private const int ContextFont = 15;
    private const int RowFont = 20;

    // The shipped row titles, langui ids 1134-1137 (docs/formats/instant-action.md "The wrap-up
    // screen"), literal text, not read off ui_strings.json at runtime: that table is a build-time
    // extraction artifact of the .rof archive, not one of the five archives a session build opens
    // (Mech3/SessionArchives.cs), and six lines are stable enough not to earn a reader of their
    // own here.
    private const string TimeTitle = "Time to Complete Mission";
    private const string DestroyedTitle = "Enemies Shot Down";
    private const string ZonesTitle = "Danger Zones Completed";
    private const string ShotsTitle = "Shot %";

    private static readonly Color WonColor = new(0.55f, 0.92f, 0.62f);
    private static readonly Color LostColor = new(0.92f, 0.45f, 0.45f);

    private string _context = "";

    // A mission that has ended stays ended: no live flag ever clears, so the shell never retires
    // this board, only OnRestartChosen below does.
    protected override bool StillEnded => true;

    /// <summary>Builds the (hidden) board. Add it to a <c>CanvasLayer</c> above the splitscreen
    /// panes; the caller calls <see cref="Present"/> once, from the mission runtime's
    /// <c>MissionEnded</c>.</summary>
    public static IaWrapupBoard Build(string context, bool exitsToMenu, PauseState state,
        System.Func<int, MenuInput> inputFor)
    {
        var board = new IaWrapupBoard { _context = context };
        board.InitShell(state, exitsToMenu, inputFor);
        return board;
    }

    /// <summary>The wrap-up's "Time to Complete Mission" row, in the decoded <c>IDS_IAWU_TIME</c>
    /// format <c>%02d:%02d</c> (docs/formats/instant-action.md "What the four numbers count").
    /// Minutes are <c>ms / 60000</c> and seconds <c>(ms / 1000) % 60</c>, both truncating. Takes
    /// the mission clock's own unit, seconds, and converts to milliseconds itself. Shared with the
    /// Original presentation's wrap-up page.</summary>
    public static string FormatElapsed(float elapsedSeconds)
    {
        int ms = (int)(elapsedSeconds * 1000f);
        int minutes = ms / 60000;
        int seconds = ms / 1000 % 60;
        return $"{minutes:00}:{seconds:00}";
    }

    /// <summary>Shows the board with the mission's four final counters
    /// (docs/formats/instant-action.md "What the four numbers count"). Called exactly once, from
    /// the mission runtime's <c>MissionEnded</c>, every value here is that instant's
    /// snapshot, the same discipline <see cref="VersusBoard"/> takes from
    /// <see cref="VersusMatch.Standings"/>. <paramref name="shots"/>, player 1's camera, is the
    /// exception: its strip under the splits fills in as late frames land.</summary>
    public void Present(bool won, float elapsedSeconds, int enemiesShotDown, int zonesCompleted,
        int shotPercent, StuntSummary? stunt = null, StuntCapture? shots = null)
    {
        Populate(won, elapsedSeconds, enemiesShotDown, zonesCompleted, shotPercent, stunt, shots);
        Wake();
    }

    // Nothing else retires this board, so unlike the race and dogfight boards the hide and the
    // clock release happen here rather than on a live flag.
    protected override void OnRestartChosen()
    {
        Retire();
        Restart?.Invoke();
    }

    private void Populate(bool won, float elapsedSeconds, int enemiesShotDown, int zonesCompleted,
        int shotPercent, StuntSummary? stunt, StuntCapture? shots)
    {
        float s = BoardScale();
        var body = BeginPanel(s);

        // The shipped screen has no outcome text; the original never lost a mission with lives
        // to run out. This stands in as the headline, in VersusBoard's shape.
        string title = won ? "MISSION COMPLETE" : "MISSION FAILED";
        var titleColor = won ? WonColor : LostColor;
        body.AddChild(Centered(Label(title, (int)(TitleFont * s), titleColor)));
        body.AddChild(Centered(Label(_context, (int)(ContextFont * s), ContextColor)));
        body.AddChild(Separator(s));

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", Mathf.RoundToInt(26f * s));
        grid.AddThemeConstantOverride("v_separation", Mathf.RoundToInt(6f * s));
        body.AddChild(grid);

        int labelW = (int)(280f * s), valueW = (int)(90f * s);
        int font = (int)(RowFont * s);
        AddCell(grid, TimeTitle, font, RowColor, HorizontalAlignment.Left, labelW);
        AddCell(grid, FormatElapsed(elapsedSeconds), font, RowColor,
            HorizontalAlignment.Right, valueW);
        AddCell(grid, DestroyedTitle, font, RowColor, HorizontalAlignment.Left, labelW);
        AddCell(grid, enemiesShotDown.ToString(), font, RowColor, HorizontalAlignment.Right, valueW);
        AddCell(grid, ZonesTitle, font, RowColor, HorizontalAlignment.Left, labelW);
        AddCell(grid, zonesCompleted.ToString(), font, RowColor, HorizontalAlignment.Right, valueW);
        AddCell(grid, ShotsTitle, font, RowColor, HorizontalAlignment.Left, labelW);
        AddCell(grid, $"{shotPercent}%", font, RowColor, HorizontalAlignment.Right, valueW);

        // Player 1's run, the board is shared, and splitscreen pilots each fly their own copy of
        // the zone set.
        if (stunt is { } run)
        {
            body.AddChild(Separator(s));
            StuntSplits.Add(body, run, s, separatorBeforeTotal: false);
            AddShotStrip(body, shots, s);
        }

        body.AddChild(Separator(s));
        AddStandardMenu(body, s);
    }
}
