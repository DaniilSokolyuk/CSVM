using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Mech3;
using CSVM.UI.Screens;
using Godot;

namespace CSVM.UI.Boards;

/// <summary>
/// Draws a <see cref="ComposedBoard"/> over the whole window: the authored 800x600 composition
/// mapped through <see cref="BoardFit"/> and letterboxed. It samples nearest, so the original's
/// pixel grid stays hard instead of turning soft on a large display. Owns nothing but its texture
/// cache; what a screen is made of is <see cref="Campaign.CampaignBoards"/>'s, and where the cursor is the
/// shell's.
/// </summary>
public sealed partial class ComposedBoardView : Control
{
    /// <summary>The smallest face a shrinking note is scaled to. A block that will not fit its box
    /// even here is drawn at this size and runs past it. That fault is visible, where losing rows
    /// or words is silent.</summary>
    public const float MinNoteFont = 9f;

    /// <summary>The smallest face a block fitted to <see cref="BoardLine.Height"/> is stepped down
    /// to. A block that will not fit its box even here is drawn at this size and runs past it. A
    /// fault that shows is better than words silently dropped.</summary>
    public const int MinBlockPoints = 8;

    // A langui face's size is authored in points and drawn in pixels at 96 dpi. A whole-point step
    // is therefore this many pixels, and LanguiFace.Pixels is the same ratio.
    private const float PixelsPerPoint = 96f / 72f;

    // The controls hint and the focused row's description. Neither is the original's chrome, which
    // said both with a mouse pointer; a pad player has no pointer, so the board says it in words.
    private const float HintFont = 12f;

    // How many characters of a description the one-line hint band holds at the authored width.
    private const int HintCap = 110;

    // The band's own height in authored pixels: two lines and a little air.
    private const float HintBand = 32f;

    // The play count a board's movie takes. Every movie row on a screen composed here authors
    // Loops 0, the layout's own spelling of a background that never ends (docs/formats/cinemas.md).
    // The two rows that play a fixed number of times are cinema screens. A cinema screen is a flow
    // rather than a picture under one.
    private const int EndlessPlays = 0;

    // How far a synthetic italic leans, as the shear of one em. The extraction ships no italic
    // face, so a slanted draw of the board's own face stands in for the original's. The value is
    // chosen to read like the reference screenshot's note, not decoded from anything.
    private const float Slant = 0.25f;

    // How long a text caret stays lit and then dark. Nothing in the layout or the shipped scripts
    // states a period, so this one reads like a text cursor rather than being decoded from any.
    private const double CaretBlinkSeconds = 0.5;

    // How much heavier a bold draw is than the board's own face, as Godot's own embolden amount.
    // The extraction ships no second typeface either, so this stands in for the weight the load
    // screen's two headings differ by. The value is chosen to read like that screenshot.
    private const float Weight = 0.5f;

    // The OpenType weight and stretch classes a langui face is looked up at.
    private const int RegularWeight = 400;
    private const int BoldWeight = 700;
    private const int FullStretch = 100;

    private readonly Dictionary<string, Texture2D?> _textures = new();

    // The textures of the in-memory pictures the board last shown draws, by image.
    private readonly Dictionary<Image, Texture2D> _held = new(ReferenceEqualityComparer.Instance);

    // The installed fonts langui faces resolved to, by tag, a machine lacking one caching null.
    private readonly Dictionary<string, Font?> _faces = new();

    // The movies behind the boards, one per file, kept beside the texture cache rather than in it.
    // A picture is drawn from the same ImageTexture forever, and only the surface knows when that
    // texture's pixels changed. A file that does not open caches a null so it is tried once.
    private readonly Dictionary<string, MovieSurface?> _movies = new();

    // The moving layer's own canvas item, a child of this control's. Its commands reach the server
    // as they are asked for. A Control's own _Draw is a callback the main loop flushes. This layer
    // is therefore the only part of a board a blocked frame loop can still change.
    private Rid _motion;
    private IReadOnlyList<BoardPicture> _moving = Array.Empty<BoardPicture>();

    private FontVariation? _slanted;
    private FontVariation? _emboldened;

    // The caret's blink: how far into the current half-period the board is, and whether that half
    // is the lit one. The last says whether anything on the board carries a caret at all.
    private double _caretClock;
    private bool _caretLit = true;
    private bool _caretOnBoard;

    private ComposedBoard? _board;
    private string _dataRoot = string.Empty;
    private string _detail = string.Empty;
    private string _footer = string.Empty;
    private BoardPalette _palette = BoardPalette.Paper;

    /// <summary>The board last handed to <see cref="Show"/>, which is what the surface is
    /// drawing.</summary>
    public ComposedBoard? Board => _board;

    /// <summary>The pictures last handed to <see cref="PresentMoving"/>, which is what the moving
    /// layer holds over that board.</summary>
    public IReadOnlyList<BoardPicture> Moving => _moving;

    /// <summary>Builds the view over the extraction root its art loads from.</summary>
    public static ComposedBoardView Build(string dataRoot)
    {
        var view = new ComposedBoardView
        {
            _dataRoot = dataRoot,
            MouseFilter = MouseFilterEnum.Ignore,
            TextureFilter = TextureFilterEnum.Nearest,
        };
        view.SetAnchorsPreset(LayoutPreset.FullRect);
        return view;
    }

    /// <summary>How tall an entry of <paramref name="note"/> draws, in authored pixels, for the
    /// font and scale a frame is being drawn at. This is the measurement <see cref="BoardNote"/>
    /// cannot make for itself, and the only reason a flowed list is not composed engine-free.</summary>
    public static Func<string, float, float> Measure(BoardFit fit, Font font, BoardNote note)
    {
        var box = MeasureBox(fit, font, note.Size);
        return (text, width) => box(text, width).Y;
    }

    /// <summary>How wide and how tall a wrapped entry draws at <paramref name="size"/>, in authored
    /// pixels. The width is the widest line the wrap produced, which a word too long to break can
    /// push past the measure it was wrapped to.</summary>
    public static Func<string, float, Vector2> MeasureBox(BoardFit fit, Font font, float size)
    {
        ArgumentNullException.ThrowIfNull(fit);
        ArgumentNullException.ThrowIfNull(font);
        int points = Mathf.Max(1, Mathf.RoundToInt(fit.Length(size)));
        return (text, width) => font.GetMultilineStringSize(
            text, HorizontalAlignment.Left, fit.Length(width), points) / fit.Scale;
    }

    /// <summary>How tall a line's wrapped block draws, in authored pixels, at the size and pitch it
    /// carries. How many lines the words wrap to is a font metric, so this is a measurement a
    /// composed board cannot make for itself.</summary>
    public static float Block(BoardFit fit, Font font, BoardLine line)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(line);
        int points = Mathf.Max(1, Mathf.RoundToInt(fit.Length(line.Size)));
        float pitch = line.Leading > 0f ? line.Leading : font.GetHeight(points) / fit.Scale;
        if (line.Width <= 0f || line.Text.Length == 0)
        {
            return pitch;
        }

        int rows = 0;
        foreach (var _ in Wrap(font, line.Text, points, fit.Length(line.Width)))
        {
            rows++;
        }

        return rows * pitch;
    }

    /// <summary>The line at the largest whole point size, its own or smaller, whose wrapped block
    /// fits <see cref="BoardLine.Height"/>. A line carrying no box, no wrap width or no words comes
    /// back as it stands. One that still will not fit at <see cref="MinBlockPoints"/> is returned
    /// there rather than clipped.</summary>
    public static BoardLine Fitted(BoardFit fit, Font font, BoardLine line)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(line);
        if (line.Height <= 0f || line.Width <= 0f || line.Size <= 0f || line.Text.Length == 0)
        {
            return line;
        }

        int authored = Mathf.Max(1, Mathf.RoundToInt(line.Size / PixelsPerPoint));
        for (int points = authored; points > MinBlockPoints; points--)
        {
            var candidate = points == authored ? line : Sized(line, points * PixelsPerPoint);
            if (Block(fit, font, candidate) <= line.Height)
            {
                return candidate;
            }
        }

        return Sized(line, MinBlockPoints * PixelsPerPoint);
    }

    /// <summary>The note at the largest whole face size, its own or smaller, whose entries all fit
    /// its box. A note that may not shrink comes back unchanged. A block that still will not fit
    /// at <see cref="MinNoteFont"/> is drawn there rather than losing rows.</summary>
    public static BoardNote Fitted(BoardFit fit, Font font, BoardNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (!note.Shrink || note.Height <= 0f || note.Entries.Count == 0)
        {
            return note;
        }

        for (float size = note.Size; size > MinNoteFont; size -= 1f)
        {
            if (Fits(MeasureBox(fit, font, size), note))
            {
                return size == note.Size ? note : note with { Size = size };
            }
        }

        return note with { Size = MinNoteFont };
    }

    /// <summary>Advances every movie this view has opened by that many seconds, answering whether
    /// any of them put a new picture in its texture. ⚠ On a deterministic run, hand it a step that
    /// does not come from the wall clock. Otherwise the picture a capture lands on is a property
    /// of the machine rather than of the frame count. See <c>docs/verification.md</c>'s
    /// DET-7.</summary>
    public bool AdvanceMovies(double elapsedSeconds)
    {
        bool changed = false;
        foreach (var movie in _movies.Values)
        {
            if (movie != null)
            {
                changed |= movie.Advance(elapsedSeconds);
            }
        }

        return changed;
    }

    /// <summary>Advances the text caret's blink by that many seconds, answering whether the picture
    /// changed and the board wants repainting. Takes its step from the caller for the reason
    /// <see cref="AdvanceMovies"/> does. A clock read here would make the frame a capture lands on
    /// a property of the machine.</summary>
    public bool AdvanceCaret(double elapsedSeconds)
    {
        _caretClock += elapsedSeconds;
        bool flipped = false;
        while (_caretClock >= CaretBlinkSeconds)
        {
            _caretClock -= CaretBlinkSeconds;
            _caretLit = !_caretLit;
            flipped = true;
        }

        return flipped && _caretOnBoard;
    }

    /// <summary>One bitmap's own size in its own pixels, or zero where the extraction does not
    /// carry it. The one measurement a composed board cannot make for itself: a progress fill is a
    /// pixel clip against the fill bitmap's own width.</summary>
    public Vector2 ArtSize(BoardArt art) => Load(art) is { } texture ? texture.GetSize() : Vector2.Zero;

    /// <summary>The installed font a langui face names, or null on a machine without the family.
    /// A miss keeps the board's own face rather than the system's arbitrary substitute. Looked up
    /// once per tag, a miss included. A caller measuring a composed line measures in this face:
    /// the board's own sets to another width and wraps to another line count.</summary>
    public Font? Installed(LanguiFace face)
    {
        if (_faces.TryGetValue(face.Tag, out var cached))
        {
            return cached;
        }

        int weight = face.Bold ? BoldWeight : RegularWeight;
        Font? font = null;
        if (OS.GetSystemFontPath(face.Family, weight, FullStretch, face.Italic).Length > 0)
        {
            font = new SystemFont
            {
                FontNames = new[] { face.Family },
                FontWeight = weight,
                FontItalic = face.Italic,
            };
        }

        _faces[face.Tag] = font;
        return font;
    }

    /// <summary>Puts a composed board on screen, with the two lines the shell adds under it.</summary>
    public void Show(ComposedBoard board, BoardPalette palette, string detail, string footer)
    {
        ArgumentNullException.ThrowIfNull(board);
        bool caret = HasCaret(board);
        if (caret && !_caretOnBoard)
        {
            // A box that has just taken the focus shows its caret at once. The blink starts from
            // the press, not from wherever the last one left the phase.
            _caretClock = 0d;
            _caretLit = true;
        }

        _caretOnBoard = caret;
        ForgetHeld(board);
        _board = board;
        _palette = palette;
        _detail = detail;
        _footer = footer;
        QueueRedraw();
    }

    /// <summary>Puts the pictures that move over the still board on screen at once, and asks for a
    /// frame. This is the one repaint a caller holding the frame loop can still make. The pictures
    /// reach the server here rather than being queued for a redraw callback that loop would have
    /// to reach. They stay up until the next call replaces them.
    /// ⚠ Keep them out of the board handed to <see cref="Show"/>, or each one draws twice.</summary>
    public void PresentMoving(IReadOnlyList<BoardPicture> pictures)
    {
        ArgumentNullException.ThrowIfNull(pictures);
        _moving = pictures;
        PaintMoving();
        RenderingServer.ForceDraw();
    }

    /// <inheritdoc/>
    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        var fit = BoardFit.For(size.X, size.Y);
        DrawRect(new Rect2(Vector2.Zero, size), Colors.Black);
        if (_board is not { } board)
        {
            return;
        }

        foreach (var picture in board.Backdrop)
        {
            DrawPicture(fit, picture);
        }

        foreach (var fill in board.Fills)
        {
            DrawFill(fit, fill);
        }

        foreach (var picture in board.Pictures)
        {
            DrawPicture(fit, picture);
        }

        foreach (var stroke in board.Strokes)
        {
            DrawLine(
                new Vector2(fit.X(stroke.X1), fit.Y(stroke.Y1)),
                new Vector2(fit.X(stroke.X2), fit.Y(stroke.Y2)),
                new Color(stroke.R / 255f, stroke.G / 255f, stroke.B / 255f, stroke.Opacity),
                Mathf.Max(1f, fit.Length(2f)));
        }

        var font = GetThemeDefaultFont();
        foreach (var line in board.Lines)
        {
            DrawText(fit, Face(font, line), line);
        }

        foreach (var note in board.Notes)
        {
            DrawNote(fit, font, note);
        }

        foreach (var plaque in board.Plaques)
        {
            DrawPlaque(fit, font, plaque);
        }

        // Over the finished screen: an open drop-down list covers the widgets it hangs across, and
        // a dialog covers the list too.
        foreach (var panel in board.Overlays)
        {
            foreach (var fill in panel.Fills)
            {
                DrawFill(fit, fill);
            }

            foreach (var picture in panel.Pictures)
            {
                DrawPicture(fit, picture);
            }

            foreach (var line in panel.Lines)
            {
                DrawText(fit, Face(font, line), line);
            }
        }

        DrawHints(fit, font);

        // The moving layer sits in its own canvas item. A resize re-runs this draw, and has to
        // re-place that layer at the new fit. Nothing else touches it between pumps.
        PaintMoving();
    }

    /// <inheritdoc/>
    public override void _Notification(int what)
    {
        if (what == NotificationPredelete && _motion.IsValid)
        {
            RenderingServer.FreeRid(_motion);
            _motion = default;
        }
    }

    // The hint band is one line, so a multi-line description is joined and cut rather than allowed
    // to run off both edges of the board.
    private static string Flatten(string text)
    {
        string one = text.Replace('\n', ' ').Replace('\r', ' ');
        return one.Length <= HintCap ? one : one[..HintCap] + "…";
    }

    // Whether any text on the board carries a caret, which is what decides that the blink is worth
    // a repaint. An overlay's lines count: a dialog's own edit box would draw there.
    private static bool HasCaret(ComposedBoard board)
    {
        foreach (var line in board.Lines)
        {
            if (line.Caret != null)
            {
                return true;
            }
        }

        foreach (var panel in board.Overlays)
        {
            foreach (var line in panel.Lines)
            {
                if (line.Caret != null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // One frame of a stacked strip, in texture pixels. A strip's frames divide its height evenly,
    // and a name the extraction is missing simply draws nothing.
    private static Rect2 FrameRect(Texture2D texture, int frames, int frame)
    {
        var size = texture.GetSize();
        int count = Mathf.Max(1, frames);
        float height = Mathf.Floor(size.Y / count);
        float top = Mathf.Clamp(frame, 0, count - 1) * height;
        return new Rect2(0f, top, size.X, height);
    }

    // Greedy word wrap in one face at one size: the words that fit a width, in order. Each authored
    // line break starts a new line, and an empty paragraph stands as an empty line. A single word
    // longer than the width stands on its own line rather than being broken mid-word. A paragraph's
    // leading spaces are its indent and are kept.
    private static IEnumerable<string> Wrap(Font font, string text, int points, float width)
    {
        foreach (var paragraph in text.Replace("\r", string.Empty).Split('\n'))
        {
            var line = new StringBuilder();
            bool first = true;
            foreach (var word in paragraph.Split(' '))
            {
                string candidate = first ? word : line + " " + word;
                if (!first && line.ToString().Trim().Length > 0
                    && font.GetStringSize(candidate, HorizontalAlignment.Left, -1f, points).X > width)
                {
                    yield return line.ToString();
                    line.Clear().Append(word);
                    continue;
                }

                line.Clear().Append(candidate);
                first = false;
            }

            yield return line.ToString();
        }
    }

    // Keep Godot's loader and texture color-space handling; only the uncommon PNG transfer curve
    // needs correcting in its decoded pixels.
    private static void NormalizeGamma(Image image, uint gamma)
    {
        float exponent = gamma / (float)PngImage.UiGamma;
        for (int y = 0; y < image.GetHeight(); y++)
        {
            for (int x = 0; x < image.GetWidth(); x++)
            {
                var colour = image.GetPixel(x, y);
                colour.R = Mathf.Pow(colour.R, exponent);
                colour.G = Mathf.Pow(colour.G, exponent);
                colour.B = Mathf.Pow(colour.B, exponent);
                image.SetPixel(x, y, colour);
            }
        }
    }

    // One line at another face size, its authored pitch scaled with it. A block the original
    // pitches at its own face size stays pitched at whatever size it ends up drawn at.
    private static BoardLine Sized(BoardLine line, float size) => line with
    {
        Size = size,
        Leading = line.Leading > 0f ? line.Leading * size / line.Size : 0f,
    };

    // Every entry of a wrapped block fits the box the note carries, in both axes.
    private static bool Fits(Func<string, float, Vector2> box, BoardNote note)
    {
        float tall = -note.Spacing;
        foreach (string entry in note.Entries)
        {
            var drawn = box(entry, note.Width);
            if (drawn.X > note.Width)
            {
                return false;
            }

            tall += drawn.Y + note.Spacing;
        }

        return tall <= note.Height;
    }

    // The palette a piece of text takes. A plaque's label is the one place the state is in the ink
    // rather than in the art. That is what the original's three label fonts are.
    private Color InkOf(BoardInk ink) => ink switch
    {
        BoardInk.RowFocused => _palette.Focus,
        BoardInk.Heading => _palette.Heading,
        BoardInk.Detail => _palette.Detail,
        BoardInk.LabelNormal => _palette.LabelNormal,
        BoardInk.LabelRollover => _palette.LabelRollover,
        BoardInk.LabelActivate => _palette.LabelActivate,
        BoardInk.Dialog => Colors.White,
        BoardInk.DialogPressed => Colors.Black,
        BoardInk.Secret => Colors.Yellow,
        BoardInk.Alarm => Colors.Red,
        BoardInk.Seat1 => SplitScreen.PlayerColor(0),
        BoardInk.Seat2 => SplitScreen.PlayerColor(1),
        BoardInk.Seat3 => SplitScreen.PlayerColor(2),
        BoardInk.Seat4 => SplitScreen.PlayerColor(3),
        _ => _palette.Row,
    };

    // A line's authored colour where it carries one, else its ink.
    private Color InkOf(BoardLine line) =>
        line.Colour is { } rgb ? new Color(rgb.R / 255f, rgb.G / 255f, rgb.B / 255f) : InkOf(line.Ink);

    // The face one line draws in: the langui face it names where the machine has it installed,
    // else a variation of the board's own. A slant wins over a weight rather than the two
    // compounding into a face nothing authored.
    private Font? Face(Font? font, BoardLine line)
    {
        if (line.Face is { } face && Installed(face) is { } installed)
        {
            return installed;
        }

        return line.Italic ? Slanted(font) : line.Bold ? Emboldened(font) : font;
    }

    // The board's own face at a heavier weight, built once, for a screen that writes two authored
    // faces side by side.
    private FontVariation? Emboldened(Font? font)
    {
        if (font == null)
        {
            return null;
        }

        _emboldened ??= new FontVariation { BaseFont = font, VariationEmbolden = Weight };
        return _emboldened;
    }

    // The board's own face sheared into an oblique, built once. Godot's variation transform is a
    // 2x3 matrix over the glyph outline, so the x-shear is the whole of the lean.
    private FontVariation? Slanted(Font? font)
    {
        if (font == null)
        {
            return null;
        }

        if (_slanted == null)
        {
            _slanted = new FontVariation { BaseFont = font };
            _slanted.VariationTransform = new Transform2D(
                new Vector2(1f, 0f), new Vector2(Slant, 1f), Vector2.Zero);
        }

        return _slanted;
    }

    private void DrawFill(BoardFit fit, BoardFill fill)
    {
        var colour = fill.Ink is { } ink
            ? InkOf(ink) with { A = fill.Opacity }
            : new Color(fill.R / 255f, fill.G / 255f, fill.B / 255f, fill.Opacity);
        var box = new Rect2(
            fit.X(fill.X), fit.Y(fill.Y), fit.Length(fill.Width), fit.Length(fill.Height));
        if (!fill.Border)
        {
            DrawRect(box, colour);
            return;
        }

        // An outline of one authored pixel, at least one real one however small the board is
        // drawn. A sub-pixel border is a border nobody sees.
        DrawRect(box, colour, filled: false, Mathf.Max(1f, fit.Length(1f)));
    }

    // Where one picture lands and which part of its bitmap it takes, both in this control's own
    // pixels. The result is null where the extraction does not carry the art.
    private (Texture2D Texture, Rect2 Dest, Rect2 Src, Color Tint)? Placed(
        BoardFit fit, BoardPicture picture)
    {
        if (Load(picture.Art) is not { } texture)
        {
            return null;
        }

        var frame = FrameRect(texture, picture.Art.Frames, picture.Frame);
        if (picture.Crop is { } crop)
        {
            frame = frame.Intersection(
                new Rect2(frame.Position.X + crop.X, frame.Position.Y + crop.Y, crop.Width, crop.Height));
        }

        var span = new Vector2(
            fit.Length(picture.Width > 0f ? picture.Width : frame.Size.X),
            fit.Length(picture.Height > 0f ? picture.Height : frame.Size.Y));
        var at = new Vector2(fit.X(picture.X), fit.Y(picture.Y));
        if (picture.Centered)
        {
            at -= span / 2f;
        }

        // Growing about the middle rather than the corner. A scrap swelling under the cursor then
        // stays where the page put it, instead of creeping down and to the right.
        if (picture.Scale != 1f)
        {
            var grown = span * picture.Scale;
            at -= (grown - span) / 2f;
            span = grown;
        }

        var tint = picture.Tint is { } rgb
            ? new Color(rgb.R / 255f, rgb.G / 255f, rgb.B / 255f, Mathf.Clamp(picture.Opacity, 0f, 1f))
            : new Color(1f, 1f, 1f, Mathf.Clamp(picture.Opacity, 0f, 1f));
        return (texture, new Rect2(at, span), frame, tint);
    }

    // The moving pictures, re-issued to their own canvas item at the current fit. Cleared first,
    // so the layer holds the last call's pictures and nothing older.
    private void PaintMoving()
    {
        if (_moving.Count == 0 && !_motion.IsValid)
        {
            return;
        }

        if (!_motion.IsValid)
        {
            _motion = RenderingServer.CanvasItemCreate();
            RenderingServer.CanvasItemSetParent(_motion, GetCanvasItem());
            RenderingServer.CanvasItemSetDefaultTextureFilter(
                _motion, RenderingServer.CanvasItemTextureFilter.Nearest);
        }

        RenderingServer.CanvasItemClear(_motion);
        var fit = BoardFit.For(GetViewportRect().Size.X, GetViewportRect().Size.Y);
        foreach (var picture in _moving)
        {
            if (Placed(fit, picture) is not { } placed)
            {
                continue;
            }

            if (picture.Revs == 0f)
            {
                RenderingServer.CanvasItemAddTextureRectRegion(
                    _motion, placed.Dest, placed.Texture.GetRid(), placed.Src, placed.Tint);
                continue;
            }

            var span = placed.Dest.Size;
            RenderingServer.CanvasItemAddSetTransform(
                _motion,
                new Transform2D(picture.Revs * Mathf.Tau, placed.Dest.Position + (span / 2f)));
            RenderingServer.CanvasItemAddTextureRectRegion(
                _motion, new Rect2(-span / 2f, span), placed.Texture.GetRid(), placed.Src, placed.Tint);
            RenderingServer.CanvasItemAddSetTransform(_motion, Transform2D.Identity);
        }
    }

    private void DrawPicture(BoardFit fit, BoardPicture picture)
    {
        if (Placed(fit, picture) is not { } placed)
        {
            return;
        }

        if (picture.Revs == 0f)
        {
            DrawTextureRectRegion(placed.Texture, placed.Dest, placed.Src, placed.Tint);
            return;
        }

        // A spin turns the element about its own middle, where the script's own centred placement
        // puts it. Drawing through a transform keeps the frame region intact.
        var span = placed.Dest.Size;
        DrawSetTransform(placed.Dest.Position + (span / 2f), picture.Revs * Mathf.Tau, Vector2.One);
        DrawTextureRectRegion(placed.Texture, new Rect2(-span / 2f, span), placed.Src, placed.Tint);
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    private void DrawPlaque(BoardFit fit, Font? font, BoardPlaque plaque)
    {
        if (Load(plaque.Art) is not { } texture)
        {
            return;
        }

        var frame = FrameRect(texture, plaque.Art.Frames, plaque.Frame);
        var span = new Vector2(fit.Length(frame.Size.X), fit.Length(frame.Size.Y));
        var at = new Vector2(fit.X(plaque.X), fit.Y(plaque.Y));
        DrawTextureRectRegion(texture, new Rect2(at, span), frame, Colors.White);
        if (plaque.Label.Length == 0 || font == null)
        {
            return;
        }

        // A one-frame plaque bakes no words in, so the label is drawn over it. It is centred in
        // the frame and shadowed the way the original's own outlined face reads. A strip carrying
        // its own baseline is one whose plaque stands somewhere other than its frame's middle.
        int points = Mathf.Max(1, Mathf.RoundToInt(fit.Length(13f)));
        float baseline = plaque.LabelBaseline > 0f
            ? at.Y + fit.Length(plaque.LabelBaseline)
            : at.Y + (span.Y / 2f) + (points * 0.38f);
        DrawString(font, new Vector2(at.X + 1f, baseline + 1f), plaque.Label,
            HorizontalAlignment.Center, span.X, points, Colors.Black);
        DrawString(font, new Vector2(at.X, baseline), plaque.Label,
            HorizontalAlignment.Center, span.X, points, InkOf(plaque.Ink));
    }

    private void DrawText(BoardFit fit, Font? font, BoardLine line)
    {
        if (font == null)
        {
            return;
        }

        line = Fitted(fit, font, line);
        int points = Mathf.Max(1, Mathf.RoundToInt(fit.Length(line.Size)));
        var at = new Vector2(fit.X(line.X), fit.Y(line.Y) + points);
        DrawCaret(fit, font, line, points);
        if (line.Glyph is { } control)
        {
            DrawGlyphLine(font, line, control, points, at);
            return;
        }

        if (line.Text.Length == 0)
        {
            return;
        }

        if (line.Width <= 0f)
        {
            DrawString(font, at, line.Text, HorizontalAlignment.Left, -1f, points, InkOf(line));
            return;
        }

        if (line.Leading > 0f)
        {
            DrawPitched(fit, font, line, points, at);
            return;
        }

        // Wrapped, because a description panel's text is a block. A row's own text may still be
        // longer than the widget it sits in, and a single-line draw would run off the board.
        var justify = line.Justify switch
        {
            BoardJustify.Right => HorizontalAlignment.Right,
            BoardJustify.Center => HorizontalAlignment.Center,
            _ => HorizontalAlignment.Left,
        };
        DrawMultilineString(font, at, line.Text, justify, fit.Length(line.Width),
            points, -1, InkOf(line));
    }

    // A line with a pad control in it, drawn through the composition the flight prompts use: words,
    // picture, words. The measurement is why the line comes through here rather than being spaced
    // by its composer.
    private void DrawGlyphLine(Font font, BoardLine line, GlyphKey glyph, int points, Vector2 baseline)
    {
        int slot = line.Text.IndexOf(BoardLine.GlyphSlot, StringComparison.Ordinal);
        var prompt = slot < 0
            ? ControlLine.Plain(line.Text)
            : ControlLine.Around(line.Text[..slot], line.Text[(slot + BoardLine.GlyphSlot.Length)..], glyph);
        prompt.Draw(this, font, points, new Vector2(baseline.X, baseline.Y - font.GetAscent(points)), InkOf(line));
    }

    // The edit box's cursor after the text it follows, on the lit half of the blink. The text is
    // measured here because the board cannot: a caret that counted characters would sit wrong on
    // every proportional face. It stays inside the box, so a full line does not push it off.
    private void DrawCaret(BoardFit fit, Font font, BoardLine line, int points)
    {
        if (line.Caret is not { } caret || !_caretLit)
        {
            return;
        }

        float left = fit.X(line.X);
        float x = left + (line.Text.Length > 0
            ? font.GetStringSize(line.Text, HorizontalAlignment.Left, -1f, points).X
            : 0f);
        if (line.Width > 0f)
        {
            x = Mathf.Min(x, left + fit.Length(line.Width - caret.Width));
        }

        DrawRect(
            new Rect2(x, fit.Y(line.Y), fit.Length(caret.Width), fit.Length(caret.Height)),
            new Color(caret.R / 255f, caret.G / 255f, caret.B / 255f));
    }

    // Wrapped at the widget's own line pitch rather than the face's. Godot's multiline draw spaces
    // by the font's metrics. On a face other than the authored one, that runs a block past the
    // artwork it was written to sit inside. A justification moves the block as a whole, its lines
    // left-aligned under the widest, which is how the original sets a centred multi-line title.
    private void DrawPitched(BoardFit fit, Font font, BoardLine line, int points, Vector2 at)
    {
        float step = fit.Length(line.Leading);
        float width = fit.Length(line.Width);
        var parts = new List<string>(Wrap(font, line.Text, points, width));
        float widest = 0f;
        foreach (var part in parts)
        {
            widest = Math.Max(widest, font.GetStringSize(part, HorizontalAlignment.Left, -1f, points).X);
        }

        at.X += line.Justify switch
        {
            BoardJustify.Right => Math.Max(0f, width - widest),
            BoardJustify.Center => Math.Max(0f, (width - widest) / 2f),
            _ => 0f,
        };
        foreach (var part in parts)
        {
            DrawString(font, at, part, HorizontalAlignment.Left, -1f, points, InkOf(line));
            at.Y += step;
        }
    }

    private void DrawNote(BoardFit fit, Font? font, BoardNote note)
    {
        if (font == null)
        {
            return;
        }

        note = Fitted(fit, font, note);
        var height = Measure(fit, font, note);
        if (note.Counted is { } counted)
        {
            // Hands back the measurement the composer could not make, for the frame after this
            // one. A scrolled box's window is counted in lines, and lines are a font metric.
            var rows = note.Rows(height);
            counted(rows.Total, rows.Fits);
        }

        foreach (var line in note.Flow(height))
        {
            DrawText(fit, Face(font, line), line);
        }

        // Over the rows rather than under them: the mark is a brush stroke across the words it
        // marks, and the original draws it last.
        foreach (var mark in note.Marks(height))
        {
            DrawPicture(fit, mark);
        }
    }

    private void DrawHints(BoardFit fit, Font? font)
    {
        if (font == null)
        {
            return;
        }

        int points = Mathf.Max(1, Mathf.RoundToInt(fit.Length(HintFont)));
        float width = fit.Length(BoardFit.AuthoredWidth);
        if (_detail.Length > 0 || _footer.Length > 0)
        {
            // A scrim under the band, so it reads as something laid over the screen rather than as
            // words stuck to the artwork. It stays legible on a light board and a dark one alike.
            DrawRect(
                new Rect2(fit.X(0f), fit.Y(0f), width, fit.Length(HintBand)),
                new Color(0f, 0f, 0f, 0.45f));
        }

        for (int i = 0; i < 2; i++)
        {
            string text = Flatten(i == 0 ? _detail : _footer);
            if (text.Length == 0)
            {
                continue;
            }

            var at = new Vector2(fit.X(0f), fit.Y(2f + (i * 15f)) + points);
            DrawString(font, at + Vector2.One, text, HorizontalAlignment.Center, width, points, Colors.Black);
            DrawString(font, at, text, HorizontalAlignment.Center, width, points, _palette.Hint);
        }
    }

    // The decoded texture for a board bitmap, kept including a miss so an absent extraction is
    // probed once per name rather than once per frame. Godot's own loader reads the JPG the
    // screen backgrounds ship as, which no engine-free decoder here covers.
    private Texture2D? Load(BoardArt art)
    {
        if (art.Library == BoardArtLibrary.Held)
        {
            return Held(art.Pixels);
        }

        string path = art.Library switch
        {
            BoardArtLibrary.Rimage =>
                Path.Combine(_dataRoot, "extracted", "rimage", art.Name.ToLowerInvariant() + ".png"),
            BoardArtLibrary.Loose => art.Name,
            BoardArtLibrary.Movie => SessionPaths.Cinema(_dataRoot, art.Name),
            _ => Path.Combine(_dataRoot, "extracted", "rof", "ASSETS", "GRAPHICS", art.Name),
        };
        if (_textures.TryGetValue(path, out var cached))
        {
            return cached;
        }

        Texture2D? texture = null;
        if (art.Library == BoardArtLibrary.Movie)
        {
            // The surface rewrites this one texture in place for the life of the view. The cache
            // above needs no invalidation, and the picture animates with nothing else done to it.
            var movie = MovieSurface.Open(path, EndlessPlays);
            _movies[path] = movie;
            texture = movie?.Texture;
        }
        else if (File.Exists(path) && Image.LoadFromFile(path) is { } image && !image.IsEmpty())
        {
            if (Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                && PngImage.TryReadGamma(path) is { } gamma && gamma != PngImage.UiGamma)
            {
                NormalizeGamma(image, gamma);
            }
            texture = ImageTexture.CreateFromImage(image);
        }

        _textures[path] = texture;
        return texture;
    }

    // One texture per in-memory picture, made on its first draw rather than every repaint. Keyed by
    // the image itself: two runs can photograph under the same file name.
    private Texture2D? Held(Image? pixels)
    {
        if (pixels == null || pixels.IsEmpty())
        {
            return null;
        }

        if (!_held.TryGetValue(pixels, out var texture))
        {
            texture = ImageTexture.CreateFromImage(pixels);
            _held[pixels] = texture;
        }

        return texture;
    }

    // Drops the textures of held pictures the new board no longer draws. A page shown once per
    // stunt run then keeps no earlier run's photographs alive.
    private void ForgetHeld(ComposedBoard board)
    {
        if (_held.Count == 0)
        {
            return;
        }

        var kept = new HashSet<Image>(ReferenceEqualityComparer.Instance);
        foreach (var picture in board.Pictures)
        {
            if (picture.Art.Pixels is { } pixels)
            {
                kept.Add(pixels);
            }
        }

        foreach (var pixels in new List<Image>(_held.Keys))
        {
            if (!kept.Contains(pixels))
            {
                _held.Remove(pixels);
            }
        }
    }
}
