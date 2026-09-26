using System.Collections.Generic;
using CSVM.Flight.Modes;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>One stunt run's numbers for a board's split table: the run itself, its total and how
/// it compares to the stored best. Read at run end, when the run is over and the clock is
/// halted.</summary>
public readonly record struct StuntSummary(StuntMission Mission, float Total, float? PrevBest, bool NewBest);

/// <summary>
/// The stunt run's splits section, shared by <see cref="StuntScoreboard"/> and
/// <see cref="IaWrapupBoard"/>: the zone table (name, split, cumulative, in the order flown, with
/// em-dash rows for zones never reached), the total, and the ★ NEW BEST / BEST comparison line.
/// </summary>
public static class StuntSplits
{
    /// <summary>The word that opens the total's line in <see cref="Lines"/>, so a page that already
    /// shows the mission clock can leave that line out.</summary>
    public const string TotalLabel = "TOTAL";

    // Section metrics at 720p (scaled by the board's own scale). All TUNE.
    private const int HeaderFont = 14;
    private const int RowFont = 17;
    private const int TotalFont = 23;
    private const int BestFont = 16;

    private static readonly Color TotalColor = new(0.96f, 0.98f, 1f);
    private static readonly Color BestColor = new(0.60f, 0.75f, 0.95f);
    private static readonly Color NewBestColor = new(1f, 0.82f, 0.28f);

    /// <summary>The same table as flat text, one line per zone and then the total and the best
    /// comparison. What a run hands a menu page: the summary holds a live mission object, which
    /// cannot outlive the session, and these strings can.</summary>
    public static IReadOnlyList<string> Lines(StuntSummary run)
    {
        var lines = new List<string>();
        float prev = 0f;
        int n = 1;
        foreach (var z in run.Mission.InCompletionOrder())
        {
            string name = z.Description.Length > 0 ? z.Description
                : z.MarkerText().Length > 0 ? z.MarkerText() : z.DzName;
            lines.Add(z.Completed
                ? $"{n}.  {name}   {StuntMission.FormatTime(z.CompletedAt - prev)}   {StuntMission.FormatTime(z.CompletedAt)}"
                : $"{n}.  {name}   -   -");
            if (z.Completed)
            {
                prev = z.CompletedAt;
            }

            n++;
        }

        lines.Add($"{TotalLabel}   {StuntMission.FormatTime(run.Total)}");
        if (run.NewBest)
        {
            lines.Add(run.PrevBest is { } was
                ? $"NEW BEST   (was {StuntMission.FormatTime(was)})"
                : "NEW BEST");
        }
        else if (run.PrevBest is { } stored)
        {
            lines.Add($"BEST   {StuntMission.FormatTime(stored)}");
        }

        return lines;
    }

    /// <summary>Appends the section to <paramref name="body"/> at scale <paramref name="s"/>.
    /// <paramref name="separatorBeforeTotal"/> keeps the two boards' shipped layouts: the
    /// scoreboard rules off its total, the wrap-up board runs the table straight into it.</summary>
    public static void Add(VBoxContainer body, StuntSummary run, float s, bool separatorBeforeTotal)
    {
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", Mathf.RoundToInt(26f * s));
        grid.AddThemeConstantOverride("v_separation", Mathf.RoundToInt(5f * s));
        body.AddChild(grid);

        int nameW = (int)(280f * s), timeW = (int)(96f * s);
        int header = (int)(HeaderFont * s), font = (int)(RowFont * s);
        ResultsBoard.AddCell(grid, "ZONE", header, ResultsBoard.HeaderColor, HorizontalAlignment.Left, nameW);
        ResultsBoard.AddCell(grid, "SPLIT", header, ResultsBoard.HeaderColor, HorizontalAlignment.Right, timeW);
        ResultsBoard.AddCell(grid, "TIME", header, ResultsBoard.HeaderColor, HorizontalAlignment.Right, timeW);

        float prev = 0f;
        int n = 1;
        foreach (var z in run.Mission.InCompletionOrder())
        {
            string name = $"{n}.  " + (z.Description.Length > 0 ? z.Description
                : z.MarkerText().Length > 0 ? z.MarkerText() : z.DzName);
            ResultsBoard.AddCell(grid, name, font, ResultsBoard.RowColor, HorizontalAlignment.Left, nameW);
            if (z.Completed)
            {
                ResultsBoard.AddCell(grid, StuntMission.FormatTime(z.CompletedAt - prev), font,
                    ResultsBoard.RowColor, HorizontalAlignment.Right, timeW);
                ResultsBoard.AddCell(grid, StuntMission.FormatTime(z.CompletedAt), font,
                    ResultsBoard.RowColor, HorizontalAlignment.Right, timeW);
                prev = z.CompletedAt;
            }
            else
            {
                ResultsBoard.AddCell(grid, "-", font, ResultsBoard.HeaderColor, HorizontalAlignment.Right, timeW);
                ResultsBoard.AddCell(grid, "-", font, ResultsBoard.HeaderColor, HorizontalAlignment.Right, timeW);
            }
            n++;
        }

        if (separatorBeforeTotal)
            body.AddChild(ResultsBoard.Separator(s));
        body.AddChild(ResultsBoard.Centered(ResultsBoard.Label(
            $"TOTAL   {StuntMission.FormatTime(run.Total)}", (int)(TotalFont * s), TotalColor)));

        if (run.NewBest)
        {
            string best = run.PrevBest is { } was
                ? $"★  NEW BEST   (was {StuntMission.FormatTime(was)})"
                : "★  NEW BEST";
            body.AddChild(ResultsBoard.Centered(ResultsBoard.Label(best, (int)(BestFont * s), NewBestColor)));
        }
        else if (run.PrevBest is { } stored)
        {
            body.AddChild(ResultsBoard.Centered(ResultsBoard.Label(
                $"BEST   {StuntMission.FormatTime(stored)}", (int)(BestFont * s), BestColor)));
        }
    }
}
