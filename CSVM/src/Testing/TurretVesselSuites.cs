using System;
using System.Collections.Generic;
using System.Linq;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.World;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>A turret gunner's candidate scan over the whole of the engine's <c>VehicleList</c>,
/// the pool that holds AI ground and sea vehicles beside the aircraft (docs/org/targeting.md
/// "The turret gunner runs the same predicate"). Run against C1B/M03's four authored patrol
/// boats, with no aircraft in the scene at all, so an acquisition can only be a hull.</summary>
internal static class TurretVesselSuites
{
    private const string BoatChapter = "C1B";
    private const string BoatMission = "M03";
    private const string BoatDef = "patrolboat";
    private const string SitePattern = "test_vessel_gun";
    private const float StepDt = 1f / 30f;

    // Where the gun stands relative to the hull it should find: close enough that no shipped
    // DETECTION_RANGE can exclude it, far enough that the two are not coincident.
    private static readonly Vector3 GunOffset = new(0f, 25f, 80f);

    [Suite("turret-vessel-targets",
        "a turret gunner acquires a hostile surface hull (BL-626): with C1B/M03's four woken "
        + "patrol boats in the world and no aircraft anywhere in the scene, an emplacement on "
        + "the player's team locks the nearest hull and reports it as its tracked target, and "
        + "the same gun moved onto the hulls' own team acquires nothing, so the widened scan "
        + "still runs the one hostility predicate rather than shooting at everything afloat")]
    internal static void TurretVesselTargets(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, BoatChapter, BoatMission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, BoatChapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, BoatChapter);
        ctx.RequireData(missionZrdr, $"{BoatChapter}/{BoatMission} zrdr");
        ctx.RequireData(chapterZrdr, $"{BoatChapter} zrdr");
        ctx.RequireData(texturesPath, $"{BoatChapter} textures");

        var mission = MissionOf(ctx, BoatChapter, BoatMission);
        var script = ObjectiveScript.Load(missionZrdr);
        var defs = VehicleDefs.Load(ctx.ZrdrPath);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var turretDefs = TurretDefs.Load(ctx.ZrdrPath);
        var director = CampaignDirector.Create(script, mission, CampaignProfileDef.NewProfile("Guns"), null);

        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        try
        {
            ctx.WithWorld(BoatChapter, collision: false, BoatMission, world =>
            {
                var worldRoot = world.Runtime.WorldRoot ?? world.Stage;
                var vessels = new SurfaceVehicleRuntime(world.Gamez, world.Session.Builder.Scene,
                    world.Runtime, defs, worldRoot);
                worldRoot.AddChild(vessels);
                int aircraftAsked = 0;
                director.BuildRoster(new CampaignDirector.RosterInputs
                {
                    ChapterZrdrPath = chapterZrdr,
                    MissionZrdrPath = missionZrdr,
                    ZrdrPath = ctx.ZrdrPath,
                    MinAiActiveDist = skills.MinAiActiveDist,
                    FindNodes = name => world.Runtime.FindNodes(name),
                    Spawn = (_, _, _, _) =>
                    {
                        aircraftAsked++;
                        return null;   // the aircraft half of the roster stays unbuilt on purpose
                    },
                    SpawnSurface = (plan, pos, forward) => vessels.Spawn(plan, pos, forward),
                    Rng = new Random(1),
                });
                foreach (var hull in director.Vessels.Values)
                {
                    hull.Wake();
                }

                var boat = director.Vessels.Values.FirstOrDefault();
                ctx.Check(boat != null && !boat.Inert,
                    $"{BoatChapter}/{BoatMission} builds a woken '{BoatDef}' hull to aim at");
                if (boat == null)
                {
                    return;
                }
                ctx.Check(boat.Team is int hullTeam && hullTeam != AimAssist.NeutralTeam,
                    $"the hull carries its block's team, so the predicate can admit it: {boat.Team}");

                var live = new ProjectilePool(textures, null, null) { SurfaceVehicles = vessels };
                pool = live;
                ctx.Host.AddChild(live);

                // The scan's own reading, before any gunner runs: the pool offers the hulls and
                // nothing else, so an acquisition below cannot be an aircraft in disguise.
                var scan = new AimCandidateSet();
                live.CollectVehicleList(scan);
                ctx.Same(director.Vessels.Count, scan.Vehicles.Count,
                    $"the pool's VehicleList is the hulls alone ({aircraftAsked} aircraft block(s) left unbuilt)");

                // A hand-placed emplacement beside the hull: an authored standalone entry with its
                // NODES/PARTS chain repointed at nodes built here, since this suite proves the
                // candidate class rather than a chapter's placement census.
                var siteNode = new Node3D { Name = SitePattern, Position = boat.Position + GunOffset };
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
                gunDef.Team = AimAssist.PlayerTeam;
                gunDef.NodePatterns = new List<IReadOnlyList<string>>
                {
                    new List<string> { SitePattern },
                };
                var guns = TurretController.BuildEmplacements(turretDefs, weapons,
                    (name, _) => name == SitePattern
                        ? new[] { siteNode }
                        : Array.Empty<Node3D>(),
                    live);
                ctx.Check(guns.Length == 1, $"the hand-placed gun resolves turrets={guns.Length}");
                if (guns.Length != 1)
                {
                    return;
                }

                var gun = guns[0];
                gun.SetActivated(true);
                float reach = gun.WorldPosition.DistanceTo(boat.Position);
                ctx.Check(reach < gun.Def.DetectionRange,
                    $"the hull is inside the gun's DETECTION_RANGE: {reach:0} m of {gun.Def.DetectionRange:0} m");

                // Which hull it should find: the picker minimises range over the pool, and
                // C1B/M03's four boats patrol within sight of each other, so the answer is the
                // nearest of them rather than the one the gun was placed beside.
                var nearest = director.Vessels.Values
                    .OrderBy(hull => gun.WorldPosition.DistanceTo(hull.Position))
                    .First();
                gun.SimStep(StepDt);
                ctx.Check(gun.Gate != TurretGate.NoTarget,
                    $"the gun acquires with only hulls in the world: gate={gun.Gate}");
                float missBy = gun.TargetPosition.DistanceTo(nearest.Position);
                ctx.Check(missBy < 1f,
                    $"the acquired target is the nearest hull '{nearest.Name}', {missBy:0.###} m from its own position");

                // Moved onto the hulls' own team, the same gun in the same place finds nothing:
                // the widening changed which pool is walked, not the predicate that admits an entry.
                gun.SetTeam(boat.Team ?? AimAssist.NeutralTeam);
                gun.SimStep(StepDt);
                ctx.Check(gun.Gate == TurretGate.NoTarget,
                    $"a gun on the hulls' own team acquires nothing: gate={gun.Gate}");

                ctx.Note($"a turret locks a hostile hull at {reach:0} m and drops it on a team change");
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

    private static CampaignMission MissionOf(TestContext ctx, string chapter, string folder)
    {
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(chapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(folder, StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }

        throw new SuiteSkippedException($"{chapter}/{folder} is not in cm_sequence");
    }
}
