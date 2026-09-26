using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CSVM.Mech3;
using CSVM.UI.Menu;
using CSVM.UI.Overlays;
using CSVM.UI.Screens;

namespace CSVM.UI.Boards;

/// <summary>One row of the pause parchment: the objective's own words and whether it is done.
/// A done row keeps the colour an open row has and takes the mark instead
/// (<c>docs/formats/objectives.md</c>).</summary>
public readonly record struct PauseObjective(string Text, bool Completed);

/// <summary>An icon the screen places by world position through the mission map's own window,
/// which the chart turns into a pixel. <see cref="Revs"/> is its turn in revolutions clockwise, 0
/// for an icon that does not face anywhere.</summary>
public readonly record struct PauseWorldIcon(string Bitmap, float WorldX, float WorldZ, float Revs);

/// <summary>
/// The pause screen's authored half: the dialog its mission resolves to, the shared block every
/// dialog in the file borrows, the reveal that places the pins and picks the parchment's rows, and
/// the strips with their labels, in the order a cursor walks them. <c>InstantAction</c> says which
/// file it was read from, and <c>Texts</c> holds the words an Instant Action dialog writes on the
/// blackboard, empty for a campaign one. Built once per sortie, since none of it changes while the
/// sortie runs. Decode: docs/org/pause-screen.md.
/// </summary>
public sealed record PauseSheet(
    EscapeState State,
    EscapeShared Shared,
    BriefingReveal Reveal,
    string ObjectivesTitle,
    IReadOnlyList<string> ButtonLabels,
    bool InstantAction,
    IReadOnlyList<BoardLine> Texts,
    IReadOnlyList<EscapeButton?> Strips)
{
    /// <summary>Reads one dialog and its shared block, runs its script out through
    /// <see cref="EscapeDialog.Settled"/>, and resolves every label through the message
    /// table. An Instant Action dialog also takes the four texts its own script writes, which no
    /// campaign dialog carries.</summary>
    public static PauseSheet? Load(
        string zrdrPath, string messagesPath, string dialogKey, bool instantAction)
    {
        EscapeDialog dialog;
        Messages messages;
        string file = instantAction
            ? EscapeDialog.InstantActionFile
            : EscapeDialog.CampaignFile;
        try
        {
            dialog = EscapeDialog.Load(zrdrPath, file);
            messages = Messages.Load(messagesPath);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            // Shown over a running mission, so an unreadable extraction leaves the screen to the
            // built-in board rather than taking the session down with it.
            return null;
        }

        if (dialog.Find(dialogKey) is not { } state || dialog.Shared is not { } shared)
        {
            return null;
        }

        var strips = new List<EscapeButton?>();
        var labels = new List<string>();
        foreach (string key in PauseScreens.ButtonKeys)
        {
            strips.Add(shared.Button(key));
            labels.Add(EscapeDialog.Label(messages, shared.Button(key)?.LabelKey ?? string.Empty));
        }

        strips.Insert(PauseScreens.PhotoRow, PauseScreens.PhotoStrip(shared));
        labels.Insert(PauseScreens.PhotoRow, PauseScreens.PhotoLabel);

        return new PauseSheet(
            state,
            shared,
            EscapeDialog.Settled(state.Steps),
            EscapeDialog.Label(messages, shared.Objectives?.TitleKey ?? string.Empty),
            labels,
            instantAction,
            instantAction
                ? LoadScreens.DialogTexts(zrdrPath, file, state.Key, messages)
                : Array.Empty<BoardLine>(),
            strips);
    }
}

/// <summary>What the pause screen shows of the running mission: the objectives and their marks, the
/// profile's memento, and the icons the chart places by world position.</summary>
public sealed record PauseReadout(
    IReadOnlyList<PauseObjective> Objectives,
    string Memento,
    IReadOnlyList<PauseWorldIcon> Icons)
{
    /// <summary>A readout with nothing in it, for a dialog that carries no map or parchment.</summary>
    public static PauseReadout Empty { get; } = new(
        Array.Empty<PauseObjective>(), string.Empty, Array.Empty<PauseWorldIcon>());

    /// <summary>The chart icon one world pose takes: the shared block's bitmap at the pose's plan
    /// position, turned so its drawn nose reads against the compass. Null only when no bitmap is
    /// named, which is the original's whole binding test; one place, so a session and a suite agree.
    /// ⚠ Whether the pose reaches the chart is <see cref="MissionMap.Icon"/>'s answer at
    /// composition, and a pose outside the window yields an icon here and draws nothing
    /// there.</summary>
    public static PauseWorldIcon? Icon(
        string bitmap, float worldX, float worldZ, float forwardX, float forwardZ) =>
        bitmap.Length == 0
            ? null
            : new PauseWorldIcon(
                bitmap, worldX, worldZ, MissionMap.IconRevs(bitmap, forwardX, forwardZ));

    /// <summary>The parchment's rows for one mission's note, each marked by what
    /// <paramref name="completed"/> answers for that row's own <c>OBJECTIVEn</c> number.
    /// ⚠ Ask by the number, never by the row's priority: priority is the note's row order, and a
    /// mission whose priorities are not its objective numbers then marks a row on an unrelated
    /// objective's completion (docs/formats/objectives.md).</summary>
    public static IReadOnlyList<PauseObjective> Rows(
        IReadOnlyList<BriefingObjective> note, Func<int, bool> completed)
    {
        var rows = new List<PauseObjective>(note.Count);
        foreach (var objective in note)
        {
            rows.Add(new PauseObjective(objective.Text, completed(objective.Number)));
        }

        return rows;
    }
}

/// <summary>
/// What the Original presentation's pause screen is made of, engine-free: the mission's chart at
/// its authored crop, the pins and icons the dialog's script places, the objectives parchment, the
/// profile's memento and the button strips, the four the block authors plus the remake's own photo
/// strip. Composed the way the load and briefing screens are, through
/// <see cref="ComposedBoard"/>, and sharing <see cref="MissionMap"/> with both.
///
/// An Instant Action sortie pauses on the same screen over a dialog that carries none of
/// that: the load screen's blackboard, its three photographs, its four texts and the strips.
/// </summary>
public static class PauseScreens
{
    /// <summary>RESUME's row.</summary>
    public const int ResumeRow = 0;

    /// <summary>PHOTO MODE's row, the one strip no dialog authors.</summary>
    public const int PhotoRow = 1;

    /// <summary>RESTART's row.</summary>
    public const int RestartRow = 2;

    /// <summary>PREFERENCES' row.</summary>
    public const int PreferencesRow = 3;

    /// <summary>QUIT's row.</summary>
    public const int QuitRow = 4;

    /// <summary>A strip's plate in authored pixels across. Every strip draws the same three
    /// bitmaps, which measure 132x28, so one rectangle serves every row
    /// (docs/org/pause-screen.md).</summary>
    public const float StripWidth = 132f;

    /// <summary>A strip's plate in authored pixels down (docs/org/pause-screen.md).</summary>
    public const float StripHeight = 28f;

    /// <summary>What the remake's own strip reads. The authored four are set in title case, so this
    /// one is too rather than shouting beside them.</summary>
    public const string PhotoLabel = "Photo Mode";

    // The widget names the shared BUTTONS block authors, and the name the remake's own strip
    // carries, which no shipped file holds.
    private const string ResumeKey = "RESUME_MISSION_BTN";
    private const string RestartKey = "RESTART_MISSION_BTN";
    private const string PreferencesKey = "CONFIGURE_BTN";
    private const string QuitKey = "MAINMENU_BTN";
    private const string PhotoKey = "PHOTO_MODE_BTN";

    /// <summary>The four authored strips, in the order the shared <c>BUTTONS</c> block authors
    /// them. A sheet's own <see cref="PauseSheet.Strips"/> is this with the remake's photo strip
    /// standing at <see cref="PhotoRow"/>, which is the order a cursor walks.</summary>
    public static IReadOnlyList<string> ButtonKeys { get; } = new[]
    {
        ResumeKey, RestartKey, PreferencesKey, QuitKey,
    };

    /// <summary>The remake's own strip, in the authored plates and label offset the block's own
    /// RESUME carries, at the one place that block leaves room for a fifth: the channel between two
    /// columns of strips, or the cell under RESTART where they stand three across. Null where the
    /// block authors too few strips to stand one against.</summary>
    public static EscapeButton? PhotoStrip(EscapeShared shared)
    {
        ArgumentNullException.ThrowIfNull(shared);
        if (shared.Button(ResumeKey) is not { } resume
            || shared.Button(RestartKey) is not { } restart
            || shared.Button(PreferencesKey) is not { } preferences
            || shared.Button(QuitKey) is not { } quit)
        {
            return null;
        }

        var channel = new BriefingPoint((resume.At.X + preferences.At.X) / 2f, resume.At.Y);
        var under = new BriefingPoint(restart.At.X, resume.At.Y + (quit.At.Y - preferences.At.Y));
        var at = Covered(shared, channel) <= Covered(shared, under) ? channel : under;
        return new EscapeButton(
            PhotoKey, at, resume.Normal, resume.Rollover, resume.Activate, string.Empty,
            resume.LabelOffset);
    }

    /// <summary>The strip an authored point lands on, or -1 for a point on none of them. This is
    /// the pointer's whole hit test: the plates are the screen's only widgets.
    /// ⚠ Answers the first row in walk order, not the nearest: the campaign block's channel is four
    /// pixels narrower than a plate, so the photo strip shares two columns with each neighbour and
    /// the earlier row owns them.</summary>
    public static int RowAt(PauseSheet sheet, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        for (int row = 0; row < sheet.Strips.Count; row++)
        {
            if (sheet.Strips[row] is not { } button)
            {
                continue;
            }

            if (x >= button.At.X && x < button.At.X + StripWidth
                && y >= button.At.Y && y < button.At.Y + StripHeight)
            {
                return row;
            }
        }

        return -1;
    }

    /// <summary>Composes the screen for one pause. <paramref name="focusedRow"/> is the strip the
    /// cursor stands on and <paramref name="pressed"/> whether it is held, which is what picks
    /// between the three authored strip bitmaps and their three label inks.
    /// <paramref name="pointer"/> is the pointer in authored pixels, or null for a screen nobody is
    /// pointing at, and draws the dialog's own cursor over everything else.</summary>
    public static ComposedBoard For(
        PauseSheet sheet, PauseReadout readout, int focusedRow, bool pressed,
        (float X, float Y)? pointer = null)
    {
        var backdrop = new List<BoardPicture>();
        if (sheet.State.Background.Length > 0)
        {
            backdrop.Add(new BoardPicture(
                new BoardArt(BoardArtLibrary.Rimage, sheet.State.Background), 0, 0, 0, false, 1f, 0f,
                BoardFit.AuthoredWidth, BoardFit.AuthoredHeight));
        }

        var pictures = new List<BoardPicture>();
        if (sheet.State.Map is { Bitmap.Length: > 0 } map)
        {
            pictures.Add(MissionMap.Sheet(map));
        }

        bool parchment = ShowsObjectives(sheet);
        if (parchment && sheet.Shared.Objectives is { Background.Length: > 0 } list)
        {
            pictures.Add(new BoardPicture(
                new BoardArt(BoardArtLibrary.Rimage, list.Background),
                list.BackgroundAt.X, list.BackgroundAt.Y));
        }

        MissionMap.Elements(pictures, sheet.Reveal, back: true);
        MissionMap.Elements(pictures, sheet.Reveal, back: false);
        AddWorldIcons(pictures, sheet, readout);
        AddMemento(pictures, sheet, readout);

        var lines = new List<BoardLine>(sheet.Texts);
        if (parchment && sheet.Shared.Objectives is { } titled && sheet.ObjectivesTitle.Length > 0)
        {
            lines.Add(new BoardLine(
                sheet.ObjectivesTitle, titled.TitleAt.X, titled.TitleAt.Y, 0f,
                EscapeObjectivesList.TitleFont, BoardInk.Heading));
        }

        var overlays = new List<BoardPanel>();
        AddCursor(overlays, sheet, pointer);

        return new ComposedBoard(
            pictures,
            MissionMap.Strokes(sheet.Reveal),
            lines,
            Plaques(sheet, focusedRow, pressed),
            Notes(sheet, readout),
            backdrop,
            overlays: overlays);
    }

    // The parchment is a shared widget every dialog in the file carries, and each dialog's own
    // script says whether it stands: a campaign dialog turns it on, an Instant Action one authors
    // an explicit Off and draws the blackboard bare (docs/org/pause-screen.md).
    private static bool ShowsObjectives(PauseSheet sheet) =>
        sheet.Reveal.Element("OBJECTIVESLIST")?.Visible ?? false;

    // The parchment's rows: one per objective the script revealed, in the order it revealed them,
    // with the mark riding the row rather than a column of its own.
    private static IReadOnlyList<BoardNote> Notes(PauseSheet sheet, PauseReadout readout)
    {
        if (!ShowsObjectives(sheet) || sheet.Shared.Objectives is not { } list
            || sheet.Reveal.RevealedObjectives.Count == 0)
        {
            return Array.Empty<BoardNote>();
        }

        var entries = new List<string>();
        var marked = new List<bool>();
        foreach (int index in sheet.Reveal.RevealedObjectives)
        {
            if (index >= 0 && index < readout.Objectives.Count)
            {
                entries.Add(readout.Objectives[index].Text);
                marked.Add(readout.Objectives[index].Completed);
            }
        }

        if (entries.Count == 0)
        {
            return Array.Empty<BoardNote>();
        }

        return new[]
        {
            new BoardNote(
                entries, list.ListAt.X, list.ListAt.Y, list.RowWrap, list.RowBox,
                list.Spacing, EscapeObjectivesList.RowFont, BoardInk.Row,
                list.CheckMark.Length > 0
                    ? new BoardArt(BoardArtLibrary.Rimage, list.CheckMark)
                    : null,
                marked,
                Italic: true, Shrink: true),
        };
    }

    // How much of the authored plates a strip standing at this point would cover, in square pixels.
    // The block's own shape is what picks between the two places a fifth strip can stand: two
    // columns leave the channel between them clear, three across leave the cell under the second.
    private static float Covered(EscapeShared shared, BriefingPoint at)
    {
        float covered = 0f;
        foreach (var button in shared.Buttons)
        {
            float across = Math.Min(at.X + StripWidth, button.At.X + StripWidth)
                - Math.Max(at.X, button.At.X);
            float down = Math.Min(at.Y + StripHeight, button.At.Y + StripHeight)
                - Math.Max(at.Y, button.At.Y);
            covered += across > 0f && down > 0f ? across * down : 0f;
        }

        return covered;
    }

    // Each strip is three separate bitmaps rather than one stacked frame, so the state picks the
    // art and the ink together; there is no disabled frame to pick.
    private static IReadOnlyList<BoardPlaque> Plaques(PauseSheet sheet, int focusedRow, bool pressed)
    {
        var plaques = new List<BoardPlaque>();
        for (int row = 0; row < sheet.Strips.Count; row++)
        {
            if (sheet.Strips[row] is not { } button)
            {
                continue;
            }

            bool focused = row == focusedRow;
            bool held = focused && pressed;
            string art = held ? button.Activate : focused ? button.Rollover : button.Normal;
            if (art.Length == 0)
            {
                continue;
            }

            plaques.Add(new BoardPlaque(
                new BoardArt(BoardArtLibrary.Rimage, art), button.At.X, button.At.Y, row, 0,
                row < sheet.ButtonLabels.Count ? sheet.ButtonLabels[row] : string.Empty,
                ComposedBoard.PlaqueInk(focused, held)));
        }

        return plaques;
    }

    // The dialog's own pointer, over everything else the screen draws: the rollover bitmap while it
    // stands on a strip and the plain one everywhere else, which is the only rollover the original's
    // cursor has. A dialog that authors no cursor draws none and leaves the pointer to its host.
    private static void AddCursor(
        List<BoardPanel> into, PauseSheet sheet, (float X, float Y)? pointer)
    {
        if (sheet.State.Cursor is not { } cursor || pointer is not { } at)
        {
            return;
        }

        string bitmap = RowAt(sheet, at.X, at.Y) >= 0 ? cursor.Rollover : cursor.Bitmap;
        if (bitmap.Length == 0)
        {
            return;
        }

        into.Add(new BoardPanel(
            Array.Empty<BoardFill>(),
            new[]
            {
                new BoardPicture(
                    new BoardArt(BoardArtLibrary.Rimage, bitmap), at.X, at.Y,
                    Centered: cursor.Centered),
            },
            Array.Empty<BoardLine>()));
    }

    private static void AddWorldIcons(
        List<BoardPicture> into, PauseSheet sheet, PauseReadout readout)
    {
        if (sheet.State.Map is not { } map)
        {
            return;
        }

        foreach (var icon in readout.Icons)
        {
            if (MissionMap.Icon(map, icon.Bitmap, icon.WorldX, icon.WorldZ, icon.Revs) is { } placed)
            {
                into.Add(placed);
            }
        }
    }

    // The primitive's position is authored and its picture is not: the runtime replaces the
    // placeholder with the profile's own memento image.
    private static void AddMemento(
        List<BoardPicture> into, PauseSheet sheet, PauseReadout readout)
    {
        string bitmap = readout.Memento.Length > 0 ? readout.Memento : sheet.State.MementoBitmap;
        if (bitmap.Length > 0)
        {
            into.Add(new BoardPicture(
                new BoardArt(BoardArtLibrary.Rimage, bitmap),
                sheet.State.MementoAt.X, sheet.State.MementoAt.Y));
        }
    }
}
