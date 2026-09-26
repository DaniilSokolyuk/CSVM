using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Camera;
using CSVM.Mech3;
using CSVM.Tooling;
using CSVM.UI.Boards;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Labs;

/// <summary>
/// The node lab (N) in <c>--freecam</c>/<c>--anim-lab</c>: a dockable panel holding the world's
/// node tree by <c>cs_name</c>, a search box, per-node actions (frame the camera, hide/show and
/// export the subtree), the export set's actions and a dependency readout for whatever <see cref="SelectionService"/> currently has. A
/// second view lists the chapter's destructibles with coverage columns. The tree is lazy: a
/// branch populates only when expanded. Full behaviour: this module's entry in
/// docs/architecture.md.
/// ⚠ A dependency source this mode does not build must say so, never show an empty list: an empty
/// collider list here is the instrument missing, not missing colliders.
/// </summary>
public sealed partial class NodeLab : Node
{
    /// <summary>Rows a single branch shows before it stops and points at the search box. A world
    /// root can have thousands of direct children; an unbounded branch would defeat the laziness
    /// the tree exists for.</summary>
    public const int MaxBranchItems = 500;

    /// <summary>Search hits listed at once. The full match count is always reported.</summary>
    public const int MaxSearchResults = 300;

    // Definitions and textures listed inline before the readout elides.
    private const int MaxListed = 12;

    // Unresolved destructibles get a far larger allowance than the resolved ones, because that is
    // the list somebody is scanning for a specific entry; a full world has a dozen. A slice has
    // hundreds by construction, so the cap exists, and the elided count is always printed.
    private const int MaxUnresolvedListed = 40;

    private const float StatusPeriod = 0.25f;

    private static readonly Color Amber = new(1f, 0.93f, 0.35f);
    private static readonly Color Loud = new(1f, 0.42f, 0.42f);
    private static readonly Color Dim = new(0.62f, 0.62f, 0.68f);

    private readonly Node3D _world;
    private readonly SelectionService _selection;
    private readonly ExportSet _set;
    private readonly AnimRuntime? _runtime;
    private readonly AnimProgram? _program;
    private readonly SceneBuilder? _scene;
    private readonly bool _collisionBuilt;
    private readonly Dictionary<ulong, Node3D> _itemNode = new();
    private readonly Dictionary<ulong, TreeItem> _nodeItem = new();
    private readonly HashSet<ulong> _stubs = new();

    private CanvasLayer? _layer;
    private Tree? _tree;
    private LineEdit? _search;
    private Label? _status;
    private Label? _setLabel;
    private RichTextLabel? _deps;
    private Button? _hideBtn;
    private CheckButton? _destBtn;

    private bool _open;
    private bool _destView;
    private bool _syncing;
    private float _statusTimer;
    private int _debugFrames = -1;

    private TreeItem? _rootItem;

    private List<(string Name, Node3D Node)>? _nameIndex;
    private Dictionary<ulong, List<AnimDefinition>>? _defsByAnchor;
    private Dictionary<string, List<AnimDefinition>>? _defsByTarget;
    private Dictionary<ulong, string>? _textureOfMaterial;

    public NodeLab(Node3D world, SelectionService selection, AnimRuntime? runtime,
        AnimProgram? program, SceneBuilder? scene, bool collisionBuilt)
    {
        _world = world;
        _selection = selection;
        _set = new ExportSet(selection);
        _runtime = runtime;
        _program = program;
        _scene = scene;
        _collisionBuilt = collisionBuilt;
        Name = "node_lab";
    }

    /// <summary>Extra subtrees to list alongside the world content, props parked beside it rather
    /// than under it (the anim lab's <c>--plane=</c> stage prop), which the tree, the search index
    /// and <see cref="SelectByName"/> would otherwise miss. Shares the selection's list by
    /// reference, populated after construction; read when the tree/index are first built.</summary>
    public IReadOnlyList<Node3D> ExtraRoots { get; init; } = Array.Empty<Node3D>();

    /// <summary>Resolves the session camera at the moment it is needed. The freecam is created
    /// after this lab is, so the reference cannot be captured at construction.</summary>
    public Func<SpectatorCamera?>? CameraSource { get; init; }

    /// <summary>Pixels of window kept clear below the panel, the anim lab parks its timeline
    /// strip there, plain freecam does not.</summary>
    public int BottomMargin { get; init; } = 16;

    /// <summary><c>--debug-nodelab[=deps,dest,open,node=&lt;cs_name&gt;]</c>: open the panel at
    /// launch and dump the requested readouts to the log once the selection has settled, the
    /// scripted stand-in for pressing N and reading the panel, which live input cannot do here.</summary>
    public string? DebugSpec { get; init; }

    /// <summary>Whether the panel is showing. Nothing is built until it first opens, so a capture
    /// without N, and without <c>--debug-nodelab</c>, renders as if this file did not exist.</summary>
    public bool IsOpen => _open;

    /// <summary>Parses <c>--debug-nodelab[=spec]</c>: a comma-separated list of <c>deps</c>,
    /// <c>dest</c>, <c>open</c> and <c>node=&lt;cs_name&gt;</c>. Unknown tokens are reported and
    /// dropped rather than silently disabling the dump the run was launched for.</summary>
    public static string ParseDebugSpec(string spec, List<string>? rejected = null)
    {
        var kept = new List<string>();
        foreach (string token in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (token.Equals("deps", StringComparison.OrdinalIgnoreCase)
                || token.Equals("dest", StringComparison.OrdinalIgnoreCase)
                || token.Equals("open", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("node=", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(token);
                continue;
            }
            // A caller that supplies the list wants the tokens back as data, not in the log, that
            // is what lets the spec normalise a value without a Godot runtime to print into.
            if (rejected != null)
            {
                rejected.Add(token);
                continue;
            }
            Log.Warn("ui", $"--debug-nodelab token '{token}' is not deps/dest/open/all/node=<cs_name>, ignoring it");
        }
        return string.Join(",", kept);
    }

    public override void _Ready()
    {
        // The set joins the tree whether or not the panel is ever opened: Ctrl+click gathers it
        // through the selection, and it draws nothing until a node is in it.
        AddChild(_set);
        _selection.Changed += OnSelectionChanged;
        if (DebugSpec != null)
        {
            Toggle();
            _debugFrames = 0;
            return;
        }
        SetProcess(false);
    }

    public override void _ExitTree()
    {
        _selection.Changed -= OnSelectionChanged;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.N })
        {
            Toggle();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (_debugFrames >= 0)
        {
            // Deferred two frames: SelectionService runs its own scripted pick on its first
            // frame, and the readout is only interesting once it has something selected.
            _debugFrames++;
            if (_debugFrames >= 2)
            {
                _debugFrames = -1;
                RunDebugDump();
            }
        }
        if (!_open)
        {
            return;
        }
        _statusTimer += (float)delta;
        if (_statusTimer >= StatusPeriod)
        {
            _statusTimer = 0f;
            UpdateStatus();
        }
    }

    // ---- panel --------------------------------------------------------------------------------

    /// <summary>Shows or hides the panel, building it on the first open.</summary>
    public void Toggle()
    {
        if (_layer == null)
        {
            BuildUi();
            _open = true;
            SetProcess(true);
            RebuildTree();
            RefreshDeps();
            UpdateStatus();
            Log.Info("ui", $"nodelab open destructibles={_destView} collision_built={_collisionBuilt}");
            return;
        }
        _open = !_open;
        _layer.Visible = _open;
        SetProcess(_open || _debugFrames >= 0);
        if (_open)
        {
            RebuildTree();
            RefreshDeps();
            UpdateStatus();
        }
        Log.Info("ui", $"nodelab {(_open ? "open" : "closed")}");
    }

    // ---- actions ------------------------------------------------------------------------------

    /// <summary>Puts the camera on the selection's measured subtree box and locks the orbit onto
    /// it, so a search hit half a map away is one double-click.</summary>
    public void FrameSelection()
    {
        if (_selection.Current is not { } node || !IsInstanceValid(node))
        {
            Log.Info("ui", $"nodelab frame, nothing is selected");
            return;
        }
        if (CameraSource?.Invoke() is not { } cam)
        {
            Log.Warn("ui", $"nodelab frame, this session has no free camera to move");
            return;
        }
        var box = _selection.CurrentBox;
        if (box.Size.LengthSquared() < 1e-4f)
        {
            // A meshless pivot rung: orbit a nominal box around it rather than the world origin.
            box = new Aabb(node.GlobalPosition - Vector3.One * 10f, Vector3.One * 20f);
        }
        cam.Frame(box);
        cam.FollowNode(node);
        var c = box.GetCenter();
        Log.Info("ui", $"nodelab frame node={SelectionService.NameOf(node)} centre=({c.X:0.0},{c.Y:0.0},{c.Z:0.0}) size=({box.Size.X:0.0},{box.Size.Y:0.0},{box.Size.Z:0.0})");
    }

    /// <summary>Flips the selected subtree's <c>Visible</c>, nothing is torn down, so it is
    /// reversible, and an animation that re-shows the node afterwards is the data working, not a
    /// bug. The panel reports live <c>Visible</c> so that reads as what it is.</summary>
    public void ToggleHide()
    {
        if (_selection.Current is not { } node || !IsInstanceValid(node))
        {
            Log.Info("ui", $"nodelab hide, nothing is selected");
            return;
        }
        node.Visible = !node.Visible;
        Log.Info("ui", $"nodelab {(node.Visible ? "show" : "hide")} node={SelectionService.NameOf(node)} visible={node.Visible} in_tree={node.IsVisibleInTree()}");
        UpdateStatus();
        RefreshDeps();
    }

    /// <summary>Writes the selected subtree as a timestamped GLB in <c>Exports/</c>.</summary>
    public void ExportSelection()
    {
        if (_selection.Current is not { } node || !IsInstanceValid(node))
        {
            Log.Info("ui", $"nodelab export, nothing is selected");
            return;
        }
        string nodeName = SelectionService.NameOf(node);
        Log.Info("ui", $"nodelab export node={nodeName}");
        GltfExporter.ExportToExports(node, nodeName);
    }

    /// <summary>Flips the current rung in the export set, so a tree row or a walked-up rung can
    /// join it as well as a Ctrl+clicked leaf.</summary>
    public void ToggleSetMembership()
    {
        if (_selection.Current is not { } node || !IsInstanceValid(node))
        {
            Log.Info("ui", $"nodelab set, nothing is selected");
            return;
        }
        _set.Toggle(node);
        UpdateStatus();
    }

    /// <summary>The dependency readout for one node as plain lines, the same text the panel shows
    /// and the scripted dump logs, so the two can never disagree. Null node yields the
    /// nothing-selected notice.</summary>
    public List<string> DependencyLines(Node3D? node)
    {
        var lines = new List<string>();
        if (node == null || !IsInstanceValid(node))
        {
            lines.Add("nothing selected");
            return lines;
        }
        var box = SelectionService.SubtreeWorldAabb(node);
        var c = box.GetCenter();
        lines.Add(Log.Format($"node cs_name={SelectionService.NameOf(node)} godot={node.Name} visible={node.Visible} in_tree={node.IsVisibleInTree()}"));
        lines.Add(Log.Format($"box centre=({c.X:0.0},{c.Y:0.0},{c.Z:0.0}) size=({box.Size.X:0.0},{box.Size.Y:0.0},{box.Size.Z:0.0})"));
        AddAnimLines(node, lines);
        AddDestructibleLines(node, lines);
        AddGeometryLines(node, lines);
        AddColliderLines(node, lines);
        return lines;
    }

    /// <summary>Selects a node by <c>cs_name</c>, the tree panel's own entry, and the way to reach
    /// anything the click cannot (hidden, or off screen). An exact match wins; failing that the first substring match, with the full candidate list logged so an
    /// ambiguous name is visible rather than silently resolved.</summary>
    public bool SelectByName(string name)
    {
        EnsureNameIndex();
        var matches = _nameIndex!
            .Where(e => IsInstanceValid(e.Node)
                        && e.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        var exact = matches.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var pick = exact.Node != null ? exact : matches.FirstOrDefault();
        if (pick.Node == null)
        {
            Log.Warn("ui", $"nodelab select name='{name}' matched nothing in this world");
            return false;
        }
        Log.Info("ui", $"nodelab select name='{name}' → '{pick.Name}' matches={matches.Count} exact={(exact.Node != null)} candidates=[{string.Join(" ", matches.Take(MaxListed).Select(m => m.Name))}]");
        _selection.Select(pick.Node);
        return true;
    }

    /// <summary>Test-only hooks for the synchronous <c>--run-tests</c> harness, which runs a whole
    /// suite inside one <c>_Ready</c> call and so never gets a live engine frame to wait out the
    /// panel's own 4 Hz status timer or its selection-changed event. These call the exact private
    /// paths a live session fires on its own.</summary>
    internal void RevealSelectionForTest() => RevealSelection();

    internal void RefreshStatusForTest() => UpdateStatus();

    internal (string Text, bool Dim)? RowStateForTest(Node3D node) =>
        _nodeItem.TryGetValue(node.GetInstanceId(), out var item)
            ? (item.GetText(0), item.GetCustomColor(0) == Dim)
            : null;

    // ---- static helpers -------------------------------------------------------------------

    private static Label Small(string text)
    {
        var label = new Label { Text = text, Modulate = new Color(1, 1, 1, 0.65f) };
        label.AddThemeFontSizeOverride("font_size", 11);
        return label;
    }

    private static string RowText(Node3D node)
    {
        string name = SelectionService.NameOf(node);
        return node.Visible ? name : name + "  (hidden)";
    }

    // The nearest `cs_name`-bearing descendants of a node, the exact inverse of
    // the selection ladder's ancestor walk, so the tree's parent/child relation and the
    // breadcrumb's rungs are the same relation. SceneBuilder's unnamed wrappers are stepped
    // through, never shown.
    private static void CollectNamed(Node parent, List<Node3D> into)
    {
        foreach (var child in parent.GetChildren())
        {
            if (child is Node3D named && named.HasMeta(AnimRuntime.NameMeta))
            {
                into.Add(named);
            }
            else
            {
                CollectNamed(child, into);
            }
        }
    }

    private static bool HasNamed(Node parent)
    {
        foreach (var child in parent.GetChildren())
        {
            if (child is Node3D named && named.HasMeta(AnimRuntime.NameMeta))
            {
                return true;
            }
            if (HasNamed(child))
            {
                return true;
            }
        }
        return false;
    }

    // A full-width message row: the text spans every column and carries itself as its tooltip, so
    // nothing loud is lost to a column boundary.
    private static void Banner(TreeItem item, string text, Color color)
    {
        item.SetText(0, text);
        item.SetCustomColor(0, color);
        item.SetExpandRight(0, true);
        item.SetTooltipText(0, text);
        item.SetSelectable(0, false);
    }

    // Which of a definition's event kinds the runtime's dispatch table acts on. Reported per
    // definition rather than as a global tally, because the question a coverage column answers is
    // "would THIS object's destruction play out".
    private static (string Text, bool Ok) EventCoverage(AnimDefinition def)
    {
        var missing = new Dictionary<string, int>(StringComparer.Ordinal);
        var partial = new Dictionary<string, int>(StringComparer.Ordinal);
        int total = 0;
        void Scan(AnimSequence seq)
        {
            foreach (var ev in seq.Events)
            {
                total++;
                if (AnimRuntime.PartialEventKinds.Contains(ev.Kind))
                {
                    partial[ev.Kind] = partial.TryGetValue(ev.Kind, out int p) ? p + 1 : 1;
                }
                else if (!AnimRuntime.HandledEventKinds.Contains(ev.Kind))
                {
                    missing[ev.Kind] = missing.TryGetValue(ev.Kind, out int m) ? m + 1 : 1;
                }
            }
        }
        foreach (var seq in def.Sequences)
        {
            Scan(seq);
        }
        if (def.ResetState is { } reset)
        {
            Scan(reset);
        }
        if (missing.Count == 0 && partial.Count == 0)
        {
            return ($"{total} ok", true);
        }
        var parts = new List<string>();
        foreach (var kv in missing.OrderByDescending(kv => kv.Value))
        {
            parts.Add($"{kv.Key}×{kv.Value}");
        }
        foreach (var kv in partial.OrderByDescending(kv => kv.Value))
        {
            parts.Add($"{kv.Key}×{kv.Value}(partial)");
        }
        return (string.Join(", ", parts), missing.Count == 0);
    }

    private static string DefLabel(AnimDefinition def) =>
        def.AnimName is { Length: > 0 } anim && !anim.Equals(def.Name, StringComparison.OrdinalIgnoreCase)
            ? $"{anim}@{def.Name}"
            : def.Name.Length > 0 ? def.Name : "(unnamed)";

    private static string DefLine(AnimDefinition def, string relation)
    {
        var seqs = def.Sequences.Select(s => s.Name.Length > 0 ? s.Name : "(unnamed)");
        return Log.Format($"anim def={DefLabel(def)} rel={relation} activation={def.Activation} health={def.Health:0.#} source={(def.Archive != null ? "compiled" : "reader")} seqs=[{string.Join(" ", seqs)}]");
    }

    // The descending ANIM_HEALTH tests a DAMAGE_SEQUENCE branches on, how many progressive stages
    // the object can escalate through.
    private static int CountThresholds(AnimSequence damage) =>
        damage.Events.Count(e => e.Kind is "If" or "Elseif");

    // Every node name a definition refers to. A compiled def carries its own symbol table, which is
    // exact; a reader def is scanned for the two spellings mech3ax uses for a node reference, and
    // `name` is taken only from the OBJECT_* kinds, where it is a node rather than a sound, a
    // puffer, a light or another animation.
    private static IEnumerable<string> TargetNames(AnimDefinition def)
    {
        foreach (string name in def.NodeRefs.Keys)
        {
            yield return name;
        }
        foreach (var seq in def.Sequences)
        {
            foreach (string name in NamesIn(seq))
            {
                yield return name;
            }
        }
        if (def.ResetState is { } reset)
        {
            foreach (string name in NamesIn(reset))
            {
                yield return name;
            }
        }
        if (def.Name.Length > 0)
        {
            yield return def.Name;
        }

        static IEnumerable<string> NamesIn(AnimSequence seq)
        {
            foreach (var ev in seq.Events)
            {
                if (ev.Data.Str("node") is { } node)
                {
                    yield return node;
                }
                if (ev.Kind.StartsWith("Object", StringComparison.Ordinal)
                    && ev.Data.Str("name") is { } named)
                {
                    yield return named;
                }
            }
        }
    }

    private static IEnumerable<string> NameKeys(Node3D node)
    {
        string name = SelectionService.NameOf(node);
        yield return name;
        // Definitions name a node without its model-file suffix as often as with it, exactly as
        // the runtime's own matcher allows.
        if (name.EndsWith(".flt", StringComparison.OrdinalIgnoreCase))
        {
            yield return name[..^4];
        }
    }

    private void BuildUi()
    {
        _layer = new CanvasLayer { Layer = HudLayers.Lab };
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        // Down the left edge, clear of the freecam readout and the selection breadcrumb above it
        // and of the anim lab's timeline strip below.
        var panel = new PanelContainer { SelfModulate = new Color(1, 1, 1, 0.88f) };
        panel.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        panel.OffsetLeft = 8;
        panel.OffsetRight = 448;
        panel.OffsetTop = 126;
        panel.OffsetBottom = -BottomMargin;

        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, 8);
        }
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);

        var title = new Label { Text = "NODE LAB", Modulate = Amber };
        box.AddChild(title);
        _status = Small("");
        box.AddChild(_status);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 4);
        actions.AddChild(Btn("Frame", FrameSelection));
        _hideBtn = Btn("Hide", ToggleHide);
        actions.AddChild(_hideBtn);
        actions.AddChild(Btn("Export glTF", ExportSelection));
        actions.AddChild(Btn("Deps ⟳", RefreshDeps));
        _destBtn = new CheckButton { Text = "Destructibles", FocusMode = Control.FocusModeEnum.None };
        _destBtn.Toggled += on =>
        {
            GetViewport().GuiReleaseFocus();
            _destView = on;
            RebuildTree();
            UpdateStatus();
        };
        actions.AddChild(_destBtn);
        box.AddChild(actions);

        var setRow = new HBoxContainer();
        setRow.AddThemeConstantOverride("separation", 4);
        setRow.AddChild(Btn("± set", ToggleSetMembership));
        setRow.AddChild(Btn("Export set", _set.WriteGltf));
        setRow.AddChild(Btn("Clear set", ClearSet));
        _setLabel = Small("");
        _setLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _setLabel.ClipText = true;
        setRow.AddChild(_setLabel);
        box.AddChild(setRow);

        _search = new LineEdit { PlaceholderText = "search cs_name…" };
        _search.TextChanged += _ => RebuildTree();
        box.AddChild(_search);

        // The tree and the readout share whatever height the panel's anchors leave, two to one.
        // Neither carries a minimum height: a Container's children's minimums win over its anchor
        // rect, which would push the panel down over the anim lab's transport and timeline.
        _tree = new Tree
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 2f,
            HideRoot = false,
            SelectMode = Tree.SelectModeEnum.Row,
        };
        _tree.ItemSelected += OnItemSelected;
        _tree.ItemActivated += OnItemActivated;
        _tree.ItemCollapsed += OnItemCollapsed;
        box.AddChild(_tree);

        _deps = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1f,
            SelectionEnabled = true,
        };
        _deps.AddThemeFontSizeOverride("normal_font_size", 11);
        box.AddChild(_deps);

        box.AddChild(Small("click a row to select · double-click frames · Export glTF writes the selection · Export set writes the set as one file · N hides"));

        margin.AddChild(box);
        panel.AddChild(margin);
        root.AddChild(panel);
        _layer.AddChild(root);
        AddChild(_layer);
    }

    private void ClearSet()
    {
        _set.Clear();
        UpdateStatus();
    }

    private Button Btn(string text, Action pressed)
    {
        // FocusMode None keeps a clicked button from swallowing the mode keys afterwards, and
        // releasing GUI focus hands the keyboard back to the camera after typing in the search box.
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        b.Pressed += () =>
        {
            GetViewport().GuiReleaseFocus();
            pressed();
        };
        return b;
    }

    private void UpdateStatus()
    {
        if (_status == null)
        {
            return;
        }
        var node = _selection.Current;
        string sel = node == null || !IsInstanceValid(node)
            ? "nothing selected, click an object or a row"
            : Log.Format($"{SelectionService.NameOf(node)}  rung {_selection.Level + 1}/{_selection.Ladder.Count}  visible={node.Visible} in_tree={node.IsVisibleInTree()}");
        _status.Text = sel;
        if (_setLabel != null)
        {
            var set = _set.Members;
            _setLabel.Text = set.Count == 0
                ? "set empty, Ctrl+click adds"
                : $"set {set.Count}: {string.Join(" ", set.Where(IsInstanceValid).Select(SelectionService.NameOf))}";
            _setLabel.TooltipText = _setLabel.Text;
        }
        if (_hideBtn != null)
        {
            _hideBtn.Text = node != null && IsInstanceValid(node) && !node.Visible ? "Show" : "Hide";
        }
        RefreshTreeVisibility();
    }

    // Re-reads live `Visible` for every bound row on the panel's existing 4 Hz
    // status cadence, so a def re-showing a node updates its row without user input, the row
    // reflects the node, it never latches what a button last did.
    private void RefreshTreeVisibility()
    {
        if (_tree == null || _destView)
        {
            return;
        }
        foreach (var (nodeId, item) in _nodeItem)
        {
            if (item == _rootItem || GodotObject.InstanceFromId(nodeId) is not Node3D node || !IsInstanceValid(node))
            {
                continue;
            }
            item.SetText(0, RowText(node));
            if (node.Visible)
            {
                item.ClearCustomColor(0);
            }
            else
            {
                item.SetCustomColor(0, Dim);
            }
        }
    }

    // ---- selection sync -----------------------------------------------------------------------

    private void OnSelectionChanged(SelectionService selection, bool fresh)
    {
        if (!_open)
        {
            return;
        }
        RevealSelection();
        RefreshDeps();
        UpdateStatus();
    }

    // Walks the shared ladder from the outermost rung inward, expanding each branch on the way, so
    // a world click lands on the right tree row without the tree ever being built eagerly.
    private void RevealSelection()
    {
        if (_tree == null || _destView || _rootItem == null)
        {
            return;
        }
        var ladder = _selection.Ladder;
        if (ladder.Count == 0)
        {
            return;
        }
        var item = _rootItem;
        for (int i = ladder.Count - 1; i >= _selection.Level; i--)
        {
            if (ChildItemFor(item, ladder[i]) is not { } next)
            {
                return;
            }
            item = next;
        }
        _syncing = true;
        item.Select(0);
        _tree.ScrollToItem(item, true);
        _syncing = false;
    }

    // The tree row for one named child of an already-created row, expanding and populating the
    // parent first. Null when the child is past the branch cap or is not a named child at all.
    private TreeItem? ChildItemFor(TreeItem parent, Node3D child)
    {
        Expand(parent);
        return _nodeItem.TryGetValue(child.GetInstanceId(), out var item)
               && item.GetParent() == parent ? item : null;
    }

    private void OnItemSelected()
    {
        if (_syncing || _tree?.GetSelected() is not { } item)
        {
            return;
        }
        if (!_itemNode.TryGetValue(item.GetInstanceId(), out var node) || !IsInstanceValid(node))
        {
            return;
        }
        _syncing = true;
        _selection.Select(node);
        _syncing = false;
        RefreshDeps();
        UpdateStatus();
    }

    private void OnItemActivated()
    {
        OnItemSelected();
        FrameSelection();
    }

    // ---- tree ---------------------------------------------------------------------------------

    private void RebuildTree()
    {
        if (_tree == null)
        {
            return;
        }
        _tree.Clear();
        _itemNode.Clear();
        _nodeItem.Clear();
        _stubs.Clear();
        _rootItem = null;
        if (_destView)
        {
            BuildDestructibleRows();
            return;
        }
        string query = _search?.Text.Trim() ?? "";
        if (query.Length > 0)
        {
            BuildSearchRows(query);
            return;
        }
        BuildBrowseRows();
    }

    private void BuildBrowseRows()
    {
        var tree = _tree!;
        tree.Columns = 1;
        tree.ColumnTitlesVisible = false;
        tree.HideRoot = false;
        _rootItem = tree.CreateItem();
        _rootItem.SetText(0, "world");
        _rootItem.SetCustomColor(0, Amber);
        Bind(_rootItem, _world);
        int children = Populate(_rootItem, _world);
        // Props parked beside the world content (the anim lab's --plane=): each is its own named
        // top-level branch under "world", so a click-less tester can still reach the plane's parts.
        foreach (var extra in ExtraRoots)
        {
            if (!IsInstanceValid(extra) || !extra.HasMeta(AnimRuntime.NameMeta))
            {
                continue;
            }
            var item = tree.CreateItem(_rootItem);
            item.SetText(0, RowText(extra));
            Bind(item, extra);
            children += Populate(item, extra) + 1;
        }
        _rootItem.Collapsed = false;
        Log.Debug("ui", $"nodelab tree root named_children={children} rows={Math.Min(children, MaxBranchItems)} cap={MaxBranchItems}");
        RevealSelection();
    }

    private void BuildSearchRows(string query)
    {
        var tree = _tree!;
        tree.Columns = 1;
        tree.ColumnTitlesVisible = false;
        tree.HideRoot = false;
        EnsureNameIndex();
        var hits = new List<(string Name, Node3D Node)>();
        int total = 0;
        foreach (var entry in _nameIndex!)
        {
            if (!IsInstanceValid(entry.Node)
                || entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }
            total++;
            if (hits.Count < MaxSearchResults)
            {
                hits.Add(entry);
            }
        }
        _rootItem = tree.CreateItem();
        _rootItem.SetText(0, $"search '{query}', {hits.Count} of {total} match(es)");
        _rootItem.SetCustomColor(0, Amber);
        _rootItem.SetSelectable(0, false);
        foreach (var (_, node) in hits)
        {
            var item = tree.CreateItem(_rootItem);
            item.SetText(0, RowText(node));
            if (!node.Visible)
            {
                item.SetCustomColor(0, Dim);
            }
            Bind(item, node);
        }
        if (total > hits.Count)
        {
            var more = tree.CreateItem(_rootItem);
            more.SetText(0, $"… {total - hits.Count} more, narrow the search");
            more.SetCustomColor(0, Dim);
            more.SetSelectable(0, false);
        }
    }

    // One branch's rows. Each child that has named descendants of its own gets a single placeholder
    // child so the expand arrow shows; Expand reuses that placeholder as the first real row.
    private int Populate(TreeItem item, Node3D node)
    {
        var tree = _tree!;
        var kids = new List<Node3D>();
        CollectNamed(node, kids);
        kids.Sort((a, b) => string.Compare(SelectionService.NameOf(a), SelectionService.NameOf(b),
            StringComparison.OrdinalIgnoreCase));
        int shown = Math.Min(kids.Count, MaxBranchItems);
        for (int i = 0; i < shown; i++)
        {
            var child = kids[i];
            var row = tree.CreateItem(item);
            row.SetText(0, RowText(child));
            if (!child.Visible)
            {
                row.SetCustomColor(0, Dim);
            }
            Bind(row, child);
            if (HasNamed(child))
            {
                var stub = tree.CreateItem(row);
                stub.SetText(0, "…");
                stub.SetSelectable(0, false);
                row.Collapsed = true;
                _stubs.Add(row.GetInstanceId());
            }
        }
        if (kids.Count > shown)
        {
            var more = tree.CreateItem(item);
            more.SetText(0, $"… {kids.Count - shown} more, use the search box");
            more.SetCustomColor(0, Dim);
            more.SetSelectable(0, false);
        }
        return kids.Count;
    }

    private void OnItemCollapsed(TreeItem item)
    {
        if (!item.Collapsed)
        {
            Expand(item);
        }
    }

    // Turns a placeholder branch into its real rows. Idempotent, a branch already populated, or
    // one that never had a placeholder, is left alone.
    private void Expand(TreeItem item)
    {
        item.Collapsed = false;
        ulong id = item.GetInstanceId();
        if (!_stubs.Remove(id))
        {
            return;
        }
        if (!_itemNode.TryGetValue(id, out var node) || !IsInstanceValid(node))
        {
            return;
        }
        var kids = new List<Node3D>();
        CollectNamed(node, kids);
        kids.Sort((a, b) => string.Compare(SelectionService.NameOf(a), SelectionService.NameOf(b),
            StringComparison.OrdinalIgnoreCase));
        var tree = _tree!;
        int shown = Math.Min(kids.Count, MaxBranchItems);
        // The placeholder becomes the first row rather than being freed, so no TreeItem lifetime
        // question arises; the remaining rows append after it in order.
        var reuse = item.GetFirstChild();
        for (int i = 0; i < shown; i++)
        {
            var child = kids[i];
            var row = i == 0 && reuse != null ? reuse : tree.CreateItem(item);
            row.SetText(0, RowText(child));
            row.SetSelectable(0, true);
            row.ClearCustomColor(0);
            if (!child.Visible)
            {
                row.SetCustomColor(0, Dim);
            }
            Bind(row, child);
            if (HasNamed(child))
            {
                var stub = tree.CreateItem(row);
                stub.SetText(0, "…");
                stub.SetSelectable(0, false);
                row.Collapsed = true;
                _stubs.Add(row.GetInstanceId());
            }
        }
        if (kids.Count > shown)
        {
            var more = tree.CreateItem(item);
            more.SetText(0, $"… {kids.Count - shown} more, use the search box");
            more.SetCustomColor(0, Dim);
            more.SetSelectable(0, false);
        }
    }

    private void Bind(TreeItem item, Node3D node)
    {
        _itemNode[item.GetInstanceId()] = node;
        _nodeItem[node.GetInstanceId()] = item;
    }

    // Every named node in the world, flattened once. The world tree is not added to after the
    // build (the anim runtime's own find cache rests on the same fact), so one pass serves every
    // keystroke; freed nodes are skipped at query time rather than invalidating the index.
    private void EnsureNameIndex()
    {
        if (_nameIndex != null)
        {
            return;
        }
        _nameIndex = new List<(string, Node3D)>();
        void Walk(Node n)
        {
            if (n is Node3D named && named.HasMeta(AnimRuntime.NameMeta))
            {
                _nameIndex.Add((SelectionService.NameOf(named), named));
            }
            foreach (var child in n.GetChildren())
            {
                Walk(child);
            }
        }
        Walk(_world);
        foreach (var extra in ExtraRoots)
        {
            if (IsInstanceValid(extra))
            {
                Walk(extra);
            }
        }
        _nameIndex.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Log.Debug("ui", $"nodelab name index nodes={_nameIndex.Count}");
    }

    // ---- destructibles view -------------------------------------------------------------------

    private void BuildDestructibleRows()
    {
        var tree = _tree!;
        tree.Columns = 4;
        tree.ColumnTitlesVisible = true;
        tree.SetColumnTitle(0, "destructible");
        tree.SetColumnTitle(1, "root");
        tree.SetColumnTitle(2, "events");
        tree.SetColumnTitle(3, "hp");
        tree.SetColumnExpandRatio(0, 5);
        tree.SetColumnExpandRatio(1, 2);
        tree.SetColumnExpandRatio(2, 2);
        tree.SetColumnExpandRatio(3, 3);
        tree.HideRoot = false;

        _rootItem = tree.CreateItem();
        _rootItem.SetSelectable(0, false);
        if (_runtime == null || _program == null)
        {
            _rootItem.SetText(0, "no anim runtime in this session");
            _rootItem.SetCustomColor(0, Loud);
            return;
        }
        var rows = DestructibleRows();
        var totals = DestructibleTotals();
        // The totals and the warnings run across the whole row (SetExpandRight) rather than into a
        // 90 px column, because a truncated "131 unres…" is exactly the loud fact being muffled.
        Banner(_rootItem, $"defs {totals.Defs} · instances {totals.Instances} · node groups {totals.Anchors} · {totals.Unresolved} unresolved def(s)",
            totals.Unresolved > 0 ? Loud : Amber);
        if (_runtime.SuppressRootLift)
        {
            Banner(tree.CreateItem(_rootItem),
                "PARTIAL WORLD: these are the slice's destructibles, not the chapter's", Loud);
            foreach (string line in _runtime.ResolutionLines())
            {
                Banner(tree.CreateItem(_rootItem), line, Loud);
            }
        }
        foreach (var row in rows)
        {
            var item = tree.CreateItem(_rootItem);
            // The warning sign rides the name column, which is the widest: the `hp` cell's
            // "UNRESOLVED" clips at a narrow panel and the loud fact must not clip with it.
            item.SetText(0, row.Instances.Count == 0 ? "⚠ " + row.Label : row.Label);
            item.SetText(1, row.RootColumn);
            item.SetText(2, row.EventColumn);
            item.SetText(3, row.HpColumn);
            item.SetTooltipText(0, $"{row.Label}\nroot {row.RootColumn}\nevents {row.EventColumn}\nhp {row.HpColumn}");
            item.SetSelectable(0, false);
            if (row.Instances.Count == 0)
            {
                item.SetCustomColor(0, Loud);
                item.SetCustomColor(3, Loud);
            }
            if (!row.RootOk)
            {
                item.SetCustomColor(1, Loud);
            }
            if (!row.EventsOk)
            {
                item.SetCustomColor(2, Loud);
            }
            foreach (var inst in row.Instances)
            {
                var sub = tree.CreateItem(item);
                sub.SetText(0, SelectionService.NameOf(inst.Anchor));
                sub.SetText(1, RootColumnFor(row.Def, inst.Anchor, out bool ok));
                sub.SetText(3, $"{inst.Health:0.#}/{inst.MaxHealth:0.#} {inst.Status}");
                if (!ok)
                {
                    sub.SetCustomColor(1, Loud);
                }
                Bind(sub, inst.Anchor);
            }
            item.Collapsed = true;
        }
    }

    private List<DestRow> DestructibleRows()
    {
        var rows = new List<DestRow>();
        if (_runtime == null || _program == null)
        {
            return rows;
        }
        var byDef = new Dictionary<AnimDefinition, List<DestructibleRegistry.Instance>>();
        foreach (var inst in _runtime.Destructibles.All)
        {
            if (!byDef.TryGetValue(inst.Def, out var list))
            {
                byDef[inst.Def] = list = new List<DestructibleRegistry.Instance>();
            }
            list.Add(inst);
        }
        foreach (var def in _program.Defs)
        {
            if (!def.Destructible)
            {
                continue;
            }
            var row = new DestRow
            {
                Def = def,
                Label = DefLabel(def),
                Instances = byDef.TryGetValue(def, out var list) ? list : new List<DestructibleRegistry.Instance>(),
            };
            if (def.RootName == null)
            {
                row.RootColumn = "-";
            }
            else if (row.Instances.Count == 0)
            {
                row.RootColumn = def.RootName;
                row.RootOk = false;
            }
            else
            {
                int resolved = row.Instances.Count(i => _runtime.FindNodes(def.RootName, i.Anchor).Count > 0);
                row.RootColumn = $"{resolved}/{row.Instances.Count}";
                row.RootOk = resolved == row.Instances.Count;
            }
            var (eventText, eventsOk) = EventCoverage(def);
            row.EventColumn = eventText;
            row.EventsOk = eventsOk;
            row.HpColumn = row.Instances.Count == 0
                ? "UNRESOLVED"
                : $"{row.Instances.Count}× {def.Health:0.#}";
            rows.Add(row);
        }
        rows.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    private (int Defs, int Instances, int Anchors, int Unresolved) DestructibleTotals()
    {
        if (_runtime == null || _program == null)
        {
            return (0, 0, 0, 0);
        }
        int defs = 0, unresolved = 0;
        var bound = new HashSet<AnimDefinition>();
        foreach (var inst in _runtime.Destructibles.All)
        {
            bound.Add(inst.Def);
        }
        foreach (var def in _program.Defs)
        {
            if (!def.Destructible)
            {
                continue;
            }
            defs++;
            if (!bound.Contains(def))
            {
                unresolved++;
            }
        }
        return (defs, _runtime.Destructibles.Count, _runtime.Destructibles.DistinctAnchors, unresolved);
    }

    private string RootColumnFor(AnimDefinition def, Node3D anchor, out bool ok)
    {
        if (def.RootName == null || _runtime == null)
        {
            ok = true;
            return "-";
        }
        int n = _runtime.FindNodes(def.RootName, anchor).Count;
        ok = n > 0;
        return ok ? $"{n} node(s)" : "NONE";
    }

    // ---- dependency readout -------------------------------------------------------------------

    private void RefreshDeps()
    {
        if (_deps == null)
        {
            return;
        }
        var sb = new System.Text.StringBuilder();
        foreach (string line in DependencyLines(_selection.Current))
        {
            bool loud = line.Contains("NOT BUILT", StringComparison.Ordinal)
                        || line.Contains("PARTIAL WORLD", StringComparison.Ordinal)
                        || line.Contains("UNRESOLVED", StringComparison.Ordinal);
            sb.Append(loud ? "[color=#ff6b6b]" : "");
            sb.Append(line.Replace("[", "[lb]", StringComparison.Ordinal));
            sb.AppendLine(loud ? "[/color]" : "");
        }
        _deps.Text = sb.ToString();
    }

    private void AddAnimLines(Node3D node, List<string> lines)
    {
        if (_runtime == null || _program == null)
        {
            lines.Add("anim NOT BUILT, this session has no animation runtime, so no definition can be attributed");
            return;
        }
        EnsureAnimIndex();
        var anchored = _defsByAnchor!.TryGetValue(node.GetInstanceId(), out var byAnchor)
            ? byAnchor : new List<AnimDefinition>();
        var named = new List<AnimDefinition>();
        foreach (string key in NameKeys(node))
        {
            if (_defsByTarget!.TryGetValue(key, out var list))
            {
                foreach (var def in list)
                {
                    if (!anchored.Contains(def) && !named.Contains(def))
                    {
                        named.Add(def);
                    }
                }
            }
        }
        lines.Add(Log.Format($"anim defs anchored_here={anchored.Count} naming_this_node={named.Count}"));
        if (_runtime.SuppressRootLift)
        {
            lines.Add("anim PARTIAL WORLD, this is a --node= slice: the root-name lift is refused here, so a definition the full chapter binds may be missing from this list");
        }
        foreach (var def in anchored.Take(MaxListed))
        {
            lines.Add(DefLine(def, "anchor"));
        }
        if (anchored.Count > MaxListed)
        {
            lines.Add($"anim … {anchored.Count - MaxListed} more anchored here");
        }
        foreach (var def in named.Take(MaxListed))
        {
            lines.Add(DefLine(def, "names"));
        }
        if (named.Count > MaxListed)
        {
            lines.Add($"anim … {named.Count - MaxListed} more naming this node");
        }
    }

    private void AddDestructibleLines(Node3D node, List<string> lines)
    {
        if (_runtime == null)
        {
            return;
        }
        var registry = _runtime.Destructibles;
        var here = registry.PoolsOn(node);
        var resolved = registry.Resolve(node);
        if (here.Count == 0)
        {
            lines.Add(resolved == null
                ? "destructible none, nothing up this node's parent chain is a destructible"
                : Log.Format($"destructible none on this node, the enclosing '{SelectionService.NameOf(resolved.Anchor)}' is the pool a hit here would damage"));
            return;
        }
        foreach (var inst in here)
        {
            bool authoritative = ReferenceEquals(resolved, inst);
            lines.Add(Log.Format($"destructible pool def={DefLabel(inst.Def)} hp={inst.Health:0.#}/{inst.MaxHealth:0.#} state={inst.Status} stage={inst.DamageStage} authoritative={authoritative} source={(inst.Def.Archive != null ? "compiled" : "reader")}"));
            var damage = inst.Def.Sequences.FirstOrDefault(s =>
                s.Name.Equals("DAMAGE_SEQUENCE", StringComparison.OrdinalIgnoreCase));
            lines.Add(damage == null
                ? Log.Format($"destructible DAMAGE_SEQUENCE absent def={DefLabel(inst.Def)}, damage escalates no stages on this pool")
                : Log.Format($"destructible DAMAGE_SEQUENCE def={DefLabel(inst.Def)} events={damage.Events.Count} thresholds={CountThresholds(damage)}"));
            var (coverage, ok) = EventCoverage(inst.Def);
            lines.Add(Log.Format($"destructible event coverage def={DefLabel(inst.Def)} {(ok ? "" : "UNRESOLVED kinds ")}{coverage}"));
        }
    }

    private void AddGeometryLines(Node3D node, List<string> lines)
    {
        int meshes = 0, surfaces = 0;
        var textures = new List<string>();
        var materials = new HashSet<ulong>();
        EnsureTextureNames();
        void Walk(Node n)
        {
            if (n is MeshInstance3D { Mesh: { } mesh } mi)
            {
                meshes++;
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                {
                    surfaces++;
                    var mat = mi.GetActiveMaterial(s);
                    if (mat == null || !materials.Add(mat.GetInstanceId()))
                    {
                        continue;
                    }
                    if (_textureOfMaterial!.TryGetValue(mat.GetInstanceId(), out string? texName))
                    {
                        if (!textures.Contains(texName))
                        {
                            textures.Add(texName);
                        }
                    }
                    else if (!textures.Contains(mat.GetType().Name))
                    {
                        textures.Add(mat.GetType().Name);
                    }
                }
            }
            foreach (var child in n.GetChildren())
            {
                Walk(child);
            }
        }
        Walk(node);
        string listed = textures.Count == 0
            ? "-"
            : string.Join(", ", textures.Take(MaxListed))
              + (textures.Count > MaxListed ? $", … +{textures.Count - MaxListed}" : "");
        lines.Add(Log.Format($"geometry meshes={meshes} surfaces={surfaces} materials={materials.Count} textures={listed}"));
        if (_scene == null && meshes > 0)
        {
            lines.Add("geometry texture names unavailable, this session kept no SceneBuilder, so materials are named by type only");
        }
    }

    private void AddColliderLines(Node3D node, List<string> lines)
    {
        if (!_collisionBuilt)
        {
            lines.Add("colliders NOT BUILT IN THIS MODE, --freecam/--anim-lab build the world with no collision at all, so an empty list here would be the missing instrument, not missing colliders (verification WORLD-9)");
            return;
        }
        int bodies = 0, on = 0, off = 0;
        void Walk(Node n)
        {
            if (n is StaticBody3D)
            {
                bodies++;
            }
            if (n is CollisionShape3D shape)
            {
                if (shape.Disabled)
                {
                    off++;
                }
                else
                {
                    on++;
                }
            }
            foreach (var child in n.GetChildren())
            {
                Walk(child);
            }
        }
        Walk(node);
        lines.Add(Log.Format($"colliders bodies={bodies} shapes_enabled={on} shapes_disabled={off}"));
    }

    // ---- lazy indexes -------------------------------------------------------------------------

    // Which definitions touch which node, built once on the first readout. Anchor resolution is
    // the runtime's own, so the answer is the one the bootstrap actually used; the name map covers
    // definitions that anchor elsewhere and reach in by name.
    private void EnsureAnimIndex()
    {
        if (_defsByAnchor != null || _runtime == null || _program == null)
        {
            return;
        }
        _defsByAnchor = new Dictionary<ulong, List<AnimDefinition>>();
        _defsByTarget = new Dictionary<string, List<AnimDefinition>>(StringComparer.OrdinalIgnoreCase);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var def in _program.Defs)
        {
            foreach (var anchor in _runtime.AnchorsOf(def))
            {
                if (anchor == null || !IsInstanceValid(anchor))
                {
                    continue;
                }
                ulong id = anchor.GetInstanceId();
                if (!_defsByAnchor.TryGetValue(id, out var list))
                {
                    _defsByAnchor[id] = list = new List<AnimDefinition>();
                }
                if (!list.Contains(def))
                {
                    list.Add(def);
                }
            }
            foreach (string name in TargetNames(def))
            {
                if (!_defsByTarget.TryGetValue(name, out var list))
                {
                    _defsByTarget[name] = list = new List<AnimDefinition>();
                }
                if (!list.Contains(def))
                {
                    list.Add(def);
                }
            }
        }
        Log.Debug("ui", $"nodelab anim index defs={_program.Defs.Count} anchored_nodes={_defsByAnchor.Count} named_targets={_defsByTarget.Count} ms={watch.Elapsed.TotalMilliseconds:0.0}");
    }

    private void EnsureTextureNames()
    {
        if (_textureOfMaterial != null)
        {
            return;
        }
        _textureOfMaterial = new Dictionary<ulong, string>();
        if (_scene == null)
        {
            return;
        }
        foreach (var (material, texture) in _scene.TexturedMaterials)
        {
            _textureOfMaterial[material.GetInstanceId()] = texture;
        }
    }

    // ---- scripted dump ------------------------------------------------------------------------

    private void RunDebugDump()
    {
        var tokens = (DebugSpec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? wanted = null;
        bool deps = false, dest = false, openOnly = false;
        foreach (string token in tokens)
        {
            if (token.StartsWith("node=", StringComparison.OrdinalIgnoreCase))
            {
                wanted = token["node=".Length..];
            }
            else if (token.Equals("deps", StringComparison.OrdinalIgnoreCase))
            {
                deps = true;
            }
            else if (token.Equals("dest", StringComparison.OrdinalIgnoreCase))
            {
                dest = true;
            }
            else if (token.Equals("open", StringComparison.OrdinalIgnoreCase))
            {
                openOnly = true;
            }
        }
        // Asking for the destructibles by name also switches the panel to that view, so a capture
        // shows what the dump reports; the default (both readouts) leaves the tree showing.
        if (dest && _destBtn != null)
        {
            _destBtn.ButtonPressed = true;
        }
        // `open` is the perf/capture shape: show the panel, log nothing more. Naming any readout
        // beats it, so `open,deps` is still a dump.
        if (!deps && !dest && !openOnly)
        {
            deps = dest = true;
        }
        Log.Info("ui", $"nodelab debug spec={(DebugSpec is { Length: > 0 } s ? s : "all")} deps={deps} destructibles={dest} collision_built={_collisionBuilt}");
        if (wanted != null)
        {
            SelectByName(wanted);
        }
        if (deps)
        {
            foreach (string line in DependencyLines(_selection.Current))
            {
                Log.Info("ui", $"nodelab deps {line}");
            }
        }
        if (!dest)
        {
            return;
        }
        var totals = DestructibleTotals();
        Log.Info("ui", $"nodelab destructibles defs={totals.Defs} instances={totals.Instances} node_groups={totals.Anchors} unresolved_defs={totals.Unresolved}");
        if (_runtime is { SuppressRootLift: true })
        {
            Log.Warn("ui", $"nodelab destructibles PARTIAL WORLD, a --node= slice refuses the root-name lift, so this list is the slice's own destructibles and NOT the chapter's census");
            foreach (string line in _runtime.ResolutionLines())
            {
                Log.Warn("ui", $"nodelab destructibles {line}");
            }
        }
        int listed = 0, unlisted = 0, skippedResolved = 0, skippedUnresolved = 0;
        foreach (var row in DestructibleRows())
        {
            bool unresolved = row.Instances.Count == 0;
            if (unresolved ? unlisted >= MaxUnresolvedListed : listed >= MaxListed * 2)
            {
                if (unresolved)
                {
                    skippedUnresolved++;
                }
                else
                {
                    skippedResolved++;
                }
                continue;
            }
            if (unresolved)
            {
                unlisted++;
            }
            else
            {
                listed++;
            }
            Log.Info("ui", $"nodelab destructible def={row.Label} instances={row.Instances.Count} root={row.RootColumn} root_ok={row.RootOk} events={row.EventColumn} events_ok={row.EventsOk} hp={row.HpColumn}");
        }
        if (skippedResolved + skippedUnresolved > 0)
        {
            Log.Info("ui", $"nodelab destructible … elided resolved={skippedResolved} unresolved={skippedUnresolved}, the panel's list is complete");
        }
    }

    // One destructible definition's row: how many world node groups it actually bound,
    // whether its `ANIMATION_ROOT_NAME` resolves inside each of them, and whether its
    // sequences use only event kinds the runtime acts on.
    private sealed class DestRow
    {
        public AnimDefinition Def = null!;
        public string Label = "";
        public string RootColumn = "";
        public string EventColumn = "";
        public string HpColumn = "";
        public bool RootOk = true;
        public bool EventsOk = true;
        public List<DestructibleRegistry.Instance> Instances = new();
    }
}
