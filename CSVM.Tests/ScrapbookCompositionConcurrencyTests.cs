using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using CSVM.UI.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>The three per-path memos under several threads at once. xUnit runs test classes in
/// parallel and more than one of them reaches this composition, so an insert racing a read throws
/// out of <c>Dictionary.TryInsert</c> and the parsed rows are lost. Each thread walks its own
/// unseen paths, whose misses are memoised too, so all six insert while all six read.</summary>
public class ScrapbookCompositionConcurrencyTests
{
    // Sized so the inserts overlap a rehash rather than merely following one: a memo grown to
    // sixteen thousand keys rehashes twelve times, and the later rehashes are long enough that the
    // other seven threads insert into the table while it is being rebuilt.
    private const int Threads = 8;
    private const int PerThread = 2000;

    [Fact]
    public void ParallelReadersAndWritersLeaveThePerPathMemosIntact()
    {
        string shared = ScrapbookCompositionFixture.WriteResolvableScrap(TestData.TempDir(), mission: 1);
        var failures = new ConcurrentQueue<Exception>();
        var start = new Barrier(Threads);
        var workers = new Thread[Threads];

        for (int t = 0; t < Threads; t++)
        {
            int worker = t;
            workers[t] = new Thread(() => Hammer(worker, shared, start, failures));
            workers[t].Start();
        }

        foreach (var thread in workers)
        {
            thread.Join();
        }

        if (failures.TryDequeue(out var first))
        {
            Assert.Fail($"{failures.Count + 1} of {Threads} threads failed, first: {first}");
        }
    }

    private static void Hammer(int worker, string shared, Barrier start, ConcurrentQueue<Exception> failures)
    {
        try
        {
            start.SignalAndWait();
            for (int i = 0; i < PerThread; i++)
            {
                // No file is written under this root: a miss is memoised the same way a hit is, so
                // the insert costs one File.Exists and the window stays as wide as it can be.
                string unseen = Path.Combine(TestData.TempRoot, $"race-{worker}-{i}");
                Assert.Empty(ScrapbookComposition.Items(unseen, 1, 1));
                Assert.Null(ScrapbookComposition.ZoomFamily(unseen, 'M'));
                Assert.Null(ScrapbookComposition.StringId(unseen, "IDS_TEST_TITLE"));

                Assert.Single(ScrapbookComposition.Items(shared, 1, 1));
                Assert.NotNull(ScrapbookComposition.ZoomFamily(shared, 'M'));
                Assert.Equal(40002, ScrapbookComposition.StringId(shared, "IDS_TEST_TITLE"));
            }
        }
        catch (Exception ex)
        {
            failures.Enqueue(ex);
        }
    }
}
