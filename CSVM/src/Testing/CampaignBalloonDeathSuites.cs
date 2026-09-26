using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Godot;

namespace CSVM.Testing;

/// <summary>The suite over CM10's attack balloons as destructibles: the nine sites each carry two
/// weapon-hit pools on one group node, and a hit has to reach the one whose own animation root
/// covers the piece that was struck.</summary>
internal static class CampaignBalloonDeathSuites
{
    // CM10's story position, and the site the report was written against. Every node and animation
    // name below comes out of that mission's own data.
    private const int BalloonSeq = 9;
    private const int SiteCount = 9;
    private const string GroupPrefix = "lifesaver";
    private const string BalloonNode = "healthy_balloon";
    private const string BalloonRoot = "lifeballoon";
    private const string BoatNode = "lifeboat";
    private const string BalloonAnim = "lifefall";
    private const string BoatAnim = "lboat_destruction";

    // The two branches lifefallNM's lifeboat launch names, and which one fires says what the drop
    // struck. The water arm calls med_splash and the boat's own destruction pool; the dry default
    // calls that pool alone.
    private const string BoatBounce = "boat_explode";
    private const string BoatWetBounce = "boat_explode_water:";
    private const float BalloonHealth = 60f;
    private const float BoatHealth = 40f;

    // The bursting pieces the death flings, and how far one must travel before the launch counts
    // as real. They start at 10-16 m/s, so half a second clears this comfortably; a piece merely
    // re-posed at rest moves nothing at all.
    private const float PieceTravel = 2f;

    // How long the death is stepped for, and at what step. Past the pieces' launch and well
    // inside their 5 s run time, so nothing has faded out from under the assertion yet.
    private const float DeathSeconds = 1f;
    private const float Step = 1f / 60f;

    private static readonly string[] Pieces = { "b_part1", "b_part2", "b_part3", "b_part4", "b_part5", "b_part6" };

    /// <summary>CM10 (C1/M05)'s attack balloons over its BUILT world. Each <c>lifesaverNM</c> group
    /// carries the balloon's own <c>lifefallNM</c> pool and the lifeboat's <c>lboat_destructionNM</c>
    /// pool, so the suite asserts which one a hit on each half reaches, then kills a balloon through
    /// the hit path and reads the burst off the world: the envelope falls, the six pieces fly, the
    /// objective's own <c>healthy_balloon</c> ends hidden, and the lifeboat's downward launch stops
    /// on the sea it already floats on and takes its <c>boat_explode_water</c> branch.</summary>
    [Suite("campaign-balloon-death",
        "CM10's attack balloons as destructibles over C1/M05's BUILT world: each lifesaver "
        + "group node carries two weapon-hit pools, the balloon's lifefallNM at 60 HP and the "
        + "lifeboat's lboat_destructionNM at 40, so a hit on the envelope reaches the balloon's "
        + "own pool on all nine sites, and killing one flies its six bursting pieces, drops the "
        + "envelope, leaves healthy_balloon hidden for the objective graph, and lands the lifeboat's "
        + "own downward launch on the sea it already floats on, where it takes the water branch")]
    internal static void CampaignBalloonDeath(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), BalloonSeq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {BalloonSeq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder), $"{chapter}/{folder} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");

        var report = new StringBuilder();
        report.AppendLine($"seq {BalloonSeq} -> {chapter}/{folder}");
        // Collision on: the lifeboat's own drop is an untimed launch that only a contact tier can
        // end, so a mask-free world poses it at rest and the boat half of the death is unobservable.
        ctx.WithWorld(chapter, collision: true, folder, world => Drive(ctx, world, report));
        ctx.WriteArtifact($"test-campaign-balloon-death-{chapter}.txt", report.ToString());
        ctx.Note($"{chapter}/{folder}: a shot balloon reaches its own pool, bursts and falls");
    }

    private static void Drive(TestContext ctx, TestWorld world, StringBuilder report)
    {
        var runtime = world.Runtime;
        int routed = 0, paired = 0;
        Node3D? subject = null;
        for (int wave = 1; wave <= 3; wave++)
        {
            for (int index = 1; index <= 3; index++)
            {
                string site = $"{GroupPrefix}{wave}{index}";
                if (First(runtime.FindNodes(site)) is not { } group)
                {
                    continue;
                }

                subject ??= group;
                paired += Paired(ctx, runtime, site, group, report) ? 1 : 0;
                routed += Routed(ctx, runtime, site, group, report) ? 1 : 0;
            }
        }

        ctx.Same(SiteCount, paired, $"every attack-balloon site carries both weapon-hit pools its mission authors");
        ctx.Same(SiteCount, routed,
            $"and on every one of them a hit on the balloon reaches the balloon's own pool rather than the lifeboat's");
        EitherOrder(ctx, report);
        if (subject != null)
        {
            Burst(ctx, runtime, subject, report);
        }
    }

    // The routing must not depend on which of the two defs the archive lists first, and the shipped
    // pair happens to list the outer one first. Both orders, over a two-node stand-in.
    private static void EitherOrder(TestContext ctx, StringBuilder report)
    {
        foreach (bool outerFirst in new[] { true, false })
        {
            var outer = new Node3D { Name = "outer" };
            var inner = new Node3D { Name = "inner" };
            outer.AddChild(inner);
            var registry = new DestructibleRegistry();
            var onOuter = new AnimDefinition { Name = "outer", RootName = "outer", AnimName = "on_outer", Health = 40f };
            var onInner = new AnimDefinition { Name = "outer", RootName = "inner", AnimName = "on_inner", Health = 60f };
            if (outerFirst)
            {
                registry.Register(onOuter, outer, onOuter.Health, outer);
                registry.Register(onInner, outer, onInner.Health, inner);
            }
            else
            {
                registry.Register(onInner, outer, onInner.Health, inner);
                registry.Register(onOuter, outer, onOuter.Health, outer);
            }

            string atInner = registry.Resolve(inner)?.Def.AnimName ?? "nothing";
            string atOuter = registry.Resolve(outer)?.Def.AnimName ?? "nothing";
            report.AppendLine($"registered {(outerFirst ? "outer first" : "inner first")}: inner -> {atInner}, outer -> {atOuter}");
            ctx.Check(atInner == "on_inner" && atOuter == "on_outer",
                $"two pools on one anchor route by their own root whichever order they register in ({(outerFirst ? "outer first" : "inner first")}: {atInner} / {atOuter})");
            outer.Free();
        }
    }

    // The shape the routing question is asked of: one group node, two compiled pools, the balloon's
    // at HEALTH 60 and the lifeboat's at 40. Read off the registry, so a mission that stopped
    // authoring the pair fails here rather than silently making the next check vacuous.
    private static bool Paired(TestContext ctx, AnimRuntime runtime, string site, Node3D group,
        StringBuilder report)
    {
        var pools = runtime.Destructibles.PoolsOn(group);
        var balloon = PoolNamed(pools, BalloonAnim);
        var boat = PoolNamed(pools, BoatAnim);
        bool ok = balloon != null && boat != null
            && Mathf.Abs(balloon.MaxHealth - BalloonHealth) < 0.01f
            && Mathf.Abs(boat.MaxHealth - BoatHealth) < 0.01f;
        report.AppendLine($"{site}: {pools.Count} pool(s) on the group node ["
            + string.Join(", ", Named(pools)) + "]");
        ctx.Check(ok, $"'{site}' carries the balloon's own pool at {BalloonHealth} HP and the lifeboat's at {BoatHealth}");
        return ok;
    }

    // The routing itself: the struck node climbs to the pool whose animation root covers it. The
    // balloon's envelope belongs to `lifefallNM`, whose root is the balloon node; the lifeboat
    // belongs to `lboat_destructionNM`, whose root is the group.
    private static bool Routed(TestContext ctx, AnimRuntime runtime, string site, Node3D group,
        StringBuilder report)
    {
        var envelope = First(runtime.FindNodes(BalloonNode, group));
        var boat = First(runtime.FindNodes(BoatNode, group));
        if (envelope == null || boat == null)
        {
            ctx.Check(false, $"'{site}' builds both the balloon envelope and the lifeboat");
            return false;
        }

        var hitBalloon = runtime.Destructibles.Resolve(envelope);
        var hitBoat = runtime.Destructibles.Resolve(boat);
        report.AppendLine($"{site}: a hit on '{BalloonNode}' -> {Name(hitBalloon)}, on '{BoatNode}' -> {Name(hitBoat)}");
        bool ok = Is(hitBalloon, site, BalloonAnim) && Is(hitBoat, site, BoatAnim);
        string suffix = site[GroupPrefix.Length..];
        ctx.Check(ok, $"'{site}': the balloon answers to {BalloonAnim}{suffix} and the lifeboat to {BoatAnim}{suffix} (got {Name(hitBalloon)} / {Name(hitBoat)})");
        return ok;
    }

    // The burst, driven through the ordinary weapon-hit path on one site: the envelope falls, the
    // six pieces leave their rest poses, and the node the objective graph watches ends hidden.
    private static void Burst(TestContext ctx, AnimRuntime runtime, Node3D group, StringBuilder report)
    {
        var envelope = First(runtime.FindNodes(BalloonNode, group));
        var balloon = First(runtime.FindNodes(BalloonRoot, group));
        var boat = First(runtime.FindNodes(BoatNode, group));
        if (envelope == null || balloon == null || boat == null)
        {
            ctx.Check(false, $"the killed site builds its envelope, its balloon node and its lifeboat");
            return;
        }

        var rest = new Dictionary<string, Vector3>();
        foreach (string piece in Pieces)
        {
            if (First(runtime.FindNodes(piece, group)) is { } node)
            {
                rest[piece] = node.GlobalPosition;
            }
        }

        ctx.Same(Pieces.Length, rest.Count, $"the balloon's bursting pieces are built");
        float balloonRest = balloon.GlobalPosition.Y;
        float boatRest = boat.GlobalPosition.Y;

        // The lifeboat's drop is an untimed launch only a contact tier can end, so without the mask
        // a real session wires it is posed at rest and the boat half of the death is unobservable.
        uint maskWas = runtime.ContactMask;
        var waterWas = runtime.SurfaceIsWater;
        runtime.ContactMask = CollisionLayers.World;
        // The branch a landing picks is read off the surface it struck, so the sea has to be
        // recognisable here as a real session makes it. Without this the boat takes its dry
        // default over water and the suite cannot tell the two branches apart.
        runtime.SurfaceIsWater = body => ProjectilePool.SurfaceIsWater(body as Node);
        var fired = new List<string>();
        bool landed;
        try
        {
            runtime.OnEventDispatched = d => fired.Add($"{d.Sequence}:{d.EventKind}({d.EventName})");
            landed = runtime.DamageAt(envelope, BalloonHealth + 1f);
            for (float t = 0f; t < DeathSeconds; t += Step)
            {
                runtime.Advance(Step);
            }
        }
        finally
        {
            runtime.OnEventDispatched = null;
            runtime.SurfaceIsWater = waterWas;
            runtime.ContactMask = maskWas;
        }

        ctx.Check(landed, $"a hit on the balloon's envelope lands on a destructible");

        int moved = 0;
        var travels = new List<string>();
        foreach (var (piece, from) in rest)
        {
            float travel = First(runtime.FindNodes(piece, group)) is { } node
                ? node.GlobalPosition.DistanceTo(from)
                : 0f;
            travels.Add($"{piece} {travel:0.0} m");
            moved += travel > PieceTravel ? 1 : 0;
        }

        float fell = balloonRest - balloon.GlobalPosition.Y;
        float boatNow = boat.GlobalPosition.Y;
        var bounces = fired.FindAll(f => f.StartsWith(BoatBounce, StringComparison.Ordinal));
        report.AppendLine($"killed: pieces flew [{string.Join(", ", travels)}]; balloon fell {fell:0.0} m; "
            + $"boat y {boatRest:0.00} -> {boatNow:0.00}; bounce [{string.Join(", ", bounces)}]; "
            + $"'{BalloonNode}' visible={envelope.Visible}");
        ctx.Same(Pieces.Length, moved,
            $"every bursting piece leaves its rest pose, so the death is flown rather than swapped");
        ctx.Check(fell > 0.5f, $"and the balloon itself falls out of the sky rather than hanging where it died (fell {fell:0.0} m)");

        // The lifeboat's launch is a 1 m/s throw straight DOWN from a hull that already floats, so
        // it meets water at once and owes bounce_sequence.water. A distance fallen answers nothing
        // here: sinking through the sea travels furthest (docs/verification.md INSTR-38).
        ctx.Check(bounces.Exists(f => f.StartsWith(BoatWetBounce, StringComparison.Ordinal)),
            $"the lifeboat's drop strikes water and takes its {BoatWetBounce} branch fired=[{string.Join(", ", bounces)}]");
        ctx.Check(Mathf.Abs(boatNow - boatRest) < 1f,
            $"…leaving it afloat where it was rather than sinking through the sea (y {boatRest:0.00} -> {boatNow:0.00})");
        ctx.Check(!envelope.Visible,
            $"and '{BalloonNode}' ends hidden, which is what retires the site's own objective");
    }

    private static bool Is(DestructibleRegistry.Instance? inst, string site, string anim) =>
        inst != null && inst.Def.AnimName is { } name
        && string.Equals(name, anim + site[GroupPrefix.Length..], StringComparison.OrdinalIgnoreCase);

    private static DestructibleRegistry.Instance? PoolNamed(List<DestructibleRegistry.Instance> pools, string prefix)
    {
        foreach (var pool in pools)
        {
            if (pool.Def.AnimName is { } name && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return pool;
            }
        }

        return null;
    }

    private static IEnumerable<string> Named(List<DestructibleRegistry.Instance> pools)
    {
        foreach (var pool in pools)
        {
            yield return $"{pool.Def.AnimName}@{pool.MaxHealth:0}";
        }
    }

    private static string Name(DestructibleRegistry.Instance? inst) => inst?.Def.AnimName ?? "nothing";

    private static Node3D? First(IReadOnlyList<Node3D> found) => found.Count > 0 ? found[0] : null;

    private static CampaignMission? MissionOf(IReadOnlyList<CampaignMission> missions, int seq)
    {
        foreach (var mission in missions)
        {
            if (mission.Seq == seq)
            {
                return mission;
            }
        }

        return null;
    }
}
