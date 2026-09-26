using System;
using System.Linq;
using CSVM.Mech3;
using CSVM.UI.Menu;
using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The shared Instant Action feature: its decoded option sets, the setup's opening state, the
/// rules between the fields (the ace duel, the militia reset, the clouds barring stunt flying), a
/// preset applied over the fields, the launch gate and the built def and exit, and what a discard
/// drops. The launchscreen's public rosters are checked against it so the two cannot drift.
/// </summary>
public class InstantActionFeatureTests
{
    private static readonly InstantActionDef Base = InstantActionDefFor("Test Ace");

    [Fact]
    public void TheOptionSetsAreTheDecodedOnesInTheirDropdownOrders()
    {
        Assert.Equal(new[] { "C1", "C2B", "C3", "C5", "C1B", "C4", "C2" }, InstantActionFeature.Environments.Select(e => e.Code));
        Assert.Equal(new[] { "dogfight_ace", "dogfight_squadron", "stunt_flying", "zeppelin_run" }, InstantActionFeature.AllMissionTypes.Select(m => m.Key));
        Assert.Equal(11, InstantActionFeature.Airframes.Count);
        Assert.Equal("Autogyro", InstantActionFeature.Airframes[0].Name);
        Assert.Equal("player_pfighter", InstantActionFeature.Airframes[5].Node);
        Assert.Equal(13, InstantActionFeature.Militias.Count);
        Assert.Equal(11, InstantActionFeature.AircraftFor("Fortune Hunter").Count);
        Assert.Equal(new[] { "Hellhound", "Warhawk" }, InstantActionFeature.AircraftFor("Sacred Trust"));
        Assert.Equal(new[] { "novice", "veteran", "ace" }, InstantActionFeature.Skills);
        Assert.Equal(19, InstantActionFeature.Presets.Count);
        Assert.Throws<ArgumentException>(() => InstantActionFeature.AircraftFor("Not A Militia"));
    }

    [Fact]
    public void TheLaunchscreensRostersReadOffTheFeature()
    {
        Assert.Equal(InstantActionFeature.Environments.Select(e => e.Code), LaunchMenu.EnvironmentCodes());
        Assert.Equal(InstantActionFeature.Environments.Select(e => e.Name), LaunchMenu.EnvironmentNames());
        Assert.Equal(InstantActionFeature.Airframes.Select(a => a.Name), LaunchMenu.PlaneNames());
        Assert.Equal(InstantActionFeature.Militias.Select(m => m.Name), LaunchMenu.MilitiaNames());
        Assert.Equal(InstantActionFeature.Skills, LaunchMenu.SkillKeys());
        Assert.Equal(InstantActionFeature.MissionTypesFor("C2B").Select(m => m.Key), LaunchMenu.MissionTypeKeysFor("C2B"));
        Assert.Equal(InstantActionFeature.WaveFor(3, 1, 0, 2), LaunchMenu.WaveFor(3, 1, 0, 2));
    }

    [Fact]
    public void TheCloudsBarStuntFlyingBothWays()
    {
        Assert.Equal(3, InstantActionFeature.MissionTypesFor("C2B").Count);
        Assert.DoesNotContain(InstantActionFeature.MissionTypesFor("C2B"), m => m.Key == InstantActionFeature.StuntKey);
        Assert.Equal(4, InstantActionFeature.MissionTypesFor("C4").Count);
        Assert.False(InstantActionFeature.EnvironmentAllowed(1, InstantActionFeature.StuntKey));
        Assert.True(InstantActionFeature.EnvironmentAllowed(1, InstantActionFeature.AceKey));
        Assert.True(InstantActionFeature.EnvironmentAllowed(0, InstantActionFeature.StuntKey));
    }

    [Fact]
    public void AFreshFeatureStandsOnTheScreensOpeningState()
    {
        var ia = Feature();

        Assert.Equal(0, ia.EnvironmentIndex);
        Assert.Equal("an airfield", ia.Environment.Name);
        Assert.Equal(0, ia.MissionTypeIndex);
        Assert.True(ia.IsAceDuel);
        Assert.Equal(1, ia.Lives);
        Assert.Equal(0, ia.NumWingmen);
        Assert.Equal("Autogyro", ia.WingmanPlane.Name);
        Assert.Equal("Autogyro", ia.PlayerPlane.Name);
        Assert.True(ia.WingmanFit.IsStock);
        Assert.Equal(-1, ia.PresetIndex);
        Assert.Null(ia.BaseDef);
        Assert.All(ia.Waves, w => Assert.Equal(default, w));
    }

    [Fact]
    public void ConfirmingTheEnvironmentLoadsItsDefAndRefitsTheMissionOntoItsRoster()
    {
        string? asked = null;
        var ia = new InstantActionFeature(code =>
        {
            asked = code;
            return Base;
        });
        ia.SelectMissionType(3);
        ia.SelectEnvironment(1);
        Assert.Equal(3, ia.MissionTypeIndex);
        Assert.Null(asked);

        ia.ConfirmEnvironment();

        Assert.Equal("C2B", asked);
        Assert.Same(Base, ia.BaseDef);
        Assert.Equal(0, ia.MissionTypeIndex);
        Assert.Throws<ArgumentOutOfRangeException>(() => ia.SelectEnvironment(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => ia.SelectMissionType(3));
    }

    [Fact]
    public void LivesReadUnlimitedAtZeroAndTheCountAbove()
    {
        Assert.Equal("Unlimited", InstantActionFeature.LivesLabel(0));
        Assert.Equal("1", InstantActionFeature.LivesLabel(1));
        Assert.Equal("9", InstantActionFeature.LivesLabel(InstantActionFeature.MaxLives));
    }

    [Fact]
    public void LivesClampBetweenUnlimitedAndTheCap()
    {
        var ia = Feature();
        ia.StepLives(-1);
        ia.StepLives(-1);
        Assert.Equal(0, ia.Lives);
        for (int i = 0; i < 20; i++)
        {
            ia.StepLives(1);
        }

        Assert.Equal(InstantActionFeature.MaxLives, ia.Lives);
    }

    [Fact]
    public void AWavesMilitiaChangeResetsItsAircraftAndTheCountAndSkillWrapOrClamp()
    {
        var ia = Feature();
        ia.SelectWaveMilitia(0, 7);
        ia.SelectWaveAircraft(0, 1);
        Assert.Equal(new InstantActionWaveSetup(0, 7, 1, 0), ia.Waves[0]);
        Assert.Equal(new[] { "Brigand", "Kestrel" }, ia.WaveAircraft(0));

        ia.StepWaveMilitia(0, 1);
        Assert.Equal(new InstantActionWaveSetup(0, 8, 0, 0), ia.Waves[0]);
        ia.StepWaveAircraft(0, 1);
        Assert.Equal(0, ia.Waves[0].AircraftIndex);

        ia.StepWaveCount(0, -1);
        Assert.Equal(0, ia.Waves[0].Count);
        for (int i = 0; i < 9; i++)
        {
            ia.StepWaveCount(0, 1);
        }

        Assert.Equal(InstantActionFeature.MaxEnemies, ia.Waves[0].Count);
        ia.StepWaveSkill(0, -1);
        Assert.Equal(2, ia.Waves[0].SkillIndex);
        Assert.Throws<ArgumentOutOfRangeException>(() => ia.StepWaveCount(4, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ia.SelectWaveAircraft(0, 5));
    }

    [Fact]
    public void AChangedWingmanPlaneDropsTheFitAndTheCountClamps()
    {
        var ia = Feature();
        ia.SetWingmen(9);
        Assert.Equal(InstantActionFeature.MaxWingmen, ia.NumWingmen);
        ia.StepWingmen(-9);
        Assert.Equal(0, ia.NumWingmen);

        ia.WingmanFit.SetGunAmmo(1, "test");
        Assert.False(ia.WingmanFit.IsStock);
        ia.SelectWingmanPlane(0);
        Assert.False(ia.WingmanFit.IsStock);
        ia.StepWingmanPlane(1);
        Assert.Equal("Hellhound", ia.WingmanPlane.Name);
        Assert.True(ia.WingmanFit.IsStock);
        ia.StepWingmanPlane(-1);
        Assert.Equal("Autogyro", ia.WingmanPlane.Name);
        ia.StepWingmanPlane(-1);
        Assert.Equal("Warhawk", ia.WingmanPlane.Name);
    }

    [Fact]
    public void ApplyingAPresetWritesEveryFieldButTheLivesAndTheBaseDef()
    {
        var ia = Feature();
        ia.StepLives(2);
        ia.ApplyPreset(0);

        Assert.Equal(0, ia.PresetIndex);
        Assert.Equal("Sky Haven", ia.Environment.Name);
        Assert.Equal("dogfight_squadron", ia.MissionType.Key);
        Assert.Equal("Firebrand", ia.PlayerPlane.Name);
        Assert.Equal(2, ia.NumWingmen);
        Assert.Equal("Peacemaker", ia.WingmanPlane.Name);
        Assert.Equal(new InstantActionWaveSetup(4, 7, 1, 1), ia.Waves[0]);
        Assert.Equal(new InstantActionWaveSetup(2, 1, 0, 2), ia.Waves[1]);
        Assert.Equal(0, ia.Waves[2].Count);
        Assert.Equal(4, ia.Waves[2].MilitiaIndex);
        Assert.Equal(3, ia.Lives);
        Assert.Null(ia.BaseDef);

        // An ace preset leaves the wingman plane where it was: the decode reports none at zero.
        ia.ApplyPreset(2);
        Assert.Equal("the ocean", ia.Environment.Name);
        Assert.True(ia.IsAceDuel);
        Assert.Equal(0, ia.NumWingmen);
        Assert.Equal("Peacemaker", ia.WingmanPlane.Name);
    }

    [Fact]
    public void TheGateWantsOneJoinedSeatAndEveryoneConfirmed()
    {
        var ia = Feature();
        Assert.Equal("no seat joined", ia.Refusal(0, 0));
        Assert.Equal("1 of 2 seats not confirmed", ia.Refusal(2, 1));
        Assert.Null(ia.Refusal(1, 1));
        Assert.True(ia.CanLaunch(3, 3));
        Assert.False(ia.CanLaunch(2, 1));
        Assert.Throws<InvalidOperationException>(() => ia.BuildExit(Array.Empty<MenuSeatChoice>()));
        Assert.Throws<InvalidOperationException>(() => ia.BuildExit(new[] { new MenuSeatChoice(string.Empty, Array.Empty<int>()) }));
    }

    [Fact]
    public void TheExitCarriesTheEnvironmentsChapterTheModeAndTheBuiltDef()
    {
        var ia = Feature();
        ia.ApplyPreset(0);
        ia.ConfirmEnvironment();
        ia.StepWaveCount(0, 1);
        ia.StepLives(1);
        var seats = new[] { new MenuSeatChoice("player_fury", Array.Empty<int>()) };

        var exit = ia.BuildExit(seats, "Fury");

        Assert.Equal("C4", exit.Chapter);
        Assert.Equal(MenuMode.Stunt, exit.Mode);
        Assert.Same(seats, exit.Seats);
        var def = exit.InstantAction!;
        Assert.Equal("dogfight_squadron", def.MissionType);
        Assert.Equal("Fury", def.PlayerPlane);
        Assert.Equal(2, def.NumWingmen);
        Assert.Equal("Peacemaker", def.WingmanPlane);
        // The trailing id is the militia's own wave accent, Medusa's and Black Swan's.
        Assert.Equal(new InstantActionWave(5, "Medusa Kestrel", "Kestrel", "veteran", 8), def.Waves[0]);
        Assert.Equal(new InstantActionWave(2, "Black Swan Fury", "Fury", "ace", 1), def.Waves[1]);
        Assert.Equal(InstantAction.EmptyWave, def.Waves[2]);
        Assert.Equal(2, def.Lives);
        Assert.Null(exit.WingmanLoadout);
        Assert.Equal("Test Ace", def.AceName);

        // Without a player name the feature's own pick is the def's aircraft.
        Assert.Equal("Firebrand", ia.BuildExit(seats).InstantAction!.PlayerPlane);
    }

    [Fact]
    public void TheAceDuelForcesTheWingmenAndEveryWaveEmptyInTheDef()
    {
        var ia = Feature();
        ia.SetWingmen(3);
        ia.SetWave(1, new InstantActionWaveSetup(4, 0, 0, 0));
        ia.ConfirmEnvironment();

        var def = ia.BuildDef();

        Assert.Equal("dogfight_ace", def.MissionType);
        Assert.Equal(0, def.NumWingmen);
        Assert.All(def.Waves, w => Assert.Equal(InstantAction.EmptyWave, w));
        Assert.Equal(InstantAction.Defaults().AceName, new InstantActionFeature(_ => InstantAction.Defaults()).BuildDef().AceName);
    }

    /// <summary>The wingman fit is dropped wherever no wingmen carry it (the ace duel, a count of
    /// 0). Stale wizard state therefore cannot arm a flight that does not exist. It is one
    /// fit for the whole flight, the original's Player/Wingman radio, never one per wingman.</summary>
    [Fact]
    public void TheWingmanFitIsDroppedWhenNoWingmenFly()
    {
        var ia = Feature();
        ia.ConfirmEnvironment();
        ia.WingmanFit.SetPylon(1, "wep_14");

        ia.SelectMissionType(1);
        ia.SetWingmen(2);
        Assert.Equal("wep_14", ia.LaunchWingmanFit!.PylonFor(1));
        ia.SetWingmen(0);
        Assert.Null(ia.LaunchWingmanFit);
        ia.SetWingmen(3);
        ia.SelectMissionType(0);
        Assert.True(ia.IsAceDuel);
        Assert.Null(ia.LaunchWingmanFit);
    }

    [Fact]
    public void DiscardPutsEveryFieldBackAndKeepsTheOptionSets()
    {
        var ia = Feature();
        ia.ApplyPreset(3);
        ia.ConfirmEnvironment();
        ia.StepLives(3);
        ia.WingmanFit.SetGunAmmo(1, "test");

        ia.Discard();

        Assert.Equal(0, ia.EnvironmentIndex);
        Assert.Equal(0, ia.MissionTypeIndex);
        Assert.Equal(1, ia.Lives);
        Assert.Equal(0, ia.NumWingmen);
        Assert.Equal(0, ia.WingmanPlaneIndex);
        Assert.True(ia.WingmanFit.IsStock);
        Assert.Equal(0, ia.PlayerPlaneIndex);
        Assert.Equal(-1, ia.PresetIndex);
        Assert.Null(ia.BaseDef);
        Assert.All(ia.Waves, w => Assert.Equal(default, w));
        Assert.Equal(7, InstantActionFeature.Environments.Count);
    }

    /// <summary>A def over the built-in defaults with one recognisable ace, so a test can tell the
    /// confirmed environment's def from the fallback.</summary>
    internal static InstantActionDef InstantActionDefFor(string aceName)
    {
        var d = InstantAction.Defaults();
        return new InstantActionDef
        {
            MissionType = d.MissionType,
            DisallowMissions = d.DisallowMissions,
            PlayerPlane = d.PlayerPlane,
            NumWingmen = d.NumWingmen,
            WingmanPlane = d.WingmanPlane,
            Waves = d.Waves,
            ZeppelinType = d.ZeppelinType,
            CargoZeppelinNode = d.CargoZeppelinNode,
            PassengerZeppelinNode = d.PassengerZeppelinNode,
            MilitaryZeppelinNode = d.MilitaryZeppelinNode,
            AceName = aceName,
            AcePlane = d.AcePlane,
            AceSkill = d.AceSkill,
            AceStats = d.AceStats,
            AceAccentId = d.AceAccentId,
            AceLivery = d.AceLivery,
            Lives = d.Lives,
        };
    }

    private static InstantActionFeature Feature() => new(_ => Base);
}
