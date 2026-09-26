using System.IO;
using CSVM.Flight.Airframe;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The name a targeting readout prints for an aircraft. It is AUTHORED, not composed: a militia def
/// carries a <c>title</c> message key of its own and the string table resolves it, which is the
/// difference between "Kestrel" and "Medusa Kestrel". Pinned against the shipped install rather
/// than against strings copied into the test.
/// </summary>
public class PlaneStatsTitleTests
{
    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    private static string MessagesPath =>
        Path.Combine(TestData.ExtractedRoot!, "messages.json");

    // The item's own case: the militia def authors the key and the table has the name behind it.
    [ExtractedDataFact]
    public void AMilitiaDefAuthorsItsOwnDisplayTitle()
    {
        var stats = PlaneStats.LoadForAi(ZrdrPath, "player_kestrel", "medkestrel");

        Assert.Equal("MSG_VEH_MEDUSA_KESTREL", stats.AiTitleKey);
        Assert.Equal("Medusa Kestrel", Messages.Load(MessagesPath).Get(stats.AiTitleKey!));
    }

    // Resolved, it is what the marker prints; unresolved, the readout keeps the derivation rather
    // than showing a raw message key.
    [ExtractedDataFact]
    public void TheResolvedTitleIsWhatTheReadoutPrints()
    {
        var stats = PlaneStats.LoadForAi(ZrdrPath, "player_kestrel", "medkestrel");

        Assert.Equal("Kestrel", PlaneRoster.PlaneDisplayName(stats));

        stats.AiTitle = Messages.Load(MessagesPath).Get(stats.AiTitleKey!);
        Assert.Equal("Medusa Kestrel", PlaneRoster.PlaneDisplayName(stats));
    }

    // A def with no title of its own takes the airframe's, so a plain AI Kestrel is still a Kestrel
    // and the militia half is genuinely the def's own contribution.
    [ExtractedDataFact]
    public void ADefWithoutOneInheritsTheAirframesTitle()
    {
        var stats = PlaneStats.LoadForAi(ZrdrPath, "player_kestrel");
        stats.AiTitle = Messages.Load(MessagesPath).Get(stats.AiTitleKey!);

        Assert.Equal("MSG_VEH_KESTREL", stats.AiTitleKey);
        Assert.Equal("Kestrel", PlaneRoster.PlaneDisplayName(stats));
    }

    // The player's own load is untouched: no AI chain, no title, and the marker for another human
    // in a --vs pane reads exactly what it read before.
    [ExtractedDataFact]
    public void APlayerLoadCarriesNoTitleAtAll()
    {
        var stats = PlaneStats.Load(ZrdrPath, "player_kestrel");

        Assert.Null(stats.AiTitleKey);
        Assert.Null(stats.AiTitle);
        Assert.Equal("Kestrel", PlaneRoster.PlaneDisplayName(stats));
    }
}
