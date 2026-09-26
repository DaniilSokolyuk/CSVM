using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.InstantAction;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>Whether a PARKED zeppelin ring holds fire on a target its own hull stands between,
/// over C1/M04's real <c>piratezep</c>. For every emplacement, maps its own authored yaw/pitch
/// envelope against the shipped emplacement sight-line rule to find the first direction the hull
/// blocks, then parks a hostile plane there and reads whether the ring holds fire across several
/// of the cached line-of-sight's 1-2 s windows.
/// ⚠ First-direction, so it cannot see a shot ACROSS the hull; only a deepest-crossing sweep
/// over the flying hull can (docs/verification.md INSTR-48).</summary>
internal static class TurretHullBlockSuites
{
    private const float Dt = 1f / 60f;

    // Long enough to cross several of WorldRayBlocked's random 1-2 s cache windows, so a shot the
    // cache merely missed on its first read cannot pass as "held fire".
    private const float ProbeSeconds = 8f;

    [Suite("turret-hull-blocks-own-fire",
        "a PARKED zeppelin ring holds fire on a hostile plane its own hull stands between, over " +
        "several of the cached line-of-sight's 1-2s windows: every emplacement on C1/M04's real " +
        "piratezep is gimbal-swept with the shipped sight-line rule for the first direction its " +
        "own hull blocks, then probed live at that direction on a stepping clock")]
    internal static void TurretHullBlocksOwnFire(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var turretDefs = TurretDefs.Load(ctx.ZrdrPath);
        var report = new StringBuilder();

        ctx.WithWorld("C1", collision: true, mission: "M04", world =>
        {
            var textures = new TextureArchive(texturesPath);
            ProjectilePool? pool = null;
            FlightController? plane = null;
            TurretEmplacementRuntime? emplacements = null;
            var savedClock = Utils.GameClock.Current;
            try
            {
                var live = new ProjectilePool(textures, null, null) { DamageSink = world.Runtime.DamageAt };
                pool = live;
                ctx.Host.AddChild(live);
                var runtime = emplacements = new TurretEmplacementRuntime(turretDefs, weapons,
                    (pattern, scope) => world.Runtime.FindNodes(pattern, scope), live,
                    world.Runtime.WorldRoot);

                var hulls = world.Runtime.FindNodes("piratezep");
                ctx.Check(hulls.Count == 1, $"C1/M04's world carries the piratezep hull");
                if (hulls.Count != 1)
                {
                    return;
                }
                var hull = hulls[0];
                world.Runtime.SetTargetActive(hull, true);

                var rings = runtime.Emplacements
                    .Where(t => t.Site is { } s && (s == hull || hull.IsAncestorOf(s))).ToList();
                ctx.Check(rings.Count > 0, $"piratezep carries emplacements count={rings.Count}");
                if (rings.Count == 0)
                {
                    return;
                }
                // The record ships piratezep allied (TEAM 1); forced hostile here only so a real
                // plane is something for AcquireTarget to find. The mechanism under test does not
                // read team.
                runtime.SetTeamUnder(hull, InstantActionRuntime.EnemyTeam);

                var space = ctx.Host.GetWorld3D().DirectSpaceState;

                // ⚠ Keep the clock stepping. Without it GameClock.Current?.Time never moves, the
                // 1-2 s line-of-sight cache never expires, and the 8 s below rides ONE cast per
                // ring rather than the several it claims (docs/verification.md INSTR-49).
                Utils.GameClock.Current = new Utils.GameClock { Mode = Utils.GameClock.RunMode.FixedStep };

                var st = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
                var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
                var rig = new FlightController
                {
                    PlaneModel = model,
                    Collider = PlaneCollider.Build(model),
                    Damage = new PlaneDamage(st.DestroyableParts),
                    PlayerIndex = 0,
                    Projectiles = live,
                    UseKeyboard = false,
                    AllowPause = false,
                };
                rig.AddChild(model);
                rig.Setup(new FlightModel(st), ctx.Camera, new CamParams(), hull.GlobalPosition,
                    hull.GlobalPosition + Vector3.Forward);
                ctx.Host.AddChild(rig);
                plane = rig;

                // Every ring's gimbal envelope, mapped before any SimStep moves a PitchNode off the
                // rest pose BaseBasis reads: per (yaw, pitch), whether the shipped sight-line rule
                // meets the hull inside DETECTION_RANGE.
                var candidates = new List<(TurretController Ring, Vector3 Dir, float Fraction)>();
                int ownRigOnly = 0;
                foreach (var ring in rings)
                {
                    var ringPos = ring.WorldPosition;
                    var ownRig = ring.SiteColliderRids();
                    var anchor = ring.YawNode ?? ring.PitchNode;
                    var parentBasis = (anchor.GetParent() as Node3D)?.GlobalBasis ?? Basis.Identity;
                    var baseBasis = (parentBasis * anchor.Transform.Basis).Orthonormalized();
                    float yawMin = ring.Def.YawRestricted ? ring.Def.YawMinDeg!.Value : -180f;
                    float yawMax = ring.Def.YawRestricted ? ring.Def.YawMaxDeg!.Value : 180f;
                    float pitchMin = ring.Def.PitchRestricted ? ring.Def.PitchMinDeg!.Value : -80f;
                    float pitchMax = ring.Def.PitchRestricted ? ring.Def.PitchMaxDeg!.Value : 80f;
                    Vector3? blockedDir = null;
                    float blockedFraction = 0f;
                    string blockedName = "none";
                    for (float yaw = yawMin; blockedDir == null && yaw <= yawMax; yaw += 10f)
                    {
                        for (float pitch = pitchMin; pitch <= pitchMax; pitch += 10f)
                        {
                            var dir = baseBasis * TurretController.LocalDir(yaw, pitch);
                            var to = ringPos + dir * ring.Def.DetectionRange;
                            var span = to - ringPos;
                            var from = ringPos + span * (TurretController.MountSkirtM / span.Length());
                            // ⚠ The sweep runs the SHIPPED rule, own rig excluded. Without it the
                            // direction picked is the ring's own barrel, which the live gate reads
                            // as clear, and the probe below asserts on a ring with no hull in front.
                            var query = PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.World);
                            if (ownRig.Count > 0)
                            {
                                query.Exclude = ownRig;
                            }
                            var hit = space.IntersectRay(query);
                            if (hit.Count == 0 && space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                                from, to, CollisionLayers.World)).Count > 0)
                            {
                                ownRigOnly++;
                            }
                            if (hit.Count > 0)
                            {
                                blockedDir = dir;
                                var hitPos = (Vector3)hit["position"];
                                blockedFraction = ringPos.DistanceTo(hitPos) / ring.Def.DetectionRange;
                                blockedName = hit["collider"].Obj is Node hitNode
                                    ? WorldCollision.OwnerOf(hitNode).Name.ToString() : "?";
                                break;
                            }
                        }
                    }
                    if (blockedDir is { } dirFound)
                    {
                        candidates.Add((ring, dirFound, blockedFraction));
                    }
                    report.AppendLine($"{ring.Label} sweep-hit={blockedName} at {blockedFraction:0.00} of DETECTION_RANGE");
                }
                ctx.Check(candidates.Count > 0,
                    $"piratezep's own gimbal sweep finds at least one ring with an in-arc, hull-blocked direction (found {candidates.Count} of {rings.Count})");

                // A dormant ring's SimStep returns before the LOS check ever runs (ACTIVATED gates
                // tracking too), so it can neither confirm nor break this mechanism; wake every
                // candidate so its probe actually exercises WorldRayBlocked.
                foreach (var (ring, _, _) in candidates)
                {
                    ring.SetActivated(true);
                }

                // Ticked in isolation (ring.SimStep, not runtime.SimStep): stepping the whole roster
                // would slew every OTHER candidate off the rest pose its own sweep read, which found
                // a since-moved sibling ring's own barrel as "the hull" while tracing this.
                var testOrder = candidates.OrderByDescending(c => c.Ring.Label.EndsWith("@ctur2")).ToList();
                int ringsBlocked = 0;
                int ringsConfirmed = 0;
                foreach (var (ring, dir, fraction) in testOrder)
                {
                    var ringPos = ring.WorldPosition;
                    var anchorNode = (Node3D)(ring.YawNode ?? ring.PitchNode);
                    var ringSpace = anchorNode.GetWorld3D()?.DirectSpaceState;

                    // WorldPosition sits meters off the raw PlaceHeld pose, so a fixed distance guess
                    // can clip a thin panel's edge; confirmed instead against increasing fractions
                    // past the sweep's own hit point, with the shipped sight-line rule itself.
                    Vector3? confirmedPos = null;
                    for (float extra = 0.15f; extra <= 0.6f; extra += 0.1f)
                    {
                        var tryPos = ringPos + dir * (ring.Def.DetectionRange * Mathf.Min(0.97f, fraction + extra));
                        rig.PlaceHeld(tryPos, ringPos);
                        var checkTo = rig.WorldPosition + Vector3.Up * 0.2f;
                        if (ringSpace != null && TurretController.WorldBlocksEmplacementLine(
                            ringSpace, ring.WorldPosition, checkTo, ring.SiteColliderRids()))
                        {
                            confirmedPos = tryPos;
                            break;
                        }
                    }
                    report.AppendLine($"{ring.Label} dir={dir} sweep-hit-fraction={fraction:0.00} confirmed-blocked-placement={(confirmedPos.HasValue ? "yes" : "NO")}");
                    if (confirmedPos is not { } chosenPos)
                    {
                        // The sweep's own obstruction could not be confirmed with WorldRayBlocked's
                        // exact call at any tried distance (a thin panel's edge, most likely): not
                        // this ring's evidence either way, so it is not asserted on.
                        continue;
                    }
                    rig.PlaceHeld(chosenPos, ringPos);
                    ringsConfirmed++;
                    int before = ring.ShotsFired;
                    bool sawBlocked = false;
                    var gates = new List<TurretGate>();
                    int frames = (int)(ProbeSeconds / Dt);
                    for (int i = 0; i < frames; i++)
                    {
                        Utils.GameClock.Current?.BeginFrame(Dt);
                        ring.SimStep(Dt);
                        live.SimStep(Dt);
                        if (ring.Gate == TurretGate.Blocked)
                        {
                            sawBlocked = true;
                        }
                        if (i % 60 == 0)
                        {
                            gates.Add(ring.Gate);
                        }
                    }
                    int shots = ring.ShotsFired - before;
                    if (sawBlocked)
                    {
                        ringsBlocked++;
                    }
                    report.AppendLine($"{ring.Label} dir={dir} pos={chosenPos}: shots={shots} sawBlocked={sawBlocked} gates=[{string.Join(",", gates)}]");
                    ctx.Check(shots == 0,
                        $"{ring.Label} holds fire over {ProbeSeconds:0}s on the far side of its own hull (confirmed blocked by its own gimbal sweep) shots={shots} gates=[{string.Join(",", gates)}]");
                }
                ctx.Check(ringsConfirmed > 0,
                    $"at least one ring's sweep-found obstruction is confirmed with WorldRayBlocked's exact call shape ({ringsConfirmed} of {candidates.Count})");
                ctx.Check(ringsBlocked == ringsConfirmed,
                    $"every confirmed-blocked ring reads Blocked at least once rather than merely missing the target ({ringsBlocked} of {ringsConfirmed})");

                ctx.WriteArtifact("test-turret-hull-block.txt", report.ToString());
                ctx.Note($"{candidates.Count} of {rings.Count} ring(s) had an in-arc hull-blocked direction; {ringsConfirmed} confirmed, all held fire there");
                ctx.Note($"{ownRigOnly} swept direction(s) were obstructed by the ring's own rig alone, which the sight-line rule reads as clear");
            }
            finally
            {
                Utils.GameClock.Current = savedClock;
                plane?.Free();
                emplacements?.Free();
                pool?.Free();
                textures.Dispose();
            }
        });
    }
}
