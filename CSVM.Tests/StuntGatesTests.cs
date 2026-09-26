using System.IO;
using System.Linq;
using CSVM.Flight.Modes;
using CSVM.Mech3;
using CSVM.Testing;
using CSVM.Utils;
using Godot;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// C4 Danger Zones require their authored entry and exit apertures, not a marker sphere. This runs
/// engine-free: <see cref="StuntMission"/> logs through <see cref="Log"/> rather than touching the
/// engine. With no sink installed that falls through to the real
/// <c>GD.Print</c>, which crashes the whole test host outside the engine (the
/// <c>StuntRaceTests</c> precedent), which the process-wide no-op sink in
/// <c>TestHostLogSink</c> covers, so no per-test ceremony is needed here.
/// </summary>
public class StuntGatesTests
{
    private const string Chapter = "C4";

    private static string GamezPath => SessionPaths.ChapterGamez(TestData.DataRoot!, Chapter);

    private static string MissionPath => SessionPaths.MissionZrdr(TestData.DataRoot!, Chapter, "IA1");

    private static string MessagesPath => Path.Combine(TestData.ExtractedRoot!, "messages.json");

    [ExtractedDataFact]
    public void C4DangerZonesRequireBothGatesInOrderNotAMarkerSphere()
    {
        Run();
    }

    private static void Run()
    {
        var mission = StuntMission.Load(GameZ.Load(GamezPath), MissionPath, Messages.Load(MessagesPath));
        Assert.True(mission != null, "C4 IA1 loads authored Danger Zone gates");
        if (mission == null)
            return;
        Assert.Equal(14, mission.TotalCount);
        var zone = mission.Zones.FirstOrDefault(z => z.PathName == "dzpath14");
        Assert.True(zone != null, "C4 dzpath14 resolves despite its route being polygon 2");
        if (zone == null)
            return;

        var separated = mission.Zones.FirstOrDefault(
            z => z.GreenGate.Center.DistanceTo(z.RedGate.Center) > 100f);
        Assert.True(separated != null, "C4 has a Danger Zone with separated gate pair");
        if (separated == null)
            return;
        Cross(mission, separated.RedGate);
        Assert.False(separated.Completed, "C4 red gate alone does not score");
        Cross(mission, separated.GreenGate);
        Assert.True(separated.Completed, "C4 scores after both gates in red-to-green order");

        mission.Reset();
        var side = zone.GreenGate.Normal.Cross(Vector3.Up);
        if (side.LengthSquared() < 1e-4f)
            side = zone.GreenGate.Normal.Cross(Vector3.Right);
        side = side.Normalized() * 10000f;
        mission.Update(zone.GreenGate.Center + side - zone.GreenGate.Normal * 20f);
        mission.Update(zone.GreenGate.Center + side + zone.GreenGate.Normal * 20f);
        Assert.False(zone.Completed, "C4 dzpath14 plane crossing beside the aperture does not score");
    }

    private static void Cross(StuntMission mission, StuntGate gate)
    {
        mission.Update(gate.Center - gate.Normal * 20f);
        mission.Update(gate.Center + gate.Normal * 20f);
    }
}
