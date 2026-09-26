using System;
using System.IO;
using System.Text;
using CSVM.Tooling;
using Xunit;

namespace CSVM.Tests;

/// <summary>Writes the Bloodhawk's per-step pull readout (<see cref="Probes.PullToLimit"/>) with
/// the AOA window spent and held off, side by side, to <c>CSVM_PULL_OUT</c> (default: the temp
/// folder's <c>flight-pull.txt</c>). It is the instrument behind docs/org/flightModel.md, "The α a
/// full pull holds", and it asserts nothing about the numbers: the doc section owns the reading.</summary>
public class ZzPullInstrument
{
    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    [ExtractedDataFact]
    public void DumpTheBloodhawkPull()
    {
        string outPath = Environment.GetEnvironmentVariable("CSVM_PULL_OUT")
                         ?? Path.Combine(Path.GetTempPath(), "flight-pull.txt");
        var sb = new StringBuilder();
        foreach (float seam in new[] { 1f, 0f })
        {
            sb.AppendLine(Probes.PullToLimit(ZrdrPath, "player_bhawk", 200f, 20f, seam));
            sb.AppendLine();
        }

        // The diagnostic: the same pull with the window live and the lag at the executable's
        // compiled fallback instead of the authored 0.75. A lead for a live read, not a plant.
        sb.AppendLine(Probes.PullToLimit(ZrdrPath, "player_bhawk", 200f, 20f, 1f, liftAccelRate: 1.2f));

        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Assert.True(File.Exists(outPath));
    }
}
