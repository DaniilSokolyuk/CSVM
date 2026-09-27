using System.IO;
using CSVM.Session.Launch;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The extraction stamp's schema promise: a tree stamped at <see cref="ExtractionStamp.Schema"/>
/// is current, and one stamped below it is refused with the re-extract instruction.
/// </summary>
public class ExtractionStampTests
{
    [Fact]
    public void ATreeStampedAtTheSchemaThisBuildWritesIsNotBehind()
    {
        string root = Path.Combine(Path.GetTempPath(), "csvm-stamp-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "extracted"));
        try
        {
            Stamp(root, ExtractionStamp.Schema);
            Assert.False(ExtractionStamp.Behind(root, ExtractionStamp.Schema, out var reason), reason);

            // And the extraction made before the reader needed its new output is refused, naming
            // the re-run rather than opening a screen with nothing behind it.
            Stamp(root, ExtractionStamp.Schema - 1);
            Assert.True(ExtractionStamp.Behind(root, ExtractionStamp.Schema, out reason));
            Assert.Contains($"stamped schema={ExtractionStamp.Schema - 1}", reason);
            Assert.Contains("re-extract from the Extract screen", reason);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Stamp(string root, int schema) =>
        File.WriteAllText(
            Path.Combine(root, "extracted", "VERSION.json"),
            $"{{ \"schema\": {schema}, \"rof\": {{ \"movies\": 10 }} }}");
}
