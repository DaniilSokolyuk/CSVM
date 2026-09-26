using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Mech3;

namespace CSVM.UI.Hangar;

/// <summary>
/// The PAINT screen, the original's own model: a pattern, three (colour, shade) index pairs and
/// three decals, over a live preview composed through the original's own paint-screen region masks
/// (<see cref="PaintIcons"/>, the plan view the blueprint page draws, not the aircraft's 3D skin,
/// which <see cref="PlanePainter"/> paints from a different mask set entirely). The pattern row
/// steps only the patterns this airframe's availability mask allows and loads that entry's six
/// colour/shade defaults on the way; a colour row steps the 27-row swatch table and resets its
/// slot's shade, a shade row walks that colour's own ramp, and the three decal rows step the
/// gapless 00-49 texture set. Every table is decoded data in <c>CSVM/data</c>
/// (docs/formats/paint.md, "The swatch table and the pattern defaults").
/// </summary>
public sealed class HangarPaintPage : HangarPage
{
    /// <summary>The pattern row; then a colour and a shade row per slot, then the three decals.</summary>
    public const int PatternRow = 0;

    /// <summary>The first decal row (nose); tail and wing follow.</summary>
    public const int NoseDecalRow = 7;

    // The pattern dropdown's labels are langui 3425 + pattern index (callback 2235).
    private const int PatternStringBase = 3425;

    // Pattern index 0-13 (record +0x40) is a row of the engine's 14-entry pattern-name table at
    // 0x0060301c, which is also the archive folder name. Read out of crimson.exe; the four
    // indices seven genuine saves pin (blckswan 1, fortune 4, hughes 6, studio 11) all match.
    private static readonly string[] PatternNames =
    {
        "blackhat", "blckswan", "blake", "british", "fortune", "hollywd", "hughes",
        "medusas", "cccp", "sactrust", "german", "studio", "broadway", "itstaxi",
    };

    // Airframe id 0-10 to the skin-texture prefix its masks are named after. Confirmed against
    // the shipped icon sets: each PX_ICON_<af>_<pattern>_* set is exactly the pattern list
    // PatternsFor gives that prefix (itstaxi aside, which ships no icons at all).
    private static readonly string[] SkinPrefixes =
    {
        "agyro", "hel", "bal", "blo", "bri", "dev", "fir", "fur", "kes", "pea", "war",
    };

    private readonly Dictionary<int, List<int>> _rosters = new();
    private readonly Dictionary<int, PaintIcons?> _iconSets = new();
    private readonly Dictionary<int, HangarArt?> _decalArt = new();
    private HangarArt? _art;
    private (int Airframe, int Pattern, PaintColour C1, PaintColour C2, PaintColour C3)? _artKey;
    private TgaImage? _decalSheet;
    private bool _decalSheetTried;

    /// <summary>Binds the page to its flow.</summary>
    public HangarPaintPage(HangarFlow flow)
        : base(flow)
    {
    }

    /// <inheritdoc/>
    public override HangarScreen Screen => HangarScreen.Paint;

    /// <inheritdoc/>
    public override int RowCount => NoseDecalRow + HangarPaintTables.Slots;

    /// <summary>The preview of the scratch plane's own paint, recomposed whenever any of the five
    /// fields it draws from changes and handed over as the same image until then, so the shell
    /// rebuilds its texture on an edit and on nothing else.</summary>
    public override HangarArt? Art
    {
        get
        {
            var key = (Scratch.Airframe, Scratch.PaintPattern, Scratch.Colour1, Scratch.Colour2,
                Scratch.Colour3);
            if (_artKey is { } seen && seen.Equals(key))
            {
                return _art;
            }

            _artKey = key;
            _art = LivePreview() ?? IconFor(Scratch.PaintPattern);
            return _art;
        }
    }

    /// <summary>The swatch, pattern and decal tables every row on this screen reads.</summary>
    private static HangarPaintTables Tables => HangarPaintTables.Default;

    /// <summary>The archive folder / table name for a pattern index, or "" outside 0-13.</summary>
    public static string PatternName(int pattern) =>
        pattern >= 0 && pattern < PatternNames.Length ? PatternNames[pattern] : string.Empty;

    /// <summary>The skin-texture prefix an airframe's masks are named after.</summary>
    public static string SkinPrefix(int airframe) =>
        SkinPrefixes[Math.Clamp(airframe, 0, SkinPrefixes.Length - 1)];

    /// <summary>Paint is free (langui 1158), so no row here takes the wallet mark.</summary>
    public override int? CostWith(int row) => null;

    /// <inheritdoc/>
    public override string RowText(int row)
    {
        if (row == PatternRow)
        {
            return "Pattern: " + PatternLabel(Scratch.PaintPattern);
        }

        if (row >= NoseDecalRow)
        {
            int decal = DecalOf(row);
            return $"{DecalSlotName(row)}: {DecalLabel(decal)}";
        }

        int slot = SlotOf(row);
        if (IsShadeRow(row))
        {
            var rgb = Scratch.PaintColourAt(slot);
            return $"Shade {slot + 1}: {Scratch.PaintShades[slot] + 1}/{ShadeCount(slot)}   {rgb.R},{rgb.G},{rgb.B}";
        }

        var chip = Tables.Resolve(Scratch.PaintColours[slot], Tables.DefaultShadeFor(Scratch.PaintColours[slot]));
        return $"{SlotName(slot)}: swatch {Scratch.PaintColours[slot]}   {chip.R},{chip.G},{chip.B}";
    }

    /// <inheritdoc/>
    public override string Detail(int row)
    {
        if (row == PatternRow)
        {
            var roster = Roster();
            int seat = roster.IndexOf(Scratch.PaintPattern);
            string where = $"{roster.Count} of {HangarPaintTables.PatternCount} patterns fit this airframe";
            return seat < 0 ? $"{where}   (this one does not)" : $"{where}   {seat + 1}/{roster.Count}";
        }

        if (row >= NoseDecalRow)
        {
            return row == NoseDecalRow
                ? "nose art, the 21-49 half of the set"
                : "squadron and nation logos, the 00-20 half";
        }

        int slot = SlotOf(row);
        var paint = Scratch.PaintColourAt(slot);
        return IsShadeRow(row)
            ? $"{paint.R},{paint.G},{paint.B}   the colour's own dark-to-light ramp"
            : $"{Tables.Swatches.Count} swatches   picking one resets this slot's shade to its default";
    }

    /// <inheritdoc/>
    public override bool Step(int row, int dir)
    {
        if (row == PatternRow)
        {
            return StepPattern(dir);
        }

        if (row >= NoseDecalRow)
        {
            return StepDecal(row, dir);
        }

        return IsShadeRow(row) ? StepShade(SlotOf(row), dir) : StepColour(SlotOf(row), dir);
    }

    /// <summary>On a decal row, the chosen decal's own tile out of the shipped 5-wide sheet, so
    /// the three decals are picked by their artwork and not by their filename. Null on every other
    /// row, on the keep-the-placeholder sentinel, and without an extraction.</summary>
    public override HangarArt? RowArt(int row) =>
        row >= NoseDecalRow && row < RowCount ? DecalArt(DecalOf(row)) : null;

    // The original's own paint-screen composite: four alpha-over layers on a bare page, the three
    // region masks in slot order carrying the picked colours, then the detail plate on top. No
    // shading multiply and no weight normalisation: a fully-masked texel IS its colour, and the
    // panel lines, canopy and propeller are the plate showing through where no mask claims it.
    private static byte[] Compose(PaintIcons set, PaintColour c1, PaintColour c2, PaintColour c3)
    {
        var plate = set.Plate;
        int n = plate.Width * plate.Height;
        var rgba = new byte[n * 4];
        PaintColour[] colours = { c1, c2, c3 };
        for (int p = 0; p < n; p++)
        {
            int o = p * 4;
            float ar = 0f, ag = 0f, ab = 0f, aa = 0f;
            for (int slot = 0; slot < colours.Length; slot++)
            {
                float a = set.Masks[slot].Rgba[o + 3] / 255f;
                var c = colours[slot];
                ar = (ar * (1f - a)) + (c.R * a);
                ag = (ag * (1f - a)) + (c.G * a);
                ab = (ab * (1f - a)) + (c.B * a);
                aa = (aa * (1f - a)) + a;
            }

            float pa = plate.Rgba[o + 3] / 255f;
            ar = (ar * (1f - pa)) + (plate.Rgba[o] * pa);
            ag = (ag * (1f - pa)) + (plate.Rgba[o + 1] * pa);
            ab = (ab * (1f - pa)) + (plate.Rgba[o + 2] * pa);
            aa = (aa * (1f - pa)) + pa;

            // Premultiplied through, so a half-covered edge texel keeps its own colour rather
            // than darkening towards the transparent ground it was accumulated over.
            float scale = aa > 0.0001f ? 1f / aa : 0f;
            rgba[o] = Round(ar * scale);
            rgba[o + 1] = Round(ag * scale);
            rgba[o + 2] = Round(ab * scale);
            rgba[o + 3] = Round(aa * 255f);
        }

        return rgba;
    }

    private static byte Round(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);

    // The art seam speaks TgaImage, which has no factory for pixels a page composed itself and
    // is not this item's file to extend: wrapping the composite in a 32-bit top-down TGA is the
    // way in, and it runs the same decoder the blueprints already come through.
    private static TgaImage? AsImage(int width, int height, byte[] rgba)
    {
        var tga = new byte[18 + rgba.Length];
        tga[2] = 2;                                  // uncompressed truecolour
        tga[12] = (byte)(width & 0xff);
        tga[13] = (byte)(width >> 8);
        tga[14] = (byte)(height & 0xff);
        tga[15] = (byte)(height >> 8);
        tga[16] = 32;
        tga[17] = 0x28;                              // 8 alpha bits, top-down rows
        for (int p = 0; p < rgba.Length; p += 4)
        {
            tga[18 + p] = rgba[p + 2];
            tga[18 + p + 1] = rgba[p + 1];
            tga[18 + p + 2] = rgba[p];
            tga[18 + p + 3] = rgba[p + 3];
        }

        return TgaImage.Decode(tga);
    }

    private static string SlotName(int slot) => slot switch
    {
        0 => "Paint 1 (body)",
        1 => "Paint 2 (dark trim)",
        _ => "Paint 3 (light trim)",
    };

    private static string DecalSlotName(int row) => row switch
    {
        NoseDecalRow => "Nose decal",
        NoseDecalRow + 1 => "Tail decal",
        _ => "Wing decal",
    };

    // Rows 1-6 are colour, shade, colour, shade, colour, shade: each slot's pair together, the
    // way the original's screen pairs its two dropdowns per slot.
    private static bool IsShadeRow(int row) => row % 2 == 0;

    private static int SlotOf(int row) => (row - 1) / 2;

    private static int Wrap(int at, int dir, int count) => ((at + dir) % count + count) % count;

    private int ShadeCount(int slot) => Math.Max(1, Tables.ShadeCount(Scratch.PaintColours[slot]));

    private int DecalOf(int row) => row switch
    {
        NoseDecalRow => Scratch.NoseDecal,
        NoseDecalRow + 1 => Scratch.TailDecal,
        _ => Scratch.WingDecal,
    };

    private string DecalLabel(int decal)
    {
        if (decal < 0)
        {
            return "none (shipped placeholder)";
        }

        string name = Tables.DecalName(decal);
        return name.Length == 0 ? $"Decal {decal:00}" : name;
    }

    // The dropdown's own label (langui 3425 + index); the internal name, which is also the
    // pattern's archive folder, stands in when the string table is missing.
    private string PatternLabel(int pattern)
    {
        string name = PatternName(pattern);
        return Flow.Strings.Text(
            PatternStringBase + pattern,
            name.Length == 0 ? $"pattern {pattern}" : name.ToUpperInvariant());
    }

    // The patterns this airframe may wear, as record indices, from the pattern table's own
    // availability masks, cached per airframe. With no table loaded every pattern is offered,
    // which is the hangar's missing-data idiom rather than an empty screen.
    private List<int> Roster()
    {
        int airframe = Math.Clamp(Scratch.Airframe, 0, SkinPrefixes.Length - 1);
        if (_rosters.TryGetValue(airframe, out var cached))
        {
            return cached;
        }

        var roster = new List<int>();
        for (int pattern = 0; pattern < HangarPaintTables.PatternCount; pattern++)
        {
            if (Tables.Available(pattern, airframe))
            {
                roster.Add(pattern);
            }
        }

        _rosters[airframe] = roster;
        return roster;
    }

    // Selecting a pattern copies its six colour/shade defaults over the plane's own and touches
    // nothing else, which is all the original's SET handler does.
    private bool StepPattern(int dir)
    {
        var roster = Roster();
        if (roster.Count == 0)
        {
            return false;
        }

        int at = roster.IndexOf(Scratch.PaintPattern);
        int next = at < 0 ? (dir > 0 ? 0 : roster.Count - 1) : Wrap(at, dir, roster.Count);
        if (roster[next] == Scratch.PaintPattern)
        {
            return false;
        }

        Scratch.LoadPatternDefaults(roster[next]);
        return true;
    }

    private bool StepColour(int slot, int dir)
    {
        int count = Math.Max(1, Tables.Swatches.Count);
        int next = Wrap(Scratch.PaintColours[slot], dir, count);
        if (next == Scratch.PaintColours[slot])
        {
            return false;
        }

        Scratch.SetPaintColour(slot, next);
        return true;
    }

    private bool StepShade(int slot, int dir)
    {
        int count = ShadeCount(slot);
        int next = Wrap(Scratch.PaintShades[slot], dir, count);
        if (next == Scratch.PaintShades[slot])
        {
            return false;
        }

        Scratch.PaintShades[slot] = next;
        return true;
    }

    // A fresh build's slot holds the keep-the-placeholder sentinel, which the original cannot
    // store: the first step enters the 0-49 set, and the cycle stays inside it after that.
    private bool StepDecal(int row, int dir)
    {
        int current = DecalOf(row);
        int next = current < 0
            ? (dir > 0 ? 0 : HangarPaintTables.DecalCount - 1)
            : Wrap(current, dir, HangarPaintTables.DecalCount);
        if (next == current)
        {
            return false;
        }

        switch (row)
        {
            case NoseDecalRow: Scratch.NoseDecal = next; break;
            case NoseDecalRow + 1: Scratch.TailDecal = next; break;
            default: Scratch.WingDecal = next; break;
        }

        return true;
    }

    // The scratch plane's paint on its own airframe, composed from that pair's icon layer set.
    // Null when there is no extraction, and for the one pair that ships no set (itstaxi on the
    // Hoplite), where the icon fallback stands in.
    private HangarArt? LivePreview()
    {
        int airframe = Math.Clamp(Scratch.Airframe, 0, SkinPrefixes.Length - 1);
        if (IconSet(airframe, Scratch.PaintPattern) is not { } set)
        {
            return null;
        }

        var rgba = Compose(set, Scratch.Colour1, Scratch.Colour2, Scratch.Colour3);
        return AsImage(set.Plate.Width, set.Plate.Height, rgba) is { } image
            ? new HangarArt(image, $"{PatternLabel(Scratch.PaintPattern)}   {Flow.AirframeName(airframe)}")
            : null;
    }

    // The pattern's own plan view of this airframe unpainted, for the one pair with no set of its
    // own: pattern 4 ships a set for all eleven aircraft, so its plate is the stand-in.
    private HangarArt? IconFor(int pattern)
    {
        int airframe = Math.Clamp(Scratch.Airframe, 0, SkinPrefixes.Length - 1);
        var set = IconSet(airframe, 4);
        return set == null
            ? null
            : new HangarArt(set.Plate, $"{PatternLabel(pattern)}   {Flow.AirframeName(airframe)}");
    }

    // One airframe/pattern icon set, decoded once and kept, misses included, so an absent
    // extraction is probed once per pair rather than once per frame.
    private PaintIcons? IconSet(int airframe, int pattern)
    {
        int key = (airframe * 100) + pattern;
        if (_iconSets.TryGetValue(key, out var cached))
        {
            return cached;
        }

        PaintIcons? set = null;
        if (Flow.DataRoot is { } root)
        {
            var graphics = Path.Combine(root, "extracted", "rof", "ASSETS", "GRAPHICS");
            var layers = new TgaImage?[4];
            for (int layer = 0; layer < layers.Length; layer++)
            {
                layers[layer] = TgaImage.TryLoad(
                    Path.Combine(graphics, $"PX_ICON_{airframe}_{pattern}_{layer}.TGA"));
            }

            set = PaintIcons.From(layers);
        }

        _iconSets[key] = set;
        return set;
    }

    // One decal's own tile out of the shipped sheet: 50 tiles of 66x66 stacked top to bottom in
    // index order, which is the 5-wide grid the original's picker lays out read row by row.
    private HangarArt? DecalArt(int decal)
    {
        if (decal < 0 || decal >= HangarPaintTables.DecalCount)
        {
            return null;
        }

        if (_decalArt.TryGetValue(decal, out var cached))
        {
            return cached;
        }

        HangarArt? art = null;
        if (DecalSheet() is { } sheet && sheet.Height >= HangarPaintTables.DecalCount * sheet.Width)
        {
            int side = sheet.Width;
            var rgba = new byte[side * side * 4];
            Array.Copy(sheet.Rgba, decal * side * side * 4, rgba, 0, rgba.Length);
            if (AsImage(side, side, rgba) is { } image)
            {
                art = new HangarArt(image, DecalLabel(decal));
            }
        }

        _decalArt[decal] = art;
        return art;
    }

    private TgaImage? DecalSheet()
    {
        if (!_decalSheetTried)
        {
            _decalSheetTried = true;
            _decalSheet = Flow.DataRoot is { } root
                ? TgaImage.TryLoad(Path.Combine(root, "extracted", "rof", "ASSETS", "GRAPHICS",
                    "PX_P_DECALS.TGA"))
                : null;
        }

        return _decalSheet;
    }
}

/// <summary>
/// One airframe/pattern pair's paint-screen artwork as the original ships it:
/// <c>PX_ICON_&lt;airframe&gt;_&lt;pattern&gt;_0..3.TGA</c>, four same-sized 32-bit plan views of
/// the aircraft. Layer 0 is the detail plate (panel lines, canopy, propeller, guns) with the
/// coverage in its alpha; layers 1-3 are the three paint slots' region masks, white RGB with the
/// region in the alpha. The set is exactly the pattern-availability mask: every pair the mask
/// allows ships one, and only itstaxi on the Hoplite does not.
/// </summary>
public sealed record PaintIcons(TgaImage Plate, TgaImage[] Masks)
{
    /// <summary>The set four decoded layers make, or null when any is missing or a different size
    /// from the plate. A half-read set would compose a plane out of two different aircraft.</summary>
    public static PaintIcons? From(IReadOnlyList<TgaImage?> layers)
    {
        if (layers.Count != 4 || layers[0] is not { } plate)
        {
            return null;
        }

        var masks = new TgaImage[3];
        for (int i = 0; i < masks.Length; i++)
        {
            if (layers[i + 1] is not { } mask
                || mask.Width != plate.Width || mask.Height != plate.Height)
            {
                return null;
            }

            masks[i] = mask;
        }

        return new PaintIcons(plate, masks);
    }
}
