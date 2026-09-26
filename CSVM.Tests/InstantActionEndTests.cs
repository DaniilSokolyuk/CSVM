using System.Globalization;
using System.IO;
using CSVM.Mech3;
using CSVM.Session.InstantAction;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Instant Action mission end: the per-mission-type win
/// condition, the INVENTED per-pilot lives ledger and the one-way outcome. Runs engine-free,
/// <see cref="InstantActionRuntime"/>'s end half holds no engine type and logs nothing, the same
/// construction rule <c>VersusMatch</c> follows, so every rule here is pinned without a session.
/// Each def goes in through the real <c>--ia=</c> reader rather than a hand-built record, so a
/// change to how <c>lives</c> or <c>mission_type</c> parses moves these tests too.
/// </summary>
public class InstantActionEndTests
{
    [Fact]
    public void EachMissionTypeCarriesItsOwnWinCondition()
    {
        Assert.Equal(InstantActionObjective.AceDown,
            InstantActionRuntime.ObjectiveFor("dogfight_ace"));
        Assert.Equal(InstantActionObjective.WavesCleared,
            InstantActionRuntime.ObjectiveFor("dogfight_squadron"));
        Assert.Equal(InstantActionObjective.ZonesFlown,
            InstantActionRuntime.ObjectiveFor("stunt_flying"));
        // The zeppelin run wins on the ENGINES, not the hull (FUN_0045b9d0 at 0x0045be0a tests
        // the live engine vector before the hull's death byte), the mode's own briefing string
        // and its "Disable Engines" target label say the same.
        Assert.Equal(InstantActionObjective.ZeppelinDisabled,
            InstantActionRuntime.ObjectiveFor("zeppelin_run"));
        // ground_target is the fifth type every shipped map's disallow_missions bars, and this
        // milestone does not implement it: no win condition, deliberately.
        Assert.Null(InstantActionRuntime.ObjectiveFor("ground_target"));
        Assert.Null(InstantActionRuntime.ObjectiveFor("something_invented"));
    }

    [Fact]
    public void OnlyTheMissionsOwnObjectiveWinsIt()
    {
        var zeppelin = new InstantActionRuntime(Def("zeppelin_run"));

        // A zeppelin run clears all four of its waves like every other mode (F12 credits them
        // either way), that is not this mission's win.
        zeppelin.ReportObjective(InstantActionObjective.WavesCleared);
        zeppelin.ReportObjective(InstantActionObjective.AceDown);
        zeppelin.ReportObjective(InstantActionObjective.ZonesFlown);
        Assert.False(zeppelin.Ended);

        zeppelin.ReportObjective(InstantActionObjective.ZeppelinDisabled);
        Assert.Equal(InstantActionOutcome.Won, zeppelin.Outcome);
    }

    [Fact]
    public void ClearingAStuntMissionsWavesIsNotItsWinEither()
    {
        // Stunt flying carries enemies, but clearing them is not what completes the run: the
        // zones are, exactly as the zeppelin is on a zeppelin run.
        var stunt = new InstantActionRuntime(Def("stunt_flying"));

        stunt.ReportObjective(InstantActionObjective.WavesCleared);
        stunt.ReportObjective(InstantActionObjective.ZeppelinDisabled);
        stunt.ReportObjective(InstantActionObjective.AceDown);
        Assert.False(stunt.Ended);

        stunt.ReportObjective(InstantActionObjective.ZonesFlown);
        Assert.Equal(InstantActionOutcome.Won, stunt.Outcome);

        // The able-to-fail half of the same A/B: one field changed and the same report wins.
        var squadron = new InstantActionRuntime(Def("dogfight_squadron"));
        squadron.ReportObjective(InstantActionObjective.WavesCleared);
        Assert.Equal(InstantActionOutcome.Won, squadron.Outcome);
    }

    [Fact]
    public void OneLifeIsTheDefaultAndADownedSoloPilotLosesTheMission()
    {
        var ia = new InstantActionRuntime(Def("dogfight_ace"));
        Assert.Equal(1, ia.Def.Lives);
        ia.RegisterPilot(0);

        Assert.False(ia.NotifyPilotDown(0));
        Assert.True(ia.IsSpectating(0));
        Assert.Equal(0, ia.LivesLeft(0));
        Assert.Equal(InstantActionOutcome.Lost, ia.Outcome);
    }

    [Fact]
    public void NLivesGiveNMinusOneRespawns()
    {
        var ia = new InstantActionRuntime(Def("dogfight_squadron", lives: 3));
        ia.RegisterPilot(0);

        Assert.True(ia.NotifyPilotDown(0));
        Assert.Equal(2, ia.LivesLeft(0));
        Assert.True(ia.NotifyPilotDown(0));
        Assert.Equal(1, ia.LivesLeft(0));
        Assert.False(ia.NotifyPilotDown(0));   // the third death is the last
        Assert.Equal(0, ia.LivesLeft(0));
        Assert.Equal(InstantActionOutcome.Lost, ia.Outcome);
    }

    [Fact]
    public void ZeroLivesIsUnlimitedAndNeverEndsTheMission()
    {
        var ia = new InstantActionRuntime(Def("dogfight_squadron", lives: 0));
        ia.RegisterPilot(0);

        for (int i = 0; i < 10; i++)
        {
            Assert.True(ia.NotifyPilotDown(0));
        }
        Assert.False(ia.IsSpectating(0));
        Assert.False(ia.Ended);
    }

    [Fact]
    public void AnUnregisteredPilotKeepsItsOrdinaryRespawn()
    {
        var ia = new InstantActionRuntime(Def("dogfight_ace"));

        Assert.True(ia.NotifyPilotDown(2));
        Assert.False(ia.Ended);
        Assert.Equal(-1, ia.LivesLeft(2));
    }

    [Fact]
    public void TheMissionRunsWhileAnyHumanIsStillFlying()
    {
        var ia = new InstantActionRuntime(Def("dogfight_squadron"));
        ia.RegisterPilot(0);
        ia.RegisterPilot(1);
        Assert.Equal(2, ia.PilotCount);

        Assert.False(ia.NotifyPilotDown(1));
        Assert.False(ia.Ended);            // P2 watches; P1 flies on

        Assert.False(ia.NotifyPilotDown(0));
        Assert.Equal(InstantActionOutcome.Lost, ia.Outcome);
    }

    [Fact]
    public void TheFirstOutcomeStandsAndIsRaisedOnce()
    {
        var ia = new InstantActionRuntime(Def("dogfight_ace"));
        ia.RegisterPilot(0);
        int raised = 0;
        InstantActionOutcome seen = InstantActionOutcome.Running;
        ia.MissionEnded += o =>
        {
            raised++;
            seen = o;
        };

        ia.ReportObjective(InstantActionObjective.AceDown);
        ia.NotifyPilotDown(0);             // the ace took the player with it, the win stands
        ia.ReportObjective(InstantActionObjective.AceDown);

        Assert.Equal(InstantActionOutcome.Won, ia.Outcome);
        Assert.Equal(InstantActionOutcome.Won, seen);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ADisabledObjectiveCannotBeWonButTheMissionCanStillBeLost()
    {
        var ia = new InstantActionRuntime(Def("dogfight_squadron"));
        ia.RegisterPilot(0);
        ia.DisableObjective();

        ia.ReportObjective(InstantActionObjective.WavesCleared);
        Assert.False(ia.Ended);

        ia.NotifyPilotDown(0);
        Assert.Equal(InstantActionOutcome.Lost, ia.Outcome);
    }

    [Fact]
    public void ZoneSetsAreFlownWhenEveryPilotWhoCanStillFlyHasFinished()
    {
        // Decision 10, with lives folded in. (OutOfLives, Finished) per pilot.
        Assert.False(InstantActionRuntime.ZoneSetsFlown(new[] { (false, true), (false, false) }));
        Assert.True(InstantActionRuntime.ZoneSetsFlown(new[] { (false, true), (false, true) }));

        // A pilot out of lives can never clear another gate: counting it would hold the mission
        // open forever, which is exactly what StuntRace's own all-finished rule would do.
        Assert.True(InstantActionRuntime.ZoneSetsFlown(new[] { (false, true), (true, false) }));

        // Nobody left flying is a LOSS, decided by the lives ledger, never a win.
        Assert.False(InstantActionRuntime.ZoneSetsFlown(new[] { (true, false), (true, true) }));
        Assert.False(InstantActionRuntime.ZoneSetsFlown(System.Array.Empty<(bool, bool)>()));
    }

    [Fact]
    public void TheMissionClockRunsOnSimDtAndFreezesAtTheOutcome()
    {
        var ia = new InstantActionRuntime(Def("dogfight_ace"));

        for (int i = 0; i < 120; i++)
        {
            ia.Advance(1f / 60f);
        }
        Assert.Equal(2f, ia.Elapsed, 3);

        ia.ReportObjective(InstantActionObjective.AceDown);
        ia.Advance(5f);
        Assert.Equal(2f, ia.Elapsed, 3);
    }

    // The real --ia= reader over a minimal hand-authored file: everything not named here takes the
    // original's own reset defaults, lives included (1).
    private static InstantActionDef Def(string missionType, int? lives = null)
    {
        string body = lives is { } n
            ? $", \"lives\": {n.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
        string file = Path.Combine(TestData.TempDir(), "ia-end.json");
        File.WriteAllText(file, $"{{\"mission_type\": \"{missionType}\"{body}}}");
        return InstantAction.LoadFromJson(file);
    }
}
