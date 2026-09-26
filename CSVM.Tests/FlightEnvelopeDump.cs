using System;
using System.IO;
using System.Text;
using CSVM.Tooling;
using Xunit;

namespace CSVM.Tests;

/// <summary>The eleven-airframe flight dump, out of the test host so a plant change can be A/B'd
/// without launching the engine. Same report <c>--dump-flight=all</c> prints, so a hash taken
/// either way is the same hash. Writes to <c>CSVM_DUMP_OUT</c> (default: the temp folder's
/// <c>flight-dump.txt</c>) and asserts nothing about the numbers: the diff is the instrument.</summary>
public class FlightEnvelopeDump
{
    private static string ZrdrPath =>
        SessionPaths.PreferUnzipped(Path.Combine(TestData.ExtractedRoot!, "zrdr.zip"));

    [ExtractedDataFact]
    public void DumpEveryAirframe()
    {
        string outPath = Environment.GetEnvironmentVariable("CSVM_DUMP_OUT")
                         ?? Path.Combine(Path.GetTempPath(), "flight-dump.txt");
        var r = Probes.FlightEnvelopeAll(ZrdrPath);
        File.WriteAllText(outPath, r.Text, new UTF8Encoding(false));
        Assert.Null(r.Error);
        Assert.True(File.Exists(outPath));
    }
}
