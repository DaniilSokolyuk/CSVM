using System;

namespace CSVM.Mech3;

/// <summary>
/// A path the game's own data names, split the way the original reads it: on <c>\</c> and on
/// <c>/</c> alike, on every host. The readers list their files by Windows path. The host's
/// <see cref="System.IO.Path"/> splits on <c>\</c> only on Windows, so on Linux it takes such a
/// path for one file name.
/// ⚠ Split a path out of game data here, never through <see cref="System.IO.Path"/>. A path on
/// our own disk is the host's and stays with <see cref="System.IO.Path"/>.
/// </summary>
public static class GamePath
{
    private static readonly char[] Separators = { '\\', '/' };

    /// <summary>The last segment: everything after the last <c>\</c> or <c>/</c>, the whole
    /// string when there is neither, empty when the path ends in one. A drive letter is not a
    /// segment of its own, so <c>c:name.zrd</c> stays whole.</summary>
    public static string FileName(string path) => path[(path.LastIndexOfAny(Separators) + 1)..];

    /// <summary>True when <paramref name="folder"/> (one segment, or several joined by either
    /// separator) names consecutive folder segments of <paramref name="path"/>, ignoring case. The
    /// file name is not a folder, so <c>cutscenes</c> does not match <c>..\zrdr\cutscenes</c>.</summary>
    public static bool HasFolder(string path, string folder)
    {
        int cut = path.LastIndexOfAny(Separators);
        if (cut < 0 || folder.Length == 0)
        {
            return false;
        }

        string folders = "/" + path[..cut].Replace('\\', '/') + "/";
        return folders.Contains("/" + folder.Replace('\\', '/').Trim('/') + "/", StringComparison.OrdinalIgnoreCase);
    }
}
