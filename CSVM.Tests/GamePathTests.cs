using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// <see cref="GamePath"/>: a path out of game data splits on both separators whatever the host.
/// Every expectation is a literal, never a <see cref="Path"/> call, because <see cref="Path"/> is
/// the host-dependent thing under test. Windows splits <c>\</c> and Linux does not.
/// </summary>
public class GamePathTests
{
    [Theory]
    [InlineData(@"..\data\c1\m02\zrdr\cutscenes\cabpickup.zrd", "cabpickup.zrd")]
    [InlineData("../data/c1/m02/zrdr/cutscenes/cabpickup.zrd", "cabpickup.zrd")]
    [InlineData(@"..\data/c1\m02/cabpickup.zrd", "cabpickup.zrd")]
    [InlineData(@"z:\crimsonrun\data\common\vessels\", "")]
    [InlineData("data/common/", "")]
    [InlineData(@"C:\cabpickup.zrd", "cabpickup.zrd")]
    [InlineData("cabpickup.zrd", "cabpickup.zrd")]
    [InlineData("", "")]
    public void TheFileNameIsWhatFollowsTheLastSeparatorOfEitherKind(string path, string expected)
    {
        Assert.Equal(expected, GamePath.FileName(path));
    }

    [Fact]
    public void ADriveLetterIsNotASegment()
    {
        Assert.Equal("c:cabpickup.zrd", GamePath.FileName("c:cabpickup.zrd"));
    }

    [Theory]
    [InlineData(@"..\data\c1\m02\zrdr\cutscenes\cabpickup.zrd", "cutscenes", true)]
    [InlineData("../data/c1/m02/zrdr/CutScenes/cabpickup.zrd", "cutscenes", true)]
    [InlineData(@"..\data\common\zrdr\effects\huge_splash.zrd", "common/zrdr", true)]
    [InlineData(@"..\data/common\zrdr/map_anims.zrd", @"common\zrdr", true)]
    [InlineData(@"..\data\c1\zrdr\common\zrdr_x\a.zrd", "common/zrdr", false)]
    [InlineData(@"..\data\c1\m02\zrdr\cutscenes.zrd", "cutscenes", false)]
    [InlineData(@"..\data\c1\m02\zrdr\my_cutscenes\a.zrd", "cutscenes", false)]
    [InlineData("cutscenes", "cutscenes", false)]
    [InlineData(@"cutscenes\a.zrd", "cutscenes", true)]
    public void AFolderMatchesWholeFolderSegmentsAndNeverTheFileName(string path, string folder, bool expected)
    {
        Assert.Equal(expected, GamePath.HasFolder(path, folder));
    }

    [Fact]
    public void NoSourceNormalisesAPathToBackslashesForTheHostToSplit()
    {
        // Replace('/', '\\') feeds a game path to Path, which splits it on Windows only.
        string src = Path.Combine(TestData.RepoRoot, "CSVM", "src");
        Assert.True(Directory.Exists(src), src);
        var toBackslash = new Regex(@"Replace\(\s*'/'\s*,\s*'\\\\'\s*\)");
        var hits = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => toBackslash.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();
        Assert.Empty(hits);
    }
}
