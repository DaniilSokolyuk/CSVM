using System;
using System.Globalization;
using System.Text;
using CSVM.UI.Boards;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Overlays;

/// <summary>
/// The always-available frame-cost readout (key F14): fps, the current frame's wall cost, and
/// the worst frame in the last few seconds, cycling Off, Compact, Full. The Full tier adds the
/// per-frame cost split, GC counts by generation, breadcrumbs, and a rolling frame-time graph.
/// Built once by <see cref="CSVM.Session.Launch.Launcher"/>, never per <c>GameSession</c> or
/// splitscreen pane, since fps/frame cost/GC are process-wide facts. Off by default so the 11
/// golden screenshots stay byte-identical; <c>--debug-fps[=compact|full]</c> is the scripted
/// twin. Fed the same raw <c>Stopwatch</c>-based cost <see cref="Utils.HitchMonitor"/> ticks on,
/// never Godot's <c>delta</c>; the current-cost term is unaveraged, matching this frame exactly.
/// Full decode: docs/architecture.md. What a frame-cost number can and cannot prove: PERF-1 and
/// PERF-13/14 in docs/verification.md.
/// </summary>
public sealed partial class PerfHud : Node
{
    // "A few seconds" per the Goal: long enough that a hitch you felt is still on screen a
    // moment later, short enough that the number still reads as "recent". UI cosmetic, not a
    // measurement threshold, a plain const, the same precedent as NodeLabels' RefreshInterval.
    private const double WorstHoldMs = 3000;

    // How often the label text is rebuilt. One short string either way, but there is no reason
    // to touch Label.Text sixty times a second for a number a human reads a few times a second.
    private const double RefreshIntervalMs = 150;

    // The font size this readout was sized at against HudMetrics.ReferenceHeight (1440p), bigger
    // than the 12 px debug-overlay labels (NodeLabels/MarkerOverlay), since this one is meant to
    // stay legible at a glance during ordinary play, not just be read up close while debugging.
    private const float ReferenceFontSize = 26f;

    // Label's own line_spacing theme default (see tools/godot-docs/doc/classes/Label.xml), not
    // scaled by our font-size override, so it stays a small constant rather than something the
    // WindowScale ratio applies to.
    private const float LabelLineSpacingPx = 3f;

    // perfHud.stripFrames' in-code default: the strip shows HitchMonitor's whole ring by default
    // (matches its own RingFramesDefault) rather than an arbitrary independent span.
    private const int StripFramesDefault = 120;

    private const float StripReferenceWidth = 360f;
    private const float StripReferenceHeight = 60f;
    private const float StripReferenceGapY = 6f;

    // How far inside the window's top-right corner the readout sits. Deliberately NOT scaled by
    // WindowScale: this is a margin against the screen edge, not a HUD metric measured at 1440p,
    // and a margin that grew with the window would drift away from the corner it marks.
    private const float CornerInsetPx = 8f;

    private readonly PerfSampleFrame _samples = new();

    private CanvasLayer? _hudLayer;
    private Control? _root;
    private Label? _hud;
    private PerfHudStrip? _strip;
    private Mode _mode = Mode.Off;
    private double _sinceRefreshMs;
    private double _lastFrameMs;
    private FrameCounters _lastCounters;
    private double _worstMs;
    private double _worstAgeMs;
    private bool _justRearmed;

    public PerfHud()
    {
        Name = "perf_hud";
    }

    public enum Mode { Off, Compact, Full }

    /// <summary>--debug-fps[=compact|full]: start switched on, for a scripted screenshot.</summary>
    public Mode InitialMode { get; init; } = Mode.Off;

    /// <summary>The hitch detector Full's per-frame terms and the strip read from directly, never
    /// a history of its own, per this class's own Trap. Set once, before
    /// the first <see cref="SetMode"/> that could need it.</summary>
    public HitchMonitor Monitor { get; init; } = null!;

    // The two placed controls, for the perf-hud-layout suite. Handed out rather than their rects,
    // so the suite owns the settling a Godot Label needs (its minimum size is recomputed on a
    // deferred call, which a suite running inside one frame never reaches).
    internal Label? Readout => _hud;

    internal PerfHudStrip? Strip => _strip;

    /// <summary>Parses the --debug-fps value. Absent value = Compact, the useful default.</summary>
    public static Mode ParseMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "full" => Mode.Full,
        "off" => Mode.Off,
        _ => Mode.Compact,
    };

    public override void _Ready()
    {
        if (InitialMode != Mode.Off)
            SetMode(InitialMode);
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F14 })
        {
            SetMode(_mode switch
            {
                Mode.Off => Mode.Compact,
                Mode.Compact => Mode.Full,
                _ => Mode.Off,
            });
        }
    }

    /// <summary>Feeds one rendered frame's wall cost and the same <see cref="FrameCounters"/>
    /// <see cref="Utils.HitchMonitor.Tick"/> was just handed, one counters read serves both
    /// instruments, per <c>Launcher._Process</c>'s own comment. Runs unconditionally, Off or not:
    /// the worst-frame peak has to already be warm the instant someone presses F14, or the readout
    /// would have nothing to say about the hitch that made them look.</summary>
    public void Tick(double frameMs, in FrameCounters counters)
    {
        if (double.IsNaN(frameMs) || frameMs < 0)
            frameMs = 0;

        if (_justRearmed)
        {
            // The frame that closes over a session build/teardown is not a real hitch, the same
            // reason HitchMonitor drops its own baseline here. Seed the display only.
            _justRearmed = false;
            _lastFrameMs = frameMs;
            _lastCounters = counters;
            return;
        }

        _lastFrameMs = frameMs;
        _lastCounters = counters;
        if (frameMs >= _worstMs)
        {
            _worstMs = frameMs;
            _worstAgeMs = 0;
        }
        else
        {
            _worstAgeMs += frameMs;
            if (_worstAgeMs >= WorstHoldMs)
            {
                _worstMs = frameMs;
                _worstAgeMs = 0;
            }
        }

        if (_mode == Mode.Off)
            return;
        // D11: the strip redraws every frame, unthrottled, a "rolling" strip that only advanced
        // a few times a second would not look rolling. Only while Full is actually shown, so
        // Compact costs nothing extra.
        if (_mode == Mode.Full)
            _strip?.QueueRedraw();
        _sinceRefreshMs += frameMs;
        if (_sinceRefreshMs < RefreshIntervalMs)
            return;
        _sinceRefreshMs = 0;
        Refresh();
    }

    /// <summary>Drops the worst-frame peak and skips one frame of tracking, call alongside
    /// <see cref="Utils.HitchMonitor.Rearm"/> after anything that legitimately stalls the frame
    /// loop (a session build, a teardown), so that stall never reads as the worst recent
    /// frame.</summary>
    public void Rearm()
    {
        _worstMs = 0;
        _worstAgeMs = 0;
        _justRearmed = true;
    }

    // Positions a control anchored to the window's top-right corner by its RIGHT edge, which is
    // the only edge that can be trusted there: a right-anchored control's OffsetLeft stays where
    // its box was pinned, while GrowHorizontal.Begin moves only the drawn rect, so reading
    // OffsetLeft back (or assigning Size, which derives OffsetRight from OffsetLeft) places the
    // control a full width off the right of the window. Width/height 0 hands sizing to the
    // control's own minimum size, which is what the label wants.
    private static void PlaceTopRight(Control control, float top, float width, float height)
    {
        control.OffsetRight = -CornerInsetPx;
        control.OffsetLeft = control.OffsetRight - width;
        control.OffsetTop = top;
        control.OffsetBottom = top + height;
    }

    private static string ModeLabel(Mode mode) => mode switch
    {
        Mode.Full => "full",
        _ => "compact",
    };

    // Mirrors HitchSidecar.FormatSamples' exact grammar (site:callsxms, comma-separated, PerfSite
    // order, "none" when nothing ran) so a breadcrumb reads the same in the readout as in the
    // sidecar's log line, duplicated rather than shared, since the two live in different modules
    // for unrelated reasons (a sidecar log line vs. a live label) and the format is ten lines.
    private static string FormatSamples(PerfSampleFrame s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Calls.Length; i++)
        {
            if (s.Calls[i] == 0)
                continue;
            if (sb.Length > 0)
                sb.Append(',');
            sb.Append(PerfSample.NameOf((PerfSite)i)).Append(':').Append(s.Calls[i]).Append('x')
                .Append(s.Ms[i].ToString("0.00", CultureInfo.InvariantCulture));
        }
        return sb.Length == 0 ? "none" : sb.ToString();
    }

    private void SetMode(Mode mode)
    {
        _mode = mode;
        if (mode == Mode.Off)
        {
            if (_hudLayer != null)
                _hudLayer.Visible = false;
            return;
        }
        EnsureBuilt();
        _hudLayer!.Visible = true;
        _sinceRefreshMs = RefreshIntervalMs; // redraw on the next Tick rather than waiting out the interval
        Refresh();
        Log.Info("perf", $"fps readout: {_mode}");
    }

    // Built lazily on first switch-on: a session that never presses F14 (or passes
    // --debug-fps) adds no nodes at all, so nothing it renders can differ.
    private void EnsureBuilt()
    {
        if (_hudLayer != null)
            return;
        _hudLayer = new CanvasLayer { Layer = HudLayers.PerfReadout };
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _hud = new Label { Text = "" };
        _hud.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _hud.GrowHorizontal = Control.GrowDirection.Begin;
        // A zero-width box on the corner inset; GrowHorizontal.Begin spends the label's minimum
        // width leftward from there, so the right edge stays on the inset however wide the text
        // and the font-size override make it. Not a Position, which is parent-space, not anchor.
        PlaceTopRight(_hud, CornerInsetPx, 0f, 0f);
        root.AddChild(_hud);
        _hudLayer.AddChild(root);
        AddChild(_hudLayer);
        _root = root;
    }

    // Built lazily on first entry into Full, a session that only ever cycles to Compact
    // never pays for the strip's own buffer or control. Sizes the buffer off `perfHud.stripFrames`
    // (clamped to RingFrames, since asking for more than the ring
    // keeps is meaningless) once, rather than on every refresh.
    private void EnsureStrip()
    {
        if (_strip != null)
            return;
        int span = Math.Clamp(Config.GetInt("perfHud.stripFrames", StripFramesDefault), 1, Monitor.RingFrames);
        _strip = new PerfHudStrip { MouseFilter = Control.MouseFilterEnum.Ignore, Monitor = Monitor };
        _strip.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _strip.GrowHorizontal = Control.GrowDirection.Begin;
        _strip.SetSpan(span);
        _root!.AddChild(_strip);
    }

    private void Refresh()
    {
        if (_hud == null)
            return;
        float scale = WindowScale();
        _hud.AddThemeFontSizeOverride("font_size", Mathf.Max(1, Mathf.RoundToInt(ReferenceFontSize * scale)));
        double fps = _lastFrameMs > 0 ? 1000.0 / _lastFrameMs : 0;
        string headline = string.Create(CultureInfo.InvariantCulture,
            $"perf [F14]: {ModeLabel(_mode)}, {fps:0.0} fps  frame {_lastFrameMs:0.00} ms  worst {_worstMs:0.00} ms");

        if (_mode != Mode.Full)
        {
            _hud.Text = headline;
            if (_strip != null)
                _strip.Visible = false;
            return;
        }

        // D11: the current frame's own split/count/memory terms, the same FrameCounters read
        // HitchMonitor.Tick was just handed, never a second sample of the engine.
        var c = _lastCounters;
        string split = string.Create(CultureInfo.InvariantCulture,
            $"split: script {c.ScriptMs:0.00}  render {c.RenderCpuMs:0.00}  gpu {c.GpuMs:0.00}  physics {c.PhysicsMs:0.00} ms");
        string counts = string.Create(CultureInfo.InvariantCulture,
            $"draws {c.Draws}  prims {c.Prims}  nodes {c.Nodes}  mem {c.MemBytes / (1024.0 * 1024.0):0.0} MB");
        // Raw generation counts, not deltas: a live readout reads better as "gc2 has fired 3 times
        // this run" than as a per-refresh delta that is almost always 0 between two 150 ms ticks.
        string gc = string.Create(CultureInfo.InvariantCulture,
            $"gc0 {GC.CollectionCount(0)}  gc1 {GC.CollectionCount(1)}  gc2 {GC.CollectionCount(2)}");
        // PerfSample.SnapshotInto reads the last CLOSED frame, which Launcher._Process ends in the
        // same call that feeds this Tick, so _lastFrameMs and this snapshot describe one frame.
        PerfSample.SnapshotInto(_samples, _lastFrameMs);
        string samples = string.Create(CultureInfo.InvariantCulture,
            $"samples={FormatSamples(_samples)}  attributed {_samples.AttributedMs:0.00}  unattributed {_samples.UnattributedMs:0.00} ms");
        _hud.Text = string.Join("\n", headline, split, counts, gc, samples);

        EnsureStrip();
        _strip!.Visible = true;
        // GetLineHeight/GetLineCount read the font metrics Godot itself just laid the text out
        // with (post the font-size override above), rather than a guessed line pitch, so the strip
        // sits exactly under the label whatever its line count, at any window size.
        float textHeight = (_hud.GetLineHeight() + LabelLineSpacingPx) * _hud.GetLineCount();
        PlaceTopRight(
            _strip,
            _hud.OffsetTop + textHeight + (StripReferenceGapY * scale),
            StripReferenceWidth * scale,
            StripReferenceHeight * scale);
    }

    // HudMetrics.Scale damps by PaneFactor (sqrt of the pane's share of the window), which is
    // exactly wrong here: this readout draws once for the whole window regardless of splitscreen,
    // so it takes the plain window-height ratio instead.
    private float WindowScale()
    {
        float windowH = GetTree()?.Root?.Size.Y ?? Flight.Hud.HudMetrics.ReferenceHeight;
        return windowH / Flight.Hud.HudMetrics.ReferenceHeight;
    }
}

/// <summary>
/// D11's rolling frame-time strip: one bar per recent frame, read fresh from
/// <see cref="Utils.HitchMonitor.CopyRing"/> on every draw, never its own history, so it can
/// never disagree with what a hitch record says about the same frame (the item's own Trap).
/// Redrawn every <see cref="PerfHud.Tick"/> while <see cref="PerfHud.Mode.Full"/> is showing,
/// unthrottled, a rolling strip that only advanced a few times a second would not look rolling.
///
/// <para>Vertical scale is <c>max(threshold, worst bar in the buffer) × 1.1</c>, recomputed every
/// draw: the trigger threshold is therefore always on screen (never scrolled off the top by a
/// tall bar), which is the point, the Approach calls for the threshold to be a visible line so
/// the relationship between what is drawn and what fires a record is legible.</para>
/// </summary>
public sealed partial class PerfHudStrip : Control
{
    internal HitchMonitor? Monitor;

    private static readonly Color StripBgColor = new(0f, 0f, 0f, 0.35f);
    private static readonly Color StripBarColor = new(0.4f, 0.9f, 0.5f, 0.9f);
    private static readonly Color StripBarHitchColor = new(1f, 0.3f, 0.25f, 0.95f);
    private static readonly Color StripThresholdColor = new(1f, 0.85f, 0.2f, 0.6f);

    private FrameSample[] _buf = Array.Empty<FrameSample>();

    public override void _Draw()
    {
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), StripBgColor);
        if (Monitor == null || _buf.Length == 0)
            return;
        int count = Monitor.CopyRing(_buf);
        if (count == 0)
            return;

        double thresholdMs = Monitor.ThresholdMs;
        double maxMs = thresholdMs;
        for (int i = 0; i < count; i++)
            maxMs = Math.Max(maxMs, _buf[i].FrameMs);
        double scaleMs = Math.Max(maxMs * 1.1, 20.0);

        // Most recent frame always at the right edge: while the ring is still filling (just after
        // a Rearm) the bars appear from the right and fill leftward, rather than stretching wide.
        float barW = size.X / _buf.Length;
        int offset = _buf.Length - count;
        for (int i = 0; i < count; i++)
        {
            double ms = _buf[i].FrameMs;
            float h = (float)Mathf.Clamp(ms / scaleMs * size.Y, 0.0, size.Y);
            float x = (offset + i) * barW;
            var color = ms > thresholdMs ? StripBarHitchColor : StripBarColor;
            DrawRect(new Rect2(x, size.Y - h, Mathf.Max(1f, barW - 1f), h), color);
        }

        float ty = size.Y - (float)Mathf.Clamp(thresholdMs / scaleMs * size.Y, 0.0, size.Y);
        DrawLine(new Vector2(0, ty), new Vector2(size.X, ty), StripThresholdColor, 1f);
    }

    internal void SetSpan(int frames)
    {
        if (_buf.Length != frames)
            _buf = new FrameSample[frames];
    }
}
