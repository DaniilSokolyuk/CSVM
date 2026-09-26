using CSVM.Flight.Hangar;

namespace CSVM.UI.Hangar;

/// <summary>
/// The HARDPOINTS screen: two rows, one per wing, named through langui 1176/1177 ("Left Wing:
/// %1!d!" / "Right Wing: %1!d!", which carry the count themselves). The ←→ stepper walks the
/// focused wing's count 0-4 with wraparound, the original's 5-row dropdown as a cycle, writing
/// the scratch def; Confirm advances without editing. The detail line speaks the dropdown's own
/// vocabulary (1165 "None", 1168 "1 Hardpoint", 1169 "%1!d! Hardpoints") and prices it: the
/// decoded $410 / 480 lb per hardpoint, then the wing's line total.
/// </summary>
public sealed class HangarHardpointsPage : HangarPage
{
    /// <summary>Binds the page to its flow.</summary>
    public HangarHardpointsPage(HangarFlow flow)
        : base(flow)
    {
    }

    /// <inheritdoc/>
    public override HangarScreen Screen => HangarScreen.Hardpoints;

    /// <inheritdoc/>
    public override int RowCount => 2;

    /// <inheritdoc/>
    public override string RowText(int row)
    {
        int count = CountOf(row);
        string text = Flow.Strings.Format(row == 0 ? 1176 : 1177, count);
        return text.Length > 0 ? text : $"{(row == 0 ? "Left" : "Right")} Wing: {count}";
    }

    /// <inheritdoc/>
    public override string Detail(int row)
    {
        int count = CountOf(row);
        return $"{CountLabel(count)}   ${HangarEconomy.HardpointCost} / " +
               $"{HangarEconomy.HardpointWeight} lbs. each   " +
               $"${count * HangarEconomy.HardpointCost}   {count * HangarEconomy.HardpointWeight} lbs.";
    }

    /// <inheritdoc/>
    public override bool Step(int row, int dir)
    {
        int rows = CustomPlaneDef.MaxHardpointsPerWing + 1;
        int count = ((CountOf(row) + dir) % rows + rows) % rows;
        if (row == 0)
        {
            Scratch.LeftHardpoints = count;
        }
        else
        {
            Scratch.RightHardpoints = count;
        }

        return true;
    }

    // The count in the original dropdown's own words: None, the singular row, or the plural
    // format.
    private string CountLabel(int count) => count switch
    {
        0 => Flow.Strings.Text(1165, "None"),
        1 => Flow.Strings.Text(1168, "1 Hardpoint"),
        _ => Format1169(count),
    };

    private string Format1169(int count)
    {
        string text = Flow.Strings.Format(1169, count);
        return text.Length > 0 ? text : $"{count} Hardpoints";
    }

    private int CountOf(int row) => row == 0 ? Scratch.LeftHardpoints : Scratch.RightHardpoints;
}
