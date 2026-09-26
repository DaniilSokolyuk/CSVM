using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Objectives;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>What the pilot's Non-Aircraft key reaches in the campaign, mission by mission. The
/// answer is one set per mission, the mission's own <c>targets.zrd</c> <c>other_target</c> entries,
/// so the pin is that whole set for all 24 missions plus three of them flown over their real
/// worlds with every gun emplacement switched on and hostile. A gun on the cycle is the wrong
/// admission this pins against: nothing in the install flags one, so no mission of the original
/// offers a turret to that key.</summary>
internal static class CampaignTargetListSuites
{
    // The starting set of every campaign mission, by CM ordinal: the `other_target` keys of the
    // table the mission reads (its own, or its chapter's where it ships none, which is CM06).
    // objectives.zrd then edits the set as the mission runs; the edits are in the closing commit.
    private static readonly Dictionary<int, string[]> StartingOtherTargets = new()
    {
        [1] = new[] { "piratezep/rock_zeppelin" },
        [2] = new[] { "piratezep/rock_zeppelin" },
        [3] = new[]
        {
            "g_tower1", "g_tower2", "g_tower3", "unit01", "unit02", "unit03", "unit04", "unit05",
            "unit06", "unit07", "dock2", "piratezep/rock_zeppelin",
        },
        [4] = new[] { "piratezep/rock_zeppelin" },
        [5] = new[] { "shipwreck" },
        [6] = Array.Empty<string>(),
        [7] = new[] { "trcargo01" },
        [8] = new[] { "tanker", "piratezep/rock_zeppelin", "vostokzep", "slhouse" },
        [9] = new[] { "piratezep/rock_zeppelin" },
        [10] = new[] { "piratezep/rock_zeppelin" },
        [11] = Array.Empty<string>(),
        [12] = Array.Empty<string>(),
        [13] = Array.Empty<string>(),
        [14] = Array.Empty<string>(),
        [15] = new[] { "cargozep2" },
        [16] = new[] { "piratezep/rock_zeppelin" },
        [17] = new[] { "piratezep/rock_zeppelin" },
        [18] = new[] { "piratezep/rock_zeppelin" },
        [19] = new[] { "piratezep/rock_zeppelin" },
        [20] = Array.Empty<string>(),
        [21] = new[] { "piratezep/rock_zeppelin" },
        [22] = Array.Empty<string>(),
        [23] = Array.Empty<string>(),
        [24] = Array.Empty<string>(),
    };

    // The three flown here: the two the comparison at the controls named, and CM07 for its chapter,
    // which places 74 emplacement sites and so is where a gun on the cycle would show.
    private static readonly (int Ordinal, string Chapter, string Mission)[] Flown =
    {
        (1, "C3", "M01"),
        (6, "C1C", "M01"),
        (7, "C1", "M02"),
    };

    [Suite("target-mission-list",
        "the pilot's Non-Aircraft list per campaign mission: all 24 missions' starting sets read "
        + "through the same loader the flight uses, against the install's own target tables, "
        + "CM06 taking its chapter's table because it ships none of its own; then CM01, CM06 and "
        + "CM07 flown over their real worlds, where the cycle holds exactly the flagged keys the "
        + "world resolves (CM01 the Pandora alone, CM06 nothing at all, CM07 the cargo train) "
        + "while every gun emplacement of the chapter is switched on, alive and hostile to the "
        + "pilot and not one of them reaches a cycle")]
    internal static void CampaignTargetList(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var report = new StringBuilder();
        var missions = CampaignSequence.Load(ctx.ZrdrPath);
        ctx.Same(CampaignSequence.MissionCount, missions.Count,
            $"the campaign sequence carries all {CampaignSequence.MissionCount} missions");
        CheckEveryMissionsTable(ctx, missions, report);

        int flownWithGuns = 0;
        foreach (var flown in Flown)
        {
            flownWithGuns += FlyOne(ctx, flown, report) ? 1 : 0;
        }

        ctx.Check(flownWithGuns > 0,
            $"CONTROL: at least one flown mission had live emplacements hostile to the pilot standing in its world, so 'no turret on the cycle' is a refusal and not an empty world, missions={flownWithGuns} of {Flown.Length}");
        ctx.Note($"the Non-Aircraft list of all {missions.Count} campaign missions is its targets.zrd other_target set, and three flown worlds offer no gun");
        ctx.WriteArtifact("test-target-mission-list.txt", report.ToString());
    }

    // The data half: every mission's starting set, through MissionTargets and the same collection
    // pass ObjectiveSites runs, with no director, which is the set the mission begins with.
    private static void CheckEveryMissionsTable(TestContext ctx,
        IReadOnlyList<CampaignMission> missions, StringBuilder report)
    {
        foreach (var mission in missions)
        {
            string chapter = mission.ChapterFolder;
            string folder = mission.MissionFolder;
            var targets = MissionTargets.Load(
                SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder),
                SessionPaths.ChapterZrdr(ctx.DataRoot, chapter));
            var flagged = new List<string>();
            ObjectiveSites.CollectFlagged(TargetFlag.OtherTarget, null, null, targets, flagged);
            string[] expected = StartingOtherTargets[mission.Ordinal];
            report.AppendLine($"CM{mission.Ordinal:00} {chapter}/{folder}: {targets.Count} table "
                + $"entr(ies), {flagged.Count} other_target :: {string.Join(", ", flagged)}");
            ctx.Check(SameSet(flagged, expected),
                $"CM{mission.Ordinal:00} ({chapter}/{folder}) starts with {expected.Length} other-target key(s): [{string.Join(", ", expected)}], read [{string.Join(", ", flagged)}]");
        }
    }

    // One mission flown over its own world: the flagged keys the world actually resolves are the
    // whole cycle, and the chapter's guns, switched on and hostile, are beside it and off it.
    // Returns whether this world offered the hostile-gun control at all.
    private static bool FlyOne(TestContext ctx, (int Ordinal, string Chapter, string Mission) flown,
        StringBuilder report)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, flown.Chapter);
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, flown.Chapter, flown.Mission);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, flown.Chapter);
        ctx.RequireData(chapterZrdr, $"{flown.Chapter} zrdr");
        ctx.RequireData(texturesPath, $"{flown.Chapter} textures");

        var targets = MissionTargets.Load(missionZrdr, chapterZrdr);
        var messages = Messages.Load(ctx.MessagesPath);
        var turretDefs = TurretDefs.Load(ctx.ZrdrPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        bool control = false;
        ctx.WithWorld(flown.Chapter, collision: false, flown.Mission, world =>
        {
            var textures = new TextureArchive(texturesPath);
            ProjectilePool? live = null;
            TurretEmplacementRuntime? emplacements = null;
            var switched = new List<Node3D>();
            var self = new FlightController { IsHumanPiloted = false, Team = AimAssist.PlayerTeam };
            try
            {
                live = new ProjectilePool(textures, null, null);
                ctx.Host.AddChild(live);
                emplacements = new TurretEmplacementRuntime(turretDefs, weapons,
                    (pattern, scope) => world.Runtime.FindNodes(pattern, scope), live,
                    world.Runtime.WorldRoot);
                foreach (var site in emplacements.Emplacements
                             .Where(t => !t.Alive).Select(t => t.Site).OfType<Node3D>().Distinct())
                {
                    world.Runtime.SetTargetActive(site, true);
                    switched.Add(site);
                }

                // Every pool the flight's own targeting pass fills, filled the same way: the guns
                // are here to be refused, and the destructibles to show the curated list at work.
                var scan = new AimCandidateSet();
                live.CollectTurrets(scan);
                scan.AddStructures(world.Runtime.Destructibles);
                int hostileGuns = scan.Turrets.Count(c => c.Live
                    && AimAssist.Hostile(AimAssist.PlayerTeam, c.Team));
                control = hostileGuns > 0;

                var offered = new List<AimCandidate>();
                new ObjectiveSites(messages, targets, world.Runtime).Collect(offered);
                var selection = new TargetSelection();
                selection.Rebuild(scan, null, AimAssist.PlayerTeam, self, Vector3.Zero,
                    Basis.Identity, offered);
                var pool = selection.Pool;

                var resolving = StartingOtherTargets[flown.Ordinal]
                    .Where(k => ObjectiveSites.ResolveTarget(world.Runtime,
                        ObjectiveTarget.Parse(k)) != null)
                    .ToList();
                report.AppendLine($"CM{flown.Ordinal:00} {flown.Chapter}/{flown.Mission} flown: "
                    + $"{emplacements.Count} emplacement(s), {hostileGuns} live and hostile, "
                    + $"{scan.Structures.Count} destructible(s), cycle nonAircraft="
                    + $"{pool.NonAircraft.Count} :: {string.Join(", ", pool.NonAircraft.Select(t => t.Name))}");

                ctx.Check(SameSet(pool.NonAircraft.Select(t => t.Name).ToList(), resolving),
                    $"CM{flown.Ordinal:00}'s Non-Aircraft cycle is the {resolving.Count} flagged key(s) its world resolves, read [{string.Join(", ", pool.NonAircraft.Select(t => t.Name))}]");
                ctx.Same(0, pool.NonAircraft.Count(t => t.Kind == AimTargetKind.Turret),
                    $"…and holds no gun, though {hostileGuns} of the chapter's {emplacements.Count} emplacement(s) are alive and hostile in the same scan");
                ctx.Same(0, pool.Enemy.Count(t => t.Kind == AimTargetKind.Turret)
                    + pool.Ally.Count(t => t.Kind == AimTargetKind.Turret),
                    $"…and no gun lands on either aircraft cycle instead, where the same table's {pool.Enemy.Count} objective site(s) ride");
            }
            finally
            {
                foreach (var site in switched)
                {
                    world.Runtime.SetTargetActive(site, false);
                }

                self.Free();
                emplacements?.Free();
                live?.Free();
                textures.Dispose();
            }
        });
        return control;
    }

    private static bool SameSet(IReadOnlyList<string> read, IReadOnlyList<string> expected) =>
        read.Count == expected.Count
        && expected.All(e => read.Any(r => string.Equals(r, e, StringComparison.OrdinalIgnoreCase)));
}
