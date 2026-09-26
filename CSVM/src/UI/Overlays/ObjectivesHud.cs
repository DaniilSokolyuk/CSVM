using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Flight.Modes;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.UI.Boards;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Overlays;

/// <summary>One line of the objectives readout: a display row's own priority, its text resolved
/// through the message table, and whether it is completed.</summary>
public readonly record struct ObjectivesHudLine(int Priority, string Text, bool Completed);

/// <summary>One row as the readout draws it: the line behind it, whether the completion mark is
/// over it, that mark's box in the row's own frame (the origin is the row's first character), and
/// the colour the row's text is drawn in. The mark is centred on the origin and the text colour
/// never changes with completion, which is what the original's pause parchment does.</summary>
public readonly record struct ObjectivesHudRow(
    ObjectivesHudLine Line, bool Marked, Rect2 MarkBox, Color TextColor);

/// <summary>
/// The flown campaign mission's objectives readout, on the PAUSE screen alone: the original keeps
/// its objectives on the pause screen's parchment and leaves the flight HUD to the gauges, so this
/// layer stays hidden until <see cref="PauseState.Paused"/>. Content is
/// <see cref="CampaignDirector"/>'s read model (<see cref="ObjectiveGraph.Rows"/>), text resolved
/// through <see cref="Messages"/>, completion marked rather than dropped. Every row is shown
/// unconditionally, never filtered by its own Awake flag: see docs/architecture.md's entry for
/// why. The reference frame fixes the corner and nothing else, so the metrics below stay TUNE; the
/// completion mark is the original's own art and its placement is not. Self-mounting, but unlike
/// <see cref="PerfHud"/>'s one-for-the-window instance, a splitscreen session builds ONE PER RIG
/// under that rig's own <c>HudParent</c>, so every pane polls the shared graph on its own.
/// </summary>
public sealed partial class ObjectivesHud : Node
{
    /// <summary>The completion mark's art in an extracted <c>rimage</c> set.</summary>
    public const string MarkFile = "obj_check1.png";

    private const float RefFontSize = 20f;      // TUNE: the reference frame fixes no type size
    private const float RefHeadingSize = 24f;   // TUNE
    private const float RefTopMargin = 48f;     // TUNE
    private const float RefSideMargin = 48f;    // TUNE
    private const float RefPanelWidth = 460f;   // TUNE: the reference parchment's rough share

    // The type size the mark's art was cut against, so the mark keeps its share of a row here.
    // The original sets its objectives list in ObjList, "Andy Bold" at 14 px (fonts.zrd), and our
    // own RefFontSize above is a TUNE choice rather than that 14.
    private const float MarkReferenceFontSize = 14f;

    private readonly CampaignDirector _director;
    private readonly Messages _messages;
    private readonly PauseState _pause;
    private readonly Texture2D? _mark;
    private CanvasLayer? _hudLayer;
    private Control? _root;
    private PanelContainer? _panel;
    private Label? _heading;
    private VBoxContainer? _rows;
    private ObjectiveGraph? _graph;
    private string _drawn = "";

    private ObjectivesHud(
        CampaignDirector director, Messages messages, PauseState pause, Texture2D? mark)
    {
        Name = "objectives_hud";
        _director = director;
        _messages = messages;
        _pause = pause;
        _mark = mark;
    }

    /// <summary>Builds the readout over a campaign director, the shared message table, the session's
    /// pause state and the completion mark (<see cref="LoadMark"/>). The director's graph may not
    /// exist yet (<see cref="CampaignDirector.Attach"/> runs after this node would be added), so
    /// binding is polled in <see cref="_Process"/> rather than assumed at construction.</summary>
    public static ObjectivesHud Build(
        CampaignDirector director, Messages messages, PauseState pause, Texture2D? mark) =>
        new(director, messages, pause, mark);

    /// <summary>Loads the completion mark from an extracted <c>rimage</c> directory. Null (with one
    /// log line) when the file is absent, which leaves a completed row unmarked; the art carries its
    /// own alpha, so no colour-keying is needed.</summary>
    public static Texture2D? LoadMark(string rimageDir)
    {
        var path = Path.Combine(rimageDir, MarkFile);
        if (!File.Exists(path))
        {
            Log.Warn("ui", $"no {MarkFile} in {rimageDir}, completion marks off (run ExtractRof.ps1)");
            return null;
        }

        var img = Image.LoadFromFile(path);
        return img != null ? ImageTexture.CreateFromImage(img) : null;
    }

    /// <summary>The current display lines: one per <see cref="ObjectiveGraph.Rows"/> row, in the
    /// graph's own priority order, text resolved through the message table. Exposed separately
    /// from drawing so an in-engine suite can assert the readout's own content follows the graph,
    /// not just the graph's internal state.</summary>
    public IReadOnlyList<ObjectivesHudLine> BuildLines()
    {
        var lines = new List<ObjectivesHudLine>();
        if (_graph == null)
        {
            return lines;
        }

        foreach (var row in _graph.Rows)
        {
            lines.Add(new ObjectivesHudLine(row.Priority, _messages.Get(row.MessageKey), row.Completed));
        }

        return lines;
    }

    /// <summary>The lines actually drawn: <see cref="BuildLines"/> minus every row the mission
    /// gives no message key, which resolves to no text at all (C3/M01 authors two such secondary
    /// rows). Drawn anyway, such a row is a mark against blank space, which is what stopped a
    /// player reading WHICH objective had completed; the original's pause screen lists the keyed
    /// rows alone.</summary>
    public IReadOnlyList<ObjectivesHudLine> DrawnLines()
    {
        var drawn = new List<ObjectivesHudLine>();
        foreach (var line in BuildLines())
        {
            if (!string.IsNullOrWhiteSpace(line.Text))
            {
                drawn.Add(line);
            }
        }

        return drawn;
    }

    /// <summary>The rows as they actually stand on the board: one per <see cref="DrawnLines"/> line,
    /// each carrying whether the mark is over it, the mark's box in that row's own frame, and the
    /// colour its text is drawn in. This is what holds the placement (a mark centred on the row's
    /// first characters, moving and recolouring nothing) rather than the completion bit alone.
    /// Empty until the board has been drawn once.</summary>
    public IReadOnlyList<ObjectivesHudRow> DrawnRows()
    {
        var rows = new List<ObjectivesHudRow>();
        if (_rows == null)
        {
            return rows;
        }

        var lines = DrawnLines();
        foreach (var child in _rows.GetChildren())
        {
            if (child is not Label text || rows.Count >= lines.Count)
            {
                continue;
            }

            var box = new Rect2();
            bool marked = false;
            foreach (var over in text.GetChildren())
            {
                if (over is TextureRect drawnMark)
                {
                    marked = true;
                    box = new Rect2(drawnMark.Position, drawnMark.Size);
                }
            }

            rows.Add(new ObjectivesHudRow(
                lines[rows.Count], marked, box, text.GetThemeColor("font_color")));
        }

        return rows;
    }

    public override void _Ready() => EnsureBuilt();

    public override void _Process(double delta)
    {
        if (_graph == null && _director.Graph is { } graph)
        {
            Attach(graph);
        }

        // Only the pause screen carries the readout, so nothing is drawn or rebuilt while flying.
        if (_hudLayer != null)
        {
            _hudLayer.Visible = _pause.Paused;
        }

        if (_pause.Paused)
        {
            Refresh();
        }
    }

    private void Attach(ObjectiveGraph graph)
    {
        _graph = graph;
        graph.Woke += _ => Refresh();
        graph.Completed += _ => Refresh();
    }

    private void EnsureBuilt()
    {
        if (_hudLayer != null)
        {
            return;
        }

        // The board layer, not the HUD one: this is part of the pause screen, and the session adds
        // it after the pause board, so tree order puts it over that board's own backdrop.
        _hudLayer = new CanvasLayer { Layer = HudLayers.Board, Name = "objectives_hud_layer", Visible = false };
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _heading = new Label { Text = "Objectives" };
        _rows = new VBoxContainer();
        box.AddChild(_heading);
        box.AddChild(_rows);
        _panel.AddChild(box);
        _root.AddChild(_panel);
        _hudLayer.AddChild(_root);
        AddChild(_hudLayer);
    }

    // Redraws only when the content or the window scale actually changed: a pause holds for as
    // long as the player leaves it holding, and rebuilding label nodes every frame of that is
    // waste nobody can see.
    private void Refresh()
    {
        if (_rows == null || _panel == null || _heading == null || _root == null)
        {
            return;
        }

        var lines = DrawnLines();
        float scale = WindowScale();
        // The box's right edge joins the signature: a window that widens without changing height
        // moves the panel, and the height ratio alone would report nothing to redraw.
        var signature = new StringBuilder().Append(scale.ToString("0.###"))
            .Append('|').Append(PanelRight().ToString("0.###"));
        foreach (var line in lines)
        {
            signature.Append(line.Completed ? "|1|" : "|0|").Append(line.Text);
        }

        if (signature.ToString() == _drawn)
        {
            return;
        }

        _drawn = signature.ToString();
        Draw(lines, scale);
    }

    private void Draw(IReadOnlyList<ObjectivesHudLine> lines, float scale)
    {
        int font = Mathf.Max(1, Mathf.RoundToInt(RefFontSize * scale));
        float textWidth = RefPanelWidth * scale;
        foreach (var child in _rows!.GetChildren())
        {
            _rows.RemoveChild(child);
            child.QueueFree();
        }

        foreach (var line in lines)
        {
            var text = new Label
            {
                Text = line.Text,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(textWidth, 0f),
            };
            text.AddThemeFontSizeOverride("font_size", font);
            // The completed row keeps the colour it had open: the mark is the only thing that says
            // it is done, as it is on the original's parchment.
            if (line.Completed && _mark != null)
            {
                text.AddChild(MarkOver(font));
            }

            _rows.AddChild(text);
        }

        _heading!.AddThemeFontSizeOverride("font_size", Mathf.Max(1, Mathf.RoundToInt(RefHeadingSize * scale)));
        _panel!.AddThemeStyleboxOverride("panel", PanelStyle(scale));
        _panel.Size = Vector2.Zero;      // a Control clamps up to its combined minimum size
        // ⚠ The right edge is the reading box's, not the window's: on a window wider than the
        // reference frame this panel would otherwise stand out at the far edge, away from the
        // paused board it belongs with. The two edges are the same at 16:9 and under.
        _panel.Position = new Vector2(
            Mathf.Max(0f, PanelRight() - ((RefPanelWidth + RefSideMargin) * scale)), RefTopMargin * scale);
    }

    // The mark is centred on the row's own origin, over its leading characters, which is what
    // escape.zrd's CHECKMARK primitive asks for (CENTER) and where the original draws it. A child
    // of the row's own label rather than a cell beside it, so it cannot move the text it marks.
    private TextureRect MarkOver(int font)
    {
        var size = _mark!.GetSize() * (font / MarkReferenceFontSize);
        return new TextureRect
        {
            Texture = _mark,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            Size = size,
            Position = -size * 0.5f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
    }

    private StyleBoxFlat PanelStyle(float scale) => new()
    {
        BgColor = new Color(0f, 0f, 0f, 0.55f),
        ContentMarginLeft = 16f * scale,
        ContentMarginRight = 16f * scale,
        ContentMarginTop = 12f * scale,
        ContentMarginBottom = 12f * scale,
    };

    // The edge this panel hangs from: the reading box's right, which is the window's own on any
    // window 16:9 or narrower (Flight.Hud.HudMetrics.ReadingBox).
    private float PanelRight() => Flight.Hud.HudMetrics.ReadingBox(_root!).End.X;

    // The window-height ratio, not HudMetrics.Scale's pane-damped form: this readout draws once
    // for the whole window regardless of splitscreen, the same reasoning PerfHud's own override
    // gives for taking the plain ratio instead.
    private float WindowScale()
    {
        float windowH = GetTree()?.Root?.Size.Y ?? Flight.Hud.HudMetrics.ReferenceHeight;
        return windowH / Flight.Hud.HudMetrics.ReferenceHeight;
    }
}
