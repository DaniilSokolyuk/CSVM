using System;
using System.Collections.Generic;
using CSVM.UI.Boards;
using CSVM.UI.Menu;
using CSVM.Utils;

namespace CSVM.UI.Campaign;

/// <summary>
/// The decoded menu layout as the campaign boards read it: one widget row's authored geometry
/// and art by section and key, with the value the board drew before the layout existed handed in
/// beside every read as the fallback. A board therefore composes the same pixels whether the
/// layout is present, absent or unreadable; what changes is where the number comes from. Loaded
/// once per data root and kept, the reason for a failed load logged once with it. Engine-free;
/// the sections and keys are <c>ASSETS\LAYOUT.CSV</c>'s own (<c>docs/formats/menu-layout.md</c>),
/// and which of a screen's values still come from a measurement rather than a row is in
/// <c>docs/org/campaign-board.md</c>.
/// </summary>
public sealed class CampaignLayout
{
    /// <summary>The profile screen's section, <c>CAMPAIGN.SCRIPT</c>'s.</summary>
    public const string RosterSection = "Campaign";

    /// <summary>The cabin's section.</summary>
    public const string CabinSection = "PassengerCabin";

    /// <summary>The memento chooser's section, <c>MOMENTOSELECTION.SCRIPT</c>'s, whose own keys
    /// spell the word the way the data does.</summary>
    public const string MementoSection = "MomentoSelection";

    /// <summary>The flight check's section.</summary>
    public const string FlightCheckSection = "FlightCheck";

    /// <summary>The ammo screen's section, <c>ORDINANCELAYOUT.SCRIPT</c>'s.</summary>
    public const string AmmoSection = "OrdinanceLayout";

    /// <summary>The plane selection screen's section.</summary>
    public const string PlaneSelectionSection = "PlaneSelection";

    /// <summary>The scrapbook's table of contents, the previous-missions screen.</summary>
    public const string ContentsSection = "ScrapBook_TOC";

    /// <summary>The scrapbook itself.</summary>
    public const string BookSection = "ScrapBook";

    /// <summary>A scrap's detail view.</summary>
    public const string ZoomSection = "ScrapbookZoom";

    /// <summary>The Instant Action wrap-up page, <c>IA_WRAPUP.SCRIPT</c>'s.</summary>
    public const string WrapupSection = "IA_WrapUp";

    /// <summary>The message box every campaign screen's dialog is composed from.</summary>
    public const string DialogSection = "MessageBox";

    /// <summary>The main menu, whose title mark the profile screen stands over.</summary>
    public const string MainMenuSection = "MainMenu";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, CampaignLayout> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private CampaignLayout(MenuLayout? decoded, string? reason)
    {
        Decoded = decoded;
        Reason = reason;
    }

    /// <summary>The layout with no decoded artifact behind it: every read answers with its
    /// fallback. What a flow with no data root, and every unit test, composes over.</summary>
    public static CampaignLayout Fallback { get; } = new(null, null);

    /// <summary>The decoded layout, or null when the boards draw from their fallbacks.</summary>
    public MenuLayout? Decoded { get; }

    /// <summary>Why <see cref="Decoded"/> is null for a data root that was asked for one, or null
    /// when it loaded or no root was named.</summary>
    public string? Reason { get; }

    /// <summary>The layout under <paramref name="dataRoot"/>, loaded on first sight and kept. A
    /// missing or unreadable artifact is the <see cref="Fallback"/> with its reason, logged once,
    /// so the boards draw as they always did and the log says why; a null root is the fallback
    /// with no reason, since nothing was there to read.</summary>
    public static CampaignLayout For(string? dataRoot)
    {
        if (dataRoot == null)
        {
            return Fallback;
        }

        lock (Gate)
        {
            if (Loaded.TryGetValue(dataRoot, out var known))
            {
                return known;
            }

            var decoded = MenuLayout.TryLoad(MenuLayout.PathUnder(dataRoot), out string? reason);
            if (decoded == null)
            {
                Log.Warn("ui", $"campaign boards draw their hardcoded chrome: {reason}");
            }

            var layout = new CampaignLayout(decoded, reason);
            Loaded[dataRoot] = layout;
            return layout;
        }
    }

    /// <summary>A layout over an already-parsed artifact, for a caller that holds one: a test's
    /// fixture, or a presentation that loaded the same file for itself.</summary>
    public static CampaignLayout Over(MenuLayout layout) => new(layout, null);

    /// <summary>The row under <paramref name="key"/> in <paramref name="section"/>, or null when
    /// the layout, the section or the row is absent.</summary>
    public MenuLayoutWidget? Widget(string section, string key) =>
        Decoded?.Screen(section)?.Widget(key);

    /// <summary>A row's authored top-left, or the fallback pair when the row or either
    /// coordinate is missing. Never one coordinate from each source.</summary>
    public (float X, float Y) At(string section, string key, float x, float y)
    {
        var widget = Widget(section, key);
        return widget != null && widget.TryInt("X", out int ax) && widget.TryInt("Y", out int ay)
            ? (ax, ay)
            : (x, y);
    }

    /// <summary>A text, list or field row's top-left and width, or the fallback triple when any
    /// of the three is missing.</summary>
    public (float X, float Y, float Width) Box(string section, string key, float x, float y, float width)
    {
        var widget = Widget(section, key);
        return widget != null && widget.TryInt("X", out int ax) && widget.TryInt("Y", out int ay)
            && widget.TryInt("Width", out int aw)
            ? (ax, ay, aw)
            : (x, y, width);
    }

    /// <summary>One <c>int</c> field of a row (<c>ItemHeight</c>, <c>TotalDisplayed</c>, ...), or
    /// the fallback when the row lacks it.</summary>
    public int Int(string section, string key, string field, int fallback) =>
        Widget(section, key) is { } widget && widget.TryInt(field, out int value) ? value : fallback;

    /// <summary>The art a row names in <paramref name="field"/>, as a screen-chrome bitmap. The
    /// row's own <c>ArtPath</c> carries its frame count too; an arrow or slider named in another
    /// column has none in the layout, so those keep the fallback's frames.</summary>
    public BoardArt Art(string section, string key, BoardArt fallback, string field = "ArtPath")
    {
        var widget = Widget(section, key);
        string name = widget?.Field(field) ?? string.Empty;
        if (name.Length == 0)
        {
            return fallback;
        }

        int frames = field == "ArtPath" ? widget!.Frames : fallback.Frames;
        return new BoardArt(BoardArtLibrary.Ui, name, frames);
    }

    /// <summary>A bitmap a <c>[GLOBALVARS]</c> macro names (<c>GN_DROPDOWN</c>, <c>FC_SLIDER</c>),
    /// with the fallback's frame count since a macro carries none; the fallback when the layout or
    /// the macro is absent.</summary>
    public BoardArt GlobalArt(string macro, BoardArt fallback)
    {
        string? name = Decoded?.Global(macro);
        return string.IsNullOrEmpty(name) ? fallback : new BoardArt(BoardArtLibrary.Ui, name, fallback.Frames);
    }

    /// <summary>A text row's justification, or the fallback when the row carries none the board
    /// can draw (the layout's 3 and 4 are alignments the renderer does not have).</summary>
    public BoardJustify Justify(string section, string key, BoardJustify fallback) =>
        Int(section, key, "Justify", -1) switch
        {
            0 => BoardJustify.Left,
            1 => BoardJustify.Center,
            2 => BoardJustify.Right,
            _ => fallback,
        };

    /// <summary>One zoom family's three text boxes off the <c>SBZ_T_TITLE</c>/<c>CAPTION</c>/
    /// <c>TEXT&lt;letter&gt;</c> rows, or null when any of the three is missing; the caller then
    /// falls back on <see cref="ScrapbookComposition.ZoomFamily"/>'s own read.</summary>
    public ScrapbookZoomFamily? ZoomFamily(char letter)
    {
        var title = Widget(ZoomSection, $"SBZ_T_TITLE{letter}");
        var caption = Widget(ZoomSection, $"SBZ_T_CAPTION{letter}");
        var text = Widget(ZoomSection, $"SBZ_T_TEXT{letter}");
        if (title == null || caption == null || text == null
            || !TryBox(title, out var t) || !TryBox(caption, out var c) || !TryBox(text, out var b))
        {
            return null;
        }

        return new ScrapbookZoomFamily(
            t.X, t.Y, t.W, t.H, c.X, c.Y, c.W, c.H, b.X, b.Y, b.W, b.H);
    }

    private static bool TryBox(MenuLayoutWidget widget, out (float X, float Y, float W, float H) box)
    {
        box = default;
        if (!widget.TryInt("X", out int x) || !widget.TryInt("Y", out int y)
            || !widget.TryInt("Width", out int w) || !widget.TryInt("Height", out int h))
        {
            return false;
        }

        box = (x, y, w, h);
        return true;
    }
}
