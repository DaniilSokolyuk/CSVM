using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CSVM.Extraction;
using CSVM.Utils;
using Xunit;

namespace CSVM.Tests;

/// <summary>The install lookup over temp folders spelled the way no retail copy is. Covered: the
/// segment walk, the picked-folder check, the candidate order and the remembered path.
/// ⚠ Assert a resolved path's spelling ordinally, never with <c>Directory.Exists</c>. Windows
/// answers an existence probe in any case, so only the spelling proves the walk ran.</summary>
public class InstallLocatorTests
{
    [Fact]
    public void ResolveWalksEachSegmentAndAnswersTheDiskSpelling()
    {
        string root = OddlySpelledInstall(TestData.TempDir());

        Assert.Equal(Path.Combine(root, "Gosdata", "assets"), InstallLocator.ResolveDirectory(root, "GOSDATA/ASSETS"));
        Assert.Equal(Path.Combine(root, "zbd"), InstallLocator.ResolveDirectory(root, "ZBD"));
        Assert.Equal(Path.Combine(root, "BINARIES", "LANGUI.DLL"), InstallLocator.ResolveFile(root, "binaries/langui.dll"));
        Assert.Equal(Path.Combine(root, "Gosdata", "assets", "crimson.ROF"), InstallLocator.ResolveFile(root, @"GOSDATA\ASSETS\crimson.rof"));
        Assert.Equal(root, InstallLocator.ResolveDirectory(root, string.Empty));
    }

    [Fact]
    public void ResolveAnswersNullForAnythingAbsentOrOfTheOtherKind()
    {
        string root = OddlySpelledInstall(TestData.TempDir());

        Assert.Null(InstallLocator.ResolveFile(root, "BINARIES/language.dll"));
        Assert.Null(InstallLocator.ResolveDirectory(root, "GOSDATA/ASSETS/GRAPHICS"));
        Assert.Null(InstallLocator.ResolveFile(root, "ZBD"));
        Assert.Null(InstallLocator.ResolveDirectory(root, "BINARIES/LANGUI.DLL"));
        Assert.Null(InstallLocator.ResolveDirectory(Path.Combine(root, "absent"), "ZBD"));
    }

    [CaseSensitiveFact]
    public void AnExactMatchWinsOverACaseFoldedOne()
    {
        string root = CaseSensitiveFactAttribute.NewDirectory();
        Directory.CreateDirectory(Path.Combine(root, "zbd"));
        Directory.CreateDirectory(Path.Combine(root, "ZBD"));
        Directory.CreateDirectory(Path.Combine(root, "Zbd"));

        Assert.Equal(Path.Combine(root, "Zbd"), InstallLocator.ResolveDirectory(root, "Zbd"));
        Assert.Equal(Path.Combine(root, "ZBD"), InstallLocator.ResolveDirectory(root, "ZBD"));
        Assert.Equal(Path.Combine(root, "ZBD"), InstallLocator.ResolveDirectory(root, "zBd"));
    }

    [Fact]
    public void AnOddlySpelledInstallChecksAsAnInstall()
    {
        string root = OddlySpelledInstall(TestData.TempDir());

        var check = InstallLocator.Check(root);

        Assert.Equal(InstallCheckKind.Install, check.Kind);
        Assert.True(check.IsInstall);
        Assert.Equal(root, check.InstallRoot);
        Assert.Null(check.Message);
    }

    [Fact]
    public void APastedPathWithQuotesAndATrailingSeparatorStillChecks()
    {
        string root = OddlySpelledInstall(TestData.TempDir());

        var check = InstallLocator.Check("  \"" + root + Path.DirectorySeparatorChar + "\" ");

        Assert.True(check.IsInstall);
        Assert.Equal(root, check.Folder);
    }

    [Fact]
    public void PickingTheFolderAboveTheInstallNamesTheInstall()
    {
        string games = Path.Combine(TestData.TempDir(), "Microsoft Games");
        string root = OddlySpelledInstall(Path.Combine(games, "Crimson Skies"));

        var check = InstallLocator.Check(games);
        var twoUp = InstallLocator.Check(Path.GetDirectoryName(games)!);

        Assert.Equal(InstallCheckKind.HoldsInstall, check.Kind);
        Assert.Equal(root, check.InstallRoot);
        Assert.Contains($"'{root}'", check.Message);
        Assert.Contains(InstallLocator.ExpectedFolder, check.Message);
        Assert.Equal(InstallCheckKind.HoldsInstall, twoUp.Kind);
        Assert.Equal(root, twoUp.InstallRoot);
    }

    [Fact]
    public void PickingAFolderInsideTheInstallNamesTheInstall()
    {
        string root = OddlySpelledInstall(Path.Combine(TestData.TempDir(), "Crimson Skies"));

        var check = InstallLocator.Check(Path.Combine(root, "Gosdata", "assets"));

        Assert.Equal(InstallCheckKind.InsideInstall, check.Kind);
        Assert.Equal(root, check.InstallRoot);
        Assert.Contains($"'{root}'", check.Message);
        Assert.Contains(InstallLocator.ExpectedFolder, check.Message);
    }

    [Fact]
    public void AnUnrelatedOrHalfInstallSaysWhichHalfIsMissing()
    {
        string empty = TestData.TempDir();
        string zbdOnly = TestData.TempDir();
        Directory.CreateDirectory(Path.Combine(zbdOnly, "ZBD"));
        string noArchives = TestData.TempDir();
        Directory.CreateDirectory(Path.Combine(noArchives, "ZBD", "C1"));
        Directory.CreateDirectory(Path.Combine(noArchives, "GOSDATA", "ASSETS"));

        Assert.Equal(InstallCheckKind.NoZbd, InstallLocator.Check(empty).Kind);
        Assert.Equal(InstallCheckKind.NoAssets, InstallLocator.Check(zbdOnly).Kind);
        Assert.Equal(InstallCheckKind.EmptyZbd, InstallLocator.Check(noArchives).Kind);
        Assert.Equal(InstallCheckKind.Missing, InstallLocator.Check(Path.Combine(empty, "absent")).Kind);
        Assert.Equal(InstallCheckKind.Missing, InstallLocator.Check("  ").Kind);
        Assert.All(new[] { empty, zbdOnly, noArchives }, f => Assert.Contains(InstallLocator.ExpectedFolder, InstallLocator.Check(f).Message));
    }

    [Fact]
    public void WindowsCandidatesComeRememberedFirstThenProgramFilesThenEachDrive()
    {
        string machine = TestData.TempDir();
        string programFiles = Path.Combine(machine, "Program Files");
        string programFilesX86 = Path.Combine(machine, "Program Files (x86)");
        string driveD = Path.Combine(machine, "D");
        string driveE = Path.Combine(machine, "E");
        string remembered = OddlySpelledInstall(Path.Combine(machine, "Copies", "CS"));
        string installer = OddlySpelledInstall(Path.Combine(programFilesX86, "Microsoft Games", "Crimson Skies"));
        string dGames = OddlySpelledInstall(Path.Combine(driveD, "Games", "Crimson Skies"));
        string dBare = OddlySpelledInstall(Path.Combine(driveD, "crimson skies"));
        string eMicrosoft = OddlySpelledInstall(Path.Combine(driveE, "MICROSOFT GAMES", "Crimson Skies"));
        Directory.CreateDirectory(programFiles);
        Directory.CreateDirectory(Path.Combine(driveE, "Crimson Skies"));
        var roots = new InstallSearchRoots(
            Windows: true,
            Home: null,
            ProgramFiles: new[] { programFiles, programFilesX86, programFilesX86 },
            FixedDrives: new[] { driveD, driveE });

        var found = InstallLocator.Candidates(roots, remembered);

        Assert.Equal(new[] { remembered, installer, dGames, dBare, eMicrosoft }, found);
    }

    [Fact]
    public void AStaleOrRepeatedRememberedPathDoesNotBecomeACandidate()
    {
        string machine = TestData.TempDir();
        string installer = OddlySpelledInstall(Path.Combine(machine, "PF", "Microsoft Games", "Crimson Skies"));
        var roots = new InstallSearchRoots(true, null, new[] { Path.Combine(machine, "PF") }, Array.Empty<string>());

        Assert.Equal(new[] { installer }, InstallLocator.Candidates(roots, Path.Combine(machine, "gone")));
        Assert.Equal(new[] { installer }, InstallLocator.Candidates(roots, installer + Path.DirectorySeparatorChar));
        Assert.Empty(InstallLocator.Candidates(new InstallSearchRoots(true, null, Array.Empty<string>(), Array.Empty<string>()), null));
    }

    [Fact]
    public void LinuxCandidatesSearchWineThenEveryProtonPrefix()
    {
        string home = TestData.TempDir();
        string compat = Path.Combine(home, ".local", "share", "Steam", "steamapps", "compatdata");
        Directory.CreateDirectory(Path.Combine(compat, "228980", "pfx", "drive_c", "Program Files"));
        string proton = OddlySpelledInstall(Path.Combine(
            compat, "2000000123", "pfx", "drive_c", "Program Files (x86)", "Microsoft Games", "Crimson Skies"));
        string wine = OddlySpelledInstall(Path.Combine(home, ".wine", "drive_c", "Program Files", "microsoft games", "crimson skies"));
        var roots = new InstallSearchRoots(false, home, Array.Empty<string>(), Array.Empty<string>());

        Assert.Equal(new[] { wine, proton }, InstallLocator.Candidates(roots, null));
    }

    [Fact]
    public void LinuxCandidatesIgnoreTheWindowsRoots()
    {
        string machine = TestData.TempDir();
        OddlySpelledInstall(Path.Combine(machine, "PF", "Microsoft Games", "Crimson Skies"));
        var roots = new InstallSearchRoots(false, TestData.TempDir(), new[] { Path.Combine(machine, "PF") }, new[] { machine });

        Assert.Empty(InstallLocator.Candidates(roots, null));
    }

    [DirectoryLinkFact]
    public void BothSteamRootsReachingOnePrefixGiveOneCandidate()
    {
        string home = TestData.TempDir();
        string steam = Path.Combine(home, ".local", "share", "Steam");
        string proton = OddlySpelledInstall(Path.Combine(
            steam, "steamapps", "compatdata", "2000000123", "pfx", "drive_c", "Program Files", "Microsoft Games", "Crimson Skies"));
        Directory.CreateDirectory(Path.Combine(home, ".steam"));
        string link = Path.Combine(home, ".steam", "steam");
        Assert.True(DirectoryLinkFactAttribute.TryLink(link, steam), "the probe that enabled this test could link, so this link must succeed");
        try
        {
            var roots = new InstallSearchRoots(false, home, Array.Empty<string>(), Array.Empty<string>());

            Assert.Equal(new[] { proton }, InstallLocator.Candidates(roots, null));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [MovieDataFact]
    public void TheRetailInstallChecksAsAnInstallAndResolvesItsArchive()
    {
        string install = Path.GetFullPath(Path.Combine(TestData.MovieRoot!, "..", "..", "..", ".."));

        Assert.Equal(InstallCheckKind.Install, InstallLocator.Check(install).Kind);
        Assert.Equal(Path.Combine(install, "GOSDATA", "ASSETS", "crimson.rof"), InstallLocator.ResolveFile(install, "gosdata/assets/CRIMSON.ROF"));
    }

    [Fact]
    public void TheRememberedPathRoundTripsThroughTheOptionsFileAndKeepsTheOtherOptions()
    {
        var store = new OptionsStore(TestData.TempDir());
        store.Save(new OptionsDef { Difficulty = DifficultyWords.Hard });
        string install = TestData.TempDir();

        Assert.Null(RememberedInstall.Get(store));
        RememberedInstall.Set(store, install + Path.DirectorySeparatorChar);

        Assert.Equal(install, RememberedInstall.Get(store));
        Assert.Equal(DifficultyWords.Hard, store.Load().Difficulty);
    }

    [Fact]
    public void ARelativeInstallPathInTheFileReadsAsNeverSet()
    {
        string dir = TestData.TempDir();
        File.WriteAllText(Path.Combine(dir, "options.json"), "{\"version\": 1, \"installPath\": \"Crimson Skies\", \"difficulty\": \"hard\"}");
        var def = new OptionsStore(dir).Load();

        Assert.Null(def.InstallPath);
        Assert.Equal(DifficultyWords.Hard, def.Difficulty);
    }

    // An install with every name in a case the lookup does not ask for. One archive makes the
    // check pass, and one DLL stands for the two extraction reads.
    private static string OddlySpelledInstall(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Gosdata", "assets"));
        Directory.CreateDirectory(Path.Combine(root, "zbd", "c1"));
        Directory.CreateDirectory(Path.Combine(root, "BINARIES"));
        File.WriteAllBytes(Path.Combine(root, "zbd", "c1", "GAMEZ.ZBD"), new byte[] { 0 });
        File.WriteAllBytes(Path.Combine(root, "Gosdata", "assets", "crimson.ROF"), new byte[] { 0 });
        File.WriteAllBytes(Path.Combine(root, "BINARIES", "LANGUI.DLL"), new byte[] { 0 });
        return root;
    }
}

/// <summary>A fact that needs a folder whose names differ by case alone. Linux has one anywhere;
/// Windows has one where <c>fsutil file setCaseSensitiveInfo</c> is allowed, which is probed once.
/// Skipped with a reason otherwise, so the exact-match rule is never reported as passing
/// unchecked.</summary>
public sealed class CaseSensitiveFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Parent = new(Probe);

    public CaseSensitiveFactAttribute()
    {
        if (Parent.Value == null)
        {
            Skip = "no case-sensitive folder can be made on this machine (fsutil setCaseSensitiveInfo refused)";
        }
    }

    /// <summary>A fresh empty case-sensitive folder. Only valid inside a test this attribute did not
    /// skip.</summary>
    public static string NewDirectory()
    {
        string dir = Path.Combine(Parent.Value!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // A new folder inherits the flag from its parent, so one flagged parent serves every test.
    private static string? Probe()
    {
        string dir = TestData.TempDir();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var fsutil = Process.Start(new ProcessStartInfo("fsutil", $"file setCaseSensitiveInfo \"{dir}\" enable")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (fsutil == null || !fsutil.WaitForExit(10000))
                {
                    return null;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        Directory.CreateDirectory(Path.Combine(dir, "probe", "a"));
        Directory.CreateDirectory(Path.Combine(dir, "probe", "A"));
        return Directory.GetDirectories(Path.Combine(dir, "probe")).Length == 2 ? dir : null;
    }
}

/// <summary>A fact that needs a directory link. Linux makes a symbolic link freely; Windows makes
/// one only with developer mode, so a junction stands in there. Probed once, skipped with a reason
/// when neither can be made.</summary>
public sealed class DirectoryLinkFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> CanLink = new(Probe);

    public DirectoryLinkFactAttribute()
    {
        if (!CanLink.Value)
        {
            Skip = "no directory link can be made on this machine";
        }
    }

    /// <summary>Makes <paramref name="link"/> point at <paramref name="target"/>, answering whether
    /// it did. ⚠ Remove the link with a non-recursive <c>Directory.Delete</c> before the test ends,
    /// so no recursive delete ever meets it.</summary>
    public static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            return mklink != null && mklink.WaitForExit(10000) && mklink.ExitCode == 0 && Directory.Exists(link);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool Probe()
    {
        string dir = TestData.TempDir();
        string target = Path.Combine(dir, "target");
        string link = Path.Combine(dir, "link");
        Directory.CreateDirectory(target);
        if (!TryLink(link, target))
        {
            return false;
        }

        Directory.Delete(link);
        return true;
    }
}
