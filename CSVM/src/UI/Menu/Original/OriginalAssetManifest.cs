using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CSVM.Extraction;

namespace CSVM.UI.Menu.Original;

/// <summary>Whether Original can compose the screen that names a file without that file.</summary>
public enum OriginalAssetNeed
{
    /// <summary>A screen Original composes cannot be drawn without it.</summary>
    Required,

    /// <summary>Its absence degrades one screen locally and leaves it usable.</summary>
    Optional,
}

/// <summary>
/// One file the manifest classifies: the name the layout or a script writes, where it sits under
/// the extraction tree, the need it falls under, and the section and row that named it. The
/// section and row are what makes a failure actionable, so they travel with the entry.
/// </summary>
public sealed record OriginalAsset(
    string Name, string RelativePath, OriginalAssetNeed Need, string Section, string Row, string Note)
{
    /// <summary>Where the file sits under a data root, in the case the extraction writes it.</summary>
    public string PathUnder(string dataRoot) => RofTree.Under(dataRoot, RelativePath);
}

/// <summary>One entry the check refused, and what is wrong with it.</summary>
public sealed record OriginalAssetFault(OriginalAsset Asset, string Trouble)
{
    /// <summary>The entry as one line of a reason string.</summary>
    public override string ToString() => $"{Asset.Section}.{Asset.Row} names {Asset.Name} ({Trouble})";
}

/// <summary>
/// What one check of a manifest against a data root found: the faults among the required entries,
/// which refuse Original for the run, and among the optional ones, which the presentation draws
/// without. Both lists carry every entry that failed, so a player reads the whole list once
/// instead of discovering it one blank screen at a time.
/// </summary>
public sealed class OriginalAssetReport
{
    private const int Listed = 6;

    internal OriginalAssetReport(
        int requiredCount, int optionalCount,
        IReadOnlyList<OriginalAssetFault> requiredFaults, IReadOnlyList<OriginalAssetFault> optionalFaults)
    {
        RequiredCount = requiredCount;
        OptionalCount = optionalCount;
        RequiredFaults = requiredFaults;
        OptionalFaults = optionalFaults;
    }

    /// <summary>The manifest schema this report was made under.</summary>
    public int Schema => OriginalAssetManifest.Schema;

    /// <summary>How many required entries were checked.</summary>
    public int RequiredCount { get; }

    /// <summary>How many optional entries were checked.</summary>
    public int OptionalCount { get; }

    /// <summary>The required entries that are missing or do not read.</summary>
    public IReadOnlyList<OriginalAssetFault> RequiredFaults { get; }

    /// <summary>The optional entries that are missing.</summary>
    public IReadOnlyList<OriginalAssetFault> OptionalFaults { get; }

    /// <summary>Whether every required entry is on disk and reads.</summary>
    public bool Complete => RequiredFaults.Count == 0;

    /// <summary>Why Original cannot run over this tree, or null when it can.</summary>
    public string? Reason => Complete ? null
        : $"the Original asset manifest (schema {Schema}) refuses {RequiredFaults.Count} of {RequiredCount} required files: {Join(RequiredFaults)}";

    /// <summary>What Original will draw without, or null when nothing optional is missing.</summary>
    public string? Degraded => OptionalFaults.Count == 0 ? null
        : $"{OptionalFaults.Count} of {OptionalCount} optional files are not there and are drawn without: {Join(OptionalFaults)}";

    private static string Join(IReadOnlyList<OriginalAssetFault> faults)
    {
        var text = new StringBuilder();
        for (int i = 0; i < faults.Count && i < Listed; i++)
        {
            text.Append(i == 0 ? string.Empty : "; ").Append(faults[i]);
        }

        if (faults.Count > Listed)
        {
            text.Append("; and ").Append(faults.Count - Listed).Append(" more");
        }

        return text.ToString();
    }
}

/// <summary>
/// The versioned required/optional manifest of the files Original draws from, derived from the
/// decoded layout rather than hand-listed: every art name of the sections Original composes is
/// required, the rows it does not compose and every other section's art are optional, and the
/// three files the scripts name outside the layout (the two pointers and the font) are required.
/// <see cref="Check"/> validates structure without decoding a bitmap. The manifest's own reader
/// is <see cref="OriginalAvailability"/>; how it classifies is in <c>docs/org/menu-inventory.md</c>.
/// </summary>
public sealed class OriginalAssetManifest
{
    /// <summary>The manifest schema. Bump it when the derivation changes what Original needs: the
    /// composed-section table, the rows Original does not draw or draws optionally, the
    /// script-named files, or what <see cref="Check"/> accepts as a readable file.</summary>
    public const int Schema = 8;

    /// <summary>The extraction stamp schema Original refuses to read a tree below: the loaders'
    /// own expectation, which the decoded menu layout's first reader raised, so a tree extracted
    /// before that decode is refused with the re-extract instruction rather than read as empty.</summary>
    public const int StampSchema = CSVM.Session.Launch.ExtractionStamp.Schema;

    /// <summary>The layout sections Original composes screens from. Every other section's art is
    /// optional, since nothing Original draws reads it.</summary>
    public static readonly IReadOnlyList<string> ComposedSections = new[]
    {
        "MainMenu", "Preferences", "GameOptions", "Audio", "Video", "ControlsPrefs", "Keys", "Credits",
        "InstantAction", "IA_WrapUp", "Campaign",
        "PassengerCabin", "MomentoSelection", "FlightCheck", "PlaneSelection", "OrdinanceLayout", "ScrapBook", "ScrapBook_TOC",
        "ScrapbookZoom", "Hangar", "PlaneName", "PlaneConstruction", "AirFrame", "Engine", "Armor", "Guns",
        "HardPoints", "Paint", "Purchase", "MessageBox",
    };

    // The files the GUI scripts name outside the layout and Original draws anyway: the two pointer
    // bitmaps, the 3D font, and the plane construction screen's two export plaques, which the
    // script names and no layout row does. Their paths come from the layout's own external-asset
    // list; a layout that does not name one falls back to the graphics directory.
    private static readonly string[] ScriptNamed =
    {
        "activepointerz.png", "passivepointerz.png", "arial8.tga", "PX_B_ReadyToExport.png", "PX_B_CancelExport.png",
    };

    // Rows of a composed section whose art Original never draws, each with why. The keys are
    // "Section.Row", matched case-insensitively.
    private static readonly Dictionary<string, string> NotDrawn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Preferences.PF_B_RETURNTOGAME"] = "the in-flight way back, which the menu's Preferences page never offers",
        ["Audio.AP_B_MUSIC"] = "the In-Game Music checkbox, whose mute a slider that reaches zero already offers",
        ["Audio.AP_D_SQuality"] = "the Sound Quality tier, which no mixer this port runs on has an equivalent of",
        ["PassengerCabin.PC_B_SAVE"] = "SAVE GAME, deactivated in the original and never drawn",
        ["MessageBox.MP_P_BACKGROUND"] = "the multiplayer error box, which has no local counterpart",
        ["MessageBox.MP_B_LEFT"] = "the multiplayer error box, which has no local counterpart",
        ["MessageBox.MP_B_CENTER"] = "the multiplayer error box, which has no local counterpart",
        ["MessageBox.MP_B_RIGHT"] = "the multiplayer error box, which has no local counterpart",
        ["MessageBox.MA_B_LEFT"] = "the About box's unused answers, it being the one-button box",
        ["MessageBox.MA_B_RIGHT"] = "the About box's unused answers, it being the one-button box",
    };

    // Rows of a composed section Original does draw, but whose file it can be missing and still
    // draw the rest of that screen. Keyed and matched like NotDrawn above.
    private static readonly Dictionary<string, string> Degrades = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MainMenu.MOVIE"] = "the top level's backdrop movie, the screen behind it drawing whole without it",
        ["Preferences.MOVIE"] = "the page's backdrop movie, the page behind it drawing whole without it",
    };

    // The extensions of a file Original could draw or play. Anything else a script names (the
    // string DLL) is no concern of the manifest's, since nothing here loads it.
    private static readonly string[] Media = { ".png", ".jpg", ".jpeg", ".tga", ".tif", ".bm", ".mpg", ".wav" };

    private readonly List<OriginalAsset> _assets;
    private readonly Dictionary<string, int> _index;

    private OriginalAssetManifest(List<OriginalAsset> assets, Dictionary<string, int> index)
    {
        _assets = assets;
        _index = index;
        foreach (var asset in assets)
        {
            if (asset.Need == OriginalAssetNeed.Required)
            {
                RequiredCount++;
            }
            else
            {
                OptionalCount++;
            }
        }
    }

    /// <summary>Every classified file, first-named first, one entry per distinct name.</summary>
    public IReadOnlyList<OriginalAsset> Assets => _assets;

    /// <summary>How many files Original cannot draw a composed screen without.</summary>
    public int RequiredCount { get; }

    /// <summary>How many files it degrades over.</summary>
    public int OptionalCount { get; }

    /// <summary>Derives the manifest from a decoded layout. Reads no file.</summary>
    public static OriginalAssetManifest Derive(MenuLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var assets = new List<OriginalAsset>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var screen in layout.Screens)
        {
            bool composed = Composes(screen.Section);
            foreach (var widget in screen.Widgets)
            {
                string? why = composed ? Undrawn(screen.Section, widget.Key)
                    : $"[{screen.Section}] is not a section Original composes";
                var need = why == null ? OriginalAssetNeed.Required : OriginalAssetNeed.Optional;
                foreach (string art in widget.Art)
                {
                    Add(assets, index, new OriginalAsset(art, OriginalAvailability.RelativeArtPath(art), need,
                        screen.Section, widget.Key, why ?? "drawn by the section Original composes"));
                }
            }
        }

        foreach (string name in ScriptNamed)
        {
            Add(assets, index, new OriginalAsset(name, ScriptPath(layout, name), OriginalAssetNeed.Required,
                "GLOBALS", "script", "named by the scripts, drawn on every screen"));
        }

        foreach (var asset in layout.ExternalAssets)
        {
            if (asset.Kind != "file" || !asset.Path.Contains('/', StringComparison.Ordinal) || !IsMedia(asset.Path))
            {
                continue;
            }

            string name = asset.Path[(asset.Path.LastIndexOf('/') + 1)..];
            Add(assets, index, new OriginalAsset(name, RofTree.Canonical(asset.Path), OriginalAssetNeed.Optional,
                asset.Script, "script", "named by a script, drawn or played by no composed screen"));
        }

        return new OriginalAssetManifest(assets, index);
    }

    /// <summary>Whether Original composes any screen from a layout section.</summary>
    public static bool Composes(string section)
    {
        foreach (string name in ComposedSections)
        {
            if (string.Equals(name, section, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The entry for a file name, or null when the manifest does not classify it. A name
    /// a script assembles at runtime (a blueprint, a scrap, a paint mask) is one of those.</summary>
    public OriginalAsset? Find(string name) =>
        _index.TryGetValue(name, out int at) ? _assets[at] : null;

    /// <summary>Checks every entry against a data root: existence and a cheap structural read for
    /// the required ones, existence alone for the optional ones.</summary>
    public OriginalAssetReport Check(string dataRoot)
    {
        var required = new List<OriginalAssetFault>();
        var optional = new List<OriginalAssetFault>();
        foreach (var asset in _assets)
        {
            string path = asset.PathUnder(dataRoot);
            if (asset.Need == OriginalAssetNeed.Required)
            {
                if (Trouble(path, asset.Name) is { } trouble)
                {
                    required.Add(new OriginalAssetFault(asset, trouble));
                }
            }
            else if (!File.Exists(path))
            {
                optional.Add(new OriginalAssetFault(asset, "not there"));
            }
        }

        return new OriginalAssetReport(RequiredCount, OptionalCount, required, optional);
    }

    // Existence, then a PNG's own signature and IHDR size; any other extension only has to carry
    // bytes. ⚠ Do not decode the image here: this runs over every required file before entry, and
    // a whole-bitmap load per file is a start-up pass no player would wait through.
    private static string? Trouble(string path, string name)
    {
        try
        {
            if (!File.Exists(path))
            {
                return "not there";
            }

            using var stream = File.OpenRead(path);
            if (stream.Length == 0)
            {
                return "empty";
            }

            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var header = new byte[24];
            int read = 0;
            int got;
            while (read < header.Length && (got = stream.Read(header, read, header.Length - read)) > 0)
            {
                read += got;
            }

            if (read < header.Length)
            {
                return "shorter than a PNG header";
            }

            if (header[0] != 0x89 || header[1] != (byte)'P' || header[2] != (byte)'N' || header[3] != (byte)'G')
            {
                return "not a PNG";
            }

            int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return width > 0 && height > 0 ? null : "a PNG header with no size";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"unreadable: {e.GetType().Name}";
        }
    }

    // Why a composed section's row is optional: Original never draws it, or it draws it and the
    // screen survives its absence. Null for a row whose file the screen cannot be drawn without.
    private static string? Undrawn(string section, string key)
    {
        string row = $"{section}.{key}";
        return NotDrawn.TryGetValue(row, out var undrawn) ? undrawn
            : Degrades.TryGetValue(row, out var degrades) ? degrades : null;
    }

    private static bool IsMedia(string path)
    {
        foreach (string extension in Media)
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // The path a script-named file sits at, taken from the layout's own external-asset list so the
    // artifact stays the source of the name; the graphics directory is the fallback.
    private static string ScriptPath(MenuLayout layout, string name)
    {
        foreach (var asset in layout.ExternalAssets)
        {
            if (asset.Kind == "file" && asset.Path.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                return RofTree.Canonical(asset.Path);
            }
        }

        return RofTree.Canonical("ASSETS/GRAPHICS/" + name);
    }

    // One entry per distinct name, the first row that named it. A file a composed row and an
    // out-of-scope row both name is required: the composed row still cannot draw without it.
    private static void Add(List<OriginalAsset> assets, Dictionary<string, int> index, OriginalAsset asset)
    {
        if (index.TryGetValue(asset.Name, out int at))
        {
            if (assets[at].Need == OriginalAssetNeed.Optional && asset.Need == OriginalAssetNeed.Required)
            {
                assets[at] = asset;
            }

            return;
        }

        index[asset.Name] = assets.Count;
        assets.Add(asset);
    }
}
