using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.InstantAction;
using CSVM.Session.Roster;
using Godot;

namespace CSVM.Testing;

/// <summary>The livery a campaign mission's own enemy set flies in: each block's militia def
/// resolved through the session's own spawner, painted onto the aircraft it builds. The def also
/// carries the damage model, the weapons fit and the pilot vector, so what this watches is the one
/// seam all four ride (docs/org/paint.md).</summary>
internal static class CampaignMilitiaLiverySuites
{
    // CM01's enemy set is three Medusa Kestrels, CM02's is the British bombers and their escort:
    // two militias, three airframes, over two missions of the same chapter. CM16 flies the Black
    // Swan on the player's side and CM19 five Fortune Hunters wingmen.
    private static readonly (string Chapter, string Mission)[] Missions =
    {
        ("C3", "M01"), ("C3", "M05"), ("C4", "M01"), ("C4", "M04"),
    };

    [Suite("campaign-militia-livery",
        "the campaign's livery over CM01 (C3/M01), CM02 (C3/M05), CM16 (C4/M01) and CM19 "
        + "(C4/M04), spawned through the session's own roster path: every enemy block resolves "
        + "the militia def its name carries, its aircraft is painted in that def's authored "
        + "pattern with a painter that finds skins for the airframe, the aircraft is still built "
        + "under the shipped-skins reading (so the def's livery outranks it rather than replacing "
        + "the fork), the player's own side wears its def's livery (the Fortune Hunters default, "
        + "or the shipped skins for CM16's Black Swan, whose def authors none), and a spawn "
        + "naming no def at all under that same reading stays unpainted")]
    internal static void CampaignMilitiaLivery(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        string rof = Path.Combine(ctx.DataRoot, "extracted", "rof");
        ctx.RequireData(texturesPath, $"C1 textures");
        ctx.RequireData(rof, $"extracted UI archive (paint patterns)");

        var spec = SessionSpec.Parse(Array.Empty<string>());
        var liveries = new LiveryResolver(spec, rof);
        if (liveries.Patterns.IsEmpty)
        {
            throw new SuiteSkippedException("the extracted UI archive ships no paint patterns");
        }

        var defs = VehicleDefs.Load(ctx.ZrdrPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        var pool = new ProjectilePool(textures, null, null);
        var built = new List<FlightController>();
        var report = new StringBuilder();
        int bareFriends = 0;
        ctx.Host.AddChild(pool);
        try
        {
            var roster = Spawner(ctx, spec, liveries, planesGamez, textures, pool);
            foreach (var (chapter, mission) in Missions)
            {
                string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, chapter);
                string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, mission);
                ctx.RequireData(chapterZrdr, $"{chapter} zrdr");
                ctx.RequireData(missionZrdr, $"{chapter}/{mission} zrdr");
                var plan = CampaignRosterPlan.Build(AiSkills.LoadRoster(missionZrdr), defs,
                    AiNets.Load(chapterZrdr));
                report.AppendLine($"-- {chapter}/{mission}: {plan.Spawns.Count} planned block(s) --");
                bareFriends += CheckMission(ctx, roster, liveries, plan, built, report);
            }

            // The arm the Black Swan rides, seen taken: without it the friendly checks prove only
            // the Fortune Hunters default.
            ctx.Check(bareFriends > 0,
                $"{bareFriends} player-side block(s) whose own def authors no livery were spawned and read");

            CheckNoDefControl(ctx, roster, built, report);
            roster.ClearMembership();
        }
        finally
        {
            foreach (var rig in built)
            {
                rig.Free();
            }
            pool.Free();
            textures.Dispose();
        }

        ctx.WriteArtifact("test-campaign-militia-livery.txt", report.ToString());
        ctx.Note($"spawned {built.Count} aircraft off {Missions.Length} missions and read each one's livery");
    }

    // Every aircraft block of one mission, spawned far enough apart that nothing interacts, with
    // its livery read back off the built controller rather than off the plan. Returns how many
    // player-side blocks took the shipped-skins arm.
    private static int CheckMission(TestContext ctx, FlightRoster roster, LiveryResolver liveries,
        CampaignRosterPlan plan, List<FlightController> built, StringBuilder report)
    {
        int enemies = 0, painted = 0, friends = 0, bare = 0;
        foreach (var block in plan.Spawns)
        {
            if (block.Surface)
            {
                continue;
            }

            var at = new Vector3(built.Count * 400f, 900f, 0f);
            var rig = roster.SpawnAi(CampaignRosterPlan.SpawnFor(
                block, at, at + Vector3.Forward, AiPilot.HoldingCourse(at, at + Vector3.Forward)));
            built.Add(rig);
            var authored = (block.AiDef ?? block.LiveryDef) is { } def
                ? liveries.DefScheme(ctx.ZrdrPath, def) : null;
            report.AppendLine($"  {block.Name}: team {block.Team} def '{block.AiDef ?? block.LiveryDef ?? "-"}' "
                + $"-> {rig.Scheme?.Pattern ?? "(unpainted)"} shipped-skins={rig.ShippedSkins} "
                + $"painter={(rig.Painter == null ? "none" : rig.Painter.PatternMissesAircraft ? "decals only" : "skins")}");

            if (block.Team == AimAssist.PlayerTeam)
            {
                friends++;
                ctx.Check(!rig.ShippedSkins,
                    $"{block.Name} flies on the player's team, outside the shipped-skins reading");
                if (block.LiveryDef is { } own && liveries.DefScheme(ctx.ZrdrPath, own) == null)
                {
                    // The Black Swan: her def authors nothing, so the original sends no scheme and
                    // the Fury's own key skins show, not the player's default pattern.
                    bare++;
                    ctx.Check(rig.Scheme == null && rig.Painter == null,
                        $"…and its own def '{own}' authors no paint_pattern, so it flies the shipped skins, not the player's colours ('{rig.Scheme?.Pattern ?? "-"}')");
                    continue;
                }

                string want = authored?.Pattern ?? LiveryResolver.DefaultPattern;
                ctx.Check(rig.Scheme != null && rig.Scheme.Pattern == want,
                    $"…and draws its def's livery '{want}' (built '{rig.Scheme?.Pattern ?? "-"}')");
                continue;
            }

            enemies++;
            // The fork is still crossed: the block is built under the shipped-skins reading, and
            // the def's own livery outranks it. Without both halves the arm below proves nothing.
            ctx.Check(rig.ShippedSkins,
                $"{block.Name} is built under the shipped-skins reading, as every non-player team is");
            if (authored == null)
            {
                ctx.Check(rig.Scheme == null,
                    $"…and '{block.AiDef ?? "-"}' authors no paint_pattern, so it keeps the shipped skins");
                continue;
            }

            painted++;
            ctx.Check(rig.Scheme != null && rig.Scheme.Pattern == authored.Pattern,
                $"…and wears its militia def's authored '{authored.Pattern}' (built '{rig.Scheme?.Pattern ?? "-"}')");
            ctx.Check(rig.Scheme != null && rig.Scheme.Color1 == authored.Color1
                      && rig.Scheme.NoseDecal == authored.NoseDecal,
                $"…with that def's own first colour and nose decal, not another militia's");
            ctx.Check(rig.Painter is { PatternMissesAircraft: false },
                $"…and the pattern ships skins for '{block.PlaneNode}', so the paint reaches the model");
        }

        ctx.Check(enemies > 0 && painted == enemies,
            $"every one of the mission's {enemies} enemy aeroplane(s) is painted by its own def ({painted})");
        ctx.Check(friends > 0, $"and the mission fields {friends} aircraft on the player's own side");
        return bare;
    }

    // The control the arms above need to be seen able to fail: the same reading on a spawn that
    // names no militia def keeps today's answer, the bare shipped skins. That is the CLI airframe
    // an enemy generator with no vehicle.params label launches.
    private static void CheckNoDefControl(TestContext ctx, FlightRoster roster,
        List<FlightController> built, StringBuilder report)
    {
        var at = new Vector3(built.Count * 400f, 900f, 0f);
        var bare = roster.SpawnAi(new AiSpawn(ctx.PlaneName, at, at + Vector3.Forward,
            AiPilot.HoldingCourse(at, at + Vector3.Forward),
            Team: InstantActionRuntime.EnemyTeam, ShippedSkins: true));
        built.Add(bare);
        report.AppendLine($"  (control) {ctx.PlaneName} with no militia def -> {bare.Scheme?.Pattern ?? "(unpainted)"}");
        ctx.Check(bare.Scheme == null && bare.Painter == null,
            $"a spawn naming no militia def under the same reading stays unpainted ('{bare.Scheme?.Pattern ?? "-"}')");
    }

    private static FlightRoster Spawner(TestContext ctx, SessionSpec spec, LiveryResolver liveries,
        GameZ planesGamez, TextureArchive textures, ProjectilePool pool) =>
        new(FlightRosterPolicy.From(spec), liveries, null, ctx.Host,
            SuiteConstants.AircraftResources(ctx, planesGamez, textures),
            new FlightWorldBindings { Projectiles = pool, Gamez = planesGamez },
            new HumanRosterBindings());
}
