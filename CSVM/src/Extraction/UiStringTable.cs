using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CSVM.Extraction;

/// <summary>
/// Builds <c>ui_strings.json</c> from the rows of the original's string-table DLLs. Each row is
/// joined to its <c>IDS_</c> symbol from <c>RESOURCE.H</c> and split from its <c>[FONTID]</c> tag.
/// The row shape and the join are in docs/formats/strings.md; <c>Mech3/UiStrings.cs</c> is the
/// runtime reader.
/// </summary>
public static class UiStringTable
{
    // The patterns and case-insensitivity of the first extractor, whose output readers expect.
    // The tag split takes a single-line row only, so a multi-line row keeps its tag in the text.
    private static readonly Regex DefineRe =
        new(@"^\s*#define\s+(IDS_\w+)\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FontTagRe =
        new(@"^\[(\w+)\](.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The <c>IDS_</c> symbol for each id <c>#define</c>d in a <c>RESOURCE.H</c> text.
    /// The first definition of an id wins.</summary>
    public static Dictionary<int, string> ParseSymbols(string resourceHeader)
    {
        var symbols = new Dictionary<int, string>();
        foreach (string line in resourceHeader.ReplaceLineEndings("\n").Split('\n'))
        {
            Match m = DefineRe.Match(line);
            if (m.Success
                && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                && !symbols.ContainsKey(id))
            {
                symbols[id] = m.Groups[1].Value;
            }
        }

        return symbols;
    }

    /// <summary>One DLL's table as rows, in ascending id order. <paramref name="dll"/> is the
    /// DLL's name without extension, the value the reader filters on.</summary>
    public static List<UiStringRow> Rows(
        string dll, SortedDictionary<int, string> table, IReadOnlyDictionary<int, string> symbols)
    {
        var rows = new List<UiStringRow>(table.Count);
        foreach (var (id, raw) in table)
        {
            string? font = null;
            string text = raw;
            Match m = FontTagRe.Match(raw);
            if (m.Success)
            {
                font = m.Groups[1].Value;
                text = m.Groups[2].Value;
            }

            rows.Add(new UiStringRow(id, symbols.TryGetValue(id, out var symbol) ? symbol : null, font, text, dll));
        }

        return rows;
    }

    /// <summary>Text by id across <paramref name="rows"/>, the first row for an id winning, which
    /// is <c>langui</c> over <c>language</c> when the rows are in that order.</summary>
    public static Dictionary<int, string> TextById(IEnumerable<UiStringRow> rows)
    {
        var text = new Dictionary<int, string>();
        foreach (var row in rows)
        {
            text.TryAdd(row.Id, row.Text);
        }

        return text;
    }

    /// <summary>The rows as the JSON array <c>ui_strings.json</c> holds, UTF-8 without a BOM.
    /// </summary>
    public static byte[] ToJson(IReadOnlyList<UiStringRow> rows)
    {
        using var buffer = new MemoryStream();
        var options = new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using (var w = new Utf8JsonWriter(buffer, options))
        {
            w.WriteStartArray();
            foreach (var row in rows)
            {
                w.WriteStartObject();
                w.WriteNumber("id", row.Id);
                WriteNullable(w, "symbol", row.Symbol);
                WriteNullable(w, "font", row.Font);
                w.WriteString("text", row.Text);
                w.WriteString("dll", row.Dll);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return buffer.ToArray();
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value == null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }
}

/// <summary>One row of <c>ui_strings.json</c>. <see cref="Symbol"/> is null for an id no
/// <c>IDS_</c> define names, <see cref="Font"/> for a row with no single-line <c>[FONTID]</c>
/// tag.</summary>
public sealed record UiStringRow(int Id, string? Symbol, string? Font, string Text, string Dll);
