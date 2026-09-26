using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Mech3;
using Godot;

namespace CSVM.UI.Labs;

/// <summary>
/// The animation debugger's timeline: a custom-drawn strip showing, per Initial sequence, its
/// events at their authored start times (upper band, computed statically) and a bright tick at
/// each event's actual dispatch time (lower band, fed from
/// <see cref="AnimRuntime.OnEventDispatched"/>). ⚠ The authored band deliberately re-derives the
/// documented scheduling rule rather than calling the runtime's <c>SequenceRunner</c>, so a
/// scheduling bug shows as a gap between an event's block and its tick; do not "simplify" that
/// independence away. A <c>CALL_ANIMATION</c> child becomes an appended, indented lane group
/// offset at its instance's start time; loops accumulate ticks across passes, Restart clears.
/// Display only: <see cref="Control.MouseFilterEnum.Ignore"/>. Full decode: docs/architecture.md.
/// </summary>
public sealed partial class AnimTimeline : Control
{
    // Beyond this many ticks a looping lane drops its oldest, so a train left running for
    // minutes cannot grow the list without bound. Short one-shot defs never reach it.
    private const int MaxFiredPerLane = 600;

    private const float MinSpan = 2f;      // the axis never zooms in tighter than 2 s
    private const float Pad = 8f;
    private const float LabelW = 150f;     // the sequence-name column
    private const float LegendY = 13f;     // the legend baseline (row 1)
    private const float AxisLabelY = 28f;  // the axis tick-label baseline (row 2)
    private const float HeaderH = 32f;     // gridlines + lanes start below both header rows
    private const float LaneH = 20f;
    private const float Indent = 14f;      // a child group's lane indent

    // ---- drawing colours -----------------------------------------------------------------------

    private static readonly Color Bg = new(0.05f, 0.06f, 0.08f, 0.85f);
    private static readonly Color GridCol = new(1f, 1f, 1f, 0.09f);
    private static readonly Color AxisText = new(0.6f, 0.7f, 0.85f);
    private static readonly Color LaneSep = new(1f, 1f, 1f, 0.05f);
    private static readonly Color LabelCol = new(0.72f, 0.83f, 1f);
    private static readonly Color LabelDim = new(0.55f, 0.62f, 0.75f);
    private static readonly Color AuthTimed = new(0.32f, 0.5f, 0.82f, 0.55f);
    private static readonly Color AuthTimedEdge = new(0.5f, 0.7f, 1f, 0.9f);
    private static readonly Color AuthInstant = new(0.5f, 0.7f, 1f, 0.95f);
    private static readonly Color ControlCol = new(0.6f, 0.6f, 0.68f, 0.6f);
    private static readonly Color FiredCol = new(1f, 0.78f, 0.25f);
    private static readonly Color ConnectorCol = new(1f, 0.78f, 0.25f, 0.5f);
    private static readonly Color Playhead = new(0.95f, 0.98f, 1f, 0.95f);
    private static readonly Color ChildHdr = new(0.7f, 0.9f, 0.72f);

    private readonly List<Lane> _lanes = new();
    private readonly List<ChildGroup> _children = new();

    private AnimProgram? _program;
    private AnimDefinition? _def;
    private string _anchorLabel = "";
    private bool _primaryFinished;
    private float _playhead;
    private Font _font = null!;
    private int _fontSize;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        _font = GetThemeDefaultFont();
        _fontSize = 12;
    }

    /// <summary>Selects the definition the timeline visualises: rebuilds the authored lanes from
    /// its Initial (non-on-call) sequences and drops every previous mark. <paramref name="def"/>
    /// null clears the strip.</summary>
    public void SetDef(AnimProgram program, AnimDefinition? def, string anchorLabel)
    {
        _program = program;
        _def = def;
        _anchorLabel = anchorLabel;
        _primaryFinished = false;
        _lanes.Clear();
        _children.Clear();
        _playhead = 0f;
        if (def != null)
        {
            foreach (var seq in def.Sequences.Where(s => !s.OnCallOnly))
            {
                _lanes.Add(BuildLane(def, seq));
            }
        }
        QueueRedraw();
    }

    public void Clear() => SetDef(_program ?? new AnimProgram(), null, "");

    /// <summary>The current anchor label, updated once the runtime reports which anchor the
    /// played instance took (the header shows it).</summary>
    public void SetAnchorLabel(string label)
    {
        if (label == _anchorLabel)
        {
            return;
        }
        _anchorLabel = label;
        QueueRedraw();
    }

    /// <summary>Stamps a fired tick on the primary def's matching lane. A dispatch whose sequence
    /// is not among the authored lanes (a CALL_SEQUENCE'd on-call sequence) gets a bare lane
    /// appended so its firings are still visible, without authored blocks, since its start time
    /// is dynamic.</summary>
    public void AddPrimaryMark(string seq, int eventIndex, float time)
    {
        StampFired(_lanes, seq, eventIndex, time, allowDiscover: true);
        QueueRedraw();
    }

    /// <summary>Appends a CALL_ANIMATION child lane group at the playhead time it began.</summary>
    public void AddChild(AnimDefinition def, Node3D? anchor, float startTime)
    {
        if (_program == null)
        {
            return;
        }
        var group = new ChildGroup { Def = def, Anchor = anchor, StartTime = startTime };
        foreach (var seq in def.Sequences.Where(s => !s.OnCallOnly))
        {
            group.Lanes.Add(BuildLane(def, seq));
        }
        _children.Add(group);
        QueueRedraw();
    }

    public void AddChildMark(AnimDefinition def, Node3D? anchor, string seq, int eventIndex, float time)
    {
        var group = FindChild(def, anchor);
        if (group == null)
        {
            return;
        }
        StampFired(group.Lanes, seq, eventIndex, time, allowDiscover: true);
        QueueRedraw();
    }

    public void MarkPrimaryFinished()
    {
        _primaryFinished = true;
        QueueRedraw();
    }

    public void MarkChildFinished(AnimDefinition def, Node3D? anchor)
    {
        if (FindChild(def, anchor) is { } group)
        {
            group.Finished = true;
            QueueRedraw();
        }
    }

    /// <summary>The playhead time (steps × dt). Only redraws when it actually moves, so a paused
    /// lab does not repaint every frame.</summary>
    public void SetPlayhead(float t)
    {
        if (Mathf.IsEqualApprox(t, _playhead))
        {
            return;
        }
        _playhead = t;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), Bg);
        Text(new Vector2(Pad, LegendY), Legend(), AxisText, _fontSize);
        if (_def == null || _lanes.Count == 0)
        {
            string hint = _def == null
                ? "no def playing, pick one (P) or --play-anim=<name>"
                : $"{_def.AnimName} has no Initial sequences (all on-call)";
            Text(new Vector2(Pad, HeaderH + 18), hint, LabelDim, _fontSize);
            return;
        }

        float trackX = LabelW;
        float trackW = Mathf.Max(40f, size.X - LabelW - Pad);
        float tMax = ComputeTMax();

        DrawAxis(trackX, trackW, tMax, size.Y);

        float y = HeaderH + 4f;
        foreach (var lane in _lanes)
        {
            DrawLane(lane, ref y, trackX, trackW, tMax, 0f, indented: false, dim: false);
        }
        foreach (var group in _children)
        {
            string tag = group.Def.AnimName ?? group.Def.Name;
            Text(new Vector2(Pad + Indent, y + 13), $"↳ {tag}{(group.Finished ? " ✓" : "")}",
                group.Finished ? LabelDim : ChildHdr, _fontSize);
            // A tick at the child's start offset, so where the call landed on the axis is visible.
            float sx = TimeToX(group.StartTime, trackX, trackW, tMax);
            DrawLine(new Vector2(sx, y + 2), new Vector2(sx, y + LaneH - 2), ChildHdr with { A = 0.5f }, 1f);
            y += LaneH;
            foreach (var lane in group.Lanes)
            {
                DrawLane(lane, ref y, trackX, trackW, tMax, group.StartTime, indented: true, dim: group.Finished);
            }
        }

        // The playhead over every lane. Clamped to the axis: once a one-shot def finishes, the
        // clock keeps running but the marks stop, so pinning it at the right edge keeps the
        // authored-vs-fired comparison readable rather than compressing it to nothing.
        float px = TimeToX(_playhead, trackX, trackW, tMax);
        DrawLine(new Vector2(px, HeaderH), new Vector2(px, y), Playhead, 1.5f);
    }

    // ---- authored schedule (the independent half of the instrument) --------------------------

    private static void StampFired(List<Lane> lanes, string seq, int eventIndex, float time, bool allowDiscover)
    {
        var lane = lanes.FirstOrDefault(l => string.Equals(l.Name, seq, StringComparison.OrdinalIgnoreCase));
        if (lane == null)
        {
            if (!allowDiscover)
            {
                return;
            }
            lane = new Lane { Name = seq, OnCallOnly = true }; // discovered from a call, no authored blocks
            lanes.Add(lane);
        }
        lane.Fired.Add((eventIndex, time));
        if (lane.Fired.Count > MaxFiredPerLane)
        {
            lane.Fired.RemoveAt(0);
        }
    }

    private static bool IsControlFlow(string kind) =>
        kind is "Loop" or "If" or "Elseif" or "Else" or "Endif";

    // ---- drawing -----------------------------------------------------------------------------

    private static float? AuthoredTimeOf(Lane lane, int eventIndex)
    {
        foreach (var b in lane.Blocks)
        {
            if (b.EventIndex == eventIndex)
            {
                return b.Time;
            }
        }
        return null;
    }

    private static float TimeToX(float t, float trackX, float trackW, float tMax) =>
        trackX + Mathf.Clamp(t / tMax, 0f, 1f) * trackW;

    // A round axis step (1/2/5 × 10^n) near tMax/5.
    private static float NiceStep(float target)
    {
        if (target <= 0f)
        {
            return 1f;
        }
        double exp = Math.Floor(Math.Log10(target));
        double f = target / Math.Pow(10, exp);
        double nf = f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10;
        return (float)(nf * Math.Pow(10, exp));
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private ChildGroup? FindChild(AnimDefinition def, Node3D? anchor)
    {
        for (int i = _children.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_children[i].Def, def) && ReferenceEquals(_children[i].Anchor, anchor))
            {
                return _children[i];
            }
        }
        return null;
    }

    // Lays a sequence's events out at their authored fire times in one linear pass,
    // deliberately not via the runtime's `SequenceRunner`, following the scheduling rule at
    // docs/formats/anim-definitions.md. Loops are not unrolled; fired ticks accumulate across
    // passes and show the period against this single authored layout.
    private Lane BuildLane(AnimDefinition def, AnimSequence seq)
    {
        var lane = new Lane { Name = seq.Name, OnCallOnly = seq.OnCallOnly };
        float baseT = 0f;
        for (int i = 0; i < seq.Events.Count; i++)
        {
            var ev = seq.Events[i];
            bool control = IsControlFlow(ev.Kind);
            float due = ev.StartOffset is "Animation" or "Sequence" ? ev.StartTime : baseT + ev.StartTime;
            float dur = control ? 0f : StaticDuration(def, ev);
            lane.Blocks.Add(new Block(i, due, dur, control));
            if (!control)
            {
                baseT = due + dur;
            }
        }
        return lane;
    }

    // The event's run time, mirroring AnimRuntime.Dispatch's `duration` out-param: the tween /
    // spin run time, or the SI script length. Everything else is instantaneous (0).
    private float StaticDuration(AnimDefinition def, AnimEvent ev)
    {
        switch (ev.Kind)
        {
            case "ObjectMotionFromTo":
            case "ObjectMotion":
                return ev.Data.Num("run_time") ?? 0f;
            case "ObjectMotionSiScript":
                int slot = (int)(ev.Data.Num("index") ?? 0f);
                return _program?.ScriptFor(def, slot)?.Duration ?? 0f;
            case AnimDefinition.AllNamesKind:
                float longest = 0f;
                foreach (var motion in ev.Data.Objects("motions"))
                {
                    int one = (int)(motion.Num("index") ?? 0f);
                    longest = Mathf.Max(longest, _program?.ScriptFor(def, one)?.Duration ?? 0f);
                }
                return longest;
            default:
                return 0f;
        }
    }

    private string Legend()
    {
        string subject = _def == null ? "" :
            $"{_def.AnimName ?? _def.Name}{(_anchorLabel.Length > 0 ? $" @ {_anchorLabel}" : "")}{(_primaryFinished ? " (finished)" : "")}   ";
        return $"timeline · {subject}authored ▏  fired ▎  playhead │   t {_playhead:0.00}s";
    }

    private void DrawAxis(float trackX, float trackW, float tMax, float height)
    {
        float step = NiceStep(tMax / 5f);
        for (float t = 0f; t <= tMax + 1e-3f; t += step)
        {
            float x = TimeToX(t, trackX, trackW, tMax);
            DrawLine(new Vector2(x, HeaderH), new Vector2(x, height), GridCol, 1f);
            Text(new Vector2(x + 2, AxisLabelY), $"{t:0.##}", AxisText, _fontSize - 1);
        }
    }

    private void DrawLane(Lane lane, ref float y, float trackX, float trackW, float tMax,
        float offset, bool indented, bool dim)
    {
        DrawLine(new Vector2(0, y + LaneH), new Vector2(trackX + trackW, y + LaneH), LaneSep, 1f);
        var labelCol = dim ? LabelDim : lane.OnCallOnly ? LabelDim : LabelCol;
        string label = Truncate((indented ? "  " : "") + lane.Name + (lane.OnCallOnly ? " (call)" : ""),
            indented ? 20 : 22);
        Text(new Vector2(Pad + (indented ? Indent : 0), y + 13), label, labelCol, _fontSize);

        float authTop = y + 2f, authBot = y + 9f;
        float firedTop = y + 10f, firedBot = y + LaneH - 2f;

        foreach (var b in lane.Blocks)
        {
            float x = TimeToX(offset + b.Time, trackX, trackW, tMax);
            if (b.Control)
            {
                DrawLine(new Vector2(x, authTop), new Vector2(x, authBot), dim ? ControlCol with { A = 0.3f } : ControlCol, 1f);
            }
            else if (b.Duration > 0f)
            {
                float x2 = TimeToX(offset + b.Time + b.Duration, trackX, trackW, tMax);
                var r = new Rect2(x, authTop, Mathf.Max(2f, x2 - x), authBot - authTop);
                DrawRect(r, dim ? AuthTimed with { A = 0.3f } : AuthTimed);
                DrawRect(r, dim ? AuthTimedEdge with { A = 0.4f } : AuthTimedEdge, filled: false, width: 1f);
            }
            else
            {
                DrawRect(new Rect2(x, authTop, 2f, authBot - authTop), dim ? AuthInstant with { A = 0.4f } : AuthInstant);
            }
        }

        // Only the first firing of each event index gets a connector to its authored block;
        // a slant is the authored-vs-actual divergence this strip exists to surface.
        var connected = new HashSet<int>();
        foreach (var (idx, time) in lane.Fired)
        {
            float fx = TimeToX(time, trackX, trackW, tMax);
            DrawRect(new Rect2(fx - 1f, firedTop, 2f, firedBot - firedTop), dim ? FiredCol with { A = 0.4f } : FiredCol);
            if (connected.Add(idx) && AuthoredTimeOf(lane, idx) is { } at)
            {
                float ax = TimeToX(offset + at, trackX, trackW, tMax);
                DrawLine(new Vector2(ax, authBot), new Vector2(fx, firedTop), dim ? ConnectorCol with { A = 0.25f } : ConnectorCol, 1f);
            }
        }
        y += LaneH;
    }

    private float ComputeTMax()
    {
        float m = MinSpan;
        void Consider(List<Lane> lanes, float offset)
        {
            foreach (var lane in lanes)
            {
                foreach (var b in lane.Blocks)
                {
                    m = Mathf.Max(m, offset + b.Time + b.Duration);
                }
                foreach (var (_, time) in lane.Fired)
                {
                    m = Mathf.Max(m, time);
                }
            }
        }
        Consider(_lanes, 0f);
        foreach (var group in _children)
        {
            Consider(group.Lanes, group.StartTime);
        }
        return m * 1.05f;
    }

    private void Text(Vector2 pos, string s, Color col, int size) =>
        DrawString(_font, pos, s, HorizontalAlignment.Left, -1, size, col);

    // One authored event, its statically-computed fire time within its sequence.
    private readonly record struct Block(int EventIndex, float Time, float Duration, bool Control);

    // One sequence's lane: its authored blocks (empty for a lane discovered only from a fired
    // mark, a CALL_SEQUENCE'd on-call sequence) and the actual dispatch times stamped onto it.
    private sealed class Lane
    {
        public readonly List<Block> Blocks = new();
        public readonly List<(int EventIndex, float Time)> Fired = new();
        public string Name = "";
        public bool OnCallOnly;
    }

    // A CALL_ANIMATION child instance: its own lanes, offset on the axis by the playhead time it
    // began. Kept after it finishes (its ticks are the record); only dimmed.
    private sealed class ChildGroup
    {
        public readonly List<Lane> Lanes = new();
        public AnimDefinition Def = null!;
        public Node3D? Anchor;
        public float StartTime;
        public bool Finished;
    }
}
