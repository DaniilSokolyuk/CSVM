using System;
using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Labs;

/// <summary>
/// The static viewer's livery lab (<c>--viewer</c>, key L): an interactive editor for one
/// aircraft's <see cref="PaintScheme"/>, pattern, the three paint colours as RGB sliders, and
/// the three decal slots, repainting the parked plane live. Exists because the paint region
/// table is hand-authored rather than extracted (<c>docs/formats/paint.md</c>): checking a
/// colour against the original means seeing it on the model. "copy CLI args" writes the exact
/// <c>--paint…</c> arguments that reproduce the current livery. Full behaviour: this module's
/// entry in docs/architecture.md.
/// ⚠ Every edit funnels through <see cref="Apply"/> as the single write path; do not repaint any
/// other way.
/// </summary>
public sealed partial class LiveryLab : Node
{
    private const int DecalCount = 50; // the gapless 00-49 set every chapter ships

    private readonly PlaneBuilder _builder;
    private readonly IReadOnlyList<PaintScheme> _catalog;
    // The patterns THIS aircraft has masks for, the same list the original's paint UI offers
    // per plane (the Fury has four, the Balmoral two). Stepping is over this, not the
    // vehicle.json catalog, because a pattern the plane lacks would paint nothing.
    private readonly IReadOnlyList<string> _patterns;
    private readonly TextureArchive _textures;
    private readonly RandomNumberGenerator _rng = new();
    private readonly ColorRect[] _swatches = new ColorRect[3];
    private readonly HSlider[,] _rgb = new HSlider[3, 3];
    private readonly Label[] _decalLabels = new Label[3];

    // The live scheme. Null = unpainted (the shipped skins), which is what a --viewer
    // launch starts on unless --paint named something, so existing screenshots are unchanged.
    private PaintScheme? _scheme;
    private int _patternIndex = -1;

    private CanvasLayer _ui = null!;
    private Label _patternLabel = null!;
    private Label _cliLabel = null!;
    private CheckButton _paintedToggle = null!;
    private bool _suppressCallbacks; // set while rewriting widgets from a scheme change

    public LiveryLab(PlaneBuilder builder, IReadOnlyList<PaintScheme> catalog,
        TextureArchive textures, PaintScheme? initial, IReadOnlyList<string>? patterns = null)
    {
        _builder = builder;
        _catalog = catalog;
        _patterns = patterns ?? Array.Empty<string>();
        _textures = textures;
        _scheme = initial;
        _rng.Randomize();
        if (initial != null)
            _patternIndex = IndexOfPattern(initial);
        Name = "livery_lab";
    }

    /// <summary>--debug-livery[=N]: open the panel at launch (it is hidden by default so an
    /// unadorned --viewer screenshot stays byte-identical) and, with N, step the pattern N
    /// times first, so one scripted screenshot exercises the stepper, the repaint and the
    /// widget sync, not just the layout. Same role as --debug-scoreboard / --debug-join.</summary>
    public bool DebugShow { get; init; }
    public int DebugPatternSteps { get; init; }

    public override void _Ready()
    {
        BuildUi();
        // The lab is the single owner of the livery in --viewer: GameSession builds the model
        // bare, and the initial scheme applies here through the same Repaint every slider uses.
        Apply();

        if (DebugPatternSteps != 0)
        {
            SelectPattern(DebugPatternSteps);
            Log.Info("ui", $"[livery] --debug-livery stepped {DebugPatternSteps} → {CliArgs()}");
        }
        if (DebugShow)
            _ui.Visible = true;
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.L })
            _ui.Visible = !_ui.Visible;
    }

    private static bool SameAs(PaintScheme a, PaintScheme b) =>
        a.Color1 == b.Color1 && a.Color2 == b.Color2 && a.Color3 == b.Color3
        && a.NoseDecal == b.NoseDecal && a.TailDecal == b.TailDecal && a.WingDecal == b.WingDecal;

    private static string Rgb(Color c) =>
        $"{Mathf.RoundToInt(c.R * 255f)},{Mathf.RoundToInt(c.G * 255f)},{Mathf.RoundToInt(c.B * 255f)}";

    private static Button StepButton(string text, Action pressed)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(28, 0) };
        b.Pressed += pressed;
        return b;
    }

    private static HSeparator Separator() => new();

    private static Label Small(string text)
    {
        var label = new Label { Text = text, Modulate = new Color(1, 1, 1, 0.65f) };
        label.AddThemeFontSizeOverride("font_size", 11);
        return label;
    }

    // Position of a scheme's pattern in this aircraft's pattern list. Matches on
    // either spelling, vehicle.json says `player_fortune`, the archive folder is `FORTUNE`.
    private int IndexOfPattern(PaintScheme scheme)
    {
        for (int i = 0; i < _patterns.Count; i++)
            if (string.Equals(_patterns[i], scheme.Pattern, StringComparison.OrdinalIgnoreCase)
                || string.Equals(_patterns[i], scheme.FolderName, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    // The catalog's canonical colours for a pattern folder name, or null when
    // vehicle.json names no such pattern (BROADWAY and ITSTAXI ship masks but no def).
    private PaintScheme? CatalogFor(string folder)
    {
        foreach (var s in _catalog)
            if (string.Equals(s.FolderName, folder, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    // ---- edits -------------------------------------------------------------------------

    // Steps to the next/previous pattern this aircraft carries and puts on that
    // squadron's whole livery, colours and decals, not just the mask layout. Switching
    // squadron means switching to their colours; stepping the Bloodhawk's three gives you
    // Fortune Hunters red, Blake blue-gray and Hughes yellow, each with its own logos.
    // A pattern vehicle.json names no colours for (BROADWAY, ITSTAXI) keeps the current
    // ones, since there is nothing canonical to load.
    private void SelectPattern(int delta)
    {
        if (_patterns.Count == 0)
            return;
        _patternIndex = Mathf.PosMod(_patternIndex + delta, _patterns.Count);
        LoadSquadronLivery();
    }

    // (Re)loads the current pattern's shipped squadron colours and decals, the
    // livery as the game's own defs define it. Also the panel's "squadron colours" button,
    // which is how you get back to canonical after dragging the RGB sliders.
    private void LoadSquadronLivery()
    {
        if (_patternIndex < 0 || _patternIndex >= _patterns.Count)
            return;
        var folder = _patterns[_patternIndex];
        var src = CatalogFor(folder);
        _scheme = new PaintScheme
        {
            Pattern = folder,
            // No catalog entry (BROADWAY/ITSTAXI ship masks but no vehicle def names them):
            // carry the current colours rather than inventing some.
            Color1 = src?.Color1 ?? _scheme?.Color1 ?? Colors.White,
            Color2 = src?.Color2 ?? _scheme?.Color2 ?? Colors.White,
            Color3 = src?.Color3 ?? _scheme?.Color3 ?? Colors.White,
            NoseDecal = src?.NoseDecal ?? _scheme?.NoseDecal ?? 21,
            TailDecal = src?.TailDecal ?? _scheme?.TailDecal ?? 7,
            WingDecal = src?.WingDecal ?? _scheme?.WingDecal ?? 7,
        };
        Apply();
    }

    private void SetColor(int slot, Color c)
    {
        if (_scheme == null)
            return;
        switch (slot)
        {
            case 0: _scheme.Color1 = c; break;
            case 1: _scheme.Color2 = c; break;
            default: _scheme.Color3 = c; break;
        }
        // A hand-edited colour is no longer the named pattern's, so the label says so.
        Apply();
    }

    private void StepDecal(int slot, int delta)
    {
        if (_scheme == null)
            return;
        int cur = slot switch { 0 => _scheme.NoseDecal, 1 => _scheme.TailDecal, _ => _scheme.WingDecal };
        int next = Mathf.PosMod(cur + delta, DecalCount);
        switch (slot)
        {
            case 0: _scheme.NoseDecal = next; break;
            case 1: _scheme.TailDecal = next; break;
            default: _scheme.WingDecal = next; break;
        }
        Apply();
    }

    private void Randomize()
    {
        _scheme = PaintScheme.Random(_rng, _catalog, _patterns);
        _patternIndex = IndexOfPattern(_scheme);
        Apply();
    }

    private void SetPainted(bool on)
    {
        if (on && _scheme == null)
        {
            _patternIndex = 0;
            SelectPattern(0);
            return;
        }
        if (!on)
            _scheme = null;
        Apply();
    }

    // Repaints the model and refreshes the panel. The single write path.
    private void Apply()
    {
        _builder.Repaint(_scheme);
        SyncWidgets();
    }

    // ---- panel -------------------------------------------------------------------------

    private void SyncWidgets()
    {
        _suppressCallbacks = true;
        bool painted = _scheme != null;
        _paintedToggle.ButtonPressed = painted;
        _patternLabel.Text = !painted
            ? "unpainted (shipped skin)"
            : $"{_scheme!.Label}{(_patternIndex >= 0 ? $"   [{_patternIndex + 1}/{_patterns.Count}]" : "   (not for this plane)")}";

        for (int slot = 0; slot < 3; slot++)
        {
            var c = painted ? _scheme!.ColorFor(slot) : new Color(0.35f, 0.35f, 0.35f);
            _swatches[slot].Color = c;
            _rgb[slot, 0].Value = Mathf.Round(c.R * 255f);
            _rgb[slot, 1].Value = Mathf.Round(c.G * 255f);
            _rgb[slot, 2].Value = Mathf.Round(c.B * 255f);
            _decalLabels[slot].Text = painted ? DecalText(slot) : "-";
        }
        _cliLabel.Text = CliArgs();
        _suppressCallbacks = false;
    }

    private string DecalText(int slot)
    {
        int index = slot switch { 0 => _scheme!.NoseDecal, 1 => _scheme!.TailDecal, _ => _scheme!.WingDecal };
        var name = _textures.FindByDecalIndex(index);
        return name != null ? $"{index:00}  {name}" : $"{index:00}  (absent)";
    }

    // The exact CLI arguments that reproduce the current livery, the lab's output.
    // A pattern is emitted only when the colours and decals still match it verbatim;
    // otherwise the explicit colour/decal overrides carry the whole scheme.
    private string CliArgs()
    {
        if (_scheme == null)
            return "--paint=none";
        var s = _scheme;
        // _patternIndex indexes THIS AIRCRAFT's pattern list, not the vehicle.json catalog,
        // different lists, different lengths. Resolve the catalog entry by name.
        bool clean = _patternIndex >= 0
            && CatalogFor(_patterns[_patternIndex]) is { } canonical
            && SameAs(canonical, s);
        if (clean)
            return $"--paint={s.Pattern}";
        string pattern = _patternIndex >= 0 ? $"--paint={s.Pattern} " : "";
        return pattern
            + $"--paint-color={Rgb(s.Color1)}/{Rgb(s.Color2)}/{Rgb(s.Color3)} "
            + $"--paint-decal={s.NoseDecal},{s.TailDecal},{s.WingDecal}";
    }

    private void BuildUi()
    {
        // Starts HIDDEN: an unadorned --viewer must render the same clean orbit the pre-paint
        // viewer did, so every scripted screenshot stays deterministic (and byte-identical).
        // L brings the panel up. The livery itself still applies whether or not it is shown.
        _ui = new CanvasLayer { Layer = 1, Visible = false };
        // Anchored top-RIGHT so it never collides with the damage lab's top-left panel,
        // --viewer --damage opens both at once.
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var panel = new PanelContainer { SelfModulate = new Color(1, 1, 1, 0.85f) };
        panel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        panel.GrowHorizontal = Control.GrowDirection.Begin;
        panel.Position = new Vector2(-8, 8);

        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 10);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);

        box.AddChild(new Label { Text = "LIVERY LAB" });
        box.AddChild(Small("L hides this panel · F19 toggles the damage lab"));
        box.AddChild(Small("patterns are per aircraft, this plane's set only"));

        _paintedToggle = new CheckButton { Text = "painted" };
        _paintedToggle.Toggled += on => { if (!_suppressCallbacks) SetPainted(on); };
        box.AddChild(_paintedToggle);

        // pattern stepper
        _patternLabel = new Label();
        var patternRow = new HBoxContainer();
        patternRow.AddChild(StepButton("<", () => SelectPattern(-1)));
        _patternLabel.CustomMinimumSize = new Vector2(230, 0);
        _patternLabel.VerticalAlignment = VerticalAlignment.Center;
        patternRow.AddChild(_patternLabel);
        patternRow.AddChild(StepButton(">", () => SelectPattern(1)));
        box.AddChild(patternRow);

        box.AddChild(Separator());

        // three colour slots: swatch + R/G/B
        string[] slotNames = { "1  body", "2  dark trim", "3  light trim" };
        for (int slot = 0; slot < 3; slot++)
        {
            int captured = slot;
            var header = new HBoxContainer();
            _swatches[slot] = new ColorRect { CustomMinimumSize = new Vector2(26, 14) };
            header.AddChild(_swatches[slot]);
            header.AddChild(new Label { Text = "  colour " + slotNames[slot] });
            box.AddChild(header);

            var row = new HBoxContainer();
            for (int ch = 0; ch < 3; ch++)
            {
                int cc = ch;
                row.AddChild(Small("RGB"[ch].ToString()));
                var slider = new HSlider
                {
                    MinValue = 0,
                    MaxValue = 255,
                    Step = 1,
                    CustomMinimumSize = new Vector2(78, 0),
                };
                slider.ValueChanged += _ =>
                {
                    if (_suppressCallbacks || _scheme == null)
                        return;
                    var c = _scheme.ColorFor(captured);
                    float v = (float)_rgb[captured, cc].Value / 255f;
                    SetColor(captured, cc switch
                    {
                        0 => new Color(v, c.G, c.B),
                        1 => new Color(c.R, v, c.B),
                        _ => new Color(c.R, c.G, v),
                    });
                };
                _rgb[slot, ch] = slider;
                row.AddChild(slider);
            }
            box.AddChild(row);
        }

        box.AddChild(Separator());

        // three decal slots
        string[] decalNames = { "nose", "tail", "wing" };
        for (int slot = 0; slot < 3; slot++)
        {
            int captured = slot;
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = decalNames[slot].PadRight(5) });
            row.AddChild(StepButton("<", () => StepDecal(captured, -1)));
            _decalLabels[slot] = new Label
            {
                CustomMinimumSize = new Vector2(190, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.AddChild(_decalLabels[slot]);
            row.AddChild(StepButton(">", () => StepDecal(captured, 1)));
            box.AddChild(row);
        }
        box.AddChild(Small("00–20 squadron logos · 21–49 nose art"));

        box.AddChild(Separator());

        var squadron = new Button { Text = "reset to squadron colours" };
        squadron.Pressed += LoadSquadronLivery;
        box.AddChild(squadron);

        var random = new Button { Text = "random livery" };
        random.Pressed += Randomize;
        box.AddChild(random);

        _cliLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _cliLabel.CustomMinimumSize = new Vector2(300, 0);
        _cliLabel.AddThemeFontSizeOverride("font_size", 11);
        box.AddChild(_cliLabel);

        var copy = new Button { Text = "copy CLI args" };
        copy.Pressed += () =>
        {
            var args = CliArgs();
            DisplayServer.ClipboardSet(args);
            Log.Info("ui", $"[livery] {args}");
        };
        box.AddChild(copy);

        margin.AddChild(box);
        panel.AddChild(margin);
        root.AddChild(panel);
        _ui.AddChild(root);
        AddChild(_ui);
    }
}
