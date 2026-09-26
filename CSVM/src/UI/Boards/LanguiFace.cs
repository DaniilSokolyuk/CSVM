using System;
using System.Collections.Generic;
using System.Globalization;

namespace CSVM.UI.Boards;

/// <summary>
/// One langui <c>[FONTID]</c> tag read as a typeface: <c>IMP36</c> is Impact at 36 points,
/// <c>BEL14B</c> Bell MT bold, <c>CSB9I</c> Century Schoolbook italic. The string table declares
/// each tag as <c>/font=&lt;TAG.ttf&gt;</c> and the archive ships none of those files, so the tag's
/// family letters are resolved to the Windows face they abbreviate (docs/formats/strings.md, "Font
/// prefix"). Engine-free: the renderer turns <see cref="Family"/> into an installed font or keeps
/// its own face when the machine lacks it.
/// </summary>
public readonly record struct LanguiFace(string Tag, string Family, int Points, bool Bold, bool Italic)
{
    // Points to authored pixels: the original sizes a face in points at 96 dpi, which is what puts
    // an IMP36 headline's cap height at the 39 px the original's capture measures on the 800x600
    // board, and its line pitch at the same pixel size.
    private const float PixelsPerPoint = 96f / 72f;

    // The tag's family letters and the face each abbreviates, every family the font table
    // declares. AB is Book Antiqua Bold, the one family whose letters name a weight as well.
    private static readonly Dictionary<string, (string Family, bool Bold)> Families =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["AB"] = ("Book Antiqua", true),
            ["BEL"] = ("Bell MT", false),
            ["CENT"] = ("Century", false),
            ["COP"] = ("Copperplate Gothic Bold", false),
            ["COUR"] = ("Courier New", false),
            ["CSB"] = ("Century Schoolbook", false),
            ["FREE"] = ("Freestyle Script", false),
            ["IMP"] = ("Impact", false),
            ["PEP"] = ("Pepita MT", false),
            ["STEN"] = ("Stencil", false),
            ["TNR"] = ("Times New Roman", false),
            ["TREB"] = ("Trebuchet MS", false),
            ["VIN"] = ("Viner Hand ITC", false),
        };

    /// <summary>The face's size in authored board pixels, what a <see cref="BoardLine"/> is sized
    /// in.</summary>
    public float Pixels => Points * PixelsPerPoint;

    /// <summary>Reads a tag, bracketed or bare, or null for one whose letters name no declared
    /// family or that carries no size.</summary>
    public static LanguiFace? Parse(string? tag)
    {
        string bare = (tag ?? string.Empty).Trim().Trim('[', ']');
        int digits = 0;
        while (digits < bare.Length && !char.IsAsciiDigit(bare[digits]))
        {
            digits++;
        }

        int end = digits;
        while (end < bare.Length && char.IsAsciiDigit(bare[end]))
        {
            end++;
        }

        if (digits == 0 || end == digits
            || !Families.TryGetValue(bare[..digits], out var family)
            || !int.TryParse(bare[digits..end], NumberStyles.None, CultureInfo.InvariantCulture, out int points))
        {
            return null;
        }

        string style = bare[end..].ToUpperInvariant();
        if (style.Length > 0 && style is not ("B" or "I" or "BI" or "IB"))
        {
            return null;
        }

        return new LanguiFace(
            bare.ToUpperInvariant(), family.Family, points, family.Bold || style.Contains('B'), style.Contains('I'));
    }
}
