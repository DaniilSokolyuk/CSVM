using CSVM.Flight.Hangar;

namespace CSVM.UI.Hangar;

/// <summary>
/// The GUNS screen: always four rows, since every airframe has exactly four slots (the slot-count
/// disproof; titles vary, the count does not), each titled from the airframe's stat-table
/// slot-title string. The ←→ stepper walks the original's 11-entry dropdown as a cycle: five
/// calibres single (langui 3310-3314), the same five twinned ("(2) " via format 506), then
/// No Gun (langui 3315, the empty gun id 5's own name in the 3310+type series). The detail is
/// the slot's decoded cost and weight (turret column when the airframe's turret bit marks the
/// slot, doubled when twinned) plus the calibre's magazine, because a bigger calibre
/// legitimately buys fewer rounds and the trade-off should be visible where the pick is made.
/// </summary>
public sealed class HangarGunsPage : HangarPage
{
    // The calibre-derived magazine (the original's CLUSTER_SIZE, gun-table +0x14): per gun, not
    // per barrel, so twinning does not change it.
    private static readonly int[] MagazineRounds = { 2800, 2400, 2000, 1600, 1200 };

    /// <summary>Binds the page to its flow.</summary>
    public HangarGunsPage(HangarFlow flow)
        : base(flow)
    {
    }

    /// <inheritdoc/>
    public override HangarScreen Screen => HangarScreen.Guns;

    /// <inheritdoc/>
    public override int RowCount => CustomPlaneDef.GunSlots;

    /// <inheritdoc/>
    public override string RowText(int row)
    {
        var stats = HangarEconomy.Airframes[Scratch.Airframe];
        string title = Flow.Strings.Text(stats.SlotTitle(row), $"Slot {row + 1}");
        return $"{title}: {PickName(Scratch.Guns[row])}";
    }

    /// <inheritdoc/>
    public override string Detail(int row)
    {
        var gun = Scratch.Guns[row];
        if (gun.Calibre is not { } calibre)
        {
            return "$0   0 lbs.";
        }

        var stats = HangarEconomy.Airframes[Scratch.Airframe];
        var table = HangarEconomy.GunTable[calibre];
        var one = stats.IsTurretSlot(row)
            ? new CostWeight(table.TurretCost, table.TurretWeight)
            : new CostWeight(table.WingCost, table.WingWeight);
        var line = gun.Twin ? one * 2 : one;
        return $"${line.Cost}   {line.Weight} lbs.   {MagazineRounds[calibre]} rounds";
    }

    /// <inheritdoc/>
    public override bool Step(int row, int dir)
    {
        const int rows = 11;
        int at = ((CycleIndex(Scratch.Guns[row]) + dir) % rows + rows) % rows;
        Scratch.Guns[row] = at switch
        {
            10 => default,
            < 5 => new GunChoice(at, Twin: false),
            _ => new GunChoice(at - 5, Twin: true),
        };
        return true;
    }

    // Where a pick sits in the 11-entry cycle: singles 0-4, twins 5-9, No Gun 10.
    private static int CycleIndex(GunChoice gun) =>
        gun.Calibre is not { } calibre ? 10 : gun.Twin ? calibre + 5 : calibre;

    // The slot's pick as the dropdown named it: the shared calibre naming (with its "(2) "
    // twin prefix), or the empty row's own string.
    private string PickName(GunChoice gun) =>
        gun.IsEmpty ? Flow.Strings.Text(3315, "No Gun") : Flow.GunName(gun);
}
