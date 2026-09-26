using System;
using System.Collections.Generic;

namespace CSVM.UI.Boards;

/// <summary>Which library a board bitmap comes from. The two extractions keep different naming:
/// the mission art is lowercase under <c>rimage</c>, the screen chrome keeps the authored
/// filename under <c>rof</c>.</summary>
public enum BoardArtLibrary
{
    /// <summary>Mission art, <c>extracted/rimage/&lt;name&gt;.png</c>, named without its extension.</summary>
    Rimage,

    /// <summary>Screen chrome, <c>extracted/rof/ASSETS/GRAPHICS/&lt;name&gt;</c>, named with it.</summary>
    Ui,

    /// <summary>A file outside the extraction, <see cref="BoardArt.Name"/> being its whole path: a
    /// scrapbook capture, which lives in the profile's own directory and is the one thing the book
    /// draws that no asset library holds.</summary>
    Loose,

    /// <summary>A movie, <c>extracted/rof/ASSETS/GRAPHICS/MPG/&lt;name&gt;</c>, named with its
    /// extension: the picture playing now rather than a bitmap. The renderer plays it endlessly,
    /// which is what every <c>movie</c> row on a screen it composes authors.</summary>
    Movie,

    /// <summary>A picture already in memory, <see cref="BoardArt.Pixels"/>, with
    /// <see cref="BoardArt.Name"/> naming it for a reader: a stunt photograph's strip thumbnail,
    /// which the wrap-up page draws without reading the full-size file back.</summary>
    Held,
}

/// <summary>How a piece of board text is inked. The authored colours are per widget and mostly
/// undecoded, so a board names the role and the renderer owns the palette.</summary>
public enum BoardInk
{
    /// <summary>A list row nobody is on.</summary>
    Row,

    /// <summary>The list row the cursor is on.</summary>
    RowFocused,

    /// <summary>A heading over a list.</summary>
    Heading,

    /// <summary>The line under the list describing the focused row, and the controls hint.</summary>
    Detail,

    /// <summary>A button label the pointer is not on (<c>BtnLabelNormal</c>).</summary>
    LabelNormal,

    /// <summary>A button label under the cursor (<c>BtnLabelRollover</c>).</summary>
    LabelRollover,

    /// <summary>A button label being pressed (<c>BtnLabelActivate</c>).</summary>
    LabelActivate,

    /// <summary>A dialog's own words, and a dialog button's label on its two dark frames. White
    /// whatever screen it stands over, which is what <c>MB_T_MESSAGE</c>'s authored
    /// <c>0xFFFFFFFF</c> and <c>[GLOBALVARS]</c>' <c>ACTIVE</c>/<c>ROLLOVER</c> say, so it takes
    /// no palette.</summary>
    Dialog,

    /// <summary>A dialog button's label while it is held: black on the depressed frame's light
    /// plaque, <c>[GLOBALVARS]</c>' <c>DEPRESSED</c>, and no palette either.</summary>
    DialogPressed,

    /// <summary>The credits screen's hidden line, in the yellow its script-created text widget is
    /// given (<c>0xffffff00</c>). One screen writes it and it takes no palette.</summary>
    Secret,

    /// <summary>A figure the build behind it fails a check on, the plane cost past the wallet and
    /// the current weight past the airframe's capacity. Pure red whatever screen it stands on,
    /// which is what the plane-construction script's own <c>0xffff0000</c> says
    /// (<c>docs/org/hangar.md</c>, "The two red figures"), so it takes no palette.</summary>
    Alarm,

    /// <summary>Player 1's chip on a seat strip, <c>SplitScreen.PlayerColor(0)</c>. An identity
    /// colour is the same in both presentations and on every background, so it takes no
    /// palette; <see cref="SeatStrip"/> is what maps a seat to one of these four.</summary>
    Seat1,

    /// <summary>Player 2's chip, <c>SplitScreen.PlayerColor(1)</c>.</summary>
    Seat2,

    /// <summary>Player 3's chip, <c>SplitScreen.PlayerColor(2)</c>.</summary>
    Seat3,

    /// <summary>Player 4's chip, <c>SplitScreen.PlayerColor(3)</c>.</summary>
    Seat4,
}

/// <summary>Which edge of its <see cref="BoardLine.Width"/> a text widget's words sit against,
/// which is <c>LAYOUT.CSV</c>'s own trailing justification field on a <c>T</c> row.</summary>
public enum BoardJustify
{
    /// <summary>Against the widget's left edge, the layout's 0.</summary>
    Left,

    /// <summary>Centred in its width, the layout's 1.</summary>
    Center,

    /// <summary>Against its right edge, the layout's 2.</summary>
    Right,
}

/// <summary>The screen buttons the campaign boards carry, one member per authored widget. A page
/// names one of these per row so the board can draw that row as its own plaque instead of a text
/// line; the geometry for each is in <see cref="Campaign.CampaignBoards"/>.</summary>
public enum BoardButton
{
    /// <summary>Not a button: the row is list text.</summary>
    None,

    /// <summary><c>CM_B_START</c>, the profile screen's CONTINUE.</summary>
    Continue,

    /// <summary><c>CM_B_DELETEPLAYER</c>.</summary>
    DeletePlayer,

    /// <summary><c>CM_B_CANCEL</c>.</summary>
    CancelProfile,

    /// <summary><c>PC_B_NEWMISSION</c>.</summary>
    NextMission,

    /// <summary><c>PC_B_PREVIOUS</c>.</summary>
    PreviousMissions,

    /// <summary><c>PC_B_PLANEX</c>.</summary>
    PlaneConstruction,

    /// <summary><c>PC_B_RETURNMM</c>.</summary>
    ReturnToMainMenu,

    /// <summary><c>PC_B_CHANGEMOMENTO</c>, the cabin's door onto the memento chooser.</summary>
    ChangeMemento,

    /// <summary><c>MS_B_ARROWL</c>, the chooser's step back through the awarded pictures.</summary>
    PreviousMemento,

    /// <summary><c>MS_B_ARROWR</c>, the chooser's step forward.</summary>
    NextMemento,

    /// <summary><c>MS_B_ACCEPT</c>, which hangs the picture on show.</summary>
    AcceptMemento,

    /// <summary><c>MS_B_CANCEL</c>, which leaves the cabin's picture as it was.</summary>
    CancelMemento,

    /// <summary><c>SBTOC_B_VIEW</c>.</summary>
    ViewMission,

    /// <summary><c>SBTOC_B_REPLAY</c>.</summary>
    ReplayMission,

    /// <summary><c>SBTOC_B_RETURN</c>, also the briefing's RETURN TO CABIN.</summary>
    ReturnToCabin,

    /// <summary>The briefing's <c>REPLAY</c>.</summary>
    ReplayBriefing,

    /// <summary>The briefing's <c>FLIGHTCHECK</c>.</summary>
    GoToFlightCheck,

    /// <summary><c>FC_B_CHANGEPLANE</c>, one per crew slot.</summary>
    ChangePlane,

    /// <summary><c>FC_B_CHANGEAMMO</c>, one per crew slot.</summary>
    ChangeAmmo,

    /// <summary><c>FC_B_RETURNBRIEF</c>.</summary>
    ReturnToBriefing,

    /// <summary><c>FC_B_FLYMISSION</c>.</summary>
    FlyMission,

    /// <summary><c>OL_B_ACCEPT</c>.</summary>
    AcceptLoadout,

    /// <summary><c>OL_B_CANCEL</c>.</summary>
    CancelLoadout,

    /// <summary><c>PS_B_EXPORT</c>, one per crew slot.</summary>
    ExportPlane,

    /// <summary><c>PS_B_ACCEPT</c>.</summary>
    AcceptSelections,

    /// <summary><c>PS_B_CANCEL</c>.</summary>
    CancelSelections,

    /// <summary><c>SBZ_B_RETURN</c>, the scrap detail view's close button.</summary>
    CloseZoom,

    /// <summary><c>SBZ_B_EXPORT</c>, the scrap detail view's Export to Desktop.</summary>
    ExportScrap,

    /// <summary><c>SB_B_TOC</c>, the book's VIEW ALL MISSIONS.</summary>
    ViewAllMissions,

    /// <summary><c>SB_B_BEST</c>, the results card's Best to Date tab.</summary>
    BestTab,

    /// <summary><c>SB_B_MOST</c>, the results card's Most Recent tab.</summary>
    MostTab,

    /// <summary><c>sb_b_prev</c>, the book's page-back arrow.</summary>
    ScrapbookPrev,

    /// <summary><c>sb_b_next</c>, the book's page-forward arrow.</summary>
    ScrapbookNext,

    /// <summary><c>sb_b_current</c>, the Current Mission bookmark.</summary>
    CurrentMission,
}

/// <summary>An RGB multiply over a picture's own pixels, which is how the paint screen colours a
/// white region mask: the mask carries the shape in its alpha, the tint is the picked colour.</summary>
public readonly record struct BoardTint(byte R, byte G, byte B);

/// <summary>An edit box's text cursor: the bar's authored colour and its size in authored pixels,
/// the colour being the box's own <c>CursorColor</c> field rather than a palette role. Where along
/// the line it stands is a font measurement, so the line carrying one says no more than that.</summary>
public readonly record struct BoardCaret(byte R, byte G, byte B, float Width, float Height);

/// <summary>A rectangle of the source bitmap to draw instead of the whole frame, in the bitmap's
/// own pixels, which is a <c>MAP</c> primitive's <c>CLIP</c>: the crop's top left lands on the
/// picture's authored position and the drawn size is the crop's. Not a scale and not a screen
/// clip; a crop bigger than the frame is trimmed to it.</summary>
public readonly record struct BoardCrop(float X, float Y, float Width, float Height);

/// <summary>One bitmap a board draws, and how many stacked frames it holds. A button strip is four
/// frames (disabled, normal, rollover, depressed, in that order); everything else is one.
/// <paramref name="Pixels"/> is the picture itself for <see cref="BoardArtLibrary.Held"/> art and
/// null for every library read from a file.</summary>
public sealed record BoardArt(BoardArtLibrary Library, string Name, int Frames = 1, Godot.Image? Pixels = null);

/// <summary>A picture placed at its authored pixel position. <paramref name="Centered"/> is the
/// briefing script's own <c>center</c> flag: the coordinate is the middle, not the top left.
/// <paramref name="Scale"/> grows the art about its own middle and leaves its authored corner
/// where it is, which is the <c>scale()</c> a scrapbook scrap takes under the pointer.
/// <paramref name="Tint"/> multiplies the pixels, null drawing them as authored.
/// <paramref name="Crop"/> takes a region of the source instead of the whole frame.</summary>
public sealed record BoardPicture(
    BoardArt Art, float X, float Y, int Frame = 0, bool Centered = false,
    float Opacity = 1f, float Revs = 0f, float Width = 0f, float Height = 0f, float Scale = 1f,
    BoardTint? Tint = null, BoardCrop? Crop = null);

/// <summary>A straight connector line between two authored points in its authored colour, which
/// is the briefing script's <c>Line</c> opcode and the only non-picture element any board draws.
/// <paramref name="Opacity"/> is the element's, so the line fades in with everything else.</summary>
public sealed record BoardStroke(
    float X1, float Y1, float X2, float Y2, byte R, byte G, byte B, float Opacity = 1f);

/// <summary>A rectangle in an authored ARGB colour, a list widget's own
/// <c>ldrawrect</c>/<c>ldrawframe</c> pair. The table of contents draws a row's picked and focused
/// states with nothing else. The <paramref name="Border"/> flag draws the one-pixel outline instead
/// of the fill, and the <paramref name="Opacity"/> is the colour's own alpha byte. An
/// <paramref name="Ink"/> takes the palette's colour instead of the three bytes, which is what a
/// seat chip wants. The identity colours then stay in the renderer rather than being turned into
/// bytes by every composer that paints one.</summary>
public sealed record BoardFill(
    float X, float Y, float Width, float Height, byte R, byte G, byte B, float Opacity = 1f,
    bool Border = false, BoardInk? Ink = null);

/// <summary>One button plaque: its art strip, its authored top-left, the page row it presses, the
/// strip frame to draw and the label to write over it. <see cref="Label"/> is empty where the art
/// bakes its own words in, which every screen but the briefing and the paper buttons does.
/// <paramref name="LabelBaseline"/> puts the label's baseline that many pixels below the frame's
/// top instead of centring it in the frame, for a strip whose plaque does not fill its own frame:
/// 0 keeps the centred placement every other plaque takes.</summary>
public sealed record BoardPlaque(
    BoardArt Art, float X, float Y, int Row, int Frame, string Label, BoardInk Ink,
    float LabelBaseline = 0f);

/// <summary>One line of board text at an authored position, wrapped to <paramref name="Width"/>
/// (0 for no wrap). <paramref name="Row"/> is the page row it stands for, or -1 for chrome.
/// <paramref name="Italic"/> is the <c>I</c> of a langui row's own <c>[FONTID]</c> tag, which is
/// how the original names a slanted face; the renderer decides what it draws that with.
/// <paramref name="Justify"/> needs a width to mean anything, since it is measured from one.
/// <paramref name="Caret"/> is the blinking cursor an edit box draws after its text.
/// <paramref name="Bold"/> is the weight of an authored face a screen draws beside a lighter one,
/// which the extraction ships no second typeface for, so the renderer emboldens its own.
/// <paramref name="Leading"/> is the pitch a wrapped block's lines take, 0 leaving it to the face's
/// own metrics; a widget whose authored block must end where the artwork under it does sets it.
/// <paramref name="Face"/> is the typeface the langui row names, drawn in place of the board's own
/// where the machine carries it. <paramref name="Colour"/> is an authored colour beating the ink.</summary>
public sealed record BoardLine(
    string Text, float X, float Y, float Width, float Size, BoardInk Ink, int Row = -1,
    bool Italic = false, BoardJustify Justify = BoardJustify.Left, BoardCaret? Caret = null,
    bool Bold = false, float Leading = 0f, LanguiFace? Face = null, BoardTint? Colour = null)
{
    /// <summary>Where <see cref="Glyph"/> is drawn inside <see cref="Text"/>. A control character,
    /// so no authored string carries one.</summary>
    public const string GlyphSlot = "\u0001";

    /// <summary>The authored box height a wrapped block is asked to fit inside, 0 for a line
    /// drawn at its own size whatever it comes to. A block taller than this is stepped down a
    /// point at a time by the renderer, which is the only half that can measure it.</summary>
    public float Height { get; init; }

    /// <summary>The pad control drawn where <see cref="GlyphSlot"/> stands in the text, or null for
    /// a line of words alone. ⚠ The gap after the glyph is the renderer's, since only it can measure
    /// the picture. A composer that spaces the words itself breaks the line on another face. A line
    /// carrying one is drawn unwrapped and unjustified for the same reason.</summary>
    public GlyphKey? Glyph { get; init; }
}

/// <summary>
/// A list widget's entries and the box they flow inside, in authored pixels (the briefing
/// parchment's own <c>LIST</c>, <c>docs/formats/briefing.md</c>). A layer of its own because how
/// tall an entry draws is a font metric the engine-free half does not hold, so where the next
/// entry starts cannot be composed here. <paramref name="Italic"/> slants every entry the way a
/// langui row's <c>[FONTID]</c> tag does. Four answers to a body longer than its box:
/// <paramref name="Cut"/> keeps the words of an over-tall entry that fit; <paramref name="Skip"/>
/// drops that many wrapped lines off the first entry's head, a scrolled box's window; a
/// <paramref name="Height"/> of 0 stops the entries nowhere; <paramref name="Shrink"/> asks the
/// renderer to scale the face down until every entry fits. <paramref name="Counted"/> hands back
/// the lines the body wraps to and the box holds, what a scrollbar is drawn and clamped from.
/// </summary>
public sealed record BoardNote(
    IReadOnlyList<string> Entries, float X, float Y, float Width, float Height, float Spacing,
    float Size, BoardInk Ink, BoardArt? Mark = null, IReadOnlyList<bool>? Marked = null,
    bool Italic = false, bool Cut = false, bool Shrink = false, int Skip = 0, Action<int, int>? Counted = null)
{
    /// <summary>The entries as placed lines, stacked from the widget's top-left and stopped at its
    /// authored height, or at nothing where that height is 0 or the face may shrink to the box.
    /// <paramref name="height"/> measures one entry wrapped to a width, in authored pixels; a fixed
    /// pitch instead draws a wrapped entry over the one under it.</summary>
    public IReadOnlyList<BoardLine> Flow(Func<string, float, float> height)
    {
        ArgumentNullException.ThrowIfNull(height);
        var lines = new List<BoardLine>();
        foreach (var (_, line) in Placed(height))
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>The mark over each flowed entry <see cref="Marked"/> says is done, centred on that
    /// entry's own origin, which is where the original's <c>CHECKMARK</c> element puts it: over the
    /// row's leading characters rather than in a column beside them
    /// (<c>docs/formats/objectives.md</c>). Empty where the widget carries no mark art.</summary>
    public IReadOnlyList<BoardPicture> Marks(Func<string, float, float> height)
    {
        var marks = new List<BoardPicture>();
        if (Mark is not { } art || Marked is not { } marked)
        {
            return marks;
        }

        foreach (var (index, line) in Placed(height))
        {
            if (index < marked.Count && marked[index])
            {
                marks.Add(new BoardPicture(art, line.X, line.Y, 0, true));
            }
        }

        return marks;
    }

    /// <summary>The lines the first entry wraps to at this width, and the lines the box holds: what
    /// a scrolled box's window is counted in. Both are zero where there is nothing to measure.
    /// </summary>
    public (int Total, int Fits) Rows(Func<string, float, float> height)
    {
        ArgumentNullException.ThrowIfNull(height);
        float line = Entries.Count > 0 ? height("A", Width) : 0f;
        return line <= 0f
            ? (0, 0)
            : ((int)Math.Round(height(Entries[0], Width) / line, MidpointRounding.AwayFromZero), (int)(Height / line));
    }

    private IEnumerable<(int Index, BoardLine Line)> Placed(Func<string, float, float> height)
    {
        float top = Y;
        for (int i = 0; i < Entries.Count; i++)
        {
            string entry = i == 0 ? Scrolled(Entries[0], height) : Entries[i];
            string text = entry;
            if (Height > 0f && !Shrink)
            {
                float room = Y + Height - top;
                text = height(entry, Width) <= room ? entry
                    : Cut ? Fit(entry, room, height) : string.Empty;
            }

            if (text.Length == 0)
            {
                break;
            }

            yield return (i, new BoardLine(text, X, top, Width, Size, Ink, -1, Italic));
            top += height(text, Width) + Spacing;
            if (text.Length < entry.Length)
            {
                break;
            }
        }
    }

    // The body past the lines the window has scrolled off, cut at the same word boundary the wrap
    // would have used: the head filling Skip lines is the longest one that fits their room, so the
    // rest begins where the drawn box begins. An over-run skip keeps the whole body, the owner of
    // the window being the one that clamps it.
    private string Scrolled(string entry, Func<string, float, float> height)
    {
        float line = Skip > 0 ? height("A", Width) : 0f;
        if (line <= 0f)
        {
            return entry;
        }

        string head = Fit(entry, (Skip * line) + 1f, height);
        return head.Length == 0 || head.Length >= entry.Length ? entry : entry[head.Length..].TrimStart();
    }

    // The longest head of an entry that fits the room left, cut at a word: the last word that
    // still fits is found by halving, so the measurer is called a handful of times rather than
    // once per word. Nothing fits under one line's worth of room.
    private string Fit(string entry, float room, Func<string, float, float> height)
    {
        var breaks = new List<int>();
        for (int i = entry.IndexOf(' ', StringComparison.Ordinal); i > 0; i = entry.IndexOf(' ', i + 1))
        {
            breaks.Add(i);
        }

        int low = 0;
        int high = breaks.Count;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (height(entry[..breaks[mid - 1]], Width) > room)
            {
                high = mid - 1;
            }
            else
            {
                low = mid;
            }
        }

        return low == 0 ? string.Empty : entry[..breaks[low - 1]];
    }
}

/// <summary>
/// Something drawn over the finished screen: an open drop-down list, a dialog. Its own three layers
/// draw in the order they are named here, after everything the board itself carries, which is what
/// makes it an overlay rather than another layer of the screen. A screen with none composes none.
/// </summary>
public sealed record BoardPanel(
    IReadOnlyList<BoardFill> Fills,
    IReadOnlyList<BoardPicture> Pictures,
    IReadOnlyList<BoardLine> Lines);

/// <summary>
/// One campaign screen composed in the original's 800x600 dialog space: the pictures under it, the
/// text on it and the button plaques over it, all at authored pixel positions. Engine-free, so
/// what a screen is made of tests off engine; <see cref="ComposedBoardView"/> is only its renderer
/// and <see cref="BoardFit"/> is how the space meets the window.
/// </summary>
public sealed class ComposedBoard
{
    /// <summary>Builds a board out of its ordered layers.</summary>
    public ComposedBoard(
        IReadOnlyList<BoardPicture> pictures,
        IReadOnlyList<BoardStroke> strokes,
        IReadOnlyList<BoardLine> lines,
        IReadOnlyList<BoardPlaque> plaques,
        IReadOnlyList<BoardNote>? notes = null,
        IReadOnlyList<BoardPicture>? backdrop = null,
        IReadOnlyList<BoardFill>? fills = null,
        IReadOnlyList<BoardPanel>? overlays = null)
    {
        Pictures = pictures;
        Strokes = strokes;
        Lines = lines;
        Plaques = plaques;
        Notes = notes ?? Array.Empty<BoardNote>();
        Backdrop = backdrop ?? Array.Empty<BoardPicture>();
        Fills = fills ?? Array.Empty<BoardFill>();
        Overlays = overlays ?? Array.Empty<BoardPanel>();
    }

    /// <summary>The screen's own fixed art, drawn under everything: the painted background and the
    /// panels bolted to it. Separate from <see cref="Pictures"/> so a page's fills can sit over the
    /// background and still stay under the page's own pictures, which is where a list's selection
    /// bar goes.</summary>
    public IReadOnlyList<BoardPicture> Backdrop { get; }

    /// <summary>Rectangles over the backdrop and under the pictures, in draw order.</summary>
    public IReadOnlyList<BoardFill> Fills { get; }

    /// <summary>The page's own pictures in draw order.</summary>
    public IReadOnlyList<BoardPicture> Pictures { get; }

    /// <summary>Connector lines, drawn over the pictures.</summary>
    public IReadOnlyList<BoardStroke> Strokes { get; }

    /// <summary>Text over the pictures, in draw order.</summary>
    public IReadOnlyList<BoardLine> Lines { get; }

    /// <summary>Button plaques, one layer over every picture. ⚠ A screen whose authored z
    /// interleaves the two has to draw the under-side plaque as a picture instead, since this
    /// layer cannot go below one: the results card between its own two tabs is the case.</summary>
    public IReadOnlyList<BoardPlaque> Plaques { get; }

    /// <summary>List widgets whose entries flow, drawn with the text.</summary>
    public IReadOnlyList<BoardNote> Notes { get; }

    /// <summary>What is drawn over the finished screen, in order: an open drop-down list, then a
    /// dialog raised over it.</summary>
    public IReadOnlyList<BoardPanel> Overlays { get; }

    /// <summary>The strip frame a plaque draws. A four-frame strip inks the state into the art
    /// itself; a one-frame plaque keeps frame 0 and changes its label's font instead, which is
    /// what the briefing's <c>BtnLabelNormal</c>/<c>Rollover</c>/<c>Activate</c> triad is.</summary>
    public static int PlaqueFrame(int frames, bool focused, bool pressed)
    {
        if (frames < 4)
        {
            return 0;
        }

        return pressed ? 3 : focused ? 2 : 1;
    }

    /// <summary>The ink a plaque's own label takes, which is the one-frame plaque's whole focus
    /// state.</summary>
    public static BoardInk PlaqueInk(bool focused, bool pressed) =>
        pressed ? BoardInk.LabelActivate : focused ? BoardInk.LabelRollover : BoardInk.LabelNormal;

    /// <summary>The ink a messagebox button's label takes, which never reads the screen's palette:
    /// the box's own strip is dark on its normal and rollover frames and light on the depressed
    /// one, so a palette ink chosen for a paper form hides the label on it.</summary>
    public static BoardInk DialogInk(bool pressed) => pressed ? BoardInk.DialogPressed : BoardInk.Dialog;
}
