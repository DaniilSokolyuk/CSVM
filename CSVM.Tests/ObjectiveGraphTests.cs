using System.Collections.Generic;
using System.IO;
using CSVM;
using CSVM.Flight.Weapons;
using CSVM.Session.Campaign;
using CSVM.Session.Objectives;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The objectives runtime's decoded rules, pinned off-engine: the four-state machine, the rotating
/// one-completion-per-tick scan, the chaining executor with its already-awake truncation, the nap
/// that clears a completed flag, the awake-gate of <c>TICK_DEPENDS_ON_OBJ</c>, and the display
/// rows the mask is built from. Every claim is docs/formats/objectives.md's.
/// </summary>
[Trait("Tier", "Quick")]
public class ObjectiveGraphTests
{
    [Fact]
    public void Parsing_stops_at_the_first_missing_objective_number()
    {
        var script = Script("\"OBJECTIVE1\",null,\"OBJECTIVE2\",null,\"OBJECTIVE4\",null");
        Assert.Equal(2, script.Objectives.Count);
    }

    [Fact]
    public void A_null_block_parses_awake_with_no_conditions_and_completes()
    {
        var (graph, _) = Build("\"OBJECTIVE1\",null");
        Assert.Equal(ObjectiveState.Awake, graph.StateOf(1));
        graph.Step(0.1f);
        Assert.True(graph.CompletedOf(1));
        Assert.Equal(ObjectiveState.Retired, graph.StateOf(1));
    }

    [Fact]
    public void At_most_one_objective_completes_per_tick_from_a_rotating_scan()
    {
        var (graph, _) = Build("\"OBJECTIVE1\",null,\"OBJECTIVE2\",null,\"OBJECTIVE3\",null");
        graph.Step(0.1f);
        Assert.True(graph.CompletedOf(1));
        Assert.False(graph.CompletedOf(2));
        graph.Step(0.1f);
        Assert.True(graph.CompletedOf(2));
        Assert.False(graph.CompletedOf(3));
        graph.Step(0.1f);
        Assert.True(graph.CompletedOf(3));
    }

    [Fact]
    public void Begin_dormant_wakes_at_its_mission_time_and_minus_one_never_does()
    {
        var (graph, world) = Build(
            "\"OBJECTIVE1\",[\"BEGIN_DORMANT\",[1.0],\"WAKEUP_SOUND_GROUP\",[\"snd_start\"],"
            + "\"INACTIVE1\",[\"never\"]],"
            + "\"OBJECTIVE2\",[\"BEGIN_DORMANT\",[-1.0],\"INACTIVE1\",[\"never\"]]");
        Run(graph, 0.5f);
        Assert.Equal(ObjectiveState.Dormant, graph.StateOf(1));
        Run(graph, 1.0f);
        Assert.Equal(ObjectiveState.Awake, graph.StateOf(1));
        Assert.Contains("snd_start", world.SoundGroups);
        Run(graph, 30f);
        Assert.Equal(ObjectiveState.Dormant, graph.StateOf(2));
    }

    [Fact]
    public void A_wake_list_truncates_at_an_already_awake_target()
    {
        // OBJ2 is awake on a condition that never reads true, so the executor's early return fires
        // and OBJ3, listed after it, is never reached.
        var (graph, _) = Build(
            "\"OBJECTIVE1\",[\"WAKE_OBJECTIVE_WHEN_I_COMPLETE\",[2,3]],"
            + "\"OBJECTIVE2\",[\"INACTIVE1\",[\"never\"]],"
            + "\"OBJECTIVE3\",[\"BEGIN_DORMANT\",[-1.0],\"INACTIVE1\",[\"never\"]]");
        graph.Step(0.1f);
        Assert.True(graph.CompletedOf(1));
        Assert.Equal(ObjectiveState.Awake, graph.StateOf(2));
        Assert.Equal(ObjectiveState.Dormant, graph.StateOf(3));
    }

    [Fact]
    public void Nap_clears_the_completed_flag_and_is_the_only_re_run_path()
    {
        var (graph, _) = Build(
            "\"OBJECTIVE1\",[\"BEGIN_DORMANT\",[1.0],\"NAP_OBJECTIVE_WHEN_I_COMPLETE\",[2,1.0]],"
            + "\"OBJECTIVE2\",null");
        graph.Step(0.1f);
        Assert.True(graph.CompletedOf(2));
        Run(graph, 1.2f);
        Assert.True(graph.CompletedOf(1));
        Assert.False(graph.CompletedOf(2));
        Assert.Equal(ObjectiveState.Napping, graph.StateOf(2));
        Run(graph, 1.2f);
        Assert.True(graph.CompletedOf(2));
    }

    [Fact]
    public void A_plain_wake_never_re_runs_a_completed_objective()
    {
        var (graph, _) = Build(
            "\"OBJECTIVE1\",[\"BEGIN_DORMANT\",[1.0],\"WAKE_OBJECTIVE_WHEN_I_COMPLETE\",[2]],"
            + "\"OBJECTIVE2\",null");
        Run(graph, 2f);
        Assert.Equal(ObjectiveState.Retired, graph.StateOf(2));
    }

    [Fact]
    public void Tick_depends_on_obj_gates_on_the_dependency_being_awake()
    {
        var (graph, _) = Build(
            "\"OBJECTIVE1\",[\"TICK_DEPENDS_ON_OBJ\",[2]],"
            + "\"OBJECTIVE2\",[\"BEGIN_DORMANT\",[1.0],\"INACTIVE1\",[\"never\"]]");
        Run(graph, 0.5f);
        Assert.False(graph.CompletedOf(1));
        Run(graph, 1f);
        Assert.True(graph.CompletedOf(1));
    }

    [Fact]
    public void Kill_retires_a_target_permanently()
    {
        var (graph, _) = Build(
            "\"OBJECTIVE1\",[\"KILL_OBJECTIVE_WHEN_I_COMPLETE\",[2],"
            + "\"WAKE_OBJECTIVE_WHEN_I_COMPLETE\",[2]],"
            + "\"OBJECTIVE2\",[\"BEGIN_DORMANT\",[-1.0],\"INACTIVE1\",[\"never\"]]");
        Run(graph, 1f);
        Assert.False(graph.AliveOf(2));
        Assert.Equal(ObjectiveState.Retired, graph.StateOf(2));
    }

    [Fact]
    public void Inactive_counts_node_active_bit_clears_against_its_completion_count()
    {
        var (graph, world) = Build(
            "\"OBJECTIVE1\",[\"INACTIVE_COMPLETION_COUNT\",[2],"
            + "\"INACTIVE1\",[\"a\",\"healthy\"],\"INACTIVE2\",[\"b\"],\"INACTIVE3\",[\"c\"]]");
        world.Inactive.Add("healthy");
        Run(graph, 0.5f);
        Assert.False(graph.CompletedOf(1));
        world.Inactive.Add("b");
        Run(graph, 0.5f);
        Assert.True(graph.CompletedOf(1));
    }

    [Fact]
    public void Condition_families_or_together()
    {
        var (graph, world) = Build(
            "\"OBJECTIVE1\",[\"INACTIVE1\",[\"never\"],"
            + "\"ANIM_STATE\",[\"ANIM\",[\"NAME\",[\"fly\"],\"STATE\",[\"RUNNING\"]]]]");
        Run(graph, 0.5f);
        Assert.False(graph.CompletedOf(1));
        world.AnimStates["fly"] = 2;
        Run(graph, 0.5f);
        Assert.True(graph.CompletedOf(1));
    }

    [Fact]
    public void A_dedg_the_world_cannot_answer_reports_false_rather_than_completing()
    {
        var (graph, _) = Build("\"OBJECTIVE1\",[\"DEDG\",[2,0]]");
        Run(graph, 2f);
        Assert.False(graph.CompletedOf(1));
        Assert.True(graph.UnresolvedConditions > 0);
    }

    [Fact]
    public void Danger_zones_only_count_while_the_objective_is_awake()
    {
        var (graph, _) = Build(
            "\"OBJECTIVE1\",[\"BEGIN_DORMANT\",[1.0],\"DANGER_ZONES_COMPLETED\",[\"dz1\"]]");
        graph.NotifyDangerZoneCompleted("dz1");
        Run(graph, 0.5f);
        Assert.False(graph.CompletedOf(1));
        Run(graph, 1f);
        Assert.False(graph.CompletedOf(1));
        graph.NotifyDangerZoneCompleted("dz1");
        Run(graph, 0.2f);
        Assert.True(graph.CompletedOf(1));
    }

    [Fact]
    public void Instantwin_ends_the_mission_after_the_short_wrap_up()
    {
        var (graph, _) = Build("\"OBJECTIVE1\",[\"INSTANTWIN\"]");
        int ended = 0;
        graph.MissionEnded += _ => ended++;
        graph.Step(0.05f);
        Assert.True(graph.Ending);
        Assert.False(graph.Ended);
        graph.Step(0.1f);
        Assert.Equal(MissionOutcome.Won, graph.Outcome);
        Assert.Equal(1, ended);
    }

    [Fact]
    public void The_dockings_completion_code_ends_the_mission_with_no_wrap_up_and_only_once()
    {
        var (graph, _) = Build("\"OBJECTIVE1\",[\"INACTIVE1\",[\"never\"]]");
        int ended = 0;
        graph.MissionEnded += _ => ended++;
        Assert.True(graph.NotifyDockingComplete());
        graph.Step(1f / 60f);
        Assert.Equal(MissionOutcome.Won, graph.Outcome);
        Assert.Equal(1, ended);
        Assert.False(graph.NotifyDockingComplete());
        graph.Step(3f);
        Assert.Equal(MissionOutcome.Won, graph.Outcome);
        Assert.Equal(1, ended);
    }

    [Fact]
    public void Identity_rows_are_one_per_unique_priority_and_a_bit_is_that_priority()
    {
        var (graph, _) = Build(
            "\"OBJECTIVE1\",[\"IDENTITY\",[\"SECONDARY\",11,\"MSG_B\"],\"INACTIVE1\",[\"never\"]],"
            + "\"OBJECTIVE2\",[\"IDENTITY\",[\"PRIMARY\",1,\"MSG_A\"]],"
            + "\"OBJECTIVE3\",[\"IDENTITY\",[\"PRIMARY\",1,\"MSG_A\"],\"INACTIVE1\",[\"never\"]]");
        Assert.Equal(2, graph.Rows.Count);
        Assert.Equal(1, graph.Rows[0].Priority);
        Assert.Equal(ObjectiveClass.Primary, graph.Rows[0].Class);
        Assert.Equal("MSG_A", graph.Rows[0].MessageKey);
        Assert.Equal(11, graph.Rows[1].Priority);
        Run(graph, 0.5f);
        Assert.True(graph.Rows[0].Completed);

        // The completed row is priority 1, so the mask is bit 1, not bit 0 (the win flag, which the
        // graph never sets) and not bit 0 as "row zero".
        Assert.Equal(1 << 1, graph.CompletedMask);
        Assert.Equal(0, graph.CompletedMask & CampaignProgression.PrimaryObjectiveMask);
    }

    /// <summary>A mask bit is the row's authored priority, so a mission whose rows are 1 and 11
    /// records bits 1 and 11 and never bits 0 and 1.</summary>
    [Fact]
    public void A_completed_row_sets_the_bit_its_priority_names_not_its_row_position()
    {
        var (graph, _) = Build(
            "\"OBJECTIVE1\",[\"IDENTITY\",[\"SECONDARY\",11,\"MSG_B\"]],"
            + "\"OBJECTIVE2\",[\"IDENTITY\",[\"PRIMARY\",1,\"MSG_A\"]]");
        Assert.Equal(2, graph.Rows.Count);
        Run(graph, 0.5f);
        Assert.True(graph.Rows[0].Completed);
        Assert.True(graph.Rows[1].Completed);
        Assert.Equal((1 << 1) | (1 << 11), graph.CompletedMask);
    }

    [Fact]
    public void The_shipped_misspellings_stay_dead()
    {
        var (graph, world) = Build(
            "\"OBJECTIVE1\",[\"WAKEUP_OBJECTIVE_WHEN_I_COMPLETE\",[2],"
            + "\"SET_AI_\",[[\"wingman_1\",\"M2Regulars\"]]],"
            + "\"OBJECTIVE2\",[\"BEGIN_DORMANT\",[-1.0],\"INACTIVE1\",[\"never\"]]");
        Run(graph, 1f);
        Assert.Equal(ObjectiveState.Dormant, graph.StateOf(2));
        Assert.Empty(world.AiNets);
    }

    [Fact]
    public void Completion_actions_edit_the_target_lists_and_the_help_labels()
    {
        var (graph, _) = Build(
            "\"OBJECTIVE1\",[\"ADD_OBJECTIVE_TARGET\",[\"sprucegoose\"],"
            + "\"REMOVE_OTHER_TARGET\",[\"propane\"],\"ADD_OTHER_TARGET\",[\"ftank01\"],"
            + "\"SET_HELP_LABEL\",[[\"ftank01\"],\"MSG_OBJ_DESTROY\"]]");
        graph.Step(0.1f);
        Assert.Contains("sprucegoose", graph.ObjectiveTargets);
        Assert.Contains("ftank01", graph.OtherTargets);
        Assert.Equal("MSG_OBJ_DESTROY", graph.HelpLabels["ftank01"]);
    }

    [Fact]
    public void A_nested_target_list_reads_as_one_path_and_a_bare_name_stays_bare()
    {
        var script = Script(
            "\"OBJECTIVE1\",[\"ADD_OBJECTIVE_TARGET\",[[\"a\",\"b\"],\"c\"],"
            + "\"REMOVE_OTHER_TARGET\",[[\"d\",\"e\",\"f\"]],"
            + "\"SET_HELP_LABEL\",[[\"a\",\"b\"],\"MSG_OBJ_DEFEND\"]]");
        var def = script.Objectives[0];
        Assert.Equal(new[] { "a/b", "c" }, def.AddObjectiveTarget.ConvertAll(t => t.Key));
        Assert.Equal(new[] { "a", "b" }, def.AddObjectiveTarget[0].Path);
        Assert.Equal("b", def.AddObjectiveTarget[0].Node);
        Assert.True(def.AddObjectiveTarget[0].Scoped);
        Assert.False(def.AddObjectiveTarget[1].Scoped);
        Assert.Equal("d/e/f", def.RemoveOtherTarget[0].Key);
        Assert.Equal("a/b", def.HelpLabel!.Value.Names[0].Key);
        Assert.Equal(new[] { "a", "b" }, ObjectiveTarget.Parse("a/b").Path);

        var (graph, _) = Build(
            "\"OBJECTIVE1\",[\"ADD_OBJECTIVE_TARGET\",[[\"a\",\"b\"]],"
            + "\"SET_HELP_LABEL\",[[\"a\",\"b\"],\"MSG_OBJ_DEFEND\"]]");
        graph.Step(0.1f);
        Assert.True(graph.IsObjectiveTarget("a/b"));
        Assert.False(graph.IsObjectiveTarget("a"));
        Assert.False(graph.IsObjectiveTarget("b"));
        Assert.Equal("MSG_OBJ_DEFEND", graph.HelpLabels["a/b"]);
    }

    [Fact]
    public void A_lost_player_stops_the_tick_and_the_countdown_without_ending_anything()
    {
        var (graph, world) = Build(
            "\"MISSION_TIMER\",[10.0],"
            + "\"OBJECTIVE1\",[\"BEGIN_DORMANT\",[1.0],\"WAKEUP_SOUND_GROUP\",[\"snd_start\"],"
            + "\"INACTIVE1\",[\"never\"]]");
        Assert.True(graph.NotifyPlayerLost());
        Assert.False(graph.NotifyPlayerLost());
        Run(graph, 30f);
        Assert.False(graph.Ended);
        Assert.False(graph.Ending);
        Assert.Equal(0f, graph.Elapsed);
        Assert.Equal(ObjectiveState.Dormant, graph.StateOf(1));
        Assert.Empty(world.SoundGroups);
    }

    [Fact]
    public void The_countdown_running_out_raises_one_expiry_notice_before_the_wrap_up()
    {
        // The HUD's "Time Expired" / "Mission LOST!" pair hangs off this, so it fires once, as the
        // clock stops, and never on a NOLOSS clock, which pins at zero instead of expiring.
        var (graph, _) = Build(
            "\"MISSION_TIMER\",[10.0],\"OBJECTIVE1\",[\"BEGIN_DORMANT\",[0.1],"
            + "\"RESET_TIMER\",[1.0],\"INACTIVE1\",[\"never\"]]");
        int expired = 0;
        graph.TimerExpired += () => expired++;
        Run(graph, 0.5f);
        Assert.True(graph.TimerRunning);
        Assert.Equal(0, expired);
        Run(graph, 2f);
        Assert.False(graph.TimerRunning);
        Assert.Equal(1, expired);
        Assert.True(graph.Ending);
        Run(graph, 5f);
        Assert.Equal(1, expired);
        Assert.Equal(MissionOutcome.Lost, graph.Outcome);
    }

    [Fact]
    public void A_NOLOSS_countdown_pins_at_zero_and_raises_no_expiry_notice()
    {
        var (graph, _) = Build(
            "\"MISSION_TIMER\",[10.0,\"NOLOSS\"],\"OBJECTIVE1\",[\"BEGIN_DORMANT\",[0.1],"
            + "\"RESET_TIMER\",[1.0],\"INACTIVE1\",[\"never\"]]");
        int expired = 0;
        graph.TimerExpired += () => expired++;
        Run(graph, 5f);
        Assert.Equal(0, expired);
        Assert.Equal(0f, graph.TimerRemaining);
        Assert.False(graph.Ended);
    }

    [Fact]
    public void The_lost_players_wreck_landing_ends_the_mission_lost_and_only_once()
    {
        var (graph, _) = Build("\"OBJECTIVE1\",[\"INACTIVE1\",[\"never\"]]");
        var outcomes = new List<MissionOutcome>();
        graph.MissionEnded += outcome => outcomes.Add(outcome);
        Assert.False(graph.EndAfterPlayerLost());
        graph.NotifyPlayerLost();
        Assert.True(graph.EndAfterPlayerLost());
        Assert.False(graph.EndAfterPlayerLost());
        Assert.Equal(new[] { MissionOutcome.Lost }, outcomes);
        Assert.Equal(MissionOutcome.Lost, graph.Outcome);
    }

    [Fact]
    public void A_mission_already_won_when_the_player_died_stays_won()
    {
        // The debrief reads the won flag alone, so an INSTANTWIN that fired before the death
        // survives it; the death only stops the wrap-up that would have delivered it.
        // A step shorter than INSTANTWIN's own 0.1 s wrap-up, so the win is decided but undelivered.
        var (graph, _) = Build("\"OBJECTIVE1\",[\"INSTANTWIN\"]");
        graph.Step(0.01f);
        Assert.True(graph.Ending);
        graph.NotifyPlayerLost();
        Run(graph, 5f);
        Assert.False(graph.Ended);
        Assert.True(graph.EndAfterPlayerLost());
        Assert.Equal(MissionOutcome.Won, graph.Outcome);
    }

    [ExtractedDataFact]
    public void The_shipped_C1_M02_script_parses_to_its_censused_shape()
    {
        var script = ObjectiveScript.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C1", "M02"));
        Assert.Equal(50, script.Objectives.Count);
        Assert.Equal(0f, script.MissionTimer);
        var first = script.Objectives[0];
        Assert.True(first.BeginDormant);
        Assert.Equal(2f, first.DormantUntil);
        Assert.Equal(new[] { "aagun**" }, first.WakeupTurrets);
        Assert.Equal("snd_NW2Start", first.WakeSoundGroup);
        Assert.Equal(new[] { 48 }, first.WakeWhenComplete);

        // OBJECTIVE2 is a null body: it parses, starts awake and completes as a no-op.
        Assert.False(script.Objectives[1].HasConditions);
        var third = script.Objectives[2];
        Assert.Equal(ObjectiveClass.Primary, third.Identity!.Value.Class);
        Assert.Equal(1, third.Identity!.Value.Priority);
        Assert.Equal(4, third.SetAiNet.Count);
        Assert.Equal(9, third.KillWhenComplete.Count);
        Assert.Equal((6, 5f), third.NapWhenComplete);
        Assert.Contains(script.Objectives, o => o.InstantWin);
    }

    [ExtractedDataFact]
    public void C1_M04_reaches_the_squad_wake_with_the_tower_down_inside_the_distress_window()
    {
        // 15 (tower down) kills 16 and naps 17, 17 naps 19, and 19's nap is HELD by
        // TICK_DEPENDS_ON_OBJ 29 until 28 wakes 29; then 19 wakes 3 s later and naps 20 for 90 s.
        var script = ObjectiveScript.Load(SessionPaths.MissionZrdr(TestData.DataRoot!, "C1", "M04"));
        var world = new FakeWorld();
        var graph = new ObjectiveGraph(script, world);
        var log = new List<ObjectiveTransition>();
        graph.Transitioned += log.Add;
        float WokeAt(int n) => log.Find(t => t.Number == n && t.Kind == ObjectiveTransitionKind.Woke).Elapsed;

        graph.Wake(14);
        world.Inactive.Add("rtwr_healthy");
        Run(graph, 2f);
        Assert.True(graph.CompletedOf(15));
        Assert.False(graph.AliveOf(16));
        Assert.Equal(ObjectiveState.Napping, graph.StateOf(17));
        Run(graph, 20f);
        Assert.True(graph.CompletedOf(17));
        Assert.Equal(ObjectiveState.Napping, graph.StateOf(19));
        Assert.Contains(log, t => t.Number == 19 && t.Kind == ObjectiveTransitionKind.Napped && t.Gated);
        Assert.Equal(ObjectiveState.Dormant, graph.StateOf(29));

        // A Promised Land hatch goes down: 23 completes and naps 24 for 25 s; 24 wakes 28.
        world.AnimStates["destroy_hkzep_rbroad1"] = 4;
        Run(graph, 30f);
        Assert.True(graph.CompletedOf(24));
        Assert.Equal(ObjectiveState.Awake, graph.StateOf(28));
        Assert.Equal(ObjectiveState.Napping, graph.StateOf(19));

        // Group 2 down to two: 28 completes and wakes 29, which releases 19's held nap.
        world.GroupLive[2] = 2;
        Run(graph, 1f);
        Assert.Equal(ObjectiveState.Awake, graph.StateOf(29));
        Run(graph, 5f);
        Assert.True(graph.CompletedOf(19));
        Assert.InRange(WokeAt(19) - WokeAt(29), 2.9f, 3.3f);
        Assert.Equal(ObjectiveState.Napping, graph.StateOf(20));
        Run(graph, 95f);
        Assert.InRange(WokeAt(20) - WokeAt(19), 89.9f, 90.3f);
        Assert.Contains("blakebloodhawk_1", world.WokenEnemies);
        Assert.Equal(ObjectiveState.Awake, graph.StateOf(40));

        // The Promised Land goes down: 30 wakes 42, and the DEDG chain 42/43/44 (groups 1, 2, 5
        // empty) naps the docking, 31, in.
        world.Inactive.Add("panelleft1");
        Run(graph, 1f);
        Assert.True(graph.CompletedOf(30));
        Assert.Equal(ObjectiveState.Awake, graph.StateOf(42));
        world.GroupLive[1] = 0;
        world.GroupLive[2] = 0;
        world.GroupLive[5] = 0;
        Run(graph, 3f);
        Assert.True(graph.CompletedOf(44));
        Assert.True(graph.CompletedOf(31));
        Assert.Contains("pzhookpoint", graph.ObjectiveTargets);
    }

    [Fact]
    public void A_travelers_objective_completes_on_the_guest_who_arrives_first()
    {
        var (graph, world) = Build(
            "\"OBJECTIVE1\",[\"TRAVELERS\",[\"player\",\"APPROACHING\",[1000.0,0.0,0.0],100.0]]");
        world.Humans.Add(new HumanState(Vector3.Zero, null, false));
        world.Humans.Add(new HumanState(new Vector3(2000f, 0f, 0f), null, false));
        Run(graph, 1f);
        Assert.False(graph.CompletedOf(1));

        // The guest flies in while the scripted player stays where it was: the objective is the
        // human field's, so the first one to arrive settles it.
        world.Humans[1] = new HumanState(new Vector3(1050f, 0f, 0f), null, false);
        Run(graph, 1f);
        Assert.True(graph.CompletedOf(1));
    }

    [Fact]
    public void A_departing_travelers_objective_waits_for_the_last_human_out()
    {
        var (graph, world) = Build(
            "\"OBJECTIVE1\",[\"TRAVELERS\",[\"player\",\"LEAVING\",[0.0,0.0,0.0],100.0]]");
        world.Humans.Add(new HumanState(new Vector3(500f, 0f, 0f), null, false));
        world.Humans.Add(new HumanState(new Vector3(50f, 0f, 0f), null, false));
        Run(graph, 1f);
        Assert.False(graph.CompletedOf(1));

        world.Humans[1] = new HumanState(new Vector3(500f, 0f, 0f), null, false);
        Run(graph, 1f);
        Assert.True(graph.CompletedOf(1));
    }

    [Fact]
    public void A_dedg_counts_every_human_the_capture_stamped_with_its_group()
    {
        // The roster's own members of group 7 are gone; two humans hold captured aeroplanes
        // stamped with it, so the group is not wiped out until both of them are.
        var (graph, world) = Build("\"OBJECTIVE1\",[\"DEDG\",[7,0]]");
        world.GroupLive[7] = 0;
        world.Humans.Add(new HumanState(Vector3.Zero, 7, false));
        world.Humans.Add(new HumanState(Vector3.Zero, 7, false));
        Run(graph, 1f);
        Assert.False(graph.CompletedOf(1));

        world.Humans[0] = new HumanState(Vector3.Zero, 7, true);
        Run(graph, 1f);
        Assert.False(graph.CompletedOf(1));

        world.Humans[1] = new HumanState(Vector3.Zero, 7, true);
        Run(graph, 1f);
        Assert.True(graph.CompletedOf(1));
    }

    [Fact]
    public void An_awake_dedg_widens_its_group_every_tick_it_is_tested()
    {
        var (graph, world) = Build("\"OBJECTIVE1\",[\"DEDG\",[3,0]]");
        world.GroupLive[3] = 2;
        Run(graph, 1f);
        Assert.InRange(world.Widened.Count, 9, 11);
        Assert.All(world.Widened, g => Assert.Equal(3, g));
        Assert.False(graph.CompletedOf(1));
    }

    [Fact]
    public void A_dedg_that_is_not_awake_widens_nothing()
    {
        // Dormant, then gated on a dormant objective: neither is tested, so neither widens. The
        // original's walk sits behind the same awake-and-not-complete gate.
        var (dormant, dormantWorld) = Build("\"OBJECTIVE1\",[\"BEGIN_DORMANT\",[5.0],\"DEDG\",[3,0]]");
        dormantWorld.GroupLive[3] = 2;
        Run(dormant, 2f);
        Assert.Empty(dormantWorld.Widened);

        var (gated, gatedWorld) = Build(
            "\"OBJECTIVE1\",[\"TICK_DEPENDS_ON_OBJ\",[2],\"DEDG\",[3,0]],"
            + "\"OBJECTIVE2\",[\"BEGIN_DORMANT\",[60.0],\"INACTIVE1\",[\"never\"]]");
        gatedWorld.GroupLive[3] = 2;
        Run(gated, 2f);
        Assert.Empty(gatedWorld.Widened);
    }

    [Fact]
    public void A_completed_dedg_stops_widening_and_a_group_0_clause_never_starts()
    {
        var (graph, world) = Build("\"OBJECTIVE1\",[\"DEDG\",[3,0]]");
        world.GroupLive[3] = 0;
        Run(graph, 1f);
        Assert.True(graph.CompletedOf(1));
        int widenedWhileAwake = world.Widened.Count;
        Run(graph, 1f);
        Assert.Equal(widenedWhileAwake, world.Widened.Count);

        var (zero, zeroWorld) = Build("\"OBJECTIVE1\",[\"DEDG\",[0,1]]");
        zeroWorld.GroupLive[0] = 2;
        Run(zero, 1f);
        Assert.Empty(zeroWorld.Widened);
    }

    private static (ObjectiveGraph Graph, FakeWorld World) Build(string body)
    {
        var world = new FakeWorld();
        return (new ObjectiveGraph(Script(body), world), world);
    }

    private static ObjectiveScript Script(string body)
    {
        var dir = TestData.TempDir();
        File.WriteAllText(Path.Combine(dir, "objectives.json"), "[[" + body + "]]");
        return ObjectiveScript.Load(dir);
    }

    private static void Run(ObjectiveGraph graph, float seconds)
    {
        for (float t = 0f; t < seconds; t += 0.1f)
        {
            graph.Step(0.1f);
        }
    }

    // Answers only what a test sets, and records every world-touching action.
    private sealed class FakeWorld : IObjectiveWorld
    {
        public HashSet<string> Inactive { get; } = new();

        public Dictionary<string, int> AnimStates { get; } = new();

        public List<string> SoundGroups { get; } = new();

        public List<string> AiNets { get; } = new();

        public List<string> Anims { get; } = new();

        public Dictionary<int, int> GroupLive { get; } = new();

        public List<int> Widened { get; } = new();

        public List<string> WokenEnemies { get; } = new();

        // The human field, read through the same CampaignHumanField rules CampaignDirector's own
        // world uses, so these tests drive the graph over the answers a co-op sortie gives it.
        public List<HumanState> Humans { get; } = new();

        public bool? NodeInactive(IReadOnlyList<string> path) => Inactive.Contains(path[^1]);

        public int AnimState(string anim) => AnimStates.TryGetValue(anim, out int s) ? s : 0;

        public int? GroupLiveCount(int group, string? generator) =>
            GroupLive.TryGetValue(group, out int live)
                ? live + CampaignHumanField.LiveInGroup(Humans, group)
                : null;

        // One entry per widening call, so a test can count the ticks that widened a group.
        public void WidenGroupEngagement(int group) => Widened.Add(group);

        public bool? TravelersMet(TravelersSpec spec)
        {
            if (Humans.Count == 0 || spec.WherePoint is not { } p)
            {
                return null;
            }

            return CampaignHumanField.Travelers(
                Humans, new Vector3(p[0], p[1], p[2]), spec.Radius, spec.Approaching);
        }

        public void WakeupEnemies(IReadOnlyList<string> names) => WokenEnemies.AddRange(names);

        public void WakeupTurrets(IReadOnlyList<string> patterns)
        {
        }

        public void WakeupZepTurrets(IReadOnlyList<string> nodes)
        {
        }

        public void WakeupGenerator(string name, int count)
        {
        }

        public void WakeAnim(string anim, string? node) => Anims.Add(anim);

        public void PlaySoundGroup(string group) => SoundGroups.Add(group);

        public void StopQueuedSounds(IReadOnlyList<string> names)
        {
        }

        public void WarpVehicle(string vehicle, IReadOnlyList<WarpPoint> points)
        {
        }

        public void SetAiTeam(IReadOnlyList<(string Name, int Team)> entries)
        {
        }

        public void SetAiNet(IReadOnlyList<(string Name, string Net)> entries)
        {
            foreach (var entry in entries)
            {
                AiNets.Add(entry.Net);
            }
        }

        public void SetAiAttackRadius(IReadOnlyList<(string Name, float Radius)> entries)
        {
        }

        public void CompletedZepcannons(IReadOnlyList<(string Zeppelin, int Flag)> entries)
        {
        }

        public void CompletedStoppoint(IReadOnlyList<(string Net, int Stop, int Flag)> entries)
        {
        }

        public void StartTaxi(IReadOnlyList<string> names)
        {
        }
    }
}
