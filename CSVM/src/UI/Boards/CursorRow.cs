using Godot;

namespace CSVM.UI.Boards;

/// <summary>
/// One centred list row with a ▶ cursor, the shared row of every menu that has one: the
/// launchscreen's screens, its per-player aircraft panes, and every board menu.
/// ⚠ The marker is a cell of its own, NOT a prefix on the row's text. A centred label whose text
/// gains "▶  " when selected and "     " when not is centred on the PADDING as well, and since the
/// two are not the same width every unselected row drifts sideways, which is what made these
/// menus read as uncentred. The same cell width is reserved on the right, so the label sits on the
/// panel's centre line in both states.
/// </summary>
public sealed partial class CursorRow : CenterContainer
{
    private const string Marker = "▶";

    // The marker cell's width as a multiple of the row's font size, room for the marker and its
    // gap at any scale, since callers pass a font size already scaled by the board's own factor.
    private const float MarkerCellEms = 1.6f;

    private Label _marker = null!;
    private Label _label = null!;

    // The second column, on a two-column row only. Null on a plain one, which has no value cell.
    private Label? _value;

    /// <summary>Builds a row carrying <paramref name="text"/> at an already-scaled
    /// <paramref name="fontSize"/>. <paramref name="color"/> applies to both the label and the
    /// marker, so a caller that colours a selected row differently passes the colour it wants.</summary>
    public static CursorRow Build(string text, int fontSize, Color color, bool selected)
    {
        float cell = fontSize * MarkerCellEms;
        var row = new CursorRow
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };

        // Centred as a GROUP, with the label at its natural width: that keeps the marker beside the
        // text rather than out at the panel's edge, which is where an expanding label pushes it.
        var group = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(group);

        row._marker = Text(Marker, fontSize, HorizontalAlignment.Right);
        row._marker.CustomMinimumSize = new Vector2(cell, 0f);
        group.AddChild(row._marker);

        row._label = Text(text, fontSize, HorizontalAlignment.Center);
        group.AddChild(row._label);

        // The marker cell's mirror. Without it the group would centre on the axis of
        // (marker + label), leaving the label itself half a cell right of the panel's centre line.
        group.AddChild(new Control
        {
            CustomMinimumSize = new Vector2(cell, 0f),
            MouseFilter = MouseFilterEnum.Ignore,
        });

        row.Set(text, color, selected);
        return row;
    }

    /// <summary>A two-column row: a left-aligned <paramref name="label"/> cell of
    /// <paramref name="labelWidth"/> and a left-aligned <paramref name="value"/> cell of
    /// <paramref name="valueWidth"/>, the pair centred as a group like <see cref="Build"/>'s.
    /// ⚠ Both widths are fixed on purpose. Centring one string that carries label and value
    /// together recentres the row whenever the value changes length, so stepping a stepper walks
    /// the label out from under the cursor; fixed cells hold both still.</summary>
    public static CursorRow BuildColumns(string label, string value, float labelWidth,
        float valueWidth, int fontSize, Color color, bool selected)
    {
        float cell = fontSize * MarkerCellEms;
        var row = new CursorRow
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };

        var group = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(group);

        row._marker = Text(Marker, fontSize, HorizontalAlignment.Right);
        row._marker.CustomMinimumSize = new Vector2(cell, 0f);
        group.AddChild(row._marker);

        row._label = Text(label, fontSize, HorizontalAlignment.Left);
        row._label.CustomMinimumSize = new Vector2(labelWidth, 0f);
        group.AddChild(row._label);

        row._value = Text(value, fontSize, HorizontalAlignment.Left);
        row._value.CustomMinimumSize = new Vector2(valueWidth, 0f);
        group.AddChild(row._value);

        group.AddChild(new Control
        {
            CustomMinimumSize = new Vector2(cell, 0f),
            MouseFilter = MouseFilterEnum.Ignore,
        });

        row.Set(label, color, selected);
        row._value.AddThemeColorOverride("font_color", color);
        return row;
    }

    /// <summary>Repoints the row at new text, colour and selection. Cheap enough for every cursor
    /// move: it touches the two labels and nothing else.</summary>
    public void Set(string text, Color color, bool selected)
    {
        _label.Text = text;
        // Emptied rather than hidden: a Godot container skips invisible children, which would
        // collapse the cell and undo the whole point of reserving it.
        _marker.Text = selected ? Marker : "";
        _label.AddThemeColorOverride("font_color", color);
        _marker.AddThemeColorOverride("font_color", color);
    }

    private static Label Text(string text, int fontSize, HorizontalAlignment align)
    {
        var l = new Label { Text = text, HorizontalAlignment = align };
        l.AddThemeFontSizeOverride("font_size", fontSize);
        l.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.7f));
        l.AddThemeConstantOverride("shadow_offset_x", 1);
        l.AddThemeConstantOverride("shadow_offset_y", 1);
        return l;
    }
}
