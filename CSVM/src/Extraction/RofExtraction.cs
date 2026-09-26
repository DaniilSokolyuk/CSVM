using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CSVM.Extraction;

/// <summary>How one archive's step ended.</summary>
public enum RofArchiveOutcome
{
    /// <summary>No archive at the given path.</summary>
    Absent,

    /// <summary>Its output was already at least as new as the archive.</summary>
    UpToDate,

    /// <summary>Unpacked this run.</summary>
    Extracted,
}

/// <summary>
/// The non-ZBD half of an extraction, into <c>extracted/rof/</c>. It unpacks both UI archives,
/// the patch archive into <c>_crimptch/</c>, and decodes each <c>.BM</c> to PNGs. It copies the
/// cinemas and writes <c>ui_strings.json</c> and <c>menu_layout.json</c>. It takes resolved paths
/// only; finding the install and writing the version stamp belong to the caller. Each step reports
/// to the log callback. Layout: docs/formats/extraction.md.
/// </summary>
public static class RofExtraction
{
    /// <summary>The folder under the output root the patch archive unpacks into.</summary>
    public const string PatchFolder = "_crimptch";

    /// <summary>Runs every step in order. Paths in <paramref name="request"/> that are null or do
    /// not exist are skipped and logged, as an incomplete install is reported rather than refused.
    /// </summary>
    public static RofExtractionResult Run(RofExtractionRequest request, Action<string>? log = null)
    {
        log ??= _ => { };
        string output = request.OutputRoot;
        Directory.CreateDirectory(output);

        var archives = new List<RofArchiveResult>
        {
            ExtractArchive(request.BaseArchive, "crimson.rof", output, request.Force, log),
            ExtractArchive(request.PatchArchive, "crimptch.rof", Path.Combine(output, PatchFolder), request.Force, log),
        };

        MovieCopyResult movies = CopyMovies(request.MovieFolder, output, log);

        // langui.dll first: the first row for an id wins everywhere downstream, the order the
        // original loads the two in.
        var rows = new List<UiStringRow>();
        var symbols = ReadSymbols(output);
        AddRows(request.LanguiDll, "langui.dll", symbols, rows, log);
        AddRows(request.LanguageDll, "language.dll", symbols, rows, log);
        if (rows.Count > 0)
        {
            File.WriteAllBytes(Path.Combine(output, "ui_strings.json"), UiStringTable.ToJson(rows));
            log("  ui_strings.json (" + rows.Count + " rows)");
        }

        MenuLayoutDocument? menu = DecodeMenuLayout(output, rows, log);
        return new RofExtractionResult(archives, movies, rows.Count, menu);
    }

    private static RofArchiveResult ExtractArchive(string? rofPath, string label, string destination, bool force, Action<string> log)
    {
        if (rofPath == null || !File.Exists(rofPath))
        {
            log("SKIP " + label + " (not present)");
            return new RofArchiveResult(label, RofArchiveOutcome.Absent, 0, 0, 0);
        }

        // The ASSETS folder is the marker a finished unpack leaves, so one at least as new as the
        // archive means this archive is already extracted.
        string marker = Path.Combine(destination, "ASSETS");
        if (!force && Directory.Exists(marker)
            && Directory.GetLastWriteTimeUtc(marker) >= File.GetLastWriteTimeUtc(rofPath))
        {
            log("ok   " + label + " (up to date)");
            return new RofArchiveResult(label, RofArchiveOutcome.UpToDate, 0, 0, 0);
        }

        log("->   " + label);
        Directory.CreateDirectory(destination);
        byte[] archive = File.ReadAllBytes(rofPath);
        int files = 0;
        int dirs = 0;
        int images = 0;
        foreach (var entry in RofArchive.Walk(archive))
        {
            string target = Path.Combine(destination, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                dirs++;
                continue;
            }

            byte[] data = RofArchive.ReadMember(archive, entry);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, data);
            files++;
            if (entry.Path.EndsWith(".BM", StringComparison.OrdinalIgnoreCase))
            {
                BmTexture? texture = BmTexture.TryDecode(data);
                if (texture != null)
                {
                    texture.WritePngsBeside(target);
                    images++;
                }
            }
        }

        log("     " + files + " files, " + dirs + " dirs, " + images + " .BM decoded");
        return new RofArchiveResult(label, RofArchiveOutcome.Extracted, files, dirs, images);
    }

    private static MovieCopyResult CopyMovies(string? movieFolder, string output, Action<string> log)
    {
        string destination = Path.Combine(output, "ASSETS", "GRAPHICS", "MPG");
        MovieCopyResult movies = MovieCopy.Run(movieFolder, destination);
        if (!movies.SourceFound)
        {
            log("SKIP GRAPHICS/MPG (not present at " + (movieFolder ?? "<none>") + ")");
            log("     nothing will play behind the front end or before a chapter");
            return movies;
        }

        log("->   GRAPHICS/MPG (" + movies.Copied + " copied, " + movies.AlreadyCurrent + " already current)");
        if (movies.Copied > 0)
        {
            log(string.Format(CultureInfo.InvariantCulture, "     {0:N0} MB copied verbatim", movies.BytesCopied / (1024.0 * 1024.0)));
        }

        if (movies.Missing.Count > 0)
        {
            log("     MISSING " + movies.Missing.Count + " of " + MovieCopy.Expected.Count + " movies: "
                + string.Join(", ", movies.Missing));
            log("     those will not play; check the install is complete");
        }

        return movies;
    }

    // RESOURCE.H ships inside the base archive, so this reads the tree just unpacked.
    private static Dictionary<int, string> ReadSymbols(string output)
    {
        string header = Path.Combine(output, "ASSETS", "SCRIPTS", "RESOURCE.H");
        return File.Exists(header)
            ? UiStringTable.ParseSymbols(File.ReadAllText(header, Encoding.Latin1))
            : new Dictionary<int, string>();
    }

    private static void AddRows(string? dllPath, string label, Dictionary<int, string> symbols, List<UiStringRow> rows, Action<string> log)
    {
        if (dllPath == null || !File.Exists(dllPath))
        {
            log("SKIP " + label + " (not present)");
            return;
        }

        var table = PeStringTable.Read(File.ReadAllBytes(dllPath));
        log("->   " + label + " (" + table.Count + " strings)");
        rows.AddRange(UiStringTable.Rows(Path.GetFileNameWithoutExtension(label), table, symbols));
    }

    // After the string table, so every IDS_ symbol a widget names can be joined to its text.
    private static MenuLayoutDocument? DecodeMenuLayout(string output, List<UiStringRow> rows, Action<string> log)
    {
        if (!File.Exists(Path.Combine(output, "ASSETS", "LAYOUT.CSV")))
        {
            log("SKIP menu_layout.json (ASSETS/LAYOUT.CSV not extracted)");
            return null;
        }

        MenuLayoutDocument menu = MenuLayoutDecoder.Run(output, UiStringTable.TextById(rows));
        var census = new Dictionary<string, int>();
        foreach (var count in menu.Counts)
        {
            census[count.Name] = count.Value;
        }

        int Count(string name) => census.TryGetValue(name, out int value) ? value : 0;
        log("->   menu_layout.json (schema " + menu.Schema + ")");
        log("     " + Count("screens") + " screens, " + Count("widgets") + " widgets, " + Count("macros")
            + " macros, " + Count("navigationEdges") + " nav edges");
        log("     " + Count("artReferences") + " art refs (" + Count("artMissing") + " absent), "
            + Count("stringSymbols") + " string symbols (" + Count("stringSymbolsResolved") + " resolved), "
            + Count("scrapbookEntries") + " scrapbook rows");
        if (Count("macrosUnresolved") > 0 || Count("warnings") > 0)
        {
            log("     " + Count("macrosUnresolved") + " unresolved macros, " + Count("warnings")
                + " warnings (both listed in the file)");
        }

        return menu;
    }
}

/// <summary>What <see cref="RofExtraction.Run"/> reads and where it writes. Each input is an
/// absolute path already resolved against the install, or null when the install lacks it.
/// <see cref="Force"/> re-unpacks archives whose output is already newer.</summary>
public sealed record RofExtractionRequest(
    string? BaseArchive,
    string? PatchArchive,
    string? MovieFolder,
    string? LanguiDll,
    string? LanguageDll,
    string OutputRoot,
    bool Force = false);

/// <summary>What <see cref="RofExtraction.Run"/> did. <see cref="MenuLayout"/> is null when no
/// <c>LAYOUT.CSV</c> was there to decode.</summary>
public sealed record RofExtractionResult(
    IReadOnlyList<RofArchiveResult> Archives,
    MovieCopyResult Movies,
    int StringRows,
    MenuLayoutDocument? MenuLayout);

/// <summary>One archive's step: the counts are zero unless it was unpacked this run.</summary>
public sealed record RofArchiveResult(string Name, RofArchiveOutcome Outcome, int Files, int Directories, int DecodedTextures);
