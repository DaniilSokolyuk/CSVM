using System;
using CSVM.Flight;
using Godot;
using Xunit;

namespace CSVM.Tests;

public class ZeppelinReplicaTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void AHullWithNoSampleHoldsWhereItWasPlaced()
    {
        var placed = new Vector3(100f, 400f, -2000f);
        var replica = new ZeppelinReplica(placed, 0.5f, 0.1f);
        Run(replica, 10f);
        Assert.Equal(placed, replica.Position);
        Assert.Equal(0.5f, replica.YawRad, 4);
        Assert.Equal(0.1f, replica.PitchRad, 4);
    }

    // The decay is e^(-2 dt) per step, so a still target one second away has closed by 1 - e^-2.
    [Fact]
    public void TheHullClosesOnAStillTargetAtTheDecodedRate()
    {
        var replica = new ZeppelinReplica(Vector3.Zero, 0f, 0f);
        Assert.True(replica.Receive(1, new Vector3(0f, 0f, -100f), 0f, 0f, 0f));
        Run(replica, 1f);
        float expected = 100f * (1f - MathF.Exp(-ZeppelinReplica.ChaseRatePerS));
        Assert.Equal(expected, -replica.Position.Z, 1);
    }

    // The target is carried forward at the sent speed, and the chase trails it by speed over rate.
    [Fact]
    public void AMovingTargetIsDeadReckonedAndTrailedBySpeedOverTheRate()
    {
        const float speed = 20f;
        var replica = new ZeppelinReplica(Vector3.Zero, 0f, 0f);
        replica.Receive(1, Vector3.Zero, speed, 0f, 0f);
        Run(replica, 10f);
        float reckoned = speed * 10f;
        float trail = speed / ZeppelinReplica.ChaseRatePerS;
        Assert.InRange(-replica.Position.Z, reckoned - trail - 0.5f, reckoned - trail + 0.5f);
        Assert.Equal(0f, replica.Position.X, 3);
    }

    [Fact]
    public void TheFacingTurnsOntoTheSentFacing()
    {
        var replica = new ZeppelinReplica(Vector3.Zero, 0f, 0f);
        replica.Receive(1, Vector3.Zero, 0f, 1.2f, -0.2f);
        Run(replica, 5f);
        Assert.Equal(1.2f, replica.YawRad, 2);
        Assert.Equal(-0.2f, replica.PitchRad, 2);
    }

    [Fact]
    public void AStaleSampleChangesNothingAndTheSequenceWraps()
    {
        var replica = new ZeppelinReplica(Vector3.Zero, 0f, 0f);
        Assert.True(replica.Receive(65535, new Vector3(0f, 0f, -10f), 0f, 0f, 0f));
        Assert.False(replica.Receive(65534, new Vector3(0f, 0f, 500f), 0f, 0f, 0f));
        Assert.False(replica.Receive(65535, new Vector3(0f, 0f, 500f), 0f, 0f, 0f));
        Assert.True(replica.Receive(0, new Vector3(0f, 0f, -20f), 0f, 0f, 0f));
        Assert.Equal(2, replica.Accepted);
        Assert.Equal(2, replica.Stale);
        Run(replica, 10f);
        Assert.Equal(-20f, replica.Position.Z, 1);
    }

    // A scripted motion hands the hull back where it left it; the chase resumes from there.
    [Fact]
    public void AReseatMovesTheDrawnPoseAndKeepsTheTarget()
    {
        var replica = new ZeppelinReplica(Vector3.Zero, 0f, 0f);
        replica.Receive(1, new Vector3(0f, 0f, -50f), 0f, 0f, 0f);
        replica.Reseat(new Vector3(300f, 0f, 0f), 0f, 0f);
        Assert.Equal(300f, replica.Position.X);
        Run(replica, 10f);
        Assert.Equal(0f, replica.Position.X, 1);
        Assert.Equal(-50f, replica.Position.Z, 1);
    }

    private static void Run(ZeppelinReplica replica, float seconds)
    {
        int steps = (int)Math.Round(seconds / Dt);
        for (int i = 0; i < steps; i++)
        {
            replica.Step(Dt);
        }
    }
}
