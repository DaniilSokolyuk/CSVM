using Godot;

namespace CSVM.UI.Overlays;

/// <summary>
/// The static inspection view's orbit-camera controller: LMB-drag orbit, mouse-wheel zoom, and
/// AABB-based framing of a subject. Shared by <see cref="CSVM.Session.Launch.GameSession"/> and the
/// <c>--anim-lab</c> mode. Owns the orbit state and steers a <see cref="Camera3D"/> it does not
/// own; the host keeps ownership of the placement flags and F11/F12. <see cref="Frame"/>'s own doc
/// covers how the camera FOV sizes the orbit distance.
/// ⚠ <see cref="Frame"/>'s <c>lookAt</c> is a pivot point, not a direction: with the eye it also
/// sets the orbit radius, so a direction-only placement must be synthesized into a pivot first.
/// </summary>
public sealed class OrbitCamera
{
    private readonly Camera3D _camera;
    private Vector3 _orbitCenter;
    private float _orbitDistance = 20f;
    private float _yaw = 2.5f, _pitch = 0.3f; // default: front-left three-quarter view (nose is -Z)
    private bool _dragging;

    public OrbitCamera(Camera3D camera)
    {
        _camera = camera;
    }

    /// <summary>The point the camera orbits and aims at, the framed pivot. F11 prints it as the
    /// orbit-mode <c>--lookat</c> (the one mode whose F11 form is a point, because only a point
    /// reproduces the radius as well as the angle), and <c>--shots</c> jitter micro-orbits about
    /// it.</summary>
    public Vector3 OrbitCenter => _orbitCenter;

    /// <summary>Initial view angles (radians) from <c>--yaw=</c>/<c>--pitch=</c>, or from a
    /// <c>--direction</c> with no eye to place. Set before the first <see cref="Frame"/>; a framing
    /// with no eye position keeps them, one with an eye reconstructs them from it.</summary>
    public float Yaw { get => _yaw; set => _yaw = value; }
    public float Pitch { get => _pitch; set => _pitch = value; }

    /// <summary>Merges every mesh AABB under <paramref name="root"/> into one world-space box,
    /// the subject box <see cref="Frame"/> takes.
    /// ⚠ A meshless subtree returns a zero-size box at the origin; callers must special-case it.
    /// The nodes must be in the scene tree, or <c>GlobalTransform</c> reads identity.</summary>
    public static Aabb MergedAabb(Node3D root)
    {
        Aabb merged = default;
        bool first = true;
        void Walk(Node node)
        {
            if (node is MeshInstance3D mi && mi.Mesh != null)
            {
                var box = mi.GlobalTransform * mi.Mesh.GetAabb();
                merged = first ? box : merged.Merge(box);
                first = false;
            }
            foreach (var child in node.GetChildren())
            {
                Walk(child);
            }
        }
        Walk(root);
        return merged;
    }

    /// <summary>Frame the subject (its merged mesh <paramref name="aabb"/>). <paramref name="lookAt"/>
    /// overrides the pivot; <paramref name="camPos"/> (<c>--pos</c>), when set, places the eye there
    /// and reconstructs pitch/yaw from it, otherwise the distance is derived from the AABB radius
    /// and the camera FOV.</summary>
    public void Frame(Aabb aabb, Vector3? camPos, Vector3? lookAt)
    {
        _orbitCenter = lookAt ?? aabb.GetCenter();
        if (camPos is { } pos)
        {
            var offset = pos - _orbitCenter;
            _orbitDistance = offset.Length();
            if (_orbitDistance < 0.01f) { _orbitDistance = 1f; offset = Vector3.Back; }
            var dir = offset / _orbitDistance;
            _pitch = Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f));
            _yaw = Mathf.Atan2(dir.X, dir.Z);
        }
        else
        {
            var radius = aabb.Size.Length() * 0.5f;
            if (radius < 0.01f) radius = 5f;
            _orbitDistance = radius / Mathf.Sin(Mathf.DegToRad(_camera.Fov) * 0.5f) * 0.8f;
        }
        Update();
    }

    /// <summary>Recompute the camera transform from the current orbit state.</summary>
    public void Update()
    {
        _pitch = Mathf.Clamp(_pitch, -1.5f, 1.5f);
        var dir = new Vector3(
            Mathf.Sin(_yaw) * Mathf.Cos(_pitch),
            Mathf.Sin(_pitch),
            Mathf.Cos(_yaw) * Mathf.Cos(_pitch));
        _camera.Position = _orbitCenter + dir * _orbitDistance;
        _camera.LookAt(_orbitCenter, Vector3.Up);
    }

    /// <summary>Feed one input event: LMB toggles orbit-drag, the wheel zooms, and drag motion
    /// spins yaw/pitch. The host filters out the modes that own the camera (flight / freecam) and
    /// the global keys (Esc / F11 / F12) before delegating here.</summary>
    public void HandleInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                _dragging = mb.Pressed;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                _orbitDistance *= 0.9f;
                Update();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                _orbitDistance *= 1.1f;
                Update();
                break;
            case InputEventMouseMotion motion when _dragging:
                _yaw -= motion.Relative.X * 0.008f;
                _pitch += motion.Relative.Y * 0.008f;
                Update();
                break;
        }
    }
}
