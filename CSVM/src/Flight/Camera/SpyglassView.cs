using Godot;

namespace CSVM.Flight.Camera;

/// <summary>The spyglass picture: a square viewport rendering the SHARED world through a camera of
/// its own, which <see cref="Hud.TargetHud"/> then draws as a disc at the off-screen marker's anchor.
/// The world is inherited rather than owned, so the target is the one in play and wears the flown
/// zone's fog; the pane's own camera lends its clip planes and cull mask, since the picture is the
/// same view from the same aeroplane, less the pilot's own airframe (<see cref="DiscMask"/>).
/// One per pane, built with the HUD, rendering only while
/// <see cref="Aim"/> is being called. Where it looks and how wide is <see cref="Spyglass"/>'s.
/// </summary>
public sealed partial class SpyglassView : SubViewport
{
    private Camera3D _camera = null!;
    private Camera3D? _pane;
    private uint _ownLayer;
    private bool _live;

    /// <summary>Whether the picture is rendering this frame.</summary>
    public bool Live => _live;

    /// <summary>Where the picture's camera stands and how it is turned, as <see cref="Aim"/> last
    /// left it. Read by the suite, which is how the eye's source is pinned.</summary>
    public Transform3D Eye => _camera.Transform;

    /// <summary>The cull mask the picture's camera renders through, the pane's less the pilot's
    /// own airframe. Read by the suite.</summary>
    public uint DiscCullMask => _camera.CullMask;

    /// <summary>The picture at <paramref name="pane"/>'s side: idle until the first
    /// <see cref="Aim"/>, and hung on the HUD control that draws it, which is what keeps it inside
    /// that pane's viewport and therefore in that pane's world. <paramref name="ownLayer"/> is the
    /// visual layer this pane's own aeroplane is drawn on, which the picture never shows.</summary>
    public static SpyglassView Build(Camera3D? pane, uint ownLayer)
    {
        int side = Mathf.RoundToInt(Spyglass.RefWindow);
        var view = new SpyglassView
        {
            Name = "spyglass_view",
            Size = new Vector2I(side, side),
            RenderTargetUpdateMode = UpdateMode.Disabled,
            HandleInputLocally = false,
            AudioListenerEnable3D = false,
            Msaa3D = (Msaa)(int)ProjectSettings.GetSetting(
                "rendering/anti_aliasing/quality/msaa_3d", 0).AsInt32(),
        };
        view._pane = pane;
        view._ownLayer = ownLayer;
        view._camera = new Camera3D { Name = "spyglass_camera", Current = true };
        view.AddChild(view._camera);
        return view;
    }

    /// <summary>The picture's cull mask: <paramref name="paneMask"/> less the one layer the pilot's
    /// own aeroplane is drawn on (<see cref="UI.Boards.SplitScreen.OwnAirframeLayer"/>), every other
    /// aircraft kept.
    /// ⚠ NOT decoded, and do not put the airframe back on the decode's authority: the original's
    /// update touches no per-object visibility, and this follows the original at the controls
    /// (docs/org/spyglass.md).</summary>
    public static uint DiscMask(uint paneMask, uint ownLayer) => paneMask & ~ownLayer;

    /// <summary>Point the picture and start it rendering. <paramref name="side"/> is the disc's
    /// drawn diameter in device pixels, so the texture is rasterised at the size it is shown at.
    /// </summary>
    public void Aim(Transform3D pose, float fovDeg, int side)
    {
        if (side > 0 && Size.X != side)
        {
            Size = new Vector2I(side, side);
        }

        _camera.Transform = pose;
        _camera.Fov = fovDeg;
        if (_pane != null && GodotObject.IsInstanceValid(_pane))
        {
            // The zone apply writes the pane camera's clip pair and the pane owns the cull mask a
            // splitscreen seat draws through; the original hands camera 2 both alongside camera 1.
            _camera.Near = _pane.Near;
            _camera.Far = _pane.Far;
            _camera.CullMask = DiscMask(_pane.CullMask, _ownLayer);
        }

        RenderTargetUpdateMode = UpdateMode.Always;
        _live = true;
    }

    /// <summary>Stop rendering. The gates are re-answered every frame, so this is called on every
    /// frame the disc is down and must stay cheap when it is already idle.</summary>
    public void Idle()
    {
        if (!_live)
        {
            return;
        }

        RenderTargetUpdateMode = UpdateMode.Disabled;
        _live = false;
    }
}
