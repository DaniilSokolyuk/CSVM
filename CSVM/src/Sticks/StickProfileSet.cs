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
/// active set moved, which is when a seat already flying must re-read. When exactly one
/// stick-shaped model has no file, <see cref="GenericStickDefault"/> is its active profile.
/// Engine-free; <c>StickProfiles</c> builds the live one.
/// </summary>
public sealed class StickProfileSet : IStickRows
{
    private readonly StickProfileStore _store;
    private readonly Func<IReadOnlyCollection<StickModel>> _connected;
    private readonly Func<StickModel, StickShape>? _shapeOf;
    private IReadOnlyList<StickProfileFile> _files = Array.Empty<StickProfileFile>();
    private IReadOnlyDictionary<StickModel, StickProfileFile> _active = new Dictionary<StickModel, StickProfileFile>();
    private List<StickModel> _present = new();

    // The generic default last handed out, kept so re-selecting the same claim yields the same file
    // and a quiet refresh reports no change.
    private StickProfileFile? _generic;

    /// <summary>A set over <paramref name="store"/> selecting for whatever
    /// <paramref name="connected"/> answers. Call <see cref="Reload"/> once to read the files. The
    /// <paramref name="shapeOf"/> judge decides the generic single-stick default; without it no
    /// model gets one.</summary>
    public StickProfileSet(
        StickProfileStore store, Func<IReadOnlyCollection<StickModel>> connected, Func<StickModel, StickShape>? shapeOf = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _connected = connected ?? throw new ArgumentNullException(nameof(connected));
        _shapeOf = shapeOf;
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

    /// <summary>Re-selects against the models connected now and their shapes. True when the active
    /// set changed. The roster's change signal calls it, which also fires when a stick settles.
    /// </summary>
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

    /// <summary>The same follow for a reader holding one context's map, such as a menu seat.</summary>
    public bool MergeIfChanged(ActionMap map, InputContext context, ref int seen)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (seen == Revision)
        {
            return false;
        }

        seen = Revision;
        StickProfileResolver.MergeInto(map, ActiveProfiles(), context);
        return true;
    }

    /// <summary>The rows a Controls screen reset gives each connected model, from shipped files alone.
    /// That is the shipped file the resolver would pick. Failing that, the one stick-shaped model with
    /// no shipped file gets the generic default. A model with neither, or ignored by its active or
    /// shipped file, has no entry and keeps its rows. Here a user file does not count as a profile.
    /// </summary>
    public IReadOnlyDictionary<StickModel, StickProfile> ResetDefaults()
    {
        var shippedFiles = new List<StickProfileFile>();
        foreach (var file in _files)
        {
            if (file.Source == StickProfileSource.Shipped)
            {
                shippedFiles.Add(file);
            }
        }

        var shipped = StickProfileResolver.Resolve(_present, shippedFiles);
        var defaults = new Dictionary<StickModel, StickProfile>();
        var unshipped = new List<StickModel>();
        foreach (var model in _present)
        {
            if (ActiveFor(model) is { Ignore: true })
            {
                continue;
            }

            if (!shipped.TryGetValue(model, out var file))
            {
                unshipped.Add(model);
            }
            else if (!file.Profile.Ignore)
            {
                defaults[model] = file.Profile;
            }
        }

        if (_shapeOf is not null && GenericStickDefault.Pick(unshipped, _shapeOf) is { } generic)
        {
            defaults[generic] = GenericStickDefault.For(generic, _shapeOf(generic).Axes);
        }

        return defaults;
    }

    /// <inheritdoc/>
    public void ResetInto(ActionMap reset, ActionMap staged, InputContext context)
    {
        ArgumentNullException.ThrowIfNull(reset);
        ArgumentNullException.ThrowIfNull(staged);
        foreach (var action in DefaultBindings.ActionsIn(context))
        {
            foreach (var binding in BindingStore.StoredRow(staged, action))
            {
                if (StickProfileResolver.IsStick(binding))
                {
                    reset.Add(action, binding);
                }
            }
        }

        StickProfileResolver.ReplaceRows(reset, ResetDefaults().Values, context);
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
        // ⚠ The choice the keymap was staged against, not the live one. Each Save re-selects and can
        // hand the generic default to a later model. That model's rows were never in the keymap, so
        // copying them from it would write its file empty.
        var staged = _active;
        foreach (var model in new List<StickModel>(_present))
        {
            var current = staged.TryGetValue(model, out var file) ? file.Profile : null;
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

    // The generic single-stick default joins the resolver's choice for the one model it claims, if
    // any. The claimed model keeps the same file object while its claim holds.
    private void AddGeneric(Dictionary<StickModel, StickProfileFile> next, List<StickModel> present)
    {
        var unprofiled = present.FindAll(model => !next.ContainsKey(model));
        if (_shapeOf is null || GenericStickDefault.Pick(unprofiled, _shapeOf) is not { } model)
        {
            _generic = null;
            return;
        }

        int axes = _shapeOf(model).Axes;
        if (_generic is null || _generic.Profile.Model != model)
        {
            _generic = new StickProfileFile(StickProfileSource.Generic, GenericStickDefault.FileName, GenericStickDefault.For(model, axes));
        }

        next[model] = _generic;
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
        _present = present;
        var next = new Dictionary<StickModel, StickProfileFile>(StickProfileResolver.Resolve(present, _files));
        AddGeneric(next, present);
        if (!force && SameChoice(next, _active))
        {
            return false;
        }

        _active = next;
        Revision++;
        var connected = new HashSet<StickModel>(present);
        foreach (var model in present)
        {
            string choice = next.TryGetValue(model, out var file)
                ? file.Source.ToString().ToLowerInvariant() + " " + file.FileName + (file.Profile.Ignore ? " (ignored)" : string.Empty)
                : "none";
            Log.Info("core", $"stick profile for {model}: {choice}");
            if (file is not null)
            {
                foreach (var tied in StickProfileResolver.TiedWith(file, _files, connected))
                {
                    Log.Warn("core", $"stick profile for {model}: {file.FileName} and {tied.FileName} both apply, {file.FileName} wins on its name alone");
                }
            }
        }

        Changed?.Invoke();
        return true;
    }
}
