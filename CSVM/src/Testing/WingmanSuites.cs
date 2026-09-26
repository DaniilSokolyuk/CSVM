using System.IO;
using System.Linq;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Roster;
using Godot;

namespace CSVM.Testing;

/// <summary>The campaign wingman's station-keeping: the decoded netless <c>mode wingman</c>
/// escort law, first as pure geometry and then flown by two live aircraft.</summary>
internal static class WingmanSuites
{
    // The wingman starts this far out on its leader's beam: outside the 700 m join threshold, so
    // the join is something the suite watches happen rather than something it starts inside of,
    // and abeam rather than astern so the closure never depends on out-running the leader.
    private const float StartAbeamM = 1200f;

    private const float StepDt = 1f / 60f;

    // Every rig's spawn lever and speed, the value a mission's PLAYER_INIT would give. The scripted
    // legs below never move off this lever, so their leader holds one straight cruise; the flown leg
    // firewalls from it, which is the case those legs cannot fail on.
    private const float LeaderThrottle = 0.3f;
    private const float LeaderSpeedMps = 55f;

    // The hold window, and what the hold is judged against. The commanded point is at most 99 m
    // from the leader (the 19 m station plus the 80 m push) and the law weaves around it instead
    // of settling on it, so the leash allows for that weave while still failing the behaviour
    // BL-362 reports, a wingman that simply leaves.
    private const float HoldFromS = 60f;
    private const float LeashM = 600f;
    private const float MeanHoldM = 250f;

    // The campaign's own starting airframe, which is what C3/M01 gives the player and wingman_1.
    // ⚠ Its fd_speed is 113 m/s against the law's 111.76 m/s ceiling, a 1.24 m/s margin, where the
    // suite's default Bloodhawk has 23 m/s. Quote a campaign figure off this one only.
    private const string FlownPlaneNode = "player_pfighter";

    // The AI def a campaign wingman block resolves to, and how many spawn draws the jitter gate is
    // swept over: a pinned seed makes one draw, and a gate that leaks on some draws survives it.
    private const string WingmanAiDef = "wingman";
    private const int JitterSeeds = 64;

    // The flown-leader leg: its spawn altitude, how long it runs, and from when it is judged. The
    // first seconds are the leader's own acceleration off the spawn lever, which no station-keeper
    // can be inside of, so the hold is judged after them.
    private const float FlownLeaderAltitudeM = 1200f;
    private const float FlownRunS = 120f;
    private const float FlownHoldFromS = 10f;

    // The flown leg's leash IS the join gate, because an escort that has never joined commands a
    // point 200 m above its leader and that is the reported symptom. ⚠ It is not an exit: the
    // formation state is never left once entered, so crossing this line matters at the FIRST join,
    // not later. It bounds the settled average below as well as the worst separation.
    private const float FlownLeashM = AiEscort.JoinRangeM;

    // The tail of the run the hold is judged as settled over. ⚠ The hold's own mean is not a
    // property of station-keeping: the leader firewalls off its spawn lever and holds full throttle,
    // so on one airframe leader and wingman share a top speed and a stern chase never closes, and
    // what the wingman recovers is only however much corner the leader's turns offer it. The settled
    // average against the hold's says the wingman is holding rather than leaving, which is the
    // reported failure, and it does not move with the profile's turn budget.
    private const float SettledWindowS = 30f;

    // How long the leader's stick is held over to roll into each turn. ⚠ Authored for the BANK it
    // reaches, about 45°, not for the deflection-seconds: the plant's roll rate is what converts one
    // into the other, so a change to it moves this number. Held twice this long the leader rolls
    // past 95°, and the pull that follows digs a knife-edge descent instead of a climbing turn.
    private const float RollInS = 0.75f;

    // The pull through each turn, and the climb/descent pair between them. Same rule as RollInS:
    // these are the attitude change the leader is meant to fly, converted to a hold by the plant's
    // own pitch rate.
    private const float TurnPullS = 4f;
    private const float ClimbPullS = 3f;

    // The push after each turn's roll-out, held until the flight path is level rather than for a
    // fixed time, with this as its safety cap. ⚠ A timed push cannot level two airframes: the same
    // deflection loses more height at 135 m/s than at 113. The plant has no auto-level, so a profile
    // that ends nose-high phugoids through the band edge and into the ground afterwards.
    private const float TurnLevelCapS = 6f;

    // The altitude both aircraft must stay above for the leg to mean anything. ⚠ Below
    // FlightController's under-map backstop an aircraft is teleported to its spawn with no crash
    // and no log, and the teleport lands in the separation statistic as a several-kilometre reading
    // that no aeroplane flew. Fail on the descent instead of averaging the jump.
    private const float FlownFloorM = 200f;

    // The asymmetric leg's clock: the formation is settled by 40 s, so the command goes in at 45 s
    // and the baseline is the ten seconds before it. The leader then holds its straight line past
    // the cutoff, because the gap keeps opening while it sheds the boost. The burn is not timed
    // here at all: it ends on the tank's own 5 % cutoff, 30 units burned at a net 3/s.
    private const float NitroEngageAtS = 45f;
    private const float NitroBaselineS = 10f;
    private const float NitroStraightS = 63f;

    // ⚠ The asymmetric leg spawns the pair on the cruise it holds, not on the spawn lever the other
    // legs use. Accelerating from 55 m/s opens a transient of its own, which would be read as the
    // injector's term; the question here is what the burn adds to a settled formation.
    private const float NitroSpawnThrottle = 1f;

    // The turns after the burn, and how long the whole leg runs. ⚠ Two aircraft at one fd_speed
    // close no straight-line gap, so the corner a turn offers is the only way back to the station
    // and a leg with no turns in it measures a permanent loss.
    private const int NitroTurnCycles = 5;
    private const float NitroLevelS = 3f;
    private const float NitroRunS = 140f;

    // What the burn has to cost the station for the leg to have measured anything, and how long
    // after the cutoff the escort has to be back inside the symmetric legs' leash. Both are floors
    // under the run's own figures, not targets: the burn opens about 390 m and it is back inside
    // the leash about 15 s after the cutoff.
    private const float NitroLossM = 200f;
    private const float NitroReformCapS = 30f;

    // How close to its pre-burn separation the escort has to be back by the end of the leg. The run
    // settles within single metres of it, so this is slack, not a target.
    private const float NitroReformM = 100f;

    // The engagement leg's stage. The bandit flies the same course as the pair from this far
    // ahead, so it sits in the wingman's own forward gun cone without the wingman maneuvering:
    // the leg then answers what the guns do while the station is held, not what the flight law
    // would do if it chased.
    private const float EngageBanditAheadM = 700f;
    private const float EngageRunS = 45f;

    // How far off the pair's track the bandit is placed. Dead ahead the leader flies into it and
    // the leg measures a ram; 80 m clears both airframes and still leaves the bandit well inside
    // the wingman's own gun cone all the way in.
    private const float EngageBanditAbeamM = 80f;

    // How long the cutscene-hold leg holds the clock, and then flies it: C3/M01's intro runs about
    // 40 s, and an unheld wingman covers kilometres in that time.

    // CM02, whose wingman_4 authors the rating_biases the engagement question is asked against.
    private const string EngageChapter = "C3";
    private const string EngageMission = "M05";

    // CM05's own pairing, read out of its roster: wingman_2 escorts devastator_1, which flies
    // M4Bravo, the eleventh net of the Hawaii chapter. The net id is the mission's, not a choice.
    private const string LostLeaderChapter = "C3";
    private const int LostLeaderNetId = 11;

    // Long enough for the escort to join and the leader to walk onto a real edge before the
    // leader is taken out of play, and then for the survivor to fly the net it inherits.
    private const float LostLeaderJoinS = 8f;
    private const float LostLeaderAfterS = 4f;

    // What a pursuit leaves behind on a wingman whose quarry is then destroyed: the bearing to
    // where that aeroplane was, and the altitude it died at, both far off the formation's own.
    private const float LostLeaderStaleBearingDeg = 215f;
    private const float LostLeaderStaleAltitudeM = 90f;

    [Suite("wingman-station",
        "the D34 campaign wingman (BL-362): the decoded netless mode-wingman escort law as " +
        "geometry (both body-frame stations, the rolled-leader frame, the 106.68/259.08 m " +
        "target station, the 80 m separation push, the 700 m and 20.576 m/s join gates) and " +
        "then flown, the flown legs covering one pairing each: neither aircraft carrying a " +
        "nitrous injector, on the campaign airframe and on the suite's default, and then the " +
        "leader carrying one against an escort that carries none, where the burn costs the " +
        "escort its station and the leader's turns give it back; plus a live wingman joining " +
        "from 1200 m abeam and riding the aft station behind a player leader where it rides " +
        "the forward one behind an AI leader")]
    internal static void WingmanStation(TestContext ctx)
    {
        StationGeometry(ctx);
        JitterGate(ctx);

        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var stats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
        var textures = new TextureArchive(texturesPath);
        try
        {
            // Twice, and the pair is the point: the symmetric pairing on the campaign's own
            // airframe, then the same leg on the suite's default, where the mechanism is eighteen
            // times larger. ⚠ A number off the default describes the mechanism, never the campaign.
            FlyFlownLeaderLeg(ctx, planesGamez, textures, FlownPlaneNode);
            FlyFlownLeaderLeg(ctx, planesGamez, textures, ctx.PlaneName);
            // …and then the asymmetric pairing, the leader's injector against an escort with none.
            FlyNitroLeaderLeg(ctx, planesGamez, textures);
            var behindPlayer = FlyLeg(ctx, planesGamez, textures, stats, playerLeader: true);
            var withAi = FlyLeg(ctx, planesGamez, textures, stats, playerLeader: false);

            // The A/B the two decoded stations predict: commanded 18 m astern of a player against
            // 8 m ahead of an AI. ⚠ Off the COMMANDED point, never the flown one: the two legs
            // weave around the push by different routes and the residual buries the 26 m.
            ctx.Check(behindPlayer.Z > withAi.Z,
                $"a player leader's wingman is commanded aft of an AI leader's, its own decoded station: {behindPlayer.Z:0.0} m against {withAi.Z:0.0} m");
        }
        finally
        {
            textures.Dispose();
        }
    }

    /// <summary>What a campaign wingman does about a hostile (<c>BL-505</c>): the gates CM02's own
    /// wingman blocks fly, and then a flown leg with one bandit inside those gates, reporting
    /// whether the wingman acquires it, whether the guns fire while the station is held, and
    /// whether the decoded escort state moves.</summary>
    [Suite("wingman-engage",
        "what a campaign wingman does about a hostile (BL-505): every escorting block CM02 " +
        "plans flies an attack gate wider than BL-504's 1 m and most author rating_biases, " +
        "and over a flown leg with one bandit ahead of the pair the wingman acquires it " +
        "through the ordinary ranking and fires from the station, while the decoded escort " +
        "state never leaves the formation, which is FUN_0041e760's own shape")]
    internal static void WingmanEngage(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, "C1");
        ctx.RequireData(texturesPath, $"C1 textures");

        EngageGates(ctx);

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        try
        {
            FlyEngageLeg(ctx, planesGamez, textures);
        }
        finally
        {
            textures.Dispose();
        }
    }

    [Suite("wingman-lost-leader",
        "a wingman whose leader leaves play takes that leader's own net (BL-524): CM05's pairing, "
        + "devastator_1 walking M4Bravo#11 with its wingman on the decoded station, the leader "
        + "then deactivated so InPlay is really false, and the director's hand-off run over that "
        + "roster; the survivor drops its escort, walks the leader's net and re-derives both order "
        + "fields off a stale pursuit pair, while a wingman whose leader flies no net is left alone")]
    internal static void WingmanLostLeader(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, LostLeaderChapter);
        ctx.RequireData(chapterZrdr, $"{LostLeaderChapter} chapter zrdr");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, LostLeaderChapter);
        ctx.RequireData(texturesPath, $"{LostLeaderChapter} textures");

        if (AiNets.ById(AiNets.Load(chapterZrdr), LostLeaderNetId) is not { } leadersNet)
        {
            throw new SuiteSkippedException($"{LostLeaderChapter} carries no net #{LostLeaderNetId}");
        }

        float minActive = AiSkills.Load(ctx.ZrdrPath).MinAiActiveDist;
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        try
        {
            FlyLostLeaderLeg(ctx, planesGamez, textures, leadersNet, minActive);
        }
        finally
        {
            textures.Dispose();
        }
    }

    // The authored half: every escorting block CM02 plans, its effective engagement gates after
    // the volume overlay, and whether it authors rating_biases at all. BL-504 was a 1 m attack
    // gate reaching an aircraft it was not authored on, so the gate is read before the behaviour.
    private static void EngageGates(TestContext ctx)
    {
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, EngageChapter, EngageMission);
        ctx.RequireData(missionZrdr, $"{EngageChapter}/{EngageMission} zrdr");
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var plan = CampaignRosterPlan.Build(
            AiSkills.LoadRoster(missionZrdr),
            VehicleDefs.Load(ctx.ZrdrPath),
            AiNets.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, EngageChapter)),
            netDraw: _ => 0);

        int escorts = 0, biased = 0, gated = 0;
        foreach (var spawn in plan.Spawns)
        {
            if (!spawn.Escorts)
            {
                continue;
            }
            escorts++;
            biased += spawn.Biases.Count > 0 ? 1 : 0;
            var stats = PlaneStats.LoadForAi(ctx.ZrdrPath, spawn.PlaneNode);
            var machine = new AiModeMachine(new System.Random(7))
            {
                ActivationRange = skills.MinAiActiveDist,
                AttackRange = stats.AiAttackRange,
                ReturnRange = stats.AiReturnRange,
            };
            CampaignRosterPlan.ApplyVolumes(machine, spawn.Volumes, skills.MinAiActiveDist);
            gated += machine.AttackRange > 1f ? 1 : 0;
            ctx.Note($"[{spawn.Name}] escorts '{spawn.LeaderName}' as {spawn.PlaneNode}: attack {machine.AttackRange:0} m, return {machine.ReturnRange:0} m, activation {machine.ActivationRange:0} m, {spawn.Biases.Count} rating_biases");
        }

        ctx.Check(escorts > 0, $"{EngageChapter}/{EngageMission} plans {escorts} escorting wingman block(s)");
        ctx.Check(escorts > 0 && gated == escorts,
            $"…every one of them flies an attack gate wider than BL-504's 1 m: {gated} of {escorts}");
        ctx.Check(biased > 0,
            $"…and {biased} of {escorts} author rating_biases, which only a block that picks its own targets can use");
    }

    // The flown half: a scripted player leader on a straight cruise, its wingman on the decoded
    // station, and one hostile ahead of both on the same course. The bandit is not a block CM02
    // tells anyone to ignore, so nothing here is answered by a bias.
    private static void FlyEngageLeg(TestContext ctx, GameZ planesGamez, TextureArchive textures)
    {
        var stats = PlaneStats.Load(ctx.ZrdrPath, FlownPlaneNode);
        var aiStats = PlaneStats.LoadForAi(ctx.ZrdrPath, FlownPlaneNode);
        var weapons = WeaponDefs.Load(ctx.ZrdrPath, null);
        LoadoutDef? stock = null;
        foreach (var def in StockLoadouts.Load().All.Values)
        {
            if (def.Model == FlownPlaneNode)
            {
                stock = def;
                break;
            }
        }
        if (stock == null)
        {
            throw new SuiteSkippedException($"no stock loadout for {FlownPlaneNode}");
        }

        ProjectilePool? pool = null;
        FlightController? leader = null;
        FlightController? wing = null;
        FlightController? bandit = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var leaderPos = new Vector3(0f, FlownLeaderAltitudeM, 0f);
            leader = EngageRig(ctx, planesGamez, textures, stats, live, leaderPos, true, null,
                FlightRoster.ShooterIdBase, AimAssist.PlayerTeam, null, null, null);

            var wingPos = AiEscort.FormationStation(leaderPos, Basis.Identity, playerLeader: true);
            var escort = new AiEscort { Leader = leader };
            var pilot = AiPilot.HoldingCourse(wingPos, wingPos + Vector3.Forward);
            pilot.Escort = escort;
            pilot.Machine = new AiModeMachine(new System.Random(7));
            pilot.Gunner = new AiGunner(new RandomNumberGenerator { Seed = 20260825 });
            var leaderRig = leader;
            var humans = new Vector3[1];
            wing = EngageRig(ctx, planesGamez, textures, aiStats, live, wingPos, false, pilot,
                FlightRoster.ShooterIdBase + 1, AimAssist.PlayerTeam, stock, weapons,
                () => { humans[0] = leaderRig.WorldPosition; return humans; });
            var gun = wing.Loadout!.FirableGuns.First();

            // Pinned rather than flown: a bandit under its own stick drifts off the pair's course
            // and the leg would then measure the drift instead of the wingman. The pair closes on
            // it head-on at cruise, so the gun window opens and shuts on its own.
            var banditPos = leaderPos + (Vector3.Forward * EngageBanditAheadM)
                + (Vector3.Right * EngageBanditAbeamM);
            bandit = EngageRig(ctx, planesGamez, textures, stats, live, banditPos, false, null,
                FlightRoster.ShooterIdBase + 2, AimAssist.PlayerTeam + 1, null, null, null);
            bandit.Held = true;
            bandit.PlaceHeld(banditPos, banditPos + Vector3.Forward);

            int ammoAtStart = gun.Ammo;
            int acquired = 0, wantsFire = 0, engagingSteps = 0;
            float worstRange = 0f, closest = float.MaxValue;
            bool everCrashed = false;
            for (int i = 0; i < (int)(EngageRunS / StepDt); i++)
            {
                live.SimStep(StepDt);
                leader.SimStep(StepDt);
                wing.SimStep(StepDt);
                acquired += ReferenceEquals(pilot.Gunner.Target, bandit) ? 1 : 0;
                wantsFire += pilot.Gunner.WantsFire ? 1 : 0;
                engagingSteps += escort.State != EscortState.Station ? 1 : 0;
                worstRange = Mathf.Max(worstRange, wing.WorldPosition.DistanceTo(leader.WorldPosition));
                closest = Mathf.Min(closest, wing.WorldPosition.DistanceTo(bandit.WorldPosition));
                everCrashed |= wing.Crashed || leader.Crashed;
                if (i % 600 == 0)
                {
                    ctx.Note($"[engage] t={i * StepDt:0}s escort={escort.State} target={(pilot.Gunner.AircraftTarget is { } held ? held.Name.ToString() : "-")} bandit={wing.WorldPosition.DistanceTo(bandit.WorldPosition):0} m leader={wing.WorldPosition.DistanceTo(leader.WorldPosition):0} m rounds={ammoAtStart - gun.Ammo}");
                }
            }

            int fired = ammoAtStart - gun.Ammo;
            ctx.Note($"[engage] {acquired} of {(int)(EngageRunS / StepDt)} steps on the bandit, {wantsFire} trigger steps, {fired} rounds, {engagingSteps} steps out of the formation state, closest pass {closest:0} m, worst leader range {worstRange:0} m");
            ctx.Check(!everCrashed, $"neither the leader nor its wingman goes in over the {EngageRunS:0} s leg");
            ctx.Check(acquired > 0,
                $"a wingman holding station acquires the hostile through the same ranking every AI uses: {acquired} step(s)");
            ctx.Check(fired > 0,
                $"…and its guns fire from the station, which is the original's own tail arm of the escort law: {fired} round(s)");
            ctx.Check(engagingSteps == 0,
                $"…while the decoded escort state never leaves the formation, exactly as FUN_0041e760 has no exit from it: {engagingSteps} step(s)");
            ctx.Check(worstRange < FlownLeashM,
                $"…and the station is held throughout: worst {worstRange:0} m of {FlownLeashM:0}");
        }
        finally
        {
            bandit?.Free();
            wing?.Free();
            leader?.Free();
            pool?.Free();
        }
    }

    // The leg: a netted leader, its wingman on the station, a human leader with a wingman of its
    // own as the control, and then both leaders taken out of play at once. Only the netted one has
    // a route to pass on, which is what the hand-off is allowed to act on.
    private static void FlyLostLeaderLeg(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        AiNet leadersNet, float minActive)
    {
        var stats = PlaneStats.Load(ctx.ZrdrPath, FlownPlaneNode);
        var aiStats = PlaneStats.LoadForAi(ctx.ZrdrPath, FlownPlaneNode);
        ProjectilePool? pool = null;
        FlightController? leader = null, wing = null, human = null, humansWing = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var start = leadersNet.Nodes[0].Position;
            var leaderPilot = new AiPilot
            {
                Patrol = new AiNetFollower(leadersNet, new System.Random(3)),
            };
            leader = Rig(ctx, planesGamez, textures, aiStats, live, start, false, leaderPilot,
                FlightRoster.ShooterIdBase + 1, null, null, out _, FlownPlaneNode);
            leader.Name = "devastator_1";

            var station = AiEscort.FormationStation(start, Basis.Identity, playerLeader: false);
            var wingPilot = AiPilot.HoldingCourse(station, station + Vector3.Forward);
            wingPilot.Escort = new AiEscort { Leader = leader };
            wingPilot.Machine = new AiModeMachine(new System.Random(7)) { ActivationRange = minActive };
            wing = Rig(ctx, planesGamez, textures, aiStats, live, station, false, wingPilot,
                FlightRoster.ShooterIdBase + 2, null, null, out _, FlownPlaneNode);
            wing.Name = "wingman_2";

            // The control pair: a human leader never carries a pilot, so it flies no net, which is
            // the shape of all 21 player-led escort blocks in the shipped campaign.
            var humanPos = start + (Vector3.Right * StartAbeamM);
            human = Rig(ctx, planesGamez, textures, stats, live, humanPos, true, null,
                FlightRoster.ShooterIdBase, null, null, out _, FlownPlaneNode);
            human.Name = "player";
            var humansStation = AiEscort.FormationStation(humanPos, Basis.Identity, playerLeader: true);
            var humansWingPilot = AiPilot.HoldingCourse(humansStation, humansStation + Vector3.Forward);
            humansWingPilot.Escort = new AiEscort { Leader = human };
            humansWing = Rig(ctx, planesGamez, textures, aiStats, live, humansStation, false,
                humansWingPilot, FlightRoster.ShooterIdBase + 3, null, null, out _, FlownPlaneNode);
            humansWing.Name = "wingman_1";

            for (int i = 0; i < (int)(LostLeaderJoinS / StepDt); i++)
            {
                live.SimStep(StepDt);
                leader.SimStep(StepDt);
                wing.SimStep(StepDt);
                humansWing.SimStep(StepDt);
            }

            ctx.Check(leader.InPlay && leaderPilot.Patrol is { } walked && walked.Net.Id == LostLeaderNetId,
                $"the leader walks its own net #{LostLeaderNetId}: node {leaderPilot.Patrol?.CurrentIndex}");
            ctx.Check(wingPilot.Escort is { } held && ReferenceEquals(held.Leader, leader) && wingPilot.Patrol == null,
                $"…and its wingman is a netless escort on it, {wing.WorldPosition.DistanceTo(leader.WorldPosition):0} m off");

            wingPilot.TargetHeadingDeg = LostLeaderStaleBearingDeg;
            wingPilot.TargetAltitude = LostLeaderStaleAltitudeM;
            humansWingPilot.TargetHeadingDeg = LostLeaderStaleBearingDeg;
            humansWingPilot.TargetAltitude = LostLeaderStaleAltitudeM;
            leader.Inert = true;
            human.Inert = true;
            ctx.Check(!leader.InPlay && !human.InPlay, $"both leaders have left play");

            var roster = new System.Collections.Generic.Dictionary<string, FlightController>
            {
                ["devastator_1"] = leader,
                ["wingman_2"] = wing,
                ["player"] = human,
                ["wingman_1"] = humansWing,
            };
            int moved = CampaignDirector.TakeLostLeadersNets(roster, null, minActive,
                new System.Collections.Generic.HashSet<string>());

            ctx.Check(moved == 1, $"the hand-off moves the one wingman whose leader flies a net: {moved}");
            ctx.Check(wingPilot.Escort == null && wingPilot.Patrol is { } taken && taken.Net.Id == LostLeaderNetId,
                $"…'{wing.Name}' drops its escort and takes '{leadersNet.Name}#{leadersNet.Id}': net {wingPilot.Patrol?.Net.Id}");
            ctx.Check(humansWingPilot.Escort != null && humansWingPilot.Patrol == null,
                $"…while the human's wingman keeps its escort and is given no net");
            ctx.Check(wingPilot.Machine is { AttackRange: > 1f },
                $"…and the inherited net re-baselines the machine's gates: attack {wingPilot.Machine?.AttackRange:0} m");

            for (int i = 0; i < (int)(LostLeaderAfterS / StepDt); i++)
            {
                live.SimStep(StepDt);
                wing.SimStep(StepDt);
                humansWing.SimStep(StepDt);
            }

            float heldBearing = Mathf.Abs(Mathf.Wrap(
                humansWingPilot.TargetHeadingDeg - LostLeaderStaleBearingDeg, -180f, 180f));
            float freshBearing = Mathf.Abs(Mathf.Wrap(
                wingPilot.TargetHeadingDeg - LostLeaderStaleBearingDeg, -180f, 180f));
            ctx.Note($"[lost leader] '{wing.Name}' node {wingPilot.Patrol?.CurrentIndex} heading {wingPilot.TargetHeadingDeg:0.0}° altitude {wingPilot.TargetAltitude:0} m; '{humansWing.Name}' heading {humansWingPilot.TargetHeadingDeg:0.0}° altitude {humansWingPilot.TargetAltitude:0} m");
            ctx.Check(freshBearing > 1f && Mathf.Abs(wingPilot.TargetAltitude - LostLeaderStaleAltitudeM) > 1f,
                $"…and the survivor re-derives both orders off the stale pursuit pair: {freshBearing:0.0}° and {Mathf.Abs(wingPilot.TargetAltitude - LostLeaderStaleAltitudeM):0} m away from it");
            ctx.Check(wingPilot.SteeringPatrol, $"…flying the inherited net rather than a projected order");
            ctx.Check(heldBearing < 1f,
                $"…where the untouched wingman still holds its stale pair, the able-to-fail control: {heldBearing:0.0}° off");
        }
        finally
        {
            humansWing?.Free();
            human?.Free();
            wing?.Free();
            leader?.Free();
            pool?.Free();
        }
    }

    // The engagement leg's rig. Separate from Rig below because both the team and the stock arming
    // have to be in place BEFORE the node enters the tree: the firing-state slots are built once,
    // when the controller is readied, so a loadout bound afterwards never fires.
    private static FlightController EngageRig(TestContext ctx, GameZ planesGamez,
        TextureArchive textures, PlaneStats stats, ProjectilePool live, Vector3 pos, bool human,
        AiPilot? pilot, int shooterId, int team, LoadoutDef? stock, WeaponDefs? weapons,
        System.Func<System.Collections.Generic.IReadOnlyList<Vector3>>? humanPositions)
    {
        var model = new PlaneBuilder(planesGamez, textures).Build(FlownPlaneNode);
        var rig = new FlightController();
        rig.Bind(new FlightControllerBuild
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            Damage = new PlaneDamage(stats.DestroyableParts),
            PlayerIndex = shooterId,
            IsHumanPiloted = human,
            Pilot = pilot,
            HumanPositions = humanPositions,
            Projectiles = live,
            UseKeyboard = false,
            PadDevices = System.Array.Empty<int>(),
            AllowPause = false,
            Team = team,
        });
        if (stock != null && weapons != null)
        {
            rig.Loadout = Loadout.Bind(stock, model, weapons);
        }
        rig.Setup(new FlightModel(stats, aiForcePath: pilot != null), null, new CamParams(),
            pos, pos + Vector3.Forward, LeaderThrottle, LeaderSpeedMps);
        rig.Name = $"{FlownPlaneNode}_{shooterId}";
        ctx.Host.AddChild(rig);
        return rig;
    }

    // The per-spawn jitter's vehicle-class gate, over the campaign's own airframe: the wingman def
    // must come out of a spawn draw with the dynamics it authors, while the same airframe's jet def
    // must be moved by one. The jet arm is the able-to-fail control for the wingman arm.
    private static void JitterGate(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var wingman = PlaneStats.LoadForAi(ctx.ZrdrPath, FlownPlaneNode, WingmanAiDef);
        var jet = PlaneStats.LoadForAi(ctx.ZrdrPath, FlownPlaneNode);
        ctx.Note($"[jitter] '{wingman.AiDefName}' mode={wingman.VehicleMode} fd_speed {wingman.FdSpeed:0.0} m/s, '{jet.AiDefName}' mode={jet.VehicleMode} fd_speed {jet.FdSpeed:0.0} m/s");

        float worstWingman = 0f, worstJet = 0f;
        for (int seed = 0; seed < JitterSeeds; seed++)
        {
            var spun = wingman.WithAiSpawnJitter(new System.Random(seed));
            worstWingman = Mathf.Max(worstWingman, Mathf.Abs(spun.FdSpeed - wingman.FdSpeed));
            var spunJet = jet.WithAiSpawnJitter(new System.Random(seed));
            worstJet = Mathf.Max(worstJet, Mathf.Abs(spunJet.FdSpeed - jet.FdSpeed));
        }

        ctx.Check(worstWingman == 0f,
            $"[jitter] a mode-wingman spawn keeps its authored fd_speed over {JitterSeeds:0} seeds: worst drift {worstWingman:0.000} m/s");
        ctx.Check(worstJet > 0f,
            $"…while the same airframe's jet def is moved by the same draw, so the check above can fail: worst drift {worstJet:0.000} m/s");
    }

    // The station geometry, with no engine state at all: the two decoded offsets in a leader's own
    // body frame, the speed-ramped station on a target, and the separation push.
    private static void StationGeometry(TestContext ctx)
    {
        var leaderPos = new Vector3(100f, 500f, -200f);
        var station = AiEscort.FormationStation(leaderPos, Basis.Identity, playerLeader: true);
        ctx.Check(station.IsEqualApprox(leaderPos + new Vector3(6f, 0f, 18f)),
            $"a player leader's station is 6 m right and 18 m astern of it: {station - leaderPos}");

        // Rolled 90° right, the leader's own right axis points DOWN, so the 6 m "out" goes with
        // it: the offset is in the leader's frame, never in the world's.
        var rolled = new Basis(Vector3.Forward, Mathf.DegToRad(90f));
        var rolledStation = AiEscort.FormationStation(leaderPos, rolled, playerLeader: true);
        var rolledOffset = rolledStation - leaderPos;
        ctx.Check(rolledOffset.IsEqualApprox(new Vector3(0f, -6f, 18f)),
            $"…and rolls with the leader: 90° right of bank puts it 6 m LOW {rolledOffset}");

        var aiStation = AiEscort.FormationStation(leaderPos, Basis.Identity, playerLeader: false);
        ctx.Check(aiStation.IsEqualApprox(leaderPos + new Vector3(8f, -2f, -8f)),
            $"an AI leader's station is 8 m out, 2 m low and 8 m AHEAD: {aiStation - leaderPos}");

        // The station on a selected target: the decoded 350 ft / 850 ft ramp, placed along the
        // negation of the target's backward axis, i.e. ahead of it along its own facing.
        var targetPos = new Vector3(0f, 400f, 0f);
        var backward = Vector3.Back;
        var slow = AiEscort.TargetStation(targetPos, backward, 10f);
        var fast = AiEscort.TargetStation(targetPos, backward, 200f);
        ctx.Check(slow.IsEqualApprox(targetPos - (backward * 106.68f)),
            $"a slow target's station sits 106.68 m ahead of it: {slow - targetPos}");
        ctx.Check(fast.IsEqualApprox(targetPos - (backward * 259.08f)),
            $"…and a fast one's 259.08 m ahead: {fast - targetPos}");

        // The separation push: a station 19 m from the leader is always inside the 80 m test, so
        // the commanded point is pushed out along the leader-to-wingman line to ~99 m.
        var own = leaderPos + new Vector3(6f, 0f, 18f);
        var pushed = AiEscort.Separated(own, own, leaderPos);
        float pushedRange = pushed.DistanceTo(leaderPos);
        ctx.Check(Mathf.IsEqualApprox(pushedRange, own.DistanceTo(leaderPos) + AiEscort.SeparationM, 0.01f),
            $"the 80 m separation push moves the station out to {pushedRange:0.0} m of the leader");
        var far = leaderPos + new Vector3(0f, 0f, 120f);
        ctx.Check(AiEscort.Separated(far, far, leaderPos).IsEqualApprox(far),
            $"…and stands down outside 80 m");

        // The join: the threshold is a range AND a speed, both from the transition table.
        var escort = new AiEscort();
        var leader = new EscortLeader { Attitude = Basis.Identity, IsPlayer = true };
        escort.Next(new Vector3(0f, 0f, 701f), 60f, leader, null, out _);
        ctx.Check(escort.State == EscortState.Joining,
            $"701 m astern the wingman has not joined: {escort.State}");
        escort.Next(new Vector3(0f, 0f, 699f), 20f, leader, null, out _);
        ctx.Check(escort.State == EscortState.Joining,
            $"…inside 700 m but below 20.576 m/s it still has not: {escort.State}");
        escort.Next(new Vector3(0f, 0f, 699f), 21f, leader, null, out _);
        ctx.Check(escort.State == EscortState.Station,
            $"…and inside 700 m above 20.576 m/s it joins: {escort.State}");

        // What an escort that has NOT joined commands, which is the reported symptom's own shape:
        // a point 200 m directly above the leader. The formation state is never left inside the
        // law, so this is reachable only before the first join, never after it.
        var unjoined = new AiEscort();
        var overfly = unjoined.Next(new Vector3(0f, 0f, 100f), 5f, leader, null, out _);
        ctx.Check(unjoined.State == EscortState.Joining
            && Mathf.IsEqualApprox(overfly.Y - leader.Position.Y, AiEscort.LeaderOverflyM, 0.01f),
            $"a wingman below the join speed commands {AiEscort.LeaderOverflyM:0} m ABOVE its leader: {unjoined.State}, {overfly.Y - leader.Position.Y:0.0} m up");

        // …and the formation state, once entered, is never left however far the wingman strays.
        var joined = new AiEscort();
        joined.Next(new Vector3(0f, 0f, 100f), 60f, leader, null, out _);
        joined.Next(new Vector3(0f, 0f, 5000f), 60f, leader, null, out _);
        ctx.Check(joined.State == EscortState.Station,
            $"…and a joined wingman 5 km out is still in the formation state, not re-joining: {joined.State}");
    }

    // The leader's stick for the flown leg: a human profile, not a cruise lever. Full throttle
    // throughout, a right turn, a climb, a level-off and a left turn, which is the speed and
    // altitude range a flown mission covers. Holding a straight cruise lever is not station-keeping
    // on a player, so the scripted legs cannot fail on it.
    // ⚠ Each turn ends on a push flown to the HORIZON, not to a clock (see TurnLevelCapS).
    private static (FlightInput Input, float Duration, bool UntilLevel)[] FlownLeaderProfile() => new[]
    {
        (new FlightInput { Throttle = 1f }, 20f, false),
        (new FlightInput { Throttle = 1f, Roll = 0.6f }, RollInS, false),
        (new FlightInput { Throttle = 1f, Pitch = 0.35f }, TurnPullS, false),
        (new FlightInput { Throttle = 1f, Roll = -0.6f }, RollInS, false),
        (new FlightInput { Throttle = 1f, Pitch = -0.35f }, TurnLevelCapS, true),
        (new FlightInput { Throttle = 1f }, 5f, false),
        (new FlightInput { Throttle = 1f, Pitch = 0.15f }, ClimbPullS, false),
        (new FlightInput { Throttle = 1f, Pitch = -0.15f }, ClimbPullS, false),
        (new FlightInput { Throttle = 1f }, 5f, false),
        (new FlightInput { Throttle = 1f, Roll = -0.6f }, RollInS, false),
        (new FlightInput { Throttle = 1f, Pitch = 0.35f }, TurnPullS, false),
        (new FlightInput { Throttle = 1f, Roll = 0.6f }, RollInS, false),
        (new FlightInput { Throttle = 1f, Pitch = -0.35f }, TurnLevelCapS, true),
        (new FlightInput { Throttle = 1f }, 0f, false),
    };

    // The leg the scripted one cannot fail on: a leader flown the way a player flies, on a human
    // stick at full throttle, with the wingman on the AI force path the live roster gives it. Both
    // fly the CAMPAIGN's own airframe, not the suite's default: the Devastator's fd_speed sits
    // 1.24 m/s above the decoded ceiling, where a Bloodhawk's sits 23 m/s above it, so the default
    // would measure a far larger effect than a campaign wingman ever meets.
    private static void FlyFlownLeaderLeg(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        string planeNode)
    {
        var stats = PlaneStats.Load(ctx.ZrdrPath, planeNode);
        var aiStats = PlaneStats.LoadForAi(ctx.ZrdrPath, planeNode);
        ctx.Note($"[flown {planeNode}] leader fd_speed {stats.FdSpeed:0.0} m/s, wingman fd_speed {aiStats.FdSpeed:0.0} m/s, ceiling {AiControlLaw.SpeedCeiling:0.0} m/s, margin {stats.FdSpeed - AiControlLaw.SpeedCeiling:0.0} m/s");
        ProjectilePool? pool = null;
        FlightController? leader = null;
        FlightController? wing = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var leaderPos = new Vector3(0f, FlownLeaderAltitudeM, 0f);
            FlightController? flying = null;
            var stick = new LevellingLeaderStick(FlownLeaderProfile(),
                () => flying?.WorldVelocity.Y ?? 0f);
            leader = Rig(ctx, planesGamez, textures, stats, live, leaderPos, true, null,
                FlightRoster.ShooterIdBase, null, null, out _, planeNode, stick);
            flying = leader;

            // Spawned on its own decoded station, which is where the campaign's aiv puts it: this
            // leg is about holding the station, not about reaching it.
            var wingPos = AiEscort.FormationStation(leaderPos, Basis.Identity, playerLeader: true);
            var escort = new AiEscort { Leader = leader };
            var pilot = AiPilot.HoldingCourse(wingPos, wingPos + Vector3.Forward);
            pilot.Escort = escort;
            pilot.Machine = new AiModeMachine(new System.Random(7));
            // The human field a live session binds, so the wingman reaches the far-field plant as
            // it does in the game. That plant is no escape from the ceiling: it holds the LEVER
            // times fd_speed, and the lever already carries the cap. The readout below proves it.
            var leaderRig = leader;
            var humans = new Vector3[1];
            wing = Rig(ctx, planesGamez, textures, aiStats, live, wingPos, false, pilot,
                FlightRoster.ShooterIdBase + 1, null,
                () => { humans[0] = leaderRig.WorldPosition; return humans; }, out var wingPlant,
                planeNode);

            float worst = 0f, meanRange = 0f, worstAbove = 0f, worstLeaderSpeed = 0f;
            float farFastest = 0f, nearFastest = 0f, lowest = float.MaxValue;
            float settledRange = 0f;
            bool leftStation = false, everCrashed = false;
            int samples = 0, settledSamples = 0, farSteps = 0, allSteps = 0;
            for (int i = 0; i < (int)(FlownRunS / StepDt); i++)
            {
                live.SimStep(StepDt);
                leader.SimStep(StepDt);
                wing.SimStep(StepDt);
                float range = wing.WorldPosition.DistanceTo(leader.WorldPosition);
                worstLeaderSpeed = Mathf.Max(worstLeaderSpeed, leader.WorldVelocity.Length());
                everCrashed |= wing.Crashed || leader.Crashed;
                lowest = Mathf.Min(lowest, Mathf.Min(wing.WorldPosition.Y, leader.WorldPosition.Y));
                allSteps++;
                float wingSpeed = wing.WorldVelocity.Length();
                if (wingPlant.FarFieldPlant)
                {
                    farSteps++;
                    farFastest = Mathf.Max(farFastest, wingSpeed);
                }
                else
                {
                    nearFastest = Mathf.Max(nearFastest, wingSpeed);
                }
                if (i % 900 == 0)
                {
                    ctx.Note($"[flown {planeNode}] t={i * StepDt:0}s range={range:0} escort={escort.State} mode={AiModeMachine.NameOf(pilot.Machine!.Mode)} dY={wing.WorldPosition.Y - leader.WorldPosition.Y:0} wingV={wing.WorldVelocity.Length():0} leadV={leader.WorldVelocity.Length():0} lever={pilot.Throttle:0.00}");
                }

                if (i * StepDt < FlownHoldFromS)
                    continue;
                samples++;
                worst = Mathf.Max(worst, range);
                meanRange += range;
                worstAbove = Mathf.Max(worstAbove, wing.WorldPosition.Y - leader.WorldPosition.Y);
                leftStation |= escort.State != EscortState.Station;
                if (i * StepDt >= FlownRunS - SettledWindowS)
                {
                    settledSamples++;
                    settledRange += range;
                }
            }

            meanRange /= Mathf.Max(1, samples);
            settledRange /= Mathf.Max(1, settledSamples);
            ctx.Note($"[flown {planeNode}] hold: mean {meanRange:0} m, settled {settledRange:0} m, worst {worst:0} m, worst above {worstAbove:0} m, leader peak {worstLeaderSpeed:0} m/s, lowest {lowest:0} m");
            ctx.Note($"[flown {planeNode}] plant: far-field {farSteps} of {allSteps} steps ({100f * farSteps / Mathf.Max(1, allSteps):0.0}%), fastest far {farFastest:0.0} m/s, fastest near {nearFastest:0.0} m/s, ceiling {AiControlLaw.SpeedCeiling:0.0}, fd_speed {stats.FdSpeed:0.0}");
            ctx.Check(!everCrashed,
                $"[flown leader] neither aircraft goes in over the {FlownRunS:0} s the leader is flown");
            ctx.Check(lowest > FlownFloorM,
                $"…neither drops through the under-map backstop, whose teleport would be read as separation: lowest {lowest:0} m of {FlownFloorM:0}");
            ctx.Check(!leftStation,
                $"…the wingman never falls out of the formation state, which commands {AiEscort.LeaderOverflyM:0} m above the leader");
            ctx.Check(worst > 0f && worst < FlownLeashM,
                $"…and stays with a leader flown on a human stick: worst {worst:0} m of {FlownLeashM:0}");
            ctx.Check(settledRange < FlownLeashM && settledRange <= meanRange,
                $"…and settles rather than drifts away: last {SettledWindowS:0} s average {settledRange:0} m against the hold's {meanRange:0} m, both inside {FlownLeashM:0}");
        }
        finally
        {
            wing?.Free();
            leader?.Free();
            pool?.Free();
        }
    }

    // The asymmetric leg's stick: the cruise both aircraft spawn on, held straight through the burn
    // so nothing but the injector opens the gap, and then the symmetric leg's own turn, alternating
    // hand, for as long as the recovery is watched.
    private static (FlightInput Input, float Duration, bool UntilLevel)[] NitroLeaderProfile()
    {
        var segments = new System.Collections.Generic.List<(FlightInput, float, bool)>
        {
            (new FlightInput { Throttle = 1f }, NitroStraightS, false),
        };
        for (int cycle = 0; cycle < NitroTurnCycles; cycle++)
        {
            float roll = cycle % 2 == 0 ? 0.6f : -0.6f;
            segments.Add((new FlightInput { Throttle = 1f, Roll = roll }, RollInS, false));
            segments.Add((new FlightInput { Throttle = 1f, Pitch = 0.35f }, TurnPullS, false));
            segments.Add((new FlightInput { Throttle = 1f, Roll = -roll }, RollInS, false));
            segments.Add((new FlightInput { Throttle = 1f, Pitch = -0.35f }, TurnLevelCapS, true));
            segments.Add((new FlightInput { Throttle = 1f }, NitroLevelS, false));
        }

        segments.Add((new FlightInput { Throttle = 1f }, 0f, false));
        return segments.ToArray();
    }

    // The asymmetric pairing, which the symmetric legs above never reach: the leader's injector
    // installed and the wingman's not, the shape a bought injector makes against a wingman block
    // that authors no nitro slot. The escort commands no matching term at any lever, so the leg
    // measures what the burn costs the station and what the leader's turns give back.
    private static void FlyNitroLeaderLeg(TestContext ctx, GameZ planesGamez, TextureArchive textures)
    {
        var stats = PlaneStats.Load(ctx.ZrdrPath, FlownPlaneNode);
        var aiStats = PlaneStats.LoadForAi(ctx.ZrdrPath, FlownPlaneNode);
        ProjectilePool? pool = null;
        FlightController? leader = null;
        FlightController? wing = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var leaderPos = new Vector3(0f, FlownLeaderAltitudeM, 0f);
            FlightController? flying = null;
            var stick = new LevellingLeaderStick(NitroLeaderProfile(),
                () => flying?.WorldVelocity.Y ?? 0f);
            leader = Rig(ctx, planesGamez, textures, stats, live, leaderPos, true, null,
                FlightRoster.ShooterIdBase, null, null, out _, FlownPlaneNode, stick,
                NitroSpawnThrottle, stats.FdSpeed);
            flying = leader;
            leader.Nitro.Installed = true;

            var wingPos = AiEscort.FormationStation(leaderPos, Basis.Identity, playerLeader: true);
            var escort = new AiEscort { Leader = leader };
            var pilot = AiPilot.HoldingCourse(wingPos, wingPos + Vector3.Forward);
            pilot.Escort = escort;
            pilot.Machine = new AiModeMachine(new System.Random(7));
            var leaderRig = leader;
            var humans = new Vector3[1];
            wing = Rig(ctx, planesGamez, textures, aiStats, live, wingPos, false, pilot,
                FlightRoster.ShooterIdBase + 1, null,
                () => { humans[0] = leaderRig.WorldPosition; return humans; }, out _,
                FlownPlaneNode, null, NitroSpawnThrottle, aiStats.FdSpeed);

            float baseline = 0f, peak = 0f, peakAtS = 0f, burnS = 0f, burnEndS = -1f;
            float burnPeakSpeed = 0f, cruisePeakSpeed = 0f, rangeAtBurnEnd = 0f;
            float reformS = -1f, lowest = float.MaxValue, settled = 0f;
            bool everCrashed = false, leftStation = false, burned = false;
            int baselineSamples = 0, settledSamples = 0;
            for (int i = 0; i < (int)(NitroRunS / StepDt); i++)
            {
                float t = i * StepDt;
                if (t >= NitroEngageAtS && burnEndS < 0f)
                {
                    leader.AutoNitro = true;
                }

                live.SimStep(StepDt);
                leader.SimStep(StepDt);
                wing.SimStep(StepDt);
                float range = wing.WorldPosition.DistanceTo(leader.WorldPosition);
                float leaderSpeed = leader.WorldVelocity.Length();
                everCrashed |= wing.Crashed || leader.Crashed;
                leftStation |= escort.State != EscortState.Station;
                lowest = Mathf.Min(lowest, Mathf.Min(wing.WorldPosition.Y, leader.WorldPosition.Y));

                if (leader.Nitro.Boosting)
                {
                    burned = true;
                    burnS += StepDt;
                    burnPeakSpeed = Mathf.Max(burnPeakSpeed, leaderSpeed);
                }
                else
                {
                    if (t < NitroEngageAtS)
                        cruisePeakSpeed = Mathf.Max(cruisePeakSpeed, leaderSpeed);
                    // One burn, not a tank that refills and re-engages: the command is a held key
                    // and the original's cutoff is what ends it.
                    if (burned && burnEndS < 0f)
                    {
                        burnEndS = t;
                        rangeAtBurnEnd = range;
                        leader.AutoNitro = false;
                    }
                }

                if (t >= NitroEngageAtS - NitroBaselineS && t < NitroEngageAtS)
                {
                    baselineSamples++;
                    baseline += range;
                }

                if (t >= NitroEngageAtS && range > peak)
                {
                    peak = range;
                    peakAtS = t;
                }

                if (burnEndS >= 0f && reformS < 0f && t > burnEndS && range < FlownLeashM)
                {
                    reformS = t - burnEndS;
                }

                if (t >= NitroRunS - SettledWindowS)
                {
                    settledSamples++;
                    settled += range;
                }

                if (i % 300 == 0)
                {
                    ctx.Note($"[flown nitro] t={t:0}s range={range:0} escort={escort.State} boost={leader.Nitro.Boosting} charge={leader.Nitro.Charge:0.0} leadV={leaderSpeed:0} wingV={wing.WorldVelocity.Length():0} dY={wing.WorldPosition.Y - leader.WorldPosition.Y:0} lever={pilot.Throttle:0.00}");
                }
            }

            baseline /= Mathf.Max(1, baselineSamples);
            settled /= Mathf.Max(1, settledSamples);
            ctx.Note($"[flown nitro] burn {burnS:0.0} s ending at t={burnEndS:0.0} s, leader peak {burnPeakSpeed:0.0} m/s boosting against {cruisePeakSpeed:0.0} m/s on the lever alone");
            ctx.Note($"[flown nitro] station: baseline {baseline:0} m, {rangeAtBurnEnd:0} m at the cutoff, peak {peak:0} m at t={peakAtS:0} s, back inside {FlownLeashM:0} m after {reformS:0.0} s, settled {settled:0} m");
            ctx.Check(!everCrashed && lowest > FlownFloorM,
                $"[flown nitro] neither aircraft goes in or drops through the backstop: lowest {lowest:0} m of {FlownFloorM:0}");
            ctx.Check(leader.Nitro.Installed && !wing.Nitro.Installed,
                $"…the leader carries the injector and its escort carries none, the asymmetry the roster allows");
            ctx.Check(burnS > 0f && burnEndS > 0f,
                $"…the leader's burn ends on the tank's own cutoff rather than a clock: {burnS:0.0} s");
            ctx.Check(burnPeakSpeed > cruisePeakSpeed,
                $"…and it is a thrust term no lever reaches: {burnPeakSpeed:0.0} m/s against {cruisePeakSpeed:0.0} m/s");
            ctx.Check(peak - baseline > NitroLossM,
                $"…the escort loses station while it runs, commanding nothing to match it: {baseline:0} m out to {peak:0} m, {peak - baseline:0} m of {NitroLossM:0}");
            ctx.Check(peak > FlownLeashM,
                $"…far enough out to break the leash the symmetric legs hold: {peak:0} m of {FlownLeashM:0}");
            ctx.Check(!leftStation,
                $"…without ever falling out of the formation state, which has no exit");
            ctx.Check(reformS >= 0f && reformS < NitroReformCapS,
                $"…and is back inside that leash {reformS:0.0} s after the cutoff, of {NitroReformCapS:0} s");
            ctx.Check(settled < baseline + NitroReformM,
                $"…and re-forms on the separation it started from once the leader turns: settled {settled:0} m against the baseline's {baseline:0} m, of {NitroReformM:0} m");
        }
        finally
        {
            wing?.Free();
            leader?.Free();
            pool?.Free();
        }
    }

    // One leader/wingman pair, flown, reporting the mean COMMANDED offset in the leader's frame.
    // The leader is scripted: no pilot at all and the stick centred, so it holds a straight course.
    // IsHumanPiloted on it is the one thing that picks which decoded station the wingman flies.
    private static Vector3 FlyLeg(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        PlaneStats stats, bool playerLeader)
    {
        string kind = playerLeader ? "player" : "AI";
        ProjectilePool? pool = null;
        FlightController? leader = null;
        FlightController? wing = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);

            var leaderPos = new Vector3(0f, 500f, 0f);
            var wingPos = leaderPos + new Vector3(StartAbeamM, 0f, 0f);
            leader = Rig(ctx, planesGamez, textures, stats, live, leaderPos, playerLeader, null,
                FlightRoster.ShooterIdBase, null, null, out _);

            var escort = new AiEscort { Leader = leader };
            var pilot = AiPilot.HoldingCourse(wingPos, wingPos + Vector3.Forward);
            pilot.Escort = escort;
            pilot.Machine = new AiModeMachine(new System.Random(7));
            wing = Rig(ctx, planesGamez, textures, stats, live, wingPos, false, pilot,
                FlightRoster.ShooterIdBase + 1, null, null, out _);

            float joinedRange = -1f;
            bool joined = false, unjoined = false, leftStation = false;
            float worstLate = 0f, meanRange = 0f;
            var lateOffset = Vector3.Zero;
            var commandedOffset = Vector3.Zero;
            var farStationError = 0f;
            int lateSamples = 0, farSamples = 0;
            for (int i = 0; i < (int)(120f / StepDt); i++)
            {
                live.SimStep(StepDt);
                leader.SimStep(StepDt);
                // The pose the escort law read this step: the leader after its step, the wingman
                // before its own, so the commanded-point check below rebuilds the law's inputs.
                var wingBefore = wing.WorldPosition;
                wing.SimStep(StepDt);
                float range = wing.WorldPosition.DistanceTo(leader.WorldPosition);
                if (!joined && escort.State == EscortState.Station)
                {
                    joined = true;
                    joinedRange = range;
                }
                else if (joined && escort.State == EscortState.Joining)
                {
                    unjoined = true;
                }

                if (i % 1800 == 0)
                {
                    ctx.Note($"[{kind}] t={i * StepDt:0}s range={range:0} escort={escort.State} mode={AiModeMachine.NameOf(pilot.Machine!.Mode)} wingY={wing.WorldPosition.Y:0} leadY={leader.WorldPosition.Y:0} wingV={wing.WorldVelocity.Length():0} leadV={leader.WorldVelocity.Length():0} wingUp={wing.InPlay}/{wing.Crashed} leadUp={leader.InPlay}/{leader.Crashed}");
                }

                // The second minute is the hold; the first is the join and its settling.
                if (i * StepDt >= HoldFromS)
                {
                    lateSamples++;
                    worstLate = Mathf.Max(worstLate, range);
                    meanRange += range;
                    var leaderBasis = leader.GlobalTransform.Basis;
                    var leaderFrame = leaderBasis.Inverse();
                    lateOffset += leaderFrame * (wing.WorldPosition - leader.WorldPosition);
                    commandedOffset += leaderFrame * (escort.StationPoint - leader.WorldPosition);
                    leftStation |= escort.State != EscortState.Station;
                    // Outside the 80 m push the commanded point IS the decoded station, rebuilt
                    // from the inputs the law read. Avoid crash runs AHEAD of the escort in the
                    // fork order, so a step it owns commands the climb-out instead.
                    bool escortFlew = pilot.Machine!.Mode is not (AiMode.AvoidCrash or AiMode.Stunned);
                    if (escortFlew && wingBefore.DistanceTo(leader.WorldPosition) >= AiEscort.SeparationM)
                    {
                        farSamples++;
                        var station = AiEscort.FormationStation(leader.WorldPosition, leaderBasis, playerLeader);
                        farStationError = Mathf.Max(farStationError, escort.StationPoint.DistanceTo(station));
                    }
                }
            }

            var mean = lateOffset / Mathf.Max(1, lateSamples);
            var meanCommanded = commandedOffset / Mathf.Max(1, lateSamples);
            meanRange /= Mathf.Max(1, lateSamples);
            ctx.Note($"[{kind}] hold: mean range {meanRange:0} m, worst {worstLate:0} m, mean flown offset out {mean.X:0.0} up {mean.Y:0.0} along {mean.Z:0.0}, mean commanded offset out {meanCommanded.X:0.0} up {meanCommanded.Y:0.0} along {meanCommanded.Z:0.0}, {farSamples} samples outside the push (station error {farStationError:0.00} m)");

            // A player leader's wingman walks the join gate; an AI leader's is forced into the
            // engaging state every frame and, with no target, reads the formation station at once.
            if (playerLeader)
            {
                ctx.Check(joined, $"[{kind} leader] the wingman joins from {StartAbeamM:0} m abeam");
                ctx.Check(joined && joinedRange < AiEscort.JoinRangeM,
                    $"…inside the decoded 700 m threshold: joined at {joinedRange:0} m");
                ctx.Check(!unjoined, $"…and never falls back out of the formation state");
            }
            else
            {
                ctx.Check(escort.State == EscortState.Station,
                    $"[{kind} leader] the forced engaging state reads the formation station: {escort.State}");
            }

            // The commanded point never sits farther from the leader than the station's own 19 m
            // plus the 80 m separation push, and the law weaves around it rather than settling on
            // it (that limit cycle is the decoded law's own: docs/org/aiPilot.md).
            ctx.Check(worstLate > 0f && worstLate < LeashM,
                $"…and stays with it: worst {worstLate:0} m of {LeashM:0}, mean {meanRange:0} m over the last minute");
            ctx.Check(meanRange < MeanHoldM,
                $"…averaging inside {MeanHoldM:0} m of the leader: {meanRange:0} m");
            // The decode pins the COMMANDED station, not where the push's weave puts the flown
            // mean: the formation state is never left, and outside
            // the push the commanded point is the body-frame station to the centimetre.
            if (playerLeader)
            {
                ctx.Check(!leftStation, $"…never leaving the formation state during the hold");
            }
            ctx.Check(farSamples > 0 && farStationError < 0.01f,
                $"…commanding the decoded station whenever outside the 80 m push: {farSamples} samples, worst error {farStationError:0.000} m");

            return meanCommanded;
        }
        finally
        {
            wing?.Free();
            leader?.Free();
            pool?.Free();
        }
    }

    // One aircraft on the suite's own stage: no camera, no HUD, no devices, exactly what
    // FlightRoster builds for an actor, the AI force path included. `holdSegments` gives the
    // leader a scripted human stick; null leaves it on its spawn lever with the stick centred.
    private static FlightController Rig(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        PlaneStats stats, ProjectilePool live, Vector3 pos, bool human, AiPilot? pilot, int shooterId,
        (FlightInput Input, float Duration)[]? holdSegments,
        System.Func<System.Collections.Generic.IReadOnlyList<Vector3>>? humanPositions,
        out FlightModel plant, string? planeNode = null, IFlightInputSource? stick = null,
        float throttle = LeaderThrottle, float speedMps = LeaderSpeedMps)
    {
        var model = new PlaneBuilder(planesGamez, textures).Build(planeNode ?? ctx.PlaneName);
        var rig = new FlightController();
        rig.Bind(new FlightControllerBuild
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            Damage = new PlaneDamage(stats.DestroyableParts),
            PlayerIndex = shooterId,
            IsHumanPiloted = human,
            Pilot = pilot,
            HoldSegments = holdSegments,
            InputSource = stick,
            HumanPositions = humanPositions,
            Projectiles = live,
            UseKeyboard = false,
            PadDevices = System.Array.Empty<int>(),
            AllowPause = false,
        });
        plant = new FlightModel(stats, aiForcePath: pilot != null);
        rig.Setup(plant, null, new CamParams(), pos, pos + Vector3.Forward,
            throttle, speedMps);
        ctx.Host.AddChild(rig);
        return rig;
    }
    // Plays the profile above, ending each turn on the horizon rather than on a stopwatch: the push
    // is held until the flight path stops climbing, capped so an unusual attitude cannot hang the
    // leg. The last segment holds forever, as a timed script's does.
    private sealed class LevellingLeaderStick : IFlightInputSource
    {
        private readonly (FlightInput Input, float Duration, bool UntilLevel)[] _segments;
        private readonly System.Func<float> _climbRateMps;
        private int _index;
        private float _held;

        public LevellingLeaderStick((FlightInput Input, float Duration, bool UntilLevel)[] segments,
            System.Func<float> climbRateMps)
        {
            _segments = segments;
            _climbRateMps = climbRateMps;
        }

        public FlightInput Read(float dt)
        {
            var seg = _segments[_index];
            if (_index == _segments.Length - 1)
                return seg.Input;
            _held += dt;
            bool done = seg.UntilLevel
                ? _climbRateMps() <= 0f || _held >= seg.Duration
                : _held >= seg.Duration;
            if (done)
            {
                _index++;
                _held = 0f;
            }

            return seg.Input;
        }
    }
}
