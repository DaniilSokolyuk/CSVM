using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>The proximity fuse against a real surface hull. The original's sweep walks
/// <c>VehicleList</c>, which holds the AI ground and sea vehicles beside the aircraft
/// (docs/org/ordnanceTypes.md "The proximity fuse"), so a fused round passing a patrol boat inside
/// its trigger distance detonates there and the blast reaches the hull.</summary>
internal static class VesselFuseSuites
{
    private const string Chapter = "C1B";
    private const string Mission = "M03";
    private const string Boat = "patrolboat_1";
    private const float StepDt = 1f / 60f;

    // How far either side of the point over the hull the fly-by line starts and ends.
    private const float Lead = 80f;

    // ⚠ The hull is never stepped: a moved physics body does not re-enter the space queries inside
    // one frame (INSTR-13), and the blast reaches the hull only through that query.
    [Suite("vessel-fuse",
        "the proximity fuse arms against a surface hull: over CM08's built patrolboat_1 in C1B, a "
        + "dumbfire fused rocket flown level past the hull origin inside its DETONATION_DISTANCE, "
        + "on a line the space proves clear of every collider, detonates in the step holding its "
        + "closest approach rather than at first entry and its blast takes HP off the hull's "
        + "destructible pool; the same pass flies on with the hull on the round's own team, and "
        + "with no hull registry on the pool")]
    internal static void VesselFuse(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");
        ctx.RequireData(texturesPath, $"{Chapter} textures");
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);

        // The zeppelin-fuse suite's pick: a dumbfire, spread-free fused rocket with no dot cone.
        var rocket = weapons.All.FirstOrDefault(w =>
            w.IsRocket && w.DetonationDistance is > 0f && w.DetonationDotProduct is null
            && w.CannonSpread is not > 0f && !w.IsGuided
            && w.Velocity is > 0f && w.Range is not < 300f);
        ctx.Check(rocket != null, $"a dumbfire proximity-fused rocket with a 300 m+ range ships");
        if (rocket == null)
            return;
        float fuseRange = rocket.DetonationDistance!.Value;
        ctx.Note($"round {rocket.Id}: fuse {fuseRange:0.#} m, blast {rocket.ImpactProximity:0.#} m, v {rocket.Velocity:0} m/s");

        var mission = MissionOf(ctx);
        var script = ObjectiveScript.Load(missionZrdr);
        var defs = VehicleDefs.Load(ctx.ZrdrPath);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var director = CampaignDirector.Create(script, mission, CampaignProfileDef.NewProfile("Fuse"), null);
        var report = new StringBuilder();
        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        try
        {
            ctx.WithWorld(Chapter, collision: true, Mission, world =>
            {
                var runtime = world.Runtime;
                var worldRoot = runtime.WorldRoot ?? world.Stage;
                var vessels = new SurfaceVehicleRuntime(world.Gamez, world.Session.Builder.Scene,
                    runtime, defs, worldRoot);
                worldRoot.AddChild(vessels);
                director.BuildRoster(new CampaignDirector.RosterInputs
                {
                    ChapterZrdrPath = chapterZrdr,
                    MissionZrdrPath = missionZrdr,
                    ZrdrPath = ctx.ZrdrPath,
                    MinAiActiveDist = skills.MinAiActiveDist,
                    FindNodes = name => runtime.FindNodes(name),
                    Spawn = (_, _, _, _) => null,   // the aircraft half stays unbuilt on purpose
                    SpawnSurface = (plan, pos, forward) => vessels.Spawn(plan, pos, forward),
                    Rng = new Random(1),
                });
                foreach (var hull in director.Vessels.Values)
                    hull.Wake();
                // The hulls were placed after the world build, so their colliders reach the space
                // only through a sync (INSTR-13).
                ctx.SyncPhysics();
                if (!director.Vessels.TryGetValue(Boat, out var boat))
                {
                    ctx.Check(false, $"'{Boat}' builds a hull in {Chapter}/{Mission}");
                    return;
                }
                int boatTeam = boat.Team ?? AimAssist.NeutralTeam;
                // CM08's four boats sit within 60 m of each other, so the others go onto the round's
                // side: the pass then answers for this one hull alone.
                foreach (var other in director.Vessels.Values.Where(v => v != boat))
                    other.Team = AimAssist.PlayerTeam;
                ctx.Check(boatTeam != AimAssist.PlayerTeam && boat.Health is > 0f,
                    $"'{Boat}' is a live hostile hull with a destructible pool: team {boatTeam}, HP {boat.Health:0.#}");

                var space = ctx.Host.GetWorld3D().DirectSpaceState;
                var origin = boat.Position;
                report.AppendLine($"{Boat}: origin {origin}, colliders {boat.Body.FindChildren("*", "CollisionObject3D", recursive: true, owned: false).Count}");
                foreach (var (name, hull) in director.Vessels)
                    report.AppendLine($"hull {name}: at {hull.Position}, {hull.Position.DistanceTo(origin):0.#} m off, team {hull.Team}");
                var down = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                    origin + new Vector3(0f, 200f, 0f), origin - new Vector3(0f, 50f, 0f)));
                var deckBody = down.Count > 0 ? down["collider"].Obj as Node : null;
                ctx.Check(deckBody != null && boat.Body.IsAncestorOf(deckBody),
                    $"a ray down over the hull origin meets the hull's own collider ({deckBody?.GetParent()?.Name}/{deckBody?.Name})");
                if (deckBody == null || !boat.Body.IsAncestorOf(deckBody))
                    return;

                // A level line at gap G over the origin passes within G of it; the first gap and
                // axis that crosses the whole hull without a strike is the fly-by.
                Vector3 start = default, dir = default, over = default;
                float gap = 0f;
                bool clear = false;
                foreach (float frac in new[] { 0.5f, 0.7f, 0.85f })
                {
                    foreach (var axis in new[] { Vector3.Right, Vector3.Back, new Vector3(1f, 0f, 1f).Normalized(), new Vector3(1f, 0f, -1f).Normalized() })
                    {
                        var through = origin + new Vector3(0f, fuseRange * frac, 0f);
                        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                            through - axis * Lead, through + axis * Lead));
                        report.AppendLine($"gap {fuseRange * frac:0.##} along {axis}: {(hit.Count == 0 ? "clear" : $"strikes {(hit["collider"].Obj as Node)?.GetParent()?.Name}/{(hit["collider"].Obj as Node)?.Name} at {hit["position"].AsVector3()}")}");
                        if (hit.Count == 0)
                        {
                            (start, dir, over, gap, clear) = (through - axis * Lead, axis, through, fuseRange * frac, true);
                            break;
                        }
                    }
                    if (clear)
                        break;
                }
                ctx.Check(clear && gap < fuseRange,
                    $"a level line {gap:0.##} m over the hull origin, inside the {fuseRange:0.#} m fuse, crosses the hull clear of every collider");
                if (!clear)
                    return;

                var struck = new List<Node?>();
                var live = new ProjectilePool(textures, null, null)
                {
                    DamageSink = (node, amount) =>
                    {
                        struck.Add(node);
                        return runtime.DamageAt(node, amount);
                    },
                };
                pool = live;
                ctx.Host.AddChild(live);

                // The two refusals first, while the hull is untouched.
                var noRegistry = Pass(live, rocket, start, dir, over, fuseRange);
                report.AppendLine($"no registry: {noRegistry}");
                ctx.Check(noRegistry.FlewOn && struck.Count == 0,
                    $"with no hull registry on the pool the round flies on past the hull ({noRegistry})");

                live.SurfaceVehicles = vessels;
                boat.Team = AimAssist.PlayerTeam;
                var friendly = Pass(live, rocket, start, dir, over, fuseRange);
                report.AppendLine($"own team ({boat.Team}): {friendly}; strikes {struck.Count}, HP {boat.Health:0.##}");
                boat.Team = boatTeam;
                ctx.Check(friendly.FlewOn && struck.Count == 0,
                    $"with the hull on the round's own team the round flies on past it ({friendly})");

                float before = boat.Health ?? 0f;
                var fused = Pass(live, rocket, start, dir, over, fuseRange);
                report.AppendLine($"hostile: {fused}; strikes {struck.Count}, HP {before:0.##} -> {boat.Health:0.##}");
                ctx.Check(!fused.FlewOn && fused.Steps > 0,
                    $"against the hostile hull the round detonates before passing it ({fused})");
                // Closest approach lies inside the step the round died in: past its last live
                // sample and no further than one step on. First entry is several steps earlier.
                float toFoot = (over - fused.Last).Dot(dir);
                float entryShort = Mathf.Sqrt(fuseRange * fuseRange - gap * gap);
                ctx.Check(toFoot >= 0f && toFoot <= fused.StepLength * 1.25f,
                    $"it detonated in the step holding its closest approach: last sample {toFoot:0.##} m short of it, step {fused.StepLength:0.##} m, where first entry sits {entryShort:0.##} m short");
                ctx.Check(struck.Any(n => n != null && boat.Body.IsAncestorOf(n)) && boat.Health < before,
                    $"the blast reaches the hull and spends its pool: HP {before:0.##} -> {boat.Health:0.##}");
            });
        }
        finally
        {
            if (pool != null && GodotObject.IsInstanceValid(pool))
                pool.Free();
            textures.Dispose();
        }
        ctx.WriteArtifact($"test-vessel-fuse-{Chapter}-{Mission}.txt", report.ToString());
    }

    // One fly-by from start along dir: stepped until the round dies or is well past the point over
    // the hull, reporting its last live sample and the length of the step it was taking there.
    private static PassResult Pass(ProjectilePool pool, WeaponDef rocket, Vector3 start, Vector3 dir,
        Vector3 over, float fuseRange)
    {
        pool.Clear();
        pool.Spawn(rocket, new Transform3D(Basis.LookingAt(dir, Vector3.Up), start), Vector3.Zero,
            team: AimAssist.PlayerTeam);
        var live = new List<(Vector3 Pos, Vector3 Velocity)>();
        var result = new PassResult { Last = start };
        for (int i = 0; i < 600; i++)
        {
            pool.SimStep(StepDt);
            live.Clear();
            pool.CollectLiveRounds(live);
            if (live.Count != 1)
                return result;
            result.Steps = i + 1;
            result.Last = live[0].Pos;
            result.StepLength = live[0].Velocity.Length() * StepDt;
            if ((live[0].Pos - over).Dot(dir) > 2f * fuseRange)
            {
                result.FlewOn = true;
                return result;
            }
        }
        return result;
    }

    private static CampaignMission MissionOf(TestContext ctx)
    {
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(Chapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(Mission, StringComparison.OrdinalIgnoreCase))
                return m;
        }
        throw new SuiteSkippedException($"{Chapter}/{Mission} is not in cm_sequence");
    }

    private struct PassResult
    {
        public bool FlewOn;
        public int Steps;
        public Vector3 Last;
        public float StepLength;

        public override readonly string ToString() => FormattableString.Invariant(
            $"steps {Steps}, flew on {FlewOn}, last ({Last.X:0.#},{Last.Y:0.#},{Last.Z:0.#})");
    }
}
