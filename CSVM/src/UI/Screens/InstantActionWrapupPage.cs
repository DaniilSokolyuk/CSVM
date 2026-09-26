using System.Collections.Generic;
using System.Globalization;
using CSVM.Flight.Modes;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Menu;

namespace CSVM.UI.Screens;

/// <summary>One yellow post-it on the wrap-up page: its top-left and size in authored pixels and the
/// further lines written on it, in order.</summary>
public sealed record WrapupPostIt(float X, float Y, float Width, float Height, IReadOnlyList<string> Lines);

/// <summary>One Danger Zone photograph on the wrap-up page: its print's top-left and size in
/// authored pixels, border included, and the shot it shows. A shot whose thumbnail has not landed
/// yet keeps its print, empty, until it does.</summary>
public sealed record WrapupPrint(float X, float Y, float Width, float Height, StuntShot Shot);

/// <summary>
/// The Original presentation's Instant Action wrap-up page,
/// <c>docs/formats/instant-action/wrap-up.md</c>. It composes <c>[@IA_WrapUp@]</c>'s own rows: the
/// magazine background, the four brushstroke panes, the screen title and the four title/value
/// pairs. Three pieces of remake furniture stand in the space below them. The first is yellow
/// post-its carrying the further lines the shipped page has no row for: the context line and a
/// stunt run's splits. A stunt run's photographs sit to the left of the post-its, and a tick box
/// above CONTINUE says whether the mission was won. Row titles are literal strings, following
/// <see cref="IaWrapupBoard"/>'s precedent, and positions come through the
/// <see cref="CampaignLayout"/> a caller hands in.
/// </summary>
public static class InstantActionWrapupPage
{
    /// <summary>The layout section the page is composed from.</summary>
    public const string Section = CampaignLayout.WrapupSection;

    /// <summary>The full-page magazine art under everything.</summary>
    public const string BackgroundKey = "IAWU_BACKGROUND";

    /// <summary>The CONTINUE plaque, back to the Instant Action screen.</summary>
    public const string ContinueKey = "IAWU_B_CONTINUE";

    /// <summary>The page's own heading row.</summary>
    public const string TitleKey = "IAWU_T_TITLE";

    /// <summary>The heading's face, against its 40-pixel authored box.</summary>
    public const float TitleFont = 26f;

    /// <summary>A row title's face, against the 20-pixel boxes the four title rows author.</summary>
    public const float RowFont = 13f;

    /// <summary>A value's face, one step up from its title so the number reads as the answer.</summary>
    public const float ValueFont = 15f;

    /// <summary>The further lines' face on a post-it, the size a post-it is measured against. The
    /// renderer shrinks it only where a line is wider than the post-it.</summary>
    public const float ExtraFont = 11f;

    /// <summary>The gap between two further lines on one post-it, against their own face.</summary>
    public const float ExtraSpacing = 2f;

    // The shipped titles, langui 1133-1137. Literal text for the reason IaWrapupBoard's own copies
    // are. The file ui_strings.json is a build-time extraction artifact, not one of the archives a
    // session opens. Five lines do not earn a reader of their own here.
    private const string TitleText = "Instant Action";
    private const string TimeTitle = "Time to Complete Mission";
    private const string DestroyedTitle = "Enemies Shot Down";
    private const string ZonesTitle = "Danger Zones Completed";
    private const string ShotsTitle = "Shot %";

    // The authored geometry, [@IA_WrapUp@]'s own, as the fallback beside every layout read. It
    // carries the heading, the title column, the value column, the four brushstroke panes and the
    // plaque.
    private const float TitleX = 467f;
    private const float TitleY = 98f;
    private const float ColumnX = 480f;
    private const float ValueX = 533f;
    private const float StrokeX = 489f;
    private const float ContinueX = 640f;
    private const float ContinueY = 449f;

    // GN_B_Continue.png's one frame, which the engine-free half cannot measure; the tick box is
    // centred over it.
    private const float ContinueFrameWidth = 112f;

    // The gap between the last value row and the first post-it, which is the pitch the value rows
    // themselves leave. How far the first post-it stands proud of the title column, so its writing
    // still lines up with the rows. How far it stops short of the plaque.
    private const float ExtraGap = 26f;
    private const float ColumnInset = 14f;
    private const float PlaqueClearance = 6f;

    // A post-it is at most square, the shape the paper is. Remake furniture, so every measure here
    // is a look rather than a decode. The side, the gap between two post-its, the margins around
    // the writing, and the pitch a line is planned at. The top margin is wider, for the glue strip.
    private const float PostItSide = 168f;
    private const float PostItGap = 12f;
    private const float PostItMargin = 6f;
    private const float PostItHead = 12f;
    private const float PostItFoot = 6f;
    private const float LinePitch = 16f;

    // How far a post-it's corner may sit from the page's left edge, and how far every second one
    // drops. A row of them then reads as stuck on by hand rather than tiled.
    private const float PageMargin = 8f;
    private const float PostItStagger = 10f;

    // The page's authored height, which the photographs stop a margin short of.
    private const float PageHeight = 600f;

    // A photograph's print: the white border round the picture and the gap between two prints. A
    // pending print is laid out at the built-in strip's own 4:3 until a landed one says otherwise.
    private const float PrintBorder = 3f;
    private const float PrintGap = 6f;
    private const float PendingAspect = 3f / 4f;

    // The tick box, on the notepad above the plaque. Its side, its gap above the plaque, and its
    // outline weight in strokes one authored pixel apart.
    private const float TickBoxSide = 40f;
    private const float TickBoxGap = 10f;
    private const int OutlinePasses = 2;
    private const int TickPasses = 4;

    // The page's own inks, sampled from its art: the notepad's graphite for the box, the
    // brushstroke red for the tick.
    private const byte InkR = 48, InkG = 40, InkB = 36;
    private const byte RedR = 168, RedG = 38, RedB = 30;

    // The four rows, top to bottom: the title row's line, its value's line, and the brushstroke
    // pane under the title.
    private static readonly (string TitleKey, string Title, float TitleY, string ValueKey, float ValueY, string StrokeKey, float StrokeY)[] Wired =
    {
        ("IAWU_T_TIMETITLE", TimeTitle, 154f, "IAWU_T_TIME", 185f, "IAWU_LINE0", 173f),
        ("IAWU_T_DESTROYEDTITLE", DestroyedTitle, 210f, "IAWU_T_DESTROYED", 235f, "IAWU_LINE1", 226f),
        ("IAWU_T_ZONESTITLE", ZonesTitle, 280f, "IAWU_T_ZONES", 306f, "IAWU_LINE2", 297f),
        ("IAWU_T_SHOTSTITLE", ShotsTitle, 345f, "IAWU_T_SHOTS", 369f, "IAWU_LINE3", 359f),
    };

    // The tick as a hand makes it, in the box's own pixels. It goes down into the lower left, then
    // a long stroke up past the box's top right corner.
    private static readonly (float X, float Y)[] Tick = { (7f, 19f), (16f, 31f), (41f, -7f) };

    private static readonly BoardArt Background = new(BoardArtLibrary.Ui, "IA_StatScreenBackground.jpg");
    private static readonly BoardArt Brushstroke = new(BoardArtLibrary.Ui, "IA_StatScreen_Brushstroke.png");
    private static readonly BoardArt ContinuePlaque = new(BoardArtLibrary.Ui, "GN_B_Continue.png", 4);

    /// <summary>The most lines one post-it carries: as many planned pitches as fit a square one
    /// between its glue strip and its foot.</summary>
    public static int PostItCapacity => (int)((PostItSide - PostItHead - PostItFoot) / LinePitch);

    /// <summary>The heading and the four title/value pairs, each at its own <c>IAWU_T_*</c> row.
    /// The four values are the snapshot's own and are never recomputed here.</summary>
    public static IReadOnlyList<BoardLine> Rows(IaWrapupSnapshot snapshot, CampaignLayout? layout = null)
    {
        System.ArgumentNullException.ThrowIfNull(snapshot);
        layout ??= CampaignLayout.Fallback;
        var (titleX, titleY) = layout.At(Section, TitleKey, TitleX, TitleY);
        var lines = new List<BoardLine>
        {
            new(TitleText, titleX, titleY, 0f, TitleFont, BoardInk.Heading, Italic: true),
        };
        string[] values =
        {
            IaWrapupBoard.FormatElapsed(snapshot.Elapsed),
            snapshot.EnemiesShotDown.ToString(CultureInfo.InvariantCulture),
            snapshot.ZonesCompleted.ToString(CultureInfo.InvariantCulture),
            snapshot.ShotPercent.ToString(CultureInfo.InvariantCulture) + "%",
        };
        for (int i = 0; i < Wired.Length; i++)
        {
            var row = Wired[i];
            var (tx, ty) = layout.At(Section, row.TitleKey, ColumnX, row.TitleY);
            var (vx, vy) = layout.At(Section, row.ValueKey, ValueX, row.ValueY);
            lines.Add(new BoardLine(row.Title, tx, ty, 0f, RowFont, BoardInk.Heading, Italic: true));
            lines.Add(new BoardLine(values[i], vx, vy, 0f, ValueFont, BoardInk.Row, Italic: true));
        }

        return lines;
    }

    /// <summary>The further lines the built-in board carries and the shipped page authors no row
    /// for. First the context line naming the chapter and the mission type, then the stunt run's
    /// splits as the final snapshot has them. The outcome is the tick box's, not a line.
    /// ⚠ The splits' total is left out: both clocks run from the start to the ending, so it is the
    /// time row's own figure again.</summary>
    public static IReadOnlyList<string> ExtraLines(IaWrapupSnapshot snapshot)
    {
        System.ArgumentNullException.ThrowIfNull(snapshot);
        var lines = new List<string>();
        if (snapshot.Context.Length > 0)
        {
            lines.Add(snapshot.Context);
        }

        if (snapshot.StuntLines is { } splits)
        {
            foreach (string line in splits)
            {
                if (!line.StartsWith(StuntSplits.TotalLabel + " ", System.StringComparison.Ordinal))
                {
                    lines.Add(line);
                }
            }
        }

        return lines;
    }

    /// <summary>How many post-its <paramref name="lines"/> further lines take: one per
    /// <see cref="PostItCapacity"/> lines. No more than stand side by side from the first one's
    /// corner to the page's left edge, and none for no lines.</summary>
    public static int PostItCount(int lines, CampaignLayout? layout = null)
    {
        if (lines <= 0)
        {
            return 0;
        }

        var (x, _, _) = FirstPostIt(layout);
        int room = 1 + (int)((x - PageMargin) / (PostItSide + PostItGap));
        int wanted = (lines + PostItCapacity - 1) / PostItCapacity;
        return System.Math.Clamp(wanted, 1, System.Math.Max(1, room));
    }

    /// <summary>The post-its the further lines are written on, first to last. The first stands on
    /// the notepad under the last value row and clear of the plaque. Each further one stands to the
    /// left of the one before. The lines are shared out evenly and each post-it is only as tall as
    /// what it holds, never taller than it is wide.</summary>
    public static IReadOnlyList<WrapupPostIt> PostIts(IaWrapupSnapshot snapshot, CampaignLayout? layout = null)
    {
        var lines = ExtraLines(snapshot);
        int count = PostItCount(lines.Count, layout);
        var postIts = new List<WrapupPostIt>(count);
        if (count == 0)
        {
            return postIts;
        }

        var (x, y, width) = FirstPostIt(layout);
        int each = (lines.Count + count - 1) / count;
        for (int i = 0, taken = 0; i < count; i++)
        {
            int n = System.Math.Min(each, lines.Count - taken);
            var held = new List<string>(n);
            for (int j = 0; j < n; j++)
            {
                held.Add(Compact(lines[taken + j]));
            }

            taken += n;
            // Half a square at the least, so a single line still reads as a note and not a strip.
            float height = System.Math.Clamp(
                PostItHead + (n * LinePitch) + PostItFoot, PostItSide / 2f, PostItSide);
            float left = i == 0 ? x : x - (i * (PostItSide + PostItGap));
            float top = y + (i % 2 == 1 ? PostItStagger : 0f);
            postIts.Add(new WrapupPostIt(left, top, i == 0 ? width : PostItSide, height, held));
        }

        return postIts;
    }

    /// <summary>The paper under one post-it's writing: a soft shadow, the yellow sheet, and the
    /// glue strip along its top, drawn in that order.</summary>
    public static IReadOnlyList<BoardFill> PostItPaper(WrapupPostIt postIt)
    {
        System.ArgumentNullException.ThrowIfNull(postIt);
        return new[]
        {
            new BoardFill(postIt.X + 3f, postIt.Y + 3f, postIt.Width, postIt.Height, 0, 0, 0, 0.28f),
            new BoardFill(postIt.X, postIt.Y, postIt.Width, postIt.Height, 252, 236, 132),
            new BoardFill(postIt.X, postIt.Y, postIt.Width, PostItHead - 4f, 243, 222, 108),
        };
    }

    /// <summary>The writing on one post-it: its lines flowed inside the paper's margins, in the
    /// page's row ink. It shrinks only where a line is wider than the post-it.</summary>
    public static BoardNote PostItNote(WrapupPostIt postIt)
    {
        System.ArgumentNullException.ThrowIfNull(postIt);
        return new BoardNote(
            postIt.Lines, postIt.X + PostItMargin, postIt.Y + PostItHead,
            postIt.Width - (2f * PostItMargin), postIt.Height - PostItHead - PostItFoot + ExtraSpacing,
            ExtraSpacing, ExtraFont, BoardInk.Row, Italic: true, Shrink: true);
    }

    /// <summary>The run's photographs as prints, in marker order. They are laid out as a grid in
    /// the band under the rows, to the left of the post-its and right against them. Each picture
    /// is as wide as the band allows up to <see cref="StuntCapture.ThumbWidth"/>. A shot whose
    /// frame never arrived is left out; one still on its way keeps its place. No photographs, no
    /// prints.</summary>
    public static IReadOnlyList<WrapupPrint> Prints(IaWrapupSnapshot snapshot, CampaignLayout? layout = null)
    {
        System.ArgumentNullException.ThrowIfNull(snapshot);
        var prints = new List<WrapupPrint>();
        var shots = new List<StuntShot>();
        float aspect = PendingAspect;
        bool measured = false;
        foreach (var shot in snapshot.Shots ?? System.Array.Empty<StuntShot>())
        {
            if (shot.Landed && shot.Thumb == null)
            {
                continue;
            }

            shots.Add(shot);
            if (!measured && shot.Thumb is { } thumb && thumb.GetWidth() > 0)
            {
                aspect = (float)thumb.GetHeight() / thumb.GetWidth();
                measured = true;
            }
        }

        if (shots.Count == 0)
        {
            return prints;
        }

        var (x, top, width) = FirstPostIt(layout);
        var postIts = PostIts(snapshot, layout);
        float right = postIts.Count > 0 ? postIts[^1].X - PostItGap : x + width;
        var (columns, picture) = PrintGrid(shots.Count, right - PageMargin, PageHeight - PageMargin - top, aspect);
        if (picture <= 0f)
        {
            return prints;
        }

        float cellWidth = picture + (2f * PrintBorder);
        float cellHeight = (picture * aspect) + (2f * PrintBorder);
        float left = right - ((columns * cellWidth) + ((columns - 1) * PrintGap));
        for (int i = 0; i < shots.Count; i++)
        {
            prints.Add(new WrapupPrint(
                left + ((i % columns) * (cellWidth + PrintGap)), top + ((i / columns) * (cellHeight + PrintGap)),
                cellWidth, cellHeight, shots[i]));
        }

        return prints;
    }

    /// <summary>The paper under one photograph: the same soft shadow a post-it casts, then the
    /// print's white border.</summary>
    public static IReadOnlyList<BoardFill> PrintPaper(WrapupPrint print)
    {
        System.ArgumentNullException.ThrowIfNull(print);
        return new[]
        {
            new BoardFill(print.X + 3f, print.Y + 3f, print.Width, print.Height, 0, 0, 0, 0.28f),
            new BoardFill(print.X, print.Y, print.Width, print.Height, 246, 243, 234),
        };
    }

    /// <summary>The photograph inside its print's border, or null while its thumbnail has not
    /// landed. The picture keeps its own proportions, so one whose pane differs from the grid's
    /// is fitted inside the print.</summary>
    public static BoardPicture? PrintPicture(WrapupPrint print)
    {
        System.ArgumentNullException.ThrowIfNull(print);
        if (print.Shot.Thumb is not { } thumb || thumb.GetWidth() <= 0 || thumb.GetHeight() <= 0)
        {
            return null;
        }

        float room = print.Width - (2f * PrintBorder);
        float tall = print.Height - (2f * PrintBorder);
        float scale = System.Math.Min(room / thumb.GetWidth(), tall / thumb.GetHeight());
        float width = thumb.GetWidth() * scale;
        float height = thumb.GetHeight() * scale;
        return new BoardPicture(
            new BoardArt(BoardArtLibrary.Held, print.Shot.Path, 1, thumb),
            print.X + PrintBorder + ((room - width) / 2f), print.Y + PrintBorder + ((tall - height) / 2f),
            Width: width, Height: height);
    }

    /// <summary>The tick box's top-left and side, on the notepad centred over the CONTINUE plaque.
    /// </summary>
    public static (float X, float Y, float Side) TickBox(CampaignLayout? layout = null)
    {
        var (plaqueX, plaqueY) = ContinueAt(layout);
        return (plaqueX + (ContinueFrameWidth / 2f) - (TickBoxSide / 2f), plaqueY - TickBoxGap - TickBoxSide, TickBoxSide);
    }

    /// <summary>The tick box drawn in strokes: the outline in graphite, and on a won mission the
    /// tick through it in the brushstroke red. A lost mission leaves the box empty; the original
    /// never lost an Instant Action, so that state is the remake's own.</summary>
    public static IReadOnlyList<BoardStroke> TickStrokes(bool won, CampaignLayout? layout = null)
    {
        var (x, y, side) = TickBox(layout);
        var strokes = new List<BoardStroke>();
        for (int p = 0; p < OutlinePasses; p++)
        {
            float l = x + p, t = y + p, r = x + side - p, b = y + side - p;
            strokes.Add(new BoardStroke(l, t, r, t, InkR, InkG, InkB));
            strokes.Add(new BoardStroke(r, t, r, b, InkR, InkG, InkB));
            strokes.Add(new BoardStroke(r, b, l, b, InkR, InkG, InkB));
            strokes.Add(new BoardStroke(l, b, l, t, InkR, InkG, InkB));
        }

        if (!won)
        {
            return strokes;
        }

        for (int p = 0; p < TickPasses; p++)
        {
            for (int s = 0; s + 1 < Tick.Length; s++)
            {
                strokes.Add(new BoardStroke(
                    x + Tick[s].X + p, y + Tick[s].Y, x + Tick[s + 1].X + p, y + Tick[s + 1].Y, RedR, RedG, RedB));
            }
        }

        return strokes;
    }

    /// <summary>The page's art: the magazine spread under everything, then a brushstroke under each
    /// of the four row titles.</summary>
    public static IReadOnlyList<BoardPicture> Pictures(CampaignLayout? layout = null)
    {
        layout ??= CampaignLayout.Fallback;
        var (backX, backY) = layout.At(Section, BackgroundKey, 0f, 0f);
        var pictures = new List<BoardPicture>
        {
            new(layout.Art(Section, BackgroundKey, Background), backX, backY),
        };
        foreach (var row in Wired)
        {
            var (x, y) = layout.At(Section, row.StrokeKey, StrokeX, row.StrokeY);
            pictures.Add(new BoardPicture(layout.Art(Section, row.StrokeKey, Brushstroke), x, y));
        }

        return pictures;
    }

    /// <summary>The CONTINUE plaque's art strip.</summary>
    public static BoardArt ContinueArt(CampaignLayout? layout = null) =>
        (layout ?? CampaignLayout.Fallback).Art(Section, ContinueKey, ContinuePlaque);

    /// <summary>The CONTINUE plaque's authored top-left.</summary>
    public static (float X, float Y) ContinueAt(CampaignLayout? layout = null) =>
        (layout ?? CampaignLayout.Fallback).At(Section, ContinueKey, ContinueX, ContinueY);

    /// <summary>A stand-in run for the screenshot aids and the coverage walk. It is a stunt flight
    /// with three zones behind it, so the page shows every line family at once.</summary>
    public static IaWrapupSnapshot Sample(bool won) => new(
        won, "C1   ·   Stunt Flying", 186f, 4, 3, 27,
        new[] { "1.  Pier    12.4    12.4", "2.  Bridge    15.1    27.5", "3.  Tower    18.6    46.1", StuntSplits.TotalLabel + "   46.1" });

    /// <summary>A stand-in for the longest stunt run the install ships: seventeen zones with a new
    /// best. The screenshot aid then shows the page carrying more than one post-it.</summary>
    public static IaWrapupSnapshot LongSample()
    {
        string[] names =
        {
            "Police Tower", "Harbour Bridge", "Gyro Hangar", "North Crane", "Girders 1", "Girders 2",
            "East Crane", "River Crane", "Girders 3", "Dock Crane", "Pier Crane", "Girders 4",
            "Tall Crane", "Yard Crane 1", "Yard Crane 2", "Sky Bridges", "Zeppelin Mast",
        };
        var lines = new List<string>();
        float total = 0f;
        for (int i = 0; i < names.Length; i++)
        {
            float split = 9.5f + ((i * 7) % 11);
            total += split;
            lines.Add($"{i + 1}.  {names[i]}   {StuntMission.FormatTime(split)}   {StuntMission.FormatTime(total)}");
        }

        lines.Add($"{StuntSplits.TotalLabel}   {StuntMission.FormatTime(total)}");
        lines.Add($"NEW BEST   (was {StuntMission.FormatTime(total + 21.3f)})");
        return new IaWrapupSnapshot(true, "C5   ·   Stunt Flying", total, 0, names.Length, 0, lines);
    }

    // The first post-it's corner and width: under the last value row, a little proud of the title
    // column, and clear of the plaque.
    private static (float X, float Y, float Width) FirstPostIt(CampaignLayout? layout)
    {
        layout ??= CampaignLayout.Fallback;
        var last = Wired[^1];
        var (column, _) = layout.At(Section, last.TitleKey, ColumnX, last.TitleY);
        var (_, valueY) = layout.At(Section, last.ValueKey, ValueX, last.ValueY);
        var (plaqueX, _) = layout.At(Section, ContinueKey, ContinueX, ContinueY);
        float x = column - ColumnInset;
        return (x, valueY + ExtraGap, System.Math.Clamp(plaqueX - PlaqueClearance - x, 1f, PostItSide));
    }

    // The shared grid rule over the room, a print's border on every side and the thumbnail width as
    // the cap.
    private static (int Columns, float Picture) PrintGrid(int count, float width, float height, float aspect) =>
        ShotGrid.Fit(count, width, height, aspect, PrintGap, 2f * PrintBorder, 2f * PrintBorder, StuntCapture.ThumbWidth);

    // A split line's columns closed up to two spaces. The board's own three are more than a
    // post-it's width can spare before the renderer would wrap the line.
    private static string Compact(string line)
    {
        var text = new System.Text.StringBuilder(line.Length);
        int run = 0;
        foreach (char c in line)
        {
            run = c == ' ' ? run + 1 : 0;
            if (run <= 2)
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }
}
