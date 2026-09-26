using System;
using System.IO;
using System.Text;
using CSVM.Tooling;
using Xunit;

namespace CSVM.Tests;

/// <summary>Writes the autogyro's and the Bloodhawk's stall entries (<see cref="Probes.StallEntry"/>)
/// nose-high and nose-low, on the player path and on the AI path, to <c>CSVM_STALL_OUT</c> (default:
/// the temp folder's <c>flight-stall.txt</c>). It is the instrument behind docs/org/flightModel.md,
/// "Why the autogyro's low-speed nose-down is softer", and it asserts nothing about the numbers: the
/// doc section owns the reading.</summary>
public class ZzAutogyroStallInstrument
{
    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    [ExtractedDataFact]
    public void DumpTheAutogyroAndBloodhawkStalls()
    {
        string outPath = Environment.GetEnvironmentVariable("CSVM_STALL_OUT")
                         ?? Path.Combine(Path.GetTempPath(), "flight-stall.txt");
        var sb = new StringBuilder();
        foreach (string plane in new[] { "player_autogyro", "player_bhawk" })
        {
            foreach (float noseDeg in new[] { 30f, -20f })
            {
                sb.AppendLine(Probes.StallEntry(ZrdrPath, plane, 70f, noseDeg, 20f, aiPath: false));
                sb.AppendLine();
                sb.AppendLine(Probes.StallEntry(ZrdrPath, plane, 70f, noseDeg, 20f, aiPath: true));
                sb.AppendLine();
            }
        }

        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Assert.True(File.Exists(outPath));
    }
}
