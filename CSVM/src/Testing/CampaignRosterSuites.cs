using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The campaign roster spawner over a shipped story mission's <c>aiv</c> roster and its
/// built chapter world: every enabled block gets a rig, the decoded net-versus-escort fork is applied per
/// block, the leader pass names the player rig, a deactivated block is inert, the taxi-path
/// vehicles are placed, and the escorting wingman then holds the player within the
/// <c>wingman-station</c> leash over a two-minute flown run.</summary>
internal static class CampaignRosterSuites
{
    // The story mission with the richest roster shape in one file: a player-escorting wingman_1,
    // two wingmen escorting AI leaders, netted enemies, deactivated blocks and four taxi-path
    // vehicles (docs/formats/ai-rosters.md).
    private const string RosterChapter = "C1";
    private const string RosterMission = "M04";

    private const float StepDt = 1f / 60f;
    private const float RunS = 120f;
    private const float HoldFromS = 60f;

    // The scripted player: no pilot, stick centred, a cruise lever, the same leader the
    // wingman-station suite flies against, so the leash below is that suite's.
    private const float LeaderThrottle = 0.3f;
    private const float LeaderSpeedMps = 55f;
    private const float LeashM = 600f;
    private const float MeanHoldM = 250f;
    private const float FlownLegLiftM = 800f;

    // BL-401's mission: C1/M02 is where both arms of rating_biases name something a spawn can be.
    // The player's side (wingman_4 and the devastators) authors an outright exclusion on the enemy
    // ace BLOCK bloodhawk_2, and bloodhawk_2 itself authors always-target on the player. Neither
    // pattern can match the assembler's ai{n}_{plane} fallback.
    private const string BiasChapter = "C1";
    private const string BiasMission = "M02";
    private const string ExcludingBlock = "wingman_4";
    private const string ExcludedBlock = "bloodhawk_2";
    private const string PlainBlock = "blakepeace_2_1";

    // CM02: three britbalmoral blocks sharing net 19, group 5, no rating_biases, spawned about
    // 110 m apart. The formation is authored as three aircraft on one net, not as a station.
    private const string BomberChapter = "C3";
    private const string BomberMission = "M05";
    private const int BomberNetId = 19;
    // Long enough for the formation to walk several nodes and, in the formation suite, for the
    // hit bomber to settle back beside the other two after the shot at BomberFireAtS.
    private const float BomberRunS = 40f;
    private const float BomberFireAtS = 20f;

    // How far apart the three may ever get. Their spawn spread is about 145 m, and a run that
    // splits at a node passes this inside seconds.
    private const float BomberSpreadCeilingM = 600f;

    // The crash-probe suite's trail geometry: one bomber held this far astern of another on that
    // one's own track, which is inside the decoded 4.5 s lookahead at bomber cruise and well
    // outside both airframes. Held for long enough to cover a whole probe interval, since the
    // cadence is a per-plane 0.5…1.0 s draw and a shorter hold can miss every cast.
    private const float ProbeTrailM = 200f;
    private const float ProbeArmS = 2f;
    private const float ProbeReleaseS = 8f;

    // How often the formation leg samples its own probe geometry, and how many of those samples may
    // find a wing-mate across the ray. The probe fires at most twice a second, so four samples a
    // second describe it without a per-frame hull walk. A turn does put one of three aircraft on
    // one net in front of another now and then, which costs the one behind a short climb-out; the
    // ceiling refuses a formation that has become a nose-to-tail queue, where that would be the
    // normal state rather than an occasional one.
    private const int ProbeSampleFrames = 15;
    private const int ProbeOnRayCeiling = 24;

    // A ray this far down from a bomber on its net reaches C3's ground, so a clear one means the
    // world was built without colliders and the whole suite would be measuring nothing.
    private const float ProbeGroundRayM = 2000f;

    // Backwards through the caster's own airframe: long enough to start outside its own hulls,
    // short enough not to reach the aircraft behind it (they fly about 100 m apart).
    private const float ProbeSelfRayM = 60f;

    // The two candidate rings, both dead ahead and level, so distance and the authored bias are
    // the only terms that differ between an arm's two candidates.
    private const float NearRingM = 400f;
    private const float MidRingM = 900f;
    private const float FarRingM = 1500f;
    private const float ScanRangeM = 3000f;

    // The difficulty offset's worked mission. C1/M02 is the one mission carrying all three shapes
    // at once: the ace bloodhawk_2 (slot 67, a uniform 6 across its vector), four hostile
    // blakepeace_2_* with no flag and an authored 9 natural touch beside a 0 steady hand, and
    // wingmen on the player's own side with an authored 1 dead eye.
    private const string SkillAceBlock = "bloodhawk_2";
    private const string SkillPlainBlock = "blakepeace_2_1";
    private const string SkillFriendBlock = "wingman_3";
    private const int AceNaturalTouch = 6;
    private const int AceDeadEye = 6;
    private const int PlainNaturalTouch = 9;
    private const int PlainSteadyHand = 0;
    private const int FriendDeadEye = 1;

    // What Normal adds to a hostile pilot's every rating (Flight.Hangar.Difficulty): the low tier's k.
    private const int DifficultyStep = -2;
    private const string SteadyHandKey = "steady_hand_chance";

    // BL-497: the one block in the extracted install that authors both talker and constitution
    // (7, 8) and carries an accent, so its resolved voice chances can be told apart from the
    // session's flat rating-5 fallback.
    private const string VoiceChapter = "C5";
    private const string VoiceMission = "M01";
    private const string VoiceBlock = "autogyro_1";
    private const int VoiceBlockTalker = 7;
    private const int VoiceBlockConstitution = 8;

    private static readonly string[] BomberBlocks =
    {
        "britbalmoral_1", "britbalmoral_2", "britbalmoral_3",
    };

    [Suite("campaign-roster",
        "the campaign roster spawner (D34, BL-362/BL-364) over C1/M04's shipped aiv roster in "
        + "its built world: every enabled non-player block gets a rig except the netless jet the "
        + "original builds dead, while disabled blocks remain generator templates; the decoded "
        + "fork puts an escort "
        + "on the netless wingman_1 (leader: the player rig) and on wingman_2/3 (leaders: the "
        + "devastator blocks) and a patrol net on every netted block with no block carrying "
        + "both, the deactivated blocks are inert, the four taxiPath vehicles are placed frozen, "
        + "a net's authored 700 m return radius reaches its block under the min_ai_active_dist "
        + "floor, and over a two-minute flown run wingman_1 holds the scripted player inside "
        + "the wingman-station leash")]
    internal static void CampaignRoster(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, RosterChapter, RosterMission);
        ctx.RequireData(missionZrdr, $"{RosterChapter}/{RosterMission} zrdr");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, RosterChapter);
        ctx.RequireData(texturesPath, $"{RosterChapter} textures");

        CampaignMission? found = null;
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(RosterChapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(RosterMission, StringComparison.OrdinalIgnoreCase))
            {
                found = m;
            }
        }
        if (found is not { } mission)
        {
            throw new SuiteSkippedException($"{RosterChapter}/{RosterMission} is not in cm_sequence");
        }

        var blocks = AiSkills.LoadRoster(missionZrdr);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var script = ObjectiveScript.Load(missionZrdr);
        var director = CampaignDirector.Create(script, mission, CampaignProfileDef.NewProfile("Zachary"), null);
        var report = new StringBuilder();
        report.AppendLine($"{RosterChapter}/{RosterMission} seq={mission.Seq}: {blocks.Count} roster block(s)");

        ctx.WithWorld(RosterChapter, collision: false, RosterMission, world =>
        {
            var textures = new TextureArchive(texturesPath);
            var rigs = new List<FlightController>();
            ProjectilePool? pool = null;
            try
            {
                var live = new ProjectilePool(textures, null, null);
                pool = live;
                ctx.Host.AddChild(live);

                // The human rig at the player block's own pose: a scripted leader, as the
                // wingman-station suite's is.
                var playerPose = PlayerPose(blocks);
                var playerStats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
                var player = Rig(ctx, planesGamez, textures, playerStats, live, ctx.PlaneName,
                    playerPose.Position, playerPose.Position + playerPose.Forward, human: true,
                    pilot: null, FlightRoster.ShooterIdBase, AimAssist.PlayerTeam);
                rigs.Add(player);

                int shooterId = FlightRoster.ShooterIdBase + 1;
                string what = director.BuildRoster(new CampaignDirector.RosterInputs
                {
                    ChapterZrdrPath = SessionPaths.ChapterZrdr(ctx.DataRoot, RosterChapter),
                    MissionZrdrPath = missionZrdr,
                    ZrdrPath = ctx.ZrdrPath,
                    MinAiActiveDist = skills.MinAiActiveDist,
                    Player = () => player,
                    NetTrailers = new NetTrailerTargets(
                        () => player.WorldPosition,
                        name => world.Runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null),
                    FindNodes = name => world.Runtime.FindNodes(name),
                    Spawn = (plan, pos, look, pilot) =>
                    {
                        var stats = StatsFor(ctx, plan, report);
                        // A machine so the volumes have somewhere to land and the escort dispatch
                        // runs the decoded fork order; no gunner, so nothing in this run engages.
                        pilot.Machine = new AiModeMachine(new System.Random(7))
                        {
                            ActivationRange = skills.MinAiActiveDist,
                            AttackRange = stats.AiAttackRange,
                            ReturnRange = stats.AiReturnRange,
                        };
                        var rig = Rig(ctx, planesGamez, textures, stats, live, plan.PlaneNode, pos, look,
                            human: false, pilot, shooterId++, plan.Team ?? AimAssist.PlayerTeam);
                        rig.Inert = plan.Inert;
                        rigs.Add(rig);
                        return rig;
                    },
                    Rng = new System.Random(1),
                });
                report.AppendLine($"build summary suffix: '{what}'");

                var roster = director.Roster;
                var vehicleDefs = VehicleDefs.Load(ctx.ZrdrPath);
                int expectedRoster = 0;
                int builtDead = 0;
                foreach (var (name, fields) in blocks)
                {
                    if (name.Equals(CampaignRosterPlan.PlayerBlock, StringComparison.OrdinalIgnoreCase)
                        || !AiSkills.RosterEnabled(fields))
                    {
                        continue;
                    }
                    // The original builds a netless block dead unless its def is a mode wingman.
                    string blockMode = vehicleDefs.DefForBlock(name) is { } blockDef
                        ? vehicleDefs.ModeOf(blockDef) ?? VehicleDefs.JetMode
                        : VehicleDefs.JetMode;
                    if (AiSkills.RosterNetIds(fields).Count == 0
                        && !blockMode.Equals(VehicleDefs.WingmanMode, StringComparison.OrdinalIgnoreCase))
                    {
                        builtDead++;
                        continue;
                    }
                    expectedRoster++;
                }
                ctx.Same(1, builtDead, $"one enabled block (blakepeace_2_2) is the netless jet the original builds dead");
                ctx.Same(expectedRoster, roster.Count,
                    $"every other enabled non-player roster block has a rig");
                CheckFork(ctx, roster, player, report);
                CheckPaths(ctx, director, report);
                CheckVolumes(ctx, roster, skills, report);
                FlyEscort(ctx, roster, player, rigs, live, director, report);
            }
            finally
            {
                foreach (var rig in rigs)
                {
                    rig.Free();
                }
                pool?.Free();
                textures.Dispose();
            }
        });

        ctx.WriteArtifact($"test-campaign-roster-{RosterChapter}-{RosterMission}.txt", report.ToString());
        ctx.Note($"spawned {RosterChapter}/{RosterMission}'s roster from its aiv blocks and flew wingman_1 on the player");
    }

    /// <summary>The debrief's two per-airframe kill tallies, credited off real
    /// <see cref="FlightController.Downed"/> reports over C3/M05's shipped roster. Forces down two
    /// netted <c>britbalmoral</c> bombers, three plain <c>britpeace</c> Peacemakers and the ace
    /// <c>britpeace_7</c>, reproducing CM02's own drawn stamps (2/3/starred 1, total 6); a friendly
    /// wingman's loss and an unattributed one must reach neither tally.</summary>
    [Suite("campaign-kill-credit",
        "the debrief's two per-airframe kill tallies, credited off real Downed "
        + "reports over C3/M05's shipped roster: two of the three netted britbalmoral bombers "
        + "and three of the five plain britpeace Peacemakers land in the plain array, the ace "
        + "britpeace_7 lands in the starred array instead, and a friendly wingman's loss and an "
        + "unattributed one reach neither")]
    internal static void CampaignKillCredit(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, BomberChapter, BomberMission);
        ctx.RequireData(missionZrdr, $"{BomberChapter}/{BomberMission} zrdr");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, BomberChapter);
        ctx.RequireData(texturesPath, $"{BomberChapter} textures");

        CampaignMission? found = null;
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(BomberChapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(BomberMission, StringComparison.OrdinalIgnoreCase))
            {
                found = m;
            }
        }
        if (found is not { } mission)
        {
            throw new SuiteSkippedException($"{BomberChapter}/{BomberMission} is not in cm_sequence");
        }

        var blocks = AiSkills.LoadRoster(missionZrdr);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var script = ObjectiveScript.Load(missionZrdr);
        var director = CampaignDirector.Create(script, mission, CampaignProfileDef.NewProfile("Zachary"), null);
        var report = new StringBuilder();
        report.AppendLine($"{BomberChapter}/{BomberMission} seq={mission.Seq}: {blocks.Count} roster block(s)");

        ctx.WithWorld(BomberChapter, collision: false, BomberMission, world =>
        {
            var textures = new TextureArchive(texturesPath);
            var rigs = new List<FlightController>();
            ProjectilePool? pool = null;
            FlightController? player = null;
            try
            {
                var live = new ProjectilePool(textures, null, null);
                pool = live;
                ctx.Host.AddChild(live);

                var playerPose = PlayerPose(blocks);
                var playerStats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
                player = Rig(ctx, planesGamez, textures, playerStats, live, ctx.PlaneName,
                    playerPose.Position, playerPose.Position + playerPose.Forward, human: true,
                    pilot: null, FlightRoster.ShooterIdBase, AimAssist.PlayerTeam);
                var human = player;

                director.Attach(new CampaignDirector.WorldInputs
                {
                    Runtime = world.Runtime,
                    PlayerAircraft = () => human,
                });

                int shooterId = FlightRoster.ShooterIdBase + 1;
                director.BuildRoster(new CampaignDirector.RosterInputs
                {
                    ChapterZrdrPath = SessionPaths.ChapterZrdr(ctx.DataRoot, BomberChapter),
                    MissionZrdrPath = missionZrdr,
                    ZrdrPath = ctx.ZrdrPath,
                    MinAiActiveDist = skills.MinAiActiveDist,
                    Player = () => human,
                    NetTrailers = new NetTrailerTargets(
                        () => human.WorldPosition,
                        name => world.Runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null),
                    FindNodes = name => world.Runtime.FindNodes(name),
                    Spawn = (plan, pos, look, pilot) =>
                    {
                        var stats = StatsFor(ctx, plan, report);
                        var rig = Rig(ctx, planesGamez, textures, stats, live, plan.PlaneNode, pos, look,
                            human: false, pilot, shooterId++, plan.Team ?? AimAssist.PlayerTeam);
                        rigs.Add(rig);
                        return rig;
                    },
                    Rng = new System.Random(1),
                });

                var roster = director.Roster;
                string[] plainKills =
                {
                    "britbalmoral_1", "britbalmoral_2", "britpeace_1", "britpeace_2", "britpeace_3",
                };
                foreach (var name in plainKills)
                {
                    ctx.Check(roster.TryGetValue(name, out var rig), $"'{name}' is in the built roster");
                    rig?.DebugForceCrash(player.PlayerIndex);
                }

                ctx.Check(roster.TryGetValue("britpeace_7", out var ace), $"the ace 'britpeace_7' is in the built roster");
                ace?.DebugForceCrash(player.PlayerIndex);

                // Neither a friendly loss nor an unattributed one may reach either tally: the
                // credit rule gates on the player's own kill against a hostile airframe.
                ctx.Check(roster.TryGetValue("wingman_3", out var friendly), $"a friendly wingman is in the built roster");
                friendly?.DebugForceCrash(player.PlayerIndex);
                ctx.Check(roster.TryGetValue("britpeace_8", out var spared), $"a fourth plain Peacemaker is in the built roster");
                spared?.DebugForceCrash(killer: null);

                var graph = director.Graph!;
                ctx.Check(graph.NotifyPlayerLost(), $"the player's own death is reported once");
                graph.EndAfterPlayerLost();
                Advance(director, CampaignDirector.LeavingHoldS + 0.2f);
                ctx.Check(director.Result != null, $"the mission ended and banked its result");
                var attempt = director.Result!.Value.Attempt;

                ctx.Same(2, attempt.Kills[2], $"two Balmorals (airframe 2) credited into the plain tally");
                ctx.Same(3, attempt.Kills[9], $"three plain Peacemakers (airframe 9) credited");
                ctx.Same(1, attempt.AceKills[9], $"the ace Peacemaker credited into the starred tally instead");
                int total = 0;
                for (int i = 0; i < CampaignProgression.AirframeCount; i++)
                {
                    total += attempt.Kills[i] + attempt.AceKills[i];
                }
                ctx.Same(6, total, $"both arrays sum to CM02's own drawn Overall Planes Downed");
                report.AppendLine($"kills={string.Join(",", attempt.Kills)} aceKills={string.Join(",", attempt.AceKills)}");
            }
            finally
            {
                player?.Free();
                foreach (var rig in rigs)
                {
                    rig.Free();
                }
                pool?.Free();
                textures.Dispose();
            }
        });

        ctx.WriteArtifact($"test-campaign-kill-credit-{BomberChapter}-{BomberMission}.txt", report.ToString());
        ctx.Note($"credited two Balmorals, three plain Peacemakers and the ace over {BomberChapter}/{BomberMission}'s real roster, and excluded a friendly loss and an unattributed one");
    }

    /// <summary>BL-401: a campaign spawn wears its roster block's own name, so the patterns
    /// <c>rating_biases</c> is authored with reach it, and an authored bias then moves the pick.
    /// Spawns run through the session's own <see cref="FlightRoster"/> and the same
    /// <see cref="CampaignRosterPlan.SpawnFor"/> record the campaign director builds.</summary>
    // BL-401: the assembler named every spawn ai{n}_{plane}, which no authored pattern can
    // match, so rating_biases was dead on the campaign path.
    [Suite("roster-spawn-names",
        "BL-401's authored spawn identity over C1/M02's shipped roster: a campaign spawn "
        + "wears its roster block's own name while a spawn with none keeps the counter form, "
        + "wingman_4's authored exclusion on the bloodhawk_2 BLOCK matches the spawned node "
        + "and moves the live pick off it, and bloodhawk_2's always-target on the 'player' "
        + "role takes the human rig over a nearer aircraft")]
    internal static void RosterSpawnNames(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, BiasChapter, BiasMission);
        ctx.RequireData(missionZrdr, $"{BiasChapter}/{BiasMission} zrdr");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, BiasChapter);
        ctx.RequireData(texturesPath, $"{BiasChapter} textures");

        var plan = CampaignRosterPlan.Build(
            AiSkills.LoadRoster(missionZrdr),
            VehicleDefs.Load(ctx.ZrdrPath),
            AiNets.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, BiasChapter)),
            netDraw: _ => 0);
        var excluding = PlanNamed(plan, ExcludingBlock);
        var excluded = PlanNamed(plan, ExcludedBlock);
        var plain = PlanNamed(plan, PlainBlock);
        if (excluding == null || excluded == null || plain == null)
        {
            throw new SuiteSkippedException(
                $"{BiasChapter}/{BiasMission} does not plan {ExcludingBlock}/{ExcludedBlock}/{PlainBlock}");
        }

        // The authored term, read before anything is built: the exclusion is written against the
        // BLOCK name, and it saturates rather than merely penalising.
        AiRatingBias? exclusion = null;
        foreach (var b in excluding.Biases)
        {
            if (exclusion == null && b.Matches(excluded.Name))
            {
                exclusion = b;
            }
        }
        string exclusionBias = Bias(exclusion);
        ctx.Check(exclusion is { Bias: <= -1f },
            $"'{ExcludingBlock}' authors a rating_biases exclusion on the block name '{excluded.Name}': bias={exclusionBias}");
        AiRatingBias? attract = null;
        foreach (var b in excluded.Biases)
        {
            if (attract == null && b.Matches(AiTargetRanking.PlayerRole))
            {
                attract = b;
            }
        }
        string attractBias = Bias(attract);
        ctx.Check(attract is { Bias: >= 1f },
            $"'{ExcludedBlock}' authors always-target on the '{AiTargetRanking.PlayerRole}' role: bias={attractBias}");
        if (exclusion == null)
        {
            return;
        }

        var report = new StringBuilder();
        report.AppendLine($"{BiasChapter}/{BiasMission}: {plan.Spawns.Count} planned block(s)");
        var textures = new TextureArchive(texturesPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        ProjectilePool? pool = null;
        FlightRoster? roster = null;
        FlightController? human = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            roster = Spawner(ctx, planesGamez, textures, live);

            var origin = new Vector3(0f, 500f, 0f);
            var fwd = Vector3.Forward;
            FlightController Launch(RosterSpawnPlan block, float ring)
            {
                var at = origin + (fwd * ring);
                return roster!.SpawnAi(CampaignRosterPlan.SpawnFor(block, at, at + fwd,
                    AiPilot.HoldingCourse(at, at + fwd)));
            }

            // The shooter authors no name of its own, so the counter fallback is exercised in the
            // same run: only a caller WITH an authored name takes it.
            var shooterPilot = AiPilot.HoldingCourse(origin, origin + fwd);
            var shooter = roster.SpawnAi(new AiSpawn(ctx.PlaneName, origin, origin + fwd,
                shooterPilot, Scheme: null, Team: AimAssist.PlayerTeam, AttackRating: 5));
            ctx.Check(shooter.Name == $"ai1_{ctx.PlaneName}",
                $"a spawn with no authored name keeps the assembler's counter form: {shooter.Name}");

            var aceRig = Launch(excluded, NearRingM);
            var plainRig = Launch(plain, MidRingM);
            var wingRig = Launch(excluding, MidRingM);
            ctx.Check(aceRig.Name == excluded.Name && plainRig.Name == plain.Name
                      && wingRig.Name == excluding.Name,
                $"every campaign spawn wears its block's own name: {aceRig.Name}, {plainRig.Name}, {wingRig.Name}");
            ctx.Check(exclusion.Matches(aceRig.Name),
                $"the authored pattern '{exclusion.Pattern}' matches the SPAWNED node's name '{aceRig.Name}'");
            ctx.Check(!exclusion.Matches($"ai2_{excluded.PlaneNode}"),
                $"…and matches nothing of the old counter shape 'ai2_{excluded.PlaneNode}'");

            // BL-493: the block's own pilot name (aiv slot 20) is what the original's readout
            // prints, and it outranks the airframe title. The plain block authors none, so the two
            // sources are told apart in one run rather than agreeing by accident.
            var strings = Messages.Load(ctx.MessagesPath);
            string aceName = PlaneRoster.PlaneDisplayName(aceRig.Stats!);
            string plainName = PlaneRoster.PlaneDisplayName(plainRig.Stats!);
            string wingName = PlaneRoster.PlaneDisplayName(wingRig.Stats!);
            ctx.Check(excluded.Title != null && aceName == strings.Get(excluded.Title),
                $"the ace block's authored '{excluded.Title}' is the name its marker prints: '{aceName}'");
            ctx.Check(excluding.Title != null && wingName == strings.Get(excluding.Title),
                $"…and so is the named wingman's '{excluding.Title}': '{wingName}'");
            ctx.Check(plain.Title == null && plainName != aceName && plainName != wingName,
                $"a block with no authored name keeps the airframe title instead: '{plainName}'");
            report.AppendLine($"marker names: {excluded.Name}='{aceName}' " +
                $"{excluding.Name}='{wingName}' {plain.Name}='{plainName}'");

            // Two of these blocks ship deactivated, and an out-of-play rig never reaches the scan
            // at all: leaving them inert would let a control pass for the wrong reason. Their
            // activation is campaign-roster's subject, not this suite's; the geometry is.
            var stage = new[] { shooter, aceRig, plainRig, wingRig };
            foreach (var rig in stage)
            {
                rig.Held = true;
                rig.Inert = false;
            }
            ctx.Check(Array.TrueForAll(stage, rig => rig.InPlay),
                $"every rig on the stage is in play before the ranking is measured");
            if (shooter.Pilot?.Machine is { } machine)
            {
                // The picker's reach is the ATTACK radius (docs/org/aiPilot.md); the activation one
                // goes with it so the pursue entry gate agrees with what the ranking admits.
                machine.AttackRange = ScanRangeM;
                machine.ActivationRange = ScanRangeM;
            }
            var gunner = shooter.Pilot?.Gunner;
            ctx.Check(gunner != null, $"the shooter's spawn armed a gunner");
            if (gunner == null)
            {
                return;
            }

            void Park()
            {
                shooter.PlaceHeld(origin, origin + fwd);
                aceRig.PlaceHeld(origin + (fwd * NearRingM), origin + (fwd * (NearRingM + 1f)));
                plainRig.PlaceHeld(origin + (fwd * MidRingM), origin + (fwd * (MidRingM + 1f)));
                wingRig.PlaceHeld(origin + (fwd * MidRingM), origin + (fwd * (MidRingM + 1f)));
                if (human != null)
                {
                    human.PlaceHeld(origin + (fwd * FarRingM), origin + (fwd * (FarRingM + 1f)));
                }
            }

            FlightController? Acquire(IReadOnlyList<AiRatingBias>? biases)
            {
                Park();
                gunner.AutoTarget = true;
                gunner.RatingBiases = biases;
                gunner.Target = null;
                shooter.SimStep(StepDt);
                live.SimStep(StepDt);
                return gunner.Target as FlightController;
            }

            // Arm one, the exclusion. The shooter stands in for wingman_4, so the enemy blocks are
            // its candidates and its own side is gated out by team.
            ctx.Check(aceRig.Team != shooter.Team && plainRig.Team != shooter.Team
                      && wingRig.Team == shooter.Team,
                $"the authored teams scan the enemy blocks and gate the wingman out: shooter={shooter.Team} ace={aceRig.Team} plain={plainRig.Team} wing={wingRig.Team}");
            var control = Acquire(null);
            ctx.Check(ReferenceEquals(control, aceRig),
                $"with no biases the nearer '{excluded.Name}' at {NearRingM:0} m is the pick: {control?.Name.ToString() ?? "none"}");
            var biased = Acquire(excluding.Biases);
            ctx.Check(ReferenceEquals(biased, plainRig),
                $"the authored exclusion moves the pick to '{plain.Name}' at {MidRingM:0} m: {biased?.Name.ToString() ?? "none"}");
            ReportRanks(ctx, report, origin, fwd, "exclusion",
                (aceRig, NearRingM), (plainRig, MidRingM), excluding.Biases);

            // Arm two, the player role. bloodhawk_2's own always-target is authored against
            // "player", which no human rig is NAMED (HumanFlightAdapter builds player1), so only
            // the role resolution can carry it. The shooter changes sides to be that ace.
            var playerStats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
            var humanPos = origin + (fwd * FarRingM);
            human = Rig(ctx, planesGamez, textures, playerStats, live, ctx.PlaneName, humanPos,
                humanPos + fwd, human: true, pilot: null, FlightRoster.ShooterIdBase + 50,
                AimAssist.PlayerTeam);
            human.Name = "player1";
            human.Held = true;
            shooter.Team = aceRig.Team;
            ctx.Check(human.Name != AiTargetRanking.PlayerRole,
                $"the human rig is NOT named '{AiTargetRanking.PlayerRole}': {human.Name}");
            var roleControl = Acquire(null);
            ctx.Check(ReferenceEquals(roleControl, wingRig),
                $"with no biases the wingman at {MidRingM:0} m out-ranks the human at {FarRingM:0} m: {roleControl?.Name.ToString() ?? "none"}");
            var roleBiased = Acquire(excluded.Biases);
            ctx.Check(ReferenceEquals(roleBiased, human),
                $"'{excluded.Name}'s authored always-target takes the human instead: {roleBiased?.Name.ToString() ?? "none"}");
            ReportRanks(ctx, report, origin, fwd, "player role",
                (wingRig, MidRingM), (human, FarRingM), excluded.Biases);
        }
        finally
        {
            // Only what this suite put on the host: the host is shared with every other suite in
            // the run, so sweeping it by type would free a neighbour's rig.
            human?.Free();
            var members = new List<FlightController>(roster?.AiAircraft ?? Array.Empty<FlightController>());
            roster?.ClearMembership();
            foreach (var rig in members)
            {
                rig.Free();
            }
            pool?.Free();
            textures.Dispose();
        }

        ctx.WriteArtifact($"test-roster-spawn-names-{BiasChapter}-{BiasMission}.txt", report.ToString());
        ctx.Note($"{BiasChapter}/{BiasMission}'s authored rating_biases reach their spawns and move the pick");
    }

    /// <summary>BL-497: a campaign spawn's authored <c>talker</c>/<c>constitution</c> ratings reach
    /// <see cref="AiVoiceRuntime"/> through <see cref="CampaignDirector.BuildRoster"/>'s own voice
    /// hand-off, not the session's flat rating-5 default. No chapter world is built: the roster
    /// phase alone is under test, over <c>Spawner</c>'s lightweight rig factory.</summary>
    // BL-497: CampaignDirector passed null where a spawn's own talker/constitution ratings
    // would go, so every campaign pilot chattered at the session's flat rating-5 default.
    /// <summary>The difficulty's skill offset and the ace exemption, over a shipped roster and the
    /// session's own assembler. The session runs at Normal, where <c>k</c> is -2, so a hostile
    /// block's authored ratings arrive two lower while the ace's arrive as authored.</summary>
    [Suite("roster-skill-difficulty",
        "BL-496's ace exemption over C1/M02's shipped roster at Normal (k = -2): the hostile "
        + "blakepeace_2_1's authored natural touch of 9 reaches its pilot as 7 and its authored "
        + "steady hand of 0 floors at 0 instead of going negative, the ace bloodhawk_2 (slot 67) "
        + "keeps its authored 6 on both natural touch and dead eye, and wingman_3 on the player's "
        + "own side keeps its authored dead eye of 1")]
    internal static void RosterSkillDifficulty(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, BiasChapter, BiasMission);
        ctx.RequireData(missionZrdr, $"{BiasChapter}/{BiasMission} zrdr");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, BiasChapter);
        ctx.RequireData(texturesPath, $"{BiasChapter} textures");

        var plans = CampaignRosterPlan.Build(
            AiSkills.LoadRoster(missionZrdr),
            VehicleDefs.Load(ctx.ZrdrPath),
            AiNets.Load(SessionPaths.ChapterZrdr(ctx.DataRoot, BiasChapter)),
            netDraw: _ => 0);
        var acePlan = PlanNamed(plans, SkillAceBlock);
        var plainPlan = PlanNamed(plans, SkillPlainBlock);
        var friendPlan = PlanNamed(plans, SkillFriendBlock);
        if (acePlan == null || plainPlan == null || friendPlan == null)
        {
            throw new SuiteSkippedException($"{BiasChapter}/{BiasMission} does not plan "
                + $"{SkillAceBlock}/{SkillPlainBlock}/{SkillFriendBlock}");
        }

        // The authored side of the claim, read before anything is built: without these three the
        // spawned readings below would agree with the wrong arithmetic for the wrong reason.
        ctx.Check(acePlan.Ace && !plainPlan.Ace && !friendPlan.Ace,
            $"'{SkillAceBlock}' carries slot 67 and the other two do not");
        string authored = $"ace {Rating(acePlan.Skills.NaturalTouch)}/{Rating(acePlan.Skills.DeadEye)}, plain {Rating(plainPlan.Skills.NaturalTouch)}/{Rating(plainPlan.Skills.SteadyHand)}, friendly {Rating(friendPlan.Skills.DeadEye)}";
        ctx.Check(acePlan.Skills.NaturalTouch == AceNaturalTouch && acePlan.Skills.DeadEye == AceDeadEye
            && plainPlan.Skills.NaturalTouch == PlainNaturalTouch
            && plainPlan.Skills.SteadyHand == PlainSteadyHand
            && friendPlan.Skills.DeadEye == FriendDeadEye,
            $"the three blocks author ace natural-touch/dead-eye, plain natural-touch/steady-hand and friendly dead-eye as {authored}");

        var skills = AiSkills.Load(ctx.ZrdrPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(texturesPath);
        ProjectilePool? pool = null;
        FlightRoster? roster = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            roster = Spawner(ctx, planesGamez, textures, live);

            var ace = SpawnPlan(roster, acePlan, 0f).Pilot;
            var plain = SpawnPlan(roster, plainPlan, 600f).Pilot;
            var friend = SpawnPlan(roster, friendPlan, 1200f).Pilot;
            if (ace?.Machine == null || plain?.Machine == null || friend?.Gunner == null
                || ace.Gunner == null)
            {
                throw new SuiteSkippedException("the assembler armed no machine or gunner");
            }

            int shifted = PlainNaturalTouch + DifficultyStep;
            float flooredChance = skills.At(SteadyHandKey, 0);
            float aceCone = skills.DeadEyeAngleDeg(AceDeadEye);
            float shiftedCone = skills.DeadEyeAngleDeg(AceDeadEye + DifficultyStep);
            float friendCone = skills.DeadEyeAngleDeg(FriendDeadEye);

            ctx.Check(plain.Machine.NaturalTouch == shifted,
                $"the hostile '{SkillPlainBlock}' flies its authored natural touch {PlainNaturalTouch} shifted to {shifted} at Normal: {plain.Machine.NaturalTouch}");
            float flooredExponent = AiModeMachine.ExponentFor(flooredChance);
            ctx.Check(Mathf.IsEqualApprox(plain.Machine.SteadyHandExponent, flooredExponent),
                $"…and its authored steady hand of {PlainSteadyHand} floors at rating 0 rather than going negative: exponent {plain.Machine.SteadyHandExponent:0.000} vs {flooredExponent:0.000} off chance {flooredChance:0.000}");

            ctx.Check(ace.Machine.NaturalTouch == AceNaturalTouch,
                $"the ace '{SkillAceBlock}' flies its authored natural touch unshifted: {ace.Machine.NaturalTouch} vs {AceNaturalTouch} (a shifted ace would read {AceNaturalTouch + DifficultyStep})");
            ctx.Check(Mathf.IsEqualApprox(ace.Gunner.DeadEyeAngleDeg, aceCone),
                $"…and its dead-eye cone is the authored rating's: {ace.Gunner.DeadEyeAngleDeg:0.000}° vs {aceCone:0.000}° (shifted would be {shiftedCone:0.000}°)");
            ctx.Check(!Mathf.IsEqualApprox(aceCone, shiftedCone),
                $"…on a curve where the shifted rating is a different cone ({aceCone:0.000}° vs {shiftedCone:0.000}°), so the check above can fail");

            ctx.Check(Mathf.IsEqualApprox(friend.Gunner.DeadEyeAngleDeg, friendCone),
                $"'{SkillFriendBlock}' on the player's own side takes no offset either: {friend.Gunner.DeadEyeAngleDeg:0.000}° vs {friendCone:0.000}°");
            ctx.Note($"{BiasChapter}/{BiasMission} at Normal: plain natural touch {PlainNaturalTouch} to {plain.Machine.NaturalTouch}, ace {AceNaturalTouch} to {ace.Machine.NaturalTouch}, friendly dead eye held at {FriendDeadEye}");
        }
        finally
        {
            var members = new List<FlightController>(roster?.AiAircraft ?? Array.Empty<FlightController>());
            roster?.ClearMembership();
            foreach (var rig in members)
            {
                rig.Free();
            }
            pool?.Free();
            textures.Dispose();
        }
    }

    [Suite("roster-voice-ratings",
        "BL-497's voice hand-off over C5/M01's shipped roster: autogyro_1 authors both talker "
        + "and constitution (7, 8) and an accent, and its resolved chances each read their own "
        + "authored rating on their own curve rather than the session's rating-5 fallback")]
    internal static void RosterVoiceRatings(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.SoundsPath, $"sound archive (soundsh)");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, VoiceChapter, VoiceMission);
        ctx.RequireData(missionZrdr, $"{VoiceChapter}/{VoiceMission} zrdr");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, VoiceChapter);
        ctx.RequireData(texturesPath, $"{VoiceChapter} textures");

        CampaignMission? found = null;
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(VoiceChapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(VoiceMission, StringComparison.OrdinalIgnoreCase))
            {
                found = m;
            }
        }
        if (found is not { } mission)
        {
            throw new SuiteSkippedException($"{VoiceChapter}/{VoiceMission} is not in cm_sequence");
        }

        var skills = AiSkills.Load(ctx.ZrdrPath);
        var script = ObjectiveScript.Load(missionZrdr);
        var director = CampaignDirector.Create(script, mission, CampaignProfileDef.NewProfile("Zachary"), null);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var soundDefs = SoundDefs.Load(ctx.ZrdrPath);
        var soundGroups = SoundDefs.LoadGroups(ctx.ZrdrPath);
        var voice = new CombatVoice(soundDefs, soundGroups, CombatVoice.LoadAccents(ctx.ZrdrPath));

        var textures = new TextureArchive(texturesPath);
        using var archive = new SoundArchive(ctx.SoundsPath);
        WorldSounds? sounds = null;
        MissionRadio? radio = null;
        AiVoiceRuntime? runtime = null;
        FlightRoster? roster = null;
        ProjectilePool? pool = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            roster = Spawner(ctx, planesGamez, textures, live);

            sounds = new WorldSounds(soundDefs, soundGroups)
            {
                Loader = (d, warn) => archive.Find(d.WavName, d.Looped, warn),
            };
            ctx.Host.AddChild(sounds);
            sounds.Prewarm(voice.PrewarmNames(new[] { 32 }));
            sounds.Loader = null;

            radio = new MissionRadio(soundDefs, soundGroups, sounds.StreamFor);
            ctx.Host.AddChild(radio);
            runtime = new AiVoiceRuntime(voice, sounds, radio, new System.Random(5));
            ctx.Host.AddChild(runtime);

            // The exact conversion GameSession.RegisterAiVoice applies in production: a null
            // override falls back to rating 5, otherwise each chance is read at its OWN rating.
            // What is under test is the two overrides CampaignDirector passes in, not this.
            void RegisterVoice(FlightController? ai, int? accentId, int? talkerOverride, int? constitutionOverride)
            {
                if (ai == null || accentId is not { } acc)
                {
                    return;
                }
                int talkerRating = talkerOverride ?? 5;
                int constitutionRating = constitutionOverride ?? 5;
                runtime!.RegisterAi(ai, acc, skills.At("talker_chance", talkerRating),
                    skills.At("constitution_chance", constitutionRating));
            }

            director.BuildRoster(new CampaignDirector.RosterInputs
            {
                ChapterZrdrPath = SessionPaths.ChapterZrdr(ctx.DataRoot, VoiceChapter),
                MissionZrdrPath = missionZrdr,
                ZrdrPath = ctx.ZrdrPath,
                MinAiActiveDist = skills.MinAiActiveDist,
                NetTrailers = new NetTrailerTargets(() => null, _ => null),
                Spawn = (plan, pos, look, pilot) =>
                    roster!.SpawnAi(CampaignRosterPlan.SpawnFor(plan, pos, look, pilot)),
                RegisterVoice = RegisterVoice,
                Rng = new System.Random(1),
            });

            if (!director.Roster.TryGetValue(VoiceBlock, out var rig))
            {
                throw new SuiteSkippedException($"{VoiceChapter}/{VoiceMission} does not plan '{VoiceBlock}'");
            }
            var speaker = runtime.Dispatcher.Find(rig.PlayerIndex);
            ctx.Check(speaker != null, $"'{VoiceBlock}' registers as a voice speaker");
            if (speaker == null)
            {
                return;
            }

            float fallbackTalker = skills.At("talker_chance", 5);
            float fallbackConstitution = skills.At("constitution_chance", 5);
            float expectedTalker = skills.At("talker_chance", VoiceBlockTalker);
            float expectedConstitution = skills.At("constitution_chance", VoiceBlockConstitution);

            ctx.Check(Mathf.IsEqualApprox(speaker.TalkerChance, expectedTalker),
                $"'{VoiceBlock}' talker chance reads its own authored rating ({VoiceBlockTalker}), not the session fallback (5): {speaker.TalkerChance:0.000} vs {expectedTalker:0.000} (fallback {fallbackTalker:0.000})");
            ctx.Check(Mathf.IsEqualApprox(speaker.ConstitutionChance, expectedConstitution),
                $"'{VoiceBlock}' constitution chance reads its own authored rating ({VoiceBlockConstitution}), on a curve independent of the talker rating above: {speaker.ConstitutionChance:0.000} vs {expectedConstitution:0.000} (fallback {fallbackConstitution:0.000})");
            ctx.Check(!Mathf.IsEqualApprox(speaker.TalkerChance, speaker.ConstitutionChance),
                $"…and the two chances differ from each other, since the two authored ratings (7, 8) differ: talker {speaker.TalkerChance:0.000} vs constitution {speaker.ConstitutionChance:0.000}");
            ctx.Note($"{VoiceChapter}/{VoiceMission}'s '{VoiceBlock}' authors talker={VoiceBlockTalker} constitution={VoiceBlockConstitution}, each rating reaching the voice runtime on its own curve");
        }
        finally
        {
            var members = new List<FlightController>(roster?.AiAircraft ?? Array.Empty<FlightController>());
            roster?.ClearMembership();
            foreach (var rig in members)
            {
                rig.Free();
            }
            pool?.Free();
            radio?.Free();
            if (sounds != null)
            {
                sounds.FlushOneShots();
                sounds.Free();
            }
            textures.Dispose();
        }
    }

    // The hand-built speaker in the ai-voice suite cannot see this, because the aircraft
    // that speaks is not the one whose mode moved. The mission's own roster decides both, so the
    // suite spawns C1/M02's through the director and drives the site a flown mission drives.
    [Suite("ai-voice-mission",
        "BL-934's gap between a hand-built speaker and a flown mission: C1/M02's shipped roster "
        + "spawned through CampaignDirector on the session's own mission prewarm set, where the "
        + "enemy blakepeace_2_1 authors accentID -1 and registers no speaker of its own, yet its "
        + "patrol-to-pursue commit against the human rig still rolls the computed bearing "
        + "call-out on one of the player's own voiced wingmen and reaches a playing stream on the "
        + "mission radio's flat Voice-bus player, building no positional one-shot")]
    internal static void AiVoiceMission(TestContext ctx)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.SoundsPath, $"sound archive (soundsh)");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, BiasChapter, BiasMission);
        ctx.RequireData(missionZrdr, $"{BiasChapter}/{BiasMission} zrdr");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, BiasChapter);
        ctx.RequireData(texturesPath, $"{BiasChapter} textures");

        CampaignMission? found = null;
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(BiasChapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(BiasMission, StringComparison.OrdinalIgnoreCase))
            {
                found = m;
            }
        }
        if (found is not { } mission)
        {
            throw new SuiteSkippedException($"{BiasChapter}/{BiasMission} is not in cm_sequence");
        }

        var blocks = AiSkills.LoadRoster(missionZrdr);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var script = ObjectiveScript.Load(missionZrdr);
        var director = CampaignDirector.Create(script, mission, CampaignProfileDef.NewProfile("Zachary"), null);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var soundDefs = SoundDefs.Load(ctx.ZrdrPath);
        var soundGroups = SoundDefs.LoadGroups(ctx.ZrdrPath);
        var voice = new CombatVoice(soundDefs, soundGroups, CombatVoice.LoadAccents(ctx.ZrdrPath));

        var textures = new TextureArchive(texturesPath);
        using var archive = new SoundArchive(ctx.SoundsPath);
        WorldSounds? sounds = null;
        MissionRadio? radio = null;
        AiVoiceRuntime? runtime = null;
        FlightRoster? roster = null;
        ProjectilePool? pool = null;
        FlightController? player = null;
        try
        {
            var live = new ProjectilePool(textures, null, null);
            pool = live;
            ctx.Host.AddChild(live);
            roster = Spawner(ctx, planesGamez, textures, live);

            // The session's voice lifecycle: the MISSION's own accents prewarmed while the archive
            // is open, the loader then retired as WorldSession.Build retires it, so a clip outside
            // that set cannot play here either.
            sounds = new WorldSounds(soundDefs, soundGroups)
            {
                Loader = (d, warn) => archive.Find(d.WavName, d.Looped, warn),
            };
            ctx.Host.AddChild(sounds);
            sounds.Prewarm(CombatVoice.SessionPrewarmNames(ctx.ZrdrPath, missionZrdr, soundDefs, soundGroups));
            sounds.Loader = null;

            radio = new MissionRadio(soundDefs, soundGroups, sounds.StreamFor);
            ctx.Host.AddChild(radio);
            runtime = new AiVoiceRuntime(voice, sounds, radio, new System.Random(5));
            ctx.Host.AddChild(runtime);

            var playerPose = PlayerPose(blocks);
            var playerStats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
            player = Rig(ctx, planesGamez, textures, playerStats, live, ctx.PlaneName,
                playerPose.Position, playerPose.Position + playerPose.Forward, human: true,
                pilot: null, FlightRoster.ShooterIdBase, AimAssist.PlayerTeam);
            var human = player;
            runtime.RegisterPlayer(human);

            // GameSession.RegisterAiVoice's own shape: every spawn is handed over, accent or none.
            void RegisterVoice(FlightController? ai, int? accentId, int? talkerOverride, int? constitutionOverride)
            {
                if (ai == null)
                {
                    return;
                }
                runtime!.RegisterAi(ai, accentId, skills.At("talker_chance", talkerOverride ?? 5),
                    skills.At("constitution_chance", constitutionOverride ?? 5));
            }

            director.BuildRoster(new CampaignDirector.RosterInputs
            {
                ChapterZrdrPath = SessionPaths.ChapterZrdr(ctx.DataRoot, BiasChapter),
                MissionZrdrPath = missionZrdr,
                ZrdrPath = ctx.ZrdrPath,
                MinAiActiveDist = skills.MinAiActiveDist,
                Player = () => human,
                NetTrailers = new NetTrailerTargets(() => human.WorldPosition, _ => null),
                Spawn = (plan, pos, look, pilot) =>
                    roster!.SpawnAi(CampaignRosterPlan.SpawnFor(plan, pos, look, pilot)),
                RegisterVoice = RegisterVoice,
                Rng = new System.Random(1),
            });

            if (!director.Roster.TryGetValue(PlainBlock, out var enemy))
            {
                throw new SuiteSkippedException($"{BiasChapter}/{BiasMission} does not plan '{PlainBlock}'");
            }
            ctx.Check(runtime.Dispatcher.Find(enemy.PlayerIndex) == null,
                $"the shipped enemy '{PlainBlock}' authors no accent, so it registers no speaker of its own");

            // The player's own flight, the side that actually speaks about an enemy. Talker chance
            // is pinned past 1 so the halved bearing roll (BearingChanceFactor) is still certain
            // and a silent broadcast can only be a gate decision.
            var allies = new List<FlightController>();
            foreach (var entry in director.Roster)
            {
                if (entry.Value.Team == AimAssist.PlayerTeam
                    && runtime.Dispatcher.Find(entry.Value.PlayerIndex) is { } ally)
                {
                    ally.TalkerChance = 2f;
                    allies.Add(entry.Value);
                }
            }
            ctx.Check(allies.Count > 0,
                $"the mission voices the player's own flight: {allies.Count} registered speaker(s) on team {AimAssist.PlayerTeam}");
            if (allies.Count == 0)
            {
                return;
            }

            var played = new List<(string Tag, int Trigger, string Clip)>();
            runtime.LinePlayed += (tag, trigger, clip) => played.Add((tag, trigger, clip));
            runtime.Step(3f); // past the decoded 2 s mute window

            if (enemy.Pilot is not { Machine: { } machine } enemyPilot)
            {
                throw new SuiteSkippedException($"'{PlainBlock}' spawned without a mode machine");
            }
            // The assembler arms a gunner only for a spawn carrying an attack rating, and the
            // target slot is all the dispatch site reads.
            enemyPilot.Gunner ??= new AiGunner(new RandomNumberGenerator { Seed = 20260917 });
            enemyPilot.Gunner.Target = human;

            int expected = AiVoiceDispatcher.BearingTriggerFor(
                human.WorldPosition, human.NoseDirection, enemy.WorldPosition);
            int startedBefore = sounds.OneShotsStarted;
            ctx.Check(machine.Mode == AiMode.Patrol,
                $"baseline: the enemy is on patrol before it commits mode={AiModeMachine.NameOf(machine.Mode)}");
            machine.Enter(AiMode.Pursue, $"suite: committed to the player");

            ctx.Check(played.Count == 1 && played[0].Trigger == expected,
                $"the enemy's patrol-to-pursue commit rolls exactly one line, the bearing call-out #{expected} computed in the player's own frame played=[{string.Join(", ", played)}]");
            if (played.Count == 0)
            {
                return;
            }
            ctx.Check(played[0].Clip.Contains("WA-Enemy-"),
                $"…resolved to a bearing clip of the speaking pilot's own accent clip={played[0].Clip}");
            FlightController? speakerRig = null;
            foreach (var rig in allies)
            {
                if (rig.Name.ToString() == played[0].Tag)
                {
                    speakerRig = rig;
                }
            }
            ctx.Check(speakerRig != null,
                $"…spoken by an aircraft on the player's own team, not by the accentless enemy whose mode moved speaker={played[0].Tag}");
            radio.Tick(0.1f);
            ctx.Check(radio.LinesStarted == 1 && radio.OnAir == played[0].Clip,
                $"…starting exactly one radio line lines={radio.LinesStarted} on-air={radio.OnAir}");
            ctx.Check(sounds.OneShotsStarted == startedBefore,
                $"…and no positional one-shot started={sounds.OneShotsStarted - startedBefore}");
            var radioPlayer = AiSuites.RadioPlayer(radio);
            ctx.Check(radioPlayer is { Stream: not null, Playing: true },
                $"…on a playing stream stream={radioPlayer?.Stream != null} playing={radioPlayer?.Playing}");
            ctx.Check(radioPlayer != null && radioPlayer.Bus.ToString() == AudioBuses.Voice,
                $"…on the Voice bus bus={radioPlayer?.Bus}");
            ctx.Note($"{BiasChapter}/{BiasMission}: '{PlainBlock}' carries no accent and no speaker, and its commit to the player has '{played[0].Tag}' speak trigger #{expected}");
        }
        finally
        {
            var members = new List<FlightController>(roster?.AiAircraft ?? Array.Empty<FlightController>());
            roster?.ClearMembership();
            foreach (var rig in members)
            {
                rig.Free();
            }
            player?.Free();
            pool?.Free();
            radio?.Free();
            if (sounds != null)
            {
                sounds.FlushOneShots();
                sounds.Free();
            }
            textures.Dispose();
        }
    }

    /// <summary>The session's own AI spawner with no world effects: <c>CrashProgram</c> and
    /// <c>WorldScene</c> stay null, so the assembler's crash-runtime block (their only reader) is
    /// skipped and <c>worldEffects</c> is never dereferenced. Shared with
    /// <see cref="LandingApproachSuites"/>, which spawns a mission's roster for the scaffolding
    /// those rigs carry.</summary>
    internal static FlightRoster Spawner(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        ProjectilePool live)
    {
        var spec = SessionSpec.Parse(Array.Empty<string>());
        var resources = new AircraftAssemblyResources
        {
            PlanesGamez = planesGamez,
            StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
            AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
            PaintRng = new RandomNumberGenerator(),
            ZrdrPath = ctx.ZrdrPath,
            StockLoadouts = StockLoadouts.Load(),
            WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
            // The string table the assembler resolves a spawn's authored name through. Without it
            // every rig falls back to the def-name derivation and the name checks pass vacuously.
            WeaponMessages = Messages.Load(ctx.MessagesPath),
            Textures = textures,
            Shakes = ShakeDefs.Load(ctx.ZrdrPath),
        };
        return new FlightRoster(FlightRosterPolicy.From(spec),
            new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
            null!, ctx.Host, resources,
            new FlightWorldBindings { Projectiles = live, Gamez = planesGamez },
            new HumanRosterBindings());
    }

    /// <summary>CM02's three bombers hold the formation their shared net flies. They are spawned
    /// from that mission's own roster into its own world, flown with nobody engaging them, then one
    /// is hit hard enough that its steady-hand test is certain to fail. What is under test is that
    /// the three leave their seat node the same way and stay on one node together, which the seeded
    /// branch draw did not do (<c>BL-498</c>).</summary>
    [Suite("campaign-bomber-formation",
        "CM02's three netted bombers (BL-498) spawned from C3/M05's own aiv roster into its "
        + "built world: all three carry net 19, they leave their shared seat node the same way "
        + "and fly one node of it together for forty seconds with nobody engaging them, and a "
        + "certain steady-hand failure on one leaves it on that node and back with the other two")]
    internal static void BomberFormation(TestContext ctx)
    {
        var report = WithBomberRoster(ctx, collision: false,
            (director, rigs, live, log) => FlyBombers(ctx, director.Roster, rigs, live, log));
        ctx.WriteArtifact($"test-bomber-formation-{BomberChapter}-{BomberMission}.txt", report.ToString());
        ctx.Note($"flew {BomberChapter}/{BomberMission}'s three netted bombers, unattacked and then fired on");
    }

    /// <summary>The avoid-crash override between aircraft flying one net, over the same three CM02
    /// bombers in a world built WITH colliders, which the formation suite above cannot exercise.
    /// The ray is measured on the real rigs (it sees the netted aircraft beside it and never the
    /// caster, <c>FUN_0041f810</c>'s deactivate-cast-restore), the formation's own probe geometry
    /// is sampled over forty flown seconds, and two of the three are then put in trail so the override
    /// has to arm on another netted aircraft, climb out and release back onto the net.</summary>
    [Suite("campaign-bomber-crash-probe",
        "the avoid-crash probe between C3/M05's three netted bombers in a COLLISION world: the "
        + "world-and-aircraft ray sees the netted aircraft beside it and never its own airframe, "
        + "the authored formation flies forty seconds of its net without becoming a nose-to-tail queue "
        + "on each other's lookahead rays, and one held 200 m astern of another arms the override "
        + "on that aircraft, climbs out 1000 m and releases back onto the net when the line clears")]
    internal static void BomberCrashProbe(TestContext ctx)
    {
        var report = WithBomberRoster(ctx, collision: true,
            (director, rigs, live, log) => ProbeBombers(ctx, director.Roster, rigs, live, log));
        ctx.WriteArtifact($"test-bomber-crash-probe-{BomberChapter}-{BomberMission}.txt", report.ToString());
        ctx.Note($"probed {BomberChapter}/{BomberMission}'s three netted bombers against each other");
    }

    // The C3/M05 roster build both bomber suites fly: the mission's own aiv blocks spawned through
    // the session's own assembler into that chapter's built world, with or without colliders.
    // Returns the report the flight appended to, so each suite names its own artifact.
    private static StringBuilder WithBomberRoster(TestContext ctx, bool collision,
        Action<CampaignDirector, List<FlightController>, ProjectilePool, StringBuilder> flight)
    {
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, BomberChapter, BomberMission);
        ctx.RequireData(missionZrdr, $"{BomberChapter}/{BomberMission} zrdr");
        string texturesPath = SessionPaths.ChapterTextures(ctx.DataRoot, BomberChapter);
        ctx.RequireData(texturesPath, $"{BomberChapter} textures");

        CampaignMission? found = null;
        foreach (var m in CampaignSequence.Load(ctx.ZrdrPath))
        {
            if (m.ChapterFolder.Equals(BomberChapter, StringComparison.OrdinalIgnoreCase)
                && m.MissionFolder.Equals(BomberMission, StringComparison.OrdinalIgnoreCase))
            {
                found = m;
            }
        }
        if (found is not { } mission)
        {
            throw new SuiteSkippedException($"{BomberChapter}/{BomberMission} is not in cm_sequence");
        }

        var blocks = AiSkills.LoadRoster(missionZrdr);
        var skills = AiSkills.Load(ctx.ZrdrPath);
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var script = ObjectiveScript.Load(missionZrdr);
        var director = CampaignDirector.Create(script, mission, CampaignProfileDef.NewProfile("Zachary"), null);
        var report = new StringBuilder();
        report.AppendLine($"{BomberChapter}/{BomberMission} seq={mission.Seq}: {blocks.Count} roster block(s)");

        ctx.WithWorld(BomberChapter, collision, BomberMission, world =>
        {
            var textures = new TextureArchive(texturesPath);
            var rigs = new List<FlightController>();
            ProjectilePool? pool = null;
            FlightRoster? spawner = null;
            FlightController? player = null;
            try
            {
                var live = new ProjectilePool(textures, null, null);
                pool = live;
                ctx.Host.AddChild(live);
                spawner = Spawner(ctx, planesGamez, textures, live);

                var playerPose = PlayerPose(blocks);
                var playerStats = PlaneStats.Load(ctx.ZrdrPath, ctx.PlaneName);
                player = Rig(ctx, planesGamez, textures, playerStats, live, ctx.PlaneName,
                    playerPose.Position, playerPose.Position + playerPose.Forward, human: true,
                    pilot: null, FlightRoster.ShooterIdBase, AimAssist.PlayerTeam);

                var human = player;
                director.BuildRoster(new CampaignDirector.RosterInputs
                {
                    ChapterZrdrPath = SessionPaths.ChapterZrdr(ctx.DataRoot, BomberChapter),
                    MissionZrdrPath = missionZrdr,
                    ZrdrPath = ctx.ZrdrPath,
                    MinAiActiveDist = skills.MinAiActiveDist,
                    Player = () => human,
                    NetTrailers = new NetTrailerTargets(
                        () => human.WorldPosition,
                        name => world.Runtime.FindNodes(name) is { Count: > 0 } hits ? hits[0] : null),
                    FindNodes = name => world.Runtime.FindNodes(name),
                    // The session's own assembler, so the machine, the skills and the inert flag
                    // are the ones a played mission gets rather than the plan read back by hand.
                    Spawn = (plan, pos, look, pilot) =>
                    {
                        var rig = spawner!.SpawnAi(CampaignRosterPlan.SpawnFor(plan, pos, look, pilot));
                        rigs.Add(rig);
                        return rig;
                    },
                    Rng = new System.Random(1),
                });

                flight(director, rigs, live, report);
            }
            finally
            {
                player?.Free();
                var members = new List<FlightController>(
                    spawner?.AiAircraft ?? Array.Empty<FlightController>());
                spawner?.ClearMembership();
                foreach (var rig in members)
                {
                    rig.Free();
                }
                pool?.Free();
                textures.Dispose();
            }
        });

        return report;
    }

    // The decoded fork per block: wingman_1 escorts the player with no net; a wingman whose
    // primary_target is another block escorts THAT rig; a netted block patrols and never escorts;
    // a deactivated block is inert and out of play.
    private static void CheckFork(TestContext ctx, IReadOnlyDictionary<string, FlightController> roster,
        FlightController player, StringBuilder report)
    {
        int escorts = 0, netted = 0, inert = 0;
        foreach (var (name, rig) in roster)
        {
            var pilot = rig.Pilot;
            bool hasEscort = pilot?.Escort != null;
            bool hasNet = pilot?.Patrol != null;
            ctx.Check(!(hasEscort && hasNet), $"'{name}' never carries both an escort and a patrol net");
            escorts += hasEscort ? 1 : 0;
            netted += hasNet ? 1 : 0;
            inert += rig.Inert ? 1 : 0;
            report.AppendLine($"  {name}: escort={(hasEscort ? pilot!.Escort!.Leader?.Name.ToString() : "-")} " +
                              $"net={(hasNet ? pilot!.Patrol!.Net.Id.ToString() : "-")} inert={rig.Inert} team={rig.Team}");
        }
        report.AppendLine($"{escorts} escort(s), {netted} netted, {inert} inert");

        ctx.Check(roster.TryGetValue(CampaignDirector.WingmanName, out var wingman1),
            $"'{CampaignDirector.WingmanName}' is spawned");
        if (wingman1?.Pilot is { } w1)
        {
            ctx.Check(w1.Escort is { } e && ReferenceEquals(e.Leader, player),
                $"wingman_1 carries an escort whose leader is the player rig");
            ctx.Check(w1.Patrol == null, $"wingman_1 (netless mode wingman) carries no patrol net");
        }

        if (roster.TryGetValue("wingman_3", out var wingman3) && roster.TryGetValue("devastator_3", out var dev3))
        {
            ctx.Check(wingman3.Pilot?.Escort is { } e3 && ReferenceEquals(e3.Leader, dev3),
                $"wingman_3's primary_target names devastator_3 and resolves to that rig");
        }

        if (roster.TryGetValue("blakepeace_2_1", out var netBlock))
        {
            ctx.Check(netBlock.Pilot?.Patrol is { Net.Id: 15 },
                $"blakepeace_2_1 (netids 15) carries that net as its patrol: {netBlock.Pilot?.Patrol?.Net.Id}");
            ctx.Check(netBlock.Pilot?.Escort == null, $"…and no escort (a net demotes any wingman)");
        }

        if (roster.TryGetValue("blakebloodhawk_8", out var parked))
        {
            ctx.Check(parked.Inert && !parked.InPlay, $"blakebloodhawk_8 (deactivated 1) is inert and out of play");
        }
        ctx.Check(!roster.ContainsKey("blakepeace_2_2"),
            $"blakepeace_2_2 (an empty netids list on a jet) is not spawned: the original builds it dead");
        ctx.Check(escorts == 3, $"the three wingman blocks are the mission's three escorts: {escorts}");
        ctx.Check(inert == 9, $"the nine deactivated blocks are inert: {inert}");
    }

    // The four taxi-path vehicles are placed, frozen on their authored paths in the built world.
    private static void CheckPaths(TestContext ctx, CampaignDirector director, StringBuilder report)
    {
        var paths = director.Paths;
        ctx.Check(paths != null, $"the roster build created the scripted-path registry");
        if (paths == null)
        {
            return;
        }
        ctx.Same(4, paths.Count, $"the four blocks authoring a taxiPath are placed");
        ctx.Check(paths.IsFrozen("blakepeace_2_3"), $"blakepeace_2_3 (pp1) is frozen on its path until START_TAXI");
        report.AppendLine($"{paths.Count} vehicle(s) on a path");
    }

    // The volume order: a net's authored 700 m return radius reaches a block that authors none of
    // its own, and the activation radius is floored at min_ai_active_dist.
    private static void CheckVolumes(TestContext ctx, IReadOnlyDictionary<string, FlightController> roster,
        AiSkills skills, StringBuilder report)
    {
        if (roster.TryGetValue("devastator_2", out var dev2) && dev2.Pilot?.Machine is { } m)
        {
            report.AppendLine($"devastator_2 volumes: act={m.ActivationRange:0} att={m.AttackRange:0} ret={m.ReturnRange:0}");
            ctx.Check(Mathf.IsEqualApprox(m.ReturnRange, 700f),
                $"devastator_2 takes its net's authored 700 m return radius: {m.ReturnRange:0} m");
            ctx.Check(m.ActivationRange >= skills.MinAiActiveDist,
                $"…and its activation radius is floored at min_ai_active_dist: {m.ActivationRange:0} m");
        }
    }

    // The flown half: wingman_1 stays with the scripted player rig over two minutes, judged over
    // the second minute against the wingman-station suite's own leash.
    private static void FlyEscort(TestContext ctx, IReadOnlyDictionary<string, FlightController> roster,
        FlightController player, List<FlightController> rigs, ProjectilePool live,
        CampaignDirector director, StringBuilder report)
    {
        if (!roster.TryGetValue(CampaignDirector.WingmanName, out var wing) || wing.Pilot?.Escort is not { } escort)
        {
            return;
        }
        // The flown leg is lifted well clear of the chapter's terrain: the roster poses (110 m
        // at the player block) are the spawner's business, and under a collision world a
        // scripted leader at that height flies into the ground.
        var up = Vector3.Up * FlownLegLiftM;
        player.Activate(player.WorldPosition + up, player.WorldPosition + up + player.NoseDirection);
        wing.Activate(wing.WorldPosition + up, wing.WorldPosition + up + wing.NoseDirection);
        float worstLate = 0f, meanRange = 0f;
        int lateSamples = 0;
        bool joined = false;
        for (int i = 0; i < (int)(RunS / StepDt); i++)
        {
            live.SimStep(StepDt);
            foreach (var rig in rigs)
            {
                if (rig.InPlay)
                {
                    rig.SimStep(StepDt);
                }
            }
            director.Paths?.Step(StepDt);
            float range = wing.WorldPosition.DistanceTo(player.WorldPosition);
            joined |= escort.State == EscortState.Station;
            if (i % 1800 == 0)
            {
                ctx.Note($"t={i * StepDt:0}s range={range:0} escort={escort.State} mode={AiModeMachine.NameOf(wing.Pilot!.Machine!.Mode)} wingV={wing.WorldVelocity.Length():0} leadV={player.WorldVelocity.Length():0}");
            }
            if (i * StepDt >= HoldFromS)
            {
                lateSamples++;
                worstLate = Mathf.Max(worstLate, range);
                meanRange += range;
            }
        }
        meanRange /= Mathf.Max(1, lateSamples);
        report.AppendLine($"wingman_1 hold: mean {meanRange:0} m, worst {worstLate:0} m over the last minute, joined={joined}");
        ctx.Check(joined, $"wingman_1 joins the formation state on the player");
        ctx.Check(wing.InPlay && player.InPlay, $"both the player and wingman_1 are still flying after {RunS:0} s");
        ctx.Check(worstLate > 0f && worstLate < LeashM,
            $"wingman_1 stays with the player: worst {worstLate:0} m of {LeashM:0} over the last minute");
        ctx.Check(meanRange < MeanHoldM,
            $"…averaging inside {MeanHoldM:0} m of the player: {meanRange:0} m");
    }

    // The flown half: the three bombers over a minute of their shared net, with one hit part way.
    private static void FlyBombers(TestContext ctx, IReadOnlyDictionary<string, FlightController> roster,
        List<FlightController> rigs, ProjectilePool live, StringBuilder report)
    {
        var bombers = new List<FlightController>();
        foreach (string name in BomberBlocks)
        {
            if (roster.TryGetValue(name, out var rig))
            {
                bombers.Add(rig);
            }
        }
        ctx.Same(BomberBlocks.Length, bombers.Count, $"the three britbalmoral blocks all have a rig");
        if (bombers.Count != BomberBlocks.Length)
        {
            return;
        }
        foreach (var rig in bombers)
        {
            ctx.Check(rig.Pilot?.Patrol is { Net.Id: BomberNetId },
                $"{rig.Name} carries its authored net {BomberNetId}: {rig.Pilot?.Patrol?.Net.Id}");
        }

        float spawnSpread = Spread(bombers);
        report.AppendLine($"spawn spread {spawnSpread:0} m");
        // Each aircraft's own walk, appended when it steps. They are a hundred metres apart, so
        // they draw abeam a node frames apart; what must match is the route, not the frame.
        var routes = new List<List<int>> { new(), new(), new() };
        bool split = false, drifted = false, wasHit = false;
        float worstSpread = 0f;
        int fireFrame = (int)(BomberFireAtS / StepDt);
        for (int i = 0; i < (int)(BomberRunS / StepDt); i++)
        {
            live.SimStep(StepDt);
            foreach (var rig in rigs)
            {
                if (rig.InPlay)
                {
                    rig.SimStep(StepDt);
                }
            }
            if (i == fireFrame && bombers[1].Pilot?.Machine is { } hit)
            {
                // A certain steady-hand failure, which is the worst the roll can do: the reaction
                // runs rather than being rolled away, and the aircraft must still hold its net.
                // A bite that covers the whole pool, which the decoded roll fails outright.
                hit.NotifyDamage(0f, 20f, 0f, 20f);
                wasHit = hit.Mode != AiMode.Patrol || hit.Evading;
                report.AppendLine($"t={i * StepDt:0}s hit {bombers[1].Name}: mode={AiModeMachine.NameOf(hit.Mode)}");
            }
            int node = bombers[0].Pilot!.Patrol!.CurrentIndex;
            for (int b = 0; b < bombers.Count; b++)
            {
                var walk = bombers[b].Pilot!.Patrol!;
                var route = routes[b];
                if (route.Count == 0 || route[route.Count - 1] != walk.CurrentIndex)
                {
                    route.Add(walk.CurrentIndex);
                }
                // A lag of more than one node is a divergence rather than a straggler.
                split |= Math.Abs(walk.Advances - bombers[0].Pilot!.Patrol!.Advances) > 1;
            }
            float spread = Spread(bombers);
            worstSpread = Mathf.Max(worstSpread, spread);
            drifted |= spread > BomberSpreadCeilingM;
            if (i % 600 == 0)
            {
                string modes = $"{AiModeMachine.NameOf(bombers[0].Pilot!.Machine!.Mode)}/"
                    + $"{AiModeMachine.NameOf(bombers[1].Pilot!.Machine!.Mode)}/"
                    + $"{AiModeMachine.NameOf(bombers[2].Pilot!.Machine!.Mode)}";
                ctx.Note($"t={i * StepDt:0}s node={node} spread={spread:0} m modes={modes}");
            }
        }

        float finalSpread = Spread(bombers);
        for (int b = 0; b < routes.Count; b++)
        {
            report.AppendLine($"{bombers[b].Name} route: {string.Join(" ", routes[b])}");
        }
        report.AppendLine($"worst spread {worstSpread:0} m, final {finalSpread:0} m, split={split}");
        string route0 = string.Join(" ", routes[0]);
        ctx.Check(string.Join(" ", routes[1]) == route0 && string.Join(" ", routes[2]) == route0,
            $"all three walk the same nodes of their net: {route0}");
        ctx.Check(!split, $"…never more than one node apart over the whole {BomberRunS:0} s");
        ctx.Check(wasHit, $"the hit did run a reaction, so the held formation is not an unfired test");
        ctx.Check(!drifted,
            $"…and never spread past {BomberSpreadCeilingM:0} m of each other: worst {worstSpread:0} m");
        ctx.Check(finalSpread < BomberSpreadCeilingM,
            $"the one that was fired on is back with the other two: {finalSpread:0} m");
    }

    // The avoid-crash half, in a world that has colliders: the ray's own reach on the real rigs,
    // then the authored formation with the probe live, then a wing-mate held inside the lookahead
    // so the override has to arm on another netted aircraft rather than on terrain.
    private static void ProbeBombers(TestContext ctx, IReadOnlyDictionary<string, FlightController> roster,
        List<FlightController> rigs, ProjectilePool live, StringBuilder report)
    {
        var bombers = new List<FlightController>();
        foreach (string name in BomberBlocks)
        {
            if (roster.TryGetValue(name, out var rig))
            {
                bombers.Add(rig);
            }
        }
        ctx.Same(BomberBlocks.Length, bombers.Count, $"the three britbalmoral blocks all have a rig");
        if (bombers.Count != BomberBlocks.Length)
        {
            return;
        }

        var overrides = new List<string>();
        foreach (var rig in bombers)
        {
            ctx.Check(rig.Pilot?.Patrol is { Net.Id: BomberNetId },
                $"{rig.Name} carries its authored net {BomberNetId}: {rig.Pilot?.Patrol?.Net.Id}");
            if (rig.Pilot?.Machine is { } modes)
            {
                string tag = rig.Name;
                modes.ModeChanged += (from, to, why) =>
                {
                    if (to == AiMode.AvoidCrash || from == AiMode.AvoidCrash)
                    {
                        overrides.Add($"{tag}: {AiModeMachine.NameOf(from)}>{AiModeMachine.NameOf(to)} ({why})");
                    }
                };
            }
        }

        void Step(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                live.SimStep(StepDt);
                foreach (var rig in rigs)
                {
                    if (rig.InPlay)
                    {
                        rig.SimStep(StepDt);
                    }
                }
            }
        }

        // ⚠ Where each airframe's COLLIDER stands for the whole suite, since a flown aeroplane
        // leaves its own behind (docs/verification.md INSTR-13). The trail leg flies the pair back
        // to these poses rather than casting at where an aeroplane is drawn.
        var pinned = new List<(Vector3 Pos, float HeadingDeg)>();
        foreach (var rig in bombers)
        {
            pinned.Add((rig.WorldPosition, AiPilot.HeadingDegOf(rig.NoseDirection)));
        }

        Step(1); // the first pilot input, which is what wires each rig's own probe onto its machine
        var lead = bombers[0];
        var mate = bombers[1];
        string mateName = mate.Name;
        report.AppendLine($"lead y={lead.WorldPosition.Y:0} m v={lead.WorldVelocity.Length():0} m/s, spread {Spread(bombers):0} m, mate {lead.WorldPosition.DistanceTo(mate.WorldPosition):0} m off");
        ctx.Check(lead.Pilot?.Machine?.ProbeBlocked != null,
            $"{lead.Name} probes through its own rig's world-and-aircraft ray");
        string? ground = lead.AvoidCrashBlocksLine(
            lead.WorldPosition, lead.WorldPosition + (Vector3.Down * ProbeGroundRayM));
        ctx.Check(ground != null, $"the world under {lead.Name} carries colliders: {ground ?? "clear"}");
        string? atMate = lead.AvoidCrashBlocksLine(lead.WorldPosition, mate.WorldPosition);
        report.AppendLine($"probe ray at {mateName}: {atMate ?? "clear"}");
        ctx.Check(atMate != null && atMate.StartsWith(mateName, StringComparison.Ordinal),
            $"…and the probe ray sees the netted aircraft beside it: {atMate ?? "clear"}");
        ctx.Check(
            lead.AvoidCrashBlocksLine(
                lead.WorldPosition - (lead.NoseDirection * ProbeSelfRayM), lead.WorldPosition) == null,
            $"…and never the caster's own airframe, which the original deactivates before it casts");

        // The authored formation, its spacing measured and never widened to quiet a probe: how
        // close each aeroplane's own lookahead ray comes to a wing-mate's hulls. ⚠ Sampled on the
        // hulls rather than counted off the casts, since the colliders are pinned above.
        var routes = new List<List<int>> { new(), new(), new() };
        bool split = false;
        float worstSpread = 0f;
        float nearestOnRay = float.MaxValue;
        int wouldStrike = 0;
        for (int i = 0; i < (int)(BomberRunS / StepDt); i++)
        {
            Step(1);
            for (int b = 0; b < bombers.Count; b++)
            {
                var walk = bombers[b].Pilot!.Patrol!;
                var route = routes[b];
                if (route.Count == 0 || route[route.Count - 1] != walk.CurrentIndex)
                {
                    route.Add(walk.CurrentIndex);
                }
                split |= Math.Abs(walk.Advances - bombers[0].Pilot!.Patrol!.Advances) > 1;
            }
            worstSpread = Mathf.Max(worstSpread, Spread(bombers));
            if (i % ProbeSampleFrames == 0)
            {
                float onRay = NearestOnProbeRay(bombers);
                nearestOnRay = Mathf.Min(nearestOnRay, onRay);
                wouldStrike += onRay <= 0f ? 1 : 0;
            }
            if (i % 600 == 0)
            {
                ctx.Note($"t={i * StepDt:0}s node={bombers[0].Pilot!.Patrol!.CurrentIndex} spread={Spread(bombers):0} m nearest-on-ray={nearestOnRay:0} m");
            }
        }
        string route0 = string.Join(" ", routes[0]);
        for (int b = 0; b < routes.Count; b++)
        {
            report.AppendLine($"{bombers[b].Name} route: {string.Join(" ", routes[b])}");
        }
        report.AppendLine($"worst spread {worstSpread:0} m over {BomberRunS:0} s, nearest wing-mate hull on a probe ray {nearestOnRay:0} m, {wouldStrike} sample(s) on the ray, {overrides.Count} override(s)");
        ctx.Check(string.Join(" ", routes[1]) == route0 && string.Join(" ", routes[2]) == route0,
            $"all three walk the same nodes with the probe live: {route0}");
        ctx.Check(!split, $"…never more than one node apart over the whole {BomberRunS:0} s");
        ctx.Check(worstSpread < BomberSpreadCeilingM,
            $"…and never spread past {BomberSpreadCeilingM:0} m: worst {worstSpread:0} m");
        ctx.Check(wouldStrike <= ProbeOnRayCeiling,
            $"the authored formation is not flying nose to tail: a wing-mate's hull lies on another's lookahead ray on {wouldStrike} of {(int)(BomberRunS / StepDt / ProbeSampleFrames)} samples (nearest {nearestOnRay:0} m), ceiling {ProbeOnRayCeiling}");
        foreach (var rig in bombers)
        {
            string mode = AiModeMachine.NameOf(rig.Pilot!.Machine!.Mode);
            ctx.Check(rig.Pilot!.Machine!.Mode != AiMode.AvoidCrash,
                $"{rig.Name} is not left climbing out at the end of the leg: {mode}");
        }

        ProbeTrail(ctx, bombers, pinned[2], Step, report);
        report.AppendLine(overrides.Count == 0
            ? "no avoid-crash transition over the whole suite"
            : string.Join(System.Environment.NewLine, overrides));
    }

    // The forced half: two of the netted three put in trail, the geometry their own spacing
    // produces only now and then. Both are flown back to the target's pinned pose, so the aeroplane
    // the ray meets is the one the aeroplane is drawn at. Under test is the decoded cycle, where a
    // blocked ray takes the mode and names the aircraft it saw, the climb-out is flown, and one
    // clear ray hands the pilot back to its net.
    private static void ProbeTrail(TestContext ctx, List<FlightController> bombers,
        (Vector3 Pos, float HeadingDeg) pinned, Action<int> step, StringBuilder report)
    {
        var prober = bombers[1];
        var target = bombers[2];
        string targetName = target.Name;
        var machine = prober.Pilot!.Machine!;
        string? armedOn = null;
        machine.ModeChanged += (from, to, why) =>
        {
            if (to == AiMode.AvoidCrash)
            {
                armedOn = why;
            }
        };

        // A warp, not an Activate: it writes a pose and a velocity and nothing else, so both keep
        // the net seat an activation would reseat.
        var track = new Basis(Vector3.Up, Mathf.DegToRad(pinned.HeadingDeg)) * Vector3.Forward;
        void Pin(bool holdProber)
        {
            target.WarpTo(pinned.Pos, pinned.HeadingDeg, target.WorldVelocity.Length());
            if (holdProber)
            {
                prober.WarpTo(pinned.Pos - (track * ProbeTrailM), pinned.HeadingDeg,
                    prober.WorldVelocity.Length());
            }
        }

        int netBefore = prober.Pilot!.Patrol!.Net.Id;
        int armedAt = 0;
        for (; armedAt < (int)(ProbeArmS / StepDt) && machine.Mode != AiMode.AvoidCrash; armedAt++)
        {
            Pin(holdProber: true);
            step(1);
        }
        float yArmed = prober.WorldPosition.Y;
        string armedMode = AiModeMachine.NameOf(machine.Mode);
        report.AppendLine($"trail leg: {prober.Name} {ProbeTrailM:0} m astern of {targetName} armed after {armedAt * StepDt:0.00} s as {armedMode}, y={yArmed:0} m, climb-out={machine.ClimbOutAltitude:0} m, on '{armedOn}'");
        ctx.Check(machine.Mode == AiMode.AvoidCrash,
            $"a netted wing-mate {ProbeTrailM:0} m ahead arms the override: {armedMode}");
        ctx.Check(armedOn != null && armedOn.Contains(targetName, StringComparison.Ordinal),
            $"…on that aircraft, not on terrain: '{armedOn}'");
        ctx.Check(machine.ClimbOutAltitude > yArmed + (AiModeMachine.ClimbOutM * 0.9f),
            $"…ordering the decoded {AiModeMachine.ClimbOutM:0} m climb-out from {yArmed:0} m: {machine.ClimbOutAltitude:0} m");

        // Released from the hold, the prober flies its own climb-out. The aeroplane it saw stays
        // where it is, so what clears the line is the climb-out itself turning off it.
        int released = 0;
        for (; released < (int)(ProbeReleaseS / StepDt) && machine.Mode == AiMode.AvoidCrash; released++)
        {
            Pin(holdProber: false);
            step(1);
        }
        step(1); // one step on the released mode, so the pilot's own dispatch has flown the net
        string releasedTo = AiModeMachine.NameOf(machine.Mode);
        report.AppendLine($"…climbed to y={prober.WorldPosition.Y:0} m and released after {released * StepDt:0.0} s as {releasedTo}, patrol={prober.Pilot!.SteeringPatrol}");
        ctx.Check(prober.WorldPosition.Y > yArmed,
            $"{prober.Name} flies the climb-out: {prober.WorldPosition.Y:0} m from {yArmed:0} m");
        ctx.Check(machine.Mode != AiMode.AvoidCrash,
            $"…and one clear ray releases it inside {ProbeReleaseS:0} s: {released * StepDt:0.0} s");
        ctx.Check(prober.Pilot!.SteeringPatrol,
            $"…leaving {prober.Name} steering its net again rather than holding bare orders");
        ctx.Same(netBefore, prober.Pilot!.Patrol!.Net.Id,
            $"…the same net it was flying before the override");
    }

    // The closest any of the group's hulls comes to another's own avoid-crash lookahead ray,
    // metres, 0 when one lies across it. Measured on the collision hulls through the node
    // transform, which is the geometry the decoded ray meets in a played session; the bounding
    // sphere is a floor on that distance, so a pair it puts no nearer than the best pair so far
    // needs no hull walk at all.
    private static float NearestOnProbeRay(List<FlightController> group)
    {
        float nearest = float.MaxValue;
        foreach (var caster in group)
        {
            var pos = caster.WorldPosition;
            var velocity = caster.WorldVelocity;
            float speed = velocity.Length();
            float reach = Mathf.Max(speed * AiModeMachine.ProbeLookaheadS, AiModeMachine.ProbeMinLookaheadM);
            var ahead = pos + ((speed > 1e-3f ? velocity / speed : caster.NoseDirection) * reach);
            foreach (var other in group)
            {
                if (ReferenceEquals(other, caster) || other.Body is not { } body)
                {
                    continue;
                }
                float floor = SegmentDistanceTo(pos, ahead, body.GlobalPosition) - body.BoundRadius;
                nearest = floor >= nearest
                    ? nearest
                    : Mathf.Min(nearest, body.SegmentDistance(pos, ahead, out _, out _));
            }
        }
        return nearest;
    }

    // Distance from a point to a segment, metres.
    private static float SegmentDistanceTo(Vector3 from, Vector3 to, Vector3 point)
    {
        var span = to - from;
        float lengthSq = span.LengthSquared();
        if (lengthSq < 1e-6f)
        {
            return from.DistanceTo(point);
        }
        float t = Mathf.Clamp((point - from).Dot(span) / lengthSq, 0f, 1f);
        return (from + (span * t)).DistanceTo(point);
    }

    // The widest gap between any two of the group, metres.
    private static float Spread(List<FlightController> group)
    {
        float worst = 0f;
        for (int i = 0; i < group.Count; i++)
        {
            for (int j = i + 1; j < group.Count; j++)
            {
                worst = Mathf.Max(worst, group[i].WorldPosition.DistanceTo(group[j].WorldPosition));
            }
        }
        return worst;
    }

    private static (Vector3 Position, Vector3 Forward) PlayerPose(
        IReadOnlyList<(string Name, List<object?> Fields)> blocks)
    {
        foreach (var (name, fields) in blocks)
        {
            if (name.Equals(CampaignRosterPlan.PlayerBlock, StringComparison.OrdinalIgnoreCase)
                && AiSkills.RosterSpawnPose(fields) is { } pose)
            {
                return (pose.Position, new Basis(Vector3.Up, Mathf.DegToRad(pose.YawDeg)) * Vector3.Forward);
            }
        }
        return (new Vector3(0f, 500f, 0f), Vector3.Forward);
    }

    // The AI flavour of the plan's airframe; a def PlaneStats cannot resolve as a variant of that
    // airframe falls back to the plain base def, which is what the session's own spawner does.
    private static PlaneStats StatsFor(TestContext ctx, RosterSpawnPlan plan, StringBuilder report)
    {
        try
        {
            return PlaneStats.LoadForAi(ctx.ZrdrPath, plan.PlaneNode, plan.AiDef);
        }
        catch (ArgumentException e)
        {
            report.AppendLine($"  {plan.Name}: '{plan.AiDef}' not loadable as a variant of {plan.PlaneNode} ({e.Message}); base def used");
            return PlaneStats.LoadForAi(ctx.ZrdrPath, plan.PlaneNode);
        }
    }

    // One aircraft on the suite's own stage, the shape WingmanSuites.Rig builds: no camera, no
    // HUD, no devices.
    private static FlightController Rig(TestContext ctx, GameZ planesGamez, TextureArchive textures,
        PlaneStats stats, ProjectilePool live, string planeNode, Vector3 pos, Vector3 lookAt, bool human,
        AiPilot? pilot, int shooterId, int team)
    {
        var model = new PlaneBuilder(planesGamez, textures).Build(planeNode);
        var rig = new FlightController
        {
            PlaneModel = model,
            Collider = PlaneCollider.Build(model),
            Damage = stats.DestroyableParts.Count > 0 || stats.VehicleHealth is > 0f ? PlaneDamage.For(stats) : null,
            PlayerIndex = shooterId,
            IsHumanPiloted = human,
            Pilot = pilot,
            Projectiles = live,
            UseKeyboard = false,
            PadDevices = Array.Empty<int>(),
            AllowPause = false,
            Team = team,
        };
        rig.AddChild(model);
        if (human)
        {
            rig.Setup(new FlightModel(stats), null, new CamParams(), pos, lookAt, LeaderThrottle, LeaderSpeedMps);
        }
        else
        {
            rig.Setup(new FlightModel(stats, aiForcePath: true), null, new CamParams(), pos, lookAt);
        }
        rig.Name = $"{planeNode}_{shooterId}";
        ctx.Host.AddChild(rig);
        return rig;
    }

    private static string Bias(AiRatingBias? entry) =>
        entry?.Bias.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    private static string Rating(int? slot) =>
        slot?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unset";

    // One planned block spawned on its own, far from the others, so the rig under inspection is
    // the assembler's whole answer for that block and nothing else has touched it.
    private static FlightController SpawnPlan(FlightRoster roster, RosterSpawnPlan plan, float offsetM)
    {
        var at = new Vector3(offsetM, 900f, 0f);
        return roster.SpawnAi(CampaignRosterPlan.SpawnFor(
            plan, at, at + Vector3.Forward, AiPilot.HoldingCourse(at, at + Vector3.Forward)));
    }

    private static void Advance(CampaignDirector director, float seconds)
    {
        for (float t = 0f; t < seconds; t += 0.1f)
        {
            director.Step(0.1f);
        }
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

    // The two candidates' ranks with and without the authored list, computed on the same inputs
    // the live acquisition builds, so the artifact carries the numbers behind each pick.
    private static void ReportRanks(TestContext ctx, StringBuilder report, Vector3 origin,
        Vector3 fwd, string arm, (FlightController Rig, float Ring) a, (FlightController Rig, float Ring) b,
        IReadOnlyList<AiRatingBias> biases)
    {
        report.AppendLine($"-- {arm} --");
        foreach (var (rig, ring) in new[] { a, b })
        {
            string key = rig.IsHumanPiloted ? AiTargetRanking.PlayerRole : rig.Name.ToString();
            var candidate = new RankedTargetCandidate
            {
                Position = origin + (fwd * ring),
                Velocity = fwd * 60f,
                IsPlayer = rig.IsHumanPiloted,
                ObjectiveBias = 0f,
            };
            float plainRank = AiTargetRanking.Score(origin, fwd, ScanRangeM, AiScorer.Jet,
                candidate).Rank;
            candidate.ObjectiveBias = AiTargetRanking.ObjectiveBiasFor(key, biases);
            float biasedRank = AiTargetRanking.Score(origin, fwd, ScanRangeM, AiScorer.Jet,
                candidate).Rank;
            report.AppendLine($"  {rig.Name} as '{key}' at {ring:0} m: rank {plainRank:0.#} -> "
                              + $"{biasedRank:0.#} (bias term {candidate.ObjectiveBias:0.#})");
        }
        ctx.Note($"{arm}: ranks written to the artifact");
    }
}
