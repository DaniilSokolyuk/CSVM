using CSVM.Flight.Modes;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The shared results board for a splitscreen stunt race, <see cref="ResultsBoard"/>'s shell.
/// Where the single-player <see cref="StuntScoreboard"/> shows one pilot's per-zone splits inside
/// their own pane, this ranks the whole field and covers the entire window on its own CanvasLayer
/// over the splitscreen panes, because the race ends for everybody at once. One row per player in
/// finishing order: placing, colour-coded tag, aircraft, zones cleared and total time. R and pad Y
/// still reach the rematch directly. Construction detail: this module's entry in
/// docs/architecture.md.
/// ⚠ Best times are deliberately not recorded here (<see cref="ScoreStore"/> is single-player
/// only): race totals aren't comparable across player counts.</summary>
public sealed partial class StuntRaceBoard : ResultsBoard
{
    // Base metrics at 720p (scaled by window height). All TUNE, mirrors StuntScoreboard so the
    // solo and race boards read as the same screen.
    private const int TitleFont = 26;
    private const int ContextFont = 15;
    private const int HeaderFont = 14;
    private const int RowFont = 18;

    private StuntRace _race = null!;
    private string _context = "";

    // A rematch clears the placings, which the shell's _Process turns into the hide and the release.
    protected override bool StillEnded => _race.AllFinished;

    /// <summary>Builds the (hidden) board and subscribes to the race's completion. Add it to a
    /// CanvasLayer above the splitscreen panes; it wakes itself on
    /// <see cref="StuntRace.RaceCompleted"/> and retires on a rematch.</summary>
    public static StuntRaceBoard Build(StuntRace race, string context, bool exitsToMenu,
        PauseState state, System.Func<int, MenuInput> inputFor)
    {
        var board = new StuntRaceBoard { _race = race, _context = context };
        board.InitShell(state, exitsToMenu, inputFor);
        race.RaceCompleted += board.OnRaceCompleted;
        return board;
    }

    public override void _ExitTree() => _race.RaceCompleted -= OnRaceCompleted;

    private void OnRaceCompleted()
    {
        // Log the final order too, so a race is reviewable from a headless run's log.
        Log.Info("flight", $"stunt race results:");
        foreach (var r in _race.Standings())
            Log.Info("flight",
                $"  {StuntRace.Ordinal(r.Rank)}  {r.Tag}  {r.PlaneDisplay}  {StuntMission.FormatTime(r.FinishTime)}");
        Populate();
        Wake();
    }

    private void Populate()
    {
        float s = BoardScale();
        var body = BeginPanel(s);

        body.AddChild(Centered(Label("STUNT RACE RESULTS", (int)(TitleFont * s), TitleColor)));
        body.AddChild(Centered(Label(_context, (int)(ContextFont * s), ContextColor)));
        body.AddChild(Separator(s));

        // One row per player: placing | tag | aircraft | zones | total (+ gap to the winner).
        var grid = new GridContainer { Columns = 5 };
        grid.AddThemeConstantOverride("h_separation", Mathf.RoundToInt(22f * s));
        grid.AddThemeConstantOverride("v_separation", Mathf.RoundToInt(6f * s));
        body.AddChild(grid);

        int rankW = (int)(52f * s), tagW = (int)(46f * s), planeW = (int)(180f * s),
            zonesW = (int)(76f * s), timeW = (int)(120f * s);
        AddCell(grid, "", (int)(HeaderFont * s), HeaderColor, HorizontalAlignment.Left, rankW);
        AddCell(grid, "", (int)(HeaderFont * s), HeaderColor, HorizontalAlignment.Left, tagW);
        AddCell(grid, "AIRCRAFT", (int)(HeaderFont * s), HeaderColor, HorizontalAlignment.Left, planeW);
        AddCell(grid, "ZONES", (int)(HeaderFont * s), HeaderColor, HorizontalAlignment.Right, zonesW);
        AddCell(grid, "TOTAL", (int)(HeaderFont * s), HeaderColor, HorizontalAlignment.Right, timeW);

        float winnerTime = 0f;
        bool haveWinner = false;
        foreach (var r in _race.Standings())
        {
            bool won = r.Finished && r.Rank == 1;
            if (won)
            {
                winnerTime = r.FinishTime;
                haveWinner = true;
            }
            // The winner's row wears their own identity colour; everyone else stays neutral so the
            // placing reads at a glance.
            var color = won ? r.Color : RowColor;
            int font = (int)(RowFont * s);
            AddCell(grid, r.Finished ? StuntRace.Ordinal(r.Rank) : "-", font, color, HorizontalAlignment.Left, rankW);
            AddCell(grid, r.Tag, font, r.Color, HorizontalAlignment.Left, tagW);
            AddCell(grid, r.PlaneDisplay, font, color, HorizontalAlignment.Left, planeW);
            AddCell(grid, $"{r.Mission.CompletedCount}/{r.Mission.TotalCount}", font, color,
                HorizontalAlignment.Right, zonesW);
            string time = r.Finished
                ? StuntMission.FormatTime(r.FinishTime)
                    + (haveWinner && !won ? $"  (+{StuntMission.FormatTime(r.FinishTime - winnerTime)})" : "")
                : "DNF";
            AddCell(grid, time, font, color, HorizontalAlignment.Right, timeW);
        }

        body.AddChild(Separator(s));
        AddStandardMenu(body, s);
    }
}
