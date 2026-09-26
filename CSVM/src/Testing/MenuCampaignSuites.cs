using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;
using CSVM.UI.Hangar;
using CSVM.UI.Menu;
using CSVM.UI.Screens;

namespace CSVM.Testing;

/// <summary>
/// Built-in's campaign journey, characterized: a real <see cref="LaunchMenu"/> is driven through
/// the Mode screen's Campaign door over a scratch profile store and its screens are read back
/// through the <c>Shown*</c> read-outs and the composed board. The roster's three states, a player
/// created and one deleted, the cabin, previous missions, the briefing and its reveal, the flight
/// check, ammo and plane selection with their writes into the store, the cabin's PLANE CONSTRUCTION
/// into the hangar and back, FLY MISSION as a <see cref="CampaignMissionExit"/>, the debrief return
/// landing on the scrapbook with its page turn back to the cabin, a replay offering what the profile
/// has earned against the narrower offer three flights in, the guest check under a debug
/// join, and every scratch-profile aid. Every check pins what the screens do today. ⚠ Nothing here
/// touches <c>user://Profiles</c>: the door is pointed at a scratch store and the aids read their own.
/// </summary>
internal static class MenuCampaignSuites
{
    private const string Pilot = "Zachary";
    private const string Guest = "Nathan";
    private const string Filmed = "Maria";
    private const string Finished = "Paladin";
    private const string Veteran = "Ramirez";
    private const int FlownBefore = 3;

    // The rocket table's own thresholds (docs/formats/campaign-screens.md, "Ammo selection"):
    // aerial torpedoes are the last row to unlock, three rows stand from the first mission and a
    // fourth stands once three are flown.
    private const int TorpedoOrdinal = 20;
    private const int OpeningPylonRows = 3;
    private const int PylonRowsAtFour = 4;

    // The ammo screen's first pylon row and the row past its last: the four gun groups stand before
    // the eight pylon cells.
    private const int FirstPylon = 4;
    private const int PastPylons = 12;

    private static readonly MenuCommands Accept = new() { Accept = true };
    private static readonly MenuCommands Back = new() { Back = true };
    private static readonly MenuCommands Down = new() { MoveY = 1 };
    private static readonly MenuCommands Up = new() { MoveY = -1 };
    private static readonly MenuCommands Left = new() { MoveX = -1 };
    private static readonly MenuCommands Right = new() { MoveX = 1 };
    private static readonly MenuCommands Secondary = new() { Contents = true };

    [Suite("menu-campaign-journey",
        "Built-in's campaign journey pinned end to end over a scratch profile store: the Mode "
        + "screen's Campaign door opens the empty roster, a typed name creates a player and lands on "
        + "the cabin, the roster then stands on that player, a second player is created and deleted "
        + "through the confirm stage, previous missions lists a progressed profile's three flights and "
        + "opens the scrapbook, the briefing runs its reveal and restarts its narration on REPLAY, the "
        + "flight check opens ammo selection whose two description panes fill at once, whose ACCEPT "
        + "writes the pick into the profile and whose "
        + "CANCEL writes nothing, plane selection writes the pilot's pick on ACCEPT and keeps it on "
        + "CANCEL, PLANE CONSTRUCTION opens the hangar over the profile's wallet and Back resumes the "
        + "cabin, FLY MISSION leaves as one CampaignMissionExit with the profile saved, the debrief "
        + "return opens the scrapbook on the flown mission and turns back to the cabin, a replay of "
        + "the first mission offers every ordnance and aircraft the profile has earned where the "
        + "same screen three flights in offers only the rows those flights unlocked, the guest "
        + "check walks a debug-joined field, a guest's own ammunition pick writes nothing into the "
        + "seated profile and still stands on the next mission's check after the launch and the "
        + "return, every scratch-profile aid opens its screen, and the two "
        + "campaign films own the frames they play and the press that ended them")]
    internal static void MenuCampaignJourney(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        var exits = new List<MenuExit>();
        var host = MenuSuiteHost.Bare(exits, ctx.DataRoot, out var seat);
        var menu = LaunchMenu.Build(ctx.ZrdrPath, ctx.DataRoot, host, seat.Input);
        ctx.Host.AddChild(menu);
        menu.SetProcess(false);
        string root = Path.Combine(ctx.ScratchDir, "menu-campaign-journey");
        string dir = Path.Combine(root, "Profiles");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        var store = new CampaignProfileStore(dir);
        menu.CampaignProfiles = store;
        try
        {
            EmptyRoster(ctx, menu);
            CreatePlayer(ctx, menu, store);
            SecondPlayerAndDelete(ctx, menu, store);
            FreshCabin(ctx, menu, store);
            Progress(store);
            PreviousMissions(ctx, menu, store);
            Briefing(ctx, menu);
            FlightCheckAndAmmo(ctx, menu, store);
            PlaneSelection(ctx, menu, store);
            HangarDoor(ctx, menu, store);
            FlyMission(ctx, menu, store, exits);
            DebriefReturn(ctx, menu, store);
            ReplayOffersEarnedRows(ctx, menu, store);
            GuestCheck(ctx, menu);
            GuestPickAcrossMissions(ctx, menu, store, exits);
            Aids(ctx, menu);
            FilmHandBack(ctx);
        }
        finally
        {
            ctx.Host.RemoveChild(menu);
            menu.QueueFree();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void EmptyRoster(TestContext ctx, LaunchMenu menu)
    {
        menu.ShowMenu();
        WalkTo(menu, LaunchMenu.CampaignRow);
        Is(ctx, "the Mode screen's fourth row is the campaign door", LaunchMenu.CampaignRow, menu.ShownRowText);
        menu.Drive(Accept);
        Is(ctx, "Accept on the door opens the Campaign screen", "Campaign", menu.ShownScreen);
        Is(ctx, "on the roster", "SELECT PLAYER", menu.ShownHeading);
        Is(ctx, "its breadcrumb", LaunchMenu.CampaignRow + "  ›  SELECT PLAYER", menu.ShownBreadcrumb);
        ctx.Check(menu.Campaign is { Screen: CampaignScreen.Roster, Profile: null } && menu.Campaign.Roster.Count == 0,
            $"the flow is open on the roster over an empty store with nobody seated");
        ctx.Check(menu.ShownRowCount == 4 && menu.ShownRow == 0, $"an empty roster is the name field and three buttons, cursor on the field ({menu.ShownRowCount}, {menu.ShownRow})");
        Is(ctx, "the field reads nobody", "Name:  (none)", menu.ShownRowText);
        Is(ctx, "described as the way to type", "Enter / A to type a player name", menu.ShownDetail);
        ctx.Check(menu.ShownBoard is { } board && board.Plaques.Count == 3, $"the board carries the three plaques ({menu.ShownBoard?.Plaques.Count})");
        WalkTo(menu, "CONTINUE");
        Is(ctx, "the CONTINUE row", "Creates the named player, or continues the one that exists", menu.ShownDetail);
        menu.Drive(Accept);
        Is(ctx, "CONTINUE with no name is refused in the original's words",
            menu.Campaign!.Strings.Text(200, "You must enter a player name."), menu.ShownDetail);
        ctx.Check(menu.Campaign.Screen == CampaignScreen.Roster, $"and the roster stands ({menu.Campaign.Screen})");
        WalkTo(menu, "CANCEL");
        Is(ctx, "the CANCEL row", "Back to the main menu", menu.ShownDetail);
        menu.Drive(Accept);
        ctx.Check(menu.ShownScreen == "Mode" && menu.Campaign == null, $"CANCEL closes the flow and lands on the Mode screen ({menu.ShownScreen})");
    }

    private static void CreatePlayer(TestContext ctx, LaunchMenu menu, CampaignProfileStore store)
    {
        WalkTo(menu, LaunchMenu.CampaignRow);
        menu.Drive(Accept);
        var flow = menu.Campaign!;
        menu.Drive(Accept);
        ctx.Check(flow.CapturesText, $"Accept on the field arms it for typing");
        Has(ctx, "the footer names typing", "Type a name", menu.ShownFooter);
        Is(ctx, "the armed field shows a caret", "Name:  _", menu.ShownRowText);
        flow.Type(Pilot);
        menu.Drive(MenuCommands.None);
        Is(ctx, "typed letters land in the field with the caret", "Name:  " + Pilot + "_", menu.ShownRowText);
        // A driven frame carries no pad axis, and the armed field takes its cursor axes from the
        // pad alone, so a Down here moves nothing: the field keeps the keyboard's letters.
        menu.Drive(Down);
        ctx.Check(menu.ShownRow == 0 && flow.CapturesText, $"a keyboard axis does not leave the armed field ({menu.ShownRow})");
        ctx.Check(store.Load(Pilot) == null, $"nothing is written before the commit");
        menu.Drive(Accept);
        Is(ctx, "Accept in the armed field continues onto the cabin", "CAMPAIGN CABIN", menu.ShownHeading);
        ctx.Check(flow.Screen == CampaignScreen.Cabin && flow.Profile?.Name == Pilot, $"the flow seats the new player ({flow.Profile?.Name})");
        string expected = CampaignProfileStore.Serialize(CampaignProfileDef.NewProfile(Pilot));
        ctx.Check(File.ReadAllText(Path.Combine(store.DirFor(Pilot), "profile.json")) == expected,
            $"the store holds exactly a fresh profile under the name");
        Is(ctx, "and remembers the player as last seated", Pilot, store.LastPlayed);
        ctx.Check(menu.ShownRowCount == 5 && menu.ShownRow == 0, $"the cabin's five rows, cursor on Next Mission ({menu.ShownRowCount}, {menu.ShownRow})");
        Is(ctx, "the first row", "Next Mission", menu.ShownRowText);
        Is(ctx, "described", "Opens the briefing for the next mission", menu.ShownDetail);
        menu.Drive(Down);
        Is(ctx, "a fresh profile has no previous missions", "No missions finished yet", menu.ShownDetail);
        menu.Drive(Back);
        ctx.Check(flow.Screen == CampaignScreen.Roster && menu.ShownRowCount == 5, $"Back from the cabin returns to the roster with one row ({flow.Screen}, {menu.ShownRowCount})");
        ctx.Check(menu.ShownRow == 1 && menu.ShownRowText == "✓ " + Pilot, $"the cursor opens on the remembered player's ticked row ({menu.ShownRow}, {menu.ShownRowText})");
        Is(ctx, "described as one press away", "Enter / A again to fly this player's campaign", menu.ShownDetail);
    }

    private static void SecondPlayerAndDelete(TestContext ctx, LaunchMenu menu, CampaignProfileStore store)
    {
        var flow = menu.Campaign!;
        menu.Drive(Up);
        Is(ctx, "the field still carries the name that continued", "Name:  " + Pilot, menu.ShownRowText);
        menu.Drive(Accept);
        Is(ctx, "arming it keeps that name under the caret", "Name:  " + Pilot + "_", menu.ShownRowText);
        while (flow.Backspace())
        {
        }

        flow.Type(Guest);
        menu.Drive(Back);
        ctx.Check(!flow.CapturesText && menu.ShownRowText == "Name:  " + Guest, $"Back disarms the field and keeps the name ({menu.ShownRowText})");
        WalkTo(menu, "CONTINUE");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Cabin && flow.Profile?.Name == Guest && store.Load(Guest) != null,
            $"CONTINUE creates the second player and seats them ({flow.Profile?.Name})");
        string pilotFile = File.ReadAllText(Path.Combine(store.DirFor(Pilot), "profile.json"));
        menu.Drive(Back);
        ctx.Check(menu.ShownRowCount == 6 && menu.ShownRowText == "✓ " + Guest, $"the roster lists both, standing on the newer ({menu.ShownRowCount}, {menu.ShownRowText})");
        WalkTo(menu, "DELETE PLAYER");
        Is(ctx, "the DELETE row", "Removes the named player and its campaign; hangar planes stay", menu.ShownDetail);
        menu.Drive(Accept);
        ctx.Check(menu.ShownRowCount == 2 && menu.ShownRow == 1, $"DELETE PLAYER opens the confirm stage with the cursor on Keep ({menu.ShownRowCount}, {menu.ShownRow})");
        Is(ctx, "the keep answer names the player", "Keep " + Guest, menu.ShownRowText);
        Is(ctx, "over the original's question", flow.Strings.Text(201, "Are you sure you want to delete this player and all associated saved games?"), menu.ShownDetail);
        Has(ctx, "the footer names the answer press", "Answer", menu.ShownFooter);
        menu.Drive(Back);
        ctx.Check(menu.ShownRowCount == 6 && menu.ShownRowText == "DELETE PLAYER" && store.Load(Guest) != null,
            $"Back keeps the player and lands on the delete row ({menu.ShownRowText})");
        menu.Drive(Accept);
        menu.Drive(Up);
        Is(ctx, "the delete answer", "Delete " + Guest, menu.ShownRowText);
        menu.Drive(Accept);
        ctx.Check(store.Load(Guest) == null && !Directory.Exists(store.DirFor(Guest)), $"Accept removes the profile's directory");
        ctx.Check(File.ReadAllText(Path.Combine(store.DirFor(Pilot), "profile.json")) == pilotFile, $"and leaves the other profile byte for byte");
        Is(ctx, "the deleted player was the last seated, so nobody is remembered", string.Empty, store.LastPlayed);
        ctx.Check(menu.ShownRowCount == 5 && menu.ShownRow == 0 && menu.ShownRowText == "Name:  (none)",
            $"the roster is back to one row with the field cleared ({menu.ShownRowCount}, {menu.ShownRowText})");
    }

    private static void FreshCabin(TestContext ctx, LaunchMenu menu, CampaignProfileStore store)
    {
        var flow = menu.Campaign!;
        WalkTo(menu, Pilot);
        menu.Drive(Accept);
        Is(ctx, "the first confirm on a roster row selects it", "✓ " + Pilot, menu.ShownRowText);
        ctx.Check(flow.Screen == CampaignScreen.Roster, $"and stays on the roster ({flow.Screen})");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Cabin && flow.Profile?.Name == Pilot, $"the second continues onto the cabin ({flow.Screen})");
        Is(ctx, "seating records the player again", Pilot, store.LastPlayed);
        menu.Drive(Down);
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.PreviousMissions && menu.ShownHeading == "PREVIOUS MISSIONS",
            $"Previous Missions opens the table of contents ({flow.Screen})");
        ctx.Check(menu.ShownRowCount == 5 && menu.ShownRow == 0,
            $"with no mission flown, the career row alone over the four buttons ({menu.ShownRowCount})");
        Is(ctx, "the one row is the career page", flow.Strings.Text(1217, "Starting My Career"), menu.ShownDetail);
        ctx.Check(ButtonRow(flow, BoardButton.ReplayMission) < 0, $"REPLAY MISSION is not offered on it");
        CareerPage(ctx, menu, flow);
        ctx.Check(flow.Screen == CampaignScreen.PreviousMissions, $"Back returns to the contents ({flow.Screen})");
        menu.Drive(Back);
        ctx.Check(flow.Screen == CampaignScreen.Cabin, $"Back returns to the cabin ({flow.Screen})");
        menu.Drive(Back);
        menu.Drive(Back);
        ctx.Check(menu.ShownScreen == "Mode" && menu.Campaign == null, $"Back twice more leaves the campaign ({menu.ShownScreen})");
    }

    // The career page as the contents' secondary press reaches it: SCRAPBOOK.CSV slot 0, whose one
    // spread carries scraps alone, titled langui 1216 over the player's name. Leaves the cursor
    // back on the contents.
    private static void CareerPage(TestContext ctx, LaunchMenu menu, CampaignFlow flow)
    {
        menu.Drive(Secondary);
        ctx.Check(flow.Screen == CampaignScreen.Scrapbook && flow.MissionSeq == -1 && menu.ShownHeading == "SCRAPBOOK",
            $"X on the career row opens the book at its front ({flow.Screen}, seq {flow.MissionSeq})");
        ctx.Check(menu.ShownBoard is { } board && HasLine(board, flow.Strings.Format(1216, Pilot)),
            $"titled over the player's name alone, langui 1216");
        int shipped = ScrapbookComposition.Items(ctx.DataRoot, 0, 1).Count;
        ctx.Note($"SCRAPBOOK.CSV slot 0 carries {shipped} scraps");
        ctx.Check(shipped > 0 && flow.Page.Pictures.Count == shipped && flow.Page.Captions.Count == 1,
            $"its scraps draw with no results card over them ({flow.Page.Pictures.Count} pictures, {flow.Page.Captions.Count} lines)");
        ctx.Check(ButtonRow(flow, BoardButton.ReplayMission) < 0 && ButtonRow(flow, BoardButton.MostTab) < 0,
            $"and the page offers neither REPLAY MISSION nor a results tab");
        menu.Drive(Back);
    }

    // The career page from the other side: the book opened on mission 1 and its back arrow pressed
    // once, which lands on the front of the book rather than falling into the mission overview.
    // Leaves the cursor back on the contents.
    private static void BackArrowOffMissionOne(TestContext ctx, LaunchMenu menu, CampaignFlow flow)
    {
        WalkToRow(menu, 1);
        menu.Drive(Secondary);
        ctx.Check(flow.Screen == CampaignScreen.Scrapbook && flow.MissionSeq == 0,
            $"X on the first mission row opens the book on mission 1 ({flow.Screen}, seq {flow.MissionSeq})");
        int prev = ButtonRow(flow, BoardButton.ScrapbookPrev);
        ctx.Check(prev >= 0, $"whose results page carries the back arrow ({prev})");
        if (prev >= 0 && ScrapbookComposition.Items(ctx.DataRoot, 0, 1).Count > 0)
        {
            WalkToRow(menu, prev);
            menu.Drive(Accept);
            ctx.Check(flow.Screen == CampaignScreen.Scrapbook && flow.Page.Captions.Count == 1
                && flow.Page.Captions[0].Text == flow.Strings.Format(1216, Pilot),
                $"and the arrow turns back onto the career page ({flow.Screen}, {flow.Page.Captions.Count} lines, '{(flow.Page.Captions.Count > 0 ? flow.Page.Captions[0].Text : string.Empty)}')");
        }

        menu.Drive(Back);
    }

    // Three missions flown, the shape the progressed aid store has, written the way the session's
    // director writes a result: through the progression rules and one save.
    private static void Progress(CampaignProfileStore store)
    {
        var profile = store.Load(Pilot)!;
        for (int seq = 0; seq < FlownBefore; seq++)
        {
            CampaignProgression.Record(profile, new MissionAttempt(
                seq, CampaignProgression.PrimaryObjectiveMask, 300_000 + (seq * 20_000), 400, 120,
                profile.Planes[0].Airframe, profile.Planes[0].Name));
        }

        store.Save(profile);
    }

    private static void PreviousMissions(TestContext ctx, LaunchMenu menu, CampaignProfileStore store)
    {
        menu.ShowMenu();
        menu.OpenCampaignCabin(Pilot);
        var flow = menu.Campaign!;
        ctx.Check(menu.ShownScreen == "Campaign" && flow.Screen == CampaignScreen.Cabin && flow.Profile?.MissionsCompleted == FlownBefore,
            $"the cabin return re-reads the profile with its three flights ({flow.Profile?.MissionsCompleted})");
        menu.Drive(Down);
        Is(ctx, "previous missions now offers a review", "Review or replay a finished mission", menu.ShownDetail);
        menu.Drive(Accept);
        ctx.Check(menu.ShownRowCount == FlownBefore + 6 && menu.ShownRow == 0,
            $"the career row and three mission rows, then VIEW SELECTED, REPLAY MISSION, the arrow, the bookmark and RETURN TO CABIN ({menu.ShownRowCount})");
        Is(ctx, "the first row is the career page", flow.Strings.Text(1217, "Starting My Career"), menu.ShownDetail);
        menu.Drive(Down);
        Is(ctx, "the second is the first mission", flow.Strings.Text(3450, "Mission 1"), menu.ShownDetail);
        Has(ctx, "the footer names the view shortcut", "X  View", menu.ShownFooter);
        ctx.Check(menu.ShownBoard is { } board && board.Fills.Count >= 2, $"the focused row draws its wash and outline ({menu.ShownBoard?.Fills.Count})");
        menu.Drive(Accept);
        Is(ctx, "a confirm picks the row", "Selected. Confirm again to replay it", menu.ShownDetail);
        menu.Drive(Down);
        menu.Drive(Secondary);
        ctx.Check(flow.Screen == CampaignScreen.Scrapbook && flow.MissionSeq == 1 && menu.ShownHeading == "SCRAPBOOK",
            $"X on a mission row opens the book on that mission ({flow.Screen}, seq {flow.MissionSeq})");
        Is(ctx, "with the cursor on the way out", "RETURN TO CABIN", menu.ShownRowText);
        menu.Drive(Back);
        ctx.Check(flow.Screen == CampaignScreen.PreviousMissions, $"Back returns to the contents ({flow.Screen})");
        WalkTo(menu, "REPLAY MISSION");
        Is(ctx, "REPLAY MISSION names the picked mission", flow.Strings.Text(3451, "Mission 2"), menu.ShownDetail);
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Briefing && flow.MissionSeq == 1, $"and opens its briefing ({flow.Screen}, seq {flow.MissionSeq})");
        menu.Drive(Back);
        ctx.Check(flow.Screen == CampaignScreen.PreviousMissions, $"Back from a replay's briefing returns to the contents ({flow.Screen})");
        BackArrowOffMissionOne(ctx, menu, flow);
        WalkTo(menu, "RETURN TO CABIN");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Cabin, $"RETURN TO CABIN lands on the cabin ({flow.Screen})");
        ctx.Check(store.Load(Pilot)?.MissionsCompleted == FlownBefore, $"nothing browsed wrote the profile");
    }

    private static void Briefing(TestContext ctx, LaunchMenu menu)
    {
        var flow = menu.Campaign!;
        WalkTo(menu, "Next Mission");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Briefing && flow.MissionSeq == FlownBefore,
            $"Next Mission opens the briefing of the next story position ({flow.Screen}, seq {flow.MissionSeq})");
        Is(ctx, "headed by the mission's long name", flow.Strings.Text(3450 + FlownBefore, "MISSION BRIEFING"), menu.ShownHeading);
        ctx.Check(menu.ShownRowCount == 3 && menu.ShownRow == 0 && menu.ShownRowText == "REPLAY BRIEFING",
            $"three plaques, the cursor on REPLAY BRIEFING ({menu.ShownRowCount}, {menu.ShownRowText})");
        if (flow.Page is not CampaignBriefingPage page || page.Reveal == null)
        {
            ctx.Note($"the extraction carries no briefing for seq {FlownBefore}; the reveal checks did not run");
            return;
        }

        ctx.Check(page.State != null && page.NarrationWav.Length > 0, $"the mission's state and narration resolved ({page.NarrationWav})");
        int pictures = menu.ShownBoard?.Pictures.Count ?? 0;
        int freshElements = page.Reveal.Elements.Count;
        for (int frame = 0; frame < 600; frame++)
        {
            menu._Process(1.0 / 60.0);
        }

        ctx.Check(page.Reveal.Clock > 9.9 && page.NarrationStarts == 1,
            $"ten seconds of frames advance the reveal and start the narration once ({page.Reveal.Clock:0.0}s, {page.NarrationStarts})");
        ctx.Check((menu.ShownBoard?.Pictures.Count ?? 0) > pictures || page.Reveal.RevealedObjectives.Count > 0,
            $"and the reveal put something on the board ({pictures} pictures to {menu.ShownBoard?.Pictures.Count}, {page.Reveal.RevealedObjectives.Count} objectives)");
        menu.Drive(Accept);
        menu._Process(1.0 / 60.0);
        ctx.Check(page.Reveal.Clock < 1.0 && page.NarrationStarts == 2,
            $"REPLAY BRIEFING restarts the reveal and the narration ({page.Reveal.Clock:0.0}s, {page.NarrationStarts})");

        // The reveal is driven on until the map is no longer blank, so the return below has
        // something to start over from; the first beat's time is the mission's own.
        int frames = 0;
        while (frames++ < 3600 && page.Reveal.Elements.Count == freshElements && page.Reveal.RevealedObjectives.Count == 0)
        {
            menu._Process(1.0 / 60.0);
        }

        ctx.Check(page.Reveal.Elements.Count > freshElements || page.Reveal.RevealedObjectives.Count > 0,
            $"a minute after REPLAY at most places more on the map than a fresh reveal or reveals an objective ({page.Reveal.Clock:0.0}s, {freshElements} to {page.Reveal.Elements.Count} elements, {page.Reveal.RevealedObjectives.Count} objectives)");
        menu.Drive(Down);
        Is(ctx, "the second plaque", "RETURN TO CABIN", menu.ShownRowText);
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Cabin, $"RETURN TO CABIN lands on the cabin that opened the briefing ({flow.Screen})");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Briefing && page.Reveal.Clock < 1.0
            && page.Reveal.Elements.Count == freshElements && page.Reveal.RevealedObjectives.Count == 0 && page.NarrationStarts == 3,
            $"Next Mission on the same mission reopens the briefing from a blank map with the narration asked for again ({page.Reveal.Clock:0.0}s, {page.Reveal.Elements.Count} elements, {page.Reveal.RevealedObjectives.Count} objectives, {page.NarrationStarts})");
    }

    private static void FlightCheckAndAmmo(TestContext ctx, LaunchMenu menu, CampaignProfileStore store)
    {
        var flow = menu.Campaign!;
        WalkTo(menu, "GO TO FLIGHT CHECK");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.FlightCheck, $"GO TO FLIGHT CHECK opens the flight check ({flow.Screen})");
        Is(ctx, "headed by the mission's name", flow.Strings.Text(3450 + FlownBefore, $"Mission {FlownBefore + 1}"), menu.ShownHeading);
        int slots = flow.MissionHasWingman ? 2 : 1;
        ctx.Check(menu.ShownRowCount == (slots * 2) + 2, $"a heading and CHANGE AMMO per crew slot, no CHANGE PLANE under two planes, then the two plaques ({menu.ShownRowCount}, wingman {flow.MissionHasWingman})");
        ctx.Check(menu.ShownRow == 1 && menu.ShownRowText == "CHANGE AMMO", $"the cursor settles past the pilot heading onto CHANGE AMMO ({menu.ShownRow}, {menu.ShownRowText})");
        Has(ctx, "the pilot heading names the starter", "Gypsy Magic", RowText(menu, 0));
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Ammo && flow.AmmoSlot == 0 && menu.ShownHeading == "AMMO SELECTION",
            $"CHANGE AMMO opens ammo selection on the pilot's aircraft ({flow.Screen}, slot {flow.AmmoSlot})");
        ctx.Check(menu.ShownRowCount == 14, $"four gun groups, eight pylons and the two plaques ({menu.ShownRowCount})");
        int early = FirstPylonRow(flow);
        if (early >= 0)
        {
            int offered = flow.Page.Combo(early)!.Entries.Count;
            ctx.Check(offered == PylonRowsAtFour,
                $"whose pylon offers the rows this profile's {FlownBefore} flights have unlocked and no more, torpedoes far off ({offered} of {CampaignLoadout.PylonRows})");
        }

        int group = -1;
        for (int row = 0; row < 4 && group < 0; row++)
        {
            if (flow.Page.Combo(row) != null)
            {
                group = row;
            }
        }

        ctx.Check(group >= 0, $"the starter mounts a gun in some group ({group})");
        if (group < 0)
        {
            return;
        }

        WalkTo(menu, flow.Page.RowText(group));
        Is(ctx, "the group's field reads the stock ammunition", flow.Strings.Text(3360, "Slug"), menu.ShownRowText);
        menu.Drive(Right);
        Is(ctx, "the stepper takes the next ammunition", flow.Strings.Text(3361, "Dum-dum"), menu.ShownRowText);
        string upper = PaneText(menu.ShownBoard, BoardDetailPane.Upper, flow.Layout);
        string lower = PaneText(menu.ShownBoard, BoardDetailPane.Lower, flow.Layout);
        ctx.Check(upper.Length > 0 && lower.Length > 0 && upper != lower,
            $"the screen reads the gun's ammunition and the pylon's ordnance side by side (\"{upper}\" / \"{lower}\")");
        ctx.Check(store.Load(Pilot)!.Planes[0].Ammo[group] == 0, $"nothing is written before ACCEPT");
        WalkTo(menu, "ACCEPT LOADOUT");
        ctx.Check(PaneText(menu.ShownBoard, BoardDetailPane.Upper, flow.Layout) == upper
            && PaneText(menu.ShownBoard, BoardDetailPane.Lower, flow.Layout) == lower,
            $"and the plaques leave both panes on the gun and the pylon they describe");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.FlightCheck, $"ACCEPT LOADOUT returns to the flight check ({flow.Screen})");
        ctx.Check(store.Load(Pilot)!.Planes[0].Ammo[group] == 1, $"and the profile file carries the pick");
        string saved = File.ReadAllText(Path.Combine(store.DirFor(Pilot), "profile.json"));
        WalkTo(menu, "CHANGE AMMO");
        menu.Drive(Accept);
        WalkTo(menu, flow.Page.RowText(group));
        menu.Drive(Right);
        Is(ctx, "a second edit steps on", flow.Strings.Text(3362, "Armor-piercing"), menu.ShownRowText);
        WalkTo(menu, "CANCEL LOADOUT");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.FlightCheck && File.ReadAllText(Path.Combine(store.DirFor(Pilot), "profile.json")) == saved,
            $"CANCEL LOADOUT returns with the file byte for byte as ACCEPT left it");
        ctx.Check(flow.Profile!.Planes[0].Ammo[group] == 1, $"and the seated profile still reads the accepted pick ({flow.Profile.Planes[0].Ammo[group]})");
    }

    private static void PlaneSelection(TestContext ctx, LaunchMenu menu, CampaignProfileStore store)
    {
        // A third aircraft, since the flight check bars CHANGE PLANE under three.
        var profile = store.Load(Pilot)!;
        profile.Planes.Add(new OwnedPlane { Name = "Test Bird", Airframe = 3 });
        store.Save(profile);
        menu.ShowMenu();
        menu.OpenCampaignCabin(Pilot);
        var flow = menu.Campaign!;
        menu.Drive(Accept);
        WalkTo(menu, "GO TO FLIGHT CHECK");
        menu.Drive(Accept);
        int slots = flow.MissionHasWingman ? 2 : 1;
        ctx.Check(flow.Screen == CampaignScreen.FlightCheck && menu.ShownRowCount == (slots * 3) + 2,
            $"with three planes each crew slot offers CHANGE PLANE too ({menu.ShownRowCount})");
        WalkTo(menu, "CHANGE PLANE");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.PlaneSelection && flow.PlaneSlot == 0 && menu.ShownHeading == flow.Strings.Text(3450 + FlownBefore, $"Mission {FlownBefore + 1}"),
            $"CHANGE PLANE opens plane selection on the pilot's slot ({flow.Screen}, slot {flow.PlaneSlot})");
        ctx.Check(menu.ShownRow == 0 && menu.ShownRowCount == (slots * 2) + 2, $"a combo and EXPORT per slot then the two plaques, cursor on the pilot's combo ({menu.ShownRow}, {menu.ShownRowCount})");
        Has(ctx, "the combo reads the pilot's plane", "Gypsy Magic", menu.ShownRowText);
        menu.Drive(Left);
        Has(ctx, "Left wraps the pick onto the new plane", "Test Bird", menu.ShownRowText);
        ctx.Check(flow.Modal == null && store.Load(Pilot)!.SelectedPlane == 0, $"no refusal, and nothing written before ACCEPT");
        WalkTo(menu, "ACCEPT SELECTIONS");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.FlightCheck && store.Load(Pilot)!.SelectedPlane == 2,
            $"ACCEPT SELECTIONS writes the pilot's pick into the profile ({store.Load(Pilot)!.SelectedPlane})");
        Has(ctx, "and the pilot heading now names it", "Test Bird", RowText(menu, 0));
        WalkTo(menu, "CHANGE PLANE");
        menu.Drive(Accept);
        menu.Drive(Right);
        Has(ctx, "Right wraps back onto the starter", "Gypsy Magic", menu.ShownRowText);
        WalkTo(menu, "CANCEL SELECTIONS");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.FlightCheck && store.Load(Pilot)!.SelectedPlane == 2 && flow.Profile!.SelectedPlane == 2,
            $"CANCEL SELECTIONS keeps the accepted pick ({store.Load(Pilot)!.SelectedPlane})");
    }

    private static void HangarDoor(TestContext ctx, LaunchMenu menu, CampaignProfileStore store)
    {
        var flow = menu.Campaign!;
        WalkTo(menu, "RETURN TO BRIEFING");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Briefing, $"RETURN TO BRIEFING returns to the briefing ({flow.Screen})");
        WalkTo(menu, "RETURN TO CABIN");
        menu.Drive(Accept);
        WalkTo(menu, "Plane Construction");
        Is(ctx, "the cabin's third row", "Buy, sell and fit aircraft", menu.ShownDetail);
        menu.Drive(Accept);
        ctx.Check(menu.ShownScreen == "Hangar" && menu.Hangar is { Campaign: not null, Screen: HangarScreen.PlaneSelection },
            $"PLANE CONSTRUCTION opens the hangar over the profile's wallet ({menu.ShownScreen})");
        ctx.Check(menu.Hangar!.Campaign!.Funds == store.Load(Pilot)!.Funds && menu.Hangar.Saved.Count == 3,
            $"the wallet is the profile's and the roster its three planes ({menu.Hangar.Campaign.Funds}, {menu.Hangar.Saved.Count})");
        ctx.Check(ReferenceEquals(menu.Campaign, flow) && flow.Exit == CampaignExit.OpenHangar, $"the campaign flow stands behind the door ({flow.Exit})");
        menu.Drive(Back);
        ctx.Check(menu.ShownScreen == "Campaign" && menu.Hangar == null && ReferenceEquals(menu.Campaign, flow) && flow.Screen == CampaignScreen.Cabin && flow.Exit == CampaignExit.None,
            $"Back closes the hangar and resumes the cabin ({menu.ShownScreen}, {flow.Screen})");
        Is(ctx, "on the row that opened it", "Plane Construction", menu.ShownRowText);
    }

    private static void FlyMission(TestContext ctx, LaunchMenu menu, CampaignProfileStore store, List<MenuExit> exits)
    {
        var flow = menu.Campaign!;
        WalkTo(menu, "Next Mission");
        menu.Drive(Accept);
        WalkTo(menu, "GO TO FLIGHT CHECK");
        menu.Drive(Accept);
        WalkTo(menu, "FLY MISSION");
        var profile = flow.Profile!;
        menu.Drive(Accept);
        ctx.Check(exits.Count == 1 && exits[0] is CampaignMissionExit, $"FLY MISSION leaves as one campaign mission exit ({exits.Count})");
        if (exits.Count != 1 || exits[0] is not CampaignMissionExit exit)
        {
            return;
        }

        ctx.Check(exit.Profile == Pilot && exit.MissionSeq == FlownBefore && exit.Seats.Count == 1,
            $"for the seated profile at its next story position with one seat ({exit.Profile}, {exit.MissionSeq}, {exit.Seats.Count})");
        var seat = exit.Seats[0];
        ctx.Check(seat.PlaneNode == PlanePickerRoster.AirframeNode(3) && seat.Fit != null && seat.Custom == null && seat.Pads.Count == 0,
            $"seat 0 flies the picked Bloodhawk's node with its campaign fit, no build and no pad ({seat.PlaneNode}, fit {seat.Fit != null}, custom {seat.Custom != null})");
        ctx.Check(menu.Campaign == null && menu.ShownScreen == "Mode", $"the flow is closed and the screen stands on Mode for the return ({menu.ShownScreen})");
        ctx.Check(File.ReadAllText(Path.Combine(store.DirFor(Pilot), "profile.json")) == CampaignProfileStore.Serialize(profile),
            $"the profile was saved as it stood on the press");
    }

    private static void DebriefReturn(TestContext ctx, LaunchMenu menu, CampaignProfileStore store)
    {
        // The session's director records the flown mission and saves; the menu comes back on it.
        var profile = store.Load(Pilot)!;
        CampaignProgression.Record(profile, new MissionAttempt(
            FlownBefore, CampaignProgression.PrimaryObjectiveMask, 420_000, 200, 90, 3, "Test Bird"));
        store.Save(profile);
        menu.ShowMenu();
        menu.OpenCampaignScrapbook(Pilot, FlownBefore, missionWon: true);
        var flow = menu.Campaign!;
        ctx.Check(menu.ShownScreen == "Campaign" && flow.Screen == CampaignScreen.Scrapbook && flow.MissionSeq == FlownBefore,
            $"the debrief return opens the book on the flown mission ({flow.Screen}, seq {flow.MissionSeq})");
        ctx.Check(flow.Profile?.MissionsCompleted == FlownBefore + 1, $"over the profile as the mission wrote it ({flow.Profile?.MissionsCompleted})");
        Is(ctx, "headed as the book", "SCRAPBOOK", menu.ShownHeading);
        Is(ctx, "with the cursor on the way out", "RETURN TO CABIN", menu.ShownRowText);
        ctx.Check(flow.Page.RowText(0) == "REPLAY MISSION", $"the results page offers REPLAY MISSION first ({flow.Page.RowText(0)})");
        ctx.Check(menu.ShownBoard is { } board && HasLine(board, "Mission Completed") && HasLine(board, "$" + profile.MissionResults[^1].Latest.Money),
            $"the results card reads the flown mission's outcome and cash");
        int prev = ButtonRow(flow, BoardButton.ScrapbookPrev);
        ctx.Check(prev >= 0, $"the page carries its back arrow ({prev})");
        if (prev >= 0)
        {
            WalkToRow(menu, prev);
            menu.Drive(Accept);
            bool hasSpreads = ScrapbookComposition.Items(ctx.DataRoot, FlownBefore, 1).Count > 0;
            if (hasSpreads)
            {
                ctx.Check(flow.Screen == CampaignScreen.Scrapbook && flow.Page.Button(menu.ShownRow).Button == BoardButton.ScrapbookPrev,
                    $"the arrow turns back a page and keeps the cursor on itself ({flow.Screen}, row {menu.ShownRow})");
                int next = ButtonRow(flow, BoardButton.ScrapbookNext);
                ctx.Check(next >= 0, $"and the forward arrow is offered again ({next})");
                WalkToRow(menu, next);
                menu.Drive(Accept);
                ctx.Check(flow.Page.RowText(0) == "REPLAY MISSION", $"forward returns to the results page ({flow.Page.RowText(0)})");
            }
            else
            {
                ctx.Check(flow.Screen == CampaignScreen.PreviousMissions, $"with no earlier spread the arrow falls into the mission overview ({flow.Screen})");
                WalkTo(menu, flow.Strings.Text(1200, "Current Mission"));
                menu.Drive(Accept);
            }
        }

        WalkTo(menu, "RETURN TO CABIN");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Cabin && menu.ShownHeading == "CAMPAIGN CABIN", $"RETURN TO CABIN lands on the cabin ({flow.Screen})");
        menu.Drive(Back);
        ctx.Check(flow.Screen == CampaignScreen.Roster, $"Back from the cabin behind the book returns to the roster ({flow.Screen})");
        menu.Drive(Back);
        ctx.Check(menu.ShownScreen == "Mode" && menu.Campaign == null, $"and Back once more leaves the campaign ({menu.ShownScreen})");
    }

    // A replay offers what the pilot has earned, not what the replayed mission had unlocked: both
    // the pylon lists and the plane roster are filled from the profile's progress, which is the
    // counter the original filters on (docs/formats/campaign-screens.md, "Ammo selection").
    private static void ReplayOffersEarnedRows(
        TestContext ctx, LaunchMenu menu, CampaignProfileStore store)
    {
        store.Save(Flown(Veteran, TorpedoOrdinal));
        menu.ShowMenu();
        menu.OpenCampaignCabin(Veteran);
        var flow = menu.Campaign!;
        var profile = flow.Profile!;
        int late = LatestAvailability(profile);
        ctx.Check(profile.MissionsCompleted == TorpedoOrdinal && profile.Planes.Count > 2 && late > 1,
            $"a veteran's cabin opens past the torpedo missions with its awards in the hangar ({profile.MissionsCompleted}, {profile.Planes.Count} planes, latest availability {late})");
        menu.Drive(Down);
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.PreviousMissions, $"previous missions opens the contents ({flow.Screen})");
        menu.Drive(Down);
        menu.Drive(Accept);
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Briefing && flow.MissionSeq == 0,
            $"a second press on the first mission row replays it ({flow.Screen}, seq {flow.MissionSeq})");
        WalkTo(menu, "GO TO FLIGHT CHECK");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.FlightCheck, $"whose flight check is the replay's ({flow.Screen})");
        WalkTo(menu, "CHANGE AMMO");
        menu.Drive(Accept);
        int pylon = FirstPylonRow(flow);
        ctx.Check(pylon >= 0, $"the pilot's aircraft carries a hardpoint to fit ({pylon})");
        if (pylon >= 0)
        {
            int rows = flow.Page.Combo(pylon)!.Entries.Count;
            ctx.Check(rows == CampaignLoadout.PylonRows,
                $"and its pylon offers every earned ordnance, torpedoes included, on a mission whose own ordinal unlocks {OpeningPylonRows} ({rows} of {CampaignLoadout.PylonRows})");
        }

        WalkTo(menu, "CANCEL LOADOUT");
        menu.Drive(Accept);
        WalkTo(menu, "CHANGE PLANE");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.PlaneSelection, $"CHANGE PLANE opens plane selection on the replay ({flow.Screen})");
        int listed = flow.Page.Combo(0)?.Entries.Count ?? 0;
        ctx.Check(listed == profile.Planes.Count,
            $"whose combo lists every owned aircraft, the awards whose airframes unlock as late as mission {late} among them ({listed} of {profile.Planes.Count})");
        for (int step = 0; step < 8 && menu.Campaign != null; step++)
        {
            menu.Drive(Back);
        }

        ctx.Check(menu.Campaign == null, $"and Back walks the replay back out of the campaign ({menu.ShownScreen})");
    }

    // The first pylon cell of the ammo screen drawing a field: a hardpoint the wing does not carry
    // has none, and the gun groups stand on the rows before them.
    private static int FirstPylonRow(CampaignFlow flow)
    {
        for (int row = FirstPylon; row < PastPylons; row++)
        {
            if (flow.Page.Combo(row) != null)
            {
                return row;
            }
        }

        return -1;
    }

    // The highest availability threshold among the airframes a profile owns, the stat table's own
    // column: past 1 means an airframe the campaign's first mission does not offer.
    private static int LatestAvailability(CampaignProfileDef profile)
    {
        int latest = 0;
        foreach (var plane in profile.Planes)
        {
            latest = Math.Max(latest, HangarEconomy.Airframes[plane.Airframe].Availability);
        }

        return latest;
    }

    private static void GuestCheck(TestContext ctx, LaunchMenu menu)
    {
        menu.ShowMenu("campaign-guestcheck:2");
        menu.DebugJoin(3);
        var flow = menu.Campaign!;
        ctx.Check(flow.Screen == CampaignScreen.FlightCheck && flow.Field.Players == 4 && flow.Field.Current == 2 && flow.Field.Locked,
            $"the guest-check aid walks a four-player field to P3's check ({flow.Field.Players}, current {flow.Field.Current})");
        ctx.Check(menu.ShownBoard is { } board && HasLine(board, "FLIGHT CHECK P3"), $"headed for the guest");
        ctx.Check(menu.ShownRowCount == 5 && menu.ShownRow == 1 && menu.ShownRowText == "CHANGE AMMO",
            $"a guest's check is one PILOT block with CHANGE AMMO and CHANGE PLANE, then the two plaques ({menu.ShownRowCount}, {menu.ShownRowText})");
        menu.Drive(Back);
        ctx.Check(flow.Field.Current == 1 && flow.Screen == CampaignScreen.FlightCheck, $"Back retreats to P2's check ({flow.Field.Current})");
        ctx.Check(menu.ShownBoard is { } second && HasLine(second, "FLIGHT CHECK P2"), $"headed for that guest");
        WalkTo(menu, "FLY MISSION");
        menu.Drive(Accept);
        ctx.Check(flow.Field.Current == 2 && menu.ShownRowText == "CHANGE AMMO",
            $"FLY MISSION on a guest's check advances to the next, the cursor opening afresh ({flow.Field.Current}, {menu.ShownRowText})");
        WalkTo(menu, "FLY MISSION");
        menu.Drive(Accept);
        ctx.Check(flow.Field.Current == 3 && flow.Screen == CampaignScreen.FlightCheck, $"and again onto P4's ({flow.Field.Current})");
    }

    // A guest's ammunition pick is theirs for the rest of the run. The launch discards the campaign
    // and the return re-reads the profile, and the next mission's check still opens on what they
    // chose. ⚠ The same walk pins that none of it reached the seated profile. A guest's record is
    // a copy, and their ACCEPT must leave the profile file untouched.
    private static void GuestPickAcrossMissions(
        TestContext ctx, LaunchMenu menu, CampaignProfileStore store, List<MenuExit> exits)
    {
        string file = Path.Combine(store.DirFor(Pilot), "profile.json");
        var flow = OpenGuestFlightCheck(ctx, menu, "four humans reach the mission's checks");
        if (flow == null)
        {
            return;
        }

        string picked = flow.Field.Plane(1)?.Name ?? string.Empty;
        int seq = flow.MissionSeq;
        WalkTo(menu, "CHANGE AMMO");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Ammo && ReferenceEquals(flow.AmmoTarget(), flow.Field.Plane(1)),
            $"the guest's CHANGE AMMO opens on their own aircraft ({flow.Screen}, {picked})");
        int group = -1;
        for (int row = 0; row < 4 && group < 0; row++)
        {
            if (flow.Page.Combo(row) != null)
            {
                group = row;
            }
        }

        ctx.Check(group >= 0, $"the guest's aircraft mounts a gun to fit ({group})");
        if (group < 0)
        {
            return;
        }

        string before = File.ReadAllText(file);
        WalkTo(menu, flow.Page.RowText(group));
        menu.Drive(Right);
        WalkTo(menu, "ACCEPT LOADOUT");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.FlightCheck && flow.Field.Plane(1)?.Ammo[group] == 1,
            $"ACCEPT LOADOUT fits the guest's own record ({flow.Field.Plane(1)?.Ammo[group]})");
        ctx.Check(File.ReadAllText(file) == before, $"and writes nothing into the seated profile's file");

        for (int player = 1; player < 4; player++)
        {
            WalkTo(menu, "FLY MISSION");
            menu.Drive(Accept);
        }

        ctx.Check(menu.Campaign == null && exits[^1] is CampaignMissionExit { Seats.Count: 4 },
            $"the last check's FLY MISSION launches all four seats ({(exits[^1] as CampaignMissionExit)?.Seats.Count})");

        // The mission's own record, which is what brings the cabin back on a later story position.
        // The reset under test is the launch's discard and the cabin's re-read profile.
        var flown = store.Load(Pilot)!;
        CampaignProgression.Record(flown, new MissionAttempt(
            seq, CampaignProgression.PrimaryObjectiveMask, 300_000, 120, 50, 2, "Test Bird"));
        store.Save(flown);

        flow = OpenGuestFlightCheck(ctx, menu, "the next mission's checks open on the same four");
        if (flow == null)
        {
            return;
        }

        ctx.Check(flow.MissionSeq == seq + 1, $"on the story position the flown mission advanced to ({flow.MissionSeq})");
        var kept = flow.Field.Plane(1);
        ctx.Check(kept?.Name == picked && kept?.Ammo[group] == 1,
            $"and the guest's check still stands on their aircraft with the ammunition they picked ({kept?.Name}, {kept?.Ammo[group]})");
    }

    // The walk both halves above share: the cabin a flown mission returns to, then the frame that
    // takes the joined seats onto the field. Then Next Mission, and FLY MISSION onto the first
    // guest's check. Null when the walk did not land there, which the caller reports.
    private static CampaignFlow? OpenGuestFlightCheck(TestContext ctx, LaunchMenu menu, string what)
    {
        menu.ShowMenu();
        menu.OpenCampaignCabin(Pilot);
        menu.Drive(MenuCommands.None);
        var flow = menu.Campaign;
        WalkTo(menu, "Next Mission");
        menu.Drive(Accept);
        WalkTo(menu, "GO TO FLIGHT CHECK");
        menu.Drive(Accept);
        WalkTo(menu, "FLY MISSION");
        menu.Drive(Accept);
        bool there = flow is { Screen: CampaignScreen.FlightCheck, Field.Players: 4, Field.Current: 1 };
        ctx.Check(there, $"{what}: P2's flight check ({flow?.Screen}, {flow?.Field.Players} players, current {flow?.Field.Current})");
        return there ? flow : null;
    }

    private static void Aids(TestContext ctx, LaunchMenu menu)
    {
        menu.ShowMenu("campaign-empty");
        ctx.Check(menu.Campaign is { Screen: CampaignScreen.Roster, Roster.Count: 0 } && menu.ShownRowCount == 4, $"--menu=campaign-empty opens an empty roster ({menu.ShownRowCount})");
        menu.ShowMenu("campaign-roster");
        // ⚠ Pinned as it behaves, not as the inventory describes it: the aid seeds two profiles and
        // then seats the first before branching on its name, so the screen it shows is the cabin.
        ctx.Check(menu.Campaign is { Screen: CampaignScreen.Cabin, Roster.Count: 2, Profile.Name: Pilot },
            $"--menu=campaign-roster seeds two profiles and lands on the first one's cabin ({menu.Campaign?.Screen}, {menu.Campaign?.Roster.Count})");
        menu.ShowMenu("campaign-entry");
        ctx.Check(menu.Campaign is { Screen: CampaignScreen.Roster, CapturesText: true } && menu.ShownRowText == "Name:  Zachary_", $"--menu=campaign-entry opens the roster mid-entry ({menu.ShownRowText})");
        menu.ShowMenu("campaign-cabin");
        ctx.Check(menu.Campaign is { Screen: CampaignScreen.Cabin } && menu.Campaign.Profile?.MissionsCompleted == FlownBefore, $"--menu=campaign-cabin opens the progressed cabin ({menu.Campaign?.Profile?.MissionsCompleted})");
        menu.ShowMenu("campaign-previous");
        ctx.Check(menu.Campaign is { Screen: CampaignScreen.PreviousMissions } && menu.ShownRowCount == FlownBefore + 6, $"--menu=campaign-previous opens the contents with the career row over three flights ({menu.ShownRowCount})");
        // x is the pad's secondary press, which Built-in binds and Original does not: here it views
        // the mission the cursor stands on without walking down to the button.
        menu.ShowMenu("campaign-previous:2d-x");
        ctx.Check(menu.Campaign is { Screen: CampaignScreen.Scrapbook, MissionSeq: 1 },
            $"--menu=campaign-previous:2d-x presses Built-in's secondary on the second flight ({menu.Campaign?.Screen}, {menu.Campaign?.MissionSeq})");
        menu.ShowMenu("campaign-scrapbook");
        ctx.Check(menu.Campaign is { Screen: CampaignScreen.Scrapbook, MissionSeq: FlownBefore - 1 } && menu.ShownRowText == "RETURN TO CABIN", $"--menu=campaign-scrapbook opens the book on the last flown mission ({menu.Campaign?.MissionSeq})");
        menu.ShowMenu("campaign-briefing:24");
        ctx.Check(menu.Campaign is { Screen: CampaignScreen.Briefing, MissionSeq: FlownBefore } && menu.Campaign.Page is CampaignBriefingPage { Reveal: { Clock: > 23.9 } },
            $"--menu=campaign-briefing:24 opens the briefing advanced to 24 s ({(menu.Campaign?.Page as CampaignBriefingPage)?.Reveal?.Clock:0.0})");
        menu.ShowMenu("campaign-flightcheck");
        ctx.Check(menu.Campaign is { Screen: CampaignScreen.FlightCheck, MissionSeq: FlownBefore } && menu.ShownRowText == "CHANGE AMMO", $"--menu=campaign-flightcheck opens the flight check ({menu.ShownRowText})");
        menu.ShowMenu("campaign-ammo");
        ctx.Check(menu.Campaign is { Screen: CampaignScreen.Ammo, AmmoSlot: 0 } && menu.ShownHeading == "AMMO SELECTION", $"--menu=campaign-ammo opens ammo selection ({menu.ShownHeading})");
        menu.ShowMenu("campaign-planeselection");
        ctx.Check(menu.Campaign is { Screen: CampaignScreen.PlaneSelection, PlaneSlot: 0 } && menu.ShownRow == 0, $"--menu=campaign-planeselection opens plane selection on the pilot ({menu.ShownRow})");
        menu.ShowMenu("campaign-hangar");
        menu.Drive(MenuCommands.None);
        ctx.Check(menu.ShownScreen == "Hangar" && menu.Hangar is { Campaign: not null } && menu.Campaign is { Screen: CampaignScreen.Cabin },
            $"--menu=campaign-hangar opens the hangar over the scratch profile's wallet with the cabin behind it ({menu.ShownScreen})");
    }

    // Built-in's two campaign films and the press that ends one, in the order the runtime runs them:
    // the cinema's own handler stops a film and hands off inside the input flush, and the seat poll
    // that reads the press runs after it in the same frame. The Stop calls below stand for that
    // flush and the Drive that follows each for that poll, so one press spans the hand-back. Its own
    // host: a film on the journey's would stand in front of every cabin door that journey takes.
    private static void FilmHandBack(TestContext ctx)
    {
        var chapter = new FilmRecorder();
        var closing = new FilmRecorder();
        var host = MenuSuiteHost.Bare(new List<MenuExit>(), ctx.DataRoot, out var seat,
            chapterCinema: new ChapterCinema(chapter.Play), closingCinema: new ClosingCinema(closing.Play));
        var menu = LaunchMenu.Build(ctx.ZrdrPath, ctx.DataRoot, host, seat.Input);
        ctx.Host.AddChild(menu);
        menu.SetProcess(false);
        string root = Path.Combine(ctx.ScratchDir, "menu-campaign-film");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        var store = new CampaignProfileStore(Path.Combine(root, "Profiles"));
        menu.CampaignProfiles = store;
        try
        {
            ChapterFilm(ctx, menu, store, chapter);
            ClosingFilm(ctx, menu, store, closing);
        }
        finally
        {
            ctx.Host.RemoveChild(menu);
            menu.QueueFree();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // The chapter film the flight-return door plays in front of the cabin.
    private static void ChapterFilm(
        TestContext ctx, LaunchMenu menu, CampaignProfileStore store, FilmRecorder film)
    {
        store.Save(Flown(Filmed, ChapterCinema.MissionsPerChapter));
        menu.ShowMenu();
        menu.OpenCampaignCabin(Filmed);
        var flow = menu.Campaign!;
        ctx.Check(film.Plays == 1 && film.Name == ChapterCinema.NameOf(2),
            $"the cabin door plays the film of the chapter the profile has reached ({film.Name})");
        ctx.Check(flow.Screen == CampaignScreen.Roster, $"and the cabin waits behind it ({flow.Screen})");
        menu.Drive(Down);
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Roster && film.Plays == 1,
            $"a press under the film walks and fires nothing on the screen behind it ({flow.Screen})");
        film.Stop();
        ctx.Check(flow.Screen == CampaignScreen.Cabin,
            $"the hand-off opens the cabin on the frame the film stops ({flow.Screen})");
        bool redrawn = menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Cabin,
            $"the poll on that frame reads the press that ended the film as nothing ({flow.Screen})");
        ctx.Check(redrawn, $"and that frame still redraws, the screen having changed unread behind the film");
        Is(ctx, "the cabin standing on its first plaque", "Next Mission", menu.ShownRowText);
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Briefing, $"the next press is the player's own ({flow.Screen})");
    }

    // The closing film the mission-end door plays in front of the book, the second of the two.
    private static void ClosingFilm(
        TestContext ctx, LaunchMenu menu, CampaignProfileStore store, FilmRecorder film)
    {
        int last = CampaignSequence.MissionCount - 1;
        store.Save(Flown(Finished, CampaignSequence.MissionCount));
        menu.ShowMenu();
        menu.OpenCampaignScrapbook(Finished, last, missionWon: true);
        var flow = menu.Campaign!;
        ctx.Check(film.Plays == 1 && film.Name == ClosingCinema.Name,
            $"the mission-end door plays the closing film ({film.Name})");
        ctx.Check(flow.Screen == CampaignScreen.Cabin, $"and the book waits behind it ({flow.Screen})");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Cabin,
            $"a press under the film fires nothing on the cabin behind it ({flow.Screen})");
        film.Stop();
        ctx.Check(flow.Screen == CampaignScreen.Scrapbook,
            $"the hand-off opens the book on the frame the film stops ({flow.Screen})");
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Scrapbook,
            $"the poll on that frame reads the press that ended it as nothing ({flow.Screen})");
        Is(ctx, "the book standing on its way out", "RETURN TO CABIN", menu.ShownRowText);
        menu.Drive(Accept);
        ctx.Check(flow.Screen == CampaignScreen.Cabin, $"and the next press is the book's own ({flow.Screen})");
    }

    // A profile that has completed its first flown missions, written through the progression rules
    // the way a mission director writes one.
    private static CampaignProfileDef Flown(string name, int flown)
    {
        var profile = CampaignProfileDef.NewProfile(name);
        for (int seq = 0; seq < flown; seq++)
        {
            CampaignProgression.Record(profile, new MissionAttempt(
                seq, CampaignProgression.PrimaryObjectiveMask, 300_000, 400, 120,
                profile.Planes[0].Airframe, profile.Planes[0].Name));
        }

        return profile;
    }

    // What one of the ammo screen's two description panes carries, found by the pane's own authored
    // position: the composer writes each pane's words into its own column.
    private static string PaneText(ComposedBoard? board, BoardDetailPane pane, CampaignLayout layout)
    {
        if (board == null || CampaignBoards.DetailSlot(CampaignScreen.Ammo, pane, layout) is not { } slot)
        {
            return string.Empty;
        }

        foreach (var line in board.Lines)
        {
            if (line.X == slot.X && line.Y == slot.Y)
            {
                return line.Text;
            }
        }

        return string.Empty;
    }

    private static bool HasLine(ComposedBoard board, string text)
    {
        foreach (var line in board.Lines)
        {
            if (line.Text == text)
            {
                return true;
            }
        }

        return false;
    }

    private static int ButtonRow(CampaignFlow flow, BoardButton button)
    {
        for (int row = 0; row < flow.Page.RowCount; row++)
        {
            if (flow.Page.Button(row).Button == button)
            {
                return row;
            }
        }

        return -1;
    }

    // Walks the cursor onto the row carrying a text, at most one lap; the cursor stays put when no
    // row carries it.
    private static void WalkTo(LaunchMenu menu, string text)
    {
        int count = menu.ShownRowCount;
        for (int i = 0; i < count && menu.ShownRowText != text; i++)
        {
            menu.Drive(Down);
        }
    }

    private static void WalkToRow(LaunchMenu menu, int row)
    {
        int count = menu.ShownRowCount;
        for (int i = 0; i < count && menu.ShownRow != row; i++)
        {
            menu.Drive(Down);
        }
    }

    // The text of an absolute row, read off the page: the heading rows are not focusable, so the
    // cursor cannot be walked onto them.
    private static string RowText(LaunchMenu menu, int row) => menu.Campaign?.Page.RowText(row) ?? string.Empty;

    private static void Is(TestContext ctx, string what, string expected, string actual) =>
        ctx.Check(expected == actual, $"{what}: expected '{expected}', got '{actual}'");

    private static void Has(TestContext ctx, string what, string expected, string actual) =>
        ctx.Check(actual.Contains(expected, StringComparison.Ordinal),
            $"{what}: expected '{expected}' in '{actual}'");

    // The stand-in for Launcher.PlayCinema: it records what it was asked for and hands the film's
    // end back, so the arm decides which frame the film stops on.
    private sealed class FilmRecorder
    {
        private Action? _then;

        public string? Name { get; private set; }

        public int Plays { get; private set; }

        public void Play(string name, Action then, CinemaSkip skip)
        {
            Name = name;
            Plays++;
            _then = then;
        }

        public void Stop() => _then?.Invoke();
    }
}
