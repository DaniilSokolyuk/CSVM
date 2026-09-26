using System;
using System.Collections.Generic;
using CSVM.Bindings;
using Godot;

namespace CSVM.Flight.Camera;

/// <summary>Free-flying observation camera (`--freecam`, docs/architecture.md). Drives the session
/// camera directly with no aircraft in the world: WASD/arrows move, Q/E (or Z/U) down/up, RMB-held
/// mouse look, wheel sets speed; gamepad mirrors it. Deliberately has NO collision, and pitch is
/// clamped short of vertical with roll never applied, so the view cannot tumble into an
/// unrecoverable attitude.
/// ⚠ Never bind a movement key another action already uses (C toggles the collider overlay, Space
/// fires the guns): this camera polls raw key state, so a shared key moves the camera as a side
/// effect of the other action, which is why the vertical pair is Q/E with Z/U beside it.</summary>
public sealed partial class SpectatorCamera : Node
{
    /// <summary>Base movement speed in m/s, before the boost/slow modifiers. TUNE.</summary>
    public const float DefaultSpeed = 180f;

    /// <summary>The identity this seat's pad bindings are authored on, standing for whichever pads
    /// this spectator owns rather than for one piece of hardware. A binding carries a single
    /// <see cref="DeviceId"/> while a seat reads a device set: `BL-375` gives each downed pilot its
    /// own pads, and a single-seat freecam reads every unclaimed one. <see cref="SeatDeviceState"/>
    /// answers for this and nothing else, so no connection index reaches a binding. Same species as
    /// <see cref="DefaultBindings.AnyPad"/>.</summary>
    public static readonly DeviceId SeatPad = DeviceId.Joypad("spectator-seat");

    /// <summary>Shows the live position/speed readout (on by default; off for screenshots).</summary>
    public bool ShowReadout = true;

    /// <summary>What the target key (<c>F</c> / pad <c>X</c>) may lock onto, answered afresh on
    /// each press. Null (the default) leaves the key inert, for a call site with no roster to
    /// offer. A caller that has one supplies the aircraft it considers in play; the key takes the
    /// nearest and steps outward (<see cref="OrbitLock.Next"/>), which is the only way back into
    /// a lock once a translation has released it.</summary>
    public Func<IReadOnlyList<Node3D>>? LockCandidates;

    private const float MinSpeed = 2f, MaxSpeed = 6000f;
    private const float BoostFactor = 6f, SlowFactor = 6f;
    private const float MouseLookRate = 0.0035f;   // radians per pixel of mouse motion. TUNE.
    private const float KeyLookRate = 1.6f;        // radians/s for the IJKL fallback. TUNE.
    private const float PadLookRate = 2.4f;        // radians/s at full right-stick deflection. TUNE.
    private const float PitchLimit = 1.5533f;      // ~89°, so the view never gimbals over the top
    private const float OrbitMinDist = 3f, OrbitMaxDist = 8000f;
    private const float OrbitZoomRate = 1.6f;      // trigger dolly while locked (1/s, exponential). TUNE.
    // Kept short of straight-above so the look-at (Basis.LookingAt) never gets parallel to world
    // up, which is degenerate.
    private const float OrbitPitchLimit = 1.396f; // ~80°

    private readonly Camera3D _camera;
    // The device filter: null/true (the default) reads every connected pad plus
    // the keyboard, matching the single-seat call sites (--freecam, the anim lab, the weapon lab,
    // all single-seat). A downed splitscreen pilot's spectator gets its rig's own PadDevices/
    // UseKeyboard instead, so two pilots watching at once no longer move together.
    private readonly int[]? _padDevices;
    private readonly bool _useKeyboard;
    // The camera context's actions, resolved twice a frame over two halves of the seat's hardware.
    // Two rather than one because this camera gives a key and a stick bound to the same action
    // different laws: IJKL turns at KeyLookRate and the right stick at PadLookRate, and the two sum.
    // One merged read would have to pick a rate and would drop the sum, which is a behaviour change.
    private readonly PlayerActions _keyActions;
    private readonly PlayerActions _padActions;
    private readonly SeatDeviceState _keyState;
    private readonly SeatDeviceState _padState;
    // Scratch for CycleLock, reused so a per-press lock costs no allocation.
    private readonly List<Node3D> _lockNodes = new();
    private readonly List<Vector3> _lockScan = new();

    private float _yaw, _pitch;
    private float _speed = DefaultSpeed;
    private bool _looking;                          // right mouse button held
    private Label? _readout;
    // Spherical offset of the eye from the target: distance, azimuth, elevation.
    private float _orbitYaw, _orbitPitch, _orbitDist;

    public SpectatorCamera(Camera3D camera, Vector3 position, Vector3 lookAt,
        int[]? padDevices = null, bool useKeyboard = true)
    {
        _camera = camera;
        _padDevices = padDevices;
        _useKeyboard = useKeyboard;
        // One map behind both halves, so a rebinding screen edits a single object and the two
        // reads stay two views of the same keymap rather than two keymaps. The free camera is
        // player one's, and under the launch gate this is the shipped set (LaunchBindings).
        var map = LaunchBindings.Map(1, InputContext.Camera, SeatPad, useKeyboard);
        // Each half mutes the other's devices: the keyboard reader is given no pads, and the pad
        // reader no keyboard, so a key and a stick on one action keep their own look rates and sum.
        _keyActions = new PlayerActions(map, useKeyboard);
        _padActions = new PlayerActions(map, false);
        _keyState = new SeatDeviceState(SeatPad, () => _padDevices, readsPads: false);
        _padState = new SeatDeviceState(SeatPad, () => _padDevices);
        _camera.Position = position;
        var to = lookAt - position;
        // Only the direction survives, not the distance, a --lookat point or a --direction
        // projected one unit ahead both frame the same way.
        if (to.LengthSquared() > 1e-6f)
        {
            _yaw = Mathf.Atan2(-to.X, -to.Z);
            _pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(to.Normalized().Y, -1f, 1f)), -PitchLimit, PitchLimit);
        }
        ApplyOrientation();
    }

    /// <summary>The camera this drives, exposed so the anim lab can project mouse-pick rays
    /// through it (the lab reuses this camera as its freecam).</summary>
    public Camera3D Camera => _camera;

    // ---- follow / orbit (anim lab): the camera locks onto a node and orbits it -----------------

    /// <summary>The node the camera is locked onto, or null. While set, the camera <b>orbits</b>
    /// it (RMB or the right stick swings, the wheel or the triggers zoom, and the node stays
    /// centred as it moves), like the static viewer's orbit camera but around a live target. Set
    /// via <see cref="FollowNode"/> or the target key; cleared the moment the user translates
    /// (WASD/QE / left stick), so flying off ends the lock while looking keeps it.</summary>
    public Node3D? Follow { get; private set; }

    // True while a text input owns keyboard focus (the picker's filter): the camera polls raw key
    // state, which bypasses GUI focus, so without this typing WASD would fly the camera.
    private bool KeyboardCaptured => GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit;

    /// <summary>Lock onto a node and orbit it, seeding the orbit from the camera's current offset
    /// (so <see cref="Frame"/> having just placed the eye means no jump). Null releases the lock.</summary>
    public void FollowNode(Node3D? node)
    {
        Follow = node;
        if (node != null && IsInstanceValid(node))
        {
            var off = _camera.Position - node.GlobalPosition;
            _orbitDist = Mathf.Max(off.Length(), OrbitMinDist);
            _orbitPitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(off.Y / _orbitDist, -1f, 1f)), -OrbitPitchLimit, OrbitPitchLimit);
            _orbitYaw = Mathf.Atan2(off.X, off.Z);
        }
    }

    /// <summary>Places the eye to frame an AABB from a fixed front-and-above angle, at the
    /// distance the current FOV needs to fit it, the anim lab's "focus this object". Leaves the
    /// follow state alone (the lab sets it alongside).</summary>
    public void Frame(Aabb aabb)
    {
        var center = aabb.GetCenter();
        float radius = Mathf.Max(aabb.Size.Length() * 0.5f, 1f);
        float dist = radius / Mathf.Tan(Mathf.DegToRad(_camera.Fov * 0.5f)) * 1.3f;
        var pos = center + new Vector3(0.4f, 0.45f, 1f).Normalized() * dist;
        _camera.Position = pos;
        var to = center - pos;
        _yaw = Mathf.Atan2(-to.X, -to.Z);
        _pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(to.Normalized().Y, -1f, 1f)), -PitchLimit, PitchLimit);
        ApplyOrientation();
    }

    public override void _Ready()
    {
        if (!ShowReadout)
            return;
        var layer = new CanvasLayer();
        AddChild(layer);
        _readout = new Label
        {
            Position = new Vector2(12, 12),
            Modulate = new Color(0.75f, 0.9f, 1f),
        };
        _readout.AddThemeFontSizeOverride("font_size", 14);
        layer.AddChild(_readout);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            // ⚠ The target key is handled here, never polled like the axes below it. This camera
            // reads raw key state, which bypasses GUI focus and SetInputAsHandled, so a polled
            // edge would also fire for a host that has already spoken for the key (BL-279).
            case InputEventKey { Keycode: Key.F, Pressed: true, Echo: false }
                when _useKeyboard && !KeyboardCaptured:
                CycleLock();
                break;
            case InputEventJoypadButton { ButtonIndex: JoyButton.X, Pressed: true } padButton
                when ReadsPad(padButton.Device):
                CycleLock();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } rmb:
                _looking = rmb.Pressed;
                Input.MouseMode = _looking ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                // While locked the wheel zooms the orbit; otherwise it sets the fly speed.
                if (Follow != null)
                {
                    _orbitDist = Mathf.Max(_orbitDist / 1.15f, OrbitMinDist);
                }
                else
                {
                    _speed = Mathf.Min(_speed * 1.25f, MaxSpeed);
                }
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                if (Follow != null)
                {
                    _orbitDist = Mathf.Min(_orbitDist * 1.15f, OrbitMaxDist);
                }
                else
                {
                    _speed = Mathf.Max(_speed / 1.25f, MinSpeed);
                }
                break;
            case InputEventMouseMotion motion when _looking:
                if (Follow != null)
                {
                    // Drag the orbit around the locked target (the OrbitUpdate re-aims each frame).
                    _orbitYaw -= motion.Relative.X * MouseLookRate;
                    _orbitPitch = Mathf.Clamp(_orbitPitch - motion.Relative.Y * MouseLookRate, -OrbitPitchLimit, OrbitPitchLimit);
                }
                else
                {
                    _yaw -= motion.Relative.X * MouseLookRate;
                    _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseLookRate, -PitchLimit, PitchLimit);
                    ApplyOrientation();
                }
                break;
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        // Both halves resolved before anything reads them, so every site this frame sees one
        // consistent tick.
        _keyState.Refresh();
        _padState.Refresh();
        _keyActions.Poll(_keyState);
        _padActions.Poll(_padState);
        // Locked onto a target: orbit it, unless the user asks to translate (WASD/QE / pad),
        // that releases the lock and this frame flies freely instead.
        if (Follow != null)
        {
            if (!IsInstanceValid(Follow))
            {
                Follow = null;
            }
            else if (TranslationRequested())
            {
                ExitFollow();
            }
            else
            {
                OrbitPad(dt);
                OrbitUpdate();
                UpdateReadout();
                return;
            }
        }
        Look(dt);
        Move(dt);
        UpdateReadout();
    }

    private void UpdateReadout()
    {
        if (_readout == null)
        {
            return;
        }
        var p = _camera.Position;
        _readout.Text = $"freecam  x {p.X:0} y {p.Y:0} z {p.Z:0}   speed {_speed:0} m/s" +
                        "   [RMB look · WASD/QE move · Shift fast · wheel speed · F lock]";
    }

    // The pad's half of the locked orbit: right stick swings it, triggers dolly it (RT out, LT in,
    // the sense FlightController.OrbitInput already uses). Without this the lock is mouse-only and
    // a pad's sole effect on it is TranslationRequested, which throws it away.
    private void OrbitPad(float dt)
    {
        float yaw = _padActions.Axis(InputAction.CameraLookRight, InputAction.CameraLookLeft);
        float pitch = _padActions.Axis(InputAction.CameraLookDown, InputAction.CameraLookUp);
        // Its own action pair rather than the boost and slow triggers, whose digital threshold would
        // leave the first half of trigger travel inert and double the slope of the second.
        float zoom = _padActions.Value(InputAction.CameraDollyOut) - _padActions.Value(InputAction.CameraDollyIn);
        _orbitYaw -= yaw * PadLookRate * dt;
        _orbitPitch = Mathf.Clamp(_orbitPitch - (pitch * PadLookRate * dt), -OrbitPitchLimit, OrbitPitchLimit);
        _orbitDist = Mathf.Clamp(_orbitDist * Mathf.Exp(OrbitZoomRate * zoom * dt), OrbitMinDist, OrbitMaxDist);
    }

    // The target key: nearest first, then a step outward. The roster is re-read on every press
    // rather than snapshotted, so an aircraft shot down between presses stops being lockable.
    private void CycleLock()
    {
        var candidates = LockCandidates?.Invoke();
        if (candidates == null)
            return;
        _lockScan.Clear();
        _lockNodes.Clear();
        int current = -1;
        foreach (var node in candidates)
        {
            if (node == null || !IsInstanceValid(node))
                continue;
            if (node == Follow)
                current = _lockNodes.Count;
            _lockNodes.Add(node);
            _lockScan.Add(node.GlobalPosition);
        }
        int next = OrbitLock.Next(_lockScan, _camera.Position, current);
        if (next >= 0)
            FollowNode(_lockNodes[next]);
    }

    // Whether this seat may read `device`, the event-side twin of the Pads.For gate the polled
    // reads use, so --no-pads, an unfocused window and a per-seat binding all still hold.
    private bool ReadsPad(int device)
    {
        foreach (int pad in Pads.For(_padDevices))
            if (pad == device)
                return true;
        return false;
    }

    // Places the eye on its orbit around the locked target and aims at it, the whole "orbit like
    // the static viewer, but around a live object" behaviour.
    private void OrbitUpdate()
    {
        var target = Follow!.GlobalPosition;
        float horiz = _orbitDist * Mathf.Cos(_orbitPitch);
        _camera.Position = target + new Vector3(
            horiz * Mathf.Sin(_orbitYaw), _orbitDist * Mathf.Sin(_orbitPitch), horiz * Mathf.Cos(_orbitYaw));
        var fwd = target - _camera.Position;
        if (fwd.LengthSquared() > 1e-6f)
        {
            _camera.Basis = Basis.LookingAt(fwd, Vector3.Up);
        }
    }

    // Release the lock, carrying the current orientation into the free-look yaw/pitch so the view
    // does not snap when you fly off.
    private void ExitFollow()
    {
        var fwd = -_camera.Basis.Z;
        _yaw = Mathf.Atan2(-fwd.X, -fwd.Z);
        _pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(fwd.Y, -1f, 1f)), -PitchLimit, PitchLimit);
        Follow = null;
    }

    // Any translation input this frame (used to release a lock). Keyboard is ignored while a text
    // field has focus, so typing a filter in the picker never moves the camera or breaks a lock.
    private bool TranslationRequested()
    {
        if (!KeyboardCaptured && (KeyMove() != Vector3.Zero))
        {
            return true;
        }
        return PadMove() != Vector3.Zero;
    }

    // The seat's net translation demand this frame as (strafe right, rise, forward), one method per
    // half of the hardware so Move can sum them the way the keyboard and the pad have always summed.
    private Vector3 KeyMove() => new(
        _keyActions.Axis(InputAction.CameraRight, InputAction.CameraLeft),
        _keyActions.Axis(InputAction.CameraUp, InputAction.CameraDown),
        _keyActions.Axis(InputAction.CameraForward, InputAction.CameraBack));

    private Vector3 PadMove() => new(
        _padActions.Axis(InputAction.CameraRight, InputAction.CameraLeft),
        _padActions.Axis(InputAction.CameraUp, InputAction.CameraDown),
        _padActions.Axis(InputAction.CameraForward, InputAction.CameraBack));

    private void Look(float dt)
    {
        // Keyboard fallback (IJKL) and the right stick both feed the same yaw/pitch as the
        // mouse, so any of the three can drive a session, including a pad-only machine.
        // They keep their own rates (a stick deflects proportionally, a key is on or off).
        float kb = (_useKeyboard && !KeyboardCaptured) ? 1f : 0f;
        float yaw = (kb * _keyActions.Axis(InputAction.CameraLookRight, InputAction.CameraLookLeft) * KeyLookRate)
                  + (_padActions.Axis(InputAction.CameraLookRight, InputAction.CameraLookLeft) * PadLookRate);
        float pitch = (kb * _keyActions.Axis(InputAction.CameraLookDown, InputAction.CameraLookUp) * KeyLookRate)
                    + (_padActions.Axis(InputAction.CameraLookDown, InputAction.CameraLookUp) * PadLookRate);
        if (yaw == 0f && pitch == 0f)
            return;
        _yaw -= yaw * dt;
        _pitch = Mathf.Clamp(_pitch - pitch * dt, -PitchLimit, PitchLimit);
        ApplyOrientation();
    }

    private void Move(float dt)
    {
        // Keyboard is silenced while typing a filter (KeyboardCaptured) or unassigned to this
        // seat (_useKeyboard); the pad is not.
        float kb = (_useKeyboard && !KeyboardCaptured) ? 1f : 0f;
        var basis = _camera.Basis;
        var demand = (kb * KeyMove()) + PadMove();
        // Forward is the camera's -Z (the project's convention everywhere); strafe its +X.
        // Vertical stays WORLD up regardless of camera pitch.
        var move = (basis.Z * -demand.Z) + (basis.X * demand.X) + (Vector3.Up * demand.Y);
        if (move.LengthSquared() < 1e-8f)
            return;

        float scale = 1f;
        if ((kb > 0f && _keyActions.Held(InputAction.CameraBoost)) || _padActions.Held(InputAction.CameraBoost))
        {
            scale *= BoostFactor;
        }
        if ((kb > 0f && _keyActions.Held(InputAction.CameraSlow)) || _padActions.Held(InputAction.CameraSlow))
        {
            scale /= SlowFactor;
        }
        _camera.Position += move.Normalized() * (_speed * scale * dt);
    }

    private void ApplyOrientation()
    {
        // Yaw about world up, then pitch about the camera's own X: no roll term ever enters,
        // so the horizon stays level.
        _camera.Basis = new Basis(Vector3.Up, _yaw) * new Basis(Vector3.Right, _pitch);
    }

}
