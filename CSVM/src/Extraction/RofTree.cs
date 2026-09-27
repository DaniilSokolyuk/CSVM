using System.IO;

namespace CSVM.Extraction;

/// <summary>
/// The one case every game-named path under <c>extracted/rof/</c> is written in, upper case, and
/// the mapping a reader puts a name from game data through. The archive stores its members upper
/// case. The layout, the scripts and the install spell the same files in any case. A
/// case-sensitive disk finds only the spelling written. <see cref="RofExtraction"/> writes
/// through <see cref="Member"/>, so a reader that resolves through it never scans a directory.
/// The files the extraction authors itself keep their own names: <c>menu_layout.json</c>,
/// <c>ui_strings.json</c> and the <c>_crimptch</c> folder. Layout: docs/formats/extraction.md.
/// </summary>
public static class RofTree
{
    /// <summary>The rof extraction under a data root, <c>extracted/rof</c>.</summary>
    public static string Root(string dataRoot) =>
        Path.Combine(dataRoot, ExtractionRun.ExtractedFolder, ExtractionRun.RofFolder);

    /// <summary>A game-named path relative to a rof root, <c>/</c>-separated, in the case the
    /// extraction writes it. ⚠ Do not map a name any other way; the writer and every reader must
    /// agree or a case-sensitive disk loses the file.</summary>
    public static string Canonical(string relative) =>
        relative.Replace('\\', '/').ToUpperInvariant();

    /// <summary>Where a game-named relative path sits under <paramref name="rofRoot"/> (the base
    /// tree or its <c>_crimptch</c> folder).</summary>
    public static string Member(string rofRoot, string relative) =>
        Path.Combine(rofRoot, Canonical(relative).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The same place under a data root's <c>extracted/rof</c>.</summary>
    public static string Under(string dataRoot, string relative) => Member(Root(dataRoot), relative);
}
