using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using CSVM.Session.Launch;

namespace CSVM.Extraction;

/// <summary>What happened to one step of a ZBD extraction.</summary>
public enum ZbdStep
{
    /// <summary>unzbd is about to run on the step's archive.</summary>
    Extracting,

    /// <summary>unzbd exited 0; <see cref="ZbdProgress.Notes"/> holds what its stderr said.</summary>
    Extracted,

    /// <summary>The output is at least as new as its source and was left alone.</summary>
    UpToDate,

    /// <summary>No mode is mapped to the archive's name, so it was skipped.</summary>
    UnknownType,

    /// <summary>unzbd exited non-zero; <see cref="ZbdProgress.Detail"/> holds its output.</summary>
    Failed,

    /// <summary>The zip was expanded into its sibling folder.</summary>
    Unzipped,

    /// <summary>The install holds no <c>strings.dll</c>, so <c>messages.json</c> was not written.</summary>
    NoStrings,
}

/// <summary>How a ZBD extraction runs. The player's Extract button uses the defaults.
/// With <see cref="Force"/>, every output is redone however new. With <see cref="Unzip"/>, each
/// zip is also expanded into a sibling folder named after it, the shape the loaders prefer.</summary>
public sealed record ZbdExtractionOptions(bool Force = false, bool Unzip = false);

/// <summary>One progress report. <see cref="Index"/> counts from 1 to <see cref="Total"/>, one per
/// archive plus the messages step, and <see cref="Output"/> is relative to the extraction root.</summary>
public sealed record ZbdProgress(int Index, int Total, string Output, ZbdStep Step, string? Mode, UnzbdNotes? Notes, string? Detail)
{
    /// <summary>The console lines the extraction script printed for this report, none for a
    /// clean extraction.</summary>
    public IEnumerable<string> Lines()
    {
        switch (Step)
        {
            case ZbdStep.Extracting:
                yield return $"  ->   {Output}  [{Mode}]";
                break;
            case ZbdStep.Extracted when Notes != null:
                foreach (string line in Notes.Unexpected)
                {
                    yield return "       " + line;
                }

                if (Notes.TransformNotes > 0)
                {
                    yield return $"       ({Notes.TransformNotes.ToString(CultureInfo.InvariantCulture)} transform-precision notes)";
                }

                if (Notes.AnimNotes > 0)
                {
                    yield return $"       ({Notes.AnimNotes.ToString(CultureInfo.InvariantCulture)} anim-validation notes)";
                }

                break;
            case ZbdStep.UpToDate:
                yield return $"  ok   {Output} (up to date)";
                break;
            case ZbdStep.UnknownType:
                yield return $"  SKIP (unknown type) {Output}";
                break;
            case ZbdStep.Failed:
                foreach (string line in (Detail ?? string.Empty).Split('\n'))
                {
                    yield return "       " + line;
                }

                break;
            case ZbdStep.NoStrings:
                yield return $"  SKIP {Output} ({Detail})";
                break;
        }
    }
}

/// <summary>The totals of one ZBD extraction. <see cref="Fatal"/> is set when nothing could run,
/// with no unzbd or no <c>ZBD</c> folder. Every count is then zero. <see cref="Stamp"/> is the
/// stamp path, written only by a run without failures.</summary>
public sealed class ZbdExtractionResult
{
    /// <summary>Gets why the run could not start, or null.</summary>
    public string? Fatal { get; init; }

    /// <summary>Gets the number of archives found under <c>ZBD</c>.</summary>
    public int Archives { get; init; }

    /// <summary>Gets the outputs unzbd wrote, the messages step included.</summary>
    public int Extracted { get; internal set; }

    /// <summary>Gets the outputs left alone because they were up to date.</summary>
    public int UpToDate { get; internal set; }

    /// <summary>Gets the archives of an unknown type, skipped.</summary>
    public int Skipped { get; internal set; }

    /// <summary>Gets the zips expanded into sibling folders.</summary>
    public int Unzipped { get; internal set; }

    /// <summary>Gets the transform-precision notes summed over the run.</summary>
    public int TransformNotes { get; internal set; }

    /// <summary>Gets the anim-validation notes summed over the run.</summary>
    public int AnimNotes { get; internal set; }

    /// <summary>Gets each failed step as <c>path (exit N)</c>.</summary>
    public List<string> Failures { get; } = new();

    /// <summary>Gets the archives no mode is mapped to, relative to <c>ZBD</c>.</summary>
    public List<string> Unknowns { get; } = new();

    /// <summary>Gets the stderr lines of archives that extracted which are not a known note.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Gets the stamp written by a run without failures, or null.</summary>
    public string? Stamp { get; internal set; }

    /// <summary>Gets whether the run started and nothing failed.</summary>
    public bool Succeeded => Fatal == null && Failures.Count == 0;

    /// <summary>The closing summary the extraction script printed, one line per entry.</summary>
    public IEnumerable<string> Summary(bool unzip)
    {
        if (Fatal != null)
        {
            yield return "  FAILED: " + Fatal;
            yield break;
        }

        yield return "  extracted:  " + Number(Extracted);
        yield return "  up to date: " + Number(UpToDate);
        yield return "  skipped:    " + Number(Skipped);
        if (unzip)
        {
            yield return "  unzipped:   " + Number(Unzipped);
        }

        if (TransformNotes > 0)
        {
            yield return $"  transform-precision notes: {Number(TransformNotes)} (informational)";
        }

        if (AnimNotes > 0)
        {
            yield return $"  anim-validation notes: {Number(AnimNotes)} (informational)";
        }

        foreach (string unknown in Unknowns)
        {
            yield return "  UNKNOWN type (no mode mapped): " + unknown;
        }

        if (Stamp != null)
        {
            yield return $"  stamped:    {Stamp} (schema {Number(ExtractionStamp.Schema)})";
        }

        foreach (string failure in Failures)
        {
            yield return "  FAILED: " + failure;
        }
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

