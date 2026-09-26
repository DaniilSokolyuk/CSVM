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

/// <summary>BL-477's instrument: what actually stands between a weapon ray and C3/M01's cargo
/// zeppelin's slung hydrogen tanks, measured rather than inferred. Casts a fixed sphere of rays at
/// <c>hydrogentank1</c>'s centre from the built world and names the first collider on each,
/// so the fore/aft asymmetry reported at the controls is a table of node names and counts.
/// The original's own rule is <c>docs/org/weaponRay.md</c>: its ray test reads no texture.
/// The second half is the splash question the first left open: a burst on the hull underside
/// above the tanks, run through the production cover ray down to each tank.</summary>
internal static class AlphaCutoutRaySuites
{
    private const string Chapter = "C3";
    private const string Mission = "M01";
    private const string Zeppelin = "cargozep1";
    private const string Tank = "hydrogentank1";

    // The HE rocket, the ordnance the original run used; its IMPACT_PROXIMITY is the splash radius.
    private const string Rocket = "wep_06";

    // Outside cargozep1's own child bbox (z spans -336..352) and inside the front truss's
    // f_hi LOD band (range.max 800), which is the level the original tests at gun range.
    private const float CastRadius = 500f;

    private const int Azimuths = 36;

    private static readonly float[] Elevations = { -60f, -30f, 0f, 30f, 60f };

    // BL-477: what stops a shot at the cargo zeppelin's slung tanks was inferred from the
    // geometry; this measures it. The original's own ray test reads no texture at all
    // (docs/org/weaponRay.md), so this is a census of OUR occluders, not a fidelity gate.
    [Suite("alpha-cutout-ray-census",
        "the occluders standing between a weapon ray and C3/M01's cargo zeppelin: rays at "
        + "hydrogentank1's mesh centre from 36 azimuths at five elevations, each naming the "
        + "first collider's gamez node, over the mission's own world with the zeppelins placed "
        + "at their authored pose; then the splash half, a burst on the hull underside plate "
        + "g482 run through the production cover ray down to each of hydrogentank1..4")]
    internal static void AlphaCutoutRayCensus(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");

        var report = new StringBuilder();
        ctx.WithWorld(Chapter, collision: true, mission: Mission, world =>
        {
            var runtime = world.Session.Runtime;
            var defs = Zeppelins.Load(missionZrdr);
            var nets = AiNets.Load(chapterZrdr);
            ZeppelinRuntime? zeps = null;
            try
            {
                // Placing the zeppelins is what puts cargozep1 at its authored pose; the built
                // pose is wherever the gamez parked the template.
                zeps = new ZeppelinRuntime(defs,
                    name => runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null, nets);
                var host = runtime.FindNodes(Zeppelin).FirstOrDefault();
                ctx.Check(host != null, $"the {Zeppelin} world node resolves in the {Chapter}/{Mission} world");
                if (host == null)
                    return;

                var tank = runtime.FindNodes(Tank, host).FirstOrDefault();
                ctx.Check(tank != null, $"{Tank} resolves under the {Zeppelin} subtree");
                if (tank == null)
                    return;

                var centre = SubtreeCentre(tank);
                ctx.Check(centre != null, $"{Tank}'s subtree carries mesh geometry to aim at");
                if (centre is not { } aimAt)
                    return;

                Census(ctx, world, host, tank, aimAt, report);
                BlastCover(ctx, world, host, report);
            }
            finally
            {
                zeps?.Free();
            }
        });

        ctx.WriteArtifact("test-alpha-cutout-ray-census.txt", report.ToString());
    }

    private static void Census(TestContext ctx, TestWorld world, Node3D host, Node3D tank,
        Vector3 aimAt, StringBuilder report)
    {
        // A moved static body's transform reaches the physics server on the next frame, which
        // never comes inside a suite; without this push every ray reads the colliders at the
        // BUILT pose and finds nothing where the meshes now are (verification INSTR-13).
        foreach (var n in Subtree(host))
        {
            if (n is Node3D n3d)
                n3d.ForceUpdateTransform();
        }

        var space = world.Stage.GetWorld3D().DirectSpaceState;
        var basis = host.GlobalTransform.Basis;
        var tankNodes = Subtree(tank).ToHashSet();

        var occluders = new Dictionary<string, int>();
        int reached = 0;
        int missed = 0;
        int zepBodies = Subtree(host).Count(n => n is StaticBody3D);
        int tankBodies = Subtree(tank).Count(n => n is StaticBody3D);
        int zepMeshes = Subtree(host).Count(n => n is MeshInstance3D);
        report.AppendLine($"{Zeppelin} subtree: {zepMeshes} mesh instance(s), {zepBodies} collider "
            + $"body/bodies; {Tank} subtree: {tankBodies}.");
        report.AppendLine($"{Chapter}/{Mission} {Zeppelin} -> {Tank}: {Azimuths} azimuths x "
            + $"{Elevations.Length} elevations at {CastRadius:0} m, first collider per ray.");
        report.AppendLine("azimuth 0 = from dead ahead of the nose (-Z), 180 = from astern (+Z).");
        report.AppendLine();
        foreach (float elevation in Elevations)
        {
            var row = new StringBuilder($"el {elevation,4:0}: ");
            for (int a = 0; a < Azimuths; a++)
            {
                float az = a * 360f / Azimuths;
                var outward = basis * Outward(az, elevation);
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                    aimAt + (outward * CastRadius), aimAt, CollisionLayers.World));
                string name;
                if (hit.Count == 0)
                {
                    name = "(nothing)";
                    missed++;
                }
                else
                {
                    var collider = hit["collider"].Obj as Node;
                    name = OwnerName(collider);
                    if (collider != null && IsUnder(collider, tankNodes))
                        reached++;
                }

                occluders[name] = occluders.GetValueOrDefault(name) + 1;
                row.Append($"{az,3:0}={name} ");
            }

            report.AppendLine(row.ToString());
        }

        int total = Azimuths * Elevations.Length;
        report.AppendLine();
        report.AppendLine($"reached {Tank}: {reached}/{total}; nothing hit: {missed}/{total}");
        foreach (var (name, count) in occluders.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key))
            report.AppendLine($"  {count,4} x {name}");

        ctx.Check(reached > 0, $"some aspect reaches {Tank} at all, reached={reached}/{total}");
        ctx.Check(reached < total,
            $"and some aspect does not, so the census measures an asymmetry rather than open sky: reached={reached}/{total}");
        string worst = string.Join(", ", occluders.Where(kv => kv.Key != Tank)
            .OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key} ({kv.Value})"));
        ctx.Note($"{reached} of {total} rays at {Tank} reach it; the rest stop on {worst}");
    }

    // BL-477's second half: the tanks were killed in the original by a rocket that struck the HULL
    // UNDERSIDE above them, not by a round through the truss. The truss is a lateral screen around
    // the tanks rather than a roof over them, so the shipped data has no polygon between the
    // underside plate `g482` and the tank tops; this asks whether our colliders agree, by running
    // the production splash cover ray (ProjectilePool.BlastCoverBetween) from that burst.
    private static void BlastCover(TestContext ctx, TestWorld world, Node3D host, StringBuilder report)
    {
        var runtime = world.Session.Runtime;
        var space = world.Stage.GetWorld3D().DirectSpaceState;
        var up = (host.GlobalTransform.Basis * Vector3.Up).Normalized();
        var side = (host.GlobalTransform.Basis * Vector3.Right).Normalized();

        var tanks = new List<(string Name, Vector3 Centre, Aabb Box, Rid Rid)>();
        for (int i = 1; i <= 4; i++)
        {
            string name = $"hydrogentank{i}";
            var node = runtime.FindNodes(name, host).FirstOrDefault();
            var box = node == null ? null : SubtreeBox(node);
            var body = node == null ? null : Subtree(node).OfType<StaticBody3D>().FirstOrDefault();
            ctx.Check(box != null && body != null, $"{name} resolves with geometry and a collider body");
            if (box is not { } b || body == null)
                return;
            tanks.Add((name, b.GetCenter(), b, body.GetRid()));
        }

        // The burst: straight up off the first tank's top until it meets the hull. The zeppelin's
        // own subtree already had its transforms pushed to the physics server by Census (INSTR-13).
        var first = tanks[0];
        var from = first.Centre + (up * ((first.Box.Size.Y * 0.5f) + 0.05f));
        var upHit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(
            from, from + (up * 80f), CollisionLayers.World));
        report.AppendLine();
        report.AppendLine("--- the hull underside above the tanks, and the splash cover ray from it ---");
        ctx.Check(upHit.Count > 0, $"a hull surface stands above {first.Name} to put the rocket into");
        if (upHit.Count == 0)
        {
            report.AppendLine($"nothing above {first.Name} within 80 m: no burst point to test from.");
            return;
        }

        var burst = (Vector3)upHit["position"];
        var normal = (Vector3)upHit["normal"];
        string hull = OwnerName(upHit["collider"].Obj as Node);
        report.AppendLine($"struck surface {hull} at ({burst.X:0.0}, {burst.Y:0.0}, {burst.Z:0.0}), "
            + $"{burst.DistanceTo(first.Centre):0.0} m above {first.Name}'s centre, normal "
            + $"({normal.X:0.00}, {normal.Y:0.00}, {normal.Z:0.00}).");

        // The pool is here only to run its own cover predicate; it is never added to the tree and
        // fires nothing, so the archive is the cheapest one that satisfies its constructor.
        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, Chapter));
        var pool = new ProjectilePool(textures, null, null);
        try
        {
            foreach (var t in tanks)
            {
                bool covered = pool.BlastCoverBetween(space, burst, normal, t.Centre, t.Rid, out var cover);
                report.AppendLine($"  {t.Name}: {(covered ? "COVERED by " + OwnerName(cover) : "clear")}"
                    + $" at {burst.DistanceTo(t.Centre):0.0} m");
                string stands = covered ? $", but {OwnerName(cover)} stands in it" : string.Empty;
                ctx.Check(!covered,
                    $"a burst on the hull underside has a clear splash ray down to {t.Name}{stands}");
            }

            // The able-to-fail control: the same test from outside the truss, at tank height, with
            // no struck surface and so no CoverRayLift, must be blocked by the truss it crosses.
            var outside = tanks[0].Centre + (side * 120f);
            bool blocked = pool.BlastCoverBetween(space, outside, Vector3.Zero, tanks[0].Centre,
                tanks[0].Rid, out var screen);
            report.AppendLine($"  control, from {120:0} m abeam at tank height: "
                + $"{(blocked ? "COVERED by " + OwnerName(screen) : "clear")}");
            ctx.Check(blocked,
                $"and the control fails as it must: a burst abeam is stopped by {OwnerName(screen)}");

            BlastGather(ctx, world, pool, space, burst, normal, tanks, report);
        }
        finally
        {
            pool.Free();
            textures.Dispose();
        }
    }

    // The same burst through the PRODUCTION gather, so the radius, the nearest-surface scoring and
    // the 32-target cap are the shipped ones rather than the census's own arithmetic.
    private static void BlastGather(TestContext ctx, TestWorld world, ProjectilePool pool,
        PhysicsDirectSpaceState3D space, Vector3 burst, Vector3 normal,
        List<(string Name, Vector3 Centre, Aabb Box, Rid Rid)> tanks, StringBuilder report)
    {
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        if (!weapons.TryGet(Rocket, out var rocket) || rocket.ImpactProximity is not > 0f)
        {
            ctx.Check(false, $"{Rocket} carries an IMPACT_PROXIMITY to gather at");
            return;
        }

        float radius = rocket.ImpactProximity.Value;
        var rows = new List<ProjectilePool.BlastCoverRow>();
        pool.BlastCoverCensus(space, burst, normal, radius, rows);
        report.AppendLine($"production gather at IMPACT_PROXIMITY {radius:0.#} m ({Rocket}): "
            + $"{rows.Count} world body/bodies inside it.");
        foreach (var r in rows)
        {
            report.AppendLine($"  {r.Distance,6:0.0} m  {(r.Covered ? "covered by " + OwnerName(r.Cover) : "clear")}"
                + $"  {OwnerName(r.Body)}");
        }

        var reached = tanks.Where(t => rows.Any(r => !r.Covered && r.Centre.DistanceTo(t.Centre) < 1f))
            .Select(t => t.Name).ToList();
        string named = reached.Count > 0 ? $": {string.Join(", ", reached)}" : string.Empty;
        ctx.Note($"a {Rocket} burst on the hull underside reaches {reached.Count} of the four tanks inside its {radius:0.#} m radius{named}");
    }

    private static Aabb? SubtreeBox(Node3D root)
    {
        Aabb? box = null;
        foreach (var n in Subtree(root))
        {
            if (n is not MeshInstance3D mi || mi.Mesh == null)
                continue;
            var world = mi.GlobalTransform * mi.GetAabb();
            box = box is { } b ? b.Merge(world) : world;
        }

        return box;
    }

    // Zeppelin-local: azimuth 0 points out along -Z (ahead of the nose), 180 along +Z (astern).
    private static Vector3 Outward(float azimuthDeg, float elevationDeg)
    {
        float az = Mathf.DegToRad(azimuthDeg);
        float el = Mathf.DegToRad(elevationDeg);
        return new Vector3(
            Mathf.Sin(az) * Mathf.Cos(el),
            Mathf.Sin(el),
            -Mathf.Cos(az) * Mathf.Cos(el)).Normalized();
    }

    // The gamez name of the nearest ancestor that carries one: a collider is a StaticBody3D child
    // of the built node, so its parent is the node the ray actually struck.
    private static string OwnerName(Node? collider)
    {
        for (var n = collider; n != null; n = n.GetParent())
        {
            if (n.HasMeta(AnimRuntime.NameMeta))
                return n.GetMeta(AnimRuntime.NameMeta).AsString();
        }

        return collider?.Name.ToString() ?? "(none)";
    }

    private static bool IsUnder(Node node, HashSet<Node> subtree)
    {
        for (var n = node; n != null; n = n.GetParent())
        {
            if (subtree.Contains(n))
                return true;
        }

        return false;
    }

    private static IEnumerable<Node> Subtree(Node root)
    {
        yield return root;
        foreach (var child in root.GetChildren())
        {
            foreach (var n in Subtree(child))
                yield return n;
        }
    }

    // Mesh-AABB centre in world space, because a node origin locates nothing reliably
    // (docs/formats/gamez.md).
    private static Vector3? SubtreeCentre(Node3D root)
    {
        Aabb? box = null;
        foreach (var n in Subtree(root))
        {
            if (n is not MeshInstance3D mi || mi.Mesh == null)
                continue;
            var world = mi.GlobalTransform * mi.GetAabb();
            box = box is { } b ? b.Merge(world) : world;
        }

        return box?.GetCenter();
    }
}
