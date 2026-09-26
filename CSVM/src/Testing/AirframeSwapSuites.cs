using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CSVM.Flight.Ai;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Hangar;
using CSVM.Flight.Modes;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The suite over the mission-script host's airframe swap, callback codes 965 to 967: the
/// code CM02's own capture animation authors, driven against that mission's built world, with the
/// player's rig coming off the session's own roster so what is swapped is the flown aircraft.</summary>
internal static class AirframeSwapSuites
{
    // CM02's story position, and the airframe the swap is driven FROM. A Bloodhawk is the campaign's
    // own starting aircraft and shares none of the Balmoral's fit, so every reading below separates
    // the two airframes rather than reading the same numbers twice.
    private const int Cm02Seq = 1;
    private const string StartPlane = "player_bhawk";

    // CM07's story position, the code its hangar drop raises, and the airframe the drop is flown
    // in on: the campaign's own starting Devastator, so the Bloodhawk it hands over is a change.
    private const int Cm07Seq = 6;
    private const int HangarSwapCode = 965;
    private const string HangarStartPlane = "player_pfighter";

    private const string DirectionSensor = "hdrop_direction";

    // Where the rig is flown from, and how close the replacement has to land to it. The tolerances
    // are a swap's, not a simulation's: the aircraft is rebuilt at the pose the outgoing one held,
    // so the two readings differ only by the spawn placement's own rounding.
    private const float PoseTolerance = 1f;
    private const float SpeedTolerance = 2f;

    // The hand-over's own tolerances. The placement is arithmetic off one pose, so metres and
    // degrees are generous; the pools are floats carried through two divisions.
    private const float RangeTolerance = 1f;
    private const float BearingTolerance = 1f;
    private const float FractionTolerance = 0.02f;
    private const float PoolTolerance = 0.5f;

    // The played leg's frame. Long enough to outlast the wing walk's own 19.25 s motion, which is
    // what the capture's called definition raises its reveal behind.
    private const float StepDt = 1f / 60f;
    private const float PlayBudgetS = 30f;

    // The approach the drop is reached over, the way a flown session reaches it: the rig is bound
    // at the world build and the mission's own start anims are then run out with the player clear
    // of the drop's band, since what those anims leave the staged `player` marker in is exactly
    // what a suite binding at the trigger cannot see.
    private const float AwayM = 400f;
    private const float ArmBudgetS = 4f;
    private const float EarlierBudgetS = 90f;

    // How far from the hangar anchor the pilot may be handed back and still be at the doors. Wide,
    // because the marker the drop leaves them on is the lift's own end and not the anchor: what
    // this separates is the doors from the world origin 11 km away.
    private const float HangarDoorsM = 500f;

    // The Ammo Selection picks the sortie is flown on. Neither is any airframe's stock fit, so a
    // rebuilt rig still carrying one is unambiguous; every slot is picked, since the three codes
    // hand over airframes with two, six and eight pylons.
    private const string SortiePylon = "wep_07";
    private const string SortieAmmo = "magnesium";

    // What every swap case writes over the handed-over airframe's slots: slug guns and the
    // high-explosive rocket on every pylon, which is each def's own stock fit.
    private const string HandedPylon = Loadout.StockOrdnance;
    private const string HandedAmmo = "slug";

    // The drop's fork: each pair is one camera leg and its twin, told apart by the direction
    // sensor's active state alone; the tail runs after the swap with no prerequisite.
    private static readonly (string Leg, string Twin)[] HangarLegs =
    {
        ("hdplayer1", "hdplayer1b"),
        ("hdchute1", "hdchute1b"),
    };

    private static readonly string[] HangarTail = { "hdplayer2", "hdplayer3" };

    // The Bloodhawk's shipped skin textures the unpainted rebuild draws: wing and fin carry the
    // yellow-olive stripe, the fuselage top the blue-grey body.
    private static readonly string[] BloodhawkSkins = { "blo_wing", "blo_fin", "blo_fusalagetop" };

    // The other two codes, driven past 965 on the same rig so the whole table is read rather than
    // the one case the reported sortie flew.
    private static readonly int[] OtherSwapCodes = { 966, 967 };

    /// <summary>Drives the swap CM02 authors against CM02's own built world: the code comes out of
    /// the mission's compiled definitions, the rig off the session's roster, and the replacement is
    /// read for the named airframe's own stock fit, hardpoint table and armour rather than the
    /// airframe it replaced.</summary>
    // BL-494: callback codes 965 to 967, which put the player in a different airframe mid
    // mission and reached nothing until the roster grew a swap.
    [Suite("campaign-airframe-swap",
        "the mission-script host's airframe swap over CM02's BUILT world: the three decoded "
        + "codes name the defs the shipped stat rows carry, the mission's own capture "
        + "definition authors the Balmoral one, and driving that code through the host "
        + "rebuilds the player's rig on the named airframe at the pose, heading and speed it "
        + "was flying, with that airframe's stock hardpoint table at full ammunition and its "
        + "own armour pools and damage zones rather than the airframe it replaced, the "
        + "outgoing aircraft out of the world with no registration of its own left in the "
        + "projectile pool, and the cutscene flags the code sets landing on the aircraft the "
        + "swap built; plus the two things 967 does past that rebuild, on the same data: the "
        + "capture definition's own aircraft hidden with what is left of its hull carried onto "
        + "the player's, and the outgoing aeroplane handed to wingman_4 -- authored "
        + "deactivated, flying the player's own airframe in this mission and its own def's "
        + "everywhere else, revealed 100 m off the old nose at -45 degrees on the player's own "
        + "heading with the sums measured off the hull it was given")]
    internal static void AirframeSwap(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), Cm02Seq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {Cm02Seq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder), $"{chapter}/{folder} zrdr");

        var report = new StringBuilder();
        report.AppendLine($"seq {Cm02Seq} -> {chapter}/{folder}");
        CheckTable(ctx, report);
        CheckHandoverPlan(ctx, mission, report);
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(chapter, collision: false, folder,
                world => Drive(ctx, world, chapter, folder, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-airframe-swap-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"drove the swap {chapter}/{folder} authors against that mission's own world");
    }

    /// <summary>Plays CM07's hangar drop over that mission's built world on a realtime clock: the
    /// drop's two camera legs fork on <c>hdrop_direction</c>'s state, so exactly one of each pair
    /// starts, and the 965 it raises rebuilds the player on the Blue Streak build in its shipped
    /// skins rather than on a stock Bloodhawk in the pilot's own paint; the flown aeroplane is
    /// drawn on the staged <c>player</c> marker through the lift leg, with the undercarriage.</summary>
    // BL-574/BL-575: the hangar hand-over gave a stock Bloodhawk in the pilot's own paint, and
    // both camera legs of the drop ran at once because their node-state prerequisite was
    // parsed away.
    [Suite("campaign-hangar-handover",
        "CM07's hangar drop over that mission's BUILT world, played through the runtime on a "
        + "realtime clock: each camera leg and its twin carry opposite node-state "
        + "prerequisites on hdrop_direction, exactly one of each pair starts and it is the one "
        + "the sensor's state picks, the two legs after the hand-over start, and the 965 the "
        + "drop raises rebuilds the player on the Blue Streak build the special-plane template "
        + "carries -- twin 40 over twin 30, one pylon a wing, 20 armour a zone, the nitrous "
        + "injector -- in its shipped blo_* skins with no scheme over them (the blue-grey "
        + "body and yellow wingtips of the original), while a plain swap onto the same node "
        + "stays a stock Bloodhawk with no injector; the sortie is flown with an ammo-screen "
        + "choice on every slot and the aeroplane flown in carries it, but every one of the "
        + "three codes rebuilds onto slug guns and wep_06 pylons, the fit its own case writes; "
        + "the flown aeroplane rides the drop in "
        + "view: the hangar-floor Bloodhawk prop shows for the first leg and goes at the "
        + "swap, and after it the rig is drawn on the player marker, wearing the staged "
        + "undercarriage, as the lift leg moves that marker, and the parachutist the drop's "
        + "own site-less chute call animates is the one staged figure, drawn for the whole "
        + "leg, hanging under the actor the stage left where it stands and coming down at the "
        + "hangar rather than kilometres off it")]
    internal static void HangarHandover(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(ctx.PlanesGamezPath, $"planes gamez");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), Cm07Seq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {Cm07Seq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");
        ctx.RequireData(SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder), $"{chapter}/{folder} zrdr");

        var report = new StringBuilder();
        report.AppendLine($"seq {Cm07Seq} -> {chapter}/{folder}");
        CheckBlueStreakTemplate(ctx, report);
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(chapter, collision: false, folder,
                world => DriveHangar(ctx, world, chapter, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"test-hangar-handover-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"played the hangar drop {chapter}/{folder} authors against that mission's own world");
    }

    // What 965's case writes by hand, read off the code table and the award template it names:
    // the two have to agree, or the hangar would hand over one Blue Streak and the debrief award
    // another.
    private static void CheckBlueStreakTemplate(TestContext ctx, StringBuilder report)
    {
        var code = AirframeSwapCodes.For(HangarSwapCode);
        ctx.Check(code is { AwardAirframe: AirframeSwapCodes.BlueStreakAirframe },
            $"code {HangarSwapCode} names the Blue Streak's airframe template, the build its own case writes over the stock Bloodhawk");
        ctx.Check(AirframeSwapCodes.For(966) is { AwardAirframe: null }
                  && AirframeSwapCodes.For(967) is { AwardAirframe: null },
            $"and the other two codes hand over the stock fit, the way their cases do");
        var build = CampaignProgression.AwardBuild(AirframeSwapCodes.BlueStreakAirframe);
        ctx.Check(build != null, $"the special-plane table carries that template");
        if (build == null)
        {
            return;
        }

        report.AppendLine($"template: engine {build.Engine}, pylons {build.LeftHardpoints}/{build.RightHardpoints}, " +
            $"armour {build.ArmourNose}/{build.ArmourTail}/{build.ArmourLeftWing}/{build.ArmourRightWing}, " +
            $"guns {string.Join(",", build.Guns.Select(g => g.Calibre is { } c ? $"{30 + (10 * c)}{(g.Twin ? "x2" : "")}" : "-"))}");
        ctx.Check(CustomPlaneBuild.HasNitrous(build),
            $"whose engine {build.Engine} is a nitrous tier, the injector 965 alone sets");
        ctx.Check(build.LeftHardpoints == 1 && build.RightHardpoints == 1,
            $"with one pylon a wing, the two hardpoints of six the case writes");
        ctx.Check(build.Guns[0] is { Calibre: 1, Twin: true } && build.Guns[1] is { Calibre: 0, Twin: true },
            $"and a twin 40 over a twin 30, the 40/30 gun table with both twin bytes set");
        ctx.Check(build.ArmourNose * CustomPlaneBuild.ArmourUnitScale == 20
                  && build.ArmourRightWing * CustomPlaneBuild.ArmourUnitScale == 20,
            $"at 20 armour a zone, the one number the case writes to all four sections");
    }

    private static void DriveHangar(TestContext ctx, TestWorld world, string chapter, StringBuilder report)
    {
        (string Anim, int Code, string Root)? drop = null;
        foreach (var call in SwapCallsIn(world.Session.Program))
        {
            if (call.Code == HangarSwapCode)
            {
                drop = call;
            }
        }

        ctx.Check(drop != null, $"the mission's compiled program authors callback {HangarSwapCode}");
        if (drop == null)
        {
            return;
        }

        report.AppendLine($"'{drop.Value.Root}-{drop.Value.Anim}' authors callback {drop.Value.Code}");
        CheckLegPrerequisites(ctx, world, report);
        CheckChuteCall(ctx, world, drop.Value.Anim, report);
        WithStagedHangar(ctx, world, chapter,
            staged => PlayTheDrop(ctx, world, staged, drop.Value, report));
    }

    // The fork as the data authors it: each camera leg is gated on the direction sensor, one on
    // it active and its twin on it inactive, so the runtime has something to read.
    private static void CheckLegPrerequisites(TestContext ctx, TestWorld world, StringBuilder report)
    {
        foreach (var (leg, twin) in HangarLegs)
        {
            var legDef = First(world.Session.Program.ByAnimName(leg));
            var twinDef = First(world.Session.Program.ByAnimName(twin));
            ctx.Check(legDef != null && twinDef != null, $"'{leg}' and '{twin}' are both defined for this mission");
            if (legDef == null || twinDef == null)
            {
                continue;
            }

            report.AppendLine($"'{leg}' prerequisites: {Describe(legDef.PrereqNodes)}; '{twin}': {Describe(twinDef.PrereqNodes)}");
            ctx.Check(legDef.PrereqNodes.Count == 1 && legDef.PrereqNodes[0] is { Required: true, Active: false }
                      && string.Equals(legDef.PrereqNodes[0].Path[^1], DirectionSensor, StringComparison.OrdinalIgnoreCase),
                $"'{leg}' requires '{DirectionSensor}' inactive");
            ctx.Check(twinDef.PrereqNodes.Count == 1 && twinDef.PrereqNodes[0] is { Required: true, Active: true }
                      && string.Equals(twinDef.PrereqNodes[0].Path[^1], DirectionSensor, StringComparison.OrdinalIgnoreCase),
                $"and '{twin}' requires it active, so the two legs cannot both run");
        }
    }

    // The drop's chute leg as the data authors it: a CALL_ANIMATION with no site at all, onto a
    // definition rooted on the staged parachutist. That pairing is the whole reason the figure has
    // to stay where it was staged, so the suite reads it rather than assuming it.
    private static void CheckChuteCall(TestContext ctx, TestWorld world, string dropAnim,
        StringBuilder report)
    {
        var caller = First(world.Session.Program.ByAnimName(dropAnim));
        var leg = First(world.Session.Program.ByAnimName(HangarChute.ChuteLeg));
        ctx.Check(leg != null && string.Equals(leg.RootName, AircraftStage.ChuteNode, StringComparison.OrdinalIgnoreCase),
            $"'{HangarChute.ChuteLeg}' is rooted on the staged '{AircraftStage.ChuteNode}' the aircraft archive supplies");
        bool sited = true;
        bool called = false;
        foreach (var seq in caller?.Sequences ?? new List<AnimSequence>())
        {
            foreach (var ev in seq.Events)
            {
                if (ev.Kind != "CallAnimation"
                    || !string.Equals(ev.Data.Str("name"), HangarChute.ChuteLeg, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                called = true;
                sited = ev.Data.Obj("parameters") != null || ev.Data.Str("operand_node") != null;
            }
        }

        report.AppendLine($"'{dropAnim}' calls '{HangarChute.ChuteLeg}': called={called} sited={sited}");
        ctx.Check(called && !sited,
            $"and '{dropAnim}' calls it with no AT_NODE site of its own, so the call asks for the figure where it stands rather than at a placement");
    }

    private static string Describe(IReadOnlyList<AnimNodePrereq> prereqs) =>
        prereqs.Count == 0 ? "none"
            : string.Join(", ", prereqs.Select(p => $"{string.Join("/", p.Path)}={(p.Active ? "active" : "inactive")}{(p.Required ? "" : " (optional)")}"));

    // The drop as a flown session runs it, and not as a suite can most cheaply reach it: the host
    // is bound at the world build the way GameSession binds it, the mission's own start anims are
    // then run out with the player clear of the band, and the definition is armed by its own caller
    // and started by the player reaching the hangar. Every callee start is recorded.
    private static void PlayTheDrop(TestContext ctx, TestWorld world, HangarStaged staged,
        (string Anim, int Code, string Root) drop, StringBuilder report)
    {
        var (roster, rig, before, cutscene, textures) = staged;
        var savedClock = GameClock.Current;
        var savedHost = world.Runtime.CallbackHost;
        var savedStarted = world.Runtime.OnInstanceStarted;
        var savedPositions = world.Runtime.PlayerPositions;
        var started = new List<string>();
        var aircraft = world.Session.Aircraft;
        ctx.Check(aircraft is { PlayerMarker: not null },
            $"the mission's world stages the aircraft archive's '{AircraftStage.PlayerNode}' marker, the node the drop poses the flown aeroplane on");
        var ride = new HangarRide(aircraft);
        var chute = new HangarChute(aircraft);
        try
        {
            var clock = new GameClock { Mode = GameClock.RunMode.Realtime };
            GameClock.Current = clock;
            // The session's own order: the world's registration, then the rigs, at the build and
            // before the mission has stepped. A bind at the trigger cannot see what the start anims
            // do to the marker in between.
            cutscene.BindWorld(world.Runtime, aircraft);
            cutscene.HostDefinitions(world.Session.LandingCutsceneAnims);
            cutscene.BindRigs(new[] { rig }, () => roster.AiAircraft);
            cutscene.SwapAirframe = order => roster.RunSwap(rig, order, handsOver: false);
            world.Runtime.CallbackHost = cutscene.Host;
            world.Runtime.OnInstanceStarted = (def, anchor) =>
            {
                savedStarted?.Invoke(def, anchor);
                if (def.AnimName is { } name)
                {
                    started.Add(name);
                }
            };
            bool sensorActive = SensorActive(world);
            report.AppendLine($"'{DirectionSensor}' active={sensorActive} before the drop");
            FlyTheEarlierCutscenes(ctx, world, cutscene, clock, rig, drop.Anim, report);
            if (FlyTheApproach(ctx, world, rig, drop.Anim, started, report) is not { } hangar)
            {
                return;
            }

            started.Clear();

            bool swapped = false;
            float swappedAtS = -1f;
            for (int i = 0; i < (int)(PlayBudgetS / StepDt); i++)
            {
                clock.BeginFrame(StepDt);
                world.Runtime.Advance(StepDt);
                cutscene.Tick();
                rig.Controller?._PhysicsProcess(StepDt);
                if (!swapped && !ReferenceEquals(rig.Controller, before))
                {
                    swapped = true;
                    swappedAtS = i * StepDt;
                }

                bool liftLeg = started.Contains(HangarRide.LiftLeg, StringComparer.OrdinalIgnoreCase)
                    && !started.Contains(HangarRide.AfterLift, StringComparer.OrdinalIgnoreCase);
                ride.Sample(rig.Controller, swapped, cutscene.Playing && liftLeg, cutscene.Playing);
                chute.Sample(world,
                    started.Contains(HangarChute.ChuteLeg, StringComparer.OrdinalIgnoreCase)
                    && cutscene.Playing);
            }

            report.AppendLine($"played '{drop.Anim}' for {PlayBudgetS:0} s: codes {string.Join(",", cutscene.Codes)}, " +
                $"swapped={swapped} at {swappedAtS:0.##} s, started [{string.Join(",", started)}]");
            CheckLegs(ctx, started, sensorActive, report);
            ctx.Check(swapped, $"playing '{drop.Anim}' reaches callback {drop.Code} through the runtime");
            var after = rig.Controller;
            ctx.Check(after != null, $"and the swap built an aircraft");
            if (after == null)
            {
                return;
            }

            report.AppendLine(Describe("before", before));
            report.AppendLine(Describe("after", after));
            CheckBlueStreakFit(ctx, before, after, textures, report);
            CheckRide(ctx, ride, cutscene, hangar, report);
            CheckChute(ctx, chute, hangar, report);
            CheckStockStaysStock(ctx, roster, rig, report);
            CheckOtherCodesDropIt(ctx, roster, rig, report);
        }
        finally
        {
            world.Runtime.PlayerPositions = savedPositions;
            world.Runtime.OnInstanceStarted = savedStarted;
            world.Runtime.CallbackHost = savedHost;
            GameClock.Current = savedClock;
        }
    }

    // What a flown CM07 has already played by the time it reaches the hangar: the mission's other
    // landing-trigger cutscenes, each run as its own episode, ending in the host's own handoff. They
    // pose the same staged `player` marker the drop does, and one of them parents it under a moving
    // train, so what they leave behind is the state the drop starts from.
    private static void FlyTheEarlierCutscenes(TestContext ctx, TestWorld world,
        CutsceneController cutscene, GameClock clock, PlayerRig rig, string dropAnim,
        StringBuilder report)
    {
        var marker = world.Session.Aircraft?.PlayerMarker;
        foreach (var row in world.Session.Landings)
        {
            if (row.Auto || string.Equals(row.Anim, dropAnim, StringComparison.OrdinalIgnoreCase)
                || cutscene.Playing)
            {
                continue;
            }

            cutscene.Own(row.Anim);
            world.Runtime.PlayMissionTrigger(row.Anim);
            for (float t = 0f; t < EarlierBudgetS; t += StepDt)
            {
                clock.BeginFrame(StepDt);
                world.Runtime.Advance(StepDt);
                cutscene.Tick();
                rig.Controller?._PhysicsProcess(StepDt);
                if (t > StepDt && !cutscene.Playing)
                {
                    break;
                }
            }

            string parent = marker?.GetParent() is Node3D under ? AnimRuntime.NameOf(under) : "-";
            report.AppendLine($"earlier cutscene '{row.Anim}' handed off={!cutscene.Playing}; " +
                $"'{AircraftStage.PlayerNode}' is under '{parent}' at " +
                $"{(marker != null ? AnimRuntime.WorldTransform(marker, out _).Origin.ToString() : "-")}");
            ctx.Check(!cutscene.Playing, $"the mission's earlier '{row.Anim}' episode plays out and hands off");
            ctx.Check(marker != null && ReferenceEquals(marker.GetParent(), world.Runtime.WorldRoot),
                $"and leaves the staged '{AircraftStage.PlayerNode}' marker back under the world root rather than on the '{parent}' its own composition parented it to, so the next episode poses the flown aeroplane in the world's frame and not in that node's");
        }
    }

    // The approach the session flies in on. The drop is a range-gated definition: a call only arms
    // it and the player reaching its anchor is what runs it, so this parks the rig outside the band
    // while the mission's start anims run, then flies it onto the hangar. Answers false when the
    // world cannot offer the drop at all, which is a failure already recorded.
    private static Vector3? FlyTheApproach(TestContext ctx, TestWorld world, PlayerRig rig,
        string dropAnim, List<string> started, StringBuilder report)
    {
        var anchors = world.Runtime.AnchorsOf(First(world.Session.Program.ByAnimName(dropAnim))
            ?? throw new InvalidOperationException($"'{dropAnim}' is not in the mission's program"));
        var anchor = anchors.Count > 0 ? anchors[0] : null;
        ctx.Check(anchor != null, $"'{dropAnim}' resolves the authored anchor its range gate measures from");
        if (anchor == null || rig.Controller is not { } plane)
        {
            return null;
        }

        var site = AnimRuntime.VisualOriginOf(anchor);
        var away = site + (Vector3.Right * AwayM);
        // The approach is a probe position rather than the rig's own: an aeroplane flown at the
        // hangar covers the band inside the arming budget, and what this leg is about is the state
        // the start anims leave behind, not the flight.
        var probe = away;
        world.Runtime.PlayerPositions = () => new[] { probe };
        plane.Activate(away, away + Vector3.Right);
        string? caller = CallerOf(world, dropAnim);
        ctx.Check(caller != null, $"an ambient definition calls '{dropAnim}', which is the only thing that arms it");
        if (caller != null)
        {
            world.Runtime.Play(caller);
        }

        for (float t = 0f; t < ArmBudgetS; t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            plane._PhysicsProcess(StepDt);
        }

        report.AppendLine($"armed from '{caller ?? "-"}' {AwayM:0} m out: '{dropAnim}' state=" +
            $"{world.Runtime.AnimStateOf(dropAnim)}, marker visible=" +
            $"{world.Session.Aircraft?.PlayerMarker?.Visible.ToString() ?? "-"} after the start anims");
        ctx.Check(!started.Contains(dropAnim, StringComparer.OrdinalIgnoreCase),
            $"'{caller ?? "-"}' arms '{dropAnim}' without running it, the player being outside its band");
        ctx.Check(world.Session.Aircraft?.PlayerMarker is { Visible: true },
            $"and the mission's own start anims leave the '{AircraftStage.PlayerNode}' marker ACTIVE, since a marker switched off holds the pilot undrawn for the whole drop");

        // Onto the hangar, where the range gate runs the armed definition on the next advance.
        probe = site;
        rig.Controller?.Activate(site, site + (Vector3.Forward * 100f));
        return site;
    }

    // The definition whose CALL_ANIMATION arms the named one.
    private static string? CallerOf(TestWorld world, string called)
    {
        foreach (var def in world.Session.Program.Defs)
        {
            foreach (var seq in def.Sequences)
            {
                foreach (var ev in seq.Events)
                {
                    if (ev.Kind == "CallAnimation" && ev.Data.Str("name") is { } name
                        && name.Equals(called, StringComparison.OrdinalIgnoreCase)
                        && def.AnimName is { Length: > 0 } caller)
                    {
                        return caller;
                    }
                }
            }
        }

        return null;
    }

    // Exactly one leg of each pair, the one whose prerequisite the sensor's state satisfies, plus
    // the two legs after the swap that carry no prerequisite at all.
    private static void CheckLegs(TestContext ctx, IReadOnlyList<string> started, bool sensorActive,
        StringBuilder report)
    {
        foreach (var (leg, twin) in HangarLegs)
        {
            bool legRan = started.Contains(leg, StringComparer.OrdinalIgnoreCase);
            bool twinRan = started.Contains(twin, StringComparer.OrdinalIgnoreCase);
            ctx.Check(legRan != twinRan,
                $"exactly one of '{leg}' and '{twin}' starts (leg={legRan}, twin={twinRan}), the fork the direction sensor authors");
            ctx.Check(twinRan == sensorActive,
                $"and it is the one the sensor's state picks ('{DirectionSensor}' active={sensorActive})");
        }

        foreach (var name in HangarTail)
        {
            ctx.Check(started.Contains(name, StringComparer.OrdinalIgnoreCase),
                $"'{name}' starts, the drop's choreography after the hand-over");
        }
    }

    // What 965 hands the player: the Blue Streak build in its shipped skins, not the stock
    // Bloodhawk in the pilot's own paint.
    private static void CheckBlueStreakFit(TestContext ctx, FlightController before, FlightController after,
        TextureArchive textures, StringBuilder report)
    {
        var wanted = AirframeSwapCodes.For(HangarSwapCode)!.Value;
        ctx.Check(after.Loadout is { } fit && string.Equals(fit.Def.Def, wanted.Def, StringComparison.OrdinalIgnoreCase),
            $"the player is flying '{wanted.Def}'");
        ctx.Check(!before.Nitro.Installed && after.Nitro.Installed,
            $"with the nitrous injector the aircraft flown in did not have");
        var guns = after.Loadout?.Def.Guns ?? new List<GunSpec>();
        report.AppendLine($"guns: {string.Join(", ", guns.Select(g => $"{g.Caliber} cal x{g.Markers.Count}"))}; " +
            $"hardpoints {after.Loadout?.Hardpoints.Count ?? 0}; " +
            $"armour {string.Join("/", (after.Damage?.Parts.Values ?? Enumerable.Empty<PlaneDamage.PartState>()).Select(p => p.Def.MaxArmor.ToString("0")))}; " +
            $"livery '{after.Scheme?.Label ?? "-"}' painted={after.Painter != null}");
        ctx.Check(guns.Count == 2 && guns[0].Caliber == 40 && guns[1].Caliber == 30,
            $"a 40 over a 30, the gun table 965 writes");
        ctx.Check(guns.Count == 2 && guns[0].Markers.Count == 2 && guns[1].Markers.Count == 2,
            $"both twinned");
        ctx.Check(after.Loadout?.Hardpoints.Count == 2,
            $"two pylons, one a wing, rather than the stock Bloodhawk's own table");
        CheckSortieFitDropped(ctx, before, after, report);
        ctx.Check(after.Damage != null && after.Damage.Parts.Count == 4
                  && after.Damage.Parts.Values.All(p => Mathf.IsEqualApprox(p.Def.MaxArmor, 20f)),
            $"and 20 armour on each of the four zones");
        ctx.Check(wanted.ShippedSkins && after.ShippedSkins && after.Scheme == null && after.Painter == null,
            $"drawn in the airframe's shipped skins with no scheme composited over them, the state the original leaves the Blue Streak's textures in");
        ctx.Check(before.Scheme != null || before.Painter != null,
            $"where the aircraft flown in wore the pilot's paint ('{before.Scheme?.Label ?? "-"}')");
        var onModel = after.PlaneModel is { } model ? SurfaceTextures(model) : new HashSet<Texture2D>();
        var found = new List<string>();
        foreach (string skin in BloodhawkSkins)
        {
            // The archive caches one texture per name, so the instance a surface samples IS the
            // one Find answers, and the shipped skin being drawn is a reference test.
            if (textures.Find(skin) is { } archived && onModel.Contains(archived))
            {
                found.Add(skin);
            }
        }

        report.AppendLine($"shipped skins on the model: [{string.Join(",", found)}] of [{string.Join(",", BloodhawkSkins)}], " +
            $"{onModel.Count} distinct texture(s) sampled");
        ctx.Check(found.Count == BloodhawkSkins.Length,
            $"and the model's surfaces sample the archive's own {string.Join("/", BloodhawkSkins)} (the blue-grey body and the yellow-olive wingtip stripe), so the skins the original shows are the ones loaded");
    }

    // The sortie's Ammo Selection picks stop at the aeroplane the pilot leaves. Each case writes
    // the handed-over airframe's own weapon ids over every slot, so the rebuilt rig flies slug
    // guns and high-explosive rockets whatever the pilot bought for the sortie.
    private static void CheckSortieFitDropped(TestContext ctx, FlightController before,
        FlightController after, StringBuilder report)
    {
        report.AppendLine($"sortie fit: flown in {DescribeFit(before)}; rebuilt {DescribeFit(after)}");
        ctx.Check(Carries(before, SortiePylon, SortieAmmo),
            $"the aeroplane the drop is flown in carries the sortie's own picks ('{SortiePylon}' on every pylon, '{SortieAmmo}' in every gun)");
        ctx.Check(Carries(after, HandedPylon, HandedAmmo),
            $"and the Blue Streak the drop hands over carries '{HandedPylon}' on both pylons and '{HandedAmmo}' in both guns, the fit 965's own case writes, rather than what the pilot picked at the ammo screen");
    }

    // The same reading over the other two codes, driven on the rig the drop left: all three cases
    // write the same slug/high-explosive table, so none of them may carry a sortie pick either.
    private static void CheckOtherCodesDropIt(TestContext ctx, FlightRoster roster, PlayerRig rig,
        StringBuilder report)
    {
        foreach (int code in OtherSwapCodes)
        {
            if (AirframeSwapCodes.For(code) is not { } airframe)
            {
                continue;
            }

            var rebuilt = roster.RunSwap(rig, new AirframeSwapOrder(airframe, null), handsOver: false).Swapped
                ? rig.Controller
                : null;
            report.AppendLine($"code {code}: {(rebuilt == null ? "no rebuild" : DescribeFit(rebuilt))}");
            ctx.Check(rebuilt != null && Carries(rebuilt, HandedPylon, HandedAmmo),
                $"code {code}'s rebuild onto '{airframe.PlaneNode}' carries '{HandedPylon}' on every pylon and '{HandedAmmo}' in every gun, its own case's table rather than the sortie's picks");
        }
    }

    // Whether every pylon mounts one ordnance and every firable gun one ammunition. Read off the
    // BOUND weapons rather than the def, since the bind is where a pick would still reach.
    private static bool Carries(FlightController plane, string pylonId, string ammo)
    {
        if (plane.Loadout is not { } fit || fit.Hardpoints.Count == 0)
        {
            return false;
        }

        foreach (var pylon in fit.Hardpoints)
        {
            if (!string.Equals(pylon.Weapon.Id, pylonId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        int guns = 0;
        foreach (var gun in fit.Guns)
        {
            if (gun.IsTurret)
            {
                continue;
            }

            guns++;
            if (!string.Equals(gun.Weapon.Id, StockLoadouts.GunWeaponId(GunCaliber(fit, gun.Slot), ammo),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return guns > 0;
    }

    // The caliber the def authored for one gun slot, which the ammunition rides: a bound group
    // keeps no caliber of its own, only the resolved weapon the two together name.
    private static int GunCaliber(Loadout fit, int slot)
    {
        foreach (var gun in fit.Def.Guns)
        {
            if (gun.Slot == slot)
            {
                return gun.Caliber;
            }
        }

        return 0;
    }

    private static string DescribeFit(FlightController plane)
    {
        if (plane.Loadout is not { } fit)
        {
            return "unarmed";
        }

        return $"'{fit.Def.Def}' guns [{string.Join(",", fit.Guns.Select(g => g.Weapon.Id))}]" +
            $" pylons [{string.Join(",", fit.Hardpoints.Select(h => h.Weapon.Id))}]";
    }

    private static HashSet<Texture2D> SurfaceTextures(Node3D root)
    {
        var textures = new HashSet<Texture2D>();
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is MeshInstance3D mesh && mesh.Mesh is { } geometry)
            {
                for (int i = 0; i < geometry.GetSurfaceCount(); i++)
                {
                    if (mesh.GetActiveMaterial(i) is ShaderMaterial shader
                        && shader.GetShaderParameter("albedo_tex").As<Texture2D>() is { } texture)
                    {
                        textures.Add(texture);
                    }
                }
            }

            foreach (var child in node.GetChildren())
            {
                stack.Push(child);
            }
        }

        return textures;
    }

    // The flown aeroplane rides the drop in view, the way the clip shows it: the hangar-floor
    // Bloodhawk prop is on for the first camera leg and off from the swap, and through the lift
    // leg the rebuilt rig is drawn on the `player` marker, wearing the staged undercarriage, while
    // that marker moves.
    private static void CheckRide(TestContext ctx, HangarRide ride, CutsceneController cutscene,
        Vector3 hangar, StringBuilder report)
    {
        report.AppendLine(ride.Describe());
        ctx.Check(ride.PropShownBeforeSwap, $"the hangar-floor Bloodhawk prop ('anim_bloodhawk') is drawn under world1 before the swap");
        ctx.Check(ride.PropShownAfterSwap == 0, $"and switched off from the swap on ({ride.PropShownAfterSwap} frame(s) still drawn)");
        ctx.Check(ride.LiftFrames > 0, $"the lift leg '{HangarRide.LiftLeg}' ran with the rebuilt rig present ({ride.LiftFrames} frame(s))");
        ctx.Check(ride.LiftFrames > 0 && ride.RigHiddenOnLift == 0,
            $"the rig is drawn on every one of them ({ride.RigHiddenOnLift} frame(s) undrawn)");
        ctx.Check(ride.LiftFrames > 0 && ride.RigOffMarker == 0,
            $"and sits on the '{AircraftStage.PlayerNode}' marker's pose on every one ({ride.RigOffMarker} frame(s) off it by over {HangarRide.MarkerTolerance} m)");
        ctx.Check(ride.LiftFrames > 0 && ride.GearOffMarker == 0,
            $"wearing '{HangarRide.GearNode}' under that marker ({ride.GearOffMarker} frame(s) without it)");
        ctx.Check(ride.LiftTravel > HangarRide.MinLiftTravel,
            $"while the marker travels {ride.LiftTravel:0.#} m over the leg, the lift the clip shows");
        ctx.Check(!cutscene.Playing, $"and the definition ends within the budget, handing the aeroplane back");
        ctx.Check(!cutscene.Playing && !ride.PropsDrawnAfterHandoff,
            $"with both props switched off again at the handoff, so the detached undercarriage is not left drawn at the origin");
        ctx.Check(ride.HandoffDistance is { } handoff && handoff < HangarRide.HandoffTolerance,
            $"and the pilot flies out of where the drop left the marker ({ride.HandoffDistance?.ToString("0.#") ?? "-"} m off it), the 951 its RESET_STATE authors, rather than from the approach");
        float? fromHangar = ride.HandoffPosition is { } stood ? stood.DistanceTo(hangar) : null;
        report.AppendLine($"handoff {fromHangar?.ToString("0.#") ?? "-"} m from the hangar at {hangar}");
        ctx.Check(fromHangar is { } gap && gap < HangarDoorsM,
            $"and stands at the hangar doors ({fromHangar?.ToString("0.#") ?? "-"} m from '{hangar}'), not at the world origin: a marker put back at its rest pose by the reset would read on the marker and nowhere in the mission");
    }

    // The parachutist the drop drops into the hangar, read over the leg that animates him.
    private static void CheckChute(TestContext ctx, HangarChute chute, Vector3 hangar,
        StringBuilder report)
    {
        report.AppendLine(chute.Describe());
        ctx.Check(chute.LegFrames > 0,
            $"'{HangarChute.ChuteLeg}' runs inside the played drop ({chute.LegFrames} frame(s))");
        ctx.Same(1, chute.MostActors,
            $"with exactly one '{AircraftStage.ChuteNode}' in the world: the drop's site-less call asks for the staged figure itself, so a pooled copy beside it would be a second parachutist");
        ctx.Check(chute.LegFrames > 0 && chute.ActorMoved == 0,
            $"the staged '{AircraftStage.ChuteNode}' stays where the archive staged it ({chute.ActorMoved} frame(s) moved off it), the frame its own script poses the figure in");
        ctx.Check(chute.LegFrames > 0 && chute.FigureHidden == 0,
            $"the '{HangarChute.FigureNode}' figure is drawn on every frame of the leg ({chute.FigureHidden} frame(s) undrawn)");
        ctx.Check(chute.FigureUnderActor,
            $"hanging under that '{AircraftStage.ChuteNode}' rather than under a copy of it");
        float? gap = chute.FigureEnd is { } near ? near.DistanceTo(hangar) : null;
        report.AppendLine($"figure {gap?.ToString("0.#") ?? "-"} m from the hangar at {hangar}");
        ctx.Check(gap is { } metres && metres < HangarChute.AtHangarM,
            $"and comes down at the hangar ({gap?.ToString("0.#") ?? "-"} m from '{hangar}'), not kilometres out over the map where a relocated root would carry a world-posed script");
        ctx.Check(chute.Descent > HangarChute.MinDescentM,
            $"descending {chute.Descent:0.#} m over the leg, the drop into the hangar the clip shows");
    }

    // The trap: the build is 965's, not the airframe's. A plain swap onto the same node hands
    // over a stock Bloodhawk with no injector and its own hardpoint table.
    private static void CheckStockStaysStock(TestContext ctx, FlightRoster roster, PlayerRig rig,
        StringBuilder report)
    {
        var wanted = AirframeSwapCodes.For(HangarSwapCode)!.Value;
        var stock = roster.SwapPlayerAirframe(rig, wanted.PlaneNode);
        var stockFit = StockLoadouts.Load().For(wanted.Def);
        report.AppendLine($"stock rebuild: nitro={stock.Nitro.Installed} hardpoints={stock.Loadout?.Hardpoints.Count ?? 0} " +
            $"(stock table {stockFit?.Hardpoints?.Count ?? 0})");
        ctx.Check(!stock.Nitro.Installed,
            $"a swap onto '{wanted.PlaneNode}' with no build of its own installs no injector, so the nitrous is the Blue Streak's and never the airframe's");
        ctx.Check(stock.Loadout?.Hardpoints.Count == (stockFit?.Hardpoints?.Count ?? 0),
            $"and flies the stock hardpoint table");
    }

    private static bool SensorActive(TestWorld world)
    {
        foreach (var node in world.Runtime.FindNodes(DirectionSensor))
        {
            return node.Visible;
        }

        return true;
    }

    private static AnimDefinition? First(IReadOnlyList<AnimDefinition> defs) =>
        defs.Count > 0 ? defs[0] : null;

    // One staged hangar: the mission's own world and a flown human rig on the campaign's own
    // starting airframe, so the swap has a different aircraft to replace.
    private static void WithStagedHangar(TestContext ctx, TestWorld world, string chapter,
        Action<HangarStaged> leg)
    {
        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, chapter));
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var pane = new SubViewport();
        ctx.Host.AddChild(pane);
        var rig = new PlayerRig { Index = 0, Camera = ctx.Camera, HudParent = pane, Viewport = pane };
        var rigs = new[] { rig };
        FlightRoster? roster = null;
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        try
        {
            roster = BuildRoster(ctx, world, chapter, textures, pool, rigs, HangarStartPlane, SortieFit());
            roster.BuildPlayers(rigs);
            var before = rig.Controller ?? throw new InvalidOperationException("no rig was built");
            leg(new HangarStaged(roster, rig, before, cutscene, textures));
        }
        finally
        {
            var live = rig.Controller;
            roster?.ClearMembership();
            live?.Free();
            cutscene.Free();
            pane.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    // The decoded table against the shipped stat data: each code's def/node pair has to be the pair
    // the airframe itself carries, or the swap would build one airframe and fit another's weapons.
    private static void CheckTable(TestContext ctx, StringBuilder report)
    {
        var seen = new List<int>();
        foreach (var entry in AirframeSwapCodes.Table)
        {
            var stats = PlaneStats.Load(ctx.ZrdrPath, entry.PlaneNode);
            report.AppendLine($"code {entry.Code}: '{entry.PlaneNode}' def '{stats.DefName}' " +
                $"(decoded '{entry.Def}')");
            ctx.Check(string.Equals(stats.DefName, entry.Def, StringComparison.OrdinalIgnoreCase),
                $"code {entry.Code}'s '{entry.PlaneNode}' is the shipped def '{entry.Def}' the decode names");
            ctx.Check(!seen.Contains(entry.Code), $"and code {entry.Code} names exactly one airframe");
            seen.Add(entry.Code);
        }

        ctx.Same(3, seen.Count, $"the host answers three swap codes, one per airframe the data names");
        ctx.Check(AirframeSwapCodes.For(11) == null,
            $"and a cutscene code that is not a swap names no airframe, so the two vocabularies stay apart");
    }

    // Step 5's mission gate and its placement, read off CM02's own roster rather than named here.
    // The hand-over is keyed on chapter and mission strings inside the executable, so nothing in
    // the shipped data asks for it and only the exe decode says these two missions are special.
    private static void CheckHandoverPlan(TestContext ctx, CampaignMission mission,
        StringBuilder report)
    {
        ctx.Check(AirframeHandover.Resolves(mission.ChapterFolder, mission.MissionFolder),
            $"'{AirframeHandover.WingmanName}' resolves in {mission.ChapterFolder}/{mission.MissionFolder}, one of the two missions the exe names");
        ctx.Check(!AirframeHandover.Resolves(mission.ChapterFolder, "m01"),
            $"and in no other mission of the same chapter, so the hand-over is not a chapter-wide rule");

        var blocks = AiSkills.LoadRoster(SessionPaths.MissionZrdr(ctx.DataRoot,
            mission.ChapterFolder.ToUpperInvariant(), mission.MissionFolder.ToUpperInvariant()));
        var defs = VehicleDefs.Load(ctx.ZrdrPath);
        var nets = Array.Empty<AiNet>();
        var own = PlanNamed(CampaignRosterPlan.Build(blocks, defs, nets),
            AirframeHandover.WingmanName);
        var handed = PlanNamed(
            CampaignRosterPlan.Build(blocks, defs, nets,
                handover: new FlyingAirframe(StartPlane, null)),
            AirframeHandover.WingmanName);
        report.AppendLine($"{AirframeHandover.WingmanName}: own def flies '{own?.PlaneNode ?? "-"}', " +
            $"handed over it flies '{handed?.PlaneNode ?? "-"}'");
        ctx.Check(own != null && handed != null,
            $"the mission's own roster carries a '{AirframeHandover.WingmanName}' block for the swap to hand over to");
        ctx.Check(own!.Inert,
            $"authored deactivated, so it is built out of the world until the swap reveals it");
        ctx.Check(string.Equals(handed!.PlaneNode, StartPlane, StringComparison.OrdinalIgnoreCase),
            $"and in this mission it flies the player's own '{StartPlane}', which is the aeroplane it is about to be given");
        ctx.Check(!string.Equals(own.PlaneNode, StartPlane, StringComparison.OrdinalIgnoreCase),
            $"rather than the '{own.Def}' def's own airframe it flies everywhere else");
    }

    private static void Drive(TestContext ctx, TestWorld world, string chapter, string folder,
        StringBuilder report)
    {
        var authored = SwapCallsIn(world.Session.Program);
        foreach (var (anim, code, root) in authored)
        {
            report.AppendLine($"'{root}-{anim}' authors callback {code} -> " +
                $"'{AirframeSwapCodes.For(code)!.Value.PlaneNode}'");
        }

        ctx.Check(authored.Count > 0,
            $"{chapter}/{folder} authors a swap callback of its own, so the mission needs one");
        var wanted = AirframeSwapCodes.For(authored[0].Code)!.Value;
        ctx.Check(string.Equals(wanted.PlaneNode, "player_balmoral", StringComparison.Ordinal),
            $"and the airframe it names is the Balmoral the mission's capture hands the player");

        WithStagedCapture(ctx, world, chapter, folder, wanted, authored[0],
            staged => RunSwap(ctx, world, staged, wanted, authored[0], report));
        WithStagedCapture(ctx, world, chapter, folder, wanted, authored[0],
            staged => PlayTheCapture(ctx, world, staged, authored[0], report));
    }

    // The team CM02's own roster block authors for the capture, so the stand-in resolves its paint
    // exactly the way a real enemy spawn does (CampaignRoster.SpawnFor's ShippedSkins fork), rather
    // than through a scheme this suite would otherwise have to invent.
    private static int CapturedTeamFor(TestContext ctx, string chapter, string folder, string blockName)
    {
        var blocks = AiSkills.LoadRoster(SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder));
        foreach (var (name, fields) in blocks)
        {
            if (string.Equals(name, blockName, StringComparison.OrdinalIgnoreCase))
            {
                return AiSkills.RosterTeam(fields) ?? AimAssist.PlayerTeam + 1;
            }
        }

        return AimAssist.PlayerTeam + 1;
    }

    // One staged capture: the mission's own world, a flown human rig off the session's roster, the
    // capture animation's aircraft and the hand-over block, then the leg. Two legs share it because
    // a swap replaces the rig it ran on, so neither can read the other's world back.
    private static void WithStagedCapture(TestContext ctx, TestWorld world, string chapter, string folder,
        AirframeSwapCode wanted, (string Anim, int Code, string Root) call, Action<Staged> leg)
    {
        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, chapter));
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var pane = new SubViewport();
        ctx.Host.AddChild(pane);
        var rig = new PlayerRig { Index = 0, Camera = ctx.Camera, HudParent = pane, Viewport = pane };
        var rigs = new[] { rig };
        FlightRoster? roster = null;
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        try
        {
            roster = BuildRoster(ctx, world, chapter, textures, pool, rigs);
            roster.BuildPlayers(rigs);
            var before = rig.Controller ?? throw new InvalidOperationException("no rig was built");
            // A real enemy roster spawn, not an override: team != the player's own, so the AI
            // assembler resolves its paint through ShippedSkins exactly as a real capture would.
            int capturedTeam = CapturedTeamFor(ctx, chapter, folder, call.Root);
            var captured = StageAi(roster, call.Root, wanted.PlaneNode,
                before.WorldPosition + (before.NoseDirection * 300f), inert: false,
                team: capturedTeam, shippedSkins: true);
            var wingman = StageAi(roster, AirframeHandover.WingmanName, StartPlane,
                before.WorldPosition + new Vector3(0f, 0f, 5000f), inert: true);
            Damage(wingman, 0.10f);
            leg(new Staged(roster, rig, before, cutscene, captured, wingman, pool));
        }
        finally
        {
            // Only what this suite put on the host, the two AI rigs it staged included: the host is
            // shared with every suite in the run, and a leaked 'wingman_4' renames the next one.
            var live = rig.Controller;
            var members = new List<FlightController>(roster?.AiAircraft ?? Array.Empty<FlightController>());
            roster?.ClearMembership();
            live?.Free();
            foreach (var ai in members)
            {
                ai.Free();
            }

            cutscene.Free();
            pane.Free();
            pool.Free();
            textures.Dispose();
        }
    }

    // The swap itself, driven through the host the runtime dispatches a CALLBACK to, then read on
    // both sides: what the aircraft was, what it became, and what carried across.
    private static void RunSwap(TestContext ctx, TestWorld world, Staged staged,
        AirframeSwapCode wanted, (string Anim, int Code, string Root) call, StringBuilder report)
    {
        var (roster, rig, before, cutscene, captured, wingman, pool) = staged;
        string anim = call.Anim;
        cutscene.BindWorld(world.Runtime);
        cutscene.BindRigs(rigs: new[] { rig }, aiPlanes: Array.Empty<FlightController>);
        cutscene.HostDefinitions(new[] { anim });
        // The whole swap, the way the session runs it: this mission is one of the two that resolve
        // the hand-over name, so the seam is armed for it here too.
        cutscene.SwapAirframe = order => roster.RunSwap(rig, order, handsOver: true);

        var wasPos = before.WorldPosition;
        var wasNose = before.NoseDirection;
        float wasSpeed = before.WorldVelocity.Length();
        report.AppendLine(Describe("before", before));
        ctx.Check(before.Loadout != null && before.Damage != null,
            $"the flown aircraft arrives with a bound fit and a damage ledger to swap out of");
        string wasDef = before.Loadout!.Def.Def;
        ctx.Check(!string.Equals(wasDef, wanted.Def, StringComparison.OrdinalIgnoreCase),
            $"and it is not already '{wanted.Def}', so the swap has something to change");

        var (armorLeft, healthLeft) = Damage(captured, 0.25f);
        float outgoingArmor = before.Damage!.WholeArmor;
        float outgoingHealth = before.Damage!.WholeHealth;
        report.AppendLine($"capture '{call.Root}': armour {armorLeft * 100f:0}% structure {healthLeft * 100f:0}% of its own maxima");
        ctx.Check(cutscene.Host(wanted.Code, anim, call.Root),
            $"the mission-script host answers callback {wanted.Code} rather than declining it");
        var after = rig.Controller ?? throw new InvalidOperationException("the swap built no aircraft");
        report.AppendLine(Describe("after", after));
        CheckCapturedHull(ctx, captured, after, armorLeft, healthLeft, report);
        CheckLivery(ctx, before, captured, after, report);
        CheckHandover(ctx, wingman, wasPos, wasNose, outgoingArmor, outgoingHealth, report);

        ctx.Check(!ReferenceEquals(before, after),
            $"the swap replaces the player's aircraft rather than editing the one that was flying");
        ctx.Check(!GodotObject.IsInstanceValid(before) || !before.IsInsideTree(),
            $"and the aircraft it replaced is out of the world, not left flying beside it");

        CheckAirframe(ctx, ctx.ZrdrPath, after, wanted, wasDef, report);
        ctx.Check(after.WorldPosition.DistanceTo(wasPos) < PoseTolerance,
            $"the replacement starts where the aircraft it replaced was flying");
        ctx.Check(after.NoseDirection.Dot(wasNose) > 0.99f,
            $"pointed the way it was pointed");
        ctx.Check(Mathf.Abs(after.WorldVelocity.Length() - wasSpeed) < SpeedTolerance,
            $"and at the speed it was flying, so the swap is not a respawn");
        ctx.Same(before.PlayerIndex, after.PlayerIndex,
            $"the pilot keeps their shooter id, which is what a round already in the air scores to");
        ctx.Check(ReferenceEquals(pool.RigOfShooter(after.PlayerIndex), after),
            $"and the pool's registration under that id resolves the replacement, so the old rig gave its own up and the new one took its place");

        // The cutscene flags codes 965 to 967 set are the player vehicle's own +0x91d/+0x91e pair
        // and the chrome off: the state code 11 and code 2 assert between them
        // (docs/formats/anim-definitions/cutscenes.md).
        ctx.Check(cutscene.OutOfFlight && cutscene.Presenting,
            $"the swap sets the cutscene flags, so the player is out of flight with the chrome down while the capture plays");
        ctx.Check(after.Held && after.Inert,
            $"and that state lands on the aircraft the swap built, not on the one it replaced");
    }

    // The capture as a flown session runs it: the mission's own definition played through the
    // runtime on a realtime clock, with the AI list the park and reveal codes actually see. The
    // dispatched leg raises 967 by itself, so it can see neither the 913/914 pair the capture's own
    // wing walk wraps that code in; this leg isolates the authored swap choreography.
    private static void PlayTheCapture(TestContext ctx, TestWorld world, Staged staged,
        (string Anim, int Code, string Root) call, StringBuilder report)
    {
        var (roster, rig, before, cutscene, captured, _, _) = staged;
        Damage(captured, 0.25f);
        var savedClock = GameClock.Current;
        var savedHost = world.Runtime.CallbackHost;
        try
        {
            var clock = new GameClock { Mode = GameClock.RunMode.Realtime };
            GameClock.Current = clock;
            cutscene.BindWorld(world.Runtime);
            cutscene.HostDefinitions(ClosureOf(world, call.Anim));
            cutscene.BindRigs(new[] { rig }, () => roster.AiAircraft);
            cutscene.SwapAirframe = order => roster.RunSwap(rig, order, handsOver: true);
            world.Runtime.CallbackHost = cutscene.Host;
            world.Runtime.Play(call.Anim);

            bool swapped = false;
            bool hiddenAtSwap = false;
            bool shownAgain = false;
            float shownAtS = -1f;
            for (int i = 0; i < (int)(PlayBudgetS / StepDt); i++)
            {
                clock.BeginFrame(StepDt);
                world.Runtime.Advance(StepDt);
                cutscene.Tick();
                if (!swapped && !ReferenceEquals(rig.Controller, before))
                {
                    swapped = true;
                    hiddenAtSwap = captured.Inert;
                }

                // ⚠ Read the hidden bit, never InPlay: a stand-in flown into the sea reads out of
                // the world for a reason this leg is not about (INSTR-10).
                if (swapped && !captured.Inert && !shownAgain)
                {
                    shownAgain = true;
                    shownAtS = i * StepDt;
                }
            }

            report.AppendLine($"played '{call.Anim}' for {PlayBudgetS:0} s on a realtime clock: " +
                $"codes {string.Join(",", cutscene.Codes)}, swapped={swapped} " +
                $"hidden-at-swap={hiddenAtSwap} shown-again={shownAgain} at {shownAtS:0.##} s, " +
                $"'{call.Root}' end inert={captured.Inert} crashed={captured.Crashed}");
            ctx.Check(swapped,
                $"playing the mission's own '{call.Anim}' reaches callback {call.Code} through the runtime, the way a flown session reaches it");
            ctx.Check(hiddenAtSwap,
                $"and the capture animation's own aircraft is hidden as the swap runs");
            ctx.Check(!shownAgain,
                $"and stays hidden for the rest of the episode, rather than coming back in front of the player when the wing walk reveals the parked AI");
        }
        finally
        {
            world.Runtime.CallbackHost = savedHost;
            GameClock.Current = savedClock;
        }
    }

    // Every definition the capture can reach, its own plus the CALL_ANIMATION closure: what the
    // session's own host answers for, and the only way the called wing walk's codes are hosted.
    private static IReadOnlyList<string> ClosureOf(TestWorld world, string anim)
    {
        var names = new List<string>();
        foreach (var def in world.Session.Program.Subset(new[] { anim }).Defs)
        {
            if (def.AnimName is { } name && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    // One roster aircraft standing in for a block this suite's world builds no roster for: the
    // capture animation's own aircraft, and the block the outgoing aeroplane is handed to. Their
    // names are what the swap resolves them by, and that is all it reads them for.
    // ⚠ Staged facing BACKWARDS and damaged where the reveal is the subject: an aircraft parked
    // on the player's own heading with full pools passes the placement and pool checks without the
    // hand-over having run at all (INSTR-10).
    private static FlightController StageAi(FlightRoster roster, string name, string planeNode,
        Vector3 at, bool inert, PaintScheme? scheme = null, int team = AimAssist.PlayerTeam,
        bool shippedSkins = false)
    {
        var aim = at - Vector3.Forward;
        return roster.SpawnAi(new AiSpawn(planeNode, at, aim, AiPilot.HoldingCourse(at, aim),
            Scheme: scheme, Team: team, Inert: inert, ShippedSkins: shippedSkins, NodeName: name));
    }

    // Whether two rigs' painted output actually matches, read off the painter rather than the
    // scheme record alone (the rebuild's paint is what a player sees, and a record could match by
    // field while the model it built stayed unpainted or vice versa). Neither having a painter is
    // itself a match: a ShippedSkins reading can legitimately resolve to no paint at all, and that
    // null has to carry as faithfully as a real scheme would.
    private static bool PaintMatches(FlightController a, FlightController b)
    {
        bool aPainted = a.Painter != null;
        bool bPainted = b.Painter != null;
        if (aPainted != bPainted)
            return false;
        if (!aPainted)
            return true;
        // The tail decal placeholder every Balmoral ships (docs/formats/paint.md) is the fixed
        // point both painters are asked to resolve, so the comparison is real composited pixels.
        return ImagesEqual(a.Painter!.Substitute("bal_taillogo", null),
            b.Painter!.Substitute("bal_taillogo", null));
    }

    private static bool ImagesEqual(ImageTexture? a, ImageTexture? b)
    {
        if (a == null || b == null)
            return ReferenceEquals(a, b);
        var ia = a.GetImage();
        var ib = b.GetImage();
        if (ia == null || ib == null || ia.GetWidth() != ib.GetWidth() || ia.GetHeight() != ib.GetHeight())
            return false;
        return ia.GetData().SequenceEqual(ib.GetData());
    }

    // Shoots the stand-in down and answers what is left. Two hits, not one: armour still standing
    // against a hit that carries no armour damage nulls the health damage outright, so structure
    // is only reachable once the armour is spent (docs/org/vehicleDamage.md). An AI aircraft
    // resolves no zones, so both spends land on the whole pair (BL-386).
    private static (float Armor, float Health) Damage(FlightController plane, float health)
    {
        var hull = plane.Damage!;
        hull.Apply("nose", 0f, hull.WholeArmorMax);
        hull.Apply("nose", hull.WholeHealthMax * (1f - health), 0f);
        return (hull.WholeArmorMax > 0f ? hull.WholeArmor / hull.WholeArmorMax : 1f,
            hull.WholeHealthMax > 0f ? hull.WholeHealth / hull.WholeHealthMax : 1f);
    }

    // Code 967's first half: the aircraft the capture animation belongs to is out of the world, and
    // what is left of its hull is what the player's new one is flying on.
    private static void CheckCapturedHull(TestContext ctx, FlightController captured,
        FlightController after, float armor, float health, StringBuilder report)
    {
        var hull = after.Damage!;
        float gotArmor = hull.WholeArmorMax > 0f ? hull.WholeArmor / hull.WholeArmorMax : 1f;
        float gotHealth = hull.WholeHealthMax > 0f ? hull.WholeHealth / hull.WholeHealthMax : 1f;
        report.AppendLine($"inherited hull: armour {gotArmor * 100f:0}% structure {gotHealth * 100f:0}%");
        ctx.Check(captured.Inert,
            $"the aircraft the capture animation belongs to is hidden, not left flying beside the player");
        ctx.Check(Mathf.Abs(gotArmor - armor) < FractionTolerance
                  && Mathf.Abs(gotHealth - health) < FractionTolerance,
            $"and the new hull carries that aircraft's own armour and structure fractions, so a Balmoral shot half to pieces is the one the player inherits");
        ctx.Check(gotHealth < 1f - FractionTolerance,
            $"which leaves the player damaged rather than handing them a pristine airframe and making the ending easier than the original's");
    }

    // The rebuilt rig's actual painted output matches the captured aircraft's, read off the
    // painter rather than the scheme record alone, and the match is not a coincidence of the two
    // happening to share a pattern: the captured stand-in is a real enemy roster spawn (team != the
    // player's own), so its ShippedSkins reading is what CampaignRoster.SpawnFor gives every
    // campaign enemy, distinct from the player's own default Fortune Hunters paint.
    private static void CheckLivery(TestContext ctx, FlightController before,
        FlightController captured, FlightController after, StringBuilder report)
    {
        report.AppendLine($"livery: was '{before.Scheme?.Label ?? "-"}' (painted={before.Painter != null}), " +
            $"captured '{captured.Scheme?.Label ?? "-"}' (shipped-skins={captured.ShippedSkins}, " +
            $"painted={captured.Painter != null}), rebuilt '{after.Scheme?.Label ?? "-"}' " +
            $"(painted={after.Painter != null})");
        ctx.Check(!PaintMatches(before, captured),
            $"the capture stand-in's own paint is not the player's own, so a match below cannot be coincidence");
        ctx.Check(PaintMatches(after, captured),
            $"the rebuilt rig's actual painted output matches the captured aircraft's, as the original reads at the controls");
    }

    // Step 5: the aeroplane the player just left, in the hands of wingman_4 and visible.
    private static void CheckHandover(TestContext ctx, FlightController wingman, Vector3 wasPos,
        Vector3 wasNose, float armor, float health, StringBuilder report)
    {
        var offset = wingman.WorldPosition - wasPos;
        var flatNose = new Vector3(wasNose.X, 0f, wasNose.Z).Normalized();
        var flatOffset = new Vector3(offset.X, 0f, offset.Z);
        float bearing = Mathf.RadToDeg(flatNose.SignedAngleTo(flatOffset.Normalized(), Vector3.Up));
        report.AppendLine($"{AirframeHandover.WingmanName}: {offset.Length():0.#} m off at {bearing:0.#} deg, " +
            $"armour {wingman.Damage?.WholeArmor ?? 0f:0.#}/{armor:0.#} structure {wingman.Damage?.WholeHealth ?? 0f:0.#}/{health:0.#}");
        ctx.Check(!wingman.Inert && wingman.InPlay,
            $"'{AirframeHandover.WingmanName}' is revealed by the swap rather than left built out of the world");
        ctx.Check(Mathf.Abs(offset.Length() - AirframeHandover.RangeM) < RangeTolerance,
            $"{AirframeHandover.RangeM:0} m from where the player was flying");
        ctx.Check(Mathf.Abs(bearing - AirframeHandover.BearingDeg) < BearingTolerance,
            $"at {AirframeHandover.BearingDeg:0} degrees off that aircraft's own nose");
        ctx.Check(wingman.NoseDirection.Dot(flatNose) > 0.99f,
            $"pointed the way the player was pointed, so the two are flying alongside rather than converging");
        // ⚠ Against the sums CAPPED at this aircraft's own maxima: one airframe reads a zone-sum on
        // a human rig and the AI def's own authored pair here (BL-386), so the cap does real work.
        ctx.Check(wingman.Damage is { } hull
                  && Mathf.Abs(hull.WholeArmor - Mathf.Min(armor, hull.WholeArmorMax)) < PoolTolerance
                  && Mathf.Abs(hull.WholeHealth - Mathf.Min(health, hull.WholeHealthMax)) < PoolTolerance,
            $"carrying the armour and structure sums measured off the aeroplane the player left, not a repaired hull");
    }

    // What the replacement is: the named airframe's own def, its own stock hardpoint table, and its
    // own armour pools. The comparison is against the airframe LEFT as much as against the data,
    // because a swap that changed the model and kept the previous fit would read right on its own.
    private static void CheckAirframe(TestContext ctx, string zrdrPath, FlightController after,
        AirframeSwapCode wanted, string wasDef, StringBuilder report)
    {
        var stats = PlaneStats.Load(zrdrPath, wanted.PlaneNode);
        var stock = PlaneDamage.For(stats);
        ctx.Check(after.Loadout is { } fit
                  && string.Equals(fit.Def.Def, wanted.Def, StringComparison.OrdinalIgnoreCase),
            $"the player is flying '{wanted.Def}', the def callback {wanted.Code} names");
        var loadout = after.Loadout!;
        int hardpoints = loadout.Hardpoints.Count;
        var wasStock = StockLoadouts.Load().For(wasDef);
        report.AppendLine($"hardpoints: {hardpoints} on '{wanted.Def}', " +
            $"{(wasStock?.Hardpoints?.Count ?? 0)} authored on '{wasDef}'");
        ctx.Check(hardpoints > 0, $"with '{wanted.Def}'s own hardpoint table bound to its pylons");
        ctx.Check(hardpoints != (wasStock?.Hardpoints?.Count ?? 0),
            $"and that table is the new airframe's, not the '{wasDef}' table the aircraft carried in");
        int full = 0;
        foreach (var hardpoint in loadout.Hardpoints)
        {
            full += hardpoint.Ammo == hardpoint.Capacity ? 1 : 0;
        }

        ctx.Same(hardpoints, full,
            $"every pylon arrives at its own capacity: the ordnance is the airframe's, so nothing of the previous count carries over");

        ctx.Check(after.Damage != null, $"the replacement carries a damage ledger");
        var damage = after.Damage!;
        report.AppendLine($"armour: whole {damage.WholeArmorMax:0.#}/{damage.WholeHealthMax:0.#} " +
            $"over {damage.Parts.Count} zone(s); the airframe's own is " +
            $"{stock.WholeArmorMax:0.#}/{stock.WholeHealthMax:0.#} over {stock.Parts.Count}");
        ctx.Check(Mathf.IsEqualApprox(damage.WholeArmorMax, stock.WholeArmorMax)
                  && Mathf.IsEqualApprox(damage.WholeHealthMax, stock.WholeHealthMax),
            $"whose armour and health pools are '{wanted.PlaneNode}'s own, off its own stat rows");
        ctx.Same(stock.Parts.Count, damage.Parts.Count,
            $"over that airframe's own damage zones, which is the ladder the capture's aircraft has to fly on");
        ctx.Check(damage.WorstFraction < 1f,
            $"whose zones start at what is left of the CAPTURED aircraft's hull rather than at the record's own full pools, which is the whole of the swap's difficulty");
    }

    private static string Describe(string when, FlightController controller) =>
        $"{when}: def='{controller.Loadout?.Def.Def ?? "-"}' " +
        $"hardpoints={controller.Loadout?.Hardpoints.Count ?? 0} " +
        $"guns={controller.Loadout?.Guns.Count ?? 0} " +
        $"armour={controller.Damage?.WholeArmorMax ?? 0f:0.#} " +
        $"zones={controller.Damage?.Parts.Count ?? 0} " +
        $"pos={controller.WorldPosition} speed={controller.WorldVelocity.Length():0.#}";

    // Every definition in the mission's compiled program that raises one of the swap codes, with the
    // code it raises. Read out of the shipped data rather than named here: what the mission asks for
    // is the mission's own business, and this suite only drives it.
    private static List<(string Anim, int Code, string Root)> SwapCallsIn(AnimProgram program)
    {
        var found = new List<(string, int, string)>();
        foreach (var def in program.Defs)
        {
            if (def.AnimName is not { } anim)
            {
                continue;
            }

            string root = def.RootName is { Length: > 0 } named ? named : def.Name;

            foreach (var sequence in def.Sequences)
            {
                foreach (var ev in sequence.Events)
                {
                    if (!string.Equals(ev.Kind, "Callback", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int code = (int)(ev.Data.Num("value") ?? -1f);
                    if (AirframeSwapCodes.For(code) != null)
                    {
                        found.Add((anim, code, root));
                    }
                }
            }
        }

        return found;
    }

    // The pilot's own Ammo Selection choice for the sortie, on every slot the screen offers: the
    // campaign launch hands one of these to the session for the seated aeroplane.
    private static LoadoutChoice SortieFit()
    {
        var fit = new LoadoutChoice();
        for (int slot = 1; slot <= LoadoutChoice.MaxGunSlot; slot++)
        {
            fit.SetGunAmmo(slot, SortieAmmo);
        }

        for (int pylon = 1; pylon <= LoadoutChoice.MaxPylon; pylon++)
        {
            fit.SetPylon(pylon, SortiePylon);
        }

        return fit;
    }

    private static FlightRoster BuildRoster(TestContext ctx, TestWorld world, string chapter,
        TextureArchive textures, ProjectilePool pool, IReadOnlyList<PlayerRig> rigs,
        string plane = StartPlane, LoadoutChoice? fit = null)
    {
        var spec = SessionSpec.Parse(new[] { $"--plane={plane}" });
        if (fit != null)
        {
            spec = spec.WithSeatedAircraft(plane, null, fit);
        }

        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var resources = new AircraftAssemblyResources
        {
            PlanesGamez = planesGamez,
            StatsFor = plane => PlaneStats.Load(ctx.ZrdrPath, plane),
            AiStatsFor = (plane, aiDef) => PlaneStats.LoadForAi(ctx.ZrdrPath, plane, aiDef),
            CamParamsFor = _ => new CamParams(),
            PaintRng = new RandomNumberGenerator(),
            ZrdrPath = ctx.ZrdrPath,
            StockLoadouts = StockLoadouts.Load(),
            WeaponDefs = WeaponDefs.Load(ctx.ZrdrPath, null),
            WeaponMessages = Messages.Load(ctx.MessagesPath),
            Textures = textures,
            Shakes = ShakeDefs.Load(ctx.ZrdrPath),
        };
        return new FlightRoster(FlightRosterPolicy.From(spec),
            new LiveryResolver(spec, Path.Combine(ctx.DataRoot, "extracted", "rof")),
            new WorldEffectsFactory(spec, ctx.Host, () => Vector3.Zero), ctx.Host, resources,
            new FlightWorldBindings
            {
                Projectiles = pool,
                Gamez = planesGamez,
                ChapterZrdrPath = SessionPaths.ChapterZrdr(ctx.DataRoot, chapter),
            },
            new HumanRosterBindings
            {
                RigCount = rigs.Count,
                Rigs = rigs,
                PauseState = new PauseState(),
                MenuInputFor = _ => new MenuInput(),
                ExitSession = () => { },
            }, new SwapFlightStarts());
    }

    private static RosterSpawnPlan? PlanNamed(CampaignRosterPlan plan, string name)
    {
        foreach (var spawn in plan.Spawns)
        {
            if (string.Equals(spawn.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return spawn;
            }
        }

        return null;
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

    // The actors one staged capture hands its leg: the session-shaped roster and rig, the aircraft
    // that was flying before the swap, the host, the capture animation's own aircraft, the block the
    // outgoing aeroplane goes to, and the pool the registrations are counted in.
    private sealed record Staged(
        FlightRoster Roster,
        PlayerRig Rig,
        FlightController Before,
        CutsceneController Cutscene,
        FlightController Captured,
        FlightController Wingman,
        ProjectilePool Pool);

    // The actors one staged hangar hands its leg: the roster and rig, the aircraft flown in, the
    // host, and the chapter archive the rebuild's skins come out of.
    private sealed record HangarStaged(
        FlightRoster Roster,
        PlayerRig Rig,
        FlightController Before,
        CutsceneController Cutscene,
        TextureArchive Textures);

    // One start, high enough over the mission's own terrain that the aircraft is flying rather than
    // resolving a ground contact on the frame the swap rebuilds it.
    private sealed class SwapFlightStarts : IFlightStarts
    {
        public IReadOnlyList<FlightStart> ChooseStarts(IReadOnlyList<SpawnPoint>? spawns,
            string missionZrdrPath, int spawnBase, int playerCount)
        {
            var starts = new FlightStart[playerCount];
            for (int i = 0; i < playerCount; i++)
            {
                var position = new Vector3(i * 60f, 1200f, 0f);
                starts[i] = new FlightStart(position, position + Vector3.Forward, 0.5f, 90f);
            }

            return starts;
        }
    }

    // The per-frame record of the parachutist the drop's own chute leg animates. That leg poses the
    // staged actor's CHILDREN with world-coordinate scripts and never moves the actor itself, so
    // the reading that matters is where the figure hangs while the actor stays put: a root carried
    // to a call site would take the whole descent with it and leave the shot empty.
    private sealed class HangarChute
    {
        public const string ChuteLeg = "hdchute1";
        public const string FigureNode = "chutemanparent";
        public const float StagedTolerance = 1f;
        public const float AtHangarM = 200f;
        public const float MinDescentM = 10f;

        private readonly Node3D? _actor;
        private readonly Vector3 _stagedAt;
        private float _highest = float.MinValue;
        private float _lowest = float.MaxValue;

        public HangarChute(AircraftStage? aircraft)
        {
            _actor = aircraft?.Chuteman;
            _stagedAt = _actor != null ? AnimRuntime.WorldTransform(_actor, out _).Origin : Vector3.Zero;
        }

        /// <summary>The most <c>chuteman</c> nodes the world held at once. One: the drop names no
        /// site, so the staged figure is the figure, and a second is a pooled copy nothing drives.
        /// </summary>
        public int MostActors { get; private set; }

        public int LegFrames { get; private set; }

        public int ActorMoved { get; private set; }

        public int FigureHidden { get; private set; }

        public bool FigureUnderActor { get; private set; }

        /// <summary>Where the figure hung on the leg's last frame, in world coordinates.</summary>
        public Vector3? FigureEnd { get; private set; }

        public float Descent => _highest > _lowest ? _highest - _lowest : 0f;

        public void Sample(TestWorld world, bool legRunning)
        {
            MostActors = Math.Max(MostActors, world.Runtime.FindNodes(AircraftStage.ChuteNode).Count);
            if (!legRunning)
            {
                return;
            }

            LegFrames++;
            if (_actor == null)
            {
                FigureHidden++;
                return;
            }

            if (AnimRuntime.WorldTransform(_actor, out _).Origin.DistanceTo(_stagedAt) > StagedTolerance)
            {
                ActorMoved++;
            }

            Node3D? figure = null;
            foreach (var candidate in world.Runtime.FindNodes(FigureNode))
            {
                if (_actor.IsAncestorOf(candidate))
                {
                    figure = candidate;
                    break;
                }
            }

            if (figure == null || !figure.IsVisibleInTree())
            {
                FigureHidden++;
                return;
            }

            FigureUnderActor = true;
            var at = AnimRuntime.WorldTransform(figure, out _).Origin;
            FigureEnd = at;
            _highest = Math.Max(_highest, at.Y);
            _lowest = Math.Min(_lowest, at.Y);
        }

        public string Describe() =>
            $"chute: '{AircraftStage.ChuteNode}' most in world={MostActors}, leg {LegFrames} frame(s), " +
            $"actor moved off its staged pose {ActorMoved}, figure undrawn {FigureHidden}, " +
            $"under the actor={FigureUnderActor}, descent {Descent:0.#} m, " +
            $"figure ends at {FigureEnd?.ToString() ?? "-"}";
    }

    // The per-frame record of the drop's ride, sampled after the cutscene host has posed the rig.
    // The lift leg is the window between the two tail starts, read off the start record rather
    // than the runtime's state, so the sample cannot miss a leg that ends within the frame.
    private sealed class HangarRide
    {
        public const string PropNode = "anim_bloodhawk";
        public const string GearNode = "bloodhawk_gear";
        public const string LiftLeg = "hdplayer2";
        public const string AfterLift = "hdplayer3";
        public const float MarkerTolerance = 0.5f;
        public const float MinLiftTravel = 1f;
        public const float HandoffTolerance = 10f;

        private readonly Node3D? _marker;
        private readonly Node3D? _prop;
        private readonly Node3D? _gear;
        private Vector3? _liftStart;
        private Vector3 _liftEnd;
        private Vector3? _markerWhilePlaying;

        public HangarRide(AircraftStage? aircraft)
        {
            _marker = aircraft?.PlayerMarker;
            _prop = aircraft != null && aircraft.Props.TryGetValue(PropNode, out var prop) ? prop : null;
            _gear = aircraft != null && aircraft.Props.TryGetValue(GearNode, out var gear) ? gear : null;
        }

        public bool PropShownBeforeSwap { get; private set; }

        public int PropShownAfterSwap { get; private set; }

        public int LiftFrames { get; private set; }

        public int RigHiddenOnLift { get; private set; }

        public int RigOffMarker { get; private set; }

        public int GearOffMarker { get; private set; }

        public float LiftTravel => _liftStart is { } start ? start.DistanceTo(_liftEnd) : 0f;

        /// <summary>How far the rig sat, on the first frame after the handoff, from where the
        /// definition's last playing frame left the marker. ⚠ Against that remembered pose and not
        /// against the marker itself: the handoff sends the marker home to the world root, so a
        /// live read would measure the trip home rather than the re-placement. Null while the
        /// definition is still playing.</summary>
        public float? HandoffDistance { get; private set; }

        /// <summary>Where the rig stood on that frame, in world coordinates. Read against the
        /// hangar rather than against the marker: the marker is put back at its rest pose by the
        /// definition's own reset, so a rig standing on it says nothing about where in the world
        /// the two of them ended up.</summary>
        public Vector3? HandoffPosition { get; private set; }

        /// <summary>Whether either prop is still drawn once the drop has handed off: the reset's
        /// own child detach must leave the undercarriage at the origin undrawn.</summary>
        public bool PropsDrawnAfterHandoff =>
            (_prop != null && _prop.IsVisibleInTree()) || (_gear != null && _gear.IsVisibleInTree());

        public void Sample(FlightController? rig, bool swapped, bool liftLeg, bool playing)
        {
            bool propShown = _prop != null && _prop.IsVisibleInTree();
            if (!swapped)
            {
                PropShownBeforeSwap |= propShown;
            }
            else if (propShown)
            {
                PropShownAfterSwap++;
            }

            if (playing && _marker != null)
            {
                _markerWhilePlaying = AnimRuntime.WorldTransform(_marker, out _).Origin;
            }

            // The first frame after the handoff: the aeroplane flies out of where the definition
            // left the marker, one flight step on.
            if (swapped && !playing && HandoffDistance == null && _markerWhilePlaying is { } left
                && rig != null)
            {
                HandoffPosition = rig.GlobalTransform.Origin;
                HandoffDistance = rig.GlobalTransform.Origin.DistanceTo(left);
            }

            if (!swapped || !liftLeg || _marker == null || rig == null)
            {
                return;
            }

            LiftFrames++;
            var pose = AnimRuntime.WorldTransform(_marker, out _);
            _liftStart ??= pose.Origin;
            _liftEnd = pose.Origin;
            if (rig.PlaneModel is not { } model || !model.IsVisibleInTree())
            {
                RigHiddenOnLift++;
            }

            if (rig.GlobalTransform.Origin.DistanceTo(pose.Origin) > MarkerTolerance)
            {
                RigOffMarker++;
            }

            if (_gear == null || _gear.GetParent() != _marker || !_gear.IsVisibleInTree())
            {
                GearOffMarker++;
            }
        }

        public string Describe() =>
            $"ride: prop shown before swap={PropShownBeforeSwap}, after={PropShownAfterSwap} frame(s); " +
            $"lift leg {LiftFrames} frame(s), rig undrawn {RigHiddenOnLift}, off marker {RigOffMarker}, " +
            $"gear off marker {GearOffMarker}, marker travel {LiftTravel:0.##} m, props drawn after handoff {PropsDrawnAfterHandoff}, " +
            $"handoff at {HandoffPosition?.ToString() ?? "-"}, {HandoffDistance?.ToString("0.##") ?? "-"} m off where the drop left the marker";
    }
}
