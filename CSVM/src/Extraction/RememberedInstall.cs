using System.IO;
using CSVM.Utils;

namespace CSVM.Extraction;

/// <summary>The install folder the last extraction read, kept as <see cref="OptionsDef.InstallPath"/>
/// in <c>user://options.json</c> so it survives a data tree being deleted. The overloads taking a
/// store are what tests call; the bare ones use <see cref="OptionsStore.UserOptions"/>, which needs
/// the engine. A save loads first and changes only this field, so every other option is kept.</summary>
public static class RememberedInstall
{
    /// <summary>The remembered folder from the player's own options, or null when none is saved.
    /// Not checked here: <see cref="InstallLocator.Candidates"/> drops it when it is stale.</summary>
    public static string? Get() => Get(OptionsStore.UserOptions());

    /// <summary>The remembered folder in <paramref name="store"/>, or null when none is saved.</summary>
    public static string? Get(OptionsStore store) => store.Load().InstallPath;

    /// <summary>Remembers <paramref name="installRoot"/> in the player's own options.</summary>
    public static void Set(string installRoot) => Set(OptionsStore.UserOptions(), installRoot);

    /// <summary>Remembers <paramref name="installRoot"/> in <paramref name="store"/>, made fully
    /// qualified first. A relative path would resolve against the next run's working directory,
    /// and the store drops one.</summary>
    public static void Set(OptionsStore store, string installRoot)
    {
        var options = store.Load();
        options.InstallPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        store.Save(options);
    }
}
