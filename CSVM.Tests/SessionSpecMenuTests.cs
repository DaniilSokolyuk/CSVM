using System;
using System.Collections.Generic;
using CSVM;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// What a launchscreen launch resolves to. The launchscreen is the one path with no automated
/// coverage anywhere else, the pixel goldens never open it and the resolution baseline drives the
/// CLI only, so these are the only facts standing behind it.
/// </summary>
public class SessionSpecMenuTests
{
    [Fact]
    public void AMenuLaunchIsAlwaysFlightOverAChapterWorld()
    {
        var spec = Menu(Cli(), "C4", MenuMode.Free, "player_fury");

        Assert.Equal(SessionMode.Fly, spec.Mode);
        Assert.True(spec.Fly);
        Assert.True(spec.WorldMode);
        Assert.False(spec.Viewer);
        Assert.Equal("C4", spec.Chapter);
        Assert.Equal("player_fury", spec.PlaneName);
        Assert.Equal(1, spec.Players);
    }

    /// <summary>A bare launch resolves to the menu, and the menu's pick turns it into flight,
    /// the CLI spec itself is never flight, so this is the whole reason the factory exists.</summary>
    [Fact]
    public void ABareLaunchShowsTheMenuAndTheMenuPickFliesIt()
    {
        var cli = Cli();
        Assert.Equal(SessionMode.Menu, cli.Mode);
        Assert.True(cli.ShowsMenu);

        Assert.Equal(SessionMode.Fly, Menu(cli, "C1", MenuMode.Free, "player_bhawk").Mode);
    }

    /// <summary>The menu launches flight even when the command line asked for a static view: the
    /// pick is an answer, not another vote. `--viewer --menu` is the launch that proves it.</summary>
    [Fact]
    public void TheMenuBeatsAViewerCommandLine()
    {
        var cli = Cli("--viewer", "--menu", "--plane=player_kestrel");
        Assert.Equal(SessionMode.Viewer, cli.Mode);

        var spec = Menu(cli, "C2", MenuMode.Free, "player_fury");
        Assert.Equal(SessionMode.Fly, spec.Mode);
        Assert.False(spec.Viewer);
        Assert.Equal("player_fury", spec.PlaneName);
    }

    [Fact]
    public void OnePlanePerPlayerStatesThePlayerCount()
    {
        var spec = Menu(Cli(), "C1", MenuMode.Free, "player_bhawk", "player_fury", "player_kestrel");

        Assert.Equal(3, spec.Players);
        Assert.Equal(new[] { "player_bhawk", "player_fury", "player_kestrel" }, spec.PlaneNames);
        // Every single-plane path reads PlaneName, which is the first pick.
        Assert.Equal("player_bhawk", spec.PlaneName);
    }

    [Fact]
    public void AStuntPickTakesTheStuntSpawnListAndAFreeFlightPickTakesTheDefault()
    {
        Assert.Equal("stunt_flying", Menu(Cli(), "C1", MenuMode.Stunt, "player_bhawk").Scenario);
        Assert.True(Menu(Cli(), "C1", MenuMode.Stunt, "player_bhawk").Stunt);

        Assert.Equal("zeppelin_run", Menu(Cli(), "C1", MenuMode.Free, "player_bhawk").Scenario);
        Assert.False(Menu(Cli(), "C1", MenuMode.Free, "player_bhawk").Stunt);
    }

    /// <summary>The third mode: a Dogfight pick takes the dogfight_ace spawn list and sets
    /// Versus, not Stunt, the two modifiers are mutually exclusive from the menu, same as from
    /// the CLI's fixed `--vs`/`--stunt` precedence.</summary>
    [Fact]
    public void AVersusPickTakesTheDogfightAceSpawnList()
    {
        var spec = Menu(Cli(), "C1", MenuMode.Versus, "player_bhawk", "player_fury");

        Assert.Equal("dogfight_ace", spec.Scenario);
        Assert.True(spec.Versus);
        Assert.False(spec.Stunt);
        Assert.Equal(SessionMode.Fly, spec.Mode);
        Assert.Equal(2, spec.Players);
    }

    /// <summary>Dogfight's two match rules: the shipped defaults stand when no screen offers them,
    /// a screen's choice reaches the spec, and a spelled-out flag beats that choice, which is what
    /// keeps a scripted `--vs --vs-kills=` run saying what it asks for whatever a menu was left
    /// at.</summary>
    [Fact]
    public void TheMenusMatchRulesReachTheSpecAndTheFlagsStillBeatThem()
    {
        var bare = Cli();
        Assert.False(bare.VsKillsExplicit);
        Assert.False(bare.VsTimeExplicit);

        var untouched = Menu(bare, "C1", MenuMode.Versus, "player_bhawk", "player_fury");
        Assert.Equal(5, untouched.VsKills);
        Assert.Equal(5, untouched.VsTimeMinutes);

        var chosen = SessionSpec.FromMenu(bare, "C1", new[] { "player_bhawk", "player_fury" },
            MenuMode.Versus, vsKills: 12, vsTimeMinutes: 0);
        Assert.Equal(12, chosen.VsKills);
        Assert.Equal(0, chosen.VsTimeMinutes);

        var pinned = Cli("--vs-kills=3", "--vs-time=9");
        Assert.True(pinned.VsKillsExplicit);
        Assert.True(pinned.VsTimeExplicit);
        var beaten = SessionSpec.FromMenu(pinned, "C1", new[] { "player_bhawk", "player_fury" },
            MenuMode.Versus, vsKills: 12, vsTimeMinutes: 0);
        Assert.Equal(3, beaten.VsKills);
        Assert.Equal(9, beaten.VsTimeMinutes);

        // One flag alone: the other rule is still the screen's.
        var half = SessionSpec.FromMenu(Cli("--vs-time=9"), "C1", new[] { "player_bhawk", "player_fury" },
            MenuMode.Versus, vsKills: 12, vsTimeMinutes: 0);
        Assert.Equal(12, half.VsKills);
        Assert.Equal(9, half.VsTimeMinutes);
    }

    /// <summary>The lobby's lives rule reaches the spec the same way, and the two flags beat it.</summary>
    [Fact]
    public void TheLobbysLivesRuleReachesTheSpecAndTheFlagsStillBeatIt()
    {
        var bare = Cli();
        Assert.Equal(0, bare.VsLives);
        Assert.True(bare.VsAutoRespawn);

        var chosen = SessionSpec.FromMenu(bare, "C1", new[] { "player_bhawk" }, MenuMode.Versus, vsLives: 2, vsAutoRespawn: false);
        Assert.Equal(2, chosen.VsLives);
        Assert.False(chosen.VsAutoRespawn);

        // ABLE-TO-FAIL CONTROL: spelled-out flags win over the lobby's choice.
        var pinned = Cli("--vs-lives=4", "--vs-no-respawn");
        Assert.True(pinned.VsLivesExplicit);
        Assert.True(pinned.VsAutoRespawnExplicit);
        var beaten = SessionSpec.FromMenu(pinned, "C1", new[] { "player_bhawk" }, MenuMode.Versus, vsLives: 2, vsAutoRespawn: true);
        Assert.Equal(4, beaten.VsLives);
        Assert.False(beaten.VsAutoRespawn);
    }

    /// <summary>A tester who pinned a scenario alongside a bare launch keeps it, in all three
    /// modes, the re-derivation is a default, not an override.</summary>
    [Fact]
    public void AnExplicitScenarioSurvivesEveryMenuMode()
    {
        var cli = Cli("--scenario=hangar_run");

        Assert.Equal("hangar_run", Menu(cli, "C1", MenuMode.Free, "player_bhawk").Scenario);
        Assert.Equal("hangar_run", Menu(cli, "C1", MenuMode.Stunt, "player_bhawk").Scenario);
        Assert.Equal("hangar_run", Menu(cli, "C1", MenuMode.Versus, "player_bhawk").Scenario);
    }

    /// <summary>Nothing a previous launch settled reaches the next one. Every field the factory
    /// writes is asserted against the SECOND pick, not the first, this is the property that used
    /// to rest on three hand-written patches in the caller. Each expected value differs from BOTH
    /// the first pick and the parse default, so a dropped write cannot pass by landing on one.</summary>
    [Fact]
    public void ASecondLaunchKeepsNothingFromTheFirst()
    {
        var cli = Cli();
        var first = Menu(cli, "C4", MenuMode.Stunt, "player_fury", "player_bhawk");
        Assert.Equal("stunt_flying", first.Scenario);
        Assert.Equal(2, first.Players);

        var second = SessionSpec.FromMenu(cli, "C2", new[] { "player_kestrel" }, MenuMode.Free);
        Assert.Equal("C2", second.Chapter);
        Assert.Equal("player_kestrel", second.PlaneName);
        Assert.Equal(new[] { "player_kestrel" }, second.PlaneNames);
        Assert.Equal(1, second.Players);
        Assert.False(second.Stunt);
        Assert.Equal("zeppelin_run", second.Scenario);
    }

    /// <summary>Deriving from the pristine spec and patching the live one agree on every field the
    /// factory writes, which is what makes the change from one to the other behaviour-neutral. The
    /// second launch is the case that could have differed: it is the first one whose base carries a
    /// previous pick.</summary>
    [Fact]
    public void DerivingFreshAgreesWithPatchingTheLiveSpec()
    {
        var cli = Cli("--menu");
        var first = Menu(cli, "C4", MenuMode.Stunt, "player_fury", "player_bhawk");

        var fresh = SessionSpec.FromMenu(cli, "C2", new[] { "player_kestrel" }, MenuMode.Free);
        var patched = SessionSpec.FromMenu(first, "C2", new[] { "player_kestrel" }, MenuMode.Free);

        Assert.Equal(fresh.Chapter, patched.Chapter);
        Assert.Equal(fresh.PlaneName, patched.PlaneName);
        Assert.Equal(fresh.PlaneNames, patched.PlaneNames);
        Assert.Equal(fresh.Players, patched.Players);
        Assert.Equal(fresh.Stunt, patched.Stunt);
        Assert.Equal(fresh.Versus, patched.Versus);
        Assert.Equal(fresh.Mode, patched.Mode);
        Assert.Equal(fresh.WorldMode, patched.WorldMode);
        Assert.Equal(fresh.Scenario, patched.Scenario);
    }

    /// <summary>The command line still settles everything the pick does not name, the pick is a
    /// chapter, some planes and a mode, not a whole session.</summary>
    [Fact]
    public void EverythingThePickDoesNotNameStillComesFromTheCommandLine()
    {
        var cli = Cli("--menu", "--mission=M02", "--seed=7", "--mute", "--volume=0", "--no-fog", "--anim-lod=1");
        var spec = Menu(cli, "C3", MenuMode.Free, "player_bhawk");

        Assert.Equal("M02", spec.Mission);
        Assert.Equal(7ul, spec.Seed);
        Assert.True(spec.Mute);
        Assert.Equal(0f, spec.Volume);
        Assert.True(spec.NoFog);
        Assert.Equal(1, spec.AnimLod);
    }

    /// <summary>Splitscreen is capped by the rig, not by the pick.</summary>
    [Fact]
    public void ThePlayerCountIsClampedToTheRigsCapacity()
    {
        var many = new List<string>();
        for (int i = 0; i < 9; i++)
        {
            many.Add("player_bhawk");
        }
        Assert.Equal(UI.SplitScreen.MaxPlayers, SessionSpec.FromMenu(Cli(), "C1", many, MenuMode.Free).Players);
    }

    // ---- The third mode + its menu-only launch gate ---------------------------------------------

    /// <summary>The Mode screen's three rows map 1:1 onto <see cref="MenuMode"/>'s ordinals
    /// (`LaunchMenu.Modes[(int)mode]`), in this order, the fact the row-to-enum mapping in
    /// <c>LaunchMenu</c> (an engine-bound CanvasLayer, unreachable from here) depends on.</summary>
    [Fact]
    public void TheThreeMenuModesExistInRowOrder()
    {
        var modes = (MenuMode[])Enum.GetValues(typeof(MenuMode));
        Assert.Equal(new[] { MenuMode.Free, MenuMode.Stunt, MenuMode.Versus }, modes);
    }

    /// <summary>The launch-gate rule itself (<see cref="UI.LaunchMenu.CanLaunch(MenuMode, bool, int)"/>)
    /// is a pure static function, reachable from here with no menu instance behind it: Free Flight
    /// and Stunt Flying launch as soon as everyone joined is locked, solo included; Dogfight
    /// additionally needs 2 joined players. Nothing launches before everyone is locked, whatever
    /// the mode or the count.</summary>
    [Theory]
    [InlineData(MenuMode.Free, 1, true)]
    [InlineData(MenuMode.Stunt, 1, true)]
    [InlineData(MenuMode.Versus, 1, false)]
    [InlineData(MenuMode.Versus, 2, true)]
    [InlineData(MenuMode.Versus, 4, true)]
    public void TheLaunchGateLocksDogfightBelowTwoPlayers(MenuMode mode, int joinedCount, bool expected)
        => Assert.Equal(expected, UI.LaunchMenu.CanLaunch(mode, allLocked: true, joinedCount));

    [Theory]
    [InlineData(MenuMode.Free)]
    [InlineData(MenuMode.Versus)]
    public void TheLaunchGateNeverOpensBeforeEveryoneIsLocked(MenuMode mode)
        => Assert.False(UI.LaunchMenu.CanLaunch(mode, allLocked: false, joinedCount: 4));

    /// <summary>The Map screen's roster rule (<see cref="UI.LaunchMenu.ChapterCodesFor"/>): Stunt
    /// Flying hides C1C and C2B, they ship no <c>dzones</c>, so a stunt run there would be an
    /// empty free flight (the original hides "the clouds" from stunt for the same reason), while
    /// every other mode offers all eight chapters.</summary>
    [Fact]
    public void StuntFlyingHidesTheChaptersWithoutDangerZones()
    {
        Assert.Equal(
            new[] { "C1", "C1B", "C2", "C3", "C4", "C5" },
            UI.LaunchMenu.ChapterCodesFor(MenuMode.Stunt));
        Assert.Equal(8, UI.LaunchMenu.ChapterCodesFor(MenuMode.Free).Length);
        Assert.Equal(8, UI.LaunchMenu.ChapterCodesFor(MenuMode.Versus).Length);
    }

    // ---- Instant Action wizard steps 1-2 --------------------------

    /// <summary>The Environment screen's roster, in the decoded dropdown order, seven rows,
    /// C1C never among them (the chapter Instant Action omits). Matches
    /// docs/formats/instant-action.md's "Environment → chapter" table exactly.</summary>
    [Fact]
    public void TheEnvironmentScreenOffersTheDecodedSevenInOrder()
    {
        Assert.Equal(
            new[] { "C1", "C2B", "C3", "C5", "C1B", "C4", "C2" },
            UI.LaunchMenu.EnvironmentCodes());
    }

    /// <summary>The MissionType screen's roster for one environment: all four mission types except
    /// on "the clouds" (C2B), whose own `disallow_missions` bars Stunt Flying, the same rule
    /// `ChapterCodesFor`/`StuntFlyingHidesTheChaptersWithoutDangerZones` already exercises from the
    /// plain Chapter screen's side, read here from the Environment screen's.</summary>
    [Fact]
    public void MissionTypeHidesStuntFlyingOnlyWhereTheChapterBarsIt()
    {
        Assert.Equal(
            new[] { "dogfight_ace", "dogfight_squadron", "stunt_flying", "zeppelin_run" },
            UI.LaunchMenu.MissionTypeKeysFor("C1"));
        Assert.Equal(
            new[] { "dogfight_ace", "dogfight_squadron", "zeppelin_run" },
            UI.LaunchMenu.MissionTypeKeysFor("C2B"));
    }

    /// <summary>Every one of the seven Instant Action environments offers at least the three
    /// mission types no chapter's `disallow_missions` ever bars, ace, squadron and zeppelin are
    /// never filtered, only stunt is.</summary>
    [Fact]
    public void EveryEnvironmentOffersAceSquadronAndZeppelin()
    {
        foreach (string code in UI.LaunchMenu.EnvironmentCodes())
        {
            var keys = UI.LaunchMenu.MissionTypeKeysFor(code);
            Assert.Contains("dogfight_ace", keys);
            Assert.Contains("dogfight_squadron", keys);
            Assert.Contains("zeppelin_run", keys);
        }
    }

    // ---- Instant Action wizard steps 3-5, one build path -----------

    /// <summary>When the wizard hands over a built <c>InstantActionDef</c>, IT, not the picked
    /// <see cref="MenuMode"/>, decides <see cref="SessionSpec.Scenario"/>/<see cref="SessionSpec.Stunt"/>:
    /// the wizard already knows exactly which of the four mission types was picked, so this
    /// replaces H15's own approximation (mission type mapped onto whichever of Free/Stunt it most
    /// resembled) now that a real build path exists.</summary>
    [Theory]
    [InlineData("dogfight_ace", false)]
    [InlineData("dogfight_squadron", false)]
    [InlineData("stunt_flying", true)]
    [InlineData("zeppelin_run", false)]
    public void AnIaDefsOwnMissionTypeDecidesScenarioAndStunt(string missionType, bool expectStunt)
    {
        var def = WizardDef(missionType);
        var spec = SessionSpec.FromMenu(Cli(), "C1", new[] { "player_bhawk" }, MenuMode.Stunt, def);

        Assert.Equal(missionType, spec.Scenario);
        Assert.Equal(expectStunt, spec.Stunt);
        Assert.False(spec.Versus);
        Assert.Same(def, spec.IaDef);
    }

    /// <summary>Every CLI launch and every Free Flight/Dogfight menu pick carries no
    /// <c>InstantActionDef</c> at all, <see cref="SessionSpec.IaDef"/> stays null, the same
    /// backward-compatible default every existing <c>FromMenu</c> call site (this file's own
    /// 4-argument calls included) already relies on.</summary>
    [Fact]
    public void IaDefIsNullOutsideInstantAction()
    {
        Assert.Null(Cli().IaDef);
        Assert.Null(Menu(Cli(), "C1", MenuMode.Free, "player_bhawk").IaDef);
        Assert.Null(Menu(Cli(), "C1", MenuMode.Versus, "player_bhawk", "player_fury").IaDef);
    }

    /// <summary>Decision 8a's own promise, checked at the seam <c>FireLaunch</c> actually calls:
    /// the flown-wingmen clamp is a Plane-screen DISPLAY concern
    /// (<c>InstantActionRuntime.FlownWingmen</c>), never a rewrite of the def, a 4-player wizard
    /// launch and its 1-player equivalent carry the exact same <c>NumWingmen</c>, because
    /// <c>FromMenu</c> never reads <paramref name="planeNodes"/>'s length into
    /// <see cref="SessionSpec.IaDef"/> at all.</summary>
    [Fact]
    public void AFourPlayerAndAOnePlayerLaunchCarryTheSameWizardDefUntouched()
    {
        var def = WizardDef("dogfight_squadron");

        var onePlayer = SessionSpec.FromMenu(Cli(), "C1", new[] { "player_bhawk" }, MenuMode.Stunt, def);
        var fourPlayers = SessionSpec.FromMenu(Cli(), "C1",
            new[] { "player_bhawk", "player_fury", "player_kestrel", "player_warhawk" }, MenuMode.Stunt, def);

        Assert.Equal(1, onePlayer.Players);
        Assert.Equal(4, fourPlayers.Players);
        Assert.Equal(def.NumWingmen, onePlayer.IaDef!.NumWingmen);
        Assert.Equal(def.NumWingmen, fourPlayers.IaDef!.NumWingmen);
        Assert.Equal(onePlayer.IaDef!.NumWingmen, fourPlayers.IaDef!.NumWingmen);
    }

    /// <summary>A menu-chosen fit rides the spec per pane, and an absent one is empty rather than
    /// null so the bind site can index without a guard of its own.</summary>
    [Fact]
    public void AMenuLaunchCarriesOneFitPerPane()
    {
        var mine = new Flight.LoadoutChoice();
        mine.SetPylon(1, "wep_14");
        var spec = SessionSpec.FromMenu(Cli(), "C4", new[] { "player_fury", "player_bhawk" },
            MenuMode.Free, iaDef: null, loadouts: new Flight.LoadoutChoice?[] { mine, null });

        Assert.Equal(2, spec.MenuLoadouts.Count);
        Assert.Equal("wep_14", spec.MenuLoadouts[0]!.PylonFor(1));
        Assert.Null(spec.MenuLoadouts[1]);
        Assert.Empty(Menu(Cli(), "C4", MenuMode.Free, "player_fury").MenuLoadouts);
    }

    /// <summary>A cabin launch is co-op flight over the story position, at the number of seats that
    /// joined. The count is the one <c>LaunchCampaign</c> takes off its pad list, so a spec that
    /// says two seats is a spec whose caller had two pad bindings to hand over.</summary>
    [Fact]
    public void ACampaignLaunchFliesOneSeatPerJoinedPlayer()
    {
        var solo = SessionSpec.FromCampaign(Cli(), "Rosie", 3, new[] { "player_bhawk" }, 1);
        var pair = SessionSpec.FromCampaign(Cli(), "Rosie", 3,
            new[] { "player_bhawk", "player_fury" }, 2);

        Assert.Equal(1, solo.Players);
        Assert.Equal(2, pair.Players);
        Assert.True(pair.Coop);
        Assert.Equal(SessionMode.Fly, pair.Mode);
        Assert.Equal("player_bhawk", pair.PlaneName);
    }

    /// <summary>⚠ Why a cabin launch has to hand its join-flow pad binding over rather than let the
    /// host re-derive one. The roster split is the fallback, and for the ordinary two-seat setup,
    /// one pad and the keyboard, it gives that pad to seat 1, which already has the keyboard, and
    /// seat 2 nothing. Both pilots then fly the same plane. Steam Remote Play Together makes this
    /// the NORMAL shape, since the remote pilot's controller is the host's only pad.</summary>
    [Fact]
    public void TheRosterSplitCannotStandInForTheJoinFlowsBinding()
    {
        var derived = Pads.AssignPads(2, new List<int> { 0 });

        Assert.Equal(new[] { 0 }, derived![0]); // seat 1: the pad AND the keyboard
        Assert.Empty(derived[1]);               // seat 2: nothing to fly with

        // What the join flow knows and the roster cannot: seat 2 is the one that claimed pad 0.
        var joined = new[] { Array.Empty<int>(), new[] { 0 } };
        Assert.NotEqual(joined[1], derived[1]);
    }

    // ---- Which aircraft each pane actually flies -------------------------------------------------

    /// <summary>The wizard writes PLAYER 1's pick into the def's player_plane, so that field is a
    /// label rather than an override: each pane flies the aircraft its own pilot selected. Taking
    /// it as an override is what put player 2 in player 1's aircraft.</summary>
    [Fact]
    public void AnInstantActionWizardLaunchLeavesEveryPaneItsOwnPick()
    {
        var spec = SessionSpec.FromMenu(Cli(), "C1", new[] { "player_bhawk", "player_fury" },
            MenuMode.Stunt, WizardDef("stunt_flying"));

        Assert.Null(Session.PlaneRoster.InstantActionOverride(spec, "player_bhawk"));
        Assert.Equal("player_bhawk", Session.PlaneRoster.PlaneFor(spec, 0));
        Assert.Equal("player_fury", Session.PlaneRoster.PlaneFor(spec, 1));
    }

    /// <summary>A def read off a file has no per-player pick behind it, so its one player_plane
    /// still stands for every human, the reading <c>--ia=</c> has always had.</summary>
    [Fact]
    public void AnIaFileLaunchStillPutsEveryHumanInTheDefsPlane()
    {
        var spec = Cli("--ia=mission.json", "--plane=player_bhawk,player_fury");

        Assert.Equal("player_kestrel", Session.PlaneRoster.InstantActionOverride(spec, "player_kestrel"));
    }

    /// <summary>The screenshot aid's pointer, which stands in for seat 0's on the Original
    /// presentation: two authored numbers, then a word per button held, and no pointer at all from
    /// a spec that spells fewer than two, since a half-read point would shoot the wrong
    /// widget.</summary>
    [Fact]
    public void TheDebugPointerTakesTwoAuthoredNumbersAndAWordPerHeldButton()
    {
        Assert.Null(Cli().DebugPointer);
        Assert.Equal((400f, 454f, false, false), Cli("--debug-pointer=400,454").DebugPointer);
        Assert.Equal((400f, 454.5f, true, false), Cli("--debug-pointer=400,454.5,down").DebugPointer);
        Assert.Equal((400f, 454f, false, false), Cli("--debug-pointer=400,454,up").DebugPointer);
        // The secondary button on its own, and both at once: the credits screen's hidden line is
        // held open by the right button alone, so neither word implies the other.
        Assert.Equal((320f, 320f, false, true), Cli("--debug-pointer=320,320,right").DebugPointer);
        Assert.Equal((320f, 320f, true, true), Cli("--debug-pointer=320,320,down,right").DebugPointer);
        Assert.Null(Cli("--debug-pointer=400").DebugPointer);
        Assert.Null(Cli("--debug-pointer=left,454").DebugPointer);
        Assert.Null(Cli("--debug-pointer=").DebugPointer);
    }

    private static InstantActionDef WizardDef(string missionType) => new()
    {
        MissionType = missionType,
        DisallowMissions = Array.Empty<string>(),
        PlayerPlane = "Bloodhawk",
        NumWingmen = 5,
        WingmanPlane = "Fury",
        Waves = new[] { InstantAction.EmptyWave, InstantAction.EmptyWave, InstantAction.EmptyWave, InstantAction.EmptyWave },
        CargoZeppelinNode = "vostokzep",
        PassengerZeppelinNode = "vostokzep",
        MilitaryZeppelinNode = "vostokzep",
        AceName = "Marshall Bill Redmann",
        AcePlane = "Devastator",
        AceSkill = "veteran",
        AceStats = default,
        AceAccentId = -1,
        Lives = 1,
    };

    private static SessionSpec Cli(params string[] args) => SessionSpec.Parse(args);

    private static SessionSpec Menu(SessionSpec cli, string chapter, MenuMode mode, params string[] planes)
        => SessionSpec.FromMenu(cli, chapter, planes, mode);
}
