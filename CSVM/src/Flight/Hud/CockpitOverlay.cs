using System.Collections.Generic;
using CSVM.Flight.Camera;
using Godot;

namespace CSVM.Flight.Hud;

/// <summary>
/// Draws the cockpit interior in a world of its own, with the camera and the interior both at the
/// origin, and composites the result over the main view. The projection is the same one the main
/// world uses: only the camera-relative half of <see cref="CameraController.FirstPersonPose"/>
/// survives the move, because the plane position and the <c>cockpit_camera</c> offset cancel
/// between the eye and the panel. What is left is small, so float32 rounds it far below a pixel,
/// which the interior's world transform at chapter-scale coordinates does not.
/// The shipped path; <c>--no-cockpit-pass</c> leaves the interior in the main world instead.
/// One per player, built on that player's own HUD parent (<see cref="PlayerRig.HudParent"/>).
/// </summary>
public sealed partial class CockpitOverlay : CanvasLayer
{
    /// <summary>The panel material parameter carrying the world position of the pass's origin.</summary>
    public const string LightOriginParam = "light_origin";

    private readonly SubViewport _view;
    private readonly Camera3D _camera;
    private readonly Node3D _interior;
    private readonly DirectionalLight3D? _light;
    private readonly DirectionalLight3D? _sun;
    private readonly Basis _mount;
    private readonly List<OmniLight3D> _flashes = new();

    // The panel's own shader materials, whose point-light term needs the world position of this
    // pass's origin (SceneBuilder's `light_origin`).
    private readonly List<ShaderMaterial> _materials = new();

    private CockpitOverlay(SubViewport view, Camera3D camera, Node3D interior,
        DirectionalLight3D? light, DirectionalLight3D? sun)
    {
        _view = view;
        _camera = camera;
        _interior = interior;
        _light = light;
        _sun = sun;
        _mount = interior.Transform.Basis;
        CollectMaterials(interior, _materials);
    }

    /// <summary>The camera looking at the interior from that world's origin. The overlay's own,
    /// never the pilot's: the pilot's camera stays in the main world drawing everything else.</summary>
    public Camera3D Camera => _camera;

    /// <summary>The interior as this pass holds it: re-parented out of the plane model, into the
    /// overlay world, at zero translation.</summary>
    public Node3D Interior => _interior;

    /// <summary>This pass's own cloned sun, so <c>WeatherRig.RegisterExtraLighting</c>
    /// can keep its LEVEL in step with future zone changes; its bearing is <see cref="Sync"/>'s,
    /// re-taken from the world sun every frame. Null when the caller passed no sun (a suite rig
    /// with no lighting).</summary>
    public DirectionalLight3D? Sun => _light;

    /// <summary>This pass's own cloned Environment, for the same registration. Null when the
    /// caller passed no Environment.</summary>
    public Godot.Environment? Env => _view.World3D.Environment;

    /// <summary>Moves <paramref name="interior"/> into a private world under
    /// <paramref name="parent"/> and returns the pass drawing it, or null when the node has no
    /// parent to be taken from. <paramref name="sun"/> and <paramref name="env"/> are the main
    /// world's, copied so the panel is lit as it was; the pass keeps the sun to re-aim its copy
    /// per frame, since the interior's world frame turns with the aircraft.</summary>
    public static CockpitOverlay? Build(Node parent, Node3D interior, DirectionalLight3D? sun,
        Godot.Environment? env)
    {
        if (interior.GetParent() is not { } owner)
        {
            return null;
        }
        var overlay = NewOverlay(interior, sun, env);
        owner.RemoveChild(interior);
        overlay.AddViewportChildren(interior);
        parent.AddChild(overlay);
        return overlay;
    }

    /// <summary>The interior's basis for one frame: the wobble pivot's roll, about the plane's Z,
    /// applied over the mount basis exactly as the plane model's pivot applies it over the model.
    /// Public so the suite can assert the roll reaches the panel without a rig.</summary>
    public static Basis WobbledMount(Basis mount, float shakeRoll) =>
        new Basis(Vector3.Back, shakeRoll) * mount;

    /// <summary>A main-world point in the pass's frame: the pass keeps the world's orientation and
    /// puts the eye at its origin, so a light at <paramref name="world"/> lands at its offset from
    /// the eye. Public for the suite.</summary>
    public static Vector3 ToOverlay(Vector3 world, Vector3 eye) => world - eye;

    /// <summary>Point the overlay camera where the pilot's head points and re-light the panel for
    /// this frame's attitude, then show or hide the pass to match the interior's own visibility so
    /// <see cref="CockpitVisibility"/> keeps deciding which views draw a cockpit. Called every
    /// frame the rig owns its camera; <paramref name="attitude"/> is the DRAWN plane basis and
    /// <paramref name="shakeRoll"/> the wobble pivot's roll, which the interior inherited below
    /// that pivot in the main world and takes here through <see cref="WobbledMount"/>.</summary>
    public void Sync(Basis attitude, CameraController camera, float shakeRoll,
        IEnumerable<(Vector3 Position, float Range, Color Color, float Energy)>? flashes = null)
    {
        MirrorFlashes(flashes, camera.EyePosition);
        if (!Shown(GodotObject.IsInstanceValid(_interior) && _interior.Visible))
        {
            return;
        }
        // The attitude stays and only the translation goes: a rotation at the origin rounds far
        // below a pixel, and keeping it means the sun, the sky radiance and the flashes all sit
        // where the main world has them, with nothing re-aimed.
        var attitudeOnly = attitude.Orthonormalized();
        _interior.Transform = new Transform3D(attitudeOnly * WobbledMount(_mount, shakeRoll), Vector3.Zero);
        var (_, basis) = CameraController.FirstPersonPose(Vector3.Zero, attitudeOnly, Vector3.Zero,
            camera.Head.Elevation, camera.Head.Azimuth);
        _camera.Transform = new Transform3D(basis, Vector3.Zero);
        _camera.Fov = CameraController.FirstPersonFovDeg(camera.ViewMode);
        // The world's point lights sit at world positions; the panel sits at the eye's offset.
        foreach (var material in _materials)
            material.SetShaderParameter(LightOriginParam, camera.EyePosition);
        if (_light != null && _sun != null && GodotObject.IsInstanceValid(_sun))
        {
            _light.Basis = _sun.GlobalBasis;
        }
    }

    /// <summary>Take the pass off the screen for a caller that has stopped syncing it. The crash
    /// cut is that caller: it leaves first person while the rig's per-frame camera work is already
    /// halted, so nothing would reach <see cref="Sync"/> to notice the interior went away.</summary>
    public void Deactivate() => Shown(false);

    // The whole node graph, assembled before anything is added to a tree so a failed build frees
    // cleanly. The viewport owns its World3D outright: sharing the main one would put the interior
    // back at chapter-scale coordinates, which is the defect this pass exists to avoid.
    private static CockpitOverlay NewOverlay(Node3D interior, DirectionalLight3D? sun,
        Godot.Environment? env)
    {
        var view = new SubViewport
        {
            Name = "interior_view",
            World3D = new World3D(),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            HandleInputLocally = false,
            AudioListenerEnable3D = false,
            Msaa3D = (Viewport.Msaa)(int)ProjectSettings.GetSetting(
                "rendering/anti_aliasing/quality/msaa_3d", 0).AsInt32(),
        };
        if (env != null)
        {
            // The sky stays: a transparent viewport never paints its background, and clearing it
            // leaves the pass's reflected light with no radiance map. Duplicated so the pass owns
            // its copy, whose ambient RegisterExtraLighting then keeps on the flown zone's value.
            view.World3D.Environment = (Godot.Environment)env.Duplicate();
        }
        var camera = new Camera3D { Name = "interior_camera", Near = 0.01f, Far = 100f, Current = true };
        DirectionalLight3D? light = null;
        if (sun != null)
        {
            light = new DirectionalLight3D
            {
                Name = "interior_sun",
                LightEnergy = sun.LightEnergy,
                LightColor = sun.LightColor,
                // Copied off the live sun, which by this point in the build already carries the
                // flown zone's settings (WeatherRig.Build runs ahead of BuildCockpitPasses),
                // false in original mode, since the world sun's own flag never turns on there.
                ShadowEnabled = sun.ShadowEnabled,
            };
            if (sun.ShadowEnabled)
            {
                light.DirectionalShadowMode = sun.DirectionalShadowMode;
                light.DirectionalShadowSplit1 = sun.DirectionalShadowSplit1;
                light.DirectionalShadowSplit2 = sun.DirectionalShadowSplit2;
                light.DirectionalShadowSplit3 = sun.DirectionalShadowSplit3;
                light.DirectionalShadowBlendSplits = sun.DirectionalShadowBlendSplits;
                light.ShadowBias = sun.ShadowBias;
                light.ShadowNormalBias = sun.ShadowNormalBias;
                light.LightAngularDistance = sun.LightAngularDistance;
                light.ShadowBlur = sun.ShadowBlur;
                // Clamped to this pass's own camera far plane: the world sun's distance is a
                // zone's fog far (thousands of metres, always past 100 m), and passing it through
                // would push every PSSM split past what this near-field pass ever renders.
                light.DirectionalShadowMaxDistance = Mathf.Min(sun.DirectionalShadowMaxDistance, camera.Far);
            }
        }
        return new CockpitOverlay(view, camera, interior, light, sun)
        {
            Name = "cockpit_pass",
            Layer = UI.Boards.HudLayers.CockpitPass,
        };
    }

    // Every distinct shader material the subtree draws with, override or mesh surface.
    private static void CollectMaterials(Node node, List<ShaderMaterial> into)
    {
        if (node is MeshInstance3D mi && mi.Mesh is { } mesh)
        {
            if (mi.MaterialOverride is ShaderMaterial over && !into.Contains(over))
                into.Add(over);
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                if ((mi.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s)) is ShaderMaterial m
                    && !into.Contains(m))
                {
                    into.Add(m);
                }
            }
        }
        foreach (var child in node.GetChildren())
            CollectMaterials(child, into);
    }

    // Whether the pass draws this frame, as the two writes that decide it: the layer's own
    // visibility and the viewport's update mode, which together also stop it re-rendering.
    private bool Shown(bool shown)
    {
        Visible = shown;
        _view.RenderTargetUpdateMode = shown
            ? SubViewport.UpdateMode.Always
            : SubViewport.UpdateMode.Disabled;
        return shown;
    }

    private void AddViewportChildren(Node3D interior)
    {
        var container = new SubViewportContainer
        {
            Name = "interior_pane",
            Stretch = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            // A transparent 3D viewport writes premultiplied colour; composited as straight alpha
            // the gunsight glass takes its alpha twice and reads darker than in the main world.
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.PremultAlpha },
        };
        container.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(container);
        container.AddChild(_view);
        _view.AddChild(_camera);
        if (_light != null)
        {
            _view.AddChild(_light);
        }
        // Everything but the translation is kept: the head-pitch tilt and the interior scale are
        // the mount PlaneBuilder built, and the pass changes only where the frame's origin is.
        interior.Transform = new Transform3D(interior.Transform.Basis, Vector3.Zero);
        _view.AddChild(interior);
    }

    // The omni muzzle flashes live in the main world, which this world cannot see, so each lit one
    // gets a twin here at the same eye-relative place; twins past the lit count go dark. Only the
    // enhanced interior is shaded and takes them; the original-mode interior takes the point term.
    private void MirrorFlashes(
        IEnumerable<(Vector3 Position, float Range, Color Color, float Energy)>? flashes, Vector3 eye)
    {
        int used = 0;
        if (flashes != null)
        {
            foreach (var f in flashes)
            {
                if (used >= _flashes.Count)
                {
                    var twin = new OmniLight3D { Name = $"interior_flash_{used}", ShadowEnabled = false };
                    _view.AddChild(twin);
                    _flashes.Add(twin);
                }
                var light = _flashes[used++];
                light.Position = ToOverlay(f.Position, eye);
                light.OmniRange = f.Range;
                light.LightColor = f.Color;
                light.LightEnergy = f.Energy;
                light.Visible = true;
            }
        }
        for (int i = used; i < _flashes.Count; i++)
            _flashes[i].Visible = false;
    }
}
