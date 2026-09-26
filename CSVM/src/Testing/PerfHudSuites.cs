using CSVM.UI.Overlays;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>
/// Where the frame-cost readout lands on screen, not what it says. Both of its controls are
/// anchored to the window's top-right corner and grow leftward as the text and the font-size
/// override widen them, so the only edge either of them can be placed by is the RIGHT one: a
/// right-anchored control placed by its left edge instead walks its own width off the side of the
/// window and shows a sliver. The readout is built lazily, so the suite drives the real Full tier
/// through a real <see cref="PerfHud.Tick"/> rather than inspecting a hand-made layout.
/// </summary>
internal static class PerfHudSuites
{
    // The inset the readout is expected to keep from the window's right edge. Written out rather
    // than read from PerfHud: a check that takes its expectation from the code it checks cannot
    // notice that code changing.
    private const float ExpectedInsetPx = 8f;

    // One frame's worth of plausible cost, enough for Tick to refresh the label and lay out the
    // strip. The numbers themselves are never asserted on.
    private const double SampleFrameMs = 16.0;

    [Suite("perf-hud-layout",
        "where the frame-cost readout lands: the real Full tier is driven a frame, then its "
        + "label and its frame-time strip are both required to keep their right edge 8 px "
        + "inside the window and their left edge on screen, since a top-right control placed "
        + "by its LEFT edge walks its own width off the side")]
    internal static void PerfHudLayout(TestContext ctx)
    {
        var hud = new PerfHud { InitialMode = PerfHud.Mode.Full, Monitor = new HitchMonitor() };
        ctx.Host.AddChild(hud);
        try
        {
            var counters = default(FrameCounters);
            hud.Tick(SampleFrameMs, counters);
            hud.Tick(SampleFrameMs, counters);

            var label = hud.Readout;
            var strip = hud.Strip;
            ctx.Check(label != null && strip != null,
                $"the Full tier built both the readout and its frame-time strip, so there is a layout to measure");
            if (label == null || strip == null)
                return;

            Settle(label);
            float windowW = label.GetViewportRect().Size.X;
            var labelRect = label.GetGlobalRect();
            var stripRect = strip.GetGlobalRect();
            ctx.Note($"window {windowW} px, readout {labelRect}, strip {stripRect}");

            Inside(ctx, "the readout", labelRect, windowW);
            Inside(ctx, "the frame-time strip", stripRect, windowW);
            ctx.Check(labelRect.Size.X > ExpectedInsetPx,
                $"the readout measured its own text ({labelRect.Size.X} px wide), so the edge checks above ran against a real box");
            ctx.Check(stripRect.Position.Y >= labelRect.End.Y,
                $"the strip sits under the readout ({stripRect.Position.Y} >= {labelRect.End.Y}) rather than over its last line");
        }
        finally
        {
            ctx.Host.RemoveChild(hud);
            hud.QueueFree();
        }
    }

    // Godot recomputes a Label's minimum size on a deferred call and only then re-runs the layout,
    // which a suite running inside a single frame never reaches; writing an offset re-runs it now,
    // against the text the refresh above already set.
    private static void Settle(Control control)
    {
        control.OffsetTop += 1f;
        control.OffsetTop -= 1f;
    }

    private static void Inside(TestContext ctx, string what, Rect2 rect, float windowW)
    {
        ctx.Check(Mathf.IsEqualApprox(rect.End.X, windowW - ExpectedInsetPx),
            $"{what}'s right edge is {ExpectedInsetPx} px inside the window ({rect.End.X} of {windowW}), so widening text moves its left edge instead of running it off the side");
        ctx.Check(rect.Position.X >= 0f,
            $"and {what} starts on screen ({rect.Position.X}), so the whole box is visible and not just its trailing edge");
    }
}
