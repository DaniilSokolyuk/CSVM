using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CSVM.Effects;
using CSVM.Mech3;
using CSVM.Mech3.Anim;
using CSVM.Utils;
using Godot;

namespace CSVM.Testing;

/// <summary>How one suite ended. <see cref="Skip"/> is reported separately from
/// <see cref="Pass"/> on purpose: "the data was not there" must never read as "the check
/// held".</summary>
public enum SuiteStatus
{
    Pass,
    Fail,
    Skip,
}

/// <summary>
/// The in-engine test harness behind <c>--run-tests[=filter]</c>: a registry of assertion suites,
/// a PASS/FAIL/SKIP table, a <c>.scratch/test-report.json</c>, and a nonzero exit code on failure.
/// Scope and error policy: this module's entry in docs/architecture.md. Windowed-run rule:
/// docs/verification.md LOG-8.
/// ⚠ In-engine is the SMALLER half. Only a check that needs a live Godot belongs here; anything
/// that runs without the engine belongs in <c>CSVM.Tests</c> (<c>dotnet test</c>) instead.
/// </summary>
public static class TestHarness
{
    /// <summary>The <c>test-report.json</c> schema, bumped when a field's meaning changes or one is
    /// removed; a reader keys off this before a field name, the same rule the golden manifest's
    /// own <c>schema</c> follows. 1 was the unversioned shape; 2 adds the phase-attribution block;
    /// 3 adds the <c>shard</c> block and per-suite <c>index</c>, and moves a sharded run's report
    /// out of <c>.scratch/</c> into the shard's own subdirectory.</summary>
    public const int ReportSchema = 3;

    /// <summary>The engine errors this project currently emits that are not the harness's to fix.
    /// Every entry names the open item that owns it; when that item lands, the entry is deleted and
    /// the cap does the rest.</summary>
    public static readonly IReadOnlyList<ErrorAllowance> ErrorAllowlist = new[]
    {
        // Godot's own Basis::invert guard in the destructible death path: a print-and-return C++
        // macro, not a managed throw, so the run completes and every later row still reports.
        // Cap set just above the worst measured chapter (C2 4) so a new source still trips it.
        new ErrorAllowance(@"Condition ""det == 0"" is true\.", 8,
            "pre-existing singular-basis guard in the destructible death path (backlog: det == 0 invert error)"),
    };

    // The engine's own error format (print_error): "ERROR: …", "SCRIPT ERROR: …", "USER ERROR: …".
    // Log's own error lines read "ERROR [cat] …", no colon, and are deliberately out of scope
    // here, because the suite that emitted one has already failed on it.
    private static readonly Regex EngineError = new(@"^(ERROR|SCRIPT ERROR|USER ERROR): ", RegexOptions.Compiled);

    private static readonly List<Suite> Registry = new();

    /// <summary>The registry, built on first use. ⚠ Keep the lock: the engine reaches this from one
    /// thread, but xUnit runs test classes in parallel, and two concurrent first uses left the list
    /// holding null entries.</summary>
    public static IReadOnlyList<Suite> All
    {
        get
        {
            lock (Registry)
            {
                if (Registry.Count == 0)
                {
                    Registry.AddRange(SuiteCatalog.Discover());
                }
                return Registry;
            }
        }
    }

    /// <summary>Runs the suites <paramref name="filter"/> selects (empty = all), prints the table,
    /// writes <c>.scratch/test-report.json</c>, and returns the process exit code: 0 when nothing
    /// failed, 1 otherwise. A skipped suite is not a failure; a selector term that matched nothing
    /// is, and nothing runs in that case.</summary>
    public static int Run(TestContext ctx, string filter)
    {
        // Numbers in a committed report must read the same on every machine.
        System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        var shard = SuiteShards.Parse(filter, out string terms, out string? shardError);
        if (shardError != null)
        {
            Log.Error("test", $"run-tests {shardError}");
            return 1;
        }
        var selected = Select(All, terms, out var unmatched);
        var results = new List<SuiteResult>();
        Log.Info("test", $"run-tests suites={selected.Count}/{All.Count} filter='{filter}' chapter={ctx.Chapter} mission={ctx.Mission}");
        if (unmatched.Count > 0)
        {
            Log.Error("test", $"run-tests selector matched nothing: {string.Join(", ", unmatched)}; registered: {string.Join(", ", All.Select(s => s.Name))}");
            return 1;
        }
        if (selected.Count == 0)
        {
            Log.Error("test", $"run-tests filter '{filter}' matched no suite of {string.Join(", ", All.Select(s => s.Name))}");
            return 1;
        }

        // Sharding narrows an already-valid selection, so it runs AFTER the miss checks above: an
        // empty shard is a legitimate division of a small set, an empty selector is a typo.
        var plan = PlanShard(ctx, shard, ref selected);
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < All.Count; i++)
        {
            order[All[i].Name] = i;
        }

        var totals = new PhaseAttribution.Categorized();
        double totalBuildSeconds = 0, totalDisposalSeconds = 0, totalRestSeconds = 0, totalOverrunSeconds = 0;
        int totalWorldsBuilt = 0;
        foreach (var suite in selected)
        {
            ctx.Failures.Clear();
            ctx.Notes.Clear();
            ctx.Counts.Clear();
            ctx.ResetForSuite();
            // Every suite starts from the same stream positions, so its verdict is a function of
            // the suite rather than of how many draws its predecessors left in this one process.
            Rng.Rewind();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            SuiteStatus status;
            string detail = suite.What;
            try
            {
                suite.Body(ctx);
                status = ctx.Failures.Count == 0 ? SuiteStatus.Pass : SuiteStatus.Fail;
            }
            catch (SuiteSkippedException e)
            {
                status = SuiteStatus.Skip;
                detail = e.Message;
            }
            catch (Exception e)
            {
                status = SuiteStatus.Fail;
                detail = $"{e.GetType().Name}: {e.Message}";
                ctx.Failures.Add(detail);
                Log.Error("test", $"suite threw name={suite.Name}", e);
            }
            watch.Stop();
            double wallSeconds = watch.Elapsed.TotalSeconds;
            double buildSeconds = ctx.WorldBuildSeconds;
            double disposalSeconds = ctx.DisposalSeconds;
            double restSeconds = PhaseAttribution.Rest(wallSeconds, buildSeconds, disposalSeconds);
            double overrunSeconds = PhaseAttribution.Overrun(wallSeconds, buildSeconds, disposalSeconds);
            results.Add(new SuiteResult
            {
                Name = suite.Name,
                Index = order[suite.Name],
                Status = status,
                Seconds = wallSeconds,
                Detail = detail,
                Failures = ctx.Failures.ToList(),
                Notes = ctx.Notes.ToList(),
                Counts = new Dictionary<string, long>(ctx.Counts),
                WorldsBuilt = ctx.WorldsBuilt,
                BuildSeconds = buildSeconds,
                ArchiveDecodeSeconds = ctx.WorldBuildPhases.ArchiveDecodeMs / 1000.0,
                SoundPrepSeconds = ctx.WorldBuildPhases.SoundPrepMs / 1000.0,
                RuntimeConstructionSeconds = ctx.WorldBuildPhases.RuntimeConstructionMs / 1000.0,
                OtherBuildSeconds = ctx.WorldBuildPhases.OtherMs / 1000.0,
                DisposalSeconds = disposalSeconds,
                RestSeconds = restSeconds,
                OverrunSeconds = overrunSeconds,
            });
            totals += ctx.WorldBuildPhases;
            totalBuildSeconds += buildSeconds;
            totalDisposalSeconds += disposalSeconds;
            totalRestSeconds += restSeconds;
            totalOverrunSeconds += overrunSeconds;
            totalWorldsBuilt += ctx.WorldsBuilt;
            // One line per suite: the phase breakdown rides on the existing verdict line rather
            // than adding one, and only for a suite that actually built a world, a no-world suite
            // has nothing to attribute (the ⚠ trap this item names: chatter on the timing path).
            string phaseSuffix = ctx.WorldsBuilt > 0
                ? $" worlds={ctx.WorldsBuilt} build={buildSeconds:0.00}s"
                  + $" (decode={ctx.WorldBuildPhases.ArchiveDecodeMs / 1000.0:0.00}s"
                  + $" sound={ctx.WorldBuildPhases.SoundPrepMs / 1000.0:0.00}s"
                  + $" rt={ctx.WorldBuildPhases.RuntimeConstructionMs / 1000.0:0.00}s"
                  + $" other={ctx.WorldBuildPhases.OtherMs / 1000.0:0.00}s)"
                  + $" disposal={disposalSeconds:0.00}s rest={restSeconds:0.00}s"
                : "";
            Log.Info("test", $"suite {suite.Name} {status.ToString().ToUpperInvariant()} in {wallSeconds:0.00}s{phaseSuffix}");
        }
        var releaseWatch = System.Diagnostics.Stopwatch.StartNew();
        ctx.ReleaseWorlds();
        releaseWatch.Stop();
        double finalDisposalSeconds = releaseWatch.Elapsed.TotalSeconds;

        double totalWallSeconds = results.Sum(r => r.Seconds);
        string totalsLine = FormatTotalsLine(totalWallSeconds, totalWorldsBuilt, totalBuildSeconds,
            totals, totalDisposalSeconds, finalDisposalSeconds, totalRestSeconds, totalOverrunSeconds);
        var (decodeHits, decodeMisses) = ctx.DecodeCounts;
        Log.Info("test", $"{totalsLine} decode_hits={decodeHits} decode_misses={decodeMisses}");

        var screen = ScreenEngineLog(out string? logPath);
        int pass = results.Count(r => r.Status == SuiteStatus.Pass);
        int fail = results.Count(r => r.Status == SuiteStatus.Fail);
        int skip = results.Count(r => r.Status == SuiteStatus.Skip);
        // An unknown error fails the run; an over-cap allowance fails it; no log SKIPs the engine
        // errors row, it never counts as a pass, but it never fails the run either.
        bool screenFailed = screen is { Ok: false };

        var phaseTotals = new PhaseTotals(totals, totalBuildSeconds, totalDisposalSeconds,
            finalDisposalSeconds, totalRestSeconds, totalOverrunSeconds, totalWorldsBuilt, totalWallSeconds);
        Log.Raw(FormatTable(results, screen, logPath));
        WriteReport(ctx, results, screen, logPath, phaseTotals, filter, plan);
        string errors = screen == null ? "unscreened" : screen.Ok ? "clean" : "UNEXPECTED";
        Log.Info("test", $"run-tests pass={pass} fail={fail} skip={skip} errors={errors}");
        return fail > 0 || screenFailed ? 1 : 0;
    }

    /// <summary>The suites a <c>--run-tests=</c> spec selects, in registry order and deduplicated.
    /// Comma-separated terms, unioned: <c>suite:&lt;name&gt;</c> exact, <c>tier:&lt;name&gt;</c> a
    /// checked-in tier, anything else a name substring. A term that selects nothing lands in
    /// <paramref name="unmatched"/>, and <c>Run</c> refuses the whole run before any suite starts
    /// rather than quietly narrowing it: an empty shard is a legitimate division, an empty selector
    /// is a typo. Pure, so a selector can be proved outside the engine.</summary>
    public static IReadOnlyList<Suite> Select(IReadOnlyList<Suite> suites, string spec,
        out IReadOnlyList<string> unmatched)
    {
        var missed = new List<string>();
        unmatched = missed;
        if (spec.Trim().Length == 0)
        {
            return suites;
        }
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in spec.Split(','))
        {
            string term = raw.Trim();
            if (term.Length == 0)
            {
                continue;
            }
            var hits = MatchTerm(suites, term);
            if (hits.Count == 0)
            {
                missed.Add(term);
                continue;
            }
            foreach (var hit in hits)
            {
                wanted.Add(hit.Name);
            }
        }
        return suites.Where(s => wanted.Contains(s.Name)).ToList();
    }

    /// <summary>Classifies a run's log lines: how many engine error lines there were, how many the
    /// allowlist covers, which are unknown, and which allowed pattern went over its cap. Pure, no
    /// Godot API, no file access, so it is unit-testable outside the engine.</summary>
    public static StderrScreen Screen(IEnumerable<string> lines)
    {
        var counts = new Dictionary<string, int>();
        var unexpected = new List<string>();
        int total = 0, allowed = 0;
        string? pending = null;   // the previous error line, so its "   at: …" location joins it
        foreach (string line in lines)
        {
            if (pending != null && line.StartsWith("   at: ", StringComparison.Ordinal))
            {
                unexpected[^1] = $"{pending} {line.Trim()}";
                pending = null;
                continue;
            }
            pending = null;
            if (!EngineError.IsMatch(line))
            {
                continue;
            }
            total++;
            var match = ErrorAllowlist.FirstOrDefault(a => Regex.IsMatch(line, a.Pattern));
            if (match != null)
            {
                counts.TryGetValue(match.Pattern, out int n);
                counts[match.Pattern] = n + 1;
                allowed++;
                continue;
            }
            unexpected.Add(line);
            pending = line;
        }
        var overCap = new List<string>();
        foreach (var allowance in ErrorAllowlist)
        {
            counts.TryGetValue(allowance.Pattern, out int n);
            if (n > allowance.Max)
            {
                overCap.Add($"{allowance.Pattern} seen {n}x, allowed {allowance.Max}x, {allowance.Why}");
            }
        }
        return new StderrScreen
        {
            Total = total,
            Allowed = allowed,
            Unexpected = unexpected,
            OverCap = overCap,
            AllowedCounts = counts,
        };
    }

    // Narrows an already-valid selection to one shard and tags the context, so every artifact this
    // process writes lands under .scratch/<tag>/ instead of over a sibling shard's.
    private static ShardPlan PlanShard(TestContext ctx, ShardSpec? shard, ref IReadOnlyList<Suite> selected)
    {
        int total = selected.Count;
        if (shard is not { } s || s.Count < 2)
        {
            return new ShardPlan(1, 1, total, "", Array.Empty<string>());
        }
        var weights = SuiteShards.Load(Path.Combine(ctx.RepoRoot, "analysis", "engine-suite-weights.json"));
        var unweighted = SuiteShards.Unweighted(selected.Select(x => x.Name), weights);
        selected = SuiteShards.Plan(selected, x => x.Name, weights, s.Count)[s.Index - 1];
        // Beside this shard's own engine log, which the launcher already made unique per run: that
        // is what keeps two concurrent RunTests.ps1 invocations from writing one another's reports.
        string root = Path.GetDirectoryName(EngineLogPath() ?? "") ?? "";
        ctx.ShardScratch = Path.Combine(
            root.Length > 0 ? root : Path.Combine(ctx.RepoRoot, ".scratch"),
            $"shard{s.Index}of{s.Count}");
        Log.Info("test", $"run-tests shard={s.Index}/{s.Count} suites={selected.Count}/{total} unweighted={unweighted.Count} weights='{weights.Source}' scratch={ctx.ScratchDir}");
        return new ShardPlan(s.Index, s.Count, total, weights.Source, unweighted);
    }

    private static List<Suite> MatchTerm(IReadOnlyList<Suite> suites, string term)
    {
        const string exact = "suite:";
        const string tier = "tier:";
        if (term.StartsWith(exact, StringComparison.OrdinalIgnoreCase))
        {
            string name = term[exact.Length..];
            return suites.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (term.StartsWith(tier, StringComparison.OrdinalIgnoreCase))
        {
            var names = SuiteCatalog.Tier(term[tier.Length..]);
            if (names == null)
            {
                return new List<Suite>();
            }
            var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            return suites.Where(s => set.Contains(s.Name)).ToList();
        }
        return suites.Where(s => s.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // The engine log Godot's `--log-file` is writing, or null when launched without it. Error
    // lines are flushed as printed, so reading it while the process holds it open is sound.
    // ⚠ Read the command line directly, not `OS.GetCmdlineArgs()`: Godot hands that back only
    // args its own parser did not recognise, and `--log-file` is one it consumes. Falls back to
    // the project's own rotating log (desktop default) if the flag is absent.
    private static string? EngineLogPath()
    {
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--log-file")
            {
                return args[i + 1];
            }
        }
        string setting = ProjectSettings.GetSetting("debug/file_logging/log_path").AsString();
        if (setting.Length == 0)
        {
            return null;
        }
        string fallback = ProjectSettings.GlobalizePath(setting);
        // Only if this run is the one writing it: file logging can be off, and screening a
        // previous run's leftover log would report someone else's errors as this run's.
        if (File.Exists(fallback)
            && File.GetLastWriteTime(fallback) >= System.Diagnostics.Process.GetCurrentProcess().StartTime)
        {
            return fallback;
        }
        return null;
    }

    private static StderrScreen? ScreenEngineLog(out string? logPath)
    {
        logPath = EngineLogPath();
        if (logPath == null)
        {
            return null;
        }
        try
        {
            // Godot still owns the handle; share it rather than fighting for exclusive access.
            using var stream = new FileStream(logPath, FileMode.Open, System.IO.FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
            return Screen(lines);
        }
        catch (Exception e)
        {
            Log.Warn("test", $"engine log unreadable path={logPath} error={e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    // Plain string.Format, not Log's FormattableString overload: this one line sums several
    // already-rounded figures rather than naming one check, so it does not belong in the
    // Check/Note vocabulary the rest of the harness's logging goes through.
    private static string FormatTotalsLine(double wallSeconds, int worldsBuilt, double buildSeconds,
        PhaseAttribution.Categorized build, double disposalSeconds, double finalDisposalSeconds,
        double restSeconds, double overrunSeconds)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "phase totals wall={0:0.00}s worlds={1} build={2:0.00}s "
            + "(decode={3:0.00}s sound={4:0.00}s rt={5:0.00}s other={6:0.00}s) "
            + "disposal={7:0.00}s final_disposal={8:0.00}s rest={9:0.00}s overrun={10:0.00}s",
            wallSeconds, worldsBuilt, buildSeconds,
            build.ArchiveDecodeMs / 1000.0, build.SoundPrepMs / 1000.0,
            build.RuntimeConstructionMs / 1000.0, build.OtherMs / 1000.0,
            disposalSeconds, finalDisposalSeconds, restSeconds, overrunSeconds);
    }

    private static string FormatTable(List<SuiteResult> results, StderrScreen? screen, string? logPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("--- run-tests -----------------------------------------------------------");
        int width = results.Count == 0 ? 4 : results.Max(r => r.Name.Length);
        foreach (var r in results)
        {
            sb.AppendLine($"  {Tag(r.Status)}  {r.Name.PadRight(width)}  {r.Seconds,6:0.00}s  {r.Detail}");
            foreach (string f in r.Failures)
            {
                sb.AppendLine($"          !! {f}");
            }
            foreach (string n in r.Notes)
            {
                sb.AppendLine($"           . {n}");
            }
        }
        if (screen == null)
        {
            sb.AppendLine($"  SKIP  engine errors  no --log-file, native ERROR lines not screened"
                          + (logPath != null ? $" ({logPath})" : ""));
        }
        else
        {
            sb.AppendLine($"  {Tag(screen.Ok ? SuiteStatus.Pass : SuiteStatus.Fail)}  engine errors  "
                          + $"{screen.Total} line(s), {screen.Allowed} allowlisted, {screen.Unexpected.Count} unexpected");
            foreach (var allowance in ErrorAllowlist)
            {
                screen.AllowedCounts.TryGetValue(allowance.Pattern, out int n);
                sb.AppendLine($"           . allowed {n}/{allowance.Max}x  {allowance.Why}");
            }
            foreach (string u in screen.Unexpected)
            {
                sb.AppendLine($"          !! {u}");
            }
            foreach (string o in screen.OverCap)
            {
                sb.AppendLine($"          !! over cap: {o}");
            }
        }
        int pass = results.Count(r => r.Status == SuiteStatus.Pass);
        int fail = results.Count(r => r.Status == SuiteStatus.Fail);
        int skip = results.Count(r => r.Status == SuiteStatus.Skip);
        sb.AppendLine($"  {pass} passed, {fail} failed, {skip} skipped");
        sb.AppendLine("-------------------------------------------------------------------------");
        return sb.ToString();
    }

    private static string Tag(SuiteStatus s) => s switch
    {
        SuiteStatus.Pass => "PASS",
        SuiteStatus.Fail => "FAIL",
        _ => "SKIP",
    };

    private static void WriteReport(TestContext ctx, List<SuiteResult> results,
        StderrScreen? screen, string? logPath, PhaseTotals phases, string selector, ShardPlan plan)
    {
        var json = new StringBuilder();
        json.AppendLine("{");
        json.AppendLine($"  \"schema\": {ReportSchema},");
        json.AppendLine($"  \"binary\": {{\"path\": {Quote(BinaryPath(ctx))}, \"md5\": {Quote(BinaryMd5(ctx))}}},");
        json.AppendLine($"  \"selector\": {Quote(selector)},");
        json.AppendLine($"  \"shard\": {{\"index\": {plan.Index}, \"count\": {plan.Count}, "
                        + $"\"selectedTotal\": {plan.SelectedTotal}, \"weights\": {Quote(plan.WeightsSource)}, "
                        + $"\"unweighted\": [{string.Join(", ", plan.Unweighted.Select(Quote))}]}},");
        json.AppendLine($"  \"chapter\": {Quote(ctx.Chapter)},");
        json.AppendLine($"  \"mission\": {Quote(ctx.Mission)},");
        json.AppendLine($"  \"dataRoot\": {Quote(ctx.DataRoot)},");
        json.AppendLine($"  \"passed\": {results.Count(r => r.Status == SuiteStatus.Pass)},");
        json.AppendLine($"  \"failed\": {results.Count(r => r.Status == SuiteStatus.Fail)},");
        json.AppendLine($"  \"skipped\": {results.Count(r => r.Status == SuiteStatus.Skip)},");
        json.AppendLine("  \"phaseTotals\": {");
        json.AppendLine($"    \"wallSeconds\": {Sec(phases.WallSeconds)},");
        json.AppendLine($"    \"worldsBuilt\": {phases.WorldsBuilt},");
        json.AppendLine($"    \"buildSeconds\": {Sec(phases.BuildSeconds)},");
        json.AppendLine($"    \"archiveDecodeSeconds\": {Sec(phases.Build.ArchiveDecodeMs / 1000.0)},");
        json.AppendLine($"    \"soundPrepSeconds\": {Sec(phases.Build.SoundPrepMs / 1000.0)},");
        json.AppendLine($"    \"runtimeConstructionSeconds\": {Sec(phases.Build.RuntimeConstructionMs / 1000.0)},");
        json.AppendLine($"    \"otherBuildSeconds\": {Sec(phases.Build.OtherMs / 1000.0)},");
        json.AppendLine($"    \"disposalSeconds\": {Sec(phases.DisposalSeconds)},");
        json.AppendLine($"    \"finalDisposalSeconds\": {Sec(phases.FinalDisposalSeconds)},");
        json.AppendLine($"    \"restSeconds\": {Sec(phases.RestSeconds)},");
        json.AppendLine($"    \"overrunSeconds\": {Sec(phases.OverrunSeconds)},");
        json.AppendLine($"    \"decodeCacheHits\": {ctx.DecodeCounts.Hits},");
        json.AppendLine($"    \"decodeCacheMisses\": {ctx.DecodeCounts.Misses}");
        json.AppendLine("  },");
        json.AppendLine("  \"suites\": [");
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            json.AppendLine("    {");
            json.AppendLine($"      \"name\": {Quote(r.Name)},");
            json.AppendLine($"      \"index\": {r.Index},");
            json.AppendLine($"      \"status\": {Quote(r.Status.ToString().ToLowerInvariant())},");
            json.AppendLine($"      \"seconds\": {Sec(r.Seconds)},");
            json.AppendLine($"      \"worldsBuilt\": {r.WorldsBuilt},");
            json.AppendLine($"      \"buildSeconds\": {Sec(r.BuildSeconds)},");
            json.AppendLine($"      \"archiveDecodeSeconds\": {Sec(r.ArchiveDecodeSeconds)},");
            json.AppendLine($"      \"soundPrepSeconds\": {Sec(r.SoundPrepSeconds)},");
            json.AppendLine($"      \"runtimeConstructionSeconds\": {Sec(r.RuntimeConstructionSeconds)},");
            json.AppendLine($"      \"otherBuildSeconds\": {Sec(r.OtherBuildSeconds)},");
            json.AppendLine($"      \"disposalSeconds\": {Sec(r.DisposalSeconds)},");
            json.AppendLine($"      \"restSeconds\": {Sec(r.RestSeconds)},");
            json.AppendLine($"      \"overrunSeconds\": {Sec(r.OverrunSeconds)},");
            json.AppendLine($"      \"detail\": {Quote(r.Detail)},");
            json.AppendLine($"      \"counts\": {{{string.Join(", ", r.Counts.Select(kv => $"{Quote(kv.Key)}: {kv.Value}"))}}},");
            json.AppendLine($"      \"failures\": [{string.Join(", ", r.Failures.Select(Quote))}],");
            json.AppendLine($"      \"notes\": [{string.Join(", ", r.Notes.Select(Quote))}]");
            json.AppendLine(i == results.Count - 1 ? "    }" : "    },");
        }
        json.AppendLine("  ],");
        json.AppendLine("  \"engineErrors\": {");
        json.AppendLine($"    \"logFile\": {(logPath == null ? "null" : Quote(logPath))},");
        json.AppendLine($"    \"screened\": {(screen != null).ToString().ToLowerInvariant()},");
        json.AppendLine($"    \"total\": {screen?.Total ?? 0},");
        json.AppendLine($"    \"allowlisted\": {screen?.Allowed ?? 0},");
        json.AppendLine($"    \"unexpected\": [{string.Join(", ", (screen?.Unexpected ?? Array.Empty<string>()).Select(Quote))}],");
        json.AppendLine($"    \"overCap\": [{string.Join(", ", (screen?.OverCap ?? Array.Empty<string>()).Select(Quote))}],");
        json.AppendLine("    \"allowlist\": [");
        for (int i = 0; i < ErrorAllowlist.Count; i++)
        {
            var a = ErrorAllowlist[i];
            int seen = 0;
            screen?.AllowedCounts.TryGetValue(a.Pattern, out seen);
            json.AppendLine($"      {{\"pattern\": {Quote(a.Pattern)}, \"max\": {a.Max}, \"seen\": {seen}, \"why\": {Quote(a.Why)}}}"
                            + (i == ErrorAllowlist.Count - 1 ? "" : ","));
        }
        json.AppendLine("    ]");
        json.AppendLine("  }");
        json.AppendLine("}");

        // Absolute, and inside .scratch/: the one place a suite is allowed to write.
        Directory.CreateDirectory(ctx.ScratchDir);
        string path = Path.Combine(ctx.ScratchDir, "test-report.json");
        File.WriteAllText(path, json.ToString());
        Log.Info("test", $"report file={path}");
    }

    private static string Sec(double seconds) => seconds.ToString("0.000", CultureInfo.InvariantCulture);

    // The DLL this process actually loaded, matched by the SAME fixed path RunTests.ps1's perf
    // stage hashes as $PerfDll, not Assembly.GetExecutingAssembly().Location, which Godot's own
    // Mono host returns empty for (confirmed live: every report before this fix wrote "").
    private static string BinaryPath(TestContext ctx) =>
        Path.Combine(ctx.RepoRoot, "CSVM", ".godot", "mono", "temp", "bin", "Debug", "CSVM.dll");

    private static string BinaryMd5(TestContext ctx)
    {
        string path = BinaryPath(ctx);
        if (path.Length == 0 || !File.Exists(path))
        {
            return "";
        }
        try
        {
            using var stream = File.OpenRead(path);
            using var md5 = System.Security.Cryptography.MD5.Create();
            return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
        }
        catch (IOException)
        {
            // Godot still has the file mapped for execution; a locked-out hash is reported as
            // empty rather than failing the whole report over an identity nicety.
            return "";
        }
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>The run-wide sums <see cref="WriteReport"/> needs, computed once in <see cref="Run"/>
    /// rather than re-derived from <c>results</c> there (<see cref="SuiteResult"/> already holds the
    /// per-suite figures these are sums of).</summary>
    /// <summary>Which shard this process ran, of how many, out of how big a selection, written
    /// into the report so an aggregator can prove the shards cover the selection exactly once
    /// rather than assuming they did.</summary>
    private readonly record struct ShardPlan(int Index, int Count, int SelectedTotal,
        string WeightsSource, IReadOnlyList<string> Unweighted);

    private readonly record struct PhaseTotals(
        PhaseAttribution.Categorized Build, double BuildSeconds, double DisposalSeconds,
        double FinalDisposalSeconds, double RestSeconds, double OverrunSeconds, int WorldsBuilt,
        double WallSeconds);

    public sealed record Suite(string Name, string What, Action<TestContext> Body);
}

/// <summary>Marks a body as a registered in-engine suite and carries its two facts: the name every
/// selector, log line, shard plan and weights key spells it by, and the one-line description the
/// report table prints. Both are literals rather than doc comments because the build generates no
/// documentation file, so a comment is unreadable at run time.
/// ⚠ The name is not derived from the method name and must not be: 47 of them differ.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SuiteAttribute : Attribute
{
    public SuiteAttribute(string name, string what)
    {
        Name = name;
        What = what;
    }

    public string Name { get; }

    public string What { get; }
}

/// <summary>Thrown by <see cref="TestContext.RequireData"/> to end a suite as SKIP.</summary>
public sealed class SuiteSkippedException : Exception
{
    public SuiteSkippedException(string why) : base(why) { }
}

/// <summary>One built chapter world, held for the suites that need one. The archives are closed
/// as soon as the build is done (<see cref="WorldSession"/>'s disposal-lifetime contract), so what
/// survives here is the scene subtree and its bound runtime.</summary>
public sealed class TestWorld
{
    public required string Chapter { get; init; }
    public required bool Collision { get; init; }
    public required WorldSession Session { get; init; }
    public required Node3D Stage { get; init; }

    /// <summary>The chapter's parsed gamez, kept past the build so a suite can build real geometry
    /// of its own from it, the effect-template stage the world-effects runtime stages,
    /// which is meshes and cannot be faked with named empty nodes. Not disposable, and
    /// not the texture archive, which IS and is closed with the build.</summary>
    public required GameZ Gamez { get; init; }

    public AnimRuntime Runtime => Session.Runtime;

    internal void Destroy()
    {
        Session.Lights?.Dispose();
        // Free, not QueueFree: the harness runs to completion inside one _Ready call, so a queued
        // free would only happen after every suite had already built its own world.
        Stage.Free();
    }
}

/// <summary>
/// What a suite is handed: the resolved data paths, the assertion verbs, a scene-tree host, and
/// the chapter-world builder. Every assertion and note goes through the <c>test</c> category of
/// <see cref="Log"/>, so the full-detail file sink records the whole run whatever the console
/// filter is.
/// </summary>
public sealed class TestContext
{
    internal readonly List<string> Failures = new();
    internal readonly List<string> Notes = new();
    internal readonly Dictionary<string, long> Counts = new();

    // The current suite's world-build attribution: reset by ResetPhaseAttribution() at the top of
    // every suite in Run(), the same lifetime Failures/Notes/Counts already have.
    internal PhaseAttribution.Categorized WorldBuildPhases;
    internal double WorldBuildSeconds;
    internal double DisposalSeconds;
    internal int WorldsBuilt;

    // Set by TestHarness.Run once the shard term is parsed, before any suite starts. Null on a
    // serial run, which keeps writing straight into .scratch/ as it always has.
    internal string? ShardScratch;

    private readonly Dictionary<string, TestWorld> _worlds = new();

    // One run builds the same chapter and the same chapter+mission many times, so the two
    // expensive decodes are paid once each. Sound and pixel state are deliberately absent from it:
    // see DecodeCache for what it holds and the read-only contract that binds every user.
    private readonly DecodeCache _decode = new();

    public required string RepoRoot { get; init; }
    public required string DataRoot { get; init; }
    public required string Chapter { get; init; }
    public required string Mission { get; init; }
    public required string ZrdrPath { get; init; }
    public required string MessagesPath { get; init; }
    public required string PlanesGamezPath { get; init; }
    public required string InterpPath { get; init; }
    public required string SoundsPath { get; init; }
    public required string PlaneName { get; init; }
    public required bool Mute { get; init; }

    /// <summary><c>--loadout=&lt;def&gt;</c>: bind every plane to that def instead of its own. A
    /// cross-binding control, passing a def whose markers the airframe does not carry is how the
    /// <c>loadout-bind</c> suite is shown able to fail on real bad input rather than a planted
    /// assertion.</summary>
    public string? LoadoutOverride { get; init; }

    /// <summary>Installs a fake in place of the real <c>PufferEmitterFactory</c> for the next world
    /// this builds, null (the default) leaves <see cref="WorldSession.Options.EmitterFactory"/> null
    /// too, so a suite that never touches this gets the real adapter exactly as before. Mutable, not
    /// <c>init</c>: a suite sets it right before its own <see cref="WithWorld"/> call, on a chapter
    /// other than <see cref="Chapter"/> so the cached default-chapter world, built with whatever this
    /// property held first, is never silently reused in its place.</summary>
    public IEmitterFactory? EmitterFactory { get; set; }

    /// <summary>The ambience the next world this builds hands its real emitter factory, what a game
    /// session gives from its own instance (<see cref="WorldSession.Options.ArchiveEmitterFactory"/>).
    /// Null (the default) builds that factory over still air. A suite that never sets it gets no
    /// wind and no camera, the state every other emitter suite reads. It is mutable for the reason
    /// <see cref="EmitterFactory"/> is, and needs <see cref="WithPrivateWorld"/> for the same one.
    /// A cached world would hand this suite's ambience to every later suite on the chapter.</summary>
    public EffectAmbience? Ambience { get; set; }

    /// <summary>Extra sound-group names to prewarm for the next world this builds, a mission's own
    /// vocabulary the anim program never sees (<c>ObjectiveScript.SoundGroupNames()</c>). Mutable,
    /// the same reason <see cref="EmitterFactory"/> is: a suite sets it right before its own
    /// <see cref="WithWorld(string, bool, Action{TestWorld})"/> call.</summary>
    public IReadOnlyCollection<string>? ExtraPrewarmSoundNames { get; set; }

    /// <summary>Builds the next world the way a STORY mission session does: the cutscene roots
    /// stand up and the chapter's landings table resolves. Mutable for the reason
    /// <see cref="EmitterFactory"/> is, and off by default because those roots add nodes a
    /// per-chapter census counts.</summary>
    public bool CutsceneRoots { get; set; }

    /// <summary>Where a suite parents anything that must be in the scene tree, a built plane whose
    /// markers are read by global transform, a chapter world whose death sequences are ticked.</summary>
    public required Node3D Host { get; init; }

    /// <summary>The session camera. PLAYER_RANGE conditions and the sound listener measure from it,
    /// exactly as an interactive session's do.</summary>
    public required Camera3D Camera { get; init; }

    /// <summary>The scratch directory every artifact this run writes must stay inside. A sharded
    /// run gets a directory of its own beside its engine log, so neither a sibling shard nor a
    /// concurrent run can overwrite its report or its per-suite artifacts.</summary>
    public string ScratchDir => ShardScratch ?? Path.Combine(RepoRoot, ".scratch");

    /// <summary>How the decode store answered this run: reported so a warm-cache A/B shows the
    /// hits happened rather than only that the wall time moved.</summary>
    internal (int Hits, int Misses) DecodeCounts => (_decode.Hits, _decode.Misses);

    /// <summary>Reads every positional sound level and cull at the data's own <c>RANGE</c> radii
    /// until the returned handle is disposed, whatever factor the session carries. A suite that
    /// pins the decoded law's numbers has to say which factor it reads them at. The shipped
    /// <see cref="Mech3.SoundFalloff.ShippedRangeScale"/> is a departure from the radii the law was
    /// decoded against. The session's factor comes back on dispose.</summary>
    public static IDisposable AtAuthoredSoundRadii() => new SoundRangeScope(1f);

    /// <summary>The counterpart of <see cref="AtAuthoredSoundRadii"/>: reads every positional sound
    /// level and cull at the factor the build ships, whatever the caller's session carries. What a
    /// suite asking how far a cue reaches AT THE CONTROLS needs. That question is about the shipped
    /// reach, not about the decoded radii.</summary>
    public static IDisposable AtShippedSoundRadii() =>
        new SoundRangeScope(Mech3.SoundFalloff.ShippedRangeScale);

    /// <summary>Records a check. A false verdict fails the suite but does not stop it, the rest of
    /// the checks still run, so one report names every broken thing rather than the first.</summary>
    public void Check(bool ok, FormattableString what)
    {
        string text = Log.Format(what);
        if (ok)
        {
            Log.Debug("test", $"ok {text}");
            return;
        }
        Failures.Add(text);
        Log.Error("test", $"FAIL {text}");
    }

    /// <summary>A check on a count, phrased so the failure line carries both numbers.</summary>
    public void Same(long expected, long actual, FormattableString what)
    {
        Counts[Log.Format(what)] = actual;
        Check(expected == actual, $"{Log.Format(what)} expected={expected} actual={actual}");
    }

    /// <summary>Something worth having in the report that is not a verdict, a measured count, a
    /// caveat about what this run could not see.</summary>
    public void Note(FormattableString what)
    {
        string text = Log.Format(what);
        Notes.Add(text);
        Log.Info("test", $"note {text}");
    }

    /// <summary>Ends the suite as SKIP when an input the suite needs is not on disk. Skipped is
    /// reported distinctly from passed, so an empty data root cannot read as a green run.</summary>
    public void RequireData(string path, FormattableString what)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new SuiteSkippedException($"{Log.Format(what)} not found: {path}");
        }
    }

    /// <summary>Leaves a suite's full report in the scratch folder, so a failure is diagnosable
    /// without re-running the equivalent <c>--dump-*</c> tool by hand. Absolute path, inside
    /// <c>.scratch/</c>, where every suite artifact belongs. The one exception is a suite proving
    /// persistence ACROSS processes: <c>.scratch/</c> is swept, so <c>campaign-loop</c> keeps its
    /// own store under <c>user://Testing/</c>, never <c>user://Profiles</c>.</summary>
    public void WriteArtifact(string fileName, string text)
    {
        Directory.CreateDirectory(ScratchDir);
        string path = Path.Combine(ScratchDir, fileName);
        File.WriteAllText(path, text);
        Log.Info("test", $"artifact file={path}");
    }

    /// <summary>Brings the physics space in step with the scene, which a suite must do after it
    /// shows, moves or re-enables anything a later cast has to find. The whole run happens inside
    /// one <c>_Ready</c>, so nothing ever flushes what the server defers to its next step: a
    /// re-enabled <c>CollisionShape3D</c> is only QUEUED for its broadphase rebuild, and a
    /// transform written to a node waits for the next frame. Pushing every in-tree collider's pose
    /// through the server does both. ⚠ Not a kinematic body: that write is read as a move target.</summary>
    public void SyncPhysics()
    {
        foreach (var node in Host.FindChildren("*", "CollisionObject3D", recursive: true, owned: false))
        {
            if (node is not CollisionObject3D collider || !collider.IsInsideTree())
            {
                continue;
            }
            if (collider is Area3D)
            {
                PhysicsServer3D.AreaSetTransform(collider.GetRid(), collider.GlobalTransform);
            }
            else
            {
                PhysicsServer3D.BodySetState(collider.GetRid(),
                    PhysicsServer3D.BodyState.Transform, collider.GlobalTransform);
            }
        }
    }

    /// <summary>Builds (or reuses) a chapter world and runs <paramref name="body"/> against it. The
    /// run's own chapter is cached; any other chapter is freed once the body returns, so a
    /// per-chapter census does not hold eight worlds at once. The subtree is in the scene tree with
    /// <see cref="AnimRuntime.ManualAdvance"/> set, so an out-of-tree transform read returns identity
    /// and <c>_Process</c> does not also drive the runtime.</summary>
    public void WithWorld(string chapter, bool collision, Action<TestWorld> body) =>
        WithWorld(chapter, collision, mission: null, body);

    /// <summary>Builds a world nothing else will ever see and frees it when <paramref name="body"/>
    /// returns: never read from the shared cache, never written to it. What a suite whose build
    /// options differ from every other suite's needs, so its choices cannot ride into a later
    /// suite's world, an installed <see cref="EmitterFactory"/> above all.</summary>
    public void WithPrivateWorld(string chapter, bool collision, Action<TestWorld> body)
    {
        var world = BuildWorld(chapter, collision, Mission);
        try
        {
            body(world);
        }
        finally
        {
            DisposalSeconds += TimeDestroy(world);
        }
    }

    /// <summary>The mission-override form: builds the chapter at a mission other than the
    /// run's own (the zeppelin damage suite wants C1 at M04, where <c>piratezep</c> is live).
    /// An overridden-mission world is never cached, the cache is keyed by chapter alone, so
    /// storing it would hand the wrong mission to every later same-chapter suite.</summary>
    public void WithWorld(string chapter, bool collision, string? mission, Action<TestWorld> body)
    {
        bool defaultMission = mission == null || mission == Mission;
        if (defaultMission && _worlds.TryGetValue(chapter, out var cached))
        {
            if (!collision || cached.Collision)
            {
                body(cached);
                return;
            }
            _worlds.Remove(chapter);
            DisposalSeconds += TimeDestroy(cached);
        }
        // One physics space: a cached chapter's colliders would stand inside the new world's
        // airspace (C1's scenery met C2/M03's racers), so a collidable build evicts every cached
        // collidable world first; the suites that want one back rebuild it.
        if (collision)
        {
            EvictCollidableWorlds();
        }
        var world = BuildWorld(chapter, collision, mission ?? Mission);
        if (defaultMission && chapter == Chapter)
        {
            _worlds[chapter] = world;
            body(world);
            return;
        }
        try
        {
            body(world);
        }
        finally
        {
            DisposalSeconds += TimeDestroy(world);
        }
    }

    /// <summary>Clears everything a suite may leave behind on this context: the build knobs and the
    /// world-build/disposal attribution. Called once per suite in <see cref="TestHarness.Run"/>, the
    /// same lifetime <see cref="Failures"/>/<see cref="Notes"/>/<see cref="Counts"/> already have.
    /// ⚠ Keep the knob resets: they are what makes a suite's verdict independent of which suite ran
    /// before it, and therefore of which shard it landed in.</summary>
    internal void ResetForSuite()
    {
        // Suites run back to back inside one frame, so a predecessor's QueueFree'd rigs are still
        // under the host: a same-named spawn is renamed @Node3D@N and a lookup by name finds the
        // dead one. Free them now, the flush the frame end would have done between suites.
        foreach (var child in Host.GetChildren())
        {
            if (child.IsQueuedForDeletion())
                child.Free();
        }
        EmitterFactory = null;
        Ambience = null;
        ExtraPrewarmSoundNames = null;
        CutsceneRoots = false;
        WorldBuildPhases = default;
        WorldBuildSeconds = 0;
        DisposalSeconds = 0;
        WorldsBuilt = 0;
    }

    /// <summary>Destroys every cached collidable world. A suite that builds its own physics lab
    /// under the host without a world calls this first: the cache keeps the default chapter's
    /// collidable world standing across suites, and its sea collider sits at the origin, so a
    /// splash or a ray fired there reads the previous suite's scenery. The same rule a collidable
    /// build applies before it opens its own space.</summary>
    internal void EvictCollidableWorlds()
    {
        var evicted = new List<string>();
        foreach (var (key, w) in _worlds)
        {
            if (w.Collision)
                evicted.Add(key);
        }
        foreach (var key in evicted)
        {
            var w = _worlds[key];
            _worlds.Remove(key);
            DisposalSeconds += TimeDestroy(w);
        }
    }

    internal void ReleaseWorlds()
    {
        foreach (var w in _worlds.Values)
        {
            w.Destroy();
        }
        _worlds.Clear();
    }

    // Times one Destroy() call. Never attributed to a suite when it happens outside one (the
    // shared cache's own teardown after every suite has run, TestHarness.Run times that itself,
    // as the run's finalDisposalSeconds, since no single suite owns a world every suite shared).
    private static double TimeDestroy(TestWorld world)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        world.Destroy();
        return watch.Elapsed.TotalSeconds;
    }

    private TestWorld BuildWorld(string chapter, bool collision, string mission)
    {
        string gamezPath = SessionPaths.ChapterGamez(DataRoot, chapter);
        string texturesPath = SessionPaths.ChapterTextures(DataRoot, chapter);
        RequireData(gamezPath, $"chapter {chapter} gamez");
        RequireData(texturesPath, $"chapter {chapter} textures");

        var buildWatch = System.Diagnostics.Stopwatch.StartNew();
        // A private StartupProfile the shared build code's own Mark/Record calls still reach --
        // docs/org/startup-profile.md on why Current is otherwise left null here.
        var profile = new StartupProfile("test-suite", bootMs: 0);
        var previousProfile = StartupProfile.Current;
        StartupProfile.Current = profile;
        try
        {
            var stage = new Node3D { Name = $"TestWorld_{chapter}" };
            Host.AddChild(stage);
            // The archives are this scope's: WorldSession clears the puffer factory and the sound
            // loader after its bootstrap precisely so they can close here.
            var archives = SessionArchives.OpenFor(ArchiveIntent.Suite, gamezPath, texturesPath,
                SoundsPath, ZrdrPath, Mute, _decode);
            using var textures = archives.Textures;
            using var sounds = archives.Sounds;
            var ambience = Ambience;

            var session = WorldSession.Build(
                new WorldSession.Options
                {
                    DataRoot = DataRoot,
                    Chapter = chapter,
                    Mission = mission,
                    ZrdrPath = ZrdrPath,
                    InterpPath = InterpPath,
                    MissionZrdrPath = SessionPaths.MissionZrdr(DataRoot, chapter, mission),
                    ChapterZrdrPath = SessionPaths.ChapterZrdr(DataRoot, chapter),
                    EffectsParent = stage,
                    PlayerPosition = () => Camera.GlobalPosition,
                    Collision = collision,
                    RuntimeSeed = Rng.IntSeedFor(Rng.Anim),
                    EmitterFactory = EmitterFactory,
                    ArchiveEmitterFactory = (tex, parent) => new PufferEmitterFactory(tex, parent, ambience),
                    ExtraPrewarmNames = ExtraPrewarmSoundNames,
                    Cutscenes = CutsceneRoots ? CSVM.Session.Launch.GameSession.CutsceneWorldNames : null,
                    LandingTriggers = CutsceneRoots,
                    PlanesGamezPath = PlanesGamezPath,
                    Decode = _decode,
                    TexturesOutliveBuild = archives.TexturesOutliveBuild,
                    SoundsOutliveBuild = archives.SoundsOutliveBuild,
                },
                archives.Gamez, textures, sounds, archives.SoundDefs, archives.SoundGroups);
            stage.AddChild(session.Root);
            session.Runtime.ManualAdvance = true;

            return new TestWorld
            {
                Chapter = chapter,
                Collision = collision,
                Session = session,
                Stage = stage,
                Gamez = archives.Gamez,
            };
        }
        finally
        {
            // Restored even on a thrown build: a leaked Current would silently misattribute every
            // later suite's phases (or a real session's, if one ever ran after in the same
            // process) to this build's profile instead of its own.
            StartupProfile.Current = previousProfile;
            buildWatch.Stop();
            // The outer stopwatch, not profile's own clock, is what "other" closes against: it is
            // the one wall time TestHarness.Run also attributes to this suite.
            WorldBuildPhases += PhaseAttribution.Categorize(profile.Phases, buildWatch.Elapsed.TotalMilliseconds);
            WorldBuildSeconds += buildWatch.Elapsed.TotalSeconds;
            WorldsBuilt++;
        }
    }

    // Holds SoundFalloff at one factor for a suite's duration and puts the session's own back.
    // Disposing twice restores nothing a second time, so a nested scope cannot outlive its owner.
    private sealed class SoundRangeScope : IDisposable
    {
        private readonly float _previous = Mech3.SoundFalloff.RangeScale;
        private bool _restored;

        internal SoundRangeScope(float scale) => Mech3.SoundFalloff.SetRangeScale(scale);

        public void Dispose()
        {
            if (_restored)
            {
                return;
            }
            _restored = true;
            Mech3.SoundFalloff.SetRangeScale(_previous);
        }
    }
}

/// <summary>One suite's verdict, as it reaches the table and the JSON report.</summary>
public sealed class SuiteResult
{
    public required string Name { get; init; }

    /// <summary>This suite's position in the registry. Carried into the report so shard reports
    /// merge back into registry order without the merger holding a copy of the catalog.</summary>
    public int Index { get; init; }

    public required SuiteStatus Status { get; init; }
    public required double Seconds { get; init; }
    public string Detail { get; init; } = "";
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, long> Counts { get; init; } = new Dictionary<string, long>();

    /// <summary>How many <see cref="TestContext.WithWorld(string, bool, Action{TestWorld})"/> calls
    /// actually built (rather than reused) a world during this suite.</summary>
    public int WorldsBuilt { get; init; }

    /// <summary>Wall time this suite spent inside <c>BuildWorld</c>, summed over every world it
    /// built. <c>ArchiveDecodeSeconds + SoundPrepSeconds + RuntimeConstructionSeconds +
    /// OtherBuildSeconds == BuildSeconds</c> (the same identity <see cref="PhaseAttribution"/>
    /// states per build, summed).</summary>
    public double BuildSeconds { get; init; }
    public double ArchiveDecodeSeconds { get; init; }
    public double SoundPrepSeconds { get; init; }
    public double RuntimeConstructionSeconds { get; init; }
    public double OtherBuildSeconds { get; init; }

    /// <summary>Wall time this suite spent freeing a world it built and did not hand to the shared
    /// cache (<see cref="TestContext.WithWorld(string, bool, string?, Action{TestWorld})"/>'s
    /// non-cached path). The shared cache's own teardown happens once, after every suite, and is
    /// reported only in the run's totals (<c>finalDisposalSeconds</c>), never against one suite.</summary>
    public double DisposalSeconds { get; init; }

    /// <summary>What is left of <see cref="Seconds"/> once build and disposal are subtracted:
    /// manual simulation plus assertion work. See <see cref="PhaseAttribution.Rest"/>.</summary>
    public double RestSeconds { get; init; }

    /// <summary>How far <c>BuildSeconds + DisposalSeconds</c> overran <see cref="Seconds"/>, zero
    /// on a clean measurement. See <see cref="PhaseAttribution.Overrun"/>.</summary>
    public double OverrunSeconds { get; init; }
}

/// <summary>A native engine error the run is known to emit and that no suite here caused. Each
/// carries a cap, so the same message appearing MORE often than measured still fails, an
/// allowlist that swallowed an unbounded count would hide the next regression in the shape of an
/// old one.</summary>
public sealed record ErrorAllowance(string Pattern, int Max, string Why);

/// <summary>The result of screening a run's engine log for error lines.</summary>
public sealed class StderrScreen
{
    public int Total { get; init; }
    public int Allowed { get; init; }
    public IReadOnlyList<string> Unexpected { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> OverCap { get; init; } = Array.Empty<string>();

    /// <summary>How often each allowlisted pattern actually fired. Reported even when the screen
    /// passes: an allowlist entry whose count is invisible is exactly how a new error hides inside
    /// an old one's shape.</summary>
    public IReadOnlyDictionary<string, int> AllowedCounts { get; init; } = new Dictionary<string, int>();

    public bool Ok => Unexpected.Count == 0 && OverCap.Count == 0;
}

