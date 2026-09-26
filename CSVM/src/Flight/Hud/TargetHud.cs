using System.Collections.Generic;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using Godot;

namespace CSVM.Flight.Hud;

/// <summary>
/// The per-pane targeting HUD, built on every human pane in every flight session (a <c>--vs</c>
/// pane gets one alongside <see cref="Modes.VersusHud"/>). It draws the pilot's own sticky selection
/// (<see cref="Selected"/>) in the original's shape: a bracket box gated on the SELECTED GUN's
/// reach (<see cref="GunReaches"/>), the label below it, and off screen the edge marker with a name
/// and clock bearing. Colour is the decoded <c>Target::GetColor</c> (<see cref="MarkerColor"/>).
/// <see cref="TrackedHostile"/> is the fallback where no selection exists at all, and the F16 key
/// or <c>--debug-markers</c> (<see cref="MarkAll"/>) widens it to every live aircraft with a
/// full identity string (<see cref="DebugTag"/>). The edge placement and clock bearing are
/// <see cref="EdgeMarker"/>'s; only the arrow and label styling is this HUD's own.
/// Decode: <see href="../../../../docs/org/targeting.md">org/targeting.md</see>.
/// </summary>
public sealed partial class TargetHud : Control
{
    /// <summary>Extra metres of authored gun range that keep the brackets on once they are on.
    /// <b>TUNE, and ours rather than the original's</b>, the original re-runs its gate every
    /// frame with no memory, which strobes the box on a target sitting exactly at the reach
    /// boundary.</summary>
    public const float BracketHysteresis = 50f;

    /// <summary>This HUD's frame-callback priority, above every flight rig's default 0.
    /// ⚠ Keep it above them. An aeroplane writes its interpolated drawn pose inside its own
    /// callback, and the spyglass aims at the target's node: a HUD that ran first aimed at the new
    /// simulation pose on a frame with a physics step and at last frame's drawn pose on one
    /// without, and the picture stepped with the target in a turn.</summary>
    public const int AfterFlightRigs = 1;

    /// <summary>Which pane this draws in (0-based), the identity <see cref="OwnTeam"/> falls
    /// back to with no aircraft bound.</summary>
    public int PlayerIndex;

    /// <summary>The pool whose registered aircraft the hostile tracker scans. Null leaves
    /// the tracker off; set, <see cref="UpdateHostile"/> re-selects the nearest live AI aircraft
    /// every frame, so a runtime spawn (generators) is picked up and a downed hostile drops
    /// without extra plumbing.</summary>
    public ProjectilePool? HostilePool;

    /// <summary><c>--debug-markers</c> at launch, <see cref="UI.Overlays.DebugMarkerToggle"/>'s F16 in
    /// flight: mark EVERY live aircraft in <see cref="HostilePool"/> instead of the single nearest
    /// hostile, red for a hostile team, blue for this pane's own side, each with its tag and slant
    /// range. A watching aid while the AI is being worked on (which of six planes is the one that
    /// flew off), off by default and never a gameplay feature: the shipped marker is one hostile.
    /// </summary>
    public bool MarkAll;

    /// <summary>This pane's own aircraft: the side every team test here runs against
    /// (<see cref="OwnTeam"/>), and the plane excluded from <see cref="MarkAll"/>'s sweep. Null
    /// marks everything the pool lists, which is what the suite wants and what a pane with no
    /// aircraft of its own (a spectator) should see.</summary>
    public FlightController? Own;

    /// <summary>The live per-zone fog band (near, far) the spyglass's range gate is derived from,
    /// or null on a pane with no weather bound, where the gate is its 2000 m cap alone.</summary>
    public System.Func<Vector2>? FogRange;

    // 1440p reference metrics (scaled by HudMetrics, matches VersusHud's calibration).
    private const int RefMarkerFont = 14;
    private const float RefOnScreenLift = 22f; // gap above a plane's own projected point
    private const float RefStaggerStep = 18f;  // --debug-markers: gap between two edge tags on one bearing

    // The off-screen marker's own decoded pixels (docs/org/spyglass.md). The arrowhead sits 20
    // back along the bearing and 5 to each side (0x006035e4 / 0x006036bc). Its label takes the
    // same 3-pixel gap a box's label does below the anchor in the pane's upper half (0x006035d0),
    // and 45 above it in the lower half (0x006082d4), which is three label lines.
    private const float RefHeadLen = 20f;
    private const float RefHeadHalf = 5f;
    private const float RefEdgeLabelUp = 45f;
    private const float RefShaftWidth = 2f;    // ours: the original draws a 1 px line sprite

    // The spyglass disc's own label rule: the three lines plus their 3-pixel gap must still fit
    // under the disc's bottom or the block flips above its top (0x006082d8 and the literal 0x2d).
    private const float RefDiscLabelBlock = 48f;
    private const float RefDiscRimWidth = 2f;  // ours, like the shaft: the original's is a 1 px circle
    private const int DiscSegments = 48;

    // The selected target's marker: the original's own absolute pixel constants (0x00607a0c /
    // 0x00607a14 / 0x00607a10 and FUN_004574d0's label anchor), read at the 1440p reference and
    // scaled by HudMetrics like every other CSVM HUD element. The original never scales its box;
    // scaling it is the deliberate divergence docs/org/targeting.md records, because 20 x 16 fixed
    // pixels is nearly invisible on a modern display.
    private const float RefBracketHalfW = 10f;
    private const float RefBracketHalfH = 8f;
    private const float RefBracketArm = 4f;
    private const float RefBracketWidth = 2f;  // ours: the original draws 1 px line sprites
    private const float RefLabelGap = 3f;      // anchor = box bottom + 3
    private const float RefLabelPitch = 15f;   // the three lines' spacing
    private const float RefLabelBlock = 33f;   // the flip-above test: box bottom + 33 must still fit
    private const float RefLabelAbove = 30f;   // flipped, the block starts box top − 30

    private static readonly Color HudBlue = new(0.55f, 0.78f, 1f);
    private static readonly Color HudRed = new(1f, 0.55f, 0.55f);

    /// <summary>The friendly-target colour. The decode's own answer is a friendly is GREEN, not blue
    /// (docs/org/targeting.md "Colour"); this is that (0,255,0) in the HUD palette's desaturation,
    /// the same relationship <see cref="HudRed"/> has to the decoded (200,0,0).</summary>
    private static readonly Color HudGreen = new(0.6f, 1f, 0.6f);

    private static readonly Color Shadow = new(0f, 0f, 0f, 0.75f);

    /// <summary>The four objective categories the original draws RED, the rest blue
    /// (<c>MSG_OBJ_DESTROY</c> / <c>DISABLE</c> / <c>DISABLEENG</c> / <c>DAMAGE</c>).</summary>
    private static readonly string[] DestructiveCategories =
        { "Destroy", "Disable", "Disable Engines", "Damage" };

    private readonly List<string> _labelLines = new();  // rebuilt per draw
    private readonly Vector2[] _discPoints = new Vector2[DiscSegments];  // the mask, refilled per draw
    private readonly Vector2[] _discUvs = new Vector2[DiscSegments];

    private readonly AimCandidateSet _hostileScan = new(); // rebuilt per frame, aircraft list only
    private readonly List<(TargetRef Target, bool Friendly)> _marks = new(); // --debug-markers

    private Camera3D _camera = null!;
    private SpyglassView? _spyglass;
    private FlightController? _hostile;
    private string _hostileTag = "AI";
    private bool _bracketed;   // last frame's gate answer, for the hysteresis
    private object? _heldSubject;  // what the spyglass is holding, the clone at +0x20c
    private float _heldRadius;     // that subject's framing radius, measured once per hold

    /// <summary>This pane's own world pose, fed every frame by FlightController, the tracked
    /// hostile's clock bearing reads off it, exactly like VersusHud's PlanePos/HeadingDeg.</summary>
    public Vector3 PlanePos { get; set; }

    /// <summary>This pane's own nose heading, 0 = north (−Z), see <see cref="PlanePos"/>.</summary>
    public float HeadingDeg { get; set; }

    /// <summary>The pose this pane's aeroplane is DRAWN at, the render-interpolated one the chase
    /// camera follows, which the spyglass eye stands on and rolls with (for an aircraft target;
    /// every other class is watched level).
    /// ⚠ Not <see cref="PlanePos"/>'s sim pose. The target point the picture is aimed at comes off
    /// the same interpolation, and an eye quantised to the physics tick against it is the shake
    /// <see cref="FlightController.TryRenderPosition"/> exists to keep out of the markers.</summary>
    public Transform3D RenderPose { get; set; } = Transform3D.Identity;

    /// <summary>Whether this pane's spyglass is armed, <see cref="Spyglass.DefaultOn"/> at level
    /// load; the pilot's toggle flips it and every other gate is re-answered every frame regardless
    /// of it.</summary>
    public bool SpyglassOn { get; set; } = Spyglass.DefaultOn;

    /// <summary>Whether the round picture is up this frame, which is also what moves the anchor,
    /// the shaft's start and the label. Answered by <see cref="UpdateSpyglass"/>.</summary>
    public bool DiscShown { get; private set; }

    /// <summary>Whether the picture's own viewport is rendering, which <see cref="DiscShown"/>
    /// alone does not say: a pane with no scale yet shows the gate open and the viewport idle.
    /// Read by the suite.</summary>
    public bool PictureLive => _spyglass is { Live: true };

    /// <summary>This pane's picture, or null on a HUD built without one. Read by the suite, which
    /// pins where its eye stands and what its camera is allowed to draw.</summary>
    public SpyglassView? Picture => _spyglass;

    /// <summary>This pane's tracked AI hostile, or null with none in the pool. Read by the
    /// suite; the fallback marker draws off the same field.</summary>
    public FlightController? TrackedHostile => _hostile;

    /// <summary>The pilot's own sticky selection, or null on a pane with no
    /// <see cref="TargetSelection"/> bound (a spectator, a suite rig). The shipped marker.</summary>
    public TargetRef? Selected => Own?.Targeting?.Current;

    /// <summary>Whether the brackets are currently drawn around <see cref="Selected"/>, the gun's
    /// reach gate plus its hysteresis, re-answered every <see cref="_Process"/>. Read by the
    /// suite.</summary>
    public bool Bracketed => _bracketed;

    /// <summary>The side this pane is on: <see cref="Own"/>'s <see cref="FlightController.Team"/>
    /// field, falling back to the pilot-index derivation only with no aircraft bound.
    /// ⚠ Read the FIELD. Deriving <c>AimAssist.TeamOfPilot(PlayerIndex)</c> is right for P1 by
    /// coincidence and wrong once a mission sets teams (under Instant Action and <c>--coop</c> every
    /// human is team 1), which is the wingman-in-the-marker bug.</summary>
    public int OwnTeam => Own?.Team ?? AimAssist.TeamOfPilot(PlayerIndex);

    /// <summary>Binds this pane's own camera (the marker projects through it) and, when
    /// <paramref name="pool"/> is non-null, the hostile tracker. Add to the HUD canvas;
    /// <see cref="PlanePos"/>/<see cref="HeadingDeg"/> every frame, nothing else needs feeding.
    /// One per human pane, in EVERY flight session; it draws nothing until a hostile
    /// exists, so an AI-free session's HUD output is unchanged.</summary>
    public static TargetHud Build(int playerIndex, Camera3D camera, ProjectilePool pool)
    {
        var hud = new TargetHud
        {
            PlayerIndex = playerIndex,
            _camera = camera,
            HostilePool = pool,
            MouseFilter = MouseFilterEnum.Ignore,
            FocusMode = FocusModeEnum.None,
            ProcessPriority = AfterFlightRigs,
        };
        // The picture hangs on the HUD control itself, so it lives inside this pane's viewport and
        // renders this pane's world; it is idle until a target is off screen and inside the gate.
        hud._spyglass = SpyglassView.Build(camera, UI.Boards.SplitScreen.OwnAirframeLayer(playerIndex));
        hud.AddChild(hud._spyglass);
        return hud;
    }

    /// <summary>The nearest live hostile AI aircraft in <paramref name="scan"/>, or null. Pure
    /// over the candidate snapshot: live (a crashed plane is listed but not live), an
    /// AI-piloted <see cref="FlightController"/> (the human pane's hostile class: another
    /// human is a VS opponent, never a hostile here), and the engine's team gate (either side
    /// neutral rejects the pair).</summary>
    public static FlightController? NearestHostile(Vector3 ownPos, int ownTeam, AimCandidateSet scan)
    {
        FlightController? best = null;
        float bestD2 = float.MaxValue;
        foreach (var c in scan.Vehicles)
        {
            if (!c.Live || c.Team == AimAssist.NeutralTeam || ownTeam == AimAssist.NeutralTeam
                || c.Team == ownTeam)
                continue;
            if (c.Source is not FlightController { IsHumanPiloted: false } fc)
                continue;
            float d2 = ownPos.DistanceSquaredTo(c.Position);
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = fc;
            }
        }

        return best;
    }

    /// <summary>Every live aircraft in <paramref name="scan"/>, wrapped as a <see cref="TargetRef"/>
    /// and paired with whether it is on <paramref name="ownTeam"/> (<c>--debug-markers</c>),
    /// skipping <paramref name="own"/>. The team test is the plain identity one, not the assist's: a
    /// neutral aircraft is nobody's friend, so a debugging overlay marks it hostile rather than
    /// letting it vanish. The <see cref="TargetRef"/> wrapping is what lets <see cref="DebugTag"/>
    /// read health/armor as optional fields.</summary>
    public static void CollectMarks(int ownTeam, object? own, AimCandidateSet scan,
        List<(TargetRef Target, bool Friendly)> into)
    {
        foreach (var c in scan.Vehicles)
        {
            if (!c.Live || ReferenceEquals(c.Source, own))
                continue;
            if (c.Source is not FlightController fc)
                continue;
            var dmg = fc.Damage;
            var cls = TargetRef.Classify(AimTargetKind.Vehicle, live: true, c.Team, ownTeam)
                ?? TargetClass.Enemy;
            var target = TargetRef.ForAircraft(c, cls, fc.Name,
                fc.Stats is { } stats ? PlaneRoster.PlaneDisplayName(stats) : null,
                dmg == null ? null : TargetRef.Fraction(dmg.WholeHealth, dmg.WholeHealthMax),
                dmg == null ? null : TargetRef.Fraction(dmg.WholeArmor, dmg.WholeArmorMax));
            into.Add((target, c.Team == ownTeam && c.Team != AimAssist.NeutralTeam));
        }
    }

    /// <summary>A marked plane's current AI mode in the ENGINE's own vocabulary
    /// (<see cref="AiModeMachine.NameOf"/>: patrol / pursue / lay off / evade / evasive maneuver /
    /// stunned / avoid crash), or empty for anything without a mode machine (a human seat, or an
    /// AI flying bare orders). <c>--debug-markers</c> only; the shipped marker carries a tag and a
    /// bearing, nothing about the other pilot's state.</summary>
    public static string ModeSuffix(FlightController plane) =>
        plane.Pilot?.Machine is { } machine ? $"  {AiModeMachine.NameOf(machine.Mode)}" : "";

    /// <summary>The marker tag for a hostile: the controller name's first '_'-segment,
    /// uppercased ("ai1_player_fury" reads "AI1"), "AI" when the name yields nothing.</summary>
    public static string HostileTag(string name)
    {
        int cut = name.IndexOf('_');
        string head = cut > 0 ? name.Substring(0, cut) : name;
        return head.Length > 0 ? head.ToUpperInvariant() : "AI";
    }

    /// <summary><c>--debug-markers</c>' own tag: <c>AI1 Fury 640 m H78 A91 pursue</c>. The one
    /// marker that keeps the FULL identity string rather than the shipped marker's plane-type-alone
    /// label, plus the plane type, the slant range, health then armor as whole percentages with no
    /// percent sign, and <paramref name="modeSuffix"/>.
    /// ⚠ Omit health or armor where the source carries no figure; never default it. <c>H100</c> for
    /// a turret with no health model is a number the game does not have.</summary>
    public static string DebugTag(string identity, in TargetRef target, float rangeM, string modeSuffix)
    {
        var tag = new StringBuilder(identity);
        if (target.DisplayName is { Length: > 0 } name)
        {
            tag.Append(' ').Append(name);
        }

        tag.Append(' ').Append(Mathf.RoundToInt(rangeM)).Append(" m");
        if (target.Health is { } health)
        {
            tag.Append(" H").Append(Mathf.RoundToInt(health * 100f));
        }

        if (target.Armor is { } armor)
        {
            tag.Append(" A").Append(Mathf.RoundToInt(armor * 100f));
        }

        tag.Append(modeSuffix);
        return tag.ToString();
    }

    /// <summary>The marker's colour, <c>Target::GetColor</c> (<c>FUN_004a5f40</c>) verbatim: an
    /// objective is red for the four destructive categories and blue for every other one, and
    /// anything without a category is red when the teams differ and both are non-zero, green
    /// otherwise.
    /// ⚠ A friendly is GREEN. Blue is the non-destructive objective (protect, escort).</summary>
    public static Color MarkerColor(in TargetRef target, int ownTeam)
    {
        if (target.Category is { Length: > 0 } category)
        {
            foreach (var destructive in DestructiveCategories)
            {
                if (string.Equals(category, destructive, System.StringComparison.OrdinalIgnoreCase))
                {
                    return HudRed;
                }
            }

            return HudBlue;
        }

        return target.Team != ownTeam && target.Team != AimAssist.NeutralTeam
            && ownTeam != AimAssist.NeutralTeam
            ? HudRed
            : HudGreen;
    }

    /// <summary>The bracket gate's arithmetic (<c>FUN_004574d0</c>): solve the aim assist's lead
    /// intercept, accept only inside the weapon's authored <paramref name="range"/>. No intercept
    /// (a target outrunning the round) is no brackets, at any distance.
    /// ⚠ Measure the reach along the MUZZLE VELOCITY: the round's speed along the intercept
    /// direction PLUS the plane's. The assist's own range gate uses the round speed alone.
    /// ⚠ Brackets UNDER the gun's reach, never past it; the inverse was proposed and declined, do not flip it.</summary>
    public static bool GunReaches(Vector3 muzzle, Vector3 shooterVel, float speed, float range,
        Vector3 targetPos, Vector3 targetVel)
    {
        if (!AimAssist.TryIntercept(muzzle, speed, targetPos, targetVel - shooterVel,
                out var dir, out float t))
        {
            return false;
        }

        float reach = (dir * speed + shooterVel).Length() * t;
        return reach <= range;
    }

    /// <summary>The marker's label lines, top to bottom, the original's three text elements
    /// (<c>FUN_004579e0</c>): the category line, the name, and <paramref name="bearing"/> (null on
    /// screen, since the clock line is the off-screen case only).
    /// ⚠ <paramref name="keepSlots"/> holds an empty category SLOT under a box: the original's lines
    /// sit at fixed offsets, so an aircraft's name is the SECOND line's distance down and closing
    /// that gap puts the name into the silhouette. At the screen edge there is no box.</summary>
    public static void LabelLines(in TargetRef target, string? bearing, List<string> into,
        bool keepSlots = true)
    {
        string category = target.CategoryLine;
        if (category.Length > 0 || keepSlots)
        {
            into.Add(category);
        }

        if (target.DisplayName is { Length: > 0 } name)
        {
            into.Add(name);
        }

        if (bearing is { Length: > 0 })
        {
            into.Add(bearing);
        }
    }

    /// <summary>The arrowhead's three points at <paramref name="tip"/>: the point itself, then the
    /// two base corners <see cref="RefHeadLen"/> back along <paramref name="dir"/> and
    /// <see cref="RefHeadHalf"/> to each side, at scale <paramref name="s"/>. Fixed size, so an
    /// arrow's visible length is its shaft's (docs/org/spyglass.md "The arrow").</summary>
    public static (Vector2 Point, Vector2 Left, Vector2 Right) ArrowHead(Vector2 tip, Vector2 dir,
        float s)
    {
        var back = tip - dir * (RefHeadLen * s);
        var perp = new Vector2(-dir.Y, dir.X) * (RefHeadHalf * s);
        return (tip, back + perp, back - perp);
    }

    /// <summary>The off-screen label block's anchor: the marker anchor's x untouched, and its y 3
    /// pixels down in the pane's upper half or 45 up in the lower one. With <paramref name="disc"/>
    /// the same block is measured against the picture instead, 3 below its bottom unless the whole
    /// block would fall off the pane, then 45 above its top. There is no back-off along the arrow's
    /// direction (docs/org/spyglass.md "The label anchor").</summary>
    public static Vector2 EdgeLabelAnchor(Vector2 anchor, float paneHeight, float s,
        bool disc = false)
    {
        if (!disc)
        {
            return new Vector2(anchor.X,
                anchor.Y + ((anchor.Y <= paneHeight * 0.5f ? RefLabelGap : -RefEdgeLabelUp) * s));
        }

        float bottom = anchor.Y + (Spyglass.RefRadius * s);
        return new Vector2(anchor.X,
            bottom + (RefDiscLabelBlock * s) <= paneHeight
                ? bottom + (RefLabelGap * s)
                : anchor.Y - ((Spyglass.RefRadius + RefEdgeLabelUp) * s));
    }

    /// <summary>Where the arrow's shaft starts: the marker anchor, or the disc's rim along the
    /// bearing once the picture is up, so the shaft leaves the picture rather than crossing it
    /// (<c>0x0049e2ae</c>).</summary>
    public static Vector2 ShaftTail(Vector2 anchor, Vector2 dir, float s, bool disc) =>
        disc ? anchor + (dir * (Spyglass.RefRadius * s)) : anchor;

    public override void _Process(double delta)
    {
        // Track the pane (resizable window / splitscreen layout) and repaint every frame.
        Position = Vector2.Zero;
        Size = GetViewportRect().Size;
        UpdateHostile();
        UpdateBrackets();
        StepSpyglass();
        QueueRedraw();
    }

    /// <summary>Re-answers the bracket gate for this frame's selection: the selected gun's reach
    /// (<see cref="FlightController.GunReachesTarget"/>), widened by
    /// <see cref="BracketHysteresis"/> while the brackets are already on. Called every
    /// <see cref="_Process"/>; public so the suite drives it without pumping frames. Kept out of
    /// <see cref="_Draw"/> deliberately, a hysteresis that advanced per repaint would depend on how
    /// often the pane redraws.</summary>
    public void UpdateBrackets()
    {
        var target = Selected;
        bool next = Own != null && target is { } t
            && Own.GunReachesTarget(t.Position, t.Velocity,
                _bracketed ? BracketHysteresis : 0f);
        if (next != _bracketed && target is { } shown)
        {
            // The transitions only, like UpdateHostile's: a steady chase inside gun range stays
            // quiet, and the line is what makes the range gate readable without a screenshot.
            Utils.Log.Info("flight",
                $"targeting hud: P{PlayerIndex + 1} brackets {(next ? "on" : "off")} {shown.DisplayName} at {PlanePos.DistanceTo(shown.Position):0} m");
        }

        _bracketed = next;
    }

    /// <summary>Re-selects this pane's tracked hostile from the pool's live aircraft roster:
    /// nearest live AI aircraft to <see cref="PlanePos"/>, null with none. Called every
    /// <see cref="_Process"/>; public so the suite drives it without pumping frames. Logs the
    /// acquire/lose transitions only, so a steady chase stays quiet.</summary>
    public void UpdateHostile()
    {
        FlightController? next = null;
        if (HostilePool != null)
        {
            _hostileScan.Clear();
            HostilePool.CollectAircraft(_hostileScan);
            next = NearestHostile(PlanePos, OwnTeam, _hostileScan);
        }
        if (ReferenceEquals(next, _hostile))
            return;
        if (next != null)
        {
            _hostileTag = HostileTag(next.Name);
            Utils.Log.Info("flight",
                $"targeting hud: P{PlayerIndex + 1} tracking {next.Name} at {PlanePos.DistanceTo(next.WorldPosition):0} m");
        }
        else
        {
            // The tag was cached at acquire, so a freed hostile is never dereferenced here.
            Utils.Log.Info("flight", $"targeting hud: P{PlayerIndex + 1} lost {_hostileTag}");
        }
        _hostile = next;
    }

    /// <summary>Re-answers whether the round picture is up for <paramref name="target"/> at
    /// <paramref name="at"/>, and aims it when it is: armed, off screen, and inside
    /// <see cref="Spyglass.RangeGate"/> for this pane's fog band, the wider gate while this same
    /// subject is already held. The framing radius is measured once per hold, a mesh box being
    /// fixed. Called every <see cref="_Process"/>; public so the suite drives it without pumping
    /// frames.</summary>
    public bool UpdateSpyglass(in TargetRef target, Vector3 at, bool offScreen)
    {
        bool held = _heldSubject != null && ReferenceEquals(_heldSubject, target.Source);
        float gate = Spyglass.RangeGate(FogRange?.Invoke() ?? Vector2.Zero, held);
        if (!SpyglassOn || !offScreen || PlanePos.DistanceTo(at) > gate)
        {
            ReleaseSpyglass();
            return false;
        }

        if (!held)
        {
            _heldSubject = target.Source;
            _heldRadius = FramingRadius(target);
        }

        DiscShown = true;
        float s = Size.Y <= 0f ? 0f : HudMetrics.Scale(this);
        if (_spyglass != null && s > 0f)
        {
            // The eye stands on the DRAWN pose, never the sim one the gates above read: an eye
            // quantised to the physics tick shakes against the frame around it. The roll is the
            // original's aircraft case alone, every other class is watched level.
            _spyglass.Aim(
                Spyglass.Pose(RenderPose.Origin, at, RenderPose.Basis,
                    target.Source is FlightController),
                Spyglass.FovDeg(_heldRadius, PlanePos.DistanceTo(at)),
                Mathf.Max(1, Mathf.RoundToInt(Spyglass.RefWindow * s)));
        }

        return true;
    }

    /// <summary>Drops the picture and forgets what it was holding, so the next engage re-measures
    /// its framing radius and takes the narrower gate. Idempotent, called on every frame the disc
    /// is down.</summary>
    public void ReleaseSpyglass()
    {
        DiscShown = false;
        _heldSubject = null;
        _heldRadius = 0f;
        _spyglass?.Idle();
    }

    public override void _Draw()
    {
        // Same zero-size guard as VersusHud: a draw can land before _Process has sized
        // this pane.
        float s = Size.Y <= 0f ? 0f : HudMetrics.Scale(this);
        if (s <= 0f)
            return;
        var font = GetThemeDefaultFont();
        int markerFont = Mathf.Max(1, Mathf.RoundToInt(RefMarkerFont * s * HudMetrics.MarkerTextScale));

        // The shipped marker, drawn in BOTH modes unlike the hostile tracker below: --debug-markers
        // replaces that tracker but not this, so brackets, label and debug string draw in one frame
        // (the c1-flight-kill golden).
        bool selected = Selected is { } target && DrawSelected(font, target, s, markerFont);

        // --debug-markers: every live aircraft at once, red hostile / blue own side, each with
        // its slant range. Replaces the single-hostile marker below rather than stacking on it,
        // so the tracked one is not drawn twice in two colours.
        if (MarkAll && HostilePool != null)
        {
            _marks.Clear();
            CollectMarks(OwnTeam, Own, _hostileScan, _marks);
            int stagger = 0;
            foreach (var (mark, friendly) in _marks)
            {
                if (mark.Source is not FlightController plane
                    || !GodotObject.IsInstanceValid(plane) || !plane.IsInsideTree())
                    continue;
                var at = plane.GlobalPosition;
                DrawOpponent(font, at, friendly ? HudBlue : HudRed,
                    DebugTag(HostileTag(mark.Name), mark, PlanePos.DistanceTo(at),
                        ModeSuffix(plane)),
                    s, markerFont, stagger++);
            }

            return;
        }

        // The tracked AI hostile, a FALLBACK: a pilot who has a selection marks that one target and
        // nothing else, so this draws only where no selection exists at all. The validity guard
        // covers a hostile freed between UpdateHostile's scan and this draw.
        if (!selected && GodotObject.IsInstanceValid(_hostile) && _hostile is { InPlay: true } hostile
            && hostile.IsInsideTree())
            DrawOpponent(font, hostile.GlobalPosition, HudRed, _hostileTag, s, markerFont);
    }

    // The target's framing radius: half the diagonal of its merged mesh box, which is what holds a
    // banking aeroplane whole in the picture where half its widest span would clip the wingtips. A
    // meshless or detached subject measures zero, and Spyglass.FovDeg then takes its 3 degrees.
    private static float FramingRadius(in TargetRef target)
    {
        if (target.Source is not Node3D node || !GodotObject.IsInstanceValid(node)
            || !node.IsInsideTree())
        {
            return 0f;
        }

        return UI.Overlays.OrbitCamera.MergedAabb(node).Size.Length() * 0.5f;
    }

    // The spyglass gate off this frame's selection, kept beside the bracket gate rather than in
    // _Draw: both latch, and a latch that advanced per repaint would depend on how often the pane
    // redraws. A hidden pane (a dead pilot, a cut scene) holds nothing and renders nothing.
    private void StepSpyglass()
    {
        if (!Visible || Selected is not { Source: not null } sel
            || !GodotObject.IsInstanceValid(_camera))
        {
            ReleaseSpyglass();
            return;
        }

        var at = FlightController.TryRenderPosition(sel.Source, out var render)
            ? render
            : sel.Position;
        var placed = EdgeMarker.Resolve(_camera.UnprojectPosition(at),
            _camera.IsPositionBehind(at), Size);
        UpdateSpyglass(sel, at, !placed.OnScreen);
    }

    // The selected target's marker: on screen, the bracket box (when the gun reaches it) over the
    // label block; off screen, the edge marker with that block plus the clock bearing and no box.
    // The FUN_004574d0 label anchor is computed from the box whether or not the box is drawn, so an
    // out-of-range target's label does not move. False = nothing drawn, so the caller falls back.
    // ⚠ The clock line is the OFF-SCREEN case only: the Kestrel shot shows an on-screen target
    // with its name alone.
    private bool DrawSelected(Font font, in TargetRef target, float s, int fontSize)
    {
        if (target.Source == null)
        {
            return false;
        }

        // Drawn at the source's render pose, like --debug-markers; the candidate's own Position is
        // the physics pose the gun solves to, and projecting that through a camera on the render
        // pose is a fly-by shake. The gate in UpdateBrackets stays on the physics pose.
        if (!FlightController.TryRenderPosition(target.Source, out var pos))
        {
            pos = target.Position;
        }
        var color = MarkerColor(target, OwnTeam);
        bool behind = _camera.IsPositionBehind(pos);
        var sp = _camera.UnprojectPosition(pos);
        var placed = EdgeMarker.Resolve(sp, behind, Size);
        _labelLines.Clear();
        if (placed.OnScreen)
        {
            if (_bracketed)
            {
                DrawBrackets(sp, color, s);
            }

            LabelLines(target, null, _labelLines);
            float below = sp.Y + (RefBracketHalfH + RefLabelGap) * s;
            float anchor = sp.Y + (RefBracketHalfH + RefLabelBlock) * s <= Size.Y
                ? below
                : sp.Y - (RefBracketHalfH + RefLabelAbove) * s;
            DrawLabelBlock(font, new Vector2(sp.X, anchor), color, s, fontSize);
            return true;
        }

        // Off screen: the edge marker's arrow from the anchor out to the tip, and the block above or
        // below it, the tag broken onto its own lines the way HUD.png shows. With the picture up
        // all three measure against the disc instead, which is half a window further in.
        bool disc = DiscShown;
        float radius = Spyglass.RefRadius * s;
        if (disc)
        {
            placed = EdgeMarker.Resolve(sp, behind, Size, radius);
            DrawDisc(placed.Anchor, radius, color, s);
        }

        DrawArrow(placed.Tip, ShaftTail(placed.Anchor, placed.Dir, s, disc), placed.Dir, s, color);
        LabelLines(target, $"{EdgeMarker.ClockHour(PlanePos, HeadingDeg, pos)} o'clock",
            _labelLines, keepSlots: false);
        DrawLabelBlock(font, EdgeLabelAnchor(placed.Anchor, Size.Y, s, disc), color, s, fontSize);
        return true;
    }

    // The picture itself: the viewport's texture masked to a circle of that radius at that centre,
    // ringed in the marker's own colour. The mask is a polygon rather than a shader, so it costs
    // one draw call and needs no material on the HUD control (the original blits a pre-cut round
    // sprite).
    private void DrawDisc(Vector2 center, float radius, Color color, float s)
    {
        for (int i = 0; i < DiscSegments; i++)
        {
            float a = Mathf.Tau * i / DiscSegments;
            var unit = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            _discPoints[i] = center + (unit * radius);
            _discUvs[i] = (unit + Vector2.One) * 0.5f;
        }

        if (_spyglass?.GetTexture() is { } picture)
        {
            DrawColoredPolygon(_discPoints, Colors.White, _discUvs, picture);
        }

        float w = Mathf.Max(1f, RefDiscRimWidth * s);
        var off = new Vector2(1.5f, 1.5f) * s;
        DrawArc(center + off, radius, 0f, Mathf.Tau, DiscSegments, Shadow, w, antialiased: true);
        DrawArc(center, radius, 0f, Mathf.Tau, DiscSegments, color, w, antialiased: true);
    }

    /// <summary>The bracket box: six line sprites forming a <c>[ ]</c> pair around the projected
    /// point, 20 x 16 reference pixels with 4-pixel arms (<c>0x00607a0c</c>/<c>0x00607a14</c>/
    /// <c>0x00607a10</c>). Fixed size, scaled for resolution by <paramref name="s"/> and by nothing
    /// else, least of all by range.</summary>
    private void DrawBrackets(Vector2 at, Color color, float s)
    {
        // The same drop shadow the arrow and the tags carry, so the box stays readable over sky.
        Brackets(at + new Vector2(1.5f, 1.5f) * s, Shadow, s);
        Brackets(at, color, s);
    }

    private void Brackets(Vector2 at, Color color, float s)
    {
        float hw = RefBracketHalfW * s, hh = RefBracketHalfH * s, arm = RefBracketArm * s;
        float w = Mathf.Max(1f, RefBracketWidth * s);
        for (int side = 0; side < 2; side++)
        {
            float x = side == 0 ? at.X - hw : at.X + hw;
            var inward = new Vector2(side == 0 ? arm : -arm, 0f);
            var top = new Vector2(x, at.Y - hh);
            var bottom = new Vector2(x, at.Y + hh);
            DrawLine(top, bottom, color, w, antialiased: true);
            DrawLine(top, top + inward, color, w, antialiased: true);
            DrawLine(bottom, bottom + inward, color, w, antialiased: true);
        }
    }

    /// <summary>The label lines stacked downward from <paramref name="anchor"/> at the original's
    /// 15-pixel pitch, each centred on it. The anchor is the block's first LINE (our text is centred
    /// vertically on the point it is given, where the original's is drawn from a baseline), which is
    /// the one place this geometry is ours rather than measured.</summary>
    private void DrawLabelBlock(Font font, Vector2 anchor, Color color, float s, int fontSize)
    {
        for (int i = 0; i < _labelLines.Count; i++)
        {
            if (_labelLines[i].Length == 0)
            {
                continue;   // a blank slot draws nothing and still holds its place
            }

            DrawTag(font, anchor + new Vector2(0f, i * RefLabelPitch * s), _labelLines[i], color,
                fontSize);
        }
    }

    /// <summary>One tracked hostile's marker: on screen, its tag floats just above the projected
    /// point; off screen (or behind), the edge marker's arrow + "N o'clock" bearing,
    /// <see cref="EdgeMarker"/>'s placement, plus the <paramref name="stagger"/> step
    /// <c>--debug-markers</c> needs when several planes share one bearing.</summary>
    private void DrawOpponent(Font font, Vector3 pos, Color color, string tag, float s, int fontSize,
        int stagger = 0)
    {
        bool behind = _camera.IsPositionBehind(pos);
        Vector2 sp = _camera.UnprojectPosition(pos);
        var placed = EdgeMarker.Resolve(sp, behind, Size);
        if (placed.OnScreen)
        {
            DrawTag(font, sp + new Vector2(0f, -RefOnScreenLift * s), tag, color, fontSize);
            return;
        }
        DrawArrow(placed.Tip, placed.Anchor, placed.Dir, s, color);
        // Several planes on one bearing put their tags on the same pixel (--debug-markers marks
        // six at once); step each one along the screen edge so all of them stay readable.
        var along = new Vector2(-placed.Dir.Y, placed.Dir.X) * (stagger * RefStaggerStep * s);
        DrawTag(font, EdgeLabelAnchor(placed.Anchor, Size.Y, s) + along,
            $"{tag}  {EdgeMarker.ClockHour(PlanePos, HeadingDeg, pos)} o'clock", color, fontSize);
    }

    // The arrow the original draws: a shaft from the label anchor out to the tip, carrying the
    // apparent length, and the fixed head on the tip's end. Both take the drop shadow the box and
    // the tags carry, so the arrow stays readable over sky.
    private void DrawArrow(Vector2 tip, Vector2 tail, Vector2 dir, float s, Color color)
    {
        var (point, left, right) = ArrowHead(tip, dir, s);
        var off = new Vector2(1.5f, 1.5f) * s;
        float w = Mathf.Max(1f, RefShaftWidth * s);
        DrawLine(tail + off, point + off, Shadow, w, antialiased: true);
        DrawColoredPolygon(new[] { point + off, left + off, right + off }, Shadow);
        DrawLine(tail, point, color, w, antialiased: true);
        DrawColoredPolygon(new[] { point, left, right }, color);
    }

    private void DrawTag(Font font, Vector2 center, string text, Color color, int fontSize)
    {
        float w = font.GetStringSize(text, HorizontalAlignment.Left, -1f, fontSize).X;
        float h = font.GetHeight(fontSize);
        var p = new Vector2(center.X - w / 2f, center.Y - h / 2f + font.GetAscent(fontSize));
        DrawString(font, p + Vector2.One, text, HorizontalAlignment.Left, -1f, fontSize, Shadow);
        DrawString(font, p, text, HorizontalAlignment.Left, -1f, fontSize, color);
    }
}
