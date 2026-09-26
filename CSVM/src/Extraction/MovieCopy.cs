using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CSVM.Extraction;

/// <summary>
/// Copies the install's <c>.mpg</c> cinemas into the extraction byte for byte. They are not
/// archive members: <c>crimson.rof</c> carries <c>ASSETS/GRAPHICS/MPG</c> as an empty directory
/// and the files sit loose in the install, see docs/formats/cinemas.md. Names are kept as the
/// install spells them. The runtime resolves the data's differing spellings case-insensitively,
/// so a rename here would only move the mismatch.
/// </summary>
public static class MovieCopy
{
    /// <summary>The ten movies a complete install has, lower-case. Only the report uses it: the
    /// copy takes whatever <c>.mpg</c> files the folder holds.</summary>
    public static readonly IReadOnlyList<string> Expected = new[]
    {
        "chap0.mpg", "chap1.mpg", "chap2.mpg", "chap3.mpg", "chap4.mpg", "chap5.mpg",
        "crimflag.mpg", "final.mpg", "msopen1.mpg", "zipper.mpg",
    };

    /// <summary>Copies every <c>.mpg</c> in <paramref name="sourceFolder"/> into
    /// <paramref name="destFolder"/>, skipping a target that already has the source's length. A
    /// null or absent source copies nothing and reports every movie missing.</summary>
    public static MovieCopyResult Run(string? sourceFolder, string destFolder)
    {
        if (sourceFolder == null || !Directory.Exists(sourceFolder))
        {
            return new MovieCopyResult(false, 0, 0, 0, Expected.ToArray());
        }

        Directory.CreateDirectory(destFolder);
        int copied = 0;
        int current = 0;
        long bytes = 0;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = Directory.EnumerateFiles(sourceFolder)
            .Where(f => string.Equals(Path.GetExtension(f), ".mpg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (string source in sources)
        {
            string name = Path.GetFileName(source);
            found.Add(name);
            string target = Path.Combine(destFolder, name);
            long length = new FileInfo(source).Length;

            // A verbatim copy already at the source's length is the copy this step would make
            // again. Skipping it spares rewriting about 106 MB.
            if (File.Exists(target) && new FileInfo(target).Length == length)
            {
                current++;
                continue;
            }

            ReplaceWithCopy(source, target);
            copied++;
            bytes += length;
        }

        string[] missing = Expected.Where(m => !found.Contains(m)).ToArray();
        return new MovieCopyResult(true, copied, current, bytes, missing);
    }

    // A half-written or read-only leftover is replaced rather than failing the step.
    private static void ReplaceWithCopy(string source, string target)
    {
        if (File.Exists(target))
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }

        File.Copy(source, target, overwrite: true);
    }
}

/// <summary>What <see cref="MovieCopy.Run"/> did. <see cref="SourceFound"/> is false when there was
/// no folder to copy from; <see cref="Missing"/> names each expected movie the folder lacked.
/// </summary>
public sealed record MovieCopyResult(bool SourceFound, int Copied, int AlreadyCurrent, long BytesCopied, IReadOnlyList<string> Missing)
{
    /// <summary>Movies the extraction now holds, the count the version stamp records.</summary>
    public int Present => Copied + AlreadyCurrent;
}
