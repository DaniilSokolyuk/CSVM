using Godot;

namespace CSVM.Utils;

/// <summary>
/// Shows a folder in the system file browser, creating it first. A directory path handed to
/// <see cref="OS.ShellOpen"/> opens the file browser on Windows and on Linux alike. Every caller that hands a player a folder goes through here, so each open
/// and each failure reads the same in the log.
/// </summary>
public static class FolderOpener
{
    /// <summary>Creates <paramref name="path"/> if missing and opens it. <paramref name="what"/>
    /// names the folder in the log line. Returns the full path, or null with a warning when either
    /// step failed.</summary>
    public static string? Open(string path, string what)
    {
        string full = System.IO.Path.GetFullPath(path);
        var made = DirAccess.MakeDirRecursiveAbsolute(full);
        if (made != Error.Ok && !DirAccess.DirExistsAbsolute(full))
        {
            Log.Warn("core", $"{what} {full} could not be created: {made}");
            return null;
        }

        var opened = OS.ShellOpen(full);
        if (opened != Error.Ok)
        {
            Log.Warn("core", $"{what} {full} could not be opened: {opened}");
            return null;
        }

        Log.Info("core", $"{what} opened: {full}");
        return full;
    }
}
