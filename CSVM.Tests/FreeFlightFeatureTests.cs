using System;
using System.Collections.Generic;
using System.Linq;
using CSVM;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The Free Flight feature off-engine: the roster it offers, the pick, the launch gate and the
/// typed exit, each checked against the launchscreen's own public rules where one exists, so the
/// extracted feature and the screens it was cut from cannot disagree.
/// </summary>
public class FreeFlightFeatureTests
{
    private static readonly string[] AllEight = { "C1", "C1B", "C1C", "C2", "C2B", "C3", "C4", "C5" };

    [Fact]
    public void TheRosterIsAllEightChaptersInTheLaunchscreensOrder()
    {
        var feature = new FreeFlightFeature();

        Assert.Equal(AllEight, feature.Chapters.Select(c => c.Code));
        Assert.Equal(UI.Screens.LaunchMenu.ChapterCodesFor(MenuMode.Free), feature.Chapters.Select(c => c.Code));
        Assert.Equal(MenuMode.Free, FreeFlightFeature.Mode);
    }

    [Fact]
    public void NothingIsPickedUntilAPresentationPicksIt()
    {
        var feature = new FreeFlightFeature();

        Assert.Null(feature.Chapter);
        Assert.Equal("no chapter picked", feature.Refusal(1, 1));
        Assert.False(feature.CanLaunch(1, 1));
    }

    [Fact]
    public void PickingReplacesThePickAndRefusesACodeOutsideTheRoster()
    {
        var feature = new FreeFlightFeature();
        feature.SelectChapter("C5");
        Assert.Equal("C5", feature.Chapter!.Value.Code);

        feature.SelectChapter("C1B");
        Assert.Equal("C1B", feature.Chapter!.Value.Code);

        Assert.Throws<ArgumentException>(() => feature.SelectChapter("C9"));
        Assert.Equal("C1B", feature.Chapter!.Value.Code);
    }

    /// <summary>The gate is the launchscreen's own Free Flight rule: everyone joined has
    /// confirmed, solo included, and no second seat is ever required.</summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 3)]
    [InlineData(4, 4)]
    public void TheGateMatchesTheLaunchscreensRule(int joined, int confirmed)
    {
        var feature = Picked("C1");

        Assert.Equal(
            UI.Screens.LaunchMenu.CanLaunch(MenuMode.Free, allLocked: confirmed == joined, joined),
            feature.CanLaunch(joined, confirmed));
    }

    [Fact]
    public void TheRefusalNamesWhatIsMissing()
    {
        var feature = Picked("C1");

        Assert.Equal("no seat joined", feature.Refusal(0, 0));
        Assert.Equal("1 of 2 seats not confirmed", feature.Refusal(2, 1));
        Assert.Null(feature.Refusal(1, 1));
    }

    [Fact]
    public void TheExitCarriesTheChapterEverySeatModeFreeAndNoInstantAction()
    {
        var feature = Picked("C5");
        var fit = new LoadoutChoice();
        fit.SetPylon(1, "wep_14");
        var custom = new CustomPlaneDef { Name = "Blue Streak", Airframe = 7 };
        var seats = new[]
        {
            new MenuSeatChoice("player_balmoral", Array.Empty<int>()),
            new MenuSeatChoice("player_fury", new[] { 2 }, fit, custom),
        };

        var exit = feature.BuildExit(seats);

        Assert.Equal("C5", exit.Chapter);
        Assert.Equal(MenuMode.Free, exit.Mode);
        Assert.Null(exit.InstantAction);
        Assert.Same(seats, exit.Seats);
        Assert.Equal("player_balmoral", exit.Seats[0].PlaneNode);
        Assert.Null(exit.Seats[0].Fit);
        Assert.Null(exit.Seats[0].Custom);
        Assert.Same(fit, exit.Seats[1].Fit);
        Assert.Same(custom, exit.Seats[1].Custom);
        Assert.Equal(new[] { 2 }, exit.Seats[1].Pads);
    }

    [Fact]
    public void TheExitRefusesAHalfBuiltLaunch()
    {
        var unpicked = new FreeFlightFeature();
        var seat = new MenuSeatChoice("player_bhawk", Array.Empty<int>());
        Assert.Throws<InvalidOperationException>(() => unpicked.BuildExit(new[] { seat }));

        var picked = Picked("C1");
        Assert.Throws<InvalidOperationException>(() => picked.BuildExit(Array.Empty<MenuSeatChoice>()));
        Assert.Throws<InvalidOperationException>(
            () => picked.BuildExit(new[] { new MenuSeatChoice(string.Empty, Array.Empty<int>()) }));
        Assert.Throws<ArgumentNullException>(() => picked.BuildExit(null!));
    }

    [Fact]
    public void DiscardDropsThePickAndKeepsTheRoster()
    {
        var feature = Picked("C3");

        feature.Discard();

        Assert.Null(feature.Chapter);
        Assert.Equal(AllEight, feature.Chapters.Select(c => c.Code));
        Assert.False(feature.CanLaunch(1, 1));
    }

    [Fact]
    public void TheFeatureRegistersInTheSharedSet()
    {
        var features = new MenuFeatureSet();
        var feature = Picked("C2");
        features.Add(feature);

        Assert.Same(feature, features.Get<FreeFlightFeature>());
        features.DiscardTransient();
        Assert.Null(feature.Chapter);
    }

    // ---- The shared chapter roster --------------------------------------------------------------

    [Fact]
    public void TheSharedRosterAgreesWithTheLaunchscreensChapterScreens()
    {
        Assert.Equal(AllEight, MenuChapters.All.Select(c => c.Code));
        Assert.Equal(UI.Screens.LaunchMenu.ChapterCodesFor(MenuMode.Free), MenuChapters.For(MenuMode.Free).Select(c => c.Code));
        Assert.Equal(UI.Screens.LaunchMenu.ChapterCodesFor(MenuMode.Versus), MenuChapters.For(MenuMode.Versus).Select(c => c.Code));
        Assert.Equal(UI.Screens.LaunchMenu.ChapterCodesFor(MenuMode.Stunt), MenuChapters.For(MenuMode.Stunt).Select(c => c.Code));
    }

    [Fact]
    public void StuntFlyingOffersOnlyTheChaptersWithDangerZones()
    {
        Assert.Equal(new[] { "C1", "C1B", "C2", "C3", "C4", "C5" }, MenuChapters.For(MenuMode.Stunt).Select(c => c.Code));
        Assert.True(MenuChapters.DangerZonesFor("C1"));
        Assert.False(MenuChapters.DangerZonesFor("C2B"));
        Assert.False(MenuChapters.DangerZonesFor("C1C"));
        Assert.False(MenuChapters.DangerZonesFor("nowhere"));
        Assert.Null(MenuChapters.Find("nowhere"));
        Assert.Equal(new MenuChapter("C4", true), MenuChapters.Find("C4"));
    }

    private static FreeFlightFeature Picked(string code)
    {
        var feature = new FreeFlightFeature();
        feature.SelectChapter(code);
        return feature;
    }
}
