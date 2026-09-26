using System;
using CSVM.Flight.Hangar;

namespace CSVM.UI.Hangar;

/// <summary>
/// The four ratings the plane selection screen prints beside an aircraft (<c>PS_T_TOPSPEEDP</c> and
/// its three neighbours), each as a 0-to-4 index into langui 501-505, Poor to Excellent.
///
/// All four are the original's own integer arithmetic over one plane record and its airframe's stat
/// row, transcribed case for case; the divisors, the inputs each reads and the reference aircraft
/// they reproduce are in <c>docs/org/hangar.md</c>, "The four rating words".
/// </summary>
public static class PlaneRatings
{
    /// <summary>The highest rating index, langui 505 <c>Excellent</c>.</summary>
    public const int Best = 4;

    // The four divisors, in the rating helper's own case order. Each turns one raw quantity (engine
    // power, armour units, the agility stat, armament weight) into a rating index.
    private const int SpeedDivisor = 0x55;
    private const int ArmourDivisor = 0x49;
    private const int AgilityDivisor = 4;
    private const int OffenseDivisor = 0x80c;

    /// <summary>The four ratings in the order the screen prints them: top speed, armour, agility,
    /// offense.</summary>
    public static int[] For(PlaneFit fit)
    {
        ArgumentNullException.ThrowIfNull(fit);
        return new[] { Speed(fit), Armour(fit), Agility(fit.Airframe), Offense(fit) };
    }

    /// <summary>TOP SPEED: the airframe's engine power at the fitted engine's factor, less one and
    /// truncated, over the speed divisor. A record with no engine rates Poor without the
    /// arithmetic.</summary>
    public static int Speed(PlaneFit fit)
    {
        ArgumentNullException.ThrowIfNull(fit);
        if (fit.Engine == CustomPlaneDef.EngineNone)
        {
            return 0;
        }

        double power = HangarEconomy.EngineBases[fit.Airframe].Power
            * HangarEconomy.EnginePowerFactors[
                Math.Clamp(fit.Engine, 0, HangarEconomy.EnginePowerFactors.Length - 1)];
        return Word((int)(power - 1.0) / SpeedDivisor);
    }

    /// <summary>ARMOR: the airframe's armour base plus the record's fitted armour units, less one,
    /// over the armour divisor. The same reading the hangar's own star column takes.</summary>
    public static int Armour(PlaneFit fit)
    {
        ArgumentNullException.ThrowIfNull(fit);
        return Word(
            (HangarEconomy.Airframes[fit.Airframe].Armour + fit.ArmourUnits - 1) / ArmourDivisor);
    }

    /// <summary>AGILITY: the airframe's agility stat alone, less one, over four. The one rating no
    /// part of the build moves.</summary>
    public static int Agility(int airframe) =>
        Word((HangarEconomy.Airframes[Math.Clamp(airframe, 0, HangarEconomy.Airframes.Length - 1)]
            .Agility - 1) / AgilityDivisor);

    /// <summary>OFFENSE: what the armament weighs, guns priced by the slot's own wing or turret
    /// column and doubled for a twin mount, plus one hardpoint weight per hardpoint, over the
    /// offense divisor.</summary>
    public static int Offense(PlaneFit fit)
    {
        ArgumentNullException.ThrowIfNull(fit);
        var stats = HangarEconomy.Airframes[fit.Airframe];
        int weight = fit.Hardpoints * HangarEconomy.HardpointWeight;
        for (int slot = 0; slot < fit.Slots.Count; slot++)
        {
            weight += HangarEconomy.GunLine(stats, fit.Slots[slot], slot).Weight;
        }

        return Word(weight / OffenseDivisor);
    }

    // The original clamps the top alone, so an index below zero names a langui id outside the five
    // rating words. No plane record drives it there (docs/org/hangar.md), so the floor is a guard.
    private static int Word(int value) => Math.Clamp(value, 0, Best);
}
