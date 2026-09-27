using System.Globalization;
using CSVM.Bindings;
using Godot;

namespace CSVM.UI.Boards;

/// <summary>One control as a picture set addresses it. It is keyed the way
/// <see cref="BindingControl"/> is: kind, index, axis sign and hat direction. Deadzone and
/// modifiers are left out, since they decide when a control fires and not how it looks.
/// The Stick member holds the flight stick's caption prefix ("R"), and is null for other devices.
/// ⚠ A stick's button 0 and a pad's A share kind and index, so without it a stick draws pad art.
/// </summary>
public readonly record struct GlyphKey(
    ControlKind Kind, int Index, int Sign, HatDirection Direction = HatDirection.None, string? Stick = null)
{
    /// <summary>The key one binding's control sits on, its stick named the way its caption is.</summary>
    public static GlyphKey Of(Binding binding) => Of(binding, BindingLabels.StickName);

    /// <summary><see cref="Of(Binding)"/> with the stick names taken from
    /// <paramref name="stickName"/> instead of the registered one.</summary>
    public static GlyphKey Of(Binding binding, System.Func<DeviceId, string?>? stickName)
    {
        var c = binding.Control;
        return new(c.Kind, c.Index, c.Sign, c.Direction, stickName?.Invoke(binding.Device));
    }
}

/// <summary>
/// The glyph set in force, and the one question a composing site asks it: does this control draw as
/// a picture. One holder rather than a parameter on every prompt, so swapping the whole set is one
/// assignment and no composing site has to be told about it.
/// </summary>
public static class ControlGlyphs
{
    /// <summary>The A button as a glyph key. A gesture read raw off a device has no binding to name
    /// it (<see cref="Screens.MenuInput.SignOnPressed"/>), so a line prompting one names the button itself.
    /// Every prompt that does stand on a binding goes through <see cref="For"/> instead.</summary>
    public static readonly GlyphKey PadA = new(ControlKind.Button, (int)JoyButton.A, 0);

    /// <summary>The B button, the sign-off half of the pair above.</summary>
    public static readonly GlyphKey PadB = new(ControlKind.Button, (int)JoyButton.B, 0);

    /// <summary>The Start button, likewise read raw.</summary>
    public static readonly GlyphKey PadStart = new(ControlKind.Button, (int)JoyButton.Start, 0);

    private static ControlGlyphSet _set = new PromptFontGlyphs();

    /// <summary>The set every control line draws through. Assigning a different one changes every
    /// prompt and board hint at once; null puts the shipped set back.</summary>
    public static ControlGlyphSet Set
    {
        get => _set;
        set => _set = value ?? new PromptFontGlyphs();
    }

    /// <summary>The glyph that stands for <paramref name="binding"/>'s control, or null where the set
    /// draws none and the line keeps its words.</summary>
    public static GlyphKey? For(Binding binding)
    {
        var key = GlyphKey.Of(binding);
        return _set.Draws(key) ? key : null;
    }
}

/// <summary>
/// A per-control picture set: which controls it draws, how wide one is at a line's height, and how
/// to draw one. A set that declines a control leaves the line its words, which is how a keyboard
/// seat reads its own keys. Swappable whole through <see cref="ControlGlyphs.Set"/>, because the
/// original ships no such art to copy and how these should look is a judgement at the controls.
/// </summary>
public abstract class ControlGlyphSet
{
    /// <summary>Whether this set has a picture for that control.</summary>
    public abstract bool Draws(GlyphKey key);

    /// <summary>How wide the picture is when it is <paramref name="height"/> tall.</summary>
    public abstract float Width(Font font, GlyphKey key, float height);

    /// <summary>Draws the control inside <paramref name="box"/>, in <paramref name="color"/>.
    /// </summary>
    public abstract void Draw(CanvasItem into, Font font, GlyphKey key, Rect2 box, Color color);
}

/// <summary>
/// The shipped set: pad controls as characters of PromptFont (SIL OFL 1.1). Its neutral glyphs
/// draw a face button, d-pad or stick as its cluster, cross or stick, the pressed part marked. Shoulders and
/// triggers use the font's Xbox-lettered glyphs, the font having no neutral ones. A button with no
/// glyph, or every control when the font is missing, draws as a lettered plaque. A flight stick's
/// button or hat draws as the font's flight stick followed by its <see cref="StickMark"/>. It
/// declines keys, mouse buttons, pad hats and stick axes, so those keep the words
/// <see cref="BindingLabels.Describe"/> gives.
/// </summary>
public sealed class PromptFontGlyphs : ControlGlyphSet
{
    /// <summary>The font file. Named with an extension Godot does not import, so an export's import
    /// step leaves no untracked <c>.import</c> beside it; the export's include filter packs it.</summary>
    public const string FontPath = "res://data/promptfont.ttf.bin";

    /// <summary>PromptFont's flight stick, the picture every stick control starts with.</summary>
    public const string FlightStick = "\U0001F57D";

    /// <summary>The stick caption that draws the flight stick mirrored, as a left hand holds it: the
    /// name a HOSAS pair's left profile carries. TUNE: a convention, not a property of the device.
    /// </summary>
    public const string LeftHandStick = "L";

    // Metrics as fractions of the glyph's height, so the set scales with the line it sits on.
    // All TUNE: nothing in the original fixes them.
    private const float GlyphScale = 1.0f;
    private const float Stroke = 0.09f;
    private const float LetterHeight = 0.52f;
    private const float PlaquePad = 0.30f;

    private static FontFile? _face;
    private static bool _loaded;

    /// <summary>Gets the font's face for a caller drawing its own PromptFont characters, or null
    /// when the font file is missing.</summary>
    public static FontFile? Font => Face();

    /// <summary>What follows the flight stick for a stick control, or null for an axis, whose
    /// number names nothing a player can find on the stick. A button is its number counted from 1,
    /// as its caption counts, in the font's filled button digits. Button 28 is a filled 2 and a
    /// filled 8. A hat direction is the d-pad arm, a hat being a small d-pad.</summary>
    public static string? StickMark(GlyphKey key)
    {
        if (key.Kind == ControlKind.Hat)
        {
            return key.Direction switch
            {
                HatDirection.Up => "↟",
                HatDirection.Down => "↡",
                HatDirection.Left => "↞",
                HatDirection.Right => "↠",
                _ => null,
            };
        }

        if (key.Kind != ControlKind.Button)
        {
            return null;
        }

        // PromptFont draws its filled digits 1 to 9 at U+24F5..U+24FD (Unicode's double-circled
        // digits) and its filled 0 at U+24FF.
        string number = (key.Index + 1).ToString(CultureInfo.InvariantCulture);
        var digits = new System.Text.StringBuilder(number.Length);
        foreach (char d in number)
        {
            digits.Append(d == '0' ? '⓿' : (char)('⓵' + (d - '1')));
        }

        return digits.ToString();
    }

    /// <inheritdoc/>
    public override bool Draws(GlyphKey key) => key.Stick == null
        ? key.Kind is ControlKind.Button or ControlKind.Axis
        : StickMark(key) != null;

    /// <inheritdoc/>
    public override float Width(Font font, GlyphKey key, float height)
    {
        if (key.Stick != null && Face() is { } stickFace && StickMark(key) is { } mark)
        {
            return stickFace.GetStringSize(FlightStick + mark, HorizontalAlignment.Left, -1f, GlyphSize(height)).X;
        }

        if (key.Stick == null && Face() is { } face && Glyph(key) is { } glyph)
        {
            return face.GetStringSize(glyph, HorizontalAlignment.Left, -1f, GlyphSize(height)).X;
        }

        float text = font?.GetStringSize(Label(key), HorizontalAlignment.Left, -1f, LetterSize(height)).X ?? 0f;
        return Mathf.Max(height, text + (height * PlaquePad * 2f));
    }

    /// <inheritdoc/>
    public override void Draw(CanvasItem into, Font font, GlyphKey key, Rect2 box, Color color)
    {
        if (into == null)
        {
            return;
        }

        if (key.Stick != null && Face() is { } stickFace && StickMark(key) is { } mark)
        {
            DrawStick(into, stickFace, key.Stick, mark, box, color);
            return;
        }

        if (key.Stick == null && Face() is { } face && Glyph(key) is { } glyph)
        {
            // Centred on the font's line box, which is what keeps a glyph level with the words.
            int size = GlyphSize(box.Size.Y);
            var at = new Vector2(
                box.Position.X,
                box.Position.Y + ((box.Size.Y - face.GetHeight(size)) / 2f) + face.GetAscent(size));
            into.DrawString(face, at, glyph, HorizontalAlignment.Left, -1f, size, color);
            return;
        }

        Plaque(into, font, Label(key), box, color);
    }

    // The flight stick, then its mark. The left hand's stick is mirrored about its own advance, so
    // a HOSAS pair's two prompts tell the hands apart without a letter. ⚠ The transform is put back
    // to identity after, as every other transformed draw on a board leaves it.
    private static void DrawStick(CanvasItem into, FontFile face, string stick, string mark, Rect2 box, Color color)
    {
        int size = GlyphSize(box.Size.Y);
        float baseline = box.Position.Y + ((box.Size.Y - face.GetHeight(size)) / 2f) + face.GetAscent(size);
        float wide = face.GetStringSize(FlightStick, HorizontalAlignment.Left, -1f, size).X;
        if (stick == LeftHandStick)
        {
            into.DrawSetTransform(new Vector2(box.Position.X + wide, 0f), 0f, new Vector2(-1f, 1f));
            into.DrawString(face, new Vector2(0f, baseline), FlightStick, HorizontalAlignment.Left, -1f, size, color);
            into.DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
        }
        else
        {
            into.DrawString(face, new Vector2(box.Position.X, baseline), FlightStick, HorizontalAlignment.Left, -1f, size, color);
        }

        into.DrawString(face, new Vector2(box.Position.X + wide, baseline), mark, HorizontalAlignment.Left, -1f, size, color);
    }

    // Loaded once, as bytes: a res:// path read through FileAccess reaches the file in the project
    // folder and inside the exported pck alike, with no import step in either.
    private static FontFile? Face()
    {
        if (!_loaded)
        {
            _loaded = true;
            if (FileAccess.FileExists(FontPath))
            {
                _face = new FontFile { Data = FileAccess.GetFileAsBytes(FontPath) };
            }
            else
            {
                GD.PushWarning($"control glyphs: {FontPath} not found, pad controls draw as lettered plaques");
            }
        }

        return _face;
    }

    // The PromptFont character for a control, or null where the font has none. Godot's stick Y axis
    // is negative upward, so a negative sign on a Y axis is the "up" glyph.
    private static string? Glyph(GlyphKey key)
    {
        char c = key.Kind == ControlKind.Axis
            ? (JoyAxis)key.Index switch
            {
                JoyAxis.LeftX => key.Sign < 0 ? '↼' : key.Sign > 0 ? '⇀' : '⇄',
                JoyAxis.LeftY => key.Sign < 0 ? '↾' : key.Sign > 0 ? '⇂' : '⇅',
                JoyAxis.RightX => key.Sign < 0 ? '↽' : key.Sign > 0 ? '⇁' : '⇆',
                JoyAxis.RightY => key.Sign < 0 ? '↿' : key.Sign > 0 ? '⇃' : '⇵',
                JoyAxis.TriggerLeft => '↖',
                JoyAxis.TriggerRight => '↗',
                _ => '\0',
            }
            : (JoyButton)key.Index switch
            {
                JoyButton.A => '↧',
                JoyButton.B => '↦',
                JoyButton.X => '↤',
                JoyButton.Y => '↥',
                JoyButton.Back => '⇷',
                JoyButton.Guide => '⇹',
                JoyButton.Start => '⇸',
                JoyButton.LeftStick => '↺',
                JoyButton.RightStick => '↻',
                JoyButton.LeftShoulder => '↘',
                JoyButton.RightShoulder => '↙',
                JoyButton.DpadUp => '↟',
                JoyButton.DpadDown => '↡',
                JoyButton.DpadLeft => '↞',
                JoyButton.DpadRight => '↠',
                _ => '\0',
            };
        return c == '\0' ? null : c.ToString();
    }

    // The plaque's letters. A pad button no name is known for falls back to its index, so an
    // unusual controller still reads as something a player can look for.
    private static string Label(GlyphKey key)
    {
        string index = key.Index.ToString(CultureInfo.InvariantCulture);
        if (key.Stick != null)
        {
            return key.Kind == ControlKind.Hat
                ? key.Stick + " " + key.Direction.ToString().ToUpperInvariant()
                : key.Stick + " " + (key.Index + 1).ToString(CultureInfo.InvariantCulture);
        }

        if (key.Kind == ControlKind.Axis)
        {
            string dir = key.Sign < 0 ? "-" : key.Sign > 0 ? "+" : string.Empty;
            return (JoyAxis)key.Index switch
            {
                JoyAxis.TriggerLeft => "LT",
                JoyAxis.TriggerRight => "RT",
                JoyAxis.LeftX => "LS X" + dir,
                JoyAxis.LeftY => "LS Y" + dir,
                JoyAxis.RightX => "RS X" + dir,
                JoyAxis.RightY => "RS Y" + dir,
                _ => "AXIS " + index + dir,
            };
        }

        return (JoyButton)key.Index switch
        {
            JoyButton.A => "A",
            JoyButton.B => "B",
            JoyButton.X => "X",
            JoyButton.Y => "Y",
            JoyButton.LeftShoulder => "LB",
            JoyButton.RightShoulder => "RB",
            JoyButton.LeftStick => "L3",
            JoyButton.RightStick => "R3",
            JoyButton.Back => "BACK",
            JoyButton.Start => "START",
            JoyButton.Guide => "GUIDE",
            JoyButton.DpadUp => "UP",
            JoyButton.DpadDown => "DOWN",
            JoyButton.DpadLeft => "LEFT",
            JoyButton.DpadRight => "RIGHT",
            _ => "#" + index,
        };
    }

    private static int GlyphSize(float height) => Mathf.Max(1, Mathf.RoundToInt(height * GlyphScale));

    private static int LetterSize(float height) => Mathf.Max(1, Mathf.RoundToInt(height * LetterHeight));

    // A bordered plaque sized to its own label, which is how a shoulder, a trigger and the menu
    // buttons appear on a pad's own artwork.
    private static void Plaque(CanvasItem into, Font font, string label, Rect2 box, Color color)
    {
        float h = box.Size.Y;
        var frame = new Rect2(box.Position + new Vector2(0f, h * 0.12f), box.Size.X, h * 0.76f);
        into.DrawRect(frame, color, false, h * Stroke);
        if (font == null)
        {
            return;
        }

        int size = LetterSize(h);
        var span = font.GetStringSize(label, HorizontalAlignment.Left, -1f, size);
        var at = box.Position + new Vector2(
            (box.Size.X - span.X) / 2f,
            ((box.Size.Y - span.Y) / 2f) + font.GetAscent(size));
        into.DrawString(font, at, label, HorizontalAlignment.Left, -1f, size, color);
    }
}
