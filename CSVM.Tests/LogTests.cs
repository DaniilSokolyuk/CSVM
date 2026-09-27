using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using CSVM.Utils;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The pure half of the logging facility: the <c>--log=</c> filter grammar, the line grammar, the
/// invariant-culture rendering and the sink directory. None of it touches a Godot API, so it runs
/// in a plain test host, and neither does the walk that holds the call sites to the closed category
/// vocabulary. The tests share <see cref="Log"/>'s process-global filter state, so each one re-baselines
/// with a <c>*</c> spec first (which clears every per-category override).
/// </summary>
public class LogTests
{
    [Fact]
    public void AFloatRendersInvariantWhateverTheMachineLocale()
    {
        var was = Thread.CurrentThread.CurrentCulture;
        try
        {
            // The locale of the machine this project is developed on: it renders 16,667.
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("dt=16.667 ms", Log.Format($"dt={1000f / 60f:0.000} ms"));
            Assert.Equal("pos=(-6778.7, 128.0, -5958.0)", Log.Format($"pos=({-6778.7f:0.0}, {128f:0.0}, {-5958f:0.0})"));

            // The able-to-fail control: the same interpolation without Log renders comma decimals,
            // so the assertion above is testing the invariant rendering and not the value.
            Assert.Equal("16,667", $"{1000f / 60f:0.000}");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = was;
        }
    }

    [Fact]
    public void TheDefaultConsoleShowsInfoButNotDebug()
    {
        Log.Configure("*:info");
        Assert.True(Log.ConsoleShows("anim", Log.Level.Info));
        Assert.False(Log.ConsoleShows("anim", Log.Level.Debug));
    }

    [Fact]
    public void ABareCategoryTurnsThatCategoryUpToDebugAndLeavesTheOthers()
    {
        Log.Configure("*:info");
        Log.Configure("anim");
        Assert.True(Log.ConsoleShows("anim", Log.Level.Debug));
        Assert.False(Log.ConsoleShows("world", Log.Level.Debug));
        Assert.True(Log.ConsoleShows("world", Log.Level.Info));
    }

    [Fact]
    public void ACategoryTurnedDownStillShowsWarningsAndErrors()
    {
        Log.Configure("*:info");
        Log.Configure("world:error");
        Assert.False(Log.ConsoleShows("world", Log.Level.Info));
        Assert.True(Log.ConsoleShows("world", Log.Level.Warn));
        Assert.True(Log.ConsoleShows("world", Log.Level.Error));
    }

    [Fact]
    public void AStarSpecSetsEveryCategoryAndDropsPerCategoryOverrides()
    {
        Log.Configure("*:info");
        Log.Configure("anim:debug");
        Log.Configure("*:info");
        Assert.False(Log.ConsoleShows("anim", Log.Level.Debug));

        Log.Configure("debug"); // a bare level is the whole-session form
        Assert.True(Log.ConsoleShows("anim", Log.Level.Debug));
        Assert.True(Log.ConsoleShows("weapons", Log.Level.Debug));
        Log.Configure("*:info");
    }

    [Fact]
    public void AnUnknownCategoryIsKeptRatherThanDropped()
    {
        Log.Configure("*:info");
        Log.Configure("typo:debug");
        Assert.True(Log.ConsoleShows("typo", Log.Level.Debug));
        Assert.DoesNotContain("typo", Log.Categories);
        Log.Configure("*:info");
    }

    /// <summary>The literal paths matter both ways: the toolchain reads <c>.scratch/logs/</c> by
    /// name, and a top-level <c>logs/</c> in a development tree would be committed. Each OS gets
    /// its own literals because the host separator is part of the answer.</summary>
    [Fact]
    public void AnExportedBuildLogsBesideItsExeAndARepoRunStaysUnderScratch()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"C:\Games\CSVM\logs", Log.DirectoryFor(@"C:\Games\CSVM", exported: true));
            Assert.Equal(@"Z:\CSVM\.scratch\logs", Log.DirectoryFor(@"Z:\CSVM", exported: false));
        }
        else
        {
            Assert.Equal("/opt/csvm/logs", Log.DirectoryFor("/opt/csvm", exported: true));
            Assert.Equal("/home/u/CSVM/.scratch/logs", Log.DirectoryFor("/home/u/CSVM", exported: false));
        }
    }

    [Fact]
    public void TheFileLineCarriesItsLevelThenTheConsoleLineVerbatim()
    {
        Assert.Equal("INFO  [world] clutter template not in gamez template=hangar",
            Log.FileLine(Log.Level.Info, "world", "clutter template not in gamez template=hangar"));
        Assert.Equal("DEBUG [anim] texture cycles water=f3",
            Log.FileLine(Log.Level.Debug, "anim", "texture cycles water=f3"));
        Assert.Equal("ERROR [core] config root is not a JSON object",
            Log.FileLine(Log.Level.Error, "core", "config root is not a JSON object"));
    }

    /// <summary>The vocabulary is closed, so a call site naming a category outside it fails here.
    /// Nothing at runtime catches one: an undeclared name still logs and still filters, and the
    /// only warning is for an unknown name in a <c>--log=</c> spec. The sources are walked rather
    /// than the assembly because the category is an ordinary string argument.</summary>
    [Fact]
    public void EveryCategoryACallSiteNamesIsDeclared()
    {
        string src = Path.Combine(TestData.RepoRoot, "CSVM", "src");
        Assert.True(Directory.Exists(src), src);

        var call = new Regex(@"\bLog\.(?:Error|Warn|Info|Debug)\(\s*""([A-Za-z0-9_]+)""");
        var undeclared = new List<string>();
        int sites = 0;
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match m in call.Matches(File.ReadAllText(file)))
            {
                sites++;
                if (Array.IndexOf(Log.Categories, m.Groups[1].Value) < 0)
                {
                    undeclared.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
                }
            }
        }

        // The able-to-fail control: a walk that matched nothing would pass the check below.
        Assert.True(sites > 100, $"only {sites} literal-category call sites found under {src}");
        Assert.True(undeclared.Count == 0, "undeclared categories: " + string.Join(", ", undeclared));
    }
}
