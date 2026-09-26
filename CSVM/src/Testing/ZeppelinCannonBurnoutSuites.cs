using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using Godot;

namespace CSVM.Testing;

/// <summary>The animation-authored zeppelin death: C1/M04's <c>hk_zep</c> is no zeppelin record,
/// so its whole kill runs through the compiled mission defs. A broadside cannon's WeaponHit death
/// calls its gasbag's burn, the burn calls the gasbag's finisher (gated on the burnt panels being
/// OFF), three finishers satisfy <c>finish_locklear</c>, and that sinks the hull and completes the
/// mission's primary. Every link is asserted on the real world with real rounds.</summary>
internal static class ZeppelinCannonBurnoutSuites
{
    private const string Hull = "hk_zep";
    private const float Dt = 1f / 60f;

    // The step for the authored burn chains once the last real round has landed: nothing there is
    // a projectile or a flight model, only definition timers and the switches they throw, which
    // land within a quarter second of a frame-stepped run at a fifteenth of the cost.
    private const float WaitDt = 0.25f;

    // The doors the suite kills, each with the gasbag its death burns and that gasbag's finisher.
    private static readonly (string Door, string Burn, string Finisher)[] Doors =
    {
        ("lbroad4", "left_lkgasbag04", "finished_lkgasbag04"),
        ("lbroad3", "left_lkgasbag03", "finished_lkgasbag03"),
        ("lbroad2", "left_lkgasbag02", "finished_lkgasbag02"),
    };

    // The Dante's ring on gasbag1: the cannon the suite kills, then the three the burns destroy.
    private static readonly string[] DanteRing = { "lbroad11", "lbroad12", "rbroad11", "rbroad12" };

    [Suite("zeppelin-cannon-burnout",
        "the animation-authored zeppelin kill on C1/M04's hk_zep (no zeppelin record): its " +
        "sabotaged broadside doors are deployed from t=0, real gun rounds destroy one door and " +
        "DamageAt two more, each door's WeaponHit death calls its gasbag burn, the burn " +
        "switches the panels off and calls the finisher whose REQUIRED OBJECT_INACTIVE_LIST " +
        "prerequisite reads those panels (compiled active_raw 2 = inactive, bit 1 is the " +
        "local-nodes scope), three finishers satisfy finish_locklear, lockleargoesdown brings " +
        "the hull down and the primary completes off lkgasbag05/panelleft1")]
    internal static void ZeppelinCannonBurnout(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "M04");
        ctx.RequireData(missionZrdr, $"C1/M04 zrdr");
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var gun = weapons.All.FirstOrDefault(w => w.IsGun && w.HealthDamage is > 0f && w.ImpactProximity is not > 0f)
            ?? weapons.All.FirstOrDefault(w => w.IsGun && w.HealthDamage is > 0f);
        ctx.Check(gun != null, $"a gun with HEALTH_DAMAGE ships");
        if (gun == null)
            return;
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        var mission = missions.Where(m =>
            m.ChapterFolder.Equals("C1", System.StringComparison.OrdinalIgnoreCase)
            && m.MissionFolder.Equals("M04", System.StringComparison.OrdinalIgnoreCase))
            .Cast<CampaignMission?>().FirstOrDefault()
            ?? throw new SuiteSkippedException("C1/M04 is not in cm_sequence");
        var script = ObjectiveScript.Load(missionZrdr);
        var report = new StringBuilder();

        ctx.WithWorld("C1", collision: true, mission: "M04", world =>
        {
            var runtime = world.Runtime;
            var hull = runtime.FindNodes(Hull).FirstOrDefault();
            ctx.Check(hull != null, $"the {Hull} world node resolves in the M04 world");
            if (hull == null)
                return;

            var started = new List<string>();
            var startedAt = new List<string>();
            float clock = 0f;
            var saved = runtime.OnInstanceStarted;
            runtime.OnInstanceStarted = (d, _) =>
            {
                if (d.AnimName == null)
                    return;
                started.Add(d.AnimName);
                startedAt.Add($"{d.AnimName}@{clock:0.0}");
            };
            ProjectilePool? pool = null;
            TextureArchive? textures = null;
            try
            {
                var director = CampaignDirector.Create(script, mission, CampaignProfileDef.NewProfile("Suite"), null);
                director.Attach(new CampaignDirector.WorldInputs { Runtime = runtime });
                var graph = director.Graph!;
                var done = new List<int>();
                graph.Completed += e => done.Add(e.Number);
                int primary = script.Objectives.First(o => o.Identity?.Class == ObjectiveClass.Primary).Number;
                ctx.Check(graph.StateOf(primary) == ObjectiveState.Awake,
                    $"OBJECTIVE{primary}, the primary, is awake from the start");

                // The sabotaged doors: authored open from t=0 by the OnStartup temp_hkzep_* defs,
                // which call the deploy (the hatch swings 135 degrees over its 4 s) with no
                // broadside AI and no engage flag behind it.
                for (int i = 0; i < 300; i++)
                {
                    runtime.Advance(Dt);
                    graph.Step(Dt);
                }
                report.AppendLine($"{Hull} visible={hull.Visible} inTree={hull.IsVisibleInTree()} at {hull.GlobalPosition}");
                foreach (var (door, _, _) in Doors)
                {
                    var doorNode = runtime.FindNodes(door, hull).FirstOrDefault();
                    var hatch = runtime.FindNodes("upper_br_door", doorNode).FirstOrDefault();
                    report.AppendLine($"{door} visible={doorNode?.Visible} hatch visible={hatch?.Visible} rot={hatch?.Rotation}");
                    ctx.Check(hatch != null && hatch.IsVisibleInTree() && Mathf.Abs(hatch.Rotation.Z + 2.356f) < 0.05f,
                        $"{door}'s hatch is deployed from the start with nobody engaging it (rot={hatch?.Rotation.Z ?? 0f:0.###})");
                }

                float hullY = hull.GlobalPosition.Y;
                textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, "C1"));
                pool = new ProjectilePool(textures, null, null) { DamageSink = runtime.DamageAt };
                ctx.Host.AddChild(pool);

                // Door 1 dies to real gun rounds; the other two take the same DamageAt a rocket
                // makes, since the chain under test is the death's, not the round's.
                var first = runtime.FindNodes(Doors[0].Door, hull).FirstOrDefault();
                var firstPool = first != null ? runtime.Destructibles.PoolsOn(first).FirstOrDefault() : null;
                ctx.Check(firstPool != null && Mathf.IsEqualApprox(firstPool.MaxHealth, 60f),
                    $"{Doors[0].Door} carries its compiled HEALTH 60 pool hp={firstPool?.MaxHealth ?? -1f}");
                if (first == null || firstPool == null)
                    return;
                // From abeam, 40 m out on the door's own side of the hull, so the round meets the
                // cannon frame rather than the gasbag above it.
                var target = runtime.FindNodes("frame", first).FirstOrDefault() ?? first;
                int rounds = 0;
                for (int i = 0; i < 2400 && firstPool.Status != DestructibleRegistry.State.Destroyed; i++)
                {
                    if (i % 4 == 0 && rounds < 400)
                    {
                        var at = target.GlobalPosition;
                        var outward = at - hull.GlobalPosition;
                        outward.Y = 0f;
                        var muzzlePos = at + outward.Normalized() * 40f;
                        var muzzle = new Transform3D(Basis.LookingAt((at - muzzlePos).Normalized(), Vector3.Right), muzzlePos);
                        pool.Spawn(gun, muzzle, Vector3.Zero);
                        rounds++;
                    }
                    pool.SimStep(Dt);
                    runtime.Advance(Dt);
                    graph.Step(Dt);
                }
                ctx.Check(firstPool.Status == DestructibleRegistry.State.Destroyed,
                    $"{gun.Id} rounds destroy {Doors[0].Door} rounds={rounds} hp={firstPool.Health:0.##}");
                report.AppendLine($"{Doors[0].Door} destroyed by {rounds} {gun.Id} round(s)");
                foreach (var (door, _, _) in Doors.Skip(1))
                {
                    var node = runtime.FindNodes(door, hull).FirstOrDefault();
                    var doorPool = node != null ? runtime.Destructibles.PoolsOn(node).FirstOrDefault() : null;
                    ctx.Check(doorPool != null, $"{door} carries a pool");
                    if (doorPool != null)
                        runtime.DamageAt(node, doorPool.MaxHealth + 1f);
                }

                // The chain: door death (+1 s) burn (+6 s) finisher, three finishers satisfy the
                // hull's death (min 3 of 4), which sinks the hull and switches lkgasbag05's
                // panelleft1 off, the primary's INACTIVE1 read. Stepped to the last link plus 2 s.
                bool primaryDone = false;
                float doneAt = -1f;
                float settledAt = -1f;
                for (int i = 0; i < 90f / WaitDt; i++)
                {
                    clock += WaitDt;
                    runtime.Advance(WaitDt);
                    graph.Step(WaitDt);
                    if (!primaryDone && done.Contains(primary))
                    {
                        primaryDone = true;
                        doneAt = clock;
                    }
                    if (settledAt < 0f && primaryDone && started.Contains("lockleargoesdown")
                        && hullY - hull.GlobalPosition.Y > 5f)
                    {
                        settledAt = clock;
                    }
                    if (settledAt >= 0f && clock >= settledAt + 2f)
                        break;
                }
                foreach (var (door, burn, finisher) in Doors)
                {
                    ctx.Check(started.Contains($"destroy_hkzep_{door}"), $"{door}'s death def ran");
                    ctx.Check(started.Contains(burn), $"{door}'s death called its gasbag burn {burn}");
                    ctx.Check(started.Contains(finisher),
                        $"{burn} called {finisher}, whose panels-OFF prerequisite is met once the burn switched them off");
                }
                ctx.Check(started.Contains("finish_locklear"),
                    $"three finishers satisfy finish_locklear (min 3 of 4)");
                ctx.Check(started.Contains("lockleargoesdown"), $"finish_locklear sinks the hull");
                float dropped = hullY - hull.GlobalPosition.Y;
                ctx.Check(dropped > 5f, $"the hull comes down {dropped:0.#} m");
                ctx.Check(primaryDone, $"OBJECTIVE{primary} completes off lkgasbag05/panelleft1 going inactive at {doneAt:0.#} s");
                report.AppendLine($"started: {string.Join(", ", startedAt)}");
                report.AppendLine($"hull dropped {dropped:0.#} m, primary {(primaryDone ? $"completed at {doneAt:0.#} s" : "NOT completed")}");
            }
            finally
            {
                runtime.OnInstanceStarted = saved;
                pool?.Free();
                textures?.Dispose();
            }
        });
        ctx.WriteArtifact("test-zeppelin-cannon-burnout.txt", report.ToString());
        ctx.Note($"C1/M04's {Hull}: three door deaths burn three gasbags, the hull sinks and the primary completes");
    }

    [Suite("dante-cannon-burnout",
        "C5/M04's Dante among nine other zeppelins sharing its node names: one broadside cannon " +
        "killed through its pool calls both burns of its gasbag, each burn destroys the ring's " +
        "other cannons (their pools read Destroyed, so the broadside drops them) and the engines " +
        "behind it, and the finisher starts on the Dante's OWN panels, bound through the " +
        "prerequisite's compiled node pointer rather than a name search that met the other " +
        "zeppelins' intact panels, and switches gasbag1's panels off, OBJECTIVE10's read")]
    internal static void DanteCannonBurnout(TestContext ctx)
    {
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, "C5", "M04"), $"C5/M04 zrdr");
        var report = new StringBuilder();
        ctx.WithWorld("C5", collision: true, mission: "M04", world =>
        {
            var runtime = world.Runtime;
            var dante = runtime.FindNodes("dantezep").FirstOrDefault();
            var cannon = dante == null ? null : runtime.FindNodes(DanteRing[0], dante).FirstOrDefault();
            var bag = dante == null ? null : runtime.FindNodes("gasbag1", dante).FirstOrDefault();
            var pool = cannon == null ? null : runtime.Destructibles.PoolsOn(cannon).FirstOrDefault();
            ctx.Check(dante != null && cannon != null && bag != null && pool != null,
                $"dantezep, its {DanteRing[0]}, its gasbag1 and the cannon's pool resolve");
            if (dante == null || cannon == null || bag == null || pool == null)
                return;
            int twins = runtime.FindNodes("panelleftb1").Count;
            ctx.Check(twins > 1, $"the world holds {twins} nodes named panelleftb1, so a name search is ambiguous");

            var started = new List<string>();
            var saved = runtime.OnInstanceStarted;
            runtime.OnInstanceStarted = (d, _) =>
            {
                if (d.AnimName != null && !started.Contains(d.AnimName))
                    started.Add(d.AnimName);
            };
            try
            {
                runtime.DamageAt(cannon, pool.MaxHealth + 1f);
                var panels = runtime.FindNodes("panels", bag).FirstOrDefault();
                var ringPools = DanteRing.Select(ring =>
                {
                    var node = runtime.FindNodes(ring, dante).FirstOrDefault();
                    return node != null ? runtime.Destructibles.PoolsOn(node).FirstOrDefault() : null;
                }).ToList();
                float offAt = -1f;
                float settledAt = -1f;
                // Stepped until everything the checks below read has landed plus a margin; the
                // 60 s ceiling is for a chain that never gets there.
                for (int i = 1; i <= 60f / WaitDt; i++)
                {
                    runtime.Advance(WaitDt);
                    if (offAt < 0f && panels is { Visible: false })
                        offAt = i * WaitDt;
                    if (settledAt < 0f && offAt >= 0f && started.Contains("finish_dtzepgasbag1")
                        && ringPools.All(p => p is { Status: DestructibleRegistry.State.Destroyed }))
                        settledAt = i * WaitDt;
                    if (settledAt >= 0f && i * WaitDt >= settledAt + 2f)
                        break;
                }
                foreach (var name in new[] { "dtzepleft_gasbag1", "dtzepright_gasbag1", "finish_dtzepgasbag1" })
                    ctx.Check(started.Contains(name), $"{name} started");
                string when = offAt < 0f ? "never" : $"at {offAt:0.#} s";
                ctx.Check(panels != null && !panels.Visible, $"gasbag1's panels are off {when}");
                foreach (var ring in DanteRing)
                {
                    var node = runtime.FindNodes(ring, dante).FirstOrDefault();
                    var ringPool = node != null ? runtime.Destructibles.PoolsOn(node).FirstOrDefault() : null;
                    string status = ringPool == null ? "none" : ringPool.Status.ToString();
                    ctx.Check(ringPool is { Status: DestructibleRegistry.State.Destroyed },
                        $"{ring}'s pool reads Destroyed (status {status})");
                }
                report.AppendLine($"panels off {when}; started: {string.Join(", ", started)}");
            }
            finally
            {
                runtime.OnInstanceStarted = saved;
            }
        });
        ctx.WriteArtifact("test-dante-cannon-burnout.txt", report.ToString());
        ctx.Note($"C5/M04's Dante: one cannon kill burns gasbag1, its ring and its engines");
    }
}
