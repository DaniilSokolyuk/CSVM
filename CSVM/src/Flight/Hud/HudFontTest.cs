using Godot;

namespace CSVM.Flight.Hud;

/// <summary>
/// The <c>--hud-font-test</c> verification overlay for <see cref="HudFont"/>: draws a known string
/// in the normal and highlight variants near the top-left of each player's pane, sized through
/// <see cref="HudMetrics"/> so a single-player view and a splitscreen pane can be screenshot and
/// compared. A thin rule under each line marks <see cref="HudFont.Measure"/>'s reported width,
/// confirming the metric agrees with the glyphs actually drawn.
///
/// <para>Not part of the flight HUD, it is only added when the flag is set, and exists purely to
/// prove the renderer. Real HUD text draws with the same <see cref="HudFont"/> API.</para>
/// </summary>
public sealed partial class HudFontTest : Control
{
    private const float RefTextHeight = 24f; // atlas-cell height in px at the 1440p reference
    private const float RefMarginX = 48f;    // left inset at the reference
    private const float RefMarginY = 300f;   // top inset, clear of the flight text block

    private readonly HudFont _font;
    private readonly string _text;

    public HudFontTest(HudFont font, string text)
    {
        _font = font;
        _text = text;
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.Nearest; // crisp pixel font
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        float s = HudMetrics.Scale(this);
        float scale = _font.ScaleForHeight(RefTextHeight) * s;
        float lineStep = (_font.PixelHeight + 3) * scale;
        float x = RefMarginX * s;
        float y = RefMarginY * s;

        DrawSample(new Vector2(x, y), scale, bright: false);
        DrawSample(new Vector2(x, y + lineStep), scale, bright: true);
    }

    private void DrawSample(Vector2 at, float scale, bool bright)
    {
        _font.Draw(this, _text, at, scale, bright);
        float w = _font.Measure(_text, scale);
        float ruleY = at.Y + (_font.PixelHeight + 1) * scale;
        DrawLine(new Vector2(at.X, ruleY), new Vector2(at.X + w, ruleY),
            new Color(0f, 1f, 0f, 0.4f), Mathf.Max(1f, scale));
    }
}
