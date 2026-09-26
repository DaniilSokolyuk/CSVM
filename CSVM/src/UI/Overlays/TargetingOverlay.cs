using System;
using System.Collections.Generic;
using CSVM.Flight.Airframe;
using CSVM.Flight.Weapons;
using CSVM.UI.Boards;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Overlays;

/// <summary>
/// The targeting overlay (key F15, flag <c>--debug-targets</c>): a line from each shooter to the
/// target it has actually acquired, rebuilt every frame. It draws a turret gunner's
/// <see cref="TurretController.TargetPosition"/> with its <see cref="TurretController.Gate"/>, and
/// an AI pilot's <see cref="Flight.Ai.AiGunner.Target"/> with its
/// <see cref="Flight.Ai.AiGunner.WantsFire"/>, both from their own live state, never re-derived.
/// The line's colour is the answer: red firing, amber tracking, grey held; the HUD names each held
/// shooter's gate. One instance sits under the shared world root, so every splitscreen pane draws
/// the same lines with no per-pane copy.
/// ⚠ Depth test off, unlike <c>AiNetsOverlay</c>: a line into a hull is the one worth seeing.
/// </summary>
public sealed partial class TargetingOverlay : Node
{
    // How many shooters the roll-call prints before it says how many it left out. A screenful,
    // not a census: the counts in the header are the census.
    private const int RollCallRows = 12;

    // Firing this tick: every gate passed.
    private static readonly Color FiringColor = new(1f, 0.25f, 0.2f);

    // Acquired, tracking, trigger held by a gate that will pass on its own (the shot
    // clock, a bored window, a barrel still slewing).
    private static readonly Color TrackingColor = new(1f, 0.8f, 0.2f);

    // Acquired and held by a gate that will NOT pass where it stands: no intercept
    // solution, or the line of sight is blocked.
    private static readonly Color HeldColor = new(0.55f, 0.6f, 0.7f);

    private readonly Func<IReadOnlyList<TurretController>> _turrets;
    private readonly Func<IReadOnlyList<FlightController>> _aircraft;

    private bool _shown, _debugDone;
    private Node3D? _holder;
    private MeshInstance3D? _lines;
    private ImmediateMesh? _mesh;
    private StandardMaterial3D? _material;
    private CanvasLayer? _hudLayer;
    private Label? _hud;

    /// <param name="turrets">Every world emplacement this session built.</param>
    /// <param name="aircraft">Every live aircraft: each contributes its carried turret gunners
    /// and, if it is AI-piloted, its own gunner.</param>
    public TargetingOverlay(Func<IReadOnlyList<TurretController>> turrets,
        Func<IReadOnlyList<FlightController>> aircraft)
    {
        _turrets = turrets;
        _aircraft = aircraft;
        Name = "targeting_overlay";
    }

    /// <summary><c>--debug-targets</c>: open on the first frame, the scripted stand-in for the
    /// F15 press.</summary>
    public bool DebugShow { get; init; }

    public override void _Process(double delta)
    {
        if (DebugShow && !_debugDone)
        {
            _debugDone = true;   // deferred a frame: the world subtree is final only after the build
            Toggle();
        }
        if (_shown)
        {
            Redraw();
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.F15 })
        {
            return;
        }
        Toggle();
        GetViewport().SetInputAsHandled();
    }

    /// <summary>F15: show or hide the sight lines.</summary>
    public void Toggle()
    {
        if (_shown)
        {
            _holder?.QueueFree();
            _holder = null;
            _lines = null;
            _mesh = null;
            _shown = false;
            if (_hudLayer != null)
            {
                _hudLayer.Visible = false;
            }
            Log.Info("world", $"targeting overlay off");
            return;
        }
        _material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            NoDepthTest = true,   // a line INTO a hull is the interesting one; see the summary
        };
        _mesh = new ImmediateMesh();
        _lines = new MeshInstance3D
        {
            Name = "targeting_lines",
            Mesh = _mesh,
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        _holder = new Node3D { Name = "targeting_draw" };
        _holder.SetMeta(SelectionService.OverlayMeta, true);
        _holder.AddChild(_lines);
        AddChild(_holder);
        _shown = true;
        Log.Info("world", $"targeting overlay on (F15): red = firing, amber = tracking, grey = held");
        Redraw();
    }

    private static Color ColorOf(TurretGate gate) => gate switch
    {
        TurretGate.Firing => FiringColor,
        TurretGate.Blocked or TurretGate.NoSolution or TurretGate.NoAmmo => HeldColor,
        _ => TrackingColor,
    };

    private static string GateName(TurretGate gate) => gate switch
    {
        TurretGate.Firing => "firing",
        TurretGate.Blocked => "blocked",
        TurretGate.NoSolution => "no solution",
        TurretGate.NoAmmo => "no ammo",
        TurretGate.Reloading => "shot clock",
        TurretGate.Bored => "bored",
        TurretGate.Slewing => "slewing",
        _ => gate.ToString().ToLowerInvariant(),
    };

    // One frame's lines, plus the HUD roll-call. Rebuilt whole: every endpoint moves.
    private void Redraw()
    {
        if (_mesh == null)
        {
            return;
        }
        _mesh.ClearSurfaces();
        // Two lists, aeroplanes printed first, because the roll-call is capped and one airship
        // carries seventeen turrets: a single list would spend the whole cap on emplacements and
        // never show the four aircraft the run is actually about.
        var rows = new List<string>();
        var pilotRows = new List<string>();
        int firing = 0, tracking = 0, held = 0, idle = 0;

        // ⚠ The surface opens on the FIRST line, not before the walk: SurfaceEnd on an empty
        // surface is a per-frame engine error, and a frame with nothing to draw is ordinary,
        // every gunner idle at once is the normal state whenever no hostile is in reach.
        bool begun = false;
        void Line(Vector3 from, Vector3 to, Color color)
        {
            if (!begun)
            {
                _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _material);
                begun = true;
            }
            _mesh.SurfaceSetColor(color);
            _mesh.SurfaceAddVertex(from);
            _mesh.SurfaceSetColor(color);
            _mesh.SurfaceAddVertex(to);
        }

        void Turret(TurretController t)
        {
            if (t.Gate is TurretGate.Asleep or TurretGate.Dead or TurretGate.NoTarget)
            {
                idle++;
                return;
            }
            var color = ColorOf(t.Gate);
            Line(t.WorldPosition, t.TargetPosition, color);
            if (t.Gate == TurretGate.Firing)
            {
                firing++;
            }
            else if (color == HeldColor)
            {
                held++;
            }
            else
            {
                tracking++;
            }
            rows.Add($"{t.Label}  {GateName(t.Gate)}  {Flight.Airframe.FlightController.TargetLabel(t.TargetSource)}"
                     + $"  {t.WorldPosition.DistanceTo(t.TargetPosition):0} m");
        }

        foreach (var t in _turrets())
        {
            Turret(t);
        }
        foreach (var rig in _aircraft())
        {
            if (!GodotObject.IsInstanceValid(rig) || !rig.InPlay)
            {
                continue;
            }
            foreach (var t in rig.Turrets)
            {
                Turret(t);
            }
            // The AI gunner's own standing target, of any class: its line starts at the airframe,
            // not at a muzzle, because the D14 gunner aims the whole aeroplane.
            if (rig.Pilot?.Gunner is { } gunner
                && FlightController.TryTargetGeometry(gunner.Target,
                    out var preyPos, out _, out _, out bool preyLive)
                && preyLive)
            {
                var color = gunner.WantsFire ? FiringColor : TrackingColor;
                Line(rig.WorldPosition, preyPos, color);
                if (gunner.WantsFire)
                {
                    firing++;
                }
                else
                {
                    tracking++;
                }
                pilotRows.Add($"{rig.Name}  {(gunner.WantsFire ? "firing" : "tracking")}  " +
                              $"{FlightController.TargetLabel(gunner.Target)}  " +
                              $"{rig.WorldPosition.DistanceTo(preyPos):0} m");
            }
        }
        if (begun)
        {
            _mesh.SurfaceEnd();
        }

        pilotRows.AddRange(rows);
        int shown = Math.Min(pilotRows.Count, RollCallRows);
        ShowHud($"targets (F15): {firing} firing, {tracking} tracking, {held} held, {idle} without a target"
            + (pilotRows.Count > shown ? $" ({pilotRows.Count - shown} more not listed)" : string.Empty)
            + "\n" + string.Join("\n", pilotRows.GetRange(0, shown)));
    }

    private void ShowHud(string text)
    {
        if (_hudLayer == null)
        {
            _hudLayer = new CanvasLayer { Layer = HudLayers.Debug };
            var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _hud = new Label
            {
                // Below the ai-nets readout (F13), which sits at y=260..310.
                Position = new Vector2(12, 330),
                Modulate = new Color(1f, 0.85f, 0.75f),
            };
            _hud.AddThemeFontSizeOverride("font_size", 13);
            root.AddChild(_hud);
            _hudLayer.AddChild(root);
            AddChild(_hudLayer);
        }
        _hud!.Text = text;
        _hudLayer.Visible = true;
    }
}
