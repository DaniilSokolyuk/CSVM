using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using Godot;

namespace CSVM.Testing;

/// <summary>A turret gunner's candidate scan over the engine's third pool, the mission structures
/// (docs/org/targeting.md "What a mission structure's team is"). Run against C1/M05, whose Red
/// Cross hospital ship stands on a node authoring the player's side, with no aircraft and no hulls
/// built, so an acquisition can only be a structure.</summary>
internal static class TurretStructureSuites
{
    private const string ShipChapter = "C1";
    private const string ShipMission = "M05";
    private const string ShipDef = "redcross";
    private const string SitePattern = "test_structure_gun";
    private const float StepDt = 1f / 30f;

    // The team C1/M05's hospital ship authors in its own mission slot: the player's side, which
    // is why an enemy gun engages it and the player's own does not.
    private const int ShipTeam = AimAssist.PlayerTeam;

    // Where the gun stands relative to the structure it should find: close enough that no shipped
    // DETECTION_RANGE can exclude it, far enough that the two are not coincident.
    private static readonly Vector3 GunOffset = new(0f, 30f, 60f);

    [Suite("turret-structure-targets",
        "a turret gunner acquires a hostile mission structure (BL-664): a pool standing on a "
        + "flagged scene node takes the owner that node authors for the mission being flown, so an "
        + "enemy emplacement locks C1/M05's hospital ship with no aircraft and no hulls in the "
        + "world, a gun on the ship's own side never offers it at all, and a pool on an unflagged "
        + "node is offered to nobody")]
    internal static void TurretStructureTargets(TestContext ctx)
    {
        CheckSlotRule(ctx);
        CheckRegistryRule(ctx);
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, ShipChapter);
        ctx.RequireData(texturesPath, $"{ShipChapter} textures");
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var turretDefs = TurretDefs.Load(ctx.ZrdrPath);
        int gunTeam = TurretDef.DefaultTeamId;

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        try
        {
            ctx.WithWorld(ShipChapter, collision: false, ShipMission, world =>
            {
                var worldRoot = world.Runtime.WorldRoot ?? world.Stage;
                var registry = world.Runtime.Destructibles;
                var teamed = registry.All.Where(i => i.Team != null).ToList();
                var unowned = registry.All.Where(i => i.Team == null).ToList();
                ctx.Check(teamed.Count > 0 && unowned.Count > 0,
                    $"{ShipChapter}/{ShipMission} builds {teamed.Count} owned pool(s) and {unowned.Count} unowned");
                string ids = string.Join("/", teamed.Select(i => i.Team).Distinct().OrderBy(t => t));
                ctx.Check(teamed.Any(i => i.Team == AimAssist.PlayerTeam)
                    && teamed.Any(i => i.Team == TurretDef.DefaultTeamId),
                    $"and the mission owns structures on both sides rather than one flat id: {ids}");

                var ship = registry.All.FirstOrDefault(i =>
                    i.Def.Name.Equals(ShipDef, StringComparison.OrdinalIgnoreCase));
                ctx.Check(ship != null, $"the hospital ship's own pool is registered: def '{ShipDef}'");
                if (ship == null)
                {
                    return;
                }

                ctx.Same(ShipTeam, ship.Team ?? AimAssist.NeutralTeam,
                    $"and it carries the side its node authors for this mission, not neutral");

                var live = new ProjectilePool(textures, null, null) { Structures = registry };
                pool = live;
                ctx.Host.AddChild(live);

                // The scan's own reading before any gunner runs: no aircraft were built and this
                // session has no surface hulls, so the vehicle pool is empty and an acquisition
                // below can only have come from the structure pool.
                var scan = new AimCandidateSet();
                live.CollectVehicleList(scan);
                live.CollectMissionStructures(scan);
                ctx.Same(0, scan.Vehicles.Count, $"the pool's VehicleList is empty here");
                ctx.Check(scan.Structures.Count > 0,
                    $"and its structure pool is not: {scan.Structures.Count} candidate(s)");

                // A hand-placed emplacement beside the ship: an authored standalone entry with its
                // NODES/PARTS chain repointed at nodes built here, since this suite proves the
                // candidate class rather than a chapter's placement census.
                var siteNode = new Node3D
                {
                    Name = SitePattern,
                    Position = ship.Anchor.GlobalPosition + GunOffset,
                };
                worldRoot.AddChild(siteNode);
                var pitchNode = new Node3D();
                pitchNode.SetMeta(AnimRuntime.NameMeta, "pitch");
                siteNode.AddChild(pitchNode);
                var muzzleNode = new Node3D();
                muzzleNode.SetMeta(AnimRuntime.NameMeta, "muzzle");
                siteNode.AddChild(muzzleNode);

                var gunDef = turretDefs.All.First(d => !d.Carried);
                gunDef.YawNode = null;
                gunDef.PitchNode = "pitch";
                gunDef.Firepoints = new[] { "muzzle" };
                gunDef.HealthyNode = null;
                gunDef.Team = gunTeam;
                gunDef.NodePatterns = new List<IReadOnlyList<string>>
                {
                    new List<string> { SitePattern },
                };
                var guns = TurretController.BuildEmplacements(turretDefs, weapons,
                    (name, _) => name == SitePattern ? new[] { siteNode } : Array.Empty<Node3D>(),
                    live);
                ctx.Check(guns.Length == 1, $"the hand-placed gun resolves turrets={guns.Length}");
                if (guns.Length != 1)
                {
                    return;
                }

                var gun = guns[0];
                gun.SetActivated(true);
                float reach = gun.WorldPosition.DistanceTo(ship.Anchor.GlobalPosition);
                ctx.Check(reach < gun.Def.DetectionRange,
                    $"the ship is inside the gun's DETECTION_RANGE: {reach:0} m of {gun.Def.DetectionRange:0} m");

                // Which structure an enemy gun should find: the picker minimises range over the
                // pool, so it is the nearest candidate the predicate admits.
                var wanted = Nearest(scan, gun, gunTeam);
                ctx.Check(wanted != null && ReferenceEquals(wanted.Value.Source, ship),
                    $"the nearest structure an enemy gun may take is the hospital ship itself");
                gun.SimStep(StepDt);
                ctx.Check(gun.Gate != TurretGate.NoTarget,
                    $"the gun acquires with only structures in the world: gate={gun.Gate}");
                float missBy = wanted == null
                    ? float.MaxValue
                    : gun.TargetPosition.DistanceTo(wanted.Value.Position);
                ctx.Check(missBy < 1f,
                    $"and what it locked is that structure, {missBy:0.###} m from its own position");

                // Moved onto the ship's own side, the same gun in the same place is offered the
                // ship by nobody: the new pool runs through the one hostility predicate.
                gun.SetTeam(ShipTeam);
                gun.SimStep(StepDt);
                var friendly = Nearest(scan, gun, ShipTeam);
                ctx.Check(!scan.Structures.Any(c => ReferenceEquals(c.Source, ship)
                        && AimAssist.Hostile(ShipTeam, c.Team)),
                    $"a gun on the ship's own team is hostile to no candidate the ship stands behind");
                bool matches = friendly == null
                    ? gun.Gate == TurretGate.NoTarget
                    : gun.TargetPosition.DistanceTo(friendly.Value.Position) < 1f;
                ctx.Check(matches,
                    $"and it takes whatever the predicate leaves it, which here is {(friendly == null ? "nothing" : "another structure")}: gate={gun.Gate}");

                ctx.Note($"an enemy turret locks the hospital ship at {reach:0} m; {teamed.Count} owned pool(s) here, ids {ids}");
            });
        }
        finally
        {
            // The gun's site node hangs off the world root and goes with the world; only the pool
            // outlives it, on the harness host.
            if (pool != null && GodotObject.IsInstanceValid(pool))
            {
                pool.Free();
            }

            textures.Dispose();
        }
    }

    // The nearest structure candidate a gun on this team may take, by the rule the gunner runs.
    private static AimCandidate? Nearest(AimCandidateSet scan, TurretController gun, int team)
    {
        AimCandidate? best = null;
        float bestDist = gun.Def.DetectionRange;
        foreach (var c in scan.Structures)
        {
            if (!c.Live || !AimAssist.Hostile(team, c.Team)
                || c.Source is DestructibleRegistry.Instance { Gasbag: true })
            {
                continue;
            }

            float d = gun.WorldPosition.DistanceTo(c.Position);
            if (d <= bestDist)
            {
                bestDist = d;
                best = c;
            }
        }

        return best;
    }

    // The slot arithmetic itself, with no world: a node authors one owner per mission of its
    // chapter, and the mission being flown picks which one is read.
    private static void CheckSlotRule(TestContext ctx)
    {
        var ship = new GameZNode { MissionTargetWord = 0x90155555u };
        var zone = new GameZNode { MissionTargetWord = 0x882AAAAAu };
        var third = new GameZNode { MissionTargetWord = 0x80000020u };
        var plain = new GameZNode { MissionTargetWord = 0u };
        ctx.Check(ship.IsMissionStructure && zone.IsMissionStructure && !plain.IsMissionStructure,
            $"bit 31 is the flag that makes a node a mission structure");
        ctx.Same(AimAssist.PlayerTeam, ship.MissionStructureTeam(5),
            $"C1/M05's hospital ship word authors the player's side in the mission's own slot");
        ctx.Same(TurretDef.DefaultTeamId, zone.MissionStructureTeam(1),
            $"a zeppelin zone's word authors the enemy side in its own");
        ctx.Same(TurretDef.DefaultTeamId, third.MissionStructureTeam(3),
            $"a word authoring one mission alone reads back in that mission");
        ctx.Same(AimAssist.NeutralTeam, third.MissionStructureTeam(1),
            $"and reads back unowned in any other, which is the per-mission indexing itself");
        ctx.Same(5, GameZ.MissionSlotOf("M05"), $"the mission folder's own number is the slot");
        ctx.Same(1, GameZ.MissionSlotOf("ia1"), $"an Instant Action build reads the first slot");
        ctx.Same(1, GameZ.MissionSlotOf(null), $"and so does a build naming no mission");
    }

    // The rule on a registry this suite owns outright: the scene build stamps a flagged node's
    // owner, registration reads it off the pool's own damage node, and a pool on any other node
    // stays unowned and so is nobody's target.
    private static void CheckRegistryRule(TestContext ctx)
    {
        var plainNode = new Node3D { Name = "plain_probe" };
        var allyNode = new Node3D { Name = "ally_probe" };
        var gasbagNode = new Node3D { Name = "gasbag_probe" };
        allyNode.SetMeta(SceneBuilder.MissionStructureTeamMeta, AimAssist.PlayerTeam);
        gasbagNode.SetMeta(SceneBuilder.MissionStructureTeamMeta, TurretDef.DefaultTeamId);
        gasbagNode.SetMeta(SceneBuilder.MissionStructureGasbagMeta, true);
        ctx.Host.AddChild(plainNode);
        ctx.Host.AddChild(allyNode);
        ctx.Host.AddChild(gasbagNode);
        try
        {
            var registry = new DestructibleRegistry();
            var plain = registry.Register(new AnimDefinition { Name = "plain" }, plainNode, 100f);
            var ally = registry.Register(new AnimDefinition { Name = "ally" }, allyNode, 100f);
            var gasbag = registry.Register(new AnimDefinition { Name = "gasbag" }, gasbagNode, 100f);

            ctx.Check(plain.Team == null,
                $"a pool on an unflagged node is unowned, so no gun can be hostile to it");
            ctx.Same(AimAssist.PlayerTeam, ally.Team ?? AimAssist.NeutralTeam,
                $"a pool on a flagged node takes the side that node authors");
            ctx.Check(!plain.Gasbag && !ally.Gasbag && gasbag.Gasbag,
                $"and the gasbag flag travels with it for the pass that drops those");

            var set = new AimCandidateSet();
            set.AddStructures(registry);
            int hostileToEnemy = set.Structures.Count(c =>
                AimAssist.Hostile(TurretDef.DefaultTeamId, c.Team));
            int hostileToPlayer = set.Structures.Count(c =>
                AimAssist.Hostile(AimAssist.PlayerTeam, c.Team));
            ctx.Same(1, hostileToEnemy, $"an enemy gun is hostile to the player's structure alone");
            ctx.Same(1, hostileToPlayer, $"a player gun is hostile to the enemy's alone");
        }
        finally
        {
            plainNode.Free();
            allyNode.Free();
            gasbagNode.Free();
        }
    }
}
