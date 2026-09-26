using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Mech3;
using CSVM.Mech3.Anim;
using CSVM.Session.Roster;
using CSVM.Tooling;
using CSVM.UI.Boards;
using Godot;

using static CSVM.Testing.SuiteConstants;
namespace CSVM.Testing;

/// <summary>Suites asserting what a played animation puts on screen: the launches it flies
/// and lands, the effect templates it stages and lights, the washes it paints, and the
/// authored burst timelines it dispatches.</summary>
internal static class AnimationAndEffectsSuites
{
    // ---- the full effects sweep as suite verdicts ----------------------------------------------

    // Asserts every effects-test entry on a full replica stage so sweep verdicts fail the build.
    // The fixed ~180 m play point detects a template that failed to relocate.
    // ⚠ Puffer and mesh tallies are golden only for seed 1 with this counting factory.
    [Suite("effects-census",
        "the full --effects-test sweep as verdicts: every effect resolves, template meshes show at the CALL SITE (not the stage origin), and none stays lit after its stop")]
    internal static void EffectsCensus(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var names = Flight.Airframe.EffectCatalogue.WorldEffectAnimNames(world.Session.Program);
            // The staged set is DERIVED, so this census stages what the
            // real world-effects build stages, from the same call, a root the closure gains and
            // this chapter's gamez cannot supply throws here, naming the def and the anchor.
            var roots = Session.World.WorldEffectsFactory.EffectStageRootNames(world.Session.Program, world.Gamez);
            var stage = new Node3D { Name = "EffectCensusStage" };
            var pool = new Node3D { Name = "pool0" };
            pool.SetMeta(AnimRuntime.PoolSlotMeta, 0);
            stage.AddChild(pool);
            int built = Session.World.WorldEffectsFactory.BuildEffectStage(world.Gamez,
                world.Session.Builder.Scene, pool, roots);
            ctx.Check(built == roots.Count, $"staged {built}/{roots.Count} template root(s)");
            foreach (var child in pool.GetChildren())
                if (child is Node3D root)
                {
                    root.Visible = false;
                }

            var point = new Vector3(150, 40, 90);
            var runtime = AnimRuntime.ForEffects(
                AnimRuntime.NewTemplateStage(pooled: true, shown: true, placesCalled: true),
                1, new CountingEmitterFactory(), false, 32f,
                () => point);
            runtime.ManualAdvance = true;
            ctx.Host.AddChild(stage);
            ctx.Host.AddChild(runtime);
            try
            {
                runtime.Bind(stage, world.Session.Program.Subset(names));
                var r = Probes.Effects(runtime, names, point, stage, ctx.Chapter);

                ctx.Check(r.Ok, $"all effects resolve ({r.Resolved}/{names.Count} resolved)");
                var far = r.Rows.SelectMany(row => row.MeshPeaks
                        .Where(pk => pk.Visible > 0 && pk.Distance > 100f)
                        .Select(pk => $"{row.Name}: {pk.Root} @{pk.Distance:0} m"))
                    .ToList();
                ctx.Check(far.Count == 0,
                    $"every lit template mesh peaked at the CALL SITE, not the stage origin{(far.Count == 0 ? "" : $", {string.Join("; ", far)}")}");
                var lit = r.Rows.Where(row => row.Residual.Count > 0)
                    .Select(row => $"{string.Join("/", row.Residual.Select(x => x.Root))} after {row.Name}")
                    .ToList();
                ctx.Check(lit.Count == 0,
                    $"no template mesh left lit after its effect was stopped{(lit.Count == 0 ? "" : $", {string.Join("; ", lit)}")}");
                ctx.Check(r.Puffered == 30,
                    $"the puffer half's tally holds under suite conditions ({r.Puffered} built one, expected 30)");
                // The 19 includes `biggun_flying_parts` (its eight parts fly their solved parabola
                // before their own deactivation, so samples catch them drawing) and the seeker's
                // `ballflare.flt`, whose one sequence lights the flare disc it is anchored on.
                ctx.Check(r.Meshed == 19,
                    $"the mesh half's tally holds under suite conditions ({r.Meshed} showed meshes, expected 19)");
            }
            finally
            {
                runtime.Free();
                stage.Free();
            }

            // The derivation IS the staged set; there is no hand table to compare against. What still needs
            // saying per chapter is that the pool config sizes the set really staged, since a root renamed on
            // one side sizes nothing, silently.
            var unsized = Utils.EffectPools.Load().UnknownRoots(roots);
            ctx.Check(unsized.Count == 0,
                $"effect_pools.json sizes only roots this bind stages, {ctx.Chapter}{(unsized.Count == 0 ? "" : $", sizes nothing: {string.Join(", ", unsized)}")}");

            CrashStageRootTripwire(ctx, world);
        });
    }

    // The crash half, on a replica of the crash rig's own bind scope: the player crash root, the plane
    // model and its destroyed wreck, minus the runtime the anchor question does not need. Per-plane on
    // purpose, since the wreck and part subtrees vary by airframe and the Devastator is the one whose
    // own model root a crash def names. Asserts the rig's derived roots all BUILD from this chapter's
    // gamez; a root the closure asks for that the chapter cannot supply is the silent-miss failure.
    internal static void CrashStageRootTripwire(TestContext ctx, TestWorld world)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        try
        {
            foreach (var model in new[] { "player_bhawk", "player_pfighter" })
            {
                var rigScope = new Node3D { Name = "player" };
                rigScope.SetMeta(AnimRuntime.NameMeta, "player");
                try
                {
                    var builder = new PlaneBuilder(planesGamez, textures);
                    rigScope.AddChild(builder.Build(model));
                    if (builder.BuildDestroyed(model) is { } wreck)
                    {
                        rigScope.AddChild(wreck);
                    }
                    ctx.Host.AddChild(rigScope);
                    var resolve = Session.World.WorldEffectsFactory.StageRootResolver(world.Gamez, rigScope);
                    var rigRoots = Flight.Airframe.EffectCatalogue.CrashStageRoots(world.Session.Program, resolve);
                    var built = new Node3D { Name = "crash_template_replica" };
                    ctx.Host.AddChild(built);
                    int n = Session.World.WorldEffectsFactory.BuildEffectStage(world.Gamez,
                        world.Session.Builder.Scene, built, rigRoots);
                    built.Free();
                    ctx.Check(n == rigRoots.Count,
                        $"{model}: the crash rig stages every root its bound defs anchor on ({n}/{rigRoots.Count}), {world.Chapter}");
                }
                finally
                {
                    rigScope.Free();
                }
            }
        }
        finally
        {
            textures.Dispose();
        }
    }

    // ---- contact: the flight ends where the world says ------------------------------------------

    // A gravity-bearing OBJECT_MOTION must be cut short by real geometry instead of running its
    // authored RUN_TIME out below the terrain, through whichever of the two tiers its flags select, and
    // must pick its BOUNCE_SEQUENCE branch from the surface it struck. Driven as a synthetic body
    // thrown downward from a known height, so flight time, resting height and branch are predictable.
    // ⚠ Keep the last case, the same bodies with no mask handed over, running their full clock far
    // below the surface: without it a suite that fired no query at all would pass its landing checks.
    [Suite("ground-contact",
        "a gravity-bearing OBJECT_MOTION is cut short by real geometry through the right tier (default column, do_intersections sweep, no_altitude neither), rests on the surface and picks its BOUNCE_SEQUENCE branch from what it struck, and does none of it without a mask")]
    internal static void GroundContact(TestContext ctx)
    {
        const float Tick = 1f / 60f;
        const float Authored = 20f;      // the run time these bodies carry; contact must beat it
        const float DropHeight = 60f;    // above whatever the probe finds, well clear of the arming epsilon
        const string Land = "testhit_ground";
        const string Wet = "testhit_water";

        ctx.WithWorld(ctx.Chapter, collision: true, world =>
        {
            var runtime = world.Runtime;
            var root = world.Session.Root;

            // A suite that builds no colliders would pass every contact check by taking the
            // fallback and proving nothing, so the collision world is asserted before anything
            // else is asked of it.
            var space = root.GetWorld3D()?.DirectSpaceState;
            ctx.Check(space != null, $"the world built a collision space to sweep against chapter={ctx.Chapter}");
            if (space == null)
            {
                return;
            }

            // Somewhere with ground under it: probe straight down from high up over the origin
            // column and take what the world actually offers, rather than assuming a height.
            var from = new Vector3(0f, 400f, 0f);
            var probe = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                from, new Vector3(0f, -400f, 0f), CollisionLayers.World));
            ctx.Check(probe.Count > 0, $"a downward probe finds chapter geometry chapter={ctx.Chapter}");
            if (probe.Count == 0)
            {
                return;
            }

            float surfaceY = probe["position"].AsVector3().Y;

            // One authored OBJECT_MOTION, built by hand: a 5 m/s downward throw under Earth
            // gravity from DropHeight above that surface, `do_intersections` on, a 20 s run time
            // and both bounce branches named so the choice is observable.
            static Dictionary<string, object?> Vec(float x, float y, float z) =>
                new() { ["x"] = x, ["y"] = y, ["z"] = z };

            // flagged: false is the DEFAULT authoring and selects the ground column; noAltitude is the opt-out
            // that selects neither; timed: false omits run_time the way the ballistic events do, and is thrown
            // upward so the parabola has an apex to report to the sequence.
            AnimData Body(bool flagged = true, bool noAltitude = false, bool complex = true,
                bool timed = true, bool impactForce = false)
            {
                var props = new Dictionary<string, object?>
                {
                    ["impact_force"] = impactForce,
                    ["gravity"] = new Dictionary<string, object?>
                    {
                        ["value"] = -9.8f,
                        ["complex"] = complex,
                        ["no_altitude"] = noAltitude,
                        ["do_intersections"] = flagged,
                    },
                    ["translation"] = new Dictionary<string, object?>
                    {
                        ["initial"] = Vec(0f, timed ? -5f : 10f, 0f),
                        ["delta"] = Vec(0f, 0f, 0f),
                        ["rnd_xz"] = Vec(0f, 0f, 0f),
                    },
                    ["bounce_sequence"] = new Dictionary<string, object?>
                    {
                        ["default"] = Land,
                        ["water"] = Wet,
                        ["lava"] = null,
                    },
                };
                if (timed)
                {
                    props["run_time"] = Authored;
                }

                return new AnimData(props);
            }

            // Runs one body to a stop and reports what happened to it. waterHook stands in for the session's
            // ProjectilePool.SurfaceIsWater binding: the surface-id read has its own coverage, and stubbing it
            // is what makes the branch choice assertable without needing a chapter with reachable sea.
            (float Flight, float EndY, string? Bounce, bool ByContact, MotionContactTier Tier,
                int ColumnLandings, int SweepLandings, float Reported) Run(
                uint mask, System.Func<GodotObject?, bool>? waterHook, AnimData? body = null,
                float limit = 0f, float dropFrom = DropHeight)
            {
                var node = new Node3D { Name = "ground-contact-probe" };
                root.AddChild(node);
                node.GlobalPosition = new Vector3(0f, surfaceY + dropFrom, 0f);

                uint maskWas = runtime.ContactMask;
                var hookWas = runtime.SurfaceIsWater;
                runtime.ContactMask = mask;
                runtime.SurfaceIsWater = waterHook;
                try
                {
                    var data = body ?? Body();
                    // What AnimRuntime passes: the authored value, or 0 when the event omits one.
                    var motion = MotionRuntime.Create(runtime, node, data, data.Num("run_time") ?? 0f);
                    if (motion == null)
                    {
                        return (0f, node.GlobalPosition.Y, null, false, MotionContactTier.None, 0, 0, 0f);
                    }

                    var set = new MotionSet();
                    set.Add(motion, world.Runtime.Destructibles.All.First().Def, null);
                    float flown = 0f;
                    string? bounce = null;
                    float cap = limit > 0f ? limit : Authored;
                    for (int i = 0; i < (int)(cap / Tick) + 2 && !motion.Finished; i++)
                    {
                        foreach (var landing in set.Tick(Tick))
                        {
                            bounce = landing.Bounce;
                        }

                        flown += Tick;
                    }

                    return (flown, node.GlobalPosition.Y, bounce, motion.LandedByContact,
                        motion.ContactTier, set.ColumnLandings, set.SweepLandings, motion.RunTime);
                }
                finally
                {
                    runtime.ContactMask = maskWas;
                    runtime.SurfaceIsWater = hookWas;
                    node.QueueFree();
                }
            }

            // 1, the SWEEP tier cuts the flight short and rests the body ON the surface.
            var hit = Run(CollisionLayers.World, _ => false);
            ctx.Check(hit.Tier == MotionContactTier.Sweep,
                $"do_intersections selects the sweep tier={hit.Tier}");
            ctx.Check(hit.ByContact, $"the sweep ended the body on a collider flight={hit.Flight:0.00}s");
            ctx.Check(hit.Flight < Authored,
                $"contact beat the authored run time flight={hit.Flight:0.00}s authored={Authored:0}s");
            // A band, not a point: the hit lands between two frames and the body is a point, so
            // "on the surface" is within a tick's fall of it, never below it.
            ctx.Check(hit.EndY >= surfaceY - 1f && hit.EndY <= surfaceY + 2f,
                $"the body rests at the struck surface endY={hit.EndY:0.00} surfaceY={surfaceY:0.00}");
            ctx.Check(hit.Bounce == Land, $"contact dispatched the default branch bounce={hit.Bounce ?? "(none)"}");
            ctx.Check(hit.SweepLandings == 1 && hit.ColumnLandings == 0,
                $"and it is tallied as a sweep landing sweep={hit.SweepLandings} column={hit.ColumnLandings}");

            // 2, the same contact over water takes the water branch.
            var wet = Run(CollisionLayers.World, _ => true);
            ctx.Check(wet.Bounce == Wet, $"a water surface picks the water branch bounce={wet.Bounce ?? "(none)"}");

            // 1b, the DEFAULT tier. The identical body with `do_intersections` off, which is how
            // 1,466 of the install's gravity-bearing events are authored, must land too and rest in
            // the same place. Before C6 this body sank through the world and ran its 20 s clock out.
            var column = Run(CollisionLayers.World, _ => false, Body(flagged: false));
            ctx.Check(column.Tier == MotionContactTier.Column,
                $"an unflagged gravity body selects the default column tier={column.Tier}");
            ctx.Check(column.ByContact && column.Flight < Authored,
                $"the column ended the body on a surface flight={column.Flight:0.00}s authored={Authored:0}s");
            ctx.Check(column.EndY >= surfaceY - 1f && column.EndY <= surfaceY + 2f,
                $"and rests it at the struck surface endY={column.EndY:0.00} surfaceY={surfaceY:0.00}");
            ctx.Check(column.Bounce == Land,
                $"the column landing dispatches its branch too bounce={column.Bounce ?? "(none)"}");
            ctx.Check(column.ColumnLandings == 1 && column.SweepLandings == 0,
                $"and is tallied apart from the sweep column={column.ColumnLandings} sweep={column.SweepLandings}");

            // 1b2, a body that is ALREADY under the surface. The original's column is a query at
            // (x, z) that answers whatever the body's height, and `y + stepY < height` then lifts
            // it back on; a query that only looks down answers nothing and the body keeps falling.
            const float Sunk = -25f;
            var below = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                new Vector3(0f, surfaceY + Sunk, 0f), new Vector3(0f, surfaceY + Sunk - 4096f, 0f),
                CollisionLayers.World));
            ctx.Same(0, below.Count,
                $"nothing sits under the sunk start, so only the surface above it can answer hits={below.Count}");
            var sunk = Run(CollisionLayers.World, _ => false, Body(flagged: false, complex: false),
                dropFrom: Sunk);
            ctx.Check(sunk.ByContact && sunk.ColumnLandings == 1,
                $"the column lifts a body under the surface back onto it byContact={sunk.ByContact} column={sunk.ColumnLandings}");
            ctx.Check(sunk.EndY >= surfaceY - 1f && sunk.EndY <= surfaceY + 2f,
                $"and rests it there rather than {-Sunk:0} m down endY={sunk.EndY:0.00} surfaceY={surfaceY:0.00}");

            // 1c, NO_ALTITUDE is the opt-out, and it vetoes the COLUMN only. `gunshell` is its one
            // author install-wide, and it must keep falling through the world exactly as before.
            var optedOut = Run(CollisionLayers.World, _ => false, Body(flagged: false, noAltitude: true));
            ctx.Check(optedOut.Tier == MotionContactTier.None,
                $"no_altitude vetoes the column tier={optedOut.Tier}");
            ctx.Check(!optedOut.ByContact && optedOut.EndY < surfaceY - 100f,
                $"so the body falls straight through endY={optedOut.EndY:0.00} surfaceY={surfaceY:0.00}");

            // 1d, and it does not suppress an explicitly authored sweep. The original's branch
            // order reaches the veto only on an unflagged body, so a reading that treats the two
            // flags as independent conditions fails right here.
            var bothFlags = Run(CollisionLayers.World, _ => false, Body(flagged: true, noAltitude: true));
            ctx.Check(bothFlags.Tier == MotionContactTier.Sweep && bothFlags.ByContact,
                $"do_intersections outranks no_altitude tier={bothFlags.Tier} byContact={bothFlags.ByContact}");

            // An UNTIMED launch, the shape C8 re-terminates. ⚠ It must keep REPORTING its parabola rather than
            // its watchdog: the reported number is what the sequence waits on, so a body reporting 15 s would
            // leave every vanish-shape piece on screen that long and divide its tumble by the same figure.
            var untimed = Run(CollisionLayers.World, _ => false, Body(flagged: false, timed: false));
            ctx.Check(untimed.ByContact && untimed.Flight < 10f,
                $"an untimed launch still ends on the ground flight={untimed.Flight:0.00}s byContact={untimed.ByContact}");
            ctx.Check(untimed.Reported > 0f && untimed.Reported < 5f,
                $"and reports its own parabola to the sequence, not the 15 s watchdog reported={untimed.Reported:0.00}s");
            ctx.Check(untimed.Bounce == Land,
                $"its branch comes from the surface it struck bounce={untimed.Bounce ?? "(none)"}");

            // 1g, THE WATCHDOG ITSELF, which nothing else here can fire: the same untimed body
            // with a mask no collider answers. The tier is selected (the mask is non-zero) but
            // every query comes back empty, which is the only thing that charges the accumulator.
            // Without it this body would fly forever, since an untimed launch has no clock.
            const uint EmptyLayer = 1u << 20;   // no collider in this project is built on it
            var watchdog = Run(EmptyLayer, _ => false, Body(flagged: false, timed: false), limit: 20f);
            ctx.Check(watchdog.Tier == MotionContactTier.Column && !watchdog.ByContact,
                $"a query that answers nothing still selects the tier tier={watchdog.Tier} byContact={watchdog.ByContact}");
            ctx.Check(watchdog.Flight > 14f && watchdog.Flight < 16f,
                $"and the 15 s column watchdog ends the body flight={watchdog.Flight:0.00}s");
            ctx.Check(watchdog.Bounce == Land,
                $"a watchdog end owes the default branch, from its null surface bounce={watchdog.Bounce ?? "(none)"}");

            // The veto on REAL extracted data. Every case above builds its gravity block by hand, which pins
            // the branch but not that no_altitude survives extraction and reaches Create at all. gunshell is
            // its only author install-wide, and it is reachable because muzzleburst_effects CallAnimations it.
            {
                var shellDefs = world.Session.Program.ByAnimName("gunshell");
                AnimData? shell = null;
                foreach (var def in shellDefs)
                {
                    foreach (var seq in def.Sequences)
                    {
                        foreach (var ev in seq.Events)
                        {
                            if (ev.Kind == "ObjectMotion" && ev.Data.Obj("translation_range") != null)
                            {
                                shell ??= ev.Data;
                            }
                        }
                    }
                }

                ctx.Check(shell != null,
                    $"the chapter program carries gunshell's launch defs={shellDefs.Count} chapter={ctx.Chapter}");
                if (shell != null)
                {
                    ctx.Check(shell.Obj("gravity")?.Bool("no_altitude") == true,
                        $"and the extracted event still authors no_altitude value={shell.Obj("gravity")?.Bool("no_altitude")}");
                    var node = new Node3D { Name = "ground-contact-gunshell" };
                    root.AddChild(node);
                    node.GlobalPosition = new Vector3(0f, surfaceY + DropHeight, 0f);
                    uint maskWas = runtime.ContactMask;
                    runtime.ContactMask = CollisionLayers.World;
                    try
                    {
                        var casing = MotionRuntime.Create(runtime, node, shell, shell.Num("run_time") ?? 2f);
                        ctx.Check(casing is { ContactTier: MotionContactTier.None },
                            $"so the one def that opts out selects no tier even with a mask wired tier={casing?.ContactTier}");
                    }
                    finally
                    {
                        runtime.ContactMask = maskWas;
                        node.QueueFree();
                    }
                }
            }

            // The bounce is a CONTINUATION: the sequence a landing dispatches re-launches the very node that
            // landed, and MotionRuntime.Create ordinarily re-homes a ballistic launch to the node's authored
            // rest pose, which shows as the crash jumping back to the crash point once per piece.
            {
                var node = new Node3D { Name = "ground-contact-resume" };
                root.AddChild(node);
                var restPose = new Vector3(0f, surfaceY + DropHeight, 0f);
                node.GlobalPosition = restPose;
                runtime.RestOf(node);   // record that pose as the authored rest, as a built node has

                uint maskWas = runtime.ContactMask;
                runtime.ContactMask = CollisionLayers.World;
                try
                {
                    var first = MotionRuntime.Create(runtime, node, Body(), Authored);
                    var set = new MotionSet();
                    set.Add(first!, world.Runtime.Destructibles.All.First().Def, null);
                    bool landed = false;
                    for (int i = 0; i < (int)(Authored / Tick) + 2 && !first!.Finished; i++)
                    {
                        foreach (var landing in set.Tick(Tick))
                        {
                            // What TickMotions does before dispatching the sequence.
                            landed = true;
                            if (landing.ByContact)
                            {
                                runtime.MarkLandingResume(landing.Target);
                            }
                        }
                    }

                    ctx.Check(landed, $"the first flight landed by contact before the follow-up y={node.GlobalPosition.Y:0.00}");
                    float restedY = node.GlobalPosition.Y;
                    // The follow-up is built the way pNhit authors one: the SAME body with the flag off. ⚠ It must
                    // resume from the landing and still be contact-tested, or it runs its whole clock and buries the
                    // piece under the airfield, which is the "plane went through the ground" symptom.
                    var settle = Body(flagged: false, impactForce: true); // ⚠ ON, or the authored gate refuses before the landing rule is asked
                    // A dive hands the crash rig a large downward momentum. The FIRST launch spends
                    // it; a hop off the ground must not be handed it again, or it covers the 2 m
                    // arming epsilon in 0.044 s and is under the terrain before the sweep can look.
                    var inheritWas = runtime.InheritedWorldVelocity;
                    runtime.ArmInheritedVelocity(new Vector3(0f, -45f, 0f));
                    var second = MotionRuntime.Create(runtime, node, settle, Authored);
                    runtime.ArmInheritedVelocity(inheritWas);
                    second?.Seek(0f);
                    float relaunchY = node.GlobalPosition.Y;
                    ctx.Check(Mathf.Abs(relaunchY - restedY) < 1f,
                        $"the follow-up launch starts from the landing, not the authored rest relaunchY={relaunchY:0.00} restedY={restedY:0.00} rest={restPose.Y:0.00}");
                    ctx.Check(second is { ContactTier: MotionContactTier.Column },
                        $"the settle hop is contact-tested through the default column tier={second?.ContactTier}");
                    second?.Seek(0.2f);
                    float hopY = node.GlobalPosition.Y;
                    // The band, not a point: this synthetic body is authored throwing DOWNWARD at
                    // 5 m/s, so 0.2 s of it is −1.20 m on its own. Inheriting the −45 m/s dive on
                    // top would put it another 9 m under.
                    ctx.Check(hopY > relaunchY - 3f,
                        $"and it inherits none of the dive's momentum hopY={hopY:0.00} relaunchY={relaunchY:0.00} (inherited it would be ≈{relaunchY - 10.2f:0.00})");

                    // A plain launch on a node that did NOT just land takes the same tier: the resume mark buys
                    // momentum and re-homing rules, never a different contact mechanism. A body that reported Sweep
                    // here would mean the retired inheritance had grown back somewhere.
                    var elsewhere = new Node3D { Name = "ground-contact-unflagged" };
                    root.AddChild(elsewhere);
                    elsewhere.GlobalPosition = restPose;
                    var plain = MotionRuntime.Create(runtime, elsewhere, settle, Authored);
                    ctx.Check(plain is { ContactTier: MotionContactTier.Column },
                        $"a false-flagged launch that continues nothing takes the column too tier={plain?.ContactTier}");

                    // The IMPACT_FORCE gate itself: one armed runtime, one body, the authored flag
                    // the only difference. ⚠ High above the surface, not at the rest pose, or the
                    // column tier stops both launches on the same polygon and they read alike.
                    float LaunchY(bool impactForce)
                    {
                        // Re-placed per launch: a launch integrates from the LIVE pose, so without
                        // this the second body would start where the first one's 0.2 s left it.
                        elsewhere.GlobalPosition = new Vector3(0f, surfaceY + DropHeight, 0f);
                        MotionRuntime.Create(runtime, elsewhere, Body(flagged: false, impactForce: impactForce),
                            Authored)?.Seek(0.2f);
                        return elsewhere.GlobalPosition.Y;
                    }

                    runtime.ArmInheritedVelocity(new Vector3(0f, -45f, 0f));
                    float gated = LaunchY(impactForce: false);
                    float carried = LaunchY(impactForce: true);
                    ctx.Check(gated - carried > 5f,
                        $"the authored impact_force flag is what admits the momentum, not the def's name: flagged fell to {carried:0.00}, unflagged to {gated:0.00}");
                    // ⚠ And the arm is a flag, not "the vector is non-zero": a sub-threshold Callback
                    // 16 CLEARS it in the original (`004ee143`), so a flagged body gets nothing after
                    // one, even though the stored vector is not zero.
                    runtime.ArmInheritedVelocity(new Vector3(0f, -0.005f, 0f));
                    ctx.Check(!runtime.InheritedVelocityArmed
                              && runtime.InheritedWorldVelocity != Vector3.Zero,
                        $"a sub-0.01 Callback 16 disarms the instance and still stores its vector armed={runtime.InheritedVelocityArmed} v={runtime.InheritedWorldVelocity}");
                    float afterDisarm = LaunchY(impactForce: true);
                    ctx.Check(Mathf.Abs(afterDisarm - gated) < 0.01f,
                        $"…so the flagged launch inherits nothing after it y={afterDisarm:0.00} unflagged={gated:0.00}");
                    runtime.ArmInheritedVelocity(inheritWas);
                    elsewhere.QueueFree();
                }
                finally
                {
                    runtime.ContactMask = maskWas;
                    node.QueueFree();
                }
            }

            // 3, THE CONTROL. No mask: neither tier, so the body runs its full clock and ends far
            // below the surface, which is also the no-collision-world fallback every golden
            // capture takes.
            var free = Run(0u, _ => false);
            ctx.Check(free.Tier == MotionContactTier.None && !free.ByContact,
                $"with no mask the body selects no tier at all tier={free.Tier} flight={free.Flight:0.00}s");
            ctx.Check(free.EndY < surfaceY - 100f,
                $"the unmasked body falls straight through endY={free.EndY:0.00} surfaceY={surfaceY:0.00}");
            var freeColumn = Run(0u, _ => false, Body(flagged: false));
            ctx.Check(freeColumn.Tier == MotionContactTier.None && freeColumn.EndY < surfaceY - 100f,
                $"and so does the unflagged body the column would otherwise land tier={freeColumn.Tier} endY={freeColumn.EndY:0.00}");
            ctx.Note($"sweep {hit.Flight:0.00}s ending {hit.EndY - surfaceY:0.00} m from the surface; column {column.Flight:0.00}s ending {column.EndY - surfaceY:0.00} m from it; unmasked {free.Flight:0.00}s ending {free.EndY - surfaceY:0.00} m from it");
            // ⚠ Two decoded rules are deliberately not asserted here. With a downward column, the
            // descending-step admission and complex's widening of it can only save the query, never change the
            // outcome; both are transcribed in TryGroundColumn, and the query's shape is what to re-check.
        });
    }

    // ---- the tumble: a rate about the launch's own perpendicular ---------------------------------

    // FORWARD_ROTATION turns a launched body about the horizontal PERPENDICULAR of its own launch
    // direction, at the authored rate and scaled by that direction's horizontal length, so one authored
    // number tumbles a flat throw fast and a steep one slowly. Every case is arithmetic on a synthetic
    // body, with min = max ranges and the pose read back as geometry, not as the euler triple written.
    // ⚠ A vector-translation body turns about its COMPILED direction (`rnd_xz`) and holds exactly
    // when that is zero; both cases fail if the axis is ever "fixed" to a mesh axis.
    [Suite("forward-rotation",
        "an OBJECT_MOTION tumble turns at the authored RATE about its own launch direction's horizontal perpendicular, scaled by that direction's length, a vector-translation launch about its compiled direction, and not at all when that is zero")]
    internal static void ForwardRotation(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var runtime = world.Runtime;
            var root = world.Session.Root;

            static Dictionary<string, object?> Vec(float x, float y, float z) =>
                new() { ["x"] = x, ["y"] = y, ["z"] = z };
            static Dictionary<string, object?> Range(float v) =>
                new() { ["min"] = v, ["max"] = v };

            // A ranged launch at one exact azimuth/elevation, tumbling at `rate` (+`accel`).
            static AnimData Ranged(float azimuth, float elevation, float rate, float accel = 0f,
                float runTime = 5f) =>
                new(new Dictionary<string, object?>
                {
                    ["translation_range"] = new Dictionary<string, object?>
                    {
                        ["xz"] = Range(azimuth),
                        ["y"] = Range(elevation),
                        ["initial"] = Range(10f),
                        ["delta"] = Range(0f),
                    },
                    ["forward_rotation"] = new Dictionary<string, object?>
                    {
                        ["Time"] = new Dictionary<string, object?>
                        {
                            ["initial"] = rate,
                            ["delta"] = accel,
                        },
                    },
                    ["run_time"] = runTime,
                });

            // The same tumble on the VECTOR launch form, the shape the crash pieces author. `dir` is
            // the compiled direction cache the extractor names `rnd_xz`.
            static AnimData Vector(float rate, Vector3 dir) =>
                new(new Dictionary<string, object?>
                {
                    ["translation"] = new Dictionary<string, object?>
                    {
                        ["initial"] = Vec(10f * dir.X, 10f * dir.Y, 10f * dir.Z),
                        ["delta"] = Vec(0f, 0f, 0f),
                        ["rnd_xz"] = Vec(dir.X, dir.Y, dir.Z),
                    },
                    ["forward_rotation"] = new Dictionary<string, object?>
                    {
                        ["Time"] = new Dictionary<string, object?>
                        {
                            ["initial"] = rate,
                            ["delta"] = 0f,
                        },
                    },
                    ["run_time"] = 5f,
                });

            Basis Pose(AnimData data, float t)
            {
                var node = new Node3D { Name = "forward-rotation-probe" };
                root.AddChild(node);
                try
                {
                    var motion = MotionRuntime.Create(runtime, node, data, data.Num("run_time") ?? 0f);
                    motion?.Seek(t);
                    return node.Transform.Basis.Orthonormalized();
                }
                finally
                {
                    node.QueueFree();
                }
            }

            // 1, the axis. A flat throw along +X (azimuth 0, h = 1) turns about (0, 0, −1): after a
            // quarter turn at π/2 rad/s the body's own up axis points along the throw and its own X
            // points down, which is an end-over-end tumble FORWARD over the launch.
            var alongX = Pose(Ranged(0f, 0f, Mathf.Pi / 2f), 1f);
            ctx.Check(alongX.Y.IsEqualApprox(Vector3.Right) && alongX.X.IsEqualApprox(Vector3.Down),
                $"a throw along +X pitches forward over it up={alongX.Y} fwd={alongX.X}");

            // And the axis FOLLOWS the throw rather than the mesh: the same body launched along +Z
            // turns about (1, 0, 0) instead, which the old local-X reading cannot produce.
            var alongZ = Pose(Ranged(90f, 0f, Mathf.Pi / 2f), 1f);
            ctx.Check(alongZ.Y.IsEqualApprox(Vector3.Back),
                $"and a throw along +Z pitches forward over THAT up={alongZ.Y}");

            // 2, the rate is a RATE, not an angle over the run time. Two bodies with the same
            // authored number and different run times must be in the same pose at the same instant;
            // under the ÷ run_time reading the 2 s body would have turned 2.5× as far.
            var slow = Pose(Ranged(0f, 0f, 1f, runTime: 5f), 1f);
            var fast = Pose(Ranged(0f, 0f, 1f, runTime: 2f), 1f);
            ctx.Check(slow.X.IsEqualApprox(fast.X) && slow.Y.IsEqualApprox(fast.Y),
                $"the run time does not scale the tumble at 5 s={slow.X} at 2 s={fast.X}");
            ctx.Check(Mathf.Abs(slow.GetEuler(EulerOrder.Yxz).Z + 1f) < 1e-3f,
                $"one second of 1 rad/s is one radian euler={slow.GetEuler(EulerOrder.Yxz)}");

            // 3, the launch's own horizontal length scales it, which is what makes a steep throw
            // tumble slowly off the same authored number. At 60° of elevation h = 1/3.
            var steep = Pose(Ranged(0f, 60f, 1f), 1f);
            float steepAngle = -steep.GetEuler(EulerOrder.Yxz).Z;
            ctx.Check(Mathf.Abs(steepAngle - (1f / 3f)) < 1e-3f,
                $"a 60° launch turns at h = 1 − |elev|/90 of the authored rate angle={steepAngle:0.000} rad expected=0.333");

            // 4, `delta` is the rate's own acceleration, integrated rather than dropped: from rest
            // at 2 rad/s², one second is 1 rad.
            var ramped = Pose(Ranged(0f, 0f, 0f, accel: 2f), 1f);
            float rampedAngle = -ramped.GetEuler(EulerOrder.Yxz).Z;
            ctx.Check(Mathf.Abs(rampedAngle - 1f) < 1e-3f,
                $"forward_rotation.delta accelerates the rate angle={rampedAngle:0.000} rad expected=1.000");

            // 5, the vector launch form turns about the direction the PARSER compiled into its
            // third triple, the same cache the ranged form draws: a +Z launch pitches over +Z.
            var vecZ = Pose(Vector(Mathf.Pi / 2f, Vector3.Back), 1f);
            ctx.Check(vecZ.Y.IsEqualApprox(Vector3.Back),
                $"a vector-translation launch tumbles about its compiled direction up={vecZ.Y}");

            // 6, and with that triple zero (a vertical launch, or a hand-built shape) there is no
            // axis to turn about, so the body holds its orientation however large the authored rate.
            var vec = Pose(Vector(15.708f, Vector3.Zero), 1f);
            ctx.Check(vec.IsEqualApprox(Basis.Identity),
                $"a vector-translation launch with a zero direction does not tumble basis={vec}");
            ctx.Note($"flat 1 rad/s = {-slow.GetEuler(EulerOrder.Yxz).Z:0.000} rad/s, the same launch at 60° = {steepAngle:0.000} rad/s, vector form along +Z up={vecZ.Y}, zero direction = 0");
        });
    }

    // ---- the vector launch form's third triple: a compiled direction, not a spread --------------

    // The parser compiles `TRANSLATION az elev speed delta` into `initial` (direction × speed),
    // `delta` (direction × acceleration) and the direction cache the tumble reads back, which the
    // extractor names `rnd_xz`. Nothing on the vector form is random, so two bodies must fly the
    // identical path and end exactly where `initial` puts them. ⚠ Read as a spread, the third triple
    // walked the Barracuda's 40 s cruise up to 44 m per draw, snapped away by the next placement.
    [Suite("launch-direction-cache",
        "a vector-form OBJECT_MOTION's third triple is the compiled launch DIRECTION the tumble reads back, not a random spread: two bodies fly the identical path and end exactly where initial × run_time puts them")]
    internal static void LaunchDirectionCache(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            var runtime = world.Runtime;
            var root = world.Session.Root;

            static Dictionary<string, object?> Vec(Vector3 v) =>
                new() { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };

            // The Barracuda's cruise event as authored: 40 m/s along +Z for 40 s, no acceleration,
            // the third triple its unit +Z direction (`cache` lets a case vary that triple alone).
            static AnimData Cruise(Vector3 dir, Vector3? cache = null) =>
                new(new Dictionary<string, object?>
                {
                    ["translation"] = new Dictionary<string, object?>
                    {
                        ["initial"] = Vec(dir * 40f),
                        ["delta"] = Vec(Vector3.Zero),
                        ["rnd_xz"] = Vec(cache ?? dir),
                    },
                    ["run_time"] = 40f,
                });

            Vector3 Fly(AnimData data, float t)
            {
                var node = new Node3D { Name = "launch-direction-probe" };
                root.AddChild(node);
                try
                {
                    var motion = MotionRuntime.Create(runtime, node, data, data.Num("run_time") ?? 0f);
                    motion?.Seek(t);
                    return node.Transform.Origin;
                }
                finally
                {
                    node.QueueFree();
                }
            }

            // Two bodies built one after the other share nothing but the data; under the spread
            // reading each drew its own three numbers off the runtime's RNG and the two diverged.
            var first = Fly(Cruise(Vector3.Back), 40f);
            var second = Fly(Cruise(Vector3.Back), 40f);
            var expected = new Vector3(0f, 0f, 1600f);
            ctx.Check(first.IsEqualApprox(expected),
                $"the cruise ends exactly where initial × run_time puts it end={first} expected={expected}");
            ctx.Check(first.IsEqualApprox(second),
                $"a second body flies the identical path first={first} second={second}");

            // The third triple is a direction, not an amplitude: scaling it changes nothing about
            // where the body goes, only (through the tumble) how it turns.
            var scaled = Fly(Cruise(Vector3.Back, cache: Vector3.Back * 2f), 40f);
            ctx.Check(scaled.IsEqualApprox(expected),
                $"a longer third triple moves the body not one metre further end={scaled}");
            ctx.Note($"cruise end {first} against the authored placement 1600 m along +Z");
        });
    }

    // ---- a chain of OBJECT_MOTION events on one placed node: the Barracuda's drive -------------

    // C3/M03's `sub_movement` is the install's clearest chain: a reset placement 17.8 km from the
    // hull's gamez pose (the map origin), a surfacing FromTo, three vector-form drive legs and a
    // closing FromTo 1.2 m past where the legs end. The original integrates every leg from the
    // node's live translation, so the hull never leaves its line. ⚠ A launch seeded from the
    // authored rest pose plays the whole drive at the map origin and snaps back into the bay.
    [Suite("barracuda-drive",
        "a chain of OBJECT_MOTION events on one placed node integrates every leg from the node's live pose: C3/M03's sub_movement drives the Barracuda 1680 m along +Z from its reset placement with no frame-to-frame jump and ends within metres of its closing FromTo in the bay")]
    internal static void BarracudaDrive(TestContext ctx)
    {
        const float Tick = 1f / 30f;
        const float DriveSeconds = 62f;     // 10 s surfacing, 44 s of drive, 6 s of settling, slack
        const float StepLimitM = 10f;       // 40 m/s is 1.3 m per tick; a re-seat is kilometres
        const float EndToleranceM = 3f;
        var bay = new Vector3(-12032f, 0f, -11516.288f);
        var start = new Vector3(-12032f, -38f, -13197.5f);

        ctx.WithWorld("C3", collision: false, "M03", world =>
        {
            var runtime = world.Runtime;
            var hulls = runtime.FindNodes("barracuda");
            ctx.Same(1, hulls.Count, $"C3/M03 builds one barracuda node");
            if (hulls.Count != 1)
            {
                return;
            }
            var hull = hulls[0];
            var started = runtime.Play("sub_movement");
            ctx.Check(started.Count > 0, $"sub_movement starts ({started.Count} instance(s))");
            var placed = hull.GlobalPosition;
            ctx.Check(placed.DistanceTo(start) < 1f,
                $"the reset places the hull at its authored start placed={placed} start={start}");

            var trace = new StringBuilder();
            var last = placed;
            float maxStep = 0f, maxStepAt = 0f;
            Vector3 maxStepFrom = placed, maxStepTo = placed;
            int steps = (int)(DriveSeconds / Tick);
            for (int i = 1; i <= steps; i++)
            {
                runtime.Advance(Tick);
                var now = hull.GlobalPosition;
                float step = now.DistanceTo(last);
                if (step > maxStep)
                {
                    maxStep = step;
                    maxStepAt = i * Tick;
                    maxStepFrom = last;
                    maxStepTo = now;
                }
                if (i % 30 == 0)
                {
                    trace.AppendLine($"t={i * Tick:0.0} pos=({now.X:0.0},{now.Y:0.00},{now.Z:0.0}) step={step:0.00}");
                }
                last = now;
            }

            var end = hull.GlobalPosition;
            var basis = hull.GlobalTransform.Basis.Orthonormalized();
            float yaw = basis.GetEuler(EulerOrder.Yxz).Y;
            var nose = -basis.Z;
            ctx.Check(maxStep < StepLimitM,
                $"the hull never jumps: largest step {maxStep:0.0} m at t={maxStepAt:0.0} s from {maxStepFrom} to {maxStepTo}");
            ctx.Check(end.DistanceTo(bay) < EndToleranceM,
                $"the drive ends in the bay end={end} authored={bay} off by {end.DistanceTo(bay):0.00} m");
            ctx.Check(end.Z > start.Z + 1000f,
                $"the drive ran along +Z from the start, not out to sea end.Z={end.Z:0.0} start.Z={start.Z:0.0}");
            trace.AppendLine($"end=({end.X:0.0},{end.Y:0.00},{end.Z:0.0}) yaw={yaw:0.000} rad nose={nose}");
            ctx.WriteArtifact("test-barracuda-drive.txt", trace.ToString());
            ctx.Note($"largest step {maxStep:0.00} m, end {end}, yaw {Mathf.RadToDeg(yaw):0.0} deg, nose (local -Z) points {nose}");
        });
    }

    // ---- the animation-list ACTIVATION_PREREQUISITE: a call answered on the Nth caller ----------

    // CM18's release is the install's plainest carrier: cargozep_floatfree names its own five
    // restraint deaths and authors MINIMUM_TO_SATISFY 5, and each of those five calls it. Ungated,
    // the Black Swan tears loose on the FIRST tie-down and its 190 s "too late to hook" clock runs
    // out while the player is still shooting the other four, which closes the hookup the whole back
    // half of the mission hangs off. ⚠ Play must stay ungated: every zeppelin hull death carries
    // the same shape and is fired from ZeppelinRuntime's damage model, which owns that kill.
    [Suite("anim-activation-prerequisite",
        "a CALL_ANIMATION is answered only once MINIMUM_TO_SATISFY of the animations the callee's ACTIVATION_PREREQUISITE names have run: CM18's cargozep_floatfree holds the Black Swan at its mooring through four of its five restraint deaths and releases it on the fifth, while an explicit Play still starts a hull death carrying the same shape")]
    internal static void ActivationPrerequisite(TestContext ctx)
    {
        const float Tick = 1f / 30f;
        const float SettleSeconds = 4f;
        const float MovedM = 1f;
        string[] restraints =
        {
            "breaktiedown01", "breaktiedown02", "breaktiedown03", "breaktiedown04", "breakmainclamp",
        };

        ctx.WithWorld("C4", collision: false, "M03", world =>
        {
            var runtime = world.Runtime;
            var hulls = runtime.FindNodes("cargozep1");
            ctx.Same(1, hulls.Count, $"C4/M03 builds one cargozep1 node");
            if (hulls.Count != 1)
            {
                return;
            }
            var hull = hulls[0];
            var moored = hull.GlobalPosition;
            var report = new StringBuilder();
            report.AppendLine($"moored at ({moored.X:0.0},{moored.Y:0.0},{moored.Z:0.0})");

            for (int i = 0; i < restraints.Length; i++)
            {
                ctx.Check(runtime.Play(restraints[i]).Count > 0, $"'{restraints[i]}' starts");
                for (int step = 0; step < (int)(SettleSeconds / Tick); step++)
                {
                    runtime.Advance(Tick);
                }

                int state = runtime.AnimStateOf("cargozep_floatfree");
                float drift = hull.GlobalPosition.DistanceTo(moored);
                bool last = i == restraints.Length - 1;
                report.AppendLine($"after {i + 1}/5 '{restraints[i]}': floatfree state={state} drift={drift:0.00} m");
                if (last)
                {
                    ctx.Check(state != 0, $"the fifth restraint releases the hull (state={state})");
                    ctx.Check(drift > MovedM, $"the hull is under way after the fifth ({drift:0.00} m)");
                }
                else
                {
                    ctx.Same(0, state, $"{i + 1} of 5 restraints leaves cargozep_floatfree unstarted");
                    ctx.Check(drift <= MovedM, $"the hull holds its mooring after {i + 1} of 5 ({drift:0.00} m)");
                }
            }

            // The Play bypass, on this world's own hull-death def: no finish_cg1zepgasbag* has run,
            // so the count is 0 of 3 and only an explicit Play may still start it.
            int death = runtime.Play("all_cg1zep_gasbags").Count;
            ctx.Check(death > 0,
                $"an explicit Play still starts a hull death whose gasbag count is unmet ({death} instance(s))");
            ctx.WriteArtifact("test-activation-prerequisite.txt", report.ToString());
            ctx.Note($"released {hull.GlobalPosition.DistanceTo(moored):0.0} m from the mooring on the fifth restraint (per-restraint trace in test-activation-prerequisite.txt)");
        });
    }

    // ---- CALL_ANIMATION: where the callee's first step lands in the tick -------------------------

    // The original's dispatcher walks a linked list of live animation records and re-reads the link
    // after every callback, and a start appends at the tail, so a definition called mid-tick takes
    // its first step after the caller's remaining events. Taking it inside the call instead lets a
    // callee's INVALIDATE_ANIMATION latch a name the caller has yet to call, which the original
    // cannot reach. Decode: docs/org/sequences.md.
    [Suite("anim-call-start-order",
        "a CALL_ANIMATION callee takes its first step after the caller's remaining events of the same tick rather than inside the call, so a callee that invalidates a name the caller calls one event later cannot cancel that call; a fan-out past the walk's start budget still gives every one of its callees that first step at zero dt, on the walk after")]
    internal static void CallStartOrder(TestContext ctx)
    {
        const float Tick = 1f / 30f;
        const string Caller = "order_caller";
        const string Callee = "order_callee";
        const string Victim = "order_victim";
        var program = AnimProgram.FromDefinitions(new[]
        {
            // The call that starts the callee is scheduled, so it is dispatched by a tick's walk
            // and not by the instant burst a Play runs; the second call follows in the same tick.
            OrderDef(Caller,
                OrderEvent("CallAnimation", Callee, "Sequence", 0.1f),
                OrderEvent("CallAnimation", Victim)),
            OrderDef(Callee, OrderEvent("InvalidateAnimation", Victim)),
            OrderDef(Victim, OrderEvent("StopAnimation", "order_nothing")),
        });
        var stage = new Node3D { Name = "CallOrderStage" };
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
            runtime.Bind(stage, program);
            var fired = new List<string>();
            var started = new List<string>();
            runtime.OnEventDispatched = d => fired.Add($"{d.Def.AnimName}:{d.EventKind}");
            runtime.OnInstanceStarted = (def, _) => started.Add(def.AnimName ?? "");
            ctx.Same(1, runtime.Play(Caller).Count, $"the caller starts as one instance");
            for (int step = 0; step < 16 && !fired.Contains($"{Victim}:StopAnimation"); step++)
            {
                runtime.Advance(Tick);
            }

            ctx.Check(started.Contains(Victim),
                $"the caller's second call starts {Victim}, the callee's INVALIDATE_ANIMATION not having run yet");
            string order = string.Join(" | ", fired);
            string want = $"{Caller}:CallAnimation | {Caller}:CallAnimation | "
                + $"{Callee}:InvalidateAnimation | {Victim}:StopAnimation";
            ctx.Check(order == want,
                $"both of the caller's events fire before the callee's first one, and the callee before the definition it started: {order}");
            ctx.Note($"dispatch order over {Tick:0.000} s ticks: {order}");
        }
        finally
        {
            runtime.Free();
            stage.Free();
        }

        QueuedStartsPastTheBudget(ctx);
    }

    // ---- the MAIN_ROOT_NODE self-reference: a launch onto the def's own anchor -------------------

    // MAIN_ROOT_NODE / INPUT_NODE mean "the node this definition was invoked on", a sentinel and not a
    // name, so a resolver that only matches names finds nothing and drops the event without a word,
    // leaving the whole self-referencing population unlaunched. C5's agyrobus is the whole test: it is
    // the only carrier a player can reach, and having no placement of its own, a launch that re-homes
    // to rest teleports the wreck kilometres away, so the launch must take the node over from the live
    // playback. ⚠ Stay branch-agnostic; randomdestseq opens with IF RandomWeight and either may draw.
    [Suite("self-ref-launch",
        "an OBJECT_MOTION naming the MAIN_ROOT_NODE sentinel launches the node its def was invoked on, taking that node over from whatever was driving it")]
    internal static void SelfRefLaunch(TestContext ctx)
    {
        const float Tick = 1f / 60f;
        const float Settle = 2f;    // let the fly script get the bus clear of the origin first

        ctx.WithWorld("C5", collision: true, world =>
        {
            var runtime = world.Runtime;
            var bus = runtime.Destructibles.All.FirstOrDefault(
                i => i.Def.AnimName is { } n && n.Equals("agyrobus", System.StringComparison.OrdinalIgnoreCase));
            ctx.Check(bus != null, $"C5 ships the agyrobus destructible pools={runtime.Destructibles.All.Count}");
            if (bus?.Anchor is not { } anchor)
            {
                return;
            }

            if (bus.Status == DestructibleRegistry.State.Destroyed)
            {
                runtime.ResetDestructible(bus);
            }

            IAnimMotion? DriverOf(Node3D node) => runtime.Motions.Live
                .FirstOrDefault(m => m.Target == node && m.Channel == MotionChannel.Transform);

            var origin = anchor.GlobalPosition;   // where the world PLACED it: the map origin
            for (int i = 0; i < (int)(Settle / Tick); i++)
            {
                runtime.Advance(Tick);
            }

            // The precondition, and the thing that makes the re-home wrong: the bus's whole
            // position is the SI script's doing, and its authored rest is nowhere near it.
            var flown = anchor.GlobalPosition;
            ctx.Check(DriverOf(anchor) is ScriptPlayback,
                $"agbus_fly's SI script drives the bus before the kill driver={DriverOf(anchor)?.GetType().Name ?? "(none)"}");
            ctx.Check((flown - origin).Length() > 100f,
                $"the fly script has carried the bus clear of its authored rest flown={(flown - origin).Length():0} m");

            uint maskWas = runtime.ContactMask;
            runtime.ContactMask = CollisionLayers.World;   // what a real session wires; a suite world does not
            try
            {
                int launchesWas = runtime.Motions.LaunchCount;
                runtime.DamageAt(anchor, bus.MaxHealth + 1f);
                // TWO frames, and the second matters: the death's sequence runs during an Advance after that
                // frame's motions have ticked, so one frame in the launch is registered but has written no pose,
                // and a re-homed launch would look like it had not moved.
                runtime.Advance(Tick);
                runtime.Advance(Tick);

                // 1, the sentinel resolved. Without it the OBJECT_MOTION dispatches, targets
                // nothing, and registers no body at all: launches+0, which is the only way that
                // failure shows in the log.
                var driver = DriverOf(anchor);
                ctx.Check(driver is MotionRuntime,
                    $"the MAIN_ROOT_NODE launch drives the def's own anchor driver={driver?.GetType().Name ?? "(none)"} launches+{runtime.Motions.LaunchCount - launchesWas}");

                // 2, and it took over from the playback rather than re-basing on the map origin.
                var launchedAt = anchor.GlobalPosition;
                ctx.Check((launchedAt - flown).Length() < 5f,
                    $"the launch starts where the bus was, not at its authored rest jump={(launchedAt - flown).Length():0.0} m rest={(launchedAt - origin).Length():0} m away");

                for (int i = 0; i < (int)(5f / Tick); i++)
                {
                    runtime.Advance(Tick);
                }

                // 3, the wreck falls instead of flying on. Both halves show here: the fly script
                // is off the node, and the horizontal travel collapses from the ~60 m/s route to
                // the ballistic drift of a hull with no launch velocity.
                var after = anchor.GlobalPosition;
                ctx.Check(DriverOf(anchor) is not ScriptPlayback,
                    $"agbus_fly no longer drives the wreck driver={DriverOf(anchor)?.GetType().Name ?? "(none)"}");
                float horizontal = new Vector2(after.X - launchedAt.X, after.Z - launchedAt.Z).Length();
                float routeSpeed = new Vector2(flown.X - origin.X, flown.Z - origin.Z).Length() / Settle;
                ctx.Check(horizontal < routeSpeed,   // one second of route, against five of falling
                    $"the wreck falls rather than continuing its route horizontal={horizontal:0.0} m over 5 s (route was {routeSpeed:0} m/s)");
                ctx.Check(after.Y < launchedAt.Y - 10f,
                    $"and it is going down drop={launchedAt.Y - after.Y:0.0} m");
                ctx.Note($"contact tallies: {runtime.Motions.ContactLandings} landed on a collider, {runtime.Motions.ClockEndings} ran their clock out");
            }
            finally
            {
                runtime.ContactMask = maskWas;
            }
        });
    }

    // ---- bounce-terminated launches fly and land ------------------------------------------------

    // An OBJECT_MOTION that omits RUN_TIME and names a BOUNCE_SEQUENCE is the data's "fly until you hit
    // something" idiom: it must solve its own flight time, actually fly, and dispatch that sequence on
    // landing. Kills one refuel* tank through AnimRuntime.DamageAt, the same call a rocket makes, and
    // asserts on the dispatch timeline. Scope and able-to-fail: this module's docs/architecture.md entry.
    // ⚠ Assert a BAND, never an exact time; both launches draw speed and elevation per instance and
    // the draw moves with suite order. ⚠ Do not read the emitter census here; that is emitter-lifetime's.
    [Suite("bounce-launch",
        "a bounce-terminated OBJECT_MOTION flies its solved parabola and fires its BOUNCE_SEQUENCE on landing")]
    internal static void BounceLaunch(TestContext ctx)
    {
        // The two bounce-terminated events of refuel1's healthy def: t = 2*v0y/|g| over the authored speed
        // and elevation ranges, so the support is closed. A landing is detected on a frame boundary, so an
        // observed time can run one tick long.
        // ⚠ The elevation is LINEAR, not spherical (MotionRuntime.RangeLaunchDirection): v0y is
        // speed*elev/90, not speed*sin(elev), which is why these bands sit ~23 % below the sin() ones.
        const float Part3Min = 2.400f, Part3Max = 3.422f;
        const float Part4Min = 2.178f, Part4Max = 4.000f;
        const float Tick = 1f / 60f;
        const string chapter = "C1";   // the only chapter shipping refuel* (5 defs)
        const string lateBounce = "ObjectMotion(bounce landed after its instance ended)";

        // The bands are derived from the AUTHORED speed and elevation ranges, because what this suite
        // asserts is the decode: a bounce-terminated body flies the parabola the data describes. With the
        // old global launch tune gone, the authored arc IS what flies and the bands apply directly.
        ctx.WithWorld(chapter, collision: false, world =>
        {
            var runtime = world.Runtime;
            DestructibleRegistry.Instance? tank = null;
            foreach (var inst in runtime.Destructibles.All)
            {
                if (inst.Def.AnimName is { } name
                    && name.StartsWith("refuel", System.StringComparison.OrdinalIgnoreCase))
                {
                    tank = inst;
                    break;
                }
            }
            ctx.Check(tank != null, $"chapter ships a refuel tank chapter={chapter}");
            if (tank == null)
            {
                return;
            }

            // A shared cached world reaches this suite already swept by damage-hd, and DamageAt is
            // a no-op on something already destroyed, heal first so the death actually runs.
            if (tank.Status == DestructibleRegistry.State.Destroyed)
            {
                runtime.ResetDestructible(tank);
            }

            int Missed() =>
                runtime.UnhandledEventCounts.TryGetValue(lateBounce, out int n) ? n : 0;

            var timeline = new List<(float T, string Seq, string Kind, string? Name)>();
            // Only the tank's OWN dispatches: on a shared cached world another suite's earlier kill can still
            // be running its authored death, and a runtime-wide read here would count that neighbour's launch
            // against this one (INSTR-10).
            var tankDef = tank.Def;
            float clock = 0f;
            var previous = runtime.OnEventDispatched;
            int missedBefore = Missed();
            bool everOwed = false;
            try
            {
                runtime.OnEventDispatched = d =>
                {
                    if (d.Def == tankDef)
                    {
                        timeline.Add((clock, d.Sequence, d.EventKind, d.EventName));
                    }
                };
                runtime.DamageAt(tank.Anchor, tank.MaxHealth + 1f);
                for (int i = 0; i < 600; i++)   // 10 s, comfortably past the 4.0 s worst-case flight
                {
                    clock += Tick;
                    runtime.Advance(Tick);
                    everOwed |= runtime.Motions.OwesBounce(tank.Def, tank.Anchor);
                }
            }
            finally
            {
                runtime.OnEventDispatched = previous;
            }

            ctx.Check(everOwed, $"a launched {tank.Def.AnimName} piece owed its BOUNCE_SEQUENCE while in flight");
            ctx.Check(!runtime.Motions.OwesBounce(tank.Def, tank.Anchor),
                $"nothing is still owed once every piece has landed");

            // part1/part2 with an authored RUN_TIME plus part3/part4 solved, and all four are this def's own
            // ObjectMotion events. Counted from the def-scoped timeline rather than the runtime-wide tally,
            // for the neighbour launch a shared world can bleed into this window.
            int launched = timeline.Count(e => e.Kind == "ObjectMotion");
            ctx.Same(4, launched, $"ballistic launches on one {tank.Def.AnimName} death");

            float LaunchAt(string node) => timeline
                .Where(e => e.Kind == "ObjectMotion" && e.Name == node)
                .Select(e => e.T).DefaultIfEmpty(-1f).First();
            float BounceAt(string seq) => timeline
                .Where(e => e.Seq == seq)
                .Select(e => e.T).DefaultIfEmpty(-1f).First();

            CheckFlight(ctx, "part3", "sparkout3", LaunchAt("part3"), BounceAt("sparkout3"),
                Part3Min, Part3Max + Tick);
            CheckFlight(ctx, "part4", "sparkout4", LaunchAt("part4"), BounceAt("sparkout4"),
                Part4Min, Part4Max + Tick);

            // The bounce sequence is what deactivates the flying piece and pops its fireball,
            // both of its events must run, not just the first.
            ctx.Same(2, timeline.Count(e => e.Seq == "sparkout3"), $"sparkout3 events dispatched");
            ctx.Same(2, timeline.Count(e => e.Seq == "sparkout4"), $"sparkout4 events dispatched");
            ctx.Same(0, Missed() - missedBefore, $"refuel bounces landing after their instance ended");

            // A landing must reach a LIVE instance and the launch is the last event of its sequence, so nothing
            // but the retirement hold keeps one reachable; refuel* cannot show that but the yard buildings can.
            // Grouped by ANCHOR, since a wildcard def and its compiled twin both bind these seven nodes.
            var yard = runtime.Destructibles.All
                .Where(i => i.Def.AnimName is { } n
                            && n.StartsWith("m_build", System.StringComparison.OrdinalIgnoreCase))
                .GroupBy(i => i.Anchor)
                .Select(g => g.First())
                .ToList();
            ctx.Same(7, yard.Count, $"chapter ships the yard buildings chapter={chapter}");

            timeline.Clear();
            clock = 0f;
            missedBefore = Missed();
            // Same def scoping as the refuel hook: the yard buildings' own sparkout3/sparkout4
            // sequence names repeat on the refuel def, so an unscoped read would also count a
            // neighbour's late bounce.
            var yardDefs = new HashSet<AnimDefinition>(yard.Select(b => b.Def));
            try
            {
                runtime.OnEventDispatched = d =>
                {
                    if (yardDefs.Contains(d.Def))
                    {
                        timeline.Add((clock, d.Sequence, d.EventKind, d.EventName));
                    }
                };
                foreach (var b in yard)
                {
                    if (b.Status == DestructibleRegistry.State.Destroyed)
                    {
                        runtime.ResetDestructible(b);
                    }
                    runtime.DamageAt(b.Anchor, b.MaxHealth + 1f);
                }
                for (int i = 0; i < 600; i++)
                {
                    clock += Tick;
                    runtime.Advance(Tick);
                }
            }
            finally
            {
                runtime.OnEventDispatched = previous;
            }

            ctx.Same(0, Missed() - missedBefore, $"yard bounces landing after their instance ended");
            ctx.Note($"the two zero-miss checks are invariants this seed does not discriminate: removing A4's retirement hold leaves both green here. the able-to-fail control is a SEED SWEEP of the --destroy=m_build probe, not this suite and not one run of that probe, with the hold removed it misses on 4 of 10 seeds and is clean on seed 1 (INSTR-6)");
            ctx.Same(
                yard.Count * 4,   // sparkout3 + sparkout4, two events each, per building
                timeline.Count(e => e.Seq == "sparkout3" || e.Seq == "sparkout4"),
                $"yard sparkout events dispatched");
        });
    }

    // One bounce-terminated piece: it must launch, and its bounce sequence must fire a
    // flight time later that lands inside the band its authored `translation_range` allows.
    // A missing launch and a missing landing are reported apart, they are different bugs.
    internal static void CheckFlight(
        TestContext ctx, string node, string seq, float launchAt, float bounceAt, float min, float max)
    {
        ctx.Check(launchAt >= 0f, $"{node}'s bounce-terminated OBJECT_MOTION dispatches t={launchAt:0.000}");
        ctx.Check(bounceAt >= 0f, $"{seq} dispatches on landing t={bounceAt:0.000}");
        if (launchAt < 0f || bounceAt < 0f)
        {
            return;
        }
        float flight = bounceAt - launchAt;
        ctx.Note($"{node} solved flight {flight:0.000} s (band {min:0.000}…{max:0.000})");
        ctx.Check(flight >= min && flight <= max,
            $"{node}'s solved flight is inside its authored band flight={flight:0.000} band={min:0.000}…{max:0.000}");
    }

    // ---- a launch that names neither RUN_TIME nor BOUNCE_SEQUENCE -------------------------------

    // The third launch shape: OBJECT_MOTION events that omit RUN_TIME and BOUNCE_SEQUENCE and follow
    // the launch with the flying piece's own null-start ACTIVE_STATE 0. A flight solve gated on a
    // bounce being named declines all of them, so the piece is hidden before it moves. The zeppelin
    // cannon's eight parts are the reachable repro (analysis/bl-257-nulled-launch/).
    // ⚠ Measure the GAP between each part's launch and its own deactivation, not a mesh count, which
    // reads 8/8 either way. ⚠ Assert a BAND; elevation and speed are per-instance draws, and linear.
    [Suite("nulled-launch",
        "an OBJECT_MOTION naming NEITHER RUN_TIME nor BOUNCE_SEQUENCE flies its solved parabola before its own deactivation switches it off (BL-257)")]
    internal static void NulledLaunch(TestContext ctx)
    {
        // extracted/*/cam_anim/zep_can_dstry1-dblcannon_flying_parts.json: eight parts, each
        // xz −135…135°, y 10…70°, initial 17…25 m/s, gravity −9.8, no run_time, no bounce_sequence.
        //   min  2·17·(10/90)/9.8 = 0.385 s       max  2·25·(70/90)/9.8 = 3.968 s
        // A dispatch is observed on a frame boundary, so the gap can run one tick long.
        const float FlightMin = 0.385f, FlightMax = 3.968f;
        const float Tick = 1f / 60f;
        const string root = "zep_ng_dstry1_flt";   // the staged copy's node name, '.' sanitised

        // Same as bounce-launch: the band is the AUTHORED support, which is now simply what flies.
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            EffectStageSuiteHelper.WithEffectStage(ctx, world, "biggun_flying_parts", new[] { "zep_ng_dstry1.flt" },
                (stage, runtime, point) =>
            {
                var parts = new Dictionary<string, Node3D>(System.StringComparer.Ordinal);
                CollectNamed(stage, parts);
                ctx.Same(8, parts.Count, $"the staged wreck carries its eight parts");

                var launched = new Dictionary<string, float>(System.StringComparer.Ordinal);
                var switchedOff = new Dictionary<string, float>(System.StringComparer.Ordinal);
                var moved = new Dictionary<string, float>(System.StringComparer.Ordinal);
                var restAt = new Dictionary<string, Vector3>(System.StringComparer.Ordinal);
                bool bounceOwed = false;
                float clock = 0f;
                var previous = runtime.OnEventDispatched;
                try
                {
                    runtime.OnEventDispatched = d =>
                    {
                        if (d.EventName is not { } name || !parts.ContainsKey(name))
                        {
                            return;
                        }
                        if (d.EventKind == "ObjectMotion" && !launched.ContainsKey(name))
                        {
                            launched[name] = clock;
                            restAt[name] = parts[name].Position;
                        }
                        else if (d.EventKind == "ObjectActiveState" && !switchedOff.ContainsKey(name))
                        {
                            switchedOff[name] = clock;
                        }
                        // Nothing here names a bounce, so nothing may owe one, the widened gate
                        // must solve the flight WITHOUT arming a landing sequence that does not
                        // exist, the over-generalization to watch for.
                        bounceOwed |= runtime.Motions.OwesBounce(d.Def, d.Anchor);
                    };
                    runtime.PlayEffectAt("biggun_flying_parts", point);
                    for (int i = 0; i < 360; i++)   // 6 s, past the 3.97 s worst-case flight
                    {
                        clock += Tick;
                        runtime.Advance(Tick);
                        foreach (var (name, node) in parts)
                        {
                            if (restAt.TryGetValue(name, out var rest))
                            {
                                float d = node.Position.DistanceTo(rest);
                                moved[name] = Mathf.Max(moved.GetValueOrDefault(name), d);
                            }
                        }
                    }
                }
                finally
                {
                    runtime.OnEventDispatched = previous;
                }

                ctx.Same(8, launched.Count, $"parts launched by one {root} destruction");
                ctx.Check(!bounceOwed, $"no part owes a BOUNCE_SEQUENCE, the data names none");
                foreach (var name in parts.Keys.OrderBy(n => n, System.StringComparer.Ordinal))
                {
                    if (!launched.TryGetValue(name, out float at))
                    {
                        ctx.Check(false, $"{name} never launched");
                        continue;
                    }
                    ctx.Check(switchedOff.TryGetValue(name, out float off),
                        $"{name}'s own null-start deactivation dispatches");
                    if (!switchedOff.ContainsKey(name))
                    {
                        continue;
                    }
                    float flight = off - at;
                    ctx.Note($"{name} flew {flight:0.000} s and travelled {moved.GetValueOrDefault(name):0.0} m (band {FlightMin:0.000}…{FlightMax:0.000})");
                    ctx.Check(flight >= FlightMin && flight <= FlightMax + Tick,
                        $"{name} flies its solved parabola before it is switched off flight={flight:0.000} band={FlightMin:0.000}…{FlightMax:0.000}");
                    ctx.Check(moved.GetValueOrDefault(name) > 1f,
                        $"{name} actually left its rest pose travelled={moved.GetValueOrDefault(name):0.0} m");
                }
            });
        });
    }

    // The `part1`…`part8` the flying-parts def drives, by node name, from
    // anywhere under the staged template. Named lookup rather than a child index: the wreck is
    // real gamez geometry and its parts sit at whatever depth it authors them.
    internal static void CollectNamed(Node node, Dictionary<string, Node3D> into)
    {
        if (node is Node3D n3 && n3.Name.ToString().StartsWith("part", System.StringComparison.Ordinal))
        {
            into[n3.Name.ToString()] = n3;
        }
        foreach (var child in node.GetChildren())
        {
            CollectNamed(child, into);
        }
    }

    // ---- WAIT_FOR_COMPLETION --------------------------------------------------------------------

    // WAIT_FOR_COMPLETION on the authored case, with its own control beside it in the same sequence.
    // player_crash_water's destroy_crash is the install's clean discriminator: eleven events, of which
    // exactly one carries the flag, followed immediately by an unflagged large_steam_spray that would
    // otherwise start with the splash instead of after it.
    // ⚠ Assert the control too, the nine unflagged calls that must still all start at t=0. A runtime
    // that held every call would pass the spray check and fail those.
    [Suite("wait-for-completion",
        "a WAIT_FOR_COMPLETION call holds the caller's next event for its callee, and an unflagged one beside it does not (BL-228)")]
    internal static void WaitForCompletion(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            const string animName = "player_crash_water";
            const string flagged = "plane_big_splash";
            const string held = "large_steam_spray";
            var program = world.Session.Program.Subset(animName);
            var defs = program.ByAnimName(animName);
            ctx.Check(defs.Count > 0, $"chapter program has {animName} defs={defs.Count}");
            if (defs.Count == 0)
            {
                return;
            }

            // The caller's own nodes plus each callee's ROOT: on a flat stage a callee whose root
            // is missing falls back to the caller's anchor, which still runs but stops being the
            // separate instance whose lifetime is the subject here.
            var nodes = new List<string>
            {
                "player", "healthy", "destroyed", "dontmove", "markers", "shadow", "cockpit1",
                "huge_splash_model", "splash_polys", "sp_1", "white_water_impact",
                "carnage_trails", "large_fire", "ripple1",
            };
            for (int i = 1; i <= 4; i++)
            {
                nodes.Add($"piece{i}");
            }

            WithEmitterStage(ctx, program, "CrashWaterStage", nodes, (stage, runtime, fake) =>
            {
                float clock = 0f;
                var startedAt = new Dictionary<string, float>(System.StringComparer.OrdinalIgnoreCase);
                runtime.OnInstanceStarted = (d, _) =>
                {
                    if (d.AnimName is { } name && !startedAt.ContainsKey(name))
                    {
                        startedAt[name] = clock;
                    }
                };
                runtime.Start(defs[0], stage);
                for (int i = 0; i < 480; i++)   // 8 s, well past the splash's authored 3.0 s
                {
                    clock += 1f / 60f;
                    runtime.Advance(1f / 60f);
                }
                runtime.OnInstanceStarted = null;

                ctx.Check(startedAt.ContainsKey(flagged), $"{flagged} became a live instance");
                ctx.Check(startedAt.ContainsKey(held), $"{held} became a live instance");
                if (!startedAt.TryGetValue(flagged, out float splashAt)
                    || !startedAt.TryGetValue(held, out float sprayAt))
                {
                    return;
                }

                ctx.Note($"destroy_crash: {flagged} t={splashAt:0.000}s, {held} t={sprayAt:0.000}s (gap {sprayAt - splashAt:0.000}s vs the authored 3.0s), holds armed={runtime.WaitsInstalled} abandoned={runtime.WaitsAbandoned}");
                ctx.Check(splashAt <= 2f / 60f,
                    $"the flagged call itself is NOT delayed, the hold is on what follows it (t={splashAt:0.000})");
                ctx.Check(sprayAt - splashAt >= 2.9f,
                    $"{held} waits out {flagged}'s authored 3.0 s choreography (gap={sprayAt - splashAt:0.000} s)");
                ctx.Check(sprayAt - splashAt <= 4.5f,
                    $"...and starts when the splash ENDS, not at some ceiling (gap={sprayAt - splashAt:0.000} s)");

                // The control: the unflagged calls ahead of it in the same sequence.
                foreach (var unflagged in new[] { "call_crash_trails", "large_10sec_fire" })
                {
                    if (startedAt.TryGetValue(unflagged, out float t))
                    {
                        ctx.Check(t <= 2f / 60f,
                            $"unflagged {unflagged} is not held (t={t:0.000}), null and 0 are different authored states");
                    }
                }

                ctx.Check(runtime.WaitsInstalled >= 1,
                    $"the runtime armed the hold rather than the gap coming from somewhere else (installed={runtime.WaitsInstalled})");
                ctx.Same(0, runtime.WaitsAbandoned,
                    $"no hold ended at the WaitCeilingS backstop instead of at its callee");
            },
                asCrashRig: true);
        });
    }

    // ---- what a host deactivation may and may not stop ------------------------------------------

    // An OBJECT_ACTIVE_STATE INACTIVE ends the emitters under that host, but not one that started in
    // the same instant. ⚠ Assert BOTH halves: dropping the stop entirely passes the splash half, and
    // shipping the stop unconditioned passes the debris half. They are the two populations the
    // install-wide census splits, and the split is total (analysis/bl-229-emitter-host-deactivation/).
    // SPLASH is plane_big_splash, whose emitter must survive its host's deactivation and still be gone
    // by ~0.7 s; DEBRIS is m_build01, whose deactivation is the trail's only authored stop.
    [Suite("emitter-host-deactivation",
        "a host going inactive spares the emitter that started in its own instant and still ends the one that did not (BL-229)")]
    internal static void EmitterHostDeactivation(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            SplashSurvivesItsOwnInstant(ctx, world);
            DebrisTrailStillEndsWithItsHost(ctx, world);
        });
    }

    internal static void SplashSurvivesItsOwnInstant(TestContext ctx, TestWorld world)
    {
        const string animName = "plane_big_splash";
        const string pufferName = "splasher";
        var program = world.Session.Program.Subset(animName);
        var defs = program.ByAnimName(animName);
        ctx.Check(defs.Count > 0, $"chapter program has {animName} defs={defs.Count}");
        if (defs.Count == 0)
        {
            return;
        }

        WithEmitterStage(ctx, program, "SplashStage",
            new[] { "huge_splash_model", "splash_polys", "sp_1", "ripple1", "ripple2", "ripple3" },
            (stage, runtime, fake) =>
        {
            runtime.Start(defs[0], stage);
            runtime.Advance(1f / 60f);

            ctx.Check(fake.Built.Any(e => e.Key == pufferName),
                $"{animName} reached the fake factory and built {pufferName}");
            ctx.Check(EmitterOn(runtime, pufferName, "sp_1") == true,
                $"{pufferName} survives the sp_1 deactivation it shares an instant with (BL-229)");

            for (int i = 0; i < 18; i++)   // 0.3 s, inside the callee's authored 0.5 s run
            {
                runtime.Advance(1f / 60f);
            }
            ctx.Check(EmitterOn(runtime, pufferName, "sp_1") == true,
                $"{pufferName} is still emitting 0.3 s in");

            for (int i = 0; i < 30; i++)   // out to 0.8 s, past the authored 0.5 + 0.1 s stop
            {
                runtime.Advance(1f / 60f);
            }
            ctx.Check(EmitterOn(runtime, pufferName, "sp_1") == false,
                $"{pufferName} ends on the run hg_splasher authors, not on its host (and its row is still known, so this is a pause, not a teardown)");
            var emitter = fake.Built.FirstOrDefault(e => e.Key == pufferName);
            ctx.Check(emitter is { Started: > 0 }, $"{pufferName} actually sustained particles");
        },
            asCrashRig: true);
    }

    internal static void DebrisTrailStillEndsWithItsHost(TestContext ctx, TestWorld world)
    {
        const string animName = "m_build01";
        const string pufferName = "trailpuffer3";
        const string host = "part3";
        const string offSequence = "sparkout3";   // where part3's own deactivation is authored
        var program = world.Session.Program.Subset(animName);
        var defs = program.ByAnimName(animName);
        ctx.Check(defs.Count > 0, $"chapter program has {animName} defs={defs.Count}");
        if (defs.Count == 0)
        {
            return;
        }

        // ⚠ Keep the fireball template roots on the stage. small_fireball declares a puffer also called
        // trailpuffer2, and with its own root missing the name resolution falls back to the call anchor,
        // so its stop lands on the building's key and ends the debris trail early, masking the assertion.
        var nodes = new List<string>
        {
            "m_bld_healthy", "m_bld_destroyed", "dbase", "flame_ball_01", "flame_ball_02",
        };
        for (int i = 1; i <= 9; i++)
        {
            nodes.Add($"part{i}");
        }
        WithEmitterStage(ctx, program, "DebrisStage", nodes, (stage, runtime, fake) =>
        {
            // Asserted against the DISPATCH MOMENT, never a fixed second: part3 is a bounce-solved launch, so
            // when it lands is computed rather than authored. The only other thing that could stop this trail,
            // the instance retiring, happens a second later, which a wall-clock check would blur.
            float clock = 0f;
            float deactivatedAt = -1f;
            float stoppedAt = -1f;
            bool everEmitted = false;
            runtime.OnEventDispatched = d =>
            {
                if (deactivatedAt < 0f && d.Sequence == offSequence && d.EventKind == "ObjectActiveState")
                {
                    deactivatedAt = clock;
                }
            };
            runtime.Start(defs[0], stage);
            for (int i = 0; i < 300; i++)   // 5 s, past the landing and past the instance's own end
            {
                clock += 1f / 60f;
                runtime.Advance(1f / 60f);
                bool? on = EmitterOn(runtime, pufferName, host);
                everEmitted |= on == true;
                if (everEmitted && stoppedAt < 0f && on == false)
                {
                    stoppedAt = clock;
                }
            }
            runtime.OnEventDispatched = null;

            ctx.Check(fake.Built.Any(e => e.Key == pufferName), $"{animName}'s death built {pufferName}");
            ctx.Check(everEmitted, $"{pufferName} trails {host} while it flies");
            ctx.Check(deactivatedAt > 0f, $"{offSequence} switched {host} off t={deactivatedAt:0.000}");
            ctx.Check(stoppedAt > 0f, $"{pufferName} stopped within the 5 s window t={stoppedAt:0.000}");
            ctx.Check(stoppedAt > 0f && deactivatedAt > 0f && Mathf.Abs(stoppedAt - deactivatedAt) <= 2f / 60f,
                $"{pufferName} ends on {host}'s own deactivation frame, not later, BL-224's stop is dated, not dropped (off={deactivatedAt:0.000} stop={stoppedAt:0.000})");
        });
    }

    // Is the emitter `name` ON `host` emitting? Null when
    // no such emitter is known. Host-qualified on purpose: puffer names are NOT unique across
    // definitions, `small_fireball` declares a `trailpuffer2` of its own, and a name-only read
    // answers about whichever row comes first, which lets a debris assertion pass against a
    // runtime with the stop deleted outright.
    internal static bool? EmitterOn(AnimRuntime runtime, string name, string host)
    {
        foreach (var row in runtime.Emitters.Census)
        {
            if (row.Name == name && row.Host == host)
            {
                return row.Emitting;
            }
        }
        return null;
    }

    // A bare stage carrying the nodes a definition names, plus a runtime bound to it
    // through a CountingEmitterFactory. Flat children, never a hierarchy: the point is
    // to give each named host its own subtree, so a stop that reaches the wrong one is visible
    // rather than being absorbed by a shared ancestor.
    internal static void WithEmitterStage(TestContext ctx, AnimProgram program, string stageName,
        IEnumerable<string> nodeNames,
        System.Action<Node3D, AnimRuntime, CountingEmitterFactory> body,
        bool asCrashRig = false)
    {
        var stage = new Node3D { Name = stageName };
        foreach (var name in nodeNames)
        {
            stage.AddChild(new Node3D { Name = name });
        }
        var fake = new CountingEmitterFactory();
        // The two role flags AnimRuntime.ForCrashRig sets, for a def the crash rig is the only
        // production caller of: the splash is played by the per-player rig, which relocates its own
        // called templates and holds no ExternalEffect, so the start and the stop meet on ONE director.
        var runtime = new AnimRuntime(AnimRuntime.NewTemplateStage(placesCalled: asCrashRig))
        {
            AutoStart = false,
            ManualAdvance = true,
            SoundHandledElsewhere = true,
            EmitterFactory = fake,
            NameResolveFallback = asCrashRig,
        };
        ctx.Host.AddChild(stage);
        ctx.Host.AddChild(runtime);
        try
        {
            runtime.Bind(stage, program);
            body(stage, runtime, fake);
        }
        finally
        {
            runtime.Free();
            stage.Free();
        }
    }

    // ---- the template MESH half renders at the call site ----------------------------------------

    // An effect's template MESHES must be visible at the call site while it plays and dark once it is
    // over. The world-effects stage keeps every template root hidden and the engine reveals the one a
    // call lands on (TemplateStage.Shown), so both halves are engine rules.
    // ⚠ Assert both: revealing and never hiding leaves a mesh burning at the last hit point for the
    // session, while hiding eagerly or never revealing shows nothing at all. The CALLED case is
    // he_ground_effect's staged he_ring1; the ENDED case is 3040ap_gunhit's stop-retired chunk mesh.
    [Suite("effect-template-mesh",
        "an effect's template meshes show at the call site (including a CALLED template's) and go dark when it ends (BL-061)")]
    internal static void EffectTemplateMesh(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            CalledTemplateShowsItsMesh(ctx, world);
            EndedEffectLeavesNoMeshLit(ctx, world);
        });
    }

    internal static void CalledTemplateShowsItsMesh(TestContext ctx, TestWorld world)
    {
        EffectStageSuiteHelper.WithEffectStage(ctx, world, "he_ground_effect", new[] { "he_ring", "he_ring1", "he_trails" },
            (stage, runtime, point) =>
        {
            ctx.Check(Probes.MeshCensus.VisibleMeshes(stage) == 0,
                $"the staged templates start hidden ({Probes.MeshCensus.VisibleMeshes(stage)} visible)");
            runtime.PlayEffectAt("he_ground_effect", point);
            int peak = 0;
            for (int i = 0; i < 30; i++)
            {
                runtime.Advance(1f / 60f);
                peak = Mathf.Max(peak, Probes.MeshCensus.VisibleMeshes(stage));
            }

            ctx.Check(Probes.MeshCensus.VisibleMeshesUnder(stage, "he_ring") > 0,
                $"he_ground_effect's own template mesh (he_ring) is visible, the PlayEffectAt half ({Probes.MeshCensus.VisibleMeshesUnder(stage, "he_ring")})");
            ctx.Check(Probes.MeshCensus.VisibleMeshesUnder(stage, "he_ring1") > 0,
                $"the CALLED template's mesh (he_ring1, the upper ring) is visible too, BL-061 ({Probes.MeshCensus.VisibleMeshesUnder(stage, "he_ring1")})");
            ctx.Check(peak >= 2, $"both rings drew in the same window (peak {peak} mesh(es))");
        });
    }

    internal static void EndedEffectLeavesNoMeshLit(TestContext ctx, TestWorld world)
    {
        EffectStageSuiteHelper.WithEffectStage(ctx, world, "3040ap_gunhit", new[] { "dum_gunhit" }, (stage, runtime, point) =>
        {
            runtime.PlayEffectAt("3040ap_gunhit", point, null, 0.3f);
            runtime.Advance(1f / 60f);
            ctx.Check(Probes.MeshCensus.VisibleMeshesUnder(stage, "dum_gunhit") > 0,
                $"the ap gun hit's chunk mesh is visible while it plays ({Probes.MeshCensus.VisibleMeshesUnder(stage, "dum_gunhit")})");

            // Past the def's own authored ACTIVE_STATE 0 at +0.1 s, which ends the instance well
            // inside the 0.3 s TTL, the case that would otherwise leave the mesh lit for the session.
            for (int i = 0; i < 30; i++)
            {
                runtime.Advance(1f / 60f);
            }

            ctx.Check(Probes.MeshCensus.VisibleMeshesUnder(stage, "dum_gunhit") == 0,
                $"and is dark once the effect has ended, without waiting for its TTL, BL-061 ({Probes.MeshCensus.VisibleMeshesUnder(stage, "dum_gunhit")} still lit)");
        });
    }

    // ---- the full-screen wash reports its authored run times ------------------------------------

    // he_ground_effect's frame_buffer_effects1 is six FBFX_COLOR_FROM_TO steps washing the picture over
    // 1.2 s, reached through an If PlayerRange call. The handler must report each step's authored
    // run_time as its duration, because that is the only thing spacing them: report 0 and all six fire
    // in one instant. It then asserts the routing, that each step reports where the burst was and the
    // def's own gate, and that the gate answers to the NEAREST human rather than to one camera.
    // Full inventory: this module's docs/architecture.md entry.
    [Suite("fbfx-flash",
        "he_ground_effect's six-step full-screen wash reports its authored run times, so the 1.2 s ramp does not collapse into one instant; the ramp routes by pane proximity and the victim-routed blend wash by player index, composited over it")]
    internal static void FbfxFlash(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            EffectStageSuiteHelper.WithEffectStage(ctx, world, "he_ground_effect", new[] { "he_ring", "he_ring1", "he_trails" },
                (stage, runtime, point) =>
            {
                // The authored chain, from extracted/C1/cam_anim/he_ring-he_ground_effect.json.
                var white = new Color(1f, 1f, 1f, 0.3f);
                var violet = new Color(0.2f, 0f, 1f, 0.2f);
                var wantFrom = new[] { white, violet, violet, white, violet, violet };
                var wantTo = new[] { violet, violet, white, violet, violet, white };
                var wantRun = new[] { 0.2f, 0.4f, 0.2f, 0.1f, 0.2f, 0.1f };

                const float dt = 1f / 60f;
                float clock = 0f;
                var fired = new List<(float T, Color From, Color To, float Run, Vector3 At, float GateSq)>();
                runtime.ScreenFlash = (from, to, seconds, at, gateSq) =>
                    fired.Add((clock, from, to, seconds, at, gateSq));

                // At the camera, so the def's own PLAYER_RANGE 10000 gate passes.
                runtime.PlayEffectAt("he_ground_effect", point);
                for (int i = 0; i < 150; i++)
                {
                    clock += dt;
                    runtime.Advance(dt);
                }

                string times = string.Join(", ", fired.Select(f => $"{f.T:0.####}s (run {f.Run:0.##})"));
                ctx.Note($"the wash fired at {times}");
                ctx.Check(fired.Count == 6, $"the six FBFX_COLOR_FROM_TO steps all fired ({fired.Count})");
                if (fired.Count != 6)
                    return;
                for (int i = 0; i < 6; i++)
                {
                    ctx.Check(Mathf.IsEqualApprox(fired[i].Run, wantRun[i]),
                        $"step {i + 1} reports its authored run time ({fired[i].Run:0.###} s, want {wantRun[i]:0.###})");
                    ctx.Check(fired[i].From.IsEqualApprox(wantFrom[i]) && fired[i].To.IsEqualApprox(wantTo[i]),
                        $"step {i + 1} ramps its authored colours ({fired[i].From} → {fired[i].To})");
                }
                // Each step must start one previous run time after the one before it, the
                // collapse this suite exists to catch, which no per-step assertion above can see.
                for (int i = 1; i < 6; i++)
                {
                    float gap = fired[i].T - fired[i - 1].T;
                    ctx.Check(Mathf.Abs(gap - wantRun[i - 1]) <= 2f * dt,
                        $"step {i + 1} waits step {i}'s run time ({gap:0.###} s, want {wantRun[i - 1]:0.###})");
                }
                // One step of headroom per gap: an authored run time is an exact multiple of the step here, but
                // neither it nor the accumulated clock is exact in binary float and the misses do not cancel.
                // Measured: three of the five gaps land one step late, 0.05 s over the chain.
                ctx.Check(Mathf.Abs((fired[5].T - fired[0].T) - 1.1f) <= 5f * dt,
                    $"the chain spans its authored 1.1 s first-to-last fire ({fired[5].T - fired[0].T:0.###} s)");

                // The routing half at the source: every step carries the burst point and the def's OWN gate, which
                // is what lets the overlay pick panes. 10000 is metres squared, the compiled PLAYER_RANGE
                // convention, and all 24 shipped wash defs author exactly that one gate.
                ctx.Check(fired.All(f => Mathf.IsEqualApprox(f.GateSq, 10000f)),
                    $"every step reports the def's authored PlayerRange gate ({fired[0].GateSq:0.#} m², want 10000 = 100 m)");
                float drift = fired.Max(f => f.At.DistanceTo(point));
                ctx.Note($"the wash routes from {fired[0].At} on the def's own {fired[0].GateSq:0.#} m² gate ({Mathf.Sqrt(fired[0].GateSq):0.#} m), {drift:0.###} m off the play point");
                ctx.Check(drift <= 1f,
                    $"every step reports the burst's own world point ({drift:0.###} m from where it was played)");
            });
        });
        WashPaintsOnlyThePanesItReached(ctx);
        BlendWashRoutesToTheVictimsPane(ctx);
        PlayerRangeNearestHuman(ctx);
    }

    // The victim-routed blend channel (D13): a wash addressed to player 2 paints pane 2 and leaves
    // pane 1 untouched, whatever the cameras are doing; it composites OVER a proximity ramp already
    // running in the pane and leaves that ramp's own picture unchanged where no wash is running.
    // METHOD-12: the ramp readouts are the invariant, the blended pane is what moves.
    internal static void BlendWashRoutesToTheVictimsPane(TestContext ctx)
    {
        const float gate = 10000f;
        var clear = new Color(0f, 0f, 0f, 0f);
        var white = new Color(1f, 1f, 1f, 0.3f);
        var violet = new Color(0.2f, 0f, 1f, 0.2f);
        var red = new Color(1f, 0f, 0f);

        // Both cameras at one point: the ramp's proximity gate cannot tell the panes apart, so any
        // difference between them below is the blend channel's routing alone.
        var p1 = SuiteViewers.Camera(ctx, Vector3.Zero);
        var p2 = SuiteViewers.Camera(ctx, Vector3.Zero);
        var viewers = new ViewerSet();
        viewers.Bind(new[] { p1, p2 });
        var (flash, panes) = PaneFlash(ctx, viewers);
        try
        {
            const float dt = 1f / 60f;
            // A wash addressed to player 2 (pane index 1), stepped through its 0.15 × 4 s attack.
            flash.PlayBlend(1, red, 1f, 4f);
            for (int i = 0; i < 40; i++)
                flash.Advance(dt);
            var pane2 = flash.CurrentFor(1);
            ctx.Check(pane2.A > 0.99f && pane2.R > 0.99f && pane2.G < 0.01f,
                $"a blend wash addressed to player 2 paints pane 2 red at its full weight after the attack ({pane2})");
            ctx.Check(flash.CurrentFor(0).IsEqualApprox(clear),
                $"and pane 1, whose camera stands at the same point, stays clear, routed by victim, not by proximity ({flash.CurrentFor(0)})");
            ctx.Check(!flash.RunningFor(1),
                $"and starts no RAMP in pane 2: the two channels are separate states ({flash.RunningFor(1)})");

            // A proximity ramp reaching both panes: pane 1 shows the ramp alone, pane 2 the wash
            // over the ramp, the pixel the ramp would have painted, with red laid over it.
            flash.Play(white, violet, 0.2f, Vector3.Zero, gate);
            ctx.Check(flash.CurrentFor(0).IsEqualApprox(white),
                $"an HE ramp reaching both panes paints pane 1 exactly as before the blend channel existed ({flash.CurrentFor(0)})");
            var composite = flash.CurrentFor(1);
            var want = BlendWash.Composite(white, red, flash.BlendFor(1)!.Weight);
            ctx.Check(composite.IsEqualApprox(want),
                $"and pane 2 shows the wash composited over that ramp ({composite}, want {want})");
            ctx.Check(flash.RunningFor(0) && flash.RunningFor(1),
                $"while the ramp itself runs in both panes, its routing untouched by the wash ({flash.RunningFor(0)}/{flash.RunningFor(1)})");

            // The wash ends at its duration and pane 2 falls back to whatever the ramp channel has,
            // which by then is nothing.
            for (int i = 0; i < 260; i++)
                flash.Advance(dt);
            ctx.Check(flash.CurrentFor(1).IsEqualApprox(clear) && flash.BlendFor(1)!.Running == false,
                $"the wash is gone at its 4 s duration and pane 2 reads clear again ({flash.CurrentFor(1)})");

            // A victim with no pane (an AI's player index) addresses nothing and throws nothing.
            flash.PlayBlend(FlightRoster.ShooterIdBase, red, 1f, 4f);
            flash.Advance(dt);
            ctx.Check(flash.CurrentFor(0).IsEqualApprox(clear) && flash.CurrentFor(1).IsEqualApprox(clear),
                $"a wash addressed to an AI's player index paints no pane ({flash.CurrentFor(0)} / {flash.CurrentFor(1)})");
        }
        finally
        {
            flash.Free();
            foreach (var pane in panes)
                pane.Free();
            p2.Free();
            p1.Free();
        }
    }

    // The wash reaches the panes the burst reached and no others: two panes 120 m apart under the
    // authored 100 m gate, so one pane, the other pane, and both are each reachable by moving the burst.
    // ⚠ The wash paints every player inside the burst's own authored radius, not just a hit or nearest
    // one. That is the original's rule read literally, asked once per player here, and the two
    // ground-effect defs carrying it play on terrain impacts with no hit aircraft to route to at all.
    // Ramp state is per pane; within a pane it still replaces (docs/org/sequences.md).
    internal static void WashPaintsOnlyThePanesItReached(TestContext ctx)
    {
        // The gate every shipped wash def authors: metres SQUARED in the compiled convention.
        const float gate = 10000f;
        var clear = new Color(0f, 0f, 0f, 0f);
        var white = new Color(1f, 1f, 1f, 0.3f);
        var violet = new Color(0.2f, 0f, 1f, 0.2f);
        var green = new Color(0f, 1f, 0f, 0.5f);

        var p1 = SuiteViewers.Camera(ctx, Vector3.Zero);
        var p2 = SuiteViewers.Camera(ctx, new Vector3(0f, 0f, 120f));
        var viewers = new ViewerSet();
        viewers.Bind(new[] { p1, p2 });
        var (flash, panes) = PaneFlash(ctx, viewers);
        var (blind, blindPanes) = PaneFlash(ctx, null);
        try
        {
            ctx.Check(flash.PaneCount == 2, $"the overlay built one ramp per pane ({flash.PaneCount})");

            // 50 m ahead of P1, 170 m from P2: inside the gate for one of them only.
            flash.Play(white, violet, 0.2f, new Vector3(0f, 0f, -50f), gate);
            ctx.Check(flash.RunningFor(0) && flash.CurrentFor(0).IsEqualApprox(white),
                $"a burst 50 m from P1 washes P1's pane ({flash.CurrentFor(0)})");
            ctx.Check(!flash.RunningFor(1) && flash.CurrentFor(1).IsEqualApprox(clear),
                $"and leaves P2's pane, 170 m away, clear, the BL-340 report ({flash.CurrentFor(1)})");

            // 50 m past P2, 170 m from P1, the same case from the other side, while P1's own ramp
            // is still running: two panes, two independent states.
            flash.Play(violet, white, 0.2f, new Vector3(0f, 0f, 170f), gate);
            ctx.Check(flash.RunningFor(1) && flash.CurrentFor(1).IsEqualApprox(violet),
                $"a second burst 50 m from P2 washes P2's pane ({flash.CurrentFor(1)})");
            ctx.Check(flash.CurrentFor(0).IsEqualApprox(white),
                $"without touching the ramp P1 is already watching ({flash.CurrentFor(0)}), the state is per pane");

            // Between them: 60 m from each, so BOTH are inside the burst's own radius.
            flash.Play(green, white, 0.2f, new Vector3(0f, 0f, 60f), gate);
            ctx.Check(flash.CurrentFor(0).IsEqualApprox(green) && flash.CurrentFor(1).IsEqualApprox(green),
                $"a burst 60 m from both washes both panes, every player inside the radius, not just the nearest ({flash.CurrentFor(0)} / {flash.CurrentFor(1)})");
            ctx.Check(flash.RunningFor(0) && flash.RunningFor(1),
                $"and replaces what each pane was running rather than compositing with it ({flash.RunningFor(0)}/{flash.RunningFor(1)})");

            // An ungated def (the intro cutscene's gi_scene1 authors no PlayerRange) is not a
            // proximity effect at all, so it still paints everything.
            flash.Play(white, violet, 0.2f, new Vector3(0f, 0f, -5000f), 0f);
            ctx.Check(flash.CurrentFor(0).IsEqualApprox(white) && flash.CurrentFor(1).IsEqualApprox(white),
                $"an UNGATED wash 5 km out still paints every pane ({flash.CurrentFor(0)} / {flash.CurrentFor(1)})");

            // The floor: the def's gate already fired, so something was near it. If no pane's own
            // camera agrees, the nearest pane still gets it rather than the burst washing nobody.
            flash.Play(violet, green, 0.2f, new Vector3(0f, 0f, -5000f), gate);
            ctx.Check(flash.CurrentFor(0).IsEqualApprox(violet) && flash.CurrentFor(1).IsEqualApprox(white),
                $"a gated wash no pane is in range of falls to the nearest pane alone ({flash.CurrentFor(0)} / {flash.CurrentFor(1)})");

            blind.Play(white, violet, 0.2f, new Vector3(0f, 0f, -50f), gate);
            ctx.Check(blind.CurrentFor(0).IsEqualApprox(white) && blind.CurrentFor(1).IsEqualApprox(white),
                $"ABLE-TO-FAIL CONTROL: the same burst with no viewer set bound paints both panes, which is what this did before the routing existed ({blind.CurrentFor(1)})");
        }
        finally
        {
            blind.Free();
            foreach (var pane in blindPanes)
                pane.Free();
            flash.Free();
            foreach (var pane in panes)
                pane.Free();
            p2.Free();
            p1.Free();
        }
    }

    // The wash's own gate, `If PlayerRange 10000`, answers to the NEAREST human, not
    // one camera: a burst still fires while the camera this stage was built
    // against sits 5 km off, as long as SOME entry in `PlayerPositions` is inside the 100 m
    // gate. This is upstream of B12's routing (which panes a fired wash reaches), here nothing
    // has fired yet, so no pane would have anything to route.
    internal static void PlayerRangeNearestHuman(TestContext ctx)
    {
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            EffectStageSuiteHelper.WithEffectStage(ctx, world, "he_ground_effect", new[] { "he_ring", "he_ring1", "he_trails" },
                (stage, runtime, point) =>
            {
                const float dt = 1f / 60f;
                var fired = new List<float>();
                runtime.ScreenFlash = (from, to, seconds, at, gateSq) => fired.Add(seconds);

                // Every known human 5 km out: nowhere near the def's own 100 m gate. 150 steps
                // (2.5 s) is the same margin FbfxFlash drives the full chain for above, long
                // enough that a gate wrongly left open would have fired well within it.
                runtime.PlayerPositions = () => new[] { point + new Vector3(0f, 0f, -5000f) };
                runtime.PlayEffectAt("he_ground_effect", point);
                for (int i = 0; i < 150; i++)
                    runtime.Advance(dt);
                ctx.Check(fired.Count == 0,
                    $"the burst's own PLAYER_RANGE gate stays closed while every PlayerPositions entry is 5 km off ({fired.Count} fired)");

                // A second human standing at the burst: the NEAREST of the two is now in range,
                // and the def's gate is asked against that one, not the far singleton.
                runtime.PlayerPositions = () => new[] { point + new Vector3(0f, 0f, -5000f), point };
                runtime.PlayEffectAt("he_ground_effect", point);
                for (int i = 0; i < 150; i++)
                    runtime.Advance(dt);
                ctx.Check(fired.Count == 6,
                    $"the same def fires its six-step wash once the NEAREST PlayerPositions entry stands at the burst ({fired.Count})");
            });
        });
    }

    // A two-pane ScreenFlash over bare HUD parents, the shape
    // `GameSession` builds from the rigs, with nothing but the parents and the viewer set,
    // since that is all the routing reads.
    internal static (ScreenFlash Flash, Node[] Panes) PaneFlash(TestContext ctx, ViewerSet? viewers)
    {
        var panes = new[] { new Node { Name = "pane1_hud" }, new Node { Name = "pane2_hud" } };
        foreach (var pane in panes)
            ctx.Host.AddChild(pane);
        var flash = ScreenFlash.Build(panes, viewers);
        ctx.Host.AddChild(flash);
        return (flash, panes);
    }

    // ---- three ordnance bursts, played end to end against their authored timelines -------------

    // Plays he_ground_effect, flash_effect and sonic_ground_effect end to end on a fixed-dt clock and
    // matches each one's FULL event timeline against the authored JSON. Nothing here is a membership
    // check, which would pass a broken scheduler. Inventory: this module's docs/architecture.md entry
    // and docs/org/sequences.md.
    // ⚠ Derive the staged roots (EffectCatalogue.StageRootsFor), never hand-list them; a def whose
    // anchor root was not staged plays nothing, silently. ⚠ Do not inherit --effects-test's 0.3 s TTL.
    [Suite("ordnance-burst-timeline",
        "the HE, flash and sonic bursts play end to end and every sequence's whole event timeline matches the authored JSON, in order, at the authored time (D31)")]
    internal static void OrdnanceBurstTimeline(TestContext ctx)
    {
        var report = new System.Text.StringBuilder();
        ctx.WithWorld(ctx.Chapter, collision: false, world =>
        {
            HeBurstTimeline(ctx, world, report);
            FlashBurstTimeline(ctx, world, report);
            SonicBurstTimeline(ctx, world, report);
        });
        ctx.WriteArtifact("ordnance-burst-timeline.txt", report.ToString());
    }

    // `he_ring-he_ground_effect.json`, five sequences, two of them unnamed and Initial,
    // three ON_CALL, plus `flame_ball_01-large_fireball.json`, which its fourth CALL_ANIMATION
    // reaches and which carries the parked-stopper case.
    internal static void HeBurstTimeline(TestContext ctx, TestWorld world, System.Text.StringBuilder report)
    {
        // The first unnamed Initial sequence: eight events, none carrying a start, so the whole
        // burst fires in the instant the effect starts.
        var opening = new BurstLane("", new[]
        {
            new BurstStep(0, "CallSequence", "he_light_seq", 0f),
            new BurstStep(1, "CallAnimation", "call_he_ring", 0f),
            new BurstStep(2, "CallAnimation", "call_hetrails_up", 0f),
            new BurstStep(3, "CallAnimation", "large_fireball", 0f),
            new BurstStep(4, "CallAnimation", "call_he_ring1", 0f),
            new BurstStep(5, "Sound", "ground_mixed_exp_sg", 0f),
            new BurstStep(6, "CallAnimation", "call_hetrails_up", 0f),
            new BurstStep(7, "CallSequence", "he_flashes", 0f),
        });
        // The second unnamed Initial sequence. The IF and the ENDIF are control flow the runner interprets
        // itself and never dispatches, so #1 is the only row this lane can produce, and it produces it only
        // because the burst is played at the camera, which is what makes the range condition true.
        var fbfxGate = new BurstLane("", new[]
        {
            new BurstStep(1, "CallSequence", "frame_buffer_effects1", 0f),
        });
        // he_light_seq: LIGHT_STATE on, then six LIGHT_ANIMATION ramps whose run times chain
        // (0.05, 0.025, 0.05, 0.025 → 0.15), one `Event + 0.2` gap, then 0.05 and 0.01, then off.
        var lightSeq = new BurstLane("he_light_seq", new[]
        {
            new BurstStep(0, "LightState", "he_light", 0f),
            new BurstStep(1, "LightAnimation", "he_light", 0f),
            new BurstStep(2, "LightAnimation", "he_light", 0.05f),
            new BurstStep(3, "LightAnimation", "he_light", 0.075f),
            new BurstStep(4, "LightAnimation", "he_light", 0.125f),
            new BurstStep(5, "LightAnimation", "he_light", 0.35f),
            new BurstStep(6, "LightAnimation", "he_light", 0.4f),
            new BurstStep(7, "LightState", "he_light", 0.41f),
        });
        var flashes = new BurstLane("he_flashes", new[]
        {
            new BurstStep(0, "LightState", "he_light1", 0f),
            new BurstStep(1, "LightAnimation", "he_light1", 0f),
            new BurstStep(2, "LightAnimation", "he_light1", 0.1f),
            new BurstStep(3, "LightState", "he_light1", 0.6f),
        });
        // The wash: six FBFX_COLOR_FROM_TO steps. A handler reporting 0 as its duration fires all six in
        // one instant, which is what the times here refuse. fbfx-flash asserts the colours and run times;
        // this asserts their place in the burst.
        var wash = new BurstLane("frame_buffer_effects1", new[]
        {
            new BurstStep(0, "FbfxColorFromTo", null, 0f),
            new BurstStep(1, "FbfxColorFromTo", null, 0.2f),
            new BurstStep(2, "FbfxColorFromTo", null, 0.6f),
            new BurstStep(3, "FbfxColorFromTo", null, 0.8f),
            new BurstStep(4, "FbfxColorFromTo", null, 0.9f),
            new BurstStep(5, "FbfxColorFromTo", null, 1.1f),
        });
        // The callee. `activate_puffer` shows the fireball, calls `p1trail` (which starts the
        // emitter) and then, at an authored `Event + 0.3`, STOPs `stop_p1trail`, an ON_CALL
        // sequence nothing has called, so it is parked and the stop halts nothing.
        var fireball = new[]
        {
            new BurstLane("activate_puffer", new[]
            {
                new BurstStep(0, "ObjectActiveState", "flame_ball_01", 0f),
                new BurstStep(1, "CallSequence", "p1trail", 0f),
                new BurstStep(2, "StopSequence", "stop_p1trail", 0.3f),
            }),
            new BurstLane("p1trail", new[]
            {
                new BurstStep(0, "PufferState", "fierypuffer", 0f),
            }),
        };

        WithBurst(ctx, world, "he_ground_effect", report, fired =>
        {
            CheckLanes(ctx, "he_ground_effect", fired, new[] { opening, fbfxGate, lightSeq, flashes, wash }, report);
            CheckLanes(ctx, "large_fireball", fired, fireball, report);
            // The parked stopper, and the reason `large_fireball` is asserted here at all. Its
            // `p1trail` lane above is the live control: without it, "the stopper fired nothing" is also what a
            // fireball that never started reports.
            int stopper = fired.Count(f => f.Anim == "large_fireball" && f.Sequence == "stop_p1trail");
            ctx.Check(stopper == 0,
                $"large_fireball's parked stop_p1trail dispatched nothing, a STOP_SEQUENCE halts and never starts (B12) ({stopper} event(s))");
        });
    }

    // `flash_control-flash_effect.json`, the pure light case, two sequences. The one
    // timed event in it is a `START_TIME ANIMATION 1.5`, read against the instance clock.
    internal static void FlashBurstTimeline(TestContext ctx, TestWorld world, System.Text.StringBuilder report)
    {
        var lanes = new[]
        {
            new BurstLane("", new[]
            {
                new BurstStep(0, "ObjectActiveState", "lens_flash", 0f),
                new BurstStep(1, "CallSequence", "flash_flashes", 0f),
                new BurstStep(2, "CallAnimation", "call_flasher", 0f),
                // `Animation + 1.5`, the instance clock, which for this Initial sequence is also
                // its own, so the value is the assertion and the origin is not (sonic's second
                // `sonic_light_seq` pass is where the two clocks differ).
                new BurstStep(3, "ObjectActiveState", "lens_flash", 1.5f),
            }),
            new BurstLane("flash_flashes", new[]
            {
                new BurstStep(0, "LightState", "flash_light1", 0f),
                new BurstStep(1, "LightAnimation", "flash_light1", 0f),
                new BurstStep(2, "LightAnimation", "flash_light1", 0.5f),
                new BurstStep(3, "LightState", "flash_light1", 0.75f),
            }),
        };
        WithBurst(ctx, world, "flash_effect", report,
            fired => CheckLanes(ctx, "flash_effect", fired, lanes, report));
    }

    // `sonic_effect-sonic_ground_effect.json`, four sequences, two unnamed and Initial.
    // The repeat-call case: the first Initial sequence calls `sonic_light_seq` at #0 and again at #4,
    // 1.2 s later.
    internal static void SonicBurstTimeline(TestContext ctx, TestWorld world, System.Text.StringBuilder report)
    {
        // The 15-event Initial sequence. #3 carries `START_TIME ANIMATION 1.2`; everything behind
        // it is untimed, so the whole second half fires in that instant.
        var main = new BurstLane("", new[]
        {
            new BurstStep(0, "CallSequence", "sonic_light_seq", 0f),
            new BurstStep(1, "CallAnimation", "sonic_puff1", 0f),
            new BurstStep(2, "CallAnimation", "call_flare", 0f),
            new BurstStep(3, "CallAnimation", "sonic_puff4", 1.2f),
            new BurstStep(4, "CallSequence", "sonic_light_seq", 1.2f),
            new BurstStep(5, "CallSequence", "sonic_growlight", 1.2f),
            new BurstStep(6, "CallAnimation", "call_flare", 1.2f),
            new BurstStep(7, "CallAnimation", "sonic_puff5", 1.2f),
            new BurstStep(8, "CallAnimation", "sonic_puff6", 1.2f),
            new BurstStep(9, "CallAnimation", "sonic_puff7", 1.2f),
            new BurstStep(10, "CallAnimation", "sonic_puff8", 1.2f),
            new BurstStep(11, "CallAnimation", "sonic_puff9", 1.2f),
            new BurstStep(12, "CallAnimation", "sonic_puff10", 1.2f),
            new BurstStep(13, "CallAnimation", "sonic_puff11", 1.2f),
            new BurstStep(14, "CallAnimation", "sonic_emit_downer", 1.2f),
        });
        // The second Initial sequence: four rising rings at once, then one at `START_TIME
        // SEQUENCE 1.2`, an absolute gate against this sequence's own clock, not the instance's.
        var rings = new BurstLane("", new[]
        {
            new BurstStep(0, "CallAnimation", "ring_up1", 0f),
            new BurstStep(1, "CallAnimation", "ring_up2", 0f),
            new BurstStep(2, "CallAnimation", "ring_up3", 0f),
            new BurstStep(3, "CallAnimation", "ring_up4", 0f),
            new BurstStep(4, "CallAnimation", "ring_down1", 1.2f),
        });
        // Two passes of ONE sequence, 1.2 s apart. The first must have ended for the second call to find it
        // parked and restart it: a call into a running sequence is a no-op, and a second concurrent copy is
        // not a thing the original can express.
        BurstLane LightPass(float from) => new("sonic_light_seq", new[]
        {
            new BurstStep(0, "LightState", "sonic_light", from),
            new BurstStep(1, "LightAnimation", "sonic_light", from),
            new BurstStep(2, "LightState", "sonic_light", from + 0.3f),
        });
        var grow = new BurstLane("sonic_growlight", new[]
        {
            new BurstStep(0, "LightState", "sonic_light1", 1.2f),
            new BurstStep(1, "LightAnimation", "sonic_light1", 1.2f),
            new BurstStep(2, "LightAnimation", "sonic_light1", 2.4f),
            new BurstStep(3, "LightState", "sonic_light1", 3.2f),
        });
        WithBurst(ctx, world, "sonic_ground_effect", report, fired =>
        {
            CheckLanes(ctx, "sonic_ground_effect", fired,
                new[] { main, rings, LightPass(0f), LightPass(1.2f), grow }, report);
            // Stated on its own as well as through the lanes: the lane pair above proves the two
            // passes ran, and this proves nothing else did. A third pass would be a call that
            // found the sequence parked when the original would not have.
            int passes = fired.Count(f => f.Anim == "sonic_ground_effect"
                                          && f.Sequence == "sonic_light_seq" && f.Index == 0);
            ctx.Same(2, passes, $"sonic_light_seq's two authored calls started it exactly twice (B11)");
        });
    }

    // Plays one burst on its own miniature world-effects stage and hands the recorded dispatch log to
    // body. The stage's template ROOTS are derived from the definition's own CALL_ANIMATION closure
    // against the chapter gamez, the same derivation the production bind runs, so a definition whose
    // anchor resolves nowhere throws here, naming it, instead of quietly playing nothing. Everything
    // else is the production world-effects role, with the camera as the player position.
    internal static void WithBurst(TestContext ctx, TestWorld world, string animName,
        System.Text.StringBuilder report, System.Action<IReadOnlyList<BurstFire>> body)
    {
        var roots = Flight.Airframe.EffectCatalogue.StageRootsFor(world.Session.Program, new[] { animName },
            Session.World.WorldEffectsFactory.StageRootResolver(world.Gamez));
        ctx.Check(roots.Count > 0,
            $"{animName}: its call closure's anchor roots derived ({roots.Count}: {string.Join(", ", roots)})");
        var stage = new Node3D { Name = $"BurstStage_{animName}" };
        var pool = new Node3D { Name = "pool0" };
        pool.SetMeta(AnimRuntime.PoolSlotMeta, 0);
        stage.AddChild(pool);
        int built = Session.World.WorldEffectsFactory.BuildEffectStage(world.Gamez,
            world.Session.Builder.Scene, pool, roots);
        ctx.Same(roots.Count, built, $"{animName}: template roots staged from the chapter gamez");
        foreach (var child in pool.GetChildren())
        {
            if (child is Node3D root)
            {
                root.Visible = false;
            }
        }

        var runtime = AnimRuntime.ForEffects(
            AnimRuntime.NewTemplateStage(pooled: true, shown: true, placesCalled: true),
            1, new CountingEmitterFactory(), false, BurstTtl,
            () => ctx.Camera.GlobalPosition);
        runtime.ManualAdvance = true;
        ctx.Host.AddChild(stage);
        ctx.Host.AddChild(runtime);
        try
        {
            runtime.Bind(stage, world.Session.Program.Subset(animName));
            var fired = new List<BurstFire>();
            float clock = 0f;
            runtime.OnEventDispatched = d => fired.Add(new BurstFire(clock,
                d.Def.AnimName ?? d.Def.Name, d.Sequence, d.EventIndex, d.EventKind, d.EventName));
            // At the camera, so the definitions' own PLAYER_RANGE gates pass.
            ctx.Check(runtime.PlayEffectAt(animName, ctx.Camera.GlobalPosition),
                $"{animName} resolved to a definition and started");
            int steps = Mathf.RoundToInt(BurstSeconds / BurstDt);
            for (int i = 0; i < steps; i++)
            {
                clock += BurstDt;
                runtime.Advance(BurstDt);
            }

            runtime.OnEventDispatched = null;
            runtime.UnhandledEventCounts.TryGetValue("PufferState(no host node)", out int hostless);
            ctx.Same(0, hostless, $"{animName}: PUFFER_STATE events that found no host node");
            ctx.Note($"{animName}: {fired.Count} dispatch(es) over {BurstSeconds:0.#} s at {BurstDt:0.####} s steps");
            body(fired);
        }
        finally
        {
            runtime.Free();
            stage.Free();
        }
    }

    // Matches a definition's recorded dispatches against its authored lanes and asserts both halves of
    // "the timeline is right": ORDER, each lane's rows arriving in the sequence's own order with
    // nothing unauthored arriving, and TIME, each row landing on its authored instant within
    // BurstSlack. A row is claimed by the first lane whose next unconsumed step it matches on sequence,
    // index, kind and name, so a row that arrives early or twice is reported stray. The four-part key
    // is needed because a definition's sequence NAMES are not unique.
    internal static void CheckLanes(TestContext ctx, string animName, IReadOnlyList<BurstFire> fired,
        BurstLane[] lanes, System.Text.StringBuilder report)
    {
        var own = fired.Where(f => f.Anim == animName).ToList();
        report.AppendLine($"--- {animName}: {own.Count} dispatch(es) ---");
        foreach (var f in own)
        {
            report.AppendLine($"  {f.T,7:0.0000}s  [{(f.Sequence.Length == 0 ? "<unnamed>" : f.Sequence)}] "
                              + $"#{f.Index} {f.Kind} {f.Name}");
        }

        var cursor = new int[lanes.Length];
        var at = new float[lanes.Length][];
        for (int i = 0; i < lanes.Length; i++)
        {
            at[i] = new float[lanes[i].Steps.Length];
        }

        var stray = new List<BurstFire>();
        foreach (var f in own)
        {
            int lane = -1;
            for (int l = 0; l < lanes.Length && lane < 0; l++)
            {
                if (cursor[l] >= lanes[l].Steps.Length)
                {
                    continue;
                }
                var step = lanes[l].Steps[cursor[l]];
                if (lanes[l].Sequence == f.Sequence && step.Index == f.Index
                    && step.Kind == f.Kind && step.Name == f.Name)
                {
                    lane = l;
                }
            }
            if (lane < 0)
            {
                stray.Add(f);
                continue;
            }
            at[lane][cursor[lane]] = f.T;
            cursor[lane]++;
        }

        string strays = string.Join(", ",
            stray.Select(s => $"{s.T:0.###}s [{s.Sequence}] #{s.Index} {s.Kind} {s.Name}"));
        ctx.Check(stray.Count == 0,
            $"{animName}: every dispatch is an authored event arriving in its sequence's order ({stray.Count} stray: {strays})");
        for (int l = 0; l < lanes.Length; l++)
        {
            var lane = lanes[l];
            string tag = $"{animName} [{(lane.Sequence.Length == 0 ? "<unnamed>" : lane.Sequence)}]";
            ctx.Same(lane.Steps.Length, cursor[l], $"{tag}: authored events fired, in order");
            for (int s = 0; s < cursor[l]; s++)
            {
                var step = lane.Steps[s];
                ctx.Check(Mathf.Abs(at[l][s] - step.At) <= BurstSlack,
                    $"{tag} #{step.Index} {step.Kind} fires at its authored {step.At:0.###} s ({at[l][s]:0.###} s)");
            }
        }
    }

    // One walk drains 512 queued starts and leaves the rest for the next walk, which still owes
    // each of them the zero-dt advance it was queued for. A start dropped from the queue instead
    // takes that walk's real dt as its first advance, and its t=0 events then land in the same
    // advance as an event stamped inside the first tick.
    // ⚠ Read both events of a callee, never the count of first advances. Every callee fires its
    // t=0 event whatever dt it was first advanced with, so only the stamped one tells the two apart.
    private static void QueuedStartsPastTheBudget(TestContext ctx)
    {
        const float Tick = 1f / 30f;
        const float Stamped = 0.02f;    // inside one tick, so only a charged first advance reaches it
        const int Fans = 3;             // one sequence's runner fires at most 256 events per advance
        const int PerFan = 200;         // so three of them queue 600 starts in one walk, past the 512
        const int Callees = Fans * PerFan;
        const string Caller = "budget_caller";
        const string Nothing = "budget_nothing";
        var defs = new List<AnimDefinition>();
        var caller = new AnimDefinition { Name = Caller, AnimName = Caller };
        for (int fan = 0; fan < Fans; fan++)
        {
            var seq = new AnimSequence { Name = $"fan{fan}" };
            for (int i = 0; i < PerFan; i++)
            {
                string callee = $"budget_callee{(fan * PerFan) + i:000}";
                // The first call of each fan is scheduled, so the whole fan-out is dispatched by a
                // tick's walk rather than by the instant burst a Play runs; the rest follow it in
                // the same tick.
                seq.Events.Add(i == 0
                    ? OrderEvent("CallAnimation", callee, "Sequence", 0.1f)
                    : OrderEvent("CallAnimation", callee));
                defs.Add(OrderDef(callee,
                    OrderEvent("StopAnimation", Nothing),
                    OrderEvent("InvalidateAnimation", Nothing, "Sequence", Stamped)));
            }

            caller.Sequences.Add(seq);
        }

        defs.Add(caller);
        var stage = new Node3D { Name = "StartBudgetStage" };
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
            runtime.Bind(stage, AnimProgram.FromDefinitions(defs));
            int tick = 0;
            var burst = new Dictionary<string, int>();
            var stamped = new Dictionary<string, int>();
            runtime.OnEventDispatched = d =>
            {
                string name = d.Def.AnimName ?? string.Empty;
                if (!name.StartsWith("budget_callee", System.StringComparison.Ordinal))
                {
                    return;
                }

                var landed = d.EventKind == "StopAnimation" ? burst : stamped;
                if (!landed.ContainsKey(name))
                {
                    landed[name] = tick;
                }
            };
            ctx.Same(1, runtime.Play(Caller).Count, $"the caller starts as one instance");
            for (tick = 1; tick <= 12; tick++)
            {
                runtime.Advance(Tick);
            }

            ctx.Same(Callees, burst.Count, $"every one of the {Callees} calls starts its callee");
            int charged = burst.Count(b => stamped.TryGetValue(b.Key, out int at) && at <= b.Value);
            ctx.Same(0, charged,
                $"every callee's first advance is at zero dt: {charged} of {burst.Count} reached an event stamped at {Stamped:0.###} s in the same advance as their t=0 one");
            var passes = burst.Values.GroupBy(t => t).OrderBy(g => g.Key)
                .Select(g => $"{g.Count()} in walk {g.Key}");
            ctx.Note($"first advances by walk: {string.Join(", ", passes)}");
        }
        finally
        {
            runtime.Free();
            stage.Free();
        }
    }

    // One definition of one sequence, the shape the call-order suite needs: an animation name a
    // CALL_ANIMATION can reach, and events with nothing to resolve against the world.
    private static AnimDefinition OrderDef(string animName, params AnimEvent[] events)
    {
        var def = new AnimDefinition { Name = animName, AnimName = animName };
        var seq = new AnimSequence { Name = "seq" };
        seq.Events.AddRange(events);
        def.Sequences.Add(seq);
        return def;
    }

    // An event carrying only a target name. A null offset is the unstamped form, which fires as
    // soon as the previous event has; "Sequence" makes the time absolute in the sequence's clock.
    private static AnimEvent OrderEvent(string kind, string name, string? offset = null,
        float time = 0f) =>
        new()
        {
            Kind = kind,
            StartOffset = offset,
            StartTime = time,
            Data = new AnimData(new Dictionary<string, object?> { ["name"] = name }),
        };
}
