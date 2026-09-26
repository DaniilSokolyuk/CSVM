using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Labs;

/// <summary>
/// The weapon lab's panel (<c>--weapon-lab</c>, key B): a configurator for the held aircraft's
/// live loadout. It owns no weapon and fires nothing; every stepper writes into the
/// <see cref="Flight.Airframe.FlightController"/>'s bound <see cref="Loadout"/>, and the aircraft's own
/// trigger fires it. Guns arm the plane's gun groups, hardpoint weapons arm its pylons; picking a
/// hardpoint weapon re-arms every pylon and rebuilds the mounted ordnance models. The mount
/// stepper drives <see cref="Flight.Airframe.FlightController.SelectGunGroup"/> and
/// <see cref="Flight.Airframe.FlightController.SelectPylon"/>, and "reset to stock" restores the launch
/// fit. In a lab session the bound loadout is <see cref="Loadout.ForRig"/>'s, so any weapon
/// reaches any mount without editing <c>stock_loadouts.json</c>. Full decode, including click-to-
/// place: docs/architecture.md.
/// </summary>
public sealed partial class WeaponLab : Node3D
{
    private const int Guns = 0;
    private const int Hardpoints = 1;

    private const float MinStandoff = 15f;
    private const float MaxStandoff = 1100f;      // far enough to watch a rocket fly its full course
    private const float DefaultStandoff = 90f;
    private const float RayLength = 20000f;       // past the far side of the largest chapter
    private const float ClearanceProbeStart = 0.5f;  // m off the struck face, so the back-ray leaves it
    private const float ClearanceMargin = 2f;     // m kept clear of whatever the back-ray struck

    // Where --weapon-camera=<frames> parks the free camera relative to the eye it took over, so the
    // hand-back it measures is from somewhere the orbit did not put it.
    private static readonly Vector3 ScriptedFreeCameraOffset = new(30f, 15f, 30f);

    private readonly Node3D _plane;
    private readonly string _planeModel;
    private readonly WeaponDefs _weapons;
    private readonly Camera3D? _camera;

    private readonly List<WeaponDef> _gunWeapons = new();
    private readonly List<WeaponDef> _rocketWeapons = new();
    private readonly List<Mount> _gunMounts = new();
    private readonly List<Mount> _pylonMounts = new();

    // The flight session's held aircraft this panel drives, or null for the parked
    // (--weapon-test) host. Set means there IS a live loadout to write into.
    private readonly Flight.Airframe.FlightController? _host;
    private readonly Loadout? _loadout;

    // The fit the session launched with (stock, or whatever --rocket/--loadout made it), captured
    // before the panel touches anything, what "reset to stock" restores.
    private readonly Dictionary<GunGroup, WeaponDef> _launchGuns = new();
    private WeaponDef? _launchOrdnance;

    private int _bank = Guns;
    private int _weaponIndex;       // into the current bank's weapon list
    private int _mountIndex;        // into the current bank's mount list

    private bool _panel;
    private bool _autoFire;
    private int _cycleTick;

    // Click-to-place: the click is taken in _UnhandledInput but the ray is cast in the physics
    // step, the space state may not be queried while the server is flushing queries.
    private Vector2? _pendingClick;
    private bool _pendingAimOnly;
    private bool _scriptedPlacementDone;
    private float _standoff = DefaultStandoff;
    private string _pickLine = "target: (nothing picked yet)";
    private MeshInstance3D? _marker;

    // Camera hand-off (V): the free camera exists only while it owns the view.
    private bool _freeCamera;
    private Flight.Camera.SpectatorCamera? _spectator;
    private int _cameraTick;
    private Vector3? _settleEye;

    private CanvasLayer _ui = null!;
    private Label _bankLabel = null!;
    private Label _weaponLabel = null!;
    private Label _weaponDetail = null!;
    private Label _mountLabel = null!;
    private Label _ammoLabel = null!;
    private Label _pickLabel = null!;
    private Label _standoffLabel = null!;
    private Label _cameraLabel = null!;
    private Label _cliLabel = null!;
    private CheckButton _autoFireToggle = null!;
    private CheckButton _infiniteAmmoToggle = null!;
    private bool _suppress; // set while rewriting widgets from a state change

    // In splitscreen the caller builds one lab on rig 0's aircraft/camera only, by design,
    // the other rigs fly with no panel and no camera hand-off.
    public WeaponLab(Node3D plane, WeaponDefs weapons, Loadout? loadout, string planeModel,
        Flight.Airframe.FlightController? host = null, Camera3D? camera = null)
    {
        _plane = plane;
        _weapons = weapons;
        _camera = camera;
        _planeModel = planeModel;
        _host = host;
        _loadout = loadout;
        foreach (var w in weapons.All)
        {
            (w.IsGun ? _gunWeapons : _rocketWeapons).Add(w);
        }
        BuildMounts(loadout);
        RememberLaunchFit(loadout);
        Name = "weapon_lab";
    }

    /// <summary><c>--weapon-lab</c>: open the panel at launch.</summary>
    public bool DebugShow { get; init; }

    /// <summary><c>--weapon-lab=&lt;wep_id&gt;</c>: the weapon mounted at launch (sets the bank).</summary>
    public string? InitialWeapon { get; init; }

    /// <summary><c>--weapon-mount=&lt;name&gt;</c>: the mount selected at launch.</summary>
    public string? InitialMount { get; init; }

    /// <summary><c>--weapon-fire</c>: hold the aircraft's trigger from launch, the bank decides
    /// which one (guns or rockets).</summary>
    public bool AutoFireAtStart { get; init; }

    /// <summary><c>--weapon-cycle=N</c>: step the current bank's weapon list one entry every N
    /// physics frames, the scripted twin of holding down the panel's <c>&gt;</c> button, so the
    /// mount/ordnance-rebuild path can be walked headlessly under <c>--log=weapons</c>. 0 = off.</summary>
    public int CycleFrames { get; init; }

    /// <summary><c>--weapon-click[=x,y]</c>: replay one left click at that viewport pixel on the
    /// first physics frame (the viewport centre when the value is omitted), the scripted twin of
    /// click-to-place, so a capture can aim at a real surface with nobody at the mouse. Null = off.
    /// </summary>
    public Vector2? DebugClick { get; init; }

    /// <summary>Whether <see cref="DebugClick"/> was given at all, the value is optional, so a
    /// null <see cref="DebugClick"/> still means "click the centre" when this is set.</summary>
    public bool DebugClickRequested { get; init; }

    /// <summary><c>--weapon-click=x,y,aim</c>: the scripted shift-click, aim at the struck point
    /// without moving the aircraft.</summary>
    public bool DebugClickAimOnly { get; init; }

    /// <summary><c>--weapon-target=x,y,z</c>: park facing that world point on the first physics
    /// frame. Wins over <see cref="DebugSurface"/> and <see cref="DebugClick"/>, it is the most
    /// specific of the three.</summary>
    public Vector3? DebugTarget { get; init; }

    /// <summary><c>--weapon-surface=&lt;registry name&gt;</c>: park facing the nearest collider
    /// carrying that surface id, on the first physics frame.</summary>
    public string? DebugSurface { get; init; }

    /// <summary><c>--weapon-standoff=&lt;m&gt;</c>: the parking distance every placement uses, and
    /// the panel slider's starting value. 0 keeps the 90 m default.</summary>
    public float StandoffAtStart { get; init; }

    /// <summary><c>--weapon-camera=free</c>: start with the view already handed to the free camera,
    /// so a capture can be framed from it.</summary>
    public bool FreeCameraAtStart { get; init; }

    /// <summary><c>--weapon-camera=&lt;N&gt;</c>: hand the camera over and back every N physics
    /// frames, the scripted twin of tapping <b>V</b>, which is how the no-jump hand-back is
    /// checked with nobody at the controls. 0 = off.</summary>
    public int CameraToggleFrames { get; init; }

    private List<WeaponDef> BankWeapons => _bank == Guns ? _gunWeapons : _rocketWeapons;
    private List<Mount> BankMounts => _bank == Guns ? _gunMounts : _pylonMounts;
    private WeaponDef? SelectedWeapon => _weaponIndex < BankWeapons.Count ? BankWeapons[_weaponIndex] : null;
    private Mount? SelectedMount => _mountIndex < BankMounts.Count ? BankMounts[_mountIndex] : null;

    public override void _Ready()
    {
        if (StandoffAtStart > 0f)
        {
            _standoff = Mathf.Clamp(StandoffAtStart, MinStandoff, MaxStandoff);
        }
        BuildUi();
        ResolveInitialSelection();
        _panel = DebugShow;
        ApplyMount();
        ApplyWeapon();
        SetAutoFire(AutoFireAtStart);
        SyncState();
    }

    public override void _PhysicsProcess(double delta)
    {
        // Both of this node's per-frame jobs are picks and swaps, never a shot: firing belongs to
        // the aircraft's trigger and there is deliberately no second spawn path here.
        if (!_scriptedPlacementDone && _host != null)
        {
            // Deferred to the first physics frame, as the selection service's scripted pick is:
            // the camera pose and the world's colliders are only final once the session is built.
            // Most specific wins: an explicit point, then a surface class, then a screen pixel.
            _scriptedPlacementDone = true;
            if (DebugTarget is { } target)
            {
                Log.Info("ui", $"weapon lab: scripted target ({target.X:0.0},{target.Y:0.0},{target.Z:0.0})");
                PlaceOnTarget(target);
            }
            else if (DebugSurface is { } surface)
            {
                PlaceOnNearestSurface(surface);
            }
            else if (DebugClickRequested && _camera != null)
            {
                var screen = DebugClick ?? (_camera.GetViewport().GetVisibleRect().Size * 0.5f);
                Log.Info("ui", $"weapon lab: scripted click at ({screen.X:0},{screen.Y:0}){(DebugClickAimOnly ? " (aim only)" : "")}");
                PickAt(screen, DebugClickAimOnly);
            }
            // After the placement, never before: the free camera freezes the eye where it takes
            // over (that is the point of CameraOwned), so handing it the view first would leave a
            // --weapon-camera=free capture staring at the spawn the aircraft just left.
            if (FreeCameraAtStart)
            {
                SetFreeCamera(true);
            }
        }
        if (_pendingClick is { } click)
        {
            _pendingClick = null;
            PickAt(click, _pendingAimOnly);
        }
        if (_settleEye is { } handoff && _camera != null)
        {
            // One frame after a hand-off, where a jump would show: the new owner has had a full
            // camera update, so this is the number that proves the hand-back is seamless.
            _settleEye = null;
            var now = _camera.GlobalPosition;
            Log.Info("ui", $"weapon lab: camera settled at ({now.X:0.0},{now.Y:0.0},{now.Z:0.0}), moved {now.DistanceTo(handoff):0.00} m from the hand-off");
        }
        if (CameraToggleFrames > 0 && ++_cameraTick >= CameraToggleFrames)
        {
            _cameraTick = 0;
            SetFreeCamera(!_freeCamera);
        }
        if (CycleFrames <= 0 || _host == null || ++_cycleTick < CycleFrames)
        {
            return;
        }
        _cycleTick = 0;
        StepWeapon(1);
    }

    // ---- input -------------------------------------------------------------------------------

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        // B, not W: this is a flight session, and W is pitch. The plane is held and reads no stick
        // input, but the panel key must not be one a pilot's hand rests on.
        if (@event is not InputEventKey { Echo: false, Pressed: true } key)
        {
            return;
        }
        // B, not W: this is a flight session, and W is pitch. The plane is held and reads no stick
        // input, but the panel key must not be one a pilot's hand rests on.
        if (key.Keycode == Key.B)
        {
            _panel = !_panel;
            SyncState();
        }
        else if (key.Keycode == Key.V)
        {
            SetFreeCamera(!_freeCamera);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // Unhandled only: a click on the panel's own buttons is consumed by the GUI and never
        // reaches here, so the steppers do not also re-park the aircraft.
        if (_camera == null || @event is not InputEventMouseButton
            {
                Pressed: true, ButtonIndex: MouseButton.Left,
            } click)
        {
            return;
        }
        // Read the position off the CAMERA's viewport rather than the event: in splitscreen the
        // event carries window coordinates while the ray projection wants the pane's own.
        _pendingClick = _camera.GetViewport().GetMousePosition();
        _pendingAimOnly = click.ShiftPressed;
        GetViewport().SetInputAsHandled();
    }

    // ---- weapon + mount resolution -----------------------------------------------------------

    // The raw firepoint/pylon marker nodes, each with its ordinal, sorted, the
    // no-loadout fallback for a plane the stock table omits.
    private static void CollectRawMarkers(Node3D plane,
        out List<(int Ord, Node3D Node)> firepoints, out List<(int Ord, Node3D Node)> pylons)
    {
        var fp = new List<(int, Node3D)>();
        var py = new List<(int, Node3D)>();
        void Walk(Node node)
        {
            foreach (var child in node.GetChildren())
            {
                if (child is Node3D n3d && n3d.HasMeta(AnimRuntime.NameMeta)
                    && MarkerRig.Classify(n3d.GetMeta(AnimRuntime.NameMeta).AsString(), out var kind, out int ord))
                {
                    if (kind == MarkerRig.MarkerKind.Firepoint)
                    {
                        fp.Add((ord, n3d));
                    }
                    else if (kind == MarkerRig.MarkerKind.Pylon)
                    {
                        py.Add((ord, n3d));
                    }
                }
                Walk(child);
            }
        }
        Walk(plane);
        fp.Sort(static (a, b) => a.Item1.CompareTo(b.Item1));
        py.Sort(static (a, b) => a.Item1.CompareTo(b.Item1));
        firepoints = fp;
        pylons = py;
    }

    private static int IndexOfWeapon(List<WeaponDef> list, string id)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    // Decimals are formatted invariantly (a dot, never a locale comma), like the dump tools.
    private static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string WeaponSummary(WeaponDef w)
    {
        var parts = new List<string>();
        if (w.Caliber is { } cal)
        {
            parts.Add($".{cal}");
        }
        if (w.FireRate > 0f)
        {
            parts.Add($"{F(w.FireRate)}/s");
        }
        if (w.Velocity is { } v)
        {
            parts.Add($"{v:0} m/s");
        }
        if (w.HealthDamage is { } hd)
        {
            parts.Add($"dmg h{F(hd)}/a{F(w.ArmorDamage ?? 0f)}");
        }
        else if (w.Damage is { } dm)
        {
            parts.Add($"dmg {F(dm)}");
        }
        if (w.ClusterSize is { } cs)
        {
            parts.Add($"×{cs}");
        }
        if (w.Range is { } r)
        {
            parts.Add($"range {r:0}");
        }
        foreach (var flag in new[]
                 {
                     w.HighExplosive ? "HE" : null, w.Sonic ? "sonic" : null, w.Flash ? "flash" : null,
                     w.BeeperSeeker ? "seeker" : null, w.IsGuided ? "guided" : null, w.Torpedo ? "torpedo" : null,
                     w.Crater ? "crater" : null, w.Rear ? "rear" : null,
                 })
        {
            if (flag != null)
            {
                parts.Add(flag);
            }
        }
        return string.Join(" · ", parts);
    }

    private static string NodeNames(IReadOnlyList<Node3D> nodes)
    {
        var sb = new StringBuilder();
        foreach (var n in nodes)
        {
            if (sb.Length > 0)
            {
                sb.Append(',');
            }
            sb.Append(n.HasMeta(AnimRuntime.NameMeta) ? n.GetMeta(AnimRuntime.NameMeta).AsString() : n.Name);
        }
        return sb.ToString();
    }

    // ---- ui helpers ---------------------------------------------------------------------------

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

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

    // ---- click to place ------------------------------------------------------------------------

    // The struck body's readable name: colliders are unnamed children of the mesh node,
    // so the name lives on the nearest ancestor carrying the `cs_name` meta.
    private static string NameOfStruck(Node? body)
    {
        for (var n = body; n != null; n = n.GetParent())
        {
            if (n is Node3D n3d && n3d.HasMeta(AnimRuntime.NameMeta))
            {
                return SelectionService.NameOf(n3d);
            }
        }
        return body?.Name.ToString() ?? "?";
    }

    // The closest point of a body's collision geometry to `from`, in
    // world space, measured over the trimesh's own vertices. Null when the body carries no
    // concave shape (nothing in a built chapter does, but a hand-made body might).
    private static (Vector3 Point, float Dist)? NearestVertex(StaticBody3D body, Vector3 from)
    {
        (Vector3 Point, float Dist)? best = null;
        foreach (var child in body.GetChildren())
        {
            if (child is not CollisionShape3D { Shape: ConcavePolygonShape3D concave } cs)
            {
                continue;
            }
            var xf = cs.GlobalTransform;
            foreach (var v in concave.GetFaces())
            {
                var world = xf * v;
                float d = world.DistanceSquaredTo(from);
                if (best is not { } b || d < b.Dist)
                {
                    best = (world, d);
                }
            }
        }
        return best is { } found ? (found.Point, Mathf.Sqrt(found.Dist)) : null;
    }

    // Hands the rig's camera to a free Flight.Camera.SpectatorCamera and back (V), each direction
    // taking over from exactly where the other left the eye so there is no jump. The aircraft
    // keeps flying, firing and drawing its HUD throughout; only the view changes hands.
    private void SetFreeCamera(bool on)
    {
        if (_camera == null || _host == null || on == _freeCamera)
        {
            return;
        }
        _freeCamera = on;
        var eye = _camera.GlobalPosition;
        if (on)
        {
            _host.CameraOwned = true;
            _host.SetViewedFromOutside(true);
            // The scripted twin has no hands on WASD, so the free camera would hand the view back
            // from exactly where it took it and the no-jump check could not fail. Displace it on
            // the way out, so the hand-back is measured from an eye the orbit never chose.
            var start = CameraToggleFrames > 0 ? eye + ScriptedFreeCameraOffset : eye;
            // ⚠ No LockCandidates, deliberately: F is this lab's rocket trigger and the held plane
            // fires against a running sim, so a camera target key on the same F would launch one
            // every time you re-aimed the view. The key is inert without a roster (BL-428).
            _spectator = new Flight.Camera.SpectatorCamera(_camera, start, _plane.GlobalPosition)
            {
                Name = "weapon_lab_freecam",
                ShowReadout = false,   // the lab's own panel is the readout; a second one is clutter
            };
            AddChild(_spectator);
        }
        else
        {
            _spectator?.QueueFree();
            _spectator = null;
            _host.CameraOwned = false;
            // The arm would resume next frame anyway, but V is a hand-back the tester is watching,
            // so the selected view's rules go back on this frame rather than one late.
            _host.SetViewedFromOutside(false);
        }
        Log.Info("ui", $"weapon lab: camera -> {(on ? "free (V returns it)" : "orbit")} at ({eye.X:0.0},{eye.Y:0.0},{eye.Z:0.0})");
        _settleEye = eye;   // the next frame logs where the new owner actually put the eye
        SyncState();
    }

    // Fires the lab's own ray through `screen`, reports what it struck and re-parks the held
    // aircraft on that same ray at the stand-off distance, nose on the point. `aimOnly`
    // (shift-click) turns the aircraft without moving it. The id comes from
    // ProjectilePool.SurfaceIdOf on the struck body alone, so the panel cannot disagree with
    // what the round does; a coastal tile's `col` and `col_water` bodies report separately.
    private void PickAt(Vector2 screen, bool aimOnly)
    {
        if (_camera == null || _host == null || !IsInsideTree())
        {
            return;
        }
        var from = _camera.ProjectRayOrigin(screen);
        var dir = _camera.ProjectRayNormal(screen);
        // World colliders only: a pick places the aircraft against the WORLD, the held plane's
        // own body (or a wingman's) must never be what a placement click lands on.
        var query = PhysicsRayQueryParameters3D.Create(from, from + (dir * RayLength), CollisionLayers.World);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0)
        {
            // A click on the sky is not a placement, say so and leave the aircraft where it is.
            _pickLine = "target: nothing under the cursor (sky)";
            Log.Info("ui", $"weapon lab: pick at ({screen.X:0},{screen.Y:0}) hit nothing, aircraft left where it was");
            SyncState();
            return;
        }
        var body = hit["collider"].As<Node>();
        PlaceOn(hit["position"].AsVector3(), dir, body, NameOfStruck(body), aimOnly);
    }

    // Re-parks the held aircraft on the aim line through `point`: back up
    // `dir` by the stand-off, nose on the point. Shared by the click (where
    // `dir` is the camera ray) and by the scripted target/surface twins (where it
    // is the line from the spawn), so all three place the aircraft the same way and report the same
    // line. `aimOnly` turns the aircraft without moving it.
    private void PlaceOn(Vector3 point, Vector3 dir, Node? body, string name, bool aimOnly)
    {
        if (_host == null)
        {
            return;
        }
        int surfaceId = ProjectilePool.SurfaceIdOf(body);
        string surface = $"{surfaceId}/{SurfaceRegistry.NameForId(surfaceId) ?? "?"}";
        float standoff = aimOnly ? (point - _plane.GlobalPosition).Length() : ClampedStandoff(point, dir);
        _pickLine = $"target: {Trim(name, 22)} · {surface} · {standoff:0} m";
        // The body's own node name is in the line on purpose: one mesh yields a body per texture
        // class present, so "col" vs "col_buildings" is what distinguishes a click that missed the
        // tagged sibling from a genuinely untagged surface, and those siblings need not share an id.
        Log.Info("ui", $"weapon lab: picked name={name} body={body?.Name} surface={surface} at=({point.X:0.0},{point.Y:0.0},{point.Z:0.0}) standoff={standoff:0}{(aimOnly ? " (aim only)" : "")}");
        ShowMarker(point);
        _host.PlaceHeld(aimOnly ? _plane.GlobalPosition : point - (dir * standoff), point);
        SyncState();
    }

    // `--weapon-target=x,y,z`: park facing that world point, on the line from the
    // aircraft's spawn, the mouse-free twin of a click, for a point already known from a log or a
    // previous capture. The point is taken as given: a coordinate in mid-air is a legitimate aim,
    // so nothing is raycast and the surface class reads `default` unless a body is under
    // it.
    private void PlaceOnTarget(Vector3 point)
    {
        var dir = point - _plane.GlobalPosition;
        if (dir.LengthSquared() <= 1e-6f)
        {
            Log.Warn("ui", $"--weapon-target names the aircraft's own position, leaving it at spawn");
            return;
        }
        dir = dir.Normalized();
        // Probe the point itself so the readout names a real body when there is one under the aim,
        // rather than reporting "default" for a building the tester deliberately aimed at.
        var query = PhysicsRayQueryParameters3D.Create(point - (dir * ClearanceProbeStart), point + (dir * ClearanceProbeStart), CollisionLayers.World);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        var body = hit.Count > 0 ? hit["collider"].As<Node>() : null;
        PlaceOn(point, dir, body, body != null ? NameOfStruck(body) : "(world point)", aimOnly: false);
    }

    // `--weapon-surface=<registry name>`: park facing the nearest collider carrying that
    // surface id, measured to the nearest vertex of its collision geometry, never the body's
    // origin (a chapter's water shares one origin at (0,0,0)). Ties fall to the earlier node
    // in tree order, so a `--det` capture is reproducible. Full behaviour: docs/cli.md.
    private void PlaceOnNearestSurface(string want)
    {
        if (SurfaceRegistry.IdForName(want) is not { } target)
        {
            Log.Warn("ui", $"--weapon-surface={want} is not a surface-registry name, leaving the aircraft at spawn");
            _pickLine = $"target: '{want}' is not a surface name";
            SyncState();
            return;
        }
        var origin = _plane.GlobalPosition;
        Node3D? best = null;
        var bestPoint = Vector3.Zero;
        float bestDist = float.MaxValue;
        int scanned = 0, matched = 0;
        void Walk(Node node)
        {
            foreach (var child in node.GetChildren())
            {
                if (child is StaticBody3D sb)
                {
                    scanned++;
                    if (ProjectilePool.SurfaceIdOf(sb) == target)
                    {
                        matched++;
                        if (NearestVertex(sb, origin) is { } near && near.Dist < bestDist)
                        {
                            bestDist = near.Dist;
                            bestPoint = near.Point;
                            best = sb;
                        }
                    }
                }
                Walk(child);
            }
        }
        Walk(GetParent() ?? this);
        if (best == null)
        {
            Log.Warn("ui", $"--weapon-surface={want}: this chapter has no collider carrying {target}/{want} among {scanned} scanned, leaving the aircraft at spawn");
            _pickLine = $"target: no {target}/{want} collider in this chapter";
            SyncState();
            return;
        }
        Log.Info("ui", $"--weapon-surface={want}: nearest of {matched} {target}/{want} bodies ({scanned} scanned) is {NameOfStruck(best)}/{best.Name} at {bestDist:0} m, point=({bestPoint.X:0.0},{bestPoint.Y:0.0},{bestPoint.Z:0.0})");
        // Take the FIRST thing the line to that point actually strikes: the readout must name the
        // surface a round fired down this aim would hit, which is not always the one searched for.
        var dir = (bestPoint - origin).Normalized();
        var query = PhysicsRayQueryParameters3D.Create(origin, origin + (dir * RayLength), CollisionLayers.World);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        var point = hit.Count > 0 ? hit["position"].AsVector3() : bestPoint;
        var body = hit.Count > 0 ? hit["collider"].As<Node>() : best;
        PlaceOn(point, dir, body, NameOfStruck(body), aimOnly: false);
    }

    // The stand-off the panel asks for, shortened if the ray back from the struck point
    // re-enters geometry, re-parking the aircraft inside a hillside or a warehouse would be worse
    // than standing closer than requested. Probed from just off the struck face, so the surface the
    // ray just hit is not itself the obstruction.
    private float ClampedStandoff(Vector3 point, Vector3 dir)
    {
        var back = -dir;
        var start = point + (back * ClearanceProbeStart);
        var query = PhysicsRayQueryParameters3D.Create(start, point + (back * _standoff), CollisionLayers.World);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0)
        {
            return _standoff;
        }
        float blocked = (hit["position"].AsVector3() - point).Length();
        float clamped = Mathf.Max(MinStandoff, blocked - ClearanceMargin);
        Log.Info("ui", $"weapon lab: stand-off clamped {_standoff:0} -> {clamped:0} m, geometry {blocked:0} m back along the aim");
        return clamped;
    }

    // Drops the aim marker on the struck point, a small unshaded ball, built on first
    // use. Tagged as an overlay so the inspect tools' subtree measurements skip it, and meshes
    // carry no collider, so it can never be picked or shot itself.
    private void ShowMarker(Vector3 point)
    {
        if (_marker == null)
        {
            _marker = new MeshInstance3D
            {
                Name = "weapon_lab_aim",
                Mesh = new SphereMesh { Radius = 1.5f, Height = 3f, RadialSegments = 12, Rings = 6 },
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = new Color(1f, 0.35f, 0.1f),
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            _marker.SetMeta(SelectionService.OverlayMeta, true);
            AddChild(_marker);
        }
        _marker.Visible = true;
        _marker.GlobalPosition = point;
    }

    // Builds the two mount banks from the bound loadout: one entry per firable gun
    // group (in Loadout.FirableGuns order, which is the order
    // SelectGunGroup indexes) and one per pylon. Falls back
    // to the raw firepoint/pylon markers when no loadout is bound, a read-only list then, since
    // there is nothing live to arm.
    private void BuildMounts(Loadout? loadout)
    {
        if (loadout != null)
        {
            foreach (var g in loadout.FirableGuns)
            {
                _gunMounts.Add(new Mount(g.Mount, $"g{g.Slot}", g.Muzzles) { Group = g });
            }
            foreach (var h in loadout.Hardpoints)
            {
                _pylonMounts.Add(new Mount($"pylon{h.Index}", $"pylon{h.Index}", new[] { h.Pylon }) { Hp = h });
            }
        }
        // No loadout (or a plane the table omits): fall back to the raw marker rig so the panel and
        // the self-test still have somewhere to point.
        if (_gunMounts.Count == 0 || _pylonMounts.Count == 0)
        {
            CollectRawMarkers(_plane, out var fps, out var pylons);
            if (_gunMounts.Count == 0)
            {
                foreach (var (ord, node) in fps)
                {
                    _gunMounts.Add(new Mount($"firepoint{ord}", $"firepoint{ord}", new[] { node }));
                }
            }
            if (_pylonMounts.Count == 0)
            {
                foreach (var (ord, node) in pylons)
                {
                    _pylonMounts.Add(new Mount($"pylon{ord}", $"pylon{ord}", new[] { node }));
                }
            }
        }
    }

    // Snapshots the weapon every group and pylon carries before the panel touches
    // anything, the fit "reset to stock" restores. Not read back out of
    // `stock_loadouts.json`: `--rocket=`/`--loadout=` are part of how the session
    // was launched, and the button restores the launch, not the file.
    private void RememberLaunchFit(Loadout? loadout)
    {
        if (loadout == null)
        {
            return;
        }
        foreach (var g in loadout.Guns)
        {
            _launchGuns[g] = g.Weapon;
        }
        if (loadout.Hardpoints.Count > 0)
        {
            _launchOrdnance = loadout.Hardpoints[0].Weapon;
        }
    }

    private void ResolveInitialSelection()
    {
        if (InitialWeapon != null)
        {
            int gi = IndexOfWeapon(_gunWeapons, InitialWeapon);
            int ri = IndexOfWeapon(_rocketWeapons, InitialWeapon);
            if (gi >= 0)
            {
                _bank = Guns;
                _weaponIndex = gi;
            }
            else if (ri >= 0)
            {
                _bank = Hardpoints;
                _weaponIndex = ri;
            }
            else
            {
                Log.Warn("ui", $"weapon lab: --weapon-lab names unknown weapon '{InitialWeapon}'");
            }
        }
        else
        {
            SyncWeaponIndexToMount();
        }
        if (InitialMount != null)
        {
            var mounts = BankMounts;
            for (int i = 0; i < mounts.Count; i++)
            {
                if (string.Equals(mounts[i].Cli, InitialMount, StringComparison.OrdinalIgnoreCase))
                {
                    _mountIndex = i;
                    break;
                }
            }
        }
    }

    // ---- driving the live loadout --------------------------------------------------------------

    // Points the controller's own gun/pylon selector at the selected mount, so the
    // aircraft's trigger fires from the mount the panel names.
    private void ApplyMount()
    {
        if (_host == null || SelectedMount is not { } mount)
        {
            return;
        }
        if (_bank == Guns)
        {
            _host.SelectGunGroup(_mountIndex);
        }
        else
        {
            _host.SelectPylon(_mountIndex);
        }
        Log.Debug("weapons", $"weapon lab: mount {mount.Label} selected ({NodeNames(mount.Nodes)})");
    }

    // Arms the selection: a gun goes onto the selected group alone (with a full clip of
    // its own `CLUSTER_SIZE`); a hardpoint weapon re-arms every pylon and rebuilds the
    // mounted ordnance models, so the wings show the new type.
    private void ApplyWeapon()
    {
        if (_host == null || _loadout == null || SelectedWeapon is not { } w)
        {
            return;
        }
        if (_bank == Guns)
        {
            if (SelectedMount?.Group is not { } g)
            {
                return;
            }
            g.Weapon = w;
            g.Capacity = w.ClusterSize ?? 0;
            g.Ammo = g.Capacity;
            Log.Debug("weapons", $"weapon lab: gun group {g.Slot} ({g.Mount}) -> {w.Id} ({w.Name}), {g.Ammo} rounds, muzzles {NodeNames(g.Muzzles)}");
        }
        else
        {
            if (_loadout.Hardpoints.Count == 0)
            {
                return;
            }
            Tooling.ProbeRunner.ApplyRocketOverride(_loadout, _weapons, w.Id, verbose: false);
            RebuildOrdnance(w);
        }
    }

    // Re-hangs the mounted ordnance after a hardpoint swap. The old set comes off the
    // pylons FIRST (PylonOrdnance.Unmount detaches immediately), rebuilding without
    // that leaves the previous body under every pylon, so a stepper held down leaks one model per
    // pylon per swap. A weapon with no `FLYOUT` model in this chapter's gamez mounts nothing:
    // PylonOrdnance.Build returns null and the wings simply go empty.
    private void RebuildOrdnance(WeaponDef w)
    {
        if (_host == null || _loadout == null)
        {
            return;
        }
        _host.Ordnance?.Unmount();
        _host.Ordnance = PylonOrdnance.Build(_loadout, _host.Projectiles, _host.InfiniteAmmo);
        Log.Debug("weapons", $"weapon lab: hardpoints -> {w.Id} ({w.Name}) flyout='{w.Flyout?.Model ?? "-"}' per_pylon={_loadout.Hardpoints[0].Ammo} mounted={_host.Ordnance?.Count ?? 0} ordnance_nodes={CountOrdnanceNodes()}");
    }

    // How many ordnance bodies are actually parented to the rig's pylons right now, the
    // leak tripwire the swap path is verified with (it must equal the mounted count, whatever the
    // panel has been stepped through).
    private int CountOrdnanceNodes()
    {
        int n = 0;
        foreach (var hp in _loadout?.Hardpoints ?? (IReadOnlyList<Hardpoint>)Array.Empty<Hardpoint>())
        {
            foreach (var child in hp.Pylon.GetChildren())
            {
                if (child.Name.ToString().StartsWith("ordnance", StringComparison.Ordinal))
                {
                    n++;
                }
            }
        }
        return n;
    }

    // Holds the aircraft's own trigger, the gun one or the rocket one, by bank. The
    // other is always released, so switching banks moves the held trigger with it.
    private void SetAutoFire(bool on)
    {
        _autoFire = on;
        if (_host != null)
        {
            _host.AutoFire = on && _bank == Guns;
            _host.AutoFireRockets = on && _bank == Hardpoints;
        }
    }

    // Points the weapon stepper at what the selected mount actually carries, so the panel
    // reads the live loadout rather than its own last click.
    private void SyncWeaponIndexToMount()
    {
        var carried = _bank == Guns ? SelectedMount?.Group?.Weapon : SelectedMount?.Hp?.Weapon;
        if (carried != null)
        {
            int i = IndexOfWeapon(BankWeapons, carried.Id);
            if (i >= 0)
            {
                _weaponIndex = i;
            }
        }
    }

    // Puts the launch fit back on every group and pylon, full clips, and re-hangs the
    // ordnance models.
    private void ResetToStock()
    {
        if (_host == null || _loadout == null)
        {
            return;
        }
        foreach (var g in _loadout.Guns)
        {
            if (_launchGuns.TryGetValue(g, out var w))
            {
                g.Weapon = w;
                g.Capacity = w.ClusterSize ?? 0;
                g.Ammo = g.Capacity;
            }
        }
        if (_launchOrdnance is { } ord && _loadout.Hardpoints.Count > 0)
        {
            Tooling.ProbeRunner.ApplyRocketOverride(_loadout, _weapons, ord.Id, verbose: false);
            RebuildOrdnance(ord);
        }
        SyncWeaponIndexToMount();
        Log.Info("ui", $"weapon lab: loadout reset to the fit the session launched with");
        SyncState();
    }

    // ---- panel + state -----------------------------------------------------------------------

    // Pushes the current state onto the widgets: panel visibility and every
    // readout/toggle.
    private void SyncState()
    {
        _suppress = true;
        _ui.Visible = _panel;

        _bankLabel.Text = _bank == Guns
            ? $"bank: GUNS  ({_gunWeapons.Count})"
            : $"bank: HARDPOINTS  ({_rocketWeapons.Count})";
        if (SelectedWeapon is { } w)
        {
            string kind = w.IsRocket ? "ROCKET" : w.IsGun ? "GUN" : "SPECIAL";
            _weaponLabel.Text = $"{w.Id}  {w.Name}  [{kind}]   [{_weaponIndex + 1}/{BankWeapons.Count}]";
            _weaponDetail.Text = WeaponSummary(w);
        }
        else
        {
            _weaponLabel.Text = "(no weapon)";
            _weaponDetail.Text = "";
        }
        var mounts = BankMounts;
        _mountLabel.Text = mounts.Count > 0
            ? $"{mounts[_mountIndex].Label}   [{_mountIndex + 1}/{mounts.Count}]"
            : (_bank == Guns ? "(no gun groups)" : "(no pylons)");
        _ammoLabel.Text = AmmoLine();
        _cameraLabel.Text = _freeCamera
            ? "V: FREE camera, WASD/QE fly, hold RMB to look; V returns the orbit"
            : "V: orbit camera (WASD/arrows swing it, numpad +/- zoom), V frees it";
        _pickLabel.Text = _pickLine;
        _standoffLabel.Text = $"stand-off {_standoff:0} m";
        _autoFireToggle.ButtonPressed = _autoFire;
        _infiniteAmmoToggle.ButtonPressed = _host is { InfiniteAmmo: true };
        _cliLabel.Text = CliArgs();
        _suppress = false;
    }

    // The selected mount's live counter. Infinite ammo is called out because it hides a
    // real behaviour: the counters never drain, so a pylon never empties and its mounted model
    // never disappears.
    private string AmmoLine()
    {
        if (SelectedMount is not { } m)
        {
            return "";
        }
        var (ammo, cap) = m.Group is { } g ? (g.Ammo, g.Capacity)
            : m.Hp is { } h ? (h.Ammo, h.Capacity)
            : (0, 0);
        return _host is { InfiniteAmmo: true }
            ? $"ammo: ∞ of {cap}, nothing drains, no model hides"
            : $"ammo: {ammo} / {cap}";
    }

    // The arguments that reproduce this selection, the lab's output.
    private string CliArgs()
    {
        var sb = new StringBuilder();
        sb.Append(_host != null ? "--plane=" : "--viewer --plane=").Append(_planeModel);
        if (SelectedWeapon is { } w)
        {
            sb.Append(" --weapon-lab=").Append(w.Id);
        }
        else if (_host != null)
        {
            sb.Append(" --weapon-lab");
        }
        if (_mountIndex != 0 && SelectedMount is { } mount)
        {
            sb.Append(" --weapon-mount=").Append(mount.Cli);
        }
        if (_autoFire)
        {
            sb.Append(" --weapon-fire");
        }
        return sb.ToString();
    }

    // ---- edits -------------------------------------------------------------------------------

    private void StepBank(int delta)
    {
        _bank = (_bank + delta + 2) % 2;
        _weaponIndex = 0;
        _mountIndex = 0;
        SyncWeaponIndexToMount();
        ApplyMount();
        SetAutoFire(_autoFire);   // the held trigger follows the bank
        SyncState();
    }

    private void StepWeapon(int delta)
    {
        if (BankWeapons.Count == 0)
        {
            return;
        }
        _weaponIndex = Mathf.PosMod(_weaponIndex + delta, BankWeapons.Count);
        ApplyWeapon();
        SyncState();
    }

    private void StepMount(int delta)
    {
        if (BankMounts.Count == 0)
        {
            return;
        }
        _mountIndex = Mathf.PosMod(_mountIndex + delta, BankMounts.Count);
        SyncWeaponIndexToMount();   // the panel follows what THIS mount carries; it does not re-arm it
        ApplyMount();
        SyncState();
    }

    private void BuildUi()
    {
        _ui = new CanvasLayer { Layer = 1, Visible = false };
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        // Top-right: in flight the bottom-right corner belongs to the gauge cluster (its right-hand
        // dials sit 420 px from the right edge in reference space), and the top-left is the flight
        // HUD's. Top-right is free unless F19 opens the damage lab.
        var panel = new PanelContainer { SelfModulate = new Color(1, 1, 1, 0.85f) };
        panel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        panel.GrowHorizontal = Control.GrowDirection.Begin;
        panel.GrowVertical = Control.GrowDirection.End;
        panel.Position = new Vector2(-8, 8);

        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, 10);
        }
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);

        box.AddChild(new Label { Text = "WEAPON LAB" });
        box.AddChild(Small("B hides · Space/F fire · click parks on a surface · shift-click aims"));
        _cameraLabel = Small("");
        box.AddChild(_cameraLabel);

        // target readout + stand-off: what the last click struck, and how far back up the camera
        // ray the aircraft parks from the next one.
        _pickLabel = new Label { CustomMinimumSize = new Vector2(330, 0) };
        _pickLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_pickLabel);
        _standoffLabel = Small("");
        box.AddChild(_standoffLabel);
        var standoffSlider = new HSlider
        {
            MinValue = MinStandoff,
            MaxValue = MaxStandoff,
            Step = 5,
            Value = _standoff,
            CustomMinimumSize = new Vector2(220, 0),
        };
        standoffSlider.ValueChanged += v =>
        {
            if (!_suppress)
            {
                _standoff = Mathf.Clamp((float)v, MinStandoff, MaxStandoff);
                SyncState();
            }
        };
        box.AddChild(standoffSlider);

        box.AddChild(Separator());

        // bank stepper (guns arm gun groups, hardpoints arm pylons)
        _bankLabel = new Label { CustomMinimumSize = new Vector2(300, 0), VerticalAlignment = VerticalAlignment.Center };
        var bankRow = new HBoxContainer();
        bankRow.AddChild(StepButton("<", () => StepBank(-1)));
        bankRow.AddChild(_bankLabel);
        bankRow.AddChild(StepButton(">", () => StepBank(1)));
        box.AddChild(bankRow);

        // weapon stepper, each step ARMS the selection
        _weaponLabel = new Label { CustomMinimumSize = new Vector2(330, 0), VerticalAlignment = VerticalAlignment.Center };
        var weaponRow = new HBoxContainer();
        weaponRow.AddChild(StepButton("<", () => StepWeapon(-1)));
        weaponRow.AddChild(_weaponLabel);
        weaponRow.AddChild(StepButton(">", () => StepWeapon(1)));
        box.AddChild(weaponRow);
        _weaponDetail = Small("");
        _weaponDetail.CustomMinimumSize = new Vector2(330, 0);
        _weaponDetail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_weaponDetail);

        box.AddChild(Separator());

        // mount stepper, the group/pylon the trigger fires from
        _mountLabel = new Label { CustomMinimumSize = new Vector2(280, 0), VerticalAlignment = VerticalAlignment.Center };
        var mountRow = new HBoxContainer();
        mountRow.AddChild(StepButton("<", () => StepMount(-1)));
        mountRow.AddChild(_mountLabel);
        mountRow.AddChild(StepButton(">", () => StepMount(1)));
        box.AddChild(mountRow);
        _ammoLabel = Small("");
        _ammoLabel.CustomMinimumSize = new Vector2(330, 0);
        _ammoLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_ammoLabel);

        box.AddChild(Separator());

        // The lab launches with infinite ammo so a soak run never dries up, which also hides the
        // depletion behaviour entirely (a pylon that never empties never drops its mounted model),
        // so the toggle to turn it off is on the panel rather than only on the command line.
        _infiniteAmmoToggle = new CheckButton { Text = "infinite ammo" };
        _infiniteAmmoToggle.Toggled += on =>
        {
            if (!_suppress && _host != null)
            {
                _host.InfiniteAmmo = on;
                SyncState();
            }
        };
        box.AddChild(_infiniteAmmoToggle);

        _autoFireToggle = new CheckButton { Text = "hold the trigger (auto-fire)" };
        _autoFireToggle.Toggled += on =>
        {
            if (!_suppress)
            {
                SetAutoFire(on);
                SyncState();
            }
        };
        box.AddChild(_autoFireToggle);

        var reset = new Button { Text = "reset to stock" };
        reset.Pressed += ResetToStock;
        box.AddChild(reset);

        box.AddChild(Separator());

        _cliLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(330, 0) };
        _cliLabel.AddThemeFontSizeOverride("font_size", 11);
        box.AddChild(_cliLabel);

        var copy = new Button { Text = "copy CLI args" };
        copy.Pressed += () =>
        {
            var args = CliArgs();
            DisplayServer.ClipboardSet(args);
            Log.Info("ui", $"weapon lab: {args}");
        };
        box.AddChild(copy);

        margin.AddChild(box);
        panel.AddChild(margin);
        root.AddChild(panel);
        _ui.AddChild(root);
        AddChild(_ui);
        // Space is the fire key here, so no widget of this panel may hold keyboard focus, see
        // PanelFocus for why, and note it covers the sliders and toggles too, not just the buttons.
        PanelFocus.Strip(_ui, "weapon lab: panel built");
    }

    // One selectable place on the airframe: a firable gun group or a pylon.
    // Nodes are its live muzzle / pylon Node3Ds and Cli
    // the `--weapon-mount=` token (`g1`, `pylon1`). Group /
    // Hp are the live loadout entries the panel writes into, both null for the
    // raw-marker fallback, which has nothing to arm.
    private sealed class Mount
    {
        public Mount(string label, string cli, IReadOnlyList<Node3D> nodes)
        {
            Label = label;
            Cli = cli;
            Nodes = nodes;
        }

        public string Label { get; }
        public string Cli { get; }
        public IReadOnlyList<Node3D> Nodes { get; }
        public GunGroup? Group { get; init; }
        public Hardpoint? Hp { get; init; }
    }
}
