using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.UI.Boards;
using Godot;

namespace CSVM.UI.Overlays;

/// <summary>
/// The <c>--viewer</c> marker overlay (key K): draws every gun firepoint, ordnance pylon and the
/// aim <c>target</c> on the parked aircraft as a labelled gizmo, so the user can read which
/// physical mount is which (<c>docs/formats/markers.md</c>). Firepoints, pylons and the target
/// draw in distinct colours; firepoints sharing one mount draw in a fourth colour with stacked
/// names. Splitscreen never applies: <c>--viewer</c> is single-pane by construction.
/// ⚠ Gizmo dots always show; only the labels de-clutter, so no mount position is ever lost. The
/// full named table stays in <c>--dump-markers</c>.
/// </summary>
public sealed partial class MarkerOverlay : Node3D
{
    // A co-located pair sits at the exact same point, so its labels would print on top of each
    // other; nudge each up the airframe's own up-axis by this much per stack position so they
    // land in different screen cells and the de-clutter keeps both. Metres.
    private const float StackSpacing = 0.30f;
    private const float GizmoRadius = 0.06f;

    // Screen-cell de-clutter, as in NodeLabels: a name is a wide, short box, so the cell is wider
    // than tall, and the 3×3 neighbourhood test guarantees a real minimum gap.
    private const float GapX = 96f;
    private const float GapY = 24f;

    // Labels are static in world space (the parked plane doesn't move), so only which ones the
    // camera can fit changes, recompute a few times a second rather than per frame.
    private const double RefreshInterval = 0.2;

    // Gizmo colours by role. Shared-mount firepoints get their own colour precisely because a
    // co-located pair is the thing the overlay exists to make visible.
    private static readonly Color FirepointColor = new(1.0f, 0.55f, 0.15f); // warm orange
    private static readonly Color SharedColor = new(1.0f, 0.30f, 0.85f);    // magenta, two groups, one mount
    private static readonly Color PylonColor = new(0.30f, 0.80f, 1.0f);     // cyan
    private static readonly Color TargetColor = new(0.45f, 1.0f, 0.45f);    // green

    private readonly Node3D _plane;
    private readonly List<(Label3D Label, Vector3 World, bool Priority)> _labels = new();
    private readonly HashSet<long> _occupied = new();

    private Node3D? _holder;
    private CanvasLayer? _hudLayer;
    private Label? _hud;
    private bool _visible;
    private double _sinceRefresh = 1e9;
    private int _firepoints, _pylons, _shared;
    private bool _hasTarget;

    public MarkerOverlay(Node3D plane)
    {
        _plane = plane;
        Name = "marker_overlay";
    }

    /// <summary>Start hidden (plain <c>--viewer</c>, waits for K) or shown at launch
    /// (<c>--markers</c>, so a scripted screenshot captures it).</summary>
    public bool StartHidden { get; init; } = true;

    public override void _Ready()
    {
        if (!StartHidden)
        {
            SetShown(true);
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.K })
        {
            SetShown(!_visible);
        }
    }

    public override void _Process(double delta)
    {
        if (!_visible || _labels.Count == 0)
        {
            return;
        }
        _sinceRefresh += delta;
        if (_sinceRefresh < RefreshInterval)
        {
            return;
        }
        _sinceRefresh = 0;
        Relayout();
    }

    private static long CellKey(int x, int y) => ((long)x << 32) ^ (uint)y;

    // A small unshaded sphere drawn on top of the airframe (no depth test) so a muzzle point
    // buried in the cowling is still visible.
    private static MeshInstance3D Gizmo(Vector3 world, Color color)
    {
        var mi = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = GizmoRadius, Height = GizmoRadius * 2f, RadialSegments = 8, Rings = 4 },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = color,
                NoDepthTest = true,
                RenderPriority = 18,
            },
        };
        mi.Position = world;
        return mi;
    }

    // A billboarded, constant-on-screen-size name in the marker's colour, drawn through geometry.
    private static Label3D MarkerLabel(string text, Vector3 world, Color color) => new()
    {
        Text = text,
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        FixedSize = true,
        PixelSize = 0.00055f,
        FontSize = 44,
        OutlineSize = 14,
        NoDepthTest = true,
        Modulate = color,
        OutlineModulate = new Color(0f, 0f, 0f, 0.85f),
        RenderPriority = 20,
        OutlineRenderPriority = 19,
        Position = world,
    };

    private void SetShown(bool shown)
    {
        _visible = shown;
        if (shown)
        {
            EnsureBuilt();
            _sinceRefresh = 1e9; // re-layout on the next frame, not after the timer
        }
        if (_holder != null)
        {
            _holder.Visible = shown;
        }
        if (_hudLayer != null)
        {
            _hudLayer.Visible = shown;
        }
    }

    // Built lazily on first show, an untouched viewer session adds no nodes at all, so
    // nothing it renders can differ.
    private void EnsureBuilt()
    {
        if (_holder != null)
        {
            return;
        }
        _holder = new Node3D { Name = "marker_gizmos" };
        AddChild(_holder);

        // Collect the markers from the built plane, then resolve co-location against the plane
        // frame so the shared-mount grouping matches the dump exactly.
        var nodes = new List<Node3D>();
        var kinds = new List<MarkerRig.MarkerKind>();
        var localPos = new List<Vector3>();
        var toPlane = _plane.GlobalTransform.AffineInverse();
        Collect(_plane, nodes, kinds, localPos, toPlane);
        var groups = MarkerRig.GroupCoLocated(localPos, MarkerRig.CoLocateTolerance);
        var isShared = new bool[nodes.Count];
        var stackIndex = new int[nodes.Count];
        foreach (var group in groups)
        {
            for (int s = 0; s < group.Count; s++)
            {
                isShared[group[s]] = true;
                stackIndex[group[s]] = s;
            }
        }

        var planeUp = _plane.GlobalTransform.Basis.Y.Normalized();
        _shared = groups.Count;
        for (int i = 0; i < nodes.Count; i++)
        {
            var kind = kinds[i];
            switch (kind)
            {
                case MarkerRig.MarkerKind.Firepoint: _firepoints++; break;
                case MarkerRig.MarkerKind.Pylon: _pylons++; break;
                case MarkerRig.MarkerKind.Target: _hasTarget = true; break;
            }
            var color = kind switch
            {
                MarkerRig.MarkerKind.Pylon => PylonColor,
                MarkerRig.MarkerKind.Target => TargetColor,
                _ => isShared[i] ? SharedColor : FirepointColor,
            };
            var world = nodes[i].GlobalPosition;
            _holder.AddChild(Gizmo(world, color));
            // Stack a co-located pair's labels up the airframe so both names read.
            var labelPos = world + planeUp * (StackSpacing * stackIndex[i]);
            var label = MarkerLabel(nodes[i].GetMeta(AnimRuntime.NameMeta).AsString(), labelPos, color);
            _holder.AddChild(label);
            // Firepoints (and the shared-mount pairs) are the point of the overlay, so they win a
            // contested screen cell over a pylon; pylons crowd the wing and are secondary here.
            _labels.Add((label, labelPos, kind != MarkerRig.MarkerKind.Pylon));
        }

        _hudLayer = new CanvasLayer { Layer = HudLayers.Debug };
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _hud = new Label();
        _hud.AddThemeFontSizeOverride("font_size", 12);
        _hud.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _hud.GrowVertical = Control.GrowDirection.Begin;
        _hud.OffsetLeft = 10;
        _hud.OffsetBottom = -8;
        root.AddChild(_hud);
        _hudLayer.AddChild(root);
        AddChild(_hudLayer);
    }

    // Nearest-first screen-cell de-clutter over the fixed marker set: firepoints claim
    // cells before pylons, then within each band the camera-nearest wins. A label loses to
    // something more important or closer, exactly like NodeLabels.
    private void Relayout()
    {
        // The session's only camera: this overlay builds only under --viewer, which forces
        // --players=1, so a splitscreen session never stands this camera down under it.
        var camera = GetViewport().GetCamera3D();
        if (camera == null)
        {
            return;
        }
        var eye = camera.GlobalPosition;
        // Priority band first (firepoints/target/shared), then nearest camera distance.
        _labels.Sort((a, b) => a.Priority != b.Priority
            ? (a.Priority ? -1 : 1)
            : eye.DistanceSquaredTo(a.World).CompareTo(eye.DistanceSquaredTo(b.World)));

        _occupied.Clear();
        var view = camera.GetViewport().GetVisibleRect().Grow(GapX);
        int shown = 0, hidden = 0;
        foreach (var (label, world, _) in _labels)
        {
            bool visible = !camera.IsPositionBehind(world);
            if (visible)
            {
                var screen = camera.UnprojectPosition(world);
                visible = view.HasPoint(screen) && Claim(screen);
            }
            label.Visible = visible;
            if (visible)
            {
                shown++;
            }
            else
            {
                hidden++;
            }
        }
        if (_hud != null)
        {
            _hud.Text = $"markers [K]: {_firepoints} firepoints, {_pylons} pylons"
                        + (_hasTarget ? ", 1 target" : ", no target")
                        + (_shared > 0 ? $", {_shared} shared mount(s) in magenta" : "")
                        + (hidden > 0 ? $"   ({shown} labels shown, {hidden} hidden, orbit to read)" : "");
        }
    }

    // Reserves this label's screen cell, or reports it taken. Checks the 3×3
    // neighbourhood so two labels can't sit a pixel apart across a cell boundary.
    private bool Claim(Vector2 screen)
    {
        int cx = Mathf.FloorToInt(screen.X / GapX), cy = Mathf.FloorToInt(screen.Y / GapY);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (_occupied.Contains(CellKey(cx + dx, cy + dy)))
                {
                    return false;
                }
            }
        }
        _occupied.Add(CellKey(cx, cy));
        return true;
    }

    private void Collect(Node node, List<Node3D> nodes, List<MarkerRig.MarkerKind> kinds,
        List<Vector3> localPos, Transform3D toPlane)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Node3D n3d && n3d.HasMeta(AnimRuntime.NameMeta)
                && MarkerRig.Classify(n3d.GetMeta(AnimRuntime.NameMeta).AsString(), out var kind, out _))
            {
                nodes.Add(n3d);
                kinds.Add(kind);
                localPos.Add(toPlane * n3d.GlobalPosition);
            }
            Collect(child, nodes, kinds, localPos, toPlane);
        }
    }
}
