using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.Flight.Airframe;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.UI.Boards;
using Godot;

namespace CSVM.Flight.Hud;

/// <summary>One rendered frame of everything the pilot HUD draws from. Passed by <c>in</c> and
/// never as a class: this feed runs once per rendered frame per aircraft, so it must allocate
/// nothing. The fields are the aircraft's STATE, not the readouts' values. Composing "stalling",
/// "crashed" and the rest into text and dial positions is <see cref="FlightHud"/>'s own job, which
/// is what lets that mapping be asserted with no Godot control in the process.</summary>
public struct FlightHudState
{
    /// <summary>The drawn world position: the marker HUDs' own pose feed, and the altimeter's
    /// source through <c>Y</c>.</summary>
    public Vector3 Position;

    /// <summary>Nose heading, 0 = north, 90 = east, the compass tape and both marker HUDs.</summary>
    public float HeadingDeg;

    /// <summary>Airspeed in m/s; the speedometer and the text line convert to mph.</summary>
    public float SpeedMps;

    /// <summary>Throttle 0..1, as the text line's percentage.</summary>
    public float Throttle;

    /// <summary>Frozen at the impact point waiting for a respawn.</summary>
    public bool Crashed;

    /// <summary>The airframe is pinned by the weapon lab.</summary>
    public bool Held;

    /// <summary>A board is up and the whole simulation is halted.</summary>
    public bool Halted;

    /// <summary>The flight model's own stall warning, before the crashed/halted/held gates.</summary>
    public bool StallWarned;

    /// <summary>How far into the stall the airframe is, for the warning lamp's ramp: the wing's
    /// available lift as a multiple of weight (FlightModel.AvailableLoadFactor).</summary>
    public float AvailableLoadFactor;

    /// <summary>The flight model is actually stalled, not merely warning.</summary>
    public bool Stalled;

    /// <summary>Whether the approach table's <c>auto</c> row currently passes for this aircraft
    /// (<c>LandingApproachRuntime.AutoLandOffered</c>), so the auto-land button
    /// does something right now.</summary>
    public bool AutoLandOffered;

    /// <summary>Whether the respawn button would actually bring this pilot back right now: false
    /// for a pilot out of lives and for a seat whose controls the session is holding, both of which
    /// swallow the press. A prompt naming a control that does nothing is the same defect as a
    /// prompt naming the wrong control.</summary>
    public bool RespawnOffered;

    /// <summary>Wall seconds this frame: the damage flash counts down on wall time, so a halted
    /// session does not burn the flash off while nothing is drawn.</summary>
    public float WallDt;

    /// <summary>Sim seconds this frame, which the pipper's range smoother runs on so a scripted
    /// capture stays frame-rate independent.</summary>
    public float SimDt;

    /// <summary>The damage ledger's summary line, or null/empty for no DMG line.</summary>
    public string? DamageSummary;

    /// <summary>The stunt run's status line, supplied only when there is no run HUD to carry
    /// it (see <see cref="FlightHud.NeedsStuntStatusLine"/>); null otherwise.</summary>
    public string? StuntStatusLine;

    /// <summary>The live loadout the two weapon gauges read their ammo from; null draws neither.</summary>
    public Loadout? Loadout;

    /// <summary>Fire control's gun-group cursor, as the gun gauge's selected slot.</summary>
    public int GunSelect;

    /// <summary>Fire control's pylon cursor, as the missile gauge's selected slot.</summary>
    public int PylonSelect;

    /// <summary>The nitro gauge's feed: shown only with the injector installed, the boost needle
    /// on the flag, the charge needle on the tank fraction.</summary>
    public bool NitroInstalled;
    public bool NitroBoosting;
    public float NitroChargeFrac;

    /// <summary>The gun group the pipper marks, or null to hide it, the caller resolves this,
    /// since reaching a muzzle's world pose costs engine interop no other rig should pay.</summary>
    public GunGroup? ReticleGun;

    /// <summary>That group's muzzle midpoint, where its fire converges.</summary>
    public Vector3 ReticleOrigin;

    /// <summary>The aircraft's nose axis, which the pipper marks rather than any assisted line.</summary>
    public Vector3 ReticleNose;

    /// <summary>The aircraft's own velocity, which a fired round inherits.</summary>
    public Vector3 InheritedVelocity;

    /// <summary>The aircraft's own orientation (<c>FlightModel.Attitude</c>), never the camera's:
    /// the artificial horizon reads this, not <c>PlaneBuilder.HeadPitchOffsetRad</c> or any
    /// look-around the pilot has applied.</summary>
    public Basis Attitude;

    /// <summary>The pose the aircraft is DRAWN at this frame, interpolated between sim steps, which
    /// the chase camera and the aircraft's own node both sit on. The spyglass eye stands here;
    /// everything the gauges read stays on the sim pair above.</summary>
    public Transform3D RenderPose;
}

/// <summary>Everything one pane draws for its pilot: the heading tape, the cockpit dials and their
/// two weapon gauges, the gun pipper, the stunt and targeting marker HUDs, the font-test overlay,
/// the message stack, the auto-dock prompt and the flight text block. Fed one
/// <see cref="FlightHudState"/> per rendered frame; nothing
/// outside this class writes any of those readouts. Constraints and the draw order: this module's
/// entry in docs/architecture.md.</summary>
public sealed class FlightHud
{
    /// <summary>The original's heading tape at the top of the screen. Null if the chapter's
    /// texture archive lacks the compass textures.</summary>
    public CompassTape? Compass;

    /// <summary>The original's cockpit dials (altimeter / speedometer / damage display) rebuilt
    /// from the plane's own gauges subtree. Optional.</summary>
    public GaugeCluster? Gauges;

    /// <summary>The <c>--hud-font-test</c> bitmap-font verification overlay, drawn over everything
    /// so it scales with the pane. Null unless the flag is set.</summary>
    public HudFontTest? FontTest;

    /// <summary>The gun aiming reticle: the game's pipper at the selected group's ballistic impact
    /// point. Null when the plane carries no firable gun, or the reticle texture was absent.</summary>
    public ImpactReticle? Reticle;

    /// <summary>The stunt run HUD: run status, intro and completion banners, zone-cleared flash.
    /// Null in free flight. The zone MARKER is the targeting HUD's, since a zone is an objective.
    /// </summary>
    public StuntRunHud? StuntRun;

    /// <summary>The targeting HUD: the pilot's sticky selection, the tracked-hostile fallback and
    /// <c>--debug-markers</c>. Built for every human pane in every flight session.</summary>
    public TargetHud? TargetHud;

    /// <summary>The original's HUD message stack, where a kill line lands. Built and parented by
    /// <see cref="Attach"/>, so every human pane has one; the session posts into it.</summary>
    public HudMessages? MessageStack;

    /// <summary>The original's auto-dock prompt, the centred line of its own the offer stands on.
    /// Built and parented by <see cref="Attach"/>, so every human pane has one; this feeds it the
    /// wording each frame.</summary>
    public PromptLine? AutoDock;

    /// <summary>The respawn prompt a crashed pilot reads, on the same centred footing.
    /// ⚠ Parented to the MESSAGE layer by <see cref="Attach"/>, never the HUD layer: the crash
    /// camera hides the HUD outright, and this is the one prompt whose whole audience is sitting in
    /// that hidden state.</summary>
    public PromptLine? CrashPrompt;

    /// <summary>The auto-land line this pane draws while the approach table's <c>auto</c> row
    /// passes, from <see cref="ComposeAutoLandPrompt"/> over the seat's own bindings, recomposed
    /// whenever the seat's active device moves. Empty draws no line, which is what an unbound
    /// auto-land reads as. <see cref="AutoDock"/> is where it lands, never the text block.</summary>
    public ControlLine AutoLandPrompt = ControlLine.Plain(AutoLandFallback);

    /// <summary>The respawn line this pane draws while its pilot sits at the crash, from
    /// <see cref="ComposeRespawnPrompt"/> over the seat's own bindings, recomposed on the same
    /// handover. Empty until a seat composes it, and for a seat whose active device has no
    /// <c>Respawn</c> binding at all. <see cref="CrashPrompt"/> is where it lands.</summary>
    public ControlLine RespawnPrompt = ControlLine.Empty;

    // The pipper's placement and smoothing are decoded (docs/org/aim-assist.md "What the pipper
    // follows"); no TUNE left in them.
    private const float ReticleFlightTime = 0.5f;      // s of flight the pipper marks
    private const float ReticleDefaultSpeed = 860f;    // m/s used when no weapon def resolves
    private const float ReticleAccel = 894.07996f;     // m/s² the smoother's rate builds at
    private const float ReticleRatePerGap = 1.9848576f; // rate ceiling per metre of remaining gap
    private const float ReticleFarGap = 900f;          // m past which the ceiling is flat
    private const float ReticleFarRate = 1788.1599f;   // m/s that flat ceiling
    // What the prompt reads when messages.json is absent, so a data-less rig still says the offer
    // is live. Plain English, not the shipped wording, which only the message table carries.
    private const string AutoLandFallback = "AUTO-LAND AVAILABLE";
    private const string AutoLandFallbackTemplate = AutoLandFallback + " - %1";
    private const string AutoLandPressKey = "MSG_PRESS_AUTOLAND";  // "Press %1 to autodock"
    private const string AutoLandClickKey = "MSG_CLICK_AUTOLAND";  // the mouse-button wording
    // The respawn prompt's own wording. Respawn is this port's action, so the install's table
    // carries no row for it and no language but ours; the shape is the table's all the same, a %1
    // the control name fills today and a control glyph can fill instead.
    private const string RespawnPressTemplate = "Press %1 to respawn";
    private const string RespawnClickTemplate = "Click %1 to respawn";
    private const float DamageFlashTime = 2.5f;        // s the text block shows the impact line
    private const int TextFontSize = 22;               // text block, full-screen (shrunk per pane)
    private const float AglRayLength = 1000f;          // m the altimeter's down ray reaches

    private static readonly Vector2 TextMargin = new(16, 10);

    private readonly List<float> _gunGaugeSlots = new();
    private readonly List<float> _missileGaugeSlots = new();
    private readonly List<string> _textLines = new(); // reused across frames; ComposeTextLines' return

    private Label? _text;                        // the flight text block; never built on an AI rig
    private bool _shown = true;                  // SetVisible: off in a cutscene and in photo mode
    private bool _instrumentsShown = true;       // SetInstrumentsVisible: off under --debug-spectate
    private bool _cockpitView;                   // the cockpit interior is on the screen this frame
    private float _paneFactor = 1f;              // last applied splitscreen shrink (1 = single player)
    private float _textLeft = -1f;               // last applied reading-box left edge (0 at 16:9)
    private float _damageFlash;                  // s left on the impact line
    private string _damageFlashText = "";
    private float _reticleDist = ReticleDefaultSpeed * ReticleFlightTime; // m, the pipper's smoothed range
    private float _reticleRate;                  // m/s the pipper's range is currently closing at

    // The gungauge / missilegauge state, pushed to GaugeCluster each frame. Persistent objects
    // mutated in place (the belt-fraction lists too) so the readout costs no per-frame allocation.
    // Null until Bind runs, and only for a system the plane actually carries.
    private GaugeCluster.WeaponGauge? _gunGauge;
    private GaugeCluster.WeaponGauge? _missileGauge;

    /// <summary>Whether the caller should compute a pipper feed at all. Resolving the selected
    /// group's muzzle midpoint reads a world transform per barrel, which an aircraft drawing no
    /// reticle must not pay every rendered frame.</summary>
    public bool DrawsReticle => Reticle != null;

    /// <summary>Whether there is a text block to fill at all. The lines it composes cost real work
    /// to gather (the damage ledger's summary walks and joins every hurt zone), so an aircraft with
    /// no text block must not be asked for them.</summary>
    public bool DrawsTextBlock => _text != null;

    /// <summary>Whether the stunt run's status line has to come through
    /// <see cref="FlightHudState.StuntStatusLine"/>: only where there is a text block to hold it
    /// and no run HUD, which normally carries it instead.</summary>
    public bool NeedsStuntStatusLine => _text != null && StuntRun == null;

    /// <summary>Height over ground from the last <see cref="StepAgl"/>, or
    /// <see cref="float.MaxValue"/> where the ray reached nothing (or there are no dials to hold
    /// it). The flight telemetry line reads it back out.</summary>
    public float AglMeters => Gauges?.AglMeters ?? float.MaxValue;

    /// <summary>The altimeter's LOW ALT ray, decoupled from <see cref="Gauges"/>: height above
    /// whatever it hits, or <see cref="float.MaxValue"/> off a clear ray. Static so CSVM.Tests
    /// (FlightHudMappingTests) can drive it off a synthetic <see cref="IWorldQuery"/> with no
    /// dial Control in the process.</summary>
    public static float ComputeAgl(IWorldQuery world, Vector3 position, Godot.Collections.Array<Rid>? exclude) =>
        world.Ray(position, position + Vector3.Down * AglRayLength, CollisionLayers.WorldAndAircraft,
            exclude, out var report)
            ? position.Y - report.Position.Y
            : float.MaxValue;

    /// <summary>Whether the STALL lamp should light: the flight model's own warning, gated off
    /// while the plane is crashed, halted or pinned by the weapon lab (a held plane sits at 0 m/s,
    /// below every stall speed, but it is pinned, not stalling). Static so CSVM.Tests
    /// (FlightHudMappingTests) can assert the gate directly.</summary>
    public static bool ComputeStallWarning(in FlightHudState state) =>
        !state.Crashed && !state.Halted && !state.Held && state.StallWarned;

    /// <summary>Whether the auto-dock prompt should stand this frame: the approach table's offer,
    /// gated off while the plane is crashed, halted or pinned by the weapon lab, none of which can
    /// answer the offer. Whether there is WORDING to draw is <see cref="AutoLandPrompt"/>'s. Static
    /// so CSVM.Tests can assert the gate directly.</summary>
    public static bool ShowsAutoLandPrompt(in FlightHudState state) =>
        !state.Held && !state.Crashed && !state.Halted && state.AutoLandOffered;

    /// <summary>Whether the respawn prompt should stand this frame: a crashed pilot the button can
    /// still bring back, with no board up over the wreck. The two prompts are exclusive by these
    /// gates, which is why they share a footing. Whether there is WORDING to draw is
    /// <see cref="RespawnPrompt"/>'s. Static so CSVM.Tests can assert the gate directly.</summary>
    public static bool ShowsRespawnPrompt(in FlightHudState state) =>
        state.Crashed && !state.Halted && state.RespawnOffered;

    /// <summary>The speedometer's mph conversion. Static so CSVM.Tests can assert it as a number
    /// rather than through a formatted string.</summary>
    public static float MphFromSpeedMps(float speedMps) => speedMps * 2.23694f;

    /// <summary>The altimeter's feet conversion off the drawn world Y. Static so CSVM.Tests can
    /// assert it as a number rather than through a formatted string.</summary>
    public static float FeetFromWorldY(float y) => y * 3.28084f;

    /// <summary>The auto-land prompt for a seat holding <paramref name="bindings"/>: the message
    /// table's own wording with the control of the seat's active <paramref name="side"/> in the
    /// <c>%1</c> slot, "Click" for a mouse button and "Press" otherwise, and empty when nothing is
    /// bound (docs/formats/anim-definitions/cutscenes.md). Which binding a side names, and the
    /// fallback to the other side, belong to <see cref="ActiveDevice.PromptBinding"/>; what draws in
    /// the slot, words or a glyph, belongs to <see cref="ControlLine"/>.</summary>
    public static ControlLine ComposeAutoLandPrompt(Messages? strings, IReadOnlyList<Binding> bindings,
        bool readsKeyboard, DeviceSide side)
    {
        if (ActiveDevice.PromptBinding(bindings, side, readsKeyboard) is not { } binding)
        {
            return ControlLine.Empty;
        }

        string key = binding.Control.Kind == ControlKind.Mouse ? AutoLandClickKey : AutoLandPressKey;
        string template = strings?.Get(key) ?? key;
        return ControlLine.Compose(template == key ? AutoLandFallbackTemplate : template, binding);
    }

    /// <summary>The respawn prompt for a seat holding <paramref name="bindings"/>: the port's own
    /// wording with the control of the seat's active <paramref name="side"/> in the <c>%1</c> slot,
    /// "Click" for a mouse button and "Press" otherwise, and empty when nothing that side can reach
    /// is bound. Which binding a side names, and the fallback to the other side, belong to
    /// <see cref="ActiveDevice.PromptBinding"/>.</summary>
    public static ControlLine ComposeRespawnPrompt(IReadOnlyList<Binding> bindings, bool readsKeyboard,
        DeviceSide side)
    {
        if (ActiveDevice.PromptBinding(bindings, side, readsKeyboard) is not { } binding)
        {
            return ControlLine.Empty;
        }

        return ControlLine.Compose(
            binding.Control.Kind == ControlKind.Mouse ? RespawnClickTemplate : RespawnPressTemplate,
            binding);
    }

    /// <summary>The gun gauge's selected-slot readout: the SELECTED firable group's ammo and short
    /// weapon name and the belt fraction per firable group (<paramref name="slotsOut"/>, cleared
    /// and refilled, the reused instance list a live gauge pushes each frame). Static and
    /// Loadout-free so CSVM.Tests (FlightHudMappingTests) can drive it off bare
    /// <see cref="GunGroup"/>s with no bound plane.</summary>
    public static GunGaugeReadout ComputeGunGauge(IEnumerable<GunGroup> firableGuns, int gunSel, List<float> slotsOut)
    {
        slotsOut.Clear();
        GunGroup? selected = null;
        int firable = 0;
        foreach (var g in firableGuns)
        {
            slotsOut.Add(g.Capacity > 0 ? (float)g.Ammo / g.Capacity : 0f);
            if (firable == gunSel)
            {
                selected = g;
            }
            firable++;
        }
        int selectedIndex = firable > 0 ? Mathf.Clamp(gunSel, 0, firable - 1) : 0;
        return new GunGaugeReadout(firable, selectedIndex, selected?.Ammo ?? 0, selected?.Weapon.Name ?? "");
    }

    /// <summary>The missile gauge's selected-slot readout: the SELECTED pylon's rounds are its OWN,
    /// per-pylon (the original's Warhawk gauge shows BOOM 3, not a 24-round sum across pylons).
    /// ⚠ <paramref name="slotsOut"/> is indexed by PYLON NUMBER, not position in the compacted
    /// <paramref name="hardpoints"/> list, a partial stock fit leaves gaps at the unfitted
    /// physical positions. Static so CSVM.Tests can drive it off bare <see cref="Hardpoint"/>s.</summary>
    public static MissileGaugeReadout ComputeMissileGauge(IReadOnlyList<Hardpoint> hardpoints, int pylonSelect,
        List<float> slotsOut)
    {
        if (hardpoints.Count == 0)
        {
            return default;
        }
        int sel = Mathf.Clamp(pylonSelect, 0, hardpoints.Count - 1);
        var selectedHp = hardpoints[sel];
        slotsOut.Clear();
        for (int i = 0; i < GaugeCluster.HardpointRingSize; i++)
        {
            slotsOut.Add(0f);
        }
        foreach (var h in hardpoints)
        {
            slotsOut[h.Index - 1] = h.Capacity > 0 ? (float)h.Ammo / h.Capacity : 0f;
        }
        return new MissileGaugeReadout(true, selectedHp.Index - 1, selectedHp.Ammo, selectedHp.Weapon.Name);
    }

    /// <summary>Builds the text block and parents every readout onto <paramref name="canvas"/> in
    /// the shipped draw order. <paramref name="versusHud"/> and <paramref name="scoreboard"/> are
    /// the two board-adjacent readouts the flight node still owns; they are threaded through
    /// because their z-order slots sit INSIDE this order, not after it.
    /// ⚠ <paramref name="messages"/> takes the message stack and must be a layer of its own: the
    /// crash camera hides the HUD layer, and the stack is up over that cut in the original.</summary>
    public void Attach(CanvasLayer canvas, CanvasLayer messages, Node? versusHud, Node? scoreboard)
    {
        var text = new Label { Position = TextMargin };
        text.AddThemeFontSizeOverride("font_size", TextFontSize);
        text.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.4f));
        text.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.7f));
        text.AddThemeConstantOverride("shadow_offset_y", 2);
        _text = text;
        canvas.AddChild(text);
        if (Compass != null)
            canvas.AddChild(Compass);
        if (Gauges != null)
            canvas.AddChild(Gauges);
        if (Reticle != null)
            canvas.AddChild(Reticle); // gun aiming pipper, over the dials, under the text/marker
        if (StuntRun != null)
            canvas.AddChild(StuntRun); // stunt run status + banners, drawn on top of the dials
        MessageStack = new HudMessages
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FocusMode = Control.FocusModeEnum.None,
        };
        messages.AddChild(MessageStack); // the kill/mission message stack, top centre over the dials
        CrashPrompt = new PromptLine
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FocusMode = Control.FocusModeEnum.None,
        };
        messages.AddChild(CrashPrompt); // the respawn prompt, under the crash notice it answers
        AutoDock = new PromptLine
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FocusMode = Control.FocusModeEnum.None,
        };
        canvas.AddChild(AutoDock); // the auto-dock prompt, centred below the message stack
        if (versusHud != null)
            canvas.AddChild(versusHud); // dogfight HUD: status line, kill banner, opponent markers
        if (TargetHud != null)
            canvas.AddChild(TargetHud); // targeting HUD: selected target / --debug-markers
        if (scoreboard != null)
            canvas.AddChild(scoreboard); // end-of-run results, drawn over everything
        if (FontTest != null)
            canvas.AddChild(FontTest); // --hud-font-test: the bitmap-font proof overlay
    }

    /// <summary>Binds the two weapon gauges to their persistent state, only for a system this
    /// plane actually carries, which is what the two counts decide.</summary>
    public void BindWeaponGauges(int firableGunCount, int hardpointCount)
    {
        if (Gauges == null)
        {
            return;
        }
        if (firableGunCount > 0)
        {
            _gunGauge = new GaugeCluster.WeaponGauge { Slots = _gunGaugeSlots };
            Gauges.GunGauge = _gunGauge;
        }
        if (hardpointCount > 0)
        {
            _missileGauge = new GaugeCluster.WeaponGauge { Slots = _missileGaugeSlots };
            Gauges.MissileGauge = _missileGauge;
        }
    }

    /// <summary>One rendered frame of the whole pilot HUD.</summary>
    public void Draw(in FlightHudState state)
    {
        if (Compass != null)
            Compass.HeadingDeg = state.HeadingDeg;
        if (TargetHud != null)
        {
            TargetHud.PlanePos = state.Position;
            TargetHud.HeadingDeg = state.HeadingDeg;
            TargetHud.RenderPose = state.RenderPose;   // where the spyglass eye stands, and its roll
        }
        float mph = MphFromSpeedMps(state.SpeedMps);
        float ft = FeetFromWorldY(state.Position.Y);
        if (Gauges != null)
        {
            Gauges.SpeedMph = mph;
            Gauges.AltitudeFt = ft;
            Gauges.StallWarning = ComputeStallWarning(in state);
            Gauges.AvailableLoadFactor = state.AvailableLoadFactor;
            Gauges.NitroInstalled = state.NitroInstalled;
            Gauges.NitroBoosting = state.NitroBoosting;
            Gauges.NitroChargeFrac = state.NitroChargeFrac;
            Gauges.HeadingDeg = state.HeadingDeg;
            (Gauges.HorizonPitchRad, Gauges.HorizonRollRad) = GaugeCluster.HorizonAngles(state.Attitude);
        }
        // Feeds the weapon gauges (if built) and the text readout (if built), both draw from the
        // live loadout, so this runs whenever there is one, independent of the dial cluster.
        UpdateWeaponGauges(in state);
        // Points the gun pipper at 0.5 s of the selected group's flight, on the nose axis.
        UpdateReticle(in state);
        if (AutoDock != null)
        {
            AutoDock.Prompt = ShowsAutoLandPrompt(in state) ? AutoLandPrompt : ControlLine.Empty;
        }
        if (CrashPrompt != null)
        {
            CrashPrompt.Prompt = ShowsRespawnPrompt(in state) ? RespawnPrompt : ControlLine.Empty;
        }
        UpdateTextBlock(in state, mph, ft);
    }

    /// <summary>Show or hide everything this pane draws for its pilot: the dial cluster, the
    /// compass tape, the reticle, the text block and the marker/target HUD. A cutscene hides the
    /// lot, and so does photo mode, since instruments belonging to an aircraft you are looking at
    /// from outside are noise in a picture (BL-429).
    /// ⚠ Not what <c>--debug-spectate</c> wants: that mode keeps the marker HUD
    /// deliberately, so it calls <see cref="SetInstrumentsVisible"/> instead.</summary>
    public void SetVisible(bool visible)
    {
        _shown = visible;
        ApplyVisibility();
    }

    /// <summary>Show or hide the two COCKPIT readouts alone, leaving the marker HUDs and the text
    /// block where they are. The instruments belong to an aircraft nobody is flying under
    /// <c>--debug-spectate</c>, while the marker HUD is the whole point of that mode.</summary>
    public void SetInstrumentsVisible(bool visible)
    {
        _instrumentsShown = visible;
        ApplyVisibility();
    }

    /// <summary>Whether the cockpit interior is on the screen this frame. Its own panel carries
    /// the dials, so the screen-space dials, the compass tape and the text block come off while it
    /// is; the pipper and the marker HUDs stay, since the panel has no counterpart for them. Keyed
    /// to the frame's pose, the same rule the interior itself follows, so a look-behind or a held
    /// numpad view brings the overlay back with the outside camera. Independent of the two
    /// switches above: leaving the cockpit never overrides a cutscene's or photo mode's hide.</summary>
    public void SetCockpitView(bool cockpitView)
    {
        if (_cockpitView == cockpitView)
        {
            return;
        }
        _cockpitView = cockpitView;
        ApplyVisibility();
    }

    /// <summary>The altimeter's LOW ALT feed: one ray straight down per physics frame, through the
    /// same world+aircraft mask and self-exclusion every other aircraft query uses.</summary>
    public void StepAgl(IWorldQuery world, Vector3 position, Godot.Collections.Array<Rid>? exclude)
    {
        if (Gauges == null)
        {
            return;
        }
        Gauges.AglMeters = ComputeAgl(world, position, exclude);
    }

    /// <summary>Raises the impact line on the text block for <see cref="DamageFlashTime"/>
    /// seconds. A second hit restarts the window rather than queueing behind it.</summary>
    public void Flash(string text)
    {
        _damageFlashText = text;
        _damageFlash = DamageFlashTime;
    }

    /// <summary>Counts the flash down by <paramref name="wallDt"/> and returns the impact line to
    /// show this frame, or null once it is spent. Wall time, not sim time, so a halted session
    /// does not burn the window off while nothing is drawn; halted or crashed also freezes the
    /// countdown outright rather than merely hiding the line. Public so CSVM.Tests
    /// (FlightHudMappingTests) can drive the countdown with no text Control in the process.</summary>
    public string? AdvanceDamageFlash(float wallDt, bool halted, bool crashed)
    {
        if (halted || crashed || _damageFlash <= 0f)
        {
            return null;
        }
        _damageFlash -= wallDt;
        return _damageFlashText;
    }

    /// <summary>Blinks the damage dial's struck zone, the readout half of one hit landing.</summary>
    public void OnPartDamage(string part) => Gauges?.OnPartDamage(part);

    /// <summary>Back to a healthy airframe's readouts: the dial blink timers cleared and no impact
    /// line pending. What a respawn calls.</summary>
    public void Reset()
    {
        Gauges?.Reset();
        _damageFlash = 0f;
    }

    /// <summary>The flight text block's lines in the shipped order: speed/altitude/throttle, then
    /// whichever of the stall, damage flash, damage summary, stunt status and paused lines apply.
    /// Neither prompt is one of them; each is its own centred line (<see cref="AutoDock"/>,
    /// <see cref="CrashPrompt"/>). Returns the reused instance list, valid until the next call.
    /// Public so CSVM.Tests can assert the ordering with no text Control in the process, off the
    /// numbers <see cref="MphFromSpeedMps"/> and <see cref="FeetFromWorldY"/> already expose.</summary>
    public List<string> ComposeTextLines(in FlightHudState state, float mph, float ft, bool wide)
    {
        _textLines.Clear();
        string speedAlt = $"SPD {mph,4:0} MPH   ALT {ft,5:0} FT";
        string throttle = $"THR {state.Throttle * 100,3:0}%";
        if (wide)
        {
            _textLines.Add(speedAlt);
            _textLines.Add(throttle);
        }
        else
        {
            _textLines.Add($"{speedAlt}   {throttle}");
        }
        if (!state.Held && state.Stalled)
            _textLines.Add("⚠ STALLED - SPEED UP");
        if (AdvanceDamageFlash(state.WallDt, state.Halted, state.Crashed) is { } flashLine)
            _textLines.Add(flashLine);
        if (state.DamageSummary is { Length: > 0 } dmgSummary)
            _textLines.Add($"DMG {dmgSummary}");
        // Fallback only: stunt run status normally lives in the run HUD.
        if (state.StuntStatusLine is { } stuntStatus)
            _textLines.Add(stuntStatus);
        if (state.Halted)
            _textLines.Add("⏸ PAUSED (. steps one frame)");   // the board's own menu says the rest
        return _textLines;
    }

    // Test-only: what a frame actually PUT ON SCREEN, rather than ComposeTextLines' return, which
    // a caller could compute correctly and never route to this control. Kept beside it rather than
    // hoisted, the same SA1202 trade FlightController.AutoLandPressed makes.
#pragma warning disable SA1201, SA1202
    internal string? DrawnText => _text?.Text;
#pragma warning restore SA1201, SA1202

    // Where a round of `weapon` fired from `origin` along `forward` (carrying `inheritVel`, the
    // plane's velocity) sits after travelling `distance` m of path, Ballistics.March, the SAME
    // integration ProjectilePool steps each round with, so the reticle and the rounds agree.
    private static Vector3 BallisticImpactPoint(WeaponDef weapon, Vector3 origin, Vector3 forward,
        Vector3 inheritVel, float distance)
    {
        // A fixed integration step rather than the sim's: no weapon a gun group can resolve carries
        // ACCELERATION or GRAVITY (the four accelerating defs of the 48 are rockets and a glide
        // bomb), so every marched round is a straight line, on which the step size cannot move the
        // endpoint, and a fixed step keeps the reticle from twitching with the frame rate.
        const float dt = 1f / 120f;
        return Ballistics.March(weapon, origin, forward, inheritVel, distance, dt);
    }

    // The three switches composed onto each node: the cockpit view takes the overlay off, the
    // spectate switch the instruments, and the session-level hide everything.
    private void ApplyVisibility()
    {
        bool overlay = _shown && !_cockpitView;
        if (Gauges != null)
            Gauges.Visible = overlay && _instrumentsShown;
        if (Reticle != null)
            Reticle.Visible = _shown && _instrumentsShown;
        if (Compass != null)
            Compass.Visible = overlay;
        if (_text != null)
            _text.Visible = overlay;
        if (StuntRun != null)
            StuntRun.Visible = _shown;
        if (TargetHud != null)
            TargetHud.Visible = _shown;
        if (MessageStack != null)
            MessageStack.Visible = _shown; // screen-space, so the cockpit panel does not replace it
        if (AutoDock != null)
            AutoDock.Visible = _shown;     // a message, not an instrument: the panel carries no twin
        if (CrashPrompt != null)
            CrashPrompt.Visible = _shown;  // a cutscene and photo mode take it; the crash cut cannot
    }

    // Feeds the two cockpit weapon gauges from the same live ammo the firing code draws down. With
    // `--infinite-ammo` the counters sit at capacity, so the gauges read full.
    private void UpdateWeaponGauges(in FlightHudState state)
    {
        if (state.Loadout is not { } loadout)
        {
            return;
        }

        var gun = ComputeGunGauge(loadout.FirableGuns, state.GunSelect, _gunGaugeSlots);
        if (_gunGauge != null)
        {
            _gunGauge.Selected = gun.Selected;
            _gunGauge.Count = gun.Ammo;
            _gunGauge.Type = gun.Type;
        }

        var missile = ComputeMissileGauge(loadout.Hardpoints, state.PylonSelect, _missileGaugeSlots);
        if (missile.HasHardpoints && _missileGauge != null)
        {
            _missileGauge.Selected = missile.Selected;
            _missileGauge.Count = missile.Ammo;
            _missileGauge.Type = missile.Type;
        }
    }

    // Points the gun pipper where the original points it: at the selected gun group's muzzle
    // midpoint, offset by ReticleFlightTime seconds of the round's flight, rate-smoothed. Decode:
    // docs/org/aim-assist.md "What the pipper follows".
    // ⚠ It marks the plane's NOSE axis, never the aim assist's line, the assist stays invisible
    // by design; do not make the pipper follow the assisted line instead.
    private void UpdateReticle(in FlightHudState state)
    {
        if (Reticle == null)
        {
            return;
        }
        // Hidden while crashed (the airframe is gone), a stale pipper must not hang in the sky,
        // and when there is nothing to aim, both of which arrive as a null group.
        if (state.ReticleGun is not { } sel)
        {
            Reticle.Active = false;
            return;
        }
        var origin = state.ReticleOrigin;

        // No weapon a gun group can resolve carries ACCELERATION or GRAVITY, so the straight line
        // IS the round's path (see Ballistics.cs).
        var inheritVel = state.InheritedVelocity;
        var nose = state.ReticleNose;
        float speed = sel.Weapon.Velocity ?? ReticleDefaultSpeed;
        var offset = (nose * speed + inheritVel) * ReticleFlightTime;
        float target = offset.Length();
        if (target < 1e-3f)
        {
            _reticleRate = 0f;
            Reticle.Active = false;
            return;
        }
        // Shares the rounds' own integration rather than growing a second copy of it.
        var marched = BallisticImpactPoint(sel.Weapon, origin, nose, inheritVel, target);
        // The range smoother: the closing rate builds at ReticleAccel, capped by the gap that is
        // left (so it eases in rather than overshooting), then the pipper sits at the smoothed
        // range along the same direction.
        var toward = marched - origin;
        float reach = toward.Length();
        if (reach < 1e-3f)
        {
            _reticleRate = 0f;
            Reticle.Active = false;
            return;
        }
        float gap = Mathf.Abs(_reticleDist - reach);
        float cap = gap >= ReticleFarGap ? ReticleFarRate : gap * ReticleRatePerGap;
        _reticleRate = Mathf.Min(_reticleRate + state.SimDt * ReticleAccel, cap);
        _reticleDist = Mathf.MoveToward(_reticleDist, reach, _reticleRate * state.SimDt);
        Reticle.ImpactPoint = origin + toward / reach * _reticleDist;
        Reticle.Active = true;
    }

    private void UpdateTextBlock(in FlightHudState state, float mph, float ft)
    {
        if (_text == null)
        {
            return;
        }
        // Splitscreen: the text block shrinks with the pane, like every other HUD element
        // (HudMetrics). PaneFactor is exactly 1 in single player, so the original 22 px at
        // (16,10) is untouched there; re-applied only when the factor actually changes.
        float paneFactor = HudMetrics.PaneFactor(_text);
        // ⚠ x measures from the reading box, not the pane: on a pane wider than the reference
        // frame this block would otherwise stand at the far left, a screen away from the dials it
        // belongs with. The box's left edge is 0 at 16:9 and under, so nothing moves there.
        float left = HudMetrics.ReadingBox(_text).Position.X;
        if (!Mathf.IsEqualApprox(paneFactor, _paneFactor) || !Mathf.IsEqualApprox(left, _textLeft))
        {
            _paneFactor = paneFactor;
            _textLeft = left;
            _text.AddThemeFontSizeOverride("font_size", Mathf.Max(8, Mathf.RoundToInt(TextFontSize * paneFactor)));
            _text.Position = new Vector2(left + (TextMargin.X * paneFactor), TextMargin.Y * paneFactor);
        }
        // A splitscreen pane is WIDER than tall, so a height-scaled line would run into the
        // top-centre compass tape, ComposeTextLines' wide flag splits the throttle off.
        _text.Text = string.Join("\n", ComposeTextLines(in state, mph, ft, wide: paneFactor < 1f));
    }

    /// <summary>One frame's gun-gauge readout: how many firable groups exist, which belt index the
    /// arrow targets, and the selected group's ammo and short weapon name for the dial face.</summary>
    public readonly struct GunGaugeReadout
    {
        public GunGaugeReadout(int firableCount, int selected, int ammo, string type)
        {
            FirableCount = firableCount;
            Selected = selected;
            Ammo = ammo;
            Type = type;
        }

        public int FirableCount { get; }

        public int Selected { get; }

        public int Ammo { get; }

        public string Type { get; }
    }

    /// <summary>One frame's missile-gauge readout: whether the loadout carries any hardpoint at
    /// all, and the SELECTED pylon's belt index, ammo and short weapon name for the dial face.
    /// Default (<c>HasHardpoints</c> false) when the loadout carries none.</summary>
    public readonly struct MissileGaugeReadout
    {
        public MissileGaugeReadout(bool hasHardpoints, int selected, int ammo, string type)
        {
            HasHardpoints = hasHardpoints;
            Selected = selected;
            Ammo = ammo;
            Type = type;
        }

        public bool HasHardpoints { get; }

        public int Selected { get; }

        public int Ammo { get; }

        public string Type { get; }
    }
}
