using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>C3/M01's cargo zeppelin goes down when its hydrogen tanks blow. The chain is authored
/// in animations alone: the tank's death burns four gasbag halves and calls
/// <c>cargozep1_crash</c> 35 s on, and each burn ends in a <c>finish_*</c> that switches its
/// gasbag's <c>panels</c> node off. That node is what the zeppelin's survivor walk reads, so the
/// hull dies without a single point of damage on its own pools, and a dead hull is off its net.
/// Drives the mission's own records, nets and definitions over its BUILT world.</summary>
internal static class CampaignZeppelinCrashSuites
{
    private const string Chapter = "C3";
    private const string Mission = "M01";
    private const string Hull = "cargozep1";
    private const string TankDeath = "hydrobombboom1";
    private const string Crash = "cargozep1_crash";
    private const float Tick = 1f / 30f;

    // main_altitude_check's first true-branch event, the node its downward probe is cast from
    // (the definition's node 1) and the probe's authored length (the compiled operand 3263430656
    // is the bit pattern of -65).
    private const string GateOpened = "main_altitude_check:CallSequence(rotatezep)";
    private const string ProbeNode = "gasbag5";
    private const float ProbeReachM = 65f;

    // The tank's call of the crash sits 35 s after 2.1 s of chained offsets; the burns' own
    // finish lands at 36 s. This outlasts both, the fall from the record's 300 m, and a stretch
    // of the wreck standing still afterwards.
    private const float RunSeconds = 90f;

    // A hull the net still flew would cover this in a few seconds at its 20 m/s max_speed; a
    // wreck under floatdown alone moves along no horizontal axis at all.
    private const float DriftCapM = 15f;

    [Suite("campaign-zeppelin-crash",
        "C3/M01's cargo zeppelin crashes when its hydrogen tanks blow: the tank's death starts, "
        + "the four gasbag burns switch panels off and the survivor walk kills the hull with its "
        + "own pools untouched, cargozep1_crash starts from the tank's own call, and from the "
        + "crash on the hull only sinks, it never moves off along its net again")]
    internal static void CampaignZeppelinCrash(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");

        var report = new StringBuilder();
        ctx.WithWorld(Chapter, collision: true, mission: Mission, world =>
        {
            var runtime = world.Runtime;
            uint maskWas = runtime.ContactMask;
            ZeppelinRuntime? zeps = null;
            try
            {
                // A suite world wires no contact mask, and the crash's altitude gate is a probe.
                runtime.ContactMask = CollisionLayers.World;
                runtime.SurfaceIsWater = body => ProjectilePool.SurfaceIsWater(body as Node);
                zeps = new ZeppelinRuntime(Zeppelins.Load(missionZrdr),
                    name => runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null,
                    AiNets.Load(chapterZrdr), null, runtime.Motions.DrivesTransform);
                ctx.Host.AddChild(zeps);
                zeps.WireDamage(runtime);
                Run(ctx, runtime, zeps, report);
            }
            finally
            {
                runtime.ContactMask = maskWas;
                runtime.SurfaceIsWater = null;
                zeps?.Free();
            }
        });

        ctx.WriteArtifact("test-campaign-zeppelin-crash.txt", report.ToString());
    }

    private static void Run(TestContext ctx, AnimRuntime runtime, ZeppelinRuntime zeps,
        StringBuilder report)
    {
        var host = runtime.FindNodes(Hull).FirstOrDefault();
        var tank = runtime.Destructibles.All.FirstOrDefault(i =>
            string.Equals(i.Def.AnimName, TankDeath, StringComparison.OrdinalIgnoreCase));
        ctx.Check(host != null, $"'{Hull}' resolves in the {Chapter}/{Mission} world");
        ctx.Check(tank != null, $"the tank's death '{TankDeath}' registers a damage pool");
        if (host == null || tank == null)
        {
            return;
        }

        ctx.Check(zeps.Wake(Hull), $"'{Hull}' wakes, as OBJECTIVE39 wakes it in the mission");
        var bagPools = runtime.Destructibles.All
            .Where(i => i.Gasbag && string.Equals(i.Owner, Hull, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Cruise first, so a hull the net drives is seen moving before anything happens to it.
        for (int i = 0; i < 90; i++)
        {
            runtime.Advance(Tick);
            zeps.SimStep(Tick);
        }
        var cruiseFrom = host.GlobalPosition;
        for (int i = 0; i < 90; i++)
        {
            runtime.Advance(Tick);
            zeps.SimStep(Tick);
        }
        float cruise = Flat(host.GlobalPosition - cruiseFrom);
        report.AppendLine($"cruise: {cruise:0.0} m in 3 s before the tanks blow");
        ctx.Check(cruise > 1f, $"the net flies the hull before the tanks blow ({cruise:0.0} m in 3 s)");

        var fired = new List<string>();
        runtime.OnEventDispatched = d =>
        {
            if (d.Def.AnimName is Crash)
            {
                fired.Add($"{d.Sequence}:{d.EventKind}({d.EventName})");
            }
        };

        ctx.Check(runtime.DamageAt(tank.DamageNode, tank.MaxHealth + 1f),
            $"a lethal hit lands on the tank");
        var probe = runtime.FindNodes(ProbeNode, host).FirstOrDefault();
        ctx.Check(probe != null, $"the crash's probe node '{ProbeNode}' resolves under '{Hull}'");
        float t = 0f, crashAt = -1f, deadAt = -1f, gateAt = -1f, gateY = float.NaN, probeY = float.NaN;
        Vector3 crashPos = default;
        float maxDrift = 0f, lowest = host.GlobalPosition.Y;
        bool everBoom = false;
        for (int i = 0; i < (int)(RunSeconds / Tick); i++)
        {
            runtime.Advance(Tick);
            zeps.SimStep(Tick);
            // A real session's physics bodies follow the hull; one engine frame's do not, and
            // colliders left where the world built them hide what the crash's own probe sees.
            SyncColliders(host);
            t += Tick;
            if (gateAt < 0f && fired.Contains(GateOpened))
            {
                gateAt = t;
                gateY = host.GlobalPosition.Y;
                probeY = probe?.GlobalPosition.Y ?? gateY;
            }
            everBoom |= runtime.AnimStateOf(TankDeath) is 2 or 3 or 4;
            if (deadAt < 0f && zeps.IsDead(Hull))
            {
                deadAt = t;
            }
            if (crashAt < 0f && runtime.AnimStateOf(Crash) == 2)
            {
                crashAt = t;
                crashPos = host.GlobalPosition;
            }
            if (crashAt >= 0f)
            {
                maxDrift = Mathf.Max(maxDrift, Flat(host.GlobalPosition - crashPos));
                lowest = Mathf.Min(lowest, host.GlobalPosition.Y);
            }
            if (i % 150 == 0)
            {
                var p = host.GlobalPosition;
                report.AppendLine($"t={t:0.0} hull ({p.X:0},{p.Y:0},{p.Z:0}) dead={zeps.IsDead(Hull)} " +
                    $"survivors={zeps.SurvivorsOf(Hull)} crash={runtime.AnimStateOf(Crash)}");
            }
        }
        runtime.OnEventDispatched = null;

        int poolsDestroyed = bagPools.Count(i => i.Status == DestructibleRegistry.State.Destroyed);
        report.AppendLine($"tank death ran={everBoom}; hull dead at {deadAt:0.0} s; crash started at " +
            $"{crashAt:0.0} s from ({crashPos.X:0},{crashPos.Y:0},{crashPos.Z:0}); max horizontal drift " +
            $"after it {maxDrift:0.0} m; lowest y {lowest:0}; gasbag pools destroyed {poolsDestroyed} of {bagPools.Count}; " +
            $"altitude gate opened at {gateAt:0.0} s with the hull at y={gateY:0} and '{ProbeNode}' at y={probeY:0}");
        foreach (string line in fired.Distinct())
        {
            report.AppendLine($"{Crash} {line}");
        }

        ctx.Check(everBoom, $"the hit starts the tank's own death '{TankDeath}'");
        ctx.Check(deadAt > 0f,
            $"the burns' finish_* switch gasbag panels off and the survivor walk kills '{Hull}' at {deadAt:0.0} s");
        ctx.Same(0, poolsDestroyed,
            $"…with no gasbag pool of its own destroyed, so the kill is the node flag and not damage");
        ctx.Check(crashAt > 0f, $"the tank's delayed call starts '{Crash}' at {crashAt:0.0} s");
        ctx.Check(fired.Contains("floatdown:ObjectMotion(cargozep1)"),
            $"…whose floatdown takes the hull's own transform");
        ctx.Check(lowest < crashPos.Y - 50f,
            $"the wreck sinks under floatdown ({crashPos.Y:0} -> {lowest:0} m)");
        // The probe is 65 m down from the hull. Its own gasbags hang under that origin, and a
        // probe that sees them opens the gate at cruise altitude: the splash plays in the air
        // and the stop on floatdown freezes the wreck where the burns left it.
        ctx.Check(gateAt > 0f && probeY <= ProbeReachM + 10f,
            $"main_altitude_check opens only once the sea is inside its {ProbeReachM:0} m probe, not at the hull's own colliders: at {gateAt:0.0} s with '{ProbeNode}' at y={probeY:0} (hull y={gateY:0})");
        ctx.Check(maxDrift < DriftCapM,
            $"and never moves off along its net again: {maxDrift:0.0} m horizontal from the crash start, cap {DriftCapM:0}");
        ctx.Note($"'{Hull}' dead at {deadAt:0.0} s, crash at {crashAt:0.0} s, drift {maxDrift:0.0} m, sank to y={lowest:0}");
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    // A suite runs its ticks inside one engine frame, so a StaticBody3D's physics-server transform
    // never follows its moving node. A real session's does.
    private static void SyncColliders(Node n)
    {
        if (n is CollisionObject3D body)
        {
            PhysicsServer3D.BodySetState(body.GetRid(), PhysicsServer3D.BodyState.Transform, body.GlobalTransform);
        }

        foreach (var child in n.GetChildren())
        {
            SyncColliders(child);
        }
    }
}
