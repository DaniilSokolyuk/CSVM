using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using CSVM.Utils;
using Godot;

namespace CSVM.Mech3;

/// <summary>
/// Map-edge continuation: a rolling window of repeated border-cell terrain + clutter that
/// follows the plane past the map edge, so the world continues under the fog instead of
/// ending in a void, matching the original's own tile-reload behaviour at the controls.
/// ⚠ Repeats a border-cell BLOCK, never the map interior, see <see cref="RepeatInsteadOfMirror"/>
/// and <see cref="DefaultBlockCells"/> for the per-chapter constants and their traps, and
/// <see cref="AdoptComplements"/> for the border-strip trap. Decode: docs/formats/world-structure.md.
/// </summary>
public sealed partial class MapEdgeExtender : Node3D
{
    /// <summary>The tile-grid overlay's mix strength. Deliberately weaker than
    /// <c>ClassOverlay</c>'s 0.5: this overlay is read while flying, over terrain that must stay
    /// legible as terrain, and the band boundaries, not the band colours, carry the
    /// information.</summary>
    internal const float TintStrength = 0.2f;

    // Window radius in cells around the focus. Sized to cover the weather fog-far
    // (C1 zone2: 4000 m ≈ 4 × 1024 m tiles) plus one margin ring, so the fog border never
    // creeps onto the void even mid-cell. TUNE. ⚠ The ring sits against the FULL authored far;
    // the chapter with the longest authored far is C1B at 4,700 m, still inside 5 × 1024 m.
    private const int Rings = 5;

    // The ground-tile span gate, as fractions of a cell: the floor admits the split half-tiles,
    // the ceiling keeps multi-cell sheets out. ⚠ Named, not inlined, because they are delicate: a
    // real C5 border strip falls under the floor and is dropped from the continuation (see
    // AdoptComplements). Do not retune them without reading the --dump-tilegrid census first: lowering
    // the floor far enough to admit a strip also admits every prop mesh at the border, and
    // BuildCell would then copy those, which the design forbids.
    private const float MinSpanFraction = 0.4f;
    private const float MaxSpanFraction = 1.35f;

    // Tile-grid overlay palette, indexed by repetition-band parity: [0] both axes on an even band,
    // [1] x odd, [2] z odd, [3] both odd. Four strongly separated hues, because at TintStrength a
    // subtle palette is a grey smear over textured ground. In-map cells take InMapTint instead, so
    // the map boundary itself is never in doubt.
    private static readonly Color[] ParityTints =
    {
        new(0.20f, 0.85f, 1.00f),
        new(1.00f, 0.55f, 0.10f),
        new(0.45f, 1.00f, 0.30f),
        new(1.00f, 0.30f, 0.75f),
    };

    private static readonly Color InMapTint = new(0.80f, 0.80f, 0.85f);

    // Alpha 0 is the shader's identity mix, exactly the untinted world (same convention as
    // ClassOverlay).
    private static readonly Color Untinted = new(0f, 0f, 0f, 0f);

    private readonly GameZ _gamez;
    private readonly SceneBuilder _scene;
    private readonly float _x0, _z0, _tileX, _tileZ;
    private readonly int _cols, _rows;
    // Per source cell: its ground-tile nodes (with the accumulated ancestor transform,
    // identity for every observed tile, kept for correctness) and its clutter sprites.
    private readonly Dictionary<(int, int), List<(GameZNode Node, Transform3D ParentXf)>> _tiles = new();
    // Source is the placement's index in its export, which is what its activation is read by.
    private readonly Dictionary<(int, int), List<(int Kind, Transform3D Xf, Color Fade, int Source)>> _sprites = new();
    // Gamez node index -> its map cell, for every in-map ground tile. Lets the tile-grid overlay
    // colour the real world's own tiles through the same grid maths the extension uses, keyed on
    // the same AnimRuntime.IndexMeta stamp the built scene already carries.
    private readonly Dictionary<int, (int, int)> _tileCell = new();
    // Flat sheets the classifier rejected for being thin, kept per cell as candidates for
    // AdoptComplements. Cleared once adoption has run.
    private readonly Dictionary<(int, int),
        List<(int Idx, GameZNode Node, Transform3D ParentXf, Vector3 WorldCenter, Vector3 Span)>> _spares = new();
    // Node indices adopted by AdoptComplements, so the census reports them as the ground they now
    // are rather than as rejections.
    private readonly HashSet<int> _adopted = new();
    // Per cell, the world-space X/Z bounds its ACCEPTED tiles reach between them. ⚠ A union of
    // AABBs, not a true union of footprints: two disjoint tiles that between them touch all four
    // sides read as covering, which no chapter does (measured, all eight).
    private readonly Dictionary<(int, int), (float MinX, float MaxX, float MinZ, float MaxZ)> _coverage = new();
    private readonly IReadOnlyList<ClutterBuilder.KindExport>? _clutter;

    private readonly Dictionary<(int, int), Node3D> _live = new();

    // The map's stamp activation, which each copy follows, and the live copies per extension cell.
    private readonly ClutterActivation? _activation;
    private readonly Dictionary<(int, int), List<ClutterCopy>> _copies = new();

    // The focus cells the current window was built around, one per player (splitscreen serves
    // every pane from one window). Empty until the first Update.
    private readonly List<(int, int)> _centerCells = new();

    // The activation version the live copies last followed.
    private int _activationVersion;

    // --dump-tilegrid's raw material: one row per node ScanTiles CONSIDERED, every Object3d with
    // a mesh it reached, accepted or rejected, with the reason. Null unless the census was asked
    // for. This is the only view in which a rejection is visible: the tile-grid overlay tints the
    // accepted set, so what it does NOT paint is exactly what the continuation will be missing.
    private List<CensusCandidate>? _census;

    private int _blockCells = 1;
    private bool _repeat = true;
    private bool _tinted;
    // Starts true so the FIRST window build is timed too: it builds the whole window from nothing,
    // which is the same work an F15 fold change costs. That makes the hitch a measured number in
    // every session's log rather than something only discoverable by pressing the key.
    private bool _foldChanged = true;

    private MapEdgeExtender(GameZ gamez, SceneBuilder scene, GameZNode world,
        IReadOnlyList<ClutterBuilder.KindExport>? clutter, ClutterActivation? activation)
    {
        _gamez = gamez;
        _scene = scene;
        _clutter = clutter;
        _activation = activation;
        _activationVersion = activation?.Version ?? 0;
        _x0 = world.AreaLeft;
        _z0 = world.AreaTop;
        _cols = world.PartitionCols;
        _rows = world.PartitionRows;
        _tileX = (world.AreaRight - world.AreaLeft) / _cols;
        _tileZ = (world.AreaBottom - world.AreaTop) / _rows;
        Name = "map_edge";
    }

    /// <summary>Why <see cref="ClassifyGroundMesh"/> did, or did not, take a node as a ground tile.
    /// Recorded per candidate by the <c>--dump-tilegrid</c> census, the tile-grid overlay shows
    /// only the accepted set, so a rejection is invisible at the controls except as the void it
    /// leaves past the map edge.</summary>
    public enum TileVerdict
    {
        /// <summary>Binned into its cell and copied by every extension cell that folds to it.</summary>
        Accepted,

        /// <summary>Not an <c>Object3d</c>, or its mesh index does not resolve, or it has no
        /// vertices, not a tile candidate at all.</summary>
        NoMesh,

        /// <summary>A polygon carries a cloud/sky texture. The cloudlayer deck tiles are cell-sized
        /// too, so this is what keeps the sky out of the ground.</summary>
        SkyOrCloud,

        /// <summary>Spans less than <see cref="MinSpanFraction"/> of the cell on X or Z. ⚠ A real
        /// border terrain strip can land here, dropped for being thin, see
        /// <c>AdoptComplements</c>.</summary>
        TooSmall,

        /// <summary>Spans more than <see cref="MaxSpanFraction"/> of the cell on X or Z, a
        /// multi-cell sheet, which the per-cell copy has no way to place.</summary>
        TooLarge,
    }

    /// <summary>Extension cells currently instantiated (diagnostics).</summary>
    public int LiveCellCount => _live.Count;

    /// <summary>How many border cells deep the repeated block is; per chapter, see
    /// <see cref="DefaultBlockCells"/>.</summary>
    public int BlockCells => _blockCells;

    /// <summary>Translate the block rather than alternately reflecting it. ⚠ True by default,
    /// this is what the original does, A/B'd at the controls on every chapter. False alternately
    /// reflects, which is kept only to look at.</summary>
    public bool RepeatInsteadOfMirror => _repeat;

    /// <summary>The widest block this world can take, a block deeper than the grid has no more
    /// cells to fold into. At this value the continuation mirrors the whole map.</summary>
    public int MaxBlockCells => Math.Max(1, Math.Min(_cols, _rows));

    /// <summary>Whether the tile-grid overlay's tints are currently applied.</summary>
    public bool TileTintShown => _tinted;

    /// <summary>Wall milliseconds the last fold-change rebuild took, 0 before the first one. Shown
    /// in the tile-grid overlay's HUD so the F15 hitch is a number on screen rather than a
    /// feeling.</summary>
    public double LastRebuildMs { get; private set; }

    /// <summary>The shader's identity mix, what the overlay stamps to clear a tint.</summary>
    internal static Color UntintedMix => Untinted;

    /// <summary>The four band-parity colours, indexed <c>(x band odd ? 1 : 0) | (z band odd ? 2 : 0)</c>
    ///, for the overlay's legend, so a palette edit here cannot drift out of sync with the key on
    /// screen (the rule <c>ClassOverlay.BuildLegendText</c> follows).</summary>
    internal static IReadOnlyList<Color> ParityLegendColors => ParityTints;

    /// <summary>The in-map cells' legend colour. See <see cref="ParityLegendColors"/>.</summary>
    internal static Color InMapLegendColor => InMapTint;

    // ---------------------------------------------------------------- fold mapping

    /// <summary>How many border cells deep this chapter's repeated block is, all eight measured
    /// by A/B against the original at the controls. ⚠ Do not size this from a video-derived
    /// period; fly it. An unknown chapter falls back to 1. Decode: docs/formats/world-structure.md.
    /// </summary>
    public static int DefaultBlockCells(string? chapter) => chapter?.ToUpperInvariant() switch
    {
        "C1" or "C2" or "C4" => 2,
        _ => 1,
    };

    /// <summary>Which repetition band an out-of-map index falls in: 0 inside the map, then ±1, ±2…
    /// outward, one step per <paramref name="block"/> cells. The overlay colours by this, not by
    /// <see cref="FoldAxis"/>'s flip, under repetition nothing ever flips, so keying on the flip
    /// paints the whole continuation one colour.</summary>
    public static int FoldBandIndex(int i, int n, int block)
    {
        if (i >= 0 && i < n)
            return 0;
        int b = Math.Clamp(block, 1, n);
        int t = i - (i >= n ? n - b : 0);
        // Floor division: C# truncates toward zero, and the low edge's offsets are all negative.
        return t >= 0 ? t / b : ((t + 1) / b) - 1;
    }

    /// <summary>Continuation along one axis: outside the map, the nearest <paramref name="block"/>
    /// border cells repeat forever, or under mirroring reflect at every seam. The block occupies
    /// source indices <c>[a, a+block)</c>; offsets from <c>a</c> reduce modulo the period, a
    /// mirror period of <c>2·block</c> versus a repeat period of <c>block</c>. At
    /// <paramref name="block"/> = 1, <paramref name="repeat"/> false reduces to the pre-2026-08-08
    /// clamp, pinned by <c>MapEdgeFoldTests</c>.</summary>
    public static (int Src, bool Flip) FoldAxis(int i, int n, int block, bool repeat)
    {
        if (i >= 0 && i < n)
            return (i, false);
        int b = Math.Clamp(block, 1, n);
        int a = i >= n ? n - b : 0;
        int period = repeat ? b : 2 * b;
        // C# '%' keeps the sign of the dividend, and the low edge always has a negative offset.
        int m = (((i - a) % period) + period) % period;
        return m < b ? (a + m, false) : (a + ((2 * b) - 1 - m), true);
    }

    /// <summary>The ground-tile decision, as a pure function of ONE mesh, vertices, polygon
    /// texture names, cell size, kept separate from <see cref="IsGroundTile"/> so it can be
    /// pinned by test. ⚠ A rejected node leaves a hole its cell's own width in every extension
    /// copy; see <see cref="AdoptComplements"/>. <paramref name="center"/>/<paramref name="span"/>
    /// are filled on every verdict except <see cref="TileVerdict.NoMesh"/>.</summary>
    public static TileVerdict ClassifyGroundMesh(IReadOnlyList<Vector3> vertices,
        IEnumerable<string?> polygonTextures, float tileX, float tileZ,
        out Vector3 center, out Vector3 span)
    {
        center = Vector3.Zero;
        span = Vector3.Zero;
        if (vertices.Count == 0)
        {
            return TileVerdict.NoMesh;
        }
        Vector3 min = vertices[0], max = vertices[0];
        foreach (var v in vertices)
        {
            min = min.Min(v);
            max = max.Max(v);
        }
        center = (min + max) * 0.5f;
        span = max - min;
        float spanX = span.X, spanZ = span.Z;
        // Before the span gate: the cloudlayer deck tiles are cell-sized too, so
        // size alone cannot tell them from ground. Node names are unreliable here (cloud layers
        // ship under generic names like 'g27517'), WorldBuilder.IsCloudOrSkyTexture is the rule.
        foreach (var tex in polygonTextures)
        {
            if (tex != null && WorldBuilder.IsCloudOrSkyTexture(tex))
            {
                return TileVerdict.SkyOrCloud;
            }
        }
        if (spanX < MinSpanFraction * tileX || spanZ < MinSpanFraction * tileZ)
        {
            return TileVerdict.TooSmall;
        }
        if (spanX > MaxSpanFraction * tileX || spanZ > MaxSpanFraction * tileZ)
        {
            return TileVerdict.TooLarge;
        }
        return TileVerdict.Accepted;
    }

    /// <summary>Whether a mesh the classifier dropped for being thin looks like a strip of ground
    /// that COMPLETES a cell: flat on both horizontal axes, and spanning the full cell on one of
    /// them. ⚠ Flatness alone is not enough, it also matches hangar floors and city-block
    /// rooftops. A qualifying mesh is only a CANDIDATE for <see cref="AdoptComplements"/>, never
    /// an acceptance on its own.</summary>
    public static bool IsCompletionStrip(Vector3 span, float tileX, float tileZ) =>
        span.Y < span.X && span.Y < span.Z
        && (span.X >= 0.999f * tileX || span.Z >= 0.999f * tileZ);

    /// <summary>Re-points the continuation at a different block depth or fold mode, rebuilding the
    /// whole window. Debug/prototype path only, frees and rebuilds every live cell in one frame,
    /// roughly ten times a normal cell-crossing diff, so expect a visible hitch.</summary>
    public void SetContinuation(int blockCells, bool repeat)
    {
        blockCells = Math.Clamp(blockCells, 1, MaxBlockCells);
        if (blockCells == _blockCells && repeat == _repeat)
            return;
        _blockCells = blockCells;
        _repeat = repeat;
        _foldChanged = true;
        foreach (var (_, node) in _live)
            node.QueueFree();
        _live.Clear();
        _copies.Clear();
        // Forces the next Update past its no-op check, which rebuilds the window around the
        // unchanged focus.
        _centerCells.Clear();
    }

    /// <summary>Show or hide the tile-grid tints on the extension cells. Cheap, it re-stamps a
    /// per-instance shader parameter on what is already built and never rebuilds a cell. New cells
    /// pick the state up in <see cref="BuildCell"/> as they are born, which is why this lives here
    /// rather than in a walk-once overlay: extension cells churn on every boundary crossing, so a
    /// cached tint list would go stale within seconds of flying.</summary>
    public void SetTileTint(bool on)
    {
        _tinted = on;
        foreach (var (cell, node) in _live)
            ApplyTint(node, cell.Item1, cell.Item2);
    }

    /// <summary>Single-focus convenience overload (the single-player flight camera).</summary>
    public void Update(Vector3 focus) => Update(new[] { focus });

    /// <summary>Re-centers the window on the focus points (one per player camera, splitscreen
    /// serves every pane from this one window, so the wanted set is the union of the rings around
    /// each). Cheap no-op until one of them crosses into another cell; then freed/built cells diff
    /// by ~one window row. Call once per frame.</summary>
    public void Update(IReadOnlyList<Vector3> focuses)
    {
        if (focuses.Count == 0)
            return;
        // Before the no-op check: a stamp switched while the window stands still must still follow.
        SyncActivation();
        // No-op unless some focus changed cell (the common case, every frame).
        bool moved = focuses.Count != _centerCells.Count;
        for (int i = 0; !moved && i < focuses.Count; i++)
            moved = CellOf(focuses[i]) != _centerCells[i];
        if (!moved)
            return;
        _centerCells.Clear();
        foreach (var f in focuses)
            _centerCells.Add(CellOf(f));

        var wanted = new HashSet<(int, int)>();
        foreach (var (fx, fz) in _centerCells)
            for (int dx = -Rings; dx <= Rings; dx++)
                for (int dz = -Rings; dz <= Rings; dz++)
                {
                    var c = (fx + dx, fz + dz);
                    // In-map cells are the real world's, never duplicated by the extension.
                    if (c.Item1 >= 0 && c.Item1 < _cols && c.Item2 >= 0 && c.Item2 < _rows)
                        continue;
                    wanted.Add(c);
                }

        List<(int, int)>? drop = null;
        foreach (var (cell, node) in _live)
            if (!wanted.Contains(cell))
            {
                node.QueueFree();
                (drop ??= new List<(int, int)>()).Add(cell);
            }
        if (drop != null)
            foreach (var cell in drop)
            {
                _live.Remove(cell);
                _copies.Remove(cell);
            }

        // A fold change (F15/F16) drops the whole window and rebuilds it here, which is ~10x a
        // normal crossing's diff. Time it and say so rather than leaving the stutter unexplained:
        // a debug key that hitches is fine, a debug key that hitches for an unknown reason is not.
        long startedAt = _foldChanged ? Stopwatch.GetTimestamp() : 0L;
        int built = 0;
        foreach (var cell in wanted)
            if (!_live.ContainsKey(cell))
            {
                var node = BuildCell(cell.Item1, cell.Item2);
                AddChild(node);
                _live[cell] = node;
                built++;
            }
        if (_foldChanged)
        {
            _foldChanged = false;
            double ms = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            LastRebuildMs = ms;
            string mode = _repeat ? "repeat" : "mirror";
            Log.Info("world", $"map edge: built {built} cells in {ms:F0} ms (block {_blockCells}, {mode})");
        }
    }

    /// <summary>Builds the extender for a world, or null when it carries no usable area/partition
    /// grid. <paramref name="clutter"/> lets the extension grow the same decorations the map
    /// grows, matching the original's continued-terrain clutter.</summary>
    /// <param name="blockCells">Initial <see cref="BlockCells"/>, clamped to the grid.</param>
    /// <param name="repeat">Initial <see cref="RepeatInsteadOfMirror"/>.</param>
    /// <param name="census">Record every candidate for <see cref="WriteCensus"/>. Off normally.</param>
    internal static MapEdgeExtender? Create(GameZ gamez, SceneBuilder scene, GameZNode world,
        ClutterBuilder? clutter, int blockCells = 1, bool repeat = true, bool census = false)
    {
        if (!world.HasArea || world.PartitionCols <= 0 || world.PartitionRows <= 0
            || world.AreaRight <= world.AreaLeft || world.AreaBottom <= world.AreaTop)
            return null;
        var ext = new MapEdgeExtender(gamez, scene, world, clutter?.ExportedKinds, clutter?.Activation);
        ext._blockCells = Math.Clamp(blockCells, 1, ext.MaxBlockCells);
        ext._repeat = repeat;
        if (census)
        {
            ext._census = new List<CensusCandidate>();
        }
        ext.ScanTiles(world);
        ext.AdoptComplements();
        if (ext._tiles.Count == 0)
            return null;
        ext.BinClutter();
        return ext;
    }

    /// <summary>The tile-grid tint for a built in-map ground tile, identified by the gamez node
    /// index the scene carries as <c>AnimRuntime.IndexMeta</c>; null when not one of the map's
    /// ground tiles. Binned by the same grid maths <see cref="ScanTiles"/> uses, so the split
    /// half-tiles agree with the overlay. Includes the alpha for <see cref="SceneBuilder.TintParam"/>.
    /// </summary>
    internal Color? InMapTintForNode(int gamezNodeIndex) =>
        _tileCell.TryGetValue(gamezNodeIndex, out var cell)
            ? new Color(TintFor(cell.Item1, cell.Item2), TintStrength)
            : null;

    /// <summary>The clutter copies the live window holds, and how many of them draw nothing, read
    /// off the MultiMeshes themselves. The census a stamp's activation is checked against.</summary>
    internal (int Copies, int Hidden) ClutterCopyCensus()
    {
        int copies = 0, hidden = 0;
        foreach (var list in _copies.Values)
        {
            foreach (var copy in list)
            {
                copies++;
                if (copy.Mm.GetInstanceTransform(copy.Index).Basis.Determinant() == 0f)
                    hidden++;
            }
        }
        return (copies, hidden);
    }

    /// <summary>The <c>--dump-tilegrid</c> report: every tile candidate with its
    /// <see cref="TileVerdict"/>, plus a per-cell roll-up, as indented JSON. Answers three
    /// questions at once: a missing cell, a short tile (<c>coverX</c>/<c>coverZ</c> below 1), or
    /// partial coverage (an accepted cell with a rejected candidate too). <c>suspect</c> marks a
    /// border cell hitting any of the three. Null when built without a census.</summary>
    internal string? WriteCensus(string? chapter) => BuildCensusReport(chapter);

    // World transform mapping source cell (sx, sz) geometry onto target cell (ix, iz):
    // pure translation on an unflipped axis, reflection about the shared mirror plane on a
    // flipped one (x' = 2a − x with a = the plane between the copies).
    private Transform3D MirrorTransform(int ix, int iz, int sx, bool fx, int sz, bool fz)
    {
        float ox = fx ? 2 * _x0 + (sx + ix + 1) * _tileX : (ix - sx) * _tileX;
        float oz = fz ? 2 * _z0 + (sz + iz + 1) * _tileZ : (iz - sz) * _tileZ;
        return new Transform3D(
            Basis.FromScale(new Vector3(fx ? -1 : 1, 1, fz ? -1 : 1)),
            new Vector3(ox, 0, oz));
    }

    // The partition-grid cell a world position falls in (may be outside the map, that is the
    // whole point; negative/oversize indices address extension cells).
    private (int, int) CellOf(Vector3 p) => (
        Mathf.FloorToInt((p.X - _x0) / _tileX),
        Mathf.FloorToInt((p.Z - _z0) / _tileZ));

    // ---------------------------------------------------------------- cell building

    // One extension cell: the mirrored ground leaves (mesh + collider, no child subtrees,
    // so edge buildings/props are never duplicated) under a reflection-transform holder,
    // plus the source cell's clutter sprites at mirrored positions (billboards re-face the
    // camera by shader, so mirroring a sprite is just mirroring its planted point).
    private Node3D BuildCell(int ix, int iz)
    {
        var (sx, fx) = FoldAxis(ix, _cols, _blockCells, _repeat);
        var (sz, fz) = FoldAxis(iz, _rows, _blockCells, _repeat);
        var cell = new Node3D { Name = $"ext_{ix}_{iz}" };
        var mirror = MirrorTransform(ix, iz, sx, fx, sz, fz);

        if (_tiles.TryGetValue((sx, sz), out var tiles))
        {
            var ground = new Node3D { Name = "ground", Transform = mirror };
            foreach (var (node, parentXf) in tiles)
            {
                // zoneGate: inherit the source tile's zone_id culling, or the base map's ground
                // vanishes above the deck while its mirrored continuation keeps drawing.
                var leaf = _scene.BuildSubtree(node, skip: n => !ReferenceEquals(n, node),
                    zoneGate: true);
                if (leaf == null)
                    continue;
                if (parentXf != Transform3D.Identity)
                {
                    var wrap = new Node3D { Transform = parentXf };
                    wrap.AddChild(leaf);
                    ground.AddChild(wrap);
                }
                else
                {
                    ground.AddChild(leaf);
                }
            }
            cell.AddChild(ground);
        }

        if (_clutter != null && _sprites.TryGetValue((sx, sz), out var sprites))
            _copies[(ix, iz)] = AddCellClutter(cell, mirror, sprites);
        if (_tinted)
            ApplyTint(cell, ix, iz);
        return cell;
    }

    // ---------------------------------------------------------------- tile-grid overlay

    // Stamps (or clears) one extension cell's tile-grid tint. Ground only, not the clutter
    // multimeshes, tinting a forest's sprites one colour buries the tile boundaries the overlay
    // exists to show. Through SceneBuilder.TintParam; never MaterialOverride/MaterialOverlay,
    // a different shader entirely (see SceneBuilder.TintLine).
    private void ApplyTint(Node cell, int ix, int iz)
    {
        var color = _tinted ? new Color(TintFor(ix, iz), TintStrength) : Untinted;
        void Walk(Node n)
        {
            if (n is MeshInstance3D mi && mi.Name == "mesh")
                mi.SetInstanceShaderParameter(SceneBuilder.TintParam, color);
            foreach (var child in n.GetChildren())
                Walk(child);
        }
        Walk(cell);
    }

    // Hue is the repetition band's parity (FoldBandIndex), so a colour band's width reads off the
    // block depth directly; value alternates per cell inside the band. In-map cells take a
    // neutral tint. ⚠ Keyed on the band, not FoldAxis's flip: under repetition nothing ever flips.
    private Color TintFor(int ix, int iz)
    {
        bool inMap = ix >= 0 && ix < _cols && iz >= 0 && iz < _rows;
        Color baseColor;
        if (inMap)
        {
            baseColor = InMapTint;
        }
        else
        {
            int bx = FoldBandIndex(ix, _cols, _blockCells);
            int bz = FoldBandIndex(iz, _rows, _blockCells);
            baseColor = ParityTints[(int)((uint)bx & 1) | ((int)((uint)bz & 1) << 1)];
        }
        // Godot's '%' on ints keeps the dividend's sign, and extension indices go negative.
        bool dark = (((ix + iz) % 2) + 2) % 2 == 1;
        return dark ? baseColor.Darkened(0.35f) : baseColor;
    }

    // A 3D decoration carries the mirror's reflection in its own basis, so the export carries whole
    // transforms. A sprite mirrors as position alone, re-faced by the billboard shader.
    // ⚠ Extension 3D decorations ARE collidable, one PhysicsServer3D.BodyAddShape per building
    // against ClutterBuilder's one shared shape per mesh. Do not merge into one region trimesh;
    // that would have to rebuild on the frame the camera crosses a cell boundary.
    // Returns every copy it made, so a later activation change can reach it (SyncActivation).
    private List<ClutterCopy> AddCellClutter(Node3D cell, Transform3D mirror,
        List<(int Kind, Transform3D Xf, Color Fade, int Source)> sprites)
    {
        var copies = new List<ClutterCopy>();
        // Group the cell's decorations per kind (kept in kind order for determinism). Each copy
        // keeps its source stamp's fade thresholds, so the continuation fades where the map does.
        var byKind = new Dictionary<int, List<(Transform3D Xf, Color Fade, int Source)>>();
        foreach (var (kind, xf, fade, source) in sprites)
        {
            if (!byKind.TryGetValue(kind, out var list))
                byKind[kind] = list = new List<(Transform3D, Color, int)>();
            list.Add((_clutter![kind].Solid ? mirror * xf
                : new Transform3D(Basis.Identity, mirror * xf.Origin), fade, source));
        }

        // One body for the whole cell's buildings, named so a crash log locates the cell.
        // Created lazily: most cells are sea or forest and have no solid decoration at all.
        StaticBody3D? solidBody = null;

        foreach (var (kindIndex, placements) in byKind)
        {
            var kind = _clutter![kindIndex];
            var bodyRid = default(Rid);
            int firstSlot = -1;
            if (kind.Solid && kind.CollisionShape is { } shape)
            {
                if (solidBody == null)
                {
                    solidBody = new StaticBody3D { Name = "clutter_bld_ext" };
                    cell.AddChild(solidBody);
                }
                bodyRid = solidBody.GetRid();
                firstSlot = PhysicsServer3D.BodyGetShapeCount(bodyRid);
                var shapeRid = shape.GetRid();
                foreach (var (xf, _, _) in placements)
                    PhysicsServer3D.BodyAddShape(bodyRid, shapeRid, xf);
            }
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseCustomData = true,   // before InstanceCount, or the fade has no slot
                Mesh = kind.Mesh,
                InstanceCount = placements.Count,
            };
            for (int i = 0; i < placements.Count; i++)
            {
                mm.SetInstanceTransform(i, placements[i].Xf);
                mm.SetInstanceCustomData(i, placements[i].Fade);
                var copy = new ClutterCopy(mm, i, kindIndex, placements[i].Source, placements[i].Xf,
                    bodyRid, firstSlot < 0 ? -1 : firstSlot + i);
                copies.Add(copy);
                if (_activation != null && _activation.IsHidden(kindIndex, copy.Source))
                    Show(copy, false);
            }
            var mmi = new MultiMeshInstance3D
            {
                Multimesh = mm,
                MaterialOverride = kind.Material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                // The billboard shader swings verts outside the AABB, and a scale_range draw can
                // make a card up to 3× its authored width, ClutterBuilder measured the margin
                // these placements need, so the extension uses that rather than the raw width.
                ExtraCullMargin = kind.CullMargin,
                Name = $"clutter_{kindIndex}",
            };
            if (kind.Solid)
                mmi.SetInstanceShaderParameter("node_bias", kind.NodeBias);
            cell.AddChild(mmi);
        }
        return copies;
    }

    // Every live copy re-reads its source stamp, once per change the activation reports. Copies
    // are never cratered, so a copy's state is exactly its source's.
    private void SyncActivation()
    {
        if (_activation == null || _activation.Version == _activationVersion)
            return;
        _activationVersion = _activation.Version;
        foreach (var copies in _copies.Values)
            foreach (var copy in copies)
                Show(copy, !_activation.IsHidden(copy.Kind, copy.Source));
    }

    // A hidden copy collapses as a cratered stamp does, and its shape slot is switched off.
    private void Show(ClutterCopy copy, bool shown)
    {
        copy.Mm.SetInstanceTransform(copy.Index, shown
            ? copy.Xf : new Transform3D(copy.Xf.Basis.Scaled(Vector3.Zero), copy.Xf.Origin));
        if (copy.Shape >= 0)
            PhysicsServer3D.BodySetShapeDisabled(copy.Body, copy.Shape, !shown);
    }

    // ---------------------------------------------------------------- source scan

    // Collects the map's ground tiles per grid cell: every Object3d (recursively, the
    // map-center airfield tiles hang one level down under group nodes) whose own mesh is a
    // single ~cell-sized terrain/water sheet, excluding cloud/sky sheets (the cloudlayer
    // deck tiles are cell-sized too). Verified on C1: all 144 cells covered (147 tiles,
    // split half-tile strips and the coastal falls overlay bin alongside their cell's base
    // tile), zero rotated or translated ancestors.
    private void ScanTiles(GameZNode world)
    {
        void Walk(int idx, Transform3D parentXf)
        {
            if (idx < 0 || idx >= _gamez.Nodes.Count)
                return;
            var node = _gamez.Nodes[idx];
            if (WorldBuilder.SkipWorldNode(node))
                return;
            if (node.Kind == "Lod" && node.LodRangeMin != 0f)
                return;
            var xf = node.Local is { } local ? parentXf * local : parentXf;
            var verdict = ClassifyNode(node, out var center, out var span);
            if (verdict == TileVerdict.Accepted)
            {
                var worldCenter = xf * center;
                int cx = Mathf.Clamp(Mathf.FloorToInt((worldCenter.X - _x0) / _tileX), 0, _cols - 1);
                int cz = Mathf.Clamp(Mathf.FloorToInt((worldCenter.Z - _z0) / _tileZ), 0, _rows - 1);
                if (!_tiles.TryGetValue((cx, cz), out var list))
                    _tiles[(cx, cz)] = list = new List<(GameZNode, Transform3D)>();
                list.Add((node, parentXf));
                _tileCell[idx] = (cx, cz);
                AccumulateCoverage((cx, cz), worldCenter, span);
            }
            else if (verdict == TileVerdict.TooSmall && IsCompletionStrip(span, _tileX, _tileZ))
            {
                // Held as a spare rather than accepted outright, AdoptComplements decides.
                var worldCenter = xf * center;
                int cx = Mathf.Clamp(Mathf.FloorToInt((worldCenter.X - _x0) / _tileX), 0, _cols - 1);
                int cz = Mathf.Clamp(Mathf.FloorToInt((worldCenter.Z - _z0) / _tileZ), 0, _rows - 1);
                if (!_spares.TryGetValue((cx, cz), out var spares))
                {
                    _spares[(cx, cz)] = spares = new List<(int, GameZNode, Transform3D, Vector3, Vector3)>();
                }
                spares.Add((idx, node, parentXf, worldCenter, span));
            }
            if (_census != null && verdict != TileVerdict.NoMesh)
            {
                RecordCandidate(idx, node, xf * center, verdict, span);
            }
            foreach (var c in node.Children)
                Walk(c, xf);
        }
        foreach (var c in world.Children)
            Walk(c, Transform3D.Identity);
        if (world.PartitionNodes != null)
            foreach (var idx in world.PartitionNodes)
                Walk(idx, Transform3D.Identity);
    }

    // Widens a cell's accepted-coverage box by one tile's world-space footprint.
    private void AccumulateCoverage((int, int) cell, Vector3 worldCenter, Vector3 span)
    {
        float minX = worldCenter.X - (span.X * 0.5f), maxX = worldCenter.X + (span.X * 0.5f);
        float minZ = worldCenter.Z - (span.Z * 0.5f), maxZ = worldCenter.Z + (span.Z * 0.5f);
        _coverage[cell] = _coverage.TryGetValue(cell, out var c)
            ? (Math.Min(c.MinX, minX), Math.Max(c.MaxX, maxX), Math.Min(c.MinZ, minZ), Math.Max(c.MaxZ, maxZ))
            : (minX, maxX, minZ, maxZ);
    }

    // How much of a cell its ground box spans, per axis, as a fraction of the cell, the census's
    // coverX/coverZ. Clipped to the cell, so a tile overhanging its neighbour cannot report more
    // than full coverage.
    private void CoverageOf((int, int) cell, out float coverX, out float coverZ)
    {
        coverX = 0f;
        coverZ = 0f;
        if (!_coverage.TryGetValue(cell, out var c))
        {
            return;
        }
        float x0 = _x0 + (cell.Item1 * _tileX), z0 = _z0 + (cell.Item2 * _tileZ);
        coverX = Math.Max(0f, Math.Min(x0 + _tileX, c.MaxX) - Math.Max(x0, c.MinX)) / _tileX;
        coverZ = Math.Max(0f, Math.Min(z0 + _tileZ, c.MaxZ) - Math.Max(z0, c.MinZ)) / _tileZ;
    }

    // Whether a cell's accepted tiles reach all four of its sides. A metre of slack, because the
    // spans are float differences of authored vertices and an exactly-cell-sized tile lands a hair
    // either side of its bound.
    private bool CellIsCovered((int, int) cell)
    {
        if (!_coverage.TryGetValue(cell, out var c))
        {
            return false;
        }
        const float Slack = 1f;
        float x0 = _x0 + (cell.Item1 * _tileX), z0 = _z0 + (cell.Item2 * _tileZ);
        return c.MinX <= x0 + Slack && c.MaxX >= x0 + _tileX - Slack
            && c.MinZ <= z0 + Slack && c.MaxZ >= z0 + _tileZ - Slack;
    }

    // A cell short of ground adopts the flat sheets the classifier dropped for being thin, or
    // every extension copy of that cell carries a hole its footprint. ⚠ Keyed on the coverage
    // test, never the sheet alone: a dropped sheet in an already-covered cell is not adopted.
    // Not a widening of ClassifyGroundMesh; that still answers "is this a tile".
    private void AdoptComplements()
    {
        foreach (var (cell, spares) in _spares)
        {
            if (CellIsCovered(cell))
            {
                continue;
            }
            if (!_tiles.TryGetValue(cell, out var list))
            {
                _tiles[cell] = list = new List<(GameZNode, Transform3D)>();
            }
            foreach (var spare in spares)
            {
                list.Add((spare.Node, spare.ParentXf));
                _tileCell[spare.Idx] = cell;
                _adopted.Add(spare.Idx);
                AccumulateCoverage(cell, spare.WorldCenter, spare.Span);
                Log.Info("world", $"map edge: cell ({cell.Item1},{cell.Item2}) is short of ground, adopting dropped sheet '{spare.Node.Name}' ({spare.Span.X:F0} x {spare.Span.Z:F0} m).");
            }
        }
        _spares.Clear();
    }

    // A ground tile: an Object3d whose own mesh is a single ~one-cell terrain/water sheet
    // (the regular 1024-unit tiles plus split half-tiles), NOT the cloudlayer deck / sky
    // sheets (one-cell too). `center` = the mesh AABB centroid in node-local space.
    private bool IsGroundTile(GameZNode n, out Vector3 center) =>
        ClassifyNode(n, out center, out _) == TileVerdict.Accepted;

    // The node-level half of the classifier: the Object3d requirement and the mesh lookup that
    // ClassifyGroundMesh is deliberately free of, so the decision itself stays a pure function.
    private TileVerdict ClassifyNode(GameZNode n, out Vector3 center, out Vector3 span)
    {
        center = Vector3.Zero;
        span = Vector3.Zero;
        if (n.Kind != "Object3d" || n.MeshIndex < 0 || n.MeshIndex >= _gamez.Meshes.Count)
            return TileVerdict.NoMesh;
        var mesh = _gamez.Meshes[n.MeshIndex];
        return ClassifyGroundMesh(mesh.Vertices, PolygonTextures(mesh), _tileX, _tileZ,
            out center, out span);
    }

    // One texture name per polygon, null where the material index does not resolve, the shape
    // ClassifyGroundMesh and the census both read, so neither re-walks the material table.
    private IEnumerable<string?> PolygonTextures(GameZMesh mesh)
    {
        foreach (var poly in mesh.Polygons)
        {
            yield return poly.MaterialIndex >= 0 && poly.MaterialIndex < _gamez.Materials.Count
                ? _gamez.Materials[poly.MaterialIndex].TextureName
                : null;
        }
    }

    // Bins the chapter's clutter sprites (world positions from the main build) into their
    // map cells, so each extension cell can mirror exactly its source cell's trees.
    private void BinClutter()
    {
        if (_clutter == null)
            return;
        for (int k = 0; k < _clutter.Count; k++)
        {
            var placements = _clutter[k].Placements;
            var fades = _clutter[k].Fades;
            for (int i = 0; i < placements.Count; i++)
            {
                var xf = placements[i];
                int cx = Mathf.FloorToInt((xf.Origin.X - _x0) / _tileX);
                int cz = Mathf.FloorToInt((xf.Origin.Z - _z0) / _tileZ);
                if (cx < 0 || cx >= _cols || cz < 0 || cz >= _rows)
                    continue;
                if (!_sprites.TryGetValue((cx, cz), out var list))
                    _sprites[(cx, cz)] = list = new List<(int, Transform3D, Color, int)>();
                list.Add((k, xf, fades[i], i));
            }
        }
    }

    // ---------------------------------------------------------------- census (--dump-tilegrid)

    // One census row, taken at the moment ScanTiles decided. The cell is the CLAMPED bin the scan
    // itself uses, so a candidate whose centre falls outside the grid reports the cell it would
    // have been forced into rather than an index no cell can hold.
    private void RecordCandidate(int idx, GameZNode node, Vector3 worldCenter, TileVerdict verdict,
        Vector3 span)
    {
        var textures = new SortedSet<string>(StringComparer.Ordinal);
        var surfaces = new SortedSet<string>(StringComparer.Ordinal);
        if (node.MeshIndex >= 0 && node.MeshIndex < _gamez.Meshes.Count)
        {
            foreach (var tex in PolygonTextures(_gamez.Meshes[node.MeshIndex]))
            {
                if (tex == null)
                {
                    continue;
                }
                textures.Add(tex);
                surfaces.Add(SceneBuilder.ClassifySurface(tex) ?? "default");
            }
        }
        _census!.Add(new CensusCandidate
        {
            Node = idx,
            Name = node.Name,
            Verdict = verdict.ToString(),
            Cx = Mathf.Clamp(Mathf.FloorToInt((worldCenter.X - _x0) / _tileX), 0, _cols - 1),
            Cz = Mathf.Clamp(Mathf.FloorToInt((worldCenter.Z - _z0) / _tileZ), 0, _rows - 1),
            CoverX = span.X / _tileX,
            CoverZ = span.Z / _tileZ,
            SpanX = span.X,
            SpanY = span.Y,
            SpanZ = span.Z,
            Textures = string.Join(" ", textures),
            Surfaces = string.Join(" ", surfaces),
        });
    }

    // The report itself, down here with the other census members; WriteCensus up top is the
    // documented entry point.
    private string? BuildCensusReport(string? chapter)
    {
        if (_census == null)
        {
            return null;
        }
        var accepted = new Dictionary<(int, int), int>();
        var rejected = new Dictionary<(int, int), List<string>>();
        var adoptions = new List<string>();
        foreach (var row in _census)
        {
            var key = (row.Cx, row.Cz);
            // An adopted sheet is reported as what it is, part of the cell's ground, with the
            // classifier's own verdict kept in the row, so the adoption stays legible.
            if (row.Verdict == nameof(TileVerdict.Accepted) || _adopted.Contains(row.Node))
            {
                accepted.TryGetValue(key, out int count);
                accepted[key] = count + 1;
            }
            else
            {
                if (!rejected.TryGetValue(key, out var list))
                {
                    rejected[key] = list = new List<string>();
                }
                list.Add(Log.Format($"{row.Verdict} {row.Name} {row.CoverX:F3}x{row.CoverZ:F3} [{row.Surfaces}]"));
            }
            if (_adopted.Contains(row.Node))
            {
                adoptions.Add(Log.Format($"({row.Cx},{row.Cz}) {row.Name} {row.CoverX:F3}x{row.CoverZ:F3} [{row.Surfaces}]"));
            }
        }
        var cells = new List<object>();
        int suspects = 0;
        for (int cz = 0; cz < _rows; cz++)
        {
            for (int cx = 0; cx < _cols; cx++)
            {
                var key = (cx, cz);
                bool border = cx < _blockCells || cx >= _cols - _blockCells
                    || cz < _blockCells || cz >= _rows - _blockCells;
                accepted.TryGetValue(key, out int count);
                rejected.TryGetValue(key, out var r);
                // Coverage is read off the cell's accumulated ground box, NOT off the widest single
                // tile: a cell's ground is routinely two split half-tiles that each span half of it
                // and together span all of it, and a per-tile maximum reports those as holes.
                CoverageOf(key, out float coverX, out float coverZ);
                bool covered = coverX >= 0.999f && coverZ >= 0.999f;
                bool suspect = border && (count == 0 || !covered);
                if (suspect)
                {
                    suspects++;
                }
                cells.Add(new
                {
                    cx,
                    cz,
                    border,
                    accepted = count,
                    coverX,
                    coverZ,
                    rejected = r?.Count ?? 0,
                    suspect,
                    why = r == null ? string.Empty : string.Join(" | ", r),
                });
            }
        }
        var report = new
        {
            chapter = chapter ?? string.Empty,
            grid = new { cols = _cols, rows = _rows, tileX = _tileX, tileZ = _tileZ, x0 = _x0, z0 = _z0 },
            blockCells = _blockCells,
            repeat = _repeat,
            candidateCount = _census.Count,
            acceptedCount = accepted.Count,
            adoptedCount = _adopted.Count,
            adopted = adoptions,
            suspectCells = suspects,
            cells,
            candidates = _census,
        };
        return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    }

    // One extension copy of a map stamp: where it sits in its cell's MultiMesh and body, and which
    // export placement it copies.
    private readonly record struct ClutterCopy(MultiMesh Mm, int Index, int Kind, int Source,
        Transform3D Xf, Rid Body, int Shape);

    // A census row. A class rather than a tuple because it is serialized straight to JSON, and the
    // property names ARE the report's column names.
    private sealed class CensusCandidate
    {
        /// <summary>The gamez node index, the same stamp the built scene carries as
        /// <c>AnimRuntime.IndexMeta</c>, so a row can be tied back to what is on screen.</summary>
        public int Node { get; init; }

        /// <summary>The gamez node name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary><see cref="TileVerdict"/> as text.</summary>
        public string Verdict { get; init; } = string.Empty;

        /// <summary>Grid column it binned into (clamped to the grid).</summary>
        public int Cx { get; init; }

        /// <summary>Grid row it binned into (clamped to the grid).</summary>
        public int Cz { get; init; }

        /// <summary>X span as a fraction of the cell, 1.0 is a tile that spans its cell exactly,
        /// and anything under <see cref="MinSpanFraction"/> is why a candidate was dropped.</summary>
        public float CoverX { get; init; }

        /// <summary>Z span as a fraction of the cell.</summary>
        public float CoverZ { get; init; }

        /// <summary>X span in metres.</summary>
        public float SpanX { get; init; }

        /// <summary>Y span in metres, the flat/upright test. A dropped ground strip is a sheet
        /// (a few metres of terrain relief); a dropped lamp post is tens of metres tall on a
        /// footprint of centimetres.</summary>
        public float SpanY { get; init; }

        /// <summary>Z span in metres.</summary>
        public float SpanZ { get; init; }

        /// <summary>Distinct texture names on its polygons, space-separated.</summary>
        public string Textures { get; init; } = string.Empty;

        /// <summary>Distinct <c>SceneBuilder.ClassifySurface</c> results, space-separated, the
        /// field that decides whether a rejected candidate is terrain that SHOULD be copied or a
        /// prop that must not be.</summary>
        public string Surfaces { get; init; } = string.Empty;
    }
}
