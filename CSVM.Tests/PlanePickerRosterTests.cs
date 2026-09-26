using System;
using CSVM.Flight.Hangar;
using CSVM.UI.Hangar;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The picker roster's build rule: stock airframes first in their given order,
/// saved customs after them, each custom carrying its store name and flying as its airframe's
/// stock node. `LaunchMenu` is engine-bound and untestable directly, so these are the
/// facts behind every human plane picker it draws, lone-pilot and splitscreen panes alike.
/// </summary>
public class PlanePickerRosterTests
{
    // A stand-in for LaunchMenu.Planes: order is what the roster must preserve, not the names.
    private static readonly (string Name, string Node)[] Stock =
    {
        ("Devastator", "player_pfighter"),
        ("Bloodhawk", "player_bhawk"),
        ("Warhawk", "player_warhawk"),
    };

    [Fact]
    public void StockRowsComeFirstInTheirGivenOrder()
    {
        var roster = PlanePickerRoster.Build(Stock, Array.Empty<CustomPlaneDef>());
        Assert.Equal(Stock.Length, roster.Count);
        for (int i = 0; i < Stock.Length; i++)
        {
            Assert.Equal(Stock[i].Name, roster[i].Name);
            Assert.Equal(Stock[i].Node, roster[i].Node);
            Assert.False(roster[i].IsCustom);
        }
    }

    /// <summary>The original's list sizing: 11 stock then the customs (docs/org/hangar.md,
    /// count callback 1024). Ours appends in the store's own order.</summary>
    [Fact]
    public void CustomsListAfterTheStockRows()
    {
        var customs = new[]
        {
            new CustomPlaneDef { Name = "Aardvark", Airframe = 10, Engine = 1 },
            new CustomPlaneDef { Name = "Zephyr Mk2", Airframe = 0, Engine = 1 },
        };
        var roster = PlanePickerRoster.Build(Stock, customs);
        Assert.Equal(Stock.Length + 2, roster.Count);
        Assert.Equal("Aardvark", roster[Stock.Length].Name);
        Assert.Equal("Zephyr Mk2", roster[Stock.Length + 1].Name);
        Assert.All(new[] { roster[Stock.Length], roster[Stock.Length + 1] },
            row => Assert.True(row.IsCustom));
    }

    /// <summary>A custom row's node is its airframe's stock plane, and its store
    /// name rides along so the launch layer can tell the pick apart from the stock row.</summary>
    [Fact]
    public void ACustomRowFliesAsItsAirframesStockNodeAndKeepsItsName()
    {
        var def = new CustomPlaneDef { Name = "My Warhawk", Airframe = 10, Engine = 1 };
        var roster = PlanePickerRoster.Build(Stock, new[] { def });
        var row = roster[Stock.Length];
        Assert.Equal("player_warhawk", row.Node);
        Assert.Equal("My Warhawk", row.CustomName);
    }

    /// <summary>The export gate: a campaign aeroplane nobody has pressed EXPORT on is not offered,
    /// and clearing the marker is what lists it. Every plane without the marker is offered, which is
    /// what keeps a build written before the marker existed in the list.</summary>
    [Fact]
    public void ACampaignPlaneAwaitingExportIsNotOffered()
    {
        var def = new CustomPlaneDef { Name = "The Knave", Airframe = 5, Engine = 1, AwaitingExport = true };
        var hidden = PlanePickerRoster.Build(Stock, new[] { def });
        Assert.Equal(Stock.Length, hidden.Count);
        Assert.Equal(-1, PlanePickerRoster.IndexOf(hidden, "The Knave"));

        def.AwaitingExport = false;
        var listed = PlanePickerRoster.Build(Stock, new[] { def });
        Assert.Equal(Stock.Length + 1, listed.Count);
        Assert.Equal(Stock.Length, PlanePickerRoster.IndexOf(listed, "The Knave"));
    }

    /// <summary>Airframe ids 0-10 in the stat table's order (docs/org/hangar.md), the Hoplite
    /// being player_autogyro (the shipped data's two-names aircraft).</summary>
    [Theory]
    [InlineData(0, "player_autogyro")]
    [InlineData(1, "player_avenger")]
    [InlineData(2, "player_balmoral")]
    [InlineData(3, "player_bhawk")]
    [InlineData(4, "player_brigand")]
    [InlineData(5, "player_pfighter")]
    [InlineData(6, "player_fbrand")]
    [InlineData(7, "player_fury")]
    [InlineData(8, "player_kestrel")]
    [InlineData(9, "player_peacemaker")]
    [InlineData(10, "player_warhawk")]
    public void AirframeNodesMatchTheStatTableOrder(int airframe, string node) =>
        Assert.Equal(node, PlanePickerRoster.AirframeNode(airframe));

    /// <summary>An out-of-range id clamps like the def's own fields rather than throwing: a
    /// hand-edited save must never break a picker.</summary>
    [Fact]
    public void AirframeNodeClampsOutOfRangeIds()
    {
        Assert.Equal("player_autogyro", PlanePickerRoster.AirframeNode(-3));
        Assert.Equal("player_warhawk", PlanePickerRoster.AirframeNode(99));
    }

    /// <summary>The after-build auto-select's lookup: the just-built plane by name, case
    /// blind like the store's own duplicate-name policy; -1 when the roster lacks it.</summary>
    [Fact]
    public void IndexOfFindsACustomByNameAndOnlyACustom()
    {
        var roster = PlanePickerRoster.Build(Stock,
            new[] { new CustomPlaneDef { Name = "My Warhawk", Airframe = 10, Engine = 1 } });
        Assert.Equal(Stock.Length, PlanePickerRoster.IndexOf(roster, "my warhawk"));
        Assert.Equal(-1, PlanePickerRoster.IndexOf(roster, "Warhawk")); // the stock row is not a custom
        Assert.Equal(-1, PlanePickerRoster.IndexOf(roster, "No Such Plane"));
    }
}
