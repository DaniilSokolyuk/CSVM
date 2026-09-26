using System.IO;
using CSVM.Tooling;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Every player airframe has a firepoint/pylon rig in <c>planes.zbd</c>, against the retail
/// install (engine-free: <c>Probes.Markers</c> touches no
/// live Node). Not to be confused with <see cref="MarkerRigTests"/>, which covers
/// <c>MarkerRig</c>'s own extraction/classification logic on a hand-authored fixture.
/// </summary>
public class MarkersRigTests
{
    private const int PlayerAirframes = 11;

    private static string PlanesGamezPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "planes.zip"));

    [ExtractedDataFact]
    public void EveryPlayerAirframeHasAFirepointPylonRig()
    {
        var r = Probes.Markers(PlanesGamezPath, "");
        Assert.True(r.Error == null, $"planes gamez loads error={r.Error ?? "-"}");
        Assert.Equal(PlayerAirframes, r.Requested);
        Assert.Equal(PlayerAirframes, r.Done);
        Assert.Empty(r.Missing);
    }
}
