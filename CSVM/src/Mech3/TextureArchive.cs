using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CSVM.Utils;
using Godot;

namespace CSVM.Mech3;

/// <summary>The texture drop-in instruments (<c>--tex-override</c>/<c>--tex-census</c>,
/// docs/cli.md), hooked into <see cref="TextureArchive.Find"/> because that is the one place
/// every consumer resolves a name. ⚠ Only RGB bytes change, size, pixel format, alpha channel
/// and mip chain stay the original's, so a diagnostic never alters what it measures. Census
/// colours are a pure hash of the name, read back by chromaticity (docs/cli.md).</summary>
public static class TextureDropIn
{
    /// <summary>How far a pixel's chromaticity may sit from a census colour's and still be counted
    /// as it. Measured, not guessed: see docs/cli.md for the sweep this and
    /// <see cref="Separation"/> come from.</summary>
    public const float Tolerance = 0.045f;

    /// <summary>The runner-up must be at least this much further away than the winner, or the pixel
    /// is reported contested rather than credited to a texture it may not belong to. An absolute
    /// gap, not a ratio: a pixel sitting exactly on a flat has a winning distance of ~0, and any
    /// ratio test passes trivially there however close the rival sits.</summary>
    public const float Separation = 0.03f;

    /// <summary>Pixels whose brightest linear channel is below this carry too little signal for a
    /// ratio to survive 8-bit quantisation; they are counted as dark, never classified.</summary>
    public const float DarkFloor = 0.02f;

    /// <summary>The colour an override with no explicit one gets. Deliberately the same magenta
    /// <c>SceneBuilder</c> paints an unresolvable texture, loud, and never a real texture, so a
    /// pose that may contain a genuine lookup failure wants an explicit colour instead.</summary>
    public static readonly Color DefaultOverride = new(1f, 0f, 1f);

    private static readonly Dictionary<string, Color> Overrides = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> OverridesSeen = new(StringComparer.OrdinalIgnoreCase);
    // Archive base name -> census colour, for every texture this session actually resolved. The
    // colour is a pure function of the name, so this is a record of what was loaded, not a source
    // of truth that has to exist before a lookup can be answered.
    private static readonly Dictionary<string, Color> Registered = new(StringComparer.OrdinalIgnoreCase);
    // Colour hex -> the first texture that claimed it, so a second claimant is reported, not lost.
    private static readonly Dictionary<string, string> ByColor = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> Extra = new();

    private static bool _census;
    private static bool _flushQueued;
    private static bool _unusedQueued;
    private static string _scratchDir = "";

    /// <summary>True once <c>--tex-census</c> asked for the whole-archive colouring.</summary>
    public static bool CensusActive => _census;

    /// <summary>The texture names <c>--tex-override=</c> claimed, sorted, a launch setting like
    /// any other, readable by the reports that record what a run was asked to do.</summary>
    public static IEnumerable<string> OverrideNames
    {
        get
        {
            var names = new List<string>(Overrides.Keys);
            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }

    /// <summary>True when either instrument is on, the flag <see cref="TextureCycler"/> reads to
    /// hold its flipbooks still, and the aircraft paint substitution reads to step aside.</summary>
    public static bool Active => _census || Overrides.Count > 0;

    /// <summary>Where <c>tex_census*.json</c> lands. Set once from the repo root.</summary>
    public static void SetScratchDir(string repoRoot) => _scratchDir = Path.Combine(repoRoot, ".scratch");

    /// <summary>Parses one <c>--tex-override=</c> value: <c>name</c>, or <c>name=color</c> where
    /// the colour is anything Godot's own parser takes (<c>ff0000</c>, <c>#ff0000</c>,
    /// <c>lime</c>). Several may be given by repeating the flag.</summary>
    public static void AddOverride(string spec)
    {
        int eq = spec.LastIndexOf('=');
        string name = eq < 0 ? spec : spec[..eq];
        string colorText = eq < 0 ? "" : spec[(eq + 1)..];
        name = Path.GetFileNameWithoutExtension(name.Trim());
        if (name.Length == 0)
        {
            Log.Warn("world", $"tex override has no texture name spec={spec}");
            return;
        }
        Color color = DefaultOverride;
        if (colorText.Length > 0)
        {
            // A sentinel no colour string can produce, so an unreadable value is told apart from
            // one that happens to equal the default.
            var sentinel = new Color(-1f, -1f, -1f);
            if (Color.HtmlIsValid(colorText))
            {
                color = Color.FromHtml(colorText);
            }
            else if (Color.FromString(colorText, sentinel) is var named && named != sentinel)
            {
                color = named;
            }
            else
            {
                Log.Warn("world", $"tex override colour unreadable texture={name} color={colorText}, using magenta");
            }
        }
        Overrides[name] = color;
        Log.Info("world", $"tex override texture={name} color={Hex(color)}");
    }

    /// <summary>Turns the census on. <paramref name="extraNames"/> are textures to include in a
    /// count report even when this chapter's archive has none of them, the able-to-fail control
    /// for "texture X is visible here".</summary>
    public static void EnableCensus(string extraNames)
    {
        _census = true;
        foreach (string n in extraNames.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = Path.GetFileNameWithoutExtension(n.Trim());
            if (name.Length > 0 && !Extra.Contains(name))
            {
                Extra.Add(name);
            }
        }
        Log.Info("world", $"tex census on tolerance={Tolerance:0.###} separation={Separation:0.###} extra={(Extra.Count == 0 ? "-" : string.Join(",", Extra))}");
    }

    /// <summary>The flat colour a texture must resolve to, or null when neither instrument covers
    /// it. <paramref name="archiveName"/> is the PNG's own base name and owns the identity;
    /// <paramref name="requestedName"/> is the (possibly truncated) name the material asked for,
    /// matched too so either spelling works on the command line.</summary>
    public static Color? ColorFor(string archiveName, string requestedName)
    {
        if (Overrides.Count > 0)
        {
            string? key = Overrides.ContainsKey(archiveName) ? archiveName
                : Overrides.ContainsKey(requestedName) ? requestedName
                : null;
            if (key != null)
            {
                if (OverridesSeen.Add(key))
                {
                    Log.Info("world", $"tex override applied texture={archiveName} requested={requestedName} color={Hex(Overrides[key])}");
                }
                return Overrides[key];
            }
        }
        if (!_census)
        {
            return null;
        }
        var census = ColorForName(archiveName);
        if (!Registered.ContainsKey(archiveName))
        {
            Registered[archiveName] = census;
            string hex = Hex(census);
            // Eight bits per channel cap the palette at ~200k colours, so a few hundred textures
            // do occasionally land on the same one. Said out loud rather than left to be inferred
            // from a count that quietly covers two surfaces.
            if (ByColor.TryGetValue(hex, out var twin))
            {
                Log.Warn("world", $"tex census colour collision texture={archiveName} shares={twin} color={hex}, counts for both are one number");
            }
            else
            {
                ByColor[hex] = archiveName;
            }
            Log.Debug("world", $"tex census texture={archiveName} color={hex}");
            QueueFlush();
        }
        return census;
    }

    /// <summary>True when the aircraft paint substitution must stand aside: a hand-composited skin
    /// would paint over the very colour the instrument put there.</summary>
    public static bool Covers(string requestedName) =>
        Active && (_census || Overrides.ContainsKey(Path.GetFileNameWithoutExtension(requestedName)));

    /// <summary>Warns about an override naming a texture the archive does not hold. Checked per
    /// archive rather than once, because "not in this chapter" is itself the answer sometimes.</summary>
    public static void CheckNames(string archivePath, IEnumerable<string> names)
    {
        if (Overrides.Count == 0)
        {
            return;
        }
        var have = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (var name in Overrides.Keys)
        {
            if (!have.Contains(name))
            {
                Log.Warn("world", $"tex override names a texture this archive lacks texture={name} archive={Path.GetFileName(archivePath)}");
            }
        }
        // Present in the archive but used by no material is the other half of the same lie, and it
        // is only knowable after the build, end of frame, once every material has resolved.
        if (!_unusedQueued)
        {
            _unusedQueued = true;
            Callable.From(ReportUnusedOverrides).CallDeferred();
        }
    }

    /// <summary>The census colour of any name, a pure hash, one texture wears one colour in every
    /// chapter and run. Full brightness, never grey: the readback classifies by channel ratios.
    /// ⚠ Draw the two free channels in LINEAR ratio, not sRGB bytes: sRGB draws bunch into the
    /// corners once gamma is undone.</summary>
    public static Color ColorForName(string name)
    {
        ulong h = Mix(Fnv1a(name.ToLowerInvariant()));
        // Continuous space of billions: an outright collision is vanishing. Crowding is the real
        // hazard, reported via the map's nearest-neighbour column and a shot's contested counts.
        float a = ((h >> 8) & 0xFFFF) / 65535f * 0.9f;
        float b = ((h >> 32) & 0xFFFF) / 65535f * 0.9f;
        var linear = (int)(h % 3) switch
        {
            0 => new Color(1f, a, b),
            1 => new Color(b, 1f, a),
            _ => new Color(a, b, 1f),
        };
        return linear.LinearToSrgb();
    }

    /// <summary>Repaints every texel's RGB, leaving size, format, alpha and (absent) mipmaps
    /// alone. Works on the raw buffer: a per-pixel setter would marshal tens of millions of calls
    /// across a chapter's textures.</summary>
    public static void Flatten(Image img, Color color)
    {
        var fmt = img.GetFormat();
        if (fmt is not (Image.Format.Rgb8 or Image.Format.Rgba8))
        {
            // Grayscale and packed formats have nowhere to put three channels. Converting is safe
            // here and only here: the alpha class was already read off the original pixels, and
            // the sampler sees the same values either way.
            img.Convert(fmt is Image.Format.La8 or Image.Format.Rgba4444 ? Image.Format.Rgba8 : Image.Format.Rgb8);
            fmt = img.GetFormat();
        }
        int stride = fmt == Image.Format.Rgba8 ? 4 : 3;
        var data = img.GetData();
        byte r = (byte)color.R8, g = (byte)color.G8, b = (byte)color.B8;
        for (int i = 0; i + stride <= data.Length; i += stride)
        {
            data[i] = r;
            data[i + 1] = g;
            data[i + 2] = b;
        }
        img.SetData(img.GetWidth(), img.GetHeight(), false, fmt, data);
    }

    /// <summary>Counts, per texture, how many of a rendered frame's pixels that texture painted.
    /// Candidates are every texture the session resolved plus any <c>--tex-census=</c> extras, so a
    /// name that cannot be on screen is measured alongside the ones that can.</summary>
    public static ShotCensus Count(Image image)
    {
        var names = new List<string>(Registered.Count + Extra.Count);
        var chroma = new List<Vector3>(Registered.Count + Extra.Count);
        var exact = new Dictionary<uint, string>();
        foreach (var (name, color) in Registered)
        {
            Add(name, color);
        }
        foreach (var name in Extra)
        {
            if (!Registered.ContainsKey(name))
            {
                Add(name, ColorForName(name));
            }
        }

        void Add(string name, Color color)
        {
            names.Add(name);
            chroma.Add(ChromaSrgb(color));
            exact[Key((byte)color.R8, (byte)color.G8, (byte)color.B8)] = name;
        }

        var img = image;
        if (img.GetFormat() is not (Image.Format.Rgb8 or Image.Format.Rgba8))
        {
            img = (Image)image.Duplicate();
            img.Convert(Image.Format.Rgba8);
        }
        int stride = img.GetFormat() == Image.Format.Rgba8 ? 4 : 3;
        var data = img.GetData();

        // Distinct colours first: a frame holds far fewer of them than pixels, and each one only
        // has to be classified against the candidate set once.
        var histogram = new Dictionary<uint, int>();
        int total = 0;
        for (int i = 0; i + stride <= data.Length; i += stride)
        {
            uint key = Key(data[i], data[i + 1], data[i + 2]);
            histogram.TryGetValue(key, out int n);
            histogram[key] = n + 1;
            total++;
        }

        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var contestedByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rivalPx = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var exactByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int classified = 0, exactPx = 0, contested = 0, dark = 0, unmatched = 0;
        foreach (var (key, count) in histogram)
        {
            if (exact.TryGetValue(key, out var hit))
            {
                exactByName.TryGetValue(hit, out int e);
                exactByName[hit] = e + count;
                exactPx += count;
            }
            var c = new Color(((key >> 16) & 0xFF) / 255f, ((key >> 8) & 0xFF) / 255f, (key & 0xFF) / 255f).SrgbToLinear();
            if (Mathf.Max(c.R, Mathf.Max(c.G, c.B)) < DarkFloor)
            {
                dark += count;
                continue;
            }
            var q = ChromaLinear(c);
            int best = -1, runnerUp = -1;
            float bestD = float.MaxValue, nextD = float.MaxValue;
            for (int i = 0; i < chroma.Count; i++)
            {
                float d = (chroma[i] - q).Length();
                if (d < bestD)
                {
                    nextD = bestD;
                    runnerUp = best;
                    bestD = d;
                    best = i;
                }
                else if (d < nextD)
                {
                    nextD = d;
                    runnerUp = i;
                }
            }
            if (best < 0 || bestD > Tolerance)
            {
                unmatched += count;
                continue;
            }
            if (nextD - bestD < Separation)
            {
                contestedByName.TryGetValue(names[best], out int a);
                contestedByName[names[best]] = a + count;
                if (runnerUp >= 0)
                {
                    if (!rivalPx.TryGetValue(names[best], out var rivals))
                    {
                        rivals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        rivalPx[names[best]] = rivals;
                    }
                    rivals.TryGetValue(names[runnerUp], out int rp);
                    rivals[names[runnerUp]] = rp + count;
                }
                contested += count;
                continue;
            }
            byName.TryGetValue(names[best], out int m);
            byName[names[best]] = m + count;
            classified += count;
        }
        var rivalByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, rivals) in rivalPx)
        {
            string top = "";
            int topPx = -1;
            foreach (var (rival, px) in rivals)
            {
                if (px > topPx)
                {
                    topPx = px;
                    top = rival;
                }
            }
            rivalByName[name] = top;
        }
        return new ShotCensus
        {
            Total = total,
            Classified = classified,
            Exact = exactPx,
            Contested = contested,
            Dark = dark,
            Unmatched = unmatched,
            ByName = byName,
            ContestedByName = contestedByName,
            RivalByName = rivalByName,
            ExactByName = exactByName,
        };
    }

    /// <summary>Counts a saved frame and writes the report beside the census map. Called from the
    /// screenshot site, so one <c>--det --tex-census --screenshot</c> run answers "≥N px of texture
    /// X from pose Y" without a second tool.</summary>
    public static void CountShot(Image image, string shotPath)
    {
        if (!_census)
        {
            return;
        }
        Flush();
        var r = Count(image);
        // Ranked by the upper bound, so a texture whose coverage is mostly contested still shows up
        // near the top instead of hiding behind a confident but smaller neighbour.
        var seen = new List<string>();
        foreach (var name in r.ByName.Keys)
        {
            seen.Add(name);
        }
        foreach (var name in r.ContestedByName.Keys)
        {
            if (!r.ByName.ContainsKey(name))
            {
                seen.Add(name);
            }
        }
        int Upper(string n)
        {
            r.ByName.TryGetValue(n, out int p);
            r.ContestedByName.TryGetValue(n, out int a);
            return p + a;
        }
        seen.Sort((x, y) => Upper(y).CompareTo(Upper(x)));
        var json = new StringBuilder();
        json.AppendLine("{");
        json.AppendLine($"  \"shot\": {Quote(shotPath)},");
        json.AppendLine($"  \"tolerance\": {Tolerance.ToString("0.####", CultureInfo.InvariantCulture)},");
        json.AppendLine($"  \"separation\": {Separation.ToString("0.####", CultureInfo.InvariantCulture)},");
        json.AppendLine($"  \"pixels\": {r.Total},");
        json.AppendLine($"  \"classified\": {r.Classified},");
        json.AppendLine($"  \"exact\": {r.Exact},");
        json.AppendLine($"  \"contested\": {r.Contested},");
        json.AppendLine($"  \"dark\": {r.Dark},");
        json.AppendLine($"  \"unmatched\": {r.Unmatched},");
        json.AppendLine("  \"textures\": [");
        for (int i = 0; i < seen.Count; i++)
        {
            r.ByName.TryGetValue(seen[i], out int px);
            r.ContestedByName.TryGetValue(seen[i], out int cont);
            r.ExactByName.TryGetValue(seen[i], out int e);
            r.RivalByName.TryGetValue(seen[i], out var rival);
            json.AppendLine($"    {{\"name\": {Quote(seen[i])}, \"px\": {px}, \"contested\": {cont}, "
                + $"\"exact\": {e}, \"rival\": {Quote(rival ?? "")}}}"
                + (i == seen.Count - 1 ? "" : ","));
        }
        json.AppendLine("  ],");
        // Every candidate that painted nothing, named rather than merely absent: "0 px" is the
        // answer this instrument is asked for as often as a positive count.
        var zero = new List<string>();
        foreach (var name in Registered.Keys)
        {
            if (Upper(name) == 0)
            {
                zero.Add(name);
            }
        }
        foreach (var name in Extra)
        {
            if (Upper(name) == 0 && !Registered.ContainsKey(name))
            {
                zero.Add(name);
            }
        }
        zero.Sort(StringComparer.OrdinalIgnoreCase);
        json.AppendLine($"  \"absent\": [{string.Join(", ", zero.ConvertAll(Quote))}]");
        json.AppendLine("}");
        string path = Path.Combine(ScratchDir(), $"tex_census_{Path.GetFileNameWithoutExtension(shotPath)}.json");
        Directory.CreateDirectory(ScratchDir());
        File.WriteAllText(path, json.ToString());
        Log.Info("world", $"tex census shot={Path.GetFileName(shotPath)} px={r.Total} confident={r.Classified} contested={r.Contested} exact={r.Exact} dark={r.Dark} unmatched={r.Unmatched} textures={seen.Count} report={path}");
        for (int i = 0; i < seen.Count && i < 12; i++)
        {
            r.ByName.TryGetValue(seen[i], out int px);
            r.ContestedByName.TryGetValue(seen[i], out int cont);
            r.RivalByName.TryGetValue(seen[i], out var rival);
            Log.Info("world", $"tex census top texture={seen[i]} px={px} contested={cont} rival={(cont > 0 ? rival ?? "-" : "-")}");
        }
    }

    /// <summary>Writes the name→colour map. Deferred and coalesced: textures resolve in bursts
    /// during a world build, and the map carries a nearest-neighbour column that is quadratic in
    /// the entry count.</summary>
    public static void Flush()
    {
        _flushQueued = false;
        if (!_census || Registered.Count == 0)
        {
            return;
        }
        var names = new List<string>(Registered.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        var chroma = new List<Vector3>(names.Count);
        foreach (var n in names)
        {
            chroma.Add(ChromaSrgb(Registered[n]));
        }
        var json = new StringBuilder();
        json.AppendLine("{");
        json.AppendLine($"  \"tolerance\": {Tolerance.ToString("0.####", CultureInfo.InvariantCulture)},");
        json.AppendLine($"  \"separation\": {Separation.ToString("0.####", CultureInfo.InvariantCulture)},");
        json.AppendLine($"  \"count\": {names.Count},");
        json.AppendLine($"  \"collisions\": {names.Count - ByColor.Count},");
        json.AppendLine("  \"textures\": [");
        for (int i = 0; i < names.Count; i++)
        {
            float nearest = float.MaxValue;
            string nearestName = "";
            for (int j = 0; j < names.Count; j++)
            {
                if (j == i)
                {
                    continue;
                }
                float d = (chroma[i] - chroma[j]).Length();
                if (d < nearest)
                {
                    nearest = d;
                    nearestName = names[j];
                }
            }
            var c = Registered[names[i]];
            json.AppendLine($"    {{\"name\": {Quote(names[i])}, \"color\": {Quote(Hex(c))}, "
                + $"\"rgb\": [{c.R8}, {c.G8}, {c.B8}], \"nearest\": {Quote(nearestName)}, "
                + $"\"nearest_dist\": {(names.Count > 1 ? nearest : 0f).ToString("0.####", CultureInfo.InvariantCulture)}}}"
                + (i == names.Count - 1 ? "" : ","));
        }
        json.AppendLine("  ]");
        json.AppendLine("}");
        string path = Path.Combine(ScratchDir(), "tex_census.json");
        Directory.CreateDirectory(ScratchDir());
        File.WriteAllText(path, json.ToString());
        Log.Info("world", $"tex census map textures={names.Count} collisions={names.Count - ByColor.Count} file={path}");
    }

    /// <summary>Override names that never matched a resolved texture, the loud half of the
    /// answer, since an override that coloured nothing looks exactly like an object that never
    /// drew.</summary>
    public static IReadOnlyList<string> UnusedOverrides()
    {
        var unused = new List<string>();
        foreach (var name in Overrides.Keys)
        {
            if (!OverridesSeen.Contains(name))
            {
                unused.Add(name);
            }
        }
        return unused;
    }

    private static void ReportUnusedOverrides()
    {
        _unusedQueued = false;
        foreach (var name in UnusedOverrides())
        {
            Log.Warn("world", $"tex override coloured nothing texture={name}, no material resolved it this session");
        }
    }

    private static void QueueFlush()
    {
        if (_flushQueued)
        {
            return;
        }
        _flushQueued = true;
        // End of this frame, once the build burst has stopped adding names.
        Callable.From(Flush).CallDeferred();
    }

    private static string ScratchDir() =>
        _scratchDir.Length > 0 ? _scratchDir : Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", ".scratch"));

    // The identity a shaded pixel keeps: the linear colour divided by its brightest channel, which
    // any scalar dimming leaves exactly where it was.
    private static Vector3 ChromaSrgb(Color srgb) => ChromaLinear(srgb.SrgbToLinear());

    private static Vector3 ChromaLinear(Color linear)
    {
        float max = Mathf.Max(linear.R, Mathf.Max(linear.G, linear.B));
        return max <= 0f ? Vector3.Zero : new Vector3(linear.R / max, linear.G / max, linear.B / max);
    }

    private static uint Key(byte r, byte g, byte b) => ((uint)r << 16) | ((uint)g << 8) | b;

    private static string Hex(Color c) => $"{c.R8:x2}{c.G8:x2}{c.B8:x2}";

    private static string Quote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static ulong Fnv1a(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s)
        {
            h = (h ^ (byte)c) * 1099511628211UL;
            h = (h ^ (byte)(c >> 8)) * 1099511628211UL;
        }
        return h;
    }

    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>One frame read back as texture coverage. <see cref="ByName"/> is a <b>lower
    /// bound</b> per texture and <see cref="ByName"/> + <see cref="ContestedByName"/> an upper one:
    /// a pixel whose runner-up census colour is nearly as close is never credited outright, because
    /// the renderer's per-vertex tinting moves a flat far enough to swap two crowded neighbours.</summary>
    public sealed class ShotCensus
    {
        public required int Total { get; init; }
        public required int Classified { get; init; }
        public required int Exact { get; init; }
        public required int Contested { get; init; }
        public required int Dark { get; init; }
        public required int Unmatched { get; init; }
        /// <summary>Texture name → pixels credited to it outright.</summary>
        public required Dictionary<string, int> ByName { get; init; }
        /// <summary>Texture name → pixels it won, but not by the required margin.</summary>
        public required Dictionary<string, int> ContestedByName { get; init; }
        /// <summary>Texture name → the runner-up that contested it most.</summary>
        public required Dictionary<string, string> RivalByName { get; init; }
        public required Dictionary<string, int> ExactByName { get; init; }
    }
}

/// <summary>Texture lookup over a mech3ax texture extraction, a ZIP (<c>texture.zbd</c> →
/// PNGs) or a directory of the same PNGs. Absorbs the stored-name quirks (20-char truncation,
/// legacy renames, docs/formats/gamez.md) so a truncated material name still resolves.
/// Mip levels: <see cref="Mips"/>, docs/formats/gamez.md. Plumbing: this module's entry in
/// docs/architecture.md.</summary>
public sealed class TextureArchive : IDisposable
{
    /// <summary>The clamp the original's <c>MipBias</c> command applies before the bias reaches the
    /// device, and the bound <see cref="MipBias"/> keeps (docs/org/textures.md).</summary>
    public const float MipBiasLimit = 1.0f;

    // Bit 2 of the render-flags word at offset 0x0E of the 16-byte texture header: the original
    // draws a sprite from a texture carrying it with DESTBLEND ONE and alpha-mixes every other.
    // The decode, the other bits and the install-wide census are in docs/org/textures.md.
    private const int AdditiveTransparentBit = 0x04;

    // Texture names referenced by gamez meshes that ship in NO archive of a retail
    // install, verified absent across all extracted chapters. The
    // original engine tolerates them (renders neutral), so we do too: a quiet gray
    // fallback instead of the debug magenta, and a one-line data-gap note instead of
    // a lookup-failure warning. Anything NOT on this list that goes missing is likely
    // our own name-resolution failing and stays loud (magenta + the "not found" line).
    private static readonly HashSet<string> KnownAbsentFromGameData = new(StringComparer.OrdinalIgnoreCase)
    {
        "pir_spinner", // referenced by every chapter's gamez (a pirate-zeppelin spinner disc)
        "barngrill",   // C5 only
    };

    // Absent from the retail data like the set above, but drawn as NOTHING rather than as a
    // neutral card: the original shows empty sky where these are referenced. Kept a separate set
    // because the two answers differ where it matters, a gray card is right where the original
    // drew something blank, and wrong where it drew nothing at all. See docs/org/textures.md.
    private static readonly HashSet<string> AbsentAndUndrawn = new(StringComparer.OrdinalIgnoreCase)
    {
        "cloud1", // C3's skydome names both, and C3 alone of the eight chapters ships neither
        "cloud2",
    };

    // The coastline sheets: the waterline where a land tile meets water, authored as a wide
    // feathered alpha ramp. Their dry-land half is solid, which carries the whole-sheet
    // opaque/ink ratio above the cutout threshold even though the waterline itself is
    // translucent, so the pixel rule alone scissors that ramp to a 1-bit sawtooth. The inland
    // transition sheets (`cliff*_trans*`, `terpat*_trans*`) measure 0.81-0.93 binary and are
    // genuine cutouts.
    private static readonly string[] SoftAlphaCoastline =
    {
        "beach1",      // C2
        "shore1",      // C3, C4
        "shore1_end",  // C4
        "shore2",      // C3
        "shore_trans", // C3
    };

    // Tree, bush and brush cards. Blended rather than cut because the original alpha-tests nothing,
    // so a 1-bit edge here is ours and not a reproduction (docs/org/textures.md). Every name is a
    // currently-scissored member of the family from the install-wide census in
    // analysis/alpha-classification/; a card the census already calls soft is absent, since naming
    // it would change nothing.
    private static readonly string[] SoftAlphaTrees =
    {
        "brush1", "brush2", "brush3", "brush4", "bush1", "bush2", "dougfirtree1",
        "firtree1", "firtree2", "palm1", "palm2", "palm3", "spruce",
    };

    // Fence, railing, ladder, stair and grate cards, the same decision as the tree family. Densest
    // over C4's Jimmy's camp, which is where the choice was judged.
    private static readonly string[] SoftAlphaRails =
    {
        "dockrails1", "gantry_tracks2", "gantry_tracks3", "grate01", "irongate", "jim_rail1",
        "ladder1", "ladder_01", "rail01", "rail02", "rail03", "rail1", "rail2", "scaffold1",
        "scaffold2", "scafolding1", "scafolding2", "shouse_rail01", "stair01", "steps",
    };

    // Tower lattice, girder, support and cable cards, the same decision again. Judged over C2's
    // Eiffel replica, with the radio mast and the suspension bridge's cables in frame.
    private static readonly string[] SoftAlphaLattice =
    {
        "bhf_ibeam", "brace01", "bridge_cables", "bridgesupport", "bsupport64", "cable_insulator",
        "cgcable1", "cgcable12", "cnine_wire1", "cranechain1", "eiffel1", "inthangbeam", "jim_sup1",
        "jim_sup2", "jim_sup3", "roofjoists01", "spar", "support1", "woodlegs01", "woodsupport2",
        "z_support01", "zepsupport01", "zepsupport03",
    };

    // The four families above as one lookup. Kept as separate lists so a family can be flipped back
    // to the pixel rule on its own; a name outside every list keeps that rule.
    private static readonly HashSet<string> SoftAlphaNamed = NamedSoftAlphaSet();

    private readonly ZipArchive? _zip;
    private readonly string? _dir;
    // baseName (no extension) -> the PNG's retrieval name (a zip entry's FullName, or a file name under _dir).
    private readonly Dictionary<string, string> _byBaseName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageTexture?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (bool HasAlpha, bool Soft, AlphaClass Class)> _alphaInfo = new(StringComparer.OrdinalIgnoreCase);
    // Archive base name -> the extractor's own alpha class, off the extraction manifest. Empty when
    // the tree ships PNGs alone, which is why every read of it falls back to the pixel test.
    private readonly Dictionary<string, AlphaClass> _alphaClasses = new(StringComparer.OrdinalIgnoreCase);
    // Archive base name -> the texture header's render-flags word, off the same manifest. Empty on a
    // PNG-only tree, which reads as 0 and so alpha-mixes, the engine's own fallback value.
    private readonly Dictionary<string, int> _renderFlags = new(StringComparer.OrdinalIgnoreCase);
    // Distinct texture names this archive failed to resolve, each already logged once.
    private readonly HashSet<string> _reportedMissing = new(StringComparer.OrdinalIgnoreCase);
    // Authored _N siblings whose dimensions disagreed with the level they claim, logged once each.
    private readonly HashSet<string> _reportedOddMips = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public TextureArchive(string path)
    {
        if (Directory.Exists(path))
        {
            _dir = path;
            foreach (var file in Directory.EnumerateFiles(path, "*.png"))
                _byBaseName[Path.GetFileNameWithoutExtension(file)] = Path.GetFileName(file);
        }
        else
        {
            _zip = ZipFile.OpenRead(path);
            foreach (var entry in _zip.Entries)
                if (entry.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    _byBaseName[Path.GetFileNameWithoutExtension(entry.Name)] = entry.FullName;
        }
        // Every (base, level) pair the archive ships, counted once up front so the adoption log can
        // say "N of the M this chapter ships", a bare adopted count cannot show a level was missed.
        var bases = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _byBaseName.Keys)
        {
            if (MipLevelOf(name) is > 0 && _byBaseName.ContainsKey(name[..^2]))
            {
                AuthoredMipsAvailable++;
                bases.Add(name[..^2]);
            }
        }
        AuthoredMipBases = new List<string>(bases);
        ReadTextureManifest();
        // An override naming a texture this archive does not hold would silently colour nothing,
        // which reads exactly like "the object is not drawing", the answer the flag exists to give.
        TextureDropIn.CheckNames(path, _byBaseName.Keys);
    }

    /// <summary>Where a texture's upper mip levels come from.</summary>
    public enum MipSource
    {
        /// <summary>Levels 1 and 2 are the archive's own <c>_N</c> siblings wherever one exists and
        /// is exactly half/quarter the base's size; everything below is box-filtered as before, and
        /// a texture with no sibling is untouched.</summary>
        Authored,

        /// <summary>Every level is box-filtered from the base by <c>Image.GenerateMipmaps()</c>,
        /// kept as <c>--mips=generated</c> so the two are A/B-able in
        /// one build and the goldens of either can be reproduced.</summary>
        Generated,
    }

    /// <summary>A texture's alpha class as the extractor read it out of the archive's own header
    /// (storage flags at <c>+0x09</c>, docs/org/textures.md), not off the decoded pixels. The
    /// original's hardware draw keys no lighting decision on it, only routing through the sorted
    /// transparent queue (docs/org/vertexLighting.md); this reader is the one source that sees the
    /// <see cref="Simple"/> textures at all.</summary>
    public enum AlphaClass
    {
        /// <summary>No alpha channel: header bit <c>0x04</c>.</summary>
        None,

        /// <summary>An alpha channel carried in the pixel data: header bits <c>0x02</c> without
        /// <c>0x08</c>. One to ten textures per chapter, invisible to a PNG alpha test.</summary>
        Simple,

        /// <summary>An alpha channel in a plane of its own: header bits <c>0x02</c> and
        /// <c>0x08</c>.</summary>
        Full,
    }

    /// <summary>Which mip policy every archive built afterwards follows (<c>--mips=</c>). A static
    /// for the same reason <see cref="TextureDropIn"/>'s state is: it is a launch setting read
    /// during a build, not a per-archive property anyone chooses.</summary>
    public static MipSource Mips { get; set; } = MipSource.Authored;

    /// <summary>Distinct texture names this archive could not resolve, for an end-of-build
    /// summary line. Each was already reported once (one line, no stack trace) by Find.</summary>
    public IReadOnlyCollection<string> MissingTextures => _reportedMissing;

    /// <summary>Authored <c>_1</c>/<c>_2</c> levels this archive ships whose base is present too,
    /// the denominator of the adoption count, known before anything is loaded.</summary>
    public int AuthoredMipsAvailable { get; private set; }

    /// <summary>The base textures those levels belong to, sorted, what <c>--dump-mips</c> sweeps.</summary>
    public IReadOnlyList<string> AuthoredMipBases { get; } = Array.Empty<string>();

    /// <summary>Authored levels actually installed this session, and the base textures they were
    /// installed on. Below <see cref="AuthoredMipsAvailable"/> by however many of those textures no
    /// material asked for, a chapter never loads its whole archive.</summary>
    public int AuthoredMipsInstalled { get; private set; }

    /// <inheritdoc cref="AuthoredMipsInstalled"/>
    public int AuthoredMipTextures { get; private set; }

    /// <summary>Authored levels refused because the sibling was not exactly half/quarter the base's
    /// size. A name-convention match is a lead, not a guarantee; each refusal is logged.</summary>
    public int AuthoredMipsRefused { get; private set; }

    /// <summary>True if the last texture returned by Find had an alpha channel.</summary>
    public bool LastHadAlpha { get; private set; }

    /// <summary>True when the last texture's alpha is "soft", mostly partial alpha, which a
    /// 1-bit scissor at 0.5 misrepresents (threshold and census: docs/org/textures.md).
    /// True for translucent art (shadows, fire, glow, neon); false for
    /// genuine cutouts (fences, trees), which scissor correctly.</summary>
    public bool LastAlphaIsSoft { get; private set; }

    /// <summary>The archive header's own alpha class for the last texture returned by
    /// <see cref="Find"/>. ⚠ Not interchangeable with <see cref="LastHadAlpha"/>: this is the
    /// extractor's field, so it holds for the <see cref="AlphaClass.Simple"/> textures a pixel
    /// test misses. Falls back to the pixel test only where no manifest ships.</summary>
    public AlphaClass LastAlphaClass { get; private set; }

    /// <summary>Textures the extraction manifest classified, and how many of those carry an alpha
    /// channel. Zero means no manifest shipped and <see cref="LastAlphaClass"/> is running on the
    /// pixel fallback; the population per chapter is in docs/org/vertexLighting.md.</summary>
    public int ManifestTextureCount => _alphaClasses.Count;

    /// <inheritdoc cref="ManifestTextureCount"/>
    public int ManifestAlphaCount
    {
        get
        {
            int n = 0;
            foreach (var cls in _alphaClasses.Values)
                if (cls != AlphaClass.None)
                    n++;
            return n;
        }
    }

    /// <summary>True if the name is a texture the retail game data itself lacks (see
    /// KnownAbsentFromGameData), callers render a neutral fallback, not the debug magenta.</summary>
    public static bool IsKnownAbsent(string materialTextureName) =>
        KnownAbsentFromGameData.Contains(Path.GetFileNameWithoutExtension(materialTextureName));

    /// <summary>True if the name is in the soft-alpha family table, the coastline, tree, rail and
    /// lattice cards that blend whatever their pixels measure (docs/org/textures.md). The named
    /// half of the classification only; the pixel rule decides every other texture.</summary>
    public static bool IsNamedSoftAlpha(string materialTextureName) =>
        SoftAlphaNamed.Contains(Path.GetFileNameWithoutExtension(materialTextureName));

    /// <summary>The chapter's authored mip LOD bias, read from the interp extraction's
    /// <c>MipBias</c> lines and clamped like the original's own command. 0 when the chapter authors
    /// none, which is every chapter but C5 (docs/org/textures.md).
    /// ⚠ Read <c>adjust.gw</c> only. <c>load.gw</c> is the data-compile path, which a retail
    /// install cannot run: it sources the terrain <c>.flt</c> the install does not ship.</summary>
    public static float MipBias(string interpPath, string chapter)
    {
        float bias = 0f;
        if (!File.Exists(interpPath))
        {
            return bias;
        }

        var wanted = $"support\\{chapter.ToLowerInvariant()}\\adjust.gw";
        using var doc = JsonDocument.Parse(File.ReadAllBytes(interpPath));
        foreach (var script in doc.RootElement.EnumerateArray())
        {
            if (!script.TryGetProperty("name", out var n)
                || !string.Equals(n.GetString(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var line in script.GetProperty("lines").EnumerateArray())
            {
                var parts = (line.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] == "MipBias"
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                {
                    bias = Math.Clamp(v, -MipBiasLimit, MipBiasLimit);
                }
            }
        }

        return bias;
    }

    /// <summary>True when the name is one the original draws nothing for (see AbsentAndUndrawn)
    /// AND this archive cannot resolve it, so the caller drops the polygon instead of surfacing it.
    /// ⚠ Both halves are required: the chapters that DO ship the texture must keep drawing it, so
    /// this is a per-archive question and not a name test.</summary>
    public bool IsAbsentAndUndrawn(string materialTextureName) =>
        AbsentAndUndrawn.Contains(Path.GetFileNameWithoutExtension(materialTextureName))
        && Find(materialTextureName) == null;

    /// <summary>The texture header's render-flags word as the extractor read it, 0 for a name this
    /// archive does not hold and for a PNG-only tree with no manifest. Bit 2 is the blend flag;
    /// bits 0, 1 and 3 are named by data correlation only (docs/org/textures.md).</summary>
    public int RenderFlags(string materialTextureName)
    {
        // ⚠ Read the flags off the RESOLVED archive name, never the material's: a truncated or
        // renamed material name is not a manifest key (see Resolve).
        var resolved = Resolve(Path.GetFileNameWithoutExtension(materialTextureName));
        return resolved != null
            && _renderFlags.TryGetValue(Path.GetFileNameWithoutExtension(resolved), out int flags)
            ? flags : 0;
    }

    /// <summary>True when the texture carries the additive-transparent bit, which is the whole of
    /// the original's blend rule: a sprite drawn from it adds, every other alpha-mixes
    /// (docs/org/textures.md). Unknown reads as alpha-mixed, the engine's own fallback.</summary>
    public bool IsAdditive(string materialTextureName) =>
        (RenderFlags(materialTextureName) & AdditiveTransparentBit) != 0;

    public ImageTexture? Find(string materialTextureName)
    {
        var baseName = Path.GetFileNameWithoutExtension(materialTextureName);
        // A truncated name like "blo_fusalagebottom.t" keeps its bogus extension after
        // GetFileNameWithoutExtension strips ".t"; that is exactly the prefix we want.
        if (_cache.TryGetValue(baseName, out var cached))
        {
            (LastHadAlpha, LastAlphaIsSoft, LastAlphaClass) = _alphaInfo[baseName];
            return cached;
        }

        var img = Build(baseName, out bool resolved, out _);
        ImageTexture? tex = img != null ? ImageTexture.CreateFromImage(img) : null;
        if (!resolved && _reportedMissing.Add(baseName))
        {
            // Report each distinct miss once. Log.Warn is a plain line, GD.PushWarning would
            // print a full managed stack trace per call in Godot .NET, burying real errors.
            if (AbsentAndUndrawn.Contains(baseName))
            {
                Log.Warn("world", $"texture absent from game data texture={materialTextureName}, polygons undrawn");
            }
            else if (IsKnownAbsent(materialTextureName))
            {
                Log.Warn("world", $"texture absent from game data texture={materialTextureName}, gray fallback");
            }
            else
            {
                Log.Warn("world", $"texture not found in archive texture={materialTextureName}");
            }
        }
        _cache[baseName] = tex;
        _alphaInfo[baseName] = (LastHadAlpha, LastAlphaIsSoft, LastAlphaClass);
        return tex;
    }

    /// <summary>The exact <see cref="Image"/> <see cref="Find"/> installs, freshly decoded, alpha
    /// classified, drop-in applied and mip chain built, handed back un-cached so an instrument
    /// measures the chain the renderer got rather than a re-derivation of it. Reports how many of
    /// its levels came from authored siblings. Not for render code: it rebuilds every call, and it
    /// moves <see cref="LastHadAlpha"/> and the adoption counters exactly as a lookup would.</summary>
    public Image? BuildMipped(string materialTextureName, out int authoredLevels) =>
        Build(Path.GetFileNameWithoutExtension(materialTextureName), out _, out authoredLevels);

    /// <summary>The raw PNG as a fresh, un-mipmapped <see cref="Image"/>, the source
    /// <see cref="PlanePainter"/> recolours. Deliberately NOT the ImageTexture cache: that
    /// one is shared across every plane and world instance and must never be mutated, and
    /// its images already carry generated mipmaps. Each call returns a new Image.</summary>
    public Image? FindImage(string materialTextureName)
    {
        // a synchronous decode on the frame path, PlanePainter calls this
        // per decal slot at AI spawn (players draw at build) and at livery repaint.
        using var _ = PerfSample.Scope(PerfSite.ResourceLoad);
        var name = Resolve(Path.GetFileNameWithoutExtension(materialTextureName));
        var bytes = name != null ? ReadBytes(name) : null;
        if (bytes == null)
            return null;
        var img = new Image();
        return img.LoadPngFromBuffer(bytes) == Error.Ok ? img : null;
    }

    /// <summary>The archive's texture whose name begins with a zero-padded two-digit
    /// number, e.g. 21 → "21ace_star", how <c>paint_decalN</c> indexes the gapless 00–49
    /// decal set every chapter ships. Null when out of range. The half-size "_1" LOD twins
    /// are excluded (they share the numeric prefix but are not the decal itself).</summary>
    public string? FindByDecalIndex(int index)
    {
        if (index is < 0 or > 99)
            return null;
        var prefix = index.ToString("00");
        foreach (var name in _byBaseName.Keys)
            if (name.Length > 2 && name.StartsWith(prefix, StringComparison.Ordinal)
                && !char.IsDigit(name[2]) && !name.EndsWith("_1", StringComparison.Ordinal))
                return name;
        return null;
    }

    /// <summary>The raw PNG bytes of one texture, or null when the archive lacks it. The read
    /// primitive under <see cref="Find"/>, public because <see cref="Find"/> returns a Godot resource
    /// a unit test cannot construct; the after-<see cref="Dispose"/> contract is asserted here.</summary>
    public byte[]? ReadPngBytes(string materialTextureName) =>
        Resolve(Path.GetFileNameWithoutExtension(materialTextureName)) is { } name ? ReadBytes(name) : null;

    /// <summary>Ends the archive's usable life in both shapes: a later uncached read throws
    /// <see cref="ObjectDisposedException"/> from a folder exactly as the zip's own handle does.
    /// ⚠ Do not let a folder read succeed after this. The battery reads unpacked folders and every
    /// player reads zips, so a lenient folder hides a closed-archive read from the battery.</summary>
    public void Dispose()
    {
        _disposed = true;
        _zip?.Dispose();
    }

    // The level an archive name claims by its `_N` suffix, or 0 for a base texture. Only 1 and 2
    // ship; anything else is a texture whose real name happens to end in a digit.
    private static int MipLevelOf(string archiveBaseName) =>
        archiveBaseName.Length > 2 && archiveBaseName[^2] == '_' && archiveBaseName[^1] is '1' or '2'
            ? archiveBaseName[^1] - '0'
            : 0;

    private static HashSet<string> NamedSoftAlphaSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in new[] { SoftAlphaCoastline, SoftAlphaTrees, SoftAlphaRails, SoftAlphaLattice })
        {
            set.UnionWith(family);
        }

        return set;
    }

    // Fraction of ink texels (a >= 32) that are truly opaque (a >= 200); below the threshold is
    // soft (the value and the census are on docs/org/textures.md).
    private static bool AlphaIsSoft(Image img)
    {
        var rgba = img;
        if (img.GetFormat() != Image.Format.Rgba8)
        {
            rgba = (Image)img.Duplicate();
            rgba.Convert(Image.Format.Rgba8);
        }
        var data = rgba.GetData();
        int ink = 0, opaque = 0;
        for (int i = 3; i < data.Length; i += 4)
        {
            int a = data[i];
            if (a >= 32)
            {
                ink++;
                if (a >= 200)
                    opaque++;
            }
        }
        // No ink at all draws nothing either way; call it soft.
        return opaque < ink * 0.45f || ink == 0;
    }

    // The extractor spells the render-flags word as an enum, which is why the additive bit hides
    // behind an `Unk` name: None/Horizontal/Vertical/Both are bits 0 and 1, and `UnkN` is the raw
    // value N. A number is accepted too, so a later extractor that stops spelling it still reads.
    private static int RenderFlagsOf(JsonElement stretch)
    {
        if (stretch.ValueKind == JsonValueKind.Number)
            return stretch.TryGetInt32(out int raw) ? raw : 0;
        return stretch.GetString() switch
        {
            "None" => 0,
            "Horizontal" => 1,
            "Vertical" => 2,
            "Both" => 3,
            { } s when s.StartsWith("Unk", StringComparison.Ordinal)
                && int.TryParse(s.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) => n,
            _ => 0,
        };
    }

    private static bool ImageHasAlpha(Image img) =>
        img.GetFormat() is Image.Format.Rgba8 or Image.Format.La8 or Image.Format.Rgba4444 && img.DetectAlpha() != Image.AlphaMode.None;

    // Rescales each generated mip level's alpha so its scissor coverage (a > 127) matches the
    // base level's, instead of letting the box filter erode it, the fix for lattices/foliage
    // thinning at distance. Boost-only; a level
    // already at coverage is untouched. Blend-class textures (no scissor) never reach this.
    private static void ScissorMipsKeepCoverage(Image img)
    {
        int mips = img.GetMipmapCount();
        if (img.GetFormat() != Image.Format.Rgba8 || mips < 1)
        {
            return;
        }
        var data = img.GetData();
        int baseW = img.GetWidth(), baseH = img.GetHeight();
        long basePixels = (long)baseW * baseH;
        long covered = 0;
        for (long i = 3; i < basePixels * 4; i += 4)
        {
            if (data[i] > 127)
            {
                covered++;
            }
        }
        if (covered == 0)
        {
            return; // nothing passes the scissor even at full resolution; leave the chain alone
        }
        double coverage = (double)covered / basePixels;
        var hist = new int[256];
        bool changed = false;
        for (int level = 1; level <= mips; level++)
        {
            long offset = img.GetMipmapOffset(level);
            long end = level < mips ? img.GetMipmapOffset(level + 1) : data.Length;
            System.Array.Clear(hist, 0, hist.Length);
            for (long i = offset + 3; i < end; i += 4)
            {
                hist[data[i]]++;
            }
            long want = (long)System.Math.Round(coverage * ((end - offset) / 4));
            if (want <= 0)
            {
                continue;
            }
            long above = 0;
            int v = 0;
            for (int a = 255; a >= 0; a--)
            {
                above += hist[a];
                if (above >= want)
                {
                    v = a;
                    break;
                }
            }
            if (v >= 128)
            {
                continue; // this level already keeps the base's coverage
            }
            float scale = System.Math.Min(127.5f / System.Math.Max(v, 1), 8f);
            for (long i = offset + 3; i < end; i += 4)
            {
                data[i] = (byte)System.Math.Min((int)(data[i] * scale + 0.5f), 255);
            }
            changed = true;
        }
        if (changed)
        {
            img.SetData(baseW, baseH, true, Image.Format.Rgba8, data);
        }
    }

    // The extractor's class where the manifest ships, and the decoded pixels where it does not.
    // ⚠ The fallback is a degradation, not an equivalent: it cannot see the `Simple` textures,
    // which carry the alpha bit with no PNG alpha channel (docs/org/vertexLighting.md).
    private AlphaClass AlphaClassOf(string archiveBaseName, bool hasPixelAlpha) =>
        _alphaClasses.TryGetValue(archiveBaseName, out var stored)
            ? stored
            : hasPixelAlpha ? AlphaClass.Full : AlphaClass.None;

    // The extraction manifest's per-texture `alpha` and `stretch` fields, and nothing else from it.
    // Absent from a deployed PNG-only tree, which is not an error: AlphaClassOf falls back to the
    // pixels there and the render flags fall back to 0.
    private void ReadTextureManifest()
    {
        var bytes = ReadBytes("manifest.json");
        if (bytes == null)
            return;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("texture_infos", out var infos)
                || infos.ValueKind != JsonValueKind.Array)
                return;
            foreach (var info in infos.EnumerateArray())
            {
                if (!info.TryGetProperty("name", out var name) || name.GetString() is not { } texName)
                    continue;
                if (info.TryGetProperty("alpha", out var alpha)
                    && Enum.TryParse(alpha.GetString(), out AlphaClass cls))
                    _alphaClasses[texName] = cls;
                if (info.TryGetProperty("stretch", out var stretch))
                    _renderFlags[texName] = RenderFlagsOf(stretch);
            }
        }
        catch (JsonException e)
        {
            Log.Warn("world", $"texture manifest would not parse: {e.Message}");
        }
    }

    // Decodes one texture and builds its whole mip chain. `resolved` says whether the name found a
    // PNG at all, so a decode failure is not reported as a missing texture.
    private Image? Build(string baseName, out bool resolved, out int authoredLevels)
    {
        authoredLevels = 0;
        LastHadAlpha = false;
        LastAlphaIsSoft = false;
        LastAlphaClass = AlphaClass.None;
        var name = Resolve(baseName);
        var bytes = name != null ? ReadBytes(name) : null;
        resolved = bytes != null;
        if (bytes == null)
        {
            return null;
        }
        var img = new Image();
        if (img.LoadPngFromBuffer(bytes) != Error.Ok)
        {
            Log.Warn("world", $"texture png would not decode texture={name}");
            return null;
        }
        LastHadAlpha = ImageHasAlpha(img);
        // ⚠ Read the class off the RESOLVED archive name, never the material's: a truncated or
        // renamed material name is not a manifest key (see Resolve).
        LastAlphaClass = AlphaClassOf(Path.GetFileNameWithoutExtension(name!), LastHadAlpha);
        // Before mipmaps: raw pixels only. A texture in the family table is soft whatever the pixel
        // rule says about it (see SoftAlphaNamed).
        LastAlphaIsSoft = LastHadAlpha && (AlphaIsSoft(img) || SoftAlphaNamed.Contains(baseName) || baseName.Contains("trans"));
        // The drop-in instruments repaint the RGB flat and keep everything else, size, format,
        // alpha channel, so the alpha class read just above (and with it the blend/scissor choice
        // and the cutout silhouette) is unchanged.
        var flat = TextureDropIn.ColorFor(Path.GetFileNameWithoutExtension(name!), baseName);
        if (flat is { } color)
        {
            TextureDropIn.Flatten(img, color);
        }
        // Box-filter the whole chain first either way: it allocates the levels and fixes their
        // offsets, and levels 3-and-below have no authored sibling to take their place.
        img.GenerateMipmaps();
        // Scissor cutouts only: box-filtered alpha sinks below the 0.5 threshold at distance and
        // the object thins away. Must run BEFORE the authored install.
        if (LastHadAlpha && !LastAlphaIsSoft)
        {
            ScissorMipsKeepCoverage(img);
        }
        if (Mips == MipSource.Authored)
        {
            authoredLevels = InstallAuthoredMips(img, Path.GetFileNameWithoutExtension(name!), flat);
            if (authoredLevels > 0)
            {
                AuthoredMipsInstalled += authoredLevels;
                AuthoredMipTextures++;
            }
        }
        return img;
    }

    // Overwrites levels 1 and 2 with the archive's authored `_1`/`_2` siblings where they exist.
    // Runs on the already-generated chain, so a level with no sibling, and every level below 2,
    // keeps its box filter, and a texture with no siblings at all is bit-identical to before.
    private int InstallAuthoredMips(Image img, string archiveBaseName, Color? flat)
    {
        // A level image is not itself a base: `cblock1_1` has no `cblock1_1_1`, and asking would
        // only cost a dictionary probe per texture.
        if (MipLevelOf(archiveBaseName) > 0)
        {
            return 0;
        }
        int mipCount = img.GetMipmapCount();
        if (mipCount < 1)
        {
            return 0;
        }
        var format = img.GetFormat();
        int baseW = img.GetWidth(), baseH = img.GetHeight();
        byte[]? data = null;
        int installed = 0;
        for (int level = 1; level <= 2 && level <= mipCount; level++)
        {
            if (!_byBaseName.TryGetValue($"{archiveBaseName}_{level}", out var retrieval))
            {
                continue;
            }
            var bytes = ReadBytes(retrieval);
            var sibling = new Image();
            if (bytes == null || sibling.LoadPngFromBuffer(bytes) != Error.Ok)
            {
                Refuse(level, "png would not decode");
                continue;
            }
            // The name convention is a lead; the dimensions decide. A sibling that is not exactly
            // this level's size is some other texture that happens to be spelled like one.
            int wantW = Mathf.Max(1, baseW >> level), wantH = Mathf.Max(1, baseH >> level);
            if (sibling.GetWidth() != wantW || sibling.GetHeight() != wantH)
            {
                Refuse(level, $"{sibling.GetWidth()}x{sibling.GetHeight()}, level {level} of "
                              + $"{baseW}x{baseH} wants {wantW}x{wantH}");
                continue;
            }
            if (sibling.GetFormat() != format)
            {
                sibling.Convert(format);
            }
            // An instrument that repainted the base and not its authored levels would report the
            // distance a texture painted in some other texture's colour.
            if (flat is { } color)
            {
                TextureDropIn.Flatten(sibling, color);
            }
            data ??= img.GetData();
            long offset = img.GetMipmapOffset(level);
            long end = level + 1 <= mipCount ? img.GetMipmapOffset(level + 1) : data.Length;
            var src = sibling.GetData();
            if (src.Length != end - offset)
            {
                Refuse(level, $"{src.Length} bytes where the level holds {end - offset}");
                continue;
            }
            Buffer.BlockCopy(src, 0, data, (int)offset, src.Length);
            installed++;
        }
        if (installed > 0)
        {
            img.SetData(baseW, baseH, true, format, data!);
        }
        return installed;

        void Refuse(int level, string why)
        {
            AuthoredMipsRefused++;
            if (_reportedOddMips.Add($"{archiveBaseName}_{level}"))
            {
                Log.Warn("world", $"authored mip level refused texture={archiveBaseName}_{level} reason={why}");
            }
        }
    }

    private string? Resolve(string baseName)
    {
        if (_byBaseName.TryGetValue(baseName, out var exact))
            return exact;
        // mech3ax v0.6.1 disambiguates duplicate texture-table entries as "name.-N";
        // the pixel data lives under the original name. The fork indexes textures by
        // position instead and never renames, so this is legacy-tree-only.
        var m = System.Text.RegularExpressions.Regex.Match(baseName, @"^(.*)\.-\d+$");
        if (m.Success && _byBaseName.TryGetValue(m.Groups[1].Value, out var renamed))
            return renamed;
        // Trailing doubled periods: fork-only, decode in docs/formats/gamez.md.
        var trimmed = baseName.TrimEnd('.');
        if (trimmed.Length != baseName.Length && _byBaseName.TryGetValue(trimmed, out var undoubled))
            return undoubled;
        // fixed-width truncation fallback: unique prefix match
        string? match = null;
        foreach (var (name, retrieval) in _byBaseName)
        {
            if (name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
            {
                if (match != null)
                    return null; // ambiguous
                match = retrieval;
            }
        }
        return match;
    }

    private byte[]? ReadBytes(string retrievalName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_dir != null)
        {
            var p = Path.Combine(_dir, retrievalName);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }
        var entry = _zip!.GetEntry(retrievalName);
        if (entry == null)
            return null;
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

}
