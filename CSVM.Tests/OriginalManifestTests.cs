using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using Xunit;
using Xunit.Abstractions;

namespace CSVM.Tests;

/// <summary>
/// The Original asset manifest: what a hand-authored layout classifies as required and optional,
/// and what one check over a scratch tree answers when the tree is complete, missing a required
/// file, carrying a required file that does not read, missing an optional file, stamped behind
/// what Original reads, and repaired again. The install case reads the real decoded layout and
/// its own tree.
/// </summary>
public class OriginalManifestTests : IDisposable
{
    // A layout fixture with invented file names: a top level, a Preferences page carrying the
    // in-flight way back Original never draws, a messagebox carrying the multiplayer error box's
    // rows, one section Original composes nothing from, and the script-named files.
    private const string LayoutJson = """
    {
      "schema": 1,
      "screens": [
        {
          "section": "MainMenu",
          "widgets": [
            { "key": "MM_LOGO", "type": "P", "art": ["FX_Logo.png"] },
            { "key": "MM_B_CAMPAIGN", "type": "B", "art": ["FX_B_Campaign.png"] },
            { "key": "MOVIE", "type": "Z", "art": ["FX_Movie.MPG"] }
          ]
        },
        {
          "section": "Preferences",
          "widgets": [
            { "key": "PF_BACKGROUND", "type": "P", "art": ["FX_PF_BackGround.png"] },
            { "key": "PF_B_RETURNTOGAME", "type": "B", "art": ["FX_B_ReturnToGame.png"] }
          ]
        },
        {
          "section": "MessageBox",
          "widgets": [
            { "key": "MB_P_BACKGROUND", "type": "P", "art": ["FX_MB_Background.png"] },
            { "key": "MP_B_LEFT", "type": "B", "art": ["FX_MP_Buttons.png"] }
          ]
        },
        {
          "section": "MultiPlayerMain",
          "widgets": [
            { "key": "MP_B_SMALL", "type": "B", "art": ["FX_MP_Small.png"] }
          ]
        }
      ],
      "externalAssets": [
        { "path": "assets/graphics/activepointerz.png", "kind": "file", "script": "GLOBALS" },
        { "path": "assets/graphics/passivepointerz.png", "kind": "file", "script": "GLOBALS" },
        { "path": "assets/graphics/arial8.tga", "kind": "file", "script": "GLOBALS" },
        { "path": "assets/sounds/mouseclick.wav", "kind": "file", "script": "GLOBALS" },
        { "path": "assets/binaries/langui.dll", "kind": "file", "script": "GLOBALS" },
        { "path": "assets/graphics/", "kind": "fragment", "script": "GLOBALS" }
      ]
    }
    """;

    private readonly ITestOutputHelper _output;
    private readonly string _root;

    public OriginalManifestTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "csvm-original-manifest-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheManifestClassifiesTheRowsOriginalComposesAndNothingElse()
    {
        var manifest = OriginalAssetManifest.Derive(MenuLayout.Parse(LayoutJson));

        Assert.Equal(
            new[]
            {
                "FX_Logo.png", "FX_B_Campaign.png", "FX_PF_BackGround.png", "FX_MB_Background.png",
                "activepointerz.png", "passivepointerz.png", "arial8.tga", "PX_B_ReadyToExport.png", "PX_B_CancelExport.png",
            },
            Names(manifest, OriginalAssetNeed.Required));
        Assert.Equal(
            new[] { "FX_Movie.MPG", "FX_B_ReturnToGame.png", "FX_MP_Buttons.png", "FX_MP_Small.png", "mouseclick.wav" },
            Names(manifest, OriginalAssetNeed.Optional));

        // The string DLL is a file a script names and nothing here loads, so it is no entry at all.
        Assert.Null(manifest.Find("langui.dll"));
        Assert.Equal("MainMenu", manifest.Find("FX_Logo.png")!.Section);
        Assert.Contains("in-flight", manifest.Find("FX_B_ReturnToGame.png")!.Note);

        // The backdrop movie is drawn now and optional all the same, and it resolves one directory
        // deeper than every bitmap the same rows name.
        var movie = manifest.Find("FX_Movie.MPG")!;
        Assert.Equal(OriginalAssetNeed.Optional, movie.Need);
        Assert.Equal("ASSETS/GRAPHICS/MPG/FX_Movie.MPG", movie.RelativePath);
        Assert.Contains("drawing whole without it", movie.Note);
    }

    [Fact]
    public void ACompleteTreeIsAvailableAndReportsNothingDegraded()
    {
        Populate();

        var report = OriginalAssetManifest.Derive(MenuLayout.Parse(LayoutJson)).Check(_root);

        Assert.True(report.Complete, report.Reason);
        Assert.Equal(9, report.RequiredCount);
        Assert.Equal(5, report.OptionalCount);
        Assert.Null(report.Reason);
        Assert.Null(report.Degraded);
        Assert.NotNull(OriginalAvailability.Load(_root, out var reason, out var degraded));
        Assert.Null(reason);
        Assert.Null(degraded);
    }

    [Fact]
    public void AMissingRequiredFileNamesItsSectionAndRowAndSelectsBuiltInWithoutRewritingTheRequest()
    {
        Populate();
        File.Delete(Graphics("FX_B_Campaign.png"));

        var report = OriginalAssetManifest.Derive(MenuLayout.Parse(LayoutJson)).Check(_root);

        Assert.False(report.Complete);
        Assert.Contains("MainMenu.MM_B_CAMPAIGN names FX_B_Campaign.png (not there)", report.Reason);
        Assert.Contains($"schema {OriginalAssetManifest.Schema}", report.Reason);
        Assert.Null(OriginalAvailability.Load(_root, out var reason, out _));
        Assert.Equal(report.Reason, reason);

        var host = Host(reason);
        Assert.Equal(PresentationId.BuiltIn, host.Selected);
        Assert.Equal(PresentationId.Original, host.Requested);
    }

    [Fact]
    public void ARequiredFileThatDoesNotReadIsAsMissingAsAnAbsentOne()
    {
        Populate();
        var wrongSignature = new byte[24];
        new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8' }.CopyTo(wrongSignature, 0);
        File.WriteAllBytes(Graphics("FX_Logo.png"), wrongSignature);
        File.WriteAllBytes(Graphics("FX_PF_BackGround.png"), new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

        var report = OriginalAssetManifest.Derive(MenuLayout.Parse(LayoutJson)).Check(_root);

        Assert.False(report.Complete);
        Assert.Contains("MainMenu.MM_LOGO names FX_Logo.png (not a PNG)", report.Reason);
        Assert.Contains("Preferences.PF_BACKGROUND names FX_PF_BackGround.png (shorter than a PNG header)", report.Reason);
    }

    [Fact]
    public void AMissingOptionalFileStaysAvailableAndIsReportedOnceForTheScreenToDrawWithout()
    {
        Populate();
        File.Delete(Graphics("FX_MP_Small.png"));
        File.Delete(Path.Combine(_root, "extracted", "rof", "assets", "sounds", "mouseclick.wav"));

        Assert.NotNull(OriginalAvailability.Load(_root, out var reason, out var degraded));
        Assert.Null(reason);
        Assert.Contains("2 of 5 optional files are not there", degraded);
        Assert.Contains("FX_MP_Small.png", degraded);
        Assert.Contains("mouseclick.wav", degraded);
    }

    [Fact]
    public void ATreeStampedBehindWhatOriginalReadsIsARequiredFailureNamingTheReExtraction()
    {
        Populate();
        Stamp(0);

        Assert.Null(OriginalAvailability.Load(_root, out var reason, out _));
        Assert.Contains("stamped schema=0", reason);
        Assert.Contains("re-extract from the Extract screen", reason);

        Stamp(OriginalAssetManifest.StampSchema);
        Assert.NotNull(OriginalAvailability.Load(_root, out reason, out _));
        Assert.Null(reason);

        // No stamp at all is not a failure: extractions that predate the stamp still draw.
        File.Delete(Path.Combine(_root, "extracted", "VERSION.json"));
        Assert.NotNull(OriginalAvailability.Load(_root, out reason, out _));
        Assert.Null(reason);
    }

    [Fact]
    public void AssetsPutBackAreAvailableAgainOnTheNextCheckWithNoStateReset()
    {
        Populate();
        var manifest = OriginalAssetManifest.Derive(MenuLayout.Parse(LayoutJson));
        File.Delete(Graphics("FX_B_Campaign.png"));
        Assert.False(manifest.Check(_root).Complete);
        Assert.Null(OriginalAvailability.Load(_root, out _, out _));

        Png(Graphics("FX_B_Campaign.png"));

        Assert.True(manifest.Check(_root).Complete);
        Assert.NotNull(OriginalAvailability.Load(_root, out var reason, out _));
        Assert.Null(reason);
    }

    /// <summary>The install's own layout: the classification the inventory records, and a tree
    /// whose every required file is on disk and reads.</summary>
    [ExtractedDataFact]
    public void TheInstallsLayoutClassifiesItsFilesAndPassesItsOwnCheck()
    {
        string dataRoot = TestData.DataRoot!;
        var layout = MenuLayout.TryLoad(MenuLayout.PathUnder(dataRoot), out var why);
        Assert.True(layout != null, why ?? "the install's decoded layout reads");
        var manifest = OriginalAssetManifest.Derive(layout!);

        var report = manifest.Check(dataRoot);
        _output.WriteLine($"install: {manifest.RequiredCount} required, {manifest.OptionalCount} optional, "
            + $"{report.RequiredFaults.Count} required faults, {report.OptionalFaults.Count} optional absences");
        _output.WriteLine(report.Degraded ?? "nothing optional is absent");
        Assert.True(report.Complete, report.Reason);
        Assert.Equal(OriginalAssetNeed.Required, manifest.Find("MB_Background.png")!.Need);
        // The About box draws its own background, so that file is required like the shared box's.
        Assert.Equal(OriginalAssetNeed.Required, manifest.Find("CR_AboutMessageBox.png")!.Need);
        Assert.Equal(OriginalAssetNeed.Required, manifest.Find("PC_B_ReturnMainMenu.png")!.Need);
        Assert.Equal(OriginalAssetNeed.Optional, manifest.Find("GN_B_ReturnToGame.Png")!.Need);
        Assert.Equal(OriginalAssetNeed.Optional, manifest.Find("CrimFlag.MPG")!.Need);
        var loaded = OriginalAvailability.Load(dataRoot, out var reason, out _);
        Assert.True(loaded != null, reason);
    }

    private static string[] Names(OriginalAssetManifest manifest, OriginalAssetNeed need) =>
        manifest.Assets.Where(a => a.Need == need).Select(a => a.Name).ToArray();

    // A 1x1 PNG header, which is all the check reads.
    private static void Png(string path)
    {
        var bytes = new byte[24];
        byte[] signature = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        signature.CopyTo(bytes, 0);
        bytes[19] = 1;
        bytes[23] = 1;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static MenuHost Host(string? unavailable)
    {
        var registry = new PresentationRegistry();
        registry.Register(PresentationId.BuiltIn, () => new FakePointerPresentation());
        registry.Register(PresentationId.Original, () => new FakeWizardPresentation());
        var host = new MenuHost(registry, new RecordingAudio(), _ => { });
        host.Availability = id => id == PresentationId.Original ? unavailable : null;
        host.Select(forceBuiltIn: false, cliOverride: PresentationId.Original.Value);
        return host;
    }

    private string Graphics(string name) =>
        Path.Combine(_root, "extracted", "rof", "ASSETS", "GRAPHICS", name);

    // The scratch tree: the layout artifact, a stamp Original reads, and every file the fixture
    // names, the .png ones as headers and the rest as a byte.
    private void Populate()
    {
        string rof = Path.Combine(_root, "extracted", "rof");
        Directory.CreateDirectory(rof);
        File.WriteAllText(Path.Combine(rof, "menu_layout.json"), LayoutJson);
        Stamp(OriginalAssetManifest.StampSchema);
        var manifest = OriginalAssetManifest.Derive(MenuLayout.Parse(LayoutJson));
        foreach (var asset in manifest.Assets)
        {
            string path = asset.PathUnder(_root);
            if (asset.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                Png(path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[] { 1 });
            }
        }
    }

    private void Stamp(int schema)
    {
        string extracted = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(extracted);
        File.WriteAllText(Path.Combine(extracted, "VERSION.json"), $"{{ \"schema\": {schema} }}");
    }
}
