using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CSVM.Bindings;
using CSVM.Flight;
using CSVM.Mech3;
using CSVM.Session;
using CSVM.UI;
using CSVM.UI.Menu;
using CSVM.UI.Menu.Original;
using Xunit;
using Xunit.Abstractions;

namespace CSVM.Tests;

/// <summary>
/// The Original presentation's coverage check: every in-scope screen of the inventory is reached
/// from the top level and left back to it by pointer hit-testing on its rows, by keyboard cursor
/// commands and by pad cursor commands; every navigation edge the layout states for an in-scope
/// section is either driven, drawn disabled, or recorded out of scope with its reason; no screen is
/// a dead end (each has a way back that lands on the top level and leaves no open campaign, build or
/// dialog); and every exit a screen offers arrives as its typed exit. The fixture run pins the graph
/// shape; the install run walks the same journeys over the real decoded layout and art.
/// </summary>
public class OriginalCoverageTests : IDisposable
{
    private const string Pilot = "Zachary";
    private const string SecondPilot = "Nathan";
    private const string SparePlane = "Spare";
    private const string BuildName = "Ace";
    private const int WalkGuard = 96;
    private const int BackGuard = 12;

    // The route steps: a row key to press, or a directive.
    private const string TypeStep = "*type:";
    private const string EraseStep = "*erase";
    private const string ScrapStep = "*scrap";
    private const string JoinStep = "*join";

    // The wrap-up page is opened by a flown mission's ending, not by any row, so the walk hands the
    // shell an ended run the way the presentation's return does.
    private const string WrapupStep = "*wrapup";

    private static readonly MenuCommands Accept = new() { Accept = true };
    private static readonly MenuCommands Back = new() { Back = true };
    private static readonly MenuCommands Erase = new() { Erase = true };

    // The layout sections this plan puts in scope; every other section's edges are out of scope by
    // the inventory's own list and are counted, not driven.
    private static readonly string[] InScopeSections =
    {
        "MainMenu", "Preferences", "GameOptions", "Audio", "Video", "ControlsPrefs", "Keys", "Credits",
        "InstantAction", "IA_WrapUp", "Campaign",
        "PassengerCabin", "MomentoSelection", "FlightCheck",
        "PlaneSelection", "OrdinanceLayout", "ScrapBook", "ScrapBook_TOC", "ScrapbookZoom", "Hangar", "PlaneName",
        "PlaneConstruction", "AirFrame", "Engine", "Armor", "Guns", "HardPoints", "Paint", "Purchase", "MessageBox",
    };

    private static readonly string[] Cabin ={ OriginalShell.CampaignKey, "Continue" };
    private static readonly string[] Build = { "MM_B_INSTANTACTION", OriginalInstantActionScreen.BuildKey };
    private static readonly string[] Hub = { "MM_B_INSTANTACTION", OriginalInstantActionScreen.BuildKey, TypeStep + BuildName, OriginalHangarScreen.NameOkKey };

    // Out of the wallet-free hangar: CANCEL lands on the Instant Action screen, whose Exit leaves.
    private static readonly string[] CancelBuild = { OriginalHangarScreen.CancelBuildKey, OriginalInstantActionScreen.ExitKey };

    private static readonly Journey[] Journeys =
    {
        new("free-flight", OriginalScreen.FreeFlight, new[] { OriginalShell.FreeFlightKey }, new[] { OriginalShell.BackKey }),
        new("dogfight", OriginalScreen.Dogfight, new[] { OriginalShell.DogfightKey }, new[] { OriginalShell.BackKey }),
        // ⚠ The cursor families walk this screen as the picking seat, not as seat 0, which drives
        // nothing here; only the pointer stays seat 0's. ⚠ Leave the map unpicked: the way off by
        // row is the walk's completion, which is the launch itself once FLY's gate is met.
        new("seat-plane", OriginalScreen.SeatPlane, new[] { OriginalShell.FreeFlightKey, JoinStep, OriginalShell.AirframeKey(0) },
            new[] { OriginalShell.SeatPlaneFieldKey, "AcceptSelections", OriginalShell.BackKey }),
        new("options", OriginalScreen.Options, new[] { "MM_B_PREFERENCES" }, new[] { OriginalShell.OptionsBackKey }),
        new("game-options", OriginalScreen.GameOptions, new[] { "MM_B_PREFERENCES", OriginalOptionsScreen.GameOptionsDoorKey },
            new[] { OriginalOptionsScreen.GameOptionsCancelKey, OriginalShell.OptionsBackKey }),
        new("audio", OriginalScreen.Audio, new[] { "MM_B_PREFERENCES", OriginalOptionsScreen.AudioDoorKey },
            new[] { OriginalOptionsScreen.AudioCancelKey, OriginalShell.OptionsBackKey }),
        new("video", OriginalScreen.Video, new[] { "MM_B_PREFERENCES", OriginalOptionsScreen.VideoDoorKey },
            new[] { OriginalOptionsScreen.VideoCancelKey, OriginalShell.OptionsBackKey }),
        new("controls", OriginalScreen.ControlsPrefs, new[] { "MM_B_PREFERENCES", OriginalOptionsScreen.ControlsDoorKey },
            new[] { OriginalOptionsScreen.ControlsCancelKey, OriginalShell.OptionsBackKey }),
        new("controls-accept", OriginalScreen.Options,
            new[] { "MM_B_PREFERENCES", OriginalOptionsScreen.ControlsDoorKey, OriginalOptionsScreen.ControlsAcceptKey },
            new[] { OriginalShell.OptionsBackKey }),
        new("keys", OriginalScreen.Keys,
            new[] { "MM_B_PREFERENCES", OriginalOptionsScreen.ControlsDoorKey, OriginalOptionsScreen.KeysDoorKey },
            new[] { OriginalOptionsScreen.KeysCancelKey, OriginalOptionsScreen.ControlsCancelKey, OriginalShell.OptionsBackKey }),
        new("keys-accept", OriginalScreen.ControlsPrefs,
            new[] { "MM_B_PREFERENCES", OriginalOptionsScreen.ControlsDoorKey, OriginalOptionsScreen.KeysDoorKey, OriginalOptionsScreen.KeysAcceptKey },
            new[] { OriginalOptionsScreen.ControlsCancelKey, OriginalShell.OptionsBackKey }),
        new("credits", OriginalScreen.Credits, new[] { OriginalShell.CreditsDoorKey }, new[] { OriginalShell.CreditsExitKey }),
        new("credits-about", OriginalScreen.Credits, new[] { OriginalShell.CreditsDoorKey, OriginalShell.CreditsAboutKey },
            new[] { OriginalShell.DialogOkKey, OriginalShell.CreditsExitKey },
            Expect: new[] { OriginalShell.DialogOkKey }),
        new("instant-action", OriginalScreen.InstantAction, new[] { "MM_B_INSTANTACTION" }, new[] { OriginalInstantActionScreen.ExitKey }),
        new("instant-action-exit", OriginalScreen.TopLevel, new[] { "MM_B_INSTANTACTION", OriginalInstantActionScreen.ExitKey }, Array.Empty<string>()),
        new("instant-action-loadout", OriginalScreen.InstantActionLoadout, new[] { "MM_B_INSTANTACTION", OriginalInstantActionScreen.WeaponLoadoutKey },
            new[] { OriginalInstantActionScreen.LoadoutCancelKey, OriginalInstantActionScreen.ExitKey }),
        new("instant-action-loadout-accept", OriginalScreen.InstantAction,
            new[] { "MM_B_INSTANTACTION", OriginalInstantActionScreen.WingmanRadioKey, OriginalInstantActionScreen.WeaponLoadoutKey, OriginalInstantActionScreen.LoadoutAcceptKey },
            new[] { OriginalInstantActionScreen.ExitKey }),
        new("instant-action-wrapup", OriginalScreen.InstantActionWrapup, new[] { "MM_B_INSTANTACTION", WrapupStep },
            new[] { OriginalWrapupScreen.ContinueKey, OriginalInstantActionScreen.ExitKey }),
        new("multiplayer-connection", OriginalScreen.Connection, new[] { OriginalShell.MultiplayerKey },
            new[] { OriginalConnectionScreen.ExitKey }),
        new("multiplayer-games", OriginalScreen.ConnectionGames,
            new[] { OriginalShell.MultiplayerKey, OriginalConnectionScreen.ConnectKey },
            new[] { OriginalConnectionScreen.CancelKey, OriginalConnectionScreen.ExitKey }),
        new("multiplayer-lobby", OriginalScreen.Lobby, new[] { OriginalShell.MultiplayerKey, OriginalConnectionScreen.HostKey },
            new[] { OriginalLobbyScreen.LeaveKey, OriginalConnectionScreen.ExitKey }),
        new("multiplayer-lobby-plane", OriginalScreen.Lobby,
            new[] { OriginalShell.MultiplayerKey, OriginalConnectionScreen.HostKey, OriginalLobbyScreen.PlaneTabKey },
            new[] { OriginalLobbyScreen.LeaveKey, OriginalConnectionScreen.ExitKey }),
        new("multiplayer-lobby-rockets", OriginalScreen.Lobby,
            new[] { OriginalShell.MultiplayerKey, OriginalConnectionScreen.HostKey, OriginalLobbyScreen.AmmoTabKey, OriginalLobbyScreen.RocketsTabKey },
            new[] { OriginalLobbyScreen.LeaveKey, OriginalConnectionScreen.ExitKey }),
        new("campaign-roster", OriginalScreen.CampaignRoster, new[] { OriginalShell.CampaignKey }, new[] { "CancelProfile" }),
        new("campaign-cabin", OriginalScreen.CampaignCabin, Cabin, new[] { "ReturnToMainMenu" }),
        new("campaign-memento", OriginalScreen.CampaignMemento, Then(Cabin, "ChangeMemento"), new[] { "CancelMemento", "ReturnToMainMenu" }),
        new("campaign-memento-accept", OriginalScreen.CampaignCabin, Then(Cabin, "ChangeMemento", "AcceptMemento"), new[] { "ReturnToMainMenu" }),
        new("campaign-previous", OriginalScreen.CampaignPreviousMissions, Then(Cabin, "PreviousMissions"), new[] { "ReturnToCabin", "ReturnToMainMenu" }),
        new("campaign-briefing", OriginalScreen.CampaignBriefing, Then(Cabin, "NextMission"), new[] { "ReturnToCabin", "ReturnToMainMenu" }),
        new("campaign-replay-briefing", OriginalScreen.CampaignBriefing, Then(Cabin, "PreviousMissions", "ReplayMission"), new[] { "ReturnToCabin", "ReturnToMainMenu" }),
        new("campaign-flightcheck", OriginalScreen.CampaignFlightCheck, Then(Cabin, "NextMission", "GoToFlightCheck"), new[] { "ReturnToBriefing", "ReturnToCabin", "ReturnToMainMenu" }),
        new("campaign-ammo", OriginalScreen.CampaignAmmo, Then(Cabin, "NextMission", "GoToFlightCheck", "ChangeAmmo"), new[] { "CancelLoadout", "ReturnToBriefing", "ReturnToCabin", "ReturnToMainMenu" }),
        new("campaign-ammo-accept", OriginalScreen.CampaignFlightCheck, Then(Cabin, "NextMission", "GoToFlightCheck", "ChangeAmmo", "AcceptLoadout"), new[] { "ReturnToBriefing", "ReturnToCabin", "ReturnToMainMenu" }),
        new("campaign-planeselection", OriginalScreen.CampaignPlaneSelection, Then(Cabin, "NextMission", "GoToFlightCheck", "ChangePlane"), new[] { "CancelSelections", "ReturnToBriefing", "ReturnToCabin", "ReturnToMainMenu" }),
        new("campaign-planeselection-accept", OriginalScreen.CampaignFlightCheck, Then(Cabin, "NextMission", "GoToFlightCheck", "ChangePlane", "AcceptSelections"), new[] { "ReturnToBriefing", "ReturnToCabin", "ReturnToMainMenu" }),
        new("campaign-scrapbook", OriginalScreen.CampaignScrapbook, Then(Cabin, "PreviousMissions", "ROW:0", "ViewMission"), new[] { "ReturnToCabin", "ReturnToMainMenu" }),
        new("campaign-scrapbook-zoom", OriginalScreen.CampaignScrapbookZoom, Then(Cabin, "PreviousMissions", "ROW:0", "ViewMission", ScrapStep), new[] { "CloseZoom", "ReturnToCabin", "ReturnToMainMenu" }, InstallOnly: true),
        new("campaign-hangar-door", OriginalScreen.PlaneName, Then(Cabin, "PlaneConstruction"), new[] { OriginalHangarScreen.NameCancelKey, "ReturnToMainMenu" }),
        new("plane-name", OriginalScreen.PlaneName, Build, new[] { OriginalHangarScreen.NameCancelKey, OriginalInstantActionScreen.ExitKey }),
        new("plane-airframe", OriginalScreen.HangarAirframe, Hub, CancelBuild),
        new("plane-engine", OriginalScreen.HangarEngine, Then(Hub, "PX_B_ENGINE"), CancelBuild),
        new("plane-armor", OriginalScreen.HangarArmor, Then(Hub, "PX_B_ARMOR"), CancelBuild),
        new("plane-guns", OriginalScreen.HangarGuns, Then(Hub, "PX_B_GUNS"), CancelBuild),
        new("plane-hardpoints", OriginalScreen.HangarHardpoints, Then(Hub, "PX_B_HARDPOINTS"), CancelBuild),
        new("plane-paint", OriginalScreen.HangarPaint, Then(Hub, "PX_B_PAINT"), CancelBuild),
        new("plane-paint-then-airframe", OriginalScreen.HangarAirframe, Then(Hub, "PX_B_PAINT", "PX_B_AIRFRAME"), CancelBuild),
        new("plane-purchase", OriginalScreen.HangarPurchase, Then(Hub, OriginalHangarScreen.ReadyKey), CancelBuild),
        new("plane-inventory", OriginalScreen.HangarInventory, Then(Hub, OriginalHangarScreen.SellPlanesKey), Then(new[] { OriginalHangarScreen.InventoryDoneKey }, CancelBuild)),
        new("delete-player-confirm", OriginalScreen.CampaignRoster, new[] { OriginalShell.CampaignKey, "DeletePlayer" }, new[] { OriginalShell.DialogNoKey, "CancelProfile" },
            Expect: new[] { OriginalShell.DialogYesKey, OriginalShell.DialogNoKey }),
        new("empty-name-refusal", OriginalScreen.CampaignRoster, new[] { OriginalShell.CampaignKey, EraseStep, "Continue" }, new[] { OriginalShell.DialogOkKey, "CancelProfile" },
            Expect: new[] { OriginalShell.DialogOkKey }),
        new("empty-plane-name-refusal", OriginalScreen.PlaneName, Then(Build, OriginalHangarScreen.NameOkKey),
            new[] { OriginalShell.DialogOkKey, OriginalHangarScreen.NameCancelKey, OriginalInstantActionScreen.ExitKey },
            Expect: new[] { OriginalShell.DialogOkKey }),
        new("sell-confirm", OriginalScreen.HangarInventory, Then(Hub, OriginalHangarScreen.SellPlanesKey, OriginalHangarScreen.InventorySellKey),
            Then(new[] { OriginalShell.DialogNoKey, OriginalHangarScreen.InventoryDoneKey }, CancelBuild),
            Expect: new[] { OriginalShell.DialogYesKey, OriginalShell.DialogNoKey }),
        // Only an airframe swap over an edited build asks, so the engine is hand-picked on the way.
        new("defaults-ask", OriginalScreen.HangarAirframe,
            Then(Hub, "PX_B_ENGINE", OriginalHangarScreen.EngineDropKey, OriginalHangarScreen.EngineDropKey + ":2",
                "PX_B_AIRFRAME", OriginalHangarScreen.AirframeDropKey, OriginalHangarScreen.AirframeDropKey + ":1"),
            Then(new[] { OriginalShell.DialogCancelKey }, CancelBuild),
            Expect: new[] { OriginalShell.DialogYesKey, OriginalShell.DialogNoKey, OriginalShell.DialogCancelKey }),
        new("quit", null, new[] { "MM_B_QUIT" }, Array.Empty<string>(), Exit: typeof(QuitExit)),
        new("apply-options", null,
            new[]
            {
                "MM_B_PREFERENCES", OriginalOptionsScreen.GameOptionsDoorKey,
                OriginalOptionsScreen.DifficultyKey, OriginalOptionsScreen.DifficultyKey + ":1", OriginalOptionsScreen.GameOptionsAcceptKey,
            },
            Array.Empty<string>(), Exit: typeof(OptionsApplyExit)),
        new("apply-audio", null,
            new[] { "MM_B_PREFERENCES", OriginalOptionsScreen.AudioDoorKey, OriginalOptionsScreen.AudioAcceptKey },
            Array.Empty<string>(), Exit: typeof(OptionsApplyExit)),
        new("apply-video", null,
            new[] { "MM_B_PREFERENCES", OriginalOptionsScreen.VideoDoorKey, OriginalOptionsScreen.GraphicsKey, OriginalOptionsScreen.VideoAcceptKey },
            Array.Empty<string>(), Exit: typeof(OptionsApplyExit)),
        new("free-flight-launch", null, new[] { OriginalShell.FreeFlightKey, "C1", OriginalShell.AirframeKey(0), OriginalShell.FlyKey }, Array.Empty<string>(), Exit: typeof(LaunchExit)),
        new("instant-action-launch", null, new[] { "MM_B_INSTANTACTION", OriginalInstantActionScreen.FlyMissionKey }, Array.Empty<string>(), Exit: typeof(LaunchExit)),
        new("campaign-launch", null, Then(Cabin, "NextMission", "GoToFlightCheck", "FlyMission"), Array.Empty<string>(), Exit: typeof(CampaignMissionExit)),
        new("purchase", OriginalScreen.InstantAction, Then(Hub, OriginalHangarScreen.ReadyKey, OriginalHangarScreen.PurchaseNowKey), new[] { OriginalInstantActionScreen.ExitKey }),
    };

    // Every ScriptToExe edge of an in-scope section, by "Section.Widget": the journey whose route
    // or way back presses the row that realises it (with the row's key where it differs from the
    // widget's), the journey on which it is drawn disabled, or the reason it is out of scope.
    private static readonly Dictionary<string, Edge> Edges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MainMenu.MM_B_CAMPAIGN"] = Edge.Driven("campaign-roster", OriginalShell.CampaignKey),
        ["MainMenu.MM_B_INSTANTACTION"] = Edge.Driven("instant-action", "MM_B_INSTANTACTION"),
        ["MainMenu.MM_B_MULTIPLAYER"] = Edge.Driven("multiplayer-connection", OriginalShell.MultiplayerKey),
        ["MainMenu.MM_B_PREFERENCES"] = Edge.Driven("options", "MM_B_PREFERENCES"),
        ["MainMenu.MM_B_CREDITS"] = Edge.Driven("credits", OriginalShell.CreditsDoorKey),
        ["Credits.CR_B_Exit"] = Edge.Driven("credits", OriginalShell.CreditsExitKey),
        ["Preferences.PF_B_GAMEOPTIONS"] = Edge.Driven("game-options", OriginalOptionsScreen.GameOptionsDoorKey),
        ["GameOptions.GO_B_ACCEPTCHANGES"] = Edge.Driven("apply-options", OriginalOptionsScreen.GameOptionsAcceptKey),
        ["GameOptions.GO_B_CANCELCHANGES"] = Edge.Driven("game-options", OriginalOptionsScreen.GameOptionsCancelKey),
        ["Preferences.PF_B_AUDIO"] = Edge.Driven("audio", OriginalOptionsScreen.AudioDoorKey),
        ["Audio.AP_B_ACCEPTCHANGES"] = Edge.Driven("apply-audio", OriginalOptionsScreen.AudioAcceptKey),
        ["Audio.AP_B_CANCELCHANGES"] = Edge.Driven("audio", OriginalOptionsScreen.AudioCancelKey),
        ["Preferences.PF_B_VIDEO"] = Edge.Driven("video", OriginalOptionsScreen.VideoDoorKey),
        ["Video.VP_B_ACCEPTCHANGES"] = Edge.Driven("apply-video", OriginalOptionsScreen.VideoAcceptKey),
        ["Video.VP_B_CANCELCHANGES"] = Edge.Driven("video", OriginalOptionsScreen.VideoCancelKey),
        ["Preferences.PF_B_CONTROLS"] = Edge.Driven("controls", OriginalOptionsScreen.ControlsDoorKey),
        ["ControlsPrefs.CP_B_ACCEPTCHANGES"] = Edge.Driven("controls-accept", OriginalOptionsScreen.ControlsAcceptKey),
        ["ControlsPrefs.CP_B_CANCELCHANGES"] = Edge.Driven("controls", OriginalOptionsScreen.ControlsCancelKey),
        ["ControlsPrefs.CP_B_KEYS"] = Edge.Driven("keys", OriginalOptionsScreen.KeysDoorKey),
        ["Keys.KB_B_ACCEPTCHANGES"] = Edge.Driven("keys-accept", OriginalOptionsScreen.KeysAcceptKey),
        ["Keys.KB_B_CANCELCHANGES"] = Edge.Driven("keys", OriginalOptionsScreen.KeysCancelKey),
        ["PassengerCabin.PC_B_CHANGEMOMENTO"] = Edge.Driven("campaign-memento", "ChangeMemento"),
        ["MomentoSelection.MS_B_ACCEPT"] = Edge.Driven("campaign-memento-accept", "AcceptMemento"),
        ["MomentoSelection.MS_B_CANCEL"] = Edge.Driven("campaign-memento", "CancelMemento"),
        ["PassengerCabin.PC_B_PREVIOUS"] = Edge.Driven("campaign-previous", "PreviousMissions"),
        ["PassengerCabin.PC_B_PLANEX"] = Edge.Driven("campaign-hangar-door", "PlaneConstruction"),
        ["PassengerCabin.PC_B_RETURNMM"] = Edge.Driven("campaign-cabin", "ReturnToMainMenu"),
        ["FlightCheck.FC_B_CHANGEPLANE"] = Edge.Driven("campaign-planeselection", "ChangePlane"),
        ["FlightCheck.FC_B_CHANGEPLANEW"] = Edge.Slot("campaign-planeselection", "ChangePlane:1"),
        ["FlightCheck.FC_B_CHANGEAMMO"] = Edge.Driven("campaign-ammo", "ChangeAmmo"),
        ["FlightCheck.FC_B_CHANGEAMMOW"] = Edge.Slot("campaign-ammo", "ChangeAmmo:1"),
        ["PlaneSelection.PS_B_ACCEPT"] = Edge.Driven("campaign-planeselection-accept", "AcceptSelections"),
        ["PlaneSelection.PS_B_CANCEL"] = Edge.Driven("campaign-planeselection", "CancelSelections"),
        ["OrdinanceLayout.OL_B_ACCEPT"] = Edge.Driven("campaign-ammo-accept", "AcceptLoadout"),
        ["OrdinanceLayout.OL_B_CANCEL"] = Edge.Driven("campaign-ammo", "CancelLoadout"),
        ["PlaneConstruction.PX_B_AIRFRAME"] = Edge.Driven("plane-paint-then-airframe", "PX_B_AIRFRAME"),
        ["PlaneConstruction.PX_B_ENGINE"] = Edge.Driven("plane-engine", "PX_B_ENGINE"),
        ["PlaneConstruction.PX_B_ARMOR"] = Edge.Driven("plane-armor", "PX_B_ARMOR"),
        ["PlaneConstruction.PX_B_GUNS"] = Edge.Driven("plane-guns", "PX_B_GUNS"),
        ["PlaneConstruction.PX_B_HARDPOINTS"] = Edge.Driven("plane-hardpoints", "PX_B_HARDPOINTS"),
        ["PlaneConstruction.PX_B_PAINT"] = Edge.Driven("plane-paint", "PX_B_PAINT"),
        ["PlaneConstruction.PX_B_Ready"] = Edge.Driven("plane-purchase", OriginalHangarScreen.ReadyKey),
        ["ScrapBook_TOC.SBTOC_B_RETURN"] = Edge.Driven("campaign-previous", "ReturnToCabin"),
        ["ScrapBook.SB_B_RETURNPC"] = Edge.Driven("campaign-scrapbook", "ReturnToCabin"),
        ["InstantAction.IA_B_Exit"] = Edge.Driven("instant-action", OriginalInstantActionScreen.ExitKey),
        ["IA_WrapUp.IAWU_B_CONTINUE"] = Edge.Driven("instant-action-wrapup", OriginalWrapupScreen.ContinueKey),
    };

    private readonly ITestOutputHelper _output;
    private readonly string _dir;
    private readonly HashSet<string> _drawn = new(StringComparer.OrdinalIgnoreCase);
    private int _runs;

    // The setup of the shell a journey is walking, for the join directive.
    private PlayerSetupFeature? _setup;

    public OriginalCoverageTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "csvm-original-coverage-" + Guid.NewGuid().ToString("N"));
    }

    private enum Family
    {
        Pointer,
        Keyboard,
        Pad,
    }

    private enum EdgeKind
    {
        Driven,
        Slot,
        Disabled,
        OutOfScope,
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void EveryScreenAndEdgeOfTheFixtureGraphIsReachableAndEscapableByEveryInputFamily()
    {
        Cover(MenuLayoutReaderTests.OriginalLayout(), FixtureMeasure, null, "fixture");
    }

    [ExtractedDataFact]
    public void EveryScreenAndEdgeOfTheInstallsLayoutIsReachableAndEscapableByEveryInputFamily()
    {
        string dataRoot = TestData.DataRoot!;
        var layout = MenuLayout.TryLoad(MenuLayout.PathUnder(dataRoot), out var reason);
        Assert.True(layout != null, reason ?? "the install's decoded layout reads");
        Cover(layout!, art => PngSize(OriginalAvailability.ArtPath(dataRoot, art)), dataRoot, "install");
    }

    // A PNG's pixel size off its header, the one art format every button strip ships in; any
    // other file measures as unknown and the row keeps its fallback rectangle. Shared with
    // OriginalOptionsTests, whose option-page facts walk the same install art.
    internal static (int Width, int Height)? PngSize(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        var header = new byte[24];
        if (stream.Read(header, 0, header.Length) < header.Length || header[1] != (byte)'P' || header[2] != (byte)'N' || header[3] != (byte)'G')
        {
            return null;
        }

        int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static string[] Then(string[] route, params string[] more)
    {
        var steps = new List<string>(route);
        steps.AddRange(more);
        return steps.ToArray();
    }

    // The fixture's strips: every button strip 240x200 (four 50-pixel frames), the paper plaque
    // 160x112 (four 28-pixel frames), the hangar's own sizes as its tests measure them.
    private static (int Width, int Height)? FixtureMeasure(string art) => art switch
    {
        "PH_NamePanel.png" => (260, 180),
        "PM_B_Paper.png" or "PH_B_Paper.png" => (160, 112),
        "PH_Tab.png" => (120, 120),
        "PH_B_OkCancel.png" => (80, 96),
        "PH_B_Check8.png" => (16, 128),
        "PH_B_DropUp.png" or "PH_B_DropDown.png" or "PP_B_DropUp.png" or "PP_B_DropDown.png" => (15, 56),
        "PP_B_Check8.png" => (16, 128),
        // The slider's two files at their shipped sizes, neither a strip: a slot three pixels tall
        // and a thumb that stands well clear of it. The PF_ names are what a row with no slider
        // widget of its own falls back to.
        "PP_B_SliderSlot.png" or "PF_B_SliderSlot.png" => (171, 3),
        "PP_B_Slider.png" or "PF_B_Slider.png" => (43, 21),
        "PM_AboutBox.png" => (400, 300),
        "PH_Decals.tga" => (66, 3300),
        "PH_PlaneIcons.png" => (100, 1200),
        _ when art.StartsWith("PH_B_", StringComparison.Ordinal) => (200, 128),
        _ when art.StartsWith("PX_ICON_", StringComparison.Ordinal) => (358, 335),
        _ when art.StartsWith("PM_B_", StringComparison.Ordinal) || art.StartsWith("PP_B_", StringComparison.Ordinal) => (240, 200),
        _ => null,
    };

    // A click lands on the row's centre, since two authored plaques may share an edge pixel. The
    // press arms the row and the release on it fires, so a click is two of these frames.
    private static MenuCommands Pointer(OriginalRow row, bool pressed = false) =>
        new() { Pointer = new MenuPointer(row.X + (row.Width / 2f), row.Y + (row.Height / 2f), pressed, pressed) };

    private static MenuCommands Move(Family family, bool vertical) => family == Family.Keyboard
        ? (vertical ? new MenuCommands { MoveY = 1 } : new MenuCommands { MoveX = 1 })
        : (vertical ? new MenuCommands { MoveY = -1 } : new MenuCommands { MoveX = -1 });

    // Which seat's device the screen showing answers to: the picking seat on the per-seat aircraft
    // screen, seat 0 everywhere else. The pointer stays seat 0's, the mouse being one desk device.
    private static int Driver(OriginalShell shell) => Math.Max(0, shell.PickingSeat);

    private static OriginalRow? Row(OriginalShell shell, string key)
    {
        foreach (var row in shell.Rows)
        {
            if (row.Key == key)
            {
                return row;
            }
        }

        return null;
    }

    // One seat's three shipped keymaps, each on the identity its own polling site reads.
    private static BindingProfile ControlsProfile()
    {
        var maps = new Dictionary<InputContext, ActionMap>();
        foreach (var context in Enum.GetValues<InputContext>())
        {
            maps[context] = DefaultBindings.MapFor(context, MenuControlsSeats.PadOf(context));
        }

        return new BindingProfile(maps, readsKeyboard: true);
    }

    private static void AssertHome(OriginalShell shell, CampaignFeature campaign, HangarFeature hangar, string way)
    {
        Assert.True(shell.Screen == OriginalScreen.TopLevel, $"{way} stops on {shell.Screen}");
        Assert.True(shell.Dialog == null && !shell.Campaign.IsOpen && !campaign.IsOpen && !hangar.IsOpen,
            $"{way} leaves residue: dialog={shell.Dialog != null} campaign={campaign.IsOpen} hangar={hangar.IsOpen}");
    }

    private void Cover(MenuLayout layout, Func<string, (int Width, int Height)?> measure, string? dataRoot, string label)
    {
        var failures = new List<string>();
        int screensReached = 0;
        int journeyRuns = 0;
        var screens = new HashSet<OriginalScreen>();
        foreach (var journey in Journeys)
        {
            if (journey.InstallOnly && dataRoot == null)
            {
                continue;
            }

            foreach (Family family in Enum.GetValues<Family>())
            {
                journeyRuns++;
                try
                {
                    RunJourney(layout, measure, dataRoot, journey, family, screens);
                }
                catch (Exception e) when (e is Xunit.Sdk.XunitException or InvalidOperationException or ArgumentException)
                {
                    failures.Add($"{journey.Name} by {family}: {e.Message}");
                }
            }
        }

        screensReached = screens.Count;
        var (driven, disabled, slots, outOfScope) = CheckEdges(layout, measure, dataRoot, failures);
        int drawn = CheckManifest(layout, failures);
        int expectedScreens = Enum.GetValues<OriginalScreen>().Length - (dataRoot == null ? 1 : 0);
        if (screensReached != expectedScreens)
        {
            failures.Add($"screens reached {screensReached} of {expectedScreens}: missing {string.Join(", ", Enum.GetValues<OriginalScreen>().Where(s => !screens.Contains(s)))}");
        }

        _output.WriteLine(
            $"{label}: {screensReached} screens reached and left, {Journeys.Length} journeys x 3 input families = {journeyRuns} runs, "
            + $"{driven} layout edges driven, {slots} realised as the wingman slot's row, {disabled} drawn disabled, {outOfScope} out of scope, 0 dead ends, "
            + $"{drawn} art names drawn, none of them the manifest's optional");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // One journey by one input family: the route from the top level, the target, the pointer's
    // way back (pressed by that family), then the same route again and the cursor families' Back
    // chain, each way landing on the top level with nothing left open.
    private void RunJourney(MenuLayout layout, Func<string, (int Width, int Height)?> measure, string? dataRoot,
        Journey journey, Family family, HashSet<OriginalScreen> screens)
    {
        var shell = Fresh(layout, measure, dataRoot, out var campaign, out var hangar);
        var exit = Walk(shell, journey, family);
        if (journey.Exit != null)
        {
            Assert.True(exit != null && journey.Exit.IsInstanceOfType(exit), $"expected {journey.Exit.Name}, got {exit?.GetType().Name ?? "no exit"}");
            return;
        }

        Assert.Null(exit);
        Assert.Equal(journey.Target, shell.Screen);
        screens.Add(shell.Screen);
        Collect(shell.Compose());
        foreach (string key in journey.Expect ?? Array.Empty<string>())
        {
            Assert.True(Row(shell, key) is { Enabled: true }, $"{key} stands on {shell.Screen}");
        }

        if (shell.Screen != OriginalScreen.TopLevel)
        {
            Assert.True(shell.Rows.Any(r => r.Enabled), $"{shell.Screen} offers no live row");
        }

        foreach (string key in journey.Leave)
        {
            Assert.Null(Press(shell, family, key));
        }

        AssertHome(shell, campaign, hangar, "the way back");
        if (family == Family.Pointer || journey.Target == OriginalScreen.TopLevel)
        {
            return;
        }

        shell = Fresh(layout, measure, dataRoot, out campaign, out hangar);
        Assert.Null(Walk(shell, journey, family));
        for (int i = 0; i < BackGuard && shell.Screen != OriginalScreen.TopLevel; i++)
        {
            Assert.Null(shell.StepSeat(Driver(shell), Back).Exit);
        }

        AssertHome(shell, campaign, hangar, "the Back chain");
    }

    private MenuExit? Walk(OriginalShell shell, Journey journey, Family family)
    {
        MenuExit? exit = null;
        foreach (string step in journey.Route)
        {
            Assert.Null(exit);
            if (step.StartsWith(TypeStep, StringComparison.Ordinal))
            {
                shell.Step(new MenuCommands { Typed = step[TypeStep.Length..] });
            }
            else if (step == EraseStep)
            {
                for (int i = 0; i < CampaignFeature.MaxNameLength && shell.Campaign.RosterName.Length > 0; i++)
                {
                    shell.Step(Erase);
                }
            }
            else if (step == ScrapStep)
            {
                exit = PressAScrap(shell, family);
            }
            else if (step == JoinStep)
            {
                _setup!.Join(new ScriptedMenuSeat());
            }
            else if (step == WrapupStep)
            {
                shell.Wrapup.ShowWrapup(InstantActionWrapupPage.Sample(won: true));
            }
            else
            {
                exit = Press(shell, family, step);
            }
        }

        return exit;
    }

    // The first scrap on the book's spread that opens the zoom; a scrap that does not (a result
    // card) is pressed and passed over.
    private MenuExit? PressAScrap(OriginalShell shell, Family family)
    {
        var keys = shell.Rows.Where(r => r.Key.StartsWith("ROW:", StringComparison.Ordinal) && r.Enabled && (family != Family.Pointer || r.Visible))
            .Select(r => r.Key).ToList();
        Assert.True(keys.Count > 0, "the spread carries a scrap row");
        foreach (string key in keys)
        {
            var exit = Press(shell, family, key);
            if (exit != null || shell.Screen == OriginalScreen.CampaignScrapbookZoom)
            {
                return exit;
            }
        }

        throw new Xunit.Sdk.XunitException("no scrap on the spread opens the zoom");
    }

    // One press by one family: the pointer clicks inside the row's rectangle; the cursor
    // families walk the focus onto the row (keyboard down and right, pad up and left, so both
    // wraps are taken) and Accept. A dropdown or radio under the cursor steps its value sideways,
    // so a column is only crossed from a row that does not.
    private MenuExit? Press(OriginalShell shell, Family family, string key)
    {
        var target = Row(shell, key);
        Assert.True(target != null, $"{key} is a row of {shell.Screen} ({string.Join(", ", shell.Rows.Select(r => r.Key))})");
        Assert.True(target!.Enabled, $"{key} is live on {shell.Screen}");
        if (family == Family.Pointer)
        {
            Assert.True(target.Visible && target.Width > 0f && target.Height > 0f, $"{key} has a rectangle to click on {shell.Screen}");
            var before = shell.Screen;
            shell.Step(Pointer(target, pressed: true));
            var step = shell.Step(Pointer(target));
            Assert.True(step.Changed || shell.Screen != before, $"a click on {key} changed nothing");
            return step.Exit;
        }

        for (int guard = 0; guard < WalkGuard && shell.FocusedKey != key; guard++)
        {
            var rows = shell.Rows;
            int focus = shell.Focus;
            Assert.True(focus >= 0, $"nothing on {shell.Screen} takes focus");
            int index = rows.ToList().FindIndex(r => r.Key == key);
            Assert.True(index >= 0, $"{key} left {shell.Screen}'s rows while walking");
            bool cross = rows[focus].Column != rows[index].Column && rows[focus].Kind is not (OriginalRowKind.Dropdown or OriginalRowKind.Radio);
            shell.StepSeat(Driver(shell), Move(family, !cross));
        }

        Assert.True(shell.FocusedKey == key, $"the {family} walk reached {shell.FocusedKey}, not {key}, on {shell.Screen}");
        return shell.StepSeat(Driver(shell), Accept).Exit;
    }

    // Every ScriptToExe edge of an in-scope section: mapped, and its mapping true of the shell.
    private (int Driven, int Disabled, int Slots, int OutOfScope) CheckEdges(
        MenuLayout layout, Func<string, (int Width, int Height)?> measure, string? dataRoot, List<string> failures)
    {
        int driven = 0;
        int disabled = 0;
        int slots = 0;
        int outOfScope = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var journeys = Journeys.ToDictionary(j => j.Name, StringComparer.Ordinal);
        foreach (var edge in layout.Navigation)
        {
            if (!InScopeSections.Contains(edge.From, StringComparer.OrdinalIgnoreCase))
            {
                outOfScope++;
                continue;
            }

            string id = $"{edge.From}.{edge.Widget}";
            seen.Add(id);
            if (!Edges.TryGetValue(id, out var expectation))
            {
                failures.Add($"edge {id} -> {edge.To} has no coverage entry");
                continue;
            }

            switch (expectation.Kind)
            {
                case EdgeKind.OutOfScope:
                    outOfScope++;
                    break;
                case EdgeKind.Driven:
                case EdgeKind.Slot:
                    if (!journeys.TryGetValue(expectation.Journey!, out var journey))
                    {
                        failures.Add($"edge {id} names no journey {expectation.Journey}");
                        break;
                    }

                    string key = expectation.Key!;
                    string pressed = expectation.Kind == EdgeKind.Slot ? key[..key.IndexOf(':')] : key;
                    if (!journey.Route.Contains(pressed) && !journey.Leave.Contains(pressed))
                    {
                        failures.Add($"edge {id}: journey {journey.Name} never presses {pressed}");
                        break;
                    }

                    if (expectation.Kind == EdgeKind.Slot)
                    {
                        slots += CheckSlot(layout, measure, dataRoot, journey, expectation.Key!, failures);
                    }
                    else
                    {
                        driven++;
                    }

                    break;
                case EdgeKind.Disabled:
                    var shell = Fresh(layout, measure, dataRoot, out _, out _);
                    if (expectation.Journey != null)
                    {
                        Walk(shell, journeys[expectation.Journey], Family.Pointer);
                    }

                    var row = Row(shell, expectation.Key!);
                    if (row is not { Enabled: false })
                    {
                        failures.Add($"edge {id}: {expectation.Key} should draw disabled on {shell.Screen} ({(row == null ? "no row" : "live")})");
                        break;
                    }

                    disabled++;
                    break;
            }
        }

        // Over the install every entry must name a real edge; the fixture authors a subset.
        foreach (string id in Edges.Keys)
        {
            if (dataRoot != null && !seen.Contains(id))
            {
                failures.Add($"coverage entry {id} names no edge the layout states");
            }
        }

        return (driven, disabled, slots, outOfScope);
    }

    // Every art name the journeys drew, against the manifest's classification: what a screen draws
    // cannot be a file Original is allowed to run without. A name the manifest does not carry is a
    // runtime-assembled one (a blueprint, a scrap, a paint mask), which the inventory classes on
    // its own. The other direction, a required name no journey drew, is listed for the reader.
    private int CheckManifest(MenuLayout layout, List<string> failures)
    {
        var manifest = OriginalAssetManifest.Derive(layout);
        foreach (string art in _drawn)
        {
            if (manifest.Find(art) is { Need: OriginalAssetNeed.Optional } entry)
            {
                failures.Add($"{art} is drawn and the manifest classes it optional ({entry.Section}.{entry.Row})");
            }
        }

        var idle = manifest.Assets
            .Where(a => a.Need == OriginalAssetNeed.Required && !_drawn.Contains(a.Name))
            .Select(a => $"{a.Name} ({a.Section}.{a.Row})").ToList();
        _output.WriteLine($"required but drawn by no journey: {(idle.Count == 0 ? "none" : string.Join(", ", idle))}");
        return _drawn.Count;
    }

    // The screen chrome alone. A movie is deliberately drawn and optional, which the check above
    // would otherwise read as a contradiction: the screen behind a background film draws whole
    // without it, so its absence is a degrade rather than a reason to refuse Original.
    private void Collect(ComposedBoard board)
    {
        foreach (var picture in board.Backdrop.Concat(board.Pictures).Concat(board.Overlays.SelectMany(o => o.Pictures)))
        {
            if (picture.Art.Library == BoardArtLibrary.Ui)
            {
                _drawn.Add(picture.Art.Name);
            }
        }

        foreach (var plaque in board.Plaques)
        {
            if (plaque.Art.Library == BoardArtLibrary.Ui)
            {
                _drawn.Add(plaque.Art.Name);
            }
        }
    }

    // A wingman's slot row on the flight check stands when the mission flies a wingman and is
    // absent when it does not; either way the edge is realised by the same row family.
    private int CheckSlot(MenuLayout layout, Func<string, (int Width, int Height)?> measure, string? dataRoot, Journey journey, string key, List<string> failures)
    {
        var shell = Fresh(layout, measure, dataRoot, out var campaign, out _);
        var route = journey.Route.TakeWhile(step => step != key[..key.IndexOf(':')]).ToArray();
        Walk(shell, journey with { Route = route }, Family.Pointer);
        bool present = Row(shell, key) != null;
        if (present != campaign.MissionHasWingman)
        {
            failures.Add($"{key} present={present} while the mission's wingman flag is {campaign.MissionHasWingman}");
        }

        return 1;
    }

    // A fresh shell over fresh scratch stores: a progressed player with three planes and a second
    // player, a saved custom plane, a scripted seat, no stock table.
    private OriginalShell Fresh(MenuLayout layout, Func<string, (int Width, int Height)?> measure, string? dataRoot,
        out CampaignFeature campaign, out HangarFeature hangar)
    {
        string dir = Path.Combine(_dir, (_runs++).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var profiles = new CampaignProfileStore(Path.Combine(dir, "Profiles"));
        var planes = new CustomPlaneStore(Path.Combine(dir, "Planes"));
        var pilot = CampaignProfileDef.NewProfile(Pilot);
        for (int seq = 0; seq < 3; seq++)
        {
            CampaignProgression.Record(pilot, new MissionAttempt(seq, 0x1fff, 300_000 + (seq * 20_000), 400, 120, pilot.Planes[0].Airframe, pilot.Planes[0].Name));
        }

        pilot.Planes.Add(new OwnedPlane { Name = "Second Bird", Airframe = 3 });
        pilot.Planes.Add(new OwnedPlane { Name = "Third Bird", Airframe = 7 });
        profiles.Save(pilot);
        profiles.Save(CampaignProfileDef.NewProfile(SecondPilot));
        profiles.RecordLastPlayed(Pilot);
        planes.Save(new CustomPlaneDef { Name = SparePlane, Airframe = 2, Engine = 1 });

        var strings = (dataRoot != null ? UiStrings.TryLoad(dataRoot) : null) ?? UiStrings.Empty;
        var setup = new PlayerSetupFeature();
        setup.SetRoster(OriginalPresentation.Roster(planes.List()));
        setup.Join(new ScriptedMenuSeat());
        _setup = setup;
        hangar = new HangarFeature(strings, PlanePickerRoster.AirframeNode);
        campaign = new CampaignFeature(strings, PlanePickerRoster.AirframeNode);
        var store = profiles;
        // The rebinding feature with one seat on the shipped keymaps and no save, so the CONTROLS
        // door is live and the walk edits a copy nothing writes back.
        var controls = new ControlsFeature();
        controls.AddSeat(1, ControlsProfile(), new SeatCaptureDevices(MenuControlsSeats.PadOf, Array.Empty<int>), true);
        // A network door that hosts on a lone in-process wire and joins nothing. Nobody answers on
        // its in-process LAN, so the games list stands on its Searching box.
        var lan = new CSVM.Net.LoopbackLan();
        var net = new NetPlayFeature(
            (_, _, _) => CSVM.Net.LoopbackTransport.Mesh(1, CSVM.Net.LoopbackConditions.Perfect, new Random(1))[0],
            (_, _) => throw new InvalidOperationException("the walk joins nothing"),
            lan: lan.Bind);
        return new OriginalShell(layout, new FreeFlightFeature(), setup, measure,
            hangar: hangar, planes: planes, campaign: campaign, profiles: () => store, dataRoot: dataRoot,
            controls: controls, net: net);
    }

    private sealed record Journey(
        string Name, OriginalScreen? Target, string[] Route, string[] Leave,
        Type? Exit = null, string[]? Expect = null, bool InstallOnly = false);

    private sealed record Edge(EdgeKind Kind, string? Journey, string? Key, string Reason)
    {
        public static Edge Driven(string journey, string key) => new(EdgeKind.Driven, journey, key, string.Empty);

        public static Edge Slot(string journey, string key) => new(EdgeKind.Slot, journey, key, string.Empty);

        public static Edge Disabled(string key, string reason, string? journey = null) => new(EdgeKind.Disabled, journey, key, reason);

        public static Edge OutOfScope(string reason) => new(EdgeKind.OutOfScope, null, null, reason);
    }
}
