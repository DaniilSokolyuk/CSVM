using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Camera;
using CSVM.Mech3;
using CSVM.Mech3.Anim;
using CSVM.UI.Overlays;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Labs;

/// <summary>
/// The animation debugger (<c>--anim-lab</c>): the chapter world as a quiet stage, reset states
/// and mission setup applied but nothing playing, with def playback under a deterministic
/// fixed-dt clock, a filterable def picker, an authored-vs-fired timeline
/// (<see cref="AnimTimeline"/>), and a transport button panel. The camera is the
/// <see cref="SpectatorCamera"/> freecam, so transport is on buttons; clicking an object hands
/// it to the shared <see cref="SelectionService"/> and the camera follows its ancestor ladder.
/// The clock is the session's <see cref="GameClock"/>, which the lab only drives: pause halts
/// it, step queues one, the speed selector scales it. Picker/timeline/transport hide in a
/// scripted <c>--screenshot</c>; <c>--debug-anim-ui</c> forces them visible. Bindings:
/// docs/controls.md. Ordering rules and determinism boundaries: docs/architecture.md.
/// </summary>
public sealed partial class AnimLab : Node
{
    /// <summary>The simulation step: the runtime advances in exact 1/60 s steps whatever the
    /// render rate, so a playback's dispatch times are a function of the step count alone.</summary>
    public const float FixedDt = GameClock.FixedDt;

    /// <summary>The master seed a lab launch pins when no <c>--seed=N</c> is given, so two runs
    /// replay the same dice.</summary>
    public const ulong DefaultSeed = Rng.DefaultSeed;

    /// <summary>Show the interactive UI, status line, transport, picker and timeline. On by
    /// default; off for a scripted <c>--screenshot</c> (kept byte-identical) unless
    /// <c>--debug-anim-ui</c> forces it on. When off, the runtime hooks are never attached.</summary>
    public bool ShowUi = true;

    // The stage's distance in front of the camera. A large-fireball/smokeball cluster is several
    // metres across; this frames it without clipping the near plane. TUNE.
    private const float StageAnchorDist = 55f;

    // The transport time scales, slowest to fastest (buttons + the current-speed
    // readout use these). 1× is the real-time default; below it is slow-mo, above it is
    // fast-forward for skimming a long loop.
    private static readonly float[] Speeds = { 0.1f, 0.25f, 1f, 2f, 4f };

    private readonly AnimRuntime _runtime;
    private readonly AnimProgram _program;
    private readonly SpectatorCamera _cam;
    // The pre-built, pre-indexed effect/crash stage (its effect templates + player anchor set), which
    // the lab repositions a fixed offset in front of the camera on each fresh play; also the fallback
    // anchor a still-placeless def is staged on. Owned by the session tree, not disposed here.
    private readonly Node3D _stageAnchor;
    private readonly ulong _seed;
    private readonly string? _playOnLaunch;   // --play-anim=<name>
    private readonly bool _autoFrame;         // no --pos/--direction: frame the played def
    // The session archives, kept open for the whole lab session (WorldSession.Options
    // .TexturesOutliveBuild + .SoundsOutliveBuild) so puffers/decals can be built at any playhead
    // time. The lab owns their disposal; it is the only mode that owns the SOUND archive that far
    //, every other mode closes that one when the build scope ends, textures being session-owned
    // everywhere.
    private readonly TextureArchive _textures;
    private readonly SoundArchive? _sounds;
    // ItemList row → index into _program.Defs (the filtered view).
    private readonly List<int> _rows = new();

    // Render-pose smoothing for the interactive FixedAccum clock: each live transform-motion
    // target keeps its last two SIM poses, drawn at the clock's sub-step fraction between them.
    // The true sim pose is restored before any step runs, so held-pose seeding never reads a
    // render pose. See docs/architecture.md.
    private readonly Dictionary<Node3D, (Transform3D Prev, Transform3D Curr)> _renderPoses = new();

    private Label? _status;
    private AnimTimeline? _timeline;
    private LineEdit? _filter;
    private ItemList? _list;
    private Control? _pickerPanel;
    private Button? _pauseBtn;

    private bool _launched;      // first-frame housekeeping (the --play-anim auto-play) done
    private int _steps;          // sim steps since Play/Restart (status readout)
    private float _playhead;     // sim seconds since Play/Restart
    private string? _defName;    // the selected def (null until --play-anim / Play)
    private int _instances;      // how many (def, anchor) instances the last Play started
    private bool _stopped;       // Stop: _defName kept for the status line, playback torn down
    private bool _ambient;       // ambient passes running
    private string _followName = "";  // the object the camera is following, for the status

    // Timeline tracking. Set up BEFORE AnimRuntime.Play, so the def's t=0 dispatches (which fire
    // synchronously inside Play) are attributed to the right lane.
    private AnimDefinition? _timelineDef;  // the specific def the timeline focuses on
    private Node3D? _timelineAnchor;       // captured from the first instance-start of _timelineDef
    private bool _anchorCaptured;
    private bool _trackChildren;           // CALL_ANIMATION children tracked (off once ambient runs)

    // The stage's snapshot transform, reused across a Restart.
    private Transform3D _stageAnchorXform = Transform3D.Identity;

    public AnimLab(AnimRuntime runtime, AnimProgram program, SpectatorCamera cam,
        Node3D stageAnchor, TextureArchive textures, SoundArchive? sounds,
        ulong seed, string? playAnim, bool autoFrame)
    {
        _runtime = runtime;
        _program = program;
        _cam = cam;
        _stageAnchor = stageAnchor;
        _textures = textures;
        _sounds = sounds;
        _seed = seed;
        _playOnLaunch = playAnim;
        _autoFrame = autoFrame;
    }

    /// <summary>The session's shared selection. The lab's camera follows its current rung; the
    /// binding is independent of <see cref="ShowUi"/>, so a scripted <c>--debug-select</c> run
    /// still tracks what it picked.</summary>
    public SelectionService? Selection { get; init; }

    // The session clock the transport drives. The lab never owns it, the session picks
    // the mode (accumulator when interactive, one fixed step per frame when scripted).
    private static GameClock? Clock => GameClock.Current;

    private float PlayheadTime => _playhead;

    public override void _Ready()
    {
        if (Selection != null)
        {
            Selection.Changed += OnSelectionChanged;
        }
        if (!ShowUi)
        {
            return;
        }
        BuildUi();
        // Feed the picker/timeline/transport from the runtime, not from --debug-anim log text.
        // Attached here (only when the UI is shown) so a plain scripted run leaves them null and
        // the runtime's null-conditional dispatch stays zero-cost.
        _runtime.OnEventDispatched = OnDispatch;
        _runtime.OnInstanceStarted = OnInstanceStarted;
        _runtime.OnInstanceFinished = OnInstanceFinished;
    }

    public override void _ExitTree()
    {
        if (Selection != null)
        {
            Selection.Changed -= OnSelectionChanged;
        }
        // The lab owns the session archives (kept open so effects can build mid-session); the
        // session teardown or quit is when they finally close.
        _textures.Dispose();
        _sounds?.Dispose();
    }

    public override void _Process(double delta)
    {
        if (!_launched)
        {
            // Deferred to the first frame rather than done at construction, so the auto-frame
            // wins over any startup framing and the play start is frame-exact for a scripted run.
            _launched = true;
            if (_playOnLaunch != null)
            {
                Play(_playOnLaunch);
            }
        }
        // The clock decides how many sim steps this rendered frame is worth (none while halted);
        // each one is a separate Advance, because the event scheduler resolves per step.
        if (Clock is { } clock)
        {
            if (clock.Steps > 0)
            {
                RestoreSimPoses();
                for (int i = 0; i < clock.Steps; i++)
                {
                    Step(clock.Dt);
                }
                SnapshotSimPoses();
            }
            ApplyRenderPoses(clock.StepFraction);
        }
        _timeline?.SetPlayhead(PlayheadTime);
        UpdateStatus();
    }

    // ---- transport --------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent @event)
    {
        // Only a few keys: the camera owns WASD/QE/arrows/Space/Z/IJKL/Shift/Ctrl/RMB/wheel, and
        // M/C belong to the mesh lab and the collider overlay.
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }
        switch (key.Keycode)
        {
            case Key.P:
                TogglePause();
                break;
            case Key.R:
                Restart();
                break;
            case Key.Period:
                StepFrame();
                break;
            // ⚠ F18, not F: the shared SpectatorCamera this lab flies takes F for its target key,
            // and a raw-polled camera would re-lock behind SetInputAsHandled (BL-279's mechanism).
            case Key.F18:
                TogglePicker();
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    /// <summary>Plays one def by ANIMATION_NAME the way a startanim starts (RESET_STATE, then
    /// Start per anchor, <see cref="AnimRuntime.Play"/>), from a re-pinned RNG and a zeroed
    /// playhead. The picker's overload focuses the timeline on the exact def picked; this entry
    /// (also <c>--play-anim</c>) focuses on the first def carrying the name.</summary>
    public void Play(string name) => Play(name, _program.ByAnimName(name).FirstOrDefault());

    private static bool SameAnimName(AnimDefinition a, AnimDefinition b) =>
        !string.IsNullOrEmpty(a.AnimName)
        && string.Equals(a.AnimName, b.AnimName, StringComparison.OrdinalIgnoreCase);

    private static string NodeName(Node3D n) =>
        n.HasMeta(AnimRuntime.NameMeta) ? n.GetMeta(AnimRuntime.NameMeta).AsString() : n.Name.ToString();

    private static string AnchorLabel(Node3D? anchor) => anchor == null ? "(global)" : NodeName(anchor);

    private static Label Small(string text)
    {
        var label = new Label { Text = text, Modulate = new Color(1, 1, 1, 0.65f) };
        label.AddThemeFontSizeOverride("font_size", 11);
        return label;
    }

    // ---- render-pose smoothing (see the _renderPoses field note) -----------------------------

    // Puts every smoothed target back on its exact sim pose, so the steps about to run
    //, and any event they fire that seeds a held pose from the live transform, never see an
    // interpolated render pose. Sim state stays a function of the step count alone.
    private void RestoreSimPoses()
    {
        foreach (var (target, pair) in _renderPoses)
        {
            if (IsInstanceValid(target))
            {
                target.Transform = pair.Curr;
            }
        }
    }

    // Rolls the pose pairs after this frame's steps: each live transform-motion
    // target's Curr becomes Prev and its fresh sim pose becomes Curr. A target whose motion
    // ended leaves the set at its final sim pose (just restored, never re-interpolated).
    private void SnapshotSimPoses()
    {
        var live = new Dictionary<Node3D, (Transform3D, Transform3D)>();
        foreach (var motion in _runtime.Motions.Live)
        {
            if (motion.Channel != MotionChannel.Transform || !IsInstanceValid(motion.Target))
            {
                continue;
            }
            var now = motion.Target.Transform;
            live[motion.Target] = (_renderPoses.TryGetValue(motion.Target, out var prev) ? prev.Curr : now, now);
        }
        _renderPoses.Clear();
        foreach (var (target, pair) in live)
        {
            _renderPoses[target] = pair;
        }
    }

    // Draws each smoothed target between its last two sim poses at the clock's
    // sub-step fraction. FixedStep pins the fraction to 1, an identity rewrite, so scripted
    // captures stay byte-identical; while halted the fraction holds still, so a paused frame
    // cannot wobble between stale poses.
    private void ApplyRenderPoses(float fraction)
    {
        foreach (var (target, pair) in _renderPoses)
        {
            if (IsInstanceValid(target))
            {
                target.Transform = pair.Prev.InterpolateWith(pair.Curr, fraction);
            }
        }
    }

    private void Step(float dt)
    {
        // Advanced BEFORE Advance so a fired mark lands at the time it actually fired; t=0
        // events (AnimRuntime.Play) use dt=0 and see a zero playhead, so they stamp at t=0.
        _steps++;
        _playhead += dt;
        _runtime.Advance(dt);
    }

    private void TogglePause()
    {
        if (Clock is { } clock)
        {
            clock.Halted = !clock.Halted;
        }
    }

    // A single frame step, freezing playback first, the "step" transport action for both the
    // button and the . key. The step itself lands in this frame's _Process, where every other
    // sim consumer takes the same one step.
    private void StepFrame()
    {
        if (Clock is { } clock)
        {
            clock.Halted = true;
            clock.StepOnce();
        }
    }

    private void SetSpeed(float scale)
    {
        if (Clock is { } clock)
        {
            clock.Scale = scale;
        }
    }

    private void SetAmbient(bool on)
    {
        if (on == _ambient)
        {
            return;
        }
        if (on)
        {
            _runtime.StartAmbient();
            _trackChildren = false; // don't attribute the ambient cascade to the played def's timeline
        }
        else
        {
            // Keep the played def running (by name) as the rest of the world returns to quiet.
            _runtime.StopAmbient(_defName);
            _trackChildren = _defName != null;
        }
        _ambient = on;
    }

    private void TogglePicker()
    {
        if (_pickerPanel != null)
        {
            _pickerPanel.Visible = !_pickerPanel.Visible;
        }
    }

    private void Play(string name, AnimDefinition? focus, bool freshAnchor = true)
    {
        if (_defName != null)
        {
            _runtime.Stop(_defName);
        }
        _runtime.Reseed();
        // A replay re-poses its targets outside the step loop; a stale render pair would put
        // the old pose back on top of the fresh RESET_STATE.
        _renderPoses.Clear();
        _steps = 0;
        _playhead = 0f;
        if (Clock is { } clock)
        {
            clock.Halted = false;
            clock.ResetAccumulator();
        }
        // Set the timeline focus BEFORE Start: AnimRuntime.Play fires the def's t=0 events
        // synchronously, and the dispatch hooks need _timelineDef already in place to attribute
        // them. The anchor is captured from the first instance-start (which precedes any dispatch).
        _timelineDef = focus;
        _timelineAnchor = null;
        _anchorCaptured = false;
        _trackChildren = !_ambient;
        _timeline?.SetDef(_program, _timelineDef, "");
        // Staging anchor for a placeless on-call def: a dummy at a fixed offset in front of the
        // camera, so it plays where you are looking instead of at the world origin.
        var anchor = StageAnchor(freshAnchor);
        var started = _runtime.Play(name, anchor);
        if (started.Count == 0)
        {
            Log.Info("ui", $"anim-lab: no definition named '{name}' among this program's {_program.Defs.Count} defs, check --chapter/--mission (F lists them)");
            _timeline?.Clear();
            return;
        }
        _defName = name;
        _stopped = false;
        _instances = started.Count;
        bool placeless = started.Any(s => ReferenceEquals(s.Anchor, anchor));
        Log.Info("ui", $"anim-lab: playing '{name}', {started.Count} instance(s), seed {_seed}{(placeless ? " (placeless, staged in front of the camera)" : "")}");
        if (_autoFrame)
        {
            // Placeless: frame + follow the staging dummy, so the in-front-of-camera effect is
            // centred. Anything with a real anchor frames its own target.
            if (placeless)
            {
                FocusNode(anchor);
            }
            else
            {
                FrameOn(started);
            }
        }
    }

    // Repositions the effect/crash stage a fixed offset in front of the camera on a
    // `fresh` Play (so the effect sits where you are looking), and leaves it put on
    // Restart, so a seeded replay lands in exactly the same spot. Returns it as the fallback anchor
    // for a still-placeless def.
    private Node3D StageAnchor(bool fresh)
    {
        if (fresh)
        {
            var cam = _cam.Camera.GlobalTransform;
            _stageAnchorXform = new Transform3D(Basis.Identity, cam.Origin - cam.Basis.Z * StageAnchorDist);
        }
        _stageAnchor.GlobalTransform = _stageAnchorXform;
        return _stageAnchor;
    }

    // Restart = AnimRuntime.Stop (tear down the def's live
    // motions/puffers/lights/sounds) → re-pin the RNG → re-apply RESET_STATE → Start, playhead
    // back at step 0, the visually-identical replay the fixed clock + seed exist for.
    private void Restart()
    {
        if (_defName == null)
        {
            return;
        }
        // freshAnchor:false, reuse the staging dummy's snapshot so a placeless def's replay is
        // spatially identical, matching the seeded + fixed-dt clock's visual-identity contract.
        Play(_defName, _timelineDef, freshAnchor: false);
    }

    private void StopPlayback()
    {
        if (_defName == null || _stopped)
        {
            return;
        }
        _runtime.Stop(_defName);
        // The stop tears the def's motions down mid-flight; without this the stale render
        // pairs would keep re-writing the last interpolated pose every frame.
        _renderPoses.Clear();
        _stopped = true;
        Log.Info("ui", $"anim-lab: stopped '{_defName}'");
    }

    // ---- selection follow ----------------------------------------------------------------------

    // The lab's camera tracks the shared selection's current rung: a fresh click frames and orbits
    // what was hit, and a PgUp/PgDn ladder walk re-aims onto the wider object WITHOUT re-framing,
    // re-framing on every rung would fling the camera out to the whole zeppelin's radius mid-walk.
    private void OnSelectionChanged(SelectionService selection, bool fresh)
    {
        if (selection.Current is not { } node)
        {
            return;
        }
        if (fresh)
        {
            FocusNode(node);
        }
        else
        {
            _cam.FollowNode(node);
            _followName = NodeName(node);
        }
        Log.Info("ui", $"anim-lab: following '{_followName}'");
    }

    // Frame the camera on a node and start following it (position tracks the node as it moves).
    private void FocusNode(Node3D node)
    {
        var aabb = OrbitCamera.MergedAabb(node);
        if (aabb.Size.LengthSquared() < 1e-4f)
        {
            // A mesh-less pivot/group node: orbit a nominal box around it, not the world origin.
            aabb = new Aabb(node.GlobalPosition - Vector3.One * 10f, Vector3.One * 20f);
        }
        _cam.Frame(aabb);
        _cam.FollowNode(node);
        _followName = NodeName(node);
    }

    // ---- runtime → picker/timeline hooks -----------------------------------------------------

    private void OnDispatch(EventDispatch e)
    {
        if (_timelineDef == null)
        {
            return;
        }
        float t = PlayheadTime;
        if (ReferenceEquals(e.Def, _timelineDef)
            && (!_anchorCaptured || ReferenceEquals(e.Anchor, _timelineAnchor)))
        {
            _timeline?.AddPrimaryMark(e.Sequence, e.EventIndex, t);
            return;
        }
        // A sibling def sharing the ANIMATION_NAME is another template instance, not a child;
        // and once ambient is running its instances are not the played def's cascade.
        if (!_trackChildren || SameAnimName(e.Def, _timelineDef))
        {
            return;
        }
        _timeline?.AddChildMark(e.Def, e.Anchor, e.Sequence, e.EventIndex, t);
    }

    private void OnInstanceStarted(AnimDefinition def, Node3D? anchor)
    {
        if (_timelineDef == null)
        {
            return;
        }
        if (ReferenceEquals(def, _timelineDef))
        {
            if (!_anchorCaptured)
            {
                _timelineAnchor = anchor;
                _anchorCaptured = true;
                _timeline?.SetAnchorLabel(AnchorLabel(anchor));
            }
            return;
        }
        if (!_trackChildren || SameAnimName(def, _timelineDef))
        {
            return;
        }
        _timeline?.AddChild(def, anchor, PlayheadTime);
    }

    private void OnInstanceFinished(AnimDefinition def, Node3D? anchor)
    {
        if (_timelineDef == null)
        {
            return;
        }
        if (ReferenceEquals(def, _timelineDef) && ReferenceEquals(anchor, _timelineAnchor))
        {
            _timeline?.MarkPrimaryFinished();
        }
        else if (!SameAnimName(def, _timelineDef))
        {
            _timeline?.MarkChildFinished(def, anchor);
        }
    }

    // Frames the camera on the played def and follows its anchor (or first resolved
    // target node, AnimRuntime.FrameTarget), so a moving def (the train) stays in
    // view until the user takes the camera somewhere with WASD/QE.
    private void FrameOn(List<(AnimDefinition Def, Node3D? Anchor)> started)
    {
        foreach (var (def, anchor) in started)
        {
            if (_runtime.FrameTarget(def, anchor) is { } node)
            {
                FocusNode(node);
                return;
            }
        }
        Log.Info("ui", $"anim-lab: '{_defName}' resolves no frameable node, camera left as-is");
    }

    // ---- UI ----------------------------------------------------------------------------------

    private void BuildUi()
    {
        var layer = new CanvasLayer();
        AddChild(layer);

        _status = new Label
        {
            Position = new Vector2(12, 12),
            Modulate = new Color(0.75f, 0.9f, 1f),
        };
        _status.AddThemeFontSizeOverride("font_size", 14);
        layer.AddChild(_status);

        BuildTransport(layer);
        BuildPicker(layer);
        BuildTimeline(layer);
    }

    private void BuildTransport(CanvasLayer layer)
    {
        // Anchored just ABOVE the timeline (which owns the bottom 210 px) so it never clashes
        // with the status line + key-hint at top-left.
        var panel = new PanelContainer { SelfModulate = new Color(1, 1, 1, 0.85f) };
        panel.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        panel.GrowVertical = Control.GrowDirection.Begin;
        panel.Position = new Vector2(8, -214);
        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, 6);
        }
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);

        _pauseBtn = TBtn("⏸ Pause", TogglePause);
        row.AddChild(_pauseBtn);
        row.AddChild(TBtn("⏭ Step", StepFrame));
        row.AddChild(TBtn("⟲ Restart", Restart));
        row.AddChild(TBtn("⏹ Stop", StopPlayback));

        var ambient = new CheckButton { Text = "Ambient", FocusMode = Control.FocusModeEnum.None };
        ambient.Toggled += on => { GetViewport().GuiReleaseFocus(); SetAmbient(on); };
        row.AddChild(ambient);

        row.AddChild(new VSeparator());
        row.AddChild(Small("speed"));
        var group = new ButtonGroup();
        foreach (var s in Speeds)
        {
            float captured = s;
            var b = new Button
            {
                Text = $"{s:0.##}×",
                ToggleMode = true,
                ButtonGroup = group,
                FocusMode = Control.FocusModeEnum.None,
                ButtonPressed = Mathf.IsEqualApprox(s, 1f),
            };
            b.Pressed += () => { GetViewport().GuiReleaseFocus(); SetSpeed(captured); };
            row.AddChild(b);
        }

        margin.AddChild(row);
        panel.AddChild(margin);
        layer.AddChild(panel);
    }

    private void BuildPicker(CanvasLayer layer)
    {
        // Anchored top-RIGHT (inside a full-rect Control, growing left) so it never collides with
        // the status/transport at top-left, the same layout the livery lab uses.
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var panel = new PanelContainer { SelfModulate = new Color(1, 1, 1, 0.85f) };
        panel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        panel.GrowHorizontal = Control.GrowDirection.Begin;
        panel.Position = new Vector2(-8, 8);

        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, 8);
        }
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        box.AddChild(new Label { Text = $"DEF PICKER  ({_program.Defs.Count} defs)" });
        box.AddChild(Small("type to filter · Enter plays · F hides"));

        _filter = new LineEdit
        {
            PlaceholderText = "filter (name / activation / anchor)…",
            CustomMinimumSize = new Vector2(320, 0),
        };
        _filter.TextChanged += _ => RefreshList();
        _filter.TextSubmitted += _ => PlayFirstFiltered();
        box.AddChild(_filter);

        _list = new ItemList { CustomMinimumSize = new Vector2(320, 360) };
        _list.ItemActivated += index => PlayRow((int)index);
        box.AddChild(_list);

        margin.AddChild(box);
        panel.AddChild(margin);
        root.AddChild(panel);
        layer.AddChild(root);
        _pickerPanel = root;
        RefreshList();
    }

    private void BuildTimeline(CanvasLayer layer)
    {
        _timeline = new AnimTimeline();
        _timeline.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _timeline.OffsetTop = -210;
        _timeline.OffsetBottom = 0;
        _timeline.OffsetLeft = 0;
        _timeline.OffsetRight = 0;
        layer.AddChild(_timeline);
    }

    // Rebuilds the ItemList from the filter text. Matches a case-insensitive substring against
    // the anim name, the activation and the anchor name, the three columns each row shows.
    private void RefreshList()
    {
        if (_list == null)
        {
            return;
        }
        string q = _filter?.Text.Trim() ?? "";
        _list.Clear();
        _rows.Clear();
        for (int i = 0; i < _program.Defs.Count; i++)
        {
            var d = _program.Defs[i];
            string anim = d.AnimName ?? "";
            string row = $"{(anim.Length > 0 ? anim : "(no anim-name)")}   ·  {d.Activation}   ·  @{(d.Name.Length > 0 ? d.Name : ", ")}";
            if (q.Length > 0
                && !anim.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !d.Activation.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !d.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            int idx = _list.AddItem(row);
            // A def with no ANIMATION_NAME can't be started through Play, dim it and note why.
            if (anim.Length == 0)
            {
                _list.SetItemDisabled(idx, true);
                _list.SetItemCustomFgColor(idx, new Color(0.5f, 0.5f, 0.55f));
            }
            _rows.Add(i);
        }
    }

    private void PlayFirstFiltered()
    {
        for (int row = 0; row < _rows.Count; row++)
        {
            if (!string.IsNullOrEmpty(_program.Defs[_rows[row]].AnimName))
            {
                PlayRow(row);
                return;
            }
        }
    }

    private void PlayRow(int row)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return;
        }
        var def = _program.Defs[_rows[row]];
        if (string.IsNullOrEmpty(def.AnimName))
        {
            Log.Info("ui", $"anim-lab: '{def.Name}' has no ANIMATION_NAME, cannot play it directly");
            return;
        }
        Play(def.AnimName!, def);
        // Drop focus off the filter/list so the transport keys go to the lab, not into the field.
        GetViewport().GuiReleaseFocus();
    }

    private void UpdateStatus()
    {
        bool paused = Clock is { Halted: true };
        if (_pauseBtn != null)
        {
            _pauseBtn.Text = paused ? "▶ Play" : "⏸ Pause";
        }
        if (_status == null)
        {
            return;
        }
        string def = _defName == null ? "(none, pick one or --play-anim=<name>)"
            : _stopped ? $"{_defName} (stopped)"
            : $"{_defName} ({_instances} instance{(_instances == 1 ? "" : "s")})";
        string follow = _cam.Follow != null ? $"orbiting {_followName} (RMB to rotate)" : "free camera";
        _status.Text =
            $"anim-lab  t {_playhead:0.00} s (step {_steps})  {Clock?.Scale ?? 1f:0.##}×  " +
            $"{(paused ? "PAUSED" : "PLAYING")}   seed {_seed}   ambient {(_ambient ? "on" : "off")}\n" +
            $"def: {def}   {follow}   " +
            "[P pause · . step · R restart · F18 picker · F lock · click an object · PgUp/PgDn/Home/End walk its ladder · RMB look · WASD/QE move]";
    }

    private Button TBtn(string text, Action pressed)
    {
        // FocusMode None keeps a clicked button from swallowing the transport keys. Every press
        // also releases the picker's text field, so it hands the keyboard back after a filter.
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        b.Pressed += () => { GetViewport().GuiReleaseFocus(); pressed(); };
        return b;
    }
}
