using System.IO;
using CSVM.Session.Launch;

namespace CSVM.UI.Menu.Original;

/// <summary>
/// Original's availability answer, made before entry: the extraction tree is not older than the
/// manifest reads, the decoded layout file loads with a top level in it, and every file
/// <see cref="OriginalAssetManifest"/> classes required is on disk and reads. A failure is one
/// reason for the host to select Built-in for this run and leave the request alone; what is
/// optional and absent comes back beside it, for the presentation to draw without.
/// </summary>
public static class OriginalAvailability
{
    /// <summary>The main-menu section's name in the layout.</summary>
    public const string MainMenuSection = "MainMenu";

    /// <summary>Where a layout art name resolves under a data root. A movie sits one directory
    /// deeper, which is the base <c>FUN_004a7c70</c> resolves every movie name under and where the
    /// extraction copies the files (<c>docs/formats/cinemas.md</c>).</summary>
    public static string ArtPath(string dataRoot, string name) =>
        Path.Combine(dataRoot, "extracted", "rof", RelativeArtPath(name).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The same place as a path relative to the <c>rof</c> extraction, which is what the
    /// asset manifest records per entry.</summary>
    public static string RelativeArtPath(string name) =>
        IsMovie(name) ? "ASSETS/GRAPHICS/MPG/" + name : "ASSETS/GRAPHICS/" + name;

    /// <summary>Whether a layout art name is one of the movies rather than a bitmap.</summary>
    public static bool IsMovie(string name) =>
        name != null && name.EndsWith(".mpg", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Loads the layout and checks the manifest over <paramref name="dataRoot"/>. Returns
    /// the layout when Original can run, else null with the reason.</summary>
    public static MenuLayout? Load(string dataRoot, out string? reason) =>
        Load(dataRoot, out reason, out _);

    /// <summary>The same check, also answering what is optional and absent so the caller can log
    /// once what the presentation will draw without.</summary>
    public static MenuLayout? Load(string dataRoot, out string? reason, out string? degraded)
    {
        degraded = null;
        if (ExtractionStamp.Behind(dataRoot, OriginalAssetManifest.StampSchema, out reason))
        {
            return null;
        }

        var layout = MenuLayout.TryLoad(MenuLayout.PathUnder(dataRoot), out reason);
        if (layout == null)
        {
            return null;
        }

        reason = Check(layout, dataRoot, out degraded);
        return reason == null ? layout : null;
    }

    /// <summary>Why a loaded layout cannot draw Original over <paramref name="dataRoot"/>, or null
    /// when it can.</summary>
    public static string? Check(MenuLayout layout, string dataRoot) => Check(layout, dataRoot, out _);

    /// <summary>The same answer, with the optional absences beside it.</summary>
    public static string? Check(MenuLayout layout, string dataRoot, out string? degraded)
    {
        degraded = null;
        if (layout.Screen(MainMenuSection) == null)
        {
            return $"the decoded menu layout has no [{MainMenuSection}] section";
        }

        var report = OriginalAssetManifest.Derive(layout).Check(dataRoot);
        degraded = report.Degraded;
        return report.Reason;
    }
}
