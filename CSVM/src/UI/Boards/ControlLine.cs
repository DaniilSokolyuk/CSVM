using System;
using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.Mech3;
using Godot;

namespace CSVM.UI.Boards;

/// <summary>
/// One line of prompt text with one control in it: the words either side of the message table's
/// <c>%1</c> slot, what fills that slot in words, and the glyph that draws there instead where the
/// set has one. The composition is still the table's own substitution, so a line reads exactly as
/// it always did and only the slot's painting changes. <see cref="Text"/> is the whole line in
/// words, which is what a log, a suite or a set that draws nothing sees.
/// </summary>
public sealed record ControlLine
{
    /// <summary>An empty line, which is what a pane being offered nothing and a seat with no
    /// binding for the action both read as.</summary>
    public static readonly ControlLine Empty = new(string.Empty, string.Empty, string.Empty, null);

    // The stand-in handed to the message table so the halves either side of the control are exactly
    // what a real fill would have written. A control character, so no authored template carries one.
    private const string Slot = "";

    private ControlLine(string prefix, string words, string suffix, GlyphKey? glyph)
    {
        Prefix = prefix;
        Words = words;
        Suffix = suffix;
        Glyph = glyph;
        Text = prefix + words + suffix;
    }

    /// <summary>The line up to the control's slot.</summary>
    public string Prefix { get; }

    /// <summary>What fills the slot in words, empty for a line that names no control.</summary>
    public string Words { get; }

    /// <summary>The line after the slot.</summary>
    public string Suffix { get; }

    /// <summary>The picture drawn in the slot instead of <see cref="Words"/>, or null where the set
    /// draws none and the words stand.</summary>
    public GlyphKey? Glyph { get; }

    /// <summary>The whole line in words, the reading that does not depend on a glyph set.</summary>
    public string Text { get; }

    /// <summary>Whether there is anything at all to draw. A line whose only content is its glyph
    /// still draws, which is what a board line with the picture alone in it is.</summary>
    public bool IsEmpty => Text.Length == 0 && Glyph == null;

    /// <summary>The glyph itself between two runs of words, a board line's own prompt. It is for a
    /// caller naming a control directly rather than filling a message template. The fallback is
    /// empty, so a set drawing no picture leaves the two runs butted together.</summary>
    public static ControlLine Around(string prefix, string suffix, GlyphKey glyph) =>
        new(prefix ?? string.Empty, string.Empty, suffix ?? string.Empty, glyph);

    /// <summary>A line with no control in it, which is what a fallback wording with nothing bound
    /// to name reads as.</summary>
    public static ControlLine Plain(string text) =>
        string.IsNullOrEmpty(text) ? Empty : new ControlLine(text, string.Empty, string.Empty, null);

    /// <summary>The template filled with <paramref name="binding"/>'s control: its own words, plus
    /// the glyph where the set has one. ⚠ The fill is <see cref="Messages.Fill"/>'s, never a
    /// concatenation: a template whose slot moved, or that carries none, has to come out of here
    /// reading the way the table wrote it.</summary>
    public static ControlLine Compose(string template, Binding binding)
    {
        string marked = Messages.Fill(template, Slot);
        int at = marked.IndexOf(Slot, StringComparison.Ordinal);
        if (at < 0)
        {
            return Plain(marked);
        }

        return new ControlLine(
            marked[..at],
            BindingLabels.Describe(binding),
            marked[(at + Slot.Length)..],
            ControlGlyphs.For(binding.Control));
    }

    /// <summary>The control of <paramref name="action"/> a seat on <paramref name="side"/> would be
    /// told to press, filled into <paramref name="template"/>, or an empty line where that seat can
    /// reach no binding for it.</summary>
    public static ControlLine For(string template, ActionMap map, InputAction action, DeviceSide side,
        bool readsKeyboard)
    {
        var bindings = map?.Bindings(action);
        return bindings != null && ActiveDevice.PromptBinding(bindings, side, readsKeyboard) is { } binding
            ? Compose(template, binding)
            : Empty;
    }

    /// <summary>How wide the line draws at <paramref name="fontSize"/>, the glyph included.</summary>
    public float Width(Font font, int fontSize)
    {
        if (font == null)
        {
            return 0f;
        }

        if (Glyph is not { } key)
        {
            return Span(font, Text, fontSize);
        }

        return Span(font, Prefix, fontSize) + Span(font, Suffix, fontSize)
            + ControlGlyphs.Set.Width(font, key, GlyphHeight(font, fontSize));
    }

    /// <summary>Draws the line with its top-left corner at <paramref name="topLeft"/>, the glyph
    /// sitting in the slot the words would have filled.</summary>
    public void Draw(CanvasItem into, Font font, int fontSize, Vector2 topLeft, Color color)
    {
        if (into == null || font == null || IsEmpty)
        {
            return;
        }

        float baseline = topLeft.Y + font.GetAscent(fontSize);

        // A line with no glyph is ONE string drawn once, not three parts butted together: splitting
        // it moves the words by whatever the face kerns across a seam, which is a pixel change in a
        // prompt that was never meant to move.
        if (Glyph is not { } key)
        {
            Write(into, font, Text, fontSize, new Vector2(topLeft.X, baseline), color);
            return;
        }

        float x = topLeft.X + Write(into, font, Prefix, fontSize, new Vector2(topLeft.X, baseline), color);
        float h = GlyphHeight(font, fontSize);
        float w = ControlGlyphs.Set.Width(font, key, h);
        ControlGlyphs.Set.Draw(into, font, key,
            new Rect2(x, topLeft.Y + ((font.GetHeight(fontSize) - h) / 2f), w, h), color);
        Write(into, font, Suffix, fontSize, new Vector2(x + w, baseline), color);
    }

    // A glyph stands as tall as the face's ascent, so it sits on the line rather than over it.
    private static float GlyphHeight(Font font, int fontSize) => font.GetAscent(fontSize);

    private static float Span(Font font, string text, int fontSize) =>
        text.Length == 0 ? 0f : font.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize).X;

    private static float Write(CanvasItem into, Font font, string text, int fontSize, Vector2 at, Color color)
    {
        if (text.Length == 0)
        {
            return 0f;
        }

        into.DrawString(font, at, text, HorizontalAlignment.Left, -1f, fontSize, color);
        return font.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize).X;
    }
}

/// <summary>
/// A row of control hints drawn side by side, which is what a board's footer is: each item names one
/// control of the seat driving the board, and a pad seat reads glyphs where a keyboard seat reads
/// key names. ⚠ Items are separate lines laid out in a row, never one string built by concatenating
/// controls into it: a seat names one device at a time, and each item is composed through
/// <see cref="ControlLine.For"/> so that gate holds per item.
/// </summary>
public sealed partial class ControlHintBar : Control
{
    private IReadOnlyList<ControlLine> _items = Array.Empty<ControlLine>();
    private int _fontSize = 15;
    private float _gap = 24f;
    private Color _color = Colors.White;

    /// <summary>A bar over <paramref name="items"/> at <paramref name="fontSize"/>, the items
    /// <paramref name="gap"/> pixels apart.</summary>
    public static ControlHintBar Build(IReadOnlyList<ControlLine> items, int fontSize, Color color, float gap)
    {
        var bar = new ControlHintBar
        {
            MouseFilter = MouseFilterEnum.Ignore,
            FocusMode = FocusModeEnum.None,
        };
        bar.Show(items, fontSize, color, gap);
        return bar;
    }

    /// <summary>Replaces what the bar names, which is what a handover to the other device asks
    /// for.</summary>
    public void Show(IReadOnlyList<ControlLine> items, int fontSize, Color color, float gap)
    {
        _items = items ?? Array.Empty<ControlLine>();
        _fontSize = Mathf.Max(1, fontSize);
        _color = color;
        _gap = gap;
        UpdateMinimumSize();
        QueueRedraw();
    }

    /// <inheritdoc/>
    public override Vector2 _GetMinimumSize()
    {
        var font = GetThemeDefaultFont();
        return font == null ? Vector2.Zero : new Vector2(Row(font), font.GetHeight(_fontSize));
    }

    /// <inheritdoc/>
    public override void _Draw()
    {
        var font = GetThemeDefaultFont();
        if (font == null || _items.Count == 0)
        {
            return;
        }

        float x = (Size.X - Row(font)) / 2f;
        float y = (Size.Y - font.GetHeight(_fontSize)) / 2f;
        foreach (var item in _items)
        {
            if (item.IsEmpty)
            {
                continue;
            }

            item.Draw(this, font, _fontSize, new Vector2(x, y), _color);
            x += item.Width(font, _fontSize) + _gap;
        }
    }

    // The whole row's width, the gaps between the items that actually draw included.
    private float Row(Font font)
    {
        float width = 0f;
        int drawn = 0;
        foreach (var item in _items)
        {
            if (item.IsEmpty)
            {
                continue;
            }

            width += item.Width(font, _fontSize);
            drawn++;
        }

        return drawn > 1 ? width + (_gap * (drawn - 1)) : width;
    }
}
