using System.Collections.Generic;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Boards;

/// <summary>
/// The splitscreen rendering rig: N panes, each a <see cref="SubViewportContainer"/> +
/// <see cref="SubViewport"/> with its own <see cref="Camera3D"/>, all rendering the same
/// <see cref="World3D"/> as the main viewport. Built only for 2+ players; a single player keeps
/// GameSession's original main-viewport camera untouched. Layout is <see cref="PaneRect"/>'s, with
/// 3P's last quadrant black. Each player owns one visual layer out of a reserved band
/// (<see cref="PlayerLayerBit0"/>) for camera-anchored singletons like the skydome;
/// <see cref="SetVisualLayer"/> moves the copies, <see cref="PlayerCullMask"/> culls the rest of
/// the band. Every pane is a 3D audio listener, or a splitscreen session has no listener at all.
/// The zone-gate band (<c>Mech3.ZoneGate.LayerBand</c>) and <see cref="OwnAirframeLayer"/>'s are
/// separate allocations of the same 20 layers. The listener model: docs/architecture.md.
/// </summary>
public sealed partial class SplitScreen : CanvasLayer
{
    /// <summary>Panes the rig supports, the reserved visual-layer band is this wide.</summary>
    public const int MaxPlayers = 4;

    /// <summary>The layer <see cref="Flight.Hud.CockpitVisibility.ShowForPhotograph"/> moves a pilot's
    /// hidden airframe groups onto for the one frame the Danger Zone camera draws, the bit just below
    /// the own-airframe band. No pane's cull mask carries it (<see cref="PaneCullMask"/>).</summary>
    public const uint PhotographLayer = 1u << 8;

    // First visual layer of the reserved per-player band. Godot has 20 layers (bits 0–19); the
    // world builds everything on layer 1 (bit 0), so taking the top four leaves the whole middle
    // range free for future use.
    private const int PlayerLayerBit0 = 16;
    private const uint AllLayers = 0xFFFFF;              // Godot's 20 visual layers
    private const uint PlayerBand = 0xFu << PlayerLayerBit0;

    // First layer of the own-airframe band, four bits taken well below the two bands above.
    // ⚠ These bits are the opposite of the per-player band's: they are IN every cull mask the
    // engine builds, so an aeroplane wearing one is hidden from nobody until a single camera drops
    // the single bit. Putting an airframe on its pilot's PRIVATE layer instead would hide that
    // aeroplane from every other pane, which is the reverse of what a splitscreen seat needs.
    private const int OwnAirframeBit0 = 9;

    // The shared zone-gate band is Mech3.ZoneGate.LayerBand (bits 13–15 = layers 14–16, zone_id
    // 1/2/3), taken immediately below the per-player band. Outside PlayerBand on purpose: every
    // cull mask below starts with all three INCLUDED, so the gate is something WeatherRig switches
    // OFF, never something a new camera has to remember to switch on. It is allocated in Mech3
    // rather than here because SceneBuilder stamps it at build time, node by node.

    private const int Gutter = 2;   // px between panes, confirmed at the controls

    // How long the skip notice stands, in seconds of wall time. Chrome, not sim: the skip releases
    // the world hold in the same instant, so a notice on the session clock would be the one thing
    // still frozen behind a mission that is running again. TUNE.
    private const double SkipNoticeS = 3.0;

    private const int SkipNoticeFontPx = 28;

    // How far off the bottom edge the notice sits, clear of the letterbox card's lower bar, which
    // a cutscene has over that edge for the whole time this line can be up.
    private const int SkipNoticeInsetPx = 72;

    // Per-player identity colours: the launchscreen's join strip and plane-select
    // cursors, and later the race HUD/scoreboard rows, all key off these so a player
    // recognises "their" colour from the menu through to the results. P1 keeps the launchscreen's
    // existing gold focus colour so a single-player menu looks exactly as it did. TUNE.
    private static readonly Color[] Colors4 =
    {
        new(1f, 0.86f, 0.38f),   // P1 gold
        new(0.45f, 0.83f, 1f),   // P2 sky blue
        new(0.55f, 0.95f, 0.55f),// P3 green
        new(1f, 0.60f, 0.85f),   // P4 pink
    };

    private readonly List<SubViewport> _views = new();
    private readonly List<SubViewportContainer> _panes = new();
    private Control _root = null!;
    private ColorRect _backdrop = null!;
    private Label? _skipNotice;
    private double _skipNoticeLeft;

    /// <summary>One SubViewport per player, in player order. Add the player's camera (and its
    /// HUD canvases) to it.</summary>
    public IReadOnlyList<SubViewport> Views => _views;

    /// <summary>Whether pane 1 currently fills the window on its own, what a cutscene asks for,
    /// since four small copies of one camera path is not a picture anybody framed.</summary>
    public bool Filled { get; private set; }

    /// <summary>The skip line standing on screen, or null when none is. Its text names the player
    /// and its modulate is that player's own <see cref="PlayerColor"/>.</summary>
    public Label? SkipNotice => _skipNotice is { Visible: true } label ? label : null;

    /// <summary>Whether a 2-player split stands side by side rather than stacked. The threshold is
    /// the pane's own shape, not a screen name: side by side only once each half is still at least
    /// as wide as it is tall, which is every window from 2:1 out to an ultrawide's 32:9. A 16:9
    /// window is not one, halving either axis lands the pane exactly as far from the reference
    /// frame either way, and a 640x720 pane is a shape nothing in the port is calibrated for.</summary>
    public static bool SideBySide(Vector2 window) => window.X >= window.Y * 2f;

    /// <summary>What the current layout is called in a log line, read off the same rule
    /// <see cref="PaneRect"/> lays out by, so the report cannot drift from the geometry.</summary>
    public static string LayoutName(int players, Vector2 window) =>
        players != 2 ? "2×2 grid" : SideBySide(window) ? "side by side" : "stacked top/bottom";

    /// <summary>Where player <paramref name="index"/>'s pane sits in a <paramref name="size"/>
    /// area shared by <paramref name="players"/> players: 2P stacked top/bottom, or side by side
    /// when <paramref name="sideBySide"/> says the window is wide enough for it, 3–4P a 2×2 grid
    /// (3P's fourth quadrant unused). ⚠ The axis is decided on the WINDOW and passed in, because
    /// the launchscreen lays its panes into a shorter area than it flies in and would otherwise
    /// choose a different axis from the flight it is picking for.</summary>
    public static Rect2 PaneRect(int index, int players, Vector2 size, bool sideBySide)
    {
        int cols = players > 2 || sideBySide ? 2 : 1;
        int rows = players <= 2 && sideBySide ? 1 : 2;
        float paneW = (size.X - (cols - 1) * Gutter) / cols;
        float paneH = (size.Y - (rows - 1) * Gutter) / rows;
        int col = index % cols, row = index / cols;
        return new Rect2(col * (paneW + Gutter), row * (paneH + Gutter), paneW, paneH);
    }

    /// <summary>The private visual layer of player <paramref name="index"/>, put that player's
    /// camera-anchored copies (skydome / cloud deck) on it. The ambient cloud field is NOT one of
    /// them, the authored fogvol clutter is world-anchored and shared (see FogVolumeClutter).</summary>
    public static uint PlayerVisualLayer(int index) => 1u << (PlayerLayerBit0 + index);

    /// <summary>Player <paramref name="index"/>'s identity colour (menu cursor, HUD tags).</summary>
    public static Color PlayerColor(int index) => Colors4[Mathf.PosMod(index, Colors4.Length)];

    /// <summary>Player <paramref name="index"/>'s short tag ("P1"), used wherever several players
    /// share one list or scoreboard.</summary>
    public static string PlayerTag(int index) => $"P{index + 1}";

    /// <summary>Cull mask for player <paramref name="index"/>'s camera: everything outside the
    /// reserved per-player band (the shared world, all aircraft) plus only this player's own bit.</summary>
    public static uint PlayerCullMask(int index) =>
        (AllLayers & ~PlayerBand & ~PhotographLayer) | PlayerVisualLayer(index);

    /// <summary><paramref name="mask"/> less <see cref="PhotographLayer"/>, for a pane camera
    /// whose mask is not built by <see cref="PlayerCullMask"/>.</summary>
    public static uint PaneCullMask(uint mask) => mask & ~PhotographLayer;

    /// <summary>The visual layer player <paramref name="index"/>'s OWN airframe is drawn on, so one
    /// camera can leave that pilot's aeroplane out while every other camera, this pane's included,
    /// still draws it. The spyglass disc is the only taker: its eye stands inside the aeroplane it
    /// is looking out of. Stamp the whole airframe subtree with
    /// <see cref="SetVisualLayer"/>.</summary>
    public static uint OwnAirframeLayer(int index) =>
        1u << (OwnAirframeBit0 + Mathf.PosMod(index, MaxPlayers));

    /// <summary>Moves a whole subtree onto one visual layer (recursively, every
    /// VisualInstance3D), used on each player's private skydome / deck / puff copies.</summary>
    public static void SetVisualLayer(Node node, uint layer)
    {
        if (node is VisualInstance3D vi)
            vi.Layers = layer;
        foreach (var child in node.GetChildren())
            SetVisualLayer(child, layer);
    }

    /// <summary>Builds the pane rig for <paramref name="players"/> players (2–4), each SubViewport
    /// sharing <paramref name="mainViewport"/>'s World3D. Add the returned node to the session
    /// root; the panes re-lay out themselves on every window resize.</summary>
    public static SplitScreen Build(int players, Viewport mainViewport)
    {
        players = Mathf.Clamp(players, 2, MaxPlayers);
        var split = new SplitScreen { Name = "splitscreen", Layer = HudLayers.WorldOverlay };
        split.Init(players, mainViewport);
        return split;
    }

    /// <summary>Gives the whole window to pane 1 for the duration of a cutscene
    /// (<paramref name="on"/>), or hands the panes back. The rig is not rebuilt: every pane keeps
    /// its camera, its HUD parent, its private visual layer and its cull mask, so what changes is
    /// the rect one pane covers and which panes draw at all. The main viewport's camera stays down
    /// throughout, it is the panes that render this world, in a cutscene as in flight.</summary>
    public void Fill(bool on)
    {
        if (Filled == on)
        {
            return;
        }

        Filled = on;
        for (int i = 1; i < _panes.Count; i++)
        {
            _panes[i].Visible = !on;
            // Taken down by hand rather than left to the pane's visibility: the listener set is the
            // viewport's own property, and a session that keeps four of them is exactly the pinned
            // listener model this collapse is not allowed to change (docs/architecture.md).
            _views[i].AudioListenerEnable3D = !on;
            // And the render with it: an Always pane goes on drawing the whole world into a target
            // nobody sees, which is three copies of the shot the one visible pane is already paying
            // for.
            _views[i].RenderTargetUpdateMode = on
                ? SubViewport.UpdateMode.Disabled
                : SubViewport.UpdateMode.Always;
        }

        // The gutters and (at 3P) the empty quadrant are the backdrop's; with one pane over the
        // whole window there is nothing left for it to paint.
        _backdrop.Visible = !on;
        Relayout();
        if (on)
            Log.Info("ui", $"splitscreen: pane1 fills the window for a cutscene, {_panes.Count - 1} pane(s) down, one listener left");
        else
            Log.Info("ui", $"splitscreen: {_panes.Count} panes back, a listener and a render each");
    }

    /// <summary>Names the player who skipped a cutscene, in that player's own colour, for a few
    /// seconds over the window. Splitscreen only, because that is where the question exists: with
    /// one human there is nobody else the key press could have been.</summary>
    public void NoteSkip(int playerIndex)
    {
        _skipNotice ??= BuildSkipNotice();
        _skipNotice.Text = $"{PlayerTag(playerIndex)} SKIPPED";
        _skipNotice.Modulate = PlayerColor(playerIndex);
        _skipNotice.Visible = true;
        _skipNoticeLeft = SkipNoticeS;
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_skipNotice is not { Visible: true })
        {
            return;
        }

        _skipNoticeLeft -= delta;
        if (_skipNoticeLeft <= 0.0)
        {
            _skipNotice.Visible = false;
        }
    }

    private void Init(int players, Viewport mainViewport)
    {
        _root = new Control { Name = "panes", MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        // Backdrop: paints the gutters between panes and (at 3P) the empty fourth quadrant.
        // Without it the main viewport's environment sky shows through those pixels.
        _backdrop = new ColorRect { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
        _backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_backdrop);

        // The panes render the SAME world as the main viewport. A SubViewport nested in the tree
        // inherits its parent viewport's World3D by default; setting it explicitly documents that
        // and survives any future own_world_3d change.
        var world = mainViewport.World3D;
        // SubViewports don't pick up the project's msaa_3d setting (that applies to the root
        // viewport only), mirror it so the panes anti-alias like the single-player view.
        var msaa = (Viewport.Msaa)(int)(ProjectSettings.GetSetting(
            "rendering/anti_aliasing/quality/msaa_3d", 0).AsInt32());

        for (int i = 0; i < players; i++)
        {
            var pane = new SubViewportContainer
            {
                Name = $"pane{i + 1}",
                Stretch = true,           // the SubViewport resizes with the pane
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            var view = new SubViewport
            {
                Name = $"view{i + 1}",
                World3D = world,
                Msaa3D = msaa,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                HandleInputLocally = false, // input is polled per device, never routed per pane
                // This pane's camera is a 3D audio listener: the world is heard from whichever
                // pane is nearest it (Godot maxes the listeners per channel), not from P1.
                AudioListenerEnable3D = true,
            };
            pane.AddChild(view);
            _root.AddChild(pane);
            _panes.Add(pane);
            _views.Add(view);
        }

        _root.Resized += Relayout;
        Relayout();
    }

    // Places the panes over the current window rect: 2P stacked top/bottom or side by side by the
    // window's own shape, 3–4P in a 2×2 grid (3P's fourth quadrant stays backdrop-black). Runs on
    // every resize, so dragging a window past 2:1 flips the 2P axis under the players.
    private void Relayout()
    {
        var size = _root.Size;
        if (size.X < 1f || size.Y < 1f)
            return;
        for (int i = 0; i < _panes.Count; i++)
        {
            // Filled, the hidden panes keep their own quadrant: only pane 1 is moved, so handing
            // the window back is a visibility change and one rect rather than a re-layout.
            var rect = Filled && i == 0
                ? new Rect2(Vector2.Zero, size)
                : PaneRect(i, _panes.Count, size, SideBySide(size));
            _panes[i].Position = rect.Position;
            _panes[i].Size = rect.Size;
        }
    }

    // Built on the first skip and kept: a session nobody skips a cutscene in adds no node at all.
    // Over the panes because it is added after them, which is also why it survives the collapse.
    private Label BuildSkipNotice()
    {
        var label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        label.AddThemeFontSizeOverride("font_size", SkipNoticeFontPx);
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.OffsetBottom = -SkipNoticeInsetPx;
        _root.AddChild(label);
        return label;
    }
}
