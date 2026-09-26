using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>BL-476: a zeppelin record's authored <c>team</c> reached nothing, and its
/// <c>deactivated</c> flag only held the motion, so a mission's hidden airship was drawn,
/// collidable and targetable from the first frame. Drives C3/M01's own records against its BUILT
/// world and wakes the zeppelin through the real objective graph.</summary>
internal static class CampaignZeppelinWakeSuites
{
    // C3/M01 is story position 0 and is the mission that carries the case: two zeppelin records,
    // one of them `deactivated`, and an OBJECTIVE39 that names it in WAKEUP_ENEMIES.
    private const int FirstSeq = 0;
    private const string DormantZep = "cargozep1";
    private const string LiveZep = "piratezep";
    private const int WakeObjective = 39;

    // The run_time the same objective's WAKE_ANIM authors, plus a step: the fade must finish
    // inside it, and the loop below must not run forever if it never does.
    private const float RevealSeconds = 6f;
    private const float StepDt = 1f / 60f;

    // BL-476: a zeppelin's authored team reached nothing and its `deactivated` flag held only
    // the motion, so the airship a mission reveals partway through was in play from t=0.
    [Suite("campaign-zeppelin-wakeup",
        "the hidden zeppelin and the authored team over the first story mission's BUILT world: "
        + "the three parser team names resolve to the ids the one shared team space mints, an "
        + "authored pool team beats the world fall-through while an unauthored one keeps it, a "
        + "dormant pool is no candidate at all, C3/M01's deactivated cargozep1 starts "
        + "uncollidable and out of both target pools while its sibling does not, and "
        + "OBJECTIVE39's own WAKEUP_ENEMIES puts it into the world through the real graph")]
    internal static void CampaignZeppelinWakeup(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), FirstSeq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {FirstSeq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, chapter);
        ctx.RequireData(missionZrdr, $"{chapter}/{folder} zrdr");
        ctx.RequireData(chapterZrdr, $"{chapter} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");

        var report = new StringBuilder();
        CheckAuthoredTeams(ctx, report);
        CheckCandidateGates(ctx, report);

        var script = ObjectiveScript.Load(missionZrdr);
        var defs = Zeppelins.Load(missionZrdr);
        var nets = AiNets.Load(chapterZrdr);
        ctx.Check(script.Objectives.Count >= WakeObjective
            && Contains(ObjectiveNames(script, WakeObjective), DormantZep),
            $"OBJECTIVE{WakeObjective} names '{DormantZep}' in WAKEUP_ENEMIES, which is what this seam consumes");

        var profile = CampaignProfileDef.NewProfile("Zeppelin");
        var director = CampaignDirector.Create(script, mission, profile, null);
        ctx.ExtraPrewarmSoundNames = script.SoundGroupNames();
        ctx.WithWorld(chapter, collision: true, folder, world =>
            Drive(ctx, world, director, defs, nets, report));

        ctx.WriteArtifact($"test-campaign-zeppelin-wakeup-{chapter}.txt", report.ToString());
        ctx.Note($"{chapter}/{folder}: '{DormantZep}' stays out of the world until OBJECTIVE{WakeObjective} wakes it");
    }

    // The three parser names against the ids the one shared team space mints, read off the shipped
    // records rather than restated: C3/M01 authors none, C1/MP3 an ally and C5/M03 three enemies.
    private static void CheckAuthoredTeams(TestContext ctx, StringBuilder report)
    {
        int allies = 0, enemies = 0, unauthored = 0;
        foreach (var (chapter, folder) in new[] { ("C3", "M01"), ("C1", "MP3"), ("C5", "M03") })
        {
            string path = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder);
            ctx.RequireData(path, $"{chapter}/{folder} zrdr");
            foreach (var def in Zeppelins.Load(path))
            {
                int? team = ZeppelinRuntime.AuthoredTeam(def);
                report.AppendLine($"{chapter}/{folder} '{def.Node}': team {def.Team ?? "-"} -> " +
                    $"{team?.ToString() ?? "unauthored"}");
                allies += team == AimAssist.PlayerTeam ? 1 : 0;
                enemies += team == TurretDef.DefaultTeamId ? 1 : 0;
                unauthored += team == null ? 1 : 0;
            }
        }

        ctx.Same(1, allies, $"C1/MP3's 'ally' record reads as the player's side, id {AimAssist.PlayerTeam}");
        ctx.Same(3, enemies, $"C5/M03's three 'enemy' records read as the first enemy id, {TurretDef.DefaultTeamId}");
        ctx.Check(unauthored >= 2,
            $"and a record authoring no team stays unauthored ({unauthored} of them here) rather than taking a literal, BL-407 owns that fall-through");
    }

    // What the pool flags buy on the consumer side, on a registry this suite owns outright: an
    // authored team beats the world fall-through, and a dormant pool is no candidate at all.
    private static void CheckCandidateGates(TestContext ctx, StringBuilder report)
    {
        var anchor = new Node3D { Name = "gate_probe" };
        ctx.Host.AddChild(anchor);
        try
        {
            var registry = new DestructibleRegistry();
            var plain = registry.Register(new AnimDefinition { Name = "plain" }, anchor, 100f);
            var allied = registry.Register(new AnimDefinition { Name = "allied" }, anchor, 100f);
            var asleep = registry.Register(new AnimDefinition { Name = "asleep" }, anchor, 100f);
            allied.Team = AimAssist.PlayerTeam;
            asleep.Dormant = true;

            var set = new AimCandidateSet();
            set.AddStructures(registry);
            int neutral = 0, playerTeam = 0;
            foreach (var candidate in set.Structures)
            {
                neutral += candidate.Team == AimAssist.NeutralTeam ? 1 : 0;
                playerTeam += candidate.Team == AimAssist.PlayerTeam ? 1 : 0;
            }

            report.AppendLine($"candidate gates: {set.Structures.Count} of 3 pools admitted, " +
                $"{neutral} on the fall-through, {playerTeam} on the authored side");
            ctx.Same(2, set.Structures.Count,
                $"the dormant pool is refused and the other two are admitted");
            ctx.Same(1, playerTeam, $"the pool carrying an authored team keeps it");
            ctx.Same(1, neutral,
                $"and the pool carrying none falls through to {AimAssist.NeutralTeam}, so it is nobody's target");
            _ = plain;
        }
        finally
        {
            anchor.Free();
        }
    }

    private static void Drive(TestContext ctx, TestWorld world, CampaignDirector director,
        IReadOnlyList<ZeppelinDef> defs, IReadOnlyList<AiNet> nets, StringBuilder report)
    {
        ZeppelinRuntime? zeps = null;
        try
        {
            var runtime = zeps = new ZeppelinRuntime(defs,
                name => world.Runtime.FindNodes(name, null) is { Count: > 0 } hits ? hits[0] : null,
                nets);
            ctx.Host.AddChild(runtime);
            runtime.WireDamage(world.Runtime);

            ctx.Check(runtime.IsDormant(DormantZep) && !runtime.IsDormant(LiveZep),
                $"the record authoring deactivated 1 starts dormant and its sibling does not");
            ctx.Check(!AnyColliderEnabled(HostOf(world, DormantZep)),
                $"'{DormantZep}' is out of the world: nothing under it is collidable");
            ctx.Check(AnyColliderEnabled(HostOf(world, LiveZep)),
                $"…while '{LiveZep}', which the mission never deactivated, is");

            int dormantParts = TargetParts(runtime);
            int dormantStructures = Structures(world);
            report.AppendLine($"dormant: {dormantParts} target part(s), {dormantStructures} structure candidate(s)");
            ctx.Check(dormantParts > 0,
                $"the live zeppelin still offers its own parts to the target pool ({dormantParts})");
            // BL-740: the gasbag identity is what the acquisition's ordnance gate and the
            // rocketeer's torpedo match read, so a zone pooled as a gasbag has to say so.
            int flaggedGasbags = GasbagParts(runtime);
            int gasbagStructures = GasbagStructures(world);
            report.AppendLine($"gasbag-flagged parts: {flaggedGasbags}, as AI structure candidates: {gasbagStructures}");
            ctx.Check(flaggedGasbags > 0,
                $"the live zeppelin's gasbag zones carry the gasbag identity ({flaggedGasbags} flagged of {dormantParts} parts)");
            ctx.Check(gasbagStructures > 0,
                $"…and reach the AI's structure pool, where the ordnance gate reads them ({gasbagStructures} of {dormantStructures} candidates)");

            // BL-673: a dormant pool is out of the world for a scripted DamageAt too, not only
            // for the target scan. The read is live, so the same pool must die after the wake.
            var asleepPool = PoolOf(world, DormantZep);
            ctx.Check(asleepPool != null, $"'{DormantZep}' registers a damage pool to probe");
            if (asleepPool != null)
            {
                float hp = asleepPool.Health;
                bool landed = world.Runtime.DamageAt(asleepPool.DamageNode, asleepPool.MaxHealth + 1f);
                report.AppendLine($"dormant DamageAt: landed={landed} hp {hp:0.##}->{asleepPool.Health:0.##} status={asleepPool.Status}");
                ctx.Check(!landed, $"a lethal DamageAt on the dormant '{DormantZep}' finds nothing");
                ctx.Check(Mathf.IsEqualApprox(asleepPool.Health, hp)
                    && asleepPool.Status == DestructibleRegistry.State.Healthy,
                    $"…and leaves the pool untouched: hp {asleepPool.Health:0.##}, {asleepPool.Status}");
            }

            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Zeppelins = runtime,
                Sounds = world.Runtime.Sounds,
                ListenerPosition = () => ctx.Camera.GlobalPosition,
                Rng = new Random(1),
            });
            director.Graph!.Wake(WakeObjective);

            ctx.Check(!runtime.IsDormant(DormantZep),
                $"OBJECTIVE{WakeObjective}'s WAKEUP_ENEMIES puts '{DormantZep}' into the world through the real graph");

            // The same objective's own WAKE_ANIM is an OBJECT_OPACITY_FROM_TO on the hull, so the
            // reveal is a fade the mission authored and the hull is still transparent as it starts.
            ctx.Check(!AnyColliderEnabled(HostOf(world, DormantZep)),
                $"…and the mission's own reveal animation has the hull at its start opacity, not solid on the frame it wakes");
            float faded = 0f;
            while (faded < RevealSeconds && !AnyColliderEnabled(HostOf(world, DormantZep)))
            {
                world.Runtime.Advance(StepDt);
                faded += StepDt;
            }
            report.AppendLine($"reveal: hull collidable {faded:0.00} s after the wake");
            ctx.Check(faded > 0f && faded < RevealSeconds,
                $"and the fade brings it back into the world part way through, at {faded:0.00} s of the authored reveal");

            int wokenParts = TargetParts(runtime);
            int wokenStructures = Structures(world);
            report.AppendLine($"woken: {wokenParts} target part(s), {wokenStructures} structure candidate(s)");
            ctx.Check(wokenParts > dormantParts,
                $"the woken zeppelin's parts join the target pool ({dormantParts} -> {wokenParts})");
            ctx.Check(wokenStructures > dormantStructures,
                $"and its damage pools stop being refused as structure candidates ({dormantStructures} -> {wokenStructures})");

            if (asleepPool != null)
            {
                bool landed = world.Runtime.DamageAt(asleepPool.DamageNode, asleepPool.MaxHealth + 1f);
                report.AppendLine($"woken DamageAt: landed={landed} status={asleepPool.Status}");
                ctx.Check(landed && asleepPool.Status == DestructibleRegistry.State.Destroyed,
                    $"the same DamageAt after the wake lands and kills the pool ({asleepPool.Status})");
            }
        }
        finally
        {
            zeps?.Free();
        }
    }

    private static int TargetParts(ZeppelinRuntime runtime)
    {
        var parts = new List<AimCandidate>();
        runtime.CollectTargetParts(parts);
        return parts.Count;
    }

    private static int GasbagParts(ZeppelinRuntime runtime)
    {
        var parts = new List<AimCandidate>();
        runtime.CollectTargetParts(parts);
        return parts.Count(p => p.Source is DestructibleRegistry.Instance { Gasbag: true });
    }

    private static int Structures(TestWorld world)
    {
        var set = new AimCandidateSet();
        set.AddStructures(world.Runtime.Destructibles);
        return set.Structures.Count;
    }

    private static int GasbagStructures(TestWorld world)
    {
        var set = new AimCandidateSet();
        set.AddStructures(world.Runtime.Destructibles);
        return set.Structures.Count(c => c.Source is DestructibleRegistry.Instance { Gasbag: true });
    }

    // The first pool the zeppelin's damage wiring fanned its name onto.
    private static DestructibleRegistry.Instance? PoolOf(TestWorld world, string zep)
    {
        foreach (var inst in world.Runtime.Destructibles.All)
        {
            if (string.Equals(inst.Owner, zep, StringComparison.OrdinalIgnoreCase))
            {
                return inst;
            }
        }

        return null;
    }

    private static Node3D? HostOf(TestWorld world, string node) =>
        world.Runtime.FindNodes(node, null) is { Count: > 0 } hits ? hits[0] : null;

    // Whether anything under this subtree can still be hit. A faded-out subtree has had every
    // collider it owns switched off, which is the rule the dormancy pose leans on.
    private static bool AnyColliderEnabled(Node? root)
    {
        if (root == null)
        {
            return false;
        }
        if (root is CollisionShape3D { Disabled: false })
        {
            return true;
        }
        foreach (var child in root.GetChildren())
        {
            if (AnyColliderEnabled(child))
            {
                return true;
            }
        }
        return false;
    }

    private static bool Contains(IReadOnlyList<string> names, string wanted)
    {
        foreach (var name in names)
        {
            if (name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ObjectiveNames(ObjectiveScript script, int number) =>
        number >= 1 && number <= script.Objectives.Count
            ? script.Objectives[number - 1].WakeupEnemies
            : Array.Empty<string>();

    private static CampaignMission? MissionOf(IReadOnlyList<CampaignMission> missions, int seq)
    {
        foreach (var m in missions)
        {
            if (m.Seq == seq)
            {
                return m;
            }
        }

        return null;
    }
}
