using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Net;
using Godot;
using Xunit;
using Xunit.Abstractions;

namespace CSVM.Tests;

/// <summary>
/// How smoothly a remote aeroplane plays back when two machines run their own frame loops. Each
/// side steps its simulation off its own rendered frames through a physics accumulator, so a
/// sample leaves and lands on a frame edge. The link adds the loopback transport's own latency
/// and jitter model. The measurement is the receiver's shown displacement per simulation step
/// against the displacement the sender's speed implies. A playback that follows arrival times
/// shows every late sample as a slow stretch and a catch-up.
/// </summary>
[Trait("Tier", "Quick")]
public class RemotePosePlaybackTests
{
    private const double Dt = 1.0 / 60.0;
    private const float Speed = 100f;
    private const double WallSeconds = 40.0;

    // Answers past this are the stream settled; before it the playout is still finding the link.
    private const double WarmupSeconds = 5.0;

    private readonly ITestOutputHelper _out;

    public RemotePosePlaybackTests(ITestOutputHelper output)
    {
        _out = output;
    }

    public static IEnumerable<object[]> Links => new[]
    {
        // name, sender fps, sender sim rate, receiver fps, receiver sim rate, latency, jitter
        new object[] { "two PCs, 120 fps capped guest to a 113 fps host", 120.0, 1.0, 113.0, 1.0, 0.030, 0.008 },
        new object[] { "a lossless link, frame edges alone", 120.0, 1.0, 113.0, 1.0, 0.030, 0.0 },
        new object[] { "the sender's sim at 0.6 of wall time", 120.0, 0.6, 113.0, 1.0, 0.030, 0.008 },
        new object[] { "the receiver's sim at 0.6 of wall time", 120.0, 1.0, 113.0, 0.6, 0.030, 0.008 },
    };

    [Theory]
    [MemberData(nameof(Links))]
    public void TheRemoteAeroplaneFliesAtItsOwnersSpeedWithoutSurges(
        string link, double senderFps, double senderRate, double receiverFps, double receiverRate,
        double latency, double jitter)
    {
        var run = Fly(senderFps, senderRate, receiverFps, receiverRate, new LoopbackConditions(latency, jitter, 0.0));

        // The displacement one receiver step owes: the sender's metres per wall second, over the
        // receiver's steps per wall second.
        double owed = Speed * senderRate * Dt / receiverRate;
        var ratios = run.Steps.Select(d => d / owed).ToArray();
        double rms = Math.Sqrt(ratios.Average(r => (r - 1.0) * (r - 1.0)));
        double worst = ratios.Max(r => Math.Abs(r - 1.0));

        // The surge: speed over a sliding tenth of a second, which is what reads as a wave.
        const int window = 6;
        var surges = new List<double>();
        for (int i = window; i <= ratios.Length; i++)
            surges.Add(ratios.Skip(i - window).Take(window).Average());
        double surge = surges.Max() - surges.Min();

        _out.WriteLine(FormattableString.Invariant($"{link}: {ratios.Length} steps, speed rms {rms:P1}, worst step {worst:P1}, 0.1 s surge {surge:P1}, lag {run.LagMin * 1000:0}..{run.LagMax * 1000:0} ms, starved {run.Starved}, extrapolating {run.Extrapolating}"));

        Assert.True(run.Starved == 0, $"{link}: {run.Starved} starved answers");
        Assert.True(rms < 0.02, FormattableString.Invariant($"{link}: the shown speed wanders {rms:P1} rms from the owner's"));
        Assert.True(surge < 0.05, FormattableString.Invariant($"{link}: the shown speed surges {surge:P1} over a tenth of a second"));
        Assert.True(run.LagMax < latency + jitter + 0.2, FormattableString.Invariant($"{link}: the shown aeroplane runs {run.LagMax * 1000:0} ms behind its owner"));
    }

    // Both machines, frame by frame on one wall clock. The sender samples on its cadence inside
    // its physics steps, and the link delays each payload. The receiver takes what has landed
    // before each of its steps, then reads once, as the session's physics callback does.
    private static Run Fly(double senderFps, double senderRate, double receiverFps, double receiverRate,
        LoopbackConditions conditions)
    {
        var rng = new Random(7);
        var cadence = new AircraftStateCadence();
        var inFlight = new List<(double Due, AircraftStateMessage Sample)>();
        var sentAt = new List<(double Wall, double Sim)>();

        // The sender's whole run first: each payload is due on the receiver at a wall time.
        double wall = 0.0, accum = 0.0, sim = 0.0;
        while (wall < WallSeconds + 1.0)
        {
            double frame = Frame(rng, senderFps, 0.05);
            wall += frame;
            accum += frame * senderRate;
            while (accum >= Dt)
            {
                accum -= Dt;
                sim += Dt;
                sentAt.Add((wall, sim));
                if (cadence.StepSends())
                {
                    var sample = new AircraftStateMessage(1, cadence.Next(1), new Vector3((float)(Speed * sim), 0f, 0f),
                        Quaternion.Identity, new Vector3(Speed, 0f, 0f), 1f, 0f, 0f, 0f, false);
                    inFlight.Add((wall + conditions.Delay(rng), sample));
                }
            }
        }

        inFlight.Sort((a, b) => a.Due.CompareTo(b.Due));

        var buffer = new RemotePoseBuffer();
        var run = new Run();
        int next = 0;
        float? previous = null;
        wall = 0.0;
        accum = 0.0;
        while (wall < WallSeconds)
        {
            double frame = Frame(rng, receiverFps, 0.25);
            wall += frame;
            accum += frame * receiverRate;
            while (accum >= Dt)
            {
                accum -= Dt;
                while (next < inFlight.Count && inFlight[next].Due <= wall)
                    buffer.Receive(inFlight[next++].Sample);
                buffer.Advance((float)Dt);
                if (!buffer.TrySample(out var pose))
                    continue;
                if (wall >= WarmupSeconds)
                {
                    if (previous is { } before)
                        run.Steps.Add(pose.Position.X - before);
                    run.Starved += pose.Feed == RemotePoseFeed.Starved ? 1 : 0;
                    run.Extrapolating += pose.Feed == RemotePoseFeed.Extrapolating ? 1 : 0;
                    double lag = WallOf(sentAt, pose.Position.X / Speed);
                    run.LagMin = Math.Min(run.LagMin, wall - lag);
                    run.LagMax = Math.Max(run.LagMax, wall - lag);
                }

                previous = pose.Position.X;
            }
        }

        return run;
    }

    // One rendered frame's wall duration: the rate's period, spread uniformly by a fraction.
    private static double Frame(Random rng, double fps, double spread) =>
        (1.0 / fps) * (1.0 + (((rng.NextDouble() * 2.0) - 1.0) * spread));

    // The wall time at which the sender's simulation clock read this many seconds.
    private static double WallOf(List<(double Wall, double Sim)> sentAt, double sim)
    {
        int lo = 0, hi = sentAt.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sentAt[mid].Sim >= sim)
                hi = mid;
            else
                lo = mid + 1;
        }

        return sentAt[lo].Wall;
    }

    private sealed class Run
    {
        public List<float> Steps { get; } = new();

        public int Starved { get; set; }

        public int Extrapolating { get; set; }

        public double LagMin { get; set; } = double.MaxValue;

        public double LagMax { get; set; } = double.MinValue;
    }
}
