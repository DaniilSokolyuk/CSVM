using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>BL-476's second half: a zeppelin's zones and its guns carried no owning identity, so a
/// record's authored team stopped at the damage pools and a <c>rating_biases</c> pattern naming the
/// airship matched nothing. Both halves are driven off shipped records here, C1/MP3's one
/// <c>ally</c> hull for the gun fan, C5/M03's <c>cargozep*</c> exclusion for the bias.</summary>
internal static class ZeppelinIdentitySuites
{
    // C1/MP3 is the case for the guns: two hulls, four turret NODES patterns each, and a team
    // authored on exactly one of them, so the fan and its absence are visible in one world.
    private const string AllyZep = "multiplayer2zep";
    private const string PlainZep = "multiplayer1zep";

    // C4/M03's docked cargo zeppelin is a targets.zrd mission structure, so its engines and guns
    // are damage pools named for themselves. Two blocks name the airship from opposite ends: the
    // Black Swan escort excludes it outright, the Black Hat Warhawks make it always-target.
    private const string DockedZep = "cargozep1";
    private const string EscortBlock = "bswingman_1";
    private const string HunterBlock = "bhatwarhawk_7";

    [Suite("zeppelin-identity",
        "the owning-zeppelin identity a zone and a gun carry (BL-476): C5/M03's authored "
        + "cargozep* exclusion reaches a gasbag only through its hull's name and not through "
        + "the zone's own, and over C1/MP3's built world the record team fans onto every "
        + "emplacement standing on the ally hull while the unauthored sibling's guns keep "
        + "their TURRET default")]
    internal static void ZeppelinIdentity(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        CheckBiasReach(ctx);
        CheckTurretFan(ctx);
    }

    [Suite("structure-part-bias",
        "the reach a rating_biases pattern naming a DOCKED mission structure has over C4/M03's "
        + "built world: every damage pool standing under 'cargozep1' takes the Black Swan escort's "
        + "authored -1.0 exclusion and the Black Hat Warhawks' authored 1.0 always-target through "
        + "the node name above it, while a pool standing elsewhere takes neither")]
    internal static void StructurePartBias(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C4", "M03");
        ctx.RequireData(missionZrdr, $"C4/M03 zrdr");
        var escort = BlockBiases(missionZrdr, EscortBlock);
        var hunter = BlockBiases(missionZrdr, HunterBlock);
        ctx.Check(Names(escort, DockedZep, b => b <= -1f),
            $"'{EscortBlock}' authors the hard exclusion naming '{DockedZep}' ({escort.Count} entries)");
        ctx.Check(Names(hunter, DockedZep, b => b >= 1f),
            $"…and '{HunterBlock}' authors the always-target naming the same structure ({hunter.Count} entries)");

        ctx.WithWorld("C4", collision: false, "M03", world =>
        {
            var registry = world.Runtime.Destructibles;
            ctx.Check(registry is { Count: > 0 },
                $"C4/M03's world registers damage pools at all ({registry?.Count ?? 0})");
            if (world.Runtime.FindNodes(DockedZep, null) is not { Count: > 0 } hits || registry == null)
            {
                ctx.Check(false, $"C4/M03's world holds the '{DockedZep}' node this reads");
                return;
            }

            var root = hits[0];
            var under = new List<DestructibleRegistry.Instance>();
            var elsewhere = new List<DestructibleRegistry.Instance>();
            foreach (var inst in registry.All)
            {
                if (!GodotObject.IsInstanceValid(inst.Anchor))
                {
                    continue;
                }
                (ReferenceEquals(inst.Anchor, root) || root.IsAncestorOf(inst.Anchor)
                    ? under : elsewhere).Add(inst);
            }

            ctx.Check(under.Count > 0 && elsewhere.Count > 0,
                $"the docked structure carries pools of its own and the world carries others ({under.Count} under '{DockedZep}', {elsewhere.Count} elsewhere)");
            int excluded = 0, wanted = 0;
            string named = string.Empty;
            foreach (var inst in under)
            {
                string name = TargetPool.NameOf(inst);
                var owners = new List<string>();
                TargetPool.CollectOwners(inst, owners);
                excluded += AiTargetRanking.ObjectiveBiasFor(name, owners, escort)
                    >= AiTargetRanking.NotRanked ? 1 : 0;
                wanted += AiTargetRanking.ObjectiveBiasFor(name, owners, hunter)
                    <= AiTargetRanking.AlwaysTarget ? 1 : 0;
                if (named.Length == 0 && !name.Equals(DockedZep, System.StringComparison.OrdinalIgnoreCase))
                {
                    named = name;
                }
            }

            ctx.Same(under.Count, excluded,
                $"'{EscortBlock}' ranks no pool of the docked structure at all, '{named}' included ({excluded}/{under.Count})");
            ctx.Same(under.Count, wanted,
                $"…and '{HunterBlock}' takes every one of them as its always-target ({wanted}/{under.Count})");

            int stray = 0;
            foreach (var inst in elsewhere)
            {
                var owners = new List<string>();
                TargetPool.CollectOwners(inst, owners);
                stray += AiTargetRanking.ObjectiveBiasFor(TargetPool.NameOf(inst), owners, escort) != 0f
                    ? 1 : 0;
            }

            ctx.Same(0, stray,
                $"…while the same list reaches none of the {elsewhere.Count} pools standing outside it");
            ctx.Note($"C4/M03: {under.Count} pool(s) under '{DockedZep}', first part '{named}'");
        });
    }

    // One roster block's authored rating_biases, empty where the mission names no such block.
    // The bias half, tree-free: a zone answers to its own anchor name AND to the airship that owns
    // it, which is the only way an authored pattern naming the hull can reach a gasbag.
    private static void CheckBiasReach(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C5", "M03");
        ctx.RequireData(missionZrdr, $"C5/M03 zrdr");
        var biases = new List<AiRatingBias>();
        foreach (var (_, fields) in AiSkills.LoadRoster(missionZrdr))
        {
            var own = AiSkills.RosterRatingBiases(fields);
            if (own.Any(b => b.Pattern.StartsWith("cargozep", System.StringComparison.OrdinalIgnoreCase)))
            {
                biases = own;
                break;
            }
        }

        ctx.Check(biases.Count > 0,
            $"C5/M03 authors a rating_biases entry naming its own zeppelins, which is what this reads");
        if (biases.Count == 0)
        {
            return;
        }

        float bare = AiTargetRanking.ObjectiveBiasFor("gasbag1", null, biases);
        float owned = AiTargetRanking.ObjectiveBiasFor("gasbag1", new[] { "cargozep2" }, biases);
        float other = AiTargetRanking.ObjectiveBiasFor("gasbag1", new[] { "beowulfzep" }, biases);
        ctx.Check(bare == 0f,
            $"a zone offered under its own anchor name alone matches nothing, the zone is 'gasbag1', the pattern names the hull (bias={bare})");
        ctx.Check(owned == AiTargetRanking.NotRanked,
            $"…and the same zone carrying its owning zeppelin's name takes the authored -1.0 exclusion (bias={owned})");
        ctx.Check(other == 0f,
            $"…while a zone owned by a hull the list does not name is unaffected (bias={other})");
    }

    // The gun half, over C1/MP3's built world: the record team reaches the emplacements standing on
    // the hull, and only those. The unauthored sibling is the control, its guns must not move.
    private static void CheckTurretFan(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, "C1", "MP3");
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, "C1");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(missionZrdr, $"C1/MP3 zrdr");
        ctx.RequireData(chapterZrdr, $"C1 zrdr");
        ctx.RequireData(texturesPath, $"C1 textures");

        var defs = Zeppelins.Load(missionZrdr);
        var nets = AiNets.Load(chapterZrdr);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var turretDefs = TurretDefs.Load(ctx.ZrdrPath);
        ctx.Check(ZeppelinRuntime.AuthoredTeam(Record(defs, AllyZep)) == AimAssist.PlayerTeam
            && ZeppelinRuntime.AuthoredTeam(Record(defs, PlainZep)) == null,
            $"C1/MP3 authors 'ally' on '{AllyZep}' and nothing on '{PlainZep}', the pair this measures");

        ctx.WithWorld("C1", collision: false, "MP3", world =>
        {
            var textures = new TextureArchive(texturesPath);
            ProjectilePool? live = null;
            ZeppelinRuntime? zeps = null;
            TurretEmplacementRuntime? emplacements = null;
            try
            {
                live = new ProjectilePool(textures, null, null);
                ctx.Host.AddChild(live);
                var runtime = zeps = new ZeppelinRuntime(defs,
                    name => world.Runtime.FindNodes(name, null) is { Count: > 0 } hits ? hits[0] : null,
                    nets);
                ctx.Host.AddChild(runtime);
                runtime.WireDamage(world.Runtime);

                var owners = OwnersOf(world.Runtime.Destructibles);
                ctx.Check(owners.Contains(AllyZep) && owners.Contains(PlainZep),
                    $"every wired zone pool carries its own hull's name ({owners.Count} distinct owner(s))");

                emplacements = new TurretEmplacementRuntime(turretDefs, weapons,
                    (pattern, scope) => world.Runtime.FindNodes(pattern, scope), live,
                    world.Runtime.WorldRoot);
                int allyBefore = TeamsUnder(world, emplacements, AllyZep).Count;
                int plainBefore = TeamsUnder(world, emplacements, PlainZep).Count;
                ctx.Check(allyBefore > 0 && plainBefore > 0,
                    $"both hulls carry emplacements at all (ally {allyBefore}, unauthored {plainBefore})");

                int moved = runtime.FanTeamsOntoTurrets(emplacements);
                var allyTeams = TeamsUnder(world, emplacements, AllyZep);
                var plainTeams = TeamsUnder(world, emplacements, PlainZep);
                ctx.Same(allyBefore, moved,
                    $"the fan moves exactly the ally hull's guns and no others (moved={moved})");
                ctx.Check(allyTeams.Count > 0 && allyTeams.All(t => t == AimAssist.PlayerTeam),
                    $"…every gun on '{AllyZep}' now answers to its airship's team, not to the TURRET default");
                ctx.Check(plainTeams.Count > 0 && plainTeams.All(t => t == TurretDef.DefaultTeamId),
                    $"…while '{PlainZep}', whose record authors no team, keeps its guns on the authored TURRET default {TurretDef.DefaultTeamId}");

                ctx.Same(0, runtime.FanTeamsOntoTurrets(emplacements),
                    $"a second fan moves nothing: SetTeam reports a write only when the value changes");
                ctx.Note($"C1/MP3: team {AimAssist.PlayerTeam} onto {moved} gun(s) of '{AllyZep}', {plainTeams.Count} left on {TurretDef.DefaultTeamId}");
            }
            finally
            {
                emplacements?.Free();
                zeps?.Free();
                live?.Free();
                textures.Dispose();
            }
        });
    }

    private static List<AiRatingBias> BlockBiases(string missionZrdr, string block)
    {
        foreach (var (name, fields) in AiSkills.LoadRoster(missionZrdr))
        {
            if (name.Equals(block, System.StringComparison.OrdinalIgnoreCase))
            {
                return AiSkills.RosterRatingBiases(fields);
            }
        }
        return new List<AiRatingBias>();
    }

    private static bool Names(List<AiRatingBias> biases, string pattern, System.Func<float, bool> weight) =>
        biases.Any(b => b.Pattern.Equals(pattern, System.StringComparison.OrdinalIgnoreCase)
            && weight(b.Bias));

    private static ZeppelinDef Record(IReadOnlyList<ZeppelinDef> defs, string node) =>
        defs.First(d => d.Node == node);

    private static HashSet<string> OwnersOf(DestructibleRegistry? registry)
    {
        var owners = new HashSet<string>();
        foreach (var inst in registry?.All ?? (IReadOnlyList<DestructibleRegistry.Instance>)System.Array.Empty<DestructibleRegistry.Instance>())
        {
            if (inst.Owner is { Length: > 0 } owner)
            {
                owners.Add(owner);
            }
        }
        return owners;
    }

    private static List<int> TeamsUnder(TestWorld world,
        TurretEmplacementRuntime emplacements, string node)
    {
        var teams = new List<int>();
        if (world.Runtime.FindNodes(node, null) is not { Count: > 0 } hits)
        {
            return teams;
        }
        var root = hits[0];
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
}
