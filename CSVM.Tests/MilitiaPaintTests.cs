using System.Collections.Generic;
using System.IO;
using CSVM.Mech3;
using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// What a setup-screen militia decides on the Instant Action path: the paint, and only the paint.
/// The original builds every wave member from the plain AI def of its aircraft and writes the
/// screen's pattern, decals and colours over it, so these cases check the pattern lookup and not a
/// def lookup, which the engine never does here.
/// </summary>
public class MilitiaPaintTests
{
    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    private static string MessagesPath =>
        Path.Combine(TestData.ExtractedRoot!, "messages.json");

    [ExtractedDataFact]
    public void AMilitiaNamesItsPattern()
    {
        var patterns = Patterns();

        Assert.Equal("blackhat", MilitiaPaint.PatternForWave(patterns, "Black Hat Warhawk"));
        Assert.Equal("studio", MilitiaPaint.PatternForWave(patterns, "Studio Security Autogyro"));
        Assert.Equal("cccp", MilitiaPaint.PatternForWave(patterns, "Russian Devastator"));
    }

    // The pair that has no def at all, and the reason the paint cannot come from one: a Sacred Trust
    // Warhawk is selectable in the original and wears the colours (user, at the controls).
    [ExtractedDataFact]
    public void APairWithNoDefIsStillPainted()
    {
        var patterns = Patterns();

        Assert.Equal("sactrust", MilitiaPaint.PatternForWave(patterns, "Sacred Trust Warhawk"));
        Assert.Equal("sactrust", MilitiaPaint.PatternForWave(patterns, "Sacred Trust Hellhound"));
    }

    // The menu says "Hollywood Knight", the message table "Hollywood Knights". Same militia.
    [ExtractedDataFact]
    public void TheMilitiaHalfMatchesAcrossSingularAndPlural()
    {
        Assert.Equal("hollywd", MilitiaPaint.PatternForWave(Patterns(), "Hollywood Knight Firebrand"));
    }

    // A shipped ia.json wave carries an MSG_* key naming the aircraft alone, so no militia is named
    // and the member keeps its own skins rather than being dressed in someone else's colours.
    [ExtractedDataFact]
    public void AShippedWaveNameNamesNoPattern()
    {
        Assert.Null(MilitiaPaint.PatternForWave(Patterns(), "MSG_OBJ_IVARS_FIREBRAND"));
    }

    // Every militia the wave editor offers paints, except the two the install ships no colours for:
    // Fortune Hunter is the player's own (the livery picker's default covers it) and Broadway Bomber
    // has masks under no def at all.
    [ExtractedDataFact]
    public void EveryMenuMilitiaPaintsExceptTheTwoWithNoColoursShipped()
    {
        var patterns = Patterns();
        var catalog = PaintScheme.LoadCatalog(ZrdrPath);

        foreach (string militia in LaunchMenu.MilitiaNames())
        {
            foreach (string aircraft in LaunchMenu.AircraftFor(militia))
            {
                string? pattern = MilitiaPaint.PatternForWave(patterns, $"{militia} {aircraft}");
                if (pattern == null)
                {
                    Assert.True(militia is "Fortune Hunter" or "Broadway Bomber",
                        $"'{militia}' names no pattern and is not one of the two that ship none");
                    continue;
                }
                Assert.Contains(catalog, s => s.Pattern == pattern);
            }
        }
    }

    private static Dictionary<string, string> Patterns() =>
        MilitiaPaint.PatternByMilitia(ZrdrPath, Messages.Load(MessagesPath));
}
