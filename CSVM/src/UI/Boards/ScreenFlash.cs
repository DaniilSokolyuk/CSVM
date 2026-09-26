using System.Collections.Generic;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Boards;

/// <summary>
/// The full-screen colour wash, two channels over one pixel per pane. The RAMP channel is the
/// <c>FBFX_COLOR_FROM_TO</c> wash a close HE, AP or flak burst authors, ramping the picture from
/// one RGBA to another, routed to the pane(s) whose camera the burst was near, replacing on
/// overlap. The BLEND channel is the victim-routed wash of a sonic, flash or smoke hit
/// (<see cref="BlendWash"/>), addressed to the pane flying the struck aircraft, blending on
/// overlap. They compose at paint time only (<see cref="BlendWash.Composite"/>). Composition
/// rules, routing and HUD layering: this module's entry in docs/architecture.md.
/// ⚠ One state per pane, not one global state: in splitscreen each pane is its own picture, and
/// painting the window instead would wash the gutters and the empty 3P quadrant, which are not
/// part of any picture. The original's single machine-wide wash is deliberately not reproduced.
/// </summary>
public sealed partial class ScreenFlash : Node
{
    private readonly List<ColorRect> _rects = new();
    private readonly List<CanvasLayer> _layers = new();
    private readonly List<Ramp> _ramps = new();
    private readonly List<BlendWash> _washes = new();
    // What each pane's rect currently shows: the two channels composited.
    private readonly List<Color> _painted = new();
    // Reused by every Play call, so routing a wash allocates nothing.
    private readonly List<int> _selected = new();

    private Flight.Camera.ViewerSet? _viewers;

    /// <summary>The colour currently washed over pane 0's picture, or a fully transparent one when
    /// nothing is running there. The single-player readout (one pane), kept for the `fbfx-flash`
    /// suite's timing checks; <see cref="CurrentFor"/> is the per-pane one.</summary>
    public Color Current => CurrentFor(0);

    /// <summary>Whether ANY pane has a ramp running.</summary>
    public bool Running
    {
        get
        {
            foreach (var ramp in _ramps)
                if (ramp.Running)
                    return true;
            return false;
        }
    }

    /// <summary>How many rendered views this overlay was built over, the rig count.</summary>
    public int PaneCount => _ramps.Count;

    /// <summary>Builds one hidden overlay per rendered view. <paramref name="hudParents"/> is each
    /// rig's <c>HudParent</c>, the window root with one player, the pane's SubViewport with
    /// several. <paramref name="viewers"/> is the session's viewer set, read INDEX-ALIGNED with
    /// those parents because both come from the same rig list; null (a build with no session behind
    /// it) leaves every ramp painting every pane, as it did before the routing existed.</summary>
    public static ScreenFlash Build(IEnumerable<Node> hudParents, Flight.Camera.ViewerSet? viewers = null)
    {
        var flash = new ScreenFlash { Name = "screen_flash", _viewers = viewers };
        foreach (var parent in hudParents)
        {
            // Hidden until a ramp runs, so a session that never sees a close burst renders exactly
            // what it rendered before this existed.
            var canvas = new CanvasLayer
            {
                Layer = HudLayers.WorldOverlay,
                Name = "screen_flash",
                Visible = false,
            };
            var rect = new ColorRect
            {
                Color = new Color(0f, 0f, 0f, 0f),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            canvas.AddChild(rect);
            parent.AddChild(canvas);
            flash._layers.Add(canvas);
            flash._rects.Add(rect);
            flash._ramps.Add(new Ramp());
            flash._washes.Add(new BlendWash());
            flash._painted.Add(new Color(0f, 0f, 0f, 0f));
        }
        return flash;
    }

    /// <summary>Starts a ramp in every pane the burst reaches, replacing whatever those panes were
    /// running. <paramref name="runTime"/> is the event's authored run time; a non-positive one
    /// shows nothing. <paramref name="origin"/> is the burst's world point and
    /// <paramref name="radiusSquared"/> its def's own <c>PLAYER_RANGE</c> gate in metres squared
    /// (0 = ungated, every pane).</summary>
    public void Play(Color from, Color to, float runTime, Vector3 origin, float radiusSquared)
    {
        if (runTime <= 0f)
            return;
        SelectPanes(origin, radiusSquared);
        foreach (int i in _selected)
        {
            var ramp = _ramps[i];
            ramp.From = from;
            ramp.To = to;
            ramp.RunTime = runTime;
            ramp.Elapsed = 0f;
            ramp.Running = true;
            ramp.Colour = from;
            Apply(i);
        }
    }

    /// <summary>Addresses a blend wash to the pane whose player flies the struck aircraft.
    /// <paramref name="playerIndex"/> is the victim's <c>FlightController.PlayerIndex</c>: a human
    /// seat's is its pane index, an AI's is out of range and paints nothing (an AI has no pane).
    /// Blends into a wash already running there; envelope, blending, a non-positive duration and
    /// the start delay are <see cref="BlendWash.Start"/>'s. Nothing here reads a camera: the ramp
    /// channel's proximity routing is <see cref="Play"/>'s alone.</summary>
    public void PlayBlend(int playerIndex, Color colour, float weight, float durationSeconds, float startDelaySeconds = 0f)
    {
        if (playerIndex < 0 || playerIndex >= _washes.Count)
            return;
        _washes[playerIndex].Start(colour, weight, durationSeconds, startDelaySeconds);
        Apply(playerIndex);
    }

    /// <summary>The colour currently washed over one pane's picture: both channels composited,
    /// which is exactly the ramp's own colour while no blend wash runs there.</summary>
    public Color CurrentFor(int pane) =>
        pane >= 0 && pane < _painted.Count ? _painted[pane] : new Color(0f, 0f, 0f, 0f);

    /// <summary>Whether one pane has a ramp running.</summary>
    public bool RunningFor(int pane) => pane >= 0 && pane < _ramps.Count && _ramps[pane].Running;

    /// <summary>One pane's blend-channel state, for a consumer or a suite that needs the channel
    /// on its own rather than the composite: null for a pane this was not built over.</summary>
    public BlendWash? BlendFor(int pane) => pane >= 0 && pane < _washes.Count ? _washes[pane] : null;

    public override void _Process(double delta)
    {
        // Sim time, so a halt freezes the wash and --det reproduces it frame for frame.
        Advance(GameClock.Current?.FrameDt ?? (float)delta);
    }

    /// <summary>Advances every pane's two channels by <paramref name="dt"/> sim seconds and
    /// repaints the panes whose state moved. <see cref="_Process"/>'s body, exposed so a suite can
    /// step the envelope on its own clock.</summary>
    public void Advance(float dt)
    {
        for (int i = 0; i < _ramps.Count; i++)
        {
            bool moved = StepRamp(_ramps[i], dt);
            moved |= _washes[i].Step(dt);
            if (moved)
                Apply(i);
        }
    }

    // One pane's ramp step: true when the ramp was running, so the pane needs repainting.
    private static bool StepRamp(Ramp ramp, float dt)
    {
        if (!ramp.Running)
            return false;
        ramp.Elapsed += dt;
        if (ramp.Elapsed >= ramp.RunTime)
        {
            // The original re-arms its frame-buffer effect for the current frame only, every
            // tick the handler runs; once the event completes nothing re-arms it and the wash is
            // simply gone. So the ramp does not hold its `to` colour, it ends.
            ramp.Running = false;
            ramp.Colour = new Color(0f, 0f, 0f, 0f);
            return true;
        }
        ramp.Colour = ramp.From.Lerp(ramp.To, ramp.Elapsed / ramp.RunTime);
        return true;
    }

    // The panes a burst at `origin` washes, into _selected: every pane whose own camera is within
    // the def's authored gate of it, never "the hit player". Two of the three carriers are ground
    // effects that play on terrain/water impacts, where no aircraft was struck at all.
    private void SelectPanes(Vector3 origin, float radiusSquared)
    {
        _selected.Clear();
        var cameras = _viewers?.Cameras;
        // Ungated, or no viewer set to ask: every pane. The count check is the index-alignment
        // contract between panes and cameras; a mismatched set is not one this can index into.
        if (radiusSquared <= 0f || cameras == null || cameras.Count != _rects.Count)
        {
            for (int i = 0; i < _rects.Count; i++)
                _selected.Add(i);
            return;
        }
        int nearest = -1;
        float nearestSq = float.MaxValue;
        for (int i = 0; i < cameras.Count; i++)
        {
            var cam = cameras[i];
            if (cam == null || !GodotObject.IsInstanceValid(cam))
                continue;
            float distSq = cam.GlobalPosition.DistanceSquaredTo(origin);
            if (distSq <= radiusSquared)
                _selected.Add(i);
            if (distSq < nearestSq)
            {
                nearestSq = distSq;
                nearest = i;
            }
        }
        // The gate already fired against rig 0's camera, which can disagree with a pane's own at
        // the margin; fall back to the nearest pane rather than washing nobody.
        if (_selected.Count == 0 && nearest >= 0)
            _selected.Add(nearest);
    }

    // Paints one pane: the ramp's colour with the blend wash composited over it. The ramp is
    // clamped per channel first, as the handler does before it packs the pixel, so an
    // out-of-range author cannot bleed; the composite clamps its own result.
    private void Apply(int pane)
    {
        var wash = _washes[pane];
        Color c = BlendWash.Composite(_ramps[pane].Colour.Clamp(), wash.Colour, wash.Weight);
        _painted[pane] = c;
        _rects[pane].Color = c;
        _layers[pane].Visible = c.A > 0f;
    }

    // One pane's ramp. A class rather than a struct because these are held in a list and
    // advanced in place every frame.
    private sealed class Ramp
    {
        public Color From;
        public Color To;
        public float RunTime;
        public float Elapsed;
        public bool Running;
        // The ramp's own contribution this frame, before the blend channel is composited over it.
        public Color Colour = new(0f, 0f, 0f, 0f);
    }
}
