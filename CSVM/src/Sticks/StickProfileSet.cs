using System;
using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.Utils;

namespace CSVM.Sticks;

/// <summary>
/// The stick profiles in force: the loaded files, the connected models, and the active file per
/// model the resolver picks, re-picked whenever the roster changes. Seat 1's keymap is completed
/// through <see cref="MergeInto"/>, registered as <see cref="LaunchBindings.StickRows"/>. A stick
/// action source reads <see cref="Map"/>, and the controls screens save through
/// <see cref="SaveFrom"/>. The pair <see cref="Revision"/> and <see cref="Changed"/> says when the
/// active set moved, which is when a seat already flying must re-read. Engine-free; <c>StickProfiles</c> builds
/// the live one.
/// </summary>
public sealed class StickProfileSet : IStickRows
{
    private readonly StickProfileStore _store;
    private readonly Func<IReadOnlyCollection<StickModel>> _connected;
    private IReadOnlyList<StickProfileFile> _files = Array.Empty<StickProfileFile>();
    private IReadOnlyDictionary<StickModel, StickProfileFile> _active = new Dictionary<StickModel, StickProfileFile>();
    private List<StickModel> _present = new();

    /// <summary>A set over <paramref name="store"/> selecting for whatever
    /// <paramref name="connected"/> answers; call <see cref="Reload"/> once to read the files.
    /// </summary>
    public StickProfileSet(StickProfileStore store, Func<IReadOnlyCollection<StickModel>> connected)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _connected = connected ?? throw new ArgumentNullException(nameof(connected));
    }

    /// <summary>Raised after the active set changed: a plug, an unplug, a reload or a save.</summary>
    public event Action? Changed;

    /// <summary>Counts every change of the active set, for a reader that polls instead of
    /// subscribing.</summary>
    public int Revision { get; private set; }

    /// <summary>Every usable profile file, shipped then user.</summary>
    public IReadOnlyList<StickProfileFile> Files => _files;

    /// <summary>The active file per connected model; a model with none has no entry.</summary>
    public IReadOnlyDictionary<StickModel, StickProfileFile> Active => _active;

    /// <summary>The store this set reads and saves through.</summary>
    public StickProfileStore Store => _store;

    /// <summary>The models <paramref name="roster"/> holds open, one per unit, which is the
    /// connected set the live profile set selects over.</summary>
    public static IReadOnlyCollection<StickModel> ModelsOf(StickRoster roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        var models = new List<StickModel>(roster.Sticks.Count);
        foreach (var stick in roster.Sticks)
        {
            models.Add(stick.Model);
        }

        return models;
    }

    /// <summary>That model's active profile, or null when none applies.</summary>
    public StickProfile? ActiveFor(StickModel model) =>
        _active.TryGetValue(model, out var file) ? file.Profile : null;

    /// <summary>Re-reads every file, then re-selects. True when the active set changed.</summary>
    public bool Reload()
    {
        _files = _store.LoadAll();
        return Select(force: true);
    }

    /// <summary>Re-selects against the models connected now, doing nothing when they are the ones
    /// last seen. True when the active set changed. The roster's change signal calls it.</summary>
    public bool Refresh() => Select(force: false);

    /// <summary>The stick rows of one context from the active profiles, as a fresh map.</summary>
    public ActionMap Map(InputContext context) => StickProfileResolver.Rows(ActiveProfiles(), context);

    /// <inheritdoc/>
    public void MergeInto(BindingProfile keymap) => StickProfileResolver.MergeInto(keymap, ActiveProfiles());

    /// <summary>Merges the active rows into <paramref name="keymap"/> in place when the active set has
    /// moved since <paramref name="seen"/>, and records the revision. True when it merged. A seat
    /// already flying calls it each tick, so a plug reaches every reader of its maps.</summary>
    public bool MergeIfChanged(BindingProfile keymap, ref int seen)
    {
        if (seen == Revision)
        {
            return false;
        }

        seen = Revision;
        MergeInto(keymap);
        return true;
    }

    /// <summary>Saves <paramref name="profile"/> copy-on-write through the store and re-selects; a
    /// shipped file becomes a user copy. The file saved from defaults to the model's active file
    /// when that file is for the same layout.</summary>
    public StickProfileFile Save(StickProfile profile, StickProfileFile? from = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (from is null && _active.TryGetValue(profile.Model, out var active) && active.Profile.SameLayout(profile))
        {
            from = active;
        }

        var saved = _store.Save(profile, from);
        var files = new List<StickProfileFile>(_files.Count + 1);
        foreach (var file in _files)
        {
            if (file.Source != StickProfileSource.User
                || !string.Equals(file.FileName, saved.FileName, StringComparison.OrdinalIgnoreCase))
            {
                files.Add(file);
            }
        }

        files.Add(saved);
        _files = files;
        Select(force: true);
        return saved;
    }

    /// <summary>What an accepted controls screen calls for seat 1. Each connected model's stick rows
    /// in <paramref name="keymap"/> go to its active profile, or to a new user profile without
    /// companions. Unchanged rows are not written, nor is an ignored profile. Returns the files
    /// written.</summary>
    public IReadOnlyList<StickProfileFile> SaveFrom(BindingProfile keymap)
    {
        ArgumentNullException.ThrowIfNull(keymap);
        var written = new List<StickProfileFile>();
        foreach (var model in new List<StickModel>(_present))
        {
            var current = ActiveFor(model);
            if (current is { Ignore: true })
            {
                continue;
            }

            var edited = current?.Clone() ?? new StickProfile(model);
            bool any = CopyRows(keymap, model, edited);
            if (current is null ? !any : StickProfileStore.Serialize(edited) == StickProfileStore.Serialize(current))
            {
                continue;
            }

            written.Add(Save(edited));
        }

        return written;
    }

    // One model's bindings out of a merged keymap into a profile's maps, replacing its rows. The
    // unread rows stay, so a hand-typed row this build cannot parse survives the screen.
    private static bool CopyRows(BindingProfile keymap, StickModel model, StickProfile profile)
    {
        bool any = false;
        foreach (var context in Enum.GetValues<InputContext>())
        {
            var source = keymap.Map(context);
            var target = profile.Map(context);
            target.Clear();
            foreach (var action in DefaultBindings.ActionsIn(context))
            {
                foreach (var binding in BindingStore.StoredRow(source, action))
                {
                    if (StickModel.TryFromDevice(binding.Device, out var named) && named == model)
                    {
                        target.Add(action, new Binding(model.Device, binding.Control));
                        any = true;
                    }
                }
            }
        }

        return any;
    }

    private static bool SameModels(List<StickModel> left, List<StickModel> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameChoice(
        IReadOnlyDictionary<StickModel, StickProfileFile> left, IReadOnlyDictionary<StickModel, StickProfileFile> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var other) || !ReferenceEquals(other, pair.Value))
            {
                return false;
            }
        }

        return true;
    }

    private List<StickProfile> ActiveProfiles()
    {
        var profiles = new List<StickProfile>(_active.Count);
        foreach (var file in _active.Values)
        {
            profiles.Add(file.Profile);
        }

        return profiles;
    }

    private bool Select(bool force)
    {
        var present = new List<StickModel>();
        foreach (var model in _connected())
        {
            if (!present.Contains(model))
            {
                present.Add(model);
            }
        }

        present.Sort(StickProfile.CompareModels);
        if (!force && SameModels(present, _present))
        {
            return false;
        }

        _present = present;
        var next = StickProfileResolver.Resolve(present, _files);
        if (!force && SameChoice(next, _active))
        {
            return false;
        }

        _active = next;
        Revision++;
        foreach (var model in present)
        {
            string choice = next.TryGetValue(model, out var file)
                ? file.Source.ToString().ToLowerInvariant() + " " + file.FileName + (file.Profile.Ignore ? " (ignored)" : string.Empty)
                : "none";
            Log.Info("core", $"stick profile for {model}: {choice}");
        }

        Changed?.Invoke();
        return true;
    }
}
