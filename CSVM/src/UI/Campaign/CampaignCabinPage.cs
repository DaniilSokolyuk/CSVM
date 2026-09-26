using System;
using System.Collections.Generic;
using System.IO;
using CSVM.Mech3;
using CSVM.Session.Campaign;
using CSVM.UI.Boards;
using CSVM.UI.Hangar;

namespace CSVM.UI.Campaign;

/// <summary>
/// The cabin hub (<c>Campaign Cabin.png</c>, <c>PASSENGERCABIN.SCRIPT</c>): NEXT MISSION,
/// PREVIOUS MISSIONS, PLANE CONSTRUCTION, RETURN TO MAIN MENU and CHANGE MEMENTO. SAVE GAME is the
/// button the original creates and deactivates, and is never drawn. Judged at the controls, the
/// screen has no idle behaviour, only background music over static art, so this page draws once
/// and does nothing between presses.
/// </summary>
public sealed class CampaignCabinPage : CampaignPage
{
    /// <summary>PLANE CONSTRUCTION's row, the one the shell has to reach by name: it is the press
    /// that leaves this flow standing while the hangar runs over the profile's wallet.</summary>
    public const int PlaneConstructionRow = 2;

    // PC_D_MISSIONS, the dropdown PASSENGERCABIN.SCRIPT creates deactivated and the cabin's typed
    // word activates: LAYOUT.CSV's own X, Y, width, item height and 24 displayed rows.
    private const float MissionListX = 300f;
    private const float MissionListY = 20f;
    private const float MissionListWidth = 475f;
    private const float MissionListRowHeight = 21f;
    private const int MissionListRows = 24;

    // langui 3450 + row, the long mission names uiData 2038 answers the list with.
    private const int MissionNameId = 3450;

    // The disabled-Next-Mission reason. Not a decoded langui id: docs/formats/strings.md's
    // 1200-1219 mission-results block has no "campaign finished" row, and uiData 2600 (the
    // original's own gate) is a boolean with no accompanying message string.
    private const string FinishedReason = "Every mission in the campaign has been completed.";

    // The memento's window inside PC_Mementopicframe, chosen rather than read: it is the block each
    // hangar photograph keys out for it, where the layout's PC_MEMENTO pane sits at 169,325 with
    // no size. The frame's own border covers the edges either way.
    private const int MementoX = 179;
    private const int MementoY = 330;
    private const int MementoWidth = 73;
    private const int MementoHeight = 84;

    // The five buttons, in the original's own creation order (docs/formats/campaign-screens.md,
    // "The cabin"). SAVE GAME is deactivated there and never drawn here.
    private static readonly string[] Rows =
    {
        "Next Mission",
        "Previous Missions",
        "Plane Construction",
        "Return to Main Menu",
        "Change Memento",
    };

    // The cabin scene, decoded on first sight and kept: one 800x600 PNG per session, and a miss
    // must not re-probe the disk every frame.
    private HangarArt? _scene;
    private bool _sceneProbed;

    // The cheat's mission pull-down, built the first time the word is typed and kept afterwards.
    private CampaignCombo? _missions;

    /// <summary>Binds the page to its flow.</summary>
    public CampaignCabinPage(CampaignFlow flow)
        : base(flow)
    {
    }

    /// <inheritdoc/>
    public override CampaignScreen Screen => CampaignScreen.Cabin;

    /// <inheritdoc/>
    public override string Title => "CAMPAIGN CABIN";

    /// <summary>The five buttons, and the mission pull-down after them once the cabin's typed word
    /// has shown it.</summary>
    public override int RowCount => Rows.Length + (MissionList() == null ? 0 : 1);

    /// <inheritdoc/>
    /// <remarks>The cabin scene, <c>PC_BackGround.png</c>, through <see cref="PngImage"/>. The
    /// pilot's own aircraft is not read here: the campaign screens render as composed boards, so
    /// <see cref="Pictures"/> is what draws the photograph and this stays the one flat picture the
    /// page can hand a caller that wants pixels.</remarks>
    public override HangarArt? Art => CabinScene();

    /// <summary>The scene as the original layers it: the pilot's own aircraft first, then the
    /// painted cabin over it, whose colour-keyed hole is where the window is. The photograph ships
    /// as JPG, which the shell's own loader reads. The panes' positions and the two fixed bitmaps
    /// are <c>[@PassengerCabin@]</c>'s <c>PC_PLANE</c>, <c>PC_BACKGROUND</c> and <c>PC_FRAME</c>.</summary>
    public override IReadOnlyList<BoardPicture> Pictures
    {
        get
        {
            var layout = Flow.Layout;
            const string section = CampaignLayout.CabinSection;
            var pictures = new List<BoardPicture>(4);
            if (PlaneArtName() is { } photo)
            {
                var (planeX, planeY) = layout.At(section, "PC_PLANE", 46, 69);
                pictures.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, photo), planeX, planeY));
            }

            // The memento, over the hangar photograph's own keyed-out block for it and under the
            // painting: whichever picture the profile hangs, which the chooser writes.
            pictures.Add(new BoardPicture(
                new BoardArt(BoardArtLibrary.Rimage, CampaignMementos.Bitmap(CampaignMementos.Current(Flow.Profile))),
                MementoX, MementoY, 0, false, 1f, 0f, MementoWidth, MementoHeight));
            var (backX, backY) = layout.At(section, "PC_BACKGROUND", 0, 0);
            pictures.Add(new BoardPicture(
                layout.Art(section, "PC_BACKGROUND", new BoardArt(BoardArtLibrary.Ui, "PC_BackGround.png")), backX, backY));

            // The frame is painted over the painting rather than seen through its hole, which is
            // the one element the layout puts above the background.
            var (frameX, frameY) = layout.At(section, "PC_FRAME", 169, 317);
            pictures.Add(new BoardPicture(
                layout.Art(section, "PC_FRAME", new BoardArt(BoardArtLibrary.Ui, "PC_Mementopicframe.png")), frameX, frameY));
            return pictures;
        }
    }

    /// <summary>The map's pin count: one per story chapter reached, the story chapter of the
    /// campaign's current position (<c>gosCallback</c> 7 with -2, docs/formats/campaign-screens.md,
    /// "The cabin"). The count is engine-free and tested; drawing the pins needs their authored
    /// positions on the map, which nothing decodes yet. <c>PC_mappins.png</c> itself is an ordinary
    /// 50x100 RGBA sheet the art seam reads.</summary>
    public static int MapPinCount(CampaignProfileDef profile) =>
        (CampaignProgression.NextMissionSeq(profile) / 5) + 1;

    /// <summary>The cheat's pull-down, or null while the word has not been typed. Its pick is the
    /// mission NEXT MISSION launches in place of the campaign's own position.</summary>
    public override CampaignCombo? Combo(int row) => row == Rows.Length ? MissionList() : null;

    /// <inheritdoc/>
    public override BoardButtonRef Button(int row) => row switch
    {
        0 => new BoardButtonRef(BoardButton.NextMission),
        1 => new BoardButtonRef(BoardButton.PreviousMissions),
        PlaneConstructionRow => new BoardButtonRef(BoardButton.PlaneConstruction),
        3 => new BoardButtonRef(BoardButton.ReturnToMainMenu),
        4 => new BoardButtonRef(BoardButton.ChangeMemento),
        _ => BoardButtonRef.None,
    };

    /// <inheritdoc/>
    public override string RowText(int row) => row < Rows.Length ? Rows[row] : string.Empty;

    /// <inheritdoc/>
    public override string Detail(int row)
    {
        var profile = Flow.Profile;
        if (profile == null)
        {
            return "No player is selected";
        }

        return row switch
        {
            _ when row == Rows.Length => "Chooses the mission NEXT MISSION launches",
            0 when CampaignProgression.Complete(profile) => FinishedReason,
            0 => "Opens the briefing for the next mission",
            1 => CampaignProgression.CompletedSeqs(profile).Count == 0
                ? "No missions finished yet"
                : "Review or replay a finished mission",
            2 => "Buy, sell and fit aircraft",
            4 => "Choose the picture on the cabin wall",
            _ => "Leaves the campaign for the main menu",
        };
    }

    /// <inheritdoc/>
    public override bool Accept(int row)
    {
        var profile = Flow.Profile;
        if (profile == null)
        {
            Flow.Cancel();
            return true;
        }

        if (row == Rows.Length && MissionList() is { } missions)
        {
            return PickMission(missions);
        }

        switch (row)
        {
            case 0:
                if (CampaignProgression.Complete(profile))
                {
                    Flow.SetMessage(FinishedReason);
                    return true;
                }

                Flow.SetMission(CampaignProgression.NextMissionSeq(profile));
                Flow.GoTo(CampaignScreen.Briefing);
                return true;
            case 1:
                Flow.GoTo(CampaignScreen.PreviousMissions);
                return true;
            case PlaneConstructionRow:
                // The shell owns opening the hangar over the profile's wallet and calling
                // Flow.Resume when it closes.
                Flow.Request(CampaignExit.OpenHangar);
                return true;
            case 4:
                Flow.GoTo(CampaignScreen.MementoSelection);
                return true;
            default:
                Flow.Cancel();
                return true;
        }
    }

    // The cheat's pull-down, or null while the cabin's word has not been typed: the 24 long mission
    // names, opened on the campaign's own position with 24 clamped to the last row, which is what
    // the script's QMA.QG assignment does. Built once and kept, so a pick survives a redraw.
    private CampaignCombo? MissionList()
    {
        if (!Flow.Cheats.MissionListShown)
        {
            return null;
        }

        if (_missions != null)
        {
            return _missions;
        }

        var names = new List<string>(MissionListRows);
        for (int row = 0; row < MissionListRows; row++)
        {
            names.Add(Flow.Strings.Text(MissionNameId + row, $"Mission {row + 1}"));
        }

        _missions = new CampaignCombo(
            MissionListX, MissionListY, MissionListWidth, MissionListRowHeight, MissionListRows);
        int seq = Flow.Profile is { } profile ? CampaignProgression.NextMissionSeq(profile) : 0;
        _missions.Load(names, Math.Clamp(seq, 0, MissionListRows - 1));
        Flow.Cheats.PickMission(_missions.Selected + 1);
        return _missions;
    }

    // One press on the pull-down: the closed field opens, and an open one commits whatever the
    // cursor stands on as the mission NEXT MISSION will launch.
    private bool PickMission(CampaignCombo missions)
    {
        if (missions.Expand())
        {
            return true;
        }

        if (missions.Confirm() is { } at)
        {
            missions.Select(at);
            Flow.Cheats.PickMission(missions.Selected + 1);
        }

        return true;
    }

    // The cabin's own flat art, decoded once. The composition A6 describes (background, then the
    // window-hole photograph, the memento frame and the map pins over it) needs a compositor the
    // art column does not have, so the background stands for the scene and MapPinCount stands for
    // the pins.
    private HangarArt? CabinScene()
    {
        if (_sceneProbed)
        {
            return _scene;
        }

        _sceneProbed = true;
        if (Flow.DataRoot is { } root)
        {
            var path = Path.Combine(root, "extracted", "rof", "ASSETS", "GRAPHICS", "PC_BACKGROUND.PNG");
            _scene = PngImage.TryLoad(path) is { } image
                ? new HangarArt(image, Flow.Profile?.Name ?? "Cabin")
                : null;
        }

        return _scene;
    }

    // The hangar photograph's own filename for the profile's selected aircraft, or null where the
    // profile owns nothing to photograph.
    private string? PlaneArtName()
    {
        var profile = Flow.Profile;
        if (profile == null || profile.Planes.Count == 0)
        {
            return null;
        }

        int index = Math.Clamp(profile.SelectedPlane, 0, profile.Planes.Count - 1);
        return $"PC_P_HANGAR{profile.Planes[index].Airframe}.JPG";
    }

}
