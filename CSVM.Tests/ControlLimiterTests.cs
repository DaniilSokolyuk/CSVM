using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CSVM.Flight.Airframe;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The G half of the original's opposing-command limiter is implemented and unreachable with this
/// install's authored values, on both sides of the band. Decode and the per-airframe margin table:
/// docs/org/flightModel.md, "Torques and the limiters". These tests fly every airframe in the
/// manoeuvres that produce the most load factor and fail if either threshold comes into reach,
/// which is when a stock envelope row would start moving with it.
/// ⚠ The AOA half is NOT unreachable and is not asserted here, it is a continuous window rather
/// than a threshold, live at <c>FlightModel.AoaLimiterFactor</c> 1 and binding on every stock
/// airframe. The α margins below are kept because they are the measurement behind that finding,
/// not because a threshold is being watched.
/// </summary>
public class ControlLimiterTests
{
    private const float Dt = 1f / 60f;

    // The executable's compiled fallback `highGs`, NOT what this install authors.
    private const float FallbackHighGStart = 5f;

    // The able-to-fail control's α fraction, NOT a measurement, just "a threshold the model can
    // still cross". Was 0.5, which retiring wingVert put 0.4% out of reach (peak α 22.9° against
    // 23.0°); pulling harder does not help, since α is a lag bounded by the chase rate.
    private const float DisproofAoaFraction = 0.45f;

    private static readonly string[] AllPlanes =
    {
        "player_bhawk", "player_pfighter", "player_fury", "player_warhawk", "player_autogyro",
        "player_avenger", "player_balmoral", "player_brigand", "player_fbrand", "player_kestrel",
        "player_peacemaker",
    };

    // The manoeuvres that make G and α, at full throttle: the sustained max-performance
    // pull at cruise and again entered fast (G grows with speed), the same pull banked, a full
    // forward push (the `lowGs` side), and full rudder. Ten seconds each, more than a full
    // loop, so nothing transient is missed.
    private static readonly (string Name, float EntryFdFrac, FlightInput In)[] Manoeuvres =
    {
        ("pull @ fd", 1.0f, new FlightInput { Pitch = 1f, Throttle = 1f }),
        ("pull @ 1.5 fd", 1.5f, new FlightInput { Pitch = 1f, Throttle = 1f }),
        ("banked pull @ fd", 1.0f, new FlightInput { Pitch = 1f, Roll = 1f, Throttle = 1f }),
        ("push @ 1.5 fd", 1.5f, new FlightInput { Pitch = -1f, Throttle = 1f }),
        ("rudder @ fd", 1.0f, new FlightInput { Yaw = 1f, Throttle = 1f }),
    };

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    /// <summary>The G limiter's ramp begins at the authored <c>highGs[0]</c>, so it has no effect at
    /// all below that. No airframe's hardest manoeuvre demands that much, and the demand read here
    /// is before both lift clamps, so the delivered load factor is lower still.</summary>
    [ExtractedDataFact]
    public void TheGLimiterCannotEngageOnAnyAirframe()
    {
        foreach (string plane in AllPlanes)
        {
            var stats = PlaneStats.Load(ZrdrPath, plane);
            var (peakG, where, _, _) = Worst(stats);
            Assert.True(peakG < stats.HighGStart,
                $"{plane}: peak demanded load factor {peakG:0.00} G ({where}) reaches the authored "
                + $"highGs[0] = {stats.HighGStart:0.0} G, the G limiter now engages and is owed an "
                + "implementation (gating ONLY input that opposes the current rotation)");
        }
    }

    /// <summary>The <c>lowGs</c> side, measured rather than argued: the limiter reads the DELIVERED
    /// lift's signed body-up component, which does go negative in an outside push, so the earlier
    /// "the demand is a length" reading measured the wrong quantity (METHOD-23). This side is not
    /// out of reach, a sustained forward push at 1.5 × <c>fd_speed</c> carries two airframes just
    /// past the authored −6 G. What is pinned is that the engagement stays a graze; a term running
    /// deep into the ramp would take most of a separating command and show in the envelope.</summary>
    [ExtractedDataFact]
    public void TheNegativeGLimiterOnlyEverGrazesTheTopOfItsRamp()
    {
        foreach (string plane in AllPlanes)
        {
            var stats = PlaneStats.Load(ZrdrPath, plane);
            float most = MostNegativeBodyUpG(stats);
            float into = Depth(most, stats.LowGStart, stats.LowGMax);
            Assert.True(into < 0.15f,
                $"{plane}: the delivered body-up load factor reaches {most:0.00} G, which is "
                + $"{into:0.0%} into the ramp from lowGs[0] = {stats.LowGStart:0.0} to "
                + $"lowGs[1] = {stats.LowGMax:0.0}, the negative-G limiter has stopped being a "
                + "graze, and every envelope row that pushes is owed a re-read");
        }
    }

    /// <summary>The able-to-fail control for the graze bound: the same measurement against a
    /// <c>lowGs</c> pair halved toward zero, the stand-in for a data edit that brings the ramp into
    /// real reach, must break it on the airframe with the deepest excursion (METHOD-9).</summary>
    [ExtractedDataFact]
    public void TheGrazeBoundIsAbleToFail()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        stats.LowGStart /= 2f;
        stats.LowGMax /= 2f;
        float into = Depth(MostNegativeBodyUpG(stats), stats.LowGStart, stats.LowGMax);
        Assert.True(into >= 0.15f,
            $"player_bhawk on a halved lowGs pair reaches only {into:0.0%} into the ramp, the push "
            + "has become too gentle for the graze bound above to be measuring anything");
    }

    /// <summary>The positive side on the same delivered quantity. The demand-side check above is
    /// the generous bound; this is the one the implementation actually reads, and the two are
    /// separated by both clamps and by the projection onto the body-up axis.</summary>
    [ExtractedDataFact]
    public void TheDeliveredLoadFactorStaysInsideTheAuthoredBand()
    {
        foreach (string plane in AllPlanes)
        {
            var stats = PlaneStats.Load(ZrdrPath, plane);
            float most = MostPositiveBodyUpG(stats);
            Assert.True(most < stats.HighGStart,
                $"{plane}: the delivered body-up load factor reaches {most:0.00} G against the "
                + $"authored highGs[0] = {stats.HighGStart:0.0} G, the G limiter now engages");
        }
    }

    /// <summary>α stays under the authored <c>maxAOA</c>, which is where the AOA window reaches
    /// zero and a separating pitch or yaw command would be removed outright. It is NOT the point at
    /// which that window starts to bite: the window is below 1 at every non-zero α, and this suite
    /// asserts only that no manoeuvre reaches its floor.</summary>
    [ExtractedDataFact]
    public void TheAoaLimiterCannotEngageOnAnyAirframe()
    {
        foreach (string plane in AllPlanes)
        {
            var stats = PlaneStats.Load(ZrdrPath, plane);
            float maxAoaDeg = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(stats.MaxAoaCos, -1f, 1f)));
            var (_, _, peakAlpha, where) = Worst(stats);
            Assert.True(peakAlpha < maxAoaDeg,
                $"{plane}: α peaks at {peakAlpha:0.0}° ({where}) against the authored maxAOA of "
                + $"{maxAoaDeg:0.0}°, the AOA limiter now engages and is owed an implementation "
                + "(gating ONLY input that opposes the current rotation)");
        }
    }

    /// <summary>The able-to-fail control: halving both authored thresholds, the stand-in for a
    /// data edit or override that brings them into reach, must make both checks fail.
    /// ⚠ The G margin is narrower than the authored numbers suggest: the Bloodhawk's peak demand
    /// is within 0.2% of the compiled fallback <see cref="FallbackHighGStart"/>. See
    /// docs/org/flightModel.md; a fallback is evidence of intent, not of behaviour.</summary>
    [ExtractedDataFact]
    public void TheDisproofIsAbleToFail()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");
        float maxAoaDeg = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(stats.MaxAoaCos, -1f, 1f)));
        var (peakG, gWhere, peakAlpha, alphaWhere) = Worst(stats);

        Assert.True(peakG > stats.HighGStart / 2f,
            $"player_bhawk: peak demanded load factor is only {peakG:0.00} G ({gWhere}) against "
            + $"half the authored highGs[0] ({stats.HighGStart / 2f:0.0} G), the manoeuvre has "
            + "become too gentle to trip a limiter, so the G disproof can no longer fail");
        Assert.True(peakAlpha > maxAoaDeg * DisproofAoaFraction,
            $"player_bhawk: α peaks at only {peakAlpha:0.0}° ({alphaWhere}) against "
            + $"{maxAoaDeg * DisproofAoaFraction:0.0}°, the manoeuvre has become too gentle to "
            + "trip a limiter, so the AOA disproof can no longer fail");
    }

    /// <summary>The per-airframe table behind the two disproofs, written to whatever file
    /// CSVM_LIMITER_OUT names (the same pattern FlightEnvelopeDump uses). Asserts nothing on its
    /// own.</summary>
    [ExtractedDataFact]
    public void DumpTheLimiterMargins()
    {
        // Invariant culture for the same reason Probes does it: a German decimal comma turns a
        // committed evidence table into something no later diff can compare against.
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        string? outPath = System.Environment.GetEnvironmentVariable("CSVM_LIMITER_OUT");
        var rows = new List<string>();
        foreach (string plane in AllPlanes)
        {
            var stats = PlaneStats.Load(ZrdrPath, plane);
            float maxAoaDeg = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(stats.MaxAoaCos, -1f, 1f)));
            var (peakG, gWhere, peakAlpha, alphaWhere) = Worst(stats);
            rows.Add($"{plane,-19} {peakG,8:0.00} {stats.HighGStart,9:0.0} {stats.HighGStart - peakG,9:0.00} "
                     + $"{MostPositiveBodyUpG(stats),8:0.00} {MostNegativeBodyUpG(stats),8:0.00} "
                     + $"{peakAlpha,8:0.0} {maxAoaDeg,8:0.0} {maxAoaDeg - peakAlpha,9:0.0}   "
                     + $"{gWhere} / {alphaWhere}");
        }

        if (outPath != null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{"airframe",-19} {"peak G",8} {"highGs0",9} {"margin",9} "
                          + $"{"up G max",8} {"up G min",8} "
                          + $"{"peak a",8} {"maxAOA",8} {"margin",9}   worst manoeuvre (G / a)");
            foreach (string row in rows)
                sb.AppendLine(row);
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        }

        Assert.Equal(AllPlanes.Length, rows.Count);
    }

    // How far past a ramp's start a reading sits, as a fraction of the ramp's width. Zero short of
    // the start, 1 at the far end where the limiter removes a separating command entirely.
    private static float Depth(float reading, float start, float end) =>
        (reading - start) / (end - start) is var f && f > 0f ? f : 0f;

    // The most negative and the most positive DELIVERED body-up load factor over every manoeuvre,
    // which is the signed quantity the implemented limiter reads.
    private static float MostNegativeBodyUpG(PlaneStats stats) => ExtremeBodyUpG(stats, negative: true);

    private static float MostPositiveBodyUpG(PlaneStats stats) => ExtremeBodyUpG(stats, negative: false);

    private static float ExtremeBodyUpG(PlaneStats stats, bool negative)
    {
        float extreme = 0f;
        foreach (var (_, frac, input) in Manoeuvres)
        {
            var m = new FlightModel(stats);
            m.Reset(Vector3.Zero, Basis.Identity, stats.FdSpeed * frac, input.Throttle);
            for (int i = 0; i < 600; i++)
            {
                m.Step(input, Dt);
                extreme = negative
                    ? Mathf.Min(extreme, m.BodyUpLoadFactor)
                    : Mathf.Max(extreme, m.BodyUpLoadFactor);
            }
        }

        return extreme;
    }

    // Peak demanded load factor and peak α over one manoeuvre.
    private static (float PeakG, float PeakAlpha) Fly(PlaneStats stats, float entrySpeed, FlightInput input)
    {
        var m = new FlightModel(stats);
        m.Reset(Vector3.Zero, Basis.Identity, entrySpeed, input.Throttle);
        float peakG = 0f, peakAlpha = 0f;
        for (int i = 0; i < 600; i++)
        {
            m.Step(input, Dt);
            peakG = Mathf.Max(peakG, m.LoadFactorDemand);
            peakAlpha = Mathf.Max(peakAlpha, m.Alpha);
        }
        return (peakG, peakAlpha);
    }

    // Worst case over every manoeuvre, for one airframe.
    private static (float PeakG, string GWhere, float PeakAlpha, string AlphaWhere) Worst(PlaneStats stats)
    {
        float peakG = 0f, peakAlpha = 0f;
        string gWhere = "-", alphaWhere = "-";
        foreach (var (name, frac, input) in Manoeuvres)
        {
            var (g, a) = Fly(stats, stats.FdSpeed * frac, input);
            if (g > peakG)
                (peakG, gWhere) = (g, name);
            if (a > peakAlpha)
                (peakAlpha, alphaWhere) = (a, name);
        }
        return (peakG, gWhere, peakAlpha, alphaWhere);
    }
}
