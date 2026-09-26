using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CSVM.Mech3;
using CSVM.Mech3.Anim;
using CSVM.Tooling;
using CSVM.UI.Boards;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Labs;

/// <summary>
/// The world damage lab (F19) in <c>--freecam</c>/<c>--anim-lab</c>: when
/// <see cref="SelectionService"/> lands on a destructible, this panel shows its live HP pools
/// with a slider, a Kill and a Reset, the interactive twin of <c>--damage-test</c>. Elsewhere F19
/// means the aircraft's <see cref="Flight.Airframe.DamageLab"/>; the two never coexist. Only the pool
/// <see cref="DestructibleRegistry.Resolve"/> names gets controls, since a node can carry several
/// pools and driving one nothing can ever hit would read as a working feature. Lowering the
/// slider spends the difference through <see cref="AnimRuntime.DamageAt"/>, the same call a
/// rocket makes; raising it runs <see cref="AnimRuntime.ResetDestructible"/> and re-damages down,
/// since the data has no healing. The collider-census caveats live on <c>Kill</c>; the freecam
/// stage-build trap lives on <see cref="EffectsSource"/>.
/// </summary>
public sealed partial class WorldDamageLab : Node
{
    /// <summary>Seconds per slice of a scripted clock advance. The death's debris
    /// <c>OBJECT_MOTION</c> is scheduled seconds in, so a scripted kill has to tick past it; the
    /// slicing keeps each step inside the runtime's own event resolution.</summary>
    public const float TickSlice = 0.5f;

    private const float StatusPeriod = 0.25f;
    private const int PanelMargin = 8; // MarginContainer's four sides, used to size the panel to content

    private static readonly Color Amber = new(1f, 0.93f, 0.35f);
    private static readonly Color Loud = new(1f, 0.42f, 0.42f);
    private static readonly Color Dim = new(0.62f, 0.62f, 0.68f);

    private readonly SelectionService _selection;
    private readonly AnimRuntime? _runtime;
    private readonly bool _collisionBuilt;
    private readonly List<PoolRow> _rows = new();

    private CanvasLayer? _layer;
    private PanelContainer? _panel;
    private VBoxContainer? _box;
    private ScrollContainer? _scroll;
    private Label? _header;
    private Label? _reach;
    private Label? _status;
    private VBoxContainer? _rowBox;

    private bool _open;
    private bool _syncing;
    private float _statusTimer;
    private int _debugFrames = -1;
    private int _scriptRow;          // which listed pool a scripted action drives (0 = the reachable one)
    private bool _effectsAsked;
    private List<string>? _watching; // collects the definitions one damage action starts

    public WorldDamageLab(SelectionService selection, AnimRuntime? runtime, bool collisionBuilt)
    {
        _selection = selection;
        _runtime = runtime;
        _collisionBuilt = collisionBuilt;
        Name = "world_damage_lab";
    }

    /// <summary>Selects a world node by <c>cs_name</c>, the node lab's own index, borrowed so a
    /// scripted run can name the object it means instead of aiming a pick ray at it.</summary>
    public Func<string, bool>? SelectByName { get; init; }

    /// <summary>Builds (once) and returns the world-effects runtime, so a kill's fire and smoke
    /// render. <c>--freecam</c> does not build one for itself, outside flight the puffer factory is
    /// torn down after the world build, so a damage stage would start its definition and draw
    /// nothing. Null when this session cannot build one; the HP/kill/swap/reset mechanics do not
    /// depend on it.</summary>
    public Func<AnimRuntime?>? EffectsSource { get; init; }

    /// <summary><c>--debug-damage=&lt;script&gt;</c>: open the panel at launch and run an ordered
    /// script of <c>node=</c>, <c>pool=</c>, <c>hp=</c>, <c>kill</c>, <c>reset</c> and <c>tick=</c>
    /// steps against it, the scripted stand-in for pressing H and dragging the slider, which live
    /// input cannot do here.</summary>
    public string? DebugSpec { get; init; }

    /// <summary>Pixels of window kept clear below the panel, the anim lab parks its timeline strip
    /// there, plain freecam does not.</summary>
    public int BottomMargin { get; init; } = 16;

    /// <summary>Whether the panel is showing. Nothing is built until it first opens, so a capture
    /// without H, and without <c>--debug-damage</c>, renders as if this file did not exist.</summary>
    public bool IsOpen => _open;

    /// <summary>Parses <c>--debug-damage[=script]</c>: a comma-separated, <b>ordered</b> list of
    /// <c>node=&lt;cs_name&gt;</c>, <c>pool=&lt;n&gt;</c>, <c>hp=&lt;value&gt;</c>, <c>kill</c>,
    /// <c>reset</c>, <c>tick=&lt;seconds&gt;</c> and <c>open</c>. Unknown steps are reported and
    /// dropped rather than silently changing what the run does.</summary>
    public static string ParseDebugSpec(string spec, List<string>? rejected = null)
    {
        var kept = new List<string>();
        foreach (string step in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (step.Equals("open", StringComparison.OrdinalIgnoreCase)
                || step.Equals("kill", StringComparison.OrdinalIgnoreCase)
                || step.Equals("reset", StringComparison.OrdinalIgnoreCase)
                || step.StartsWith("node=", StringComparison.OrdinalIgnoreCase)
                || step.StartsWith("pool=", StringComparison.OrdinalIgnoreCase)
                || step.StartsWith("hp=", StringComparison.OrdinalIgnoreCase)
                || step.StartsWith("tick=", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(step);
                continue;
            }
            // See NodeLab.ParseDebugSpec: a supplied list takes the rejects as data instead of
            // logging them, so the spec can normalise a value engine-free.
            if (rejected != null)
            {
                rejected.Add(step);
                continue;
            }
            Log.Warn("ui", $"--debug-damage step '{step}' is not node=/pool=/hp=/kill/reset/tick=/open, ignoring it");
        }
        return string.Join(",", kept);
    }

    public override void _Ready()
    {
        _selection.Changed += OnSelectionChanged;
        if (DebugSpec != null)
        {
            Toggle();
            _debugFrames = 0;
            return;
        }
        SetProcess(false);
    }

    public override void _ExitTree()
    {
        _selection.Changed -= OnSelectionChanged;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F19 })
        {
            Toggle();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (_debugFrames >= 0)
        {
            // Deferred two frames: SelectionService runs its own scripted pick on its first frame,
            // and a script that names no node acts on whatever that pick left selected.
            _debugFrames++;
            if (_debugFrames >= 2)
            {
                _debugFrames = -1;
                RunDebugScript();
            }
        }
        if (!_open)
        {
            return;
        }
        _statusTimer += (float)delta;
        if (_statusTimer >= StatusPeriod)
        {
            _statusTimer = 0f;
            UpdateReadouts();
        }
    }

    // ---- panel ------------------------------------------------------------------------------

    /// <summary>Shows or hides the panel, building it on the first open.</summary>
    public void Toggle()
    {
        if (_layer == null)
        {
            BuildUi();
            _open = true;
            SetProcess(true);
            Rebuild();
            Log.Info("ui", $"damagelab open collision_built={_collisionBuilt} runtime={(_runtime != null)}");
            return;
        }
        _open = !_open;
        _layer.Visible = _open;
        SetProcess(_open || _debugFrames >= 0);
        if (_open)
        {
            Rebuild();
        }
        Log.Info("ui", $"damagelab {(_open ? "open" : "closed")}");
    }

    private static Label Small(string text)
    {
        var label = new Label { Text = text, Modulate = new Color(1, 1, 1, 0.65f), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeFontSizeOverride("font_size", 11);
        return label;
    }

    private static string Label(DestructibleRegistry.Instance inst)
    {
        var def = inst.Def;
        return def.AnimName is { Length: > 0 } anim
               && !anim.Equals(def.Name, StringComparison.OrdinalIgnoreCase)
            ? $"{anim}@{def.Name}"
            : def.Name.Length > 0 ? def.Name : "(unnamed)";
    }

    private static string Source(DestructibleRegistry.Instance inst) =>
        inst.Def.Archive != null ? "compiled" : "reader";

    private void BuildUi()
    {
        _layer = new CanvasLayer { Layer = HudLayers.Lab };
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);

        // Down the right edge; the node lab owns the left. Top-right, not RightWide, so
        // ResizeToContent can hug the panel's own height instead of stretching it full-screen.
        var panel = new PanelContainer { SelfModulate = new Color(1, 1, 1, 0.88f) };
        panel.AnchorLeft = 1;
        panel.AnchorRight = 1;
        panel.AnchorTop = 0;
        panel.AnchorBottom = 0;
        panel.OffsetLeft = -448;
        panel.OffsetRight = -8;
        panel.OffsetTop = 126;
        _panel = panel;

        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, PanelMargin);
        }
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        _box = box;

        box.AddChild(new Label { Text = "DAMAGE LAB: WORLD", Modulate = Amber });
        _header = Small("");
        box.AddChild(_header);
        _reach = Small("");
        box.AddChild(_reach);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _scroll = scroll;
        _rowBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _rowBox.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_rowBox);
        box.AddChild(scroll);

        _status = Small("");
        box.AddChild(_status);
        box.AddChild(Small("slider = absolute HP (down damages, up resets then re-damages) · H hides this panel"));

        margin.AddChild(box);
        panel.AddChild(margin);
        root.AddChild(panel);
        _layer.AddChild(root);
        AddChild(_layer);
        RequestResize();
    }

    // Queues ResizeToContent for after Godot's own container layout pass.
    // Container sizing (and therefore any word-wrapped label's real height) is resolved lazily on a
    // deferred call the engine queues itself when children change, so measuring synchronously right
    // after `AddChild` reads stale, pre-layout sizes, queuing ours after theirs (both FIFO on
    // the same deferred-call queue) is what makes the measurement below correct.
    private void RequestResize() => Callable.From(ResizeToContent).CallDeferred();

    // Sizes the panel to exactly what the current rows need, capped at the window height
    // minus BottomMargin: a short pool list shrinks the whole block instead of
    // leaving blank panel below it, and a pool list taller than the cap gets a real scrollbar
    // rather than being squeezed silently.
    private void ResizeToContent()
    {
        if (_panel == null || _box == null || _scroll == null || _rowBox == null || !IsInstanceValid(_panel))
        {
            return;
        }
        float available = GetViewport().GetVisibleRect().Size.Y - _panel.OffsetTop - BottomMargin;
        float rowsHeight = _rowBox.GetCombinedMinimumSize().Y;
        _scroll.CustomMinimumSize = new Vector2(0, rowsHeight);
        float natural = _box.GetCombinedMinimumSize().Y + 2 * PanelMargin;
        if (natural <= available)
        {
            _panel.OffsetBottom = _panel.OffsetTop + natural;
            return;
        }
        _panel.OffsetBottom = _panel.OffsetTop + available;
        float overflow = natural - available;
        _scroll.CustomMinimumSize = new Vector2(0, Mathf.Max(0, rowsHeight - overflow));
    }

    private Button Btn(string text, Action pressed)
    {
        // FocusMode None keeps a clicked button from swallowing the mode keys afterwards, and
        // releasing GUI focus hands the keyboard back to the camera.
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        b.Pressed += () =>
        {
            GetViewport().GuiReleaseFocus();
            pressed();
        };
        return b;
    }

    // ---- selection --------------------------------------------------------------------------

    private void OnSelectionChanged(SelectionService selection, bool fresh)
    {
        if (_open)
        {
            Rebuild();
        }
    }

    // Rebuilds the rows for the current selection: every pool anchored on the selected
    // node, plus the one a weapon hit there would actually reach, which can be an enclosing
    // node's, since a reader wildcard can grab an inner node the compiled def roots above. The
    // reachable pool is listed first and is the only one with controls.
    private void Rebuild()
    {
        _rows.Clear();
        if (_rowBox != null)
        {
            // Detached before the free, which is deferred: leaving them parented would draw the old
            // rows over the new ones for a frame.
            foreach (var child in _rowBox.GetChildren())
            {
                _rowBox.RemoveChild(child);
                child.QueueFree();
            }
        }
        var node = _selection.Current;
        if (_runtime == null)
        {
            SetHeader("no animation runtime in this session, nothing here is a destructible", Loud);
            RequestResize();
            return;
        }
        if (node == null || !IsInstanceValid(node))
        {
            SetHeader("nothing selected, click an object (PgUp/PgDn walk its ladder)", Dim);
            RequestResize();
            return;
        }
        var registry = _runtime.Destructibles;
        var reachable = registry.Resolve(node);
        var pools = registry.PoolsOn(node);
        if (reachable != null)
        {
            // First in the list whether or not it is anchored here, so the drivable pool is the one
            // a scripted run reaches by default and the one the eye lands on.
            pools.Remove(reachable);
            pools.Insert(0, reachable);
        }
        string name = SelectionService.NameOf(node);
        if (pools.Count == 0)
        {
            SetHeader(Log.Format($"'{name}' is not a destructible, and nothing up its parent chain is one"), Dim);
            RequestResize();
            return;
        }
        SetHeader(Log.Format($"'{name}', {pools.Count} pool(s)"), Amber);
        if (_reach != null)
        {
            _reach.Text = reachable == null
                ? "no pool here is reachable by weapon fire"
                : Log.Format($"a hit here damages: {Label(reachable)} on '{SelectionService.NameOf(reachable.Anchor)}'");
            _reach.Modulate = new Color(1, 1, 1, 0.8f);
        }
        for (int i = 0; i < pools.Count; i++)
        {
            AddRow(pools[i], i + 1, pools.Count, ReferenceEquals(pools[i], reachable), reachable);
        }
        UpdateReadouts();
        RequestResize();
    }

    private void SetHeader(string text, Color color)
    {
        if (_header != null)
        {
            _header.Text = text;
            _header.Modulate = color;
        }
        if (_reach != null)
        {
            _reach.Text = "";
        }
        if (_status != null)
        {
            _status.Text = "";
        }
    }

    private void AddRow(DestructibleRegistry.Instance inst, int index, int total, bool drivable,
        DestructibleRegistry.Instance? reachable)
    {
        if (_rowBox == null)
        {
            return;
        }
        var row = new PoolRow { Inst = inst, Drivable = drivable };
        var block = new VBoxContainer();
        block.AddThemeConstantOverride("separation", 2);
        var title = new Label
        {
            Text = Log.Format($"pool {index}/{total} · {Label(inst)} · {Source(inst)}{(drivable ? " · REACHABLE" : "")}"),
            Modulate = drivable ? Amber : Dim,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        title.AddThemeFontSizeOverride("font_size", 11);
        block.AddChild(title);
        row.Readout = Small("");
        block.AddChild(row.Readout);

        if (drivable)
        {
            row.Slider = new HSlider
            {
                MinValue = 0,
                MaxValue = 100,
                Step = 1,
                Value = 100,
                TickCount = 11,
                TicksOnBorders = true,
                CustomMinimumSize = new Vector2(240, 0),
            };
            row.Slider.ValueChanged += v =>
            {
                if (!_syncing)
                {
                    DriveTo(row, (float)(v / 100.0 * inst.MaxHealth), "slider");
                }
            };
            block.AddChild(row.Slider);
            var actions = new HBoxContainer();
            actions.AddThemeConstantOverride("separation", 4);
            actions.AddChild(Btn("Kill", () => Kill(row, "button")));
            actions.AddChild(Btn("Reset", () => Reset(row, "button")));
            block.AddChild(actions);
        }
        else
        {
            var why = reachable == null
                ? "no controls: nothing can damage this pool, its object has no reachable pool at all"
                : Log.Format($"no controls: a hit resolves to {Label(reachable)}, so this pool is unreachable by damage and holds its own HP");
            var note = Small(why);
            note.Modulate = Loud;
            block.AddChild(note);
        }
        _rowBox.AddChild(block);
        _rows.Add(row);
    }

    private void UpdateReadouts()
    {
        foreach (var row in _rows)
        {
            var inst = row.Inst;
            if (row.Readout != null)
            {
                row.Readout.Text = Log.Format($"hp {inst.Health:0.##}/{inst.MaxHealth:0.##}  {inst.Status}  stage {inst.DamageStage}");
            }
            if (row.Slider != null)
            {
                _syncing = true;
                row.Slider.Value = inst.MaxHealth > 0f ? inst.Health / inst.MaxHealth * 100.0 : 0.0;
                _syncing = false;
            }
        }
        if (_status != null && _runtime != null && _rows.Count > 0)
        {
            _status.Text = Log.Format($"world: debris launched {_runtime.BallisticMotionsLaunched} · one-shot sounds {_runtime.OneShotSoundsPlayed}")
                           + (_collisionBuilt ? "" : "\ncolliders NOT BUILT IN THIS MODE, a kill's collider census would read zero here and lie");
        }
    }

    // ---- driving ----------------------------------------------------------------------------

    // Takes a pool to an absolute HP. Down spends the difference through the weapon-hit
    // path; up resets the object and re-damages to the new value, since the model has no healing
    // (the stage counter only climbs, and RESET_STATE is the authored way back).
    private void DriveTo(PoolRow row, float targetHp, string via)
    {
        if (_runtime == null || !row.Drivable)
        {
            return;
        }
        var inst = row.Inst;
        float target = Mathf.Clamp(targetHp, 0f, inst.MaxHealth);
        if (Mathf.Abs(target - inst.Health) < 1e-4f && inst.Status != DestructibleRegistry.State.Destroyed)
        {
            return;
        }
        AskForEffects();
        if (target > inst.Health || inst.Status == DestructibleRegistry.State.Destroyed)
        {
            _runtime.ResetDestructible(inst);
            Log.Info("ui", $"damagelab reset-then-damage pool={Label(inst)} target_hp={target:0.##} via={via}");
        }
        float before = inst.Health;
        int stageBefore = inst.DamageStage;
        float spend = inst.Health - target;
        var started = Watch();
        if (spend > 0f)
        {
            _runtime.DamageAt(inst.Anchor, spend);
        }
        string fired = Unwatch(started);
        Log.Info("ui", $"damagelab hp pool={Label(inst)} anchor={SelectionService.NameOf(inst.Anchor)} spent={spend:0.##} hp={before:0.##}→{inst.Health:0.##} stage={stageBefore}→{inst.DamageStage} state={inst.Status} started=[{fired}] path=DamageAt via={via}");
        if (inst.Status == DestructibleRegistry.State.Destroyed)
        {
            ReportDeath(inst, Probes.EnabledColliders(Probes.WorldRootOf(inst.Anchor)),
                _runtime.BallisticMotionsLaunched, _runtime.OneShotSoundsPlayed, "hp");
        }
        UpdateReadouts();
    }

    // Kills a pool outright through the weapon-hit path, one hit spending more than the
    // whole pool, and reports the immediate consequences: the healthy→destroyed swap and the
    // collider census. Both are read before any clock advance, because they are what the
    // death does synchronously; the debris is scheduled and belongs to Tick.
    private void Kill(PoolRow row, string via)
    {
        if (_runtime == null || !row.Drivable)
        {
            return;
        }
        var inst = row.Inst;
        AskForEffects();
        if (inst.Status == DestructibleRegistry.State.Destroyed)
        {
            Log.Info("ui", $"damagelab kill pool={Label(inst)} already destroyed, Reset it first (a dead pool takes no further damage) via={via}");
            return;
        }
        var colBefore = _collisionBuilt
            ? Probes.EnabledColliders(Probes.WorldRootOf(inst.Anchor))
            : new HashSet<CollisionShape3D>();
        int debrisBefore = _runtime.BallisticMotionsLaunched;
        int soundsBefore = _runtime.OneShotSoundsPlayed;
        float before = inst.Health;
        var started = Watch();
        _runtime.DamageAt(inst.Anchor, inst.MaxHealth + 1f);
        string fired = Unwatch(started);
        Log.Info("ui", $"damagelab kill pool={Label(inst)} anchor={SelectionService.NameOf(inst.Anchor)} hp={before:0.##}→{inst.Health:0.##} state={inst.Status} started=[{fired}] path=DamageAt via={via}");
        ReportDeath(inst, colBefore, debrisBefore, soundsBefore, via);
        UpdateReadouts();
    }

    // Everything a kill did that can be read at t=0. The collider counts are reported as two
    // numbers, never a difference: a death switches the object's healthy collision OFF and the
    // wreck's ON, and the signed sum can be positive while the removal really happened.
    private void ReportDeath(DestructibleRegistry.Instance inst, HashSet<CollisionShape3D> colBefore,
        int debrisBefore, int soundsBefore, string via)
    {
        if (_runtime == null)
        {
            return;
        }
        Probes.CountVariants(inst.Anchor, out int hVis, out int hAll, out int dVis, out int dAll);
        Log.Info("ui", $"damagelab kill swap healthy={hVis}/{hAll} shown destroyed={dVis}/{dAll} shown (immediate, pre-tick)");
        if (_collisionBuilt)
        {
            var colAfter = Probes.EnabledColliders(Probes.WorldRootOf(inst.Anchor));
            int off = colBefore.Count(cs => IsInstanceValid(cs) && !colAfter.Contains(cs));
            int on = colAfter.Count(cs => !colBefore.Contains(cs));
            Log.Info("ui", $"damagelab kill colliders off={off} on={on} (counted separately, the net hides a real removal)");
        }
        else
        {
            Log.Info("ui", $"damagelab kill colliders NOT BUILT IN THIS MODE, this world was built with no collision at all, so a census here would read zero and lie about what the death removed");
        }
        Log.Info("ui", $"damagelab kill debris={_runtime.BallisticMotionsLaunched - debrisBefore} sounds={_runtime.OneShotSoundsPlayed - soundsBefore} (immediate, pre-tick, the death's debris motion is SCHEDULED seconds in)");
    }

    // Returns a pool to healthy through the definition's own RESET_STATE.
    private void Reset(PoolRow row, string via)
    {
        if (_runtime == null || !row.Drivable)
        {
            return;
        }
        var inst = row.Inst;
        _runtime.ResetDestructible(inst);
        Probes.CountVariants(inst.Anchor, out int hVis, out int hAll, out int dVis, out int dAll);
        Log.Info("ui", $"damagelab reset pool={Label(inst)} hp={inst.Health:0.##}/{inst.MaxHealth:0.##} state={inst.Status} stage={inst.DamageStage} swap healthy={hVis}/{hAll} shown destroyed={dVis}/{dAll} shown via={via}");
        UpdateReadouts();
    }

    // Fast-forwards the world runtime past a scheduled effect and reports what launched
    // in that window. Interactively the world clock already runs, so this exists for scripted
    // runs, which do their whole script inside one frame and would otherwise read t=0 and conclude
    // the debris was never implemented.
    private void Tick(float seconds)
    {
        if (_runtime == null)
        {
            return;
        }
        int debrisBefore = _runtime.BallisticMotionsLaunched;
        int soundsBefore = _runtime.OneShotSoundsPlayed;
        float advanced = 0f;
        while (advanced < seconds)
        {
            float dt = Mathf.Min(TickSlice, seconds - advanced);
            _runtime.Advance(dt);
            advanced += dt;
        }
        Log.Info("ui", $"damagelab tick advance={advanced:0.##}s debris=+{_runtime.BallisticMotionsLaunched - debrisBefore} sounds=+{_runtime.OneShotSoundsPlayed - soundsBefore} (scheduled state, post-tick)");
        UpdateReadouts();
    }

    // Which definitions a damage action started, the stage effect a threshold crossing calls, and
    // the death's own sequences. Naming them is the difference between "the stage counter moved"
    // and "the black-smoke effect fired".
    private List<string> Watch()
    {
        var names = new List<string>();
        if (_runtime != null)
        {
            _watching = names;
            _runtime.OnInstanceStarted += OnInstanceStarted;
        }
        return names;
    }

    private string Unwatch(List<string> names)
    {
        if (_runtime != null)
        {
            _runtime.OnInstanceStarted -= OnInstanceStarted;
        }
        _watching = null;
        return names.Count == 0 ? "nothing" : string.Join(" ", names);
    }

    private void OnInstanceStarted(AnimDefinition def, Node3D? anchor)
    {
        _watching?.Add(def.AnimName is { Length: > 0 } anim ? anim : def.Name);
    }

    // Built on the first damage action, never at session start: an untouched freecam session pays
    // nothing for it, and a kill without it starts its stage definitions and renders nothing.
    private void AskForEffects()
    {
        if (_effectsAsked || EffectsSource == null)
        {
            return;
        }
        _effectsAsked = true;
        var effects = EffectsSource();
        Log.Info("ui", $"damagelab effects runtime={(effects != null ? "built" : "unavailable")}, damage-stage and death effects {(effects != null ? "render here" : "will start but draw nothing in this mode")}");
    }

    // ---- scripted script --------------------------------------------------------------------

    private void RunDebugScript()
    {
        var steps = (DebugSpec ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Log.Info("ui", $"damagelab debug script=[{string.Join(" ", steps)}] collision_built={_collisionBuilt}");
        foreach (string step in steps)
        {
            RunStep(step);
        }
    }

    private void RunStep(string step)
    {
        if (step.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            return;   // the panel is already open; the token exists to ask for that and nothing else
        }
        if (step.StartsWith("node=", StringComparison.OrdinalIgnoreCase))
        {
            string name = step["node=".Length..];
            if (SelectByName == null)
            {
                Log.Warn("ui", $"damagelab node='{name}', this session has no name index to select through");
                return;
            }
            SelectByName(name);
            _scriptRow = 0;
            LogPools();
            return;
        }
        if (step.StartsWith("pool=", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(step["pool=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int which))
        {
            _scriptRow = Mathf.Clamp(which - 1, 0, Math.Max(0, _rows.Count - 1));
            if (_rows.Count > _scriptRow)
            {
                var picked = _rows[_scriptRow];
                Log.Info("ui", $"damagelab pool={which} → {Label(picked.Inst)} drivable={picked.Drivable}");
            }
            else
            {
                Log.Info("ui", $"damagelab pool={which}, nothing is selected, so there is no pool to drive");
            }
            return;
        }
        if (Row() is not { } row)
        {
            Log.Warn("ui", $"damagelab step '{step}' skipped, no destructible pool is selected");
            return;
        }
        if (step.StartsWith("hp=", StringComparison.OrdinalIgnoreCase)
            && float.TryParse(step["hp=".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out float hp))
        {
            Refuse(row, "hp");
            DriveTo(row, hp, "script");
            return;
        }
        if (step.StartsWith("tick=", StringComparison.OrdinalIgnoreCase)
            && float.TryParse(step["tick=".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out float secs))
        {
            Tick(Mathf.Clamp(secs, 0f, 60f));
            return;
        }
        if (step.Equals("kill", StringComparison.OrdinalIgnoreCase))
        {
            Refuse(row, "kill");
            Kill(row, "script");
            return;
        }
        if (step.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            Refuse(row, "reset");
            Reset(row, "script");
            return;
        }
        Log.Warn("ui", $"damagelab step '{step}' is not node=/pool=/hp=/kill/reset/tick=/open, ignoring it");
    }

    // A step aimed at a pool nothing can hit is reported rather than silently doing nothing: that
    // refusal IS the answer to "which pool is this panel driving".
    private void Refuse(PoolRow row, string action)
    {
        if (!row.Drivable)
        {
            Log.Warn("ui", $"damagelab {action} refused on pool={Label(row.Inst)} ({Source(row.Inst)}), a hit on this object resolves elsewhere, so this pool is unreachable by damage and has no controls");
        }
    }

    private PoolRow? Row() => _rows.Count > _scriptRow ? _rows[_scriptRow] : null;

    private void LogPools()
    {
        if (_rows.Count == 0)
        {
            Log.Info("ui", $"damagelab pools node={(_selection.Current is { } n ? SelectionService.NameOf(n) : "none")} pools=0, not a destructible");
            return;
        }
        Log.Info("ui", $"damagelab pools node={SelectionService.NameOf(_selection.Current!)} pools={_rows.Count}");
        for (int i = 0; i < _rows.Count; i++)
        {
            var inst = _rows[i].Inst;
            Log.Info("ui", $"damagelab pool={i + 1}/{_rows.Count} def={Label(inst)} source={Source(inst)} anchor={SelectionService.NameOf(inst.Anchor)} hp={inst.Health:0.##}/{inst.MaxHealth:0.##} state={inst.Status} stage={inst.DamageStage} drivable={_rows[i].Drivable}");
        }
    }

    private sealed class PoolRow
    {
        public DestructibleRegistry.Instance Inst = null!;
        public bool Drivable;
        public HSlider? Slider;
        public Label? Readout;
    }
}
