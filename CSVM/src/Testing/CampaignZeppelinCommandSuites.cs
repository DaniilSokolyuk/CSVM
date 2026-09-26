using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>The two <c>SET_AI_*</c> verbs a mission aims at an AIRSHIP rather than an aeroplane,
/// each driven over the shipped mission that authors it. C4/M05's OBJECTIVE40 moves
/// <c>blackhatzep</c> off its record net onto the attack route, and C2/M05's OBJECTIVE36 turns the
/// boarded <c>cargozep2</c> from the enemy side to the player's. Both read the result off the live
/// runtime, never off the objective having run, and both assert the directive is no longer declined
/// through the director's <c>Gap</c> line.</summary>
internal static class CampaignZeppelinCommandSuites
{
    // C4/M05 is the one shipped mission whose script re-routes an airship: OBJECTIVE40 names
    // blackhatzep alone, so nothing else in the clause can carry the assertion.
    private const string NetChapter = "C4";
    private const string NetMission = "M05";
    private const string Hatzep = "blackhatzep";
    private const int NetObjective = 40;
    private const string HatzepRecordNet = "M5Hatzep";
    private const string AttackNet = "M5ZepAttack";

    // C2/M05's OBJECTIVE36 is the only shipped zeppelin team write that CHANGES a side: its
    // cargozep2 record authors `enemy`, and the clause hands the boarded airship to the player.
    private const string TeamChapter = "C2";
    private const string TeamMission = "M05";
    private const string Cargozep = "cargozep2";
    private const int TeamObjective = 36;
    private const int ScriptTeam = AimAssist.PlayerTeam;

    // Names no mission carries, for the two refusals: an unknown net keeps the route, an unknown
    // airship is reported to the caller rather than swallowed.
    private const string AbsentNet = "csvmnosuchnet";
    private const string AbsentZeppelin = "csvmnosuchzeppelin";

    private const float GraphDt = 0.1f;

    // One Step resolves a single objective of the mission per tick, round robin over all of them,
    // so a freshly woken one needs several seconds of ticks rather than one call.
    private const float GraphLimitS = 12f;

    private const float StepDt = 1f / 60f;

    [Suite("campaign-set-ai-zeppelin-net",
        "the airship arm of SET_AI_NET over C4/M05's own built world: blackhatzep flies the "
        + "M5Hatzep its record authors until OBJECTIVE40 completes through the real graph and "
        + "puts it on M5ZepAttack, seated at the node nearest where the hull stands and still "
        + "watching the net's stop points; a hull moved to the far end of that route re-seats "
        + "there rather than at the node it had; a net the chapter does not carry leaves the "
        + "airship where it is and reads as no move; a name that is no zeppelin is refused; and "
        + "the director no longer declines the directive through its Gap line")]
    internal static void CampaignSetAiZeppelinNet(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, NetChapter, NetMission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, NetChapter);
        ctx.RequireData(missionZrdr, $"{NetChapter}/{NetMission} zrdr");
        ctx.RequireData(chapterZrdr, $"{NetChapter} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, NetChapter),
            $"{NetChapter} textures");

        var mission = Find(ctx, NetChapter, NetMission);
        var script = ObjectiveScript.Load(missionZrdr);
        var defs = Zeppelins.Load(missionZrdr);
        var nets = AiNets.Load(chapterZrdr);
        var report = new StringBuilder();

        var clause = script.ByNumber(NetObjective)?.SetAiNet ?? new List<(string, string)>();
        ctx.Check(clause.Count == 1 && clause[0].Item1.Equals(Hatzep, StringComparison.OrdinalIgnoreCase)
            && clause[0].Item2.Equals(AttackNet, StringComparison.OrdinalIgnoreCase),
            $"OBJECTIVE{NetObjective} names '{Hatzep}' alone, for '{AttackNet}'");
        var record = Record(defs, Hatzep);
        ctx.Check(record != null && record.Net.Equals(HatzepRecordNet, StringComparison.OrdinalIgnoreCase),
            $"'{Hatzep}'s record authors '{HatzepRecordNet}', which is the route the clause replaces");
        ctx.Check(AiNets.ByName(nets, AttackNet) != null,
            $"'{NetChapter}' carries '{AttackNet}', so the clause has somewhere to send it");

        var director = CampaignDirector.Create(script, mission,
            CampaignProfileDef.NewProfile("Airship"), null);
        ctx.ExtraPrewarmSoundNames = script.SoundGroupNames();
        ctx.WithWorld(NetChapter, collision: false, NetMission,
            world => DriveNet(ctx, world, director, defs, nets, report));

        ctx.WriteArtifact($"test-campaign-set-ai-zeppelin-net-{NetChapter}-{NetMission}.txt",
            report.ToString());
        ctx.Note($"{NetChapter}/{NetMission}: OBJECTIVE{NetObjective} re-routes '{Hatzep}' onto '{AttackNet}'");
    }

    [Suite("campaign-set-ai-zeppelin-team",
        "the airship arm of SET_AI_TEAM over C2/M05's own built world: cargozep2's record "
        + "authors the enemy side, and its damage pools and every gun standing on the hull carry "
        + "it, until OBJECTIVE36 completes through the real graph and hands the whole airship to "
        + "the player's side, pools, guns and the target parts it offers alike; a name that is no "
        + "zeppelin is refused; and the director no longer declines the directive through its "
        + "Gap line")]
    internal static void CampaignSetAiZeppelinTeam(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, TeamChapter, TeamMission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, TeamChapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, TeamChapter);
        ctx.RequireData(missionZrdr, $"{TeamChapter}/{TeamMission} zrdr");
        ctx.RequireData(chapterZrdr, $"{TeamChapter} zrdr");
        ctx.RequireData(texturesPath, $"{TeamChapter} textures");

        var mission = Find(ctx, TeamChapter, TeamMission);
        var script = ObjectiveScript.Load(missionZrdr);
        var defs = Zeppelins.Load(missionZrdr);
        var nets = AiNets.Load(chapterZrdr);
        var report = new StringBuilder();

        var clause = script.ByNumber(TeamObjective)?.SetAiTeam ?? new List<(string, int)>();
        ctx.Check(clause.Count == 1 && clause[0].Item1.Equals(Cargozep, StringComparison.OrdinalIgnoreCase)
            && clause[0].Item2 == ScriptTeam,
            $"OBJECTIVE{TeamObjective} names '{Cargozep}' alone, for team {ScriptTeam}");
        var record = Record(defs, Cargozep);
        ctx.Check(record != null && ZeppelinRuntime.AuthoredTeam(record) == TurretDef.DefaultTeamId,
            $"'{Cargozep}'s record authors the enemy side, id {TurretDef.DefaultTeamId}, which the clause replaces");

        var director = CampaignDirector.Create(script, mission,
            CampaignProfileDef.NewProfile("Airship"), null);
        ctx.ExtraPrewarmSoundNames = script.SoundGroupNames();
        ctx.WithWorld(TeamChapter, collision: false, TeamMission,
            world => DriveTeam(ctx, world, director, defs, nets, texturesPath, report));

        ctx.WriteArtifact($"test-campaign-set-ai-zeppelin-team-{TeamChapter}-{TeamMission}.txt",
            report.ToString());
        ctx.Note($"{TeamChapter}/{TeamMission}: OBJECTIVE{TeamObjective} hands '{Cargozep}' to team {ScriptTeam}");
    }

    private static void DriveNet(TestContext ctx, TestWorld world, CampaignDirector director,
        IReadOnlyList<ZeppelinDef> defs, IReadOnlyList<AiNet> nets, StringBuilder report)
    {
        ZeppelinRuntime? owned = null;
        try
        {
            var trailers = Trailers(world);
            var runtime = owned = new ZeppelinRuntime(defs, name => Node(world, name), nets,
                trailers.For);
            ctx.Host.AddChild(runtime);

            var before = runtime.MotionFor(Hatzep);
            ctx.Check(before != null
                && before.Follower.Net.Name.Equals(HatzepRecordNet, StringComparison.OrdinalIgnoreCase),
                $"'{Hatzep}' flies its record's '{before?.Follower.Net.Name ?? "-"}' before the mission commands it");
            if (before == null)
            {
                return;
            }

            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Zeppelins = runtime,
                Sounds = world.Runtime.Sounds,
                ListenerPosition = () => ctx.Camera.GlobalPosition,
                Rng = new Random(1),
            });
            ctx.Check(director.Graph != null, $"the world phase armed the objective graph");
            if (director.Graph is not { } graph)
            {
                return;
            }

            RunObjective(graph, NetObjective);
            ctx.Check(graph.CompletedOf(NetObjective),
                $"OBJECTIVE{NetObjective} completed through the real graph");

            var after = runtime.MotionFor(Hatzep)!.Follower;
            var hull = Node(world, Hatzep)!.GlobalPosition;
            int want = NearestEdgedNode(after, hull);
            report.AppendLine($"net: '{Hatzep}' at ({hull.X:0},{hull.Y:0},{hull.Z:0}) flies "
                + $"'{after.Net.Name}#{after.Net.Id}' seated at node {after.CurrentIndex} "
                + $"(nearest to it is {want} of {after.Net.Nodes.Count})");
            ctx.Check(after.Net.Name.Equals(AttackNet, StringComparison.OrdinalIgnoreCase),
                $"the clause moved '{Hatzep}' onto '{after.Net.Name}'");
            ctx.Same(want, after.CurrentIndex,
                $"…seated at the node nearest where the hull stands, by the nearest-node rule");
            ctx.Check(after.ObservesStopPoints,
                $"…and the new follower still watches the net's stop points, which only a zeppelin does");
            ctx.Check(!Declined(director, "SET_AI_NET"),
                $"the director no longer declines SET_AI_NET through its Gap line");

            CheckReseatFromWhereItStands(ctx, runtime, world, report);
            CheckRefusals(ctx, runtime, report);
        }
        finally
        {
            owned?.Free();
        }
    }

    // The seat rule made able to fail: moved to the node of its own route FARTHEST from where it
    // stands, a second assignment has to take that node rather than the seat it took before.
    private static void CheckReseatFromWhereItStands(TestContext ctx, ZeppelinRuntime runtime,
        TestWorld world, StringBuilder report)
    {
        if (runtime.MotionFor(Hatzep) is not { } motion)
        {
            ctx.Check(false, $"'{Hatzep}' is an airship of this mission, which this parks and re-commands");
            return;
        }

        // A record's own `deactivated` keeps the hull placed but unstepped, and the park below
        // needs the step that writes the moved pose onto the world node.
        runtime.Wake(Hatzep);
        int seat = motion.Follower.CurrentIndex;
        int far = FarthestEdgedNode(motion.Follower, Node(world, Hatzep)!.GlobalPosition);
        var park = motion.Follower.NodePosition(far);
        motion.ResumeAt(park, motion.YawRad, motion.PitchRad);
        runtime.SimStep(StepDt);   // the follower's pose reaches the hull node through Place

        ctx.Check(runtime.SetNet(Hatzep, AttackNet), $"a second assignment reaches the airship");
        int parked = runtime.MotionFor(Hatzep)!.Follower.CurrentIndex;
        report.AppendLine($"park: '{Hatzep}' moved from node {seat} to node {far} of "
            + $"'{AttackNet}' re-seats at {parked}");
        ctx.Check(far != seat,
            $"node {far} is not the seat the hull took where it was, node {seat}, so the park can fail");
        ctx.Same(far, parked,
            $"the moved hull captures the route where it now stands rather than at its old seat");
    }

    private static void CheckRefusals(TestContext ctx, ZeppelinRuntime runtime, StringBuilder report)
    {
        string held = runtime.MotionFor(Hatzep)!.Follower.Net.Name;
        ctx.Check(!runtime.SetNet(Hatzep, AbsentNet),
            $"a net the chapter does not carry reads as no move rather than as an applied clause");
        string kept = runtime.MotionFor(Hatzep)!.Follower.Net.Name;
        report.AppendLine($"refusals: '{AbsentNet}' left '{Hatzep}' on '{kept}'");
        ctx.Check(kept.Equals(held, StringComparison.OrdinalIgnoreCase),
            $"…and leaves it on the route it is flying, '{kept}'");
        ctx.Check(!runtime.SetNet(AbsentZeppelin, AttackNet),
            $"a name that is no zeppelin of this mission is refused rather than swallowed");
        ctx.Check(!runtime.SetTeam(AbsentZeppelin, ScriptTeam),
            $"…and the same for the team arm");
    }

    private static void DriveTeam(TestContext ctx, TestWorld world, CampaignDirector director,
        IReadOnlyList<ZeppelinDef> defs, IReadOnlyList<AiNet> nets, string texturesPath,
        StringBuilder report)
    {
        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        ZeppelinRuntime? owned = null;
        TurretEmplacementRuntime? emplacements = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var trailers = Trailers(world);
            var runtime = owned = new ZeppelinRuntime(defs, name => Node(world, name), nets,
                trailers.For);
            ctx.Host.AddChild(runtime);
            runtime.WireDamage(world.Runtime);

            emplacements = new TurretEmplacementRuntime(TurretDefs.Load(ctx.ZrdrPath),
                WeaponDefs.Load(ctx.ZrdrPath, null),
                (pattern, scope) => world.Runtime.FindNodes(pattern, scope), live,
                world.Runtime.WorldRoot);
            runtime.FanTeamsOntoTurrets(emplacements);

            var poolsBefore = PoolTeams(world, Cargozep);
            var gunsBefore = GunTeams(world, emplacements, Cargozep);
            report.AppendLine($"before: {poolsBefore.Count} damage pool(s) on "
                + $"[{Join(poolsBefore)}], {gunsBefore.Count} gun(s) on [{Join(gunsBefore)}]");
            ctx.Check(poolsBefore.Count > 0 && All(poolsBefore, TurretDef.DefaultTeamId),
                $"'{Cargozep}'s damage pools carry the enemy side its record authors");
            ctx.Check(gunsBefore.Count > 0 && All(gunsBefore, TurretDef.DefaultTeamId),
                $"…and so does every gun standing on the hull");

            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Zeppelins = runtime,
                Turrets = emplacements,
                Sounds = world.Runtime.Sounds,
                ListenerPosition = () => ctx.Camera.GlobalPosition,
                Rng = new Random(1),
            });
            ctx.Check(director.Graph != null, $"the world phase armed the objective graph");
            if (director.Graph is not { } graph)
            {
                return;
            }

            RunObjective(graph, TeamObjective);
            ctx.Check(graph.CompletedOf(TeamObjective),
                $"OBJECTIVE{TeamObjective} completed through the real graph");

            var poolsAfter = PoolTeams(world, Cargozep);
            var gunsAfter = GunTeams(world, emplacements, Cargozep);
            var partsAfter = PartTeams(runtime, Cargozep);
            report.AppendLine($"after: pools [{Join(poolsAfter)}], guns [{Join(gunsAfter)}], "
                + $"target parts [{Join(partsAfter)}]");
            ctx.Check(poolsAfter.Count == poolsBefore.Count && All(poolsAfter, ScriptTeam),
                $"the clause moved every one of '{Cargozep}'s damage pools onto team {ScriptTeam}");
            ctx.Check(gunsAfter.Count == gunsBefore.Count && All(gunsAfter, ScriptTeam),
                $"…and every gun with them, so no part of the hull is left on the old side");
            ctx.Check(partsAfter.Count > 0 && All(partsAfter, ScriptTeam),
                $"…and the parts it offers the target pool answer to the new side too");
            ctx.Check(!Declined(director, "SET_AI_TEAM"),
                $"the director no longer declines SET_AI_TEAM through its Gap line");
            ctx.Check(!runtime.SetTeam(AbsentZeppelin, ScriptTeam),
                $"a name that is no zeppelin of this mission is refused rather than swallowed");
        }
        finally
        {
            emplacements?.Free();
            owned?.Free();
            pool?.Free();
            textures.Dispose();
        }
    }

    private static void RunObjective(ObjectiveGraph graph, int number)
    {
        graph.Step(GraphDt);
        graph.Wake(number);
        for (float t = 0f; t < GraphLimitS && !graph.CompletedOf(number); t += GraphDt)
        {
            graph.Step(GraphDt);
        }
    }

    private static bool Declined(CampaignDirector director, string directive)
    {
        foreach (var gap in director.Gaps)
        {
            if (gap.Equals(directive, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static NetTrailerTargets Trailers(TestWorld world) =>
        new(null, name => Node(world, name));

    private static Node3D? Node(TestWorld world, string name) =>
        world.Runtime.FindNodes(name, null) is { Count: > 0 } hits ? hits[0] : null;

    private static ZeppelinDef? Record(IReadOnlyList<ZeppelinDef> defs, string node)
    {
        foreach (var def in defs)
        {
            if (def.Node.Equals(node, StringComparison.OrdinalIgnoreCase))
            {
                return def;
            }
        }
        return null;
    }

    // The follower's own seat rule, restated so the seat can be checked against it: the nearest
    // node that carries an edge, in the follower's live (trailer-offset) space.
    private static int NearestEdgedNode(AiNetFollower follower, Vector3 position)
    {
        int best = -1;
        float bestSq = float.MaxValue;
        for (int i = 0; i < follower.Net.Nodes.Count; i++)
        {
            if (!HasEdge(follower.Net, i))
            {
                continue;
            }

            float dSq = follower.NodePosition(i).DistanceSquaredTo(position);
            if (dSq < bestSq)
            {
                bestSq = dSq;
                best = i;
            }
        }
        return best;
    }

    private static int FarthestEdgedNode(AiNetFollower follower, Vector3 position)
    {
        int best = -1;
        float bestSq = -1f;
        for (int i = 0; i < follower.Net.Nodes.Count; i++)
        {
            if (!HasEdge(follower.Net, i))
            {
                continue;
            }

            float dSq = follower.NodePosition(i).DistanceSquaredTo(position);
            if (dSq > bestSq)
            {
                bestSq = dSq;
                best = i;
            }
        }
        return best;
    }

    private static bool HasEdge(AiNet net, int index)
    {
        bool any = false;
        foreach (var (a, b) in net.Edges)
        {
            any |= (a == index || b == index) && a != b;
        }
        return any;
    }

    private static List<int> PoolTeams(TestWorld world, string zeppelin)
    {
        var teams = new List<int>();
        foreach (var inst in world.Runtime.Destructibles.All)
        {
            if (string.Equals(inst.Owner, zeppelin, StringComparison.OrdinalIgnoreCase)
                && inst.Team is { } team)
            {
                teams.Add(team);
            }
        }
        return teams;
    }

    private static List<int> GunTeams(TestWorld world, TurretEmplacementRuntime emplacements,
        string zeppelin)
    {
        var teams = new List<int>();
        if (Node(world, zeppelin) is not { } root)
        {
            return teams;
        }
        foreach (var t in emplacements.Emplacements)
        {
            if (t.Site is { } site && GodotObject.IsInstanceValid(site)
                && (site == root || root.IsAncestorOf(site)))
            {
                teams.Add(t.Team);
            }
        }
        return teams;
    }

    private static List<int> PartTeams(ZeppelinRuntime runtime, string zeppelin)
    {
        var parts = new List<AimCandidate>();
        runtime.CollectTargetParts(parts);
        var teams = new List<int>();
        foreach (var part in parts)
        {
            if (part.Source is DestructibleRegistry.Instance inst
                && string.Equals(inst.Owner, zeppelin, StringComparison.OrdinalIgnoreCase))
            {
                teams.Add(part.Team);
            }
        }
        return teams;
    }

    private static bool All(IReadOnlyList<int> teams, int want)
    {
        foreach (int team in teams)
        {
            if (team != want)
            {
                return false;
            }
        }
        return true;
    }

    private static string Join(IReadOnlyList<int> teams) => string.Join(",", teams);

    private static CampaignMission Find(TestContext ctx, string chapter, string folder)
    {
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(chapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(folder, StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }
        throw new SuiteSkippedException($"{chapter}/{folder} is not in cm_sequence");
    }
}
