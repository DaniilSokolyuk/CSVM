using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using CSVM.Extraction;
using Xunit;

namespace CSVM.Tests;

/// <summary>The menu-layout decode the extraction runs: sectioned CSV, the two macro
/// mechanisms, the per-type field orders, the string join and the script cross-check. The
/// fixtures under <c>fixtures/menu-layout/</c> are hand-authored from
/// docs/formats/menu-layout.md, so every field order here is pinned without an extraction.</summary>
public class MenuLayoutDecoderTests
{
    [Fact]
    public void EachSectionBecomesOneScreenBoundToTheScriptOfTheSameName()
    {
        var doc = MenuLayoutDecoder.Decode(Input());

        Assert.Equal(new[] { "ProbeMain", "ProbeOptions" }, doc.Screens.Select(s => s.Section));
        Assert.Equal("ASSETS/SCRIPTS/ProbeMain.SCRIPT", doc.Screens[0].Script);
        Assert.Equal(string.Empty, doc.Screens[1].Script);
        Assert.Contains(doc.Warnings, w => w.Contains("no script of the same name: ProbeOptions"));
    }

    [Fact]
    public void ASectionMacroBeatsTheFileWideMacroOfTheSameName()
    {
        var doc = MenuLayoutDecoder.Decode(Input());

        // ROWH is 15 file-wide and 20 in ProbeMain; the shipped file never collides the two, so
        // this precedence is pinned here rather than by the install.
        Assert.Equal("20", Field(Widget(doc, "ProbeMain", "PR_T_HINT"), "Height"));
        Assert.Equal("15", Field(Widget(doc, "ProbeOptions", "PO_D_QUALITY"), "ItemHeight"));
    }

    [Fact]
    public void AMacroThatResolvesKeepsItsAuthoredTokenBesideTheValue()
    {
        var doc = MenuLayoutDecoder.Decode(Input());
        var title = Widget(doc, "ProbeMain", "PR_T_TITLE");

        Assert.Equal("44", Field(title, "Y"));
        Assert.Equal("<TITLEY>", title.Fields.Single(f => f.Name == "Y").Raw);
    }

    [Fact]
    public void AnUnresolvableMacroIsEmittedRawAndReported()
    {
        var doc = MenuLayoutDecoder.Decode(Input());

        Assert.Equal("<NOSUCHMACRO>", Field(Widget(doc, "ProbeOptions", "PO_T_RUNTIME"), "Height"));
        Assert.Equal(new[] { "ProbeOptions.PO_T_RUNTIME <NOSUCHMACRO>" }, doc.UnresolvedMacros);
    }

    [Fact]
    public void AButtonRowPutsResIdAndScriptToExeAdjacentWithNoTabOrderBetweenThem()
    {
        var doc = MenuLayoutDecoder.Decode(Input());
        var start = Widget(doc, "ProbeMain", "PR_B_START");

        Assert.Equal("0", Field(start, "ResID"));
        Assert.Equal("ProbeOptions", Field(start, "ScriptToExe"));
        Assert.Equal("0x1000", Field(start, "ScriptPri"));
        Assert.Equal("1", Field(start, "EndScript"));
        Assert.DoesNotContain(start.Fields, f => f.Name == "ColorDisabled");
    }

    [Fact]
    public void TheFourButtonColoursAreDisabledActiveRolloverDepressedInThatOrder()
    {
        var doc = MenuLayoutDecoder.Decode(Input());
        var quit = Widget(doc, "ProbeMain", "PR_B_QUIT");

        Assert.Equal("0xFFBCBCBC", Field(quit, "ColorDisabled"));
        Assert.Equal("0xFFFFFFFF", Field(quit, "ColorActive"));
        Assert.Equal("0xFFEEEEEE", Field(quit, "ColorRollover"));
        Assert.Equal("0xFF000000", Field(quit, "ColorDepressed"));
    }

    [Fact]
    public void ATrailingCommaDoesNotBecomeAFieldOrAWarning()
    {
        var doc = MenuLayoutDecoder.Decode(Input());
        var back = Widget(doc, "ProbeOptions", "PO_B_BACK");

        Assert.Equal(14, back.Fields.Count);
        Assert.DoesNotContain(doc.Warnings, w => w.Contains("PO_B_BACK"));
    }

    [Fact]
    public void ACheckButtonDividesItsArtIntoEightFramesAndAPlainButtonIntoFour()
    {
        var doc = MenuLayoutDecoder.Decode(Input());

        Assert.Equal(8, Widget(doc, "ProbeMain", "PR_B_SOUND").Frames);
        Assert.Equal(4, Widget(doc, "ProbeMain", "PR_B_START").Frames);
        Assert.Equal(1, Widget(doc, "ProbeMain", "PR_BACKGROUND").Frames);
    }

    [Fact]
    public void AResIdSymbolIsJoinedThroughTheHeaderToItsTextAndKeptBesideIt()
    {
        var doc = MenuLayoutDecoder.Decode(Input());
        var title = Widget(doc, "ProbeMain", "PR_T_TITLE");

        Assert.Equal("IDS_PR_TITLE", title.ResIdSymbol);
        Assert.Equal(100, title.ResId);
        Assert.Equal("Probe Title", title.Text);
        Assert.Equal("resource", title.TextSource);
    }

    [Fact]
    public void AnEmptyOrBangResIdMeansTheScriptSuppliesTheString()
    {
        var doc = MenuLayoutDecoder.Decode(Input());

        Assert.Equal("none", Widget(doc, "ProbeMain", "PR_T_HINT").TextSource);
        Assert.Equal("runtime", Widget(doc, "ProbeOptions", "PO_T_RUNTIME").TextSource);
    }

    [Fact]
    public void ASymbolTheHeaderDoesNotDefineIsReportedRatherThanDropped()
    {
        var doc = MenuLayoutDecoder.Decode(Input());
        var back = Widget(doc, "ProbeOptions", "PO_B_BACK");

        Assert.Equal("IDS_PR_MISSING", back.ResIdSymbol);
        Assert.Equal(-1, back.ResId);
        Assert.Equal("unresolved-symbol", back.TextSource);
        Assert.Equal(new[] { "IDS_PR_MISSING" }, doc.UnresolvedStrings);
    }

    [Fact]
    public void AListRowsLastFieldIsItsVisibleRowWindowNotAnItemCount()
    {
        var doc = MenuLayoutDecoder.Decode(Input());
        var list = Widget(doc, "ProbeMain", "PR_L_ITEMS");

        Assert.Equal("300", Field(list, "Width"));
        Assert.Equal("20", Field(list, "ItemHeight"));
        Assert.Equal("7", Field(list, "TotalDisplayed"));
    }

    [Fact]
    public void EveryButtonWithAScriptToExeTargetBecomesOneNavigationEdge()
    {
        var doc = MenuLayoutDecoder.Decode(Input());

        Assert.Equal(2, doc.Navigation.Count);
        var edge = doc.Navigation[0];
        Assert.Equal("ProbeMain", edge.From);
        Assert.Equal("PR_B_START", edge.Widget);
        Assert.Equal("ProbeOptions", edge.To);
        Assert.Equal("0x1000", edge.Priority);
        Assert.Equal("1", edge.EndScript);
    }

    [Fact]
    public void AKeyTheScriptCreatesWithNoLayoutRowIsCarriedBesideTheLayoutsOwn()
    {
        var doc = MenuLayoutDecoder.Decode(Input());
        var main = doc.Screens[0];

        Assert.Equal(new[] { "pr_t_runtime" }, main.KeysWithoutLayoutRow);
        Assert.Contains(main.ScriptWidgets, w => w.Key == "pr_t_title" && w.Class == "@ctl@PE" && w.HasLayoutRow);
        Assert.Contains("PR_M_FLAG", main.RowsNoScriptCreates);
        Assert.Equal(new[] { "assets/scripts/probeoptions.script" }, main.ScriptRefs);
    }

    [Fact]
    public void ArtNamedOutsideTheLayoutIsCarriedWithItsCompleteOrFragmentaryForm()
    {
        var doc = MenuLayoutDecoder.Decode(Input());

        Assert.Contains(doc.ExternalAssets, a => a.Path == "assets/sounds/probeclick.wav" && a.Kind == "file" && a.Present);
        Assert.Contains(doc.ExternalAssets, a => a.Path == "assets/graphics/pr_p_photo" && a.Kind == "fragment");
        Assert.Contains(doc.ExternalAssets, a => a.Path == ".jpg" && a.Kind == "fragment");
    }

    [Fact]
    public void ArtTheArchiveDoesNotShipIsListedRatherThanSilentlyMissing()
    {
        var doc = MenuLayoutDecoder.Decode(Input());

        Assert.Equal(new[] { "ProbeFlag.MPG" }, doc.MissingArt);
    }

    [Fact]
    public void TheScrapbooksQuotedRectangleStaysOneField()
    {
        var doc = MenuLayoutDecoder.Decode(Input());
        var first = doc.Scrapbook[0];

        Assert.Equal("1_2_3", first.Key);
        Assert.Equal(1, first.Mission);
        Assert.Equal(2, first.Spread);
        Assert.Equal(3, first.Item);
        Assert.Equal("11,22,33,44", first.Fields.Single(f => f.Name == "ZoomRect").Value);
        Assert.Equal(16, first.Fields.Count);
    }

    [Fact]
    public void AStrayLineAndAnUnknownTypeAreReportedAndTheRestOfTheFileStillDecodes()
    {
        var doc = MenuLayoutDecoder.Decode(Input());

        Assert.Contains(doc.Warnings, w => w.Contains("neither a section, a comment nor a KEY=VALUE"));
        Assert.Contains(doc.Warnings, w => w.Contains("unknown widget type 'X'"));
        Assert.DoesNotContain(doc.Screens[1].Widgets, w => w.Key == "PO_X_BOGUS");
        Assert.Equal(8, doc.Screens[0].Widgets.Count);
    }

    [Fact]
    public void ThePatchOverlaysPrecedenceIsStatedBecauseTheExtractedTreeDoesNot()
    {
        var doc = MenuLayoutDecoder.Decode(Input());

        Assert.Equal("_crimptch", doc.Patch.Root);
        Assert.Contains("patch-wins", doc.Patch.Rule);
        Assert.Equal(new[] { "ASSETS/SCRIPTS/PROBEMAIN.SCRIPT" }, doc.Patch.Members);
    }

    [Fact]
    public void TheEmittedJsonParsesAndCarriesTheWidgetItsFieldsAndItsAuthoredTokens()
    {
        var doc = MenuLayoutDecoder.Decode(Input());

        using var json = JsonDocument.Parse(MenuLayoutDecoder.ToJson(doc));
        var root = json.RootElement;
        Assert.Equal(MenuLayoutDecoder.SchemaVersion, root.GetProperty("schema").GetInt32());
        var screen = root.GetProperty("screens")[0];
        var title = screen.GetProperty("widgets").EnumerateArray().Single(w => w.GetProperty("key").GetString() == "PR_T_TITLE");
        Assert.Equal("44", title.GetProperty("fields").GetProperty("Y").GetString());
        Assert.Equal("<TITLEY>", title.GetProperty("authored").GetProperty("Y").GetString());
        Assert.Equal("Probe Title", title.GetProperty("text").GetString());
        Assert.Equal(2, root.GetProperty("counts").GetProperty("screens").GetInt32());
        Assert.Equal(12, root.GetProperty("counts").GetProperty("widgets").GetInt32());
    }

    [Fact]
    public void TheWidgetTypeTableDeclaresEveryFieldsKindSoAReaderNeedsNoTypeTable()
    {
        var doc = MenuLayoutDecoder.Decode(Input());
        var button = doc.WidgetTypes.Single(t => t.Type == "B");

        Assert.Equal(11, doc.WidgetTypes.Count);
        Assert.Equal(button.Fields.Count, button.Kinds.Count);
        Assert.Equal("resid", button.Kinds[button.Fields.IndexOf("ResID")]);
        Assert.Equal("script", button.Kinds[button.Fields.IndexOf("ScriptToExe")]);
        Assert.Equal("art", button.Kinds[button.Fields.IndexOf("ArtPath")]);
    }

    [ExtractedDataFact]
    public void TheInstallsOwnLayoutDecodesToItsDocumentedCensus()
    {
        var rof = Path.Combine(TestData.ExtractedRoot!, "rof");
        if (!File.Exists(Path.Combine(rof, "ASSETS", "LAYOUT.CSV")))
        {
            return;
        }

        var doc = MenuLayoutDecoder.Decode(MenuLayoutDecoder.ReadTree(rof, new Dictionary<int, string>()));
        var counts = doc.Counts.ToDictionary(c => c.Name, c => c.Value);

        Assert.Equal(34, counts["screens"]);
        Assert.Equal(636, counts["widgets"]);
        Assert.Equal(186, counts["macros"]);
        Assert.Equal(0, counts["macrosUnresolved"]);
        Assert.Equal(46, counts["navigationEdges"]);
        Assert.Equal(124, counts["artReferences"]);
        // The archive carries no backdrop movie: the two the layout names sit loose in the install
        // and reach the tree only when the extraction copies them in, so this count is whichever
        // of them is still absent, 2 before that step and 0 after it.
        string mpg = Path.Combine(rof, "ASSETS", "GRAPHICS", "MPG");
        string[] movies = { "CrimFlag.MPG", "Final.MPG" };
        Assert.Equal(movies.Count(m => !File.Exists(Path.Combine(mpg, m))), counts["artMissing"]);
        Assert.Equal(152, counts["stringSymbols"]);
        Assert.Equal(461, counts["scrapbookEntries"]);
    }

    private static MenuLayoutInput Input()
    {
        var input = new MenuLayoutInput
        {
            LayoutCsv = File.ReadAllText(TestData.Fixture("menu-layout", "LAYOUT.CSV"), Encoding.ASCII),
            ScrapbookCsv = File.ReadAllText(TestData.Fixture("menu-layout", "SCRAPBOOK.CSV"), Encoding.ASCII),
            ResourceHeader = File.ReadAllText(TestData.Fixture("menu-layout", "RESOURCE.H"), Encoding.ASCII),
            Strings = new Dictionary<int, string> { [100] = "Probe Title", [101] = "Quit", [102] = "Sound" },
            ArchiveFiles = new List<string>
            {
                "GRAPHICS/PR_BackGround.png", "GRAPHICS/PR_B_Start.png", "GRAPHICS/PR_B_Plaque.png",
                "GRAPHICS/PR_B_Check8States.png", "GRAPHICS/PR_B_ScrollUp.png",
                "GRAPHICS/PR_B_ScrollDown.png", "GRAPHICS/PR_B_ScrollBar.png",
                "GRAPHICS/PO_BackGround.png", "GRAPHICS/probepointer.png", "SOUNDS/probeclick.wav",
            },
            PatchMembers = new List<string> { "ASSETS/SCRIPTS/PROBEMAIN.SCRIPT" },
        };
        input.Scripts.Add(new MenuScriptSource
        {
            Name = "ProbeMain",
            Text = File.ReadAllText(TestData.Fixture("menu-layout", "PROBEMAIN.SCRIPT"), Encoding.ASCII),
        });
        return input;
    }

    private static MenuWidget Widget(MenuLayoutDocument doc, string section, string key)
    {
        return doc.Screens.Single(s => s.Section == section).Widgets.Single(w => w.Key == key);
    }

    private static string Field(MenuWidget w, string name)
    {
        return w.Fields.Single(f => f.Name == name).Value;
    }
}
