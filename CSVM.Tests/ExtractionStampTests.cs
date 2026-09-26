using System.IO;
using System.Text.RegularExpressions;
using CSVM.Session.Launch;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The extraction stamp's schema promise: the number this build refuses a tree below, and the
/// number the two extraction scripts write into <c>VERSION.json</c>, are one number in three
/// places. A comment on each of the three cannot fail, and a script left behind stamps a tree this
/// build then refuses, so the three are read and compared here instead.
/// </summary>
public class ExtractionStampTests
{
    [Theory]
    [InlineData("ExtractAssets.ps1")]
    [InlineData("ExtractRof.ps1")]
    public void TheScriptStampsTheSchemaThisBuildReads(string script)
    {
        string path = Path.Combine(TestData.RepoRoot, script);
        Assert.True(File.Exists(path), path);

        var written = Regex.Match(File.ReadAllText(path), @"\$StampSchema\s*=\s*(\d+)");

        Assert.True(written.Success, $"{script} sets no $StampSchema");
        Assert.Equal(ExtractionStamp.Schema, int.Parse(written.Groups[1].Value));
    }

    [Fact]
    public void ATreeStampedAtTheSchemaTheScriptsWriteIsNotBehind()
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
            Assert.Contains("re-run ExtractAssets.ps1 and ExtractRof.ps1", reason);
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
