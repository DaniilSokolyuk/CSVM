using System.IO;
using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The rule the launcher branches on before it shows a menu, and the sentence the screen and the
/// log line share. Only the engine-free half is covered: building the layer needs a scene tree,
/// while what decides between the menu and the dead end does not.
/// </summary>
public class NoGameDataScreenTests
{
    [Fact]
    public void ARootWithNoExtractedDirectoryIsMissing()
    {
        using var root = new TempDir();
        Assert.True(NoGameDataScreen.Missing(root.Path));
    }

    [Fact]
    public void AnEmptyExtractedDirectoryIsMissingToo()
    {
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "extracted"));
        Assert.True(NoGameDataScreen.Missing(root.Path));
    }

    [Fact]
    public void AnExtractionWithAnythingInItIsNotMissing()
    {
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "extracted"));
        File.WriteAllText(Path.Combine(root.Path, "extracted", "planes.zip"), "not a real archive");
        Assert.False(NoGameDataScreen.Missing(root.Path));
    }

    [Fact]
    public void AStaleOrUnreadableStampIsNotThisScreensQuestion()
    {
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "extracted"));
        File.WriteAllText(Path.Combine(root.Path, "extracted", "VERSION.json"), "{ not json");
        Assert.False(NoGameDataScreen.Missing(root.Path));
    }

    [Fact]
    public void EachReaderIsToldTheExtractionTheirOwnBuildShips()
    {
        Assert.Contains("Extract.cmd", NoGameDataScreen.Instruction(exported: true));
        Assert.Contains("ExtractAssets.ps1", NoGameDataScreen.Instruction(exported: false));
        Assert.DoesNotContain("Extract.cmd", NoGameDataScreen.Instruction(exported: false));
    }

    private sealed class TempDir : System.IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CSVM", "no-game-data-" + System.Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
