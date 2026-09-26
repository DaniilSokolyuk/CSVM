using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Launch;
using CSVM.Session.Roster;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>The pool the load screen fills, on both sides of the seam it exists for: the
/// counting rules on their own (order, cap, build, claim, refill, discard), and over a built world
/// the one claim that matters, that an aeroplane taken from the pool is the aeroplane an in-place
/// build would have produced. The second is what lets the goldens keep flying AI aircraft: if the
/// claimed tree differed by a node, every shot with an AI aeroplane in it would move.</summary>
internal static class AiAirframePoolSuites
{
    private const string Chapter = "C4";
    private const string Mission = "M03";
    private const string Generator = "cargozep1";

    // Pumps to allow the refill: the two launches below each queue a crash rig, and the pool only
    // refills on a frame carrying neither a rig step nor a join.
    private const int RefillPumpCap = 200;

    [Suite("ai-airframe-pool",
        "the wave aeroplane pool's own counting, with a stand-in build: an order names an airframe "
        + "and a livery and raises that slot's depth up to its cap, a build serves the slots in "
        + "order order, a claim empties one and is counted, a claim on an ordered but empty slot is "
        + "a miss and a claim on a slot nobody ordered is neither, a refill builds exactly what the "
        + "claims took, and a discard frees what is left and forgets the orders")]
    internal static void PoolCounting(TestContext ctx)
    {
        var made = new List<Node3D>();
        var pool = new AiAirframePool((plane, scheme) =>
        {
            // The builder is never read by the pool, only handed back to the claim, so the counting
            // suite hands it nothing rather than building an aircraft to count with.
            var model = new Node3D { Name = $"{plane}_{made.Count}" };
            made.Add(model);
            return new AiAirframePool.Prepared(null!, model, null, null, null, null);
        });

        var claimed = new List<AiAirframePool.Prepared>();
        try
        {
            pool.Order("fury", null, 2, 3);
            pool.Order("fury", null, 2, 3);
            pool.Order("kestrel", null, 1, 3);
            ctx.Same(4, pool.Owed, $"two orders on one airframe stack, capped, and a second airframe adds its own");
            ctx.Same(0, pool.Ready, $"ordering builds nothing by itself");

            int builds = 0;
            while (pool.BuildOne())
            {
                builds++;
            }

            ctx.Same(4, builds, $"the load screen builds exactly what was ordered");
            ctx.Same(0, pool.Owed, $"and then owes nothing");
            ctx.Same(4, pool.Ready, $"with all four waiting to be claimed");
            ctx.Check(made[0].Name.ToString() == "fury_0" && made[3].Name.ToString() == "kestrel_3",
                $"slots are served in the order they were ordered in, first '{made[0].Name}', last '{made[3].Name}'");

            for (int i = 0; i < 3; i++)
            {
                var taken = pool.Claim("fury", null);
                ctx.Check(taken != null, $"claim {i + 1} of the Fury's three took a prepared aeroplane");
                if (taken != null)
                {
                    claimed.Add(taken);
                }
            }

            ctx.Check(pool.Claim("fury", null) == null, $"a fourth claim finds the slot empty");
            ctx.Same(1, pool.Misses, $"and is counted a miss, since the slot was ordered");
            ctx.Check(pool.Claim("spitfire", null) == null, $"a claim on an airframe nobody ordered finds nothing");
            ctx.Same(1, pool.Misses, $"and is no miss: the pool was never asked to hold that one");
            ctx.Same(3, pool.Claims, $"the claims are counted");
            ctx.Same(3, pool.Owed, $"what the claims took is owed again");

            ctx.Check(pool.BuildOne(), $"a quiet frame refills one");
            ctx.Same(2, pool.Owed, $"and only one");

            pool.Discard();
            ctx.Same(0, pool.Ready, $"a discard keeps nothing");
            ctx.Same(0, pool.Owed, $"and forgets the orders, so a torn-down session owes nothing");
            ctx.Check(pool.Claim("fury", null) == null, $"and a claim after it finds no slot at all");
        }
        finally
        {
            foreach (var prepared in claimed)
            {
                prepared.Model.Free();
            }
        }
    }

    [Suite("ai-airframe-pool-claim",
        "that a claimed aeroplane is the aeroplane an in-place build would have made: over C4/M03's "
        + "built world one wave aeroplane is ordered and built as the load screen does, then two "
        + "launch off the same roster block, the first claiming it and the second building in place "
        + "because the slot is empty. Their model trees, node for node, and their collision hulls "
        + "are compared, the hull resources are held to be the one shared set, and the pool is "
        + "shown refilling on the quiet frames after the launches")]
    internal static void PoolClaimMatchesBuild(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");
        ctx.RequireData(texturesPath, $"{Chapter} textures");

        var defs = EnemyGenerators.Load(missionZrdr);
        ctx.WithWorld(Chapter, collision: false, Mission,
            world => Compare(ctx, world, defs, missionZrdr, chapterZrdr, texturesPath));
    }

    // One roster, two launches off one block: the first claims, the second misses. Everything the
    // comparison needs is read off the two controllers, so nothing here models the assembly.
    private static void Compare(TestContext ctx, TestWorld world, IReadOnlyList<EnemyGeneratorDef> defs,
        string missionZrdr, string chapterZrdr, string texturesPath)
    {
        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var nets = AiNets.Load(chapterZrdr);
        var templates = CampaignRosterPlan.GeneratorTemplates(
            missionZrdr, VehicleDefs.Load(ctx.ZrdrPath), nets);
        var spec = SessionSpec.Parse(Array.Empty<string>());
        var positions = new List<Vector3>();
        ProjectilePool? pool = null;
        FlightRoster? roster = null;
        var flown = new List<FlightController>();
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            var built = new FlightRoster(FlightRosterPolicy.From(spec),
                new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
                new WorldEffectsFactory(spec, ctx.Host, () => Vector3.Zero), ctx.Host,
                SuiteConstants.AircraftResources(ctx, planesGamez, textures,
                    Messages.Load(ctx.MessagesPath)),
                new FlightWorldBindings
                {
                    Projectiles = live,
                    Gamez = world.Gamez,
                    WorldScene = world.Session.Builder.Scene,
                    CrashProgram = world.Session.Program,
                    ChapterZrdrPath = chapterZrdr,
                    MissionZrdrPath = missionZrdr,
                    HumanPositions = () => positions,
                },
                new HumanRosterBindings { RigCount = 1 });
            roster = built;

            GameSession.OrderWaveAirframes(built, defs, templates);
            ctx.Check(built.OwedAirframes > 0, $"{Chapter}/{Mission}'s generators order at least one wave aeroplane");
            int prebuilt = 0;
            while (built.BuildOrderedAirframe())
            {
                prebuilt++;
            }

            ctx.Check(prebuilt > 0, $"the load screen builds them, {prebuilt} aeroplane(s), before the first frame");
            ctx.Same(0, built.OwedAirframes, $"and leaves nothing owed");
            built.PumpDeferredCrashRigs();

            var plan = PlanFor(defs, templates);
            ctx.Check(plan != null, $"'{Generator}' launches from a roster block template");
            if (plan == null)
            {
                return;
            }

            var pilot = AiPilot.HoldingCourse(new Vector3(0f, 900f, 0f), Vector3.Forward);
            var claimedPlane = built.SpawnAi(CampaignRosterPlan.SpawnFor(
                plan, new Vector3(0f, 900f, 0f), Vector3.Forward, pilot, "poolclaim1"));
            flown.Add(claimedPlane);
            ctx.Same(1, built.AirframeClaims.Claims, $"the first launch flies the aeroplane the load screen built");
            ctx.Same(0, built.AirframeClaims.Misses, $"and builds nothing of its own");

            var inPlace = built.SpawnAi(CampaignRosterPlan.SpawnFor(
                plan, new Vector3(200f, 900f, 0f), Vector3.Forward, pilot, "poolclaim2"));
            flown.Add(inPlace);
            ctx.Same(1, built.AirframeClaims.Misses, $"the second finds the slot empty and builds in place");

            string claimedTree = Tree(claimedPlane.PlaneModel);
            string builtTree = Tree(inPlace.PlaneModel);
            ctx.Check(claimedTree == builtTree,
                $"a claimed aeroplane's model tree is the tree an in-place build makes, {Lines(claimedTree)} node(s) against {Lines(builtTree)}{Difference(claimedTree, builtTree)}");
            ctx.Check(claimedPlane.Collider?.Summary == inPlace.Collider?.Summary,
                $"and carries the same collision hulls: '{claimedPlane.Collider?.Summary}'");
            ctx.Check(claimedPlane.Collider is { Parts.Count: > 0 } hulls && inPlace.Collider is { } second
                && ReferenceEquals(hulls.Parts[0].Shape, second.Parts[0].Shape),
                $"which are the one shared set, built once for the airframe (PERF-22)");

            int pumps = 0;
            while (built.ReadyAirframes == 0 && pumps < RefillPumpCap)
            {
                built.PumpDeferredCrashRigs();
                pumps++;
            }

            ctx.Check(built.ReadyAirframes > 0,
                $"the quiet frames after a wave refill the pool, ready again after {pumps} pump(s)");
            ctx.Note($"{prebuilt} aeroplane(s) built behind the load screen, {built.AirframeClaims.Claims} claimed, {built.AirframeClaims.Misses} missed, refilled after {pumps} pump(s)");
        }
        finally
        {
            roster?.ClearMembership();
            foreach (var controller in flown)
            {
                controller.Free();
            }

            if (pool != null)
            {
                ctx.Host.RemoveChild(pool);
                pool.QueueFree();
            }

            textures.Dispose();
        }
    }

    private static int Lines(string tree) => tree.Split('\n').Length - 1;

    // The first line the two trees differ on, so a failure names the node rather than the count.
    private static string Difference(string left, string right)
    {
        if (left == right)
        {
            return string.Empty;
        }

        var a = left.Split('\n');
        var b = right.Split('\n');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
            {
                return $", first difference at line {i + 1}: '{a[i].Trim()}' against '{b[i].Trim()}'";
            }
        }

        return ", one tree runs on past the other";
    }

    private static RosterSpawnPlan? PlanFor(IReadOnlyList<EnemyGeneratorDef> defs,
        IReadOnlyDictionary<string, RosterSpawnPlan> templates)
    {
        foreach (var def in defs)
        {
            if (!def.Node.Equals(Generator, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (CampaignRosterPlan.ResolveGeneratorLaunch(templates, def.VehicleParams, out var plan)
                == GeneratorLaunch.Template)
            {
                return plan;
            }
        }

        return null;
    }

    // The tree as text: every node's name, type, visibility and surface count, depth first. A
    // difference anywhere in the built model shows up as a differing line rather than as a golden.
    private static string Tree(Node? root)
    {
        var text = new StringBuilder();
        void Walk(Node node, int depth)
        {
            // An unnamed node wears Godot's own @Type@<counter> name, and that counter is the
            // process's, so it differs between two identical trees and says nothing about either.
            string name = node.Name.ToString();
            text.Append(' ', depth * 2)
                .Append(name.StartsWith('@') ? "<unnamed>" : name)
                .Append(' ').Append(node.GetType().Name);
            if (node is GeometryInstance3D geo)
            {
                text.Append(geo.Visible ? " visible" : " hidden");
            }

            if (node is MeshInstance3D mesh)
            {
                text.Append(" surfaces=").Append(mesh.Mesh?.GetSurfaceCount() ?? 0);
            }

            text.Append('\n');
            foreach (var child in node.GetChildren())
            {
                Walk(child, depth + 1);
            }
        }

        if (root != null)
        {
            Walk(root, 0);
        }

        return text.ToString();
    }
}
