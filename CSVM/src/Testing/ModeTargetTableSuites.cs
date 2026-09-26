using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Launch;
using CSVM.Session.Objectives;
using Godot;

namespace CSVM.Testing;

/// <summary>The curated list in the two modes that run no campaign director: C1's Instant Action
/// table flags the radio tower and C1's zeppelin match flags the two rearm bases, and each reaches
/// the pilot's cycles off the table's own keys with no graph behind them. The destructibles
/// standing in the same world, the chapter's crates among them, are in the pilot's own structure
/// scan and reach no cycle at all, which is what the curated list is for. The last arm is the
/// binding rule that decides who gets this feed, including a campaign launch whose profile did not
/// load and which therefore flies with no director to edit one.</summary>
internal static class ModeTargetTableSuites
{
    private const string Chapter = "C1";

    // The radio tower, flagged `other_target` by six IA1 tables. ⚠ Read it in C1 only: the other
    // five chapters' worlds build no node of this name, so their tables offer nothing
    // (docs/org/targeting.md).
    private const string RadioTower = "ap_transmitter";

    private static readonly string[] RearmBases = { "zep_rearm_node_1", "zep_rearm_node_2" };
    private static readonly string[] MatchAirships = { "multiplayer1zep", "multiplayer2zep" };

    // Unflagged world objects of the same chapter: what a pool widened to every structure would put
    // on the cycle beside the flagged ones.
    private static readonly string[] Crates = { "crate10", "crate11", "crate12" };

    private static readonly string[] NoKeys = Array.Empty<string>();

    [Suite("mode-target-table",
        "the mission target table of the two director-free modes, read over C1's own world: the "
        + "Instant Action table's one other_target entry, the radio tower, reaches the pilot's "
        + "Non-Aircraft cycle and neither other cycle; the zeppelin match's two rearm bases reach "
        + "it too while the same table's two airships ride the Enemy cycle as objectives; each "
        + "cycle holds its own flagged half and nothing else; not one of the hundred-odd "
        + "destructibles in the same scan, the chapter's crates among them, reaches any cycle; "
        + "and the binding rule itself, which a campaign launch flying without a director passes")]
    internal static void ModeTargetTable(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");
        var messages = Messages.Load(ctx.MessagesPath);

        var report = new StringBuilder();
        Mode(ctx, messages, report, "IA1", NoKeys, new[] { RadioTower });
        Mode(ctx, messages, report, "MP3", MatchAirships, RearmBases);
        WhoBindsTheTable(ctx, report);
        ctx.Note($"{Chapter}/IA1 and {Chapter}/MP3 offer their own targets.zrd flags with no director bound");
        ctx.WriteArtifact($"test-mode-target-table-{Chapter}.txt", report.ToString());
    }

    // Which sessions reach that feed at all. The director owns the channel when one exists, so a
    // campaign launch whose profile did not load flies director-free and still gets its mission's
    // table, where keying the rule on the launch left it with none.
    private static void WhoBindsTheTable(TestContext ctx, StringBuilder report)
    {
        var campaign = SessionSpec.Parse(new[]
        {
            "--campaign=csvm-golden:6", "--players=4",
            "--plane=player_bhawk,player_bhawk,player_bhawk,player_bhawk", "--det", "--mute",
        });
        ctx.Check(campaign.CampaignProfile != null && campaign.WorldMode,
            $"a --campaign= launch is a world flight that names a profile");
        ctx.Check(Binds(campaign, hasDirector: false),
            $"…and with no director behind it, because the profile did not load, it binds the mission's own table");
        ctx.Check(!Binds(campaign, hasDirector: true),
            $"…while the same launch with a director bound leaves the channel to the graph");

        var mode = SessionSpec.Parse(new[] { "--chapter=C1", "--mission=IA1" });
        ctx.Check(Binds(mode, hasDirector: false),
            $"Instant Action still binds it, the case the rule was written for");
        ctx.Check(!Binds(mode, hasDirector: false, stunting: true),
            $"…a stunt run does not, since it owns the same channel per pane");
        ctx.Check(!Binds(SessionSpec.Parse(new[] { "--stage=empty" }), hasDirector: false),
            $"…and the empty stage does not, having no mission to read a table from");
        ctx.Check(!Binds(mode, hasDirector: false, hasWorld: false) && !Binds(mode, hasDirector: false, rigs: 0),
            $"…and neither does a session with no world runtime or no rig to aim");
        report.AppendLine("binding: campaign without a director yes, with one no; "
            + "IA1 yes; stunt, empty stage, worldless and rigless no");
    }

    private static bool Binds(SessionSpec spec, bool hasDirector, bool stunting = false,
        bool hasWorld = true, int rigs = 1) =>
        GameSession.BindsMissionTargetTable(spec, hasDirector, stunting, hasWorld, rigs);

    // One mode's session: its own table, the director-free feed over the built world, and the
    // three cycles a pilot standing in it would walk.
    private static void Mode(TestContext ctx, Messages messages, StringBuilder report,
        string mission, IReadOnlyList<string> objectives, IReadOnlyList<string> others)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, mission);
        ctx.RequireData(missionZrdr, $"{Chapter}/{mission} zrdr");
        var targets = MissionTargets.Load(missionZrdr, SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter));

        ctx.WithWorld(Chapter, collision: false, mission, world =>
        {
            var offered = new List<AimCandidate>();
            new ObjectiveSites(messages, targets, world.Runtime).Collect(offered);

            // The pilot's own third pool, collected the way FlightController collects it. Nothing
            // reads it into the cycles; it is here to show what the curated list keeps out.
            var scan = new AimCandidateSet();
            scan.AddStructures(world.Runtime.Destructibles);

            var self = new FlightController { IsHumanPiloted = false, Team = AimAssist.PlayerTeam };
            try
            {
                var selection = new TargetSelection();
                selection.Rebuild(scan, null, AimAssist.PlayerTeam, self, Vector3.Zero,
                    Basis.Identity, offered);
                var pool = selection.Pool;
                report.AppendLine($"{Chapter}/{mission}: {targets.Count} table entr(ies), "
                    + $"{offered.Count} site(s) offered, cycles enemy={pool.Enemy.Count} "
                    + $"ally={pool.Ally.Count} nonAircraft={pool.NonAircraft.Count}, beside "
                    + $"{scan.Structures.Count} destructible(s) in the same scan");

                CheckFlagged(ctx, mission, targets, pool, objectives, others);
                CheckTheUnflaggedStayOff(ctx, mission, world, scan, pool, report);
            }
            finally
            {
                self.Free();
            }
        });
    }

    // Each flagged key on the cycle its own flag picks, and on no other.
    private static void CheckFlagged(TestContext ctx, string mission, MissionTargets targets,
        TargetPool pool, IReadOnlyList<string> objectives, IReadOnlyList<string> others)
    {
        foreach (string key in others)
        {
            ctx.Check(targets.For(key).OtherTarget && !targets.For(key).Objective,
                $"{Chapter}/{mission}'s own table flags '{key}' other_target");
            ctx.Same(1, Named(pool.NonAircraft, key),
                $"…and the director-free feed puts it on the Non-Aircraft cycle, once");
            ctx.Same(0, Named(pool.Enemy, key) + Named(pool.Ally, key),
                $"…and on neither other cycle");
            ctx.Check(Find(pool.NonAircraft, key) is
            { Objective: false, Kind: AimTargetKind.Structure } site
                && site.DisplayName.Length > 0,
                $"…labelled off the table's own description rather than the bare node name");
        }

        foreach (string key in objectives)
        {
            ctx.Check(targets.For(key).Objective,
                $"{Chapter}/{mission}'s own table flags '{key}' objective");
            ctx.Same(1, Named(pool.Enemy, key),
                $"…so the same feed rides it on the Enemy cycle instead, once");
            ctx.Same(0, Named(pool.NonAircraft, key),
                $"…and never on the Non-Aircraft one");
        }

        // No aeroplane and no emplacement is in this scan, so each cycle's whole content is the
        // table's own flagged half and the counts are exact.
        ctx.Same(others.Count, pool.NonAircraft.Count,
            $"{Chapter}/{mission}'s Non-Aircraft cycle is its table's other_target entries and nothing else");
        ctx.Same(objectives.Count, pool.Enemy.Count,
            $"…and its Enemy cycle the table's objective entries and nothing else");
    }

    // The whole point of a curated list: the world's other destructibles are in the pilot's scan
    // and stay off every cycle, which a pool reading that scan would undo.
    private static void CheckTheUnflaggedStayOff(TestContext ctx, string mission, TestWorld world,
        AimCandidateSet scan, TargetPool pool, StringBuilder report)
    {
        int inScan = 0;
        foreach (string key in Crates)
        {
            var node = ObjectiveSites.ResolveTarget(world.Runtime, ObjectiveTarget.Parse(key));
            ctx.Check(node != null, $"'{key}' is a node {Chapter} actually builds");
            if (node == null)
            {
                continue;
            }

            if (scan.Structures.Any(c => ReferenceEquals(AnchorOf(c.Source), node)))
            {
                inScan++;
            }

            ctx.Same(0, Anchored(pool.NonAircraft, node) + Anchored(pool.Enemy, node)
                + Anchored(pool.Ally, node) + Named(pool.NonAircraft, key),
                $"…and reaches no cycle in {Chapter}/{mission}, the table never naming it");
        }

        // How many of them the mode's own world has switched on varies by mission variant, so the
        // count is reported rather than asserted; what is asserted is that none of the scan reaches
        // a cycle at all.
        report.AppendLine($"{Chapter}/{mission}: {inScan} of {Crates.Length} crate(s) live in the "
            + $"structure scan, none of them on any cycle");
        ctx.Check(scan.Structures.Count > pool.NonAircraft.Count,
            $"{Chapter}/{mission} scans {scan.Structures.Count} destructible(s) against a Non-Aircraft cycle of {pool.NonAircraft.Count}");
        ctx.Same(0, scan.Structures.Count(c => AnchorOf(c.Source) is { } a
                && (Anchored(pool.NonAircraft, a) + Anchored(pool.Enemy, a) + Anchored(pool.Ally, a)) > 0),
            $"…and not one of that scan reaches a cycle, which is what widening the pool to it would undo");
    }

    private static Node3D? AnchorOf(object? source) =>
        source is DestructibleRegistry.Instance inst && GodotObject.IsInstanceValid(inst.Anchor)
            ? inst.Anchor : null;

    private static int Anchored(IReadOnlyList<TargetRef> cycle, Node3D node) =>
        cycle.Count(t => ReferenceEquals(AnchorOf(t.Source), node));

    private static int Named(IReadOnlyList<TargetRef> cycle, string name) =>
        cycle.Count(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    private static TargetRef? Find(IReadOnlyList<TargetRef> cycle, string name)
    {
        foreach (var target in cycle)
        {
            if (string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }
        }

        return null;
    }
}
