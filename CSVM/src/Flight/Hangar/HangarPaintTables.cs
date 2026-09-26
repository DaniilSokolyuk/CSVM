using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CSVM.Flight.Hangar;

/// <summary>One row of the original's swatch table at <c>0x0061dd48</c>: a base colour, the ramp
/// of shade variants a dark-to-light stepper walks, and the variant a colour pick resets the
/// slot's shade to (docs/formats/paint.md, "The swatch table and the pattern defaults").</summary>
public sealed class HangarSwatch
{
    /// <summary>The row's base colour, the chip the colour dropdown draws.</summary>
    public PaintColour Base { get; init; }

    /// <summary>The variant a colour pick resets the slot's shade to.</summary>
    public int DefaultShade { get; init; }

    /// <summary>The shade ramp, darkest first. Row lengths differ (eight to ten).</summary>
    public IReadOnlyList<PaintColour> Shades { get; init; } = Array.Empty<PaintColour>();
}

/// <summary>One entry of the original's pattern table at <c>0x0061daf0</c>: which airframes may
/// wear the pattern, and the six colour/shade indices selecting it copies over the record's own
/// (callback 2238's SET). Entries 10 and 12 are de-shifted in the transcription.</summary>
public sealed class HangarPatternEntry
{
    /// <summary>The pattern's own name, which is also its archive folder.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Airframe availability: bit i set means airframe i may wear this pattern.</summary>
    public int AirframeMask { get; init; }

    /// <summary>The three default colour indices, one per paint slot.</summary>
    public IReadOnlyList<int> Colours { get; init; } = Array.Empty<int>();

    /// <summary>The three default shade indices, one per paint slot.</summary>
    public IReadOnlyList<int> Shades { get; init; } = Array.Empty<int>();
}

/// <summary>
/// The paint screen's two decoded tables and the decal name set, as CSVM's own JSON: the 27-row
/// swatch table (<c>CSVM/data/hangar_swatches.json</c>) and the 14-entry pattern table plus the
/// 50 decal names (<c>CSVM/data/hangar_patterns.json</c>). Engine-free and pure, so the whole
/// colour model resolves without a session: a paint pick is an index pair, never free RGB, and
/// <see cref="Resolve"/> is <c>FUN_004067c0</c>, the original's own resolver.
/// </summary>
public sealed class HangarPaintTables
{
    /// <summary>Paint slots per plane.</summary>
    public const int Slots = 3;

    /// <summary>Pattern indices run 0-13.</summary>
    public const int PatternCount = 14;

    /// <summary>The decal set is the gapless 00-49 texture series.</summary>
    public const int DecalCount = 50;

    /// <summary>Tiles across one row of the decal picker. The picked decal is the grid's own
    /// <c>row * 5 + column</c> and the list's row count is <c>ceil(50 / 5)</c>, both decoded
    /// (docs/org/hangar.md, callbacks 2239 and 2240).</summary>
    public const int DecalGridColumns = 5;

    /// <summary>The tables as a missing file leaves them: no swatches, no patterns, no names.</summary>
    public static readonly HangarPaintTables Empty = new();

    private static HangarPaintTables? _default;

    /// <summary>The swatch table's shipped location. A res:// path because in an exported build
    /// the file lives in the pck, where GlobalizePath and System.IO cannot reach it.</summary>
    public static string SwatchResPath => "res://data/hangar_swatches.json";

    /// <summary>The pattern table and decal names, shipped alongside the swatches.</summary>
    public static string PatternResPath => "res://data/hangar_patterns.json";

    /// <summary>The process-wide tables, loaded once. The derived paint colours on every
    /// <see cref="CustomPlaneDef"/> resolve through this, so it must never throw: an unreadable
    /// or absent pair of files leaves <see cref="Empty"/> and every colour resolves black.</summary>
    public static HangarPaintTables Default => _default ??= LoadDefault();

    /// <summary>The 27 swatch rows, in table order.</summary>
    public IReadOnlyList<HangarSwatch> Swatches { get; private set; } = Array.Empty<HangarSwatch>();

    /// <summary>The 14 pattern entries, in table order (index 0-13 is the record's own).</summary>
    public IReadOnlyList<HangarPatternEntry> Patterns { get; private set; } = Array.Empty<HangarPatternEntry>();

    /// <summary>The 50 decal texture names, indexed by their own numeric prefix.</summary>
    public IReadOnlyList<string> Decals { get; private set; } = Array.Empty<string>();

    /// <summary>Whether no table loaded, the missing-data state every hangar source has.</summary>
    public bool IsEmpty => Swatches.Count == 0;

    /// <summary>Loads both files, defaulting to their shipped res:// locations. Throws on JSON
    /// that will not parse; an absent file simply contributes nothing.</summary>
    public static HangarPaintTables Load(string? swatchPath = null, string? patternPath = null)
    {
        var swatches = ReadJson(swatchPath ?? SwatchResPath);
        var patterns = ReadJson(patternPath ?? PatternResPath);
        return Parse(swatches, patterns);
    }

    /// <summary>Both files' bytes as tables, with no IO and no engine call. Either may be null,
    /// which loads that half empty.</summary>
    public static HangarPaintTables Parse(byte[]? swatchJson, byte[]? patternJson)
    {
        var tables = new HangarPaintTables();
        if (swatchJson is { Length: > 0 })
        {
            using var doc = JsonDocument.Parse(swatchJson);
            tables.Swatches = ReadSwatches(doc.RootElement);
        }

        if (patternJson is { Length: > 0 })
        {
            using var doc = JsonDocument.Parse(patternJson);
            tables.Patterns = ReadPatterns(doc.RootElement);
            tables.Decals = ReadDecals(doc.RootElement);
        }

        return tables;
    }

    /// <summary>A (colour, shade) pair as the RGB the original resolves it to: the swatch row's
    /// shade variant, clamped into the row's own ramp. An index outside the table is black, which
    /// is what a plane with no loaded tables paints as.</summary>
    public PaintColour Resolve(int colour, int shade)
    {
        if (colour < 0 || colour >= Swatches.Count)
        {
            return default;
        }

        var row = Swatches[colour];
        return row.Shades.Count == 0 ? row.Base : row.Shades[Math.Clamp(shade, 0, row.Shades.Count - 1)];
    }

    /// <summary>The (colour, shade) pair whose resolved RGB sits closest to <paramref name="rgb"/>,
    /// by squared channel distance. The one place a free RGB enters the index model: a version-1
    /// store file held triples, and this is how they land on an authored swatch.</summary>
    public (int Colour, int Shade) Nearest(PaintColour rgb)
    {
        (int Colour, int Shade) best = (0, 0);
        int closest = int.MaxValue;
        for (int colour = 0; colour < Swatches.Count; colour++)
        {
            var row = Swatches[colour];
            for (int shade = 0; shade < row.Shades.Count; shade++)
            {
                var candidate = row.Shades[shade];
                int dr = candidate.R - rgb.R, dg = candidate.G - rgb.G, db = candidate.B - rgb.B;
                int distance = (dr * dr) + (dg * dg) + (db * db);
                if (distance < closest)
                {
                    closest = distance;
                    best = (colour, shade);
                }
            }
        }

        return best;
    }

    /// <summary>The shade a colour pick resets the slot to, the swatch row's own default.</summary>
    public int DefaultShadeFor(int colour) =>
        colour >= 0 && colour < Swatches.Count ? Swatches[colour].DefaultShade : 0;

    /// <summary>How many shades a colour offers; 0 outside the table.</summary>
    public int ShadeCount(int colour) => colour >= 0 && colour < Swatches.Count ? Swatches[colour].Shades.Count : 0;

    /// <summary>Whether an airframe may wear a pattern, the entry's availability mask. With no
    /// table loaded every pattern is offered rather than none, so the screen still works.</summary>
    public bool Available(int pattern, int airframe)
    {
        if (pattern < 0 || pattern >= Patterns.Count)
        {
            return Patterns.Count == 0 && pattern >= 0 && pattern < PatternCount;
        }

        return (Patterns[pattern].AirframeMask & (1 << airframe)) != 0;
    }

    /// <summary>The pattern's internal name (its archive folder), or "" outside the table.</summary>
    public string PatternName(int pattern) =>
        pattern >= 0 && pattern < Patterns.Count ? Patterns[pattern].Name : string.Empty;

    /// <summary>The decal's texture name, or "" outside the set.</summary>
    public string DecalName(int decal) => decal >= 0 && decal < Decals.Count ? Decals[decal] : string.Empty;

    /// <summary>The pattern entry's six defaults, or null when the table does not carry it.</summary>
    public HangarPatternEntry? PatternEntry(int pattern) =>
        pattern >= 0 && pattern < Patterns.Count ? Patterns[pattern] : null;

    // Bytes of a file that may be a res:// path (the shipped location, readable only through
    // Godot's own FileAccess inside a pck) or a plain disk path (unit tests, tools).
    private static byte[]? ReadJson(string path)
    {
        bool viaGodot = path.StartsWith("res://", StringComparison.Ordinal);
        if (viaGodot)
        {
            return Godot.FileAccess.FileExists(path) ? Godot.FileAccess.GetFileAsBytes(path) : null;
        }

        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    // The shipped files, disk first. An editor run and the xunit host both find the repo's own
    // copies by walking up from the build output, which keeps the engine out of the default
    // model; only an exported build (where the files are inside the pck) reaches for res://.
    private static HangarPaintTables LoadDefault()
    {
        try
        {
            return Load(
                OnDisk("hangar_swatches.json") ?? SwatchResPath,
                OnDisk("hangar_patterns.json") ?? PatternResPath);
        }
        catch (Exception)
        {
            return Empty;
        }
    }

    private static string? OnDisk(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            foreach (var relative in new[] { Path.Combine("data", file), Path.Combine("CSVM", "data", file) })
            {
                string candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static List<HangarSwatch> ReadSwatches(JsonElement root)
    {
        var rows = new List<HangarSwatch>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var row in root.EnumerateArray())
        {
            var shades = new List<PaintColour>();
            if (row.TryGetProperty("shades", out var ramp) && ramp.ValueKind == JsonValueKind.Array)
            {
                foreach (var shade in ramp.EnumerateArray())
                {
                    shades.Add(Triple(shade));
                }
            }

            rows.Add(new HangarSwatch
            {
                Base = row.TryGetProperty("base", out var b) ? Triple(b) : default,
                DefaultShade = row.TryGetProperty("default", out var d) && d.TryGetInt32(out int at) ? at : 0,
                Shades = shades,
            });
        }

        return rows;
    }

    private static List<HangarPatternEntry> ReadPatterns(JsonElement root)
    {
        var entries = new List<HangarPatternEntry>();
        if (!root.TryGetProperty("patterns", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return entries;
        }

        foreach (var entry in list.EnumerateArray())
        {
            entries.Add(new HangarPatternEntry
            {
                Name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                AirframeMask = entry.TryGetProperty("mask", out var m) && m.TryGetInt32(out int mask) ? mask : 0,
                Colours = Ints(entry, "colours"),
                Shades = Ints(entry, "shades"),
            });
        }

        return entries;
    }

    private static List<string> ReadDecals(JsonElement root)
    {
        var names = new List<string>();
        if (root.TryGetProperty("decals", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in list.EnumerateArray())
            {
                names.Add(name.GetString() ?? string.Empty);
            }
        }

        return names;
    }

    private static List<int> Ints(JsonElement obj, string key)
    {
        var values = new List<int>();
        if (obj.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in list.EnumerateArray())
            {
                values.Add(value.TryGetInt32(out int v) ? v : 0);
            }
        }

        return values;
    }

    private static PaintColour Triple(JsonElement rgb)
    {
        if (rgb.ValueKind != JsonValueKind.Array || rgb.GetArrayLength() != 3)
        {
            return default;
        }

        byte[] channels = new byte[3];
        int i = 0;
        foreach (var channel in rgb.EnumerateArray())
        {
            channels[i++] = (byte)Math.Clamp(channel.TryGetInt32(out int v) ? v : 0, 0, 255);
        }

        return new PaintColour(channels[0], channels[1], channels[2]);
    }
}
