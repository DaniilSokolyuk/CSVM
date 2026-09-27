using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSVM.Extraction;
using CSVM.Session.Launch;

namespace CSVM.UI.Screens;

/// <summary>Why the launcher stops at the extraction screen instead of the menu.</summary>
public enum DataProblem
{
    /// <summary>The data root holds a usable extraction; the menu comes up.</summary>
    None,

    /// <summary>No extraction at all (<see cref="NoGameDataScreen.Missing"/>).</summary>
    Missing,

    /// <summary>A run from this screen started and never finished (<see cref="ExtractionFlow.UnfinishedMarker"/>).</summary>
    Incomplete,

    /// <summary>Stamped by an older extraction than this build reads.</summary>
    Older,

    /// <summary>Stamped by a newer build than this one.</summary>
    Newer,
}

/// <summary>Which of its views the extraction screen shows.</summary>
public enum ExtractionView
{
    /// <summary>The problem, the install folder and Extract.</summary>
    Asking,

    /// <summary>An extraction is running: phase, bar and the latest line.</summary>
    Running,

    /// <summary>The last run failed; its failures, with retry and another folder.</summary>
    Failed,

    /// <summary>The run succeeded and the install is remembered; the menu follows.</summary>
    Done,
}

/// <summary>
/// The extraction screen's state, engine-free so a unit drives it from pick to extract to done or
/// failure with a fake runner. The screen reads it and calls <see cref="Tick"/> once a frame.
/// The run itself happens on a worker (<see cref="ExtractionRun.Run"/> in production). Progress and
/// the outcome cross to the main thread only through <see cref="Tick"/>.
/// Rules for the pre-fill, the stale decision and a forced re-extraction are in
/// <c>docs/architecture/UI.md</c>.
/// </summary>
public sealed class ExtractionFlow
{
    /// <summary>The file a run leaves in <c>extracted</c> from its start until it succeeds. A
    /// cancelled, failed or killed run leaves a partial tree with no stamp, which would otherwise
    /// pass for a dev tree and reach the menu.</summary>
    public const string UnfinishedMarker = ".extraction-unfinished";

    // The folder A2's runner always expands for a player's tree, so it never marks a dev tree.
    private const string AlwaysUnpacked = "rimage";

    private readonly Runner _runner;
    private readonly Action<string> _remember;
    private readonly Action<Action> _start;
    private readonly object _gate = new();

    // Written by the worker under _gate, read by Tick.
    private ExtractionProgress? _progress;
    private string? _line;
    private Outcome? _outcome;

    private CancellationTokenSource? _cancel;

    /// <summary>Builds the flow for <paramref name="problem"/> over <paramref name="dataRoot"/>.
    /// The worker runs through <paramref name="start"/>, the thread pool by default. On success the
    /// install root goes to <paramref name="remember"/>.</summary>
    public ExtractionFlow(DataProblem problem, string dataRoot, string unzbd, string preFill, Runner runner, Action<string> remember, Action<Action>? start = null)
    {
        Problem = problem;
        DataRoot = dataRoot;
        Unzbd = unzbd;
        InstallPath = preFill;
        _runner = runner;
        _remember = remember;
        _start = start ?? (work => Task.Run(work));
    }

    /// <summary>The call that extracts: <see cref="ExtractionRun.Run"/> in production.</summary>
    public delegate ExtractionResult Runner(ExtractionRequest request, Action<ExtractionProgress> progress, CancellationToken cancel);

    /// <summary>Gets why the screen is up.</summary>
    public DataProblem Problem { get; }

    /// <summary>Gets the data root the extraction writes under.</summary>
    public string DataRoot { get; }

    /// <summary>Gets the unzbd the run uses.</summary>
    public string Unzbd { get; }

    /// <summary>Gets or sets the install folder the field holds, which Extract reads.</summary>
    public string InstallPath { get; set; }

    /// <summary>Gets the view to show.</summary>
    public ExtractionView View { get; private set; } = ExtractionView.Asking;

    /// <summary>Gets the line under the folder field: why a pick is not an install, or that a run
    /// was cancelled. Null when there is nothing to say.</summary>
    public string? Notice { get; private set; }

    /// <summary>Gets the latest run's phase.</summary>
    public ExtractionPhase Phase { get; private set; } = ExtractionPhase.Zbd;

    /// <summary>Gets the latest run's fraction, 0 to 1.</summary>
    public double Fraction { get; private set; }

    /// <summary>Gets the ZBD half's latest archive number and count, or null outside it.</summary>
    public (int Index, int Total)? Archive { get; private set; }

    /// <summary>Gets the latest non-blank console line of the run.</summary>
    public string LatestLine { get; private set; } = string.Empty;

    /// <summary>Gets the failed run's reasons, empty otherwise.</summary>
    public IReadOnlyList<string> Failures { get; private set; } = Array.Empty<string>();

    /// <summary>Gets the succeeded run's warnings, empty otherwise.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();

    /// <summary>Gets whether a cancel was asked for and the worker has not stopped yet.</summary>
    public bool Cancelling => View == ExtractionView.Running && _cancel is { IsCancellationRequested: true };

    /// <summary>Gets whether a run redoes every output. The incremental rule compares file times, which
    /// call a stale tree's outputs current, and an unfinished run's half-written archive too.</summary>
    public bool Force => Problem is DataProblem.Older or DataProblem.Newer or DataProblem.Incomplete;

    /// <summary>Whether <paramref name="dataRoot"/> stops the launch at this screen, and why. Only a
    /// stamp naming another schema counts as stale; an unstamped tree reaches the menu, with the
    /// boot's warning line (<see cref="ExtractionStamp.Check"/>).</summary>
    public static DataProblem ProblemAt(string dataRoot)
    {
        if (NoGameDataScreen.Missing(dataRoot))
        {
            return DataProblem.Missing;
        }

        if (File.Exists(Path.Combine(dataRoot, ExtractionRun.ExtractedFolder, UnfinishedMarker)))
        {
            return DataProblem.Incomplete;
        }

        return ExtractionStamp.Standing(dataRoot, out _) switch
        {
            StampStanding.Older => DataProblem.Older,
            StampStanding.Newer => DataProblem.Newer,
            _ => DataProblem.None,
        };
    }

    /// <summary>The folder the field starts with: the remembered install while it is still one, else
    /// the first found one. Failing both, the remembered path stands as a hint. The candidate
    /// list puts a valid remembered path first, so its head is the answer whenever there is one.</summary>
    public static string PreFill(string? remembered, IReadOnlyList<string> candidates) =>
        candidates.Count > 0 ? candidates[0] : remembered ?? string.Empty;

    /// <summary>Whether <paramref name="extractedDir"/> holds a zip unpacked beside itself, other than
    /// the folder every tree unpacks. The loaders prefer such a folder over its zip, so a
    /// re-extraction that rewrote only the zip would leave the old vintage in use.</summary>
    public static bool HasUnpackedSiblings(string extractedDir)
    {
        if (!Directory.Exists(extractedDir))
        {
            return false;
        }

        return Directory.EnumerateFiles(extractedDir, "*.zip")
            .Select(Path.GetFileNameWithoutExtension)
            .Any(name => !string.Equals(name, AlwaysUnpacked, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(Path.Combine(extractedDir, name!)));
    }

    /// <summary>A folder chosen in the picker. It goes into the field whatever it is; when it is not
    /// an install, <see cref="Notice"/> carries <see cref="InstallLocator.Check"/>'s message.</summary>
    public bool Pick(string folder)
    {
        if (View == ExtractionView.Running)
        {
            return false;
        }

        var check = InstallLocator.Check(folder);
        InstallPath = check.Folder;
        Notice = check.Message;
        View = ExtractionView.Asking;
        return check.IsInstall;
    }

    /// <summary>Starts a run over <see cref="InstallPath"/>. A folder that is not an install is
    /// refused here, with its message, before a worker starts. Returns whether a run started.</summary>
    public bool Extract()
    {
        if (View == ExtractionView.Running)
        {
            return false;
        }

        var check = InstallLocator.Check(InstallPath);
        if (!check.IsInstall)
        {
            Notice = check.Message;
            View = ExtractionView.Asking;
            return false;
        }

        string extracted = Path.Combine(DataRoot, ExtractionRun.ExtractedFolder);
        var request = new ExtractionRequest(check.Folder, DataRoot, Unzbd, Force, Force && HasUnpackedSiblings(extracted));
        Notice = null;
        Failures = Array.Empty<string>();
        Warnings = Array.Empty<string>();
        Phase = ExtractionPhase.Zbd;
        Fraction = 0;
        Archive = null;
        LatestLine = string.Empty;
        lock (_gate)
        {
            _progress = null;
            _line = null;
            _outcome = null;
        }

        MarkUnfinished(extracted);
        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        View = ExtractionView.Running;
        _start(() => Work(request, cancel.Token));
        return true;
    }

    /// <summary>Asks a running extraction to stop. The run stops at its next archive or step and
    /// <see cref="Tick"/> then returns the screen to <see cref="ExtractionView.Asking"/>.</summary>
    public void Cancel()
    {
        if (View == ExtractionView.Running)
        {
            _cancel?.Cancel();
        }
    }

    /// <summary>Main thread, once a frame: takes the worker's latest progress and, when the run has
    /// ended, its outcome. Success remembers the install and moves to <see cref="ExtractionView.Done"/>.</summary>
    public void Tick()
    {
        ExtractionProgress? progress;
        string? line;
        Outcome? outcome;
        lock (_gate)
        {
            progress = _progress;
            line = _line;
            outcome = _outcome;
            _outcome = null;
        }

        if (View != ExtractionView.Running)
        {
            return;
        }

        if (progress != null)
        {
            Phase = progress.Phase;
            Fraction = Math.Clamp(progress.Fraction, 0, 1);
            Archive = progress.Zbd is { } zbd ? (zbd.Index, zbd.Total) : Phase == ExtractionPhase.Zbd ? Archive : null;
        }

        if (line != null)
        {
            LatestLine = line;
        }

        if (outcome != null)
        {
            Finish(outcome);
        }
    }

    /// <summary>The phase as the progress view words it.</summary>
    public string PhaseText() => Phase switch
    {
        ExtractionPhase.Zbd when Archive is { } a && a.Total > 0 =>
            string.Create(CultureInfo.InvariantCulture, $"Extracting the game archives, {Math.Min(a.Index, a.Total)} of {a.Total}"),
        ExtractionPhase.Zbd => "Extracting the game archives",
        ExtractionPhase.Rof => "Extracting the menus, cinemas and text",
        _ => "Finishing",
    };

    // A data root that refuses the marker fails the run a moment later with the runner's own
    // message. This does not report it twice.
    private static void MarkUnfinished(string extracted)
    {
        try
        {
            Directory.CreateDirectory(extracted);
            File.WriteAllText(Path.Combine(extracted, UnfinishedMarker), string.Empty);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // A crash is reported as a failure rather than rethrown. The player's way out is the same
    // retry, and the log line beside it carries the exception.
    private void Finish(Outcome outcome)
    {
        _cancel?.Dispose();
        _cancel = null;
        if (outcome.Cancelled)
        {
            View = ExtractionView.Asking;
            Notice = "Extraction cancelled. The data folder may be incomplete; extract again before playing.";
            return;
        }

        if (outcome.Crash is { } crash)
        {
            Failures = new[] { "The extraction stopped unexpectedly: " + crash.Message };
            View = ExtractionView.Failed;
            return;
        }

        var result = outcome.Result!;
        if (!result.Succeeded)
        {
            Failures = result.Failures.ToList();
            View = ExtractionView.Failed;
            return;
        }

        Warnings = result.Warnings.ToList();
        Fraction = 1;
        try
        {
            File.Delete(Path.Combine(DataRoot, ExtractionRun.ExtractedFolder, UnfinishedMarker));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Warnings = Warnings.Append("the unfinished-extraction marker could not be removed, so the next start asks to extract again: " + e.Message).ToList();
        }
        _remember(result.Install.InstallRoot ?? result.Install.Folder);
        View = ExtractionView.Done;
    }

    private void Work(ExtractionRequest request, CancellationToken cancel)
    {
        Outcome outcome;
        try
        {
            outcome = new Outcome(_runner(request, Report, cancel), false, null);
        }
        catch (OperationCanceledException)
        {
            outcome = new Outcome(null, true, null);
        }
        catch (Exception e)
        {
            outcome = new Outcome(null, false, e);
        }

        lock (_gate)
        {
            _outcome = outcome;
        }
    }

    private void Report(ExtractionProgress progress)
    {
        string? line = progress.Lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
        lock (_gate)
        {
            _progress = progress;
            if (line != null)
            {
                _line = line;
            }
        }
    }

    private sealed record Outcome(ExtractionResult? Result, bool Cancelled, Exception? Crash);
}
