using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CSVM.Extraction;

/// <summary>What <see cref="InstallLocator.Check"/> found in a folder the player picked.</summary>
public enum InstallCheckKind
{
    /// <summary>The folder is an install: it holds <c>ZBD</c> with archives in it and
    /// <c>GOSDATA/ASSETS</c>.</summary>
    Install,

    /// <summary>No folder exists at that path.</summary>
    Missing,

    /// <summary>The folder sits inside an install, such as its <c>ZBD</c> or <c>GOSDATA</c>.</summary>
    InsideInstall,

    /// <summary>The folder holds an install one or two levels down, such as <c>Microsoft Games</c>.</summary>
    HoldsInstall,

    /// <summary>The folder has no <c>ZBD</c> folder and no install above or below it.</summary>
    NoZbd,

    /// <summary>The folder has <c>ZBD</c> but no <c>GOSDATA/ASSETS</c>.</summary>
    NoAssets,

    /// <summary>The layout is right but <c>ZBD</c> holds no <c>.zbd</c> archive, an interrupted
    /// install or an image nothing was copied out of.</summary>
    EmptyZbd,
}

/// <summary>The answer for one picked folder. <see cref="InstallRoot"/> is the install itself on
/// <see cref="InstallCheckKind.Install"/>. On the two mis-pick kinds it is the install found above
/// or below the pick, so a picker can offer it. The text to show the player is
/// <see cref="Message"/>, null on an install.</summary>
public sealed record InstallCheck(InstallCheckKind Kind, string Folder, string? InstallRoot, string? Message)
{
    /// <summary>Whether extraction can run from <see cref="Folder"/>.</summary>
    public bool IsInstall => Kind == InstallCheckKind.Install;
}

/// <summary>Where <see cref="InstallLocator.Candidates"/> looks. These are values rather than
/// reads of the machine, so a test hands in a fake home folder and drive list. The production set
/// is <see cref="ForThisMachine"/>. The Program Files folders and drives are read only when
/// <see cref="Windows"/> is set, and <see cref="Home"/> only when it is not.</summary>
public sealed record InstallSearchRoots(
    bool Windows,
    string? Home,
    IReadOnlyList<string> ProgramFiles,
    IReadOnlyList<string> FixedDrives)
{
    /// <summary>This process's platform, home folder, Program Files folders and ready fixed
    /// drives.</summary>
    public static InstallSearchRoots ForThisMachine()
    {
        bool windows = OperatingSystem.IsWindows();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programFiles = new List<string>();
        var drives = new List<string>();
        if (windows)
        {
            foreach (string name in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
            {
                string? value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value))
                {
                    programFiles.Add(value);
                }
            }

            drives.AddRange(ReadyFixedDrives());
        }

        return new InstallSearchRoots(windows, home.Length == 0 ? null : home, programFiles, drives);
    }

    // A drive that throws while being asked is left out rather than failing the search. The list
    // only pre-fills a picker, which the player can always overrule.
    private static IEnumerable<string> ReadyFixedDrives()
    {
        DriveInfo[] all;
        try
        {
            all = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var drive in all)
        {
            string? root = null;
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    root = drive.RootDirectory.FullName;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (root != null)
            {
                yield return root;
            }
        }
    }
}

/// <summary>Finds a Crimson Skies install and the files in it, whatever the case of their names.
/// Every install-side lookup goes through <see cref="ResolveDirectory"/> or
/// <see cref="ResolveFile"/>, because the install is spelled differently by different copies and
/// Linux compares names by case. A lookup never renames anything: the path it returns is spelled
/// the way the disk spells it. Engine-free, so <c>CSVM.Tests</c> runs it over temp folders. Rules
/// and candidate order: <c>docs/architecture/Extraction.md</c>.</summary>
public static class InstallLocator
{
    /// <summary>The sentence every message ends on, naming the folder the player should pick.</summary>
    public const string ExpectedFolder = "the folder that holds the ZBD and GOSDATA folders side by side";

    // The install's own folder below each place a copy is normally found.
    private const string InstallFolder = "Microsoft Games/Crimson Skies";

    // How far below a picked folder an install is looked for: Program Files holds Microsoft Games,
    // which holds the install. Deeper would walk a whole drive for a picker's hint.
    private const int HoldsInstallDepth = 2;

    // How many nested link targets Canonical follows, the kernel's own symlink limit. A link
    // pointing below itself stops there rather than recursing forever.
    private const int MaxLinkDepth = 40;

    /// <summary>The directory <paramref name="relativePath"/> names under <paramref name="root"/>,
    /// matching each segment without regard to case, or null when any segment is absent. An exact
    /// match wins over a case-folded one where a case-sensitive folder holds both. Segments split
    /// on either slash; an empty path answers the root.</summary>
    public static string? ResolveDirectory(string root, string relativePath) => Resolve(root, relativePath, wantFile: false);

    /// <summary>The file <paramref name="relativePath"/> names under <paramref name="root"/>, by
    /// the same segment walk as <see cref="ResolveDirectory"/>, or null when it is absent.</summary>
    public static string? ResolveFile(string root, string relativePath) => Resolve(root, relativePath, wantFile: true);

    /// <summary>Whether <paramref name="folder"/> has the install layout: <c>ZBD</c> and
    /// <c>GOSDATA/ASSETS</c>, compared without regard to case. The silent rule the candidate
    /// search filters by; a folder the player picked gets <see cref="Check"/> instead.</summary>
    public static bool IsInstall(string folder) =>
        ResolveDirectory(folder, "ZBD") != null && ResolveDirectory(folder, "GOSDATA/ASSETS") != null;

    /// <summary>What <paramref name="picked"/> is, with the message to show when it is not an
    /// install. Stray quotes and trailing separators are dropped first, the accident a pasted
    /// Windows path carries. A pick inside or around an install names that install.</summary>
    public static InstallCheck Check(string picked)
    {
        string folder = Normalize(picked);
        if (folder.Length == 0)
        {
            return new InstallCheck(InstallCheckKind.Missing, folder, null,
                $"No folder was chosen. Choose your Crimson Skies install folder, {ExpectedFolder}.");
        }

        if (!Directory.Exists(folder))
        {
            return new InstallCheck(InstallCheckKind.Missing, folder, null,
                $"The folder '{folder}' does not exist. Choose your Crimson Skies install folder, {ExpectedFolder}.");
        }

        string? zbd = ResolveDirectory(folder, "ZBD");
        if (zbd != null && ResolveDirectory(folder, "GOSDATA/ASSETS") != null)
        {
            return HasArchives(zbd)
                ? new InstallCheck(InstallCheckKind.Install, folder, folder, null)
                : new InstallCheck(InstallCheckKind.EmptyZbd, folder, null,
                    $"The folder '{zbd}' holds no .zbd archives, so there is nothing to extract. '{folder}' looks like an "
                    + $"incomplete Crimson Skies install. Reinstall the game, or choose a complete install: {ExpectedFolder}.");
        }

        if (InstallAbove(folder) is { } above)
        {
            return new InstallCheck(InstallCheckKind.InsideInstall, folder, above,
                $"'{folder}' is inside your Crimson Skies install. Choose '{above}' instead, {ExpectedFolder}.");
        }

        if (InstallBelow(folder, HoldsInstallDepth) is { } below)
        {
            return new InstallCheck(InstallCheckKind.HoldsInstall, folder, below,
                $"'{folder}' holds your Crimson Skies install rather than being it. Choose '{below}' instead, {ExpectedFolder}.");
        }

        return zbd == null
            ? new InstallCheck(InstallCheckKind.NoZbd, folder, null,
                $"Expected a ZBD folder in '{folder}', but it is not there, so it does not look like a Crimson Skies install. "
                + $"Open your install until you see the ZBD and GOSDATA folders side by side, and choose {ExpectedFolder}.")
            : new InstallCheck(InstallCheckKind.NoAssets, folder, null,
                $"Expected the game's UI assets in GOSDATA/ASSETS under '{folder}', but that folder is not there, so it does not "
                + $"look like a complete Crimson Skies install. Choose {ExpectedFolder}.");
    }

    /// <summary>Every install found where the game is normally put, best guess first and without
    /// duplicates: <paramref name="remembered"/>, then the Windows or the Linux places
    /// <paramref name="roots"/> names. Only folders passing <see cref="IsInstall"/> are returned,
    /// so a stale remembered path drops out. The first entry pre-fills the picker.</summary>
    public static IReadOnlyList<string> Candidates(InstallSearchRoots roots, string? remembered)
    {
        var comparer = roots.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var found = new List<string>();
        var places = new List<string>();
        if (!string.IsNullOrWhiteSpace(remembered))
        {
            places.Add(Normalize(remembered));
        }

        places.AddRange(roots.Windows ? WindowsPlaces(roots) : LinuxPlaces(roots));
        foreach (string place in places)
        {
            if (Directory.Exists(place) && IsInstall(place) && seen.Add(Canonical(place)))
            {
                found.Add(place);
            }
        }

        return found;
    }

    // Today's Windows places: the installer's folder under each Program Files, then the places a
    // manual copy lands on every fixed drive. The installer's path comes first because a copy may
    // be a leftover.
    private static IEnumerable<string> WindowsPlaces(InstallSearchRoots roots)
    {
        foreach (string programFiles in roots.ProgramFiles)
        {
            if (ResolveDirectory(programFiles, InstallFolder) is { } install)
            {
                yield return install;
            }
        }

        foreach (string drive in roots.FixedDrives)
        {
            foreach (string below in new[] { InstallFolder, "Games/Crimson Skies", "Crimson Skies" })
            {
                if (ResolveDirectory(drive, below) is { } install)
                {
                    yield return install;
                }
            }
        }
    }

    // The Wine prefix, then every Steam Proton prefix under both of Steam's roots. Canonical folds
    // the two roots into one entry where ~/.steam/steam links to ~/.local/share/Steam.
    private static IEnumerable<string> LinuxPlaces(InstallSearchRoots roots)
    {
        if (roots.Home is not { } home)
        {
            yield break;
        }

        var driveCs = new List<string>();
        if (ResolveDirectory(home, ".wine/drive_c") is { } wine)
        {
            driveCs.Add(wine);
        }

        foreach (string steam in new[] { ".local/share/Steam/steamapps/compatdata", ".steam/steam/steamapps/compatdata" })
        {
            if (ResolveDirectory(home, steam) is not { } compatdata)
            {
                continue;
            }

            foreach (string prefix in SortedSubdirectories(compatdata))
            {
                if (ResolveDirectory(prefix, "pfx/drive_c") is { } driveC)
                {
                    driveCs.Add(driveC);
                }
            }
        }

        foreach (string driveC in driveCs)
        {
            foreach (string programFiles in SortedSubdirectories(driveC))
            {
                if (Path.GetFileName(programFiles).StartsWith("Program Files", StringComparison.OrdinalIgnoreCase)
                    && ResolveDirectory(programFiles, InstallFolder) is { } install)
                {
                    yield return install;
                }
            }
        }
    }

    private static string? Resolve(string root, string relativePath, bool wantFile)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        string[] segments = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return wantFile ? null : root;
        }

        string current = root;
        for (int i = 0; i < segments.Length; i++)
        {
            bool last = i == segments.Length - 1;
            string? next = MatchEntry(current, segments[i], file: last && wantFile);
            if (next == null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    // Enumerates rather than probing Path.Combine, because Windows answers a probe in any case and
    // would hand back the caller's spelling instead of the disk's. Ordinal order makes the pick
    // between two case-folded matches the same on every run.
    private static string? MatchEntry(string directory, string name, bool file)
    {
        string? folded = null;
        try
        {
            var entries = file ? Directory.EnumerateFiles(directory) : Directory.EnumerateDirectories(directory);
            foreach (string entry in entries.OrderBy(e => e, StringComparer.Ordinal))
            {
                string entryName = Path.GetFileName(entry);
                if (string.Equals(entryName, name, StringComparison.Ordinal))
                {
                    return entry;
                }

                if (folded == null && string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                {
                    folded = entry;
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return folded;
    }

    private static bool HasArchives(string zbd)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive };
            return Directory.EnumerateFiles(zbd, "*.zbd", options).Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? InstallAbove(string folder)
    {
        for (var parent = Directory.GetParent(folder); parent != null; parent = parent.Parent)
        {
            if (IsInstall(parent.FullName))
            {
                return parent.FullName;
            }
        }

        return null;
    }

    private static string? InstallBelow(string folder, int depth)
    {
        if (depth == 0)
        {
            return null;
        }

        var children = SortedSubdirectories(folder);
        foreach (string child in children)
        {
            if (IsInstall(child))
            {
                return child;
            }
        }

        foreach (string child in children)
        {
            if (InstallBelow(child, depth - 1) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static List<string> SortedSubdirectories(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder).OrderBy(d => d, StringComparer.Ordinal).ToList();
        }
        catch (IOException)
        {
            return new List<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    private static string Normalize(string picked)
    {
        string trimmed = picked.Trim().Trim('"').Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        string full;
        try
        {
            full = Path.GetFullPath(trimmed);
        }
        catch (ArgumentException)
        {
            return trimmed;
        }
        catch (NotSupportedException)
        {
            return trimmed;
        }

        return Path.TrimEndingDirectorySeparator(full);
    }

    // The path with every linked segment replaced by its target, the key two spellings of one
    // install fold to. A segment that cannot be read stays as written. A target is canonicalised
    // in turn, since it may be spelled through a linked parent (macOS's /var, Silverblue's /home).
    private static string Canonical(string path, int depth = 0)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || depth > MaxLinkDepth)
        {
            return full;
        }

        string current = root;
        foreach (string segment in full.Substring(root.Length).Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = Canonical(target.FullName, depth + 1);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }
}
