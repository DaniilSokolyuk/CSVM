using System;
using System.Collections.Generic;
using CSVM.Flight;

namespace CSVM.UI;

/// <summary>One row of a human plane picker: a stock airframe, or a saved custom plane listed
/// after them. <paramref name="Node"/> is the planes.zbd root node a
/// launch builds; for a custom row that is its airframe's STOCK node, because building the
/// custom def into a flying aircraft is D32's; <paramref name="CustomName"/> is the store name
/// distinguishing the pick, carried into <c>LaunchMenu.PlayerChoice.CustomPlane</c> so D32 can
/// read it where the session is built.</summary>
public sealed record PickerPlane(string Name, string Node, string? CustomName = null)
{
    /// <summary>Whether this row is a saved custom plane rather than a stock airframe.</summary>
    public bool IsCustom => CustomName != null;
}

/// <summary>
/// The one roster every human plane picker draws: the 11 stock airframes in the launchscreen's
/// curated order, then the store's saved customs sorted by name, the original's 11+customs list
/// sizing (docs/org/hangar.md, count callback 1024). Engine-free: the launchscreen hands in its
/// own stock table and the store's listing, so the build rule tests without a menu instance.
/// </summary>
public static class PlanePickerRoster
{
    // Airframe id 0-10 (the stat table's row order: Hoplite, Hellhound, Balmoral, Bloodhawk,
    // Brigand, Devastator, Firebrand, Fury, Kestrel, Peacemaker, Warhawk) to the planes.zbd
    // node, the same id order HangarPaintPage.SkinPrefixes is keyed by. The Hoplite is
    // player_autogyro: the shipped data names that aircraft both ways.
    private static readonly string[] AirframeNodes =
    {
        "player_autogyro", "player_avenger", "player_balmoral", "player_bhawk", "player_brigand",
        "player_pfighter", "player_fbrand", "player_fury", "player_kestrel", "player_peacemaker",
        "player_warhawk",
    };

    /// <summary>The eleven stock nodes in airframe-id order. A network roster carries an airframe
    /// as its index into this list, so both peers have to read the one order.</summary>
    public static IReadOnlyList<string> StockAirframes => AirframeNodes;

    /// <summary>The stock node an airframe id flies as, clamped like the def's own fields.</summary>
    public static string AirframeNode(int airframe) =>
        AirframeNodes[Math.Clamp(airframe, 0, AirframeNodes.Length - 1)];

    /// <summary>The inverse of <see cref="AirframeNode"/>: the airframe id a stock player node
    /// flies as, or null when the node names none of the eleven (a surface hull's own
    /// library-root model has no airframe).</summary>
    public static int? AirframeOf(string node)
    {
        for (int i = 0; i < AirframeNodes.Length; i++)
        {
            if (string.Equals(AirframeNodes[i], node, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return null;
    }

    /// <summary>Builds the picker roster: every stock row in its given order, then one row per
    /// saved custom in the store's own (name-sorted) order. A campaign aeroplane nobody has
    /// exported yet is not offered (<see cref="CustomPlaneDef.AwaitingExport"/>).</summary>
    public static IReadOnlyList<PickerPlane> Build(
        IReadOnlyList<(string Name, string Node)> stock, IReadOnlyList<CustomPlaneDef> customs)
    {
        var rows = new List<PickerPlane>(stock.Count + customs.Count);
        foreach (var (name, node) in stock)
        {
            rows.Add(new PickerPlane(name, node));
        }

        foreach (var def in customs)
        {
            if (!def.AwaitingExport)
            {
                rows.Add(new PickerPlane(def.Name, AirframeNode(def.Airframe), def.Name));
            }
        }

        return rows;
    }

    /// <summary>The row a custom name sits at, or -1 when the roster has no such plane: the
    /// after-build auto-select's lookup (the original selects index 11, the first custom slot;
    /// ours selects the just-built plane by name).</summary>
    public static int IndexOf(IReadOnlyList<PickerPlane> roster, string customName)
    {
        for (int i = 0; i < roster.Count; i++)
        {
            if (roster[i].CustomName is { } name
                && string.Equals(name, customName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
