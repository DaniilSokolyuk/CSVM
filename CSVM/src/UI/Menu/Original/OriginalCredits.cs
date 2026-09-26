using System;
using System.Collections.Generic;
using System.Text;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;

namespace CSVM.UI.Menu.Original;

/// <summary>
/// The credits screen, the shell's partial over the decoded <c>[@Credits@]</c> section behind the
/// top level's fifth row. The section is three widgets and nothing more: a full-screen background
/// pane, ABOUT and the DONE plaque. The credit names are painted into the background art, so the
/// screen lays out no roster of its own and the pane is the whole composition; the two buttons are
/// drawn over it by the shell's row loop. DONE and Escape both land on the top level, which is the
/// plaque's own <c>ScriptToExe</c> and what <c>CREDITS.SCRIPT</c>'s <c>gui_char</c> does. ABOUT
/// raises the About box, and the script's own hidden line answers a held secondary button.
/// The screen and its parts: <c>docs/org/menu-inventory.md</c>.
/// </summary>
public sealed partial class OriginalShell
{
    /// <summary>The layout section the screen is composed from.</summary>
    public const string CreditsSection = "Credits";

    /// <summary>The top level's door onto it.</summary>
    public const string CreditsDoorKey = "MM_B_CREDITS";

    /// <summary>ABOUT, which raises the About box.</summary>
    public const string CreditsAboutKey = "CR_B_About";

    /// <summary>The DONE plaque, back to the top level.</summary>
    public const string CreditsExitKey = "CR_B_Exit";

    // The pane the credit names are painted into. It wears no art of its own beyond the section's
    // row, so a layout that renames the file moves ours with it.
    private const string CreditsBackgroundKey = "CR_BackGround";

    // The row naming the About box's background, the shipped file it names, and that file's own
    // size as the fallback when it cannot be measured. The script centres the pane by whatever
    // size it reads back, so the measured file wins over both.
    private const string AboutBackgroundKey = "MA_P_BACKGROUND";
    private const string AboutBoxArt = "CR_AboutMessageBox.png";
    private const float AboutBoxWidth = 505f;
    private const float AboutBoxHeight = 416f;

    // The widget set the box is drawn from, which CREDITS.SCRIPT selects by setting @globals@OR.XR
    // before it runs messagebox.script.
    private const string AboutPrefix = "MA";

    // The box's words: langui 1301 over the product identification number. The original reads that
    // number out of the installer's registry key and falls back to this when the key is absent.
    // This port never reads that key, by decision: the fallback is faithful on every machine and
    // a registry dependency is not worth the original installer's id, so do not add one.
    private const int AboutStringId = 1301;
    private const string AboutProductId = "???";

    // The hidden line's authored corner and the region the secondary button has to be held inside,
    // both CREDITS.SCRIPT's own; its test is exclusive on all four edges. The face is the layout's
    // standard text height, the widget carrying no size of its own.
    private const float SecretX = 288f;
    private const float SecretY = 308f;
    private const float SecretLeft = 287f;
    private const float SecretRight = 353f;
    private const float SecretTop = 313f;
    private const float SecretBottom = 333f;
    private const float SecretFont = 16f;

    // The line as the script stores it, and the shift its own loop undoes character by character.
    // ⚠ Do not write the decoded words here; obfuscating them is the whole of the joke.
    private const string SecretCipher = "xl#ghy#ohdg=#ulfk#hl}hqkrhihu";
    private const int SecretShift = 3;

    // Whether the hidden line stands, and the secondary button's state last frame, since the
    // script acts on that button's transitions and not on the state itself.
    private bool _secretShown;
    private bool _secretHeld;

    // Each character of the stored line shifted back down, which is the loop CREDITS.SCRIPT runs
    // on the mail it sends itself at create.
    private static string SecretLine()
    {
        var text = new StringBuilder(SecretCipher.Length);
        foreach (char c in SecretCipher)
        {
            text.Append((char)(c - SecretShift));
        }

        return text.ToString();
    }

    private void BuildCreditsRows(List<OriginalRow> rows)
    {
        var screen = _layout.Screen(CreditsSection);
        if (screen?.Widget(CreditsAboutKey) is { } about)
        {
            rows.Add(Button(about, true));
        }

        if (screen?.Widget(CreditsExitKey) is { } exit)
        {
            rows.Add(Button(exit, true));
        }
    }

    private void ComposeCredits(BoardLayers layers)
    {
        if (_layout.Screen(CreditsSection)?.Widget(CreditsBackgroundKey) is { Art.Count: > 0 } pane)
        {
            layers.Pictures.Add(new BoardPicture(
                new BoardArt(BoardArtLibrary.Ui, pane.Art[0], Math.Max(1, pane.Frames)),
                pane.Int("X"), pane.Int("Y")));
        }

        if (_secretShown)
        {
            layers.Lines.Add(new BoardLine(SecretLine(), SecretX, SecretY, 0f, SecretFont, BoardInk.Secret));
        }
    }

    // DONE lands on the top level and ABOUT raises the box. Escape needs no arm because the shell's
    // own Back falls through to the top level.
    private MenuExit? ActivateCredits(OriginalRow row)
    {
        if (row.Key == CreditsExitKey)
        {
            Open(OriginalScreen.TopLevel);
        }
        else if (row.Key == CreditsAboutKey)
        {
            RaiseDialog(AboutChrome(), AboutMessage(), DialogIcon.Death, Ok());
        }

        return null;
    }

    // The screen's own rbutton_update: the secondary button's transitions count only while the
    // pointer stands inside the authored region, so a hold carried out of it before the button
    // comes up leaves the line standing, which is what the script does.
    private bool HoldCreditsSecret(MenuPointer pointer)
    {
        bool held = pointer.RightPressed;
        bool edge = held != _secretHeld;
        _secretHeld = held;
        bool inside = pointer.X > SecretLeft && pointer.X < SecretRight
            && pointer.Y > SecretTop && pointer.Y < SecretBottom;
        if (!edge || !inside || _secretShown == held)
        {
            return false;
        }

        _secretShown = held;
        return true;
    }

    // The line goes with the screen: the script creates its widget deactivated every time.
    private void ResetCreditsSecret()
    {
        _secretShown = false;
        _secretHeld = false;
    }

    // Where the About box's pane lands, which messagebox.script computes from the pane's own size:
    // half the board less half the art, floored by its integer division.
    private CampaignBoards.DialogChrome AboutChrome()
    {
        var row = _layout.Screen(CampaignLayout.DialogSection)?.Widget(AboutBackgroundKey);
        string art = row is { Art.Count: > 0 } ? row.Art[0] : AboutBoxArt;
        var size = Measure(art);
        float width = size?.Width ?? AboutBoxWidth;
        float height = size?.Height ?? AboutBoxHeight;
        return new CampaignBoards.DialogChrome(
            AboutPrefix,
            MathF.Floor((BoardFit.AuthoredWidth - width) / 2f),
            MathF.Floor((BoardFit.AuthoredHeight - height) / 2f),
            art);
    }

    // langui 1301 filled with the product id. Its one placeholder is wrapped in a <B>/<b> pair,
    // a bold run nothing strips centrally, so the row is cleaned here.
    private string AboutMessage()
    {
        var strings = Campaign.Strings ?? Hangar?.Strings;
        string text = strings?.Format(AboutStringId, AboutProductId) ?? string.Empty;
        return text.Replace("<B>", string.Empty).Replace("<b>", string.Empty);
    }
}
