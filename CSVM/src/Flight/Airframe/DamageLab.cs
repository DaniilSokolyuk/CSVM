using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Hud;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Airframe;

/// <summary>
/// What the lab's sliders actually drive. The parked viewer has no HP model, its sliders
/// ARE the damage state, feeding <see cref="DamageVisuals"/> directly. In flight the aircraft's
/// own <see cref="PlaneDamage"/> is the state, so the sliders write it and read back from it.
/// </summary>
public interface IDamageLabTarget
{
    /// <summary>Suffix naming which aircraft the panel is bound to ("P1"), or null when there
    /// is only one thing it could mean.</summary>
    string? Subtitle { get; }

    /// <summary>The model's own pools for a part, which the sliders mirror, null from a
    /// target that holds no state of its own (then the sliders are the only truth).</summary>
    PartFrac? Fraction(string part);

    /// <summary>Writes the slider state through. <paramref name="rebuildVisuals"/> is set only
    /// when the crossed-threshold set changed, so a mid-band drag doesn't restart the fires.</summary>
    void Apply(IReadOnlyDictionary<string, PartFrac> fractions, bool rebuildVisuals);

    /// <summary>Per-frame work the target owns.</summary>
    void Tick(float dt);
}

/// <summary>One part's dialled-in state: the two pools' own fractions (armor 1f for a part the
/// data gives no armor pool) plus the combined armor+HP fraction the injure_anims thresholds and
/// the gauge dial key off, <see cref="DamageLab"/> derives <see cref="Combined"/> from the part's
/// (MaxHp, MaxArmor) once per read, so every consumer of "how hurt is this zone as one number"
/// agrees.</summary>
public readonly record struct PartFrac(float Health, float Armor, float Combined);

/// <summary>The parked plane (--viewer): the sliders are the state and the visuals are the whole
/// effect. The trails burn in place at a virtual speed because a parked plane travels no distance
/// (see DamageVisuals.UpdateStatic), on sim time, so a halted clock freezes them for a still capture.
/// </summary>
public sealed class ViewerDamageTarget : IDamageLabTarget
{
    private readonly DamageVisuals _visuals;

    public ViewerDamageTarget(DamageVisuals visuals)
    {
        _visuals = visuals;
    }

    public string? Subtitle => null;

    public PartFrac? Fraction(string part) => null;

    public void Apply(IReadOnlyDictionary<string, PartFrac> fractions, bool rebuildVisuals)
    {
        if (!rebuildVisuals)
            return;
        _visuals.Reset();
        var health = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, f) in fractions)
        {
            health[name] = f.Health;
            _visuals.OnPartDamage(name, f.Health);
        }

        // no ledger behind the parked viewer, so the hull pool is summed from the sliders
        _visuals.OnHullDamage(_visuals.HullHealthFractionFrom(health));
    }

    public void Tick(float dt) => _visuals.UpdateStatic(dt);
}

/// <summary>The flown aircraft (--fly/--stunt): the sliders write the real per-part armor and HP,
/// so the HUD's DMG line, the damaged-engine mix and the gauge dial all follow, and a critical
/// part's HP at 0 leaves the plane one hit from down, exactly as a graze would. Each pool is set
/// through a dedicated single-pool <see cref="PlaneDamage.Apply(string,float,float)"/> call
/// (armor's with healthDamage=0, health's with armorDamage=0) after a
/// <see cref="PlaneDamage.Reset"/>, using fractions the lab already floored, armor can be
/// driven to 0 with health untouched, but not the reverse.
/// Nothing to tick: FlightController drives the visuals with the plane's live pose every frame,
/// and UpdateStatic would fight it.</summary>
public sealed class FlightDamageTarget : IDamageLabTarget
{
    private readonly FlightController _controller;

    public FlightDamageTarget(FlightController controller, string? subtitle)
    {
        _controller = controller;
        Subtitle = subtitle;
    }

    public string? Subtitle { get; }

    public PartFrac? Fraction(string part) =>
        _controller.Damage is { } damage && damage.Parts.TryGetValue(part, out var state)
            ? new PartFrac(state.HealthFraction, state.ArmorFraction, state.Fraction)
            : null;

    public void Apply(IReadOnlyDictionary<string, PartFrac> fractions, bool rebuildVisuals)
    {
        if (_controller.Damage is not { } damage)
            return;
        damage.Reset();
        var applied = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, f) in fractions)
            if (damage.Parts.TryGetValue(name, out var state))
            {
                if (state.Def.MaxArmor > 0f)
                    damage.Apply(name, 0f, (1f - f.Armor) * state.Def.MaxArmor);
                var after = damage.Apply(name, (1f - f.Health) * state.Def.MaxHp, 0f);
                applied[name] = after?.HealthFraction ?? f.Health;
            }
        if (rebuildVisuals && _controller.Visuals is { } visuals)
        {
            visuals.Reset();
            foreach (var (name, health) in applied)
                visuals.OnPartDamage(name, health);
            visuals.OnHullDamage(damage.SummaryHealthFraction);
        }
    }

    public void Tick(float dt)
    {
    }
}

/// <summary>The damage lab (F19 toggles): one armor slider plus one health slider per destroyable
/// part, driving <see cref="DamageVisuals"/> through an <see cref="IDamageLabTarget"/> off their
/// HEALTH fraction (<see cref="PartFrac.Combined"/> feeds the gauge dial, not the visuals stages).
/// Two hosts, one panel: <c>--viewer</c>'s sliders ARE the state on a parked plane,
/// <c>--fly</c>/<c>--stunt</c> write P1's real <c>PlaneDamage</c> while the sim runs and mirror it
/// back. <see cref="ReadSliders"/> floors a part's armor at 0 whenever its health reads below
/// full, mirroring the real armor-first damage path.</summary>
public sealed partial class DamageLab : Node
{
    private readonly PlaneStats _stats;
    private readonly IDamageLabTarget _target;
    private readonly IReadOnlyList<(string Part, float Frac)> _preset;
    private readonly GaugeCluster? _gauges;

    private readonly Dictionary<string, HSlider> _healthSliders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HSlider> _armorSliders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Label> _readouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _lastFractions = new(StringComparer.OrdinalIgnoreCase); // combined, for the blink-on-decrease check
    private CanvasLayer _ui = null!;
    private CanvasLayer? _gaugeLayer;
    private HashSet<string> _applied = new(StringComparer.OrdinalIgnoreCase);
    private bool _gaugesWanted = true; // the panel's HUD-gauges checkbox, remembered across F19
    private int _dragging; // sliders under the mouse right now, the read-back leaves those alone

    public DamageLab(PlaneStats stats, IDamageLabTarget target,
        IReadOnlyList<(string Part, float Frac)>? preset = null, GaugeCluster? gauges = null)
    {
        _stats = stats;
        _target = target;
        _preset = preset ?? Array.Empty<(string, float)>();
        _gauges = gauges;
        Name = "damage_lab";
    }

    /// <summary>Build the lab but keep it out of sight until F19. Set for a plain launch
    /// (no --damage), so the lab is always THERE to toggle while an unadorned viewer or
    /// flight screenshot stays byte-identical to one with no lab at all.</summary>
    public bool StartHidden { get; init; }

    /// <summary>Hang the panel off the right edge instead of the left. Set in flight, where the
    /// top-left corner is the HUD's own text block, SPD/ALT/THR plus the stall, impact, DMG,
    /// paused and crashed lines, which grow to about six and would print straight through the
    /// sliders. The right edge is clear above the dials.</summary>
    public bool RightAligned { get; init; }

    public override void _Ready()
    {
        // The HUD gauge cluster (user request): the damage dial mirrors the sliders,
        // colors from the fractions, the 5 s post-hit blink from slider decreases.
        // Altimeter/speedometer draw at rest (no flight data). Toggled from the panel.
        if (_gauges != null)
        {
            _gauges.PartFraction = name => CombinedFractionOf(name);
            _gaugeLayer = new CanvasLayer { Layer = 0 };
            _gaugeLayer.AddChild(_gauges);
            AddChild(_gaugeLayer);
        }
        BuildUi();
        foreach (var (part, frac) in _preset)
        {
            if (!_healthSliders.TryGetValue(part, out var health))
            {
                Log.Info("flight", $"damage lab: --damage names unknown part '{part}'");
                continue;
            }
            health.Value = frac * 100.0; // fires ValueChanged → Reapply
            if (_armorSliders.TryGetValue(part, out var armor))
                armor.Value = frac * 100.0;
        }
        // presets are an initial state, not fresh hits, cancel the blink their
        // slider moves triggered so --screenshot damage shots stay deterministic
        _gauges?.Reset();
        Reapply(); // sync the readouts even when no preset moved a slider
        if (StartHidden)
            SetLabVisible(false);
    }

    /// <summary>Mirrors whatever the target's own model says before letting it tick, on sim time,
    /// so a halted clock freezes the parked plane's fires for a still capture.</summary>
    public override void _Process(double delta)
    {
        SyncFromTarget();
        _target.Tick(GameClock.Current?.FrameDt ?? (float)delta);
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F19 })
        {
            SetLabVisible(!_ui.Visible);
            GetViewport().SetInputAsHandled();
        }
    }

    // Threshold-line shorthand: pdpanel4 → p4 (the wired torn-skin flip),
    // leftwing_damage_yellow → yellow (the cockpit-indicator cycle).
    private static string ShortAnim(string part, string anim)
    {
        if (anim.StartsWith("pdpanel", StringComparison.OrdinalIgnoreCase))
            return "p" + anim["pdpanel".Length..];
        var prefix = part + "_damage_";
        return anim.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? anim[prefix.Length..] : anim;
    }

    private static Label Small(string text)
    {
        var label = new Label { Text = text, Modulate = new Color(1, 1, 1, 0.65f) };
        label.AddThemeFontSizeOverride("font_size", 11);
        return label;
    }

    // Moves one slider to match the model's own value, no-op (and reports no movement)
    // when it is already there, the tolerance keeps a settled read-back from chattering.
    private static bool SyncSlider(HSlider slider, float frac)
    {
        double want = Math.Round(frac * 100.0);
        if (Math.Abs(slider.Value - want) < 0.5)
            return false;
        slider.SetValueNoSignal(want); // no Reapply, this IS the model's state
        return true;
    }

    // Pulls the target's own damage state back into the sliders, in flight that is
    // every hit taken while the panel is up, and the respawn that repairs them. Sliders under
    // the mouse are left alone: a drag that fights the read-back is unusable. The visuals are
    // NOT re-derived here; whoever moved the model (FlightController's hit path) already did.
    private void SyncFromTarget()
    {
        if (_dragging > 0)
            return;
        bool moved = false;
        foreach (var (name, slider) in _healthSliders)
        {
            if (_target.Fraction(name) is not { } f)
                continue;
            moved |= SyncSlider(slider, f.Health);
            if (_armorSliders.TryGetValue(name, out var armorSlider))
                moved |= SyncSlider(armorSlider, f.Armor);
        }
        if (!moved)
            return;
        var fractions = ReadSliders();
        UpdateReadouts(fractions);
        foreach (var (name, f) in fractions)
            _lastFractions[name] = f.Combined; // a live hit already blinked the dial; don't blink twice
        _applied = TargetAnims(fractions);
    }

    // Shows or hides the whole lab, slider panel and HUD gauges together. H is
    // "is the damage lab here", not "is one of its two layers here"; the panel's own
    // checkbox still controls the gauges independently while the lab is up, and its state
    // is remembered across a hide/show.
    private void SetLabVisible(bool on)
    {
        _ui.Visible = on;
        if (_gaugeLayer != null)
            _gaugeLayer.Visible = on && _gaugesWanted;
    }

    private void BuildUi()
    {
        _ui = new CanvasLayer { Layer = 1 };
        var panel = new PanelContainer
        {
            Position = new Vector2(8, 8),
            SelfModulate = new Color(1, 1, 1, 0.85f),
        };
        if (RightAligned)
        {
            // Anchored rather than positioned: the panel's width is content-derived (the part
            // names and threshold lines differ per plane), so only the right edge is knowable here.
            panel.AnchorLeft = panel.AnchorRight = 1f;
            panel.GrowHorizontal = Control.GrowDirection.Begin;
            panel.OffsetRight = -8;
            panel.OffsetTop = 8;
        }
        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 10);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);

        box.AddChild(new Label
        {
            Text = $"DAMAGE LAB: {_stats.DefName}" +
                   (_target.Subtitle is { } who ? $"  ·  {who}" : ""),
        });
        box.AddChild(Small("drag a part's armor/health over its thresholds · F19 hides this panel"));

        foreach (var part in _stats.DestroyableParts)
        {
            var readout = new Label();
            _readouts[part.Name] = readout;
            box.AddChild(readout);

            if (part.MaxArmor > 0f)
            {
                box.AddChild(Small("armor"));
                _armorSliders[part.Name] = AddPartSlider(box, part.Name);
            }
            box.AddChild(Small("health"));
            _healthSliders[part.Name] = AddPartSlider(box, part.Name);

            // The part's thresholds, high→low: pN = the wired pdpanelN torn-skin flip,
            // green/yellow/red = the unwired cockpit-indicator cycle (listed for context).
            if (part.InjureAnims.Count > 0)
                box.AddChild(Small(string.Join(" · ",
                    part.InjureAnims.Select(e => $"{e.Frac * 100:0} {ShortAnim(part.Name, e.Anim)}"))));
        }
        if (_stats.VehicleInjureAnims.Count > 0)
            box.AddChild(Small("any part: " + string.Join(" · ",
                _stats.VehicleInjureAnims.Select(e => $"{e.Frac * 100:0} {e.Anim}"))));

        var repair = new Button { Text = "repair all" };
        repair.Pressed += () =>
        {
            foreach (var slider in _healthSliders.Values.Concat(_armorSliders.Values))
                slider.Value = 100;
        };
        box.AddChild(repair);

        if (_gaugeLayer != null)
        {
            var hud = new CheckButton { Text = "HUD gauges", ButtonPressed = true };
            hud.Toggled += on => { _gaugesWanted = on; _gaugeLayer.Visible = on && _ui.Visible; };
            box.AddChild(hud);
        }

        margin.AddChild(box);
        panel.AddChild(margin);
        _ui.AddChild(panel);
        AddChild(_ui);
        // This lab has a flight host (F19 while flying), where Space is the trigger and a focused
        // "repair all" would swallow it, the same defect the weapon lab's panel had.
        UI.Boards.PanelFocus.Strip(_ui, "damage lab: panel built");
    }

    // One slider, the shape shared by a part's armor and health controls.
    private HSlider AddPartSlider(VBoxContainer box, string partName)
    {
        var slider = new HSlider
        {
            MinValue = 0,
            MaxValue = 100,
            Step = 1,
            Value = 100,
            TickCount = 11,
            TicksOnBorders = true,
            CustomMinimumSize = new Vector2(250, 0),
        };
        slider.ValueChanged += _ => Reapply();
        slider.DragStarted += () => _dragging++;
        slider.DragEnded += _ => _dragging--;
        box.AddChild(slider);
        return slider;
    }

    // The combined armor+HP fraction the gauge dial key off (used before the panel
    // exists, e.g. GaugeCluster's own PartFraction callback assigned in _Ready, safe because
    // it only ever runs once BuildUi has populated the sliders).
    private float CombinedFractionOf(string partName) =>
        _healthSliders.ContainsKey(partName) ? ReadSliders()[partName].Combined : 1f;

    private Dictionary<string, PartFrac> ReadSliders()
    {
        var fractions = new Dictionary<string, PartFrac>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in _stats.DestroyableParts)
        {
            float healthFrac = (float)(_healthSliders[part.Name].Value / 100.0);
            // Mirrors PlaneDamage.Apply's armor-first path: health never reads below full while
            // armor still stands.
            float armorFrac = 1f;
            if (_armorSliders.TryGetValue(part.Name, out var a))
                armorFrac = healthFrac < 1f ? 0f : (float)(a.Value / 100.0);
            float combined = part.MaxHp + part.MaxArmor > 0f
                ? (healthFrac * part.MaxHp + armorFrac * part.MaxArmor) / (part.MaxHp + part.MaxArmor)
                : 0f;
            fractions[part.Name] = new PartFrac(healthFrac, armorFrac, combined);
        }
        return fractions;
    }

    private void UpdateReadouts(Dictionary<string, PartFrac> fractions)
    {
        foreach (var part in _stats.DestroyableParts)
            if (_readouts.TryGetValue(part.Name, out var readout))
            {
                var f = fractions[part.Name];
                string text = part.Name + "  ";
                if (part.MaxArmor > 0f)
                    text += $"a{f.Armor * 100:0}% ({f.Armor * part.MaxArmor:0.#}/{part.MaxArmor:0})  ";
                text += $"h{f.Health * 100:0}% ({f.Health * part.MaxHp:0.#}/{part.MaxHp:0})";
                readout.Text = text;
            }
    }

    // Writes the sliders through to the target. The visual rebuild (Reset +
    // re-crossing every threshold) only runs when the set of crossed anims actually
    // changed, mid-band drags just move a pool and update the readouts.
    private void Reapply()
    {
        var fractions = ReadSliders();

        // a part's combined fraction going DOWN = it "took damage", start its gauge blink,
        // exactly like a flight hit (repairs don't blink)
        foreach (var (name, f) in fractions)
        {
            if (_lastFractions.TryGetValue(name, out float prev) && f.Combined < prev)
                _gauges?.OnPartDamage(name);
            _lastFractions[name] = f.Combined;
        }

        // ReadSliders floors armor at 0 whenever health reads below full, pull the armor
        // widget's own position down to match, so a slider raised while health is damaged
        // snaps back instead of showing a value the fractions dict no longer honours.
        foreach (var (name, f) in fractions)
            if (_armorSliders.TryGetValue(name, out var armorSlider))
                SyncSlider(armorSlider, f.Armor);

        UpdateReadouts(fractions);

        var target = TargetAnims(fractions);
        bool rebuild = !target.SetEquals(_applied);
        _applied = target;
        _target.Apply(fractions, rebuild);
    }

    // The anim set the current fractions demand, used only to decide whether a drag needs a
    // visual rebuild: an entry applies when the COMBINED fraction ≤ its threshold (the lab's own
    // one-number approximation), where DamageVisuals itself crosses on health alone, a
    // combined-only change can flag a rebuild the health-gated apply then no-ops.
    private HashSet<string> TargetAnims(Dictionary<string, PartFrac> fractions)
    {
        var target = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in _stats.DestroyableParts)
            if (fractions.TryGetValue(part.Name, out var f))
                foreach (var (thresh, anim) in part.InjureAnims)
                    if (f.Combined <= thresh)
                        target.Add(anim);
        if (fractions.Count > 0)
        {
            float worst = fractions.Values.Min(f => f.Combined);
            foreach (var (thresh, anim) in _stats.VehicleInjureAnims)
                if (worst <= thresh)
                    target.Add(anim);
        }
        return target;
    }
}
