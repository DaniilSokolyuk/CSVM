using System.IO;
using System.Linq;
using CSVM.UI.Boards;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// What the window shows instead of the menu when the data root holds no extraction: that the
/// game data is missing, and the step that produces it. A recipient who starts the exe before
/// extracting is answered on screen, not in a log file they have no reason to open.
/// ⚠ Do not grow this into a second provenance check; <see cref="CSVM.Session.Launch.ExtractionStamp"/>
/// owns whether an extraction is stale and stays a warning. This screen answers one question,
/// whether there is an extraction here at all, and it is the launcher that decides when to ask.
/// </summary>
public static class NoGameDataScreen
{
    // Authored against the launchscreen's own 720p metrics, like the corner stamp's 15: a title
    // read at a glance, and a body a couple of points above it.
    private const int TitleFontSize = 30;
    private const int BodyFontSize = 17;

    // Held off the window edge on all four sides, so a long install path wraps inside the screen
    // rather than against the frame.
    private const int EdgeMarginPx = 64;

    /// <summary>Whether <paramref name="dataRoot"/> holds nothing to play from: no
    /// <c>extracted</c> directory, or an empty one. Engine-free, so the launcher's branch and the
    /// unit that covers it read the same rule.</summary>
    public static bool Missing(string dataRoot)
    {
        string dir = Path.Combine(dataRoot, "extracted");
        return !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any();
    }

    /// <summary>The one sentence naming the extraction this build's own reader runs, for the
    /// screen and for the log line beside it. <paramref name="exported"/> tells a release payload,
    /// which ships the wrapper, from a repo checkout, which runs the scripts.</summary>
    public static string Instruction(bool exported) => exported
        ? "Double-click Extract.cmd in this folder, point it at your Crimson Skies install, then start CSVM.exe again."
        : "Run ExtractAssets.ps1 and then ExtractRof.ps1 against your Crimson Skies install; docs/tooling.md documents both.";

    /// <summary>The screen as one layer the caller parents and never ticks: a backdrop, the
    /// title, the instruction, the path that was looked in, and the way out.</summary>
    public static CanvasLayer Build(string dataRoot, bool exported)
    {
        // The launchscreen's own tier: this screen is what stands in its place, and nothing else
        // is drawn while it is up.
        var layer = new CanvasLayer { Layer = HudLayers.Board };
        var backdrop = new ColorRect { Color = new Color(0.04f, 0.05f, 0.07f) };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(backdrop);

        // The margin is what gives the wrapped lines a measure: a box on the bare full rect wraps
        // against the window edge, which reads as text falling off the screen.
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, EdgeMarginPx);
        }

        var box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.AddThemeConstantOverride("separation", 18);
        box.AddChild(Line("No game data found", TitleFontSize, new Color(0.93f, 0.83f, 0.55f)));
        box.AddChild(Line(
            "CSVM plays Crimson Skies from your own copy of the game, and this folder holds none of it yet.",
            BodyFontSize, new Color(0.84f, 0.87f, 0.92f)));
        box.AddChild(Line(Instruction(exported), BodyFontSize, new Color(0.84f, 0.87f, 0.92f)));
        box.AddChild(Line($"Looked in:  {Path.Combine(dataRoot, "extracted")}",
            BodyFontSize, new Color(0.58f, 0.62f, 0.69f)));
        box.AddChild(Line("Press Esc to close.", BodyFontSize, new Color(0.58f, 0.62f, 0.69f)));
        margin.AddChild(box);
        layer.AddChild(margin);
        return layer;
    }

    private static Label Line(string text, int fontSize, Color colour)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", colour);
        return label;
    }
}
