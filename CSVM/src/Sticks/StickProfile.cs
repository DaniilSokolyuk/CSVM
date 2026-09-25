using System;
using System.Collections.Generic;
using CSVM.Bindings;

namespace CSVM.Sticks;

/// <summary>Where a profile file was read from. Shipped files are read-only; a user file overrides
/// a shipped one for the same model and companions.</summary>
public enum StickProfileSource
{
    Shipped,
    User,
}

/// <summary>One profile file as the resolver ranks it: its source, its file name and what it holds.
/// The name is the last tie-break and the file a user save rewrites.</summary>
public sealed record StickProfileFile(StickProfileSource Source, string FileName, StickProfile Profile);

/// <summary>
/// One stick model's bindings in one layout: the rows a profile file holds, per context, every one
/// on <see cref="StickModel.Device"/>. A profile may name companion models and applies only while
/// all of them are connected. That is how one model keeps a solo and a HOSAS layout side by side
/// (<see cref="StickProfileResolver"/>). The ignore flag marks a device that yields no bindings, and
/// the name is the short label ("R", "L") screens print.
/// Rows a file held that this build could not read ride along untouched, so a re-save keeps them.
/// The file format is in <c>docs/org/input.md</c>, "The CSVM stick profile files".
/// </summary>
public sealed class StickProfile
{
    private readonly Dictionary<InputContext, ActionMap> _maps = new();

    /// <summary>An empty profile for <paramref name="model"/>. Companions are kept distinct and in
    /// model order. The model itself is dropped from them: its own units always satisfy it, so it
    /// would only inflate how specific the file ranks.</summary>
    public StickProfile(StickModel model, IEnumerable<StickModel>? companions = null, string? name = null, bool ignore = false)
    {
        Model = model;
        var set = new SortedSet<StickModel>(Comparer<StickModel>.Create(CompareModels));
        foreach (var companion in companions ?? Array.Empty<StickModel>())
        {
            if (companion != model)
            {
                set.Add(companion);
            }
        }

        Companions = new List<StickModel>(set);
        Name = name ?? string.Empty;
        Ignore = ignore;
        foreach (var context in Enum.GetValues<InputContext>())
        {
            _maps[context] = new ActionMap();
        }
    }

    public StickModel Model { get; }

    /// <summary>The models that must all be connected for this profile to apply, in model order.
    /// </summary>
    public IReadOnlyList<StickModel> Companions { get; }

    /// <summary>The short display name, empty when the file gives none.</summary>
    public string Name { get; set; }

    /// <summary>Whether the model yields no bindings while this profile is active. It is for a device
    /// that enumerates as a joystick but is no flight stick, such as a gaming keypad.</summary>
    public bool Ignore { get; set; }

    /// <summary>Rows this build could not read, as raw JSON per context and action name. That is an
    /// unknown context or action, or a token it cannot parse. The writer puts each back unless the
    /// action now holds bindings.</summary>
    public List<(string Context, string Action, string Json)> Unread { get; } = new();

    /// <summary>Vendor, then product: the order companions are kept and file names list them in.
    /// </summary>
    public static int CompareModels(StickModel left, StickModel right) =>
        left.Vendor != right.Vendor ? left.Vendor.CompareTo(right.Vendor) : left.Product.CompareTo(right.Product);

    /// <summary>That context's rows. A full axis sits on both actions of its pair, as in any map.
    /// </summary>
    public ActionMap Map(InputContext context) => _maps[context];

    /// <summary>Whether <paramref name="other"/> is for the same model and the same companions, the
    /// pair a user file must match to override a shipped one.</summary>
    public bool SameLayout(StickProfile other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Model != Model || other.Companions.Count != Companions.Count)
        {
            return false;
        }

        for (int i = 0; i < Companions.Count; i++)
        {
            if (Companions[i] != other.Companions[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A copy carrying the same model, companions, name, flag, unread rows and bindings,
    /// for an edit that may be cancelled or saved elsewhere.</summary>
    public StickProfile Clone()
    {
        var copy = new StickProfile(Model, Companions, Name, Ignore);
        copy.Unread.AddRange(Unread);
        foreach (var pair in _maps)
        {
            copy._maps[pair.Key].Fill(pair.Value);
        }

        return copy;
    }
}
