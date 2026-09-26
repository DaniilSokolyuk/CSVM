using CSVM.Flight.Airframe;
using CSVM.Flight.Hud;
using CSVM.Utils;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The two stall cues sit on different quantities (a load-factor margin for the lamp, an airspeed
/// one for the nose-drop), and both warning lamps blink on a decoded rate ramp. Decode:
/// docs/formats/hud.md's "Cockpit gauges" and docs/org/flightModel.md's "The two stall cues".
/// <see cref="GaugeCluster.StallLamp"/> is a plain struct; <see cref="Log.Debug"/> falls through
/// the process-wide no-op <c>TestHostLogSink</c>, avoiding the <c>StuntRaceTests</c> crash.
/// ⚠ These are the engine's own seconds. Do NOT convert them by k = 1.390: the lamp clock and the
/// flight-model dt are one variable in the original.
/// </summary>
public class StallWarningTests
{
    private const float SimDt = 1f / 60f;

    // One sim frame of tolerance: the integrator can only toggle on a step boundary.
    private const float FrameMs = SimDt * 1000f;

    [Fact]
    public void TheLampLeadsTheNoseDropOnItsOwnQuantity()
    {
        // Flies the Bloodhawk's real dynamics, not the fallback PlaneStats() defaults, whose own
        // stall speed sits close enough to the warn threshold to blur the split.
        var model = new FlightModel(new PlaneStats { VehWeight = 1900f, RefArea = 330f, FdSpeed = 135f });

        void At(float speedFactor, bool warned, bool stalled, string what)
        {
            model.Reset(Vector3.Zero, Basis.Identity, model.StallSpeed * speedFactor, 0f);
            bool w = model.IsStallWarned(), s = model.isStalled();
            Assert.True(w == warned && s == stalled,
                $"{what}: at {speedFactor:0.00}x stall speed n_avail={model.AvailableLoadFactor:0.000} " +
                $"warned={w} stalled={s}, expected warned={warned} stalled={stalled}");
        }

        // The gate is 2.35 g of available lift, and lift goes as v², so it sits near 1.53x the
        // speed at which the wing can just carry the aircraft.
        At(1.70f, false, false, "well clear of both cues, neither shows");
        At(1.40f, true, false, "inside the lamp's 2.35 g gate, nose still held");
        At(0.95f, true, true, "the nose drops once the wing cannot carry its own weight");

        // The mechanism, not just the ordering: the lamp reads the load factor and nothing else.
        model.Reset(Vector3.Zero, Basis.Identity, model.StallSpeed * 1.40f, 0f);
        Assert.True(model.IsStallWarned() == (model.AvailableLoadFactor < 2.35f),
            $"the lamp is exactly the 2.35 g test, n_avail={model.AvailableLoadFactor:0.000}");
    }

    [Fact]
    public void TheStallBlinkLawIsBoundedAndRampsWithDepth()
    {
        // At the gate the driver is zero, so the half-period is the base constant outright.
        Assert.True(Mathf.IsEqualApprox(GaugeCluster.StallBlinkHalfPeriodS(2.35f), 0.4f),
            "at the gate the half-period is 0.400 s");
        Assert.True(Mathf.Abs(GaugeCluster.StallBlinkHalfPeriodS(0f) - 0.100375f) < 1e-5f,
            "at zero available lift the half-period bottoms out at 0.100 s");

        // ⚠ The bound is the point: a law that can reach 643 ms is not this one.
        for (float n = 0f; n <= 2.35f; n += 0.05f)
        {
            float half = GaugeCluster.StallBlinkHalfPeriodS(n);
            Assert.True(half > 0.100f && half <= 0.4001f,
                $"the half-period stays inside (0.100, 0.400] s: {half:0.000} at n_avail {n:0.00}");
        }

        Assert.True(GaugeCluster.StallBlinkHalfPeriodS(2.0f) > GaugeCluster.StallBlinkHalfPeriodS(1.0f),
            "the blink shortens as the available load factor falls");
        Assert.True(GaugeCluster.StallDriver(2.35f) <= 0f && GaugeCluster.StallDriver(2.0f) > 0f,
            "the driver is positive exactly where the lamp shows");
    }

    [Fact]
    public void TheStallIntegratorCarriesTheRampOnItsOwnSimClock()
    {
        var lamp = new GaugeCluster.StallLamp();
        lamp.Advance(warning: true, nAvail: 2.3f, mph: 0f, simDt: 0f);
        Assert.True(lamp.Lit, "the lamp is lit on the tick the warning arrives");

        foreach (float n in new[] { 2.3f, 1.0f })
        {
            float dwell = DwellAt(ref lamp, n);
            float expected = GaugeCluster.StallBlinkHalfPeriodS(n) * 1000f;
            Assert.True(Mathf.Abs(dwell - expected) < FrameMs + 1f,
                $"a dwell at n_avail {n:0.00} spans {dwell:0} ms against the law's {expected:0}");
        }

        // A fixed-period blink would read the phase off a clock; this one integrates, so a change
        // part-way through a dwell shortens the REMAINDER rather than jumping the lamp.
        bool before = lamp.Lit;
        lamp.Advance(true, 2.3f, 0f, SimDt);
        lamp.Advance(true, 0.5f, 0f, SimDt);
        Assert.True(lamp.Lit == before,
            "a mid-dwell change does not toggle the lamp, only its remaining time");

        // No hysteresis: the cue follows the margin both ways, and clearing it re-arms the lamp.
        lamp.Advance(false, 0.5f, 0f, SimDt);
        Assert.False(lamp.Lit, "the lamp is dark once the warning clears");
        lamp.Advance(true, 2.3f, 0f, SimDt);
        Assert.True(lamp.Lit, "re-entering the warning lights the lamp again at once");
    }

    [Fact]
    public void TheLowAltLampRampsWithHeightAndFinishesItsDwell()
    {
        Assert.True(Mathf.IsEqualApprox(GaugeCluster.LowAltBlinkHalfPeriodS(0f), 0.14f),
            "on the deck the half-period is 0.140 s");
        Assert.True(Mathf.IsEqualApprox(GaugeCluster.LowAltBlinkHalfPeriodS(60f), 0.5f),
            "at the 60 m gate the half-period is 0.500 s");

        var lamp = new GaugeCluster.LowAltLamp();
        lamp.Advance(belowGate: false, aglMetres: 200f, simDt: SimDt);
        Assert.False(lamp.Lit, "well clear of the ground the lamp is dark");

        lamp.Advance(true, 10f, 0f);
        Assert.True(lamp.Lit, "dropping inside the gate lights it at once");

        // The rate is read every step, so a descent tightens the blink as it happens rather than
        // latching whatever height the lamp was armed at.
        float low = DwellAt(ref lamp, 5f);
        float high = DwellAt(ref lamp, 55f);
        Assert.True(high > low,
            $"the blink widens with height: {low:0} ms at 5 m against {high:0} ms at 55 m");

        // ⚠ The turn-off tail: climbing out mid-dwell finishes the half-period rather than
        // snapping the lamp off, and once dark it does not re-light.
        while (!lamp.Lit)
        {
            lamp.Advance(true, 5f, SimDt);
        }
        lamp.Advance(false, 80f, SimDt);
        Assert.True(lamp.Lit, "a lit lamp serves out its dwell after the climb through the gate");
        for (int i = 0; i < 200 && lamp.Lit; i++)
        {
            lamp.Advance(false, 80f, SimDt);
        }
        Assert.False(lamp.Lit, "and then goes dark");
        for (int i = 0; i < 200; i++)
        {
            lamp.Advance(false, 80f, SimDt);
            Assert.False(lamp.Lit, "out of the band it never blinks again");
        }
    }

    private static float DwellAt(ref GaugeCluster.StallLamp lamp, float nAvail)
    {
        bool lit = lamp.Lit;
        int steps = 0;
        while (lamp.Lit == lit && steps < 1000)
        {
            lamp.Advance(true, nAvail, 0f, SimDt);
            steps++;
        }
        return steps * SimDt * 1000f;
    }

    private static float DwellAt(ref GaugeCluster.LowAltLamp lamp, float aglMetres)
    {
        bool lit = lamp.Lit;
        int steps = 0;
        while (lamp.Lit == lit && steps < 1000)
        {
            lamp.Advance(true, aglMetres, SimDt);
            steps++;
        }
        return steps * SimDt * 1000f;
    }
}
