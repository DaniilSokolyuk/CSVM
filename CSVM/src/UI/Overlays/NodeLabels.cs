using System;
using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Overlays;

/// <summary>
/// Floating node-name labels over the built scene, available in the static viewer and in flight:
/// the fastest way to identify an object at a wrong position is to read its name off it.
/// Names come from the <c>cs_name</c> meta SceneBuilder stamps on every node, not
/// <c>Node.Name</c>, which Godot sanitises and auto-renames on duplicate siblings. Labels are
/// limited to <see cref="MaxLabels"/> nearest the camera within <see cref="Radius"/>, recomputed
/// on a timer since the tree changes in flight as MapEdgeExtender adds and drops border tiles.
/// Off unless <c>--debug-names</c> asks for a mode, which is the only way in: this is a viewer
/// instrument and holds no key. Modes are Meshes (nodes that draw something) and All (every node).
/// Full decode, including the splitscreen selection trap: docs/architecture.md.
/// </summary>
public sealed partial class NodeLabels : Node
{
    // Selection cadence. Positions are world-static and Label3D billboards itself, so
    // recomputing a few times a second is indistinguishable from per-frame. TUNE.
    private const double RefreshInterval = 0.35;

    // Minimum on-screen separation between two labels. Wider than tall because a name is a
    // wide, short box, a square grid either overlaps horizontally or throws away far too
    // many rows. Both TUNE.
    private const float GapX = 108f;
    private const float GapY = 26f;

    private readonly Node3D _root;
    private readonly Camera3D _camera;
    private readonly List<Label3D> _pool = new();
    private readonly List<(Node3D Node, string Name, bool Deprio, Vector3 Anchor)> _candidates = new();
    private readonly List<(float Rank, Vector3 Pos, string Name)> _picked = new();
    private readonly HashSet<long> _occupied = new();

    private Node3D? _holder;
    private CanvasLayer? _hudLayer;
    private Label? _hud;
    private Mode _mode = Mode.Off;
    private double _sinceRefresh = 1e9;

    // In splitscreen, GameSession passes rig 0's camera here: the nearest/de-clutter pick is
    // P1's viewpoint alone. The Label3D nodes built from it are ordinary world-space children of
    // the root, so every pane's own camera still renders them regardless.
    public NodeLabels(Node3D root, Camera3D camera)
    {
        _root = root;
        _camera = camera;
        Name = "node_labels";
    }

    public enum Mode { Off, Meshes, All }

    /// <summary>How far from the camera a node may be and still get a label. This is a
    /// performance bound, not the readability one, screen-space de-cluttering is what keeps
    /// the view legible, so it can be generous. It has to be: in flight the camera sits
    /// several hundred metres up, and at the 300 m of the first cut the ground was out of
    /// range entirely and only the player's own aircraft got labelled.</summary>
    public float Radius { get; init; } = 1500f;

    /// <summary>Subtrees whose nodes sort *after* everything else, so they claim screen space
    /// only where nothing else wants it. GameSession passes the player aircraft in flight:
    /// it is always the nearest thing to the camera by a wide margin, so nearest-first would
    /// otherwise spend every label on the plane you are sitting in while the world you are
    /// actually inspecting goes unnamed. Deprioritised, not excluded, a misplaced node can
    /// be on the aircraft too, and the labels still appear once the world runs out of room.</summary>
    public IReadOnlyList<Node3D>? Deprioritise { get; init; }

    /// <summary>Cap on simultaneously drawn labels, nearest first.</summary>
    public int MaxLabels { get; init; } = 200;

    /// <summary>--debug-names[=meshes|all]: start switched on, for scripted screenshots.</summary>
    public Mode InitialMode { get; init; } = Mode.Off;

    /// <summary>Parses the --debug-names value. Absent value = Meshes, the useful default
    /// (All includes every empty group node and is mostly noise on a first look).</summary>
    public static Mode ParseMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "all" => Mode.All,
        "off" => Mode.Off,
        _ => Mode.Meshes,
    };

    public override void _Ready()
    {
        if (InitialMode != Mode.Off)
            SetMode(InitialMode);
    }

    public override void _Process(double delta)
    {
        if (_mode == Mode.Off)
            return;
        _sinceRefresh += delta;
        if (_sinceRefresh < RefreshInterval)
            return;
        _sinceRefresh = 0;
        Rescan();
        Refresh();
    }

    private static bool HasMesh(Node3D n)
    {
        foreach (var c in n.GetChildren())
            if (c is MeshInstance3D)
                return true;
        return false;
    }

    // ⚠ Anchor at the mesh-AABB centre in node-local space, never the node origin. Computed once
    // per scan and transformed per refresh, so it follows a node that moves (aircraft parts)
    // without being recomputed.
    private static Vector3 LocalAnchor(Node3D n)
    {
        var sum = Vector3.Zero;
        int count = 0;
        foreach (var c in n.GetChildren())
            if (c is MeshInstance3D { Mesh: not null } mi)
            {
                sum += mi.Transform * mi.GetAabb().GetCenter();
                count++;
            }
        return count > 0 ? sum / count : Vector3.Zero;
    }

    private static long CellKey(int x, int y) => ((long)x << 32) ^ (uint)y;

    private void SetMode(Mode mode)
    {
        _mode = mode;
        _sinceRefresh = 1e9; // refresh on the next frame rather than waiting out the timer
        if (_mode == Mode.Off)
        {
            foreach (var l in _pool)
                l.Visible = false;
            if (_hudLayer != null)
                _hudLayer.Visible = false;
            return;
        }
        EnsureBuilt();
        _hudLayer!.Visible = true;
        Log.Info("ui", $"[names] node labels: {_mode}");
    }

    private void EnsureBuilt()
    {
        // Built lazily on first use: an unadorned viewer/flight session that never presses T
        // adds no nodes at all, so nothing it renders can differ.
        _holder ??= CreateHolder();
        if (_hudLayer != null)
            return;
        _hudLayer = new CanvasLayer { Layer = HudLayers.Debug };
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _hud = new Label { Text = "" };
        _hud.AddThemeFontSizeOverride("font_size", 12);
        _hud.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        _hud.GrowHorizontal = Control.GrowDirection.Begin;
        _hud.GrowVertical = Control.GrowDirection.Begin;
        _hud.OffsetRight = -10;
        _hud.OffsetBottom = -8;
        root.AddChild(_hud);
        _hudLayer.AddChild(root);
        AddChild(_hudLayer);
    }

    private Node3D CreateHolder()
    {
        var h = new Node3D { Name = "node_label_markers" };
        _root.AddChild(h);
        return h;
    }

    // Re-walks the tree for labellable nodes. Redone periodically because the flight
    // scene is not static, MapEdgeExtender adds and removes border tiles on cell crossings,
    // and a one-shot walk would label ghosts and miss new ground.
    private void Rescan()
    {
        _candidates.Clear();
        Walk(_root, false);
    }

    private void Walk(Node node, bool deprio)
    {
        foreach (var child in node.GetChildren())
        {
            // Never label our own overlays (the marker holder, the mesh lab's draw nodes),
            // they carry no cs_name anyway, but skipping the subtree saves the descent.
            if (ReferenceEquals(child, _holder) || child.Name.ToString().StartsWith("mesh_lab_"))
                continue;
            bool childDeprio = deprio || IsDeprioritised(child);
            if (child is Node3D n3d && n3d.HasMeta(AnimRuntime.NameMeta)
                && (_mode == Mode.All || HasMesh(n3d)))
                _candidates.Add((n3d, n3d.GetMeta(AnimRuntime.NameMeta).AsString(),
                    childDeprio, LocalAnchor(n3d)));
            Walk(child, childDeprio);
        }
    }

    private bool IsDeprioritised(Node node)
    {
        if (Deprioritise == null)
            return false;
        foreach (var d in Deprioritise)
            if (ReferenceEquals(d, node))
                return true;
        return false;
    }

    private void Refresh()
    {
        var eye = _camera.GlobalPosition;
        float maxSq = Radius * Radius;
        _picked.Clear();
        foreach (var (node, name, deprio, anchor) in _candidates)
        {
            if (!node.IsInsideTree())
                continue;
            var pos = node.GlobalTransform * anchor;
            float d = eye.DistanceSquaredTo(pos);
            if (d <= maxSq)
                // Deprioritised subtrees rank behind every in-range node rather than being
                // dropped: a penalty larger than any admissible distance orders them last.
                _picked.Add((deprio ? d + maxSq : d, pos, name));
        }
        _picked.Sort(static (a, b) => a.Rank.CompareTo(b.Rank));

        // Screen-space de-cluttering, nearest-first, or the labels stack into an unreadable
        // pile. A node loses its label only to something closer to the camera.
        _occupied.Clear();
        int shown = 0, hidden = 0;
        var view = _camera.GetViewport().GetVisibleRect().Grow(GapX);
        foreach (var (_, world, name) in _picked)
        {
            // Behind the camera unprojects mirrored, which would claim a bogus screen cell.
            if (_camera.IsPositionBehind(world))
                continue;
            var screen = _camera.UnprojectPosition(world);
            // Off-screen nodes would otherwise claim cells and burn label slots on text
            // nobody can see.
            if (!view.HasPoint(screen))
                continue;
            if (shown >= MaxLabels || !Claim(screen))
            {
                hidden++;
                continue;
            }
            var label = LabelAt(shown++);
            label.Text = name;
            label.GlobalPosition = world;
            label.Visible = true;
        }
        for (int i = shown; i < _pool.Count; i++)
            _pool[i].Visible = false;

        if (_hud != null)
            _hud.Text = $"node labels: {_mode}, {shown} shown"
                        + (hidden > 0 ? $", {hidden} hidden (overlap / cap {MaxLabels})" : "")
                        + $" within {Radius:0} m";
    }

    // Reserves this label's screen cell, or reports the spot as already taken.
    // Checking the 3×3 neighbourhood is what guarantees a real minimum gap; testing only the
    // own cell would happily place two labels a pixel apart across a cell boundary.
    private bool Claim(Vector2 screen)
    {
        int cx = Mathf.FloorToInt(screen.X / GapX), cy = Mathf.FloorToInt(screen.Y / GapY);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (_occupied.Contains(CellKey(cx + dx, cy + dy)))
                    return false;
        _occupied.Add(CellKey(cx, cy));
        return true;
    }

    private Label3D LabelAt(int i)
    {
        while (_pool.Count <= i)
        {
            var l = new Label3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                // Constant on-screen size: a name is either readable or useless, and one that
                // shrinks with distance is useless exactly when you are hunting for it.
                FixedSize = true,
                PixelSize = 0.0007f,
                FontSize = 48,
                OutlineSize = 14,
                // Drawn through geometry on purpose, half the job is spotting a node that has
                // ended up inside or behind something it should not be.
                NoDepthTest = true,
                Modulate = new Color(1f, 0.93f, 0.35f),
                OutlineModulate = new Color(0f, 0f, 0f, 0.85f),
                RenderPriority = 20,
                OutlineRenderPriority = 19,
                Visible = false,
            };
            _holder!.AddChild(l);
            _pool.Add(l);
        }
        return _pool[i];
    }
}
