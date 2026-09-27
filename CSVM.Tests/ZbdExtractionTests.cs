using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CSVM.Extraction;
using CSVM.Session.Launch;
using Xunit;

namespace CSVM.Tests;

/// <summary>The ZBD half of extraction without the real unzbd: the mode map, the output naming,
/// the up-to-date rules, the stderr notes and the stamp writer. The runner itself runs over a
/// fake install with a batch file standing in for unzbd, so it is Windows-only here.</summary>
public class ZbdExtractionTests
{
    [Theory]
    [InlineData("interp", "interp", ".json")]
    [InlineData("planes", "gamez", ".zip")]
    [InlineData("GAMEZ", "gamez", ".zip")]
    [InlineData("soundsh", "sounds", ".zip")]
    [InlineData("SoundsL", "sounds", ".zip")]
    [InlineData("zrdr", "reader", ".zip")]
    [InlineData("rimage", "textures", ".zip")]
    [InlineData("texture", "textures", ".zip")]
    [InlineData("rtexture12", "textures", ".zip")]
    [InlineData("cam_anim", "anim", ".zip")]
    [InlineData("MIS_ANIM", "anim", ".zip")]
    public void EachArchiveNameMapsToItsMode(string baseName, string mode, string extension)
    {
        Assert.Equal(new ZbdMode(mode, extension), ZbdPlan.ModeFor(baseName));
    }

    [Theory]
    [InlineData("rtexture")]
    [InlineData("sounds")]
    [InlineData("gamez2")]
    [InlineData("anim")]
    public void AnUnmappedNameHasNoMode(string baseName)
    {
        Assert.Null(ZbdPlan.ModeFor(baseName));
    }

    [Fact]
    public void TheOutputTakesUpperCaseFoldersALowerCaseNameAndItsExtension()
    {
        string rel = Path.Combine("c1b", "Ia1", "GAMEZ.ZBD");

        Assert.Equal(Path.Combine("C1B", "IA1", "gamez.zip"), ZbdPlan.OutputRelativePath(rel, ZbdPlan.ModeFor("GAMEZ")!));
        Assert.Equal("interp.json", ZbdPlan.OutputRelativePath("Interp.zbd", ZbdPlan.ModeFor("interp")!));
        Assert.Equal("C3/M01/zrdr.zip", ZbdTree.Canonical(@"c3\m01\ZRDR.zip"));
    }

    [Fact]
    public void AnOutputAsNewAsItsSourceIsUpToDate()
    {
        var source = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(ZbdPlan.UpToDate(source, source));
        Assert.True(ZbdPlan.UpToDate(source, source.AddSeconds(1)));
        Assert.False(ZbdPlan.UpToDate(source, source.AddSeconds(-1)));
        Assert.False(ZbdPlan.UpToDate(source, null));
    }

    [Fact]
    public void OnlyAZipNewerThanItsFolderIsExpandedAgain()
    {
        var zip = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(ZbdPlan.UnzipNeeded(zip, null, force: false));
        Assert.True(ZbdPlan.UnzipNeeded(zip, zip.AddSeconds(-1), force: false));
        Assert.False(ZbdPlan.UnzipNeeded(zip, zip, force: false));
        Assert.True(ZbdPlan.UnzipNeeded(zip, zip.AddSeconds(1), force: true));
    }

    [Fact]
    public void TheKnownNotesAreCountedAndEveryOtherLineIsAWarning()
    {
        var notes = ZbdPlan.Classify(new[]
        {
            "WARN object3d transform fail node 12",
            "INTERVAL VAL FAIL at event 3",
            "delta val fail at event 4",
            "anim def duplicate anim ref 'x'",
            "unexpected thing",
        });

        Assert.Equal(1, notes.TransformNotes);
        Assert.Equal(3, notes.AnimNotes);
        Assert.Equal(new[] { "unexpected thing" }, notes.Unexpected);
    }

    [Fact]
    public void TheToolIsNamedForItsPlatformUnderTools()
    {
        Assert.Equal("unzbd.exe", UnzbdTool.FileName(windows: true));
        Assert.Equal("unzbd", UnzbdTool.FileName(windows: false));
        Assert.Equal(Path.Combine("x", "tools", UnzbdTool.FileName(OperatingSystem.IsWindows())), UnzbdTool.DefaultPath("x"));
    }

    [Fact]
    public void TheStampMergeKeepsTheOtherHalfAndReadsBack()
    {
        string root = TestData.TempDir();
        string extracted = Path.Combine(root, "extracted");
        Directory.CreateDirectory(extracted);

        // The shape PowerShell 5.1 writes: a BOM, an older schema, and the rof half's field.
        File.WriteAllText(
            Path.Combine(extracted, "VERSION.json"),
            "{ \"schema\": 2, \"rof\": { \"script\": \"ExtractRof.ps1\", \"movies\": 10 } }",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        string path = ExtractionStampWriter.WriteAssets(extracted, new UnzbdIdentity("unzbd v1 (build)", "AB12", null), new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        byte[] bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        using var doc = JsonDocument.Parse(bytes);
        var stamp = doc.RootElement;
        Assert.Equal(ExtractionStamp.Schema, stamp.GetProperty("schema").GetInt32());
        Assert.Equal(10, stamp.GetProperty("rof").GetProperty("movies").GetInt32());
        Assert.Equal("2026-01-02T03:04:05Z", stamp.GetProperty("assets").GetProperty("date").GetString());
        Assert.Equal("unzbd v1 (build)", stamp.GetProperty("assets").GetProperty("unzbdVersion").GetString());
        Assert.Equal("AB12", stamp.GetProperty("assets").GetProperty("unzbdSha256").GetString());
        Assert.False(stamp.GetProperty("assets").TryGetProperty("unzbdCommit", out _));
        Assert.False(ExtractionStamp.Behind(root, ExtractionStamp.Schema, out var reason), reason);

        ExtractionStampWriter.WriteRof(extracted, 7, DateTime.UtcNow);
        using var again = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(7, again.RootElement.GetProperty("rof").GetProperty("movies").GetInt32());
        Assert.Equal("AB12", again.RootElement.GetProperty("assets").GetProperty("unzbdSha256").GetString());
    }

    [Fact]
    public void AnUnreadableStampIsRewrittenFromNothing()
    {
        string extracted = TestData.TempDir();
        File.WriteAllText(Path.Combine(extracted, "VERSION.json"), "{ not json");

        string path = ExtractionStampWriter.WriteRof(extracted, 10, DateTime.UtcNow);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(ExtractionStamp.Schema, doc.RootElement.GetProperty("schema").GetInt32());
    }

    [Fact]
    public void TheRunnerExtractsSkipsCountsAndStampsOnlyWithoutFailures()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = TestData.TempDir();
        string install = Path.Combine(root, "install");
        string extracted = Path.Combine(root, "extracted");
        Touch(install, "Zbd", "C1", "interp.zbd");
        Touch(install, "Zbd", "C1", "Sub", "Texture.ZBD");
        Touch(install, "Zbd", "weird.zbd");
        Touch(install, "Strings.DLL");
        string fake = FakeUnzbd(root);

        var steps = new System.Collections.Generic.List<ZbdProgress>();
        var first = ZbdExtraction.Run(install, extracted, fake, new ZbdExtractionOptions(), steps.Add);

        Assert.True(first.Succeeded, string.Join("; ", first.Failures));
        Assert.Equal(3, first.Archives);
        Assert.Equal(3, first.Extracted);
        Assert.Equal(1, first.Skipped);
        Assert.Equal(new[] { "weird.zbd" }, first.Unknowns);
        Assert.Equal(2, first.TransformNotes);
        Assert.Equal(2, first.AnimNotes);
        Assert.Equal(2, first.Warnings.Count);
        Assert.True(File.Exists(Path.Combine(extracted, "C1", "interp.json")));
        Assert.Equal(new[] { "texture.zip" }, Directory.GetFiles(Path.Combine(extracted, "C1", "Sub")).Select(Path.GetFileName));
        Assert.Equal(new[] { "SUB" }, Directory.GetDirectories(Path.Combine(extracted, "C1")).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(extracted, "messages.json")));
        Assert.Equal(Path.Combine(extracted, "VERSION.json"), first.Stamp);
        Assert.All(steps, s => Assert.Equal(4, s.Total));

        var second = ZbdExtraction.Run(install, extracted, fake, new ZbdExtractionOptions());
        Assert.Equal(0, second.Extracted);
        Assert.Equal(3, second.UpToDate);

        // A gamez archive makes the fake exit non-zero: the run continues, and the tree is not stamped.
        File.Delete(Path.Combine(extracted, "VERSION.json"));
        Touch(install, "Zbd", "C2", "gamez.zbd");
        var third = ZbdExtraction.Run(install, extracted, fake, new ZbdExtractionOptions());
        Assert.False(third.Succeeded);
        Assert.Equal(new[] { Path.Combine("C2", "gamez.zbd") + " (exit 3)" }, third.Failures);
        Assert.Null(third.Stamp);
        Assert.False(File.Exists(Path.Combine(extracted, "VERSION.json")));
    }

    [Fact]
    public void AMissingToolOrZbdFolderIsFatal()
    {
        string root = TestData.TempDir();

        Assert.NotNull(ZbdExtraction.Run(root, root, Path.Combine(root, "absent.exe"), new ZbdExtractionOptions()).Fatal);
        Touch(root, "unzbd.exe");
        Assert.NotNull(ZbdExtraction.Run(root, root, Path.Combine(root, "unzbd.exe"), new ZbdExtractionOptions()).Fatal);
    }

    private static void Touch(string root, params string[] parts)
    {
        string path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
    }

    // Stands in for unzbd. --version prints a line and gamez fails. Every other archive mode writes
    // its output, two notes and one unexpected line on stderr.
    private static string FakeUnzbd(string root)
    {
        string path = Path.Combine(root, "unzbd.cmd");
        File.WriteAllText(path, string.Join("\r\n", new[]
        {
            "@echo off",
            "if \"%~1\"==\"--version\" (echo unzbd fake& exit /b 0)",
            "if \"%~2\"==\"gamez\" (echo boom 1>&2& exit /b 3)",
            "if \"%~2\"==\"messages\" (echo {}> \"%~4\"& exit /b 0)",
            "echo object3d transform fail 1>&2",
            "echo INTERVAL VAL FAIL 1>&2",
            "echo odd line 1>&2",
            "echo {}> \"%~4\"",
            "exit /b 0",
        }) + "\r\n");
        return path;
    }
}
