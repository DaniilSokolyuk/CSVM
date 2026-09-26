using System.Globalization;
using System.Linq;
using CSVM.Flight.Modes;
using CSVM.UI.Boards;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The shared results board for splitscreen "Dogfight", <see cref="ResultsBoard"/>'s shell: the
/// match ends for everybody at once, so this covers the WHOLE window on its own CanvasLayer over
/// the splitscreen panes, not a per-pane overlay. Winner (or "DRAW" on a tie) on top, then one
/// ranked row per player, tag, score, kills, deaths, in their own identity colour; the winner's row is
/// highlighted the same way the race board highlights first place. R and pad Y still reach the
/// rematch directly, for the muscle memory and for the hold-test harness.
/// </summary>
public sealed partial class VersusBoard : ResultsBoard
{
    // Base metrics at 720p (scaled by window height). All TUNE, mirrors StuntRaceBoard so the
    // two shared boards read as the same screen.
    private const int TitleFont = 30;
    private const int ContextFont = 15;
    private const int HeaderFont = 14;
    private const int RowFont = 20;

    private VersusMatch _match = null!;
    private string _context = "";

    // A rematch clears Completed, which the shell's _Process turns into the hide and the release.
    protected override bool StillEnded => _match.Completed;

    /// <summary>Builds the (hidden) board and subscribes to the match's completion. Add it to a
    /// CanvasLayer above the splitscreen panes; it wakes itself on
    /// <see cref="VersusMatch.MatchCompleted"/> and retires on a rematch.</summary>
    public static VersusBoard Build(VersusMatch match, string context, bool exitsToMenu,
        PauseState state, System.Func<int, MenuInput> inputFor)
    {
        var board = new VersusBoard { _match = match, _context = context };
        board.InitShell(state, exitsToMenu, inputFor);
        match.MatchCompleted += board.OnMatchCompleted;
        return board;
    }

    public override void _ExitTree() => _match.MatchCompleted -= OnMatchCompleted;

    private void OnMatchCompleted()
    {
        // Log the final standings too, so a match is reviewable from a headless run's log.
        Log.Info("flight", $"dogfight results:");
        foreach (var st in _match.Standings())
            Log.Info("flight",
                $"  #{st.Rank}  {SplitScreen.PlayerTag(st.PlayerIndex)}  {st.Score} pts  {st.Kills}K/{st.Deaths}D");
        Populate();
        Wake();
    }

    private void Populate()
    {
        float s = BoardScale();
        var body = BeginPanel(s);

        // Captured once here so a later Restart() zeroing the live match never rebuilds these
        // already-drawn labels.
        var standings = _match.Standings().ToList();
        var winners = standings.Where(st => st.Rank == 1).ToList();
        string title = winners.Count == 1 ? $"{SplitScreen.PlayerTag(winners[0].PlayerIndex)} WINS" : "DRAW";
        var titleColor = winners.Count == 1 ? SplitScreen.PlayerColor(winners[0].PlayerIndex) : TitleColor;

        body.AddChild(Centered(Label(title, (int)(TitleFont * s), titleColor)));
        body.AddChild(Centered(Label(_context, (int)(ContextFont * s), ContextColor)));
        body.AddChild(Separator(s));

        // One row per player: placing | tag | score | kills | deaths. Score is the ranked number
        // and kills alone do not explain it, a death with no killer costs a point.
        var grid = new GridContainer { Columns = 5 };
        grid.AddThemeConstantOverride("h_separation", Mathf.RoundToInt(26f * s));
        grid.AddThemeConstantOverride("v_separation", Mathf.RoundToInt(6f * s));
        body.AddChild(grid);

        int rankW = (int)(52f * s), tagW = (int)(56f * s), scoreW = (int)(80f * s);
        int killsW = (int)(80f * s), deathsW = (int)(80f * s);
        AddCell(grid, "", (int)(HeaderFont * s), HeaderColor, HorizontalAlignment.Left, rankW);
        AddCell(grid, "", (int)(HeaderFont * s), HeaderColor, HorizontalAlignment.Left, tagW);
        AddCell(grid, "SCORE", (int)(HeaderFont * s), HeaderColor, HorizontalAlignment.Right, scoreW);
        AddCell(grid, "KILLS", (int)(HeaderFont * s), HeaderColor, HorizontalAlignment.Right, killsW);
        AddCell(grid, "DEATHS", (int)(HeaderFont * s), HeaderColor, HorizontalAlignment.Right, deathsW);

        foreach (var st in standings)
        {
            bool won = st.Rank == 1 && winners.Count == 1;
            // The winner's row wears their own identity colour; everyone else stays neutral so
            // the placing reads at a glance, same rule StuntRaceBoard's winner row follows.
            var color = won ? SplitScreen.PlayerColor(st.PlayerIndex) : RowColor;
            int font = (int)(RowFont * s);
            AddCell(grid, $"#{st.Rank}", font, color, HorizontalAlignment.Left, rankW);
            AddCell(grid, SplitScreen.PlayerTag(st.PlayerIndex), font, SplitScreen.PlayerColor(st.PlayerIndex),
                HorizontalAlignment.Left, tagW);
            AddCell(grid, st.Score.ToString(CultureInfo.InvariantCulture), font, color, HorizontalAlignment.Right, scoreW);
            AddCell(grid, st.Kills.ToString(CultureInfo.InvariantCulture), font, color, HorizontalAlignment.Right, killsW);
            AddCell(grid, st.Deaths.ToString(CultureInfo.InvariantCulture), font, color, HorizontalAlignment.Right, deathsW);
        }

        body.AddChild(Separator(s));
        AddStandardMenu(body, s);
    }
}
