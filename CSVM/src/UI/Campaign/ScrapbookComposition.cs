using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;

namespace CSVM.UI.Campaign;

/// <summary>One scrap authored on a scrapbook spread: <c>extracted\rof\ASSETS\SCRAPBOOK.CSV</c>'s
/// own columns (<c>docs/formats/campaign-screens.md#scrapbookcsv</c>). <see cref="Extension"/> is
/// resolved from <c>ImageType</c>'s first letter (<c>B</c> BMP, <c>J</c> JPG, else PNG).</summary>
public readonly record struct ScrapbookScrap(
    int Objective, string ImageName, string Extension, float X, float Y, int DrawOrder,
    char Zoom, float ZoomX, float ZoomY, string CaptionKey, string TitleKey, string TextKey)
{
    /// <summary>The row's own 1-based item ordinal within its spread, for re-finding this scrap
    /// later (<see cref="ScrapbookComposition.Items"/> is re-read fresh rather than cached by a
    /// caller).</summary>
    public int Item { get; init; }

    /// <summary>A player capture rather than shipped art: resolved against the profile directory,
    /// not <c>assets\graphics\</c>, and skipped when the file is not on disk
    /// (<c>docs/org/debrief.md#navigation-and-page-composition-a-data-file-not-code</c>).</summary>
    public bool IsCapture => ImageName.StartsWith("Snap_", StringComparison.Ordinal);

    /// <summary>The capture's own file name, for the profile-directory lookup
    /// <see cref="IsCapture"/> callers make.</summary>
    public string FileName => $"{ImageName}.{Extension}";

    /// <summary>Whether this scrap opens into a detail view at all: the <c>Zoom</c> column is a
    /// real family letter rather than <c>0</c>. Every photo-corner mount (<c>DZ_generic_corners</c>,
    /// 167 rows) carries <c>Zoom=0</c> and never opens; every capture and every ordinary scrap that
    /// authors a family letter does.</summary>
    public bool Opens => Zoom != '0';

    /// <summary>The zoom inset's own extension, resolved the same three-way way the page image's
    /// is (<c>ImageType</c>'s second letter), meaningful only while <see cref="Opens"/>.</summary>
    public string ZoomExtension { get; init; }

    /// <summary>Whether the zoom view draws an inset image at all. <c>ImageType</c>'s second letter
    /// <c>0</c> names none, which is every text scrap: the view is then the family's background and
    /// the words alone, with nothing for EXPORT TO DESKTOP to copy
    /// (<c>docs/formats/campaign-screens.md#resolving-a-row-to-a-file</c>).</summary>
    public bool HasZoomInset { get; init; } = true;

    /// <summary>The inset image's file name for the zoom view.</summary>
    public string ZoomFileName => $"{ImageName}.{ZoomExtension}";

    /// <summary>The row's own clickable region, the quoted <c>Left,Top,Right,Bottom</c> column, as
    /// an authored rectangle; null when the row authors <c>0,0,0,0</c>, which a pointer-driven
    /// presentation then answers with the picture's own bounds.</summary>
    public (float X, float Y, float Width, float Height)? Region { get; init; }
}

/// <summary>One zoom family's three text boxes (title, caption, body), <c>LAYOUT.CSV</c>'s
/// <c>SBZ_T_TITLE&lt;letter&gt;</c>/<c>CAPTION&lt;letter&gt;</c>/<c>TEXT&lt;letter&gt;</c> rows: each
/// box's authored top-left, wrap width and height. The height is what a block too tall for its box
/// is fitted to. The font-index column is not carried. The colour column is read by the page
/// rather than here, two of the 26 families' colour fields being typo'd
/// (<c>docs/formats/campaign-screens.md#resolving-a-row-to-a-file</c>).</summary>
public readonly record struct ScrapbookZoomFamily(
    float TitleX, float TitleY, float TitleWidth, float TitleHeight,
    float CaptionX, float CaptionY, float CaptionWidth, float CaptionHeight,
    float TextX, float TextY, float TextWidth, float TextHeight);

/// <summary>
/// The scrapbook's per-spread scrap layout, read from the shipped <c>SCRAPBOOK.CSV</c> rather than
/// invented: one <c>[SCRAPBOOK]</c> section keyed <c>&lt;mission&gt;_&lt;spread&gt;_&lt;item&gt;</c>,
/// enumerated upward from item 1 and stopped at the first missing key, the way the original's own
/// reader does (<c>docs/org/debrief.md#navigation-and-page-composition-a-data-file-not-code</c>).
/// Whether a scrap opens into a detail view is the <c>Zoom</c> column alone
/// (<see cref="ScrapbookScrap.Opens"/>), not <c>ImageType</c>'s second letter
/// (<c>docs/formats/campaign-screens.md</c>, "The scrapbook"). Parsed rows are cached per file
/// path, misses included (<see cref="Hangar.PlaneDiagrams"/>'s own precedent).
/// </summary>
public static class ScrapbookComposition
{
    // How far the original shifts a capture off its authored position before laying the smudge
    // over it, and the ten-frame smudge strip itself.
    private const float CaptureNudgeX = 3f;
    private const float CaptureNudgeY = 4f;

    // The region a capture is forced into on the page, which is the grime strip's own frame size.
    // ⚠ Do not draw the print at its file's own size; the smudge is cut for this rectangle and
    // lands beside the print otherwise (docs/formats/campaign-screens.md, "The danger-zone slot").
    private const float CaptureWidth = 164f;
    private const float CaptureHeight = 123f;

    // What the pointer does to the scrap it is over, SCRAPBOOK.SCRIPT's own 10002 handler: two
    // percent onto the scrap's authored scale, and z + 1000 so it stands over its neighbours. 10001
    // puts both back. A capture's grime frame goes with it, scaled 100 to 102 in the same handler.
    private const float HoverGrow = 1.02f;

    private static readonly BoardArt GrimeArt = new(BoardArtLibrary.Ui, "SB_P_Grime.Png", 10);

    // One gate over all three memos. A menu page reads them on the UI thread, but xUnit runs test
    // classes in parallel, and a Dictionary insert racing a read corrupts it. Held across the file
    // read as well, which CampaignLayout does too, so a racing double-load cannot happen.
    private static readonly object Gate = new();

    private static readonly Dictionary<string, Dictionary<string, string[]>?> Files =
        new(StringComparer.Ordinal);

    private static readonly Dictionary<string, Dictionary<char, ScrapbookZoomFamily>?> ZoomFamilies =
        new(StringComparer.Ordinal);

    private static readonly Dictionary<string, Dictionary<string, int>?> Symbols =
        new(StringComparer.Ordinal);

    /// <summary>The mission slot's spread, in item order (not draw order): the raw rows, with no
    /// objective gate or capture check applied. Empty when <paramref name="dataRoot"/> is null, the
    /// extraction lacks the file, or the spread has no item 1.</summary>
    public static IReadOnlyList<ScrapbookScrap> Items(string? dataRoot, int mission, int spread)
    {
        var rows = dataRoot == null ? null : Load(Path.Combine(
            dataRoot, "extracted", "rof", "ASSETS", "SCRAPBOOK.CSV"));
        if (rows == null)
        {
            return Array.Empty<ScrapbookScrap>();
        }

        var items = new List<ScrapbookScrap>();
        for (int item = 1; ; item++)
        {
            if (!rows.TryGetValue($"{mission}_{spread}_{item}", out var fields))
            {
                break;
            }

            items.Add(Parse(fields) with { Item = item });
        }

        return items;
    }

    /// <summary>The spread's scraps as drawable pictures, gated and ordered the way the original
    /// draws them: the <c>Objective</c> gate against <paramref name="bestMask"/> (the merged
    /// best-to-date mask), a capture skipped when <paramref name="capturePath"/> returns null for
    /// it, the survivors stacked by ascending <c>DrawOrder</c>, and a capture nudged off its
    /// authored position under a <c>SB_P_Grime</c> frame. <paramref name="focusedItem"/> is the
    /// item the cursor is on, which draws last and two percent bigger.</summary>
    public static IReadOnlyList<BoardPicture> Pictures(
        string? dataRoot, int mission, int spread, int bestMask,
        Func<ScrapbookScrap, string?> capturePath, int focusedItem = -1, bool revealAll = false)
    {
        var visible = Filtered(dataRoot, mission, spread, bestMask, capturePath, revealAll);
        visible.Sort((a, b) => a.DrawOrder.CompareTo(b.DrawOrder));

        var grime = new ScrapbookGrime(mission, spread);
        var pictures = new List<BoardPicture>(visible.Count);
        int first = -1;
        int count = 0;
        foreach (var scrap in visible)
        {
            int at = pictures.Count;
            if (!scrap.IsCapture)
            {
                pictures.Add(new BoardPicture(
                    new BoardArt(BoardArtLibrary.Ui, $"SCRAPBOOK/{scrap.FileName}"), scrap.X, scrap.Y));
            }
            else
            {
                float x = scrap.X + CaptureNudgeX;
                float y = scrap.Y + CaptureNudgeY;
                pictures.Add(new BoardPicture(
                    new BoardArt(BoardArtLibrary.Loose, capturePath(scrap) ?? string.Empty), x, y,
                    Width: CaptureWidth, Height: CaptureHeight));
                pictures.Add(new BoardPicture(GrimeArt, x, y, grime.Next(pictures.Count)));
            }

            if (scrap.Item == focusedItem)
            {
                (first, count) = (at, pictures.Count - at);
            }
        }

        return first < 0 ? pictures : Lift(pictures, first, count);
    }

    /// <summary>The spread's scraps a player can open into detail, in item order: the same gate
    /// <see cref="Pictures"/> applies, narrowed to <see cref="ScrapbookScrap.Opens"/>.</summary>
    public static IReadOnlyList<ScrapbookScrap> Openable(
        string? dataRoot, int mission, int spread, int bestMask,
        Func<ScrapbookScrap, string?> capturePath, bool revealAll = false)
    {
        var visible = Filtered(dataRoot, mission, spread, bestMask, capturePath, revealAll);
        visible.RemoveAll(s => !s.Opens);
        return visible;
    }

    /// <summary>The <c>Objective</c> column's own gate: 0 always draws; any other value first
    /// requires bit 0 of <paramref name="bestMask"/> (the mission won at least once), then requires
    /// its own bit set for a positive value or clear for a negative one
    /// (<c>docs/formats/campaign-screens.md#the-objective-gate</c>). The whole test is skipped
    /// while <paramref name="revealAll"/> stands, which is how the original's <c>fViewAll</c>
    /// guards it in <c>FUN_004061d0</c>.</summary>
    public static bool Visible(int objective, int bestMask, bool revealAll = false)
    {
        if (objective == 0 || revealAll)
        {
            return true;
        }

        if ((bestMask & CampaignProgression.PrimaryObjectiveMask) == 0)
        {
            return false;
        }

        int bit = 1 << Math.Abs(objective);
        return objective > 0 ? (bestMask & bit) != 0 : (bestMask & bit) == 0;
    }

    /// <summary>The langui id a scrap's <c>TitleResID</c>/<c>TextResID</c> symbol stands for, or
    /// null for the CSV's own <c>0</c> sentinel and for a symbol the header does not carry.
    /// <c>SCRAPBOOK.CSV</c> names those strings by symbol and <c>ASSETS\SCRIPTS\RESRC1.H</c> is the
    /// <c>#define</c> table turning one into the id <c>ui_strings.json</c> holds the text under, so
    /// the letters and clippings resolve to the words the original prints on them.</summary>
    public static int? StringId(string? dataRoot, string symbol)
    {
        if (symbol.Length == 0 || symbol == "0" || dataRoot == null)
        {
            return null;
        }

        var symbols = LoadSymbols(Path.Combine(
            dataRoot, "extracted", "rof", "ASSETS", "SCRIPTS", "RESRC1.H"));
        return symbols != null && symbols.TryGetValue(symbol, out int id) ? id : null;
    }

    /// <summary>One zoom family's three text boxes, or null when the letter carries none (no such
    /// family, or the extraction lacks <c>LAYOUT.CSV</c>). Cached per file path, misses
    /// included.</summary>
    public static ScrapbookZoomFamily? ZoomFamily(string? dataRoot, char letter)
    {
        var families = dataRoot == null ? null : LoadZoomFamilies(Path.Combine(
            dataRoot, "extracted", "rof", "ASSETS", "LAYOUT.CSV"));
        return families != null && families.TryGetValue(letter, out var family) ? family : null;
    }

    // The focused scrap's own pictures (a capture's grime among them) moved to the end of the list
    // and grown. Reordered after the whole page is composed rather than while it is: the grime
    // generator walks its ten frames by picture index, so a scrap taken out of the list mid-compose
    // would re-roll every capture's smudge behind it as the cursor moved.
    private static List<BoardPicture> Lift(List<BoardPicture> pictures, int first, int count)
    {
        var lifted = pictures.GetRange(first, count).ConvertAll(p => p with { Scale = HoverGrow });
        pictures.RemoveRange(first, count);
        pictures.AddRange(lifted);
        return pictures;
    }

    // RESRC1.H's own `#define <symbol> <decimal>` lines, symbol -> id. The authoring tool's
    // _APS_NEXT_* bookkeeping parses as one too and is kept: no scrap row names it, so filtering it
    // out would buy nothing. Cached per file path, misses included.
    private static Dictionary<string, int>? LoadSymbols(string path)
    {
        lock (Gate)
        {
            if (Symbols.TryGetValue(path, out var cached))
            {
                return cached;
            }

            Dictionary<string, int>? symbols = null;
            if (File.Exists(path))
            {
                symbols = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var line in File.ReadAllLines(path))
                {
                    var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length >= 3 && fields[0] == "#define"
                        && int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                    {
                        symbols[fields[1]] = id;
                    }
                }
            }

            Symbols[path] = symbols;
            return symbols;
        }
    }

    private static List<ScrapbookScrap> Filtered(
        string? dataRoot, int mission, int spread, int bestMask,
        Func<ScrapbookScrap, string?> capturePath, bool revealAll)
    {
        var visible = new List<ScrapbookScrap>();
        foreach (var scrap in Items(dataRoot, mission, spread))
        {
            if (!Visible(scrap.Objective, bestMask, revealAll))
            {
                continue;
            }

            if (scrap.IsCapture && capturePath(scrap) == null)
            {
                continue;
            }

            visible.Add(scrap);
        }

        return visible;
    }

    private static ScrapbookScrap Parse(string[] fields)
    {
        int objective = int.Parse(fields[0].Trim(), CultureInfo.InvariantCulture);
        string caption = fields[1].Trim();
        string imageName = fields[2].Trim();
        string imageType = fields[3].Trim();
        float x = float.Parse(fields[4].Trim(), CultureInfo.InvariantCulture);
        float y = float.Parse(fields[5].Trim(), CultureInfo.InvariantCulture);
        int drawOrder = int.Parse(fields[9].Trim(), CultureInfo.InvariantCulture);
        char zoom = fields[11].Trim() is { Length: 1 } letter ? letter[0] : '0';
        float zoomX = ParseFloat(fields[12]);
        float zoomY = ParseFloat(fields[13]);
        string title = fields[14].Trim();
        string text = fields[15].Trim();

        return new ScrapbookScrap(
            objective, imageName, ExtensionOf(imageType, 0), x, y, drawOrder,
            zoom, zoomX, zoomY, caption, title, text)
        {
            ZoomExtension = ExtensionOf(imageType, 1),
            HasZoomInset = !(imageType.Length > 1 && imageType[1] == '0'),
            Region = ParseRegion(fields[10]),
        };
    }

    // The clickable region as Left,Top,Right,Bottom; an all-zero or malformed field is no region.
    private static (float X, float Y, float Width, float Height)? ParseRegion(string field)
    {
        var parts = field.Split(',');
        if (parts.Length != 4)
        {
            return null;
        }

        var edges = new float[4];
        for (int i = 0; i < 4; i++)
        {
            edges[i] = ParseFloat(parts[i]);
        }

        if (edges[2] <= edges[0] || edges[3] <= edges[1])
        {
            return null;
        }

        return (edges[0], edges[1], edges[2] - edges[0], edges[3] - edges[1]);
    }

    // 7_1_2 (SB_07_01_ilsanote) ships ZoomX/ZoomY blank despite a real Zoom letter; not traced
    // further, so a blank field reads as 0 rather than throwing.
    private static float ParseFloat(string field) =>
        float.TryParse(field.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : 0f;

    // ImageType's letter at `index` (0 page, 1 zoom inset): B/b -> BMP, J/j -> JPG, anything else
    // (including a missing character) -> PNG, matching docs/formats/campaign-screens.md's rule.
    private static string ExtensionOf(string imageType, int index)
    {
        char letter = imageType.Length > index ? imageType[index] : '\0';
        return letter switch
        {
            'B' or 'b' => "BMP",
            'J' or 'j' => "JPG",
            _ => "PNG",
        };
    }

    // The [SCRAPBOOK] section as a flat key->fields map: comment (';') and section ('[') lines
    // skipped, everything else split on the key's '=' then the value's ',' -- quote-aware, since
    // the one quoted field (the clickable region, unread here) sits before the Zoom/ZoomX/ZoomY/
    // TitleResID/TextResID columns and would misalign every naive split past it.
    private static Dictionary<string, string[]>? Load(string path)
    {
        lock (Gate)
        {
            if (Files.TryGetValue(path, out var cached))
            {
                return cached;
            }

            Dictionary<string, string[]>? rows = null;
            if (File.Exists(path))
            {
                rows = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var line in File.ReadAllLines(path))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '[')
                    {
                        continue;
                    }

                    int eq = trimmed.IndexOf('=');
                    if (eq < 0)
                    {
                        continue;
                    }

                    rows[trimmed[..eq]] = SplitRespectingQuotes(trimmed[(eq + 1)..]);
                }
            }

            Files[path] = rows;
            return rows;
        }
    }

    // A comma split that treats one "..." run as a single field, unquoted -- this file's shape,
    // not general CSV (no escaped quotes inside a quoted field).
    private static string[] SplitRespectingQuotes(string value)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in value)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    // LAYOUT.CSV's SBZ_T_TITLE<letter>/CAPTION<letter>/TEXT<letter> rows, `Name=T,!,X,Y,?,Width,
    // Height,Colour,FontIndex`. X, Y, Width and Height are read. Two families' colour fields are
    // typo'd and would not parse. The colour is therefore read per row by the page that draws it
    // rather than here (docs/formats/campaign-screens.md, "Resolving a row to a file").
    private static Dictionary<char, ScrapbookZoomFamily>? LoadZoomFamilies(string path)
    {
        lock (Gate)
        {
            if (ZoomFamilies.TryGetValue(path, out var cached))
            {
                return cached;
            }

            Dictionary<char, ScrapbookZoomFamily>? families = null;
            if (File.Exists(path))
            {
                var titles = new Dictionary<char, (float X, float Y, float W, float H)>();
                var captions = new Dictionary<char, (float X, float Y, float W, float H)>();
                var texts = new Dictionary<char, (float X, float Y, float W, float H)>();
                foreach (var line in File.ReadAllLines(path))
                {
                    string trimmed = line.Trim();
                    int eq = trimmed.IndexOf('=');
                    if (eq < 0)
                    {
                        continue;
                    }

                    string key = trimmed[..eq].TrimEnd();
                    Dictionary<char, (float X, float Y, float W, float H)>? target = key switch
                    {
                        _ when key.StartsWith("SBZ_T_TITLE", StringComparison.Ordinal) => titles,
                        _ when key.StartsWith("SBZ_T_CAPTION", StringComparison.Ordinal) => captions,
                        _ when key.StartsWith("SBZ_T_TEXT", StringComparison.Ordinal) => texts,
                        _ => null,
                    };
                    if (target == null || key[^1] is < 'A' or > 'Z')
                    {
                        continue;
                    }

                    var fields = trimmed[(eq + 1)..].Split(',');
                    if (fields.Length < 7)
                    {
                        continue;
                    }

                    target[key[^1]] = (
                        float.Parse(fields[2].Trim(), CultureInfo.InvariantCulture),
                        float.Parse(fields[3].Trim(), CultureInfo.InvariantCulture),
                        float.Parse(fields[5].Trim(), CultureInfo.InvariantCulture),
                        float.Parse(fields[6].Trim(), CultureInfo.InvariantCulture));
                }

                families = new Dictionary<char, ScrapbookZoomFamily>();
                foreach (var letter in titles.Keys)
                {
                    if (!captions.TryGetValue(letter, out var caption) || !texts.TryGetValue(letter, out var text))
                    {
                        continue;
                    }

                    var title = titles[letter];
                    families[letter] = new ScrapbookZoomFamily(
                        title.X, title.Y, title.W, title.H, caption.X, caption.Y, caption.W, caption.H,
                        text.X, text.Y, text.W, text.H);
                }
            }

            ZoomFamilies[path] = families;
            return families;
        }
    }
}

/// <summary>
/// The smudge overlay's frame picker, <c>uiData</c> 2413: seeded from the page it dirties and
/// handing out each of <c>SB_P_Grime.Png</c>'s ten frames once before repeating, so a page's grime
/// is the same every time it is turned to and different from its neighbours'
/// (<c>docs/formats/campaign-screens.md#the-grime</c>). The original reseeds from the clock once
/// the page is composed; nothing here needs that, since a frame is only ever asked for while a page
/// is being composed.
/// </summary>
public sealed class ScrapbookGrime
{
    private const int Frames = 10;

    private readonly int _seed;
    private int _used;

    /// <summary>Seeds the generator for one spread, the original's <c>(mission &lt;&lt; 8) | spread</c>.</summary>
    public ScrapbookGrime(int mission, int spread) => _seed = (mission << 8) | spread;

    /// <summary>The frame for the <paramref name="index"/>-th grimed scrap of this page, 0 to 9,
    /// each taken once before the ten are offered again.</summary>
    public int Next(int index)
    {
        if (_used == (1 << Frames) - 1)
        {
            _used = 0;
        }

        // A fixed walk from a page-derived start rather than a real PRNG: the original's own
        // generator hashes the seed with the draw ordinal, and what matters here is only that a
        // page is stable and its neighbours differ.
        int start = ((_seed * 31) + index) % Frames;
        for (int step = 0; step < Frames; step++)
        {
            int frame = (start + step) % Frames;
            if ((_used & (1 << frame)) == 0)
            {
                _used |= 1 << frame;
                return frame;
            }
        }

        return 0;
    }
}
