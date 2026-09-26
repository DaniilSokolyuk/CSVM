using System;
using System.Collections.Generic;
using CSVM.UI.Boards;
using CSVM.UI.Campaign;

namespace CSVM.UI.Menu.Original;

/// <summary>One answer of a dialog standing over a screen. It carries its row key, the messagebox
/// button row it draws at, its words, and what answering it runs.</summary>
public sealed record OriginalDialogAnswer(string Key, string LayoutKey, string Label, Action? Run);

/// <summary>A dialog standing over a screen, the original's <c>messagebox.script</c> over whatever
/// screen is showing. It carries its words, the icon its message class draws, and its one, two or
/// three answers. It also carries the widget set it is drawn from, null for the shared <c>mb_</c>
/// box. While one stands the rows are its answers alone.</summary>
public sealed record OriginalDialog(
    string Message, DialogIcon Icon, IReadOnlyList<OriginalDialogAnswer> Answers,
    CampaignBoards.DialogChrome? Chrome = null);

/// <summary>
/// The standing dialog, the shell's own partial rather than any screen family's: the original's
/// <c>messagebox.script</c> over whatever screen is showing. The campaign and hangar modules raise
/// it through <see cref="IOriginalScreenHost.RaiseDialog"/>; the credits screen's About box uses
/// its own widget set. The shell owns it because the shell answers for it: while a box stands its
/// answers are the only rows. <c>Compose</c> draws it over the screen's own picture, an Activate on
/// one of its rows is the answer, and Back takes the declining one. The answers' words come from
/// the string table whichever feature carries one (langui 100 to 103). The focus the raise took is
/// put back when the box is answered.
/// </summary>
public sealed partial class OriginalShell
{
    /// <summary>A one-answer dialog's OK.</summary>
    public const string DialogOkKey = "DIALOG:OK";

    /// <summary>A two-answer dialog's confirming answer.</summary>
    public const string DialogYesKey = "DIALOG:YES";

    /// <summary>A two-answer dialog's declining answer.</summary>
    public const string DialogNoKey = "DIALOG:NO";

    /// <summary>A three-answer dialog's third answer, which leaves the screen as it was.</summary>
    public const string DialogCancelKey = "DIALOG:CANCEL";

    // How far outside an answer's own rectangle its focus mark stands. The strip fills its frame
    // corner to corner but for the pill's rounded ends. A mark on the rectangle itself would be
    // drawn under the art and lost. Three pixels clear puts it on the box's black ground.
    private const float DialogMarkOutset = 3f;

    private OriginalDialog? _dialog;
    private int _focusBeforeDialog = -1;

    /// <summary>The dialog standing over the screen, or null.</summary>
    public OriginalDialog? Dialog => _dialog;

    // The messagebox script's own answer words, read through whichever feature carries the string
    // table. The one-button box takes langui 100 (OK), the two-button pair 102 and 103 (Yes, No).
    // The 0x8 box takes 102, 103 and 101 across all three slots.
    private OriginalDialogAnswer Ok(Action? run = null) =>
        new(DialogOkKey, CampaignBoards.DialogCenterKey, DialogWord(100, "OK"), run);

    private OriginalDialogAnswer Yes(Action run) =>
        new(DialogYesKey, CampaignBoards.DialogLeftKey, DialogWord(102, "Yes"), run);

    private OriginalDialogAnswer No() =>
        new(DialogNoKey, CampaignBoards.DialogRightKey, DialogWord(103, "No"), null);

    // The 0x8 box's own two extra answers. Its No moves onto the centre slot the two-button box
    // leaves empty, and Cancel takes the right one (MESSAGEBOX.SCRIPT's gui_init).
    private OriginalDialogAnswer NoCentred(Action run) =>
        new(DialogNoKey, CampaignBoards.DialogCenterKey, DialogWord(103, "No"), run);

    private OriginalDialogAnswer Cancel(Action run) =>
        new(DialogCancelKey, CampaignBoards.DialogRightKey, DialogWord(101, "Cancel"), run);

    private string DialogWord(int id, string fallback)
    {
        string word = MenuStrings.Text(id, fallback);
        return word.Length > 0 ? word : fallback;
    }

    // A standing dialog's answers, at the messagebox rows they draw on, whatever screen it stands over.
    private List<OriginalRow> DialogRows()
    {
        var rows = new List<OriginalRow>();
        if (_dialog is not { } dialog)
        {
            return rows;
        }

        foreach (var answer in dialog.Answers)
        {
            var (art, x, y) = CampaignBoards.DialogSlot(answer.LayoutKey, _campaignLayout, dialog.Chrome);
            var size = OriginalWidgets.PlaqueSizeOf(art, Measure);
            rows.Add(new OriginalRow(answer.Key, answer.Label, OriginalRowKind.Button, x, y, size.Width, size.Height, true, 0, art));
        }

        return rows;
    }

    // The standing dialog as the shared board component's messagebox panel.
    // ⚠ Do not take the strip frame off the focus. Every raise focuses an answer, so a frame read
    // from it would stand on the default answer for the life of the box. That is not what the
    // original draws. The pointer owns the rollover frame and the cursor gets the focus mark
    // instead (docs/org/campaign-board.md). The ink is the box's own rather than the screen's,
    // which on a paper screen would hide the label on the dark strip.
    private void ComposeDialog(IReadOnlyList<OriginalRow> rows, int focus, List<BoardPanel> overlays)
    {
        var dialog = _dialog!;
        var buttons = new List<CampaignBoards.DialogButton>(dialog.Answers.Count);
        var marks = new List<BoardFill>();
        for (int i = 0; i < dialog.Answers.Count; i++)
        {
            // The hit is re-checked rather than trusted. The hover index outlives the frame that
            // set it, and the answers are not the rows it was measured against.
            var row = i < rows.Count ? rows[i] : null;
            bool lit = i == _hover && row != null && _pointer is { } at && row.Contains(at.X, at.Y);
            bool held = i == _pressed;
            if (i == focus && !lit && row != null)
            {
                marks.Add(FocusBox(row, DialogMarkOutset));
            }

            buttons.Add(new CampaignBoards.DialogButton(
                dialog.Answers[i].LayoutKey, dialog.Answers[i].Label,
                ComposedBoard.PlaqueFrame(4, lit, held), ComposedBoard.DialogInk(held)));
        }

        overlays.Add(CampaignBoards.Dialog(dialog.Message, buttons, dialog.Icon, _campaignLayout, dialog.Chrome));
        if (marks.Count > 0)
        {
            // The mark rides its own panel over the box rather than the box's fill layer. A panel
            // draws its fills before its pictures, and the messagebox's background covers the
            // whole panel. A mark inside it would be drawn and then painted over.
            overlays.Add(new BoardPanel(marks, Array.Empty<BoardPicture>(), Array.Empty<BoardLine>()));
        }
    }

    // Every raise names its icon, because MESSAGEBOX.SCRIPT reads the frame off the raising
    // screen's button mask rather than off anything the box itself can see. A default here would
    // be a rule of "one button means the warning", which the original's 0x2 boxes break.
    private void RaiseDialog(string message, DialogIcon icon, params OriginalDialogAnswer[] answers) =>
        RaiseDialog(null, message, icon, answers);

    // The same raise in another widget set, which the credits screen's About box is drawn from.
    private void RaiseDialog(
        CampaignBoards.DialogChrome? chrome, string message, DialogIcon icon, params OriginalDialogAnswer[] answers)
    {
        _focusBeforeDialog = _focus[(int)_screen];
        _dialog = new OriginalDialog(message, icon, answers, chrome);
        _hover = -1;
        _pressed = -1;
        _armed = null;
        // A box opens on its first answer, the left button MESSAGEBOX.SCRIPT focuses for the plain
        // 0x4 and 0x8 masks. Back still takes the last one, which is the answer the script's own
        // Escape posts for every mask, so a mistake has a way out.
        _focus[(int)_screen] = 0;
    }

    private void AnswerDialog(string key)
    {
        if (_dialog is not { } dialog)
        {
            return;
        }

        _dialog = null;
        _focus[(int)_screen] = _focusBeforeDialog;
        foreach (var answer in dialog.Answers)
        {
            if (answer.Key == key)
            {
                answer.Run?.Invoke();
                return;
            }
        }
    }
}
