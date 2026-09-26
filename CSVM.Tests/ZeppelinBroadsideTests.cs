using System;
using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The F19 broadside law, engine-free: the decoded 90° arc (dot &gt; 0.707 on the moving hull's
/// side normal, both sides), the per-cannon stowed → deploy → ready → fire machine and its own
/// re-fire timer, the script-owned engage flag, the F18 coupling (a dead cannon drops out of the
/// volley), the decoded skip-on-no-solution rule, and the zeppelin-vs-zeppelin gasbag pick's
/// seeded determinism.
/// </summary>
public class ZeppelinBroadsideTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void ArcIsTheDirectedCone0707OnBothSides()
    {
        var hull = Vector3.Zero;
        // Yaw 0 (forward −Z): starboard is +X, port −X, ahead neither.
        Assert.Equal(BroadsideSide.Right,
            ZeppelinBroadside.TargetSide(0f, 0f, 1f, hull, new Vector3(100f, 0f, 0f)));
        Assert.Equal(BroadsideSide.Left,
            ZeppelinBroadside.TargetSide(0f, 0f, 1f, hull, new Vector3(-100f, 0f, 0f)));
        Assert.Equal(BroadsideSide.None,
            ZeppelinBroadside.TargetSide(0f, 0f, 1f, hull, new Vector3(0f, 0f, -100f)));

        // The decoded boundary: 44° off the lateral axis is inside (cos 44° = 0.719 > 0.707),
        // 46° is out (0.695), the > 0.707 gate, a 45° half-angle.
        Vector3 OffRight(float deg) => new(
            Mathf.Cos(Mathf.DegToRad(deg)) * 100f, 0f, -Mathf.Sin(Mathf.DegToRad(deg)) * 100f);
        Assert.Equal(BroadsideSide.Right, ZeppelinBroadside.TargetSide(0f, 0f, 1f, hull, OffRight(44f)));
        Assert.Equal(BroadsideSide.None, ZeppelinBroadside.TargetSide(0f, 0f, 1f, hull, OffRight(46f)));
    }

    [Fact]
    public void ArcRidesTheMovingHull()
    {
        // The hull yawed to fly +X (yaw −90°): starboard swings to +Z, and the world point
        // that was starboard at yaw 0 is now dead ahead, out of both arcs.
        float yaw = -Mathf.Pi / 2f;
        var hull = new Vector3(50f, 200f, -30f);
        Assert.Equal(BroadsideSide.Right,
            ZeppelinBroadside.TargetSide(yaw, 0f, 1f, hull, hull + new Vector3(0f, 0f, 100f)));
        Assert.Equal(BroadsideSide.Left,
            ZeppelinBroadside.TargetSide(yaw, 0f, 1f, hull, hull + new Vector3(0f, 0f, -100f)));
        Assert.Equal(BroadsideSide.None,
            ZeppelinBroadside.TargetSide(yaw, 0f, 1f, hull, hull + new Vector3(100f, 0f, 0f)));
        // Pitch alone leaves the lateral axis lateral (zeppelins never bank).
        Assert.Equal(BroadsideSide.Right, ZeppelinBroadside.TargetSide(
            0f, Mathf.DegToRad(20f), 1f, hull, hull + new Vector3(100f, 0f, 0f)));
        // A mirrored model (RightSign −1) swaps the answers, never silences them.
        Assert.Equal(BroadsideSide.Left,
            ZeppelinBroadside.TargetSide(yaw, 0f, -1f, hull, hull + new Vector3(0f, 0f, 100f)));
    }

    [Fact]
    public void StowedCannonDeploysInsteadOfFiringAndReadiesOnTheAnimDuration()
    {
        var bs = Engaged(new ZeppelinBroadside(Def(), _ => 2f, _ => 1.5f));
        var deploying = new List<ZeppelinBroadside.Cannon>();
        var ready = new List<ZeppelinBroadside.Cannon>();

        bs.Step(Dt, BroadsideSide.Right, _ => true, deploying, readyToFire: ready);
        Assert.Equal(new[] { "rb1", "rb2" }, deploying.ConvertAll(c => c.Record.Node));
        Assert.Empty(ready);   // deploy triggered INSTEAD of firing (decoded)
        Assert.All(bs.Cannons, c => Assert.Equal(
            c.Side == BroadsideSide.Right ? ZeppelinCannonState.Deploying : ZeppelinCannonState.Stowed,
            c.State));

        // Mid-deploy the cannon is skipped entirely; at the authored 2 s it becomes ready and
        // only then joins a volley.
        for (float t = Dt; t < 1.9f; t += Dt)
        {
            deploying.Clear();
            ready.Clear();
            bs.Step(Dt, BroadsideSide.Right, _ => true, deploying, readyToFire: ready);
            Assert.Empty(deploying);
            Assert.Empty(ready);
        }
        for (float t = 0f; t < 0.3f && ready.Count == 0; t += Dt)
        {
            ready.Clear();
            bs.Step(Dt, BroadsideSide.Right, _ => true, readyToFire: ready);
        }
        Assert.Equal(new[] { "rb1", "rb2" }, ready.ConvertAll(c => c.Record.Node));
    }

    [Fact]
    public void EachCannonRunsItsOwnRefireTimer()
    {
        var bs = Engaged(new ZeppelinBroadside(Def(delay: 20f), _ => 0f, _ => 0f));
        var ready = new List<ZeppelinBroadside.Cannon>();
        bs.Step(Dt, BroadsideSide.Left, _ => true, readyToFire: ready);   // instant deploy
        bs.Step(Dt, BroadsideSide.Left, _ => true, readyToFire: ready);   // deploy → ready
        ready.Clear();
        bs.Step(Dt, BroadsideSide.Left, _ => true, readyToFire: ready);
        Assert.Equal(2, ready.Count);

        // Fire ONE: only its own timer arms (per cannon, decoded), the other stays ready.
        bs.Fired(ready[0]);
        var fired = ready[0];
        ready.Clear();
        bs.Step(Dt, BroadsideSide.Left, _ => true, readyToFire: ready);
        Assert.Single(ready);
        Assert.NotSame(fired, ready[0]);

        // The fired cannon returns after cannon_fire_delay (20 s), not before.
        bs.Fired(ready[0]);
        for (float t = 0f; t < 19.5f; t += 0.5f)
        {
            ready.Clear();
            bs.Step(0.5f, BroadsideSide.Left, _ => true, readyToFire: ready);
            Assert.Empty(ready);
        }
        ready.Clear();
        bs.Step(1f, BroadsideSide.Left, _ => true, readyToFire: ready);
        Assert.Equal(2, ready.Count);
    }

    [Fact]
    public void DestroyedCannonDropsOutOfTheVolley()
    {
        var bs = Engaged(new ZeppelinBroadside(Def(), _ => 0f, _ => 0f));
        bool Alive(string node) => node != "rb1";   // F18's zone view: rb1 is destroyed
        var deploying = new List<ZeppelinBroadside.Cannon>();
        var ready = new List<ZeppelinBroadside.Cannon>();
        bs.Step(Dt, BroadsideSide.Right, Alive, deploying, readyToFire: ready);
        Assert.Equal(new[] { "rb2" }, deploying.ConvertAll(c => c.Record.Node));
        bs.Step(Dt, BroadsideSide.Right, Alive);   // deploy → ready
        ready.Clear();
        bs.Step(Dt, BroadsideSide.Right, Alive, readyToFire: ready);
        Assert.Equal(new[] { "rb2" }, ready.ConvertAll(c => c.Record.Node));
    }

    [Fact]
    public void IdleReadyCannonRetractsAfterTheInventedStowWindow()
    {
        var bs = Engaged(new ZeppelinBroadside(Def(), _ => 0f, _ => 1f));
        bs.Step(Dt, BroadsideSide.Right, _ => true);   // deploy (instant)
        bs.Step(Dt, BroadsideSide.Right, _ => true);   // ready

        var retracting = new List<ZeppelinBroadside.Cannon>();
        float idle = 0f;
        while (idle < ZeppelinBroadside.StowAfterIdleSeconds + 0.5f && retracting.Count == 0)
        {
            bs.Step(0.25f, BroadsideSide.None, _ => true, retracting: retracting);
            idle += 0.25f;
        }
        Assert.Equal(2, retracting.Count);
        Assert.True(idle >= ZeppelinBroadside.StowAfterIdleSeconds,
            $"retract fired early, at {idle:0.##} s idle");
        bs.Step(1.1f, BroadsideSide.None, _ => true);   // the 1 s retract leg completes
        Assert.All(retracting, c => Assert.Equal(ZeppelinCannonState.Stowed, c.State));
    }

    [Fact]
    public void NoInterceptSolutionMeansSkipNeverAStraightShot()
    {
        // A target outrunning the wep_28 round (350 m/s) radially: no real root, skip.
        Assert.False(ZeppelinBroadside.TryAim(Vector3.Zero, 350f,
            new Vector3(1000f, 0f, 0f), new Vector3(500f, 0f, 0f), Vector3.Zero, out _));

        // A solvable crossing target yields a unit lead direction ahead of the target.
        Assert.True(ZeppelinBroadside.TryAim(Vector3.Zero, 350f,
            new Vector3(400f, 0f, 0f), new Vector3(0f, 0f, -80f), Vector3.Zero, out var aim));
        Assert.Equal(1f, aim.Length(), 3);
        Assert.True(aim.Z < 0f, $"lead points ahead of the target's motion, aim={aim}");

        // The platform's own velocity is differenced out (relative-velocity solve): a hull
        // pacing its target sees a pure beam shot.
        Assert.True(ZeppelinBroadside.TryAim(Vector3.Zero, 350f,
            new Vector3(400f, 0f, 0f), new Vector3(0f, 0f, -80f), new Vector3(0f, 0f, -80f),
            out var paced));
        Assert.Equal(0f, paced.Z, 3);
    }

    [Fact]
    public void GasbagPickIsSeededAndDeterministic()
    {
        int[] Draw(int seed)
        {
            var rng = new Random(seed);
            var picks = new int[12];
            for (int i = 0; i < picks.Length; i++)
                picks[i] = ZeppelinBroadside.PickGasbag(5, rng);
            return picks;
        }

        Assert.Equal(Draw(7), Draw(7));
        Assert.NotEqual(Draw(7), Draw(8));
        Assert.All(Draw(7), p => Assert.InRange(p, 0, 4));
        Assert.Equal(-1, ZeppelinBroadside.PickGasbag(0, new Random(1)));
    }

    [Fact]
    public void UnauthoredTimingsFallBackToTheNamedInventedConstants()
    {
        var bs = new ZeppelinBroadside(Def(delay: null));
        Assert.Equal(ZeppelinBroadside.FallbackFireDelaySeconds, bs.FireDelaySeconds);
        Assert.All(bs.Cannons, c =>
            Assert.Equal(ZeppelinBroadside.FallbackDeploySeconds, c.DeploySeconds));
    }

    // The decoded engage flag (zeppelin byte +0xc): clear at construction, so a broadside the
    // script never engages neither deploys nor readies whatever bears on it, and one already
    // ready is retracted outright when the flag drops.
    [Fact]
    public void DisengagedBroadsideNeverDeploysAndRetractsWhatIsReady()
    {
        var bs = new ZeppelinBroadside(Def(), _ => 0f, _ => 1f);
        Assert.False(bs.CannonsEngaged);
        var deploying = new List<ZeppelinBroadside.Cannon>();
        var ready = new List<ZeppelinBroadside.Cannon>();
        for (float t = 0f; t < 25f; t += 0.5f)
        {
            bs.Step(0.5f, BroadsideSide.Right, _ => true, deploying, readyToFire: ready);
        }
        Assert.Empty(deploying);
        Assert.Empty(ready);
        Assert.All(bs.Cannons, c => Assert.Equal(ZeppelinCannonState.Stowed, c.State));

        bs.CannonsEngaged = true;
        bs.Step(Dt, BroadsideSide.Right, _ => true, deploying);   // instant deploy
        bs.Step(Dt, BroadsideSide.Right, _ => true);               // deploy → ready
        bs.Step(Dt, BroadsideSide.Right, _ => true, readyToFire: ready);
        Assert.Equal(2, deploying.Count);
        Assert.Equal(2, ready.Count);

        var retracting = new List<ZeppelinBroadside.Cannon>();
        ready.Clear();
        bs.CannonsEngaged = false;
        bs.Step(Dt, BroadsideSide.Right, _ => true, retracting: retracting, readyToFire: ready);
        Assert.Equal(2, retracting.Count);   // at once, not after the idle window
        Assert.Empty(ready);
        Assert.All(retracting, c => Assert.Equal(ZeppelinCannonState.Retracting, c.State));
    }

    // The decoded candidate walk: the original resolves every targets name through the world
    // node table, 'player' included; the engage flag above, not this walk, is the gate.
    [Fact]
    public void PlayerResolvesLikeAnyZeppelinNode()
    {
        Assert.True(ZeppelinBroadside.IsPlayerTarget("player"));
        Assert.True(ZeppelinBroadside.IsPlayerTarget("Player"));
        Assert.False(ZeppelinBroadside.IsPlayerTarget("piratezep"));
        int? hit = ZeppelinBroadside.FirstLiveTarget(new[] { "player" },
            name => ZeppelinBroadside.IsPlayerTarget(name) ? 1 : (int?)null);
        Assert.Equal(1, hit);
    }

    [Fact]
    public void FirstLiveTargetWalksAuthoredOrder()
    {
        var live = new Dictionary<string, int> { ["dantezep"] = 7, ["player"] = 1 };
        int? Resolve(string name) => live.TryGetValue(name, out var v) ? v : null;
        Assert.Equal(7, ZeppelinBroadside.FirstLiveTarget(new[] { "dantezep", "player" }, Resolve));
        Assert.Equal(1, ZeppelinBroadside.FirstLiveTarget(new[] { "player", "dantezep" }, Resolve));
    }

    [Fact]
    public void FirstLiveTargetSkipsAnUnresolvedName()
    {
        var live = new Dictionary<string, int> { ["player"] = 1 };
        int? Resolve(string name) => live.TryGetValue(name, out var v) ? v : null;
        Assert.Equal(1, ZeppelinBroadside.FirstLiveTarget(new[] { "deadzep", "player" }, Resolve));
    }

    [Fact]
    public void FirstLiveTargetIsNullWhenNothingResolves()
    {
        Assert.Null(ZeppelinBroadside.FirstLiveTarget(new[] { "deadzep" }, _ => (int?)null));
        Assert.Null(ZeppelinBroadside.FirstLiveTarget(Array.Empty<string>(), _ => (int?)1));
    }

    // The script's COMPLETED_ZEPCANNONS, stood in for: every broadside starts disengaged.
    private static ZeppelinBroadside Engaged(ZeppelinBroadside bs)
    {
        bs.CannonsEngaged = true;
        return bs;
    }

    private static ZeppelinDef Def(float? delay = 20f) => new()
    {
        Node = "testzep",
        Position = Vector3.Zero,
        Net = "TestNet",
        Targets = new[] { "player" },
        Healthy = Array.Empty<ZeppelinHealthyZone>(),
        NumHealthyRequired = 1,
        Engines = Array.Empty<string>(),
        Gasbags = Array.Empty<ZeppelinGasbag>(),
        CannonHealth = Array.Empty<ZeppelinCannonHealth>(),
        LeftCannons = new[]
        {
            new ZeppelinCannon("lb1", "deploy_lb1", "retract_lb1"),
            new ZeppelinCannon("lb2", "deploy_lb2", "retract_lb2"),
        },
        RightCannons = new[]
        {
            new ZeppelinCannon("rb1", "deploy_rb1", "retract_rb1"),
            new ZeppelinCannon("rb2", "deploy_rb2", "retract_rb2"),
        },
        CannonFireDelay = delay,
        CannonFireRange = 500f,
    };
}
