using System.Collections.Generic;
using CSVM.Flight.Airframe;
using Godot;

namespace CSVM.Flight.Camera;

/// <summary>One built skydome and the gamez <c>zone_id</c> of the <c>horizon/zone*</c> node it was
/// built from, the pair <c>Mech3.ZoneGate.Draws</c> needs to decide whether it is this camera's
/// sky.</summary>
public readonly record struct HorizonDome(Node3D Node, int ZoneId);

/// <summary>
/// Everything one player's *view* owns for a session. A single-player session has
/// exactly one rig wrapping GameSession's original main-viewport camera, so the 1P path is
/// unchanged; splitscreen has one per pane (see <see cref="UI.Boards.SplitScreen"/>).
///
/// <para>The rig exists because the flight view is not just a camera: the skydome, the cloud
/// deck and the cloud-band whiteout overlay are all anchored to <i>the</i> camera every frame, so
/// each player needs a private copy of each, on that player's visual layer. GameSession's
/// per-frame anchoring loops over rigs; everything else in the world (terrain, clutter, the
/// ambient cloud field, map-edge extension, precipitation, all aircraft) is shared and rendered
/// in every pane.</para>
/// </summary>
public sealed class PlayerRig
{
    /// <summary>0-based player index (player 1 = 0).</summary>
    public int Index;

    /// <summary>This player's camera. Single player: GameSession's own camera in the main
    /// viewport. Splitscreen: a camera parented to the player's SubViewport, not a Node3D, its
    /// local <c>Position</c> IS the world transform, so per-frame anchoring can read
    /// <c>Camera.Position</c> directly in both modes.</summary>
    public Camera3D Camera = null!;

    /// <summary>The player's SubViewport, or null in single player (the main viewport).</summary>
    public SubViewport? Viewport;

    /// <summary>Where this player's screen-space layers go (HUD canvas, whiteout overlay): the
    /// player's SubViewport in splitscreen, the session root in single player.</summary>
    public Node HudParent = null!;

    /// <summary>The visual layer carrying this player's private camera-anchored copies, or 0 in
    /// single player (everything stays on the default layer and no cull mask is narrowed).</summary>
    public uint VisualLayer;

    /// <summary>The player's aircraft, once the flight session builds it.</summary>
    public FlightController? Controller;

    /// <summary>This player's skydome copy, re-centered on <see cref="Camera"/> each frame. It is a
    /// CONTAINER holding one <see cref="HorizonDomes"/> entry per built horizon zone,
    /// the anchor moves, the zone gate picks which child draws.</summary>
    public Node3D? Horizon;

    /// <summary>The zone domes under <see cref="Horizon"/>, one per horizon zone the world built.
    /// <c>Session.World.WeatherRig.Tick</c> shows exactly the one matching this rig's own camera
    /// weather state; decode: docs/org/weather.md.
    /// ⚠ A single entry stays visible at every state: a chapter whose data supports no swap must
    /// not render a frame with no sky in it.</summary>
    public List<HorizonDome> HorizonDomes = new();

    /// <summary>This player's cloudlayer deck copy, re-anchored under the camera each frame.</summary>
    public Node3D? Deck;

    // No per-rig ambient cloud field: the authored fogvol clutter is world-anchored static
    // geometry every pane sees (see Effects/FogVolumeClutter), unlike the dome and the deck,
    // which follow a camera and therefore still need a copy each.

    /// <summary>This player's full-pane cloud-band whiteout overlay, faded by camera altitude. It
    /// draws under this pane's own cockpit pass (<c>UI.Boards.HudLayers.Whiteout</c>), so the window
    /// whites out and the interior does not.</summary>
    public ColorRect? Whiteout;

    /// <summary>This rig's screen-space layers that depict the WORLD rather than the chrome: the
    /// lens flare's sprites and wash, and the cloud whiteout. A <c>CanvasLayer</c> draws over all
    /// 3D content whatever its depth, so these would otherwise paint the sun and the cloud over a
    /// cutscene's letterbox card, which sits 7.5 m in front of the eye and occludes both in world
    /// terms. <c>Session.World.CutsceneController</c> lowers them while a definition presents (BL-452);
    /// nothing else touches them, so an ordinary flight is unchanged.</summary>
    public List<CanvasLayer> WorldOverlays = new();

    /// <summary>This player's own camera weather state (1/2/3, <c>WeatherState.CameraWeatherState</c>),
    /// published once per frame by
    /// <c>Session.World.WeatherRig.Tick</c>. Per rig, not per session, a splitscreen pane's camera can
    /// sit in a different state than another pane's at the same instant, same as
    /// <see cref="Deck"/>'s regime. Consumed by nothing yet; defaults to 1 (the binary's own
    /// default) until the first <c>Tick</c> resolves it.</summary>
    public int CameraWeatherState = 1;
}
