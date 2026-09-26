using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Testing;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The square-wave pitch-cadence sweep, run against our own flight model, drives the same input
/// the original was filmed at into a throwaway <see cref="FlightModel"/> and measures the ripple
/// the same way. Each cadence runs twice: through <see cref="StickRamp"/>, the deflection the
/// original's aircraft saw, and raw. Decode and the measured roll-off: docs/org/flightModel.md.
/// ⚠ Fit the trend and the sinusoid simultaneously; detrending first moves the amplitude with the span.
/// ⚠ Quote the WALL reading: the macro drove the keys in wall milliseconds, so the period the
/// game saw is that × 1.390 (docs/verification.md DET-11). The sim reading is kept only because
/// earlier results were quoted from it; the ratio is NOT invariant once a rate limit is in play.
/// ⚠ This does not fit a τ: the cadences are not at a common operating point, which is why mean
/// airspeed is reported beside every row.
/// </summary>
public class ZzCadenceSweep
{
    // Sim seconds per wall second.
    private const double SimPerWall = 1.390;

    private const float Dt = 1f / 60f;
    private const float Mph = 0.44704f;
    private const float Ft = 0.3048f;

    // Periods of settling discarded before the fit window opens, then periods fitted.
    // Eight is the floor for the simultaneous fit; twelve leaves margin.
    private const int SettlePeriods = 3;
    private const int FitPeriods = 12;

    // The original's six cadences, in the wall milliseconds its input log recorded
    // (jitter sd 0.002–0.535 ms, so these are known rather than nominal), with the ripple each one
    // produced in feet. The last two sat at the decode floor and are quoted as upper bounds.
    private static readonly (int WallMs, double OriginalFt, bool AtFloor)[] Cadences =
    {
        (1300, 26.31, false),
        (930, 7.07, false),
        (700, 3.09, false),
        (570, 0.63, false),
        (370, 0.065, true),
        (230, 0.037, true),
    };

    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    [ExtractedDataFact]
    public void SweepThePitchCadences()
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        string outPath = System.Environment.GetEnvironmentVariable("CSVM_CADENCE_OUT")
                         ?? Path.Combine(Path.GetTempPath(), "cadence-sweep.txt");
        var stats = PlaneStats.Load(ZrdrPath, "player_bhawk");

        var sb = new StringBuilder();
        sb.AppendLine("# square-wave pitch-cadence sweep: player_bhawk");
        sb.AppendLine("# alternating full pitch-up / full pitch-down, 300 mph level entry, full throttle");
        sb.AppendLine($"# {SettlePeriods} periods settled, {FitPeriods} periods fitted "
                      + "(cubic + sin + cos simultaneously, never detrend first)");
        sb.AppendLine("# 'original' = the measured ripple from the original's own clips, feet");
        sb.AppendLine();

        foreach (bool ramped in new[] { true, false })
        {
            sb.AppendLine(ramped
                ? "# STICK RAMPED, the key command through StickRamp, which is what the original's"
                  + " aircraft saw"
                : "# STICK RAW, full deflection the instant the key goes down; no original flies this");
            foreach (bool simIsWall in new[] { false, true })
            {
                double k = simIsWall ? 1.0 : SimPerWall;
                sb.AppendLine(simIsWall
                    ? "## cadence read as SIM seconds (period_sim = period_wall), SUPERSEDED, kept for continuity"
                    : $"## cadence read as WALL seconds (period_sim = period_wall x {SimPerWall:0.000}, DET-11), QUOTE THIS ONE");
                sb.AppendLine("wall ms   period_sim   f0_sim      ripple ft   mean mph   original ft");
                var amps = new List<double>();
                foreach (var (wallMs, originalFt, atFloor) in Cadences)
                {
                    double period = wallMs / 1000.0 * k;
                    var (amp, meanMph) = Ripple(stats, (float)period, ramped);
                    amps.Add(amp);
                    sb.AppendLine($"{wallMs,7}   {period,10:0.0000}   {1.0 / period,7:0.000}   "
                                  + $"{amp,9:0.0000}   {meanMph,8:0.0}   "
                                  + (atFloor ? $"<= {originalFt:0.000}" : $"{originalFt,8:0.000}"));
                }

                // The discriminating span, 1300 -> 570 ms; see docs/org/flightModel.md's C23 landing
                // note for the single-lag ceiling this compares against and its own correction.
                double fRatio = 1300.0 / 570.0;
                double model = amps[0] / amps[3];
                double lagCeiling = fRatio * fRatio * fRatio;
                sb.AppendLine($"roll-off 1300 -> 570 ms: model {model:0.0}x   original 42x   "
                              + $"single-lag ceiling {lagCeiling:0.0}x   "
                              + $"model excess {model / lagCeiling:0.00}x   original excess 3.5x");
                sb.AppendLine();
            }
        }

        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Assert.True(File.Exists(outPath));
    }

    // Flies one cadence and returns (ripple amplitude in feet, mean airspeed in mph over
    // the fit window).
    private static (double AmplitudeFt, double MeanMph) Ripple(
        PlaneStats stats, float period, bool ramped)
    {
        var m = new FlightModel(stats);
        m.Reset(Vector3.Zero, Basis.Identity, 300f * Mph, 1f);
        m.Position = new Vector3(0f, 1000f, 0f);

        double settle = SettlePeriods * period;
        double total = settle + (FitPeriods * period);
        var t = new List<double>();
        var y = new List<double>();
        double speedSum = 0;
        float stick = 0f;
        for (double now = 0; now < total; now += Dt)
        {
            // The square wave: full up for the first half of each period, full down for the second.
            // Alternating (rather than one key) keeps the mean rate at zero, so the aircraft
            // porpoises about level and the operating point stays put.
            float key = (now % period) < (period * 0.5) ? 1f : -1f;
            stick = ramped ? StickRamp.Step(stick, key, Dt) : key;
            m.Step(new FlightInput { Pitch = stick, Throttle = 1f }, Dt);
            if (now < settle)
                continue;
            t.Add(now - settle);
            y.Add(m.Position.Y / Ft);
            speedSum += m.Speed / Mph;
        }

        return (FitSinusoid(t, y, 1.0 / period), t.Count > 0 ? speedSum / t.Count : 0);
    }

    // Least-squares fit of cubic + A·sin(2πf·t) + B·cos(2πf·t) over the whole window, all
    // six coefficients solved together; returns sqrt(A² + B²). Fitting the trend and the sinusoid
    // simultaneously is the point, removing the trend first has real gain at f and biases the
    // amplitude, which is the error that produces a wrong figure.
    private static double FitSinusoid(IReadOnlyList<double> t, IReadOnlyList<double> y, double f)
    {
        const int N = 6;
        if (t.Count < N)
            return 0;

        // t is rescaled to [-1, 1] for the polynomial columns only, so the normal equations stay
        // well conditioned at cubic order; the sinusoid keeps real time.
        double span = t[t.Count - 1] - t[0];
        var ata = new double[N, N];
        var atb = new double[N];
        var row = new double[N];
        for (int i = 0; i < t.Count; i++)
        {
            double u = span > 0 ? (((t[i] - t[0]) / span) * 2.0) - 1.0 : 0.0;
            row[0] = 1;
            row[1] = u;
            row[2] = u * u;
            row[3] = u * u * u;
            row[4] = Math.Sin(2 * Math.PI * f * t[i]);
            row[5] = Math.Cos(2 * Math.PI * f * t[i]);
            for (int a = 0; a < N; a++)
            {
                atb[a] += row[a] * y[i];
                for (int b = 0; b < N; b++)
                    ata[a, b] += row[a] * row[b];
            }
        }

        var x = Solve(ata, atb, N);
        return x == null ? 0 : Math.Sqrt((x[4] * x[4]) + (x[5] * x[5]));
    }

    // Gaussian elimination with partial pivoting; null if the system is singular.
    private static double[]? Solve(double[,] a, double[] b, int n)
    {
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col]))
                    piv = r;
            if (Math.Abs(a[piv, col]) < 1e-12)
                return null;
            if (piv != col)
            {
                for (int c = 0; c < n; c++)
                    (a[col, c], a[piv, c]) = (a[piv, c], a[col, c]);
                (b[col], b[piv]) = (b[piv], b[col]);
            }

            for (int r = col + 1; r < n; r++)
            {
                double factor = a[r, col] / a[col, col];
                for (int c = col; c < n; c++)
                    a[r, c] -= factor * a[col, c];
                b[r] -= factor * b[col];
            }
        }

        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double sum = b[r];
            for (int c = r + 1; c < n; c++)
                sum -= a[r, c] * x[c];
            x[r] = sum / a[r, r];
        }

        return x;
    }
}
