using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace CSVM.Mech3;

/// <summary>
/// Reader for mech3ax zrdr extractions (the game's "reader" config files → JSON).
/// Reader data is nested lists of strings and numbers. By convention most lists
/// alternate key/value pairs: [key, [values...], key, [values...], ...], wrap those
/// in <see cref="ZrdrDict"/>. vehicle.json defs additionally inherit via 'kind_of'.
/// </summary>
public static class Zrdr
{
    /// <summary>Loads one reader file (e.g. "vehicle.json") from a zrdr ZIP or a directory of JSON files.</summary>
    public static List<object?> LoadFile(string zrdrPath, string fileName)
    {
        using var doc = JsonDocument.Parse(ReadEntry(zrdrPath, fileName));
        return Convert(doc.RootElement) as List<object?>
            ?? throw new InvalidDataException($"'{fileName}' is not a reader list");
    }

    /// <summary>Loads one reader file, treating an EMPTY reader as an empty list rather than as an
    /// error. mech3ax writes a reader with no entries as the four bytes <c>null</c>, which
    /// <see cref="LoadFile"/> rejects as "not a reader list", indistinguishable there from a
    /// corrupt file. Two shipped `templates.zrd` are in that state (C1C, C2B), and for a caller
    /// that must tell "this chapter authors nothing" apart from "this file is broken", that
    /// difference is the whole point. Still throws when the entry is absent.</summary>
    public static List<object?> LoadFileOrEmpty(string zrdrPath, string fileName)
    {
        using var doc = JsonDocument.Parse(ReadEntry(zrdrPath, fileName));
        if (doc.RootElement.ValueKind == JsonValueKind.Null)
            return new List<object?>();
        return Convert(doc.RootElement) as List<object?>
            ?? throw new InvalidDataException($"'{fileName}' is not a reader list");
    }

    /// <summary>
    /// Enumerates every reader file in a zrdr ZIP or directory whose raw JSON contains
    /// <paramref name="contentFilter"/> (a cheap pre-parse sniff, reader archives hold
    /// hundreds of files and most callers want one family, e.g. "ANIMATION_DEFINITIONS"),
    /// yielding (fileName, parsed root list). Unparseable files are skipped.
    /// </summary>
    public static IEnumerable<(string Name, List<object?> Root)> LoadMatchingFiles(
        string zrdrPath, string contentFilter)
    {
        static (string, List<object?>)? TryParse(string name, byte[] bytes, string filter)
        {
            // UTF-8 substring sniff: the filter keys are plain ASCII JSON strings.
            if (System.Text.Encoding.UTF8.GetString(bytes).IndexOf(filter, StringComparison.Ordinal) < 0)
                return null;
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                return Convert(doc.RootElement) is List<object?> root ? (name, root) : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (Directory.Exists(zrdrPath))
        {
            foreach (var file in Directory.EnumerateFiles(zrdrPath, "*.json"))
                if (TryParse(Path.GetFileName(file), File.ReadAllBytes(file), contentFilter) is { } hit)
                    yield return hit;
        }
        else if (File.Exists(zrdrPath))
        {
            using var zip = ZipFile.OpenRead(zrdrPath);
            foreach (var entry in zip.Entries)
            {
                if (!entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                if (TryParse(entry.Name, ms.ToArray(), contentFilter) is { } hit)
                    yield return hit;
            }
        }
    }

    /// <summary>Enumerates reader files by NAME (dir or zip), the counterpart of
    /// <see cref="LoadMatchingFiles"/> for families keyed by filename rather than content
    /// (the ne0NNNNN patrol nets are pure number arrays with nothing to sniff for). The
    /// predicate sees the stored file name; unparseable matches are skipped.</summary>
    public static IEnumerable<(string Name, List<object?> Root)> LoadFilesNamed(
        string zrdrPath, Func<string, bool> namePredicate)
    {
        static (string, List<object?>)? TryParse(string name, byte[] bytes)
        {
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                return Convert(doc.RootElement) is List<object?> root ? (name, root) : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (Directory.Exists(zrdrPath))
        {
            foreach (var file in Directory.EnumerateFiles(zrdrPath, "*.json"))
            {
                var name = Path.GetFileName(file);
                if (namePredicate(name) && TryParse(name, File.ReadAllBytes(file)) is { } hit)
                    yield return hit;
            }
        }
        else if (File.Exists(zrdrPath))
        {
            using var zip = ZipFile.OpenRead(zrdrPath);
            foreach (var entry in zip.Entries)
            {
                if (!namePredicate(entry.Name))
                    continue;
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                if (TryParse(entry.Name, ms.ToArray()) is { } hit)
                    yield return hit;
            }
        }
    }

    // The names a requested reader file may be stored under. mech3ax v0.6.1
    // replaced the source extension ("vehicle.zrd" → "vehicle.json"); the fork appends
    // instead ("vehicle.zrd.json"), keeping the original extension visible. Content is
    // identical, all 222 readers verified semantically equal across the two, so only
    // the lookup needs to accept both.
    private static byte[] ReadEntry(string zrdrPath, string fileName)
    {
        byte[]? bytes = null;
        if (Directory.Exists(zrdrPath))
        {
            foreach (var candidate in CandidateNames(fileName))
            {
                var p = Path.Combine(zrdrPath, candidate);
                if (File.Exists(p))
                {
                    bytes = File.ReadAllBytes(p);
                    break;
                }
            }
        }
        else
        {
            using var zip = ZipFile.OpenRead(zrdrPath);
            foreach (var candidate in CandidateNames(fileName))
            {
                if (zip.GetEntry(candidate) is not { } entry)
                    continue;
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                bytes = ms.ToArray();
                break;
            }
        }
        return bytes ?? throw new FileNotFoundException($"'{fileName}' missing from '{zrdrPath}'");
    }

    private static IEnumerable<string> CandidateNames(string fileName)
    {
        yield return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            yield return stem + ".zrd.json";
    }

    private static object? Convert(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Array => ConvertList(e),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetSingle(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static List<object?> ConvertList(JsonElement e)
    {
        var list = new List<object?>(e.GetArrayLength());
        foreach (var item in e.EnumerateArray())
            list.Add(Convert(item));
        return list;
    }
}

/// <summary>
/// Key→value view over an alternating reader list. A key is a string followed by a
/// list value; a string followed by another string (or the list end) is a bare flag.
/// </summary>
public sealed class ZrdrDict
{
    private readonly Dictionary<string, List<object?>> _props = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every key present, in no particular order (duplicates already collapsed). Lets a
    /// typed reader assert it consumed every key its source file carries, see
    /// <c>Flight.Weapons.WeaponDefs</c>'s unhandled-key check.</summary>
    public IReadOnlyCollection<string> Keys => _props.Keys;

    public static ZrdrDict FromAlternating(List<object?> list)
    {
        var d = new ZrdrDict();
        int i = 0;
        while (i < list.Count)
        {
            if (list[i] is string key)
            {
                if (i + 1 < list.Count && list[i + 1] is List<object?> value)
                {
                    d._props[key] = value;
                    i += 2;
                }
                else
                {
                    d._props[key] = new List<object?>(); // bare flag, e.g. 'is_autogyro'
                    i += 1;
                }
            }
            else
            {
                i += 1; // stray value, tolerate
            }
        }
        return d;
    }

    public bool Has(string key) => _props.ContainsKey(key);

    public List<object?>? List(string key) => _props.TryGetValue(key, out var v) ? v : null;

    public bool TryFloat(string key, out float value, int index = 0)
    {
        value = 0f;
        if (_props.TryGetValue(key, out var v) && index < v.Count && v[index] is float f)
        {
            value = f;
            return true;
        }
        return false;
    }

    public float Float(string key, float fallback = 0f, int index = 0) =>
        TryFloat(key, out var f, index) ? f : fallback;

    public string? Str(string key, int index = 0) =>
        _props.TryGetValue(key, out var v) && index < v.Count ? v[index] as string : null;

    /// <summary>Interprets the value list of <paramref name="key"/> as a nested alternating dict.</summary>
    public ZrdrDict? Dict(string key) =>
        _props.TryGetValue(key, out var v) ? FromAlternating(v) : null;
}
