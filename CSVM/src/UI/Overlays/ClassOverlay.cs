using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Overlays;

/// <summary>
/// The colour-by-class overlay (key H): every drawn mesh in the live world tinted a flat colour
/// by what it is (destructible / facade / clutter / plain scenery), so a target named in a
/// report can actually be found at the controls. Classification reuses the exact mechanisms a
/// hit already uses, never a guess: this module's entry in docs/architecture.md.
/// ⚠ Never key classification on <see cref="SceneBuilder.SurfaceMeta"/>. That tag answers what a
/// bullet does here, not what the object is, and a dock and a skyscraper can share a surface tag.
/// </summary>
public sealed partial class ClassOverlay : Node
{
    // How much of the class colour is mixed over the object's real appearance. At 0.5 a target
    // reads as its class and stays recognisable as itself. Applied via SceneBuilder.TintParam,
    // never a MaterialOverride: an installed material is a different shader from the world's
    // (opposite cull default, no depth bias, no clutter billboard spin), so it renders wrong and
    // cannot blend with the texture the way a parameter into the real shader can.
    private const float TintStrength = 0.5f;

    private static readonly Color DestructibleColor = new(0.95f, 0.15f, 0.15f);
    private static readonly Color FacadeColor = new(1f, 0.35f, 0.85f);
    private static readonly Color ClutterColor = new(0.45f, 0.95f, 0.25f);
    private static readonly Color SceneryColor = new(0.3f, 0.55f, 0.95f);

    // Alpha 0 = the shader's identity mix, i.e. exactly the untinted world.
    private static readonly Color Untinted = new(0f, 0f, 0f, 0f);

    // The legend's own class list, always shown regardless of what actually drew this session
    // (same rule as ColliderOverlay: a map legend describes the key, not just
    // what is currently on screen).
    private static readonly string[] LegendClasses = { "destructible", "facade", "clutter", "scenery" };

    private readonly Node3D _world;
    private readonly GameZ? _gamez;
    private readonly AnimRuntime? _runtime;

    private readonly List<GeometryInstance3D> _tinted = new();

    private bool _shown, _debugDone;
    private CanvasLayer? _hudLayer;
    private Label? _hud;
    private RichTextLabel? _legend;
    private string _summary = "";

    public ClassOverlay(Node3D world, GameZ? gamez, AnimRuntime? runtime)
    {
        _world = world;
        _gamez = gamez;
        _runtime = runtime;
        Name = "class_overlay";
    }

    /// <summary><c>--debug-classoverlay</c>: open the overlay on the first frame, the scripted
    /// stand-in for the H press.</summary>
    public bool DebugShow { get; init; }

    public override void _Process(double delta)
    {
        if (DebugShow && !_debugDone)
        {
            // Deferred one frame like every other --debug-* opener: the world subtree is only
            // final once the session has finished building.
            _debugDone = true;
            Toggle();
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        // H rather than X: the original spends X on Fire Rockets (docs/controls.md), and a tint
        // toggling under every rocket shot is worse than an overlay on a duller key. It stays a
        // letter because the F13 upward block's numbers are the user's own keypad rows.
        if (@event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.H })
        {
            return;
        }
        Toggle();
        GetViewport().SetInputAsHandled();
    }

    /// <summary>H: show or hide the class tints. Rebuilt every time it is shown rather than cached
    /// once, a destructible's death can swap its subtree (wreck pieces replace the healthy mesh),
    /// so a stale cache would tint a node that no longer exists and miss the one that does.</summary>
    public void Toggle()
    {
        if (_gamez == null)
        {
            // No gamez content to classify: --stage=empty and no other mode reaches this overlay
            // without one. Say so rather than showing an overlay that tints nothing, which would
            // read as "nothing here has a class" instead of "there is no world here".
            Log.Warn("world", $"class overlay: this mode builds no gamez world to classify (--stage=empty), there is nothing to tint.");
            ShowNotice("NO WORLD CONTENT IN THIS MODE\n--stage=empty builds no gamez content to classify.");
            return;
        }
        if (_shown)
        {
            Restore();
            _shown = false;
            HideNotice();
            Log.Info("world", $"class overlay off");
            return;
        }
        Build();
        _shown = true;
        ShowNotice(_summary, showLegend: true);
    }

    private static Color ColorFor(string cls) => cls switch
    {
        "destructible" => DestructibleColor,
        "facade" => FacadeColor,
        "clutter" => ClutterColor,
        _ => SceneryColor,
    };

    // One coloured word per class, straight from ColorFor, a palette change is the only edit
    // that can move it out of sync with the legend text.
    private static string BuildLegendText()
    {
        var parts = new List<string>(LegendClasses.Length);
        foreach (var cls in LegendClasses)
        {
            parts.Add($"[color=#{ColorFor(cls).ToHtml(false)}]{cls}[/color]");
        }
        return string.Join("   ", parts);
    }

    // Which class a built world node's own mesh belongs to. `owner` is
    // the structural node SceneBuilder built for it (the "mesh" MeshInstance3D's parent), the
    // same node DestructibleRegistry.Resolve and AnimRuntime.IndexMeta
    // are keyed on.
    private string ClassOf(Node3D owner)
    {
        if (_runtime != null && _runtime.Destructibles.Resolve(owner) != null)
        {
            return "destructible";
        }
        if (owner.HasMeta(AnimRuntime.IndexMeta))
        {
            int index = (int)owner.GetMeta(AnimRuntime.IndexMeta);
            if (index >= 0 && index < _gamez!.Nodes.Count)
            {
                int meshIndex = _gamez.Nodes[index].MeshIndex;
                if (meshIndex >= 0 && meshIndex < _gamez.Meshes.Count
                    && SceneBuilder.ClassifyBillboard(_gamez.Meshes[meshIndex]) is { } kind
                    && kind != SceneBuilder.BillboardKind.None)
                {
                    return "facade";
                }
            }
        }
        return "scenery";
    }

    private void Build()
    {
        var perClass = new Dictionary<string, int>();

        void Tint(GeometryInstance3D gi, string cls)
        {
            _tinted.Add(gi);
            gi.SetInstanceShaderParameter(SceneBuilder.TintParam, new Color(ColorFor(cls), TintStrength));
            perClass[cls] = perClass.GetValueOrDefault(cls) + 1;
        }

        void Walk(Node n)
        {
            if (n is Node3D marked && marked.HasMeta(SelectionService.OverlayMeta))
            {
                return; // our own drawings (and any other overlay's)
            }
            switch (n)
            {
                // Every MultiMeshInstance3D under the world root is ClutterBuilder's output;
                // weather/particle multimeshes live under the session root, not here.
                case MultiMeshInstance3D mmi:
                    Tint(mmi, "clutter");
                    break;
                case MeshInstance3D mi when mi.Name == "mesh" && mi.GetParent() is Node3D owner:
                    Tint(mi, ClassOf(owner));
                    break;
            }
            foreach (var child in n.GetChildren())
            {
                Walk(child);
            }
        }
        Walk(_world);

        var parts = new List<string>();
        foreach (var (cls, count) in perClass)
        {
            parts.Add($"{cls} {count}");
        }
        parts.Sort(System.StringComparer.Ordinal);
        _summary = $"class overlay: {string.Join(" · ", parts)}";
        Log.Info("world", $"{_summary}");
    }

    private void Restore()
    {
        foreach (var node in _tinted)
        {
            if (IsInstanceValid(node))
            {
                node.SetInstanceShaderParameter(SceneBuilder.TintParam, Untinted);
            }
        }
        _tinted.Clear();
    }

    private void ShowNotice(string text, bool showLegend = false)
    {
        if (_hudLayer == null)
        {
            _hudLayer = new CanvasLayer { Layer = HudLayers.Debug };
            var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _hud = new Label
            {
                // Below the collider overlay's readout (C), which sits at y=120..160.
                Position = new Vector2(12, 200),
                Modulate = new Color(0.85f, 0.75f, 1f),
            };
            _hud.AddThemeFontSizeOverride("font_size", 13);
            root.AddChild(_hud);
            _legend = new RichTextLabel
            {
                Position = new Vector2(12, 220),
                Size = new Vector2(900, 24),
                BbcodeEnabled = true,
                FitContent = true,
                ScrollActive = false,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Text = BuildLegendText(),
            };
            _legend.AddThemeFontSizeOverride("normal_font_size", 13);
            _legend.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
            root.AddChild(_legend);
            _hudLayer.AddChild(root);
            AddChild(_hudLayer);
        }
        _hud!.Text = text;
        _legend!.Visible = showLegend;
        _hudLayer.Visible = true;
    }

    private void HideNotice()
    {
        if (_hudLayer != null)
        {
            _hudLayer.Visible = false;
        }
    }
}
