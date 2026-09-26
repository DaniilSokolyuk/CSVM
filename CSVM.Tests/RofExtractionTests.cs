using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CSVM.Extraction;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>The whole <see cref="RofExtraction"/> run on a fixture install. It covers where each
/// output lands, the up-to-date skip and its override, and the movie copy's rules. One check keeps
/// <c>CSVM.Extraction</c> free of engine types. Three read the player's install when present.
/// </summary>
public class RofExtractionTests
{
    [Fact]
    public void ARunWritesEveryOutputWhereTheScriptDid()
    {
        var install = FixtureInstall();
        string output = Path.Combine(TestData.TempDir(), "rof");

        var result = RofExtraction.Run(install.Request(output));

        Assert.True(File.Exists(Path.Combine(output, "ASSETS", "GRAPHICS", "FORTUNE", "KES_WING.BM")));
        Assert.True(File.Exists(Path.Combine(output, "ASSETS", "GRAPHICS", "FORTUNE", "KES_WING.png")));
        Assert.True(File.Exists(Path.Combine(output, "ASSETS", "GRAPHICS", "FORTUNE", "KES_WING_mask.png")));
        Assert.True(File.Exists(Path.Combine(output, "_crimptch", "ASSETS", "SCRIPTS", "AIRFRAME.SCRIPT")));
        Assert.True(File.Exists(Path.Combine(output, "ASSETS", "GRAPHICS", "MPG", "CrimFlag.MPG")));
        Assert.True(File.Exists(Path.Combine(output, "menu_layout.json")));
        Assert.Equal(RofArchiveOutcome.Extracted, result.Archives[0].Outcome);
        Assert.Equal(1, result.Archives[0].DecodedTextures);
        Assert.Equal(1, result.Archives[1].Files);
        Assert.Equal(3, result.StringRows);

        var strings = UiStrings.Parse(File.ReadAllText(Path.Combine(output, "ui_strings.json")));
        Assert.Equal("Kestrel", strings.Text(16));
        Assert.Equal("IMP36", strings.Face(16));
    }

    [Fact]
    public void TheShadingPngHoldsTheBmShadingPlane()
    {
        var install = FixtureInstall();
        string output = Path.Combine(TestData.TempDir(), "rof");

        RofExtraction.Run(install.Request(output));

        var png = PngImage.TryLoad(Path.Combine(output, "ASSETS", "GRAPHICS", "FORTUNE", "KES_WING.png"))!;
        var bm = BmTexture.TryDecode(File.ReadAllBytes(Path.Combine(output, "ASSETS", "GRAPHICS", "FORTUNE", "KES_WING.BM")))!;
        Assert.Equal(bm.Width, png.Width);
        Assert.Equal(bm.Shading[3], png.Rgba[4]);
    }

    [Fact]
    public void RowsJoinTheirSymbolAndLanguiWinsTheMenuTextJoin()
    {
        var install = FixtureInstall();
        string output = Path.Combine(TestData.TempDir(), "rof");

        RofExtraction.Run(install.Request(output));

        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "ui_strings.json")));
        var rows = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(new[] { "langui", "langui", "language" }, rows.Select(r => r.GetProperty("dll").GetString()));
        Assert.Equal("IDS_KESTREL", rows[0].GetProperty("symbol").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("symbol").ValueKind);

        string menu = File.ReadAllText(Path.Combine(output, "menu_layout.json"));
        Assert.Contains("Kestrel", menu, StringComparison.Ordinal);
        Assert.DoesNotContain("shadowed", menu, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUpToDateArchiveIsSkippedUnlessForced()
    {
        var install = FixtureInstall();
        string output = Path.Combine(TestData.TempDir(), "rof");
        RofExtraction.Run(install.Request(output));
        File.SetLastWriteTimeUtc(install.BaseArchive, DateTime.UtcNow.AddDays(-1));
        File.Delete(Path.Combine(output, "ASSETS", "LAYOUT.CSV"));

        var again = RofExtraction.Run(install.Request(output));
        Assert.Equal(RofArchiveOutcome.UpToDate, again.Archives[0].Outcome);
        Assert.False(File.Exists(Path.Combine(output, "ASSETS", "LAYOUT.CSV")));

        var forced = RofExtraction.Run(install.Request(output) with { Force = true });
        Assert.Equal(RofArchiveOutcome.Extracted, forced.Archives[0].Outcome);
        Assert.True(File.Exists(Path.Combine(output, "ASSETS", "LAYOUT.CSV")));
    }

    [Fact]
    public void AnAbsentInputIsSkippedAndLoggedRatherThanRefused()
    {
        string output = Path.Combine(TestData.TempDir(), "rof");
        var log = new List<string>();

        var result = RofExtraction.Run(new RofExtractionRequest(null, null, null, null, null, output), log.Add);

        Assert.All(result.Archives, a => Assert.Equal(RofArchiveOutcome.Absent, a.Outcome));
        Assert.False(result.Movies.SourceFound);
        Assert.Equal(0, result.StringRows);
        Assert.Null(result.MenuLayout);
        Assert.Contains(log, l => l.StartsWith("SKIP crimson.rof", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("SKIP langui.dll", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(output, "ui_strings.json")));
    }

    [Fact]
    public void MoviesKeepTheirSpellingSkipAMatchingLengthAndNameWhatIsMissing()
    {
        string source = TestData.TempDir();
        string dest = Path.Combine(TestData.TempDir(), "MPG");
        File.WriteAllBytes(Path.Combine(source, "CHAP0.MPG"), new byte[10]);
        File.WriteAllBytes(Path.Combine(source, "zipper.mpg"), new byte[20]);
        File.WriteAllBytes(Path.Combine(source, "notes.txt"), new byte[1]);

        var first = MovieCopy.Run(source, dest);
        Assert.Equal(2, first.Copied);
        Assert.Equal(30, first.BytesCopied);
        Assert.True(File.Exists(Path.Combine(dest, "CHAP0.MPG")));
        Assert.False(File.Exists(Path.Combine(dest, "notes.txt")));
        Assert.Equal(8, first.Missing.Count);
        Assert.DoesNotContain("chap0.mpg", first.Missing);

        // A target at the source's length is current; a read-only one of another length is replaced.
        File.WriteAllBytes(Path.Combine(dest, "zipper.mpg"), new byte[3]);
        File.SetAttributes(Path.Combine(dest, "zipper.mpg"), FileAttributes.ReadOnly);
        var second = MovieCopy.Run(source, dest);
        Assert.Equal(1, second.AlreadyCurrent);
        Assert.Equal(1, second.Copied);
        Assert.Equal(2, second.Present);
        Assert.Equal(20, new FileInfo(Path.Combine(dest, "zipper.mpg")).Length);
    }

    [Fact]
    public void ExtractionTypesReferenceNoEngineType()
    {
        var violations = AssemblyDependencyScan.Violations(
            Path.Combine(AppContext.BaseDirectory, "CSVM.dll"),
            ns => ns == "CSVM.Extraction",
            name => name.StartsWith("Godot.", StringComparison.Ordinal));

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    /// <summary>The shipped archive's census, which docs/formats/rof.md records: every member
    /// inflates to its declared size and every <c>.BM</c> decodes.</summary>
    [InstallFact]
    public void TheInstalledArchiveInflatesWholeAndEveryBmDecodes()
    {
        byte[] rof = File.ReadAllBytes(Path.Combine(InstallFactAttribute.Assets!, "crimson.rof"));

        var entries = RofArchive.Walk(rof).ToList();
        var files = entries.Where(e => !e.IsDirectory).ToList();
        int textures = 0;
        foreach (var file in files)
        {
            byte[] data = RofArchive.ReadMember(rof, file);
            if (file.Path.EndsWith(".BM", StringComparison.OrdinalIgnoreCase) && BmTexture.TryDecode(data) != null)
            {
                textures++;
            }
        }

        Assert.Equal(21, entries.Count - files.Count);
        Assert.Equal(846, files.Count);
        Assert.Equal(184, textures);
    }

    [InstallFact]
    public void TheInstalledStringTablesHoldTheirDocumentedCounts()
    {
        string binaries = Path.Combine(InstallFactAttribute.Assets!, "BINARIES");

        Assert.Equal(1247, PeStringTable.Read(File.ReadAllBytes(Path.Combine(binaries, "langui.dll"))).Count);
        Assert.Equal(36, PeStringTable.Read(File.ReadAllBytes(Path.Combine(binaries, "language.dll"))).Count);
    }

    /// <summary>The whole run against the player's install, into the folder
    /// <c>CSVM_ROF_EXTRACT_TO</c> names. Opt-in, since it writes about 200 MB; it is how the output
    /// is compared with a tree the extraction script wrote.</summary>
    [InstallFact(OptInVariable = "CSVM_ROF_EXTRACT_TO")]
    public void ExtractTheInstallIntoTheNamedFolder()
    {
        string assets = InstallFactAttribute.Assets!;
        string binaries = Path.Combine(assets, "BINARIES");
        var request = new RofExtractionRequest(
            Path.Combine(assets, "crimson.rof"),
            Path.Combine(assets, "crimptch.rof"),
            Path.Combine(assets, "GRAPHICS", "MPG"),
            Path.Combine(binaries, "langui.dll"),
            Path.Combine(binaries, "language.dll"),
            Environment.GetEnvironmentVariable("CSVM_ROF_EXTRACT_TO")!,
            Force: true);

        var result = RofExtraction.Run(request);

        Assert.Equal(10, result.Movies.Present);
        Assert.NotNull(result.MenuLayout);
    }

    private static FixtureInstallPaths FixtureInstall()
    {
        string root = TestData.TempDir();
        string layout = "[@ProbeMain@]\r\nPM_T_TITLE=T,IDS_KESTREL,120,44,0,200,20,0xFF102030,1\r\n";
        byte[] baseRof = ExtractionFixtures.Rof(new RofDir().Dir("ASSETS", new RofDir()
            .File("LAYOUT.CSV", Encoding.ASCII.GetBytes(layout))
            .Dir("SCRIPTS", new RofDir()
                .File("RESOURCE.H", Encoding.ASCII.GetBytes("#define IDS_KESTREL 16\r\n")))
            .Dir("GRAPHICS", new RofDir()
                .Dir("MPG", new RofDir())
                .Dir("FORTUNE", new RofDir().File("KES_WING.BM", ExtractionFixtures.Bm(4, 2, i => (byte)i))))));
        byte[] patchRof = ExtractionFixtures.Rof(new RofDir().Dir("ASSETS", new RofDir()
            .Dir("SCRIPTS", new RofDir().File("AIRFRAME.SCRIPT", Encoding.ASCII.GetBytes("gui_create")))));

        var paths = new FixtureInstallPaths(
            Path.Combine(root, "crimson.rof"),
            Path.Combine(root, "crimptch.rof"),
            Path.Combine(root, "MPG"),
            Path.Combine(root, "langui.dll"),
            Path.Combine(root, "language.dll"));
        File.WriteAllBytes(paths.BaseArchive, baseRof);
        File.WriteAllBytes(paths.PatchArchive, patchRof);
        Directory.CreateDirectory(paths.Movies);
        File.WriteAllBytes(Path.Combine(paths.Movies, "CrimFlag.MPG"), new byte[4]);
        File.WriteAllBytes(paths.Langui, ExtractionFixtures.StringTablePe(2, new[] { "[IMP36]Kestrel", "Second" }));
        File.WriteAllBytes(paths.Language, ExtractionFixtures.StringTablePe(2, new[] { "shadowed" }));
        return paths;
    }

    private sealed record FixtureInstallPaths(string BaseArchive, string PatchArchive, string Movies, string Langui, string Language)
    {
        public RofExtractionRequest Request(string output) =>
            new(BaseArchive, PatchArchive, Movies, Langui, Language, output);
    }
}

/// <summary>A fact that needs the player's install (its <c>GOSDATA/ASSETS</c> folder under a data
/// root) and, when <see cref="OptInVariable"/> is set, that variable too. Skipped with the reason
/// otherwise, so an absent install never reads as a pass.</summary>
public sealed class InstallFactAttribute : FactAttribute
{
    private string? _optIn;

    public InstallFactAttribute()
    {
        if (Assets == null)
        {
            Skip = "no retail install: set CSVM_DATA_ROOT to a checkout holding CrimsonSkiesGame/";
        }
    }

    /// <summary>The install's <c>GOSDATA/ASSETS</c> folder, or null when none is reachable.</summary>
    public static string? Assets { get; } = FindAssets();

    /// <summary>An environment variable the test also needs; unset, the test is skipped.</summary>
    public string? OptInVariable
    {
        get => _optIn;
        set
        {
            _optIn = value;
            if (Skip == null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(value!)))
            {
                Skip = "opt-in: set " + value + " to run this";
            }
        }
    }

    private static string? FindAssets()
    {
        foreach (var root in new[] { Environment.GetEnvironmentVariable("CSVM_DATA_ROOT"), TestData.RepoRoot })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            string assets = Path.Combine(root, "CrimsonSkiesGame", "GOSDATA", "ASSETS");
            if (File.Exists(Path.Combine(assets, "crimson.rof")))
            {
                return assets;
            }
        }

        return null;
    }
}
