using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Godot;

namespace CSVM.Testing;

/// <summary>C3/M03's Barracuda, the install's plainest case of a pool whose damage node is not the
/// piece a shooter aims at. <c>sub_destruction</c> HEALTH 200 anchors on the whole
/// <c>barracuda</c> group but roots on <c>subgen_doors</c>, the hangar block that is a SIBLING of
/// the five hull meshes under <c>subhealthy</c>. A round on the flight deck, the conning tower or
/// the doors climbs to that anchor and must find nothing there while the boat lives.</summary>
internal static class SubmarineHullSuites
{
    private const string Chapter = "C3";
    private const string Mission = "M03";
    private const string Anchor = "barracuda";
    private const string Hangar = "subgen_doors";
    private const string Surfacing = "sub_movement";
    private const string PoolAnim = "sub_destruction";

    // wep_07 FLAK, the sortie's weapon: HEALTH_DAMAGE 35 and IMPACT_PROXIMITY 100 m, a blast that
    // covers the whole 276 m boat, so what the splash spends depends on how many meshes answer.
    private const string Flak = "wep_07";

    private const float Tick = 1f / 30f;

    // sub_movement's whole surfacing: a 10 s rise, the drive into the bay and a 6 s settle, with
    // slack over the 60 s the hangar switch is reached at. The wait ends the moment it is.
    private const float SurfacingSeconds = 75f;

    private static readonly string[] HullMeshes =
    {
        "sub_body", "sub_body2", "sub_tower", "sub_runway", "sub_doors",
    };

    [Suite("destructible-sub-hull",
        "C3/M03's Barracuda answers a weapon hit on the hangar block its pool roots on and "
        + "nowhere else while it lives: sub_destruction's HEALTH 200 anchors on 'barracuda' and "
        + "roots on 'subgen_doors', so once sub_movement's subgen_doors_on has switched that block "
        + "into the world Resolve hands back the pool for it and nothing for sub_body, sub_body2, "
        + "sub_tower, sub_runway or sub_doors, which leaves a flak burst one splash recipient "
        + "instead of six and its authored six hits to the kill; before the surfacing the whole "
        + "boat is untouchable, and the wreck answers on every mesh again")]
    internal static void DestructibleSubHull(TestContext ctx)
    {
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission),
            $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var flak = WeaponDefs.Load(ctx.ZrdrPath, null).All
            .FirstOrDefault(w => w.Id.Equals(Flak, StringComparison.OrdinalIgnoreCase));
        ctx.Check(flak?.HealthDamage is { } hd && Mathf.IsEqualApprox(hd, 35f)
            && flak.ImpactProximity is > 0f,
            $"{Flak} FLAK authors HEALTH_DAMAGE {flak?.HealthDamage ?? -1f:0} over a {flak?.ImpactProximity ?? -1f:0} m blast");

        var report = new StringBuilder();
        ctx.WithWorld(Chapter, collision: false, Mission, world =>
        {
            var runtime = world.Runtime;
            var registry = runtime.Destructibles;
            var sub = runtime.FindNodes(Anchor).FirstOrDefault();
            ctx.Check(sub != null, $"{Chapter}/{Mission} builds the '{Anchor}' group");
            if (sub == null)
            {
                return;
            }

            var pool = registry.PoolsOn(sub).FirstOrDefault(p =>
                string.Equals(p.Def.AnimName, PoolAnim, StringComparison.OrdinalIgnoreCase));
            ctx.Check(pool != null && Mathf.IsEqualApprox(pool.MaxHealth, 200f),
                $"'{Anchor}' carries {PoolAnim}'s HEALTH 200 pool hp={pool?.MaxHealth ?? -1f:0}");
            var hangar = runtime.FindNodes(Hangar, sub).FirstOrDefault();
            ctx.Check(hangar != null, $"the boat carries its '{Hangar}' hangar block");
            if (pool == null || hangar == null)
            {
                return;
            }

            ctx.Check(ReferenceEquals(pool.DamageNode, hangar),
                $"the pool's damage node is '{Hangar}', not the '{Anchor}' anchor it is registered under");

            var hull = new List<Node3D>();
            foreach (string name in HullMeshes)
            {
                var mesh = runtime.FindNodes(name, sub).FirstOrDefault();
                ctx.Check(mesh != null, $"the boat carries its '{name}' mesh");
                if (mesh != null)
                {
                    hull.Add(mesh);
                }
            }

            ctx.Same(HullMeshes.Length, hull.Count, $"all five hull meshes resolve under '{Anchor}'");
            if (hull.Count != HullMeshes.Length)
            {
                return;
            }

            report.AppendLine($"{Chapter}/{Mission} {Anchor}: pool {pool.Def.AnimName} hp={pool.MaxHealth:0} "
                + $"anchor={AnimRuntime.NameOf(pool.Anchor)} damage node={AnimRuntime.NameOf(pool.DamageNode)}");
            report.AppendLine($"submerged: {Hangar} visible={hangar.Visible}; " + Answers(registry, hull, hangar));

            ctx.Check(!hangar.Visible,
                $"{Surfacing}'s RESET_STATE leaves '{Hangar}' switched off before the surfacing");
            ctx.Same(0, Answering(registry, hull).Count,
                $"no hull mesh reaches the pool while '{Hangar}' is out of the world, so the submerged boat is untouchable");

            var started = runtime.Play(Surfacing, sub);
            ctx.Check(started.Count > 0, $"'{Surfacing}' starts ({started.Count} instance(s))");
            float at = 0f;
            while (at < SurfacingSeconds && !hangar.Visible)
            {
                runtime.Advance(Tick);
                at += Tick;
            }

            ctx.Check(hangar.Visible,
                $"subgen_doors_on switches the hangar block into the world after {at:0.0} s of {Surfacing}");
            report.AppendLine($"surfaced at t={at:0.0} s: " + Answers(registry, hull, hangar));

            ctx.Check(ReferenceEquals(registry.Resolve(hangar), pool),
                $"a round on the hangar block answers with the submarine's own pool");
            var reached = Answering(registry, hull);
            ctx.Same(0, reached.Count,
                $"no hull mesh answers while the pool lives (reached: {(reached.Count == 0 ? "none" : string.Join(", ", reached))})");

            // One splash share per world OBJECT (Projectile.ApplyDamage dedups on
            // WorldCollision.OwnerOf), so the meshes that resolve ARE the recipients a burst over
            // the boat spends on. Six of them made the flak read three hits rather than its six.
            int recipients = Answering(registry, hull).Count + (registry.Resolve(hangar) != null ? 1 : 0);
            ctx.Same(1, recipients,
                $"a flak burst covering the whole boat has one recipient on it, not six");
            ctx.Same(6, Mathf.CeilToInt(pool.MaxHealth / (flak?.HealthDamage ?? 35f)),
                $"and {Flak}'s health term spends the 200 HP pool in six clean hits");

            bool landed = runtime.DamageAt(hangar, pool.MaxHealth + 1f);
            ctx.Check(landed && pool.Status == DestructibleRegistry.State.Destroyed,
                $"a lethal hit on the hangar block sinks the boat landed={landed} status={pool.Status}");
            ctx.Same(HullMeshes.Length, Answering(registry, hull).Count,
                $"the dead pool keeps its anchor claim: every hull mesh answers with it again");

            // Past the death's own node switching, which takes dbase off and swaps the hull.
            for (int i = 0; i < 120; i++)
            {
                runtime.Advance(1f / 60f);
            }

            ctx.Same(HullMeshes.Length, Answering(registry, hull).Count,
                $"and still does once the death has played out, which is what a hit on a wreck needs");
            report.AppendLine($"wreck: " + Answers(registry, hull, hangar));
        });

        ctx.WriteArtifact("test-destructible-sub-hull.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}'s Barracuda takes hits on '{Hangar}' alone while it lives, and everywhere once it is wreckage");
    }

    // The hull meshes a weapon hit currently reaches a pool through, by name.
    private static List<string> Answering(DestructibleRegistry registry, List<Node3D> hull) =>
        hull.Where(n => registry.Resolve(n) != null).Select(AnimRuntime.NameOf).ToList();

    private static string Answers(DestructibleRegistry registry, List<Node3D> hull, Node3D hangar)
    {
        var rows = hull.Concat(new[] { hangar }).Select(n =>
            $"{AnimRuntime.NameOf(n)}->{(registry.Resolve(n) is { } p ? p.Def.AnimName ?? p.Def.Name : "none")}");
        return string.Join(" ", rows);
    }
}
