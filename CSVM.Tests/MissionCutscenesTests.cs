using System.IO;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The mission cutscene classifier (<c>docs/formats/anim-definitions/cutscenes.md</c>): which of
/// a mission's <c>ANIMATION_DEFINITION_FILE</c> entries sit under its <c>cutscenes\</c> directory,
/// and the <c>ANIMATION_NAME</c>s those files define. Input is
/// <c>fixtures/mission-cutscenes/</c>, whose mis_anim lists one cutscene file and the same ambient
/// file twice, once by path and once by bare name. The listed paths are Windows paths on every
/// host, which <see cref="GamePath"/> splits and <see cref="Path"/> splits on Windows only.
/// </summary>
public class MissionCutscenesTests
{
    [Fact]
    public void OnlyTheCutscenesDirectoryContributesItsAnimationNames()
    {
        Assert.Equal(new[] { "probe_drop", "probe_drop_cam" }, Load());
    }

    [Fact]
    public void ADefinitionFileOutsideThatDirectoryContributesNothing()
    {
        // probe_ambient.zrd is listed twice and readable both times; neither entry is a cutscene,
        // so the ambient loop it defines must not reach the host or the range gate.
        Assert.DoesNotContain("probe_ambient_loop", Load());
    }

    [Fact]
    public void AMissionWhoseReaderListsNoCutsceneIsEmpty()
    {
        var dir = TestData.TempDir();
        File.WriteAllText(Path.Combine(dir, MissionCutscenes.FileName),
            "[[\"ANIMATION_DEFINITIONS\",[\"ANIMATION_LIST\",[\"ANIMATION_DEFINITION_FILE\","
            + "[\"..\\\\data\\\\probe\\\\m01\\\\zrdr\\\\envmodels\\\\probe_ambient.zrd\"]]]]]");
        Assert.Empty(MissionCutscenes.AnimNames(dir));
    }

    [Fact]
    public void AListedCutscenePathResolvesToTheLeafTheExtractionStores()
    {
        // The literals are the point: a host Path call would split the backslashes on Windows only.
        Assert.Equal(
            new[] { "cabpickup.json", "climbcab.json", "cabwave.json" },
            MissionCutscenes.StoredNames(new[]
            {
                @"..\data\c1\m02\zrdr\cutscenes\cabpickup.zrd",
                "../data/c1/m02/zrdr/cutscenes/climbcab.zrd",
                @"..\data\c1\m02\zrdr/CUTSCENES\cabwave.zrd",
                @"..\data\c1\m02\zrdr\envmodels\lifesaver1.zrd",
                @"..\data\c1\m02\zrdr\cutscenes.zrd",
            }));
    }

    [Theory]
    [InlineData(@"..\data\common\zrdr\effects\huge_splash.zrd", "huge_splash", true)]
    [InlineData(@"..\data\c1\zrdr\envmodels\tr_boxcar1.zrd", "tr_boxcar1", false)]
    [InlineData("player-1.zrd.json", "player", false)]
    [InlineData("map_anims.json", "map_anims", false)]
    public void AListedPathAndTheFileItNamesShareOneStem(string path, string stem, bool shared)
    {
        Assert.Equal(stem, AnimProgram.StemOf(path));
        Assert.Equal(shared, AnimProgram.IsSharedPath(path));
    }

    [Fact]
    public void AMissionWithNoReaderAtAllIsEmptyRatherThanAThrow()
    {
        Assert.Empty(MissionCutscenes.AnimNames(TestData.TempDir()));
    }

    private static System.Collections.Generic.List<string> Load() =>
        MissionCutscenes.AnimNames(TestData.Fixture("mission-cutscenes"));
}
