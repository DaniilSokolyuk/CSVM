using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text;
using CSVM.Utils;
using Xunit;
using Xunit.Abstractions;

namespace CSVM.Tests;

/// <summary>
/// The accumulate / freeze / snapshot cycle behind <see cref="PerfSample"/>:
/// what a scope adds, what <see cref="PerfSample.EndFrame"/> hands to the next record, the
/// remainder arithmetic, and the two properties the instrument's honesty rests on, a nested scope
/// cannot double-count, and a scope allocates nothing.
///
/// <para>⚠ This is the ONLY class that writes <see cref="PerfSample"/>'s ambient statics. xUnit runs
/// test classes in parallel but the tests within one class serially, so that is what keeps these
/// deterministic; a second class exercising scopes would have to join this one.</para>
/// </summary>
public class PerfSampleTests
{
    // How many independent measurement windows the allocation tests open. Every one of them must
    // read exactly zero for the scope path to count as allocation-free, since an allocator that
    // charges only some windows is the shape such a path fails in.
    private const int Windows = 5;

    // Scope opens per window, and allocations per window in the negative controls. High enough that
    // one small object per open is thousands of bytes against a counter that is exact.
    private const int WindowOpens = 10_000;

    private readonly ITestOutputHelper _out;

    public PerfSampleTests(ITestOutputHelper output)
    {
        _out = output;
        PerfSample.Reset();
    }

    // ---- what a scope accumulates ----

    [Fact]
    public void ScopesAccumulatePerSiteAndCountTheirCalls()
    {
        using (PerfSample.Scope(PerfSite.DebrisSpawn))
        {
            Spin();
        }
        using (PerfSample.Scope(PerfSite.DebrisSpawn))
        {
            Spin();
        }
        using (PerfSample.Scope(PerfSite.AiSpawn))
        {
            Spin();
        }

        var frame = Snapshot(frameMs: 100);
        Assert.Equal(2, frame.Calls[(int)PerfSite.DebrisSpawn]);
        Assert.Equal(1, frame.Calls[(int)PerfSite.AiSpawn]);
        Assert.True(frame.Ms[(int)PerfSite.DebrisSpawn] > 0);
        Assert.Equal(0, frame.Calls[(int)PerfSite.ResourceLoad]);
        Assert.Equal(0.0, frame.Ms[(int)PerfSite.ResourceLoad]);
    }

    [Fact]
    public void AFrameOnlyReportsItsOwnScopes()
    {
        using (PerfSample.Scope(PerfSite.DebrisSpawn))
        {
            Spin();
        }
        Snapshot(frameMs: 100);

        // Nothing ran in the frame after it, so the next record must not repeat the one before.
        var second = Snapshot(frameMs: 100);
        Assert.Equal(0, second.Calls[(int)PerfSite.DebrisSpawn]);
        Assert.Equal(0.0, second.AttributedMs);
        Assert.Equal(100.0, second.UnattributedMs, 9);
    }

    [Fact]
    public void SnapshotReportsTheClosedFrameNotTheOneInProgress()
    {
        using (PerfSample.Scope(PerfSite.DebrisSpawn))
        {
            Spin();
        }
        PerfSample.EndFrame();

        // Work in the frame now in progress belongs to the NEXT record: a hitch record is filled at
        // the top of the frame after the one it describes, so a snapshot that read the live
        // accumulators would credit the hitching frame with work that has not happened yet.
        using (PerfSample.Scope(PerfSite.AiSpawn))
        {
            Spin();
        }
        var frame = new PerfSampleFrame();
        PerfSample.SnapshotInto(frame, 100);
        Assert.Equal(1, frame.Calls[(int)PerfSite.DebrisSpawn]);
        Assert.Equal(0, frame.Calls[(int)PerfSite.AiSpawn]);
    }

    // ---- the remainder is whatever the frame cost that no scope claimed ----

    [Fact]
    public void AttributedPlusUnattributedIsExactlyTheFrameCost()
    {
        using (PerfSample.Scope(PerfSite.MaterialCreate))
        {
            Spin();
        }

        var frame = Snapshot(frameMs: 62.5);
        Assert.Equal(62.5, frame.AttributedMs + frame.UnattributedMs, 9);
        Assert.True(frame.AttributedMs > 0);
        Assert.True(frame.UnattributedMs < 62.5);
    }

    [Fact]
    public void AFrameWithNoScopesIsEntirelyUnattributed()
    {
        var frame = Snapshot(frameMs: 47.0);
        Assert.Equal(0.0, frame.AttributedMs);
        Assert.Equal(47.0, frame.UnattributedMs, 9);
        Assert.Equal(0, frame.Violations);
    }

    [Fact]
    public void TheRemainderIsNotClamped()
    {
        using (PerfSample.Scope(PerfSite.ResourceLoad))
        {
            Spin();
        }

        // A frame cheaper than the work attributed to it means a scope spanned the frame boundary.
        // Clamping to zero would hide exactly that, so it reads negative instead.
        var frame = Snapshot(frameMs: 0);
        Assert.True(frame.UnattributedMs < 0);
    }

    // ---- flat leaves only: a nested scope is suppressed, never double-counted ----

    [Fact]
    public void ANestedScopeIsCountedAsAViolationAndMeasuresNothing()
    {
        using (PerfSample.Scope(PerfSite.DebrisSpawn))
        {
            using (PerfSample.Scope(PerfSite.EffectCheckout))
            {
                Spin();
            }
            Spin();
        }

        var frame = Snapshot(frameMs: 100);
        Assert.Equal(1, frame.Calls[(int)PerfSite.DebrisSpawn]);
        Assert.Equal(0, frame.Calls[(int)PerfSite.EffectCheckout]);
        Assert.Equal(0.0, frame.Ms[(int)PerfSite.EffectCheckout]);
        Assert.Equal(1, frame.Violations);
        // The identity survives the violation, which is the point of suppressing rather than
        // adding: the inner scope's time is inside the outer's and is reported once.
        Assert.Equal(100.0, frame.AttributedMs + frame.UnattributedMs, 9);
    }

    [Fact]
    public void TheOuterScopeStillRecordsAfterASuppressedInnerOne()
    {
        using (PerfSample.Scope(PerfSite.DebrisSpawn))
        {
            using (PerfSample.Scope(PerfSite.EffectCheckout))
            {
                Spin();
            }
        }
        using (PerfSample.Scope(PerfSite.AiSpawn))
        {
            Spin();
        }

        // The suppressed scope must not leave the gate shut behind it: everything after it would
        // silently stop being measured, which is the failure mode this instrument cannot have.
        var frame = Snapshot(frameMs: 100);
        Assert.Equal(1, frame.Calls[(int)PerfSite.DebrisSpawn]);
        Assert.Equal(1, frame.Calls[(int)PerfSite.AiSpawn]);
        Assert.Equal(1, frame.Violations);
    }

    [Fact]
    public void AnUnknownSiteIsAViolationRatherThanAnIndexOutOfRange()
    {
        using (PerfSample.Scope((PerfSite)99))
        {
            Spin();
        }

        var frame = Snapshot(frameMs: 100);
        Assert.Equal(1, frame.Violations);
        Assert.Equal(0.0, frame.AttributedMs);
    }

    [Fact]
    public void AScopeLeftOpenAcrossAFrameBoundaryIsAViolationAndDoesNotJamTheNextFrame()
    {
        var scope = PerfSample.Scope(PerfSite.AudioLoad);
        PerfSample.EndFrame();
        var straddled = new PerfSampleFrame();
        PerfSample.SnapshotInto(straddled, 100);
        Assert.Equal(1, straddled.Violations);

        scope.Dispose();
        using (PerfSample.Scope(PerfSite.DebrisSpawn))
        {
            Spin();
        }
        var next = Snapshot(frameMs: 100);
        Assert.Equal(1, next.Calls[(int)PerfSite.DebrisSpawn]);
        Assert.Equal(0, next.Violations);
    }

    // ---- Reset drops both frames, for a build or a teardown ----

    [Fact]
    public void ResetDropsTheFrameInProgressAndTheOneBeforeIt()
    {
        using (PerfSample.Scope(PerfSite.ResourceLoad))
        {
            Spin();
        }
        PerfSample.EndFrame();
        using (PerfSample.Scope(PerfSite.ResourceLoad))
        {
            Spin();
        }

        PerfSample.Reset();
        var frame = Snapshot(frameMs: 100);
        Assert.Equal(0.0, frame.AttributedMs);
        Assert.Equal(0, frame.Calls[(int)PerfSite.ResourceLoad]);
    }

    // ---- the site vocabulary ----

    [Fact]
    public void EverySiteHasItsOwnWireName()
    {
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (PerfSite site in Enum.GetValues<PerfSite>())
        {
            string name = PerfSample.NameOf(site);
            Assert.NotEqual("unknown", name);
            // Written into the sidecar's hand-built JSON as-is, so nothing here may need escaping.
            Assert.Matches("^[a-z][a-z_]*$", name);
            Assert.True(seen.Add(name), $"duplicate site name {name}");
        }
        Assert.Equal(Enum.GetValues<PerfSite>().Length, PerfSample.SiteCount);
    }

    // ---- a record carries the frame's attribution, filled by the monitor itself ----

    [Fact]
    public void AHitchRecordCarriesTheAttributionOfTheFrameItDescribes()
    {
        var idle = new FrameCounters(1, 2, 3, 0.5, 100, 5000, 400, 1024);
        var mon = new HitchMonitor(4, 40, 8, 8, 0);
        using (PerfSample.Scope(PerfSite.PartDetach))
        {
            Spin();
        }
        PerfSample.EndFrame();

        Assert.True(mon.Tick(500, idle));
        Assert.Equal(1, mon.Last.Samples.Calls[(int)PerfSite.PartDetach]);
        Assert.Equal(500.0, mon.Last.Samples.AttributedMs + mon.Last.Samples.UnattributedMs, 9);
    }

    // ---- the two properties that decide whether this can be left on, and their controls ----

    [Fact]
    public void AScopeAllocatesNothing()
    {
        // An identical unmeasured warm-up loop pays off the runtime's own deferred work (tiered
        // JIT recompilation, OSR) so the windows below measure the scope alone.
        OpenScopes();

        // Exactly zero in every window, not a tolerance and not one clean window of several: an
        // allocator that charges intermittently passes any weaker rule, and an instrument that
        // allocates manufactures the collections it exists to catch (docs/verification.md PERF-28).
        int firstCharged = MeasureWindows(OpenScopes, out long[] charged, out int[] collections);

        // The per-window bytes are unrecoverable after the run, and the next reader of a flake needs
        // the clean runs as much as the red one, so they are written whichever way this goes.
        string readings = Readings(charged, collections, firstCharged);
        _out.WriteLine(readings);
        Assert.True(firstCharged < 0, readings);
    }

    [Fact]
    public void TheWindowsCatchAnAllocatorThatChargesEveryWindow()
    {
        // The negative control on the measurement itself: a harness reading nothing at all would
        // look exactly like a clean scope path, so a body that allocates on every open has to
        // charge its first window and stop the run there.
        int firstCharged = MeasureWindows(Allocate, out long[] charged, out int[] collections);
        string readings = Readings(charged, collections, firstCharged);
        _out.WriteLine(readings);
        Assert.Equal(0, firstCharged);
        Assert.True(charged[0] >= WindowOpens, readings);
    }

    [Fact]
    public void TheWindowsCatchAnAllocatorThatChargesOneWindowOfFive()
    {
        // The control for the shape a one-clean-window rule accepted: a body that allocates in its
        // third window alone, clean in the two before it. Requiring every window clean is the only
        // reason this reads red.
        int opened = 0;
        int firstCharged = MeasureWindows(
            () =>
            {
                if (opened++ == 2)
                {
                    Allocate();
                }
            },
            out long[] charged,
            out int[] collections);
        string readings = Readings(charged, collections, firstCharged);
        _out.WriteLine(readings);
        Assert.True(firstCharged >= 0, readings);
    }

    [Fact]
    public void AScopeCostsFarLessThanTheFrameItMeasures()
    {
        const int Iterations = 200_000;
        for (int i = 0; i < 1000; i++)
        {
            using (PerfSample.Scope(PerfSite.DebrisSpawn))
            {
            }
        }

        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < Iterations; i++)
        {
            using (PerfSample.Scope(PerfSite.DebrisSpawn))
            {
            }
        }
        double ns = (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency / Iterations;
        _out.WriteLine($"PerfSample.Scope: {ns:0.0} ns per open+close");

        // A ceiling well above measured cost, not a benchmark: catches a lock, allocation or
        // dictionary lookup landing on the scope path.
        Assert.True(ns < 2000, $"a scope cost {ns:0.0} ns, something expensive is on the path");
        PerfSample.Reset();
    }

    // Runs body once per window, reading the thread's allocated bytes and the gen-0 count across
    // each, and stops at the first window that charged anything. Returns that window's index, or
    // -1 when every window read exactly zero. AiStepCostTests measures its bracket through it too.
    internal static int MeasureWindows(Action body, out long[] charged, out int[] collections)
    {
        charged = new long[Windows];
        collections = new int[Windows];
        for (int w = 0; w < Windows; w++)
        {
            // Opens the window on an emptied allocation context. The counter steps up by whatever
            // that context still held when the runtime retires it, which charges a window that
            // allocated nothing (docs/verification.md PERF-29).
            GC.Collect(0, GCCollectionMode.Forced, true);
            int gen0 = GC.CollectionCount(0);
            long before = GC.GetAllocatedBytesForCurrentThread();
            body();
            charged[w] = GC.GetAllocatedBytesForCurrentThread() - before;
            collections[w] = GC.CollectionCount(0) - gen0;
            if (charged[w] != 0)
            {
                return w;
            }
        }

        return -1;
    }

    // The scope path under measurement: one open and close per iteration and nothing else, so a
    // window's reading is the scope's own.
    private static void OpenScopes()
    {
        for (int i = 0; i < WindowOpens; i++)
        {
            using (PerfSample.Scope(PerfSite.DebrisSpawn))
            {
            }
        }
    }

    // The negative controls' body. The object goes to a call the runtime cannot inline away, since
    // a runtime may elide an allocation that escapes nowhere.
    private static void Allocate()
    {
        for (int i = 0; i < WindowOpens; i++)
        {
            GC.KeepAlive(new object());
        }
    }

    // What a reader of a red allocation run needs and cannot get afterwards: which windows were
    // charged and by how much, whether a collection crossed each one, and which binary and thread
    // produced the readings.
    private static string Readings(long[] charged, int[] collections, int firstCharged)
    {
        int opened = firstCharged >= 0 ? firstCharged + 1 : charged.Length;
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"scope allocation over {opened} window(s): ");
        for (int w = 0; w < opened; w++)
        {
            text.Append(CultureInfo.InvariantCulture, $"[{w}] {charged[w]} bytes, {collections[w]} gen0; ");
        }

        text.Append(CultureInfo.InvariantCulture, $"thread {Environment.CurrentManagedThreadId}, ");
        text.Append(CultureInfo.InvariantCulture, $"{Environment.ProcessorCount} cpus, ");
        text.Append(CultureInfo.InvariantCulture, $"server GC {GCSettings.IsServerGC}, ");
        text.Append(CultureInfo.InvariantCulture, $"module {typeof(PerfSample).Assembly.ManifestModule.ModuleVersionId}");
        return text.ToString();
    }

    // Enough work that a scope's span is non-zero on a coarse timer, without a Thread.Sleep's
    // scheduler quantum.
    private static void Spin()
    {
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetTimestamp() - start < Stopwatch.Frequency / 10_000)
        {
        }
    }

    private static PerfSampleFrame Snapshot(double frameMs)
    {
        PerfSample.EndFrame();
        var frame = new PerfSampleFrame();
        PerfSample.SnapshotInto(frame, frameMs);
        return frame;
    }
}
