using CSVM.Flight.Hangar;
using CSVM.UI.Menu;

namespace CSVM.UI.Hangar;

/// <summary>
/// The ARMOR screen: the four zones as rows, named through their own langui formats (1191-1194,
/// "Nose: %1!d! units" and kin, which carry the number themselves). The ←→ stepper walks the
/// focused zone the way the original's 13-row dropdown does, 0 to 60 in fives: what the screen
/// shows is the press count times five, and row 0 of that dropdown is langui 1165 "None" rather
/// than a count (callback 2246 at <c>0x0040b7bd</c> pushes 1165 for index 0 and format 1170 with
/// <c>index*5</c> for the rest). One press therefore buys five units, at $20 and 20 lb. The two
/// wing rows are held equal by <see cref="HangarFeature.SetZoneUnits"/>, the shared rule Original's
/// pair of combo boxes obeys too, which is why four rows show where three values move.
/// </summary>
public sealed class HangarArmourPage : HangarPage
{
    /// <summary>The units one press buys, which is also the factor between the stored press count
    /// and the figure the screen shows. Named here for the screens; the decode is on the
    /// economy's own constant.</summary>
    public const int DisplayScale = HangarEconomy.ArmourUnitsPerStep;

    private static readonly string[] ZoneFallbacks = { "Nose", "Tail", "Left Wing", "Right Wing" };

    /// <summary>Binds the page to its flow.</summary>
    public HangarArmourPage(HangarFlow flow)
        : base(flow)
    {
    }

    /// <inheritdoc/>
    public override HangarScreen Screen => HangarScreen.Armour;

    /// <inheritdoc/>
    public override int RowCount => ZoneFallbacks.Length;

    /// <inheritdoc/>
    public override string RowText(int row)
    {
        int shown = UnitsOf(row) * DisplayScale;
        string text = Flow.Strings.Format(1191 + row, shown);
        return text.Length > 0 ? text : $"{ZoneFallbacks[row]}: {shown} units";
    }

    /// <inheritdoc/>
    public override string Detail(int row)
    {
        int units = UnitsOf(row);
        string bought = units == 0
            ? Flow.Strings.Text(1165, "None")
            : Flow.Strings.Format(1170, units * DisplayScale);
        if (bought.Length == 0)
        {
            bought = $"{units * DisplayScale} units";
        }

        return $"{bought}   ${units * HangarEconomy.ArmourStepCost}   " +
               $"{units * HangarEconomy.ArmourStepWeight} lbs.";
    }

    /// <inheritdoc/>
    public override bool Step(int row, int dir)
    {
        int count = CustomPlaneDef.MaxArmourUnits + 1;
        SetUnits(row, ((UnitsOf(row) + dir) % count + count) % count);
        return true;
    }

    // The zones in the record's own order, the order 1191-1194 name them in.
    private int UnitsOf(int row) => row switch
    {
        0 => Scratch.ArmourNose,
        1 => Scratch.ArmourTail,
        2 => Scratch.ArmourLeftWing,
        _ => Scratch.ArmourRightWing,
    };

    private void SetUnits(int row, int units) => HangarFeature.SetZoneUnits(Scratch, row, units);
}
