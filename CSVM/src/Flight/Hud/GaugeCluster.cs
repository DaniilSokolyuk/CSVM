using System;
using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Flight.Hud;

/// <summary>
/// The original's cockpit gauges as a screen-space HUD: altimeter (two needles + blinking LOW
/// ALT), speedometer (needle + blinking STALL), and the per-plane damage display (part fills +
/// border bars, blinking after a hit). Everything is rebuilt from each plane's own 'gauges'
/// subtree in planes.zbd, see docs/formats/hud.md for the dial geometry, texture cycles and
/// scales. Only screen placement and the value→angle scales are ours, measured in
/// OriginalScreenshots/HUD.png like CompassTape.
/// </summary>
public sealed partial class GaugeCluster : Control
{
    // Belt indicator colour by remaining fraction: the low tier lights at or below a quarter full.
    // Decoded (0x006034f4, compared at 0x004547de), one state function serves BOTH gauges, so this
    // is not gun-only. ⚠ Was a 0.15 TUNE, and the gun-only reading came with it.
    public const float IndicatorLowFrac = 0.25f;
    // Weapon-gauge arrow sweep rate, shared by both gauges: 0.8 revolutions per second, constant,
    // with no easing at either end (FUN_004544b0, step = frame dt x 5.0265484 = 0.8 x 2pi).
    // ⚠ Supersedes a 168.7 °/s figure measured off footage. Public so CSVM.Tests can assert it.
    public const float ArrowSweepDegPerSimS = 288f;
    // The two nitro needles' decoded law: each chases its target through the shared exponential
    // (slot = target + (slot − target)·exp(−rate·dt)), full sweep 3.7699 rad = 216°, the boost
    // needle at 3/s and the charge needle at 1.5/s (FUN_004568c0).
    public const float NitroNeedleSweepDeg = 216f;
    public const float NitroBoostNeedleRate = 3f;
    public const float NitroChargeNeedleRate = 1.5f;
    // The needle laws, decoded. Each is a rate per unit of the value the needle shows, and the
    // engine writes an ABSOLUTE angle (the modeled rest rotations are arbitrary). Quoted here
    // clockwise-positive, which is what the screen-space draw wants; the in-3D drive negates them,
    // since the authored Euler-z is counter-clockwise-positive.
    public const float AltHundredsDegPerFt = 0.36f;     // 0x00607704, 1,000 ft per revolution
    public const float AltThousandsDegPerFt = 0.036f;   // 0x00607700, 10,000 ft per revolution
    public const float SpeedDegPerMph = 0.7199957f;     // 0x006076e8 with the mph factor at 0x006076e4

    // Each column's outer edge in the reference frame: the outermost dial centre less the shared
    // radius. Public so CSVM.Tests can assert the placement against the border a pane offers.
    public const float LeftColumnMargin = 340.5f;       // altimeter/rockets centre 425.5 less R 85
    public const float RightColumnMargin = 335f;        // speedometer/guns/nitro 420 less R 85

    // ---- state fed by the FlightController ----
    public float AltitudeFt;               // above sea level (the dial is in feet)
    public float AglMeters = float.MaxValue; // above ground (physics ray), LOW ALT
    public float SpeedMph;
    /// <summary>STALL lamp gate, the flight model's stall WARNING (under 2.35 g of available
    /// lift), which lights well before the nose-drop the model flies at 1 g.</summary>
    public bool StallWarning;
    /// <summary>Available lift as a multiple of weight (FlightModel.AvailableLoadFactor): the
    /// STALL lamp's blink RATE ramps over it. Only read while StallWarning is set.</summary>
    public float AvailableLoadFactor = StallWarnLoadFactor;
    /// <summary>Part HP source for the damage display: name → fraction (1 = pristine).
    /// Flight binds PlaneDamage, the damage lab binds its sliders. Null = all green.</summary>
    public Func<string, float>? PartFraction;

    /// <summary>The gun gauge's per-frame state (selected gun group). Null hides it (no loadout).</summary>
    public WeaponGauge? GunGauge;
    /// <summary>The missile gauge's per-frame state (selected ordnance type). Null hides it.</summary>
    public WeaponGauge? MissileGauge;

    /// <summary>The nitro gauge's feed: drawn only with the injector installed, as the original
    /// shows the dial only with the nitrous engine. The boost needle chases full sweep while
    /// boosting, the charge needle the tank's empty fraction (docs/org/flightModel.md, "Nitro").</summary>
    public bool NitroInstalled;
    public bool NitroBoosting;
    public float NitroChargeFrac = 1f;

    /// <summary>The artificial horizon's decoded pitch and roll (radians), from
    /// <see cref="HorizonAngles"/> off the aircraft's own attitude, never the camera's. Computed
    /// here so the 3D panel (<see cref="CockpitGauges"/>) and any future flat draw read the same
    /// two numbers. Zero at spawn, matching an identity attitude.</summary>
    public float HorizonPitchRad;

    /// <inheritdoc cref="HorizonPitchRad"/>
    public float HorizonRollRad;

    /// <summary>The nose heading in degrees, the same value <see cref="CompassTape"/> reads (0 =
    /// north/−Z, 90 = east/+X). Carried here so the screen-space tape and the 3D panel's compass
    /// drum (<see cref="CockpitGauges"/>) turn off one computed number rather than each deriving
    /// it.</summary>
    public float HeadingDeg;

    // ---- tuning ----
    // The hardpoint dial's belt-light ring is 8 positions on every airframe regardless of the
    // loadout's pylon count (user-confirmed against the original; markers.md),
    // never derive it from the bound Hardpoints count. Indicator i is pylon i+1: the belt
    // light and arrow-target math both index by PYLON NUMBER, not by position in a compacted list.
    internal const int HardpointRingSize = 8;
    // The STALL lamp's decoded law. The engine drives both the gate and the blink rate off one
    // scalar s = (1 − n_avail + 1.35) × 0.425, and the half-period is 0.4 − 0.3·s, which bottoms out
    // at 0.100 s and reaches 0.400 s at the gate. ⚠ Do NOT apply k = 1.390 to these: the lamp clock
    // and the flight-model dt are the same variable in the original (docs/formats/hud.md).
    internal const float StallWarnLoadFactor = 2.35f;   // lamp dark at or above this available g
    internal const float StallDriverBias = 1.35f;       // 0x00608338
    internal const float StallDriverScale = 0.425f;     // 0x00608334
    internal const float StallBlinkBaseS = 0.4f;        // 0x00603538
    internal const float StallBlinkPerDriverS = 0.3f;   // 0x006034ac
    // LOW ALT's decoded law: it lights below 60 m ABOVE GROUND and its blink half-period RAMPS with
    // height, 0.14 s on the deck widening to 0.50 s at the gate. Not a fixed period, and not an
    // altitude above sea level.
    internal const float LowAltAglM = 60f;              // 0x006076fc
    internal const float LowAltBlinkBaseS = 0.14f;      // 0x006076f4
    internal const float LowAltBlinkPerMetreS = 0.006f; // 0x006076f8
    private const float DamageBlinkTime = 5f;    // s a hit part blinks (user-observed in the original)
    private const float DamageBlinkPeriod = 0.32f; // s per on/off cycle of the hit part (TUNE)
    // Four color states (user-confirmed in the original: green/yellow/orange/red, the
    // full cockpit.gw cycle) over the data's three *_damage_* injure thresholds, each
    // threshold steps to the NEXT color: green above the "green" anim's 0.72, yellow
    // ≤ 0.72, orange ≤ 0.46, red ≤ 0.20 (red on a still-flying plane matches the
    // reference shot). Defaults when a part carries no such anims:
    private const float DefaultYellowAt = 0.72f, DefaultOrangeAt = 0.46f, DefaultRedAt = 0.20f;

    // Screen metrics measured in OriginalScreenshots/HUD.png (2556×1440) via dark-span
    // scans of the bezel rings, scaled by viewport height like CompassTape. All three
    // dials share one size (R 85); x anchors from the reading box's left edge except the
    // speedometer (from its right, mirroring the altimeter's margin).
    private const float AltRadius = 85f;
    private const float SpdCenterFromRight = 420f, SpdCenterY = 1108.5f, SpdRadius = 85f;
    private const float DmgRadius = 85f;
    // The two weapon gauges (measured off OriginalScreenshots/HUD.png): the ROCKETS dial sits one
    // dial-pitch (190.5 px, the alt→damage spacing) above the altimeter, the GUNS dial the same
    // above the speedometer. Same radius as the other dials.
    private const float MissileRadius = 85f;
    private const float GunCenterFromRight = 420f, GunCenterY = 918f, GunRadius = 85f;
    // The nitro dial: the bottom of the right column, one dial-pitch below the speedometer and the
    // same radius, so it mirrors the damage dial's row on the left (user-confirmed against the
    // original). ⚠ Not above the GUNS dial, the column fills downward from ROCKETS/GUNS.
    private const float NitroCenterFromRight = 420f, NitroCenterY = 1299f, NitroRadius = 85f;

    private static readonly Vector2 AltCenter = new(425.5f, 1108.5f);
    private static readonly Vector2 DmgCenter = new(426.5f, 1299f);
    private static readonly Vector2 MissileCenter = new(425.5f, 918f);

    // flat indicator tints when a colour-variant png is missing from the archive
    private static readonly Color[] IndicatorFlat =
    {
        new(0.2f, 0.9f, 0.2f), new(0.95f, 0.9f, 0.1f), new(0.9f, 0.15f, 0.15f),
    };

    // flat zone tints when a color-variant png is missing from the archive
    private static readonly Color[] ZoneFlat =
    {
        new(0.25f, 0.9f, 0.2f), new(0.95f, 0.9f, 0.1f),
        new(0.95f, 0.55f, 0.05f), new(0.9f, 0.1f, 0.1f),
    };

    private readonly List<GaugePoly> _altFace = new();
    private readonly List<GaugePoly> _altWarn = new();  // lowalt_on (blinks)
    private readonly List<GaugePoly> _spdFace = new();
    private readonly List<GaugePoly> _spdWarn = new();  // stallwarning_on (blinks)
    private readonly List<GaugePoly> _dmgFace = new();
    private readonly List<DamageZone> _zones = new();
    // color-variant textures for the zone swap (0 green / 1 yellow / 2 orange / 3 red)
    private readonly Texture2D?[] _hilite = new Texture2D?[4];
    private readonly Texture2D?[] _hatch = new Texture2D?[4];
    private readonly GaugeGeom _gunGaugeGeom = new();
    private readonly GaugeGeom _missileGaugeGeom = new();
    private readonly List<GaugePoly> _nitroFace = new();
    private readonly List<GaugePoly> _nitroWarn = new();  // no *_on child ships; kept for the shared extractor
    // Glyph atlas for the digit/type cycles: char → texture (zero..nine, A..Z; space/unknown = null).
    private readonly Dictionary<char, Texture2D?> _glyphs = new();
    // Belt-indicator colour variants (0 green / 1 yellow / 2 red), the hilite bar and the light.
    private readonly Texture2D?[] _indHilite = new Texture2D?[3];
    private readonly Texture2D?[] _indLight = new Texture2D?[3];
    // DrawGaugePoly's per-vertex scratch, one pair per vertex count the face uses. DrawPolygon
    // marshals both arrays into packed arrays before it returns, so the next poly of the same
    // size may have them back; the arrays must be exactly as long as the poly, which is why this
    // is keyed by vertex count rather than grown to a high-water mark.
    private readonly Dictionary<int, (Vector2[] Points, Color[] Colors)> _polyScratch = new();

    private GaugePoly? _altHundreds, _altThousands;
    private GaugePoly? _spdNeedle;
    private GaugePoly? _nitroBoostPoly, _nitroChargePoly;
    private NitroNeedle _nitroBoostNeedle = new(NitroBoostNeedleRate);
    private NitroNeedle _nitroChargeNeedle = new(NitroChargeNeedleRate);
    private double _time;
    // Live sweep state of each weapon-gauge pointer and the STALL lamp's blink, plain structs
    // so CSVM.Tests can drive them without a live Control.
    private ArrowSweep _gunArrow = new();
    private ArrowSweep _missileArrow = new();
    private StallLamp _stallLamp = new();
    private LowAltLamp _lowAltLamp = new();

    /// <summary>Whether the STALL overlay is lit this frame. <see cref="CockpitGauges"/> mirrors
    /// this onto the authored <c>stallwarning_on</c> node so the 3D panel and the screen-space dial
    /// blink in step rather than each integrating its own phase.</summary>
    public bool StallLampLit => _stallLamp.Lit;

    /// <inheritdoc cref="StallLampLit"/>
    public bool LowAltLampLit => _lowAltLamp.Lit;

    /// <summary>The nitro needles' live angles (decoded Euler-z, counter-clockwise-positive), for
    /// the same mirroring. The screen-space draw negates them; the 3D drive does not.</summary>
    public float NitroBoostAngleDeg => _nitroBoostNeedle.Angle;

    /// <inheritdoc cref="NitroBoostAngleDeg"/>
    public float NitroChargeAngleDeg => _nitroChargeNeedle.Angle;

    /// <summary>The two weapon-gauge arrows' live sweep angles, clockwise degrees from the belt's
    /// position 0. NaN before the first sweep, which is a gauge with no loadout bound.</summary>
    public float GunArrowAngleDeg => _gunArrow.Angle;

    /// <inheritdoc cref="GunArrowAngleDeg"/>
    public float MissileArrowAngleDeg => _missileArrow.Angle;

    private bool DamagePhaseOn => Mathf.PosMod((float)_time, DamageBlinkPeriod) < DamageBlinkPeriod * 0.5f;


    /// <summary>Builds the cluster from the plane's 'gauges' subtree in planes.zbd and
    /// the chapter texture archive. Null when the subtree or its dial textures are
    /// missing (each miss is logged by the archive).</summary>
    public static GaugeCluster? Build(GameZ planes, string planeName, TextureArchive textures,
        IReadOnlyList<DestroyablePart> parts)
    {
        var root = planes.FindByName(planeName);
        if (root == null)
            return null;
        var gauges = FindDescendant(planes, root, "gauges");
        if (gauges == null)
        {
            Log.Info("flight", $"[gauges] '{planeName}' has no gauges subtree, HUD dials off");
            return null;
        }

        var cluster = new GaugeCluster
        {
            MouseFilter = MouseFilterEnum.Ignore,
            TextureRepeat = TextureRepeatEnum.Enabled, // the hatch fills tile their UVs
            // ⚠ Ahead of the FlightController, which reads these lamps back out the same frame to
            // drive the authored 3D panel (CockpitGauges). Both copies of a lamp must toggle on the
            // same frame, and without an order the 3D one would trail the dial by a frame.
            ProcessPriority = -1,
        };
        cluster.SetAnchorsPreset(LayoutPreset.FullRect);

        foreach (int ci in gauges.Children)
        {
            var dial = planes.Nodes[ci];
            switch (dial.Name.ToLowerInvariant())
            {
                case "altimeter":
                    cluster.ExtractInstrument(planes, textures, dial,
                        cluster._altFace, cluster._altWarn,
                        ("hundreds", p => cluster._altHundreds = p),
                        ("thousands", p => cluster._altThousands = p));
                    break;
                case "speedometer":
                    cluster.ExtractInstrument(planes, textures, dial,
                        cluster._spdFace, cluster._spdWarn,
                        ("speed", p => cluster._spdNeedle = p));
                    break;
                case "damageindicator":
                    cluster.ExtractDamageDial(planes, textures, dial, parts);
                    break;
                case "gungauge":
                    cluster.ExtractWeaponGauge(planes, textures, dial, cluster._gunGaugeGeom, "gg");
                    break;
                case "missilegauge":
                    cluster.ExtractWeaponGauge(planes, textures, dial, cluster._missileGaugeGeom, "mg");
                    break;
                case "nitrogauge":
                    cluster.ExtractNitroDial(planes, textures, dial);
                    break;
            }
        }

        // Load the shared digit/letter atlas + belt-indicator colour variants once, only if a weapon
        // gauge was actually found (all 11 flyable models carry both, but a lab plane may not).
        if (cluster._gunGaugeGeom.HasGeometry || cluster._missileGaugeGeom.HasGeometry)
        {
            cluster.LoadGaugeTextures(textures);
        }

        if (cluster._altFace.Count == 0 && cluster._spdFace.Count == 0 && cluster._dmgFace.Count == 0)
        {
            Log.Info("flight", $"[gauges] '{planeName}': no dial geometry extracted, HUD dials off");
            return null;
        }
        return cluster;
    }

    // green > low > empty, indexing the 3 indicator colour variants. ONE decoded rule serves both
    // gauges (FUN_004547a0 takes rounds and capacity from either a gun record or a pylon one).
    // ⚠ The pylons' "no intermediate colour" reading is not a second rule: a pylon carrying a
    // single round has a fraction of 1 or 0, so its low tier is unreachable rather than absent, and
    // a pylon deep enough to sit at a quarter full does light it. Public for CSVM.Tests.
    public static int GunIndicatorColor(float frac) => frac <= 0f ? 2 : frac <= IndicatorLowFrac ? 1 : 0;

    /// <inheritdoc cref="GunIndicatorColor"/>
    public static int HardpointIndicatorColor(float frac) => GunIndicatorColor(frac);

    // The colour of belt indicator i, including positions past the end of the loadout: an unfitted
    // slot reads RED, the same as a fitted-but-spent one. In the original every belt light on the
    // dial is lit, a plane with fewer guns/pylons than the dial has positions shows the surplus in
    // red, it does not leave them dark. Public so CSVM.Tests can assert that directly.
    public static int SlotIndicatorColor(IReadOnlyList<float> slots, int i, bool isGun) =>
        i >= slots.Count ? 2
        : isGun ? GunIndicatorColor(slots[i]) : HardpointIndicatorColor(slots[i]);

    // Damage zones: green > yellow > orange > red over the zone's combined armor+health fraction,
    // thresholds mined per-part from the data's own injure_anims, see docs/formats/hud.md
    // "Thresholds". ⚠ Do not re-shape this as a per-pool ring split; that reading is refuted.
    // Crosses to the next colour at or below threshold. Public for CSVM.Tests.
    public static int DamageZoneColor(float frac, float yellowAt, float orangeAt, float redAt) =>
        frac > yellowAt ? 0 : frac > orangeAt ? 1 : frac > redAt ? 2 : 3;

    // Public so CSVM.Tests (GaugeArrowTweenTests) can assert the sweep math directly.
    public static float TargetArrowAngle(int positions, int selected) =>
        positions > 0 ? -(360f / positions) * selected : 0f;

    /// <summary>Advances a weapon-gauge arrow angle at most <see cref="ArrowSweepDegPerSimS"/> ×
    /// simDt toward target, routed the shortest way round. NaN snaps instead of sweeping
    /// in from an undefined pose (gauge just appeared, or a respawn cleared it via Reset). Public
    /// so CSVM.Tests (GaugeArrowTweenTests) can assert the sweep directly.
    /// ⚠ Only the arrow sweeps, the readout digits/type name still snap on the sweep's first
    /// frame. Do not tween those too.</summary>
    public static float TweenArrow(float current, float target, float simDt)
    {
        if (float.IsNaN(current))
            return target;
        float delta = Mathf.PosMod(target - current + 180f, 360f) - 180f;
        float maxStep = ArrowSweepDegPerSimS * simDt;
        return Mathf.Abs(delta) <= maxStep ? target : current + Mathf.Sign(delta) * maxStep;
    }

    /// <summary>The x each dial column measures its offsets from: the reading box's own edges on a
    /// pane at the reference aspect or wider, pushed outward on a narrower one so each column
    /// keeps only <see cref="HudMetrics.MinimumColumnMargin"/> of border rather than a 16:9
    /// frame's margin. Public so CSVM.Tests can assert the placement without a live
    /// Control.</summary>
    public static (float Left, float Right) ColumnAnchors(Rect2 box, float scale) => (
        box.Position.X - (HudMetrics.ColumnOutdent(box.Size, LeftColumnMargin) * scale),
        box.End.X + (HudMetrics.ColumnOutdent(box.Size, RightColumnMargin) * scale));

    /// <summary>The long altimeter needle's angle, clockwise degrees from the top. Wraps every
    /// 1,000 ft; the short needle wraps every 10,000.</summary>
    public static float AltHundredsAngleDeg(float altitudeFt) =>
        Mathf.Max(0f, altitudeFt) % 1000f * AltHundredsDegPerFt;

    /// <inheritdoc cref="AltHundredsAngleDeg"/>
    public static float AltThousandsAngleDeg(float altitudeFt) =>
        Mathf.Max(0f, altitudeFt) % 10000f * AltThousandsDegPerFt;

    /// <summary>The speedometer needle's angle, clockwise degrees from the top. One revolution per
    /// 500 mph, and the engine puts no wrap or clamp on it beyond the speed itself.</summary>
    public static float SpeedAngleDeg(float speedMph) =>
        Mathf.Max(0f, speedMph) * SpeedDegPerMph;

    /// <summary>The artificial horizon's pitch and roll, decoded off the aircraft's own attitude
    /// (FUN_0053df30; docs/formats/hud.md, "Cockpit gauges"). No axis remap: the original's
    /// row-major body axes are exactly <paramref name="attitude"/>'s columns X/Y/Z. Heading is
    /// discarded, as the original discards it here. Public for CSVM.Tests.</summary>
    public static (float PitchRad, float RollRad) HorizonAngles(Basis attitude)
    {
        float m7 = attitude.Z.Y;
        if (Mathf.Abs(m7) >= 1f)
        {
            return (-Mathf.Sign(m7) * Mathf.Pi / 2f, 0f);
        }
        return (Mathf.Asin(-m7), Mathf.Atan2(attitude.X.Y, attitude.Y.Y));
    }

    /// <summary>The engine's single STALL scalar: positive exactly where the lamp shows, and the
    /// term its blink rate is built from. Public so CSVM.Tests can assert the gate and the rate
    /// against one another rather than against two independent numbers.</summary>
    public static float StallDriver(float availableLoadFactor) =>
        (StallWarnLoadFactor - availableLoadFactor) * StallDriverScale;

    /// <summary>Half of the STALL lamp's blink period at <paramref name="availableLoadFactor"/> g
    /// of available lift. Duty is 0.50, so the full period is twice this. Bounded to
    /// (0.100, 0.400] s by construction, since the driver lies in (0, 1] wherever the lamp is
    /// lit at all.</summary>
    public static float StallBlinkHalfPeriodS(float availableLoadFactor) =>
        StallBlinkBaseS - (StallBlinkPerDriverS * StallDriver(availableLoadFactor));

    /// <summary>Half of the LOW ALT lamp's blink period at <paramref name="aglMetres"/> above
    /// ground: 0.14 s on the deck, 0.50 s at the 60 m gate. Recomputed at each toggle, so the ramp
    /// tracks the aircraft rather than being latched at onset.</summary>
    public static float LowAltBlinkHalfPeriodS(float aglMetres) =>
        LowAltBlinkBaseS + (LowAltBlinkPerMetreS * Mathf.Max(aglMetres, 0f));

    /// <summary>This frame's colour tier for one belt position, and the two colour-variant
    /// textures a tier picks. <see cref="CockpitGauges"/> pushes the same choice onto the authored
    /// 3D indicator's material, which is how one decode drives both instrument sets.</summary>
    public int BeltTier(bool isGun, int position)
    {
        var state = isGun ? GunGauge : MissileGauge;
        return state == null ? 0 : SlotIndicatorColor(state.Slots, position, isGun);
    }

    /// <inheritdoc cref="BeltTier"/>
    public Texture2D? BeltLightTexture(int tier) => _indLight[Mathf.Clamp(tier, 0, 2)];

    /// <inheritdoc cref="BeltTier"/>
    public Texture2D? BeltHiliteTexture(int tier) => _indHilite[Mathf.Clamp(tier, 0, 2)];

    /// <summary>This frame's colour tier for one damage zone, by the part name the zone carries.
    /// Negative means the zone is in the dark half of its post-hit blink, which the screen-space
    /// dial draws by skipping the zone outright.</summary>
    public int ZoneTier(string part)
    {
        foreach (var z in _zones)
        {
            if (!z.Part.Equals(part, StringComparison.OrdinalIgnoreCase))
                continue;
            if (z.BlinkLeft > 0f && !DamagePhaseOn)
                return -1;
            return DamageZoneColor(PartFraction?.Invoke(z.Part) ?? 1f, z.YellowAt, z.OrangeAt, z.RedAt);
        }
        return 0;
    }

    /// <inheritdoc cref="ZoneTier"/>
    public Texture2D? ZoneHiliteTexture(int tier) => _hilite[Mathf.Clamp(tier, 0, 3)];

    /// <inheritdoc cref="ZoneTier"/>
    public Texture2D? ZoneHatchTexture(int tier) => _hatch[Mathf.Clamp(tier, 0, 3)];

    /// <summary>The glyph atlas entry for one character, or null for a blank cell. The authored
    /// readouts cycle their per-character texture exactly this way.</summary>
    public Texture2D? Glyph(char c) => _glyphs.TryGetValue(c, out var g) ? g : null;

    /// <summary>The two readouts' text for this frame, already padded to the cell count the
    /// authored quads provide. Empty when no loadout feeds that gauge.</summary>
    public string BeltCountText(bool isGun, int cells)
    {
        var state = isGun ? GunGauge : MissileGauge;
        return state == null ? string.Empty : FormatCount(state.Count, cells);
    }

    /// <inheritdoc cref="BeltCountText"/>
    public string BeltTypeText(bool isGun, int cells)
    {
        var state = isGun ? GunGauge : MissileGauge;
        return state == null ? string.Empty : FormatType(state.Type, cells);
    }

    /// <summary>Restarts the warning/blink state (respawn).</summary>
    public void Reset()
    {
        foreach (var z in _zones)
            z.BlinkLeft = 0f;
        _gunArrow.Reset();
        _missileArrow.Reset();
        _stallLamp.Set();
        _lowAltLamp.Set();
    }

    /// <summary>A part took damage: its fill + border blink for the next few seconds
    /// (the original blinks even inside the green range, user-verified).</summary>
    public void OnPartDamage(string partName)
    {
        foreach (var z in _zones)
            if (z.Part.Equals(partName, StringComparison.OrdinalIgnoreCase))
                z.BlinkLeft = DamageBlinkTime;
    }

    public override void _Process(double delta)
    {
        _time += delta;
        // ⚠ Both animated cues advance on SIM dt, never the raw frame delta: their rates are
        // video-decoded in sim seconds, and the wall figures would run them 39% fast.
        float simDt = GameClock.Current?.FrameDt ?? (float)delta;
        if (GunGauge is { } gg)
            _gunArrow.Advance(TargetArrowAngle(_gunGaugeGeom.Positions, gg.Selected), simDt);
        if (MissileGauge is { } mg)
            _missileArrow.Advance(TargetArrowAngle(_missileGaugeGeom.Positions, mg.Selected), simDt);
        _stallLamp.Advance(StallWarning, AvailableLoadFactor, SpeedMph, simDt);
        _lowAltLamp.Advance(AglMeters < LowAltAglM, AglMeters, simDt);
        _nitroBoostNeedle.Advance(NitroBoosting ? -NitroNeedleSweepDeg : 0f, simDt);
        _nitroChargeNeedle.Advance((1f - Mathf.Clamp(NitroChargeFrac, 0f, 1f)) * NitroNeedleSweepDeg, simDt);
        foreach (var z in _zones)
            z.BlinkLeft = Mathf.Max(0f, z.BlinkLeft - (float)delta);
        QueueRedraw();
    }

    public override void _Draw()
    {
        // Every x here anchors to a column's own edge, not the pane's: on a wider pane the two
        // columns would otherwise stand a screen apart, and on a narrower one they would hold a
        // 16:9 frame's margin in from a border that is nearer than that (ColumnAnchors).
        var box = HudMetrics.ReadingBox(this);
        float s = HudMetrics.Scale(this);
        (float left, float right) = ColumnAnchors(box, s);
        float bottom = box.End.Y;

        // altimeter: long needle 360°/1,000 ft, short 360°/10,000 ft, 0 at the top
        var altC = new Vector2(left + (AltCenter.X * s), FromBottom(AltCenter.Y, s, bottom));
        float altR = AltRadius * s;
        foreach (var p in _altFace)
            DrawGaugePoly(p, altC, altR);
        if (_lowAltLamp.Lit)
            foreach (var p in _altWarn)
                DrawGaugePoly(p, altC, altR);
        if (_altThousands != null)
            DrawGaugePoly(_altThousands, altC, altR, AltThousandsAngleDeg(AltitudeFt));
        if (_altHundreds != null)
            DrawGaugePoly(_altHundreds, altC, altR, AltHundredsAngleDeg(AltitudeFt));

        // speedometer: 0.7199957°/mph, decoded (500 mph per revolution)
        var spdC = new Vector2(right - (SpdCenterFromRight * s), FromBottom(SpdCenterY, s, bottom));
        float spdR = SpdRadius * s;
        foreach (var p in _spdFace)
            DrawGaugePoly(p, spdC, spdR);
        if (_stallLamp.Lit)
            foreach (var p in _spdWarn)
                DrawGaugePoly(p, spdC, spdR);
        if (_spdNeedle != null)
            DrawGaugePoly(_spdNeedle, spdC, spdR, SpeedAngleDeg(SpeedMph));

        // damage display: face silhouette, then each zone's border bar + hatch fill
        // in its color; a freshly hit zone blinks (fill + border) for a few seconds
        var dmgC = new Vector2(left + (DmgCenter.X * s), FromBottom(DmgCenter.Y, s, bottom));
        float dmgR = DmgRadius * s;
        foreach (var p in _dmgFace)
            DrawGaugePoly(p, dmgC, dmgR);
        foreach (var z in _zones)
        {
            if (z.BlinkLeft > 0f && !DamagePhaseOn)
                continue; // blink-off phase hides the whole zone (fill + outline)
            float frac = PartFraction?.Invoke(z.Part) ?? 1f;
            int color = DamageZoneColor(frac, z.YellowAt, z.OrangeAt, z.RedAt);
            foreach (var p in z.Border)
                DrawGaugePoly(p, dmgC, dmgR, 0f, _hilite[color], ZoneFlat[color]);
            foreach (var p in z.Fill)
                DrawGaugePoly(p, dmgC, dmgR, 0f, _hatch[color], ZoneFlat[color]);
        }

        // The two weapon gauges: the ROCKETS dial above the altimeter, the GUNS dial above the
        // speedometer (mirrored from the right edge). Each renders only when the FlightController is
        // feeding it, hidden in the static viewer, which carries no loadout.
        if (MissileGauge is { } mg && _missileGaugeGeom.HasGeometry)
        {
            var c = new Vector2(left + (MissileCenter.X * s), FromBottom(MissileCenter.Y, s, bottom));
            DrawWeaponGauge(_missileGaugeGeom, mg, c, MissileRadius * s, isGun: false, _missileArrow.Angle);
        }
        if (GunGauge is { } gg && _gunGaugeGeom.HasGeometry)
        {
            var c = new Vector2(right - (GunCenterFromRight * s), FromBottom(GunCenterY, s, bottom));
            DrawWeaponGauge(_gunGaugeGeom, gg, c, GunRadius * s, isGun: true, _gunArrow.Angle);
        }

        // The nitro dial, only with the injector installed (the original hides the node otherwise).
        if (NitroInstalled && _nitroFace.Count > 0)
        {
            var c = new Vector2(right - (NitroCenterFromRight * s), FromBottom(NitroCenterY, s, bottom));
            float r = NitroRadius * s;
            foreach (var p in _nitroFace)
                DrawGaugePoly(p, c, r);
            // ⚠ Negated: the needle state holds the decoded Euler-z angle, which is
            // counter-clockwise-positive, while rotDeg here is clockwise-positive.
            if (_nitroChargePoly != null)
                DrawGaugePoly(_nitroChargePoly, c, r, -_nitroChargeNeedle.Angle);
            if (_nitroBoostPoly != null)
                DrawGaugePoly(_nitroBoostPoly, c, r, -_nitroBoostNeedle.Angle);
        }
    }

    // ---- extraction ----

    private static GameZNode? FindDescendant(GameZ gz, GameZNode from, string name)
    {
        var queue = new Queue<GameZNode>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (n.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return n;
            foreach (int c in n.Children)
                queue.Enqueue(gz.Nodes[c]);
        }
        return null;
    }

    // All flat polys of a node's mesh in dial-local coords. Node Local
    // transforms are ignored on purpose: needles carry an arbitrary modeled rest
    // rotation (we set the angle from the value), everything else is identity.
    private static List<GaugePoly> MeshPolys(GameZ gz, TextureArchive textures, GameZNode node)
    {
        var result = new List<GaugePoly>();
        if (node.MeshIndex < 0 || node.MeshIndex >= gz.Meshes.Count)
            return result;
        var mesh = gz.Meshes[node.MeshIndex];
        foreach (var poly in mesh.Polygons)
        {
            if (poly.VertexIndices.Count < 3)
                continue;
            var pts = new Vector2[poly.VertexIndices.Count];
            for (int i = 0; i < pts.Length; i++)
            {
                var v = mesh.Vertices[poly.VertexIndices[i]];
                pts[i] = new Vector2(v.X, v.Y);
            }
            var uvs = new Vector2[pts.Length];
            if (poly.UvCoords != null && poly.UvCoords.Count == pts.Length)
                for (int i = 0; i < pts.Length; i++)
                    uvs[i] = poly.UvCoords[i];
            string texName = poly.MaterialIndex >= 0 && poly.MaterialIndex < gz.Materials.Count
                ? gz.Materials[poly.MaterialIndex].TextureName ?? "" : "";
            result.Add(new GaugePoly
            {
                Points = pts,
                Uvs = uvs,
                Tex = texName.Length > 0 ? textures.Find(texName) : null,
                Priority = poly.Priority,
                TexName = texName,
            });
        }
        return result;
    }

    private static float CenterX(GaugePoly p)
    {
        float sum = 0f;
        foreach (var pt in p.Points)
        {
            sum += pt.X;
        }
        return p.Points.Length > 0 ? sum / p.Points.Length : 0f;
    }

    // The trailing integer of a name like "ggindicator3" (→ 3); 0 if it ends in no digits.
    private static int TrailingInt(string name)
    {
        int i = name.Length;
        while (i > 0 && char.IsDigit(name[i - 1]))
        {
            i--;
        }
        return i < name.Length && int.TryParse(name[i..], out var n) ? n : 0;
    }

    // The dials are measured off HUD.png as absolute 1440p-reference y coordinates, but
    // they are really anchored to the BOTTOM of the screen (they sit 331 / 141 px up from it).
    // Measuring from the bottom is identical to `refY · s` whenever s is the plain
    // height ratio (single player), and is what keeps them on screen when a splitscreen pane
    // draws them at a damped, larger-than-proportional scale (see HudMetrics).
    private static float FromBottom(float refY, float s, float viewportH) =>
        viewportH - (HudMetrics.ReferenceHeight - refY) * s;

    // A count right-aligned into `width` digit cells, space-padded
    // (clamped 0..9999, the 4-cell readout's range).
    private static string FormatCount(int count, int width)
    {
        string s = Mathf.Clamp(count, 0, 9999).ToString();
        if (s.Length > width)
            s = s[^width..];
        return s.PadLeft(width);
    }

    // A weapon name upper-cased, left-aligned and padded/truncated to the type readout's
    // `width` cells (the atlas is uppercase-only).
    private static string FormatType(string type, int width)
    {
        string s = type.ToUpperInvariant();
        if (s.Length > width)
            s = s[..width];
        return s.PadRight(width);
    }

    // An altimeter/speedometer node: face polys from any unnamed child mesh
    // (g784 …), warning overlays from *_on children, needles by exact child name.
    // ⚠ The nitro dial is the one instrument NOT authored in normalized dial coords, never draw
    // it under the shared radius-1 rule. Its bezel centre and radius are read from the tree here
    // instead of assumed (docs/formats/hud.md, "Cockpit gauges").
    private void ExtractNitroDial(GameZ gz, TextureArchive textures, GameZNode dial)
    {
        ExtractInstrument(gz, textures, dial, _nitroFace, _nitroWarn,
            ("nitro_boost", p => _nitroBoostPoly = p),
            ("nitro_charge", p => _nitroChargePoly = p));

        var centre = Vector2.Zero;
        foreach (int ci in dial.Children)
        {
            var child = gz.Nodes[ci];
            if (child.Name.Equals("nitro_boost", StringComparison.OrdinalIgnoreCase)
                && child.Local is { } local)
            {
                centre = new Vector2(local.Origin.X, local.Origin.Y);
                break;
            }
        }

        float radius = 0f;
        foreach (var p in _nitroFace)
            foreach (var pt in p.Points)
                radius = Mathf.Max(radius, Mathf.Abs(pt.X - centre.X));
        if (radius <= 0f)
            return;

        foreach (var p in _nitroFace)
            for (int i = 0; i < p.Points.Length; i++)
                p.Points[i] = (p.Points[i] - centre) / radius;
        // The needles already sit about their own pivot, so they take the scale alone.
        foreach (var needle in new[] { _nitroBoostPoly, _nitroChargePoly })
            if (needle != null)
                for (int i = 0; i < needle.Points.Length; i++)
                    needle.Points[i] /= radius;
    }

    private void ExtractInstrument(GameZ gz, TextureArchive textures, GameZNode dial,
        List<GaugePoly> face, List<GaugePoly> warn,
        params (string Name, Action<GaugePoly> Set)[] needles)
    {
        foreach (int ci in dial.Children)
        {
            var child = gz.Nodes[ci];
            bool isNeedle = false;
            foreach (var (name, set) in needles)
                if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    var polys = MeshPolys(gz, textures, child);
                    if (polys.Count > 0)
                        set(polys[0]); // a needle is a single quad
                    isNeedle = true;
                    break;
                }
            if (isNeedle)
                continue;
            var target = child.Name.EndsWith("_on", StringComparison.OrdinalIgnoreCase) ? warn : face;
            target.AddRange(MeshPolys(gz, textures, child));
        }
        face.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    // The damage dial: each *damage child is one zone (border bar + hatch fill); everything else
    // is the silhouette face. Thresholds come from the matching part's injure anims.
    // ⚠ Face parenting differs per plane, see docs/formats/hud.md. Any non-zone child counts as
    // face, the same rule ExtractInstrument uses for the other two dials.
    private void ExtractDamageDial(GameZ gz, TextureArchive textures, GameZNode dial,
        IReadOnlyList<DestroyablePart> parts)
    {
        _dmgFace.AddRange(MeshPolys(gz, textures, dial));

        foreach (int ci in dial.Children)
        {
            var child = gz.Nodes[ci];
            if (!child.Name.EndsWith("damage", StringComparison.OrdinalIgnoreCase))
            {
                _dmgFace.AddRange(MeshPolys(gz, textures, child));
                continue;
            }
            var zone = new DamageZone { Part = child.Name[..^"damage".Length] };
            foreach (var p in MeshPolys(gz, textures, child))
                (p.TexName.Contains("hilite", StringComparison.OrdinalIgnoreCase)
                    ? zone.Border : zone.Fill).Add(p);
            foreach (var part in parts)
                if (part.Name.Equals(zone.Part, StringComparison.OrdinalIgnoreCase))
                {
                    // each anim threshold steps to the NEXT color (see the constants)
                    foreach (var (frac, anim) in part.InjureAnims)
                    {
                        if (anim.EndsWith("_damage_green", StringComparison.OrdinalIgnoreCase))
                            zone.YellowAt = frac;
                        else if (anim.EndsWith("_damage_yellow", StringComparison.OrdinalIgnoreCase))
                            zone.OrangeAt = frac;
                        else if (anim.EndsWith("_damage_red", StringComparison.OrdinalIgnoreCase))
                            zone.RedAt = frac;
                    }
                    break;
                }
            _zones.Add(zone);
        }
        _dmgFace.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        // the four swap variants the cockpit.gw texture cycle names
        string[] hilite = { "greenhilite", "yellowhilite", "orangehilite", "redhilite" };
        string[] hatch = { "grn_hatchptrn", "yel_hatchptrn", "orng_hatchptrn", "red_hatchptrn" };
        for (int i = 0; i < 4; i++)
        {
            _hilite[i] = textures.Find(hilite[i]);
            _hatch[i] = textures.Find(hatch[i]);
        }
    }

    // A weapon gauge (gungauge/missilegauge): identical layout on all 11 planes, see
    // docs/formats/hud.md. Named children are the ammo/type readouts, belt lights and pointer;
    // anything else is dial face, the same rule the damage dial uses. Extracts geometry and belt
    // order only; the glyph/colour cycles are driven live by FlightController via WeaponGauge.
    private void ExtractWeaponGauge(GameZ gz, TextureArchive textures, GameZNode dial, GaugeGeom geom, string prefix)
    {
        geom.Face.AddRange(MeshPolys(gz, textures, dial)); // -1 on every plane, but follows the face rule
        var byIndex = new SortedDictionary<int, List<GaugePoly>>();
        foreach (int ci in dial.Children)
        {
            var child = gz.Nodes[ci];
            string name = child.Name.ToLowerInvariant();
            if (name == "4char_ammo")
            {
                geom.Digits.AddRange(MeshPolys(gz, textures, child));
            }
            else if (name == "6char_type")
            {
                geom.TypeChars.AddRange(MeshPolys(gz, textures, child));
            }
            else if (name == prefix + "arrow")
            {
                var polys = MeshPolys(gz, textures, child);
                if (polys.Count > 0)
                {
                    geom.Arrow = polys[0]; // a single arrow-shaped polygon (5 verts, tip +y)
                }
            }
            else if (name.StartsWith(prefix + "indicator", StringComparison.Ordinal))
            {
                byIndex[TrailingInt(name)] = MeshPolys(gz, textures, child);
            }
            else
            {
                geom.Face.AddRange(MeshPolys(gz, textures, child)); // the generic face child
            }
        }
        geom.Digits.Sort((a, b) => CenterX(a).CompareTo(CenterX(b)));
        geom.TypeChars.Sort((a, b) => CenterX(a).CompareTo(CenterX(b)));
        int max = -1;
        foreach (int k in byIndex.Keys)
        {
            max = Math.Max(max, k);
        }
        for (int i = 0; i <= max; i++)
        {
            geom.Indicators.Add(byIndex.TryGetValue(i, out var polys) ? polys : new List<GaugePoly>());
        }
        geom.Positions = geom.Indicators.Count; // 4 (gun) or 8 (missile), the arrow's step angle
        geom.Face.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    // Loads the shared digit/letter atlas (the `zero.tif`…`nine.tif` + `A.tif`…
    // `Z.tif` cycle both readouts index) and the belt-indicator colour variants
    // (green/yellow/red). Space and unrepresented chars stay absent → drawn as a blank cell.
    private void LoadGaugeTextures(TextureArchive textures)
    {
        string[] digits = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };
        for (int d = 0; d < 10; d++)
        {
            _glyphs[(char)('0' + d)] = textures.Find(digits[d]);
        }
        for (char c = 'A'; c <= 'Z'; c++)
        {
            _glyphs[c] = textures.Find(c.ToString());
        }
        // ⚠ The blank is a real glyph, not the absence of one. The screen-space draw can leave a
        // cell unpainted, but the authored 3D cell already carries a character and has to be
        // overwritten with this to clear it.
        _glyphs[' '] = textures.Find("space");
        string[] hilite = { "greenhilite", "yellowhilite", "redhilite" };
        string[] light = { "greenindicator", "yellowindicator", "redindicator" };
        for (int i = 0; i < 3; i++)
        {
            _indHilite[i] = textures.Find(hilite[i]);
            _indLight[i] = textures.Find(light[i]);
        }
    }

    // Draws one weapon gauge: the labelled face, every belt light, guns step
    // green/yellow/red by remaining fraction, hardpoints step green/red with no intermediate
    // colour, and a position this airframe does not fit at all reads red like a spent one, the
    // right-aligned digit count, the left-aligned type name, then the pointer at its animated
    // sweep angle (tweened toward the selected belt slot in _Process; the readout
    // above still snaps).
    private void DrawWeaponGauge(GaugeGeom geom, WeaponGauge state, Vector2 center, float radius, bool isGun,
        float arrowAngle)
    {
        foreach (var p in geom.Face)
            DrawGaugePoly(p, center, radius);
        for (int i = 0; i < geom.Indicators.Count; i++)
        {
            int color = SlotIndicatorColor(state.Slots, i, isGun);
            foreach (var p in geom.Indicators[i])
            {
                bool bar = p.TexName.Contains("hilite", StringComparison.OrdinalIgnoreCase);
                DrawGaugePoly(p, center, radius, 0f, bar ? _indHilite[color] : _indLight[color], IndicatorFlat[color]);
            }
        }
        DrawGlyphs(geom.Digits, FormatCount(state.Count, geom.Digits.Count), center, radius);
        DrawGlyphs(geom.TypeChars, FormatType(state.Type, geom.TypeChars.Count), center, radius);
        if (geom.Arrow != null && geom.Positions > 0)
            DrawGaugePoly(geom.Arrow, center, radius, arrowAngle);
    }

    // Draws each fixed glyph quad with the atlas texture for its character; a space or an
    // unrepresented char leaves that cell blank (the atlas has no space glyph, that IS the space).
    private void DrawGlyphs(List<GaugePoly> quads, string text, Vector2 center, float radius)
    {
        for (int i = 0; i < quads.Count && i < text.Length; i++)
        {
            // ⚠ The space glyph is deliberately skipped HERE and drawn in the 3D panel. A cell this
            // draw leaves alone shows the face beneath it, which is already blank; the authored 3D
            // cell carries a character instead and has to be painted over.
            if (text[i] != ' ' && _glyphs.TryGetValue(text[i], out var g) && g != null)
                DrawGaugePoly(quads[i], center, radius, 0f, g);
        }
    }

    // Draws one extracted poly at a dial's screen center/radius, rotated
    // clockwise by rotDeg about the dial center (needles). Dial-local y-up flips to
    // screen y-down; an override texture substitutes the zone color variants (with
    // a flat tint standing in when the variant png is missing).
    private void DrawGaugePoly(GaugePoly p, Vector2 center, float radius, float rotDeg = 0f,
        Texture2D? overrideTex = null, Color? missingTint = null)
    {
        var tex = overrideTex ?? p.Tex;
        int n = p.Points.Length;
        if (!_polyScratch.TryGetValue(n, out var scratch))
        {
            scratch = (new Vector2[n], new Color[n]);
            _polyScratch[n] = scratch;
        }
        var pts = scratch.Points;
        float rad = Mathf.DegToRad(rotDeg);
        float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
        for (int i = 0; i < n; i++)
        {
            var v = p.Points[i];
            // clockwise-on-screen rotation in x-right/y-up dial coords
            float x = v.X * cos + v.Y * sin;
            float y = -v.X * sin + v.Y * cos;
            pts[i] = new Vector2(center.X + x * radius, center.Y - y * radius);
        }
        var colors = scratch.Colors;
        var flat = tex != null ? Colors.White
            : missingTint ?? new Color(0.85f, 0.85f, 0.8f);
        for (int i = 0; i < n; i++)
            colors[i] = flat;
        if (tex != null)
            DrawPolygon(pts, colors, p.Uvs, tex);
        else
            DrawPolygon(pts, colors);
    }

    /// <summary>One weapon-gauge arrow's live sweep angle (degrees, same convention as
    /// <see cref="DrawGaugePoly"/>'s rotDeg). <see cref="Angle"/> starts NaN, "not yet drawn", so
    /// the first <see cref="Advance"/> snaps to target instead of sweeping in from zero. A plain
    /// struct outside GaugeCluster's private fields so CSVM.Tests
    /// (<c>GaugeArrowTweenTests</c>) can drive it without constructing a live Control.</summary>
    public struct ArrowSweep
    {
        public float Angle = float.NaN;

        public ArrowSweep()
        {
        }

        /// <summary>Advances toward <paramref name="target"/> at <see cref="ArrowSweepDegPerSimS"/>,
        /// the shortest way round.</summary>
        public void Advance(float target, float simDt) => Angle = TweenArrow(Angle, target, simDt);

        /// <summary>Clears to NaN so the next <see cref="Advance"/> snaps instead of sweeping in
        /// (gauge just appeared, or a respawn).</summary>
        public void Reset() => Angle = float.NaN;
    }

    /// <summary>One nitro needle's angle (degrees), chasing its target through the original's
    /// shared exponential smoother at a fixed rate. A plain struct so CSVM.Tests can drive it.</summary>
    public struct NitroNeedle
    {
        private readonly float _rate;

        public NitroNeedle(float rate)
        {
            _rate = rate;
            Angle = 0f;
        }

        public float Angle { get; private set; }

        public void Advance(float target, float simDt) =>
            Angle = target + (Angle - target) * Mathf.Exp(-_rate * simDt);
    }

    /// <summary>The STALL lamp's blink: binary brightness, duty 0.50, integrated on
    /// its own sim clock rather than read off a wall clock, a rate change mid-dwell shortens the
    /// remainder rather than jumping the lamp. A plain struct outside GaugeCluster's private fields
    /// so CSVM.Tests (<c>StallWarningTests</c>) can drive it without
    /// constructing a live Control.</summary>
    public struct StallLamp
    {
        // The fraction of the current half-period elapsed, see the class remark on why this is
        // integrated rather than PosMod'd over accumulated time.
        private double _phase;
        private float _dwellS;   // sim time the current dwell has run, for the toggle log
        private bool _lampOn;
        private bool _warnPrev;  // last-seen warning flag, so the crossing logs once

        public StallLamp() => Set();

        /// <summary>Whether the lit STALL overlay should draw this frame.</summary>
        public bool Lit { get; private set; }

        /// <summary>(Re)arms the lamp lit and clears the blink phase, construction and a respawn
        /// (<see cref="GaugeCluster.Reset"/>) both start here, so a respawn snaps.</summary>
        public void Set()
        {
            _phase = 0.0;
            _dwellS = 0f;
            _lampOn = true;
            _warnPrev = false;
            Lit = false;
        }

        /// <summary>Integrates one sim step. The lamp lights the instant <paramref name="warning"/>
        /// does and its phase restarts when it clears, the original shows no hysteresis at the
        /// threshold. <paramref name="nAvail"/> is the available load factor (only read while
        /// warned); <paramref name="mph"/> is for the crossing log only.</summary>
        public void Advance(bool warning, float nAvail, float mph, float simDt)
        {
            if (warning != _warnPrev)
            {
                // The threshold crossing itself, with the load factor it happened at, the other
                // half of what a scripted run needs to check the cue against.
                Log.Debug("flight", $"stall warning {(warning ? "on" : "off")} n_avail={nAvail:0.000} mph={mph:0.0}");
                _warnPrev = warning;
            }
            if (!warning)
            {
                _phase = 0.0;
                _dwellS = 0f;
                _lampOn = true;
                Lit = false;
                return;
            }
            _dwellS += simDt;
            _phase += simDt / StallBlinkHalfPeriodS(nAvail);
            while (_phase >= 1.0)
            {
                _phase -= 1.0;
                _lampOn = !_lampOn;
                // The dwell that just ended, in sim ms, the one number the blink law is expressed
                // in, so a run can be checked without eyes on the lamp. Carries its own times as
                // values: the log has no timestamp column by design.
                Log.Debug("flight", $"stall lamp {(_lampOn ? "lit" : "dark")} dwell_ms={_dwellS * 1000f:0} n_avail={nAvail:0.000} half_ms={StallBlinkHalfPeriodS(nAvail) * 1000f:0}");
                _dwellS = 0f;
            }
            Lit = _lampOn;
        }
    }

    /// <summary>The LOW ALT lamp's blink. Same binary brightness and 0.50 duty as the STALL lamp,
    /// but its half-period ramps with height above ground rather than with a stall margin, and it
    /// carries the original's turn-off tail: climbing back through the gate does not snap the lamp
    /// off mid-dwell, it finishes the half-period it is in. A plain struct so CSVM.Tests can drive
    /// it without constructing a live Control.</summary>
    public struct LowAltLamp
    {
        private double _phase;
        private bool _lampOn;

        public LowAltLamp() => Set();

        /// <summary>Whether the lit LOW ALT overlay should draw this frame.</summary>
        public bool Lit { get; private set; }

        /// <summary>Clears the lamp dark and resets the blink phase (construction, respawn).</summary>
        public void Set()
        {
            _phase = 0.0;
            _lampOn = false;
            Lit = false;
        }

        /// <summary>Integrates one sim step. <paramref name="aglMetres"/> sets the rate and is read
        /// every step, so a descent tightens the blink as it happens.</summary>
        public void Advance(bool belowGate, float aglMetres, float simDt)
        {
            if (!belowGate && !Lit)
            {
                // Already dark and out of the band: nothing pending, so stay put.
                _phase = 0.0;
                _lampOn = false;
                return;
            }
            if (belowGate && !_lampOn && _phase <= 0.0)
            {
                _lampOn = true;  // entering the band lights it immediately
                Lit = true;
                return;
            }
            _phase += simDt / LowAltBlinkHalfPeriodS(aglMetres);
            while (_phase >= 1.0)
            {
                _phase -= 1.0;
                _lampOn = !_lampOn;
                // ⚠ Out of the band the lamp goes dark at the END of the dwell it is serving and
                // stays there: the original tests "lit AND expired", so it never re-lights.
                if (!belowGate)
                {
                    _phase = 0.0;
                    _lampOn = false;
                    Lit = false;
                    return;
                }
            }
            Lit = _lampOn;
        }
    }

    /// <summary>Live state for one weapon gauge (gun or rocket), pushed by the FlightController each
    /// frame. Positions map 1:1 onto the gauge's belt indicators: <see cref="Slots"/>[i] drives
    /// <c>ggindicator</c>/<c>mgindicator</c> i (green &gt; low &gt; empty); an indicator past the end
    /// of <see cref="Slots"/> is a position the airframe does not fit and reads red. The arrow points at
    /// <see cref="Selected"/>, <see cref="Count"/> fills the 4-digit readout and <see cref="Type"/>
    /// the 6-char name. Left null (the default) hides that gauge, the static viewer has no loadout.</summary>
    public sealed class WeaponGauge
    {
        public int Count;                                       // rounds shown in 4char_ammo (0..9999)
        public string Type = "";                                // 6char_type name (weapon NAME, upper-cased)
        public int Selected;                                    // 0-based belt position the arrow points at
        public IReadOnlyList<float> Slots = Array.Empty<float>(); // per-indicator ammo fraction 0..1
    }

    // One flat gauge polygon extracted from the mesh: dial-local points
    // (x right, y up, radius 1), normalized UVs, source texture, draw priority.
    private sealed class GaugePoly
    {
        public Vector2[] Points = Array.Empty<Vector2>();
        public Vector2[] Uvs = Array.Empty<Vector2>();
        public Texture2D? Tex;
        public int Priority;
        public string TexName = "";
    }

    private sealed class DamageZone
    {
        public string Part = "";               // "nose" / "tail" / "leftwing" / "rightwing"
        public List<GaugePoly> Border = new(); // the bezel-edge bar ("hilite")
        public List<GaugePoly> Fill = new();   // the part-shaped hatch overlay
        public float YellowAt = DefaultYellowAt, OrangeAt = DefaultOrangeAt, RedAt = DefaultRedAt;
        public float BlinkLeft;                // s of post-hit blinking remaining
    }

    // One weapon gauge's extracted geometry (gungauge or missilegauge). All flat, dial-local
    // like the other dials. The digit / type quads are ordered left→right; each indicator's polys sit
    // at its parsed index (ggindicator3 → Indicators[3]); the arrow rotates about the
    // centre to point at a belt position.
    private sealed class GaugeGeom
    {
        public readonly List<GaugePoly> Face = new();          // the labelled dial face (gungauge/missilegauge.tif + ring)
        public readonly List<GaugePoly> Digits = new();        // 4char_ammo quads, left→right
        public readonly List<GaugePoly> TypeChars = new();     // 6char_type quads, left→right
        public readonly List<List<GaugePoly>> Indicators = new(); // belt lights by index (0 = top, CCW)
        public GaugePoly? Arrow;                               // gg/mgarrow, rest points up (belt position 0)
        public int Positions;                                  // belt positions (4 gun / 8 missile) → arrow step
        public bool HasGeometry => Face.Count > 0 || Digits.Count > 0;
    }
}
