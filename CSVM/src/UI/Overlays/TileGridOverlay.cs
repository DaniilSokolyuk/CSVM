using System;
using System.Collections.Generic;
using CSVM.Mech3;
using CSVM.UI.Boards;
using CSVM.UI.Screens;
using CSVM.Utils;
using Godot;

namespace CSVM.UI.Overlays;

/// <summary>
/// The map-edge tile-grid overlay (flag <c>--debug-tilegrid</c>, no key bound): tints every
/// ground tile, the map's own and <see cref="MapEdgeExtender"/>'s continuation cells alike, by
/// which cell it is and how it was folded, so the continuation's shape reads at the controls.
/// Hue is the repetition band's parity (<see cref="MapEdgeExtender.FoldBandIndex"/>), so a run of
/// one hue is exactly <see cref="MapEdgeExtender.BlockCells"/> cells wide; value alternates cell
/// by cell within a band. Strength is 0.2, lower than <c>ClassOverlay</c>'s 0.5, since this is
/// read while flying over terrain that must stay legible. The map's own tiles are cached once
/// like <c>ClassOverlay</c>; extension cells tint themselves as they are born instead, since they
/// are created and freed on every cell crossing. Both stamp <see cref="SceneBuilder.TintParam"/>.
/// The map-edge fold decode this overlay settled: docs/architecture.md's MapEdgeExtender.cs entry.
/// </summary>
public sealed partial class TileGridOverlay : Node
{
    // The block depths the cycle walks: today's clamp, C4's measurement, C2's measurement, one
    // past C2, and whole-map (the constant-free alternative implementation, refuted by the airport
    // observation but one step away from being checked at the controls). --map-edge-block=
    // inserts its own value in sorted position, so a CLI choice is never stranded off the cycle.
    private static readonly int[] BaseCycle = { 1, 2, 3, 4, 12 };

    // Band-parity swatches. Named for what they ARE, alternating repetition bands, rather than
    // for the fold, because band parity is what they encode and "mirrored" would be a lie in the
    // default repeat mode.
    private static readonly string[] LegendLabels =
    {
        "band even", "band odd x", "band odd z", "band odd xz",
    };

    private readonly Node3D _world;
    private readonly MapEdgeExtender? _extender;

    // The map's own ground tiles, tinted once per show and restored on hide.
    private readonly List<GeometryInstance3D> _tinted = new();

    private bool _shown, _debugDone;
    private CanvasLayer? _hudLayer;
    private Label? _hud;
    private RichTextLabel? _legend;

    public TileGridOverlay(Node3D world, MapEdgeExtender? extender)
    {
        _world = world;
        _extender = extender;
        Name = "tilegrid_overlay";
    }

    /// <summary><c>--debug-tilegrid</c>: open the overlay on the first frame. The only way to open
    /// it, no key is bound.</summary>
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

    /// <summary>Show or hide the tile-grid tints.</summary>
    public void Toggle()
    {
        if (_extender == null)
        {
            // No continuation means no grid to colour: --stage=empty, or a world with no
            // area/partition record. Say which, rather than showing an overlay that tints
            // nothing: that reads as "the grid is uniform" instead of "there is no grid".
            Log.Warn("world", $"tile-grid overlay: this world has no map-edge continuation to colour (no area/partition grid).");
            ShowNotice("NO MAP-EDGE GRID IN THIS WORLD\nThe chapter builds no area/partition grid, so there are no tiles to colour.");
            return;
        }
        if (_shown)
        {
            Restore();
            _extender.SetTileTint(false);
            _shown = false;
            HideNotice();
            Log.Info("world", $"tile-grid overlay off");
            return;
        }
        TintInMapTiles();
        _extender.SetTileTint(true);
        _shown = true;
        ShowNotice(StatusText(), showLegend: true);
        Log.Info("world", $"{StatusText()}");
    }

    /// <summary>Steps the block depth to the next value in the cycle and rebuilds the window.
    /// A no-op while the overlay is hidden, the whole point of stepping is to watch the bands
    /// change, and silently rebuilding 121 cells for an invisible effect is a hitch for
    /// nothing.</summary>
    public void StepBlock()
    {
        if (_extender == null || !_shown)
        {
            return;
        }
        var cycle = BuildCycle(_extender.MaxBlockCells, _extender.BlockCells);
        int at = cycle.IndexOf(_extender.BlockCells);
        int next = cycle[(at + 1) % cycle.Count];
        _extender.SetContinuation(next, _extender.RepeatInsteadOfMirror);
        ShowNotice(StatusText(), showLegend: true);
        Log.Info("world", $"{StatusText()}");
    }

    /// <summary>Swaps between the confirmed alternating reflection and plain repetition.
    /// Hidden-overlay no-op, for the same reason as <see cref="StepBlock"/>.</summary>
    public void ToggleMode()
    {
        if (_extender == null || !_shown)
        {
            return;
        }
        _extender.SetContinuation(_extender.BlockCells, !_extender.RepeatInsteadOfMirror);
        ShowNotice(StatusText(), showLegend: true);
        Log.Info("world", $"{StatusText()}");
    }

    /// <summary>The block-depth cycle: the standing candidates, dropped to what this grid can
    /// hold, plus whatever <c>--map-edge-block=</c> asked for so a CLI value is always reachable
    /// again after stepping away from it.</summary>
    internal static List<int> BuildCycle(int maxBlockCells, int current)
    {
        var set = new SortedSet<int>();
        foreach (int n in BaseCycle)
        {
            if (n <= maxBlockCells)
            {
                set.Add(n);
            }
        }
        set.Add(Math.Clamp(current, 1, maxBlockCells));
        return new List<int>(set);
    }

    // One coloured word per parity, straight from the extender's own palette, a palette change is
    // the only edit that can move this out of sync with what is on screen.
    private static string BuildLegendText()
    {
        var parts = new List<string>(LegendLabels.Length + 1)
        {
            $"[color=#{MapEdgeExtender.InMapLegendColor.ToHtml(false)}]in-map[/color]",
        };
        var colors = MapEdgeExtender.ParityLegendColors;
        for (int i = 0; i < LegendLabels.Length; i++)
        {
            parts.Add($"[color=#{colors[i].ToHtml(false)}]{LegendLabels[i]}[/color]");
        }
        parts.Add("(one band = one block)");
        return string.Join("   ", parts);
    }

    // Shown in the HUD and echoed to the log, so a screenshot and a log line both say which
    // configuration produced them, four captures of a coastline are otherwise indistinguishable
    // a week later.
    private string StatusText()
    {
        var ext = _extender!;
        string mode = ext.RepeatInsteadOfMirror ? "repeat" : "mirror (not the original)";
        // The rebuild cost is only known after the first fold change, so it appears rather than
        // sitting at a meaningless zero.
        string cost = ext.LastRebuildMs > 0 ? $" · rebuild {ext.LastRebuildMs:F0} ms" : "";
        return $"map edge: block {ext.BlockCells} cell(s) · {mode} · {ext.LiveCellCount} ext cells{cost}";
    }

    // The map's own ground tiles, identified through AnimRuntime.IndexMeta and binned by
    // MapEdgeExtender, so map tiles and extension cells share one piece of grid maths.
    // ⚠ Skip the extender's own subtree. Its cells share IndexMeta with their source tile and
    // would resolve here to the source's in-map colour, overwriting the parity tint the
    // extender just gave them and painting the whole continuation neutral.
    private void TintInMapTiles()
    {
        void Walk(Node n)
        {
            if (ReferenceEquals(n, _extender))
            {
                return; // the continuation tints itself, per-cell, in BuildCell
            }
            if (n is Node3D marked && marked.HasMeta(SelectionService.OverlayMeta))
            {
                return; // another overlay's drawings
            }
            if (n is MeshInstance3D mi && mi.Name == "mesh" && mi.GetParent() is Node3D owner
                && owner.HasMeta(AnimRuntime.IndexMeta)
                && _extender!.InMapTintForNode((int)owner.GetMeta(AnimRuntime.IndexMeta)) is { } tint)
            {
                mi.SetInstanceShaderParameter(SceneBuilder.TintParam, tint);
                _tinted.Add(mi);
            }
            foreach (var child in n.GetChildren())
            {
                Walk(child);
            }
        }
        Walk(_world);
    }

    private void Restore()
    {
        foreach (var node in _tinted)
        {
            if (IsInstanceValid(node))
            {
                node.SetInstanceShaderParameter(SceneBuilder.TintParam, MapEdgeExtender.UntintedMix);
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
                // Below the class overlay's readout (H), which sits at y=200..240.
                Position = new Vector2(12, 260),
                Modulate = new Color(0.85f, 0.95f, 1f),
            };
            _hud.AddThemeFontSizeOverride("font_size", 13);
            root.AddChild(_hud);
            _legend = new RichTextLabel
            {
                Position = new Vector2(12, 280),
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
