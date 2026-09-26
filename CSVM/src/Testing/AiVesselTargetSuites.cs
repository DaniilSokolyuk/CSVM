using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using CSVM.Session.World;
using Godot;

namespace CSVM.Testing;

/// <summary>An AI aeroplane's ranked acquisition over the whole of the engine's <c>VehicleList</c>,
/// the pool that holds the AI ground and sea vehicles beside the aircraft (docs/org/targeting.md).
/// Run against C1B/M03's four authored patrol boats with one hostile aeroplane further out, so a
/// pilot that ranks hulls takes a boat, and the mission's own <c>patrolboat*</c> hard exclusion is
/// what moves the pick back onto the aeroplane.</summary>
internal static class AiVesselTargetSuites
{
    private const string BoatChapter = "C1B";
    private const string BoatMission = "M03";

    // Two of the five blocks on this mission authoring the hard exclusion against its hulls, and
    // one whose own authored list names no hull, so the control arm runs a real list rather than
    // an empty one.
    private const string ExcludingBlock = "wingman_1";
    private const string SecondExcludingBlock = "devastator_2";
    private const string PlainBlock = "blakepeace_2_1";

    private const float StepDt = 1f / 60f;

    // The shooter's pose relative to the hull it is aimed at: astern and above, well inside the
    // 2,000 m activation a pilot with no mode machine falls back to.
    private static readonly Vector3 ShooterOffset = new(0f, 300f, 600f);

    // And the hostile aeroplane's, relative to the shooter: dead ahead at the shooter's own
    // altitude and further out than the hull, so distance is the only scorer term separating them.
    private static readonly Vector3 QuarryOffset = new(0f, 0f, -1200f);

    [Suite("ai-vessel-targets",
        "an AI aeroplane ranks a surface hull: with C1B/M03's four patrol boats woken and one "
        + "hostile aeroplane 1,200 m dead ahead, a pilot carrying no rating_biases takes the "
        + "nearer boat, and so does one carrying blakepeace_2_1's authored list, which names no "
        + "hull; wingman_1's and devastator_2's authored patrolboat* entries are the hard "
        + "exclusion, so those two rank the boat as a candidate and still pick the aeroplane "
        + "instead, which is what an authored -1.0 meaning NEVER looks like from outside")]
    internal static void AiVesselTargets(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, BoatChapter, BoatMission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, BoatChapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, BoatChapter);
        ctx.RequireData(missionZrdr, $"{BoatChapter}/{BoatMission} zrdr");
        ctx.RequireData(chapterZrdr, $"{BoatChapter} zrdr");
        ctx.RequireData(texturesPath, $"{BoatChapter} textures");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");

        var mission = MissionOf(ctx, BoatChapter, BoatMission);
        var script = ObjectiveScript.Load(missionZrdr);
        var defs = VehicleDefs.Load(ctx.ZrdrPath);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        var director = CampaignDirector.Create(script, mission,
            CampaignProfileDef.NewProfile("Vessels"), null);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);

        // The authored lists, read off the same roster the director spawns from.
        var roster = CampaignRosterPlan.Build(AiSkills.LoadRoster(missionZrdr), defs,
            AiNets.Load(chapterZrdr), netDraw: _ => 0);
        var excluding = PlanNamed(roster, ExcludingBlock);
        var secondExcluding = PlanNamed(roster, SecondExcludingBlock);
        var plain = PlanNamed(roster, PlainBlock);
        if (excluding == null || secondExcluding == null || plain == null)
        {
            throw new SuiteSkippedException(
                $"{BoatChapter}/{BoatMission} does not plan {ExcludingBlock}/{SecondExcludingBlock}/{PlainBlock}");
        }

        LoadoutDef? stock = null;
        foreach (var def in StockLoadouts.Load().All.Values)
        {
            if (def.Model == ctx.PlaneName)
            {
                stock = def;
                break;
            }
        }

        if (stock == null)
        {
            throw new SuiteSkippedException($"no stock loadout for plane={ctx.PlaneName}");
        }

        var report = new StringBuilder();
        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightController? shooter = null;
        FlightController? quarry = null;
        try
        {
            ctx.WithWorld(BoatChapter, collision: false, BoatMission, world =>
            {
                var worldRoot = world.Runtime.WorldRoot ?? world.Stage;
                var live = new ProjectilePool(textures, null, null);
                pool = live;
                ctx.Host.AddChild(live);

                var vessels = new SurfaceVehicleRuntime(world.Gamez, world.Session.Builder.Scene,
                    world.Runtime, defs, worldRoot);
                live.SurfaceVehicles = vessels;
                worldRoot.AddChild(vessels);
                director.BuildRoster(new CampaignDirector.RosterInputs
                {
                    ChapterZrdrPath = chapterZrdr,
                    MissionZrdrPath = missionZrdr,
                    ZrdrPath = ctx.ZrdrPath,
                    MinAiActiveDist = skills.MinAiActiveDist,
                    FindNodes = name => world.Runtime.FindNodes(name),
                    Spawn = (_, _, _, _) => null,   // the aircraft half stays unbuilt on purpose
                    SpawnSurface = (plan, pos, forward) => vessels.Spawn(plan, pos, forward),
                    Rng = new Random(1),
                });
                foreach (var hull in director.Vessels.Values)
                {
                    hull.Wake();
                }

                var boats = director.Vessels.Values.ToList();
                ctx.Check(boats.Count > 0,
                    $"{BoatChapter}/{BoatMission} builds hulls to rank: {boats.Count}");
                if (boats.Count == 0)
                {
                    return;
                }

                // The authored term, read before anything ranks: a pattern against the hulls' own
                // spawn names, saturating rather than merely penalising.
                var boat = boats[0];
                AiRatingBias? exclusion = null;
                foreach (var b in excluding.Biases)
                {
                    if (exclusion == null && b.Matches(boat.Name))
                    {
                        exclusion = b;
                    }
                }

                ctx.Check(exclusion is { Bias: <= -1f },
                    $"'{ExcludingBlock}' authors a hard exclusion matching the hull '{boat.Name}': pattern='{exclusion?.Pattern ?? "-"}'");
                ctx.Check(!plain.Biases.Any(b => b.Matches(boat.Name)),
                    $"'{PlainBlock}' authors {plain.Biases.Count} entr(ies) and none of them names a hull");

                int hullTeam = boat.Team ?? AimAssist.NeutralTeam;
                ctx.Check(hullTeam != AimAssist.NeutralTeam && hullTeam != AimAssist.PlayerTeam,
                    $"the hulls carry a hostile team, so the ranked pick's gate can admit them: {hullTeam}");

                var shooterPos = boat.Position + ShooterOffset;
                var quarryPos = shooterPos + QuarryOffset;
                var gunner = new AiGunner(new RandomNumberGenerator { Seed = 20260910 });
                var pilot = AiPilot.HoldingCourse(shooterPos, shooterPos + Vector3.Forward);
                pilot.Gunner = gunner;
                shooter = Rig(ctx, planesGamez, textures, stats, live, stock, weapons, pilot,
                    shooterPos, shooterPos + Vector3.Forward, AimAssist.PlayerTeam, "vessel_shooter");
                quarry = Rig(ctx, planesGamez, textures, stats, live, stock, weapons, null,
                    quarryPos, quarryPos + Vector3.Forward, hullTeam, "vessel_quarry");
                float hullRange = shooterPos.DistanceTo(boat.Position);
                float quarryRange = shooterPos.DistanceTo(quarryPos);
                ctx.Check(hullRange < quarryRange,
                    $"the hull is the nearer of the two candidates: {hullRange:0} m against the aeroplane's {quarryRange:0} m");

                object? Acquire(IReadOnlyList<AiRatingBias>? biases)
                {
                    shooter!.PlaceHeld(shooterPos, shooterPos + Vector3.Forward);
                    quarry!.PlaceHeld(quarryPos, quarryPos + Vector3.Forward);
                    gunner.AutoTarget = true;
                    gunner.RatingBiases = biases;
                    gunner.Target = null;
                    shooter.SimStep(StepDt);
                    live.SimStep(StepDt);
                    return gunner.Target;
                }

                var control = Acquire(null);
                ctx.Check(control is SurfaceVehicle,
                    $"with no authored biases the nearer hull is the pick: {TargetPool.NameOf(control)}");
                var plainPick = Acquire(plain.Biases);
                ctx.Check(plainPick is SurfaceVehicle,
                    $"and so it is for '{PlainBlock}', whose list names no hull: {TargetPool.NameOf(plainPick)}");

                var excludedPick = Acquire(excluding.Biases);
                ctx.Check(ReferenceEquals(excludedPick, quarry),
                    $"'{ExcludingBlock}'s authored exclusion moves the pick to the aeroplane: {TargetPool.NameOf(excludedPick)}");
                var pooled = shooter.RankedPoolSourcesForTest(gunner);
                ctx.Check(pooled.Any(s => s is SurfaceVehicle),
                    $"the excluded hull is still a RANKED candidate, dropped by its own bias rather than never offered: {pooled.Count} candidate(s)");
                var secondPick = Acquire(secondExcluding.Biases);
                ctx.Check(ReferenceEquals(secondPick, quarry),
                    $"'{SecondExcludingBlock}'s own copy of that list reads the same: {TargetPool.NameOf(secondPick)}");

                // Nothing but the hulls left in the world: an excluded pilot has no target at all
                // rather than falling back onto a boat it was told to leave alone.
                quarry.Inert = true;
                var starved = Acquire(excluding.Biases);
                ctx.Check(starved == null,
                    $"with the aeroplane gone the excluded pilot holds no target: {TargetPool.NameOf(starved)}");
                quarry.Inert = false;

                report.AppendLine($"{BoatChapter}/{BoatMission}: {boats.Count} hull(s), shooter {hullRange:0} m off '{boat.Name}' and {quarryRange:0} m off the aeroplane");
                foreach (var hull in boats)
                {
                    report.AppendLine($"  {hull.Name} at {shooterPos.DistanceTo(hull.Position):0} m, team {hull.Team?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
                }

                foreach (var b in excluding.Biases)
                {
                    report.AppendLine($"  {ExcludingBlock} bias '{b.Pattern}' = {b.Bias.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)}");
                }

                ctx.Note($"an AI aeroplane ranks {BoatChapter}/{BoatMission}'s hulls, and the authored patrolboat* exclusion is what keeps a pilot off them");
            });
        }
        finally
        {
            // Only what this suite put on the harness host: the hulls and their runtime hang off
            // the world root and go with the world.
            quarry?.Free();
            shooter?.Free();
            if (pool != null && GodotObject.IsInstanceValid(pool))
            {
                pool.Free();
            }

            textures.Dispose();
        }

        ctx.WriteArtifact($"test-ai-vessel-targets-{BoatChapter}-{BoatMission}.txt", report.ToString());
    }

    // One aeroplane on the harness host, stock-armed so the gunner's own fire control is built:
    // the acquisition runs off the back of that, not off the loadout.
    private static FlightController Rig(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        PlaneStats stats, ProjectilePool live, LoadoutDef stock, WeaponDefs weapons, AiPilot? pilot,
        Vector3 at, Vector3 lookAt, int team, string name)
    {
        var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
        var rig = new FlightController
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            Damage = stats.DestroyableParts.Count > 0 || stats.VehicleHealth is > 0f
                ? PlaneDamage.For(stats) : null,
            PlayerIndex = FlightRoster.ShooterIdBase + (pilot != null ? 0 : 1),
            IsHumanPiloted = false,
            Pilot = pilot,
            Projectiles = live,
            UseKeyboard = false,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            Team = team,
        };
        rig.AddChild(model);
        rig.Loadout = Loadout.Bind(stock, model, weapons);
        rig.Setup(new FlightModel(stats, aiForcePath: true), null, new CamParams(), at, lookAt);
        rig.Name = name;
        ctx.Host.AddChild(rig);
        rig.Held = true;
        rig.PlaceHeld(at, lookAt);
        live.RegisterAircraft(rig.Body!);
        return rig;
    }

    private static RosterSpawnPlan? PlanNamed(CampaignRosterPlan plan, string name)
    {
        foreach (var spawn in plan.Spawns)
        {
            if (spawn.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return spawn;
            }
        }

        return null;
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
