using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The maneuver executor, engine-free: program timing on hand-built maneuvers over a default
/// <see cref="PlaneStats"/> model, plus the worked demonstration, a real
/// <see cref="FlightModel"/> on the shipped Bloodhawk stats flying shipped programs (dive,
/// split_s), attitude and altitude asserted before vs after. Playback is pure, so there is no
/// in-engine suite for this: nothing here needs a Node, a clock or the render path.
/// </summary>
public class ManeuverExecutorTests
{
    private const float Dt = 1f / 60f;

    private static string InstallZrdr =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    [Fact]
    public void TimedStepsAdvanceOnTheirDurationsAndFinish()
    {
        var maneuver = Hand("timed", steps: new[]
        {
            new ManeuverStep(1f, 0f, 0f, 0f, Array.Empty<float>()),
            new ManeuverStep(2f, 0f, 0f, 0f, Array.Empty<float>()),
        });
        var (model, exec) = Launch(maneuver);

        // 60 frames of step 0, 120 of step 1, then Done, the HoldSegments-style clock.
        for (int frame = 0; frame < 60; frame++)
        {
            model.Step(exec.Next(model, Dt), Dt);
            Assert.Equal(0, exec.StepIndex);
        }
        for (int frame = 0; frame < 120; frame++)
        {
            model.Step(exec.Next(model, Dt), Dt);
            Assert.Equal(1, exec.StepIndex);
        }
        Assert.False(exec.Done);
        model.Step(exec.Next(model, Dt), Dt);
        Assert.True(exec.Done);
    }

    [Fact]
    public void AZeroDurationStepAdvancesWhenTheAttitudeIsCaptured()
    {
        // Roll to 60° left bank (zero duration = advance when reached), then hold level 1 s.
        var maneuver = Hand("capture", steps: new[]
        {
            new ManeuverStep(0f, 0f, 0f, 60f, Array.Empty<float>()),
            new ManeuverStep(1f, 0f, 0f, 0f, Array.Empty<float>()),
        });
        var (model, exec) = Launch(maneuver);

        int frames = FlyUntil(model, exec, () => exec.StepIndex >= 1, maxSeconds: 5f);
        float bankDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(model.Attitude.X.Y, -1f, 1f)));
        Assert.True(frames * Dt < ManeuverExecutor.ZeroDurationTimeoutS,
            $"step 0 only advanced by the safety timeout, after {frames * Dt:0.0} s");
        Assert.InRange(bankDeg, 60f - ManeuverExecutor.StepToleranceDeg - 10f, 90f);
    }

    [Fact]
    public void AStubRefusesToBeFlown()
    {
        var stub = new Maneuver
        {
            Name = "stub",
            Difficulty = 99,
            Steps = Array.Empty<ManeuverStep>(),
        };
        Assert.Throws<ArgumentException>(() => new ManeuverExecutor(stub));
    }

    [Fact]
    public void PlaybackIsDeterministicOnAFixedDt()
    {
        float[] Run()
        {
            var maneuver = Hand("det", steps: new[]
            {
                new ManeuverStep(0f, 0f, 45f, 0f, Array.Empty<float>()),
                new ManeuverStep(2f, 20f, 45f, 0f, Array.Empty<float>()),
            });
            var (model, exec) = Launch(maneuver);
            for (int frame = 0; frame < 600; frame++)
                model.Step(exec.Next(model, Dt), Dt);
            return new[] { model.Position.X, model.Position.Y, model.Position.Z, model.Speed };
        }

        Assert.Equal(Run(), Run());
    }

    // ---- The worked demonstration on shipped data -----------------------------------------------

    [ExtractedDataFact]
    public void TheShippedDiveFliesABloodhawkSixtyDegreesDownhill()
    {
        var dive = Maneuvers.Load(InstallZrdr).Single(m => m.Name == "dive");
        var (model, exec) = Launch(dive, stats: PlaneStats.Load(InstallZrdr, "player_bhawk"),
            altitude: 1500f, speed: 80f);

        FlyUntil(model, exec, () => exec.Done, maxSeconds: 10f);

        // One step: [4 s, −60° pitch]. The nose must actually get near −60 and the altitude
        // must fall by a real dive's worth, placeholder-law honesty: the capture takes ~1.5 s
        // of the 4, so the loss is hundreds of metres, not the full-program ballistic figure.
        float noseDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp((-model.Attitude.Z).Y, -1f, 1f)));
        Assert.True(noseDeg < -40f, $"dive ended nose at {noseDeg:0.0}°, wanted well below -40°");
        Assert.True(model.Position.Y < 1500f - 200f,
            $"dive lost only {1500f - model.Position.Y:0.0} m of altitude");
        Assert.True(model.Speed > 80f, $"a 60° dive should gain speed, ended at {model.Speed:0.0} m/s");
    }

    [ExtractedDataFact]
    public void TheShippedSplitSReversesTheHeadingAndEndsLower()
    {
        var splitS = Maneuvers.Load(InstallZrdr).Single(m => m.Name == "split_s");
        var (model, exec) = Launch(splitS, stats: PlaneStats.Load(InstallZrdr, "player_bhawk"),
            altitude: 1500f, speed: 100f);

        FlyUntil(model, exec, () => exec.Done, maxSeconds: 40f);

        // Tolerance is wide: the waypoints pass through the vertical, where the tracking law
        // leans on the zero-duration timeout. The claim is "recognisably a split-S".
        float headingDeg = AiPilot.HeadingDegOf(-model.Attitude.Z);
        float turned = Mathf.Abs(Mathf.Wrap(headingDeg - 0f, -180f, 180f));
        Assert.True(turned > 120f, $"split_s ended only {turned:0.0}° off the entry heading");
        Assert.True(model.Position.Y < 1500f, $"split_s ended higher than it began ({model.Position.Y:0.0} m)");
    }

    [ExtractedDataFact]
    public void ThePredictedPathChainsOneStepReachPerProgramStep()
    {
        var splitS = Maneuvers.Load(InstallZrdr).Single(m => m.Name == "split_s");
        var path = ManeuverExecutor.PredictedPath(splitS, new Vector3(0f, 1500f, 0f), Basis.Identity);

        // Level, straight down, then two reversals: one step lower and one step back down the
        // entry heading, which is what the selection-time altitude veto reads.
        Assert.Equal(splitS.Steps.Count, path.Length);
        float reach = ManeuverExecutor.PredictedStepM;
        Assert.Equal(1500f - reach, path[^1].Y, 2);
        Assert.Equal(reach, path[^1].Z, 2);
        Assert.Equal(0f, path[^1].X, 2);
    }

    private static Maneuver Hand(string name, IReadOnlyList<ManeuverStep> steps) => new()
    {
        Name = name,
        Difficulty = 1,
        Steps = steps,
    };

    private static (FlightModel Model, ManeuverExecutor Exec) Launch(
        Maneuver maneuver, PlaneStats? stats = null, float altitude = 800f, float speed = 80f)
    {
        var model = new FlightModel(stats ?? new PlaneStats());
        model.Reset(new Vector3(0f, altitude, 0f), Basis.Identity, speed, 0.85f);
        return (model, new ManeuverExecutor(maneuver));
    }

    private static int FlyUntil(FlightModel model, ManeuverExecutor exec, Func<bool> stop, float maxSeconds)
    {
        int frames = 0;
        while (!stop() && frames * Dt < maxSeconds)
        {
            model.Step(exec.Next(model, Dt), Dt);
            frames++;
        }
        return frames;
    }
}
