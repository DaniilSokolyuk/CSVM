using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CSVM.Extraction;
using CSVM.Session.Launch;
using CSVM.UI.Screens;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The extraction screen's decisions, engine-free: whether a data root stops the launch and why,
/// and what the folder field starts with. The flow runs from a pick to done or failure, with a
/// fake runner standing in for <see cref="ExtractionRun.Run"/>.
/// </summary>
public class ExtractionFlowTests
{
    [Fact]
    public void AMissingTreeIsMissingAndAStampNamingAnotherSchemaIsStale()
    {
        string root = TestData.TempDir();
        Assert.Equal(DataProblem.Missing, ExtractionFlow.ProblemAt(root));

        string extracted = Path.Combine(root, "extracted");
        Directory.CreateDirectory(extracted);
        Assert.Equal(DataProblem.Missing, ExtractionFlow.ProblemAt(root));

        File.WriteAllText(Path.Combine(extracted, "planes.zip"), "x");
        Assert.Equal(DataProblem.None, ExtractionFlow.ProblemAt(root));

        Stamp(root, ExtractionStamp.Schema);
        Assert.Equal(DataProblem.None, ExtractionFlow.ProblemAt(root));

        Stamp(root, ExtractionStamp.Schema - 1);
        Assert.Equal(DataProblem.Older, ExtractionFlow.ProblemAt(root));

        Stamp(root, ExtractionStamp.Schema + 1);
        Assert.Equal(DataProblem.Newer, ExtractionFlow.ProblemAt(root));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{ \"assets\": {} }")]
    [InlineData("{ \"schema\": \"3\" }")]
    public void AnUnstampedOrUnreadableTreeKeepsWarnOnly(string stamp)
    {
        string root = TestData.TempDir();
        Directory.CreateDirectory(Path.Combine(root, "extracted"));
        File.WriteAllText(Path.Combine(root, "extracted", "VERSION.json"), stamp);

        Assert.Equal(StampStanding.Unstamped, ExtractionStamp.Standing(root, out var found));
        Assert.Null(found);
        Assert.Equal(DataProblem.None, ExtractionFlow.ProblemAt(root));
    }

    [Fact]
    public void TheFieldStartsWithTheRememberedInstallThenTheFirstCandidate()
    {
        Assert.Equal(@"C:\remembered", ExtractionFlow.PreFill(@"C:\remembered", new[] { @"C:\remembered", @"D:\other" }));
        Assert.Equal(@"D:\found", ExtractionFlow.PreFill(null, new[] { @"D:\found", @"E:\second" }));

        // A remembered folder that stopped being an install is not a candidate; a found one wins.
        Assert.Equal(@"D:\found", ExtractionFlow.PreFill(@"C:\gone", new[] { @"D:\found" }));
        Assert.Equal(@"C:\gone", ExtractionFlow.PreFill(@"C:\gone", Array.Empty<string>()));
        Assert.Equal(string.Empty, ExtractionFlow.PreFill(null, Array.Empty<string>()));
    }

    [Fact]
    public void APickThatIsNotAnInstallShowsTheCheckMessageAndExtractRunsNothing()
    {
        string root = TestData.TempDir();
        string notInstall = Directory.CreateDirectory(Path.Combine(root, "Documents")).FullName;
        var runner = new FakeRunner();
        var flow = Flow(root, runner, string.Empty);

        Assert.False(flow.Pick(notInstall));
        Assert.Equal(notInstall, flow.InstallPath);
        Assert.Equal(InstallLocator.Check(notInstall).Message, flow.Notice);

        Assert.False(flow.Extract());
        Assert.Equal(ExtractionView.Asking, flow.View);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void APickInsideTheInstallIsNamedNotCorrected()
    {
        string root = TestData.TempDir();
        string install = Install(root);
        var flow = Flow(root, new FakeRunner(), string.Empty);

        Assert.False(flow.Pick(Path.Combine(install, "ZBD")));
        Assert.Contains(install, flow.Notice);
        Assert.Equal(Path.Combine(install, "ZBD"), flow.InstallPath);

        Assert.True(flow.Pick(install));
        Assert.Null(flow.Notice);
    }

    [Fact]
    public void ASuccessfulRunReportsProgressRemembersTheInstallAndIsDone()
    {
        string root = TestData.TempDir();
        string install = Install(root);
        var remembered = new List<string>();
        var runner = new FakeRunner
        {
            Steps =
            {
                new ExtractionProgress(ExtractionPhase.Zbd, 0.4, new[] { "  c1/gamez.zbd", string.Empty }, null),
            },
        };
        var flow = Flow(root, runner, install, remembered.Add);

        Assert.True(flow.Extract());
        Assert.Equal(ExtractionView.Running, flow.View);
        Assert.Empty(remembered);

        flow.Tick();
        Assert.Equal(ExtractionView.Done, flow.View);
        Assert.Equal(new[] { install }, remembered);
        Assert.Equal(1, flow.Fraction);
        Assert.Equal("c1/gamez.zbd", flow.LatestLine);
        Assert.False(runner.LastRequest!.Force);
        Assert.False(runner.LastRequest.Unzip);
        Assert.Equal(root, runner.LastRequest.DataRoot);
    }

    [Fact]
    public void ProgressCrossesToTheScreenOnlyThroughTick()
    {
        string root = TestData.TempDir();
        string install = Install(root);
        using var release = new ManualResetEventSlim();
        using var reported = new ManualResetEventSlim();
        var flow = new ExtractionFlow(DataProblem.Missing, root, "unzbd", install, (request, progress, cancel) =>
        {
            progress(new ExtractionProgress(ExtractionPhase.Rof, 0.9, new[] { "  crimson.rof" }, null));
            reported.Set();
            release.Wait(cancel);
            return Succeeded(request);
        }, _ => { });

        Assert.True(flow.Extract());
        Assert.True(reported.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, flow.Fraction);

        flow.Tick();
        Assert.Equal(ExtractionView.Running, flow.View);
        Assert.Equal(ExtractionPhase.Rof, flow.Phase);
        Assert.Equal(0.9, flow.Fraction, 3);
        Assert.Equal("crimson.rof", flow.LatestLine);

        release.Set();
        WaitFor(flow, v => v != ExtractionView.Running);
        Assert.Equal(ExtractionView.Done, flow.View);
    }

    [Fact]
    public void AFailedRunShowsItsFailuresAndARetryRunsAgain()
    {
        string root = TestData.TempDir();
        string install = Install(root);
        var runner = new FakeRunner { Fail = "unzbd failed on c1/gamez.zbd" };
        var remembered = new List<string>();
        var flow = Flow(root, runner, install, remembered.Add);

        flow.Extract();
        flow.Tick();
        Assert.Equal(ExtractionView.Failed, flow.View);
        Assert.Equal(new[] { "unzbd failed on c1/gamez.zbd" }, flow.Failures);
        Assert.Empty(remembered);

        runner.Fail = null;
        Assert.True(flow.Extract());
        Assert.Empty(flow.Failures);
        flow.Tick();
        Assert.Equal(ExtractionView.Done, flow.View);
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public void ACrashIsAFailureNotAnEscape()
    {
        string root = TestData.TempDir();
        string install = Install(root);
        var flow = new ExtractionFlow(DataProblem.Missing, root, "unzbd", install,
            (_, _, _) => throw new InvalidOperationException("disk gone"), _ => { }, work => work());

        flow.Extract();
        flow.Tick();
        Assert.Equal(ExtractionView.Failed, flow.View);
        Assert.Contains("disk gone", flow.Failures[0]);
    }

    [Fact]
    public void CancelStopsTheRunAndReturnsToTheQuestion()
    {
        string root = TestData.TempDir();
        string install = Install(root);
        var remembered = new List<string>();
        var flow = new ExtractionFlow(DataProblem.Missing, root, "unzbd", install, (request, _, cancel) =>
        {
            cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            cancel.ThrowIfCancellationRequested();
            return Succeeded(request);
        }, remembered.Add);

        flow.Extract();
        flow.Cancel();
        Assert.True(flow.Cancelling);
        WaitFor(flow, v => v != ExtractionView.Running);

        Assert.Equal(ExtractionView.Asking, flow.View);
        Assert.Contains("cancelled", flow.Notice);
        Assert.Empty(remembered);
    }

    [Fact]
    public void AStaleTreeIsReExtractedWithForceAndUnpackedWhereItWasUnpacked()
    {
        string root = TestData.TempDir();
        string install = Install(root);
        string extracted = Directory.CreateDirectory(Path.Combine(root, "extracted")).FullName;
        File.WriteAllText(Path.Combine(extracted, "rimage.zip"), "x");
        Directory.CreateDirectory(Path.Combine(extracted, "rimage"));
        Assert.False(ExtractionFlow.HasUnpackedSiblings(extracted));

        var runner = new FakeRunner();
        var flow = Flow(root, runner, install, problem: DataProblem.Older);
        flow.Extract();
        Assert.True(runner.LastRequest!.Force);
        Assert.False(runner.LastRequest.Unzip);

        File.WriteAllText(Path.Combine(extracted, "zrdr.zip"), "x");
        Directory.CreateDirectory(Path.Combine(extracted, "zrdr"));
        Assert.True(ExtractionFlow.HasUnpackedSiblings(extracted));
        flow.Tick();
        flow.Extract();
        Assert.True(runner.LastRequest.Force);
        Assert.True(runner.LastRequest.Unzip);

        // A missing tree is extracted with the player defaults whatever lies next to it.
        var fresh = new FakeRunner();
        Flow(root, fresh, install).Extract();
        Assert.False(fresh.LastRequest!.Force);
        Assert.False(fresh.LastRequest.Unzip);
    }

    [Fact]
    public void ARunThatDoesNotFinishLeavesTheTreeIncompleteUntilOneDoes()
    {
        string root = TestData.TempDir();
        string install = Install(root);
        string marker = Path.Combine(root, "extracted", ExtractionFlow.UnfinishedMarker);
        var runner = new FakeRunner
        {
            OnRun = request => File.WriteAllText(Path.Combine(request.DataRoot, "extracted", "planes.zip"), "x"),
            Fail = "unzbd failed on c1/gamez.zbd",
        };

        var flow = Flow(root, runner, install);
        flow.Extract();
        Assert.True(File.Exists(marker));
        flow.Tick();
        Assert.Equal(ExtractionView.Failed, flow.View);
        Assert.Equal(DataProblem.Incomplete, ExtractionFlow.ProblemAt(root));

        // A stamp does not clear it: the partial tree may carry the ZBD half's stamp.
        Stamp(root, ExtractionStamp.Schema);
        Assert.Equal(DataProblem.Incomplete, ExtractionFlow.ProblemAt(root));

        var again = Flow(root, runner, install, problem: ExtractionFlow.ProblemAt(root));
        Assert.True(again.Force);
        runner.Fail = null;
        again.Extract();
        Assert.True(runner.LastRequest!.Force);
        again.Tick();
        Assert.Equal(ExtractionView.Done, again.View);
        Assert.False(File.Exists(marker));
        Assert.Equal(DataProblem.None, ExtractionFlow.ProblemAt(root));
    }

    private static ExtractionFlow Flow(string root, FakeRunner runner, string preFill, Action<string>? remember = null, DataProblem problem = DataProblem.Missing) =>
        new(problem, root, "unzbd", preFill, runner.Run, remember ?? (_ => { }), work => work());

    // Enough of an install for InstallLocator.Check: ZBD with one archive, and GOSDATA/ASSETS.
    private static string Install(string root)
    {
        string install = Path.Combine(root, "Crimson Skies");
        Directory.CreateDirectory(Path.Combine(install, "ZBD"));
        Directory.CreateDirectory(Path.Combine(install, "GOSDATA", "ASSETS"));
        File.WriteAllText(Path.Combine(install, "ZBD", "planes.zbd"), "x");
        return install;
    }

    private static void Stamp(string root, int schema) =>
        File.WriteAllText(Path.Combine(root, "extracted", "VERSION.json"), $"{{ \"schema\": {schema} }}");

    private static ExtractionResult Succeeded(ExtractionRequest request) =>
        new() { Install = InstallLocator.Check(request.InstallFolder) };

    private static void WaitFor(ExtractionFlow flow, Func<ExtractionView, bool> done)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < until)
        {
            flow.Tick();
            if (done(flow.View))
            {
                return;
            }

            Thread.Sleep(5);
        }

        Assert.Fail($"the flow stayed {flow.View}");
    }

    private sealed class FakeRunner
    {
        public List<ExtractionProgress> Steps { get; } = new();

        public string? Fail { get; set; }

        public Action<ExtractionRequest>? OnRun { get; set; }

        public int Calls { get; private set; }

        public ExtractionRequest? LastRequest { get; private set; }

        public ExtractionResult Run(ExtractionRequest request, Action<ExtractionProgress> progress, CancellationToken cancel)
        {
            Calls++;
            LastRequest = request;
            OnRun?.Invoke(request);
            foreach (var step in Steps)
            {
                progress(step);
            }

            var result = Succeeded(request);
            if (Fail != null)
            {
                result.Failures.Add(Fail);
            }

            return result;
        }
    }
}
