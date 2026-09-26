using System;
using System.IO;

namespace CSVM.UI.Campaign;

/// <summary>
/// EXPORT TO DESKTOP, the scrap detail view's second button (<c>uiData</c> 2412,
/// <c>FUN_00406870</c>): the open scrap's own file copied byte for byte to the desktop under its
/// own base name, overwriting whatever stood there, then the outcome reported in words. Engine-free
/// so the copy and both messages test off engine, and so a test can write somewhere other than a
/// real desktop.
/// </summary>
public static class ScrapbookExport
{
    /// <summary>Copies <paramref name="source"/> into <paramref name="folder"/>, the user's desktop
    /// when none is given. Returns whether it landed and either the name it landed as or the reason
    /// it did not, which are langui 705's and 706's own arguments.</summary>
    public static (bool Saved, string Detail) ToDesktop(string source, string? folder = null)
    {
        string target = folder ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string name = Path.GetFileName(source);
        if (target.Length == 0 || name.Length == 0)
        {
            return (false, "no desktop folder");
        }

        try
        {
            File.Copy(source, Path.Combine(target, name), overwrite: true);
            return (true, name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return (false, e.Message);
        }
    }
}
