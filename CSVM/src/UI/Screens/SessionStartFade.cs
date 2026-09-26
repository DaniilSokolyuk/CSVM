using System;
using CSVM.UI.Boards;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Screens;

/// <summary>
/// The cover a session starts under: a full-screen <see cref="ColorRect"/> raised with the load
/// screen and held opaque until the session reports the first frame the player is meant to see,
/// then faded up from <see cref="StartCover"/>'s dark tone. Without it the frames between the load
/// screen coming down and the cutscene taking the eye show the world still assembling and the
/// chase view, which the original never does. Self-mounting on the launcher, in the shape
/// <see cref="MissionEndFade"/> uses for the ending's black-out; the ramp itself is
/// <see cref="StartCover"/>, so the rule is decided off engine.
/// </summary>
public sealed partial class SessionStartFade : Node
{
    private readonly StartCover _cover;
    private readonly Func<bool> _ready;
    private CanvasLayer? _hudLayer;
    private ColorRect? _rect;
    private bool _logged;

    private SessionStartFade(StartCover cover, Func<bool> ready)
    {
        Name = "session_start_fade";
        _cover = cover;
        _ready = ready;
    }

    /// <summary>Is the cover done with, so the launcher can drop it?</summary>
    public bool Finished => _cover.Finished;

    /// <summary>The alpha the cover is painting, exposed for a suite to read back.</summary>
    public float Alpha => _cover.Alpha;

    /// <summary>The layer the cover draws on, exposed so a suite can assert what it stands over
    /// without reaching into the tree by name.</summary>
    public CanvasLayer? Layer => _hudLayer;

    /// <summary>Builds the cover for one session start, or null under <c>--det</c>, where nothing
    /// may stand over the world: a pinned golden hashes the frame this would paint over, and
    /// <c>--frames=N</c> counts from the first world frame. <paramref name="ready"/> is the
    /// session's own answer to whether that first frame is ready.</summary>
    public static SessionStartFade? Build(bool det, Func<bool> ready)
    {
        var cover = new StartCover(det);
        return cover.Enabled ? new SessionStartFade(cover, ready) : null;
    }

    public override void _Ready()
    {
        _hudLayer = new CanvasLayer { Layer = HudLayers.SessionStartFade, Name = "session_start_fade_layer" };
        _rect = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore };
        _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _hudLayer.AddChild(_rect);
        AddChild(_hudLayer);
        Refresh();
    }

    public override void _Process(double delta) => Tick((float)delta);

    /// <summary>One frame of the cover, exposed because every suite runs inside a single
    /// <c>_Ready</c> and never yields a frame, so nothing in engine reaches <c>_Process</c>. Wall
    /// time, like the score: a held or stepped session must not strand the cover half opaque.
    /// </summary>
    public void Tick(float dt)
    {
        bool held = !_cover.Released;
        _cover.Advance(dt, _ready());
        Refresh();
        if (held && _cover.Released && !_logged)
        {
            _logged = true;
            string why = _cover.TimedOut ? "no first frame reported, hold cap" : "the session's first frame";
            Log.Info("ui", $"start cover: held {_cover.HeldSeconds:0.00} s ({why}), fading over {StartCover.FadeSeconds} s");
        }
    }

    private void Refresh()
    {
        if (_rect == null || _hudLayer == null)
        {
            return;
        }

        float alpha = _cover.Alpha;
        _rect.Color = new Color(StartCover.ToneR, StartCover.ToneG, StartCover.ToneB, alpha);
        _hudLayer.Visible = alpha > 0f;
    }
}
