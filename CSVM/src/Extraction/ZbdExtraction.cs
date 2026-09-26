using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using CSVM.Session.Launch;

namespace CSVM.Extraction;

/// <summary>The ZBD half of extraction. Every archive under the install's <c>ZBD</c> folder runs
/// through unzbd into the same relative path under the extraction root, in
/// <see cref="ZbdTree"/>'s case. Then <c>strings.dll</c>
/// becomes <c>messages.json</c>, and the stamp is written. Archives run one at a time on the calling thread,
/// which must not be the engine's main thread. Engine-free; the rules are <see cref="ZbdPlan"/>.</summary>
public static class ZbdExtraction
{
    /// <summary>Extracts <paramref name="installRoot"/>'s archives into
    /// <paramref name="extractedDir"/> (the data root's <c>extracted</c> folder) with the unzbd at
    /// <paramref name="unzbd"/>. <paramref name="progress"/> is called on this thread. A failed
    /// archive is counted and the run continues; cancelling throws.</summary>
    public static ZbdExtractionResult Run(
        string installRoot,
        string extractedDir,
        string unzbd,
        ZbdExtractionOptions options,
        Action<ZbdProgress>? progress = null,
        CancellationToken cancel = default)
    {
        if (!File.Exists(unzbd))
        {
            return new ZbdExtractionResult { Fatal = $"unzbd not found at {unzbd}" };
        }

        string? zbdRoot = InstallLocator.ResolveDirectory(installRoot, "ZBD");
        if (zbdRoot == null)
        {
            return new ZbdExtractionResult { Fatal = $"no ZBD folder in {installRoot}; {InstallLocator.ExpectedFolder}" };
        }

        var archives = Archives(zbdRoot);
        var result = new ZbdExtractionResult { Archives = archives.Count };
        int total = archives.Count + 1;
        for (int i = 0; i < archives.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            Archive(zbdRoot, archives[i], extractedDir, unzbd, options, result, Report(progress, i + 1, total), cancel);
        }

        cancel.ThrowIfCancellationRequested();
        Messages(installRoot, extractedDir, unzbd, options, result, Report(progress, total, total), cancel);

        if (result.Failures.Count == 0)
        {
            result.Stamp = ExtractionStampWriter.WriteAssets(extractedDir, UnzbdTool.Identify(unzbd), DateTime.UtcNow);
        }

        return result;
    }

    // The extension compares without case, since Linux compares names by case and a copy may say
    // .ZBD. The sort keeps the progress order the same between runs.
    private static List<string> Archives(string zbdRoot) =>
        Directory.EnumerateFiles(zbdRoot, "*", SearchOption.AllDirectories)
            .Where(f => string.Equals(Path.GetExtension(f), ".zbd", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static Action<ZbdStep, string, string?, UnzbdNotes?, string?> Report(Action<ZbdProgress>? progress, int index, int total) =>
        (step, output, mode, notes, detail) => progress?.Invoke(new ZbdProgress(index, total, output, step, mode, notes, detail));

    private static void Archive(
        string zbdRoot,
        string zbd,
        string extractedDir,
        string unzbd,
        ZbdExtractionOptions options,
        ZbdExtractionResult result,
        Action<ZbdStep, string, string?, UnzbdNotes?, string?> report,
        CancellationToken cancel)
    {
        string rel = Path.GetRelativePath(zbdRoot, zbd);
        var mode = ZbdPlan.ModeFor(Path.GetFileNameWithoutExtension(zbd));
        if (mode == null)
        {
            result.Unknowns.Add(rel);
            result.Skipped++;
            report(ZbdStep.UnknownType, rel, null, null, null);
            return;
        }

        string outRel = ZbdPlan.OutputRelativePath(rel, mode);
        string output = Path.Combine(extractedDir, outRel);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        if (!options.Force && ZbdPlan.UpToDate(File.GetLastWriteTimeUtc(zbd), WriteTime(output, file: true)))
        {
            result.UpToDate++;
            report(ZbdStep.UpToDate, outRel, mode.Mode, null, null);
        }
        else
        {
            report(ZbdStep.Extracting, outRel, mode.Mode, null, null);
            var run = UnzbdTool.Run(unzbd, new[] { "cs", mode.Mode, zbd, output }, cancel);
            if (run.ExitCode != 0)
            {
                result.Failures.Add($"{rel} (exit {run.ExitCode.ToString(CultureInfo.InvariantCulture)})");
                report(ZbdStep.Failed, outRel, mode.Mode, null, Failure(run));
                return;
            }

            var notes = ZbdPlan.Classify(run.Stderr);
            result.TransformNotes += notes.TransformNotes;
            result.AnimNotes += notes.AnimNotes;
            result.Warnings.AddRange(notes.Unexpected.Select(line => $"{rel}: {line}"));
            result.Extracted++;
            report(ZbdStep.Extracted, outRel, mode.Mode, notes, null);
        }

        if ((options.Unzip || ZbdPlan.AlwaysUnzipped(outRel)) && mode.Extension == ".zip" && File.Exists(output))
        {
            Unzip(output, options.Force, result, () => report(ZbdStep.Unzipped, outRel, mode.Mode, null, null));
        }
    }

    // The folder is deleted before expanding, so an entry the new zip no longer holds does not
    // survive from an older extraction and mix vintages.
    private static void Unzip(string zip, bool force, ZbdExtractionResult result, Action done)
    {
        string folder = Path.Combine(Path.GetDirectoryName(zip)!, Path.GetFileNameWithoutExtension(zip));
        if (!ZbdPlan.UnzipNeeded(File.GetLastWriteTimeUtc(zip), WriteTime(folder, file: false), force))
        {
            return;
        }

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        Expand(zip, folder);
        result.Unzipped++;
        done();
    }

    // A sounds zip holds entries whose names differ only by case. A case-insensitive disk keeps
    // one file, and deleting before each write makes the later entry's spelling win, as Expand-Archive does.
    private static void Expand(string zip, string folder)
    {
        string root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.Ordinal))
            {
                throw new IOException($"zip entry escapes its folder: {entry.FullName} in {zip}");
            }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Delete(target);
            entry.ExtractToFile(target);
        }
    }

    // strings.dll sits at the install root beside ZBD, so the archive walk never meets it. Without
    // messages.json the HUD and briefings fall back to raw MSG_* keys.
    private static void Messages(
        string installRoot,
        string extractedDir,
        string unzbd,
        ZbdExtractionOptions options,
        ZbdExtractionResult result,
        Action<ZbdStep, string, string?, UnzbdNotes?, string?> report,
        CancellationToken cancel)
    {
        const string outRel = "messages.json";
        string? strings = InstallLocator.ResolveFile(installRoot, "strings.dll");
        if (strings == null)
        {
            report(ZbdStep.NoStrings, outRel, "messages", null, $"no strings.dll in {installRoot}");
            return;
        }

        string output = Path.Combine(extractedDir, outRel);
        if (!options.Force && ZbdPlan.UpToDate(File.GetLastWriteTimeUtc(strings), WriteTime(output, file: true)))
        {
            result.UpToDate++;
            report(ZbdStep.UpToDate, outRel, "messages", null, null);
            return;
        }

        Directory.CreateDirectory(extractedDir);
        report(ZbdStep.Extracting, outRel, "messages", null, null);
        var run = UnzbdTool.Run(unzbd, new[] { "cs", "messages", strings, output }, cancel);
        if (run.ExitCode != 0)
        {
            result.Failures.Add($"{Path.GetFileName(strings)} -> {outRel} (exit {run.ExitCode.ToString(CultureInfo.InvariantCulture)})");
            report(ZbdStep.Failed, outRel, "messages", null, Failure(run));
            return;
        }

        result.Extracted++;
        report(ZbdStep.Extracted, outRel, "messages", null, null);
    }

    private static DateTime? WriteTime(string path, bool file)
    {
        if (file)
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }

        return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : null;
    }

    private static string Failure(UnzbdRun run) =>
        string.Join("\n", new[] { $"FAILED (unzbd exit {run.ExitCode.ToString(CultureInfo.InvariantCulture)})" }
            .Concat(run.Stderr.Select(line => "  " + line))
            .Concat(run.Stdout.Select(line => "  " + line)));
}
