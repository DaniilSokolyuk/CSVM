using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The Dante's engine deaths on C5/M04, read as effect siting. Every engine's
/// <c>destroy_dtz?engNN</c> calls <c>large_30sec_fire</c> WITH its own <c>supports</c> node and
/// nothing stops it before the authored 30 s, so a hull whose engine bank has been shot through
/// owes one fire per dead engine, each at its own nacelle. The count that must not collapse is
/// therefore the engine bank's size, and the shipped <c>fire_here</c> pool is what decides
/// whether it does (effect_pools.json).</summary>
internal static class DanteEngineFireSuites
{
    private const float Dt = 1f / 60f;

    // The Dante's whole engine bank, the concurrency one hull authors. Every one of these carries
    // its own destroy_dtz?engNN, and the gasbag burns work through them four at a time.
    private static readonly string[] Engines =
    {
        "leng11", "leng12", "leng21", "leng22", "leng31", "leng32", "leng42",
        "reng11", "reng12", "reng21", "reng22", "reng31", "reng32", "reng42",
    };

    [Suite("dante-engine-fires",
        "every engine of C5/M04's Dante killed in turn keeps its own large_30sec_fire on its own " +
        "supports node: the calls route WITH_NODE to the world-effects stage staged at the " +
        "shipped fire_here pool depth, each fire is fed within a metre of the engine it belongs " +
        "to, and no call takes a still-burning engine's template copy back")]
    internal static void DanteEngineFires(TestContext ctx)
    {
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, "C5", "M04"), $"C5/M04 zrdr");
        // The size the session ships, not a number of this suite's own: the claim under test is
        // that the shipped pool covers one hull's authored engine bank.
        int slots = EffectPools.Load().SlotsFor("fire_here", 1);
        var report = new StringBuilder();
        report.AppendLine($"fire_here pool depth {slots} for {Engines.Length} engine(s)");
        ctx.WithWorld("C5", collision: false, mission: "M04", world =>
        {
            var runtime = world.Runtime;
            var dante = runtime.FindNodes("dantezep").FirstOrDefault();
            ctx.Check(dante != null, $"dantezep resolves in the C5/M04 world");
            if (dante == null)
                return;
            // Nine zeppelins share these node names, so a fire landing on the right engine is a
            // statement about the compiled symbol table, not about a name search.
            ctx.Check(runtime.FindNodes("supports").Count > Engines.Length,
                $"the world holds {runtime.FindNodes("supports").Count} nodes named supports, so a name search is ambiguous");

            var sites = new Dictionary<string, Node3D>();
            var pools = new Dictionary<string, DestructibleRegistry.Instance>();
            foreach (var name in Engines)
            {
                var eng = runtime.FindNodes(name, dante).FirstOrDefault();
                var supports = eng == null ? null : runtime.FindNodes("supports", eng).FirstOrDefault();
                var pool = eng == null ? null : runtime.Destructibles.PoolsOn(eng).FirstOrDefault();
                if (supports != null)
                    sites[name] = supports;
                if (pool != null)
                    pools[name] = pool;
            }
            ctx.Same(Engines.Length, sites.Count, $"every engine resolves its own supports node");
            ctx.Same(Engines.Length, pools.Count, $"every engine carries its own destroy pool");
            if (sites.Count != Engines.Length || pools.Count != Engines.Length)
                return;

            var factory = new CountingEmitterFactory();
            EffectStageSuiteHelper.WithEffectStage(ctx, world, "large_30sec_fire", new[] { "fire_here" },
                (_, effects, _) =>
                {
                    var previous = runtime.ExternalEffect;
                    var routed = new List<(string Site, Vector3 At)>();
                    runtime.ExternalEffect = (name, pt, node, follow) =>
                    {
                        if (name == "large_30sec_fire")
                            routed.Add((node != null ? AnimRuntime.NameOf(node) : "-", pt));
                        return effects.Handles(name) && effects.PlayEffectAt(name, pt, node, follow: follow);
                    };
                    try
                    {
                        foreach (var name in Engines)
                        {
                            runtime.DamageAt(pools[name].Anchor, pools[name].MaxHealth + 1f);
                            for (int i = 0; i < 6; i++)
                            {
                                runtime.Advance(Dt);
                                effects.Advance(Dt);
                            }
                        }

                        ctx.Same(Engines.Length, routed.Count,
                            $"each engine death routed one large_30sec_fire to the effects stage");
                        ctx.Check(routed.All(r => r.Site == "supports"),
                            $"every routed call named a supports node [{string.Join(",", routed.Select(r => r.Site).Distinct())}]");
                        ctx.Same(0, effects.PoolRecycles,
                            $"no call took a still-burning engine's template copy back (fire_here depth {slots})");

                        // The whole bank, at one instant: every engine's own site must have a fire
                        // being fed at it, and no two engines may share one.
                        var live = factory.Built
                            .Where(e => e.Key == "fire_n_smoke" && e.Sustaining)
                            .ToList();
                        ctx.Same(Engines.Length, live.Count, $"one live fire per dead engine");
                        var claimed = new HashSet<CountingEmitter>();
                        foreach (var name in Engines)
                        {
                            var want = AnimRuntime.VisualOriginOf(sites[name]);
                            var mine = live.FirstOrDefault(e => !claimed.Contains(e)
                                                                && e.LastPos.DistanceTo(want) < 1f);
                            if (mine != null)
                                claimed.Add(mine);
                            var got = mine != null
                                ? string.Format(CultureInfo.InvariantCulture, "({0:0.#}, {1:0.#}, {2:0.#})",
                                    mine.LastPos.X, mine.LastPos.Y, mine.LastPos.Z)
                                : "NOTHING";
                            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                                "{0}: want ({1:0.#}, {2:0.#}, {3:0.#}) got {4}", name, want.X, want.Y, want.Z, got));
                            ctx.Check(mine != null,
                                $"{name} keeps its own fire at its own supports node (wanted {want.X:0.#},{want.Y:0.#},{want.Z:0.#})");
                        }
                    }
                    finally
                    {
                        runtime.ExternalEffect = previous;
                    }
                }, factory, slots);
        });
        ctx.WriteArtifact("test-dante-engine-fires.txt", report.ToString());
        ctx.Note($"C5/M04's Dante: {Engines.Length} engines, {Engines.Length} fires, {slots} fire_here copies");
    }
}
