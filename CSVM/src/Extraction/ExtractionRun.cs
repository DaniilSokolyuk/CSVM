using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using CSVM.Session.Launch;

namespace CSVM.Extraction;

/// <summary>Which half of an extraction a progress report comes from.</summary>
public enum ExtractionPhase
{
    /// <summary>The <c>ZBD</c> archives through unzbd, then <c>messages.json</c>.</summary>
    Zbd,

    /// <summary>The <c>.rof</c> archives, the cinemas and the string tables.</summary>
    Rof,

    /// <summary>The run has ended; <see cref="ExtractionProgress.Fraction"/> is 1.</summary>
    Done,
}

/// <summary>The whole extraction, the one entry point the Extract button and <c>--extract</c>
/// share. It checks the install and runs the ZBD half (<see cref="ZbdExtraction"/>). Then it runs
/// the <c>.rof</c> half (<see cref="RofExtraction"/>) over inputs <see cref="InstallLocator"/>
/// finds, and stamps that half. A ZBD failure stops the run before the
/// <c>.rof</c> half. It runs on the calling thread, which must not be the engine's main thread.
/// Engine-free; the order and the failure rules are in <c>docs/architecture/Extraction.md</c>.</summary>
public static class ExtractionRun
{
    /// <summary>The extraction root's folder name under the data root.</summary>
    public const string ExtractedFolder = "extracted";

    /// <summary>The <c>.rof</c> half's folder name under the extraction root.</summary>
    public const string RofFolder = "rof";

    // Where the ZBD half ends on the progress bar. It is most of the wall time, one unzbd process
    // per archive, against a .rof half that only inflates and copies.
    private const double ZbdShare = 0.85;

    /// <summary>The unzbd an extraction uses when none is named. An exported build carries it in
    /// <c>tools/</c> beside the executable in <paramref name="root"/>. A checkout uses the fork's
    /// release build under <paramref name="root"/>, the tool the extraction scripts defaulted to.</summary>
    public static string DefaultUnzbd(string root, bool exported) =>
        exported
            ? UnzbdTool.DefaultPath(root)
            : Path.Combine(root, "tools", "mech3ax", "target", "release", UnzbdTool.FileName(OperatingSystem.IsWindows()));

    /// <summary>The <c>.rof</c> half's inputs in <paramref name="installRoot"/>, each found without
    /// regard to case and null where the install lacks it, writing into
    /// <paramref name="rofOutput"/>.</summary>
    public static RofExtractionRequest RofRequest(string installRoot, string rofOutput, bool force) =>
        new(
            InstallLocator.ResolveFile(installRoot, "GOSDATA/ASSETS/crimson.rof"),
            InstallLocator.ResolveFile(installRoot, "GOSDATA/ASSETS/crimptch.rof"),
            InstallLocator.ResolveDirectory(installRoot, "GOSDATA/ASSETS/GRAPHICS/MPG"),
            InstallLocator.ResolveFile(installRoot, "GOSDATA/ASSETS/BINARIES/langui.dll"),
            InstallLocator.ResolveFile(installRoot, "GOSDATA/ASSETS/BINARIES/language.dll"),
            rofOutput,
            force);

    /// <summary>The lines that open a run's console output, naming what is read and written.</summary>
    public static IEnumerable<string> Header(ExtractionRequest request)
    {
        yield return "Extracting Crimson Skies data";
        yield return "  from: " + request.InstallFolder;
        yield return "  to:   " + request.ExtractedDir;
        yield return "  with: " + request.Unzbd;
        if (request.Force)
        {
            yield return "  (forced: every output is redone)";
        }

        if (request.Unzip)
        {
            yield return "  (will also unzip produced archives)";
        }

        yield return "(the install is only read, never modified)";
    }

    /// <summary>Runs the whole extraction. <paramref name="progress"/> is called on this thread.
    /// Cancelling throws <see cref="OperationCanceledException"/> at the next archive or logged
    /// step, and leaves the tree unstamped for the half it interrupted.</summary>
    public static ExtractionResult Run(ExtractionRequest request, Action<ExtractionProgress>? progress = null, CancellationToken cancel = default)
    {
        var check = InstallLocator.Check(request.InstallFolder);
        var result = new ExtractionResult { Install = check };
        if (!check.IsInstall)
        {
            result.Failures.Add(check.Message ?? "not a Crimson Skies install");
            Report(progress, ExtractionPhase.Done, 1.0, Array.Empty<string>(), null);
            return result;
        }

        string install = check.InstallRoot!;
        if (!RunZbd(install, request, result, progress, cancel))
        {
            Report(progress, ExtractionPhase.Done, 1.0, Array.Empty<string>(), null);
            return result;
        }

        cancel.ThrowIfCancellationRequested();
        RunRof(install, request, result, progress, cancel);
        Report(progress, ExtractionPhase.Done, 1.0, Array.Empty<string>(), null);
        return result;
    }

    /// <summary>Runs <paramref name="request"/> for a command line. The header, every progress line
    /// and the summary go to <paramref name="write"/>; a failed run also sends one line naming its
    /// first failure to <paramref name="fail"/>. Returns the process exit code, 0 on success and 1
    /// on any failure.</summary>
    public static int RunToConsole(ExtractionRequest request, Action<string> write, Action<string> fail, CancellationToken cancel = default)
    {
        foreach (string line in Header(request))
        {
            write(line);
        }

        var result = Run(request, report => report.Lines.ToList().ForEach(write), cancel);
        foreach (string line in result.Summary(request.Unzip))
        {
            write(line);
        }

        if (!result.Succeeded)
        {
            fail($"extraction failed: {result.Failures[0]}");
        }

        return result.Succeeded ? 0 : 1;
    }

    /// <summary>Where a ZBD report puts the whole run's progress bar: an archive counts as done once
    /// its step has ended.</summary>
    public static double ZbdFraction(ZbdProgress step)
    {
        int done = step.Step == ZbdStep.Extracting ? step.Index - 1 : step.Index;
        return step.Total <= 0 ? 0 : ZbdShare * Math.Clamp(done, 0, step.Total) / step.Total;
    }

    // The exceptions caught are what a disk, a permission or an unzbd that cannot start raise. A bad
    // copy then fails the run with a reason rather than taking the caller down.
    private static bool RunZbd(string install, ExtractionRequest request, ExtractionResult result, Action<ExtractionProgress>? progress, CancellationToken cancel)
    {
        Report(progress, ExtractionPhase.Zbd, 0, new[] { string.Empty, "ZBD archives:" }, null);
        ZbdExtractionResult zbd;
        try
        {
            zbd = ZbdExtraction.Run(
                install,
                request.ExtractedDir,
                request.Unzbd,
                new ZbdExtractionOptions(request.Force, request.Unzip),
                step => Report(progress, ExtractionPhase.Zbd, ZbdFraction(step), step.Lines().ToList(), step),
                cancel);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
        {
            result.Failures.Add("extracting the game archives stopped: " + e.Message);
            return false;
        }

        result.Zbd = zbd;
        if (zbd.Fatal != null)
        {
            result.Failures.Add(zbd.Fatal);
        }

        result.Failures.AddRange(zbd.Failures.Select(f => "unzbd failed on " + f));
        result.Warnings.AddRange(zbd.Unknowns.Select(u => "no unzbd mode for " + u + ", skipped"));
        if (!result.Succeeded)
        {
            result.Failures.Add("the menus and interface files were not extracted, since the game archives failed");
            return false;
        }

        return true;
    }

    // Only a missing crimson.rof fails this half: without it there is no menu and no paint. The
    // patch archive, the cinemas and the string tables are reported as warnings instead.
    private static void RunRof(string install, ExtractionRequest request, ExtractionResult result, Action<ExtractionProgress>? progress, CancellationToken cancel)
    {
        var rofRequest = RofRequest(install, request.RofDir, request.Force);
        Report(progress, ExtractionPhase.Rof, ZbdShare, new[] { string.Empty, "Menus and interface files:" }, null);
        if (rofRequest.BaseArchive == null)
        {
            result.Failures.Add($"no GOSDATA/ASSETS/crimson.rof in {install}; the install is incomplete");
            return;
        }

        // RofExtraction takes no token, so the log callback is where a cancel lands between steps.
        void Relay(string line)
        {
            cancel.ThrowIfCancellationRequested();
            Report(progress, ExtractionPhase.Rof, ZbdShare, new[] { "  " + line }, null);
        }

        RofExtractionResult rof;
        try
        {
            rof = RofExtraction.Run(rofRequest, Relay);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            result.Failures.Add("UI resource extraction stopped: " + e.Message);
            return;
        }

        result.Rof = rof;
        result.Warnings.AddRange(RofWarnings(rofRequest, rof));
        cancel.ThrowIfCancellationRequested();
        result.RofStamp = ExtractionStampWriter.WriteRof(request.ExtractedDir, rof.Movies.Present, DateTime.UtcNow);
    }

    private static IEnumerable<string> RofWarnings(RofExtractionRequest request, RofExtractionResult rof)
    {
        if (request.PatchArchive == null)
        {
            yield return "no GOSDATA/ASSETS/crimptch.rof, so the patch overlay is absent";
        }

        if (rof.Movies.Missing.Count > 0)
        {
            yield return Count(rof.Movies.Missing.Count) + " cinema(s) missing, which will not play: " + string.Join(", ", rof.Movies.Missing);
        }

        if (rof.StringRows == 0)
        {
            yield return "no UI string table was read, so the menus will show no text";
        }
    }

    private static void Report(Action<ExtractionProgress>? progress, ExtractionPhase phase, double fraction, IReadOnlyList<string> lines, ZbdProgress? zbd) =>
        progress?.Invoke(new ExtractionProgress(phase, fraction, lines, zbd));

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One whole extraction: the install to read, the data root to write under, and the
/// unzbd to run. With <see cref="Force"/> set it redoes every output however new. With
/// <see cref="Unzip"/> set it expands every zip into its sibling folder. The Extract button runs
/// both off.</summary>
public sealed record ExtractionRequest(string InstallFolder, string DataRoot, string Unzbd, bool Force = false, bool Unzip = false)
{
    /// <summary>Gets the extraction root, <c>&lt;data root&gt;/extracted</c>.</summary>
    public string ExtractedDir => Path.Combine(DataRoot, ExtractionRun.ExtractedFolder);

    /// <summary>Gets where the <c>.rof</c> half writes, <c>extracted/rof</c>.</summary>
    public string RofDir => Path.Combine(ExtractedDir, ExtractionRun.RofFolder);
}

/// <summary>One progress report. <see cref="Fraction"/> runs from 0 to 1 over the whole
/// extraction. <see cref="Lines"/> are its console lines (often none), and <see cref="Zbd"/> is the
/// ZBD half's own report.</summary>
public sealed record ExtractionProgress(ExtractionPhase Phase, double Fraction, IReadOnlyList<string> Lines, ZbdProgress? Zbd);

/// <summary>What one extraction did. <see cref="Failures"/> holds every reason the run is a failure,
/// and a run with none <see cref="Succeeded"/>. The two halves' own results are null for a half
/// that never ran.</summary>
public sealed class ExtractionResult
{
    /// <summary>Gets the verdict on the install folder, checked before anything runs.</summary>
    public required InstallCheck Install { get; init; }

    /// <summary>Gets the ZBD half's result, or null when the install check failed.</summary>
    public ZbdExtractionResult? Zbd { get; internal set; }

    /// <summary>Gets the <c>.rof</c> half's result, or null when it did not run to the end.</summary>
    public RofExtractionResult? Rof { get; internal set; }

    /// <summary>Gets the stamp the <c>.rof</c> half wrote, or null.</summary>
    public string? RofStamp { get; internal set; }

    /// <summary>Gets why the run failed, one entry per reason; empty on success.</summary>
    public List<string> Failures { get; } = new();

    /// <summary>Gets what the player should know about a run that still succeeded.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Gets whether the whole extraction ran and nothing failed.</summary>
    public bool Succeeded => Failures.Count == 0;

    /// <summary>The closing summary, one line per entry, in the shape the extraction scripts
    /// printed: each half's totals, then the warnings, then the verdict.</summary>
    public IEnumerable<string> Summary(bool unzip)
    {
        yield return string.Empty;
        // A half that could not start has no totals; its reason is among the failures below.
        if (Zbd is { Fatal: null })
        {
            yield return "ZBD archives:";
            foreach (string line in Zbd.Summary(unzip))
            {
                yield return line;
            }
        }

        if (Rof != null)
        {
            int files = Rof.Archives.Sum(a => a.Files);
            int images = Rof.Archives.Sum(a => a.DecodedTextures);
            int upToDate = Rof.Archives.Count(a => a.Outcome == RofArchiveOutcome.UpToDate);
            yield return "Menus and interface files:";
            yield return "  files extracted: " + Number(files);
            if (images > 0)
            {
                yield return "  .BM decoded:     " + Number(images) + " (each -> .png + _mask.png)";
            }

            yield return "  movies copied:   " + Number(Rof.Movies.Present) + " of " + Number(MovieCopy.Expected.Count) + " (verbatim, no conversion)";
            yield return "  string rows:     " + Number(Rof.StringRows);
            if (upToDate > 0)
            {
                yield return "  up to date:      " + Number(upToDate) + " archive(s); pass --extract-force to redo";
            }

            if (RofStamp != null)
            {
                yield return $"  stamped:         {RofStamp} (schema {Number(ExtractionStamp.Schema)})";
            }
        }

        foreach (string warning in Warnings)
        {
            yield return "WARNING: " + warning;
        }

        if (Succeeded)
        {
            yield return "Done.";
            yield break;
        }

        foreach (string failure in Failures)
        {
            yield return "FAILED: " + failure;
        }
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
