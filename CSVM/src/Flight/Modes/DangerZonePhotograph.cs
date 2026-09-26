using System;
using System.Collections.Generic;
using CSVM.Flight.Camera;
using CSVM.Flight.Hud;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Modes;

/// <summary>
/// The Danger Zone camera's own eye. The original does not photograph the pilot's view: for one
/// frame nobody sees it poses the main camera ahead of the aircraft, looking back at it at the
/// external 60° with the cockpit and HUD chrome off, renders, saves, and puts the view back
/// (<c>docs/formats/campaign-screens.md</c>, "The danger-zone slot"). This is that pose on a viewport
/// of its own sharing the pane's world, so the pane never shows it. <see cref="Request"/> is the
/// <see cref="PaneRequest"/> both photograph writers take: the pose is resolved in this node's
/// <see cref="_Process"/>, after the aircraft's own has placed the drawn pose, the viewport renders
/// once on that frame's draw, and the pixels are read back through <see cref="PaneReadback"/> after
/// it, so the frame that asks waits on nothing.
/// </summary>
public sealed partial class DangerZonePhotograph : SubViewport
{
    /// <summary>How far ahead of the aircraft the eye stands, in multiples of the airframe's
    /// <c>camparam</c> <c>dist</c>. The original's constant is -2.5 along the backward axis.</summary>
    public const float AheadFactor = 2.5f;

    /// <summary>The largest world-X and world-Z scatter added to the eye, as a fraction of
    /// <c>dist</c>.</summary>
    public const float ScatterAcross = 0.15f;

    /// <summary>The largest world-Y scatter added to the eye, as a fraction of <c>dist</c>. The
    /// eye otherwise stands level with the aircraft.</summary>
    public const float ScatterUp = 0.25f;

    private readonly List<GeometryInstance3D> _filled = new();
    private Camera3D _camera = null!;
    private Camera3D? _pane;
    private CockpitVisibility? _cockpit;
    private Node? _airframe;
    private Func<Transform3D> _aircraft = null!;
    private Func<float> _unit = null!;
    private float _dist;
    private Action<Image?>? _pending;
    private bool _drawing;
    private Callable _drawn;

    /// <summary>Whether this run draws frames at all. A headless run never reaches
    /// <c>FramePostDraw</c>, so a request would never land; its callers read the pane instead.</summary>
    public static bool Drawable => DisplayServer.GetName() != "headless";

    /// <summary>Where the eye stood for the last photograph. Read by the suite.</summary>
    public Transform3D Eye => _camera.Transform;

    /// <summary>The cull mask the last photograph was drawn through. Read by the suite.</summary>
    public uint PhotoCullMask => _camera.CullMask;

    /// <summary>Whether a request is waiting for its frame to be drawn or read.</summary>
    public bool Busy => _pending != null;

    /// <summary>The instances the photograph's fill light is armed on, empty outside the frame it
    /// draws. Read by the suite.</summary>
    public IReadOnlyList<GeometryInstance3D> Filled => _filled;

    /// <summary>The eye for one pilot. <paramref name="pane"/> lends its clip planes, cull mask and
    /// environment and names the world drawn; <paramref name="aircraft"/> answers the aircraft's
    /// drawn pose; <paramref name="dist"/> is its <c>camparam</c> <c>dist</c>;
    /// <paramref name="random"/> answers a uniform draw in [0, 1); <paramref name="airframe"/> is
    /// the subtree the photograph's fill light reaches, the pilot's own aircraft. Add it anywhere
    /// in the tree the pane's world is reachable from.</summary>
    public static DangerZonePhotograph Build(Camera3D? pane, CockpitVisibility? cockpit,
        Func<Transform3D> aircraft, float dist, Func<float> random, Node? airframe = null)
    {
        var view = new DangerZonePhotograph
        {
            Name = "danger_zone_photograph",
            Size = new Vector2I(4, 3),
            RenderTargetUpdateMode = UpdateMode.Disabled,
            HandleInputLocally = false,
            AudioListenerEnable3D = false,
            Msaa3D = (Msaa)(int)ProjectSettings.GetSetting(
                "rendering/anti_aliasing/quality/msaa_3d", 0).AsInt32(),
        };
        view._pane = pane;
        view._cockpit = cockpit;
        view._airframe = airframe;
        view._aircraft = aircraft;
        view._dist = dist;
        view._unit = () => (random() * 2f) - 1f;
        view._drawn = Callable.From(view.Drawn);
        view._camera = new Camera3D { Name = "danger_zone_camera", Current = true };
        view.AddChild(view._camera);
        return view;
    }

    /// <summary>The decoded pose: <see cref="AheadFactor"/> times <paramref name="dist"/> along
    /// the nose's level heading, a world-axis scatter from <paramref name="unit"/> (a draw in
    /// [-1, 1], taken for X, Y and Z in that order), and the eye turned to look at the aircraft's
    /// origin with no roll. A nose pointing straight up or down has no level heading and leaves
    /// the eye on the scatter alone, as the original's normalise does.</summary>
    public static Transform3D Pose(Transform3D aircraft, float dist, Func<float> unit)
    {
        var nose = -aircraft.Basis.Z;
        var level = new Vector3(nose.X, 0f, nose.Z);
        if (level.LengthSquared() > 0f)
        {
            level = level.Normalized();
        }

        var target = aircraft.Origin;
        var eye = target + (level * (AheadFactor * dist));
        eye += new Vector3(unit() * ScatterAcross * dist, unit() * ScatterUp * dist, unit() * ScatterAcross * dist);
        return new Transform3D(LookAt(eye, target), eye);
    }

    /// <summary>The eye's turn toward <paramref name="target"/> as the original builds it: a yaw
    /// and a pitch off the two positions and a zero roll. Unlike a look-at against an up vector it
    /// has an answer when the target is straight above or below.</summary>
    public static Basis LookAt(Vector3 eye, Vector3 target)
    {
        var d = eye - target;
        float level = Mathf.Sqrt((d.X * d.X) + (d.Z * d.Z));
        float yaw = Mathf.Atan2(d.X, d.Z);
        float pitch = Mathf.Atan2(target.Y - eye.Y, level);
        return Basis.FromEuler(new Vector3(pitch, yaw, 0f), EulerOrder.Yxz);
    }

    /// <summary>Asks for one photograph. Answers false, and never calls
    /// <paramref name="landed"/>, when there is no pane to take the world and the shape from or a
    /// photograph is already on its way; the caller tries again on a later frame.</summary>
    public bool Request(Action<Image?> landed)
    {
        if (_pending != null || !IsInsideTree() || _pane == null || !IsInstanceValid(_pane)
            || !_pane.IsInsideTree())
        {
            return false;
        }

        _pending = landed;
        return true;
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_pending == null || _drawing)
        {
            return;
        }

        if (_pane == null || !IsInstanceValid(_pane) || !_pane.IsInsideTree())
        {
            Land(null);
            return;
        }

        var paneView = _pane.GetViewport();
        var size = (Vector2I)paneView.GetVisibleRect().Size;
        if (size.X <= 0 || size.Y <= 0)
        {
            Land(null);
            return;
        }

        Size = size;
        World3D = paneView.FindWorld3D();
        _camera.Transform = Pose(_aircraft(), _dist, _unit);
        _camera.Fov = CameraController.ExternalFovDeg;
        _camera.Near = _pane.Near;
        _camera.Far = _pane.Far;
        _camera.Environment = _pane.Environment;
        _camera.Attributes = _pane.Attributes;
        _camera.CullMask = _pane.CullMask | SplitScreen.PhotographLayer;
        _cockpit?.ShowForPhotograph(SplitScreen.PhotographLayer);
        Fill(_camera.Transform.Origin);
        RenderTargetUpdateMode = UpdateMode.Once;
        _drawing = true;
        RenderingServer.Singleton.Connect(RenderingServer.SignalName.FramePostDraw,
            _drawn, (uint)ConnectFlags.OneShot);
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        var rs = RenderingServer.Singleton;
        if (rs.IsConnected(RenderingServer.SignalName.FramePostDraw, _drawn))
        {
            rs.Disconnect(RenderingServer.SignalName.FramePostDraw, _drawn);
        }
        if (_drawing)
        {
            _cockpit?.EndPhotograph();
            Unfill();
        }
        Land(null);
    }

    // The original's fill light, on the hardware renderer: for the photograph frame the player's
    // node raises the scene's directional light's ambient to ambient × 1.5 + 0.1 while its subtree
    // draws (docs/formats/campaign-screens.md, "The fill light"). Armed per instance with this
    // eye, so a pane drawing the same aircraft on the same frame keeps the zone's ambient.
    private void Fill(Vector3 eye)
    {
        Unfill();
        if (_airframe == null || !IsInstanceValid(_airframe))
        {
            return;
        }

        var armed = new Vector4(eye.X, eye.Y, eye.Z, 1f);
        var pending = new Stack<Node>();
        pending.Push(_airframe);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is GeometryInstance3D instance)
            {
                instance.SetInstanceShaderParameter(SceneBuilder.PhotoEyeParam, armed);
                _filled.Add(instance);
            }

            foreach (var child in node.GetChildren())
            {
                if (child is not Viewport)
                {
                    pending.Push(child);
                }
            }
        }
    }

    private void Unfill()
    {
        foreach (var instance in _filled)
        {
            if (IsInstanceValid(instance))
            {
                instance.SetInstanceShaderParameter(SceneBuilder.PhotoEyeParam, Vector4.Zero);
            }
        }
        _filled.Clear();
    }

    // After the draw that rendered the pose: the airframe goes back to what the pilot's view drew,
    // and the readback starts off the frame path.
    private void Drawn()
    {
        _cockpit?.EndPhotograph();
        Unfill();
        if (!IsInstanceValid(this) || !IsInsideTree())
        {
            Land(null);
            return;
        }

        var landed = _pending;
        _pending = null;
        _drawing = false;
        if (landed != null && !PaneReadback.Request(this, landed))
        {
            landed(null);
        }
    }

    private void Land(Image? frame)
    {
        var landed = _pending;
        _pending = null;
        _drawing = false;
        landed?.Invoke(frame);
    }
}
