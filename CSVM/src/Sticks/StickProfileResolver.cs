using System;
using System.Collections.Generic;
using CSVM.Bindings;

namespace CSVM.Sticks;

/// <summary>
/// Which profile file is active for each connected stick model, and the binding rows the active set
/// yields. Pure: connected models and loaded files in, a choice per model out. A file applies while
/// its model and every companion it names are connected. Among those, more companions win, then a
/// user file over a shipped one, then the ordinally first file name. So R alone takes its solo
/// file, R with L takes the file naming L, and a saved copy beats the shipped original. The rule is
/// in <c>docs/org/input.md</c>, "The CSVM stick profile files".
/// </summary>
public static class StickProfileResolver
{
    /// <summary>The active file per connected model; a model no file applies to has no entry.
    /// Duplicate connected entries (identical units) count once.</summary>
    public static IReadOnlyDictionary<StickModel, StickProfileFile> Resolve(
        IEnumerable<StickModel> connected, IEnumerable<StickProfileFile> files)
    {
        ArgumentNullException.ThrowIfNull(connected);
        ArgumentNullException.ThrowIfNull(files);
        var present = new HashSet<StickModel>(connected);
        var active = new Dictionary<StickModel, StickProfileFile>();
        foreach (var file in files)
        {
            var model = file.Profile.Model;
            if (!Applies(file.Profile, present))
            {
                continue;
            }

            if (!active.TryGetValue(model, out var best) || Compare(file, best) < 0)
            {
                active[model] = file;
            }
        }

        return active;
    }

    /// <summary>Whether <paramref name="profile"/> applies with <paramref name="connected"/> plugged
    /// in: its model and all its companions are there.</summary>
    public static bool Applies(StickProfile profile, IReadOnlySet<StickModel> connected)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(connected);
        if (!connected.Contains(profile.Model))
        {
            return false;
        }

        foreach (var companion in profile.Companions)
        {
            if (!connected.Contains(companion))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The other applying files for <paramref name="winner"/>'s model that only its file name
    /// beats: the same companions and the same source. A copy made beside a file in Explorer is
    /// one, and it silently takes over whenever its name sorts first.</summary>
    public static List<StickProfileFile> TiedWith(
        StickProfileFile winner, IEnumerable<StickProfileFile> files, IReadOnlySet<StickModel> connected)
    {
        ArgumentNullException.ThrowIfNull(winner);
        ArgumentNullException.ThrowIfNull(files);
        var tied = new List<StickProfileFile>();
        foreach (var file in files)
        {
            if (!ReferenceEquals(file, winner)
                && file.Profile.Model == winner.Profile.Model
                && file.Source == winner.Source
                && file.Profile.Companions.Count == winner.Profile.Companions.Count
                && Applies(file.Profile, connected))
            {
                tied.Add(file);
            }
        }

        return tied;
    }

    /// <summary>Negative when <paramref name="left"/> ranks ahead of <paramref name="right"/> for the
    /// same model: more companions, then user over shipped, then ordinal file name. Total, so the
    /// choice never depends on the order files were listed in.</summary>
    public static int Compare(StickProfileFile left, StickProfileFile right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        int byCompanions = right.Profile.Companions.Count.CompareTo(left.Profile.Companions.Count);
        if (byCompanions != 0)
        {
            return byCompanions;
        }

        if (left.Source != right.Source)
        {
            return left.Source == StickProfileSource.User ? -1 : 1;
        }

        return string.CompareOrdinal(left.FileName, right.FileName);
    }

    /// <summary>The stick rows the active profiles give one context, as one map: each non-ignored
    /// profile's rows on its own model's identity, in model order.</summary>
    public static ActionMap Rows(IEnumerable<StickProfile> active, InputContext context)
    {
        var map = new ActionMap();
        AddRows(map, active, context);
        return map;
    }

    /// <summary>Takes every stick binding off <paramref name="keymap"/> in every context and adds the
    /// active profiles' rows in their place, leaving every other row untouched. A stick binding the
    /// keymap file carried is dropped here, since stick rows belong to the profile files.</summary>
    public static void MergeInto(BindingProfile keymap, IEnumerable<StickProfile> active)
    {
        ArgumentNullException.ThrowIfNull(keymap);
        var profiles = new List<StickProfile>(active);
        foreach (var context in Enum.GetValues<InputContext>())
        {
            MergeInto(keymap.Map(context), profiles, context);
        }
    }

    /// <summary>The same replacement on one context's map, for a reader holding only that map.
    /// </summary>
    public static void MergeInto(ActionMap map, IEnumerable<StickProfile> active, InputContext context)
    {
        ArgumentNullException.ThrowIfNull(map);
        RemoveStickRows(map);
        AddRows(map, active, context);
    }

    /// <summary>A copy of <paramref name="keymap"/> without its stick bindings: what the keymap
    /// file is saved from, so stick rows are written only to the profile files.</summary>
    public static BindingProfile WithoutStickRows(BindingProfile keymap)
    {
        ArgumentNullException.ThrowIfNull(keymap);
        var maps = new Dictionary<InputContext, ActionMap>();
        foreach (var context in Enum.GetValues<InputContext>())
        {
            var map = keymap.Map(context).Clone();
            RemoveStickRows(map);
            maps[context] = map;
        }

        return new BindingProfile(maps, keymap.ReadsKeyboard)
        {
            MouseFlying = keymap.MouseFlying,
            MouseSensitivity = keymap.MouseSensitivity,
        };
    }

    /// <summary>Replaces the rows of each model <paramref name="profiles"/> names with that profile's
    /// rows for one context. Every other binding, another stick's included, is left alone.</summary>
    public static void ReplaceRows(ActionMap map, IEnumerable<StickProfile> profiles, InputContext context)
    {
        ArgumentNullException.ThrowIfNull(map);
        var list = new List<StickProfile>(profiles);
        var models = new HashSet<StickModel>();
        foreach (var profile in list)
        {
            models.Add(profile.Model);
        }

        RemoveRows(map, binding => StickModel.TryFromDevice(binding.Device, out var model) && models.Contains(model));
        AddRows(map, list, context);
    }

    /// <summary>Whether <paramref name="binding"/> is on a stick identity, in any case.</summary>
    public static bool IsStick(Binding binding) => StickModel.TryFromDevice(binding.Device, out _);

    private static void RemoveStickRows(ActionMap map) => RemoveRows(map, IsStick);

    private static void RemoveRows(ActionMap map, Func<Binding, bool> drop)
    {
        foreach (var action in new List<InputAction>(map.BoundActions))
        {
            foreach (var binding in new List<Binding>(map.Bindings(action)))
            {
                if (drop(binding))
                {
                    map.Unassign(action, binding);
                }
            }
        }
    }

    // Model order, so the rows a screen lists come out the same whatever order devices enumerated
    // in. Add rather than Assign: two profiles never share a device, and a row's own order stays.
    private static void AddRows(ActionMap map, IEnumerable<StickProfile> active, InputContext context)
    {
        var profiles = new List<StickProfile>(active);
        profiles.Sort((a, b) => StickProfile.CompareModels(a.Model, b.Model));
        foreach (var profile in profiles)
        {
            if (profile.Ignore)
            {
                continue;
            }

            var rows = profile.Map(context);
            foreach (var action in DefaultBindings.ActionsIn(context))
            {
                foreach (var binding in BindingStore.StoredRow(rows, action))
                {
                    map.Add(action, binding);
                }
            }
        }
    }
}
