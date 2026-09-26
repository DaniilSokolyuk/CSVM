using System;
using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Flight.Camera;
using CSVM.Flight.Hud;
using CSVM.Mech3;
using CSVM.Mech3.Anim;
using CSVM.Session.Roster;
using CSVM.Utils;
using Godot;

namespace CSVM.Session.World;

/// <summary>
/// The host a cutscene definition's <c>CALLBACK</c> codes are dispatched to, and the session-side
/// state those codes describe: presentation on, the player out of flight, the world and the
/// objectives held, the AI parked, then one hard cut back to gameplay. The bars and the camera path
/// are the definition's own data (the <c>letterbox</c> node pinned to <c>camera1</c>); what lives
/// here is the state no definition can assert by itself. Decode:
/// docs/formats/anim-definitions/cutscenes.md.
/// </summary>
public sealed partial class CutsceneController : Node
{
    /// <summary>The gamez node a cutscene definition poses, and the pose every rig camera mirrors
    /// while one plays. Bodiless in the gamez, so the world build stands a marker in for it.</summary>
    public const string CameraNode = CanopyHoleCue.ViewCameraNode;

    /// <summary>The gamez node carrying the two black bars, activated by the shared
    /// <c>letterbox</c> definition and pinned to <see cref="CameraNode"/> every tick.</summary>
    public const string BarsNode = "letterbox";

    /// <summary>The code that marks a definition as the running cutscene: the original's host
    /// stores the animation raising it in the active-cutscene slot, which is what suspends the
    /// world.</summary>
    public const int CodeHoldsWorld = 20;

    /// <summary>The code that takes the player out of flight, and with them the pose half: the
    /// flown airframe rides the staged <c>player</c> marker while it holds. Public because a suite
    /// driving an intro over a world built before any rig existed has to re-raise it, the way
    /// <see cref="BindRigs"/> re-applies the state for a session.</summary>
    public const int CodeOutOfFlight = 11;

    /// <summary>How far the card is made to overhang the pane it covers. Both fits are equalities
    /// without it, the height on every ratio and the stretched width on anything wider than the
    /// card, so the card's outer edge would land on the frame edge and the world show through the
    /// boundary column under MSAA and projection rounding. TUNE: the original's own framing fov is
    /// undecoded, so this is a margin clear of that boundary, not a decoded figure.</summary>
    public const float CardOverscan = 0.02f;

    /// <summary>The definitions a story mission's start list plays as its opening movie: the
    /// bespoke C1/M04 intro, the one the other twelve share, and C3/M03's cargo zeppelin camera,
    /// which no list names (its start anim <c>calldestroy_the_cargozep</c> calls it). ⚠ The
    /// authored codes do NOT identify a cutscene: <c>player_setup</c>, which every mission opening
    /// without a movie bootstraps, raises the same nine, so a code test would give every mission a
    /// letterbox and a held world (docs/formats/anim-definitions/cutscenes.md).</summary>
    public static readonly string[] IntroAnims =
        { "mission_intro_animation", "generic_intro", "cgzep_camera" };

    /// <summary>Raised whenever the world hold changes, so the session can suspend the mission
    /// director alongside its own per-step world update (code 20 stops both).</summary>
    public Action<bool>? WorldHeld;

    /// <summary>Raised as an episode takes the window and again as it gives it back, so a
    /// splitscreen session can play the cutscene across the whole window rather than in N small
    /// copies of one camera path. Both exits (the definition ending and a skip) go through the
    /// restore, so both hand it back; <see cref="BindRigs"/> re-raises it for an episode that
    /// started before the panes existed, which is every mission intro.</summary>
    public Action<bool>? FillsWindow;

    /// <summary>Puts the player into the airframe codes 965 to 967 name, and carries out whatever
    /// else the raised code asks of the mission (<see cref="AirframeHandover"/>). The session fills
    /// this in with its roster's own swap; unbound, the three codes are hosted and counted rather
    /// than reaching an aircraft, which is what a session with no rigs wants. A result reporting no
    /// swap says no aircraft changed.</summary>
    public Func<AirframeSwapOrder, AirframeSwapResult>? SwapAirframe;

    /// <summary>Code 13, which the original answers with the call its objectives runtime makes when
    /// a primary completes and then the mission-end path. Every mission that ends by docking on a
    /// zeppelin's hook ends on this code and on nothing else: no objective in the shipped data
    /// completes on a landing, so unbound the docking plays out and the mission simply carries on.
    /// Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
    public Action? MissionComplete;

    /// <summary>Code 86, which the original answers by walking every weapon type's live-round list
    /// and despawning each round where it flies, with no detonation and no impact effect. The one
    /// definition that raises it is the hangar drop that also swaps the airframe, so the air is
    /// cleared of what the outgoing aeroplane fired. Decode:
    /// docs/formats/anim-definitions/cutscenes.md.</summary>
    public Action? ClearOrdnance;

    /// <summary>Has the flown mission's ending already landed? An episode whose mission ended
    /// under it keeps its shot: no handoff and no skip, so the leaving fade runs over the film's
    /// last frame the way the original's does (docs/formats/objectives.md, "The mission-end
    /// path"). Unbound, every episode hands off at its definition's end.</summary>
    public Func<bool>? EndingLanded;

    // The rest of the mission-script host's codes the intro definitions author. Each is the whole
    // message: the definition it sits in never qualifies it
    // (docs/formats/anim-definitions/cutscenes.md).
    private const int CodeHandoff = 1;
    private const int CodePresentation = 2;
    private const int CodeRestoreSystems = 10;
    private const int CodeMissionComplete = 13;
    private const int CodeClearOrdnance = 86;
    private const int CodeCamParamsFree = 666;
    private const int CodeCamParamsRestore = 667;
    private const int CodeParkAi = 913;
    private const int CodeRevealAi = 914;
    private const int CodeReplacePlayer = 951;

    // ANIM_STATE's RUNNING, the value AnimRuntime.AnimStateOf reports while an instance is live.
    private const int AnimRunning = 2;

    // The two codes that reach no case in the original's own host either. Answered as named gaps so
    // a census can tell "authored and ignored" from "never authored".
    private static readonly int[] NamedGaps = { 14, 123 };

    // The gameplay end state, in the order C1/M04's own RESET_STATE authors it: control and chrome
    // back, the in-flight systems back, the AI revealed, the camera-parameter gate closed.
    private static readonly int[] RestoreCodes =
        { CodeHandoff, CodeRestoreSystems, CodeRevealAi, CodeCamParamsRestore };

    private readonly HashSet<string> _hosted = new(StringComparer.Ordinal);
    // Every definition of the current episode that authors a code, or has raised one. The
    // original's player flags are written by the codes alone (11 takes flight away, 1 gives it
    // back) and never by a definition ending, so the end-of-definition handoff below waits for
    // these to finish as well: CM06's docking row ends with its unhook, whose handoff code comes
    // at its own end, still playing.
    private readonly HashSet<string> _raisers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<int> _codes = new();
    private readonly List<FlightController> _parked = new();
    private readonly HashSet<int> _gapsLogged = new();
    private readonly Dictionary<Camera3D, float> _fov = new();
    // The fast-forward a held key applies to an episode the player may not skip, handed to the
    // runtime at the bind and scoped per episode below.
    private readonly CutsceneFastForward _fastForward = new();
    // What is down right now, as the session's own input saw it go down. Confirmed against the
    // device every tick rather than waiting for a release: a key-up consumed elsewhere would
    // otherwise leave the picture running at the raised rate for the rest of the episode.
    private readonly HashSet<Key> _keysDown = new();
    private readonly HashSet<(int Device, JoyButton Button)> _padsDown = new();
    private bool _scriptedHold;
    // The stick's skip, re-read through its own reader each tick like the keys above; null while
    // no stick is holding the fast-forward.
    private Func<bool>? _stickHeld;
    // Is the AI held by the mission start's own park rather than by a playing definition? The lift
    // is the bootstrap definition's reset, so this says which of the two owns the release.
    private bool _startParked;
    private bool _fastForwardLogged;
    private AnimRuntime? _runtime;
    private IReadOnlyList<PlayerRig> _rigs = Array.Empty<PlayerRig>();
    private Func<IReadOnlyList<FlightController>>? _aiPlanes;
    private Node3D? _cutsceneCamera;
    // The node the code being dispatched was raised from, which is the aircraft a capture
    // definition belongs to. Set per dispatch, not per episode: a called definition raises its own
    // codes off its own root.
    private string? _codeRoot;
    // The landings slot: the row definition the trigger has just started, owning the next episode
    // whichever callee raises its first code. Null once that episode has taken it.
    private string? _owner;
    // The human whose trigger claimed that slot, taken with it. Null where the trigger named none.
    private PlayerRig? _ownerRig;
    // The episode owner, latched when the episode took the session: the human the swap and the
    // staging follow. Null means the scripted player, resolved live below rather than at the claim,
    // because a suite (and the intro) can own an episode before BindRigs has run. Latch it as each
    // episode takes the session, not at handoff, so a swap remains the aircraft a hookup resolves.
    private PlayerRig? _episodeOwner;
    // The world root `camera1` belongs under, so Restore can undo a definition's own reparent.
    private Node3D? _cameraHome;
    private Node3D? _bars;
    // The staged `player` marker an intro poses, or null in a session with no aircraft stage, and
    // the world root it was built under, so Restore can undo a definition's own reparent of it.
    private Node3D? _playerMarker;
    private Node3D? _markerHome;
    private AircraftStage? _aircraft;
    private Node3D? _card;
    private Aabb _cardBox;
    private MeshInstance3D? _cardMesh;
    // The bars node's authored scale, read alongside the card measurement and for the same reason:
    // PinBars re-asserts a pose every tick, and reading the scale back off a posed node would let
    // it compound. The pane's width factor rides on top of it, recomputed each frame from the live
    // viewport for the same reason.
    private Vector3 _barsScale = Vector3.One;
    private float _barsWidth = 1f;

    // BL-452 watchdog state (WatchBars): the bars' visibility as of the last tick, and how many
    // times it has flipped in the current episode.
    private bool _lastBarsVisible;
    private int _barsFlipsThisEpisode;

    /// <summary>Constructs the host. It ticks LAST in the frame so the rig cameras take the pose
    /// this frame's animation advance put <c>camera1</c> in; a tick before that advance would show
    /// the bars, posed inside it, against a camera one frame behind them.</summary>
    public CutsceneController()
    {
        Name = "CutsceneController";
        ProcessPriority = 1000;
    }

    /// <summary>Whether a cutscene definition currently owns the session.</summary>
    public bool Playing { get; private set; }

    /// <summary>Whether the world and the objectives update are held (code 20). The session's drive
    /// paths read this; nothing clears it but the handoff or a skip.</summary>
    public bool HoldsWorld { get; private set; }

    /// <summary>Does this episode offer the player a skip? Armed by the hold code and disarmed at
    /// the handoff, which is the original's own active-cutscene slot: a mid-mission definition
    /// that never raises that code is played out, and the key press belongs to the game.
    /// Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
    public bool Skippable { get; private set; }

    /// <summary>Whether the chrome is hidden and the view is off the aircraft (code 2).</summary>
    public bool Presenting { get; private set; }

    /// <summary>Has an episode's camera been handed to the rigs at least once? The session's start
    /// cover holds until this, so an intro's first shot and not the chase view is the first frame
    /// of a mission. Latched for the process: a later episode starts from gameplay, which is
    /// already a frame the player was meant to see.</summary>
    public bool CameraPosed { get; private set; }

    /// <summary>Whether this episode is being kept past its own definition's end because the
    /// mission ended under it (<see cref="EndingLanded"/>). The shot the leaving fade ramps over
    /// is the film's while this is true.</summary>
    public bool HeldForEnding { get; private set; }

    /// <summary>Whether the player is out of flight: input suspended, the airframe pinned and
    /// undrawn, engine audio released (code 11).</summary>
    public bool OutOfFlight { get; private set; }

    /// <summary>The rate this episode's own definitions are running at, 1 at real speed. A
    /// remake-only rule, offered where <see cref="Skippable"/> is false, so a scene the original
    /// plays out in full can be held through rather than cut short.</summary>
    public float FastForwardRate => _fastForward.Rate;

    /// <summary>Whether the AI vehicles are parked (code 913), the state code 914 reverses.</summary>
    public bool AiParked { get; private set; }

    /// <summary>Whether the camera-parameter profile is left to the definition (code 666). CSVM
    /// applies that profile once when a rig is built and never on a view change, so there is no
    /// live applier for this to suppress; the gate is tracked, not acted on.</summary>
    public bool CamParamsFree { get; private set; }

    /// <summary>Every code the current (or last) episode hosted, in the order it was dispatched,
    /// which is what a suite asserts the shape of the cutscene by.</summary>
    public IReadOnlyList<int> Codes => _codes;

    /// <summary>The definition the episode belongs to, whose end hands off: the one the landings
    /// trigger started (<see cref="Own"/>), or else the one that raised the first code. ⚠ Not
    /// always the raiser: CM06's docking row raises no code itself and calls the definitions that
    /// do, and the first of those ends with the aeroplane still on the hook.</summary>
    public string? Anim { get; private set; }

    /// <summary>The human this episode belongs to: the one whose mission trigger started it
    /// (<see cref="Own"/>), or the scripted player where no trigger named one, which is every
    /// mission intro. The airframe swap rebuilds THIS rig, so a guest who flies the capture cone
    /// ends up in the captured aeroplane rather than handing it to P1. Null only in a session with
    /// no rigs bound.</summary>
    public PlayerRig? EpisodeOwner => _episodeOwner ?? ScriptedPlayer;

    /// <summary>The letterbox card's extent as <see cref="BindWorld"/> measured it, in the bars
    /// root's own frame. Read-only, and measured once: see the note in <c>BindWorld</c>.</summary>
    public Aabb CardBox => _cardBox;

    /// <summary>The card's own mesh instance, whose material <see cref="BindWorld"/> overrode to
    /// draw unoccludable (no depth test, top render priority). Null before a bind, or if
    /// the bound chapter ships no card mesh under the bars.</summary>
    public MeshInstance3D? CardMesh => _cardMesh;

    // The scripted player's rig: P1's, the one aeroplane an authored `player` token means. It is
    // what an unclaimed episode owns, so a 1P session and every mission intro resolve to P1.
    private PlayerRig? ScriptedPlayer => _rigs.Count > 0 ? _rigs[0] : null;

    // The one pilot the staged `player` marker poses and the re-placement moves: the episode owner's,
    // which is the scripted player's in an unclaimed episode and in every 1P session. There is
    // exactly one marker, so the other humans hold the pose they were in when the code took them out
    // of flight rather than stacking on that point, and no second staging geometry exists to give
    // them. ⚠ Read live: a swap rebuilds the owner's controller mid-episode.
    private FlightController? OwnerPilot =>
        EpisodeOwner?.Controller is { IsHumanPiloted: true } pilot ? pilot : null;

    /// <summary>The vertical field of view, in degrees, at which the letterbox card of extent
    /// <paramref name="cardBox"/> covers a pane's height, or null when the card has no usable
    /// extent. Aspect-free by design: the card's authored height is the frame on every window, the
    /// way a 4:3 film plays on a wide screen, and <see cref="BarsWidthScale"/> covers the width a
    /// wider pane adds. Public because the fit is asserted directly.</summary>
    public static float? FramingFovDeg(Aabb cardBox)
    {
        float dist = Mathf.Abs(cardBox.GetCenter().Z);
        float halfHeight = cardBox.Size.Y * 0.5f;
        if (dist <= 0f || halfHeight <= 0f)
        {
            return null;
        }

        float half = halfHeight / (1f + CardOverscan);
        return Mathf.RadToDeg(2f * Mathf.Atan(half / dist));
    }

    /// <summary>How far the card has to stretch along the eye's right axis for the bars to reach
    /// both edges of a pane of ratio <paramref name="aspect"/> framed by
    /// <see cref="FramingFovDeg"/>. 1 at the card's own ratio and below, so a 4:3 pane gets the
    /// authored card untouched, and a stretched card keeps <see cref="CardOverscan"/>'s margin on
    /// the sides it grew to.</summary>
    public static float BarsWidthScale(Aabb cardBox, float aspect)
    {
        float halfHeight = cardBox.Size.Y * 0.5f;
        float halfWidth = cardBox.Size.X * 0.5f;
        if (halfHeight <= 0f || halfWidth <= 0f || aspect <= 0f)
        {
            return 1f;
        }

        return Mathf.Max(1f, halfHeight * aspect / halfWidth);
    }

    /// <summary>Is this one of the story-mission opening definitions (<see cref="IntroAnims"/>)?
    /// </summary>
    public static bool IsIntro(string? animName) =>
        animName != null && Array.IndexOf(IntroAnims, animName) >= 0;

    /// <summary>Registers definitions this host answers for beyond the intros: what the landings
    /// trigger can start, plus what those definitions reach by <c>CALL_ANIMATION</c>. This is the
    /// original's per-instance host registration, resolved from the authored data instead.
    /// ⚠ Pass definition names, never callback codes; the no-movie bootstrap <c>player_setup</c>
    /// raises the same nine an intro does.</summary>
    public void HostDefinitions(IEnumerable<string> animNames)
    {
        foreach (string name in animNames)
        {
            _hosted.Add(name);
        }
    }

    /// <summary>Does this host answer for <paramref name="animName"/>? An intro always, and a
    /// definition registered through <see cref="HostDefinitions"/>; every other definition's codes
    /// keep the answer they had.</summary>
    public bool Hosts(string? animName) =>
        animName != null && (IsIntro(animName) || _hosted.Contains(animName));

    /// <summary>Puts <paramref name="animName"/> in the landings slot: the next episode belongs to
    /// it and ends when it does, however deep the callee that raises its first code sits, and
    /// <paramref name="owner"/> is the human whose trigger this is (null: the scripted player's,
    /// which is what a mission intro means). This is the original's own slot, which holds the
    /// instance the trigger started. Call it before starting the definition, since the first code
    /// can land inside that start. Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
    public void Own(string animName, PlayerRig? owner = null)
    {
        // ⚠ Not every mission trigger starts a cutscene: an objective's WAKE_ANIM and the ladder
        // switch go through the same call, and a slot one of those claimed would outrank the next
        // episode's real raiser for as long as it ran.
        if (Playing || (_runtime != null && !RaisesInClosure(animName)))
        {
            return;
        }

        _owner = animName;
        _ownerRig = owner;
    }

    /// <summary>The world's animation runtime and the two nodes a cutscene definition drives. Run
    /// once the world is built, before any rig exists: the intro definitions start during the
    /// animation bootstrap, so their codes are hosted before there is anything to apply them to.
    /// </summary>
    public void BindWorld(AnimRuntime? runtime, AircraftStage? aircraft = null)
    {
        _runtime = runtime;
        _aircraft = aircraft;
        _playerMarker = aircraft?.PlayerMarker;
        if (runtime == null)
        {
            return;
        }

        // The rate rides the runtime's own advance, so it reaches a realtime flown session and the
        // parent-driven probe clock through the one path both step through.
        runtime.FastForward = _fastForward;

        _markerHome = runtime.WorldRoot;
        _cutsceneCamera = First(runtime.FindNodes(CameraNode));
        // ⚠ The runtime's root, not the camera's parent right now: this binds AFTER the bootstrap,
        // by which time an intro definition has already reparented the camera into what it frames.
        _cameraHome = runtime.WorldRoot;
        _bars = First(runtime.FindNodes(BarsNode));
        // Measured once, before anything scales it: re-measuring a scaled card would read its own
        // last answer back and oscillate.
        _card = _bars != null && _bars.GetChildCount() > 0 ? _bars.GetChild(0) as Node3D : null;
        _barsScale = _bars?.Basis.Scale ?? Vector3.One;
        _cardMesh = null;
        if (_card != null && CardBounds(_card) is { } box)
        {
            _cardBox = box;
            _cardMesh = MakeUnoccludable(_card);
        }

        Log.Info("anim", $"cutscene: {(_cutsceneCamera != null ? "camera1" : "NO camera1")}, {(_card != null ? Log.Format($"letterbox card {_cardBox.Size}") : "NO letterbox bars")}");
    }

    /// <summary>The session's rigs and its live AI aircraft, once both exist. Re-applies whatever
    /// state the codes already asked for, which is how a cutscene that started during the world
    /// build reaches the aircraft built after it.</summary>
    public void BindRigs(IReadOnlyList<PlayerRig> rigs, Func<IReadOnlyList<FlightController>> aiPlanes)
    {
        _rigs = rigs;
        _aiPlanes = aiPlanes;
        StageFlownAirframe();
        if (!Playing)
        {
            // ⚠ The flown vehicle's node is ACTIVE once gameplay starts: `player_setup` switches
            // `player` off and its end-of-definition reset (unrun here) back on. Left off, a later
            // drop holds the pilot undrawn for its whole length (docs/architecture.md).
            if (_playerMarker != null)
            {
                _playerMarker.Visible = true;
            }

            return;
        }

        FillsWindow?.Invoke(true);
        ApplyPresentation(Presenting);
        ApplyOutOfFlight(OutOfFlight);
        if (AiParked)
        {
            ParkAi();
        }
    }

    /// <summary>The <c>CALLBACK</c> host itself: answers one authored code, returning whether this
    /// host acted on it. A code outside the cutscene vocabulary is declined, so the runtime's own
    /// two vehicle-death codes and every unknown one keep the answer they had.
    /// <paramref name="rootName"/> is the raising definition's root node, which the airframe swap
    /// resolves the capture's own aircraft by.</summary>
    public bool Host(int code, string? animName, string? rootName = null)
    {
        if (!Hosts(animName) && !(Playing && animName == Anim))
        {
            return false;
        }

        switch (code)
        {
            case CodeHoldsWorld:
            case CodePresentation:
            case CodeOutOfFlight:
            case CodeParkAi:
            case CodeRevealAi:
            case CodeCamParamsFree:
            case CodeCamParamsRestore:
            case CodeHandoff:
            case CodeRestoreSystems:
            case CodeMissionComplete:
            case CodeReplacePlayer:
            case CodeClearOrdnance:
                break;
            default:
                if (AirframeSwapCodes.For(code) != null)
                {
                    break;
                }

                if (Array.IndexOf(NamedGaps, code) < 0)
                {
                    return false;
                }

                break;
        }

        // The record covers ONE episode. Cleared as the next one takes the session rather than at
        // the handoff, so a caller can still read the shape of the cutscene that just ended.
        if (!Playing)
        {
            _codes.Clear();
            _raisers.Clear();
        }

        _codes.Add(code);
        if (animName != null)
        {
            _raisers.Add(animName);
        }
        if (!Playing)
        {
            Playing = true;
            // The slot beats the raiser only while its definition is live, so a stale one cannot
            // outlast it. ⚠ With no runtime yet to ask it wins outright: the intro's first code
            // lands in the bootstrap, before the build has handed this host a runtime.
            bool slotWins = _owner != null
                && (_runtime == null || _runtime.AnimStateOf(_owner) == AnimRunning);
            Anim = slotWins ? _owner : animName;
            // The rig rides the slot and not the raiser: a stale slot that lost the definition has
            // lost its claim on the episode's owner too, and an unclaimed episode is the scripted
            // player's.
            _episodeOwner = slotWins ? _ownerRig : null;
            _owner = null;
            _ownerRig = null;
            // The owner's own aeroplane into the node table as the episode takes the session, not
            // at a swap alone. A hookup resolves `player_<airframe>` by name, and the seat staged
            // at the bind is otherwise the one whose hook swings.
            StageFlownAirframe();
            SeedRaisers();
            ScopeFastForward();
            HeldForEnding = false;
            _barsFlipsThisEpisode = 0;
            _lastBarsVisible = _bars?.Visible ?? false;
            if (Anim == animName)
                Log.Info("anim", $"cutscene: '{animName}' has the session");
            else
                Log.Info("anim", $"cutscene: '{Anim}' has the session, its callee '{animName}' raising the first code");
            FillsWindow?.Invoke(true);
        }

        _codeRoot = rootName;
        Act(code);
        return true;
    }

    /// <summary>The mission start's own AI park, which belongs to every mission of every type and
    /// not to a story intro: the original parks imperatively before any start list runs, reading no
    /// mission type, and the bootstrap definition's reset lifts it with code 914. An intro has
    /// already parked by the time a session reaches this and holds it to that film's end; a mission
    /// opening without one, an Instant Action wave among them, is lifted on its first step. Decode:
    /// docs/formats/anim-definitions/cutscenes.md.</summary>
    public void ParkAtMissionStart()
    {
        if (AiParked)
        {
            return;
        }

        AiParked = true;
        _startParked = true;
        ParkAi();
        Log.Info("anim", $"cutscene: the mission start parks {_parked.Count} AI aircraft, lifted by the bootstrap definition's own 914");
    }

    /// <inheritdoc/>
    public override void _Process(double delta) => Tick();

    /// <summary>One frame of the cutscene: every rig camera takes the pose the definition put
    /// <c>camera1</c> in, and the definition ending is the handoff. Cheap and inert when no
    /// cutscene is playing.</summary>
    public void Tick()
    {
        LiftMissionStartPark();
        if (!Playing)
        {
            return;
        }

        PollFastForward();
        MirrorCamera();
        StagePlayerAircraft();
        // The fit first: the pin stretches the card to the pane this frame's fit measured.
        FrameBars();
        PinBars();
        WatchBars();
        if (_runtime != null && Anim != null && _runtime.AnimStateOf(Anim) != AnimRunning
            && !AnyRaiserRunning())
        {
            if (KeepsTheShot())
            {
                return;
            }

            Restore("its definition ended");
        }
    }

    /// <summary>The player's skip. Force-stops the definition the way the original's state core
    /// does, then restores the gameplay state the definition's own RESET_STATE asserts, so the
    /// remaining beats being dropped cannot leave the mission held, hidden or unflyable.
    /// Declined, and the key press left to whatever else reads it, while no skip is armed.
    /// <paramref name="playerIndex"/> is the human whose device it came from: any of them may
    /// skip, so who did is something the other players are owed rather than a detail.</summary>
    public bool Skip(int playerIndex = 0)
    {
        if (!Playing || !Skippable || KeepsTheShot())
        {
            return false;
        }

        Log.Info("anim", $"cutscene '{Anim}' skipped by P{playerIndex + 1}");
        if (Anim != null)
        {
            _runtime?.Stop(Anim);
        }

        Restore("skipped");
        return true;
    }

    /// <summary>Takes an input the session offered the skip and the skip declined, and holds the
    /// picture at a raised rate for as long as that input stays down. A remake-only rule offered
    /// only where no skip is armed, which is every mid-mission definition; an intro that armed one
    /// has already taken the press as the original's own force-stop.</summary>
    public void NoteHeld(InputEvent @event)
    {
        if (!Playing || Skippable)
        {
            return;
        }

        if (@event is InputEventKey { Pressed: true, Keycode: not Key.Escape } key)
        {
            _keysDown.Add(key.Keycode);
        }
        else if (@event is InputEventJoypadButton { Pressed: true } pad)
        {
            _padsDown.Add((pad.Device, pad.ButtonIndex));
        }
    }

    /// <summary>Holds (or releases) the fast-forward without an input event, for the suite that
    /// plays one scene at both rates. Stands beside a real held input rather than replacing it;
    /// either arms the rate, and the episode's own gate below still decides whether it may.
    /// </summary>
    public void HoldFastForward(bool held) => _scriptedHold = held;

    /// <summary>A stick's skip press, which raises no input event. It gets the same skip a key or
    /// pad press gets. When that is declined, it holds the fast-forward while <paramref name="held"/>
    /// reads true. True when it skipped; <paramref name="playerIndex"/> is as for
    /// <see cref="Skip"/>.</summary>
    public bool TakeStickPress(int playerIndex, Func<bool> held)
    {
        ArgumentNullException.ThrowIfNull(held);
        if (Skip(playerIndex))
        {
            return true;
        }

        if (Playing && !Skippable)
        {
            _stickHeld = held;
        }

        return false;
    }

    private static bool AuthorsCode(AnimDefinition def)
    {
        foreach (var seq in def.Sequences)
        {
            foreach (var ev in seq.Events)
            {
                if (ev.Kind == "Callback")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Node3D? First(IReadOnlyList<Node3D> found) => found.Count > 0 ? found[0] : null;

    // The card's extent in the bars root's own frame: one mesh on or under `g1`, whose AABB carries
    // both quads and the authored 7.5 m standoff.
    private static Aabb? CardBounds(Node3D card)
    {
        if (card is VisualInstance3D self)
        {
            return card.Transform * self.GetAabb();
        }

        foreach (var child in card.GetChildren())
        {
            if (child is VisualInstance3D mesh and Node3D inner)
            {
                return card.Transform * (inner.Transform * mesh.GetAabb());
            }
        }

        return null;
    }

    // The card's own material carries no occlusion guarantee, so it is overridden: no depth test
    // plus the top render priority draws it last regardless of what claimed the pixel that frame.
    // ⚠ Not a Duplicate(): every world mesh carries the bias ShaderMaterial
    // (SceneBuilder.BiasMaterial), with no NoDepthTest/RenderPriority of its own, so the
    // replacement is built fresh from the source shader's albedo_color, unshaded and both-sided.
    private static MeshInstance3D? MakeUnoccludable(Node3D card)
    {
        var mesh = card as MeshInstance3D;
        if (mesh == null)
        {
            foreach (var child in card.GetChildren())
            {
                if (child is MeshInstance3D found)
                {
                    mesh = found;
                    break;
                }
            }
        }

        if (mesh == null)
        {
            return null;
        }

        var albedo = Colors.Black;
        if (mesh.GetActiveMaterial(0) is ShaderMaterial source)
        {
            var tint = source.GetShaderParameter("albedo_color");
            if (tint.VariantType == Variant.Type.Color)
            {
                albedo = tint.As<Color>();
            }
        }
        else if (mesh.GetActiveMaterial(0) is StandardMaterial3D standard)
        {
            albedo = standard.AlbedoColor;
        }

        mesh.MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(albedo.R, albedo.G, albedo.B),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            NoDepthTest = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            RenderPriority = 127, // the engine's ceiling: renders after every other transparent draw
        };
        return mesh;
    }

    // The question the slot is claimed on, asked of the whole call closure: a row that raises no
    // code itself and calls the definitions that do is the shape the slot exists for.
    private bool RaisesInClosure(string animName)
    {
        foreach (var def in _runtime!.CallClosureOf(animName))
        {
            if (AuthorsCode(def))
            {
                return true;
            }
        }

        return false;
    }

    // The definitions the fast-forward may raise: this episode's whole call closure, since the
    // callee flying the shot drives as much of the picture as the definition that owns the
    // episode. Nothing is scoped where there is no runtime to ask.
    // Everything back to real speed at once: the episode is over, or it turned out to arm the
    // original's own skip and was never one to hold a key through.
    private void ClearFastForward()
    {
        _fastForward.Clear();
        _keysDown.Clear();
        _padsDown.Clear();
        _scriptedHold = false;
        _stickHeld = null;
        _fastForwardLogged = false;
    }

    private void ScopeFastForward()
    {
        ClearFastForward();
        if (_runtime == null || Anim == null)
        {
            return;
        }

        _fastForward.Scope(_runtime.CallClosureOf(Anim));
    }

    // Whether the rate is armed this tick, and at what. ⚠ The devices are re-read rather than
    // waited on for a release: an input whose key-up another handler consumed would otherwise hold
    // the picture at the raised rate for the rest of the episode.
    private void PollFastForward()
    {
        _keysDown.RemoveWhere(key => !Input.IsKeyPressed(key));
        _padsDown.RemoveWhere(pad => !Input.IsJoyButtonPressed(pad.Device, pad.Button));
        if (_stickHeld != null && !_stickHeld())
        {
            _stickHeld = null;
        }

        bool down = _scriptedHold || _keysDown.Count > 0 || _padsDown.Count > 0 || _stickHeld != null;
        _fastForward.Held = down && !Skippable && !HeldForEnding;
        if (!_fastForward.Held || _fastForwardLogged)
        {
            return;
        }

        _fastForwardLogged = true;
        Log.Info("anim", $"cutscene '{Anim}' fast-forwards while an input is held; this scene arms no skip");
    }

    // The bars are separately-called data (docs/formats/anim-definitions/cutscenes.md),
    // so this controller never turns them on or off itself except in Restore below. While Playing
    // is true they should transition AT MOST once, false->true, from the letterbox call site(s).
    // A second flip in either direction inside a live episode is the reported flicker, and costs
    // one bool compare a tick to catch. Quiet unless it actually happens.
    private void WatchBars()
    {
        if (_bars == null || _bars.Visible == _lastBarsVisible)
        {
            return;
        }

        _lastBarsVisible = _bars.Visible;
        _barsFlipsThisEpisode++;
        if (_barsFlipsThisEpisode > 1 || !_bars.Visible)
        {
            Log.Error("anim", $"cutscene: '{Anim}' letterbox bars flipped to {_bars.Visible} mid-episode (flip #{_barsFlipsThisEpisode}) at t={Utils.GameClock.Current?.Time ?? 0.0:0.###} -- the bars may rise once per episode, so this is the mid-cutscene flicker");
        }
    }

    // The gameplay end state the intro definitions author in their own RESET_STATE, raised here
    // because a CSVM RESET_STATE dispatch deliberately raises no callbacks and the original's
    // RESET_TIME schedule is undecoded. Also retracts the bars: nothing stops the letterbox
    // definition by name, in this engine or the original.
    private void Restore(string why)
    {
        Log.Info("anim", $"cutscene: '{Anim}' {why} at t={Utils.GameClock.Current?.Time ?? 0.0:0.##}, handing off after {_codes.Count} code(s)");
        // ⚠ Before the reset block below, not after: its own events register motions, and a rate
        // still standing here would run the hand-back faster than the world it hands back to.
        ClearFastForward();
        // What the ending definition's own RESET_STATE asserts beyond the codes below: CM07's
        // hangar drop calls `got_the_plane` there, and that call is the only thing in the mission
        // that completes its "Fly Through Zeppelin Hangar" objective.
        if (Anim != null && _runtime?.RunResetStateEvents(Anim) > 0)
        {
            Log.Info("anim", $"cutscene: '{Anim}' ran its authored RESET_STATE at the handoff");
        }

        // The staged archive props go back to their switched-off base state. The reset's own
        // OBJECT_DELETE_CHILD detaches one to the world root, where the original's walk no longer
        // reaches it but this scene still draws it (the hangar undercarriage, left at the origin).
        if (_aircraft != null)
        {
            foreach (var prop in _aircraft.Props.Values)
            {
                AnimRuntime.SetSubtreeActive(prop, false);
            }
        }

        // A re-placement authored in that same block is raised here, since the reset walk above
        // suppresses callbacks. ⚠ Before the restore codes: the hand-back reads the target it sets.
        if (Anim != null && ResetStateAuthors(Anim, CodeReplacePlayer))
        {
            _codes.Add(CodeReplacePlayer);
            Act(CodeReplacePlayer);
        }

        foreach (int code in RestoreCodes)
        {
            _codes.Add(code);
            Act(code);
        }

        // After the restore codes, so OutOfFlight is already down and this hands every aircraft
        // back the pose it held before the intro staged it.
        StagePlayerAircraft();
        if (_bars != null)
        {
            AnimRuntime.SetSubtreeActive(_bars, false);
        }

        // ⚠ Put the camera back under the world root BEFORE parking it at the origin: a definition
        // composes itself by reparenting `camera1`, so identity is a pose in the framed node's
        // frame until that is undone. Other definitions pose against `camera1` too.
        if (_cutsceneCamera != null)
        {
            if (_cameraHome != null && _cutsceneCamera.GetParent() != _cameraHome)
            {
                AnimRuntime.Reparent(_cutsceneCamera, _cameraHome);
            }

            _cutsceneCamera.Transform = Transform3D.Identity;
        }

        // The `player` marker goes home too, and for the camera's reason above: left on the frame a
        // definition composed itself onto, it poses the NEXT episode's aeroplane in that frame
        // (docs/architecture.md). ⚠ After the restore codes, whose 951 reads the pose it was left in.
        if (_playerMarker != null)
        {
            if (_markerHome != null && _playerMarker.GetParent() != _markerHome)
            {
                AnimRuntime.Reparent(_playerMarker, _markerHome);
            }

            _playerMarker.Transform = Transform3D.Identity;
        }

        foreach (var (camera, fov) in _fov)
        {
            camera.Fov = fov;
        }

        _fov.Clear();
        Playing = false;
        Anim = null;
        // Last, with the state it was raised alongside already down: the panes come back to a
        // session that is flying again rather than to one still holding the world.
        FillsWindow?.Invoke(false);
    }

    // Whether this episode holds its shot instead of ending: the mission is already over, and the
    // ending's fade belongs over the film's last frame rather than over a view of the world it cut
    // back to. Latched into HeldForEnding so the reason is reported once and readable afterwards.
    private bool KeepsTheShot()
    {
        if (EndingLanded?.Invoke() != true)
        {
            return false;
        }

        if (!HeldForEnding)
        {
            HeldForEnding = true;
            Log.Info("anim", $"cutscene '{Anim}' keeps the shot: the mission ended under it, and the leaving fade runs over the film's own last frame");
        }

        return true;
    }

    private void Act(int code)
    {
        switch (code)
        {
            case CodeHoldsWorld:
                HoldsWorld = true;
                // The original arms its skip HERE and nowhere else, so the two states share a
                // code and not an implementation: the hold is what a definition asks for, the
                // skip is what the player is then offered.
                Skippable = true;
                // ⚠ And the fast-forward goes away with the same code. An episode offering the
                // original's own force-stop must not also run fast; the key press ends it.
                ClearFastForward();
                WorldHeld?.Invoke(true);
                // The original parks the AI before every mission's intro regardless of what codes
                // that intro's own data authors; the shared generic_intro never raises 913, and
                // the bespoke intros that do (C1/M04, C3/M03) restate a state already entered.
                if (IsIntro(Anim) && !AiParked)
                {
                    AiParked = true;
                    ParkAi();
                }

                break;
            case CodePresentation:
                Presenting = true;
                ApplyPresentation(true);
                break;
            case CodeOutOfFlight:
                OutOfFlight = true;
                ApplyOutOfFlight(true);
                break;
            case CodeParkAi:
                AiParked = true;
                ParkAi();
                break;
            case CodeRevealAi:
                AiParked = false;
                RevealAi();
                break;
            case CodeCamParamsFree:
                CamParamsFree = true;
                break;
            case CodeCamParamsRestore:
                CamParamsFree = false;
                break;
            case CodeHandoff:
                HoldsWorld = false;
                Skippable = false;
                WorldHeld?.Invoke(false);
                Presenting = false;
                ApplyPresentation(false);
                OutOfFlight = false;
                ApplyOutOfFlight(false);
                break;
            case CodeReplacePlayer:
                ReplacePlayer();
                break;
            case CodeMissionComplete:
                MissionComplete?.Invoke();
                break;
            case CodeClearOrdnance:
                ClearOrdnance?.Invoke();
                break;
            case CodeRestoreSystems:
                foreach (var pilot in Pilots())
                {
                    pilot.Audio?.SetPaused(false);
                    pilot.SetPilotHudVisible(true);
                }

                break;
            default:
                if (AirframeSwapCodes.For(code) is { } airframe)
                {
                    Swap(airframe);
                    break;
                }

                if (_gapsLogged.Add(code))
                {
                    Log.Info("anim", $"cutscene: callback {code} is a named gap, reaching no case in the original's own host either");
                }

                break;
        }
    }

    // One of codes 965 to 967: the airframe is rebuilt first, then the cutscene flags the code sets
    // (the ones 11 and 2 set between them) land on the aircraft that now exists. ⚠ Both halves of
    // that order matter: the original's rebuild CLEARS those flags on the way through, and the
    // state has to reach the new rig. Nothing here ends the presentation, the definition ending
    // does, which is how the player gets flight back in the new airframe.
    private void Swap(AirframeSwapCode airframe)
    {
        var swapped = SwapAirframe?.Invoke(new AirframeSwapOrder(airframe, _codeRoot, EpisodeOwner))
                      ?? default;
        if (!swapped.Swapped)
        {
            Log.Info("anim", $"cutscene: callback {airframe.Code} names '{airframe.PlaneNode}' but no aircraft was there to swap");
            return;
        }

        // ⚠ Drop the capture's aircraft from the parked list, or 914 reveals it again. CM02's
        // capture is called from the wing walk, whose 913 parks that aircraft BEFORE 967 hides it,
        // and the swap's hide is the mission's to keep rather than this episode's to undo.
        if (swapped.Hidden is { } hidden)
        {
            _parked.Remove(hidden);
            // The hide is the original's deactivate (its dead byte set), so the park no longer
            // stands behind the inert flag: the player now counts for that aircraft's group.
            hidden.Parked = false;
        }

        StageFlownAirframe();
        OutOfFlight = true;
        ApplyOutOfFlight(true);
        Presenting = true;
        ApplyPresentation(true);
    }

    // The episode owner's airframe subtree into the runtime's node table, which is the aeroplane a
    // hookup definition resolves its own hook, wings and mount offset off. The original has one
    // player vehicle and names it; with a field, the one the definition means is the human whose
    // trigger started the episode, and an unclaimed episode still answers the scripted player's.
    // Run wherever that aircraft can have been replaced, since the definition resolves it by name.
    private void StageFlownAirframe()
    {
        if (_runtime is { } runtime && _aircraft is { } aircraft && EpisodeOwner is { } owner)
        {
            aircraft.StageFlown(runtime, owner.Controller?.PlaneModel);
        }
    }

    // The chrome and the view target: CameraOwned is what silences the whole per-frame camera arm,
    // which is also what stops the cockpit rules being re-asserted over the cutscene every frame.
    // The rig's world overlays go down with the chrome: a CanvasLayer ignores depth, so the flare
    // and the whiteout would paint over a card the world puts in front of them (BL-452).
    private void ApplyPresentation(bool on)
    {
        foreach (var pilot in Pilots())
        {
            pilot.CameraOwned = on;
            pilot.SetPilotHudVisible(!on);
            // ⚠ Beside CameraOwned, never instead of it: that flag is what stops the arm
            // re-asserting the pilot's own view rules, so a cockpit seat would otherwise be framed
            // by the episode's camera with its airframe hidden and its panel over the shot.
            pilot.SetViewedFromOutside(on);
        }

        foreach (var rig in _rigs)
        {
            foreach (var overlay in rig.WorldOverlays)
            {
                overlay.Visible = !on;
            }
        }
    }

    // Out of flight: the airframe holds its pose, takes no input and is neither drawn nor hittable,
    // and its sound handles go with it. This IS the original's OBJECT_ACTIVE_STATE [player, false]:
    // its `player` node is the flown aircraft, resolved by name at runtime and carrying whichever
    // airframe the pilot bought (docs/formats/anim-definitions/cutscenes.md). What CSVM has no node
    // for is the POSE half, so an intro's own aircraft motion drops instead of playing and the
    // airframe stays undrawn for the whole cutscene (BL-482).
    private void ApplyOutOfFlight(bool on)
    {
        foreach (var pilot in Pilots())
        {
            pilot.Held = on;
            pilot.Inert = on;
            pilot.Audio?.SetPaused(on);
        }

        // A pilot the film is posing is no longer where a PLAYER_RANGE poll should read him:
        // the drop reparents his aeroplane onto the very marker one of the mission's own polls
        // re-poses, and the two then chase each other (AnimRuntime.RangePositions).
        if (_runtime != null)
        {
            _runtime.PlayerRangeHeld = on;
        }

        // ⚠ In the same instant, not on the next tick: the definition raising this code goes on
        // posing the aircraft in the same dispatch, so the pose it reads must be this one.
        StagePlayerAircraft();
    }

    private void ParkAi()
    {
        if (_aiPlanes == null)
        {
            return;
        }

        foreach (var ai in _aiPlanes())
        {
            if (!ai.Inert)
            {
                // Parked before inert, so a listener on the inert flip already reads the park:
                // the original's 913 sets the hold flag and never the dead byte, and the DEDG
                // walk keeps counting a parked vehicle (docs/formats/objectives.md).
                ai.Parked = true;
                ai.Inert = true;
                _parked.Add(ai);
            }
        }
    }

    // Code 914 out of the bootstrap definition's reset. The definition a mission without a movie
    // bootstraps carries no timed event and resets at once, so the mission-start park lasts that
    // mission's first step; an intro is still playing here and keeps the park to its own end.
    private void LiftMissionStartPark()
    {
        if (!_startParked || Playing)
        {
            return;
        }

        AiParked = false;
        RevealAi();
    }

    // Only what this controller parked comes back: an aircraft built inert for a later wave is not
    // this cutscene's to activate.
    private void RevealAi()
    {
        _startParked = false;
        foreach (var ai in _parked)
        {
            ai.Inert = false;
            ai.Parked = false;
        }

        _parked.Clear();
    }

    // The pose half of the original's `player` node: an intro activates it, reparents it under the
    // airship and flies it on an SI script, and the aeroplane that follows it is whichever airframe
    // the pilot flies. Re-asserted every tick rather than latched, so a respawn cannot take the
    // model back off the screen mid-cutscene. Cleared at the handoff, which hands the aeroplane back
    // the pose it held before the staging unless the definition re-placed it (ReplacePlayer).
    private void StagePlayerAircraft()
    {
        if (_playerMarker == null)
        {
            return;
        }

        var pose = OutOfFlight && _playerMarker.Visible
            ? AnimRuntime.WorldTransform(_playerMarker, out _)
            : (Transform3D?)null;
        var owner = OwnerPilot;
        foreach (var pilot in Pilots())
        {
            pilot.StageAt(ReferenceEquals(pilot, owner) ? pose : null);
        }
    }

    // Where the pilot flies out of: the `player` node's world pose, read the moment the definition
    // asks for it. The original writes that pose straight into the vehicle's own position, rotation
    // and velocity, so this reads the marker whether or not it is drawn, and the aeroplane's own
    // hand-back target moves with it (docs/formats/anim-definitions/cutscenes.md). The episode
    // owner's target alone, for the one-marker reason on OwnerPilot.
    private void ReplacePlayer()
    {
        if (_playerMarker == null)
        {
            Log.Info("anim", $"cutscene: callback {CodeReplacePlayer} re-places the pilot, but this session staged no '{AircraftStage.PlayerNode}' to read a pose off");
            return;
        }

        // The original reads the flown vehicle's OWN node, so a definition raising this code
        // without posing `player` re-places the pilot on themselves; the stand-in marker would
        // instead put them on the world root, under the terrain (CM15's paratrooper drop).
        if (MarkerParked())
        {
            Log.Info("anim", $"cutscene: '{Anim}' raises {CodeReplacePlayer} with '{AircraftStage.PlayerNode}' unposed, so it authors no placement to fly out of");
            return;
        }

        var pose = AnimRuntime.WorldTransform(_playerMarker, out _);
        OwnerPilot?.ResumeAt(pose);
        Log.Info("anim", $"cutscene: '{Anim}' re-places P{(EpisodeOwner?.Index ?? 0) + 1} at {pose.Origin}");
    }

    // Is the marker still where the build and every handoff park it: under the world root, at
    // identity? A definition that posed it has reparented it, written a transform, or both.
    private bool MarkerParked() =>
        _playerMarker != null && _playerMarker.GetParent() == _markerHome
        && _playerMarker.Transform.IsEqualApprox(Transform3D.Identity);

    private void MirrorCamera()
    {
        if (_cutsceneCamera == null)
        {
            return;
        }

        var pose = _cutsceneCamera.GlobalTransform.Orthonormalized();
        foreach (var rig in _rigs)
        {
            rig.Camera.GlobalTransform = pose;
            CameraPosed = true;
        }
    }

    // BL-452: the bars take camera1's pose HERE, in the same instant MirrorCamera hands that pose
    // to the rig cameras, because the letterbox definition's own LOOP pin lands at an arbitrary
    // point in the runtime's instance walk and can be a frame behind the eye it clads
    // (docs/formats/anim-definitions/cutscenes.md, "What the def does"). ⚠ Keep the node's rest
    // scale, the way PoseChannel does: the card must not be resized (see BindWorld).
    private void PinBars()
    {
        if (_bars == null || _cutsceneCamera == null || !_bars.Visible)
        {
            return;
        }

        var pose = _cutsceneCamera.GlobalTransform.Orthonormalized();
        // The stretch is along the eye's right axis alone, so the bars keep their authored height
        // and the gap between them stays the frame FrameBars fitted.
        var basis = pose.Basis.Scaled(_barsScale);
        _bars.GlobalTransform = new Transform3D(
            new Basis(basis.X * _barsWidth, basis.Y, basis.Z), pose.Origin);
    }

    // The bars are a fixed card 7.5 m in front of the eye, so what they cover is a question of
    // frame shape: the card IS the frame, which is the only reading under which the geometry is a
    // letterbox at all. Its authored height is that frame on every pane, so the vertical picture is
    // the 4:3 one a wider window widens around instead of cropping into, and the card stretches
    // sideways to reach the edges of a pane wider than itself.
    private void FrameBars()
    {
        if (_card == null || _rigs.Count == 0 || FramingFovDeg(_cardBox) is not { } fov)
        {
            return;
        }

        float widest = 0f;
        foreach (var rig in _rigs)
        {
            var size = rig.Camera.GetViewport().GetVisibleRect().Size;
            widest = Mathf.Max(widest, size.Y > 0f ? size.X / size.Y : 1f);
            _fov.TryAdd(rig.Camera, rig.Camera.Fov);
            rig.Camera.Fov = fov;
        }

        // One card serves every camera, so the widest pane is what it has to cover; a narrower one
        // is then covered by more than it needs, which costs nothing.
        _barsWidth = BarsWidthScale(_cardBox, widest);
    }


    private IEnumerable<FlightController> Pilots()
    {
        foreach (var rig in _rigs)
        {
            if (rig.Controller is { IsHumanPiloted: true } pilot)
            {
                yield return pilot;
            }
        }
    }

    // The episode's code-authoring definitions, read off the owning definition's call closure at
    // the start: a callee that has not raised anything yet (CM06's unhook, whose two codes sit at
    // its end) is as much the episode's as one that has.
    private void SeedRaisers()
    {
        if (_runtime == null || Anim == null)
        {
            return;
        }

        foreach (var def in _runtime.CallClosureOf(Anim))
        {
            if (def.AnimName is { Length: > 0 } name && AuthorsCode(def))
            {
                _raisers.Add(name);
            }
        }
    }

    // A trailing WAIT_FOR_COMPLETION never holds a runner open (docs/org/sequences.md), so a row
    // definition can end while the callee it called last is still posing the player: the
    // definitions that author or raised this episode's codes are what the handoff waits for.
    private bool AnyRaiserRunning()
    {
        foreach (string raiser in _raisers)
        {
            if (raiser != Anim && _runtime!.AnimStateOf(raiser) == AnimRunning)
            {
                return true;
            }
        }

        return false;
    }

    // Does any definition of this name author the code in its RESET_STATE?
    private bool ResetStateAuthors(string animName, int code)
    {
        if (_runtime == null)
        {
            return false;
        }

        foreach (var def in _runtime.DefsFor(animName))
        {
            if (def.ResetState == null)
            {
                continue;
            }

            foreach (var ev in def.ResetState.Events)
            {
                if (ev.Kind == "Callback" && (int)(ev.Data.Num("value") ?? -1f) == code)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
