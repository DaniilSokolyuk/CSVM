using System.Collections.Generic;
using System.Text;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Launch;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>BL-451: a <c>--campaign=</c> launch's <see cref="SessionSpec"/> never carried
/// <see cref="SessionSpec.Zeppelins"/>/<see cref="SessionSpec.Generators"/>, so a campaign
/// mission's zeppelins sat deactivated at the origin. Drives the exact production path
/// (<see cref="CampaignDirector.ResolveSpec"/> then <see
/// cref="GameSession.ResolveCampaignZeppelins"/>) over a mission with zeppelins and an empty
/// generator file, then places the mission's own zeppelins the way the session's placement
/// gate does.</summary>
internal static class CampaignZeppelinSuites
{
    // C3/M01: ships zeppelins.zrd (piratezep, cargozep1) and a 10-byte [null] egen.zrd, so the
    // two flags this suite guards land on opposite sides of the peek.
    private const string ZepChapter = "C3";
    private const string ZepMission = "M01";

    // BL-451: a --campaign= launch's SessionSpec never carried Zeppelins/Generators, so the
    // mission's zeppelins sat deactivated at the origin.
    [Suite("campaign-zeppelins",
        "SessionSpec.FromCampaign sets neither Zeppelins nor Generators; GameSession's "
        + "campaign-mission peek turns Zeppelins on for C3/M01 (ships zeppelins.zrd) and leaves "
        + "Generators off (its egen.zrd is authored empty), and the mission's own zeppelins "
        + "place live once the flag is on")]
    internal static void CampaignZeppelins(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        var mission = MissionOf(missions, ZepChapter, ZepMission)
            ?? throw new SuiteSkippedException($"{ZepChapter}/{ZepMission} is not in cm_sequence");
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, ZepChapter);
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, ZepChapter, ZepMission);
        ctx.RequireData(chapterZrdr, $"{ZepChapter} zrdr");
        ctx.RequireData(missionZrdr, $"{ZepChapter}/{ZepMission} zrdr");

        var report = new StringBuilder();
        var cli = SessionSpec.Parse(System.Array.Empty<string>());
        var campaignSpec = SessionSpec.FromCampaign(cli, "Suite", mission.Seq, new[] { "player_bhawk" }, players: 1);
        ctx.Check(!campaignSpec.Zeppelins && !campaignSpec.Generators,
            $"FromCampaign itself sets neither flag, BL-451's own bug, guarded so it cannot come back unnoticed");

        var resolved = CampaignDirector.ResolveSpec(campaignSpec, ctx.ZrdrPath);
        ctx.Check(resolved.Chapter.Equals(ZepChapter, System.StringComparison.OrdinalIgnoreCase)
            && resolved.Mission.Equals(ZepMission, System.StringComparison.OrdinalIgnoreCase),
            $"seq {mission.Seq} resolves to {ZepChapter}/{ZepMission}, got {resolved.Chapter}/{resolved.Mission}");

        var final = GameSession.ResolveCampaignZeppelins(resolved, ctx.DataRoot);
        report.AppendLine($"seq {mission.Seq} -> {resolved.Chapter}/{resolved.Mission}: "
            + $"Zeppelins={final.Zeppelins} Generators={final.Generators}");
        ctx.Check(final.Zeppelins,
            $"{ZepChapter}/{ZepMission} ships zeppelins.zrd, so the peek turns placement on");
        ctx.Check(!final.Generators,
            $"…but its egen.zrd is authored empty ([null]), so generators stay off");

        PlaceTheZeppelins(ctx, chapterZrdr, missionZrdr, report);

        ctx.WriteArtifact("test-campaign-zeppelins.txt", report.ToString());
        ctx.Note($"{ZepChapter}/{ZepMission}: the campaign peek turns zeppelins on (not generators), and the mission's own zeppelins place live rather than sitting at the origin");
    }

    // The same load-and-place BuildWorldStage's --zeppelins gate performs (GameSession.cs
    // ~2311-2325): proves the flag landing "on" is not academic, the mission's zeppelins are
    // actually live once it does.
    private static void PlaceTheZeppelins(TestContext ctx, string chapterZrdr, string missionZrdr, StringBuilder report)
    {
        var defs = Zeppelins.Load(missionZrdr);
        ctx.Check(defs.Count > 0, $"{ZepChapter}/{ZepMission}'s zeppelin records are non-empty, count={defs.Count}");
        if (defs.Count == 0)
        {
            return;
        }

        var hosts = new Dictionary<string, Node3D>();
        foreach (var def in defs)
        {
            var host = new Node3D { Name = def.Node };
            ctx.Host.AddChild(host);
            hosts[def.Node] = host;
        }

        ZeppelinRuntime? runtime = null;
        try
        {
            var nets = AiNets.Load(chapterZrdr);
            runtime = new ZeppelinRuntime(defs, name => hosts.TryGetValue(name, out var h) ? h : null, nets);
            report.AppendLine($"placed {runtime.LiveCount} of {defs.Count} zeppelin(s): "
                + string.Join(", ", hosts.Keys));
            ctx.Same(defs.Count, runtime.LiveCount,
                $"every authored zeppelin is live once the flag is on, not left switched off at the origin");
        }
        finally
        {
            runtime?.Free();
            foreach (var host in hosts.Values)
            {
                host.Free();
            }
        }
    }

    private static CampaignMission? MissionOf(IReadOnlyList<CampaignMission> missions, string chapter, string mission)
    {
        foreach (var m in missions)
        {
            if (m.ChapterFolder.Equals(chapter, System.StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(mission, System.StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }

        return null;
    }
}
