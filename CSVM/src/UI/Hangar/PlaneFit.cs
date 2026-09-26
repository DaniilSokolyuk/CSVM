using System;
using System.Collections.Generic;
using CSVM.Flight.Hangar;
using CSVM.Flight.Weapons;

namespace CSVM.UI.Hangar;

/// <summary>
/// What one campaign aircraft is actually carrying: which gun sits in each of the four slots, how
/// many barrels of each calibre, how many hardpoints, how much armour and which engine. Resolved
/// from the plane's hangar build when it has one, and from its airframe's stock fit when it does
/// not, which is the case for the two profile-seeded starters and every granted reward aircraft
/// (<c>docs/org/hangar.md</c>, "the campaign wallet"). Engine-free, so the screens that draw it
/// test off engine.
/// </summary>
public sealed class PlaneFit
{
    // A record with no build carries the original's own stock build for that airframe, and every
    // one of those eleven takes the middle engine tier (docs/org/hangar.md, "The stock builds").
    private const int StockEngine = 1;

    private PlaneFit(
        int airframe, GunChoice[] slots, Dictionary<int, int> barrels, int hardpoints,
        int armourUnits, int engine)
    {
        Airframe = airframe;
        Slots = slots;
        Barrels = barrels;
        Hardpoints = hardpoints;
        ArmourUnits = armourUnits;
        Engine = engine;
    }

    /// <summary>The airframe id this record flies, 0-10.</summary>
    public int Airframe { get; }

    /// <summary>The four gun slots in the record's own order, empty where nothing is mounted. Slot
    /// order is what decides a gun's turret or wing price, so it is kept rather than summed.</summary>
    public IReadOnlyList<GunChoice> Slots { get; }

    /// <summary>How many barrels of each calibre in millimetres (30 to 70), by calibre.</summary>
    public IReadOnlyDictionary<int, int> Barrels { get; }

    /// <summary>How many underwing hardpoints, both wings.</summary>
    public int Hardpoints { get; }

    /// <summary>How much armour is fitted, in the record's own units (five to a stepper press).</summary>
    public int ArmourUnits { get; }

    /// <summary>The engine id, or the middle tier for a plane with no build.</summary>
    public int Engine { get; }

    /// <summary>Resolves a record. <paramref name="build"/> is its hangar build or null;
    /// <paramref name="stock"/> is its airframe's stock fit, used only when there is no build.
    /// ⚠ The caller resolves the build, never this: a stock record is named for its airframe, and
    /// looking one up by name would fit it with a hangar plane that happens to share the name
    /// (<see cref="CampaignFlightField.IsStock"/>).</summary>
    public static PlaneFit For(int airframe, CustomPlaneDef? build, LoadoutDef? stock)
    {
        int id = Math.Clamp(airframe, 0, HangarEconomy.Airframes.Length - 1);
        var slots = new GunChoice[CustomPlaneDef.GunSlots];
        if (build != null)
        {
            Array.Copy(build.Guns, slots, slots.Length);
            int presses = build.ArmourNose + build.ArmourTail
                + build.ArmourLeftWing + build.ArmourRightWing;
            return new PlaneFit(
                id, slots, BarrelsOf(slots), build.LeftHardpoints + build.RightHardpoints,
                presses * HangarEconomy.ArmourUnitsPerStep, build.Engine);
        }

        foreach (var gun in stock?.Guns ?? new List<GunSpec>())
        {
            if (gun.Slot is >= 1 and <= CustomPlaneDef.GunSlots)
            {
                slots[gun.Slot - 1] = new GunChoice(
                    Math.Clamp((gun.Caliber - 30) / 10, 0, CustomPlaneDef.MaxCalibre),
                    gun.Markers.Count > 1);
            }
        }

        return new PlaneFit(
            id, slots, BarrelsOf(slots), stock?.Hardpoints?.Count ?? 0,
            HangarEconomy.StockArmourUnits[id], StockEngine);
    }

    private static Dictionary<int, int> BarrelsOf(GunChoice[] slots)
    {
        var barrels = new Dictionary<int, int>();
        foreach (var gun in slots)
        {
            if (gun.Calibre is { } calibre)
            {
                int mm = 30 + (calibre * 10);
                barrels[mm] = barrels.GetValueOrDefault(mm) + (gun.Twin ? 2 : 1);
            }
        }

        return barrels;
    }
}
