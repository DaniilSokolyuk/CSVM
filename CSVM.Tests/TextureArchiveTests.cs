using System.IO;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The parts of the texture archive that are name arithmetic rather than pixels: the decal
/// numbering (<c>docs/formats/paint.md</c>) and the known-absent list. Decoding a texture needs
/// Godot's <c>Image</c>, so alpha classification and the truncated-name resolution stay with the
/// in-engine suites; only the index is exercised here, over a directory of empty files whose
/// names are the whole input.
/// </summary>
public class TextureArchiveTests
{
    [Fact]
    public void DecalIndexFindsTheZeroPaddedTwoDigitName()
    {
        using var archive = new TextureArchive(DecalDir());
        Assert.Equal("21probe_star", archive.FindByDecalIndex(21));
        Assert.Equal("07probe_mark", archive.FindByDecalIndex(7));
        Assert.Equal("00probe_zero", archive.FindByDecalIndex(0));
    }

    [Fact]
    public void AThirdDigitMeansItIsNotADecalAndTheLodTwinIsSkipped()
    {
        using var archive = new TextureArchive(DecalDir());
        // "213probe_notadecal" begins with "21" but its third character is a digit.
        Assert.NotEqual("213probe_notadecal", archive.FindByDecalIndex(21));
        Assert.NotEqual("21probe_star_1", archive.FindByDecalIndex(21));
    }

    [Fact]
    public void AnIndexWithNoDecalOrOutOfRangeGivesNull()
    {
        using var archive = new TextureArchive(DecalDir());
        Assert.Null(archive.FindByDecalIndex(42));
        Assert.Null(archive.FindByDecalIndex(-1));
        Assert.Null(archive.FindByDecalIndex(100));
    }

    [Fact]
    public void TexturesTheRetailDataItselfLacksAreFlaggedAsKnownAbsent()
    {
        // These render neutral instead of debug magenta; anything else missing stays loud.
        Assert.True(TextureArchive.IsKnownAbsent("pir_spinner"));
        Assert.True(TextureArchive.IsKnownAbsent("pir_spinner.t"));
        Assert.True(TextureArchive.IsKnownAbsent("barngrill.tif"));
        Assert.False(TextureArchive.IsKnownAbsent("probe_plain"));
    }

    [Fact]
    public void TheNamedFamiliesBlendWhateverTheirPixelsMeasure()
    {
        // One member per family, each measured above the binary-ness threshold and so scissored by
        // the pixel rule alone (analysis/alpha-classification/).
        Assert.True(TextureArchive.IsNamedSoftAlpha("firtree1"));      // trees
        Assert.True(TextureArchive.IsNamedSoftAlpha("shouse_rail01")); // rails
        Assert.True(TextureArchive.IsNamedSoftAlpha("eiffel1.tif"));   // lattice
        Assert.True(TextureArchive.IsNamedSoftAlpha("shore1_end"));    // coastline
    }

    [Fact]
    public void ATextureOutsideTheFamiliesIsLeftToThePixelRule()
    {
        // ⚠ A family is a name list, not a prefix: the Spruce Goose skins share four letters with
        // the `spruce` tree card and are not foliage.
        Assert.False(TextureArchive.IsNamedSoftAlpha("sprucegoose6"));
        Assert.False(TextureArchive.IsNamedSoftAlpha("cblock1"));
        // Already soft by its own pixels; naming it would decide nothing.
        Assert.False(TextureArchive.IsNamedSoftAlpha("eiffel2"));
    }

    [Fact]
    public void AnUndrawnTextureIsOnlyUndrawnWhereTheArchiveCannotResolveIt()
    {
        // C3's skydome names cloud1/cloud2 and C3 ships neither, so its two cards draw nothing.
        // ⚠ The name alone must never be enough: the seven chapters that DO ship the pair keep
        // drawing it, and this archive stands in for one that has no copy.
        using var archive = new TextureArchive(TestData.TempDir());
        Assert.True(archive.IsAbsentAndUndrawn("cloud1.tif"));
        Assert.True(archive.IsAbsentAndUndrawn("cloud2"));
        // Absent, but a name the original draws a neutral card for rather than nothing.
        Assert.False(archive.IsAbsentAndUndrawn("pir_spinner.tif"));
        Assert.False(archive.IsAbsentAndUndrawn("probe_plain"));
    }

    // The blend rule is the texture header's own render-flags word, which the extractor spells as
    // the `stretch` enum (docs/org/textures.md). Reading it is name-and-manifest arithmetic, so it
    // belongs here; what the shipped chapters actually carry is the `puffer-blend-flag` suite.

    [Fact]
    public void OnlyBitTwoOfTheRenderFlagsWordMeansAdditive()
    {
        using var archive = new TextureArchive(FlagDir());
        // Every non-additive spelling, including "Both", whose word is non-zero for the stretch
        // bits alone: a reader testing the word for zero would call it additive.
        Assert.False(archive.IsAdditive("probe_none"));
        Assert.False(archive.IsAdditive("probe_horizontal"));
        Assert.False(archive.IsAdditive("probe_vertical"));
        Assert.False(archive.IsAdditive("probe_both"));
        Assert.False(archive.IsAdditive("probe_unk8"));
        // Additive alone, and additive carried alongside both stretch bits (the fire flipbook's 7).
        Assert.True(archive.IsAdditive("probe_unk4"));
        Assert.True(archive.IsAdditive("probe_unk7"));
        Assert.Equal(3, archive.RenderFlags("probe_both"));
        Assert.Equal(7, archive.RenderFlags("probe_unk7"));
    }

    [Fact]
    public void AnUnknownTextureAlphaMixes()
    {
        using var archive = new TextureArchive(FlagDir());
        // The engine's own terminal fallback is a flagless image, so an unresolvable name mixes
        // rather than adding, the answer that cannot make a sprite glow where nothing should.
        Assert.Equal(0, archive.RenderFlags("probe_absent"));
        Assert.False(archive.IsAdditive("probe_absent"));
        // A material name arrives truncated and extensioned; the flags follow the resolved name.
        Assert.True(archive.IsAdditive("probe_unk7.tif"));
    }

    [Fact]
    public void AFrameListCarriesOneVerdictPerFrame()
    {
        using var archive = new TextureArchive(FlagDir());
        // Blend belongs to the texture, so a flipbook crossing from a flagged frame to an unflagged
        // one changes blend under the particle rather than picking one verdict for the emitter.
        var frames = new[] { "probe_unk7", "probe_unk4", "probe_none", "probe_both" };
        Assert.Equal(new[] { true, true, false, false },
            System.Array.ConvertAll(frames, archive.IsAdditive));
    }

    // A disposed archive refuses a read from a folder exactly as it does from a zip. The battery
    // reads unpacked folders and a player's install reads zips, so any leniency in the folder shape
    // hides a closed-archive read from every suite.

    [Fact]
    public void AFolderArchiveRefusesAReadAfterDisposeLikeAZip()
    {
        var folder = new TextureArchive(DecalDir());
        Assert.NotNull(folder.ReadPngBytes("probe_plain"));
        folder.Dispose();
        Assert.Throws<System.ObjectDisposedException>(() => folder.ReadPngBytes("probe_plain"));
    }

    [Fact]
    public void AZipArchiveRefusesAReadAfterDispose()
    {
        string zip = Path.Combine(TestData.TempDir(), "texture.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(DecalDir(), zip);
        var archive = new TextureArchive(zip);
        Assert.NotNull(archive.ReadPngBytes("probe_plain"));
        archive.Dispose();
        Assert.Throws<System.ObjectDisposedException>(() => archive.ReadPngBytes("probe_plain"));
    }

    [Fact]
    public void AnArchiveOverAnEmptyDirectoryHasNoMissesYet()
    {
        using var archive = new TextureArchive(TestData.TempDir());
        Assert.Empty(archive.MissingTextures);
    }

    // The census colour is name arithmetic too, a hash, no engine, and the whole instrument
    // rests on it giving the same answer in every process. These lock that down.

    [Fact]
    public void ACensusColourIsAFunctionOfTheNameAlone()
    {
        // Recorded values, not a recomputation: a self-comparison would pass on any hash and could
        // never catch the map silently changing under a refactor.
        Assert.Equal("a1ffa0", Hex(TextureDropIn.ColorForName("lkzepskin")));
        Assert.Equal("ddffb6", Hex(TextureDropIn.ColorForName("firtree1")));
        Assert.Equal("c9d0ff", Hex(TextureDropIn.ColorForName("grass1")));
        // Case and extension are not part of the identity: the material's stored name is a
        // truncated, arbitrarily-cased version of the PNG's.
        Assert.Equal(TextureDropIn.ColorForName("lkzepskin"), TextureDropIn.ColorForName("LKZepSkin"));
    }

    [Fact]
    public void EveryCensusColourIsFullBrightnessAndFarFromGrey()
    {
        for (int i = 0; i < 1000; i++)
        {
            var c = TextureDropIn.ColorForName($"probe_texture_{i}");
            Assert.True(c.R8 == 255 || c.G8 == 255 || c.B8 == 255, $"probe_texture_{i} is not full brightness: {Hex(c)}");
            // Greyness is a LINEAR-space property, because that is where the classifier measures:
            // one channel must sit at most 0.9 of the brightest, which is 0.1 of chromaticity,
            // twice the whole match tolerance away from white.
            var lin = c.SrgbToLinear();
            float max = System.Math.Max(lin.R, System.Math.Max(lin.G, lin.B));
            float min = System.Math.Min(lin.R, System.Math.Min(lin.G, lin.B));
            Assert.True(min / max <= 0.91f, $"probe_texture_{i} is nearly grey in linear space: {Hex(c)}");
        }
    }

    [Fact]
    public void CollisionsAreRareEnoughToBeReportableRatherThanRoutine()
    {
        // Locks the low measured collision rate; a palette shrink would blow straight through it.
        var seen = new System.Collections.Generic.HashSet<string>();
        for (int i = 0; i < 1000; i++)
        {
            seen.Add(Hex(TextureDropIn.ColorForName($"probe_texture_{i}")));
        }
        Assert.True(seen.Count >= 985, $"only {seen.Count} distinct colours for 1000 names");
    }

    private static string DecalDir()
    {
        var dir = TestData.TempDir();
        foreach (var name in new[]
                 {
                     "00probe_zero.png",
                     "07probe_mark.png",
                     "21probe_star.png",
                     "21probe_star_1.png",   // the half-size LOD twin, not the decal
                     "213probe_notadecal.png",
                     "probe_plain.png",
                 })
        {
            File.WriteAllBytes(Path.Combine(dir, name), System.Array.Empty<byte>());
        }
        return dir;
    }

    // A texture directory plus the extraction manifest's own shape, one texture per `stretch`
    // spelling. The PNGs stay empty: nothing here decodes a pixel.
    private static string FlagDir()
    {
        var dir = TestData.TempDir();
        var infos = new System.Text.StringBuilder();
        foreach (var spelling in new[] { "None", "Horizontal", "Vertical", "Both", "Unk4", "Unk7", "Unk8" })
        {
            var name = "probe_" + spelling.ToLowerInvariant();
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), System.Array.Empty<byte>());
            if (infos.Length > 0)
            {
                infos.Append(',');
            }
            infos.Append($"{{\"name\":\"{name}\",\"alpha\":\"None\",\"stretch\":\"{spelling}\"}}");
        }
        File.WriteAllText(Path.Combine(dir, "manifest.json"), $"{{\"texture_infos\":[{infos}]}}");
        return dir;
    }

    private static string Hex(Godot.Color c) => $"{c.R8:x2}{c.G8:x2}{c.B8:x2}";
}
