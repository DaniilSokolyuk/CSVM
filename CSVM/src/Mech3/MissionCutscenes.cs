using System;
using System.Collections.Generic;
using System.IO;

namespace CSVM.Mech3;

/// <summary>
/// The animation definitions a mission loads out of its own <c>cutscenes\</c> directory. That
/// directory IS the authored classification: <c>mis_anim.zrd</c>'s
/// <c>ANIMATION_DEFINITION_FILE</c> list names each reader file by path, and the mid-mission
/// choreography sits under <c>cutscenes\</c> while the ambient world furniture (the zeppelin
/// wiring, the walkers, the ladders) does not. Nine story missions ship one; every other mission
/// and every Instant Action stub resolves to nothing.
/// Decode: docs/formats/anim-definitions/cutscenes.md.
/// </summary>
public static class MissionCutscenes
{
    /// <summary>The mission reader carrying the <c>ANIMATION_DEFINITION_FILE</c> list.</summary>
    public const string FileName = "mis_anim.json";

    /// <summary>The path segment that marks a cutscene definition file.</summary>
    public const string Directory = "cutscenes";

    private const string FileKey = "ANIMATION_DEFINITION_FILE";

    /// <summary>Every <c>ANIMATION_NAME</c> the mission's cutscene reader files define, in load
    /// order, de-duplicated. A mission with no <c>cutscenes\</c> entry, and one whose
    /// <c>mis_anim.zrd</c> is missing or unreadable, resolves to nothing, so a caller can treat
    /// "this mission hosts no cutscene of its own" and "this is not a story mission" alike.</summary>
    public static List<string> AnimNames(string missionZrdrPath)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files(missionZrdrPath))
        {
            foreach (var def in AnimDefs.LoadFileDefs(missionZrdrPath, file))
            {
                if (def.AnimName is { Length: > 0 } name && seen.Add(name))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    /// <summary>Every <c>ANIMATION_DEFINITION_FILE</c> value in a reader tree, wherever the
    /// nesting puts it, as authored (install-relative source paths). Shared with
    /// <see cref="AnimProgram"/>'s shared-scope file gate, which reads the same lists.</summary>
    internal static IEnumerable<string> ListedPaths(List<object?> node)
    {
        for (int i = 0; i < node.Count; i++)
        {
            if (node[i] is string key && key.Equals(FileKey, StringComparison.OrdinalIgnoreCase)
                && i + 1 < node.Count && node[i + 1] is List<object?> { Count: > 0 } value
                && value[0] is string path)
            {
                yield return path;
            }

            if (node[i] is List<object?> inner)
            {
                foreach (var found in ListedPaths(inner))
                {
                    yield return found;
                }
            }
        }
    }

    /// <summary>The <c>cutscenes\*.zrd</c> entries of an <c>ANIMATION_DEFINITION_FILE</c> list,
    /// as the names the extraction stores them under. The extraction flattens a mission's readers
    /// into one directory, so only the leaf survives. ⚠ Split through <see cref="GamePath"/>: the
    /// authored paths are Windows paths on every host.</summary>
    internal static List<string> StoredNames(IEnumerable<string> listedPaths)
    {
        var files = new List<string>();
        foreach (var path in listedPaths)
        {
            if (!GamePath.HasFolder(path, Directory))
            {
                continue;
            }

            var leaf = GamePath.FileName(path);
            if (leaf.Length > 0)
            {
                files.Add(Path.ChangeExtension(leaf, ".json"));
            }
        }

        return files;
    }

    private static List<string> Files(string missionZrdrPath)
    {
        List<object?> root;
        try
        {
            root = Zrdr.LoadFileOrEmpty(missionZrdrPath, FileName);
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            return new List<string>();
        }

        return StoredNames(ListedPaths(root));
    }
}
