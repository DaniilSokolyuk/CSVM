using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.UI.Hangar;
using Xunit;

namespace CSVM.Tests;

/// <summary>
/// The PAINT screen: the pattern
/// row stepping only the patterns this airframe's availability mask allows and loading that
/// entry's six colour/shade defaults, a colour row and a shade row per slot walking the 27-row
/// swatch table and its ramps, three decal rows over the 00-49 texture set, and the live preview
/// composed through the original's region masks (docs/formats/paint.md).
/// </summary>
public class HangarPaintPageTests : IDisposable
{
    private readonly string _dir;
    private readonly CustomPlaneStore _store;

    public HangarPaintPageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "csvm-paint-" + Guid.NewGuid().ToString("N"));
        _store = new CustomPlaneStore(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Ten rows: the pattern, then each slot's colour and shade together, then the three
    /// decals in the dropdowns' own nose/tail/wing order.</summary>
    [Fact]
    public void OffersThePatternEachSlotsPairAndTheThreeDecals()
    {
        var flow = OpenOnPaint();

        Assert.Equal(10, flow.Page.RowCount);
        Assert.StartsWith("Pattern:", flow.Page.RowText(0), StringComparison.Ordinal);
        Assert.StartsWith("Paint 1 (body):", flow.Page.RowText(1), StringComparison.Ordinal);
        Assert.StartsWith("Shade 1:", flow.Page.RowText(2), StringComparison.Ordinal);
        Assert.StartsWith("Paint 2 (dark trim):", flow.Page.RowText(3), StringComparison.Ordinal);
        Assert.StartsWith("Shade 2:", flow.Page.RowText(4), StringComparison.Ordinal);
        Assert.StartsWith("Paint 3 (light trim):", flow.Page.RowText(5), StringComparison.Ordinal);
        Assert.StartsWith("Shade 3:", flow.Page.RowText(6), StringComparison.Ordinal);
        Assert.StartsWith("Nose decal:", flow.Page.RowText(7), StringComparison.Ordinal);
        Assert.StartsWith("Tail decal:", flow.Page.RowText(8), StringComparison.Ordinal);
        Assert.StartsWith("Wing decal:", flow.Page.RowText(9), StringComparison.Ordinal);
    }

    /// <summary>The pattern row is the airframe's availability mask, not the whole table: the
    /// Hoplite (airframe 0) wears exactly blackhat, fortune, studio and itstaxi.</summary>
    [Fact]
    public void ThePatternRowStepsOnlyThisAirframesPatterns()
    {
        var flow = OpenOnPaint();
        var seen = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            flow.Step(1);
            seen.Add(flow.Scratch.PaintPattern);
        }

        Assert.Equal(new[] { 4, 11, 13, 0 }, seen);
    }

    /// <summary>The Fury's four (blckswan, fortune, hughes, studio) are the four paint.md's
    /// per-aircraft table lists, read here off the masks rather than off an extraction.</summary>
    [Fact]
    public void TheFuryOffersItsOwnFour()
    {
        var flow = OpenOnPaint();
        flow.Scratch.Airframe = 7;
        var seen = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            flow.Step(1);
            seen.Add(flow.Scratch.PaintPattern);
        }

        Assert.Equal(new[] { 1, 4, 6, 11, }, seen);
    }

    /// <summary>Selecting a pattern copies its six colour/shade defaults over the plane's own,
    /// which is all the original's SET handler does. fortune's resolve to red, the white ramp's
    /// darkest shade, and white: player_fortune's triple, reached through the tables.</summary>
    [Fact]
    public void PickingAPatternLoadsItsSixDefaults()
    {
        var flow = OpenOnPaint();

        Assert.True(flow.Step(1)); // blackhat -> fortune

        Assert.Equal(4, flow.Scratch.PaintPattern);
        Assert.Equal(new[] { 1, 26, 26 }, flow.Scratch.PaintColours);
        Assert.Equal(new[] { 8, 0, 9 }, flow.Scratch.PaintShades);
        Assert.Equal(new PaintColour(223, 0, 41), flow.Scratch.Colour1);
        Assert.Equal(new PaintColour(25, 25, 25), flow.Scratch.Colour2);
        Assert.Equal(new PaintColour(255, 255, 255), flow.Scratch.Colour3);
    }

    /// <summary>blackhat's defaults resolve to its shipped scheme triple exactly, which is the
    /// pattern table and the swatch table checked against vehicle.json.</summary>
    [Fact]
    public void BlackhatsDefaultsResolveToItsShippedTriple()
    {
        var flow = OpenOnPaint();
        flow.Scratch.LoadPatternDefaults(0);

        Assert.Equal(new PaintColour(177, 130, 66), flow.Scratch.Colour1);
        Assert.Equal(new PaintColour(119, 74, 43), flow.Scratch.Colour2);
        Assert.Equal(new PaintColour(66, 39, 15), flow.Scratch.Colour3);
    }

    /// <summary>A colour row walks the 27 swatch rows, and picking one resets that slot's shade to
    /// the row's own default: the original's reset at 0x0040d402, which is why a colour pick lands
    /// on the artist's brightness rather than keeping the last one.</summary>
    [Fact]
    public void AColourPickResetsItsSlotsShade()
    {
        var flow = OpenOnPaint();
        flow.Scratch.LoadPatternDefaults(4);
        flow.Move(1);

        Assert.True(flow.Step(1)); // swatch 1 -> 2

        Assert.Equal(2, flow.Scratch.PaintColours[0]);
        Assert.Equal(8, flow.Scratch.PaintShades[0]); // swatch 2's own default
        Assert.Equal(new PaintColour(227, 59, 34), flow.Scratch.Colour1);
        Assert.Equal(26, flow.Scratch.PaintColours[1]); // the other slots stand
    }

    /// <summary>A shade row walks its colour's own dark-to-light ramp and nothing else.</summary>
    [Fact]
    public void AShadeRowWalksItsColoursOwnRamp()
    {
        var flow = OpenOnPaint();
        flow.Scratch.LoadPatternDefaults(4);
        flow.Move(2);

        Assert.True(flow.Step(-1)); // slot 1's red, one shade darker

        Assert.Equal(1, flow.Scratch.PaintColours[0]);
        Assert.Equal(7, flow.Scratch.PaintShades[0]);
        Assert.Equal(new PaintColour(204, 0, 41), flow.Scratch.Colour1);
    }

    /// <summary>Ramps differ in length, so a shade cycle is the row's own: swatch 26's is ten long
    /// and wraps from its darkest to its lightest.</summary>
    [Fact]
    public void ShadeSteppingWrapsWithinTheRow()
    {
        var flow = OpenOnPaint();
        flow.Scratch.LoadPatternDefaults(4);
        flow.Move(4); // slot 2's shade: swatch 26, shade 0

        Assert.True(flow.Step(-1));
        Assert.Equal(9, flow.Scratch.PaintShades[1]);
        Assert.Equal(new PaintColour(255, 255, 255), flow.Scratch.Colour2);
    }

    /// <summary>A fresh build's decal slots keep the shipped placeholder, which the original
    /// cannot store; the first step enters the 0-49 set and the cycle stays inside it.</summary>
    [Fact]
    public void DecalRowsStepTheFiftyTextureSet()
    {
        var flow = OpenOnPaint();

        Assert.Equal(-1, flow.Scratch.NoseDecal);
        Assert.Contains("placeholder", flow.Page.RowText(7), StringComparison.Ordinal);

        flow.Move(7);
        Assert.True(flow.Step(-1));
        Assert.Equal(49, flow.Scratch.NoseDecal);
        Assert.Equal("Nose decal: 49Ace_Star2", flow.Page.RowText(7));

        Assert.True(flow.Step(1));
        Assert.Equal(0, flow.Scratch.NoseDecal);
        Assert.Equal("Nose decal: 00BBomber_logo1", flow.Page.RowText(7));
    }

    /// <summary>Each decal row edits its own slot.</summary>
    [Fact]
    public void EachDecalRowEditsItsOwnSlot()
    {
        var flow = OpenOnPaint();
        flow.Page.Step(8, 1);

        Assert.Equal(-1, flow.Scratch.NoseDecal);
        Assert.Equal(0, flow.Scratch.TailDecal);
        Assert.Equal(-1, flow.Scratch.WingDecal);
    }

    /// <summary>The pattern label is the dropdown's own langui string (3425 + index), with the
    /// internal name standing in when the table is missing.</summary>
    [Fact]
    public void ThePatternLabelIsLangui3425PlusTheIndex()
    {
        Assert.Equal("Pattern: BLACKHAT", OpenOnPaint().Page.RowText(0));

        var strings = UiStrings.Parse("[{\"id\":3425,\"text\":\"Black Hat Brigade\",\"dll\":\"langui\"}]");
        Assert.Equal("Pattern: Black Hat Brigade", OpenOnPaint(strings: strings).Page.RowText(0));
    }

    /// <summary>Confirm advances to the name screen without editing the paint.</summary>
    [Fact]
    public void AcceptAdvancesWithoutEditing()
    {
        var flow = OpenOnPaint();
        flow.Scratch.LoadPatternDefaults(4);
        flow.Accept();

        Assert.Equal(HangarScreen.Name, flow.Screen);
        Assert.Equal(4, flow.Scratch.PaintPattern);
        Assert.Equal(new PaintColour(223, 0, 41), flow.Scratch.Colour1);
    }

    /// <summary>No data root is no art, as on every other screen: the hangar runs without an
    /// extraction.</summary>
    [Fact]
    public void ArtIsNullWithoutADataRoot() => Assert.Null(OpenOnPaint().Page.Art);

    /// <summary>The preview is the original's own paint-screen composite: the three region masks
    /// alpha-over in slot order carrying the picked colours, then the detail plate over them.
    /// Pinned against a hand-built icon set whose expected pixels are arithmetic, not a
    /// screenshot.</summary>
    [Fact]
    public void ThePreviewComposesTheIconMasksAndTheColours()
    {
        string root = Path.Combine(_dir, "root");
        WriteIconSet(root, 3, 4);
        var flow = OpenOnPaint(root);
        flow.Scratch.Airframe = 3;          // Bloodhawk
        flow.Scratch.LoadPatternDefaults(4); // fortune: red, 25/25/25, white

        var art = flow.Page.Art;
        Assert.NotNull(art);
        Assert.Equal(2, art!.Image.Width);
        Assert.Equal(2, art.Image.Height);

        // A fully-masked texel IS its colour: no shading multiply, no weight normalisation.
        AssertPixel(art.Image, 0, 223, 0, 41, 255);
        // Both masks claim texel 1; the later slot is drawn over the earlier one.
        AssertPixel(art.Image, 1, 25, 25, 25, 255);
        // No mask claims texel 2, so the opaque detail plate is all there is.
        AssertPixel(art.Image, 2, 40, 50, 60, 255);
        // A half-alpha plate over slot 1's red: the plate's black darkens it by its own coverage.
        AssertPixel(art.Image, 3, 111, 0, 20, 255);
    }

    /// <summary>Coverage carries through: a texel no layer claims is transparent, so the preview
    /// sits on the shell's own ground the way the original's sits on its blueprint page.</summary>
    [Fact]
    public void UnclaimedTexelsStayTransparent()
    {
        string root = Path.Combine(_dir, "root");
        WriteIconSet(root, 3, 4, plateAlpha: new byte[] { 255, 255, 255, 0 }, masks: new byte[][]
        {
            new byte[] { 255, 0, 0, 0 }, new byte[] { 0, 255, 0, 0 }, new byte[] { 0, 0, 0, 0 },
        });
        var flow = OpenOnPaint(root);
        flow.Scratch.Airframe = 3;
        flow.Scratch.LoadPatternDefaults(4);

        Assert.Equal(0, flow.Page.Art!.Image.Rgba[(3 * 4) + 3]);
    }

    /// <summary>Every scratch edit refreshes the preview, and nothing else does: the shell rebuilds
    /// its texture when the page hands over a different image, so an unchanged screen must hand
    /// over the same one.</summary>
    [Fact]
    public void EveryEditRefreshesThePreview()
    {
        string root = Path.Combine(_dir, "root");
        WriteIconSet(root, 3, 4);
        var flow = OpenOnPaint(root);
        flow.Scratch.Airframe = 3;
        flow.Scratch.LoadPatternDefaults(4);

        var first = flow.Page.Art;
        Assert.NotNull(first);
        Assert.Same(first!.Image, flow.Page.Art!.Image);

        flow.Move(1);
        Assert.True(flow.Step(1));
        Assert.NotSame(first.Image, flow.Page.Art!.Image);
    }

    /// <summary>A decal row hands the shell the decal's own tile out of the shipped sheet (E48),
    /// sliced 66x66 in index order; no other row has one, and neither does the placeholder.</summary>
    [Fact]
    public void DecalRowsCarryTheirOwnTile()
    {
        string root = Path.Combine(_dir, "root");
        WriteDecalSheet(root);
        var flow = OpenOnPaint(root);

        Assert.Null(flow.Page.RowArt(HangarPaintPage.PatternRow));
        Assert.Null(flow.Page.RowArt(HangarPaintPage.NoseDecalRow)); // still the placeholder

        flow.Move(HangarPaintPage.NoseDecalRow);
        Assert.True(flow.Step(1)); // decal 0
        var art = flow.Page.RowArt(HangarPaintPage.NoseDecalRow);
        Assert.NotNull(art);
        Assert.Equal(2, art!.Image.Width);
        Assert.Equal(2, art.Image.Height);
        Assert.Equal("00BBomber_logo1", art.Caption);
        AssertPixel(art.Image, 0, 0, 0, 0, 255); // tile 0's own marker row

        Assert.True(flow.Step(1)); // decal 1
        AssertPixel(flow.Page.RowArt(HangarPaintPage.NoseDecalRow)!.Image, 0, 1, 0, 0, 255);
    }

    /// <summary>The shipped icon sets compose for every airframe wearing every pattern its own mask
    /// allows, which is the sweep that would catch a wrong airframe row or a mask offering a
    /// pattern that ships no artwork for that plane.</summary>
    [ExtractedDataFact]
    public void EveryAirframeComposesEveryPatternItsMaskAllows()
    {
        var flow = OpenOnPaint(TestData.DataRoot);
        for (int airframe = 0; airframe <= CustomPlaneDef.MaxAirframe; airframe++)
        {
            flow.Scratch.Airframe = airframe;
            for (int pattern = 0; pattern < HangarPaintTables.PatternCount; pattern++)
            {
                if (!HangarPaintTables.Default.Available(pattern, airframe))
                {
                    continue;
                }

                flow.Scratch.LoadPatternDefaults(pattern);
                var art = flow.Page.Art;
                Assert.True(art != null && art.Image.Width > 1, $"airframe {airframe} pattern {pattern}");
            }
        }
    }

    /// <summary>The shipped Fury in Fortune Hunters colours, the reference screenshot's own build:
    /// the plan view is 358x335 and its three regions come out as the resolved colours exactly,
    /// with no shading term anywhere in them.</summary>
    [ExtractedDataFact]
    public void TheFurysFortuneSchemePaintsItsRegionsExactly()
    {
        var flow = OpenOnPaint(TestData.DataRoot);
        flow.Scratch.Airframe = 7;
        flow.Scratch.LoadPatternDefaults(4);

        var image = flow.Page.Art!.Image;
        Assert.Equal(358, image.Width);
        Assert.Equal(335, image.Height);
        Assert.True(Count(image, 223, 0, 41) > 8000, "the red body");
        Assert.True(Count(image, 25, 25, 25) > 2000, "the black trim");
        Assert.True(Count(image, 255, 255, 255) > 500, "the white pinstripe");
    }

    /// <summary>The decal sheet ships 50 tiles of 66x66, one per index, and the page slices the
    /// focused row's own.</summary>
    [ExtractedDataFact]
    public void TheShippedDecalSheetSlicesFiftyTiles()
    {
        var flow = OpenOnPaint(TestData.DataRoot);
        flow.Move(HangarPaintPage.NoseDecalRow);
        for (int decal = 0; decal < HangarPaintTables.DecalCount; decal++)
        {
            flow.Scratch.NoseDecal = decal;
            var art = flow.Page.RowArt(HangarPaintPage.NoseDecalRow);
            Assert.True(art != null && art.Image.Width == 66 && art.Image.Height == 66, $"decal {decal}");
        }
    }

    // How many opaque texels of the composed preview are exactly this colour.
    private static int Count(TgaImage image, int r, int g, int b)
    {
        int n = 0;
        for (int p = 0; p < image.Rgba.Length; p += 4)
        {
            if (image.Rgba[p] == r && image.Rgba[p + 1] == g && image.Rgba[p + 2] == b
                && image.Rgba[p + 3] == 255)
            {
                n++;
            }
        }

        return n;
    }

    // One texel's RGBA out of the composed preview.
    private static void AssertPixel(TgaImage image, int pixel, int r, int g, int b, int a)
    {
        Assert.Equal(r, image.Rgba[pixel * 4]);
        Assert.Equal(g, image.Rgba[(pixel * 4) + 1]);
        Assert.Equal(b, image.Rgba[(pixel * 4) + 2]);
        Assert.Equal(a, image.Rgba[(pixel * 4) + 3]);
    }

    // A 2x2 `PX_ICON_<airframe>_<pattern>_0..3` set. Layer 0 is the detail plate (RGB plus its own
    // coverage), layers 1-3 the three slots' region masks, white with the region in the alpha.
    private static void WriteIconSet(string dataRoot, int airframe, int pattern,
        byte[]? plateAlpha = null, byte[][]? masks = null)
    {
        var dir = Path.Combine(dataRoot, "extracted", "rof", "ASSETS", "GRAPHICS");
        Directory.CreateDirectory(dir);
        byte[][] plateRgb =
        {
            new byte[] { 0, 0, 0 }, new byte[] { 0, 0, 0 },
            new byte[] { 40, 50, 60 }, new byte[] { 0, 0, 0 },
        };
        plateAlpha ??= new byte[] { 0, 0, 255, 128 };
        masks ??= new byte[][]
        {
            new byte[] { 255, 255, 0, 255 }, new byte[] { 0, 255, 0, 0 }, new byte[] { 0, 0, 0, 0 },
        };
        WriteTga(Path.Combine(dir, $"PX_ICON_{airframe}_{pattern}_0.TGA"), 2, 2, plateRgb, plateAlpha);
        for (int slot = 0; slot < masks.Length; slot++)
        {
            byte[][] white = { new byte[] { 255, 255, 255 }, new byte[] { 255, 255, 255 },
                new byte[] { 255, 255, 255 }, new byte[] { 255, 255, 255 } };
            WriteTga(Path.Combine(dir, $"PX_ICON_{airframe}_{pattern}_{slot + 1}.TGA"), 2, 2, white, masks[slot]);
        }
    }

    // A 2x50-tile decal sheet, 2x2 per tile, each tile's texels carrying its own index as red.
    private static void WriteDecalSheet(string dataRoot)
    {
        var dir = Path.Combine(dataRoot, "extracted", "rof", "ASSETS", "GRAPHICS");
        Directory.CreateDirectory(dir);
        int tiles = HangarPaintTables.DecalCount;
        var rgb = new byte[2 * 2 * tiles][];
        var alpha = new byte[rgb.Length];
        for (int i = 0; i < rgb.Length; i++)
        {
            rgb[i] = new byte[] { (byte)(i / 4), 0, 0 };
            alpha[i] = 255;
        }

        WriteTga(Path.Combine(dir, "PX_P_DECALS.TGA"), 2, 2 * tiles, rgb, alpha);
    }

    // One uncompressed 32-bit top-down TGA (descriptor 0x28), the form TgaImage decodes.
    private static void WriteTga(string path, int width, int height, byte[][] rgb, byte[] alpha)
    {
        var bytes = new byte[18 + (width * height * 4)];
        bytes[2] = 2;
        bytes[12] = (byte)(width & 0xff);
        bytes[13] = (byte)(width >> 8);
        bytes[14] = (byte)(height & 0xff);
        bytes[15] = (byte)(height >> 8);
        bytes[16] = 32;
        bytes[17] = 0x28;
        for (int p = 0; p < width * height; p++)
        {
            bytes[18 + (p * 4)] = rgb[p][2];
            bytes[18 + (p * 4) + 1] = rgb[p][1];
            bytes[18 + (p * 4) + 2] = rgb[p][0];
            bytes[18 + (p * 4) + 3] = alpha[p];
        }

        File.WriteAllBytes(path, bytes);
    }

    // A flow standing on the PAINT screen with a fresh scratch plane.
    private HangarFlow OpenOnPaint(string? dataRoot = null, UiStrings? strings = null)
    {
        var flow = new HangarFlow(_store, strings ?? UiStrings.Empty, dataRoot);
        for (int guard = 0; flow.Screen != HangarScreen.Paint && guard < HangarFlow.Order.Length + 3; guard++)
        {
            // A no-op except on the airframe-defaults ask (E41), which the airframe screen's own
            // confirm raises before the next confirm advances (E49).
            flow.AnswerDefaultsAsk(false);
            flow.Accept();
        }

        Assert.Equal(HangarScreen.Paint, flow.Screen);
        return flow;
    }
}
