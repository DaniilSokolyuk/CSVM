using System;
using System.Collections.Generic;
using System.Text;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using Godot;

namespace CSVM.Testing;

/// <summary>The human field against the real director and C3/M01's own shipped script: an
/// objective condition that named one aeroplane is satisfied by whichever human satisfies it, while
/// an authored <c>player</c> token still resolves to a single aircraft. Two humans throughout, and
/// every leg carries its able-to-fail control: the guest's arrival is measured against the same
/// objective with the scripted player alone, and the split gate pair against the same crossing
/// flown by one human.</summary>
internal static class CampaignHumanFieldSuites
{
    private const string Chapter = "C3";
    private const string Mission = "M01";

    // C3/M01's OBJECTIVE5: BEGIN_DORMANT -1 (so nothing but a wake reaches it) with one condition,
    // TRAVELERS ["player", "APPROACHING", [-5458, 120, -5390], 1100]. Read off the shipped
    // objectives.zrd, and re-checked here before it is flown.
    private const int TravelersObjective = 5;
    private const float TravelersRadius = 1100f;

    private const float StepDt = 1f / 60f;

    // ObjectiveGraph completes at most one objective per tick from a rotating scan, so a leg has to
    // run past all 39 of this mission's armed objectives, not one.
    private const float ScanWindow = 5f;

    // Far outside that radius on every axis, and clear of the mission terrain.
    private static readonly Vector3 FarAway = new(2000f, 900f, 2000f);

    [Suite("campaign-coop-human-field",
        "the human field over C3/M01's own shipped script and gate geometry: OBJECTIVE5's "
        + "authored 'player' TRAVELERS completes on the guest who reaches the point while the "
        + "scripted player stays 5 km out, and completes on nobody while the same aeroplane sits "
        + "there outside the field, an escorting block's 'player' leader still resolves to P1's "
        + "rig alone, and dzpath1's gate pair completes when P1 flies the green gate and the "
        + "guest the red one, which neither half completes by itself")]
    internal static void CampaignCoopHumanField(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, Chapter, Mission);
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, Chapter);
        ctx.RequireData(missionZrdr, $"{Chapter}/{Mission} zrdr");
        ctx.RequireData(texturesPath, $"{Chapter} textures");

        CampaignMission? found = null;
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(Chapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(Mission, StringComparison.OrdinalIgnoreCase))
            {
                found = m;
            }
        }

        if (found is not { } mission)
        {
            throw new SuiteSkippedException($"{Chapter}/{Mission} is not in cm_sequence");
        }

        var script = ObjectiveScript.Load(missionZrdr);
        var report = new StringBuilder();
        var where = CheckAuthored(ctx, script, report);

        ctx.WithWorld(Chapter, collision: false, Mission,
            world => Drive(ctx, world, script, mission, missionZrdr, texturesPath, where, report));

        ctx.WriteArtifact($"test-campaign-coop-human-field-{Chapter}-{Mission}.txt", report.ToString());
        ctx.Note($"{Chapter}/{Mission}: the objective a guest satisfies completes, and a split gate pair unions");
    }

    // The shipped condition this suite flies, read off the data before anything is built: a suite
    // that flew a re-authored objective would prove nothing about the game.
    private static Vector3 CheckAuthored(TestContext ctx, ObjectiveScript script, StringBuilder report)
    {
        ObjectiveDef? target = null;
        foreach (var def in script.Objectives)
        {
            if (def.Number == TravelersObjective)
            {
                target = def;
            }
        }

        var spec = target?.Travelers;
        ctx.Check(spec is { Group: null, Approaching: true, WherePoint: { Length: 3 } }
                  && string.Equals(spec.Who, "player", StringComparison.OrdinalIgnoreCase),
            $"OBJECTIVE{TravelersObjective} carries a node-form TRAVELERS on the authored 'player' subject");
        if (spec?.WherePoint is not { Length: 3 } point)
        {
            throw new SuiteSkippedException(
                $"{Chapter}/{Mission} OBJECTIVE{TravelersObjective} no longer authors a player TRAVELERS point");
        }

        ctx.Check(Mathf.IsEqualApprox(spec.Radius, TravelersRadius),
            $"…at the authored {TravelersRadius:0} m radius ({spec.Radius:0} m)");
        ctx.Check(target!.BeginDormant,
            $"…and OBJECTIVE{TravelersObjective} begins dormant, so nothing but this suite's own wake reaches it");
        var where = new Vector3(point[0], point[1], point[2]);
        report.AppendLine($"OBJECTIVE{TravelersObjective}: TRAVELERS player APPROACHING "
            + $"({where.X:0},{where.Y:0},{where.Z:0}) r={spec.Radius:0}");
        return where;
    }

    private static void Drive(TestContext ctx, TestWorld world, ObjectiveScript script,
        CampaignMission mission, string missionZrdr, string texturesPath, Vector3 where,
        StringBuilder report)
    {
        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        ProjectilePool? pool = null;
        FlightController? p1 = null, guest = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            p1 = HumanRig(ctx, planesGamez, textures, live, FarAway, "player1");
            guest = HumanRig(ctx, planesGamez, textures, live, FarAway, "player2");
            CheckTravelers(ctx, world, script, mission, missionZrdr, live, p1, guest, where, report);
            CheckScriptedPlayer(ctx, p1, guest, report);
            CheckDangerZoneUnion(ctx, world, script, missionZrdr, report);
        }
        finally
        {
            guest?.Free();
            p1?.Free();
            pool?.Free();
            textures.Dispose();
        }
    }

    // OBJECTIVE5 asks where "the player" is; with two humans flying, the human field answers.
    private static void CheckTravelers(TestContext ctx, TestWorld world, ObjectiveScript script,
        CampaignMission mission, string missionZrdr, ProjectilePool live,
        FlightController p1, FlightController guest, Vector3 where, StringBuilder report)
    {
        var director = CampaignDirector.Create(script, mission,
            CampaignProfileDef.NewProfile("Zachary"), null, missionZrdr);
        var field = new List<FlightController> { p1 };
        director.Attach(new CampaignDirector.WorldInputs
        {
            Runtime = world.Runtime,
            Gamez = world.Gamez,
            Sounds = world.Runtime.Sounds,
            Projectiles = live,
            ListenerPosition = () => p1.WorldPosition,
            PlayerAircraft = () => p1,
            Humans = () => field,
            Rng = new Random(1),
        });

        var graph = director.Graph!;
        graph.Wake(TravelersObjective);
        Step(director, ScanWindow);
        ctx.Check(!graph.CompletedOf(TravelersObjective),
            $"nobody is near the authored point, so OBJECTIVE{TravelersObjective} has not completed");

        // Able-to-fail control: the guest arrives while it is NOT in the field. A read widened to
        // every aircraft in the session would complete here and make this leg fail.
        guest.PlaceHeld(where, where + Vector3.Forward);
        Step(director, ScanWindow);
        ctx.Check(!graph.CompletedOf(TravelersObjective),
            $"…and an aeroplane at the point that no human field names still completes nothing");

        field.Add(guest);
        Step(director, ScanWindow);
        ctx.Check(graph.CompletedOf(TravelersObjective),
            $"the guest joins the field at the point and OBJECTIVE{TravelersObjective} completes off it");
        ctx.Check(p1.WorldPosition.DistanceTo(where) > TravelersRadius,
            $"…with the scripted player {p1.WorldPosition.DistanceTo(where):0} m out, well past the {TravelersRadius:0} m radius, so only the guest can have settled it");
        report.AppendLine($"travelers: completed with P1 {p1.WorldPosition.DistanceTo(where):0} m out "
            + $"and the guest {guest.WorldPosition.DistanceTo(where):0} m in");
    }

    // The other half of the split, and the one a later reader will assume was missed: an authored
    // `player` token is still ONE aeroplane, and it is P1's.
    private static void CheckScriptedPlayer(TestContext ctx, FlightController p1, FlightController guest,
        StringBuilder report)
    {
        var roster = new Dictionary<string, FlightController>(StringComparer.OrdinalIgnoreCase);
        var leader = CampaignRosterPlan.ResolveLeader("player", roster, p1);
        ctx.Check(ReferenceEquals(leader, p1),
            $"an escorting block's 'player' leader resolves to the scripted player ({p1.Name})");
        ctx.Check(!ReferenceEquals(leader, guest),
            $"…and never to a guest ({guest.Name}), because a wing follows one aeroplane");
        report.AppendLine($"scripted player: ResolveLeader(\"player\") is {p1.Name} with a field of two");
    }

    // The human-field danger-zone rule over C3/M01's own gate geometry: crossed flags are per
    // zone, so the pair may be split between two humans.
    private static void CheckDangerZoneUnion(TestContext ctx, TestWorld world, ObjectiveScript script,
        string missionZrdr, StringBuilder report)
    {
        var zones = CampaignDangerZones.Load(script, world.Gamez, missionZrdr);
        if (zones == null || !zones.TryGateProbe("dzpath1", out var gc, out var gn, out var rc, out var rn))
        {
            throw new SuiteSkippedException($"{Chapter}/{Mission} carries no dzpath1 gate geometry");
        }

        // One human flying only half the pair completes nothing, which is what makes the union
        // below a finding rather than a restatement of the single-human rule.
        var solo = CampaignDangerZones.Load(script, world.Gamez, missionZrdr)!;
        var soloDone = new List<string>();
        solo.Update(new[] { Human(gc - gn * 5f) }, soloDone.Add);
        solo.Update(new[] { Human(gc + gn * 5f) }, soloDone.Add);
        ctx.Check(!soloDone.Contains("dzpath1"),
            $"one human across dzpath1's green gate alone completes no zone ({soloDone.Count} fired)");

        var split = CampaignDangerZones.Load(script, world.Gamez, missionZrdr)!;
        var splitDone = new List<string>();
        // P1 flies the green gate and the guest the red one, each in their own slot of the field.
        split.Update(new[] { Human(gc - gn * 5f), Human(rc - rn * 5f) }, splitDone.Add);
        split.Update(new[] { Human(gc + gn * 5f), Human(rc + rn * 5f) }, splitDone.Add);
        ctx.Check(splitDone.Contains("dzpath1"),
            $"…and the same crossing split between two humans completes it ({splitDone.Count} fired), the flags unioning per zone");
        report.AppendLine($"danger zones: dzpath1 completed on a split pair ({splitDone.Count}), not on either half alone");
    }

    private static HumanState Human(Vector3 at) => new(at, null, false);

    private static void Step(CampaignDirector director, float seconds)
    {
        for (float t = 0f; t < seconds; t += StepDt)
        {
            director.Step(StepDt);
        }
    }

    // The shape every campaign suite's human rig takes, minus the pad and the pause claim.
    private static FlightController HumanRig(TestContext ctx, GameZ planesGamez,
        TextureArchive textures, ProjectilePool live, Vector3 pos, string name)
    {
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var model = new PlaneBuilder(planesGamez, textures).Build(ctx.PlaneName);
        var rig = new FlightController
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            Damage = stats.DestroyableParts.Count > 0 || stats.VehicleHealth is > 0f
                ? PlaneDamage.For(stats) : null,
            PlayerIndex = FlightRoster.ShooterIdBase - 1,
            IsHumanPiloted = true,
            Projectiles = live,
            UseKeyboard = false,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            Team = AimAssist.PlayerTeam,
        };
        rig.AddChild(model);
        rig.Setup(new FlightModel(stats), null, new CamParams(), pos, pos + Vector3.Forward);
        rig.Name = name;
        ctx.Host.AddChild(rig);
        return rig;
    }
}
