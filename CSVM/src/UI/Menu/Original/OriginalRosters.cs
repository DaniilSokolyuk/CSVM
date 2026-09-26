using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.UI.Hangar;

namespace CSVM.UI.Menu.Original;

/// <summary>One chapter row of the Original Free Flight screen: the shared roster's code and the
/// label this screen writes for it.</summary>
public readonly record struct OriginalChapter(string Code, string Label);

/// <summary>One airframe row: the display name and the planes.zbd node the launch builds.</summary>
public readonly record struct OriginalAirframe(string Name, string Node);

/// <summary>
/// The two rosters the Original Free Flight screen lists. The chapters are the shared
/// <see cref="MenuChapters"/> roster with a short label per code; the airframes are the eleven
/// stock names in the string table's own order, resolved to nodes through the Instant Action
/// decode so the name-to-node map has one home. The saved custom planes are appended by
/// <see cref="OriginalPresentation.Roster"/> through the shared player setup's roster rule.
/// </summary>
public static class OriginalRosters
{
    private static readonly string[] AirframeNames =
    {
        "Autogyro", "Hellhound", "Balmoral", "Bloodhawk", "Brigand", "Devastator",
        "Firebrand", "Fury", "Kestrel", "Peacemaker", "Warhawk",
    };

    /// <summary>Every Free Flight chapter with its label, in the shared roster's order.</summary>
    public static IReadOnlyList<OriginalChapter> Chapters { get; } = BuildChapters();

    /// <summary>The eleven stock airframes with their nodes.</summary>
    public static IReadOnlyList<OriginalAirframe> Airframes { get; } = BuildAirframes();

    /// <summary>The roster both presentations pick from: the eleven stock airframes, then the
    /// saved customs, each flying its airframe's stock node.</summary>
    public static IReadOnlyList<MenuAircraft> Roster(IReadOnlyList<CSVM.Flight.Hangar.CustomPlaneDef> customs)
    {
        var stock = new List<(string Name, string Node)>(Airframes.Count);
        foreach (var airframe in Airframes)
        {
            stock.Add((airframe.Name, airframe.Node));
        }

        return PlayerSetupFeature.BuildRoster(stock, customs, PlanePickerRoster.AirframeNode);
    }

    private static IReadOnlyList<OriginalChapter> BuildChapters()
    {
        var rows = new List<OriginalChapter>();
        foreach (var chapter in MenuChapters.All)
        {
            rows.Add(new OriginalChapter(chapter.Code, ChapterLabel(chapter.Code)));
        }

        return rows;
    }

    private static IReadOnlyList<OriginalAirframe> BuildAirframes()
    {
        var rows = new List<OriginalAirframe>();
        foreach (string name in AirframeNames)
        {
            if (InstantAction.PlaneNodeFor(name) is { } node)
            {
                rows.Add(new OriginalAirframe(name, node));
            }
        }

        return rows;
    }

    // The regions by their place names, short enough for a list column at authored size.
    private static string ChapterLabel(string code) => code switch
    {
        "C1" => "Sea Haven (night)",
        "C1B" => "The Ocean",
        "C1C" => "Sea Haven (variant C)",
        "C2" => "Hollywood",
        "C2B" => "The Clouds",
        "C3" => "Hawaii",
        "C4" => "Rocky Mountains",
        "C5" => "New York",
        _ => code,
    };
}
