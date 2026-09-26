using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The F17 zeppelin motion law, engine-free: the decoded sqrt engine-loss curve (never the
/// design's 10/40/50 bands), the record's rate/speed/pitch limits enforced per step, the
/// verbatim (unclamped) initial pitch, the decoded steer law (quadratic ease inside 25°, way
/// on to turn, no per-step band), and a net flown with every hop on the edge list.
/// </summary>
public class ZeppelinMotionTests
{
    private const float Dt = 1f / 60f;

    [Theory]
    [InlineData(4, 4, 1f)]
    [InlineData(2, 4, 0.70710678f)]
    [InlineData(1, 4, 0.5f)]
    [InlineData(0, 4, 0f)]
    public void TheEngineLossCurveIsTheDecodedSquareRoot(int alive, int total, float expected)
    {
        Assert.Equal(expected, ZeppelinMotion.EngineFactor(alive, total), 5);
    }

    [Fact]
    public void EngineFactorClampsOutOfRangeCounts()
    {
        Assert.Equal(1f, ZeppelinMotion.EngineFactor(9, 4));
        Assert.Equal(0f, ZeppelinMotion.EngineFactor(-1, 4));
        Assert.Equal(1f, ZeppelinMotion.EngineFactor(0, 0)); // no engines list: unscaled
    }

    [Fact]
    public void EngineLossScalesSpeedToZeroButAccelKeepsTheTwentyPercentFloor()
    {
        var m = NewMotion(Def(), SquareNet());
        Assert.Equal(4, m.TotalEngines);
        Assert.Equal(15f, m.EffectiveMaxSpeed);

        m.AliveEngines = 2;   // The damage side writes the surviving count.
        Assert.Equal(15f * 0.70710678f, m.EffectiveMaxSpeed, 3);
        Assert.Equal(((0.8f * 0.70710678f) + 0.2f) * 4.47f, m.EffectiveMaxAccel, 3);

        m.AliveEngines = 0;
        Assert.Equal(0f, m.EffectiveMaxSpeed);
        Assert.Equal(0.2f * 4.47f, m.EffectiveMaxAccel, 4);
    }

    [Fact]
    public void TotalEngineLossDeceleratesTheZeppelinToAStop()
    {
        var m = NewMotion(Def(), SquareNet());
        for (int i = 0; i < 60 * 30 && m.Speed < 14.9f; i++)
        {
            m.Step(Dt);
        }
        Assert.True(m.Speed > 14.5f, $"cruise never reached: {m.Speed}");

        m.AliveEngines = 0;
        float before = m.Speed;
        m.Step(Dt);
        Assert.True(m.Speed < before, "no deceleration after total engine loss");
        for (int i = 0; i < 60 * 60 && m.Speed > 0f; i++)
        {
            m.Step(Dt);
        }
        Assert.Equal(0f, m.Speed);
    }

    [Fact]
    public void SpeedAndAccelerationStayInsideTheRecordsLimits()
    {
        var m = NewMotion(Def(), SquareNet());
        float last = 0f;
        for (int i = 0; i < 60 * 60; i++)
        {
            m.Step(Dt);
            Assert.True(m.Speed <= m.Def.MaxSpeed + 1e-3f, $"speed {m.Speed} over max");
            Assert.True(Math.Abs(m.Speed - last) <= (m.Def.MaxAccel * Dt) + 1e-4f,
                $"accel over max at step {i}");
            last = m.Speed;
        }
    }

    [Fact]
    public void TheYawRateNeverExceedsMaxRateYaw()
    {
        // Spawn facing away from the first node so the law has a big turn to make.
        var def = Def(yawDeg: 180f);
        var m = NewMotion(def, SquareNet());
        float maxRate = Mathf.DegToRad(def.MaxRateYawDeg);
        float last = m.YawRad;
        for (int i = 0; i < 60 * 60; i++)
        {
            m.Step(Dt);
            float delta = Mathf.Abs(Mathf.AngleDifference(last, m.YawRad));
            Assert.True(delta <= (maxRate * Dt) + 1e-4f, $"yaw rate over limit at step {i}");
            last = m.YawRad;
        }
    }

    [Fact]
    public void ThePitchFollowsTheLegSlopeUnboundedByTheRecordsBand()
    {
        // A climb of 2000 m over 2000 m horizontal asks for 45° of pitch. The record's ±30 band
        // never bounds it (the original's is in degrees and clamps only the drawn pose), so the
        // law settles ON the slope, from below, without overshooting it.
        var net = Net(new Vector3(0f, 400f, 0f), new Vector3(0f, 2400f, -2000f));
        var m = NewMotion(Def(), net);
        float slope = Mathf.DegToRad(45f);
        float peak = 0f;
        for (int i = 0; i < 60 * 120; i++)
        {
            m.Step(Dt);
            peak = Mathf.Max(peak, m.PitchRad);
            Assert.True(m.PitchRad <= slope + Mathf.DegToRad(1f), $"pitch overshot the slope at step {i}");
        }
        Assert.True(peak > Mathf.DegToRad(35f), $"never pitched past the old band: {Mathf.RadToDeg(peak):0.#}°");
        Assert.True(m.Position.Y > 500f, $"never climbed: y={m.Position.Y}");
    }

    [Theory]
    [InlineData(25f, 5f)]
    [InlineData(90f, 5f)]
    [InlineData(12.5f, 1.25f)]
    [InlineData(-12.5f, -1.25f)]
    [InlineData(5f, 0.2f)]
    public void TheSteerLawEasesQuadraticallyInsideTwentyFiveDegrees(float errorDeg, float rateDeg)
    {
        // The decoded rate command: the full max_rate at 25° of error and beyond, and
        // max_rate·(error/25°)² inside it, sign kept, with the accel limit out of the way.
        float rate = ZeppelinMotion.Steer(0f, Mathf.DegToRad(errorDeg), Dt, Mathf.DegToRad(5f), 1e6f);
        Assert.Equal(rateDeg, Mathf.RadToDeg(rate), 3);
    }

    [Fact]
    public void TheRateCommandIsReachedThroughTheAccelLimit()
    {
        float rate = ZeppelinMotion.Steer(0f, Mathf.Pi, Dt, Mathf.DegToRad(5f), Mathf.DegToRad(0.5f));
        Assert.Equal(0.5f * Dt, Mathf.RadToDeg(rate), 4);
    }

    [Fact]
    public void TurningNeedsWayOnSoAStoppedHullHoldsItsPose()
    {
        // Both rates scale by speed over the authored max_speed: with no engines there is no
        // speed, and a 180° heading error moves the nose not at all.
        var m = NewMotion(Def(yawDeg: 180f), SquareNet());
        m.AliveEngines = 0;
        for (int i = 0; i < 60 * 30; i++)
        {
            m.Step(Dt);
        }
        Assert.Equal(0f, m.Speed);
        Assert.Equal(Mathf.DegToRad(180f), Mathf.Abs(m.YawRad), 4);
    }

    [Fact]
    public void ALevelRouteSeatedAboveItsNodesNeverPorpoises()
    {
        // The CM08 shape: level legs with gentle steps, the record seated 50 m above its first
        // node. The old bang-bang rate under accel_pitch 0.5°/s² rang up into a standing ±30°
        // oscillation here; the decoded ease keeps the pitch within the steepest leg's slope.
        var net = Net(
            new Vector3(0f, 150f, 0f), new Vector3(-1400f, 150f, 200f),
            new Vector3(-1700f, 150f, 900f), new Vector3(-1800f, 100f, 2100f),
            new Vector3(-1800f, 150f, 2700f), new Vector3(-1700f, 200f, 3300f),
            new Vector3(-1200f, 260f, 3700f), new Vector3(-300f, 330f, 3800f));
        var def = Def(yawDeg: 80f, position: new Vector3(0f, 200f, 0f));
        var follower = new AiNetFollower(net, new Random(1), 500f, observesStopPoints: true);
        var m = new ZeppelinMotion(def, follower);
        float worst = 0f;
        for (int i = 0; i < 60 * 600; i++)
        {
            m.Step(Dt);
            worst = Mathf.Max(worst, Mathf.Abs(m.PitchRad));
        }
        Assert.True(worst < Mathf.DegToRad(8f), $"pitch reached {Mathf.RadToDeg(worst):0.#}° on a route whose steepest leg is 6.3°");
        Assert.True(follower.Advances >= 6, $"only {follower.Advances} captures");
    }

    [Fact]
    public void ResumeAtReseatsThePoseAndZeroesTheRatesAndSpeed()
    {
        var m = NewMotion(Def(), SquareNet());
        for (int i = 0; i < 60 * 30; i++)
        {
            m.Step(Dt);
        }
        Assert.True(m.Speed > 10f);
        m.AliveEngines = 2;
        m.ResumeAt(new Vector3(50f, 620f, -80f), Mathf.DegToRad(135f), Mathf.DegToRad(-4f));
        Assert.Equal(new Vector3(50f, 620f, -80f), m.Position);
        Assert.Equal(Mathf.DegToRad(135f), m.YawRad, 4);
        Assert.Equal(Mathf.DegToRad(-4f), m.PitchRad, 4);
        Assert.Equal(0f, m.Speed);
        Assert.Equal(2, m.AliveEngines);   // engines as they stand, never re-read from the record
        // No rate survives the re-seat: the first step from rest turns not at all.
        m.Step(Dt);
        Assert.Equal(Mathf.DegToRad(135f), m.YawRad, 4);
        Assert.Equal(Mathf.DegToRad(-4f), m.PitchRad, 4);
    }

    [Fact]
    public void TheAuthoredInitialPitchIsTakenVerbatimNeverLoadClamped()
    {
        // The original's load-time clamp compares radians against raw degrees and never fires
        // (the unit bug); we deliberately do not reproduce it as a working clamp, so an
        // out-of-band authored pitch arrives verbatim.
        var m = NewMotion(Def(pitchDeg: 45f), SquareNet());
        Assert.Equal(Mathf.DegToRad(45f), m.PitchRad, 5);
    }

    [Fact]
    public void TheNetIsFlownNodeToNodeAlongTheEdgeList()
    {
        var net = SquareNet();
        var follower = new AiNetFollower(net, new Random(1), 250f);
        var m = new ZeppelinMotion(Def(), follower);
        var hops = new List<(int From, int To)>();
        int last = -1;
        int steps = 0;
        while (follower.Advances < 4 && steps < 60 * 600)
        {
            steps++;
            m.Step(Dt);
            if (follower.CurrentIndex != last)
            {
                if (last >= 0)
                {
                    hops.Add((last, follower.CurrentIndex));
                }
                last = follower.CurrentIndex;
            }
        }
        Assert.True(follower.Advances >= 4, $"only {follower.Advances} captures in {steps} steps");
        foreach (var (from, to) in hops)
        {
            Assert.True(net.Edges.Contains((from, to)) || net.Edges.Contains((to, from)),
                $"hop {from}->{to} is not an edge");
        }
    }

    [Fact]
    public void AnArmedStopPointBringsTheZeppelinDownToAHoldOnTheNode()
    {
        // A straight 4 km run to an armed stop point: the throttle rides max until 250 m out,
        // ramps down through it, and the hull parks on the node rather than flowing past it.
        var def = Def();
        var net = new AiNet
        {
            Id = 1,
            Name = "TestNet",
            Nodes = new[]
            {
                new AiNetNode(new Vector3(0f, 400f, 0f), Array.Empty<float>()),
                new AiNetNode(new Vector3(0f, 400f, -4000f), new[] { 1f, 1f }),
            },
            Edges = new[] { (0, 1) },
        };
        var m = new ZeppelinMotion(def, new AiNetFollower(net, new Random(1), 250f,
            observesStopPoints: true));

        float atFullSpeed = 0f;
        for (int i = 0; i < 60 * 900 && !m.Follower.Holding; i++)
        {
            m.Step(Dt);
            float range = (m.Follower.CurrentTarget - m.Position).Length();
            if (range > 400f)
            {
                atFullSpeed = m.Speed;
            }
            else if (range is > 60f and < 200f)
            {
                Assert.InRange(m.Speed, 0f, m.EffectiveMaxSpeed * 0.85f);
            }
        }

        Assert.Equal(15f, atFullSpeed, 1);   // untouched outside the ramp
        Assert.True(m.Follower.Holding);
        Assert.InRange((m.Follower.CurrentTarget - m.Position).Length(), 0f,
            AiNetFollower.StopPointHoldM);

        // Held: the last of the speed bleeds off, then it stays put and levels off, and the
        // walk never advances.
        for (int i = 0; i < 60 * 60; i++)
        {
            m.Step(Dt);
        }
        Assert.Equal(0f, m.Speed);
        var parked = m.Position;
        for (int i = 0; i < 60 * 60; i++)
        {
            m.Step(Dt);
        }
        Assert.InRange(parked.DistanceTo(m.Position), 0f, 0.01f);
        Assert.Equal(0f, m.PitchRad, 3);
        Assert.Equal(1, m.Follower.CurrentIndex);
    }

    [Fact]
    public void AnUnarmedDeadEndHoldsAndLevelsInsteadOfPorpoisingForever()
    {
        // CM08's Klondike1 shape: an open route with a mid-route dip and no stop point
        // at its far, level node. Without the structural dead-end hold, the follower
        // re-picks its only neighbour and shuttles the dip back and forth forever.
        var def = Def();
        var net = new AiNet
        {
            Id = 1,
            Name = "TestNet",
            Nodes = new[]
            {
                new AiNetNode(new Vector3(0f, 400f, 0f), Array.Empty<float>()),
                new AiNetNode(new Vector3(2000f, 400f, 800f), Array.Empty<float>()),
                new AiNetNode(new Vector3(4000f, 400f, 0f), Array.Empty<float>()), // unarmed far end
            },
            Edges = new[] { (0, 1), (1, 2) },
        };
        var m = new ZeppelinMotion(def, new AiNetFollower(net, new Random(1), 250f,
            observesStopPoints: true));

        int steps = 0;
        while (!m.Follower.Holding && steps < 60 * 900)
        {
            m.Step(Dt);
            steps++;
            // A level route: the pitch never leaves the few degrees the seat offset asks for.
            Assert.True(Mathf.Abs(m.PitchRad) <= Mathf.DegToRad(8f),
                $"pitch rang up at step {steps}: {Mathf.RadToDeg(m.PitchRad):0.#}");
        }
        Assert.True(m.Follower.Holding, $"never held in {steps / 60f:0} s");
        Assert.Equal(2, m.Follower.CurrentIndex); // the far end, not a re-picked node 1

        // Once reached the walk goes no further and the throttle stays cut, whatever the
        // rate-limited pitch/yaw law is still settling (its own band compliance is
        // ThePitchBandHoldsOnASteepClimbTarget's).
        for (int i = 0; i < 60 * 30; i++)
        {
            m.Step(Dt);
            Assert.Equal(2, m.Follower.CurrentIndex); // held, never shuttled back toward node 0
        }
        Assert.Equal(0f, m.Speed);
    }

    [Fact]
    public void AnArmedStopAheadSettlesTheHullOnItsNodeNotOnTheHoldSphere()
    {
        // The hold latches on the 30 m sphere; the dock glide is what carries the hull the rest
        // of the way onto the node, in altitude as well as plan. CM08's Pandora needs it: its
        // cargo point is where a 54 m crane chain has to reach the freighter's deck.
        var def = Def(position: new Vector3(0f, 500f, 300f));
        var net = new AiNet
        {
            Id = 1,
            Name = "TestNet",
            Nodes = new[]
            {
                new AiNetNode(new Vector3(0f, 400f, 0f), Array.Empty<float>()),
                new AiNetNode(new Vector3(0f, 300f, -4000f), new[] { 1f, 1f }),
            },
            Edges = new[] { (0, 1) },
        };
        var m = new ZeppelinMotion(def, new AiNetFollower(net, new Random(1), 250f,
            observesStopPoints: true));

        for (int i = 0; i < 60 * 900 && !m.Follower.Holding; i++)
        {
            m.Step(Dt);
        }
        Assert.True(m.Follower.Holding, "never reached the armed stop");
        Assert.Equal(1, m.Follower.CurrentIndex);

        for (int i = 0; i < 60 * 40; i++)
        {
            m.Step(Dt);
        }
        Assert.InRange(m.Position.DistanceTo(net.Nodes[1].Position), 0f, 0.5f);
        Assert.Equal(net.Nodes[1].Position.Y, m.Position.Y, 1);
    }

    [Fact]
    public void TheDockGlideLevelsAHullThatArrivedPitchedAtItsStopPoint()
    {
        // A 900 m descent onto an armed stop, so the hull arrives well nose-down. The dock glide
        // decays the pitch toward level at the same rate as the position and the heading, outside
        // the speed factor, so the parked airship does not keep its approach attitude.
        var def = Def(position: new Vector3(0f, 1200f, 300f));
        var net = new AiNet
        {
            Id = 1,
            Name = "TestNet",
            Nodes = new[]
            {
                new AiNetNode(new Vector3(0f, 1200f, 0f), Array.Empty<float>()),
                new AiNetNode(new Vector3(0f, 300f, -1500f), new[] { 1f, 1f }),
            },
            Edges = new[] { (0, 1) },
        };
        var m = new ZeppelinMotion(def, new AiNetFollower(net, new Random(1), 250f,
            observesStopPoints: true));

        for (int i = 0; i < 60 * 900 && !m.Follower.Holding; i++)
        {
            m.Step(Dt);
        }
        Assert.True(m.Follower.Holding, "never reached the armed stop");
        Assert.True(Mathf.Abs(m.PitchRad) > Mathf.DegToRad(10f),
            $"arrived too level to test the glide: {Mathf.RadToDeg(m.PitchRad):0.#}");

        for (int i = 0; i < 60 * 40; i++)
        {
            m.Step(Dt);
        }
        Assert.InRange(Mathf.RadToDeg(m.PitchRad), -0.5f, 0.5f);
        Assert.InRange(m.Position.DistanceTo(net.Nodes[1].Position), 0f, 0.5f);
    }

    [Fact]
    public void AZeppelinSeatedOnItsOwnArmedNodeHoldsWhereTheRecordPutIt()
    {
        // The other half of the decoded pair: the node the hull already sits on halts, so it
        // holds station where it stands. Nothing draws it onto the node, or C1/M04's Pandora
        // would slide off the pose its record authored the moment the mission started.
        var def = Def(position: new Vector3(0f, 400f, 20f));
        var net = new AiNet
        {
            Id = 1,
            Name = "TestNet",
            Nodes = new[]
            {
                new AiNetNode(new Vector3(0f, 400f, 0f), new[] { 1f, 1f }),
                new AiNetNode(new Vector3(0f, 400f, -4000f), Array.Empty<float>()),
            },
            Edges = new[] { (0, 1) },
        };
        var m = new ZeppelinMotion(def, new AiNetFollower(net, new Random(1), 250f,
            observesStopPoints: true));

        for (int i = 0; i < 60 * 60; i++)
        {
            m.Step(Dt);
        }
        Assert.True(m.Follower.Holding);
        Assert.Equal(0, m.Follower.CurrentIndex);
        Assert.InRange(m.Position.DistanceTo(def.Position), 0f, 0.01f);
    }

    private static ZeppelinDef Def(float yawDeg = 0f, float pitchDeg = 0f, Vector3? position = null) => new()
    {
        Node = "testzep",
        Position = position ?? new Vector3(0f, 400f, 300f),
        YawDeg = yawDeg,
        PitchDeg = pitchDeg,
        MaxSpeed = 15f,
        MaxAccel = 4.47f,
        AccelPitchDeg = 0.5f,
        AccelYawDeg = 1.5f,
        MaxRateYawDeg = 15f,
        MaxRatePitchDeg = 5f,
        MinPitchDeg = -30f,
        MaxPitchDeg = 30f,
        Net = "TestNet",
        Targets = Array.Empty<string>(),
        Healthy = Array.Empty<ZeppelinHealthyZone>(),
        NumHealthyRequired = 1,
        Engines = new[] { "leng1", "leng2", "reng1", "reng2" },
        Gasbags = Array.Empty<ZeppelinGasbag>(),
        LeftCannons = Array.Empty<ZeppelinCannon>(),
        RightCannons = Array.Empty<ZeppelinCannon>(),
        CannonHealth = Array.Empty<ZeppelinCannonHealth>(),
    };

    private static ZeppelinMotion NewMotion(ZeppelinDef def, AiNet net) =>
        new(def, new AiNetFollower(net, new Random(1), 250f));

    // A 1 km square patrol ring at the def's altitude.
    private static AiNet SquareNet() => Net(
        new Vector3(0f, 400f, 0f), new Vector3(1000f, 400f, 0f),
        new Vector3(1000f, 400f, 1000f), new Vector3(0f, 400f, 1000f));

    private static AiNet Net(params Vector3[] positions)
    {
        var nodes = new List<AiNetNode>();
        foreach (var p in positions)
        {
            nodes.Add(new AiNetNode(p, Array.Empty<float>()));
        }
        var edges = new List<(int A, int B)>();
        for (int i = 0; i < nodes.Count; i++)
        {
            edges.Add((i, (i + 1) % nodes.Count));
        }
        if (nodes.Count == 2)
        {
            edges.RemoveAt(1); // a two-node net needs one edge, not a doubled pair
        }
        return new AiNet { Id = 1, Name = "TestNet", Nodes = nodes, Edges = edges };
    }
}
