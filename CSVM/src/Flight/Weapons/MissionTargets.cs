using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Mech3;

namespace CSVM.Flight.Weapons;

/// <summary>The display keys a mission's <c>targets.json</c> attaches to one world node: a
/// <c>description</c> (the target's own name, e.g. <c>MSG_OBJ_TRAINTUNNEL_M</c>), a
/// <c>category_label</c> (its type, e.g. <c>MSG_OBJ_DZ</c> = "Danger Zone"), and a
/// <c>help_label</c> (the action, e.g. <c>MSG_OBJ_FLYTHROUGH</c> = "Fly Through"). Any may be
/// absent (a plain reference point has only a description + help). Resolve the keys through
/// <see cref="Messages"/>. <c>Objective</c> and <c>OtherTarget</c> are the file's own valueless
/// <c>objective</c> / <c>other_target</c> keys: the marker flags a mission STARTS with, which
/// <c>objectives.zrd</c>'s <c>ADD_/REMOVE_OBJECTIVE_TARGET</c> then edits.
/// ⚠ Read <c>Objective</c> for the starting set. C3/M01 only ever REMOVES its three sites, so a
/// set built from the script's adds alone is empty for the whole mission.</summary>
public readonly record struct MissionTarget(string? Description, string? CategoryLabel,
    string? HelpLabel, bool Objective = false, bool OtherTarget = false);

/// <summary>
/// Loads a mission's <c>targets.json</c>, the map from a target KEY to its objective display
/// strings. The file is a list of target entries, each a list of <c>[key, value]</c> pairs (NOT
/// the flat-alternating reader form): <c>description</c>, a <c>nodes</c> list of the target(s)
/// the entry labels, an optional <c>category_label</c>, and a <c>help_label</c>. A target is a
/// bare node name or a nested <c>[parent, child, ...]</c> path, keyed <c>parent/child</c> the
/// way <c>objectives.zrd</c>'s target directives are, so the two tables meet on one key.
/// Generic across mission types, the stunt mode reads the <c>dzN</c> entries, but
/// dogfight/zeppelin targets parse identically.
/// </summary>
public sealed class MissionTargets
{
    private readonly Dictionary<string, MissionTarget> _byNode = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byNode.Count;

    /// <summary>Every entry, target key to its display keys, what a marker HUD walks to find the
    /// targets the mission flags <c>objective</c> before its script has edited anything.</summary>
    public IReadOnlyDictionary<string, MissionTarget> ByNode => _byNode;

    /// <summary>Loads targets.json from a mission's zrdr (zip or unpacked dir). Missing file
    /// → an empty set (a mission may have none); malformed entries are skipped.</summary>
    public static MissionTargets Load(string missionZrdrPath) =>
        TryLoad(missionZrdrPath) ?? new MissionTargets();

    /// <summary>The original's reader search path: the mission's own zrdr first, then the
    /// chapter's, one file wins whole (<c>init.gw</c>'s <c>RdrAddPath</c> chain, read by the
    /// reader opener the targets loader calls).
    /// ⚠ Do not read the mission scope alone. C1C/M01 ships no targets.zrd of its own and takes
    /// the chapter's, which is where every one of its objective labels lives.</summary>
    public static MissionTargets Load(string missionZrdrPath, string chapterZrdrPath) =>
        TryLoad(missionZrdrPath) ?? TryLoad(chapterZrdrPath) ?? new MissionTargets();

    /// <summary>The display keys for a target key, or an all-null <see cref="MissionTarget"/>
    /// if the key has no targets.json entry.</summary>
    public MissionTarget For(string nodeName) =>
        _byNode.TryGetValue(nodeName, out var t) ? t : default;

    private static MissionTargets? TryLoad(string zrdrPath)
    {
        List<object?> root;
        try
        {
            root = Zrdr.LoadFile(zrdrPath, "targets.json");
        }
        catch (IOException)
        {
            return null; // no targets.json in this scope
        }

        var targets = new MissionTargets();
        foreach (var entryObj in root)
        {
            if (entryObj is not List<object?> entry)
                continue;
            string? description = null, category = null, help = null;
            bool objective = false, other = false;
            List<object?>? nodes = null;
            foreach (var pairObj in entry)
            {
                if (pairObj is not List<object?> { Count: > 0 } pair || pair[0] is not string key)
                    continue;
                switch (key.ToLowerInvariant())
                {
                    case "description": description = Value(pair); break;
                    case "category_label": category = Value(pair); break;
                    case "help_label": help = Value(pair); break;
                    case "objective": objective = true; break;
                    case "other_target": other = true; break;
                    case "nodes": nodes = pair.Count > 1 ? pair[1] as List<object?> : null; break;
                }
            }
            if (nodes == null)
                continue;
            var info = new MissionTarget(description, category, help, objective, other);
            foreach (var n in nodes)
                if (KeyOf(n) is { } targetKey)
                    targets._byNode[targetKey] = info;
        }
        return targets;
    }

    // A nested list is one path, outer name first; anything but strings in it is no target.
    private static string? KeyOf(object? node)
    {
        if (node is string name)
            return name;
        if (node is not List<object?> { Count: > 0 } path)
            return null;
        var names = new string[path.Count];
        for (int i = 0; i < path.Count; i++)
        {
            if (path[i] is not string segment)
                return null;
            names[i] = segment;
        }
        return string.Join("/", names);
    }

    private static string? Value(List<object?> pair) => pair.Count > 1 ? pair[1] as string : null;
}
