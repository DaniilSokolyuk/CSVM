using Godot;

namespace CSVM.UI;

/// <summary>
/// The <c>--debug-net</c> line in the top-left corner: a network match's desync counters as
/// <see cref="Net.NetInstruments.Describe"/> writes them, the same text the launcher logs once a
/// second. Built once by <see cref="CSVM.Session.Launcher"/> and only under the flag, so an
/// ordinary run and the golden sweep never build it. Shown only while a session holds a wire.
/// </summary>
public sealed partial class NetReadout : Node
{
    // Small and plain, the size of the other debug overlays. It is read up close while testing a
    // link, not at a glance in a fight.
    private const int FontSize = 14;

    // A margin against the screen edge, unscaled for the reason PerfHud gives for its own corner.
    private const float CornerInsetPx = 8f;

    private CanvasLayer _layer = null!;
    private Label _label = null!;

    public NetReadout()
    {
        Name = "net_readout";
    }

    /// <summary>Shows <paramref name="line"/>, or hides the readout when it is null, which is the
    /// launcher's word for "no session on a wire".</summary>
    public void Show(string? line)
    {
        _layer.Visible = line != null;
        if (line != null)
        {
            _label.Text = line.Replace(" | ", "\n", System.StringComparison.Ordinal);
        }
    }

    public override void _Ready()
    {
        _layer = new CanvasLayer { Layer = HudLayers.Debug, Visible = false };
        _label = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(CornerInsetPx, CornerInsetPx),
        };
        _label.AddThemeFontSizeOverride("font_size", FontSize);
        _label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.8f));
        _label.AddThemeConstantOverride("shadow_offset_x", 1);
        _label.AddThemeConstantOverride("shadow_offset_y", 1);
        _layer.AddChild(_label);
        AddChild(_layer);
    }
}
