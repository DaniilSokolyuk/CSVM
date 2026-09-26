using System.Collections.Generic;
using System.IO;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using Xunit;

namespace CSVM.Tests;

/// <summary>The scrapbook's per-spread scrap composition, read from <c>SCRAPBOOK.CSV</c>
/// rather than invented. Parser-level behaviour (extension resolution, the item-1-and-up
/// enumeration, the <c>Objective</c> gate, the capture skip and the draw-order stack) is exercised
/// against hand-authored fixtures, never a copy of the shipped file
/// (<c>CSVM.Tests/TestData.cs</c>'s own rule). The two claims that need the real shipped file
/// (every spread's art present, CM01's story page reproduced) are <see cref="ExtractedDataFact"/>
/// tests against the player's own extraction.</summary>
public class ScrapbookCompositionTests
{
    // Mirrors the shipped file's own shape: a [SCRAPBOOK] section, a column-header comment, and
    // rows keyed <mission>_<spread>_<item>, values comma-split with one quoted region field that
    // sits past every column this parser reads.
    private const string Fixture =
        "[SCRAPBOOK]\n" +
        ";Mission_Spread_Item=Objective,ResourceID,ImageName,ImageType,X,Y,Alpha,Width,Height,DrawOrder,\"Left,Top,Right,Bottom\",Zoom,ZoomX,ZoomY,TitleResID,TextResID\n" +
        "1_1_1=0,0,SB_01_01_coin,PJ,49,250,1,0,0,100,\"0,0,0,0\",A,0,0,0,0\n" +
        "1_1_2=0,0,SB_01_01_news3,P0,49,70,2,0,0,60,\"0,0,0,0\",B,0,0,0,0\n" +
        "1_1_3=0,0,SB_01_02_mag2,P0,135,197,2,0,0,90,\"0,0,0,0\",M,0,0,0,0\n" +
        "2_1_1=3,0,SB_02_01_gated,PJ,10,20,1,0,0,10,\"0,0,0,0\",A,0,0,0,0\n" +
        "2_1_2=-3,0,SB_02_01_notgated,PJ,30,40,1,0,0,20,\"0,0,0,0\",A,0,0,0,0\n" +
        "3_1_1=0,0,SB_03_01_bmp,B0,1,2,1,0,0,10,\"0,0,0,0\",0,0,0,0,0\n" +
        "4_1_1=0,,Snap_4_1,PP,5,6,0,0,0,10,\"0,0,0,0\",Q,0,0,0,0\n" +
        "4_1_2=0,,DZ_generic_corners,P0,5,6,1,0,0,11,\"0,0,0,0\",0,0,0,0,0\n" +
        "5_1_1=0,0,SB_05_01_jpg,J0,1,2,1,0,0,10,\"0,0,0,0\",0,0,0,0,0\n";

    [Fact]
    public void EnumeratesItemsContiguouslyFromOneAndStopsAtTheFirstGap()
    {
        string root = Root();
        var items = ScrapbookComposition.Items(root, 1, 1);

        Assert.Equal(3, items.Count);
        Assert.Equal("SB_01_01_coin", items[0].ImageName);
        Assert.Equal("SB_01_01_news3", items[1].ImageName);
        Assert.Equal("SB_01_02_mag2", items[2].ImageName);
    }

    [Fact]
    public void AnUnpopulatedSpreadIsEmptyRatherThanThrowing()
    {
        Assert.Empty(ScrapbookComposition.Items(Root(), 1, 2));
        Assert.Empty(ScrapbookComposition.Items(Root(), 99, 1));
    }

    [Fact]
    public void ANullDataRootReadsAsNoComposition()
    {
        Assert.Empty(ScrapbookComposition.Items(null, 1, 1));
        Assert.Empty(ScrapbookComposition.Pictures(null, 1, 1, bestMask: -1, scrap => OnDisk(scrap)));
    }

    [Theory]
    [InlineData(1, "PNG")] // "PJ": the first letter alone picks the page extension, not the second
    [InlineData(3, "BMP")] // "B0"
    [InlineData(5, "JPG")] // "J0"
    public void TheImageTypesFirstLetterAlonePicksThePageExtension(int mission, string extension)
    {
        var items = ScrapbookComposition.Items(Root(), mission, 1);
        Assert.Contains(items, i => i.Extension == extension);
    }

    [Fact]
    public void PositionAndDrawOrderComeThroughUnchanged()
    {
        var coin = ScrapbookComposition.Items(Root(), 1, 1)[0];
        Assert.Equal(49f, coin.X);
        Assert.Equal(250f, coin.Y);
        Assert.Equal(100, coin.DrawOrder);
    }

    [Fact]
    public void PicturesStackByAscendingDrawOrderRegardlessOfItemOrder()
    {
        // Item order is coin(100), news3(60), mag2(90); stacked bottom first that is news3, mag2,
        // coin -- A1's own CM01 reading (coin over magazine over the Aloha Daily).
        var pictures = ScrapbookComposition.Pictures(Root(), 1, 1, bestMask: 1, scrap => OnDisk(scrap));

        Assert.Equal(3, pictures.Count);
        Assert.EndsWith("news3.PNG", pictures[0].Art.Name);
        Assert.EndsWith("mag2.PNG", pictures[1].Art.Name);
        Assert.EndsWith("coin.PNG", pictures[2].Art.Name);
    }

    /// <summary>The focused scrap takes the original's own hover (<c>SCRAPBOOK.SCRIPT</c>'s 10002):
    /// two percent bigger, and last in the list so it stands over the neighbours it was under.
    /// </summary>
    [Fact]
    public void TheFocusedScrapDrawsLastAndSlightlyBigger()
    {
        // news3 stacks bottom by DrawOrder; focusing it must put it on top of the other two.
        var pictures = ScrapbookComposition.Pictures(
            Root(), 1, 1, bestMask: 1, scrap => OnDisk(scrap), focusedItem: 2);

        Assert.Equal(3, pictures.Count);
        Assert.EndsWith("news3.PNG", pictures[2].Art.Name);
        Assert.Equal(1.02f, pictures[2].Scale);
        Assert.Equal(1f, pictures[0].Scale);
        Assert.Equal(1f, pictures[1].Scale);
    }

    /// <summary>A capture's grime frame is lifted and grown with the capture it dirties, and the
    /// frame itself does not change as the cursor moves: the generator walks by picture index, so
    /// reordering during composition rather than after it would re-roll every smudge behind it.
    /// </summary>
    [Fact]
    public void AFocusedCaptureCarriesItsGrimeUpWithItAtTheSameFrame()
    {
        var resting = ScrapbookComposition.Pictures(Root(), 4, 1, bestMask: 1, scrap => OnDisk(scrap));
        var focused = ScrapbookComposition.Pictures(
            Root(), 4, 1, bestMask: 1, scrap => OnDisk(scrap), focusedItem: 1);

        // At rest: capture, grime, corner mount. Focused: the mount drops under the pair.
        Assert.EndsWith("DZ_generic_corners.PNG", focused[0].Art.Name);
        Assert.Equal(BoardArtLibrary.Loose, focused[1].Art.Library);
        Assert.Equal("SB_P_Grime.Png", focused[2].Art.Name);
        Assert.Equal(new[] { 1.02f, 1.02f }, new[] { focused[1].Scale, focused[2].Scale });
        Assert.Equal(resting[1].Frame, focused[2].Frame);
    }

    /// <summary>An item ordinal no visible scrap carries (the cursor on a button, or on a scrap the
    /// objective gate hides) leaves the page stacked exactly as it rests.</summary>
    [Fact]
    public void AnUnmatchedFocusLeavesTheStackAlone()
    {
        var resting = ScrapbookComposition.Pictures(Root(), 1, 1, bestMask: 1, scrap => OnDisk(scrap));
        var focused = ScrapbookComposition.Pictures(
            Root(), 1, 1, bestMask: 1, scrap => OnDisk(scrap), focusedItem: 99);

        Assert.Equal(resting, focused);
    }

    /// <summary>A scrap's title and body columns are langui symbols, and <c>RESRC1.H</c>'s own
    /// <c>#define</c> table is what turns one into the id the string table holds its text under.
    /// The CSV's <c>0</c> sentinel and a symbol the header lacks both resolve to nothing.</summary>
    [Fact]
    public void AScrapsStringSymbolResolvesThroughResrc1H()
    {
        string root = ScrapbookCompositionFixture.WriteResolvableScrap(TestData.TempDir(), mission: 1);

        Assert.Equal(40002, ScrapbookComposition.StringId(root, "IDS_TEST_TITLE"));
        Assert.Equal(40003, ScrapbookComposition.StringId(root, "IDS_TEST_BODY"));
        Assert.Null(ScrapbookComposition.StringId(root, "0"));
        Assert.Null(ScrapbookComposition.StringId(root, string.Empty));
        Assert.Null(ScrapbookComposition.StringId(root, "IDS_NOT_IN_THE_HEADER"));
        Assert.Null(ScrapbookComposition.StringId(null, "IDS_TEST_TITLE"));
    }

    [Fact]
    public void AnObjectiveOfZeroAlwaysDraws()
    {
        Assert.True(ScrapbookComposition.Visible(0, bestMask: 0));
    }

    [Fact]
    public void APositiveObjectiveNeedsTheMissionWonAndItsOwnBitSet()
    {
        const int primary = 1; // bit 0
        const int objective = 3;
        int bit = 1 << objective;

        Assert.False(ScrapbookComposition.Visible(objective, bestMask: 0)); // never won
        Assert.False(ScrapbookComposition.Visible(objective, bestMask: primary)); // won, bit clear
        Assert.True(ScrapbookComposition.Visible(objective, bestMask: primary | bit));
    }

    [Fact]
    public void ANegativeObjectiveNeedsTheMissionWonAndItsOwnBitClear()
    {
        const int primary = 1;
        const int objective = -3;
        int bit = 1 << 3;

        Assert.False(ScrapbookComposition.Visible(objective, bestMask: 0)); // never won
        Assert.False(ScrapbookComposition.Visible(objective, bestMask: primary | bit)); // bit set
        Assert.True(ScrapbookComposition.Visible(objective, bestMask: primary));
    }

    [Fact]
    public void ThePicturesGateFollowsVisibleRowByRow()
    {
        const int primary = 1;
        var withoutBit3 = ScrapbookComposition.Pictures(Root(), 2, 1, primary, scrap => OnDisk(scrap));
        Assert.Single(withoutBit3); // only the -3 row (bit 3 clear) draws; the +3 row's bit is unset
        Assert.Equal("SCRAPBOOK/SB_02_01_notgated.PNG", withoutBit3[0].Art.Name);

        var withBit3 = ScrapbookComposition.Pictures(Root(), 2, 1, primary | (1 << 3), scrap => OnDisk(scrap));
        Assert.Single(withBit3); // now the +3 row's gate passes and the -3 row's fails: still one
        Assert.Equal("SCRAPBOOK/SB_02_01_gated.PNG", withBit3[0].Art.Name);
    }

    [Fact]
    public void ACaptureIsSkippedWhenItsFileIsNotOnDisk()
    {
        var withoutFile = ScrapbookComposition.Pictures(Root(), 4, 1, bestMask: 1, scrap => null);
        Assert.Single(withoutFile); // just the corner mount, not the missing Snap_

        var withFile = ScrapbookComposition.Pictures(Root(), 4, 1, bestMask: 1, scrap => OnDisk(scrap));
        Assert.Equal(3, withFile.Count); // the capture, its grime, then the corner mount over both
    }

    /// <summary>A capture draws from the resolver's own path rather than the asset library, three
    /// and four pixels off its authored position, with a <c>SB_P_Grime</c> frame over it at the
    /// same place.</summary>
    [Fact]
    public void ACaptureCarriesItsOwnPathAndAGrimeFrameOverIt()
    {
        var pictures = ScrapbookComposition.Pictures(Root(), 4, 1, bestMask: 1, scrap => OnDisk(scrap));

        Assert.Equal(BoardArtLibrary.Loose, pictures[0].Art.Library);
        Assert.Equal(Path.Combine("profile", "Snap_4_1.PNG"), pictures[0].Art.Name);
        Assert.Equal(8f, pictures[0].X); // the row's own 5, plus 3
        Assert.Equal(10f, pictures[0].Y); // and its 6, plus 4

        Assert.Equal("SB_P_Grime.Png", pictures[1].Art.Name);
        Assert.Equal(10, pictures[1].Art.Frames);
        Assert.Equal(pictures[0].X, pictures[1].X);
        Assert.Equal(pictures[0].Y, pictures[1].Y);
        Assert.InRange(pictures[1].Frame, 0, 9);
    }

    /// <summary>The print is forced into the page's own 164x123 region rather than drawn at the
    /// file's size, so the smudge strip, which is cut at exactly that size, lands on it.</summary>
    [Fact]
    public void ACaptureIsForcedIntoThePagesOwnRegionSoItsGrimeCoversIt()
    {
        var pictures = ScrapbookComposition.Pictures(Root(), 4, 1, bestMask: 1, scrap => OnDisk(scrap));

        Assert.Equal(164f, pictures[0].Width);
        Assert.Equal(123f, pictures[0].Height);
    }

    /// <summary>The grime generator hands out each of the ten frames once before repeating, and a
    /// page's own sequence is the same every time it is turned to.</summary>
    [Fact]
    public void GrimeFramesAreStablePerPageAndUseAllTenBeforeRepeating()
    {
        var first = new List<int>();
        var again = new List<int>();
        var run = new ScrapbookGrime(1, 1);
        var rerun = new ScrapbookGrime(1, 1);
        for (int i = 0; i < 10; i++)
        {
            first.Add(run.Next(i));
            again.Add(rerun.Next(i));
        }

        Assert.Equal(first, again);
        Assert.Equal(10, new HashSet<int>(first).Count);
        Assert.NotEqual(first[0], new ScrapbookGrime(1, 2).Next(0)); // a neighbouring page differs
    }

    [Fact]
    public void ACaptureIsRecognisedByItsSnapPrefixAlone()
    {
        var snap = ScrapbookComposition.Items(Root(), 4, 1)[0];
        Assert.True(snap.IsCapture);
        Assert.Equal("Snap_4_1.PNG", snap.FileName);
        Assert.False(ScrapbookComposition.Items(Root(), 4, 1)[1].IsCapture); // the corner mount
    }

    [Fact]
    public void OpensIsGatedByTheZoomColumnAloneNotImageType()
    {
        // mag2 (1_1_3) ships ImageType "P0", the letter the plan's own Evidence line blamed for
        // "does not open"; its Zoom column ("M") is what actually decides it, and it opens.
        var mag2 = ScrapbookComposition.Items(Root(), 1, 1)[2];
        Assert.Equal('M', mag2.Zoom);
        Assert.True(mag2.Opens);

        // The corner mount (4_1_2) carries the same ImageType "P0" but Zoom "0": it does not open.
        var mount = ScrapbookComposition.Items(Root(), 4, 1)[1];
        Assert.Equal('0', mount.Zoom);
        Assert.False(mount.Opens);
    }

    [Fact]
    public void TheZoomInsetsExtensionComesFromImageTypesSecondLetterOnly()
    {
        // coin: "PJ" -> zoom inset is JPG even though the page image is PNG.
        var coin = ScrapbookComposition.Items(Root(), 1, 1)[0];
        Assert.Equal("PNG", coin.Extension);
        Assert.Equal("JPG", coin.ZoomExtension);

        // mag2: "P0" -> the second letter falls into the same else-PNG bucket as the first.
        var mag2 = ScrapbookComposition.Items(Root(), 1, 1)[2];
        Assert.Equal("PNG", mag2.ZoomExtension);
    }

    [Fact]
    public void OpenableCombinesTheObjectiveGateWithOpens()
    {
        const int primary = 1;
        var withoutBit3 = ScrapbookComposition.Openable(Root(), 2, 1, primary, scrap => OnDisk(scrap));
        Assert.Single(withoutBit3);
        Assert.Equal("SB_02_01_notgated", withoutBit3[0].ImageName);

        var withBit3 = ScrapbookComposition.Openable(Root(), 2, 1, primary | (1 << 3), scrap => OnDisk(scrap));
        Assert.Single(withBit3);
        Assert.Equal("SB_02_01_gated", withBit3[0].ImageName);
    }

    [Fact]
    public void ItemCarriesItsOwn1BasedOrdinal()
    {
        var items = ScrapbookComposition.Items(Root(), 1, 1);
        Assert.Equal(1, items[0].Item);
        Assert.Equal(2, items[1].Item);
        Assert.Equal(3, items[2].Item);
    }

    [Fact]
    public void ZoomFamilyReadsTheThreeBoxesForItsLetter()
    {
        string root = Root();
        WriteLayout(root, "SBZ_T_TITLEM    =T,!,60,25,0,525,85,0xff000000,0\n" +
                           "SBZ_T_CAPTIONM  =T,!,0,0,0,700,550,0xff000000,0\n" +
                           "SBZ_T_TEXTM     =T,!,60,113,0,525,487,0xff000000,0\n");

        var family = ScrapbookComposition.ZoomFamily(root, 'M');

        Assert.NotNull(family);
        Assert.Equal(60f, family!.Value.TitleX);
        Assert.Equal(25f, family.Value.TitleY);
        Assert.Equal(525f, family.Value.TitleWidth);
        Assert.Equal(85f, family.Value.TitleHeight);
        Assert.Equal(550f, family.Value.CaptionHeight);
        Assert.Equal(525f, family.Value.TextWidth);
        Assert.Equal(487f, family.Value.TextHeight);
    }

    [Fact]
    public void ZoomFamilyIsNullForAnUnknownLetterOrAMissingFile()
    {
        Assert.Null(ScrapbookComposition.ZoomFamily(Root(), 'Z')); // no LAYOUT.CSV written
        Assert.Null(ScrapbookComposition.ZoomFamily(null, 'M'));
    }

    // Writes the fixture into a fresh temp dataRoot's extracted/rof/ASSETS/SCRAPBOOK.CSV, the
    // path ScrapbookComposition.Items resolves against -- never the shipped file itself.
    // Stands in for CampaignFlow.CapturePath: every capture resolves, under a profile directory
    // that is not the asset library. Returning null instead is "no such file on disk".
    private static string OnDisk(ScrapbookScrap scrap) => Path.Combine("profile", scrap.FileName);

    private static string Root()
    {
        string root = TestData.TempDir();
        string dir = Path.Combine(root, "extracted", "rof", "ASSETS");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SCRAPBOOK.CSV"), Fixture);
        return root;
    }

    // Writes a minimal LAYOUT.CSV fixture (SBZ_T_* rows only) under an already-rooted dataRoot.
    private static void WriteLayout(string root, string body)
    {
        string dir = Path.Combine(root, "extracted", "rof", "ASSETS");
        File.WriteAllText(Path.Combine(dir, "LAYOUT.CSV"), body);
    }
}

/// <summary>The two claims that need the shipped install to check: every populated spread's art is
/// present, and CM01's story page reproduces scrap for scrap
/// (<c>OriginalScreenshots/Campaign Scrapbook CM01 Story Scraps.png</c>).</summary>
public class ScrapbookCompositionExtractedTests
{
    // Slots 1-24, spreads 1-3 (8 missions have one spread, 10 have two, 6 have three).
    [ExtractedDataFact]
    public void EveryPopulatedSpreadsArtIsPresentOnDisk()
    {
        string root = TestData.DataRoot!;
        string graphics = Path.Combine(root, "extracted", "rof", "ASSETS", "GRAPHICS", "SCRAPBOOK");
        int checkedFiles = 0;

        for (int mission = 0; mission <= 24; mission++)
        {
            for (int spread = 1; spread <= 3; spread++)
            {
                foreach (var scrap in ScrapbookComposition.Items(root, mission, spread))
                {
                    if (scrap.IsCapture)
                    {
                        continue; // a player capture, not shipped art (docs/org/debrief.md)
                    }

                    string path = Path.Combine(graphics, scrap.FileName);
                    Assert.True(File.Exists(path), $"missing {path} ({mission}_{spread})");
                    checkedFiles++;
                }
            }
        }

        // 461 rows total, 167 of them Snap_ captures (docs/formats/campaign-screens.md): the
        // remaining 294 are exactly A1's own "all 294 page images ... are present" count.
        Assert.Equal(294, checkedFiles);
    }

    [ExtractedDataFact]
    public void Cm01SStoryPageReproducesRows1To7()
    {
        string root = TestData.DataRoot!;
        var items = ScrapbookComposition.Items(root, mission: 1, spread: 2);

        Assert.Equal(7, items.Count);
        Assert.Equal("SB_01_01_wanted1", items[0].ImageName);
        Assert.Equal("SB_01_01_doc2", items[1].ImageName);
        Assert.Equal("SB_01_01_doc3", items[2].ImageName);
        Assert.Equal("DZ_generic_corners", items[3].ImageName);
        Assert.Equal("DZ_generic_corners", items[4].ImageName);
        Assert.Equal("Snap_1_18", items[5].ImageName);
        Assert.Equal("Snap_1_21", items[6].ImageName);
        Assert.True(items[5].IsCapture);
        Assert.True(items[6].IsCapture);

        // Every bit these rows gate on set: the three shipped scraps and both corner mounts draw;
        // the captures still do not, since CSVM saves none yet (D21's known gap, not invented).
        int mask = 1 | (1 << 1) | (1 << 3) | (1 << 12);
        var pictures = ScrapbookComposition.Pictures(root, 1, 2, mask, scrap => null);
        Assert.Equal(5, pictures.Count);
    }

    [ExtractedDataFact]
    public void Mag2OpensWithItsOwnTitleAndBodyKeys()
    {
        var mag2 = ScrapbookComposition.Items(TestData.DataRoot, mission: 1, spread: 1)[2];

        Assert.Equal("SB_01_02_mag2", mag2.ImageName);
        Assert.True(mag2.Opens);
        Assert.Equal("IDS_SB_01_01_mag2_t", mag2.TitleKey);
        Assert.Equal("IDS_SB_01_01_mag2_b", mag2.TextKey);
    }

    [ExtractedDataFact]
    public void APhotoCornerMountDoesNotOpen()
    {
        var mount = ScrapbookComposition.Items(TestData.DataRoot, mission: 1, spread: 2)[3];

        Assert.Equal("DZ_generic_corners", mount.ImageName);
        Assert.False(mount.Opens);
    }

    [ExtractedDataFact]
    public void EveryFamilyLetterAScrapNamesHasATextLayout()
    {
        string root = TestData.DataRoot!;
        var letters = new HashSet<char>();
        for (int mission = 0; mission <= 24; mission++)
        {
            for (int spread = 1; spread <= 3; spread++)
            {
                foreach (var scrap in ScrapbookComposition.Items(root, mission, spread))
                {
                    if (scrap.Opens)
                    {
                        letters.Add(scrap.Zoom);
                    }
                }
            }
        }

        Assert.NotEmpty(letters);
        foreach (var letter in letters)
        {
            Assert.True(
                ScrapbookComposition.ZoomFamily(root, letter) != null,
                $"no LAYOUT.CSV text layout for zoom family '{letter}'");
        }
    }
}
