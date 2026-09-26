using System;
using System.Collections.Generic;
using CSVM.Utils;
using Godot;

namespace CSVM.Mech3;

/// <summary>
/// Builds a renderable Godot node tree for one aircraft out of a planes.zbd GameZ model.
/// Exterior view only: picks the nearest LOD, skips cockpit/damage/destroyed variants.
///
/// Propellers have several representations under <c>dontmove</c> (see <see cref="PropParts"/>).
/// The default (exterior) build keeps the still <c>staticpropN</c> disc and drops the blur
/// layers; the <c>spinningProps</c> build (free flight) keeps BOTH rather than choosing one, so a
/// <c>Flight.Airframe.PropAnimator</c> can spin the blur discs while the startprops/stopprops
/// choreography cross-fades between them and the static disc at spawn and at engine stop.
/// The <c>nitropropN</c> boost disc is built hidden in a flight build, for the nitro_boost def.
/// </summary>
public sealed class PlaneBuilder
{
    /// <summary>The uniform scale the <c>cockpit1</c> interior is mounted at. TUNE, not decoded:
    /// the subtree is authored in its own units with the pilot's eye at its own origin, so the
    /// FRAMING is scale-invariant and this chooses only how the interior composites against world
    /// geometry. ⚠ The interior and the airframe are not a similarity apart, do not try to derive
    /// this from the model. First-person decode: docs/org/cameraViews.md.</summary>
    public const float InteriorScale = 0.04f;

    /// <summary>The fixed head-pitch offset <c>FUN_0042d980</c> applies about the same axis as
    /// elevation, in both first-person views: −4.70° = −0.08203 rad (bit pattern
    /// <c>0xbda7ff58</c>). Not head-look, a constant tilt baked into the view build. The camera
    /// reads it with <see cref="CockpitCameraOffset"/>, and the interior mount carries the same
    /// tilt so the gunsight stays on the guns. Decode: docs/org/cameraViews.md.</summary>
    public const float HeadPitchOffsetRad = -0.08203f;

    // Non-prop subtrees that make no sense in an exterior view: cockpit interiors are
    // separate (differently-scaled) models; damage/destroyed are alternate states.
    // player_damage_off holds the intact duplicates (pdpNi) of the panels that
    // player_damage_on already provides as pdpN_h, the original engine shows exactly
    // one of the two groups (its vehicle-damage detail toggle); we model "damage on".
    // `cockpit2` is defensive: no shipped tree carries one.
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cockpit1", "cockpit2", "destroyed", "shadow", "player_damage_off",
    };

    private readonly GameZ _gamez;
    private readonly TextureArchive _textures;
    private readonly SceneBuilder _scene;
    private readonly SceneBuilder? _interiorScene;
    private readonly bool _spinningProps;
    private readonly bool _withDamagePanels;
    private readonly bool _withCockpitInterior;
    private readonly bool _withDockingHook;
    private readonly List<Node3D> _wingFlares = new();
    private readonly List<Node3D> _damagePanels = new();
    private readonly List<Node3D> _cockpitDamagePanels = new();
    private PaintScheme? _scheme;
    private PatternLibrary _patterns;
    private PlanePainter? _painter;
    private string? _skinPrefix;
    private StandardMaterial3D? _flareMaterial;

    /// <param name="spinningProps">Spins the blur layers instead of the static disc; implies damage panels.</param>
    /// <param name="damagePanels">Builds exterior pdpN panels hidden, for the --damage lab.</param>
    /// <param name="scheme">Paint livery (<see cref="PlanePainter"/>); null keeps shipped skins, per builder so two players can differ.</param><param name="painter">A painter already built for this airframe and this <paramref name="scheme"/>, so a second aeroplane of the pair wears the skins the first composed instead of composing them again (PERF-22). ⚠ Its scheme and skin prefix must be this build's, nothing here checks them.</param>
    /// <param name="patterns"><see cref="PatternLibrary"/>'s region masks; empty paints nothing.</param>
    /// <param name="cockpitInterior">Builds <see cref="CockpitInterior"/>; a human rig only, so an AI plane never pays for a cockpit nobody sits in.</param>
    /// <param name="dockingHook">Builds the airframe's <c>*_hook</c> group, parked at its archive-authored inactive bit; a human rig only.</param>
    public PlaneBuilder(GameZ gamez, TextureArchive textures, bool spinningProps = false,
        bool damagePanels = false, PaintScheme? scheme = null, PatternLibrary? patterns = null,
        bool cockpitInterior = false, bool dockingHook = false, PlanePainter? painter = null)
    {
        _gamez = gamez;
        _textures = textures;
        _scheme = scheme;
        _painter = scheme != null ? painter : null;
        _patterns = patterns ?? PatternLibrary.Empty;
        _scene = new SceneBuilder(gamez, textures, blendTexture: IsPropBlurTexture, cullBackfaces: true,
            textureSubstitute: (name, tex) => _painter?.Substitute(name, tex) ?? tex, sunVertexLit: true);
        // ⚠ A builder of its own, never a field toggled on the airframe's: DepthBiasScale is baked
        // into cached meshes and materials, so one builder switching it mid-build would hand a
        // later caller a mesh biased for the wrong scale.
        if (cockpitInterior)
        {
            _interiorScene = new SceneBuilder(gamez, textures, blendTexture: IsInteriorBlendTexture,
                cullBackfaces: true,
                textureSubstitute: (name, tex) => _painter?.Substitute(name, tex) ?? tex, sunVertexLit: true)
            {
                DepthBiasScale = 1f / InteriorScale,
                // The panel is a wall of soft-alpha decals over dark instruments, which is where
                // the linear-space composite departs visibly from the original's (see the field).
                GammaBlendAlpha = true,
            };
        }
        _spinningProps = spinningProps;
        _withDamagePanels = spinningProps || damagePanels;
        _withCockpitInterior = cockpitInterior;
        _withDockingHook = dockingHook;
    }

    /// <summary>The paint applied to this build, once <see cref="Build"/> has resolved the
    /// aircraft's skin prefix, null when built unpainted.</summary>
    public PlanePainter? Painter => _painter;

    public int MeshInstanceCount => _scene.MeshInstanceCount + (_interiorScene?.MeshInstanceCount ?? 0);

    /// <summary>The wingtip flare nodes, built hidden (reset state) and re-skinned with an
    /// additive amber tint. A <c>Flight.Airframe.WingLightBlinker</c> flashes them in flight;
    /// the static viewer leaves them off. Populated by <see cref="Build"/>.</summary>
    public IReadOnlyList<Node3D> WingFlares => _wingFlares;

    /// <summary>Flight and damage-lab builds: the exterior damage-state
    /// panels, the torn-skin pdpN nodes, built HIDDEN (their reset state), plus their
    /// healthy pdpN_h twins, built visible. A <c>Flight.Airframe.DamageVisuals</c> flips
    /// them as part HP crosses the vehicle def's injure_anims thresholds.</summary>
    public IReadOnlyList<Node3D> DamagePanels => _damagePanels;

    /// <summary>Cockpit-interior builds only: the two torn-skin cockpit panels,
    /// <c>pcdp4</c>/<c>pcdp6</c>, built HIDDEN like their exterior
    /// counterparts. They carry no healthy twin (no <c>pcdp4_h</c>/<c>pcdp6_h</c> ships anywhere
    /// in <c>planes.zbd</c>), <c>Flight.Airframe.DamageVisuals</c> flips them off the SAME
    /// <c>pdpanel4</c>/<c>pdpanel6</c> injure entries that flip <c>pdp4</c>/<c>pdp6</c>, not a
    /// separate cockpit rule. Empty unless the builder was asked for a cockpit interior.</summary>
    public IReadOnlyList<Node3D> CockpitDamagePanels => _cockpitDamagePanels;

    /// <summary>The aircraft's skin-texture prefix, known once <see cref="Build"/> has run.
    /// Null when the model carries no decal-placeholder material to read it from.</summary>
    public string? SkinPrefix => _skinPrefix;

    /// <summary>The plane-local offset of this aircraft's authored <c>cockpit_camera</c> marker
    /// (docs/formats/markers.md), read the same way <see cref="MarkerRig"/> reads weapon markers,
    /// accumulated from below the plane root, skipping the cockpit-interior/wreck subtrees that
    /// may carry their own same-named node. Zero before <see cref="Build"/> runs, and the
    /// original's own fallback for a plane with no such node
    /// (docs/org/cameraViews.md, "Where the first-person camera sits").</summary>
    public Vector3 CockpitCameraOffset { get; private set; }

    /// <summary>The built <c>cockpit1</c> interior inside the model <see cref="Build"/> returned,
    /// hidden and mounted at <see cref="CockpitCameraOffset"/>; null unless the builder was asked
    /// for one. <c>Flight.Hud.CockpitVisibility</c> is what shows it, per view mode.</summary>
    public Node3D? CockpitInterior { get; private set; }

    /// <summary>The interior's own textured materials paired with the texture each resolved from,
    /// the same registry <see cref="Repaint"/> uses. <c>Flight.Hud.CockpitGauges</c> reads it to
    /// tell an indicator's light from its hilite bar by NAME rather than by guessing at the
    /// surface order, then overrides each driven surface with a copy of its own.</summary>
    public IReadOnlyList<(ShaderMaterial Material, string TextureName)> InteriorMaterials =>
        _interiorScene?.TexturedMaterials ?? Array.Empty<(ShaderMaterial, string)>();

    /// <summary>A <c>cockpit1</c> node whose visibility is a STATE something else drives, so a
    /// pristine cockpit must show none of it: the <c>bulNx</c> hole quads the
    /// <c>cockpit_bulletholes</c> defs light, and the two warning lamps, which
    /// <c>Flight.Hud.CockpitGauges</c> lights. Parking them still holds: a build with no rig
    /// driving it must render pristine, and the labs are such builds. ⚠ Everything else on the
    /// panel is always-drawn geometry that changes COLOUR, not visibility.</summary>
    public static bool IsInteriorDrivenState(string name)
    {
        if (name.EndsWith("_on", StringComparison.OrdinalIgnoreCase))
            return true;
        // ⚠ The QUADS, not the meshless bulletN groups above them: a parked group hides its own
        // leaves for good, and reset_bulletholes switches exactly these off and nothing else.
        if (!name.StartsWith("bul", StringComparison.OrdinalIgnoreCase))
            return false;
        int i = "bul".Length, digits = 0;
        while (i < name.Length && char.IsDigit(name[i]))
        {
            i++;
            digits++;
        }
        if (digits == 0 || i == name.Length)
            return false;
        for (; i < name.Length; i++)
            if (!char.IsLetter(name[i]))
                return false;
        return true;
    }

    /// <summary>The airframe's skyhook group node (<c>bal_hook</c>, <c>blood_hook</c>, …), the
    /// subtree a zeppelin hookup cutscene extends through that airframe's own
    /// <c>&lt;x&gt;_hook_extend</c> definition.</summary>
    public static bool IsDockingHook(string name) =>
        name.EndsWith("_hook", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the subtree rooted at the named node (e.g. "player_bhawk").</summary>
    public Node3D Build(string rootName)
    {
        var root = _gamez.FindByName(rootName)
            ?? throw new ArgumentException($"node '{rootName}' not found in GameZ data");
        EnsurePainter(root);
        CockpitCameraOffset = MarkerRig.FindNamedMarker(_gamez, rootName, "cockpit_camera");
        var built = _scene.BuildSubtree(root, Skip)!;
        // ⚠ Before the flare/panel walk, not after: that walk is what hides and collects the two
        // cockpit damage panels (pcdp4/pcdp6), which live inside the interior.
        MountCockpitInterior(built, root);
        CollectWingFlares(built);
        return built;
    }

    /// <summary>Builds the plane's 'destroyed' wreck-piece subtree (skipped by
    /// <see cref="Build"/>) for the crash breakup: a group of
    /// pieceN meshes. The returned root's transform is the accumulated plane-root →
    /// destroyed chain, so `planePose × root.Transform × piece.Transform` is each
    /// piece's crash-time world pose. Null when the plane has no such subtree.</summary>
    public Node3D? BuildDestroyed(string rootName)
    {
        var root = _gamez.FindByName(rootName);
        if (root == null)
            return null;
        EnsurePainter(root); // wreck pieces wear the same skins, so the same livery
        // depth-first for the 'destroyed' group, accumulating local transforms
        (GameZNode Node, Transform3D Acc)? found = null;
        void Search(GameZNode n, Transform3D acc)
        {
            if (found != null)
                return;
            acc *= n.Local ?? Transform3D.Identity;
            if (n.Name.Equals("destroyed", StringComparison.OrdinalIgnoreCase))
            {
                found = (n, acc);
                return;
            }
            foreach (int c in n.Children)
                Search(_gamez.Nodes[c], acc);
        }
        // accumulate below the root only (the built plane model itself carries the
        // root node's transform)
        foreach (int c in root.Children)
            Search(_gamez.Nodes[c], Transform3D.Identity);
        if (found == null)
            return null;
        var built = _scene.BuildSubtree(found.Value.Node, _ => false);
        if (built != null)
            built.Transform = found.Value.Acc;
        return built;
    }

    /// <summary>Re-liveries the already-built aircraft in place: a fresh painter, then every
    /// textured material re-resolved through it. The viewer's livery lab drives this from its
    /// sliders, repainting ~8 small skins is a few ms, where rebuilding the model for each
    /// slider pixel would not be interactive. Null paints nothing (back to the shipped skins).
    /// No-op before <see cref="Build"/>, which is what discovers the skin prefix.</summary>
    public void Repaint(PaintScheme? scheme)
    {
        _scheme = scheme;
        _painter = scheme != null && _skinPrefix != null
            ? new PlanePainter(_textures, _patterns, scheme, _skinPrefix)
            : null;
        _scene.Repaint();
        _interiorScene?.Repaint();
    }

    // ⚠ Blur disc textures must alpha-blend, never scissor: their alpha peaks around 26%,
    // which a scissor cutout erases outright.
    private static bool IsPropBlurTexture(string tex) =>
        tex.Contains("blur", StringComparison.OrdinalIgnoreCase);

    // ⚠ compasstxt and horizonindicator must alpha-blend in the interior, never scissor. The
    // compass window fades the drum's ends under two quads sampling that atlas' black alpha ramp,
    // and the artificial horizon's glass carries a golden haze inside its upper rim on a ramp that
    // crosses the cutout threshold, so a scissor turns each into an opaque bar (docs/formats/hud.md).
    private static bool IsInteriorBlendTexture(string tex) =>
        IsPropBlurTexture(tex)
        || tex.StartsWith("compasstxt", StringComparison.OrdinalIgnoreCase)
        || tex.StartsWith("horizonindicator", StringComparison.OrdinalIgnoreCase);

    // ⚠ pdpN_h (the healthy twin, see IsHealthyPanel) must always render: skipping all of
    // player_damage_on amputates real airframe sections, not just the torn-skin state.
    private static bool IsDamagePanel(string name, out bool cockpit)
    {
        cockpit = name.StartsWith("pcdp", StringComparison.OrdinalIgnoreCase);
        int start = cockpit ? 4
            : name.StartsWith("pdp", StringComparison.OrdinalIgnoreCase) ? 3 : -1;
        if (start < 0 || start == name.Length)
            return false;
        for (int i = start; i < name.Length; i++)
            if (!char.IsDigit(name[i]))
                return false;
        return true;
    }

    // pdpN_h, the healthy twin of an exterior damage panel.
    private static bool IsHealthyPanel(string name) =>
        name.EndsWith("_h", StringComparison.OrdinalIgnoreCase)
        && IsDamagePanel(name[..^2], out bool cockpit) && !cockpit;

    // The painter can only be built once the aircraft's root is known, its skin prefix is
    // read off the model's own material names. Built on the first Build/BuildDestroyed call
    // and reused, so both share one painted-texture cache.
    private void EnsurePainter(GameZNode root)
    {
        _skinPrefix ??= PlanePainter.PrefixFor(_gamez, root);
        if (_scheme == null || _painter != null)
            return;
        if (_skinPrefix == null)
        {
            Log.Info("world", $"[paint] {root.Name}: no <prefix>_noselogo/_taillogo/_winglogo material, building unpainted");
            return;
        }
        _painter = new PlanePainter(_textures, _patterns, _scheme, _skinPrefix);
        Log.Info("world", $"[paint] {root.Name} ({_skinPrefix}): {_scheme}{(_painter.PatternMissesAircraft ? $", pattern '{_scheme.FolderName}' ships no {_skinPrefix} skins, decals only" : "")}");
    }

    // Hides and re-skins the wingtip flares (WingLights.cs), and in the same walk collects
    // the flight build's damage panels: torn pdpN hidden, healthy pdpN_h as built.
    private void CollectWingFlares(Node node)
    {
        if (node is Node3D n3d)
        {
            if (WingLights.IsFlare(n3d.Name))
            {
                n3d.Visible = false;
                foreach (var child in n3d.GetChildren())
                    if (child is MeshInstance3D mi && mi.Name.ToString() == "mesh")
                        mi.MaterialOverride = FlareMaterial();
                _wingFlares.Add(n3d);
            }
            else if (PropParts.Classify(n3d.Name) == PropParts.Kind.Nitro)
            {
                n3d.Visible = false; // the nitro disc waits for the nitro_boost def to activate it
            }
            else if (IsDamagePanel(n3d.Name, out bool cockpit))
            {
                n3d.Visible = false; // torn skin waits for DamageVisuals to flip it on
                // pcdpN is kept off DamagePanels, that list is the exterior set the pairing walk
                // measures mesh-AABB centers over, and collected on its own list instead (B12);
                // DamageVisuals flips both off the same pdpanelN injure entries.
                (cockpit ? _cockpitDamagePanels : _damagePanels).Add(n3d);
            }
            else if (IsHealthyPanel(n3d.Name))
            {
                _damagePanels.Add(n3d);
            }
            else if (IsDockingHook(AnimRuntime.NameOf(n3d)))
            {
                // Retracted until the hookup cutscene calls <x>_hook_extend, whose own first
                // sequence is what activates the group. The archive ships the bit that way.
                n3d.Visible = ActiveInGameZ(n3d);
            }
        }
        foreach (var child in node.GetChildren())
            CollectWingFlares(child);
    }

    // Builds the interior, places it and parks it hidden, on a pass of its own because the mount
    // scale is what the depth bias must be told (_interiorScene). The eye is cockpit1's own origin,
    // and that is where FirstPersonPose puts the camera, so the mount position is the
    // cockpit_camera offset. ⚠ The mount also carries the fixed head-pitch tilt, and that is what
    // puts the gunsight on the guns, untilted the sight rides 3.9° above the pipper's nose axis
    // and never meets it. Head-look is NOT applied: the interior stays plane-fixed. See docs.
    private void MountCockpitInterior(Node3D built, GameZNode root)
    {
        if (!_withCockpitInterior || _interiorScene == null)
        {
            return;
        }
        foreach (int c in root.Children)
        {
            var node = _gamez.Nodes[c];
            if (!node.Name.Equals("cockpit1", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // Exempt only THIS node from the skip list: `cockpit1` is on it unconditionally now
            // that the airframe pass never builds the interior, and it is the root here.
            if (_interiorScene.BuildSubtree(node, n => !ReferenceEquals(n, node) && Skip(n))
                is not { } n3d)
            {
                return;
            }
            n3d.Transform = new Transform3D(
                new Basis(Vector3.Right, HeadPitchOffsetRad)
                    .Scaled(Vector3.One * InteriorScale),
                CockpitCameraOffset);
            n3d.Visible = false; // shown only while a first-person view is on the screen
            ParkInteriorStates(n3d);
            built.AddChild(n3d);
            CockpitInterior = n3d;
            return;
        }
    }

    // ⚠ The interior's off-states ship ACTIVE. A node the build script left NodeSetActive(false)
    // is off outright, but the state overlays are authored visible and hidden engine-side until
    // something drives them, so a pristine cockpit renders every windshield bullet hole and both
    // warning lamps unless they are parked here. See IsInteriorDrivenState.
    private void ParkInteriorStates(Node3D node)
    {
        if (IsInteriorDrivenState(AnimRuntime.NameOf(node)) || !ActiveInGameZ(node))
        {
            node.Visible = false;
        }
        foreach (var child in node.GetChildren())
        {
            if (child is Node3D n3d)
            {
                ParkInteriorStates(n3d);
            }
        }
    }

    // This built node's own gamez `flags.active`; true when the node carries no index to look up.
    private bool ActiveInGameZ(Node3D node)
    {
        if (!node.HasMeta(AnimRuntime.IndexMeta))
            return true;
        int index = (int)node.GetMeta(AnimRuntime.IndexMeta);
        return index < 0 || index >= _gamez.Nodes.Count || _gamez.Nodes[index].Active;
    }

    // Shared additive glow material for every flare quad (colour/texture: WingLights.cs).
    // ⚠ No billboard: forcing the quad to face the camera flattened the star burst into a blob.
    private StandardMaterial3D FlareMaterial() => _flareMaterial ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoTexture = _textures.Find(WingLights.FlareTexture),
        AlbedoColor = WingLights.FlareColor,
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        // The flare quad draws its texture exactly once; with the engine-default repeat on,
        // bilinear filtering at the UV border bleeds the opposite edge in (the same artifact
        // once seen as a tracer-tail streak).
        TextureRepeat = false,
    };

    private bool Skip(GameZNode node)
    {
        // ⚠ `cockpit1` is skipped here even when the caller asked for an interior: that subtree is
        // built by its own pass, on its own builder (MountCockpitInterior).
        if (SkipNames.Contains(node.Name))
            return true;
        // Cockpit damage panels (pcdpN) come with the interior and nothing else; exterior pdpN skip
        // only in the plain static viewer, flight and damage-lab builds construct them hidden for
        // DamageVisuals (10c). Both stay hidden until something flips them.
        if (IsDamagePanel(node.Name, out bool cockpit)
            && (cockpit ? !_withCockpitInterior : !_withDamagePanels))
            return true;
        // Skyhook arms: built only where a docking cutscene can call the airframe's own
        // <x>_hook_extend, since that definition ACTIVATES the group rather than creating it.
        if (!_withDockingHook && IsDockingHook(node.Name))
            return true;
        var kind = PropParts.Classify(node.Name);
        // Exterior shows only the static disc.
        if (!_spinningProps)
            return PropParts.IsDynamic(kind);
        // nitropropN is built hidden (CollectWingFlares): the nitro_boost def activates and fades
        // it in over the spinning discs, and nitro_decay parks it again. ⚠ staticpropN is kept
        // (see the class remarks); staticrotorN is not, that def naming propeller nodes only.
        return kind == PropParts.Kind.Static
            && !node.Name.StartsWith("staticprop", StringComparison.OrdinalIgnoreCase);
    }
}
