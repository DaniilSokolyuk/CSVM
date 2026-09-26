using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using Godot;

namespace CSVM.Testing;

/// <summary>CM05 (C3/M04)'s authored instant loss over its own BUILT world. The mission counts the
/// Pandora's twelve engine nacelles at one, two, four and six out through four objectives that nap
/// each other in turn; the six-nacelle rung hands OBJECTIVE10 a 45 s nap, and that objective
/// authors INSTANTLOSS with no completion condition of its own, so waking out of the nap loses the
/// mission. Nothing else in the file reaches OBJECTIVE10, which makes the nacelle count the whole
/// loss condition. Decode: docs/formats/objectives.md, "Worked example: CM05".</summary>
internal static class CampaignPandoraLossSuites
{
    private const string Chapter = "C3";
    private const string Mission = "M04";
    private const string Hull = "piratezep";

    // The rung that is awake at mission start (the mission authors it no BEGIN_DORMANT), the last
    // rung, and the count that last rung needs.
    private const int FirstRung = 6;
    private const int LastRung = 9;
    private const int LastRungCount = 6;

    // The loss objective and the nap length that is its only route to being awake.
    private const int LossObjective = 10;
    private const float LossNap = 45f;

    // Each nacelle's own destroy def: a WeaponHit destructible of this many HP whose death switches
    // the engine's healthy model off, which is the node every rung's INACTIVE paths read.
    private const float NacelleHealth = 40f;

    private const float Tick = 0.25f;

    // How long the chain is given to run its three 1 s naps out after a kill, and the slack the
    // fuse is allowed over its authored length (the completion tick plus the 0.1 s wrap-up).
    private const float ChainSeconds = 12f;
    private const float FuseSlack = 1f;

    // The Brigand roster blocks that attack the hull, and the target-selection bias each carries
    // onto it. Nothing caps how many nacelles one attacker may take, which is the item's question.
    private const string BrigandPrefix = "medbrigand";
    private const float BrigandBias = 0.5f;

    // The shared crate destructible, and the C3 node that carries no healthy model of its own.
    // The pair shows a definition the world holds several instances of writing outside one.
    private const string CrateFile = "box_call_generic.zrd.json";
    private const string LakeNode = "craterlake";

    // The hull's seventeen turrets, each behind its own <turret>/healthy model, the census a stray
    // `healthy INACTIVE` write switches off in one go.
    private static readonly string[] Turrets =
    {
        "rtur1", "rtur2", "rtur3", "rtur4", "rtur5", "rtur6",
        "ltur1", "ltur2", "ltur3", "ltur4", "ltur5", "ltur6",
        "ctur1", "ctur2", "ctur3", "doublecannon4", "doublecannon5",
    };

    // The twelve INACTIVE paths every rung carries, in the file's own order.
    private static readonly string[] Nacelles =
    {
        "reng11", "reng12", "reng21", "reng22", "reng31", "reng32",
        "leng11", "leng12", "leng21", "leng22", "leng31", "leng32",
    };

    // The chain as the file authors it: the rung, the nacelle count it completes on, the objective
    // its completion naps, and that nap's length.
    private static readonly (int Number, int Count, int Naps, float Seconds)[] Rungs =
    {
        (6, 1, 7, 1f),
        (7, 2, 8, 1f),
        (8, 4, 9, 1f),
        (9, 6, 10, 45f),
    };

    /// <summary>Drives CM05's zeppelin-damage chain over the mission's BUILT world. The twelve
    /// nacelles all start switched on behind their own 40 HP pools. Five out leave the loss
    /// objective dormant, the sixth naps it for 45 s, and the mission is lost when the nap runs
    /// out. The crate leg first shows what must NOT arm the chain: a shared destructible dying
    /// elsewhere with no healthy model under it.</summary>
    [Suite("campaign-pandora-loss",
        "CM05 (C3/M04)'s authored instant loss over its own BUILT world: the Pandora's twelve "
        + "engine nacelles start switched on, each behind its own 40 HP WeaponHit pool, and the "
        + "mission's rungs count them out at one, two, four and six. Five nacelles down leave "
        + "OBJECTIVE10 dormant with the mission running; the sixth completes the last rung, which "
        + "naps OBJECTIVE10 for the authored 45 s, and since that objective is INSTANTLOSS with no "
        + "condition of its own the mission is lost when the nap runs out, not on the kill. Nothing "
        + "else in the script reaches OBJECTIVE10, so the nacelle count is the whole loss condition. "
        + "The shared crate destructible, killed first on a node carrying no healthy model of its "
        + "own, leaves all twelve nacelles lit and all seventeen hull turrets alive, since a "
        + "definition the world holds several instances of writes inside its own instance or nowhere")]
    internal static void CampaignPandoraLoss(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, Chapter), $"{Chapter} textures");

        var script = ObjectiveScript.Load(missionZrdr);
        var report = new StringBuilder();
        report.AppendLine($"{Chapter}/{Mission}: {script.Objectives.Count} objectives");
        CheckScript(ctx, script, report);
        CheckAttackers(ctx, missionZrdr, report);

        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath))
            ?? throw new SuiteSkippedException($"cm_sequence carries no {Chapter}/{Mission}");
        var director = CampaignDirector.Create(script, mission,
            CampaignProfileDef.NewProfile("Zachary"), null, missionZrdr);
        ctx.ExtraPrewarmSoundNames = script.SoundGroupNames();
        ctx.WithWorld(Chapter, collision: false, Mission, world =>
            Drive(ctx, world, director, report));

        ctx.WriteArtifact("test-campaign-pandora-loss.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: six nacelles out arm the {LossNap:0} s fuse on OBJECTIVE{LossObjective}");
    }

    // What the shipped script authors, before any world is built: the loss objective's own shape,
    // every directive in the file that reaches it, and the four rungs that count the nacelles.
    private static void CheckScript(TestContext ctx, ObjectiveScript script, StringBuilder report)
    {
        if (Def(script, LossObjective) is not { } loss)
        {
            ctx.Check(false, $"the mission authors OBJECTIVE{LossObjective}");
            return;
        }

        ctx.Check(loss.InstantLoss && !loss.HasConditions,
            $"OBJECTIVE{LossObjective} is INSTANTLOSS with no completion condition, so being awake is enough to lose");
        ctx.Check(loss.BeginDormant && loss.DormantUntil < 0f,
            $"and BEGIN_DORMANT -1, so it never wakes on a clock of its own");
        ctx.Same(1, script.Objectives.Count(d => d.InstantLoss),
            $"and it is the mission's only INSTANTLOSS, so no other objective can lose it");

        var reaches = new List<string>();
        foreach (var def in script.Objectives)
        {
            if (def.WakeWhenComplete.Contains(LossObjective))
            {
                reaches.Add($"OBJECTIVE{def.Number} WAKE_WHEN_I_COMPLETE");
            }

            if (def.WakeWhenSleep.Contains(LossObjective))
            {
                reaches.Add($"OBJECTIVE{def.Number} WAKE_WHEN_I_SLEEP");
            }

            if (def.NapWhenComplete is { } nap && nap.Target == LossObjective)
            {
                reaches.Add($"OBJECTIVE{def.Number} NAP_WHEN_I_COMPLETE {nap.Seconds:0.#}s");
            }
        }

        report.AppendLine($"OBJECTIVE{LossObjective} reached by: {string.Join(", ", reaches)}");
        ctx.Same(1, reaches.Count,
            $"exactly one directive in the whole file reaches OBJECTIVE{LossObjective} ({string.Join(", ", reaches)})");
        CheckRungs(ctx, script, report);
    }

    // The escalation chain: four rungs over the same twelve nacelle paths, each completing at its
    // own count and napping the next, with the last one's nap the fuse under the loss.
    private static void CheckRungs(TestContext ctx, ObjectiveScript script, StringBuilder report)
    {
        foreach (var (number, count, naps, seconds) in Rungs)
        {
            if (Def(script, number) is not { } def)
            {
                ctx.Check(false, $"the mission authors OBJECTIVE{number}");
                continue;
            }

            string paths = string.Join(",", def.Inactive.Select(p => p.Count == 3 ? p[1] : "?"));
            report.AppendLine($"OBJECTIVE{number}: count {def.InactiveCount} of {def.Inactive.Count} "
                + $"[{paths}] naps {def.NapWhenComplete?.Target} for {def.NapWhenComplete?.Seconds:0.#}s, "
                + $"dormant={def.BeginDormant}");
            ctx.Same(Nacelles.Length, def.Inactive.Count,
                $"OBJECTIVE{number} watches all twelve of the Pandora's engine nacelles");
            ctx.Check(def.Inactive.All(WatchesANacelle),
                $"and every entry is that hull's own <engine>/healthy path rather than another node");
            ctx.Same(count, def.InactiveCount ?? def.Inactive.Count,
                $"OBJECTIVE{number} completes at {count} of them switched off");
            ctx.Check(def.NapWhenComplete is { } nap && nap.Target == naps
                    && Mathf.IsEqualApprox(nap.Seconds, seconds),
                $"and its completion naps OBJECTIVE{naps} for {seconds:0.#} s, the next rung");
            if (number == FirstRung)
            {
                ctx.Check(!def.BeginDormant,
                    $"OBJECTIVE{number} is awake from mission start, so the first nacelle out counts");
            }
            else
            {
                ctx.Check(def.BeginDormant && def.DormantUntil < 0f,
                    $"OBJECTIVE{number} only ever wakes out of the rung below it");
            }
        }
    }

    // Why one attacker is enough to reach the last rung on its own: the mission flies five Brigand
    // blocks, each authored with a positive target-selection bias onto the hull, against 6 x 40 HP.
    private static void CheckAttackers(TestContext ctx, string missionZrdr, StringBuilder report)
    {
        int biased = 0, blocks = 0;
        foreach (var (name, fields) in AiSkills.LoadRoster(missionZrdr))
        {
            if (!name.StartsWith(BrigandPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            blocks++;
            var biases = AiSkills.RosterRatingBiases(fields);
            var onHull = biases.FirstOrDefault(b =>
                b.Pattern.Equals(Hull, StringComparison.OrdinalIgnoreCase));
            biased += onHull != null && onHull.Bias >= BrigandBias ? 1 : 0;
            report.AppendLine($"{name}: biases [{string.Join(",", biases.Select(b => $"{b.Pattern} {b.Bias:0.##}"))}]");
        }

        report.AppendLine($"{biased} of {blocks} {BrigandPrefix}* blocks carry a +{BrigandBias:0.0#} bias "
            + $"onto {Hull}, against {LastRungCount * NacelleHealth:0} HP of nacelle to reach the last rung");
        ctx.Check(blocks > 0 && biased == blocks,
            $"every {BrigandPrefix}* block the mission flies is weighted onto the {Hull} hull, so one of them attacks it unprompted");
    }

    private static void Drive(TestContext ctx, TestWorld world, CampaignDirector director,
        StringBuilder report)
    {
        var runtime = world.Runtime;
        director.Attach(new CampaignDirector.WorldInputs
        {
            Runtime = runtime,
            Sounds = runtime.Sounds,
            Rng = new Random(1),
        });
        var graph = director.Graph!;
        var completed = new List<int>();
        graph.Completed += c => completed.Add(c.Number);
        graph.Transitioned += t =>
            report.AppendLine($"  t={t.Elapsed:0.00}s OBJECTIVE{t.Number} {t.Kind} (by {t.Source}, {t.Seconds:0.#}s)");
        graph.Step(Tick);

        var host = runtime.FindNodes(Hull).FirstOrDefault();
        ctx.Check(host != null, $"the {Hull} hull builds in the {Chapter}/{Mission} world");
        if (host == null)
        {
            return;
        }

        var healthy = Nacelles.Select(engine => Healthy(runtime, host, engine)).ToList();
        int lit = healthy.Count(h => h is { Visible: true });
        int pooled = healthy.Count(h => h != null
            && runtime.Destructibles.Resolve(h) is { } pool
            && Mathf.IsEqualApprox(pool.MaxHealth, NacelleHealth)
            && host.IsAncestorOf(pool.Anchor));
        report.AppendLine($"{healthy.Count(h => h != null)} of {Nacelles.Length} nacelle healthy models "
            + $"resolve, {lit} switched on, {pooled} behind a {NacelleHealth:0} HP pool of their own");
        ctx.Same(Nacelles.Length, lit,
            $"every one of the Pandora's twelve nacelles starts switched ON, so the chain is unarmed at mission start");
        ctx.Same(Nacelles.Length, pooled,
            $"and each stands behind its own {NacelleHealth:0} HP pool inside the hull, which is what a weapon hit reaches");
        if (lit != Nacelles.Length || pooled != Nacelles.Length)
        {
            return;
        }

        CheckCrateDeathStaysOffTheHull(ctx, runtime, host, healthy, report);
        CheckFiveIsNotEnough(ctx, runtime, graph, healthy, completed, report);
        CheckSixthArmsTheFuse(ctx, runtime, graph, healthy, report);
    }

    // The shared crate destructible killed on a node carrying no healthy model. Its NAME wildcard
    // makes it one of several instances of one authored definition, so the write resolves inside
    // its own instance or nowhere. Decode: docs/org/sequences.md, "Where CSVM stands against this".
    private static void CheckCrateDeathStaysOffTheHull(TestContext ctx, AnimRuntime runtime,
        Node3D host, IReadOnlyList<Node3D?> healthy, StringBuilder report)
    {
        var crate = AnimDefs.LoadFileDefs(ctx.ZrdrPath, CrateFile)
            .FirstOrDefault(d => d.Name.Contains('*'));
        var lake = runtime.FindNodes(LakeNode).FirstOrDefault();
        if (crate == null || lake == null)
        {
            ctx.Check(false, $"'{CrateFile}' and '{LakeNode}' both resolve for the {Chapter} world");
            return;
        }

        ctx.Same(0, runtime.FindNodes("healthy", lake).Count,
            $"'{LakeNode}' carries no healthy model, which is what makes the '{crate.Name}' write miss");
        ctx.Same(Turrets.Length, TurretsAlive(runtime, host),
            $"all {Turrets.Length} of the hull's turrets show their healthy model before the crate dies");

        runtime.Destructibles.Register(crate, lake, crate.Health, lake);
        ctx.Check(runtime.DamageAt(lake, crate.Health + 1f),
            $"the shared '{crate.Name}' destructible takes the hit on '{LakeNode}' and dies");

        int lit = healthy.Count(h => h is { Visible: true });
        int turrets = TurretsAlive(runtime, host);
        report.AppendLine($"'{crate.Name}' killed on '{LakeNode}': {lit} of {Nacelles.Length} nacelle "
            + $"healthy models still on, {turrets} of {Turrets.Length} turrets still alive");
        ctx.Same(Nacelles.Length, lit,
            $"its healthy write reaches no engine nacelle, so the rung chain is still unarmed");
        ctx.Same(Turrets.Length, turrets,
            $"and no turret on the hull loses its healthy model to another instance's death");
    }

    // How many of the hull's turrets still show a healthy model of their own.
    private static int TurretsAlive(AnimRuntime runtime, Node3D host) =>
        Turrets.Count(t => Healthy(runtime, host, t) is { Visible: true });

    // Five nacelles down: the first three rungs complete and the last one wakes, but its own count
    // is six, so the loss objective is still dormant and the mission is still running.
    private static void CheckFiveIsNotEnough(TestContext ctx, AnimRuntime runtime,
        ObjectiveGraph graph, IReadOnlyList<Node3D?> healthy, List<int> completed, StringBuilder report)
    {
        for (int i = 0; i < LastRungCount - 1; i++)
        {
            ctx.Check(runtime.DamageAt(healthy[i], NacelleHealth + 1f),
                $"{Nacelles[i]} takes the hit on its own pool");
            Run(runtime, graph, ChainSeconds / 4f, advanceWorld: true, () => graph.StateOf(LastRung) == ObjectiveState.Awake);
        }

        Run(runtime, graph, ChainSeconds, advanceWorld: true, () => graph.StateOf(LastRung) == ObjectiveState.Awake);
        int dark = healthy.Count(h => h is { Visible: false });
        report.AppendLine($"five nacelles hit: {dark} healthy models off, completed [{string.Join(",", completed)}], "
            + $"OBJECTIVE{LastRung} {graph.StateOf(LastRung)}, OBJECTIVE{LossObjective} {graph.StateOf(LossObjective)}");
        ctx.Same(LastRungCount - 1, dark,
            $"each kill switched that nacelle's healthy model off, the bit the rungs count");
        ctx.Check(Rungs.Take(3).All(r => completed.Contains(r.Number)),
            $"the one, two and four rungs have completed, so the runtime read the authored paths");
        ctx.Check(graph.StateOf(LastRung) == ObjectiveState.Awake && !completed.Contains(LastRung),
            $"OBJECTIVE{LastRung} is awake and waiting for its sixth nacelle");
        ctx.Check(graph.StateOf(LossObjective) == ObjectiveState.Dormant,
            $"OBJECTIVE{LossObjective} is still dormant on five nacelles, so five is not the loss");
        ctx.Check(!graph.Ended && !graph.Ending, $"and the mission is still running");
    }

    // The sixth: the last rung completes, naps the loss objective for its authored 45 s, and the
    // mission is lost when that nap runs out rather than on the kill.
    private static void CheckSixthArmsTheFuse(TestContext ctx, AnimRuntime runtime,
        ObjectiveGraph graph, IReadOnlyList<Node3D?> healthy, StringBuilder report)
    {
        ctx.Check(runtime.DamageAt(healthy[LastRungCount - 1], NacelleHealth + 1f),
            $"{Nacelles[LastRungCount - 1]}, the sixth, takes the hit on its own pool");
        Run(runtime, graph, ChainSeconds, advanceWorld: true,
            () => graph.StateOf(LossObjective) == ObjectiveState.Napping);
        float armed = graph.Elapsed;
        report.AppendLine($"sixth nacelle out at t={armed:0.00}s: OBJECTIVE{LossObjective} "
            + $"{graph.StateOf(LossObjective)}, ended={graph.Ended}, ending={graph.Ending}");
        ctx.Check(graph.StateOf(LossObjective) == ObjectiveState.Napping,
            $"the sixth nacelle naps OBJECTIVE{LossObjective} rather than waking it, so the kill itself does not lose");
        ctx.Check(!graph.Ended && !graph.Ending,
            $"and the mission is still running at the moment the fuse is lit");

        // The fuse itself needs no world motion: the nacelles are already off and nothing in the
        // world switches a healthy model back on, so the graph alone runs the nap down.
        Run(runtime, graph, LossNap - Tick * 2f, advanceWorld: false, () => graph.Ended || graph.Ending);
        report.AppendLine($"t={graph.Elapsed:0.00}s, {graph.Elapsed - armed:0.00}s into the nap: "
            + $"ended={graph.Ended}, ending={graph.Ending}");
        ctx.Check(!graph.Ended && !graph.Ending,
            $"the mission survives the whole {LossNap:0} s nap, so the loss is the fuse and not the sixth kill");

        Run(runtime, graph, LossNap, advanceWorld: false, () => graph.Ended);
        float fuse = graph.Elapsed - armed;
        report.AppendLine($"outcome {graph.Outcome} at t={graph.Elapsed:0.00}s, {fuse:0.00}s after the nap began");
        ctx.Check(graph.Outcome == MissionOutcome.Lost,
            $"waking out of the nap loses the mission, which is the authored INSTANTLOSS ({graph.Outcome})");
        ctx.Check(fuse >= LossNap && fuse <= LossNap + FuseSlack,
            $"and it lands {fuse:0.00} s after the sixth nacelle, the authored {LossNap:0} s nap");
    }

    // Steps the graph (and the world, where the check needs its motion) until the predicate holds
    // or the budget runs out, the way a session's own frame does.
    private static void Run(AnimRuntime runtime, ObjectiveGraph graph, float seconds,
        bool advanceWorld, Func<bool> until)
    {
        for (float t = 0f; t < seconds && !until(); t += Tick)
        {
            if (advanceWorld)
            {
                runtime.Advance(Tick);
            }

            graph.Step(Tick);
        }
    }

    private static bool WatchesANacelle(IReadOnlyList<string> path) =>
        path.Count == 3
        && path[0].Equals(Hull, StringComparison.OrdinalIgnoreCase)
        && Nacelles.Contains(path[1], StringComparer.OrdinalIgnoreCase)
        && path[2].Equals("healthy", StringComparison.OrdinalIgnoreCase);

    private static Node3D? Healthy(AnimRuntime runtime, Node3D host, string engine)
    {
        var nacelle = runtime.FindNodes(engine, host).FirstOrDefault();
        return nacelle != null ? runtime.FindNodes("healthy", nacelle).FirstOrDefault() : null;
    }

    private static ObjectiveDef? Def(ObjectiveScript script, int number) =>
        script.Objectives.FirstOrDefault(d => d.Number == number);

    private static CampaignMission? MissionOf(IReadOnlyList<CampaignMission> missions) =>
        missions.FirstOrDefault(m =>
            m.ChapterFolder.Equals(Chapter, StringComparison.OrdinalIgnoreCase)
            && m.MissionFolder.Equals(Mission, StringComparison.OrdinalIgnoreCase));
}
