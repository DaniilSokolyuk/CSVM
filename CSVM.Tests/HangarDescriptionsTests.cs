using System.Linq;
using System.Text.Json;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.UI.Menu;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The construction tabs' description boxes: the shipped info string filled with the build's own
/// figures, the heading that string ends with, and the component's prose row appended under it.
/// The figures are checked against the reference stills of the Fury build
/// (<c>OriginalScreenshots/CustomPlane Engine.png</c>, <c>CustomPlane Guns.png</c>) and of the
/// Hoplite airframe (<c>CustomPlane PlaneSelection.png</c>), cell for cell. The prose rows here
/// are stand-ins; which id a component reads is what the cases pin.
/// </summary>
public class HangarDescriptionsTests
{
    private const string AirframeInfo =
        "COST: $%1!d!\nWEIGHT: %2!d! lbs.\nWEIGHT CAPACITY: %3!d! lbs.\nAGILITY: %4!s!\n"
        + "BASE ARMOR: %5!s!\nTURRETS: %6!s!\n\nDESCRIPTION\n";

    private const string EngineInfo =
        "COST: $%1!d!\nWEIGHT: %2!d! lbs.\nTOP SPEED: %3!d! m.p.h.\nNITRO-BOOST: %4!s!\n\nDESCRIPTION\n";

    private const string GunInfo =
        "COST: $%1!d!\nWEIGHT: %2!d! lbs.\nCALIBER: %3!d!\nFIRE RATE: %4!s!/sec.\nRANGE: %5!d! ft.\n"
        + "AMMO: %6!d! rounds\n\nDESCRIPTION\n";

    [Fact]
    public void TheEngineBoxIsTheFurysOwnFiguresUnderItsFamilysProse()
    {
        var strings = Table((1154, EngineInfo), (1166, "No"), (3283, "Fly straight! Fly Wright!"));

        // Airframe 7 is the Fury, engine id 1 its plain middle tier: the still's own four lines.
        var info = HangarDescriptions.Engine(strings, 7, 1);

        Assert.Equal(
            new[] { "COST: $850", "WEIGHT: 1000 lbs.", "TOP SPEED: 281 m.p.h.", "NITRO-BOOST: No" },
            info.Figures);
        Assert.Equal("DESCRIPTION", info.Heading);
        Assert.Equal("Fly straight! Fly Wright!", info.Prose);
    }

    [Fact]
    public void TheEngineProseIsIndexedByAirframeAndEngineAndTheNitroTiersSayYes()
    {
        var strings = Table(
            (1154, EngineInfo), (1166, "No"), (1167, "Yes"),
            (3240, "Ford"), (3283, "Wright"), (3286, "Wright nitro"));

        Assert.Equal("Ford", HangarDescriptions.Engine(strings, 0, 0).Prose);
        Assert.Equal("Wright", HangarDescriptions.Engine(strings, 7, 1).Prose);
        Assert.Equal("Wright nitro", HangarDescriptions.Engine(strings, 7, 4).Prose);
        Assert.Contains("NITRO-BOOST: Yes", HangarDescriptions.Engine(strings, 7, 4).Figures);
        Assert.Contains("NITRO-BOOST: No", HangarDescriptions.Engine(strings, 7, 2).Figures);
    }

    [Fact]
    public void AnEmptyEngineSlotIsItsOwnStringWithNoFiguresOverIt()
    {
        var info = HangarDescriptions.Engine(
            Table((1154, EngineInfo), (3307, "No Information Available")), 7, CustomPlaneDef.EngineNone);

        Assert.Empty(info.Figures);
        Assert.Equal(string.Empty, info.Heading);
        Assert.Equal("No Information Available", info.Prose);
    }

    [Fact]
    public void TheGunBoxIsTheTwinGoliathsOwnFiguresUnderItsCalibresProse()
    {
        var strings = Table((1156, GunInfo), (3334, "The Bruin Armaments cannon."));
        var fury = HangarEconomy.Airframes[7];

        // The still's slot 0 carries a twinned .70: the rate halves and the magazine doubles.
        var info = HangarDescriptions.Gun(strings, fury, new GunChoice(4, Twin: true), 0);

        Assert.Equal(
            new[]
            {
                "COST: $1160", "WEIGHT: 1360 lbs.", "CALIBER: 70",
                "FIRE RATE: 3.0/sec.", "RANGE: 1000 ft.", "AMMO: 1200 rounds",
            },
            info.Figures);
        Assert.Equal("DESCRIPTION", info.Heading);
        Assert.Equal("The Bruin Armaments cannon.", info.Prose);
    }

    [Fact]
    public void AnEmptyGunSlotIsTheNoGunStringAndASingleMountHalvesItsMagazine()
    {
        var strings = Table((1156, GunInfo), (3330, "Zephyr"), (3335, "No Information Available"));
        var fury = HangarEconomy.Airframes[7];

        var empty = HangarDescriptions.Gun(strings, fury, default, 0);
        Assert.Empty(empty.Figures);
        Assert.Equal("No Information Available", empty.Prose);

        var single = HangarDescriptions.Gun(strings, fury, new GunChoice(0, Twin: false), 0);
        Assert.Equal("Zephyr", single.Prose);
        Assert.Contains("FIRE RATE: 10.0/sec.", single.Figures);
        Assert.Contains("AMMO: 1400 rounds", single.Figures);
    }

    [Fact]
    public void TheAirframeBoxCarriesTheStatRowTheTwoWordsAndTheTurretCount()
    {
        var strings = Table((1153, AirframeInfo), (1165, "None"), (3040, "The Ford Hoplite."));

        // Airframe 0 is the Hoplite, whose still reads $6800 / 1400 / 4160 and no turret.
        var hoplite = HangarDescriptions.Airframe(strings, 0, "Excellent", "Poor");
        Assert.Equal(
            new[]
            {
                "COST: $6800", "WEIGHT: 1400 lbs.", "WEIGHT CAPACITY: 4160 lbs.",
                "AGILITY: Excellent", "BASE ARMOR: Poor", "TURRETS: None",
            },
            hoplite.Figures);
        Assert.Equal("The Ford Hoplite.", hoplite.Prose);

        // The Balmoral mounts two of its four slots in turrets, which is what the line counts.
        Assert.Contains("TURRETS: 2", HangarDescriptions.Airframe(strings, 2, "Poor", "Excellent").Figures);
    }

    [Fact]
    public void ArmourHardpointsAndPaintCarryTheirProseInsideTheirOwnString()
    {
        var strings = Table(
            (1155, "COST: $%1!d!/%2!d! units\nWEIGHT: %3!d! lbs./%4!d! units\n\nDESCRIPTION\nAero-armor."),
            (1157, "COST: $%1!d!\nWEIGHT: %2!d! lbs.\n\nHISTORY\nDeBruin hardpoints."),
            (1158, "COST: Free\n\nDESCRIPTION\nEZ Air-Lite paint."));

        var armour = HangarDescriptions.Armour(strings);
        Assert.Equal(new[] { "COST: $20/5 units", "WEIGHT: 20 lbs./5 units" }, armour.Figures);
        Assert.Equal("DESCRIPTION", armour.Heading);
        Assert.Equal("Aero-armor.", armour.Prose);

        var hardpoints = HangarDescriptions.Hardpoints(strings);
        Assert.Equal(new[] { "COST: $410", "WEIGHT: 480 lbs." }, hardpoints.Figures);
        Assert.Equal("HISTORY", hardpoints.Heading);
        Assert.Equal("DeBruin hardpoints.", hardpoints.Prose);

        var paint = HangarDescriptions.Paint(strings);
        Assert.Equal(new[] { "COST: Free" }, paint.Figures);
        Assert.Equal("EZ Air-Lite paint.", paint.Prose);
    }

    [Fact]
    public void WithNoStringTableTheFiguresStandAloneAndNothingIsInvented()
    {
        var info = HangarDescriptions.Engine(UiStrings.Empty, 7, 1);

        Assert.Equal(
            new[] { "COST: $850", "WEIGHT: 1000 lbs.", "TOP SPEED: 281 m.p.h.", "NITRO-BOOST: No" },
            info.Figures);
        Assert.Equal(string.Empty, info.Heading);
        Assert.Equal(string.Empty, info.Prose);
        Assert.Equal(HangarInfo.None.Figures, HangarDescriptions.Gun(UiStrings.Empty, HangarEconomy.Airframes[7], default, 0).Figures);
    }

    private static UiStrings Table(params (int Id, string Text)[] rows) =>
        UiStrings.Parse(JsonSerializer.Serialize(
            rows.Select(row => new { id = row.Id, dll = UiStrings.Table, text = row.Text })));
}
