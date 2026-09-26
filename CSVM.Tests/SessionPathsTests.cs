using System.IO;
using CSVM;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// Extraction-path arithmetic: the per-chapter and per-mission layout, and the
/// unpacked-folder-beats-zip preference.
/// </summary>
public class SessionPathsTests
{
    [Fact]
    public void ChapterAndMissionPathsFollowTheExtractionLayout()
    {
        var root = TestData.TempDir();
        Assert.Equal(Path.Combine(root, "extracted", "C4", "texture.zip"),
            SessionPaths.ChapterTextures(root, "C4"));
        Assert.Equal(Path.Combine(root, "extracted", "C4", "gamez.zip"),
            SessionPaths.ChapterGamez(root, "C4"));
        Assert.Equal(Path.Combine(root, "extracted", "C4", "zrdr.zip"),
            SessionPaths.ChapterZrdr(root, "C4"));
        Assert.Equal(Path.Combine(root, "extracted", "C4", "IA1", "zrdr.zip"),
            SessionPaths.MissionZrdr(root, "C4", "IA1"));
    }

    /// <summary>The campaign sequence names its folders <c>c3</c>/<c>m01</c>; the extraction
    /// writes <c>C3/M01</c>. Compared as strings, since a Windows disk would find either.</summary>
    [Fact]
    public void ALowerCaseChapterAndMissionMapToTheCaseTheExtractionWrites()
    {
        var root = TestData.TempDir();
        Assert.Equal(Path.Combine(root, "extracted", "C3", "M01", "zrdr.zip"),
            SessionPaths.MissionZrdr(root, "c3", "m01"));
        Assert.Equal(Path.Combine(root, "extracted", "C1B", "gamez.zip"),
            SessionPaths.ChapterGamez(root, "c1b"));
        Assert.Equal(Path.Combine(root, "extracted", "C2B", "zrdr.zip"),
            SessionPaths.ChapterZrdr(root, "c2b"));
        Assert.Equal(Path.Combine(root, "extracted", "C5", "texture.zip"),
            SessionPaths.ChapterTextures(root, "c5"));
        Assert.Equal(
            (Path.Combine(root, "extracted", "C3", "cam_anim.zip"), Path.Combine(root, "extracted", "C3", "M01", "mis_anim.zip")),
            Mech3.AnimProgram.ArchivePaths(root, "c3", "m01"));
    }

    /// <summary>Every campaign mission's own folder names resolve to the path the extraction
    /// writes for that mission's archive, whatever case the install spells it in.</summary>
    [Fact]
    public void EveryCampaignMissionResolvesToWhereTheExtractionWritesIt()
    {
        var root = TestData.TempDir();
        var reader = Extraction.ZbdPlan.ModeFor("zrdr")!;
        for (int campaign = 1; campaign <= 8; campaign++)
        {
            for (int mission = 1; mission <= 5; mission++)
            {
                var named = new Mech3.CampaignMission(0, "", campaign, mission, "", false);
                string install = Path.Combine(named.ChapterFolder, named.MissionFolder.ToUpperInvariant(), "Zrdr.ZBD");
                Assert.Equal(
                    Path.Combine(root, "extracted", Extraction.ZbdPlan.OutputRelativePath(install, reader)),
                    SessionPaths.MissionZrdr(root, named.ChapterFolder, named.MissionFolder));
            }
        }
    }

    [Fact]
    public void AnUnpackedSiblingFolderWinsOverItsZip()
    {
        var root = TestData.TempDir();
        var chapter = Path.Combine(root, "extracted", "C4");
        Directory.CreateDirectory(Path.Combine(chapter, "gamez"));
        File.WriteAllText(Path.Combine(chapter, "gamez.zip"), "not really a zip");

        Assert.Equal(Path.Combine(chapter, "gamez"), SessionPaths.ChapterGamez(root, "C4"));
        // No folder for textures, so its zip path passes through verbatim.
        Assert.Equal(Path.Combine(chapter, "texture.zip"), SessionPaths.ChapterTextures(root, "C4"));
    }

    [Fact]
    public void PreferUnzippedIsPurePathArithmeticWhenNothingExists()
    {
        var missing = Path.Combine(TestData.TempDir(), "nowhere", "zrdr.zip");
        Assert.Equal(missing, SessionPaths.PreferUnzipped(missing));
    }

    [Fact]
    public void ForceZippedTakesTheZipEvenWhereTheUnpackedFolderExists()
    {
        var root = TestData.TempDir();
        var chapter = Path.Combine(root, "extracted", "C4");
        Directory.CreateDirectory(Path.Combine(chapter, "gamez"));
        File.WriteAllText(Path.Combine(chapter, "gamez.zip"), "not really a zip");
        try
        {
            SessionPaths.ForceZipped = true;
            Assert.Equal(Path.Combine(chapter, "gamez.zip"), SessionPaths.ChapterGamez(root, "C4"));
        }
        finally
        {
            SessionPaths.ForceZipped = false;
        }
    }

    [Fact]
    public void ForceZippedStillTakesTheFolderWhereNoZipWasEverExtracted()
    {
        var root = TestData.TempDir();
        var chapter = Path.Combine(root, "extracted", "C4");
        Directory.CreateDirectory(Path.Combine(chapter, "gamez"));
        try
        {
            // Asking for the export's asset shape must not refuse to start a tree that only ever
            // had the folder, the flag narrows the choice, it does not add a requirement.
            SessionPaths.ForceZipped = true;
            Assert.Equal(Path.Combine(chapter, "gamez"), SessionPaths.ChapterGamez(root, "C4"));
        }
        finally
        {
            SessionPaths.ForceZipped = false;
        }
    }

    [Fact]
    public void TheTopRtextureTierBeatsTheBaseTextureArchive()
    {
        var root = TestData.TempDir();
        var chapter = Path.Combine(root, "extracted", "C1");
        Directory.CreateDirectory(chapter);
        File.WriteAllText(Path.Combine(chapter, "texture.zip"), "x");
        File.WriteAllText(Path.Combine(chapter, "rtexture2.zip"), "x");
        File.WriteAllText(Path.Combine(chapter, "rtexture15.zip"), "x");
        // numeric, not lexicographic: 15 must beat 2 and 8
        File.WriteAllText(Path.Combine(chapter, "rtexture8.zip"), "x");

        Assert.Equal(Path.Combine(chapter, "rtexture15.zip"), SessionPaths.ChapterTextures(root, "C1"));

        // and the tier obeys the same unpacked-folder preference as everything else
        Directory.CreateDirectory(Path.Combine(chapter, "rtexture15"));
        Assert.Equal(Path.Combine(chapter, "rtexture15"), SessionPaths.ChapterTextures(root, "C1"));
    }
}
