using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CSVM.Flight.Hangar;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Menu;
using CSVM.UI.Menu.BuiltIn;
using CSVM.UI.Menu.Original;
using CSVM.UI.Screens;

namespace CSVM.Testing;

/// <summary>
/// The original's typed menu cheats through the presentation boundary over the install's own
/// decoded layout and a scratch profile store: the cabin's mission pull-down, the table of
/// contents' gallery, the plane construction hub's cash grant and the unlocking pilot name, each
/// driven as a player drives one, with a left click inside the screen's authored region and then
/// typed characters. ⚠ Nothing here touches <c>user://Profiles</c>: the presentation is pointed at
/// a scratch store.
/// </summary>
internal static class MenuOriginalCheatSuites
{
    private const float Dt = 1f / 60f;
    private const string Pilot = "Zachary";
    private const string Built = "Cheat Bird";
    private const string CabinPainting = "PC_BackGround.png";

    // A point inside each screen's authored cheat region. No row of any of the three screens stands
    // in one, which is what lets the click be read before the hit test.
    private const float CabinX = 20f;
    private const float CabinY = 400f;
    private const float ContentsX = 60f;
    private const float ContentsY = 450f;
    private const float HubX = 650f;
    private const float HubY = 40f;

    // The mission the pull-down is walked onto, as a row of its 24 and as the ordinal that row
    // stands for: row 2 is the third mission, cm_sequence index 2.
    private const int PickedRow = 2;

    private static readonly MenuCommands Accept = new() { Accept = true };
    private static readonly MenuCommands Back = new() { Back = true };

    [Suite("menu-original-cheats",
        "The original's typed menu cheats through the presentation boundary over the install's "
        + "decoded layout and a scratch profile store: a word typed on the cabin with nothing armed "
        + "fires nothing, a left click inside the cabin's authored region takes the keyboard and "
        + "opens no screen, the word in the wrong case and a word with a repeated first letter both "
        + "leave the buffer empty, the word itself shows the mission pull-down opened on the "
        + "campaign's own position, a press opens its list and a press on a row commits that "
        + "mission, NEXT MISSION then launches the picked row rather than the campaign's own, and "
        + "the press after it is ordinary again; the table of contents' own word puts every mission "
        + "of the campaign in the list with nothing flown and prints the book's position line over "
        + "the career page; the plane construction hub's word grants 25000 through the same wallet "
        + "the build is priced against, twice, and is refused at 50000; and the unlocking pilot "
        + "name on the profile screen re-accepts the player the screen stood on with a full wallet "
        + "and the eleven stock airframes, leaving the screen standing and every airframe offered")]
    internal static void MenuOriginalCheats(TestContext ctx)
    {
        ctx.RequireData(ctx.ZrdrPath, $"zrdr archive");
        ctx.RequireData(MenuLayout.PathUnder(ctx.DataRoot), $"decoded menu layout");
        var layout = OriginalAvailability.Load(ctx.DataRoot, out var why);
        ctx.Check(layout != null, $"the install's layout passes the availability check ({why ?? "ok"})");
        if (layout == null)
        {
            return;
        }

        string root = Path.Combine(ctx.ScratchDir, "menu-original-cheats");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        var store = new CampaignProfileStore(Path.Combine(root, "Profiles"));
        var exits = new List<MenuExit>();
        var seat = new ScriptedSeat();
        var registry = new PresentationRegistry();
        registry.Register(PresentationId.BuiltIn, () => new BuiltInPresentation(
            ctx.Host, ctx.ZrdrPath, ctx.DataRoot, string.Empty, new MenuInput { Keyboard = true }));
        registry.Register(PresentationId.Original, () => new OriginalPresentation(
            ctx.Host, ctx.DataRoot, layout, string.Empty, new MenuInput { Keyboard = true })
        {
            CampaignProfiles = store,
        });
        var host = new MenuHost(registry, new MenuSuiteHost.SilentMenuAudio(), exits.Add);
        MenuSuiteHost.AddFeatures(host, ctx.DataRoot);
        host.AddSeat(seat);
        var campaign = host.Features.Get<CampaignFeature>();
        try
        {
            host.Select(forceBuiltIn: false, cliOverride: "original");
            host.Show(MenuReturnDestination.TopLevel);
            var shell = (host.Active as OriginalPresentation)?.Shell;
            ctx.Check(shell != null, $"Show activates Original ({host.Active?.Id})");
            if (shell == null)
            {
                return;
            }

            var size = ctx.Host.GetViewport().GetVisibleRect().Size;
            var fit = BoardFit.For(size.X, size.Y);
            Seat(ctx, host, seat, shell, fit, campaign);
            Cabin(ctx, host, seat, shell, fit, campaign);
            Contents(ctx, host, seat, shell, fit, campaign);
            Hub(ctx, host, seat, shell, fit, campaign, store);
            UnlockingName(ctx, host, seat, shell, fit, campaign, store);
        }
        finally
        {
            host.Deactivate();
            Godot.Input.MouseMode = Godot.Input.MouseModeEnum.Visible;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // A player seated on the cabin, the screen all three typed words are reached from.
    private static void Seat(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, BoardFit fit,
        CampaignFeature campaign)
    {
        var door = Row(shell, OriginalShell.CampaignKey);
        if (door == null)
        {
            ctx.Check(false, $"the top level carries the Campaign row");
            return;
        }

        Click(host, seat, Pointer(fit, door.X + 5f, door.Y + 5f, pressed: true, clicked: true));
        Press(host, seat, new MenuCommands { Typed = Pilot });
        Press(host, seat, Accept);
        ctx.Check(shell.Screen == OriginalScreen.CampaignCabin && campaign.Profile?.Name == Pilot,
            $"a name typed on the profile screen seats a player on the cabin ({shell.Screen}, {campaign.Profile?.Name})");
    }

    // PASSENGERCABIN.SCRIPT's word: the click that gives the widget the keyboard, the two refusals,
    // the pull-down it shows, and the mission NEXT MISSION launches afterwards.
    private static void Cabin(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, BoardFit fit,
        CampaignFeature campaign)
    {
        if (shell.Screen != OriginalScreen.CampaignCabin)
        {
            return;
        }

        ctx.Check(!shell.TypingCheat && Dropdown(shell) == null,
            $"the cabin opens with no armed word and no pull-down ({shell.Rows.Count} rows)");
        Press(host, seat, new MenuCommands { Typed = CampaignCheats.MissionWord });
        ctx.Check(!campaign.Cheats.MissionListShown,
            $"the word typed with the widget unarmed fires nothing");

        Click(host, seat, Pointer(fit, CabinX, CabinY, pressed: true, clicked: true));
        ctx.Check(shell.Screen == OriginalScreen.CampaignCabin && shell.TypingCheat && host.Seats[0].CapturingText,
            $"a left click inside the region takes the keyboard and opens nothing ({shell.Screen}, typing {shell.TypingCheat})");

        Press(host, seat, new MenuCommands { Typed = "IDAHO" });
        ctx.Check(!campaign.Cheats.MissionListShown, $"the word in the wrong case fires nothing");
        Press(host, seat, new MenuCommands { Typed = "i" + CampaignCheats.MissionWord });
        ctx.Check(!campaign.Cheats.MissionListShown,
            $"and a repeated first letter empties the buffer rather than re-anchoring it");

        Press(host, seat, new MenuCommands { Typed = CampaignCheats.MissionWord });
        var field = Dropdown(shell);
        ctx.Check(campaign.Cheats.MissionListShown && field != null,
            $"the word shows the mission pull-down ({shell.Rows.Count} rows)");
        ctx.Same(1, campaign.Cheats.MissionPick, $"opened on the campaign's own position as a 1-based ordinal");
        if (field == null)
        {
            return;
        }

        // The field's paper is a fill and fills draw under the pictures, so the painting has to
        // stand in the backdrop for the closed field to show at all.
        var shown = shell.Compose();
        ctx.Check(HasArt(shown.Backdrop, CabinPainting) && !HasArt(shown.Pictures, CabinPainting) && shown.Fills.Count > 0,
            $"the closed field's paper draws over the cabin painting ({shown.Backdrop.Count} backdrop, {shown.Fills.Count} fills)");

        Click(host, seat, Pointer(fit, field.X + 5f, field.Y + 5f, pressed: true, clicked: true));
        var entry = Row(shell, "ENTRY:" + PickedRow.ToString(CultureInfo.InvariantCulture));
        ctx.Check(Row(shell, "ENTRY:0") != null && entry != null,
            $"a press on the field opens its list of 24 missions ({shell.Rows.Count} rows)");
        if (entry == null)
        {
            return;
        }

        Click(host, seat, Pointer(fit, entry.X + 5f, entry.Y + 5f, pressed: true, clicked: true));
        ctx.Check(Row(shell, "ENTRY:0") == null && campaign.Cheats.MissionPick == PickedRow + 1,
            $"a press on a row closes the list on that mission ({campaign.Cheats.MissionPick})");

        var next = Row(shell, nameof(BoardButton.NextMission));
        if (next == null)
        {
            ctx.Check(false, $"the cabin carries NEXT MISSION");
            return;
        }

        Click(host, seat, Pointer(fit, next.X + 5f, next.Y + 5f, pressed: true, clicked: true));
        ctx.Check(shell.Screen == OriginalScreen.CampaignBriefing && campaign.MissionSeq == PickedRow,
            $"NEXT MISSION launches the picked mission rather than the campaign's own ({shell.Screen}, seq {campaign.MissionSeq})");

        ReturnToCabin(ctx, host, seat, shell, fit);
        next = Row(shell, nameof(BoardButton.NextMission));
        if (shell.Screen != OriginalScreen.CampaignCabin || next == null)
        {
            return;
        }

        Click(host, seat, Pointer(fit, next.X + 5f, next.Y + 5f, pressed: true, clicked: true));
        ctx.Check(shell.Screen == OriginalScreen.CampaignBriefing && campaign.MissionSeq == 0,
            $"and the press after it is the ordinary one again, the buffer having been read and emptied (seq {campaign.MissionSeq})");
        ReturnToCabin(ctx, host, seat, shell, fit);
    }

    // SCRAPBOOK_TOC.SCRIPT's word: the whole campaign in the contents list with nothing flown, and
    // the book's own position line over the career page.
    private static void Contents(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, BoardFit fit,
        CampaignFeature campaign)
    {
        var door = Row(shell, nameof(BoardButton.PreviousMissions));
        if (shell.Screen != OriginalScreen.CampaignCabin || door == null)
        {
            return;
        }

        Click(host, seat, Pointer(fit, door.X + 5f, door.Y + 5f, pressed: true, clicked: true));
        int before = shell.Rows.Count;
        ctx.Check(shell.Screen == OriginalScreen.CampaignPreviousMissions && !shell.TypingCheat,
            $"PREVIOUS MISSIONS opens the contents with no word armed ({shell.Screen}, {before} rows)");

        Press(host, seat, new MenuCommands { Typed = CampaignCheats.GalleryWord });
        ctx.Check(!campaign.Cheats.RevealAll, $"its word typed with the widget unarmed fires nothing");
        Click(host, seat, Pointer(fit, ContentsX, ContentsY, pressed: true, clicked: true));
        ctx.Check(shell.Screen == OriginalScreen.CampaignPreviousMissions && shell.TypingCheat,
            $"a left click inside its region takes the keyboard and opens nothing ({shell.Screen})");
        Press(host, seat, new MenuCommands { Typed = CampaignCheats.GalleryWord });
        ctx.Check(campaign.Cheats.RevealAll && shell.Rows.Count == before + CampaignCheats.RevealedMissions,
            $"the word puts every mission of the campaign in the list with nothing flown ({before} to {shell.Rows.Count} rows)");

        // The career row twice: the first press picks it and the second opens the book on it.
        var career = Row(shell, "ROW:0");
        if (career == null)
        {
            return;
        }

        Click(host, seat, Pointer(fit, career.X + 5f, career.Y + 5f, pressed: true, clicked: true));
        Click(host, seat, Pointer(fit, career.X + 5f, career.Y + 5f, pressed: true, clicked: true));
        ctx.Check(shell.Screen == OriginalScreen.CampaignScrapbook && HasLine(shell.Compose(), "mission: 0 spread: 1"),
            $"and the open gallery prints the book's own position line ({shell.Screen})");
        ReturnToCabin(ctx, host, seat, shell, fit);
    }

    // PLANECONSTRUCTION.SCRIPT's word, over the wallet the build is priced against: two grants and
    // the refusal at the script's own ceiling.
    private static void Hub(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, BoardFit fit,
        CampaignFeature campaign, CampaignProfileStore store)
    {
        var door = Row(shell, nameof(BoardButton.PlaneConstruction));
        if (shell.Screen != OriginalScreen.CampaignCabin || door == null)
        {
            return;
        }

        var hangar = host.Features.Get<HangarFeature>();
        Click(host, seat, Pointer(fit, door.X + 5f, door.Y + 5f, pressed: true, clicked: true));
        Press(host, seat, new MenuCommands { Typed = Built });
        var ok = Row(shell, OriginalHangarScreen.NameOkKey);
        if (ok == null)
        {
            ctx.Check(false, $"the name screen carries its OK button");
            return;
        }

        Click(host, seat, Pointer(fit, ok.X + 5f, ok.Y + 5f, pressed: true, clicked: true));
        ctx.Check(shell.Screen == OriginalScreen.HangarAirframe && hangar.Wallet?.Funds == 0,
            $"the hub opens over the profile's own empty wallet ({shell.Screen}, {hangar.Wallet?.Funds})");

        Press(host, seat, new MenuCommands { Typed = CampaignCheats.CashWord });
        ctx.Check(hangar.Wallet?.Funds == 0, $"its word typed with the widget unarmed grants nothing ({hangar.Wallet?.Funds})");
        Click(host, seat, Pointer(fit, HubX, HubY, pressed: true, clicked: true));
        ctx.Check(shell.Screen == OriginalScreen.HangarAirframe && shell.TypingCheat && host.Seats[0].CapturingText,
            $"a left click on the cash figure takes the keyboard and presses nothing ({shell.Screen})");

        Press(host, seat, new MenuCommands { Typed = CampaignCheats.CashWord });
        ctx.Same(CampaignCheats.CashGrant, hangar.Wallet?.Funds ?? -1, $"the word grants the script's own sum");
        Press(host, seat, new MenuCommands { Typed = CampaignCheats.CashWord });
        ctx.Same(CampaignCheats.CashGrant, hangar.Wallet?.Funds ?? -1,
            $"typed straight again it grants nothing, the completed word still standing in the buffer");
        Press(host, seat, new MenuCommands { Typed = "x" + CampaignCheats.CashWord });
        ctx.Same(CampaignCheats.CashCeiling, hangar.Wallet?.Funds ?? -1,
            $"a character that empties the buffer first lets the word grant again");
        Press(host, seat, new MenuCommands { Typed = "x" + CampaignCheats.CashWord });
        ctx.Check(hangar.Wallet?.Funds == CampaignCheats.CashCeiling && store.Load(Pilot)?.Funds == CampaignCheats.CashCeiling,
            $"a third grant is refused at the ceiling, and the profile on disk carries the two ({hangar.Wallet?.Funds}, {store.Load(Pilot)?.Funds})");

        Press(host, seat, Back);
        ctx.Check(shell.Screen == OriginalScreen.CampaignCabin && !campaign.Cheats.AllowAll,
            $"Back cancels the build and resumes the cabin with no airframe unlocked ({shell.Screen})");
    }

    // CAMPAIGN.SCRIPT's hidden pilot name, typed into the profile screen's own box.
    private static void UnlockingName(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, BoardFit fit,
        CampaignFeature campaign, CampaignProfileStore store)
    {
        var door = Row(shell, nameof(BoardButton.ReturnToMainMenu));
        if (shell.Screen != OriginalScreen.CampaignCabin || door == null)
        {
            return;
        }

        Click(host, seat, Pointer(fit, door.X + 5f, door.Y + 5f, pressed: true, clicked: true));
        var campaignRow = Row(shell, OriginalShell.CampaignKey);
        if (campaignRow == null)
        {
            return;
        }

        Click(host, seat, Pointer(fit, campaignRow.X + 5f, campaignRow.Y + 5f, pressed: true, clicked: true));
        ctx.Check(shell.Screen == OriginalScreen.CampaignRoster && shell.Campaign.RosterName == Pilot,
            $"the profile screen opens with the remembered player in the box ({shell.Screen}, {shell.Campaign.RosterName})");

        for (int i = Pilot.Length; i > 0; i--)
        {
            Press(host, seat, new MenuCommands { Erase = true });
        }

        Press(host, seat, new MenuCommands { Typed = CampaignCheats.UnlockName });
        ctx.Check(shell.Campaign.RosterName == CampaignCheats.UnlockName,
            $"the box takes the name's own exclamation mark, which the campaign's name rule refuses ({shell.Campaign.RosterName})");

        var start = Row(shell, nameof(BoardButton.Continue));
        if (start == null)
        {
            ctx.Check(false, $"the profile screen carries CONTINUE");
            return;
        }

        Click(host, seat, Pointer(fit, start.X + 5f, start.Y + 5f, pressed: true, clicked: true));
        ctx.Check(shell.Screen == OriginalScreen.CampaignRoster && shell.Campaign.RosterName == Pilot,
            $"the name leaves the screen standing with the previous player back in the box ({shell.Screen}, {shell.Campaign.RosterName})");
        ctx.Check(campaign.Cheats.AllowAll && store.Load(CampaignCheats.UnlockName) == null,
            $"every airframe is offered for the rest of the session and no profile was created under the name");
        var profile = store.Load(Pilot);
        ctx.Same(CampaignCheats.UnlockFunds, profile?.Funds ?? -1, $"the player's wallet is the one literal sum in the image");
        ctx.Check(profile != null && profile.Planes.Count == 2 + HangarEconomy.Airframes.Length,
            $"and the eleven stock airframes stand beside the two starters ({profile?.Planes.Count})");
    }

    // RETURN TO CABIN wherever it is offered, the one door back this walk uses.
    private static void ReturnToCabin(TestContext ctx, MenuHost host, ScriptedSeat seat, OriginalShell shell, BoardFit fit)
    {
        var back = Row(shell, nameof(BoardButton.ReturnToCabin));
        if (back == null)
        {
            ctx.Check(false, $"the screen showing carries RETURN TO CABIN ({shell.Screen})");
            return;
        }

        Click(host, seat, Pointer(fit, back.X + 5f, back.Y + 5f, pressed: true, clicked: true));
        ctx.Check(shell.Screen == OriginalScreen.CampaignCabin, $"RETURN TO CABIN lands on the cabin ({shell.Screen})");
    }

    private static MenuCommands Pointer(BoardFit fit, float authoredX, float authoredY, bool pressed = false, bool clicked = false) =>
        new() { Pointer = new MenuPointer(fit.X(authoredX), fit.Y(authoredY), pressed, clicked) };

    // One click as the shell reads it: the press arms the row under the pointer and the release on
    // it fires, so a click is two frames rather than one.
    private static void Click(MenuHost host, ScriptedSeat seat, MenuCommands frame)
    {
        Press(host, seat, frame);
        Press(host, seat, frame with { Pointer = frame.Pointer!.Value with { Pressed = false, Clicked = false } });
    }

    private static void Press(MenuHost host, ScriptedSeat seat, MenuCommands frame)
    {
        seat.Enqueue(frame);
        host.Tick(Dt);
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

    private static bool HasArt(IReadOnlyList<BoardPicture> pictures, string name)
    {
        foreach (var picture in pictures)
        {
            if (string.Equals(picture.Art.Name, name, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

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

    // The screen's drop-down field, which on the cabin only the typed word puts there.
    private static OriginalRow? Dropdown(OriginalShell shell)
    {
        foreach (var row in shell.Rows)
        {
            if (row.Kind == OriginalRowKind.Dropdown)
            {
                return row;
            }
        }

        return null;
    }

    // A seat whose frames this suite writes, and which reads nothing of its own.
    private sealed class ScriptedSeat : IMenuInputSource
    {
        private readonly Queue<MenuCommands> _frames = new();

        public string DeviceLabel => "scripted";

        public bool CapturingText { get; set; }

        public void Enqueue(MenuCommands frame) => _frames.Enqueue(frame);

        public MenuCommands Poll(float dt) => _frames.Count > 0 ? _frames.Dequeue() : MenuCommands.None;

        public void Prime()
        {
        }
    }
}
