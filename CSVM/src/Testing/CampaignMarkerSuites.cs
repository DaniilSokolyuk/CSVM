using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Flight.Hud;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using Godot;

namespace CSVM.Testing;

/// <summary>The suite over the campaign's objective sites: the store that says where the mission
/// wants the player to go, and the target cycle that finally consumes it. The sites go through the
/// ordinary selection, so what is asserted is a pool, a cycle and a sticky selection.</summary>
internal static class CampaignMarkerSuites
{
    // The story position flown. Chapter, mission folder and every node name below come out of
    // cm_sequence and that mission's own data, so nothing here names a shipped world node.
    private const int FirstSeq = 0;

    // The story position whose report this is (C1/M04): the hull's own child and a ground node
    // share the child's name, which is what a path has to tell apart.
    private const int PathSeq = 8;
    private const string PathParent = "piratezep";
    private const string PathChild = "rock_zeppelin";
    private const string PathKey = "piratezep/rock_zeppelin";
    private const string PathLabel = "MSG_OBJ_DEFEND";

    // How far the hull's own child must stand from every other node of that name. The hull is
    // 400 m across, so a marker on its geometry can be several hundred metres from its origin.
    private const float PathApart = 500f;

    // The story position whose objective labels are pinned (C1C/M01): the one mission with no
    // targets.zrd of its own, labelled by the chapter's through the reader search path.
    private const int LabelSeq = 5;

    // The story position whose attack balloons are marked (C1/M05), the first of its nine sites,
    // and the two halves the site's group holds: the balloon the objective watches and the
    // lifeboat that drops out of it and outlives it.
    private const int BalloonSeq = 9;
    private const string BalloonSite = "lifesaver11";
    private const string BalloonNode = "healthy_balloon";
    private const string BalloonPath = "lifesaver11/lifesaver/lifeballoon";
    private const string BoatPath = "lifesaver/lifeboat";
    private const string BalloonDescription = "MSG_OBJ_ATTACKBALLOON";
    private const int BalloonCount = 9;

    // How far above the balloon node's own origin the marker may still stand. The balloon's
    // envelope reaches about 8 m over it, so anything higher is off the balloon entirely.
    private const float BalloonReach = 10f;

    private const float AltitudeTolerance = 0.5f;

    // OBJECTIVE10, the shipped script's own wake trigger for wave 1: WAKE_ANIM attack_wave1 then
    // ADD_OBJECTIVE_TARGET, which is what carries the group from its rest pose through the
    // SiScript entrance that passes close to the water before the rise sequence lifts it.
    private const int WakeObjective = 10;
    private const float WakeStepDt = 0.1f;
    private const int WakeSteps = 700; // 70 s: past the entrance's lowest pass over the water.

    // ScanForCompletion resolves one objective per tick, so a removal needs more than one.
    private const float RetireSeconds = 3f;

    // How far a site's world node is moved to prove the candidate follows it.
    private static readonly Vector3 Shove = new(600f, 0f, -400f);

    // The climb the balloon assembly is flown by, well past the 26 m the assembly is tall.
    private static readonly Vector3 Climb = new(0f, 300f, 0f);

    // C1C/M01's three flown objective targets as the script authors them, with the label lines
    // the chapter's targets.zrd and messages.json give each, and whether the action draws red.
    private static readonly (string Key, string Line1, string Line2, bool Red)[] LabelCases =
    {
        ("workersvoyagezep", "Zeppelin [Disable] -", "Worker's Voyage", true),
        ("wv_tailhook/peoplehook", "[Dock] -", "Worker's Voyage Docking Hook", false),
        ("pzhookpoint", "[Dock] -", "Pandora Docking Hook", false),
    };

    /// <summary>Drives the campaign's first mission against its BUILT world: the objective sites
    /// its <c>targets.zrd</c> flags reach the player's Enemy cycle carrying the mission's objective
    /// flag, exactly one is selected at a time, a site under a node that moves is marked where it
    /// now is, and flying a site's own <c>TRAVELERS</c> approach retires it and leaves the
    /// rest.</summary>
    // BL-468: the objective-target store had no consumer, so a flown mission never showed the
    // player where its sites were.
    [Suite("campaign-objective-markers",
        "the campaign's objective markers over the first story mission's BUILT world: every "
        + "site its targets.zrd flags carries a marker with the original's category line over "
        + "the site name, in the decoded blue, sitting on the world node the mission named, "
        + "and flying one site's own TRAVELERS approach retires that marker alone")]
    internal static void CampaignObjectiveMarkers(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), FirstSeq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {FirstSeq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder);
        ctx.RequireData(missionZrdr, $"{chapter}/{folder} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");

        var script = ObjectiveScript.Load(missionZrdr);
        var targets = MissionTargets.Load(missionZrdr, SessionPaths.ChapterZrdr(ctx.DataRoot, chapter));
        var messages = Messages.Load(ctx.MessagesPath);
        var report = new StringBuilder();
        report.AppendLine($"seq {FirstSeq} -> {chapter}/{folder}: " +
            $"{targets.Count} target entries, {script.Objectives.Count} objectives");
        ctx.Check(FlaggedCount(targets) > 0,
            $"the flown mission's target table flags at least one node as an objective");

        var profile = CampaignProfileDef.NewProfile("Zachary");
        var director = CampaignDirector.Create(script, mission, profile, null);
        ctx.ExtraPrewarmSoundNames = script.SoundGroupNames();
        ctx.WithWorld(chapter, collision: false, folder, world =>
            Drive(ctx, world, director, script, targets, messages, report));

        ctx.WriteArtifact($"test-campaign-objective-markers-{chapter}.txt", report.ToString());
        ctx.Note($"drove {chapter}/{folder}'s objective sites through the target cycle");
    }

    /// <summary>A path-authored site over the mission whose report this is: with two
    /// <c>rock_zeppelin</c> nodes in the world, <c>ADD_OBJECTIVE_TARGET [[piratezep,
    /// rock_zeppelin]]</c> offers ONE site, standing on the hull's own child, with the
    /// <c>SET_HELP_LABEL</c> written against the same path on it; the hull's root and the ground
    /// node stay unmarked.</summary>
    [Suite("campaign-objective-target-path",
        "a path-authored objective target over C1/M04's BUILT world: with two rock_zeppelin "
        + "nodes present, ADD_OBJECTIVE_TARGET [[piratezep, rock_zeppelin]] is held as one key, "
        + "offers one site standing on the hull's own child rather than the ground node, and "
        + "carries the SET_HELP_LABEL written against the same path")]
    internal static void CampaignObjectiveTargetPath(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), PathSeq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {PathSeq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder);
        ctx.RequireData(missionZrdr, $"{chapter}/{folder} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");

        var shipped = ObjectiveScript.Load(missionZrdr);
        ctx.Check(AuthorsPath(shipped), $"{chapter}/{folder} authors ADD_OBJECTIVE_TARGET [[{PathParent}, {PathChild}]]");

        // The same directives the shipped objective carries, on a conditionless objective so
        // they run on the first tick instead of after the hatch animations it waits on.
        var script = ObjectiveScript.Parse(new List<object?>
        {
            new List<object?>
            {
                "OBJECTIVE1", new List<object?>
                {
                    "ADD_OBJECTIVE_TARGET", new List<object?> { new List<object?> { PathParent, PathChild } },
                    "SET_HELP_LABEL", new List<object?> { new List<object?> { PathParent, PathChild }, PathLabel },
                },
            },
        });
        var targets = MissionTargets.Load(missionZrdr, SessionPaths.ChapterZrdr(ctx.DataRoot, chapter));
        var messages = Messages.Load(ctx.MessagesPath);
        var report = new StringBuilder();
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var director = CampaignDirector.Create(script, mission, profile, null);
        ctx.WithWorld(chapter, collision: false, folder, world =>
            DrivePath(ctx, world, director, targets, messages, report));

        ctx.WriteArtifact($"test-campaign-objective-target-path-{chapter}.txt", report.ToString());
        ctx.Note($"{chapter}/{folder}: [{PathParent}, {PathChild}] marks the hull's own child alone");
    }

    /// <summary>The marker labels over C1C/M01's BUILT world: the mission's three flown objective
    /// targets, added as the script adds them, each reach the Enemy cycle with the original's
    /// category line and proper name (the chapter's <c>targets.zrd</c>, since the mission ships
    /// none) and the colour its action earns, with the node name kept as the identity alone.
    /// The mission's own table would label nothing, which is the raw-node-name marker.</summary>
    [Suite("campaign-objective-labels",
        "the objective marker's text over C1C/M01's BUILT world, the one mission with no "
        + "targets.zrd of its own: its three flown targets take the chapter's table through the "
        + "reader search path and label 'Zeppelin [Disable] -' over 'Worker's Voyage' in red and "
        + "'[Dock] -' over each docking hook's proper name in blue, the node key kept as identity")]
    internal static void CampaignObjectiveLabels(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), LabelSeq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {LabelSeq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, chapter);
        ctx.RequireData(missionZrdr, $"{chapter}/{folder} zrdr");
        ctx.RequireData(chapterZrdr, $"{chapter} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");

        var missionOnly = MissionTargets.Load(missionZrdr);
        var targets = MissionTargets.Load(missionZrdr, chapterZrdr);
        var report = new StringBuilder();
        report.AppendLine($"seq {LabelSeq} -> {chapter}/{folder}: mission table {missionOnly.Count} " +
            $"entries, with the chapter's {targets.Count}");
        ctx.Same(0, missionOnly.Count, $"{chapter}/{folder} ships no targets.zrd of its own");
        ctx.Check(targets.Count > 0, $"and the chapter's table labels it through the reader search path");

        var adds = new List<object?>();
        foreach (var (key, _, _, _) in LabelCases)
        {
            var target = ObjectiveTarget.Parse(key);
            adds.Add(target.Scoped ? new List<object?>(target.Path) : target.Node);
        }

        var script = ObjectiveScript.Parse(new List<object?>
        {
            new List<object?> { "OBJECTIVE1", new List<object?> { "ADD_OBJECTIVE_TARGET", adds } },
        });
        var messages = Messages.Load(ctx.MessagesPath);
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var director = CampaignDirector.Create(script, mission, profile, null);
        ctx.WithWorld(chapter, collision: false, folder, world =>
            DriveLabels(ctx, world, director, targets, messages, report));

        ctx.WriteArtifact($"test-campaign-objective-labels-{chapter}.txt", report.ToString());
        ctx.Note($"{chapter}/{folder}: the three objective markers read the original's verb and proper name");
    }

    /// <summary>CM10 (C1/M05)'s attack-balloon markers over its BUILT world. Each
    /// <c>lifesaverNM</c> site is a group node standing on the water with the balloon hung above it
    /// and the lifeboat at its own origin, so the marker belongs on the group's geometry rather
    /// than on the node. Asserted over the shipped table and script, then flown at two balloon
    /// altitudes and retired by the balloon; then, over a second BUILT world, driven through the
    /// shipped OBJECTIVE10 wake trigger, sampled across the wave's own SiScript entrance.</summary>
    [Suite("campaign-balloon-marker",
        "CM10's attack-balloon markers over C1/M05's BUILT world: its nine lifesaver sites are "
        + "group nodes standing on the water with the balloon hung above and the lifeboat at "
        + "the group's own origin, so the marker stands on the group's geometry clear of the "
        + "boat, flies with the assembly and rises when the balloon alone rises, and retires "
        + "when the balloon its objective watches goes inactive while the boat is still afloat; "
        + "driven through the shipped OBJECTIVE10 wake trigger, the marker is offered from the "
        + "tick the wave wakes and tracks the live assembly through its whole SiScript entrance, "
        + "never a stale reading that predates the balloon")]
    internal static void CampaignBalloonMarker(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), BalloonSeq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {BalloonSeq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder);
        ctx.RequireData(missionZrdr, $"{chapter}/{folder} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");

        var shipped = ObjectiveScript.Load(missionZrdr);
        var targets = MissionTargets.Load(missionZrdr, SessionPaths.ChapterZrdr(ctx.DataRoot, chapter));
        var messages = Messages.Load(ctx.MessagesPath);
        var report = new StringBuilder();
        report.AppendLine($"seq {BalloonSeq} -> {chapter}/{folder}");
        CheckBalloonData(ctx, shipped, targets, report);

        // The shipped ADD is on a dormant wave objective and the REMOVE on the one that watches the
        // balloon; both are restated conditionless here so they run on the first tick.
        var script = ObjectiveScript.Parse(new List<object?>
        {
            new List<object?>
            {
                "OBJECTIVE1", new List<object?>
                {
                    "ADD_OBJECTIVE_TARGET", new List<object?> { BalloonSite },
                },
                "OBJECTIVE2", new List<object?>
                {
                    "INACTIVE1", new List<object?> { BalloonSite, BalloonNode },
                    "REMOVE_OBJECTIVE_TARGET", new List<object?> { BalloonSite },
                },
            },
        });
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var director = CampaignDirector.Create(script, mission, profile, null);
        ctx.WithWorld(chapter, collision: false, folder, world =>
            DriveBalloon(ctx, world, director, targets, messages, report));

        // A second, separate BUILT world: the wake-driven drive triggers the real attack_wave1
        // animation, which must not run in the same world the check above poses by hand.
        var wakeDirector = CampaignDirector.Create(shipped, mission, profile, null);
        ctx.WithWorld(chapter, collision: false, folder, world =>
            DriveBalloonWake(ctx, world, wakeDirector, targets, messages, report));

        ctx.WriteArtifact($"test-campaign-balloon-marker-{chapter}.txt", report.ToString());
        ctx.Note($"{chapter}/{folder}: the attack-balloon marker stands on the balloon group's geometry, at two altitudes, retires with the balloon, and tracks the live wave-1 entrance from the tick it wakes");
    }

    // The shipped files: nine attack-balloon sites, each retired by its own balloon going inactive.
    private static void CheckBalloonData(TestContext ctx, ObjectiveScript script,
        MissionTargets targets, StringBuilder report)
    {
        int balloons = 0;
        foreach (var entry in targets.ByNode)
        {
            if (string.Equals(entry.Value.Description, BalloonDescription, StringComparison.Ordinal))
            {
                balloons++;
            }
        }

        ctx.Same(BalloonCount, balloons, $"targets.zrd describes the mission's attack-balloon sites");

        int retired = 0;
        foreach (var def in script.Objectives)
        {
            foreach (var path in def.Inactive)
            {
                if (path.Count == 2 && Removes(def, path[0])
                    && string.Equals(path[1], BalloonNode, StringComparison.OrdinalIgnoreCase))
                {
                    retired++;
                }
            }
        }

        report.AppendLine($"{balloons} attack-balloon target entries, {retired} retired by their own '{BalloonNode}'");
        ctx.Same(BalloonCount, retired,
            $"and one objective per site removes it when that site's own '{BalloonNode}' goes inactive");
    }

    private static void DriveBalloon(TestContext ctx, TestWorld world, CampaignDirector director,
        MissionTargets targets, Messages messages, StringBuilder report)
    {
        var listener = ctx.Camera.GlobalPosition;
        director.Attach(new CampaignDirector.WorldInputs
        {
            Runtime = world.Runtime,
            Gamez = world.Gamez,
            Sounds = world.Runtime.Sounds,
            ListenerPosition = () => listener,
            Rng = new Random(1),
        });
        var graph = director.Graph!;
        graph.Step(0.1f);

        var group = ObjectiveSites.ResolveTarget(world.Runtime, ObjectiveTarget.Parse(BalloonSite));
        var balloon = ObjectiveSites.ResolveTarget(world.Runtime, ObjectiveTarget.Parse(BalloonPath));
        var healthy = ObjectiveSites.ResolveTarget(world.Runtime, ObjectiveTarget.Parse(BalloonSite + "/" + BalloonNode));
        var boat = ObjectiveSites.ResolveTarget(world.Runtime, ObjectiveTarget.Parse(BalloonSite + "/" + BoatPath));
        ctx.Check(group != null && balloon != null && healthy != null && boat != null,
            $"'{BalloonSite}' builds its group node, its balloon and its lifeboat");
        if (group == null || balloon == null || healthy == null || boat == null)
        {
            return;
        }

        float boatTop = WorldBox(boat)?.End.Y ?? group.GlobalPosition.Y;
        report.AppendLine($"'{BalloonSite}' node at {group.GlobalPosition}, balloon at {balloon.GlobalPosition}, "
            + $"lifeboat top y {boatTop:0.0}, anchor {ObjectiveSites.SiteAnchor(group)}");
        ctx.Check(balloon.GlobalPosition.Y - group.GlobalPosition.Y > boatTop - group.GlobalPosition.Y,
            $"the balloon hangs above the lifeboat the same group holds, so the two are not one point");

        var sites = new ObjectiveSites(director, messages, targets, world.Runtime);
        var pilot = new Pilot(sites);
        pilot.Fly(listener);
        if (Find(pilot.Selection.Pool.Enemy, BalloonSite) is not { } marked)
        {
            ctx.Check(false, $"'{BalloonSite}' is offered as an objective site");
            return;
        }

        report.AppendLine($"offered '{marked.Name}' \"{marked.DisplayName}\" / \"{marked.CategoryLine}\" at {marked.Position}");
        ctx.Check(marked.Position.Y > boatTop,
            $"the marker stands clear of the lifeboat below it ({marked.Position.Y:0.0} m against the boat's {boatTop:0.0} m)");
        ctx.Check(marked.Position.Y <= balloon.GlobalPosition.Y + BalloonReach,
            $"and within the balloon it names rather than above it ({marked.Position.Y:0.0} m)");
        CheckBalloonAltitudes(ctx, group, balloon, pilot, report);
        CheckBalloonRetires(ctx, graph, healthy, boat, pilot, report);
    }

    // Two altitudes: the whole assembly flown as its own SiScript flies it, then the balloon alone
    // rising off its boat. A marker read off the balloon's live geometry moves for both; one lifted
    // off the group node by a constant moves only for the first.
    private static void CheckBalloonAltitudes(TestContext ctx, Node3D group, Node3D balloon,
        Pilot pilot, StringBuilder report)
    {
        var assembly = group.GetChild<Node3D>(0);
        var restAssembly = assembly.GlobalPosition;
        var restBalloon = balloon.GlobalPosition;
        float before = Find(pilot.Selection.Pool.Enemy, BalloonSite)!.Value.Position.Y;

        assembly.GlobalPosition = restAssembly + Climb;
        pilot.Fly(pilot.Position);
        var flown = Find(pilot.Selection.Pool.Enemy, BalloonSite);
        assembly.GlobalPosition = restAssembly;

        balloon.GlobalPosition = restBalloon + Climb;
        pilot.Fly(pilot.Position);
        var risen = Find(pilot.Selection.Pool.Enemy, BalloonSite);
        balloon.GlobalPosition = restBalloon;
        pilot.Fly(pilot.Position);

        report.AppendLine($"marker y {before:0.0} -> assembly climbed {flown?.Position.Y ?? float.NaN:0.0}"
            + $" -> balloon alone climbed {risen?.Position.Y ?? float.NaN:0.0}");
        ctx.Check(flown is { } f && Mathf.Abs(f.Position.Y - (before + Climb.Y)) < AltitudeTolerance,
            $"the marker flies with the whole assembly, so it is right at every altitude the wave attacks from");
        ctx.Check(risen is { } r && r.Position.Y > before + AltitudeTolerance,
            $"and rises when only the balloon rises, so it is not a constant lift off the group node");
    }

    // The marker belongs to the balloon: the boat outlives it and must not keep the marker alive.
    private static void CheckBalloonRetires(TestContext ctx, ObjectiveGraph graph, Node3D healthy,
        Node3D boat, Pilot pilot, StringBuilder report)
    {
        healthy.Visible = false;
        for (float t = 0f; t < RetireSeconds; t += 0.1f)
        {
            graph.Step(0.1f);
        }

        pilot.Fly(pilot.Position);
        var left = Find(pilot.Selection.Pool.Enemy, BalloonSite);
        report.AppendLine($"balloon killed with the boat still afloat ({boat.Visible}): site "
            + $"{(left == null ? "retired" : "still offered")}, live target keys "
            + $"[{string.Join(",", graph.ObjectiveTargets)}]");
        healthy.Visible = true;
        ctx.Check(boat.Visible, $"the lifeboat is still there when the balloon dies");
        ctx.Check(left == null, $"and the marker retires with the balloon rather than staying on the boat");
    }

    private static Aabb? WorldBox(Node3D node)
    {
        Aabb? merged = null;
        void Walk(Node current)
        {
            foreach (var child in current.GetChildren())
            {
                if (child is MeshInstance3D { Mesh: not null } mesh)
                {
                    var box = mesh.GlobalTransform * mesh.GetAabb();
                    merged = merged?.Merge(box) ?? box;
                }

                Walk(child);
            }
        }

        Walk(node);
        return merged;
    }

    // The wave-1 wake drive over its own BUILT world: the site is offered the instant OBJECTIVE10
    // wakes, and its SiScript entrance carries the whole assembly from a hidden altitude down
    // past the water before the rise sequence lifts it to attack height. Samples the marker every
    // tick across that entrance and checks it never reads outside the group's own currently built
    // geometry, which is what a stale, balloon-less merge would do.
    private static void DriveBalloonWake(TestContext ctx, TestWorld world, CampaignDirector director,
        MissionTargets targets, Messages messages, StringBuilder report)
    {
        var listener = ctx.Camera.GlobalPosition;
        director.Attach(new CampaignDirector.WorldInputs
        {
            Runtime = world.Runtime,
            Gamez = world.Gamez,
            Sounds = world.Runtime.Sounds,
            ListenerPosition = () => listener,
            Rng = new Random(1),
        });
        var graph = director.Graph!;
        graph.Step(WakeStepDt);

        var group = ObjectiveSites.ResolveTarget(world.Runtime, ObjectiveTarget.Parse(BalloonSite));
        ctx.Check(group != null, $"'{BalloonSite}' builds its group node");
        if (group == null)
        {
            return;
        }

        // OBJECTIVE10 itself gates on nothing once awake, so its ADD_OBJECTIVE_TARGET fires on
        // completion the very next Step after Wake rather than inside Wake itself; a real session
        // never renders the gap, since both happen well inside one frame's Step call.
        graph.Wake(WakeObjective);
        graph.Step(WakeStepDt);

        var sites = new ObjectiveSites(director, messages, targets, world.Runtime);
        var pilot = new Pilot(sites);
        int offered = 0, tracked = 0;
        float worstMargin = float.MaxValue;
        string worstAt = "";
        for (int i = 0; i < WakeSteps; i++)
        {
            pilot.Fly(listener);
            if (Find(pilot.Selection.Pool.Enemy, BalloonSite) is { } marked && WorldBox(group) is { } built)
            {
                offered++;
                float margin = Mathf.Min(marked.Position.Y - built.Position.Y, built.End.Y - marked.Position.Y);
                tracked += margin >= -AltitudeTolerance ? 1 : 0;
                if (margin < worstMargin)
                {
                    worstMargin = margin;
                    worstAt = $"t={i * WakeStepDt:0.0}s anchor.y={marked.Position.Y:0.0} built.y=[{built.Position.Y:0.0}..{built.End.Y:0.0}]";
                }
            }

            world.Runtime.Advance(WakeStepDt);
            graph.Step(WakeStepDt);
        }

        report.AppendLine($"wave 1 wake: offered {offered}/{WakeSteps} sampled ticks, anchor inside the "
            + $"currently built mesh bounds on {tracked}/{offered}, worst margin {worstMargin:0.00} m ({worstAt})");
        ctx.Same(WakeSteps, offered,
            $"'{BalloonSite}' is offered from the tick its wave wakes through the whole sampled entrance");
        ctx.Same(offered, tracked,
            $"and the marker never reads outside the group's own live geometry, so it is never a stale reading that predates the balloon");
    }

    private static void DriveLabels(TestContext ctx, TestWorld world, CampaignDirector director,
        MissionTargets targets, Messages messages, StringBuilder report)
    {
        var listener = ctx.Camera.GlobalPosition;
        director.Attach(new CampaignDirector.WorldInputs
        {
            Runtime = world.Runtime,
            Sounds = world.Runtime.Sounds,
            ListenerPosition = () => listener,
            Rng = new Random(1),
        });
        director.Graph!.Step(0.1f);

        var pilot = new Pilot(new ObjectiveSites(director, messages, targets, world.Runtime));
        pilot.Fly(listener);
        // The destructive colour, read off the decoded rule itself rather than a palette constant.
        var hudRed = TargetHud.MarkerColor(TargetRef.ForStructure(
            new AimCandidate { Team = AimAssist.PlayerTeam, Live = true, Source = new object() },
            TargetClass.Enemy, "destroy", "Zeppelin", "Destroy", objective: true), AimAssist.PlayerTeam);
        var lines = new List<string>();
        foreach (var (key, line1, line2, red) in LabelCases)
        {
            if (Find(pilot.Selection.Pool.Enemy, key) is not { } target)
            {
                ctx.Check(false, $"'{key}' is offered on the Enemy cycle");
                continue;
            }

            lines.Clear();
            TargetHud.LabelLines(target, null, lines);
            var colour = TargetHud.MarkerColor(target, AimAssist.PlayerTeam);
            report.AppendLine($"'{key}': \"{string.Join("\" / \"", lines)}\" colour {colour}");
            ctx.Check(lines.Count == 2 && lines[0] == line1 && lines[1] == line2,
                $"'{key}' labels \"{line1}\" over \"{line2}\", not its node name (\"{string.Join("\" / \"", lines)}\")");
            ctx.Check(colour == (red ? hudRed : MarkerDraw.HudBlue),
                $"'{key}' draws {(red ? "red, a destructive action" : "blue, a non-destructive action")}, whatever team the node is on");
            ctx.Check(target.Name == key,
                $"'{key}' keeps the node key as its identity for --target= ('{target.Name}')");
        }
    }

    private static void DrivePath(TestContext ctx, TestWorld world, CampaignDirector director,
        MissionTargets targets, Messages messages, StringBuilder report)
    {
        var listener = ctx.Camera.GlobalPosition;
        director.Attach(new CampaignDirector.WorldInputs
        {
            Runtime = world.Runtime,
            Sounds = world.Runtime.Sounds,
            ListenerPosition = () => listener,
            Rng = new Random(1),
        });
        var graph = director.Graph!;
        graph.Step(0.1f);
        ctx.Check(graph.IsObjectiveTarget(PathKey), $"the graph holds the path as one key '{PathKey}'");
        ctx.Check(!graph.IsObjectiveTarget(PathParent) && !graph.IsObjectiveTarget(PathChild),
            $"and neither bare name on its own");

        var all = world.Runtime.FindNodes(PathChild, null);
        var hull = ObjectiveSites.ResolveTarget(world.Runtime, new ObjectiveTarget(new[] { PathParent }));
        var child = ObjectiveSites.ResolveTarget(world.Runtime, ObjectiveTarget.Parse(PathKey));
        report.AppendLine($"{all.Count} '{PathChild}' nodes; hull {hull?.GlobalPosition.ToString() ?? "-"}, " +
            $"its child {child?.GlobalPosition.ToString() ?? "-"}");
        ctx.Check(all.Count >= 2, $"the world carries more than one '{PathChild}' ({all.Count})");
        ctx.Check(hull != null && child != null && hull.IsAncestorOf(child),
            $"'{PathKey}' resolves to the node inside '{PathParent}'");

        var sites = new ObjectiveSites(director, messages, targets, world.Runtime);
        var offered = new List<AimCandidate>();
        sites.Collect(offered);
        var marked = new List<ObjectiveSite>();
        foreach (var candidate in offered)
        {
            if (candidate.Source is ObjectiveSite site
                && site.Target.Node.Equals(PathChild, StringComparison.OrdinalIgnoreCase))
            {
                marked.Add(site);
                report.AppendLine($"site '{site.Node}' at {site.Position} category '{site.Category}'");
            }
        }

        ctx.Same(1, marked.Count, $"exactly one '{PathChild}' site is offered");
        ctx.Check(marked.Count == 1 && marked[0].Node == PathKey,
            $"and it is the path's own key, not a bare name");
        ctx.Check(marked.Count == 1 && child != null
                && marked[0].Position.IsEqualApprox(ObjectiveSites.SiteAnchor(child)),
            $"standing on the hull's child's own geometry");
        ctx.Check(marked.Count == 1 && child != null && Nearest(all, child, marked[0].Position) > PathApart,
            $"and nowhere near a ground '{PathChild}', which is what the path had to tell apart");
        ctx.Check(marked.Count == 1 && marked[0].Category == messages.Get(PathLabel).Trim(),
            $"with the help label written against the same path ('{marked[0].Category}')");
    }

    // How far the marked site is from the nearest node of the same name that is NOT the hull's own.
    private static float Nearest(IReadOnlyList<Node3D> all, Node3D mine, Vector3 at)
    {
        float best = float.MaxValue;
        foreach (var node in all)
        {
            if (node != mine)
            {
                best = Mathf.Min(best, at.DistanceTo(node.GlobalPosition));
            }
        }

        return best;
    }

    private static bool AuthorsPath(ObjectiveScript script)
    {
        foreach (var def in script.Objectives)
        {
            foreach (var target in def.AddObjectiveTarget)
            {
                if (target.Is(PathKey))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void Drive(TestContext ctx, TestWorld world, CampaignDirector director,
        ObjectiveScript script, MissionTargets targets, Messages messages, StringBuilder report)
    {
        // A one-slot array, not a captured local: the driver below flies the aircraft by writing
        // the listener position the director reads, which a lambda cannot do to a `ref` local.
        var listener = new Vector3[1] { ctx.Camera.GlobalPosition };
        director.Attach(new CampaignDirector.WorldInputs
        {
            Runtime = world.Runtime,
            Sounds = world.Runtime.Sounds,
            ListenerPosition = () => listener[0],
            Rng = new Random(1),
        });
        var graph = director.Graph!;
        var sites = new ObjectiveSites(director, messages, targets, world.Runtime);
        var pilot = new Pilot(sites);
        pilot.Fly(listener[0]);
        foreach (var target in pilot.Selection.Pool.Enemy)
        {
            report.AppendLine($"enemy cycle: '{target.Name}' objective={target.Objective} " +
                $"\"{target.CategoryLine}\" / \"{target.DisplayName}\" at {target.Position}");
        }

        ctx.Check(ObjectiveCount(pilot.Selection.Pool.Enemy) > 0,
            $"the mission's objective sites reach the pilot's Enemy cycle");
        ctx.Check(NamesOf(pilot.Selection.Pool.NonAircraft, script, graph, targets) == 0,
            $"and none of them landed on the Non-Aircraft cycle instead");
        CheckSelection(ctx, pilot, report);
        CheckCycle(ctx, pilot, report);
        CheckPointSite(ctx, world, script, pilot, report);
        CheckMovingSite(ctx, world, script, pilot, report);
        RetireOneSite(ctx, script, graph, pilot, listener, report);
    }

    // The one selected site: the head of the cycle, drawn with the original's category line over
    // the site's own name, in the blue a non-destructive objective earns.
    private static void CheckSelection(TestContext ctx, Pilot pilot, StringBuilder report)
    {
        if (pilot.Selection.Current is not { } current)
        {
            ctx.Check(false, $"the pilot has a selected target with objective sites in the pool");
            return;
        }

        report.AppendLine($"selected '{current.Name}': objective={current.Objective} " +
            $"class={current.Class} colour {TargetHud.MarkerColor(current, AimAssist.PlayerTeam)}");
        ctx.Check(current.Objective && current.Class == TargetClass.Enemy,
            $"an objective site is what the auto-acquire selects ('{current.Name}')");
        ctx.Check(pilot.Selection.Ordered.Count > 0 && pilot.Selection.Ordered[0].Objective,
            $"and it sorts ahead of every sector, so it heads the cycle");
        ctx.Check(current.DisplayName.Length > 0
            && !current.DisplayName.StartsWith("MSG_", StringComparison.Ordinal),
            $"the marker's name line is resolved, not a raw message key ('{current.DisplayName}')");
        ctx.Check(current.CategoryLine.EndsWith("-", StringComparison.Ordinal),
            $"with the original's category line above it ('{current.CategoryLine}')");
        ctx.Check(TargetHud.MarkerColor(current, AimAssist.PlayerTeam) == MarkerDraw.HudBlue,
            $"in the marker blue the original uses for a non-destructive objective");
    }

    // One at a time, stepped like an enemy: d-pad up's NextEnemy moves the selection to another
    // site, and an untouched rebuild keeps the one it landed on.
    private static void CheckCycle(TestContext ctx, Pilot pilot, StringBuilder report)
    {
        if (pilot.Selection.Current is not { } first || ObjectiveCount(pilot.Selection.Ordered) < 2)
        {
            report.AppendLine("fewer than two objective sites are selectable, so no step to check");
            return;
        }

        pilot.Selection.NextEnemy();
        pilot.Fly(pilot.Position);
        var stepped = pilot.Selection.Current;
        report.AppendLine($"next enemy: '{first.Name}' -> '{stepped?.Name ?? "-"}'");
        ctx.Check(stepped is { Objective: true } && !stepped.Value.IsSameTarget(first),
            $"stepping the enemy cycle moves to another objective site");
        pilot.Fly(pilot.Position);
        ctx.Check(stepped is { } held && pilot.Selection.Current is { } after
            && after.IsSameTarget(held),
            $"and a rebuild holds it, so the selection survives a frame");
    }

    // A site the mission names by a bare TRAVELERS point: it belongs on that point, not on the
    // world node of the same name, which this mission parks at the origin.
    private static void CheckPointSite(TestContext ctx, TestWorld world, ObjectiveScript script,
        Pilot pilot, StringBuilder report)
    {
        foreach (var target in pilot.Selection.Pool.Enemy)
        {
            if (!target.Objective || ObjectiveSites.PointFor(script, target.Name) is not { } point)
            {
                continue;
            }

            var found = ObjectiveSites.ResolveTarget(world.Runtime, ObjectiveTarget.Parse(target.Name));
            report.AppendLine($"'{target.Name}' point {point} vs node " +
                $"{(found != null ? found.GlobalPosition.ToString() : "unresolved")}");
            ctx.Check(target.Position.IsEqualApprox(point),
                $"'{target.Name}' sits at the point its objective tests, not at its node");
            ctx.Check(found == null || !found.GlobalPosition.IsEqualApprox(point),
                $"and that point is somewhere the node itself is not, so the choice matters");
            return;
        }

        report.AppendLine("no offered site is named by a bare TRAVELERS point");
    }

    // The frozen-marker check: a site standing on a world node is rebuilt from that node every
    // frame, so moving the node (or its parent) moves the candidate with it.
    private static void CheckMovingSite(TestContext ctx, TestWorld world, ObjectiveScript script,
        Pilot pilot, StringBuilder report)
    {
        if (NodeSite(world, script, pilot) is not { } pick)
        {
            report.AppendLine("no offered site stands on a resolvable world node");
            return;
        }

        var (target, mover) = pick;
        var before = target.Position;
        var origin = mover.GlobalPosition;
        mover.GlobalPosition = origin + Shove;
        pilot.Fly(pilot.Position);
        var moved = Find(pilot.Selection.Pool.Enemy, target.Name);
        // Restore and rebuild together: a pool left holding the shoved position is a world state
        // every later check would read, and the mission's own approach tests would miss by 720 m.
        mover.GlobalPosition = origin;
        pilot.Fly(pilot.Position);
        report.AppendLine($"moved '{mover.Name}' by {Shove}: '{target.Name}' {before} -> " +
            $"{(moved is { } m ? m.Position.ToString() : "gone")}");
        ctx.Check(moved is { } after && after.Position.IsEqualApprox(before + Shove),
            $"'{target.Name}' tracks the node it stands on when that node moves");
        ctx.Check(moved is { } still && still.Source is ObjectiveSite,
            $"and it is still the same site object, so a selection on it would hold");
    }

    // Flies the site's own TRAVELERS approach by putting the listener on it: the objective
    // completes, its REMOVE_OBJECTIVE_TARGET fires, and that site alone leaves the cycle.
    private static void RetireOneSite(TestContext ctx, ObjectiveScript script, ObjectiveGraph graph,
        Pilot pilot, Vector3[] listener, StringBuilder report)
    {
        if (Approachable(script, pilot) is not { } site)
        {
            report.AppendLine("no site this mission offers is retired by a TRAVELERS approach");
            return;
        }

        int before = ObjectiveCount(pilot.Selection.Pool.Enemy);
        listener[0] = site.Position;
        for (float t = 0f; t < 6f; t += 0.1f)
        {
            graph.Step(0.1f);
        }

        pilot.Fly(pilot.Position);
        int after = ObjectiveCount(pilot.Selection.Pool.Enemy);
        report.AppendLine($"flew the approach to '{site.Name}': {before} sites -> {after}");
        ctx.Check(Find(pilot.Selection.Pool.Enemy, site.Name) == null,
            $"flying '{site.Name}'s approach retires its marker");
        ctx.Check(after > 0 && after < before,
            $"and leaves the mission's remaining sites offered ({after} still selectable)");
    }

    // The first offered site whose approach an awake objective both flies (TRAVELERS on that node)
    // and removes when it completes, which is the mission's own "you have been here" pair.
    private static TargetRef? Approachable(ObjectiveScript script, Pilot pilot)
    {
        foreach (var target in pilot.Selection.Pool.Enemy)
        {
            foreach (var def in script.Objectives)
            {
                if (target.Objective && def.Travelers is { } spec
                    && string.Equals(spec.WhereNode, target.Name, StringComparison.OrdinalIgnoreCase)
                    && Removes(def, target.Name))
                {
                    return target;
                }
            }
        }

        return null;
    }

    // The first offered site that stands on a world node rather than a bare point, with the node
    // to move: its PARENT where that parent is itself parented, which is the under-a-moving-hull
    // case, and the site's own node where the parent is the chapter world's own root.
    private static (TargetRef Target, Node3D Mover)? NodeSite(TestWorld world,
        ObjectiveScript script, Pilot pilot)
    {
        foreach (var target in pilot.Selection.Pool.Enemy)
        {
            if (!target.Objective || ObjectiveSites.PointFor(script, target.Name) != null)
            {
                continue;
            }

            var found = ObjectiveSites.ResolveTarget(world.Runtime, ObjectiveTarget.Parse(target.Name));
            if (found == null || !found.IsInsideTree())
            {
                continue;
            }

            var parent = found.GetParent() as Node3D;
            return (target, parent?.GetParent() is Node3D ? parent : found);
        }

        return null;
    }

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

    private static int ObjectiveCount(IReadOnlyList<TargetRef> cycle)
    {
        int found = 0;
        foreach (var target in cycle)
        {
            if (target.Objective)
            {
                found++;
            }
        }

        return found;
    }

    // How many of the mission's live site names ended up on a cycle they do not belong to.
    private static int NamesOf(IReadOnlyList<TargetRef> cycle, ObjectiveScript script,
        ObjectiveGraph graph, MissionTargets targets)
    {
        var live = new List<string>();
        ObjectiveSites.CollectFlagged(TargetFlag.Objective, script, graph, targets, live);
        int found = 0;
        foreach (var target in cycle)
        {
            if (Names(live, target.Name))
            {
                found++;
            }
        }

        return found;
    }

    private static bool Removes(ObjectiveDef def, string key)
    {
        foreach (var target in def.RemoveObjectiveTarget)
        {
            if (target.Is(key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Names(IReadOnlyList<string> names, string node)
    {
        foreach (var name in names)
        {
            if (string.Equals(name, node, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int FlaggedCount(MissionTargets targets)
    {
        int flagged = 0;
        foreach (var entry in targets.ByNode)
        {
            if (entry.Value.Objective)
            {
                flagged++;
            }
        }

        return flagged;
    }

    private static CampaignMission? MissionOf(IReadOnlyList<CampaignMission> missions, int seq)
    {
        foreach (var mission in missions)
        {
            if (mission.Seq == seq)
            {
                return mission;
            }
        }

        return null;
    }

    // A pilot's targeting frame with no aircraft: the same pool feed and the same per-frame
    // rebuild FlightController.StepTargeting runs, driven a frame at a time by the suite.
    private sealed class Pilot
    {
        private readonly ObjectiveSites _sites;
        private readonly List<AimCandidate> _offered = new();
        private readonly AimCandidateSet _scan = new();

        internal Pilot(ObjectiveSites sites) => _sites = sites;

        internal TargetSelection Selection { get; } = new();

        internal Vector3 Position { get; private set; }

        internal void Fly(Vector3 position)
        {
            Position = position;
            _offered.Clear();
            _sites.Collect(_offered);
            Selection.Rebuild(_scan, null, AimAssist.PlayerTeam, null, position, Basis.Identity,
                _offered);
        }
    }
}
