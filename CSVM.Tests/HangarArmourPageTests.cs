using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.UI.Hangar;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The ARMOR screen, on the original's display scale: the four zones
/// named through their own langui formats (1191-1194, which carry the number themselves) at the
/// record's stored units x5, the stepper walking that 0-60-in-fives roster into the scratch def,
/// and the detail line naming the pick the way the original's dropdown does (1165 "None" on zero,
/// 1170 "%d units" of units x5) beside the cost and weight the units themselves buy at x4. The two
/// wing rows are coupled the way the original's pair of combo boxes is, and a saved plane whose
/// wings disagree is not levelled on load.
/// </summary>
public class HangarArmourPageTests : IDisposable
{
    private readonly string _dir;
    private readonly CustomPlaneStore _store;

    public HangarArmourPageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csvm-armour-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Four rows, each its zone's own langui format filled with the displayed figure,
    /// the record's stored units x5.</summary>
    [Fact]
    public void OffersTheFourZones_NamedFromLangui_OnTheDisplayScale()
    {
        var strings = UiStrings.Parse(
            "[{\"id\":1191,\"text\":\"Nose: %1!d! units\",\"dll\":\"langui\"}," +
            "{\"id\":1194,\"text\":\"Right Wing: %1!d! units\",\"dll\":\"langui\"}]");
        var flow = OpenOnArmour(strings);
        flow.Scratch.ArmourNose = 3;

        Assert.Equal(4, flow.Page.RowCount);
        Assert.Equal("Nose: 15 units", flow.Page.RowText(0));
        Assert.Equal("Right Wing: 0 units", flow.Page.RowText(3));
    }

    /// <summary>A missing string table falls back to the same shape in plain text.</summary>
    [Fact]
    public void RowTextFallsBackWithoutStrings()
    {
        var flow = OpenOnArmour(UiStrings.Empty);
        flow.Scratch.ArmourTail = 7;

        Assert.Equal("Tail: 35 units", flow.Page.RowText(1));
        Assert.Equal("Left Wing: 0 units", flow.Page.RowText(2));
    }

    /// <summary>The row walks 0 to 60 in fives, the thirteen rows the original's dropdown offers
    /// (callback 2246 at 0x0040b7bd), while the model keeps counting in units.</summary>
    [Fact]
    public void TheDisplayedFigureRunsZeroToSixtyInFives()
    {
        var flow = OpenOnArmour(UiStrings.Empty);
        var shown = new List<string>();
        for (int i = 0; i <= CustomPlaneDef.MaxArmourUnits; i++)
        {
            shown.Add(flow.Page.RowText(0));
            flow.Page.Step(0, 1);
        }

        Assert.Equal("Nose: 0 units", shown[0]);
        Assert.Equal("Nose: 5 units", shown[1]);
        Assert.Equal("Nose: 60 units", shown[12]);
        Assert.Equal(0, flow.Scratch.ArmourNose); // and back to the start
    }

    /// <summary>The nose and tail rows step alone, in the 1191-1194 order the record stores the
    /// zones: nose, tail, left wing, right wing.</summary>
    [Fact]
    public void TheNoseAndTailRowsEditTheirOwnZone()
    {
        var flow = OpenOnArmour(UiStrings.Empty);

        Assert.True(flow.Page.Step(0, 1));
        Assert.True(flow.Page.Step(1, 1));

        Assert.Equal(1, flow.Scratch.ArmourNose);
        Assert.Equal(1, flow.Scratch.ArmourTail);
        Assert.Equal(0, flow.Scratch.ArmourLeftWing);
        Assert.Equal(0, flow.Scratch.ArmourRightWing);
    }

    /// <summary>The two wing rows move together, as the original's pair of combo boxes does:
    /// stepping either writes both dwords and both rows read the same afterwards, while the nose
    /// and tail stay where they were.</summary>
    [Fact]
    public void SteppingEitherWingRowMovesBothWings()
    {
        var flow = OpenOnArmour(UiStrings.Empty);
        flow.Scratch.ArmourNose = 2;

        Assert.True(flow.Page.Step(2, 1));
        Assert.Equal(1, flow.Scratch.ArmourLeftWing);
        Assert.Equal(1, flow.Scratch.ArmourRightWing);
        Assert.Equal("Left Wing: 5 units", flow.Page.RowText(2));
        Assert.Equal("Right Wing: 5 units", flow.Page.RowText(3));

        Assert.True(flow.Page.Step(3, 1));
        Assert.Equal(2, flow.Scratch.ArmourLeftWing);
        Assert.Equal(2, flow.Scratch.ArmourRightWing);
        Assert.Equal(2, flow.Scratch.ArmourNose);
        Assert.Equal(0, flow.Scratch.ArmourTail);
    }

    /// <summary>A saved plane whose wings disagree loads as it was stored: the record keeps two
    /// dwords so the mission side can damage a wing at a time, and levelling on load would rewrite
    /// a plane nobody edited. The two rows show their own figures until a wing row is stepped, and
    /// that press is what levels them.</summary>
    [Fact]
    public void AnUnequalSavedPlaneKeepsItsWingsUntilOneIsStepped()
    {
        _store.Save(new CustomPlaneDef { Name = "Lopsided", ArmourLeftWing = 2, ArmourRightWing = 9 });
        var flow = OpenOnArmour(UiStrings.Empty);

        flow.StartFromSaved(_store.Load("Lopsided")!);

        Assert.Equal(2, flow.Scratch.ArmourLeftWing);
        Assert.Equal(9, flow.Scratch.ArmourRightWing);
        Assert.Equal("Left Wing: 10 units", flow.Page.RowText(2));
        Assert.Equal("Right Wing: 45 units", flow.Page.RowText(3));

        Assert.True(flow.Page.Step(2, 1));
        Assert.Equal(3, flow.Scratch.ArmourLeftWing);
        Assert.Equal(3, flow.Scratch.ArmourRightWing);
        Assert.Equal(9, _store.Load("Lopsided")!.ArmourRightWing);
    }

    /// <summary>The stepper walks the 13-unit roster as a cycle: below zero lands on 12, past
    /// 12 lands on zero.</summary>
    [Fact]
    public void SteppingWrapsAtBothEnds()
    {
        var flow = OpenOnArmour(UiStrings.Empty);

        Assert.True(flow.Step(-1));
        Assert.Equal(CustomPlaneDef.MaxArmourUnits, flow.Scratch.ArmourNose);
        Assert.True(flow.Step(1));
        Assert.Equal(0, flow.Scratch.ArmourNose);
    }

    /// <summary>The detail names the pick and what it costs: 3 presses show as 15 units and buy
    /// $60 of 60 lb armour, the decoded $4 and 4 lb a unit. The unit count and the pounds are
    /// different numbers here, so a readout printing one for the other cannot pass.</summary>
    [Fact]
    public void DetailNamesThePickAndItsPrice()
    {
        var flow = OpenOnArmour(UiStrings.Empty);
        flow.Scratch.ArmourNose = 3;

        string detail = flow.Page.Detail(0);
        Assert.StartsWith("15 units", detail, StringComparison.Ordinal);
        Assert.Contains("$60", detail, StringComparison.Ordinal);
        Assert.Contains("60 lbs.", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("lb shown", detail, StringComparison.Ordinal);
    }

    /// <summary>The pick renders through the dropdown's own strings: format 1170 with the presses
    /// times five, and langui 1165 "None" for the zero row rather than a count of nothing. A full
    /// zone is 60 units, $240 and 240 lb, a quarter of the full airframe's 240 units.</summary>
    [Fact]
    public void DetailUsesFormat1170_AndString1165ForNone()
    {
        var strings = UiStrings.Parse(
            "[{\"id\":1170,\"text\":\"%1!d! units\",\"dll\":\"langui\"}," +
            "{\"id\":1165,\"text\":\"None\",\"dll\":\"langui\"}]");
        var flow = OpenOnArmour(strings);
        flow.Scratch.ArmourRightWing = 12;

        string detail = flow.Page.Detail(3);
        Assert.StartsWith("60 units", detail, StringComparison.Ordinal);
        Assert.Contains("$240", detail, StringComparison.Ordinal);
        Assert.Contains("240 lbs.", detail, StringComparison.Ordinal);
        Assert.StartsWith("None   $0", flow.Page.Detail(0), StringComparison.Ordinal);
    }

    /// <summary>Confirm advances to the Guns screen without touching the zones.</summary>
    [Fact]
    public void AcceptAdvancesWithoutEditing()
    {
        var flow = OpenOnArmour(UiStrings.Empty);
        flow.Scratch.ArmourNose = 5;
        flow.Scratch.ArmourRightWing = 9;
        flow.Accept();

        Assert.Equal(HangarScreen.Guns, flow.Screen);
        Assert.Equal(5, flow.Scratch.ArmourNose);
        Assert.Equal(9, flow.Scratch.ArmourRightWing);
    }

    // A flow standing on the ARMOR screen with a fresh scratch plane.
    private HangarFlow OpenOnArmour(UiStrings strings)
    {
        var flow = new HangarFlow(_store, strings);
        flow.Accept(); // New Plane, on to Airframe
        flow.Accept(); // pick the focused airframe (E49), which raises the defaults ask
        flow.AnswerDefaultsAsk(false); // decline it (E41)
        flow.Accept(); // on to Engine
        flow.Accept(); // on to Armour
        Assert.Equal(HangarScreen.Armour, flow.Screen);
        return flow;
    }
}
