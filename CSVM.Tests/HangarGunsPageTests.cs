using System;
using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.UI.Hangar;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The GUNS screen: always four rows titled from the airframe's stat-table
/// slot-title strings, each stepping the original's 11-entry dropdown as a cycle (five calibres
/// single, the same five twinned via format 506, then No Gun, langui 3315), and the detail line
/// carrying the slot's decoded cost and weight (turret column per the airframe's 0-based turret
/// bit, doubled for twin) plus the calibre's magazine rounds.
/// </summary>
public class HangarGunsPageTests : IDisposable
{
    private readonly string _dir;
    private readonly CustomPlaneStore _store;

    public HangarGunsPageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csvm-guns-" + Guid.NewGuid().ToString("N"));
        _store = new CustomPlaneStore(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Four rows on every airframe, titled from the stat table: the Balmoral (airframe
    /// 2) shows its two turret slots' own title strings on rows 2 and 3.</summary>
    [Fact]
    public void AlwaysFourRows_TitledFromTheStatTable()
    {
        var strings = UiStrings.Parse(
            "[{\"id\":3060,\"text\":\"Nose Turret\",\"dll\":\"langui\"}," +
            "{\"id\":3073,\"text\":\"Rear Turret\",\"dll\":\"langui\"}," +
            "{\"id\":3315,\"text\":\"No Gun\",\"dll\":\"langui\"}]");
        var flow = OpenOnGuns(strings);
        flow.Scratch.Airframe = 2;

        Assert.Equal(4, flow.Page.RowCount);
        Assert.Equal("Nose Turret: No Gun", flow.Page.RowText(2));
        Assert.Equal("Rear Turret: No Gun", flow.Page.RowText(3));
    }

    /// <summary>A missing string table falls back to plain slot numbers and "No Gun".</summary>
    [Fact]
    public void RowTextFallsBackWithoutStrings()
    {
        var flow = OpenOnGuns(UiStrings.Empty);

        Assert.Equal("Slot 1: No Gun", flow.Page.RowText(0));
        Assert.Equal("Slot 4: No Gun", flow.Page.RowText(3));
    }

    /// <summary>From the empty pick, one step forward lands on the first single calibre; one
    /// step back wraps to the last twin row (index 9, the twinned .70).</summary>
    [Fact]
    public void SteppingWalksTheElevenRowCycle()
    {
        var flow = OpenOnGuns(UiStrings.Empty);

        Assert.True(flow.Page.Step(0, 1));
        Assert.Equal(new GunChoice(0, Twin: false), flow.Scratch.Guns[0]);

        flow.Scratch.Guns[0] = default;
        Assert.True(flow.Page.Step(0, -1));
        Assert.Equal(new GunChoice(4, Twin: true), flow.Scratch.Guns[0]);
    }

    /// <summary>Walking eleven steps from empty visits singles 0-4, twins 0-4, then No Gun.</summary>
    [Fact]
    public void ElevenStepsReturnToEmpty()
    {
        var flow = OpenOnGuns(UiStrings.Empty);
        for (int i = 0; i < 5; i++)
        {
            flow.Page.Step(1, 1);
            Assert.Equal(new GunChoice(i, Twin: false), flow.Scratch.Guns[1]);
        }

        for (int i = 0; i < 5; i++)
        {
            flow.Page.Step(1, 1);
            Assert.Equal(new GunChoice(i, Twin: true), flow.Scratch.Guns[1]);
        }

        flow.Page.Step(1, 1);
        Assert.True(flow.Scratch.Guns[1].IsEmpty);
    }

    /// <summary>Stepping a row edits that slot alone.</summary>
    [Fact]
    public void EachRowEditsItsOwnSlot()
    {
        var flow = OpenOnGuns(UiStrings.Empty);
        flow.Page.Step(2, 1);

        Assert.True(flow.Scratch.Guns[0].IsEmpty);
        Assert.True(flow.Scratch.Guns[1].IsEmpty);
        Assert.Equal(new GunChoice(0, Twin: false), flow.Scratch.Guns[2]);
        Assert.True(flow.Scratch.Guns[3].IsEmpty);
    }

    /// <summary>A twinned pick renders through the shared naming: the "(2) " prefix (format 506)
    /// before the calibre's own name.</summary>
    [Fact]
    public void TwinRowsCarryThePrefix()
    {
        var strings = UiStrings.Parse(
            "[{\"id\":506,\"text\":\"(%1!d!) \",\"dll\":\"langui\"}," +
            "{\"id\":3312,\"text\":\"Barret Arms .50-cal.\",\"dll\":\"langui\"}]");
        var flow = OpenOnGuns(strings);
        flow.Scratch.Guns[0] = new GunChoice(2, Twin: true);

        Assert.Equal("Slot 1: (2) Barret Arms .50-cal.", flow.Page.RowText(0));
    }

    /// <summary>A wing slot's detail prices the gun table's wing column: .50-cal (id 2) is $410,
    /// 480 lbs., with the calibre's 2000-round magazine stated.</summary>
    [Fact]
    public void DetailShowsTheWingColumnAndRounds()
    {
        var flow = OpenOnGuns(UiStrings.Empty);
        flow.Scratch.Guns[0] = new GunChoice(2, Twin: false);

        Assert.Equal("$410   480 lbs.   2000 rounds", flow.Page.Detail(0));
    }

    /// <summary>A turret slot prices the turret column, 0-based: the Balmoral's slot 2 carries
    /// the .50-cal at $610, 720 lbs.</summary>
    [Fact]
    public void DetailUsesTheTurretColumn()
    {
        var flow = OpenOnGuns(UiStrings.Empty);
        flow.Scratch.Airframe = 2;
        flow.Scratch.Guns[2] = new GunChoice(2, Twin: false);

        Assert.Equal("$610   720 lbs.   2000 rounds", flow.Page.Detail(2));
    }

    /// <summary>Twin doubles cost and weight but not the magazine: it is per gun, calibre-derived
    /// (CLUSTER_SIZE), so the rounds figure stays the calibre's own.</summary>
    [Fact]
    public void DetailDoublesForTwin_ButNotTheRounds()
    {
        var flow = OpenOnGuns(UiStrings.Empty);
        flow.Scratch.Guns[1] = new GunChoice(2, Twin: true);

        Assert.Equal("$820   960 lbs.   2000 rounds", flow.Page.Detail(1));
    }

    /// <summary>The biggest calibre buys the fewest rounds: the .70's magazine is 1200.</summary>
    [Fact]
    public void BiggerCalibreMeansFewerRounds()
    {
        var flow = OpenOnGuns(UiStrings.Empty);
        flow.Scratch.Guns[0] = new GunChoice(4, Twin: false);

        Assert.Equal("$580   680 lbs.   1200 rounds", flow.Page.Detail(0));
    }

    /// <summary>An empty slot's detail is the zero line, no rounds figure.</summary>
    [Fact]
    public void EmptySlotDetailIsZero()
    {
        var flow = OpenOnGuns(UiStrings.Empty);

        Assert.Equal("$0   0 lbs.", flow.Page.Detail(0));
    }

    /// <summary>The plan's verify line: the PURCHASE screen's figures move when picks change,
    /// because the bill prices the same scratch def the stepper edits.</summary>
    [Fact]
    public void PickingAGunMovesTheBill()
    {
        var flow = OpenOnGuns(UiStrings.Empty);
        int before = HangarEconomy.Price(flow.Scratch).Total.Cost;
        flow.Page.Step(0, 1);

        Assert.Equal(before + 240, HangarEconomy.Price(flow.Scratch).Total.Cost);
    }

    /// <summary>Confirm advances to the Hardpoints screen without touching the slots.</summary>
    [Fact]
    public void AcceptAdvancesWithoutEditing()
    {
        var flow = OpenOnGuns(UiStrings.Empty);
        flow.Scratch.Guns[0] = new GunChoice(1, Twin: true);
        flow.Accept();

        Assert.Equal(HangarScreen.Hardpoints, flow.Screen);
        Assert.Equal(new GunChoice(1, Twin: true), flow.Scratch.Guns[0]);
    }

    /// <summary>With the real extraction, every string the screen leans on resolves: the five
    /// calibre names, No Gun at 3315, the twin prefix 506, and every airframe's four slot-title
    /// ids.</summary>
    [ExtractedDataFact]
    public void GunStringsAndSlotTitlesResolve()
    {
        var strings = UiStrings.TryLoad(TestData.DataRoot!);
        Assert.NotNull(strings);

        for (int id = 3310; id <= 3315; id++)
        {
            Assert.True(strings!.Has(id), $"missing langui {id}");
        }

        Assert.True(strings!.Has(506));
        foreach (var stats in HangarEconomy.Airframes)
        {
            for (int slot = 0; slot < CustomPlaneDef.GunSlots; slot++)
            {
                Assert.True(strings.Has(stats.SlotTitle(slot)), $"missing langui {stats.SlotTitle(slot)}");
            }
        }
    }

    // A flow standing on the GUNS screen with a fresh scratch plane.
    private HangarFlow OpenOnGuns(UiStrings strings)
    {
        var flow = new HangarFlow(_store, strings);
        flow.Accept(); // New Plane, on to Airframe
        flow.Accept(); // pick the focused airframe (E49), which raises the defaults ask
        flow.AnswerDefaultsAsk(false); // decline it (E41)
        flow.Accept(); // on to Engine
        flow.Accept(); // on to Armour
        flow.Accept(); // on to Guns
        Assert.Equal(HangarScreen.Guns, flow.Screen);
        return flow;
    }
}
