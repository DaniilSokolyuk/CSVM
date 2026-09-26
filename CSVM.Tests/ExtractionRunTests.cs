using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using CSVM.Extraction;
using CSVM.Session.Launch;
using Xunit;

namespace CSVM.Tests;

/// <summary>The whole extraction as <c>--extract</c> and the Extract button run it. The install
/// check comes first, then the ZBD half, the <c>.rof</c> half over case-blind inputs, the stamp and
/// the exit code. The runs use a batch file standing in for unzbd, so they
/// are Windows-only here; the flags' parsing is at the end.</summary>
public class ExtractionRunTests
{
    [Fact]
    public void TheRofInputsAreFoundWhateverTheirCase()
    {
        string install = OddlySpelledInstall(TestData.TempDir(), withPatch: false);

        var request = ExtractionRun.RofRequest(install, Path.Combine(install, "out"), force: true);

        Assert.Equal(Path.Combine(install, "gosdata", "Assets", "CRIMSON.ROF"), request.BaseArchive);
        Assert.Null(request.PatchArchive);
        Assert.Equal(Path.Combine(install, "gosdata", "Assets", "Graphics", "mpg"), request.MovieFolder);
        Assert.Equal(Path.Combine(install, "gosdata", "Assets", "binaries", "LANGUI.DLL"), request.LanguiDll);
        Assert.Equal(Path.Combine(install, "gosdata", "Assets", "binaries", "Language.Dll"), request.LanguageDll);
        Assert.True(request.Force);
    }

    [Fact]
    public void TheDefaultToolIsTheReleasesOrTheForkBuild()
    {
        string name = UnzbdTool.FileName(OperatingSystem.IsWindows());

        Assert.Equal(Path.Combine("x", "tools", name), ExtractionRun.DefaultUnzbd("x", exported: true));
        Assert.Equal(Path.Combine("x", "tools", "mech3ax", "target", "release", name), ExtractionRun.DefaultUnzbd("x", exported: false));
    }

    [Fact]
    public void OnlyTheRootRimageZipIsAlwaysExpanded()
    {
        Assert.True(ZbdPlan.AlwaysUnzipped("rimage.zip"));
        Assert.True(ZbdPlan.AlwaysUnzipped("RIMAGE.zip"));
        Assert.False(ZbdPlan.AlwaysUnzipped(Path.Combine("C1", "rimage.zip")));
        Assert.False(ZbdPlan.AlwaysUnzipped("zrdr.zip"));
    }

    [Fact]
    public void TheProgressBarRisesPerFinishedArchiveAndEndsAtTheRofHalf()
    {
        double start = ExtractionRun.ZbdFraction(new ZbdProgress(1, 4, "a", ZbdStep.Extracting, "gamez", null, null));
        double first = ExtractionRun.ZbdFraction(new ZbdProgress(1, 4, "a", ZbdStep.Extracted, "gamez", null, null));
        double last = ExtractionRun.ZbdFraction(new ZbdProgress(4, 4, "m", ZbdStep.UpToDate, "messages", null, null));

        Assert.Equal(0, start);
        Assert.True(first > start && first < last);
        Assert.True(last < 1);
    }

    [Fact]
    public void AFolderThatIsNotAnInstallFailsBeforeAnythingIsWritten()
    {
        string root = TestData.TempDir();
        string dataRoot = Path.Combine(root, "data");
        var lines = new List<string>();
        var failures = new List<string>();

        int code = ExtractionRun.RunToConsole(
            new ExtractionRequest(Path.Combine(root, "absent"), dataRoot, Path.Combine(root, "unzbd.exe")), lines.Add, failures.Add);

        Assert.Equal(1, code);
        Assert.False(Directory.Exists(dataRoot));
        Assert.Contains(lines, l => l.StartsWith("FAILED: The folder", StringComparison.Ordinal));
        Assert.Single(failures);
    }

    [Fact]
    public void AMissingToolFailsTheRunAndLeavesTheRofHalfUndone()
    {
        string root = TestData.TempDir();
        string install = OddlySpelledInstall(root, withPatch: true);
        var request = new ExtractionRequest(install, Path.Combine(root, "data"), Path.Combine(root, "absent.exe"));

        var result = ExtractionRun.Run(request);

        Assert.False(result.Succeeded);
        Assert.StartsWith("unzbd not found", result.Failures[0], StringComparison.Ordinal);
        Assert.Null(result.Rof);
        Assert.False(Directory.Exists(request.RofDir));
    }

    [Fact]
    public void AWholeRunExtractsBothHalvesStampsBothAndExitsZero()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = TestData.TempDir();
        string install = OddlySpelledInstall(root, withPatch: true);
        var request = new ExtractionRequest(install, Path.Combine(root, "data"), FakeUnzbd(root));
        var phases = new List<ExtractionProgress>();

        var result = ExtractionRun.Run(request, phases.Add);

        Assert.True(result.Succeeded, string.Join("; ", result.Failures));
        Assert.Equal(2, result.Zbd!.Extracted);
        Assert.Equal(RofArchiveOutcome.Extracted, result.Rof!.Archives[0].Outcome);
        Assert.True(File.Exists(Path.Combine(request.ExtractedDir, "C1", "zrdr.zip")));
        Assert.True(File.Exists(Path.Combine(request.RofDir, "ASSETS", "SCRIPTS", "RESOURCE.H")));
        Assert.True(File.Exists(Path.Combine(request.RofDir, "ASSETS", "GRAPHICS", "MPG", "Chap0.MPG")));
        using (var stamp = JsonDocument.Parse(File.ReadAllText(Path.Combine(request.ExtractedDir, "VERSION.json"))))
        {
            Assert.Equal(ExtractionStamp.Schema, stamp.RootElement.GetProperty("schema").GetInt32());
            Assert.Equal("unzbd fake", stamp.RootElement.GetProperty("assets").GetProperty("unzbdVersion").GetString());
            Assert.Equal(1, stamp.RootElement.GetProperty("rof").GetProperty("movies").GetInt32());
        }

        Assert.Contains(result.Warnings, w => w.Contains("cinema(s) missing", StringComparison.Ordinal));
        Assert.Equal(ExtractionPhase.Done, phases[^1].Phase);
        Assert.Equal(1.0, phases[^1].Fraction);
        Assert.True(phases.Select(p => p.Fraction).SequenceEqual(phases.Select(p => p.Fraction).OrderBy(f => f)));
        Assert.Contains(phases, p => p.Phase == ExtractionPhase.Rof && p.Lines.Any(l => l.Contains("crimson.rof", StringComparison.Ordinal)));

        var lines = new List<string>();
        Assert.Equal(0, ExtractionRun.RunToConsole(request, lines.Add, _ => { }));
        Assert.Equal("Done.", lines[^1]);
        Assert.Contains(lines, l => l.Contains("up to date:      2 archive(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailedArchiveStopsTheRunBeforeTheRofHalf()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = TestData.TempDir();
        string install = OddlySpelledInstall(root, withPatch: true);
        Touch(install, "Zbd", "C2", "gamez.zbd");
        var request = new ExtractionRequest(install, Path.Combine(root, "data"), FakeUnzbd(root));

        var result = ExtractionRun.Run(request);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures, f => f.StartsWith("unzbd failed on " + Path.Combine("C2", "gamez.zbd"), StringComparison.Ordinal));
        Assert.Null(result.Rof);
        Assert.False(File.Exists(Path.Combine(request.ExtractedDir, "VERSION.json")));
    }

    [Fact]
    public void AnInstallWithoutTheBaseArchiveFailsAndIsNotStampedForTheRofHalf()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = TestData.TempDir();
        string install = OddlySpelledInstall(root, withPatch: true);
        File.Delete(Path.Combine(install, "gosdata", "Assets", "CRIMSON.ROF"));
        var request = new ExtractionRequest(install, Path.Combine(root, "data"), FakeUnzbd(root));

        var result = ExtractionRun.Run(request);

        Assert.False(result.Succeeded);
        Assert.Contains("crimson.rof", result.Failures[0], StringComparison.Ordinal);
        Assert.Null(result.RofStamp);
    }

    [Fact]
    public void ACancelledRunThrowsAfterTheInstallCheck()
    {
        string root = TestData.TempDir();
        string install = OddlySpelledInstall(root, withPatch: true);
        Touch(root, "unzbd.exe");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ExtractionRun.Run(new ExtractionRequest(install, Path.Combine(root, "data"), Path.Combine(root, "unzbd.exe")), null, cancel.Token));
    }

    [Fact]
    public void TheExtractFlagsAreValuesAndMakeAScriptedExtractRun()
    {
        var s = SessionSpec.Parse(new[] { "--headless", "--extract=C:/Games/Crimson Skies", "--extract-force", "--extract-unzip", "--unzbd=u.exe", "--data-root=d" });

        Assert.Equal("C:/Games/Crimson Skies", s.ExtractInstall);
        Assert.True(s.ExtractForce);
        Assert.True(s.ExtractUnzip);
        Assert.Equal("u.exe", s.UnzbdPath);
        Assert.Equal("d", s.DataRoot);
        Assert.Equal("extract", s.ModeName);
        Assert.True(s.IsScripted);
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void ABareExtractCarriesAnEmptyInstallForTheCheckToRefuse()
    {
        var s = SessionSpec.Parse(new[] { "--extract" });

        Assert.Equal(string.Empty, s.ExtractInstall);
        Assert.False(InstallLocator.Check(s.ExtractInstall!).IsInstall);
    }

    [Fact]
    public void TheDevelopmentOptionsWithoutExtractAreNamedAndIgnored()
    {
        var plain = SessionSpec.Parse(new[] { "--fly" });
        Assert.Null(plain.ExtractInstall);
        Assert.False(plain.IsScripted);

        var stray = SessionSpec.Parse(new[] { "--extract-force", "--extract-unzip", "--unzbd=u.exe" });
        Assert.Null(stray.ExtractInstall);
        Assert.Equal(3, stray.Warnings.Count);
        Assert.All(stray.Warnings, w => Assert.Contains("without --extract=", w.Message, StringComparison.Ordinal));
    }

    // An install spelled the way no retail copy is, so every lookup has to fold case. It holds one
    // ZBD archive, strings.dll, a base archive with RESOURCE.H, one movie and both string tables.
    private static string OddlySpelledInstall(string root, bool withPatch)
    {
        string install = Path.Combine(root, "install");
        Touch(install, "Zbd", "C1", "ZRDR.zbd");
        Touch(install, "Strings.DLL");
        string assets = Path.Combine(install, "gosdata", "Assets");
        Directory.CreateDirectory(Path.Combine(assets, "binaries"));
        Directory.CreateDirectory(Path.Combine(assets, "Graphics", "mpg"));
        File.WriteAllBytes(Path.Combine(assets, "CRIMSON.ROF"), ExtractionFixtures.Rof(new RofDir().Dir("ASSETS", new RofDir()
            .Dir("SCRIPTS", new RofDir().File("RESOURCE.H", Encoding.ASCII.GetBytes("#define IDS_KESTREL 16\r\n"))))));
        if (withPatch)
        {
            File.WriteAllBytes(Path.Combine(assets, "CrimPtch.rof"), ExtractionFixtures.Rof(new RofDir().Dir("ASSETS", new RofDir())));
        }

        File.WriteAllBytes(Path.Combine(assets, "Graphics", "mpg", "Chap0.MPG"), new byte[4]);
        File.WriteAllBytes(Path.Combine(assets, "binaries", "LANGUI.DLL"), ExtractionFixtures.StringTablePe(2, new[] { "[IMP36]Kestrel" }));
        File.WriteAllBytes(Path.Combine(assets, "binaries", "Language.Dll"), ExtractionFixtures.StringTablePe(2, new[] { "shadowed" }));
        return install;
    }

    private static void Touch(string root, params string[] parts)
    {
        string path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
    }

    // Stands in for unzbd: --version prints a line, gamez fails, and every other mode writes its
    // output with nothing on stderr.
    private static string FakeUnzbd(string root)
    {
        string path = Path.Combine(root, "unzbd.cmd");
        File.WriteAllText(path, string.Join("\r\n", new[]
        {
            "@echo off",
            "if \"%~1\"==\"--version\" (echo unzbd fake& exit /b 0)",
            "if \"%~2\"==\"gamez\" (echo boom 1>&2& exit /b 3)",
            "echo {}> \"%~4\"",
            "exit /b 0",
        }) + "\r\n");
        return path;
    }
}
