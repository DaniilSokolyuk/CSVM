using System;
using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.UI.Hangar;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The HARDPOINTS screen: two per-wing rows named through langui 1176/1177
/// (which carry the count themselves), the stepper walking each wing's count 0-4 with wraparound
/// into the scratch def, and the detail line speaking the original dropdown's vocabulary
/// (1165/1168/1169) beside the decoded $410 / 480 lb per hardpoint and the wing's line total.
/// </summary>
public class HangarHardpointsPageTests : IDisposable
{
    private readonly string _dir;
    private readonly CustomPlaneStore _store;

    public HangarHardpointsPageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csvm-hardpoints-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Two rows, each its wing's own langui format with the count filled in.</summary>
    [Fact]
    public void TwoRows_NamedFromLangui()
    {
        var strings = UiStrings.Parse(
            "[{\"id\":1176,\"text\":\"Left Wing: %1!d!\",\"dll\":\"langui\"}," +
            "{\"id\":1177,\"text\":\"Right Wing: %1!d!\",\"dll\":\"langui\"}]");
        var flow = OpenOnHardpoints(strings);
        flow.Scratch.LeftHardpoints = 3;

        Assert.Equal(2, flow.Page.RowCount);
        Assert.Equal("Left Wing: 3", flow.Page.RowText(0));
        Assert.Equal("Right Wing: 0", flow.Page.RowText(1));
    }

    /// <summary>A missing string table falls back to the same shape in plain text.</summary>
    [Fact]
    public void RowTextFallsBackWithoutStrings()
    {
        var flow = OpenOnHardpoints(UiStrings.Empty);
        flow.Scratch.RightHardpoints = 2;

        Assert.Equal("Left Wing: 0", flow.Page.RowText(0));
        Assert.Equal("Right Wing: 2", flow.Page.RowText(1));
    }

    /// <summary>Stepping a row edits that wing alone.</summary>
    [Fact]
    public void EachRowEditsItsOwnWing()
    {
        var flow = OpenOnHardpoints(UiStrings.Empty);
        Assert.True(flow.Page.Step(1, 1));

        Assert.Equal(0, flow.Scratch.LeftHardpoints);
        Assert.Equal(1, flow.Scratch.RightHardpoints);
    }

    /// <summary>The stepper walks the original's 5-row roster as a cycle: below zero lands on
    /// 4, past 4 lands on zero.</summary>
    [Fact]
    public void SteppingWrapsAtBothEnds()
    {
        var flow = OpenOnHardpoints(UiStrings.Empty);

        Assert.True(flow.Step(-1));
        Assert.Equal(CustomPlaneDef.MaxHardpointsPerWing, flow.Scratch.LeftHardpoints);
        Assert.True(flow.Step(1));
        Assert.Equal(0, flow.Scratch.LeftHardpoints);
    }

    /// <summary>The detail speaks the dropdown's vocabulary at each count shape (None, the
    /// singular row, the plural format) and prices the wing: per-hardpoint $410 / 480 lb, then
    /// the line total.</summary>
    [Fact]
    public void DetailSpeaksTheDropdownVocabulary()
    {
        var flow = OpenOnHardpoints(UiStrings.Empty);

        Assert.Equal("None   $410 / 480 lbs. each   $0   0 lbs.", flow.Page.Detail(0));

        flow.Scratch.LeftHardpoints = 1;
        Assert.Equal("1 Hardpoint   $410 / 480 lbs. each   $410   480 lbs.", flow.Page.Detail(0));

        flow.Scratch.LeftHardpoints = 3;
        Assert.Equal("3 Hardpoints   $410 / 480 lbs. each   $1230   1440 lbs.", flow.Page.Detail(0));
    }

    /// <summary>The vocabulary renders through langui 1165/1168/1169 when the table has them.</summary>
    [Fact]
    public void DetailUsesLanguiWhenPresent()
    {
        var strings = UiStrings.Parse(
            "[{\"id\":1165,\"text\":\"Keine\",\"dll\":\"langui\"}," +
            "{\"id\":1169,\"text\":\"%1!d! Aufhaengungen\",\"dll\":\"langui\"}]");
        var flow = OpenOnHardpoints(strings);

        Assert.StartsWith("Keine", flow.Page.Detail(1), StringComparison.Ordinal);

        flow.Scratch.RightHardpoints = 4;
        Assert.StartsWith("4 Aufhaengungen", flow.Page.Detail(1), StringComparison.Ordinal);
    }

    /// <summary>Confirm advances to the Paint screen without touching the counts.</summary>
    [Fact]
    public void AcceptAdvancesWithoutEditing()
    {
        var flow = OpenOnHardpoints(UiStrings.Empty);
        flow.Scratch.LeftHardpoints = 2;
        flow.Scratch.RightHardpoints = 4;
        flow.Accept();

        Assert.Equal(HangarScreen.Paint, flow.Screen);
        Assert.Equal(2, flow.Scratch.LeftHardpoints);
        Assert.Equal(4, flow.Scratch.RightHardpoints);
    }

    /// <summary>With the real extraction, the row formats and the dropdown vocabulary all
    /// resolve.</summary>
    [ExtractedDataFact]
    public void HardpointStringsResolve()
    {
        var strings = UiStrings.TryLoad(TestData.DataRoot!);
        Assert.NotNull(strings);

        foreach (int id in new[] { 1165, 1168, 1169, 1176, 1177 })
        {
            Assert.True(strings!.Has(id), $"missing langui {id}");
        }
    }

    // A flow standing on the HARDPOINTS screen with a fresh scratch plane.
    private HangarFlow OpenOnHardpoints(UiStrings strings)
    {
        var flow = new HangarFlow(_store, strings);
        flow.Accept(); // New Plane, on to Airframe
        flow.Accept(); // pick the focused airframe (E49), which raises the defaults ask
        flow.AnswerDefaultsAsk(false); // decline it (E41)
        flow.Accept(); // on to Engine
        flow.Accept(); // on to Armour
        flow.Accept(); // on to Guns
        flow.Accept(); // on to Hardpoints
        Assert.Equal(HangarScreen.Hardpoints, flow.Screen);
        return flow;
    }
}
