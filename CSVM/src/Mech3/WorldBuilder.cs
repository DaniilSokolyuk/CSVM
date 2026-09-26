using System;
using System.Collections.Generic;
using CSVM.Utils;
using Godot;

namespace CSVM.Mech3;

/// <summary>One <c>zone*</c> child of the gamez <c>horizon</c> node, and how many meshed nodes its
/// subtree carries, the dome <see cref="WorldBuilder.BuildHorizon"/> would build for it.
/// <see cref="MeshedNodes"/> 0 means the zone is a bare marker, which C1B, C2 and C3 ship for
/// <c>zone2</c>. <see cref="ZoneId"/> is the zone node's own gamez <c>zone_id</c>, what
/// <see cref="ZoneGate"/> tests the built dome against per rig.</summary>
public readonly record struct HorizonZone(string Name, int MeshedNodes, int ZoneId = -1)
{
    /// <summary>The zone has a dome to build.</summary>
    public bool BuildsGeometry => MeshedNodes > 0;
}

/// <summary>
/// Builds the whole world of a chapter gamez.zbd (terrain chunks, buildings, zeppelins,
/// horizon). World content lives in two places: the World node's children, and ~350
/// top-level subtrees referenced only through the World's spatial partition grid.
/// </summary>
public sealed class WorldBuilder
{
    /// <summary>Drives the world's material texture flipbooks (animated water, surf, boat wakes,
    /// turbulence, the walking crowd). Built here so its frames resolve while the session's
    /// TextureArchive is open; the caller adds it to the scene tree. Empty on chapters whose
    /// materials carry no cycle, and it costs nothing then.</summary>
    public readonly TextureCycler Cycler = new() { Name = "TextureCycler" };

    /// <summary>Node metadata key marking a <see cref="MeshInstance3D"/> under
    /// <see cref="CloudDeck"/> as the rim extension rather than one of the 144 authored deck tiles.
    /// ⚠ Any census of deck tiles must skip a node carrying it; the extension has no undimmed twin
    /// and is not an authored tile. <c>WeatherRig.CollectDeckTiles</c> is the other reader.</summary>
    internal static readonly StringName DeckExtensionMeta = "deck_extension";

    // Share of the World node's own `area` rect a co-altitude bucket of flat quads must cover to
    // be the overcast deck. Per-chapter tile census: docs/formats/weather.md.
    // ⚠ Classify the deck structurally, never by texture name. 'cloudlayer' misses C4's Sky1.tif
    // deck entirely, and widening to 'sky*' drags in solid terrain, because skywal* is a building
    // texture inside the world walk. Altitude is no discriminator either: C4's tallest non-tile
    // root sits above its own deck.
    private const float DeckCoverageFraction = 0.5f;

    private readonly GameZ _gamez;
    private readonly TextureArchive _textures;
    private readonly SceneBuilder _scene;
    // Walk-root indices belonging to the deck; filled by FindCloudDeck before the walk.
    private readonly HashSet<int> _deckNodes = new();
    // Each built deck tile's UNDIMMED mesh, keyed by the RID of the dimmed one the tile is built
    // with; filled by Add as the deck is walked. See CloudDeckUndimmedMeshes.
    private readonly Dictionary<Rid, ArrayMesh> _deckUndimmedMeshes = new();
    // The built `cloudparent` cluster subtrees; filled by CollectCloudClusters after the walk.
    private readonly List<Node3D> _cloudClusters = new();
    // Entities the chapter parks at the world origin awaiting placement (see
    // HideUnplacedEntities): the gamez node and the subtree we built for it.
    private readonly List<(GameZNode Node, Node3D Built)> _parkedAtOrigin = new();
    // What HideUnplacedEntities switched off, still awaiting the verdict below.
    private readonly List<(GameZNode Node, Node3D Built)> _hiddenUnplaced = new();
    private GameZNode? _builtWorld; // the World node of the last Build (for CreateEdgeExtender)
    private int _deckAltitude;
    private float _deckCoverage;
    // The horizon's zone children in flat-list order, falling an absent request back to the
    // first one. Logged at most once per WorldBuilder: BuildHorizon is called once per
    // splitscreen rig, and four identical warnings would read as four separate faults.
    private bool _loggedHorizonZoneFallback;

    /// <param name="collision">Attach static colliders to solid geometry, so the flight loop can
    /// raycast terrain and buildings. Off for static viewing.</param>
    /// <param name="scrollOverrides">Per-model UV scroll rates from the mission's interp boot script
    /// (<see cref="MissionSetup.ScrollByModel"/>); null leaves each model on its gamez field.</param>
    /// <param name="debugClutterFlag"><see cref="SceneBuilder.DebugClutterFlag"/>.</param>
    /// <param name="hiddenAlpha"><see cref="SceneBuilder.HiddenAlpha"/>.</param>
    public WorldBuilder(GameZ gamez, TextureArchive textures, bool collision = false,
        IReadOnlyDictionary<int, Vector2>? scrollOverrides = null, bool debugClutterFlag = false,
        SceneBuilder.TransparencyClass hiddenAlpha = SceneBuilder.TransparencyClass.None)
    {
        _gamez = gamez;
        _textures = textures;
        // ⚠ Do not build the world double-sided. Roughly 60% of its polygons are authored
        // single-sided, and only culling resolves a back-to-back coplanar pair or keeps the
        // camera-anchored skydome off distant terrain (docs/formats/gotchas.md).
        _scene = new SceneBuilder(gamez, textures, fullbright: true,
            generateCollision: collision, blendTexture: IsCloudOrSkyTexture,
            billboardTexture: IsCloudSpriteTexture, glowTexture: IsFlareTexture,
            cullBackfaces: true, scrollOverrides: scrollOverrides);
        _scene.Cycler = Cycler;
        _scene.DebugClutterFlag = debugClutterFlag;
        _scene.HiddenAlpha = hiddenAlpha;
    }

    /// <summary>Which mission of the chapter this world is being built for, 1-based, forwarded to
    /// the scene builder because a mission-structure node authors one owner per mission
    /// (<see cref="SceneBuilder.MissionSlot"/>). Set before building.</summary>
    public int MissionSlot
    {
        get => _scene.MissionSlot;
        set => _scene.MissionSlot = value;
    }

    public int MeshInstanceCount => _scene.MeshInstanceCount;
    public int ColliderCount => _scene.ColliderCount;

    /// <summary>Models animating their UVs, see <see cref="SceneBuilder.ScrollingModelCount"/>.
    /// Read after Build (and after BuildHorizon, which is where C1's daytime sky layer is).</summary>
    public int ScrollingModelCount => _scene.ScrollingModelCount;

    /// <summary>Models built from their authored <c>lighting: false</c> / <c>fog: false</c>
    /// flags, see <see cref="SceneBuilder.UnlitModelCount"/>.</summary>
    public int UnlitModelCount => _scene.UnlitModelCount;
    public int UnfoggedModelCount => _scene.UnfoggedModelCount;

    /// <summary>Overlay-pass surfaces built, and overlay polygons declined for want of a biasable
    /// material, see <see cref="SceneBuilder.OverlayPassSurfaceCount"/>.</summary>
    public int OverlayPassSurfaceCount => _scene.OverlayPassSurfaceCount;
    public int OverlayPassDeclinedCount => _scene.OverlayPassDeclinedCount;

    /// <summary>World surfaces committed per transparency class, see
    /// <see cref="SceneBuilder.BlendSurfaceCount"/>.</summary>
    public int BlendSurfaceCount => _scene.BlendSurfaceCount;
    public int ScissorSurfaceCount => _scene.ScissorSurfaceCount;

    /// <summary>Polygons drawn as nothing for want of a texture the retail data lacks, see
    /// <see cref="SceneBuilder.UndrawnPolygonCount"/>.</summary>
    public int UndrawnPolygonCount => _scene.UndrawnPolygonCount;

    /// <summary>One-sided against two-sided collision faces per surface class, and the back-to-back
    /// pairs among them; see <see cref="SceneBuilder.CollisionSidedness"/>.</summary>
    public IReadOnlyDictionary<string, (int OneSided, int TwoSided)> CollisionSidedness => _scene.CollisionSidedness;

    public int CollisionBackToBackPairs => _scene.CollisionBackToBackPairs;

    /// <summary>The overcast deck as a separate node so the caller can make it follow the
    /// player (see GameSession): the opaque overcast sheet tracks the plane and flips
    /// above/below at the cloud band, as in the original. A child of the world root at its
    /// original altitude; null if the world has no map-covering deck (C1B/C2/C3/C5).</summary>
    public Node3D? CloudDeck { get; private set; }

    /// <summary>Each deck tile's UNDIMMED mesh, keyed by the <see cref="Rid"/> of the DIMMED mesh
    /// the tile is built with. Empty for a world with no deck. Both variants are built here
    /// because <c>forceLit</c> is a shader variant, not a uniform, so the regime switch is a mesh
    /// swap; <c>Session/WeatherRig.Tick</c> assigns one per camera at the band crossing.
    /// ⚠ Key on the RID, not the node. A splitscreen session's deck copies
    /// (<c>GameSession.AssignCloudDecks</c>) share these very resources.</summary>
    public IReadOnlyDictionary<Rid, ArrayMesh> CloudDeckUndimmedMeshes => _deckUndimmedMeshes;

    /// <summary>The gamez <c>zone_id</c> the deck tiles author, or −1 when this world has no deck
    /// or its tiles disagree. The deck is the one world subtree <see cref="ZoneGate"/> does not
    /// stamp with a zone layer, being a per-rig camera-anchored copy, so
    /// <c>Session.World.WeatherRig.Tick</c> tests this per rig instead.</summary>
    public int CloudDeckZoneId { get; private set; } = -1;

    /// <summary>The deck tiles' own AUTHORED altitude, read off the coverage-winning bucket
    /// <see cref="FindCloudDeck"/> computes. 0 for a world with no deck, never read then.
    /// ⚠ Render the deck floor here at every camera altitude, world-fixed. Do not re-pin it to
    /// <c>CLOUD_COVER</c>'s band centre, which was C4's own coincidence, and do not carry it above
    /// the camera as a ceiling; below the band <c>horizon/zone1</c>'s dome is the ceiling.</summary>
    public float CloudDeckAltitude => _deckAltitude;

    /// <summary>Mesh instances this world put on each zone-gate layer, by <c>zone_id</c>,
    /// <see cref="SceneBuilder.ZoneGatedMeshes"/> for the world walk. The before-census a state
    /// flip's delta is asserted against.</summary>
    public IReadOnlyList<int> ZoneGatedMeshes => _scene.ZoneGatedMeshes;

    /// <summary>The world's placed <c>cloudparent</c> cluster subtrees, in walk order, the
    /// ambient cloud population that is ordinary world geometry rather than <c>fvol</c> clutter.
    /// Some chapters ship none. Empty until <see cref="Build"/> has run.
    /// ⚠ Resolve these by the node's original gamez name (<c>AnimRuntime.NameMeta</c>), never by
    /// <c>Node.Name</c>. Siblings share the name <c>cloudparent</c>, so Godot's duplicate-sibling
    /// renaming is free to have touched the built name.</summary>
    public IReadOnlyList<Node3D> CloudClusters => _cloudClusters;

    /// <summary>This world's shared scene builder, its mesh/material/shape caches and its
    /// fullbright world materials. Handed to <see cref="ClutterBuilder"/> so the clutter's 3D
    /// city-block decorations render as real world geometry (same shader, same fog, same depth
    /// bias) instead of through the billboard path, and share the caches with the placed world.</summary>
    internal SceneBuilder Scene => _scene;

    /// <summary>
    /// Every gamez node the <c>--node=</c> request names, in flat-list order. Duplicate names are
    /// normal in this data, which is why this returns a list.
    /// ⚠ Match on the source name (<see cref="AnimRuntime.NameMeta"/>), case-insensitively and
    /// with the <c>.flt</c> suffix optional. Godot node names are sanitized and auto-renamed.
    /// </summary>
    public static List<GameZNode> MatchNodes(GameZ gamez, string request)
    {
        var hits = new List<GameZNode>();
        foreach (var n in gamez.Nodes)
        {
            if (n.Name.Length == 0)
            {
                continue;
            }
            if (string.Equals(n.Name, request, StringComparison.OrdinalIgnoreCase)
                || (n.Name.EndsWith(".flt", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(n.Name[..^4], request, StringComparison.OrdinalIgnoreCase)))
            {
                hits.Add(n);
            }
        }
        return hits;
    }

    /// <summary>Distinct source names CONTAINING the request, what a miss offers instead of
    /// nothing, so a mistyped <c>--node=</c> is one line away from the right spelling.</summary>
    public static List<string> SuggestNodes(GameZ gamez, string request, int cap)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var n in gamez.Nodes)
        {
            if (n.Name.Length == 0 || !n.Name.Contains(request, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (seen.Add(n.Name) && names.Count < cap)
            {
                names.Add(n.Name);
            }
        }
        return names;
    }

    /// <summary>World-frame union of a built subtree's mesh AABBs, from the meshes and node
    /// transforms rather than <c>GlobalTransform</c>, so it is valid before the subtree joins the
    /// scene tree. Null when the subtree draws nothing.
    /// ⚠ Do not switch this to <c>GlobalTransform</c>; on a detached node it returns identity and
    /// logs an error per call.</summary>
    public static Aabb? DetachedWorldAabb(Node3D root) => SubtreeAabb(root, root.Transform);

    /// <summary>The <c>horizon</c> node's zone children in gamez order, each with the meshed-node
    /// count its subtree carries. Empty when the chapter ships no <c>horizon</c> node. Static over
    /// a <see cref="GameZ"/> so it needs no built scene and is testable off-engine.
    /// ⚠ Read this before the horizon build. Three chapters ship a <c>zone2</c> that is a bare
    /// marker, so requesting it renders no sky at all; the selection rule is
    /// <c>Flight.Airframe.WeatherState.ResolveZone</c>.</summary>
    public static IReadOnlyList<HorizonZone> HorizonZonesOf(GameZ gamez)
    {
        var zones = new List<HorizonZone>();
        var horizon = gamez.FindByName("horizon");
        if (horizon == null)
            return zones;
        foreach (var childIndex in horizon.Children)
        {
            if (childIndex < 0 || childIndex >= gamez.Nodes.Count)
                continue;
            var child = gamez.Nodes[childIndex];
            if (!child.Name.StartsWith("zone", StringComparison.OrdinalIgnoreCase))
                continue;
            zones.Add(new HorizonZone(child.Name, MeshedNodesIn(gamez, child), child.ZoneId));
        }
        return zones;
    }

    /// <summary>Which horizon zones this world builds a DOME for, in build order:
    /// <paramref name="activeZone"/> first, then every other zone <see cref="ZoneGate"/> can tell
    /// apart from it. A deck chapter needs both, because the original draws the dome of the zone
    /// its camera is in and below the deck that is <c>zone1</c>'s.
    /// ⚠ Decide this from the gate's own arithmetic, never a chapter list: a second dome is added
    /// only if it builds geometry and its gateable <c>zone_id</c> is not already taken.</summary>
    public static IReadOnlyList<string> DomeZonesToBuild(
        IReadOnlyList<HorizonZone> zones, string activeZone)
    {
        var built = new List<string> { activeZone };
        int activeId = -1;
        foreach (var z in zones)
            if (z.Name.Equals(activeZone, StringComparison.OrdinalIgnoreCase))
                activeId = z.ZoneId;
        if (ZoneGate.LayerFor(activeId) == 0)
            return built;
        var taken = new List<int> { activeId };
        foreach (var z in zones)
        {
            if (z.Name.Equals(activeZone, StringComparison.OrdinalIgnoreCase) || !z.BuildsGeometry)
                continue;
            if (ZoneGate.LayerFor(z.ZoneId) == 0 || taken.Contains(z.ZoneId))
                continue;
            taken.Add(z.ZoneId);
            built.Add(z.Name);
        }
        return built;
    }

    /// <summary>The gamez <c>zone_id</c> every <c>fvol*</c> volume node in this world authors, or
    /// −1 when the chapter ships none or they disagree. The <c>fvol</c> sprite field
    /// (<c>CSVM.Effects.FogVolumeClutter</c>) is gated with those volumes.
    /// ⚠ Read it per chapter and never assume zone 2. C2B ships its volumes at −1, and assuming
    /// otherwise hides its ambient cloud field below the deck (docs/formats/weather.md).</summary>
    public static int FogVolumeZoneIdOf(GameZ gamez)
    {
        int? common = null;
        foreach (var n in gamez.Nodes)
        {
            if (!IsFogVolumeNode(n))
                continue;
            if (common is { } got && got != n.ZoneId)
                return -1; // mixed, ungated is the only reading that cannot hide authored content
            common = n.ZoneId;
        }
        return common ?? -1;
    }

    /// <summary>The same coverage-winning altitude bucket <see cref="CloudDeckAltitude"/> exposes,
    /// as a pure function of the raw <see cref="GameZ"/> data, so a test can pin a chapter's
    /// authored deck altitude with no scene build. Null when the chapter has no map-covering deck
    /// or no <paramref name="worldName"/> world node. Shares <see cref="FlatTileOf"/> with
    /// <see cref="FindCloudDeck"/> so the two cannot drift.
    /// ⚠ Read the altitude from the data, never hardcode a chapter's value.</summary>
    public static float? CloudDeckAltitudeOf(GameZ gamez, string worldName = "world1")
    {
        GameZNode? world = null;
        foreach (var n in gamez.Nodes)
            if (n.Kind == "World" && string.Equals(n.Name, worldName, StringComparison.OrdinalIgnoreCase))
            {
                world = n;
                break;
            }
        if (world == null || !world.HasArea)
            return null;
        float mapW = Mathf.Abs(world.AreaRight - world.AreaLeft);
        float mapH = Mathf.Abs(world.AreaBottom - world.AreaTop);
        if (mapW <= 0f || mapH <= 0f)
            return null;
        float mapLeft = Mathf.Min(world.AreaLeft, world.AreaRight);
        float mapRight = Mathf.Max(world.AreaLeft, world.AreaRight);
        float mapTop = Mathf.Min(world.AreaTop, world.AreaBottom);
        float mapBottom = Mathf.Max(world.AreaTop, world.AreaBottom);

        var roots = new List<int>(world.Children);
        if (world.PartitionNodes != null)
            roots.AddRange(world.PartitionNodes);

        var buckets = new Dictionary<int, float>();
        var seen = new HashSet<int>();
        foreach (var idx in roots)
        {
            if (idx < 0 || idx >= gamez.Nodes.Count || !seen.Add(idx))
                continue;
            var n = gamez.Nodes[idx];
            if (SkipWorldNode(n) || !FlatTileOf(gamez, n, out float y, out float x0, out float z0,
                    out float x1, out float z1))
                continue;
            float w = Mathf.Min(x1, mapRight) - Mathf.Max(x0, mapLeft);
            float h = Mathf.Min(z1, mapBottom) - Mathf.Max(z0, mapTop);
            if (w <= 0f || h <= 0f)
                continue; // wholly outside the map rect
            int key = Mathf.RoundToInt(y);
            buckets[key] = buckets.TryGetValue(key, out float area) ? area + w * h : w * h;
        }

        float best = DeckCoverageFraction * mapW * mapH;
        float? altitude = null;
        foreach (var (alt, area) in buckets)
            if (area > best)
            {
                best = area;
                altitude = alt;
            }
        return altitude;
    }

    /// <inheritdoc cref="HorizonZonesOf"/>
    public IReadOnlyList<HorizonZone> HorizonZones() => HorizonZonesOf(_gamez);

    public Node3D Build(string worldName = "world1")
    {
        GameZNode? world = null;
        foreach (var n in _gamez.Nodes)
            if (n.Kind == "World" && string.Equals(n.Name, worldName, StringComparison.OrdinalIgnoreCase))
            {
                world = n;
                break;
            }
        if (world == null)
            throw new ArgumentException($"world node '{worldName}' not found in GameZ data");

        var root = new Node3D { Name = worldName };
        // The cloudlayer deck is collected into its own node (kept a child of the world root at
        // identity, so its world-space tile geometry stays put) that GameSession moves to follow
        // the player; everything else goes into the static world root.
        var deck = new Node3D { Name = "cloud_deck" };

        var roots = new List<int>(world.Children);
        if (world.PartitionNodes != null)
            roots.AddRange(world.PartitionNodes);

        FindCloudDeck(world, roots);
        _deckUndimmedMeshes.Clear();
        RankConflicts(roots);
        foreach (var idx in roots)
            Add(root, deck, idx);

        // Said out loud per build because "0 / 0 / 0" from a gate that stopped stamping is
        // otherwise indistinguishable from a chapter that authors no zoned content.
        var gated = _scene.ZoneGatedMeshes;
        Log.Info("world", $"zone gate: {gated[1]} / {gated[2]} / {gated[3]} mesh instance(s) on zone 1 / 2 / 3 (deck zone_id {CloudDeckZoneId}, fvol zone_id {FogVolumeZoneIdOf(_gamez)})");

        if (deck.GetChildCount() > 0)
        {
            // ⚠ The tile census below must keep reading `_deckNodes.Count`; the rim extension
            // added here is a child of `deck`, so `GetChildCount()` would count it as a tile.
            AddDeckAnnulus(deck, MergedLocalAabb(deck));
            root.AddChild(deck);
            CloudDeck = deck;
            // The deck covers the whole map, so a shadow-casting one puts the entire world in
            // shade. Its overcast is already in the authored SUNLIGHT the sun is driven from.
            DisableShadows(deck);
            Log.Info("world", $"cloud deck: {_deckNodes.Count} tiles at y={_deckAltitude} ({_deckCoverage:P0} of the map)");
        }

        // A post-walk pass because the clusters are nested too deep for a walk root to recognise.
        // Logged per chapter so a real "none" cannot read like a census that stopped working.
        CollectCloudClusters(root);
        if (_cloudClusters.Count > 0)
        {
            Log.Info("world", $"cloud clusters: {_cloudClusters.Count} placed 'cloudparent' subtree(s)");
            // Cloud sprites are alpha billboards facing the camera, so their silhouette in a light
            // pass is whatever they happen to be turned to; ground shade from them is not authored.
            foreach (var cluster in _cloudClusters)
                DisableShadows(cluster);
        }

        _builtWorld = world;
        return root;
    }

    /// <summary>Builds ONE named subtree as a standalone stage instead of the whole world
    /// (<c>--node=</c>), at its world transform rather than the origin.
    /// ⚠ Keep this unlike <see cref="Build"/> in three ways, each of which would erase the subject:
    /// no <see cref="SkipWorldNode"/> filter, no cloud-deck split, and no origin-parked
    /// registration, which would switch off the very vehicle a <c>--node=</c> run asks for.
    /// </summary>
    public Node3D BuildNode(GameZ gamez, GameZNode node)
    {
        var root = new Node3D { Name = "world1" };
        var built = _scene.BuildSubtree(node, skip: null, collisionSkip: NoCollisionNode);
        if (built != null)
        {
            built.Transform = gamez.WorldTransformOf(node);
            root.AddChild(built);
        }
        return root;
    }

    /// <summary>The map-edge continuation rig (<see cref="MapEdgeExtender"/>), or null when the
    /// world carries no area grid or no recognizable ground tiles. Call after <see cref="Build"/>
    /// and after the chapter's clutter build, then drive its <c>Update(cameraPos)</c> each frame.
    /// ⚠ Pass <c>MapEdgeExtender.DefaultBlockCells(chapter)</c> for <paramref name="blockCells"/>
    /// unless the CLI overrode it. The default here is the safe-everywhere 1-cell repeat, because
    /// this method does not know the chapter.</summary>
    public MapEdgeExtender? CreateEdgeExtender(ClutterBuilder? clutter = null,
        int blockCells = 1, bool repeat = true, bool census = false) =>
        _builtWorld == null ? null
            : MapEdgeExtender.Create(_gamez, _scene, _builtWorld, clutter, blockCells, repeat, census);

    /// <summary>Switches off the entities nothing ever placed, returning the names switched off. A
    /// chapter's build script parks every mission's vehicles at the world origin, and retail data
    /// leaves a heap of them wherever a mission's own script misses one.
    /// ⚠ Call this only after the animation runtime's bootstrap, once every mechanism that places
    /// or hides one has run; still being at the origin then is the definition of unplaced.
    /// ⚠ Never call it without <see cref="RestorePlacedEntities"/>; alone it is wrong.</summary>
    public List<string> HideUnplacedEntities()
    {
        _hiddenUnplaced.Clear();
        var hidden = new List<string>();
        foreach (var (node, built) in _parkedAtOrigin)
        {
            if (!GodotObject.IsInstanceValid(built) || !built.Visible)
            {
                continue; // the mission's setup script already switched it off
            }
            if (!built.Transform.Origin.IsZeroApprox())
            {
                continue; // an ON_STARTUP translate placed it, this is real, shown content
            }
            built.Visible = false; // colliders follow (WorldCollision)
            _hiddenUnplaced.Add((node, built));
            hidden.Add(node.Name);
        }
        return hidden;
    }

    /// <summary>The other half of <see cref="HideUnplacedEntities"/>: restores anything that has
    /// since moved off the world origin, because moving proves a definition owns it after all.
    /// ⚠ Call this repeatedly, not once. An OnCall definition can start its motion at any time, so
    /// there is no deadline after which it is safe to stop asking. It costs one vector compare per
    /// node still hidden, and each drops out of the list for good once restored.</summary>
    public List<string> RestorePlacedEntities()
    {
        var restored = new List<string>();
        for (int i = _hiddenUnplaced.Count - 1; i >= 0; i--)
        {
            var (node, built) = _hiddenUnplaced[i];
            if (!GodotObject.IsInstanceValid(built))
            {
                _hiddenUnplaced.RemoveAt(i);
                continue;
            }
            if (built.Transform.Origin.IsZeroApprox())
            {
                continue; // still parked, leave it switched off
            }
            built.Visible = true;
            _hiddenUnplaced.RemoveAt(i);
            restored.Add(node.Name);
        }
        return restored;
    }

    /// <summary>Builds the original skydome (the world's <c>horizon</c> subtree) as a separate
    /// node the caller anchors to the camera: a backdrop, not scenery. Zone names are per chapter,
    /// so an absent zone falls back to the horizon's first zone child. Never collidable, never
    /// casts shadows. Dome geometry and colour: docs/formats/weather.md.
    /// ⚠ Do not add a colour-grading stage. The dome's colour is wholly authored, and it matches
    /// the original ungraded.</summary>
    public Node3D? BuildHorizon(string zone = "zone2")
    {
        var horizon = _gamez.FindByName("horizon");
        if (horizon == null)
            return null;
        zone = ResolveHorizonZone(horizon, zone);
        bool SkipOtherZones(GameZNode n) =>
            n.Name.StartsWith("zone", StringComparison.OrdinalIgnoreCase)
            && !n.Name.Equals(zone, StringComparison.OrdinalIgnoreCase);
        // ⚠ Do not force-fog the dome. Every horizon model is authored `fog: false` and the
        // FOG_ALTITUDE fade never reaches the dome's own authored size, so force-fogging paints
        // nothing but flat fog colour. Decode: docs/formats/weather.md.
        var built = _scene.BuildSubtree(horizon, SkipOtherZones, collisionSkip: _ => true);
        if (built == null)
            return null;
        DisableShadows(built);
        BillboardMoon(built);
        DisableLightRangeFade(built);
        return built;
    }

    /// <summary>Builds the mission's danger-zone route ribbons, the 'dzpaths' subtree the
    /// world build skips (see <see cref="SkipWorldNode"/>). This is AI/route guide data the
    /// original never renders (the dzN completion points sit on these polylines); exposed only
    /// for --debug-dzpaths inspection. Never collidable. Null when the world has no dzpaths.</summary>
    public Node3D? BuildDzPaths()
    {
        var dzpaths = _gamez.FindByName("dzpaths");
        return dzpaths == null ? null : BuildDzPathTree(dzpaths);
    }

    // Internal: ClutterBuilder walks the same placed world with the same exclusions.
    internal static bool SkipWorldNode(GameZNode n) =>
        n.Name.Equals("horizon", StringComparison.OrdinalIgnoreCase)
        || n.Name.Equals("dzpaths", StringComparison.OrdinalIgnoreCase)
        || IsFogVolumeNode(n);

    // The deck-tile test: one flat, untilted 4-vertex quad, so a wall or a ramp fails it. Static
    // and gamez-only so CloudDeckAltitudeOf can run it with no built scene; the instance walk goes
    // through FlatTile below rather than a parallel copy. StyleCop's ordering rules, not the
    // accessibility, are what pin it here rather than beside that wrapper.
    internal static bool FlatTileOf(GameZ gamez, GameZNode n, out float altitude, out float x0,
        out float z0, out float x1, out float z1)
    {
        altitude = x0 = z0 = x1 = z1 = 0f;
        if (n.MeshIndex < 0 || n.MeshIndex >= gamez.Meshes.Count)
            return false;
        var mesh = gamez.Meshes[n.MeshIndex];
        if (mesh.Polygons.Count != 1 || mesh.Vertices.Count != 4)
            return false;
        var xf = n.Local ?? Transform3D.Identity;
        var first = xf * mesh.Vertices[0];
        x0 = x1 = first.X;
        z0 = z1 = first.Z;
        for (int i = 1; i < 4; i++)
        {
            var v = xf * mesh.Vertices[i];
            if (Mathf.Abs(v.Y - first.Y) > 0.001f)
                return false; // tilted, a wall or a ramp, not a deck tile
            x0 = Mathf.Min(x0, v.X);
            x1 = Mathf.Max(x1, v.X);
            z0 = Mathf.Min(z0, v.Z);
            z1 = Mathf.Max(z1, v.Z);
        }
        altitude = first.Y;
        return true;
    }

    /// <summary>A placed ambient cloud cluster, the gamez node the original names
    /// <c>cloudparent</c>, whose children are the individual cloud facades. This is the OTHER
    /// ambient cloud population, world-placed rather than <c>fvol</c>-scattered, and the two are
    /// gated together by camera altitude (<see cref="CloudClusters"/>).</summary>
    internal static bool IsCloudClusterName(string name) =>
        name.StartsWith("cloudparent", StringComparison.OrdinalIgnoreCase);

    /// <summary>A <c>fvol1</c>…<c>fvol34</c> fog-volume node: an invisible box the world walk
    /// skips (above) and <see cref="FogVolumeSpec.VolumesOf"/> measures. One predicate for both,
    /// so the set that is excluded from the render and the set that is filled with cloud clutter
    /// can never drift apart.</summary>
    internal static bool IsFogVolumeNode(GameZNode n) =>
        n.Name.StartsWith("fvol", StringComparison.OrdinalIgnoreCase);

    // A cloud- or sky-textured surface, classified by texture because node names are unreliable
    // here (cloud layers turn up under generic names like 'g27517'). Shared by three callers: the
    // collision exemption below, the cloud alpha-blend passed to SceneBuilder, and
    // MapEdgeExtender's ground-tile classifier.
    internal static bool IsCloudOrSkyTexture(string tex) =>
        tex.StartsWith("cloud", StringComparison.OrdinalIgnoreCase)
        || tex.StartsWith("sky", StringComparison.OrdinalIgnoreCase);

    // Light-source flare, fire and flame sprites: single flat quads the original renders
    // camera-billboarded, never solid and never dimmed. World-fixed they show edge-on or skewed.
    // The three substrings were surveyed install-wide, so widening one risks catching real
    // scenery; classified by texture like the clouds, since the names are inconsistent.
    internal static bool IsFlareTexture(string tex) =>
        tex.Contains("flare", StringComparison.OrdinalIgnoreCase)
        || tex.Contains("fire", StringComparison.OrdinalIgnoreCase)
        || tex.Contains("flame", StringComparison.OrdinalIgnoreCase);

    // The cloud SPRITES only: cloud1/cloud2 are soft vertical cards that face the camera.
    // ⚠ Keep this disjoint from whatever FindCloudDeck classifies as deck, or the deck tiles
    // would spin to face the camera instead of staying a flat horizontal sheet.
    private static bool IsCloudSpriteTexture(string tex) =>
        tex.StartsWith("cloud", StringComparison.OrdinalIgnoreCase)
        && !tex.StartsWith("cloudlayer", StringComparison.OrdinalIgnoreCase);

    // ⚠ Keep `skywal*` out of the collision exemption; it is a building wall texture, not sky.
    // MeshUsesTexture matches if any polygon carries it, so one such face makes a whole structure
    // phantom and the player flies through it. Narrowed here rather than in IsCloudOrSkyTexture
    // because that predicate also drives the alpha-blend and the ground-tile filter, where the
    // `sky*` prefix is right. C4's Sky1.tif deck still matches and stays exempt.
    private static bool IsNonSolidSkyTexture(string tex) =>
        IsCloudOrSkyTexture(tex)
        && !tex.StartsWith("skywal", StringComparison.OrdinalIgnoreCase);

    // Union of the subtree's mesh AABBs in `xf`'s frame, null when it draws nothing. Computed from
    // the built meshes so it does not depend on the world being in the scene tree yet.
    private static Aabb? SubtreeAabb(Node3D node, Transform3D xf)
    {
        Aabb? total = null;
        if (node is MeshInstance3D { Mesh: not null } mi)
        {
            total = xf * mi.GetAabb();
        }
        foreach (var child in node.GetChildren())
        {
            if (child is not Node3D c3d)
            {
                continue;
            }
            if (SubtreeAabb(c3d, xf * c3d.Transform) is { } sub)
            {
                total = total?.Merge(sub) ?? sub;
            }
        }
        return total;
    }

    // True when this walk root is an ENTITY parked at the world origin rather than map geometry:
    // it carries no transform of its own AND its geometry wraps around that origin. Both halves
    // are needed, and the second is what does the work: roughly 300 roots per chapter carry no
    // transform and nearly all of them are terrain (docs/formats/interp.md).
    // ⚠ Do not run this test on the gamez `child_bbox` field. That box is in the node's local
    // frame, so every placed terrain tile trivially contains its own origin.
    private static bool IsParkedAtOrigin(GameZNode node, Node3D built)
    {
        if (node.Local != null)
        {
            return false; // authored somewhere specific; wherever that is, it is not "unplaced"
        }
        var aabb = SubtreeAabb(built, Transform3D.Identity);
        if (aabb == null)
        {
            return false; // no geometry at all (empty group node), nothing to draw either way
        }
        var box = aabb.Value;
        // Strictly straddling the origin in x and z. Map geometry never does: every chapter's
        // world `area` is x,z in [-N, 0], so the origin is the map's CORNER and real terrain
        // only ever touches it, never surrounds it.
        return box.Position.X < 0f && box.End.X > 0f
            && box.Position.Z < 0f && box.End.Z > 0f;
    }

    // The dome's star point-lights sit ~22 km out (camera-anchored, 2.5× scaled), far past
    // their data visibility range (4000 m), which is meant for in-world beacons. Opt them
    // out of the light shader's distance fade or the night sky goes starless.
    private static void DisableLightRangeFade(Node node)
    {
        if (node is MeshInstance3D mi && mi.Name == "lights")
            mi.SetInstanceShaderParameter("csky_light_fade", 0f);
        foreach (var child in node.GetChildren())
            DisableLightRangeFade(child);
    }

    // Alpha from distance to the background color (sampled at a corner): background → 0,
    // the painted glow halo → partial, the moon disc → 1. Reproduces the original's
    // color-key so the sky shows through right up to the halo, with no hard quad edge.
    private static ImageTexture ColorKeyed(ImageTexture tex)
    {
        var img = tex.GetImage();
        img.ClearMipmaps();
        img.Convert(Image.Format.Rgba8);
        var bg = img.GetPixel(0, 0);
        const float ramp = 0.25f; // channels this far from the background are fully opaque
        for (int y = 0; y < img.GetHeight(); y++)
            for (int x = 0; x < img.GetWidth(); x++)
            {
                var c = img.GetPixel(x, y);
                float d = Mathf.Max(Mathf.Abs(c.R - bg.R),
                    Mathf.Max(Mathf.Abs(c.G - bg.G), Mathf.Abs(c.B - bg.B)));
                c.A = Mathf.Clamp(d / ramp, 0f, 1f);
                img.SetPixel(x, y, c);
            }
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    private static Node3D? FindChildByName(Node3D root, string name)
    {
        if (root.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
            return root;
        foreach (var child in root.GetChildren())
            if (child is Node3D n3d && FindChildByName(n3d, name) is { } found)
                return found;
        return null;
    }

    // The backdrop populations: the dome, the overcast deck and the ambient cloud sprites each
    // span the whole sky or the whole map, so any of them casting puts the world in shade.
    private static void DisableShadows(Node node)
    {
        if (node is MeshInstance3D mi)
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        foreach (var child in node.GetChildren())
            DisableShadows(child);
    }

    // Meshed nodes in a subtree, the node itself included: what BuildHorizon has to draw for a
    // zone. Counts MeshIndex >= 0 rather than polygons, because that is the same test SceneBuilder
    // uses to decide a node carries a model at all; the distinction this serves is 0 vs 3-6.
    private static int MeshedNodesIn(GameZ gamez, GameZNode node)
    {
        int meshed = node.MeshIndex >= 0 ? 1 : 0;
        foreach (var childIndex in node.Children)
            if (childIndex >= 0 && childIndex < gamez.Nodes.Count)
                meshed += MeshedNodesIn(gamez, gamez.Nodes[childIndex]);
        return meshed;
    }

    // Merges every MeshInstance3D under `root`, at any depth, into one LOCAL-space box, composing
    // Transform rather than GlobalTransform because nothing built here is in the scene tree yet.
    // ⚠ Walk to any depth; a single level of GetChildren() comes back a zero box at the origin,
    // because BuildSubtree can wrap a leaf's mesh in its own transform node.
    private static Aabb MergedLocalAabb(Node3D root)
    {
        Aabb merged = default;
        bool first = true;
        void Walk(Node3D node, Transform3D parentXf)
        {
            var xf = parentXf * node.Transform;
            if (node is MeshInstance3D { Mesh: { } mesh })
            {
                var box = xf * mesh.GetAabb();
                merged = first ? box : merged.Merge(box);
                first = false;
            }
            foreach (var child in node.GetChildren())
                if (child is Node3D n3)
                    Walk(n3, xf);
        }
        Walk(root, Transform3D.Identity);
        return merged;
    }

    // Ranks this world's nodes by its conflict graph for the scene builder's `node_bias`. Runs
    // before the build because the rank is an input to it, like the polygon priority.
    // ⚠ Keep this walk's filters in step with Add and SceneBuilder.BuildSubtree, or the graph
    // covers geometry that is never built. The origin-parked pile stays out: it is switched off a
    // moment later, and alone it stretches the longest conflict chain past any usable step size.
    private void RankConflicts(List<int> roots)
    {
        var start = Time.GetTicksMsec();
        var nodes = new List<(GameZNode Node, Transform3D World)>();
        var subtree = new List<(GameZNode Node, Transform3D World)>();
        int excluded = 0;
        foreach (int idx in roots)
        {
            if (idx < 0 || idx >= _gamez.Nodes.Count)
                continue;
            var rootNode = _gamez.Nodes[idx];
            if (SkipWorldNode(rootNode))
                continue;
            subtree.Clear();
            Collect(rootNode, Transform3D.Identity, subtree);
            if (rootNode.Local == null && WrapsOrigin(subtree))
            {
                excluded++;
                continue;
            }
            nodes.AddRange(subtree);
        }

        var report = ConflictRank.Compute(_gamez, nodes, excluded);
        _scene.ConflictRanks = report.Ranks;
        Log.Info("world", $"draw order: {report.Pairs} conflicting node pair(s) over {nodes.Count} node(s) / {report.Triangles} triangle(s) -> {report.MaxRank + 1} rank(s), {excluded} origin-parked root(s) excluded, {Time.GetTicksMsec() - start} ms");
        if (report.MaxRank > SceneBuilder.ConflictRankCap)
        {
            GD.PushWarning($"draw order: conflict chain {report.MaxRank + 1} exceeds the "
                           + $"{SceneBuilder.ConflictRankCap + 1}-rank budget; the deepest layers "
                           + "share a bias and can z-fight");
        }
    }

    private void Collect(GameZNode node, Transform3D parent, List<(GameZNode, Transform3D)> into)
    {
        if (SkipWorldNode(node) || (node.Kind == "Lod" && node.LodRangeMin != 0f))
            return;
        var world = node.Local is { } local ? parent * local : parent;
        into.Add((node, world));
        foreach (int child in node.Children)
        {
            if (child >= 0 && child < _gamez.Nodes.Count)
                Collect(_gamez.Nodes[child], world, into);
        }
    }

    // The geometry half of IsParkedAtOrigin, from the gamez meshes instead of the built tree,
    // the same union of the same vertices, taken before there is a tree to walk.
    private bool WrapsOrigin(List<(GameZNode Node, Transform3D World)> subtree)
    {
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        bool any = false;
        foreach (var (node, world) in subtree)
        {
            if (node.MeshIndex < 0 || node.MeshIndex >= _gamez.Meshes.Count)
                continue;
            foreach (var v in _gamez.Meshes[node.MeshIndex].Vertices)
            {
                var p = world * v;
                min = min.Min(p);
                max = max.Max(p);
                any = true;
            }
        }
        return any && min.X < 0f && max.X > 0f && min.Z < 0f && max.Z > 0f;
    }

    private Node3D BuildDzPathTree(GameZNode node)
    {
        var built = new Node3D { Name = node.Name };
        if (node.Local is { } local)
            built.Transform = local;
        if (node.MeshIndex >= 0 && node.MeshIndex < _gamez.Meshes.Count)
        {
            var mesh = _gamez.Meshes[node.MeshIndex];
            var materialCounts = new Dictionary<int, int>();
            foreach (var poly in mesh.Polygons)
                materialCounts[poly.MaterialIndex] = materialCounts.TryGetValue(poly.MaterialIndex, out int count)
                    ? count + 1 : 1;
            int gateNumber = 0;
            foreach (var poly in mesh.Polygons)
            {
                bool gate = materialCounts[poly.MaterialIndex] == 2;
                Color color = gate
                    ? ++gateNumber == 1 ? new Color(0f, 1f, 0f, 0.5f) : new Color(1f, 0f, 0f, 0.5f)
                    : new Color(1f, 1f, 1f, 0.35f);
                built.AddChild(new MeshInstance3D
                {
                    Name = gate ? gateNumber == 1 ? "gate_green" : "gate_red" : "route",
                    Mesh = DebugPolygonMesh(mesh, poly, color, line: !gate),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
            }
        }
        foreach (int childIndex in node.Children)
        {
            if (childIndex >= 0 && childIndex < _gamez.Nodes.Count)
                built.AddChild(BuildDzPathTree(_gamez.Nodes[childIndex]));
        }
        return built;
    }

    private ArrayMesh DebugPolygonMesh(GameZMesh mesh, GameZPolygon poly, Color color, bool line)
    {
        var surface = new SurfaceTool();
        surface.Begin(line ? Mesh.PrimitiveType.LineStrip : Mesh.PrimitiveType.Triangles);
        if (line)
        {
            foreach (int index in poly.VertexIndices)
                surface.AddVertex(mesh.Vertices[index]);
        }
        else
        {
            void AddTriangle(int a, int b, int c)
            {
                surface.AddVertex(mesh.Vertices[poly.VertexIndices[a]]);
                surface.AddVertex(mesh.Vertices[poly.VertexIndices[b]]);
                surface.AddVertex(mesh.Vertices[poly.VertexIndices[c]]);
            }
            if (poly.TriangleStrip)
            {
                for (int i = 0; i + 2 < poly.VertexIndices.Count; i++)
                    if ((i & 1) == 0) AddTriangle(i, i + 1, i + 2); else AddTriangle(i, i + 2, i + 1);
            }
            else
            {
                for (int i = 1; i + 1 < poly.VertexIndices.Count; i++)
                    AddTriangle(0, i, i + 1);
            }
        }
        surface.SetMaterial(new StandardMaterial3D
        {
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        });
        var result = new ArrayMesh();
        surface.Commit(result);
        return result;
    }

    // Rendered but not solid, subtree-inherited: sky/cloud surfaces, billboards, and any node the
    // gamez flags `intersect_surface` false. That flag is the original's own collision-participation
    // record (docs/formats/gamez.md); honouring it is what lets a plane fly through debris and the
    // C3 spiderweb. Terrain, water, buildings, zeppelins and trains stay solid.
    private bool NoCollisionNode(GameZNode n) =>
        !n.IntersectSurface || MeshUsesTexture(n, IsNonSolidSkyTexture) || IsBillboardNode(n);

    // A billboard is a flat card the engine turns toward the camera, so its collider would be a
    // phantom wall wherever the card happens to be facing. Asked of the gamez model itself rather
    // than a poly-count and texture-name heuristic, which leaves tree cards and multi-poly facades
    // solid. The classifier is per MESH and this predicate per NODE, hence the MeshIndex hop.
    private bool IsBillboardNode(GameZNode n)
    {
        if (n.MeshIndex < 0 || n.MeshIndex >= _gamez.Meshes.Count)
            return false;
        var mesh = _gamez.Meshes[n.MeshIndex];
        return SceneBuilder.ClassifyBillboard(mesh) is { } kind
            ? kind != SceneBuilder.BillboardKind.None
            // Legacy v0.6.1 extraction: no ModelType, so fall back to the flare-sprite rule
            // rather than guessing.
            : mesh.Polygons.Count == 1 && MeshUsesTexture(n, IsFlareTexture);
    }

    // True when the node's model is one flat horizontal quad, reporting its altitude and its
    // world-space x/z footprint, the instance-side wrapper FindCloudDeck (below) walks with.
    private bool FlatTile(GameZNode n, out float altitude, out float x0, out float z0,
        out float x1, out float z1) => FlatTileOf(_gamez, n, out altitude, out x0, out z0, out x1, out z1);

    // Walks the built world for cloudparent subtrees. Stops descending at each hit: the whole
    // subtree is the cluster, and cloud clusters do not nest. Matched on the name the DATA
    // carries (AnimRuntime.NameMeta), never on Node.Name, see the CloudClusters remarks.
    private void CollectCloudClusters(Node node)
    {
        if (node is Node3D n3d && n3d.HasMeta(AnimRuntime.NameMeta)
            && IsCloudClusterName(n3d.GetMeta(AnimRuntime.NameMeta).AsString()))
        {
            _cloudClusters.Add(n3d);
            return;
        }
        foreach (var child in node.GetChildren())
        {
            CollectCloudClusters(child);
        }
    }

    // Picks the deck out of the world's flat-quad roots: bucket them by altitude in 1 m buckets,
    // and take the bucket whose footprint covers at least DeckCoverageFraction of the map. Tiles
    // are clipped to the map rect and their areas summed rather than unioned, which is safe
    // because the decks are non-overlapping grids and the margin over every other bucket is 10x.
    private void FindCloudDeck(GameZNode world, List<int> roots)
    {
        _deckNodes.Clear();
        CloudDeckZoneId = -1;
        if (!world.HasArea)
            return;
        float mapW = Mathf.Abs(world.AreaRight - world.AreaLeft);
        float mapH = Mathf.Abs(world.AreaBottom - world.AreaTop);
        if (mapW <= 0f || mapH <= 0f)
            return;
        float mapLeft = Mathf.Min(world.AreaLeft, world.AreaRight);
        float mapRight = Mathf.Max(world.AreaLeft, world.AreaRight);
        float mapTop = Mathf.Min(world.AreaTop, world.AreaBottom);
        float mapBottom = Mathf.Max(world.AreaTop, world.AreaBottom);

        var buckets = new Dictionary<int, (float Area, List<int> Nodes)>();
        var seen = new HashSet<int>();
        foreach (var idx in roots)
        {
            if (idx < 0 || idx >= _gamez.Nodes.Count || !seen.Add(idx))
                continue;
            var n = _gamez.Nodes[idx];
            if (SkipWorldNode(n) || !FlatTile(n, out float y, out float x0, out float z0,
                    out float x1, out float z1))
                continue;
            float w = Mathf.Min(x1, mapRight) - Mathf.Max(x0, mapLeft);
            float h = Mathf.Min(z1, mapBottom) - Mathf.Max(z0, mapTop);
            if (w <= 0f || h <= 0f)
                continue; // wholly outside the map rect
            int key = Mathf.RoundToInt(y);
            var slot = buckets.TryGetValue(key, out var got) ? got : (0f, new List<int>());
            slot.Item1 += w * h;
            slot.Item2.Add(idx);
            buckets[key] = slot;
        }

        float best = DeckCoverageFraction * mapW * mapH;
        foreach (var (alt, slot) in buckets)
            if (slot.Area > best)
            {
                best = slot.Area;
                _deckNodes.Clear();
                foreach (var idx in slot.Nodes)
                    _deckNodes.Add(idx);
                _deckAltitude = alt;
                _deckCoverage = slot.Area / (mapW * mapH);
            }

        // The tiles' own zone_id, for the per-rig gate. Mixed tiles (never observed, 144/144 on
        // zone_id 2 in all four deck chapters) read as −1, i.e. ungated, which is the only reading
        // that cannot hide authored content it does not understand.
        int? deckZone = null;
        foreach (var idx in _deckNodes)
        {
            int zone = _gamez.Nodes[idx].ZoneId;
            if (deckZone is { } got && got != zone)
            {
                deckZone = -1;
                break;
            }
            deckZone = zone;
        }
        CloudDeckZoneId = deckZone ?? -1;
    }

    // True if any of the node's own-mesh polygons is skinned with a texture matching the predicate.
    private bool MeshUsesTexture(GameZNode n, Func<string, bool> match)
    {
        if (n.MeshIndex < 0 || n.MeshIndex >= _gamez.Meshes.Count)
            return false;
        foreach (var poly in _gamez.Meshes[n.MeshIndex].Polygons)
        {
            if (poly.MaterialIndex < 0 || poly.MaterialIndex >= _gamez.Materials.Count)
                continue;
            var tex = _gamez.Materials[poly.MaterialIndex].TextureName;
            if (tex != null && match(tex))
                return true;
        }
        return false;
    }

    private void Add(Node3D root, Node3D deck, int nodeIndex)
    {
        if (nodeIndex < 0 || nodeIndex >= _gamez.Nodes.Count)
            return;
        var node = _gamez.Nodes[nodeIndex];
        bool isDeck = _deckNodes.Contains(nodeIndex);
        // ⚠ Deck tiles are the one exception to the world's backface culling: authored
        // single-sided, but flown through, so the quad reads from both faces (weather.md has the
        // other two). applyActive starts EVERY node of the subtree at its own record's ACTIVE bit.
        var built = _scene.BuildSubtree(node, SkipWorldNode, NoCollisionNode,
            forceDoubleSided: isDeck, forceLit: isDeck, zoneGate: !isDeck, applyActive: true);
        if (built != null)
        {
            if (IsParkedAtOrigin(node, built))
            {
                _parkedAtOrigin.Add((node, built));
            }
            if (isDeck)
            {
                RecordDeckUndimmedMesh(node);
            }
            (isDeck ? deck : root).AddChild(built);
        }
    }

    // The tile's own mesh in both lit variants, from the SceneBuilder cache the built node just
    // used, so the dimmed side is the very resource the MeshInstance3D carries, and the RID is a
    // key WeatherRig can look a live instance up by. A deck tile is one flat 4-vertex quad
    // (FlatTile), so its own MeshIndex is the whole tile; a tile that somehow carried child
    // geometry would simply not be swappable, which WeatherRig's own census reports rather than
    // hides.
    private void RecordDeckUndimmedMesh(GameZNode node)
    {
        if (_scene.SharedMesh(node.MeshIndex, forceDoubleSided: true, forceLit: true) is { } dimmed
            && _scene.SharedMesh(node.MeshIndex, forceDoubleSided: true, forceLit: false) is { } undimmed)
        {
            _deckUndimmedMeshes[dimmed.GetRid()] = undimmed;
        }
    }

    // Extends the deck sheet with a flat untextured rim so its edge does not read as a hard step
    // near the horizon. `TargetHalfSpan` is a picked constant inside the smallest flown dome (C1's
    // zone2, 21.86 km); ⚠ do not re-derive it from a camera-anchored ceiling, that mechanism is gone.
    // ⚠ Build it as a picture frame, never a full plane, which would z-fight the tiles.
    // ⚠ Add it as a CHILD of `deck`, never a sibling, nesting alone makes the rim follow the
    // camera and flip regime with the sheet.
    private void AddDeckAnnulus(Node3D deck, Aabb tilesAabb)
    {
        const float TargetHalfSpan = 20480f;
        Vector3 c = tilesAabb.GetCenter();
        float innerHalfX = tilesAabb.Size.X / 2f, innerHalfZ = tilesAabb.Size.Z / 2f;
        float halfX = Mathf.Max(TargetHalfSpan, innerHalfX);
        float halfZ = Mathf.Max(TargetHalfSpan, innerHalfZ);
        float innerMinX = c.X - innerHalfX, innerMaxX = c.X + innerHalfX;
        float innerMinZ = c.Z - innerHalfZ, innerMaxZ = c.Z + innerHalfZ;
        float outerMinX = c.X - halfX, outerMaxX = c.X + halfX;
        float outerMinZ = c.Z - halfZ, outerMaxZ = c.Z + halfZ;
        float y = c.Y;

        Vector3 P(float x, float z) => new(x, y, z);
        // North/south strips run the full outer width (so they cover the four corners too);
        // east/west fill only the remaining middle strip, a standard picture-frame tiling with
        // no overlap and no gap, regardless of how much bigger the target is than the sheet.
        var quads = new List<(Vector3, Vector3, Vector3, Vector3)>
        {
            (P(outerMinX, outerMinZ), P(outerMaxX, outerMinZ), P(outerMaxX, innerMinZ), P(outerMinX, innerMinZ)),
            (P(outerMinX, innerMaxZ), P(outerMaxX, innerMaxZ), P(outerMaxX, outerMaxZ), P(outerMinX, outerMaxZ)),
            (P(innerMaxX, innerMinZ), P(outerMaxX, innerMinZ), P(outerMaxX, innerMaxZ), P(innerMaxX, innerMaxZ)),
            (P(outerMinX, innerMinZ), P(innerMinX, innerMinZ), P(innerMinX, innerMaxZ), P(outerMinX, innerMaxZ)),
        };
        var mi = new MeshInstance3D { Mesh = _scene.BuildFlatQuadMesh(quads), Name = "deck_annulus" };
        mi.SetMeta(DeckExtensionMeta, true);
        deck.AddChild(mi);
    }

    private string ResolveHorizonZone(GameZNode horizon, string zone)
    {
        var zones = HorizonZones();
        if (zones.Count == 0)
            return zone;
        foreach (var z in zones)
            if (z.Name.Equals(zone, StringComparison.OrdinalIgnoreCase))
                return zone;
        if (!_loggedHorizonZoneFallback)
        {
            _loggedHorizonZoneFallback = true;
            var names = new List<string>();
            foreach (var z in zones)
                names.Add(z.Name);
            Log.Info("world", $"horizon: no '{zone}' subtree (has {string.Join("/", names)}), building '{zones[0].Name}'");
        }
        return zones[0].Name;
    }

    // The source moon is an axis-aligned quad, which looks tilted from most headings, but the
    // original shows a round upright moon from any direction, so it must billboard. Replaced
    // with a camera-facing quad of the same position and size. The original also color-keys the
    // uniform background away, since crater detail rules out additive and the sky shows through
    // to the halo; ColorKeyed reproduces that as an alpha ramp on distance from that colour.
    private void BillboardMoon(Node3D built)
    {
        var moonNode = FindChildByName(built, "moon");
        var gzMoon = _gamez.FindByName("moon");
        if (moonNode == null || gzMoon == null
            || gzMoon.MeshIndex < 0 || gzMoon.MeshIndex >= _gamez.Meshes.Count)
            return;
        var verts = _gamez.Meshes[gzMoon.MeshIndex].Vertices;
        if (verts.Count == 0)
            return;
        Vector3 min = verts[0], max = verts[0];
        foreach (var v in verts)
        {
            min = min.Min(v);
            max = max.Max(v);
        }
        var size = max - min;
        float side = Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));

        foreach (var child in moonNode.GetChildren())
            if (child is MeshInstance3D)
                child.QueueFree();

        var tex = _textures.Find("moon1.tif");
        if (tex == null)
            return;
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            BillboardKeepScale = true, // billboards ignore inherited scale (the 2.5× dome anchor) without this
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoTexture = ColorKeyed(tex),
        };
        moonNode.AddChild(new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(side, side), Material = mat },
            Position = (min + max) * 0.5f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Name = "mesh",
        });
    }
}
