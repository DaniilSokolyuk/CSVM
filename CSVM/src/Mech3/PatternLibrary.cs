using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Utils;
using Godot;

namespace CSVM.Mech3;

/// <summary>
/// One aircraft skin's paint data as the original ships it: a `.BM` from the UI resource
/// archive (`ASSETS/GRAPHICS/&lt;PATTERN&gt;/&lt;SKIN&gt;.BM`, see <c>docs/formats/rof.md</c>).
///
/// Four planes over the same w×h grid: a near-greyscale shading map, three 8-bit per-pixel
/// weight masks (one per paint colour slot, summing to 255), and a 32bpp overlay for the
/// pattern's decorative artwork. The masks are the engine's region table: they say which
/// texel belongs to which paint slot.
/// </summary>
public sealed class PaintBitmap
{
    public int Width;
    public int Height;
    public byte[] Shading = Array.Empty<byte>(); // 3n, RGB interleaved
    public byte[] Slot1 = Array.Empty<byte>();   // n, weight for paint colour 1
    public byte[] Slot2 = Array.Empty<byte>();
    public byte[] Slot3 = Array.Empty<byte>();
    public byte[]? Overlay;                      // 4n RGBA, null when the file carries none

    /// <summary>True when the overlay has any non-zero texel, patterns leave it empty for
    /// parts they do not decorate, so this skips the composite entirely.</summary>
    public bool HasOverlay;

    /// <summary>Reads one `.BM`. Header is u16 height then u16 width (height first, settled
    /// against the game's own textures of the same name, see rof.md); payload is
    /// width*height*10 bytes of planes. Null on anything malformed: paint is cosmetic and
    /// must never take a build down.</summary>
    public static PaintBitmap? Load(string path)
    {
        byte[] d;
        try
        {
            d = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        if (d.Length < 4)
            return null;
        int h = d[0] | (d[1] << 8);
        int w = d[2] | (d[3] << 8);
        long n = (long)w * h;
        if (w <= 0 || h <= 0 || d.Length < 4 + 6 * n)
            return null;

        var bm = new PaintBitmap
        {
            Width = w,
            Height = h,
            Shading = new byte[3 * n],
            Slot1 = new byte[n],
            Slot2 = new byte[n],
            Slot3 = new byte[n],
        };
        Buffer.BlockCopy(d, 4, bm.Shading, 0, (int)(3 * n));
        Buffer.BlockCopy(d, (int)(4 + 3 * n), bm.Slot1, 0, (int)n);
        Buffer.BlockCopy(d, (int)(4 + 4 * n), bm.Slot2, 0, (int)n);
        Buffer.BlockCopy(d, (int)(4 + 5 * n), bm.Slot3, 0, (int)n);
        if (d.Length >= 4 + 10 * n)
        {
            bm.Overlay = new byte[4 * n];
            Buffer.BlockCopy(d, (int)(4 + 6 * n), bm.Overlay, 0, (int)(4 * n));
            foreach (byte b in bm.Overlay)
                if (b != 0)
                {
                    bm.HasOverlay = true;
                    break;
                }
        }
        return bm;
    }
}

/// <summary>
/// The original's paint patterns, read from the extracted UI resource archive
/// (<c>extracted/rof/ASSETS/GRAPHICS/&lt;PATTERN&gt;/</c>, produced by the extraction's <c>.rof</c> half).
///
/// A pattern is a folder of `.BM` skins, and **it is per aircraft**: `FORTUNE` covers all
/// eleven, every other pattern covers one to three. So the paint UI offers a different pattern
/// list per plane. The Fury has four (Fortune Hunters, Black Swan, Hughes, Studio Security), the
/// Balmoral two. <see cref="PatternsFor"/> is that list.
///
/// Absent extraction is not an error: the library comes back empty, aircraft build unpainted,
/// and a single line says how to produce it.
/// </summary>
public sealed class PatternLibrary
{
    // Subfolders of ASSETS/GRAPHICS that are not patterns (they hold no skins anyway, but
    // naming them keeps the log honest about what was scanned).
    private static readonly HashSet<string> NotPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "MPG", "SCRAPBOOK",
    };

    // pattern -> (skin base name -> .BM path), both case-insensitive.
    private readonly Dictionary<string, Dictionary<string, string>> _patterns =
        new(StringComparer.OrdinalIgnoreCase);
    // pattern -> the aircraft skin prefixes it covers ("blo", "fur", …)
    private readonly Dictionary<string, HashSet<string>> _covers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Pattern, string Skin), PaintBitmap?> _cache = new();
    private readonly List<string> _order = new();

    /// <summary>A library with no patterns, what an unpainted build uses, and the fallback
    /// when the rof extraction is absent.</summary>
    public static PatternLibrary Empty { get; } = new();

    /// <summary>Every pattern folder found, in scan order.</summary>
    public IReadOnlyList<string> Patterns => _order;

    public bool IsEmpty => _order.Count == 0;

    /// <summary>Scans an extracted `rof` tree. <paramref name="rofRoot"/> is the extraction
    /// root (the folder holding ASSETS). Never throws.</summary>
    public static PatternLibrary Load(string rofRoot)
    {
        var lib = new PatternLibrary();
        var graphics = Path.Combine(rofRoot, "ASSETS", "GRAPHICS");
        if (!Directory.Exists(graphics))
        {
            Log.Info("world", $"[paint] no pattern library at {graphics}, extract the game data again on the Extract screen to enable the original's paint patterns; aircraft build unpainted");
            return lib;
        }
        foreach (var dir in Directory.EnumerateDirectories(graphics))
        {
            var name = Path.GetFileName(dir);
            if (NotPatterns.Contains(name))
                continue;
            Dictionary<string, string>? skins = null;
            foreach (var bm in Directory.EnumerateFiles(dir, "*.BM"))
            {
                skins ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var skin = Path.GetFileNameWithoutExtension(bm);
                skins[skin] = bm;
            }
            if (skins == null)
                continue;
            lib._patterns[name] = skins;
            lib._order.Add(name);
            var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var skin in skins.Keys)
            {
                int us = skin.IndexOf('_');
                if (us > 0)
                    prefixes.Add(skin[..us]);
            }
            lib._covers[name] = prefixes;
        }
        return lib;
    }

    /// <summary>The patterns that carry skins for this aircraft (its `blo`/`fur`/… prefix),
    /// in scan order, exactly the list the original's paint UI offers for that plane.</summary>
    public List<string> PatternsFor(string skinPrefix)
    {
        var list = new List<string>();
        foreach (var p in _order)
            if (_covers.TryGetValue(p, out var prefixes) && prefixes.Contains(skinPrefix))
                list.Add(p);
        return list;
    }

    /// <summary>True if this pattern paints this aircraft.</summary>
    public bool Covers(string pattern, string skinPrefix) =>
        _covers.TryGetValue(pattern, out var prefixes) && prefixes.Contains(skinPrefix);

    /// <summary>The pattern's data for one skin (`blo_wing`), or null if this pattern does
    /// not paint that part. Cached, so several aircraft in one session share the decode.</summary>
    public PaintBitmap? Skin(string pattern, string skinName)
    {
        var key = (pattern, skinName);
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        PaintBitmap? bm = null;
        if (_patterns.TryGetValue(pattern, out var skins) && skins.TryGetValue(skinName, out var path))
            bm = PaintBitmap.Load(path);
        _cache[key] = bm;
        return bm;
    }
}
