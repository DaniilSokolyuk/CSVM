using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The mission-end black-out: a full-screen <see cref="ColorRect"/> that reads
/// <see cref="CampaignDirector.LeavingFade"/> every frame and paints it straight onto the alpha
/// channel. The original's ending path (<c>FUN_00443090</c>) copies the framebuffer and ramps a
/// black overlay over it for the same two seconds the leaving hold runs
/// (docs/formats/objectives.md, "The mission-end path, and what the player sees after it"); this
/// polls the director's own clock instead of copying a frame, so the ramp is exactly the hold's,
/// on whichever ending reached it, and the next screen (the scrapbook or the cabin) always takes
/// over on black. Self-mounting, one per rig's <c>HudParent</c>, the same shape
/// <see cref="Overlays.ObjectivesHud"/> uses for a per-pane readout.
/// </summary>
public sealed partial class MissionEndFade : Node
{
    private readonly CampaignDirector _director;
    private CanvasLayer? _hudLayer;
    private ColorRect? _rect;

    private MissionEndFade(CampaignDirector director)
    {
        Name = "mission_end_fade";
        _director = director;
    }

    /// <summary>Builds the overlay over a campaign director. Hidden until the director's fade
    /// leaves 0, so a session that never ends the mission renders exactly what it rendered
    /// before this existed.</summary>
    public static MissionEndFade Build(CampaignDirector director) => new(director);

    public override void _Ready()
    {
        _hudLayer = new CanvasLayer { Layer = HudLayers.MissionEndFade, Name = "mission_end_fade_layer", Visible = false };
        _rect = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _hudLayer.AddChild(_rect);
        AddChild(_hudLayer);
    }

    public override void _Process(double delta) => Refresh(_director.LeavingFade);

    /// <summary>Paints one alpha onto the overlay, exposed so a suite can assert the drawn state
    /// directly off a director's own <see cref="CampaignDirector.LeavingFade"/> without a frame of
    /// <c>_Process</c> in between.</summary>
    public void Refresh(float alpha)
    {
        if (_rect == null || _hudLayer == null)
        {
            return;
        }

        _rect.Color = new Color(0f, 0f, 0f, alpha);
        _hudLayer.Visible = alpha > 0f;
    }
}
