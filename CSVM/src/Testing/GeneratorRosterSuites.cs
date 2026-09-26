using System.Collections.Generic;
using System.IO;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Roster;
using Godot;

namespace CSVM.Testing;

/// <summary>The mission spawner's roster read: an enemy generator's <c>vehicle.params</c> label
/// resolves to the mission's own disabled roster block, and the aircraft it launches carries that
/// block's fields rather than a CLI airframe's defaults.</summary>
internal static class GeneratorRosterSuites
{
    // The one shipped mission whose generator template authors the nitro slot: C5/M04's dantezep
    // launches parameter 'Miles', the disabled block stihellhound_5_7, which authors slot 34.
    private const string Chapter = "C5";
    private const string Mission = "M04";
    private const string NitroBlock = "stihellhound_5_7";
    private const string NitroPattern = "sactrust";

    private const string TypoChapter = "C1";
    private const string TypoMission = "M04";
    private const string TypoGenerator = "eairg32";
    private const string ResolvedGenerator = "eairg31";

    private const string ShipChapter = "C2";
    private const string ShipMission = "M01";
    private const string ShipGenerator = "eshipg31";

    [Suite("generator-roster-params",
        "the mission spawner's roster read (BL-453) over C5/M04: the dantezep generator's "
        + "vehicle.params label 'Miles' resolves to the disabled block stihellhound_5_7 with "
        + "no campaign profile in the run, the aircraft it launches carries that block's "
        + "nitro slot, its own authored name and that block's militia livery, its volumes reach "
        + "the machine under the min_ai_active_dist floor, the CLI-airframe fallback a "
        + "parameterless generator takes installs no injector and stays unpainted, and over "
        + "C1/M04 eairg32's misspelt 'Eairg32_params' "
        + "resolves the decoded empty launch (nothing built, no airframe) while eairg31's "
        + "label resolves its block, and over C2/M01 eshipg31's 'Eshipg31_params' resolves a "
        + "surface launch of the patrolboat hull")]
    internal static void GeneratorRosterParams(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, Chapter);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(chapterZrdr, $"{Chapter} zrdr");
        ctx.RequireData(texturesPath, $"C1 textures");

        var nets = AiNets.Load(chapterZrdr);
        var defs = VehicleDefs.Load(ctx.ZrdrPath);
        var templates = CampaignRosterPlan.GeneratorTemplates(missionZrdr, defs, nets);
        string? parameter = null;
        foreach (var def in EnemyGenerators.Load(missionZrdr))
        {
            parameter ??= def.VehicleParams;
        }
        ctx.Check(parameter != null, $"{Chapter}/{Mission}'s generator names a vehicle.params block");
        if (parameter == null || !templates.TryGetValue(parameter, out var plan))
        {
            throw new SuiteSkippedException(
                $"{Chapter}/{Mission} carries no generator template for '{parameter}'");
        }

        ctx.Check(plan.Name == NitroBlock,
            $"the generator's '{parameter}' resolves to the roster block {NitroBlock}");
        ctx.Check(plan.Nitro, $"the block's slot 34 plans an installed nitro injector");
        CheckUnresolvedLabel(ctx, defs);
        var skills = AiSkills.Load(ctx.ZrdrPath);

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        var pool = new ProjectilePool(textures, null, null);
        FlightController? launched = null;
        FlightController? fallback = null;
        ctx.Host.AddChild(pool);
        try
        {
            var spec = SessionSpec.Parse(System.Array.Empty<string>());
            var resources = new AircraftAssemblyResources
            {
                PlanesGamez = planesGamez,
                StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
                AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
                PaintRng = new RandomNumberGenerator(),
                ZrdrPath = ctx.ZrdrPath,
                StockLoadouts = StockLoadouts.Load(),
                WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
                Textures = textures,
                Shakes = ShakeDefs.Load(ctx.ZrdrPath),
            };
            var roster = new FlightRoster(FlightRosterPolicy.From(spec),
                new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
                null, ctx.Host, resources,
                new FlightWorldBindings
                {
                    Projectiles = pool,
                    Gamez = planesGamez,
                    ChapterZrdrPath = chapterZrdr,
                },
                new HumanRosterBindings());

            var drop = new Vector3(0f, 900f, 0f);
            var pilot = AiPilot.HoldingCourse(drop, drop + Vector3.Forward);
            launched = roster.SpawnAi(
                CampaignRosterPlan.SpawnFor(plan, drop, drop + Vector3.Forward, pilot));
            CampaignRosterPlan.ApplyPlan(pilot, plan, skills.MinAiActiveDist);

            ctx.Check(launched.Nitro.Installed,
                $"the generated aircraft carries the block's nitro injector");
            ctx.Check(launched.Name == NitroBlock,
                $"the generated aircraft wears the block's own name");
            // The template's militia def decides the livery as much as the fit and the skills do,
            // and the launch is built under the shipped-skins reading, which the def outranks.
            ctx.Check(launched.ShippedSkins && launched.Scheme?.Pattern == NitroPattern,
                $"…and its militia def's authored '{NitroPattern}' livery ('{launched.Scheme?.Pattern ?? "-"}')");
            ctx.Check(pilot.Machine == null
                      || pilot.Machine.ActivationRange >= skills.MinAiActiveDist,
                $"the plan's volumes reach the machine under the min_ai_active_dist floor");

            // The CLI fallback the spawner takes when no parameter resolves, so the arm above is
            // seen able to fail: nothing outside a roster block installs an injector.
            var spare = new Vector3(0f, 900f, -300f);
            fallback = roster.SpawnAi(new AiSpawn(ctx.PlaneName, spare, spare + Vector3.Forward,
                AiPilot.HoldingCourse(spare, spare + Vector3.Forward), ShippedSkins: true));
            ctx.Check(!fallback.Nitro.Installed,
                $"a spawn with no roster block installs no injector");
            ctx.Check(fallback.Scheme == null,
                $"…and names no militia def, so it keeps the shipped skins ('{fallback.Scheme?.Pattern ?? "-"}')");

            roster.ClearMembership();
        }
        finally
        {
            launched?.Free();
            fallback?.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    // C1/M04 ships the one label that names no block: eairg32's 'Eairg32_params' against a label
    // table spelling 'Earig32_params'. Its sibling eairg31 resolves, and a parameterless
    // generator keeps the CLI airframe, so the three launch shapes are seen apart.
    private static void CheckUnresolvedLabel(TestContext ctx, VehicleDefs defs)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, TypoChapter, TypoMission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, TypoChapter);
        ctx.RequireData(missionZrdr, $"{TypoChapter}/{TypoMission} zrdr");
        ctx.RequireData(chapterZrdr, $"{TypoChapter} zrdr");
        var templates = CampaignRosterPlan.GeneratorTemplates(missionZrdr, defs,
            AiNets.Load(chapterZrdr));
        var launches = new Dictionary<string, (GeneratorLaunch Kind, RosterSpawnPlan? Plan)>(
            System.StringComparer.OrdinalIgnoreCase);
        foreach (var def in EnemyGenerators.Load(missionZrdr))
        {
            var kind = CampaignRosterPlan.ResolveGeneratorLaunch(
                templates, def.VehicleParams, out var launchPlan);
            launches[def.Node] = (kind, launchPlan);
        }

        ctx.Check(launches.TryGetValue(TypoGenerator, out var typo)
                  && typo.Kind == GeneratorLaunch.Empty && typo.Plan == null,
            $"{TypoGenerator}'s misspelt params label builds nothing, not an airframe");
        ctx.Check(launches.TryGetValue(ResolvedGenerator, out var sibling)
                  && sibling.Kind == GeneratorLaunch.Template && sibling.Plan != null,
            $"{ResolvedGenerator}'s params label resolves its roster block");
        ctx.Check(CampaignRosterPlan.ResolveGeneratorLaunch(templates, null, out _)
                  == GeneratorLaunch.Airframe,
            $"a generator with no params label keeps the CLI airframe");
        CheckSurfaceLabel(ctx, defs);
    }

    // C2/M01's eshipg31 names Eshipg31_params, the disabled patrolboat_eg0 block: a mode ship def
    // resolves the fourth launch shape, a hull, and never the airframe fallback.
    private static void CheckSurfaceLabel(TestContext ctx, VehicleDefs defs)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, ShipChapter, ShipMission);
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, ShipChapter);
        ctx.RequireData(missionZrdr, $"{ShipChapter}/{ShipMission} zrdr");
        ctx.RequireData(chapterZrdr, $"{ShipChapter} zrdr");
        var templates = CampaignRosterPlan.GeneratorTemplates(missionZrdr, defs,
            AiNets.Load(chapterZrdr));
        GeneratorLaunch? kind = null;
        RosterSpawnPlan? plan = null;
        foreach (var def in EnemyGenerators.Load(missionZrdr))
        {
            if (def.Node.Equals(ShipGenerator, System.StringComparison.OrdinalIgnoreCase))
            {
                kind = CampaignRosterPlan.ResolveGeneratorLaunch(templates, def.VehicleParams, out plan);
            }
        }
        ctx.Check(kind == GeneratorLaunch.Surface && plan is { Surface: true, Def: "patrolboat", AiDef: null },
            $"{ShipGenerator}'s params label resolves a surface launch of the patrolboat hull ({kind}, '{plan?.Def}')");
    }
}
