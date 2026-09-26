using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CSVM.Flight.Camera;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.World;

/// <summary>Draws the sun's lens flare: a screen-space rig of four sprites strung along the
/// sun→screen-centre vector, plus a full-screen white wash whose opacity rises as the sun nears
/// centre. One instance per pane, ticked from the same per-rig block as <see cref="WeatherRig"/>.
/// The whole spec is measured off footage, not decoded: <c>docs/org/weather.md</c>.
/// ⚠ Two gates must agree, a gamez <c>sun</c> node and <c>LensFlareTexture</c> slots, true only
/// of C2 and C3; a disagreement is a real signal about the data and is logged, not smoothed over.
/// ⚠ C2's flare is predicted, not verified; only C3 was captured. Do not treat C2 as matched.
/// Per-pane state lives here rather than on <see cref="PlayerRig"/>, unlike the simpler cloud
/// whiteout, because a flare instance is several nodes plus fade state.</summary>
public sealed class LensFlareRig
{
    // ── Measured off the footage, at 1280×720 ──────────────────────────────────────────────────
    // Every pixel figure below is normalised by pane HEIGHT against this reference. Godot's default
    // KeepHeight aspect holds the vertical FOV whatever the pane's shape, so height is the axis that
    // maps to a fixed angle, and that angle is the decoded external base
    // (CameraController.ExternalFovDeg), the original's own vertical, so a pixel share of this
    // pane's height and an angular size say the same thing in every pane at every resolution.
    private const float RefHeight = 720f;

    // The wash ("sun blindness"): a plain white alpha composite, opacity ~linear in the sun's
    // screen distance from centre. Values fit a line through the measured table's two ends;
    // the fit and its evidence are in docs/org/weather.md.
    private const float WashMax = 0.66f;         // measured 0.65–0.68 at the sun near centre
    private const float WashFullPx = 30f;        // inside this the wash is at WashMax
    private const float WashSlopePerPx = 0.001651f;

    // The rig pops in COMPLETE when the sun core crosses into frame, and fades out in ~0.1–0.15 s
    // as it leaves. So: instant attack, timed release.
    private const float FadeOutSeconds = 0.125f;

    // Ring intensities, calibrated against the footage's own measured Δlum targets.
    // ⚠ Only Ring A's intensity was nudged; Ring B and C landed mid-band and were deliberately
    // left alone. The pose/statistic matching and the measured values: docs/org/weather.md.
    private static readonly Element[] Elements =
    {
        // Core, at the sun, "saturated-white disc, blue-cyan skirt", ~63 px half-max, blooming to
        // ~86 near centre. Measured Δlum: saturating, hence full intensity.
        new(0f, 63f, 1.00f),
        // Ring A, frac 0.50, ~100–104 px, soft wide dim ring. Measured Δlum 10–12; calibrated
        // 0.30 → 0.38, which reads 11.0 (see the calibration note below).
        new(0.50f, 102f, 0.38f),
        // Ring B, frac 0.90, ~42–48 px, the crispest and brightest. Measured Δlum 14–21;
        // reads 17.3, mid-band, uncalibrated.
        new(0.90f, 45f, 0.45f),
        // Ring C, frac 1.95–2.0, ~158–170 px, the faintest. Measured Δlum 3–9; reads 5.0,
        // mid-band, uncalibrated.
        new(2.0f, 164f, 0.18f),
    };

    private readonly SessionSpec _spec;
    private readonly List<Instance> _instances = new();

    public LensFlareRig(SessionSpec spec) => _spec = spec;

    /// <summary>How many panes got a flare, 0 when the chapter authors none, which is every
    /// chapter but C2 and C3.</summary>
    public int InstanceCount => _instances.Count;

    /// <summary>Reads the chapter's <c>LensFlareTexture</c> slot registrations out of the interp
    /// extraction, returning the texture name per slot in slot order. Empty when the chapter
    /// registers none, which is the gate, not an error. Mirrors
    /// <see cref="Clutter.TemplateNames"/>, which reads <c>adjust.gw</c> from the same file the
    /// same way.</summary>
    public static List<string> FlareTextureNames(string interpPath, string chapter)
    {
        var bySlot = new SortedDictionary<int, string>();
        if (!File.Exists(interpPath))
            return new List<string>();
        var wanted = $"support\\{chapter.ToLowerInvariant()}\\init.gw";
        using var doc = JsonDocument.Parse(File.ReadAllBytes(interpPath));
        foreach (var script in doc.RootElement.EnumerateArray())
        {
            if (!script.TryGetProperty("name", out var n)
                || !string.Equals(n.GetString(), wanted, System.StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var line in script.GetProperty("lines").EnumerateArray())
            {
                var parts = (line.GetString() ?? "").Split(' ',
                    System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[0] == "LensFlareTexture"
                    && int.TryParse(parts[1], out int slot))
                    bySlot[slot] = parts[2];
            }
        }
        return new List<string>(bySlot.Values);
    }

    /// <summary>Builds one flare per rig, or nothing at all when either gate is closed. Safe to
    /// call for every chapter, the gates do the deciding.</summary>
    public void Build(IReadOnlyList<PlayerRig> rigs, TextureArchive textures,
        string interpPath, string chapter)
    {
        var names = FlareTextureNames(interpPath, chapter);
        // Gate 1: the registered textures. Gate 2: a `sun` node in this rig's dome, checked per rig
        // below (each pane builds its own horizon copy, so each has its own sun).
        bool anySun = false;
        foreach (var rig in rigs)
            if (FindSun(rig.Horizon) != null)
                anySun = true;

        if (names.Count == 0 || !anySun)
        {
            // Log only when the two gates DISAGREE, that is data telling us something we have not
            // decoded. Both closed is the ordinary case for six of the eight chapters.
            if (names.Count > 0 != anySun)
            {
                Log.Warn("world", $"lens flare gates disagree chapter={chapter} textures={names.Count} sun_node={anySun} flare=off");
            }
            return;
        }

        if (_spec.NoFlare)
        {
            Log.Info("world", $"lens flare: --no-flare, {chapter} flare suppressed (slots {string.Join(",", names)})");
            return;
        }

        foreach (var rig in rigs)
        {
            var sun = FindSun(rig.Horizon);
            if (sun == null)
                continue;

            var inst = new Instance { Rig = rig, Sun = sun };

            // Sprites: under the HUD, and on their own layer below the cloud whiteout's, so flying
            // into a cloud swallows the flare with everything else outside the canopy. The layer
            // numbers carry that order, never the build order of the two rigs.
            var spriteCanvas = new CanvasLayer { Layer = HudLayers.FlareSprites, Name = "flare" };
            var spriteRoot = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            spriteRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            spriteCanvas.AddChild(spriteRoot);
            rig.HudParent.AddChild(spriteCanvas);
            rig.WorldOverlays.Add(spriteCanvas);

            for (int i = 0; i < Elements.Length && i < names.Count; i++)
            {
                var tex = textures.Find(names[i]);
                if (tex == null)
                    continue;
                var rect = new TextureRect
                {
                    Texture = tex,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    StretchMode = TextureRect.StretchModeEnum.Scale,
                    // Additive: matches the core's measured saturating bloom and Ring C fading
                    // over bright background. ⚠ Overrules one measurement (treated as capture
                    // noise); see docs/org/weather.md if intensities won't hit target without blowing out the core.
                    Material = new CanvasItemMaterial
                    {
                        BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
                    },
                };
                spriteRoot.AddChild(rect);
                inst.Sprites.Add(rect);
            }

            // The wash: above the HUD, because the original's whitens the compass and the gauge
            // faces at the same α as world pixels.
            var washCanvas = new CanvasLayer { Layer = HudLayers.SunWash, Name = "sun_wash" };
            inst.Wash = new ColorRect
            {
                Color = new Color(1f, 1f, 1f, 0f),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            inst.Wash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            washCanvas.AddChild(inst.Wash);
            rig.HudParent.AddChild(washCanvas);
            rig.WorldOverlays.Add(washCanvas);

            _instances.Add(inst);
        }

        if (_instances.Count > 0)
        {
            Log.Info("world", $"lens flare [{chapter}]: {_instances.Count} rig(s), slots {string.Join(",", names)}");
        }
    }

    /// <summary>Per-frame: place the elements, gate them, fade them. Called from the per-rig block
    /// of <c>GameSession._Process</c>, after the dome has been re-anchored.</summary>
    public void Tick(double delta)
    {
        foreach (var inst in _instances)
        {
            var cam = inst.Rig.Camera;
            var viewport = cam.GetViewport();
            var size = viewport.GetVisibleRect().Size;
            if (size.Y <= 0f)
                continue;

            // The dome is camera-anchored; the sun's own GlobalPosition already reflects that, so
            // it stays exact even if the dome's re-anchoring runs after this tick.
            var sunGlobal = inst.Sun.GlobalPosition;

            bool behind = cam.IsPositionBehind(sunGlobal);
            var screen = cam.UnprojectPosition(sunGlobal);
            bool onScreen = !behind && viewport.GetVisibleRect().HasPoint(screen);

            // Line of sight for the sprites only: one ray to the sun's centre against the world's
            // own colliders.
            // ⚠ Testing the centre needs no invented coverage threshold; see docs/org/weather.md.
            bool losClear = true;
            if (onScreen)
            {
                var space = cam.GetWorld3D().DirectSpaceState;
                using var query = PhysicsRayQueryParameters3D.Create(cam.GlobalPosition, sunGlobal);
                using var hit = space.IntersectRay(query);
                losClear = hit.Count == 0;
            }

            inst.SpriteFade = Step(inst.SpriteFade, onScreen && losClear, delta);
            inst.WashFade = Step(inst.WashFade, onScreen, delta);   // the wash ignores occlusion

            float scale = size.Y / RefHeight;
            var centre = size * 0.5f;
            var toCentre = centre - screen;

            for (int i = 0; i < inst.Sprites.Count; i++)
            {
                var rect = inst.Sprites[i];
                if (inst.SpriteFade <= 0f)
                {
                    rect.Visible = false;
                    continue;
                }
                var e = Elements[i];
                float dia = e.DiaPx * scale;
                var pos = screen + (toCentre * e.Frac);
                rect.Visible = true;
                rect.Size = new Vector2(dia, dia);
                rect.Position = pos - (rect.Size * 0.5f);
                float a = e.Intensity * inst.SpriteFade;
                rect.Modulate = new Color(a, a, a, 1f);
            }

            if (inst.Wash != null)
            {
                // Distance normalised to the reference height, so the falloff is the same angular
                // falloff in a splitscreen pane as in a full window.
                float distPx = toCentre.Length() / scale;
                float alpha = Mathf.Clamp(
                    WashMax - ((distPx - WashFullPx) * WashSlopePerPx), 0f, WashMax);
                inst.Wash.Color = new Color(1f, 1f, 1f, alpha * inst.WashFade);
            }
        }
    }

    // Instant attack, timed release, the measured shape: the rig "pops in complete" when
    // the sun core enters frame and fades over ~0.1–0.15 s as it leaves.
    private static float Step(float current, bool on, double delta)
        => on ? 1f : Mathf.Max(0f, current - ((float)delta / FadeOutSeconds));

    // The `sun` node inside one rig's dome copy, or null when this chapter's horizon
    // carries none. Scoped to the dome deliberately: the name is short enough that an unscoped
    // search could pick up something unrelated elsewhere in the world.
    private static Node3D? FindSun(Node3D? horizon)
        => horizon?.FindChild("sun", recursive: true, owned: false) as Node3D;

    // One flare element: where it sits along the sun→screen-centre vector, how wide it is at
    // RefHeight, and how hard it is drawn. `Frac` 0 is the sun, 1 the screen centre.
    // ⚠ Slot order ascending the vector is a fact about this four-element rig, not a decoded
    // rule about the format, see docs/org/weather.md.
    private readonly record struct Element(float Frac, float DiaPx, float Intensity);

    private sealed class Instance
    {
        public readonly List<TextureRect> Sprites = new();
        public PlayerRig Rig = null!;
        public Node3D Sun = null!;
        public ColorRect? Wash;
        public float SpriteFade;
        public float WashFade;
    }
}
