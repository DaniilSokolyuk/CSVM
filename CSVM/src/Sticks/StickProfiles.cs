using System;
using System.Collections.Generic;
using CSVM.Bindings;
using Godot;

namespace CSVM.Sticks;

/// <summary>
/// The engine side of the stick profiles: where the files live and the one live
/// <see cref="StickProfileSet"/>. Shipped profiles are read through Godot's file API, since an
/// exported build keeps them in the pck. User profiles live in <c>user://stick_profiles/</c>, the
/// only place a save writes. The pump (<see cref="StickPump"/>) starts the set and refreshes it on
/// every roster change, so a companion plugged in mid-flight switches files that frame.
/// </summary>
public static class StickProfiles
{
    /// <summary>The shipped, read-only profiles, committed under <c>CSVM/data/stick_profiles/</c>
    /// and exported through the preset's <c>data/*.json</c> include filter.</summary>
    public const string ShippedDirectory = "res://data/stick_profiles";

    /// <summary>The player's own profiles, and the folder the controls screen opens.</summary>
    public const string UserDirectory = "user://stick_profiles";

    /// <summary>Where user profiles are read and written instead of <see cref="UserDirectory"/>
    /// while set, so a suite never loads or overwrites the profiles on this machine.</summary>
    public static string? DirectoryOverride { get; set; }

    /// <summary>The live set, or null while sticks are off (no roster, so every scripted run).
    /// </summary>
    public static StickProfileSet? Live { get; private set; }

    /// <summary>The user profile folder as an OS path, or the <see cref="DirectoryOverride"/>.
    /// </summary>
    public static string UserPath() => DirectoryOverride ?? ProjectSettings.GlobalizePath(UserDirectory);

    /// <summary>Builds and loads the live set over <paramref name="roster"/>, and registers it as
    /// seat 1's stick rows (<see cref="LaunchBindings.StickRows"/>).</summary>
    public static StickProfileSet Start(StickRoster roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        var set = new StickProfileSet(new StickProfileStore(Shipped, UserPath()), () => StickProfileSet.ModelsOf(roster));
        set.Reload();
        Live = set;
        LaunchBindings.StickRows = set;
        return set;
    }

    /// <summary>Seat 1's per-tick follow of the live set: see
    /// <see cref="StickProfileSet.MergeIfChanged"/>. False, and nothing touched, while sticks are off.
    /// </summary>
    public static bool MergeIfChanged(BindingProfile keymap, ref int seen) =>
        Live is { } set && set.MergeIfChanged(keymap, ref seen);

    /// <summary>Unregisters <paramref name="set"/> if it is still the live one.</summary>
    public static void Stop(StickProfileSet? set)
    {
        if (set is not null && ReferenceEquals(Live, set))
        {
            Live = null;
            LaunchBindings.StickRows = null;
        }
    }

    private static IEnumerable<StickProfileText> Shipped()
    {
        var texts = new List<StickProfileText>();
        if (!DirAccess.DirExistsAbsolute(ShippedDirectory))
        {
            return texts;
        }

        var names = DirAccess.GetFilesAt(ShippedDirectory);
        Array.Sort(names, StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                texts.Add(new StickProfileText(name, Godot.FileAccess.GetFileAsString(ShippedDirectory + "/" + name)));
            }
        }

        return texts;
    }
}
