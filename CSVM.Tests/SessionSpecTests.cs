using System.Linq;
using CSVM;
using CSVM.Effects;
using CSVM.Mech3;
using CSVM.Utils;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The launch-argument resolution truth table: what each command line settles, rule by rule.
///
/// <para>Each fact here names the rule that settles its row, not merely that a row moved. This
/// file is the only thing standing behind the launch surface (no whole-command-line baseline
/// backs it up), so when a rule is added or changed, add the fact with the rule.</para>
///
/// <para>Two known defects are asserted AS THEY ARE, marked below. Both are reproduced on purpose;
/// fixing either is a behaviour change and needs its own item.</para>
/// </summary>
public class SessionSpecTests
{
    // ---- Mode arbitration ----------------------------------------------------------------------

    [Fact]
    public void ABareLaunchIsTheMenu()
    {
        var s = S();
        Assert.Equal(SessionMode.Menu, s.Mode);
        Assert.False(s.HasContentArg);
        Assert.True(s.ShowsMenu);
    }

    [Theory]
    [InlineData("--plane=player_fury")]
    [InlineData("--chapter=C4")]
    [InlineData("--chapter")]
    [InlineData("--stage=empty")]
    [InlineData("--screenshot=shot.png")]
    [InlineData("--fly")]
    [InlineData("--stunt")]
    [InlineData("--vs")]
    public void AnyContentArgFliesByDefault(string arg)
    {
        var s = S(arg);
        Assert.True(s.HasContentArg);
        Assert.Equal(SessionMode.Fly, s.Mode);
        Assert.False(s.ShowsMenu);
    }

    [Fact]
    public void ViewerOptsOutOfTheFlightDefault()
    {
        Assert.Equal(SessionMode.Viewer, S("--viewer", "--plane=player_fury").Mode);
    }

    /// <summary>Anim lab &gt; freecam &gt; viewer &gt; fly, and the winner clears the modifiers of
    /// everything it beat.</summary>
    [Fact]
    public void TheAnimLabBeatsEveryOtherMode()
    {
        var s = S("--anim-lab", "--fly", "--stunt", "--viewer", "--damage", "--freecam");
        Assert.Equal(SessionMode.AnimLab, s.Mode);
        Assert.False(s.Stunt);
        Assert.False(s.DamageLab);
    }

    /// <summary>⚠ The modifiers are the assertion that bites here. <c>Mode</c> alone cannot tell
    /// this rule from the viewer-beats-fly one below, with `--viewer` also on the line, that rule
    /// clears the same modifiers and the mode ternary reaches Freecam either way. So the case that
    /// isolates the freecam rule carries NO other mode flag: the stunt and damage-lab modifiers have
    /// nobody else to clear them.</summary>
    [Fact]
    public void FreecamBeatsFlightAndTheViewer()
    {
        var s = S("--freecam", "--fly", "--stunt", "--viewer", "--damage");
        Assert.Equal(SessionMode.Freecam, s.Mode);
        Assert.False(s.Stunt);
        Assert.False(s.DamageLab);

        var stunting = S("--freecam", "--stunt");
        Assert.Equal(SessionMode.Freecam, stunting.Mode);
        Assert.False(stunting.Stunt);

        var damaged = S("--freecam", "--damage");
        Assert.Equal(SessionMode.Freecam, damaged.Mode);
        Assert.False(damaged.DamageLab);
    }

    [Fact]
    public void TheViewerBeatsFlight()
    {
        var s = S("--viewer", "--fly", "--stunt");
        Assert.Equal(SessionMode.Viewer, s.Mode);
        Assert.False(s.Stunt);
    }

    [Fact]
    public void ANodeStageForcesTheViewerAndItsChapter()
    {
        var s = S("--node=hk_zep", "--fly", "--freecam");
        Assert.Equal(SessionMode.Viewer, s.Mode);
        // The subtree comes out of the chapter's gamez, so asking for one asks for that world.
        Assert.True(s.ChapterGiven);
    }

    [Fact]
    public void ANodeStageDoesNotBeatTheAnimLab()
    {
        Assert.Equal(SessionMode.AnimLab, S("--anim-lab", "--node=hk_zep").Mode);
    }

    [Theory]
    [InlineData("--damage")]
    [InlineData("--markers")]
    [InlineData("--weapon-test")]
    public void TheseFlagsVoteForTheViewer(string arg) => Assert.Equal(SessionMode.Viewer, S(arg).Mode);

    [Theory]
    [InlineData("--damage-test")]
    [InlineData("--effects-test")]
    public void TheseProbesVoteForTheFreecam(string arg) => Assert.Equal(SessionMode.Freecam, S(arg).Mode);

    [Theory]
    [InlineData("--play-anim=waterfall")]
    [InlineData("--debug-anim-ui")]
    public void TheseFlagsVoteForTheAnimLab(string arg) => Assert.Equal(SessionMode.AnimLab, S(arg).Mode);

    /// <summary>The four mode bools are computed from <c>Mode</c>, so exactly one is ever true,
    /// the property hand-written per-mode predicates get wrong one term at a time.</summary>
    [Theory]
    [InlineData("--fly")]
    [InlineData("--viewer")]
    [InlineData("--freecam")]
    [InlineData("--anim-lab")]
    public void AtMostOneModeBoolIsEverTrue(string arg)
    {
        var s = S(arg);
        int on = (s.Fly ? 1 : 0) + (s.Viewer ? 1 : 0) + (s.Freecam ? 1 : 0) + (s.AnimLab ? 1 : 0);
        Assert.True(on <= 1, $"{arg} lit {on} mode bools");
    }

    [Fact]
    public void TheProbeThatCoercedTheModeIsNamed()
    {
        Assert.Equal(SessionProbe.DamageTest, S("--damage-test").Probe);
        Assert.Equal(SessionProbe.EffectsTest, S("--effects-test").Probe);
        Assert.Equal(SessionProbe.WeaponTest, S("--weapon-test").Probe);
        Assert.Equal(SessionProbe.None, S("--freecam").Probe);
    }

    // ---- Step order IS the behaviour -----------------------------------------------------------

    /// <summary>`--stunt` moves the spawn list BEFORE arbitration can clear the stunt flag, so a
    /// mode that beats flight still inherits the stunt spawn list. Reordering those two steps is a
    /// behaviour change that looks like tidying.</summary>
    [Fact]
    public void StuntPicksItsSpawnListBeforeArbitrationCanClearIt()
    {
        var s = S("--anim-lab", "--stunt");
        Assert.Equal(SessionMode.AnimLab, s.Mode);
        Assert.False(s.Stunt);
        Assert.Equal("stunt_flying", s.Scenario);
    }

    [Fact]
    public void APinnedScenarioSurvivesStunt()
    {
        var s = S("--stunt", "--scenario=hangar_run");
        Assert.True(s.Stunt);
        Assert.Equal("hangar_run", s.Scenario);
        Assert.True(s.ScenarioExplicit);
    }

    // ---- --vs (Versus / "Dogfight") ---------------------------------------------------------

    [Fact]
    public void VsParsesToFlightWithTheVersusModifier()
    {
        var s = S("--vs");
        Assert.Equal(SessionMode.Fly, s.Mode);
        Assert.True(s.Versus);
        Assert.False(s.Stunt);
    }

    /// <summary>Not last-flag-wins like the rest of the parser: `--vs` beats `--stunt` by FIXED
    /// precedence whichever order they appear in, and says so.</summary>
    [Fact]
    public void VsBeatsStuntByFixedPrecedenceWithAWarning()
    {
        var vsThenStunt = S("--vs", "--stunt");
        Assert.True(vsThenStunt.Versus);
        Assert.False(vsThenStunt.Stunt);
        Assert.Contains(vsThenStunt.Warnings, w => w.Message.Contains("--vs beats --stunt"));

        var stuntThenVs = S("--stunt", "--vs");
        Assert.True(stuntThenVs.Versus);
        Assert.False(stuntThenVs.Stunt);
        Assert.Contains(stuntThenVs.Warnings, w => w.Message.Contains("--vs beats --stunt"));
    }

    [Fact]
    public void VsKillsAndVsTimeDefaultToFiveAndFive()
    {
        var s = S("--vs");
        Assert.Equal(5, s.VsKills);
        Assert.Equal(5, s.VsTimeMinutes);
    }

    [Fact]
    public void VsKillsAndVsTimeTakeExplicitValues()
    {
        var s = S("--vs", "--vs-kills=10", "--vs-time=15");
        Assert.Equal(10, s.VsKills);
        Assert.Equal(15, s.VsTimeMinutes);
    }

    /// <summary>0 disables the limit, a match with both at 0 has no end condition, which is
    /// accepted here (the win/lose flow that would need to guard against it is a later item).</summary>
    [Fact]
    public void ZeroDisablesEitherVsLimit()
    {
        var s = S("--vs", "--vs-kills=0", "--vs-time=0");
        Assert.Equal(0, s.VsKills);
        Assert.Equal(0, s.VsTimeMinutes);
    }

    /// <summary>The menu enforces at least 2 joined pilots before it will start a match; the CLI
    /// has no join flow to gate on, so it only warns and runs with what --players= asked for.</summary>
    [Fact]
    public void VsWithOnePlayerWarnsButStillRuns()
    {
        var s = S("--vs", "--players=1");
        Assert.True(s.Versus);
        Assert.Equal(1, s.Players);
        Assert.Contains(s.Warnings, w => w.Message.Contains("needs at least 2 pilots"));

        Assert.Empty(S("--vs", "--players=2").Warnings);
    }

    [Fact]
    public void VsNamesItsModeVs() => Assert.Equal("vs", S("--vs").ModeName);

    [Fact]
    public void VsDefaultsToTheDogfightAceScenario() => Assert.Equal("dogfight_ace", S("--vs").Scenario);

    [Fact]
    public void AnExplicitScenarioBeatsVssDefault()
    {
        var s = S("--vs", "--scenario=hangar_run");
        Assert.True(s.Versus);
        Assert.Equal("hangar_run", s.Scenario);
        Assert.True(s.ScenarioExplicit);
    }

    // ---- --coop -----------------------------------------------

    [Fact]
    public void CoopParsesTrueAndLeavesFlyUnaffected()
    {
        var s = S("--fly", "--coop");
        Assert.Equal(SessionMode.Fly, s.Mode);
        Assert.True(s.Coop);
        Assert.False(s.Versus);
    }

    [Fact]
    public void CoopDefaultsToFalse() => Assert.False(S("--fly").Coop);

    /// <summary>`--vs`'s FFA is explicit and outranks `--coop`, whichever order they're given in,
    /// dropped with a warning rather than racing `--vs` at the assembler.</summary>
    [Fact]
    public void VsDropsCoopWithAWarning()
    {
        var vsThenCoop = S("--vs", "--coop");
        Assert.True(vsThenCoop.Versus);
        Assert.False(vsThenCoop.Coop);
        Assert.Contains(vsThenCoop.Warnings, w => w.Message.Contains("--coop has no effect with --vs"));

        var coopThenVs = S("--coop", "--vs");
        Assert.True(coopThenVs.Versus);
        Assert.False(coopThenVs.Coop);
        Assert.Contains(coopThenVs.Warnings, w => w.Message.Contains("--coop has no effect with --vs"));
    }

    // ---- The launchscreen decision -------------------------------------------------------------

    [Fact]
    public void MenuForcesTheLaunchscreenAlongsideAContentArg()
    {
        var s = S("--menu", "--chapter=C4");
        Assert.Equal(SessionMode.Fly, s.Mode);
        Assert.True(s.ShowsMenu);
    }

    /// <summary>⚠ KNOWN DEFECT, asserted as it is: `--run-tests` resolves to a session that would
    /// show the launchscreen. The harness is saved only by returning before the menu branch, so the
    /// correctness of the test runner rests on statement order in the caller.</summary>
    [Fact]
    public void RunTestsStillResolvesToShowingTheMenu()
    {
        var s = S("--run-tests");
        Assert.False(s.HasContentArg);
        Assert.True(s.ShowsMenu);
    }

    // ---- The boot sequence decision ------------------------------------------------------------

    /// <summary>The rule the boot sequence rests on: a bare launch plays it and every other launch
    /// does not, so no test, golden or perf run grows by the three movies.</summary>
    [Fact]
    public void OnlyABareLaunchPlaysTheBootSequence()
    {
        Assert.True(S().PlaysBootSequence);
        Assert.False(S().SkipIntro);
    }

    /// <summary>Any argument suppresses it, which is why <c>--skip-intro</c> is documented for the
    /// desktop shortcut rather than as a general suppressor. The rows are the argument every
    /// scripted surface carries: the engine harness, a golden, a perf run, and a launch that still
    /// shows the launchscreen.</summary>
    [Theory]
    [InlineData("--run-tests")]
    [InlineData("--screenshot=shot.png")]
    [InlineData("--det")]
    [InlineData("--mute")]
    [InlineData("--volume=1.0")]
    [InlineData("--menu")]
    [InlineData("--chapter=C4")]
    [InlineData("--movie=zipper")]
    [InlineData("--not-a-flag")]
    public void AnyArgumentAtAllSuppressesTheBootSequence(string arg)
        => Assert.False(S(arg).PlaysBootSequence);

    /// <summary><c>--skip-intro</c> denies the sequence by meaning, not by being one more argument,
    /// and it neither selects content nor hides the launchscreen.</summary>
    [Fact]
    public void SkipIntroDeniesTheSequenceAndNothingElse()
    {
        var s = S("--skip-intro");
        Assert.True(s.SkipIntro);
        Assert.False(s.PlaysBootSequence);
        Assert.False(s.HasContentArg);
        Assert.True(s.ShowsMenu);
        Assert.Equal(SessionMode.Menu, s.Mode);
    }

    /// <summary><c>--intro</c> is the other direction: it plays the sequence despite the arguments
    /// that would otherwise suppress it. The rows are what the two launch scripts prepend and what
    /// a judging run would pair it with.</summary>
    [Theory]
    [InlineData("--volume=1.0")]
    [InlineData("--mute")]
    [InlineData("--menu")]
    [InlineData("--not-a-flag")]
    public void IntroPlaysTheSequenceDespiteAnotherArgument(string arg)
        => Assert.True(S("--intro", arg).PlaysBootSequence);

    /// <summary>It selects no content and forces no mode, so a launch carrying it still reaches
    /// the launchscreen the sequence hands off to.</summary>
    [Fact]
    public void IntroSelectsNoContentAndForcesNoMode()
    {
        var s = S("--intro");
        Assert.True(s.ForceIntro);
        Assert.True(s.PlaysBootSequence);
        Assert.False(s.HasContentArg);
        Assert.True(s.ShowsMenu);
        Assert.Equal(SessionMode.Menu, s.Mode);
    }

    /// <summary><c>--skip-intro</c> wins whichever order the two are given in: a suppressor another
    /// flag can overrule is not a suppressor.</summary>
    [Fact]
    public void SkipIntroBeatsIntroBothWaysRound()
    {
        Assert.False(S("--intro", "--skip-intro").PlaysBootSequence);
        Assert.False(S("--skip-intro", "--intro").PlaysBootSequence);
    }

    /// <summary>Nothing automatic reaches it: the flag has to be typed, so a launch that carries
    /// only what a scripted surface passes still plays nothing.</summary>
    [Theory]
    [InlineData("--run-tests")]
    [InlineData("--screenshot=x.png")]
    [InlineData("--volume=1.0")]
    public void WithoutTheFlagAScriptedLaunchStillPlaysNothing(string arg)
    {
        Assert.False(S(arg).ForceIntro);
        Assert.False(S(arg).PlaysBootSequence);
    }

    /// <summary>A launch that builds content shows no launchscreen, which is the branch the
    /// sequence lives in, so the flag does nothing there however the rule reads.</summary>
    [Fact]
    public void IntroShowsNoLaunchscreenAlongsideAContentArgument()
        => Assert.False(S("--intro", "--chapter=C4").ShowsMenu);

    // ---- ModeName: the log file's name and the startup line's ----------------------------------

    [Theory]
    [InlineData("anim-lab", "--anim-lab")]
    [InlineData("test", "--damage-test")]
    [InlineData("test", "--run-tests")]
    [InlineData("dump", "--dump-markers")]
    [InlineData("dump", "--dump-config")]
    [InlineData("freecam", "--freecam")]
    [InlineData("viewer", "--viewer")]
    [InlineData("stunt", "--stunt")]
    [InlineData("fly", "--fly")]
    public void TheSessionShapeNamesItself(string expected, string arg)
        => Assert.Equal(expected, S(arg).ModeName);

    /// <summary>⚠ KNOWN DEFECT, asserted as it is: `--dump-flight` is missing from the "dump" arm,
    /// so it logs as a menu session.</summary>
    [Fact]
    public void DumpFlightIsMissingFromTheDumpArm()
        => Assert.Equal("menu", S("--dump-flight").ModeName);

    // ---- Scripted sessions ---------------------------------------------------------------------

    [Fact]
    public void TheFlagDrivingTheSessionIsNamedInPrecedenceOrder()
    {
        Assert.Equal("--screenshot", S("--screenshot=a.png", "--run-tests", "--damage-test").ScriptedBy);
        Assert.Equal("--damage-test", S("--damage-test", "--run-tests").ScriptedBy);
        Assert.Equal("--run-tests", S("--run-tests").ScriptedBy);
        Assert.Equal("", S("--fly").ScriptedBy);
    }

    [Theory]
    [InlineData("--no-focus")]
    [InlineData("--screenshot=a.png")]
    [InlineData("--run-tests")]
    [InlineData("--dump-markers")]
    [InlineData("--dump-weapons")]
    [InlineData("--dump-loadout")]
    [InlineData("--dump-config")]
    [InlineData("--damage-test")]
    [InlineData("--effects-test")]
    [InlineData("--weapon-test")]
    public void AScriptedSessionHidesItsWindow(string arg) => Assert.True(S(arg).IsScripted);

    [Theory]
    [InlineData("--fly")]
    [InlineData("--viewer")]
    [InlineData("--freecam")]
    [InlineData("--anim-lab")]
    public void AnInteractiveSessionAsksForFocus(string arg) => Assert.False(S(arg).IsScripted);

    /// <summary>⚠ KNOWN DRIFT, asserted as it is: `--dump-flight` drives and ends the session (so it
    /// turns the <c>--det</c> bundle on) yet is not a term of <see cref="SessionSpec.IsScripted"/>,
    /// so its window still asks for focus. Same omission as the ModeName arm above.</summary>
    [Fact]
    public void DumpFlightTurnsDetOnButIsNotCountedAsScripted()
    {
        var s = S("--dump-flight");
        Assert.Equal("--dump-flight", s.ScriptedBy);
        Assert.True(s.Det);
        Assert.False(s.IsScripted);
    }

    // ---- The --det bundle ----------------------------------------------------------------------

    [Theory]
    [InlineData("--screenshot=a.png")]
    [InlineData("--dump-markers")]
    [InlineData("--damage-test")]
    [InlineData("--run-tests")]
    [InlineData("--det")]
    public void TheseTurnTheDeterministicBundleOn(string arg) => Assert.True(S(arg).Det);

    /// <summary>The boundary the bundle must never cross: a bare interactive launch keeps its
    /// randomness.</summary>
    [Fact]
    public void ABareFlightIsNotDeterministic()
    {
        var s = S("--fly");
        Assert.False(s.Det);
        Assert.False(s.SeedPinned);
        Assert.False(s.PadsDisabled);
        Assert.True(s.SpawnIndex < 0);
    }

    [Fact]
    public void NoDetBeatsBothTheImplicationAndAnExplicitDet()
    {
        Assert.False(S("--screenshot=a.png", "--no-det").Det);
        Assert.False(S("--det", "--no-det").Det);
        Assert.False(S("--no-det", "--det").Det);
    }

    [Fact]
    public void WhatTurnedTheBundleOnIsAnnounced()
    {
        Assert.Equal("--det", S("--det").DetVia);
        Assert.Equal("--screenshot", S("--screenshot=a.png").DetVia);
        // An explicit --det wins the attribution even when a scripted flag would also imply it.
        Assert.Equal("--det", S("--det", "--run-tests").DetVia);
    }

    /// <summary>A pinned CHOICE beats a pinned dice roll, a seeded pick still moves when the
    /// mission's spawn list grows. An explicit `--spawn=N` still wins.</summary>
    [Fact]
    public void DetPinsTheSpawnToIndexZeroUnlessOneWasNamed()
    {
        Assert.Equal(0, S("--det").SpawnIndex);
        Assert.Equal(3, S("--det", "--spawn=3").SpawnIndex);
        Assert.Equal(-1, S("--fly").SpawnIndex);
    }

    /// <summary>A burst dithers the camera so z-fighting flickers across frames; `--det` defaults it
    /// off instead, because bit-identical frames are the one property a deterministic run is for.</summary>
    [Fact]
    public void TheJitterDefaultFollowsTheBurstAndTheBundle()
    {
        Assert.Equal(0f, S("--fly").JitterDeg);
        Assert.Equal(0.15f, S("--shots=4", "--no-det").JitterDeg);
        Assert.Equal(0f, S("--shots=4", "--det").JitterDeg);
        Assert.Equal(0.5f, S("--shots=4", "--jitter=0.5").JitterDeg);
    }

    [Fact]
    public void EveryPadIsIgnoredWhenAskedOrWhenDeterministic()
    {
        Assert.True(S("--no-pads").PadsDisabled);
        Assert.True(S("--det").PadsDisabled);
        Assert.False(S("--fly").PadsDisabled);
    }

    [Fact]
    public void TheMasterSeedIsPinnedByASeedByDetAndByTheAnimLab()
    {
        Assert.True(S("--seed=9").SeedPinned);
        Assert.True(S("--det").SeedPinned);
        // The animation debugger is deterministic by nature: its whole point is an identical replay.
        Assert.True(S("--anim-lab").SeedPinned);
        Assert.False(S("--freecam").SeedPinned);
    }

    /// <summary>An unpinned seed is drawn from the clock by the CALLER, not here, a spec that read
    /// the clock would not be a function of its args, and a baseline would differ from
    /// itself.</summary>
    [Fact]
    public void AnUnpinnedSeedIsNullRatherThanDrawnHere()
    {
        Assert.Null(S("--fly").PinnedSeed);
        Assert.Equal(Rng.DefaultSeed, S("--det").PinnedSeed);
        Assert.Equal(9ul, S("--seed=9").PinnedSeed);
        // Same args, same answer, twice, because that is the property at stake.
        Assert.Equal(S("--fly").PinnedSeed, S("--fly").PinnedSeed);
    }

    // ---- BuildsCollision: the one shared predicate, consumers must not spell it themselves -----

    [Theory]
    [InlineData("--fly")]
    [InlineData("--damage-test")]
    [InlineData("--collision", "--freecam")]
    [InlineData("--debug-damage", "--freecam")]
    public void TheseSessionsBuildTheWorldsColliders(params string[] args)
        => Assert.True(S(args).BuildsCollision);

    [Theory]
    [InlineData("--freecam")]
    [InlineData("--viewer")]
    [InlineData("--anim-lab")]
    public void TheseDoNotBuildColliders(string arg) => Assert.False(S(arg).BuildsCollision);

    /// <summary>`--debug-damage` is dropped outside the two observation modes, so it stops being a
    /// term there, flight still builds colliders on its own account.</summary>
    [Fact]
    public void DebugDamageIsNotATermWhereItIsDropped()
    {
        var s = S("--viewer", "--debug-damage");
        Assert.Null(s.DebugDamage);
        Assert.False(s.BuildsCollision);
    }

    // ---- WorldMode and the empty stage ---------------------------------------------------------

    [Theory]
    [InlineData("--fly")]
    [InlineData("--freecam")]
    [InlineData("--anim-lab")]
    public void FlightAndTheObservationModesAlwaysBuildTheWorld(string arg)
        => Assert.True(S(arg).WorldMode);

    [Fact]
    public void TheViewerBuildsAWorldOnlyWhenAChapterWasAskedFor()
    {
        Assert.False(S("--viewer", "--plane=player_fury").WorldMode);
        Assert.True(S("--viewer", "--chapter=C4").WorldMode);
        // --node= asks for a chapter implicitly.
        Assert.True(S("--node=hk_zep").WorldMode);
    }

    [Fact]
    public void TheEmptyStageReplacesTheWorldEntirely()
    {
        var s = S("--stage=empty");
        Assert.True(s.EmptyStage);
        Assert.False(s.WorldMode);
    }

    [Fact]
    public void AnUnknownStageIsIgnoredLoudly()
    {
        var s = S("--stage=nowhere");
        Assert.False(s.EmptyStage);
        Assert.Contains(s.Warnings, w => w.Message.Contains("not a known stage"));
    }

    /// <summary>The empty stage has no gamez to inspect, which is what the viewer, the anim lab and
    /// the node stage exist for.</summary>
    [Theory]
    [InlineData("--viewer")]
    [InlineData("--anim-lab")]
    [InlineData("--node=hk_zep")]
    public void TheEmptyStageIsRefusedWhereThereIsNothingToInspect(string arg)
    {
        var s = S("--stage=empty", arg);
        Assert.False(s.EmptyStage);
        Assert.Contains(s.Warnings, w => w.Message.Contains("no gamez to inspect"));
    }

    // ---- Players and the plane list ------------------------------------------------------------

    [Fact]
    public void APlaneListStatesThePlayerCountAndAnExplicitCountStillWins()
    {
        Assert.Equal(2, S("--fly", "--plane=player_bhawk,player_fury").Players);
        Assert.Equal(1, S("--fly", "--plane=player_bhawk,player_fury", "--players=1").Players);
        Assert.Equal("player_bhawk", S("--fly", "--plane=player_bhawk,player_fury").PlaneName);
    }

    [Fact]
    public void ThePlayerCountIsClampedToTheRigsCapacity()
        => Assert.Equal(UI.Boards.SplitScreen.MaxPlayers, S("--fly", "--players=9").Players);

    /// <summary>Splitscreen is a flight mode: it needs planes to fly.</summary>
    [Fact]
    public void AStaticViewFallsBackToOnePlayer()
    {
        var s = S("--viewer", "--players=3");
        Assert.Equal(1, s.Players);
        Assert.Contains(s.Warnings, w => w.Message.Contains("needs flight"));
    }

    [Fact]
    public void TheDamageLabIsThePlaneLabAndIsDroppedOverAWorld()
    {
        Assert.True(S("--damage").DamageLab);
        var s = S("--damage", "--chapter=C4");
        Assert.False(s.DamageLab);
        Assert.Contains(s.Warnings, w => w.Message.Contains("--damage is the plane lab"));
    }

    /// <summary>The weapon lab fires through a real FlightController, so it needs the flight mode,
    /// not the static viewer, unlike --weapon-test, which stays a cheap plane-only probe.</summary>
    [Theory]
    [InlineData("--weapon-lab")]
    [InlineData("--weapon-mount=wep_06")]
    [InlineData("--weapon-fire")]
    public void TheWeaponLabRoutesToFlightNotTheViewer(string flag)
    {
        Assert.Equal(SessionMode.Fly, S(flag).Mode);
    }

    [Fact]
    public void WeaponTestStillRoutesToTheViewer()
    {
        Assert.Equal(SessionMode.Viewer, S("--weapon-test").Mode);
    }

    [Fact]
    public void WeaponLabAgainstAnExplicitViewerReportsTheContradiction()
    {
        var s = S("--weapon-lab", "--viewer");
        Assert.Equal(SessionMode.Viewer, s.Mode);
        Assert.Contains(s.Warnings, w => w.Category == "core" && w.Message.Contains("need flight"));
    }

    // ---- Tools that only exist in some modes ---------------------------------------------------

    /// <summary>The numpad views orbit a FLYING plane; every other mode places its camera with
    /// `--pos`/`--direction` instead.</summary>
    [Fact]
    public void TheNumpadViewsAreDroppedOutsideFlight()
    {
        Assert.Equal(2, S("--fly", "--view=2").View);
        var s = S("--viewer", "--view=2");
        Assert.Equal(0, s.View);
        Assert.Contains(s.Warnings, w => w.Category == "core" && w.Message.Contains("flight camera"));
    }

    [Fact]
    public void AViewDigitWithNoPerspectiveFallsBackToTheChaseCamera()
    {
        var s = S("--fly", "--view=5");
        Assert.Equal(0, s.View);
        Assert.Contains(s.Warnings, w => w.Message.Contains("not a numpad view"));
    }

    /// <summary>`--view=` also spells the three SELECTED views (A1), which are a different concept
    /// from the held numpad digits and must not be read as one: naming a mode leaves
    /// <c>View</c> at the chase camera and vice versa.</summary>
    [Fact]
    public void TheViewFlagAlsoSelectsCockpitAndNose()
    {
        var cockpit = S("--fly", "--view=cockpit");
        Assert.Equal(CSVM.Flight.Camera.PilotViewMode.Cockpit, cockpit.ViewMode);
        Assert.Equal(0, cockpit.View);
        Assert.Empty(cockpit.Warnings);
        Assert.Equal(CSVM.Flight.Camera.PilotViewMode.Nose, S("--fly", "--view=nose").ViewMode);
        Assert.Equal(CSVM.Flight.Camera.PilotViewMode.Chase, S("--fly", "--view=2").ViewMode);
    }

    /// <summary>Same rule as the numpad digits: the first-person modes sit on a flown aircraft's
    /// camera, so another mode is told rather than silently given a view it has no plane for.</summary>
    [Fact]
    public void TheSelectedViewModesAreDroppedOutsideFlight()
    {
        var s = S("--viewer", "--view=cockpit");
        Assert.Equal(CSVM.Flight.Camera.PilotViewMode.Chase, s.ViewMode);
        Assert.Contains(s.Warnings, w => w.Category == "core" && w.Message.Contains("flight camera"));
    }

    /// <summary>The shared selection lives in the two world-observation modes: the viewer's LMB is
    /// already the orbit drag, and flight has no cursor.</summary>
    [Theory]
    [InlineData("--debug-select")]
    [InlineData("--debug-nodelab")]
    [InlineData("--debug-damage")]
    public void TheSelectionToolsExistOnlyInFreecamAndTheAnimLab(string tool)
    {
        Assert.NotNull(Value(S("--freecam", tool), tool));
        Assert.NotNull(Value(S("--anim-lab", tool), tool));
        Assert.Null(Value(S("--viewer", tool), tool));
        Assert.Null(Value(S("--fly", tool), tool));

        static string? Value(SessionSpec s, string tool) => tool switch
        {
            "--debug-select" => s.DebugSelect,
            "--debug-nodelab" => s.DebugNodeLab,
            _ => s.DebugDamage,
        };
    }

    /// <summary>A lab's spec value is filtered through that lab's own token grammar, so an unknown
    /// token is dropped as data rather than reaching the lab.</summary>
    [Fact]
    public void ALabSpecKeepsOnlyTheTokensItsLabUnderstands()
    {
        var s = S("--freecam", "--debug-nodelab=deps,bogus,dest");
        Assert.Equal("deps,dest", s.DebugNodeLab);
        Assert.Contains(s.Warnings, w => w.Category == "ui" && w.Message.Contains("bogus"));
    }

    // ---- Placement -----------------------------------------------------------------------------

    [Fact]
    public void ThePlacementPairMovesThePlaneInFlight()
    {
        var s = S("--fly", "--pos=10,20,30", "--direction=0,0,-1");
        Assert.Equal(new Vector3(10, 20, 30), s.SpawnAt);
        Assert.Equal(new Vector3(0, 0, -1), s.SpawnDir);
        Assert.Null(s.CamPos);
        Assert.Null(s.CamDir);
    }

    [Fact]
    public void ThePlacementPairMovesTheCameraEverywhereElse()
    {
        var s = S("--freecam", "--pos=10,20,30", "--direction=0,0,-1");
        Assert.Equal(new Vector3(10, 20, 30), s.CamPos);
        Assert.Equal(new Vector3(0, 0, -1), s.CamDir);
        Assert.Null(s.SpawnAt);
        Assert.Null(s.SpawnDir);
    }

    /// <summary>`--lookat` names a POINT and `--direction` a VECTOR; the conversion is one-way and
    /// flight-only, because the orbit view PIVOTS on the point and no direction can express that.</summary>
    [Fact]
    public void ALookAtPointBecomesADirectionInFlightOnly()
    {
        var flying = S("--fly", "--pos=0,0,0", "--lookat=0,0,-5");
        Assert.Equal(new Vector3(0, 0, -1), flying.SpawnDir);

        var looking = S("--viewer", "--pos=0,0,0", "--lookat=0,0,-5");
        Assert.Null(looking.CamDir);
        Assert.Equal(new Vector3(0, 0, -5), looking.LookAt);
    }

    [Fact]
    public void ADirectionIsNormalisedAndADegenerateOneIsDropped()
    {
        Assert.Equal(new Vector3(1, 0, 0), S("--fly", "--pos=0,0,0", "--direction=7,0,0").SpawnDir);
        Assert.Null(S("--fly", "--pos=0,0,0", "--direction=0,0,0").Direction);
    }

    /// <summary>The superseded spellings keep their old per-mode reach: `--campos` places a camera
    /// and never the plane, in any mode.</summary>
    [Fact]
    public void CamposNeverPlacesThePlane()
    {
        var s = S("--fly", "--campos=1,2,3");
        Assert.Equal(new Vector3(1, 2, 3), s.CamPos);
        Assert.Null(s.SpawnAt);
        Assert.Contains(s.Deprecated, d => d.Old == "--campos" && d.New == "--pos");
    }

    [Fact]
    public void ADeprecatedSpellingIsReportedOnceWithItsReplacement()
    {
        var s = S("--spawn-at=1,2,3", "--spawn-at=4,5,6", "--spawn-dir=0,0,-1");
        Assert.Equal(new[] { "--spawn-at", "--spawn-dir" }, s.Deprecated.Select(d => d.Old));
    }

    [Fact]
    public void AmmoSetsTheCap()
    {
        var s = S("--ammo=3");
        Assert.Equal(3, s.AmmoCap);
        Assert.False(s.InfiniteAmmo);
    }

    /// <summary>Mutually exclusive with `--infinite-ammo`: whichever comes last on the command line
    /// wins, and the loser is logged.</summary>
    [Fact]
    public void AmmoAndInfiniteAmmoAreMutuallyExclusiveByOrder()
    {
        var ammoWins = S("--infinite-ammo", "--ammo=3");
        Assert.Equal(3, ammoWins.AmmoCap);
        Assert.False(ammoWins.InfiniteAmmo);
        Assert.Contains(ammoWins.Warnings, w => w.Message.Contains("--ammo="));

        var infiniteWins = S("--ammo=3", "--infinite-ammo");
        Assert.Null(infiniteWins.AmmoCap);
        Assert.True(infiniteWins.InfiniteAmmo);
        Assert.Contains(infiniteWins.Warnings, w => w.Message.Contains("--infinite-ammo"));
    }

    /// <summary>A nose direction with nothing to place it on is a silently ignored argument, so it
    /// says so.</summary>
    [Fact]
    public void ADirectionWithoutAPositionIsCalledOutInFlight()
    {
        var s = S("--fly", "--spawn-dir=0,0,-1");
        Assert.Contains(s.Warnings, w => w.Message.Contains("--direction ignored"));
    }

    /// <summary>The empty stage has no mission spawn list, so the subject starts over the grid
    /// origin, through the same fields `--pos` resolves into, so an explicit placement still wins.</summary>
    [Fact]
    public void TheEmptyStageDefaultsThePlacementItHasNoSpawnListFor()
    {
        Assert.Equal(new Vector3(0f, Mech3.EmptyStage.SpawnAltitude, 0f), S("--stage=empty").SpawnAt);
        Assert.Equal(Mech3.EmptyStage.CameraPos, S("--stage=empty", "--freecam").CamPos);
        Assert.Equal(new Vector3(1, 2, 3), S("--stage=empty", "--pos=1,2,3").SpawnAt);
    }

    // ---- Audio ---------------------------------------------------------------------------------

    /// <summary>Null when unset is the whole point: it is what leaves room for the `audio.volume`
    /// config key underneath, so an absent flag is not the same as `--volume=1`.</summary>
    [Fact]
    public void TheVolumeIsAnOverrideValueNullWhenUnset()
    {
        Assert.Null(S("--fly").Volume);
        Assert.Equal(0f, S("--volume=0").Volume);
        Assert.Equal(0.5f, S("--volume=0.5").Volume);
        Assert.Equal(1f, S("--volume=1").Volume);
    }

    /// <summary>Written with a decimal comma on a German machine, `0,5` parses as neither 0.5 nor a
    /// number, the invariant culture is what keeps one command line meaning one thing everywhere.</summary>
    [Fact]
    public void TheVolumeIsReadInTheInvariantCulture()
    {
        Assert.Equal(0.5f, S("--volume=0.5").Volume);
        Assert.Null(S("--volume=0,5").Volume);
    }

    [Fact]
    public void AVolumeOutsideZeroToOneIsClampedLoudly()
    {
        var loud = S("--volume=4");
        Assert.Equal(1f, loud.Volume);
        Assert.Contains(loud.Warnings, w => w.Category == "core" && w.Message.Contains("outside 0-1"));

        var negative = S("--volume=-2");
        Assert.Equal(0f, negative.Volume);
        Assert.Contains(negative.Warnings, w => w.Message.Contains("outside 0-1"));
    }

    /// <summary>A typo'd volume must not take the launch down: the run is still perfectly usable at
    /// the default gain, so it says so and carries on.</summary>
    [Fact]
    public void AnUnreadableVolumeIsIgnoredLoudlyRatherThanThrowing()
    {
        var s = S("--fly", "--volume=loud");
        Assert.Null(s.Volume);
        Assert.Equal(SessionMode.Fly, s.Mode);
        Assert.Contains(s.Warnings, w => w.Category == "core" && w.Message.Contains("not a number"));
    }

    /// <summary>The two audio flags are not variants of each other: `--mute` never builds the audio
    /// subsystem, so a gain has nothing to attenuate. Neither is cleared, both spell silence, but
    /// the combination loses the sounds the volume flag exists to keep audible in the log.</summary>
    [Fact]
    public void MuteAndVolumeTogetherAreCalledOut()
    {
        var s = S("--mute", "--volume=0");
        Assert.True(s.Mute);
        Assert.Equal(0f, s.Volume);
        Assert.Contains(s.Warnings, w => w.Category == "core" && w.Message.Contains("--mute skips loading audio"));

        Assert.Empty(S("--volume=0").Warnings);
        Assert.Empty(S("--mute").Warnings);
    }

    [Fact]
    public void TheSoundRangeScaleDefaultsToTheShippedFactor()
    {
        // The one assertion of the shipped departure's value: a session nobody passes the flag to
        // plays every positional voice at 2.5 times its authored radii.
        Assert.Equal(2.5f, SoundFalloff.ShippedRangeScale);
        Assert.Equal(SoundFalloff.ShippedRangeScale, S("--fly").SoundRangeScale);
        Assert.Equal(1f, S("--sound-range-scale=1").SoundRangeScale);
        Assert.Equal(2f, S("--sound-range-scale=2").SoundRangeScale);
        Assert.Equal(0.5f, S("--sound-range-scale=0.5").SoundRangeScale);
        Assert.Empty(S("--sound-range-scale=4").Warnings);
    }

    [Fact]
    public void AnUnusableSoundRangeScaleIsIgnoredLoudly()
    {
        foreach (string bad in new[] { "0", "-1", "far", "2,5" })
        {
            var s = S($"--sound-range-scale={bad}");
            Assert.Equal(SoundFalloff.ShippedRangeScale, s.SoundRangeScale);
            Assert.Contains(s.Warnings, w => w.Category == "core" && w.Message.Contains("not a positive number"));
        }
    }

    [Fact]
    public void TheCloudJitterDefaultsToTheShippedOffset()
    {
        Assert.Equal(FogVolumeClutter.ShippedJitter, S("--fly").CloudJitter);
        Assert.Equal(40f, S("--cloud-jitter=40").CloudJitter);
        Assert.Equal(0f, S("--cloud-jitter=0").CloudJitter);
        Assert.Empty(S("--cloud-jitter=12.5").Warnings);
    }

    [Fact]
    public void AnUnusableCloudJitterIsIgnoredLoudly()
    {
        foreach (string bad in new[] { "-1", "wide", "2,5", "NaN" })
        {
            var s = S($"--cloud-jitter={bad}");
            Assert.Equal(FogVolumeClutter.ShippedJitter, s.CloudJitter);
            Assert.Contains(s.Warnings, w => w.Category == "core" && w.Message.Contains("--cloud-jitter"));
        }
    }

    // ---- Parse conventions ---------------------------------------------------------------------

    [Fact]
    public void TheLastOccurrenceOfAFlagWins()
    {
        Assert.Equal("C5", S("--chapter=C1", "--chapter=C5").Chapter);
        Assert.Equal(7ul, S("--seed=1", "--seed=7").Seed);
        Assert.Equal(0f, S("--volume=1", "--volume=0").Volume);
    }

    [Fact]
    public void AnUnrecognisedArgIsIgnoredSilently()
    {
        var s = S("--fly", "--no-such-flag", "nonsense");
        Assert.Equal(SessionMode.Fly, s.Mode);
        Assert.Empty(s.Warnings);
    }

    /// <summary>Path flags are override VALUES, null when unset, the default arithmetic belongs to
    /// the caller, which is why nothing here builds a path.</summary>
    [Fact]
    public void PathFlagsAreOverrideValuesOnly()
    {
        var bare = S("--fly");
        Assert.Null(bare.Gamez);
        Assert.Null(bare.Textures);
        Assert.Null(bare.DataRoot);

        var s = S("--gamez=g.zip", "--textures=t.zip", "--data-root=/somewhere");
        Assert.Equal("g.zip", s.Gamez);
        Assert.Equal("t.zip", s.Textures);
        Assert.Equal("/somewhere", s.DataRoot);
    }

    /// <summary>`--ia=` is a path VALUE only, loading the file into an `InstantActionDef` is the
    /// runtime's job, which is what keeps this type free of file
    /// I/O.</summary>
    [Fact]
    public void IaIsAPathValueOnly()
    {
        Assert.Null(S("--fly").IaPath);
        Assert.Equal("my-mission.json", S("--ia=my-mission.json").IaPath);
    }

    /// <summary>`--campaign=` is a plain value pair, parsed but not resolved into a store or a
    /// mission, and, with no other mode vote, still resolves the session to Fly.</summary>
    [Fact]
    public void CampaignIsAProfileAndSeqValueOnly()
    {
        Assert.Null(S("--fly").CampaignProfile);
        Assert.Null(S("--fly").CampaignMissionSeq);

        var s = S("--campaign=Zachary:3");
        Assert.Equal("Zachary", s.CampaignProfile);
        Assert.Equal(3, s.CampaignMissionSeq);
        Assert.Equal(SessionMode.Fly, s.Mode);
        Assert.Empty(s.Warnings);
    }

    /// <summary>A missing or unparseable ":seq" suffix keeps the profile name and warns, rather
    /// than dropping the whole flag.</summary>
    [Fact]
    public void CampaignWithNoSeq_KeepsTheProfileNameAndWarns()
    {
        var s = S("--campaign=Zachary");
        Assert.Equal("Zachary", s.CampaignProfile);
        Assert.Null(s.CampaignMissionSeq);
        Assert.Single(s.Warnings);
    }

    /// <summary>`--no-crash-loss` is off by default, so the game rule is what an unflagged
    /// campaign launch gets.</summary>
    [Fact]
    public void NoCrashLossIsOffUnlessAskedFor()
    {
        Assert.False(S("--campaign=Zachary:3").NoCrashLoss);
        var s = S("--campaign=Zachary:3", "--no-crash-loss");
        Assert.True(s.NoCrashLoss);
        Assert.Empty(s.Warnings);
    }

    /// <summary>Globals are recorded, never applied, that is what keeps the type reachable from
    /// here, with no engine under it.</summary>
    [Fact]
    public void TheSpecRecordsGlobalsRatherThanApplyingThem()
    {
        var s = S("--tex-override=sky=ff00ff", "--log=anim:debug", "--no-pads");
        Assert.Equal(new[] { "sky=ff00ff" }, s.TexOverrides);
        Assert.Equal(new[] { "anim:debug" }, s.LogSpecs);
        Assert.True(s.NoPads);
    }

    /// <summary>`--debug-anim`'s implied "anim:debug,sound:debug" is the caller's, not a parsed
    /// spec, the flag records itself and nothing more.</summary>
    [Fact]
    public void DebugAnimDoesNotSynthesiseALogSpec()
    {
        var s = S("--debug-anim");
        Assert.True(s.DebugAnim);
        Assert.Empty(s.LogSpecs);
    }

    /// <summary>`--debug-net` records itself and leaves the log spec and every other debug flag
    /// alone; off unless given.</summary>
    [Fact]
    public void DebugNetIsItsOwnFlag()
    {
        Assert.False(S().DebugNet);
        var s = S("--debug-net");
        Assert.True(s.DebugNet);
        Assert.False(s.DebugAnim);
        Assert.Empty(s.LogSpecs);
    }

    private static SessionSpec S(params string[] args) => SessionSpec.Parse(args);
}
