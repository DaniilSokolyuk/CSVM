using CSVM.UI.Boards;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The build's version as <c>CSVM v&lt;version&gt;</c> in the bottom-right corner of the menu, so a
/// screenshot a stranger sends already carries the build it was taken on, and the number is not
/// taken for the original game's own. Built once by <see cref="CSVM.Session.Launch.Launcher"/>
/// and shown whenever the menu is up, which is what puts it on EVERY presentation: the stamp is a
/// fact about the binary, not part of any one presentation's screen graph, and Original draws the
/// decoded artwork with no place to put one. Hidden in flight, so no golden screenshot sees it.
/// The number's home and its other two surfaces: <see cref="BuildVersion"/>.
/// </summary>
public sealed partial class BuildStamp : Node
{
    // Authored against the launchscreen's own 720p metrics (LaunchMenu's FooterFont), so the stamp
    // reads as one line of the same size as the controls line it sits below.
    private const float ReferenceFontSize = 15f;
    private const float ReferenceHeight = 720f;

    // A margin against the screen edge rather than a scaled metric, the same choice PerfHud makes
    // for its own corner: a margin that grew with the window would drift off the corner it marks.
    private const float CornerInsetPx = 8f;

    private CanvasLayer _layer = null!;
    private Label _label = null!;
    private int _fontSize;

    public BuildStamp()
    {
        Name = "build_stamp";
    }

    /// <summary>Shows the stamp while the menu is on screen and keeps it sized to the window.
    /// Called once a frame by the launcher, which owns "the menu is up".</summary>
    public void Tick(bool menuShown)
    {
        _layer.Visible = menuShown;
        if (!menuShown)
        {
            return;
        }
        int size = Mathf.RoundToInt(ReferenceFontSize * WindowScale());
        if (size != _fontSize)
        {
            _fontSize = size;
            _label.AddThemeFontSizeOverride("font_size", size);
        }
    }

    public override void _Ready()
    {
        _layer = new CanvasLayer { Layer = HudLayers.BuildStamp, Visible = false };
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        // ⚠ Keep the CSVM prefix on every presentation. A bare version number in the corner of the
        // Original menu is read as the original game's own, and a bug report has to name the build.
        _label = new Label { Text = $"CSVM v{BuildVersion.Current}", MouseFilter = Control.MouseFilterEnum.Ignore };
        // Dimmer than anything a player navigates by: it is there to be read back off a capture,
        // not to compete with the screen it sits on. The shadow carries it over the Original
        // presentation's artwork, which is not the flat backdrop Built-in draws.
        _label.AddThemeColorOverride("font_color", new Color(0.62f, 0.66f, 0.72f, 0.75f));
        _label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.7f));
        _label.AddThemeConstantOverride("shadow_offset_x", 1);
        _label.AddThemeConstantOverride("shadow_offset_y", 1);
        // A zero-size box on the bottom-right inset spending the label's minimum size up and to
        // the left, so the corner holds however wide the text and the font size make it.
        _label.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _label.GrowHorizontal = Control.GrowDirection.Begin;
        _label.GrowVertical = Control.GrowDirection.Begin;
        _label.OffsetRight = -CornerInsetPx;
        _label.OffsetLeft = _label.OffsetRight;
        _label.OffsetBottom = -CornerInsetPx;
        _label.OffsetTop = _label.OffsetBottom;
        root.AddChild(_label);
        _layer.AddChild(root);
        AddChild(_layer);
    }

    // The plain window-height ratio against the metrics the menu itself is authored at, floored at
    // 1: a window shorter than the reference draws the menu at 1:1 and the stamp with it.
    private float WindowScale()
    {
        float windowH = GetTree()?.Root?.Size.Y ?? ReferenceHeight;
        return Mathf.Max(1f, windowH / ReferenceHeight);
    }
}
