using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CSVM.Extraction;
using CSVM.Mech3;
using Xunit;

namespace CSVM.Tests;

/// <summary>The decoders under <c>CSVM.Extraction</c> on inputs built from the documented layouts
/// by <see cref="ExtractionFixtures"/>. They cover the <c>.rof</c> walk, the <c>.BM</c> planes and
/// the PE string table. The PNG writer is read back through the engine's own PNG decoder, and
/// <c>ui_strings.json</c> through its runtime reader.</summary>
public class ExtractionDecoderTests
{
    [Fact]
    public void TheRofWalkListsDirectoriesBeforeTheirContentsWithSlashPaths()
    {
        byte[] rof = ExtractionFixtures.Rof(new RofDir().Dir("ASSETS", new RofDir()
            .File("A.TXT", Encoding.ASCII.GetBytes("stored"), deflate: false)
            .Dir("SUB", new RofDir().File("C.CSV", Encoding.ASCII.GetBytes("x,y")))));

        var entries = RofArchive.Walk(rof).ToList();

        Assert.Equal(new[] { "ASSETS", "ASSETS/A.TXT", "ASSETS/SUB", "ASSETS/SUB/C.CSV" }, entries.Select(e => e.Path));
        Assert.Equal(new[] { true, false, true, false }, entries.Select(e => e.IsDirectory));
    }

    [Fact]
    public void StoredAndDeflatedMembersReadBackToTheirBytes()
    {
        byte[] big = Enumerable.Range(0, 5000).Select(i => (byte)(i % 7)).ToArray();
        byte[] rof = ExtractionFixtures.Rof(new RofDir().Dir("ASSETS", new RofDir()
            .File("S.PNG", new byte[] { 1, 2, 3 }, deflate: false)
            .File("D.TGA", big)));

        var files = RofArchive.Walk(rof).Where(e => !e.IsDirectory).ToList();

        Assert.False(files[0].Deflated);
        Assert.Equal(new byte[] { 1, 2, 3 }, RofArchive.ReadMember(rof, files[0]));
        Assert.True(files[1].Deflated);
        Assert.True(files[1].StoredSize < big.Length);
        Assert.Equal(big, RofArchive.ReadMember(rof, files[1]));
    }

    [Fact]
    public void AnUnknownEntryKindIsRejected()
    {
        byte[] rof = ExtractionFixtures.Rof(new RofDir().File("X", new byte[] { 9 }, deflate: false));
        rof[8 + 12] = 7; // the first entry's kind

        Assert.Throws<InvalidDataException>(() => RofArchive.Walk(rof).ToList());
    }

    [Fact]
    public void TheBmSplitsIntoShadingAndSlotMasksInStoredRowOrder()
    {
        const int w = 3, h = 2, n = w * h;
        byte[] bm = ExtractionFixtures.Bm(w, h, i => (byte)(i + 1));

        var texture = BmTexture.TryDecode(bm)!;

        Assert.Equal(w, texture.Width);
        Assert.Equal(h, texture.Height);
        Assert.Equal(Enumerable.Range(1, 3 * n).Select(i => (byte)i), texture.Shading);
        for (int px = 0; px < n; px++)
        {
            Assert.Equal((byte)((3 * n) + px + 1), texture.Masks[3 * px]);
            Assert.Equal((byte)((4 * n) + px + 1), texture.Masks[(3 * px) + 1]);
            Assert.Equal((byte)((5 * n) + px + 1), texture.Masks[(3 * px) + 2]);
        }
    }

    [Fact]
    public void ABmShorterThanItsShadingAndMaskPlanesIsNotDecoded()
    {
        byte[] bm = ExtractionFixtures.Bm(4, 4, _ => 0);

        Assert.Null(BmTexture.TryDecode(bm.AsSpan(0, 4 + (6 * 16) - 1)));
        Assert.NotNull(BmTexture.TryDecode(bm.AsSpan(0, 4 + (6 * 16))));
        Assert.Null(BmTexture.TryDecode(new byte[] { 0, 0 }));
    }

    [Fact]
    public void AWrittenPngDecodesToTheSamePixels()
    {
        const int w = 5, h = 3;
        byte[] rgb = Enumerable.Range(0, w * h * 3).Select(i => (byte)((i * 37) % 256)).ToArray();

        var image = PngImage.Decode(PngWriter.EncodeRgb(w, h, rgb))!;

        Assert.Equal(w, image.Width);
        Assert.Equal(h, image.Height);
        for (int px = 0; px < w * h; px++)
        {
            Assert.Equal(rgb[3 * px], image.Rgba[4 * px]);
            Assert.Equal(rgb[(3 * px) + 1], image.Rgba[(4 * px) + 1]);
            Assert.Equal(rgb[(3 * px) + 2], image.Rgba[(4 * px) + 2]);
        }
    }

    [Fact]
    public void EveryPngChunkCarriesItsCrc()
    {
        byte[] png = PngWriter.EncodeRgb(2, 2, new byte[12]);

        var types = new List<string>();
        for (int at = 8; at < png.Length;)
        {
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            uint crc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length));
            Assert.Equal(PngWriter.Crc32(png.AsSpan(at + 4, length + 4)), crc);
            types.Add(Encoding.ASCII.GetString(png, at + 4, 4));
            at += 12 + length;
        }

        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, types);
        Assert.Equal(0xCBF43926u, PngWriter.Crc32(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void TheStringTableReadsEachUsedSlotUnderItsId()
    {
        byte[] pe = ExtractionFixtures.StringTablePe(3, new[] { "zero", null, "two", string.Empty, "four" });

        var table = PeStringTable.Read(pe);

        // Block 3 holds ids 32 to 47; an empty or unused slot is no string at all.
        Assert.Equal(new[] { 32, 34, 36 }, table.Keys);
        Assert.Equal("zero", table[32]);
        Assert.Equal("two", table[34]);
        Assert.Equal("four", table[36]);
    }

    [Fact]
    public void AFileThatIsNotAPeIsRejected()
    {
        byte[] pe = ExtractionFixtures.StringTablePe(1, new[] { "a" });
        pe[0x40] = (byte)'X';

        Assert.Throws<InvalidDataException>(() => PeStringTable.Read(pe));
    }

    [Fact]
    public void ResourceSymbolsKeepTheFirstDefinitionOfAnId()
    {
        const string header = "#define IDS_FIRST 12\r\n  #define IDS_SECOND 12\n#define IDC_OTHER 13\n#define IDS_THIRD 14 // note";

        var symbols = UiStringTable.ParseSymbols(header);

        Assert.Equal("IDS_FIRST", symbols[12]);
        Assert.Equal("IDS_THIRD", symbols[14]);
        Assert.False(symbols.ContainsKey(13));
    }

    [Fact]
    public void ASingleLineRowLosesItsFontTagToTheFontField()
    {
        var table = new SortedDictionary<int, string>
        {
            [1] = "[IMP36]Title",
            [2] = "Plain",
            [3] = "[TNR14]First\r\nSecond",
        };

        var rows = UiStringTable.Rows("langui", table, new Dictionary<int, string> { [1] = "IDS_TITLE" });

        Assert.Equal(new UiStringRow(1, "IDS_TITLE", "IMP36", "Title", "langui"), rows[0]);
        Assert.Equal(new UiStringRow(2, null, null, "Plain", "langui"), rows[1]);

        // A multi-line row keeps its tag; the runtime reader strips it.
        Assert.Equal(new UiStringRow(3, null, null, "[TNR14]First\r\nSecond", "langui"), rows[2]);
    }

    [Fact]
    public void TheWrittenTableReadsBackThroughTheRuntimeReader()
    {
        var rows = new List<UiStringRow>
        {
            new(5, "IDS_A", "IMP36", "Kestrel <\"quoted\"> é", "langui"),
            new(5, null, null, "shadowed", "language"),
            new(6, null, null, "[TNR14]a\nb", "langui"),
        };

        byte[] json = UiStringTable.ToJson(rows);
        var strings = UiStrings.Parse(Encoding.UTF8.GetString(json));

        Assert.NotEqual(0xEF, json[0]);
        Assert.Equal("Kestrel <\"quoted\"> é", strings.Text(5));
        Assert.Equal("IMP36", strings.Face(5));
        Assert.Equal("a\nb", strings.Text(6));
        Assert.Equal("TNR14", strings.Face(6));
    }

    [Fact]
    public void TheFirstRowForAnIdWinsTheTextJoin()
    {
        var rows = new[]
        {
            new UiStringRow(9, null, null, "from langui", "langui"),
            new UiStringRow(9, null, null, "from language", "language"),
        };

        Assert.Equal("from langui", UiStringTable.TextById(rows)[9]);
    }
}
