using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.InstantAction;
using CSVM.Session.Objectives;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>The pilot's nine class keys over a session that holds all three classes at once:
/// C3/M01's live zeppelin, that chapter's own gun emplacements switched on, and hostile and
/// friendly aeroplanes around them, read by two panes flying 4 km apart. Each class's Next key
/// walks its own class and reaches every entry of it, its Previous key walks the same order back,
/// its Nearest key restarts the cycle at the head, a class change lands on the head of the class it
/// changes to, and neither pane's key moves the other pane's pick.</summary>
internal static class TargetClassCycleSuites
{
    private const string Chapter = "C3";
    private const string Mission = "M01";

    // C3/M01's two records: one is authored `deactivated` and wakes on OBJECTIVE39, the other
    // flies from the first frame and is the one whose parts this suite cycles.
    private const string LiveZep = "piratezep";

    // The mission's own curated target list. Its targets.zrd flags exactly one node
    // `other_target`, and names these two with no flag at all, which is what separates a mission's
    // chosen structures from every other destructible standing in the same world.
    private const string FlaggedSite = "piratezep/rock_zeppelin";
    private const string UnflaggedTank = "hydrogentank1";
    private const string UnflaggedPoint = "pzhookpoint";

    // The two panes, 4 km apart along the mission's own axis, each with a hostile 300 m off its
    // own nose. Far enough apart that the same two hostiles sort into opposite orders, which is
    // how a shared pick would show itself.
    private static readonly Vector3 PaneGap = new(0f, 0f, 4000f);
    private static readonly Vector3 OffTheNose = new(0f, 0f, -300f);
    private static readonly Vector3 OffTheWing = new(-500f, 0f, 0f);

    [Suite("target-class-cycle",
        "the nine class keys over C3/M01, which carries all three classes at once: the "
        + "Enemy/Objective key walks the hostile aeroplanes and wraps, the Ally key the friendly "
        + "ones, and the Non-Aircraft key the live zeppelin's own parts, every entry of it a "
        + "structure and never an aeroplane, while the chapter's switched-on gun emplacements are "
        + "alive and hostile beside it and reach no cycle at all; each class's Previous key walks "
        + "that same order backwards and wraps "
        + "off the head onto the tail, and its Nearest key restarts the cycle at the head from "
        + "wherever the pilot had reached; a class change lands on the head of the cycle it changes to; "
        + "Target Nothing stays cleared through a rebuild until a class key; two panes "
        + "4 km apart auto-acquire different hostiles, one pane's step leaving the other's pick "
        + "and the other's class alone; the mission's own targets.zrd puts the one node it "
        + "flags other_target on the Non-Aircraft cycle while the destructibles the same table "
        + "names with no flag reach no cycle at all; and the zeppelin's parts ride that cycle only "
        + "while the pilot's selected ordnance carries LOCK_ON, switching off it dropping the "
        + "selected part instead of holding it")]
    internal static void TargetClassCycle(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(texturesPath, $"{Chapter} textures");

        var zepDefs = Zeppelins.Load(missionZrdr);
        var nets = AiNets.Load(chapterZrdr);
        var turretDefs = TurretDefs.Load(ctx.ZrdrPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        // The Non-Aircraft cycle's torpedo gate, read off the install's own weapon table rather
        // than a stand-in def: a zeppelin's parts reach that cycle only while the pilot's selected
        // ordnance carries LOCK_ON, so every pane below flies one.
        var torpedo = weapons.All.FirstOrDefault(ProjectilePool.CarriesLockOn)
            ?? throw new SuiteSkippedException("the weapon table carries no LOCK_ON ordnance");
        if (!zepDefs.Any(d => d.Node == LiveZep))
        {
            throw new SuiteSkippedException($"{Chapter}/{Mission} carries no '{LiveZep}' record");
        }

        // The curated list and the director that edits it: the mission's own story position, so
        // the script runs against the same world the cycles above are read from.
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath))
            ?? throw new SuiteSkippedException($"cm_sequence carries no {Chapter}/{Mission}");
        var script = ObjectiveScript.Load(missionZrdr);
        var missionTargets = MissionTargets.Load(missionZrdr, chapterZrdr);
        var messages = Messages.Load(ctx.MessagesPath);
        ctx.ExtraPrewarmSoundNames = script.SoundGroupNames();

        var report = new StringBuilder();
        ctx.WithWorld(Chapter, collision: false, Mission, world =>
        {
            var textures = new TextureArchive(texturesPath);
            ProjectilePool? live = null;
            ZeppelinRuntime? zeps = null;
            TurretEmplacementRuntime? emplacements = null;
            var switched = new List<Node3D>();
            var rigs = new List<FlightController>();
            try
            {
                var pool = live = new ProjectilePool(textures, null, null);
                ctx.Host.AddChild(pool);
                var runtime = zeps = new ZeppelinRuntime(zepDefs,
                    name => world.Runtime.FindNodes(name, null) is { Count: > 0 } hits ? hits[0] : null,
                    nets);
                ctx.Host.AddChild(runtime);
                runtime.WireDamage(world.Runtime);

                var parts = new List<AimCandidate>();
                runtime.CollectTargetParts(parts);
                ctx.Check(parts.Count > 0,
                    $"'{LiveZep}' offers its own parts to the target pool, count={parts.Count}");
                if (parts.Count == 0)
                {
                    return;
                }

                emplacements = new TurretEmplacementRuntime(turretDefs, weapons,
                    (pattern, scope) => world.Runtime.FindNodes(pattern, scope), pool,
                    world.Runtime.WorldRoot);
                foreach (var site in emplacements.Emplacements
                             .Where(t => !t.Alive).Select(t => t.Site).OfType<Node3D>().Distinct())
                {
                    world.Runtime.SetTargetActive(site, true);
                    switched.Add(site);
                }

                int liveGuns = emplacements.Emplacements.Count(t => t.Alive);
                int hostileGuns = emplacements.Emplacements
                    .Count(t => t.Alive && AimAssist.Hostile(AimAssist.PlayerTeam, t.Team));
                ctx.Check(liveGuns > 0 && hostileGuns > 0,
                    $"CONTROL: {Chapter} places gun emplacements that are alive and hostile to the pilot once their sites are on, alive={liveGuns} hostile={hostileGuns} of {emplacements.Count}");

                // The two panes and the aeroplanes around them, anchored on the zeppelin the
                // non-aircraft cycle is built from so every class sits in one piece of airspace.
                var p1Pose = parts[0].Position + PaneGap;
                var p2Pose = p1Pose + PaneGap;
                var p1Self = Rig(rigs, AimAssist.PlayerTeam);
                var p2Self = Rig(rigs, AimAssist.PlayerTeam);
                var hostileA = Rig(rigs, InstantActionRuntime.EnemyTeam);
                var hostileB = Rig(rigs, InstantActionRuntime.EnemyTeam);
                var wingman = Rig(rigs, AimAssist.PlayerTeam);

                var scan = new AimCandidateSet();
                Offer(scan, p1Self, p1Pose);
                Offer(scan, p2Self, p2Pose);
                Offer(scan, hostileA, p1Pose + OffTheNose);
                Offer(scan, hostileB, p2Pose + OffTheNose);
                Offer(scan, wingman, p1Pose + OffTheWing);
                pool.CollectTurrets(scan);

                var p1 = new Pane(scan, parts, p1Self, p1Pose, torpedo);
                var p2 = new Pane(scan, parts, p2Self, p2Pose, torpedo);
                report.AppendLine($"{Chapter}/{Mission}: {parts.Count} zeppelin part(s), {liveGuns} live emplacement(s) ({hostileGuns} hostile, none selectable), "
                    + $"cycles enemy={p1.Selection.Pool.Enemy.Count} ally={p1.Selection.Pool.Ally.Count} "
                    + $"nonAircraft={p1.Selection.Pool.NonAircraft.Count}");

                CheckCycleMembership(ctx, p1, parts.Count, hostileA, hostileB, p2Self, wingman);
                CheckEachKeyWalksItsClass(ctx, p1);
                CheckPreviousWalksItBack(ctx, p1);
                CheckNearestRestartsTheCycle(ctx, p1);
                CheckClassChangeLandsOnTheHead(ctx, p1);
                CheckClearStaysCleared(ctx, p1);
                CheckPanesAreIndependent(ctx, p1, p2);
                CheckTheCuratedListPicksTheCycle(ctx, world,
                    new Curated(mission, script, missionTargets, messages, scan, parts, p1Self,
                        p1Pose, parts.Count, torpedo),
                    report);
                CheckTheTorpedoGatesTheParts(ctx, p1, parts.Count);
                ctx.Note($"{Chapter}/{Mission}: three class cycles of {p1.Selection.Pool.Enemy.Count}/{p1.Selection.Pool.Ally.Count}/{p1.Selection.Pool.NonAircraft.Count}, each walked forward, back and restarted by its own three keys");
            }
            finally
            {
                foreach (var site in switched)
                {
                    world.Runtime.SetTargetActive(site, false);
                }

                foreach (var rig in rigs)
                {
                    rig.Free();
                }

                emplacements?.Free();
                zeps?.Free();
                live?.Free();
                textures.Dispose();
            }
        });
        ctx.WriteArtifact($"test-target-class-cycle-{Chapter}-{Mission}.txt", report.ToString());
    }

    // Each cycle holds its own class and nothing else. The non-aircraft one is the zeppelin's own
    // parts, never an aeroplane and never one of the live guns standing in the same world.
    private static void CheckCycleMembership(TestContext ctx, Pane pane, int nonAircraftCount,
        object hostileA, object hostileB, object otherPane, object wingman)
    {
        var pool = pane.Selection.Pool;
        ctx.Check(pool.Enemy.Count == 2 && Holds(pool.Enemy, hostileA) && Holds(pool.Enemy, hostileB),
            $"the Enemy/Objective cycle holds the two hostile aeroplanes and nothing else, count={pool.Enemy.Count}");
        ctx.Check(pool.Ally.Count == 2 && Holds(pool.Ally, otherPane) && Holds(pool.Ally, wingman),
            $"the Ally cycle holds the wingman and the other pane's own aeroplane, count={pool.Ally.Count}");
        ctx.Check(pool.NonAircraft.Count == nonAircraftCount,
            $"the Non-Aircraft cycle holds every zeppelin part, count={pool.NonAircraft.Count} of {nonAircraftCount}");
        ctx.Check(pool.NonAircraft.All(t => t.Kind == AimTargetKind.Structure),
            $"…each one a structure, never an aeroplane");
        ctx.Check(!pool.NonAircraft.Any(t => t.Kind == AimTargetKind.Turret),
            $"…and never a gun emplacement, though the scan lists every one of this chapter's and they are switched on");
    }

    // One key per class, each walking the whole of its own cycle and wrapping. This is what the
    // shipped keymap's E, W and R do, dispatched in FlightController.StepTargeting.
    private static void CheckEachKeyWalksItsClass(TestContext ctx, Pane pane)
    {
        foreach (var cls in new[] { TargetClass.Enemy, TargetClass.Ally, TargetClass.NonAircraft })
        {
            pane.Home(cls);
            var expected = Sources(pane.Selection.Ordered);
            var walked = new List<object> { pane.Selection.Current!.Value.Source! };
            for (int i = 1; i < expected.Count; i++)
            {
                pane.Press(cls);
                walked.Add(pane.Selection.Current!.Value.Source!);
            }

            ctx.Check(expected.Count > 1 && walked.SequenceEqual(expected),
                $"the {cls} key walks all {expected.Count} of that cycle in the decoded order");
            pane.Press(cls);
            ctx.Check(ReferenceEquals(pane.Selection.Current!.Value.Source, expected[0]),
                $"…and the next press wraps back to its head");
        }
    }

    // The Previous key of each class, the half a forward-only cycle costs seven presses to reach:
    // it steps the same order backwards and wraps off the head onto the tail.
    private static void CheckPreviousWalksItBack(TestContext ctx, Pane pane)
    {
        foreach (var cls in new[] { TargetClass.Enemy, TargetClass.Ally, TargetClass.NonAircraft })
        {
            pane.Home(cls);
            var expected = Sources(pane.Selection.Ordered);
            pane.Press(cls);
            pane.StepBack(cls);
            ctx.Check(expected.Count > 1
                      && ReferenceEquals(pane.Selection.Current!.Value.Source, expected[0]),
                $"the {cls} Previous key undoes one press of its Next key, over {expected.Count} entries");

            pane.StepBack(cls);
            ctx.Check(ReferenceEquals(pane.Selection.Current!.Value.Source, expected[expected.Count - 1]),
                $"…and a press off the head wraps onto the tail of that same cycle");
        }
    }

    // The class-restarting Nearest key: from anywhere in a cycle it returns to that cycle's head,
    // which is the decoded head of the sector order rather than the nearest thing in space.
    private static void CheckNearestRestartsTheCycle(TestContext ctx, Pane pane)
    {
        foreach (var cls in new[] { TargetClass.Enemy, TargetClass.Ally, TargetClass.NonAircraft })
        {
            pane.Home(cls);
            var head = pane.Selection.Current!.Value.Source;
            int count = pane.Selection.Ordered.Count;
            pane.Press(cls);
            ctx.Check(count > 1 && !ReferenceEquals(pane.Selection.Current!.Value.Source, head),
                $"a press walks the {cls} cycle off its head, over {count} entries");
            pane.Home(cls);
            ctx.Check(pane.Selection.ActiveClass == cls
                      && ReferenceEquals(pane.Selection.Current!.Value.Source, head),
                $"…and that class's Nearest key restarts it there, whatever index the pilot reached");
        }
    }

    private static void CheckClassChangeLandsOnTheHead(TestContext ctx, Pane pane)
    {
        pane.Home(TargetClass.NonAircraft);
        var head = pane.Selection.Current!.Value.Source;
        pane.Home(TargetClass.Enemy);
        pane.Press(TargetClass.Enemy);
        pane.Press(TargetClass.NonAircraft);
        ctx.Check(ReferenceEquals(pane.Selection.Current!.Value.Source, head),
            $"a class change lands on the HEAD of the cycle it changes to, whatever index the pilot had reached in the one they left");
    }

    private static void CheckClearStaysCleared(TestContext ctx, Pane pane)
    {
        pane.Selection.Clear();
        pane.Rebuild();
        ctx.Check(pane.Selection.ActiveClass == null && pane.Selection.Current == null
                  && pane.Selection.Pool.Count == 0,
            $"Target Nothing empties the pool and STAYS empty through a rebuild, which is what keeps the clear cleared");
        pane.Press(TargetClass.NonAircraft);
        ctx.Check(pane.Selection.ActiveClass == TargetClass.NonAircraft
                  && pane.Selection.Current is { Kind: AimTargetKind.Structure },
            $"…and a class key is the way back in, on that key's own cycle");
    }

    private static void CheckPanesAreIndependent(TestContext ctx, Pane p1, Pane p2)
    {
        p1.Home(TargetClass.Enemy);
        p2.Home(TargetClass.Enemy);
        ctx.Check(p1.Selection.Current is { } a && p2.Selection.Current is { } b
                  && !a.IsSameTarget(b),
            $"each pane sorts the same two hostiles against its own pose and holds its own pick");

        var p2Was = p2.Selection.Current!.Value.Source;
        p1.Press(TargetClass.Enemy);
        ctx.Check(ReferenceEquals(p2.Selection.Current!.Value.Source, p2Was),
            $"…P1's step moves P1's marker and leaves P2's where it was");
        p2.Press(TargetClass.NonAircraft);
        ctx.Check(p2.Selection.ActiveClass == TargetClass.NonAircraft
                  && p1.Selection.ActiveClass == TargetClass.Enemy,
            $"…and P2 changing class leaves P1 on the cycle P1 chose");
    }

    // The mission's own targets.zrd is the curated list the original's Non-Aircraft cycle walks.
    // The node it flags `other_target` joins that cycle, the nodes the same table names without a
    // flag reach none, and its `objective` entries keep the Enemy cycle they already rode.
    private static void CheckTheCuratedListPicksTheCycle(TestContext ctx, TestWorld world,
        Curated curated, StringBuilder report)
    {
        var flagged = curated.Targets.For(FlaggedSite);
        var unflagged = curated.Targets.For(UnflaggedTank);
        ctx.Check(flagged.OtherTarget && !flagged.Objective,
            $"{Chapter}/{Mission}'s target table flags '{FlaggedSite}' other_target and nothing else");
        ctx.Check(!unflagged.OtherTarget && !unflagged.Objective && unflagged.Description != null,
            $"…and names '{UnflaggedTank}' in that same table with no flag at all");

        var director = CampaignDirector.Create(curated.Script, curated.Mission,
            CampaignProfileDef.NewProfile("Zachary"), null);
        var listener = curated.Pose;
        director.Attach(new CampaignDirector.WorldInputs
        {
            Runtime = world.Runtime,
            Sounds = world.Runtime.Sounds,
            ListenerPosition = () => listener,
            Rng = new Random(1),
        });
        var offered = new List<AimCandidate>();
        new ObjectiveSites(director, curated.Messages, curated.Targets, world.Runtime)
            .Collect(offered);

        var selection = new TargetSelection();
        selection.Rebuild(curated.Scan, curated.Parts, AimAssist.PlayerTeam, curated.Self,
            curated.Pose, Basis.Identity, offered, curated.Torpedo);
        var pool = selection.Pool;
        report.AppendLine($"curated list: {offered.Count} site(s) offered, cycles enemy={pool.Enemy.Count} "
            + $"ally={pool.Ally.Count} nonAircraft={pool.NonAircraft.Count} over a baseline of {curated.Baseline}");

        ctx.Same(1, Named(pool.NonAircraft, FlaggedSite),
            $"the one entry the mission flags other_target, '{FlaggedSite}', is on the Non-Aircraft cycle, once");
        ctx.Same(0, Named(pool.Enemy, FlaggedSite) + Named(pool.Ally, FlaggedSite),
            $"…and on neither other cycle, which is where the objective flag would have put it");
        ctx.Check(Find(pool.NonAircraft, FlaggedSite) is
        { Objective: false, Kind: AimTargetKind.Structure } site
                  && site.DisplayName.Length > 0 && site.CategoryLine.Length > 0,
            $"…labelled off the table's own description and category, and not sorting ahead of the sectors");
        ctx.Same(curated.Baseline + 1, pool.NonAircraft.Count,
            $"…joining the zeppelin's parts rather than replacing them, and bringing no gun with it");

        foreach (string key in new[] { UnflaggedTank, UnflaggedPoint })
        {
            ctx.Check(ObjectiveSites.ResolveTarget(world.Runtime, ObjectiveTarget.Parse(key)) != null,
                $"'{key}' is a node this world actually builds");
            ctx.Same(0, Named(pool.NonAircraft, key) + Named(pool.Enemy, key) + Named(pool.Ally, key),
                $"…and reaches no cycle at all, the table naming it without flagging it");
        }

        ctx.Check(pool.Enemy.Count(t => t.Objective) > 0,
            $"the same table's objective entries still ride the Enemy cycle they already rode");
    }

    // The torpedo gate on the sub-part channel. A zeppelin's parts stand in for a flag no mission
    // table authors, so the cycle offers them only while the pilot has a LOCK_ON round selected.
    private static void CheckTheTorpedoGatesTheParts(TestContext ctx, Pane pane, int partCount)
    {
        pane.Home(TargetClass.NonAircraft);
        var head = pane.Selection.Current!.Value.Source;
        ctx.Check(partCount > 0 && pane.Selection.Pool.NonAircraft.Count == partCount,
            $"CONTROL: with the torpedo selected the cycle holds all {partCount} of the zeppelin's parts and one of them is picked");

        pane.RebuildWith(null);
        ctx.Check(pane.Selection.Pool.NonAircraft.Count == 0 && pane.Selection.Current == null,
            $"switching off the torpedo empties the Non-Aircraft cycle and DROPS the selected part rather than holding it stale, count={pane.Selection.Pool.NonAircraft.Count}");

        pane.Rebuild();
        ctx.Check(pane.Selection.Pool.NonAircraft.Count == partCount
                  && ReferenceEquals(pane.Selection.Current!.Value.Source, head),
            $"…and selecting it again brings the same parts back, the cycle re-acquiring at its head");
    }

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

    private static CampaignMission? MissionOf(IReadOnlyList<CampaignMission> missions)
    {
        foreach (var mission in missions)
        {
            if (string.Equals(mission.ChapterFolder, Chapter, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mission.MissionFolder, Mission, StringComparison.OrdinalIgnoreCase))
            {
                return mission;
            }
        }

        return null;
    }

    private static List<object> Sources(IReadOnlyList<TargetRef> cycle)
    {
        var into = new List<object>(cycle.Count);
        foreach (var target in cycle)
        {
            into.Add(target.Source!);
        }

        return into;
    }

    private static bool Holds(IReadOnlyList<TargetRef> cycle, object source) =>
        cycle.Any(t => ReferenceEquals(t.Source, source));

    private static FlightController Rig(List<FlightController> owned, int team)
    {
        var rig = new FlightController { IsHumanPiloted = false, Team = team };
        owned.Add(rig);
        return rig;
    }

    private static void Offer(AimCandidateSet scan, FlightController rig, Vector3 at) =>
        scan.AddVehicle(at, Vector3.Zero, rig.Team, live: true, rig);

    // One call's worth of the mission's curated list, the candidates it is read beside, and the
    // Non-Aircraft count a flagged site has to EXTEND rather than replace.
    private readonly record struct Curated(CampaignMission Mission, ObjectiveScript Script,
        MissionTargets Targets, Messages Messages, AimCandidateSet Scan,
        IReadOnlyList<AimCandidate> Parts, object Self, Vector3 Pose, int Baseline,
        WeaponDef Torpedo);

    // One pilot's pane: its own selection over the shared candidates, sorted against its own pose.
    // A press and the rebuild that publishes it are separate calls because the original's handler
    // steps the list the last frame built and the next frame's pass is what shows the result.
    private sealed class Pane
    {
        private readonly AimCandidateSet _scan;
        private readonly IReadOnlyList<AimCandidate> _parts;
        private readonly object _self;
        private readonly Vector3 _pose;
        private readonly WeaponDef _torpedo;

        public Pane(AimCandidateSet scan, IReadOnlyList<AimCandidate> parts, object self,
            Vector3 pose, WeaponDef torpedo)
        {
            _scan = scan;
            _parts = parts;
            _self = self;
            _pose = pose;
            _torpedo = torpedo;
            Rebuild();
        }

        public TargetSelection Selection { get; } = new();

        // The pane flies the torpedo throughout: the sub-part channel is gated on the selected
        // ordnance carrying LOCK_ON, and the Non-Aircraft cycle here is the zeppelin's own parts.
        public void Rebuild() => RebuildWith(_torpedo);

        // The same per-frame pass under another selected ordnance, which is what the gate splits on.
        public void RebuildWith(WeaponDef? weapon) =>
            Selection.Rebuild(_scan, _parts, AimAssist.PlayerTeam, _self, _pose, Basis.Identity,
                null, weapon);

        // The cycle's head, whatever the pilot had reached: the per-class Nearest key, which is
        // also the setup step that makes a walk start at a known entry.
        public void Home(TargetClass cls)
        {
            Selection.Nearest(cls);
            Rebuild();
        }

        // The per-class Previous key, the backward twin of Press.
        public void StepBack(TargetClass cls)
        {
            Selection.Previous(cls);
            Rebuild();
        }

        public void Press(TargetClass cls)
        {
            if (cls == TargetClass.Enemy)
            {
                Selection.NextEnemy();
            }
            else
            {
                Selection.Next(cls);
            }

            Rebuild();
        }
    }
}
