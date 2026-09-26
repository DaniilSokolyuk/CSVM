using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Roster;
using CSVM.Tooling;
using Godot;

namespace CSVM.Testing;

/// <summary>Suites asserting the choreography a death dispatches: destroy defs, wreck
/// flights, crash rigs, callbacks, and the stops that end them.</summary>
internal static class DestroyChoreographySuites
{
    // ---- CALLBACK: the two vehicle-death codes ---------------------------------------------------

    // A destroy def's CALLBACK events reach the seams the rig supplies: 16 hands the instance the
    // wreck's velocity, 15 stops the damage stages. Both are asserted on shipped defs, player-player
    // (which authors 3, then 16 and 15 through destroy_craft) and the AI's fury-fury.
    // ⚠ Assert the unknown code and the unwired control too. A handler that acted on every code
    // would pass the two arms and still be inventing behaviour, and one wired to nothing at all
    // leaves the wreck motionless exactly as it did before this existed.
    [Suite("callback-events",
        "a destroy def's CALLBACK 16 hands the instance the rig's wreck velocity, its 15 stops the damage stages and the player def's own 3 takes the pilot out of the view they chose, on player-player and fury-fury; an authored code the runtime does not act on is counted, an acted-on code with no seam wired is named as unwired, and no def in the chapter authors the free arm, code 0 (D18)")]
    internal static void CallbackEvents(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            CallbackSeamsReceiveTheirCodes(ctx, world);
            CallbackCodeZeroIsNeverAuthored(ctx, world);
        });
    }

    // ---- one anchor, several authored calls, one copy each -------------------------------------

    // The other half of the pool's keying: a root CALLED REPEATEDLY from one anchor. The slot is
    // claimed per (root, anchor, authored event), so pdpanel7's four gimmeflakes calls at pdp7 take
    // four copies and the Balmoral's three chuteman calls at `destroyed` become three parachutes.
    // Keyed per anchor alone, calls two to four resolve to the first call's copy and the live guard
    // drops them, which is the "one chute where the data asks for three" symptom.
    [Suite("repeat-call-slots",
        "a template root CALLED REPEATEDLY from one anchor takes a pooled copy per authored call, not one for the anchor: pdpanel7's four gimmeflakes calls at pdp7 hold four copies, and a second tear reclaims those four rather than wrapping the pool (D21)")]
    internal static void RepeatCallSlots(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var stage = new Node3D { Name = "RepeatCallStage" };
            var pdp7 = DamageSuites.PoolAnchorNode("pdp7", new Vector3(-10, 0, 0));
            stage.AddChild(pdp7);
            var copies = new List<Node3D>();
            for (int slot = 0; slot < 4; slot++)
            {
                var pool = new Node3D { Name = $"pool{slot}" };
                pool.SetMeta(AnimRuntime.PoolSlotMeta, slot);
                stage.AddChild(pool);
                Session.World.WorldEffectsFactory.BuildEffectStage(world.Gamez,
                    world.Session.Builder.Scene, pool, new[] { "planeflakes" });
                foreach (var child in pool.GetChildren())
                {
                    if (child is Node3D copy)
                    {
                        copies.Add(copy);
                    }
                }
            }

            ctx.Check(copies.Count == 4, $"four planeflakes copies staged ({copies.Count})");
            var runtime = new AnimRuntime(
                Session.World.WorldEffectsFactory.NewCrashTemplateStage())
            {
                AutoStart = false,
                ManualAdvance = true,
                SoundHandledElsewhere = true,
                EmitterFactory = new CountingEmitterFactory(),
                NameResolveFallback = true,
            };
            ctx.Host.AddChild(stage);
            ctx.Host.AddChild(runtime);
            try
            {
                runtime.Bind(stage, world.Session.Program.Subset(new[] { "pdpanel7" }));
                // The authored sites: one call at pdp7 + 2 m, three at pdp7 - 2 m, the last three
                // staggered 0.2 / 0.3 / 0.4 s apart. Debris flies off after placement, so the set
                // is collected AS each call lands rather than sampled at the end.
                var siteA = pdp7.GlobalTransform.Origin + new Vector3(2, 0, 0);
                var siteB = pdp7.GlobalTransform.Origin + new Vector3(-2, 0, 0);
                var taken = new HashSet<Node3D>();
                runtime.Play("pdpanel7", stage, applyReset: false);
                for (int i = 0; i < 90; i++)
                {
                    runtime.Advance(1f / 60f);
                    foreach (var copy in copies)
                    {
                        var at = copy.GlobalTransform.Origin;
                        if (at.DistanceTo(siteA) < 0.5f || at.DistanceTo(siteB) < 0.5f)
                            taken.Add(copy);
                    }
                }

                ctx.Check(taken.Count == 4,
                    $"pdpanel7's four authored gimmeflakes calls took four different copies ({taken.Count})");
                ctx.Check(runtime.PoolRecycles == 0,
                    $"no pool wrap for four call sites over four copies ({runtime.PoolRecycles})");
                // Sticky per call site, as it is per anchor: a second tear reclaims the copies its
                // own four events already hold rather than four more.
                runtime.Play("pdpanel7", stage, applyReset: false);
                for (int i = 0; i < 90; i++)
                {
                    runtime.Advance(1f / 60f);
                }

                ctx.Check(runtime.PoolRecycles == 0,
                    $"a second tear reclaims its own copies ({runtime.PoolRecycles} wrap(s))");
            }
            finally
            {
                runtime.Free();
                stage.Free();
            }
        });
    }

    // The authored STOP_SEQUENCE stops must actually run; nothing else in the gate measures an effect's
    // DURATION (--effects-test only proves a puffer builds, docs/verification.md). It asserts on the
    // dispatch timeline via AnimRuntime.OnEventDispatched, so it needs no textures: the rocket
    // fireball's ON_CALL stopper is named by a stop nothing runs and must stay silent, and the 30 s
    // fire's emitting poll loop is halted, since an un-halted Loop{-1} re-fires every frame forever.
    [Suite("stop-sequence",
        "authored STOP_SEQUENCE stops run: the fireball's 0.3 s stopper, the 30 s fire's halt, and the car lap's stopped car_dust1 refusing every later lap's call")]
    internal static void StopSequenceStops(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var program = world.Session.Program.Subset(new[] { "large_fireball", "large_30sec_fire", "car_loop1_start" });
            var fireball = program.ByAnimName("large_fireball");
            var fire30 = program.ByAnimName("large_30sec_fire");
            var carLoop = program.ByAnimName("car_loop1_start");
            ctx.Check(fireball.Count > 0, $"chapter program has large_fireball defs={fireball.Count}");
            ctx.Check(fire30.Count > 0, $"chapter program has large_30sec_fire defs={fire30.Count}");
            ctx.Check(carLoop.Count > 0, $"chapter program has car_loop1_start defs={carLoop.Count}");
            if (fireball.Count == 0 || fire30.Count == 0 || carLoop.Count == 0)
            {
                return;
            }

            var stage = new Node3D { Name = "StopSequenceStage" };
            var runtime = new AnimRuntime { AutoStart = false, ManualAdvance = true, SoundHandledElsewhere = true };
            ctx.Host.AddChild(stage);
            ctx.Host.AddChild(runtime);
            try
            {
                runtime.Bind(stage, program);
                var timeline = new List<(float T, string Seq, string Kind)>();
                float clock = 0f;
                runtime.OnEventDispatched = d => timeline.Add((clock, d.Sequence, d.EventKind));

                // The fireball: activate_puffer names its ON_CALL stopper at EVENT_OFFSET 0.3 while
                // nothing runs under that name, the stop halts nothing and must START nothing,
                // so the stopper's teardown never dispatches at all.
                runtime.Start(fireball[0], stage);
                for (int i = 0; i < 60; i++)
                {
                    clock += 1f / 60f;
                    runtime.Advance(1f / 60f);
                }
                int stopperDispatches = 0;
                int trailPuffs = 0;
                foreach (var e in timeline)
                {
                    if (e.Seq == "stop_p1trail")
                    {
                        stopperDispatches++;
                    }
                    else if (e.Kind == "PufferState")
                    {
                        trailPuffs++;
                    }
                }
                ctx.Check(trailPuffs > 0, $"the fireball's own puffer events dispatch puffs={trailPuffs}");
                ctx.Same(0, stopperDispatches, $"stop_p1trail dispatches nothing within 1 s");

                // The 30 s fire: fire_n_smoke is a running Loop{-1} poll re-asserting its emitter
                // every frame, the ANIMATION_OFFSET 30 stop must HALT it (the halt idiom), or the
                // re-assert would revive the puffer one frame after the paired INACTIVE.
                float t0 = clock;
                runtime.Start(fire30[0], stage);
                for (int i = 0; i < 320; i++)
                {
                    clock += 0.1f;
                    runtime.Advance(0.1f);
                }
                int pollsBefore = 0;
                float lastPoll = -1f;
                int stopPuffs = 0;
                float stop30At = -1f;
                foreach (var e in timeline)
                {
                    float rel = e.T - t0;
                    if (e.Seq == "fire_n_smoke")
                    {
                        pollsBefore += rel <= 30f ? 1 : 0;
                        lastPoll = rel > lastPoll ? rel : lastPoll;
                    }
                    else if (e.Seq == "stop_fire_n_smoke" && e.Kind == "PufferState")
                    {
                        stopPuffs++;
                        stop30At = rel;
                    }
                }
                ctx.Check(pollsBefore > 100, $"the fire's poll loop runs until its stop polls={pollsBefore}");
                ctx.Check(lastPoll <= 30.2f, $"no fire_n_smoke dispatch after the authored 30 s halt last={lastPoll:0.0}");
                ctx.Same(1, stopPuffs, $"stop_fire_n_smoke PUFFER_STATE dispatches");
                ctx.Check(stop30At >= 29.5f && stop30At <= 30.5f,
                    $"the fire's own puffer-off lands at the authored 30 s t={stop30At:0.0}");

                // The car lap: start_cruisin CALLs car_dust1, STOPs it later in the lap, then LOOPs.
                // A stopped sequence is DONE and a call starts only from PARKED, so every lap after
                // the first is refused and car_dust1's own puffer event fires exactly once.
                runtime.Start(carLoop[0], stage);
                timeline.Clear();
                // A lap is about 25 s; 150 s covers five of them.
                for (int i = 0; i < 1500; i++)
                {
                    clock += 0.1f;
                    runtime.Advance(0.1f);
                }
                int dustCalls = 0;
                int dustRuns = 0;
                float secondCallAt = -1f;
                foreach (var e in timeline)
                {
                    if (e.Seq == "start_cruisin" && e.Kind == "CallSequence")
                    {
                        dustCalls++;
                        if (dustCalls == 2)
                            secondCallAt = e.T;
                    }
                    else if (e.Seq == "car_dust1")
                    {
                        dustRuns++;
                    }
                }
                ctx.Check(dustCalls >= 2, $"the car's lap loop calls car_dust1 on at least two laps calls={dustCalls}");
                ctx.Same(1, dustRuns, $"car_dust1 runs on the first lap only; the later calls are refused (second call at t={secondCallAt:0.0})");
            }
            finally
            {
                runtime.Free();
                stage.Free();
            }
        });
    }

    // ---- PLAYER_1ST_PERSON follows the pilot's view mode -----------------------------------------

    // Condition 120 answered a hardwired false until the Cockpit/Nose view modes existed (A1), so
    // no branch behind it had ever been taken. Subject: the shipped `bullet1` def, whose first
    // Initial sequence is `IF PLAYER_1ST_PERSON / ELSE CALL_ANIMATION two_bulletholes_a / ENDIF`,
    // the exterior bulletholes are what a pilot NOT in a cockpit gets. Its second Initial sequence
    // runs either way and is the control: it proves the def ran at all, so a zero call count reads
    // as "the branch was taken", never as "nothing happened".
    [Suite("first-person-condition",
        "the PLAYER_1ST_PERSON condition follows the pilot's selected view mode: the bullethole def's else branch runs in Chase and is skipped in Cockpit and Nose (A1)")]
    internal static void PlayerFirstPersonCondition(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var program = world.Session.Program.Subset("bullet1");
            var defs = program.ByAnimName("bullet1");
            ctx.Check(defs.Count > 0, $"the program carries the first-person-gated bullet1 def defs={defs.Count}");
            if (defs.Count == 0)
                return;

            var stage = new Node3D { Name = "FirstPersonConditionStage" };
            var runtime = new AnimRuntime { AutoStart = false, ManualAdvance = true, SoundHandledElsewhere = true };
            ctx.Host.AddChild(stage);
            ctx.Host.AddChild(runtime);
            try
            {
                runtime.Bind(stage, program);
                int calls = 0, other = 0;
                runtime.OnEventDispatched = d =>
                {
                    if (d.EventKind == "CallAnimation")
                        calls++;
                    else
                        other++;
                };

                // No seam wired: the answer every runtime without a session gives, and the answer
                // this condition gave everywhere before A1.
                RunBulletDef(runtime, defs[0], stage);
                ctx.Check(calls == 1, $"with no view seam the else branch calls the exterior bulletholes calls={calls}");
                ctx.Check(other > 0, $"and the def's ungated second sequence ran events={other}");

                foreach (var mode in new[] { PilotViewMode.Cockpit, PilotViewMode.Nose })
                {
                    runtime.FirstPersonView = () => PilotView.IsFirstPerson(mode);
                    calls = 0;
                    other = 0;
                    RunBulletDef(runtime, defs[0], stage);
                    ctx.Check(calls == 0, $"in {PilotView.Name(mode)} the condition holds and the call is skipped calls={calls}");
                    ctx.Check(other > 0, $"while the same def's ungated sequence still ran events={other}");
                }

                // Back to Chase on the same runtime: the condition is polled, not latched at bind.
                runtime.FirstPersonView = () => PilotView.IsFirstPerson(PilotViewMode.Chase);
                calls = 0;
                RunBulletDef(runtime, defs[0], stage);
                ctx.Check(calls == 1, $"selecting the chase view again re-opens the else branch calls={calls}");
            }
            finally
            {
                runtime.Free();
                stage.Free();
            }
        });
    }

    // ---- the compiled destruction slot dispatches at death --------------------------------------

    // The live-path start check the direct-Start stop-sequence suite cannot make: a real kill must
    // dispatch the def's compiled destruction slot (AnimDefinition.DeathSlot), the block carrying
    // nearly all of large_30sec_fire's death calls. An unparsed block no-ops every one of them and
    // every "the fire ends on time" check reads the absence as a pass (DIAG-20). Subject: a C1 AA gun.
    // Able to fail: with RunDeathSlot deleted, no destruction_slot lane ever dispatches.
    [Suite("death-slot",
        "a killed destructible dispatches its compiled destruction slot, the block carrying the 30 s fire's 1,035 death calls (BL-276)")]
    internal static void DeathSlotDispatches(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var runtime = world.Runtime;
            DestructibleRegistry.Instance? gun = null;
            foreach (var inst in runtime.Destructibles.All)
            {
                if (inst.Def.DeathSlot is { } s && s.Events.Any(e => e.Kind == "CallAnimation"))
                {
                    gun = inst;
                    break;
                }
            }
            ctx.Check(gun != null, $"chapter ships a destructible with a calling destruction slot chapter={ctx.Chapter}");
            if (gun == null)
            {
                return;
            }
            if (gun.Status == DestructibleRegistry.State.Destroyed)
            {
                runtime.ResetDestructible(gun);
            }

            var gunDef = gun.Def;
            var slotDispatches = new List<(string Kind, string? Name)>();
            var previous = runtime.OnEventDispatched;
            try
            {
                runtime.OnEventDispatched = d =>
                {
                    if (d.Def == gunDef && d.Sequence == "destruction_slot")
                    {
                        slotDispatches.Add((d.EventKind, d.EventName));
                    }
                };
                runtime.DamageAt(gun.Anchor, gun.MaxHealth + 1f);
                for (int i = 0; i < 30; i++)
                {
                    runtime.Advance(1f / 60f);
                }
            }
            finally
            {
                runtime.OnEventDispatched = previous;
            }

            ctx.Check(slotDispatches.Count > 0,
                $"the destruction slot dispatched on death def={gunDef.AnimName} events={slotDispatches.Count}");
            ctx.Check(slotDispatches.Any(e => e.Kind == "CallAnimation"),
                $"the slot's CALL_ANIMATION dispatched targets=[{string.Join(",", slotDispatches.Where(e => e.Kind == "CallAnimation").Select(e => e.Name))}]");
        });
    }

    // ---- what shades the pieces a death flings --------------------------------------------------

    // C1's refuel tanks draw their launched pieces near-black over their own fire column. The data
    // asks for that and the original draws it (docs/verification.md WORLD-47). Pinned so the
    // reading is not re-opened as a lighting bug. The pieces take the SAME shading path as the
    // intact tank beside them, and the darkness is the authored vertex colour alone.
    // Subject: C1's refuel-tank destructibles; able to fail by clearing the debris model's
    // `lighting` flag, by dropping the vertex-colour pass, or by the authored colours changing.
    [Suite("debris-shading",
        "a death's launched pieces take the same shading path as the mesh they came off: C1's refuel debris draws lit and vertex-coloured exactly as the intact tank does, and reads dark only because its pieces are authored at vertex colour 119 on a sheet whose mean texel is 63 where the standing tank is authored at 254 (WORLD-47)")]
    internal static void DebrisShading(TestContext ctx)
    {
        ctx.WithPrivateWorld("C1", collision: false, world =>
        {
            var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
            try
            {
                // The scalar C1's ZONE1 authors, the one a flown session writes to the global. Named
                // here rather than read off the server, which answers with an empty Variant.
                const float c1WorldLight = 0.6070f;
                var report = Probes.DebrisShading(world.Runtime, world.Gamez, textures,
                    world.Chapter, "refuel", c1WorldLight);
                ctx.Check(report.Launches > 0, $"the refuel tanks' death launches ballistic pieces launches={report.Launches}");
                var flew = report.Rows.Where(r => r.Launched).ToList();
                var stood = report.Rows.Where(r => !r.Launched && r.VisibleBefore).ToList();
                ctx.Check(flew.Count > 0 && stood.Count > 0,
                    $"the report separates flung pieces from the standing tank flew={flew.Count} stood={stood.Count}");
                if (flew.Count == 0 || stood.Count == 0)
                {
                    return;
                }

                string litFlew = string.Join(",", flew.Select(r => r.Lighting));
                string litStood = string.Join(",", stood.Select(r => r.Lighting));
                ctx.Check(stood.All(r => r.Lighting) && flew.All(r => r.Lighting),
                    $"every flung piece is drawn lit, exactly as the mesh it came off is flew=[{litFlew}] stood=[{litStood}]");
                string coverage = string.Join(",", flew.Select(r => r.Materials.Count > 0 ? r.Materials[0].VertexColorCoverage : 0f));
                ctx.Check(flew.All(r => r.Materials.Count > 0 && r.Materials[0].VertexColorCoverage > 0.99f),
                    $"every flung piece's sheet is modulated by authored vertex colours over its whole area coverage=[{coverage}]");
                string built = string.Join(",", flew.Select(r => $"{r.SurfacesWithColors}/{r.SurfacesReadingWorldLight}/{r.BuiltSurfaces}"));
                ctx.Check(flew.All(r => r.BuiltSurfaces > 0 && r.SurfacesWithColors == r.BuiltSurfaces
                        && r.SurfacesReadingWorldLight == r.BuiltSurfaces),
                    $"the BUILT mesh keeps both terms: every surface commits its colours and carries the dimming shader coloured/dimmed/total=[{built}]");

                float flungVc = flew.Max(r => r.Materials[0].VertexColor.X);
                float standingVc = stood.Max(r => r.Materials[0].VertexColor.X);
                ctx.Check(flungVc > 0.44f && flungVc < 0.50f,
                    $"the flung pieces are authored at about half brightness vc={flungVc * 255f:0}");
                ctx.Check(standingVc > 0.97f,
                    $"the standing tank is authored at full brightness vc={standingVc * 255f:0}");
                float flungTexel = flew.Max(r => r.Materials[0].Texel.X);
                ctx.Check(flungTexel > 0.20f && flungTexel < 0.30f,
                    $"the pieces' sheet is itself dark, mean texel={flungTexel * 255f:0}");
            }
            finally
            {
                textures.Dispose();
            }
        });
    }

    // ---- a start-state swap must reach the pool, not only the node -----------------------------

    // A destructible whose own Initial sequence authors the healthy/destroyed role swap directly
    // is dispatched through the same Start() call a mission's start-state script reaches, never
    // through DamageAt. A pool that does not follow stays Healthy at full HP while the swap
    // already reads destroyed, so a later hit finds a live pool and replays the whole death
    // choreography on an object that looks dead already. Subject: the first shipped destructible
    // AuthorsOwnSwap finds; able to fail with SyncDestructiblePool's call site removed.
    [Suite("start-state-swap-pool",
        "a destructible whose own Initial sequence authors the healthy/destroyed swap directly (a start-state script's shape, never DamageAt) leaves the HP pool destroyed too, so a later hit does not replay the death choreography (BL-513, BL-521)")]
    internal static void StartStateSwapSyncsThePool(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var runtime = world.Runtime;
            DestructibleRegistry.Instance? subject = null;
            foreach (var cand in runtime.Destructibles.All)
            {
                if (cand.Status == DestructibleRegistry.State.Healthy && cand.Anchor != null
                    && AuthorsOwnSwap(cand.Def))
                {
                    subject = cand;
                    break;
                }
            }
            ctx.Check(subject != null,
                $"chapter {ctx.Chapter} ships a destructible whose own Initial sequence authors the role swap");
            if (subject is not { } inst)
            {
                return;
            }

            runtime.Start(inst.Def, inst.Anchor);
            for (int i = 0; i < 6; i++)
            {
                runtime.Advance(1f / 60f);
            }

            ctx.Check(inst.Status == DestructibleRegistry.State.Destroyed && inst.Health <= 0f,
                $"the pool follows a start-state swap with no DamageAt in the picture (status={inst.Status}, hp={inst.Health:0.##})");

            bool landed = runtime.DamageAt(inst.Anchor, inst.MaxHealth + 1f);
            ctx.Check(landed, $"a later hit still resolves to the destructible def={inst.Def.AnimName}");
            ctx.Check(inst.Status == DestructibleRegistry.State.Destroyed,
                $"…and finds the pool already dead rather than replaying the death choreography");
        });
    }

    // ---- one death calls another destructible's own death -----------------------------------------

    // C3's suspension bridge is the install's chain reaction: the fuel truck parked on it
    // (`bridge_truck01`) runs `chainreaction`, which CALL_ANIMATIONs the bridge's own death anim
    // `rope1burn`, and `KillCalledDestructible` turns that call into the bridge's kill.
    // ⚠ Assert the bridge boots standing as well as dying. The call is refused outright on a pool
    // already destroyed, so a baseline misread as a death leaves the bridge whole and silent with
    // the truck still exploding on it, and only the second check tells the two apart.
    [Suite("called-death-chain",
        "C3/M01's fuel truck kills the suspension bridge through its CALL_ANIMATION of the bridge's own death anim, and the bridge boots standing for that call to reach")]
    internal static void CalledDeathChain(TestContext ctx)
    {
        ctx.WithWorld("C3", collision: false, mission: "M01", world =>
        {
            var runtime = world.Runtime;
            var bridge = runtime.Destructibles.All.FirstOrDefault(i =>
                i.Def.Name.Equals("susp_bridge", System.StringComparison.OrdinalIgnoreCase));
            var truck = runtime.Destructibles.All.FirstOrDefault(i =>
                i.Def.Name.Equals("bridge_truck01", System.StringComparison.OrdinalIgnoreCase));
            ctx.Check(bridge != null && truck != null,
                $"C3/M01 ships both pools bridge={bridge?.Def.Name ?? "-"} truck={truck?.Def.Name ?? "-"}");
            if (bridge is not { } span || truck is not { } fuel)
            {
                return;
            }

            ctx.Check(span.Status == DestructibleRegistry.State.Healthy && span.Health == span.MaxHealth,
                $"the bridge boots standing hp={span.Health}/{span.MaxHealth} state={span.Status}");

            // Two seconds, because `chainreaction` staggers its calls on EVENT_OFFSET and the
            // bridge's is several steps down the list.
            runtime.DamageAt(fuel.Anchor, fuel.MaxHealth + 1f);
            for (int i = 0; i < 120; i++)
            {
                runtime.Advance(1f / 60f);
            }

            ctx.Check(fuel.Status == DestructibleRegistry.State.Destroyed,
                $"the fuel truck dies status={fuel.Status}");
            ctx.Check(span.Status == DestructibleRegistry.State.Destroyed && span.Health <= 0f,
                $"…and its chain reaction takes the bridge with it status={span.Status} hp={span.Health:0.##}");
        });
    }

    // ---- a def's own death choreography is never a revival --------------------------------------

    // The Barracuda (`sub_destruction`, C3/M03) switches its `dbase` node off as an ordinary step of
    // dying and sinks `subdestroyed` away a minute later. SyncDestructiblePool reads a dbase-role
    // node as a destroyed-role one, so both events reach its revival branch and hand the killed
    // submarine full HP back after the visible death has played.
    // ⚠ Read the pool twice, once in the frames after the kill and once past the sink. The two
    // events are a minute apart, so a fix covering only the synchronous death burst passes the first.
    [Suite("death-not-a-revival",
        "the Barracuda's own death switches its dbase node off and sinks its destroyed hull away a minute later, a lifesaver's switches `destroyed` itself off in the same breath as `healthy`, and a constructed def clears its wreck away from the ON_CALL sequence its death calls; none of the four hands the killed pool its health back, while a baseline outside that chain still revives it")]
    internal static void OwnDeathIsNotARevival(TestContext ctx)
    {
        ctx.WithWorld("C3", collision: false, mission: "M03", world =>
        {
            var runtime = world.Runtime;
            var sub = runtime.Destructibles.All.FirstOrDefault(i => string.Equals(
                i.Def.AnimName, "sub_destruction", System.StringComparison.OrdinalIgnoreCase));
            ctx.Check(sub != null, $"C3/M03 ships the Barracuda's pool def={sub?.Def.Name ?? "-"}");
            if (sub is not { } boat)
            {
                return;
            }

            ctx.Check(boat.Status == DestructibleRegistry.State.Healthy,
                $"the submarine boots standing hp={boat.Health:0.##}/{boat.MaxHealth:0.##} state={boat.Status}");
            runtime.DamageAt(boat.Anchor, boat.MaxHealth + 1f);
            for (int i = 0; i < 120; i++)
            {
                runtime.Advance(1f / 60f);
            }

            ctx.Check(boat.Status == DestructibleRegistry.State.Destroyed && boat.Health <= 0f,
                $"the `dbase` its death switches off leaves the pool dead status={boat.Status} hp={boat.Health:0.##}");

            // Past the 35 s hold and the 80 s sink the death ends `subdestroyed` off with.
            for (int i = 0; i < 500; i++)
            {
                runtime.Advance(0.25f);
            }

            ctx.Check(boat.Status == DestructibleRegistry.State.Destroyed && boat.Health <= 0f,
                $"and so does the hull sinking away status={boat.Status} hp={boat.Health:0.##}");
        });

        // C1/M05's nine lifesavers are the other authored shape: the role word is `destroyed`
        // itself, and both switches land in one block, so the death is a kill immediately undone.
        ctx.WithWorld("C1", collision: false, mission: "M05", world =>
        {
            var runtime = world.Runtime;
            var raft = runtime.Destructibles.All.FirstOrDefault(i => string.Equals(
                i.Def.Name, "lifesaver11", System.StringComparison.OrdinalIgnoreCase));
            ctx.Check(raft != null, $"C1/M05 ships a lifesaver pool def={raft?.Def.Name ?? "-"}");
            if (raft is not { } boat)
            {
                return;
            }

            runtime.DamageAt(boat.Anchor, boat.MaxHealth + 1f);
            for (int i = 0; i < 120; i++)
            {
                runtime.Advance(1f / 60f);
            }

            ctx.Check(boat.Status == DestructibleRegistry.State.Destroyed && boat.Health <= 0f,
                $"the lifesaver's own death leaves its pool dead status={boat.Status} hp={boat.Health:0.##}");
        });

        WreckClearedFromACalledSequence(ctx);
    }

    // ---- a carried pose reaches the pieces a called sequence hides -----------------------------

    // C3's suspension bridge switches two of its spans off from ON_CALL sequences, which a pose
    // applied without choreography reaches only by following CALL_SEQUENCE (`BL-791`).
    // ⚠ Assert a rope the def's MAIN sequences hide in the same pass, or the check still passes on
    // a pose that applied nothing at all.
    [Suite("carried-pose-called-sequence",
        "a carried destroyed state on C3's suspension bridge hides the spans its ON_CALL fire-puffer sequences switch off, not only the ropes its main sequences do, and leaves dbase standing under them")]
    internal static void CarriedPoseCalledSequence(TestContext ctx)
    {
        ctx.WithWorld("C3", collision: false, mission: "M01", world =>
        {
            var runtime = world.Runtime;
            var bridge = runtime.Destructibles.All.FirstOrDefault(i =>
                i.Def.Name.Equals("susp_bridge", System.StringComparison.OrdinalIgnoreCase));
            ctx.Check(bridge != null, $"C3/M01 ships the susp_bridge pool");
            if (bridge is not { } span)
            {
                return;
            }

            var pieces = new[]
            {
                "rope1", "rope2", "part1", "part2", "part3a", "part3b", "part3c", "part4",
                "part5", "dbase",
            };
            string Snap() => string.Join(" ", pieces.Select(p =>
                $"{p}:{string.Join(string.Empty, runtime.FindNodes(p, span.Anchor).Select(n => n.Visible ? "1" : "0"))}"));
            int Down(string piece) => runtime.FindNodes(piece, span.Anchor).Count(n => !n.Visible);

            ctx.Note($"standing {Snap()}");
            ctx.Check(Down("rope1") == 0 && Down("part5") == 0,
                $"nothing is down before the carried state lands");

            // ⚠ CarryState, never DamageAt: the persist log opens a mission on the pose a death
            // ends in, and routing it through the kill would replay the choreography instead.
            ctx.Check(runtime.CarryState(span, destroyed: true, 0f),
                $"the carried destroyed state applies to the pool");
            ctx.Note($"carried  {Snap()}");

            // rope1 comes off an unnamed main sequence and is the control: it was already down
            // before this fix, so it fails alongside the two below only if the pose applied nothing.
            ctx.Check(Down("rope1") == 1, $"rope1 is down, off the def's own main sequence");

            // part5 resolves to one node, part1 to four of that name under this anchor, and the
            // death binds one of them. Both are switched off only in an ON_CALL fire-puffer
            // sequence, so a pose that does not follow CALL_SEQUENCE leaves them standing.
            ctx.Check(Down("part5") == 1, $"part5 is down, off part5_fire_puffer (ON_CALL)");
            ctx.Check(Down("part1") >= 1, $"part1 is down, off part1_fire_puffer (ON_CALL)");

            ctx.Check(runtime.FindNodes("dbase", span.Anchor) is { Count: > 0 } plinth
                && plinth.All(n => n.Visible),
                $"…and dbase still stands under the wreck");
        });
    }

    // ---- a carried state lands silently, on the pool and the pose ------------------------------

    // The persist log opens a later mission on the pose a death ends in, never on a replayed death:
    // no instance starts (no fireball, smoke, debris or sound), the pool reads destroyed at HP 0,
    // the healthy role is hidden and the destroyed one shown, and a later hit is a no-op. A carried
    // partial HP lands at its damage stage with no stage burst. Subjects: shipped PERSIST_LOG
    // destructibles. Able to fail with ApplyTo routed back through DamageAt.
    [Suite("carried-state-silent",
        "a persist-log state lands on the pool and the destroyed pose with no instance started, a carried partial HP lands at its stage, and a later hit on the carried kill is a no-op")]
    internal static void CarriedStateIsSilent(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var runtime = world.Runtime;
            var subjects = new List<DestructibleRegistry.Instance>();
            foreach (var cand in runtime.Destructibles.All)
            {
                // Both roles required, not assumed: the healthy pick depends on what earlier suites
                // in the shard already destroyed, and a shipped PERSIST_LOG destructible with no
                // healthy/destroyed pair cannot answer the pose half of this at all.
                if (!cand.Def.PersistLog || !cand.Anchor.HasMeta(AnimRuntime.IndexMeta)
                    || runtime.Destructibles.Resolve(cand.Anchor) is not { } live
                    || live.Status != DestructibleRegistry.State.Healthy || live.MaxHealth <= 0f
                    || runtime.FindNodes("healthy", cand.Anchor).Count == 0
                    || runtime.FindNodes("destroyed", cand.Anchor).Count == 0
                    || subjects.Contains(live))
                {
                    continue;
                }

                subjects.Add(live);
                if (subjects.Count == 2)
                {
                    break;
                }
            }

            ctx.Check(subjects.Count == 2,
                $"chapter {ctx.Chapter} ships two healthy PERSIST_LOG destructibles (found {subjects.Count})");
            if (subjects.Count < 2)
            {
                return;
            }

            var dead = subjects[0];
            var worn = subjects[1];
            float wornHp = worn.MaxHealth * 0.5f;
            const int chapter = 99;
            var log = new CampaignPersistLog();
            const int earlierSeq = 3;   // an earlier mission of the same chapter recorded both
            log.Merge(chapter, earlierSeq, new[]
            {
                new PersistedObject((int)dead.Anchor.GetMeta(AnimRuntime.IndexMeta), dead.Def.Name, dead.Anchor.Name, true, 0f),
                new PersistedObject((int)worn.Anchor.GetMeta(AnimRuntime.IndexMeta), worn.Def.Name, worn.Anchor.Name, false, wornHp),
            });

            // A death's first start is synchronous inside the call, so the watch brackets the calls
            // themselves; the frames between are advanced unwatched, where the world's own ambient
            // loops restart and would be counted against the replay.
            var started = new List<string>();
            var before = runtime.OnInstanceStarted;
            void Watch() => runtime.OnInstanceStarted = (def, anchor) => started.Add($"{def.AnimName}@{anchor?.Name}");
            void Unwatch() => runtime.OnInstanceStarted = before;
            try
            {
                Watch();
                int applied = log.ApplyTo(runtime, chapter, earlierSeq + 1);
                Unwatch();
                for (int i = 0; i < 6; i++)
                {
                    runtime.Advance(1f / 60f);
                }

                ctx.Same(2, applied, $"both carried objects are applied");
                ctx.Check(started.Count == 0,
                    $"no instance starts on the replay: no effect, debris or sound (started=[{string.Join(", ", started)}])");
                ctx.Check(dead.Status == DestructibleRegistry.State.Destroyed && dead.Health <= 0f,
                    $"the carried kill lands on the pool ({dead.Anchor.Name} status={dead.Status}, hp={dead.Health:0.##})");
                var healthy = runtime.FindNodes("healthy", dead.Anchor);
                var destroyed = runtime.FindNodes("destroyed", dead.Anchor);
                ctx.Check(healthy.Count > 0 && healthy.All(n => !n.Visible),
                    $"{dead.Anchor.Name}: the healthy role is hidden ({healthy.Count} node(s))");
                ctx.Check(destroyed.Count > 0 && destroyed.Any(n => n.Visible),
                    $"{dead.Anchor.Name}: the destroyed role is shown ({destroyed.Count} node(s))");

                ctx.Check(worn.Status == DestructibleRegistry.State.Damaged
                    && Mathf.Abs(worn.Health - wornHp) < 1e-3f,
                    $"the carried partial HP lands on the pool ({worn.Anchor.Name} status={worn.Status}, hp={worn.Health:0.##})");
                ctx.Check(!runtime.ApplyDamageStages(worn),
                    $"{worn.Anchor.Name}: the damage stage is already the carried HP's, so no stage burst is owed");

                Watch();
                bool landed = runtime.DamageAt(dead.Anchor, dead.MaxHealth + 1f);
                Unwatch();
                ctx.Check(landed && dead.Status == DestructibleRegistry.State.Destroyed,
                    $"a later hit on {dead.Anchor.Name} resolves and finds the pool dead");
                ctx.Check(started.Count == 0,
                    $"…and starts nothing: the death does not replay (started=[{string.Join(", ", started)}])");
            }
            finally
            {
                runtime.OnInstanceStarted = before;
            }
        });
    }

    // ---- an AI kill, from the death frame to the ground -----------------------------------------

    // The whole fall as one sequence: the kill starts the SELF-NAMED destroy def and nothing else,
    // the airframe stays VISIBLE for every frame of it, the hull travels a measured distance under
    // the flight model, Callback 16 hands the anim the velocity it reached THERE, and a wreck that
    // meets the ground first plays the ground-impact def and is hidden only then.
    // ⚠ Keep the unwired control: the bug this pins is a hull hidden on the death frame.
    // Decode: docs/org/vehicleDamage.md, "What happens to the wreck".
    [Suite("ai-wreck-fall",
        "a killed AI aircraft's whole fall: the kill starts its self-named destroy def and no ai_crash_* def, the airframe is drawn on every frame of the fall, the hull travels under the flight model until Callback 15 releases it at the authored 3.0 s, Callback 16 hands the anim the velocity it reached THERE, a wreck that meets the ground first plays its surface-indexed crash def and is hidden only then (D21), and the lever/surface command last written by the AI think reads back bit-identical every frame of the fall (BL-451)")]
    internal static void AiWreckFall(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.WithWorld(ctx.Chapter, collision: true, world =>
        {
            var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
            var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
            ProjectilePool? pool = null;
            try
            {
                var live = new ProjectilePool(textures, null, null);
                pool = live;
                ctx.Host.AddChild(live);
                var spec = SessionSpec.Parse(System.Array.Empty<string>());
                var liveries = new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof"));
                var factory = new Session.World.WorldEffectsFactory(spec, ctx.Host, () => Vector3.Zero);
                var inputs = new AircraftAssemblyResources
                {
                    PlanesGamez = planesGamez,
                    StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                    AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                    PaintRng = new RandomNumberGenerator(),
                    ZrdrPath = ctx.ZrdrPath,
                    StockLoadouts = StockLoadouts.Load(),
                    WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
                    Textures = textures,
                    Shakes = ShakeDefs.Load(ctx.ZrdrPath),
                };
                var spawner = new FlightRoster(FlightRosterPolicy.From(spec), liveries, factory, ctx.Host, inputs, new FlightWorldBindings { Projectiles = live, Gamez = world.Gamez, WorldScene = world.Session.Builder.Scene, CrashProgram = world.Session.Program }, new HumanRosterBindings());

                // A suite that built no colliders would pass the landing arm by never reaching the
                // ground at all, so the chapter's geometry is found before anything is asked of it.
                var space = world.Session.Root.GetWorld3D()?.DirectSpaceState;
                if (space == null)
                {
                    ctx.Check(false, $"the world built a collision space to fall into chapter={ctx.Chapter}");
                    return;
                }

                bool found = FindFlatRun(space, out var lowSpawn, out var lowAim);
                ctx.Check(found,
                    $"chapter {ctx.Chapter} offers a flat run of land to sink onto at {(found ? $"({lowSpawn.X:0},{lowSpawn.Y:0},{lowSpawn.Z:0})" : "nowhere")}");

                // ⚠ Below the 2000 m atmosphere band edge: a wreck released above it falls through
                // air 16.73x thinner, so the fall arm would measure the wrong regime's drag.
                const float FallFrom = 1500f;
                WreckFallsBeforeItIsHandedOver(ctx, spawner, FallFrom);
                // Both airframes, because the reported split was between them: the immortal stage is
                // pfsmoketrail at `prop1`, which every airframe carries, and the Bloodhawk's missing
                // lft_elev/rt_elev belong to random_remote_damage, which ends on its own timeline.
                StagedEffectsEndAtTheDeath(ctx, spawner, FallFrom, "player_fury", -3000f);
                StagedEffectsEndAtTheDeath(ctx, spawner, FallFrom, "player_bhawk", -6000f);
                WreckWithNoDestroyDefIsHiddenOnTheKill(ctx, spawner, FallFrom);
                if (found)
                    WreckReachingTheGroundPlaysItsCrashDef(ctx, spawner, lowSpawn, lowAim);
            }
            finally
            {
                pool?.Free();
                textures.Dispose();
            }
        });
    }

    // The player's own destroy choreography. `player-player` is the one destroy def that does not
    // fall as an intact hull: it breaks into four burning pieces, each flown by its own
    // OBJECT_MOTION, with a cockpit eject and a parachutist.
    // ⚠ Both arms are READ, never guessed: `If NODE_ACTIVE 1` names entry one of the def's own node
    // list, `player_autogyro`, so the rotor arm is an airframe test and every other airframe takes
    // `random_destroy`. Decode: docs/org/vehicleDamage.md.
    [Suite("player-destroy-choreography",
        "a shot-down player plays player-player whole: the two authored arms are chosen by the def's own IF NODE_ACTIVE 1 (its node one is `player_autogyro`, so only the autogyro stops its rotor), the cockpit eject stages and shows its cpilot, all four wreck pieces appear and fly their own OBJECT_MOTION, and the def's own Callback 3 reaches the view seam the kill binds rather than going counted (D25)")]
    internal static void PlayerDestroyChoreography(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
            var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
            try
            {
                // Every airframe, not ctx.PlaneName: part of the verdict IS the branch between
                // them, and `pilot` is a name all eleven carry, so a resolver binding the wrong
                // one is wrong everywhere at once.
                foreach (var planeName in Flight.Airframe.EffectCatalogue.AirframeDestroyAnims.Keys)
                {
                    PlayerDestroyArm(ctx, world, planesGamez, textures, planeName,
                        autogyro: string.Equals(planeName, "player_autogyro", System.StringComparison.OrdinalIgnoreCase));
                }
            }
            finally
            {
                textures.Dispose();
            }
        });
    }

    // ---- binding the crash rig must leave the airframe under the controller --------------------

    // Builds the crash rig the way WorldEffectsFactory.BuildFlightCrashRuntime does, binds the
    // crash-rig subset, and asserts the two things that go wrong in flight. ⚠ The airframe model stays
    // a plain child of the controller, not world-pinned: on the Devastator the reset defs' authored
    // name is the model root itself, and the reset chain must not relocate the aircraft the way it
    // places effect templates. ⚠ Every pooled template copy of one root must show the same lit mesh
    // count as its slot-0 sibling; a copy the reset pass missed stays lit for the whole session.
    [Suite("crash-rig-anchors",
        "binding the crash rig leaves the airframe model under the controller (even the Devastator, whose model root shares the crash defs' authored NAME) and stages every pooled copy in the same reset pose")]
    internal static void CrashRigAnchors(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
            var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
            try
            {
                foreach (var model in new[] { "player_bhawk", "player_pfighter" })
                {
                    var controller = new Node3D { Name = "controller_replica" };
                    var runtime = AnimRuntime.ForCrashRig(
                        Session.World.WorldEffectsFactory.NewCrashTemplateStage(),
                        1, new CountingEmitterFactory(), false);
                    runtime.ManualAdvance = true;
                    try
                    {
                        var builder = new PlaneBuilder(planesGamez, textures);
                        var planeModel = builder.Build(model);
                        controller.AddChild(planeModel);
                        var crashRoot = new Node3D { Name = "player" };
                        crashRoot.SetMeta(AnimRuntime.NameMeta, "player");
                        crashRoot.Transform = planeModel.Transform;
                        controller.AddChild(crashRoot);
                        var rootNames = Session.World.WorldEffectsFactory.CrashStageRootNames(
                            world.Session.Program, world.Gamez, controller);
                        Session.World.WorldEffectsFactory.StageCrashTemplates(world.Gamez,
                            world.Session.Builder.Scene, crashRoot, rootNames,
                            Utils.EffectPools.Load());
                        var copies = new List<(string Root, int Slot, Node3D Copy)>();
                        foreach (var child in crashRoot.GetChildren())
                        {
                            if (child is Node3D pool && pool.HasMeta(AnimRuntime.PoolSlotMeta))
                            {
                                int slot = (int)pool.GetMeta(AnimRuntime.PoolSlotMeta);
                                foreach (var staged in pool.GetChildren())
                                {
                                    if (staged is Node3D copy)
                                    {
                                        string root = copy.HasMeta(AnimRuntime.NameMeta)
                                            ? (string)copy.GetMeta(AnimRuntime.NameMeta)
                                            : copy.Name;
                                        copies.Add((root, slot, copy));
                                    }
                                }
                            }
                        }

                        var wreck = builder.BuildDestroyed(model);
                        var restPoses = new List<(Node3D Node, Transform3D RestPose)>();
                        if (wreck != null)
                        {
                            wreck.Visible = false;
                            crashRoot.AddChild(wreck);
                            CollectRestPoses(wreck, restPoses);
                        }

                        ctx.Host.AddChild(controller);
                        ctx.Host.AddChild(runtime);
                        var restOrigin = planeModel.GlobalTransform.Origin;
                        runtime.Bind(controller,
                            world.Session.Program.Subset(Flight.Airframe.EffectCatalogue.CrashRigAnimNames(
                                Flight.Airframe.EffectCatalogue.CrashDefTable(world.Session.Program))));
                        for (int i = 0; i < 6; i++)
                        {
                            runtime.Advance(1f / 60f);
                        }

                        ctx.Check(!planeModel.TopLevel,
                            $"{model}: the airframe model is not world-pinned (TopLevel) by the rig's bind");
                        ctx.Check(planeModel.GetParent() == controller,
                            $"{model}: the airframe model still hangs under the controller");
                        ctx.Check(planeModel.GlobalTransform.Origin.DistanceTo(restOrigin) < 0.5f,
                            $"{model}: the airframe model has not moved off its rig position");
                        // Staged dark: a copy left lit sits at the plane's centre for the whole
                        // session (the flake/gunhit family has no authored deactivation).
                        var lit = string.Join("; ", copies
                            .Select(c => (c.Root, c.Slot, Lit: LitMeshCount(c.Copy)))
                            .Where(c => c.Lit > 0)
                            .Select(c => $"'{c.Root}' slot{c.Slot} lights {c.Lit}"));
                        ctx.Check(lit.Length == 0,
                            $"{model}: every staged template copy is dark after the bind{(lit.Length == 0 ? "" : $", {lit}")}");

                        // A real tear: the damage sink's own call shape. The CALLed gimmeflakes
                        // copy must light at its pdp5 site, and the panel def's instance ending on
                        // the AIRFRAME anchor must not drag the model into the retire-hide.
                        runtime.Play("pdpanel5", planeModel, applyReset: false);
                        for (int i = 0; i < 6; i++)
                        {
                            runtime.Advance(1f / 60f);
                        }

                        ctx.Check(copies.Any(c => c.Root == "planeflakes" && LitMeshCount(c.Copy) > 0),
                            $"{model}: the tear's planeflakes copy is revealed while its burst flies");
                        for (int i = 0; i < 120; i++)
                        {
                            runtime.Advance(1f / 60f);
                        }

                        ctx.Check(copies.All(c => c.Root != "planeflakes" || LitMeshCount(c.Copy) == 0),
                            $"{model}: the burst's copy goes dark again once the effect is over");
                        ctx.Check(!planeModel.TopLevel
                                  && planeModel.GlobalTransform.Origin.DistanceTo(restOrigin) < 0.5f
                                  && planeModel.IsVisibleInTree(),
                            $"{model}: the airframe model is still parented, placed and visible after the tear");

                        // Crash, respawn, move, crash again: the wreck and every template a crash reveals must play at the
                        // SECOND crash's site. The failure looks like the destroyed plane and the dirt burst replaying at
                        // the first crash's position on every crash after the first.
                        runtime.Play("player_crash_dirt", crashRoot, applyReset: false);
                        for (int i = 0; i < 180; i++)
                        {
                            runtime.Advance(1f / 60f);
                        }

                        // The respawn ritual, FlightController.Respawn's crash arm verbatim.
                        runtime.ResetToBaseState();
                        foreach (var (node, rest) in restPoses)
                        {
                            node.Transform = rest;
                        }

                        var leftover = string.Join("; ", copies
                            .Select(c => (c.Root, c.Slot, Lit: LitMeshCount(c.Copy)))
                            .Where(c => c.Lit > 0)
                            .Select(c => $"'{c.Root}' slot{c.Slot} lights {c.Lit}"));
                        ctx.Check(leftover.Length == 0,
                            $"{model}: respawn leaves no crash template revealed{(leftover.Length == 0 ? "" : $", {leftover}")}");

                        controller.Position += new Vector3(400, 0, 0);
                        runtime.Play("player_crash_dirt", crashRoot, applyReset: false);
                        for (int i = 0; i < 180; i++)
                        {
                            runtime.Advance(1f / 60f);
                        }

                        var here = controller.GlobalTransform.Origin;
                        if (wreck != null)
                        {
                            ctx.Check(wreck.GlobalTransform.Origin.DistanceTo(here) < 150f,
                                $"{model}: the wreck flies from the SECOND crash's site ({wreck.GlobalTransform.Origin.DistanceTo(here):0} m away)");
                        }

                        var stale = string.Join("; ", copies
                            .Where(c => LitMeshCount(c.Copy) > 0
                                        && c.Copy.GlobalTransform.Origin.DistanceTo(here) > 150f)
                            .Select(c => $"'{c.Root}' slot{c.Slot} at {c.Copy.GlobalTransform.Origin.DistanceTo(here):0} m"));
                        ctx.Check(stale.Length == 0,
                            $"{model}: every template the second crash reveals plays at its own site{(stale.Length == 0 ? "" : $", {stale}")}");
                        ctx.Check(!crashRoot.TopLevel,
                            $"{model}: the crash scaffold is never world-pinned by a crash's own calls");
                    }
                    finally
                    {
                        runtime.Free();
                        controller.Free();
                    }
                }
            }
            finally
            {
                textures.Dispose();
            }
        });
    }

    // nitro_boost/nitro_decay anchor as NAME "warhawk" (plane_props.zrd), which never resolves in
    // a per-plane crash rig's own index, the shape startprops/stopprops share, fixed by Play's
    // PlaneModel fallback. ⚠ No flyable player_* model carries nitropropN (that disc geometry
    // ships only on the separate bare-named library root); the fix restores what the flown
    // plane's own nodes CAN show, the nitropuffN exhaust puffers at exhaust1..4.
    [Suite("nitro-boost-anchors",
        "nitro_boost/nitro_decay author NAME \"warhawk\" as their anchor, which never resolves inside a per-plane crash rig; Play's PlaneModel fallback (the same shape startprops/stopprops already use) starts both defs on the flown Warhawk and sustains its nitropuff1 exhaust puffer, though no flyable model carries the nitropropN disc geometry itself")]
    internal static void NitroBoostAnchors(TestContext ctx)
    {
        const string model = "player_warhawk";
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
            var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
            try
            {
                var controller = new Node3D { Name = "controller_replica" };
                var emitters = new CountingEmitterFactory();
                var runtime = AnimRuntime.ForCrashRig(
                    Session.World.WorldEffectsFactory.NewCrashTemplateStage(), 1, emitters, false);
                runtime.ManualAdvance = true;
                try
                {
                    var builder = new PlaneBuilder(planesGamez, textures);
                    var planeModel = builder.Build(model);
                    controller.AddChild(planeModel);
                    ctx.Host.AddChild(controller);
                    ctx.Host.AddChild(runtime);
                    runtime.Bind(controller,
                        world.Session.Program.Subset(Flight.Airframe.EffectCatalogue.CrashRigAnimNames(
                            Flight.Airframe.EffectCatalogue.CrashDefTable(world.Session.Program))));

                    ctx.Check(Find(planeModel, "exhaust1") != null,
                        $"{model}: builds the exhaust1 marker nitro_boost's puffers anchor at");

                    var started = runtime.Play("nitro_boost", planeModel, applyReset: false);
                    ctx.Check(started.Count > 0,
                        $"{model}: nitro_boost starts on the flown plane (PlayWithin's missing fallback left this empty)");
                    for (int i = 0; i < 30; i++)
                        runtime.Advance(1f / 60f);
                    ctx.Check(emitters.Built.Any(e => e.Key.Equals("nitropuff1", System.StringComparison.OrdinalIgnoreCase) && e.Sustaining),
                        $"{model}: nitro_boost's exhaust puffer is sustaining [{string.Join(",", emitters.Built.Select(e => e.Key))}]");

                    runtime.Stop("nitro_boost");
                    var decayStarted = runtime.Play("nitro_decay", planeModel, applyReset: false);
                    ctx.Check(decayStarted.Count > 0, $"{model}: nitro_decay starts on release too");
                }
                finally
                {
                    runtime.Free();
                    controller.Free();
                }
            }
            finally
            {
                textures.Dispose();
            }
        });
    }

    // The same engage on the rig a SESSION builds, which is a different rig: the production factory
    // stages the effect templates and the wreck under a crash root, pre-warms every puffer before
    // anything plays, and the edge arrives through the flight step rather than a direct Play. A
    // replica rig cannot see a defect that only the staged neighbours or the pre-warm can cause,
    // which is why this arm exists beside the one above.
    [Suite("nitro-boost-flown-rig",
        "a nitro engage on the rig WorldEffectsFactory builds for a session, reached through FlightController's own step with the command held: the boost engages, nitro_boost anchors on the flown airframe rather than a staged neighbour, and its nitropuffN exhaust puffers are emitting on the aircraft's own exhaust nodes")]
    internal static void NitroBoostFlownRig(TestContext ctx)
    {
        const string model = "player_warhawk";
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
            var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
            FlightController? player = null;
            try
            {
                var factory = new Session.World.WorldEffectsFactory(
                    SessionSpec.Parse(System.Array.Empty<string>()), ctx.Host, () => Vector3.Zero);
                var spawn = new Vector3(0f, 500f, 0f);
                var stats = PlaneStats.Load(ctx.ZrdrPath, model);
                var builder = new PlaneBuilder(planesGamez, textures);
                var planeModel = builder.Build(model);
                player = new FlightController
                {
                    PlaneModel = planeModel,
                    Collider = PlaneCollider.Build(planeModel),
                    PlayerIndex = 0,
                    UseKeyboard = false,
                    PadDevices = System.Array.Empty<int>(),
                    AllowPause = false,
                    Damage = PlaneDamage.For(stats),
                };
                player.AddChild(planeModel);
                player.Setup(new FlightModel(stats), null, new CamParams(), spawn, spawn + Vector3.Forward);
                ctx.Host.AddChild(player);
                factory.BuildFlightCrashRuntime(player, builder, model, world.Gamez,
                    world.Session.Builder.Scene, textures, world.Session.Program, verbose: false,
                    planesGamez: planesGamez);
                if (player.CrashRuntime is not { } rig)
                {
                    ctx.Check(false, $"{model}: the session rig built a crash runtime");
                    return;
                }
                rig.ManualAdvance = true;

                // The injector is the hangar pick's bit; without it the command arm refuses and the
                // engage edge this suite is about never exists.
                const float Dt = 1f / 60f;
                player.Nitro.Installed = true;
                player.AutoNitro = true;
                int engagedAt = -1, firstEmit = -1, lastEmit = -1;
                string midCensus = "none";
                bool onOwnExhaust = false;
                const int Frames = 180;
                for (int i = 0; i < Frames; i++)
                {
                    player.SimStep(Dt);
                    if (player.Nitro.EngagedThisTick && engagedAt < 0)
                        engagedAt = i;
                    rig.Advance(Dt);
                    var rows = rig.Emitters.Census
                        .Where(r => r.Name.StartsWith("nitropuff", System.StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (rows.Any(r => r.Emitting))
                    {
                        if (firstEmit < 0)
                            firstEmit = i;
                        lastEmit = i;
                    }
                    if (i == 30)
                    {
                        midCensus = rows.Count == 0 ? "none" : string.Join(", ",
                            rows.Select(r => $"{r.Name}@{r.Host} emitting={r.Emitting} live={r.LiveParticles}"));
                        onOwnExhaust = rows.Any(r => r.Emitting && r.HostNode != null
                                                     && planeModel.IsAncestorOf(r.HostNode));
                    }
                }
                ctx.Note($"engaged at frame {engagedAt}, exhaust puffers emitting frames {firstEmit}..{lastEmit} of {Frames}; at frame 30 [{midCensus}]");

                ctx.Check(player.Nitro.Boosting && player.Nitro.BoostAnimAlive,
                    $"{model}: the held command engaged the boost through the flight step boosting={player.Nitro.Boosting} animAlive={player.Nitro.BoostAnimAlive}");
                // The edge, not the flag: the boost animation, the shake and the loop sound all hang
                // off this one read, and a step that clears it before the read cancels all three
                // while the boost itself still accelerates the aircraft.
                ctx.Check(engagedAt == 0,
                    $"{model}: the engage EDGE reached the step's own reader on the engaging frame engagedAt={engagedAt}");

                // Where the def landed. Its authored NAME resolves nothing on an airframe, so the
                // anchor must be the plane model the call site passes; a staged template root
                // answering that name instead would put the whole sequence on a neighbour.
                var anchors = rig.AnchorsOf(world.Session.Program.ByAnimName("nitro_boost")[0]);
                string anchorNames = anchors.Count == 0 ? "-"
                    : string.Join(",", anchors.Select(a => a == null ? "null" : AnimRuntime.NameOf(a)));
                ctx.Check(anchors.Count == 0,
                    $"{model}: nitro_boost's NAME resolves nothing in the session rig, so the call site's plane model is its anchor [{anchorNames}]");

                ctx.Check(firstEmit == 0,
                    $"{model}: the exhaust puffers start emitting on the engage frame firstEmit={firstEmit}");
                ctx.Check(onOwnExhaust,
                    $"{model}: …on the aircraft's own exhaust nodes, not a staged neighbour's [{midCensus}]");
                // The burst is one second long because the def says so: each PUFFER_STATE 1 is paired
                // with its own INACTIVE at ANIMATION_OFFSET 1, alongside the 1.0 s opacity ramps. A
                // burst that outlives that is a stop this runtime dropped, not a longer boost.
                ctx.Check(lastEmit is >= 55 and <= 65,
                    $"{model}: …and stop at the def's own ANIMATION_OFFSET 1 stop, one second in lastEmit={lastEmit}");
            }
            finally
            {
                player?.Free();
                textures.Dispose();
            }
        });
    }

    // Every edge that reaches the original's two prop anim slots, on the rig a session builds and
    // through the flight step. The disabled-systems mask's bit-2 pair: the choke puts the wind-down
    // on the slot, and the timer expiring puts the silent restart back. The death and the respawn
    // are the other two edges. The AI arm runs beside the human one because the original's choker
    // branch has no player guard. ⚠ The third arm is the pair's only interaction: a choked aircraft
    // that then crashes must not replay the wind-down. It asserts an EXECUTED state stays EXECUTED.
    [Suite("prop-slot-edges",
        "an aircraft's two propeller slots on a session-built rig, on the human rig and the AI one alike: the choke plays stopprops, which cross-fades the blur discs out to the still blade and holds the slot for as long as the engine is out, the timer expiring plays the silent spinprops back with its endless XYZ_ROTATION suppressed so PropAnimator stays the only writer on those discs, a crash on an already choked aircraft leaves the wind-down where it is instead of playing a second one, a kill winds the discs down on the same once-per-death event the engine-stop cue rides, and a respawn taken inside that wind-down puts the spinning discs back rather than flying the still blade")]
    internal static void ChokePropEdges(TestContext ctx)
    {
        const string model = "player_warhawk";
        const float Dt = 1f / 60f;
        const int Running = 2, Executed = 3;   // AnimRuntime.AnimStateOf's ANIM_STATE numbering
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
            var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
            var built = new List<FlightController>();
            try
            {
                var factory = new Session.World.WorldEffectsFactory(
                    SessionSpec.Parse(System.Array.Empty<string>()), ctx.Host, () => Vector3.Zero);
                var stats = PlaneStats.Load(ctx.ZrdrPath, model);
                FlightController? Build(bool human, Vector3 spawn, int index)
                {
                    // ⚠ spinningProps, as both flight assemblers build: the exterior build drops the
                    // blur layers outright, so the two definitions would have no propN to cross-fade.
                    var builder = new PlaneBuilder(planesGamez, textures, spinningProps: true);
                    var planeModel = builder.Build(model);
                    var rig = new FlightController
                    {
                        PlaneModel = planeModel,
                        Collider = PlaneCollider.Build(planeModel),
                        PlayerIndex = index,
                        IsHumanPiloted = human,
                        Pilot = human ? null : AiPilot.HoldingCourse(spawn, spawn + Vector3.Forward),
                        UseKeyboard = false,
                        PadDevices = System.Array.Empty<int>(),
                        AllowPause = false,
                        Damage = PlaneDamage.For(stats),
                    };
                    rig.AddChild(planeModel);
                    rig.Setup(new FlightModel(stats), null, new CamParams(), spawn, spawn + Vector3.Forward);
                    rig.Name = (human ? "p" : "ai") + index;
                    ctx.Host.AddChild(rig);
                    built.Add(rig);
                    factory.BuildFlightCrashRuntime(rig, builder, model, world.Gamez,
                        world.Session.Builder.Scene, textures, world.Session.Program, verbose: false,
                        planesGamez: planesGamez);
                    if (rig.CrashRuntime is not { } runtime)
                    {
                        ctx.Check(false, $"{rig.Name}: the session rig built a crash runtime");
                        return null;
                    }
                    runtime.ManualAdvance = true;
                    // The spawn choreography, replayed the way both assemblers do. Setup ran before
                    // this runtime existed, so the rig would otherwise fly with no definition ever
                    // having touched its two prop presentations.
                    runtime.Play("startprops", planeModel, applyReset: false);
                    return rig;
                }

                // A kilometre apart and three kilometres up, so neither aeroplane reaches the other
                // or the ground over the ten seconds this suite flies them.
                var human = Build(true, new Vector3(0f, 3000f, 0f), 0);
                var ai = Build(false, new Vector3(1000f, 3000f, 0f), FlightRoster.ShooterIdBase);
                if (human == null || ai == null)
                {
                    return;
                }

                void Fly(float seconds)
                {
                    for (int i = 0; i < Mathf.RoundToInt(seconds / Dt); i++)
                    {
                        foreach (var rig in built)
                        {
                            rig.SimStep(Dt);
                            rig.CrashRuntime?.Advance(Dt);
                        }
                    }
                }

                // Flies until what is being waited on has landed, rather than for a duration read
                // off the definition. The authored RUN_TIME is not when a definition's state lands.
                // Reaching the cap is the failure the check that follows then reports.
                float FlyUntil(System.Func<bool> settled, float capSeconds)
                {
                    float flown = 0f;
                    while (!settled() && flown < capSeconds)
                    {
                        Fly(Dt);
                        flown += Dt;
                    }
                    return flown;
                }

                // Whether a named disc under this aeroplane is on the screen. ⚠ Both channels, not
                // the active bit alone: each cross-fade drives the opacity first and clears the bit
                // after. A disc faded to nothing is gone to the eye whatever its bit says.
                // Absent reads as not shown, and a subtree nothing has faded reads as solid.
                static bool Shown(FlightController rig, string node)
                {
                    if (Find(rig.PlaneModel!, node) is not { } found || !found.Visible)
                    {
                        return false;
                    }

                    float alpha = Alpha(found);
                    return alpha < 0f || alpha > 0.01f;
                }

                // ⚠ The spawn definition's own end, not the moment the discs look right. Its last
                // OBJECT_ACTIVE_STATE lands a frame after the fade it follows. A choke taken on
                // that frame would have its own staticpropN activation undone by it.
                float open = FlyUntil(() => human.CrashRuntime!.AnimStateOf("startprops") == Executed
                                            && ai.CrashRuntime!.AnimStateOf("startprops") == Executed, 20f);
                foreach (var rig in built)
                {
                    ctx.Check(!rig.PropsStopped && Shown(rig, "prop1") && !Shown(rig, "staticprop1"),
                        $"{rig.Name}: opens with the spinning discs on the slot stopped={rig.PropsStopped} prop1={Shown(rig, "prop1")} staticprop1={Shown(rig, "staticprop1")} after={open:0.00} s");
                }

                // The rising edge, read on the frame the choke lands rather than the next step.
                foreach (var rig in built)
                {
                    ctx.Check(rig.TryChokeEngine(3f) && rig.PropsStopped
                              && rig.CrashRuntime!.AnimStateOf("stopprops") == Running,
                        $"{rig.Name}: the choke put stopprops on the slot on its own frame stopped={rig.PropsStopped} state={rig.CrashRuntime!.AnimStateOf("stopprops")}");
                }

                // Inside the choke's own three seconds, so the reading below is the engine-out
                // presentation and not the restart's.
                float down = FlyUntil(
                    () => !Shown(human, "prop1") && Shown(human, "staticprop1")
                          && !Shown(ai, "prop1") && Shown(ai, "staticprop1"), 2.5f);
                foreach (var rig in built)
                {
                    ctx.Check(rig.EngineDeadRemainingS > 0f && rig.PropsStopped
                              && !Shown(rig, "prop1") && Shown(rig, "staticprop1"),
                        $"{rig.Name}: …and the cross-fade left the still blade alone on the aeroplane while the engine is out prop1={Shown(rig, "prop1")} staticprop1={Shown(rig, "staticprop1")} dead={rig.EngineDeadRemainingS:0.00} s after={down:0.00} s");
                }

                // Past the choke's own three seconds: the falling edge is the timer running out
                // inside the flight step, with nothing else touching the aeroplane.
                FlyUntil(() => human.EngineDeadRemainingS == 0f && ai.EngineDeadRemainingS == 0f, 4f);
                foreach (var rig in built)
                {
                    ctx.Check(rig.EngineDeadRemainingS == 0f && !rig.PropsStopped
                              && Shown(rig, "prop1") && !Shown(rig, "staticprop1"),
                        $"{rig.Name}: the timer expiring put the blur discs back stopped={rig.PropsStopped} prop1={Shown(rig, "prop1")} staticprop1={Shown(rig, "staticprop1")}");
                }

                // Past the definition's own 0.1 s ANIMATION_OFFSET, which is all that is left of it
                // once the endless motion behind that offset is suppressed.
                Fly(0.5f);
                foreach (var rig in built)
                {
                    // The restart is spinprops and not startprops, whose snd_propstart the
                    // original's data plays nowhere. EXECUTED rather than RUNNING, because its only
                    // sustain is the endless motion this rig suppresses (docs/verification.md, INSTR-74).
                    ctx.Same(Executed, rig.CrashRuntime!.AnimStateOf("spinprops"),
                        $"{rig.Name}: the restart ran spinprops");
                    ctx.Check(rig.CrashRuntime!.SuppressedMotionAnims.Contains("spinprops"),
                        $"{rig.Name}: …with its endless XYZ_ROTATION suppressed, so PropAnimator stays the only writer on the discs");
                }

                // The third arm: choke, let the wind-down finish, then crash. ⚠ The choke outlasts
                // the whole wind-down. A shorter choke or a restart inside it would clear the
                // slot, and the crash below would take the second stopprops this arm forbids.
                foreach (var rig in built)
                {
                    rig.TryChokeEngine(60f);
                }

                float wind = FlyUntil(() => human.CrashRuntime!.AnimStateOf("stopprops") == Executed
                                            && ai.CrashRuntime!.AnimStateOf("stopprops") == Executed, 30f);
                foreach (var rig in built)
                {
                    ctx.Same(Executed, rig.CrashRuntime!.AnimStateOf("stopprops"),
                        $"{rig.Name}: the second choke's wind-down has run to its end before the crash after={wind:0.00} s");
                    rig.DebugForceCrash();
                    ctx.Check(rig.PropsStopped && rig.CrashRuntime!.AnimStateOf("stopprops") == Executed,
                        $"{rig.Name}: the crash on a choked aeroplane left that wind-down where it was rather than playing a second state={rig.CrashRuntime!.AnimStateOf("stopprops")}");
                }

                // Back in the air off the stopped presentation the crash above left behind. That is
                // the spawn choreography's own job: nothing else writes those opacities back.
                foreach (var rig in built)
                {
                    rig.Respawn();
                }

                float spun = FlyUntil(() => Shown(human, "prop1") && !Shown(human, "staticprop1")
                                            && Shown(ai, "prop1") && !Shown(ai, "staticprop1"), 4f);
                foreach (var rig in built)
                {
                    ctx.Check(!rig.PropsStopped && Shown(rig, "prop1") && !Shown(rig, "staticprop1"),
                        $"{rig.Name}: the respawn put the blur discs back on a hull that went down stopped prop1={Shown(rig, "prop1")} staticprop1={Shown(rig, "staticprop1")} after={spun:0.00} s");
                }

                // The fourth arm, the death: a kill with no choke anywhere near it winds the discs
                // down. The visual half rides the same once-per-death shutdown that plays
                // snd_propstop (docs/org/ordnanceTypes.md). Read on the kill's own frame.
                foreach (var rig in built)
                {
                    if (rig.Damage is not { } ledger)
                    {
                        ctx.Check(false, $"{rig.Name}: the session rig carries a damage ledger");
                        continue;
                    }

                    float overkill = (ledger.WholeHealthMax + ledger.WholeArmorMax) * 4f;
                    rig.TakeCollisionHit(overkill, overkill, rig.GlobalPosition, 0);
                    ctx.Check(rig.Destroyed && rig.PropsStopped
                              && rig.CrashRuntime!.AnimStateOf("stopprops") == Running,
                        $"{rig.Name}: the kill put stopprops on the slot on its own frame destroyed={rig.Destroyed} stopped={rig.PropsStopped} state={rig.CrashRuntime!.AnimStateOf("stopprops")}");
                }

                // ⚠ Read the kill's cross-fade as OPACITY, never as visibility. The destroy def
                // plays over the wind-down and deactivates the healthy hull's own nodes. A dead
                // aeroplane carries no propeller of either kind to look at.
                static float DiscAlpha(FlightController rig, string node) =>
                    Find(rig.PlaneModel!, node) is { } found ? Alpha(found) : -9f;

                // The definition's own ends rather than a count of frames: propN is authored to
                // zero over 1.5 s and staticpropN to one over 2.0 s.
                float dead = FlyUntil(
                    () => DiscAlpha(human, "prop1") <= 0.01f && DiscAlpha(human, "staticprop1") >= 0.99f
                          && DiscAlpha(ai, "prop1") <= 0.01f && DiscAlpha(ai, "staticprop1") >= 0.99f, 2.5f);
                foreach (var rig in built)
                {
                    ctx.Check(DiscAlpha(rig, "prop1") <= 0.01f && DiscAlpha(rig, "staticprop1") >= 0.99f,
                        $"{rig.Name}: …and the kill's wind-down ran the authored cross-fade to its ends prop1={DiscAlpha(rig, "prop1"):0.00} staticprop1={DiscAlpha(rig, "staticprop1"):0.00} after={dead:0.00} s");
                }

                // The fifth arm, the respawn taken INSIDE the wind-down rather than after it. The
                // two definitions would otherwise write the same discs at once, one fading
                // staticpropN in while the other fades it out.
                foreach (var rig in built)
                {
                    ctx.Same(Running, rig.CrashRuntime!.AnimStateOf("stopprops"),
                        $"{rig.Name}: the wind-down is still on the slot as the respawn lands state={rig.CrashRuntime!.AnimStateOf("stopprops")}");
                    rig.Respawn();
                }

                float restored = FlyUntil(() => Shown(human, "prop1") && !Shown(human, "staticprop1")
                                                && Shown(ai, "prop1") && !Shown(ai, "staticprop1"), 4f);
                foreach (var rig in built)
                {
                    ctx.Check(!rig.PropsStopped && Shown(rig, "prop1") && !Shown(rig, "staticprop1")
                              && rig.CrashRuntime!.AnimStateOf("stopprops") != Running,
                        $"{rig.Name}: the respawn took the wind-down off the slot and put the blur discs back prop1={Shown(rig, "prop1")} staticprop1={Shown(rig, "staticprop1")} stop={rig.CrashRuntime!.AnimStateOf("stopprops")} after={restored:0.00} s");
                }
            }
            finally
            {
                foreach (var rig in built)
                {
                    rig.Free();
                }
                textures.Dispose();
            }
        });
    }

    // The three arms of an engage that only an AI takes, on the rig a session builds: the shake
    // def a person never gets, the keyed loop sound, and the decay lockout. The maneuver is forced
    // by handing the mode machine a one-entry library, so the engage arrives through the same
    // `Maneuver.Nitro` test the live chooser reaches it by.
    [Suite("nitro-ai-edges",
        "an AI's nitro engage on a session-built rig: medium_aishake rocks the aircraft's own healthy node where a person gets the camera shake, the positional snd_nitro loop is a 0.1 s blip at the engage and about a second more after the maneuver rather than a sustain, and the decay lockout lasts exactly as long as the nitro_decay INSTANCE the runtime holds")]
    internal static void NitroAiEdges(TestContext ctx)
    {
        const string model = "player_warhawk";
        const float Dt = 1f / 60f;
        const int Frames = 900;
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
            var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
            var soundDefs = SoundDefs.Load(ctx.ZrdrPath);
            using var sounds = new SoundArchive(ctx.SoundsPath);
            FlightController? ai = null;
            try
            {
                var factory = new Session.World.WorldEffectsFactory(
                    SessionSpec.Parse(System.Array.Empty<string>()), ctx.Host, () => Vector3.Zero);
                var spawn = new Vector3(0f, 500f, 0f);
                var stats = PlaneStats.Load(ctx.ZrdrPath, model);
                var builder = new PlaneBuilder(planesGamez, textures);
                var planeModel = builder.Build(model);
                var nitroEvade = Maneuvers.Load(ctx.ZrdrPath).FirstOrDefault(m => m.Nitro);
                if (nitroEvade == null)
                {
                    ctx.Check(false, $"the shipped maneuver library carries a nitro-flagged maneuver");
                    return;
                }

                // One-entry library, so the chooser's own weighted draw can only return the
                // nitro-flagged maneuver: the subject here is the engage, not the selection.
                var machine = new AiModeMachine(new System.Random(11))
                {
                    NaturalTouch = 9,
                    Library = new[] { nitroEvade },
                    // The injector cull, which the live rig wires off its own aircraft: this one
                    // carries the injector, so the flagged entry is drawable.
                    NitroUsable = () => true,
                };
                var pilot = AiPilot.HoldingCourse(spawn, spawn + Vector3.Forward);
                pilot.Machine = machine;
                ai = new FlightController
                {
                    PlaneModel = planeModel,
                    Collider = PlaneCollider.Build(planeModel),
                    PlayerIndex = FlightRoster.ShooterIdBase,
                    IsHumanPiloted = false,
                    Pilot = pilot,
                    UseKeyboard = false,
                    PadDevices = System.Array.Empty<int>(),
                    AllowPause = false,
                    Damage = PlaneDamage.For(stats),
                };
                ai.AddChild(planeModel);
                ai.Setup(new FlightModel(stats, aiForcePath: true), null, new CamParams(),
                    spawn, spawn + Vector3.Forward);
                ctx.Host.AddChild(ai);
                ai.Nitro.Installed = true;
                // The listener rides the aircraft, so the distance cull cannot stand in for a loop
                // this suite claims is silent.
                ai.EngineAudio = AiEngineAudio.Attach(ai, sounds, soundDefs, stats,
                    () => new[] { ai!.WorldPosition });
                factory.BuildFlightCrashRuntime(ai, builder, model, world.Gamez,
                    world.Session.Builder.Scene, textures, world.Session.Program, verbose: false,
                    planesGamez: planesGamez);
                if (ai.CrashRuntime is not { } rig)
                {
                    ctx.Check(false, $"{model}: the session rig built a crash runtime");
                    return;
                }
                rig.ManualAdvance = true;

                // One tick first, so the machine knows where the aeroplane is. The selection vetoes
                // a program predicted to end under the altitude floor, and an unseated machine
                // would read the origin.
                machine.Update(spawn, Vector3.Forward, null, null, Dt, attitude: Basis.Identity);

                // A bite covering the whole pool, which the decoded roll fails outright.
                machine.NotifyDamage(0f, 10f, 0f, 10f);
                bool armed = machine.Executor is { Maneuver.Nitro: true };
                ctx.Check(armed,
                    $"the mode machine is flying '{machine.Executor?.Maneuver.Name ?? "-"}', a nitro-flagged maneuver");

                var healthy = Find(planeModel, "healthy");
                int engagedAt = -1, releasedAt = -1, shakeFrom = -1, shakeTo = -1;
                int loopFrames = 0, lastLoopFrame = -1, firstSilentAfterEngage = -1;
                int lockoutFrames = 0, decayRunningFrames = 0, lockoutOutlivedDecay = 0;
                float maxShakeDeg = 0f;
                for (int i = 0; i < Frames; i++)
                {
                    ai.SimStep(Dt);
                    rig.Advance(Dt);
                    if (ai.Nitro.EngagedThisTick && engagedAt < 0)
                        engagedAt = i;
                    if (ai.Nitro.ReleasedThisTick && releasedAt < 0)
                        releasedAt = i;
                    bool shaking = rig.AnimStateOf(Flight.Airframe.EffectCatalogue.AiShakeAnim) == 2;
                    if (shaking)
                    {
                        if (shakeFrom < 0)
                            shakeFrom = i;
                        shakeTo = i;
                        if (healthy != null)
                            maxShakeDeg = Mathf.Max(maxShakeDeg, RotationMagnitudeDeg(healthy));
                    }
                    if (ai.EngineAudio is { NitroSounding: true })
                    {
                        loopFrames++;
                        lastLoopFrame = i;
                    }
                    else if (engagedAt >= 0 && firstSilentAfterEngage < 0)
                    {
                        firstSilentAfterEngage = i;
                    }

                    bool decayRunning = rig.AnimStateOf("nitro_decay") == 2;
                    decayRunningFrames += decayRunning ? 1 : 0;
                    if (ai.Nitro.DecayAnimPlaying)
                    {
                        lockoutFrames++;
                        if (!decayRunning && releasedAt >= 0 && i > releasedAt)
                            lockoutOutlivedDecay++;
                    }
                }

                ctx.Note($"engaged frame {engagedAt}, released {releasedAt}; shake instance frames {shakeFrom}..{shakeTo} peak {maxShakeDeg:0.00}°; loop sounding {loopFrames} frame(s), first silence at {firstSilentAfterEngage}, last at {lastLoopFrame}; decay instance {decayRunningFrames} frame(s), lockout {lockoutFrames}");

                ctx.Check(engagedAt == 0,
                    $"{model}: the nitro-flagged maneuver engaged the boost on its first step engagedAt={engagedAt}");
                // The shake: an aircraft nobody is sitting in gets the plane-rocking def instead of
                // the camera shake, on its own node, and it is a finite three-loop wobble.
                ctx.Check(shakeFrom == engagedAt,
                    $"{model}: {Flight.Airframe.EffectCatalogue.AiShakeAnim} starts on the engaging frame shakeFrom={shakeFrom}");
                ctx.Check(shakeTo > shakeFrom && shakeTo < Frames - 1,
                    $"{model}: …and ends with the def rather than running on shakeTo={shakeTo}");
                ctx.Check(healthy != null && maxShakeDeg > 0.5f,
                    $"{model}: …having rocked the aircraft's own healthy node peak={maxShakeDeg:0.00}°");

                // The loop: keyed, so its cadence is the state machine's call pattern. A sustain
                // would sound for every frame of the burn instead.
                ctx.Check(loopFrames > 0 && firstSilentAfterEngage is > 0 and <= 12,
                    $"{model}: the loop is a blip at the engage, silent again by frame {firstSilentAfterEngage}");
                ctx.Check(releasedAt > 0 && lastLoopFrame >= releasedAt - 3 && lastLoopFrame <= releasedAt + 12,
                    $"{model}: …and sounds again through the release calls that follow the maneuver lastLoop={lastLoopFrame} released={releasedAt}");
                ctx.Check(loopFrames < Frames / 2,
                    $"{model}: …never as a sustain over the whole burn loopFrames={loopFrames} of {Frames}");

                // ⚠ One step of lag is the mechanism, not slack: the step polls the runtime once,
                // so the instance always ends first. The shipped def runs the same second a fixed
                // clock would, so what this measures is the END it tracks, not the length.
                ctx.Check(decayRunningFrames > 0,
                    $"{model}: the release started a nitro_decay instance decayFrames={decayRunningFrames}");
                ctx.Check(lockoutOutlivedDecay <= 1,
                    $"{model}: …and the re-engage lockout ends with it, not on a clock of its own (outlived it by {lockoutOutlivedDecay} frame(s))");
            }
            finally
            {
                ai?.Free();
                textures.Dispose();
            }
        });
    }

    // The pre-warm's contract on a replica rig: after Bind and PrewarmEmitters nothing emits, a
    // crash and a panel tear reach the factory for no emitter, the claims count as built, and
    // respawn keeps the emitters so the next crash builds nothing either.
    [Suite("emitter-prewarm",
        "a crash rig's and the world-effects stage's PUFFER_STATE emitters are built at bind, unstarted: a crash, a panel tear, a post-respawn crash and five sonic bursts over a four-slot pool all reach the factory for no emitter, and the claims still count as built")]
    internal static void EmitterPrewarm(TestContext ctx)
    {
        const string model = "player_bhawk";
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
            var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
            var controller = new Node3D { Name = "controller_replica" };
            var fake = new CountingEmitterFactory();
            var runtime = AnimRuntime.ForCrashRig(
                Session.World.WorldEffectsFactory.NewCrashTemplateStage(), 1, fake, false);
            runtime.ManualAdvance = true;
            try
            {
                var builder = new PlaneBuilder(planesGamez, textures);
                var planeModel = builder.Build(model);
                controller.AddChild(planeModel);
                var crashRoot = new Node3D { Name = "player" };
                crashRoot.SetMeta(AnimRuntime.NameMeta, "player");
                crashRoot.Transform = planeModel.Transform;
                controller.AddChild(crashRoot);
                var program = world.Session.Program;
                var crashDefs = Flight.Airframe.EffectCatalogue.CrashDefTable(program);
                var rootNames = Session.World.WorldEffectsFactory.CrashStageRootNames(
                    program, world.Gamez, controller, crashDefs);
                Session.World.WorldEffectsFactory.StageCrashTemplates(world.Gamez,
                    world.Session.Builder.Scene, crashRoot, rootNames, Utils.EffectPools.Load());
                var wreck = builder.BuildDestroyed(model);
                if (wreck != null)
                {
                    wreck.Visible = false;
                    crashRoot.AddChild(wreck);
                }

                ctx.Host.AddChild(controller);
                ctx.Host.AddChild(runtime);
                runtime.Bind(controller,
                    program.Subset(Flight.Airframe.EffectCatalogue.CrashRigAnimNames(crashDefs)));
                for (int i = 0; i < 6; i++)
                {
                    runtime.Advance(1f / 60f);
                }

                int factoryAtBind = fake.Built.Count;
                int builtAtBind = runtime.PuffersBuilt;
                var warmed = runtime.PrewarmEmitters(planeModel, crashRoot);
                int warmedCount = fake.Built.Count;
                ctx.Note($"pre-warm: built={warmed.Built} unhosted={warmed.Unhosted} self_hosted={warmed.SelfHosted} factory_calls={warmedCount - factoryAtBind}");
                ctx.Check(warmed.Built > 0 && warmedCount - factoryAtBind == warmed.Built,
                    $"{model}: the pre-warm built its emitters through the factory built={warmed.Built}");
                ctx.Check(fake.Built.All(e => e.Started == 0 && !e.Sustaining),
                    $"{model}: nothing the pre-warm built has started");
                ctx.Check(runtime.PuffersBuilt == builtAtBind,
                    $"{model}: the pre-warm does not count as built (PuffersBuilt {runtime.PuffersBuilt})");
                ctx.Check(runtime.Emitters.Census.All(r => !r.Emitting),
                    $"{model}: no census row emits after the pre-warm");

                // The crash: every emitter it asserts must already exist.
                runtime.Play("player_crash_dirt", crashRoot, applyReset: false);
                for (int i = 0; i < 180; i++)
                {
                    runtime.Advance(1f / 60f);
                }

                ctx.Check(fake.Built.Count == warmedCount,
                    $"{model}: the crash reaches the factory for no emitter (built {fake.Built.Count - warmedCount} more)");
                ctx.Check(fake.Built.Any(e => e.Started > 0),
                    $"{model}: the crash started pre-warmed emitters");
                ctx.Check(runtime.PuffersBuilt > builtAtBind,
                    $"{model}: a claimed emitter counts as built (PuffersBuilt {runtime.PuffersBuilt})");

                // A panel tear, the damage sink's own call shape.
                runtime.Play("pdpanel5", planeModel, applyReset: false);
                for (int i = 0; i < 6; i++)
                {
                    runtime.Advance(1f / 60f);
                }

                var late = fake.Built.Skip(warmedCount).Select(e => e.Key).ToList();
                var lateRows = runtime.Emitters.Census
                    .Where(r => late.Contains(r.Name) && r.Emitting)
                    .Select(r => $"{r.Name}@{r.Host}[{r.Def}]");
                ctx.Check(fake.Built.Count == warmedCount,
                    $"{model}: the tear reaches the factory for no emitter (built {late.Count} more: {string.Join(", ", lateRows)})");
                int afterTear = fake.Built.Count;

                // Respawn keeps the emitters, so the second crash builds nothing either.
                runtime.ResetToBaseState();
                ctx.Check(fake.Built.All(e => e.IsValid && !e.Sustaining),
                    $"{model}: respawn keeps every emitter, stopped");
                runtime.Play("player_crash_dirt", crashRoot, applyReset: false);
                for (int i = 0; i < 60; i++)
                {
                    runtime.Advance(1f / 60f);
                }

                ctx.Check(fake.Built.Count == afterTear,
                    $"{model}: the second crash reaches the factory for no emitter (built {fake.Built.Count - afterTear} more)");
                ctx.Check(fake.Built.Any(e => e.Sustaining),
                    $"{model}: the second crash emits from the kept emitters");
            }
            finally
            {
                runtime.Free();
                controller.Free();
                textures.Dispose();
            }
        });
        WorldEffectsPrewarm(ctx);
    }

    // Every wreck node's rest pose, the local mirror of
    // `WorldEffectsFactory.CollectRestPoses`, so the suite's respawn ritual can re-home the
    // flung pieces the way `FlightController.Respawn` does.
    internal static void CollectRestPoses(Node3D node, List<(Node3D Node, Transform3D RestPose)> into)
    {
        into.Add((node, node.Transform));
        foreach (var child in node.GetChildren())
        {
            if (child is Node3D sub)
            {
                CollectRestPoses(sub, into);
            }
        }
    }

    // Meshes drawing under one staged template copy, visibility taken in-tree, so a
    // parent the reset pass switched off darkens the whole copy the way it does on screen.
    internal static int LitMeshCount(Node3D copy)
    {
        int n = copy is MeshInstance3D lit && lit.IsVisibleInTree() ? 1 : 0;
        foreach (var child in copy.GetChildren())
        {
            if (child is Node3D sub)
            {
                n += LitMeshCount(sub);
            }
        }
        return n;
    }

    // ---- a mid-flight AI introduction owes its crash rig, and pays it later -----------------

    // The deferral's two halves, checked on the production spawn path rather than on a replica rig:
    // the launch frame leaves the rig unbuilt, and the rig arrives complete however the caller gets
    // there. ⚠ Read CrashRigPending before anything else on a deferred aeroplane: CrashRuntime,
    // CrashAnchor and CrashDefs all force the build, which is the point of them.
    [Suite("ai-crash-rig-deferral",
        "a mid-flight AI introduction leaves its crash rig armed rather than built, the roster's pump takes more than one frame to finish it, and the finished rig is the same one an undeferred build makes, while a second aeroplane that is hit before the pump reaches it builds its rig on the damage intake instead (BL-641)")]
    internal static void AiCrashRigDeferral(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
            var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
            ProjectilePool? pool = null;
            FlightController? pumped = null;
            FlightController? forced = null;
            try
            {
                var live = new ProjectilePool(textures, null, null);
                pool = live;
                ctx.Host.AddChild(live);
                var spec = SessionSpec.Parse(System.Array.Empty<string>());
                var liveries = new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof"));
                var factory = new Session.World.WorldEffectsFactory(spec, ctx.Host, () => Vector3.Zero);
                var inputs = new AircraftAssemblyResources
                {
                    PlanesGamez = planesGamez,
                    StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                    AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                    PaintRng = new RandomNumberGenerator(),
                    ZrdrPath = ctx.ZrdrPath,
                    StockLoadouts = StockLoadouts.Load(),
                    WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
                    Textures = textures,
                    Shakes = ShakeDefs.Load(ctx.ZrdrPath),
                };
                var spawner = new FlightRoster(FlightRosterPolicy.From(spec), liveries, factory,
                    ctx.Host, inputs,
                    new FlightWorldBindings
                    {
                        Projectiles = live,
                        Gamez = world.Gamez,
                        WorldScene = world.Session.Builder.Scene,
                        CrashProgram = world.Session.Program,
                    },
                    new HumanRosterBindings());

                // The first pump is what puts the roster into deferring, so this stands in for the
                // session's first frame: an aeroplane introduced BEFORE it gets its rig in place.
                spawner.PumpDeferredCrashRigs();

                var at = new Vector3(0f, 500f, 0f);
                pumped = spawner.SpawnAi(new AiSpawn(ctx.PlaneName, at, at + Vector3.Forward,
                    AiPilot.HoldingCourse(at, at + Vector3.Forward)));
                ctx.Check(pumped.CrashRigPending && spawner.PendingCrashRigs == 1,
                    $"the launch frame left the rig armed rather than built pending={pumped.CrashRigPending} queued={spawner.PendingCrashRigs}");
                ctx.Check(pumped.IsInsideTree() && pumped.PlaneModel is { },
                    $"…and the aeroplane is already in the world with its model, which is what the launch frame is for");

                // The spread itself. One pump a frame, so a rig that finished in one call would be
                // no deferral at all: the count is the claim.
                int pumps = 0;
                while (spawner.PendingCrashRigs > 0 && pumps < 200)
                {
                    spawner.PumpDeferredCrashRigs();
                    pumps++;
                }
                ctx.Check(pumps > 1 && spawner.PendingCrashRigs == 0,
                    $"the pump finished the rig over {pumps} frames, none of them the launch frame");
                ctx.Check(!pumped.CrashRigPending && pumped.CrashRuntime != null
                          && pumped.CrashDefs != null && pumped.CrashAnchor != null,
                    $"the pumped rig is whole: runtime={pumped.CrashRuntime != null} defs={pumped.CrashDefs != null} anchor={pumped.CrashAnchor != null}");
                ctx.Check(pumped.Visuals is { DamageEffectSink: not null },
                    $"…including the damage-stage wiring the rig's second phase owns");

                // The forcing arm: an aeroplane hit while its rig is still queued builds it on the
                // intake, so no round can land on a plane whose wreck does not exist yet.
                var second = at + new Vector3(3000f, 0f, 0f);
                forced = spawner.SpawnAi(new AiSpawn(ctx.PlaneName, second, second + Vector3.Forward,
                    AiPilot.HoldingCourse(second, second + Vector3.Forward)));
                ctx.Check(forced.CrashRigPending,
                    $"the second aeroplane's rig is queued too pending={forced.CrashRigPending}");
                forced.TakeCollisionHit(0f, 0f, forced.GlobalPosition, 0);
                ctx.Check(!forced.CrashRigPending && spawner.PendingCrashRigs == 0,
                    $"the damage intake built it before spending anything queued={spawner.PendingCrashRigs}");

                // Same rig either way. The pool subtree is the part the step split touches, so its
                // shape is what a spread build could have got wrong.
                int pumpedPools = CountPools(pumped.CrashAnchor);
                int forcedPools = CountPools(forced.CrashAnchor);
                ctx.Same(pumpedPools, forcedPools,
                    $"the pumped rig staged the same pool slots as the forced one ({pumpedPools})");
                ctx.Check(pumpedPools > 0 && pumped.DestroyDef == forced.DestroyDef,
                    $"…and both took the same death slot '{pumped.DestroyDef ?? "-"}' over {pumpedPools} pool(s)");
            }
            finally
            {
                // ⚠ Both aeroplanes go, not just the pool. Suites share one host node, and an
                // aircraft left under it takes a name a later suite's own spawn wants, which Godot
                // then renames out from under that suite's identity checks.
                pumped?.Free();
                forced?.Free();
                if (pool != null)
                {
                    ctx.Host.RemoveChild(pool);
                    pool.QueueFree();
                }
            }
        });
    }

    // ---- an AI plane's crash picks from the ai_crash_* vector ----------------------------

    // The AI arm of the crash-family split, through the REAL factory call, which keys the family on
    // IsHumanPiloted: an AI controller's rig binds the ai_crash_* vector, a crash on a body stamped
    // dirt selects ai_crash_dirt, and a crash with no struck body takes the null-material arm to slot
    // 0, ai_crash_default, never a player_crash_* def. ⚠ Keep the human-piloted A/B control; without
    // it a family mix-up in the pick would be invisible from the AI side alone.
    [Suite("ai-crash-defs",
        "an AI plane's crash rig binds the ai_crash_* family and its crash indexes it by the struck surface id, dirt(13) plays ai_crash_dirt, no material plays ai_crash_default, and the def switches off both the airframe's healthy subtree and the crash root's wreck, while a human rig off the same factory keeps player_crash_* (G21)")]
    internal static void AiCrashDefs(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
            var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
            var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
            var factory = new Session.World.WorldEffectsFactory(
                SessionSpec.Parse(System.Array.Empty<string>()), ctx.Host, () => Vector3.Zero);
            FlightController? ai = null;
            FlightController? human = null;
            StaticBody3D? dirt = null;
            try
            {
                var spawn = new Vector3(0f, 500f, 0f);
                var builder = new PlaneBuilder(planesGamez, textures);
                var aiModel = builder.Build(ctx.PlaneName);
                ai = new FlightController
                {
                    PlaneModel = aiModel,
                    Collider = PlaneCollider.Build(aiModel),
                    PlayerIndex = FlightRoster.ShooterIdBase,
                    IsHumanPiloted = false,
                    Pilot = AiPilot.HoldingCourse(spawn, spawn + Vector3.Forward),
                    UseKeyboard = false,
                    PadDevices = System.Array.Empty<int>(),
                    AllowPause = false,
                };
                ai.AddChild(aiModel);
                ai.Setup(new FlightModel(stats), null, new CamParams(), spawn, spawn + Vector3.Forward);
                ctx.Host.AddChild(ai);
                // The planes gamez goes in as both spawners pass it: the destroy def's `chuteman`
                // is a template root of planes.zbd, and without it the rig cannot stage it.
                factory.BuildFlightCrashRuntime(ai, builder, ctx.PlaneName, world.Gamez,
                    world.Session.Builder.Scene, textures, world.Session.Program, verbose: false,
                    planesGamez: planesGamez);

                ctx.Check(ai.CrashRuntime != null && ai.CrashDefs != null,
                    $"the AI rig built a crash runtime with a def table");
                if (ai.CrashDefs == null)
                    return;
                ctx.Check(ai.CrashDefs.PlayableDefs.Count == 3
                          && ai.CrashDefs.PlayableDefs.All(d =>
                              d.StartsWith(Flight.Airframe.EffectCatalogue.AiCrashDefPrefix, System.StringComparison.Ordinal)),
                    $"the AI table's playable slots are the ai_crash_* trio [{string.Join(", ", ai.CrashDefs.PlayableDefs)}]");

                // The two subtrees one context node has to reach: `healthy` sits on the plane
                // model, `destroyed` under the crash root. Both shown first, or the wreck's
                // built-hidden state would answer for the deactivation instead of the def.
                var healthy = ai.PlaneModel?.FindChild("healthy", true, false) as Node3D;
                var wreck = ai.CrashAnchor?.FindChild("destroyed", true, false) as Node3D;
                if (healthy != null)
                    healthy.Visible = true;
                if (wreck != null)
                    wreck.Visible = true;

                // A crash on a known surface: a struck body stamped dirt(13), the id cascade's
                // own-slot arm, through the production Crash path.
                dirt = new StaticBody3D { Name = "dirt_probe" };
                dirt.SetMeta(SceneBuilder.SurfaceIdMeta, 13);
                ctx.Host.AddChild(dirt);
                ai.DebugForceCrash(null, dirt);
                ctx.Check(ai.Crashed && ai.LastCrashDef == Flight.Airframe.EffectCatalogue.AiCrashDefPrefix + "dirt",
                    $"an AI crash on dirt(13) plays ai_crash_dirt def={ai.LastCrashDef ?? "-"}");
                string healthyState = healthy == null ? "-" : healthy.Visible ? "on" : "off";
                string wreckState = wreck == null ? "-" : wreck.Visible ? "on" : "off";
                ctx.Check(healthy is { Visible: false } && wreck is { Visible: false },
                    $"…and the def's own OBJECT_ACTIVE_STATE events reach BOTH subtrees off one context node: healthy={healthyState} destroyed={wreckState}");

                // No struck body: the null-material arm resolves slot 0 of the SAME family.
                ai.Respawn();
                ai.DebugForceCrash();
                ctx.Check(ai.LastCrashDef == Flight.Airframe.EffectCatalogue.AiCrashDefPrefix + "default",
                    $"an AI crash with no material falls to ai_crash_default def={ai.LastCrashDef ?? "-"}");

                // The A/B control: a human rig through the same factory keeps the player family.
                var humanBuilder = new PlaneBuilder(planesGamez, textures);
                var humanModel = humanBuilder.Build(ctx.PlaneName);
                human = new FlightController
                {
                    PlaneModel = humanModel,
                    Collider = PlaneCollider.Build(humanModel),
                    PlayerIndex = 0,
                    UseKeyboard = false,
                    AllowPause = false,
                };
                human.AddChild(humanModel);
                human.Setup(new FlightModel(stats), ctx.Camera, new CamParams(),
                    spawn + new Vector3(2000f, 0f, 0f), spawn + new Vector3(2000f, 0f, -1f));
                ctx.Host.AddChild(human);
                factory.BuildFlightCrashRuntime(human, humanBuilder, ctx.PlaneName, world.Gamez,
                    world.Session.Builder.Scene, textures, world.Session.Program, verbose: false,
                    planesGamez: planesGamez);
                ctx.Check(human.CrashDefs != null && human.CrashDefs.PlayableDefs.All(d =>
                        d.StartsWith(Flight.Airframe.EffectCatalogue.CrashDefPrefix, System.StringComparison.Ordinal)),
                    $"the same factory keeps a human rig on player_crash_* [{string.Join(", ", human.CrashDefs?.PlayableDefs ?? System.Array.Empty<string>())}]");
                human.DebugForceCrash(null, dirt);
                ctx.Check(human.LastCrashDef == Flight.Airframe.EffectCatalogue.CrashDefPrefix + "dirt",
                    $"…and its dirt crash plays player_crash_dirt def={human.LastCrashDef ?? "-"}");
            }
            finally
            {
                dirt?.Free();
                human?.Free();
                ai?.Free();
                textures.Dispose();
            }
        });
    }

    internal static int Count(IReadOnlyDictionary<string, int> counts, string key) =>
        counts.TryGetValue(key, out var n) ? n : 0;

    // One exact health spend, so a band is entered by crossing it rather than by an approximate
    // hit: the armour pool is already empty, so the whole amount reaches the hull pair.
    internal static void SpendHullTo(FlightController ai, PlaneDamage damage, float fraction)
    {
        float spend = damage.WholeHealth - (fraction * damage.WholeHealthMax);
        if (spend > 0f)
            ai.TakeCollisionHit(0f, spend, ai.GlobalPosition, 0);
    }

    // The same healthy/destroyed role-swap test AnimRuntime.AuthorsSwap runs internally, exposed
    // here so StartStateSwapSyncsThePool can find a subject without depending on that private
    // method.
    private static bool AuthorsOwnSwap(AnimDefinition def) =>
        def.Sequences.Any(seq => !seq.OnCallOnly && seq.Events.Any(ev =>
        {
            if (ev.Kind != "ObjectActiveState")
            {
                return false;
            }

            var name = ev.Data.Str("node") ?? ev.Data.Str("name") ?? "";
            bool active = ev.Data.Bool("state");
            return (active && (name.Contains("destroyed", System.StringComparison.OrdinalIgnoreCase)
                                || name.Contains("dbase", System.StringComparison.OrdinalIgnoreCase)))
                || (!active && name.Contains("healthy", System.StringComparison.OrdinalIgnoreCase));
        }));

    // The same pre-warm at the second host, the world-effects stage: the sonic burst's puffer defs
    // are built at bind, and five plays over a four-slot pool then reach the factory for none of
    // them. Five, not one, because each fresh slot copy is its own host and so its own key.
    private static void WorldEffectsPrewarm(TestContext ctx)
    {
        const int slots = 4;
        const string anim = "sonic_ground_effect";
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var stage = OrdnanceSuites.StageBurstRoots(ctx, world, anim, slots);
            var fake = new CountingEmitterFactory();
            var runtime = AnimRuntime.ForEffects(
                AnimRuntime.NewTemplateStage(pooled: true, shown: true, placesCalled: true),
                1, fake, false, SuiteConstants.BurstTtl, () => ctx.Camera.GlobalPosition);
            runtime.ManualAdvance = true;
            ctx.Host.AddChild(stage);
            ctx.Host.AddChild(runtime);
            try
            {
                runtime.Bind(stage, world.Session.Program.Subset(anim));
                int atBind = fake.Built.Count;
                var warmed = runtime.PrewarmEmitters();
                int warmedCount = fake.Built.Count;
                ctx.Note($"world-effects pre-warm: built={warmed.Built} unhosted={warmed.Unhosted} self_hosted={warmed.SelfHosted}");
                ctx.Check(warmed.Built > 0 && warmedCount - atBind == warmed.Built,
                    $"{anim}: the pre-warm built its emitters through the factory built={warmed.Built}");
                ctx.Check(fake.Built.All(e => e.Started == 0 && !e.Sustaining),
                    $"{anim}: nothing the pre-warm built has started");
                ctx.Check(runtime.Emitters.Census.All(r => !r.Emitting),
                    $"{anim}: no census row emits after the pre-warm");

                int steps = Mathf.RoundToInt(SuiteConstants.BurstSeconds * 60f);
                for (int play = 1; play <= slots + 1; play++)
                {
                    ctx.Check(runtime.PlayEffectAt(anim, ctx.Camera.GlobalPosition), $"burst {play} started");
                    for (int i = 0; i < steps; i++)
                    {
                        runtime.Advance(1f / 60f);
                    }
                }

                var late = fake.Built.Skip(warmedCount).Select(e => e.Key).Distinct().ToList();
                ctx.Check(late.Count == 0,
                    $"{anim}: {slots + 1} bursts over {slots} slot(s) reach the factory for no emitter{(late.Count == 0 ? string.Empty : $", built {string.Join(", ", late)}")}");
                ctx.Check(fake.Built.Any(e => e.Started > 0),
                    $"{anim}: the bursts started pre-warmed emitters");
            }
            finally
            {
                runtime.Free();
                stage.Free();
            }
        });
    }

    // One start of the bullethole def, advanced past its authored second (its last event sits at
    // 0.91 s), so the whole of both Initial sequences has dispatched before anything is counted.
    private static void RunBulletDef(AnimRuntime runtime, AnimDefinition def, Node3D stage)
    {
        runtime.Start(def, stage);
        for (int i = 0; i < 120; i++)
            runtime.Advance(1f / 60f);
    }

    // How far a node has been rotated out of its rest pose, in degrees: the shake defs are authored
    // as XYZ_ROTATION on the plane's own body node, so any axis counts.
    private static float RotationMagnitudeDeg(Node3D node) =>
        Mathf.RadToDeg(node.Quaternion.Normalized().GetAngle());

    private static void CallbackSeamsReceiveTheirCodes(TestContext ctx, TestWorld world)
    {
        // 3.0 s past chuteman's authored start, which is what fury-fury's own callbacks follow.
        const int Steps = 360;
        var handed = new Vector3(11f, -23f, 37f);
        // The nodes player-player and fury-fury name, flat, so a call that misses one is visible
        // rather than absorbed by a shared ancestor.
        var destroyDefNodes = new[]
        {
            "healthy", "destroyed", "dontmove", "markers", "shadow", "cockpit1",
            "piece1", "piece2", "piece3", "piece4",
            "prop1", "prop1b", "wing_flare1", "wing_flare2", "staticprop1", "nitroprop1",
        };
        foreach (var animName in new[] { "player", "fury" })
        {
            var program = world.Session.Program.Subset(animName);
            var defs = program.ByAnimName(animName);
            ctx.Check(defs.Count > 0, $"chapter program has the self-named destroy def {animName} defs={defs.Count}");
            if (defs.Count == 0)
            {
                continue;
            }

            AnimationAndEffectsSuites.WithEmitterStage(ctx, program, $"Callback_{animName}", destroyDefNodes, (stage, runtime, fake) =>
            {
                int stops = 0, views = 0;
                runtime.WreckVelocity = () => handed;
                runtime.StopDamageStages = () => stops++;
                runtime.ResetPilotView = () => views++;
                runtime.Start(defs[0], stage);
                for (int i = 0; i < Steps; i++)
                {
                    runtime.Advance(1f / 60f);
                }

                ctx.Check(runtime.InheritedWorldVelocity.IsEqualApprox(handed) && runtime.InheritedVelocityArmed,
                    $"{animName}: CALLBACK 16 handed the instance the rig's velocity and ARMED it inherited={runtime.InheritedWorldVelocity} handed={handed} armed={runtime.InheritedVelocityArmed}");
                ctx.Same(1, stops, $"{animName}: CALLBACK 15 stopped the damage stages exactly once");
                // Both destroy arms raise 3 at their head and only one arm runs per play, so the
                // player's def reaches the view seam exactly once; the AI's authors it nowhere.
                ctx.Same(animName == "player" ? 1 : 0, views,
                    $"{animName}: CALLBACK 3 reached the view seam {views}×");
                foreach (var (key, n) in runtime.UnhandledEventCounts)
                {
                    ctx.Check(!key.Contains("no seam wired"),
                        $"{animName}: no acted-on code went unwired ({key}×{n})");
                }
            },
                asCrashRig: true);

            // THE CONTROL, and the unknown-code arm: the same def with no seams. Nothing moves, and
            // player-player's authored 3 is counted rather than guessed at either way.
            AnimationAndEffectsSuites.WithEmitterStage(ctx, program, $"CallbackBare_{animName}", destroyDefNodes, (stage, runtime, fake) =>
            {
                runtime.Start(defs[0], stage);
                for (int i = 0; i < Steps; i++)
                {
                    runtime.Advance(1f / 60f);
                }

                ctx.Check(runtime.InheritedWorldVelocity == Vector3.Zero && !runtime.InheritedVelocityArmed,
                    $"ABLE-TO-FAIL CONTROL {animName}: with no seam wired the wreck inherits nothing inherited={runtime.InheritedWorldVelocity} armed={runtime.InheritedVelocityArmed}");
                ctx.Check(runtime.UnhandledEventCounts.ContainsKey("Callback(16, no seam wired)")
                          && runtime.UnhandledEventCounts.ContainsKey("Callback(15, no seam wired)"),
                    $"...and both codes are reported as unwired rather than silently skipped keys=[{string.Join(",", runtime.UnhandledEventCounts.Keys)}]");
                if (animName == "player")
                {
                    ctx.Check(runtime.UnhandledEventCounts.TryGetValue("Callback(3, no seam wired)", out int unwired)
                              && unwired > 0,
                        $"player: with no seam wired the authored code 3 is reported unwired rather than acted on (×{unwired})");
                }
            },
                asCrashRig: true);
        }
    }

    // The census behind the prohibition on implementing code 0, the free/delete arm: no def in the
    // loaded program authors it, in a sequence or in a RESET_STATE.
    private static void CallbackCodeZeroIsNeverAuthored(TestContext ctx, TestWorld world)
    {
        var codes = new SortedDictionary<int, int>();
        foreach (var def in world.Session.Program.Defs)
        {
            var blocks = new List<AnimSequence>(def.Sequences);
            if (def.ResetState is { } reset)
            {
                blocks.Add(reset);
            }

            foreach (var block in blocks)
            {
                foreach (var ev in block.Events)
                {
                    if (ev.Kind != "Callback")
                    {
                        continue;
                    }

                    int code = (int)(ev.Data.Num("value") ?? -1f);
                    codes[code] = codes.TryGetValue(code, out int n) ? n + 1 : 1;
                }
            }
        }

        ctx.Note($"chapter {ctx.Chapter} authors callback codes {string.Join(", ", codes.Select(kv => $"{kv.Key}×{kv.Value}"))}");
        ctx.Same(0, codes.TryGetValue(0, out int zeroes) ? zeroes : 0,
            $"no def authors the free/delete arm, code 0");
        ctx.Check(codes.ContainsKey(16) && codes.ContainsKey(15),
            $"and the two codes this runtime acts on ARE authored here 16×{(codes.TryGetValue(16, out int c16) ? c16 : 0)} 15×{(codes.TryGetValue(15, out int c15) ? c15 : 0)}");
    }

    // The fall itself, from a height clear of both the ground and the altitude cap. Everything here
    // is one aircraft's own timeline, so the checks are ordered as the frames are.
    private static void WreckFallsBeforeItIsHandedOver(TestContext ctx, FlightRoster spawner,
        float fallFrom)
    {
        const float Dt = 1f / 60f;
        FlightController? ai = null;
        try
        {
            var spawn = new Vector3(0f, fallFrom, 0f);
            ai = spawner.SpawnAi(new AiSpawn("player_fury", spawn, spawn + Vector3.Forward,
                AiPilot.HoldingCourse(spawn, spawn + Vector3.Forward)));
            if (ai.CrashRuntime is not { } rig || ai.Damage is not { } damage
                || ai.PlaneModel is not { } model)
            {
                ctx.Check(false, $"the spawner built the AI Fury a crash runtime, a damage ledger and a model");
                return;
            }

            rig.ManualAdvance = true;
            ctx.Check(ai.DestroyDef == "fury" && ai.DestroyDefFliesWreck,
                $"the AI Fury's death slot is its own self-named def '{ai.DestroyDef ?? "-"}', and the def flies the hull itself (fliesOwnHull={ai.DestroyDefFliesWreck})");

            var starts = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            rig.OnInstanceStarted += (def, _) =>
            {
                string n = def.AnimName ?? def.Name ?? "";
                starts[n] = starts.TryGetValue(n, out var c) ? c + 1 : 1;
            };

            var healthy = Find(model, "healthy");
            var wreck = ai.CrashAnchor != null ? Find(ai.CrashAnchor, "destroyed") : null;

            // ⚠ Fly it before killing it. On the spawn frame the commands are still default, so the
            // frozen-commands check below cannot fail: freezing and neutralising are the same step
            // there. One second of live flight puts the pilot's real throttle in them.
            for (int i = 0; i < 60; i++)
                ai.SimStep(Dt);

            var posAtKill = ai.WorldPosition;
            var velAtKill = ai.WorldVelocity;

            // The kill through the production ram entry, both magnitudes, standing armour would
            // otherwise null the health damage outright (PlaneDamage.Spend).
            float overkill = (damage.WholeHealthMax + damage.WholeArmorMax) * 4f;
            ai.TakeCollisionHit(overkill, overkill, ai.GlobalPosition, 0);
            var commandAtKill = ai.LastCommand;   // the lever/surface command the AI think left behind

            ctx.Check(ai.Destroyed && ai.WreckFalling,
                $"the kill starts the destroy anim and leaves the hull FLYING destroyed={ai.Destroyed} falling={ai.WreckFalling}");
            ctx.Same(1, Count(starts, "fury"),
                $"the rig started the self-named destroy def exactly once on the kill");
            int crashStarts = starts.Where(kv => kv.Key.StartsWith(
                Flight.Airframe.EffectCatalogue.AiCrashDefPrefix, System.StringComparison.OrdinalIgnoreCase))
                .Sum(kv => kv.Value);
            ctx.Same(0, crashStarts,
                $"…and no ai_crash_* def, which belongs to the ground contact seconds away");
            ctx.Check(model.Visible && healthy is { Visible: true },
                $"the airframe is still there on the death frame model={model.Visible} healthy={(healthy?.Visible.ToString() ?? "-")}");

            // The fall, sampled every frame rather than at its ends: a hull hidden for part of it and
            // shown again would read as never hidden from the endpoints alone.
            int blankFrames = 0;
            int hullHiddenFrames = 0;
            float handoverAt = -1f;
            var velAtHandover = Vector3.Zero;
            var posAtHandover = posAtKill;
            // The lever/surface freeze: FUN_004b82d0 zeroes neither on the original, and nothing
            // here writes _lastInput once Crashed, so the same struct should read back
            // bit-identical on every one of these frames, not just at the endpoints.
            bool commandDrifted = false;
            var commandAtDrift = commandAtKill;
            for (float t = 0f; t < 8f && handoverAt < 0f; t += Dt)
            {
                ai.SimStep(Dt);
                // Read between the two halves of the frame: Callback 16 fires inside Advance below
                // and samples the hull the step above has just moved.
                velAtHandover = ai.WorldVelocity;
                posAtHandover = ai.WorldPosition;
                var command = ai.LastCommand;
                if (!commandDrifted && (command.Pitch != commandAtKill.Pitch
                    || command.Roll != commandAtKill.Roll || command.Yaw != commandAtKill.Yaw
                    || command.Throttle != commandAtKill.Throttle))
                {
                    commandDrifted = true;
                    commandAtDrift = command;
                }
                rig.Advance(Dt);
                if (!model.Visible)
                    hullHiddenFrames++;
                if (healthy is { Visible: false } && wreck is { Visible: false })
                    blankFrames++;
                if (!ai.WreckFalling)
                    handoverAt = t + Dt;
            }

            var commandAtHandover = ai.LastCommand;
            ctx.Note($"lever/surface command at kill: throttle={commandAtKill.Throttle:0.000} pitch={commandAtKill.Pitch:0.000} roll={commandAtKill.Roll:0.000} yaw={commandAtKill.Yaw:0.000}; at handover ({handoverAt:0.00} s): throttle={commandAtHandover.Throttle:0.000} pitch={commandAtHandover.Pitch:0.000} roll={commandAtHandover.Roll:0.000} yaw={commandAtHandover.Yaw:0.000}");
            ctx.Check(!commandDrifted,
                $"the lever/surface command stayed frozen every frame of the dead-hull flight (else it moved to throttle={commandAtDrift.Throttle:0.000} pitch={commandAtDrift.Pitch:0.000} roll={commandAtDrift.Roll:0.000} yaw={commandAtDrift.Yaw:0.000})");
            ctx.Check(commandAtHandover.Throttle == commandAtKill.Throttle
                && commandAtHandover.Pitch == commandAtKill.Pitch
                && commandAtHandover.Roll == commandAtKill.Roll
                && commandAtHandover.Yaw == commandAtKill.Yaw,
                $"…and reads back bit-identical at the handover (BL-451)");

            float travelled = posAtHandover.DistanceTo(posAtKill);
            ctx.Note($"the AI Fury's wreck flew {travelled:0} m of its own in {handoverAt:0.00} s, from y={posAtKill.Y:0} to y={posAtHandover.Y:0} m (a glide, not a drop), at {velAtKill.Length():0} → {velAtHandover.Length():0} m/s");
            ctx.Check(handoverAt is >= 2.9f and <= 3.2f,
                $"Callback 15 released the hull at the def's authored 3.0 s gate t={handoverAt:0.00} s");
            ctx.Same(0, hullHiddenFrames,
                $"the airframe node was never hidden across the {handoverAt:0.00} s fall");
            ctx.Same(0, blankFrames,
                $"…and something of the aircraft was drawn on every one of those frames (healthy, then the destroyed wreck)");
            // The dead hull GLIDES rather than dropping, which the decode says is faithful: the
            // original changes nothing about a destroyed aircraft's integration.
            ctx.Check(travelled > 100f,
                $"the hull TRAVELLED under the flight model before the handover: {travelled:0} m (want over 100)");
            // The arm that tells freezing from neutralising: a default input bleeds speed (measured
            // 82 to 58 m/s), a frozen throttle holds it. ⚠ Speed RETAINED, never a downrange
            // distance: the recordings' 175 m may not contest a decode (docs/org/flightModel.md).
            float retained = velAtKill.Length() > 0.01f
                ? velAtHandover.Length() / velAtKill.Length() : 0f;
            ctx.Check(retained >= 0.95f,
                $"the wreck flew its FROZEN commands, not neutral ones: it kept {retained * 100f:0}% of its speed ({velAtKill.Length():0} → {velAtHandover.Length():0} m/s, want 95% or better)");
            ctx.Check(rig.InheritedWorldVelocity.IsEqualApprox(velAtHandover)
                      && velAtHandover != Vector3.Zero,
                $"Callback 16 handed the anim the velocity the wreck had reached inherited={rig.InheritedWorldVelocity} wreck={velAtHandover}");
            // The magnitude of the change, not its sign: what this proves is that the sample is the
            // handover's rather than the kill's, and whether the hull gained or lost speed getting
            // there is the frozen throttle's business, not this claim's.
            ctx.Check(Mathf.Abs(velAtKill.Length() - rig.InheritedWorldVelocity.Length()) > 5f,
                $"…sampled at the handover, not at the kill: it went from {velAtKill.Length():0.0} to {rig.InheritedWorldVelocity.Length():0.0} m/s over those seconds");
            ctx.Check(ai.LastCrashDef == null,
                $"nothing played the ground-impact family on the way down def={ai.LastCrashDef ?? "-"}");

            // Past the handover the hull is the anim's: the flight model must not still be moving it,
            // or the wreck and its ObjectMotion would fly the same node apart.
            var afterHandover = ai.WorldPosition;
            for (int i = 0; i < 60; i++)
                ai.SimStep(Dt);
            ctx.Check(ai.WorldPosition.IsEqualApprox(afterHandover),
                $"and the flight model stopped moving it once released moved={ai.WorldPosition.DistanceTo(afterHandover):0.00} m");

            // What carries it from here is IMPACT_FORCE, and randomdestseq's MAIN_ROOT_NODE is the
            // install's clearest carrier: it authors translation (0,0,0), so every metre downrange
            // below is inherited. ⚠ A wreck that drops vertically means the gate refused it.
            var wreckFrom = ai.CrashAnchor?.GlobalPosition ?? Vector3.Zero;
            for (int i = 0; i < 60; i++)
                rig.Advance(Dt);
            var wreckTo = ai.CrashAnchor?.GlobalPosition ?? Vector3.Zero;
            float downrange = new Vector3(wreckTo.X - wreckFrom.X, 0f, wreckTo.Z - wreckFrom.Z).Length();
            ctx.Note($"the wreck carried {downrange:0} m downrange in the second after the handover, off {velAtHandover.Length():0} m/s inherited");
            ctx.Check(downrange > 20f,
                $"the authored IMPACT_FORCE gate ADMITTED the hull's momentum: {downrange:0} m downrange in that second (want over 20; unflagged it would be 0)");
        }
        finally
        {
            ai?.Free();
        }
    }

    // The stages the hull was wearing when it died. Both AI stage anims are LOOP −1 with no authored
    // exit, so the only thing that can end them is the destroy def's own Callback 15; a stage left
    // live keeps emitting at whatever node the dead hull left behind.
    // ⚠ Balance STARTS against FINISHES, never a puffer count: an emitter with nothing left to emit
    // reads as quiet for a while and then resumes.
    private static void StagedEffectsEndAtTheDeath(TestContext ctx, FlightRoster spawner,
        float fallFrom, string planeName, float lane)
    {
        const float Dt = 1f / 60f;
        FlightController? ai = null;
        try
        {
            var spawn = new Vector3(lane, fallFrom, 0f);
            ai = spawner.SpawnAi(new AiSpawn(planeName, spawn, spawn + Vector3.Forward,
                AiPilot.HoldingCourse(spawn, spawn + Vector3.Forward)));
            if (ai.CrashRuntime is not { } rig || ai.Damage is not { } damage)
            {
                ctx.Check(false, $"the staged rig spawned with a crash runtime and a damage ledger");
                return;
            }

            rig.ManualAdvance = true;
            var live = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            rig.OnInstanceStarted += (def, _) =>
            {
                string n = def.AnimName ?? def.Name ?? "";
                live[n] = live.TryGetValue(n, out var c) ? c + 1 : 1;
            };
            rig.OnInstanceFinished += (def, _) =>
            {
                string n = def.AnimName ?? def.Name ?? "";
                live[n] = live.TryGetValue(n, out var c) ? c - 1 : -1;
            };

            // The ladder walked down through the production take-hit entry, as AiDamageStages does,
            // so the aircraft is wearing both stage anims when the kill lands.
            ai.TakeCollisionHit(damage.WholeArmor, 0f, ai.GlobalPosition, 0);
            foreach (float frac in new[] { 0.9f, 0.7f, 0.55f, 0.47f, 0.42f, 0.3f, 0.2f })
                SpendHullTo(ai, damage, frac);
            int stagedAtKill = Count(live, "pfsmoketrail") + Count(live, "random_remote_damage");
            ctx.Check(Count(live, "pfsmoketrail") > 0 && Count(live, "random_remote_damage") > 0,
                $"{planeName}: the wounded hull is wearing {stagedAtKill} live stage instance(s) at the kill ({Count(live, "pfsmoketrail")} pfsmoketrail, {Count(live, "random_remote_damage")} random_remote_damage)");

            float overkill = (damage.WholeHealthMax + damage.WholeArmorMax) * 4f;
            ai.TakeCollisionHit(overkill, overkill, ai.GlobalPosition, 0);
            for (float t = 0f; t < 5f; t += Dt)
            {
                ai.SimStep(Dt);
                rig.Advance(Dt);
            }
            ctx.Check(!ai.WreckFalling,
                $"…the fall ran past the def's 3.0 s gate, so Callback 15 has fired falling={ai.WreckFalling}");
            int stagedAfter = Count(live, "pfsmoketrail") + Count(live, "random_remote_damage");
            ctx.Same(0, stagedAfter,
                $"…and Callback 15 ended every stage the dead hull was wearing: {stagedAfter} still live ({Count(live, "pfsmoketrail")} pfsmoketrail, {Count(live, "random_remote_damage")} random_remote_damage)");
        }
        finally
        {
            ai?.Free();
        }
    }

    // THE ABLE-TO-FAIL CONTROL, and the bug in one line: with no destroy def bound the airframe is
    // hidden on the death frame and never travels a metre. Every check in the arm above passes on
    // this rig too if it is written loosely enough, which is why it runs.
    private static void WreckWithNoDestroyDefIsHiddenOnTheKill(TestContext ctx,
        FlightRoster spawner, float fallFrom)
    {
        const float Dt = 1f / 60f;
        FlightController? ai = null;
        try
        {
            var spawn = new Vector3(3000f, fallFrom, 0f);
            ai = spawner.SpawnAi(new AiSpawn("player_fury", spawn, spawn + Vector3.Forward,
                AiPilot.HoldingCourse(spawn, spawn + Vector3.Forward)));
            if (ai.Damage is not { } damage || ai.PlaneModel is not { } model)
            {
                ctx.Check(false, $"the control rig spawned with a damage ledger and a model");
                return;
            }

            // ⚠ Force the rig before taking its death slot away. A mid-flight introduction returns
            // with the rig still armed, and the build writes DestroyDef itself, so a null written
            // ahead of it would be put straight back.
            ai.EnsureCrashRig();
            ai.DestroyDef = null;
            var posAtKill = ai.WorldPosition;
            float overkill = (damage.WholeHealthMax + damage.WholeArmorMax) * 4f;
            ai.TakeCollisionHit(overkill, overkill, ai.GlobalPosition, 0);
            for (int i = 0; i < 180; i++)
                ai.SimStep(Dt);
            ctx.Check(!model.Visible && !ai.WreckFalling,
                $"ABLE-TO-FAIL CONTROL: with no destroy def the airframe is hidden on the death frame model={model.Visible} falling={ai.WreckFalling}");
            ctx.Check(ai.WorldPosition.DistanceTo(posAtKill) < 1f,
                $"…and the hull never falls: {ai.WorldPosition.DistanceTo(posAtKill):0.00} m in three seconds");
        }
        finally
        {
            ai?.Free();
        }
    }

    // A run of chapter land for a wreck to strike, 60 m under the spawn and aimed into it. ⚠ The
    // DIVE is what makes it reachable: a dead hull holds its altitude almost exactly, so one killed
    // in level flight glides out its three seconds and never meets the ground at all. Both ends of
    // the run must be land at one height; sea level is skipped, so the crash table indexes its own
    // ground slot rather than the water one.
    private static bool FindFlatRun(PhysicsDirectSpaceState3D space, out Vector3 spawn, out Vector3 aim)
    {
        spawn = Vector3.Zero;
        aim = Vector3.Forward;
        for (int xi = -3; xi <= 3; xi++)
        {
            for (int zi = -3; zi <= 3; zi++)
            {
                var a = new Vector3(xi * 1500f, 0f, zi * 1500f);
                var b = a + Vector3.Forward * 120f;
                if (SurfaceHeight(space, a) is not { } ha || SurfaceHeight(space, b) is not { } hb)
                    continue;
                if (ha < 1f || Mathf.Abs(ha - hb) > 10f)
                    continue;
                spawn = new Vector3(a.X, ha + 60f, a.Z);
                aim = spawn + new Vector3(0f, -1f, -0.3f);
                return true;
            }
        }

        return false;
    }

    private static float? SurfaceHeight(PhysicsDirectSpaceState3D space, Vector3 column)
    {
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
            new Vector3(column.X, 3000f, column.Z), new Vector3(column.X, -200f, column.Z),
            CollisionLayers.World));
        return hit.Count > 0 ? hit["position"].AsVector3().Y : null;
    }

    // The other outcome the same code path serves: a wreck that reaches the world INSIDE the three
    // seconds is still a vehicle, so its contact runs the surface-indexed ai_crash_* table, and that
    // def is what hides it. Spawned low over the flat run above, because a hull that holds its
    // altitude for three seconds started high reaches no ground at all.
    private static void WreckReachingTheGroundPlaysItsCrashDef(TestContext ctx,
        FlightRoster spawner, Vector3 spawn, Vector3 aim)
    {
        const float Dt = 1f / 60f;
        FlightController? ai = null;
        try
        {
            ai = spawner.SpawnAi(new AiSpawn("player_fury", spawn, aim, AiPilot.HoldingCourse(spawn, aim)));
            if (ai.CrashRuntime is not { } rig || ai.Damage is not { } damage
                || ai.PlaneModel is not { } model)
            {
                ctx.Check(false, $"the low rig spawned with a crash runtime, a damage ledger and a model");
                return;
            }

            rig.ManualAdvance = true;
            var live = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            rig.OnInstanceStarted += (def, _) =>
            {
                string n = def.AnimName ?? def.Name ?? "";
                live[n] = live.TryGetValue(n, out var c) ? c + 1 : 1;
            };
            rig.OnInstanceFinished += (def, _) =>
            {
                string n = def.AnimName ?? def.Name ?? "";
                live[n] = live.TryGetValue(n, out var c) ? c - 1 : -1;
            };

            // Wounded before it is killed, so the ground contact arrives with stages already
            // burning: the order the ladder and the death run in is what this arm's last check is
            // about.
            ai.TakeCollisionHit(damage.WholeArmor, 0f, ai.GlobalPosition, 0);
            foreach (float frac in new[] { 0.9f, 0.7f, 0.55f, 0.47f, 0.42f, 0.3f, 0.2f })
                SpendHullTo(ai, damage, frac);
            float overkill = (damage.WholeHealthMax + damage.WholeArmorMax) * 4f;
            ai.TakeCollisionHit(overkill, overkill, ai.GlobalPosition, 0);
            ctx.Check(model.Visible && ai.LastCrashDef == null,
                $"this wreck leaves the kill frame visible and unlanded too def={ai.LastCrashDef ?? "-"}");

            float landedAt = -1f;
            int visibleFramesBeforeLanding = 0;
            for (float t = 0f; t < 8f && landedAt < 0f; t += Dt)
            {
                ai.SimStep(Dt);
                rig.Advance(Dt);
                if (ai.LastCrashDef != null)
                    landedAt = t + Dt;
                else if (model.Visible)
                    visibleFramesBeforeLanding++;
            }

            ctx.Note($"the gliding wreck struck the ground at t={landedAt:0.00} s, {spawn.DistanceTo(ai.WorldPosition):0} m out, def={ai.LastCrashDef ?? "-"}");
            ctx.Check(landedAt is > 0f and < 3f,
                $"the wreck reached the world inside the 3.0 s window, so it was still a vehicle t={landedAt:0.00} s");
            ctx.Check(ai.LastCrashDef != null && ai.LastCrashDef.StartsWith(
                    Flight.Airframe.EffectCatalogue.AiCrashDefPrefix, System.StringComparison.Ordinal),
                $"…and its contact played the AI ground-impact family, indexed by the struck surface def={ai.LastCrashDef ?? "-"}");
            ctx.Check(visibleFramesBeforeLanding > 0 && !model.Visible,
                $"…which is what hides it, and only then: {visibleFramesBeforeLanding} visible frame(s) of flight, then model={model.Visible}");
            ctx.Check(!ai.WreckFalling,
                $"…and the wreck stopped flying itself on that contact falling={ai.WreckFalling}");

            // The landing does NOT cancel the destroy def, so its Callback 15 still arrives at the
            // authored 3.0 s and the stages this hull was wearing end there, the ground reaching
            // the wreck first must not cost it its stop (BL-422).
            for (float t = landedAt; t < 4f; t += Dt)
            {
                ai.SimStep(Dt);
                rig.Advance(Dt);
            }
            int stagedAfter = Count(live, "pfsmoketrail") + Count(live, "random_remote_damage");
            ctx.Same(0, stagedAfter,
                $"…and the stages it was wearing ended anyway: {stagedAfter} still live ({Count(live, "pfsmoketrail")} pfsmoketrail, {Count(live, "random_remote_damage")} random_remote_damage)");
        }
        finally
        {
            ai?.Free();
        }
    }

    // One airframe's whole death, through the production factory and the production take-hit path.
    private static void PlayerDestroyArm(TestContext ctx, TestWorld world, GameZ planesGamez,
        TextureArchive textures, string planeName, bool autogyro)
    {
        var factory = new Session.World.WorldEffectsFactory(
            SessionSpec.Parse(System.Array.Empty<string>()), ctx.Host, () => Vector3.Zero);
        FlightController? player = null;
        try
        {
            var spawn = new Vector3(0f, 500f, 0f);
            var stats = PlaneStats.Load(ctx.ZrdrPath, planeName);
            var builder = new PlaneBuilder(planesGamez, textures);
            var planeModel = builder.Build(planeName);
            player = new FlightController
            {
                PlaneModel = planeModel,
                Collider = PlaneCollider.Build(planeModel),
                PlayerIndex = 0,
                UseKeyboard = false,
                PadDevices = System.Array.Empty<int>(),
                AllowPause = false,
                // The ledger the production assembler gives a human rig, without it the take-hit
                // path returns early and nothing can reach Destroy.
                Damage = PlaneDamage.For(stats),
            };
            player.AddChild(planeModel);
            player.Setup(new FlightModel(stats), null, new CamParams(), spawn, spawn + Vector3.Forward);
            // Dying banked, not level: the parachute is staged under this controller, so a level
            // airframe cannot tell a chute that levels from one that copies the wreck.
            player.Rotation = new Vector3(0.35f, 1.2f, -0.8f);
            ctx.Host.AddChild(player);
            factory.BuildFlightCrashRuntime(player, builder, planeName, world.Gamez,
                world.Session.Builder.Scene, textures, world.Session.Program, verbose: false,
                planesGamez: planesGamez);
            if (player.CrashRuntime is not { } rig || player.CrashAnchor is not { } crashRoot)
            {
                ctx.Check(false, $"{planeName}: the human rig built a crash runtime");
                return;
            }

            rig.ManualAdvance = true;
            ctx.Check(player.DestroyDef == Flight.Airframe.EffectCatalogue.PlayerDestroyAnim
                      && !player.DestroyDefFliesWreck,
                $"{planeName}: the human rig's destroy def is '{player.DestroyDef ?? "-"}' and authors no hull ObjectMotion (fliesOwnHull={player.DestroyDefFliesWreck})");

            // The eject's own template: `cpilot` is a planes-gamez root no def NAMES, so the anchor
            // closure alone would leave cpeject1/cpeject2 playing on nothing.
            var cpilot = Find(crashRoot, "cpilot");
            var chuteman = Find(crashRoot, "chuteman");
            ctx.Check(cpilot != null && chuteman != null,
                $"{planeName}: the rig staged the bailing pilot and the parachute cpilot={(cpilot != null ? "yes" : "NO")} chuteman={(chuteman != null ? "yes" : "NO")}");
            var pieces = new[] { "piece1", "piece2", "piece3", "piece4" }
                .Select(n => (Name: n, Node: Find(crashRoot, n))).ToList();
            ctx.Check(pieces.All(p => p.Node != null),
                $"{planeName}: BuildDestroyed built all four wreck pieces [{string.Join(", ", pieces.Select(p => $"{p.Name}={(p.Node != null ? "yes" : "NO")}"))}]");
            if (pieces.Any(p => p.Node == null) || cpilot == null)
            {
                return;
            }

            var before = pieces.Select(p => p.Node!.GlobalPosition).ToList();
            var healthy = Find(planeModel, "healthy");
            var wreck = Find(crashRoot, "destroyed");

            // The kill, through the production ram entry, not a visuals API. Both magnitudes, or
            // standing armour nulls the health damage outright (PlaneDamage.Spend).
            float overkill = (player.Damage!.WholeHealthMax + player.Damage.WholeArmorMax) * 4f;
            player.TakeCollisionHit(overkill, overkill, player.GlobalPosition, 1);
            ctx.Check(player.Destroyed, $"{planeName}: the hull is spent and the destroy def is playing");

            // The eject first, and destroy_craft only once it COMPLETES: both arms call
            // cpeject1/cpeject2 with WAIT_FOR_COMPLETION, so the breakup is authored to wait for
            // the pilot to leave. The autogyro's own call sits behind a 0.15 s rotor event.
            float ejectAt = AdvanceUntil(rig, 30f, () => cpilot.Visible);
            ctx.Check(ejectAt >= 0f,
                $"{planeName}: the cockpit eject switched its cpilot on at t={ejectAt:0.00} s");
            float breakupAt = AdvanceUntil(rig, 30f, () => wreck is { Visible: true });
            ctx.Check(breakupAt >= 0f,
                $"{planeName}: destroy_craft swapped the airframe for the wreck at t={breakupAt:0.00} s, once the eject completed");
            ctx.Check(healthy is { Visible: false },
                $"{planeName}: …and switched the airframe's healthy subtree off healthy={(healthy?.Visible.ToString() ?? "-")}");

            // Which arm ran, read off the two firetrail templates the branches call: the autogyro
            // arm hangs `sputter_firetrail` on `destroyed`, `random_destroy` hangs
            // `dense_firetrail` on `healthy`.
            var sputter = Find(crashRoot, "sputter_firetrail");
            var dense = Find(crashRoot, "dense_firetrail");
            string arm = sputter is { Visible: true } ? "autogyro" : dense is { Visible: true } ? "random_destroy" : "neither";
            ctx.Check(arm == (autogyro ? "autogyro" : "random_destroy"),
                $"{planeName}: IF NODE_ACTIVE 1 chose the {arm} arm (want {(autogyro ? "autogyro" : "random_destroy")})");
            ctx.Check(pieces.All(p => p.Node!.Visible),
                $"{planeName}: all four pieces are showing [{string.Join(", ", pieces.Select(p => $"{p.Name}={p.Node!.Visible}"))}]");

            // Two seconds of the pieces' own motion.
            for (int i = 0; i < 120; i++)
            {
                rig.Advance(1f / 60f);
            }

            var moved = pieces.Select((p, i) => (p.Name, Dist: p.Node!.GlobalPosition.DistanceTo(before[i]))).ToList();
            ctx.Check(moved.All(m => m.Dist > 1f),
                $"{planeName}: every piece flew its own ObjectMotion [{string.Join(", ", moved.Select(m => $"{m.Name}={m.Dist:0.0} m"))}]");
            ctx.Check(rig.BallisticMotionsLaunched >= 4,
                $"{planeName}: the rig launched {rig.BallisticMotionsLaunched} ballistic motion(s), one per piece at least");

            // Callback 3 is the def's own view code: the kill binds the seam reaching
            // CameraController.ResetToChase, so it is ACTED ON here rather than counted
            // (docs/formats/anim-definitions/cutscenes.md).
            ctx.Check(rig.ResetPilotView != null,
                $"{planeName}: the kill bound the view seam the authored Callback 3 reaches");
            ctx.Check(!rig.UnhandledEventCounts.ContainsKey("Callback(3)")
                      && !rig.UnhandledEventCounts.ContainsKey("Callback(3, no seam wired)"),
                $"{planeName}: …and the code was answered rather than counted keys=[{string.Join(",", rig.UnhandledEventCounts.Keys)}]");
            ctx.Check(chuteman is { Visible: true },
                $"{planeName}: the parachutist is out too, untimed here where the ten AI defs gate him at 3.0 s");
            // He hangs level in the WORLD while the airframe he left is banked: his template is
            // authored at identity, and only our staging could hand the placing call a wreck basis.
            var chuteEuler = chuteman != null
                ? chuteman.GlobalBasis.GetEuler() * (180f / Mathf.Pi) : Vector3.Zero;
            ctx.Check(chuteman != null && chuteEuler.Length() < 1f,
                $"{planeName}: the canopy hangs level, world rot ({chuteEuler.X:0.0}, {chuteEuler.Y:0.0}, {chuteEuler.Z:0.0}), under a wreck at ({player.GlobalRotationDegrees.X:0.0}, {player.GlobalRotationDegrees.Y:0.0}, {player.GlobalRotationDegrees.Z:0.0})");
            // ⚠ The eject hides the SEATED pilot, never the parachutist's body, both nodes are
            // named `pilot`, and the staged chute copy hangs under the crash root `cpeject1`
            // anchors on.
            var seatedPilot = Find(planeModel, "pilot");
            var chutePilot = chuteman != null ? Find(chuteman, "pilot") : null;
            ctx.Check(seatedPilot is { Visible: false } && chutePilot is { Visible: true },
                $"{planeName}: the seat is empty and the man under the canopy has a body seated={(seatedPilot?.Visible.ToString() ?? "-")} chute={(chutePilot?.Visible.ToString() ?? "-")}");
            ctx.Note($"{planeName}: eject at t={ejectAt:0.00} s, breakup at t={breakupAt:0.00} s");
            ctx.Note($"{planeName}: unhandled event kinds [{string.Join(", ", rig.UnhandledEventCounts.Select(kv => $"{kv.Key}×{kv.Value}"))}]");
        }
        finally
        {
            player?.Free();
        }
    }

    // Steps the rig at 60 Hz until the beat lands, and returns when, or -1 after the budget. A
    // fixed frame count cannot express "once the eject completes"; the wait is authored, not timed.
    private static float AdvanceUntil(AnimRuntime rig, float budgetSeconds, System.Func<bool> beat)
    {
        const float Dt = 1f / 60f;
        for (float t = 0f; t <= budgetSeconds; t += Dt)
        {
            if (beat())
            {
                return t;
            }

            rig.Advance(Dt);
        }

        return -1f;
    }

    // The strongest translucency an OBJECT_OPACITY event has left anywhere in this subtree, read
    // off the per-instance shader parameter the fade writes. It is -1 where nothing has been
    // faded, which a caller reads as solid rather than as transparent.
    private static float Alpha(Node node)
    {
        float best = -1f;
        if (node is GeometryInstance3D g)
        {
            var value = g.GetInstanceShaderParameter(SceneBuilder.OpacityParam);
            if (value.VariantType != Variant.Type.Nil)
            {
                best = value.AsSingle();
            }
        }

        foreach (var child in node.GetChildren())
        {
            best = Mathf.Max(best, Alpha(child));
        }

        return best;
    }

    // The first descendant carrying this authored NAME (or Godot name), the way a def's own
    // resolution finds it, a suite must not assume the Godot node name survived staging.
    private static Node3D? Find(Node root, string name)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is Node3D n3d)
            {
                string authored = n3d.HasMeta(AnimRuntime.NameMeta)
                    ? (string)n3d.GetMeta(AnimRuntime.NameMeta)
                    : n3d.Name.ToString();
                if (authored.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                {
                    return n3d;
                }
            }

            if (child != null && Find(child, name) is { } hit)
            {
                return hit;
            }
        }

        return null;
    }

    // The rig's `poolN` containers, the one part of the stage the per-slot step split builds one at
    // a time.
    private static int CountPools(Node3D? crashRoot)
    {
        if (crashRoot == null)
        {
            return 0;
        }

        int pools = 0;
        foreach (var child in crashRoot.GetChildren())
        {
            if (child is Node3D node && node.Name.ToString().StartsWith("pool", System.StringComparison.Ordinal))
            {
                pools++;
            }
        }

        return pools;
    }

    // The third authored shape of a wreck removing itself: the clear-away step sits in an ON_CALL
    // sequence the death itself calls, which is C5's agyrobus (destroy_craft calls randomdestseq,
    // and that switches `destroyed` off as the wreck is launched). Constructed rather than flown,
    // because the shipped case opens on IF RandomWeight and only one draw reaches the switch.
    // ⚠ Assert the repair outside the chain in the same pass. A guard that swallowed every
    // backward swap would pass the first check and silently retire the revival branch.
    private static void WreckClearedFromACalledSequence(TestContext ctx)
    {
        const float Tick = 1f / 30f;
        const string Anim = "wreck_clears_itself";
        var stage = new Node3D { Name = "RevivalStage" };
        var anchor = new Node3D { Name = Anim };
        anchor.AddChild(new Node3D { Name = "healthy" });
        anchor.AddChild(new Node3D { Name = "destroyed" });
        stage.AddChild(anchor);
        var runtime = new AnimRuntime
        {
            AutoStart = false,
            ManualAdvance = true,
            SoundHandledElsewhere = true,
        };
        ctx.Host.AddChild(stage);
        ctx.Host.AddChild(runtime);
        try
        {
            runtime.Bind(stage, AnimProgram.FromDefinitions(new[] { WreckClearingDef(Anim) }));
            var pool = runtime.Destructibles.All.FirstOrDefault(i => i.Def.AnimName == Anim);
            ctx.Check(pool != null,
                $"the constructed def registers its pool pools={runtime.Destructibles.All.Count}");
            if (pool is not { } wreck)
            {
                return;
            }

            runtime.DamageAt(wreck.Anchor, wreck.MaxHealth + 1f);
            for (int i = 0; i < 4; i++)
            {
                runtime.Advance(Tick);
            }

            ctx.Check(wreck.Status == DestructibleRegistry.State.Destroyed && wreck.Health <= 0f,
                $"the wreck its called sequence clears away leaves the pool dead status={wreck.Status} hp={wreck.Health:0.##}");

            // The other direction: a RESET_STATE is outside the death chain, so the same backward
            // swap read from there is the repair it looks like.
            runtime.RunResetStateEvents(Anim);
            ctx.Check(wreck.Status == DestructibleRegistry.State.Healthy && wreck.Health >= wreck.MaxHealth,
                $"a baseline outside that chain still revives the pool status={wreck.Status} hp={wreck.Health:0.##}");
        }
        finally
        {
            runtime.Free();
            stage.Free();
        }
    }

    // A destructible whose death hides `healthy`, shows `destroyed` and then calls the ON_CALL
    // sequence that clears the wreck away, with a RESET_STATE naming the standing pose.
    private static AnimDefinition WreckClearingDef(string animName)
    {
        var def = new AnimDefinition { Name = animName, AnimName = animName, Health = 10f };
        var death = new AnimSequence();
        death.Events.Add(SwitchEvent("healthy", false));
        death.Events.Add(SwitchEvent("destroyed", true));
        death.Events.Add(new AnimEvent
        {
            Kind = "CallSequence",
            Data = new AnimData(new Dictionary<string, object?> { ["name"] = "clear_away" }),
        });
        var clear = new AnimSequence { Name = "clear_away", OnCallOnly = true };
        clear.Events.Add(SwitchEvent("destroyed", false));
        def.Sequences.Add(death);
        def.Sequences.Add(clear);
        var reset = new AnimSequence();
        reset.Events.Add(SwitchEvent("healthy", true));
        reset.Events.Add(SwitchEvent("destroyed", false));
        def.ResetState = reset;
        return def;
    }

    private static AnimEvent SwitchEvent(string node, bool state) => new()
    {
        Kind = "ObjectActiveState",
        Data = new AnimData(new Dictionary<string, object?>
        {
            ["node"] = node,
            ["state"] = state,
        }),
    };
}
