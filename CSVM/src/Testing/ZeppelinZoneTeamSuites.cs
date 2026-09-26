using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>BL-740: a zeppelin's gasbag pool stands on the zone group, but the flagged mission
/// structure is the state child the record's healthy entry names, and its own ownership slot
/// is the team the original builds the structure with. C4/M03's Pandora is the case: its record
/// authors no team, its engines read the player's side off their flagged nodes, and its gasbags
/// read the same off <c>gasbagN/panels</c>.</summary>
internal static class ZeppelinZoneTeamSuites
{
    private const string Chapter = "C4";
    private const string Mission = "M03";
    private const string Zep = "piratezep";

    [Suite("zeppelin-zone-team",
        "a gasbag zone's team over C4/M03's BUILT world (BL-740): the Pandora's record authors no "
        + "team, its engine pools read the player's side off their flagged nodes, and each gasbag "
        + "pool reads the same side off the panels child its healthy entry names, so the AI's "
        + "structure pool offers the gasbags on the engines' team rather than as nobody's")]
    internal static void ZeppelinZoneTeam(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");
        var defs = Zeppelins.Load(missionZrdr);
        var nets = AiNets.Load(chapterZrdr);
        var def = defs.FirstOrDefault(d => d.Node == Zep);
        ctx.Check(def != null && ZeppelinRuntime.AuthoredTeam(def) == null,
            $"'{Zep}' authors no team, so its parts' sides come off the scene nodes alone");

        var report = new StringBuilder();
        ctx.WithWorld(Chapter, collision: false, Mission, world =>
        {
            ZeppelinRuntime? zeps = null;
            try
            {
                var runtime = zeps = new ZeppelinRuntime(defs,
                    name => world.Runtime.FindNodes(name, null) is { Count: > 0 } hits ? hits[0] : null,
                    nets);
                ctx.Host.AddChild(runtime);
                runtime.WireDamage(world.Runtime);

                var set = new AimCandidateSet();
                set.AddStructures(world.Runtime.Destructibles);
                var parts = set.Structures
                    .Where(c => c.Source is DestructibleRegistry.Instance { Owner: Zep })
                    .ToList();
                var engines = parts.Where(c => !((DestructibleRegistry.Instance)c.Source!).Gasbag).ToList();
                var gasbags = parts.Where(c => ((DestructibleRegistry.Instance)c.Source!).Gasbag).ToList();
                foreach (var c in parts)
                {
                    report.AppendLine($"{TargetPool.NameOf(c.Source)} team={c.Team} gasbag={((DestructibleRegistry.Instance)c.Source!).Gasbag}");
                }

                var engineTeams = engines.Select(c => c.Team).Distinct().ToList();
                var gasbagTeams = gasbags.Select(c => c.Team).Distinct().ToList();
                ctx.Check(engines.Count > 0 && engineTeams.Count == 1 && engineTeams[0] == AimAssist.PlayerTeam,
                    $"the Pandora's {engines.Count} engine pools all read the player's side off their flagged nodes (teams {string.Join(",", engineTeams)})");
                ctx.Same(def?.Healthy.Count ?? 0, gasbags.Count,
                    $"every healthy zone reaches the AI's structure pool as a gasbag");
                ctx.Check(gasbags.Count > 0 && gasbagTeams.Count == 1 && gasbagTeams[0] == AimAssist.PlayerTeam,
                    $"…and each reads the same side off its panels child rather than falling through as nobody's (teams {string.Join(",", gasbagTeams)})");
            }
            finally
            {
                zeps?.Free();
            }
        });
        ctx.WriteArtifact($"test-zeppelin-zone-team-{Chapter}-{Mission}.txt", report.ToString());
    }
}
