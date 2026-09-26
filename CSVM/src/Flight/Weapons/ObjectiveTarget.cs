using System;
using System.Collections.Generic;

namespace CSVM.Flight.Weapons;

/// <summary>One argument of a target directive: <c>ADD_/REMOVE_OBJECTIVE_TARGET</c>,
/// <c>ADD_/REMOVE_OTHER_TARGET</c> or <c>SET_HELP_LABEL</c>. It is a bare name, matched anywhere
/// in the world, or an authored <c>[parent, child, ...]</c> path. Each later name of a path is
/// found under the node before it. Every store and every site is keyed by
/// <see cref="Key"/>, the segments joined with <c>/</c> (a bare name's key is the name itself).
/// ⚠ A path is ONE target, never two names. C1/M04's <c>[[piratezep, rock_zeppelin]]</c> names
/// the hull's own <c>rock_zeppelin</c>; read as two names, it lights the hull's root and a same-named ground node too.</summary>
public readonly struct ObjectiveTarget
{
    /// <summary>Builds a target over a non-empty path.</summary>
    public ObjectiveTarget(IReadOnlyList<string> path)
    {
        Path = path;
        Key = string.Join("/", path);
    }

    /// <summary>The authored names, outermost first; one entry for a bare name.</summary>
    public IReadOnlyList<string> Path { get; }

    /// <summary>The identity string: the path joined with <c>/</c>.</summary>
    public string Key { get; }

    /// <summary>The name of the node the target lands on, the last segment. What
    /// <c>targets.zrd</c> is looked up by.</summary>
    public string Node => Path[^1];

    /// <summary>Whether the target is a path rather than a bare name.</summary>
    public bool Scoped => Path.Count > 1;

    /// <summary>The target a key denotes, inverse of <see cref="Key"/>.</summary>
    public static ObjectiveTarget Parse(string key) => new(key.Split('/'));

    /// <summary>Whether this target's key is the given one, case-insensitively.</summary>
    public bool Is(string key) => string.Equals(Key, key, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override string ToString() => Key;
}
