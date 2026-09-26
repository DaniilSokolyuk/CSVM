using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Airframe;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Labs;

/// <summary>
/// The mesh lab (key M): geometry and shading diagnostics for one subtree, normal vectors,
/// wireframe with smoothing seams, collision zone boxes, steerable lighting, and live overrides
/// of the two render decisions shading artifacts usually trace back to, so each candidate cause
/// can be toggled and watched alone. In <c>--viewer</c> it owns the parked aircraft; in
/// <c>--freecam</c>/<c>--anim-lab</c> it is scoped to whatever <see cref="SelectionService"/> has
/// selected, and restores that subtree's original material and mesh on change or deselection.
/// Starts hidden so an unadorned <c>--viewer</c> screenshot stays byte-identical; M toggles it,
/// DamageLab owns H and LiveryLab owns L, so all three can be open at once. The FLAT heuristic can
/// false-positive: a lone triangle whose vertex normals equal its own face normal reads as flat
/// though the data is smooth-shaded.
/// </summary>
public sealed partial class MeshLab : Node
{
    // ---- state ------------------------------------------------------------------------

    // The most surfaces the lab reads back off one subtree. A rung high up the ladder can
    // carry a whole partition; collecting it would stall the frame and drown the overlays. Over the
    // cap the lab still works on what it took and SAYS it was capped, rather than quietly
    // diagnosing part of the object.
    private const int MaxSurfaces = 3000;

    // And the most triangles the line overlays emit. Independent of the collection cap:
    // the counts stay honest for the whole subtree, only the drawing stops.
    private const int MaxOverlayTris = 60000;

    // Normal line length as a fraction of the aircraft's bounding radius, so the lines read
    // the same on the Balmoral and the autogyro. TUNE.
    private const float NormalLenFrac = 0.022f;

    // The uniform the derived shader carries, named so it cannot collide with anything
    // SceneBuilder declares.
    private const string NormalModeParam = "csky_lab_normal_mode";

    private static readonly Dictionary<string, Color> PartColors = new()
    {
        ["nose"] = new Color(1f, 0.9f, 0.2f),
        ["tail"] = new Color(1f, 0.3f, 1f),
        ["leftwing"] = new Color(0.2f, 0.9f, 1f),
        ["rightwing"] = new Color(1f, 0.55f, 0.1f),
    };

    private static readonly string[] CullTokens = { "cull_disabled", "cull_front", "cull_back" };

    private static bool _warnedDeriveGap;

    private static bool _warnedOverrideGap;

    private readonly PlaneCollider? _collider;
    private readonly DirectionalLight3D _sun;
    private readonly Godot.Environment? _env;
    private readonly Camera3D _camera;
    // Set only in the scoped (--freecam/--anim-lab) mode: the shared selection the lab follows.
    private readonly SelectionService? _selection;

    // The aircraft's geometry, read back once from the built ArrayMeshes and cached in the
    // plane's own frame. Rebuilding the overlays is then pure line emission.
    private readonly List<Surf> _surfaces = new();

    private readonly Dictionary<int, Shader> _overrideShaders = new();

    // Derived shaders, keyed by the source shader and the cull mode asked of it. One per pair, for
    // the whole session: the source shaders are themselves cached and shared across surfaces.
    private readonly Dictionary<(ulong, int), Shader?> _derived = new();

    private readonly Dictionary<Surf, ArrayMesh> _smoothed = new();

    private Node3D? _target;

    private bool _scopedActive;

    private NormalColorMode _colorMode = NormalColorMode.Provenance;
    private NormalDensity _density = NormalDensity.Off;
    private WireMode _wire = WireMode.Off;
    private CullOverride _cull = CullOverride.AsData;
    private NormalSource _normals = NormalSource.AsData;
    private bool _boxes, _headlight, _engineWireframe;

    private CanvasLayer _ui = null!;
    private Label _statusLabel = null!;
    private Label _countsLabel = null!;
    private MeshInstance3D _normalDraw = null!, _wireDraw = null!, _boxDraw = null!;
    private bool _suppressCallbacks;

    private int _triCount, _flatCount, _twoSidedTris;

    private int _debugCycles;
    private bool _scopedPending;
    private bool _debugRestore;

    private bool _capped;
    private int _fullbrightSurfaces;

    // Parent of the three overlay meshes in the scoped mode: one node the lab owns, whose transform
    // follows the target each frame. In --viewer the overlays stay children of the parked plane,
    // which is where they have always been.
    private Node3D? _overlayRoot;

    // --debug-mesh=force: build the override materials even when both cyclers sit on AsData. The
    // lab's able-to-fail control, an override that only ever differs in the thing under test must
    // render the shipped picture when asked for the shipped settings, and this is what measures it.
    private bool _forceOverride;

    private Vector3 _sunDir = new(-0.5f, -0.7f, 0.5f);
    private float _sunEnergy = 1.6f, _ambientEnergy = 0.9f;
    private bool _ambientOn = true;
    // The scoped lab's own light. The session's sun and environment belong to the world, and a
    // diagnostic that re-aims them changes the thing it is measuring everywhere else on screen,
    // so in that mode the sliders drive this instead, created dark and only on first use.
    private DirectionalLight3D? _labLight;

    private Button _normalsBtn = null!, _colorBtn = null!, _wireBtn = null!,
        _cullBtn = null!, _sourceBtn = null!;
    private CheckButton? _boxBtn, _ambientBtn, _engineWireBtn;
    private CheckButton _headlightBtn = null!;
    private Label? _lightNote;
    private string _scopedNote = "";

    /// <summary>The <c>--viewer</c> lab: one fixed subtree (the parked aircraft) for the session.</summary>
    public MeshLab(Node3D planeRoot, PlaneCollider? collider, DirectionalLight3D sun,
        Godot.Environment? env, Camera3D camera)
    {
        _target = planeRoot;
        _collider = collider;
        _sun = sun;
        _env = env;
        _camera = camera;
        Name = "mesh_lab";
    }

    /// <summary>The scoped lab (<c>--freecam</c>/<c>--anim-lab</c>): no target until M attaches it
    /// to the shared selection's current rung.</summary>
    public MeshLab(SelectionService selection, DirectionalLight3D sun, Godot.Environment? env,
        Camera3D camera)
    {
        _selection = selection;
        _sun = sun;
        _env = env;
        _camera = camera;
        Name = "mesh_lab";
    }

    // ---- modes ------------------------------------------------------------------------

    /// <summary>What the normal lines are coloured by. Provenance is the default because it
    /// is the one that maps onto a real question (is this patch flat-shaded?); direction is
    /// the conventional engine view; winding tests whether a normal opposes its own polygon.</summary>
    public enum NormalColorMode { Provenance, Direction, Winding }

    /// <summary>Off / one line per triangle at its centroid / one line per triangle corner.
    /// Per-corner is the honest picture (it shows split vertices) but it is ~3× the lines
    /// and reads as a hairball on a small aircraft, so per-face is the default.</summary>
    public enum NormalDensity { Off, PerFace, PerCorner }

    public enum WireMode { Off, Edges, EdgesAndSeams, SeamsOnly }

    /// <summary>Culling override. AsData honours the per-polygon SHOW_BACKFACE flag the
    /// builder read (unk2); Inverted is cull_back, i.e. the opposite of this project's
    /// "the visible side is the CCW loop" reading, so it tests that convention directly.</summary>
    public enum CullOverride { AsData, AllDoubleSided, AllSingleSided, Inverted }

    /// <summary>Normal override. AllFlat derives a geometric normal per fragment from screen
    /// derivatives; AllSmooth re-welds the mesh on the CPU and area-averages; Negated is the
    /// sanity check that shows what genuinely-reversed normals look like on this model.</summary>
    public enum NormalSource { AsData, AllFlat, AllSmooth, Negated }

    /// <summary>--debug-mesh[=spec]: open the panel at launch and preset modes, so one
    /// scripted screenshot exercises the overlays rather than an empty panel. Same role as
    /// --debug-livery / --debug-scoreboard. In a scoped run it also stands in for the M press,
    /// attaching the lab to whatever the run selected. Null = flag absent.</summary>
    public string? DebugSpec { get; init; }

    /// <summary>Whether the panel is shown when the scoped lab attaches. A scripted
    /// <c>--screenshot</c> run turns it off, the same convention the anim lab follows, so the
    /// capture answers a question about the geometry rather than about the UI over it.</summary>
    public bool ShowPanel { get; init; } = true;

    private bool Scoped => _selection != null;

    private int NormalMode => _normals switch
    {
        NormalSource.AllFlat => 1,
        NormalSource.Negated => 2,
        _ => 0, // AsData and AllSmooth both use the mesh's normals (smooth swaps the mesh)
    };

    public override void _Ready()
    {
        if (Scoped)
        {
            // Nothing is collected, drawn or overridden until M attaches the lab to a selection,
            // so an unadorned freecam capture is unchanged by this node existing.
            BuildUi();
            _selection!.Changed += OnSelectionChanged;
            if (DebugSpec != null)
            {
                ApplyDebugSpec(DebugSpec);
                // The scripted twin of pressing M: attach to whatever the run selected. Deferred
                // to the first frame, because --debug-select picks there.
                _scopedPending = true;
            }
            return;
        }
        CollectGeometry();
        BuildDrawNodes();
        SeedLighting();
        BuildUi();
        if (DebugSpec != null)
        {
            ApplyDebugSpec(DebugSpec);
            _ui.Visible = true;
        }
        // Lighting is only written when the flag asked for it. Merely constructing the lab
        // must not re-aim the sun: it did in the first cut (a hardcoded default direction),
        // and the untouched --viewer screenshot stopped being byte-identical, caught by md5.
        RefreshAll(applyLighting: DebugSpec != null);

        for (int i = 0; i < _debugCycles; i++)
        {
            _normals = Cycle(_normals);
            ApplyOverrides();
            Log.Info("ui", $"[mesh] --debug-mesh cycle {i + 1}/{_debugCycles} → source={_normals}");
        }
        if (_debugCycles > 0)
        {
            SyncWidgets();
            UpdateStatus();
        }
    }

    public override void _ExitTree()
    {
        if (_selection != null)
        {
            _selection.Changed -= OnSelectionChanged;
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } k)
            return;
        // Scoped mode binds M and nothing else: the single-letter cyclers below are the same keys
        // the free camera flies on (W/G/C/V), so there they belong to the camera and the modes are
        // panel buttons only.
        if (Scoped)
        {
            if (k.Keycode == Key.M)
            {
                ToggleScoped();
                GetViewport().SetInputAsHandled();
            }
            return;
        }
        switch (k.Keycode)
        {
            case Key.M: _ui.Visible = !_ui.Visible; return;
            case Key.G: _density = Cycle(_density); RebuildNormals(); break;
            case Key.N: _colorMode = Cycle(_colorMode); RebuildNormals(); break;
            case Key.W: _wire = Cycle(_wire); RebuildWireframe(); break;
            case Key.B: _boxes = !_boxes; RebuildBoxes(); break;
            case Key.C: _cull = Cycle(_cull); ApplyOverrides(); break;
            case Key.V: _normals = Cycle(_normals); ApplyOverrides(); break;
            default: return;
        }
        SyncWidgets();
        UpdateStatus();
    }

    public override void _Process(double delta)
    {
        if (_scopedPending)
        {
            // --debug-mesh in a scoped run: attach on the first frame, after --debug-select picked.
            _scopedPending = false;
            ToggleScoped();
            if (_debugRestore)
            {
                // The scripted restore probe: everything applied, then taken straight back off, so
                // the capture must be identical to one with no lab at all.
                ToggleScoped();
            }
        }
        if (_overlayRoot != null && _target != null && IsInstanceValid(_target))
        {
            // The overlays are emitted in the target's own frame and live OUTSIDE its subtree
            // (a drawing parked inside the thing it measures would enter the next box measured
            // over it), so they ride its transform from here instead of inheriting it.
            _overlayRoot.GlobalTransform = _target.GlobalTransform;
        }
        if (!_headlight)
            return;
        // Headlight: the light rides the camera, so every surface facing you is lit by
        // definition and one that stays dark while pointed at you has a bad normal. This is
        // the sharpest normals test the lab has, and it needs no reasoning about geometry.
        var fwd = -_camera.GlobalTransform.Basis.Z;
        AimSun(fwd);
    }

    // ---- static helpers -----------------------------------------------------------------

    private static T Cycle<T>(T value) where T : struct, Enum
    {
        var all = Enum.GetValues<T>();
        int i = Array.IndexOf(all, value);
        return all[(i + 1) % all.Length];
    }

    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (var c in root.GetChildren())
        {
            yield return c;
            foreach (var d in Descendants(c))
                yield return d;
        }
    }

    // ---- provenance ---------------------------------------------------------------------

    private static Vector3 FaceNormal(Surf s, int t)
    {
        var a = s.Verts[s.Tris[t]];
        var b = s.Verts[s.Tris[t + 1]];
        var c = s.Verts[s.Tris[t + 2]];
        var n = (b - a).Cross(c - a);
        return n.LengthSquared() > 1e-14f ? n.Normalized() : Vector3.Up;
    }

    // True when this triangle's three corner normals are equal to each other and to
    // its winding normal, the exact signature of SceneBuilder's flat fallback. See the class
    // doc for the false-positive case.
    private static bool IsFlatFallback(Surf s, int t)
    {
        if (s.Normals.Length == 0)
            return true;
        var n0 = s.Normals[s.Tris[t]];
        var n1 = s.Normals[s.Tris[t + 1]];
        var n2 = s.Normals[s.Tris[t + 2]];
        const float eps = 1e-3f;
        if ((n0 - n1).LengthSquared() > eps || (n0 - n2).LengthSquared() > eps)
            return false;
        return (n0 - FaceNormal(s, t)).LengthSquared() <= eps;
    }

    private static MeshInstance3D MakeDrawNode(string name, Node parent)
    {
        var mi = new MeshInstance3D
        {
            Name = name,
            Mesh = new ImmediateMesh(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        mi.SetMeta(SelectionService.OverlayMeta, true);
        parent.AddChild(mi);
        return mi;
    }

    // ---- wireframe ----------------------------------------------------------------------

    private static (long, long) EdgeKey(Vector3 a, Vector3 b)
    {
        long ka = Quantise(a), kb = Quantise(b);
        return ka <= kb ? (ka, kb) : (kb, ka);
    }

    private static long Quantise(Vector3 v)
    {
        // 0.1 mm buckets: far below any real feature on these models, far above float noise.
        unchecked
        {
            long h = (long)Mathf.Round(v.X * 10000f);
            h = h * 1000003 + (long)Mathf.Round(v.Y * 10000f);
            h = h * 1000003 + (long)Mathf.Round(v.Z * 10000f);
            return h;
        }
    }

    // An edge is a hard smoothing seam when the triangles meeting on it disagree
    // about the normal at a shared position, i.e. the shading breaks across this edge.
    // These outline the flat/smooth patch boundaries directly.
    private static bool IsSeam(Surf s, List<(int Tri, int A, int B)> list)
    {
        if (s.Normals.Length == 0 || list.Count < 2)
            return false;
        var (_, a0, b0) = list[0];
        for (int i = 1; i < list.Count; i++)
        {
            var (_, a1, b1) = list[i];
            // the two triangles may name the edge in either order
            bool sameOrder = Quantise(s.Verts[a0]) == Quantise(s.Verts[a1]);
            var na = s.Normals[sameOrder ? a1 : b1];
            var nb = s.Normals[sameOrder ? b1 : a1];
            if ((s.Normals[a0] - na).LengthSquared() > 1e-4f
                || (s.Normals[b0] - nb).LengthSquared() > 1e-4f)
                return true;
        }
        return false;
    }

    // ---- zone boxes ---------------------------------------------------------------------

    private static Color ColorFor(string part) =>
        PartColors.TryGetValue(part, out var c) ? c : new Color(0.7f, 0.7f, 0.7f);

    // ---- render overrides ----------------------------------------------------------------

    private static string CullToken(BaseMaterial3D.CullModeEnum cull) => cull switch
    {
        BaseMaterial3D.CullModeEnum.Disabled => "cull_disabled",
        BaseMaterial3D.CullModeEnum.Back => "cull_back",
        _ => "cull_front",
    };

    private static string? RewriteShader(string code, string cullToken)
    {
        int rm = code.IndexOf("render_mode", StringComparison.Ordinal);
        int fragment = code.IndexOf("void fragment()", StringComparison.Ordinal);
        if (rm < 0 || fragment < 0)
        {
            return null;
        }
        int rmEnd = code.IndexOf(';', rm);
        int brace = code.IndexOf('{', fragment);
        if (rmEnd < 0 || brace < 0)
        {
            return null;
        }

        string modes = code[rm..rmEnd];
        string replaced = modes;
        bool declared = false;
        foreach (var token in CullTokens)
        {
            if (modes.Contains(token, StringComparison.Ordinal))
            {
                replaced = modes.Replace(token, cullToken);
                declared = true;
                break;
            }
        }
        if (!declared)
        {
            // No sidedness declared ⇒ Godot's default (cull_back). Name it explicitly so the
            // override is the mode the panel says it is.
            replaced = modes + ", " + cullToken;
        }

        // Rewriting NORMAL at the TOP of fragment() rather than the bottom: the fullbright world
        // path derives its LIGHT_STATE lighting normal inside the body, and a rewrite after that
        // would leave the one thing NORMAL still drives in that variant untouched.
        const string inject = @"
    if (csky_lab_normal_mode == 1) {
        // Geometric normal from screen derivatives of the view-space position. Sign follows the
        // rendered side (FRONT_FACING) so it stays predictable under any cull mode.
        vec3 csky_lab_g = normalize(cross(dFdx(VERTEX), dFdy(VERTEX)));
        NORMAL = FRONT_FACING ? csky_lab_g : -csky_lab_g;
    } else if (csky_lab_normal_mode == 2) {
        NORMAL = -NORMAL;
    }
";
        var sb = new System.Text.StringBuilder(code.Length + inject.Length + 64);
        sb.Append(code, 0, rm);
        sb.Append(replaced);
        sb.Append(code, rmEnd, brace + 1 - rmEnd);
        sb.Append(inject);
        sb.Append(code, brace + 1, code.Length - brace - 1);
        // The uniform goes after the render_mode statement, where a shader's own uniforms live.
        sb.Insert(rm + replaced.Length + 1, $"\nuniform int {NormalModeParam} = 0;");
        return sb.ToString();
    }

    // Copies every uniform the source material actually carries onto the derived one. By
    // name, over the shader's own uniform list, so a parameter added to SceneBuilder later is
    // carried without touching this file.
    private static void CopyParameters(Shader shader, ShaderMaterial from, ShaderMaterial to)
    {
        foreach (var entry in shader.GetShaderUniformList())
        {
            var dict = entry.AsGodotDictionary();
            if (!dict.TryGetValue("name", out var nameVar))
            {
                continue;
            }
            string name = nameVar.AsString();
            var value = from.GetShaderParameter(name);
            if (value.VariantType != Variant.Type.Nil)
            {
                to.SetShaderParameter(name, value);
            }
        }
    }

    // ---- smooth-normal rebuild -------------------------------------------------------------

    // Sets (or clears) this surface's override material.
    // ⚠ Do not check the mesh's own surface count here; it errors per call. Godot sizes
    // `surface_override_materials` off the mesh at assignment time, so a mesh whose surfaces were
    // committed afterward (SceneBuilder's pattern) can leave that array short, re-assign the
    // mesh to force a resize. Clearing on a missing slot is a genuine no-op and returns early,
    // since a silently un-overridden surface would wrongly pass this diagnostic's A/B.
    private static void SetOverride(Surf s, Material? mat)
    {
        var mesh = s.Instance.Mesh;
        if (mesh == null || s.Index >= mesh.GetSurfaceCount())
            return;
        if (mat == null && s.Instance.GetSurfaceOverrideMaterialCount() <= s.Index)
            return; // no slot ⇒ nothing was ever set there ⇒ nothing to clear
        if (s.Instance.GetSurfaceOverrideMaterialCount() <= s.Index)
        {
            s.Instance.Mesh = null;
            s.Instance.Mesh = mesh;
        }
        if (s.Instance.GetSurfaceOverrideMaterialCount() > s.Index)
        {
            s.Instance.SetSurfaceOverrideMaterial(s.Index, mat);
        }
        else if (!_warnedOverrideGap)
        {
            _warnedOverrideGap = true;
            Log.Info("ui", $"[mesh] '{s.Instance.Name}' surface {s.Index} has no override slot, override not applied there (results for that surface are not A/B'd)");
        }
    }

    // ---- UI -----------------------------------------------------------------------------------

    private static Label Small(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 11);
        return l;
    }

    private static HSeparator Separator() => new();

    // Adopts the viewer's existing lighting as the lab's starting state instead of
    // inventing one, so the sliders open on what you are actually looking at and an untouched
    // lab writes nothing.
    private void SeedLighting()
    {
        _sunDir = -_sun.GlobalTransform.Basis.Z;
        _sunEnergy = _sun.LightEnergy;
        if (_env != null)
        {
            _ambientEnergy = _env.AmbientLightEnergy;
            _ambientOn = _ambientEnergy > 0f;
        }
    }

    // ---- scoped attach / detach -----------------------------------------------------------

    // M in `--freecam`/`--anim-lab`: attach every overlay and override to the
    // selection's current rung, or take them all back off.
    private void ToggleScoped()
    {
        if (_scopedActive)
        {
            int restored = _surfaces.Count;
            DetachScoped();
            _ui.Visible = false;
            Log.Info("ui", $"mesh lab off, restored {restored} surface(s)");
            return;
        }
        var pick = _selection?.Current;
        if (pick == null)
        {
            // Absence of a subject is not "the subtree is clean", say which it is.
            Log.Info("ui", $"mesh lab: nothing is selected, click an object first (PgUp/PgDn pick the rung)");
            _ui.Visible = ShowPanel;
            _scopedNote = "NOTHING SELECTED, click an object, then M";
            UpdateStatus();
            return;
        }
        AttachScoped(pick);
    }

    private void AttachScoped(Node3D target)
    {
        _target = target;
        _scopedActive = true;
        _scopedNote = "";
        CollectGeometry();
        BuildDrawNodes();
        RefreshAll(applyLighting: false);
        _ui.Visible = ShowPanel;
        var scale = target.GlobalTransform.Basis.Scale;
        Log.Info("ui", $"mesh lab on '{SelectionService.NameOf(target)}': {_surfaces.Count} surface(s), {_triCount} tri(s), {_fullbrightSurfaces} fullbright, {_surfaces.Count - _fullbrightSurfaces} shaded, local radius {BoundingRadius():0.##} m, target scale ({scale.X:0.###},{scale.Y:0.###},{scale.Z:0.###})");
    }

    // Puts the current target back exactly as it was built: original materials, original
    // meshes, no overlay geometry. The mode selections survive, so re-attaching (or moving to
    // another object) re-applies the same comparison.
    private void DetachScoped()
    {
        RestoreSurfaces();
        _overlayRoot?.QueueFree();
        _overlayRoot = null;
        _normalDraw = _wireDraw = _boxDraw = null!;
        _surfaces.Clear();
        _smoothed.Clear();
        _triCount = _flatCount = _twoSidedTris = _fullbrightSurfaces = 0;
        _capped = false;
        _target = null;
        _scopedActive = false;
        if (_labLight != null)
        {
            _labLight.LightEnergy = 0f;
        }
    }

    // The selection moved while the lab was attached: restore the old subtree first, then
    // take the new one. A deselection (Current == null) just restores.
    private void OnSelectionChanged(SelectionService selection, bool freshPick)
    {
        if (!_scopedActive)
        {
            return;
        }
        DetachScoped();
        if (selection.Current is { } next)
        {
            AttachScoped(next);
            return;
        }
        _scopedNote = "selection cleared";
        UpdateStatus();
    }

    // ---- geometry read-back -------------------------------------------------------------

    // Reads every built surface back out of its ArrayMesh into the target's local space.
    // Done once per target: the parked aircraft never moves, and re-reading per toggle would make
    // the cyclers feel sticky on the bigger models. The target must already be in the tree,
    // GlobalTransform on a detached node reads identity and logs an error.
    private void CollectGeometry()
    {
        if (_target == null)
        {
            return;
        }
        var toPlane = _target.GlobalTransform.AffineInverse();
        foreach (var inst in Descendants(_target).OfType<MeshInstance3D>())
        {
            if (inst.Mesh is not ArrayMesh am)
                continue;
            if (inst.HasMeta(SelectionService.OverlayMeta))
            {
                continue; // another tool's drawing, not content
            }
            if (_surfaces.Count >= MaxSurfaces)
            {
                _capped = true;
                break;
            }
            var xf = toPlane * inst.GlobalTransform;
            var nrm = xf.Basis.Inverse().Transposed(); // correct under any non-uniform scale
            for (int s = 0; s < am.GetSurfaceCount(); s++)
            {
                var arrays = am.SurfaceGetArrays(s);
                var vArr = arrays[(int)Mesh.ArrayType.Vertex];
                var nArr = arrays[(int)Mesh.ArrayType.Normal];
                if (vArr.VariantType == Variant.Type.Nil)
                    continue;
                var verts = vArr.AsVector3Array();
                var norms = nArr.VariantType == Variant.Type.Nil
                    ? Array.Empty<Vector3>() : nArr.AsVector3Array();
                var iArr = arrays[(int)Mesh.ArrayType.Index];
                var tris = iArr.VariantType == Variant.Type.Nil
                    ? Enumerable.Range(0, verts.Length).ToArray() : iArr.AsInt32Array();

                var mat = am.SurfaceGetMaterial(s);
                _surfaces.Add(new Surf
                {
                    Instance = inst,
                    Index = s,
                    Verts = verts.Select(v => xf * v).ToArray(),
                    Normals = norms.Select(n => (nrm * n).Normalized()).ToArray(),
                    Tris = tris,
                    // Sidedness is not exposed on the material, but SceneBuilder bakes it into
                    // the generated shader's render_mode, so read it back from the code. Hacky
                    // but honest, and it avoids widening SceneBuilder's API for a debug view.
                    DoubleSided = mat is ShaderMaterial sm && sm.Shader != null
                                  && sm.Shader.Code.Contains("cull_disabled"),
                    // The world's variants render unshaded, so no light of ours reaches them,
                    // the lighting section says so instead of pretending to steer them.
                    Fullbright = mat is ShaderMaterial fb && fb.Shader != null
                                 && fb.Shader.Code.Contains("unshaded"),
                    Original = mat,
                    OriginalMesh = am,
                });
            }
        }
        TallyTriangles();
    }

    private void TallyTriangles()
    {
        _triCount = _flatCount = _twoSidedTris = _fullbrightSurfaces = 0;
        foreach (var s in _surfaces)
        {
            if (s.Fullbright)
            {
                _fullbrightSurfaces++;
            }
        }
        foreach (var s in _surfaces)
            for (int t = 0; t + 2 < s.Tris.Length; t += 3)
            {
                _triCount++;
                if (s.DoubleSided)
                    _twoSidedTris++;
                if (IsFlatFallback(s, t))
                    _flatCount++;
            }
    }

    // ---- overlays -----------------------------------------------------------------------

    private void BuildDrawNodes()
    {
        Node parent = _target!;
        if (Scoped)
        {
            _overlayRoot = new Node3D { Name = "mesh_lab_overlays" };
            _overlayRoot.SetMeta(SelectionService.OverlayMeta, true);
            AddChild(_overlayRoot);
            _overlayRoot.GlobalTransform = _target!.GlobalTransform;
            parent = _overlayRoot;
        }
        _normalDraw = MakeDrawNode("mesh_lab_normals", parent);
        _wireDraw = MakeDrawNode("mesh_lab_wire", parent);
        _boxDraw = MakeDrawNode("mesh_lab_boxes", parent);
    }

    private void RefreshAll(bool applyLighting = true)
    {
        RebuildNormals();
        RebuildWireframe();
        RebuildBoxes();
        ApplyOverrides();
        if (applyLighting)
            ApplyLighting();
        SyncWidgets();
        UpdateStatus();
    }

    private void RebuildNormals()
    {
        if (_normalDraw is not { Mesh: ImmediateMesh im })
        {
            return;
        }
        im.ClearSurfaces();
        if (_density == NormalDensity.Off || _surfaces.Count == 0)
            return;

        float len = Mathf.Max(BoundingRadius() * NormalLenFrac, 0.01f);
        int drawn = 0;
        im.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (var s in _surfaces)
            for (int t = 0; t + 2 < s.Tris.Length; t += 3)
            {
                if (++drawn > MaxOverlayTris)
                {
                    _capped = true;
                    break;
                }
                bool flat = IsFlatFallback(s, t);
                var face = FaceNormal(s, t);
                if (_density == NormalDensity.PerFace)
                {
                    var c = (s.Verts[s.Tris[t]] + s.Verts[s.Tris[t + 1]] + s.Verts[s.Tris[t + 2]]) / 3f;
                    var n = s.Normals.Length == 0 ? face
                        : (s.Normals[s.Tris[t]] + s.Normals[s.Tris[t + 1]] + s.Normals[s.Tris[t + 2]]).Normalized();
                    EmitNormal(im, c, n, face, len, flat, s.DoubleSided);
                }
                else
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = s.Tris[t + k];
                        var n = s.Normals.Length == 0 ? face : s.Normals[vi];
                        EmitNormal(im, s.Verts[vi], n, face, len, flat, s.DoubleSided);
                    }
                }
            }
        im.SurfaceEnd();
    }

    private void EmitNormal(ImmediateMesh im, Vector3 at, Vector3 n, Vector3 face,
        float len, bool flat, bool doubleSided)
    {
        var col = _colorMode switch
        {
            // Provenance: hue = where the normal came from, brightness = sidedness. This is
            // the encoding that answers "is this patch flat-shaded, and is it single-sided?".
            NormalColorMode.Provenance => flat
                ? (doubleSided ? new Color(1f, 0.25f, 0.2f) : new Color(1f, 0.6f, 0.1f))
                : (doubleSided ? new Color(0.35f, 0.5f, 1f) : new Color(0.2f, 0.95f, 0.95f)),
            NormalColorMode.Direction => new Color(n.X * 0.5f + 0.5f, n.Y * 0.5f + 0.5f, n.Z * 0.5f + 0.5f),
            _ => n.Dot(face) < 0f ? new Color(1f, 0.15f, 0.15f) : new Color(0.2f, 1f, 0.3f),
        };
        // Root slightly brighter than the tip so the direction reads without arrowheads.
        im.SurfaceSetColor(col);
        im.SurfaceAddVertex(at);
        im.SurfaceSetColor(col with { A = 0.15f });
        im.SurfaceAddVertex(at + n * len);
    }

    // Half the diagonal of the collected geometry's own bounding box, the subject's SIZE,
    // measured about its own centre rather than about the frame's origin. World subtrees are built
    // with their vertices in absolute coordinates under an identity node transform, so a
    // distance-from-origin radius reads as the object's distance from the map corner: the C1 water
    // tower measured 7,420 m and drew 163 m normal lines across the whole chapter.
    private float BoundingRadius()
    {
        if (_surfaces.Count == 0)
        {
            return 0f;
        }
        var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var s in _surfaces)
            foreach (var v in s.Verts)
            {
                lo = new Vector3(Mathf.Min(lo.X, v.X), Mathf.Min(lo.Y, v.Y), Mathf.Min(lo.Z, v.Z));
                hi = new Vector3(Mathf.Max(hi.X, v.X), Mathf.Max(hi.Y, v.Y), Mathf.Max(hi.Z, v.Z));
            }
        return hi.X < lo.X ? 0f : (hi - lo).Length() * 0.5f;
    }

    // ---- wireframe ----------------------------------------------------------------------

    private void RebuildWireframe()
    {
        if (_wireDraw is not { Mesh: ImmediateMesh im })
        {
            return;
        }
        im.ClearSurfaces();
        if (_wire == WireMode.Off)
            return;

        var ordinary = new Color(0.55f, 0.55f, 0.6f, 0.5f);
        var boundary = new Color(1f, 1f, 1f, 0.85f);
        var seam = new Color(1f, 0.15f, 0.15f, 0.95f);

        int drawn = 0;
        im.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (var s in _surfaces)
        {
            if (drawn > MaxOverlayTris)
            {
                _capped = true;
                break;
            }
            drawn += s.Tris.Length / 3;
            // Keyed by quantised position, not vertex index: SurfaceTool splits a vertex
            // wherever the normal differs, which is the seam this is trying to find.
            var edges = new Dictionary<(long, long), List<(int Tri, int A, int B)>>();
            for (int t = 0; t + 2 < s.Tris.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int ia = s.Tris[t + k], ib = s.Tris[t + (k + 1) % 3];
                    var key = EdgeKey(s.Verts[ia], s.Verts[ib]);
                    if (!edges.TryGetValue(key, out var list))
                        edges[key] = list = new List<(int, int, int)>();
                    list.Add((t, ia, ib));
                }

            foreach (var (_, list) in edges)
            {
                var (_, ia, ib) = list[0];
                Color col;
                if (list.Count == 1)
                    col = boundary;
                else if (IsSeam(s, list))
                    col = seam;
                else
                    col = ordinary;

                bool isSeam = col == seam;
                if (_wire == WireMode.SeamsOnly && !isSeam)
                    continue;
                if (_wire == WireMode.Edges && isSeam)
                    col = ordinary; // plain edges only, no seam highlighting
                im.SurfaceSetColor(col);
                im.SurfaceAddVertex(s.Verts[ia]);
                im.SurfaceAddVertex(s.Verts[ib]);
            }
        }
        im.SurfaceEnd();
    }

    // ---- zone boxes ---------------------------------------------------------------------

    private void RebuildBoxes()
    {
        if (_boxDraw is not { Mesh: ImmediateMesh im })
        {
            return;
        }
        im.ClearSurfaces();
        if (!_boxes || _collider == null)
            return;

        im.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (var part in _collider.Parts)
        {
            // A wing hull straddling the centreline maps to leftwing OR rightwing depending on
            // where it is struck, so a single colour would be a lie: each edge takes the colour
            // of the part its own midpoint maps to, and the ambiguity becomes visible instead.
            foreach (var (a, b) in part.Hull.Edges)
            {
                var pa = part.Local * part.Hull.Points[a];
                var pb = part.Local * part.Hull.Points[b];
                im.SurfaceSetColor(ColorFor(PlaneDamage.MapStruckPart(part.Name, (pa + pb) * 0.5f)));
                im.SurfaceAddVertex(pa);
                im.SurfaceAddVertex(pb);
            }
        }
        im.SurfaceEnd();
    }

    // ---- render overrides ----------------------------------------------------------------

    private void ApplyOverrides()
    {
        bool off = !_forceOverride && _cull == CullOverride.AsData && _normals == NormalSource.AsData;
        foreach (var s in _surfaces)
        {
            if (off)
            {
                SetOverride(s, null);
                RestoreMesh(s);
                continue;
            }
            if (_normals == NormalSource.AllSmooth)
                SmoothMesh(s);
            else
                RestoreMesh(s);
            SetOverride(s, OverrideMaterial(s));
        }
        // Engine wireframe is viewport-wide; the labs' panels are CanvasLayer 2D so they stay
        // readable, and only the 3D view goes to lines.
        GetViewport().DebugDraw = _engineWireframe
            ? Viewport.DebugDrawEnum.Wireframe : Viewport.DebugDrawEnum.Disabled;
    }

    // The override material for one surface: the surface's OWN shader with the cull token
    // swapped and a normal rewrite injected, plus every parameter copied across. Deriving beats
    // re-implementing, the world's fullbright variants carry fog, the sRGB vertex modulate, the
    // LIGHT_STATE point term and UV scroll, and a stand-in shader that dropped any of those would
    // change what you are inspecting instead of only what you asked to test.
    private ShaderMaterial OverrideMaterial(Surf s)
    {
        if (s.Original is ShaderMaterial src && src.Shader is { } shader
            && DerivedShader(shader, CullFor(s)) is { } derived)
        {
            var mat = new ShaderMaterial { Shader = derived };
            CopyParameters(shader, src, mat);
            mat.SetShaderParameter(NormalModeParam, NormalMode);
            return mat;
        }
        // A surface whose material is not one of ours (or whose shader we could not read): fall
        // back to the replica of SceneBuilder's shaded stage, and say so rather than skipping it.
        if (!_warnedDeriveGap)
        {
            _warnedDeriveGap = true;
            Log.Info("ui", $"mesh lab: '{s.Instance.Name}' surface {s.Index} has no readable shader, overriding it through the replica shader (fog/scroll/alpha terms are not carried there)");
        }
        bool textured = s.Original is ShaderMaterial osm
                        && osm.GetShaderParameter("albedo_tex").VariantType != Variant.Type.Nil;
        var fallback = new ShaderMaterial { Shader = GetOverrideShader(CullFor(s), textured) };
        if (s.Original is ShaderMaterial plain)
        {
            if (textured)
                fallback.SetShaderParameter("albedo_tex", plain.GetShaderParameter("albedo_tex"));
            else
                fallback.SetShaderParameter("albedo_color", plain.GetShaderParameter("albedo_color"));
            // Carry the depth bias so overridden surfaces keep their coplanar layering, an
            // override must differ in the thing under test and nothing else.
            fallback.SetShaderParameter("depth_bias", plain.GetShaderParameter("depth_bias"));
        }
        fallback.SetShaderParameter("normal_mode", NormalMode);
        return fallback;
    }

    // Rewrites a built shader into the lab's A/B twin: the cull token in its
    // `render_mode` becomes `cull`, and `fragment()` opens with the
    // normal-source rewrite. Everything else, vertex stage, fog, lights, alpha, the instance
    // uniform block and its declaration order, is the original text. Null when the code does not
    // have the two anchors, which is the caller's cue to fall back rather than guess.
    private Shader? DerivedShader(Shader source, BaseMaterial3D.CullModeEnum cull)
    {
        var key = (source.GetInstanceId(), (int)cull);
        if (_derived.TryGetValue(key, out var cached))
        {
            return cached;
        }
        string? code = RewriteShader(source.Code, CullToken(cull));
        var shader = code == null ? null : new Shader { Code = code };
        _derived[key] = shader;
        return shader;
    }

    private BaseMaterial3D.CullModeEnum CullFor(Surf s) => _cull switch
    {
        CullOverride.AllDoubleSided => BaseMaterial3D.CullModeEnum.Disabled,
        CullOverride.AllSingleSided => BaseMaterial3D.CullModeEnum.Front,
        CullOverride.Inverted => BaseMaterial3D.CullModeEnum.Back,
        _ => s.DoubleSided ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Front,
    };

    private Shader GetOverrideShader(BaseMaterial3D.CullModeEnum cull, bool textured)
    {
        int key = (int)cull * 2 + (textured ? 1 : 0);
        if (_overrideShaders.TryGetValue(key, out var cached))
            return cached;
        string mode = cull switch
        {
            BaseMaterial3D.CullModeEnum.Disabled => "cull_disabled",
            BaseMaterial3D.CullModeEnum.Back => "cull_back",
            _ => "cull_front",
        };
        // Vertex stage is SceneBuilder's, verbatim (see the class doc): same
        // skip_vertex_transform + scale-toward-eye depth bias, so only the tested thing differs.
        string code = $@"
shader_type spatial;
render_mode skip_vertex_transform, {mode};
uniform float depth_bias = 0.0;
// Reads only node_bias, but declares the canonical order like every other shader: this
// diagnostic's contract is that its vertex stage matches SceneBuilder's verbatim.
#include ""res://shaders/csky_instance_uniforms.gdshaderinc""
uniform int normal_mode = 0;
{(textured
    ? "uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;\n"
      + Mech3.SceneBuilder.MipBiasInclude
    : "uniform vec4 albedo_color : source_color = vec4(1.0);")}

void vertex() {{
    // Leading minus cancels the engine's back-face normal flip (cull_front on aircraft), so
    // AsData matches the shipped renderer and Negated gives the inside-out sanity view.
    VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
    NORMAL = -normalize(MODELVIEW_NORMAL_MATRIX * NORMAL);
    VERTEX *= 1.0 - (depth_bias + node_bias);
}}

void fragment() {{
    vec4 col = COLOR * {(textured ? Mech3.SceneBuilder.SampleAlbedo("UV") : "albedo_color")};
    ALBEDO = col.rgb;
    ROUGHNESS = 0.85;
    METALLIC = 0.0;
    SPECULAR = 0.5;
    if (normal_mode == 1) {{
        // Geometric normal from screen derivatives of the view-space position. Sign follows
        // the rendered side (FRONT_FACING) so it stays predictable under any cull mode.
        vec3 g = normalize(cross(dFdx(VERTEX), dFdy(VERTEX)));
        NORMAL = FRONT_FACING ? g : -g;
    }} else if (normal_mode == 2) {{
        NORMAL = -NORMAL;
    }}
}}";
        var shader = new Shader { Code = code };
        _overrideShaders[key] = shader;
        return shader;
    }

    // ---- smooth-normal rebuild -------------------------------------------------------------

    // Swaps in a copy of this surface's mesh with area-weighted vertex normals,
    // welded by position, the "what if every polygon were smooth-shaded" comparison. Cached,
    // so cycling back to it is instant.
    private void SmoothMesh(Surf s)
    {
        if (s.Instance.Mesh is ArrayMesh cur && _smoothed.TryGetValue(s, out var done) && cur == done)
            return;
        if (!_smoothed.TryGetValue(s, out var smooth))
        {
            var acc = new Dictionary<long, Vector3>();
            for (int t = 0; t + 2 < s.Tris.Length; t += 3)
            {
                var a = s.Verts[s.Tris[t]];
                var b = s.Verts[s.Tris[t + 1]];
                var c = s.Verts[s.Tris[t + 2]];
                var w = (b - a).Cross(c - a); // unnormalised == area-weighted
                foreach (var v in stackalloc[] { a, b, c })
                {
                    long k = Quantise(v);
                    acc[k] = acc.TryGetValue(k, out var prev) ? prev + w : w;
                }
            }
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            for (int t = 0; t + 2 < s.Tris.Length; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int vi = s.Tris[t + k];
                    var v = s.Verts[vi];
                    var n = acc[Quantise(v)];
                    st.SetNormal(n.LengthSquared() > 1e-12f ? n.Normalized() : FaceNormal(s, t));
                    st.AddVertex(v);
                }
            smooth = st.Commit();
            _smoothed[s] = smooth;
        }
        // Multi-surface instances keep their original mesh; a triangle-less surface commits
        // to a 0-surface mesh, which would leave nothing to render or override.
        if (s.OriginalMesh is ArrayMesh am && am.GetSurfaceCount() == 1
            && smooth.GetSurfaceCount() == 1)
            s.Instance.Mesh = smooth;
    }

    private void RestoreMesh(Surf s)
    {
        if (s.OriginalMesh != null && IsInstanceValid(s.Instance) && s.Instance.Mesh != s.OriginalMesh)
        {
            s.Instance.Mesh = s.OriginalMesh;
        }
    }

    // Puts every collected surface back on its original material and mesh. The exact
    // restore the scoped lab owes on M-off, on a selection change and on teardown; a surface whose
    // node has since been freed (a destructible swapping to its wreck) is simply skipped.
    private void RestoreSurfaces()
    {
        foreach (var s in _surfaces)
        {
            if (!IsInstanceValid(s.Instance))
            {
                continue;
            }
            SetOverride(s, null);
            RestoreMesh(s);
        }
        if (_engineWireframe && IsInstanceValid(GetViewport()))
        {
            GetViewport().DebugDraw = Viewport.DebugDrawEnum.Disabled;
        }
    }

    // ---- lighting -------------------------------------------------------------------------

    private void ApplyLighting()
    {
        if (Scoped)
        {
            ApplyScopedLighting();
            return;
        }
        _sun.LightEnergy = _sunEnergy;
        if (!_headlight)
            AimSun(_sunDir);
        if (_env != null)
            _env.AmbientLightEnergy = _ambientOn ? _ambientEnergy : 0f;
    }

    private void ApplyScopedLighting()
    {
        if (_labLight == null)
        {
            _labLight = new DirectionalLight3D
            {
                Name = "mesh_lab_light",
                LightEnergy = 0f,
                ShadowEnabled = false,
            };
            _labLight.SetMeta(SelectionService.OverlayMeta, true);
            AddChild(_labLight);
        }
        _labLight.LightEnergy = _sunEnergy;
        if (!_headlight)
            AimSun(_sunDir);
    }

    private void AimSun(Vector3 dir)
    {
        var light = Scoped ? _labLight : _sun;
        if (light == null || dir.LengthSquared() < 1e-6f)
        {
            return;
        }
        dir = dir.Normalized();
        // LookAt degenerates when the direction is parallel to the up hint, pick another.
        var up = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.999f ? Vector3.Forward : Vector3.Up;
        light.LookAtFromPosition(light.GlobalPosition, light.GlobalPosition + dir, up);
    }

    // ---- --debug-mesh spec ------------------------------------------------------------------

    // Presets modes from the flag's comma-separated spec, e.g.
    // `--debug-mesh=normals,wire=seams,cull=double`. Unknown tokens are reported rather
    // than ignored, so a typo in a scripted screenshot does not silently shoot the wrong thing.
    private void ApplyDebugSpec(string spec)
    {
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim().ToLowerInvariant();
            string key = token, value = "";
            int eq = token.IndexOf('=');
            if (eq >= 0)
            {
                key = token[..eq];
                value = token[(eq + 1)..];
            }
            switch (key)
            {
                case "": break;
                case "normals": _density = value == "corners" ? NormalDensity.PerCorner : NormalDensity.PerFace; break;
                case "color":
                    _colorMode = value switch
                    {
                        "direction" => NormalColorMode.Direction,
                        "winding" => NormalColorMode.Winding,
                        _ => NormalColorMode.Provenance,
                    }; break;
                case "wire":
                    _wire = value switch
                    {
                        "seams" => WireMode.EdgesAndSeams,
                        "seamsonly" => WireMode.SeamsOnly,
                        _ => WireMode.Edges,
                    }; break;
                case "boxes": _boxes = true; break;
                case "force": _forceOverride = true; break;
                case "restore":
                    // Scoped runs only: attach with everything the spec asked for, then detach
                    // again on the same frame. The scripted twin of M-on-M-off, and the only way
                    // to prove the restore is exact, the capture must match a run with no lab.
                    _debugRestore = true;
                    break;
                case "headlight": _headlight = true; break;
                case "enginewire": _engineWireframe = true; break;
                case "cull":
                    _cull = value switch
                    {
                        "double" => CullOverride.AllDoubleSided,
                        "single" => CullOverride.AllSingleSided,
                        "inverted" => CullOverride.Inverted,
                        _ => CullOverride.AsData,
                    }; break;
                case "source":
                    _normals = value switch
                    {
                        "flat" => NormalSource.AllFlat,
                        "smooth" => NormalSource.AllSmooth,
                        "negated" => NormalSource.Negated,
                        _ => NormalSource.AsData,
                    }; break;
                case "ambient": _ambientOn = value != "off"; break;
                case "cycle":
                    // cycle=N, step the normal-source cycler N times at launch (the headless
                    // equivalent of clicking it), reaching a second ApplyOverrides after a mesh
                    // swap that a one-shot spec would otherwise never exercise.
                    if (int.TryParse(value, out int n))
                        _debugCycles = n;
                    break;
                case "dir":
                    // dir=x/y/z, the direction the light TRAVELS, so dir=0/-1/0 is straight
                    // down. Slashes, because commas already separate spec tokens.
                    var parts = value.Split('/');
                    if (parts.Length == 3
                        && float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float dx)
                        && float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float dy)
                        && float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float dz))
                        _sunDir = new Vector3(dx, dy, dz);
                    else
                        Log.Info("ui", $"[mesh] --debug-mesh: bad dir '{value}' (want dir=x/y/z)");
                    break;
                case "sun":
                    if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float e)) _sunEnergy = e; break;
                default: Log.Info("ui", $"[mesh] --debug-mesh: unknown token '{raw.Trim()}'"); break;
            }
        }
        Log.Info("ui", $"[mesh] --debug-mesh: normals={_density}/{_colorMode} wire={_wire} boxes={_boxes} cull={_cull} source={_normals} headlight={_headlight}");
    }

    // ---- UI -----------------------------------------------------------------------------------

    private void BuildUi()
    {
        // Hidden by default, like the other two labs, so an unadorned --viewer screenshot is
        // unchanged. Anchored bottom-left: DamageLab owns top-left, LiveryLab top-right.
        _ui = new CanvasLayer { Layer = 1, Visible = false };
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var panel = new PanelContainer { SelfModulate = new Color(1, 1, 1, 0.85f) };
        panel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        panel.GrowVertical = Control.GrowDirection.Begin;
        panel.Position = new Vector2(8, -8);

        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 10);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);

        box.AddChild(new Label { Text = Scoped ? "MESH LAB: SELECTION" : "MESH LAB" });
        box.AddChild(Small(Scoped
            ? "M detaches (restores the subtree) · click / PgUp / PgDn re-target it"
            : "M hides this panel · F19 damage lab · L livery lab"));
        _countsLabel = Small("");
        box.AddChild(_countsLabel);
        box.AddChild(Separator());

        // The single-letter shortcuts exist only in the viewer: in the scoped modes those keys fly
        // the camera, so there the buttons are the whole interface.
        _normalsBtn = CycleRow(box, Scoped ? "normals" : "normals  [G]", () => { _density = Cycle(_density); RebuildNormals(); });
        _colorBtn = CycleRow(box, Scoped ? "colour" : "colour   [N]", () => { _colorMode = Cycle(_colorMode); RebuildNormals(); });
        box.AddChild(Small("cyan/blue = from file (1/2-sided)"));
        box.AddChild(Small("orange/red = FLAT FALLBACK (1/2-sided)"));
        _wireBtn = CycleRow(box, Scoped ? "wireframe" : "wireframe [W]", () => { _wire = Cycle(_wire); RebuildWireframe(); });
        if (!Scoped)
        {
            // Zone boxes are the aircraft's damage-collision boxes, and the engine wireframe is
            // viewport-wide, neither is a property of a selected world subtree.
            _boxBtn = CheckRow(box, "zone boxes  [B]", v => { _boxes = v; RebuildBoxes(); });
            _engineWireBtn = CheckRow(box, "engine wireframe", v => { _engineWireframe = v; ApplyOverrides(); });
        }

        box.AddChild(Separator());
        box.AddChild(Small("OVERRIDES: A/B the render decisions"));
        _cullBtn = CycleRow(box, Scoped ? "cull" : "cull     [C]", () => { _cull = Cycle(_cull); ApplyOverrides(); });
        _sourceBtn = CycleRow(box, Scoped ? "normal src" : "normal src [V]", () => { _normals = Cycle(_normals); ApplyOverrides(); });

        box.AddChild(Separator());
        box.AddChild(Small("LIGHTING"));
        if (Scoped)
        {
            // Scoped lighting drives the lab's OWN light. The world's sun and ambient are the
            // world's; steering them to inspect one building would re-light everything else.
            box.AddChild(Small("the lab's own light, the world sun is not touched"));
            _lightNote = Small("");
            box.AddChild(_lightNote);
        }
        else
        {
            _ambientBtn = CheckRow(box, "ambient", v => { _ambientOn = v; ApplyLighting(); });
        }
        _headlightBtn = CheckRow(box, "headlight (light = camera)", v => { _headlight = v; ApplyLighting(); });
        if (!Scoped)
        {
            SliderRow(box, "ambient energy", 0f, 3f, _ambientEnergy, v => { _ambientEnergy = v; ApplyLighting(); });
        }
        SliderRow(box, Scoped ? "light energy" : "sun energy", 0f, 6f, _sunEnergy, v => { _sunEnergy = v; ApplyLighting(); });
        string[] axis = { "dir X", "dir Y", "dir Z" };
        for (int i = 0; i < 3; i++)
        {
            int ax = i;
            SliderRow(box, axis[i], -1f, 1f, _sunDir[i], v =>
            {
                var d = _sunDir;
                d[ax] = v;
                _sunDir = d;
                _headlight = false;
                SyncWidgets();
                ApplyLighting();
            });
        }

        box.AddChild(Separator());
        _statusLabel = Small("");
        box.AddChild(_statusLabel);

        margin.AddChild(box);
        panel.AddChild(margin);
        root.AddChild(panel);
        _ui.AddChild(root);
        AddChild(_ui);
    }

    private Button CycleRow(VBoxContainer parent, string label, Action onPress)
    {
        var row = new HBoxContainer();
        var l = new Label { Text = label, CustomMinimumSize = new Vector2(112, 0) };
        row.AddChild(l);
        var b = new Button { CustomMinimumSize = new Vector2(190, 0) };
        b.Pressed += () =>
        {
            onPress();
            SyncWidgets();
            UpdateStatus();
        };
        row.AddChild(b);
        parent.AddChild(row);
        return b;
    }

    private CheckButton CheckRow(VBoxContainer parent, string label, Action<bool> onToggle)
    {
        var c = new CheckButton { Text = label };
        c.Toggled += v =>
        {
            if (_suppressCallbacks)
                return;
            onToggle(v);
            UpdateStatus();
        };
        parent.AddChild(c);
        return c;
    }

    private void SliderRow(VBoxContainer parent, string label, float min, float max,
        float value, Action<float> onChange)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(112, 0) });
        var s = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = 0.01,
            Value = value,
            CustomMinimumSize = new Vector2(190, 0),
        };
        var read = Small(value.ToString("0.00"));
        s.ValueChanged += v =>
        {
            read.Text = ((float)v).ToString("0.00");
            if (!_suppressCallbacks)
                onChange((float)v);
        };
        row.AddChild(s);
        row.AddChild(read);
        parent.AddChild(row);
    }

    private void SyncWidgets()
    {
        _suppressCallbacks = true;
        _normalsBtn.Text = _density.ToString();
        _colorBtn.Text = _colorMode.ToString();
        _wireBtn.Text = _wire.ToString();
        _cullBtn.Text = _cull.ToString();
        _sourceBtn.Text = _normals.ToString();
        _headlightBtn.ButtonPressed = _headlight;
        if (_boxBtn != null)
        {
            _boxBtn.ButtonPressed = _boxes;
        }
        if (_ambientBtn != null)
        {
            _ambientBtn.ButtonPressed = _ambientOn;
        }
        if (_engineWireBtn != null)
        {
            _engineWireBtn.ButtonPressed = _engineWireframe;
        }
        _suppressCallbacks = false;
    }

    private void UpdateStatus()
    {
        string subject = Scoped
            ? (_target != null && IsInstanceValid(_target)
                ? $"'{SelectionService.NameOf(_target)}' · " : "")
            : "";
        _countsLabel.Text = subject + $"{_surfaces.Count} surfaces · {_triCount} tris · "
                            + $"{_flatCount} flat ({Pct(_flatCount)}) · {_twoSidedTris} 2-sided ({Pct(_twoSidedTris)})"
                            + (_capped ? $" · CAPPED at {MaxSurfaces} surfaces / {MaxOverlayTris} drawn tris" : "");
        if (_lightNote != null)
        {
            // A light that cannot reach the subject is worse than no light control: it looks like
            // a measurement. Say which case this target is.
            _lightNote.Text = _surfaces.Count == 0 ? ""
                : _fullbrightSurfaces == _surfaces.Count
                    ? "target is fullbright (unshaded), no light reaches it"
                    : $"{_surfaces.Count - _fullbrightSurfaces} of {_surfaces.Count} surfaces are lit";
        }
        _statusLabel.Text = _scopedNote.Length > 0 ? _scopedNote
            : !_forceOverride && _cull == CullOverride.AsData && _normals == NormalSource.AsData
                ? "render: shipped path"
                : $"render: OVERRIDDEN ({_cull}, {_normals})";
    }

    private string Pct(int n) => _triCount > 0 ? $"{n * 100f / _triCount:0}%" : "-";

    // One committed surface, flattened into plane-local space. Indices are always
    // materialised (SurfaceTool commits indexed, but an unindexed surface is legal).
    private sealed class Surf
    {
        public required MeshInstance3D Instance;
        public required int Index;
        public required Vector3[] Verts;
        public required Vector3[] Normals;
        public required int[] Tris;
        public required bool DoubleSided;
        public required bool Fullbright;
        public required Material? Original;
        public Mesh? OriginalMesh;
    }
}
