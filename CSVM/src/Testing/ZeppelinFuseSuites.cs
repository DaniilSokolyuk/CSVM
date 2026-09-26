using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Godot;

namespace CSVM.Testing;

/// <summary>The proximity fuse against a real mission zeppelin. The original's sweep walks
/// <c>VehicleList</c>, which never holds a zeppelin (docs/org/ordnanceTypes.md "The proximity
/// fuse"), so a fused round passing inside its trigger distance of the hull flies on.</summary>
internal static class ZeppelinFuseSuites
{
    private const string Chapter = "C1";
    private const string Mission = "M04";
    private const string Zep = "piratezep";

    // ⚠ Every pose here is the BUILT pose, read before anything moves the node: a moved physics body
    // never re-enters the space queries inside one frame (INSTR-13), so no ZeppelinRuntime is made.
    [Suite("zeppelin-fuse",
        "the proximity fuse never fuses on a zeppelin: over C1/M04's built piratezep, a dumbfire "
        + "fused rocket flown level across the hull's top inside its DETONATION_DISTANCE, on a line "
        + "the space proves clear, passes its closest approach and flies on with no detonation and "
        + "no damage, while the same round aimed down at the same hull point strikes it and spends "
        + "damage through the destructible sink")]
    internal static void ZeppelinFuse(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);

        // Data-driven: the air-to-air suite's fuse pick (a dumbfire, spread-free rocket with no dot
        // cone), so the positive half of this claim is that suite's fused pass on an aircraft.
        var rocket = weapons.All.FirstOrDefault(w =>
            w.IsRocket && w.DetonationDistance is > 0f && w.DetonationDotProduct is null
            && w.CannonSpread is not > 0f && !w.IsGuided
            && w.Velocity is > 0f && w.Range is not < 300f);
        ctx.Check(rocket != null, $"a dumbfire proximity-fused rocket with a 300 m+ range ships");
        if (rocket == null)
            return;
        float fuseRange = rocket.DetonationDistance!.Value;
        ctx.Note($"round {rocket.Id}: fuse {fuseRange:0.#} m, range {rocket.Range:0} m, v {rocket.Velocity:0} m/s");

        var report = new StringBuilder();
        ctx.WithWorld(Chapter, collision: true, Mission, world =>
        {
            var runtime = world.Session.Runtime;
            var host = runtime.FindNodes(Zep).FirstOrDefault();
            var bag = host == null ? null : runtime.FindNodes("gasbag1", host).FirstOrDefault();
            ctx.Check(host != null && bag != null, $"'{Zep}' and its gasbag1 resolve in the {Chapter}/{Mission} world");
            if (host == null || bag == null)
                return;

            // The hull's top over gasbag1: straight down from well above, into the zeppelin's subtree.
            var space = ctx.Host.GetWorld3D().DirectSpaceState;
            var over = bag.GlobalPosition;
            var down = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                over + new Vector3(0f, 400f, 0f), over - new Vector3(0f, 400f, 0f)));
            var top = down.Count > 0 ? down["position"].AsVector3() : over;
            var topBody = down.Count > 0 ? down["collider"].Obj as Node : null;
            ctx.Check(topBody != null && host.IsAncestorOf(topBody),
                $"a ray down over gasbag1 meets the zeppelin's own collider first ({topBody?.GetParent()?.Name}/{topBody?.Name})");
            if (topBody == null || !host.IsAncestorOf(topBody))
                return;
            report.AppendLine($"hull top over gasbag1: {top} into {topBody.GetParent()?.Name}/{topBody.Name}");

            // A level line at gap G above that point is within G of the hull at its closest. Try
            // both horizontal axes and a few gaps until one crosses the whole hull without a strike.
            const float Lead = 180f;
            Vector3 start = default, end = default, dir = default;
            float gap = 0f;
            bool clear = false;
            foreach (float frac in new[] { 0.3f, 0.5f, 0.7f })
            {
                foreach (var axis in new[] { Vector3.Right, Vector3.Back })
                {
                    var through = top + new Vector3(0f, fuseRange * frac, 0f);
                    var s = through - axis * Lead;
                    var e = through + axis * Lead;
                    var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(s, e));
                    report.AppendLine($"gap {fuseRange * frac:0.##} along {axis}: {(hit.Count == 0 ? "clear" : $"strikes {(hit["collider"].Obj as Node)?.Name}")}");
                    if (hit.Count == 0)
                    {
                        (start, end, dir, gap, clear) = (s, e, axis, fuseRange * frac, true);
                        break;
                    }
                }

                if (clear)
                    break;
            }

            ctx.Check(clear && gap < fuseRange,
                $"a level line {gap:0.##} m over the hull top, inside the {fuseRange:0.#} m fuse range, crosses the zeppelin clear of every collider");
            if (!clear)
                return;

            var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, Chapter));
            ProjectilePool? pool = null;
            try
            {
                var struck = new List<Node?>();
                pool = new ProjectilePool(textures, null, null)
                {
                    DamageSink = (node, amount) =>
                    {
                        struck.Add(node);
                        return runtime.DamageAt(node, amount);
                    },
                };
                ctx.Host.AddChild(pool);

                // The fly-by: fired from the line's start, stepped until it is well past the point
                // over the hull, sampling its nearest approach to that point on the way.
                pool.Spawn(rocket, new Transform3D(Basis.LookingAt(dir, Vector3.Up), start), Vector3.Zero);
                var live = new List<(Vector3 Pos, Vector3 Velocity)>();
                var over0 = top + new Vector3(0f, gap, 0f);
                float nearest = float.PositiveInfinity;
                bool alive = true;
                bool pastIt = false;
                for (int i = 0; i < 600 && alive && !pastIt; i++)
                {
                    pool.SimStep(1f / 60f);
                    live.Clear();
                    pool.CollectLiveRounds(live);
                    alive = live.Count == 1;
                    if (alive)
                    {
                        nearest = Mathf.Min(nearest, live[0].Pos.DistanceTo(over0));
                        pastIt = (live[0].Pos - over0).Dot(dir) > 2f * fuseRange;
                    }
                }

                report.AppendLine($"fly-by: alive={alive} past={pastIt} nearest to the line's point over the hull {nearest:0.##} m, strikes {struck.Count}");
                ctx.Check(nearest < 10f,
                    $"the round flew the line: its nearest sample to the point {gap:0.##} m over the hull is {nearest:0.##} m");
                ctx.Check(alive && pastIt,
                    $"the round passed within {gap:0.##} m of the hull, inside its {fuseRange:0.#} m fuse, and flew on past it (alive={alive})");
                ctx.Same(0, struck.Count, $"…and nothing took damage from it");

                // The control: the same round straight down at the same hull point strikes it and
                // spends damage, so the pool, the space and the sink are all live.
                pool.Clear();
                var from = top + new Vector3(0f, 120f, 0f);
                pool.Spawn(rocket, new Transform3D(Basis.LookingAt(Vector3.Down, Vector3.Right), from), Vector3.Zero);
                for (int i = 0; i < 300 && struck.Count == 0; i++)
                    pool.SimStep(1f / 60f);
                ctx.Check(struck.Any(n => n != null && host.IsAncestorOf(n)),
                    $"control: the same round aimed down at the hull strikes the zeppelin and spends damage through the sink (strikes {struck.Count})");
            }
            finally
            {
                pool?.Free();
                textures.Dispose();
            }
        });
        ctx.WriteArtifact($"test-zeppelin-fuse-{Chapter}-{Mission}.txt", report.ToString());
    }
}
