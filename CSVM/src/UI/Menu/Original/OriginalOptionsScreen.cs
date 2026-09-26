using System;
using System.Collections.Generic;
using System.Globalization;
using CSVM.Bindings;
using CSVM.UI.Boards;

namespace CSVM.UI.Menu.Original;

/// <summary>
/// The five pages behind the Options hub's doors, one standalone module over the decoded
/// <c>[@GameOptions@]</c>, <c>[@Audio@]</c>, <c>[@Video@]</c>, <c>[@ControlsPrefs@]</c> and
/// <c>[@Keys@]</c> sections. They are one module because they are one form, read from the saved
/// options once per entry. Every page's ACCEPT CHANGES leaves as the same apply exit, carrying
/// every setting, the ones it does not show included. That keeps
/// <see cref="CSVM.Utils.OptionsStore"/>'s one writer the only writer. The rebinding pages stage
/// their edits in the shared <see cref="ControlsFeature"/> instead, so their CANCEL CHANGES drops a
/// whole visit, and the hub itself stays the shell's. What each page draws and the decode behind it
/// are in <c>docs/architecture/UI.md</c> and <c>docs/org/menu-inventory.md</c>.
/// </summary>
public sealed class OriginalOptionsScreen : IOriginalScreenModule
{
    /// <summary>The Game Options page's layout section.</summary>
    public const string GameOptionsSection = "GameOptions";

    /// <summary>The Options hub's door onto the Game Options page.</summary>
    public const string GameOptionsDoorKey = "PF_B_GAMEOPTIONS";

    /// <summary>ACCEPT CHANGES, which leaves as the options apply carrying every choice.</summary>
    public const string GameOptionsAcceptKey = "GO_B_ACCEPTCHANGES";

    /// <summary>CANCEL CHANGES, back to the hub with the choices dropped.</summary>
    public const string GameOptionsCancelKey = "GO_B_CANCELCHANGES";

    /// <summary>The Game Options page's difficulty dropdown, the page's first row.</summary>
    public const string DifficultyKey = "DIFFICULTY";

    /// <summary>The Game Options page's Default View dropdown, the original's own second row.</summary>
    public const string DefaultViewKey = "DEFAULTVIEW";

    /// <summary>The Game Options page's Auto Head Turn checkbox, the original's own third row.</summary>
    public const string AutoHeadTurnKey = "AUTOHEADTURN";

    /// <summary>The Game Options page's next-target checkbox.</summary>
    public const string NearestAfterKillKey = "NEARESTAFTERKILL";

    /// <summary>The Game Options page's controller-rumble checkbox.</summary>
    public const string RumbleKey = "RUMBLE";

    /// <summary>The AUDIO page's layout section.</summary>
    public const string AudioSection = "Audio";

    /// <summary>The Options hub's door onto the AUDIO page.</summary>
    public const string AudioDoorKey = "PF_B_AUDIO";

    /// <summary>ACCEPT CHANGES, which leaves as the options apply carrying every choice.</summary>
    public const string AudioAcceptKey = "AP_B_ACCEPTCHANGES";

    /// <summary>CANCEL CHANGES, back to the hub with the levels dropped.</summary>
    public const string AudioCancelKey = "AP_B_CANCELCHANGES";

    /// <summary>The AUDIO page's Master slider, the page's first row.</summary>
    public const string AudioMasterKey = "AUDIOMASTER";

    /// <summary>The AUDIO page's Music Volume slider.</summary>
    public const string AudioMusicKey = "AUDIOMUSIC";

    /// <summary>The AUDIO page's Effects Volume slider.</summary>
    public const string AudioEffectsKey = "AUDIOEFFECTS";

    /// <summary>The AUDIO page's Voice Volume slider.</summary>
    public const string AudioVoiceKey = "AUDIOVOICE";

    /// <summary>The VIDEO page's layout section.</summary>
    public const string VideoSection = "Video";

    /// <summary>The Options hub's door onto the VIDEO page.</summary>
    public const string VideoDoorKey = "PF_B_VIDEO";

    /// <summary>ACCEPT CHANGES, which leaves as the options apply carrying every choice.</summary>
    public const string VideoAcceptKey = "VP_B_ACCEPTCHANGES";

    /// <summary>CANCEL CHANGES, back to the hub with the choices dropped.</summary>
    public const string VideoCancelKey = "VP_B_CANCELCHANGES";

    /// <summary>The VIDEO page's monitor dropdown, the page's first row.</summary>
    public const string MonitorKey = "MONITOR";

    /// <summary>The VIDEO page's resolution dropdown.</summary>
    public const string ResolutionKey = "RESOLUTION";

    /// <summary>The VIDEO page's display-mode dropdown.</summary>
    public const string DisplayModeKey = "DISPLAYMODE";

    /// <summary>The VIDEO page's V-Sync dropdown.</summary>
    public const string VSyncKey = "VSYNC";

    /// <summary>The VIDEO page's enhanced-graphics checkbox.</summary>
    public const string GraphicsKey = "GRAPHICS";

    /// <summary>The Options hub's fourth door, onto the CONTROLS page.</summary>
    public const string ControlsDoorKey = "PF_B_CONTROLS";

    /// <summary>The CONTROLS page's layout section.</summary>
    public const string ControlsPrefsSection = "ControlsPrefs";

    /// <summary>The KEYS AND BUTTONS page's layout section.</summary>
    public const string KeysSection = "Keys";

    /// <summary>The CONTROLS page's seat chooser, which takes the Controller Type row.</summary>
    public const string ControlsPlayerKey = "CP_D_Fly";

    /// <summary>The CONTROLS page's flying-scheme chooser, on the Mouse Sensitivity panel's title
    /// line. Remake-only, so the layout names no widget for it.</summary>
    public const string ControlsMouseKey = "MOUSESCHEME";

    /// <summary>The CONTROLS page's Mouse Sensitivity slider, the authored one, which sets the Fly
    /// scheme's sensitivity.</summary>
    public const string ControlsSensitivityKey = "CP_S_MOUSE";

    /// <summary>The CONTROLS page's door onto the KEYS AND BUTTONS page.</summary>
    public const string KeysDoorKey = "CP_B_KEYS";

    /// <summary>The CONTROLS page's ACCEPT CHANGES, which writes the staged keymaps.</summary>
    public const string ControlsAcceptKey = "CP_B_ACCEPTCHANGES";

    /// <summary>The CONTROLS page's CANCEL CHANGES, which drops them.</summary>
    public const string ControlsCancelKey = "CP_B_CANCELCHANGES";

    /// <summary>The KEYS AND BUTTONS page's RESET TO DEFAULT.</summary>
    public const string KeysResetKey = "KB_B_RESET";

    /// <summary>The KEYS AND BUTTONS page's ACCEPT CHANGES.</summary>
    public const string KeysAcceptKey = "KB_B_ACCEPTCHANGES";

    /// <summary>The KEYS AND BUTTONS page's CANCEL CHANGES, authored left of ACCEPT CHANGES.
    /// </summary>
    public const string KeysCancelKey = "KB_B_CANCELCHANGES";

    /// <summary>The KEYS AND BUTTONS page's action list, whose window and pitch the rows take.
    /// </summary>
    public const string KeysListKey = "KB_L_Controls";

    /// <summary>How many category tabs the page authors.</summary>
    public const int KeysTabCount = 7;

    // The Options hub's own section, whose logo every page here keeps standing behind it since none
    // of the five authors one. The hub is the shell's (OriginalShell.PreferencesSection), its name
    // restated here so this module holds no reference to OriginalShell.
    private const string HubSection = "Preferences";

    // The hub's own title font, which every page here writes its own title in.
    private const float PageTitleFont = 20f;

    // A checkbox's size where its strip cannot be measured, and a category tab's, so the row still
    // has a rectangle. The tab's pair is the shell's paper-plaque fallback, that strip being what
    // the tab column falls back to.
    private const float FallbackCheckSize = 24f;
    private const float FallbackTabWidth = 162f;
    private const float FallbackTabHeight = 28f;

    // A decoded button strip's size where its own art cannot be measured.
    private const float FallbackButtonWidth = 220f;
    private const float FallbackButtonHeight = 42f;

    // The slider's two art files, and their shipped pixel sizes as the fallback when neither can
    // be measured. Neither row carries a frame count, so each is one image with no state to draw.
    // docs/formats/menu-layout.md holds the Z row's decode.
    private const string SliderSlotArt = "PF_B_SliderSlot.png";
    private const string SliderThumbArt = "PF_B_Slider.png";
    private const float FallbackSlotWidth = 171f;
    private const float FallbackSlotHeight = 3f;
    private const float FallbackThumbWidth = 43f;
    private const float FallbackThumbHeight = 21f;

    // The authored insets from the slot to the region a press has to land in, negative where the
    // region grows. Three pixels of slot become twenty-three, which is what makes the whole thumb
    // pressable. Every shipped slider row authors these four.
    private const int SliderInsetLeft = 0;
    private const int SliderInsetTop = -10;
    private const int SliderInsetRight = 1;
    private const int SliderInsetBottom = -10;

    // The one dropdown the Game Options section authors, whose box, window and scroll art every
    // option row of that page takes. The page authors a single D row, and the rows stand at its
    // column.
    private const string GameOptionDropKey = "GO_D_DIFFICULTY";

    // The Game Options page's authored row shape, used where a layout does not carry the section.
    // It is the title column and its box, the first row's line and the 62-pixel pitch, and the
    // dropdown box. The checkbox's offset from its own row and the description column follow.
    // The decode these come from is in docs/org/menu-inventory.md.
    private const float GameOptionTitleX = 138f;
    private const float GameOptionTitleWidth = 170f;
    private const float GameOptionCheckTitleWidth = 112f;
    private const float GameOptionFirstY = 283f;
    private const float GameOptionPitch = 62f;
    private const float GameOptionDropX = 143f;
    private const float GameOptionDropDy = 16f;
    private const float GameOptionDropWidth = 144f;
    private const float GameOptionItemHeight = 17f;
    private const float GameOptionCheckDx = 122f;
    private const float GameOptionCheckDy = 3f;
    private const float GameOptionDescX = 346f;
    private const float GameOptionDescDy = 9f;
    private const float GameOptionDescWidth = 310f;

    private const float GameOptionTitleFont = 14f;
    private const float GameOptionDescFont = 12f;

    // The plate the section authors and how many rows it holds. GO_BackGround.png draws three
    // raised row panels at the authored pitch over one grey description window, and the section
    // stands three rows on it. A page carrying more grows the plate by whole bands of its own art.
    private const int GameOptionAuthoredRows = 3;

    // The band a repeat takes, in the plate art's own pixels. Y 116 to 178 is one whole row panel,
    // both seams inside the dark gaps the art leaves between panels. Those gaps are 112 to 119 and
    // 174 to 181. A repeat therefore cuts no rivet, no panel edge and no window border, and the
    // description window grows with the rows. A vertical stretch would smear all three.
    private const int GameOptionPlateBandY = 116;
    private const int GameOptionPlateBandHeight = 62;

    // The first authored row's own inset from the top of the band it stands on, used where the plate
    // is not there to measure it from. A band opens one band above the head crop's bottom edge.
    private const float GameOptionBandInset = 14f;

    // The AUDIO page's authored row shape, used where a layout does not carry the section or one of
    // its rows. It is the title column and its box, the slider column, and the distance from a
    // title's line down to its own slider's. The description column and its width follow. Each
    // row's own line is on its table entry instead, the authored pitch being four different numbers
    // rather than one (docs/org/menu-inventory.md).
    private const float AudioTitleX = 137f;
    private const float AudioTitleWidth = 170f;
    private const float AudioSliderX = 137f;
    private const float AudioSliderOffset = 26f;
    private const float AudioDescX = 348f;
    private const float AudioDescWidth = 310f;

    private const float AudioTitleFont = 14f;
    private const float AudioDescFont = 12f;

    // The aid's four levels, one per row and all distinct. A single shot then shows the thumb at
    // four places on the track rather than at the shipped default three times over.
    private const int AudioPoseMaster = 100;
    private const int AudioPoseMusic = 25;
    private const int AudioPoseEffects = 60;
    private const int AudioPoseVoice = 85;

    // The VIDEO page's authored row shape, used where a layout does not carry the section. It is
    // the title column and the Shadows row's line, the checkbox's offset from its title, and the
    // dropdown column and box. The description column takes the width the plaque column leaves it.
    private const float VideoTitleX = 18f;
    private const float VideoTitleWidth = 162f;
    private const float VideoShadowsY = 565f;
    private const float VideoCheckDx = 158f;
    private const float VideoCheckDy = -4f;
    private const float VideoDropX = 186f;
    private const float VideoDropWidth = 120f;
    private const float VideoItemHeight = 17f;
    private const float VideoDescX = 325f;
    private const float VideoDescWidth = 244f;
    private const float VideoPlaqueX = 569f;

    private const float VideoTitleFont = 14f;
    private const float VideoDescFont = 12f;

    // The two control cells' key prefixes, each followed by the action's index in the standing tab.
    private const string KeysCellA = "KB:A:";
    private const string KeysCellB = "KB:B:";

    // The KEYS AND BUTTONS page's authored geometry, used where a layout does not carry the
    // section. It is the list's corner, pitch and window, the three column heads, and the tab
    // strip's corner and pitch.
    private const float KeysListX = 200f;
    private const float KeysListY = 310f;
    private const float KeysItemHeight = 16f;
    private const int KeysWindowRows = 12;
    private const float KeysActionX = 196f;
    private const float KeysActionWidth = 220f;
    private const float KeysControlAX = 455f;
    private const float KeysControlAWidth = 182f;
    private const float KeysControlBX = 641f;
    private const float KeysControlBWidth = 174f;
    private const float KeysHeadY = 287f;
    private const float KeysPlateX = 9f;
    private const float KeysPlateWidth = 788f;
    private const float KeysArrowWidth = 16f;

    // The rows under the category heading are indented, which is what the page's own still shows.
    private const float KeysRowIndent = 14f;

    private const float KeysHeadFont = 14f;
    private const float KeysRowFont = 12f;
    private const float KeysDescFont = 12f;

    // The CONTROLS page's authored row shape, the same fallbacks in the same spirit.
    private const float ControlsTitleX = 136f;
    private const float ControlsTitleWidth = 173f;
    private const float ControlsDropX = 134f;
    private const float ControlsDropY = 315f;
    private const float ControlsDropWidth = 175f;
    private const float ControlsItemHeight = 17f;
    private const float ControlsDescX = 349f;
    private const float ControlsDescWidth = 310f;
    private const float ControlsMouseX = 142f;
    private const float ControlsMouseY = 391f;
    private const float ControlsMouseTitleY = 364f;
    private const float ControlsMouseDescY = 373f;

    // The three IDS_DIFFICULTY rows as the campaign selector labels them.
    private static readonly string[] DifficultyWords =
    {
        CSVM.Flight.Hangar.Difficulty.Label(CSVM.Flight.Hangar.Difficulty.Normal),
        CSVM.Flight.Hangar.Difficulty.Label(CSVM.Flight.Hangar.Difficulty.Hard),
        CSVM.Flight.Hangar.Difficulty.Label(CSVM.Flight.Hangar.Difficulty.Hardest),
    };

    // The Default View dropdown's own three items, the words and the order the original's list
    // carries. They come from CSVM.Flight.Camera.PilotView.Selectable and .Label, decoded from uiData 2127
    // in crimson.exe (docs/org/menu-inventory.md holds the addresses).
    private static readonly string[] DefaultViewWords =
    {
        CSVM.Flight.Camera.PilotView.Label(CSVM.Flight.Camera.PilotView.Selectable[0]),
        CSVM.Flight.Camera.PilotView.Label(CSVM.Flight.Camera.PilotView.Selectable[1]),
        CSVM.Flight.Camera.PilotView.Label(CSVM.Flight.Camera.PilotView.Selectable[2]),
    };

    // The checkbox's two words, in the order its eight-frame strip reads them: index 0 unchecked,
    // index 1 checked. Only the page with no section draws them, as a text button's label.
    private static readonly string[] NearestAfterKillWords = { "OFF", "ON" };

    private static readonly string[] GraphicsWords = { "FAITHFUL", "ENHANCED" };

    // The Game Options page's options in their authored row order. Each is a title, a control, a
    // description and the words of the store field it reads and writes. A page never saves. The
    // apply exit carries every choice, and Launcher.ApplyOptions is the options file's one writer.
    // The difficulty row's title and description are IDS_GO_DIFFICULTY_TITLE and _DESC as authored.
    // The setting scales enemy armour and health at spawn and nothing about how the enemy flies.
    private static readonly GameOption[] GameOptions =
    {
        new(DifficultyKey, "Difficulty", _ => "Select the difficulty level for a solo campaign.",
            OriginalRowKind.Dropdown, DifficultyWords,
            s => CSVM.Flight.Hangar.Difficulty.Clamp(s._difficulty),
            (s, i) => s._difficulty = CSVM.Flight.Hangar.Difficulty.Clamp(i)),
        new(DefaultViewKey, "Default View", _ => "Select your default view.",
            OriginalRowKind.Dropdown, DefaultViewWords,
            s => IndexOfView(s._defaultView),
            (s, i) => s._defaultView = CSVM.Flight.Camera.PilotView.Name(
                CSVM.Flight.Camera.PilotView.Selectable[Math.Clamp(i, 0, CSVM.Flight.Camera.PilotView.Selectable.Count - 1)])),
        new(AutoHeadTurnKey, "Auto Head Turn",
            _ => "Select to turn your head automatically as your aircraft turns.",
            OriginalRowKind.Radio, NearestAfterKillWords,
            s => s._autoHeadTurn == true ? 1 : 0,
            (s, i) => s._autoHeadTurn = i == 1),
        new(NearestAfterKillKey, "Next Target",
            _ => "Take the nearest target after a kill instead of the first of the list.",
            OriginalRowKind.Radio, NearestAfterKillWords,
            s => s._nearestAfterKill == true ? 1 : 0,
            (s, i) => s._nearestAfterKill = i == 1),
        new(RumbleKey, "Rumble",
            _ => "Rumble the gamepad for guns, launches, hits, the nitro and a dive past the rated maximum.",
            OriginalRowKind.Radio, NearestAfterKillWords,
            s => s._rumble == false ? 0 : 1,
            (s, i) => s._rumble = i == 1),
    };

    // The AUDIO page's levels, in the authored order of the rows they stand on. The cursor walks
    // the table, and a form is read top to bottom. Each is a title, the authored title, control and
    // description widgets it stands on, and a description. Each also carries the authored lines
    // those two texts fall back to, and how the store field is read and written. A level is never a
    // vocabulary word, so a row reads a never-set field as the shipped default rather than as a
    // first word.
    private static readonly AudioOption[] AudioOptions =
    {
        new(AudioMasterKey, MenuMixLevel.Master, "Master", "AP_T_MusicTitle", null, "AP_T_MusicDesc",
            "Set the overall volume of all sounds.", 266f, 278f,
            s => s._audioMaster ?? CSVM.Utils.AudioMix.DefaultMaster,
            (s, v) => s._audioMaster = v),
        new(AudioMusicKey, MenuMixLevel.Music, "Music Volume", "AP_T_MVolTitle", "AP_S_MVOLUME", "AP_T_MVolDesc",
            "Set the volume of the in-game music.", 324f, 332f,
            s => s._audioMusic ?? CSVM.Utils.AudioMix.DefaultMusic,
            (s, v) => s._audioMusic = v),
        new(AudioEffectsKey, MenuMixLevel.Effects, "Effects Volume", "AP_T_EVolTitle", "AP_S_EVOLUME", "AP_T_EVolDesc",
            "Set the volume of the sound effects.", 381f, 387f,
            s => s._audioEffects ?? CSVM.Utils.AudioMix.DefaultEffects,
            (s, v) => s._audioEffects = v),
        new(AudioVoiceKey, MenuMixLevel.Voice, "Voice Volume", "AP_T_VVolTitle", "AP_S_VVOLUME", "AP_T_VVolDesc",
            "Set the volume of the voices.", 434f, 442f,
            s => s._audioVoice ?? CSVM.Utils.AudioMix.DefaultVoice,
            (s, v) => s._audioVoice = v),
    };

    // The first authored slider row, which is where the AUDIO page's slider geometry is read from.
    // The Master row has no authored slider of its own. Its authored row carries the In-Game Music
    // checkbox, which stands at another column and on the title's own line. A Master slider placed
    // at that widget's corner would sit 122 pixels right of the three below it.
    private static readonly AudioOption AudioSliderRow = AudioOptions[1];

    // The VIDEO page's settings, in the authored order of the rows they stand on. Each is a title,
    // the authored row it stands on, a description, a control and the words of the store field it
    // reads and writes.
    private static readonly VideoOption[] VideoOptions =
    {
        new(MonitorKey, "Monitor", "VP_T_VideoTitle", "VP_D_Device", "VP_T_DEVICEDESC",
            _ => "Select the monitor the game opens on.",
            OriginalRowKind.Dropdown, s => s.MonitorWords,
            s => CSVM.Utils.MonitorSetting.Resolve(s._monitorIndex, s.Screens).Screen,
            (s, i) => s._monitorIndex = CSVM.Utils.MonitorSetting.Word(i)),
        new(ResolutionKey, "Resolution", "VP_T_DisplayTitle", "VP_D_Display", "VP_T_DisplayDESC",
            s => s.ResolutionDescription(),
            OriginalRowKind.Dropdown, s => s.ResolutionWords,
            s => DisplaySettingRows.ResolutionIndex(s.Sizes, s._resolution, s._displayMode),
            (s, i) => s._resolution = s.ResolutionWords[i],
            s => !s.ResolutionPinned),
        new(DisplayModeKey, "Display Mode", "VP_T_ViewTitle", "VP_D_View", "VP_T_ViewDESC",
            _ => "Select how the window sits on the screen. Borderless leaves the desktop beneath it.",
            OriginalRowKind.Dropdown, _ => DisplaySettingRows.DisplayModeLabels,
            s => DisplaySettingRows.WordIndex(CSVM.Utils.DisplayWords.DisplayModes, s._displayMode, CSVM.Utils.DisplayModeSetting.Default),
            (s, i) => s._displayMode = CSVM.Utils.DisplayWords.DisplayModes[i]),
        new(VSyncKey, "V-Sync", "VP_T_EffectsTitle", "VP_D_Effects", "VP_T_EffectsDESC",
            _ => "Select the frame pacing. On follows the screen; off runs free, or to a frame cap.",
            OriginalRowKind.Dropdown, _ => DisplaySettingRows.VSyncLabels,
            s => DisplaySettingRows.WordIndex(CSVM.Utils.DisplayWords.VSyncChoices, s._vsync, CSVM.Utils.VSyncSetting.Default),
            (s, i) => s._vsync = CSVM.Utils.DisplayWords.VSyncChoices[i]),
        new(GraphicsKey, "Enhanced Graphics", "VP_T_ShadowsTitle", "VP_B_SHADOWS", "VP_T_ShadowsDESC",
            s => s.GraphicsDescription(), OriginalRowKind.Radio, _ => GraphicsWords,
            s => s._graphics == CSVM.Utils.GraphicsMode.EnhancedWord ? 1 : 0,
            (s, i) => s._graphics = i == 1 ? CSVM.Utils.GraphicsMode.EnhancedWord : CSVM.Utils.GraphicsMode.Default),
    };

    // The six named tabs are the original's own action categories, over this port's flight actions,
    // each in that page's own row order (`OriginalScreenshots/Keybinds *.png`). The seventh, Other,
    // takes its own flight rows first, then every action the six leave over, in context then enum
    // order. A new action therefore lands on a page rather than nowhere. The reading is in
    // docs/org/menu-inventory.md.
    private static readonly InputAction[][] KeysFlightGroups =
    {
        new[]
        {
            InputAction.PitchDown, InputAction.PitchUp, InputAction.RollLeft, InputAction.RollRight,
            InputAction.YawLeft, InputAction.YawRight,
        },
        new[]
        {
            InputAction.ThrottleUp, InputAction.ThrottleDown, InputAction.ThrottleSet0,
            InputAction.ThrottleSet1, InputAction.ThrottleSet2, InputAction.ThrottleSet3,
            InputAction.ThrottleSet4, InputAction.ThrottleSet5, InputAction.ThrottleSet6,
            InputAction.ThrottleSet7, InputAction.ThrottleSet8,
        },
        new[]
        {
            InputAction.FireGuns, InputAction.FireRockets, InputAction.SelectGunGroup,
            InputAction.SelectGunGroupPrev, InputAction.SelectOrdnance, InputAction.SelectOrdnancePrev,
        },
        new[]
        {
            InputAction.TargetNextEnemy, InputAction.TargetPreviousEnemy, InputAction.TargetNearestEnemy,
            InputAction.TargetNextAlly, InputAction.TargetPreviousAlly, InputAction.TargetNearestAlly,
            InputAction.TargetNextNonAircraft, InputAction.TargetPreviousNonAircraft,
            InputAction.TargetNearestNonAircraft, InputAction.TargetNearest, InputAction.TargetClear,
        },
        new[]
        {
            InputAction.ToggleSpyglass, InputAction.CycleCockpitViews, InputAction.FlybyView,
            InputAction.SnapLookMode, InputAction.TrackTarget, InputAction.SmoothLookMode,
            InputAction.SelectChaseView,
            InputAction.LookBack, InputAction.LookCenter, InputAction.FreeLook,
        },
        new[]
        {
            InputAction.LookUp, InputAction.LookDown, InputAction.LookLeft, InputAction.LookRight,
            InputAction.LookAimUp, InputAction.LookAimDown, InputAction.LookAimLeft, InputAction.LookAimRight,
        },
    };

    // The Other tab's own flight rows, in the original's Other page order. The rest of that tab is
    // whatever the seven groups leave over, which is every menu and free-camera action.
    private static readonly InputAction[] KeysOtherFlightGroup =
    {
        InputAction.AutoLand, InputAction.Nitro, InputAction.Respawn, InputAction.Pause,
    };

    private static readonly string[] KeysTabNames =
    {
        "Movement", "Throttle", "Weapons", "Targeting", "Views 1", "Views 2", "Other",
    };

    private static readonly ControlsTab[] KeysTabs = BuildKeysTabs();

    private readonly MenuLayout _layout;
    private readonly IOriginalScreenHost _host;
    private readonly Func<CSVM.Utils.OptionsDef>? _options;
    private readonly Func<CSVM.Utils.SizeList>? _screenSizes;
    private readonly Func<CSVM.Utils.ScreenList>? _screens;
    private readonly ControlsFeature? _controls;

    private string? _goOpen;
    private int _goListTop;
    // Which level a row's slider moved, cleared by the host's read of it. The control writes only
    // where the value actually changed. This stands at None through every frame of a drag that held
    // the thumb still, which keeps a preview off a pointer's frame rate.
    private MenuMixLevel _audioMoved;
    private string? _vpOpen;
    private int _vpListTop;
    private int _keysTab;
    private int _keysTop;
    private string _graphics = CSVM.Utils.GraphicsMode.Default;
    private int _difficulty = CSVM.Flight.Hangar.Difficulty.Normal;
    // The targeting setting as saved, null while never set, which the consumer reads as off. It is
    // held nullable rather than as the checkbox's own 0/1. A page that never showed it then hands
    // back "never set" instead of writing a choice the player did not make.
    private bool? _nearestAfterKill;
    // The haptics setting as saved, held the same way but read the other way round. Null is "never
    // set", which the consumer reads as ON, since the original ships force feedback on.
    private bool? _rumble;
    // The opening view as saved, the --view= word, null while never set, which the flight reads as
    // Chase. Held as the word rather than the mode for the reason the store holds one.
    private string? _defaultView;
    // The automatic head turn as saved, null while never set. Null leaves the headLook.autohead
    // config key deciding rather than overruling it with a default of this page's own.
    private bool? _autoHeadTurn;
    // The four display settings as they were saved. A page that shows a setting still has to hand
    // back the ones it does not, or the one writer's save would clear them. Carrying them here is
    // what lets every page's apply do that.
    private string? _monitorIndex;
    private string? _resolution;
    // The size the options file named when the pages last read it, which the size row offers as an
    // entry of its own (Sizes). It is held apart from the stepped choice, so a hand-written size
    // stays in the list after a step lands elsewhere. A step back then reaches it again.
    private string? _savedResolution;
    private string? _displayMode;
    private string? _vsync;
    // The four saved volume levels, carried for the same reason. The AUDIO page shows them and the
    // other pages do not, and every page's apply hands back the settings it does not show.
    private int? _audioMaster;
    private int? _audioMusic;
    private int? _audioEffects;
    private int? _audioVoice;

    /// <summary>An options module over <paramref name="layout"/>'s own five sections, calling back
    /// into <paramref name="host"/> for the state every screen family shares. The reader
    /// <paramref name="options"/> reads the saved settings the pages show back, null opening them on
    /// the shipped defaults, which an engine-free test wants. The VIDEO page's two enumerated rows
    /// come from <paramref name="screenSizes"/> and <paramref name="screens"/>, and the CONTROLS
    /// pages stage in <paramref name="controls"/>. It never writes.</summary>
    public OriginalOptionsScreen(
        MenuLayout layout,
        IOriginalScreenHost host,
        Func<CSVM.Utils.OptionsDef>? options = null,
        Func<CSVM.Utils.SizeList>? screenSizes = null,
        Func<CSVM.Utils.ScreenList>? screens = null,
        ControlsFeature? controls = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _options = options;
        _screenSizes = screenSizes;
        _screens = screens;
        _controls = controls;
    }

    /// <summary>The seven category tabs and the rows each one lists, in the order the KEYS AND
    /// BUTTONS page draws them.</summary>
    public static IReadOnlyList<ControlsTab> ControlTabs => KeysTabs;

    /// <summary>The Game Options page's open option list's key, or null when none is open.</summary>
    public string? OpenGameOption => _goOpen;

    /// <summary>The VIDEO page's open option list's key, or null when none is open.</summary>
    public string? OpenVideoOption => _vpOpen;

    /// <summary>The sizes the resolution row offers, which is what the window's own screen can
    /// hold. The size the options file names is added where that is not among them. Every other
    /// row's words are a fixed vocabulary. This one's are enumerated per screen, so a shell with no
    /// screen to ask offers every candidate size instead.</summary>
    public IReadOnlyList<string> ResolutionWords => Sizes.Words;

    /// <summary>The screens the monitor row offers, one label per screen in index order. Enumerated
    /// like the resolution row's sizes, so a shell with no engine to ask offers the one screen it
    /// can name.</summary>
    public IReadOnlyList<string> MonitorWords => Screens.Labels;

    /// <summary>Whether the display mode standing on the page owns the size, which borderless does.
    /// The row then reads the screen's own size, takes no press and draws dead. The saved size is
    /// left where it is, so picking Windowed or Fullscreen again gives the player it back.</summary>
    public bool ResolutionPinned => CSVM.Utils.ResolutionSetting.Pinned(_displayMode);

    /// <summary>The mix the AUDIO page stands at while it is open. The host applies it, so a level
    /// can be judged by ear as it moves. It is null on every other screen, which is what makes
    /// leaving this page by any door drop the preview. The module states four levels and nothing
    /// more. Which bus each reaches, and whether a preview sounds at all, are the audio
    /// service's.</summary>
    public CSVM.Utils.AudioLevels? AudioPreviewMix =>
        _host.Screen == OriginalScreen.Audio
            ? new CSVM.Utils.AudioLevels(
                AudioLevel(MenuMixLevel.Master), AudioLevel(MenuMixLevel.Music),
                AudioLevel(MenuMixLevel.Effects), AudioLevel(MenuMixLevel.Voice))
            : null;

    /// <summary>Which category tab the KEYS AND BUTTONS page is standing on.</summary>
    public int KeysTab => _keysTab;

    /// <summary>The first row of the standing tab that the list's window shows.</summary>
    public int KeysTop => _keysTop;

    /// <summary>The graphics mode word the VIDEO page would apply.</summary>
    public string GraphicsChoice => _graphics;

    /// <summary>The screen index (<see cref="CSVM.Utils.MonitorSetting.Word"/>'s spelling) the
    /// VIDEO page would apply, or null while nothing has been saved and no row has been
    /// touched.</summary>
    public string? MonitorChoice => _monitorIndex;

    /// <summary>The window size (<see cref="CSVM.Utils.OptionsStore.FormatResolution"/>'s spelling)
    /// the VIDEO page would apply, or null while nothing has been saved and no row has been
    /// touched.</summary>
    public string? ResolutionChoice => _resolution;

    /// <summary>The display-mode word (<see cref="CSVM.Utils.DisplayWords.DisplayModes"/>) the
    /// VIDEO page would apply, or null while nothing has been saved and no row has been
    /// touched.</summary>
    public string? DisplayModeChoice => _displayMode;

    /// <summary>The V-Sync word (<see cref="CSVM.Utils.DisplayWords.VSyncChoices"/>) the VIDEO page
    /// would apply, or null while nothing has been saved and no row has been touched.</summary>
    public string? VSyncChoice => _vsync;

    /// <summary>The Master level (<see cref="CSVM.Utils.AudioMix"/>'s 0..100) the AUDIO page would
    /// apply, or null while nothing has been saved and no row has been touched.</summary>
    public int? AudioMasterChoice => _audioMaster;

    /// <summary>The Music level the AUDIO page would apply, or null while never set.</summary>
    public int? AudioMusicChoice => _audioMusic;

    /// <summary>The Effects level the AUDIO page would apply, or null while never set.</summary>
    public int? AudioEffectsChoice => _audioEffects;

    /// <summary>The Voice level the AUDIO page would apply, or null while never set.</summary>
    public int? AudioVoiceChoice => _audioVoice;

    /// <summary>The difficulty tier (<see cref="CSVM.Flight.Hangar.Difficulty"/>) the Game Options page
    /// would apply.</summary>
    public int DifficultyChoice => _difficulty;

    /// <summary>The targeting setting the Game Options page would apply, or null while nothing has
    /// been saved and no row has been touched.</summary>
    public bool? NearestAfterKillChoice => _nearestAfterKill;

    /// <summary>The haptics setting the Game Options page would apply, or null while nothing has
    /// been saved and no row has been touched.</summary>
    public bool? RumbleChoice => _rumble;

    /// <summary>The opening view the Game Options page would apply, a
    /// <see cref="CSVM.Flight.Camera.PilotView.Name"/> word, or null while nothing has been saved and no
    /// row has been touched.</summary>
    public string? DefaultViewChoice => _defaultView;

    /// <summary>The automatic head turn the Game Options page would apply. It is null while nothing
    /// has been saved and no row has been touched, which leaves the config key deciding.</summary>
    public bool? AutoHeadTurnChoice => _autoHeadTurn;

    /// <summary>The n-th tab's own button key, which is how the layout spells the strip.</summary>
    public static string KeysTabKey(int tab) =>
        "KB_B_CAT" + (tab + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The key of the control cell at that row and column, the two positions of the
    /// authored Control A and Control B columns.</summary>
    public static string KeysCellKey(int row, bool second) =>
        (second ? KeysCellB : KeysCellA) + row.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether the screen showing is one of this module's five.</summary>
    public bool Owns(OriginalScreen screen) =>
        screen is OriginalScreen.GameOptions or OriginalScreen.Audio or OriginalScreen.Video
            or OriginalScreen.ControlsPrefs or OriginalScreen.Keys;

    /// <summary>The showing page's rows, in focus order.</summary>
    public void BuildRows(List<OriginalRow> rows)
    {
        switch (_host.Screen)
        {
            case OriginalScreen.GameOptions:
                BuildGameOptionsRows(rows);
                break;
            case OriginalScreen.Audio:
                BuildAudioRows(rows);
                break;
            case OriginalScreen.Video:
                BuildVideoRows(rows);
                break;
            case OriginalScreen.ControlsPrefs:
                BuildControlsPrefsRows(rows);
                break;
            case OriginalScreen.Keys:
                BuildKeysRows(rows);
                break;
        }
    }

    /// <summary>The showing page's scrolling lists for the pointer. They are an open option list
    /// where one stands, and the action list on the KEYS AND BUTTONS page. The AUDIO page has none,
    /// its rows all in view.</summary>
    public void Lists(List<OriginalList> lists)
    {
        switch (_host.Screen)
        {
            case OriginalScreen.GameOptions:
                GameOptionsLists(lists);
                break;
            case OriginalScreen.Video:
                VideoLists(lists);
                break;
            case OriginalScreen.Keys:
                KeysLists(lists);
                break;
        }
    }

    /// <summary>A sideways step on the focused row where it changes a value there. That is an
    /// option's next word with wrap, the next registered seat, or the other flying scheme. False on
    /// anything else, so the step crosses columns. The AUDIO page's sliders are the shell's own
    /// step, which runs ahead of this one.</summary>
    public bool StepSideways(IReadOnlyList<OriginalRow> rows, int focus, int direction) =>
        _host.Screen switch
        {
            OriginalScreen.GameOptions => StepGameOptionValue(rows, focus, direction),
            OriginalScreen.Video => StepVideoValue(rows, focus, direction),
            OriginalScreen.ControlsPrefs => StepControlsValue(rows, focus, direction),
            _ => false,
        };

    /// <summary>Closes an open option list and puts the focus back on its box; false when none is
    /// open.</summary>
    public bool CloseDropdown() => CloseGameOptionsDropdown() || CloseVideoDropdown();

    /// <summary>The showing page's answer to an activated row.</summary>
    public MenuExit? Activate(OriginalRow row) =>
        _host.Screen switch
        {
            OriginalScreen.GameOptions => ActivateGameOptions(row),
            OriginalScreen.Audio => ActivateAudio(row),
            OriginalScreen.Video => ActivateVideo(row),
            OriginalScreen.ControlsPrefs => ActivateControlsPrefs(row),
            OriginalScreen.Keys => ActivateKeys(row),
            _ => null,
        };

    /// <summary>Back on the showing page. On Game Options and VIDEO the first one closes an open
    /// list and the next leaves. On the AUDIO page, which carries no list, and on the two rebinding
    /// pages, the first one leaves. Every page answers it, each the way its own CANCEL CHANGES does,
    /// since that is the declining answer the layout gives them.</summary>
    public bool Back()
    {
        switch (_host.Screen)
        {
            case OriginalScreen.GameOptions:
                BackGameOptions();
                return true;
            case OriginalScreen.Audio:
                BackToPreferences();
                return true;
            case OriginalScreen.Video:
                BackVideo();
                return true;
            case OriginalScreen.ControlsPrefs:
            case OriginalScreen.Keys:
                BackControls();
                return true;
            default:
                return false;
        }
    }

    /// <summary>The showing page as drawn. No page writes a note or a stroke, since the prose
    /// layer is the hangar's description box alone and the pen is the campaign scrapbook's.
    /// Those two layers of <paramref name="layers"/> stand untouched here.</summary>
    public void Compose(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        switch (_host.Screen)
        {
            case OriginalScreen.GameOptions:
                ComposeGameOptions(rows, focus, layers);
                break;
            case OriginalScreen.Audio:
                ComposeAudio(rows, focus, layers);
                break;
            case OriginalScreen.Video:
                ComposeVideo(rows, focus, layers);
                break;
            case OriginalScreen.ControlsPrefs:
                ComposeControlsPrefs(rows, focus, layers);
                break;
            case OriginalScreen.Keys:
                ComposeKeys(rows, focus, layers);
                break;
        }
    }

    /// <summary>Re-reads the saved settings where the screen just opened is one of the three pages
    /// that show them back. The shell calls it from every Open. A page opened by a door, by a
    /// return or by a screenshot aid owes the player the same words.</summary>
    public void ScreenOpened(OriginalScreen screen)
    {
        if (screen is OriginalScreen.GameOptions or OriginalScreen.Audio or OriginalScreen.Video)
        {
            ReadSavedOptions();
        }
    }

    /// <summary>Keeps the KEYS AND BUTTONS page's list window over the cursor at the end of a
    /// frame. The cell the player is looking at is then the cell a capture binds. Nothing on any
    /// other screen.</summary>
    public void SyncWindows()
    {
        if (_host.Screen == OriginalScreen.Keys)
        {
            SyncKeysWindow();
        }
    }

    /// <summary>Opens the Game Options page on the saved options with its first row focused. The
    /// hub's GAME OPTIONS door and the screenshot aid both go through it. The page is a form, not a
    /// list. It opens on its first option rather than on wherever the cursor stood when it was last
    /// left.</summary>
    public void OpenGameOptions()
    {
        _goOpen = null;
        _goListTop = 0;
        _host.Open(OriginalScreen.GameOptions);
        _host.FocusedRow = -1;
    }

    /// <summary>Opens the AUDIO page on the saved mix with Master focused, which is what the hub's
    /// AUDIO door and the screenshot aid both go through.</summary>
    public void OpenAudio()
    {
        _host.Open(OriginalScreen.Audio);
        _host.FocusedRow = -1;
    }

    /// <summary>Stands the four AUDIO rows at four distinct levels, the screenshot aid's pose for a
    /// page whose four thumbs otherwise sit at two places. Nothing happens off the AUDIO page, and
    /// nothing is saved: the pose is an edit the aid's shot is taken of.</summary>
    public void PoseAudioMix()
    {
        if (_host.Screen != OriginalScreen.Audio)
        {
            return;
        }

        _audioMaster = AudioPoseMaster;
        _audioMusic = AudioPoseMusic;
        _audioEffects = AudioPoseEffects;
        _audioVoice = AudioPoseVoice;
    }

    /// <summary>Which level has moved since this was last asked, and <see cref="MenuMixLevel.None"/>
    /// when none has. ⚠ Taken rather than read: the host sounds the moved category, so a caller that
    /// saw one move twice would sound it twice.</summary>
    public MenuMixLevel TakeAudioMoved()
    {
        var moved = _audioMoved;
        _audioMoved = MenuMixLevel.None;
        return moved;
    }

    /// <summary>Opens the VIDEO page on the saved options with its first row focused. The hub's
    /// VIDEO door and the screenshot aid both go through it.</summary>
    public void OpenVideo()
    {
        _vpOpen = null;
        _vpListTop = 0;
        _host.Open(OriginalScreen.Video);
        _host.FocusedRow = -1;
    }

    /// <summary>Opens the VIDEO page with one named row focused rather than its first, the
    /// screenshot aid's way onto a setting further down the page.</summary>
    public void OpenVideoOn(string key)
    {
        OpenVideo();
        _host.FocusKey(key);
    }

    /// <summary>Opens the CONTROLS page on its first row, the hub's CONTROLS door and the
    /// screenshot aid's door alike.</summary>
    public void OpenControlsPrefs()
    {
        _host.Open(OriginalScreen.ControlsPrefs);
        _host.FocusedRow = -1;
    }

    /// <summary>Opens the KEYS AND BUTTONS page on its first category with the list at its head.
    /// </summary>
    public void OpenKeys()
    {
        _keysTab = 0;
        _keysTop = 0;
        _controls?.CancelCapture();
        _host.Open(OriginalScreen.Keys);
        _host.FocusedRow = -1;
    }

    /// <summary>Stands the KEYS AND BUTTONS page on one category tab, the screenshot aids' door.
    /// </summary>
    public void ShowKeysTab(int tab)
    {
        _keysTab = Math.Clamp(tab, 0, KeysTabs.Length - 1);
        _keysTop = 0;
        _host.FocusedRow = -1;
    }

    /// <summary>What the KEYS AND BUTTONS page prints in that row's Control A and Control B cells.
    /// The first control stands in A and the rest in B, which is what keeps a third binding from
    /// being hidden by two authored columns.</summary>
    public (string A, string B) KeysCellText(int row)
    {
        if (_controls is not { } controls || controls.Players.Count == 0
            || row < 0 || row >= KeysTabs[_keysTab].Rows.Count)
        {
            return (string.Empty, string.Empty);
        }

        var cell = KeysTabs[_keysTab].Rows[row];
        var bindings = controls.Bindings(cell.Context, cell.Action);
        if (bindings.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        string a = BindingLabels.Describe(bindings[0]);
        if (bindings.Count == 1)
        {
            return (a, string.Empty);
        }

        var rest = new List<Binding>(bindings.Count - 1);
        for (int i = 1; i < bindings.Count; i++)
        {
            rest.Add(bindings[i]);
        }

        return (a, BindingLabels.Row(rest, 1));
    }

    // The rest of this class stays in the five pages' own narrative order. A helper stands beside
    // the entry point it serves, and one page's half after another's. Nothing is hoisted into
    // blocks for the ordering rules' sake, the same trade OriginalInstantActionScreen.cs makes.
#pragma warning disable SA1201, SA1202, SA1204

    // The saved options every page shows back. They are what was asked for, not what this process
    // resolved. A flag or the config key can have decided either, and the page still owes the
    // player the words their own ACCEPT CHANGES saved. Every page reads every setting, since each
    // one's apply carries the settings it does not show unchanged. A module with no reader opens
    // on the shipped defaults, which is what an engine-free test wants.
    private void ReadSavedOptions()
    {
        var saved = _options?.Invoke();
        _graphics = saved?.GraphicsMode ?? CSVM.Utils.GraphicsMode.Default;
        _difficulty = CSVM.Flight.Hangar.Difficulty.Parse(saved?.Difficulty) ?? CSVM.Flight.Hangar.Difficulty.Normal;
        _nearestAfterKill = saved?.NearestAfterKill;
        _rumble = saved?.Rumble;
        _defaultView = saved?.DefaultView;
        _autoHeadTurn = saved?.AutoHeadTurn;
        _monitorIndex = saved?.MonitorIndex;
        _resolution = saved?.Resolution;
        _savedResolution = saved?.Resolution;
        _displayMode = saved?.DisplayMode;
        _vsync = saved?.VSync;
        _audioMaster = saved?.AudioMaster;
        _audioMusic = saved?.AudioMusic;
        _audioEffects = saved?.AudioEffects;
        _audioVoice = saved?.AudioVoice;
    }

    // The apply exit every option page leaves through, carrying every setting the store holds. A
    // page writes the ones it shows and hands the rest back as ReadSavedOptions read them. That
    // keeps Launcher.ApplyOptions the options file's one writer.
    private OptionsApplyExit AppliedOptions() =>
        new(_graphics, CSVM.Flight.Hangar.Difficulty.Word(_difficulty),
            _monitorIndex, _resolution, _displayMode, _vsync,
            _audioMaster, _audioMusic, _audioEffects, _audioVoice, _nearestAfterKill, _rumble,
            _defaultView, _autoHeadTurn);

    // Back from a page: the saved settings are read again, so an edit the player declined is gone.
    private void BackToPreferences()
    {
        ReadSavedOptions();
        _host.Open(OriginalScreen.Options);
    }

    // The n-th art a row names as a strip, the shared drop-list rule's own reading. The option
    // pages and the Keys page name their arrows, bars and checkbox strips this way.
    private static BoardArt? StripArt(IReadOnlyList<string> art, int index, int frames = 4) =>
        OriginalDropLists.StripArt(art, index, frames);

    // A strip's one-frame size from the host's measurer, or the fallback when the file is not there.
    private (float Width, float Height) StripSize(BoardArt? art, float fallbackWidth, float fallbackHeight)
    {
        if (art == null || _host.Measure(art.Name) is not { } size)
        {
            return (fallbackWidth, fallbackHeight);
        }

        return (size.Width, (float)Math.Floor(size.Height / (float)Math.Max(1, art.Frames)));
    }

    // One of a section's own button strips as a row, at its authored corner in its measured size.
    // The offset <paramref name="dy"/> moves it down from that corner, which takes a plaque standing
    // on a grown plate's bottom band down with the plate. A dy of 0 leaves it where it is authored.
    private void AddStrip(
        MenuLayoutScreen screen, List<OriginalRow> rows, string key, OriginalRowKind kind, bool enabled,
        int column, float dy = 0f)
    {
        if (screen.Widget(key) is not { } widget)
        {
            return;
        }

        var art = StripArt(widget.Art, 0, widget.Frames);
        var size = StripSize(art, FallbackButtonWidth, FallbackButtonHeight);
        rows.Add(new OriginalRow(key, widget.Text ?? string.Empty, kind, widget.Int("X"), widget.Int("Y") + dy,
            size.Width, size.Height, enabled, column, art));
    }

    // The shared open-dropdown rule under the names these pages call it by.
    private static OpenDropList DropList(
        string key, MenuLayoutWidget? widget, IReadOnlyList<string> items,
        (float X, float Y, float Width, float Height) box) =>
        OriginalDropLists.Over(key, widget, items, box);

    private static int DropListTop(OpenDropList drop, int top, int focused) =>
        OriginalDropLists.Top(drop, top, focused);

    private void AddDropListRows(OpenDropList drop, int top, List<OriginalRow> rows) =>
        OriginalDropLists.AddRows(drop, top, rows, StripSize);

    private ListWindow? DropListWindow(OpenDropList drop, int top) =>
        OriginalDropLists.Window(drop, top, StripSize);

    // The focus is per screen, so the pull the rule applies to a focused item lands on the showing
    // page's own cursor.
    private int ScrollDropList(OpenDropList drop, int top)
    {
        int focused = _host.FocusedRow;
        int first = OriginalDropLists.Scroll(drop, top, ref focused);
        _host.FocusedRow = focused;
        return first;
    }

    // A slider row from its authored widget. The slot stands at the widget's corner in its own
    // art's measured size, the thumb in its own art's. The row's rectangle is the region the widget
    // insets the slot into, which is what the pointer has to hit. The page supplies the range its
    // setting spans, the level it stands at and where a new level goes. The shell's own
    // SliderControl drives it and the shell draws it, this row being one of its kinds.
    private OriginalRow SliderRow(
        MenuLayoutWidget? widget, string key, float fallbackX, float fallbackY,
        int min, int max, int value, Action<int> setValue, bool enabled = true, int column = 0)
    {
        var slot = SliderArt(widget, 0, SliderSlotArt);
        var thumb = SliderArt(widget, 1, SliderThumbArt);
        var slotSize = StripSize(slot, FallbackSlotWidth, FallbackSlotHeight);
        var thumbSize = StripSize(thumb, FallbackThumbWidth, FallbackThumbHeight);
        float x = widget?.Int("X", (int)fallbackX) ?? fallbackX;
        float y = widget?.Int("Y", (int)fallbackY) ?? fallbackY;
        var track = new SliderTrack(x, y, slotSize.Width, slotSize.Height, thumbSize.Width, thumbSize.Height, min, max);
        var region = SliderRegion(widget, x, y, slotSize);
        return new OriginalRow(key, string.Empty, OriginalRowKind.Slider, region.X, region.Y,
            region.Width, region.Height, enabled, column, thumb,
            Slider: new OriginalSlider(track, track.Clamp(value), setValue, slot));
    }

    // The rectangle a slider row authors is its slot art's own at the row's corner. The four insets
    // the row carries move it in, and out where one is negative. That is the region a press has to
    // land in. Any row standing in such a box takes it for its box, its words and its hit area
    // alike. A Z row states no width and no height of its own.
    private static (float X, float Y, float Width, float Height) SliderRegion(
        MenuLayoutWidget? widget, float x, float y, (float Width, float Height) slot)
    {
        float left = x + (widget?.Int("Left", SliderInsetLeft) ?? SliderInsetLeft);
        float top = y + (widget?.Int("Top", SliderInsetTop) ?? SliderInsetTop);
        float right = x + slot.Width - (widget?.Int("Right", SliderInsetRight) ?? SliderInsetRight);
        float bottom = y + slot.Height - (widget?.Int("Bottom", SliderInsetBottom) ?? SliderInsetBottom);
        return (left, top, Math.Max(1f, right - left), Math.Max(1f, bottom - top));
    }

    // The n-th art a slider row names, falling back to the shipped file name. The control then
    // still has a name to draw where the section is absent. Neither art is a strip.
    private static BoardArt SliderArt(MenuLayoutWidget? widget, int index, string fallback)
    {
        var art = widget?.Art;
        string name = art != null && index < art.Count && art[index].Length > 0 ? art[index] : fallback;
        return new BoardArt(BoardArtLibrary.Ui, name, 1);
    }

    // Every page draws the hub's logo, its own section authoring none. Each puts its own plate in
    // the backdrop rather than among the pictures. A board draws its fills between the two layers.
    // A plate among the pictures would paint over each row's own focus mark. The page would then
    // show no focused row at all, whatever the mark is, the mark being under opaque art.
    private void ComposePreferencesLogo(List<BoardPicture> backdrop)
    {
        if (_layout.Screen(HubSection)?.Widget("PF_LOGO") is { Art.Count: > 0 } logo)
        {
            backdrop.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, logo.Art[0], Math.Max(1, logo.Frames)),
                logo.Int("X"), logo.Int("Y")));
        }
    }

    // The Game Options plate grows by whole bands of its own art rather than stretching. A page
    // with more rows than the section authors still stands on art at its authored scale. The pieces
    // are the head above the band, that band once per row, then the tail below it. An
    // <paramref name="extraRows"/> of 0 draws the one authored picture and nothing else.
    // ⚠ The band is the art's, not the row pitch a layout reads. The seams are chosen against the
    // bitmap's own gaps, so a section authoring another pitch still repeats these 62 pixels.
    private void ComposeGameOptionsPlate(MenuLayoutScreen screen, int extraRows, List<BoardPicture> backdrop)
    {
        if (screen.Widget("GO_BACKGROUND") is not { Art.Count: > 0 } plate)
        {
            return;
        }

        var art = new BoardArt(BoardArtLibrary.Ui, plate.Art[0], Math.Max(1, plate.Frames));
        float x = plate.Int("X");
        float y = plate.Int("Y");
        if (extraRows <= 0 || _host.Measure(plate.Art[0]) is not { } size)
        {
            backdrop.Add(new BoardPicture(art, x, y));
            return;
        }

        backdrop.Add(new BoardPicture(art, x, y, Crop: new BoardCrop(0f, 0f, size.Width, GameOptionPlateBandY)));
        for (int i = 0; i <= extraRows; i++)
        {
            backdrop.Add(new BoardPicture(art, x, y + GameOptionPlateBandY + (i * GameOptionPlateBandHeight),
                Crop: new BoardCrop(0f, GameOptionPlateBandY, size.Width, GameOptionPlateBandHeight)));
        }

        float below = GameOptionPlateBandY + GameOptionPlateBandHeight;
        backdrop.Add(new BoardPicture(art, x, y + below + (extraRows * GameOptionPlateBandHeight),
            Crop: new BoardCrop(0f, below, size.Width, size.Height - below)));
    }

    // How many whole bands the plate grows by, one per row past the three the section authors. The
    // count is capped at what the authored canvas still holds under the plate's own corner. A grown
    // plate that ran off the canvas would put its bottom band and the two plaques on it out of
    // sight. The rows past the cap tighten instead (FitGameOptionPitch).
    private int ExtraGameOptionRows(MenuLayoutScreen screen)
    {
        int wanted = Math.Max(0, GameOptions.Length - GameOptionAuthoredRows);
        if (wanted == 0 || screen.Widget("GO_BACKGROUND") is not { Art.Count: > 0 } plate
            || _host.Measure(plate.Art[0]) is not { } size)
        {
            return 0;
        }

        int room = (int)Math.Floor(
            (BoardFit.AuthoredHeight - plate.Int("Y") - size.Height) / (float)GameOptionPlateBandHeight);
        return Math.Clamp(room, 0, wanted);
    }

    private void ComposePlate(MenuLayoutScreen screen, string key, List<BoardPicture> backdrop)
    {
        if (screen.Widget(key) is { Art.Count: > 0 } plate)
        {
            backdrop.Add(new BoardPicture(new BoardArt(BoardArtLibrary.Ui, plate.Art[0], Math.Max(1, plate.Frames)),
                plate.Int("X"), plate.Int("Y")));
        }
    }

    private void ComposePageTitle(MenuLayoutScreen screen, string key, string fallback, List<BoardLine> lines)
    {
        if (screen.Widget(key) is { } title)
        {
            lines.Add(new BoardLine(title.Text ?? fallback, title.Int("X"), title.Int("Y"), title.Int("Width"),
                PageTitleFont, BoardInk.Heading, -1, false,
                title.Int("Justify") == 1 ? BoardJustify.Center : BoardJustify.Left));
        }
    }

    // An open option list as the overlay, drawn by both dropdown pages. It is the panel over the
    // rows the window shows, with each item's words on it. Once the list outruns the window it also
    // draws the two arrows in their state frames and the thumb on its track. The panel runs the
    // authored box's full width, the scroll column included. The rows gave that column up so their
    // words and their marks keep off the chrome, not the panel.
    private BoardPanel ComposeOptionList(OpenDropList drop, int top, IReadOnlyList<OriginalRow> rows, int focus)
    {
        var panelFills = new List<BoardFill>();
        var panelLines = new List<BoardLine>();
        var panelPictures = new List<BoardPicture>();
        float head = float.MaxValue, foot = float.MinValue;
        foreach (var row in rows)
        {
            if (row.Visible && row.Kind == OriginalRowKind.ListRow)
            {
                head = Math.Min(head, row.Y);
                foot = Math.Max(foot, row.Y + row.Height);
            }
        }

        // A dark panel in the plate's own key, not the white one the paper pages open. These pages
        // write in the section's pale text colour, which no white ground would carry.
        if (head < foot)
        {
            panelFills.Add(new BoardFill(drop.X, head, drop.Width, foot - head, 16, 14, 12, 0.94f));
            panelFills.Add(new BoardFill(drop.X, head, drop.Width, foot - head, 200, 190, 170, 1f, Border: true));
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var item = rows[i];
            if (!item.Visible)
            {
                continue;
            }

            if (item.Kind == OriginalRowKind.Button && item.Art != null)
            {
                int frame = item.Enabled ? ComposedBoard.PlaqueFrame(item.Art.Frames, i == focus, i == _host.PressedRow) : 0;
                panelPictures.Add(new BoardPicture(item.Art, item.X, item.Y, frame));
                continue;
            }

            if (i == focus)
            {
                panelFills.Add(new BoardFill(item.X, item.Y, item.Width, item.Height, 255, 255, 255, 0.18f));
            }

            panelLines.Add(new BoardLine(item.Label, item.X + 4f, item.Y + 2f, item.Width - 8f, GameOptionDescFont,
                i == focus ? BoardInk.RowFocused : BoardInk.Row, i));
        }

        if (DropListWindow(drop, top) is { } window && drop.Thumb is { } thumb)
        {
            panelPictures.Add(new BoardPicture(thumb, window.ThumbX, window.ThumbY, Height: window.ThumbHeight));
        }

        return new BoardPanel(panelFills, panelPictures, panelLines);
    }

    // The Game Options rows. An open list's items stand alone while one is open. Otherwise the
    // option controls stand at their authored rows with the two plaques under them, all one column.
    // Without the section the controls stand as text buttons so the page is still walkable.
    private void BuildGameOptionsRows(List<OriginalRow> rows)
    {
        var screen = _layout.Screen(GameOptionsSection);
        if (screen == null)
        {
            for (int i = 0; i < GameOptions.Length; i++)
            {
                var option = GameOptions[i];
                rows.Add(_host.PlaqueRow(option.Key, option.Words[option.Read(this)], i, true, 0));
            }

            rows.Add(_host.PlaqueRow(GameOptionsAcceptKey, "ACCEPT CHANGES", GameOptions.Length, true, 0));
            rows.Add(_host.PlaqueRow(GameOptionsCancelKey, "CANCEL CHANGES", GameOptions.Length + 1, true, 0));
            return;
        }

        if (OpenGameOptionDrop() is { } drop)
        {
            _goListTop = DropListTop(drop, _goListTop, _host.FocusedRow);
            AddDropListRows(drop, _goListTop, rows);
            return;
        }

        BuildGameOptionsControls(screen, ReadGameOptionsPage(screen), rows);
    }

    // The open option's list, or null while none is open. It is that option's words under the
    // authored dropdown's box on its own row, windowed by the TotalDisplayed the section authors.
    private OpenDropList? OpenGameOptionDrop()
    {
        if (_goOpen is not { } key || _layout.Screen(GameOptionsSection) is not { } screen
            || GameOptionFor(key) is not { } open)
        {
            return null;
        }

        return DropList(key, screen.Widget(GameOptionDropKey), open.Words,
            ReadGameOptionsPage(screen).DropBoxFor(IndexOfGameOption(key)));
    }

    // The page's lists for the pointer: an open option list alone, and only once it outruns the
    // authored window.
    private void GameOptionsLists(List<OriginalList> lists)
    {
        if (OpenGameOptionDrop() is { } drop && DropListWindow(drop, _goListTop) is { } window)
        {
            lists.Add(new OriginalList(drop.Key, window, top => _goListTop = ScrollDropList(drop, top)));
        }
    }

    private void BuildGameOptionsControls(MenuLayoutScreen screen, GameOptionsPage page, List<OriginalRow> rows)
    {
        for (int i = 0; i < GameOptions.Length; i++)
        {
            var option = GameOptions[i];
            if (option.Kind == OriginalRowKind.Dropdown)
            {
                var box = page.DropBoxFor(i);
                rows.Add(new OriginalRow(option.Key, option.Words[option.Read(this)], OriginalRowKind.Dropdown,
                    box.X, box.Y, box.Width, box.Height, true, 0, page.Arrow));
                continue;
            }

            var size = StripSize(page.Box, FallbackCheckSize, FallbackCheckSize);
            rows.Add(new OriginalRow(option.Key, string.Empty, OriginalRowKind.Radio,
                page.TitleX + page.CheckDx, page.RowY(i) + page.CheckDy, size.Width, size.Height, true, 0, page.Box));
        }

        AddStrip(screen, rows, GameOptionsAcceptKey, OriginalRowKind.Button, true, 0, page.PlaqueDy);
        AddStrip(screen, rows, GameOptionsCancelKey, OriginalRowKind.Button, true, 0, page.PlaqueDy);
    }

    // The page's row shape off the section's own widgets, each number falling back to the authored
    // one when the row is not there. It is the title column, the first row's line and the pitch
    // between the authored rows. The dropdown box, the checkbox's offset from its row and the
    // description column follow.
    private GameOptionsPage ReadGameOptionsPage(MenuLayoutScreen screen)
    {
        var first = screen.Widget("GO_T_DIFFTITLE");
        var second = screen.Widget("GO_T_VIEWTITLE");
        var third = screen.Widget("GO_T_HEADTITLE");
        var drop = screen.Widget(GameOptionDropKey);
        var box = screen.Widget("GO_B_HEADTURN");
        var description = screen.Widget("GO_T_DIFFDESC");
        float titleX = first?.Int("X", (int)GameOptionTitleX) ?? GameOptionTitleX;
        float firstY = first?.Int("Y", (int)GameOptionFirstY) ?? GameOptionFirstY;
        float pitch = first != null && second != null ? second.Int("Y") - first.Int("Y") : GameOptionPitch;
        float descDy = description != null ? description.Int("Y") - firstY : GameOptionDescDy;
        float dropDy = drop != null ? drop.Int("Y") - firstY : GameOptionDropDy;
        float dropHeight = drop?.Int("ItemHeight", (int)GameOptionItemHeight) ?? GameOptionItemHeight;
        var checkArt = box != null ? StripArt(box.Art, 0, box.Frames) : null;
        float checkDy = box != null && third != null ? box.Int("Y") - third.Int("Y") : GameOptionCheckDy;
        float checkHeight = StripSize(checkArt, FallbackCheckSize, FallbackCheckSize).Height;
        float below = Math.Max(descDy + GameOptionDescFont,
            Math.Max(dropDy + dropHeight, checkDy + checkHeight));
        int extraRows = ExtraGameOptionRows(screen);
        float plaqueDy = extraRows * GameOptionPlateBandHeight;
        var accept = screen.Widget(GameOptionsAcceptKey);
        pitch = pitch > 0f ? pitch : GameOptionPitch;
        var lines = BandedGameOptionLines(
            firstY, pitch, BandInset(screen, firstY), checkHeight, GameOptionAuthoredRows + extraRows);
        if (lines == null || !LinesClearThePlaques(lines, below, accept, plaqueDy))
        {
            lines = TightGameOptionLines(firstY, FitGameOptionPitch(pitch, firstY, below, accept, plaqueDy));
        }

        return new GameOptionsPage(
            titleX,
            first?.Int("Width", (int)GameOptionTitleWidth) ?? GameOptionTitleWidth,
            third?.Int("Width", (int)GameOptionCheckTitleWidth) ?? GameOptionCheckTitleWidth,
            firstY,
            lines,
            drop?.Int("X", (int)GameOptionDropX) ?? GameOptionDropX,
            dropDy,
            drop?.Int("Width", (int)GameOptionDropWidth) ?? GameOptionDropWidth,
            dropHeight,
            box != null ? box.Int("X") - titleX : GameOptionCheckDx,
            checkDy,
            checkHeight,
            description?.Int("X", (int)GameOptionDescX) ?? GameOptionDescX,
            descDy,
            description?.Int("Width", (int)GameOptionDescWidth) ?? GameOptionDescWidth,
            StripArt(drop?.Art ?? Array.Empty<string>(), 4),
            checkArt,
            extraRows);
    }

    // Where each row's own line falls on the plate, or null where the rows do not fit its bands. A
    // dropdown takes a band to itself, its open list standing under the box. The checkbox rows take
    // the bands left over, paired from the top of their run where there are more of them than
    // bands, so the bottom band keeps one row and its description stays clear of the plaques under
    // it. A pair's upper row stands at its band's own top (inset above the authored line) and the
    // lower one a checkbox below, which is the only way two boxes fit a band drawn for one.
    private static float[]? BandedGameOptionLines(
        float firstY, float pitch, float inset, float checkHeight, int bands)
    {
        int drops = 0;
        foreach (var option in GameOptions)
        {
            drops += option.Kind == OriginalRowKind.Dropdown ? 1 : 0;
        }

        int spare = bands - drops;
        if (spare < 1 || GameOptions.Length - drops > 2 * spare)
        {
            return null;
        }

        var lines = new float[GameOptions.Length];
        int pairs = Math.Max(0, GameOptions.Length - drops - spare);
        int band = -1;
        int held = 0;
        int allow = 0;
        for (int i = 0; i < GameOptions.Length; i++)
        {
            bool drop = GameOptions[i].Kind == OriginalRowKind.Dropdown;
            if (drop || held >= allow)
            {
                band++;
                held = 0;
                allow = !drop && pairs > 0 ? 2 : 1;
                pairs -= allow == 2 ? 1 : 0;
            }

            if (band >= bands)
            {
                return null;
            }

            float line = firstY + (band * pitch);
            lines[i] = allow == 1 ? line : line - inset + (held * checkHeight);
            held++;
        }

        return lines;
    }

    // The first authored row's own inset from the top of its band, measured off the plate: the head
    // crop ends one band below that band's own top, so the band opens at the plate's corner plus the
    // difference. Clamped at zero, a layout standing its first row above its own plate stating
    // nothing about where a band opens.
    private static float BandInset(MenuLayoutScreen screen, float firstY) =>
        screen.Widget("GO_BACKGROUND") is { } plate
            ? Math.Max(0f, firstY - plate.Int("Y") - (GameOptionPlateBandY - GameOptionPlateBandHeight))
            : GameOptionBandInset;

    // The rows one under another at a single pitch, the placement a page falls back to when its rows
    // outrun the plate's bands.
    private static float[] TightGameOptionLines(float firstY, float pitch)
    {
        var lines = new float[GameOptions.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = firstY + (i * pitch);
        }

        return lines;
    }

    // Whether every row's reach stops above the moved plaque line. ⚠ Never draw a row over the
    // button: the press regions would overlap and one pointer press would land on two rows.
    private static bool LinesClearThePlaques(float[] lines, float below, MenuLayoutWidget? accept, float plaqueDy)
    {
        if (accept == null)
        {
            return true;
        }

        float limit = accept.Int("Y") + plaqueDy;
        foreach (float line in lines)
        {
            if (line + below > limit)
            {
                return false;
            }
        }

        return true;
    }

    // The authored section carries three rows and this page holds more. The plate grows a whole
    // band per extra row and takes the plaques down with it. The authored pitch stands for as many
    // rows as the canvas holds bands. Past that the rows tighten into the space between the first
    // line and the moved button. Here below is how far a row reaches under its own line, plaqueDy
    // how far the growth took the button down.
    private static float FitGameOptionPitch(
        float authored, float firstY, float below, MenuLayoutWidget? accept, float plaqueDy)
    {
        if (accept == null || GameOptions.Length < 2)
        {
            return authored;
        }

        // Floored to a whole point: the authored pitches are integers and a fractional one would put
        // every row below the first on a half pixel.
        float fit = MathF.Floor((accept.Int("Y") + plaqueDy - firstY - below) / (GameOptions.Length - 1));
        return fit > 0f && fit < authored ? fit : authored;
    }

    private int IndexOfGameOption(string key)
    {
        for (int i = 0; i < GameOptions.Length; i++)
        {
            if (GameOptions[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    // Where a saved view word stands in the Default View dropdown's own list. A word the list does
    // not carry, and a never-set field, read as Chase, which is what an unset opening view flies.
    private static int IndexOfView(string? word)
    {
        var mode = CSVM.Flight.Camera.PilotView.Parse(word ?? string.Empty) ?? CSVM.Flight.Camera.PilotViewMode.Chase;
        for (int i = 0; i < CSVM.Flight.Camera.PilotView.Selectable.Count; i++)
        {
            if (CSVM.Flight.Camera.PilotView.Selectable[i] == mode)
            {
                return i;
            }
        }

        return 0;
    }

    private GameOption? GameOptionFor(string key)
    {
        int at = IndexOfGameOption(key);
        return at >= 0 ? GameOptions[at] : null;
    }

    // What a press does. A list item picks and closes, a dropdown opens its list, a checkbox flips.
    // ACCEPT CHANGES leaves as the apply exit, and CANCEL CHANGES drops the edits and goes back.
    private MenuExit? ActivateGameOptions(OriginalRow row)
    {
        int colon = row.Key.IndexOf(':');
        if (colon > 0 && GameOptionFor(row.Key[..colon]) is { } picked)
        {
            string suffix = row.Key[(colon + 1)..];
            if (suffix is OriginalDropLists.UpSuffix or OriginalDropLists.DownSuffix)
            {
                if (OpenGameOptionDrop() is { } open)
                {
                    _goListTop = ScrollDropList(open, _goListTop + (suffix == OriginalDropLists.UpSuffix ? -1 : 1));
                }

                return null;
            }

            picked.Write(this, int.Parse(suffix, CultureInfo.InvariantCulture));
            _goOpen = null;
            _host.FocusKey(picked.Key);
            return null;
        }

        switch (row.Key)
        {
            case GameOptionsAcceptKey:
                return AppliedOptions();
            case GameOptionsCancelKey:
                BackToPreferences();
                return null;
        }

        if (GameOptionFor(row.Key) is not { } option)
        {
            return null;
        }

        if (option.Kind == OriginalRowKind.Dropdown && _layout.Screen(GameOptionsSection) != null)
        {
            _goOpen = option.Key;
            _goListTop = 0;
            _host.FocusedRow = Math.Max(0, option.Read(this));
            return null;
        }

        option.Write(this, (option.Read(this) + 1) % option.Words.Count);
        return null;
    }

    // A sideways step on a focused option picks the next value with wrap, as the hangar's and the
    // Instant Action screen's dropdowns do. False on anything else, so the step crosses columns.
    private bool StepGameOptionValue(IReadOnlyList<OriginalRow> rows, int focus, int direction)
    {
        if (focus < 0 || focus >= rows.Count || GameOptionFor(rows[focus].Key) is not { } option)
        {
            return false;
        }

        int count = option.Words.Count;
        option.Write(this, ((option.Read(this) + direction) % count + count) % count);
        _host.FocusKey(option.Key);
        return true;
    }

    private bool CloseGameOptionsDropdown()
    {
        if (_goOpen == null)
        {
            return false;
        }

        string key = _goOpen;
        _goOpen = null;
        _host.FocusKey(key);
        return true;
    }

    // Back from the page. An open list closes first, then the page leaves the way CANCEL CHANGES
    // does. GO_B_CANCELCHANGES is the declining answer the layout gives the page.
    private void BackGameOptions()
    {
        if (!CloseGameOptionsDropdown())
        {
            BackToPreferences();
        }
    }

    // The page as drawn. It is the hub's logo, the page's background and its title, then each
    // option's title and description at their authored columns. The controls and an open list as
    // the overlay follow. ⚠ The logo and the plate are backdrop, not pictures, for the reason
    // ComposePreferencesLogo states. A board draws its fills between the two layers, so a plate
    // among the pictures buries every focus mark the rows compose.
    private void ComposeGameOptions(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        ComposePreferencesLogo(layers.Backdrop);
        var screen = _layout.Screen(GameOptionsSection);
        if (screen == null)
        {
            _host.ComposePlainPage("GAME OPTIONS", rows, focus, layers);
            return;
        }

        var page = ReadGameOptionsPage(screen);
        ComposeGameOptionsPlate(screen, page.ExtraRows, layers.Backdrop);
        ComposePageTitle(screen, "GO_T_TITLE", "GAME OPTIONS", layers.Lines);
        for (int i = 0; i < GameOptions.Length; i++)
        {
            var option = GameOptions[i];
            layers.Lines.Add(new BoardLine(option.Title, page.TitleX, page.TitleYFor(i, option.Kind),
                page.TitleWidthFor(option.Kind), GameOptionTitleFont, BoardInk.Row));
            layers.Lines.Add(new BoardLine(option.Description(this), page.DescX, page.DescY(i), page.DescWidth,
                GameOptionDescFont, BoardInk.Row));
        }

        ComposeGameOptionsControls(screen, page, rows, focus, layers);
    }

    // The controls in their states. With a list open the page under it is drawn from the closed
    // controls with the open one focused. The items become the overlay, as the Instant Action
    // screen's own list does.
    private void ComposeGameOptionsControls(
        MenuLayoutScreen screen, GameOptionsPage page, IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        var controls = rows;
        int controlFocus = focus;
        int controlPressed = _host.PressedRow;
        if (_goOpen != null)
        {
            var closed = new List<OriginalRow>();
            BuildGameOptionsControls(screen, page, closed);
            controls = closed;
            controlFocus = IndexOfGameOption(_goOpen);
            controlPressed = -1;
        }

        for (int i = 0; i < controls.Count; i++)
        {
            var control = controls[i];
            if (control.Kind == OriginalRowKind.Radio && control.Art != null)
            {
                // An eight-state strip: the four button states unchecked, then the same four checked.
                int state = control.Enabled ? (i == controlPressed ? 3 : i == controlFocus ? 2 : 1) : 0;
                int frame = (GameOptionFor(control.Key)?.Read(this) == 1 ? 4 : 0) + state;
                layers.Plaques.Add(new BoardPlaque(control.Art, control.X, control.Y, i, frame, string.Empty, BoardInk.LabelNormal));
                continue;
            }

            _host.ComposeGenericRow(control, i == controlFocus, i == controlPressed, i, layers);
        }

        if (OpenGameOptionDrop() is { } drop && rows.Count > 0)
        {
            layers.Overlays.Add(ComposeOptionList(drop, _goListTop, rows, focus));
        }
    }

    // The AUDIO rows: each level's slider on its authored line and the two plaques beside them, all
    // one column. Without the section the levels stand as text buttons carrying their own value.
    // The page is then still walkable and still says what the mix is. The pageless composer draws
    // no slider, which is why the fallback is not one.
    private void BuildAudioRows(List<OriginalRow> rows)
    {
        var screen = _layout.Screen(AudioSection);
        if (screen == null)
        {
            for (int i = 0; i < AudioOptions.Length; i++)
            {
                var fallback = AudioOptions[i];
                rows.Add(_host.PlaqueRow(fallback.Key, $"{fallback.Title} {fallback.Read(this)}", i, true, 0));
            }

            rows.Add(_host.PlaqueRow(AudioAcceptKey, "ACCEPT CHANGES", AudioOptions.Length, true, 0));
            rows.Add(_host.PlaqueRow(AudioCancelKey, "CANCEL CHANGES", AudioOptions.Length + 1, true, 0));
            return;
        }

        BuildAudioControls(screen, rows);
    }

    private void BuildAudioControls(MenuLayoutScreen screen, List<OriginalRow> rows)
    {
        foreach (var option in AudioOptions)
        {
            var place = PlaceAudioRow(screen, option);
            var control = option.ControlKey is { } key ? screen.Widget(key) : null;
            // The moved level is recorded here rather than in the table's own write. The control
            // calls this only where the value actually changed. A drag that held the thumb on the
            // same whole number records nothing, and the host sounds nothing for it.
            rows.Add(SliderRow(control, option.Key, place.SliderX, place.SliderY,
                CSVM.Utils.AudioMix.MinLevel, CSVM.Utils.AudioMix.MaxLevel,
                option.Read(this),
                v =>
                {
                    option.Write(this, v);
                    _audioMoved = option.Level;
                }));
        }

        AddStrip(screen, rows, AudioAcceptKey, OriginalRowKind.Button, true, 0);
        AddStrip(screen, rows, AudioCancelKey, OriginalRowKind.Button, true, 0);
    }

    // The level a row stands at, found through its own table entry so a level and its shipped
    // fallback keep one definition. Every member but None is in the table, so the last answer is
    // unreachable. It is the resting level rather than silence, a preview being no place to invent
    // a mute.
    private int AudioLevel(MenuMixLevel level)
    {
        foreach (var option in AudioOptions)
        {
            if (option.Level == level)
            {
                return option.Read(this);
            }
        }

        return CSVM.Utils.AudioMix.MaxLevel;
    }

    // One row's shape off the section's own widgets, each number falling back to the authored one
    // when the row is not there. The lines are read per row rather than off a first row and a
    // pitch. This section's pitch changes from row to row (docs/org/menu-inventory.md). A single
    // pitch would misplace the rows under the second by up to five pixels each.
    private AudioPlacement PlaceAudioRow(MenuLayoutScreen screen, AudioOption option)
    {
        var title = screen.Widget(option.TitleKey);
        var description = screen.Widget(option.DescriptionKey);
        float titleY = title?.Int("Y", (int)option.TitleY) ?? option.TitleY;
        return new AudioPlacement(
            title?.Int("X", (int)AudioTitleX) ?? AudioTitleX,
            titleY,
            title?.Int("Width", (int)AudioTitleWidth) ?? AudioTitleWidth,
            SliderColumnX(screen),
            titleY + SliderOffset(screen),
            description?.Int("X", (int)AudioDescX) ?? AudioDescX,
            description?.Int("Y", (int)option.DescY) ?? option.DescY,
            description?.Int("Width", (int)AudioDescWidth) ?? AudioDescWidth);
    }

    // The column every slider stands in, and the drop from a title's line to its own slider's, both
    // read off the first authored slider row. They place the Master row, whose own authored widget
    // is the checkbox. A row with a slider of its own is placed by that widget, and reaches these
    // only when the layout has dropped it.
    private float SliderColumnX(MenuLayoutScreen screen) =>
        screen.Widget(AudioSliderRow.ControlKey!)?.Int("X", (int)AudioSliderX) ?? AudioSliderX;

    private float SliderOffset(MenuLayoutScreen screen)
    {
        var title = screen.Widget(AudioSliderRow.TitleKey);
        var slider = screen.Widget(AudioSliderRow.ControlKey!);
        return title != null && slider != null ? slider.Int("Y") - title.Int("Y") : AudioSliderOffset;
    }

    // ACCEPT CHANGES leaves as the apply exit carrying every saved choice. The settings this page
    // does not show ride it unchanged, read back when the page opened. CANCEL CHANGES drops the
    // edits and goes back. A slider row answers nothing. Its value moves under the pointer or by a
    // sideways step, and an Accept that also moved it would have no opposite.
    private MenuExit? ActivateAudio(OriginalRow row)
    {
        switch (row.Key)
        {
            case AudioAcceptKey:
                return AppliedOptions();
            case AudioCancelKey:
                BackToPreferences();
                return null;
        }

        return null;
    }

    // The page as drawn. It is the hub's logo, the page's background and its title. Each level's
    // title and description follow at their authored columns, with the sliders and plaques over
    // them.
    private void ComposeAudio(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        ComposePreferencesLogo(layers.Backdrop);
        var screen = _layout.Screen(AudioSection);
        if (screen == null)
        {
            _host.ComposePlainPage("AUDIO", rows, focus, layers);
            return;
        }

        ComposePlate(screen, "AP_BACKGROUND", layers.Backdrop);
        ComposePageTitle(screen, "AP_T_TITLE", "AUDIO", layers.Lines);

        // The focused row's title in the focused ink, the mark a list row takes. The cursor then
        // shows where the eye already reads the row's name. The rows are built from this table in
        // this order, so a level's index is its row's.
        for (int i = 0; i < AudioOptions.Length; i++)
        {
            var option = AudioOptions[i];
            var place = PlaceAudioRow(screen, option);
            layers.Lines.Add(new BoardLine(option.Title, place.TitleX, place.TitleY, place.TitleWidth,
                AudioTitleFont, i == focus ? BoardInk.RowFocused : BoardInk.Row));
            layers.Lines.Add(new BoardLine(option.Description, place.DescX, place.DescY, place.DescWidth,
                AudioDescFont, BoardInk.Row));
        }

        for (int i = 0; i < rows.Count; i++)
        {
            _host.ComposeGenericRow(rows[i], i == focus, i == _host.PressedRow, i, layers);
        }
    }

    // The size row's description says what the size does under the mode standing beside it. It does
    // something different in each. It is the window's own size, nothing at all, or the size the
    // game draws at inside a fullscreen window Godot will not resize.
    private string ResolutionDescription()
    {
        if (ResolutionPinned)
        {
            return "Borderless runs at the desktop's size. Pick Windowed or Fullscreen to choose one.";
        }

        return _displayMode == CSVM.Utils.DisplayWords.Fullscreen
            ? "Select the size the game draws at, scaled up to fill the screen."
            : "Select the window size.";
    }

    // The graphics row's description says whether a restart is still owed. The mode is resolved
    // once at launch. A choice that differs from the running one reaches the world on the next
    // start, and nothing on the page can show it sooner. A player who saved it and came back would
    // otherwise see the box checked and a world unchanged. They would read that as a failed switch.
    private string GraphicsDescription()
    {
        bool running = CSVM.Utils.GraphicsMode.Enhanced;
        bool chosen = _graphics == CSVM.Utils.GraphicsMode.EnhancedWord;
        return chosen == running
            ? "Select the lit world. Takes effect on the next start."
            : $"Select the lit world. This run is {(running ? "enhanced" : "original")}; restart to apply.";
    }

    // The screen's sizes and the one a saved size it lacks falls back to. They are read through the
    // reader on every access, like the screens below. The list is widened with the size the options
    // file named, so a hand-written one stands on the row where it sorts. The row's value falls
    // back through this list's own fallback, the same word ResolutionSetting.Resolve lands on. The
    // row therefore cannot name a size the window would not be standing at.
    private CSVM.Utils.SizeList Sizes =>
        (_screenSizes?.Invoke() ?? CSVM.Utils.ResolutionSetting.Unknown).Including(_savedResolution);

    // The machine's screens and the one a saved index that names none falls back to. It is read
    // through the reader on every access, since a monitor can be plugged in while the page stands
    // open. The row's value goes through MonitorSetting.Resolve over this, the same call the apply
    // makes. The row therefore cannot show a screen the window would not be moved to.
    private CSVM.Utils.ScreenList Screens => _screens?.Invoke() ?? CSVM.Utils.MonitorSetting.Unknown;

    // The VIDEO rows. An open list's items stand alone while one is open. Otherwise each setting's
    // control stands on its authored row with the two plaques beside them, all one column. Without
    // the section the controls stand as text buttons so the page is still walkable.
    private void BuildVideoRows(List<OriginalRow> rows)
    {
        var screen = _layout.Screen(VideoSection);
        if (screen == null)
        {
            for (int i = 0; i < VideoOptions.Length; i++)
            {
                var fallback = VideoOptions[i];
                rows.Add(_host.PlaqueRow(fallback.Key, fallback.Words(this)[fallback.Read(this)], i,
                    fallback.Editable(this), 0));
            }

            rows.Add(_host.PlaqueRow(VideoAcceptKey, "ACCEPT CHANGES", VideoOptions.Length, true, 0));
            rows.Add(_host.PlaqueRow(VideoCancelKey, "CANCEL CHANGES", VideoOptions.Length + 1, true, 0));
            return;
        }

        if (OpenVideoDrop() is { } drop)
        {
            _vpListTop = DropListTop(drop, _vpListTop, _host.FocusedRow);
            AddDropListRows(drop, _vpListTop, rows);
            return;
        }

        BuildVideoControls(screen, rows);
    }

    // The open setting's list, or null while none is open. It is that row's words under its own
    // authored control box, windowed by the TotalDisplayed that row authors. The Resolution row's
    // words are enumerated per screen and can outrun that window. Every other row's vocabulary
    // fits it.
    private OpenDropList? OpenVideoDrop()
    {
        if (_vpOpen is not { } key || _layout.Screen(VideoSection) is not { } screen
            || VideoOptionFor(key) is not { } option)
        {
            return null;
        }

        var place = PlaceVideoRow(screen, option);
        return DropList(key, screen.Widget(option.ControlKey), option.Words(this),
            (place.BoxX, place.BoxY, place.BoxWidth, place.BoxHeight));
    }

    // The page's lists for the pointer: an open setting's list alone, and only once it outruns the
    // authored window.
    private void VideoLists(List<OriginalList> lists)
    {
        if (OpenVideoDrop() is { } drop && DropListWindow(drop, _vpListTop) is { } window)
        {
            lists.Add(new OriginalList(drop.Key, window, top => _vpListTop = ScrollDropList(drop, top)));
        }
    }

    private void BuildVideoControls(MenuLayoutScreen screen, List<OriginalRow> rows)
    {
        foreach (var option in VideoOptions)
        {
            var place = PlaceVideoRow(screen, option);
            rows.Add(new OriginalRow(option.Key,
                option.Kind == OriginalRowKind.Dropdown ? option.Words(this)[option.Read(this)] : string.Empty,
                option.Kind, place.BoxX, place.BoxY, place.BoxWidth, place.BoxHeight,
                option.Editable(this), 0, place.Box));
        }

        AddStrip(screen, rows, VideoAcceptKey, OriginalRowKind.Button, true, 0);
        AddStrip(screen, rows, VideoCancelKey, OriginalRowKind.Button, true, 0);
    }

    // One row's shape off the section's own widgets, each number falling back to the authored one
    // when the row is not there. Two of them are derived rather than read. The title box stops at
    // the control beside it. Every Video title but the Graphics one is authored the same 162 wide,
    // whatever stands to its right. A description the section gives no width wraps at the plaque
    // column, the two widthless rows being the two the plaques stand beside.
    private VideoPlacement PlaceVideoRow(MenuLayoutScreen screen, VideoOption option)
    {
        var title = screen.Widget(option.TitleKey);
        var control = screen.Widget(option.ControlKey);
        var description = screen.Widget(option.DescriptionKey);
        bool drop = option.Kind == OriginalRowKind.Dropdown;
        float titleX = title?.Int("X", (int)VideoTitleX) ?? VideoTitleX;
        float titleY = title?.Int("Y", (int)VideoShadowsY) ?? VideoShadowsY;
        var box = drop
            ? StripArt(control?.Art ?? Array.Empty<string>(), 4)
            : StripArt(control?.Art ?? Array.Empty<string>(), 0, control?.Frames ?? 8);
        float fallbackX = drop ? VideoDropX : titleX + VideoCheckDx;
        float fallbackY = drop ? titleY : titleY + VideoCheckDy;
        float boxX = control?.Int("X", (int)fallbackX) ?? fallbackX;
        float boxY = control?.Int("Y", (int)fallbackY) ?? fallbackY;
        float boxWidth;
        float boxHeight;
        if (drop)
        {
            boxWidth = control?.Int("Width", (int)VideoDropWidth) ?? VideoDropWidth;
            boxHeight = control?.Int("ItemHeight", (int)VideoItemHeight) ?? VideoItemHeight;
        }
        else
        {
            var size = StripSize(box, FallbackCheckSize, FallbackCheckSize);
            boxWidth = size.Width;
            boxHeight = size.Height;
        }

        float descX = description?.Int("X", (int)VideoDescX) ?? VideoDescX;
        float descY = description?.Int("Y", (int)titleY) ?? titleY;
        float plaqueX = screen.Widget(VideoAcceptKey)?.Int("X", (int)VideoPlaqueX) ?? VideoPlaqueX;
        int authoredDesc = description?.Int("Width") ?? 0;
        return new VideoPlacement(
            titleX,
            titleY,
            Math.Max(1f, Math.Min(title?.Int("Width", (int)VideoTitleWidth) ?? VideoTitleWidth, boxX - titleX)),
            boxX,
            boxY,
            boxWidth,
            boxHeight,
            descX,
            descY,
            authoredDesc > 0 ? authoredDesc : Math.Max(1f, plaqueX - descX),
            box);
    }

    private int IndexOfVideoOption(string key)
    {
        for (int i = 0; i < VideoOptions.Length; i++)
        {
            if (VideoOptions[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    private VideoOption? VideoOptionFor(string key)
    {
        int at = IndexOfVideoOption(key);
        return at >= 0 ? VideoOptions[at] : null;
    }

    // A list item picks and closes, a dropdown opens its list, a checkbox flips. ACCEPT CHANGES
    // leaves as the apply exit carrying every saved choice. The settings this page does not show
    // ride it unchanged, read back when the page opened. CANCEL CHANGES drops the edits and goes
    // back.
    private MenuExit? ActivateVideo(OriginalRow row)
    {
        int colon = row.Key.IndexOf(':');
        if (colon > 0 && VideoOptionFor(row.Key[..colon]) is { } picked)
        {
            string suffix = row.Key[(colon + 1)..];
            if (suffix is OriginalDropLists.UpSuffix or OriginalDropLists.DownSuffix)
            {
                if (OpenVideoDrop() is { } open)
                {
                    _vpListTop = ScrollDropList(open, _vpListTop + (suffix == OriginalDropLists.UpSuffix ? -1 : 1));
                }

                return null;
            }

            picked.Write(this, int.Parse(suffix, CultureInfo.InvariantCulture));
            _vpOpen = null;
            _host.FocusKey(picked.Key);
            return null;
        }

        switch (row.Key)
        {
            case VideoAcceptKey:
                return AppliedOptions();
            case VideoCancelKey:
                BackToPreferences();
                return null;
        }

        if (VideoOptionFor(row.Key) is not { } option)
        {
            return null;
        }

        if (option.Kind == OriginalRowKind.Dropdown && _layout.Screen(VideoSection) != null)
        {
            _vpOpen = option.Key;
            _vpListTop = 0;
            _host.FocusedRow = Math.Max(0, option.Read(this));
            return null;
        }

        option.Write(this, (option.Read(this) + 1) % option.Words(this).Count);
        return null;
    }

    // A sideways step on a focused setting picks the next value with wrap, as the Game Options
    // page's rows do. False on anything else, so the step crosses columns. The Accept and pointer
    // paths read a row's own Enabled flag. This one reads the option behind it, since a dead row
    // must not take a value from a key that never presses it.
    private bool StepVideoValue(IReadOnlyList<OriginalRow> rows, int focus, int direction)
    {
        if (focus < 0 || focus >= rows.Count || VideoOptionFor(rows[focus].Key) is not { } option
            || !option.Editable(this))
        {
            return false;
        }

        option.Write(this, DisplaySettingRows.Step(option.Read(this), direction, option.Words(this).Count));
        _host.FocusKey(option.Key);
        return true;
    }

    private bool CloseVideoDropdown()
    {
        if (_vpOpen == null)
        {
            return false;
        }

        string key = _vpOpen;
        _vpOpen = null;
        _host.FocusKey(key);
        return true;
    }

    // Back from the page. An open list closes first, then the page leaves the way CANCEL CHANGES
    // does. VP_B_CANCELCHANGES is the declining answer the layout gives the page.
    private void BackVideo()
    {
        if (!CloseVideoDropdown())
        {
            BackToPreferences();
        }
    }

    // The page as drawn. It is the hub's logo, the background and the title, then each setting's
    // title and description at their authored columns. The controls stand over them, and an open
    // list is the overlay. ⚠ Keep the logo and the plate in the backdrop, never among the pictures.
    // ComposePreferencesLogo states the reason: art there buries every focus mark the rows compose.
    private void ComposeVideo(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        ComposePreferencesLogo(layers.Backdrop);
        var screen = _layout.Screen(VideoSection);
        if (screen == null)
        {
            _host.ComposePlainPage("VIDEO", rows, focus, layers);
            return;
        }

        ComposePlate(screen, "VP_BACKGROUND", layers.Backdrop);
        ComposePageTitle(screen, "VP_T_TITLE", "VIDEO", layers.Lines);
        foreach (var option in VideoOptions)
        {
            var place = PlaceVideoRow(screen, option);
            layers.Lines.Add(new BoardLine(option.Title, place.TitleX, place.TitleY, place.TitleWidth,
                VideoTitleFont, BoardInk.Row));
            layers.Lines.Add(new BoardLine(option.Description(this), place.DescX, place.DescY, place.DescWidth,
                VideoDescFont, BoardInk.Row));
        }

        ComposeVideoControls(screen, rows, focus, layers);
    }

    // The controls in their states. With a list open the page under it is drawn from the closed
    // controls with the open one focused. The items become the overlay, as the Game Options page's
    // own list does.
    private void ComposeVideoControls(MenuLayoutScreen screen, IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        var controls = rows;
        int controlFocus = focus;
        int controlPressed = _host.PressedRow;
        if (_vpOpen != null)
        {
            var closed = new List<OriginalRow>();
            BuildVideoControls(screen, closed);
            controls = closed;
            controlFocus = IndexOfVideoOption(_vpOpen);
            controlPressed = -1;
        }

        for (int i = 0; i < controls.Count; i++)
        {
            var row = controls[i];
            if (row.Kind == OriginalRowKind.Radio && row.Art != null)
            {
                // An eight-state strip: the four button states unchecked, then the same four checked.
                int state = row.Enabled ? (i == controlPressed ? 3 : i == controlFocus ? 2 : 1) : 0;
                int frame = (VideoOptionFor(row.Key)?.Read(this) == 1 ? 4 : 0) + state;
                layers.Plaques.Add(new BoardPlaque(row.Art, row.X, row.Y, i, frame, string.Empty, BoardInk.LabelNormal));
                continue;
            }

            _host.ComposeGenericRow(row, i == controlFocus, i == controlPressed, i, layers);
        }

        if (OpenVideoDrop() is { } drop && rows.Count > 0)
        {
            layers.Overlays.Add(ComposeOptionList(drop, _vpListTop, rows, focus));
        }
    }

    // Whether a seat is registered at all. Without one the feature holds no keymap to read, so the
    // cells and the two whole-keymap buttons stand disabled rather than drawing somebody's blanks.
    private bool ControlsSeated => _controls is { } controls && controls.Players.Count > 0;

    // Whether the flying-scheme row can be pressed at all. A seat has to be registered, and it has
    // to read the keyboard. A pad-only splitscreen seat holds no mouse to hand the stick.
    private bool MouseSchemeLive => ControlsSeated && _controls!.ReadsKeyboard;

    // The seven tabs: the six named ones over their own flight actions, then every action they
    // leave over. A tab claims each action once, which the module's own unit test pins.
    private static ControlsTab[] BuildKeysTabs()
    {
        var tabs = new ControlsTab[KeysTabNames.Length];
        var claimed = new HashSet<InputAction>();
        for (int i = 0; i < KeysFlightGroups.Length; i++)
        {
            var rows = new List<ControlsTabRow>(KeysFlightGroups[i].Length);
            foreach (var action in KeysFlightGroups[i])
            {
                claimed.Add(action);
                rows.Add(new ControlsTabRow(InputContext.Flight, action));
            }

            tabs[i] = new ControlsTab(KeysTabNames[i], rows);
        }

        var other = new List<ControlsTabRow>();
        foreach (var action in KeysOtherFlightGroup)
        {
            claimed.Add(action);
            other.Add(new ControlsTabRow(InputContext.Flight, action));
        }

        foreach (var context in Enum.GetValues<InputContext>())
        {
            foreach (var action in DefaultBindings.ActionsIn(context))
            {
                if (context != InputContext.Flight || !claimed.Contains(action))
                {
                    other.Add(new ControlsTabRow(context, action));
                }
            }
        }

        tabs[^1] = new ControlsTab(KeysTabNames[^1], other);
        return tabs;
    }

    // A leading [FONTID] tag is a renderer directive the extractor left on this one multi-line row.
    // The newline inside it is the original's own break, which a wrapped line does not need.
    private static string KeysDescription(string text)
    {
        string line = text.Replace('\n', ' ');
        if (line.Length > 2 && line[0] == '[' && line.IndexOf(']') is var close && close > 1)
        {
            line = line[(close + 1)..];
        }

        return line;
    }

    private static int IndexOfAction(ControlsFeature controls, InputAction action)
    {
        var actions = controls.Actions;
        for (int i = 0; i < actions.Count; i++)
        {
            if (actions[i] == action)
            {
                return i;
            }
        }

        return 0;
    }

    private static (int Row, bool Second)? KeysCellOf(string key)
    {
        bool second = key.StartsWith(KeysCellB, StringComparison.Ordinal);
        if (!second && !key.StartsWith(KeysCellA, StringComparison.Ordinal))
        {
            return null;
        }

        return int.TryParse(key[KeysCellA.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int row)
            ? (row, second)
            : null;
    }

    // The CONTROLS page has the seat chooser on the Controller Type row. The flying scheme stands
    // on the Mouse Sensitivity panel's title line with the authored slider under it. The KEYS AND
    // BUTTONS door and the exit pair follow, all one column. Without the section they stand as text
    // buttons so the page is still walkable.
    private void BuildControlsPrefsRows(List<OriginalRow> rows)
    {
        var screen = _layout.Screen(ControlsPrefsSection);
        if (screen == null)
        {
            rows.Add(_host.PlaqueRow(ControlsPlayerKey, PlayerWord(), 0, ControlsSeated, 0));
            rows.Add(_host.PlaqueRow(ControlsMouseKey, MouseWord(), 1, MouseSchemeLive, 0));
            rows.Add(_host.PlaqueRow(ControlsSensitivityKey, SensitivityWord(), 2, MouseSchemeLive, 0));
            rows.Add(_host.PlaqueRow(KeysDoorKey, "KEYS AND BUTTONS", 3, true, 0));
            rows.Add(_host.PlaqueRow(ControlsAcceptKey, "ACCEPT CHANGES", 4, true, 0));
            rows.Add(_host.PlaqueRow(ControlsCancelKey, "CANCEL CHANGES", 5, true, 0));
            return;
        }

        var box = ControlsPlayerBox(screen);
        var arrow = StripArt(screen.Widget(ControlsPlayerKey)?.Art ?? Array.Empty<string>(), 4);
        rows.Add(new OriginalRow(ControlsPlayerKey, PlayerWord(), OriginalRowKind.Dropdown,
            box.X, box.Y, box.Width, box.Height, ControlsSeated, 0, arrow));
        var mouseBox = ControlsMouseBox(screen);
        rows.Add(new OriginalRow(ControlsMouseKey, MouseWord(), OriginalRowKind.Dropdown,
            mouseBox.X, mouseBox.Y, mouseBox.Width, mouseBox.Height, MouseSchemeLive, 0, arrow));
        // The authored slider as the original draws it, over the one scale both presentations step.
        rows.Add(SliderRow(screen.Widget(ControlsSensitivityKey), ControlsSensitivityKey,
            ControlsMouseX, ControlsMouseY, 0, SensitivityScale.MaxLevel,
            SensitivityScale.Level(ControlsSeated ? _controls!.MouseSensitivity : SensitivityScale.Default),
            level =>
            {
                if (MouseSchemeLive)
                {
                    _controls!.MouseSensitivity = SensitivityScale.FromLevel(level);
                }
            },
            MouseSchemeLive));
        AddStrip(screen, rows, KeysDoorKey, OriginalRowKind.Button, true, 0);
        AddStrip(screen, rows, ControlsAcceptKey, OriginalRowKind.Button, true, 0);
        AddStrip(screen, rows, ControlsCancelKey, OriginalRowKind.Button, true, 0);
    }

    // The KEYS AND BUTTONS page's rows in cursor order. The seven tabs stand in their own column.
    // Each action's two control cells are interleaved, so a sideways step crosses from Control A to
    // Control B of the same action. RESET and CANCEL stand under the left column with ACCEPT under
    // the right. A cell outside the list's window keeps its place for the cursor, unseen and unhit.
    private void BuildKeysRows(List<OriginalRow> rows)
    {
        var screen = _layout.Screen(KeysSection);
        var page = ReadKeysPage(screen);
        var tab = KeysTabs[_keysTab];
        for (int i = 0; i < KeysTabs.Length; i++)
        {
            var widget = screen?.Widget(KeysTabKey(i));
            var art = StripArt(widget?.Art ?? Array.Empty<string>(), 0, widget?.Frames ?? 4);
            var size = StripSize(art, FallbackTabWidth, FallbackTabHeight);
            float x = widget?.Int("X", (int)KeysPlateX) ?? KeysPlateX;
            float y = widget?.Int("Y", (int)(KeysHeadY + (i * size.Height))) ?? (KeysHeadY + (i * size.Height));
            rows.Add(new OriginalRow(KeysTabKey(i), KeysTabs[i].Name, OriginalRowKind.TextButton,
                x, y, size.Width, size.Height, true, 0, art));
        }

        int window = page.Rows;
        bool live = ControlsSeated;
        for (int i = 0; i < tab.Rows.Count; i++)
        {
            float y = page.LineY(i, _keysTop);
            bool visible = i >= _keysTop && i < _keysTop + window;
            rows.Add(new OriginalRow(KeysCellKey(i, false), string.Empty, OriginalRowKind.TextButton,
                page.ControlAX, y, page.ControlAWidth, page.ItemHeight, live, 1, null, visible));
            rows.Add(new OriginalRow(KeysCellKey(i, true), string.Empty, OriginalRowKind.TextButton,
                page.ControlBX, y, page.ControlBWidth, page.ItemHeight, live, 2, null, visible));
        }

        if (screen == null)
        {
            rows.Add(_host.PlaqueRow(KeysResetKey, "RESET TO DEFAULT", 0, live, 1));
            rows.Add(_host.PlaqueRow(KeysCancelKey, "CANCEL CHANGES", 1, true, 1));
            rows.Add(_host.PlaqueRow(KeysAcceptKey, "ACCEPT CHANGES", 2, true, 2));
            return;
        }

        AddStrip(screen, rows, KeysResetKey, OriginalRowKind.Button, live, 1);
        AddStrip(screen, rows, KeysCancelKey, OriginalRowKind.Button, true, 1);
        AddStrip(screen, rows, KeysAcceptKey, OriginalRowKind.Button, true, 2);
    }

    // The page's row shape off the section's own widgets, each falling back to the authored number
    // where the row is absent. ⚠ Two authored widths run past the plate. The list's 644 puts its
    // scrollbar at 844, and the Control B column's 174 ends at 815, against a plate ending at 797.
    // Both are clamped to the plate, which is what the page's own still shows.
    private KeysPage ReadKeysPage(MenuLayoutScreen? screen)
    {
        var list = screen?.Widget(KeysListKey);
        var action = screen?.Widget("KB_T_COMMANDTITLE");
        var first = screen?.Widget("KB_T_CONTTITLEA");
        var second = screen?.Widget("KB_T_CONTTITLEB");
        var plate = screen?.Widget("KB_BACKGROUND");
        float plateX = plate?.Int("X", (int)KeysPlateX) ?? KeysPlateX;
        float plateWidth = plate != null && _host.Measure(plate.Art.Count > 0 ? plate.Art[0] : string.Empty) is { } size
            ? size.Width
            : KeysPlateWidth;
        float right = plateX + plateWidth;
        float itemHeight = list?.Int("ItemHeight", (int)KeysItemHeight) ?? KeysItemHeight;
        var bar = StripArt(list?.Art ?? Array.Empty<string>(), 0, 1);
        var arrow = StripArt(list?.Art ?? Array.Empty<string>(), 1);
        var arrowSize = StripSize(arrow, KeysArrowWidth, KeysArrowWidth);
        float controlBX = second?.Int("X", (int)KeysControlBX) ?? KeysControlBX;
        return new KeysPage(
            list?.Int("X", (int)KeysListX) ?? KeysListX,
            list?.Int("Y", (int)KeysListY) ?? KeysListY,
            itemHeight,
            // The heading line naming the category takes the window's first row.
            Math.Max(1, (list?.Int("TotalDisplayed", KeysWindowRows) ?? KeysWindowRows) - 1),
            action?.Int("X", (int)KeysActionX) ?? KeysActionX,
            action?.Int("Width", (int)KeysActionWidth) ?? KeysActionWidth,
            first?.Int("X", (int)KeysControlAX) ?? KeysControlAX,
            first?.Int("Width", (int)KeysControlAWidth) ?? KeysControlAWidth,
            controlBX,
            Math.Min(second?.Int("Width", (int)KeysControlBWidth) ?? KeysControlBWidth, Math.Max(1f, right - controlBX)),
            action?.Int("Y", (int)KeysHeadY) ?? KeysHeadY,
            right - arrowSize.Width,
            arrowSize.Width,
            arrowSize.Height,
            StripSize(bar, arrowSize.Width, 11f).Height);
    }

    // The CONTROLS page's seat chooser at the Controller Type dropdown's own box.
    private (float X, float Y, float Width, float Height) ControlsPlayerBox(MenuLayoutScreen screen)
    {
        var drop = screen.Widget(ControlsPlayerKey);
        return (
            drop?.Int("X", (int)ControlsDropX) ?? ControlsDropX,
            drop?.Int("Y", (int)ControlsDropY) ?? ControlsDropY,
            drop?.Int("Width", (int)ControlsDropWidth) ?? ControlsDropWidth,
            drop?.Int("ItemHeight", (int)ControlsItemHeight) ?? ControlsItemHeight);
    }

    // The CONTROLS page's scheme chooser, on the Mouse Sensitivity title's own line, since the
    // authored slider keeps the panel's only other line. It takes the right half of the Controller
    // Type box's column at that box's item height. Its arrow then ends where the seat row's does,
    // and the panel's title keeps the left half. It stops short of the slider's press region, which
    // reaches ten pixels above the slot.
    private (float X, float Y, float Width, float Height) ControlsMouseBox(MenuLayoutScreen screen)
    {
        var column = ControlsPlayerBox(screen);
        float y = screen.Widget("CP_T_MouseTitle")?.Int("Y", (int)ControlsMouseTitleY) ?? ControlsMouseTitleY;
        float half = column.Width / 2f;
        float sliderTop = ControlsSensitivityBox(screen).Y;
        return (column.X + half, y, half, Math.Max(1f, Math.Min(column.Height, sliderTop - y)));
    }

    // The Mouse Sensitivity slider's authored press region, which is the slider row's own rectangle.
    private (float X, float Y, float Width, float Height) ControlsSensitivityBox(MenuLayoutScreen screen)
    {
        var slider = screen.Widget(ControlsSensitivityKey);
        float x = slider?.Int("X", (int)ControlsMouseX) ?? ControlsMouseX;
        float y = slider?.Int("Y", (int)ControlsMouseY) ?? ControlsMouseY;
        var slot = StripSize(SliderArt(slider, 0, SliderSlotArt), FallbackSlotWidth, FallbackSlotHeight);
        return SliderRegion(slider, x, y, slot);
    }

    private string PlayerWord() => _controls is { } controls && controls.Players.Count > 0
        ? "Player " + controls.Player.ToString(CultureInfo.InvariantCulture)
        : string.Empty;

    private string MouseWord() => !MouseSchemeLive ? string.Empty
        : _controls!.MouseFlying ? "Fly" : "Look";

    private string SensitivityWord() => !MouseSchemeLive ? string.Empty
        : "Sensitivity " + SensitivityScale.Label(_controls!.MouseSensitivity);

    private MenuExit? ActivateControlsPrefs(OriginalRow row)
    {
        switch (row.Key)
        {
            case ControlsPlayerKey:
                StepControlsPlayer(1);
                break;
            case ControlsMouseKey:
                StepMouseScheme();
                break;
            case KeysDoorKey:
                OpenKeys();
                break;
            case ControlsAcceptKey:
                _controls?.Accept();
                BackToPreferences();
                break;
            case ControlsCancelKey:
                _controls?.Cancel();
                BackToPreferences();
                break;
        }

        return null;
    }

    // A press on the KEYS AND BUTTONS page. A pending steal takes the answer first, and a tab
    // stands its category. A cell arms a capture on that control's position. RESET restages this
    // seat's whole keymap from the shipped defaults, and the exit pair writes or drops the visit.
    private MenuExit? ActivateKeys(OriginalRow row)
    {
        if (_controls is not { } controls)
        {
            return null;
        }

        if (controls.Pending != null)
        {
            controls.ConfirmSteal();
            return null;
        }

        for (int i = 0; i < KeysTabs.Length; i++)
        {
            if (row.Key == KeysTabKey(i))
            {
                controls.CancelCapture();
                _keysTab = i;
                _keysTop = 0;
                return null;
            }
        }

        switch (row.Key)
        {
            case KeysResetKey:
                controls.ResetSeat();
                return null;
            case KeysAcceptKey:
                controls.Accept();
                _host.Open(OriginalScreen.ControlsPrefs);
                return null;
            case KeysCancelKey:
                controls.Cancel();
                _host.Open(OriginalScreen.ControlsPrefs);
                return null;
        }

        if (KeysCellOf(row.Key) is not { } cell)
        {
            return null;
        }

        FocusCell(cell.Row, cell.Second);
        controls.BeginCapture();
        return null;
    }

    // Points the feature at the control the cell stands for: the tab's context and action, then the
    // slot the column is. Control B is the second position, which on an action holding one control
    // is the empty slot past it. A press there therefore adds rather than replaces.
    private void FocusCell(int row, bool second)
    {
        if (_controls is not { } controls || row < 0 || row >= KeysTabs[_keysTab].Rows.Count)
        {
            return;
        }

        var cell = KeysTabs[_keysTab].Rows[row];
        controls.Context = cell.Context;
        int index = IndexOfAction(controls, cell.Action);
        controls.Focus(index);
        if (second)
        {
            controls.MoveSlot(1);
        }
    }

    // The seat chooser steps to the next registered player with wrap. A lone seat has nobody to
    // step to, and the row stays where it is.
    private bool StepControlsPlayer(int direction)
    {
        if (_controls is not { } controls || controls.Players.Count < 2)
        {
            return false;
        }

        var players = controls.Players;
        int at = 0;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i] == controls.Player)
            {
                at = i;
            }
        }

        int next = ((at + direction) % players.Count + players.Count) % players.Count;
        controls.Player = players[next];
        _host.FocusKey(ControlsPlayerKey);
        return true;
    }

    // The scheme has two values, so a step either way is the other one. Staged like every other edit
    // on this page: ACCEPT CHANGES is what reaches the seat, and CANCEL CHANGES puts it back.
    private bool StepMouseScheme()
    {
        if (_controls is not { } controls || !MouseSchemeLive)
        {
            return false;
        }

        controls.MouseFlying = !controls.MouseFlying;
        _host.FocusKey(ControlsMouseKey);
        return true;
    }

    // A sideways step on the seat chooser picks the next player and one on the scheme row flips it;
    // anything else crosses columns. The slider is the shell's own step and never reaches here. The
    // text-button page drawn without the section has no slider, so its sensitivity row steps here.
    private bool StepControlsValue(IReadOnlyList<OriginalRow> rows, int focus, int direction)
    {
        if (focus < 0 || focus >= rows.Count)
        {
            return false;
        }

        return rows[focus].Key switch
        {
            ControlsPlayerKey => StepControlsPlayer(direction),
            ControlsMouseKey => StepMouseScheme(),
            ControlsSensitivityKey => StepSensitivity(direction),
            _ => false,
        };
    }

    private bool StepSensitivity(int direction)
    {
        if (_controls is not { } controls || !MouseSchemeLive)
        {
            return false;
        }

        controls.MouseSensitivity = SensitivityScale.Step(controls.MouseSensitivity, direction);
        _host.FocusKey(ControlsSensitivityKey);
        return true;
    }

    // Back from either rebinding page. A pending steal is dropped first, then the page leaves the
    // way its own CANCEL CHANGES does. That is the declining answer the layout gives each of
    // them.
    private void BackControls()
    {
        if (_controls is { Pending: not null } pending)
        {
            pending.DiscardSteal();
            return;
        }

        _controls?.Cancel();
        if (_host.Screen == OriginalScreen.Keys)
        {
            _host.Open(OriginalScreen.ControlsPrefs);
            return;
        }

        BackToPreferences();
    }

    // Keeps the list's window over the cursor. Read off the showing screen's own focus rather than
    // the feature's row, which only moves when a cell is pressed.
    private void SyncKeysWindow()
    {
        var page = ReadKeysPage(_layout.Screen(KeysSection));
        int count = KeysTabs[_keysTab].Rows.Count;
        int last = Math.Max(0, count - page.Rows);
        int top = Math.Clamp(_keysTop, 0, last);
        int focus = _host.FocusedRow - KeysTabCount;
        if (focus >= 0 && focus < count * 2)
        {
            int at = focus / 2;
            if (at < top)
            {
                top = at;
            }
            else if (at >= top + page.Rows)
            {
                top = at - page.Rows + 1;
            }
        }

        _keysTop = Math.Clamp(top, 0, last);
    }

    // The action list as the pointer's wheel and thumb see it, when the standing tab is longer than
    // the window.
    private void KeysLists(List<OriginalList> lists)
    {
        var page = ReadKeysPage(_layout.Screen(KeysSection));
        int count = KeysTabs[_keysTab].Rows.Count;
        if (count <= page.Rows)
        {
            return;
        }

        float top = page.WindowY;
        float height = page.Rows * page.ItemHeight;
        float trackHeight = height - (2f * page.ArrowHeight);
        int at = Math.Clamp(_keysTop, 0, count - page.Rows);
        var window = new ListWindow(
            page.ListX, top, page.BarX + page.ArrowWidth - page.ListX, height,
            page.BarX,
            ListWindow.ThumbYFor(top + page.ArrowHeight, trackHeight, page.ThumbHeight, at, count - page.Rows),
            page.ArrowWidth, page.ThumbHeight,
            top + page.ArrowHeight, trackHeight, count, page.Rows, at);
        lists.Add(new OriginalList(KeysListKey, window, line => _keysTop = line));
    }

    // The CONTROLS page as drawn. The hub's logo and this section's plate are the backdrop, then
    // the page title. The seat and mouse rows carry their own titles and descriptions, and the KEYS
    // AND BUTTONS description stands as authored. The rows follow, the Mouse Sensitivity slider
    // among them in its own slot and thumb.
    private void ComposeControlsPrefs(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        ComposePreferencesLogo(layers.Backdrop);
        var screen = _layout.Screen(ControlsPrefsSection);
        if (screen == null)
        {
            _host.ComposePlainPage("CONTROLS", rows, focus, layers);
            return;
        }

        ComposePlate(screen, "CP_BACKGROUND", layers.Backdrop);
        ComposePageTitle(screen, "CP_T_TITLE", "CONTROLS", layers.Lines);
        var lines = layers.Lines;
        if (screen.Widget("CP_T_JoystickTitle") is { } title)
        {
            lines.Add(new BoardLine("Player", title.Int("X", (int)ControlsTitleX), title.Int("Y"),
                title.Int("Width", (int)ControlsTitleWidth), GameOptionTitleFont, BoardInk.Row));
        }

        if (screen.Widget("CP_T_JoystickDesc") is { } description)
        {
            lines.Add(new BoardLine(
                "Whose keymap KEYS AND BUTTONS edits. Each seat holds its own.",
                description.Int("X", (int)ControlsDescX), description.Int("Y"),
                description.Int("Width", (int)ControlsDescWidth), KeysDescFont, BoardInk.Row));
        }

        if (screen.Widget("CP_T_MouseTitle") is { } mouseTitle)
        {
            // The title stops where the scheme chooser on its line starts.
            float titleX = mouseTitle.Int("X", (int)ControlsTitleX);
            float titleWidth = Math.Min(mouseTitle.Int("Width", (int)ControlsTitleWidth), ControlsMouseBox(screen).X - titleX);
            lines.Add(new BoardLine("Mouse", titleX, mouseTitle.Int("Y", (int)ControlsMouseTitleY),
                Math.Max(1f, titleWidth), GameOptionTitleFont, BoardInk.Row));
        }

        if (screen.Widget("CP_T_MouseDesc") is { } mouseDescription)
        {
            lines.Add(new BoardLine(
                "Fly steers the aeroplane with the mouse, Look turns the head. The slider sets how "
                + "fast a flying mouse moves the stick.",
                mouseDescription.Int("X", (int)ControlsDescX),
                mouseDescription.Int("Y", (int)ControlsMouseDescY),
                mouseDescription.Int("Width", (int)ControlsDescWidth), KeysDescFont, BoardInk.Row));
        }

        if (screen.Widget("CP_T_KeysDesc") is { } keys)
        {
            lines.Add(new BoardLine(KeysDescription(keys.Text ?? string.Empty),
                keys.Int("X"), keys.Int("Y"), keys.Int("Width"), KeysDescFont, BoardInk.Row));
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Kind == OriginalRowKind.Button && row.Art != null)
            {
                layers.Plaques.Add(new BoardPlaque(row.Art, row.X, row.Y, i,
                    row.Enabled ? ComposedBoard.PlaqueFrame(row.Art.Frames, i == focus, i == _host.PressedRow) : 0,
                    string.Empty, BoardInk.LabelNormal));
                continue;
            }

            _host.ComposeGenericRow(row, i == focus, i == _host.PressedRow, i, layers);
        }
    }

    // The KEYS AND BUTTONS page as drawn. It is the plate, the title, the three column heads, and
    // the tab strip with the standing tab in its depressed frame. The category heading stands over
    // its rows in the list's window, beside the two control columns. The instruction line closes
    // it, replaced by the status while the feature has something to say.
    private void ComposeKeys(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        ComposePreferencesLogo(layers.Backdrop);
        var screen = _layout.Screen(KeysSection);
        if (screen == null)
        {
            _host.ComposePlainPage("KEYS AND BUTTONS", rows, focus, layers);
            return;
        }

        ComposePlate(screen, "KB_BACKGROUND", layers.Backdrop);
        ComposePageTitle(screen, "KB_T_TITLE", "KEYS AND BUTTONS", layers.Lines);
        var lines = layers.Lines;
        var page = ReadKeysPage(screen);
        foreach (var (key, fallbackX, fallbackWidth) in new[]
        {
            ("KB_T_COMMANDTITLE", page.ActionX, page.ActionWidth),
            ("KB_T_CONTTITLEA", page.ControlAX, page.ControlAWidth),
            ("KB_T_CONTTITLEB", page.ControlBX, page.ControlBWidth),
        })
        {
            if (screen.Widget(key) is { } head)
            {
                lines.Add(new BoardLine(head.Text ?? string.Empty, fallbackX, head.Int("Y", (int)page.HeadY),
                    fallbackWidth, KeysHeadFont, BoardInk.Row, -1, false, BoardJustify.Left, Bold: true));
            }
        }

        var tab = KeysTabs[_keysTab];
        lines.Add(new BoardLine(tab.Name, page.ActionX, page.ListY, page.ActionWidth, KeysRowFont,
            BoardInk.RowFocused, -1, false, BoardJustify.Left, Bold: true));
        for (int i = _keysTop; i < tab.Rows.Count && i < _keysTop + page.Rows; i++)
        {
            float y = page.LineY(i, _keysTop);
            lines.Add(new BoardLine(BindingLabels.Name(tab.Rows[i].Action), page.ActionX + KeysRowIndent, y,
                page.ActionWidth - KeysRowIndent, KeysRowFont, BoardInk.Row));
            var text = KeysCellText(i);
            lines.Add(new BoardLine(text.A, page.ControlAX, y, page.ControlAWidth, KeysRowFont, BoardInk.Row));
            lines.Add(new BoardLine(text.B, page.ControlBX, y, page.ControlBWidth, KeysRowFont, BoardInk.Row));
        }

        ComposeKeysRows(rows, focus, layers);
        ComposeKeysBar(page, tab.Rows.Count, layers, screen);
        if (screen.Widget("KB_T_DEFAULTDESC") is { } instruction)
        {
            string status = _controls?.Status ?? string.Empty;
            lines.Add(new BoardLine(
                status.Length > 0 ? status : KeysDescription(instruction.Text ?? string.Empty),
                instruction.Int("X"), instruction.Int("Y"), instruction.Int("Width"), KeysDescFont, BoardInk.Detail));
        }
    }

    // The page's own rows. The tabs and the three buttons draw as plaques, the standing tab in the
    // depressed frame it never leaves. The focused control cell is marked with the focus box.
    private void ComposeKeysRows(IReadOnlyList<OriginalRow> rows, int focus, BoardLayers layers)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Art != null && row.Kind == OriginalRowKind.TextButton)
            {
                int frame = row.Key == KeysTabKey(_keysTab)
                    ? 3
                    : ComposedBoard.PlaqueFrame(row.Art.Frames, i == focus, i == _host.PressedRow);
                // A tab's label takes the page's own text pair rather than the paper plaque's.
                // The strip authors ColorActive as the description cream and ColorRollover as
                // the file-wide ACTIVE. Those two palette roles already carry exactly that.
                layers.Plaques.Add(new BoardPlaque(row.Art, row.X, row.Y, i, frame, row.Label,
                    i == focus || i == _host.PressedRow ? BoardInk.RowFocused : BoardInk.Row));
                continue;
            }

            if (row.Art != null && row.Kind == OriginalRowKind.Button)
            {
                layers.Plaques.Add(new BoardPlaque(row.Art, row.X, row.Y, i,
                    row.Enabled ? ComposedBoard.PlaqueFrame(row.Art.Frames, i == focus, i == _host.PressedRow) : 0,
                    string.Empty, BoardInk.LabelNormal));
                continue;
            }

            if (i == focus && row.Visible && row.Enabled)
            {
                layers.Fills.Add(new BoardFill(row.X, row.Y, row.Width, row.Height, 0, 0, 0, 0.10f));
                layers.Fills.Add(_host.FocusMark(row));
            }
        }
    }

    // The list's scrollbar, at the plate's own right edge rather than at the authored list width,
    // which stands past it. Nothing is drawn while the standing tab fits its window.
    private void ComposeKeysBar(KeysPage page, int count, BoardLayers layers, MenuLayoutScreen screen)
    {
        if (count <= page.Rows)
        {
            return;
        }

        var list = screen.Widget(KeysListKey);
        var up = StripArt(list?.Art ?? Array.Empty<string>(), 1);
        var down = StripArt(list?.Art ?? Array.Empty<string>(), 2);
        var bar = StripArt(list?.Art ?? Array.Empty<string>(), 0, 1);
        float top = page.WindowY;
        float trackHeight = (page.Rows * page.ItemHeight) - (2f * page.ArrowHeight);
        float thumbY = ListWindow.ThumbYFor(top + page.ArrowHeight, trackHeight, page.ThumbHeight,
            Math.Clamp(_keysTop, 0, count - page.Rows), count - page.Rows);
        if (up != null && down != null && bar != null
            && _host.Measure(up.Name) != null && _host.Measure(down.Name) != null && _host.Measure(bar.Name) != null)
        {
            layers.Pictures.Add(new BoardPicture(up, page.BarX, top));
            layers.Pictures.Add(new BoardPicture(down, page.BarX, top + (page.Rows * page.ItemHeight) - page.ArrowHeight));
            layers.Pictures.Add(new BoardPicture(bar, page.BarX, thumbY));
            return;
        }

        layers.Fills.Add(new BoardFill(page.BarX, top, page.ArrowWidth, page.Rows * page.ItemHeight, 255, 255, 255, 0.3f, Border: true));
        layers.Fills.Add(new BoardFill(page.BarX, thumbY, page.ArrowWidth, page.ThumbHeight, 255, 255, 255, 0.6f));
    }

    /// <summary>One row of a category tab: which keymap it belongs to and which action it names.
    /// The two travel together because the seven tabs are the original's action groups and this
    /// port holds three keymaps. One tab can therefore list rows from more than one of them.</summary>
    public readonly record struct ControlsTabRow(InputContext Context, InputAction Action);

    /// <summary>One category tab of the KEYS AND BUTTONS page: the word on its button and the rows
    /// it lists.</summary>
    public sealed record ControlsTab(string Name, IReadOnlyList<ControlsTabRow> Rows);

    // One option of the Game Options page. It carries its title and its description, read off the
    // module since a row can say something about its saved state. The control it takes, the words
    // of the store field it shows, and how that field is read and written follow.
    private sealed record GameOption(
        string Key, string Title, Func<OriginalOptionsScreen, string> Description, OriginalRowKind Kind,
        IReadOnlyList<string> Words,
        Func<OriginalOptionsScreen, int> Read, Action<OriginalOptionsScreen, int> Write);

    // The Game Options page's row shape in authored pixels, every number off the section's own
    // widgets. It is the title column, the first row's line and every row's own line. The
    // dropdown box, the checkbox's offset from its row and its own height, the description column
    // and the two controls' strips follow.
    private sealed record GameOptionsPage(
        float TitleX, float TitleWidth, float CheckTitleWidth, float FirstY, IReadOnlyList<float> Lines,
        float DropX, float DropDy, float DropWidth, float ItemHeight,
        float CheckDx, float CheckDy, float CheckHeight, float DescX, float DescDy, float DescWidth,
        BoardArt? Arrow, BoardArt? Box, int ExtraRows)
    {
        // How far the plate's growth took the two plaques and everything else standing on its
        // bottom band down from their authored line.
        public float PlaqueDy => ExtraRows * GameOptionPlateBandHeight;

        public float RowY(int row) => Lines[Math.Clamp(row, 0, Lines.Count - 1)];

        // Where a row's description stands in the column beside it. That column is one window rather
        // than a row of panels, so the paragraphs are spread evenly from the first row's own line to
        // the last row's instead of crowding where two rows share a band; each still stands beside
        // its own control. With every row on a band of its own this is the authored offset again.
        public float DescY(int row)
        {
            float first = RowY(0) + DescDy;
            float last = RowY(Lines.Count - 1) + DescDy;
            return Lines.Count < 2
                ? first
                : MathF.Floor(first + ((last - first) * Math.Clamp(row, 0, Lines.Count - 1) / (Lines.Count - 1)));
        }

        // Where a row's own words stand. A dropdown row's title keeps the row's line, its box
        // opening under it. A checkbox row's title stands on its box's centre line instead, the box
        // being taller than the face and standing beside the words. That is what the VIDEO section
        // authors for the same pair, its clutter title seven pixels under a 29-pixel box against a
        // 14-pixel face. A line is drawn from the top of its own em box, so half the difference
        // between the two centres it.
        public float TitleYFor(int row, OriginalRowKind kind) =>
            kind == OriginalRowKind.Radio
                ? RowY(row) + CheckDy + MathF.Floor((CheckHeight - GameOptionTitleFont) / 2f)
                : RowY(row);

        // A checkbox row takes the head-turn row's own narrower title box, which leaves the box
        // beside it clear of the words. A dropdown row takes the wide one.
        public float TitleWidthFor(OriginalRowKind kind) =>
            kind == OriginalRowKind.Radio ? CheckTitleWidth : TitleWidth;

        public (float X, float Y, float Width, float Height) DropBoxFor(int row) =>
            (DropX, RowY(Math.Max(0, row)) + DropDy, DropWidth, ItemHeight);
    }

    // One level of the AUDIO page, by its key and which of the mix's levels it is for the host
    // that hears one move. Its title and the authored widgets it composes over: the title, the
    // slider and the description, the slider null where the row has none. Its description, the
    // authored lines the title and description fall back to, and how the store field is read and
    // written follow. The description is a fixed string. No level says anything about its saved
    // state that the thumb does not already show, unlike the VIDEO page's graphics row.
    private sealed record AudioOption(
        string Key, MenuMixLevel Level, string Title, string TitleKey, string? ControlKey,
        string DescriptionKey, string Description, float TitleY, float DescY,
        Func<OriginalOptionsScreen, int> Read, Action<OriginalOptionsScreen, int> Write);

    // One AUDIO row's place in authored pixels. It is the title box, the corner its slider stands
    // at where the row authors no slider of its own, and the description box.
    private sealed record AudioPlacement(
        float TitleX, float TitleY, float TitleWidth,
        float SliderX, float SliderY,
        float DescX, float DescY, float DescWidth);

    // One setting of the VIDEO page, by its title and the authored widgets it composes over: the
    // title, the control and the description. Its description is read off the module, since a row
    // can say something about its saved state. The control it takes, the words of the store field
    // it shows, how it is read and written, and whether the row is live. The words come off the
    // module too, the resolution row's being enumerated per screen rather than held as an array.
    private sealed record VideoOption(
        string Key, string Title, string TitleKey, string ControlKey, string DescriptionKey,
        Func<OriginalOptionsScreen, string> Description, OriginalRowKind Kind,
        Func<OriginalOptionsScreen, IReadOnlyList<string>> Words,
        Func<OriginalOptionsScreen, int> Read, Action<OriginalOptionsScreen, int> Write,
        Func<OriginalOptionsScreen, bool>? Live = null)
    {
        // Whether the row takes a press at all. A row another setting owns the value of is dead.
        // The cursor walks past it, the pointer cannot arm it, and it draws in its disabled frame.
        // Every other unavailable row on these pages already does that.
        public bool Editable(OriginalOptionsScreen options) => Live?.Invoke(options) ?? true;
    }

    // One VIDEO row's place in authored pixels: the title box, the control's own rectangle and
    // strip, and the description box.
    private sealed record VideoPlacement(
        float TitleX, float TitleY, float TitleWidth,
        float BoxX, float BoxY, float BoxWidth, float BoxHeight,
        float DescX, float DescY, float DescWidth, BoardArt? Box);

    // The KEYS AND BUTTONS page's row shape in authored pixels. It is the list's corner, its pitch,
    // and the rows it shows under the category heading. The three columns, the head line, and the
    // scrollbar's own column at the plate's right edge follow.
    private sealed record KeysPage(
        float ListX, float ListY, float ItemHeight, int Rows,
        float ActionX, float ActionWidth, float ControlAX, float ControlAWidth,
        float ControlBX, float ControlBWidth, float HeadY,
        float BarX, float ArrowWidth, float ArrowHeight, float ThumbHeight)
    {
        // The window's own first line, where the scrollbar stands. ⚠ Do not anchor the bar at
        // LineY(0, top). A scrolled-away first row's line sits above the window. The bar would then
        // slide up with the list instead of holding still under its thumb.
        public float WindowY => ListY + ItemHeight;

        // The category heading stands on the window's first line, so a row sits one line below it.
        public float LineY(int row, int top) => ListY + ((row - top + 1) * ItemHeight);
    }
}
