using System;
using System.Collections.Generic;
using CSVM.Bindings;
using CSVM.Utils;

namespace CSVM.Sticks;

/// <summary>
/// The sticks CSVM reads beside Godot's pads: each listed device whose model Godot lacks
/// (<see cref="GapFill"/>). <see cref="Update"/> follows plugs and unplugs and
/// logs every change. Reads answer neutral while
/// <c>inputBlocked</c> holds, the same gate <see cref="Pads.For"/> applies to pads; the roster
/// itself is not gated, for the reason <see cref="Pads"/> gives.
/// Engine-free: the library and Godot's roster come in through <see cref="IStickNative"/> and a
/// delegate, so every rule here runs in a unit test. Identity is the model;
/// <see cref="ModelAxis"/> and its siblings merge identical units of one model.
/// </summary>
public sealed class StickRoster : IDisposable
{
    /// <summary>The highest button count read: DirectInput's own limit, and the most any stick
    /// reports. A button at or past it reads released.</summary>
    public const int MaxButtons = 128;

    /// <summary>How many updates after opening a stick's axes are sampled as its rest. DirectInput
    /// answers zeros until a device has been polled a few times, so a sample at open says nothing.
    /// TUNE; <c>--dump-sticks</c> waits as many polls.</summary>
    public const int SettleUpdates = 10;

    /// <summary>Valve's USB vendor id: the Steam Deck's built-in controls, the Steam Controller and
    /// Steam Input's virtual pad. None is a flight stick; see <see cref="GapFill"/>.</summary>
    public const ushort ValveVendor = 0x28DE;

    private readonly IStickNative _native;
    private readonly bool _godotReadsGamepads;
    private readonly Func<IReadOnlyCollection<StickModel>> _godotModels;
    private readonly Func<bool> _inputBlocked;
    private readonly List<Stick> _sticks = new();
    private readonly HashSet<int> _skipped = new();
    private readonly Dictionary<int, int> _age = new();
    private readonly Dictionary<int, float[]> _rest = new();
    private IReadOnlyCollection<StickModel>? _godotSeen;
    private bool _listed;

    /// <param name="native">The stick library; the roster owns it and disposes it.</param>
    /// <param name="godotModels">The models of Godot's pad roster. Returning the same instance
    /// while it is unchanged spares a re-list every frame.</param>
    /// <param name="inputBlocked">The read gate, <see cref="Pads.InputBlocked"/> in the game.</param>
    /// <param name="godotReadsGamepads">True off Windows: <see cref="GapFill"/>'s Linux rules.</param>
    public StickRoster(
        IStickNative native, Func<IReadOnlyCollection<StickModel>> godotModels, Func<bool> inputBlocked, bool godotReadsGamepads = false)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _godotModels = godotModels ?? throw new ArgumentNullException(nameof(godotModels));
        _inputBlocked = inputBlocked ?? throw new ArgumentNullException(nameof(inputBlocked));
        _godotReadsGamepads = godotReadsGamepads;
    }

    /// <summary>The opened sticks, in the order they were opened.</summary>
    public IReadOnlyList<Stick> Sticks => _sticks;

    /// <summary>The stick library's version, for the log.</summary>
    public string Version => _native.Version;

    /// <summary>Whether reads are suppressed right now; the roster keeps following plugs either way.</summary>
    public bool InputBlocked => _inputBlocked();

    /// <summary>The gap-filler: the listed devices whose model Godot's roster lacks, in list order.
    /// A model is dropped whole when Godot has it, so a pad never gets a second reader. With
    /// <paramref name="godotReadsGamepads"/> (Linux, where Godot's SDL3 reads every gamepad) a
    /// device SDL maps as a gamepad, or any <see cref="ValveVendor"/> device, is dropped too.
    /// Rules and reasons: <c>docs/tooling.md</c>, "SDL2 for flight sticks".</summary>
    public static StickListing[] GapFill(
        IReadOnlyList<StickListing> listed, IReadOnlyCollection<StickModel> godot, bool godotReadsGamepads = false)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(godot);
        var models = new HashSet<StickModel>(godot);
        var kept = new List<StickListing>(listed.Count);
        foreach (var listing in listed)
        {
            if (SkipReason(listing, models, godotReadsGamepads) is null)
            {
                kept.Add(listing);
            }
        }

        return kept.ToArray();
    }

    /// <summary>SDL's signed 16-bit axis as -1..1. The negative end is one step longer, so it is
    /// clamped rather than scaled by 32768, which keeps full positive travel at exactly 1.</summary>
    public static float Normalise(short raw) => Math.Max(-1f, raw / 32767f);

    /// <summary>One stick as its roster line prints it: name, model, GUID and control counts.</summary>
    public static string Describe(Stick stick)
    {
        ArgumentNullException.ThrowIfNull(stick);
        return Log.Format($"\"{stick.Name}\" {stick.Model} guid={stick.Guid} axes={stick.Axes} buttons={stick.Buttons} hats={stick.Hats}");
    }

    /// <summary>The suffix a line printing a model's axis values carries, <c> quirks=[axis 5 flipped]</c>,
    /// so a reader knows the values are corrected. Empty for a model with none.</summary>
    public static string QuirksText(StickModel model) =>
        StickQuirks.Describe(model) is { Length: > 0 } quirks ? $" quirks=[{quirks}]" : string.Empty;

    /// <summary>Once per frame: pumps the library, and re-applies the gap-filler when a device came
    /// or went or Godot's roster changed. True when the opened set changed or a stick's rest was
    /// just sampled (<see cref="RestingAxes"/>). The first call always lists, so a roster is
    /// complete as soon as it is built.</summary>
    public bool Update()
    {
        bool plugged = _native.Pump();
        var godot = _godotModels();
        bool changed = false;
        if (!_listed || plugged || !ReferenceEquals(godot, _godotSeen))
        {
            _listed = true;
            _godotSeen = godot;
            changed = Reconcile(_native.List(), godot);
        }

        return Settle() || changed;
    }

    /// <summary>A stick's axes as they read <see cref="SettleUpdates"/> updates after it opened,
    /// or null before then or once it is gone. Sampled ungated, since where an axis rests is a
    /// hardware fact like the roster itself.</summary>
    public IReadOnlyList<float>? RestingAxes(Stick stick)
    {
        ArgumentNullException.ThrowIfNull(stick);
        return _rest.TryGetValue(stick.Instance, out var rest) ? rest : null;
    }

    /// <summary>A stick's axis, -1..1, after its model's <see cref="StickQuirks"/>; 0 while blocked,
    /// for an axis it lacks, or once it is gone.</summary>
    public float Axis(Stick stick, int axis) =>
        Readable(stick, out var live) && axis >= 0 && axis < live.Axes
            ? Read(live, axis)
            : 0f;

    /// <summary>Whether a stick's button is held; false while blocked, past its count or past
    /// <see cref="MaxButtons"/>, or once it is gone.</summary>
    public bool Button(Stick stick, int button) =>
        Readable(stick, out var live) && button >= 0 && button < Math.Min(live.Buttons, MaxButtons)
            && _native.Button(live.Instance, button);

    /// <summary>A stick's hat, as the binding model's direction bits; centred while blocked.</summary>
    public HatDirection Hat(Stick stick, int hat) =>
        Readable(stick, out var live) && hat >= 0 && hat < live.Hats
            ? (HatDirection)(_native.Hat(live.Instance, hat) & 0x0F)
            : HatDirection.None;

    /// <summary>The model's axis across every connected unit of it: the deepest deflection wins,
    /// the way <c>BindingSet</c> takes the deepest of its bindings.</summary>
    public float ModelAxis(StickModel model, int axis)
    {
        float deepest = 0f;
        foreach (var stick in _sticks)
        {
            if (stick.Model == model)
            {
                float value = Axis(stick, axis);
                if (Math.Abs(value) > Math.Abs(deepest))
                {
                    deepest = value;
                }
            }
        }

        return deepest;
    }

    /// <summary>The model's button across every connected unit of it, ORed.</summary>
    public bool ModelButton(StickModel model, int button)
    {
        foreach (var stick in _sticks)
        {
            if (stick.Model == model && Button(stick, button))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The model's hat across every connected unit of it: the first unit pushing it.</summary>
    public HatDirection ModelHat(StickModel model, int hat)
    {
        foreach (var stick in _sticks)
        {
            if (stick.Model == model && Hat(stick, hat) is var direction && direction != HatDirection.None)
            {
                return direction;
            }
        }

        return HatDirection.None;
    }

    public void Dispose()
    {
        foreach (var stick in _sticks)
        {
            _native.Close(stick.Instance);
        }

        _sticks.Clear();
        _native.Dispose();
    }

    // Why the gap-filler leaves a listing out, as the skip log line prints it; null keeps it.
    private static string? SkipReason(StickListing listing, HashSet<StickModel> godot, bool godotReadsGamepads) =>
        godot.Contains(listing.Model) ? "Godot's pad roster has this model"
        : !godotReadsGamepads ? null
        : listing.Gamepad ? "SDL maps it as a gamepad, which Godot reads on this platform"
        : listing.Model.Vendor == ValveVendor ? "a Valve device (Steam Deck controls, Steam Controller or Steam Input)"
        : null;

    // ⚠ Do not read _native.Axis anywhere else. This is where a model's flipped axis is corrected,
    // so a read that bypasses it would disagree with every profile token.
    private float Read(Stick stick, int axis)
    {
        float value = Normalise(_native.Axis(stick.Instance, axis));
        return StickQuirks.Flips(stick.Model, axis) ? -value : value;
    }

    private bool Readable(Stick stick, out Stick live)
    {
        ArgumentNullException.ThrowIfNull(stick);
        live = stick;
        if (_inputBlocked())
        {
            return false;
        }

        foreach (var open in _sticks)
        {
            if (open.Instance == stick.Instance)
            {
                live = open;
                return true;
            }
        }

        return false;
    }

    // Ages every opened stick by one update and samples the rest of each that just came of age.
    // The update that opened a stick starts it at zero.
    private bool Settle()
    {
        bool sampled = false;
        foreach (var stick in _sticks)
        {
            if (_rest.ContainsKey(stick.Instance))
            {
                continue;
            }

            int age = _age.TryGetValue(stick.Instance, out int seen) ? seen + 1 : 0;
            _age[stick.Instance] = age;
            if (age < SettleUpdates)
            {
                continue;
            }

            var rest = new float[stick.Axes];
            var printed = new List<string>(stick.Axes);
            for (int a = 0; a < stick.Axes; a++)
            {
                rest[a] = Read(stick, a);
                printed.Add(Log.Format($"{rest[a]:0.00}"));
            }

            _rest[stick.Instance] = rest;
            sampled = true;
            Log.Info("core", $"stick at rest: \"{stick.Name}\" {stick.Model} axes=[{string.Join(" ", printed)}]{QuirksText(stick.Model)}");
        }

        return sampled;
    }

    private bool Reconcile(IReadOnlyList<StickListing> listed, IReadOnlyCollection<StickModel> godot)
    {
        var wanted = GapFill(listed, godot, _godotReadsGamepads);
        var godotSet = new HashSet<StickModel>(godot);
        var wantedIds = new HashSet<int>();
        foreach (var listing in wanted)
        {
            wantedIds.Add(listing.Instance);
        }

        bool changed = false;
        for (int i = _sticks.Count - 1; i >= 0; i--)
        {
            var stick = _sticks[i];
            if (!wantedIds.Contains(stick.Instance))
            {
                _native.Close(stick.Instance);
                _sticks.RemoveAt(i);
                _age.Remove(stick.Instance);
                _rest.Remove(stick.Instance);
                changed = true;
                Log.Info("core", $"stick disconnected: \"{stick.Name}\" {stick.Model}");
            }
        }

        var listedIds = new HashSet<int>();
        foreach (var listing in listed)
        {
            listedIds.Add(listing.Instance);
            if (!wantedIds.Contains(listing.Instance) && _skipped.Add(listing.Instance))
            {
                Log.Info("core", $"stick skipped: \"{listing.Name}\" {listing.Model}, {SkipReason(listing, godotSet, _godotReadsGamepads)}");
            }
        }

        _skipped.IntersectWith(listedIds);
        foreach (var listing in wanted)
        {
            if (_sticks.Exists(s => s.Instance == listing.Instance))
            {
                continue;
            }

            _skipped.Remove(listing.Instance);
            if (_native.Open(listing) is { } stick)
            {
                _sticks.Add(stick);
                changed = true;
                Log.Info("core", $"stick connected: {Describe(stick)}");
            }
            else
            {
                Log.Warn("core", $"stick open failed: \"{listing.Name}\" {listing.Model}: {_native.LastError}");
            }
        }

        if (changed)
        {
            LogRoster();
        }

        return changed;
    }

    // The whole roster after a change, so one line answers what a player had connected, and which
    // models read as one because identical units merge.
    private void LogRoster()
    {
        var entries = new List<string>(_sticks.Count);
        var units = new Dictionary<StickModel, int>();
        for (int i = 0; i < _sticks.Count; i++)
        {
            entries.Add(Log.Format($"[{i}] {Describe(_sticks[i])}"));
            units[_sticks[i].Model] = units.GetValueOrDefault(_sticks[i].Model) + 1;
        }

        Log.Info("core", $"stick roster (SDL {_native.Version}): {(entries.Count > 0 ? string.Join(", ", entries) : "none connected")}");
        foreach (var (model, count) in units)
        {
            if (count > 1)
            {
                Log.Info("core", $"stick model {model}: {count} identical units read as one device");
            }
        }
    }
}
