using System;
using System.Collections.Generic;
using System.Globalization;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The shared world selection in <c>--freecam</c> and <c>--anim-lab</c>: click any object to pick
/// the mesh under the cursor, then PgUp/PgDn walk the <c>cs_name</c> ancestor ladder from that
/// leaf up to the placed world object it belongs to. A breadcrumb HUD line names every rung and a
/// wireframe box outlines the current one. Every other inspect tool reads this state rather than
/// picking for itself. Ladder construction: this module's entry in docs/architecture.md.
/// ⚠ A manual scan, not a physics raycast (neither mode builds collision): objects by AABB, so a
/// click just off a thin one can take it; map-scale meshes (terrain) by triangle, occluding.
/// Unreachable in splitscreen by construction: <c>--freecam</c>/<c>--anim-lab</c> are not
/// <c>Fly</c>, and <c>SessionSpec.Resolve</c> clamps players back to 1 whenever more than one
/// joined a non-flight mode, so this single-camera pick never has a second pane to be wrong about.
/// </summary>
public sealed partial class SelectionService : Node
{
    /// <summary>The largest world-space AABB diagonal picked by box. A terrain tile is a 1024 m cell
    /// whose box covers the air above its valleys, so a box test would take the tile over the
    /// object standing on it; meshes at or over this are tested against their triangles instead.
    /// Buildings, vehicles and animated props are all well under it. TUNE.</summary>
    public const float MaxPickDiag = 350f;

    /// <summary>Node metadata marking a subtree as another tool's DRAWING rather than world content
    /// (<see cref="SubtreeBounds.OverlayMeta"/>). The pick and the box measurement both skip anything
    /// carrying it. A debug overlay parented onto the object it annotates is therefore never selectable
    /// and never grows the next box measured over it.</summary>
    public const string OverlayMeta = SubtreeBounds.OverlayMeta;

    // Smallest edge the highlight box is drawn at, so a meshless pivot rung is still visible. The
    // MEASURED box is what CurrentBox and the log report, only the drawing is grown.
    private const float MinHighlightEdge = 2f;

    // How many rungs the breadcrumb prints before it elides the middle. The LOG is never elided,
    // a truncated list that drops the rung under test is worse than a long one.
    private const int MaxCrumbs = 9;

    // The breadcrumb's text conventions follow NodeLabels: the same amber as a floating node name,
    // because this is the same question ("what is that thing called").
    private static readonly Color Amber = new(1f, 0.93f, 0.35f);

    private readonly Node3D _world;
    private readonly Camera3D _camera;
    private readonly List<Node3D> _ladder = new();

    private CanvasLayer? _hudLayer;
    private Label? _hud;
    private MeshInstance3D? _highlight;
    private bool _debugDone;

    public SelectionService(Node3D world, Camera3D camera)
    {
        _world = world;
        _camera = camera;
        Name = "selection";
    }

    /// <summary>Fires whenever the selection changes. The flag is true for a fresh pick and false
    /// for a ladder walk, which is the difference between "show me this" and "same object, wider
    /// scope", the anim lab re-frames the camera on the first and only re-follows on the
    /// second.</summary>
    public event Action<SelectionService, bool>? Changed;

    /// <summary>Fires when a pick lands with Ctrl held, carrying the node it selected. Ctrl is
    /// otherwise only the camera's slow modifier, so it is free for a tool to claim a second
    /// meaning on a click; the node lab's export set is the tool that does.</summary>
    public event Action<Node3D>? CtrlPicked;

    /// <summary><c>--debug-select=x,y[,up]</c>: a synthetic click at a screen position on the first
    /// frame, optionally followed by that many <see cref="StepUp"/>s, the scripted stand-in for
    /// the click and the PgUp presses, which are not scriptable here. Null screen position means
    /// the middle of the viewport.</summary>
    public (Vector2? Screen, int Up)? DebugPick { get; init; }

    /// <summary>Extra pickable subtrees walked in addition to the world content root, props parked
    /// beside it rather than under it (the anim lab's <c>--plane=</c> stage prop). Read live at pick
    /// time, so the owner may populate it after construction. Each also caps its own ancestor ladder,
    /// so a pick inside it walks up to that root and no further.</summary>
    public IReadOnlyList<Node3D> ExtraRoots { get; init; } = Array.Empty<Node3D>();

    /// <summary>Extra text a tool appends to the breadcrumb block, read on every HUD update so the
    /// supplier may attach after this service is built (the node lab's export set is the one that
    /// does). Empty string for nothing to add; <see cref="RefreshHud"/> re-renders it.</summary>
    public Func<string>? HudLine { get; set; }

    /// <summary>The ladder, leaf first: the struck mesh's <c>cs_name</c>-bearing ancestors up to
    /// (not including) the world content root. Empty when nothing is selected.</summary>
    public IReadOnlyList<Node3D> Ladder => _ladder;

    /// <summary>Which rung is current, 0 is the struck leaf, <c>Ladder.Count - 1</c> the outermost
    /// placed object. PgUp walks toward the root, PgDn back toward the leaf, Home/End jump to the
    /// ends.</summary>
    public int Level { get; private set; }

    /// <summary>The selected node: the ladder's current rung, or null when nothing is selected.</summary>
    public Node3D? Current => _ladder.Count > 0 && Level < _ladder.Count ? _ladder[Level] : null;

    /// <summary>The current rung's subtree bounding box in <b>world</b> space, measured from the
    /// subtree's own meshes at selection time. Zero-size when the rung draws nothing itself and has
    /// no drawing descendants.</summary>
    public Aabb CurrentBox { get; private set; }

    /// <summary>The game-file name of a built node, the <c>cs_name</c> meta, never
    /// <c>Node.Name</c>, which Godot sanitises and auto-renames.</summary>
    public static string NameOf(Node3D n) =>
        n.HasMeta(AnimRuntime.NameMeta) ? n.GetMeta(AnimRuntime.NameMeta).AsString() : n.Name.ToString();

    // ---- geometry -----------------------------------------------------------------------------

    /// <summary>World-frame union of a subtree's own mesh AABBs (<see cref="SubtreeBounds.WorldAabb"/>).
    /// Public because every inspect tool wants this measurement and none of them may take the
    /// merge-over-the-live-tree shortcut.</summary>
    public static Aabb SubtreeWorldAabb(Node3D root) => SubtreeBounds.WorldAabb(root);

    /// <summary>Parses <c>--debug-select[=x,y[,up]]</c>. An empty value means the middle of the
    /// viewport with no ladder walk; a malformed one is reported and ignored rather than throwing
    /// the session away.</summary>
    public static (Vector2? Screen, int Up)? ParseDebugPick(string spec)
    {
        string text = spec.Trim();
        if (text.Length == 0)
        {
            return (null, 0);
        }
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
        {
            Log.Warn("ui", $"--debug-select='{spec}' is not x,y[,up], ignoring it");
            return null;
        }
        int up = 0;
        if (parts.Length == 3
            && !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out up))
        {
            Log.Warn("ui", $"--debug-select='{spec}' has a non-integer rung count, ignoring the walk");
            up = 0;
        }
        return (new Vector2(x, y), up);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb)
        {
            // A click in the 3D area releases whatever text field had focus (Godot does not
            // defocus a LineEdit on a click into empty space, which left the camera keys dead
            // after typing in the anim lab's filter), then picks.
            GetViewport().GuiReleaseFocus();
            if (PickAt(mb.Position) && mb.CtrlPressed && Current is { } picked)
            {
                CtrlPicked?.Invoke(picked);
            }
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }
        switch (key.Keycode)
        {
            case Key.Pageup:
                StepUp(1);
                break;
            case Key.Pagedown:
                StepUp(-1);
                break;
            // A real ladder is deeper than the two presses the complaint imagined, C1's moored
            // zeppelin is nine rungs from a motor's mesh to hk_zep, so both ends are one key away.
            case Key.Home:
                StepUp(_ladder.Count);
                break;
            case Key.End:
                StepUp(-_ladder.Count);
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        if (!_debugDone)
        {
            // Deferred to the first frame: the camera pose and the world subtree are only final
            // once the session has been built and added to the tree.
            _debugDone = true;
            RunDebugPick();
        }
        if (_highlight == null)
        {
            return;
        }
        if (Current is { } node && IsInstanceValid(node))
        {
            // The box mesh is built in the rung's own frame, so riding its transform tracks a
            // moving object (train, zeppelin) exactly and costs nothing per frame.
            _highlight.GlobalTransform = node.GlobalTransform;
        }
        else if (_ladder.Count > 0)
        {
            // The selected node was freed under us (a destructible swapping to its wreck).
            Clear();
        }
    }

    // ---- picking ------------------------------------------------------------------------------

    /// <summary>Casts a ray from the camera through a screen position and selects the nearest mesh
    /// it hits. Returns false, and says why, when nothing pickable is under the cursor.</summary>
    public bool PickAt(Vector2 screenPos)
    {
        var from = _camera.ProjectRayOrigin(screenPos);
        var dir = _camera.ProjectRayNormal(screenPos);
        Node3D? best = null;
        float bestT = float.MaxValue;
        int tested = 0;
        var mapScale = new List<MeshInstance3D>();
        void Walk(Node n)
        {
            if (n is Node3D overlay && overlay.HasMeta(OverlayMeta))
            {
                return; // a tool's drawing, not content: never pickable
            }
            if (n is MeshInstance3D { Mesh: { } mesh } mi && mi.IsVisibleInTree())
            {
                var aabb = mesh.GetAabb();
                var xf = mi.GlobalTransform;
                if ((xf.Basis * aabb.Size).Length() >= MaxPickDiag)
                {
                    mapScale.Add(mi);
                }
                else
                {
                    tested++;
                    // The parameter along the local ray equals the world distance (the inverse
                    // transform is affine and dir is unit), so it compares directly across nodes.
                    var inv = xf.AffineInverse();
                    if (RayAabb(inv * from, inv.Basis * dir, aabb, out float t) && t < bestT)
                    {
                        bestT = t;
                        best = mi;
                    }
                }
            }
            foreach (var child in n.GetChildren())
            {
                Walk(child);
            }
        }
        Walk(_world);
        foreach (var root in ExtraRoots)
        {
            if (IsInstanceValid(root))
            {
                Walk(root);
            }
        }
        // Map-scale meshes last, by triangle. A tile whose box the ray enters beyond the best hit
        // cannot win, which prunes almost every tile; a nearer triangle beats an object's box.
        int triTested = 0;
        bool mapScaleHit = false;
        foreach (var mi in mapScale)
        {
            var inv = mi.GlobalTransform.AffineInverse();
            var o = inv * from;
            var d = inv.Basis * dir;
            if (!RayAabb(o, d, mi.Mesh.GetAabb(), out float tBox) || tBox >= bestT)
            {
                continue;
            }
            triTested++;
            if (RayMesh(o, d, mi.Mesh, out float t) && t < bestT)
            {
                bestT = t;
                best = mi;
                mapScaleHit = true;
            }
        }
        if (best == null)
        {
            Log.Info("ui", $"select miss screen=({screenPos.X:0},{screenPos.Y:0}) tested={tested} map_scale={mapScale.Count} map_scale_tri_tested={triTested}");
            return false;
        }
        var at = from + dir * bestT;
        Log.Debug("ui", $"select hit screen=({screenPos.X:0},{screenPos.Y:0}) dist={bestT:0.0} at=({at.X:0.0},{at.Y:0.0},{at.Z:0.0}) tested={tested} map_scale={mapScale.Count} map_scale_tri_tested={triTested} map_scale_hit={mapScaleHit}");
        Select(best);
        return true;
    }

    /// <summary>Selects a node directly (a tree panel, a search result, a scripted probe): the
    /// ladder is rebuilt from it and the current rung reset to the leaf end.</summary>
    public void Select(Node3D node)
    {
        _ladder.Clear();
        Node? n = node;
        while (n != null && !ReferenceEquals(n, _world))
        {
            if (n is Node3D n3d && n3d.HasMeta(AnimRuntime.NameMeta))
            {
                _ladder.Add(n3d);
            }
            // An extra pick root is a ladder boundary too: it is a placed prop, not a rung under
            // the world content, so the walk includes it and stops rather than climbing out into
            // the session container above it.
            if (IsExtraRoot(n))
            {
                break;
            }
            n = n.GetParent();
        }
        if (_ladder.Count == 0)
        {
            Log.Warn("ui", $"select found no named ancestor above node='{node.Name}', nothing under the cursor carries a cs_name, so there is no ladder to walk");
            Clear();
            return;
        }
        Level = 0;
        Apply(fresh: true);
    }

    /// <summary>Walks the ladder: positive toward the world root, negative back toward the leaf.
    /// Clamped at both ends, and a clamped press says which end it hit.</summary>
    public void StepUp(int steps)
    {
        if (_ladder.Count == 0)
        {
            Log.Info("ui", $"select nothing selected, click an object first (steps={steps})");
            return;
        }
        int want = Level + steps;
        int next = Mathf.Clamp(want, 0, _ladder.Count - 1);
        if (next == Level)
        {
            Log.Info("ui", $"select already at the {(steps > 0 ? "outermost" : "innermost")} rung level={Level + 1}/{_ladder.Count} name={NameOf(_ladder[Level])}");
            return;
        }
        Level = next;
        Apply(fresh: false);
    }

    /// <summary>Drops the selection and hides its overlay (the nodes stay built, so re-selecting
    /// costs nothing).</summary>
    public void Clear()
    {
        _ladder.Clear();
        Level = 0;
        CurrentBox = default;
        if (_highlight != null)
        {
            _highlight.Visible = false;
        }
        if (_hudLayer != null)
        {
            _hudLayer.Visible = false;
        }
        Changed?.Invoke(this, false);
    }

    /// <summary>Re-renders the breadcrumb HUD, for a tool whose own state feeds
    /// <see cref="HudLine"/> and has just changed.</summary>
    public void RefreshHud() => UpdateHud();

    /// <summary>A wireframe box instance for an inspect overlay to draw into: unshaded, unfogged,
    /// depth-test off and hidden until something draws it. Shared rather than per-tool so every
    /// outline in these modes reads as the same instrument at a different colour.</summary>
    internal static MeshInstance3D NewBoxInstance(string name, Color color, int priority)
    {
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = color,
            VertexColorUseAsAlbedo = false,
            // Drawn through geometry: half the job is outlining an object you picked from the
            // far side of a hull, and the world's fog would otherwise fade the box out at range.
            NoDepthTest = true,
            DisableFog = true,
            RenderPriority = priority,
        };
        return new MeshInstance3D
        {
            Name = name,
            Mesh = new ImmediateMesh(),
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
    }

    /// <summary>Draws a world-space box into an instance in the node's own frame, so the drawing
    /// then rides that node's transform. A degenerate box is grown to a visible edge; the caller's
    /// measured box is untouched.</summary>
    internal static void DrawBox(MeshInstance3D target, Node3D node, Aabb box)
    {
        if (target.Mesh is not ImmediateMesh mesh)
        {
            return;
        }
        // Grow a degenerate box (a meshless pivot rung, a flat surface) to something visible. The
        // measured box is what CurrentBox and the log carry; only the drawing is padded.
        var size = box.Size;
        var center = box.GetCenter();
        for (int a = 0; a < 3; a++)
        {
            if (size[a] < MinHighlightEdge)
            {
                size[a] = MinHighlightEdge;
            }
        }
        var padded = new Aabb(center - size * 0.5f, size);
        // The corners are taken into the node's own frame, so the drawn box is exactly the world
        // box at selection time and then rides the node's transform.
        var inv = node.GlobalTransform.AffineInverse();
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = inv * (padded.Position + new Vector3(
                (i & 1) != 0 ? padded.Size.X : 0f,
                (i & 2) != 0 ? padded.Size.Y : 0f,
                (i & 4) != 0 ? padded.Size.Z : 0f));
        }
        mesh.ClearSurfaces();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        for (int i = 0; i < 8; i++)
        {
            for (int bit = 1; bit <= 4; bit <<= 1)
            {
                if ((i & bit) != 0)
                {
                    continue;
                }
                mesh.SurfaceAddVertex(corners[i]);
                mesh.SurfaceAddVertex(corners[i | bit]);
            }
        }
        mesh.SurfaceEnd();
        target.GlobalTransform = node.GlobalTransform;
        target.Visible = true;
    }

    // Slab test. Returns the near intersection parameter (>= 0) of the ray o + t·d with the box.
    private static bool RayAabb(Vector3 o, Vector3 d, Aabb box, out float tHit)
    {
        tHit = 0f;
        float tmin = 0f, tmax = float.MaxValue;
        Vector3 lo = box.Position, hi = box.End;
        for (int a = 0; a < 3; a++)
        {
            float da = d[a], oa = o[a];
            if (Mathf.Abs(da) < 1e-9f)
            {
                if (oa < lo[a] || oa > hi[a])
                {
                    return false;
                }
                continue;
            }
            float t1 = (lo[a] - oa) / da, t2 = (hi[a] - oa) / da;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }
            tmin = Mathf.Max(tmin, t1);
            tmax = Mathf.Min(tmax, t2);
            if (tmin > tmax)
            {
                return false;
            }
        }
        tHit = tmin;
        return tmax >= 0f;
    }

    // Nearest two-sided hit of the ray o + t·d against a mesh's triangle surfaces, in the mesh's own
    // frame. Allocates each surface's arrays, which a click can afford and a per-frame caller cannot.
    private static bool RayMesh(Vector3 o, Vector3 d, Mesh mesh, out float tHit)
    {
        tHit = float.MaxValue;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh is ArrayMesh array && array.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles)
            {
                continue;
            }
            var arrays = mesh.SurfaceGetArrays(s);
            var verts = arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
            var indices = arrays[(int)Mesh.ArrayType.Index].As<int[]>();
            bool indexed = indices.Length > 0;
            int count = indexed ? indices.Length : verts.Length;
            for (int i = 0; i + 2 < count; i += 3)
            {
                var a = verts[indexed ? indices[i] : i];
                var b = verts[indexed ? indices[i + 1] : i + 1];
                var c = verts[indexed ? indices[i + 2] : i + 2];
                if (RayTriangle(o, d, a, b, c, out float t) && t < tHit)
                {
                    tHit = t;
                }
            }
        }
        return tHit < float.MaxValue;
    }

    // Möller–Trumbore, both faces.
    private static bool RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        var e1 = b - a;
        var e2 = c - a;
        var p = d.Cross(e2);
        float det = e1.Dot(p);
        if (Mathf.Abs(det) < 1e-12f)
        {
            return false;
        }
        float invDet = 1f / det;
        var s = o - a;
        float u = s.Dot(p) * invDet;
        if (u < 0f || u > 1f)
        {
            return false;
        }
        var q = s.Cross(e1);
        float v = d.Dot(q) * invDet;
        if (v < 0f || u + v > 1f)
        {
            return false;
        }
        t = e2.Dot(q) * invDet;
        return t >= 0f;
    }

    private bool IsExtraRoot(Node n)
    {
        foreach (var root in ExtraRoots)
        {
            if (ReferenceEquals(root, n))
            {
                return true;
            }
        }
        return false;
    }

    private void Apply(bool fresh)
    {
        var node = _ladder[Level];
        CurrentBox = SubtreeWorldAabb(node);
        EnsureBuilt();
        DrawBox(_highlight!, node, CurrentBox);
        UpdateHud();
        LogLevel(fresh);
        Changed?.Invoke(this, fresh);
    }

    // ---- overlay ------------------------------------------------------------------------------

    // Built on the first selection, never before: an unadorned --freecam/--anim-lab session adds no
    // CanvasLayer and no mesh at all, so its capture is byte-identical to one without this service.
    private void EnsureBuilt()
    {
        if (_highlight == null)
        {
            _highlight = NewBoxInstance("selection_box", Amber, 20);
            // Parented to this service, NOT into the selected subtree, a highlight living inside
            // the thing it measures would grow the next box it measures.
            AddChild(_highlight);
        }
        if (_hudLayer != null)
        {
            _hudLayer.Visible = true;
            return;
        }
        _hudLayer = new CanvasLayer { Layer = HudLayers.Debug };
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _hud = new Label
        {
            // Below the freecam readout and the anim lab's two-line status, both at the top left.
            Position = new Vector2(12, 62),
            Modulate = Amber,
        };
        _hud.AddThemeFontSizeOverride("font_size", 13);
        root.AddChild(_hud);
        _hudLayer.AddChild(root);
        AddChild(_hudLayer);
    }

    private void UpdateHud()
    {
        if (_hud == null)
        {
            return;
        }
        var c = CurrentBox.GetCenter();
        var s = CurrentBox.Size;
        string extent = s.LengthSquared() < 1e-6f
            ? "no mesh of its own"
            : Log.Format($"centre ({c.X:0}, {c.Y:0}, {c.Z:0})  size {s.X:0.#} × {s.Y:0.#} × {s.Z:0.#} m");
        string extra = HudLine?.Invoke() ?? "";
        _hud.Text = Log.Format($"selection [click to pick · Ctrl+click add to set · PgUp/PgDn walk · Home/End jump]  rung {Level + 1}/{_ladder.Count}\n")
                    + Crumbs() + "\n" + extent + extra;
    }

    private string Crumbs()
    {
        var parts = new List<string>();
        bool elided = false;
        for (int i = 0; i < _ladder.Count; i++)
        {
            // Elide the middle, never the leaf, the current rung or the two outermost.
            bool keep = i == Level || i == 0 || i >= _ladder.Count - 2 || i < MaxCrumbs - 3;
            if (_ladder.Count > MaxCrumbs && !keep)
            {
                if (!elided)
                {
                    parts.Add("…");
                    elided = true;
                }
                continue;
            }
            elided = false;
            string name = NameOf(_ladder[i]);
            parts.Add(i == Level ? $"[{name}]" : name);
        }
        return string.Join(" < ", parts);
    }

    // The whole ladder, one line per rung with its world-frame box, the scripted instrument. Every
    // rung is printed on a fresh pick so one run answers "what did that click actually select"; a
    // ladder walk prints only the rung it moved to.
    private void LogLevel(bool fresh)
    {
        if (fresh)
        {
            Log.Info("ui", $"select ladder rungs={_ladder.Count} leaf={NameOf(_ladder[0])} outermost={NameOf(_ladder[^1])}");
            for (int i = 0; i < _ladder.Count; i++)
            {
                LogRung(i);
            }
            return;
        }
        LogRung(Level);
    }

    private void LogRung(int i)
    {
        var node = _ladder[i];
        var box = i == Level ? CurrentBox : SubtreeWorldAabb(node);
        var c = box.GetCenter();
        var s = box.Size;
        Log.Info("ui", $"select rung={i + 1}/{_ladder.Count}{(i == Level ? "*" : " ")} cs_name={NameOf(node)} godot={node.Name} centre=({c.X:0.0},{c.Y:0.0},{c.Z:0.0}) size=({s.X:0.0},{s.Y:0.0},{s.Z:0.0})");
    }

    private void RunDebugPick()
    {
        if (DebugPick is not { } request)
        {
            return;
        }
        var screen = request.Screen ?? GetViewport().GetVisibleRect().Size * 0.5f;
        var eye = _camera.GlobalPosition;
        var aim = -_camera.GlobalTransform.Basis.Z;
        Log.Info("ui", $"select debug-pick screen=({screen.X:0},{screen.Y:0}) up={request.Up} eye=({eye.X:0.0},{eye.Y:0.0},{eye.Z:0.0}) aim=({aim.X:0.000},{aim.Y:0.000},{aim.Z:0.000})");
        if (PickAt(screen) && request.Up != 0)
        {
            StepUp(request.Up);
        }
    }
}
