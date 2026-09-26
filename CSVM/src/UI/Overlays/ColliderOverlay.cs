using System;
using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Overlays;

/// <summary>
/// The collision wireframe overlay (key C): every built collider in the session drawn as
/// coloured lines, so "is this solid?" is answerable by looking rather than by reading a census.
/// Collision is a flight-build option; pressing C without <c>--collision</c> prints why and draws
/// nothing rather than an empty overlay that reads as "nothing here is collidable". World and
/// aircraft geometry hangs shapes on <see cref="CollisionShape3D"/> nodes; solid clutter attaches
/// shared shapes straight to a region body's RID, read back through the physics server. ⚠ Only
/// the server-side <c>BodyGetShape*</c> getters are used on those; a <c>ShapeOwner*</c> call
/// would rebuild the body from nodes it lacks and silently empty it. The drawing follows each
/// shape's <c>Disabled</c> flag, so a kill's collider swap shows live. Colour is the resolved
/// surface id a touch will actually select, not the raw stamp. Full decode: docs/architecture.md.
/// </summary>
public sealed partial class ColliderOverlay : Node
{
    // Trimeshes bigger than this draw as their bounding box instead of their triangles: a
    // terrain tile's collider is tens of thousands of lines and tells you nothing an outline does
    // not. The count of boxed shapes is reported, never hidden.
    private const int MaxShapeTris = 2000;

    // And a total line budget across the whole overlay; past it every remaining shape is
    // boxed. A C5 city is ~8k clutter placements.
    private const int MaxTotalLines = 400000;

    // Wireframes sit exactly on the surface they outline, which z-fights into dashes.
    // Scaling each shape a hair about its own geometric centre lifts the lines clear.
    // ⚠ Never about the local origin: chapter-world trimeshes carry vertices baked in world
    // coordinates under identity nodes, so an origin-relative scale shifts the whole wireframe
    // by 0.25% of the vertex magnitude, ~20 m at a map corner, invisible near the origin.
    private const float Inflate = 1.0025f;

    // How often the drawing re-reads the Disabled flags. Fast enough that a kill's swap is visibly
    // immediate, cheap enough that it is a list walk, not a tree walk.
    private const double SyncInterval = 0.15;

    // How many names a flip line prints before it says how many more there were. The COUNTS above
    // are never elided, a truncated name list must not read as a smaller change.
    private const int MaxNames = 12;

    // The two owner classes neither surface tag decides, plus the fallback for a body that is
    // neither. Negative, so a key can never collide with a registry surface id.
    private const int ClutterKey = -1, PlaneKey = -2, OtherKey = -3;

    private static readonly Color ClutterColor = new(0.4f, 1f, 0.4f);
    private static readonly Color PlaneColor = new(1f, 0.9f, 0.25f);
    private static readonly Color OtherColor = new(1f, 0.3f, 1f);

    // One colour per SurfaceRegistry id, index == surface id. Eleven of the fourteen cannot be
    // drawn in this install, an id whose def nothing ships resolves to slot 0 before it reaches
    // here (ResolveId), but the palette covers the registry rather than the shipped subset, so a
    // chapter or install that does ship one gets a colour instead of a silent collapse.
    private static readonly Color[] SurfaceColors =
    {
        new(0.35f, 0.65f, 1f),    // 0  default   (the world blue this overlay always drew terrain in)
        new(0.2f, 1f, 0.9f),      // 1  water
        new(0.15f, 0.45f, 0.75f), // 2  seafloor
        new(0.9f, 0.85f, 0.4f),   // 3  quicksand
        new(1f, 0.25f, 0.1f),     // 4  lava
        new(1f, 0.5f, 0.5f),      // 5  fire
        new(1f, 1f, 1f),          // 6  player
        new(0.7f, 0f, 0f),        // 7  enemy
        new(0.6f, 0.6f, 0.65f),   // 8  airstrip
        new(0.55f, 0.3f, 1f),     // 9  opensesame
        new(0.45f, 0f, 0.25f),    // 10 death
        new(1f, 0.55f, 0.15f),    // 11 buildings
        new(0.1f, 0.55f, 0.4f),   // 12 dzone
        new(0.85f, 0.6f, 0.35f),  // 13 dirt
    };

    private static StandardMaterial3D? _lineMaterial;

    private readonly Node3D _world;
    private readonly bool _collisionBuilt;
    private readonly List<Entry> _entries = new();
    private readonly List<MeshInstance3D> _unswitched = new();

    private bool _shown, _debugDone;
    private double _sinceSync;
    private int _lastOn = -1, _lastOff = -1;
    private CanvasLayer? _hudLayer;
    private Label? _hud;
    private RichTextLabel? _legend;
    private string _summary = "";

    public ColliderOverlay(Node3D world, bool collisionBuilt)
    {
        _world = world;
        _collisionBuilt = collisionBuilt;
        Name = "collider_overlay";
    }

    /// <summary><c>--collision=show</c>: open the overlay on the first frame, the scripted stand-in
    /// for the C press.</summary>
    public bool DebugShow { get; init; }

    /// <summary>The flown aircraft and their swept airframe hulls. Those are shape resources the
    /// flight code casts with directly, not scene nodes, so they are passed in rather than found.
    /// <c>Frame</c> is the FlightController, not the plane model, <see cref="PlaneCollider"/>'s
    /// hulls are expressed in the model's PARENT frame, so drawing them under the model itself
    /// would apply its own local transform a second time.</summary>
    public IReadOnlyList<(Node3D Frame, PlaneCollider Collider)> Planes { get; init; } =
        Array.Empty<(Node3D, PlaneCollider)>();

    /// <summary>What each surface id resolves to on contact against this session's program,
    /// <see cref="EffectCatalogue.ResolvedSurfaceIds(Mech3.AnimProgram)"/>, index == id. Null only
    /// when the session has no world program, which is also the only case in which nothing carries
    /// a stamped id: <see cref="ResolveId"/> then shows the raw one rather than inventing a
    /// resolution nothing measured.</summary>
    public IReadOnlyList<int>? ResolvedSurfaceIds { get; init; }

    public override void _Process(double delta)
    {
        if (DebugShow && !_debugDone)
        {
            // Deferred one frame like every other --debug-* opener: the world subtree, the plane
            // and any --destroy kill are only final once the session has been built. Once, a
            // no-collision run would otherwise reprint its notice every frame.
            _debugDone = true;
            Toggle();
        }
        if (!_shown)
        {
            return;
        }
        _sinceSync += delta;
        if (_sinceSync >= SyncInterval)
        {
            _sinceSync = 0.0;
            SyncEnabled(report: true);
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.C })
        {
            return;
        }
        Toggle();
        GetViewport().SetInputAsHandled();
    }

    /// <summary>C: show or hide the wireframes. Each show walks the current tree so a destructible
    /// swap cannot leave the overlay holding drawings parented to freed nodes.</summary>
    public void Toggle()
    {
        if (!_collisionBuilt)
        {
            // The whole reason this control exists: no bodies were built in this mode, which is not
            // the same fact as "nothing here is solid".
            Log.Warn("world", $"collider overlay: this mode builds NO collision at all, there is nothing to draw, which is not the same as 'nothing is collidable'. Relaunch with --collision (or fly the chapter) to see the colliders.");
            ShowNotice("NO COLLISION BUILT IN THIS MODE\n"
                       + "--freecam / --anim-lab build no bodies at all.\n"
                       + "Relaunch with --collision to build and draw them.");
            return;
        }
        if (_shown)
        {
            HideNotice();
            Clear();
            _shown = false;
        }
        else
        {
            Build();
            _shown = true;
            SyncEnabled(report: false);
            ShowNotice(_summary, showLegend: true);
        }
        // Two counts, never their difference: a death switches a healthy collider off and its
        // wreck's on, and the signed sum of that reads as death ADDING collision.
        Log.Info("world", $"collider overlay {(_shown ? "on" : "off")}, {_summary} · switched on {_lastOn} · switched off {_lastOff}");
    }

    private static StandardMaterial3D LineMaterial() => _lineMaterial ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        DisableFog = true,
        RenderPriority = 15,
    };

    private static Color ColorFor(int key) => key switch
    {
        ClutterKey => ClutterColor,
        PlaneKey => PlaneColor,
        OtherKey => OtherColor,
        _ => key >= 0 && key < SurfaceColors.Length ? SurfaceColors[key] : OtherColor,
    };

    // What a key reads as: `13/dirt` for a surface id, the bare word for the three
    // owner keys. Id and name together because fourteen ids do not have fourteen readable
    // colours, the number is the part that identifies the slot.
    private static string LabelFor(int key) => key switch
    {
        ClutterKey => "clutter",
        PlaneKey => "plane",
        OtherKey => "other",
        _ => $"{key}/{SurfaceRegistry.NameForId(key) ?? "?"}",
    };

    // ---- shape emission ------------------------------------------------------------------------

    // Draws one shape into the mesh. Returns true when it was drawn as a bounding box
    // rather than in full, the caller counts those so the panel can say so.
    // Whether this shape has anything to draw. Checked before opening a surface: an
    // ImmediateMesh surface closed with no vertices in it is an engine error.
    private static bool HasGeometry(Shape3D shape) =>
        shape is not ConcavePolygonShape3D concave || concave.GetFaces().Length >= 3;

    private static bool EmitShape(ImmediateMesh mesh, Shape3D shape, Transform3D at, Color col, ref int lines)
    {
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        bool boxed = false;
        switch (shape)
        {
            case BoxShape3D box:
                EmitBox(mesh, at, box.Size, col);
                lines += 12;
                break;
            case SphereShape3D sphere:
                EmitSphere(mesh, at, sphere.Radius, col);
                lines += 72;
                break;
            case ConcavePolygonShape3D concave:
                boxed = EmitTriangles(mesh, concave.GetFaces(), at, col, ref lines);
                break;
            case ConvexPolygonShape3D convex when convex.Points.Length >= 4:
                lines += EmitHull(mesh, ConvexHull.Of(convex.Points, 0f), at, col);
                break;
            default:
                // Anything else (capsules, a degenerate hull): its own bounds, which is still an
                // honest "something solid is here" rather than nothing.
                EmitBox(mesh, at, Vector3.One, col);
                lines += 12;
                boxed = true;
                break;
        }
        mesh.SurfaceEnd();
        return boxed;
    }

    private static bool EmitTriangles(ImmediateMesh mesh, Vector3[] faces, Transform3D at, Color col,
        ref int lines)
    {
        int tris = faces.Length / 3;
        if (tris == 0)
        {
            return false;
        }
        var bounds = new Aabb(faces[0], Vector3.Zero);
        foreach (var v in faces)
        {
            bounds = bounds.Expand(v);
        }
        var centre = bounds.GetCenter();
        if (tris > MaxShapeTris || lines > MaxTotalLines)
        {
            var boxAt = at;
            boxAt.Origin = at * centre;
            EmitBox(mesh, boxAt, bounds.Size * Inflate, col);
            lines += 12;
            return true;
        }
        // Each triangle's three edges, de-duplicated by quantised endpoint pair: a closed trimesh
        // shares every interior edge between two faces, so drawing them raw doubles the line count
        // for an identical picture.
        var seen = new HashSet<(long, long)>();
        mesh.SurfaceSetColor(col);
        for (int t = 0; t + 2 < faces.Length; t += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                var a = faces[t + k];
                var b = faces[t + (k + 1) % 3];
                if (!seen.Add(EdgeKey(a, b)))
                {
                    continue;
                }
                mesh.SurfaceAddVertex(at * (centre + (a - centre) * Inflate));
                mesh.SurfaceAddVertex(at * (centre + (b - centre) * Inflate));
                lines++;
            }
        }
        return false;
    }

    private static (long, long) EdgeKey(Vector3 a, Vector3 b)
    {
        long ka = Quantise(a), kb = Quantise(b);
        return ka <= kb ? (ka, kb) : (kb, ka);
    }

    private static long Quantise(Vector3 v)
    {
        unchecked
        {
            long h = (long)Mathf.Round(v.X * 1000f);
            h = h * 1000003 + (long)Mathf.Round(v.Y * 1000f);
            h = h * 1000003 + (long)Mathf.Round(v.Z * 1000f);
            return h;
        }
    }

    private static void EmitBox(ImmediateMesh mesh, Transform3D xf, Vector3 size, Color col)
    {
        var h = size * 0.5f;
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = xf * new Vector3(
                (i & 1) == 0 ? -h.X : h.X,
                (i & 2) == 0 ? -h.Y : h.Y,
                (i & 4) == 0 ? -h.Z : h.Z);
        }
        ReadOnlySpan<int> pairs = stackalloc int[]
        {
            0, 1, 2, 3, 4, 5, 6, 7,
            0, 2, 1, 3, 4, 6, 5, 7,
            0, 4, 1, 5, 2, 6, 3, 7,
        };
        mesh.SurfaceSetColor(col);
        for (int i = 0; i < pairs.Length; i += 2)
        {
            mesh.SurfaceAddVertex(corners[pairs[i]]);
            mesh.SurfaceAddVertex(corners[pairs[i + 1]]);
        }
    }

    // Every hull edge once; returns the line count.
    private static int EmitHull(ImmediateMesh mesh, ConvexHull hull, Transform3D xf, Color col)
    {
        mesh.SurfaceSetColor(col);
        foreach (var (a, b) in hull.Edges)
        {
            mesh.SurfaceAddVertex(xf * hull.Points[a]);
            mesh.SurfaceAddVertex(xf * hull.Points[b]);
        }
        return hull.Edges.Length;
    }

    private static void EmitSphere(ImmediateMesh mesh, Transform3D xf, float radius, Color col)
    {
        const int segments = 24;
        mesh.SurfaceSetColor(col);
        for (int axis = 0; axis < 3; axis++)
        {
            for (int i = 0; i < segments; i++)
            {
                float a0 = Mathf.Tau * i / segments, a1 = Mathf.Tau * (i + 1) / segments;
                mesh.SurfaceAddVertex(xf * OnCircle(axis, a0, radius));
                mesh.SurfaceAddVertex(xf * OnCircle(axis, a1, radius));
            }
        }
    }

    private static Vector3 OnCircle(int axis, float angle, float radius)
    {
        float c = Mathf.Cos(angle) * radius, s = Mathf.Sin(angle) * radius;
        return axis switch
        {
            0 => new Vector3(0f, c, s),
            1 => new Vector3(c, 0f, s),
            _ => new Vector3(c, s, 0f),
        };
    }

    // The game-file name of the object a collider belongs to: the nearest ancestor
    // carrying a `cs_name`, which is what a destructible is called in the data. A collider's
    // own node is SceneBuilder's unnamed `col` body, which names nothing.
    private static string OwnerName(Node3D shape)
    {
        for (Node? n = shape; n != null; n = n.GetParent())
        {
            if (n is Node3D n3d && n3d.HasMeta(AnimRuntime.NameMeta))
            {
                return SelectionService.NameOf(n3d);
            }
        }
        return shape.Name.ToString();
    }

    private static string Names(List<string> names)
    {
        if (names.Count == 0)
        {
            return "(none)";
        }
        if (names.Count <= MaxNames)
        {
            return $"{names.Count}: {string.Join(", ", names)}";
        }
        return $"{names.Count}: {string.Join(", ", names.GetRange(0, MaxNames))}, … +{names.Count - MaxNames} more";
    }

    // ---- building ------------------------------------------------------------------------------

    // The overlay's colour key for one shape: the surface id its body resolves to, or one
    // of the three owner keys above. The id decides what happens when you touch the geometry, so
    // it is what the wireframe is coloured by; the texture-derived class
    // (SceneBuilder.SurfaceMeta) decides nothing here and is not read.
    private int KeyOf(CollisionShape3D cs)
    {
        if (cs.GetParent() is not StaticBody3D sb)
        {
            return OtherKey;
        }
        string name = sb.Name.ToString();
        if (name.StartsWith("clutter_bld", StringComparison.Ordinal))
        {
            return ClutterKey;
        }
        if (sb.HasMeta(SceneBuilder.SurfaceIdMeta))
        {
            return ResolveId(sb.GetMeta(SceneBuilder.SurfaceIdMeta).AsInt32());
        }
        // Safety net for an unstamped body; SceneBuilder stamps the id on every collider it
        // builds. Matches ProjectilePool.SurfaceIdOf's answer for an untagged collider.
        return name.StartsWith("col", StringComparison.Ordinal) ? SurfaceRegistry.Default : OtherKey;
    }

    // The id a stamped body actually resolves to, its own when a touch cascade ships a
    // def for it, else slot 0, including for an id outside the registry (the cascade's own
    // out-of-range arm): the overlay shows what will be
    // selected, never the raw stamp, or it hides the empty-slot arm it exists to expose.
    private int ResolveId(int id)
    {
        if (ResolvedSurfaceIds is not { } resolved)
        {
            return id; // no world program: nothing in the tree carries a stamp to resolve
        }
        return id >= 0 && id < resolved.Count ? resolved[id] : SurfaceRegistry.Default;
    }

    private void Build()
    {
        int lines = 0, boxed = 0, unknown = 0;
        var perKey = new Dictionary<int, int>();

        void Walk(Node n)
        {
            if (n is Node3D marked && marked.HasMeta(SelectionService.OverlayMeta))
            {
                return; // our own drawings
            }
            if (n is CollisionShape3D { Shape: { } shape } cs && HasGeometry(shape))
            {
                int key = KeyOf(cs);
                var mesh = new ImmediateMesh();
                bool asBox = EmitShape(mesh, shape, Transform3D.Identity, ColorFor(key), ref lines);
                if (asBox)
                {
                    boxed++;
                }
                // '/' is not legal in a Godot node name (it would be silently substituted), so the
                // id/name label is spelled with an underscore here and only here.
                var draw = MakeDraw(mesh, cs, $"col_wire_{LabelFor(key).Replace('/', '_')}");
                _entries.Add(new Entry { Shape = cs, Draw = draw, WasEnabled = !cs.Disabled });
                perKey[key] = perKey.GetValueOrDefault(key) + 1;
            }
            else if (n is StaticBody3D body && body.GetChildCount() == 0)
            {
                // The clutter region bodies: shapes attached to the body RID, no child nodes.
                int drawn = EmitBodyShapes(body, ref lines, ref boxed);
                if (drawn > 0)
                {
                    perKey[ClutterKey] = perKey.GetValueOrDefault(ClutterKey) + drawn;
                }
                else if (PhysicsServer3D.BodyGetShapeCount(body.GetRid()) > 0)
                {
                    unknown++;
                }
            }
            foreach (var child in n.GetChildren())
            {
                Walk(child);
            }
        }
        Walk(_world);

        foreach (var (frame, collider) in Planes)
        {
            var mesh = new ImmediateMesh();
            mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
            foreach (var part in collider.Parts)
                lines += EmitHull(mesh, part.Hull, part.Local, PlaneColor);
            mesh.SurfaceEnd();
            // Drawn under FlightController (frame), the space Collider.Parts.Local is already
            // in; parenting to the model itself would double its own transform.
            _unswitched.Add(MakeDraw(mesh, frame, "col_wire_plane"));
            perKey[PlaneKey] = perKey.GetValueOrDefault(PlaneKey) + collider.Parts.Count;
        }

        // Surface ids first in slot order, then the three owner keys: a numeric sort keeps
        // 0/default … 13/dirt in registry order, which an ordinal sort of the labels would not.
        var keys = new List<int>(perKey.Keys);
        keys.Sort((a, b) =>
        {
            if (a >= 0 != b >= 0)
            {
                return a >= 0 ? -1 : 1;
            }
            return a >= 0 ? a.CompareTo(b) : b.CompareTo(a); // owner keys in their declared order
        });
        var parts = new List<string>(keys.Count);
        foreach (int key in keys)
        {
            parts.Add($"{LabelFor(key)} {perKey[key]}");
        }
        _summary = $"colliders: {string.Join(" · ", parts)} · {lines} lines"
                   + (boxed > 0 ? $" · {boxed} drawn as bounding boxes (over {MaxShapeTris} tris or past the line budget)" : "");
        if (unknown > 0)
        {
            Log.Warn("world", $"collider overlay: {unknown} body(ies) carry server-side shapes this overlay cannot read back, they are NOT drawn");
        }
        Log.Info("world", $"collider overlay built: {_summary}");
        // Said every build, not just in the docs: the two ways this picture is not the source data.
        Log.Info("world", $"collider overlay: colours are the surface id a touch RESOLVES to (an id whose def this install does not ship draws as 0/default), and the id is stamped per collider BODY, not per polygon. It shows what the runtime will select, not the material data.");
    }

    // Releases this pass's drawings before the next show rebuilds from the live tree.
    // A source subtree may already have been freed by a destructible swap, so its drawing is
    // checked independently before it is queued.
    private void Clear()
    {
        foreach (var e in _entries)
        {
            if (IsInstanceValid(e.Draw))
            {
                e.Draw.QueueFree();
            }
        }
        foreach (var draw in _unswitched)
        {
            if (IsInstanceValid(draw))
            {
                draw.QueueFree();
            }
        }
        _entries.Clear();
        _unswitched.Clear();
        _lastOn = -1;
        _lastOff = -1;
        _sinceSync = 0.0;
    }
    private MeshInstance3D MakeDraw(ImmediateMesh mesh, Node3D parent, string name)
    {
        var draw = new MeshInstance3D
        {
            Name = name,
            Mesh = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = LineMaterial(),
            Visible = false,
        };
        // Marked as a drawing so the shared selection neither picks it nor folds it into the box it
        // measures for the object it is parented onto.
        draw.SetMeta(SelectionService.OverlayMeta, true);
        parent.AddChild(draw);
        return draw;
    }

    // Reads a body's shapes back off the physics server, the clutter case, whose shared
    // shapes have no scene node. ⚠ Server getters only: a `ShapeOwner*` call here would make
    // Godot rebuild the body from its (nonexistent) shape nodes and empty it.
    private int EmitBodyShapes(StaticBody3D body, ref int lines, ref int boxed)
    {
        var rid = body.GetRid();
        int count = PhysicsServer3D.BodyGetShapeCount(rid);
        if (count == 0)
        {
            return 0;
        }
        var placements = new List<(Vector3[] Faces, Transform3D At)>();
        for (int i = 0; i < count; i++)
        {
            var data = PhysicsServer3D.ShapeGetData(PhysicsServer3D.BodyGetShape(rid, i));
            if (data.VariantType != Variant.Type.Dictionary)
            {
                continue;
            }
            var dict = data.AsGodotDictionary();
            if (!dict.TryGetValue("faces", out var faces))
            {
                continue;
            }
            var array = faces.AsVector3Array();
            if (array.Length >= 3)
            {
                placements.Add((array, PhysicsServer3D.BodyGetShapeTransform(rid, i)));
            }
        }
        if (placements.Count == 0)
        {
            return 0;
        }
        var mesh = new ImmediateMesh();
        // ⚠ One surface for the WHOLE body, not one per shape: a city region carries thousands of
        // placements and an ImmediateMesh caps at 256 surfaces (the excess errors per call and
        // draws nothing).
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (var (faces, xf) in placements)
        {
            if (EmitTriangles(mesh, faces, xf, ClutterColor, ref lines))
            {
                boxed++;
            }
        }
        mesh.SurfaceEnd();
        int drawn = placements.Count;
        // Clutter shapes are never switched off (no destructible owns one), so this drawing has no
        // Disabled flag to follow, it flips with the overlay itself.
        _unswitched.Add(MakeDraw(mesh, body, "col_wire_clutter"));
        return drawn;
    }

    // ---- live state ----------------------------------------------------------------------------

    // Re-reads every tracked shape's `Disabled` flag and matches the drawing to it,
    // then reports the two tallies whenever they move, the destructible swap, seen from outside.
    // Reported as separate on and off counts: their signed difference hides the removal.
    private void SyncEnabled(bool report)
    {
        int on = 0, off = 0;
        var wentOff = new List<string>();
        var cameOn = new List<string>();
        foreach (var e in _entries)
        {
            if (!IsInstanceValid(e.Shape))
            {
                continue;
            }
            bool enabled = !e.Shape.Disabled;
            if (enabled)
            {
                on++;
            }
            else
            {
                off++;
            }
            if (enabled != e.WasEnabled)
            {
                (enabled ? cameOn : wentOff).Add(OwnerName(e.Shape));
                e.WasEnabled = enabled;
            }
            bool want = _shown && enabled;
            if (e.Draw.Visible != want)
            {
                e.Draw.Visible = want;
            }
        }
        if (on == _lastOn && off == _lastOff)
        {
            return;
        }
        if (report && _lastOn >= 0)
        {
            Log.Info("world", $"collider overlay: enabled {_lastOn} -> {on}, disabled {_lastOff} -> {off} (separate counts: a net would hide a removal)");
            Log.Info("world", $"collider overlay: switched OFF {Names(wentOff)}");
            Log.Info("world", $"collider overlay: switched ON  {Names(cameOn)}");
        }
        _lastOn = on;
        _lastOff = off;
        if (_hud != null && _shown)
        {
            _hud.Text = _summary + Log.Format($"\nswitched on {on} · switched off {off}");
        }
    }

    // ---- notice --------------------------------------------------------------------------------

    // One coloured label per key, straight from ColorFor, the legend's only listing of them, so a
    // palette change is the only edit that can move it. The surface ids listed are the ones that
    // resolve to themselves against this session's program: every other id draws as 0/default, so
    // listing it would name a colour the overlay cannot produce. That is still the whole key rather
    // than "what happens to be on screen", an id is in it because the data can select it here, not
    // because a body drew it.
    private string BuildLegendText()
    {
        var parts = new List<string>();
        for (int id = 0; id < SurfaceRegistry.Names.Count; id++)
        {
            if (ResolveId(id) == id)
            {
                parts.Add($"[color=#{ColorFor(id).ToHtml(false)}]{LabelFor(id)}[/color]");
            }
        }
        foreach (int key in new[] { ClutterKey, PlaneKey, OtherKey })
        {
            parts.Add($"[color=#{ColorFor(key).ToHtml(false)}]{LabelFor(key)}[/color]");
        }
        return string.Join("   ", parts);
    }

    // showLegend is false for the "no collision built" notice (the trap: a legend for
    // wireframes that were never drawn is the same false "nothing is collidable" read the overlay
    // exists to avoid) and true only for the summary shown once wireframes are actually up.
    private void ShowNotice(string text, bool showLegend = false)
    {
        if (_hudLayer == null)
        {
            _hudLayer = new CanvasLayer { Layer = HudLayers.Debug };
            var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _hud = new Label
            {
                // Under the freecam readout and the selection breadcrumb, both top-left.
                Position = new Vector2(12, 120),
                Modulate = new Color(0.6f, 1f, 0.7f),
            };
            _hud.AddThemeFontSizeOverride("font_size", 13);
            root.AddChild(_hud);
            _legend = new RichTextLabel
            {
                // Below the (up to two-line) summary label above it.
                Position = new Vector2(12, 160),
                Size = new Vector2(900, 24),
                BbcodeEnabled = true,
                FitContent = true,
                ScrollActive = false,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Text = BuildLegendText(),
            };
            _legend.AddThemeFontSizeOverride("normal_font_size", 13);
            _legend.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
            root.AddChild(_legend);
            _hudLayer.AddChild(root);
            AddChild(_hudLayer);
        }
        _hud!.Text = text;
        _legend!.Visible = showLegend;
        _hudLayer.Visible = true;
    }

    private void HideNotice()
    {
        if (_hudLayer != null)
        {
            _hudLayer.Visible = false;
        }
    }

    // One node-backed shape and the wireframe drawn for it. The wireframe's visibility
    // tracks the shape's own `Disabled` flag, which is what the destructible swap moves.
    private sealed class Entry
    {
        public required CollisionShape3D Shape;
        public required MeshInstance3D Draw;
        public required bool WasEnabled;
    }
}
