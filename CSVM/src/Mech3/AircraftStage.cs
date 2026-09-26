using System;
using System.Collections.Generic;
using CSVM.Utils;
using Godot;

namespace CSVM.Mech3;

/// <summary>
/// The aircraft-archive subtrees a story-mission intro or a mid-mission cutscene animates, staged
/// into a chapter world so the animation runtime's node table can reach them: <c>piratefighter</c>
/// as a prop with no pilot, a bodiless <c>player</c> the flown aircraft follows, <c>chuteman</c>'s
/// parachutist subtree, and <c>balmoral</c>, a drop's own actor. All come from the shared aircraft
/// archive rather than the chapter gamez, so their compiled pointers are rebased onto the
/// chapter's own pointer space (<see cref="PointerBaseOf"/>). A subtree carrying aircraft skins is
/// built on a builder of its own, so <see cref="Paint"/> can dress it in the livery of the
/// aeroplane it stands in for (<see cref="StandIns"/>).
/// Decode: docs/formats/anim-definitions/cutscenes.md.
/// </summary>
public sealed class AircraftStage
{
    /// <summary>The aircraft-archive node an intro poses as the player's own aeroplane. A
    /// parentless wrapper in the archive, so CSVM stands a bodiless marker in for it and the flown
    /// airframe takes its pose; what the wrapper holds there is an artefact of the archive build,
    /// not a statement about which aeroplane the intro is about.</summary>
    public const string PlayerNode = "player";

    /// <summary>The aircraft-archive node an intro stages under <c>piratezep</c> and flies with
    /// <c>gi_pfighter1</c>/<c>gi_pfighter2</c>, the Devastator's remote model, carrying no
    /// pilot and no flight model.</summary>
    public const string PropNode = "piratefighter";

    /// <summary>The aircraft-archive node a mid-mission drop/hookup cutscene reparents under its
    /// own aiming node and activates: a parentless wrapper whose <c>chutemanparent</c> child carries
    /// the visible <c>pilot</c> mesh and the parachute's <c>stamp</c>. Ships <c>INACTIVE</c>
    /// (the shared <c>chuteman</c> def's own <c>RESET_STATE</c>), the same base state
    /// <see cref="PropNode"/> ships in.</summary>
    public const string ChuteNode = "chuteman";

    /// <summary>The aircraft-archive node C2/M05's capture drop is rooted on: a parentless wrapper
    /// its own definition reparents onto <c>cargozep2</c> and moves for the shot, then returns to
    /// the world root. Ships ACTIVE, but staged under a switched-off holder like
    /// <see cref="FigureNodes"/>: no OTHER mission's definition ever names it.</summary>
    public const string BalmoralNode = "balmoral";

    /// <summary>The block a chapter's pointer base is rounded up to. Measured over all eight
    /// chapters; no site in the executable computing it has been traced, so a ninth chapter's base
    /// is a prediction rather than a reading.</summary>
    public const int PointerBlock = 2500;

    /// <summary>The aircraft-archive nodes a wing-walk capture adds under its own composition
    /// frame: the rope ladder (<c>ladder_roll</c> → <c>rung1</c>–<c>rung6</c>, shipped
    /// <c>INACTIVE</c>, switched on by <c>ww_ladder</c>) and the wing-walking pilot
    /// (<c>cpilot_parent</c> → <c>cpilot_drop</c> → the <c>cp_*</c> limbs). Both are parentless
    /// wrappers the chapter's own walk never reaches.</summary>
    public static readonly string[] FigureNodes = { "rope_ladder", "pickup_cpilot" };

    /// <summary>The aircraft-archive props a hangar hand-over switches on itself: the Bloodhawk on
    /// the hangar floor while the pilot parachutes in (<c>anim_bloodhawk</c>, under <c>world1</c>)
    /// and the undercarriage the flown aeroplane wears on the lift (<c>bloodhawk_gear</c>, under
    /// <c>player</c>). Both ship <c>INACTIVE</c> and parentless, built the way
    /// <see cref="ChuteNode"/> is: the drop's own legs add, activate and detach them. Neither is
    /// painted, by decision: they carry no logo placeholder and neither reads on screen.</summary>
    public static readonly string[] PropNodes = { "anim_bloodhawk", "bloodhawk_gear" };

    /// <summary>Which aeroplane each staged prop wears the paint of. A node named here still keeps
    /// the shipped skins in a mission that flies neither source: nothing invents a livery for a
    /// prop, it only tracks the aeroplane it stands in for.</summary>
    public static readonly IReadOnlyList<StandIn> StandIns = new StandIn[]
    {
        new(BalmoralNode, "balmoral_1", null),
        new(PropNode, null, "devastator"),
    };

    private readonly Dictionary<string, Node3D> _figures =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Node3D> _props =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, StagedPaint> _paints =
        new(StringComparer.OrdinalIgnoreCase);

    private TextureArchive _textures = null!;

    private AircraftStage() { }

    /// <summary>The bodiless <c>player</c> marker, or null when the archive carries no such node.
    /// The flown aircraft is posed onto it while the cutscene holds the player out of flight.
    /// </summary>
    public Node3D? PlayerMarker { get; private set; }

    /// <summary>The staged <c>piratefighter</c> subtree, drawn from the build in the archive's own
    /// shipped state: no definition switches it on, its SI scripts only pose it. Null when the
    /// archive carries no such node.</summary>
    public Node3D? Prop { get; private set; }

    /// <summary>The staged <c>chuteman</c> subtree, built switched off: a mid-mission drop's own
    /// called animation (e.g. C3/M01's <c>tdchute</c>) is what reparents and activates it. Null
    /// when the archive carries no such node.</summary>
    public Node3D? Chuteman { get; private set; }

    /// <summary>The staged <see cref="BalmoralNode"/> subtree, own <c>Visible</c> matching its
    /// archive shipped state but held under a switched-off parent, the way
    /// <see cref="FigureNodes"/> are: C2/M05's capture drop is rooted on it and its own
    /// <c>OBJECT_ADD_CHILD</c> is what reparents it into view. Null when the archive carries no
    /// such node.</summary>
    public Node3D? Balmoral { get; private set; }

    /// <summary>The staged <see cref="FigureNodes"/> subtrees, by gamez name. They hang under a
    /// holder that is switched off, which is what a library root the chapter's walk never reaches
    /// amounts to: resolvable and indexed, drawn only once a capture's own
    /// <c>OBJECT_ADD_CHILD</c> moves one into the shot.</summary>
    public IReadOnlyDictionary<string, Node3D> Figures => _figures;

    /// <summary>The staged <see cref="PropNodes"/> subtrees, by gamez name, each built switched
    /// off: the definition that names one activates it.</summary>
    public IReadOnlyDictionary<string, Node3D> Props => _props;

    /// <summary>Mesh instances the prop build added, for the session's build summary.</summary>
    public int MeshInstances { get; private set; }

    /// <summary>Where this chapter's cross-archive block starts (<see cref="PointerBaseOf"/>), so a
    /// caller staging another aircraft-archive subtree rebases it the same way this build did.
    /// </summary>
    public int PointerBase { get; private set; }

    /// <summary>The airframe subtree <see cref="StageFlown"/> last indexed, or null before the
    /// flight rigs exist.</summary>
    public Node3D? Flown { get; private set; }

    /// <summary>The staged subtrees <see cref="Paint"/> can reach, by gamez name: the ones whose
    /// own materials name a decal placeholder, so a livery has skins to composite onto. A
    /// parachutist or a rope ladder carries none and is absent here.</summary>
    public IReadOnlyCollection<string> PaintableNodes => _paints.Keys;

    /// <summary>Where a chapter's own node table ends and the shared aircraft archive's begins: the
    /// chapter's node count rounded up to the next multiple of <see cref="PointerBlock"/>. Holds for
    /// all eight extracted chapters.</summary>
    public static int PointerBaseOf(int chapterNodeCount) =>
        ((chapterNodeCount / PointerBlock) + 1) * PointerBlock;

    /// <summary>Builds both aircraft under <paramref name="worldRoot"/> and rebases their compiled
    /// pointers onto <paramref name="chapterNodeCount"/>'s archive block, so the runtime's symbol
    /// table binds the names an intro definition addresses. Runs before the animation bind, beside
    /// the cutscene roots.</summary>
    public static AircraftStage Build(Node3D worldRoot, int chapterNodeCount, GameZ planesGamez,
        TextureArchive textures)
    {
        var stage = new AircraftStage();
        stage._textures = textures;
        int pointerBase = PointerBaseOf(chapterNodeCount);
        stage.PointerBase = pointerBase;
        if (planesGamez.FindByName(PlayerNode) is { } player)
        {
            var marker = new Node3D { Name = PlayerNode };
            marker.SetMeta(AnimRuntime.NameMeta, player.Name);
            marker.SetMeta(AnimRuntime.IndexMeta, player.Index + pointerBase);
            worldRoot.AddChild(marker);
            stage.PlayerMarker = marker;
        }

        // The builder over the aircraft archive: the world's own SceneBuilder reads the chapter
        // gamez's meshes and materials, which none of these subtrees' model indices address. This
        // one serves every subtree that cannot be painted; a paintable one gets its own below.
        var scene = new SceneBuilder(planesGamez, textures, cullBackfaces: true);

        // ⚠ A paintable subtree needs a builder of its own, never a substitution hook on the
        // shared one: the material cache is per builder, so one hook would hand a later subtree a
        // material painted for the first aeroplane's livery.
        SceneBuilder BuilderFor(GameZNode node)
        {
            if (PlanePainter.PrefixFor(planesGamez, node) is not { } prefix)
            {
                return scene;
            }

            var paint = new StagedPaint { Prefix = prefix };
            paint.Scene = new SceneBuilder(planesGamez, textures, cullBackfaces: true,
                textureSubstitute: (name, tex) => paint.Painter?.Substitute(name, tex) ?? tex);
            stage._paints[node.Name] = paint;
            return paint.Scene;
        }

        // ⚠ Drawn in the archive's own shipped state (ACTIVE), never forced off: C1/M04's
        // pfighter11..13 fly it on SI scripts and nothing in that mission activates or parents it,
        // so a prop built switched off leaves the wingman out of the launch and the dive.
        if (planesGamez.FindByName(PropNode) is { } prop
            && BuilderFor(prop).BuildSubtree(prop, collisionSkip: _ => true) is { } builtProp)
        {
            builtProp.Transform = Transform3D.Identity;
            Rebase(builtProp, pointerBase);
            AnimRuntime.SetSubtreeActive(builtProp, prop.Active);
            worldRoot.AddChild(builtProp);
            stage.Prop = builtProp;
        }

        if (planesGamez.FindByName(ChuteNode) is { } chute
            && BuilderFor(chute).BuildSubtree(chute, collisionSkip: _ => true) is { } builtChute)
        {
            builtChute.Transform = Transform3D.Identity;
            Rebase(builtChute, pointerBase);
            AnimRuntime.SetSubtreeActive(builtChute, false);
            worldRoot.AddChild(builtChute);
            stage.Chuteman = builtChute;
        }

        // ⚠ Never stage this at worldRoot the way PropNode is: no definition activates the node,
        // so it would draw at the archive's build origin in every other mission that stages an
        // aircraft. docs/formats/anim-definitions/cutscenes.md.
        var balmoralHolder = new Node3D { Name = "balmoral_holder", Visible = false };
        worldRoot.AddChild(balmoralHolder);
        if (planesGamez.FindByName(BalmoralNode) is { } balmoral
            && BuilderFor(balmoral).BuildSubtree(balmoral, collisionSkip: _ => true) is { } builtBalmoral)
        {
            builtBalmoral.Transform = Transform3D.Identity;
            builtBalmoral.Visible = balmoral.Active;
            Rebase(builtBalmoral, pointerBase);
            balmoralHolder.AddChild(builtBalmoral);
            stage.Balmoral = builtBalmoral;
        }

        foreach (string propName in PropNodes)
        {
            if (planesGamez.FindByName(propName) is not { } propNode
                || BuilderFor(propNode).BuildSubtree(propNode, collisionSkip: _ => true)
                    is not { } builtPropNode)
            {
                continue;
            }

            builtPropNode.Transform = Transform3D.Identity;
            Rebase(builtPropNode, pointerBase);
            AnimRuntime.SetSubtreeActive(builtPropNode, false);
            worldRoot.AddChild(builtPropNode);
            stage._props[propNode.Name] = builtPropNode;
        }

        // ⚠ Under a switched-off holder, not switched off themselves. The wing-walk pilot is never
        // activated by any definition, it is the reparent into the shot that draws him, which is
        // exactly what the original gets from a library root its world walk never reaches.
        var holder = new Node3D { Name = "figures", Visible = false };
        worldRoot.AddChild(holder);
        foreach (string figure in FigureNodes)
        {
            if (planesGamez.FindByName(figure) is not { } node
                || BuilderFor(node).BuildSubtree(node, collisionSkip: _ => true) is not { } built)
            {
                continue;
            }

            built.Transform = Transform3D.Identity;
            built.Visible = node.Active;
            Rebase(built, pointerBase);
            holder.AddChild(built);
            stage._figures[node.Name] = built;
        }

        stage.MeshInstances = scene.MeshInstanceCount;
        foreach (var paint in stage._paints.Values)
        {
            stage.MeshInstances += paint.Scene.MeshInstanceCount;
        }

        string marked = stage.PlayerMarker != null ? PlayerNode : $"no {PlayerNode}";
        string staged = stage.Prop != null ? PropNode : $"no {PropNode}";
        string chuted = stage.Chuteman != null ? ChuteNode : $"no {ChuteNode}";
        string balmoraled = stage.Balmoral != null ? BalmoralNode : $"no {BalmoralNode}";
        string figures = stage._figures.Count > 0
            ? string.Join(", ", stage._figures.Keys)
            : "no figures";
        string props = stage._props.Count > 0
            ? string.Join(", ", stage._props.Keys)
            : "no props";
        string paintable = stage._paints.Count > 0
            ? string.Join(", ", stage._paints.Keys)
            : "none paintable";
        Log.Info("world", $"aircraft stage: base {pointerBase} over {chapterNodeCount} chapter node(s), {marked}, {staged}, {chuted}, {balmoraled}, {figures}, {props}, {stage.MeshInstances} mesh instances, skins on {paintable}");
        return stage;
    }

    /// <summary>Paints one staged subtree in <paramref name="scheme"/>, so a prop wears the livery
    /// of the aeroplane it stands in for instead of the archive's shipped skins. Returns the
    /// painter, or null when this build staged no such node, the node carries no skins, or the
    /// scheme is null (the shipped skins, which is what an unresolved stand-in leaves).
    /// Runs after the flight rigs, since the scheme is theirs.</summary>
    public PlanePainter? Paint(string nodeName, PaintScheme? scheme, PatternLibrary patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (!_paints.TryGetValue(nodeName, out var paint))
        {
            return null;
        }

        paint.Painter = scheme != null
            ? new PlanePainter(_textures, patterns, scheme, paint.Prefix)
            : null;
        paint.Scene.Repaint();
        return paint.Painter;
    }

    /// <summary>The livery <see cref="Paint"/> composited onto one staged subtree, null while it
    /// still wears the shipped skins. The count of skins it repainted is what says the
    /// substitution reached the built materials.</summary>
    public PlanePainter? PainterOf(string nodeName) =>
        _paints.TryGetValue(nodeName, out var paint) ? paint.Painter : null;

    /// <summary>The skin-texture prefix a staged subtree's own materials name (<c>bal</c>,
    /// <c>dev</c>), or null when it carries no decal placeholder to read one off and so no livery
    /// can reach it.</summary>
    public string? SkinPrefixOf(string nodeName) =>
        _paints.TryGetValue(nodeName, out var paint) ? paint.Prefix : null;

    /// <summary>Puts the flown aircraft's own airframe subtree in the animation runtime's node
    /// table, rebased onto <see cref="PointerBase"/>, which is what makes a hookup definition's
    /// per-airframe branches decidable: each tests one <c>player_&lt;airframe&gt;</c> node's active
    /// bit and then poses that airframe's own hook, wing fold and mount offset. Idempotent per
    /// model; the rows naming a freed airframe go when the next stage retires them, never on their
    /// own. Decode: docs/formats/anim-definitions/cutscenes.md.</summary>
    // ⚠ Exactly ONE airframe is the flown one, so the airframe this replaces comes out of the
    // table before the new one goes in. A swap frees the outgoing aeroplane, which the retirement
    // sweep covers; a co-op episode changing hands replaces a LIVE one. Two aeroplanes under one
    // airframe name leave the symbol table's claim on the seat that is not flying the episode.
    public void StageFlown(AnimRuntime runtime, Node3D? planeModel)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (planeModel == null || ReferenceEquals(planeModel, Flown))
        {
            return;
        }

        int retired = Flown is { } previous && GodotObject.IsInstanceValid(previous)
            ? runtime.UnstageRebased(previous)
            : 0;
        Flown = planeModel;
        runtime.IndexRebasedStage(planeModel, PointerBase);
        // The archive parks the hook GROUP off, not its arms; what parks those is the airframe's
        // own retract RESET_STATE, which the rebased index deliberately does not run.
        int parked = runtime.ParkDockingHook(planeModel);
        Log.Info("world", $"aircraft stage: flown '{AnimRuntime.NameOf(planeModel)}' indexed at base {PointerBase}, {retired} row(s) of the airframe it replaces retired, hook parked by {parked} definition(s)");
    }

    // Every built node's stamped archive index shifted into the chapter's cross-archive block. The
    // two spaces cannot overlap, since the base always clears the chapter's own table.
    private static void Rebase(Node3D node, int pointerBase)
    {
        if (node.HasMeta(AnimRuntime.IndexMeta))
        {
            node.SetMeta(AnimRuntime.IndexMeta, (int)node.GetMeta(AnimRuntime.IndexMeta) + pointerBase);
        }

        foreach (var child in node.GetChildren())
        {
            if (child is Node3D n3d)
            {
                Rebase(n3d, pointerBase);
            }
        }
    }

    /// <summary>Where a staged prop's livery comes from: the mission roster block whose rig it
    /// stands in for, or the vehicle def that names the archive node as its own model when no block
    /// flies it. Both may be null, which is a prop that keeps the shipped skins.</summary>
    /// <param name="Node">The staged archive node, one of this class's own node names.</param>
    /// <param name="RosterBlock">The block whose spawned rig resolved the livery, or null.</param>
    /// <param name="VehicleDef">The def whose authored <c>paint_pattern</c> stands in, or null.</param>
    public readonly record struct StandIn(string Node, string? RosterBlock, string? VehicleDef);

    // One paintable subtree: its own builder, the skin prefix its materials name, and the painter
    // Paint installs. The builder's substitution hook reads the painter field, so installing one
    // and repainting swaps every skin the subtree resolved, exactly as PlaneBuilder.Repaint does.
    private sealed class StagedPaint
    {
        public SceneBuilder Scene = null!;
        public string Prefix = "";
        public PlanePainter? Painter;
    }
}
