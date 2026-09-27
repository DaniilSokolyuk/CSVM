using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSVM.Effects;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Weapons;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using CSVM.Session.Roster;
using CSVM.Session.World;
using CSVM.Tooling;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>The suite over the mid-mission cutscene trigger: the chapter's <c>landings.zrd</c>
/// approach table resolved against a built world, armed by the mission's own objective chain, and
/// flown until it starts the drop.</summary>
internal static class LandingApproachSuites
{
    // The story positions flown. Every node, animation and objective name below is read out of that
    // mission's own data, so nothing here names a shipped world node by hand.
    private const int FirstSeq = 0;
    private const int WingWalkSeq = 1;
    private const int DockingSeq = 5;
    private const int TrainPickupSeq = 6;
    private const int TrailerPickupSeq = 10;
    private const int CarPickupSeq = 15;
    private const float StepDt = 1f / 60f;
    private const string PlaneNode = "player_bhawk";

    // The definition the hookup calls to put the flown airframe on the trapeze, and the two
    // airframes it is flown on: the one the defect was reported on, and one whose authored mount
    // offset differs from it in every axis, so no single pose could satisfy both.
    private const string ExtendHookAnim = "player_extend_hook";
    private const string HookupPlayerSeq = "move_player";

    // CM02's own ending: the shared hookup row the mission finishes on, flown in the airframe the
    // capture handed over, which is the one branch of that definition that folds a wing.
    private const string DockAnim = "hooked_to_klondike";
    private const string BalmoralPlane = "player_balmoral";

    // The piratezep crane's own hook, woken separately from the airframe hookup above.
    private const string HookHomebaseAnim = "pzhomebase";

    // The mission-completion code the docking ends on, which is the last thing the episode raises,
    // and the authored run time of the wing fold the branch waits out before raising it.
    private const int CompleteCode = 13;
    private const float FoldRunS = 2f;

    // How far before the fold's authored end the completion code may land. The two racing sequences
    // reach the same call within a few frames of each other, and which one wins is not a claim this
    // suite makes; that the wait is waited out is.
    private const float FoldEndToleranceS = 0.25f;

    // The animation runtime's own ANIM_STATE numbering, the numbering the objective script's
    // condition is authored in: RUNNING while an instance is live, EXECUTED once it has ended.
    private const int AnimRunning = 2;
    private const int AnimExecuted = 3;

    // How far the mission's outcome may lag the completion code and still count as the same
    // moment: the code lands inside the animation advance and the graph reaches its wrap-up on the
    // step that follows it, so one frame is the whole allowance.
    private const float EndLagS = StepDt + 1e-4f;

    // How close a pose has to land on its authored value to count as that value.
    private const float PoseEpsilon = 1e-3f;

    // How close a swung arm's Euler read-back has to land on its authored end angle. Looser
    // than PoseEpsilon: GetEuler's decomposition of a composed rotate+scale basis carries more
    // float noise than a plain position lerp does.
    private const float SwingEpsilon = 1e-2f;

    // How far short of its authored end the wing fold is when the mission's own ending stops the
    // world: the fold turns 1.92 rad in 2.01 s and the branch that raises the code waits 2.0 s, so
    // the last 0.06 s of the turn is never flown, whatever the frame rate.
    private const float FoldFreezeEpsilon = 0.1f;

    // How long the mission's own intro is given to run out before a hookup is flown, and how long
    // the airframe's own wing-fold turn is given after the episode ends.
    private const float IntroSettleS = 60f;
    private const float FoldSettleS = 3f;

    // The flown approach: how far along the cone's own axis the aircraft starts, how long it is
    // given to reach the trigger, and the speed it flies at, which sits inside every band the
    // shipped table authors (50-320 mph).
    private const float AxisFraction = 0.6f;
    private const float ApproachSpeedMps = 45f;
    private const float ApproachThrottle = 0.5f;
    private const float ApproachBudgetS = 6f;

    // The pickup definition's own climb ladder: the second thing it calls, after stopping the
    // hanging ladder's wind loop. It re-parents that same rope ladder to the caboose and drives its
    // root and six rungs from one script (docs/org/ladderSwitch.md, "The ladder through the climb").
    private const string ClimbLadderAnim = "cabpkup_ladder";

    // How long the flare's smoke is sampled for. Short on purpose: the sample spends the pickup
    // window's own seconds, and the rest of the drive still has to fly the approach inside it.
    private const float FlareSampleS = 0.3f;

    // CM16's armoured-car pickup, every name read out of that mission's own data: the row's
    // animation, the definition it calls last, the train its own film blows up, and the two
    // definitions the mission's objectives wake to stage it.
    private const string CarPickupAnim = "pickup_sparks";
    private const string GotSparksAnim = "got_sparks";
    private const string TrainNode = "train01";
    private const string TrainAnim = "tsega1";
    private const string CarGunNode = "tcargun01";
    private const string IntroAnim = "generic_intro";

    // How long the mission's own chain is given to raise that pickup's three node prerequisites.
    // The passenger's climb starts 25 s into the armoured car's own definition, so the budget is
    // that plus the climb, not a guess at the whole approach.
    private const float CarStageBudgetS = 90f;

    // The film's two authored beats, in seconds from the episode start, each the sum of its own
    // scripts: 'carpkup_player.zan' (6.656 s) plus the EVENT_OFFSET 10 before destroy_car01, and
    // 'sparkspickup.zan' + 'final_cpilot.zan' (6.656 + 14.849 s) before the WAIT_FOR_COMPLETION
    // releases and the pickup calls got_sparks. The tolerance is a few frames of dispatch.
    private const float WreckAtS = 16.66f;
    private const float GotSparksAtS = 21.51f;
    private const float FilmToleranceS = 0.2f;

    // How long the parked rig waits at CM11's trailer for its range-armed definition to run the
    // authored hatch time and raise the actor the pickup requires, before the cone is flown.
    private const float TrailerApproachS = 4f;

    // How long the graph is stepped for a nap chain to run out, and how long a started cutscene
    // definition is given to reach EXECUTED.
    private const float ArmBudgetS = 12f;
    private const float PlayBudgetS = 45f;

    // How long the auto row's own WAKE_ANIM is given to reach the node write, polled rather than
    // assumed instant.
    private const float AutoArmBudgetS = 30f;

    // How long the trigger is ticked after a handoff to catch the row re-firing.
    private const int RestartFrames = 30;

    // The co-op leg's field, and how far out of the sphere the human who is not being offered the
    // prompt is parked: well past any shipped row's radius, so "outside" is not a tolerance.
    private const int CoopHumans = 2;
    private const float CoopAwayM = 1000f;

    // The docking episode: how long the row's definition is given to play out through its whole
    // call chain (hookup, drop, hook state, unhook), how far past the handoff the aeroplane is
    // watched, the largest single-frame move a flying aeroplane can make against a teleport, and
    // how far the released aeroplane may sit from the marker its re-placement code read.
    private const float DockBudgetS = 150f;
    private const float AfterReleaseS = 2f;
    private const float TeleportM = 20f;
    private const float PlacedToleranceM = 2f;

    // The mission-script host's handoff code, the one that gives the player flight back.
    private const int HandoffCode = 1;

    // How far outside its band the hangar drop is called from. Anything past the authored 75 m
    // does; this is far enough that no bounds reading of the hangar could land inside it.
    private const float AwayM = 2000f;

    // One airframe per shape of the extend-hook fork: the Balmoral's branch is the only one that
    // folds a wing, the pirate fighter's swings doors as well as arms, and the Brigand's is the
    // longest plain arm swing (the Fury shares it, the Warhawk, Peacemaker and autogyro are shorter
    // ones of the same shape). Each drive costs a full episode, so the shape is what is covered,
    // not the archive.
    private static readonly string[] HookupPlanes =
    {
        "player_balmoral", "player_pfighter", "player_brigand",
    };

    // The piratezep crane's own two inward-swinging side parts.
    private static readonly string[] HookCraneParts = { "top_seg", "hoop" };

    // The three FROM_TO channels a park seeds a node's pose from, any one of which authoring a
    // `from` puts that node on the park's own list (AnimRuntime.SeedFromExtend).
    private static readonly string[] MotionChannels = { "rotate", "scale", "translate" };

    // What a landings suite does with the built world: the harness owns the build, the suite owns
    // the drive. The mission's own zrdr path comes with it, for the readers that are not the
    // objective script.
    private delegate void MissionDrive(TestContext ctx, TestWorld world, CampaignDirector director,
        ObjectiveScript script, string missionZrdr, StringBuilder report);

    /// <summary>Drives the campaign's first mission's approach triggers against its BUILT world:
    /// the chapter's rows resolve to real cone/half-cone/sphere volumes, a mission carrying none of
    /// the animations arms none of them, the drop-off rows start disarmed, the mission's own
    /// objective chain arms them, and flying one cone starts the drop cutscene, which completes the
    /// primary objective gated on it.</summary>
    // BL-467: nothing read landings.zrd, so no mission could ever play the cutscene an
    // ANIM_STATE objective waits on.
    [Suite("landings-approach-trigger",
        "the mid-mission cutscene trigger over the first story mission's BUILT world: the "
        + "chapter's landings.zrd rows resolve to the cone, half-cone and sphere volumes their "
        + "approach nodes author, a mission carrying none of the animations resolves none of "
        + "them, the drop-off rows start disarmed and fire nothing, the mission's own objective "
        + "chain arms them, and flying one cone starts the drop cutscene through the cutscene "
        + "host, runs it to EXECUTED and completes the primary objective gated on it")]
    internal static void LandingApproachTrigger(TestContext ctx) =>
        DriveMission(ctx, FirstSeq, "test-landing-approach", Drive);

    /// <summary>Drives CM02's wing-walk capture gate against its BUILT world with its own roster
    /// spawned: three symmetric Balmoral rows, each authored under its plane's gamez node, grafted
    /// onto the rig the roster spawns so all three bind; one pair of definitions switching all
    /// three <c>land_on</c> nodes by gamez index, which arms and un-arms the rows it now reaches;
    /// an arming objective that waits on those planes' aiv group being down to one; and a driven
    /// approach at an armed Balmoral starting the capture.</summary>
    // BL-492/BL-495: CM02's wing-walk capture, whose approach rows the mission gates on a
    // land_on node state switched for all three Balmorals at once, and whose approach nodes
    // reach the world only on the rigs the roster spawns.
    [Suite("landings-wingwalk-gate",
        "CM02's Balmoral capture gate over its BUILT world with its own roster spawned: the "
        + "mission's three approach rows resolve and differ only by index, the world build "
        + "alone reaches none of them, the roster spawn grafts each onto the rig its block "
        + "spawned so all five rows bind, one pair of definitions arms and un-arms all three "
        + "land_on nodes by gamez index, the objective calling the arming one waits on those "
        + "planes' aiv group being down to one, and a driven approach at an armed Balmoral "
        + "starts the capture where the same approach before the gate starts nothing")]
    internal static void WingWalkCaptureGate(TestContext ctx) =>
        DriveMission(ctx, WingWalkSeq, "test-wingwalk-gate", DriveWingWalk);

    /// <summary>Drives CM07's caboose pickup through its range-triggered start animation: the
    /// passenger's library-root rig appears on the train, the train's own pickup timing opens the
    /// late approach cone, and flying that cone starts the pickup cutscene and clears its objective.</summary>
    [Suite("landings-train-pickup-gate",
        "CM07's caboose pickup through its real range-triggered mission path: approaching the "
        + "train stages the passenger and flare rig, ladder sensor and docking cone from their "
        + "library root, the train's own pickup_timing opens land_on in its first flyable "
        + "phase, the landing trigger discovers that late-created cone, and flying it starts "
        + "the hosted pickup cutscene beside the caboose, faces the passenger, runs to handoff "
        + "and clears the primary pickup objective")]
    internal static void TrainPickupGate(TestContext ctx) =>
        DriveMission(ctx, TrainPickupSeq, "test-train-pickup-gate", DriveTrainPickup);

    /// <summary>Drives CM07's caboose pickup the way the original runs it: the staged passenger
    /// rides the moving train as its child, waves with the lit flare once the pickup timing opens
    /// the switch, the rope ladder drops on a level approach inside the sensor, and the pickup
    /// cutscene's call to <c>caboosepickup</c> holds a live instance for the person's climb.</summary>
    [Suite("landings-train-pickup-ride",
        "CM07's caboose pickup as the original runs it, over the mission's real moving train: "
        + "the staged passenger is the caboose's child and keeps its offset while the caboose "
        + "travels, the pickup timing opening the switch selects the wave with the lit flare "
        + "and its smoke trail laying sprites on that passenger's own hand, a level aircraft "
        + "inside the 100 m sensor drops the rope ladder and the drop's own callback settles it "
        + "deployed swinging on its looped wind script, and the pickup cutscene's call to "
        + "caboosepickup holds a live instance for the person's climb while cabpkup_ladder "
        + "swings the rungs he climbs")]
    internal static void TrainPickupRide(TestContext ctx)
    {
        // The harness retires the world's puffer factory after the build, where a game session
        // keeps it (WorldSession.Options.TexturesOutliveBuild). So the flare trail asserted on the
        // passenger's hand at run time is counted by a fake instead of dropped.
        ctx.EmitterFactory = new CountingEmitterFactory();
        DriveMission(ctx, TrainPickupSeq, "test-train-pickup-ride", DriveTrainPickupRide);
    }

    /// <summary>Drives CM11's trailer pickup through the objective script's own <c>WAKE_ANIM</c>:
    /// the dock objective's definition stages the approach cone from its library root under the
    /// trailer's sensor, the landing trigger discovers it, and flying that cone starts the pickup
    /// cutscene and clears the dock objective.</summary>
    [Suite("landings-trailer-pickup-gate",
        "CM11's trailer pickup through the objective script's own WAKE_ANIM: the dock "
        + "objective's definition stages the approach cone from its library root under the "
        + "trailer's sensor, the landing trigger discovers that late-created cone, and flying "
        + "it starts the hosted pickup cutscene, runs to handoff and clears the dock objective")]
    internal static void TrailerPickupGate(TestContext ctx) =>
        DriveMission(ctx, TrailerPickupSeq, "test-trailer-pickup-gate", DriveTrailerPickup);

    /// <summary>Drives CM16's armoured-car pickup, the one pickup whose own film destroys the
    /// thing the mission is watching: <c>carpkup_player</c> calls <c>destroy_car01</c> ten seconds
    /// after its own script, and the pickup only calls <c>got_sparks</c> once the climb's
    /// <c>WAIT_FOR_COMPLETION</c> releases, 4.85 s later. Both beats are pinned against the
    /// authored script lengths, the row that started the episode must not fire again underneath
    /// it, and the film's completion code is what wins the mission.</summary>
    [Suite("landings-car-pickup-credit",
        "CM16's armoured-car pickup, the one whose own film destroys what the mission is "
        + "watching: the mission's chain stages the cone and the waving passenger, flying it "
        + "starts the pickup, the row does not fire again underneath the episode, the film "
        + "calls destroy_car01 on its authored beat and reaches got_sparks on its own, and the "
        + "mission-completion code it raises wins the mission")]
    internal static void CarPickupCredit(TestContext ctx) =>
        DriveMission(ctx, CarPickupSeq, "test-car-pickup-credit", DriveCarPickup);

    /// <summary>Drives the auto-land button over the campaign's first mission's BUILT world: flying
    /// into the chapter's <c>auto</c> row lights <see cref="LandingApproachRuntime.AutoLandOffered"/>
    /// but starts nothing on its own, pressing the button starts the row's animation the way the
    /// manual row would, and holding the button past the handoff does not re-fire it.</summary>
    [Suite("landings-auto-land-button",
        "the auto-land button over the first story mission's BUILT world: flying the chapter's "
        + "auto row lights AutoLandOffered but starts nothing while the button is up, a realtime "
        + "frame of the flown rig's own _Process draws the prompt on its own centred line in the "
        + "message table's own wording with this seat's control in it, pressing the button "
        + "starts the same animation the manual row would, the cutscene host still runs it, and "
        + "holding the button past the handoff does not re-fire the row")]
    internal static void AutoLandButton(TestContext ctx) =>
        DriveMission(ctx, FirstSeq, "test-autoland-button", DriveAutoLand);

    /// <summary>Drives the campaign's first mission's own <c>auto</c> row with two humans flying:
    /// the prompt is offered per pane, the row starts once however many humans stand in its sphere,
    /// and the episode belongs to the human who pressed rather than to player 1.</summary>
    [Suite("campaign-coop-approach-row",
        "the first story mission's own auto row flown by two humans: the guest inside the "
        + "sphere is offered the prompt in their own pane while the scripted player a kilometre "
        + "out is not and their held button starts nothing, the guest's press starts the row "
        + "and the episode belongs to the guest rather than to player 1, and a second human "
        + "standing in the same volume is not a second entry into it")]
    internal static void CampaignCoopApproachRow(TestContext ctx) =>
        DriveMission(ctx, FirstSeq, "test-campaign-coop-approach-row", DriveCoopApproachRow);

    /// <summary>Drives the same auto row with two humans in the same airframe, the guest pressing.
    /// The docking hook the episode extends is the landing pilot's own. The airframe the fork's
    /// arms resolve by name follows the episode owner, not the seat staged when the rigs were
    /// bound.</summary>
    // The hook extended on the scripted player's aeroplane while the guest docked beside it with
    // none. Both airframes carry the same node names, and only seat 0's was in the table.
    [Suite("campaign-coop-hookup-seat",
        "the first story mission's auto row flown by two humans in the same airframe, the guest "
        + "pressing: the node table carries the scripted player's airframe until a row is flown, "
        + "the guest's episode puts the guest's own airframe there in its place so the "
        + "extend-hook fork's arms cannot resolve the other seat's aeroplane, and the episode "
        + "ends with the guest's own docking hook extended at the mount offset the fork authors "
        + "for that airframe while the scripted player's hook is still retracted and their "
        + "aeroplane unmoved. The fork and the branch that swings the arms each play once rather "
        + "than once per seat, and every callback the landing raises is raised a single time")]
    internal static void CampaignCoopHookupSeat(TestContext ctx) =>
        DriveMission(ctx, FirstSeq, "test-campaign-coop-hookup-seat", DriveCoopHookupSeat);

    /// <summary>Drives the hookup on three airframes and reads what it did to each: the flown
    /// aircraft's own subtree is in the runtime's node table, so the definition's per-airframe
    /// branches are decidable, and the episode ends with that airframe's docking hook extended, its
    /// authored mount offset applied, and its wings folded where the airframe authors a fold.
    /// </summary>
    // The hookup plays with no hook, the aeroplane too high and the wings unfolded when the
    // flown airframe's own subtree is not in the runtime's node table.
    [Suite("landings-hookup-airframe",
        "the zeppelin hookup on three airframes, one per shape of the extend-hook fork, over "
        + "the first story mission's BUILT world, "
        + "every value read from the aircraft archive's own definitions: the flown aircraft is "
        + "in the animation runtime's node table so the hookup's per-airframe branches can read "
        + "its active bit, it carries its own docking-hook group built retracted, and the "
        + "episode ends with that hook extended, the airframe hung at the mount offset the "
        + "extend-hook definition authors for it, and its wings turned to the angles its own "
        + "fold definition authors where the airframe has one, having swung that hook once. "
        + "Every airframe the shared fork branches on builds its hook group parked, and the "
        + "loaded program holds one definition per hook name, so a call cannot start the same "
        + "swing twice; the episode's every start is counted by name, the airframe's own "
        + "extend-hook branch among them rather than only the shared fork. The park that seeds "
        + "those arms reaches every node its own extend definition authors a FROM pose for, and "
        + "reaches each one through that definition's compiled symbol table rather than by name, "
        + "read off the verdict the park itself recorded")]
    internal static void HookupAirframe(TestContext ctx) =>
        DriveMission(ctx, FirstSeq, "test-hookup-airframe", DriveHookupAirframe);

    /// <summary>Drives CM06's docking onto the Workers' Voyage, the one shipped row whose
    /// definition raises no code of its own and calls the ones that do: the episode belongs to the
    /// row's definition rather than the callee that raised the first code, control stays locked
    /// from the hookup to the authored handoff, and the aeroplane is left where the re-placement
    /// code put it rather than teleported when the definition runs out.</summary>
    // The docking cutscene handed the player flight back when its first callee ended and
    // teleported them when the row's definition ran out, because the episode was booked to
    // whichever definition raised the first code.
    [Suite("landings-docking-hold",
        "CM06's docking onto the Workers' Voyage over its BUILT world, the one shipped row "
        + "whose definition raises no code of its own: the episode belongs to the row the "
        + "trigger started rather than the hookup callee that raised the first code, the "
        + "player is held out of flight from the hookup through the drop to the unhook's own "
        + "handoff code although the row's definition ends before it on a trailing wait, the "
        + "host hands the session back on that code, the released aeroplane flies out of the "
        + "player marker's pose where the re-placement code read it, and no frame after the "
        + "release teleports it. The hookup calls its drop and pickup legs unconditionally and "
        + "only each leg's own REQUIRED node state keeps the wrong one off, so all three fork "
        + "paths resolve to nodes this world built (one resolving to none would pass the gate "
        + "vacuously), the drop leg runs on the first docking while the two pickup legs do "
        + "not, and nothing the row reaches plays twice (BL-525, BL-628)")]
    internal static void DockingHold(TestContext ctx) =>
        DriveMission(ctx, DockingSeq, "test-docking-hold", DriveDockingHold);

    /// <summary>Drives CM02's own ending, the docking the capture hands over to: the player arrives
    /// on the pirate zeppelin in the Balmoral it just took, so the hookup runs the one branch that
    /// folds a wing. The episode has to hold through that branch's authored turn, both wings reach
    /// the angle the fold authors, and the mission-completion code lands at its end rather than
    /// before it.</summary>
    [Suite("landings-balmoral-dock",
        "CM02's own ending over C3/M05's BUILT world: the docking row flown in the Balmoral the "
        + "capture hands over, whose branch of the shared hookup is the only one that folds a "
        + "wing. The episode holds through that branch's authored two-second turn, both wings "
        + "reach the angle the fold authors, and the mission-completion code lands at its end "
        + "while the definition is still running, so it beats the objective watching that same "
        + "definition for EXECUTED and wins the mission on its own frame, once, with a later "
        + "completion code changing nothing; the world then stands still for the whole leaving "
        + "hold, taking no aeroplane step, no animation advance and no stick, so the film's "
        + "last live frame is the code's own and the session goes to the cabin two seconds "
        + "after it rather than on it")]
    internal static void BalmoralDock(TestContext ctx) =>
        DriveMission(ctx, WingWalkSeq, "test-balmoral-dock", DriveBalmoralDock);

    /// <summary>Drives CM07's zeppelin-hangar drop, the mission's other cutscene: the depot chain
    /// reaction's <c>CALL_ANIMATION</c> only ARMS it, because the definition is range-gated;
    /// reaching the hangar runs it; and the authored <c>RESET_STATE</c> at the handoff is what
    /// clears the objective node the mission gates "Fly Through Zeppelin Hangar" on.</summary>
    [Suite("landings-hangar-drop-gate",
        "CM07's zeppelin-hangar drop over its BUILT world, every name read from the mission's "
        + "own data: the one cutscene definition it range-gates, the ambient definition that "
        + "calls it, and the objective node the drop's RESET_STATE clears. The call ARMS the "
        + "drop without running it while the player is outside the authored band, reaching the "
        + "hangar runs it and hands it to the cutscene host, and the reset block at the handoff "
        + "clears that node, which is what completes the fly-through objective")]
    internal static void HangarDropGate(TestContext ctx) =>
        DriveMission(ctx, TrainPickupSeq, "test-hangar-drop-gate", DriveHangarDrop);

    // The world build every landings suite needs: the story mission at this sequence position, its
    // objective script, an in-memory campaign profile, and the cutscene roots the definitions pose.
    private static void DriveMission(
        TestContext ctx,
        int seq,
        string artifactPrefix,
        MissionDrive drive)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var mission = MissionOf(CampaignSequence.Load(ctx.ZrdrPath), seq)
            ?? throw new SuiteSkippedException($"cm_sequence carries no story position {seq}");
        string chapter = mission.ChapterFolder.ToUpperInvariant();
        string folder = mission.MissionFolder.ToUpperInvariant();
        string missionZrdr = SessionPaths.MissionZrdr(ctx.DataRoot, chapter, folder);
        ctx.RequireData(missionZrdr, $"{chapter}/{folder} zrdr");
        ctx.RequireData(SessionPaths.ChapterTextures(ctx.DataRoot, chapter), $"{chapter} textures");

        var script = ObjectiveScript.Load(missionZrdr);
        var report = new StringBuilder();
        report.AppendLine($"seq {seq} -> {chapter}/{folder}");
        var profile = CampaignProfileDef.NewProfile("Zachary");
        var director = CampaignDirector.Create(script, mission, profile, null);
        ctx.ExtraPrewarmSoundNames = script.SoundGroupNames();
        // ⚠ The world has to carry the cutscene roots or nothing here sees the defect this suite
        // exists for: with no `camera1` the drop's own camera events resolve to nothing, the
        // definition reports zero length and the episode is over the frame it starts.
        ctx.CutsceneRoots = true;
        try
        {
            ctx.WithWorld(chapter, collision: false, folder,
                world => drive(ctx, world, director, script, missionZrdr, report));
        }
        finally
        {
            ctx.CutsceneRoots = false;
        }

        ctx.WriteArtifact($"{artifactPrefix}-{chapter}-{folder}.txt", report.ToString());
        ctx.Note($"drove {chapter}/{folder}'s landings.zrd approach triggers against a built world");
    }

    private static void Drive(TestContext ctx, TestWorld world, CampaignDirector director,
        ObjectiveScript script, string missionZrdr, StringBuilder report)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, world.Chapter);
        var armed = LandingApproaches.Resolve(
            chapterZrdr, world.Gamez, name => world.Runtime.Handles(name));
        foreach (var approach in armed)
        {
            report.AppendLine($"row anim='{approach.Anim}' node='{approach.Node}' " +
                $"{approach.Shape} r={approach.Radius:0.0} auto={approach.Auto} " +
                $"angle={Mathf.RadToDeg(approach.AngleRad):0.#} " +
                $"speed={approach.MinSpeedMps:0.#}..{approach.MaxSpeedMps:0.#} m/s");
        }

        ctx.Check(armed.Count > 0, $"the chapter's landings.zrd resolves rows this mission can run");
        ctx.Same(0, LandingApproaches.Resolve(chapterZrdr, world.Gamez, _ => false).Count,
            $"and a mission carrying none of them resolves nothing, which keeps Instant Action out");
        CheckShapes(ctx, armed, report);
        WithTrigger(ctx, world, director, armed, (trigger, cutscene, rig, graph) =>
        {
            ctx.Same(armed.Count, trigger.Armed, $"every resolved row binds to a node this world built");
            RunTheDrop(ctx, world, graph, script, armed, trigger, cutscene, rig, report);
        });
    }

    private static void DriveAutoLand(TestContext ctx, TestWorld world, CampaignDirector director,
        ObjectiveScript script, string missionZrdr, StringBuilder report)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, world.Chapter);
        var armed = LandingApproaches.Resolve(
            chapterZrdr, world.Gamez, name => world.Runtime.Handles(name));
        if (AutoFor(armed) is not { } auto)
        {
            ctx.Check(false, $"the chapter's landings.zrd carries an auto row to fly");
            return;
        }

        WithTrigger(ctx, world, director, armed, (trigger, cutscene, rig, graph) =>
            RunAutoLandButton(ctx, world, graph, script, trigger, cutscene, rig, auto, report));
    }

    private static void DriveCoopApproachRow(TestContext ctx, TestWorld world, CampaignDirector director,
        ObjectiveScript script, string missionZrdr, StringBuilder report)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, world.Chapter);
        var armed = LandingApproaches.Resolve(
            chapterZrdr, world.Gamez, name => world.Runtime.Handles(name));
        if (AutoFor(armed) is not { } auto)
        {
            ctx.Check(false, $"the chapter's landings.zrd carries an auto row to fly");
            return;
        }

        WithCoopTrigger(ctx, world, director, armed, (trigger, cutscene, humans, graph) =>
            RunCoopAutoLand(ctx, world, graph, script, trigger, cutscene, humans, auto, report));
    }

    private static void DriveCoopHookupSeat(TestContext ctx, TestWorld world, CampaignDirector director,
        ObjectiveScript script, string missionZrdr, StringBuilder report)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, world.Chapter);
        var armed = LandingApproaches.Resolve(
            chapterZrdr, world.Gamez, name => world.Runtime.Handles(name));
        if (AutoFor(armed) is not { } auto)
        {
            ctx.Check(false, $"the chapter's landings.zrd carries an auto row to fly");
            return;
        }

        var stage = world.Session.Aircraft;
        report.AppendLine($"aircraft stage: {(stage != null ? $"base {stage.PointerBase}" : "none")}");
        ctx.Check(stage != null,
            $"the mission stages the aircraft archive, which is the frame the hookup poses in");
        if (stage == null)
        {
            return;
        }

        WithCoopTrigger(ctx, world, director, armed, (trigger, cutscene, humans, graph) =>
            RunCoopHookupSeat(ctx, world, graph, script, trigger, cutscene, humans, auto, report),
            aircraft: stage);
    }

    // Two humans in the same airframe, so which aeroplane the fork's arm resolves is the whole
    // question. Both carry the same `player_<airframe>` node name, and the table holds one of them.
    private static void RunCoopHookupSeat(
        TestContext ctx, TestWorld world, ObjectiveGraph graph, ObjectiveScript script,
        LandingApproachRuntime trigger, CutsceneController cutscene, IReadOnlyList<PlayerRig> humans,
        LandingApproach auto, StringBuilder report)
    {
        if (MountBranch(world, PlaneNode) is not { } branch)
        {
            ctx.Check(false, $"'{ExtendHookAnim}' authors a branch for '{PlaneNode}'");
            return;
        }

        if (humans[0].Controller?.PlaneModel is not { } scripted
            || humans[1].Controller?.PlaneModel is not { } landing)
        {
            ctx.Check(false, $"both seats built a '{PlaneNode}' model to tell apart");
            return;
        }

        string hookAnim = branch.HookAnim ?? "";
        var scriptedHook = hookAnim.Length > 0 ? NamedIn(world, hookAnim, scripted) : null;
        var landingHook = hookAnim.Length > 0 ? NamedIn(world, hookAnim, landing) : null;
        var scriptedAt = scripted.Position;
        report.AppendLine($"mount offset {branch.Offset}, hook '{hookAnim}', " +
            $"P1 group {(scriptedHook != null ? "built" : "absent")}, " +
            $"P2 group {(landingHook != null ? "built" : "absent")}, P1 airframe at {scriptedAt}");
        ctx.Check(scriptedHook != null && landingHook != null,
            $"both seats carry their own '{hookAnim}' hook group, so either could be the one that swings");

        for (float t = 0f; t < IntroSettleS; t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            cutscene.Tick();
        }

        ArmRow(ctx, world, graph, script, auto, report);
        var frame = world.Runtime.FindNodes(auto.Node)[0].GlobalTransform;
        var inside = frame * Lerp(auto, AxisFraction);
        var aim = frame * auto.Apex;
        var away = inside + (Vector3.Up * CoopAwayM);
        Park(ctx, humans[0], away, away + Vector3.Forward);
        Park(ctx, humans[1], inside, aim);

        // The staging a mission that has flown no row leaves is the scripted player's. That is the
        // aeroplane every intro poses, and the one a single-seat session never leaves.
        report.AppendLine($"before the press, '{PlaneNode}' resolves to " +
            $"{Resolved(world, PlaneNode, scripted, landing)}");
        ctx.Check(Reaches(world, PlaneNode, scripted),
            $"the node table carries the scripted player's airframe before any row is flown");

        var plays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Count(AnimDefinition def, Node3D? anchor)
        {
            if (def.AnimName is { Length: > 0 } name)
            {
                plays[name] = plays.TryGetValue(name, out int n) ? n + 1 : 1;
            }
        }

        // The codes the episode raises, read in front of the host. A landing's own callbacks belong
        // to the one landing, and a second seat in the world must not double any of them.
        var codes = new List<int>();
        var host = world.Runtime.CallbackHost;
        world.Runtime.CallbackHost = (code, anim, root) =>
        {
            codes.Add(code);
            return cutscene.Host(code, anim, root);
        };
        world.Runtime.OnInstanceStarted += Count;
        var started = new List<string>();
        try
        {
            humans[1].Controller!.AutoLand = true;
            for (int i = 0; i < RestartFrames && !cutscene.Playing; i++)
            {
                Step(world, trigger, cutscene, graph, 1);
            }

            report.AppendLine($"guest pressed: started='{trigger.LastStarted ?? "(none)"}' by " +
                $"P{(trigger.LastStartedBy ?? -1) + 1}, episode owner=" +
                $"P{(cutscene.EpisodeOwner?.Index ?? -1) + 1}");
            ctx.Check(cutscene.Playing, $"the guest's press starts '{auto.Anim}' under the host");
            ctx.Check(ReferenceEquals(cutscene.EpisodeOwner, humans[1]),
                $"and the episode belongs to the guest who flew it");

            // The claim the whole leg rests on, read while the episode is live. The arms resolve
            // the airframe by name, and that one name has to reach the landing pilot's aeroplane.
            report.AppendLine($"with the episode live, '{PlaneNode}' resolves to " +
                $"{Resolved(world, PlaneNode, scripted, landing)}");
            ctx.Check(Reaches(world, PlaneNode, landing),
                $"the episode puts the landing pilot's own airframe in the node table, which is what the fork's arms read");
            ctx.Check(!Reaches(world, PlaneNode, scripted),
                $"…and takes the other seat's out of it, so no arm of the fork can resolve the aeroplane that is not landing");

            for (float t = 0f; t < PlayBudgetS && cutscene.Playing; t += StepDt)
            {
                Step(world, trigger, cutscene, graph, 1);
            }

            foreach (string name in plays.Keys)
            {
                started.Add(name);
            }

            report.AppendLine($"episode over: playing={cutscene.Playing}, " +
                $"{plays.Count} definition(s) started");
            ctx.Check(!cutscene.Playing, $"and the episode completes within budget");
            report.AppendLine($"after the episode: P2 mount {landing.Position} hook " +
                $"visible={landingHook?.Visible}; P1 mount {scripted.Position} hook " +
                $"visible={scriptedHook?.Visible}");
            ctx.Check(landingHook is { Visible: true },
                $"the hookup extends the landing pilot's own docking hook");
            ctx.Check(Near(landing.Position, branch.Offset),
                $"and hangs their airframe at the offset '{ExtendHookAnim}' authors for it, {branch.Offset}");
            ctx.Check(scriptedHook is not { Visible: true },
                $"while the other seat's hook is still retracted, which is the defect this covers");
            ctx.Check(Near(scripted.Position, scriptedAt),
                $"and their aeroplane is where it was, never hung on the trapeze in the landing pilot's place");
            report.AppendLine($"hook legs: '{ExtendHookAnim}' x{Played(plays, ExtendHookAnim)}, " +
                $"'{hookAnim}' x{Played(plays, hookAnim)}; codes [{string.Join(", ", codes)}]");
            ctx.Same(1, Played(plays, ExtendHookAnim),
                $"'{ExtendHookAnim}' plays once for the one landing, not once per seat");
            ctx.Same(1, Played(plays, hookAnim),
                $"…and '{hookAnim}', the branch whose motions swing the arms, a single time");
            // The row ends the mission rather than handing flight back, so its last code is the
            // completion one. Every code it raises is raised once, because a landing's callbacks
            // belong to the landing and a second seat is not a second landing.
            var doubled = codes.FindAll(c => codes.FindAll(x => x == c).Count > 1);
            ctx.Same(1, codes.FindAll(c => c == CompleteCode).Count,
                $"and the landing's own completion callback fires once");
            ctx.Same(0, doubled.Count,
                $"…with no callback of this landing raised twice ({string.Join(", ", doubled)})");
        }
        finally
        {
            world.Runtime.CallbackHost = host;
            world.Runtime.OnInstanceStarted -= Count;
            // ⚠ Both rigs are freed when this leg returns, and a motion still running on either
            // one's nodes would tick into a disposed object.
            foreach (string anim in started)
            {
                world.Runtime.Stop(anim);
            }
        }
    }

    private static int Played(IReadOnlyDictionary<string, int> plays, string anim) =>
        plays.TryGetValue(anim, out int n) ? n : 0;

    // Does the runtime's node table reach this model under that name? The fork's arms ask exactly
    // this, so the reading is the resolver's own answer rather than the model's parentage.
    private static bool Reaches(TestWorld world, string nodeName, Node3D model)
    {
        foreach (var found in world.Runtime.FindNodes(nodeName))
        {
            if (ReferenceEquals(found, model))
            {
                return true;
            }
        }

        return false;
    }

    private static string Resolved(TestWorld world, string nodeName, Node3D scripted, Node3D landing)
    {
        var who = new List<string>();
        foreach (var found in world.Runtime.FindNodes(nodeName))
        {
            who.Add(ReferenceEquals(found, scripted) ? "P1"
                : ReferenceEquals(found, landing) ? "P2" : "another node");
        }

        return who.Count > 0 ? string.Join(", ", who) : "nothing";
    }

    // Two humans over one auto row: the prompt is drawn in the pane of whoever is inside the sphere,
    // the row starts on the human who presses rather than on player 1, and standing a second human
    // in the same sphere is not a second entry into it.
    private static void RunCoopAutoLand(
        TestContext ctx, TestWorld world, ObjectiveGraph graph, ObjectiveScript script,
        LandingApproachRuntime trigger, CutsceneController cutscene, IReadOnlyList<PlayerRig> humans,
        LandingApproach auto, StringBuilder report)
    {
        var nodes = world.Runtime.FindNodes(auto.Node);
        ctx.Check(nodes.Count > 0, $"the auto row's own node '{auto.Node}' is built");
        if (nodes.Count == 0)
        {
            return;
        }

        ArmRow(ctx, world, graph, script, auto, report);
        var frame = nodes[0].GlobalTransform;
        var inside = frame * Lerp(auto, AxisFraction);
        var aim = frame * auto.Apex;
        var away = inside + (Vector3.Up * CoopAwayM);

        // The guest alone in the sphere, the scripted player a kilometre above it: the prompt is a
        // per-pane surface, so the human outside sees nothing and their button does nothing.
        Park(ctx, humans[0], away, away + Vector3.Forward);
        Park(ctx, humans[1], inside, aim);
        humans[0].Controller!.AutoLand = true;
        Step(world, trigger, cutscene, graph, RestartFrames);
        report.AppendLine($"guest alone inside: offered P1={trigger.OffersAutoLandTo(0)} " +
            $"P2={trigger.OffersAutoLandTo(1)}, started='{trigger.LastStarted ?? "(none)"}'");
        ctx.Check(trigger.OffersAutoLandTo(1),
            $"the guest inside '{auto.Node}' is offered the auto-land in their own pane");
        ctx.Check(!trigger.OffersAutoLandTo(0),
            $"…and the scripted player {away.DistanceTo(inside):0} m out is not, so the prompt is per pane");
        ctx.Check(trigger.LastStarted == null,
            $"and the human outside the sphere holding the button down starts nothing");

        // Both inside, and the one who presses is the guest: the row is started by the human who
        // satisfied it, which is what the episode then belongs to.
        Park(ctx, humans[0], inside, aim);
        humans[0].Controller!.AutoLand = false;
        Step(world, trigger, cutscene, graph, 1);
        report.AppendLine($"both inside: offered P1={trigger.OffersAutoLandTo(0)} " +
            $"P2={trigger.OffersAutoLandTo(1)}, started='{trigger.LastStarted ?? "(none)"}'");
        ctx.Check(trigger.OffersAutoLandTo(0) && trigger.OffersAutoLandTo(1),
            $"with both humans in the sphere both panes carry the prompt");

        humans[1].Controller!.AutoLand = true;
        Step(world, trigger, cutscene, graph, RestartFrames);
        report.AppendLine($"guest pressed: started='{trigger.LastStarted ?? "(none)"}' by " +
            $"P{(trigger.LastStartedBy ?? -1) + 1}, episode owner=" +
            $"P{(cutscene.EpisodeOwner?.Index ?? -1) + 1}, playing={cutscene.Playing}");
        ctx.Check(trigger.LastStarted != null, $"the guest's press starts '{auto.Anim}'");
        ctx.Same(1, trigger.LastStartedBy ?? -1,
            $"…started by the guest who pressed it, not by the scripted player beside them");
        ctx.Check(ReferenceEquals(cutscene.EpisodeOwner, humans[1]),
            $"and the episode belongs to that guest, which is the rig its own codes will act on");

        // Both are still inside and both are now pressing: a second human in the volume is not a
        // second entry into it, and the row is latched until the volume empties.
        string started = trigger.LastStarted!;
        humans[0].Controller!.AutoLand = true;
        Step(world, trigger, cutscene, graph, RestartFrames);
        report.AppendLine($"held down by both: started='{trigger.LastStarted ?? "(none)"}' by " +
            $"P{(trigger.LastStartedBy ?? -1) + 1}");
        ctx.Same(1, trigger.LastStartedBy ?? -1,
            $"the row does not re-fire for the second human standing in the same volume");
        ctx.Check(string.Equals(trigger.LastStarted, started, StringComparison.Ordinal),
            $"…and '{started}' is still the last thing it started");
    }

    // Parks one human where it is asked, stopped: every gate this leg drives is a position and an
    // attitude, so a flown approach would only add a speed the row's band has to be re-checked for.
    private static void Park(TestContext ctx, PlayerRig rig, Vector3 at, Vector3 lookAt) =>
        rig.Controller!.Setup(new FlightModel(PlaneStats.Load(ctx.ZrdrPath, PlaneNode)), null,
            new CamParams(), at, lookAt, ApproachThrottle, ApproachSpeedMps);

    private static void Step(TestWorld world, LandingApproachRuntime trigger,
        CutsceneController cutscene, ObjectiveGraph graph, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            world.Runtime.Advance(StepDt);
            trigger.Tick();
            cutscene.Tick();
            graph.Step(StepDt);
        }
    }

    // Arms the row the mission's own way (whatever objective's WAKE_ANIM writes its land_on),
    // flies the sphere with the button up (offered, but inert), presses it (starts the SAME
    // animation the manual row would), then holds it past the handoff to prove the row does not
    // re-fire while the aircraft is still parked inside it, the manual row's own guard, reused.
    private static void RunAutoLandButton(
        TestContext ctx, TestWorld world, ObjectiveGraph graph, ObjectiveScript script,
        LandingApproachRuntime trigger, CutsceneController cutscene, FlightController rig,
        LandingApproach auto, StringBuilder report)
    {
        var nodes = world.Runtime.FindNodes(auto.Node);
        ctx.Check(nodes.Count > 0, $"the auto row's own node '{auto.Node}' is built");
        if (nodes.Count == 0)
        {
            return;
        }

        ArmRow(ctx, world, graph, script, auto, report);

        var frame = nodes[0].GlobalTransform;
        var centre = frame * auto.Apex;
        report.AppendLine($"'{auto.Node}' sphere centre world pos=({centre.X:0},{centre.Y:0}," +
            $"{centre.Z:0}) r={auto.Radius:0.#}");
        rig.Setup(new FlightModel(PlaneStats.Load(ctx.ZrdrPath, PlaneNode)), null, new CamParams(),
            frame * Lerp(auto, AxisFraction), frame * auto.Apex, ApproachThrottle, ApproachSpeedMps);

        bool offered = false;
        for (int i = 0; i < RestartFrames; i++)
        {
            rig.SimStep(StepDt);
            world.Runtime.Advance(StepDt);
            trigger.Tick();
            cutscene.Tick();
            offered |= trigger.AutoLandOffered;
        }

        report.AppendLine($"auto row offered={offered} with the button up, started=" +
            $"'{trigger.LastStarted ?? "(none)"}'");
        ctx.Check(offered, $"flying into '{auto.Node}' lights AutoLandOffered");
        ctx.Check(trigger.LastStarted == null, $"and starts nothing while the button is up");

        // Nothing above this line calls the flown rig's OWN _Process, so a suite this shape never
        // exercises GameSession's feed or FlightHud's draw (INSTR-26: drive the node's own callback
        // under a Realtime clock rather than SimStep). Mirror that one feed line, then let it draw.
        var savedClock = GameClock.Current;
        GameClock.Current = new GameClock { Mode = GameClock.RunMode.Realtime };
        try
        {
            rig.AutoLandOffered = trigger.AutoLandOffered;
            for (int i = 0; i < RestartFrames; i++)
            {
                rig._Process(StepDt);
            }
        }
        finally
        {
            GameClock.Current = savedClock;
        }

        report.AppendLine($"drawn on a realtime frame: DrawsTextBlock={rig.PilotHud.DrawsTextBlock}, " +
            $"text='{rig.PilotHud.DrawnText}', prompt='{rig.PilotHud.AutoDock?.Line}'");
        ctx.Check(rig.PilotHud.DrawsTextBlock, $"the flown pane still holds a text block to draw into");
        // The shipped wording out of messages.json, not a stand-in: this rig reads no keyboard, so
        // MSG_PRESS_AUTOLAND's %1 carries its pad control. It lands on the prompt's own centred
        // line, never in the text block.
        string wanted = Messages.Load(ctx.MessagesPath).Format("MSG_PRESS_AUTOLAND", "Pad Left Stick");
        ctx.Check(rig.PilotHud.AutoDock?.Line == wanted,
            $"…and a realtime frame actually puts '{wanted}' on the prompt's own line");
        ctx.Check(rig.PilotHud.DrawnText is { Length: > 0 } drawn && !drawn.Contains(wanted),
            $"…with the text block carrying none of it: '{rig.PilotHud.DrawnText}'");

        rig.AutoLand = true;
        for (int i = 0; i < RestartFrames && !cutscene.Playing; i++)
        {
            rig.SimStep(StepDt);
            world.Runtime.Advance(StepDt);
            trigger.Tick();
            cutscene.Tick();
        }

        report.AppendLine($"button pressed: started='{trigger.LastStarted}', playing={cutscene.Playing}");
        ctx.Check(trigger.LastStarted == auto.Anim,
            $"pressing the button starts '{auto.Anim}', the row's own animation");
        ctx.Check(cutscene.Playing, $"which the cutscene host runs the same as the manual row's");

        float played = 0f;
        for (float t = 0f; t < PlayBudgetS && cutscene.Playing; t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            trigger.Tick();
            cutscene.Tick();
            graph.Step(StepDt);
            played += StepDt;
        }

        report.AppendLine($"episode ran {played:0.##} s before handoff, playing={cutscene.Playing}");
        ctx.Check(!cutscene.Playing, $"and the auto-land episode completes within budget");
        CheckNoRestart(ctx, world, trigger, cutscene, graph, rig, auto, report);
    }

    // The hookup as the aircraft archive authors it: one branch per airframe in the extend-hook
    // definition, and one more inside the hookup's own move_player sequence for the airframes that
    // fold their wings. Every name below is read out of those definitions.
    private static void DriveHookupAirframe(TestContext ctx, TestWorld world, CampaignDirector director,
        ObjectiveScript script, string missionZrdr, StringBuilder report)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, world.Chapter);
        var armed = LandingApproaches.Resolve(
            chapterZrdr, world.Gamez, name => world.Runtime.Handles(name));
        if (AutoFor(armed) is not { } auto)
        {
            ctx.Check(false, $"the chapter's landings.zrd carries an auto row to fly");
            return;
        }

        var stage = world.Session.Aircraft;
        report.AppendLine($"aircraft stage: {(stage != null ? $"base {stage.PointerBase}" : "none")}");
        ctx.Check(stage != null,
            $"the mission stages the aircraft archive, which is the frame the hookup poses in");
        if (stage == null)
        {
            return;
        }

        CheckHooksParked(ctx, world, report);
        foreach (var plane in HookupPlanes)
        {
            report.AppendLine($"--- {plane} ---");
            WithTrigger(ctx, world, director, armed,
                (trigger, cutscene, rig, graph) => RunHookupAirframe(
                    ctx, world, graph, script, trigger, cutscene, rig, auto, plane, report),
                planeNode: plane, aircraft: stage);
        }
    }

    // Arms the auto row, flies it and presses the button (the path landings-auto-land-button
    // already proves), then reads what the episode did to the airframe.
    private static void RunHookupAirframe(
        TestContext ctx, TestWorld world, ObjectiveGraph graph, ObjectiveScript script,
        LandingApproachRuntime trigger, CutsceneController cutscene, FlightController rig,
        LandingApproach auto, string planeNode, StringBuilder report)
    {
        if (MountBranch(world, planeNode) is not { } branch)
        {
            ctx.Check(false, $"'{ExtendHookAnim}' authors a branch for '{planeNode}'");
            return;
        }

        if (rig.PlaneModel is not { } model)
        {
            ctx.Check(false, $"the rig built a '{planeNode}' model");
            return;
        }

        var hook = branch.HookAnim is { } hookAnim ? NamedIn(world, hookAnim, model) : null;
        var fold = FoldOf(world, auto.Anim, planeNode);
        report.AppendLine($"authored mount offset {branch.Offset}, hook '{branch.HookAnim}' " +
            $"group {(hook != null ? $"'{AnimRuntime.NameOf(hook)}' built" : "absent")}, " +
            $"fold '{fold?.Anim ?? "(none)"}'");
        bool reachable = false;
        foreach (var found in world.Runtime.FindNodes(planeNode))
        {
            reachable |= ReferenceEquals(found, model);
        }

        ctx.Check(reachable,
            $"the flown '{planeNode}' is in the animation runtime's node table, which is what the hookup's per-airframe branches read");
        // A previous drive's airframe is freed before this one is staged, and the table's own
        // ancestry walk hashes a row's node, so a stale row throws for some later query.
        ctx.Same(0, world.Runtime.FreedNodeRows(),
            $"and the stage that put it there left no row naming a freed node behind");
        // The template stage keys two maps of its own on the same node identity and is retired by
        // the same sweep, so the airframe this one replaced is asked after on both sides.
        ctx.Same(0, world.Runtime.FreedStageKeys(),
            $"nor any template-stage key naming one");
        ctx.Check(hook != null,
            $"and carries its own '{branch.HookAnim}' hook group rather than a skipped subtree");
        ctx.Check(hook is not { Visible: true }, $"which starts retracted");
        CheckParkResolvedBySymbol(ctx, world, branch.HookAnim, report);

        // One definition per animation name is what the compiled-plus-reader merge leaves
        // (docs/formats/anim-definitions.md, the scope gates and the pair deduplication after
        // them). Two would make one CALL_ANIMATION start the same choreography twice.
        string branchAnim = branch.HookAnim ?? "";
        int forkDefs = world.Runtime.DefsFor(ExtendHookAnim).Count;
        int branchDefs = branchAnim.Length > 0 ? world.Runtime.DefsFor(branchAnim).Count : 0;
        report.AppendLine($"definitions loaded: '{ExtendHookAnim}' x{forkDefs}, " +
            $"'{branchAnim}' x{branchDefs}");
        ctx.Same(1, forkDefs,
            $"the loaded program holds one '{ExtendHookAnim}', so its one call site starts one fork");
        ctx.Same(1, branchDefs,
            $"and one '{branchAnim}', so that fork's branch starts one swing");

        // Which definitions the episode actually reached, so a check that fails says whether the
        // pose was wrong or the branch that writes it never ran at all (DIAG-20).
        var started = new List<string>();
        var plays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var atSwitchOn = new List<(string Node, Vector3 From, Vector3 At)>();
        void Record(AnimDefinition def, Node3D? anchor)
        {
            if (def.AnimName is { Length: > 0 } name)
            {
                plays[name] = plays.TryGetValue(name, out int n) ? n + 1 : 1;
                if (!started.Contains(name))
                {
                    started.Add(name);
                }

                if (atSwitchOn.Count == 0
                    && string.Equals(name, branchAnim, StringComparison.OrdinalIgnoreCase))
                {
                    SampleScaledMovers(def, model, atSwitchOn);
                }
            }
        }
        world.Runtime.OnInstanceStarted += Record;
        try
        {
            FlyTheAutoRow(ctx, world, graph, script, trigger, cutscene, rig, auto, planeNode, report);
        }
        finally
        {
            world.Runtime.OnInstanceStarted -= Record;
        }

        // The Balmoral branch ends its own sequence two seconds after calling the fold, so the
        // episode is over while the authored two-second turn is still running. Let it finish.
        for (float t = 0f; t < FoldSettleS; t += StepDt)
        {
            world.Runtime.Advance(StepDt);
        }

        report.AppendLine($"started: {string.Join(", ", started)}");
        var repeats = plays.Where(p => p.Value > 1)
            .OrderByDescending(p => p.Value).Select(p => $"{p.Key} x{p.Value}").ToList();
        report.AppendLine($"plays: {plays.Count} definition(s), repeats [{string.Join(", ", repeats)}]");
        foreach (var (node, from, at) in atSwitchOn)
        {
            report.AppendLine($"at switch-on '{node}': scale {at}, its motion starts from {from}");
        }

        // A node not parked at its own motion's FROM is drawn at the wrong pose for the whole
        // second before that motion starts, reading as a second swing; sorted magnitudes are the
        // honest comparison, since a rotated basis does not read its scale back axis-for-axis.
        foreach (var (node, from, at) in atSwitchOn)
        {
            ctx.Check(Near(SortedAxes(at), SortedAxes(from), SwingEpsilon),
                $"'{node}' is drawn at the pose '{branchAnim}' authors as its motion's FROM, {from} (read {at})");
        }

        // Every arm the branch's own definition swings by rotate FROM_TO, read against the
        // angle it authors for it, once the episode above has run to completion.
        var rotated = new List<(string Node, Vector3 To)>();
        foreach (var d in world.Runtime.DefsFor(branchAnim))
        {
            SampleRotatedMovers(d, rotated);
        }

        foreach (var (node, to) in rotated)
        {
            var live = NamedNode(model, node)?.Transform.Basis.GetEuler(EulerOrder.Yxz);
            report.AppendLine($"swung '{node}': authored to {to}, live local rotation {live}");
            ctx.Check(live is { } l && Near(l, to, SwingEpsilon),
                $"'{node}' reaches the angle '{branchAnim}' authors for it, {to} (read {live})");
        }

        // One call site playing three times and three call sites playing once are different
        // faults. The shared fork's count says nothing about the branch, which is what moves the
        // arms: the fork is authored once per archive and does play once whatever the branch does.
        ctx.Same(1, plays.TryGetValue(ExtendHookAnim, out int hookPlays) ? hookPlays : 0,
            $"the episode swings '{planeNode}'s hook once, playing '{ExtendHookAnim}' a single time");
        ctx.Same(1, plays.TryGetValue(branchAnim, out int branchPlays) ? branchPlays : 0,
            $"…and '{branchAnim}', the branch whose motions actually swing the arms, a single time");
        report.AppendLine($"after the episode: mount {model.Position}, hook visible={hook?.Visible}");
        ctx.Check(hook is { Visible: true },
            $"the hookup extends '{planeNode}'s own docking hook");
        ctx.Check(Near(model.Position, branch.Offset),
            $"and mounts it at the offset '{ExtendHookAnim}' authors for it, {branch.Offset}");
        CheckWingFold(ctx, world, fold, model, report);

        // ⚠ Last, and not optional: the rig this episode flew is freed when the body returns, and a
        // motion still running on one of its nodes ticks into a disposed object on the next drive.
        foreach (string anim in started)
        {
            world.Runtime.Stop(anim);
        }
    }

    // The flight half, exactly as the auto row runs it: arm through the mission's own objective,
    // fly the sphere, press the button, then step to the handoff.
    private static void FlyTheAutoRow(
        TestContext ctx, TestWorld world, ObjectiveGraph graph, ObjectiveScript script,
        LandingApproachRuntime trigger, CutsceneController cutscene, FlightController rig,
        LandingApproach auto, string planeNode, StringBuilder report)
    {
        // The mission's own intro is still playing at t=0 and raises the same out-of-flight code
        // the hookup does, so its handoff would land in the middle of this episode and give the
        // aircraft flight back mid-hookup. A player reaches the klondike minutes later.
        for (float t = 0f; t < IntroSettleS; t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            cutscene.Tick();
        }

        report.AppendLine($"intro settled after {IntroSettleS:0}s, playing={cutscene.Playing}");
        ArmRow(ctx, world, graph, script, auto, report);
        var frame = world.Runtime.FindNodes(auto.Node)[0].GlobalTransform;
        rig.Setup(new FlightModel(PlaneStats.Load(ctx.ZrdrPath, planeNode)),
            null, new CamParams(), frame * Lerp(auto, AxisFraction), frame * auto.Apex,
            ApproachThrottle, ApproachSpeedMps);
        rig.AutoLand = true;
        for (int i = 0; i < RestartFrames && !cutscene.Playing; i++)
        {
            rig.SimStep(StepDt);
            world.Runtime.Advance(StepDt);
            trigger.Tick();
            cutscene.Tick();
        }

        ctx.Check(cutscene.Playing, $"pressing the button starts '{auto.Anim}' under the host");
        float played = 0f;
        for (float t = 0f; t < PlayBudgetS && cutscene.Playing; t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            trigger.Tick();
            cutscene.Tick();
            graph.Step(StepDt);
            played += StepDt;
        }

        report.AppendLine($"episode ran {played:0.##} s, playing={cutscene.Playing}");
    }

    // CM02's ending: the shared hookup definition, flown in the captured Balmoral. Its move_player
    // sequence branches on which airframe is active and only this one calls a fold, so the branch
    // is decidable at all only because the flown airframe is in the runtime's node table.
    private static void DriveBalmoralDock(TestContext ctx, TestWorld world, CampaignDirector director,
        ObjectiveScript script, string missionZrdr, StringBuilder report)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, world.Chapter);
        var rows = LandingApproaches.Resolve(
            chapterZrdr, world.Gamez, name => world.Runtime.Handles(name));
        var dock = RowFor(rows, DockAnim);
        report.AppendLine($"docking row: '{dock?.Anim ?? "-"}' on '{dock?.Node ?? "-"}'");
        ctx.Check(dock != null,
            $"the chapter's landings.zrd carries the '{DockAnim}' row this mission ends on");
        var stage = world.Session.Aircraft;
        ctx.Check(stage != null,
            $"the mission stages the aircraft archive, the frame the hookup poses the airframe in");
        if (dock == null || stage == null)
        {
            return;
        }

        WithTrigger(ctx, world, director, rows, (trigger, cutscene, rig, graph) =>
            RunBalmoralDock(ctx, world, director, graph, script, trigger, cutscene, rig, dock, report),
            planeNode: BalmoralPlane, aircraft: stage);
    }

    // Flies the row and reads the branch out of the run: which definitions the episode started,
    // when the fold ran, when the mission-completion code landed and when the host let go.
    private static void RunBalmoralDock(
        TestContext ctx, TestWorld world, CampaignDirector director, ObjectiveGraph graph,
        ObjectiveScript script,
        LandingApproachRuntime trigger, CutsceneController cutscene, FlightController rig,
        LandingApproach dock, StringBuilder report)
    {
        // The crane's own hook is woken by an objective this suite does not otherwise drive.
        // A real approach reaches the hookpoint minutes after capture; this settle window gives
        // it the same head start.
        var craneStarted = world.Runtime.Play(HookHomebaseAnim);
        report.AppendLine($"'{HookHomebaseAnim}' started: {craneStarted.Count} definition(s)");
        for (float t = 0f; t < IntroSettleS; t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            cutscene.Tick();
        }

        report.AppendLine($"intro settled after {IntroSettleS:0}s, playing={cutscene.Playing}");
        foreach (var node in HookCraneParts)
        {
            var live = world.Runtime.FindNodes(node) is { Count: > 0 } hits
                ? hits[0].Transform.Basis.GetEuler(EulerOrder.Yxz)
                : (Vector3?)null;
            report.AppendLine($"crane '{node}' local rotation after the settle: {live}");
            ctx.Check(live is { } l && Near(l, Vector3.Zero, SwingEpsilon),
                $"'{node}' swings in to its authored rest, rather than stopping short or past it (read {live})");
        }

        var fold = FoldOf(world, DockAnim, BalmoralPlane);
        report.AppendLine($"fold definition: '{fold?.Anim ?? "(none)"}'");
        ctx.Check(fold != null,
            $"'{DockAnim}' authors a wing fold for '{BalmoralPlane}', the branch this run takes");
        ArmRow(ctx, world, graph, script, dock, report);

        var watcher = DockWatcherOf(script);
        report.AppendLine($"the objective watching '{DockAnim}' for EXECUTED: " +
            $"OBJECTIVE{watcher?.Number.ToString() ?? "?"}, instantwin={watcher?.InstantWin.ToString() ?? "-"}");
        ctx.Check(watcher != null,
            $"the mission carries an INSTANTWIN objective conditioned on '{DockAnim}' reaching EXECUTED, the second path to this ending");

        float now = 0f;
        float foldStartedAt = -1f;
        float foldEndedAt = -1f;
        float completedAt = -1f;
        float handedBackAt = -1f;
        float executedAt = -1f;
        float wonAt = -1f;
        float watcherAt = -1f;
        float leftAt = -1f;
        float lastFlownAt = -1f;
        int stateAtCode = -1;
        int flownAfterEnd = 0;
        int endings = 0;
        bool secondTook = false;
        void Ended(MissionOutcome outcome)
        {
            endings++;
            report.AppendLine($"  t={now,6:0.00} the mission ended {outcome}");
        }

        void CompletedOne(ObjectiveCompleted done)
        {
            if (watcher != null && done.Number == watcher.Number && watcherAt < 0f)
            {
                watcherAt = now;
                report.AppendLine($"  t={now,6:0.00} OBJECTIVE{done.Number} completed");
            }
        }

        void Started(AnimDefinition def, Node3D? anchor)
        {
            report.AppendLine($"  t={now,6:0.00} started '{def.AnimName}'");
            if (foldStartedAt < 0f && fold != null
                && string.Equals(def.AnimName, fold.Value.Anim, StringComparison.OrdinalIgnoreCase))
            {
                foldStartedAt = now;
            }
        }

        void Finished(AnimDefinition def, Node3D? anchor)
        {
            report.AppendLine($"  t={now,6:0.00} finished '{def.AnimName}'");
            if (foldEndedAt < 0f && fold != null
                && string.Equals(def.AnimName, fold.Value.Anim, StringComparison.OrdinalIgnoreCase))
            {
                foldEndedAt = now;
            }
        }

        world.Runtime.CallbackHost = (code, anim, root) =>
        {
            report.AppendLine($"  t={now,6:0.00} code {code} from '{anim}'");
            if (code == CompleteCode && completedAt < 0f)
            {
                completedAt = now;
                stateAtCode = world.Runtime.AnimStateOf(DockAnim);
            }

            return cutscene.Host(code, anim, root);
        };
        world.Runtime.OnInstanceStarted += Started;
        world.Runtime.OnInstanceFinished += Finished;
        graph.MissionEnded += Ended;
        graph.Completed += CompletedOne;
        try
        {
            ctx.Check(Fly(ctx, world, trigger, cutscene, graph, rig, dock, report),
                $"flying '{dock.Node}' starts '{dock.Anim}'");
            ctx.Check(cutscene.Playing, $"and the cutscene host takes the session on it");
            for (float t = 0f; t < DockBudgetS; t += StepDt)
            {
                // The session's own drive under an ended mission: while the leaving hold runs,
                // GameSession advances nothing but the director, so the aeroplane takes no step and
                // no stick reaches it, and the animation runtime stands where the ending left it.
                bool leaving = director.Leaving;
                bool endedBefore = completedAt >= 0f;
                if (!leaving)
                {
                    rig.SimStep(StepDt);
                    world.Runtime.Advance(StepDt);
                    trigger.Tick();
                    cutscene.Tick();
                    lastFlownAt = now;
                    if (endedBefore)
                    {
                        flownAfterEnd++;
                    }
                }

                director.Step(StepDt);
                now += StepDt;
                if (director.ReturnToCabin)
                {
                    leftAt = now;
                    report.AppendLine($"  t={now,6:0.00} the session left the world for the cabin");
                    break;
                }

                if (handedBackAt < 0f && !cutscene.Playing)
                {
                    handedBackAt = now;
                    report.AppendLine($"  t={now,6:0.00} the host handed the session back");
                }

                if (executedAt < 0f && world.Runtime.AnimStateOf(DockAnim) == AnimExecuted)
                {
                    executedAt = now;
                    report.AppendLine($"  t={now,6:0.00} '{DockAnim}' reads EXECUTED");
                }

                if (wonAt < 0f && graph.Outcome == MissionOutcome.Won)
                {
                    wonAt = now;
                }

                if (completedAt >= 0f && now > completedAt + CampaignDirector.LeavingHoldS
                    + AfterReleaseS)
                {
                    break;
                }
            }

            // The second signal: the same code again, once the mission is over. The original's
            // case for it is unguarded, and cannot fire twice only because the mission-end path it
            // calls has already left the flying state behind; here the guard is the graph's.
            secondTook = graph.NotifyDockingComplete();
            report.AppendLine($"a second completion code after the win: " +
                $"took={secondTook}, outcome={graph.Outcome}, endings={endings}");
        }
        finally
        {
            world.Runtime.OnInstanceStarted -= Started;
            world.Runtime.OnInstanceFinished -= Finished;
            graph.MissionEnded -= Ended;
            graph.Completed -= CompletedOne;
            world.Runtime.CallbackHost = cutscene.Host;
        }

        report.AppendLine($"fold started at {foldStartedAt:0.00} (finished at {foldEndedAt:0.00}, " +
            $"-1 when the ending stopped the world first), " +
            $"completion code at t={completedAt:0.00}, session handed back at t={handedBackAt:0.00}, " +
            $"codes=[{string.Join(", ", cutscene.Codes)}]");
        ctx.Check(foldStartedAt >= 0f,
            $"the docking reaches '{BalmoralPlane}'s own branch and calls its wing fold");
        ctx.Check(completedAt >= 0f && foldStartedAt >= 0f
                  && completedAt - foldStartedAt >= FoldRunS - FoldEndToleranceS,
            $"which turns through the branch's whole authored {FoldRunS:0.#} s wait before the ending stops the world");
        ctx.Check(completedAt >= 0f,
            $"the docking raises its mission-completion code {CompleteCode}");
        ctx.Check(completedAt >= 0f && foldStartedAt >= 0f
                  && completedAt >= foldStartedAt + FoldRunS - FoldEndToleranceS,
            $"at the end of the branch's own two-second wait, not before it");
        ctx.Check(handedBackAt < 0f || completedAt < 0f || handedBackAt >= completedAt - StepDt,
            $"and the host keeps the session until then (handed back at t={handedBackAt:0.00})");
        report.AppendLine($"mission outcome: {graph.Outcome}, ending={graph.Ending}, " +
            $"won at t={wonAt:0.00}, '{DockAnim}' EXECUTED at t={executedAt:0.00} " +
            $"(state when the code landed: {stateAtCode}), " +
            $"OBJECTIVE{watcher?.Number.ToString() ?? "?"} completed at t={watcherAt:0.00}, endings={endings}");
        ctx.Check(graph.Ending || graph.Outcome == MissionOutcome.Won,
            $"the code the docking ends on wins the mission, which is the only ending a mission finishing on the hook has");

        // The two paths to this ending and the order they resolve in. The code is raised by the
        // definition's last sequence, so the definition is still RUNNING when it lands and the
        // objective watching it for EXECUTED cannot have completed yet.
        ctx.Check(stateAtCode == AnimRunning,
            $"'{DockAnim}' is still RUNNING when it raises the code, so the objective's EXECUTED read cannot have won the mission first (read {stateAtCode})");
        ctx.Check(executedAt < 0f || completedAt < 0f || executedAt >= completedAt,
            $"and reads EXECUTED no earlier than that (EXECUTED at t={executedAt:0.00}, code at t={completedAt:0.00})");
        ctx.Check(executedAt < 0f || foldStartedAt < 0f
                  || executedAt >= foldStartedAt + FoldRunS - FoldEndToleranceS,
            $"which is the end of the branch's own two-second wait, not a point inside the wing fold");
        ctx.Check(watcherAt < 0f || completedAt < 0f || watcherAt >= completedAt,
            $"the INSTANTWIN objective watching it completes no earlier than the code either");

        // The original's case for this code sets the won flag and runs the mission-end path in one
        // breath. Nothing waits out a wrap-up, so the debrief opens on the film's own last frame.
        ctx.Check(wonAt >= 0f, $"the mission is won inside the run rather than left pending");
        ctx.Check(wonAt < 0f || completedAt < 0f || wonAt <= completedAt + EndLagS,
            $"on the frame the code lands, with no wrap-up in between (won at t={wonAt:0.00}, code at t={completedAt:0.00})");
        ctx.Check(endings == 1, $"and the mission ends exactly once (ended {endings} time(s))");
        ctx.Check(!secondTook, $"a second completion code after the win is refused and changes nothing");
        ctx.Check(graph.Outcome == MissionOutcome.Won, $"the outcome after it is still Won");

        // The other end of the same ending: the outcome is settled on the code's frame, and the
        // session stays in the world for the leaving hold after it. The film's last live frame is
        // the frame the code landed on, which is what the original leaves standing under its fade.
        report.AppendLine($"the film's last live frame is t={lastFlownAt:0.00} " +
            $"(the code's own frame), the session left at t={leftAt:0.00}, " +
            $"{flownAfterEnd} world step(s) ran after the ending");
        ctx.Check(leftAt >= 0f, $"the session leaves the world rather than staying in it");
        ctx.Check(leftAt < 0f || wonAt < 0f || leftAt > wonAt + StepDt,
            $"not on the frame the mission was won (won at t={wonAt:0.00}, left at t={leftAt:0.00})");
        ctx.Check(leftAt < 0f || completedAt < 0f
                  || leftAt >= completedAt + CampaignDirector.LeavingHoldS - EndLagS,
            $"but a whole {CampaignDirector.LeavingHoldS:0.#} s leaving hold after the code (left at t={leftAt:0.00})");
        ctx.Check(leftAt < 0f || completedAt < 0f
                  || leftAt <= completedAt + CampaignDirector.LeavingHoldS + AfterReleaseS,
            $"and not a second longer than the hold the original's fade runs for");
        ctx.Check(flownAfterEnd == 0,
            $"with the world standing still throughout: no aeroplane step, no animation advance and no stick between the ending and the cabin ({flownAfterEnd} step(s) ran)");
        if (rig.PlaneModel is { } model)
        {
            // ⚠ The ending catches the fold a few frames short of its authored end (the branch
            // waits 2.0 s where the fold runs 2.01), so that gap and not the pose epsilon is the
            // tolerance the wings are read at here.
            CheckWingFold(ctx, world, fold, model, report, FoldFreezeEpsilon);
        }

        world.Runtime.Stop(dock.Anim);
    }

    // CM06's docking, a shape no other shipped row has: the row's own definition authors no
    // CALLBACK and calls the hookup, the drop, the hook state and the unhook in turn, waiting on the
    // first and the last. The hookup raises the first code and ends with the aeroplane still on the
    // hook; the unhook raises the handoff and the re-placement at its own end.
    private static void DriveDockingHold(TestContext ctx, TestWorld world, CampaignDirector director,
        ObjectiveScript script, string missionZrdr, StringBuilder report)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, world.Chapter);
        var rows = LandingApproaches.Resolve(
            chapterZrdr, world.Gamez, name => world.Runtime.Handles(name));
        var dock = SilentRowOf(world, rows);
        report.AppendLine($"silent row: '{dock?.Anim ?? "-"}' on '{dock?.Node ?? "-"}'");
        ctx.Check(dock != null,
            $"the chapter's landings.zrd carries a manual row whose own definition raises no code while its call closure does");
        var stage = world.Session.Aircraft;
        ctx.Check(stage?.PlayerMarker != null,
            $"the mission stages the aircraft archive's '{AircraftStage.PlayerNode}' marker, the pose the docking flies");
        if (dock == null || stage?.PlayerMarker is not { } marker)
        {
            return;
        }

        WithTrigger(ctx, world, director, rows, (trigger, cutscene, rig, graph) =>
            RunTheDocking(ctx, world, graph, script, trigger, cutscene, rig, dock, marker, report),
            aircraft: stage);
    }

    private static void RunTheDocking(
        TestContext ctx, TestWorld world, ObjectiveGraph graph, ObjectiveScript script,
        LandingApproachRuntime trigger, CutsceneController cutscene, FlightController rig,
        LandingApproach dock, Node3D marker, StringBuilder report)
    {
        // Every start from the settle onward, by name: a leg the episode plays twice is a
        // different fault from two legs playing once, and only a count separates them.
        var plays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void CountPlay(AnimDefinition def, Node3D? anchor)
        {
            if (def.AnimName is { Length: > 0 } name)
            {
                plays[name] = plays.TryGetValue(name, out int n) ? n + 1 : 1;
            }
        }

        world.Runtime.OnInstanceStarted += CountPlay;
        try
        {
            RunTheDockingCounted(ctx, world, graph, script, trigger, cutscene, rig, dock, marker,
                report, plays);
        }
        finally
        {
            world.Runtime.OnInstanceStarted -= CountPlay;
        }
    }

    private static void RunTheDockingCounted(
        TestContext ctx, TestWorld world, ObjectiveGraph graph, ObjectiveScript script,
        LandingApproachRuntime trigger, CutsceneController cutscene, FlightController rig,
        LandingApproach dock, Node3D marker, StringBuilder report, Dictionary<string, int> plays)
    {
        // The fork's node states as the world build leaves them, before an objective has woken.
        // A world that builds both legs' nodes open would answer both calls whatever the gate does.
        var atBuild = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var leg in ForkedLegsOf(world, dock.Anim))
        {
            foreach (var prereq in leg.PrereqNodes)
            {
                atBuild[PathKey(prereq.Path)] =
                    PrereqNodeOf(world, prereq.Path) is { } at && at.Visible;
            }
        }

        report.AppendLine("at build: " + string.Join(", ",
            atBuild.Select(kv => $"{kv.Key}={(kv.Value ? "ACTIVE" : "INACTIVE")}")));
        for (float t = 0f; t < IntroSettleS; t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            cutscene.Tick();
        }

        report.AppendLine($"intro settled after {IntroSettleS:0}s, playing={cutscene.Playing}");
        ArmRow(ctx, world, graph, script, dock, report);

        // The row's forked legs and the node states at the instant the hookup dispatches them. The
        // fork is the data's, and the states move during the episode, so they are read at the
        // dispatch rather than at its end.
        var forks = ForkedLegsOf(world, dock.Anim);
        var atDispatch = new Dictionary<string, bool>(StringComparer.Ordinal);
        void SnapAtDispatch(AnimDefinition def, Node3D? anchor)
        {
            if (atDispatch.Count > 0 || def.PrereqNodes.Count == 0)
            {
                return;
            }

            foreach (var leg in forks)
            {
                foreach (var prereq in leg.PrereqNodes)
                {
                    atDispatch[PathKey(prereq.Path)] =
                        PrereqNodeOf(world, prereq.Path) is { } at && at.Visible;
                }
            }
        }

        world.Runtime.OnInstanceStarted += SnapAtDispatch;
        float now = 0f;
        var started = new List<string>();
        void Record(AnimDefinition def, Node3D? anchor)
        {
            if (def.AnimName is { Length: > 0 } name && !started.Contains(name))
            {
                started.Add(name);
                report.AppendLine($"  t={now,6:0.00} started '{name}'");
            }
        }

        // The handoff code as the runtime raises it, read in front of the host so the moment is
        // known whichever episode the host books it to.
        float handoffAt = -1f;
        string? handoffRaiser = null;
        string? firstRaiser = null;
        world.Runtime.CallbackHost = (code, anim, root) =>
        {
            firstRaiser ??= anim;
            report.AppendLine($"  t={now,6:0.00} code {code} from '{anim}'");
            if (code == HandoffCode && handoffAt < 0f)
            {
                handoffAt = now;
                handoffRaiser = anim;
            }

            return cutscene.Host(code, anim, root);
        };
        void Finished(AnimDefinition def, Node3D? anchor) =>
            report.AppendLine($"  t={now,6:0.00} finished '{def.AnimName}'");
        world.Runtime.OnInstanceStarted += Record;
        world.Runtime.OnInstanceFinished += Finished;
        world.Runtime.OpenResolutionCensus();
        try
        {
            ctx.Check(Fly(ctx, world, trigger, cutscene, graph, rig, dock, report),
                $"flying '{dock.Node}' starts '{dock.Anim}'");
            report.AppendLine($"episode: playing={cutscene.Playing} anim='{cutscene.Anim}' " +
                $"first code from '{firstRaiser}' codes=[{string.Join(", ", cutscene.Codes)}]");
            ctx.Check(cutscene.Playing, $"and the cutscene host takes the session on it");
            ctx.Check(firstRaiser != null && firstRaiser != dock.Anim,
                $"the first code is raised by a callee, not by '{dock.Anim}' itself");
            ctx.Check(string.Equals(cutscene.Anim, dock.Anim, StringComparison.OrdinalIgnoreCase),
                $"yet the episode belongs to '{dock.Anim}', the row the trigger started, which is the original's landings slot (read '{cutscene.Anim ?? "-"}')");
            WatchTheDocking(ctx, world, graph, trigger, cutscene, rig, dock, marker, report,
                () => now, dt => now += dt, () => handoffAt, () => handoffRaiser);
            CheckTheHookPlays(ctx, world, dock, plays, report);
            CheckTheDockingPrereqs(ctx, world, dock, forks, atDispatch, plays, report);
        }
        finally
        {
            foreach (string line in world.Runtime.ResolutionLines())
            {
                report.AppendLine($"resolution: {line}");
            }

            world.Runtime.CloseResolutionCensus();
            world.Runtime.OnInstanceStarted -= SnapAtDispatch;
            world.Runtime.OnInstanceStarted -= Record;
            world.Runtime.OnInstanceFinished -= Finished;
            world.Runtime.CallbackHost = cutscene.Host;
            foreach (string anim in started)
            {
                world.Runtime.Stop(anim);
            }
        }
    }

    // The played leg, watched frame by frame: control locked until the handoff code, the handoff
    // at the row definition's own end, and no teleport once the aeroplane is flying again.
    private static void WatchTheDocking(
        TestContext ctx, TestWorld world, ObjectiveGraph graph, LandingApproachRuntime trigger,
        CutsceneController cutscene, FlightController rig, LandingApproach dock, Node3D marker,
        StringBuilder report, Func<float> now, Action<float> tick, Func<float> handoffAt,
        Func<string?> handoffRaiser)
    {
        float releasedAt = -1f;
        float endedAt = -1f;
        float handedBackAt = -1f;
        float unlockedBeforeHandoffAt = -1f;
        float biggestStepM = 0f;
        float biggestStepAt = -1f;
        float placedOffM = -1f;
        var heldMarkerAt = AnimRuntime.WorldTransform(marker, out _).Origin;
        var prev = rig.WorldPosition;
        int frame = 0;
        while (now() < DockBudgetS && (releasedAt < 0f || now() < releasedAt + AfterReleaseS))
        {
            rig.SimStep(StepDt);
            world.Runtime.Advance(StepDt);
            trigger.Tick();
            cutscene.Tick();
            graph.Step(StepDt);
            tick(StepDt);
            frame++;
            bool locked = rig.Held && rig.Inert;
            var markerPose = AnimRuntime.WorldTransform(marker, out _);
            if (locked)
            {
                heldMarkerAt = markerPose.Origin;
            }

            if (!locked && releasedAt < 0f)
            {
                releasedAt = now();
                // ⚠ Against the pose the definition last left the marker in, not against the marker:
                // the handoff sends it home to the world root, so a live read on this very frame
                // measures that trip and not the re-placement (CutsceneController.Restore).
                placedOffM = rig.WorldPosition.DistanceTo(heldMarkerAt);
                report.AppendLine($"  t={now(),6:0.00} released: {placedOffM:0.#} m off the '{AircraftStage.PlayerNode}' marker, " +
                    $"handoff code {(handoffAt() < 0f ? "not raised" : $"raised at t={handoffAt():0.00}")}");
                if (handoffAt() < 0f)
                {
                    unlockedBeforeHandoffAt = now();
                }
            }

            if (releasedAt >= 0f && now() > releasedAt)
            {
                float step = rig.WorldPosition.DistanceTo(prev);
                if (step > biggestStepM)
                {
                    biggestStepM = step;
                    biggestStepAt = now();
                }
            }

            prev = rig.WorldPosition;
            if (endedAt < 0f && world.Runtime.AnimStateOf(dock.Anim) != 2)
            {
                endedAt = now();
                report.AppendLine($"  t={now(),6:0.00} '{dock.Anim}' ended, playing={cutscene.Playing}");
            }

            if (handedBackAt < 0f && !cutscene.Playing)
            {
                handedBackAt = now();
                report.AppendLine($"  t={now(),6:0.00} the host handed the session back");
            }

            if (frame % (int)(1f / StepDt) == 0)
            {
                report.AppendLine($"t={now(),6:0.0} held={rig.Held} inert={rig.Inert} " +
                    $"playing={cutscene.Playing} anim='{cutscene.Anim ?? "-"}' " +
                    $"rig {rig.WorldPosition} marker {markerPose.Origin}");
            }
        }

        report.AppendLine($"released at t={releasedAt:0.00}, handoff code at t={handoffAt():0.00} " +
            $"from '{handoffRaiser() ?? "-"}', '{dock.Anim}' ended at t={endedAt:0.00}, " +
            $"session handed back at t={handedBackAt:0.00}, " +
            $"biggest step after release {biggestStepM:0.##} m at t={biggestStepAt:0.00}");
        ctx.Check(handoffAt() >= 0f, $"the docking raises its handoff code within {DockBudgetS:0} s");
        ctx.Check(handoffRaiser() != null && handoffRaiser() != dock.Anim,
            $"from a callee of '{dock.Anim}', the unhook, rather than from the row's own definition");
        // The row's trailing WAIT_FOR_COMPLETION holds no runner open (docs/org/sequences.md), so
        // its definition ends before the unhook does: the episode has to outlive it.
        ctx.Check(endedAt >= 0f && handoffAt() >= 0f && endedAt < handoffAt(),
            $"'{dock.Anim}' itself ends before the handoff, its last call being a trailing wait");
        ctx.Check(handedBackAt < 0f || handoffAt() < 0f || handedBackAt >= handoffAt() - StepDt,
            $"and the host keeps the session past that end, until the code (handed back at t={handedBackAt:0.00})");
        ctx.Check(unlockedBeforeHandoffAt < 0f,
            $"the player is held out of flight from the hookup until that code, not released when the first callee ends (unlocked at t={unlockedBeforeHandoffAt:0.00})");
        ctx.Check(releasedAt >= 0f && handoffAt() >= 0f && releasedAt >= handoffAt() - StepDt,
            $"and gets flight back the frame the code lands");
        ctx.Check(handedBackAt >= 0f && handoffAt() >= 0f && handedBackAt - handoffAt() < AfterReleaseS,
            $"and the host hands the session back within {AfterReleaseS:0} s of it, the unhook being the last code-authoring definition");
        ctx.Check(placedOffM >= 0f && placedOffM < PlacedToleranceM,
            $"the released aeroplane flies out of the '{AircraftStage.PlayerNode}' marker's pose, where the re-placement code read it, within {PlacedToleranceM:0} m");
        ctx.Check(biggestStepM < TeleportM,
            $"and is never teleported after the release: no frame moves it {TeleportM:0} m or more, the end of the definition included");
    }

    // The manual row whose own definition raises no CALLBACK while something in its call closure
    // does, which is the docking's shape and the case the landings slot exists for.
    private static LandingApproach? SilentRowOf(TestWorld world, IReadOnlyList<LandingApproach> rows)
    {
        foreach (var row in rows)
        {
            if (row.Auto || RaisesCode(world, row.Anim))
            {
                continue;
            }

            foreach (string callee in ClosureOf(world, row.Anim))
            {
                if (callee != row.Anim && RaisesCode(world, callee))
                {
                    return row;
                }
            }
        }

        return null;
    }

    private static bool RaisesCode(TestWorld world, string anim)
    {
        foreach (var def in world.Runtime.DefsFor(anim))
        {
            foreach (var seq in def.Sequences)
            {
                foreach (var ev in seq.Events)
                {
                    if (ev.Kind == "Callback")
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // The mission's other path to this ending: the objective whose ANIM_STATE watches the docking
    // definition for EXECUTED and wins on it. Found by its condition rather than by number, so the
    // suite reads the data's own shape.
    private static ObjectiveDef? DockWatcherOf(ObjectiveScript script)
    {
        foreach (var def in script.Objectives)
        {
            foreach (var entry in def.AnimStates)
            {
                if (def.InstantWin && entry.State == AnimExecuted
                    && string.Equals(entry.Name, DockAnim, StringComparison.OrdinalIgnoreCase))
                {
                    return def;
                }
            }
        }

        return null;
    }

    // The wing fold's own two movers, checked against the rotations the fold definition authors.
    // An airframe that authors no fold is a coverage statement, not a failure (DIAG-22).
    private static void CheckWingFold(TestContext ctx, TestWorld world,
        (string Anim, AnimDefinition Def)? fold, Node3D model, StringBuilder report,
        float epsilon = PoseEpsilon)
    {
        if (fold is not { } authored)
        {
            report.AppendLine("no fold definition is rooted on this airframe");
            return;
        }

        int checkedPairs = 0;
        foreach (var seq in authored.Def.Sequences)
        {
            foreach (var ev in seq.Events)
            {
                if (ev.Kind != "ObjectMotionFromTo" || ev.Data.Obj("rotate") is not { } rotate
                    || ev.Data.Str("name") is not { } name)
                {
                    continue;
                }

                var want = rotate.Vec3("to");
                var node = NamedNode(model, name);
                var got = node?.Basis.GetEuler(EulerOrder.Yxz) ?? Vector3.Zero;
                report.AppendLine($"fold '{name}': authored {want}, measured {got}");
                ctx.Check(node != null, $"'{authored.Anim}' reaches the flown airframe's '{name}'");
                ctx.Check(node != null && Near(got, want, epsilon),
                    $"and turns it to the {want} the definition authors");
                checkedPairs++;
            }
        }

        ctx.Check(checkedPairs > 0, $"'{authored.Anim}' authors the movers this reads");
    }

    // The airframe's branch of the extend-hook definition: the OBJECT_TRANSLATE_STATE on its own
    // node is the offset it hangs at, and the CALL_ANIMATION after it is that airframe's hook.
    private static (Vector3 Offset, string? HookAnim)? MountBranch(TestWorld world, string planeNode)
    {
        foreach (var def in world.Runtime.DefsFor(ExtendHookAnim))
        {
            foreach (var seq in def.Sequences)
            {
                for (int i = 0; i < seq.Events.Count; i++)
                {
                    if (seq.Events[i].Kind != "ObjectTranslateState"
                        || !string.Equals(seq.Events[i].Data.Str("node"), planeNode,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? hook = null;
                    for (int j = i + 1; j < seq.Events.Count && hook == null; j++)
                    {
                        if (seq.Events[j].Kind == "CallAnimation")
                        {
                            hook = seq.Events[j].Data.Str("name");
                        }
                    }

                    return (seq.Events[i].Data.Vec3("state"), hook);
                }
            }
        }

        return null;
    }

    // The airframes the shared fork branches on, in the order it authors them: one
    // OBJECT_TRANSLATE_STATE per branch, on that airframe's own node.
    private static List<string> BranchPlanes(TestWorld world)
    {
        var planes = new List<string>();
        foreach (var def in world.Runtime.DefsFor(ExtendHookAnim))
        {
            foreach (var seq in def.Sequences)
            {
                foreach (var ev in seq.Events)
                {
                    if (ev.Kind == "ObjectTranslateState"
                        && ev.Data.Str("node") is { Length: > 0 } node
                        && !planes.Contains(node, StringComparer.OrdinalIgnoreCase))
                    {
                        planes.Add(node);
                    }
                }
            }
        }

        return planes;
    }

    // Every airframe the fork branches on, read for the bit a human rig's build copies onto its
    // hook group (PlaneBuilder parks the group at its archive-authored active flag). Only four of
    // the eleven author a startup definition and no docking calls one, so if a startup were what
    // parked a hook the other seven would ship theirs active and open deployed.
    private static void CheckHooksParked(TestContext ctx, TestWorld world, StringBuilder report)
    {
        var planes = BranchPlanes(world);
        ctx.Check(planes.Count > 0, $"'{ExtendHookAnim}' authors the airframe branches this reads");
        var gamez = GameZ.Load(ctx.PlanesGamezPath);
        int read = 0;
        foreach (string plane in planes)
        {
            if (MountBranch(world, plane) is not { HookAnim: { Length: > 0 } hookAnim })
            {
                continue;
            }

            string group = HookGroupOf(world, hookAnim);
            var node = group.Length > 0 ? gamez.FindByName(group) : null;
            report.AppendLine($"{plane}: '{hookAnim}' on '{group}' " +
                (node == null ? "absent from the aircraft archive"
                    : node.Active ? "ships ACTIVE" : "ships inactive, so the build parks it"));
            ctx.Check(node != null, $"'{plane}' carries the '{group}' group '{hookAnim}' drives");
            ctx.Check(node is not { Active: true },
                $"…shipped inactive, which is what parks it whether or not the airframe authors a startup");
            read++;
        }

        ctx.Same(planes.Count, read, $"every branch the fork authors names a hook to read");
    }

    // The park's own verdict on how it reached the nodes it seeded, read as the state the staging
    // point LEFT rather than re-asked afterwards: the same question put to the resolver once the
    // episode has dispatched answers about a table later stages have grown. Every seeded node has
    // to come from the definition's compiled symbol table, since a name walk would take the wrong
    // sibling on any airframe that ever gains a duplicate arm name.
    private static void CheckParkResolvedBySymbol(
        TestContext ctx, TestWorld world, string? hookAnim, StringBuilder report)
    {
        var park = world.Runtime.LastDockingHookPark;
        int authored = 0;
        foreach (var def in world.Runtime.DefsFor(hookAnim ?? ""))
        {
            authored += SeededMoverNames(def).Count;
        }

        report.AppendLine($"park: seeded {park.Seeded} of {authored} authored mover(s), " +
            $"{park.SymbolBound} bound by symbol table, unbound [{park.Unbound}]");
        ctx.Same(authored, park.Seeded,
            $"the park seeds every node '{hookAnim}' authors a FROM pose for");
        ctx.Check(authored > 0, $"…and '{hookAnim}' authors at least one, so that is not vacuous");
        ctx.Same(park.Seeded, park.SymbolBound,
            $"and the compiled symbol table binds every one of them at the park (unbound: [{park.Unbound}])");
    }

    // The nodes SeedFromExtend seeds off one extend definition: every distinct name an
    // ObjectMotionFromTo moves that authors a `from` on any of the three channels.
    private static List<string> SeededMoverNames(AnimDefinition def)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seq in def.Sequences)
        {
            foreach (var ev in seq.Events)
            {
                if (ev.Kind != "ObjectMotionFromTo" || ev.Data.Str("name") is not { } name)
                {
                    continue;
                }

                bool from = false;
                foreach (string channel in MotionChannels)
                {
                    from |= ev.Data.Obj(channel) is { } c && c.Has("from");
                }

                if (from && seen.Add(name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    // The node a hook definition is rooted on, as that definition names it.
    private static string HookGroupOf(TestWorld world, string hookAnim)
    {
        foreach (var def in world.Runtime.DefsFor(hookAnim))
        {
            if (def.Name is { Length: > 0 } name)
            {
                return name;
            }
        }

        return string.Empty;
    }

    // What each of a hook definition's scaled movers is drawn at when that definition starts. The
    // arms are switched on by its state sequence and only scaled by a motion its control sequence
    // starts a second later, so this reads the pose the opening shot shows in between.
    private static void SampleScaledMovers(AnimDefinition def, Node3D model,
        List<(string Node, Vector3 From, Vector3 At)> into)
    {
        foreach (var seq in def.Sequences)
        {
            foreach (var ev in seq.Events)
            {
                if (ev.Kind != "ObjectMotionFromTo" || ev.Data.Obj("scale") is not { } scale
                    || ev.Data.Str("name") is not { } name)
                {
                    continue;
                }

                into.Add((name, scale.Vec3("from"), NamedNode(model, name)?.Scale ?? Vector3.Zero));
            }
        }
    }

    // Every distinct node a hook definition swings by rotate FROM_TO, with its authored end
    // angle, so a run can compare the live pose reached against it.
    private static void SampleRotatedMovers(AnimDefinition def, List<(string Node, Vector3 To)> into)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seq in def.Sequences)
        {
            foreach (var ev in seq.Events)
            {
                if (ev.Kind != "ObjectMotionFromTo" || ev.Data.Obj("rotate") is not { } rotate
                    || ev.Data.Str("name") is not { } name || !seen.Add(name))
                {
                    continue;
                }

                into.Add((name, rotate.Vec3("to")));
            }
        }
    }

    // The airframe's own wing fold, if it authors one: a CALL_ANIMATION inside the hookup's
    // move_player sequence whose definition is rooted on this airframe's node.
    private static (string Anim, AnimDefinition Def)? FoldOf(
        TestWorld world, string hookupAnim, string planeNode)
    {
        foreach (var hookup in world.Runtime.DefsFor(hookupAnim))
        {
            foreach (var seq in hookup.Sequences)
            {
                if (!string.Equals(seq.Name, HookupPlayerSeq, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var ev in seq.Events)
                {
                    if (ev.Kind != "CallAnimation" || ev.Data.Str("name") is not { } called)
                    {
                        continue;
                    }

                    foreach (var def in world.Runtime.DefsFor(called))
                    {
                        if (string.Equals(def.Name, planeNode, StringComparison.OrdinalIgnoreCase))
                        {
                            return (called, def);
                        }
                    }
                }
            }
        }

        return null;
    }

    // The node a definition anchors on, found inside one aircraft rather than the world index, so
    // a second rig built for the next airframe cannot answer for the first.
    private static Node3D? NamedIn(TestWorld world, string animName, Node3D model)
    {
        foreach (var def in world.Runtime.DefsFor(animName))
        {
            if (NamedNode(model, def.Name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static bool Near(Vector3 a, Vector3 b, float epsilon = PoseEpsilon) =>
        (a - b).Length() <= epsilon;

    // A vector's own components, ascending: the permutation-invariant read for a scale pulled
    // back off a rotated basis, where which world axis a magnitude lands on is not the claim.
    private static Vector3 SortedAxes(Vector3 v)
    {
        Span<float> a = stackalloc float[] { v.X, v.Y, v.Z };
        a.Sort();
        return new Vector3(a[0], a[1], a[2]);
    }

    private static Node3D? NamedNode(Node3D root, string name)
    {
        if (string.Equals(AnimRuntime.NameOf(root), name, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        foreach (var child in root.GetChildren())
        {
            if (child is Node3D n3d && NamedNode(n3d, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // The session wiring a flown story mission has and the suite harness's world build does not:
    // the cutscene host over the table's own definition closure, the trigger bound to the resolved
    // rows, a rig for it to fly, and the campaign director attached to that rig.
    private static void WithTrigger(
        TestContext ctx,
        TestWorld world,
        CampaignDirector director,
        IReadOnlyList<LandingApproach> armed,
        Action<LandingApproachRuntime, CutsceneController, FlightController, ObjectiveGraph> body,
        Action<ProjectilePool>? beforeBind = null,
        string planeNode = PlaneNode,
        AircraftStage? aircraft = null)
    {
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        cutscene.BindWorld(world.Runtime, aircraft);
        cutscene.HostDefinitions(ClosureOf(world, armed));
        world.Runtime.CallbackHost = cutscene.Host;
        var trigger = new LandingApproachRuntime();
        ctx.Host.AddChild(trigger);

        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
        var pool = new ProjectilePool(textures, null, null);
        FlightController? rig = null;
        ctx.Host.AddChild(pool);
        try
        {
            rig = BuildRig(ctx, world, pool, planeNode);
            var craft = rig;
            // The session's nearest-human seam: without it the EXECUTION_BY_RANGE poll reads the
            // test camera kilometres off, and a range-armed definition on the flown approach
            // never fires under the rig. A drive wanting the player elsewhere overrides it.
            world.Runtime.PlayerPositions = () => new[] { craft.WorldPosition };
            // One rig, which is the human field a single-player mission flies: the trigger and the
            // ladder both read it, and it is what the episode owner falls back to.
            var rigs = new[]
            {
                new PlayerRig
                {
                    Index = 0,
                    Camera = ctx.Camera,
                    HudParent = ctx.Host,
                    Controller = craft,
                },
            };
            cutscene.BindRigs(rigs, () => Array.Empty<FlightController>());
            cutscene.WorldHeld = director.HoldForCutscene;
            // The session's own wiring for the docking's completion code, so a row that raises it
            // reaches the same objectives graph a flown mission's would.
            cutscene.MissionComplete = () => director.Graph?.NotifyDockingComplete();
            // The mission's own actors, before the bind: a row whose approach node arrives with a
            // roster spawn is only there to bind once that spawn has happened, which is the whole
            // ordering the session repeats when it re-binds after its roster build.
            beforeBind?.Invoke(pool);
            trigger.Bind(world.Runtime, armed, cutscene, () => rigs);
            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Sounds = world.Runtime.Sounds,
                ListenerPosition = () => craft.WorldPosition,
                PlayerAircraft = () => craft,
                Projectiles = pool,
                Rng = new Random(1),
            });
            body(trigger, cutscene, rig, director.Graph!);
        }
        finally
        {
            // ⚠ The roster spawner parents its rigs to the shared suite host, so this mission's
            // roster outlives its own suite unless freed here: a later spawn of a block both
            // missions carry (wingman_4) then collides on the node name and Godot renames it.
            foreach (var spawned in director.Roster.Values)
            {
                spawned.Free();
            }

            world.Runtime.PlayerPositions = null;
            rig?.Free();
            pool.Free();
            textures.Dispose();
            trigger.Free();
            cutscene.Free();
        }
    }

    // The same staging as WithTrigger with a human field of two: its own rigs, its own host and its
    // own trigger, so a leg reading "which human" has two to tell apart. Separate rather than a flag
    // on WithTrigger, whose twelve one-human legs must go on flying exactly one aeroplane.
    private static void WithCoopTrigger(
        TestContext ctx,
        TestWorld world,
        CampaignDirector director,
        IReadOnlyList<LandingApproach> armed,
        Action<LandingApproachRuntime, CutsceneController, IReadOnlyList<PlayerRig>, ObjectiveGraph> body,
        string planeNode = PlaneNode,
        AircraftStage? aircraft = null)
    {
        var cutscene = new CutsceneController();
        ctx.Host.AddChild(cutscene);
        cutscene.BindWorld(world.Runtime, aircraft);
        cutscene.HostDefinitions(ClosureOf(world, armed));
        world.Runtime.CallbackHost = cutscene.Host;
        var trigger = new LandingApproachRuntime();
        ctx.Host.AddChild(trigger);

        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
        var pool = new ProjectilePool(textures, null, null);
        ctx.Host.AddChild(pool);
        var built = new List<FlightController>();
        try
        {
            var rigs = new List<PlayerRig>();
            for (int i = 0; i < CoopHumans; i++)
            {
                var craft = BuildRig(ctx, world, pool, planeNode);
                craft.Name = $"CoopApproachPlayer{i + 1}";
                craft.PlayerIndex = FlightRoster.ShooterIdBase - CoopHumans + i;
                built.Add(craft);
                rigs.Add(new PlayerRig
                {
                    Index = i,
                    Camera = ctx.Camera,
                    HudParent = ctx.Host,
                    Controller = craft,
                });
            }

            // The session's nearest-human seam, over the whole field: a range-armed definition has
            // to see whichever human is closest, not player 1 alone.
            world.Runtime.PlayerPositions = () => built.ConvertAll(c => c.WorldPosition);
            cutscene.BindRigs(rigs, () => Array.Empty<FlightController>());
            cutscene.WorldHeld = director.HoldForCutscene;
            cutscene.MissionComplete = () => director.Graph?.NotifyDockingComplete();
            trigger.Bind(world.Runtime, armed, cutscene, () => rigs);
            director.Attach(new CampaignDirector.WorldInputs
            {
                Runtime = world.Runtime,
                Sounds = world.Runtime.Sounds,
                ListenerPosition = () => built[0].WorldPosition,
                PlayerAircraft = () => built[0],
                Humans = () => built,
                Projectiles = pool,
                Rng = new Random(1),
            });
            body(trigger, cutscene, rigs, director.Graph!);
        }
        finally
        {
            foreach (var spawned in director.Roster.Values)
            {
                spawned.Free();
            }

            world.Runtime.PlayerPositions = null;
            foreach (var craft in built)
            {
                craft.Free();
            }

            pool.Free();
            textures.Dispose();
            trigger.Free();
            cutscene.Free();
        }
    }

    // CM02's capture gate, which is not a per-aircraft one: the mission arms and disarms its three
    // Balmoral approaches from one pair of ON_CALL definitions covering all three at once, and the
    // objective that calls the arming one waits on those planes' own aiv group being down to one.
    // Each row's approach node is a child of that plane's gamez node, so the volume follows the
    // aircraft it belongs to.
    private static void DriveWingWalk(TestContext ctx, TestWorld world, CampaignDirector director,
        ObjectiveScript script, string missionZrdr, StringBuilder report)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, world.Chapter);
        var rows = LandingApproaches.Resolve(
            chapterZrdr, world.Gamez, name => world.Runtime.Handles(name));
        var blocks = AiSkills.LoadRoster(missionZrdr);
        var carried = new List<LandingApproach>();
        var owners = new List<string>();
        foreach (var row in rows)
        {
            string owner = TopAncestorOf(world.Gamez, row.Node);
            report.AppendLine($"row anim='{row.Anim}' node='{row.Node}' {row.Shape} " +
                $"r={row.Radius:0.0} auto={row.Auto} angle={Mathf.RadToDeg(row.AngleRad):0.#} " +
                $"speed={row.MinSpeedMps:0.#}..{row.MaxSpeedMps:0.#} m/s " +
                $"owner='{owner}' built={world.Runtime.FindNodes(row.Node).Count}");
            if (BlockOf(blocks, owner) != null)
            {
                carried.Add(row);
                owners.Add(owner);
            }
        }

        CheckCarriedRows(ctx, world, carried, owners, report);
        CheckGateCondition(ctx, script, blocks, owners, report);
        WithTrigger(ctx, world, director, rows, (trigger, cutscene, rig, graph) =>
            {
                CheckGrafted(ctx, world, carried, report);
                var gate = CheckGateReach(ctx, world, script, rows, carried, report);
                ctx.Same(rows.Count, trigger.Armed,
                    $"every resolved row binds, the roster-carried ones included: their approach nodes came with the rigs that own them");
                RunTheCapture(ctx, world, trigger, cutscene, rig, carried, gate, report);
            },
            beforeBind: pool => SpawnRoster(ctx, world, director, missionZrdr, pool, report));
    }

    private static void DriveTrainPickup(TestContext ctx, TestWorld world, CampaignDirector director,
        ObjectiveScript script, string missionZrdr, StringBuilder report)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, world.Chapter);
        var rows = LandingApproaches.Resolve(
            chapterZrdr, world.Gamez, name => world.Runtime.Handles(name));
        var pickup = RowFor(rows, "lookat_copilotpkup")
            ?? throw new InvalidOperationException("CM07 carries no copilot pickup approach row");
        Node3D? caboose = null;
        foreach (var def in world.Session.Program.ByAnimName("trigger_copilot"))
        {
            var anchors = world.Runtime.AnchorsOf(def);
            if (anchors.Count > 0)
            {
                caboose = anchors[0];
                break;
            }
        }
        ctx.Check(caboose != null, $"CM07's trigger_copilot resolves its authored caboose anchor");
        if (caboose == null)
        {
            return;
        }

        WithTrigger(ctx, world, director, rows, (trigger, cutscene, rig, graph) =>
        {
            ctx.Same(0, world.Runtime.FindNodes(pickup.Node).Count,
                $"the pickup approach starts outside the world as library content");
            int armedBefore = trigger.Armed;
            world.Runtime.PlayerPositions = () => new[] { caboose.GlobalPosition };
            rig.Setup(new FlightModel(PlaneStats.Load(ctx.ZrdrPath, PlaneNode)), null,
                new CamParams(), caboose.GlobalPosition, caboose.GlobalPosition + Vector3.Forward,
                0f, 0f);
            world.Runtime.Advance(StepDt);
            trigger.Tick();

            var agents = world.Runtime.FindNodes("pickup_agent");
            var sensors = world.Runtime.FindNodes("ladder_pickup_sensor");
            var approaches = world.Runtime.FindNodes(pickup.Node);
            report.AppendLine($"near caboose: agent={agents.Count} sensor={sensors.Count} " +
                $"approach={approaches.Count} armed={armedBefore}->{trigger.Armed}");
            ctx.Same(1, agents.Count,
                $"trigger_copilot stages the passenger and flare rig on the caboose");
            ctx.Same(1, sensors.Count,
                $"the same call stages the ladder pickup sensor");
            ctx.Same(1, approaches.Count,
                $"and attaches the authored docking approach under that sensor");
            ctx.Same(armedBefore + 1, trigger.Armed,
                $"the landing trigger discovers the approach created after its initial bind");
            if (approaches.Count == 0)
            {
                return;
            }

            // The train's own definition started pickup_timing at the world build; its first
            // open phase runs 12.36 s to 16.45 s into the run.
            var arm = world.Runtime.FindNodes(LandingApproaches.ArmNode, approaches[0]);
            for (float t = 0f; t < 14f; t += StepDt)
            {
                world.Runtime.Advance(StepDt);
                trigger.Tick();
            }
            ctx.Check(arm.Count > 0 && arm[0].Visible,
                $"the train's pickup_timing opens land_on in its first flyable phase");
            ctx.Check(Fly(ctx, world, trigger, cutscene, graph, rig, pickup, report),
                $"flying CM07's staged train approach starts '{pickup.Anim}'");
            ctx.Check(cutscene.Playing,
                $"'{pickup.Anim}' takes ownership of the session instead of ending immediately");
            var gated = ObjectiveForInactive(script, "pickup_objective");
            ctx.Check(gated != null,
                $"CM07 gates an objective on pickup_objective becoming inactive");
            if (gated == null)
            {
                return;
            }

            var objectiveNodes = world.Runtime.FindNodes("pickup_objective");
            ctx.Check(objectiveNodes.Count > 0 && !objectiveNodes[0].Visible,
                $"the pickup calls got_the_pilot and deactivates pickup_objective");
            var authoredCameras = world.Runtime.FindNodes(CutsceneController.CameraNode);
            var authoredCamera = authoredCameras.Count > 0 ? authoredCameras[0] : null;
            var cameraParent = authoredCamera?.GetParent() as Node3D;
            float agentFacing = authoredCamera != null && agents.Count > 0
                ? -authoredCamera.GlobalBasis.Z.Dot(
                    authoredCamera.GlobalPosition.DirectionTo(agents[0].GlobalPosition))
                : -1f;
            report.AppendLine($"pickup camera=({ctx.Camera.GlobalPosition.X:0.#}," +
                $"{ctx.Camera.GlobalPosition.Y:0.#},{ctx.Camera.GlobalPosition.Z:0.#}) " +
                $"authored-parent={cameraParent?.Name} parent-distance=" +
                $"{(cameraParent?.GlobalPosition.DistanceTo(ctx.Camera.GlobalPosition) ?? -1f):0.#} " +
                $"agent-facing={agentFacing:0.##}");
            ctx.Check(cameraParent?.Name == "caboose"
                    && cameraParent.GlobalPosition.DistanceTo(ctx.Camera.GlobalPosition) < 100f
                    && agentFacing > 0f,
                $"the player view follows the pickup camera beside the caboose and faces the passenger");

            float played = 0f;
            for (float t = 0f; t < PlayBudgetS
                    && (cutscene.Playing || !graph.CompletedOf(gated.Number)); t += StepDt)
            {
                world.Runtime.Advance(StepDt);
                cutscene.Tick();
                director.Step(StepDt);
                if (cutscene.Playing)
                {
                    played += StepDt;
                }
            }

            report.AppendLine($"pickup episode ran {played:0.##} s; " +
                $"OBJECTIVE{gated.Number} completed={graph.CompletedOf(gated.Number)}");
            ctx.Check(!cutscene.Playing && played > 2f,
                $"the authored pickup camera episode runs to its handoff");
            ctx.Check(graph.CompletedOf(gated.Number),
                $"finishing the train pickup clears OBJECTIVE{gated.Number}");
        });
    }

    // CM07's pickup as the passenger, the flare and the ladder see it. The train is the mission's
    // own moving consist, so "rides the train" is measured as a constant offset from the caboose
    // while the caboose itself moves; the ladder switch is the session's own runtime, bound here
    // the way GameSession binds it, and driven by the rig's attitude and position.
    private static void DriveTrainPickupRide(TestContext ctx, TestWorld world, CampaignDirector director,
        ObjectiveScript script, string missionZrdr, StringBuilder report)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, world.Chapter);
        var rows = LandingApproaches.Resolve(
            chapterZrdr, world.Gamez, name => world.Runtime.Handles(name));
        var pickup = RowFor(rows, "lookat_copilotpkup")
            ?? throw new InvalidOperationException("CM07 carries no copilot pickup approach row");
        // The consist's caboose is the one caboosewave's own symbol table binds (the chapter also
        // carries an unrelated caboose.flt in the rail yard, which name matching would reach).
        Node3D? caboose = null;
        foreach (var def in world.Session.Program.ByAnimName("caboosewave"))
        {
            if (def.NodeRefs.TryGetValue("caboose", out int index))
            {
                caboose = world.Runtime.FindNodeByIndex(index);
                break;
            }
        }
        ctx.Check(caboose != null, $"caboosewave's symbol table binds the consist's caboose");
        if (caboose == null)
        {
            return;
        }

        var pickups = Pickups.Load(missionZrdr);
        WithTrigger(ctx, world, director, rows, (trigger, cutscene, rig, graph) =>
        {
            var ladder = new LadderSwitchRuntime();
            ctx.Host.AddChild(ladder);
            try
            {
                // The one-human field this leg flies. Wrapped here rather than threaded through
                // WithTrigger: the ladder is the only leg that needs the rig itself.
                var field = new[] { new PlayerRig { Index = 0, Camera = ctx.Camera, HudParent = ctx.Host, Controller = rig } };
                ladder.Bind(world.Runtime, cutscene, () => field, pickups);
                world.Runtime.PlayerPositions = () => new[] { caboose.GlobalPosition };
                rig.Setup(new FlightModel(PlaneStats.Load(ctx.ZrdrPath, PlaneNode)), null,
                    new CamParams(), caboose.GlobalPosition, caboose.GlobalPosition + Vector3.Forward,
                    0f, 0f);
                world.Runtime.Advance(StepDt);
                trigger.Tick();

                var agents = world.Runtime.FindNodes("pickup_agent");
                ctx.Same(1, agents.Count, $"trigger_copilot stages the passenger on the caboose");
                if (agents.Count == 0)
                {
                    return;
                }

                var agent = agents[0];
                report.AppendLine($"passenger chain: {ChainOf(agent)}; caboose chain: {ChainOf(caboose)}; " +
                    $"top-level={agent.TopLevel} states: caboosewave={world.Runtime.AnimStateOf("caboosewave")} " +
                    $"train_on_track={world.Runtime.AnimStateOf("train_on_track")} " +
                    $"pickup_timing={world.Runtime.AnimStateOf("pickup_timing")} " +
                    $"unhandled add-child={(world.Runtime.UnhandledEventCounts.TryGetValue("ObjectAddChild", out int addChild) ? addChild : 0)}");
                ctx.Check(IsUnder(agent, caboose),
                    $"the passenger is a child of the caboose, not a free node beside it");
                ctx.Check(agent.Visible, $"and the passenger is active");
                var cabooseBefore = caboose.GlobalPosition;
                var offsetBefore = caboose.GlobalTransform.AffineInverse() * agent.GlobalPosition;
                for (float t = 0f; t < 3f; t += StepDt)
                {
                    world.Runtime.Advance(StepDt);
                    trigger.Tick();
                }
                float travelled = caboose.GlobalPosition.DistanceTo(cabooseBefore);
                var offsetAfter = caboose.GlobalTransform.AffineInverse() * agent.GlobalPosition;
                report.AppendLine($"caboose travelled {travelled:0.#} m in 3 s; passenger offset " +
                    $"{offsetBefore.DistanceTo(offsetAfter):0.###} m from where it stood");
                ctx.Check(travelled > 1f, $"the train is moving on its track");
                ctx.Check(offsetBefore.DistanceTo(offsetAfter) < 0.5f,
                    $"the passenger rides the caboose instead of staying where it spawned");

                // The pickup timing, started by the train's own definition, opens the switch in
                // the phases of the track loop where the pickup is flyable; the wave and its flare
                // are what the switch being active selects, and the passenger lies flat otherwise.
                float waited = 0f;
                for (; waited < 60f && world.Runtime.AnimStateOf("waveloop") != 2; waited += StepDt)
                {
                    world.Runtime.Advance(StepDt);
                    trigger.Tick();
                }
                report.AppendLine($"waveloop live after {waited:0.#} s more");
                var switches = world.Runtime.FindNodes("copilot_pickup_switch");
                var sensors = world.Runtime.FindNodes("ladder_pickup_sensor");
                var cones = world.Runtime.FindNodes("agent_approach_cone");
                report.AppendLine($"switch chain: {(switches.Count > 0 ? ChainOf(switches[0]) : "-")} " +
                    $"top-level={(switches.Count > 0 ? switches[0].TopLevel.ToString() : "-")}; sensor chain: " +
                    $"{(sensors.Count > 0 ? ChainOf(sensors[0]) : "-")} top-level=" +
                    $"{(sensors.Count > 0 ? sensors[0].TopLevel.ToString() : "-")} caboose-distance=" +
                    $"{(sensors.Count > 0 ? sensors[0].GlobalPosition.DistanceTo(caboose.GlobalPosition) : -1f):0.#}; " +
                    $"cone chain: {(cones.Count > 0 ? ChainOf(cones[0]) : "-")} caboose-distance=" +
                    $"{(cones.Count > 0 ? cones[0].GlobalPosition.DistanceTo(caboose.GlobalPosition) : -1f):0.#}");
                report.AppendLine($"then: switch={switches.Count} visible=" +
                    $"{(switches.Count > 0 ? switches[0].Visible.ToString() : "-")} sensor={sensors.Count} visible=" +
                    $"{(sensors.Count > 0 ? sensors[0].Visible.ToString() : "-")} " +
                    $"states: caboosewave={world.Runtime.AnimStateOf("caboosewave")} waveloop={world.Runtime.AnimStateOf("waveloop")} " +
                    $"hit_the_deck={world.Runtime.AnimStateOf("hit_the_deck")} get_up={world.Runtime.AnimStateOf("get_up")} " +
                    $"pickup_timing={world.Runtime.AnimStateOf("pickup_timing")}; rig inplay={rig.InPlay} held={rig.Held} " +
                    $"level={LadderSwitch.IsLevel(rig.Attitude)} sensor-distance=" +
                    $"{(sensors.Count > 0 ? sensors[0].GlobalPosition.DistanceTo(rig.WorldPosition) : -1f):0.#}");
                ctx.Same(2, world.Runtime.AnimStateOf("waveloop"),
                    $"with the pickup switch open the passenger waves (waveloop live)");
                ctx.Same(2, world.Runtime.AnimStateOf("pickup_flare"),
                    $"and holds the lit flare (pickup_flare live)");
                var flares = world.Runtime.FindNodes("ballflare.flt");
                var flare = flares.Count > 0 ? flares[0] : null;
                var trails = world.Runtime.Emitters.Census.Where(r =>
                    string.Equals(r.Name, "flaretrail", StringComparison.OrdinalIgnoreCase)).ToList();
                report.AppendLine($"flare={flares.Count} under passenger=" +
                    $"{(flare != null && IsUnder(flare, agent))} visible={flare?.Visible} " +
                    $"trail={(trails.Count == 0 ? "absent" : trails[0].Emitting ? "emitting" : "built")}" +
                    $"{(trails.Count > 0 ? $" on '{trails[0].Host}'" : "")}; pickup_flare=" +
                    $"{world.Runtime.AnimStateOf("pickup_flare")} unhandled: " + string.Join(", ",
                        world.Runtime.UnhandledEventCounts
                            .Where(kv => kv.Key.StartsWith("PufferState", StringComparison.Ordinal)
                                || kv.Key.StartsWith("ObjectAddChild", StringComparison.Ordinal))
                            .Select(kv => $"{kv.Key}={kv.Value}")));
                ctx.Check(flare != null && IsUnder(flare, agent) && flare.Visible,
                    $"the flare disc hangs from the passenger's hand and is drawn");
                ctx.Check(trails.Count > 0,
                    $"the flare's smoke trail emitter is asserted on the passenger's hand");

                // Which hand: a same-named node on the parked library figure would emit where
                // nobody is standing, and the census row's NAME alone cannot tell the two apart.
                var trailHost = trails.Count > 0 ? trails[0].HostNode : null;

                // An emitter can read "emitting" and lay nothing, and the director above holds a
                // fake (see TrainPickupRide), so the sprites are asserted one seam lower: the
                // mission's own payload over a recording renderer, along this hand's own poses.
                var smokeState = FlareTrailState(world, agent);
                var gpu = new RecordingEmitterRenderer();
                var smoke = smokeState != null ? Puffer.CreateWith(smokeState, gpu, sustained: true) : null;
                if (smoke != null)
                {
                    ctx.Host.AddChild(smoke);
                }
                var handBefore = trailHost?.GlobalPosition ?? Vector3.Zero;
                for (float t = 0f; t < FlareSampleS; t += StepDt)
                {
                    world.Runtime.Advance(StepDt);
                    trigger.Tick();
                    if (smoke != null && trailHost != null)
                    {
                        smoke.Emit(trailHost.GlobalPosition, trailHost.GlobalBasis, StepDt);
                        smoke._Process(StepDt);
                    }
                }
                float handTravel = trailHost != null
                    ? trailHost.GlobalPosition.DistanceTo(handBefore) : -1f;
                // What the authored cadence owes over that motion, halved: the hand's path is not a
                // straight line, so the metres it covers are an upper bound on the trail's length.
                int owed = smokeState is { DistanceInterval: > 0f } && handTravel > 0f
                    ? (int)(handTravel / smokeState.DistanceInterval) / 2 : 0;
                report.AppendLine($"flaretrail host chain: {(trailHost != null ? ChainOf(trailHost) : "-")} " +
                    $"under-passenger={(trailHost != null && IsUnder(trailHost, agent))} " +
                    $"hand-distance={(trailHost != null ? trailHost.GlobalPosition.DistanceTo(agent.GlobalPosition) : -1f):0.##}; " +
                    $"state interval={smokeState?.DistanceInterval:0.##} m frames={smokeState?.TextureSequence.Count}" +
                    $"+{smokeState?.Textures.Count} size={smokeState?.SizeMin:0.###}-{smokeState?.SizeMax:0.###}; " +
                    $"hand travelled {handTravel:0.##} m in {FlareSampleS:0.##} s and drew {gpu.MaxShown} " +
                    $"sprite(s), owed at least {owed}");
                ctx.Check(trailHost != null && IsUnder(trailHost, agent),
                    $"the trail's host node is the staged passenger's own hand, not the library figure's");
                ctx.Check(smokeState is { DistanceInterval: > 0f }
                        && smokeState.TextureSequence.Count + smokeState.Textures.Count > 0,
                    $"the mission's own flaretrail state is a distance trail with sprites to draw");
                ctx.Check(gpu.MaxShown > 0 && gpu.MaxShown >= owed,
                    $"and driven along that hand's motion it lays the smoke the cadence owes");
                smoke?.QueueFree();

                // The ladder: level inside the sensor drops it, the settle callback lands it. The
                // train has travelled on since the rig was parked, so the rig is re-parked level
                // beside the sensor as it stands now.
                if (sensors.Count > 0)
                {
                    var beside = sensors[0].GlobalPosition + Vector3.Up * 10f;
                    rig.Setup(new FlightModel(PlaneStats.Load(ctx.ZrdrPath, PlaneNode)), null,
                        new CamParams(), beside, beside + Vector3.Forward, 0f, 0f);
                }
                ladder.Tick();
                report.AppendLine($"ladder after a level tick inside the sensor: " +
                    $"{ladder.LastStarted ?? "-"} ({ladder.State})");
                ctx.Check(string.Equals(ladder.LastStarted, LadderSwitch.DropAnim, StringComparison.Ordinal),
                    $"a level aircraft inside the pickup sensor starts drop_ladder");
                ctx.Same(2, world.Runtime.AnimStateOf(LadderSwitch.DropAnim),
                    $"drop_ladder has a live instance");
                for (float t = 0f; t < 3f && ladder.State != LadderState.Deployed; t += StepDt)
                {
                    world.Runtime.Advance(StepDt);
                    ladder.Tick();
                }
                var ladders = world.Runtime.FindNodes("rope_ladder");
                report.AppendLine($"ladder settled: {ladder.State}; rope_ladder nodes={ladders.Count} " +
                    $"visible={(ladders.Count > 0 ? ladders[0].Visible.ToString() : "-")} " +
                    $"ladder_pos nodes={world.Runtime.FindNodes("ladder_pos").Count} (the harness rig " +
                    $"carries no airframe-stage ladder_pos, so the rungs are the session's to show)");
                ctx.Check(ladder.State == LadderState.Deployed,
                    $"the drop's own CALLBACK 123 settles the switch deployed");

                // The wind sway: gen_drop_ladder leaves each rung's ladder_loop script in an
                // infinite LOOP once the drop has landed, so a settled ladder keeps moving. The
                // rungs hinge about their own origins, so the sample is a point out along a rung.
                var settledRung = world.Runtime.FindNodes("rung1");
                var settledLadder = ladders.Count > 0 ? ladders[0] : null;
                var swayBefore = RungInLadder(settledRung, settledLadder);
                float sway = 0f;
                for (float t = 0f; t < FlareSampleS; t += StepDt)
                {
                    world.Runtime.Advance(StepDt);
                    var swayNow = RungInLadder(settledRung, settledLadder);
                    sway += swayNow.DistanceTo(swayBefore);
                    swayBefore = swayNow;
                }
                report.AppendLine($"settled ladder over {FlareSampleS:0.##} s: rung1 swung " +
                    $"{sway:0.###} m in the ladder's own frame");
                ctx.Check(sway > 0.01f,
                    $"the deployed ladder keeps swinging on its own looped wind script");

                // The docking cone, then the cutscene's own call chain.
                int waitsBefore = world.Runtime.WaitsInstalled;
                ctx.Check(Fly(ctx, world, trigger, cutscene, graph, rig, pickup, report),
                    $"flying CM07's staged train approach starts '{pickup.Anim}'");
                ctx.Same(2, world.Runtime.AnimStateOf("caboosepickup"),
                    $"'{pickup.Anim}' calls caboosepickup and it has a live instance");
                ctx.Same(waitsBefore + 1, world.Runtime.WaitsInstalled,
                    $"so the WAIT_FOR_COMPLETION on it holds instead of finding nothing");
                ctx.Check(IsUnder(agent, caboose),
                    $"the passenger is still the caboose's child through the pickup");

                // The climb REPLACES the hanging ladder's wind loop (docs/org/ladderSwitch.md), so
                // what is owed is cabpkup_ladder's own motion, read in the ladder root's frame:
                // measured globally the caboose's 60-odd metres would drown it.
                var rung = world.Runtime.FindNodes("rung1");
                var ladderRoot = ladders.Count > 0 ? ladders[0] : null;
                var rungBefore = RungInLadder(rung, ladderRoot);
                float rungTravel = 0f;
                float played = 0f;
                float climb = 0f;
                int cabpkupLadder = 0;
                for (float t = 0f; t < PlayBudgetS && cutscene.Playing; t += StepDt)
                {
                    world.Runtime.Advance(StepDt);
                    cutscene.Tick();
                    director.Step(StepDt);
                    played += StepDt;
                    if (world.Runtime.AnimStateOf("caboosepickup") == 2)
                    {
                        climb += StepDt;
                        cabpkupLadder = Mathf.Max(cabpkupLadder,
                            world.Runtime.AnimStateOf(ClimbLadderAnim));
                        var rungNow = RungInLadder(rung, ladderRoot);
                        rungTravel += rungNow.DistanceTo(rungBefore);
                        rungBefore = rungNow;
                    }
                }
                report.AppendLine($"pickup episode ran {played:0.##} s, caboosepickup live for " +
                    $"{climb:0.##} s of it; {ClimbLadderAnim} reached state {cabpkupLadder}, " +
                    $"drop_ladder state {world.Runtime.AnimStateOf(LadderSwitch.DropAnim)}, " +
                    $"rung1 swung {rungTravel:0.###} m in the ladder's own frame over the climb; " +
                    $"ladder chain {(ladderRoot != null ? ChainOf(ladderRoot) : "-")}; script misses: " +
                    string.Join(", ", world.Runtime.UnhandledEventCounts
                        .Where(kv => kv.Key.StartsWith("ObjectMotionSiScript", StringComparison.Ordinal))
                        .Select(kv => $"{kv.Key}={kv.Value}")));
                ctx.Check(!cutscene.Playing && played > 2f,
                    $"the authored pickup camera episode runs to its handoff");
                ctx.Check(climb > 1f, $"the person's climb plays for its scripted length");
                ctx.Same(2, cabpkupLadder,
                    $"'{ClimbLadderAnim}' runs over the climb, so the ladder is animated rather than parked");
                ctx.Check(rungTravel > 0.05f,
                    $"and its rungs actually move in the ladder's own frame while the person climbs them");
            }
            finally
            {
                ladder.Free();
            }
        });
    }

    // The flare's smoke as the mission authors it: waveloop's own PUFFER_STATE payload, decoded the
    // way the runtime decodes it. Read out of the definition rather than restated here, so a change
    // to the decode moves the assertion with it.
    private static PufferState? FlareTrailState(TestWorld world, Node3D agent)
    {
        foreach (var def in world.Session.Program.ByAnimName("waveloop"))
        {
            if (!def.NodeRefs.ContainsKey(AnimRuntime.NameOf(agent)))
            {
                continue;
            }
            foreach (var seq in def.Sequences)
            {
                foreach (var ev in seq.Events)
                {
                    if (ev.Kind == "PufferState" && (ev.Data.Num("active_state") ?? 0f) >= 1f)
                    {
                        return PufferState.FromAnimEvent(ev.Data);
                    }
                }
            }
        }
        return null;
    }

    // A rung's place in the ladder root's frame, so the train carrying the whole ladder does not
    // read as the rungs moving. Zero when either node is missing, which the caller's own check sees.
    // A metre out along the rung's own axis, not its origin: a rung hinges about its own origin,
    // so a position-only read is blind to the whole animation.
    private static Vector3 RungInLadder(IReadOnlyList<Node3D> rung, Node3D? ladder) =>
        rung.Count > 0 && ladder != null
            ? ladder.GlobalTransform.AffineInverse() * (rung[0].GlobalTransform * Vector3.Right)
            : Vector3.Zero;

    private static string ChainOf(Node node)
    {
        var names = new List<string>();
        for (Node? at = node; at != null && names.Count < 8; at = at.GetParent())
        {
            names.Add(at.Name);
        }
        return string.Join(" < ", names);
    }

    private static bool IsUnder(Node node, Node ancestor)
    {
        for (var at = node.GetParent(); at != null; at = at.GetParent())
        {
            if (at == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    // CM11's trailer pickup: unlike CM07's, the cone is staged by an objective's WAKE_ANIM rather
    // than a range-triggered call, so this is the director's own trigger path. The staging
    // definition and the objective that wakes it are read out of the mission's data.
    private static void DriveTrailerPickup(TestContext ctx, TestWorld world, CampaignDirector director,
        ObjectiveScript script, string missionZrdr, StringBuilder report)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, world.Chapter);
        var rows = LandingApproaches.Resolve(
            chapterZrdr, world.Gamez, name => world.Runtime.Handles(name));
        var pickup = RowFor(rows, "lookat_pickfordpkup")
            ?? throw new InvalidOperationException("CM11 carries no trailer pickup approach row");
        var staging = StagerOf(world, pickup.Node);
        var caller = staging?.Anim is { } stagerAnim ? CallerOf(script, stagerAnim) : null;
        report.AppendLine($"'{pickup.Node}' is staged by '{staging?.Anim ?? "-"}' under " +
            $"'{staging?.Parent ?? "-"}', woken by OBJECTIVE{caller?.Number.ToString() ?? "?"}");
        ctx.Check(staging != null, $"a definition's OBJECT_ADD_CHILD stages '{pickup.Node}'");
        ctx.Check(caller != null, $"and an objective's WAKE_ANIM wakes that definition");
        if (staging is not { } stager || caller is not { } wakes)
        {
            return;
        }

        WithTrigger(ctx, world, director, rows, (trigger, cutscene, rig, graph) =>
        {
            ctx.Same(0, world.Runtime.FindNodes(pickup.Node).Count,
                $"the pickup approach starts outside the world as library content");
            int armedBefore = trigger.Armed;
            graph.Wake(wakes.Number);
            world.Runtime.Advance(StepDt);
            graph.Step(StepDt);
            trigger.Tick();

            var approaches = world.Runtime.FindNodes(pickup.Node);
            var parent = approaches.Count > 0 ? approaches[0].GetParent() as Node3D : null;
            report.AppendLine($"after the wake: approach={approaches.Count} parent=" +
                $"{parent?.Name ?? "-"} armed={armedBefore}->{trigger.Armed}");
            ctx.Same(1, approaches.Count,
                $"OBJECTIVE{wakes.Number}'s WAKE_ANIM '{stager.Anim}' stages the approach cone from its library root");
            ctx.Check(parent != null && string.Equals(parent.Name, stager.Parent, StringComparison.OrdinalIgnoreCase),
                $"under '{stager.Parent}', the node the event names, so the cone rides the trailer");
            ctx.Same(armedBefore + 1, trigger.Armed,
                $"the landing trigger discovers the approach staged after its initial bind");
            if (approaches.Count == 0)
            {
                return;
            }

            var arm = world.Runtime.FindNodes(LandingApproaches.ArmNode, approaches[0]);
            ctx.Check(arm.Count > 0 && arm[0].Visible,
                $"the staged cone's land_on is open, this mission authoring no pickup timing in front of it");
            // The approach the original flies: closing on the trailer fires its range-armed
            // definition, whose authored hatch time runs before it raises the actor the pickup's
            // prerequisite requires. The parked wait is the one the train drive gives its timing.
            var site = approaches[0].GlobalPosition;
            rig.Setup(new FlightModel(PlaneStats.Load(ctx.ZrdrPath, PlaneNode)), null,
                new CamParams(), site, site + Vector3.Forward, 0f, 0f);
            for (float t = 0f; t < TrailerApproachS; t += StepDt)
            {
                world.Runtime.Advance(StepDt);
            }

            var prereqs = PrerequisitesOf(world, pickup.Anim);
            report.AppendLine($"'{pickup.Anim}' requires " + string.Join(", ",
                prereqs.Select(p => $"{p.Node}={(p.Active ? "active" : "inactive")} (reads {p.Met})")));
            ctx.Check(prereqs.Count > 0 && prereqs.All(p => p.Met),
                $"closing on the trailer meets '{pickup.Anim}'s own node-state prerequisite");
            ctx.Check(Fly(ctx, world, trigger, cutscene, graph, rig, pickup, report),
                $"flying CM11's staged trailer approach starts '{pickup.Anim}'");
            ctx.Check(cutscene.Playing,
                $"'{pickup.Anim}' takes ownership of the session instead of ending immediately");

            float played = 0f;
            for (float t = 0f; t < PlayBudgetS
                    && (cutscene.Playing || !graph.CompletedOf(wakes.Number)); t += StepDt)
            {
                world.Runtime.Advance(StepDt);
                cutscene.Tick();
                director.Step(StepDt);
                if (cutscene.Playing)
                {
                    played += StepDt;
                }
            }

            report.AppendLine($"pickup episode ran {played:0.##} s; " +
                $"OBJECTIVE{wakes.Number} completed={graph.CompletedOf(wakes.Number)}");
            ctx.Check(!cutscene.Playing && played > 2f,
                $"the authored pickup episode runs to its handoff");
            ctx.Check(graph.CompletedOf(wakes.Number),
                $"finishing the trailer pickup clears OBJECTIVE{wakes.Number}, the dock");
        });
    }

    // CM16's armoured-car pickup. The mission stages it itself: the train's definition opens the
    // approach cone, the three car guns going down and the fighters-destroyed actor let the waving
    // passenger stand up, and only then does the row's own prerequisite read met.
    private static void DriveCarPickup(TestContext ctx, TestWorld world, CampaignDirector director,
        ObjectiveScript script, string missionZrdr, StringBuilder report)
    {
        string chapterZrdr = SessionPaths.ChapterZrdr(ctx.DataRoot, world.Chapter);
        var rows = LandingApproaches.Resolve(
            chapterZrdr, world.Gamez, name => world.Runtime.Handles(name));
        var pickup = RowFor(rows, CarPickupAnim)
            ?? throw new InvalidOperationException("CM16 carries no armoured-car pickup approach row");
        var credited = ObjectiveForAnimState(script, GotSparksAnim);
        var wrecked = ObjectiveForInactive(script, TrainNode);
        var train = CallerOf(script, TrainAnim);
        var guns = ObjectiveForInactive(script, CarGunNode);
        report.AppendLine($"row '{pickup.Anim}' on '{pickup.Node}'; rescue is " +
            $"OBJECTIVE{credited?.Number.ToString() ?? "?"} (ANIM_STATE {GotSparksAnim} EXECUTED), " +
            $"wreck is OBJECTIVE{wrecked?.Number.ToString() ?? "?"} (INACTIVE {TrainNode}/healthy), " +
            $"staged by OBJECTIVE{train?.Number.ToString() ?? "?"} and " +
            $"OBJECTIVE{guns?.Number.ToString() ?? "?"}");
        ctx.Check(credited != null, $"CM16 credits the rescue on '{GotSparksAnim}' reaching EXECUTED");
        ctx.Check(wrecked != null, $"and reads the destroyed train on an objective of its own");
        ctx.Check(train != null && guns != null, $"and stages the pickup from two of its own objectives");
        if (credited is not { } rescue || wrecked is not { } wreck
            || train is not { } runs || guns is not { } down)
        {
            return;
        }

        WithTrigger(ctx, world, director, rows, (trigger, cutscene, rig, graph) =>
        {
            int starts = 0;
            float clock = -1f;
            var wasStarted = world.Runtime.OnInstanceStarted;
            var wasFinished = world.Runtime.OnInstanceFinished;
            world.Runtime.OnInstanceStarted = (def, anchor) =>
            {
                if (string.Equals(def.AnimName, pickup.Anim, StringComparison.OrdinalIgnoreCase))
                {
                    starts++;
                }

                if (clock >= 0f)
                {
                    report.AppendLine($"  t={clock:0.00} start '{def.AnimName}' ({def.Name})");
                }

                wasStarted?.Invoke(def, anchor);
            };
            world.Runtime.OnInstanceFinished = (def, anchor) =>
            {
                if (clock >= 0f)
                {
                    report.AppendLine($"  t={clock:0.00} end   '{def.AnimName}' ({def.Name})");
                }

                wasFinished?.Invoke(def, anchor);
            };
            try
            {
                RunTheCarPickup(ctx, world, director, graph, trigger, cutscene, rig, pickup,
                    rescue, wreck, runs, down, () => starts, t => clock = t, report);
            }
            finally
            {
                world.Runtime.OnInstanceStarted = wasStarted;
                world.Runtime.OnInstanceFinished = wasFinished;
            }
        });
    }

    private static void RunTheCarPickup(
        TestContext ctx, TestWorld world, CampaignDirector director, ObjectiveGraph graph,
        LandingApproachRuntime trigger, CutsceneController cutscene, FlightController rig,
        LandingApproach pickup, ObjectiveDef rescue, ObjectiveDef wreck, ObjectiveDef train,
        ObjectiveDef guns, Func<int> starts, Action<float> clock, StringBuilder report)
    {
        // The mission's own intro drives the same `player` node the pickup does, so it has to be
        // out of the way before anything here is timed against it.
        float intro = 0f;
        for (; intro < IntroSettleS
            && world.Runtime.AnimStateOf(IntroAnim) == AnimRunning; intro += StepDt)
        {
            world.Runtime.Advance(StepDt);
        }

        report.AppendLine($"'{IntroAnim}' ran out at t={intro:0.#}s");
        ctx.Check(world.Runtime.AnimStateOf(IntroAnim) != AnimRunning,
            $"the mission's own intro is over before the pickup is staged");

        graph.Wake(train.Number);
        ProbeRunner.DriveInactive(world.Runtime, guns);
        graph.Wake(guns.Number);

        float staged = -1f;
        for (float t = 0f; t < CarStageBudgetS && staged < 0f; t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            graph.Step(StepDt);
            var reads = PrerequisitesOf(world, pickup.Anim);
            if (reads.Count > 0 && reads.All(p => p.Met))
            {
                staged = t;
            }
        }

        var prereqs = PrerequisitesOf(world, pickup.Anim);
        report.AppendLine($"staged at t={staged:0.#}s: " + string.Join(", ",
            prereqs.Select(p => $"{p.Node}={(p.Active ? "active" : "inactive")} (reads {p.Met})")));
        ctx.Check(staged >= 0f,
            $"the mission's own chain meets '{pickup.Anim}'s node prerequisites");
        ctx.Check(!graph.CompletedOf(wreck.Number),
            $"and leaves OBJECTIVE{wreck.Number} uncompleted: the train is still healthy");
        if (staged < 0f)
        {
            return;
        }

        clock(0f);
        ctx.Check(Fly(ctx, world, trigger, cutscene, graph, rig, pickup, report),
            $"flying CM16's armoured-car approach starts '{pickup.Anim}'");
        ctx.Check(cutscene.Playing,
            $"'{pickup.Anim}' takes ownership of the session instead of ending immediately");
        if (!cutscene.Playing)
        {
            return;
        }

        float got = -1f, rescued = -1f, wreckedAt = -1f, played = 0f, ended = -1f;
        for (float t = 0f; t < PlayBudgetS && cutscene.Playing; t += StepDt)
        {
            clock(t);
            world.Runtime.Advance(StepDt);
            trigger.Tick();
            cutscene.Tick();
            director.Step(StepDt);
            played += StepDt;
            if (got < 0f && world.Runtime.AnimStateOf(GotSparksAnim) == AnimExecuted)
            {
                got = played;
            }

            if (rescued < 0f && graph.CompletedOf(rescue.Number))
            {
                rescued = played;
            }

            if (wreckedAt < 0f && graph.CompletedOf(wreck.Number))
            {
                wreckedAt = played;
            }

            if (ended < 0f && graph.Ended)
            {
                ended = played;
            }
        }

        report.AppendLine($"episode ran {played:0.##} s over {starts()} start(s): " +
            $"'{GotSparksAnim}' EXECUTED at {got:0.##}s, OBJECTIVE{rescue.Number} (rescue) at " +
            $"{rescued:0.##}s, OBJECTIVE{wreck.Number} (wreck) at {wreckedAt:0.##}s, " +
            $"outcome {graph.Outcome} at {ended:0.##}s; rescue alive={graph.AliveOf(rescue.Number)}, " +
            $"mask 0x{graph.CompletedMask:x}");
        ctx.Same(1, starts(),
            $"the row fires once: a cutscene owning the session locks its own trigger out");

        // The film's own two moments, against the authored script lengths rather than a watched
        // value. Both sums are on WreckAtS/GotSparksAtS where they are declared.
        ctx.Check(Math.Abs(wreckedAt - WreckAtS) < FilmToleranceS,
            $"the film calls destroy_car01 on its authored beat: OBJECTIVE{wreck.Number} at {wreckedAt:0.##}s (authored {WreckAtS:0.##}s)");
        ctx.Check(Math.Abs(got - GotSparksAtS) < FilmToleranceS,
            $"and reaches '{GotSparksAnim}' on its own: EXECUTED at {got:0.##}s (authored {GotSparksAtS:0.##}s)");
        ctx.Check(graph.Outcome == MissionOutcome.Won,
            $"the mission-completion code the film raises wins CM16 outcome={graph.Outcome}");
    }

    // CM07's hangar drop, every name read out of the mission's own data: the one cutscene
    // definition it range-gates, whatever calls that, and the objective node the drop's own reset
    // block flips through its CALL_ANIMATION.
    private static void DriveHangarDrop(TestContext ctx, TestWorld world, CampaignDirector director,
        ObjectiveScript script, string missionZrdr, StringBuilder report)
    {
        var cutscenes = MissionCutscenes.AnimNames(missionZrdr);
        report.AppendLine($"cutscene definitions: {string.Join(", ", cutscenes)}");
        var drop = RangeGatedOf(world, cutscenes);
        ctx.Check(drop?.AnimName != null,
            $"CM07 loads a range-gated cutscene definition out of its own cutscenes directory");
        if (drop?.AnimName is not { } dropAnim)
        {
            return;
        }

        string? caller = CallerAnimOf(world, dropAnim);
        string? node = ResetObjectiveNodeOf(world, script, drop);
        var gated = node != null ? ObjectiveForInactive(script, node) : null;
        report.AppendLine($"'{dropAnim}' range={Mathf.Sqrt(drop.RangeMax):0} m " +
            $"called by '{caller ?? "-"}', reset clears '{node ?? "-"}' " +
            $"gating OBJECTIVE{gated?.Number ?? -1}");
        ctx.Check(caller != null,
            $"an ambient definition calls '{dropAnim}', which is the only thing that arms it");
        ctx.Check(gated != null,
            $"and '{dropAnim}' clears the node an objective waits on, through its own RESET_STATE");
        if (caller == null || node == null || gated == null)
        {
            return;
        }

        RunTheDrop(ctx, world, director, drop, caller, node, gated, report);
    }

    // The armed-then-flown drive. The player is parked well outside the band for the call, then
    // put on the hangar, which is the only difference between the two halves.
    private static void RunTheDrop(
        TestContext ctx, TestWorld world, CampaignDirector director, AnimDefinition drop,
        string caller, string node, ObjectiveDef gated, StringBuilder report)
    {
        var anchors = world.Runtime.AnchorsOf(drop);
        var anchor = anchors.Count > 0 ? anchors[0] : null;
        ctx.Check(anchor != null, $"'{drop.AnimName}' resolves its authored hangar anchor");
        if (anchor == null)
        {
            return;
        }

        var site = AnimRuntime.VisualOriginOf(anchor);
        WithTrigger(ctx, world, director, Array.Empty<LandingApproach>(),
            (trigger, cutscene, rig, graph) =>
        {
            cutscene.HostDefinitions(ClosureOf(world, drop.AnimName!));
            graph.Wake(gated.Number);
            world.Runtime.PlayerPositions = () => new[] { site + (Vector3.Right * AwayM) };
            world.Runtime.Play(caller);
            for (float t = 0f; t < ArmBudgetS; t += StepDt)
            {
                world.Runtime.Advance(StepDt);
                director.Step(StepDt);
            }

            report.AppendLine($"{AwayM:0} m away: '{drop.AnimName}' state=" +
                $"{world.Runtime.AnimStateOf(drop.AnimName!)} playing={cutscene.Playing} " +
                $"'{node}' built={world.Runtime.FindNodes(node).Count} " +
                $"OBJECTIVE{gated.Number} completed={graph.CompletedOf(gated.Number)}");
            ctx.Same(0, world.Runtime.AnimStateOf(drop.AnimName!),
                $"'{caller}' arms the drop without running it, the player being outside its band");
            ctx.Check(!graph.CompletedOf(gated.Number),
                $"so OBJECTIVE{gated.Number} stays open with the objective awake and stepping");

            world.Runtime.PlayerPositions = () => new[] { site };
            float played = 0f;
            for (float t = 0f; t < PlayBudgetS && (played == 0f || cutscene.Playing); t += StepDt)
            {
                world.Runtime.Advance(StepDt);
                cutscene.Tick();
                director.Step(StepDt);
                played += cutscene.Playing ? StepDt : 0f;
            }

            var placed = world.Runtime.FindNodes(node);
            report.AppendLine($"at the hangar: episode ran {played:0.##} s, " +
                $"'{node}' built={placed.Count} active={Active(world, node)}, " +
                $"OBJECTIVE{gated.Number} completed={graph.CompletedOf(gated.Number)}");
            ctx.Check(played > 0f,
                $"reaching the hangar runs the armed drop and hands it to the cutscene host");
            ctx.Check(placed.Count > 0 && !Active(world, node),
                $"and its RESET_STATE at the handoff stands '{node}' up and clears it");
            ctx.Check(graph.CompletedOf(gated.Number),
                $"which completes OBJECTIVE{gated.Number}, the fly-through the mission asks for");
        });
    }

    // The mission cutscene definition whose EXECUTION_BY_RANGE is an arming gate: a call is its
    // only way in, so a definition the mission also lists in startanims is not this one.
    private static AnimDefinition? RangeGatedOf(TestWorld world, IReadOnlyList<string> cutscenes)
    {
        foreach (string name in cutscenes)
        {
            foreach (var def in world.Session.Program.ByAnimName(name))
            {
                if (def.ByRange && !System.Linq.Enumerable.Contains(world.Session.Program.StartAnims, name))
                {
                    return def;
                }
            }
        }

        return null;
    }

    // The definition whose OBJECT_ADD_CHILD parents the named node, and the parent it names.
    private static (string Anim, string Parent)? StagerOf(TestWorld world, string child)
    {
        foreach (var def in world.Session.Program.Defs)
        {
            foreach (var seq in def.Sequences)
            {
                foreach (var ev in seq.Events)
                {
                    if (ev.Kind == "ObjectAddChild" && ev.Data.Str("child") is { } named
                        && named.Equals(child, StringComparison.OrdinalIgnoreCase)
                        && ev.Data.Str("parent") is { } parent
                        && def.AnimName is { Length: > 0 } anim)
                    {
                        return (anim, parent);
                    }
                }
            }
        }

        return null;
    }

    private static string? CallerAnimOf(TestWorld world, string called)
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

    // What the drop's own RESET_STATE calls, resolved to the node that call deactivates: the
    // objective flag definitions name their node, and the mission gates an INACTIVE on it.
    private static string? ResetObjectiveNodeOf(
        TestWorld world, ObjectiveScript script, AnimDefinition drop)
    {
        if (drop.ResetState == null)
        {
            return null;
        }

        foreach (var ev in drop.ResetState.Events)
        {
            if (ev.Kind != "CallAnimation" || ev.Data.Str("name") is not { } called)
            {
                continue;
            }

            foreach (var def in world.Session.Program.ByAnimName(called))
            {
                if (def.Name is { Length: > 0 } named
                    && ObjectiveForInactive(script, named) != null)
                {
                    return named;
                }
            }
        }

        return null;
    }

    private static bool Active(TestWorld world, string node)
    {
        var found = world.Runtime.FindNodes(node);
        return found.Count > 0 && found[0].Visible;
    }

    private static string PathKey(IReadOnlyList<string> path) => string.Join("/", path);

    // The world node a prerequisite path names, each segment found inside the one before it. Null
    // is the case the gate treats as met, so a suite asserting a gate has to tell it apart.
    private static Node3D? PrereqNodeOf(TestWorld world, IReadOnlyList<string> path)
    {
        Node3D? at = null;
        foreach (string segment in path)
        {
            var found = at == null
                ? world.Runtime.FindNodes(segment)
                : world.Runtime.FindNodes(segment, at);
            if (found.Count == 0)
            {
                return null;
            }

            at = found[0];
        }

        return at;
    }

    // The legs of one row's call closure the data forks on: a definition carrying a REQUIRED
    // node-state ACTIVATION_PREREQUISITE, which is what decides whether its call answers.
    private static IReadOnlyList<AnimDefinition> ForkedLegsOf(TestWorld world, string root)
    {
        var legs = new List<AnimDefinition>();
        foreach (var def in world.Session.Program.Subset(root).Defs)
        {
            if (def.PrereqNodes.Any(p => p.Required))
            {
                legs.Add(def);
            }
        }

        return legs;
    }

    // One call site playing three times and three call sites playing once are different faults,
    // and only a count separates them, so the episode's every start is counted by name.
    private static void CheckTheHookPlays(
        TestContext ctx, TestWorld world, LandingApproach dock,
        IReadOnlyDictionary<string, int> plays, StringBuilder report)
    {
        var closure = ClosureOf(world, dock.Anim);
        var repeats = plays.Where(p => p.Value > 1)
            .OrderByDescending(p => p.Value).Select(p => $"{p.Key} x{p.Value}").ToList();
        var closureRepeats = plays.Where(p => p.Value > 1 && closure.Contains(p.Key))
            .OrderByDescending(p => p.Value).Select(p => $"{p.Key} x{p.Value}").ToList();
        report.AppendLine($"plays: {plays.Count} definition(s) started, " +
            $"{closure.Count} in '{dock.Anim}''s closure, repeats [{string.Join(", ", repeats)}]");
        ctx.Check(plays.Count > 0, $"the episode starts definitions at all, so the count means something");
        ctx.Same(0, closureRepeats.Count,
            $"and no definition '{dock.Anim}' reaches plays twice in one episode, the hook legs included ({string.Join(", ", closureRepeats)})");
    }

    // The docking's own fork: the hookup calls its drop and pickup legs unconditionally and only
    // each leg's REQUIRED node state keeps the wrong one off. A path resolving to no node passes
    // the gate vacuously, so the resolution is asserted beside the outcome.
    private static void CheckTheDockingPrereqs(
        TestContext ctx, TestWorld world, LandingApproach dock,
        IReadOnlyList<AnimDefinition> forks, IReadOnlyDictionary<string, bool> atDispatch,
        IReadOnlyDictionary<string, int> plays, StringBuilder report)
    {
        int gated = 0;
        int unresolved = 0;
        int wrongWay = 0;
        foreach (var leg in forks)
        {
            string name = leg.AnimName ?? "-";
            int played = plays.TryGetValue(name, out int n) ? n : 0;
            foreach (var prereq in leg.PrereqNodes.Where(p => p.Required))
            {
                string key = PathKey(prereq.Path);
                bool read = atDispatch.TryGetValue(key, out bool live);
                bool held = read && live == prereq.Active;
                bool built = PrereqNodeOf(world, prereq.Path) != null;
                report.AppendLine($"fork: '{name}' requires '{key}' " +
                    $"{(prereq.Active ? "ACTIVE" : "INACTIVE")}, built={built}, at dispatch " +
                    $"{(read ? (live ? "ACTIVE" : "INACTIVE") : "unread")}, played {played}x");
                gated++;
                if (!built)
                {
                    unresolved++;
                }

                if (held != (played > 0))
                {
                    wrongWay++;
                }
            }
        }

        ctx.Check(gated > 0,
            $"'{dock.Anim}''s call closure carries the node-state prerequisites the data forks its legs on ({gated} read)");
        ctx.Same(0, unresolved,
            $"and every one of those paths resolves to a node this world built, so none of them passes the gate vacuously");
        ctx.Same(0, wrongWay,
            $"and a leg runs exactly when its own required state holds at the dispatch, so the docking's wrong leg stays off");
    }

    private static IReadOnlyList<string> ClosureOf(TestWorld world, string root)
    {
        var names = new List<string>();
        foreach (var def in world.Session.Program.Subset(root).Defs)
        {
            if (def.AnimName is { } name && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    // The three rows are symmetric and each belongs to a plane the mission's roster spawns. Read
    // BEFORE the roster is spawned, which is where the reach used to end: the approach nodes hang
    // under a gamez library root the world build never places.
    private static void CheckCarriedRows(
        TestContext ctx, TestWorld world, IReadOnlyList<LandingApproach> carried,
        IReadOnlyList<string> owners, StringBuilder report)
    {
        report.AppendLine($"{carried.Count} row(s) carried by a roster vehicle: " +
            $"{string.Join(", ", owners)}");
        ctx.Same(3, carried.Count,
            $"CM02 authors one approach row per Balmoral, each under that plane's own gamez node");
        bool symmetric = true;
        int built = 0;
        foreach (var row in carried)
        {
            symmetric &= Mathf.IsEqualApprox(row.AngleRad, carried[0].AngleRad)
                && Mathf.IsEqualApprox(row.MinSpeedMps, carried[0].MinSpeedMps)
                && Mathf.IsEqualApprox(row.MaxSpeedMps, carried[0].MaxSpeedMps)
                && row.Shape == carried[0].Shape;
            built += world.Runtime.FindNodes(row.Node).Count;
        }

        ctx.Check(symmetric,
            $"and the three differ only by index, so no gate of theirs can admit one and not another");
        ctx.Same(0, built,
            $"the world build alone reaches none of their approach nodes, a vehicle's gamez node being a library root it never places");
    }

    // What the graft put in the world: one approach node per carried row, each a descendant of the
    // rig its own block spawned, so the volume moves with the aircraft rather than standing where
    // the plane happened to start.
    private static void CheckGrafted(
        TestContext ctx, TestWorld world, IReadOnlyList<LandingApproach> carried,
        StringBuilder report)
    {
        int built = 0;
        int onRig = 0;
        foreach (var row in carried)
        {
            var found = world.Runtime.FindNodes(row.Node);
            built += found.Count;
            var owner = found.Count > 0 ? RigAbove(found[0]) : null;
            onRig += owner != null ? 1 : 0;
            report.AppendLine($"  '{row.Node}' built={found.Count} " +
                $"on rig '{owner?.Name.ToString() ?? "-"}' at " +
                $"{(found.Count > 0 ? found[0].GlobalPosition : Vector3.Zero)}");
        }

        ctx.Same(carried.Count, built,
            $"the roster spawn carries each Balmoral's own approach node into the world");
        ctx.Same(carried.Count, onRig,
            $"and each one hangs under the rig its block spawned, so it follows the aircraft under SET_AI_NET rather than drifting away from it");
    }

    // The mission's own gate pair, found from the compiled definitions rather than named here: the
    // two definitions that write every carried row's land_on node, and what they can reach today.
    private static List<string> CheckGateReach(
        TestContext ctx, TestWorld world, ObjectiveScript script,
        IReadOnlyList<LandingApproach> rows, IReadOnlyList<LandingApproach> carried,
        StringBuilder report)
    {
        var arms = new List<int>();
        foreach (var row in carried)
        {
            int index = ArmIndexOf(world.Gamez, row.Node);
            arms.Add(index);
            report.AppendLine($"  '{row.Node}' land_on is gamez node {index}, " +
                $"built={(index >= 0 ? BuiltCount(world, index) : 0)}");
        }

        var gate = GateDefs(world, arms);
        report.AppendLine($"gate definitions writing all {arms.Count} land_on node(s): " +
            $"{string.Join(", ", gate)}");
        ctx.Same(2, gate.Count,
            $"the mission carries one pair of definitions that switch all three approaches together");
        int called = 0;
        foreach (string name in gate)
        {
            called += CallerOf(script, name) != null ? 1 : 0;
        }

        ctx.Same(gate.Count, called,
            $"and an objective's WAKE_ANIM calls each of them, so the gate is the mission's own");
        ctx.Check(rows.Count > carried.Count,
            $"the chapter carries static approach rows beside these, so the two kinds of owner are told apart in one run");
        int reached = 0;
        foreach (int index in arms)
        {
            reached += index >= 0 && BuiltCount(world, index) == 1 ? 1 : 0;
        }

        ctx.Same(arms.Count, reached,
            $"and every land_on the pair writes is a built node carrying that gamez index, which is what makes an index-addressed write land");
        return gate;
    }

    // The gate driven on the nodes it now reaches: the pair's own two definitions, told apart by
    // what they do rather than by name, and the capture flown at an armed Balmoral.
    private static void RunTheCapture(
        TestContext ctx, TestWorld world, LandingApproachRuntime trigger, CutsceneController cutscene,
        FlightController rig, IReadOnlyList<LandingApproach> carried, IReadOnlyList<string> gate,
        StringBuilder report)
    {
        ctx.Same(0, ArmedCount(world, carried),
            $"the capture starts un-armed: every land_on ships inactive, so it is not offered while more than one Balmoral flies");
        ctx.Check(!Fly(ctx, world, trigger, cutscene, null, rig, carried[0], report),
            $"and flying '{carried[0].Node}' before the gate opens starts nothing");

        var arming = GateRun(world, gate, carried, wantArmed: carried.Count, report);
        if (arming == null)
        {
            ctx.Check(false, $"one of the pair arms all {carried.Count} rows");
            return;
        }

        ctx.Check(Fly(ctx, world, trigger, cutscene, null, rig, carried[0], report),
            $"flying '{carried[0].Node}' once the gate has armed it starts '{trigger.LastStarted}'");
        report.AppendLine($"armed by '{arming}', started '{trigger.LastStarted}'");
        var clearing = GateRun(world, gate, carried, wantArmed: 0, report, skip: arming);
        ctx.Check(clearing != null,
            $"and the pair's other definition takes the same three rows back off, so the capture can go away again");
    }

    // Plays each gate definition in turn until the carried rows' arm bits reach `wantArmed`,
    // returning the one that did it. The pair is not named here: which of the two arms is read off
    // what the run does to the nodes.
    private static string? GateRun(
        TestWorld world, IReadOnlyList<string> gate, IReadOnlyList<LandingApproach> carried,
        int wantArmed, StringBuilder report, string? skip = null)
    {
        foreach (string name in gate)
        {
            if (string.Equals(name, skip, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            world.Runtime.Play(name);
            for (float t = 0f; t < ArmBudgetS && ArmedCount(world, carried) != wantArmed; t += StepDt)
            {
                world.Runtime.Advance(StepDt);
            }

            int armed = ArmedCount(world, carried);
            report.AppendLine($"'{name}' run: {armed} of {carried.Count} land_on armed");
            if (armed == wantArmed)
            {
                return name;
            }
        }

        return null;
    }

    private static int ArmedCount(TestWorld world, IReadOnlyList<LandingApproach> carried)
    {
        int armed = 0;
        foreach (var row in carried)
        {
            var found = world.Runtime.FindNodes(row.Node);
            if (found.Count == 0)
            {
                continue;
            }

            var arm = world.Runtime.FindNodes(LandingApproaches.ArmNode, found[0]);
            armed += arm.Count > 0 && arm[0].Visible ? 1 : 0;
        }

        return armed;
    }

    // The aircraft a grafted node hangs under, or null when it stands in the world on its own.
    private static FlightController? RigAbove(Node3D node)
    {
        for (Node? at = node; at != null; at = at.GetParent())
        {
            if (at is FlightController rig)
            {
                return rig;
            }
        }

        return null;
    }

    // The mission's roster, spawned through the session's own spawner and the director's own
    // roster phase, with the marker graft wired exactly as GameSession wires it.
    private static void SpawnRoster(TestContext ctx, TestWorld world, CampaignDirector director,
        string missionZrdr, ProjectilePool pool, StringBuilder report)
    {
        var planesGamez = GameZ.Load(ctx.PlanesGamezPath);
        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
        try
        {
            var spawner = CampaignRosterSuites.Spawner(ctx, planesGamez, textures, pool);
            int grafted = 0;
            string what = director.BuildRoster(new CampaignDirector.RosterInputs
            {
                ChapterZrdrPath = SessionPaths.ChapterZrdr(ctx.DataRoot, world.Chapter),
                MissionZrdrPath = missionZrdr,
                ZrdrPath = ctx.ZrdrPath,
                FindNodes = name => world.Runtime.FindNodes(name),
                Spawn = (plan, pos, look, pilot) =>
                    spawner.SpawnAi(CampaignRosterPlan.SpawnFor(plan, pos, look, pilot)),
                AttachMarkers = (block, node) => grafted += RosterMarkers.Attach(
                    world.Gamez, world.Session.Builder.Scene, world.Runtime, block, node),
                Rng = new Random(1),
            });
            report.AppendLine($"roster: {director.Roster.Count} rig(s), {grafted} marker graft(s){what}");
            ctx.Check(grafted > 0,
                $"the mission's roster spawn grafts the scaffolding its blocks author: {grafted} subtree(s)");
        }
        finally
        {
            textures.Dispose();
        }
    }

    // What the arming objective waits on: the DEDG the objective ahead of it authors, over the
    // group the three planes' own roster blocks are in.
    private static void CheckGateCondition(
        TestContext ctx, ObjectiveScript script,
        IReadOnlyList<(string Name, List<object?> Fields)> blocks, IReadOnlyList<string> owners,
        StringBuilder report)
    {
        int group = -1;
        bool oneGroup = owners.Count > 0;
        foreach (string owner in owners)
        {
            int mine = BlockOf(blocks, owner) is { } fields ? AiSkills.RosterGroup(fields) : -1;
            oneGroup &= group < 0 || mine == group;
            group = mine;
        }

        report.AppendLine($"the carried rows' planes are aiv group {group}");
        ctx.Check(oneGroup && group > 0,
            $"the three Balmorals share one aiv group, which is what a DEDG condition counts");
        var gate = DedgAhead(script, group);
        report.AppendLine(gate is { } found
            ? $"OBJECTIVE{found.Number} waits on DEDG [{found.Dedg!.Value.Group}, " +
              $"{found.Dedg.Value.Max}] and wakes the objective that calls the arming animation"
            : "no DEDG objective wakes an objective that calls a gate animation");
        ctx.Check(gate?.Dedg is { Max: 1 },
            $"and the capture waits on that group being down to one, so it is not offered while more than one Balmoral flies");
    }

    // The shipped volumes, read back off the resolved rows: the six drop cones are cones with a
    // real opening angle, and the auto row is the ball the original offers its auto-land inside.
    private static void CheckShapes(
        TestContext ctx, IReadOnlyList<LandingApproach> armed, StringBuilder report)
    {
        LandingApproach? cone = null;
        LandingApproach? auto = null;
        foreach (var approach in armed)
        {
            cone ??= approach.Shape == ApproachShape.Cone ? approach : null;
            auto ??= approach.Auto ? approach : null;
        }

        ctx.Check(cone != null, $"the mission's drop-off rows carry a cone condition volume");
        if (cone is { } shape)
        {
            float half = Mathf.RadToDeg(
                Mathf.Atan2(shape.Radius, shape.Apex.DistanceTo(shape.BaseCentre)));
            report.AppendLine($"'{shape.Node}' cone: half-angle {half:0.0} deg, " +
                $"reach {shape.Apex.DistanceTo(shape.BaseCentre):0.0} m");
            ctx.Check(half is > 1f and < 89f,
                $"whose authored triangle opens a usable cone ({half:0.0} deg half-angle)");
            ctx.Check(shape.Contains(Lerp(shape, AxisFraction)),
                $"a point on that cone's own axis is inside it");
            ctx.Check(!shape.Contains(shape.Apex - ((shape.BaseCentre - shape.Apex) * 0.5f)),
                $"and a point behind its apex is not, so the volume has a front");
        }

        ctx.Check(auto is { Shape: ApproachShape.Sphere },
            $"and the auto-land row the chapter authors is a sphere, tested with no attitude cone");
    }

    // The mission's own route to the drop: the objective that names a landings animation starts
    // dormant with its approach disarmed, the objective chain arms it, and flying the cone then
    // starts the cutscene and completes that objective.
    private static void RunTheDrop(
        TestContext ctx, TestWorld world, ObjectiveGraph graph, ObjectiveScript script,
        IReadOnlyList<LandingApproach> armed, LandingApproachRuntime trigger,
        CutsceneController cutscene, FlightController rig, StringBuilder report)
    {
        var drop = ConeFor(armed);
        if (drop is not { } cone)
        {
            ctx.Check(false, $"the mission carries a cone approach to fly");
            return;
        }

        var closure = ClosureOf(world, new[] { cone });
        if (GatedObjective(script, closure) is not { } gated)
        {
            ctx.Check(false, $"the mission gates an objective on what that approach plays");
            return;
        }

        report.AppendLine($"OBJECTIVE{gated.Number} waits on ANIM_STATE " +
            $"'{gated.AnimStates[0].Name}' = {gated.AnimStates[0].State}");
        foreach (var row in armed)
        {
            var found = world.Runtime.FindNodes(LandingApproaches.ArmNode,
                world.Runtime.FindNodes(row.Node)[0]);
            report.AppendLine($"  '{row.Node}' land_on armed=" +
                $"{(found.Count > 0 ? found[0].Visible.ToString() : "absent")}");
        }

        ctx.Same(0, world.Runtime.AnimStateOf(gated.AnimStates[0].Name),
            $"the animation OBJECTIVE{gated.Number} waits on has not run at mission start");
        // The graph is deliberately NOT stepped for this one: the drop cones sit on the site the
        // first primary approaches, so a flight that also ran the mission would arm them itself.
        ctx.Check(!Fly(ctx, world, trigger, cutscene, null, rig, cone, report),
            $"and flying its approach before the mission arms it starts nothing");

        Arm(ctx, world, graph, script, rig, cone, report);
        ctx.Check(Fly(ctx, world, trigger, cutscene, graph, rig, cone, report),
            $"flying '{cone.Node}' once the mission has armed it starts '{trigger.LastStarted}'");
        report.AppendLine($"started '{trigger.LastStarted}', codes [{string.Join(", ", cutscene.Codes)}]");
        // ⚠ BL-470: an instantly-completing definition satisfies both the EXECUTED and the
        // objective check below, so those two are blind to a cutscene that is over the frame it
        // starts. Fly has already ticked the host once, so still Playing here means it outlived it.
        ctx.Check(cutscene.Playing,
            $"'{trigger.LastStarted}' still owns the session a frame on, rather than completing instantly");
        foreach (string name in closure)
        {
            report.AppendLine($"  reached '{name}' state {world.Runtime.AnimStateOf(name)}");
        }

        // 11 takes the player out of flight, 2 turns the presentation on
        // (docs/formats/anim-definitions/cutscenes.md).
        ctx.Check(Raised(cutscene.Codes, 11) && Raised(cutscene.Codes, 2),
            $"the cutscene host took the drop's own callbacks, out-of-flight and presentation");

        float played = 0f;
        for (float t = 0f; t < PlayBudgetS && !graph.CompletedOf(gated.Number); t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            cutscene.Tick();
            graph.Step(StepDt);
            if (cutscene.Playing)
            {
                played += StepDt;
            }
        }

        report.AppendLine($"episode ran {played:0.##} s past the frame it started on");
        report.AppendLine($"'{gated.AnimStates[0].Name}' state " +
            $"{world.Runtime.AnimStateOf(gated.AnimStates[0].Name)}, " +
            $"OBJECTIVE{gated.Number} completed={graph.CompletedOf(gated.Number)}");
        ctx.Same(3, world.Runtime.AnimStateOf(gated.AnimStates[0].Name),
            $"the definition it waits on runs to EXECUTED rather than being declared run");
        ctx.Check(graph.CompletedOf(gated.Number),
            $"which completes OBJECTIVE{gated.Number}, the primary the drop gates");
        CheckNextPrimary(ctx, world, trigger, cutscene, graph, script, armed,
            ClosureOf(world, armed), rig, report);
    }

    // ⚠ The handoff hands control back with the aircraft exactly where the cutscene left it, which
    // is inside the volume that started it, on a row the mission has armed. Without a latch the
    // trigger re-fires the same row on the next frame, over and over.
    private static void CheckNoRestart(
        TestContext ctx, TestWorld world, LandingApproachRuntime trigger,
        CutsceneController cutscene, ObjectiveGraph graph, FlightController rig,
        LandingApproach approach, StringBuilder report)
    {
        for (int i = 0; i < RestartFrames; i++)
        {
            world.Runtime.Advance(StepDt);
            trigger.Tick();
            cutscene.Tick();
            graph.Step(StepDt);
        }

        var node = world.Runtime.FindNodes(approach.Node)[0];
        var frame = node.GlobalTransform;
        var arm = world.Runtime.FindNodes(LandingApproaches.ArmNode, node);
        report.AppendLine($"{RestartFrames} frames after the handoff: playing={cutscene.Playing} " +
            $"armed={(arm.Count > 0 ? arm[0].Visible.ToString() : "no land_on")} " +
            $"band={approach.SpeedInBand(rig.WorldVelocity.Length())} " +
            $"angle={Mathf.RadToDeg(LandingApproaches.AngleBetween(frame.Basis, rig.Attitude)):0.#} " +
            $"inside={approach.Contains(frame.AffineInverse() * rig.WorldPosition)}");
        ctx.Check(!cutscene.Playing,
            $"and the handoff does not re-fire the row the aircraft is still parked inside");
    }

    // The primary after the drop: another objective gated on a landings animation, whose own
    // approach the same trigger flies. Its wake stands in for the combat leg between them; what is
    // under test is that the condition can be met at all.
    private static void CheckNextPrimary(
        TestContext ctx, TestWorld world, LandingApproachRuntime trigger, CutsceneController cutscene,
        ObjectiveGraph graph, ObjectiveScript script, IReadOnlyList<LandingApproach> armed,
        IReadOnlyList<string> closure, FlightController rig, StringBuilder report)
    {
        if (LaterGated(script, closure, graph) is not { } later
            || RowFor(armed, later.AnimStates[0].Name) is not { } row)
        {
            report.AppendLine("no second objective is gated on a landings animation");
            return;
        }

        // Its wake stands in for the combat leg between the two primaries; the wake is also what
        // runs the objective's own WAKE_ANIM, which is what arms this approach.
        graph.Wake(later.Number);
        for (float t = 0f; t < ArmBudgetS; t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            graph.Step(StepDt);
        }

        report.AppendLine($"OBJECTIVE{later.Number} woken, waits on '{later.AnimStates[0].Name}'");
        ctx.Check(Fly(ctx, world, trigger, cutscene, graph, rig, row, report),
            $"flying '{row.Node}' starts '{later.AnimStates[0].Name}', what OBJECTIVE{later.Number} waits on");
        for (float t = 0f; t < PlayBudgetS && !graph.CompletedOf(later.Number); t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            cutscene.Tick();
            graph.Step(StepDt);
        }

        CheckNoRestart(ctx, world, trigger, cutscene, graph, rig, row, report);
        report.AppendLine($"'{later.AnimStates[0].Name}' state " +
            $"{world.Runtime.AnimStateOf(later.AnimStates[0].Name)}, " +
            $"OBJECTIVE{later.Number} completed={graph.CompletedOf(later.Number)}");
        ctx.Check(graph.CompletedOf(later.Number),
            $"and running it completes OBJECTIVE{later.Number}, the mission's route to its own end");
    }

    // Flies the approach: the aircraft starts on the volume's own axis, aimed at its apex, at a
    // speed inside the authored band. Returns whether the trigger fired inside the budget.
    private static bool Fly(
        TestContext ctx, TestWorld world, LandingApproachRuntime trigger, CutsceneController cutscene,
        ObjectiveGraph? graph, FlightController rig, LandingApproach approach, StringBuilder report)
    {
        var nodes = world.Runtime.FindNodes(approach.Node);
        if (nodes.Count == 0)
        {
            return false;
        }

        var frame = nodes[0].GlobalTransform;
        string? before = trigger.LastStarted;
        rig.Setup(new FlightModel(PlaneStats.Load(ctx.ZrdrPath, PlaneNode)), null, new CamParams(),
            frame * Lerp(approach, AxisFraction), frame * approach.Apex,
            ApproachThrottle, ApproachSpeedMps);
        for (float t = 0f; t < ApproachBudgetS; t += StepDt)
        {
            rig.SimStep(StepDt);
            world.Runtime.Advance(StepDt);
            trigger.Tick();
            cutscene.Tick();
            graph?.Step(StepDt);
            if (trigger.LastStarted != before)
            {
                return true;
            }
        }

        var arm = world.Runtime.FindNodes(LandingApproaches.ArmNode, nodes[0]);
        report.AppendLine($"  no fire on '{approach.Node}': armed=" +
            $"{(arm.Count > 0 ? arm[0].Visible.ToString() : "no land_on")} " +
            $"speed={rig.WorldVelocity.Length():0.#} band={approach.SpeedInBand(rig.WorldVelocity.Length())} " +
            $"angle={Mathf.RadToDeg(LandingApproaches.AngleBetween(frame.Basis, rig.Attitude)):0.#} " +
            $"inside={approach.Contains(frame.AffineInverse() * rig.WorldPosition)} " +
            $"playing={cutscene.Playing}");
        return false;
    }

    // Arms the drop the way the mission does: the aircraft is parked over the drop site, which is
    // what the first primary's TRAVELERS condition approaches, and the graph's own nap chain runs
    // until the objective that arms it has fired its WAKE_ANIM.
    private static void Arm(TestContext ctx, TestWorld world, ObjectiveGraph graph,
        ObjectiveScript script, FlightController rig, LandingApproach cone, StringBuilder report)
    {
        var node = world.Runtime.FindNodes(cone.Node)[0];
        var site = node.GlobalTransform * cone.Apex;
        rig.Setup(new FlightModel(PlaneStats.Load(ctx.ZrdrPath, PlaneNode)), null,
            new CamParams(), site, site + Vector3.Forward, 0f, 0f);
        for (float t = 0f; t < ArmBudgetS; t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            graph.Step(StepDt);
        }

        var arm = world.Runtime.FindNodes(LandingApproaches.ArmNode, node);
        report.AppendLine($"armed through the mission's own chain: " +
            $"{CompletedCount(graph, script)} objective(s) complete, '{cone.Node}' land_on " +
            $"{(arm.Count > 0 ? arm[0].Visible.ToString() : "absent")}");
        ctx.Check(arm.Count > 0 && arm[0].Visible,
            $"the mission's own objective chain arms '{cone.Node}' by flying its site");
    }

    // Arms a row generically: whichever objective's WAKE_ANIM writes its land_on gamez node,
    // found from the compiled definitions rather than named here, woken directly rather than
    // waited on, since the row's own site is not necessarily on the mission's main path.
    private static void ArmRow(TestContext ctx, TestWorld world, ObjectiveGraph graph,
        ObjectiveScript script, LandingApproach approach, StringBuilder report)
    {
        var node = world.Runtime.FindNodes(approach.Node)[0];
        int armIndex = ArmIndexOf(world.Gamez, approach.Node);
        var gate = armIndex >= 0 ? GateDefs(world, new[] { armIndex }) : new List<string>();
        var caller = gate.Count > 0 ? CallerOf(script, gate[0]) : null;
        report.AppendLine($"'{approach.Node}' land_on gamez node {armIndex}, gate def(s) " +
            $"[{string.Join(", ", gate)}], caller OBJECTIVE{caller?.Number.ToString() ?? "?"}");
        ctx.Check(caller != null, $"an objective's WAKE_ANIM arms '{approach.Node}'");
        if (caller is { } found)
        {
            graph.Wake(found.Number);
        }

        // Polled rather than a fixed budget: WAKE_ANIM's own animation runs its authored timeline
        // before it touches the node state, and that duration is not this suite's to guess at.
        var arm = world.Runtime.FindNodes(LandingApproaches.ArmNode, node);
        float armedAt = -1f;
        for (float t = 0f; t < AutoArmBudgetS; t += StepDt)
        {
            world.Runtime.Advance(StepDt);
            graph.Step(StepDt);
            if (arm.Count > 0 && arm[0].Visible)
            {
                armedAt = t;
                break;
            }
        }

        report.AppendLine($"armed: '{approach.Node}' land_on " +
            $"{(arm.Count > 0 ? arm[0].Visible.ToString() : "absent")} at t={armedAt:0.#}s");
        ctx.Check(arm.Count > 0 && arm[0].Visible,
            $"the mission's own objective chain arms '{approach.Node}'");
    }

    private static FlightController BuildRig(TestContext ctx, TestWorld world, ProjectilePool pool,
        string planeNode = PlaneNode)
    {
        var textures = new TextureArchive(SessionPaths.ChapterTextures(ctx.DataRoot, world.Chapter));
        try
        {
            var stats = PlaneStats.Load(ctx.ZrdrPath, planeNode);
            var model = new PlaneBuilder(GameZ.Load(ctx.PlanesGamezPath), textures,
                dockingHook: true).Build(planeNode);
            var rig = new FlightController
            {
                PlaneModel = model,
                Collider = PlaneCollider.Build(model),
                PlayerIndex = FlightRoster.ShooterIdBase,
                IsHumanPiloted = true,
                Projectiles = pool,
                UseKeyboard = false,
                PadDevices = Array.Empty<int>(),
                AllowPause = false,
                Team = AimAssist.PlayerTeam,
                Name = "LandingApproachPlayer",
            };
            rig.AddChild(model);
            ctx.Host.AddChild(rig);
            rig.Setup(new FlightModel(stats), null, new CamParams(), Vector3.Zero,
                Vector3.Forward, ApproachThrottle, ApproachSpeedMps);
            // What FlightControllerBuild.Bind does for a session rig; this suite builds the
            // controller by hand, so the prompt would otherwise stay at its data-less stand-in.
            rig.UseMessages(Messages.Load(ctx.MessagesPath));
            return rig;
        }
        finally
        {
            textures.Dispose();
        }
    }

    // The topmost gamez ancestor of a named node: the library root a vehicle instantiates when the
    // node belongs to one, and the world root's own placed content otherwise.
    private static string TopAncestorOf(GameZ gamez, string nodeName)
    {
        var parents = new int[gamez.Nodes.Count];
        for (int i = 0; i < parents.Length; i++)
        {
            parents[i] = -1;
        }

        foreach (var node in gamez.Nodes)
        {
            foreach (int child in node.Children)
            {
                if (child >= 0 && child < parents.Length)
                {
                    parents[child] = node.Index;
                }
            }
        }

        if (gamez.FindByName(nodeName) is not { } start)
        {
            return string.Empty;
        }

        var at = start;
        while (parents[at.Index] >= 0)
        {
            at = gamez.Nodes[parents[at.Index]];
        }

        return at.Name;
    }

    private static List<object?>? BlockOf(
        IReadOnlyList<(string Name, List<object?> Fields)> blocks, string name)
    {
        foreach (var (block, fields) in blocks)
        {
            if (string.Equals(block, name, StringComparison.OrdinalIgnoreCase))
            {
                return fields;
            }
        }

        return null;
    }

    // The gamez index of the land_on node under an approach node, or -1 when it carries none.
    private static int ArmIndexOf(GameZ gamez, string approachNode)
    {
        if (gamez.FindByName(approachNode) is not { } approach)
        {
            return -1;
        }

        var stack = new Stack<int>(approach.Children);
        while (stack.Count > 0)
        {
            int index = stack.Pop();
            if (index < 0 || index >= gamez.Nodes.Count)
            {
                continue;
            }

            var node = gamez.Nodes[index];
            if (string.Equals(node.Name, LandingApproaches.ArmNode, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }

            foreach (int child in node.Children)
            {
                stack.Push(child);
            }
        }

        return -1;
    }

    private static int BuiltCount(TestWorld world, int gamezIndex)
    {
        int built = 0;
        foreach (var node in world.Runtime.FindNodes(LandingApproaches.ArmNode))
        {
            built += node.HasMeta(AnimRuntime.IndexMeta)
                && (int)node.GetMeta(AnimRuntime.IndexMeta) == gamezIndex ? 1 : 0;
        }

        return built;
    }

    // The mission definitions whose symbol table binds every one of these gamez node indices: the
    // pair that switches a whole set of approach rows in one sequence.
    private static List<string> GateDefs(TestWorld world, IReadOnlyList<int> indices)
    {
        var names = new List<string>();
        foreach (var def in world.Session.Program.Defs)
        {
            if (def.AnimName is not { } name || indices.Count == 0)
            {
                continue;
            }

            bool all = true;
            foreach (int index in indices)
            {
                bool found = false;
                foreach (int bound in def.NodeRefs.Values)
                {
                    found |= bound == index;
                }

                all &= found;
            }

            if (all && !Names(names, name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    // The DEDG objective over this group that wakes an objective calling an animation: the gate the
    // capture waits behind.
    private static ObjectiveDef? DedgAhead(ObjectiveScript script, int group)
    {
        foreach (var def in script.Objectives)
        {
            if (def.Dedg is not { } dedg || dedg.Group != group)
            {
                continue;
            }

            var woken = new List<int>(def.WakeWhenComplete);
            if (def.NapWhenComplete is { } nap)
            {
                woken.Add(nap.Target);
            }

            foreach (int number in woken)
            {
                foreach (var other in script.Objectives)
                {
                    if (other.Number == number && other.WakeAnim != null)
                    {
                        return def;
                    }
                }
            }
        }

        return null;
    }

    private static ObjectiveDef? CallerOf(ObjectiveScript script, string anim)
    {
        foreach (var def in script.Objectives)
        {
            if (def.WakeAnim is { } wake
                && string.Equals(wake.Anim, anim, StringComparison.OrdinalIgnoreCase))
            {
                return def;
            }
        }

        return null;
    }

    private static Vector3 Lerp(LandingApproach approach, float fraction) =>
        approach.Apex + ((approach.BaseCentre - approach.Apex) * fraction);

    // The REQUIRED node-state prerequisites of every definition under an animation name, each with
    // whether the built world reads it met now. The node is the path's leaf, resolved globally.
    private static List<(string Node, bool Active, bool Met)> PrerequisitesOf(TestWorld world, string anim)
    {
        var found = new List<(string, bool, bool)>();
        foreach (var def in world.Session.Program.ByAnimName(anim))
        {
            foreach (var prereq in def.PrereqNodes)
            {
                if (!prereq.Required)
                {
                    continue;
                }

                string leaf = prereq.Path[prereq.Path.Count - 1];
                var nodes = world.Runtime.FindNodes(leaf);
                bool met = nodes.Count > 0 && nodes.All(n => n.Visible == prereq.Active);
                found.Add((leaf, prereq.Active, met));
            }
        }

        return found;
    }

    private static IReadOnlyList<string> ClosureOf(
        TestWorld world, IReadOnlyList<LandingApproach> armed)
    {
        var roots = new List<string>(armed.Count);
        foreach (var approach in armed)
        {
            roots.Add(approach.Anim);
        }

        var names = new List<string>();
        foreach (var def in world.Session.Program.Subset(roots).Defs)
        {
            if (def.AnimName is { } name && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    // The first objective whose ANIM_STATE condition names an animation only the approach trigger
    // can play, which is exactly the objective BL-467 found unsatisfiable.
    private static ObjectiveDef? GatedObjective(
        ObjectiveScript script, IReadOnlyList<string> closure)
    {
        foreach (var def in script.Objectives)
        {
            if (def.AnimStates.Count > 0 && Names(closure, def.AnimStates[0].Name))
            {
                return def;
            }
        }

        return null;
    }

    private static ObjectiveDef? ObjectiveForInactive(ObjectiveScript script, string node)
    {
        foreach (var def in script.Objectives)
        {
            foreach (var path in def.Inactive)
            {
                if (path.Count > 0 && string.Equals(path[0], node,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return def;
                }
            }
        }

        return null;
    }

    private static ObjectiveDef? ObjectiveForAnimState(ObjectiveScript script, string anim)
    {
        foreach (var def in script.Objectives)
        {
            foreach (var entry in def.AnimStates)
            {
                if (string.Equals(entry.Name, anim, StringComparison.OrdinalIgnoreCase))
                {
                    return def;
                }
            }
        }

        return null;
    }

    private static ObjectiveDef? LaterGated(
        ObjectiveScript script, IReadOnlyList<string> closure, ObjectiveGraph graph)
    {
        foreach (var def in script.Objectives)
        {
            if (def.AnimStates.Count > 0 && Names(closure, def.AnimStates[0].Name)
                && !graph.CompletedOf(def.Number))
            {
                return def;
            }
        }

        return null;
    }

    private static LandingApproach? RowFor(IReadOnlyList<LandingApproach> armed, string anim)
    {
        foreach (var approach in armed)
        {
            if (string.Equals(approach.Anim, anim, StringComparison.OrdinalIgnoreCase))
            {
                return approach;
            }
        }

        return null;
    }

    private static LandingApproach? ConeFor(IReadOnlyList<LandingApproach> armed)
    {
        foreach (var approach in armed)
        {
            if (approach.Shape == ApproachShape.Cone && !approach.Auto)
            {
                return approach;
            }
        }

        return null;
    }

    private static LandingApproach? AutoFor(IReadOnlyList<LandingApproach> armed)
    {
        foreach (var approach in armed)
        {
            if (approach.Auto)
            {
                return approach;
            }
        }

        return null;
    }

    // Where the mission's first awake TRAVELERS condition points, so the graph's own chain can be
    // started without naming a world node here.
    private static Vector3? ApproachSite(ObjectiveScript script, TestWorld world)
    {
        foreach (var def in script.Objectives)
        {
            if (def.BeginDormant || def.Travelers is not { } spec)
            {
                continue;
            }

            if (spec.WherePoint is { Length: 3 } point)
            {
                return new Vector3(point[0], point[1], point[2]);
            }

            var found = world.Runtime.FindNodes(spec.WhereNode ?? string.Empty);
            if (found.Count > 0)
            {
                return found[0].GlobalPosition;
            }
        }

        return null;
    }

    private static int CompletedCount(ObjectiveGraph graph, ObjectiveScript script)
    {
        int done = 0;
        foreach (var def in script.Objectives)
        {
            if (graph.CompletedOf(def.Number))
            {
                done++;
            }
        }

        return done;
    }

    private static bool Raised(IReadOnlyList<int> codes, int code)
    {
        foreach (int raised in codes)
        {
            if (raised == code)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Names(IReadOnlyList<string> names, string wanted)
    {
        foreach (string name in names)
        {
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
}
