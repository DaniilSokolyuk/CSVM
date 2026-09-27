using System;
using System.IO;
using CSVM.UI.Boards;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The build's version as <c>CSVM v&lt;version&gt;</c> in the menu's bottom-right corner. A
/// screenshot a stranger sends then carries its build, and the number is not taken for the
/// original game's own. Two mouse-only icons left of it open the logs folder and the user folder,
/// so a player filing a bug report can find the log. Built once by
/// <see cref="CSVM.Session.Launch.Launcher"/>, which shows it over every menu presentation and the
/// extraction screen and hides it in flight. The number's home: <see cref="BuildVersion"/>.
/// </summary>
public sealed partial class BuildStamp : Node
{
    /// <summary>The logs icon, PromptFont's page with a pencil (U+1F4DD). Outside the BMP, so a
    /// surrogate pair.</summary>
    public const string LogsGlyph = "\U0001F4DD";

    /// <summary>The user folder icon, PromptFont's floppy disk (U+1F4BE).</summary>
    public const string UserGlyph = "\U0001F4BE";

    /// <summary>The logs icon's tooltip.</summary>
    public const string LogsTooltip = "Open logs folder";

    /// <summary>The user folder icon's tooltip.</summary>
    public const string UserTooltip = "Open user folder";

    // Authored against the launchscreen's own 720p metrics (LaunchMenu's FooterFont), so the stamp
    // reads as one line of the same size as the controls line it sits below.
    private const float ReferenceFontSize = 15f;
    private const float ReferenceHeight = 720f;

    // The gap between the icons and the text at the reference height, scaled with the font.
    private const float ReferenceGapPx = 6f;

    // A margin against the screen edge rather than a scaled metric, the same choice PerfHud makes
    // for its own corner: a margin that grew with the window would drift off the corner it marks.
    private const float CornerInsetPx = 8f;

    // Dimmer than anything a player navigates by. It is there to be read back off a capture, not
    // to compete with the screen under it. An icon lights up under the pointer.
    private static readonly Color DimColour = new(0.62f, 0.66f, 0.72f, 0.75f);
    private static readonly Color LitColour = new(0.95f, 0.96f, 0.98f, 1f);
    private static readonly Color ShadowColour = new(0f, 0f, 0f, 0.7f);

    private readonly string _root;
    private readonly bool _exported;
    private CanvasLayer _layer = null!;
    private HBoxContainer _row = null!;
    private Label _label = null!;
    private int _fontSize;
    private bool _holding;

    /// <summary>A stamp whose logs icon falls back to <see cref="Log.DirectoryFor"/> over
    /// <paramref name="root"/> and <paramref name="exported"/> while no log file is open.</summary>
    public BuildStamp(string root, bool exported)
    {
        Name = "build_stamp";
        _root = root;
        _exported = exported;
    }

    /// <summary>Gets the folder the user icon opens, Godot's user data folder. It holds the
    /// settings, the bindings, the campaign profiles, the custom planes and Godot's own log.</summary>
    public static string UserFolder => OS.GetUserDataDir();

    /// <summary>Gets or sets what an icon's click runs, given the folder and its log name.
    /// <see cref="FolderOpener.Open"/> by default; a suite swaps it so no window opens.</summary>
    public Func<string, string, string?> Opener { get; set; } = FolderOpener.Open;

    /// <summary>Gets the logs folder icon.</summary>
    public Button LogsButton { get; private set; } = null!;

    /// <summary>Gets the user folder icon.</summary>
    public Button UserButton { get; private set; } = null!;

    /// <summary>Gets a value indicating whether the stamp is on screen.</summary>
    public bool Shown => _layer.Visible;

    /// <summary>Gets the folder the logs icon opens: the open log file's own directory.</summary>
    public string LogsFolder => LogsFolderFor(Log.SinkPath, _root, _exported);

    /// <summary>The directory of <paramref name="sinkPath"/>, or where <see cref="Log.DirectoryFor"/>
    /// puts the log when no file is open. Pure, so both branches are assertable.</summary>
    public static string LogsFolderFor(string? sinkPath, string root, bool exported) =>
        sinkPath != null && Path.GetDirectoryName(sinkPath) is { Length: > 0 } dir ? dir : Log.DirectoryFor(root, exported);

    /// <summary>Shows the stamp while the menu or the extraction screen is up and keeps it sized to
    /// the window. Called once a frame by the launcher, which owns both.</summary>
    public void Tick(bool shown)
    {
        _layer.Visible = shown;
        if (!shown)
        {
            _holding = false;
            return;
        }
        int size = Mathf.RoundToInt(ReferenceFontSize * WindowScale());
        if (size != _fontSize)
        {
            _fontSize = size;
            _label.AddThemeFontSizeOverride("font_size", size);
            LogsButton.AddThemeFontSizeOverride("font_size", size);
            UserButton.AddThemeFontSizeOverride("font_size", size);
            _row.AddThemeConstantOverride("separation", Mathf.RoundToInt(ReferenceGapPx * size / ReferenceFontSize));
        }
    }

    /// <summary>Whether a click at <paramref name="at"/> (window pixels) belongs to an icon: the
    /// pointer is over one, or a press began on one. The launcher keeps such a click from a
    /// presentation that polls the mouse rather than taking Godot's GUI events.</summary>
    public bool HoldsPointer(Vector2 at) =>
        _layer.Visible && (_holding || LogsButton.GetGlobalRect().HasPoint(at) || UserButton.GetGlobalRect().HasPoint(at));

    public override void _Ready()
    {
        // The layer sits above the boards, so the icons take their own clicks. The root and the row
        // ignore the mouse, so every other pixel still reaches the menu underneath.
        _layer = new CanvasLayer { Layer = HudLayers.BuildStamp, Visible = false };
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        // A zero-size box on the bottom-right inset, growing up and to the left by the row's minimum
        // size. The corner holds however wide the text and the font size make it.
        _row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _row.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _row.GrowHorizontal = Control.GrowDirection.Begin;
        _row.GrowVertical = Control.GrowDirection.Begin;
        _row.OffsetRight = -CornerInsetPx;
        _row.OffsetLeft = _row.OffsetRight;
        _row.OffsetBottom = -CornerInsetPx;
        _row.OffsetTop = _row.OffsetBottom;

        LogsButton = Icon(LogsGlyph, "logs", LogsTooltip, () => Opener(LogsFolder, "logs folder"));
        UserButton = Icon(UserGlyph, "user", UserTooltip, () => Opener(UserFolder, "user folder"));

        // ⚠ Keep the CSVM prefix on every presentation. A bare version number in the corner of the
        // Original menu is read as the original game's own, and a bug report has to name the build.
        _label = new Label
        {
            Text = $"CSVM v{BuildVersion.Current}",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The shadow carries it over the Original presentation's artwork, which is not the flat
        // backdrop Built-in draws.
        _label.AddThemeColorOverride("font_color", DimColour);
        _label.AddThemeColorOverride("font_shadow_color", ShadowColour);
        _label.AddThemeConstantOverride("shadow_offset_x", 1);
        _label.AddThemeConstantOverride("shadow_offset_y", 1);

        _row.AddChild(LogsButton);
        _row.AddChild(UserButton);
        _row.AddChild(_label);
        root.AddChild(_row);
        _layer.AddChild(root);
        AddChild(_layer);
    }

    // One icon. ⚠ Keep FocusMode None. A focusable icon takes the focus on a click. Enter or A on
    // the extraction screen would then open the folder again instead of extracting.
    private Button Icon(string glyph, string fallback, string tooltip, Action open)
    {
        var face = PromptFontGlyphs.Font;
        // Both characters default to emoji presentation, and Godot then draws them from a system
        // colour emoji font. The text presentation selector (U+FE0E) keeps them on PromptFont.
        var button = new Button
        {
            Text = face != null ? glyph + "︎" : fallback,
            TooltipText = tooltip,
            FocusMode = Control.FocusModeEnum.None,
            Flat = true,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        if (face != null)
        {
            button.AddThemeFontOverride("font", face);
        }

        var none = new StyleBoxEmpty();
        foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" })
        {
            button.AddThemeStyleboxOverride(state, none);
        }

        button.AddThemeColorOverride("font_color", DimColour);
        foreach (string state in new[] { "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
        {
            button.AddThemeColorOverride(state, LitColour);
        }

        button.AddThemeColorOverride("font_outline_color", ShadowColour);
        button.AddThemeConstantOverride("outline_size", 2);
        button.Pressed += open;
        button.ButtonDown += () => _holding = true;
        button.ButtonUp += () => _holding = false;
        return button;
    }

    // The plain window-height ratio against the metrics the menu itself is authored at, floored at
    // 1: a window shorter than the reference draws the menu at 1:1 and the stamp with it.
    private float WindowScale()
    {
        float windowH = GetTree()?.Root?.Size.Y ?? ReferenceHeight;
        return Mathf.Max(1f, windowH / ReferenceHeight);
    }
}
