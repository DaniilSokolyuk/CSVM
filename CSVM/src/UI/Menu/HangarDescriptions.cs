using System;
using System.Collections.Generic;
using System.Globalization;
using CSVM.Flight.Hangar;
using CSVM.Mech3;

namespace CSVM.UI.Menu;

/// <summary>What one construction tab's description box holds: the figure lines, the heading the
/// shipped info string ends with (DESCRIPTION, or the hardpoints' own HISTORY), and the
/// component's prose under it, unwrapped. A component the shipped table has no prose for leaves
/// the heading and the prose empty; an empty engine or gun slot is prose alone.</summary>
public sealed record HangarInfo(IReadOnlyList<string> Figures, string Heading, string Prose)
{
    /// <summary>An empty box.</summary>
    public static readonly HangarInfo None = new(Array.Empty<string>(), string.Empty, string.Empty);
}

/// <summary>
/// The six construction tabs' description boxes as the original's own handlers build them: the
/// tab's shipped info string filled with the build's figures, then the component's own prose
/// appended where the component has one. The info strings carry their own heading line, so the
/// heading is read out of the shipped text rather than written here; the addresses of the handlers
/// and the string-id blocks are in docs/org/hangar.md. Engine-free and presentation-neutral, so
/// either presentation's box is the same words.
/// </summary>
public static class HangarDescriptions
{
    // The tabs' info strings, each ending in a blank line and its own heading: IDS_PX_AIRFRAMEINFO
    // through IDS_PX_PAINTINFO. Armour, hardpoints and paint carry their prose inside the string;
    // the other three take a component prose row appended to it.
    private const int AirframeInfoString = 1153;
    private const int EngineInfoString = 1154;
    private const int ArmourInfoString = 1155;
    private const int GunInfoString = 1156;
    private const int HardpointInfoString = 1157;
    private const int PaintInfoString = 1158;

    // The prose blocks: IDS_AIRFRAMEDESCRIPTION is one row per airframe, IDS_ENGINEDESCRIPTION one
    // per airframe and engine id (the same airframe x 6 + id index the engine names take), and
    // IDS_GUNDESCRIPTION one per calibre with the No Gun row last. The two no-pick rows are their
    // own strings and stand alone, with no figures over them.
    private const int AirframeProseString = 3040;
    private const int EngineProseString = 3240;
    private const int NoEngineProseString = 3307;
    private const int GunProseString = 3330;
    private const int NoGunProseString = 3335;

    // None, the word the TURRETS line takes where the airframe mounts no turret.
    private const int NoneString = 1165;

    // No and Yes, the words the NITRO-BOOST line takes.
    private const int NoString = 1166;
    private const int YesString = 1167;

    /// <summary>The AIRFRAME box over one airframe's stat row and the two star words it is rated
    /// by, which the original rates from a record carrying the airframe and nothing else.</summary>
    public static HangarInfo Airframe(UiStrings strings, int airframe, string agility, string armour)
    {
        ArgumentNullException.ThrowIfNull(strings);
        var stats = HangarEconomy.Airframes[airframe];
        string turrets = stats.Turrets == 0
            ? strings.Text(NoneString, "None")
            : stats.Turrets.ToString(CultureInfo.InvariantCulture);
        return Split(
            strings.Format(AirframeInfoString, stats.Cost, stats.Weight, stats.Capacity, agility, armour, turrets)
            + strings.Text(AirframeProseString + airframe),
            new[]
            {
                Dollars("COST", stats.Cost),
                Pounds("WEIGHT", stats.Weight),
                Pounds("WEIGHT CAPACITY", stats.Capacity),
                "AGILITY: " + agility,
                "BASE ARMOR: " + armour,
                "TURRETS: " + turrets,
            });
    }

    /// <summary>The ENGINE box over one airframe's engine id. Id 6 is the no-engine row, whose own
    /// string stands alone with no figures over it.</summary>
    public static HangarInfo Engine(UiStrings strings, int airframe, int engine)
    {
        ArgumentNullException.ThrowIfNull(strings);
        if (engine == CustomPlaneDef.EngineNone)
        {
            return Split(strings.Text(NoEngineProseString), Array.Empty<string>());
        }

        var line = HangarEconomy.EngineLine(airframe, engine);
        int speed = HangarEconomy.PowerStat(airframe, engine);
        string nitro = engine >= 3 ? strings.Text(YesString, "Yes") : strings.Text(NoString, "No");
        return Split(
            strings.Format(EngineInfoString, line.Cost, line.Weight, speed, nitro)
            + strings.Text(EngineProseString + (airframe * 6) + engine),
            new[]
            {
                Dollars("COST", line.Cost),
                Pounds("WEIGHT", line.Weight),
                "TOP SPEED: " + speed.ToString(CultureInfo.InvariantCulture) + " m.p.h.",
                "NITRO-BOOST: " + nitro,
            });
    }

    /// <summary>The ARMOR box, which is one press of the stepper priced and weighed. Its prose is
    /// inside the shipped string, so no component row is appended.</summary>
    public static HangarInfo Armour(UiStrings strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return Split(
            strings.Format(
                ArmourInfoString,
                HangarEconomy.ArmourStepCost, HangarEconomy.ArmourUnitsPerStep,
                HangarEconomy.ArmourStepWeight, HangarEconomy.ArmourUnitsPerStep),
            new[]
            {
                $"COST: ${HangarEconomy.ArmourStepCost}/{HangarEconomy.ArmourUnitsPerStep} units",
                $"WEIGHT: {HangarEconomy.ArmourStepWeight} lbs./{HangarEconomy.ArmourUnitsPerStep} units",
            });
    }

    /// <summary>The GUNS box over one slot's pick: the mount's own price column, the calibre and
    /// the three figures nothing prices. An empty slot is its own string, figureless.</summary>
    public static HangarInfo Gun(UiStrings strings, AirframeStats stats, GunChoice gun, int slot)
    {
        ArgumentNullException.ThrowIfNull(strings);
        if (gun.Calibre is not { } calibre)
        {
            return Split(strings.Text(NoGunProseString), Array.Empty<string>());
        }

        var line = HangarEconomy.GunLine(stats, gun, slot);
        var figures = HangarEconomy.GunFigures(gun);
        int bore = HangarEconomy.Calibre(calibre);
        string rate = figures.Rate.ToString("0.0", CultureInfo.InvariantCulture);
        return Split(
            strings.Format(GunInfoString, line.Cost, line.Weight, bore, rate, figures.Range, figures.Ammo)
            + strings.Text(GunProseString + calibre),
            new[]
            {
                Dollars("COST", line.Cost),
                Pounds("WEIGHT", line.Weight),
                "CALIBER: " + bore.ToString(CultureInfo.InvariantCulture),
                "FIRE RATE: " + rate + "/sec.",
                "RANGE: " + figures.Range.ToString(CultureInfo.InvariantCulture) + " ft.",
                "AMMO: " + figures.Ammo.ToString(CultureInfo.InvariantCulture) + " rounds",
            });
    }

    /// <summary>The HARDPOINTS box, one hardpoint priced and weighed over the shipped history.
    /// Its heading is the one that is not DESCRIPTION.</summary>
    public static HangarInfo Hardpoints(UiStrings strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return Split(
            strings.Format(HardpointInfoString, HangarEconomy.HardpointCost, HangarEconomy.HardpointWeight),
            new[]
            {
                Dollars("COST", HangarEconomy.HardpointCost),
                Pounds("WEIGHT", HangarEconomy.HardpointWeight),
            });
    }

    /// <summary>The PAINT box, which prices nothing and is the shipped string whole.</summary>
    public static HangarInfo Paint(UiStrings strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        return Split(strings.Text(PaintInfoString), new[] { "COST: Free" });
    }

    private static string Dollars(string label, int cost) =>
        label + ": $" + cost.ToString(CultureInfo.InvariantCulture);

    private static string Pounds(string label, int weight) =>
        label + ": " + weight.ToString(CultureInfo.InvariantCulture) + " lbs.";

    // The shipped body split into its three parts at its own blank line: the figures over it, the
    // heading on the line under it, the prose after that. A body the table could not fill leaves
    // the fallback figures and no prose, and a body with no blank line is a no-pick row's string,
    // which stands as prose alone.
    private static HangarInfo Split(string body, IReadOnlyList<string> fallback)
    {
        if (body.Length == 0)
        {
            return new HangarInfo(fallback, string.Empty, string.Empty);
        }

        string[] lines = body.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        int blank = Array.FindIndex(lines, line => line.Trim().Length == 0);
        if (blank < 0)
        {
            return new HangarInfo(Array.Empty<string>(), string.Empty, body.Trim());
        }

        var figures = new string[blank];
        for (int i = 0; i < blank; i++)
        {
            figures[i] = lines[i].TrimEnd();
        }

        var prose = new List<string>();
        for (int i = blank + 2; i < lines.Length; i++)
        {
            string text = lines[i].Trim();
            if (text.Length > 0)
            {
                prose.Add(text);
            }
        }

        return new HangarInfo(
            figures,
            blank + 1 < lines.Length ? lines[blank + 1].Trim() : string.Empty,
            string.Join(" ", prose));
    }
}
